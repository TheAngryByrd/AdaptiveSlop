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
/// <strong>Threading:</strong> every thread owns its ambient graph, created
/// lazily on first use. A node belongs to the graph of the thread that
/// created it; all reads and writes on that node must run on that thread.
/// Cross-thread changes must be posted to the owner thread (the post
/// machinery), never applied directly. In debug builds a direct operation on
/// another thread's node throws.
/// </para>
/// </remarks>
type IAdaptiveValue<'T> =
    inherit IAdaptiveObject
    /// <summary>
    /// Gets the current value, recomputing if any dependencies have changed.
    /// </summary>
    /// <returns>The current computed value.</returns>
    abstract member GetValue: unit -> 'T

/// <summary>An abbreviation for <see cref="IAdaptiveValue&lt;'T&gt;"/> (FDA <c>aval&lt;'T&gt;</c> parity).</summary>
type aval<'T> = IAdaptiveValue<'T>

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
        let mutable firstEx: exn option = None

        while i < count do
            try
                buffer[i].Commit()
            with e ->
                if firstEx.IsNone then
                    firstEx <- Some e

                // Do not apply the rest of the batch; discard it.
                i <- i + 1

                while i < count do
                    buffer[i].Abort()
                    i <- i + 1

            i <- i + 1

        Array.Clear(buffer, 0, count)
        count <- 0

        match firstEx with
        | Some e -> raise e
        | None -> ()

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

    /// Clear the whole buffer. Called when the outermost evaluation ends:
    /// the collector lives on the ambient graph context of the thread, so
    /// without this the deepest evaluation's objects stay reachable through
    /// the thread-static root until a deeper evaluation overwrites the slots.
    member _.Clear() =
        if count > 0 then
            Array.Clear(depBuffer, 0, count)
            Array.Clear(versionBuffer, 0, count)

        count <- 0
        frameDepth <- 0

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
/// consumer synchronize through it. The Interlocked/Volatile use in the core is
/// confined to the explicit handoff structure (PLAN.md §7.3): this ring and the
/// per-node posted-op rings of the changeable collections (Changeable.fs).
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
        let mutable spins = 0

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
                // The ring is full: the slot has not been consumed yet. Wait
                // with bounded backoff (policy: Post blocks until the owner
                // drains; items are never dropped).
                spins <- spins + 1

                if spins >= 32 then
                    Thread.Yield() |> ignore
                    spins <- 0
                else
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
/// Every thread owns its ambient graph, created lazily on first use. A graph
/// is confined to its creating thread: every operation must run on that
/// thread. The core contains no locks; the Interlocked/Volatile use is
/// confined to the explicit handoff structure (the post rings). In debug
/// builds an access from a foreign thread throws.
/// </remarks>
[<AllowNullLiteral>]
type internal GraphContext() =
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
    // DEBUG only: the thread that created this graph. A graph is owned by its
    // creating thread; debug builds reject claims from any other thread.
    let mutable debugOwnerThread = Environment.CurrentManagedThreadId
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

    // The ambient graph of the calling thread. A thread-local pointer only:
    // all mutable state lives on the context object. Created lazily on first
    // use, so a thread that never touches the library allocates nothing.
    [<ThreadStatic>]
    [<DefaultValue>]
    static val mutable private currentContext: GraphContext

    /// The ambient graph of the calling thread, created lazily on first use.
    static member internal Current =
        match GraphContext.currentContext with
        | null ->
            let created = GraphContext()
            GraphContext.currentContext <- created
            created
        | ctx -> ctx

    /// Legacy internal name for <see cref="Current"/>: the ambient graph of
    /// the calling thread. Retained for the derived-node call sites, which
    /// legitimately resolve the ambient: every legal operation on a node runs
    /// on its creating thread, whose ambient graph is the node's graph (the
    /// debug owner check enforces this).
    static member internal Default = GraphContext.Current

    member internal _.WriteGeneration = writeGeneration
    member internal _.Collector = collector

    /// Add a dependency with its current committed version (explicit context:
    /// the scalar node hot paths pass their captured graph, avoiding the
    /// ambient resolution).
    member internal this.AddDependency(dep: IAdaptiveObject, version: int64) =
        if this.CollectorActive then
            this.Collector.Add(dep, version)

    /// Claim the graph for the current operation. Throws in debug builds when
    /// the calling thread is not the thread that created this graph, or when
    /// another thread is inside a graph operation. Pair every call with
    /// ReleaseOwner. On the outermost claim, pending posts are drained
    /// automatically (auto-pump): they apply as one batch with one
    /// notification delivery, before the operation runs.
    member internal this.ClaimOwner() =
#if DEBUG
        let tid = Environment.CurrentManagedThreadId

        if debugOwnerThread <> tid then
            invalidOp
                "This node belongs to the adaptive graph of another thread. Each thread owns its own graph; cross-thread changes must be posted to the owner thread."

        if debugActiveThread = 0 then
            debugActiveThread <- tid
        elif debugActiveThread <> tid then
            invalidOp
                "Adaptive graph accessed concurrently from two threads. A graph is confined to one thread at a time; cross-thread changes must be posted to the owner thread."

        debugClaimDepth <- debugClaimDepth + 1
#endif
        operationDepth <- operationDepth + 1

        if operationDepth = 1 && not this.TxActive then
            try
                this.DrainIfPending()
            with _ ->
                // The drain failed (a posted op threw, for example an invalid
                // posted list position). Unwind this claim before propagating
                // so the caller's ReleaseOwner in its finally cannot underflow
                // the depth counters; the graph stays usable.
                this.ReleaseOwner()
                reraise ()

    /// Release one claim of ClaimOwner at the end of an operation. A release
    /// past zero is a no-op: it happens when ClaimOwner unwound its own claim
    /// before rethrowing a drain failure (the caller's finally still runs).
    member internal this.ReleaseOwner() =
#if DEBUG
        if debugClaimDepth > 0 then
            debugClaimDepth <- debugClaimDepth - 1

        if debugClaimDepth = 0 then
            debugActiveThread <- 0
#endif
        if operationDepth > 0 then
            operationDepth <- operationDepth - 1

    member internal this.EnterEvaluation() =
        this.ClaimOwner()
        evaluationDepth <- evaluationDepth + 1

    member internal this.ExitEvaluation() =
        evaluationDepth <- evaluationDepth - 1
        this.ReleaseOwner()

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
    /// are delivered in the same drain. A throwing callback is isolated: the
    /// rest of the queue still drains, and the first exception is rethrown
    /// after the drain. A callback that enqueues without bound (a write in a
    /// callback that observes the written value) is cut off by a depth limit
    /// (policy: callbacks must not write the values they observe).
    member internal this.DeliverNotifications() =
        let mutable delivered = 0
        let mutable firstEx: exn option = None

        while notifyCount > 0 do
            if delivered >= 10000 then
                failwith
                    "AdaptiveSlop: notification delivery exceeded 10000 rounds. A callback keeps writing an observed value (infinite notification loop)."

            delivered <- delivered + 1
            notifyCount <- notifyCount - 1
            let sink = notifications[notifyCount]
            notifications[notifyCount] <- Unchecked.defaultof<INotifiable>

            try
                sink.Deliver()
            with e ->
                if firstEx.IsNone then
                    firstEx <- Some e

        match firstEx with
        | Some e -> raise e
        | None -> ()

module internal AdaptiveRuntime =
    // The runtime functions take the graph context explicitly: node methods
    // pass their captured context (a field read), so the per-node hot paths
    // never re-resolve the ambient graph (a ThreadStatic read).
    let inline getWriteGeneration (ctx: GraphContext) = ctx.WriteGeneration

    let inline enterEvaluation (ctx: GraphContext) = ctx.EnterEvaluation()
    let inline exitEvaluation (ctx: GraphContext) = ctx.ExitEvaluation()

    /// Add a dependency with its current committed version. Resolves the
    /// ambient graph (one ThreadStatic read per call); used by the collection
    /// nodes, whose read paths are dominated by delta machinery. The scalar
    /// node hot paths use the explicit-context member
    /// <see cref="GraphContext.AddDependency"/> instead.
    let inline addDependency (dep: IAdaptiveObject) (version: int64) =
        let ctx = GraphContext.Current

        if ctx.CollectorActive then
            ctx.Collector.Add(dep, version)

    /// Collect dependencies during evaluation. Returns struct tuple to avoid heap allocation.
    let inline collect (ctx: GraphContext) (f: unit -> 'T) =
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
                // Drop references to the evaluation's dependencies: the
                // collector is reachable from the static default context.
                collector.Clear()

/// <summary>
/// Applies changes posted from foreign threads.
/// </summary>
/// <remarks>
/// <para>
/// Each thread owns its ambient graph (created lazily on first use); the
/// owner thread of a graph is the thread that created it. All reads and
/// writes happen there. Foreign threads may only call <c>Post</c> on
/// changeable values (or the collection post functions); the post lands in
/// the node's own graph ring. Pending posts are applied automatically at the
/// start of the next graph operation on the owner thread, as one batch with
/// one notification delivery. No pump call is required.
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
    let pump () = GraphContext.Current.Pump()

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
        let ctx = GraphContext.Current
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
    // An edge target with a real edge list: AdaptiveNode promotes to the
    // flag-based dirty check only when every dependency link is complete
    // (depSlot >= 0). Constants must therefore be edge targets; their version
    // never changes, so no mark ever fires and the edges are never used.
    let edges = ParentEdges()

    interface IAdaptiveValue<'T> with
        member this.GetValue() =
            AdaptiveRuntime.addDependency (this :> IAdaptiveObject) 0L
            value

        member _.Version = 0L

    interface IEdgeTarget with
        member _.EdgeCount = edges.Count
        member _.AddEdge(parent: IAdaptiveNode, depIndex: int) = edges.Add(parent, depIndex)
        member _.RemoveEdgeAt(index: int) = edges.RemoveAt(index)

type LazyConstantValue<'T>([<InlineIfLambda>] create: unit -> 'T) =
    let mutable computed = false
    let mutable value = Unchecked.defaultof<'T>
    // See ConstantValue: an edge target so dependents can promote to Clean.
    let edges = ParentEdges()

    interface IAdaptiveValue<'T> with
        member this.GetValue() =
            AdaptiveRuntime.addDependency (this :> IAdaptiveObject) 0L

            if computed then
                value
            else
                let v = create ()
                value <- v
                computed <- true
                v

        member _.Version = 0L

    interface IEdgeTarget with
        member _.EdgeCount = edges.Count
        member _.AddEdge(parent: IAdaptiveNode, depIndex: int) = edges.Add(parent, depIndex)
        member _.RemoveEdgeAt(index: int) = edges.RemoveAt(index)

type AdaptiveNode<'T>([<InlineIfLambda>] compute: unit -> 'T) =
    // The graph this node belongs to, captured at creation (the ambient graph
    // of the creating thread).
    let ctx = GraphContext.Current
    let mutable version = 0L
    let mutable hasValue = false
    let mutable value = Unchecked.defaultof<'T>
    let mutable deps: IAdaptiveObject[] = Array.empty
    let mutable depVersions: int64[] = Array.empty
    // Position of this node in the parents array of each dependency. -1 = no edge.
    let mutable depSlots: int[] = Array.empty
    let mutable depCount = 0
    let edges = ParentEdges()
    let mutable dirtyState = DirtyState.MaybeDirty
    // Write-generation-keyed dirty cache: the verdict of the last version check
    // (or recompute) stays valid until the next applied write moves the global
    // generation. Repeated reads at the same generation are O(1) per node.
    let mutable lastCheckedWriteGen = -1L
    let mutable dirtyCache = true

    /// Check if dirty, using a write-generation-keyed cache: the verdict of the
    /// last check at this generation is still valid, because any write that could
    /// affect this node moves the generation (scalar writes via MarkFrom,
    /// collection writes via sink delivery + MarkFrom).
    member private this.IsDirty() =
        let writeGen = AdaptiveRuntime.getWriteGeneration ctx

        if lastCheckedWriteGen = writeGen then
            // Already checked at this write generation: return cached result
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
        // Generation at which the recompute starts. A write from user code in the
        // middle of the compute moves the generation; the computed value would be
        // stale, so the node stays Dirty and recomputes on the next read.
        let checkedGen = AdaptiveRuntime.getWriteGeneration ctx

        let struct (newValue, newDeps, newVersions, newStart, newLen) =
            AdaptiveRuntime.collect ctx compute

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

        // Store the new dependency set and the version snapshots. Plain
        // arrays, not ArrayPool: a rented array is only returned when the node
        // grows, so a garbage-collected node leaks its rented arrays out of
        // the pool (this type has no IDisposable/finalizer). In the steady
        // state (dep count unchanged) plain arrays allocate nothing either.
        if deps.Length < newLen then
            deps <- Array.zeroCreate newLen
            depVersions <- Array.zeroCreate<int64> newLen
            depSlots <- Array.create newLen -1

        Array.Copy(newDeps, newStart, deps, 0, newLen)
        Array.Copy(newVersions, newStart, depVersions, 0, newLen)

        if depCount > newLen then
            Array.Clear(deps, newLen, depCount - newLen)

        depCount <- newLen

        if observed && not sameSet then
            this.BuildEdges()

        hasValue <- true
        version <- version + 1L

        dirtyState <-
            if AdaptiveRuntime.getWriteGeneration ctx <> checkedGen then
                // A write landed mid-compute: the value may be stale, keep Dirty.
                DirtyState.Dirty
            elif observed then
                DirtyState.Clean
            else
                DirtyState.MaybeDirty

        // The recompute is valid as of checkedGen: key the cache there so later
        // reads at the same generation skip the version check.
        lastCheckedWriteGen <- checkedGen
        dirtyCache <- false

    interface IAdaptiveValue<'T> with
        member this.GetValue() =
            AdaptiveRuntime.enterEvaluation ctx

            try
                if this.IsDirty() then
                    this.Recompute()
                // Add dependency with committed version AFTER any recompute
                ctx.AddDependency(this :> IAdaptiveObject, version)
                value
            finally
                AdaptiveRuntime.exitEvaluation ctx

        member this.Version =
            AdaptiveRuntime.enterEvaluation ctx

            try
                if this.IsDirty() then version + 1L else version
            finally
                AdaptiveRuntime.exitEvaluation ctx

    interface IAdaptiveNode with
        member this.MarkDirty() =
            if dirtyState <> DirtyState.Dirty then
                dirtyState <- DirtyState.Dirty
                // Invalidate the dirty cache: a mark can arrive in the middle of an
                // evaluation (a write from user code inside a compute). The mark is
                // precise; the cache would otherwise hide it.
                lastCheckedWriteGen <- -1L

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

type ChangeableValue<'T>(initial: 'T) =
    // The graph this node belongs to, captured at creation (the ambient graph
    // of the creating thread).
    let ctx = GraphContext.Current
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
    /// full, Post waits (spin with yield backoff) until the owner drains it; items
    /// are never dropped.
    /// </para>
    /// <para>
    /// The pending value crosses threads as a plain (non-atomic) write. 'T must be
    /// a reference type or a struct no larger than a machine word: larger structs
    /// can tear (the owner may apply a value mixed from two posts).
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
            ctx.PostRing.Enqueue(this :> obj)

    /// Apply the pending posted value on the owner thread (called from Pump).
    member internal this.ApplyPostedValue() =
        // Clear the queued flag before reading: a post that lands after the clear
        // re-enqueues, so its value cannot be lost.
        Interlocked.Exchange(&posted, 0) |> ignore
        this.Apply(postedValue)

    member this.Set(newValue: 'T) =
        if ctx.TxActive then
            pendingValue <- newValue

            if not hasPending then
                hasPending <- true
                ctx.TxBuffer.Enqueue(this :> ICommit)
        else
            this.Apply(newValue)

    /// <summary>
    /// Gets or sets the current value (FDA <c>cval.Value</c> parity). The setter
    /// routes through <see cref="Set"/>: inside a transaction the write is
    /// deferred to commit. The getter returns the raw current value; it does not
    /// register a dependency (use <see cref="GetValue"/> for that).
    /// </summary>
    member this.Value
        with get () = value
        and set newValue = this.Set newValue

    /// <summary>
    /// Gets the current value and registers a dependency for the calling
    /// computation (FDA <c>cval.GetValue</c> parity; no token here).
    /// </summary>
    member this.GetValue() = (this :> IAdaptiveValue<_>).GetValue()

    /// <summary>
    /// Sets the current value and returns whether the value changed (FDA
    /// <c>cval.UpdateTo</c> parity). A write with an equal value returns
    /// <c>false</c> and marks nothing.
    /// </summary>
    member this.UpdateTo(newValue: 'T) : bool =
        if EqualityComparer<'T>.Default.Equals(value, newValue) then
            false
        else
            this.Set newValue
            true

    interface IAdaptiveValue<'T> with
        member this.GetValue() =
            ctx.ClaimOwner()

            try
                ctx.AddDependency(this :> IAdaptiveObject, version)
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

type MapNNode<'T, 'U>(deps: IAdaptiveValue<'T>[], [<InlineIfLambda>] compute: 'T[] -> 'U) =
    // The graph this node belongs to, captured at creation (the ambient graph
    // of the creating thread).
    let ctx = GraphContext.Current
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
    // Write-generation-keyed dirty cache: the last verdict stays valid until the
    // next applied write moves the global generation.
    let mutable lastCheckedWriteGen = -1L
    let mutable dirtyCache = true

    member private this.IsDirty() =
        let writeGen = AdaptiveRuntime.getWriteGeneration ctx

        if lastCheckedWriteGen = writeGen then
            dirtyCache
        elif not hasValue then
            true
        elif dirtyState = DirtyState.Dirty then
            // Marked by a dependency change.
            true
        elif dirtyState = DirtyState.Clean && edges.Count > 0 then
            // Observed and not marked: one flag check, no version reads.
            dirtyCache <- false
            lastCheckedWriteGen <- writeGen
            false
        else
            // Unobserved, or links can be stale: version check.
            let mutable dirty = false
            let mutable i = 0

            while not dirty && i < deps.Length do
                if (deps.[i] :> IAdaptiveObject).Version <> depVersions.[i] then
                    dirty <- true

                i <- i + 1

            // Verified clean: promote to the flag-based check (as AdaptiveNode
            // does) so later reads do not re-walk the dependency closure. The
            // links are complete once every dep has a slot, so marks arrive.
            if not dirty && edges.Count > 0 then
                let mutable complete = true
                let mutable j = 0

                while complete && j < deps.Length do
                    if depSlots[j] < 0 then
                        complete <- false

                    j <- j + 1

                if complete then
                    dirtyState <- DirtyState.Clean

            lastCheckedWriteGen <- writeGen
            dirtyCache <- dirty
            dirty

    member private this.Recompute() =
        // Generation at which the recompute starts; a write mid-compute would make
        // the computed value stale, so the node stays Dirty.
        let checkedGen = AdaptiveRuntime.getWriteGeneration ctx

        for i in 0 .. deps.Length - 1 do
            values.[i] <- deps.[i].GetValue()
            depVersions.[i] <- (deps.[i] :> IAdaptiveObject).Version

        value <- compute values
        hasValue <- true
        version <- version + 1L

        dirtyState <-
            if AdaptiveRuntime.getWriteGeneration ctx <> checkedGen then
                DirtyState.Dirty
            elif edges.Count > 0 then
                DirtyState.Clean
            else
                DirtyState.MaybeDirty

        lastCheckedWriteGen <- checkedGen
        dirtyCache <- false

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
            AdaptiveRuntime.enterEvaluation ctx

            try
                if this.IsDirty() then
                    this.Recompute()

                ctx.AddDependency(this :> IAdaptiveObject, version)
                value
            finally
                AdaptiveRuntime.exitEvaluation ctx

        member this.Version =
            AdaptiveRuntime.enterEvaluation ctx

            try
                if this.IsDirty() then version + 1L else version
            finally
                AdaptiveRuntime.exitEvaluation ctx

    interface IAdaptiveNode with
        member this.MarkDirty() =
            if dirtyState <> DirtyState.Dirty then
                dirtyState <- DirtyState.Dirty

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
type ReduceNode<'T>(deps: IAdaptiveValue<'T>[], init: 'T, [<InlineIfLambda>] reduce: 'T -> 'T -> 'T) =
    // The graph this node belongs to, captured at creation (the ambient graph
    // of the creating thread).
    let ctx = GraphContext.Current
    let mutable version = 0L
    let mutable hasValue = false
    let mutable value = Unchecked.defaultof<'T>
    let depVersions = Array.zeroCreate<int64> deps.Length
    // Position of this node in the parents array of each dependency. -1 = no edge.
    let depSlots = Array.create deps.Length -1
    let edges = ParentEdges()
    let mutable dirtyState = DirtyState.MaybeDirty
    // Write-generation-keyed dirty cache: the last verdict stays valid until the
    // next applied write moves the global generation.
    let mutable lastCheckedWriteGen = -1L
    let mutable dirtyCache = true

    member private this.IsDirty() =
        let writeGen = AdaptiveRuntime.getWriteGeneration ctx

        if lastCheckedWriteGen = writeGen then
            dirtyCache
        elif not hasValue then
            true
        elif dirtyState = DirtyState.Dirty then
            // Marked by a dependency change.
            true
        elif dirtyState = DirtyState.Clean && edges.Count > 0 then
            // Observed and not marked: one flag check, no version reads.
            dirtyCache <- false
            lastCheckedWriteGen <- writeGen
            false
        else
            // Unobserved, or links can be stale: version check.
            let mutable dirty = false
            let mutable i = 0

            while not dirty && i < deps.Length do
                if (deps.[i] :> IAdaptiveObject).Version <> depVersions.[i] then
                    dirty <- true

                i <- i + 1

            // Verified clean: promote to the flag-based check (as AdaptiveNode
            // does) so later reads do not re-walk the dependency closure.
            if not dirty && edges.Count > 0 then
                let mutable complete = true
                let mutable j = 0

                while complete && j < deps.Length do
                    if depSlots[j] < 0 then
                        complete <- false

                    j <- j + 1

                if complete then
                    dirtyState <- DirtyState.Clean

            lastCheckedWriteGen <- writeGen
            dirtyCache <- dirty
            dirty

    member private this.Recompute() =
        // Generation at which the recompute starts; a write mid-compute would make
        // the computed value stale, so the node stays Dirty.
        let checkedGen = AdaptiveRuntime.getWriteGeneration ctx

        let mutable acc = init

        for i in 0 .. deps.Length - 1 do
            let v = deps.[i].GetValue()
            depVersions.[i] <- (deps.[i] :> IAdaptiveObject).Version
            acc <- reduce acc v

        value <- acc
        hasValue <- true
        version <- version + 1L

        dirtyState <-
            if AdaptiveRuntime.getWriteGeneration ctx <> checkedGen then
                // A write from user code inside the reduce callback: the value
                // may be stale, keep Dirty so the next read recomputes.
                DirtyState.Dirty
            elif edges.Count > 0 then
                DirtyState.Clean
            else
                DirtyState.MaybeDirty

        // The recompute is valid as of checkedGen: key the cache there so
        // later reads at the same generation skip the version check.
        lastCheckedWriteGen <- checkedGen
        dirtyCache <- false

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
            AdaptiveRuntime.enterEvaluation ctx

            try
                if this.IsDirty() then
                    this.Recompute()

                ctx.AddDependency(this :> IAdaptiveObject, version)
                value
            finally
                AdaptiveRuntime.exitEvaluation ctx

        member this.Version =
            AdaptiveRuntime.enterEvaluation ctx

            try
                if this.IsDirty() then version + 1L else version
            finally
                AdaptiveRuntime.exitEvaluation ctx

    interface IAdaptiveNode with
        member this.MarkDirty() =
            if dirtyState <> DirtyState.Dirty then
                dirtyState <- DirtyState.Dirty

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

/// <summary>An abbreviation for <see cref="ChangeableValue&lt;'T&gt;"/> (FDA <c>cval&lt;'T&gt;</c> parity).</summary>
type cval<'T> = ChangeableValue<'T>

/// <summary>
/// An active observation of an adaptive value. Registered as a parent
/// of the observed object. Marking enqueues it once per batch; delivery pulls the
/// current value and invokes the callback when the version changed.
/// </summary>
type Observation<'T>(target: IAdaptiveValue<'T>, [<InlineIfLambda>] callback: 'T -> unit) as this =
    // The graph this observation belongs to, captured at creation (the ambient
    // graph of the creating thread).
    let ctx = GraphContext.Current
    let mutable active = true
    let mutable enqueued = false
    let mutable indexInTarget = -1
    let mutable lastVersion = -1L

    /// Force the initial read and register this observation as a parent.
    member _.Attach() =
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
                ctx.EnqueueNotification(this :> INotifiable)

        member _.SetDepSlot(depIndex: int, parentIndex: int) =
            if depIndex = -1 then
                indexInTarget <- parentIndex

        member _.OnFirstParent() = ()
        member _.OnLastParent() = ()

    interface INotifiable with
        member this.Deliver() =
            enqueued <- false

            if active then
                // The version is consumed only after the callback succeeds: a
                // throwing callback keeps the observation's lastVersion stale,
                // so the next delivery re-reads and re-delivers the change.
                let newValue = target.GetValue()
                let newVersion = (target :> IAdaptiveObject).Version

                if newVersion <> lastVersion then
                    callback newValue
                    lastVersion <- newVersion

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
    /// An adaptive value whose content is supplied by an external snapshot
    /// function, re-read only when invalidated via the handle returned by
    /// <see cref="AVal.ofExternal"/> (FDA <c>AVal.ofExternal</c> parity,
    /// MAPA-DESIGN §1.1). Not invalidated → reads are O(1): no re-read, no
    /// comparison, no allocation. The invalidate handle is O(1) to call and
    /// thread-safe (a foreign-thread call posts to the owner context, the
    /// <c>cval.Post</c> pattern); the re-read happens on the next read on the
    /// owner thread.
    /// </summary>
    type ExternalValueNode<'T when 'T: equality>([<InlineIfLambda>] read: unit -> 'T) =
        // The graph this node belongs to, captured at creation (the ambient
        // graph of the creating thread).
        let ctx = GraphContext.Current
        let mutable value = Unchecked.defaultof<'T>
        let mutable hasValue = false
        let mutable version = 0L
        let mutable dirty = true
        // Foreign-thread invalidation goes through the post ring (the
        // cval.Post pattern): a queued flag, applied on the owner thread.
        let mutable posted = 0
        let edges = ParentEdges()
        let ownerThread = Environment.CurrentManagedThreadId

        /// <summary>
        /// The invalidate handle implementation (returned by
        /// <see cref="AVal.ofExternal"/>). Call this when the external source
        /// changed; the re-read happens on the next read. Not for direct use.
        /// </summary>
        member this.Invalidate() =
            if Environment.CurrentManagedThreadId = ownerThread then
                // Owner thread: mark directly. MarkFrom bumps the write
                // generation (the *A gate) and marks observers.
                dirty <- true
                ctx.MarkFrom edges
            else if Interlocked.CompareExchange(&posted, 1, 0) = 0 then
                ctx.PostRing.Enqueue(this :> obj)

        member private this.Poll() =
            if dirty then
                dirty <- false
                let next = read ()

                if not hasValue || not (EqualityComparer<'T>.Default.Equals(value, next)) then
                    value <- next
                    hasValue <- true
                    version <- version + 1L

        interface IPostSource with
            member this.ApplyPosted() =
                // Clear the queued flag before marking: an invalidate that
                // lands after the clear re-enqueues, so it cannot be lost.
                Interlocked.Exchange(&posted, 0) |> ignore
                this.Invalidate()

        interface IAdaptiveValue<'T> with
            member this.GetValue() =
                ctx.ClaimOwner()

                try
                    this.Poll()
                    ctx.AddDependency(this :> IAdaptiveObject, version)
                    value
                finally
                    ctx.ReleaseOwner()

            member this.Version =
                // Dirty indicator: version + 1 while invalidated but not yet
                // re-read, so version-checking consumers recompute exactly
                // once; the re-read at GetValue decides the real version.
                if dirty then version + 1L else version

        interface IEdgeTarget with
            member _.EdgeCount = edges.Count
            member _.AddEdge(parent: IAdaptiveNode, depIndex: int) = edges.Add(parent, depIndex)
            member _.RemoveEdgeAt(index: int) = edges.RemoveAt(index)

    /// <summary>
    /// Creates an adaptive value from an external snapshot function and an
    /// invalidate handle (FDA <c>AVal.ofExternal</c> parity, MAPA-DESIGN §1.1).
    /// The read function runs at most once per invalidate, on the next read;
    /// when not invalidated, reads are O(1) and allocate nothing. The handle
    /// is O(1) to call and thread-safe (a foreign-thread call is posted to the
    /// owner context and applied at the next graph operation).
    /// </summary>
    /// <example>
    /// <code>
    /// let mutable current = 0
    /// let value, invalidate = AVal.ofExternal (fun () -> current)
    /// current &lt;- 42
    /// invalidate ()
    /// printfn "%d" (AVal.getValue value)  // 42
    /// </code>
    /// </example>
    let inline ofExternal ([<InlineIfLambda>] read: unit -> 'T) : aval<'T> * (unit -> unit) =
        let node = ExternalValueNode<'T>(read)
        (node :> aval<'T>, fun () -> node.Invalidate())

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
    let inline constant (value: 'T) : aval<'T> = ConstantValue value

    /// <summary>
    /// Creates a constant adaptive value using the given create function (FDA
    /// <c>AVal.delay</c> parity). The function runs at most once, on the first
    /// read; later reads return the cached value.
    /// </summary>
    /// <example>
    /// <code>
    /// let v = AVal.delay (fun () -> expensiveComputation ())
    /// </code>
    /// </example>
    let inline delay ([<InlineIfLambda>] create: unit -> 'T) : aval<'T> = LazyConstantValue create

    /// <summary>
    /// Creates a changeable value initially holding the given value (FDA
    /// <c>AVal.init</c> parity; the same as <c>CVal.create</c>).
    /// </summary>
    let inline init (value: 'T) : cval<'T> = ChangeableValue value

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
    let inline map ([<InlineIfLambda>] f: 'T -> 'U) (value: aval<'T>) : aval<'U> =
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
    let inline map2 ([<InlineIfLambda>] f: 'T -> 'U -> 'V) (left: aval<'T>) (right: aval<'U>) : aval<'V> =
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
    let inline map3 ([<InlineIfLambda>] f: 'A -> 'B -> 'C -> 'T) (a: aval<'A>) (b: aval<'B>) (c: aval<'C>) : aval<'T> =
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
        (a: aval<'A>)
        (b: aval<'B>)
        (c: aval<'C>)
        (d: aval<'D>)
        : aval<'T> =
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
    let inline mapN ([<InlineIfLambda>] compute: 'T[] -> 'U) (deps: aval<'T>[]) : aval<'U> = MapNNode(deps, compute)

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
    let inline reduce (init: 'T) ([<InlineIfLambda>] reduce: 'T -> 'T -> 'T) (deps: aval<'T>[]) : aval<'T> =
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
    let inline sum (deps: aval<int>[]) : aval<int> = ReduceNode(deps, 0, (+))

    /// <summary>
    /// Transforms an adaptive value using an async function that returns a Task.
    /// </summary>
    /// <param name="f">The async function to apply.</param>
    /// <param name="value">The source adaptive value.</param>
    /// <returns>An adaptive value containing Tasks of the result type.</returns>
    let inline mapTask ([<InlineIfLambda>] f: 'T -> Task<'U>) (value: aval<'T>) : aval<Task<'U>> =
        AdaptiveNode(fun () -> f (value.GetValue()))

    let inline mapValueTask ([<InlineIfLambda>] f: 'T -> ValueTask<'U>) (value: aval<'T>) : aval<ValueTask<'U>> =
        AdaptiveNode(fun () -> f (value.GetValue()))

    let inline bind ([<InlineIfLambda>] f: 'T -> aval<'U>) (value: aval<'T>) : aval<'U> =
        AdaptiveNode(fun () ->
            let inner = f (value.GetValue())
            inner.GetValue())

    /// <summary>
    /// Adaptively applies the mapping to the two values and adaptively depends
    /// on the adaptive value the mapping returns (FDA <c>AVal.bind2</c> parity).
    /// When an input changes, the previously returned inner value is dropped and
    /// the mapping selects a new one.
    /// </summary>
    let inline bind2 ([<InlineIfLambda>] f: 'T -> 'U -> aval<'V>) (a: aval<'T>) (b: aval<'U>) : aval<'V> =
        AdaptiveNode(fun () -> (f (a.GetValue()) (b.GetValue())).GetValue())

    /// <summary>
    /// Adaptively applies the mapping to the three values and adaptively depends
    /// on the adaptive value the mapping returns (FDA <c>AVal.bind3</c> parity).
    /// </summary>
    let inline bind3
        ([<InlineIfLambda>] f: 'T -> 'U -> 'V -> aval<'W>)
        (a: aval<'T>)
        (b: aval<'U>)
        (c: aval<'V>)
        : aval<'W> =
        AdaptiveNode(fun () -> (f (a.GetValue()) (b.GetValue()) (c.GetValue())).GetValue())

    /// <summary>
    /// Creates a custom adaptive value using the given computation (FDA
    /// <c>AVal.custom</c> parity; deviation: FDA passes an
    /// <c>AdaptiveToken</c>, we have no token, so the computation takes unit).
    /// Callers are responsible for removing inputs that are no longer needed.
    /// </summary>
    let inline custom ([<InlineIfLambda>] compute: unit -> 'T) : aval<'T> = AdaptiveNode(compute)

    let inline bindTask ([<InlineIfLambda>] f: 'T -> Task<'U>) (value: aval<'T>) : Task<'U> = value.GetValue() |> f

    let inline bindValueTask ([<InlineIfLambda>] f: 'T -> ValueTask<'U>) (value: aval<'T>) : ValueTask<'U> =
        value.GetValue() |> f

    let inline mapTaskResult ([<InlineIfLambda>] f: 'T -> 'U) (value: aval<Task<'T>>) : aval<Task<'U>> =
        AdaptiveNode(fun () ->
            task {
                let! inner = value.GetValue()
                return f inner
            })

    let inline mapValueTaskResult ([<InlineIfLambda>] f: 'T -> 'U) (value: aval<ValueTask<'T>>) : aval<ValueTask<'U>> =
        AdaptiveNode(fun () ->
            ValueTask<'U>(
                task {
                    let! inner = value.GetValue()
                    return f inner
                }
            ))

    let inline bindTaskResult ([<InlineIfLambda>] f: 'T -> Task<'U>) (value: aval<Task<'T>>) : aval<Task<'U>> =
        AdaptiveNode(fun () ->
            task {
                let! inner = value.GetValue()
                return! f inner
            })

    let inline bindValueTaskResult
        ([<InlineIfLambda>] f: 'T -> ValueTask<'U>)
        (value: aval<ValueTask<'T>>)
        : aval<ValueTask<'U>> =
        AdaptiveNode(fun () ->
            ValueTask<'U>(
                task {
                    let! inner = value.GetValue()
                    return! f inner
                }
            ))

    let inline getValue (value: aval<'T>) = value.GetValue()

    /// <summary>Evaluates the given adaptive value and returns its current value (FDA <c>AVal.force</c> parity; the same as <c>getValue</c>).</summary>
    let inline force (value: aval<'T>) = value.GetValue()

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
    let inline observe ([<InlineIfLambda>] callback: 'T -> unit) (value: aval<'T>) : IObservation =
        let observation = new Observation<_>(value, callback)
        observation.Attach()
        observation

    let inline getValueTask (value: aval<'T>) = Task.FromResult(value.GetValue())

    let inline getValueValueTask (value: aval<'T>) = ValueTask<'T>(value.GetValue())

module CVal =
    /// <summary>Creates a changeable value initially holding the given value.</summary>
    let inline create (value: 'T) : cval<'T> = ChangeableValue value

    let inline set (value: 'T) (cval: cval<'T>) = cval.Set value

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
    let inline post (value: 'T) (cval: cval<'T>) = cval.Post value

    /// <summary>Views the changeable value as an adaptive value.</summary>
    let inline value (cval: cval<'T>) : aval<'T> = cval
