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
    /// <summary>An empty adaptive set (FDA <c>ASet.empty</c> parity).</summary>
    let empty<'T> : aset<'T> = new ConstantSet<'T>(fun () -> FrozenSet<'T>.Empty)

    /// <summary>An adaptive set over fixed, immutable items.</summary>
    let inline ofSeq (items: seq<'T>) : aset<'T> =
        new ConstantSet<'T>(fun () -> items.ToFrozenSet())

    /// <summary>An adaptive set over a fixed array.</summary>
    let inline ofArray (items: 'T[]) : aset<'T> =
        new ConstantSet<'T>(fun () -> items.ToFrozenSet())

    /// <summary>An adaptive set over a fixed list.</summary>
    let inline ofList (items: 'T list) : aset<'T> =
        new ConstantSet<'T>(fun () -> items.ToFrozenSet())

    /// <summary>An adaptive set over a fixed HashSet.</summary>
    let inline ofHashSet (items: HashSet<'T>) : aset<'T> =
        new ConstantSet<'T>(fun () -> items.ToFrozenSet())

    /// <summary>
    /// An adaptive set whose content is fixed but computed lazily, once, at
    /// first read. Enables self-referential definitions (FDA parity: the
    /// create function runs at most once).
    /// </summary>
    let inline constant ([<InlineIfLambda>] create: unit -> HashSet<'T>) : aset<'T> =
        new ConstantSet<'T>(fun () -> create().ToFrozenSet())

    /// <summary>Alias of <see cref="constant"/> (FDA parity: delay is constant).</summary>
    let inline delay ([<InlineIfLambda>] create: unit -> HashSet<'T>) : aset<'T> = constant create

    /// <summary>Maps every element of the set.</summary>
    let inline map ([<InlineIfLambda>] f: 'T -> 'U) (set: aset<'T>) : aset<'U> =
        new MapSetNode<'T, 'U>(set, fun x -> ValueSome(f x))

    /// <summary>Maps every element, keeping only the ones the mapping returns a value for.</summary>
    let inline choose ([<InlineIfLambda>] f: 'T -> 'U option) (set: aset<'T>) : aset<'U> =
        new MapSetNode<'T, 'U>(set, f >> Option.toValueOption)

    /// <summary>Maps every element, keeping only the ones the mapping returns a value for.</summary>
    let inline chooseV ([<InlineIfLambda>] f: 'T -> 'U voption) (set: aset<'T>) : aset<'U> =
        new MapSetNode<'T, 'U>(set, f)

    /// <summary>Keeps the elements that satisfy the predicate.</summary>
    let inline filter ([<InlineIfLambda>] predicate: 'T -> bool) (set: aset<'T>) : aset<'T> =
        new FilterSetNode<'T>(set, predicate)

    /// <summary>
    /// Adaptively maps every element of the set to an adaptive value (FDA
    /// <c>ASet.mapA</c> parity). The output follows the aval returned for
    /// each element; writes to the avals deliver targeted deltas.
    /// </summary>
    let inline mapA ([<InlineIfLambda>] mapping: 'T -> aval<'U>) (set: aset<'T>) : aset<'U> =
        new ElementSetNode<'T, 'U>(set, fun x -> AVal.map ValueSome (mapping x))

    /// <summary>
    /// Adaptively maps every element of the set to an adaptive value, keeping
    /// only the elements whose aval holds <c>Some</c> (FDA
    /// <c>ASet.chooseA</c> parity).
    /// </summary>
    let inline chooseA ([<InlineIfLambda>] mapping: 'T -> aval<'U option>) (set: aset<'T>) : aset<'U> =
        new ElementSetNode<'T, 'U>(set, fun x -> AVal.map Option.toValueOption (mapping x))

    /// <summary>
    /// Adaptively keeps the elements whose predicate aval holds <c>true</c>
    /// (FDA <c>ASet.filterA</c> parity).
    /// </summary>
    let inline filterA ([<InlineIfLambda>] predicate: 'T -> aval<bool>) (set: aset<'T>) : aset<'T> =
        new ElementSetNode<'T, 'T>(set, fun x -> AVal.map (fun b -> if b then ValueSome x else ValueNone) (predicate x))

    /// <summary>The union of two sets.</summary>
    let inline union (left: aset<'T>) (right: aset<'T>) : aset<'T> = new UnionSetNode<'T>(left, right)

    /// <summary>
    /// The union of all given sets. Deviation from FDA: FDA's
    /// <c>unionMany</c> takes an adaptive set of sets (the dynamic form lands
    /// with <c>bind</c>/<c>collect</c>, PLAN.md 7.4); this overload takes a
    /// static sequence and folds <see cref="union"/>.
    /// </summary>
    let unionMany (sets: seq<aset<'T>>) : aset<'T> =
        let mutable acc = Unchecked.defaultof<aset<'T>>
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
    let inline collect ([<InlineIfLambda>] mapping: 'T -> aset<'U>) (set: aset<'T>) : aset<'U> =
        new CollectSetNode<'T, 'U>(set, mapping)

    /// <summary>The elements of the left set that are not in the right set.</summary>
    let inline difference (left: aset<'T>) (right: aset<'T>) : aset<'T> =
        new TwoSourceSetNode<'T>(TwoSetOp.Difference, left, right)

    /// <summary>The elements present in both sets.</summary>
    let inline intersect (left: aset<'T>) (right: aset<'T>) : aset<'T> =
        new TwoSourceSetNode<'T>(TwoSetOp.Intersect, left, right)

    /// <summary>The symmetric difference: elements present in exactly one set.</summary>
    let inline xor (left: aset<'T>) (right: aset<'T>) : aset<'T> =
        new TwoSourceSetNode<'T>(TwoSetOp.Xor, left, right)

    /// <summary>
    /// An adaptive set over an adaptive value of a sequence. Every change of
    /// the value replaces the whole state and emits the diff as the delta
    /// (FDA <c>ASet.ofAVal</c> parity; the value carries no deltas).
    /// </summary>
    let inline ofAVal<'T, 'S when 'T: equality and 'S :> seq<'T>> (value: aval<'S>) : aset<'T> =
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
    let inline bind ([<InlineIfLambda>] mapping: 'T -> aset<'U>) (value: aval<'T>) : aset<'U> =
        new BindSetNode<'T, 'U>(value, mapping)

    /// <summary>
    /// An adaptive set over an external reader function. The reader is called
    /// on every read (poll); the node diffs the result against its state and
    /// emits the diff as the delta. Pull-based: nothing marks this node, so
    /// consumers must re-read it (FDA <c>ASet.ofReader</c> is pull-based too).
    /// </summary>
    let inline ofReader ([<InlineIfLambda>] reader: unit -> HashSet<'T>) : aset<'T> = new ReaderSetNode<'T>(reader)

    /// <summary>
    /// An adaptive set driven by a compute function (FDA <c>ASet.custom</c>
    /// parity, pull model). The compute receives the current view and a delta
    /// builder; it appends the operations that describe the change since the
    /// previous call (for example, consuming its own event queue).
    /// </summary>
    let inline custom ([<InlineIfLambda>] compute: IReadOnlySet<'T> -> SetDeltaBuilder<'T> -> unit) : aset<'T> =
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
    let inline observe
        ([<InlineIfLambda>] callback: IReadOnlySet<'T> -> SetDelta<'T> -> unit)
        (set: aset<'T>)
        : IObservation =
        let node = new ObserveSetNode<'T>(set, callback)
        node.Attach()
        node

    /// <summary>
    /// Adaptively reduces the set with the given <see cref="AdaptiveReduction"/>.
    /// The state is updated incrementally from deltas: added elements apply
    /// <c>add</c>; removed elements apply <c>sub</c> (or recompute the whole
    /// state when <c>sub</c> returns <c>ValueNone</c>).
    /// </summary>
    let inline reduce (reduction: AdaptiveReduction<'a, 's, 'v>) (set: aset<'a>) : aval<'v> =
        new SetReduceNode<'a, 'a, 's, 'v>(set, id, reduction)

    /// <summary>
    /// Maps every element, then reduces the mapped values with the given
    /// <see cref="AdaptiveReduction"/>. The mapping runs per delta element.
    /// </summary>
    let inline reduceBy
        (reduction: AdaptiveReduction<'b, 's, 'v>)
        ([<InlineIfLambda>] mapping: 'a -> 'b)
        (set: aset<'a>)
        : aval<'v> =
        new SetReduceNode<'a, 'b, 's, 'v>(set, mapping, reduction)

    /// <summary>
    /// Adaptively folds the set with <c>add</c>; every removal recomputes the
    /// whole fold (the fold operation is not invertible in general). Use
    /// <see cref="foldGroup"/> when the operation has an inverse.
    /// </summary>
    let inline fold ([<InlineIfLambda>] add: 's -> 'a -> 's) (zero: 's) (set: aset<'a>) : aval<'s> =
        reduce (AdaptiveReduction.fold zero add) set

    /// <summary>
    /// Adaptively folds the set with an invertible <c>subtract</c>: removals
    /// update the state without a recompute.
    /// </summary>
    let inline foldGroup
        ([<InlineIfLambda>] add: 's -> 'a -> 's)
        ([<InlineIfLambda>] subtract: 's -> 'a -> 's)
        (zero: 's)
        (set: aset<'a>)
        : aval<'s> =
        reduce (AdaptiveReduction.group zero add subtract) set

    /// <summary>
    /// Adaptively folds the set; a removal applies <c>trySubtract</c> when it
    /// returns a value, otherwise the whole fold recomputes.
    /// </summary>
    let inline foldHalfGroup
        ([<InlineIfLambda>] add: 's -> 'a -> 's)
        ([<InlineIfLambda>] trySubtract: 's -> 'a -> 's voption)
        (zero: 's)
        (set: aset<'a>)
        : aval<'s> =
        reduce (AdaptiveReduction.halfGroup zero add trySubtract) set

    /// <summary>Adaptively gets the number of elements.</summary>
    let inline count (set: aset<'T>) : aval<int> =
        AdaptiveNode<int>(fun () -> set.GetValue().Count)

    /// <summary>Adaptively tests if the set is empty.</summary>
    let inline isEmpty (set: aset<'T>) : aval<bool> =
        AdaptiveNode<bool>(fun () -> set.GetValue().Count = 0)

    /// <summary>Adaptively tests if the set contains the given element.</summary>
    let inline contains (value: 'T) (set: aset<'T>) : aval<bool> =
        AdaptiveNode<bool>(fun () -> set.GetValue().Contains value)

    /// <summary>Adaptively tests if any element satisfies the predicate.</summary>
    let inline exists ([<InlineIfLambda>] predicate: 'T -> bool) (set: aset<'T>) : aval<bool> =
        let reduction =
            AdaptiveReduction.countPositive |> AdaptiveReduction.mapOut (fun c -> c <> 0)

        new SetReduceNode<'T, bool, int, bool>(set, predicate, reduction)

    /// <summary>Adaptively tests if every element satisfies the predicate.</summary>
    let inline forall ([<InlineIfLambda>] predicate: 'T -> bool) (set: aset<'T>) : aval<bool> =
        new SetReduceNode<'T, bool, int, bool>(
            set,
            predicate,
            AdaptiveReduction.countNegative |> AdaptiveReduction.mapOut (fun c -> c = 0)
        )

    /// <summary>Adaptively counts the elements that satisfy the predicate.</summary>
    let inline countBy ([<InlineIfLambda>] predicate: 'T -> bool) (set: aset<'T>) : aval<int> =
        new SetReduceNode<'T, bool, int, int>(set, predicate, AdaptiveReduction.countPositive)

    /// <summary>Adaptively sums the elements.</summary>
    let inline sum (set: aset<'T>) : aval<'T> = reduce (AdaptiveReduction.sum ()) set

    /// <summary>Adaptively sums the mapped elements.</summary>
    let inline sumBy ([<InlineIfLambda>] mapping: 'T -> 'U) (set: aset<'T>) : aval<'U> =
        reduceBy (AdaptiveReduction.sum ()) mapping set

    /// <summary>Adaptively gets the minimum element, or <c>ValueNone</c> when empty.</summary>
    let inline tryMin (set: aset<'T>) : aval<'T voption> =
        reduce (AdaptiveReduction.tryMin ()) set

    /// <summary>Adaptively gets the maximum element, or <c>ValueNone</c> when empty.</summary>
    let inline tryMax (set: aset<'T>) : aval<'T voption> =
        reduce (AdaptiveReduction.tryMax ()) set

    /// <summary>A constant set with a single element.</summary>
    let inline single (value: 'T) : aset<'T> =
        new ConstantSet<'T>(fun () -> [ value ].ToFrozenSet())

    /// <summary>
    /// Materializes the set as an adaptive value. Every change materializes a
    /// new immutable <see cref="FrozenSet&lt;'T&gt;"/> (the retain boundary,
    /// like <see cref="force"/>); the value is safe to retain.
    /// </summary>
    let inline toAVal (set: aset<'T>) : aval<FrozenSet<'T>> =
        AdaptiveNode<FrozenSet<'T>>(fun () -> set.GetValue().ToFrozenSet())

    /// <summary>
    /// Returns a transient view of the current state. Valid only until the next
    /// write on the owner thread; do not retain or mutate it. Use
    /// <see cref="force"/> to materialize a snapshot that is safe to retain.
    /// </summary>
    let inline getValue (set: aset<'T>) = set.GetValue()

    /// <summary>
    /// Materializes the current state as an immutable <see cref="FrozenSet&lt;'T&gt;"/>.
    /// This is the only collection operation that allocates; the result is safe to
    /// retain and the library never touches it again. Runs the pending delta
    /// processing (drain) first.
    /// </summary>
    let inline force (set: aset<'T>) : FrozenSet<'T> = set.GetValue().ToFrozenSet()

    /// <summary>Materializes the F# <c>Set</c> counterpart (sorted, structural equality).</summary>
    let inline toSet (set: aset<'T>) : Set<'T> = Set.ofSeq (set.GetValue())

/// <summary>Operations on changeable sets.</summary>
module CSet =
    /// <summary>An empty changeable set.</summary>
    let inline empty<'T> = new cset<'T> (Seq.empty)

    /// <summary>A changeable set with the given items.</summary>
    let inline ofSeq (items: seq<'T>) = new cset<'T> (items)

    /// <summary>Adds an element. No-op when already present.</summary>
    let inline add (item: 'T) (set: cset<'T>) = set.Add item

    /// <summary>Removes an element. No-op when absent.</summary>
    let inline remove (item: 'T) (set: cset<'T>) = set.Remove item

    /// <summary>Replaces the whole set.</summary>
    let inline set (value: Set<'T>) (set: cset<'T>) = set.Set value

    /// <summary>Views the changeable set as an adaptive set.</summary>
    let inline value (set: cset<'T>) : aset<'T> = set

    /// <summary>Materializes the current state as an immutable snapshot.</summary>
    let inline force (set: cset<'T>) : FrozenSet<'T> = ASet.force set

    /// <summary>Materializes the F# <c>Set</c> counterpart.</summary>
    let inline toSet (set: cset<'T>) : Set<'T> = ASet.toSet set

/// <summary>Operations on adaptive maps.</summary>
module AMap =
    /// <summary>An empty adaptive map (FDA <c>AMap.empty</c> parity).</summary>
    let empty<'K, 'V when 'K: equality> : amap<'K, 'V> =
        new ConstantMap<'K, 'V>(fun () -> FrozenDictionary<'K, 'V>.Empty)

    /// <summary>
    /// An adaptive map whose content is fixed but computed lazily, once, at
    /// first read. Enables self-referential definitions (FDA parity: the
    /// create function runs at most once; deviation: FDA's create returns a
    /// <c>HashMap</c>, ours returns a <c>Dictionary</c>).
    /// </summary>
    let inline constant ([<InlineIfLambda>] create: unit -> Dictionary<'K, 'V>) : amap<'K, 'V> =
        new ConstantMap<'K, 'V>(fun () -> create().ToFrozenDictionary())

    /// <summary>Alias of <see cref="constant"/> (symmetry with <c>ASet.delay</c>; FDA has no <c>AMap.delay</c>).</summary>
    let inline delay ([<InlineIfLambda>] create: unit -> Dictionary<'K, 'V>) : amap<'K, 'V> = constant create

    /// <summary>An adaptive map over fixed, immutable entries.</summary>
    let inline ofSeq (items: seq<'K * 'V>) : amap<'K, 'V> =
        new ConstantMap<'K, 'V>(fun () ->
            items
            |> Seq.map (fun (k, v) -> KeyValuePair(k, v))
            |> FrozenDictionary.ToFrozenDictionary)

    /// <summary>An adaptive map over a fixed array of entries.</summary>
    let inline ofArray (items: ('K * 'V)[]) : amap<'K, 'V> =
        new ConstantMap<'K, 'V>(fun () ->
            items
            |> Seq.map (fun (k, v) -> KeyValuePair(k, v))
            |> FrozenDictionary.ToFrozenDictionary)

    /// <summary>An adaptive map over a fixed list of entries.</summary>
    let inline ofList (items: ('K * 'V) list) : amap<'K, 'V> =
        new ConstantMap<'K, 'V>(fun () ->
            items
            |> Seq.map (fun (k, v) -> KeyValuePair(k, v))
            |> FrozenDictionary.ToFrozenDictionary)

    /// <summary>An adaptive map over a fixed F# <c>Map</c>.</summary>
    let inline ofMap (items: Map<'K, 'V>) : amap<'K, 'V> =
        new ConstantMap<'K, 'V>(fun () ->
            items
            |> Seq.map (fun (KeyValue(k, v)) -> KeyValuePair(k, v))
            |> FrozenDictionary.ToFrozenDictionary)

    /// <summary>Maps every entry of the map.</summary>
    let inline map ([<InlineIfLambda>] f: 'K -> 'V -> 'U) (mapValue: amap<'K, 'V>) : amap<'K, 'U> =
        new MapMapNode<'K, 'V, 'U>(mapValue, fun k v -> ValueSome(f k v))

    /// <summary>Maps every entry, keeping only the ones the mapping returns a value for.</summary>
    let inline choose ([<InlineIfLambda>] f: 'K -> 'V -> 'U option) (mapValue: amap<'K, 'V>) : amap<'K, 'U> =
        new MapMapNode<'K, 'V, 'U>(mapValue, fun k v -> f k v |> Option.toValueOption)

    /// <summary>Maps every entry, keeping only the ones the mapping returns a value for.</summary>
    let inline chooseV ([<InlineIfLambda>] f: 'K -> 'V -> 'U voption) (mapValue: amap<'K, 'V>) : amap<'K, 'U> =
        new MapMapNode<'K, 'V, 'U>(mapValue, f)

    /// <summary>Unions both maps, resolving colliding keys with the given function.</summary>
    let inline unionWith
        ([<InlineIfLambda>] resolve: 'K -> 'V -> 'V -> 'V)
        (left: amap<'K, 'V>)
        (right: amap<'K, 'V>)
        : amap<'K, 'V> =
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
    let inline union (left: amap<'K, 'V>) (right: amap<'K, 'V>) : amap<'K, 'V> = unionWith (fun _ _ r -> r) left right

    /// <summary>
    /// The keys present in both maps, with the values paired. Struct pair:
    /// the voption-first convention collapses FDA's <c>intersect</c> (tuple)
    /// and <c>intersectV</c> (struct) into the struct form.
    /// </summary>
    let inline intersect (left: amap<'K, 'V1>) (right: amap<'K, 'V2>) : amap<'K, struct ('V1 * 'V2)> =
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
        (left: amap<'K, 'V1>)
        (right: amap<'K, 'V2>)
        : amap<'K, 'V3> =
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
        ([<InlineIfLambda>] mapping: 'K -> 'V1 option -> 'V2 option -> 'V3 option)
        (left: amap<'K, 'V1>)
        (right: amap<'K, 'V2>)
        : amap<'K, 'V3> =
        new Choose2MapNode<'K, 'V1, 'V2, 'V3>(
            left,
            right,
            fun k v v2 ->
                mapping k (v |> Option.ofValueOption) (v2 |> Option.ofValueOption)
                |> Option.toValueOption
        )

    /// <summary>
    /// Merges both maps with a mapping that receives the key and both side
    /// values (voptions) and returns the output value (voption). The mapping
    /// is called only when at least one side has a value (FDA parity); a key
    /// with no value on either side is removed without a call. Voption-first:
    /// this is FDA's <c>choose2V</c> (the option variant is not provided).
    /// </summary>
    let inline choose2V
        ([<InlineIfLambda>] mapping: 'K -> 'V1 voption -> 'V2 voption -> 'V3 voption)
        (left: amap<'K, 'V1>)
        (right: amap<'K, 'V2>)
        : amap<'K, 'V3> =
        new Choose2MapNode<'K, 'V1, 'V2, 'V3>(left, right, mapping)

    /// <summary>
    /// A map from a set of entries, keeping ALL values of a key in a HashSet
    /// (FDA <c>ofASet</c> parity). A changed value set emits a fresh HashSet
    /// in the delta (this node allocates by design).
    /// </summary>
    let inline ofASet (elements: aset<'K * 'V>) : amap<'K, HashSet<'V>> =
        new SetToMapKeepAllNode<'K, 'V, 'K * 'V>(elements, id)

    /// <summary>
    /// A map from a set of entries; duplicate keys keep the LAST value
    /// (FDA <c>ofASetIgnoreDuplicates</c> parity: the constant path keeps the
    /// last value, the delta path is arbitrary).
    /// </summary>
    let inline ofASetIgnoreDuplicates (elements: aset<'K * 'V>) : amap<'K, 'V> =
        new SetToMapNode<'K, 'V, 'K * 'V>(elements, id, true)

    /// <summary>
    /// A map from a set, deriving the key from every value and keeping ALL
    /// values of a key in a HashSet (FDA <c>ofASetMapped</c> parity).
    /// </summary>
    let inline ofASetMapped ([<InlineIfLambda>] getKey: 'V -> 'K) (elements: aset<'V>) : amap<'K, HashSet<'V>> =
        new SetToMapKeepAllNode<'K, 'V, 'V>(elements, fun v -> (getKey v, v))

    /// <summary>
    /// A map from a set, deriving the key from every value; duplicate keys
    /// keep the LAST value.
    /// </summary>
    let inline ofASetMappedIgnoreDuplicates ([<InlineIfLambda>] getKey: 'V -> 'K) (elements: aset<'V>) : amap<'K, 'V> =
        new SetToMapNode<'K, 'V, 'V>(elements, (fun v -> (getKey v, v)), true)

    /// <summary>Maps the keys of a set to entries (FDA <c>mapSet</c> parity: the mapping runs per key).</summary>
    let inline mapSet ([<InlineIfLambda>] mapping: 'K -> 'V) (set: aset<'K>) : amap<'K, 'V> =
        new SetToMapNode<'K, 'V, 'K>(set, (fun k -> (k, mapping k)), false)

    /// <summary>An adaptive set of the map's keys (FDA <c>toASet</c> parity).</summary>
    let inline toASet (mapValue: amap<'K, 'V>) : aset<'K> =
        new MapToSetNode<'K, 'V, 'K>(mapValue, fun k _ -> k)

    /// <summary>An adaptive set of the map's distinct values (FDA <c>toASetValues</c> parity).</summary>
    let inline toASetValues (mapValue: amap<'K, 'V>) : aset<'V> =
        new MapToSetNode<'K, 'V, 'V>(mapValue, fun _ v -> v)

    /// <summary>
    /// An adaptive map over an adaptive value of a sequence of entries. Every
    /// change of the value replaces the whole state and emits the diff as the
    /// delta (FDA <c>AMap.ofAVal</c> parity; the value carries no deltas).
    /// </summary>
    let inline ofAVal<'K, 'V, 'S when 'K: equality and 'S :> seq<'K * 'V>> (value: aval<'S>) : amap<'K, 'V> =
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
    let inline bind ([<InlineIfLambda>] mapping: 'T -> amap<'K, 'V>) (value: aval<'T>) : amap<'K, 'V> =
        new BindMapNode<'K, 'V, 'T>(value, mapping)

    /// <summary>
    /// An adaptive map driven by a compute function (FDA <c>AMap.custom</c>
    /// parity, pull model). The compute receives the current view and a delta
    /// builder; it appends the operations that describe the change since the
    /// previous call (for example, consuming its own event queue).
    /// </summary>
    let inline custom
        ([<InlineIfLambda>] compute: IReadOnlyDictionary<'K, 'V> -> MapDeltaBuilder<'K, 'V> -> unit)
        : amap<'K, 'V> =
        new CustomMapNode<'K, 'V>(compute)

    /// <summary>Keeps the entries that satisfy the predicate.</summary>
    let inline filter ([<InlineIfLambda>] predicate: 'K -> 'V -> bool) (mapValue: amap<'K, 'V>) : amap<'K, 'V> =
        new FilterMapNode<'K, 'V>(mapValue, predicate)

    /// <summary>
    /// Adaptively maps every entry of the map to an adaptive value (FDA
    /// <c>AMap.mapA</c> parity). The output follows the aval returned for
    /// each entry; writes to the avals deliver targeted deltas.
    /// </summary>
    let inline mapA ([<InlineIfLambda>] mapping: 'K -> 'V -> aval<'U>) (mapValue: amap<'K, 'V>) : amap<'K, 'U> =
        new ElementMapNode<'K, 'V, 'U>(mapValue, fun k v -> AVal.map ValueSome (mapping k v))

    /// <summary>
    /// Adaptively maps every entry of the map to an adaptive value, keeping
    /// only the entries whose aval holds <c>Some</c> (FDA
    /// <c>AMap.chooseA</c> parity).
    /// </summary>
    let inline chooseA
        ([<InlineIfLambda>] mapping: 'K -> 'V -> aval<'U option>)
        (mapValue: amap<'K, 'V>)
        : amap<'K, 'U> =
        new ElementMapNode<'K, 'V, 'U>(mapValue, fun k v -> AVal.map Option.toValueOption (mapping k v))

    /// <summary>
    /// Adaptively keeps the entries whose predicate aval holds <c>true</c>
    /// (FDA <c>AMap.filterA</c> parity).
    /// </summary>
    let inline filterA
        ([<InlineIfLambda>] predicate: 'K -> 'V -> aval<bool>)
        (mapValue: amap<'K, 'V>)
        : amap<'K, 'V> =
        new ElementMapNode<'K, 'V, 'V>(mapValue, fun k v -> AVal.map (fun b -> if b then ValueSome v else ValueNone) (predicate k v))

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
    let inline observe
        (callback: IReadOnlyDictionary<'K, 'V> -> MapDelta<'K, 'V> -> unit)
        (mapValue: amap<'K, 'V>)
        : IObservation =
        let node = new ObserveMapNode<'K, 'V>(mapValue, callback)
        node.Attach()
        node

    /// <summary>
    /// Adaptively reduces the map with the given <see cref="AdaptiveReduction"/>
    /// over the values. The state is updated incrementally from deltas: a Set
    /// on an existing key subtracts the old value, then adds the new one.
    /// </summary>
    let inline reduce (reduction: AdaptiveReduction<'a, 's, 'v>) (mapValue: amap<'k, 'a>) : aval<'v> =
        new MapReduceNode<'k, 'a, 'a, 's, 'v>(mapValue, (fun _ v -> v), reduction)

    /// <summary>
    /// Maps every entry, then reduces the mapped values with the given
    /// <see cref="AdaptiveReduction"/>. The mapping runs per delta entry.
    /// </summary>
    let inline reduceBy
        (reduction: AdaptiveReduction<'b, 's, 'v>)
        ([<InlineIfLambda>] mapping: 'k -> 'a -> 'b)
        (mapValue: amap<'k, 'a>)
        : aval<'v> =
        new MapReduceNode<'k, 'a, 'b, 's, 'v>(mapValue, mapping, reduction)

    /// <summary>
    /// Adaptively folds the map with <c>add</c>; every removal recomputes the
    /// whole fold. Use <see cref="foldGroup"/> when the operation has an inverse.
    /// </summary>
    let inline fold ([<InlineIfLambda>] add: 's -> 'k -> 'v -> 's) (zero: 's) (mapValue: amap<'k, 'v>) : aval<'s> =
        let mapping k v = struct (k, v)
        let add2 s struct (k, v) = add s k v

        new MapReduceNode<'k, 'v, struct ('k * 'v), 's, 's>(mapValue, mapping, AdaptiveReduction.fold zero add2)


    /// <summary>
    /// Adaptively folds the map with an invertible <c>subtract</c>: removals
    /// update the state without a recompute.
    /// </summary>
    let inline foldGroup
        ([<InlineIfLambda>] add: 's -> 'k -> 'v -> 's)
        ([<InlineIfLambda>] subtract: 's -> 'k -> 'v -> 's)
        (zero: 's)
        (mapValue: amap<'k, 'v>)
        : aval<'s> =
        let inline mapping k v = struct (k, v)
        let inline add2 s struct (k, v) = add s k v
        let inline sub2 s struct (k, v) = subtract s k v

        new MapReduceNode<'k, 'v, struct ('k * 'v), 's, 's>(mapValue, mapping, AdaptiveReduction.group zero add2 sub2)


    /// <summary>Adaptively gets the number of entries.</summary>
    let inline count (mapValue: amap<'K, 'V>) : aval<int> =
        AdaptiveNode<int>(fun () -> mapValue.GetValue().Count)

    /// <summary>Adaptively tests if the map is empty.</summary>
    let inline isEmpty (mapValue: amap<'K, 'V>) : aval<bool> =
        AdaptiveNode<bool>(fun () -> mapValue.GetValue().Count = 0)

    /// <summary>Adaptively tests if any entry satisfies the predicate.</summary>
    let inline exists ([<InlineIfLambda>] predicate: 'K -> 'V -> bool) (mapValue: amap<'K, 'V>) : aval<bool> =
        new MapReduceNode<'K, 'V, bool, int, bool>(
            mapValue,
            predicate,
            AdaptiveReduction.countPositive |> AdaptiveReduction.mapOut (fun c -> c <> 0)
        )

    /// <summary>Adaptively tests if every entry satisfies the predicate.</summary>
    let inline forall ([<InlineIfLambda>] predicate: 'K -> 'V -> bool) (mapValue: amap<'K, 'V>) : aval<bool> =
        new MapReduceNode<'K, 'V, bool, int, bool>(
            mapValue,
            predicate,
            AdaptiveReduction.countNegative |> AdaptiveReduction.mapOut (fun c -> c = 0)
        )

    /// <summary>Adaptively counts the entries that satisfy the predicate.</summary>
    let inline countBy ([<InlineIfLambda>] predicate: 'K -> 'V -> bool) (mapValue: amap<'K, 'V>) : aval<int> =
        new MapReduceNode<'K, 'V, bool, int, int>(mapValue, predicate, AdaptiveReduction.countPositive)

    /// <summary>
    /// Adaptively looks up the key: the value, or <c>ValueNone</c> when the
    /// key is absent. The lookup is O(1) on read.
    /// </summary>
    let inline tryFind (key: 'K) (mapValue: amap<'K, 'V>) : aval<'V voption> =
        AdaptiveNode<'V voption>(fun () ->
            let view = mapValue.GetValue()
            let mutable v = Unchecked.defaultof<'V>

            if view.TryGetValue(key, &v) then ValueSome v else ValueNone)


    /// <summary>
    /// Adaptively looks up the key. Reading the value throws
    /// <see cref="KeyNotFoundException"/> when the key is absent.
    /// </summary>
    let inline find (key: 'K) (mapValue: amap<'K, 'V>) : aval<'V> =
        AdaptiveNode<'V>(fun () ->
            let view = mapValue.GetValue()
            let mutable v = Unchecked.defaultof<'V>

            if view.TryGetValue(key, &v) then
                v
            else
                raise (KeyNotFoundException(sprintf "could not get key: %A" key)))

    /// <summary>A constant map with a single entry.</summary>
    let inline single (key: 'K) (value: 'V) : amap<'K, 'V> =
        new ConstantMap<'K, 'V>(fun () -> [| KeyValuePair(key, value) |] |> FrozenDictionary.ToFrozenDictionary)

    /// <summary>
    /// Materializes the map as an adaptive value. Every change materializes a
    /// new immutable <see cref="FrozenDictionary&lt;'K,'V&gt;"/> (the retain
    /// boundary, like <see cref="force"/>); the value is safe to retain.
    /// </summary>
    let inline toAVal (mapValue: amap<'K, 'V>) : aval<FrozenDictionary<'K, 'V>> =
        AdaptiveNode<FrozenDictionary<'K, 'V>>(fun () -> mapValue.GetValue().ToFrozenDictionary())

    /// <summary>
    /// Returns a transient view of the current state. Valid only until the next
    /// write on the owner thread; do not retain or mutate it. Use
    /// <see cref="force"/> to materialize a snapshot that is safe to retain.
    /// </summary>
    let inline getValue (mapValue: amap<'K, 'V>) = mapValue.GetValue()

    /// <summary>
    /// Materializes the current state as an immutable
    /// <see cref="FrozenDictionary&lt;'K,'V&gt;"/>. This is the only collection
    /// operation that allocates; the result is safe to retain and the library
    /// never touches it again. Runs the pending delta processing (drain) first.
    /// </summary>
    let inline force (mapValue: amap<'K, 'V>) : FrozenDictionary<'K, 'V> =
        mapValue.GetValue().ToFrozenDictionary()

    /// <summary>Materializes the F# <c>Map</c> counterpart (sorted, structural equality).</summary>
    let inline toMap (mapValue: amap<'K, 'V>) : Map<'K, 'V> =
        mapValue.GetValue() |> Seq.map (fun (KeyValue(k, v)) -> (k, v)) |> Map.ofSeq

/// <summary>Operations on changeable maps.</summary>
module CMap =
    /// <summary>An empty changeable map.</summary>
    let inline empty<'K, 'V when 'K: equality> = new cmap<'K, 'V> (Seq.empty)

    /// <summary>A changeable map with the given entries.</summary>
    let inline ofSeq (items: seq<'K * 'V>) = new cmap<'K, 'V> (items)

    /// <summary>Adds or updates an entry. No-op when the value is unchanged.</summary>
    let inline addOrUpdate (key: 'K) (value: 'V) (mapValue: cmap<'K, 'V>) = mapValue.AddOrUpdate key value

    /// <summary>Removes an entry. No-op when absent.</summary>
    let inline remove (key: 'K) (mapValue: cmap<'K, 'V>) = mapValue.Remove key

    /// <summary>Replaces the whole map.</summary>
    let inline set (value: Map<'K, 'V>) (mapValue: cmap<'K, 'V>) = mapValue.Set(Map.toSeq value)

    /// <summary>Views the changeable map as an adaptive map.</summary>
    let inline value (mapValue: cmap<'K, 'V>) : amap<'K, 'V> = mapValue

    /// <summary>Materializes the current state as an immutable snapshot.</summary>
    let inline force (mapValue: cmap<'K, 'V>) : FrozenDictionary<'K, 'V> = AMap.force mapValue

    /// <summary>Materializes the F# <c>Map</c> counterpart.</summary>
    let inline toMap (mapValue: cmap<'K, 'V>) : Map<'K, 'V> = AMap.toMap mapValue

/// <summary>Operations on adaptive lists (docs/ALIST-DESIGN.md §4).</summary>
module AList =
    /// <summary>An empty adaptive list (FDA <c>AList.empty</c> parity).</summary>
    let empty<'T> : alist<'T> = new ConstantList<'T>(fun () -> Array.empty)

    /// <summary>An adaptive list over fixed, immutable items.</summary>
    let inline ofSeq (items: seq<'T>) : alist<'T> =
        new ConstantList<'T>(fun () -> Seq.toArray items)

    /// <summary>An adaptive list over a fixed array.</summary>
    let inline ofArray (items: 'T[]) : alist<'T> = new ConstantList<'T>(fun () -> items)

    /// <summary>An adaptive list over a fixed list.</summary>
    let inline ofList (items: 'T list) : alist<'T> =
        new ConstantList<'T>(fun () -> List.toArray items)

    /// <summary>An adaptive list over a fixed ResizeArray.</summary>
    let inline ofResizeArray (items: ResizeArray<'T>) : alist<'T> =
        new ConstantList<'T>(fun () -> items.ToArray())

    /// <summary>A constant list with a single element.</summary>
    let inline single (value: 'T) : alist<'T> =
        new ConstantList<'T>(fun () -> [| value |])

    /// <summary>
    /// An adaptive list whose content is fixed but computed lazily, once, at
    /// first read (FDA parity: the create function runs at most once).
    /// </summary>
    let inline constant (create: unit -> ResizeArray<'T>) : alist<'T> =
        new ConstantList<'T>(fun () -> create().ToArray())

    /// <summary>Alias of <see cref="constant"/> (FDA parity: delay is constant).</summary>
    let inline delay ([<InlineIfLambda>] create: unit -> ResizeArray<'T>) : alist<'T> = constant create

    /// <summary>Maps every element of the list.</summary>
    let inline map ([<InlineIfLambda>] f: 'T -> 'U) (list: alist<'T>) : alist<'U> =
        new FilterMapListNode<'T, 'U>(list, fun x -> ValueSome(f x))

    /// <summary>Maps every element, keeping only the ones the mapping returns a value for.</summary>
    let inline choose ([<InlineIfLambda>] f: 'T -> 'U option) (list: alist<'T>) : alist<'U> =
        new FilterMapListNode<'T, 'U>(
            list,
            fun x ->
                match f x with
                | Some u -> ValueSome u
                | None -> ValueNone
        )

    /// <summary>Maps every element, keeping only the ones the mapping returns a value for (voption form).</summary>
    let inline chooseV ([<InlineIfLambda>] f: 'T -> 'U voption) (list: alist<'T>) : alist<'U> =
        new FilterMapListNode<'T, 'U>(list, f)

    /// <summary>Keeps the elements that satisfy the predicate.</summary>
    let inline filter ([<InlineIfLambda>] predicate: 'T -> bool) (list: alist<'T>) : alist<'T> =
        new FilterMapListNode<'T, 'T>(list, fun x -> if predicate x then ValueSome x else ValueNone)

    /// <summary>
    /// Adaptively maps every element of the list to an adaptive value (FDA
    /// <c>AList.mapA</c> parity). The output follows the aval returned for
    /// each element; writes to the avals deliver targeted deltas.
    /// </summary>
    let inline mapA ([<InlineIfLambda>] mapping: 'T -> aval<'U>) (list: alist<'T>) : alist<'U> =
        new ElementListNode<'T, 'U>(list, fun _ x -> AVal.map ValueSome (mapping x))

    /// <summary>
    /// Adaptively maps every element of the list to an adaptive value, keeping
    /// only the elements whose aval holds <c>Some</c> (FDA
    /// <c>AList.chooseA</c> parity).
    /// </summary>
    let inline chooseA ([<InlineIfLambda>] mapping: 'T -> aval<'U option>) (list: alist<'T>) : alist<'U> =
        new ElementListNode<'T, 'U>(list, fun _ x -> AVal.map Option.toValueOption (mapping x))

    /// <summary>
    /// Adaptively keeps the elements whose predicate aval holds <c>true</c>
    /// (FDA <c>AList.filterA</c> parity).
    /// </summary>
    let inline filterA ([<InlineIfLambda>] predicate: 'T -> aval<bool>) (list: alist<'T>) : alist<'T> =
        new ElementListNode<'T, 'T>(list, fun _ x -> AVal.map (fun b -> if b then ValueSome x else ValueNone) (predicate x))

    /// <summary>The concatenation of two lists (FDA <c>AList.append</c> parity).</summary>
    let inline append (left: alist<'T>) (right: alist<'T>) : alist<'T> = new AppendListNode<'T>(left, right)

    /// <summary>
    /// Returns a transient view of the current state. Valid only until the next
    /// write on the owner thread; do not retain or mutate it. Use
    /// <see cref="force"/> to materialize a snapshot that is safe to retain.
    /// </summary>
    let inline getValue (list: alist<'T>) = list.GetValue()

    /// <summary>
    /// Materializes the current state as a fresh array. This is the only list
    /// operation that allocates; the result is safe to retain and the library
    /// never touches it again. Runs the pending delta processing (drain) first.
    /// There is no <c>FrozenList</c> in <c>System.Collections.Frozen</c> on
    /// net8/net10, so the array is the retain boundary (docs/ALIST-DESIGN.md
    /// §3.3).
    /// </summary>
    let inline force (list: alist<'T>) : 'T[] = Seq.toArray (list.GetValue())

    /// <summary>Materializes the F# <c>list</c> counterpart.</summary>
    let inline toList (list: alist<'T>) : 'T list = List.ofSeq (list.GetValue())

    /// <summary>Materializes the array counterpart.</summary>
    let inline toArray (list: alist<'T>) : 'T[] = Seq.toArray (list.GetValue())

    /// <summary>Adaptively gets the number of elements.</summary>
    let inline count (list: alist<'T>) : aval<int> =
        AdaptiveNode<int>(fun () -> list.GetValue().Count)

    /// <summary>Adaptively tests if the list is empty.</summary>
    let inline isEmpty (list: alist<'T>) : aval<bool> =
        AdaptiveNode<bool>(fun () -> list.GetValue().Count = 0)

    /// <summary>
    /// Registers a callback that receives the current view and the ordered
    /// delta after every batch that changes the list. The callback runs on the
    /// owner thread after the write, transaction, or pump completes. The view
    /// and the delta are transient: valid only during the callback. Disposing
    /// the returned observation stops delivery.
    /// </summary>
    /// <remarks>
    /// Parity: FDA <c>AddCallback(state, delta)</c> on collection readers.
    /// The delta operations are positional and applied in order; a batch that
    /// removes and reinserts at one position delivers remove+insert (no
    /// netting, docs/ALIST-DESIGN.md §3.1).
    /// </remarks>
    /// <example>
    /// <code>
    /// let items = CList.empty&lt;int&gt;
    /// use obs = AList.observe (fun view delta -&gt;
    ///     printfn "ops: %d count: %d" delta.Operations.Length view.Count)
    ///     (CList.value items)
    /// CList.append 1 items   // prints "ops: 1 count: 1"
    /// </code>
    /// </example>
    let inline observe (callback: IReadOnlyList<'T> -> ListDelta<'T> -> unit) (list: alist<'T>) : IObservation =
        let node = new ObserveListNode<'T>(list, callback)
        node.Attach()
        node

/// <summary>Operations on changeable lists.</summary>
module CList =
    /// <summary>An empty changeable list.</summary>
    let inline empty<'T> : clist<'T> = new ChangeableList<'T>(Seq.empty)

    /// <summary>A changeable list with the given items.</summary>
    let inline ofSeq (items: seq<'T>) : clist<'T> = new ChangeableList<'T>(items)

    /// <summary>A changeable list with the given items.</summary>
    let inline ofArray (items: 'T[]) : clist<'T> = new ChangeableList<'T>(items)

    /// <summary>A changeable list with the given items.</summary>
    let inline ofList (items: 'T list) : clist<'T> = new ChangeableList<'T>(items)

    /// <summary>Appends an element at the end of the list.</summary>
    let inline append (value: 'T) (list: clist<'T>) = list.Append value

    /// <summary>Inserts an element at the start of the list.</summary>
    let inline prepend (value: 'T) (list: clist<'T>) = list.Prepend value

    /// <summary>Inserts an element before the element currently at the position.</summary>
    let inline insertAt (position: int) (value: 'T) (list: clist<'T>) = list.InsertAt(position, value)

    /// <summary>Removes the element currently at the position.</summary>
    let inline removeAt (position: int) (list: clist<'T>) = list.RemoveAt position

    /// <summary>Replaces the element currently at the position.</summary>
    let inline updateAt (position: int) (value: 'T) (list: clist<'T>) = list.UpdateAt(position, value)

    /// <summary>Removes the first occurrence of the value. No-op when absent.</summary>
    let inline remove (value: 'T) (list: clist<'T>) = list.Remove value

    /// <summary>Removes all elements.</summary>
    let inline clear (list: clist<'T>) = list.Clear()

    /// <summary>Replaces the whole list. Last-wins over the whole batch inside a transaction.</summary>
    let inline set (values: seq<'T>) (list: clist<'T>) = list.Set values

    /// <summary>Views the changeable list as an adaptive list.</summary>
    let inline value (list: clist<'T>) : alist<'T> = list

    /// <summary>Materializes the current state as an immutable array snapshot.</summary>
    let inline force (list: clist<'T>) : 'T[] = AList.force list

    /// <summary>Materializes the F# <c>list</c> counterpart.</summary>
    let inline toList (list: clist<'T>) : 'T list = AList.toList list
