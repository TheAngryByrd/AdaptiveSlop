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
    let inline ofSeq (items: seq<'T>) : IAdaptiveSet<'T> =
        new ConstantSet<'T>(fun () -> items.ToFrozenSet())

    /// <summary>An adaptive set over a fixed array.</summary>
    let inline ofArray (items: 'T[]) : IAdaptiveSet<'T> =
        new ConstantSet<'T>(fun () -> items.ToFrozenSet())

    /// <summary>An adaptive set over a fixed list.</summary>
    let inline ofList (items: 'T list) : IAdaptiveSet<'T> =
        new ConstantSet<'T>(fun () -> items.ToFrozenSet())

    /// <summary>An adaptive set over a fixed HashSet.</summary>
    let inline ofHashSet (items: HashSet<'T>) : IAdaptiveSet<'T> =
        new ConstantSet<'T>(fun () -> items.ToFrozenSet())

    /// <summary>
    /// An adaptive set whose content is fixed but computed lazily, once, at
    /// first read. Enables self-referential definitions (FDA parity: the
    /// create function runs at most once).
    /// </summary>
    let inline constant (create: unit -> HashSet<'T>) : IAdaptiveSet<'T> =
        new ConstantSet<'T>(fun () -> create().ToFrozenSet())

    /// <summary>Alias of <see cref="constant"/> (FDA parity: delay is constant).</summary>
    let inline delay (create: unit -> HashSet<'T>) : IAdaptiveSet<'T> = constant create

    /// <summary>Maps every element of the set.</summary>
    let inline map ([<InlineIfLambda>] f: 'T -> 'U) (set: IAdaptiveSet<'T>) : IAdaptiveSet<'U> =
        new MapSetNode<'T, 'U>(set, fun x -> ValueSome(f x))

    /// <summary>Maps every element, keeping only the ones the mapping returns a value for.</summary>
    let inline choose ([<InlineIfLambda>] f: 'T -> 'U voption) (set: IAdaptiveSet<'T>) : IAdaptiveSet<'U> =
        new MapSetNode<'T, 'U>(set, f)

    /// <summary>Keeps the elements that satisfy the predicate.</summary>
    let inline filter ([<InlineIfLambda>] predicate: 'T -> bool) (set: IAdaptiveSet<'T>) : IAdaptiveSet<'T> =
        new FilterSetNode<'T>(set, predicate)

    /// <summary>The union of two sets.</summary>
    let inline union (left: IAdaptiveSet<'T>) (right: IAdaptiveSet<'T>) : IAdaptiveSet<'T> =
        new UnionSetNode<'T>(left, right)

    /// <summary>
    /// The union of all given sets. Deviation from FDA: FDA's
    /// <c>unionMany</c> takes an adaptive set of sets (the dynamic form lands
    /// with <c>bind</c>/<c>collect</c>, PLAN.md 7.4); this overload takes a
    /// static sequence and folds <see cref="union"/>.
    /// </summary>
    let unionMany (sets: seq<IAdaptiveSet<'T>>) : IAdaptiveSet<'T> =
        let mutable acc = Unchecked.defaultof<IAdaptiveSet<'T>>
        let mutable first = true

        for s in sets do
            if first then
                acc <- s
                first <- false
            else
                acc <- new UnionSetNode<'T>(acc, s)

        if first then
            new ConstantSet<'T>(fun () -> FrozenSet<'T>.Empty)
        else
            acc

    /// <summary>
    /// Adaptively maps over the given set and unions all resulting sets (FDA
    /// <c>ASet.collect</c> parity; PLAN.md Section 7.4). The output is the
    /// refcounted union: an element contributed by several inner sets
    /// disappears only when the last contributor drops it. This is also the
    /// dynamic <c>unionMany</c>: <c>ASet.collect id</c> over
    /// <c>IAdaptiveSet&lt;IAdaptiveSet&lt;'T&gt;&gt;</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// // group numbers by parity: the output follows both the outer set and
    /// // the inner sets.
    /// let buckets = CSet.empty&lt;int&gt;
    /// let odds = CSet.empty&lt;int&gt;
    /// let evens = CSet.empty&lt;int&gt;
    /// let all = ASet.collect (fun b -&gt; if b % 2 = 0 then evens else odds) (CSet.value buckets)
    /// CSet.add 1 buckets
    /// CSet.add 2 buckets
    /// CSet.add 3 odds
    /// // all is now {3}
    /// </code>
    /// </example>
    let inline collect ([<InlineIfLambda>] mapping: 'T -> IAdaptiveSet<'U>) (set: IAdaptiveSet<'T>) : IAdaptiveSet<'U> =
        new CollectSetNode<'T, 'U>(set, mapping)

    /// <summary>The elements of the left set that are not in the right set.</summary>
    let inline difference (left: IAdaptiveSet<'T>) (right: IAdaptiveSet<'T>) : IAdaptiveSet<'T> =
        new TwoSourceSetNode<'T>(TwoSetOp.Difference, left, right)

    /// <summary>The elements present in both sets.</summary>
    let inline intersect (left: IAdaptiveSet<'T>) (right: IAdaptiveSet<'T>) : IAdaptiveSet<'T> =
        new TwoSourceSetNode<'T>(TwoSetOp.Intersect, left, right)

    /// <summary>The symmetric difference: elements present in exactly one set.</summary>
    let inline xor (left: IAdaptiveSet<'T>) (right: IAdaptiveSet<'T>) : IAdaptiveSet<'T> =
        new TwoSourceSetNode<'T>(TwoSetOp.Xor, left, right)

    /// <summary>
    /// An adaptive set over an adaptive value of a sequence. Every change of
    /// the value replaces the whole state and emits the diff as the delta
    /// (FDA <c>ASet.ofAVal</c> parity; the value carries no deltas).
    /// </summary>
    let inline ofAVal<'T, 'S when 'T: equality and 'S :> seq<'T>> (value: IAdaptiveValue<'S>) : IAdaptiveSet<'T> =
        new OfAvalSetNode<'T, 'S>(value)

    /// <summary>
    /// Adaptively maps over the given value and returns the resulting set (FDA
    /// <c>ASet.bind</c> parity; PLAN.md Section 7.4). When the value changes,
    /// the whole inner set is swapped: the old content is removed, the inner
    /// sink is unregistered eagerly, and <c>mapping</c> selects the new inner
    /// set. The inner set's own changes propagate while it is bound.
    /// </summary>
    /// <example>
    /// <code>
    /// // a set that follows the currently selected bucket
    /// let selected = CVal.create 0
    /// let buckets = [| CSet.empty&lt;int&gt;; CSet.empty&lt;int&gt; |]
    /// let visible = ASet.bind (fun i -&gt; buckets[i]) (CVal.value selected)
    /// CSet.add 7 (buckets[0])
    /// CVal.setValue 1 selected
    /// // visible is now empty (bucket 1), and bucket 0's later changes do not leak
    /// </code>
    /// </example>
    let inline bind
        ([<InlineIfLambda>] mapping: 'T -> IAdaptiveSet<'U>)
        (value: IAdaptiveValue<'T>)
        : IAdaptiveSet<'U> =
        new BindSetNode<'T, 'U>(value, mapping)

    /// <summary>
    /// An adaptive set over an external reader function. The reader is called
    /// on every read (poll); the node diffs the result against its state and
    /// emits the diff as the delta. Pull-based: nothing marks this node, so
    /// consumers must re-read it (FDA <c>ASet.ofReader</c> is pull-based too).
    /// </summary>
    let inline ofReader (reader: unit -> HashSet<'T>) : IAdaptiveSet<'T> = new ReaderSetNode<'T>(reader)

    /// <summary>
    /// An adaptive set driven by a compute function (FDA <c>ASet.custom</c>
    /// parity, pull model). The compute receives the current view and a delta
    /// builder; it appends the operations that describe the change since the
    /// previous call (for example, consuming its own event queue).
    /// </summary>
    let inline custom (compute: IReadOnlySet<'T> -> SetDeltaBuilder<'T> -> unit) : IAdaptiveSet<'T> =
        new CustomSetNode<'T>(compute)

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
        let node = new ObserveSetNode<'T>(set, callback)
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
        new ConstantSet<'T>(fun () -> [ value ].ToFrozenSet())

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
    let inline empty<'T> = new ChangeableSet<'T>(Seq.empty)

    /// <summary>A changeable set with the given items.</summary>
    let inline ofSeq (items: seq<'T>) = new ChangeableSet<'T>(items)

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
        new ConstantMap<'K, 'V>(fun () ->
            items
            |> Seq.map (fun (k, v) -> KeyValuePair(k, v))
            |> FrozenDictionary.ToFrozenDictionary)

    /// <summary>An adaptive map over a fixed array of entries.</summary>
    let inline ofArray (items: ('K * 'V)[]) : IAdaptiveMap<'K, 'V> =
        new ConstantMap<'K, 'V>(fun () ->
            items
            |> Seq.map (fun (k, v) -> KeyValuePair(k, v))
            |> FrozenDictionary.ToFrozenDictionary)

    /// <summary>An adaptive map over a fixed list of entries.</summary>
    let inline ofList (items: ('K * 'V) list) : IAdaptiveMap<'K, 'V> =
        new ConstantMap<'K, 'V>(fun () ->
            items
            |> Seq.map (fun (k, v) -> KeyValuePair(k, v))
            |> FrozenDictionary.ToFrozenDictionary)

    /// <summary>An adaptive map over a fixed F# <c>Map</c>.</summary>
    let inline ofMap (items: Map<'K, 'V>) : IAdaptiveMap<'K, 'V> =
        new ConstantMap<'K, 'V>(fun () ->
            items
            |> Seq.map (fun (KeyValue(k, v)) -> KeyValuePair(k, v))
            |> FrozenDictionary.ToFrozenDictionary)

    /// <summary>Maps every entry of the map.</summary>
    let inline map ([<InlineIfLambda>] f: 'K -> 'V -> 'U) (mapValue: IAdaptiveMap<'K, 'V>) : IAdaptiveMap<'K, 'U> =
        new MapMapNode<'K, 'V, 'U>(mapValue, fun k v -> ValueSome(f k v))

    /// <summary>Maps every entry, keeping only the ones the mapping returns a value for.</summary>
    let inline choose
        ([<InlineIfLambda>] f: 'K -> 'V -> 'U voption)
        (mapValue: IAdaptiveMap<'K, 'V>)
        : IAdaptiveMap<'K, 'U> =
        new MapMapNode<'K, 'V, 'U>(mapValue, f)

    /// <summary>Unions both maps, resolving colliding keys with the given function.</summary>
    let inline unionWith
        ([<InlineIfLambda>] resolve: 'K -> 'V -> 'V -> 'V)
        (left: IAdaptiveMap<'K, 'V>)
        (right: IAdaptiveMap<'K, 'V>)
        : IAdaptiveMap<'K, 'V> =
        new Choose2MapNode<'K, 'V, 'V, 'V>(
            left,
            right,
            fun k lv rv ->
                match struct (lv, rv) with
                | ValueSome l, ValueSome r -> ValueSome(resolve k l r)
                | ValueSome l, ValueNone -> ValueSome l
                | ValueNone, ValueSome r -> ValueSome r
                | ValueNone, ValueNone -> ValueNone
        )

    /// <summary>
    /// Unions both maps, preferring the RIGHT value when keys collide
    /// (FDA parity: <c>union a b = unionWith (fun _ _ r -> r) a b</c>).
    /// </summary>
    let inline union (left: IAdaptiveMap<'K, 'V>) (right: IAdaptiveMap<'K, 'V>) : IAdaptiveMap<'K, 'V> =
        unionWith (fun _ _ r -> r) left right

    /// <summary>
    /// The keys present in both maps, with the values paired. Struct pair:
    /// the voption-first convention collapses FDA's <c>intersect</c> (tuple)
    /// and <c>intersectV</c> (struct) into the struct form.
    /// </summary>
    let inline intersect
        (left: IAdaptiveMap<'K, 'V1>)
        (right: IAdaptiveMap<'K, 'V2>)
        : IAdaptiveMap<'K, struct ('V1 * 'V2)> =
        new Choose2MapNode<'K, 'V1, 'V2, struct ('V1 * 'V2)>(
            left,
            right,
            fun k lv rv ->
                match struct (lv, rv) with
                | ValueSome l, ValueSome r -> ValueSome(struct (l, r))
                | _ -> ValueNone
        )

    /// <summary>Intersects both maps, combining the paired values.</summary>
    let inline intersectWith
        ([<InlineIfLambda>] combine: 'K -> 'V1 -> 'V2 -> 'V3)
        (left: IAdaptiveMap<'K, 'V1>)
        (right: IAdaptiveMap<'K, 'V2>)
        : IAdaptiveMap<'K, 'V3> =
        new Choose2MapNode<'K, 'V1, 'V2, 'V3>(
            left,
            right,
            fun k lv rv ->
                match struct (lv, rv) with
                | ValueSome l, ValueSome r -> ValueSome(combine k l r)
                | _ -> ValueNone
        )

    /// <summary>
    /// Merges both maps with a mapping that receives the key and both side
    /// values (voptions) and returns the output value (voption). The mapping
    /// is called only when at least one side has a value (FDA parity); a key
    /// with no value on either side is removed without a call. Voption-first:
    /// this is FDA's <c>choose2V</c> (the option variant is not provided).
    /// </summary>
    let inline choose2
        ([<InlineIfLambda>] mapping: 'K -> 'V1 voption -> 'V2 voption -> 'V3 voption)
        (left: IAdaptiveMap<'K, 'V1>)
        (right: IAdaptiveMap<'K, 'V2>)
        : IAdaptiveMap<'K, 'V3> =
        new Choose2MapNode<'K, 'V1, 'V2, 'V3>(left, right, mapping)

    /// <summary>
    /// A map from a set of entries, keeping ALL values of a key in a HashSet
    /// (FDA <c>ofASet</c> parity). A changed value set emits a fresh HashSet
    /// in the delta (this node allocates by design).
    /// </summary>
    let inline ofASet (elements: IAdaptiveSet<'K * 'V>) : IAdaptiveMap<'K, HashSet<'V>> =
        new SetToMapKeepAllNode<'K, 'V, 'K * 'V>(elements, id)

    /// <summary>
    /// A map from a set of entries; duplicate keys keep the LAST value
    /// (FDA <c>ofASetIgnoreDuplicates</c> parity: the constant path keeps the
    /// last value, the delta path is arbitrary).
    /// </summary>
    let inline ofASetIgnoreDuplicates (elements: IAdaptiveSet<'K * 'V>) : IAdaptiveMap<'K, 'V> =
        new SetToMapNode<'K, 'V, 'K * 'V>(elements, (fun x -> x), true)

    /// <summary>
    /// A map from a set, deriving the key from every value and keeping ALL
    /// values of a key in a HashSet (FDA <c>ofASetMapped</c> parity).
    /// </summary>
    let inline ofASetMapped (getKey: 'V -> 'K) (elements: IAdaptiveSet<'V>) : IAdaptiveMap<'K, HashSet<'V>> =
        new SetToMapKeepAllNode<'K, 'V, 'V>(elements, fun v -> (getKey v, v))

    /// <summary>
    /// A map from a set, deriving the key from every value; duplicate keys
    /// keep the LAST value.
    /// </summary>
    let inline ofASetMappedIgnoreDuplicates (getKey: 'V -> 'K) (elements: IAdaptiveSet<'V>) : IAdaptiveMap<'K, 'V> =
        new SetToMapNode<'K, 'V, 'V>(elements, (fun v -> (getKey v, v)), true)

    /// <summary>Maps the keys of a set to entries (FDA <c>mapSet</c> parity: the mapping runs per key).</summary>
    let inline mapSet (mapping: 'K -> 'V) (set: IAdaptiveSet<'K>) : IAdaptiveMap<'K, 'V> =
        new SetToMapNode<'K, 'V, 'K>(set, (fun k -> (k, mapping k)), false)

    /// <summary>An adaptive set of the map's keys (FDA <c>toASet</c> parity).</summary>
    let inline toASet (mapValue: IAdaptiveMap<'K, 'V>) : IAdaptiveSet<'K> =
        new MapToSetNode<'K, 'V, 'K>(mapValue, fun k _ -> k)

    /// <summary>An adaptive set of the map's distinct values (FDA <c>toASetValues</c> parity).</summary>
    let inline toASetValues (mapValue: IAdaptiveMap<'K, 'V>) : IAdaptiveSet<'V> =
        new MapToSetNode<'K, 'V, 'V>(mapValue, fun _ v -> v)

    /// <summary>
    /// An adaptive map over an adaptive value of a sequence of entries. Every
    /// change of the value replaces the whole state and emits the diff as the
    /// delta (FDA <c>AMap.ofAVal</c> parity; the value carries no deltas).
    /// </summary>
    let inline ofAVal<'K, 'V, 'S when 'K: equality and 'S :> seq<'K * 'V>>
        (value: IAdaptiveValue<'S>)
        : IAdaptiveMap<'K, 'V> =
        new OfAvalMapNode<'K, 'V, 'S>(value)

    /// <summary>
    /// Adaptively maps over the given value and returns the resulting map (FDA
    /// <c>AMap.bind</c> parity; PLAN.md Section 7.4). When the value changes,
    /// the whole inner map is swapped: the old content is removed, the inner
    /// sink is unregistered eagerly, and <c>mapping</c> selects the new inner
    /// map. The inner map's own changes propagate while it is bound.
    /// </summary>
    /// <example>
    /// <code>
    /// // a map that follows the currently selected table
    /// let selected = CVal.create 0
    /// let tables = [| CMap.empty&lt;string, int&gt;; CMap.empty&lt;string, int&gt; |]
    /// let visible = AMap.bind (fun i -&gt; tables[i]) (CVal.value selected)
    /// CMap.addOrUpdate "health" 10 (tables[0])
    /// CVal.setValue 1 selected
    /// // visible is now empty, and table 0's later changes do not leak
    /// </code>
    /// </example>
    let inline bind
        ([<InlineIfLambda>] mapping: 'T -> IAdaptiveMap<'K, 'V>)
        (value: IAdaptiveValue<'T>)
        : IAdaptiveMap<'K, 'V> =
        new BindMapNode<'K, 'V, 'T>(value, mapping)

    /// <summary>
    /// An adaptive map driven by a compute function (FDA <c>AMap.custom</c>
    /// parity, pull model). The compute receives the current view and a delta
    /// builder; it appends the operations that describe the change since the
    /// previous call (for example, consuming its own event queue).
    /// </summary>
    let inline custom (compute: IReadOnlyDictionary<'K, 'V> -> MapDeltaBuilder<'K, 'V> -> unit) : IAdaptiveMap<'K, 'V> =
        new CustomMapNode<'K, 'V>(compute)

    /// <summary>Keeps the entries that satisfy the predicate.</summary>
    let inline filter
        ([<InlineIfLambda>] predicate: 'K -> 'V -> bool)
        (mapValue: IAdaptiveMap<'K, 'V>)
        : IAdaptiveMap<'K, 'V> =
        new FilterMapNode<'K, 'V>(mapValue, predicate)

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
        let node = new ObserveMapNode<'K, 'V>(mapValue, callback)
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
        new ConstantMap<'K, 'V>(fun () -> [ KeyValuePair(key, value) ] |> FrozenDictionary.ToFrozenDictionary)

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
    let inline empty<'K, 'V when 'K: equality> = new ChangeableMap<'K, 'V>(Seq.empty)

    /// <summary>A changeable map with the given entries.</summary>
    let inline ofSeq (items: seq<'K * 'V>) = new ChangeableMap<'K, 'V>(items)

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
