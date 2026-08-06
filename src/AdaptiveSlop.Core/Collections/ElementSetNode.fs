namespace AdaptiveSlop.Core

open System
open System.Collections.Generic

// =============================================================================
// Per-element adaptive set node (docs/2026-08-05-MAPA-DESIGN.md)
//
// ASet.mapA / chooseA / filterA share one node: the mapping returns an
// adaptive value per element, cached by input element. Structural source
// changes go through the journal (existing pattern, cache maintained per op);
// element-aval changes go through a version scan of the cache (the dirty
// gate: a precise elementDirty flag when every aval is registered, else the
// write-generation gate).
//
// Element-aval registration (Observation pattern): on cache insert when the
// node is observed, and for all cached avals when the node gains its first
// parent edge (RegisterAll). A registered aval marks this node through the
// aval's own edge chain (cval.Apply -> MarkFrom(cval.edges) -> aval.MarkDirty
// -> PushDirty(aval.edges) -> this.MarkDirty). Avals that do not implement
// IEdgeTarget cannot mark: regComplete goes false and the generation gate
// covers the read path.
// =============================================================================

/// <summary>
/// Maps every element of a set to an adaptive value (or chooses/filters, when
/// the aval's value is <c>ValueNone</c> to drop the element). Duplicate
/// output values share one reference count (refcounted set).
/// </summary>
type ElementSetNode<'T, 'U when 'T: equality and 'U: equality>
    (source: IAdaptiveSet<'T>, [<InlineIfLambda>] mapping: 'T -> aval<'U voption>) =
    let mutable state = SetNodeState<'T, 'U>.Create(1)
    let mutable cache = Dictionary<'T, ElementEntry<'U>>()
    let mutable nextId = 0
    let mutable initialized = false
    let mutable disposed = false
    // Precise element-dirty flag: set by the mark chain from registered avals.
    let mutable elementDirty = false
    // Registration completeness: false when unobserved or any cached aval does
    // not implement IEdgeTarget (marks can be missed; the generation gate
    // covers the read path).
    let mutable regComplete = false
    // Write generation at the last scan. The generation gate fires when it
    // moved since (unobserved / incomplete registration).
    let mutable lastDrainWriteGen = -1L

    member private this.Register() =
        match box source with
        | :? ISetSinkRegistry as r -> r.AddSetSink(box (this :> ISetDeltaSink<'T>))
        | _ -> ()

    member private this.Unregister() =
        match box source with
        | :? ISetSinkRegistry as r -> r.RemoveSetSink(box (this :> ISetDeltaSink<'T>))
        | _ -> ()

    /// Register with the aval: the aval's writes then mark this node. Returns
    /// the entry with its Id and edge index filled in. Unregistered avals
    /// (not an IEdgeTarget) leave the entry unregistered and mark the
    /// registration incomplete.
    member private this.RegisterEntry(entry: ElementEntry<'U>) : ElementEntry<'U> =
        match box entry.Aval with
        | :? IEdgeTarget as t ->
            let id = nextId
            nextId <- nextId + 1
            ElementEntry(entry.Aval, entry.Version, entry.Last, id, t.AddEdge(this :> IAdaptiveNode, id))
        | _ ->
            regComplete <- false
            entry

    /// Drop the edge into the aval (the cache entry is dropped by the caller).
    member private _.UnregisterEntry(entry: ElementEntry<'U>) =
        if entry.EdgeIndex >= 0 then
            match box entry.Aval with
            | :? IEdgeTarget as t -> t.RemoveEdgeAt(entry.EdgeIndex)
            | _ -> ()

    /// The node gained its first parent edge: register every cached aval so
    /// their writes mark this node (and through it, the parents).
    member private this.RegisterAll() =
        regComplete <- true
        let mutable e = cache.GetEnumerator()

        while e.MoveNext() do
            let kvp = e.Current
            cache[kvp.Key] <- this.RegisterEntry kvp.Value

    /// The node lost its last parent edge: drop every aval edge.
    member private this.UnregisterAll() =
        let mutable e = cache.GetEnumerator()

        while e.MoveNext() do
            let kvp = e.Current
            this.UnregisterEntry kvp.Value
            let entry = kvp.Value
            cache[kvp.Key] <- ElementEntry(entry.Aval, entry.Version, entry.Last, entry.Id, -1)

    /// We were moved in an aval's edge list (another dependent removed):
    /// update the entry's edge index (matched by the Id we passed at
    /// registration).
    member private this.FixEdgeIndex(id: int, parentIndex: int) =
        let mutable e = cache.GetEnumerator()
        let mutable found = false

        while not found && e.MoveNext() do
            let kvp = e.Current

            if kvp.Value.Id = id then
                let mutable entry = kvp.Value
                entry.EdgeIndex <- parentIndex
                cache[kvp.Key] <- entry
                found <- true

    /// The dirty gate: a scan is needed when the precise flag is set, or when
    /// registration is incomplete and a write moved the generation.
    member private _.NeedsElementScan() =
        elementDirty
        || (not regComplete && GraphContext.Default.WriteGeneration <> lastDrainWriteGen)

    member private this.EnsureInitialized() =
        if not initialized then
            // Snapshot first, register between, then load: the mapping is user
            // code that may write to the source (the transient view must not
            // be iterated while it is mutated), and the write must land in
            // the journal (register before the mapping runs). The flag is set
            // last: an exception leaves the node uninitialized.
            let snapshot = HashSet<'T>(source.GetValue())
            this.Register()

            for item in snapshot do
                let aval = mapping item
                // Version read BEFORE the force: a mid-force write leaves the
                // stored version stale, so the next scan re-forces.
                let preV = aval.Version
                let newV = aval.GetValue()
                let entry = ElementEntry(aval, preV, newV, -1, -1)

                match newV with
                | ValueSome u ->
                    let struct (s2, _) = Collections.refAdd state.Set u
                    state.Set <- s2
                | ValueNone -> ()

                cache[item] <- entry

            state.DepVersions[0] <- source.Version
            initialized <- true

    /// Apply the source journal with cache maintenance: removed elements drop
    /// their cache entry (unregistered) and their contribution; added elements
    /// run the mapping, force the aval, and contribute.
    member private this.DrainJournal() =
        let rems = state.Journal.Rems
        let adds = state.Journal.Adds
        let remStart = rems.Count
        let addStart = adds.Count
        let mutable i = 0
        // Consumed counts: entries before these positions were applied and
        // must never be applied again; the throwing entry (and reentrant
        // entries appended live, beyond the start bounds) survive.
        let mutable remsDone = 0
        let mutable addsDone = 0

        try
            while i < remStart do
                let x = rems.Items[i]
                let mutable entry = Unchecked.defaultof<ElementEntry<'U>>

                if cache.TryGetValue(x, &entry) then
                    this.UnregisterEntry entry
                    cache.Remove x |> ignore

                    match entry.Last with
                    | ValueSome old ->
                        let struct (s2, removed) = Collections.refRemove state.Set old
                        state.Set <- s2

                        if removed then
                            state.Out.Rems <- Collections.bufferAppend state.Out.Rems old
                    | ValueNone -> ()

                i <- i + 1
                remsDone <- i

            i <- 0

            while i < addStart do
                let x = adds.Items[i]
                let aval = mapping x
                // Version read BEFORE the force (see EnsureInitialized).
                let preV = aval.Version
                let newV = aval.GetValue()
                let mutable entry = ElementEntry(aval, preV, newV, -1, -1)

                if state.Edges.Count > 0 then
                    entry <- this.RegisterEntry entry

                match newV with
                | ValueSome u ->
                    let struct (s2, added) = Collections.refAdd state.Set u
                    state.Set <- s2

                    if added then
                        state.Out.Adds <- Collections.bufferAppend state.Out.Adds u
                | ValueNone -> ()

                cache[x] <- entry
                i <- i + 1
                addsDone <- i
        finally
            // Compact against the LIVE journal counts: reentrant writes during
            // the mapping append to the live journal (a stale copy would drop
            // them). Consumed entries are dropped; the throwing entry survives.
            let remLive = state.Journal.Rems.Count

            if remLive > remsDone then
                Array.Copy(state.Journal.Rems.Items, remsDone, state.Journal.Rems.Items, 0, remLive - remsDone)
                state.Journal.Rems.Count <- remLive - remsDone
            else
                state.Journal.Rems.Count <- 0

            let addLive = state.Journal.Adds.Count

            if addLive > addsDone then
                Array.Copy(state.Journal.Adds.Items, addsDone, state.Journal.Adds.Items, 0, addLive - addsDone)
                state.Journal.Adds.Count <- addLive - addsDone
            else
                state.Journal.Adds.Count <- 0

    /// The element scan: re-read every cached aval's version; force the
    /// changed ones and apply the contribution change through the refcounted
    /// set. Self-healing: a reentrant write mid-scan keeps the dirty flag set
    /// so the next read rescans.
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

                match entry.Last with
                | ValueSome old ->
                    match newV with
                    | ValueSome newU ->
                        if EqualityComparer<'U>.Default.Equals(old, newU) then
                            () // version bumped to an equal value: no delta
                        else
                            let struct (s2, removed) = Collections.refRemove state.Set old
                            state.Set <- s2

                            if removed then
                                state.Out.Rems <- Collections.bufferAppend state.Out.Rems old

                            let struct (s3, added) = Collections.refAdd state.Set newU
                            state.Set <- s3

                            if added then
                                state.Out.Adds <- Collections.bufferAppend state.Out.Adds newU

                            changed <- true
                    | ValueNone ->
                        let struct (s2, removed) = Collections.refRemove state.Set old
                        state.Set <- s2

                        if removed then
                            state.Out.Rems <- Collections.bufferAppend state.Out.Rems old

                        changed <- true
                | ValueNone ->
                    match newV with
                    | ValueSome newU ->
                        let struct (s2, added) = Collections.refAdd state.Set newU
                        state.Set <- s2

                        if added then
                            state.Out.Adds <- Collections.bufferAppend state.Out.Adds newU

                        changed <- true
                    | ValueNone -> ()

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

        lastDrainWriteGen <- GraphContext.Default.WriteGeneration

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
                Collections.pushSetDelta &state.Sinks state.Out
                state.Out.Clear()
        finally
            ctx.TxActive <- wasActive

        if not wasActive then
            ctx.DeliverNotifications()

    interface ISetDeltaSink<'T> with
        member this.OnDeltas(adds: 'T[], addCnt: int, rems: 'T[], remCnt: int) =
            if not disposed then
                Collections.journalAppendSet &state.Journal adds addCnt rems remCnt
                state.Version <- state.Version + 1L
                GraphContext.Default.MarkFrom(state.Edges)

    interface IAdaptiveSet<'U> with
        member this.GetValue() =
            let ctx = GraphContext.Default
            ctx.ClaimOwner()

            try
                if disposed then
                    invalidOp "This adaptive set has been disposed."

                this.EnsureInitialized()

                if source.Version <> state.DepVersions[0] then
                    source.GetValue() |> ignore
                    state.DepVersions[0] <- source.Version

                this.Process()
                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) state.Version
                state.Set.Data :> IReadOnlySet<'U>
            finally
                ctx.ReleaseOwner()

        member this.Version =
            // Dirty indicator for version-checking readers: a pending element
            // scan is reported as a version bump (the scan itself advances the
            // version only when the output changes).
            if this.NeedsElementScan() then
                state.Version + 1L
            else
                state.Version

    interface IDisposable with
        member this.Dispose() =
            if not disposed then
                disposed <- true
                this.Unregister()
                this.UnregisterAll()
                Collections.clearSinks &state.Sinks

    interface ISetSinkRegistry with
        member this.AddSetSink(sink) = Collections.addSink &state.Sinks sink

        member this.RemoveSetSink(sink) =
            Collections.removeSink &state.Sinks sink

    interface IEdgeTarget with
        member _.EdgeCount = state.Edges.Count

        member this.AddEdge(parent: IAdaptiveNode, depIndex: int) =
            let index = state.Edges.Add(parent, depIndex)

            if state.Edges.Count = 1 then
                this.RegisterAll()

            index

        member this.RemoveEdgeAt(index: int) =
            state.Edges.RemoveAt(index)

            if state.Edges.Count = 0 then
                this.UnregisterAll()

    interface IAdaptiveNode with
        member this.MarkDirty() =
            elementDirty <- true
            let ctx = GraphContext.Default

            for i in 0 .. state.Edges.Count - 1 do
                ctx.PushDirty(state.Edges[i])

        member this.SetDepSlot(depIndex: int, parentIndex: int) =
            this.FixEdgeIndex(depIndex, parentIndex)

        member _.OnFirstParent() = ()
        member _.OnLastParent() = ()
