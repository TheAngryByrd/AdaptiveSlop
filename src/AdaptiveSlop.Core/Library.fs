namespace AdaptiveSlop.Core

open System
open System.Buffers
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

/// <summary>
/// Represents the dirty state of an adaptive node for lazy push invalidation.
/// </summary>
/// <remarks>
/// <para>
/// AdaptiveSlop uses a hybrid pull/push model:
/// <list type="bullet">
/// <item><description><c>Clean</c>: Node is up-to-date, no recomputation needed</description></item>
/// <item><description><c>Dirty</c>: Node was invalidated by a dependency change, needs recomputation</description></item>
/// <item><description><c>MaybeDirty</c>: Parent links are incomplete, fall back to version checking</description></item>
/// </list>
/// </para>
/// </remarks>
type DirtyState =
    /// Node is up-to-date; value can be returned without recomputation
    | Clean = 0
    /// Node was invalidated by a dependency change; needs recomputation on next read
    | Dirty = 1
    /// Parent links are incomplete; fall back to version checking
    | MaybeDirty = 2

/// <summary>
/// Base interface for all adaptive objects. Provides version tracking for change detection.
/// </summary>
/// <remarks>
/// The version number increases each time the object's value changes.
/// Used by dependent nodes to detect when recomputation is needed.
/// </remarks>
type IAdaptiveObject =
    /// <summary>Gets the current version number. Increases when the value changes.</summary>
    abstract member Version: int64

/// <summary>
/// Internal interface for nodes that support dirty propagation (push invalidation).
/// </summary>
/// <remarks>
/// When a source value changes, it calls <c>MarkDirty()</c> on all registered parent nodes,
/// which propagate the dirty state up the dependency graph.
/// </remarks>
type internal IMarkable =
    /// <summary>Marks this node as dirty, triggering recomputation on next read.</summary>
    abstract member MarkDirty: unit -> unit

/// <summary>
/// An adaptive value that automatically tracks dependencies and recomputes when inputs change.
/// </summary>
/// <typeparam name="T">The type of the value.</typeparam>
/// <remarks>
/// <para>
/// Adaptive values form a dependency graph. When you read a value via <c>GetValue()</c>,
/// the system checks if any dependencies have changed and recomputes if necessary.
/// </para>
/// <para>
/// <strong>Thread safety:</strong> All operations are thread-safe. Multiple threads can
/// read and modify the dependency graph concurrently.
/// </para>
/// </remarks>
type IAdaptiveValue<'T> =
    inherit IAdaptiveObject
    /// <summary>
    /// Gets the current value, recomputing if any dependencies have changed.
    /// </summary>
    /// <returns>The current computed value.</returns>
    abstract member GetValue: unit -> 'T

/// <summary>
/// Handle for an active observation of an adaptive value.
/// Disposing removes parent links and stops dirty propagation for the observed subtree.
/// </summary>
/// <remarks>
/// <para>
/// Observations enable push-based invalidation by establishing parent links from
/// source values to dependent nodes. When not observed, nodes fall back to
/// pull-based version checking.
/// </para>
/// <para>
/// <strong>Memory management:</strong> Always dispose observations when no longer needed
/// to prevent memory leaks from parent link retention.
/// </para>
/// </remarks>
type IObservation =
    inherit IDisposable
    /// <summary>Gets whether this observation is still active (not disposed).</summary>
    abstract member IsActive: bool

type IAdaptiveSet<'T when 'T: comparison> =
    inherit IAdaptiveObject
    abstract member GetValue: unit -> Set<'T>

type IAdaptiveMap<'K, 'V when 'K: comparison> =
    inherit IAdaptiveObject
    abstract member GetValue: unit -> Map<'K, 'V>

type internal ISetDeltaSink<'T when 'T: comparison> =
    abstract member OnDeltas: version: int64 * added: 'T[] * addedCount: int * removed: 'T[] * removedCount: int -> unit

type internal IMapDeltaSink<'K, 'V when 'K: comparison> =
    abstract member OnDeltas:
        version: int64 * setEntries: struct ('K * 'V)[] * setCount: int * removedKeys: 'K[] * removedCount: int -> unit

type internal ISetSinkRegistry =
    abstract member AddSetSink: sink: obj -> unit
    abstract member RemoveSetSink: sink: obj -> unit

type internal IMapSinkRegistry =
    abstract member AddMapSink: sink: obj -> unit
    abstract member RemoveMapSink: sink: obj -> unit

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
        static val mutable private current: TransactionState voption

        [<ThreadStatic; DefaultValue>]
        static val mutable private reusable: TransactionState

        static member Get() = TransactionContext.current
        static member Set(value: TransactionState voption) = TransactionContext.current <- value

        static member GetReusable() =
            let value = TransactionContext.reusable

            if obj.ReferenceEquals(value, null) then
                let created = TransactionState()
                TransactionContext.reusable <- created
                created
            else
                value

    let inline private getCurrent () =
        let value = TransactionContext.Get()

        if obj.ReferenceEquals(value, null) then
            ValueNone
        else
            value

    let inline private setCurrent value = TransactionContext.Set(value)

    let internal tryEnqueue (action: ICommit) =
        match getCurrent () with
        | ValueSome tx ->
            tx.Enqueue(action)
            true
        | ValueNone -> false

    let internal tryEnqueueFactory (factory: unit -> ICommit) =
        match getCurrent () with
        | ValueSome tx ->
            tx.Enqueue(factory ())
            true
        | ValueNone -> false

    let run (f: unit -> 'T) =
        match getCurrent () with
        | ValueSome _ -> f ()
        | ValueNone ->
            let tx = TransactionContext.GetReusable()
            tx.Reset()
            setCurrent (ValueSome tx)

            try
                let result = f ()
                tx.Commit()
                result
            finally
                tx.Reset()
                setCurrent ValueNone

/// Internal module for managing parent (dependent) links for dirty propagation
module internal ParentTracking =
    /// Compact parent storage: inline single parent, array for multiple
    [<Struct>]
    type Parents =
        | NoParents
        | SingleParent of single: IMarkable
        | MultipleParents of parents: IMarkable[]

    /// Add a parent to the parent list
    let addParent (current: Parents) (parent: IMarkable) : Parents =
        match current with
        | NoParents -> SingleParent parent
        | SingleParent existing ->
            if obj.ReferenceEquals(existing, parent) then
                current
            else
                MultipleParents [| existing; parent |]
        | MultipleParents arr ->
            // Check if already present
            let mutable found = false

            for p in arr do
                if obj.ReferenceEquals(p, parent) then
                    found <- true

            if found then
                current
            else
                let newArr = Array.zeroCreate (arr.Length + 1)
                Array.Copy(arr, newArr, arr.Length)
                newArr.[arr.Length] <- parent
                MultipleParents newArr

    /// Remove a parent from the parent list
    let removeParent (current: Parents) (parent: IMarkable) : Parents =
        match current with
        | NoParents -> NoParents
        | SingleParent existing ->
            if obj.ReferenceEquals(existing, parent) then
                NoParents
            else
                current
        | MultipleParents arr ->
            let newArr =
                [| for p in arr do
                       if obj.ReferenceEquals(p, parent) then
                           p |]

            match newArr.Length with
            | 0 -> NoParents
            | 1 -> SingleParent newArr.[0]
            | _ -> MultipleParents newArr

    /// Mark all parents as dirty (propagate invalidation)
    let markParentsDirty (parents: Parents) =
        match parents with
        | NoParents -> ()
        | SingleParent p -> p.MarkDirty()
        | MultipleParents arr ->
            for p in arr do
                p.MarkDirty()

module internal AdaptiveRuntime =
    /// Thread-static evaluation context for caching dirty checks within a single evaluation.
    /// This prevents O(depth^2) work in deep chains by ensuring each node is checked once per eval.
    type private EvaluationContext =
        [<ThreadStatic; DefaultValue>]
        static val mutable private currentId: int64

        [<ThreadStatic; DefaultValue>]
        static val mutable private depth: int

        static member GetCurrentId() = EvaluationContext.currentId

        /// Called at top-level read to start a new evaluation scope
        static member Enter() =
            if EvaluationContext.depth = 0 then
                EvaluationContext.currentId <- EvaluationContext.currentId + 1L

            EvaluationContext.depth <- EvaluationContext.depth + 1

        /// Called when top-level read completes
        static member Exit() =
            EvaluationContext.depth <- EvaluationContext.depth - 1

    let internal getEvaluationId () = EvaluationContext.GetCurrentId()
    let internal enterEvaluation () = EvaluationContext.Enter()
    let internal exitEvaluation () = EvaluationContext.Exit()

    /// Re-entrant dependency collector with stack frames.
    /// Uses two parallel arrays (original format) with frame support.
    type DependencyCollector() =
        let mutable depBuffer: IAdaptiveObject[] = Array.zeroCreate 16
        let mutable versionBuffer: int64[] = Array.zeroCreate 16
        let mutable count = 0
        // Frame stack: stores the starting index of each nested evaluation
        let mutable frameStarts: int[] = Array.zeroCreate 8
        let mutable frameDepth = 0

        member _.Reset() =
            count <- 0
            frameDepth <- 0

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

        member _.PushFrame() =
            if frameDepth = frameStarts.Length then
                let next = Array.zeroCreate (frameStarts.Length * 2)
                Array.Copy(frameStarts, next, frameStarts.Length)
                frameStarts <- next

            frameStarts[frameDepth] <- count
            frameDepth <- frameDepth + 1

        member _.PopFrame() =
            frameDepth <- frameDepth - 1
            count <- frameStarts[frameDepth]

        /// Get the current frame's deps (depBuffer, versionBuffer, start, length)
        /// Returns struct tuple to avoid heap allocation
        member _.CurrentFrame() =
            let start = if frameDepth > 0 then frameStarts[frameDepth - 1] else 0
            struct (depBuffer, versionBuffer, start, count - start)

    type private DependencyContext =
        [<ThreadStatic; DefaultValue>]
        static val mutable private current: DependencyCollector voption

        [<ThreadStatic; DefaultValue>]
        static val mutable private reusable: DependencyCollector

        static member GetCurrent() = DependencyContext.current
        static member SetCurrent(value: DependencyCollector voption) = DependencyContext.current <- value

        static member GetReusable() =
            let value = DependencyContext.reusable

            if obj.ReferenceEquals(value, null) then
                let created = DependencyCollector()
                DependencyContext.reusable <- created
                created
            else
                value

    let private getCurrent () =
        let value = DependencyContext.GetCurrent()

        if obj.ReferenceEquals(value, null) then
            ValueNone
        else
            value

    let private setCurrent value = DependencyContext.SetCurrent(value)

    /// Add a dependency with its current committed version.
    /// Must be called INSIDE the lock after any recomputation, so version is stable.
    let addDependency (dep: IAdaptiveObject) (version: int64) =
        match getCurrent () with
        | ValueSome collector -> collector.Add(dep, version)
        | ValueNone -> ()

    /// Collect dependencies during evaluation. Returns struct tuple to avoid heap allocation.
    let collect (f: unit -> 'T) =
        let collector =
            match getCurrent () with
            | ValueSome c -> c // Reuse existing collector (nested evaluation)
            | ValueNone ->
                let reusable = DependencyContext.GetReusable()
                reusable.Reset()
                setCurrent (ValueSome reusable)
                reusable

        collector.PushFrame()

        try
            let value = f ()
            let struct (deps, versions, start, len) = collector.CurrentFrame()
            struct (value, deps, versions, start, len)
        finally
            collector.PopFrame()

type ConstantValue<'T>(value: 'T) =
    interface IAdaptiveValue<'T> with
        member this.GetValue() =
            AdaptiveRuntime.addDependency (this :> IAdaptiveObject) 0L
            value

        member _.Version = 0L

and AdaptiveNode<'T>([<InlineIfLambda>] compute: unit -> 'T) =
    let syncRoot = obj ()
    let mutable version = 0L
    let mutable hasValue = false
    let mutable value = Unchecked.defaultof<'T>
    let mutable deps: IAdaptiveObject[] = Array.empty
    let mutable depsFromPool = false
    let mutable depVersions: int64[] = Array.empty
    let mutable versionsFromPool = false
    let mutable depCount = 0
    // Per-evaluation dirty cache to avoid O(depth^2) in deep chains
    let mutable lastCheckedEvalId = 0L
    let mutable dirtyCache = true

    /// Check if dirty, using per-evaluation cache to avoid redundant deep traversals
    member private this.IsDirty() =
        let evalId = AdaptiveRuntime.getEvaluationId ()

        if lastCheckedEvalId = evalId then
            // Already checked in this evaluation, return cached result
            dirtyCache
        else
            // Compute dirty status and cache it
            let dirty =
                if not hasValue then
                    true
                else
                    let mutable d = false
                    let mutable i = 0

                    while not d && i < depCount do
                        if deps[i].Version <> depVersions[i] then
                            d <- true

                        i <- i + 1

                    d

            lastCheckedEvalId <- evalId
            dirtyCache <- dirty
            dirty

    member private this.Recompute() =
        let struct (newValue, newDeps, newVersions, newStart, newLen) =
            AdaptiveRuntime.collect compute

        value <- newValue

        if newLen = 0 then
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
            if deps.Length < newLen then
                if depsFromPool && deps.Length > 0 then
                    ArrayPool<IAdaptiveObject>.Shared.Return(deps, true)

                deps <- ArrayPool<IAdaptiveObject>.Shared.Rent(newLen)
                depsFromPool <- true

            if depVersions.Length < newLen then
                if versionsFromPool && depVersions.Length > 0 then
                    ArrayPool<int64>.Shared.Return(depVersions, true)

                depVersions <- ArrayPool<int64>.Shared.Rent(newLen)
                versionsFromPool <- true
            // Copy from collector's frame using Array.Copy (faster than loop)
            Array.Copy(newDeps, newStart, deps, 0, newLen)
            Array.Copy(newVersions, newStart, depVersions, 0, newLen)

            if depCount > newLen then
                Array.Clear(deps, newLen, depCount - newLen)

            depCount <- newLen

        hasValue <- true
        version <- version + 1L
        // Update cache: we just recomputed, so we're not dirty anymore for this evaluation
        dirtyCache <- false

    interface IAdaptiveValue<'T> with
        member this.GetValue() =
            AdaptiveRuntime.enterEvaluation ()

            try
                Monitor.Enter(syncRoot)

                try
                    if this.IsDirty() then
                        this.Recompute()
                    // Add dependency with committed version AFTER any recompute, inside lock
                    AdaptiveRuntime.addDependency (this :> IAdaptiveObject) version
                    value
                finally
                    Monitor.Exit(syncRoot)
            finally
                AdaptiveRuntime.exitEvaluation ()

        member this.Version =
            AdaptiveRuntime.enterEvaluation ()

            try
                Monitor.Enter(syncRoot)

                try
                    if this.IsDirty() then version + 1L else version
                finally
                    Monitor.Exit(syncRoot)
            finally
                AdaptiveRuntime.exitEvaluation ()

and ChangeableValue<'T>(initial: 'T) =
    let syncRoot = obj ()
    let mutable value = initial
    let mutable version = 0L
    let mutable parents = ParentTracking.NoParents

    member internal _.AddParent(parent: IMarkable) =
        Monitor.Enter(syncRoot)

        try
            parents <- ParentTracking.addParent parents parent
        finally
            Monitor.Exit(syncRoot)

    member internal _.RemoveParent(parent: IMarkable) =
        Monitor.Enter(syncRoot)

        try
            parents <- ParentTracking.removeParent parents parent
        finally
            Monitor.Exit(syncRoot)

    member internal _.Apply(newValue: 'T) =
        let parentsToNotify =
            Monitor.Enter(syncRoot)

            try
                value <- newValue
                version <- version + 1L
                parents // Capture parents before releasing lock
            finally
                Monitor.Exit(syncRoot)
        // Mark parents dirty outside the lock to avoid deadlocks
        ParentTracking.markParentsDirty parentsToNotify

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

/// <summary>
/// Specialized adaptive node that combines exactly three dependencies with inline field storage.
/// </summary>
/// <typeparam name="A">Type of the first dependency.</typeparam>
/// <typeparam name="B">Type of the second dependency.</typeparam>
/// <typeparam name="C">Type of the third dependency.</typeparam>
/// <typeparam name="T">Type of the computed result.</typeparam>
/// <remarks>
/// <para>
/// This node type is more efficient than chaining <c>map2</c> calls because:
/// <list type="bullet">
/// <item><description>Uses inline fields instead of arrays for dependency tracking</description></item>
/// <item><description>Single node instead of two intermediate nodes</description></item>
/// <item><description>Supports lazy push invalidation when observed by parent nodes</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Internal implementation detail:</strong> Created via <see cref="AVal.map3"/>.
/// </para>
/// </remarks>
and Map3Node<'A, 'B, 'C, 'T>
    (
        dep0: IAdaptiveValue<'A>,
        dep1: IAdaptiveValue<'B>,
        dep2: IAdaptiveValue<'C>,
        [<InlineIfLambda>] compute: 'A -> 'B -> 'C -> 'T
    ) as this =
    let syncRoot = obj ()
    let mutable version = 0L
    let mutable hasValue = false
    let mutable value = Unchecked.defaultof<'T>
    let mutable ver0 = 0L
    let mutable ver1 = 0L
    let mutable ver2 = 0L
    let mutable dirtyState = DirtyState.Dirty
    let mutable parents = ParentTracking.NoParents
    let mutable isObserved = false
    let markable = this :> IMarkable

    member private _.RegisterWithDeps() =
        match dep0 with
        | :? ChangeableValue<'A> as cv -> cv.AddParent(markable)
        | _ -> ()

        match dep1 with
        | :? ChangeableValue<'B> as cv -> cv.AddParent(markable)
        | _ -> ()

        match dep2 with
        | :? ChangeableValue<'C> as cv -> cv.AddParent(markable)
        | _ -> ()

    member private _.UnregisterFromDeps() =
        match dep0 with
        | :? ChangeableValue<'A> as cv -> cv.RemoveParent(markable)
        | _ -> ()

        match dep1 with
        | :? ChangeableValue<'B> as cv -> cv.RemoveParent(markable)
        | _ -> ()

        match dep2 with
        | :? ChangeableValue<'C> as cv -> cv.RemoveParent(markable)
        | _ -> ()

    member internal _.AddParent(parent: IMarkable) =
        let shouldRegister =
            Monitor.Enter(syncRoot)

            try
                let wasUnobserved =
                    match parents with
                    | ParentTracking.NoParents -> true
                    | _ -> false

                parents <- ParentTracking.addParent parents parent

                if wasUnobserved && not isObserved then
                    isObserved <- true
                    true
                else
                    false
            finally
                Monitor.Exit(syncRoot)
        // Register after releasing lock
        if shouldRegister then
            this.RegisterWithDeps()

    member internal _.RemoveParent(parent: IMarkable) =
        let shouldUnregister =
            Monitor.Enter(syncRoot)

            try
                parents <- ParentTracking.removeParent parents parent

                let noParents =
                    match parents with
                    | ParentTracking.NoParents -> true
                    | _ -> false

                if noParents then
                    isObserved <- false

                noParents
            finally
                Monitor.Exit(syncRoot)

        if shouldUnregister then
            this.UnregisterFromDeps()

    member private this.IsDirty() =
        // If explicitly dirty (from push notification), definitely dirty
        if dirtyState = DirtyState.Dirty then
            true
        // If not observed, always check versions (no push notification possible)
        elif not isObserved then
            not hasValue
            || (dep0 :> IAdaptiveObject).Version <> ver0
            || (dep1 :> IAdaptiveObject).Version <> ver1
            || (dep2 :> IAdaptiveObject).Version <> ver2
        // If observed and clean, trust the dirty state
        elif dirtyState = DirtyState.Clean then
            false
        else // MaybeDirty - fall back to version check
            not hasValue
            || (dep0 :> IAdaptiveObject).Version <> ver0
            || (dep1 :> IAdaptiveObject).Version <> ver1
            || (dep2 :> IAdaptiveObject).Version <> ver2

    member private this.Recompute() =
        let v0 = dep0.GetValue()
        let v1 = dep1.GetValue()
        let v2 = dep2.GetValue()
        value <- compute v0 v1 v2
        ver0 <- (dep0 :> IAdaptiveObject).Version
        ver1 <- (dep1 :> IAdaptiveObject).Version
        ver2 <- (dep2 :> IAdaptiveObject).Version
        hasValue <- true
        version <- version + 1L
        dirtyState <- DirtyState.Clean

    interface IMarkable with
        member _.MarkDirty() =
            let parentsToNotify =
                Monitor.Enter(syncRoot)

                try
                    if dirtyState <> DirtyState.Dirty then
                        dirtyState <- DirtyState.Dirty
                        parents
                    else
                        ParentTracking.NoParents
                finally
                    Monitor.Exit(syncRoot)

            ParentTracking.markParentsDirty parentsToNotify

    interface IAdaptiveValue<'T> with
        member this.GetValue() =
            AdaptiveRuntime.enterEvaluation ()

            try
                Monitor.Enter(syncRoot)

                try
                    if this.IsDirty() then
                        this.Recompute()

                    AdaptiveRuntime.addDependency (this :> IAdaptiveObject) version
                    value
                finally
                    Monitor.Exit(syncRoot)
            finally
                AdaptiveRuntime.exitEvaluation ()

        member this.Version =
            AdaptiveRuntime.enterEvaluation ()

            try
                Monitor.Enter(syncRoot)

                try
                    if this.IsDirty() then version + 1L else version
                finally
                    Monitor.Exit(syncRoot)
            finally
                AdaptiveRuntime.exitEvaluation ()

/// <summary>
/// Specialized adaptive node that combines exactly four dependencies with inline field storage.
/// </summary>
/// <typeparam name="A">Type of the first dependency.</typeparam>
/// <typeparam name="B">Type of the second dependency.</typeparam>
/// <typeparam name="C">Type of the third dependency.</typeparam>
/// <typeparam name="D">Type of the fourth dependency.</typeparam>
/// <typeparam name="T">Type of the computed result.</typeparam>
/// <remarks>
/// <para>
/// This node type is more efficient than chaining <c>map2</c> calls because:
/// <list type="bullet">
/// <item><description>Uses inline fields instead of arrays for dependency tracking</description></item>
/// <item><description>Single node instead of three intermediate nodes</description></item>
/// <item><description>Supports lazy push invalidation when observed by parent nodes</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Internal implementation detail:</strong> Created via <see cref="AVal.map4"/>.
/// </para>
/// </remarks>
and Map4Node<'A, 'B, 'C, 'D, 'T>
    (
        dep0: IAdaptiveValue<'A>,
        dep1: IAdaptiveValue<'B>,
        dep2: IAdaptiveValue<'C>,
        dep3: IAdaptiveValue<'D>,
        [<InlineIfLambda>] compute: 'A -> 'B -> 'C -> 'D -> 'T
    ) as this =
    let syncRoot = obj ()
    let mutable version = 0L
    let mutable hasValue = false
    let mutable value = Unchecked.defaultof<'T>
    let mutable ver0 = 0L
    let mutable ver1 = 0L
    let mutable ver2 = 0L
    let mutable ver3 = 0L
    let mutable dirtyState = DirtyState.Dirty
    let mutable parents = ParentTracking.NoParents
    let mutable isObserved = false
    let markable = this :> IMarkable

    member private _.RegisterWithDeps() =
        match dep0 with
        | :? ChangeableValue<'A> as cv -> cv.AddParent(markable)
        | _ -> ()

        match dep1 with
        | :? ChangeableValue<'B> as cv -> cv.AddParent(markable)
        | _ -> ()

        match dep2 with
        | :? ChangeableValue<'C> as cv -> cv.AddParent(markable)
        | _ -> ()

        match dep3 with
        | :? ChangeableValue<'D> as cv -> cv.AddParent(markable)
        | _ -> ()

    member private _.UnregisterFromDeps() =
        match dep0 with
        | :? ChangeableValue<'A> as cv -> cv.RemoveParent(markable)
        | _ -> ()

        match dep1 with
        | :? ChangeableValue<'B> as cv -> cv.RemoveParent(markable)
        | _ -> ()

        match dep2 with
        | :? ChangeableValue<'C> as cv -> cv.RemoveParent(markable)
        | _ -> ()

        match dep3 with
        | :? ChangeableValue<'D> as cv -> cv.RemoveParent(markable)
        | _ -> ()

    member internal _.AddParent(parent: IMarkable) =
        let shouldRegister =
            Monitor.Enter(syncRoot)

            try
                let wasUnobserved =
                    match parents with
                    | ParentTracking.NoParents -> true
                    | _ -> false

                parents <- ParentTracking.addParent parents parent

                if wasUnobserved && not isObserved then
                    isObserved <- true
                    true
                else
                    false
            finally
                Monitor.Exit(syncRoot)

        if shouldRegister then
            this.RegisterWithDeps()

    member internal _.RemoveParent(parent: IMarkable) =
        let shouldUnregister =
            Monitor.Enter(syncRoot)

            try
                parents <- ParentTracking.removeParent parents parent

                let noParents =
                    match parents with
                    | ParentTracking.NoParents -> true
                    | _ -> false

                if noParents then
                    isObserved <- false

                noParents
            finally
                Monitor.Exit(syncRoot)

        if shouldUnregister then
            this.UnregisterFromDeps()

    member private this.IsDirty() =
        // If explicitly dirty (from push notification), definitely dirty
        if dirtyState = DirtyState.Dirty then
            true
        // If not observed, always check versions (no push notification possible)
        elif not isObserved then
            not hasValue
            || (dep0 :> IAdaptiveObject).Version <> ver0
            || (dep1 :> IAdaptiveObject).Version <> ver1
            || (dep2 :> IAdaptiveObject).Version <> ver2
            || (dep3 :> IAdaptiveObject).Version <> ver3
        // If observed and clean, trust the dirty state
        elif dirtyState = DirtyState.Clean then
            false
        else // MaybeDirty
            not hasValue
            || (dep0 :> IAdaptiveObject).Version <> ver0
            || (dep1 :> IAdaptiveObject).Version <> ver1
            || (dep2 :> IAdaptiveObject).Version <> ver2
            || (dep3 :> IAdaptiveObject).Version <> ver3

    member private this.Recompute() =
        let v0 = dep0.GetValue()
        let v1 = dep1.GetValue()
        let v2 = dep2.GetValue()
        let v3 = dep3.GetValue()
        value <- compute v0 v1 v2 v3
        ver0 <- (dep0 :> IAdaptiveObject).Version
        ver1 <- (dep1 :> IAdaptiveObject).Version
        ver2 <- (dep2 :> IAdaptiveObject).Version
        ver3 <- (dep3 :> IAdaptiveObject).Version
        hasValue <- true
        version <- version + 1L
        dirtyState <- DirtyState.Clean

    interface IMarkable with
        member _.MarkDirty() =
            let parentsToNotify =
                Monitor.Enter(syncRoot)

                try
                    if dirtyState <> DirtyState.Dirty then
                        dirtyState <- DirtyState.Dirty
                        parents
                    else
                        ParentTracking.NoParents
                finally
                    Monitor.Exit(syncRoot)

            ParentTracking.markParentsDirty parentsToNotify

    interface IAdaptiveValue<'T> with
        member this.GetValue() =
            AdaptiveRuntime.enterEvaluation ()

            try
                Monitor.Enter(syncRoot)

                try
                    if this.IsDirty() then
                        this.Recompute()

                    AdaptiveRuntime.addDependency (this :> IAdaptiveObject) version
                    value
                finally
                    Monitor.Exit(syncRoot)
            finally
                AdaptiveRuntime.exitEvaluation ()

        member this.Version =
            AdaptiveRuntime.enterEvaluation ()

            try
                Monitor.Enter(syncRoot)

                try
                    if this.IsDirty() then version + 1L else version
                finally
                    Monitor.Exit(syncRoot)
            finally
                AdaptiveRuntime.exitEvaluation ()

/// <summary>
/// Specialized adaptive node that combines N dependencies of the same type.
/// Dramatically more efficient than chaining <c>map2</c> for wide fan-in patterns.
/// </summary>
/// <typeparam name="T">Type of each dependency value.</typeparam>
/// <typeparam name="U">Type of the computed result.</typeparam>
/// <remarks>
/// <para>
/// <strong>Performance characteristics:</strong>
/// <list type="bullet">
/// <item><description>Single node instead of O(N) nodes from chained map2</description></item>
/// <item><description>Tight loop for version checking (cache-friendly)</description></item>
/// <item><description>3-100× faster than map2 chains for 10-500 inputs</description></item>
/// <item><description>Constant memory overhead regardless of input count</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Dirty propagation:</strong> When observed by parent nodes, this node registers
/// with ChangeableValue dependencies for push-based invalidation, avoiding unnecessary
/// version checks on unchanged values.
/// </para>
/// <para>
/// <strong>Internal implementation detail:</strong> Created via <see cref="AVal.mapN"/>.
/// </para>
/// </remarks>
and MapNNode<'T, 'U>(deps: IAdaptiveValue<'T>[], [<InlineIfLambda>] compute: 'T[] -> 'U) as this =
    let syncRoot = obj ()
    let mutable version = 0L
    let mutable hasValue = false
    let mutable value = Unchecked.defaultof<'U>
    let depVersions = Array.zeroCreate<int64> deps.Length
    let mutable dirtyState = DirtyState.Dirty
    let mutable parents = ParentTracking.NoParents
    let mutable isObserved = false
    let markable = this :> IMarkable

    member private _.RegisterWithDeps() =
        for dep in deps do
            match dep with
            | :? ChangeableValue<'T> as cv -> cv.AddParent(markable)
            | _ -> ()

    member private _.UnregisterFromDeps() =
        for dep in deps do
            match dep with
            | :? ChangeableValue<'T> as cv -> cv.RemoveParent(markable)
            | _ -> ()

    member internal _.AddParent(parent: IMarkable) =
        let shouldRegister =
            Monitor.Enter(syncRoot)

            try
                let wasUnobserved =
                    match parents with
                    | ParentTracking.NoParents -> true
                    | _ -> false

                parents <- ParentTracking.addParent parents parent

                if wasUnobserved && not isObserved then
                    isObserved <- true
                    true
                else
                    false
            finally
                Monitor.Exit(syncRoot)

        if shouldRegister then
            this.RegisterWithDeps()

    member internal _.RemoveParent(parent: IMarkable) =
        let shouldUnregister =
            Monitor.Enter(syncRoot)

            try
                parents <- ParentTracking.removeParent parents parent

                let noParents =
                    match parents with
                    | ParentTracking.NoParents -> true
                    | _ -> false

                if noParents then
                    isObserved <- false

                noParents
            finally
                Monitor.Exit(syncRoot)

        if shouldUnregister then
            this.UnregisterFromDeps()

    member private this.IsDirty() =
        // If explicitly dirty (from push notification), definitely dirty
        if dirtyState = DirtyState.Dirty then
            true
        // If not observed, always check versions (no push notification possible)
        elif not isObserved then
            let mutable dirty = not hasValue
            let mutable i = 0

            while not dirty && i < deps.Length do
                if (deps.[i] :> IAdaptiveObject).Version <> depVersions.[i] then
                    dirty <- true

                i <- i + 1

            dirty
        // If observed and clean, trust the dirty state
        elif dirtyState = DirtyState.Clean then
            false
        else // MaybeDirty
            let mutable dirty = not hasValue
            let mutable i = 0

            while not dirty && i < deps.Length do
                if (deps.[i] :> IAdaptiveObject).Version <> depVersions.[i] then
                    dirty <- true

                i <- i + 1

            dirty

    member private this.Recompute() =
        let values = Array.zeroCreate deps.Length

        for i in 0 .. deps.Length - 1 do
            values.[i] <- deps.[i].GetValue()
            depVersions.[i] <- (deps.[i] :> IAdaptiveObject).Version

        value <- compute values
        hasValue <- true
        version <- version + 1L
        dirtyState <- DirtyState.Clean

    interface IMarkable with
        member _.MarkDirty() =
            let parentsToNotify =
                Monitor.Enter(syncRoot)

                try
                    if dirtyState <> DirtyState.Dirty then
                        dirtyState <- DirtyState.Dirty
                        parents
                    else
                        ParentTracking.NoParents
                finally
                    Monitor.Exit(syncRoot)

            ParentTracking.markParentsDirty parentsToNotify

    interface IAdaptiveValue<'U> with
        member this.GetValue() =
            AdaptiveRuntime.enterEvaluation ()

            try
                Monitor.Enter(syncRoot)

                try
                    if this.IsDirty() then
                        this.Recompute()

                    AdaptiveRuntime.addDependency (this :> IAdaptiveObject) version
                    value
                finally
                    Monitor.Exit(syncRoot)
            finally
                AdaptiveRuntime.exitEvaluation ()

        member this.Version =
            AdaptiveRuntime.enterEvaluation ()

            try
                Monitor.Enter(syncRoot)

                try
                    if this.IsDirty() then version + 1L else version
                finally
                    Monitor.Exit(syncRoot)
            finally
                AdaptiveRuntime.exitEvaluation ()

/// <summary>
/// Specialized adaptive node that reduces N dependencies using a binary operation.
/// Optimized for aggregation patterns like sum, product, min, max.
/// </summary>
/// <typeparam name="T">Type of each dependency value and the result.</typeparam>
/// <remarks>
/// <para>
/// <strong>Performance characteristics:</strong>
/// <list type="bullet">
/// <item><description>Single node instead of O(N) nodes from chained operations</description></item>
/// <item><description>No intermediate array allocation (unlike MapNNode)</description></item>
/// <item><description>3-100× faster than equivalent map2 chains for 10-500 inputs</description></item>
/// <item><description>Constant memory overhead regardless of input count</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Empty array behavior:</strong> Returns the <c>init</c> value when there are no dependencies.
/// </para>
/// <para>
/// <strong>Reduction semantics:</strong> Values are reduced left-to-right:
/// <c>reduce(reduce(reduce(init, v0), v1), v2) ...</c>
/// For associative operations (addition, multiplication), order doesn't matter.
/// </para>
/// <para>
/// <strong>Internal implementation detail:</strong> Created via <see cref="AVal.reduce"/> or <see cref="AVal.sum"/>.
/// </para>
/// </remarks>
and ReduceNode<'T>(deps: IAdaptiveValue<'T>[], init: 'T, [<InlineIfLambda>] reduce: 'T -> 'T -> 'T) as this =
    let syncRoot = obj ()
    let mutable version = 0L
    let mutable hasValue = false
    let mutable value = Unchecked.defaultof<'T>
    let depVersions = Array.zeroCreate<int64> deps.Length
    let mutable dirtyState = DirtyState.Dirty
    let mutable parents = ParentTracking.NoParents
    let mutable isObserved = false
    let markable = this :> IMarkable

    member private _.RegisterWithDeps() =
        for dep in deps do
            match dep with
            | :? ChangeableValue<'T> as cv -> cv.AddParent(markable)
            | _ -> ()

    member private _.UnregisterFromDeps() =
        for dep in deps do
            match dep with
            | :? ChangeableValue<'T> as cv -> cv.RemoveParent(markable)
            | _ -> ()

    member internal _.AddParent(parent: IMarkable) =
        let shouldRegister =
            Monitor.Enter(syncRoot)

            try
                let wasUnobserved =
                    match parents with
                    | ParentTracking.NoParents -> true
                    | _ -> false

                parents <- ParentTracking.addParent parents parent

                if wasUnobserved && not isObserved then
                    isObserved <- true
                    true
                else
                    false
            finally
                Monitor.Exit(syncRoot)

        if shouldRegister then
            this.RegisterWithDeps()

    member internal _.RemoveParent(parent: IMarkable) =
        let shouldUnregister =
            Monitor.Enter(syncRoot)

            try
                parents <- ParentTracking.removeParent parents parent

                let noParents =
                    match parents with
                    | ParentTracking.NoParents -> true
                    | _ -> false

                if noParents then
                    isObserved <- false

                noParents
            finally
                Monitor.Exit(syncRoot)

        if shouldUnregister then
            this.UnregisterFromDeps()

    member private this.IsDirty() =
        // If explicitly dirty (from push notification), definitely dirty
        if dirtyState = DirtyState.Dirty then
            true
        // If not observed, always check versions (no push notification possible)
        elif not isObserved then
            let mutable dirty = not hasValue
            let mutable i = 0

            while not dirty && i < deps.Length do
                if (deps.[i] :> IAdaptiveObject).Version <> depVersions.[i] then
                    dirty <- true

                i <- i + 1

            dirty
        // If observed and clean, trust the dirty state
        elif dirtyState = DirtyState.Clean then
            false
        else // MaybeDirty
            let mutable dirty = not hasValue
            let mutable i = 0

            while not dirty && i < deps.Length do
                if (deps.[i] :> IAdaptiveObject).Version <> depVersions.[i] then
                    dirty <- true

                i <- i + 1

            dirty

    member private this.Recompute() =
        let mutable acc = init

        for i in 0 .. deps.Length - 1 do
            let v = deps.[i].GetValue()
            depVersions.[i] <- (deps.[i] :> IAdaptiveObject).Version
            acc <- reduce acc v

        value <- acc
        hasValue <- true
        version <- version + 1L
        dirtyState <- DirtyState.Clean

    interface IMarkable with
        member _.MarkDirty() =
            let parentsToNotify =
                Monitor.Enter(syncRoot)

                try
                    if dirtyState <> DirtyState.Dirty then
                        dirtyState <- DirtyState.Dirty
                        parents
                    else
                        ParentTracking.NoParents
                finally
                    Monitor.Exit(syncRoot)

            ParentTracking.markParentsDirty parentsToNotify

    interface IAdaptiveValue<'T> with
        member this.GetValue() =
            AdaptiveRuntime.enterEvaluation ()

            try
                Monitor.Enter(syncRoot)

                try
                    if this.IsDirty() then
                        this.Recompute()

                    AdaptiveRuntime.addDependency (this :> IAdaptiveObject) version
                    value
                finally
                    Monitor.Exit(syncRoot)
            finally
                AdaptiveRuntime.exitEvaluation ()

        member this.Version =
            AdaptiveRuntime.enterEvaluation ()

            try
                Monitor.Enter(syncRoot)

                try
                    if this.IsDirty() then version + 1L else version
                finally
                    Monitor.Exit(syncRoot)
            finally
                AdaptiveRuntime.exitEvaluation ()

type ChangeableSet<'T when 'T: comparison>(initial: Set<'T>) =
    let syncRoot = obj ()
    let mutable version = 0L
    let mutable data = HashSet<'T>(initial.Count)
    let mutable snapshot = initial
    let mutable snapshotVersion = -1L
    let mutable sinks: obj[] = Array.zeroCreate 4
    let mutable sinkCount = 0
    let mutable journalAdds: 'T[] = ArrayPool<'T>.Shared.Rent 16
    let mutable journalAddCount = 0
    let mutable journalRems: 'T[] = ArrayPool<'T>.Shared.Rent 16
    let mutable journalRemCount = 0
    let mutable flushEnqueued = false

    do
        for item in initial do
            data.Add item |> ignore

    member private _.InvalidateSnapshot() = snapshotVersion <- -1L

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

    member internal this.RemoveSink(sink: ISetDeltaSink<'T>) =
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
        finally
            Monitor.Exit syncRoot

    member private this.FlushDeltas(newVersion: int64, adds: 'T[], addCount: int, rems: 'T[], remCount: int) =
        if addCount > 0 || remCount > 0 then
            let sinksSnapshot =
                Monitor.Enter syncRoot

                try
                    if sinkCount = 0 then
                        Array.empty
                    else
                        let arr = Array.zeroCreate sinkCount
                        Array.Copy(sinks, arr, sinkCount)
                        arr
                finally
                    Monitor.Exit syncRoot

            for i in 0 .. sinksSnapshot.Length - 1 do
                (unbox<ISetDeltaSink<'T>> sinksSnapshot[i]).OnDeltas(newVersion, adds, addCount, rems, remCount)

    member private this.JournalFlush() =
        if journalAddCount > 0 || journalRemCount > 0 then
            let adds = journalAdds
            let addCnt = journalAddCount
            let rems = journalRems
            let remCnt = journalRemCount
            journalAdds <- ArrayPool<'T>.Shared.Rent 16
            journalAddCount <- 0
            journalRems <- ArrayPool<'T>.Shared.Rent 16
            journalRemCount <- 0
            Monitor.Enter syncRoot

            try
                for i in 0 .. addCnt - 1 do
                    data.Add adds[i] |> ignore

                for i in 0 .. remCnt - 1 do
                    data.Remove rems[i] |> ignore

                version <- version + 1L
                this.InvalidateSnapshot()
            finally
                Monitor.Exit syncRoot

            this.FlushDeltas(version, adds, addCnt, rems, remCnt)
            ArrayPool<'T>.Shared.Return(adds, true)
            ArrayPool<'T>.Shared.Return(rems, true)
            flushEnqueued <- false

    member private this.ApplyAndFlush(item: 'T, isAdd: bool) =
        let mutable added = false
        Monitor.Enter syncRoot

        try
            if isAdd then
                added <- data.Add item
            else
                added <- data.Remove item

            if added then
                version <- version + 1L
                this.InvalidateSnapshot()
        finally
            Monitor.Exit syncRoot

        if added then
            let bufAdds = ArrayPool<'T>.Shared.Rent 1
            let bufRems = ArrayPool<'T>.Shared.Rent 1

            if isAdd then
                bufAdds[0] <- item
                this.FlushDeltas(version, bufAdds, 1, bufRems, 0)
            else
                bufRems[0] <- item
                this.FlushDeltas(version, bufAdds, 0, bufRems, 1)

            ArrayPool<'T>.Shared.Return(bufAdds, true)
            ArrayPool<'T>.Shared.Return(bufRems, true)

    member internal this.Apply(newValue: Set<'T>) =
        let oldCount = data.Count
        let buffer = ArrayPool<'T>.Shared.Rent(max oldCount newValue.Count)
        let mutable oldIdx = 0
        Monitor.Enter syncRoot

        try
            for item in data do
                if not (newValue.Contains item) then
                    buffer[oldIdx] <- item
                    oldIdx <- oldIdx + 1

            data.Clear()

            for item in newValue do
                data.Add item |> ignore

            version <- version + 1L
            snapshot <- newValue
            snapshotVersion <- version
        finally
            Monitor.Exit syncRoot

        let adds = ArrayPool<'T>.Shared.Rent newValue.Count
        let mutable ai = 0

        for item in newValue do
            adds[ai] <- item
            ai <- ai + 1

        let rems = ArrayPool<'T>.Shared.Rent oldIdx
        Array.Copy(buffer, rems, oldIdx)
        this.FlushDeltas(version, adds, newValue.Count, rems, oldIdx)
        ArrayPool<'T>.Shared.Return(buffer, true)
        ArrayPool<'T>.Shared.Return(adds, true)
        ArrayPool<'T>.Shared.Return(rems, true)

    member this.Set(newValue: Set<'T>) =
        if
            not (
                Transaction.tryEnqueueFactory (fun () ->
                    { new Transaction.ICommit with
                        member _.Commit() = this.Apply newValue })
            )
        then
            this.Apply newValue

    member this.Add(item: 'T) =
        if
            not (
                Transaction.tryEnqueueFactory (fun () ->
                    { new Transaction.ICommit with
                        member _.Commit() =
                            if journalAddCount = journalAdds.Length then
                                let next = ArrayPool<'T>.Shared.Rent(journalAdds.Length * 2)
                                Array.Copy(journalAdds, next, journalAdds.Length)
                                ArrayPool<'T>.Shared.Return(journalAdds, true)
                                journalAdds <- next

                            journalAdds[journalAddCount] <- item
                            journalAddCount <- journalAddCount + 1

                            if not flushEnqueued then
                                flushEnqueued <- true

                                Transaction.tryEnqueue
                                    { new Transaction.ICommit with
                                        member _.Commit() = this.JournalFlush() }
                                |> ignore })
            )
        then
            this.ApplyAndFlush(item, true)

    member this.Remove(item: 'T) =
        if
            not (
                Transaction.tryEnqueueFactory (fun () ->
                    { new Transaction.ICommit with
                        member _.Commit() =
                            if journalRemCount = journalRems.Length then
                                let next = ArrayPool<'T>.Shared.Rent(journalRems.Length * 2)
                                Array.Copy(journalRems, next, journalRems.Length)
                                ArrayPool<'T>.Shared.Return(journalRems, true)
                                journalRems <- next

                            journalRems[journalRemCount] <- item
                            journalRemCount <- journalRemCount + 1

                            if not flushEnqueued then
                                flushEnqueued <- true

                                Transaction.tryEnqueue
                                    { new Transaction.ICommit with
                                        member _.Commit() = this.JournalFlush() }
                                |> ignore })
            )
        then
            this.ApplyAndFlush(item, false)

    interface IAdaptiveSet<'T> with
        member this.GetValue() =
            Monitor.Enter syncRoot

            try
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
                        let buffer = ArrayPool<'T>.Shared.Rent count

                        try
                            let mutable i = 0

                            for item in data do
                                buffer[i] <- item
                                i <- i + 1

                            let seg = ArraySegment(buffer, 0, i)
                            let next = Set.ofSeq seg
                            snapshot <- next
                            snapshotVersion <- version
                            next
                        finally
                            ArrayPool<'T>.Shared.Return(buffer, true)
            finally
                Monitor.Exit syncRoot

        member _.Version = Interlocked.Read &version

    interface ISetSinkRegistry with
        member this.AddSetSink(sink) =
            this.AddSink(unbox<ISetDeltaSink<'T>> sink)

        member this.RemoveSetSink(sink) =
            this.RemoveSink(unbox<ISetDeltaSink<'T>> sink)

type ChangeableMap<'K, 'V when 'K: comparison>(initial: Map<'K, 'V>) =
    let syncRoot = obj ()
    let mutable version = 0L
    let mutable data = Dictionary<'K, 'V>(initial.Count)
    let mutable snapshot = initial
    let mutable snapshotVersion = -1L
    let mutable sinks: obj[] = Array.zeroCreate 4
    let mutable sinkCount = 0

    let mutable journalSets: struct ('K * 'V)[] =
        ArrayPool<struct ('K * 'V)>.Shared.Rent 16

    let mutable journalSetCount = 0
    let mutable journalRems: 'K[] = ArrayPool<'K>.Shared.Rent 16
    let mutable journalRemCount = 0
    let mutable flushEnqueued = false

    do
        for KeyValue(key, value) in initial do
            data.Add(key, value)

    member private _.InvalidateSnapshot() = snapshotVersion <- -1L

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

    member internal this.RemoveSink(sink: IMapDeltaSink<'K, 'V>) =
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
        finally
            Monitor.Exit syncRoot

    member private this.FlushDeltas
        (newVersion: int64, sets: struct ('K * 'V)[], setCount: int, rems: 'K[], remCount: int)
        =
        if setCount > 0 || remCount > 0 then
            let sinksSnapshot =
                Monitor.Enter syncRoot

                try
                    if sinkCount = 0 then
                        Array.empty
                    else
                        let arr = Array.zeroCreate sinkCount
                        Array.Copy(sinks, arr, sinkCount)
                        arr
                finally
                    Monitor.Exit syncRoot

            for i in 0 .. sinksSnapshot.Length - 1 do
                (unbox<IMapDeltaSink<'K, 'V>> sinksSnapshot[i]).OnDeltas(newVersion, sets, setCount, rems, remCount)

    member private this.JournalFlush() =
        if journalSetCount > 0 || journalRemCount > 0 then
            let sets = journalSets
            let setCnt = journalSetCount
            let rems = journalRems
            let remCnt = journalRemCount
            journalSets <- ArrayPool<struct ('K * 'V)>.Shared.Rent 16
            journalSetCount <- 0
            journalRems <- ArrayPool<'K>.Shared.Rent 16
            journalRemCount <- 0
            Monitor.Enter syncRoot

            try
                for i in 0 .. setCnt - 1 do
                    let struct (k, v) = sets[i]
                    data[k] <- v

                for i in 0 .. remCnt - 1 do
                    data.Remove rems[i] |> ignore

                version <- version + 1L
                this.InvalidateSnapshot()
            finally
                Monitor.Exit syncRoot

            this.FlushDeltas(version, sets, setCnt, rems, remCnt)
            ArrayPool<struct ('K * 'V)>.Shared.Return(sets, true)
            ArrayPool<'K>.Shared.Return(rems, true)
            flushEnqueued <- false

    member private this.ApplyAndFlush(key: 'K, valueToSet: 'V, isRemove: bool) =
        let mutable changed = false
        Monitor.Enter syncRoot

        try
            if isRemove then
                changed <- data.Remove key
            else
                match data.TryGetValue key with
                | true, existing when EqualityComparer<'V>.Default.Equals(existing, valueToSet) -> ()
                | _ ->
                    data[key] <- valueToSet
                    changed <- true

            if changed then
                version <- version + 1L
                this.InvalidateSnapshot()
        finally
            Monitor.Exit syncRoot

        if changed then
            let bufSets = ArrayPool<struct ('K * 'V)>.Shared.Rent 1
            let bufRems = ArrayPool<'K>.Shared.Rent 1

            if isRemove then
                bufRems[0] <- key
                this.FlushDeltas(version, bufSets, 0, bufRems, 1)
            else
                bufSets[0] <- struct (key, valueToSet)
                this.FlushDeltas(version, bufSets, 1, bufRems, 0)

            ArrayPool<struct ('K * 'V)>.Shared.Return(bufSets, true)
            ArrayPool<'K>.Shared.Return(bufRems, true)

    member internal this.Apply(newValue: Map<'K, 'V>) =
        let oldCount = data.Count
        let oldKeys = ArrayPool<'K>.Shared.Rent oldCount
        let mutable oldIdx = 0
        let newEntries = ArrayPool<struct ('K * 'V)>.Shared.Rent newValue.Count
        let mutable newIdx = 0
        Monitor.Enter syncRoot

        try
            for key in data.Keys do
                if not (newValue.ContainsKey key) then
                    oldKeys[oldIdx] <- key
                    oldIdx <- oldIdx + 1

            data.Clear()

            for KeyValue(k, v) in newValue do
                data.Add(k, v)
                newEntries[newIdx] <- struct (k, v)
                newIdx <- newIdx + 1

            version <- version + 1L
            snapshot <- newValue
            snapshotVersion <- version
        finally
            Monitor.Exit syncRoot

        let rems = ArrayPool<'K>.Shared.Rent oldIdx
        Array.Copy(oldKeys, rems, oldIdx)
        this.FlushDeltas(version, newEntries, newValue.Count, rems, oldIdx)
        ArrayPool<'K>.Shared.Return(oldKeys, true)
        ArrayPool<struct ('K * 'V)>.Shared.Return(newEntries, true)
        ArrayPool<'K>.Shared.Return(rems, true)

    member this.Set(newValue: Map<'K, 'V>) =
        if
            not (
                Transaction.tryEnqueueFactory (fun () ->
                    { new Transaction.ICommit with
                        member _.Commit() = this.Apply newValue })
            )
        then
            this.Apply newValue

    member this.AddOrUpdate (key: 'K) (valueToSet: 'V) =
        if
            not (
                Transaction.tryEnqueueFactory (fun () ->
                    { new Transaction.ICommit with
                        member _.Commit() =
                            if journalSetCount = journalSets.Length then
                                let next = ArrayPool<struct ('K * 'V)>.Shared.Rent(journalSets.Length * 2)
                                Array.Copy(journalSets, next, journalSets.Length)
                                ArrayPool<struct ('K * 'V)>.Shared.Return(journalSets, true)
                                journalSets <- next

                            journalSets[journalSetCount] <- struct (key, valueToSet)
                            journalSetCount <- journalSetCount + 1

                            if not flushEnqueued then
                                flushEnqueued <- true

                                Transaction.tryEnqueue
                                    { new Transaction.ICommit with
                                        member _.Commit() = this.JournalFlush() }
                                |> ignore })
            )
        then
            this.ApplyAndFlush(key, valueToSet, false)

    member this.Remove(key: 'K) =
        if
            not (
                Transaction.tryEnqueueFactory (fun () ->
                    { new Transaction.ICommit with
                        member _.Commit() =
                            if journalRemCount = journalRems.Length then
                                let next = ArrayPool<'K>.Shared.Rent(journalRems.Length * 2)
                                Array.Copy(journalRems, next, journalRems.Length)
                                ArrayPool<'K>.Shared.Return(journalRems, true)
                                journalRems <- next

                            journalRems[journalRemCount] <- key
                            journalRemCount <- journalRemCount + 1

                            if not flushEnqueued then
                                flushEnqueued <- true

                                Transaction.tryEnqueue
                                    { new Transaction.ICommit with
                                        member _.Commit() = this.JournalFlush() }
                                |> ignore })
            )
        then
            this.ApplyAndFlush(key, Unchecked.defaultof<'V>, true)

    interface IAdaptiveMap<'K, 'V> with
        member this.GetValue() =
            Monitor.Enter syncRoot

            try
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
                        let next = data |> Seq.map (fun (KeyValue(k, v)) -> (k, v)) |> Map.ofSeq
                        snapshot <- next
                        snapshotVersion <- version
                        next
            finally
                Monitor.Exit syncRoot

        member _.Version = Interlocked.Read &version

    interface IMapSinkRegistry with
        member this.AddMapSink(sink) =
            this.AddSink(unbox<IMapDeltaSink<'K, 'V>> sink)

        member this.RemoveMapSink(sink) =
            this.RemoveSink(unbox<IMapDeltaSink<'K, 'V>> sink)

/// <summary>
/// Core operations for creating and transforming adaptive values.
/// Adaptive values automatically track dependencies and recompute only when their inputs change.
/// </summary>
/// <remarks>
/// <para>
/// AdaptiveSlop provides incremental computation through a pull-based model with optional push invalidation.
/// When you read an adaptive value, it checks if any dependencies have changed and recomputes if necessary.
/// </para>
/// <para>
/// <strong>Performance Guidance:</strong>
/// <list type="bullet">
/// <item><description>Use <c>map</c>, <c>map2</c> for 1-2 dependencies (most common case)</description></item>
/// <item><description>Use <c>map3</c>, <c>map4</c> for 3-4 dependencies (avoids intermediate nodes)</description></item>
/// <item><description>Use <c>mapN</c>, <c>reduce</c>, <c>sum</c> for N dependencies (avoids O(N) node chains)</description></item>
/// </list>
/// </para>
/// </remarks>
/// <example>
/// Basic usage:
/// <code>
/// let x = CVal.create 1
/// let y = CVal.create 2
/// let sum = AVal.map2 (+) (CVal.value x) (CVal.value y)
/// printfn "%d" (AVal.getValue sum)  // 3
/// x.Set(10)
/// printfn "%d" (AVal.getValue sum)  // 12
/// </code>
/// </example>
module AVal =
    /// <summary>
    /// Creates a constant adaptive value that never changes.
    /// </summary>
    /// <param name="value">The constant value.</param>
    /// <returns>An adaptive value that always returns the given value.</returns>
    /// <remarks>
    /// Constant values have zero overhead - they never recompute and don't track dependencies.
    /// Use this for values that are known at creation time and will never change.
    /// </remarks>
    /// <example>
    /// <code>
    /// let pi = AVal.constant 3.14159
    /// let doubled = AVal.map (fun x -> x * 2.0) pi
    /// </code>
    /// </example>
    let inline constant (value: 'T) : IAdaptiveValue<'T> = ConstantValue value

    /// <summary>
    /// Transforms an adaptive value using a mapping function.
    /// </summary>
    /// <param name="f">The function to apply to the value.</param>
    /// <param name="value">The source adaptive value.</param>
    /// <returns>A new adaptive value that applies the function to the source.</returns>
    /// <remarks>
    /// The function is called lazily - only when the result is read and the source has changed.
    /// The result is cached until the source changes.
    /// </remarks>
    /// <example>
    /// <code>
    /// let celsius = CVal.create 20.0
    /// let fahrenheit = AVal.map (fun c -> c * 9.0/5.0 + 32.0) (CVal.value celsius)
    /// </code>
    /// </example>
    let inline map ([<InlineIfLambda>] f: 'T -> 'U) (value: IAdaptiveValue<'T>) : IAdaptiveValue<'U> =
        AdaptiveNode(fun () -> f (value.GetValue()))

    /// <summary>
    /// Combines two adaptive values using a mapping function.
    /// </summary>
    /// <param name="f">The function to combine the two values.</param>
    /// <param name="left">The first adaptive value.</param>
    /// <param name="right">The second adaptive value.</param>
    /// <returns>A new adaptive value that combines both inputs.</returns>
    /// <remarks>
    /// Recomputes only when either input changes. Both inputs are read in a single evaluation.
    /// </remarks>
    /// <example>
    /// <code>
    /// let width = CVal.create 10.0
    /// let height = CVal.create 20.0
    /// let area = AVal.map2 (*) (CVal.value width) (CVal.value height)
    /// </code>
    /// </example>
    let inline map2
        ([<InlineIfLambda>] f: 'T -> 'U -> 'V)
        (left: IAdaptiveValue<'T>)
        (right: IAdaptiveValue<'U>)
        : IAdaptiveValue<'V> =
        AdaptiveNode(fun () -> f (left.GetValue()) (right.GetValue()))

    /// <summary>
    /// Combines three adaptive values using a mapping function.
    /// Uses a specialized node with inline dependency tracking for better performance.
    /// </summary>
    /// <param name="f">The function to combine the three values.</param>
    /// <param name="a">The first adaptive value.</param>
    /// <param name="b">The second adaptive value.</param>
    /// <param name="c">The third adaptive value.</param>
    /// <returns>A new adaptive value that combines all three inputs.</returns>
    /// <remarks>
    /// <para>
    /// More efficient than chaining <c>map2</c> calls because it uses a single node
    /// with inline fields instead of creating intermediate nodes.
    /// </para>
    /// <para>
    /// <strong>Performance:</strong> Uses inline dependency storage (no array allocation).
    /// Supports lazy push invalidation when observed.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// let r = CVal.create 255
    /// let g = CVal.create 128
    /// let b = CVal.create 64
    /// let color = AVal.map3 (fun r g b -> sprintf "#%02X%02X%02X" r g b)
    ///                       (CVal.value r) (CVal.value g) (CVal.value b)
    /// </code>
    /// </example>
    let inline map3
        ([<InlineIfLambda>] f: 'A -> 'B -> 'C -> 'T)
        (a: IAdaptiveValue<'A>)
        (b: IAdaptiveValue<'B>)
        (c: IAdaptiveValue<'C>)
        : IAdaptiveValue<'T> =
        Map3Node(a, b, c, f)

    /// <summary>
    /// Combines four adaptive values using a mapping function.
    /// Uses a specialized node with inline dependency tracking for better performance.
    /// </summary>
    /// <param name="f">The function to combine the four values.</param>
    /// <param name="a">The first adaptive value.</param>
    /// <param name="b">The second adaptive value.</param>
    /// <param name="c">The third adaptive value.</param>
    /// <param name="d">The fourth adaptive value.</param>
    /// <returns>A new adaptive value that combines all four inputs.</returns>
    /// <remarks>
    /// <para>
    /// More efficient than chaining <c>map2</c> calls because it uses a single node
    /// with inline fields instead of creating intermediate nodes.
    /// </para>
    /// <para>
    /// <strong>Performance:</strong> Uses inline dependency storage (no array allocation).
    /// Supports lazy push invalidation when observed.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// let x = CVal.create 0.0
    /// let y = CVal.create 0.0
    /// let width = CVal.create 100.0
    /// let height = CVal.create 50.0
    /// let rect = AVal.map4 (fun x y w h -> { X = x; Y = y; Width = w; Height = h })
    ///                      (CVal.value x) (CVal.value y) (CVal.value width) (CVal.value height)
    /// </code>
    /// </example>
    let inline map4
        ([<InlineIfLambda>] f: 'A -> 'B -> 'C -> 'D -> 'T)
        (a: IAdaptiveValue<'A>)
        (b: IAdaptiveValue<'B>)
        (c: IAdaptiveValue<'C>)
        (d: IAdaptiveValue<'D>)
        : IAdaptiveValue<'T> =
        Map4Node(a, b, c, d, f)

    /// <summary>
    /// Combines N adaptive values of the same type using a function that receives all values as an array.
    /// Optimized for wide fan-in patterns where many inputs feed into a single computation.
    /// </summary>
    /// <param name="compute">A function that receives an array of all current values and produces the result.</param>
    /// <param name="deps">An array of adaptive values to combine.</param>
    /// <returns>A new adaptive value that combines all inputs.</returns>
    /// <remarks>
    /// <para>
    /// <strong>When to use:</strong> Use <c>mapN</c> when you need to combine 5+ values of the same type,
    /// or when you need access to all values as an array (e.g., for aggregation, filtering).
    /// </para>
    /// <para>
    /// <strong>Performance:</strong> Uses a single node instead of O(N) nodes from chained <c>map2</c> calls.
    /// This provides 3-100× speedup for wide fan-in patterns (10-500 inputs).
    /// Memory usage is constant regardless of input count.
    /// </para>
    /// <para>
    /// <strong>Note:</strong> The array passed to the compute function is freshly allocated on each recomputation.
    /// If you only need a reduction (sum, min, max, etc.), prefer <see cref="reduce"/> for better performance.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Combine 10 sensor readings into their average
    /// let sensors = Array.init 10 (fun i -> CVal.create (float i))
    /// let deps = sensors |> Array.map (fun s -> CVal.value s :> IAdaptiveValue&lt;float&gt;)
    /// let average = AVal.mapN (fun values -> Array.average values) deps
    /// </code>
    /// </example>
    let inline mapN ([<InlineIfLambda>] compute: 'T[] -> 'U) (deps: IAdaptiveValue<'T>[]) : IAdaptiveValue<'U> =
        MapNNode(deps, compute)

    /// <summary>
    /// Reduces N adaptive values using a binary operation and initial value.
    /// Optimized for wide fan-in aggregation patterns (sum, product, min, max, etc.).
    /// </summary>
    /// <param name="init">The initial/identity value for the reduction (e.g., 0 for sum, 1 for product).</param>
    /// <param name="reduce">A binary function to combine values (must be associative for correct results).</param>
    /// <param name="deps">An array of adaptive values to reduce.</param>
    /// <returns>A new adaptive value containing the reduction result.</returns>
    /// <remarks>
    /// <para>
    /// <strong>When to use:</strong> Use <c>reduce</c> for aggregations like sum, product, min, max,
    /// string concatenation, or any fold-like operation over adaptive values.
    /// </para>
    /// <para>
    /// <strong>Performance:</strong> More efficient than <c>mapN</c> for reductions because it doesn't
    /// allocate an intermediate array. Uses a single node instead of O(N) nodes from chained <c>map2</c>.
    /// Provides 3-100× speedup for wide fan-in patterns.
    /// </para>
    /// <para>
    /// <strong>Empty array behavior:</strong> Returns the <paramref name="init"/> value when <paramref name="deps"/> is empty.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Sum of prices
    /// let prices = [| CVal.create 10.0; CVal.create 20.0; CVal.create 15.0 |]
    /// let deps = prices |> Array.map (fun p -> CVal.value p :> IAdaptiveValue&lt;float&gt;)
    /// let total = AVal.reduce 0.0 (+) deps
    ///
    /// // Product
    /// let product = AVal.reduce 1.0 (*) deps
    ///
    /// // Maximum (using System.Double.MinValue as identity)
    /// let maxPrice = AVal.reduce System.Double.MinValue max deps
    /// </code>
    /// </example>
    let inline reduce
        (init: 'T)
        ([<InlineIfLambda>] reduce: 'T -> 'T -> 'T)
        (deps: IAdaptiveValue<'T>[])
        : IAdaptiveValue<'T> =
        ReduceNode(deps, init, reduce)

    /// <summary>
    /// Sums N adaptive integer values. Convenience function equivalent to <c>reduce 0 (+)</c>.
    /// </summary>
    /// <param name="deps">An array of adaptive integer values to sum.</param>
    /// <returns>A new adaptive value containing the sum.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Performance:</strong> Uses a single node instead of O(N) nodes from chained additions.
    /// Provides 3-100× speedup for summing many values (10-500 inputs).
    /// </para>
    /// <para>
    /// <strong>Empty array behavior:</strong> Returns 0 when <paramref name="deps"/> is empty.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// let scores = [| CVal.create 85; CVal.create 92; CVal.create 78 |]
    /// let deps = scores |> Array.map (fun s -> CVal.value s :> IAdaptiveValue&lt;int&gt;)
    /// let totalScore = AVal.sum deps
    ///
    /// scores.[0].Set(90)
    /// printfn "Total: %d" (AVal.getValue totalScore)  // Total: 260
    /// </code>
    /// </example>
    let inline sum (deps: IAdaptiveValue<int>[]) : IAdaptiveValue<int> =
        ReduceNode(deps, 0, (+)) :> IAdaptiveValue<int>

    /// <summary>
    /// Transforms an adaptive value using an async function that returns a Task.
    /// </summary>
    /// <param name="f">The async function to apply.</param>
    /// <param name="value">The source adaptive value.</param>
    /// <returns>An adaptive value containing Tasks of the result type.</returns>
    let inline mapTask ([<InlineIfLambda>] f: 'T -> Task<'U>) (value: IAdaptiveValue<'T>) : IAdaptiveValue<Task<'U>> =
        AdaptiveNode(fun () -> f (value.GetValue()))

    let inline mapValueTask
        ([<InlineIfLambda>] f: 'T -> ValueTask<'U>)
        (value: IAdaptiveValue<'T>)
        : IAdaptiveValue<ValueTask<'U>> =
        AdaptiveNode(fun () -> f (value.GetValue()))

    let inline bind ([<InlineIfLambda>] f: 'T -> IAdaptiveValue<'U>) (value: IAdaptiveValue<'T>) : IAdaptiveValue<'U> =
        AdaptiveNode(fun () ->
            let inner = f (value.GetValue())
            inner.GetValue())

    let inline bindTask ([<InlineIfLambda>] f: 'T -> Task<'U>) (value: IAdaptiveValue<'T>) : Task<'U> =
        value.GetValue() |> f

    let inline bindValueTask ([<InlineIfLambda>] f: 'T -> ValueTask<'U>) (value: IAdaptiveValue<'T>) : ValueTask<'U> =
        value.GetValue() |> f

    let inline mapTaskResult
        ([<InlineIfLambda>] f: 'T -> 'U)
        (value: IAdaptiveValue<Task<'T>>)
        : IAdaptiveValue<Task<'U>> =
        AdaptiveNode(fun () ->
            task {
                let! inner = value.GetValue()
                return f inner
            })

    let inline mapValueTaskResult
        ([<InlineIfLambda>] f: 'T -> 'U)
        (value: IAdaptiveValue<ValueTask<'T>>)
        : IAdaptiveValue<ValueTask<'U>> =
        AdaptiveNode(fun () ->
            ValueTask<'U>(
                task {
                    let! inner = value.GetValue()
                    return f inner
                }
            ))

    let inline bindTaskResult
        ([<InlineIfLambda>] f: 'T -> Task<'U>)
        (value: IAdaptiveValue<Task<'T>>)
        : IAdaptiveValue<Task<'U>> =
        AdaptiveNode(fun () ->
            task {
                let! inner = value.GetValue()
                return! f inner
            })

    let inline bindValueTaskResult
        ([<InlineIfLambda>] f: 'T -> ValueTask<'U>)
        (value: IAdaptiveValue<ValueTask<'T>>)
        : IAdaptiveValue<ValueTask<'U>> =
        AdaptiveNode(fun () ->
            ValueTask<'U>(
                task {
                    let! inner = value.GetValue()
                    return! f inner
                }
            ))

    let inline getValue (value: IAdaptiveValue<'T>) = value.GetValue()

    let inline getValueTask (value: IAdaptiveValue<'T>) = Task.FromResult(value.GetValue())

    let inline getValueValueTask (value: IAdaptiveValue<'T>) = ValueTask<'T>(value.GetValue())

module CVal =
    let inline create (value: 'T) = ChangeableValue value

    let inline set (value: 'T) (cval: ChangeableValue<'T>) = cval.Set value

    let inline value (cval: ChangeableValue<'T>) : IAdaptiveValue<'T> = cval
