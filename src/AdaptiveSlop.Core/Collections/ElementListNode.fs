namespace AdaptiveSlop.Core

open System
open System.Collections.Generic

// =============================================================================
// Per-element adaptive list node (docs/2026-08-05-MAPA-DESIGN.md)
//
// AList.mapA / chooseA / filterA share one node: the mapping returns an aval
// per element, cached by INPUT position (parallel to the source, covering
// non-surviving elements). Structural source changes go through the journal
// (the FilterMapListNode pattern: LowerBound/Contains translation, tail
// fixups, output subsequence); element-aval changes go through a version
// scan of the cache that emits Update/Insert/Remove ops at the translated
// output positions. The mapping receives the input position (the i-variants
// pass it through; the plain variants ignore it).
// =============================================================================

/// <summary>
/// Maps every element of a list to an adaptive value (or chooses/filters,
/// when the aval's value is <c>ValueNone</c> to drop the element).
/// </summary>
/// <remarks>
/// The mapping receives the input position (FDA parity: the <c>i</c>
/// variants; the plain variants ignore it). An update on a non-surviving
/// element that now passes the mapping inserts it into the output; an update
/// on a surviving element that now fails removes it (FDA choose semantics,
/// docs/ALIST-DESIGN.md §3.4).
/// </remarks>
type ElementListNode<'T, 'U>(source: IAdaptiveList<'T>, [<InlineIfLambda>] mapping: int -> 'T -> aval<'U voption>) =
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
    // Per-element cache, parallel to the input (cache[p] = the aval of the
    // element at input position p).
    let mutable cache = ResizeArray<ElementEntry<'U>>()
    let mutable nextId = 0
    // Precise element-dirty flag: set by the mark chain from registered avals.
    let mutable elementDirty = false
    // Registration completeness (see ElementSetNode.fs).
    let mutable regComplete = false
    // Write generation at the last scan (the generation gate).
    let mutable lastDrainWriteGen = -1L

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

    /// The node gained its first parent edge: register every cached aval.
    member private this.RegisterAll() =
        regComplete <- true

        for i in 0 .. cache.Count - 1 do
            cache[i] <- this.RegisterEntry cache[i]

    /// The node lost its last parent edge: drop every aval edge.
    member private this.UnregisterAll() =
        for i in 0 .. cache.Count - 1 do
            let entry = cache[i]
            this.UnregisterEntry entry
            cache[i] <- ElementEntry(entry.Aval, entry.Version, entry.Last, entry.Id, -1)

    /// We were moved in an aval's edge list (another dependent removed):
    /// update the entry's edge index (matched by the Id we passed at
    /// registration).
    member private this.FixEdgeIndex(id: int, parentIndex: int) =
        let mutable i = 0
        let mutable found = false

        while not found && i < cache.Count do
            if cache[i].Id = id then
                let mutable entry = cache[i]
                entry.EdgeIndex <- parentIndex
                cache[i] <- entry
                found <- true
            else
                i <- i + 1

    /// The dirty gate: a scan is needed when the precise flag is set, or when
    /// registration is incomplete and a write moved the generation.
    member private _.NeedsElementScan() =
        elementDirty
        || (not regComplete && GraphContext.Default.WriteGeneration <> lastDrainWriteGen)

    /// Load the cache and the output from a snapshot of the source.
    member private this.Load(snapshot: ResizeArray<'T>) =
        for i in 0 .. snapshot.Count - 1 do
            let aval = mapping i snapshot[i]
            // Version read BEFORE the force: a mid-force write leaves the
            // stored version stale, so the next scan re-forces.
            let preV = aval.Version
            let newV = aval.GetValue()
            let entry = ElementEntry(aval, preV, newV, -1, -1)

            match newV with
            | ValueSome u ->
                output.Add u
                inputPositions.Add i
            | ValueNone -> ()

            cache.Add entry

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

    /// Apply the source journal with cache maintenance: inserts/updates run
    /// the mapping and force the aval; removes drop the cache entry
    /// (unregistered). The output translation mirrors FilterMapListNode.
    member private this.DrainJournal() =
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
                    let aval = mapping p op.Value
                    // Version read BEFORE the force (see Load).
                    let preV = aval.Version
                    let newV = aval.GetValue()
                    let mutable entry = ElementEntry(aval, preV, newV, -1, -1)

                    if edges.Count > 0 then
                        entry <- this.RegisterEntry entry

                    match newV with
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

                    cache.Insert(p, entry)
                    inputCount <- inputCount + 1
                | ListOpKind.Remove ->
                    this.UnregisterEntry cache[p]
                    cache.RemoveAt p

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
                    this.UnregisterEntry cache[p]
                    let aval = mapping p op.Value
                    // Version read BEFORE the force (see Load).
                    let preV = aval.Version
                    let newV = aval.GetValue()
                    let mutable entry = ElementEntry(aval, preV, newV, -1, -1)

                    if edges.Count > 0 then
                        entry <- this.RegisterEntry entry

                    match newV with
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

                    cache[p] <- entry
                | _ -> ()

                i <- i + 1
                opsDone <- i
        finally
            // Compact against the LIVE journal count: reentrant writes during
            // the mapping append to the live journal (a stale copy would drop
            // them). Consumed ops are dropped; the throwing op survives.
            let live = journal.Ops.Count

            if live > opsDone then
                Array.Copy(journal.Ops.Items, opsDone, journal.Ops.Items, 0, live - opsDone)
                journal.Ops.Count <- live - opsDone
            else
                journal.Ops.Count <- 0

    /// The element scan: re-read every cached aval's version; force the
    /// changed ones and translate the contribution change into an
    /// Update/Insert/Remove at the output position. The scan runs in input
    /// position order; membership flips do not move input coordinates, so the
    /// stored input positions need no tail fixup (unlike the journal drain).
    /// Self-healing: a reentrant write mid-scan keeps the dirty flag set so
    /// the next read rescans.
    member private this.ScanElements() =
        let startGen = GraphContext.Default.WriteGeneration
        let mutable changed = false

        for i in 0 .. cache.Count - 1 do
            let mutable entry = cache[i]
            // Version read BEFORE the force (see Load).
            let preV = entry.Aval.Version

            if preV <> entry.Version then
                let aval = entry.Aval
                let newV = aval.GetValue()
                let j = this.LowerBound i
                let present = this.Contains(i, j)

                // Nested match, NOT `match a, b with`: the two-value match
                // compiles to a reference tuple (docs/archive/2026-08-04-
                // BISECT-NOTES.md Cause 1 — measured 32 B per call).
                match entry.Last with
                | ValueSome old ->
                    match newV with
                    | ValueSome u ->
                        if EqualityComparer<'U>.Default.Equals(old, u) then
                            () // version bumped to an equal value: no delta
                        else
                            // Surviving element, new value: an update at its
                            // output position (present by the invariant).
                            output[j] <- u
                            out.Ops <- Collections.bufferAppend out.Ops (ListOp(ListOpKind.Update, j, u, 0uy))

                            changed <- true
                    | ValueNone ->
                        output.RemoveAt j
                        inputPositions.RemoveAt j

                        // No tail fixup: the scan changes only OUTPUT
                        // membership; input coordinates are unchanged (unlike
                        // the journal drain, where an insert/remove shifts
                        // later elements).
                        out.Ops <-
                            Collections.bufferAppend
                                out.Ops
                                (ListOp(ListOpKind.Remove, j, Unchecked.defaultof<'U>, 0uy))

                        changed <- true
                | ValueNone ->
                    match newV with
                    | ValueSome u ->
                        output.Insert(j, u)
                        inputPositions.Insert(j, i)

                        // No tail fixup (see the remove case).
                        out.Ops <- Collections.bufferAppend out.Ops (ListOp(ListOpKind.Insert, j, u, 0uy))
                        changed <- true
                    | ValueNone -> ()

                entry.Version <- preV
                entry.Last <- newV
                cache[i] <- entry

        if changed then
            version <- version + 1L

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
            if not journal.IsEmpty then
                this.DrainJournal()

            if this.NeedsElementScan() then
                this.ScanElements()

            if not out.IsEmpty then
                version <- version + 1L
                Collections.pushListDelta &sinks out
                out.Clear()
        finally
            ctx.TxActive <- wasActive

        if not wasActive then
            ctx.DeliverNotifications()

        // Capture AFTER the push: the downstream sink's MarkFrom advances the
        // write generation during the delta delivery. Capturing in the scan
        // (before the push) left the gate permanently open and the Version
        // dirty indicator inflated, so a version-gated consumer recorded a
        // phantom version and missed the next change.
        lastDrainWriteGen <- GraphContext.Default.WriteGeneration

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

                this.Process()
                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) version
                output :> IReadOnlyList<'U>
            finally
                ctx.ReleaseOwner()

        member this.Version =
            // Dirty indicator for version-checking readers (see ElementSetNode).
            if this.NeedsElementScan() then version + 1L else version

    interface IDisposable with
        member this.Dispose() =
            if not disposed then
                disposed <- true
                this.Unregister()
                this.UnregisterAll()
                Collections.clearSinks &sinks

    interface IListSinkRegistry with
        member this.AddListSink(sink) = Collections.addSink &sinks sink

        member this.RemoveListSink(sink) = Collections.removeSink &sinks sink

    interface IEdgeTarget with
        member _.EdgeCount = edges.Count

        member this.AddEdge(parent: IAdaptiveNode, depIndex: int) =
            let index = edges.Add(parent, depIndex)

            if edges.Count = 1 then
                this.RegisterAll()

            index

        member this.RemoveEdgeAt(index: int) =
            edges.RemoveAt(index)

            if edges.Count = 0 then
                this.UnregisterAll()

    interface IAdaptiveNode with
        member this.MarkDirty() =
            elementDirty <- true
            let ctx = GraphContext.Default

            for i in 0 .. edges.Count - 1 do
                ctx.PushDirty(edges[i])

        member this.SetDepSlot(depIndex: int, parentIndex: int) =
            this.FixEdgeIndex(depIndex, parentIndex)

        member _.OnFirstParent() = ()
        member _.OnLastParent() = ()
