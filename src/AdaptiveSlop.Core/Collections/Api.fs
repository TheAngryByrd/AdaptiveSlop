namespace AdaptiveSlop.Core

open System
open System.Collections.Generic
open System.Collections.Frozen

// =============================================================================
// Public API (PLAN.md Section 6.9)
//
// `force` is the materialization point: it drains and returns an immutable
// FrozenSet/FrozenDictionary that the library never touches again. `getValue`
// returns a transient view for computations. `toSet`/`toMap` materialize the
// F# Set/Map counterparts (sorted, structural equality) for interop.
// =============================================================================

/// <summary>Operations on adaptive sets.</summary>
module ASet =
    /// <summary>An adaptive set over fixed, immutable items.</summary>
    let inline ofSeq (items: seq<'T>) : IAdaptiveSet<'T> = ConstantSet(items.ToFrozenSet())

    /// <summary>Maps every element of the set.</summary>
    let inline map ([<InlineIfLambda>] f: 'T -> 'U) (set: IAdaptiveSet<'T>) : IAdaptiveSet<'U> =
        MapSetNode<'T, 'U>(set, f)

    /// <summary>Keeps the elements that satisfy the predicate.</summary>
    let inline filter ([<InlineIfLambda>] predicate: 'T -> bool) (set: IAdaptiveSet<'T>) : IAdaptiveSet<'T> =
        FilterSetNode<'T>(set, predicate)

    /// <summary>The union of two sets.</summary>
    let inline union (left: IAdaptiveSet<'T>) (right: IAdaptiveSet<'T>) : IAdaptiveSet<'T> =
        UnionSetNode<'T>(left, right)

    /// <summary>
    /// Registers a callback that receives the current view and the net delta
    /// after every batch that changes the set. The callback runs on the owner
    /// thread after the write, transaction, or pump completes. The view and
    /// the delta are transient: valid only during the callback. Disposing the
    /// returned observation stops delivery.
    /// </summary>
    /// <remarks>
    /// Parity: FDA <c>AddCallback(state, delta)</c> on collection readers.
    /// Deltas are net per element: adding and removing the same element within
    /// one batch cancels. The callback never fires for writes that do not
    /// change the set. Works on sources and derived sets alike.
    /// </remarks>
    /// <example>
    /// <code>
    /// let items = CSet.empty&lt;int&gt;
    /// use obs = ASet.observe (fun view delta -&gt;
    ///     printfn "added: %A removed: %A count: %d" delta.Added delta.Removed view.Count)
    ///     (CSet.value items)
    /// CSet.add 1 items   // prints "added: [1] removed: [] count: 1"
    /// </code>
    /// </example>
    let observe (callback: IReadOnlySet<'T> -> SetDelta<'T> -> unit) (set: IAdaptiveSet<'T>) : IObservation =
        let node = ObserveSetNode<'T>(set, callback)
        node.Attach()
        node :> IObservation

    /// <summary>
    /// Returns a transient view of the current state. Valid only until the next
    /// write on the owner thread; do not retain or mutate it. Use
    /// <see cref="force"/> to materialize a snapshot that is safe to retain.
    /// </summary>
    let inline getValue (set: IAdaptiveSet<'T>) = set.GetValue()

    /// <summary>
    /// Materializes the current state as an immutable <see cref="FrozenSet&lt;'T&gt;"/>.
    /// This is the only collection operation that allocates; the result is safe to
    /// retain and the library never touches it again. Runs the pending delta
    /// processing (drain) first.
    /// </summary>
    let inline force (set: IAdaptiveSet<'T>) : FrozenSet<'T> = set.GetValue().ToFrozenSet()

    /// <summary>Materializes the F# <c>Set</c> counterpart (sorted, structural equality).</summary>
    let inline toSet (set: IAdaptiveSet<'T>) : Set<'T> = Set.ofSeq (set.GetValue())

/// <summary>Operations on changeable sets.</summary>
module CSet =
    /// <summary>An empty changeable set.</summary>
    let inline empty<'T> = ChangeableSet<'T>(Seq.empty)

    /// <summary>A changeable set with the given items.</summary>
    let inline ofSeq (items: seq<'T>) = ChangeableSet(items)

    /// <summary>Adds an element. No-op when already present.</summary>
    let inline add (item: 'T) (set: ChangeableSet<'T>) = set.Add item

    /// <summary>Removes an element. No-op when absent.</summary>
    let inline remove (item: 'T) (set: ChangeableSet<'T>) = set.Remove item

    /// <summary>Replaces the whole set.</summary>
    let inline set (value: Set<'T>) (set: ChangeableSet<'T>) = set.Set value

    /// <summary>Views the changeable set as an adaptive set.</summary>
    let inline value (set: ChangeableSet<'T>) : IAdaptiveSet<'T> = set :> IAdaptiveSet<'T>

    /// <summary>Materializes the current state as an immutable snapshot.</summary>
    let inline force (set: ChangeableSet<'T>) : FrozenSet<'T> = ASet.force set

    /// <summary>Materializes the F# <c>Set</c> counterpart.</summary>
    let inline toSet (set: ChangeableSet<'T>) : Set<'T> = ASet.toSet set

/// <summary>Operations on adaptive maps.</summary>
module AMap =
    /// <summary>An adaptive map over fixed, immutable entries.</summary>
    let inline ofSeq (items: seq<'K * 'V>) : IAdaptiveMap<'K, 'V> =
        ConstantMap(
            items
            |> Seq.map (fun (k, v) -> KeyValuePair(k, v))
            |> FrozenDictionary.ToFrozenDictionary
        )

    /// <summary>Maps every entry of the map.</summary>
    let inline map ([<InlineIfLambda>] f: 'K -> 'V -> 'U) (mapValue: IAdaptiveMap<'K, 'V>) : IAdaptiveMap<'K, 'U> =
        MapMapNode<'K, 'V, 'U>(mapValue, f)

    /// <summary>Keeps the entries that satisfy the predicate.</summary>
    let inline filter
        ([<InlineIfLambda>] predicate: 'K -> 'V -> bool)
        (mapValue: IAdaptiveMap<'K, 'V>)
        : IAdaptiveMap<'K, 'V> =
        FilterMapNode<'K, 'V>(mapValue, predicate)

    /// <summary>
    /// Registers a callback that receives the current view and the net delta
    /// after every batch that changes the map. The callback runs on the owner
    /// thread after the write, transaction, or pump completes. The view and
    /// the delta are transient: valid only during the callback. Disposing the
    /// returned observation stops delivery.
    /// </summary>
    /// <remarks>
    /// Parity: FDA <c>AddCallback(state, delta)</c> on collection readers.
    /// Deltas are net per key: setting and removing the same key within one
    /// batch cancels; a key set twice in one batch delivers the last value.
    /// The callback never fires for writes that do not change the map. Works
    /// on sources and derived maps alike.
    /// </remarks>
    /// <example>
    /// <code>
    /// let scores = CMap.empty&lt;string, int&gt;
    /// use obs = AMap.observe (fun view delta -&gt;
    ///     printfn "set: %A removed: %A count: %d" delta.SetEntries delta.RemovedKeys view.Count)
    ///     (CMap.value scores)
    /// CMap.addOrUpdate "ada" 10 scores   // prints "set: [("ada", 10)] removed: [] count: 1"
    /// </code>
    /// </example>
    let observe
        (callback: IReadOnlyDictionary<'K, 'V> -> MapDelta<'K, 'V> -> unit)
        (mapValue: IAdaptiveMap<'K, 'V>)
        : IObservation =
        let node = ObserveMapNode<'K, 'V>(mapValue, callback)
        node.Attach()
        node :> IObservation

    /// <summary>
    /// Returns a transient view of the current state. Valid only until the next
    /// write on the owner thread; do not retain or mutate it. Use
    /// <see cref="force"/> to materialize a snapshot that is safe to retain.
    /// </summary>
    let inline getValue (mapValue: IAdaptiveMap<'K, 'V>) = mapValue.GetValue()

    /// <summary>
    /// Materializes the current state as an immutable
    /// <see cref="FrozenDictionary&lt;'K,'V&gt;"/>. This is the only collection
    /// operation that allocates; the result is safe to retain and the library
    /// never touches it again. Runs the pending delta processing (drain) first.
    /// </summary>
    let inline force (mapValue: IAdaptiveMap<'K, 'V>) : FrozenDictionary<'K, 'V> =
        mapValue.GetValue().ToFrozenDictionary()

    /// <summary>Materializes the F# <c>Map</c> counterpart (sorted, structural equality).</summary>
    let inline toMap (mapValue: IAdaptiveMap<'K, 'V>) : Map<'K, 'V> =
        mapValue.GetValue() |> Seq.map (fun (KeyValue(k, v)) -> (k, v)) |> Map.ofSeq

/// <summary>Operations on changeable maps.</summary>
module CMap =
    /// <summary>An empty changeable map.</summary>
    let inline empty<'K, 'V when 'K: equality> = ChangeableMap<'K, 'V>(Seq.empty)

    /// <summary>A changeable map with the given entries.</summary>
    let inline ofSeq (items: seq<'K * 'V>) = ChangeableMap(items)

    /// <summary>Adds or updates an entry. No-op when the value is unchanged.</summary>
    let inline addOrUpdate (key: 'K) (value: 'V) (mapValue: ChangeableMap<'K, 'V>) = mapValue.AddOrUpdate key value

    /// <summary>Removes an entry. No-op when absent.</summary>
    let inline remove (key: 'K) (mapValue: ChangeableMap<'K, 'V>) = mapValue.Remove key

    /// <summary>Replaces the whole map.</summary>
    let inline set (value: Map<'K, 'V>) (mapValue: ChangeableMap<'K, 'V>) = mapValue.Set(Map.toSeq value)

    /// <summary>Views the changeable map as an adaptive map.</summary>
    let inline value (mapValue: ChangeableMap<'K, 'V>) : IAdaptiveMap<'K, 'V> = mapValue

    /// <summary>Materializes the current state as an immutable snapshot.</summary>
    let inline force (mapValue: ChangeableMap<'K, 'V>) : FrozenDictionary<'K, 'V> = AMap.force mapValue

    /// <summary>Materializes the F# <c>Map</c> counterpart.</summary>
    let inline toMap (mapValue: ChangeableMap<'K, 'V>) : Map<'K, 'V> = AMap.toMap mapValue
