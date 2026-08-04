namespace AdaptiveSlop.Core

open System
open System.Buffers
open System.Collections.Generic

// =============================================================================
// Module functions
// =============================================================================

module ASet =
    let inline ofSeq<'T when 'T: comparison> (items: seq<'T>) : IAdaptiveSet<'T> = ConstantSet(Set.ofSeq items)

    let inline map<'T, 'U when 'T: comparison and 'U: comparison>
        ([<InlineIfLambda>] f: 'T -> 'U)
        (set: IAdaptiveSet<'T>)
        : IAdaptiveSet<'U> =
        MapSetNode<'T, 'U>(set, f)

    let inline filter<'T when 'T: comparison>
        ([<InlineIfLambda>] predicate: 'T -> bool)
        (set: IAdaptiveSet<'T>)
        : IAdaptiveSet<'T> =
        FilterSetNode<'T>(set, predicate)

    let inline union<'T when 'T: comparison> (left: IAdaptiveSet<'T>) (right: IAdaptiveSet<'T>) : IAdaptiveSet<'T> =
        UnionSetNode<'T>(left, right)

    let inline getValue (set: IAdaptiveSet<'T>) = set.GetValue()

module CSet =
    let inline empty<'T when 'T: comparison> = ChangeableSet Set.empty<'T>

    let inline ofSeq<'T when 'T: comparison> (items: seq<'T>) = ChangeableSet(Set.ofSeq items)

    let inline add (item: 'T) (set: ChangeableSet<'T>) = set.Add item

    let inline remove (item: 'T) (set: ChangeableSet<'T>) = set.Remove item

    let inline set (value: Set<'T>) (set: ChangeableSet<'T>) = set.Set value

    let inline value (set: ChangeableSet<'T>) : IAdaptiveSet<'T> = set :> IAdaptiveSet<'T>

module AMap =
    let inline ofSeq<'K, 'V when 'K: comparison> (items: seq<'K * 'V>) : IAdaptiveMap<'K, 'V> =
        ConstantMap(Map.ofSeq items)

    let inline map<'K, 'V, 'U when 'K: comparison>
        ([<InlineIfLambda>] f: 'K -> 'V -> 'U)
        (mapValue: IAdaptiveMap<'K, 'V>)
        : IAdaptiveMap<'K, 'U> =
        MapMapNode<'K, 'V, 'U>(mapValue, f) :> IAdaptiveMap<'K, 'U>

    let inline filter<'K, 'V when 'K: comparison>
        ([<InlineIfLambda>] predicate: 'K -> 'V -> bool)
        (mapValue: IAdaptiveMap<'K, 'V>)
        : IAdaptiveMap<'K, 'V> =
        FilterMapNode<'K, 'V>(mapValue, predicate)

    let inline getValue (mapValue: IAdaptiveMap<'K, 'V>) = mapValue.GetValue()

module CMap =
    let inline empty<'K, 'V when 'K: comparison> = ChangeableMap Map.empty<'K, 'V>

    let inline ofSeq<'K, 'V when 'K: comparison> (items: seq<'K * 'V>) = ChangeableMap(Map.ofSeq items)

    let inline addOrUpdate (key: 'K) (value: 'V) (mapValue: ChangeableMap<'K, 'V>) = mapValue.AddOrUpdate key value

    let inline remove (key: 'K) (mapValue: ChangeableMap<'K, 'V>) = mapValue.Remove key

    let inline set (value: Map<'K, 'V>) (mapValue: ChangeableMap<'K, 'V>) = mapValue.Set value

    let inline value (mapValue: ChangeableMap<'K, 'V>) : IAdaptiveMap<'K, 'V> = mapValue
