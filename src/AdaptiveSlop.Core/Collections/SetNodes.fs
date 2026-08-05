namespace AdaptiveSlop.Core

open System
open System.Collections.Generic
open System.Collections.Frozen

// =============================================================================
// AdaptiveSet transform nodes (PLAN.md Section 6.9)
//
// A derived node registers with its dependencies on first read. A dependency
// push appends to the journal, advances the version, and marks the scalar
// parents. Reads cascade over changed dependencies, drain the journal, and
// return a transient view of the internal state. `force` materializes a
// FrozenSet. Disposal unregisters and stops all delta processing.
// =============================================================================

/// <summary>
/// The identity mapping for union-style set drains. Module level and inline:
/// a class-level <c>let f = fun x -> ValueSome x</c> gets generalized and
/// materializes a fresh closure at every use site (measured 24 B per drain).
/// </summary>
module private Id =
    let inline identityV x = ValueSome x

/// <summary>An adaptive set over a fixed, immutable value. The value is computed once, at first read.</summary>
type ConstantSet<'T>([<InlineIfLambda>] create: unit -> FrozenSet<'T>) =
    let value = lazy create ()

    interface IAdaptiveSet<'T> with
        member this.GetValue() =
            AdaptiveRuntime.addDependency (this :> IAdaptiveObject) 0L
            value.Value :> IReadOnlySet<'T>

        member _.Version = 0L

    interface IDisposable with
        member _.Dispose() = ()

/// <summary>
/// Maps every element of a set (or chooses, when the mapping returns
/// <c>ValueNone</c> to drop an element). Duplicate outputs share one reference
/// count.
/// </summary>
type MapSetNode<'T, 'U when 'U: equality>(source: IAdaptiveSet<'T>, [<InlineIfLambda>] mapping: 'T -> 'U voption) =
    let mutable state = SetNodeState<'T, 'U>.Create(1)
    let mutable initialized = false
    let mutable disposed = false

    member private this.Register() =
        match box source with
        | :? ISetSinkRegistry as r -> r.AddSetSink(box (this :> ISetDeltaSink<'T>))
        | _ -> ()

    member private this.Unregister() =
        match box source with
        | :? ISetSinkRegistry as r -> r.RemoveSetSink(box (this :> ISetDeltaSink<'T>))
        | _ -> ()

    member private this.EnsureInitialized() =
        if not initialized then
            // Snapshot first, register between, then run the mapping over the
            // snapshot: the mapping is user code that may write to the source
            // (the transient view must not be iterated while it is mutated),
            // and the write must land in our journal (register before the
            // mapping runs). A dirty source draining during the snapshot read
            // pushes to nobody: no double-apply. The flag is set last: an
            // exception leaves the node uninitialized so the next read retries.
            let snapshot = HashSet<'T>(source.GetValue())
            this.Register()
            Collections.loadRefSet mapping snapshot &state
            state.DepVersions[0] <- source.Version
            initialized <- true

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

                if not state.Journal.IsEmpty then
                    Collections.drainSetPush mapping &state

                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) state.Version
                state.Set.Data :> IReadOnlySet<'U>
            finally
                ctx.ReleaseOwner()

        member _.Version = state.Version

    interface IDisposable with
        member this.Dispose() =
            if not disposed then
                disposed <- true
                this.Unregister()
                Collections.clearSinks &state.Sinks

    interface ISetSinkRegistry with
        member this.AddSetSink(sink) = Collections.addSink &state.Sinks sink

        member this.RemoveSetSink(sink) =
            Collections.removeSink &state.Sinks sink

    interface IEdgeTarget with
        member _.EdgeCount = state.Edges.Count
        member _.AddEdge(parent: IAdaptiveNode, depIndex: int) = state.Edges.Add(parent, depIndex)
        member _.RemoveEdgeAt(index: int) = state.Edges.RemoveAt(index)

/// <summary>Keeps the elements of a set that satisfy a predicate.</summary>
type FilterSetNode<'T when 'T: equality>(source: IAdaptiveSet<'T>, [<InlineIfLambda>] predicate: 'T -> bool) =
    let mapOpt = fun x -> if predicate x then ValueSome x else ValueNone
    let mutable state = SetNodeState<'T, 'T>.Create(1)
    let mutable initialized = false
    let mutable disposed = false

    member private this.Register() =
        match box source with
        | :? ISetSinkRegistry as r -> r.AddSetSink(box (this :> ISetDeltaSink<'T>))
        | _ -> ()

    member private this.Unregister() =
        match box source with
        | :? ISetSinkRegistry as r -> r.RemoveSetSink(box (this :> ISetDeltaSink<'T>))
        | _ -> ()

    member private this.EnsureInitialized() =
        if not initialized then
            // Snapshot first, register between (see MapSetNode.EnsureInitialized).
            // The flag is set last: an exception leaves the node uninitialized.
            let snapshot = HashSet<'T>(source.GetValue())
            this.Register()
            Collections.loadPlainSet mapOpt snapshot &state
            state.DepVersions[0] <- source.Version
            initialized <- true

    interface ISetDeltaSink<'T> with
        member this.OnDeltas(adds: 'T[], addCnt: int, rems: 'T[], remCnt: int) =
            if not disposed then
                Collections.journalAppendSet &state.Journal adds addCnt rems remCnt
                state.Version <- state.Version + 1L
                GraphContext.Default.MarkFrom(state.Edges)

    interface IAdaptiveSet<'T> with
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

                if not state.Journal.IsEmpty then
                    Collections.drainPlainSetPush mapOpt &state

                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) state.Version
                state.Set.Data :> IReadOnlySet<'T>
            finally
                ctx.ReleaseOwner()

        member _.Version = state.Version

    interface IDisposable with
        member this.Dispose() =
            if not disposed then
                disposed <- true
                this.Unregister()
                Collections.clearSinks &state.Sinks

    interface ISetSinkRegistry with
        member this.AddSetSink(sink) = Collections.addSink &state.Sinks sink

        member this.RemoveSetSink(sink) =
            Collections.removeSink &state.Sinks sink

    interface IEdgeTarget with
        member _.EdgeCount = state.Edges.Count
        member _.AddEdge(parent: IAdaptiveNode, depIndex: int) = state.Edges.Add(parent, depIndex)
        member _.RemoveEdgeAt(index: int) = state.Edges.RemoveAt(index)

/// <summary>The union of two sets. One reference count per element across both sides.</summary>
type UnionSetNode<'T when 'T: equality>(left: IAdaptiveSet<'T>, right: IAdaptiveSet<'T>) =
    let deps = [| left; right |]
    let mutable state = SetNodeState<'T, 'T>.Create(2)
    let mutable initialized = false
    let mutable disposed = false

    member private this.RegisterSide(s: IAdaptiveSet<'T>) =
        match box s with
        | :? ISetSinkRegistry as r -> r.AddSetSink(box (this :> ISetDeltaSink<'T>))
        | _ -> ()

    member private this.UnregisterSide(s: IAdaptiveSet<'T>) =
        match box s with
        | :? ISetSinkRegistry as r -> r.RemoveSetSink(box (this :> ISetDeltaSink<'T>))
        | _ -> ()

    member private this.EnsureInitialized() =
        if not initialized then
            // Snapshot first, register between (see MapSetNode.EnsureInitialized).
            // The identity mapping cannot write; the snapshot is for uniformity.
            // The flag is set last: an exception leaves the node uninitialized.
            let leftSnapshot = HashSet<'T>(left.GetValue())
            let rightSnapshot = HashSet<'T>(right.GetValue())
            this.RegisterSide left
            this.RegisterSide right
            Collections.loadRefSet Id.identityV leftSnapshot &state
            Collections.loadRefSet Id.identityV rightSnapshot &state
            state.DepVersions[0] <- left.Version
            state.DepVersions[1] <- right.Version
            initialized <- true

    interface ISetDeltaSink<'T> with
        member this.OnDeltas(adds: 'T[], addCnt: int, rems: 'T[], remCnt: int) =
            if not disposed then
                Collections.journalAppendSet &state.Journal adds addCnt rems remCnt
                state.Version <- state.Version + 1L
                GraphContext.Default.MarkFrom(state.Edges)

    interface IAdaptiveSet<'T> with
        member this.GetValue() =
            let ctx = GraphContext.Default
            ctx.ClaimOwner()

            try
                if disposed then
                    invalidOp "This adaptive set has been disposed."

                this.EnsureInitialized()

                for j in 0..1 do
                    if deps[j].Version <> state.DepVersions[j] then
                        deps[j].GetValue() |> ignore
                        state.DepVersions[j] <- deps[j].Version

                if not state.Journal.IsEmpty then
                    Collections.drainSetPush Id.identityV &state

                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) state.Version
                state.Set.Data :> IReadOnlySet<'T>
            finally
                ctx.ReleaseOwner()

        member _.Version = state.Version

    interface IDisposable with
        member this.Dispose() =
            if not disposed then
                disposed <- true
                this.UnregisterSide left
                this.UnregisterSide right
                Collections.clearSinks &state.Sinks

    interface ISetSinkRegistry with
        member this.AddSetSink(sink) = Collections.addSink &state.Sinks sink

        member this.RemoveSetSink(sink) =
            Collections.removeSink &state.Sinks sink

    interface IEdgeTarget with
        member _.EdgeCount = state.Edges.Count
        member _.AddEdge(parent: IAdaptiveNode, depIndex: int) = state.Edges.Add(parent, depIndex)
        member _.RemoveEdgeAt(index: int) = state.Edges.RemoveAt(index)

/// <summary>
/// A binary set operation over two sources: difference (left minus right),
/// intersect, or xor. Per-side reference counts drive the output membership.
/// </summary>
type TwoSourceSetNode<'T when 'T: equality>(op: TwoSetOp, left: IAdaptiveSet<'T>, right: IAdaptiveSet<'T>) =
    let deps: IAdaptiveObject[] =
        [| left :> IAdaptiveObject; right :> IAdaptiveObject |]

    let mutable state = Collections.TwoSetState<'T>.Create(2)
    let mutable leftSink: obj = null
    let mutable rightSink: obj = null
    let mutable initialized = false
    let mutable disposed = false

    member private this.Register() =
        leftSink <- box (Collections.SideSetSink<'T>(this, 0))
        rightSink <- box (Collections.SideSetSink<'T>(this, 1))

        match box left with
        | :? ISetSinkRegistry as r -> r.AddSetSink(leftSink)
        | _ -> ()

        match box right with
        | :? ISetSinkRegistry as r -> r.AddSetSink(rightSink)
        | _ -> ()

    member private this.Unregister() =
        match box left with
        | :? ISetSinkRegistry as r -> r.RemoveSetSink(leftSink)
        | _ -> ()

        match box right with
        | :? ISetSinkRegistry as r -> r.RemoveSetSink(rightSink)
        | _ -> ()

    member private this.EnsureInitialized() =
        if not initialized then
            // Read first, register after (see MapSetNode.EnsureInitialized).
            // The flag is set last: an exception leaves the node uninitialized.
            Collections.loadTwoSet op left right &state
            this.Register()
            state.DepVersions[0] <- left.Version
            state.DepVersions[1] <- right.Version
            initialized <- true

    interface Collections.ITwoSetSinkTarget<'T> with
        member this.OnSideDeltas(side: int, adds: 'T[], addCnt: int, rems: 'T[], remCnt: int) =
            if not disposed then
                if side = 0 then
                    Collections.journalAppendSet &state.JournalL adds addCnt rems remCnt
                else
                    Collections.journalAppendSet &state.JournalR adds addCnt rems remCnt

                state.Version <- state.Version + 1L
                GraphContext.Default.MarkFrom(state.Edges)

    interface IAdaptiveSet<'T> with
        member this.GetValue() =
            let ctx = GraphContext.Default
            ctx.ClaimOwner()

            try
                if disposed then
                    invalidOp "This adaptive set has been disposed."

                this.EnsureInitialized()

                for j in 0..1 do
                    if deps[j].Version <> state.DepVersions[j] then
                        if j = 0 then
                            left.GetValue() |> ignore
                        else
                            right.GetValue() |> ignore

                        state.DepVersions[j] <- deps[j].Version

                if not state.JournalL.IsEmpty || not state.JournalR.IsEmpty then
                    Collections.drainTwoSetPush op &state

                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) state.Version
                state.Out :> IReadOnlySet<'T>
            finally
                ctx.ReleaseOwner()

        member _.Version = state.Version

    interface IDisposable with
        member this.Dispose() =
            if not disposed then
                disposed <- true
                this.Unregister()
                Collections.clearSinks &state.Sinks

    interface ISetSinkRegistry with
        member this.AddSetSink(sink) = Collections.addSink &state.Sinks sink

        member this.RemoveSetSink(sink) =
            Collections.removeSink &state.Sinks sink

    interface IEdgeTarget with
        member _.EdgeCount = state.Edges.Count
        member _.AddEdge(parent: IAdaptiveNode, depIndex: int) = state.Edges.Add(parent, depIndex)
        member _.RemoveEdgeAt(index: int) = state.Edges.RemoveAt(index)

/// <summary>
/// An adaptive set over an adaptive value of a sequence. Every change of the
/// value replaces the whole state and emits the diff as the delta (the value
/// carries no deltas; this node is the rebuild boundary, like FDA
/// <c>ASet.ofAVal</c>).
/// </summary>
type OfAvalSetNode<'T, 'S when 'T: equality and 'S :> seq<'T>>(value: IAdaptiveValue<'S>) =
    let mutable state = SetNodeState<'T, 'T>.Create(1)
    let mutable edgeInValue = -1
    let mutable initialized = false
    let mutable disposed = false

    member private this.EnsureInitialized() =
        if not initialized then
            match value with
            | :? IEdgeTarget as t -> edgeInValue <- t.AddEdge(this :> IAdaptiveNode, -1)
            | _ -> ()

            // Initial load: materialize the value and build the state.
            // The flag is set last: an exception leaves the node uninitialized.
            // The init diff is not pushed: clear the out buffer so it cannot
            // pollute the first real delta.
            let next = HashSet<'T>(value.GetValue())
            Collections.rebuildSetDiff next &state |> ignore
            state.Out.Clear()
            state.DepVersions[0] <- value.Version
            initialized <- true

    /// Re-read the value when it changed and emit the diff. Called from
    /// GetValue and from the Version getter (poll model, like
    /// <see cref="CustomSetNode"/>): a downstream re-pulls only when this
    /// node's version moves, so the version must advance on the read path.
    member private this.Poll() =
        if value.Version <> state.DepVersions[0] then
            // The value may yield a transient seq: materialize it.
            let next = HashSet<'T>(value.GetValue())

            if Collections.rebuildSetDiff next &state then
                // The version must advance: downstream nodes re-pull the
                // source only when it changed (a stuck version makes
                // derived nodes stale forever).
                state.Version <- state.Version + 1L
                Collections.pushAndMarkSet state.Out state.Sinks state.Edges
                state.Out.Clear()

            state.DepVersions[0] <- value.Version

    interface IAdaptiveNode with
        member this.MarkDirty() =
            GraphContext.Default.MarkFrom(state.Edges)

        member _.SetDepSlot(depIndex: int, parentIndex: int) =
            if depIndex = -1 then
                edgeInValue <- parentIndex

        member _.OnFirstParent() = ()
        member _.OnLastParent() = ()

    interface IAdaptiveSet<'T> with
        member this.GetValue() =
            let ctx = GraphContext.Default
            ctx.ClaimOwner()

            try
                if disposed then
                    invalidOp "This adaptive set has been disposed."

                this.EnsureInitialized()
                this.Poll()
                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) state.Version
                state.Set.Data :> IReadOnlySet<'T>
            finally
                ctx.ReleaseOwner()

        member this.Version =
            this.EnsureInitialized()
            this.Poll()
            state.Version

    interface IDisposable with
        member this.Dispose() =
            if not disposed then
                disposed <- true

                match value with
                | :? IEdgeTarget as t -> t.RemoveEdgeAt(edgeInValue)
                | _ -> ()

                Collections.clearSinks &state.Sinks

    interface ISetSinkRegistry with
        member this.AddSetSink(sink) = Collections.addSink &state.Sinks sink

        member this.RemoveSetSink(sink) =
            Collections.removeSink &state.Sinks sink

    interface IEdgeTarget with
        member _.EdgeCount = state.Edges.Count
        member _.AddEdge(parent: IAdaptiveNode, depIndex: int) = state.Edges.Add(parent, depIndex)
        member _.RemoveEdgeAt(index: int) = state.Edges.RemoveAt(index)

/// <summary>
/// An adaptive set over an external reader function. The reader is called on
/// every read (poll); the node diffs the result against its state and emits
/// the diff as the delta. Pull-based: nothing marks this node, so consumers
/// must re-read it (FDA <c>ASet.ofReader</c> has the same pull model).
/// </summary>
type ReaderSetNode<'T when 'T: equality>([<InlineIfLambda>] reader: unit -> HashSet<'T>) =
    let mutable state = SetNodeState<'T, 'T>.Create(0)
    let mutable disposed = false

    /// Re-read the external state and emit the diff. Called from GetValue and
    /// from the Version getter (consumers version-check before reading).
    member private this.Poll() =
        if not disposed then
            let next = reader ()

            if Collections.rebuildSetDiff next &state then
                state.Version <- state.Version + 1L
                Collections.pushAndMarkSet state.Out state.Sinks state.Edges
                state.Out.Clear()

    interface IAdaptiveSet<'T> with
        member this.GetValue() =
            let ctx = GraphContext.Default
            ctx.ClaimOwner()

            try
                if disposed then
                    invalidOp "This adaptive set has been disposed."

                this.Poll()
                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) state.Version
                state.Set.Data :> IReadOnlySet<'T>
            finally
                ctx.ReleaseOwner()

        member this.Version =
            this.Poll()
            state.Version

    interface IDisposable with
        member this.Dispose() =
            if not disposed then
                disposed <- true
                Collections.clearSinks &state.Sinks

    interface ISetSinkRegistry with
        member this.AddSetSink(sink) = Collections.addSink &state.Sinks sink

        member this.RemoveSetSink(sink) =
            Collections.removeSink &state.Sinks sink

    interface IEdgeTarget with
        member _.EdgeCount = state.Edges.Count
        member _.AddEdge(parent: IAdaptiveNode, depIndex: int) = state.Edges.Add(parent, depIndex)
        member _.RemoveEdgeAt(index: int) = state.Edges.RemoveAt(index)

/// <summary>
/// An adaptive set whose content is driven by a compute function. The compute
/// receives the current view and a delta builder; it appends the operations
/// that describe the change since the previous call (consuming its own event
/// queue, for example). Called on every read (poll), like
/// <see cref="ReaderSetNode"/>. FDA <c>ASet.custom</c> parity, pull model.
/// </summary>
type CustomSetNode<'T when 'T: equality>([<InlineIfLambda>] compute: IReadOnlySet<'T> -> SetDeltaBuilder<'T> -> unit) =
    let mutable state = SetNodeState<'T, 'T>.Create(0)
    let writer = SetDeltaBuilder<'T>()
    // Reused scratch: prior presence of every element touched by one poll
    // (construction-time allocation; zero steady-state allocation).
    let scratch = Dictionary<'T, bool>()
    let mutable disposed = false

    member private this.Poll() =
        if not disposed then
            writer.Clear()
            compute (state.Set.Data :> IReadOnlySet<'T>) writer

            if not writer.IsEmpty then
                // Net-delta invariant: the delivered delta must not carry the
                // same element in both adds and rems (consumers apply the
                // buffers order-free). Adds apply first by convention; record
                // the prior presence of every touched element, apply the
                // batch, then deliver the net transition.
                scratch.Clear()

                for i in 0 .. writer.Adds.Count - 1 do
                    let x = writer.Adds.Items[i]

                    if not (scratch.ContainsKey x) then
                        scratch[x] <- state.Set.Data.Contains x

                    state.Set.Data.Add x |> ignore

                for i in 0 .. writer.Rems.Count - 1 do
                    let x = writer.Rems.Items[i]

                    if not (scratch.ContainsKey x) then
                        scratch[x] <- state.Set.Data.Contains x

                    state.Set.Data.Remove x |> ignore

                writer.Clear()
                let mutable e = scratch.GetEnumerator()

                while e.MoveNext() do
                    let x = e.Current.Key
                    let prior = e.Current.Value
                    let final = state.Set.Data.Contains x

                    if prior <> final then
                        if final then writer.Add x else writer.Remove x

                if not writer.IsEmpty then
                    state.Version <- state.Version + 1L
                    Collections.pushAndMarkSet (writer.Snapshot()) state.Sinks state.Edges

                writer.Clear()

    interface IAdaptiveSet<'T> with
        member this.GetValue() =
            let ctx = GraphContext.Default
            ctx.ClaimOwner()

            try
                if disposed then
                    invalidOp "This adaptive set has been disposed."

                this.Poll()
                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) state.Version
                state.Set.Data :> IReadOnlySet<'T>
            finally
                ctx.ReleaseOwner()

        member this.Version =
            this.Poll()
            state.Version

    interface IDisposable with
        member this.Dispose() =
            if not disposed then
                disposed <- true
                Collections.clearSinks &state.Sinks

    interface ISetSinkRegistry with
        member this.AddSetSink(sink) = Collections.addSink &state.Sinks sink

        member this.RemoveSetSink(sink) =
            Collections.removeSink &state.Sinks sink

    interface IEdgeTarget with
        member _.EdgeCount = state.Edges.Count
        member _.AddEdge(parent: IAdaptiveNode, depIndex: int) = state.Edges.Add(parent, depIndex)
        member _.RemoveEdgeAt(index: int) = state.Edges.RemoveAt(index)

/// <summary>
/// An adaptive set that unions one inner adaptive set per source element
/// (<c>ASet.collect</c>, PLAN.md Section 7.4). The output is the refcounted
/// union of all contributions (the CountingHashSet role): an output element
/// disappears only when the last contributing inner set drops it. A removed
/// source element unregisters its inner sink eagerly (ANALYSIS-FDA.md
/// Pitfall 1). Registration is lazy (first read); disposal unregisters
/// everything.
/// </summary>
type CollectSetNode<'T, 'U when 'T: equality and 'U: equality>
    (source: IAdaptiveSet<'T>, [<InlineIfLambda>] mapping: 'T -> IAdaptiveSet<'U>) =
    let mutable state = Collections.CollectState<'T, 'U>.Create(1)
    let mutable initialized = false
    let mutable disposed = false

    member private this.EnsureInitialized() =
        if not initialized then
            initialized <- true
            // Snapshot the source view first, then register, then run the
            // mapping over the snapshot: the mapping is user code that may
            // write to the source (the transient view must not be iterated
            // while it is mutated), and the write must land in our journal.
            let snapshot = HashSet<'T>(source.GetValue())

            match box source with
            | :? ISetSinkRegistry as r -> r.AddSetSink(box this)
            | _ -> ()

            for x in snapshot do
                let inner = mapping x
                let innerView = inner.GetValue()
                let mutable entry = Collections.CollectEntry<'U>(inner)

                for u in innerView do
                    let struct (g2, added) = Collections.refAdd state.Global u
                    state.Global <- g2
                    // The entry's own content is tracked unconditionally: only
                    // the global output delta is conditional on newness.
                    entry.Content.Add u |> ignore

                entry.Sink <- box (Collections.CollectSink<'T, 'U>(this, x))

                match box inner with
                | :? ISetSinkRegistry as r -> r.AddSetSink(entry.Sink)
                | _ -> ()

                entry.Version <- inner.Version
                state.Inner[x] <- entry

            state.DepVersions[0] <- source.Version
            initialized <- true

    interface Collections.ICollectTarget<'T, 'U> with
        member this.OnInnerDeltas(key: 'T, adds: 'U[], addCnt: int, rems: 'U[], remCnt: int) =
            if not disposed then
                let mutable entry = Unchecked.defaultof<Collections.CollectEntry<'U>>

                if state.Inner.TryGetValue(key, &entry) then
                    Collections.journalAppendSet &entry.Journal adds addCnt rems remCnt
                    state.Inner[key] <- entry
                    state.Version <- state.Version + 1L
                    GraphContext.Default.MarkFrom(state.Edges)

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

                let mutable hasPending = false

                if source.Version <> state.DepVersions[0] then
                    source.GetValue() |> ignore
                    state.DepVersions[0] <- source.Version

                hasPending <- not state.Journal.IsEmpty

                // Explicit struct enumerator: the F# KeyValue pattern over a
                // Dictionary field can allocate per element (measured 88 B/entry
                // in the version-check loop; zero with the explicit enumerator).
                let mutable ie = state.Inner.GetEnumerator()

                while ie.MoveNext() do
                    let x = ie.Current.Key
                    let e0 = ie.Current.Value
                    let mutable entry = e0

                    if entry.Node.Version <> entry.Version then
                        entry.Node.GetValue() |> ignore
                        // The pull (and the version getter itself, for poll
                        // inners) may have delivered a delta into the dictionary
                        // entry: re-read instead of clobbering the stale copy.
                        let mutable e2 = Unchecked.defaultof<Collections.CollectEntry<'U>>

                        if state.Inner.TryGetValue(x, &e2) then
                            e2.Version <- entry.Node.Version
                            state.Inner[x] <- e2
                            entry <- e2
                        else
                            entry.Version <- entry.Node.Version

                    if not hasPending && not entry.Journal.IsEmpty then
                        hasPending <- true

                if hasPending then
                    Collections.drainCollectPush this mapping &state

                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) state.Version
                state.Global.Data :> IReadOnlySet<'U>
            finally
                ctx.ReleaseOwner()

        member _.Version = state.Version

    interface IDisposable with
        member this.Dispose() =
            if not disposed then
                disposed <- true

                match box source with
                | :? ISetSinkRegistry as r -> r.RemoveSetSink(box this)
                | _ -> ()

                for KeyValue(_, entry) in state.Inner do
                    match box entry.Node with
                    | :? ISetSinkRegistry as r -> r.RemoveSetSink(entry.Sink)
                    | _ -> ()

                state.Inner.Clear()
                Collections.clearSinks &state.Sinks

    interface ISetSinkRegistry with
        member this.AddSetSink(sink) = Collections.addSink &state.Sinks sink

        member this.RemoveSetSink(sink) =
            Collections.removeSink &state.Sinks sink

    interface IEdgeTarget with
        member _.EdgeCount = state.Edges.Count
        member _.AddEdge(parent: IAdaptiveNode, depIndex: int) = state.Edges.Add(parent, depIndex)
        member _.RemoveEdgeAt(index: int) = state.Edges.RemoveAt(index)

/// <summary>
/// An adaptive set bound to a scalar value (<c>ASet.bind</c>, PLAN.md Section
/// 7.4): <c>mapping value</c> selects the inner set; when the value changes, the
/// whole inner set is swapped (old content removed, new content added) and the
/// old inner sink is unregistered eagerly (FDA <c>BindReader</c> semantics;
/// ANALYSIS-FDA.md Pitfall 1). The inner set's own changes flow through a
/// journal. Registration is lazy (first read); disposal unregisters everything.
/// </summary>
type BindSetNode<'T, 'U when 'U: equality>
    (value: IAdaptiveValue<'T>, [<InlineIfLambda>] mapping: 'T -> IAdaptiveSet<'U>) =
    let mutable state = Collections.BindSetState<'U>.Create(1)
    let mutable inner: IAdaptiveSet<'U> = Unchecked.defaultof<IAdaptiveSet<'U>>
    let mutable hasInner = false
    let mutable innerVersion = 0L
    let mutable edgeInValue = -1
    let mutable current: 'T = Unchecked.defaultof<'T>
    let mutable initialized = false
    let mutable disposed = false

    member private this.UnregisterInner() =
        if hasInner then
            match box inner with
            | :? ISetSinkRegistry as r -> r.RemoveSetSink(box this)
            | _ -> ()

            hasInner <- false

    member private this.LoadInner() =
        // Read first, register after: the view is complete, and the sink sees
        // only deltas that follow this point in time.
        let view = inner.GetValue()

        for u in view do
            state.Data.Add u |> ignore

        match box inner with
        | :? ISetSinkRegistry as r -> r.AddSetSink(box this)
        | _ -> ()

        hasInner <- true
        innerVersion <- inner.Version

    member private this.SwapTo(next: 'T) =
        // Eager edge removal (Pitfall 1): the old inner must not deliver after
        // the swap. Its pending journal is dropped with the content.
        this.UnregisterInner()
        state.Data.Clear()
        inner <- mapping next
        current <- next
        this.LoadInner()
        state.Journal.Clear()
        state.Version <- state.Version + 1L

    member private this.EnsureInitialized() =
        if not initialized then
            match value with
            | :? IEdgeTarget as t -> edgeInValue <- t.AddEdge(this :> IAdaptiveNode, -1)
            | _ -> ()

            current <- value.GetValue()
            inner <- mapping current
            this.LoadInner()
            state.DepVersions[0] <- value.Version
            initialized <- true

    interface IAdaptiveNode with
        member this.MarkDirty() =
            GraphContext.Default.MarkFrom(state.Edges)

        member _.SetDepSlot(depIndex: int, parentIndex: int) =
            if depIndex = -1 then
                edgeInValue <- parentIndex

        member _.OnFirstParent() = ()
        member _.OnLastParent() = ()

    interface ISetDeltaSink<'U> with
        member this.OnDeltas(adds: 'U[], addCnt: int, rems: 'U[], remCnt: int) =
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

                if value.Version <> state.DepVersions[0] then
                    let next = value.GetValue()
                    state.DepVersions[0] <- value.Version

                    if not (EqualityComparer<'T>.Default.Equals(current, next)) then
                        this.SwapTo(next)

                if inner.Version <> innerVersion then
                    inner.GetValue() |> ignore
                    innerVersion <- inner.Version

                if not state.Journal.IsEmpty then
                    Collections.drainBindSetPush &state

                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) state.Version
                state.Data :> IReadOnlySet<'U>
            finally
                ctx.ReleaseOwner()

        member _.Version = state.Version

    interface IDisposable with
        member this.Dispose() =
            if not disposed then
                disposed <- true
                this.UnregisterInner()

                match value with
                | :? IEdgeTarget as t -> t.RemoveEdgeAt(edgeInValue)
                | _ -> ()

                Collections.clearSinks &state.Sinks

    interface ISetSinkRegistry with
        member this.AddSetSink(sink) = Collections.addSink &state.Sinks sink

        member this.RemoveSetSink(sink) =
            Collections.removeSink &state.Sinks sink

    interface IEdgeTarget with
        member _.EdgeCount = state.Edges.Count
        member _.AddEdge(parent: IAdaptiveNode, depIndex: int) = state.Edges.Add(parent, depIndex)
        member _.RemoveEdgeAt(index: int) = state.Edges.RemoveAt(index)
