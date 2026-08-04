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
    /// Adaptively reduces the set with the given <see cref="AdaptiveReduction"/>.
    /// The state is updated incrementally from deltas: added elements apply
    /// <c>add</c>; removed elements apply <c>sub</c> (or recompute the whole
    /// state when <c>sub</c> returns <c>ValueNone</c>).
    /// </summary>
    let reduce (reduction: AdaptiveReduction<'a, 's, 'v>) (set: IAdaptiveSet<'a>) : IAdaptiveValue<'v> =
        new SetReduceNode<'a, 'a, 's, 'v>(set, id, reduction) :> IAdaptiveValue<'v>

    /// <summary>
    /// Maps every element, then reduces the mapped values with the given
    /// <see cref="AdaptiveReduction"/>. The mapping runs per delta element.
    /// </summary>
    let inline reduceBy
        (reduction: AdaptiveReduction<'b, 's, 'v>)
        ([<InlineIfLambda>] mapping: 'a -> 'b)
        (set: IAdaptiveSet<'a>)
        : IAdaptiveValue<'v> =
        new SetReduceNode<'a, 'b, 's, 'v>(set, mapping, reduction) :> IAdaptiveValue<'v>

    /// <summary>
    /// Adaptively folds the set with <c>add</c>; every removal recomputes the
    /// whole fold (the fold operation is not invertible in general). Use
    /// <see cref="foldGroup"/> when the operation has an inverse.
    /// </summary>
    let inline fold (add: 's -> 'a -> 's) (zero: 's) (set: IAdaptiveSet<'a>) : IAdaptiveValue<'s> =
        reduce (AdaptiveReduction.fold zero add) set

    /// <summary>
    /// Adaptively folds the set with an invertible <c>subtract</c>: removals
    /// update the state without a recompute.
    /// </summary>
    let inline foldGroup
        (add: 's -> 'a -> 's)
        (subtract: 's -> 'a -> 's)
        (zero: 's)
        (set: IAdaptiveSet<'a>)
        : IAdaptiveValue<'s> =
        reduce (AdaptiveReduction.group zero add subtract) set

    /// <summary>
    /// Adaptively folds the set; a removal applies <c>trySubtract</c> when it
    /// returns a value, otherwise the whole fold recomputes.
    /// </summary>
    let inline foldHalfGroup
        (add: 's -> 'a -> 's)
        (trySubtract: 's -> 'a -> 's voption)
        (zero: 's)
        (set: IAdaptiveSet<'a>)
        : IAdaptiveValue<'s> =
        reduce (AdaptiveReduction.halfGroup zero add trySubtract) set

    /// <summary>Adaptively gets the number of elements.</summary>
    let count (set: IAdaptiveSet<'T>) : IAdaptiveValue<int> =
        AdaptiveNode<int>(fun () -> set.GetValue().Count) :> IAdaptiveValue<int>

    /// <summary>Adaptively tests if the set is empty.</summary>
    let isEmpty (set: IAdaptiveSet<'T>) : IAdaptiveValue<bool> =
        AdaptiveNode<bool>(fun () -> set.GetValue().Count = 0) :> IAdaptiveValue<bool>

    /// <summary>Adaptively tests if the set contains the given element.</summary>
    let contains (value: 'T) (set: IAdaptiveSet<'T>) : IAdaptiveValue<bool> =
        AdaptiveNode<bool>(fun () -> set.GetValue().Contains value) :> IAdaptiveValue<bool>

    /// <summary>Adaptively tests if any element satisfies the predicate.</summary>
    let inline exists ([<InlineIfLambda>] predicate: 'T -> bool) (set: IAdaptiveSet<'T>) : IAdaptiveValue<bool> =
        let reduction =
            AdaptiveReduction.countPositive |> AdaptiveReduction.mapOut (fun c -> c <> 0)

        new SetReduceNode<'T, bool, int, bool>(set, predicate, reduction) :> IAdaptiveValue<bool>

    /// <summary>Adaptively tests if every element satisfies the predicate.</summary>
    let inline forall ([<InlineIfLambda>] predicate: 'T -> bool) (set: IAdaptiveSet<'T>) : IAdaptiveValue<bool> =
        let reduction =
            AdaptiveReduction.countNegative |> AdaptiveReduction.mapOut (fun c -> c = 0)

        new SetReduceNode<'T, bool, int, bool>(set, predicate, reduction) :> IAdaptiveValue<bool>

    /// <summary>Adaptively counts the elements that satisfy the predicate.</summary>
    let inline countBy ([<InlineIfLambda>] predicate: 'T -> bool) (set: IAdaptiveSet<'T>) : IAdaptiveValue<int> =
        new SetReduceNode<'T, bool, int, int>(set, predicate, AdaptiveReduction.countPositive) :> IAdaptiveValue<int>

    /// <summary>Adaptively sums the elements.</summary>
    let inline sum (set: IAdaptiveSet<'T>) : IAdaptiveValue<'T> = reduce (AdaptiveReduction.sum ()) set

    /// <summary>Adaptively sums the mapped elements.</summary>
    let inline sumBy ([<InlineIfLambda>] mapping: 'T -> 'U) (set: IAdaptiveSet<'T>) : IAdaptiveValue<'U> =
        reduceBy (AdaptiveReduction.sum ()) mapping set

    /// <summary>Adaptively gets the minimum element, or <c>ValueNone</c> when empty.</summary>
    let inline tryMin (set: IAdaptiveSet<'T>) : IAdaptiveValue<'T voption> =
        reduce (AdaptiveReduction.tryMin ()) set

    /// <summary>Adaptively gets the maximum element, or <c>ValueNone</c> when empty.</summary>
    let inline tryMax (set: IAdaptiveSet<'T>) : IAdaptiveValue<'T voption> =
        reduce (AdaptiveReduction.tryMax ()) set

    /// <summary>A constant set with a single element.</summary>
    let single (value: 'T) : IAdaptiveSet<'T> =
        new ConstantSet<'T>([ value ].ToFrozenSet())

    /// <summary>
    /// Materializes the set as an adaptive value. Every change materializes a
    /// new immutable <see cref="FrozenSet&lt;'T&gt;"/> (the retain boundary,
    /// like <see cref="force"/>); the value is safe to retain.
    /// </summary>
    let toAVal (set: IAdaptiveSet<'T>) : IAdaptiveValue<FrozenSet<'T>> =
        AdaptiveNode<FrozenSet<'T>>(fun () -> set.GetValue().ToFrozenSet()) :> IAdaptiveValue<FrozenSet<'T>>

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
    /// Adaptively reduces the map with the given <see cref="AdaptiveReduction"/>
    /// over the values. The state is updated incrementally from deltas: a Set
    /// on an existing key subtracts the old value, then adds the new one.
    /// </summary>
    let reduce (reduction: AdaptiveReduction<'a, 's, 'v>) (mapValue: IAdaptiveMap<'k, 'a>) : IAdaptiveValue<'v> =
        new MapReduceNode<'k, 'a, 'a, 's, 'v>(mapValue, (fun _ v -> v), reduction) :> IAdaptiveValue<'v>

    /// <summary>
    /// Maps every entry, then reduces the mapped values with the given
    /// <see cref="AdaptiveReduction"/>. The mapping runs per delta entry.
    /// </summary>
    let inline reduceBy
        (reduction: AdaptiveReduction<'b, 's, 'v>)
        ([<InlineIfLambda>] mapping: 'k -> 'a -> 'b)
        (mapValue: IAdaptiveMap<'k, 'a>)
        : IAdaptiveValue<'v> =
        new MapReduceNode<'k, 'a, 'b, 's, 'v>(mapValue, mapping, reduction) :> IAdaptiveValue<'v>

    /// <summary>
    /// Adaptively folds the map with <c>add</c>; every removal recomputes the
    /// whole fold. Use <see cref="foldGroup"/> when the operation has an inverse.
    /// </summary>
    let inline fold (add: 's -> 'k -> 'v -> 's) (zero: 's) (mapValue: IAdaptiveMap<'k, 'v>) : IAdaptiveValue<'s> =
        let mapping k v = struct (k, v)
        let add2 s struct (k, v) = add s k v

        new MapReduceNode<'k, 'v, struct ('k * 'v), 's, 's>(mapValue, mapping, AdaptiveReduction.fold zero add2)
        :> IAdaptiveValue<'s>

    /// <summary>
    /// Adaptively folds the map with an invertible <c>subtract</c>: removals
    /// update the state without a recompute.
    /// </summary>
    let inline foldGroup
        (add: 's -> 'k -> 'v -> 's)
        (subtract: 's -> 'k -> 'v -> 's)
        (zero: 's)
        (mapValue: IAdaptiveMap<'k, 'v>)
        : IAdaptiveValue<'s> =
        let mapping k v = struct (k, v)
        let add2 s struct (k, v) = add s k v
        let sub2 s struct (k, v) = subtract s k v

        new MapReduceNode<'k, 'v, struct ('k * 'v), 's, 's>(mapValue, mapping, AdaptiveReduction.group zero add2 sub2)
        :> IAdaptiveValue<'s>

    /// <summary>Adaptively gets the number of entries.</summary>
    let count (mapValue: IAdaptiveMap<'K, 'V>) : IAdaptiveValue<int> =
        AdaptiveNode<int>(fun () -> mapValue.GetValue().Count) :> IAdaptiveValue<int>

    /// <summary>Adaptively tests if the map is empty.</summary>
    let isEmpty (mapValue: IAdaptiveMap<'K, 'V>) : IAdaptiveValue<bool> =
        AdaptiveNode<bool>(fun () -> mapValue.GetValue().Count = 0) :> IAdaptiveValue<bool>

    /// <summary>Adaptively tests if any entry satisfies the predicate.</summary>
    let inline exists
        ([<InlineIfLambda>] predicate: 'K -> 'V -> bool)
        (mapValue: IAdaptiveMap<'K, 'V>)
        : IAdaptiveValue<bool> =
        let reduction =
            AdaptiveReduction.countPositive |> AdaptiveReduction.mapOut (fun c -> c <> 0)

        new MapReduceNode<'K, 'V, bool, int, bool>(mapValue, predicate, reduction) :> IAdaptiveValue<bool>

    /// <summary>Adaptively tests if every entry satisfies the predicate.</summary>
    let inline forall
        ([<InlineIfLambda>] predicate: 'K -> 'V -> bool)
        (mapValue: IAdaptiveMap<'K, 'V>)
        : IAdaptiveValue<bool> =
        let reduction =
            AdaptiveReduction.countNegative |> AdaptiveReduction.mapOut (fun c -> c = 0)

        new MapReduceNode<'K, 'V, bool, int, bool>(mapValue, predicate, reduction) :> IAdaptiveValue<bool>

    /// <summary>Adaptively counts the entries that satisfy the predicate.</summary>
    let inline countBy
        ([<InlineIfLambda>] predicate: 'K -> 'V -> bool)
        (mapValue: IAdaptiveMap<'K, 'V>)
        : IAdaptiveValue<int> =
        new MapReduceNode<'K, 'V, bool, int, int>(mapValue, predicate, AdaptiveReduction.countPositive)
        :> IAdaptiveValue<int>

    /// <summary>
    /// Adaptively looks up the key: the value, or <c>ValueNone</c> when the
    /// key is absent. The lookup is O(1) on read.
    /// </summary>
    let tryFind (key: 'K) (mapValue: IAdaptiveMap<'K, 'V>) : IAdaptiveValue<'V voption> =
        AdaptiveNode<'V voption>(fun () ->
            let view = mapValue.GetValue()
            let mutable v = Unchecked.defaultof<'V>

            if view.TryGetValue(key, &v) then ValueSome v else ValueNone)
        :> IAdaptiveValue<'V voption>

    /// <summary>
    /// Adaptively looks up the key. Reading the value throws
    /// <see cref="KeyNotFoundException"/> when the key is absent.
    /// </summary>
    let find (key: 'K) (mapValue: IAdaptiveMap<'K, 'V>) : IAdaptiveValue<'V> =
        AdaptiveNode<'V>(fun () ->
            let view = mapValue.GetValue()
            let mutable v = Unchecked.defaultof<'V>

            if view.TryGetValue(key, &v) then
                v
            else
                raise (KeyNotFoundException(sprintf "could not get key: %A" key)))
        :> IAdaptiveValue<'V>

    /// <summary>A constant map with a single entry.</summary>
    let single (key: 'K) (value: 'V) : IAdaptiveMap<'K, 'V> =
        ConstantMap([ KeyValuePair(key, value) ] |> FrozenDictionary.ToFrozenDictionary)

    /// <summary>
    /// Materializes the map as an adaptive value. Every change materializes a
    /// new immutable <see cref="FrozenDictionary&lt;'K,'V&gt;"/> (the retain
    /// boundary, like <see cref="force"/>); the value is safe to retain.
    /// </summary>
    let toAVal (mapValue: IAdaptiveMap<'K, 'V>) : IAdaptiveValue<FrozenDictionary<'K, 'V>> =
        AdaptiveNode<FrozenDictionary<'K, 'V>>(fun () -> mapValue.GetValue().ToFrozenDictionary())
        :> IAdaptiveValue<FrozenDictionary<'K, 'V>>

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
