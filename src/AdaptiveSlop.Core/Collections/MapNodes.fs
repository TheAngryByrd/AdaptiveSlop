namespace AdaptiveSlop.Core

open System
open System.Collections.Generic
open System.Collections.Frozen

// =============================================================================
// AdaptiveMap transform nodes (PLAN.md Section 6.9)
//
// Same journal/drain model as the set nodes. Registration happens through the
// IMapSinkRegistry interface on every node type, so derived maps compose freely.
// =============================================================================

/// <summary>An adaptive map over a fixed, immutable value.</summary>
type ConstantMap<'K, 'V when 'K: equality>(value: FrozenDictionary<'K, 'V>) =
    interface IAdaptiveMap<'K, 'V> with
        member this.GetValue() =
            AdaptiveRuntime.addDependency (this :> IAdaptiveObject) 0L
            value :> IReadOnlyDictionary<'K, 'V>

        member _.Version = 0L

    interface IDisposable with
        member _.Dispose() = ()

/// <summary>Maps every entry of a map.</summary>
type MapMapNode<'K, 'V, 'U when 'K: equality>(source: IAdaptiveMap<'K, 'V>, [<InlineIfLambda>] mapping: 'K -> 'V -> 'U)
    =
    let mapOpt = fun k v -> ValueSome(mapping k v)
    let mutable state = MapNodeState<'K, 'V, 'U>.Create(1)
    let mutable initialized = false
    let mutable disposed = false

    member private this.Register() =
        match box source with
        | :? IMapSinkRegistry as r -> r.AddMapSink(box (this :> IMapDeltaSink<'K, 'V>))
        | _ -> ()

    member private this.Unregister() =
        match box source with
        | :? IMapSinkRegistry as r -> r.RemoveMapSink(box (this :> IMapDeltaSink<'K, 'V>))
        | _ -> ()

    member private this.EnsureInitialized() =
        if not initialized then
            initialized <- true
            this.Register()
            Collections.loadMap mapOpt source &state
            state.DepVersions[0] <- source.Version

    interface IMapDeltaSink<'K, 'V> with
        member this.OnDeltas(sets: struct ('K * 'V)[], setCnt: int, rems: 'K[], remCnt: int) =
            if not disposed then
                Collections.journalAppendMap &state.Journal sets setCnt rems remCnt
                state.Version <- state.Version + 1L
                GraphContext.Default.MarkFrom(state.Edges)

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

                if not state.Journal.IsEmpty then
                    Collections.drainMapPush mapOpt &state

                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) state.Version
                state.Data :> IReadOnlyDictionary<'K, 'U>
            finally
                ctx.ReleaseOwner()

        member _.Version = state.Version

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

    interface IEdgeTarget with
        member _.EdgeCount = state.Edges.Count
        member _.AddEdge(parent: IAdaptiveNode, depIndex: int) = state.Edges.Add(parent, depIndex)
        member _.RemoveEdgeAt(index: int) = state.Edges.RemoveAt(index)

/// <summary>Keeps the entries of a map that satisfy a predicate.</summary>
type FilterMapNode<'K, 'V when 'K: equality>
    (source: IAdaptiveMap<'K, 'V>, [<InlineIfLambda>] predicate: 'K -> 'V -> bool) =
    let mapOpt = fun k v -> if predicate k v then ValueSome v else ValueNone
    let mutable state = MapNodeState<'K, 'V, 'V>.Create(1)
    let mutable initialized = false
    let mutable disposed = false

    member private this.Register() =
        match box source with
        | :? IMapSinkRegistry as r -> r.AddMapSink(box (this :> IMapDeltaSink<'K, 'V>))
        | _ -> ()

    member private this.Unregister() =
        match box source with
        | :? IMapSinkRegistry as r -> r.RemoveMapSink(box (this :> IMapDeltaSink<'K, 'V>))
        | _ -> ()

    member private this.EnsureInitialized() =
        if not initialized then
            initialized <- true
            this.Register()
            Collections.loadMap mapOpt source &state
            state.DepVersions[0] <- source.Version

    interface IMapDeltaSink<'K, 'V> with
        member this.OnDeltas(sets: struct ('K * 'V)[], setCnt: int, rems: 'K[], remCnt: int) =
            if not disposed then
                Collections.journalAppendMap &state.Journal sets setCnt rems remCnt
                state.Version <- state.Version + 1L
                GraphContext.Default.MarkFrom(state.Edges)

    interface IAdaptiveMap<'K, 'V> with
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

                if not state.Journal.IsEmpty then
                    Collections.drainMapPush mapOpt &state

                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) state.Version
                state.Data :> IReadOnlyDictionary<'K, 'V>
            finally
                ctx.ReleaseOwner()

        member _.Version = state.Version

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

    interface IEdgeTarget with
        member _.EdgeCount = state.Edges.Count
        member _.AddEdge(parent: IAdaptiveNode, depIndex: int) = state.Edges.Add(parent, depIndex)
        member _.RemoveEdgeAt(index: int) = state.Edges.RemoveAt(index)
