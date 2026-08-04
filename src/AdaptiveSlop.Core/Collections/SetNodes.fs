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

/// <summary>An adaptive set over a fixed, immutable value.</summary>
type ConstantSet<'T>(value: FrozenSet<'T>) =
    interface IAdaptiveSet<'T> with
        member this.GetValue() =
            AdaptiveRuntime.addDependency (this :> IAdaptiveObject) 0L
            value :> IReadOnlySet<'T>

        member _.Version = 0L

    interface IDisposable with
        member _.Dispose() = ()

/// <summary>Maps every element of a set. Duplicate outputs share one reference count.</summary>
type MapSetNode<'T, 'U when 'U: equality>(source: IAdaptiveSet<'T>, [<InlineIfLambda>] mapping: 'T -> 'U) =
    let mapOpt = fun x -> ValueSome(mapping x)
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
            Collections.loadRefSet mapOpt source &state
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
                    Collections.drainSetPush mapOpt &state

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
    let identity = fun x -> ValueSome x
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
            Collections.loadRefSet identity left &state
            Collections.loadRefSet identity right &state
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
                    Collections.drainSetPush identity &state

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
