namespace AdaptiveSlop.Core

open System
open System.Collections.Generic

// =============================================================================
// Per-element adaptive map node (docs/2026-08-05-MAPA-DESIGN.md)
//
// AMap.mapA / chooseA / filterA share one node, the keyed sibling of
// ElementSetNode: the mapping returns an aval per entry, cached by key. See
// ElementSetNode.fs for the dirty gate and scan contracts; this node differs
// only in the journal shape (map deltas) and the output state (a plain keyed
// dictionary).
// =============================================================================

/// <summary>
/// Maps every entry of a map to an adaptive value (or chooses/filters, when
/// the aval's value is <c>ValueNone</c> to drop the entry).
/// </summary>
type ElementMapNode<'K, 'V, 'U when 'K: equality>
    (source: IAdaptiveMap<'K, 'V>, [<InlineIfLambda>] mapping: 'K -> 'V -> aval<'U voption>) =
    let mutable state = MapNodeState<'K, 'V, 'U>.Create(1)
    let mutable cache = Dictionary<'K, ElementEntry<'U>>()
    let mutable initialized = false
    let mutable disposed = false
    let mutable elementDirty = false
    let mutable lastDrainWriteGen = -1L

    member private this.Register() =
        match box source with
        | :? IMapSinkRegistry as r -> r.AddMapSink(box (this :> IMapDeltaSink<'K, 'V>))
        | _ -> ()

    member private this.Unregister() =
        match box source with
        | :? IMapSinkRegistry as r -> r.RemoveMapSink(box (this :> IMapDeltaSink<'K, 'V>))
        | _ -> ()

    /// The dirty gate: a scan is needed when the self-healing flag is set, or
    /// when a write moved the generation since the last scan.
    member private _.NeedsElementScan() =
        elementDirty || GraphContext.Default.WriteGeneration <> lastDrainWriteGen

    member private this.EnsureInitialized() =
        if not initialized then
            // Snapshot first, register between, then load (see ElementSetNode).
            let snapshot = Dictionary<'K, 'V>(source.GetValue())
            this.Register()

            for KeyValue(k, v) in snapshot do
                let aval = mapping k v
                // Version read BEFORE the force: a mid-force write leaves the
                // stored version stale, so the next scan re-forces.
                let preV = aval.Version
                let newV = aval.GetValue()
                let entry = ElementEntry(aval, preV, newV)

                match newV with
                | ValueSome u -> state.Data[k] <- u
                | ValueNone -> ()

                cache[k] <- entry

            state.DepVersions[0] <- source.Version
            initialized <- true

    /// Apply the source journal with cache maintenance: removed keys drop
    /// their cache entry (unregistered) and their output; set keys run the
    /// mapping, force the aval, and replace the output.
    member private this.DrainJournal() =
        let rems = state.Journal.Rems
        let sets = state.Journal.Sets
        let remStart = rems.Count
        let setStart = sets.Count
        let mutable i = 0
        // Consumed counts: entries before these positions were applied and
        // must never be applied again; the throwing entry (and reentrant
        // entries appended live, beyond the start bounds) survive.
        let mutable remsDone = 0
        let mutable setsDone = 0

        try
            while i < remStart do
                let k = rems.Items[i]
                cache.Remove k |> ignore

                if state.Data.Remove k then
                    state.Out.Rems <- Collections.bufferAppend state.Out.Rems k

                i <- i + 1
                remsDone <- i

            i <- 0

            while i < setStart do
                let struct (k, v) = sets.Items[i]
                let aval = mapping k v
                // Version read BEFORE the force (see EnsureInitialized).
                let preV = aval.Version
                let newV = aval.GetValue()
                let newEntry = ElementEntry(aval, preV, newV)

                match newV with
                | ValueSome u ->
                    let mutable old = Unchecked.defaultof<'U>

                    if state.Data.TryGetValue(k, &old) && EqualityComparer<'U>.Default.Equals(old, u) then
                        ()
                    else
                        state.Data[k] <- u
                        state.Out.Sets <- Collections.bufferAppend state.Out.Sets (struct (k, u))
                | ValueNone ->
                    if state.Data.Remove k then
                        state.Out.Rems <- Collections.bufferAppend state.Out.Rems k

                cache[k] <- newEntry
                i <- i + 1
                setsDone <- i
        finally
            // Compact against the LIVE journal counts (see ElementSetNode).
            let remLive = state.Journal.Rems.Count

            if remLive > remsDone then
                Array.Copy(state.Journal.Rems.Items, remsDone, state.Journal.Rems.Items, 0, remLive - remsDone)
                state.Journal.Rems.Count <- remLive - remsDone
            else
                state.Journal.Rems.Count <- 0

            let setLive = state.Journal.Sets.Count

            if setLive > setsDone then
                Array.Copy(state.Journal.Sets.Items, setsDone, state.Journal.Sets.Items, 0, setLive - setsDone)
                state.Journal.Sets.Count <- setLive - setsDone
            else
                state.Journal.Sets.Count <- 0

    /// The element scan: re-read every cached aval's version; force the
    /// changed ones and apply the contribution change. Self-healing: a
    /// reentrant write mid-scan keeps the dirty flag set so the next read
    /// rescans.
    member private this.ScanElements() =
        let startGen = GraphContext.Default.WriteGeneration
        let mutable changed = false
        let mutable e = cache.GetEnumerator()

        while e.MoveNext() do
            let kvp = e.Current
            let mutable entry = kvp.Value
            // Version read BEFORE the force (see EnsureInitialized).
            let preV = entry.Aval.Version

            if preV <> entry.Version then
                let newV = entry.Aval.GetValue()

                match newV with
                | ValueSome u ->
                    let mutable old = Unchecked.defaultof<'U>

                    if
                        state.Data.TryGetValue(kvp.Key, &old)
                        && EqualityComparer<'U>.Default.Equals(old, u)
                    then
                        () // version bumped to an equal value: no delta
                    else
                        state.Data[kvp.Key] <- u
                        state.Out.Sets <- Collections.bufferAppend state.Out.Sets (struct (kvp.Key, u))
                        changed <- true
                | ValueNone ->
                    if state.Data.Remove kvp.Key then
                        state.Out.Rems <- Collections.bufferAppend state.Out.Rems kvp.Key
                        changed <- true

                entry.Version <- preV
                entry.Last <- newV
                cache[kvp.Key] <- entry

        if changed then
            state.Version <- state.Version + 1L

        if GraphContext.Default.WriteGeneration <> startGen then
            // A reentrant write landed mid-scan: the output may be stale, keep
            // the flag so the next read rescans.
            elementDirty <- true
        else
            elementDirty <- false

    /// Drain the journal, then the element scan, then push the accumulated
    /// output delta once, with notification delivery deferred.
    member private this.Process() =
        let ctx = GraphContext.Default
        let wasActive = ctx.TxActive
        ctx.TxActive <- true

        try
            if not state.Journal.IsEmpty then
                this.DrainJournal()

            if this.NeedsElementScan() then
                this.ScanElements()

            if not state.Out.IsEmpty then
                state.Version <- state.Version + 1L
                Collections.pushMapDelta &state.Sinks state.Out
                state.Out.Clear()
        finally
            ctx.TxActive <- wasActive

        // Capture AFTER the push: the downstream sink's write notification
        // advances the write generation during the delta delivery. Capturing
        // in the scan (before the push) left the gate permanently open and
        // the Version dirty indicator inflated, so a version-gated consumer
        // recorded a phantom version and missed the next change.
        lastDrainWriteGen <- GraphContext.Default.WriteGeneration

    interface IMapDeltaSink<'K, 'V> with
        member this.OnDeltas(setEntries: struct ('K * 'V)[], setCnt: int, removedKeys: 'K[], remCnt: int) =
            if not disposed then
                Collections.journalAppendMap &state.Journal setEntries setCnt removedKeys remCnt
                state.Version <- state.Version + 1L
                GraphContext.Default.BumpWriteGeneration()

    interface IAdaptiveMap<'K, 'U> with
        member this.GetValue() =
            let ctx = GraphContext.Default
            ctx.ClaimOwner()

            try
                if disposed then
                    invalidOp "This adaptive map has been disposed."

                this.EnsureInitialized()

                if source.Version <> state.DepVersions[0] then
                    source.GetValue() |> ignore
                    state.DepVersions[0] <- source.Version

                this.Process()
                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) state.Version
                state.Data :> IReadOnlyDictionary<'K, 'U>
            finally
                ctx.ReleaseOwner()

        member this.Version =
            // Dirty indicator for version-checking readers (see ElementSetNode).
            if this.NeedsElementScan() then
                state.Version + 1L
            else
                state.Version

    interface IDisposable with
        member this.Dispose() =
            if not disposed then
                disposed <- true
                this.Unregister()
                Collections.clearSinks &state.Sinks

    interface IMapSinkRegistry with
        member this.AddMapSink(sink) = Collections.addSink &state.Sinks sink

        member this.RemoveMapSink(sink) =
            Collections.removeSink &state.Sinks sink
