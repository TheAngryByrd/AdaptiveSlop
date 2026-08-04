namespace AdaptiveSlop.Core

open System
open System.Collections.Generic

// =============================================================================
// Collection observation (PLAN.md Section 7.1)
//
// An observation registers TWICE on its target:
//   - as a delta sink: receives the effective deltas the target pushes
//     (at write time for sources, at drain time for derived nodes);
//   - as a parent edge: receives marks, so observing a derived node works
//     even when nothing else reads it (the mark triggers the delivery, and
//     the delivery drains the target, which pushes its pending output delta
//     into the journal).
// Delivery is deferred (PLAN.md Section 6.5): the callback runs after the
// batch or the write completes, with the current transient view and the net
// delta since the previous delivery. Deltas are reduced per element: source
// ops are effective and strictly alternate per element, so the net count
// decides the delivered operation.
// =============================================================================

/// <summary>
/// Internal. A set observation: sink + edge parent on the target. Delivers
/// (view, net delta) callbacks after each batch that changes the set.
/// </summary>
type internal ObserveSetNode<'T when 'T: equality>
    (target: IAdaptiveSet<'T>, callback: IReadOnlySet<'T> -> SetDelta<'T> -> unit) as this =
    let mutable active = true
    let mutable enqueued = false
    let mutable indexInTarget = -1
    let mutable journal = SetDelta<'T>.Create()
    let mutable out = SetDelta<'T>.Create()
    // Reused scratch: net op count per element (positive = added, negative = removed).
    let counts = Dictionary<'T, int>()

    /// Reduce the journal to a net delta. Effective source ops alternate per
    /// element, so the net count decides: positive -> Add, negative -> Rem.
    /// The counts dictionary and the out buffers are reused: zero allocation
    /// on the steady state.
    let reduceJournal () =
        out.Clear()

        for i in 0 .. journal.Adds.Count - 1 do
            let x = journal.Adds.Items[i]
            let mutable n = 0

            if counts.TryGetValue(x, &n) then
                counts[x] <- n + 1
            else
                counts[x] <- 1

        for i in 0 .. journal.Rems.Count - 1 do
            let x = journal.Rems.Items[i]
            let mutable n = 0

            if counts.TryGetValue(x, &n) then
                counts[x] <- n - 1
            else
                counts[x] <- -1

        // Explicit struct-enumerator loop: `for KeyValue in dict` boxes the
        // enumerator (measured 24 B per delivery).
        let mutable e = counts.GetEnumerator()

        while e.MoveNext() do
            let kvp = e.Current

            if kvp.Value > 0 then
                out.Adds <- Collections.bufferAppend out.Adds kvp.Key
            elif kvp.Value < 0 then
                out.Rems <- Collections.bufferAppend out.Rems kvp.Key

        counts.Clear()

    /// Keep the entries appended after <paramref name="start"/> (reentrant
    /// writes during the callback); drop the consumed ones.
    let compact (buffer: DeltaBuffer<'T> byref) (start: int) =
        let live = buffer.Count

        if live > start then
            Array.Copy(buffer.Items, start, buffer.Items, 0, live - start)
            buffer.Count <- live - start
        else
            buffer.Count <- 0

    /// Register this observation on the target: as a parent edge (marks) and
    /// as a delta sink (deltas). Called once from the observe API. The initial
    /// read registers lazy derived chains (Section 6.9) so that later marks
    /// reach this observation; no callback fires on attach.
    member internal this.Attach() =
        target.GetValue() |> ignore

        match target with
        | :? IEdgeTarget as edgeTarget -> indexInTarget <- edgeTarget.AddEdge(this :> IAdaptiveNode, -1)
        | _ -> ()

        match box target with
        | :? ISetSinkRegistry as r -> r.AddSetSink(box (this :> ISetDeltaSink<'T>))
        | _ -> ()

    interface ISetDeltaSink<'T> with
        member this.OnDeltas(adds: 'T[], addCnt: int, rems: 'T[], remCnt: int) =
            if active then
                Collections.journalAppendSet &journal adds addCnt rems remCnt

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
                    let addStart = journal.Adds.Count
                    let remStart = journal.Rems.Count
                    reduceJournal ()
                    callback view out
                    compact &journal.Adds addStart
                    compact &journal.Rems remStart

    interface IObservation with
        member _.IsActive = active

        member this.Dispose() =
            if active then
                active <- false

                match target with
                | :? IEdgeTarget as edgeTarget -> edgeTarget.RemoveEdgeAt(indexInTarget)
                | _ -> ()

                match box target with
                | :? ISetSinkRegistry as r -> r.RemoveSetSink(box (this :> ISetDeltaSink<'T>))
                | _ -> ()

/// <summary>
/// Internal. A map observation: sink + edge parent on the target. Delivers
/// (view, net delta) callbacks after each batch that changes the map.
/// </summary>
type internal ObserveMapNode<'K, 'V when 'K: equality>
    (target: IAdaptiveMap<'K, 'V>, callback: IReadOnlyDictionary<'K, 'V> -> MapDelta<'K, 'V> -> unit) as this =
    let mutable active = true
    let mutable enqueued = false
    let mutable indexInTarget = -1
    let mutable journal = MapDelta<'K, 'V>.Create()
    let mutable out = MapDelta<'K, 'V>.Create()
    // Reused scratch: net op count per key (positive = set, negative = removed).
    let counts = Dictionary<'K, int>()
    // Reused scratch: the last set value per key (the delivered Set carries it).
    let lastValues = Dictionary<'K, 'V>()

    /// Reduce the journal to a net delta. Effective source ops strictly
    /// alternate per key, so the net count decides: positive -> Set with the
    /// last value, negative -> Rem, zero -> nothing.
    let reduceJournal () =
        out.Clear()

        for i in 0 .. journal.Sets.Count - 1 do
            let struct (k, v) = journal.Sets.Items[i]
            let mutable n = 0

            if counts.TryGetValue(k, &n) then
                counts[k] <- n + 1
            else
                counts[k] <- 1

            lastValues[k] <- v

        for i in 0 .. journal.Rems.Count - 1 do
            let k = journal.Rems.Items[i]
            let mutable n = 0

            if counts.TryGetValue(k, &n) then
                counts[k] <- n - 1
            else
                counts[k] <- -1

            lastValues.Remove k |> ignore

        // Explicit struct-enumerator loop: `for KeyValue in dict` boxes the
        // enumerator (measured 24 B per delivery).
        let mutable e = counts.GetEnumerator()

        while e.MoveNext() do
            let kvp = e.Current

            if kvp.Value > 0 then
                out.Sets <- Collections.bufferAppend out.Sets (struct (kvp.Key, lastValues[kvp.Key]))
            elif kvp.Value < 0 then
                out.Rems <- Collections.bufferAppend out.Rems kvp.Key

        counts.Clear()
        lastValues.Clear()

    /// Keep the entries appended after <paramref name="start"/> (reentrant
    /// writes during the callback); drop the consumed ones.
    let compact (buffer: DeltaBuffer<'T> byref) (start: int) =
        let live = buffer.Count

        if live > start then
            Array.Copy(buffer.Items, start, buffer.Items, 0, live - start)
            buffer.Count <- live - start
        else
            buffer.Count <- 0

    /// Register this observation on the target: as a parent edge (marks) and
    /// as a delta sink (deltas). Called once from the observe API. The initial
    /// read registers lazy derived chains (Section 6.9) so that later marks
    /// reach this observation; no callback fires on attach.
    member internal this.Attach() =
        target.GetValue() |> ignore

        match target with
        | :? IEdgeTarget as edgeTarget -> indexInTarget <- edgeTarget.AddEdge(this :> IAdaptiveNode, -1)
        | _ -> ()

        match box target with
        | :? IMapSinkRegistry as r -> r.AddMapSink(box (this :> IMapDeltaSink<'K, 'V>))
        | _ -> ()

    interface IMapDeltaSink<'K, 'V> with
        member this.OnDeltas(sets: struct ('K * 'V)[], setCnt: int, rems: 'K[], remCnt: int) =
            if active then
                Collections.journalAppendMap &journal sets setCnt rems remCnt

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
                    let setStart = journal.Sets.Count
                    let remStart = journal.Rems.Count
                    reduceJournal ()
                    callback view out
                    compact &journal.Sets setStart
                    compact &journal.Rems remStart

    interface IObservation with
        member _.IsActive = active

        member this.Dispose() =
            if active then
                active <- false

                match target with
                | :? IEdgeTarget as edgeTarget -> edgeTarget.RemoveEdgeAt(indexInTarget)
                | _ -> ()

                match box target with
                | :? IMapSinkRegistry as r -> r.RemoveMapSink(box (this :> IMapDeltaSink<'K, 'V>))
                | _ -> ()
