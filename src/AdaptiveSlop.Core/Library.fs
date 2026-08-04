namespace AdaptiveSlop.Core

open System
open System.Buffers
open System.Collections.Generic
open System.Diagnostics
open System.Threading
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
/// Internal. Source of a posted change: applies the pending posted value.
/// </summary>
type internal IPostSource =
    abstract member ApplyPosted: unit -> unit

/// <summary>
/// Internal. A slot of the bounded post ring. Carries a sequence number for
/// synchronization between producers and the consumer.
/// </summary>
type internal PostSlot() =
    [<DefaultValue>]
    val mutable Seq: int

    [<DefaultValue>]
    val mutable Item: obj

/// <summary>
/// Internal. Bounded multi-producer, single-consumer ring buffer for cross-thread
/// posts. Preallocated. Each slot carries a sequence number; producers and the
/// consumer synchronize through it. The only Interlocked/Volatile use in the core
/// is here, inside the explicit handoff structure (PLAN.md §7.3).
/// </summary>
type internal PostRing(capacity: int) =
    let mask = capacity - 1
    let slots = Array.init capacity (fun _ -> PostSlot())

    do
        for i in 0 .. capacity - 1 do
            slots[i].Seq <- i

    // Producer claim position. The consumer position belongs to the owner thread.
    let mutable head = 0L
    let mutable tail = 0L

    /// Enqueue one item. Spins until a slot is free. Called by foreign threads.
    member this.Enqueue(item: obj) =
        let mutable h = Volatile.Read(&head)
        let mutable enqueued = false

        while not enqueued do
            let idx = int (h &&& int64 mask)
            let slot = slots[idx]
            let seq = Volatile.Read(&slot.Seq)

            if int h = seq then
                if Interlocked.CompareExchange(&head, h + 1L, h) = h then
                    slot.Item <- item
                    Volatile.Write(&slot.Seq, seq + 1)
                    enqueued <- true
            elif seq < int h then
                // The ring is full: the slot has not been consumed yet. Wait.
                Thread.SpinWait(8)
                h <- Volatile.Read(&head)
            else
                // Claimed by another producer; retry at the new head.
                h <- Volatile.Read(&head)

    /// Dequeue one item, or none when empty. Called by the owner thread only.
    /// Returns a struct voption: no allocation.
    member this.TryDequeue() : obj voption =
        let t = Volatile.Read(&tail)
        let idx = int (t &&& int64 mask)
        let slot = slots[idx]
        let seq = Volatile.Read(&slot.Seq)

        if seq = int t + 1 then
            let item = slot.Item
            slot.Item <- null
            Volatile.Write(&slot.Seq, int (t + int64 capacity))
            Volatile.Write(&tail, t + 1L)
            ValueSome item
        else
            ValueNone

    /// True when the queue is definitely empty. A post that lands after this check
    /// is simply applied by a later pump.
    member _.IsEmpty = Volatile.Read(&tail) = Volatile.Read(&head)

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
    // Bounded ring for cross-thread posts; drained by Pump on the owner thread.
    let postRing = PostRing(1024)
    let collector = DependencyCollector()
    let mutable collectorActive = false
    let txBuffer = TransactionBuffer()
    let mutable txActive = false
    // DEBUG only: thread id of the thread inside graph operations, plus a claim
    // depth for nested operations. 0 = idle.
    let mutable debugActiveThread = 0
    let mutable debugClaimDepth = 0
    // Operation nesting depth. The automatic drain fires on the outermost claim only.
    let mutable operationDepth = 0
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

    /// Claim the graph for the current operation. Throws in debug builds when
    /// another thread is inside a graph operation. Sequential use from different
    /// threads is allowed: the claim is released when the outermost operation ends.
    /// Pair every call with ReleaseOwner. On the outermost claim, pending posts are
    /// drained automatically (auto-pump): they apply as one batch with one
    /// notification delivery, before the operation runs.
    member internal this.ClaimOwner() =
#if DEBUG
        let tid = Environment.CurrentManagedThreadId

        if debugActiveThread = 0 then
            debugActiveThread <- tid
        elif debugActiveThread <> tid then
            invalidOp
                "Adaptive graph accessed concurrently from two threads. A graph is confined to one thread at a time; cross-thread changes must be posted to the owner thread."

        debugClaimDepth <- debugClaimDepth + 1
#endif
        operationDepth <- operationDepth + 1

        if operationDepth = 1 && not this.TxActive then
            this.DrainIfPending()

    /// Release one claim of ClaimOwner at the end of an operation.
    member internal this.ReleaseOwner() =
#if DEBUG
        debugClaimDepth <- debugClaimDepth - 1

        if debugClaimDepth = 0 then
            debugActiveThread <- 0
#endif
        operationDepth <- operationDepth - 1

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

    member internal _.PostRing = postRing

    /// Apply all pending posts as one batch with one notification delivery.
    /// Called automatically on the outermost claim of any graph operation, and
    /// from Pump. No-op when the ring is empty.
    member private this.DrainIfPending() =
        if not postRing.IsEmpty then
            let wasActive = this.TxActive
            this.TxActive <- true

            try
                this.DrainPosts()
            finally
                this.TxActive <- wasActive

            if not wasActive then
                this.DeliverNotifications()

    /// Drain the ring, applying each pending posted value. Owner thread only.
    member private this.DrainPosts() =
        let mutable item = postRing.TryDequeue()

        while item.IsSome do
            (unbox<IPostSource> item.Value).ApplyPosted()
            item <- postRing.TryDequeue()

    /// Apply all pending posts now. Draining is automatic at the start of every
    /// graph operation, so this is optional: use it to choose an explicit batch
    /// boundary (for example, once per frame). No-op when nothing is pending.
    member internal this.Pump() =
        this.ClaimOwner()

        try
            this.DrainIfPending()
        finally
            this.ReleaseOwner()

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

/// <summary>
/// Applies changes posted from foreign threads.
/// </summary>
/// <remarks>
/// <para>
/// A graph is confined to one owner thread: all reads and writes happen there.
/// Foreign threads may only call <c>Post</c> on changeable values. Pending posts are
/// applied automatically at the start of the next graph operation on the owner
/// thread, as one batch with one notification delivery. No pump call is required.
/// </para>
/// <para>
/// Every posted source is applied at most once per batch, with the last posted value
/// winning. The source equality check still applies, so posting an equal value does
/// not mark. <c>Posting.pump()</c> forces application at a chosen boundary and is
/// cheap when the queue is empty; it allocates nothing.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // worker thread
/// cval.Post(health - 1)
/// // owner thread: the next read applies the post automatically
/// let h = AVal.getValue health
/// </code>
/// </example>
module Posting =
    /// <summary>
    /// Applies all pending posted changes now. Optional: pending posts are applied
    /// automatically at the next graph operation. Use this to choose an explicit
    /// batch boundary (for example, once per frame).
    /// </summary>
    /// <remarks>
    /// Must be called on the owner thread.
    /// </remarks>
    let pump () = GraphContext.Default.Pump()

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

                    // Verified clean: promote to the flag-based check so later
                    // reads do not re-walk the dependency closure. A registered
                    // node that never recomputes would otherwise stay MaybeDirty
                    // forever, making every read O(subtree) via the .Version
                    // getters (measured: 16k walks per write on a 32k-node tree).
                    // Promote only when every link is complete: a torn-down link
                    // (depSlot = -1) means marks may not arrive, and the flag
                    // would go stale.
                    if not d && edges.Count > 0 then
                        let mutable complete = true
                        let mutable j = 0

                        while complete && j < depCount do
                            if depSlots[j] < 0 then
                                complete <- false

                            j <- j + 1

                        if complete then
                            dirtyState <- DirtyState.Clean

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
    // Posting: pending value written by a foreign thread, applied at Pump.
    // 0 = not queued, 1 = queued. Interlocked-managed.
    let mutable posted = 0
    let mutable postedValue = Unchecked.defaultof<'T>

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

    /// <summary>
    /// Posts a new value from any thread. The value is applied automatically at the
    /// next graph operation on the owner thread — no pump call is needed. Several
    /// posts to this source before the application collapse to the last value. The
    /// source equality check applies at application, so posting an equal value does
    /// not mark. Allocates nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Post is the only operation a foreign thread may call on a graph. It never
    /// touches graph state: it writes the typed pending field and, if the source is
    /// not queued yet, pushes the source onto the bounded post ring. If the ring is
    /// full, Post waits until the owner drains it.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // worker thread
    /// health.Post(healthValue - 1)
    /// // owner thread: the next read applies the post automatically
    /// let h = AVal.getValue health
    /// </code>
    /// </example>
    member this.Post(newValue: 'T) =
        postedValue <- newValue

        if Interlocked.CompareExchange(&posted, 1, 0) = 0 then
            GraphContext.Default.PostRing.Enqueue(this :> obj)

    /// Apply the pending posted value on the owner thread (called from Pump).
    member internal this.ApplyPostedValue() =
        // Clear the queued flag before reading: a post that lands after the clear
        // re-enqueues, so its value cannot be lost.
        Interlocked.Exchange(&posted, 0) |> ignore
        this.Apply(postedValue)

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

    interface IPostSource with
        member this.ApplyPosted() = this.ApplyPostedValue()

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

    /// <summary>
    /// Posts a new value from any thread. The value is applied automatically at the
    /// next graph operation on the owner thread. See
    /// <see cref="ChangeableValue&lt;'T&gt;.Post"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// // worker thread
    /// CVal.post (health - 1) health
    /// // owner thread: the next read applies the post automatically
    /// let h = AVal.getValue health
    /// </code>
    /// </example>
    let inline post (value: 'T) (cval: ChangeableValue<'T>) = cval.Post value

    let inline value (cval: ChangeableValue<'T>) : IAdaptiveValue<'T> = cval
