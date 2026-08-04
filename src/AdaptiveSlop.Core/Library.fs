namespace AdaptiveSlop.Core

open System
open System.Buffers
open System.Collections.Generic
open System.Diagnostics
open System.Threading.Tasks

/// <summary>
/// Represents the dirty state of an adaptive node. Writes mark; reads recompute.
/// </summary>
/// <remarks>
/// <para>
/// AdaptiveSlop uses a push-mark, pull-evaluate model:
/// <list type="bullet">
/// <item><description><c>Clean</c>: Observed node is up-to-date; the next read is one flag check</description></item>
/// <item><description><c>Dirty</c>: Node was marked by a dependency change; the next read recomputes</description></item>
/// <item><description><c>MaybeDirty</c>: Node is unobserved or its links can be stale; the next read version-checks</description></item>
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
/// An adaptive value that automatically tracks dependencies and recomputes when inputs change.
/// </summary>
/// <typeparam name="T">The type of the value.</typeparam>
/// <remarks>
/// <para>
/// Adaptive values form a dependency graph. When you read a value via <c>GetValue()</c>,
/// the system checks if any dependencies have changed and recomputes if necessary.
/// </para>
/// <para>
/// <strong>Threading:</strong> A graph is confined to one owner thread. All reads and
/// writes must run on that thread. Cross-thread changes must be posted to the owner
/// thread, never applied directly.
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

/// <summary>
/// A unit of deferred work applied at transaction commit.
/// </summary>
type ICommit =
    /// <summary>Applies the deferred work.</summary>
    abstract member Commit: unit -> unit
    /// <summary>Discards the deferred work after a transaction rollback.</summary>
    abstract member Abort: unit -> unit

/// <summary>
/// Internal. An observation sink that receives its callback after a batch or a write.
/// </summary>
type internal INotifiable =
    /// <summary>Delivers the pending notification.</summary>
    abstract member Deliver: unit -> unit

/// <summary>
/// Internal. Reusable buffer of commit actions for one graph context.
/// </summary>
type internal TransactionBuffer() =
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

    member _.Abort() =
        let mutable i = 0

        while i < count do
            buffer[i].Abort()
            i <- i + 1

        Array.Clear(buffer, 0, count)
        count <- 0

/// <summary>
/// Internal. Re-entrant dependency collector with stack frames.
/// One instance lives on each graph context.
/// </summary>
type internal DependencyCollector() =
    let mutable depBuffer: IAdaptiveObject[] = Array.zeroCreate 16
    let mutable versionBuffer: int64[] = Array.zeroCreate 16
    let mutable count = 0
    // Frame stack: stores the starting index of each nested evaluation
    let mutable frameStarts: int[] = Array.zeroCreate 8
    let mutable frameDepth = 0

    member _.Reset() =
        count <- 0
        frameDepth <- 0

    member _.FrameDepth = frameDepth

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
        frameDepth

    /// Get the current frame's deps (depBuffer, versionBuffer, start, length)
    /// Returns struct tuple to avoid heap allocation
    member _.CurrentFrame() =
        let start = if frameDepth > 0 then frameStarts[frameDepth - 1] else 0
        struct (depBuffer, versionBuffer, start, count - start)

/// <summary>
/// Internal. A node that depends on other adaptive objects. It can be stored
/// as a parent in the edge list of its dependencies.
/// </summary>
type internal IAdaptiveNode =
    /// Marks this node dirty. Called by a dependency when the dependency changes.
    abstract member MarkDirty: unit -> unit
    /// Writes the position of this node in the parents array of the dependency
    /// at <paramref name="depIndex"/>. Called when edges are added and when a
    /// swap-pop removal moves this node. A <paramref name="depIndex"/> of -1
    /// means the parent has no dependency list (an observation sink).
    abstract member SetDepSlot: depIndex: int * parentIndex: int -> unit
    /// Called when this node gains its first parent.
    abstract member OnFirstParent: unit -> unit
    /// Called when this node loses its last parent.
    abstract member OnLastParent: unit -> unit

/// <summary>
/// Internal. An object that can hold parents: the target of an edge.
/// </summary>
type internal IEdgeTarget =
    /// The number of parents currently stored.
    abstract member EdgeCount: int
    /// Appends <paramref name="parent"/> and returns its index in the parents
    /// array. <paramref name="depIndex"/> is the position of this object in the
    /// dependency list of the parent (-1 for observation sinks).
    abstract member AddEdge: parent: IAdaptiveNode * depIndex: int -> int
    /// Removes the edge at <paramref name="index"/> with swap-pop. O(1).
    abstract member RemoveEdgeAt: index: int -> unit

/// <summary>
/// Internal. Edge storage of one object: the parents that depend on it, and for
/// each parent the position of this object in the dependency list of that parent.
/// Removal is swap-pop with slot fixup on the moved parent. O(1), no allocation
/// except array growth.
/// </summary>
type internal ParentEdges() =
    let mutable parents: IAdaptiveNode[] = Array.empty
    let mutable slots: int[] = Array.empty
    let mutable count = 0

    member _.Count = count

    member _.Item
        with get (index: int) = parents[index]

    member _.Add(parent: IAdaptiveNode, depIndex: int) : int =
        if count = parents.Length then
            let newLength = if parents.Length = 0 then 4 else parents.Length * 2
            let nextParents = Array.zeroCreate newLength
            let nextSlots = Array.zeroCreate newLength
            Array.Copy(parents, nextParents, parents.Length)
            Array.Copy(slots, nextSlots, slots.Length)
            parents <- nextParents
            slots <- nextSlots

        parents[count] <- parent
        slots[count] <- depIndex
        count <- count + 1
        count - 1

    member _.RemoveAt(index: int) =
        count <- count - 1

        if index < count then
            // Move the last entry into the removed position and fix its slot.
            let moved = parents[count]
            let movedSlot = slots[count]
            parents[index] <- moved
            slots[index] <- movedSlot
            parents[count] <- Unchecked.defaultof<IAdaptiveNode>
            slots[count] <- 0
            moved.SetDepSlot(movedSlot, index)
        else
            parents[count] <- Unchecked.defaultof<IAdaptiveNode>
            slots[count] <- 0

    member _.Clear() =
        if count > 0 then
            Array.Clear(parents, 0, count)
            Array.Clear(slots, 0, count)
            count <- 0

/// <summary>
/// Internal. Holds all mutable runtime state of one adaptive graph.
/// </summary>
/// <remarks>
/// The graph is confined to its owner thread: every operation must run on the
/// thread that created the context. The core contains no locks. In debug builds
/// an access from a foreign thread throws.
/// </remarks>
type internal GraphContext() =
    let mutable evaluationId = 0L
    let mutable evaluationDepth = 0
    // Incremented on every applied write. Invalidates per-evaluation dirty caches
    // when a write lands in the middle of an evaluation.
    let mutable writeGeneration = 0L
    let collector = DependencyCollector()
    let mutable collectorActive = false
    let txBuffer = TransactionBuffer()
    let mutable txActive = false
    // DEBUG only: thread id of the thread inside graph operations, plus a claim
    // depth for nested operations. 0 = idle.
    let mutable debugActiveThread = 0
    let mutable debugClaimDepth = 0
    // Pooled marking stack for iterative dirty propagation.
    let mutable markStack: IAdaptiveNode[] = Array.zeroCreate 64
    let mutable markCount = 0
    // Pooled notification queue. Delivered after a batch or a non-batched write.
    let mutable notifications: INotifiable[] = Array.zeroCreate 16
    let mutable notifyCount = 0

    static let defaultContext = GraphContext()

    /// The context of the ambient (default) graph.
    static member internal Default = defaultContext

    member internal _.EvaluationId = evaluationId
    member internal _.WriteGeneration = writeGeneration

    /// Debug builds only: claim the graph for the current thread at the start of
    /// an operation. Throws when another thread is inside a graph operation.
    /// Sequential use from different threads is allowed: the claim is released
    /// when the outermost operation ends. Pair every call with ReleaseOwner.
    [<Conditional("DEBUG")>]
    member internal _.ClaimOwner() =
        let tid = Environment.CurrentManagedThreadId

        if debugActiveThread = 0 then
            debugActiveThread <- tid
        elif debugActiveThread <> tid then
            invalidOp
                "Adaptive graph accessed concurrently from two threads. A graph is confined to one thread at a time; cross-thread changes must be posted to the owner thread."

        debugClaimDepth <- debugClaimDepth + 1

    /// Debug builds only: release one claim of ClaimOwner at the end of an operation.
    [<Conditional("DEBUG")>]
    member internal _.ReleaseOwner() =
        debugClaimDepth <- debugClaimDepth - 1

        if debugClaimDepth = 0 then
            debugActiveThread <- 0

    member internal this.EnterEvaluation() =
        this.ClaimOwner()

        if evaluationDepth = 0 then
            evaluationId <- evaluationId + 1L

        evaluationDepth <- evaluationDepth + 1

    member internal this.ExitEvaluation() =
        evaluationDepth <- evaluationDepth - 1
        this.ReleaseOwner()

    member internal _.Collector = collector

    member internal _.CollectorActive
        with get () = collectorActive
        and set value = collectorActive <- value

    member internal _.TxBuffer = txBuffer

    member internal _.TxActive
        with get () = txActive
        and set value = txActive <- value

    /// Push one node onto the marking stack. Amortized O(1), array growth only.
    member internal _.PushDirty(node: IAdaptiveNode) =
        if markCount = markStack.Length then
            let next = Array.zeroCreate (markStack.Length * 2)
            Array.Copy(markStack, next, markStack.Length)
            markStack <- next

        markStack[markCount] <- node
        markCount <- markCount + 1

    /// Drain the marking stack. Iterative: no recursion.
    member internal this.PropagateDirty() =
        while markCount > 0 do
            markCount <- markCount - 1
            let node = markStack[markCount]
            markStack[markCount] <- Unchecked.defaultof<IAdaptiveNode>
            node.MarkDirty()

    /// Mark every parent in the edge list and propagate. Delivers queued
    /// notifications when no transaction is running.
    member internal this.MarkFrom(edges: ParentEdges) =
        writeGeneration <- writeGeneration + 1L

        for i in 0 .. edges.Count - 1 do
            this.PushDirty(edges[i])

        this.PropagateDirty()

        if not txActive then
            this.DeliverNotifications()

    /// Queue one notification sink for delivery.
    member internal _.EnqueueNotification(sink: INotifiable) =
        if notifyCount = notifications.Length then
            let next = Array.zeroCreate (notifications.Length * 2)
            Array.Copy(notifications, next, notifications.Length)
            notifications <- next

        notifications[notifyCount] <- sink
        notifyCount <- notifyCount + 1

    /// Deliver every queued notification. Notifications queued during delivery
    /// are delivered in the same drain.
    member internal _.DeliverNotifications() =
        while notifyCount > 0 do
            notifyCount <- notifyCount - 1
            let sink = notifications[notifyCount]
            notifications[notifyCount] <- Unchecked.defaultof<INotifiable>
            sink.Deliver()

module internal AdaptiveRuntime =
    let internal getEvaluationId () = GraphContext.Default.EvaluationId
    let internal getWriteGeneration () = GraphContext.Default.WriteGeneration

    let internal enterEvaluation () = GraphContext.Default.EnterEvaluation()
    let internal exitEvaluation () = GraphContext.Default.ExitEvaluation()

    /// Add a dependency with its current committed version.
    let internal addDependency (dep: IAdaptiveObject) (version: int64) =
        let ctx = GraphContext.Default

        if ctx.CollectorActive then
            ctx.Collector.Add(dep, version)

    /// Collect dependencies during evaluation. Returns struct tuple to avoid heap allocation.
    let internal collect (f: unit -> 'T) =
        let ctx = GraphContext.Default
        let collector = ctx.Collector

        if not ctx.CollectorActive then
            collector.Reset()
            ctx.CollectorActive <- true

        collector.PushFrame()

        try
            let value = f ()
            let struct (deps, versions, start, len) = collector.CurrentFrame()
            struct (value, deps, versions, start, len)
        finally
            if collector.PopFrame() = 0 then
                ctx.CollectorActive <- false

module Transaction =
    /// <summary>
    /// Runs a function as a transaction. Writes inside the transaction are deferred
    /// and applied at commit. Nested calls join the running transaction.
    /// Reads inside a transaction see the pre-transaction values.
    /// Notifications are delivered after the outermost transaction commits.
    /// </summary>
    /// <example>
    /// <code>
    /// Transaction.run (fun () ->
    ///     a.Set(1)
    ///     b.Set(2)) |> ignore
    /// </code>
    /// </example>
    let run (f: unit -> 'T) =
        let ctx = GraphContext.Default
        ctx.ClaimOwner()

        try
            if ctx.TxActive then
                f ()
            else
                ctx.TxActive <- true
                ctx.TxBuffer.Reset()
                let mutable committed = false

                let result =
                    try
                        let value = f ()
                        ctx.TxBuffer.Commit()
                        committed <- true
                        value
                    finally
                        if not committed then
                            ctx.TxBuffer.Abort()

                        ctx.TxActive <- false
                // The transaction is closed: notifications see a consistent graph,
                // and callbacks that write apply directly.
                ctx.DeliverNotifications()
                result
        finally
            ctx.ReleaseOwner()


type ConstantValue<'T>(value: 'T) =
    interface IAdaptiveValue<'T> with
        member this.GetValue() =
            AdaptiveRuntime.addDependency (this :> IAdaptiveObject) 0L
            value

        member _.Version = 0L

and AdaptiveNode<'T>([<InlineIfLambda>] compute: unit -> 'T) =
    let mutable version = 0L
    let mutable hasValue = false
    let mutable value = Unchecked.defaultof<'T>
    let mutable deps: IAdaptiveObject[] = Array.empty
    let mutable depVersions: int64[] = Array.empty
    // Position of this node in the parents array of each dependency. -1 = no edge.
    let mutable depSlots: int[] = Array.empty
    let mutable arraysFromPool = false
    let mutable depCount = 0
    let edges = ParentEdges()
    let mutable dirtyState = DirtyState.MaybeDirty
    // Per-evaluation dirty cache to avoid O(depth^2) in deep chains
    let mutable lastCheckedEvalId = 0L
    let mutable lastCheckedWriteGen = -1L
    let mutable dirtyCache = true

    /// Check if dirty, using per-evaluation cache to avoid redundant deep traversals
    member private this.IsDirty() =
        let evalId = AdaptiveRuntime.getEvaluationId ()
        let writeGen = AdaptiveRuntime.getWriteGeneration ()

        if lastCheckedEvalId = evalId && lastCheckedWriteGen = writeGen then
            // Already checked in this evaluation, return cached result
            dirtyCache
        else
            // Compute dirty status and cache it
            let dirty =
                if not hasValue then
                    true
                elif dirtyState = DirtyState.Dirty then
                    // Marked by a dependency change.
                    true
                elif dirtyState = DirtyState.Clean && edges.Count > 0 then
                    // Observed and not marked: one flag check, no version reads.
                    false
                else
                    // Unobserved, or links can be stale: version check.
                    let mutable d = false
                    let mutable i = 0

                    while not d && i < depCount do
                        if deps[i].Version <> depVersions[i] then
                            d <- true

                        i <- i + 1

                    d

            lastCheckedEvalId <- evalId
            lastCheckedWriteGen <- writeGen
            dirtyCache <- dirty
            dirty

    /// Remove every edge from the stored dependencies to this node. O(depCount).
    member private this.TearDownEdges() =
        for j in 0 .. depCount - 1 do
            if depSlots[j] >= 0 then
                (deps[j] :?> IEdgeTarget).RemoveEdgeAt(depSlots[j])
                depSlots[j] <- -1

    /// Add an edge from every stored dependency to this node. O(depCount).
    member private this.BuildEdges() =
        for j in 0 .. depCount - 1 do
            depSlots[j] <-
                match deps[j] with
                | :? IEdgeTarget as target -> target.AddEdge(this, j)
                | _ -> -1

    /// Registration cascade: this node gained its first parent.
    member private this.RegisterWithDeps() =
        this.BuildEdges()
        // Links can be stale; the next read must version-check.
        dirtyState <- DirtyState.MaybeDirty

    /// Unregistration cascade: this node lost its last parent.
    member private this.UnregisterFromDeps() =
        this.TearDownEdges()
        dirtyState <- DirtyState.MaybeDirty

    member private this.Recompute() =
        let observed = edges.Count > 0

        let struct (newValue, newDeps, newVersions, newStart, newLen) =
            AdaptiveRuntime.collect compute

        value <- newValue

        // Compare the new dependency set with the stored set (same order, by reference).
        let mutable sameSet = newLen = depCount
        let mutable i = 0

        while sameSet && i < newLen do
            if not (obj.ReferenceEquals(newDeps[newStart + i], deps[i])) then
                sameSet <- false

            i <- i + 1

        if observed && not sameSet then
            this.TearDownEdges()

        // Store the new dependency set and the version snapshots.
        if deps.Length < newLen then
            if arraysFromPool && deps.Length > 0 then
                ArrayPool<IAdaptiveObject>.Shared.Return(deps, true)
                ArrayPool<int64>.Shared.Return(depVersions, true)
                ArrayPool<int>.Shared.Return(depSlots, true)

            deps <- ArrayPool<IAdaptiveObject>.Shared.Rent(newLen)
            depVersions <- ArrayPool<int64>.Shared.Rent(newLen)
            depSlots <- ArrayPool<int>.Shared.Rent(newLen)
            arraysFromPool <- true

        Array.Copy(newDeps, newStart, deps, 0, newLen)
        Array.Copy(newVersions, newStart, depVersions, 0, newLen)

        if depCount > newLen then
            Array.Clear(deps, newLen, depCount - newLen)

        depCount <- newLen

        if observed && not sameSet then
            this.BuildEdges()

        hasValue <- true
        version <- version + 1L
        dirtyState <- if observed then DirtyState.Clean else DirtyState.MaybeDirty
        // Update cache: we just recomputed, so we're not dirty anymore for this evaluation
        dirtyCache <- false

    interface IAdaptiveValue<'T> with
        member this.GetValue() =
            AdaptiveRuntime.enterEvaluation ()

            try
                if this.IsDirty() then
                    this.Recompute()
                // Add dependency with committed version AFTER any recompute
                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) version
                value
            finally
                AdaptiveRuntime.exitEvaluation ()

        member this.Version =
            AdaptiveRuntime.enterEvaluation ()

            try
                if this.IsDirty() then version + 1L else version
            finally
                AdaptiveRuntime.exitEvaluation ()

    interface IAdaptiveNode with
        member this.MarkDirty() =
            if dirtyState <> DirtyState.Dirty then
                dirtyState <- DirtyState.Dirty
                // Invalidate the per-evaluation dirty cache: a mark can arrive in the
                // middle of an evaluation (a write from user code inside a compute).
                lastCheckedEvalId <- -1L
                let ctx = GraphContext.Default

                for i in 0 .. edges.Count - 1 do
                    ctx.PushDirty(edges[i])

        member _.SetDepSlot(depIndex: int, parentIndex: int) = depSlots[depIndex] <- parentIndex

        member this.OnFirstParent() = this.RegisterWithDeps()
        member this.OnLastParent() = this.UnregisterFromDeps()

    interface IEdgeTarget with
        member _.EdgeCount = edges.Count

        member this.AddEdge(parent: IAdaptiveNode, depIndex: int) =
            let index = edges.Add(parent, depIndex)

            if edges.Count = 1 then
                this.RegisterWithDeps()

            index

        member this.RemoveEdgeAt(index: int) =
            edges.RemoveAt(index)

            if edges.Count = 0 then
                this.UnregisterFromDeps()

and ChangeableValue<'T>(initial: 'T) =
    let mutable value = initial
    let mutable version = 0L
    let edges = ParentEdges()
    // Pending slot for writes inside a transaction. Last write wins.
    let mutable hasPending = false
    let mutable pendingValue = Unchecked.defaultof<'T>

    member internal this.Apply(newValue: 'T) =
        let ctx = GraphContext.Default
        ctx.ClaimOwner()

        try
            // Equality at the source: a write that changes nothing does nothing.
            if not (EqualityComparer<'T>.Default.Equals(value, newValue)) then
                value <- newValue
                version <- version + 1L
                ctx.MarkFrom(edges)
        finally
            ctx.ReleaseOwner()

    member internal this.ApplyPending() =
        let newValue = pendingValue
        hasPending <- false
        pendingValue <- Unchecked.defaultof<'T>
        this.Apply(newValue)

    member internal _.AbortPending() =
        hasPending <- false
        pendingValue <- Unchecked.defaultof<'T>

    member this.Set(newValue: 'T) =
        let ctx = GraphContext.Default

        if ctx.TxActive then
            pendingValue <- newValue

            if not hasPending then
                hasPending <- true
                ctx.TxBuffer.Enqueue(this :> ICommit)
        else
            this.Apply(newValue)

    interface IAdaptiveValue<'T> with
        member this.GetValue() =
            let ctx = GraphContext.Default
            ctx.ClaimOwner()

            try
                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) version
                value
            finally
                ctx.ReleaseOwner()

        member _.Version = version

    interface IEdgeTarget with
        member _.EdgeCount = edges.Count
        member _.AddEdge(parent: IAdaptiveNode, depIndex: int) = edges.Add(parent, depIndex)
        member _.RemoveEdgeAt(index: int) = edges.RemoveAt(index)

    interface ICommit with
        member this.Commit() = this.ApplyPending()
        member this.Abort() = this.AbortPending()

and MapNNode<'T, 'U>(deps: IAdaptiveValue<'T>[], [<InlineIfLambda>] compute: 'T[] -> 'U) =
    let mutable version = 0L
    let mutable hasValue = false
    let mutable value = Unchecked.defaultof<'U>
    // Node-owned buffer, reused across recomputes. The compute function must not retain it.
    let values = Array.zeroCreate deps.Length
    let depVersions = Array.zeroCreate<int64> deps.Length
    // Position of this node in the parents array of each dependency. -1 = no edge.
    let depSlots = Array.create deps.Length -1
    let edges = ParentEdges()
    let mutable dirtyState = DirtyState.MaybeDirty

    member private this.IsDirty() =
        if not hasValue then
            true
        elif dirtyState = DirtyState.Dirty then
            // Marked by a dependency change.
            true
        elif dirtyState = DirtyState.Clean && edges.Count > 0 then
            // Observed and not marked: one flag check, no version reads.
            false
        else
            // Unobserved, or links can be stale: version check.
            let mutable dirty = false
            let mutable i = 0

            while not dirty && i < deps.Length do
                if (deps.[i] :> IAdaptiveObject).Version <> depVersions.[i] then
                    dirty <- true

                i <- i + 1

            dirty

    member private this.Recompute() =
        for i in 0 .. deps.Length - 1 do
            values.[i] <- deps.[i].GetValue()
            depVersions.[i] <- (deps.[i] :> IAdaptiveObject).Version

        value <- compute values
        hasValue <- true
        version <- version + 1L

        dirtyState <-
            if edges.Count > 0 then
                DirtyState.Clean
            else
                DirtyState.MaybeDirty

    /// Registration cascade: this node gained its first parent.
    member private this.RegisterWithDeps() =
        for j in 0 .. deps.Length - 1 do
            depSlots[j] <-
                match deps[j] with
                | :? IEdgeTarget as target -> target.AddEdge(this, j)
                | _ -> -1

        dirtyState <- DirtyState.MaybeDirty

    /// Unregistration cascade: this node lost its last parent.
    member private this.UnregisterFromDeps() =
        for j in 0 .. deps.Length - 1 do
            if depSlots[j] >= 0 then
                (deps[j] :?> IEdgeTarget).RemoveEdgeAt(depSlots[j])
                depSlots[j] <- -1

        dirtyState <- DirtyState.MaybeDirty

    interface IAdaptiveValue<'U> with
        member this.GetValue() =
            AdaptiveRuntime.enterEvaluation ()

            try
                if this.IsDirty() then
                    this.Recompute()

                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) version
                value
            finally
                AdaptiveRuntime.exitEvaluation ()

        member this.Version =
            AdaptiveRuntime.enterEvaluation ()

            try
                if this.IsDirty() then version + 1L else version
            finally
                AdaptiveRuntime.exitEvaluation ()

    interface IAdaptiveNode with
        member this.MarkDirty() =
            if dirtyState <> DirtyState.Dirty then
                dirtyState <- DirtyState.Dirty
                let ctx = GraphContext.Default

                for i in 0 .. edges.Count - 1 do
                    ctx.PushDirty(edges[i])

        member _.SetDepSlot(depIndex: int, parentIndex: int) = depSlots[depIndex] <- parentIndex

        member this.OnFirstParent() = this.RegisterWithDeps()
        member this.OnLastParent() = this.UnregisterFromDeps()

    interface IEdgeTarget with
        member _.EdgeCount = edges.Count

        member this.AddEdge(parent: IAdaptiveNode, depIndex: int) =
            let index = edges.Add(parent, depIndex)

            if edges.Count = 1 then
                this.RegisterWithDeps()

            index

        member this.RemoveEdgeAt(index: int) =
            edges.RemoveAt(index)

            if edges.Count = 0 then
                this.UnregisterFromDeps()

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
and ReduceNode<'T>(deps: IAdaptiveValue<'T>[], init: 'T, [<InlineIfLambda>] reduce: 'T -> 'T -> 'T) =
    let mutable version = 0L
    let mutable hasValue = false
    let mutable value = Unchecked.defaultof<'T>
    let depVersions = Array.zeroCreate<int64> deps.Length
    // Position of this node in the parents array of each dependency. -1 = no edge.
    let depSlots = Array.create deps.Length -1
    let edges = ParentEdges()
    let mutable dirtyState = DirtyState.MaybeDirty

    member private this.IsDirty() =
        if not hasValue then
            true
        elif dirtyState = DirtyState.Dirty then
            // Marked by a dependency change.
            true
        elif dirtyState = DirtyState.Clean && edges.Count > 0 then
            // Observed and not marked: one flag check, no version reads.
            false
        else
            // Unobserved, or links can be stale: version check.
            let mutable dirty = false
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

        dirtyState <-
            if edges.Count > 0 then
                DirtyState.Clean
            else
                DirtyState.MaybeDirty

    /// Registration cascade: this node gained its first parent.
    member private this.RegisterWithDeps() =
        for j in 0 .. deps.Length - 1 do
            depSlots[j] <-
                match deps[j] with
                | :? IEdgeTarget as target -> target.AddEdge(this, j)
                | _ -> -1

        dirtyState <- DirtyState.MaybeDirty

    /// Unregistration cascade: this node lost its last parent.
    member private this.UnregisterFromDeps() =
        for j in 0 .. deps.Length - 1 do
            if depSlots[j] >= 0 then
                (deps[j] :?> IEdgeTarget).RemoveEdgeAt(depSlots[j])
                depSlots[j] <- -1

        dirtyState <- DirtyState.MaybeDirty

    interface IAdaptiveValue<'T> with
        member this.GetValue() =
            AdaptiveRuntime.enterEvaluation ()

            try
                if this.IsDirty() then
                    this.Recompute()

                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) version
                value
            finally
                AdaptiveRuntime.exitEvaluation ()

        member this.Version =
            AdaptiveRuntime.enterEvaluation ()

            try
                if this.IsDirty() then version + 1L else version
            finally
                AdaptiveRuntime.exitEvaluation ()

    interface IAdaptiveNode with
        member this.MarkDirty() =
            if dirtyState <> DirtyState.Dirty then
                dirtyState <- DirtyState.Dirty
                let ctx = GraphContext.Default

                for i in 0 .. edges.Count - 1 do
                    ctx.PushDirty(edges[i])

        member _.SetDepSlot(depIndex: int, parentIndex: int) = depSlots[depIndex] <- parentIndex

        member this.OnFirstParent() = this.RegisterWithDeps()
        member this.OnLastParent() = this.UnregisterFromDeps()

    interface IEdgeTarget with
        member _.EdgeCount = edges.Count

        member this.AddEdge(parent: IAdaptiveNode, depIndex: int) =
            let index = edges.Add(parent, depIndex)

            if edges.Count = 1 then
                this.RegisterWithDeps()

            index

        member this.RemoveEdgeAt(index: int) =
            edges.RemoveAt(index)

            if edges.Count = 0 then
                this.UnregisterFromDeps()

/// <summary>
/// Internal. An active observation of an adaptive value. Registered as a parent
/// of the observed object. Marking enqueues it once per batch; delivery pulls the
/// current value and invokes the callback when the version changed.
/// </summary>
type internal Observation<'T>(target: IAdaptiveValue<'T>, callback: 'T -> unit) as this =
    let mutable active = true
    let mutable enqueued = false
    let mutable indexInTarget = -1
    let mutable lastVersion = -1L

    /// Force the initial read and register this observation as a parent.
    member internal _.Attach() =
        // Materialize the dependency subgraph before the cascade registers it.
        let _ = target.GetValue()

        match target with
        | :? IEdgeTarget as edgeTarget ->
            lastVersion <- (target :> IAdaptiveObject).Version
            indexInTarget <- edgeTarget.AddEdge(this :> IAdaptiveNode, -1)
        | _ -> ()

    interface IAdaptiveNode with
        member this.MarkDirty() =
            if active && not enqueued then
                enqueued <- true
                GraphContext.Default.EnqueueNotification(this :> INotifiable)

        member _.SetDepSlot(depIndex: int, parentIndex: int) =
            if depIndex = -1 then
                indexInTarget <- parentIndex

        member _.OnFirstParent() = ()
        member _.OnLastParent() = ()

    interface INotifiable with
        member this.Deliver() =
            enqueued <- false

            if active then
                let newValue = target.GetValue()
                let newVersion = (target :> IAdaptiveObject).Version

                if newVersion <> lastVersion then
                    lastVersion <- newVersion
                    callback newValue

    interface IObservation with
        member _.IsActive = active

        member this.Dispose() =
            if active then
                active <- false

                match target with
                | :? IEdgeTarget as edgeTarget -> edgeTarget.RemoveEdgeAt(indexInTarget)
                | _ -> ()

type ChangeableSet<'T when 'T: comparison>(initial: Set<'T>) =
    let mutable version = 0L
    let mutable data = HashSet<'T>(initial.Count)
    let mutable snapshot = initial
    let mutable snapshotVersion = -1L
    let edges = ParentEdges()
    let mutable sinks: obj[] = Array.zeroCreate 4
    let mutable sinkCount = 0
    let mutable journalAdds: 'T[] = ArrayPool<'T>.Shared.Rent 16
    let mutable journalAddCount = 0
    let mutable journalRems: 'T[] = ArrayPool<'T>.Shared.Rent 16
    let mutable journalRemCount = 0
    let mutable flushEnqueued = false
    // Pending full-replace for Set inside a transaction. Last write wins.
    let mutable pendingValue: Set<'T> voption = ValueNone

    do
        for item in initial do
            data.Add item |> ignore

    member private _.InvalidateSnapshot() = snapshotVersion <- -1L

    member internal this.AddSink(sink: ISetDeltaSink<'T>) =
        if sinkCount = sinks.Length then
            let next = Array.zeroCreate (sinks.Length * 2)
            Array.Copy(sinks, next, sinks.Length)
            sinks <- next

        sinks[sinkCount] <- box sink
        sinkCount <- sinkCount + 1

    member internal this.RemoveSink(sink: ISetDeltaSink<'T>) =
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

    member private this.FlushDeltas(newVersion: int64, adds: 'T[], addCount: int, rems: 'T[], remCount: int) =
        if addCount > 0 || remCount > 0 then
            let sinksSnapshot =
                if sinkCount = 0 then
                    Array.empty
                else
                    let arr = Array.zeroCreate sinkCount
                    Array.Copy(sinks, arr, sinkCount)
                    arr

            for i in 0 .. sinksSnapshot.Length - 1 do
                (unbox<ISetDeltaSink<'T>> sinksSnapshot[i]).OnDeltas(newVersion, adds, addCount, rems, remCount)

    member private this.JournalFlush() =
        flushEnqueued <- false

        if journalAddCount > 0 || journalRemCount > 0 then
            let adds = journalAdds
            let addCnt = journalAddCount
            let rems = journalRems
            let remCnt = journalRemCount
            journalAdds <- ArrayPool<'T>.Shared.Rent 16
            journalAddCount <- 0
            journalRems <- ArrayPool<'T>.Shared.Rent 16
            journalRemCount <- 0

            for i in 0 .. addCnt - 1 do
                data.Add adds[i] |> ignore

            for i in 0 .. remCnt - 1 do
                data.Remove rems[i] |> ignore

            version <- version + 1L
            this.InvalidateSnapshot()

            this.FlushDeltas(version, adds, addCnt, rems, remCnt)
            ArrayPool<'T>.Shared.Return(adds, true)
            ArrayPool<'T>.Shared.Return(rems, true)
            GraphContext.Default.MarkFrom(edges)

    member private this.ApplyAndFlush(item: 'T, isAdd: bool) =
        let mutable added = false

        if isAdd then
            added <- data.Add item
        else
            added <- data.Remove item

        if added then
            version <- version + 1L
            this.InvalidateSnapshot()

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
            GraphContext.Default.MarkFrom(edges)

    member internal this.Apply(newValue: Set<'T>) =
        let oldCount = data.Count
        let buffer = ArrayPool<'T>.Shared.Rent(max oldCount newValue.Count)
        let mutable oldIdx = 0

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
        GraphContext.Default.MarkFrom(edges)

    member this.Set(newValue: Set<'T>) =
        let ctx = GraphContext.Default
        ctx.ClaimOwner()

        try
            if ctx.TxActive then
                pendingValue <- ValueSome newValue
                // A full replace discards the journaled deltas of this batch.
                journalAddCount <- 0
                journalRemCount <- 0

                if not flushEnqueued then
                    flushEnqueued <- true
                    ctx.TxBuffer.Enqueue(this :> ICommit)
            else
                this.Apply newValue
        finally
            ctx.ReleaseOwner()

    member this.Add(item: 'T) =
        let ctx = GraphContext.Default
        ctx.ClaimOwner()

        try
            if ctx.TxActive then
                if journalAddCount = journalAdds.Length then
                    let next = ArrayPool<'T>.Shared.Rent(journalAdds.Length * 2)
                    Array.Copy(journalAdds, next, journalAdds.Length)
                    ArrayPool<'T>.Shared.Return(journalAdds, true)
                    journalAdds <- next

                journalAdds[journalAddCount] <- item
                journalAddCount <- journalAddCount + 1

                if not flushEnqueued then
                    flushEnqueued <- true
                    ctx.TxBuffer.Enqueue(this :> ICommit)
            else
                this.ApplyAndFlush(item, true)
        finally
            ctx.ReleaseOwner()

    member this.Remove(item: 'T) =
        let ctx = GraphContext.Default
        ctx.ClaimOwner()

        try
            if ctx.TxActive then
                if journalRemCount = journalRems.Length then
                    let next = ArrayPool<'T>.Shared.Rent(journalRems.Length * 2)
                    Array.Copy(journalRems, next, journalRems.Length)
                    ArrayPool<'T>.Shared.Return(journalRems, true)
                    journalRems <- next

                journalRems[journalRemCount] <- item
                journalRemCount <- journalRemCount + 1

                if not flushEnqueued then
                    flushEnqueued <- true
                    ctx.TxBuffer.Enqueue(this :> ICommit)
            else
                this.ApplyAndFlush(item, false)
        finally
            ctx.ReleaseOwner()

    interface ICommit with
        member this.Commit() =
            match pendingValue with
            | ValueSome newValue ->
                pendingValue <- ValueNone
                this.Apply newValue
            | ValueNone -> ()

            this.JournalFlush()

        member this.Abort() =
            pendingValue <- ValueNone
            journalAddCount <- 0
            journalRemCount <- 0
            flushEnqueued <- false

    interface IAdaptiveSet<'T> with
        member this.GetValue() =
            let ctx = GraphContext.Default
            ctx.ClaimOwner()

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
                ctx.ReleaseOwner()

        member _.Version = version

    interface ISetSinkRegistry with
        member this.AddSetSink(sink) =
            this.AddSink(unbox<ISetDeltaSink<'T>> sink)

        member this.RemoveSetSink(sink) =
            this.RemoveSink(unbox<ISetDeltaSink<'T>> sink)

    interface IEdgeTarget with
        member _.EdgeCount = edges.Count
        member _.AddEdge(parent: IAdaptiveNode, depIndex: int) = edges.Add(parent, depIndex)
        member _.RemoveEdgeAt(index: int) = edges.RemoveAt(index)

type ChangeableMap<'K, 'V when 'K: comparison>(initial: Map<'K, 'V>) =
    let mutable version = 0L
    let mutable data = Dictionary<'K, 'V>(initial.Count)
    let mutable snapshot = initial
    let mutable snapshotVersion = -1L
    let edges = ParentEdges()
    let mutable sinks: obj[] = Array.zeroCreate 4
    let mutable sinkCount = 0

    let mutable journalSets: struct ('K * 'V)[] =
        ArrayPool<struct ('K * 'V)>.Shared.Rent 16

    let mutable journalSetCount = 0
    let mutable journalRems: 'K[] = ArrayPool<'K>.Shared.Rent 16
    let mutable journalRemCount = 0
    let mutable flushEnqueued = false
    // Pending full-replace for Set inside a transaction. Last write wins.
    let mutable pendingValue: Map<'K, 'V> voption = ValueNone

    do
        for KeyValue(key, value) in initial do
            data.Add(key, value)

    member private _.InvalidateSnapshot() = snapshotVersion <- -1L

    member internal this.AddSink(sink: IMapDeltaSink<'K, 'V>) =
        if sinkCount = sinks.Length then
            let next = Array.zeroCreate (sinks.Length * 2)
            Array.Copy(sinks, next, sinks.Length)
            sinks <- next

        sinks[sinkCount] <- box sink
        sinkCount <- sinkCount + 1

    member internal this.RemoveSink(sink: IMapDeltaSink<'K, 'V>) =
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

    member private this.FlushDeltas
        (newVersion: int64, sets: struct ('K * 'V)[], setCount: int, rems: 'K[], remCount: int)
        =
        if setCount > 0 || remCount > 0 then
            let sinksSnapshot =
                if sinkCount = 0 then
                    Array.empty
                else
                    let arr = Array.zeroCreate sinkCount
                    Array.Copy(sinks, arr, sinkCount)
                    arr

            for i in 0 .. sinksSnapshot.Length - 1 do
                (unbox<IMapDeltaSink<'K, 'V>> sinksSnapshot[i]).OnDeltas(newVersion, sets, setCount, rems, remCount)

    member private this.JournalFlush() =
        flushEnqueued <- false

        if journalSetCount > 0 || journalRemCount > 0 then
            let sets = journalSets
            let setCnt = journalSetCount
            let rems = journalRems
            let remCnt = journalRemCount
            journalSets <- ArrayPool<struct ('K * 'V)>.Shared.Rent 16
            journalSetCount <- 0
            journalRems <- ArrayPool<'K>.Shared.Rent 16
            journalRemCount <- 0

            for i in 0 .. setCnt - 1 do
                let struct (k, v) = sets[i]
                data[k] <- v

            for i in 0 .. remCnt - 1 do
                data.Remove rems[i] |> ignore

            version <- version + 1L
            this.InvalidateSnapshot()

            this.FlushDeltas(version, sets, setCnt, rems, remCnt)
            ArrayPool<struct ('K * 'V)>.Shared.Return(sets, true)
            ArrayPool<'K>.Shared.Return(rems, true)
            GraphContext.Default.MarkFrom(edges)

    member private this.ApplyAndFlush(key: 'K, valueToSet: 'V, isRemove: bool) =
        let mutable changed = false

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
            GraphContext.Default.MarkFrom(edges)

    member internal this.Apply(newValue: Map<'K, 'V>) =
        let oldCount = data.Count
        let oldKeys = ArrayPool<'K>.Shared.Rent oldCount
        let mutable oldIdx = 0
        let newEntries = ArrayPool<struct ('K * 'V)>.Shared.Rent newValue.Count
        let mutable newIdx = 0

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

        let rems = ArrayPool<'K>.Shared.Rent oldIdx
        Array.Copy(oldKeys, rems, oldIdx)
        this.FlushDeltas(version, newEntries, newValue.Count, rems, oldIdx)
        ArrayPool<'K>.Shared.Return(oldKeys, true)
        ArrayPool<struct ('K * 'V)>.Shared.Return(newEntries, true)
        ArrayPool<'K>.Shared.Return(rems, true)
        GraphContext.Default.MarkFrom(edges)

    member this.Set(newValue: Map<'K, 'V>) =
        let ctx = GraphContext.Default
        ctx.ClaimOwner()

        try
            if ctx.TxActive then
                pendingValue <- ValueSome newValue
                // A full replace discards the journaled deltas of this batch.
                journalSetCount <- 0
                journalRemCount <- 0

                if not flushEnqueued then
                    flushEnqueued <- true
                    ctx.TxBuffer.Enqueue(this :> ICommit)
            else
                this.Apply newValue
        finally
            ctx.ReleaseOwner()

    member this.AddOrUpdate (key: 'K) (valueToSet: 'V) =
        let ctx = GraphContext.Default
        ctx.ClaimOwner()

        try
            if ctx.TxActive then
                if journalSetCount = journalSets.Length then
                    let next = ArrayPool<struct ('K * 'V)>.Shared.Rent(journalSets.Length * 2)
                    Array.Copy(journalSets, next, journalSets.Length)
                    ArrayPool<struct ('K * 'V)>.Shared.Return(journalSets, true)
                    journalSets <- next

                journalSets[journalSetCount] <- struct (key, valueToSet)
                journalSetCount <- journalSetCount + 1

                if not flushEnqueued then
                    flushEnqueued <- true
                    ctx.TxBuffer.Enqueue(this :> ICommit)
            else
                this.ApplyAndFlush(key, valueToSet, false)
        finally
            ctx.ReleaseOwner()

    member this.Remove(key: 'K) =
        let ctx = GraphContext.Default
        ctx.ClaimOwner()

        try
            if ctx.TxActive then
                if journalRemCount = journalRems.Length then
                    let next = ArrayPool<'K>.Shared.Rent(journalRems.Length * 2)
                    Array.Copy(journalRems, next, journalRems.Length)
                    ArrayPool<'K>.Shared.Return(journalRems, true)
                    journalRems <- next

                journalRems[journalRemCount] <- key
                journalRemCount <- journalRemCount + 1

                if not flushEnqueued then
                    flushEnqueued <- true
                    ctx.TxBuffer.Enqueue(this :> ICommit)
            else
                this.ApplyAndFlush(key, Unchecked.defaultof<'V>, true)
        finally
            ctx.ReleaseOwner()

    interface ICommit with
        member this.Commit() =
            match pendingValue with
            | ValueSome newValue ->
                pendingValue <- ValueNone
                this.Apply newValue
            | ValueNone -> ()

            this.JournalFlush()

        member this.Abort() =
            pendingValue <- ValueNone
            journalSetCount <- 0
            journalRemCount <- 0
            flushEnqueued <- false

    interface IAdaptiveMap<'K, 'V> with
        member this.GetValue() =
            let ctx = GraphContext.Default
            ctx.ClaimOwner()

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
                ctx.ReleaseOwner()

        member _.Version = version

    interface IMapSinkRegistry with
        member this.AddMapSink(sink) =
            this.AddSink(unbox<IMapDeltaSink<'K, 'V>> sink)

        member this.RemoveMapSink(sink) =
            this.RemoveSink(unbox<IMapDeltaSink<'K, 'V>> sink)

    interface IEdgeTarget with
        member _.EdgeCount = edges.Count
        member _.AddEdge(parent: IAdaptiveNode, depIndex: int) = edges.Add(parent, depIndex)
        member _.RemoveEdgeAt(index: int) = edges.RemoveAt(index)

/// <summary>
/// Core operations for creating and transforming adaptive values.
/// Adaptive values automatically track dependencies and recompute only when their inputs change.
/// </summary>
/// <remarks>
/// <para>
/// AdaptiveSlop provides incremental computation with a push-mark, pull-evaluate model.
/// Writes mark the dependents of a source dirty. Reads recompute marked nodes and
/// return cached values for the rest. Observed subgraphs (see <c>AVal.observe</c>)
/// get O(1) dirty checks; unobserved subgraphs fall back to version checks.
/// </para>
/// <para>
/// <strong>Performance Guidance:</strong>
/// <list type="bullet">
/// <item><description>Use <c>map</c>, <c>map2</c> for 1-2 dependencies (most common case)</description></item>
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
    /// Recomputes only when one of the three inputs changes.
    /// </summary>
    /// <param name="f">The function to combine the three values.</param>
    /// <param name="a">The first adaptive value.</param>
    /// <param name="b">The second adaptive value.</param>
    /// <param name="c">The third adaptive value.</param>
    /// <returns>A new adaptive value that combines all three inputs.</returns>
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
        AdaptiveNode(fun () -> f (a.GetValue()) (b.GetValue()) (c.GetValue()))

    /// <summary>
    /// Combines four adaptive values using a mapping function.
    /// Recomputes only when one of the four inputs changes.
    /// </summary>
    /// <param name="f">The function to combine the four values.</param>
    /// <param name="a">The first adaptive value.</param>
    /// <param name="b">The second adaptive value.</param>
    /// <param name="c">The third adaptive value.</param>
    /// <param name="d">The fourth adaptive value.</param>
    /// <returns>A new adaptive value that combines all four inputs.</returns>
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
        AdaptiveNode(fun () -> f (a.GetValue()) (b.GetValue()) (c.GetValue()) (d.GetValue()))

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
    /// <strong>Note:</strong> The array passed to the compute function is reused by the node and is valid only
    /// during the call. Do not retain it. If you only need a reduction (sum, min, max, etc.), prefer
    /// <see cref="reduce"/> for better performance.
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

    /// <summary>
    /// Observes an adaptive value. Forces an initial read and registers the callback
    /// as a parent of the value. The callback runs after a batch or a write that
    /// changed the value, and it receives the new value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The callback does not run for the initial read. It runs only for later changes.
    /// Several writes inside one transaction produce one callback.
    /// </para>
    /// <para>
    /// <strong>Memory management:</strong> Edges are strong references. Always dispose
    /// the returned observation when it is no longer needed. Otherwise the observed
    /// subgraph stays registered and keeps receiving marks.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// let count = CVal.create 0
    /// use observation = AVal.observe (fun v -> printfn "count: %d" v) (CVal.value count)
    /// count.Set(1)  // prints "count: 1"
    /// </code>
    /// </example>
    let observe (callback: 'T -> unit) (value: IAdaptiveValue<'T>) : IObservation =
        let observation = new Observation<_>(value, callback)
        observation.Attach()
        observation :> IObservation

    let inline getValueTask (value: IAdaptiveValue<'T>) = Task.FromResult(value.GetValue())

    let inline getValueValueTask (value: IAdaptiveValue<'T>) = ValueTask<'T>(value.GetValue())

module CVal =
    let inline create (value: 'T) = ChangeableValue value

    let inline set (value: 'T) (cval: ChangeableValue<'T>) = cval.Set value

    let inline value (cval: ChangeableValue<'T>) : IAdaptiveValue<'T> = cval
