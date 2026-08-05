namespace AdaptiveSlop.Core

open System
open System.Collections.Generic

// =============================================================================
// Adaptive list nodes (docs/ALIST-DESIGN.md §3)
//
// Positional operations over ordered journals. A node applies journal
// operations sequentially to its internal ResizeArray and emits the translated
// output delta:
//   - FilterMapListNode: output position = Fenwick prefix sum over surviving
//     input positions (O(log n) per op, zero allocation, an int[] buffer);
//   - AppendListNode: absolute output position = leftCount + p for right ops
//     (O(1) per op; the journal carries a source tag to preserve cross-source
//     arrival order).
// Initial loads read the source view first and register the sink after
// (PLAN.md §7.4: no double-apply of a dirty source draining into the journal).
// =============================================================================

/// <summary>
/// A constant list: the content is fixed but computed lazily, once, at first
/// read (FDA parity: the create function runs at most once).
/// </summary>
type ConstantList<'T>([<InlineIfLambda>] create: unit -> 'T[]) =
    let value = lazy create ()

    interface IAdaptiveList<'T> with
        member this.GetValue() =
            AdaptiveRuntime.addDependency (this :> IAdaptiveObject) 0L
            value.Value :> IReadOnlyList<'T>

        member _.Version = 0L

    interface IDisposable with
        member _.Dispose() = ()

/// <summary>
/// Maps every element of a list (or chooses/filters, when the mapping returns
/// <c>ValueNone</c> to drop an element).
/// </summary>
/// <remarks>
/// The output is the subsequence of input elements that survive the mapping.
/// The node keeps the input position of every output element in a sorted
/// array parallel to the output; the output position of an input element is
/// the index of its stored input position (binary search). Inserts and
/// removes shift the stored positions after the change point (tail fixup,
/// O(k), same class as the output array's own memmove). An update on a
/// non-surviving element that now passes the mapping inserts it into the
/// output; an update on a surviving element that now fails removes it (FDA
/// choose semantics, docs/ALIST-DESIGN.md §3.4).
/// </remarks>
type FilterMapListNode<'T, 'U>(source: IAdaptiveList<'T>, [<InlineIfLambda>] mapping: 'T -> 'U voption) =
    let mutable version = 0L
    let mutable edges = ParentEdges()
    let mutable sinks = SinkList.Create()
    let mutable depVersion = 0L
    let mutable initialized = false
    let mutable disposed = false
    // Output state, and the sorted input position of every output element
    // (parallel to the output).
    let mutable output = ResizeArray<'U>()
    let mutable inputPositions = ResizeArray<int>()
    let mutable inputCount = 0
    let mutable journal = ListDelta<'T>.Create()
    let mutable out = ListDelta<'U>.Create()

    member private this.Register() =
        match box source with
        | :? IListSinkRegistry as r -> r.AddListSink(box (this :> IListDeltaSink<'T>))
        | _ -> ()

    member private this.Unregister() =
        match box source with
        | :? IListSinkRegistry as r -> r.RemoveListSink(box (this :> IListDeltaSink<'T>))
        | _ -> ()

    /// First index whose stored input position is >= p (the output position of
    /// the element at input position p when it survives).
    member private _.LowerBound(p: int) =
        let mutable lo = 0
        let mutable hi = inputPositions.Count

        while lo < hi do
            let mid = (lo + hi) >>> 1

            if inputPositions[mid] < p then lo <- mid + 1 else hi <- mid

        lo

    /// Whether the element at input position p is currently in the output.
    member private this.Contains(p: int, index: int) =
        index < inputPositions.Count && inputPositions[index] = p

    member private this.Load(snapshot: ResizeArray<'T>) =
        for i in 0 .. snapshot.Count - 1 do
            match mapping snapshot[i] with
            | ValueSome u ->
                output.Add u
                inputPositions.Add i
            | ValueNone -> ()

        inputCount <- snapshot.Count

    member private this.EnsureInitialized() =
        if not initialized then
            // Snapshot first, register between, then load (PLAN.md §7.4).
            // The flag is set last: an exception leaves the node uninitialized.
            let snapshot = ResizeArray<'T>(source.GetValue())
            this.Register()
            this.Load(snapshot)
            depVersion <- source.Version
            initialized <- true

    member private this.Drain() =
        if not journal.IsEmpty then
            out.Clear()
            let ops = journal.Ops.Items
            let cnt = journal.Ops.Count
            let mutable i = 0
            // Consumed count: applied ops must never be applied again; the op
            // that threw survives for the next drain.
            let mutable opsDone = 0

            try
                while i < cnt do
                    let op = ops[i]
                    let p = op.Position
                    let j = this.LowerBound p
                    let present = this.Contains(p, j)

                    match op.Kind with
                    | ListOpKind.Insert ->
                        match mapping op.Value with
                        | ValueSome u ->
                            output.Insert(j, u)
                            inputPositions.Insert(j, p)

                            // Elements at input positions >= p shifted +1; the
                            // tail starts after the inserted position.
                            for k in j + 1 .. inputPositions.Count - 1 do
                                inputPositions[k] <- inputPositions[k] + 1

                            out.Ops <- Collections.bufferAppend out.Ops (ListOp(ListOpKind.Insert, j, u, 0uy))
                        | ValueNone ->
                            // Elements at input positions >= p shifted +1 even when
                            // the new element does not survive.
                            for k in j .. inputPositions.Count - 1 do
                                inputPositions[k] <- inputPositions[k] + 1

                        inputCount <- inputCount + 1
                    | ListOpKind.Remove ->
                        if present then
                            output.RemoveAt j
                            inputPositions.RemoveAt j

                            out.Ops <-
                                Collections.bufferAppend
                                    out.Ops
                                    (ListOp(ListOpKind.Remove, j, Unchecked.defaultof<'U>, 0uy))

                        // Elements at input positions > p shifted -1, whether or
                        // not the removed element was in the output.
                        for k in j .. inputPositions.Count - 1 do
                            inputPositions[k] <- inputPositions[k] - 1

                        inputCount <- inputCount - 1
                    | ListOpKind.Update ->
                        match mapping op.Value with
                        | ValueSome u ->
                            if present then
                                output[j] <- u
                                out.Ops <- Collections.bufferAppend out.Ops (ListOp(ListOpKind.Update, j, u, 0uy))
                            else
                                // An update never moves other elements' input
                                // positions: the new output element takes
                                // position p, the tail keeps its positions.
                                output.Insert(j, u)
                                inputPositions.Insert(j, p)
                                out.Ops <- Collections.bufferAppend out.Ops (ListOp(ListOpKind.Insert, j, u, 0uy))
                        | ValueNone ->
                            if present then
                                output.RemoveAt j
                                inputPositions.RemoveAt j

                                out.Ops <-
                                    Collections.bufferAppend
                                        out.Ops
                                        (ListOp(ListOpKind.Remove, j, Unchecked.defaultof<'U>, 0uy))
                    | _ -> ()

                    i <- i + 1
                    opsDone <- i
            finally
                // Compaction markers: entries appended during processing
                // (reentrant writes from the mapping) and the op that threw
                // survive for the next drain; consumed ops are dropped (double
                // removes corrupt the list).
                let live = journal.Ops.Count

                if live > opsDone then
                    Array.Copy(journal.Ops.Items, opsDone, journal.Ops.Items, 0, live - opsDone)
                    journal.Ops.Count <- live - opsDone
                else
                    journal.Ops.Count <- 0

            if not out.IsEmpty then
                version <- version + 1L
                Collections.pushListDelta sinks out

    interface IListDeltaSink<'T> with
        member this.OnDeltas(ops: ListOp<'T>[], opCnt: int) =
            if not disposed then
                Collections.journalAppendList &journal ops opCnt
                version <- version + 1L
                GraphContext.Default.MarkFrom(edges)

    interface IAdaptiveList<'U> with
        member this.GetValue() =
            let ctx = GraphContext.Default
            ctx.ClaimOwner()

            try
                if disposed then
                    invalidOp "This adaptive list has been disposed."

                this.EnsureInitialized()

                if source.Version <> depVersion then
                    source.GetValue() |> ignore
                    depVersion <- source.Version

                if not journal.IsEmpty then
                    this.Drain()

                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) version
                output :> IReadOnlyList<'U>
            finally
                ctx.ReleaseOwner()

        member _.Version = version

    interface IDisposable with
        member this.Dispose() =
            if not disposed then
                disposed <- true
                this.Unregister()
                Collections.clearSinks &sinks

    interface IListSinkRegistry with
        member this.AddListSink(sink) = Collections.addSink &sinks sink

        member this.RemoveListSink(sink) = Collections.removeSink &sinks sink

    interface IEdgeTarget with
        member _.EdgeCount = edges.Count
        member _.AddEdge(parent: IAdaptiveNode, depIndex: int) = edges.Add(parent, depIndex)
        member _.RemoveEdgeAt(index: int) = edges.RemoveAt(index)

/// <summary>
/// The concatenation of two lists. Ops from both sources share one journal in
/// arrival order with a source tag: cross-source order matters, because a right
/// op's absolute output position depends on <c>leftCount</c> at its application
/// point (docs/ALIST-DESIGN.md §3.4).
/// </summary>
type AppendListNode<'T>(left: IAdaptiveList<'T>, right: IAdaptiveList<'T>) =
    let mutable version = 0L
    let mutable edges = ParentEdges()
    let mutable sinks = SinkList.Create()
    let mutable leftVersion = 0L
    let mutable rightVersion = 0L
    let mutable initialized = false
    let mutable disposed = false
    let mutable output = ResizeArray<'T>()
    let mutable leftCount = 0
    // Tagged journal: Source 0 = left, 1 = right, in arrival order.
    let mutable journal: ListOp<'T>[] = Array.zeroCreate 16
    let mutable journalCount = 0
    let mutable out = ListDelta<'T>.Create()
    // One sink wrapper per source: the wrapper carries the source tag. Two
    // allocations at registration (amortized), zero on the steady state.
    let mutable leftSink: obj = null
    let mutable rightSink: obj = null

    member private this.CreateSink(source: byte) =
        { new IListDeltaSink<'T> with
            member _.OnDeltas(ops: ListOp<'T>[], opCnt: int) =
                if not disposed then
                    this.JournalAppend(ops, opCnt, source)
                    version <- version + 1L
                    GraphContext.Default.MarkFrom(edges) }

    member private this.Register() =
        leftSink <- box (this.CreateSink 0uy)
        rightSink <- box (this.CreateSink 1uy)

        match box left with
        | :? IListSinkRegistry as r -> r.AddListSink(leftSink)
        | _ -> ()

        match box right with
        | :? IListSinkRegistry as r -> r.AddListSink(rightSink)
        | _ -> ()

    member private this.Unregister() =
        if not (isNull leftSink) then
            match box left with
            | :? IListSinkRegistry as r -> r.RemoveListSink(leftSink)
            | _ -> ()

            leftSink <- null

        if not (isNull rightSink) then
            match box right with
            | :? IListSinkRegistry as r -> r.RemoveListSink(rightSink)
            | _ -> ()

            rightSink <- null

    member private this.JournalAppend(ops: ListOp<'T>[], opCnt: int, source: byte) =
        if opCnt > 0 then
            Collections.ensureCapacity &journal (journalCount + opCnt)

            for i in 0 .. opCnt - 1 do
                let op = ops[i]
                journal[journalCount] <- ListOp(op.Kind, op.Position, op.Value, source)
                journalCount <- journalCount + 1

    member private this.Load(leftSnapshot: ResizeArray<'T>, rightSnapshot: ResizeArray<'T>) =
        output <- ResizeArray<'T>(leftSnapshot.Count + rightSnapshot.Count)
        output.AddRange leftSnapshot
        output.AddRange rightSnapshot
        leftCount <- leftSnapshot.Count

    member private this.EnsureInitialized() =
        if not initialized then
            // The flag is set last: an exception leaves the node uninitialized.
            let ls = ResizeArray<'T>(left.GetValue())
            let rs = ResizeArray<'T>(right.GetValue())
            this.Register()
            this.Load(ls, rs)
            leftVersion <- left.Version
            rightVersion <- right.Version
            initialized <- true

    member private this.Drain() =
        if journalCount > 0 then
            out.Clear()
            let mutable i = 0

            while i < journalCount do
                let op = journal[i]
                let isLeft = op.Source = 0uy
                let abs = if isLeft then op.Position else leftCount + op.Position

                match op.Kind with
                | ListOpKind.Insert ->
                    output.Insert(abs, op.Value)
                    out.Ops <- Collections.bufferAppend out.Ops (ListOp(ListOpKind.Insert, abs, op.Value, 0uy))

                    if isLeft then
                        leftCount <- leftCount + 1
                | ListOpKind.Remove ->
                    output.RemoveAt abs

                    out.Ops <-
                        Collections.bufferAppend out.Ops (ListOp(ListOpKind.Remove, abs, Unchecked.defaultof<'T>, 0uy))

                    if isLeft then
                        leftCount <- leftCount - 1
                | ListOpKind.Update ->
                    output[abs] <- op.Value
                    out.Ops <- Collections.bufferAppend out.Ops (ListOp(ListOpKind.Update, abs, op.Value, 0uy))
                | _ -> ()

                i <- i + 1

            journalCount <- 0

            if not out.IsEmpty then
                version <- version + 1L
                Collections.pushListDelta sinks out

    interface IListDeltaSink<'T> with
        member this.OnDeltas(ops: ListOp<'T>[], opCnt: int) =
            if not disposed then
                this.JournalAppend(ops, opCnt, 1uy)
                version <- version + 1L
                GraphContext.Default.MarkFrom(edges)

    interface IAdaptiveList<'T> with
        member this.GetValue() =
            let ctx = GraphContext.Default
            ctx.ClaimOwner()

            try
                if disposed then
                    invalidOp "This adaptive list has been disposed."

                this.EnsureInitialized()

                if left.Version <> leftVersion then
                    left.GetValue() |> ignore
                    leftVersion <- left.Version

                if right.Version <> rightVersion then
                    right.GetValue() |> ignore
                    rightVersion <- right.Version

                if journalCount > 0 then
                    this.Drain()

                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) version
                output :> IReadOnlyList<'T>
            finally
                ctx.ReleaseOwner()

        member _.Version = version

    interface IDisposable with
        member this.Dispose() =
            if not disposed then
                disposed <- true
                this.Unregister()
                Collections.clearSinks &sinks

    interface IListSinkRegistry with
        member this.AddListSink(sink) = Collections.addSink &sinks sink

        member this.RemoveListSink(sink) = Collections.removeSink &sinks sink

    interface IEdgeTarget with
        member _.EdgeCount = edges.Count
        member _.AddEdge(parent: IAdaptiveNode, depIndex: int) = edges.Add(parent, depIndex)
        member _.RemoveEdgeAt(index: int) = edges.RemoveAt(index)

/// <summary>
/// A list observation: sink + edge parent on the target. Delivers
/// (view, ordered delta) callbacks after each batch that changes the list.
/// The delta is transient: valid only during the callback.
/// </summary>
type ObserveListNode<'T>
    (target: IAdaptiveList<'T>, [<InlineIfLambda>] callback: IReadOnlyList<'T> -> ListDelta<'T> -> unit) =
    let mutable active = true
    let mutable enqueued = false
    let mutable indexInTarget = -1
    let mutable journal = ListDelta<'T>.Create()

    /// Register this observation on the target: as a parent edge (marks) and
    /// as a delta sink (deltas). Called once from the observe API. The initial
    /// read registers lazy derived chains so that later marks reach this
    /// observation; no callback fires on attach.
    member this.Attach() =
        target.GetValue() |> ignore

        match target with
        | :? IEdgeTarget as edgeTarget -> indexInTarget <- edgeTarget.AddEdge(this :> IAdaptiveNode, -1)
        | _ -> ()

        match box target with
        | :? IListSinkRegistry as r -> r.AddListSink(box (this :> IListDeltaSink<'T>))
        | _ -> ()

    interface IListDeltaSink<'T> with
        member this.OnDeltas(ops: ListOp<'T>[], opCnt: int) =
            if active then
                Collections.journalAppendList &journal ops opCnt

                if not enqueued then
                    enqueued <- true
                    GraphContext.Default.EnqueueNotification(this :> INotifiable)

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
                // Drain the target first: for derived nodes this pushes their
                // pending output delta into the journal. Entries appended here
                // belong to this delivery.
                let view = target.GetValue()

                if not journal.IsEmpty then
                    let start = journal.Ops.Count
                    callback view journal

                    // Keep the entries appended during the callback (reentrant
                    // writes) for the next delivery.
                    let live = journal.Ops.Count

                    if live > start then
                        Array.Copy(journal.Ops.Items, start, journal.Ops.Items, 0, live - start)
                        journal.Ops.Count <- live - start
                    else
                        journal.Ops.Count <- 0

    interface IObservation with
        member _.IsActive = active

        member this.Dispose() =
            if active then
                active <- false

                match target with
                | :? IEdgeTarget as edgeTarget -> edgeTarget.RemoveEdgeAt(indexInTarget)
                | _ -> ()

                match box target with
                | :? IListSinkRegistry as r -> r.RemoveListSink(box (this :> IListDeltaSink<'T>))
                | _ -> ()
