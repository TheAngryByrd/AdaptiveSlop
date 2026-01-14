namespace AdaptiveSlop.Core

open System
open System.Buffers
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

type IAdaptiveObject =
    abstract member Version: int64

type IAdaptiveValue<'T> =
    inherit IAdaptiveObject
    abstract member GetValue: unit -> 'T

module Transaction =
    type ICommit =
        abstract member Commit: unit -> unit

    type private TransactionState() =
        let mutable buffer: ICommit[] = Array.zeroCreate 8
        let mutable count = 0

        member _.Reset() =
            if count > 0 then
                Array.Clear(buffer, 0, count)
                count <- 0

        member _.Enqueue(action: ICommit) =
            if count = buffer.Length then
                let next = Array.zeroCreate (buffer.Length * 2)
                Array.Copy(buffer, next, buffer.Length)
                buffer <- next
            buffer[count] <- action
            count <- count + 1

        member _.Commit() =
            let mutable i = 0
            while i < count do
                buffer[i].Commit()
                i <- i + 1
            Array.Clear(buffer, 0, count)
            count <- 0

    type private TransactionContext =
        [<ThreadStatic; DefaultValue>]
        static val mutable private current: TransactionState option
        [<ThreadStatic; DefaultValue>]
        static val mutable private reusable: TransactionState

        static member Get() = TransactionContext.current
        static member Set(value: TransactionState option) = TransactionContext.current <- value

        static member GetReusable() =
            let value = TransactionContext.reusable
            if obj.ReferenceEquals(value, null) then
                let created = TransactionState()
                TransactionContext.reusable <- created
                created
            else
                value

    let private getCurrent() =
        let value = TransactionContext.Get()
        if obj.ReferenceEquals(value, null) then None else value

    let private setCurrent value =
        TransactionContext.Set(value)

    let internal tryEnqueue(action: ICommit) =
        match getCurrent() with
        | Some tx ->
            tx.Enqueue(action)
            true
        | None -> false

    let internal tryEnqueueFactory(factory: unit -> ICommit) =
        match getCurrent() with
        | Some tx ->
            tx.Enqueue(factory())
            true
        | None -> false

    let run (f: unit -> 'T) =
        match getCurrent() with
        | Some _ -> f()
        | None ->
            let tx = TransactionContext.GetReusable()
            tx.Reset()
            setCurrent (Some tx)
            try
                let result = f()
                tx.Commit()
                result
            finally
                tx.Reset()
                setCurrent None

module internal AdaptiveRuntime =
    type DependencyCollector() =
        let mutable depBuffer: IAdaptiveObject[] = Array.zeroCreate 8
        let mutable versionBuffer: int64[] = Array.zeroCreate 8
        let mutable count = 0

        member _.Reset() =
            count <- 0

        member _.Add(dep: IAdaptiveObject, version: int64) =
            if count = depBuffer.Length then
                let newSize = depBuffer.Length * 2
                let nextDeps = Array.zeroCreate newSize
                let nextVersions = Array.zeroCreate newSize
                Array.Copy(depBuffer, nextDeps, depBuffer.Length)
                Array.Copy(versionBuffer, nextVersions, versionBuffer.Length)
                depBuffer <- nextDeps
                versionBuffer <- nextVersions
            depBuffer[count] <- dep
            versionBuffer[count] <- version
            count <- count + 1

        member _.Snapshot() = depBuffer, versionBuffer, count

    type private DependencyContext =
        [<ThreadStatic; DefaultValue>]
        static val mutable private current: DependencyCollector option
        [<ThreadStatic; DefaultValue>]
        static val mutable private reusable: DependencyCollector

        static member GetCurrent() = DependencyContext.current
        static member SetCurrent(value: DependencyCollector option) = DependencyContext.current <- value

        static member GetReusable() =
            let value = DependencyContext.reusable
            if obj.ReferenceEquals(value, null) then
                let created = DependencyCollector()
                DependencyContext.reusable <- created
                created
            else
                value

    let private getCurrent() =
        let value = DependencyContext.GetCurrent()
        if obj.ReferenceEquals(value, null) then None else value

    let private setCurrent value =
        DependencyContext.SetCurrent(value)

    /// Add a dependency with its current committed version.
    /// Must be called INSIDE the lock after any recomputation, so version is stable.
    let addDependency (dep: IAdaptiveObject) (version: int64) =
        match getCurrent() with
        | Some collector -> collector.Add(dep, version)
        | None -> ()

    let collect (f: unit -> 'T) =
        let previous = getCurrent()
        let collector =
            match previous with
            | Some _ -> DependencyCollector()
            | None ->
                let reusable = DependencyContext.GetReusable()
                reusable.Reset()
                reusable

        setCurrent (Some collector)
        try
            let value = f()
            let deps, versions, depCount = collector.Snapshot()
            value, deps, versions, depCount
        finally
            collector.Reset()
            setCurrent previous

type ConstantValue<'T>(value: 'T) =
    interface IAdaptiveValue<'T> with
        member this.GetValue() =
            AdaptiveRuntime.addDependency (this :> IAdaptiveObject) 0L
            value
        member _.Version = 0L

and AdaptiveNode<'T>(compute: unit -> 'T) =
    let syncRoot = obj()
    let mutable version = 0L
    let mutable hasValue = false
    let mutable value = Unchecked.defaultof<'T>
    let mutable deps: IAdaptiveObject[] = [||]
    let mutable depsFromPool = false
    let mutable depVersions: int64[] = [||]
    let mutable versionsFromPool = false
    let mutable depCount = 0

    member private this.IsDirty() =
        if not hasValue then
            true
        else
            let mutable dirty = false
            let mutable i = 0
            while not dirty && i < depCount do
                if deps[i].Version <> depVersions[i] then
                    dirty <- true
                i <- i + 1
            dirty

    member private this.Recompute() =
        let newValue, newDeps, newVersions, newCount = AdaptiveRuntime.collect compute
        value <- newValue
        if newCount = 0 then
            if depsFromPool && deps.Length > 0 then
                ArrayPool<IAdaptiveObject>.Shared.Return(deps, true)
            if versionsFromPool && depVersions.Length > 0 then
                ArrayPool<int64>.Shared.Return(depVersions, true)
            deps <- Array.empty
            depVersions <- Array.empty
            depsFromPool <- false
            versionsFromPool <- false
            depCount <- 0
        else
            if deps.Length < newCount then
                if depsFromPool && deps.Length > 0 then
                    ArrayPool<IAdaptiveObject>.Shared.Return(deps, true)
                deps <- ArrayPool<IAdaptiveObject>.Shared.Rent(newCount)
                depsFromPool <- true
            if depVersions.Length < newCount then
                if versionsFromPool && depVersions.Length > 0 then
                    ArrayPool<int64>.Shared.Return(depVersions, true)
                depVersions <- ArrayPool<int64>.Shared.Rent(newCount)
                versionsFromPool <- true
            Array.Copy(newDeps, deps, newCount)
            // Copy collected versions - these were captured at read time, not after
            Array.Copy(newVersions, depVersions, newCount)
            if depCount > newCount then
                Array.Clear(deps, newCount, depCount - newCount)
            depCount <- newCount
        hasValue <- true
        version <- version + 1L

    interface IAdaptiveValue<'T> with
        member this.GetValue() =
            Monitor.Enter(syncRoot)
            try
                if this.IsDirty() then
                    this.Recompute()
                // Add dependency with committed version AFTER any recompute, inside lock
                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) version
                value
            finally
                Monitor.Exit(syncRoot)
        member this.Version =
            Monitor.Enter(syncRoot)
            try
                if this.IsDirty() then
                    version + 1L
                else
                    version
            finally
                Monitor.Exit(syncRoot)

and ChangeableValue<'T>(initial: 'T) =
    let syncRoot = obj()
    let mutable value = initial
    let mutable version = 0L

    member internal _.Apply(newValue: 'T) =
        Monitor.Enter(syncRoot)
        try
            value <- newValue
            version <- version + 1L
        finally
            Monitor.Exit(syncRoot)

    member this.Set(newValue: 'T) =
        if not (Transaction.tryEnqueueFactory (fun () -> ValueChange(this, newValue) :> Transaction.ICommit)) then
            this.Apply(newValue)

    interface IAdaptiveValue<'T> with
        member this.GetValue() =
            Monitor.Enter(syncRoot)
            try
                // Add dependency with committed version inside lock
                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) version
                value
            finally
                Monitor.Exit(syncRoot)
        member _.Version = Interlocked.Read(&version)

and ValueChange<'T>(target: ChangeableValue<'T>, value: 'T) =
    interface Transaction.ICommit with
        member _.Commit() = target.Apply(value)

type ChangeableSet<'T when 'T: comparison>(initial: Set<'T>) =
    let syncRoot = obj()
    let mutable version = 0L
    let mutable data = HashSet<'T>(initial.Count)
    let mutable snapshot = initial
    let mutable snapshotVersion = -1L

    do
        for item in initial do
            data.Add(item) |> ignore

    member private _.Invalidate() =
        snapshotVersion <- -1L

    member internal _.Apply(newValue: Set<'T>) =
        Monitor.Enter(syncRoot)
        try
            data.Clear()
            for item in newValue do
                data.Add(item) |> ignore
            version <- version + 1L
            snapshot <- newValue
            snapshotVersion <- version
        finally
            Monitor.Exit(syncRoot)

    member internal this.ApplyAdd(item: 'T) =
        Monitor.Enter(syncRoot)
        try
            if data.Add(item) then
                version <- version + 1L
                this.Invalidate()
        finally
            Monitor.Exit(syncRoot)

    member internal this.ApplyRemove(item: 'T) =
        Monitor.Enter(syncRoot)
        try
            if data.Remove(item) then
                version <- version + 1L
                this.Invalidate()
        finally
            Monitor.Exit(syncRoot)

    member this.Set(newValue: Set<'T>) =
        if not (Transaction.tryEnqueueFactory (fun () -> SetReplaceChange(this, newValue) :> Transaction.ICommit)) then
            this.Apply(newValue)

    member this.Add(item: 'T) =
        if not (Transaction.tryEnqueueFactory (fun () -> SetAddChange(this, item) :> Transaction.ICommit)) then
            this.ApplyAdd(item)

    member this.Remove(item: 'T) =
        if not (Transaction.tryEnqueueFactory (fun () -> SetRemoveChange(this, item) :> Transaction.ICommit)) then
            this.ApplyRemove(item)

    interface IAdaptiveValue<Set<'T>> with
        member this.GetValue() =
            Monitor.Enter(syncRoot)
            try
                // Add dependency with committed version inside lock
                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) version
                if snapshotVersion = version then
                    snapshot
                else
                    let count = data.Count
                    if count = 0 then
                        snapshot <- Set.empty
                        snapshotVersion <- version
                        snapshot
                    else
                        let buffer = ArrayPool<'T>.Shared.Rent(count)
                        try
                            let mutable i = 0
                            for item in data do
                                buffer[i] <- item
                                i <- i + 1
                            let segment = ArraySegment(buffer, 0, i)
                            let next = Set.ofSeq segment
                            snapshot <- next
                            snapshotVersion <- version
                            next
                        finally
                            ArrayPool<'T>.Shared.Return(buffer, true)
            finally
                Monitor.Exit(syncRoot)
        member _.Version = Interlocked.Read(&version)

and SetReplaceChange<'T when 'T: comparison>(target: ChangeableSet<'T>, value: Set<'T>) =
    interface Transaction.ICommit with
        member _.Commit() = target.Apply(value)

and SetAddChange<'T when 'T: comparison>(target: ChangeableSet<'T>, item: 'T) =
    interface Transaction.ICommit with
        member _.Commit() = target.ApplyAdd(item)

and SetRemoveChange<'T when 'T: comparison>(target: ChangeableSet<'T>, item: 'T) =
    interface Transaction.ICommit with
        member _.Commit() = target.ApplyRemove(item)

type ChangeableMap<'K, 'V when 'K: comparison>(initial: Map<'K, 'V>) =
    let syncRoot = obj()
    let mutable version = 0L
    let mutable data = Dictionary<'K, 'V>(initial.Count)
    let mutable snapshot = initial
    let mutable snapshotVersion = -1L

    do
        for KeyValue(key, value) in initial do
            data.Add(key, value)

    member private _.Invalidate() =
        snapshotVersion <- -1L

    member internal _.Apply(newValue: Map<'K, 'V>) =
        Monitor.Enter(syncRoot)
        try
            data.Clear()
            for KeyValue(key, value) in newValue do
                data.Add(key, value)
            version <- version + 1L
            snapshot <- newValue
            snapshotVersion <- version
        finally
            Monitor.Exit(syncRoot)

    member internal this.ApplyAdd(key: 'K, valueToSet: 'V) =
        Monitor.Enter(syncRoot)
        try
            match data.TryGetValue(key) with
            | true, existing when EqualityComparer<'V>.Default.Equals(existing, valueToSet) ->
                ()
            | _ ->
                data[key] <- valueToSet
                version <- version + 1L
                this.Invalidate()
        finally
            Monitor.Exit(syncRoot)

    member internal this.ApplyRemove(key: 'K) =
        Monitor.Enter(syncRoot)
        try
            if data.Remove(key) then
                version <- version + 1L
                this.Invalidate()
        finally
            Monitor.Exit(syncRoot)

    member this.Set(newValue: Map<'K, 'V>) =
        if not (Transaction.tryEnqueueFactory (fun () -> MapReplaceChange(this, newValue) :> Transaction.ICommit)) then
            this.Apply(newValue)

    member this.AddOrUpdate(key: 'K, valueToSet: 'V) =
        if not (Transaction.tryEnqueueFactory (fun () -> MapAddChange(this, key, valueToSet) :> Transaction.ICommit)) then
            this.ApplyAdd(key, valueToSet)

    member this.Remove(key: 'K) =
        if not (Transaction.tryEnqueueFactory (fun () -> MapRemoveChange(this, key) :> Transaction.ICommit)) then
            this.ApplyRemove(key)

    interface IAdaptiveValue<Map<'K, 'V>> with
        member this.GetValue() =
            Monitor.Enter(syncRoot)
            try
                // Add dependency with committed version inside lock
                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) version
                if snapshotVersion = version then
                    snapshot
                else
                    let count = data.Count
                    if count = 0 then
                        snapshot <- Map.empty
                        snapshotVersion <- version
                        snapshot
                    else
                        let buffer = ArrayPool<'K * 'V>.Shared.Rent(count)
                        try
                            let mutable i = 0
                            for pair in data do
                                buffer[i] <- pair.Key, pair.Value
                                i <- i + 1
                            let segment = ArraySegment(buffer, 0, i)
                            let next = Map.ofSeq segment
                            snapshot <- next
                            snapshotVersion <- version
                            next
                        finally
                            ArrayPool<'K * 'V>.Shared.Return(buffer, true)
            finally
                Monitor.Exit(syncRoot)
        member _.Version = Interlocked.Read(&version)

and MapReplaceChange<'K, 'V when 'K: comparison>(target: ChangeableMap<'K, 'V>, value: Map<'K, 'V>) =
    interface Transaction.ICommit with
        member _.Commit() = target.Apply(value)

and MapAddChange<'K, 'V when 'K: comparison>(target: ChangeableMap<'K, 'V>, key: 'K, valueToSet: 'V) =
    interface Transaction.ICommit with
        member _.Commit() = target.ApplyAdd(key, valueToSet)

and MapRemoveChange<'K, 'V when 'K: comparison>(target: ChangeableMap<'K, 'V>, key: 'K) =
    interface Transaction.ICommit with
        member _.Commit() = target.ApplyRemove(key)

module AVal =
    let constant (value: 'T) : IAdaptiveValue<'T> =
        ConstantValue(value) :> IAdaptiveValue<'T>

    let map (f: 'T -> 'U) (value: IAdaptiveValue<'T>) : IAdaptiveValue<'U> =
        AdaptiveNode(fun () -> f (value.GetValue())) :> IAdaptiveValue<'U>

    let map2 (f: 'T -> 'U -> 'V) (left: IAdaptiveValue<'T>) (right: IAdaptiveValue<'U>) : IAdaptiveValue<'V> =
        AdaptiveNode(fun () -> f (left.GetValue()) (right.GetValue())) :> IAdaptiveValue<'V>

    let mapTask (f: 'T -> Task<'U>) (value: IAdaptiveValue<'T>) : IAdaptiveValue<Task<'U>> =
        AdaptiveNode(fun () -> f (value.GetValue())) :> IAdaptiveValue<Task<'U>>

    let mapValueTask (f: 'T -> ValueTask<'U>) (value: IAdaptiveValue<'T>) : IAdaptiveValue<ValueTask<'U>> =
        AdaptiveNode(fun () -> f (value.GetValue())) :> IAdaptiveValue<ValueTask<'U>>

    let bind (f: 'T -> IAdaptiveValue<'U>) (value: IAdaptiveValue<'T>) : IAdaptiveValue<'U> =
        AdaptiveNode(fun () ->
            let inner = f (value.GetValue())
            inner.GetValue()) :> IAdaptiveValue<'U>

    let bindTask (f: 'T -> Task<'U>) (value: IAdaptiveValue<'T>) : Task<'U> =
        value.GetValue() |> f

    let bindValueTask (f: 'T -> ValueTask<'U>) (value: IAdaptiveValue<'T>) : ValueTask<'U> =
        value.GetValue() |> f

    let mapTaskResult (f: 'T -> 'U) (value: IAdaptiveValue<Task<'T>>) : IAdaptiveValue<Task<'U>> =
        AdaptiveNode(fun () ->
            task {
                let! inner = value.GetValue()
                return f inner
            }) :> IAdaptiveValue<Task<'U>>

    let mapValueTaskResult (f: 'T -> 'U) (value: IAdaptiveValue<ValueTask<'T>>) : IAdaptiveValue<ValueTask<'U>> =
        AdaptiveNode(fun () ->
            ValueTask<'U>(
                task {
                    let! inner = value.GetValue()
                    return f inner
                })) :> IAdaptiveValue<ValueTask<'U>>

    let bindTaskResult (f: 'T -> Task<'U>) (value: IAdaptiveValue<Task<'T>>) : IAdaptiveValue<Task<'U>> =
        AdaptiveNode(fun () ->
            task {
                let! inner = value.GetValue()
                return! f inner
            }) :> IAdaptiveValue<Task<'U>>

    let bindValueTaskResult (f: 'T -> ValueTask<'U>) (value: IAdaptiveValue<ValueTask<'T>>) : IAdaptiveValue<ValueTask<'U>> =
        AdaptiveNode(fun () ->
            ValueTask<'U>(
                task {
                    let! inner = value.GetValue()
                    return! f inner
                })) :> IAdaptiveValue<ValueTask<'U>>

    let getValue (value: IAdaptiveValue<'T>) = value.GetValue()

    let getValueTask (value: IAdaptiveValue<'T>) = Task.FromResult(value.GetValue())

    let getValueValueTask (value: IAdaptiveValue<'T>) = ValueTask<'T>(value.GetValue())

module CVal =
    let create (value: 'T) = ChangeableValue(value)

    let set (value: 'T) (cval: ChangeableValue<'T>) = cval.Set(value)

    let value (cval: ChangeableValue<'T>) = cval :> IAdaptiveValue<'T>

module ASet =
    let ofSeq<'T when 'T: comparison> (items: seq<'T>) : IAdaptiveValue<Set<'T>> =
        AdaptiveNode(fun () -> Set.ofSeq items) :> IAdaptiveValue<Set<'T>>

    let union<'T when 'T: comparison> (left: IAdaptiveValue<Set<'T>>) (right: IAdaptiveValue<Set<'T>>) : IAdaptiveValue<Set<'T>> =
        AdaptiveNode(fun () -> Set.union (left.GetValue()) (right.GetValue())) :> IAdaptiveValue<Set<'T>>

    let map<'T, 'U when 'T: comparison and 'U: comparison> (f: 'T -> 'U) (set: IAdaptiveValue<Set<'T>>) : IAdaptiveValue<Set<'U>> =
        AdaptiveNode(fun () -> set.GetValue() |> Set.map f) :> IAdaptiveValue<Set<'U>>

    let filter<'T when 'T: comparison> (predicate: 'T -> bool) (set: IAdaptiveValue<Set<'T>>) : IAdaptiveValue<Set<'T>> =
        AdaptiveNode(fun () -> set.GetValue() |> Set.filter predicate) :> IAdaptiveValue<Set<'T>>

    let getValue (set: IAdaptiveValue<Set<'T>>) = set.GetValue()

    let getValueValueTask (set: IAdaptiveValue<Set<'T>>) = ValueTask<Set<'T>>(set.GetValue())

module CSet =
    let empty<'T when 'T: comparison> = ChangeableSet(Set.empty<'T>)

    let ofSeq<'T when 'T: comparison> (items: seq<'T>) = ChangeableSet(Set.ofSeq items)

    let add (item: 'T) (set: ChangeableSet<'T>) = set.Add(item)

    let remove (item: 'T) (set: ChangeableSet<'T>) = set.Remove(item)

    let set (value: Set<'T>) (set: ChangeableSet<'T>) = set.Set(value)

    let value (set: ChangeableSet<'T>) = set :> IAdaptiveValue<Set<'T>>

module AMap =
    let ofSeq<'K, 'V when 'K: comparison> (items: seq<'K * 'V>) : IAdaptiveValue<Map<'K, 'V>> =
        AdaptiveNode(fun () -> Map.ofSeq items) :> IAdaptiveValue<Map<'K, 'V>>

    let map<'K, 'V, 'U when 'K: comparison> (f: 'K -> 'V -> 'U) (mapValue: IAdaptiveValue<Map<'K, 'V>>) : IAdaptiveValue<Map<'K, 'U>> =
        AdaptiveNode(fun () -> mapValue.GetValue() |> Map.map f) :> IAdaptiveValue<Map<'K, 'U>>

    let filter<'K, 'V when 'K: comparison> (predicate: 'K -> 'V -> bool) (mapValue: IAdaptiveValue<Map<'K, 'V>>) : IAdaptiveValue<Map<'K, 'V>> =
        AdaptiveNode(fun () -> mapValue.GetValue() |> Map.filter predicate) :> IAdaptiveValue<Map<'K, 'V>>

    let getValue (mapValue: IAdaptiveValue<Map<'K, 'V>>) = mapValue.GetValue()

    let getValueValueTask (mapValue: IAdaptiveValue<Map<'K, 'V>>) = ValueTask<Map<'K, 'V>>(mapValue.GetValue())

module CMap =
    let empty<'K, 'V when 'K: comparison> = ChangeableMap(Map.empty<'K, 'V>)

    let ofSeq<'K, 'V when 'K: comparison> (items: seq<'K * 'V>) = ChangeableMap(Map.ofSeq items)

    let addOrUpdate (key: 'K) (value: 'V) (mapValue: ChangeableMap<'K, 'V>) = mapValue.AddOrUpdate(key, value)

    let remove (key: 'K) (mapValue: ChangeableMap<'K, 'V>) = mapValue.Remove(key)

    let set (value: Map<'K, 'V>) (mapValue: ChangeableMap<'K, 'V>) = mapValue.Set(value)

    let value (mapValue: ChangeableMap<'K, 'V>) = mapValue :> IAdaptiveValue<Map<'K, 'V>>
