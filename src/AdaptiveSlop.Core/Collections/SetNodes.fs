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
type ConstantSet<'T>(create: unit -> FrozenSet<'T>) =
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
type MapSetNode<'T, 'U when 'U: equality>(source: IAdaptiveSet<'T>, mapping: 'T -> 'U voption) =
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
            initialized <- true
            this.Register()
            Collections.loadRefSet mapping source &state
            state.DepVersions[0] <- source.Version

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
            initialized <- true
            this.Register()
            Collections.loadPlainSet mapOpt source &state
            state.DepVersions[0] <- source.Version

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
            initialized <- true
            this.RegisterSide left
            this.RegisterSide right
            Collections.loadRefSet Id.identityV left &state
            Collections.loadRefSet Id.identityV right &state
            state.DepVersions[0] <- left.Version
            state.DepVersions[1] <- right.Version

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
            initialized <- true
            this.Register()
            Collections.loadTwoSet op left right &state
            state.DepVersions[0] <- left.Version
            state.DepVersions[1] <- right.Version

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
            initialized <- true

            match value with
            | :? IEdgeTarget as t -> edgeInValue <- t.AddEdge(this :> IAdaptiveNode, -1)
            | _ -> ()

            // Initial load: materialize the value and build the state.
            let next = HashSet<'T>(value.GetValue())
            Collections.rebuildSetDiff next &state |> ignore
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

                if value.Version <> state.DepVersions[0] then
                    // The value may yield a transient seq: materialize it.
                    let next = HashSet<'T>(value.GetValue())

                    if Collections.rebuildSetDiff next &state then
                        Collections.pushAndMarkSet state.Out state.Sinks state.Edges
                        state.Out.Clear()

                    state.DepVersions[0] <- value.Version

                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) state.Version
                state.Set.Data :> IReadOnlySet<'T>
            finally
                ctx.ReleaseOwner()

        member _.Version = state.Version

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
type ReaderSetNode<'T when 'T: equality>(reader: unit -> HashSet<'T>) =
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
type CustomSetNode<'T when 'T: equality>(compute: IReadOnlySet<'T> -> SetDeltaBuilder<'T> -> unit) =
    let mutable state = SetNodeState<'T, 'T>.Create(0)
    let writer = SetDeltaBuilder<'T>()
    let mutable disposed = false

    member private this.Poll() =
        if not disposed then
            writer.Clear()
            compute (state.Set.Data :> IReadOnlySet<'T>) writer

            if not writer.IsEmpty then
                let adds = writer.Adds
                let rems = writer.Rems

                for i in 0 .. adds.Count - 1 do
                    state.Set.Data.Add adds.Items[i] |> ignore

                for i in 0 .. rems.Count - 1 do
                    state.Set.Data.Remove rems.Items[i] |> ignore

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
