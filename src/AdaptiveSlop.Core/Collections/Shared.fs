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

/// <summary>A set delta: added and removed elements. Used for journals and outputs.</summary>
[<Struct>]
type internal SetDelta<'T> =
    val mutable Adds: DeltaBuffer<'T>
    val mutable Rems: DeltaBuffer<'T>

    new(adds: DeltaBuffer<'T>, rems: DeltaBuffer<'T>) = { Adds = adds; Rems = rems }

    static member Create() =
        SetDelta(DeltaBuffer.Create(), DeltaBuffer.Create())

    member this.IsEmpty = this.Adds.IsEmpty && this.Rems.IsEmpty

    member this.Clear() =
        this.Adds.Count <- 0
        this.Rems.Count <- 0

/// <summary>A map delta: upserted entries and removed keys. Used for journals and outputs.</summary>
[<Struct>]
type internal MapDelta<'K, 'V> =
    val mutable Sets: DeltaBuffer<struct ('K * 'V)>
    val mutable Rems: DeltaBuffer<'K>

    new(sets: DeltaBuffer<struct ('K * 'V)>, rems: DeltaBuffer<'K>) = { Sets = sets; Rems = rems }

    static member Create() =
        MapDelta(DeltaBuffer.Create(), DeltaBuffer.Create())

    member this.IsEmpty = this.Sets.IsEmpty && this.Rems.IsEmpty

    member this.Clear() =
        this.Sets.Count <- 0
        this.Rems.Count <- 0

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
            SetDelta.Create(),
            SetDelta.Create()
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
            MapDelta.Create(),
            MapDelta.Create()
        )

// =============================================================================
// Shared operations
// =============================================================================

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
    let drainRefSet (map: 'T -> 'U voption) (state: SetNodeState<'T, 'U>) : struct (SetNodeState<'T, 'U> * bool) =
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
    let drainPlainSet (map: 'T -> 'T voption) (state: SetNodeState<'T, 'T>) : struct (SetNodeState<'T, 'T> * bool) =
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
    let drainMap
        (map: 'K -> 'V -> 'U voption)
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
    let drainSetPush (map: 'T -> 'U voption) (state: SetNodeState<'T, 'U> byref) =
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
    let drainPlainSetPush (map: 'T -> 'T voption) (state: SetNodeState<'T, 'T> byref) =
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
    let drainMapPush (map: 'K -> 'V -> 'U voption) (state: MapNodeState<'K, 'V, 'U> byref) =
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
    let loadRefSet (map: 'T -> 'U voption) (source: IAdaptiveSet<'T>) (state: SetNodeState<'T, 'U> byref) =
        for item in source.GetValue() do
            match map item with
            | ValueSome z ->
                let struct (set2, _) = refAdd state.Set z
                state.Set <- set2
            | ValueNone -> ()

    /// Initial load of a plain set node (filter).
    let loadPlainSet (map: 'T -> 'T voption) (source: IAdaptiveSet<'T>) (state: SetNodeState<'T, 'T> byref) =
        for item in source.GetValue() do
            match map item with
            | ValueSome z -> state.Set.Data.Add z |> ignore
            | ValueNone -> ()

    /// Initial load of a map node.
    let loadMap (map: 'K -> 'V -> 'U voption) (source: IAdaptiveMap<'K, 'V>) (state: MapNodeState<'K, 'V, 'U> byref) =
        for KeyValue(k, v) in source.GetValue() do
            match map k v with
            | ValueSome u -> state.Data[k] <- u
            | ValueNone -> ()
