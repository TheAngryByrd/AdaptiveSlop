namespace AdaptiveSlop.Core

open System
open System.Collections.Generic

// =============================================================================
// Incremental reductions (PLAN.md Section 7.2)
//
// The FDA AdaptiveReduction protocol: a struct record of seed/add/sub/view.
// The node keeps the reduction state and applies journal deltas at read time
// (pull-lazy). `sub` returns ValueNone when the operation cannot be inverted
// for the given element: the node then recomputes the whole state from the
// current view (the fallback protocol). Map reductions keep a mirror of the
// source values so removals and updates can invert.
// =============================================================================

/// <summary>
/// An incremental reduction protocol over elements of type 'a: the state 's is
/// updated with <c>add</c> for added elements and <c>sub</c> for removed ones.
/// <c>sub</c> returns <c>ValueNone</c> when it cannot invert the removal; the
/// library then recomputes the state from the current collection. <c>view</c>
/// projects the state to the observed value.
/// </summary>
/// <remarks>
/// Parity: FDA <c>AdaptiveReduction</c> (AdaptiveValue/AdaptiveReduction.fs).
/// Order of element application is undefined.
/// </remarks>
[<Struct>]
type AdaptiveReduction<'a, 's, 'v> =
    {
        /// <summary>The initial state.</summary>
        seed: 's
        /// <summary>Applies one added element to the state.</summary>
        add: 's -> 'a -> 's
        /// <summary>
        /// Inverts one removed element. Returns <c>ValueNone</c> when the removal
        /// cannot be inverted; the library recomputes from the current collection.
        /// </summary>
        sub: 's -> 'a -> 's voption
        /// <summary>Projects the state to the observed value.</summary>
        view: 's -> 'v
    }

/// <summary>Combinators for building <see cref="AdaptiveReduction"/> values.</summary>
module AdaptiveReduction =

    /// <summary>Maps the observed value of a reduction.</summary>
    let inline mapOut
        ([<InlineIfLambda>] mapping: 'v -> 'w)
        (reduction: AdaptiveReduction<'a, 's, 'v>)
        : AdaptiveReduction<'a, 's, 'w> =
        { seed = reduction.seed
          add = reduction.add
          sub = reduction.sub
          view = fun s -> mapping (reduction.view s) }

    /// <summary>A reduction with an invertible subtract operation.</summary>
    let inline group
        (zero: 's)
        ([<InlineIfLambda>] add: 's -> 'a -> 's)
        (subtract: 's -> 'a -> 's)
        : AdaptiveReduction<'a, 's, 's> =
        { seed = zero
          add = add
          sub = fun s a -> ValueSome(subtract s a)
          view = id }

    /// <summary>A reduction whose subtract may fall back to a full recompute.</summary>
    let inline halfGroup
        (zero: 's)
        ([<InlineIfLambda>] add: 's -> 'a -> 's)
        (trySubtract: 's -> 'a -> 's voption)
        : AdaptiveReduction<'a, 's, 's> =
        { seed = zero
          add = add
          sub = trySubtract
          view = id }

    /// <summary>A reduction that recomputes the whole state on every removal.</summary>
    let inline fold (zero: 's) ([<InlineIfLambda>] add: 's -> 'a -> 's) : AdaptiveReduction<'a, 's, 's> =
        { seed = zero
          add = add
          sub = fun _ _ -> ValueNone
          view = id }

    /// <summary>Counts the elements for which the mapped value is true.</summary>
    let countPositive: AdaptiveReduction<bool, int, int> =
        { seed = 0
          add = fun s b -> if b then s + 1 else s
          sub = fun s b -> if b then ValueSome(s - 1) else ValueSome s
          view = id }

    /// <summary>Counts the elements for which the mapped value is false.</summary>
    let countNegative: AdaptiveReduction<bool, int, int> =
        { seed = 0
          add = fun s b -> if b then s else s + 1
          sub = fun s b -> if b then ValueSome s else ValueSome(s - 1)
          view = id }

    /// <summary>Sums the mapped values. Needs an additive numeric type.</summary>
    let inline sum () : AdaptiveReduction<'a, 'a, 'a> =
        { seed = LanguagePrimitives.GenericZero<'a>
          add = fun s v -> s + v
          sub = fun s v -> ValueSome(s - v)
          view = id }

    /// <summary>The minimum of the mapped values, or ValueNone when empty. A removal recomputes.</summary>
    let inline tryMin () : AdaptiveReduction<'a, 'a voption, 'a voption> =
        { seed = ValueNone
          add =
            fun s v ->
                match s with
                | ValueSome m -> ValueSome(min m v)
                | ValueNone -> ValueSome v
          sub = fun _ _ -> ValueNone
          view = id }

    /// <summary>The maximum of the mapped values, or ValueNone when empty. A removal recomputes.</summary>
    let inline tryMax () : AdaptiveReduction<'a, 'a voption, 'a voption> =
        { seed = ValueNone
          add =
            fun s v ->
                match s with
                | ValueSome m -> ValueSome(max m v)
                | ValueNone -> ValueSome v
          sub = fun _ _ -> ValueNone
          view = id }

/// <summary>
/// A delta-driven reduction over a set. Registers as a delta sink on
/// the source; the journal is applied to the reduction state on read (drain),
/// with a full recompute fallback when <c>sub</c> cannot invert a removal.
/// Implements the scalar protocol: version, parent edges, dependency snapshot.
/// </summary>
type SetReduceNode<'a, 'b, 's, 'v when 'a: equality>
    (source: IAdaptiveSet<'a>, [<InlineIfLambda>] mapping: 'a -> 'b, reduction: AdaptiveReduction<'b, 's, 'v>) =
    let mutable version = 0L
    let mutable edges = ParentEdges()
    let mutable depVersions = [| 0L |]
    let mutable journal = SetDelta<'a>.Create()
    let mutable initialized = false
    let mutable disposed = false
    let mutable red = reduction.seed
    let mutable value = reduction.view reduction.seed

    member private this.Register() =
        match box source with
        | :? ISetSinkRegistry as r -> r.AddSetSink(box (this :> ISetDeltaSink<'a>))
        | _ -> ()

    member private this.Unregister() =
        match box source with
        | :? ISetSinkRegistry as r -> r.RemoveSetSink(box (this :> ISetDeltaSink<'a>))
        | _ -> ()

    /// Full recompute from the current view. The rebuilt state reflects every
    /// pending delta, so the whole journal is consumed.
    member private this.Rebuild() =
        let mutable acc = reduction.seed

        for x in source.GetValue() do
            acc <- reduction.add acc (mapping x)

        red <- acc
        value <- reduction.view red
        journal.Clear()

    /// Apply the journal to the reduction state. Entries appended during
    /// processing (reentrant writes) survive; a rebuild consumes everything.
    member private this.Drain() =
        let remStart = journal.Rems.Count
        let addStart = journal.Adds.Count
        let mutable i = 0
        let mutable rebuilt = false

        while i < remStart do
            if not rebuilt then
                let x = journal.Rems.Items[i]

                match reduction.sub red (mapping x) with
                | ValueSome s -> red <- s
                | ValueNone ->
                    this.Rebuild()
                    rebuilt <- true

            i <- i + 1

        i <- 0

        while i < addStart do
            if not rebuilt then
                let x = journal.Adds.Items[i]
                red <- reduction.add red (mapping x)

            i <- i + 1

        if not rebuilt then
            let remLive = journal.Rems.Count

            if remLive > remStart then
                Array.Copy(journal.Rems.Items, remStart, journal.Rems.Items, 0, remLive - remStart)
                journal.Rems.Count <- remLive - remStart
            else
                journal.Rems.Count <- 0

            let addLive = journal.Adds.Count

            if addLive > addStart then
                Array.Copy(journal.Adds.Items, addStart, journal.Adds.Items, 0, addLive - addStart)
                journal.Adds.Count <- addLive - addStart
            else
                journal.Adds.Count <- 0

            value <- reduction.view red

    interface ISetDeltaSink<'a> with
        member this.OnDeltas(adds: 'a[], addCnt: int, rems: 'a[], remCnt: int) =
            if not disposed then
                Collections.journalAppendSet &journal adds addCnt rems remCnt
                version <- version + 1L
                GraphContext.Default.MarkFrom(edges)

    interface IAdaptiveValue<'v> with
        member this.GetValue() =
            let ctx = GraphContext.Default
            ctx.ClaimOwner()

            try
                if disposed then
                    invalidOp "This adaptive value has been disposed."

                if not initialized then
                    initialized <- true
                    // Snapshot first, register between (see MapSetNode.EnsureInitialized
                    // in SetNodes.fs): the mapping is user code that may write to
                    // the source, and the write must land in our journal. A dirty
                    // source draining during the snapshot read pushes to nobody.
                    let snapshot = HashSet<'a>(source.GetValue())
                    this.Register()
                    let mutable acc = reduction.seed

                    for x in snapshot do
                        acc <- reduction.add acc (mapping x)

                    red <- acc
                    value <- reduction.view red
                    depVersions[0] <- source.Version

                if source.Version <> depVersions[0] then
                    source.GetValue() |> ignore
                    depVersions[0] <- source.Version

                if not journal.IsEmpty then
                    this.Drain()

                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) version
                value
            finally
                ctx.ReleaseOwner()

        member _.Version = version

    interface IEdgeTarget with
        member _.EdgeCount = edges.Count
        member _.AddEdge(parent: IAdaptiveNode, depIndex: int) = edges.Add(parent, depIndex)
        member _.RemoveEdgeAt(index: int) = edges.RemoveAt(index)

    interface IDisposable with
        member this.Dispose() =
            if not disposed then
                disposed <- true
                this.Unregister()

/// <summary>
/// A delta-driven reduction over a map. Keeps a mirror of the source
/// values so removals and updates can invert (<c>sub</c> receives the old
/// mapped value). The mapping is applied per journal element at drain time.
/// </summary>
type MapReduceNode<'k, 'a, 'b, 's, 'v when 'k: equality>
    (source: IAdaptiveMap<'k, 'a>, [<InlineIfLambda>] mapping: 'k -> 'a -> 'b, reduction: AdaptiveReduction<'b, 's, 'v>)
    =
    let mutable version = 0L
    let mutable edges = ParentEdges()
    let mutable depVersions = [| 0L |]
    let mutable journal = MapDelta<'k, 'a>.Create()
    let mirror = Dictionary<'k, 'a>()
    let mutable initialized = false
    let mutable disposed = false
    let mutable red = reduction.seed
    let mutable value = reduction.view reduction.seed

    member private this.Register() =
        match box source with
        | :? IMapSinkRegistry as r -> r.AddMapSink(box (this :> IMapDeltaSink<'k, 'a>))
        | _ -> ()

    member private this.Unregister() =
        match box source with
        | :? IMapSinkRegistry as r -> r.RemoveMapSink(box (this :> IMapDeltaSink<'k, 'a>))
        | _ -> ()

    /// Full recompute from the current view, rebuilding the mirror. Consumes
    /// the whole journal: the rebuilt state reflects every pending delta.
    member private this.Rebuild() =
        mirror.Clear()
        let mutable acc = reduction.seed

        for KeyValue(k, v) in source.GetValue() do
            mirror[k] <- v
            acc <- reduction.add acc (mapping k v)

        red <- acc
        value <- reduction.view red
        journal.Clear()

    /// Apply the journal to the reduction state. A Set on an existing key
    /// subtracts the old mapped value, then adds the new one.
    member private this.Drain() =
        let remStart = journal.Rems.Count
        let setStart = journal.Sets.Count
        let mutable i = 0
        let mutable rebuilt = false

        while i < remStart do
            if not rebuilt then
                let k = journal.Rems.Items[i]
                let mutable old = Unchecked.defaultof<'a>

                if mirror.TryGetValue(k, &old) then
                    match reduction.sub red (mapping k old) with
                    | ValueSome s -> red <- s
                    | ValueNone ->
                        this.Rebuild()
                        rebuilt <- true

                    if not rebuilt then
                        mirror.Remove k |> ignore

            i <- i + 1

        i <- 0

        while i < setStart do
            if not rebuilt then
                let struct (k, v) = journal.Sets.Items[i]
                let mutable old = Unchecked.defaultof<'a>

                if mirror.TryGetValue(k, &old) then
                    match reduction.sub red (mapping k old) with
                    | ValueSome s -> red <- s
                    | ValueNone ->
                        this.Rebuild()
                        rebuilt <- true

                if not rebuilt then
                    red <- reduction.add red (mapping k v)
                    mirror[k] <- v

            i <- i + 1

        if not rebuilt then
            let remLive = journal.Rems.Count

            if remLive > remStart then
                Array.Copy(journal.Rems.Items, remStart, journal.Rems.Items, 0, remLive - remStart)
                journal.Rems.Count <- remLive - remStart
            else
                journal.Rems.Count <- 0

            let setLive = journal.Sets.Count

            if setLive > setStart then
                Array.Copy(journal.Sets.Items, setStart, journal.Sets.Items, 0, setLive - setStart)
                journal.Sets.Count <- setLive - setStart
            else
                journal.Sets.Count <- 0

            value <- reduction.view red

    interface IMapDeltaSink<'k, 'a> with
        member this.OnDeltas(sets: struct ('k * 'a)[], setCnt: int, rems: 'k[], remCnt: int) =
            if not disposed then
                Collections.journalAppendMap &journal sets setCnt rems remCnt
                version <- version + 1L
                GraphContext.Default.MarkFrom(edges)

    interface IAdaptiveValue<'v> with
        member this.GetValue() =
            let ctx = GraphContext.Default
            ctx.ClaimOwner()

            try
                if disposed then
                    invalidOp "This adaptive value has been disposed."

                if not initialized then
                    initialized <- true
                    // Snapshot first, register between (see the set reduction node).
                    let snapshot = Dictionary<'k, 'a>()

                    for KeyValue(k, v) in source.GetValue() do
                        snapshot[k] <- v

                    this.Register()
                    let mutable acc = reduction.seed

                    for KeyValue(k, v) in snapshot do
                        mirror[k] <- v
                        acc <- reduction.add acc (mapping k v)

                    red <- acc
                    value <- reduction.view red
                    depVersions[0] <- source.Version

                if source.Version <> depVersions[0] then
                    source.GetValue() |> ignore
                    depVersions[0] <- source.Version

                if not journal.IsEmpty then
                    this.Drain()

                AdaptiveRuntime.addDependency (this :> IAdaptiveObject) version
                value
            finally
                ctx.ReleaseOwner()

        member _.Version = version

    interface IEdgeTarget with
        member _.EdgeCount = edges.Count
        member _.AddEdge(parent: IAdaptiveNode, depIndex: int) = edges.Add(parent, depIndex)
        member _.RemoveEdgeAt(index: int) = edges.RemoveAt(index)

    interface IDisposable with
        member this.Dispose() =
            if not disposed then
                disposed <- true
                this.Unregister()
