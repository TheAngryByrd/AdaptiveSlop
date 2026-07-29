namespace AdaptiveSlop.Core

open System
open System.Buffers
open System.Collections.Generic
open System.Threading

// =============================================================================
// Constant adaptive collections
// =============================================================================

type ConstantSet<'T when 'T: comparison>(value: Set<'T>) =
    interface IAdaptiveSet<'T> with
        member this.GetValue() =
            AdaptiveRuntime.addDependency (this :> IAdaptiveObject) 0L
            value

        member _.Version = 0L

type ConstantMap<'K, 'V when 'K: comparison>(value: Map<'K, 'V>) =
    interface IAdaptiveMap<'K, 'V> with
        member this.GetValue() =
            AdaptiveRuntime.addDependency (this :> IAdaptiveObject) 0L
            value

        member _.Version = 0L

// =============================================================================
// AdaptiveSet transform nodes
// =============================================================================

type MapSetNode<'T, 'U when 'T: comparison and 'U: comparison>
    (source: IAdaptiveSet<'T>, [<InlineIfLambda>] mapping: 'T -> 'U) as this =

    let syncRoot = obj ()
    let mutable version = 0L
    let data = HashSet<'U>()
    let refcounts = Dictionary<'U, int>()
    let mutable snapshot = Set.empty<'U>
    let mutable snapshotVersion = -1L
    let mutable sinks: obj[] = Array.zeroCreate 4
    let mutable sinkCount = 0
    let mutable registered = false
    let mutable initialized = false

    do this.Register()

    member private this.DoInitialLoad() =
        if not initialized then
            initialized <- true
            let sourceItems = source.GetValue()

            for item in sourceItems do
                let y = mapping item
                data.Add y |> ignore

                match refcounts.TryGetValue y with
                | true, n -> refcounts[y] <- n + 1
                | _ -> refcounts[y] <- 1

            snapshot <- Set.ofSeq data
            snapshotVersion <- 0L

    member private this.Register() =
        if not registered then
            registered <- true

            match box source with
            | :? ISetSinkRegistry as r -> r.AddSetSink(box (this :> ISetDeltaSink<'T>))
            | _ -> ()

    member private this.Unregister() =
        if registered then
            registered <- false

            match box source with
            | :? ISetSinkRegistry as r -> r.RemoveSetSink(box (this :> ISetDeltaSink<'T>))
            | _ -> ()

    member internal this.AddSink(sink: ISetDeltaSink<'U>) =
        Monitor.Enter syncRoot

        try
            if sinkCount = sinks.Length then
                let next = Array.zeroCreate (sinks.Length * 2)
                Array.Copy(sinks, next, sinks.Length)
                sinks <- next

            sinks[sinkCount] <- box sink
            sinkCount <- sinkCount + 1
        finally
            Monitor.Exit syncRoot

        if sinkCount = 1 then
            this.Register()

    member internal this.RemoveSink(sink: ISetDeltaSink<'U>) =
        let mutable becameZero = false
        Monitor.Enter syncRoot

        try
            let mutable found = -1
            let mutable i = 0

            while found < 0 && i < sinkCount do
                if obj.ReferenceEquals(sinks[i], box sink) then
                    found <- i
                else
                    i <- i + 1

            if found >= 0 then
                sinkCount <- sinkCount - 1

                for j in found .. sinkCount - 1 do
                    sinks[j] <- sinks[j + 1]

                sinks[sinkCount] <- null
                becameZero <- sinkCount = 0
        finally
            Monitor.Exit syncRoot

        if becameZero then
            this.Unregister()

    member private this.FlushDeltas(ver: int64, adds: 'U[], addCnt: int, rems: 'U[], remCnt: int) =
        if addCnt > 0 || remCnt > 0 then
            let sinksSnapshot =
                Monitor.Enter syncRoot

                try
                    if sinkCount = 0 then
                        [||]
                    else
                        let arr = Array.zeroCreate sinkCount
                        Array.Copy(sinks, arr, sinkCount)
                        arr
                finally
                    Monitor.Exit syncRoot

            for i in 0 .. sinksSnapshot.Length - 1 do
                (unbox<ISetDeltaSink<'U>> sinksSnapshot[i]).OnDeltas(ver, adds, addCnt, rems, remCnt)

    interface ISetDeltaSink<'T> with
        member this.OnDeltas(ver, adds, addCnt, rems, remCnt) =
            let ownAdds =
                if addCnt > 0 then
                    ArrayPool<'U>.Shared.Rent addCnt
                else
                    ArrayPool<'U>.Shared.Rent 1

            let ownRems =
                if remCnt > 0 then
                    ArrayPool<'U>.Shared.Rent remCnt
                else
                    ArrayPool<'U>.Shared.Rent 1

            let mutable ownAddCnt = 0
            let mutable ownRemCnt = 0
            Monitor.Enter syncRoot

            try
                for i in 0 .. remCnt - 1 do
                    let y = mapping rems[i]

                    match refcounts.TryGetValue y with
                    | true, 1 ->
                        refcounts.Remove y |> ignore
                        data.Remove y |> ignore
                        snapshot <- Set.remove y snapshot
                        ownRems[ownRemCnt] <- y
                        ownRemCnt <- ownRemCnt + 1
                    | true, n -> refcounts[y] <- n - 1
                    | _ -> ()

                for i in 0 .. addCnt - 1 do
                    let y = mapping adds[i]

                    match refcounts.TryGetValue y with
                    | true, n ->
                        refcounts[y] <- n + 1
                        ownAdds[ownAddCnt] <- y
                        ownAddCnt <- ownAddCnt + 1
                    | _ ->
                        refcounts[y] <- 1
                        data.Add y |> ignore
                        snapshot <- Set.add y snapshot
                        ownAdds[ownAddCnt] <- y
                        ownAddCnt <- ownAddCnt + 1

                version <- ver
                snapshotVersion <- ver
            finally
                Monitor.Exit syncRoot

            this.FlushDeltas(ver, ownAdds, ownAddCnt, ownRems, ownRemCnt)
            ArrayPool<'U>.Shared.Return(ownAdds, true)
            ArrayPool<'U>.Shared.Return(ownRems, true)

    interface IAdaptiveSet<'U> with
        member this.GetValue() =
            Monitor.Enter syncRoot

            try
                this.DoInitialLoad()
                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) version
                snapshot
            finally
                Monitor.Exit syncRoot

        member _.Version = Interlocked.Read &version

    interface ISetSinkRegistry with
        member this.AddSetSink(sink) =
            this.AddSink(unbox<ISetDeltaSink<'U>> sink)

        member this.RemoveSetSink(sink) =
            this.RemoveSink(unbox<ISetDeltaSink<'U>> sink)


type FilterSetNode<'T when 'T: comparison>(source: IAdaptiveSet<'T>, [<InlineIfLambda>] predicate: 'T -> bool) as this =

    let syncRoot = obj ()
    let mutable version = 0L
    let data = HashSet<'T>()
    let refcounts = Dictionary<'T, int>()
    let mutable snapshot = Set.empty<'T>
    let mutable snapshotVersion = -1L
    let mutable sinks: obj[] = Array.zeroCreate 4
    let mutable sinkCount = 0
    let mutable registered = false
    let mutable initialized = false

    do this.Register()

    member private this.DoInitialLoad() =
        if not initialized then
            initialized <- true
            let sourceItems = source.GetValue()

            for item in sourceItems do
                if predicate item then
                    data.Add item |> ignore

                    match refcounts.TryGetValue item with
                    | true, n -> refcounts[item] <- n + 1
                    | _ -> refcounts[item] <- 1

            snapshot <- Set.ofSeq data
            snapshotVersion <- 0L

    member private this.Register() =
        if not registered then
            registered <- true

            match box source with
            | :? ISetSinkRegistry as r -> r.AddSetSink(box (this :> ISetDeltaSink<'T>))
            | _ -> ()

    member private this.Unregister() =
        if registered then
            registered <- false

            match box source with
            | :? ISetSinkRegistry as r -> r.RemoveSetSink(box (this :> ISetDeltaSink<'T>))
            | _ -> ()

    member internal this.AddSink(sink: ISetDeltaSink<'T>) =
        Monitor.Enter syncRoot

        try
            if sinkCount = sinks.Length then
                let next = Array.zeroCreate (sinks.Length * 2)
                Array.Copy(sinks, next, sinks.Length)
                sinks <- next

            sinks[sinkCount] <- box sink
            sinkCount <- sinkCount + 1
        finally
            Monitor.Exit syncRoot

        if sinkCount = 1 then
            this.Register()

    member internal this.RemoveSink(sink: ISetDeltaSink<'T>) =
        let mutable becameZero = false
        Monitor.Enter syncRoot

        try
            let mutable found = -1
            let mutable i = 0

            while found < 0 && i < sinkCount do
                if obj.ReferenceEquals(sinks[i], box sink) then
                    found <- i
                else
                    i <- i + 1

            if found >= 0 then
                sinkCount <- sinkCount - 1

                for j in found .. sinkCount - 1 do
                    sinks[j] <- sinks[j + 1]

                sinks[sinkCount] <- null
                becameZero <- sinkCount = 0
        finally
            Monitor.Exit syncRoot

        if becameZero then
            this.Unregister()

    member private this.FlushDeltas(ver: int64, adds: 'T[], addCnt: int, rems: 'T[], remCnt: int) =
        if addCnt > 0 || remCnt > 0 then
            let sinksSnapshot =
                Monitor.Enter syncRoot

                try
                    if sinkCount = 0 then
                        [||]
                    else
                        let arr = Array.zeroCreate sinkCount
                        Array.Copy(sinks, arr, sinkCount)
                        arr
                finally
                    Monitor.Exit syncRoot

            for i in 0 .. sinksSnapshot.Length - 1 do
                (unbox<ISetDeltaSink<'T>> sinksSnapshot[i]).OnDeltas(ver, adds, addCnt, rems, remCnt)

    interface ISetDeltaSink<'T> with
        member this.OnDeltas(ver, adds, addCnt, rems, remCnt) =
            let ownAdds =
                if addCnt > 0 then
                    ArrayPool<'T>.Shared.Rent addCnt
                else
                    ArrayPool<'T>.Shared.Rent 1

            let ownRems =
                if remCnt > 0 then
                    ArrayPool<'T>.Shared.Rent remCnt
                else
                    ArrayPool<'T>.Shared.Rent 1

            let mutable ownAddCnt = 0
            let mutable ownRemCnt = 0
            Monitor.Enter syncRoot

            try
                for i in 0 .. remCnt - 1 do
                    let x = rems[i]

                    match refcounts.TryGetValue x with
                    | true, 1 ->
                        refcounts.Remove x |> ignore
                        data.Remove x |> ignore
                        snapshot <- Set.remove x snapshot
                        ownRems[ownRemCnt] <- x
                        ownRemCnt <- ownRemCnt + 1
                    | true, n -> refcounts[x] <- n - 1
                    | _ -> ()

                for i in 0 .. addCnt - 1 do
                    let x = adds[i]

                    if predicate x then
                        match refcounts.TryGetValue x with
                        | true, n ->
                            refcounts[x] <- n + 1
                            ownAdds[ownAddCnt] <- x
                            ownAddCnt <- ownAddCnt + 1
                        | _ ->
                            refcounts[x] <- 1
                            data.Add x |> ignore
                            snapshot <- Set.add x snapshot
                            ownAdds[ownAddCnt] <- x
                            ownAddCnt <- ownAddCnt + 1

                version <- ver
                snapshotVersion <- ver
            finally
                Monitor.Exit syncRoot

            this.FlushDeltas(ver, ownAdds, ownAddCnt, ownRems, ownRemCnt)
            ArrayPool<'T>.Shared.Return(ownAdds, true)
            ArrayPool<'T>.Shared.Return(ownRems, true)

    interface IAdaptiveSet<'T> with
        member this.GetValue() =
            Monitor.Enter syncRoot

            try
                this.DoInitialLoad()
                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) version
                snapshot
            finally
                Monitor.Exit syncRoot

        member _.Version = Interlocked.Read &version

    interface ISetSinkRegistry with
        member this.AddSetSink(sink) =
            this.AddSink(unbox<ISetDeltaSink<'T>> sink)

        member this.RemoveSetSink(sink) =
            this.RemoveSink(unbox<ISetDeltaSink<'T>> sink)


type UnionSetNode<'T when 'T: comparison>(left: IAdaptiveSet<'T>, right: IAdaptiveSet<'T>) as this =

    let syncRoot = obj ()
    let mutable version = 0L
    let data = HashSet<'T>()
    let refcounts = Dictionary<'T, int>()
    let mutable snapshot = Set.empty<'T>
    let mutable snapshotVersion = -1L
    let mutable sinks: obj[] = Array.zeroCreate 4
    let mutable sinkCount = 0
    let mutable regLeft = false
    let mutable regRight = false
    let mutable initialized = false

    do
        regLeft <- true
        regRight <- true
        this.RegisterSide left
        this.RegisterSide right

    member private this.DoInitialLoad() =
        if not initialized then
            initialized <- true

            let addItem (x: 'T) =
                data.Add x |> ignore

                match refcounts.TryGetValue x with
                | true, n -> refcounts[x] <- n + 1
                | _ -> refcounts[x] <- 1

            let leftItems = left.GetValue()

            for item in leftItems do
                addItem item

            let rightItems = right.GetValue()

            for item in rightItems do
                addItem item

            snapshot <- Set.ofSeq data
            snapshotVersion <- 0L

    member private this.RegisterSide(s: IAdaptiveSet<'T>) =
        match box s with
        | :? ISetSinkRegistry as r -> r.AddSetSink(box (this :> ISetDeltaSink<'T>))
        | _ -> ()

    member private this.UnregisterSide(s: IAdaptiveSet<'T>) =
        match box s with
        | :? ISetSinkRegistry as r -> r.RemoveSetSink(box (this :> ISetDeltaSink<'T>))
        | _ -> ()

    member internal this.AddSink(sink: ISetDeltaSink<'T>) =
        Monitor.Enter syncRoot

        try
            if sinkCount = sinks.Length then
                let next = Array.zeroCreate (sinks.Length * 2)
                Array.Copy(sinks, next, sinks.Length)
                sinks <- next

            sinks[sinkCount] <- box sink
            sinkCount <- sinkCount + 1

            if sinkCount = 1 then
                regLeft <- true
                regRight <- true
        finally
            Monitor.Exit syncRoot

        if sinkCount = 1 then
            this.RegisterSide left
            this.RegisterSide right

    member internal this.RemoveSink(sink: ISetDeltaSink<'T>) =
        let mutable becameZero = false
        Monitor.Enter syncRoot

        try
            let mutable found = -1
            let mutable i = 0

            while found < 0 && i < sinkCount do
                if obj.ReferenceEquals(sinks[i], box sink) then
                    found <- i
                else
                    i <- i + 1

            if found >= 0 then
                sinkCount <- sinkCount - 1

                for j in found .. sinkCount - 1 do
                    sinks[j] <- sinks[j + 1]

                sinks[sinkCount] <- null
                becameZero <- sinkCount = 0
        finally
            Monitor.Exit syncRoot

        if becameZero then
            this.UnregisterSide left
            this.UnregisterSide right

    member private this.FlushDeltas(ver: int64, adds: 'T[], addCnt: int, rems: 'T[], remCnt: int) =
        if addCnt > 0 || remCnt > 0 then
            let sinksSnapshot =
                Monitor.Enter syncRoot

                try
                    if sinkCount = 0 then
                        [||]
                    else
                        let arr = Array.zeroCreate sinkCount
                        Array.Copy(sinks, arr, sinkCount)
                        arr
                finally
                    Monitor.Exit syncRoot

            for i in 0 .. sinksSnapshot.Length - 1 do
                (unbox<ISetDeltaSink<'T>> sinksSnapshot[i]).OnDeltas(ver, adds, addCnt, rems, remCnt)

    member private this.ProcessDeltas(adds: 'T[], addCnt: int, rems: 'T[], remCnt: int) =
        if addCnt = 0 && remCnt = 0 then
            ()
        else

            let ownAdds =
                if addCnt > 0 then
                    ArrayPool<'T>.Shared.Rent addCnt
                else
                    ArrayPool<'T>.Shared.Rent 1

            let ownRems =
                if remCnt > 0 then
                    ArrayPool<'T>.Shared.Rent remCnt
                else
                    ArrayPool<'T>.Shared.Rent 1

            let mutable ownAddCnt = 0
            let mutable ownRemCnt = 0

            Monitor.Enter syncRoot

            try
                for i in 0 .. remCnt - 1 do
                    let x = rems[i]

                    match refcounts.TryGetValue x with
                    | true, 1 ->
                        refcounts.Remove x |> ignore
                        data.Remove x |> ignore
                        snapshot <- Set.remove x snapshot
                        ownRems[ownRemCnt] <- x
                        ownRemCnt <- ownRemCnt + 1
                    | true, n -> refcounts[x] <- n - 1
                    | _ -> ()

                for i in 0 .. addCnt - 1 do
                    let x = adds[i]

                    match refcounts.TryGetValue x with
                    | true, n -> refcounts[x] <- n + 1
                    | _ ->
                        refcounts[x] <- 1
                        data.Add x |> ignore
                        snapshot <- Set.add x snapshot
                        ownAdds[ownAddCnt] <- x
                        ownAddCnt <- ownAddCnt + 1

                snapshotVersion <- version
            finally
                Monitor.Exit syncRoot

            if ownAddCnt > 0 || ownRemCnt > 0 then
                this.FlushDeltas(Interlocked.Read &version, ownAdds, ownAddCnt, ownRems, ownRemCnt)

            ArrayPool<'T>.Shared.Return(ownAdds, true)
            ArrayPool<'T>.Shared.Return(ownRems, true)

    interface ISetDeltaSink<'T> with
        member this.OnDeltas(ver, adds, addCnt, rems, remCnt) =
            version <- ver
            this.ProcessDeltas(adds, addCnt, rems, remCnt)

    member this.OnRightDeltas(ver, adds, addCnt, rems, remCnt) =
        version <- ver
        this.ProcessDeltas(adds, addCnt, rems, remCnt)

    interface IAdaptiveSet<'T> with
        member this.GetValue() =
            Monitor.Enter syncRoot

            try
                this.DoInitialLoad()
                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) version
                snapshot
            finally
                Monitor.Exit syncRoot

        member _.Version = Interlocked.Read &version

    interface ISetSinkRegistry with
        member this.AddSetSink(sink) =
            this.AddSink(unbox<ISetDeltaSink<'T>> sink)

        member this.RemoveSetSink(sink) =
            this.RemoveSink(unbox<ISetDeltaSink<'T>> sink)


// =============================================================================
// AdaptiveMap transform nodes
// =============================================================================

type MapMapNode<'K, 'V, 'U when 'K: comparison>
    (source: IAdaptiveMap<'K, 'V>, [<InlineIfLambda>] mapping: 'K -> 'V -> 'U) as this =

    let syncRoot = obj ()
    let mutable version = 0L
    let data = Dictionary<'K, 'U>()
    let mutable snapshot = Map.empty<'K, 'U>
    let mutable snapshotVersion = -1L
    let mutable sinks: obj[] = Array.zeroCreate 4
    let mutable sinkCount = 0
    let mutable registered = false
    let mutable initialized = false

    do this.Register()

    member private this.DoInitialLoad() =
        if not initialized then
            initialized <- true
            let sourceItems = source.GetValue()

            for (KeyValue(k, v)) in sourceItems do
                data[k] <- mapping k v

            snapshot <- Map.ofSeq (data |> Seq.map (fun (KeyValue(k, v)) -> (k, v)))
            snapshotVersion <- 0L

    member private this.Register() =
        if not registered then
            registered <- true

            match box source with
            | :? IMapSinkRegistry as r -> r.AddMapSink(box (this :> IMapDeltaSink<'K, 'V>))
            | _ -> ()

    member private this.Unregister() =
        if registered then
            registered <- false

            match box source with
            | :? IMapSinkRegistry as r -> r.RemoveMapSink(box (this :> IMapDeltaSink<'K, 'V>))
            | _ -> ()

    member internal this.AddSink(sink: IMapDeltaSink<'K, 'U>) =
        Monitor.Enter syncRoot

        try
            if sinkCount = sinks.Length then
                let next = Array.zeroCreate (sinks.Length * 2)
                Array.Copy(sinks, next, sinks.Length)
                sinks <- next

            sinks[sinkCount] <- box sink
            sinkCount <- sinkCount + 1
        finally
            Monitor.Exit syncRoot

        if sinkCount = 1 then
            this.Register()

    member internal this.RemoveSink(sink: IMapDeltaSink<'K, 'U>) =
        let mutable becameZero = false
        Monitor.Enter syncRoot

        try
            let mutable found = -1
            let mutable i = 0

            while found < 0 && i < sinkCount do
                if obj.ReferenceEquals(sinks[i], box sink) then
                    found <- i
                else
                    i <- i + 1

            if found >= 0 then
                sinkCount <- sinkCount - 1

                for j in found .. sinkCount - 1 do
                    sinks[j] <- sinks[j + 1]

                sinks[sinkCount] <- null
                becameZero <- sinkCount = 0
        finally
            Monitor.Exit syncRoot

        if becameZero then
            this.Unregister()

    member private this.FlushDeltas(ver: int64, sets: struct ('K * 'U)[], setCnt: int, rems: 'K[], remCnt: int) =
        if setCnt > 0 || remCnt > 0 then
            let sinksSnapshot =
                Monitor.Enter syncRoot

                try
                    if sinkCount = 0 then
                        [||]
                    else
                        let arr = Array.zeroCreate sinkCount
                        Array.Copy(sinks, arr, sinkCount)
                        arr
                finally
                    Monitor.Exit syncRoot

            for i in 0 .. sinksSnapshot.Length - 1 do
                (unbox<IMapDeltaSink<'K, 'U>> sinksSnapshot[i]).OnDeltas(ver, sets, setCnt, rems, remCnt)

    interface IMapDeltaSink<'K, 'V> with
        member this.OnDeltas(ver, sets, setCnt, rems, remCnt) =
            let ownSets =
                if setCnt > 0 then
                    ArrayPool<struct ('K * 'U)>.Shared.Rent setCnt
                else
                    ArrayPool<struct ('K * 'U)>.Shared.Rent 1

            let ownRems =
                if remCnt > 0 then
                    ArrayPool<'K>.Shared.Rent remCnt
                else
                    ArrayPool<'K>.Shared.Rent 1

            let mutable ownSetCnt = 0
            let mutable ownRemCnt = 0

            Monitor.Enter syncRoot

            try
                for i in 0 .. remCnt - 1 do
                    let k = rems[i]

                    if data.Remove k then
                        snapshot <- Map.remove k snapshot
                        ownRems[ownRemCnt] <- k
                        ownRemCnt <- ownRemCnt + 1

                for i in 0 .. setCnt - 1 do
                    let struct (k, v) = sets[i]
                    let u = mapping k v
                    data[k] <- u
                    snapshot <- Map.add k u snapshot
                    ownSets[ownSetCnt] <- struct (k, u)
                    ownSetCnt <- ownSetCnt + 1

                version <- ver
                snapshotVersion <- ver
            finally
                Monitor.Exit syncRoot

            this.FlushDeltas(ver, ownSets, ownSetCnt, ownRems, ownRemCnt)
            ArrayPool<struct ('K * 'U)>.Shared.Return(ownSets, true)
            ArrayPool<'K>.Shared.Return(ownRems, true)

    interface IAdaptiveMap<'K, 'U> with
        member this.GetValue() =
            Monitor.Enter syncRoot

            try
                this.DoInitialLoad()
                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) version
                snapshot
            finally
                Monitor.Exit syncRoot

        member _.Version = Interlocked.Read &version

    interface IMapSinkRegistry with
        member this.AddMapSink(sink) =
            this.AddSink(unbox<IMapDeltaSink<'K, 'U>> sink)

        member this.RemoveMapSink(sink) =
            this.RemoveSink(unbox<IMapDeltaSink<'K, 'U>> sink)


type FilterMapNode<'K, 'V when 'K: comparison>
    (source: IAdaptiveMap<'K, 'V>, [<InlineIfLambda>] predicate: 'K -> 'V -> bool) as this =

    let syncRoot = obj ()
    let mutable version = 0L
    let data = Dictionary<'K, 'V>()
    let mutable snapshot = Map.empty<'K, 'V>
    let mutable snapshotVersion = -1L
    let mutable sinks: obj[] = Array.zeroCreate 4
    let mutable sinkCount = 0
    let mutable registered = false
    let mutable initialized = false

    do this.Register()

    member private this.DoInitialLoad() =
        if not initialized then
            initialized <- true
            let sourceItems = source.GetValue()

            for (KeyValue(k, v)) in sourceItems do
                if predicate k v then
                    data[k] <- v

            snapshot <- Map.ofSeq (data |> Seq.map (fun (KeyValue(k, v)) -> (k, v)))
            snapshotVersion <- 0L

    member private this.Register() =
        if not registered then
            registered <- true

            match source with
            | :? ChangeableMap<'K, 'V> as cm -> cm.AddSink(this :> IMapDeltaSink<'K, 'V>)
            | :? MapMapNode<'K, 'V, 'V> as mn -> mn.AddSink(this :> IMapDeltaSink<'K, 'V>)
            | :? FilterMapNode<'K, 'V> as fn -> fn.AddSink(this :> IMapDeltaSink<'K, 'V>)
            | _ -> ()

    member private this.Unregister() =
        if registered then
            registered <- false

            match source with
            | :? ChangeableMap<'K, 'V> as cm -> cm.RemoveSink(this :> IMapDeltaSink<'K, 'V>)
            | :? MapMapNode<'K, 'V, 'V> as mn -> mn.RemoveSink(this :> IMapDeltaSink<'K, 'V>)
            | :? FilterMapNode<'K, 'V> as fn -> fn.RemoveSink(this :> IMapDeltaSink<'K, 'V>)
            | _ -> ()

    member internal this.AddSink(sink: IMapDeltaSink<'K, 'V>) =
        Monitor.Enter syncRoot

        try
            if sinkCount = sinks.Length then
                let next = Array.zeroCreate (sinks.Length * 2)
                Array.Copy(sinks, next, sinks.Length)
                sinks <- next

            sinks[sinkCount] <- box sink
            sinkCount <- sinkCount + 1
        finally
            Monitor.Exit syncRoot

        if sinkCount = 1 then
            this.Register()

    member internal this.RemoveSink(sink: IMapDeltaSink<'K, 'V>) =
        let mutable becameZero = false
        Monitor.Enter syncRoot

        try
            let mutable found = -1
            let mutable i = 0

            while found < 0 && i < sinkCount do
                if obj.ReferenceEquals(sinks[i], box sink) then
                    found <- i
                else
                    i <- i + 1

            if found >= 0 then
                sinkCount <- sinkCount - 1

                for j in found .. sinkCount - 1 do
                    sinks[j] <- sinks[j + 1]

                sinks[sinkCount] <- null
                becameZero <- sinkCount = 0
        finally
            Monitor.Exit syncRoot

        if becameZero then
            this.Unregister()

    member private this.FlushDeltas(ver: int64, sets: struct ('K * 'V)[], setCnt: int, rems: 'K[], remCnt: int) =
        if setCnt > 0 || remCnt > 0 then
            let sinksSnapshot =
                Monitor.Enter syncRoot

                try
                    if sinkCount = 0 then
                        [||]
                    else
                        let arr = Array.zeroCreate sinkCount
                        Array.Copy(sinks, arr, sinkCount)
                        arr
                finally
                    Monitor.Exit syncRoot

            for i in 0 .. sinksSnapshot.Length - 1 do
                (unbox<IMapDeltaSink<'K, 'V>> sinksSnapshot[i]).OnDeltas(ver, sets, setCnt, rems, remCnt)

    interface IMapDeltaSink<'K, 'V> with
        member this.OnDeltas(ver, sets, setCnt, rems, remCnt) =
            let ownSets =
                if setCnt > 0 then
                    ArrayPool<struct ('K * 'V)>.Shared.Rent setCnt
                else
                    ArrayPool<struct ('K * 'V)>.Shared.Rent 1

            let ownRems =
                if remCnt > 0 then
                    ArrayPool<'K>.Shared.Rent remCnt
                else
                    ArrayPool<'K>.Shared.Rent 1

            let mutable ownSetCnt = 0
            let mutable ownRemCnt = 0

            Monitor.Enter syncRoot

            try
                for i in 0 .. remCnt - 1 do
                    let k = rems[i]

                    if data.Remove k then
                        snapshot <- Map.remove k snapshot
                        ownRems[ownRemCnt] <- k
                        ownRemCnt <- ownRemCnt + 1

                for i in 0 .. setCnt - 1 do
                    let struct (k, v) = sets[i]

                    if predicate k v then
                        data[k] <- v
                        snapshot <- Map.add k v snapshot
                        ownSets[ownSetCnt] <- struct (k, v)
                        ownSetCnt <- ownSetCnt + 1
                    elif data.Remove k then
                        snapshot <- Map.remove k snapshot
                        ownRems[ownRemCnt] <- k
                        ownRemCnt <- ownRemCnt + 1

                version <- ver
                snapshotVersion <- ver
            finally
                Monitor.Exit syncRoot

            this.FlushDeltas(ver, ownSets, ownSetCnt, ownRems, ownRemCnt)
            ArrayPool<struct ('K * 'V)>.Shared.Return(ownSets, true)
            ArrayPool<'K>.Shared.Return(ownRems, true)

    interface IAdaptiveMap<'K, 'V> with
        member this.GetValue() =
            Monitor.Enter syncRoot

            try
                this.DoInitialLoad()
                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) version
                snapshot
            finally
                Monitor.Exit syncRoot

        member _.Version = Interlocked.Read &version

// =============================================================================
// Module functions
// =============================================================================

module ASet =
    let inline ofSeq<'T when 'T: comparison> (items: seq<'T>) : IAdaptiveSet<'T> = ConstantSet(Set.ofSeq items)

    let inline map<'T, 'U when 'T: comparison and 'U: comparison>
        ([<InlineIfLambda>] f: 'T -> 'U)
        (set: IAdaptiveSet<'T>)
        : IAdaptiveSet<'U> =
        MapSetNode<'T, 'U>(set, f)

    let inline filter<'T when 'T: comparison>
        ([<InlineIfLambda>] predicate: 'T -> bool)
        (set: IAdaptiveSet<'T>)
        : IAdaptiveSet<'T> =
        FilterSetNode<'T>(set, predicate)

    let inline union<'T when 'T: comparison> (left: IAdaptiveSet<'T>) (right: IAdaptiveSet<'T>) : IAdaptiveSet<'T> =
        UnionSetNode<'T>(left, right)

    let inline getValue (set: IAdaptiveSet<'T>) = set.GetValue()

module CSet =
    let inline empty<'T when 'T: comparison> = ChangeableSet Set.empty<'T>

    let inline ofSeq<'T when 'T: comparison> (items: seq<'T>) = ChangeableSet(Set.ofSeq items)

    let inline add (item: 'T) (set: ChangeableSet<'T>) = set.Add item

    let inline remove (item: 'T) (set: ChangeableSet<'T>) = set.Remove item

    let inline set (value: Set<'T>) (set: ChangeableSet<'T>) = set.Set value

    let inline value (set: ChangeableSet<'T>) : IAdaptiveSet<'T> = set :> IAdaptiveSet<'T>

module AMap =
    let inline ofSeq<'K, 'V when 'K: comparison> (items: seq<'K * 'V>) : IAdaptiveMap<'K, 'V> =
        ConstantMap(Map.ofSeq items)

    let inline map<'K, 'V, 'U when 'K: comparison>
        ([<InlineIfLambda>] f: 'K -> 'V -> 'U)
        (mapValue: IAdaptiveMap<'K, 'V>)
        : IAdaptiveMap<'K, 'U> =
        MapMapNode<'K, 'V, 'U>(mapValue, f) :> IAdaptiveMap<'K, 'U>

    let inline filter<'K, 'V when 'K: comparison>
        ([<InlineIfLambda>] predicate: 'K -> 'V -> bool)
        (mapValue: IAdaptiveMap<'K, 'V>)
        : IAdaptiveMap<'K, 'V> =
        FilterMapNode<'K, 'V>(mapValue, predicate)

    let inline getValue (mapValue: IAdaptiveMap<'K, 'V>) = mapValue.GetValue()

module CMap =
    let inline empty<'K, 'V when 'K: comparison> = ChangeableMap Map.empty<'K, 'V>

    let inline ofSeq<'K, 'V when 'K: comparison> (items: seq<'K * 'V>) = ChangeableMap(Map.ofSeq items)

    let inline addOrUpdate (key: 'K) (value: 'V) (mapValue: ChangeableMap<'K, 'V>) = mapValue.AddOrUpdate key value

    let inline remove (key: 'K) (mapValue: ChangeableMap<'K, 'V>) = mapValue.Remove key

    let inline set (value: Map<'K, 'V>) (mapValue: ChangeableMap<'K, 'V>) = mapValue.Set value

    let inline value (mapValue: ChangeableMap<'K, 'V>) : IAdaptiveMap<'K, 'V> = mapValue
