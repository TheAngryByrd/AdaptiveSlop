namespace AdaptiveSlop.Core

open System
open System.Collections.Generic

// =============================================================================
// Changeable collection sources (PLAN.md Section 6.9)
//
// A source write: updates the internal state, advances the version, appends the
// net delta to the journal of every registered sink, and marks the scalar
// parents. Writes never process a delta; processing happens on read (drain).
// =============================================================================

/// <summary>
/// A changeable set: the writable source of an adaptive set. Reads and writes
/// are confined to the owner thread.
/// </summary>
/// <remarks>
/// <para>
/// Writes inside a <c>Transaction.run</c> are journaled in order and applied at
/// commit as one net delta: adds and removes of the same element in one batch
/// cancel, and the last write wins. Reads inside a transaction see the
/// pre-transaction state.
/// </para>
/// <para>
/// <c>GetValue</c> returns a transient view of the internal state, valid only
/// until the next write. <c>CSet.force</c> materializes an immutable snapshot.
/// </para>
/// </remarks>
type ChangeableSet<'T>(initial: seq<'T>) =
    let mutable version = 0L
    let data = HashSet<'T>(initial)
    let edges = ParentEdges()
    let mutable sinks = SinkList.Create()
    let mutable outDelta = SetDelta<'T>.Create()
    // Ordered transaction journal; replayed at commit for a net delta.
    let mutable journal: struct ('T * bool)[] = Array.zeroCreate 16
    let mutable journalCount = 0
    // Scratch sets for the net-delta computation. Reused; zero allocation.
    let mutable scratchAdds = HashSet<'T>()
    let mutable scratchRems = HashSet<'T>()
    let mutable flushEnqueued = false
    // Pending full replace for Set inside a transaction. Last write wins.
    let mutable pendingValue: seq<'T> voption = ValueNone

    member private this.PushAndMark() =
        if not outDelta.IsEmpty then
            version <- version + 1L
            Collections.pushAndMarkSet outDelta sinks edges
            outDelta.Clear()

    member private this.Apply(newValue: seq<'T>) =
        // Net delta: removed = old items not in newValue; added = new items.
        scratchAdds.Clear()

        for item in newValue do
            scratchAdds.Add item |> ignore

        outDelta.Clear()

        for item in data do
            if not (scratchAdds.Contains item) then
                outDelta.Rems <- Collections.bufferAppend outDelta.Rems item

        // Apply the removals (after the scan: data is not mutated while iterated).
        for i in 0 .. outDelta.Rems.Count - 1 do
            data.Remove outDelta.Rems.Items[i] |> ignore

        for item in scratchAdds do
            if data.Add item then
                outDelta.Adds <- Collections.bufferAppend outDelta.Adds item

        this.PushAndMark()

    member private this.ApplyAndFlush(item: 'T, isAdd: bool) =
        let mutable changed = if isAdd then data.Add item else data.Remove item

        if changed then
            outDelta.Clear()

            if isAdd then
                outDelta.Adds <- Collections.bufferAppend outDelta.Adds item
            else
                outDelta.Rems <- Collections.bufferAppend outDelta.Rems item

            this.PushAndMark()

    member private this.CommitJournal() =
        // The current batch's flush is already enqueued (we are running from
        // it); resetting now lets reentrant writes during the flush re-enqueue.
        flushEnqueued <- false

        if journalCount > 0 then
            // Replay in write order; the scratch sets hold the net delta.
            scratchAdds.Clear()
            scratchRems.Clear()
            let mutable i = 0

            while i < journalCount do
                let struct (item, isAdd) = journal[i]

                if isAdd then
                    if not (scratchRems.Remove item) && not (data.Contains item) then
                        scratchAdds.Add item |> ignore
                elif not (scratchAdds.Remove item) && data.Contains item then
                    scratchRems.Add item |> ignore

                i <- i + 1

            outDelta.Clear()

            for item in scratchAdds do
                data.Add item |> ignore
                outDelta.Adds <- Collections.bufferAppend outDelta.Adds item

            for item in scratchRems do
                data.Remove item |> ignore
                outDelta.Rems <- Collections.bufferAppend outDelta.Rems item

            journalCount <- 0
            this.PushAndMark()

    /// <summary>Replaces the whole set. Last write wins inside a transaction.</summary>
    member this.Set(newValue: seq<'T>) =
        let ctx = GraphContext.Default
        ctx.ClaimOwner()

        try
            if ctx.TxActive then
                pendingValue <- ValueSome newValue
                // A full replace discards the journaled deltas of this batch.
                journalCount <- 0

                if not flushEnqueued then
                    flushEnqueued <- true
                    ctx.TxBuffer.Enqueue(this :> ICommit)
            else
                this.Apply newValue
        finally
            ctx.ReleaseOwner()

    /// <summary>Adds an element. No-op when already present.</summary>
    member this.Add(item: 'T) =
        let ctx = GraphContext.Default
        ctx.ClaimOwner()

        try
            if ctx.TxActive then
                Collections.ensureCapacity &journal journalCount
                journal[journalCount] <- struct (item, true)
                journalCount <- journalCount + 1

                if not flushEnqueued then
                    flushEnqueued <- true
                    ctx.TxBuffer.Enqueue(this :> ICommit)
            else
                this.ApplyAndFlush(item, true)
        finally
            ctx.ReleaseOwner()

    /// <summary>Removes an element. No-op when absent.</summary>
    member this.Remove(item: 'T) =
        let ctx = GraphContext.Default
        ctx.ClaimOwner()

        try
            if ctx.TxActive then
                Collections.ensureCapacity &journal journalCount
                journal[journalCount] <- struct (item, false)
                journalCount <- journalCount + 1

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

            this.CommitJournal()

        member this.Abort() =
            pendingValue <- ValueNone
            journalCount <- 0
            flushEnqueued <- false

    interface IAdaptiveSet<'T> with
        member this.GetValue() =
            let ctx = GraphContext.Default
            ctx.ClaimOwner()

            try
                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) version
                data :> IReadOnlySet<'T>
            finally
                ctx.ReleaseOwner()

        member _.Version = version

    interface IDisposable with
        member _.Dispose() = ()

    /// Internal. Number of registered derived sinks (tests).
    member internal _.SinkCount = sinks.Count

    interface ISetSinkRegistry with
        member this.AddSetSink(sink) = Collections.addSink &sinks sink

        member this.RemoveSetSink(sink) = Collections.removeSink &sinks sink

    interface IEdgeTarget with
        member _.EdgeCount = edges.Count
        member _.AddEdge(parent: IAdaptiveNode, depIndex: int) = edges.Add(parent, depIndex)
        member _.RemoveEdgeAt(index: int) = edges.RemoveAt(index)

/// <summary>
/// A changeable map: the writable source of an adaptive map. Reads and writes
/// are confined to the owner thread. See <see cref="ChangeableSet&lt;'T&gt;"/>
/// for the transaction and view contracts.
/// </summary>
type ChangeableMap<'K, 'V when 'K: equality>(initial: seq<'K * 'V>) =
    let mutable version = 0L
    let data = Dictionary<'K, 'V>()
    let edges = ParentEdges()
    let mutable sinks = SinkList.Create()
    let mutable outDelta = MapDelta<'K, 'V>.Create()
    // Ordered transaction journal; replayed at commit for a net delta.
    // A remove entry carries Unchecked.defaultof<'V>.
    let mutable journal: struct ('K * 'V * bool)[] = Array.zeroCreate 16
    let mutable journalCount = 0
    // Scratch state for the net-delta computation. Reused; zero allocation.
    let mutable scratchSets = Dictionary<'K, 'V>()
    let mutable scratchRems = HashSet<'K>()
    let mutable flushEnqueued = false
    // Pending full replace for Set inside a transaction. Last write wins.
    let mutable pendingValue: seq<'K * 'V> voption = ValueNone

    do
        for (k, v) in initial do
            data[k] <- v

    member private this.PushAndMark() =
        if not outDelta.IsEmpty then
            version <- version + 1L
            Collections.pushAndMarkMap outDelta sinks edges
            outDelta.Clear()

    member private this.Apply(newValue: seq<'K * 'V>) =
        scratchSets.Clear()

        for (k, v) in newValue do
            scratchSets[k] <- v

        outDelta.Clear()

        for KeyValue(k, _) in data do
            if not (scratchSets.ContainsKey k) then
                outDelta.Rems <- Collections.bufferAppend outDelta.Rems k

        // Apply the removals (after the scan: data is not mutated while iterated).
        for i in 0 .. outDelta.Rems.Count - 1 do
            data.Remove outDelta.Rems.Items[i] |> ignore

        for KeyValue(k, v) in scratchSets do
            let mutable old = Unchecked.defaultof<'V>

            if data.TryGetValue(k, &old) && EqualityComparer<'V>.Default.Equals(old, v) then
                ()
            else
                data[k] <- v
                outDelta.Sets <- Collections.bufferAppend outDelta.Sets (struct (k, v))

        this.PushAndMark()

    member private this.ApplyAndFlush(key: 'K, valueToSet: 'V, isRemove: bool) =
        let mutable changed = false

        if isRemove then
            changed <- data.Remove key
        else
            let mutable existing = Unchecked.defaultof<'V>

            if
                data.TryGetValue(key, &existing)
                && EqualityComparer<'V>.Default.Equals(existing, valueToSet)
            then
                ()
            else
                data[key] <- valueToSet
                changed <- true

        if changed then
            outDelta.Clear()

            if isRemove then
                outDelta.Rems <- Collections.bufferAppend outDelta.Rems key
            else
                outDelta.Sets <- Collections.bufferAppend outDelta.Sets (struct (key, valueToSet))

            this.PushAndMark()

    member private this.CommitJournal() =
        // The current batch's flush is already enqueued (we are running from
        // it); resetting now lets reentrant writes during the flush re-enqueue.
        flushEnqueued <- false

        if journalCount > 0 then
            // Replay in write order; the scratch state holds the net delta.
            scratchSets.Clear()
            scratchRems.Clear()
            let mutable i = 0

            while i < journalCount do
                let struct (k, v, isSet) = journal[i]

                if isSet then
                    scratchRems.Remove k |> ignore
                    scratchSets[k] <- v
                elif not (scratchSets.Remove k) && data.ContainsKey k then
                    scratchRems.Add k |> ignore

                i <- i + 1

            outDelta.Clear()

            for KeyValue(k, v) in scratchSets do
                let mutable old = Unchecked.defaultof<'V>

                if data.TryGetValue(k, &old) && EqualityComparer<'V>.Default.Equals(old, v) then
                    ()
                else
                    data[k] <- v
                    outDelta.Sets <- Collections.bufferAppend outDelta.Sets (struct (k, v))

            for k in scratchRems do
                data.Remove k |> ignore
                outDelta.Rems <- Collections.bufferAppend outDelta.Rems k

            journalCount <- 0
            this.PushAndMark()

    /// <summary>Adds or updates an entry. No-op when the value is unchanged.</summary>
    member this.AddOrUpdate (key: 'K) (valueToSet: 'V) =
        let ctx = GraphContext.Default
        ctx.ClaimOwner()

        try
            if ctx.TxActive then
                Collections.ensureCapacity &journal journalCount
                journal[journalCount] <- struct (key, valueToSet, true)
                journalCount <- journalCount + 1

                if not flushEnqueued then
                    flushEnqueued <- true
                    ctx.TxBuffer.Enqueue(this :> ICommit)
            else
                this.ApplyAndFlush(key, valueToSet, false)
        finally
            ctx.ReleaseOwner()

    /// <summary>Removes an entry. No-op when absent.</summary>
    member this.Remove(key: 'K) =
        let ctx = GraphContext.Default
        ctx.ClaimOwner()

        try
            if ctx.TxActive then
                Collections.ensureCapacity &journal journalCount
                journal[journalCount] <- struct (key, Unchecked.defaultof<'V>, false)
                journalCount <- journalCount + 1

                if not flushEnqueued then
                    flushEnqueued <- true
                    ctx.TxBuffer.Enqueue(this :> ICommit)
            else
                this.ApplyAndFlush(key, Unchecked.defaultof<'V>, true)
        finally
            ctx.ReleaseOwner()

    /// <summary>Replaces the whole map. Last write wins inside a transaction.</summary>
    member this.Set(newValue: seq<'K * 'V>) =
        let ctx = GraphContext.Default
        ctx.ClaimOwner()

        try
            if ctx.TxActive then
                pendingValue <- ValueSome newValue
                // A full replace discards the journaled deltas of this batch.
                journalCount <- 0

                if not flushEnqueued then
                    flushEnqueued <- true
                    ctx.TxBuffer.Enqueue(this :> ICommit)
            else
                this.Apply newValue
        finally
            ctx.ReleaseOwner()

    interface ICommit with
        member this.Commit() =
            match pendingValue with
            | ValueSome newValue ->
                pendingValue <- ValueNone
                this.Apply newValue
            | ValueNone -> ()

            this.CommitJournal()

        member this.Abort() =
            pendingValue <- ValueNone
            journalCount <- 0
            flushEnqueued <- false

    interface IAdaptiveMap<'K, 'V> with
        member this.GetValue() =
            let ctx = GraphContext.Default
            ctx.ClaimOwner()

            try
                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) version
                data :> IReadOnlyDictionary<'K, 'V>
            finally
                ctx.ReleaseOwner()

        member _.Version = version

    interface IDisposable with
        member _.Dispose() = ()

    /// Internal. Number of registered derived sinks (tests).
    member internal _.SinkCount = sinks.Count

    interface IMapSinkRegistry with
        member this.AddMapSink(sink) = Collections.addSink &sinks sink

        member this.RemoveMapSink(sink) = Collections.removeSink &sinks sink

    interface IEdgeTarget with
        member _.EdgeCount = edges.Count
        member _.AddEdge(parent: IAdaptiveNode, depIndex: int) = edges.Add(parent, depIndex)
        member _.RemoveEdgeAt(index: int) = edges.RemoveAt(index)
