namespace AdaptiveSlop.Core

open System
open System.Collections.Generic
open System.Collections.Frozen

// =============================================================================
// Collection contracts (PLAN.md Section 6.9)
// =============================================================================

/// <summary>
/// An adaptive set: either a changeable source or a derived node.
/// </summary>
/// <remarks>
/// <para>
/// <c>GetValue</c> returns a transient view of the internal state. The view is
/// valid only until the next write on the owner thread. Computations consume it;
/// they must not retain it or mutate it. <c>ASet.force</c> materializes an
/// immutable <see cref="FrozenSet&lt;'T&gt;"/> that is safe to retain; the library
/// never touches a forced value again.
/// </para>
/// <para>
/// Derived sets are disposable: disposal unregisters the node from its
/// dependencies and stops all delta processing. Disposing a changeable source is
/// a no-op; sources are owned by the application. Dispose derived nodes before
/// their consumers. Reading a disposed node throws.
/// </para>
/// </remarks>
type IAdaptiveSet<'T> =
    inherit IAdaptiveObject
    inherit IDisposable
    abstract member GetValue: unit -> IReadOnlySet<'T>

/// <summary>
/// An adaptive map: either a changeable source or a derived node. See
/// <see cref="IAdaptiveSet&lt;'T&gt;"/> for the view and disposal contracts.
/// </summary>
type IAdaptiveMap<'K, 'V when 'K: equality> =
    inherit IAdaptiveObject
    inherit IDisposable
    abstract member GetValue: unit -> IReadOnlyDictionary<'K, 'V>

/// <summary>
/// Internal. Receives deltas from a set dependency. The implementation appends
/// the delta to its journal; processing happens on the next read (drain).
/// </summary>
type internal ISetDeltaSink<'T> =
    abstract member OnDeltas: added: 'T[] * addedCount: int * removed: 'T[] * removedCount: int -> unit

/// <summary>
/// Internal. Receives deltas from a map dependency. The implementation appends
/// the delta to its journal; processing happens on the next read (drain).
/// </summary>
type internal IMapDeltaSink<'K, 'V> =
    abstract member OnDeltas:
        setEntries: struct ('K * 'V)[] * setCount: int * removedKeys: 'K[] * removedCount: int -> unit

/// <summary>Internal. Register/unregister a set delta sink with a dependency.</summary>
type internal ISetSinkRegistry =
    abstract member AddSetSink: sink: obj -> unit
    abstract member RemoveSetSink: sink: obj -> unit

/// <summary>Internal. Register/unregister a map delta sink with a dependency.</summary>
type internal IMapSinkRegistry =
    abstract member AddMapSink: sink: obj -> unit
    abstract member RemoveMapSink: sink: obj -> unit

// =============================================================================
// Struct state holders (PLAN.md Section 6.9, shared code)
//
// The node state lives in structs so the shared operations can address it
// without abstract classes or virtual dispatch. F# forbids byref parameters in
// inline functions (FS0412), and per-element byref calls through a byref
// parameter allocate (measured 24 B per call), so:
//   - the hot per-element operations take the state struct BY VALUE and return
//     the updated struct (zero allocation, small copies);
//   - byref parameters appear only at the top-level call sites (a class field
//     address: measured zero allocation).
// =============================================================================

/// <summary>One reusable array plus count. Node-owned; grows amortized.</summary>
[<Struct>]
type internal DeltaBuffer<'T> =
    val mutable Items: 'T[]
    val mutable Count: int

    new(items: 'T[], count: int) = { Items = items; Count = count }

    static member Create() = DeltaBuffer(Array.zeroCreate 16, 0)

    member this.IsEmpty = this.Count = 0

/// <summary>
/// A set delta: elements added and removed since the previous delivery.
/// Passed to <see cref="ASet.observe"/> callbacks. The buffers are transient:
/// valid only during the callback that received the delta.
/// </summary>
[<Struct>]
type SetDelta<'T> =
    val mutable internal Adds: DeltaBuffer<'T>
    val mutable internal Rems: DeltaBuffer<'T>

    internal new(adds: DeltaBuffer<'T>, rems: DeltaBuffer<'T>) = { Adds = adds; Rems = rems }

    static member internal Create() =
        SetDelta(DeltaBuffer<_>.Create(), DeltaBuffer<_>.Create())

    /// <summary>Gets whether this delta contains no operations.</summary>
    member this.IsEmpty = this.Adds.IsEmpty && this.Rems.IsEmpty

    member internal this.Clear() =
        this.Adds.Count <- 0
        this.Rems.Count <- 0

    /// <summary>The elements added. Transient: valid during the callback only.</summary>
    member this.Added = this.Adds.Items.AsMemory(0, this.Adds.Count)

    /// <summary>The elements removed. Transient: valid during the callback only.</summary>
    member this.Removed = this.Rems.Items.AsMemory(0, this.Rems.Count)

    /// <summary>Appends an add operation. For <see cref="ASet.custom"/> computes.</summary>
    member this.Add(item: 'T) =
        if isNull this.Adds.Items then
            this.Adds.Items <- Array.zeroCreate 16
        elif this.Adds.Count = this.Adds.Items.Length then
            let next = Array.zeroCreate (this.Adds.Items.Length * 2)
            Array.Copy(this.Adds.Items, next, this.Adds.Items.Length)
            this.Adds.Items <- next

        this.Adds.Items[this.Adds.Count] <- item
        this.Adds.Count <- this.Adds.Count + 1

    /// <summary>Appends a remove operation. For <see cref="ASet.custom"/> computes.</summary>
    member this.Remove(item: 'T) =
        if isNull this.Rems.Items then
            this.Rems.Items <- Array.zeroCreate 16
        elif this.Rems.Count = this.Rems.Items.Length then
            let next = Array.zeroCreate (this.Rems.Items.Length * 2)
            Array.Copy(this.Rems.Items, next, this.Rems.Items.Length)
            this.Rems.Items <- next

        this.Rems.Items[this.Rems.Count] <- item
        this.Rems.Count <- this.Rems.Count + 1

/// <summary>
/// A mutable delta builder for <see cref="ASet.custom"/> computes. The compute
/// receives the current view and this builder, appends the operations that
/// describe the change since the previous call, and returns. The builder is a
/// class: appends mutate the node's pending delta directly (a struct delta
/// passed by value would be copied and lost).
/// </summary>
type SetDeltaBuilder<'T>() =
    let mutable adds = DeltaBuffer<_>.Create()
    let mutable rems = DeltaBuffer<_>.Create()

    /// <summary>Appends an add operation.</summary>
    member _.Add(item: 'T) =
        if adds.Count = adds.Items.Length then
            let next = Array.zeroCreate (adds.Items.Length * 2)
            Array.Copy(adds.Items, next, adds.Items.Length)
            adds.Items <- next

        adds.Items[adds.Count] <- item
        adds.Count <- adds.Count + 1

    /// <summary>Appends a remove operation.</summary>
    member _.Remove(item: 'T) =
        if rems.Count = rems.Items.Length then
            let next = Array.zeroCreate (rems.Items.Length * 2)
            Array.Copy(rems.Items, next, rems.Items.Length)
            rems.Items <- next

        rems.Items[rems.Count] <- item
        rems.Count <- rems.Count + 1

    member internal _.IsEmpty = adds.IsEmpty && rems.IsEmpty

    member internal _.Clear() =
        adds.Count <- 0
        rems.Count <- 0

    member internal _.Adds = adds
    member internal _.Rems = rems

    member internal this.Snapshot() = SetDelta(adds, rems)

/// <summary>
/// A map delta: upserted entries and removed keys since the previous delivery.
/// Passed to <see cref="AMap.observe"/> callbacks. The buffers are transient:
/// valid only during the callback that received the delta.
/// </summary>
[<Struct>]
type MapDelta<'K, 'V> =
    val mutable internal Sets: DeltaBuffer<struct ('K * 'V)>
    val mutable internal Rems: DeltaBuffer<'K>

    internal new(sets: DeltaBuffer<struct ('K * 'V)>, rems: DeltaBuffer<'K>) = { Sets = sets; Rems = rems }

    static member internal Create() =
        MapDelta(DeltaBuffer<_>.Create(), DeltaBuffer<_>.Create())

    /// <summary>Gets whether this delta contains no operations.</summary>
    member this.IsEmpty = this.Sets.IsEmpty && this.Rems.IsEmpty

    member internal this.Clear() =
        this.Sets.Count <- 0
        this.Rems.Count <- 0

    /// <summary>The entries set (added or updated). Transient: valid during the callback only.</summary>
    member this.SetEntries = this.Sets.Items.AsMemory(0, this.Sets.Count)

    /// <summary>The keys removed. Transient: valid during the callback only.</summary>
    member this.RemovedKeys = this.Rems.Items.AsMemory(0, this.Rems.Count)

    /// <summary>Appends an upsert operation. For <see cref="AMap.custom"/> computes.</summary>
    member this.Set(key: 'K, value: 'V) =
        if isNull this.Sets.Items then
            this.Sets.Items <- Array.zeroCreate 16
        elif this.Sets.Count = this.Sets.Items.Length then
            let next = Array.zeroCreate (this.Sets.Items.Length * 2)
            Array.Copy(this.Sets.Items, next, this.Sets.Items.Length)
            this.Sets.Items <- next

        this.Sets.Items[this.Sets.Count] <- struct (key, value)
        this.Sets.Count <- this.Sets.Count + 1

    /// <summary>Appends a remove operation. For <see cref="AMap.custom"/> computes.</summary>
    member this.Remove(key: 'K) =
        if isNull this.Rems.Items then
            this.Rems.Items <- Array.zeroCreate 16
        elif this.Rems.Count = this.Rems.Items.Length then
            let next = Array.zeroCreate (this.Rems.Items.Length * 2)
            Array.Copy(this.Rems.Items, next, this.Rems.Items.Length)
            this.Rems.Items <- next

        this.Rems.Items[this.Rems.Count] <- key
        this.Rems.Count <- this.Rems.Count + 1

/// <summary>
/// A mutable delta builder for <see cref="AMap.custom"/> computes. See
/// <see cref="SetDeltaBuilder&lt;'T&gt;"/> for the protocol.
/// </summary>
type MapDeltaBuilder<'K, 'V>() =
    let mutable sets = DeltaBuffer<_>.Create()
    let mutable rems = DeltaBuffer<_>.Create()

    /// <summary>Appends an upsert operation.</summary>
    member _.Set(key: 'K, value: 'V) =
        if sets.Count = sets.Items.Length then
            let next = Array.zeroCreate (sets.Items.Length * 2)
            Array.Copy(sets.Items, next, sets.Items.Length)
            sets.Items <- next

        sets.Items[sets.Count] <- struct (key, value)
        sets.Count <- sets.Count + 1

    /// <summary>Appends a remove operation.</summary>
    member _.Remove(key: 'K) =
        if rems.Count = rems.Items.Length then
            let next = Array.zeroCreate (rems.Items.Length * 2)
            Array.Copy(rems.Items, next, rems.Items.Length)
            rems.Items <- next

        rems.Items[rems.Count] <- key
        rems.Count <- rems.Count + 1

    member internal _.IsEmpty = sets.IsEmpty && rems.IsEmpty

    member internal _.Clear() =
        sets.Count <- 0
        rems.Count <- 0

    member internal _.Sets = sets
    member internal _.Rems = rems

    member internal this.Snapshot() = MapDelta(sets, rems)

/// <summary>
/// Registered consumers of a collection node. Passed by value to the push
/// operations, so reentrant sink growth during delivery is safe.
/// </summary>
[<Struct>]
type internal SinkList =
    val mutable Sinks: obj[]
    val mutable Count: int

    new(sinks: obj[], count: int) = { Sinks = sinks; Count = count }

    static member Create() = SinkList(Array.zeroCreate 4, 0)

    member this.IsEmpty = this.Count = 0

/// <summary>
/// A set with per-element reference counts: two source elements can map onto one
/// output element, and the output element disappears only when the last source
/// reference disappears.
/// </summary>
[<Struct>]
type internal RefCountedSet<'T when 'T: equality> =
    val mutable Data: HashSet<'T>
    val mutable Refcounts: Dictionary<'T, int>

    new(data: HashSet<'T>, refcounts: Dictionary<'T, int>) = { Data = data; Refcounts = refcounts }

    static member Create() =
        RefCountedSet(HashSet<'T>(), Dictionary<'T, int>())

/// <summary>
/// State of a derived set node (map over set, filter, union). 'T is the input
/// element type (the journal holds input-coordinate deltas); 'U is the output
/// element type (the state and output deltas live in output coordinates).
/// </summary>
[<Struct>]
type internal SetNodeState<'T, 'U when 'U: equality> =
    val mutable Version: int64
    val mutable Edges: ParentEdges
    val mutable Sinks: SinkList
    val mutable DepVersions: int64[]
    val mutable Set: RefCountedSet<'U>
    val mutable Journal: SetDelta<'T>
    val mutable Out: SetDelta<'U>

    new
        (
            version: int64,
            edges: ParentEdges,
            sinks: SinkList,
            depVersions: int64[],
            set: RefCountedSet<'U>,
            journal: SetDelta<'T>,
            out: SetDelta<'U>
        ) =
        { Version = version
          Edges = edges
          Sinks = sinks
          DepVersions = depVersions
          Set = set
          Journal = journal
          Out = out }

    static member Create(depCount: int) =
        SetNodeState(
            0L,
            ParentEdges(),
            SinkList.Create(),
            Array.zeroCreate depCount,
            RefCountedSet.Create(),
            SetDelta<_>.Create(),
            SetDelta<_>.Create()
        )

/// <summary>
/// State of a derived map node (map over map, filter). The journal holds
/// input-coordinate deltas (source entries); the state and output deltas live in
/// output coordinates.
/// </summary>
[<Struct>]
type internal MapNodeState<'K, 'V, 'U when 'K: equality> =
    val mutable Version: int64
    val mutable Edges: ParentEdges
    val mutable Sinks: SinkList
    val mutable DepVersions: int64[]
    val mutable Data: Dictionary<'K, 'U>
    val mutable Journal: MapDelta<'K, 'V>
    val mutable Out: MapDelta<'K, 'U>

    new
        (
            version: int64,
            edges: ParentEdges,
            sinks: SinkList,
            depVersions: int64[],
            data: Dictionary<'K, 'U>,
            journal: MapDelta<'K, 'V>,
            out: MapDelta<'K, 'U>
        ) =
        { Version = version
          Edges = edges
          Sinks = sinks
          DepVersions = depVersions
          Data = data
          Journal = journal
          Out = out }

    static member Create(depCount: int) =
        MapNodeState(
            0L,
            ParentEdges(),
            SinkList.Create(),
            Array.zeroCreate depCount,
            Dictionary<'K, 'U>(),
            MapDelta<_, _>.Create(),
            MapDelta<_, _>.Create()
        )

// =============================================================================
// Shared operations
// =============================================================================

/// <summary>
/// The binary set operation of <see cref="TwoSourceSetNode&lt;'T&gt;"/>:
/// difference (left minus right), intersection, or symmetric difference.
/// </summary>
type TwoSetOp =
    | Difference
    | Intersect
    | Xor

module internal Collections =

    /// Grow an array to hold at least n items. Amortized O(1); array growth only.
    let ensureCapacity (arr: 'T[] byref) (n: int) =
        if arr.Length < n then
            let next = Array.zeroCreate (max n (arr.Length * 2))
            Array.Copy(arr, next, arr.Length)
            arr <- next

    /// Append one item to a delta buffer. By value: the per-element hot path
    /// must not pass byrefs through byref parameters (measured 24 B per call).
    let bufferAppend (buffer: DeltaBuffer<'T>) (item: 'T) : DeltaBuffer<'T> =
        if buffer.Count = buffer.Items.Length then
            let next = Array.zeroCreate (buffer.Items.Length * 2)
            Array.Copy(buffer.Items, next, buffer.Items.Length)
            next[buffer.Count] <- item
            DeltaBuffer(next, buffer.Count + 1)
        else
            buffer.Items[buffer.Count] <- item
            DeltaBuffer(buffer.Items, buffer.Count + 1)

    /// Append a delta to a set journal (called at write time by the pusher).
    let journalAppendSet (journal: SetDelta<'T> byref) (adds: 'T[]) (addCnt: int) (rems: 'T[]) (remCnt: int) =
        ensureCapacity &journal.Adds.Items (journal.Adds.Count + addCnt)
        Array.Copy(adds, 0, journal.Adds.Items, journal.Adds.Count, addCnt)
        journal.Adds.Count <- journal.Adds.Count + addCnt
        ensureCapacity &journal.Rems.Items (journal.Rems.Count + remCnt)
        Array.Copy(rems, 0, journal.Rems.Items, journal.Rems.Count, remCnt)
        journal.Rems.Count <- journal.Rems.Count + remCnt

    /// Append a delta to a map journal (called at write time by the pusher).
    let journalAppendMap
        (journal: MapDelta<'K, 'V> byref)
        (sets: struct ('K * 'V)[])
        (setCnt: int)
        (rems: 'K[])
        (remCnt: int)
        =
        ensureCapacity &journal.Sets.Items (journal.Sets.Count + setCnt)
        Array.Copy(sets, 0, journal.Sets.Items, journal.Sets.Count, setCnt)
        journal.Sets.Count <- journal.Sets.Count + setCnt
        ensureCapacity &journal.Rems.Items (journal.Rems.Count + remCnt)
        Array.Copy(rems, 0, journal.Rems.Items, journal.Rems.Count, remCnt)
        journal.Rems.Count <- journal.Rems.Count + remCnt

    /// Register a sink. Returns nothing; the caller decides when to unregister.
    let addSink (sinks: SinkList byref) (sink: obj) =
        ensureCapacity &sinks.Sinks (sinks.Count + 1)
        sinks.Sinks[sinks.Count] <- sink
        sinks.Count <- sinks.Count + 1

    /// Remove a sink by identity.
    let removeSink (sinks: SinkList byref) (sink: obj) =
        let mutable found = -1
        let mutable i = 0

        while found < 0 && i < sinks.Count do
            if obj.ReferenceEquals(sinks.Sinks[i], sink) then
                found <- i
            else
                i <- i + 1

        if found >= 0 then
            sinks.Count <- sinks.Count - 1

            for j in found .. sinks.Count - 1 do
                sinks.Sinks[j] <- sinks.Sinks[j + 1]

            sinks.Sinks[sinks.Count] <- null

    /// Drop all sinks (disposal): releases the downstream references.
    let clearSinks (sinks: SinkList byref) =
        Array.Clear(sinks.Sinks, 0, sinks.Count)
        sinks.Count <- 0

    /// Add one reference to a refcounted set. By value: per-element hot path.
    /// Returns the updated state and whether the element is newly present.
    ///
    /// Note: the F# `match dict.TryGetValue key with | true, v ->` pattern
    /// allocates 24 B per call (measured); the explicit out-param form is
    /// zero-allocation. Hot paths must use the explicit form.
    let refAdd (state: RefCountedSet<'T>) (item: 'T) : struct (RefCountedSet<'T> * bool) =
        let mutable n = 0

        if state.Refcounts.TryGetValue(item, &n) then
            state.Refcounts[item] <- n + 1
            struct (state, false)
        else
            state.Refcounts[item] <- 1
            struct (state, state.Data.Add item)

    /// Remove one reference from a refcounted set. By value: per-element hot
    /// path. Returns the updated state and whether the element is fully removed.
    let refRemove (state: RefCountedSet<'T>) (item: 'T) : struct (RefCountedSet<'T> * bool) =
        let mutable n = 0

        if state.Refcounts.TryGetValue(item, &n) then
            if n = 1 then
                state.Refcounts.Remove item |> ignore
                struct (state, state.Data.Remove item)
            else
                state.Refcounts[item] <- n - 1
                struct (state, false)
        else
            struct (state, false)

    /// Push a set delta to every registered sink. The sink list is copied, so
    /// reentrant sink growth during delivery is safe.
    let pushSetDelta (sinks: SinkList) (delta: SetDelta<'T>) =
        if not delta.IsEmpty then
            let adds = delta.Adds.Items
            let addCnt = delta.Adds.Count
            let rems = delta.Rems.Items
            let remCnt = delta.Rems.Count

            for i in 0 .. sinks.Count - 1 do
                (unbox<ISetDeltaSink<'T>> sinks.Sinks[i]).OnDeltas(adds, addCnt, rems, remCnt)

    /// Push a map delta to every registered sink. The sink list is copied, so
    /// reentrant sink growth during delivery is safe.
    let pushMapDelta (sinks: SinkList) (delta: MapDelta<'K, 'V>) =
        if not delta.IsEmpty then
            let sets = delta.Sets.Items
            let setCnt = delta.Sets.Count
            let rems = delta.Rems.Items
            let remCnt = delta.Rems.Count

            for i in 0 .. sinks.Count - 1 do
                (unbox<IMapDeltaSink<'K, 'V>> sinks.Sinks[i]).OnDeltas(sets, setCnt, rems, remCnt)

    /// Push a delta and mark the scalar parents of a source, with notification
    /// delivery deferred to the end of the operation (PLAN.md Section 6.5).
    let pushAndMarkSet (delta: SetDelta<'T>) (sinks: SinkList) (edges: ParentEdges) =
        let ctx = GraphContext.Default
        let wasActive = ctx.TxActive
        ctx.TxActive <- true

        try
            pushSetDelta sinks delta
            ctx.MarkFrom(edges)
        finally
            ctx.TxActive <- wasActive

        if not wasActive then
            ctx.DeliverNotifications()

    /// Push a delta and mark the scalar parents of a source, with notification
    /// delivery deferred to the end of the operation (PLAN.md Section 6.5).
    let pushAndMarkMap (delta: MapDelta<'K, 'V>) (sinks: SinkList) (edges: ParentEdges) =
        let ctx = GraphContext.Default
        let wasActive = ctx.TxActive
        ctx.TxActive <- true

        try
            pushMapDelta sinks delta
            ctx.MarkFrom(edges)
        finally
            ctx.TxActive <- wasActive

        if not wasActive then
            ctx.DeliverNotifications()

    /// Drain the journal of a set node with refcounts (map over set, union):
    /// apply each pending delta to the state and collect the reduced output
    /// delta. Entries appended during processing (reentrant writes) survive.
    /// Returns the updated state and whether the state changed.
    let inline drainRefSet
        ([<InlineIfLambda>] map: 'T -> 'U voption)
        (state: SetNodeState<'T, 'U>)
        : struct (SetNodeState<'T, 'U> * bool) =
        let mutable s = state
        let mutable changed = false
        let rems = s.Journal.Rems
        let adds = s.Journal.Adds
        let remStart = rems.Count
        let addStart = adds.Count
        let mutable i = 0

        while i < remStart do
            let x = rems.Items[i]

            match map x with
            | ValueSome y ->
                let struct (set2, removed) = refRemove s.Set y
                s.Set <- set2

                if removed then
                    s.Out.Rems <- bufferAppend s.Out.Rems y
                    changed <- true
            | ValueNone -> ()

            i <- i + 1

        i <- 0

        while i < addStart do
            let y = adds.Items[i]

            match map y with
            | ValueSome z ->
                let struct (set2, added) = refAdd s.Set z
                s.Set <- set2

                if added then
                    s.Out.Adds <- bufferAppend s.Out.Adds z
                    changed <- true
            | ValueNone -> ()

            i <- i + 1

        // Compact: keep entries appended during processing (reentrant writes).
        let remLive = s.Journal.Rems.Count

        if remLive > remStart then
            Array.Copy(s.Journal.Rems.Items, remStart, s.Journal.Rems.Items, 0, remLive - remStart)
            s.Journal.Rems.Count <- remLive - remStart
        else
            s.Journal.Rems.Count <- 0

        let addLive = s.Journal.Adds.Count

        if addLive > addStart then
            Array.Copy(s.Journal.Adds.Items, addStart, s.Journal.Adds.Items, 0, addLive - addStart)
            s.Journal.Adds.Count <- addLive - addStart
        else
            s.Journal.Adds.Count <- 0

        struct (s, changed)

    /// Drain the journal of a set node without refcounts (filter): plain
    /// membership. Returns the updated state and whether the state changed.
    let inline drainPlainSet
        ([<InlineIfLambda>] map: 'T -> 'T voption)
        (state: SetNodeState<'T, 'T>)
        : struct (SetNodeState<'T, 'T> * bool) =
        let mutable s = state
        let mutable changed = false
        let rems = s.Journal.Rems
        let adds = s.Journal.Adds
        let remStart = rems.Count
        let addStart = adds.Count
        let mutable i = 0

        while i < remStart do
            let x = rems.Items[i]

            if s.Set.Data.Remove x then
                s.Out.Rems <- bufferAppend s.Out.Rems x
                changed <- true

            i <- i + 1

        i <- 0

        while i < addStart do
            let x = adds.Items[i]

            match map x with
            | ValueSome z ->
                if s.Set.Data.Add z then
                    s.Out.Adds <- bufferAppend s.Out.Adds z
                    changed <- true
            | ValueNone -> ()

            i <- i + 1

        let remLive = s.Journal.Rems.Count

        if remLive > remStart then
            Array.Copy(s.Journal.Rems.Items, remStart, s.Journal.Rems.Items, 0, remLive - remStart)
            s.Journal.Rems.Count <- remLive - remStart
        else
            s.Journal.Rems.Count <- 0

        let addLive = s.Journal.Adds.Count

        if addLive > addStart then
            Array.Copy(s.Journal.Adds.Items, addStart, s.Journal.Adds.Items, 0, addLive - addStart)
            s.Journal.Adds.Count <- addLive - addStart
        else
            s.Journal.Adds.Count <- 0

        struct (s, changed)

    /// Drain the journal of a map node: apply each pending delta to the state
    /// and collect the reduced output delta. The lambda returns ValueNone for
    /// elements to drop (filter). Returns the updated state and whether the
    /// state changed.
    let inline drainMap
        ([<InlineIfLambda>] map: 'K -> 'V -> 'U voption)
        (state: MapNodeState<'K, 'V, 'U>)
        : struct (MapNodeState<'K, 'V, 'U> * bool) =
        let mutable s = state
        let mutable changed = false
        let rems = s.Journal.Rems
        let sets = s.Journal.Sets
        let remStart = rems.Count
        let setStart = sets.Count
        let mutable i = 0

        while i < remStart do
            let k = rems.Items[i]

            if s.Data.Remove k then
                s.Out.Rems <- bufferAppend s.Out.Rems k
                changed <- true

            i <- i + 1

        i <- 0

        while i < setStart do
            let struct (k, v) = sets.Items[i]

            match map k v with
            | ValueSome u ->
                let mutable old = Unchecked.defaultof<'U>

                if s.Data.TryGetValue(k, &old) && EqualityComparer<'U>.Default.Equals(old, u) then
                    ()
                else
                    s.Data[k] <- u
                    s.Out.Sets <- bufferAppend s.Out.Sets (struct (k, u))
                    changed <- true
            | ValueNone ->
                if s.Data.Remove k then
                    s.Out.Rems <- bufferAppend s.Out.Rems k
                    changed <- true

            i <- i + 1

        let remLive = s.Journal.Rems.Count

        if remLive > remStart then
            Array.Copy(s.Journal.Rems.Items, remStart, s.Journal.Rems.Items, 0, remLive - remStart)
            s.Journal.Rems.Count <- remLive - remStart
        else
            s.Journal.Rems.Count <- 0

        let setLive = s.Journal.Sets.Count

        if setLive > setStart then
            Array.Copy(s.Journal.Sets.Items, setStart, s.Journal.Sets.Items, 0, setLive - setStart)
            s.Journal.Sets.Count <- setLive - setStart
        else
            s.Journal.Sets.Count <- 0

        struct (s, changed)

    /// Drain a set node and push the reduced output delta to its sinks, with
    /// notification delivery deferred (PLAN.md Section 6.5). The byref appears
    /// only at this top-level call site (a class field address: 0 allocation).
    let inline drainSetPush ([<InlineIfLambda>] map: 'T -> 'U voption) (state: SetNodeState<'T, 'U> byref) =
        let ctx = GraphContext.Default
        let wasActive = ctx.TxActive
        ctx.TxActive <- true

        try
            let struct (s2, changed) = drainRefSet map state
            state <- s2

            if changed then
                pushSetDelta state.Sinks state.Out
                state.Out.Clear()
        finally
            ctx.TxActive <- wasActive

        if not wasActive then
            ctx.DeliverNotifications()

    /// Drain a plain set node (filter) and push the reduced output delta.
    let inline drainPlainSetPush ([<InlineIfLambda>] map: 'T -> 'T voption) (state: SetNodeState<'T, 'T> byref) =
        let ctx = GraphContext.Default
        let wasActive = ctx.TxActive
        ctx.TxActive <- true

        try
            let struct (s2, changed) = drainPlainSet map state
            state <- s2

            if changed then
                pushSetDelta state.Sinks state.Out
                state.Out.Clear()
        finally
            ctx.TxActive <- wasActive

        if not wasActive then
            ctx.DeliverNotifications()

    /// Drain a map node and push the reduced output delta to its sinks, with
    /// notification delivery deferred (PLAN.md Section 6.5).
    let inline drainMapPush ([<InlineIfLambda>] map: 'K -> 'V -> 'U voption) (state: MapNodeState<'K, 'V, 'U> byref) =
        let ctx = GraphContext.Default
        let wasActive = ctx.TxActive
        ctx.TxActive <- true

        try
            let struct (s2, changed) = drainMap map state
            state <- s2

            if changed then
                pushMapDelta state.Sinks state.Out
                state.Out.Clear()
        finally
            ctx.TxActive <- wasActive

        if not wasActive then
            ctx.DeliverNotifications()

    /// Initial load of a refcounted set node: read the source state and build
    /// the internal state. The source read also registers the dependency.
    let inline loadRefSet
        ([<InlineIfLambda>] map: 'T -> 'U voption)
        (source: IAdaptiveSet<'T>)
        (state: SetNodeState<'T, 'U> byref)
        =
        for item in source.GetValue() do
            match map item with
            | ValueSome z ->
                let struct (set2, _) = refAdd state.Set z
                state.Set <- set2
            | ValueNone -> ()

    /// Initial load of a plain set node (filter).
    let inline loadPlainSet
        ([<InlineIfLambda>] map: 'T -> 'T voption)
        (source: IAdaptiveSet<'T>)
        (state: SetNodeState<'T, 'T> byref)
        =
        for item in source.GetValue() do
            match map item with
            | ValueSome z -> state.Set.Data.Add z |> ignore
            | ValueNone -> ()

    /// Initial load of a map node.
    let inline loadMap
        ([<InlineIfLambda>] map: 'K -> 'V -> 'U voption)
        (source: IAdaptiveMap<'K, 'V>)
        (state: MapNodeState<'K, 'V, 'U> byref)
        =
        for KeyValue(k, v) in source.GetValue() do
            match map k v with
            | ValueSome u -> state.Data[k] <- u
            | ValueNone -> ()

    // =============================================================================
    // Two-source set algebra (PLAN.md Section 7.3): difference, intersect, xor.
    //
    // Each source has its own journal (side sinks route deliveries by side). The
    // state keeps per-side reference counts; the output membership is derived per
    // operation. Cross-side ordering does not matter: each side's counts update
    // independently, and the output transition is decided from the counts.
    // =============================================================================

    /// <summary>Internal. Receives side-routed set deltas of a two-source node.</summary>
    type internal ITwoSetSinkTarget<'T> =
        abstract member OnSideDeltas: side: int * adds: 'T[] * addCnt: int * rems: 'T[] * remCnt: int -> unit

    /// <summary>
    /// Internal. A per-side set sink: appends the delivery to the side's journal.
    /// Two instances per two-source node (construction-time allocation only).
    /// </summary>
    type internal SideSetSink<'T>(target: obj, side: int) =
        interface ISetDeltaSink<'T> with
            member this.OnDeltas(adds: 'T[], addCnt: int, rems: 'T[], remCnt: int) =
                (unbox<ITwoSetSinkTarget<'T>> target).OnSideDeltas(side, adds, addCnt, rems, remCnt)

    /// <summary>
    /// Internal. Routes a delivery to the node's side handler through a plain
    /// interface call (a stored multi-arg closure would invoke curried,
    /// allocating intermediate closures per delivery). The deltas cross as
    /// <c>obj</c> and are unboxed once per delivery by the node (zero
    /// allocation; arrays are reference types).
    /// </summary>
    type internal ISideMapSinkTarget =
        abstract member OnSideDeltas: side: int * sets: obj * setCnt: int * rems: obj * remCnt: int -> unit

    type internal SideMapSink<'K, 'V>(target: ISideMapSinkTarget, side: int) =
        interface IMapDeltaSink<'K, 'V> with
            member this.OnDeltas(sets: struct ('K * 'V)[], setCnt: int, rems: 'K[], remCnt: int) =
                target.OnSideDeltas(side, sets, setCnt, rems, remCnt)

    /// <summary>Internal. State of a two-source set node.</summary>
    [<Struct>]
    type internal TwoSetState<'T when 'T: equality> =
        val mutable Version: int64
        val mutable Edges: ParentEdges
        val mutable Sinks: SinkList
        val mutable DepVersions: int64[]
        val mutable Left: RefCountedSet<'T>
        val mutable Right: RefCountedSet<'T>
        val mutable Out: HashSet<'T>
        val mutable JournalL: SetDelta<'T>
        val mutable JournalR: SetDelta<'T>
        val mutable OutDelta: SetDelta<'T>

        new
            (
                version: int64,
                edges: ParentEdges,
                sinks: SinkList,
                depVersions: int64[],
                left: RefCountedSet<'T>,
                right: RefCountedSet<'T>,
                out: HashSet<'T>,
                journalL: SetDelta<'T>,
                journalR: SetDelta<'T>,
                outDelta: SetDelta<'T>
            ) =
            { Version = version
              Edges = edges
              Sinks = sinks
              DepVersions = depVersions
              Left = left
              Right = right
              Out = out
              JournalL = journalL
              JournalR = journalR
              OutDelta = outDelta }

        static member Create(depCount: int) =
            TwoSetState(
                0L,
                ParentEdges(),
                SinkList.Create(),
                Array.zeroCreate depCount,
                RefCountedSet.Create(),
                RefCountedSet.Create(),
                HashSet<'T>(),
                SetDelta<_>.Create(),
                SetDelta<_>.Create(),
                SetDelta<_>.Create()
            )

    /// <summary>
    /// Process one side's journal of a two-source set node. By value: the hot
    /// per-element path must not pass byrefs through byref parameters (measured
    /// 24 B per call). Returns the updated state and whether the output changed.
    /// </summary>
    let processTwoSide (op: TwoSetOp) (side: int) (s: TwoSetState<'T>) : struct (TwoSetState<'T> * bool) =
        let mutable s = s
        let mutable changed = false
        let rems = if side = 0 then s.JournalL.Rems else s.JournalR.Rems
        let adds = if side = 0 then s.JournalL.Adds else s.JournalR.Adds
        let remStart = rems.Count
        let addStart = adds.Count
        let mutable i = 0

        while i < remStart do
            let x = rems.Items[i]
            let mutable removed = false

            if side = 0 then
                let struct (set2, r) = refRemove s.Left x
                s.Left <- set2
                removed <- r
            else
                let struct (set2, r) = refRemove s.Right x
                s.Right <- set2
                removed <- r

            if removed then
                let otherHas =
                    if side = 0 then
                        s.Right.Data.Contains x
                    else
                        s.Left.Data.Contains x

                match op with
                | Difference ->
                    if side = 0 then
                        if not otherHas && s.Out.Remove x then
                            s.OutDelta.Rems <- bufferAppend s.OutDelta.Rems x
                            changed <- true
                    elif otherHas && s.Out.Add x then
                        s.OutDelta.Adds <- bufferAppend s.OutDelta.Adds x
                        changed <- true
                | Intersect ->
                    if otherHas && s.Out.Remove x then
                        s.OutDelta.Rems <- bufferAppend s.OutDelta.Rems x
                        changed <- true
                | Xor ->
                    if otherHas then
                        if s.Out.Add x then
                            s.OutDelta.Adds <- bufferAppend s.OutDelta.Adds x
                            changed <- true
                    elif s.Out.Remove x then
                        s.OutDelta.Rems <- bufferAppend s.OutDelta.Rems x
                        changed <- true

            i <- i + 1

        i <- 0

        while i < addStart do
            let x = adds.Items[i]
            let mutable added = false

            if side = 0 then
                let struct (set2, a) = refAdd s.Left x
                s.Left <- set2
                added <- a
            else
                let struct (set2, a) = refAdd s.Right x
                s.Right <- set2
                added <- a

            if added then
                let otherHas =
                    if side = 0 then
                        s.Right.Data.Contains x
                    else
                        s.Left.Data.Contains x

                match op with
                | Difference ->
                    if side = 0 then
                        if not otherHas && s.Out.Add x then
                            s.OutDelta.Adds <- bufferAppend s.OutDelta.Adds x
                            changed <- true
                    elif otherHas && s.Out.Remove x then
                        s.OutDelta.Rems <- bufferAppend s.OutDelta.Rems x
                        changed <- true
                | Intersect ->
                    if otherHas && s.Out.Add x then
                        s.OutDelta.Adds <- bufferAppend s.OutDelta.Adds x
                        changed <- true
                | Xor ->
                    if otherHas then
                        if s.Out.Remove x then
                            s.OutDelta.Rems <- bufferAppend s.OutDelta.Rems x
                            changed <- true
                    elif s.Out.Add x then
                        s.OutDelta.Adds <- bufferAppend s.OutDelta.Adds x
                        changed <- true

            i <- i + 1

        // Compact the side's journal: keep entries appended during processing.
        if side = 0 then
            let remLive = s.JournalL.Rems.Count

            if remLive > remStart then
                Array.Copy(s.JournalL.Rems.Items, remStart, s.JournalL.Rems.Items, 0, remLive - remStart)
                s.JournalL.Rems.Count <- remLive - remStart
            else
                s.JournalL.Rems.Count <- 0

            let addLive = s.JournalL.Adds.Count

            if addLive > addStart then
                Array.Copy(s.JournalL.Adds.Items, addStart, s.JournalL.Adds.Items, 0, addLive - addStart)
                s.JournalL.Adds.Count <- addLive - addStart
            else
                s.JournalL.Adds.Count <- 0
        else
            let remLive = s.JournalR.Rems.Count

            if remLive > remStart then
                Array.Copy(s.JournalR.Rems.Items, remStart, s.JournalR.Rems.Items, 0, remLive - remStart)
                s.JournalR.Rems.Count <- remLive - remStart
            else
                s.JournalR.Rems.Count <- 0

            let addLive = s.JournalR.Adds.Count

            if addLive > addStart then
                Array.Copy(s.JournalR.Adds.Items, addStart, s.JournalR.Adds.Items, 0, addLive - addStart)
                s.JournalR.Adds.Count <- addLive - addStart
            else
                s.JournalR.Adds.Count <- 0

        struct (s, changed)

    /// <summary>Drain both journals of a two-source set node. Returns the updated state and whether the output changed.</summary>
    let drainTwoSet (op: TwoSetOp) (s: TwoSetState<'T>) : struct (TwoSetState<'T> * bool) =
        let struct (s1, c1) = processTwoSide op 0 s
        let struct (s2, c2) = processTwoSide op 1 s1
        struct (s2, c1 || c2)

    /// <summary>
    /// Drain a two-source set node and push the reduced output delta to its sinks,
    /// with notification delivery deferred (PLAN.md Section 6.5). The byref appears
    /// only at this top-level call site (a class field address: 0 allocation).
    /// </summary>
    let drainTwoSetPush (op: TwoSetOp) (state: TwoSetState<'T> byref) =
        let ctx = GraphContext.Default
        let wasActive = ctx.TxActive
        ctx.TxActive <- true

        try
            let struct (s2, changed) = drainTwoSet op state
            state <- s2

            if changed then
                pushSetDelta state.Sinks state.OutDelta
                state.OutDelta.Clear()
        finally
            ctx.TxActive <- wasActive

        if not wasActive then
            ctx.DeliverNotifications()

    /// <summary>Initial load of a two-source set node: build the state from both source views.</summary>
    let loadTwoSet (op: TwoSetOp) (left: IAdaptiveSet<'T>) (right: IAdaptiveSet<'T>) (state: TwoSetState<'T> byref) =
        for x in left.GetValue() do
            let struct (set2, _) = refAdd state.Left x
            state.Left <- set2

            match op with
            | Difference
            | Xor -> state.Out.Add x |> ignore
            | Intersect -> () // the right loop seeds the intersection

        for x in right.GetValue() do
            let struct (set2, _) = refAdd state.Right x
            state.Right <- set2

            match op with
            | Difference -> state.Out.Remove x |> ignore
            | Intersect ->
                if state.Left.Data.Contains x then
                    state.Out.Add x |> ignore
            | Xor ->
                if state.Left.Data.Contains x then
                    state.Out.Remove x |> ignore
                else
                    state.Out.Add x |> ignore

    // =============================================================================
    // Two-source map algebra (PLAN.md Section 7.3): choose2, intersect(With),
    // union(With). One node shape; the mapping decides the semantics (FDA models
    // all of them on Choose2VReader). The mapping receives the key and both side
    // values (voptions) and returns the output value (voption). It is called only
    // when at least one side has a value (FDA parity: "mapping will always receive
    // at least one *Some* argument"); a key with no value on either side is removed
    // without calling it.
    // =============================================================================

    /// <summary>Internal. State of a choose2 map node.</summary>
    [<Struct>]
    type internal Choose2State<'K, 'V1, 'V2, 'V3 when 'K: equality> =
        val mutable Version: int64
        val mutable Edges: ParentEdges
        val mutable Sinks: SinkList
        val mutable DepVersions: int64[]
        val mutable Sides: Dictionary<'K, struct ('V1 voption * 'V2 voption)>
        val mutable Out: Dictionary<'K, 'V3>
        val mutable JournalL: MapDelta<'K, 'V1>
        val mutable JournalR: MapDelta<'K, 'V2>
        val mutable OutDelta: MapDelta<'K, 'V3>

        new
            (
                version: int64,
                edges: ParentEdges,
                sinks: SinkList,
                depVersions: int64[],
                sides: Dictionary<'K, struct ('V1 voption * 'V2 voption)>,
                out: Dictionary<'K, 'V3>,
                journalL: MapDelta<'K, 'V1>,
                journalR: MapDelta<'K, 'V2>,
                outDelta: MapDelta<'K, 'V3>
            ) =
            { Version = version
              Edges = edges
              Sinks = sinks
              DepVersions = depVersions
              Sides = sides
              Out = out
              JournalL = journalL
              JournalR = journalR
              OutDelta = outDelta }

        static member Create(depCount: int) =
            Choose2State(
                0L,
                ParentEdges(),
                SinkList.Create(),
                Array.zeroCreate depCount,
                Dictionary<'K, struct ('V1 voption * 'V2 voption)>(),
                Dictionary<'K, 'V3>(),
                MapDelta<_, _>.Create(),
                MapDelta<_, _>.Create(),
                MapDelta<_, _>.Create()
            )

    /// <summary>
    /// Apply one output transition of a choose2 drain: compare with the stored
    /// output, emit the delta (with equal-value elision), update the output.
    /// By value: the hot per-element path must not pass byrefs through byref
    /// parameters (measured 24 B per call).
    /// </summary>
    let applyChoose2Out
        (s: Choose2State<'K, 'V1, 'V2, 'V3>)
        (k: 'K)
        (newOut: 'V3 voption)
        : struct (Choose2State<'K, 'V1, 'V2, 'V3> * bool) =
        let mutable s = s
        let mutable changed = false
        let mutable old = Unchecked.defaultof<'V3>

        if s.Out.TryGetValue(k, &old) then
            if newOut.IsSome then
                let v = newOut.Value

                if EqualityComparer<'V3>.Default.Equals(old, v) then
                    ()
                else
                    s.Out[k] <- v
                    s.OutDelta.Sets <- bufferAppend s.OutDelta.Sets (struct (k, v))
                    changed <- true
            else
                s.Out.Remove k |> ignore
                s.OutDelta.Rems <- bufferAppend s.OutDelta.Rems k
                changed <- true
        elif newOut.IsSome then
            let v = newOut.Value
            s.Out[k] <- v
            s.OutDelta.Sets <- bufferAppend s.OutDelta.Sets (struct (k, v))
            changed <- true

        struct (s, changed)

    /// <summary>
    /// Process one side's journal of a choose2 node. The mapping is called only
    /// when at least one side has a value (FDA parity). Returns the updated state
    /// and whether the output changed.
    /// </summary>
    let inline processChoose2Side
        ([<InlineIfLambda>] mapping: 'K -> 'V1 voption -> 'V2 voption -> 'V3 voption)
        (side: int)
        (s: Choose2State<'K, 'V1, 'V2, 'V3>)
        : struct (Choose2State<'K, 'V1, 'V2, 'V3> * bool) =
        let mutable s = s
        let mutable changed = false

        // The two side journals have different value types, so the loops are
        // per side (an `if side` over the journals would unify 'V1 with 'V2).
        if side = 0 then
            let rems = s.JournalL.Rems
            let sets = s.JournalL.Sets
            let remStart = rems.Count
            let setStart = sets.Count
            let mutable i = 0

            while i < remStart do
                let k = rems.Items[i]
                let mutable cur = struct (ValueNone, ValueNone)

                if s.Sides.TryGetValue(k, &cur) then
                    let struct (_, rv) = cur

                    match rv with
                    | ValueNone ->
                        // both sides gone: remove without calling the mapping.
                        s.Sides.Remove k |> ignore
                        let struct (s2, c) = applyChoose2Out s k ValueNone
                        s <- s2
                        changed <- changed || c
                    | ValueSome _ ->
                        let newOut = mapping k ValueNone rv
                        let struct (s2, c) = applyChoose2Out s k newOut
                        s <- s2
                        changed <- changed || c
                        s.Sides[k] <- struct (ValueNone, rv)

                i <- i + 1

            i <- 0

            while i < setStart do
                let struct (k, v) = sets.Items[i]
                let mutable cur = struct (ValueNone, ValueNone)

                let rv =
                    if s.Sides.TryGetValue(k, &cur) then
                        let struct (_, rv) = cur
                        rv
                    else
                        ValueNone

                let newOut = mapping k (ValueSome v) rv
                let struct (s2, c) = applyChoose2Out s k newOut
                s <- s2
                changed <- changed || c
                s.Sides[k] <- struct (ValueSome v, rv)
                i <- i + 1

            let remLive = s.JournalL.Rems.Count

            if remLive > remStart then
                Array.Copy(s.JournalL.Rems.Items, remStart, s.JournalL.Rems.Items, 0, remLive - remStart)
                s.JournalL.Rems.Count <- remLive - remStart
            else
                s.JournalL.Rems.Count <- 0

            let setLive = s.JournalL.Sets.Count

            if setLive > setStart then
                Array.Copy(s.JournalL.Sets.Items, setStart, s.JournalL.Sets.Items, 0, setLive - setStart)
                s.JournalL.Sets.Count <- setLive - setStart
            else
                s.JournalL.Sets.Count <- 0
        else
            let rems = s.JournalR.Rems
            let sets = s.JournalR.Sets
            let remStart = rems.Count
            let setStart = sets.Count
            let mutable i = 0

            while i < remStart do
                let k = rems.Items[i]
                let mutable cur = struct (ValueNone, ValueNone)

                if s.Sides.TryGetValue(k, &cur) then
                    let struct (lv, _) = cur

                    match lv with
                    | ValueNone ->
                        // both sides gone: remove without calling the mapping.
                        s.Sides.Remove k |> ignore
                        let struct (s2, c) = applyChoose2Out s k ValueNone
                        s <- s2
                        changed <- changed || c
                    | ValueSome _ ->
                        let newOut = mapping k lv ValueNone
                        let struct (s2, c) = applyChoose2Out s k newOut
                        s <- s2
                        changed <- changed || c
                        s.Sides[k] <- struct (lv, ValueNone)

                i <- i + 1

            i <- 0

            while i < setStart do
                let struct (k, v) = sets.Items[i]
                let mutable cur = struct (ValueNone, ValueNone)

                let lv =
                    if s.Sides.TryGetValue(k, &cur) then
                        let struct (lv, _) = cur
                        lv
                    else
                        ValueNone

                let newOut = mapping k lv (ValueSome v)
                let struct (s2, c) = applyChoose2Out s k newOut
                s <- s2
                changed <- changed || c
                s.Sides[k] <- struct (lv, ValueSome v)
                i <- i + 1

            let remLive = s.JournalR.Rems.Count

            if remLive > remStart then
                Array.Copy(s.JournalR.Rems.Items, remStart, s.JournalR.Rems.Items, 0, remLive - remStart)
                s.JournalR.Rems.Count <- remLive - remStart
            else
                s.JournalR.Rems.Count <- 0

            let setLive = s.JournalR.Sets.Count

            if setLive > setStart then
                Array.Copy(s.JournalR.Sets.Items, setStart, s.JournalR.Sets.Items, 0, setLive - setStart)
                s.JournalR.Sets.Count <- setLive - setStart
            else
                s.JournalR.Sets.Count <- 0

        struct (s, changed)

    /// <summary>Drain both journals of a choose2 node. Returns the updated state and whether the output changed.</summary>
    let inline drainChoose2
        ([<InlineIfLambda>] mapping: 'K -> 'V1 voption -> 'V2 voption -> 'V3 voption)
        (s: Choose2State<'K, 'V1, 'V2, 'V3>)
        : struct (Choose2State<'K, 'V1, 'V2, 'V3> * bool) =
        let struct (s1, c1) = processChoose2Side mapping 0 s
        let struct (s2, c2) = processChoose2Side mapping 1 s1
        struct (s2, c1 || c2)

    /// <summary>
    /// Drain a choose2 node and push the reduced output delta to its sinks, with
    /// notification delivery deferred (PLAN.md Section 6.5).
    /// </summary>
    let inline drainChoose2Push
        ([<InlineIfLambda>] mapping: 'K -> 'V1 voption -> 'V2 voption -> 'V3 voption)
        (state: Choose2State<'K, 'V1, 'V2, 'V3> byref)
        =
        let ctx = GraphContext.Default
        let wasActive = ctx.TxActive
        ctx.TxActive <- true

        try
            let struct (s2, changed) = drainChoose2 mapping state
            state <- s2

            if changed then
                pushMapDelta state.Sinks state.OutDelta
                state.OutDelta.Clear()
        finally
            ctx.TxActive <- wasActive

        if not wasActive then
            ctx.DeliverNotifications()

    /// <summary>Initial load of a choose2 node: merge both source views through the mapping.</summary>
    let inline loadChoose2
        ([<InlineIfLambda>] mapping: 'K -> 'V1 voption -> 'V2 voption -> 'V3 voption)
        (left: IAdaptiveMap<'K, 'V1>)
        (right: IAdaptiveMap<'K, 'V2>)
        (state: Choose2State<'K, 'V1, 'V2, 'V3> byref)
        =
        for KeyValue(k, v) in left.GetValue() do
            state.Sides[k] <- struct (ValueSome v, ValueNone)

            match mapping k (ValueSome v) ValueNone with
            | ValueSome o -> state.Out[k] <- o
            | ValueNone -> ()

        for KeyValue(k, v) in right.GetValue() do
            let mutable cur = struct (ValueNone, ValueNone)

            if state.Sides.TryGetValue(k, &cur) then
                let struct (lv, _) = cur
                state.Sides[k] <- struct (lv, ValueSome v)

                match mapping k lv (ValueSome v) with
                | ValueSome o -> state.Out[k] <- o
                | ValueNone -> state.Out.Remove k |> ignore
            else
                state.Sides[k] <- struct (ValueNone, ValueSome v)

                match mapping k ValueNone (ValueSome v) with
                | ValueSome o -> state.Out[k] <- o
                | ValueNone -> ()

    // =============================================================================
    // Rebuild helpers (PLAN.md Section 7.3): ofAval replaces the whole state on
    // every value change and emits the diff as the output delta.
    // =============================================================================

    /// <summary>
    /// Replace the state of a plain set node with <paramref name="next"/> and
    /// collect the diff as the output delta. Returns whether anything changed.
    /// The removals are collected first: mutating a HashSet while iterating it is
    /// undefined.
    /// </summary>
    let rebuildSetDiff (next: HashSet<'T>) (state: SetNodeState<'T, 'T> byref) : bool =
        let mutable changed = false
        let mutable e = next.GetEnumerator()

        while e.MoveNext() do
            let x = e.Current

            if state.Set.Data.Add x then
                state.Out.Adds <- bufferAppend state.Out.Adds x
                changed <- true

        let mutable e2 = state.Set.Data.GetEnumerator()

        while e2.MoveNext() do
            let x = e2.Current

            if not (next.Contains x) then
                state.Out.Rems <- bufferAppend state.Out.Rems x
                changed <- true

        for i in 0 .. state.Out.Rems.Count - 1 do
            state.Set.Data.Remove state.Out.Rems.Items[i] |> ignore

        changed

    /// <summary>
    /// Replace the state of a map node with <paramref name="next"/> and collect
    /// the diff as the output delta (equal values elided). Returns whether
    /// anything changed. The removals are collected first: mutating a Dictionary
    /// while iterating it is undefined.
    /// </summary>
    let rebuildMapDiff (next: Dictionary<'K, 'V>) (state: MapNodeState<'K, 'V, 'V> byref) : bool =
        let mutable changed = false

        for KeyValue(k, v) in next do
            let mutable old = Unchecked.defaultof<'V>

            if state.Data.TryGetValue(k, &old) && EqualityComparer<'V>.Default.Equals(old, v) then
                ()
            else
                state.Data[k] <- v
                state.Out.Sets <- bufferAppend state.Out.Sets (struct (k, v))
                changed <- true

        let mutable e = state.Data.GetEnumerator()
        let mutable removeCount = 0

        while e.MoveNext() do
            let k = e.Current.Key

            if not (next.ContainsKey k) then
                state.Out.Rems <- bufferAppend state.Out.Rems k
                removeCount <- removeCount + 1

        for i in 0 .. state.Out.Rems.Count - 1 do
            state.Data.Remove state.Out.Rems.Items[i] |> ignore

        changed
