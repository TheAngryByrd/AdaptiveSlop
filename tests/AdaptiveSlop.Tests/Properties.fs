// Shares a collection with the main test module: the adaptive graph is
// confined to one owner thread (PLAN.md §7.1), so xUnit must not run this
// module's tests in parallel with the rest of the suite.
[<global.Xunit.Collection("AdaptiveSlop")>]
module AdaptiveSlop.Properties

#nowarn "893"

open System
open System.Collections.Generic
open global.Xunit
open FsCheck
open FsCheck.FSharp
open AdaptiveSlop.Core

// =============================================================================
// Kipo usage shapes (E:\Kipo Pomo.Core\Projections.fs), split into small
// dedicated part tests. Part 1: the entity-scenario cross-map lookup
// (world.Scenarios |> AMap.tryFind inside AMap.mapA, then AMap.choose).
// =============================================================================

[<Fact>]
let ``part 1: cross-map lookup follows the entity-first sequence`` () =
    let entities = CMap.empty<int, int> // entity -> lookup key
    let lookups = CMap.empty<int, int> // key -> value

    // should be empty here
    let contexts =
        entities
        |> AMap.mapA (fun _ key -> AMap.tryFind key lookups)
        |> AMap.chooseV (fun _ v -> v)

    // The shrunk counterexample of the failing property: [0; 2; -1].
    // op 0: upsert entity 0 -> lookup key 0 (lookups empty: no context yet).
    CMap.addOrUpdate 0 0 entities
    Assert.Equal<Map<int, int>>(Map.empty, AMap.toMap contexts)
    // op 2: upsert lookup 0 -> 0 (the context appears).
    CMap.addOrUpdate 0 0 lookups
    Assert.Equal<Map<int, int>>(Map.ofList [ 0, 0 ], AMap.toMap contexts)
    // op -1: remove lookup 0 (the context disappears).
    CMap.remove 0 lookups
    Assert.Equal<Map<int, int>>(Map.empty, AMap.toMap contexts)

// =============================================================================
// FsCheck property tests (FsCheck 3.x API, the built-in runner per the docs:
// Check.QuickThrowOnFailure inside plain xUnit facts).
//
// The reference-impl model tests (MAPA-DESIGN §12) live here; the smoke
// tests below prove the FsCheck machinery runs before the models build on
// it.
// =============================================================================

[<Fact>]
let ``FsCheck smoke: reverse is involutive`` () =
    let revRevIsOrig (xs: int list) = List.rev (List.rev xs) = xs
    Check.QuickThrowOnFailure revRevIsOrig

[<Fact>]
let ``FsCheck smoke: ASet roundtrip`` () =
    let roundtrip (xs: int list) =
        let s = CSet.ofSeq xs
        Set.ofSeq (ASet.force (CSet.value s)) = Set.ofList xs

    Check.QuickThrowOnFailure roundtrip

[<Fact>]
let ``FsCheck smoke: AList append builds the sequence`` () =
    let builds (xs: int list) =
        let l = CList.empty<int>

        for x in xs do
            CList.append x l

        AList.force (CList.value l) = List.toArray xs

    Check.QuickThrowOnFailure builds

// =============================================================================
// Algebraic laws over generated data. These are the real property tests: the
// law is a clean universal statement, FsCheck generates the data directly
// (built-in arbitraries), and a failure shrinks to a minimal concrete value.
// =============================================================================

[<Fact>]
let ``law: AList.map preserves the mapped content`` () =
    let law (xs: int list) =
        AList.force (AList.map ((+) 1) (AList.ofSeq xs)) = Array.map ((+) 1) (List.toArray xs)

    Check.QuickThrowOnFailure law

[<Fact>]
let ``law: AList.append concatenates`` () =
    let law (a: int list, b: int list) =
        AList.force (AList.append (AList.ofSeq a) (AList.ofSeq b)) = Array.append (List.toArray a) (List.toArray b)

    Check.QuickThrowOnFailure law

[<Fact>]
let ``law: AList.rev reverses and is involutive`` () =
    let law (xs: int list) =
        let reversed = AList.force (AList.rev (AList.ofSeq xs))
        let twice = AList.force (AList.rev (AList.rev (AList.ofSeq xs)))
        reversed = Array.rev (List.toArray xs) && twice = List.toArray xs

    Check.QuickThrowOnFailure law

[<Fact>]
let ``law: AList.sort sorts and is idempotent`` () =
    let law (xs: int list) =
        let sorted = AList.force (AList.sort (AList.ofSeq xs))
        let twice = AList.force (AList.sort (AList.sort (AList.ofSeq xs)))
        sorted = Array.sort (List.toArray xs) && twice = sorted

    Check.QuickThrowOnFailure law

[<Fact>]
let ``law: AList.take truncates`` () =
    let law (n: int, xs: int list) =
        let count = abs n % (List.length xs + 1)
        AList.force (AList.take count (AList.ofSeq xs)) = Array.truncate count (List.toArray xs)

    Check.QuickThrowOnFailure law

[<Fact>]
let ``law: AList.skip skips and clamps`` () =
    let law (n: int, xs: int list) =
        let count = abs n % (List.length xs + 2)

        let expected =
            if count >= List.length xs then [||]
            else Array.skip count (List.toArray xs)

        AList.force (AList.skip count (AList.ofSeq xs)) = expected

    Check.QuickThrowOnFailure law

[<Fact>]
let ``law: AList.sub slices`` () =
    let law (o: int, c: int, xs: int list) =
        let len = List.length xs
        let offset = abs o % (len + 1)
        let count = abs c % (len + 2)

        let expected =
            let from = min offset len
            let take = max 0 (min count (len - from))
            Array.sub (List.toArray xs) from take

        AList.force (AList.sub offset count (AList.ofSeq xs)) = expected

    Check.QuickThrowOnFailure law

[<Fact>]
let ``law: AList.pairwise pairs adjacent elements`` () =
    let law (xs: int list) =
        let arr = List.toArray xs

        let expected =
            seq {
                for i in 0 .. arr.Length - 2 do
                    struct (arr[i], arr[i + 1])
            }
            |> Array.ofSeq

        AList.force (AList.pairwise (AList.ofSeq xs)) = expected

    Check.QuickThrowOnFailure law

[<Fact>]
let ``law: AList.concat flattens`` () =
    let law (xss: int list list) =
        AList.force (AList.concat (List.map AList.ofSeq xss)) = Array.concat (List.map List.toArray xss)

    Check.QuickThrowOnFailure law

[<Fact>]
let ``law: AList.choose selects`` () =
    let law (xs: int list) =
        let f x = if x % 2 = 0 then Some(x * 10) else None
        AList.force (AList.choose f (AList.ofSeq xs)) = Array.choose f (List.toArray xs)

    Check.QuickThrowOnFailure law

[<Fact>]
let ``law: AList.indexed pairs positions`` () =
    let law (xs: int list) =
        AList.force (AList.indexed (AList.ofSeq xs)) = Array.mapi (fun i x -> struct (i, x)) (List.toArray xs)

    Check.QuickThrowOnFailure law

[<Fact>]
let ``law: AList.mapA with constants maps`` () =
    let law (xs: int list) =
        AList.force (AList.mapA (fun x -> AVal.constant (x * 2)) (AList.ofSeq xs)) = Array.map ((*) 2) (List.toArray xs)

    Check.QuickThrowOnFailure law

[<Fact>]
let ``law: AList reductions match the forced content`` () =
    let law (xs: int list) =
        let arr = List.toArray xs

        (AVal.getValue (AList.sum (AList.ofSeq xs)) = Array.sum arr)
        && (AVal.getValue (AList.countBy (fun x -> x % 2 = 0) (AList.ofSeq xs)) = (Array.filter (fun x -> x % 2 = 0) arr).Length)
        && (AVal.getValue (AList.tryMin (AList.ofSeq xs)) = (if arr.Length = 0 then ValueNone else ValueSome(Array.min arr)))
        && (AVal.getValue (AList.tryMax (AList.ofSeq xs)) = (if arr.Length = 0 then ValueNone else ValueSome(Array.max arr)))

    Check.QuickThrowOnFailure law

[<Fact>]
let ``law: ASet.map and filter preserve content`` () =
    let law (s: Set<int>) =
        (ASet.toSet (ASet.map ((+) 1) (ASet.ofSeq s)) = Set.map ((+) 1) s)
        && (ASet.toSet (ASet.filter (fun x -> x % 2 = 0) (ASet.ofSeq s)) = Set.filter (fun x -> x % 2 = 0) s)

    Check.QuickThrowOnFailure law

[<Fact>]
let ``law: ASet union and intersect`` () =
    let law (a: Set<int>, b: Set<int>) =
        (ASet.toSet (ASet.union (ASet.ofSeq a) (ASet.ofSeq b)) = Set.union a b)
        && (ASet.toSet (ASet.intersect (ASet.ofSeq a) (ASet.ofSeq b)) = Set.intersect a b)

    Check.QuickThrowOnFailure law

[<Fact>]
let ``law: ASet choose and count`` () =
    let law (s: Set<int>) =
        let f x = if x % 3 = 0 then Some(x / 3) else None

        (ASet.toSet (ASet.choose f (ASet.ofSeq s)) = Set.ofSeq (Seq.choose f s))
        && (AVal.getValue (ASet.count (ASet.ofSeq s)) = Set.count s)

    Check.QuickThrowOnFailure law

[<Fact>]
let ``law: ASet reductions over constant sources`` () =
    let law (s: Set<int>) =
        (AVal.getValue (ASet.sum (ASet.ofSeq s)) = Seq.sum s)
        && (AVal.getValue (ASet.countBy (fun x -> x % 2 = 0) (ASet.ofSeq s)) = (Seq.filter (fun x -> x % 2 = 0) s |> Seq.length))

    Check.QuickThrowOnFailure law

[<Fact>]
let ``law: AMap reductions over constant sources`` () =
    let law (m: Map<int, int>) =
        AVal.getValue (AMap.fold (fun acc _ v -> acc + v) 0 (AMap.ofSeq (Map.toSeq m))) = (m |> Map.toSeq |> Seq.sumBy snd)

    Check.QuickThrowOnFailure law

[<Fact>]
let ``law: ASet mapA with constants maps`` () =
    let law (s: Set<int>) =
        ASet.toSet (ASet.mapA (fun x -> AVal.constant (x * 2)) (ASet.ofSeq s)) = Set.map ((*) 2) s

    Check.QuickThrowOnFailure law

[<Fact>]
let ``law: AMap mapV and filter preserve content`` () =
    let law (m: Map<int, int>) =
        (AMap.toMap (AMap.mapV ((+) 1) (AMap.ofSeq (Map.toSeq m))) = Map.map (fun _ v -> v + 1) m)
        && (AMap.toMap (AMap.filter (fun _ v -> v % 2 = 0) (AMap.ofSeq (Map.toSeq m))) = Map.filter (fun _ v -> v % 2 = 0) m)

    Check.QuickThrowOnFailure law

[<Fact>]
let ``law: AMap keys and toASet`` () =
    let law (m: Map<int, int>) =
        ASet.toSet (AMap.keys (AMap.ofSeq (Map.toSeq m))) = Set.ofSeq (Map.keys m)

        let pairs =
            m |> Map.toSeq |> Seq.map (fun (k, v) -> struct (k, v)) |> Set.ofSeq

        ASet.toSet (AMap.toASet (AMap.ofSeq (Map.toSeq m))) = pairs

    Check.QuickThrowOnFailure law

[<Fact>]
let ``law: AMap chooseV selects`` () =
    let law (m: Map<int, int>) =
        let f (k: int) (v: int) = if v % 2 = 0 then ValueSome(k * 100 + v) else ValueNone

        let expected =
            m
            |> Map.toSeq
            |> Seq.choose (fun (k, v) -> if v % 2 = 0 then Some(k, k * 100 + v) else None)
            |> Map.ofSeq

        AMap.toMap (AMap.chooseV f (AMap.ofSeq (Map.toSeq m))) = expected

    Check.QuickThrowOnFailure law

[<Fact>]
let ``law: changeable roundtrips`` () =
    let law (xs: (int * int) list, s: Set<int>) =
        AMap.toMap (CMap.value (CMap.ofSeq xs)) = Map.ofSeq xs
        && ASet.toSet (CSet.value (CSet.ofSeq s)) = s

    Check.QuickThrowOnFailure law

[<Fact>]
let ``law: AVal constant, map and map2`` () =
    let law (a: int, b: int) =
        AVal.getValue (AVal.constant a) = a
        && AVal.getValue (AVal.map ((+) 1) (AVal.constant a)) = a + 1
        && AVal.getValue (AVal.map2 (fun x y -> x * y) (AVal.constant a) (AVal.constant b)) = a * b

    Check.QuickThrowOnFailure law

// =============================================================================
// Incremental laws: the same algebraic laws re-asserted after EVERY mutation
// of a changeable source. The one-shot laws above check the load path only;
// these check the incremental path (drain, deltas, version gates). The forced
// source content is the oracle; the mirrors exist only for position decoding.
// =============================================================================

/// Applies one op (kind = op % 3: insert / removeAt / updateAt) to a changeable
/// list and its mirror. The position is derived from the mirror length, so it
/// is always valid for the current state.
let applyListMutation (op: int) (l: ChangeableList<int>) (model: ResizeArray<int>) =
    let kind = op % 3
    let rest = op / 3
    let element = rest % 10

    let position =
        let p = (rest / 10) % (model.Count + 1)
        if p < 0 then p + (model.Count + 1) else p

    match kind with
    | 0 ->
        CList.insertAt position element l
        model.Insert(position, element)
    | 1 ->
        if model.Count > 0 && position < model.Count then
            CList.removeAt position l
            model.RemoveAt position
    | _ ->
        if model.Count > 0 && position < model.Count then
            CList.updateAt position element l
            model[position] <- element

/// Applies one op (kind = op % 2: add / remove) to a changeable set.
let applySetMutation (op: int) (s: ChangeableSet<int>) =
    let kind = op % 2
    let rest = op / 2
    let element = rest % 20

    match kind with
    | 0 -> CSet.add element s
    | _ -> CSet.remove element s

/// Applies one op (kind = op % 2: upsert / remove) to a changeable map.
let applyMapMutation (op: int) (m: ChangeableMap<int, int>) =
    let kind = op % 2
    let rest = op / 2
    let key = rest % 10
    let value = (rest / 10) % 100

    match kind with
    | 0 -> CMap.addOrUpdate key value m
    | _ -> CMap.remove key m

[<Fact>]
let ``incremental law: AList.sort stays sorted`` () =
    let prop (ops: int list) =
        let l = CList.empty<int>
        let model = ResizeArray<int>()
        let sorted = AList.sort (CList.value l)
        let mutable ok = true

        for op in ops do
            applyListMutation op l model

            if AList.force sorted <> Array.sort (AList.force (CList.value l)) then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``incremental law: AList.rev stays reversed`` () =
    let prop (ops: int list) =
        let l = CList.empty<int>
        let model = ResizeArray<int>()
        let reversed = AList.rev (CList.value l)
        let mutable ok = true

        for op in ops do
            applyListMutation op l model

            if AList.force reversed <> Array.rev (AList.force (CList.value l)) then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``incremental law: AList.map stays mapped`` () =
    let prop (ops: int list) =
        let l = CList.empty<int>
        let model = ResizeArray<int>()
        let mapped = AList.map ((+) 1) (CList.value l)
        let mutable ok = true

        for op in ops do
            applyListMutation op l model

            if AList.force mapped <> Array.map ((+) 1) (AList.force (CList.value l)) then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``incremental law: AList.take stays truncated`` () =
    let prop (ops: int list) =
        let l = CList.empty<int>
        let model = ResizeArray<int>()
        let count = List.length ops % 5
        let taken = AList.take count (CList.value l)
        let mutable ok = true

        for op in ops do
            applyListMutation op l model

            if AList.force taken <> Array.truncate count (AList.force (CList.value l)) then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``incremental law: AList.skip stays skipped`` () =
    let prop (ops: int list) =
        let l = CList.empty<int>
        let model = ResizeArray<int>()
        let count = List.length ops % 5
        let skipped = AList.skip count (CList.value l)
        let mutable ok = true

        for op in ops do
            applyListMutation op l model

            let source = AList.force (CList.value l)

            let expected =
                if count >= source.Length then [||]
                else Array.skip count source

            if AList.force skipped <> expected then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``incremental law: AList.sub stays sliced`` () =
    let prop (ops: int list) =
        let l = CList.empty<int>
        let model = ResizeArray<int>()
        let offset = List.length ops % 3
        let count = List.length ops % 5
        let sliced = AList.sub offset count (CList.value l)
        let mutable ok = true

        for op in ops do
            applyListMutation op l model

            let source = AList.force (CList.value l)

            let expected =
                let from = min offset source.Length
                let take = max 0 (min count (source.Length - from))
                Array.sub source from take

            if AList.force sliced <> expected then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``incremental law: AList.pairwise stays paired`` () =
    let prop (ops: int list) =
        let l = CList.empty<int>
        let model = ResizeArray<int>()
        let pairs = AList.pairwise (CList.value l)
        let mutable ok = true

        for op in ops do
            applyListMutation op l model

            let source = AList.force (CList.value l)

            let expected =
                seq {
                    for i in 0 .. source.Length - 2 do
                        struct (source[i], source[i + 1])
                }
                |> Array.ofSeq

            if AList.force pairs <> expected then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``incremental law: AList.indexed stays indexed`` () =
    let prop (ops: int list) =
        let l = CList.empty<int>
        // Mirror: element -> the index its mapping ran at (positions stick).
        let model = ResizeArray<struct (int * int)>()
        let indexed = AList.indexed (CList.value l)
        let mutable ok = true

        for op in ops do
            let kind = op % 3
            let rest = op / 3
            let element = rest % 10

            let position =
                let p = (rest / 10) % (model.Count + 1)
                if p < 0 then p + (model.Count + 1) else p

            match kind with
            | 0 ->
                CList.insertAt position element l
                model.Insert(position, struct (element, position))
            | 1 ->
                if model.Count > 0 && position < model.Count then
                    CList.removeAt position l
                    model.RemoveAt position
            | _ -> // Update: equal values are a no-op (no re-map, the index sticks).
                if model.Count > 0 && position < model.Count then
                    let struct (oldE, _) = model[position]

                    if oldE <> element then
                        CList.updateAt position element l
                        model[position] <- struct (element, position)

            let expected = model |> Seq.map (fun struct (e, i) -> struct (i, e)) |> Array.ofSeq

            if AList.force indexed <> expected then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``incremental law: AList.mapA stays mapped`` () =
    let prop (ops: int list) =
        let l = CList.empty<int>
        let model = ResizeArray<int>()
        let mapped = AList.mapA (fun x -> AVal.constant (x * 2)) (CList.value l)
        let mutable ok = true

        for op in ops do
            applyListMutation op l model

            if AList.force mapped <> Array.map ((*) 2) (AList.force (CList.value l)) then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``incremental law: AList.choose stays chosen`` () =
    let prop (ops: int list) =
        let l = CList.empty<int>
        let model = ResizeArray<int>()
        let f x = if x % 2 = 0 then Some(x * 10) else None
        let chosen = AList.choose f (CList.value l)
        let mutable ok = true

        for op in ops do
            applyListMutation op l model

            if AList.force chosen <> Array.choose f (AList.force (CList.value l)) then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``incremental law: AList reductions stay correct`` () =
    let prop (ops: int list) =
        let l = CList.empty<int>
        let model = ResizeArray<int>()
        let total = AList.sum (CList.value l)
        let evenCount = AList.countBy (fun x -> x % 2 = 0) (CList.value l)
        let acc = AList.fold (fun s x -> s * 10 + x) 0 (CList.value l)
        let mutable ok = true

        for op in ops do
            applyListMutation op l model

            let source = AList.force (CList.value l)

            if
                AVal.getValue total <> Array.sum source
                || AVal.getValue evenCount <> (Array.filter (fun x -> x % 2 = 0) source).Length
                || AVal.getValue acc <> Array.fold (fun s x -> s * 10 + x) 0 source
            then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``incremental law: ASet.map and filter stay correct`` () =
    let prop (ops: int list) =
        let s = CSet.empty<int>
        let mapped = ASet.map ((+) 1) (CSet.value s)
        let filtered = ASet.filter (fun x -> x % 2 = 0) (CSet.value s)
        let mutable ok = true

        for op in ops do
            applySetMutation op s

            let source = Set.ofSeq (ASet.toSet (CSet.value s))

            if ASet.toSet mapped <> Set.map ((+) 1) source || ASet.toSet filtered <> Set.filter (fun x -> x % 2 = 0) source then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``incremental law: ASet union and intersect stay correct`` () =
    let prop (ops: int list) =
        let a = CSet.empty<int>
        let b = CSet.empty<int>
        let union = ASet.union (CSet.value a) (CSet.value b)
        let intersect = ASet.intersect (CSet.value a) (CSet.value b)
        let mutable ok = true

        for op in ops do
            let kind = op % 3
            let rest = op / 3
            let element = rest % 20
            let which = (rest / 20) % 2
            let target = if which = 0 then a else b

            match kind with
            | 0 -> CSet.add element target
            | 1 -> CSet.remove element target
            | _ -> () // no-op

            let sourceA = Set.ofSeq (ASet.toSet (CSet.value a))
            let sourceB = Set.ofSeq (ASet.toSet (CSet.value b))

            if ASet.toSet union <> Set.union sourceA sourceB || ASet.toSet intersect <> Set.intersect sourceA sourceB then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``incremental law: ASet mapA and count stay correct`` () =
    let prop (ops: int list) =
        let s = CSet.empty<int>
        let mapped = ASet.mapA (fun x -> AVal.constant (x * 2)) (CSet.value s)
        let count = ASet.count (CSet.value s)
        let mutable ok = true

        for op in ops do
            applySetMutation op s

            let source = Set.ofSeq (ASet.toSet (CSet.value s))

            if ASet.toSet mapped <> Set.map ((*) 2) source || AVal.getValue count <> Set.count source then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``incremental law: ASet sum stays correct`` () =
    let prop (ops: int list) =
        let s = CSet.empty<int>
        let total = ASet.sum (CSet.value s)
        let mutable ok = true

        for op in ops do
            applySetMutation op s

            let source = Set.ofSeq (ASet.toSet (CSet.value s))

            if AVal.getValue total <> Seq.sum source then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``incremental law: AMap mapV and filter stay correct`` () =
    let prop (ops: int list) =
        let m = CMap.empty<int, int>
        let mapped = AMap.mapV ((+) 1) (CMap.value m)
        let filtered = AMap.filter (fun _ v -> v % 2 = 0) (CMap.value m)
        let mutable ok = true

        for op in ops do
            applyMapMutation op m

            let source = AMap.toMap (CMap.value m)

            if
                AMap.toMap mapped <> Map.map (fun _ v -> v + 1) source
                || AMap.toMap filtered <> Map.filter (fun _ v -> v % 2 = 0) source
            then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``incremental law: AMap keys stay correct`` () =
    let prop (ops: int list) =
        let m = CMap.empty<int, int>
        let keys = AMap.keys (CMap.value m)
        let mutable ok = true

        for op in ops do
            applyMapMutation op m

            let source = AMap.toMap (CMap.value m)

            if ASet.toSet keys <> Set.ofSeq (Map.keys source) then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``incremental law: AMap fold stays correct`` () =
    let prop (ops: int list) =
        let m = CMap.empty<int, int>
        let total = AMap.fold (fun acc _ v -> acc + v) 0 (CMap.value m)
        let mutable ok = true

        for op in ops do
            applyMapMutation op m

            let source = AMap.toMap (CMap.value m)

            if AVal.getValue total <> (source |> Map.toSeq |> Seq.sumBy snd) then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

// =============================================================================
// Scalar combinators with a dedicated generator. The scalar layer (map,
// map2/3/4/N, bind/bind2/bind3) is the foundation of the collections (mapA
// mappings are avals, tryFind returns an aval); its dynamic behavior must be
// proven the same way. A typed op generator replaces the int decoding.
// =============================================================================

/// One scalar write: which input to set and to what value.
type ScalarOp =
    | SetInput of inputIndex: int * value: int

/// Generates a sequence of scalar writes over four inputs, values in [0, 100).
let scalarOpsGen: Gen<ScalarOp list> =
    Gen.listOf (
        gen {
            let! idx = Gen.choose(0, 3)
            let! value = Gen.choose(0, 99)
            return SetInput(idx, value)
        }
    )

/// The registered arbitrary: FsCheck resolves the property parameter by type.
type ScalarArbs =
    static member ScalarOpList() : Arbitrary<ScalarOp list> = Arb.fromGen scalarOpsGen

let private scalarConfig = Config.Quick.WithArbitrary([| typeof<ScalarArbs> |])

[<Fact>]
let ``scalar map tracks its input`` () =
    let prop (ops: ScalarOp list) =
        let inputs = [| CVal.create 0; CVal.create 0; CVal.create 0; CVal.create 0 |]
        let derived = AVal.map (fun x -> x * 2 + 1) (CVal.value inputs[0])
        let model = [| 0; 0; 0; 0 |]
        let mutable ok = true

        for op in ops do
            match op with
            | SetInput(i, v) ->
                CVal.set v inputs[i]
                model[i] <- v

            if AVal.getValue derived <> model[0] * 2 + 1 then
                ok <- false

        ok

    Check.One (scalarConfig, prop)

[<Fact>]
let ``scalar map2 tracks its inputs`` () =
    let prop (ops: ScalarOp list) =
        let inputs = [| CVal.create 0; CVal.create 0; CVal.create 0; CVal.create 0 |]
        let derived = AVal.map2 (fun x y -> x * 10 + y) (CVal.value inputs[0]) (CVal.value inputs[1])
        let model = [| 0; 0; 0; 0 |]
        let mutable ok = true

        for op in ops do
            match op with
            | SetInput(i, v) ->
                CVal.set v inputs[i]
                model[i] <- v

            if AVal.getValue derived <> model[0] * 10 + model[1] then
                ok <- false

        ok

    Check.One (scalarConfig, prop)

[<Fact>]
let ``scalar map3 tracks its inputs`` () =
    let prop (ops: ScalarOp list) =
        let inputs = [| CVal.create 0; CVal.create 0; CVal.create 0; CVal.create 0 |]

        let derived =
            AVal.map3
                (fun x y z -> x * 100 + y * 10 + z)
                (CVal.value inputs[0])
                (CVal.value inputs[1])
                (CVal.value inputs[2])

        let model = [| 0; 0; 0; 0 |]
        let mutable ok = true

        for op in ops do
            match op with
            | SetInput(i, v) ->
                CVal.set v inputs[i]
                model[i] <- v

            if AVal.getValue derived <> model[0] * 100 + model[1] * 10 + model[2] then
                ok <- false

        ok

    Check.One (scalarConfig, prop)

[<Fact>]
let ``scalar map4 tracks its inputs`` () =
    let prop (ops: ScalarOp list) =
        let inputs = [| CVal.create 0; CVal.create 0; CVal.create 0; CVal.create 0 |]

        let derived =
            AVal.map4
                (fun w x y z -> w * 1000 + x * 100 + y * 10 + z)
                (CVal.value inputs[0])
                (CVal.value inputs[1])
                (CVal.value inputs[2])
                (CVal.value inputs[3])

        let model = [| 0; 0; 0; 0 |]
        let mutable ok = true

        for op in ops do
            match op with
            | SetInput(i, v) ->
                CVal.set v inputs[i]
                model[i] <- v

            if AVal.getValue derived <> model[0] * 1000 + model[1] * 100 + model[2] * 10 + model[3] then
                ok <- false

        ok

    Check.One (scalarConfig, prop)

[<Fact>]
let ``scalar mapN tracks its inputs`` () =
    let prop (ops: ScalarOp list) =
        let inputs = [| CVal.create 0; CVal.create 0; CVal.create 0; CVal.create 0 |]

        let derived =
            AVal.mapN
                (fun (arr: int[]) -> arr[0] * 1000 + arr[1] * 100 + arr[2] * 10 + arr[3])
                [| CVal.value inputs[0]; CVal.value inputs[1]; CVal.value inputs[2]; CVal.value inputs[3] |]

        let model = [| 0; 0; 0; 0 |]
        let mutable ok = true

        for op in ops do
            match op with
            | SetInput(i, v) ->
                CVal.set v inputs[i]
                model[i] <- v

            if AVal.getValue derived <> model[0] * 1000 + model[1] * 100 + model[2] * 10 + model[3] then
                ok <- false

        ok

    Check.One (scalarConfig, prop)

[<Fact>]
let ``scalar bind tracks its input and its inner`` () =
    let prop (ops: ScalarOp list) =
        let inputs = [| CVal.create 0; CVal.create 0; CVal.create 0; CVal.create 0 |]

        // The inner aval reads input 1; the bind's value (input 0) swaps it.
        let derived =
            AVal.bind
                (fun x -> AVal.map (fun y -> x * 10 + y) (CVal.value inputs[1]))
                (CVal.value inputs[0])

        let model = [| 0; 0; 0; 0 |]
        let mutable ok = true

        for op in ops do
            match op with
            | SetInput(i, v) ->
                CVal.set v inputs[i]
                model[i] <- v

            if AVal.getValue derived <> model[0] * 10 + model[1] then
                ok <- false

        ok

    Check.One (scalarConfig, prop)

[<Fact>]
let ``scalar bind2 tracks its inputs and its inner`` () =
    let prop (ops: ScalarOp list) =
        let inputs = [| CVal.create 0; CVal.create 0; CVal.create 0; CVal.create 0 |]

        // The inner aval reads input 2; the bind's values (inputs 0, 1) swap it.
        let derived =
            AVal.bind2
                (fun x y -> AVal.map (fun z -> x * 100 + y * 10 + z) (CVal.value inputs[2]))
                (CVal.value inputs[0])
                (CVal.value inputs[1])

        let model = [| 0; 0; 0; 0 |]
        let mutable ok = true

        for op in ops do
            match op with
            | SetInput(i, v) ->
                CVal.set v inputs[i]
                model[i] <- v

            if AVal.getValue derived <> model[0] * 100 + model[1] * 10 + model[2] then
                ok <- false

        ok

    Check.One (scalarConfig, prop)

[<Fact>]
let ``scalar bind3 tracks its inputs and its inner`` () =
    let prop (ops: ScalarOp list) =
        let inputs = [| CVal.create 0; CVal.create 0; CVal.create 0; CVal.create 0 |]

        // The inner aval reads input 3; the bind's values (inputs 0, 1, 2) swap it.
        let derived =
            AVal.bind3
                (fun x y z -> AVal.map (fun w -> x * 1000 + y * 100 + z * 10 + w) (CVal.value inputs[3]))
                (CVal.value inputs[0])
                (CVal.value inputs[1])
                (CVal.value inputs[2])

        let model = [| 0; 0; 0; 0 |]
        let mutable ok = true

        for op in ops do
            match op with
            | SetInput(i, v) ->
                CVal.set v inputs[i]
                model[i] <- v

            if AVal.getValue derived <> model[0] * 1000 + model[1] * 100 + model[2] * 10 + model[3] then
                ok <- false

        ok

    Check.One (scalarConfig, prop)

// =============================================================================
// Collection scenario generators: typed, weighted op sequences with a random
// initial state, registered as FsCheck arbitraries. The differential
// properties consume the scenarios instead of decoding int lists; the
// initial state exercises the load path, the ops the drain path.
// =============================================================================

type SetOp =
    | Add of int * int // element, initial aval value
    | Remove of int
    | SetValue of int * int // element, new aval value

type MapOp =
    | Upsert of int * int
    | Remove of int
    | SetValue of int * int // key, new aval value

type ListChange =
    | Insert of int * int // element, position payload
    | RemoveAt of int // position payload
    | UpdateAt of int * int // element, position payload
    | SetValue of int * int // element, new aval value

type CrossOp =
    | EntityUpsert of int * int
    | EntityRemove of int
    | LookupUpsert of int * int
    | LookupRemove of int

type JoinOp =
    | LeftEdit of MapOp
    | RightEdit of MapOp

type ExternalOp =
    | Replace of int
    | Invalidate

type SetScenario = { initial: (int * int) list; ops: SetOp list }
type MapScenario = { initial: (int * int) list; ops: MapOp list }
type ListScenario = { initial: (int * int) list; ops: ListChange list }
type CrossScenario =
    { initialEntities: (int * int) list
      initialLookups: (int * int) list
      ops: CrossOp list }

type JoinScenario =
    { initialA: (int * int) list
      initialB: (int * int) list
      ops: JoinOp list }

type ExternalScenario = { ops: ExternalOp list }

let pairGen =
    gen {
        let! k = Gen.choose(0, 19)
        let! v = Gen.choose(0, 99)
        return k, v
    }

/// Deduplicates the initial pairs with the last value winning (the set/map
/// semantics), keeping the generated initial state valid for both sides.
let dedupPairs (xs: (int * int) list) =
    let d = Dictionary<int, int>()

    for (k, v) in xs do
        d[k] <- v

    [ for KeyValue(k, v) in d -> k, v ]

let setOpGen: Gen<SetOp> =
    Gen.frequency [
        (3, gen {
            let! e = Gen.choose(0, 19)
            let! v = Gen.choose(0, 99)
            return Add(e, v)
        })
        (2, gen {
            let! e = Gen.choose(0, 19)
            return SetOp.Remove e
        })
        (2, gen {
            let! e = Gen.choose(0, 19)
            let! v = Gen.choose(0, 99)
            return SetOp.SetValue(e, v)
        })
    ]

let mapOpGen: Gen<MapOp> =
    Gen.frequency [
        (3, gen {
            let! k = Gen.choose(0, 9)
            let! v = Gen.choose(0, 99)
            return Upsert(k, v)
        })
        (2, gen {
            let! k = Gen.choose(0, 9)
            return MapOp.Remove k
        })
        (2, gen {
            let! k = Gen.choose(0, 9)
            let! v = Gen.choose(0, 99)
            return MapOp.SetValue(k, v)
        })
    ]

let listChangeGen: Gen<ListChange> =
    Gen.frequency [
        (3, gen {
            let! e = Gen.choose(0, 9)
            let! p = Gen.choose(0, 200)
            return Insert(e, p)
        })
        (2, gen {
            let! p = Gen.choose(0, 200)
            return RemoveAt p
        })
        (2, gen {
            let! e = Gen.choose(0, 9)
            let! p = Gen.choose(0, 200)
            return UpdateAt(e, p)
        })
        (1, gen {
            let! e = Gen.choose(0, 9)
            let! v = Gen.choose(0, 99)
            return ListChange.SetValue(e, v)
        })
    ]

let crossOpGen: Gen<CrossOp> =
    Gen.frequency [
        (2, gen {
            let! k = Gen.choose(0, 9)
            let! v = Gen.choose(0, 99)
            return EntityUpsert(k, v)
        })
        (1, gen {
            let! k = Gen.choose(0, 9)
            return EntityRemove k
        })
        (2, gen {
            let! k = Gen.choose(0, 9)
            let! v = Gen.choose(0, 99)
            return LookupUpsert(k, v)
        })
        (1, gen {
            let! k = Gen.choose(0, 9)
            return LookupRemove k
        })
    ]

let joinOpGen: Gen<JoinOp> =
    Gen.frequency [
        (2, Gen.map LeftEdit mapOpGen)
        (2, Gen.map RightEdit mapOpGen)
    ]

let externalOpGen: Gen<ExternalOp> =
    Gen.frequency [
        (3, gen {
            let! v = Gen.choose(0, 99)
            return Replace v
        })
        (1, Gen.constant Invalidate)
    ]

let setScenarioGen: Gen<SetScenario> =
    gen {
        let! initial = Gen.listOf pairGen
        let! ops = Gen.listOf setOpGen
        return { initial = dedupPairs initial; ops = ops }
    }

let mapScenarioGen: Gen<MapScenario> =
    gen {
        let! initial = Gen.listOf pairGen
        let! ops = Gen.listOf mapOpGen
        return { initial = dedupPairs initial; ops = ops }
    }

let listScenarioGen: Gen<ListScenario> =
    gen {
        let! initial = Gen.listOf pairGen
        let! ops = Gen.listOf listChangeGen
        return { initial = initial; ops = ops }
    }

let crossScenarioGen: Gen<CrossScenario> =
    gen {
        let! initialEntities = Gen.listOf pairGen
        let! initialLookups = Gen.listOf pairGen
        let! ops = Gen.listOf crossOpGen

        return
            { initialEntities = dedupPairs initialEntities
              initialLookups = dedupPairs initialLookups
              ops = ops }
    }

let joinScenarioGen: Gen<JoinScenario> =
    gen {
        let! initialA = Gen.listOf pairGen
        let! initialB = Gen.listOf pairGen
        let! ops = Gen.listOf joinOpGen
        return { initialA = dedupPairs initialA; initialB = dedupPairs initialB; ops = ops }
    }

let externalScenarioGen: Gen<ExternalScenario> =
    gen {
        let! ops = Gen.listOf externalOpGen
        return { ops = ops }
    }

/// The registered arbitraries: FsCheck resolves the property parameters by type.
type ScenarioArbs =
    static member SetScenario() : Arbitrary<SetScenario> = Arb.fromGen setScenarioGen
    static member MapScenario() : Arbitrary<MapScenario> = Arb.fromGen mapScenarioGen
    static member ListScenario() : Arbitrary<ListScenario> = Arb.fromGen listScenarioGen
    static member CrossScenario() : Arbitrary<CrossScenario> = Arb.fromGen crossScenarioGen
    static member JoinScenario() : Arbitrary<JoinScenario> = Arb.fromGen joinScenarioGen
    static member ExternalScenario() : Arbitrary<ExternalScenario> = Arb.fromGen externalScenarioGen
    static member SetOpList() : Arbitrary<SetOp list> = Arb.fromGen (Gen.listOf setOpGen)
    static member MapOpList() : Arbitrary<MapOp list> = Arb.fromGen (Gen.listOf mapOpGen)
    static member ListChangeList() : Arbitrary<ListChange list> = Arb.fromGen (Gen.listOf listChangeGen)

let private scenarioConfig = Config.QuickThrowOnFailure.WithArbitrary([| typeof<ScenarioArbs> |])

// =============================================================================
// Reference-impl model: ASet.mapA (MAPA-DESIGN §12).
//
// The scenario generator provides a random initial state and a weighted op
// sequence; the model tracks element -> value plus a value refcount (the
// output set dedups); the library output is compared to the model after
// EVERY op.
// =============================================================================

[<Fact>]
let ``ASet mapA matches the reference model`` () =
    let prop (sc: SetScenario) =
        let source = CSet.empty<int>
        let values = Dictionary<int, cval<int>>()

        for (e, v) in sc.initial do
            CSet.add e source
            values[e] <- CVal.create v

        let mapped = ASet.mapA (fun v -> CVal.value values[v]) (CSet.value source)
        // Model: element -> current value; value -> occurrence count; the output.
        let elementValue = Dictionary<int, int>()
        let valueRefs = Dictionary<int, int>()
        let model = HashSet<int>()

        for (e, v) in sc.initial do
            elementValue[e] <- v

            match valueRefs.TryGetValue v with
            | true, r -> valueRefs[v] <- r + 1
            | false, _ ->
                valueRefs[v] <- 1
                model.Add v |> ignore

        let apply (op: SetOp) =
            match op with
            | Add(element, value) -> // Add the element with a fresh aval holding the value.
                CSet.add element source

                if not (elementValue.ContainsKey element) then
                    values[element] <- CVal.create value
                    elementValue[element] <- value

                    match valueRefs.TryGetValue value with
                    | true, r -> valueRefs[value] <- r + 1
                    | false, _ ->
                        valueRefs[value] <- 1
                        model.Add value |> ignore
            | SetOp.Remove element -> // Remove the element.
                CSet.remove element source

                if elementValue.ContainsKey element then
                    let v = elementValue[element]
                    elementValue.Remove element |> ignore
                    values.Remove element |> ignore
                    let r = valueRefs[v] - 1

                    if r = 0 then
                        valueRefs.Remove v |> ignore
                        model.Remove v |> ignore
                    else
                        valueRefs[v] <- r
            | SetOp.SetValue(element, value) -> // Set the element's aval.
                if elementValue.ContainsKey element then
                    let old = elementValue[element]

                    if old <> value then
                        let r = valueRefs[old] - 1

                        if r = 0 then
                            valueRefs.Remove old |> ignore
                            model.Remove old |> ignore
                        else
                            valueRefs[old] <- r

                        CVal.set value (values[element])
                        elementValue[element] <- value

                        match valueRefs.TryGetValue value with
                        | true, r2 -> valueRefs[value] <- r2 + 1
                        | false, _ ->
                            valueRefs[value] <- 1
                            model.Add value |> ignore

        let mutable ok = true

        for op in sc.ops do
            apply op

            let actual = Set.ofSeq (ASet.toSet mapped)
            let expected = Set.ofSeq model

            if actual <> expected then
                ok <- false

        ok

    Check.One (scenarioConfig, prop)

// =============================================================================
// Reference-impl model: AList.mapA (MAPA-DESIGN §12).
//
// The scenario generator provides a random initial state and a weighted op
// sequence; the model tracks the element list (the output is a list:
// duplicates survive) plus element -> value; a re-inserted element reuses
// its aval (one aval per element).
// =============================================================================

[<Fact>]
let ``AList mapA matches the reference model`` () =
    let prop (sc: ListScenario) =
        let source = CList.empty<int>
        let values = Dictionary<int, cval<int>>()

        for (e, v) in sc.initial do
            CList.append e source
            values[e] <- CVal.create v

        let mapped = AList.mapA (fun v -> CVal.value values[v]) (CList.value source)
        // Model: the element list in order, and the element -> value map.
        let elements = ResizeArray<int>(List.map fst sc.initial)
        let elementValue = Dictionary<int, int>()

        for (e, v) in sc.initial do
            elementValue[e] <- v

        let apply (op: ListChange) =
            match op with
            | Insert(element, payload) ->
                let position = payload % (elements.Count + 1)

                // Insert the element at the position; a fresh element gets a fresh aval.
                if not (elementValue.ContainsKey element) then
                    values[element] <- CVal.create 0
                    elementValue[element] <- 0

                CList.insertAt position element source
                elements.Insert(position, element)
            | RemoveAt payload ->
                let position = payload % (elements.Count + 1)

                if elements.Count > 0 && position < elements.Count then
                    CList.removeAt position source
                    elements.RemoveAt position
            | UpdateAt(element, payload) ->
                let position = payload % (elements.Count + 1)

                if elements.Count > 0 && position < elements.Count then
                    if not (elementValue.ContainsKey element) then
                        values[element] <- CVal.create 0
                        elementValue[element] <- 0

                    CList.updateAt position element source
                    elements[position] <- element
            | ListChange.SetValue(element, value) -> // Set the element's aval.
                if elementValue.ContainsKey element then
                    CVal.set value (values[element])
                    elementValue[element] <- value

        let mutable ok = true

        for op in sc.ops do
            apply op

            let actual = AList.toArray mapped
            let expected = Array.ofSeq (Seq.map (fun e -> elementValue[e]) elements)

            if actual <> expected then
                ok <- false

        ok

    Check.One (scenarioConfig, prop)

// =============================================================================
// Reference-impl model: AMap.mapA.
//
// The scenario generator provides a random initial state and a weighted op
// sequence; a fresh key gets a fresh aval; the model tracks key -> value.
// =============================================================================

[<Fact>]
let ``AMap mapA matches the reference model`` () =
    let prop (sc: MapScenario) =
        let source = CMap.empty<int, int>
        let values = Dictionary<int, cval<int>>()

        for (k, v) in sc.initial do
            CMap.addOrUpdate k v source
            values[k] <- CVal.create v

        let mapped = AMap.mapA (fun k _ -> CVal.value values[k]) (CMap.value source)
        let model = Dictionary<int, int>()

        for (k, v) in sc.initial do
            model[k] <- v

        let apply (op: MapOp) =
            match op with
            | Upsert(key, value) -> // AddOrUpdate the key. The output follows the key's aval:
                // a fresh key gets a fresh aval, an existing key keeps it
                // (the mapping ignores the map value here).
                CMap.addOrUpdate key value source

                if not (model.ContainsKey key) then
                    values[key] <- CVal.create value
                    model[key] <- value
            | MapOp.Remove key -> // Remove the key.
                CMap.remove key source

                if model.ContainsKey key then
                    model.Remove key |> ignore
            | MapOp.SetValue(key, value) -> // Set the key's aval.
                if model.ContainsKey key then
                    CVal.set value (values[key])
                    model[key] <- value

        let mutable ok = true

        for op in sc.ops do
            apply op

            let actual = AMap.toMap mapped
            let expected = Map.ofSeq (seq { for KeyValue(k, v) in model -> k, v })

            if actual <> expected then
                ok <- false

        ok

    Check.One (scenarioConfig, prop)

// =============================================================================
// Reference-impl model: ASet.filterA and ASet.chooseA.
//
// Ops encoded as an int list: kind = op % 3 (0 Add, 1 Remove, 2 Set flag);
// element = rest % 10; the flag payload = (rest / 10) % 2. The filter model
// is the membership AND the flag; the choose model maps the element to
// ValueSome value when the flag holds, else ValueNone.
// =============================================================================

[<Fact>]
let ``ASet filterA matches the reference model`` () =
    let prop (sc: SetScenario) =
        let source = CSet.empty<int>
        let flags = Dictionary<int, cval<bool>>()

        for (e, v) in sc.initial do
            CSet.add e source
            flags[e] <- CVal.create (v % 2 = 0)

        let filtered = ASet.filterA (fun v -> CVal.value flags[v]) (CSet.value source)
        let present = HashSet<int>(List.map fst sc.initial)
        let flagValue = Dictionary<int, bool>()

        for (e, v) in sc.initial do
            flagValue[e] <- v % 2 = 0

        let apply (op: SetOp) =
            match op with
            | Add(element, value) -> // Add the element with a fresh flag aval.
                let flag = value % 2 = 0
                CSet.add element source

                if present.Add element then
                    flags[element] <- CVal.create flag
                    flagValue[element] <- flag
            | SetOp.Remove element -> // Remove the element.
                CSet.remove element source
                present.Remove element |> ignore
            | SetOp.SetValue(element, value) -> // Set the element's flag aval.
                let flag = value % 2 = 0

                if present.Contains element then
                    CVal.set flag (flags[element])
                    flagValue[element] <- flag

        let mutable ok = true

        for op in sc.ops do
            apply op

            let actual = Set.ofSeq (ASet.toSet filtered)

            let expected =
                Set.ofSeq (
                    seq {
                        for e in present do
                            if flagValue[e] then
                                e
                    }
                )

            if actual <> expected then
                ok <- false

        ok

    Check.One (scenarioConfig, prop)

[<Fact>]
let ``ASet chooseA matches the reference model`` () =
    let prop (sc: SetScenario) =
        let source = CSet.empty<int>
        let flags = Dictionary<int, cval<bool>>()

        for (e, v) in sc.initial do
            CSet.add e source
            flags[e] <- CVal.create (v % 2 = 0)

        let chosen =
            ASet.chooseA
                (fun v -> AVal.map (fun f -> if f then Some(v * 10) else None) (CVal.value flags[v]))
                (CSet.value source)

        let present = HashSet<int>(List.map fst sc.initial)
        let flagValue = Dictionary<int, bool>()

        for (e, v) in sc.initial do
            flagValue[e] <- v % 2 = 0

        let apply (op: SetOp) =
            match op with
            | Add(element, value) -> // Add the element with a fresh flag aval.
                let flag = value % 2 = 0
                CSet.add element source

                if present.Add element then
                    flags[element] <- CVal.create flag
                    flagValue[element] <- flag
            | SetOp.Remove element -> // Remove the element.
                CSet.remove element source
                present.Remove element |> ignore
            | SetOp.SetValue(element, value) -> // Set the element's flag aval.
                let flag = value % 2 = 0

                if present.Contains element then
                    CVal.set flag (flags[element])
                    flagValue[element] <- flag

        let mutable ok = true

        for op in sc.ops do
            apply op

            let actual = Set.ofSeq (ASet.toSet chosen)

            let expected =
                Set.ofSeq (
                    seq {
                        for e in present do
                            if flagValue[e] then
                                e * 10
                    }
                )

            if actual <> expected then
                ok <- false

        ok

    Check.One (scenarioConfig, prop)

// =============================================================================
// Reference-impl model: ASet.ofExternal (MAPA-DESIGN §1.1).
//
// Ops encoded as an int list: kind = op % 2 (0 = replace the external
// snapshot, 1 = invalidate). The model: the library re-reads the snapshot
// only when invalidated, on the next read; the expected content is the
// snapshot when the model is dirty, else the last read snapshot.
// =============================================================================

[<Fact>]
let ``ASet ofExternal matches the reference model`` () =
    let prop (sc: ExternalScenario) =
        let mutable snapshot = HashSet<int>()
        let ext, invalidate = ASet.ofExternal (fun () -> snapshot :> IReadOnlySet<int>)
        // Model: whether an invalidate is pending, and the last read snapshot.
        // The first read always re-reads (the node materializes initially).
        let mutable dirty = true
        let mutable lastSeen = HashSet<int>()

        let apply (op: ExternalOp) =
            match op with
            | Replace value -> // Replace the external snapshot with a single element.
                snapshot <- HashSet<int>([ value ])

                if value % 4 = 0 then
                    snapshot.Add (value / 2) |> ignore // sometimes two elements
            | Invalidate -> // Invalidate: the next read re-reads the snapshot.
                invalidate ()
                dirty <- true

            // Read after every op; the expected is the snapshot iff dirty.
            let expected = if dirty then snapshot else lastSeen
            let actual = ASet.toSet ext

            if Set.ofSeq actual <> Set.ofSeq expected then
                false
            else
                dirty <- false
                lastSeen <- HashSet<int>(expected)
                true

        let mutable ok = true

        for op in sc.ops do
            if not (apply op) then
                ok <- false

        ok

    Check.One (scenarioConfig, prop)

// =============================================================================
// Kipo usage shapes (E:\Kipo Pomo.Core\Projections.fs):
//
// 1. The derived-stats join: AMap.choose2V over two maps + AMap.mapA.
// 2. The entity-scenario context: AMap.mapA whose mapping does a cross-map
//    lookup (world.Scenarios |> AMap.tryFind), then AMap.choose drops the
//    Nones.
// 3. The live-entities projection: AMap.filter + AMap.keys.
// 4. The physics cache: force the world maps, then compute per-scenario
//    snapshots (the RefreshAllCaches shape).
// 5. The nearby-entities query: cell-based radius query over the forced
//    snapshot vs the brute-force distance filter.
// 6. The combat statuses: AList.choose over the effect list.
// =============================================================================

[<Fact>]
let ``AMap join choose2V with mapA matches the model`` () =
    let prop (sc: JoinScenario) =
        let baseStats = CMap.empty<int, int>
        let effects = CMap.empty<int, int>

        for (k, v) in sc.initialA do
            CMap.addOrUpdate k v baseStats

        for (k, v) in sc.initialB do
            CMap.addOrUpdate k v effects

        let derived =
            (CMap.value baseStats, CMap.value effects)
            ||> AMap.choose2V (fun _ av bv ->
                match struct (av, bv) with
                | ValueSome x, ValueSome y -> ValueSome(struct (x, y))
                | _ -> ValueNone)
            |> AMap.mapA (fun _ struct (x, y) -> AVal.constant (x + y))

        let modelA = Dictionary<int, int>()
        let modelB = Dictionary<int, int>()

        for (k, v) in sc.initialA do
            modelA[k] <- v

        for (k, v) in sc.initialB do
            modelB[k] <- v

        let apply (op: JoinOp) =
            match op with
            | LeftEdit(Upsert(key, value)) -> // Upsert into the base-stats map.
                CMap.addOrUpdate key value baseStats
                modelA[key] <- value
            | LeftEdit(MapOp.Remove key) -> // Remove from the base-stats map.
                CMap.remove key baseStats
                modelA.Remove key |> ignore
            | LeftEdit(MapOp.SetValue _) -> ()
            | RightEdit(Upsert(key, value)) -> // Upsert into the effects map.
                CMap.addOrUpdate key value effects
                modelB[key] <- value
            | RightEdit(MapOp.Remove key) -> // Remove from the effects map.
                CMap.remove key effects
                modelB.Remove key |> ignore
            | RightEdit(MapOp.SetValue _) -> ()

        let mutable ok = true

        for op in sc.ops do
            apply op

            let actual = AMap.toMap derived

            let expected =
                Map.ofSeq (
                    seq {
                        for KeyValue(k, a) in modelA do
                            match modelB.TryGetValue k with
                            | true, b -> k, a + b
                            | false, _ -> ()
                    }
                )

            if actual <> expected then
                ok <- false

        ok

    Check.One (scenarioConfig, prop)

[<Fact>]
let ``AMap mapA with cross-map lookup matches the model`` () =
    let prop (sc: CrossScenario) =
        let entities = CMap.empty<int, int> // entity -> lookup key
        let lookups = CMap.empty<int, int> // key -> value

        for (k, v) in sc.initialEntities do
            CMap.addOrUpdate k v entities

        for (k, v) in sc.initialLookups do
            CMap.addOrUpdate k v lookups

        let contexts =
            entities
            |> AMap.mapA (fun _ key -> AMap.tryFind key (CMap.value lookups))
            |> AMap.choose (fun _ v -> ValueOption.toOption v)

        let modelEntities = Dictionary<int, int>()
        let modelLookups = Dictionary<int, int>()

        for (k, v) in sc.initialEntities do
            modelEntities[k] <- v

        for (k, v) in sc.initialLookups do
            modelLookups[k] <- v

        let apply (op: CrossOp) =
            match op with
            | EntityUpsert(key, value) -> // Upsert an entity with its lookup key.
                CMap.addOrUpdate key value entities
                modelEntities[key] <- value
            | EntityRemove key -> // Remove the entity.
                CMap.remove key entities
                modelEntities.Remove key |> ignore
            | LookupUpsert(key, value) -> // Upsert a lookup entry.
                CMap.addOrUpdate key value lookups
                modelLookups[key] <- value
            | LookupRemove key -> // Remove the lookup entry.
                CMap.remove key lookups
                modelLookups.Remove key |> ignore

        let mutable ok = true

        for op in sc.ops do
            apply op

            let actual = AMap.toMap contexts

            let expected =
                Map.ofSeq (
                    seq {
                        for KeyValue(id, lookupKey) in modelEntities do
                            match modelLookups.TryGetValue lookupKey with
                            | true, v -> id, v
                            | false, _ -> ()
                    }
                )

            if actual <> expected then
                ok <- false

        ok

    Check.One (scenarioConfig, prop)

[<Fact>]
let ``ASet mapA with cross-map lookup matches the model`` () =
    let prop (sc: CrossScenario) =
        let entities = CSet.empty<int> // entity ids
        let lookups = CMap.empty<int, int> // key -> value

        for (k, _) in sc.initialEntities do
            CSet.add k entities

        for (k, v) in sc.initialLookups do
            CMap.addOrUpdate k v lookups

        let contexts =
            entities
            |> ASet.mapA (fun id -> AMap.tryFind id (CMap.value lookups))
            |> ASet.choose (fun v -> ValueOption.toOption v)

        let modelEntities = HashSet<int>(List.map fst sc.initialEntities)
        let modelLookups = Dictionary<int, int>()

        for (k, v) in sc.initialLookups do
            modelLookups[k] <- v

        let apply (op: CrossOp) =
            match op with
            | EntityUpsert(key, _) -> // Add an entity id.
                CSet.add key entities
                modelEntities.Add key |> ignore
            | EntityRemove key -> // Remove the entity.
                CSet.remove key entities
                modelEntities.Remove key |> ignore
            | LookupUpsert(key, value) -> // Upsert a lookup entry.
                CMap.addOrUpdate key value lookups
                modelLookups[key] <- value
            | LookupRemove key -> // Remove the lookup entry.
                CMap.remove key lookups
                modelLookups.Remove key |> ignore

        let mutable ok = true

        for op in sc.ops do
            apply op

            let actual = Set.ofSeq (ASet.toSet contexts)

            let expected =
                Set.ofSeq (
                    seq {
                        for id in modelEntities do
                            match modelLookups.TryGetValue id with
                            | true, v -> v
                            | false, _ -> ()
                    }
                )

            if actual <> expected then
                ok <- false

        ok

    Check.One (scenarioConfig, prop)

[<Fact>]
let ``AList mapA with cross-map lookup matches the model`` () =
    let prop (sc: CrossScenario) =
        let entities = CList.empty<int> // entity ids in order
        let lookups = CMap.empty<int, int> // key -> value

        for (k, _) in sc.initialEntities do
            CList.append k entities

        for (k, v) in sc.initialLookups do
            CMap.addOrUpdate k v lookups

        let contexts =
            entities
            |> AList.mapA (fun id -> AMap.tryFind id (CMap.value lookups))
            |> AList.choose (fun v -> ValueOption.toOption v)

        let modelEntities = ResizeArray<int>(List.map fst sc.initialEntities)
        let modelLookups = Dictionary<int, int>()

        for (k, v) in sc.initialLookups do
            modelLookups[k] <- v

        let apply (op: CrossOp) =
            match op with
            | EntityUpsert(key, _) -> // Insert the entity id at a derived position.
                let position = key % (modelEntities.Count + 1)
                CList.insertAt position key entities
                modelEntities.Insert(position, key)
            | EntityRemove key -> // Remove the first occurrence of the entity id.
                let idx = modelEntities.IndexOf key

                if idx >= 0 then
                    CList.removeAt idx entities
                    modelEntities.RemoveAt idx
            | LookupUpsert(key, value) -> // Upsert a lookup entry.
                CMap.addOrUpdate key value lookups
                modelLookups[key] <- value
            | LookupRemove key -> // Remove the lookup entry.
                CMap.remove key lookups
                modelLookups.Remove key |> ignore

        let mutable ok = true

        for op in sc.ops do
            apply op

            let actual = AList.toArray contexts

            let expected =
                Array.ofSeq (
                    seq {
                        for id in modelEntities do
                            match modelLookups.TryGetValue id with
                            | true, v -> v
                            | false, _ -> ()
                    }
                )

            if actual <> expected then
                ok <- false

        ok

    Check.One (scenarioConfig, prop)

[<Fact>]
let ``AMap filter with keys matches the model`` () =
    let prop (ops: int list) =
        let resources = CMap.empty<int, bool> // entity -> alive flag

        let live = resources |> AMap.filter (fun _ alive -> alive) |> AMap.keys

        let model = Dictionary<int, bool>()

        let apply (op: int) =
            let kind = op % 2
            let rest = op / 2
            let key = rest % 10
            let alive = (rest / 10) % 2 = 0

            match kind with
            | 0 -> // Upsert the entity's alive flag.
                CMap.addOrUpdate key alive resources
                model[key] <- alive
            | _ -> // Remove the entity.
                CMap.remove key resources
                model.Remove key |> ignore

        let mutable ok = true

        for op in ops do
            apply op

            let actual = ASet.toSet live

            let expected =
                Set.ofSeq (
                    seq {
                        for KeyValue(k, alive) in model do
                            if alive then
                                k
                    }
                )

            if Set.ofSeq actual <> expected then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``physics cache refresh matches a fresh computation`` () =
    let prop (ops: int list) =
        let positions = CMap.empty<int, float>
        let velocities = CMap.empty<int, float>
        let inScenario = CSet.empty<int>
        let dt = 0.5

        let modelPos = Dictionary<int, float>()
        let modelVel = Dictionary<int, float>()
        let modelMembers = HashSet<int>()

        // The cache shape: force the world maps, then compute the snapshot.
        let refresh () =
            let pos = AMap.force (CMap.value positions)
            let vel = AMap.force (CMap.value velocities)
            let members = ASet.force (CSet.value inScenario)
            let computed = Dictionary<int, float>()

            for id in members do
                let mutable p = 0.0
                pos.TryGetValue(id, &p) |> ignore
                let mutable v = 0.0

                if vel.TryGetValue(id, &v) then
                    computed[id] <- p + v * dt
                else
                    computed[id] <- p

            computed

        // The model: the same computation from the model state.
        let model () =
            let computed = Dictionary<int, float>()

            for id in modelMembers do
                let mutable p = 0.0
                modelPos.TryGetValue(id, &p) |> ignore
                let mutable v = 0.0

                if modelVel.TryGetValue(id, &v) then
                    computed[id] <- p + v * dt
                else
                    computed[id] <- p

            computed

        let apply (op: int) =
            let kind = op % 5
            let rest = op / 5
            let id = rest % 10
            let value = float ((rest / 10) % 100)

            match kind with
            | 0 -> // Upsert the position and mark the entity in the scenario.
                CMap.addOrUpdate id value positions
                modelPos[id] <- value
                CSet.add id inScenario
                modelMembers.Add id |> ignore
            | 1 -> // Upsert the velocity.
                CMap.addOrUpdate id value velocities
                modelVel[id] <- value
            | 2 -> // Remove the entity from the scenario.
                CSet.remove id inScenario
                modelMembers.Remove id |> ignore
            | 3 -> // Remove the position (the entity keeps its velocity).
                CMap.remove id positions
                modelPos.Remove id |> ignore
            | _ -> // Remove the velocity.
                CMap.remove id velocities
                modelVel.Remove id |> ignore

        let mutable ok = true

        for op in ops do
            apply op

            let actual = refresh ()
            let expected = model ()

            if actual.Count <> expected.Count then
                ok <- false
            else
                let mutable mismatch = false

                for KeyValue(id, v) in expected do
                    let mutable a = 0.0

                    if not (actual.TryGetValue(id, &a)) || a <> v then
                        mismatch <- true

                if mismatch then
                    ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``physics cache snapshot agrees with the adaptive derivation`` () =
    let prop (ops: int list) =
        let positions = CMap.empty<int, float>
        let velocities = CMap.empty<int, float>
        let inScenario = CSet.empty<int>
        let dt = 0.5

        // The original adaptive cache (before the snapshot switch): a derived
        // per-member position computed from the adaptive world.
        let derived =
            inScenario
            |> ASet.mapA (fun id ->
                AVal.map2
                    (fun p v -> struct (id, p + v * dt))
                    (AMap.tryFind id (CMap.value positions)
                     |> AVal.map (ValueOption.defaultValue 0.0))
                    (AMap.tryFind id (CMap.value velocities)
                     |> AVal.map (ValueOption.defaultValue 0.0)))

        let modelPos = Dictionary<int, float>()
        let modelVel = Dictionary<int, float>()
        let modelMembers = HashSet<int>()

        // The snapshot cache shape: force the world maps, then compute.
        let snapshot () =
            let pos = AMap.force (CMap.value positions)
            let vel = AMap.force (CMap.value velocities)
            let members = ASet.force (CSet.value inScenario)
            let computed = Dictionary<int, float>()

            for id in members do
                let mutable p = 0.0
                pos.TryGetValue(id, &p) |> ignore
                let mutable v = 0.0

                if vel.TryGetValue(id, &v) then
                    computed[id] <- p + v * dt
                else
                    computed[id] <- p

            computed

        // The model: the same computation from the model state.
        let model () =
            let computed = Dictionary<int, float>()

            for id in modelMembers do
                let mutable p = 0.0
                modelPos.TryGetValue(id, &p) |> ignore
                let mutable v = 0.0

                if modelVel.TryGetValue(id, &v) then
                    computed[id] <- p + v * dt
                else
                    computed[id] <- p

            computed

        let apply (op: int) =
            let kind = op % 5
            let rest = op / 5
            let id = rest % 10
            let value = float ((rest / 10) % 100)

            match kind with
            | 0 -> // Upsert the position and mark the entity in the scenario.
                CMap.addOrUpdate id value positions
                modelPos[id] <- value
                CSet.add id inScenario
                modelMembers.Add id |> ignore
            | 1 -> // Upsert the velocity.
                CMap.addOrUpdate id value velocities
                modelVel[id] <- value
            | 2 -> // Remove the entity from the scenario.
                CSet.remove id inScenario
                modelMembers.Remove id |> ignore
            | 3 -> // Remove the position (the entity keeps its velocity).
                CMap.remove id positions
                modelPos.Remove id |> ignore
            | _ -> // Remove the velocity.
                CMap.remove id velocities
                modelVel.Remove id |> ignore

        let mutable ok = true

        for op in ops do
            apply op

            // The adaptive derivation, forced, must equal the snapshot and
            // the model.
            let adaptive =
                ASet.force derived |> Seq.map (fun struct (id, v) -> id, v) |> Map.ofSeq

            let snap = snapshot ()
            let expected = model ()

            if Map.count adaptive <> expected.Count then
                ok <- false
            else
                let mutable mismatch = false

                for KeyValue(id, v) in expected do
                    let mutable a = 0.0
                    let mutable s = 0.0

                    if
                        (not (Map.tryFind id adaptive |> Option.exists (fun x -> x = v)))
                        || (not (snap.TryGetValue(id, &s)) || s <> v)
                    then
                        mismatch <- true

                if mismatch then
                    ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``spatial radius query matches the brute force`` () =
    let prop (ops: int list) =
        let positions = CMap.empty<int, struct (float * float)>
        let live = CSet.empty<int>
        let cellSize = 4.0
        let radius = 5.0
        let center = struct (10.0, 10.0)

        let modelPos = Dictionary<int, struct (float * float)>()
        let modelLive = HashSet<int>()

        // The Kipo shape: cells in radius -> potential targets -> distance
        // filter over the forced snapshot.
        let query () =
            let pos = AMap.force (CMap.value positions)
            let members = ASet.force (CSet.value live)
            let cellRadius = int (radius / cellSize) + 1
            let struct (centerX, centerY) = center
            let struct (ccx, ccy) = struct (int (centerX / cellSize), int (centerY / cellSize))
            let results = ResizeArray<int>()

            for dx = -cellRadius to cellRadius do
                for dy = -cellRadius to cellRadius do
                    let cell = struct (ccx + dx, ccy + dy)
                    let struct (cellX, cellY) = cell
                    let cellOrigin = struct (float cellX * cellSize, float cellY * cellSize)

                    // Only cells whose origin is within radius + cellSize count.
                    let struct (ox, oy) = cellOrigin
                    let dxp = ox - centerX
                    let dyp = oy - centerY

                    if dxp * dxp + dyp * dyp <= (radius + cellSize) * (radius + cellSize) then
                        for id in members do
                            match pos.TryGetValue id with
                            | true, p ->
                                let struct (px, py) = p
                                let ddx = px - centerX
                                let ddy = py - centerY

                                if ddx * ddx + ddy * ddy <= radius * radius then
                                    results.Add id
                            | false, _ -> ()

            results.ToArray() |> Array.sort |> Set.ofArray

        let model () =
            let struct (centerX, centerY) = center

            seq {
                for id in modelLive do
                    match modelPos.TryGetValue id with
                    | true, p ->
                        let struct (px, py) = p
                        let ddx = px - centerX
                        let ddy = py - centerY

                        if ddx * ddx + ddy * ddy <= radius * radius then
                            id
                    | false, _ -> ()
            }
            |> Set.ofSeq

        let apply (op: int) =
            let kind = op % 3
            let rest = op / 3
            let id = rest % 20
            let x = float ((rest / 20) % 20)
            let y = float ((rest / 400) % 20)

            match kind with
            | 0 -> // Upsert the position and mark the entity live.
                CMap.addOrUpdate id (struct (x, y)) positions
                modelPos[id] <- struct (x, y)
                CSet.add id live
                modelLive.Add id |> ignore
            | 1 -> // Remove the entity.
                CMap.remove id positions
                modelPos.Remove id |> ignore
                CSet.remove id live
                modelLive.Remove id |> ignore
            | _ -> // Toggle liveness only.
                CSet.add id live
                modelLive.Add id |> ignore

        let mutable ok = true

        for op in ops do
            apply op

            if query () <> model () then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``AList choose matches the combat-status model`` () =
    let prop (ops: int list) =
        let effects = CList.empty<int> // effect kind

        // Kipo: stun/silence map to a status, everything else is dropped.
        let effectKindToStatus (kind: int) =
            if kind % 4 = 0 then Some(kind * 10) else None

        let statuses = effects |> AList.choose effectKindToStatus
        let model = ResizeArray<int>()

        let apply (op: int) =
            let kind = op % 3
            let rest = op / 3
            let element = rest % 10

            let position =
                // F#'s % is signed: normalize to [0, Count + 1).
                let p = (rest / 10) % (model.Count + 1)
                if p < 0 then p + (model.Count + 1) else p

            match kind with
            | 0 -> // Insert an effect at the position.
                CList.insertAt position element effects
                model.Insert(position, element)
            | 1 -> // Remove the effect at the position.
                if model.Count > 0 && position < model.Count then
                    CList.removeAt position effects
                    model.RemoveAt position
            | _ -> // Update the effect at the position.
                if model.Count > 0 && position < model.Count then
                    CList.updateAt position element effects
                    model[position] <- element

        let mutable ok = true

        for op in ops do
            apply op

            let actual = AList.toArray statuses
            let expected = Array.ofSeq (Seq.choose effectKindToStatus model)

            if actual <> expected then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

// =============================================================================
// Gap coverage: transactions, the external nodes, the AList positional and
// reduction families, bind/concat, mapiA positions, and physics removals.
// =============================================================================

[<Fact>]
let ``Transaction.run defers application until commit`` () =
    let prop (ops: int list) =
        let m = CMap.empty<int, int>
        let model = Dictionary<int, int>()

        let apply (op: int) =
            let kind = op % 2
            let rest = op / 2
            let key = rest % 10
            let value = (rest / 10) % 100
            let key2 = (key + 5) % 10

            // The state visible inside the transaction is the PRE state.
            let expectedPre = Map.ofSeq (seq { for KeyValue(k, v) in model -> k, v })

            let actualPre =
                Transaction.run (fun () ->
                    match kind with
                    | 0 -> // Batch: two upserts.
                        CMap.addOrUpdate key value m
                        CMap.addOrUpdate key2 (value + 1) m
                    | _ -> // Batch: an upsert and a remove.
                        CMap.addOrUpdate key value m
                        CMap.remove key2 m

                    AMap.toMap (CMap.value m))

            match kind with
            | 0 ->
                model[key] <- value
                model[key2] <- value + 1
            | _ ->
                model[key] <- value
                model.Remove key2 |> ignore

            let expectedPost = Map.ofSeq (seq { for KeyValue(k, v) in model -> k, v })
            let actualPost = AMap.toMap (CMap.value m)
            actualPre = expectedPre && actualPost = expectedPost

        let mutable ok = true

        for op in ops do
            if not (apply op) then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``AMap ofExternal matches the reference model`` () =
    let prop (sc: ExternalScenario) =
        let mutable snapshot = Map.empty<int, int>

        let ext, invalidate =
            AMap.ofExternal (fun () -> snapshot :> IReadOnlyDictionary<int, int>)

        // Model: whether an invalidate is pending, and the last read snapshot.
        let mutable dirty = true
        let mutable lastSeen = Map.empty<int, int>

        let apply (op: ExternalOp) =
            match op with
            | Replace value -> // Replace the external snapshot.
                snapshot <- Map.ofList [ value % 10, value ]

                if value % 4 = 0 then
                    snapshot <- Map.add ((value + 1) % 10) (value + 1) snapshot
            | Invalidate -> // Invalidate: the next read re-reads the snapshot.
                invalidate ()
                dirty <- true

            let expected = if dirty then snapshot else lastSeen
            let actual = AMap.toMap ext

            if actual <> expected then
                false
            else
                dirty <- false
                lastSeen <- expected
                true

        let mutable ok = true

        for op in sc.ops do
            if not (apply op) then
                ok <- false

        ok

    Check.One (scenarioConfig, prop)

[<Fact>]
let ``AList ofExternal matches the reference model`` () =
    let prop (sc: ExternalScenario) =
        let mutable snapshot: int list = []

        let ext, invalidate =
            AList.ofExternal (fun () -> snapshot :> IReadOnlyList<int>)

        // Model: whether an invalidate is pending, and the last read snapshot.
        let mutable dirty = true
        let mutable lastSeen: int[] = [||]

        let apply (op: ExternalOp) =
            match op with
            | Replace value -> // Replace the external snapshot.
                snapshot <- [ value; value * 2 ]
            | Invalidate -> // Invalidate: the next read re-reads the snapshot.
                invalidate ()
                dirty <- true

            let expected = if dirty then List.toArray snapshot else lastSeen
            let actual = AList.force ext

            if actual <> expected then
                false
            else
                dirty <- false
                lastSeen <- expected
                true

        let mutable ok = true

        for op in sc.ops do
            if not (apply op) then
                ok <- false

        ok

    Check.One (scenarioConfig, prop)

[<Fact>]
let ``AList take matches the model`` () =
    let prop (ops: int list) =
        let l = CList.empty<int>
        let model = ResizeArray<int>()
        let count = List.length ops % 5
        let taken = AList.take count (CList.value l)

        let apply (op: int) =
            let kind = op % 3
            let rest = op / 3
            let element = rest % 10

            let position =
                let p = (rest / 10) % (model.Count + 1)
                if p < 0 then p + (model.Count + 1) else p

            match kind with
            | 0 ->
                CList.insertAt position element l
                model.Insert(position, element)
            | 1 ->
                if model.Count > 0 && position < model.Count then
                    CList.removeAt position l
                    model.RemoveAt position
            | _ ->
                if model.Count > 0 && position < model.Count then
                    CList.updateAt position element l
                    model[position] <- element

            let expected = Array.ofSeq (Seq.truncate count model)
            AList.force taken = expected

        let mutable ok = true

        for op in ops do
            if not (apply op) then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``AList skip matches the model`` () =
    let prop (ops: int list) =
        let l = CList.empty<int>
        let model = ResizeArray<int>()
        let count = List.length ops % 5
        let skipped = AList.skip count (CList.value l)

        let apply (op: int) =
            let kind = op % 3
            let rest = op / 3
            let element = rest % 10

            let position =
                let p = (rest / 10) % (model.Count + 1)
                if p < 0 then p + (model.Count + 1) else p

            match kind with
            | 0 ->
                CList.insertAt position element l
                model.Insert(position, element)
            | 1 ->
                if model.Count > 0 && position < model.Count then
                    CList.removeAt position l
                    model.RemoveAt position
            | _ ->
                if model.Count > 0 && position < model.Count then
                    CList.updateAt position element l
                    model[position] <- element

            let expected =
                if model.Count <= count then [||]
                else Array.ofSeq (Seq.skip count model)

            AList.force skipped = expected

        let mutable ok = true

        for op in ops do
            if not (apply op) then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``AList sub matches the model`` () =
    let prop (ops: int list) =
        let l = CList.empty<int>
        let model = ResizeArray<int>()
        let offset = List.length ops % 3
        let count = List.length ops % 5
        let sliced = AList.sub offset count (CList.value l)

        let apply (op: int) =
            let kind = op % 3
            let rest = op / 3
            let element = rest % 10

            let position =
                let p = (rest / 10) % (model.Count + 1)
                if p < 0 then p + (model.Count + 1) else p

            match kind with
            | 0 ->
                CList.insertAt position element l
                model.Insert(position, element)
            | 1 ->
                if model.Count > 0 && position < model.Count then
                    CList.removeAt position l
                    model.RemoveAt position
            | _ ->
                if model.Count > 0 && position < model.Count then
                    CList.updateAt position element l
                    model[position] <- element

            let expected =
                let from = min offset model.Count
                let take = max 0 (min count (model.Count - from))
                Array.ofSeq (Seq.skip from model |> Seq.truncate take)

            AList.force sliced = expected

        let mutable ok = true

        for op in ops do
            if not (apply op) then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``AList sort matches the model`` () =
    let prop (ops: int list) =
        let l = CList.empty<int>
        let model = ResizeArray<int>()
        let sorted = AList.sort (CList.value l)

        let apply (op: int) =
            let kind = op % 3
            let rest = op / 3
            let element = rest % 10

            let position =
                let p = (rest / 10) % (model.Count + 1)
                if p < 0 then p + (model.Count + 1) else p

            match kind with
            | 0 ->
                CList.insertAt position element l
                model.Insert(position, element)
            | 1 ->
                if model.Count > 0 && position < model.Count then
                    CList.removeAt position l
                    model.RemoveAt position
            | _ ->
                if model.Count > 0 && position < model.Count then
                    CList.updateAt position element l
                    model[position] <- element

            let expected = model |> Seq.sort |> Array.ofSeq
            AList.force sorted = expected

        let mutable ok = true

        for op in ops do
            if not (apply op) then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``AList rev matches the model`` () =
    let prop (ops: int list) =
        let l = CList.empty<int>
        let model = ResizeArray<int>()
        let reversed = AList.rev (CList.value l)

        let apply (op: int) =
            let kind = op % 3
            let rest = op / 3
            let element = rest % 10

            let position =
                let p = (rest / 10) % (model.Count + 1)
                if p < 0 then p + (model.Count + 1) else p

            match kind with
            | 0 ->
                CList.insertAt position element l
                model.Insert(position, element)
            | 1 ->
                if model.Count > 0 && position < model.Count then
                    CList.removeAt position l
                    model.RemoveAt position
            | _ ->
                if model.Count > 0 && position < model.Count then
                    CList.updateAt position element l
                    model[position] <- element

            let expected = model |> Seq.rev |> Array.ofSeq
            AList.force reversed = expected

        let mutable ok = true

        for op in ops do
            if not (apply op) then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``AList pairwise matches the model`` () =
    let prop (ops: int list) =
        let l = CList.empty<int>
        let model = ResizeArray<int>()
        let pairs = AList.pairwise (CList.value l)

        let apply (op: int) =
            let kind = op % 3
            let rest = op / 3
            let element = rest % 10

            let position =
                let p = (rest / 10) % (model.Count + 1)
                if p < 0 then p + (model.Count + 1) else p

            match kind with
            | 0 ->
                CList.insertAt position element l
                model.Insert(position, element)
            | 1 ->
                if model.Count > 0 && position < model.Count then
                    CList.removeAt position l
                    model.RemoveAt position
            | _ ->
                if model.Count > 0 && position < model.Count then
                    CList.updateAt position element l
                    model[position] <- element

            let expected =
                seq {
                    for i in 0 .. model.Count - 2 do
                        struct (model[i], model[i + 1])
                }
                |> Array.ofSeq

            AList.force pairs = expected

        let mutable ok = true

        for op in ops do
            if not (apply op) then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``AList sum matches the model`` () =
    let prop (ops: int list) =
        let l = CList.empty<int>
        let model = ResizeArray<int>()
        let total = AList.sum (CList.value l)

        let apply (op: int) =
            let kind = op % 3
            let rest = op / 3
            let element = rest % 10

            let position =
                let p = (rest / 10) % (model.Count + 1)
                if p < 0 then p + (model.Count + 1) else p

            match kind with
            | 0 ->
                CList.insertAt position element l
                model.Insert(position, element)
            | 1 ->
                if model.Count > 0 && position < model.Count then
                    CList.removeAt position l
                    model.RemoveAt position
            | _ ->
                if model.Count > 0 && position < model.Count then
                    CList.updateAt position element l
                    model[position] <- element

            AVal.getValue total = Seq.sum model

        let mutable ok = true

        for op in ops do
            if not (apply op) then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``AList countBy matches the model`` () =
    let prop (ops: int list) =
        let l = CList.empty<int>
        let model = ResizeArray<int>()
        let evenCount = AList.countBy (fun x -> x % 2 = 0) (CList.value l)

        let apply (op: int) =
            let kind = op % 3
            let rest = op / 3
            let element = rest % 10

            let position =
                let p = (rest / 10) % (model.Count + 1)
                if p < 0 then p + (model.Count + 1) else p

            match kind with
            | 0 ->
                CList.insertAt position element l
                model.Insert(position, element)
            | 1 ->
                if model.Count > 0 && position < model.Count then
                    CList.removeAt position l
                    model.RemoveAt position
            | _ ->
                if model.Count > 0 && position < model.Count then
                    CList.updateAt position element l
                    model[position] <- element

            AVal.getValue evenCount = (model |> Seq.filter (fun x -> x % 2 = 0) |> Seq.length)

        let mutable ok = true

        for op in ops do
            if not (apply op) then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``AList tryMin and tryMax match the model`` () =
    let prop (ops: int list) =
        let l = CList.empty<int>
        let model = ResizeArray<int>()
        let mn = AList.tryMin (CList.value l)
        let mx = AList.tryMax (CList.value l)

        let apply (op: int) =
            let kind = op % 3
            let rest = op / 3
            let element = rest % 10

            let position =
                let p = (rest / 10) % (model.Count + 1)
                if p < 0 then p + (model.Count + 1) else p

            match kind with
            | 0 ->
                CList.insertAt position element l
                model.Insert(position, element)
            | 1 ->
                if model.Count > 0 && position < model.Count then
                    CList.removeAt position l
                    model.RemoveAt position
            | _ ->
                if model.Count > 0 && position < model.Count then
                    CList.updateAt position element l
                    model[position] <- element

            let expectedMin = if model.Count = 0 then ValueNone else ValueSome(Seq.min model)
            let expectedMax = if model.Count = 0 then ValueNone else ValueSome(Seq.max model)
            AVal.getValue mn = expectedMin && AVal.getValue mx = expectedMax

        let mutable ok = true

        for op in ops do
            if not (apply op) then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``AList fold matches the model`` () =
    let prop (ops: int list) =
        let l = CList.empty<int>
        let model = ResizeArray<int>()
        let acc = AList.fold (fun s x -> s * 10 + x) 0 (CList.value l)

        let apply (op: int) =
            let kind = op % 3
            let rest = op / 3
            let element = rest % 10

            let position =
                let p = (rest / 10) % (model.Count + 1)
                if p < 0 then p + (model.Count + 1) else p

            match kind with
            | 0 ->
                CList.insertAt position element l
                model.Insert(position, element)
            | 1 ->
                if model.Count > 0 && position < model.Count then
                    CList.removeAt position l
                    model.RemoveAt position
            | _ ->
                if model.Count > 0 && position < model.Count then
                    CList.updateAt position element l
                    model[position] <- element

            let expected = model |> Seq.fold (fun s x -> s * 10 + x) 0
            AVal.getValue acc = expected

        let mutable ok = true

        for op in ops do
            if not (apply op) then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``AList bind matches the model`` () =
    let prop (ops: int list) =
        let l1 = CList.empty<int>
        let l2 = CList.empty<int>
        let selector = CVal.create 0
        let model1 = ResizeArray<int>()
        let model2 = ResizeArray<int>()

        // The bind switches the whole output when the selector aval changes.
        let bound =
            AList.bind
                (fun n -> if n = 0 then CList.value l1 else CList.value l2)
                (CVal.value selector)

        let apply (op: int) =
            let kind = op % 4
            let rest = op / 4
            let element = rest % 10
            let which = (rest / 10) % 2
            let target = if which = 0 then l1 else l2
            let model = if which = 0 then model1 else model2

            let position =
                let p = (rest / 100) % (model.Count + 1)
                if p < 0 then p + (model.Count + 1) else p

            match kind with
            | 0 ->
                CList.insertAt position element target
                model.Insert(position, element)
            | 1 ->
                if model.Count > 0 && position < model.Count then
                    CList.removeAt position target
                    model.RemoveAt position
            | 2 ->
                if model.Count > 0 && position < model.Count then
                    CList.updateAt position element target
                    model[position] <- element
            | _ -> // Switch the selector.
                CVal.set (if which = 0 then 0 else 1) selector

            let expected =
                if AVal.getValue (CVal.value selector) = 0 then
                    Array.ofSeq model1
                else
                    Array.ofSeq model2

            AList.force bound = expected

        let mutable ok = true

        for op in ops do
            if not (apply op) then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``AList concat matches the model`` () =
    let prop (ops: int list) =
        let l1 = CList.empty<int>
        let l2 = CList.empty<int>
        let model1 = ResizeArray<int>()
        let model2 = ResizeArray<int>()
        let concat = AList.concat [ CList.value l1; CList.value l2 ]

        let apply (op: int) =
            let kind = op % 3
            let rest = op / 3
            let element = rest % 10
            let which = (rest / 10) % 2
            let target = if which = 0 then l1 else l2
            let model = if which = 0 then model1 else model2

            let position =
                let p = (rest / 100) % (model.Count + 1)
                if p < 0 then p + (model.Count + 1) else p

            match kind with
            | 0 ->
                CList.insertAt position element target
                model.Insert(position, element)
            | 1 ->
                if model.Count > 0 && position < model.Count then
                    CList.removeAt position target
                    model.RemoveAt position
            | _ ->
                if model.Count > 0 && position < model.Count then
                    CList.updateAt position element target
                    model[position] <- element

            let expected = Array.ofSeq (Seq.append model1 model2)
            AList.force concat = expected

        let mutable ok = true

        for op in ops do
            if not (apply op) then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``AList mapiA passes the mapping-time position`` () =
    let prop (ops: int list) =
        let l = CList.empty<int>
        // The mapping encodes the position it was invoked with into the value.
        let mapped = AList.mapiA (fun i v -> AVal.constant (v * 100 + i)) (CList.value l)
        // Model: element -> the position its mapping ran at (positions stick).
        let model = ResizeArray<struct (int * int)>()

        let apply (op: int) =
            let kind = op % 3
            let rest = op / 3
            let element = rest % 10

            let position =
                let p = (rest / 10) % (model.Count + 1)
                if p < 0 then p + (model.Count + 1) else p

            match kind with
            | 0 ->
                CList.insertAt position element l
                model.Insert(position, struct (element, position))
            | 1 ->
                if model.Count > 0 && position < model.Count then
                    CList.removeAt position l
                    model.RemoveAt position
            | _ -> // Update: equal values are a no-op (no re-map, the position sticks).
                if model.Count > 0 && position < model.Count then
                    let struct (oldE, _) = model[position]

                    if oldE <> element then
                        CList.updateAt position element l
                        model[position] <- struct (element, position)

            let expected = model |> Seq.map (fun struct (e, i) -> e * 100 + i) |> Array.ofSeq
            AList.force mapped = expected

        let mutable ok = true

        for op in ops do
            if not (apply op) then
                ok <- false

        ok

    Check.QuickThrowOnFailure prop

// =============================================================================
// Gap coverage: AVal.ofExternal, the changeables' batch ops, and the observed
// path (the IObservation delivery).
// =============================================================================

[<Fact>]
let ``AVal ofExternal matches the reference model`` () =
    let prop (sc: ExternalScenario) =
        let mutable snapshot = 0
        let ext, invalidate = AVal.ofExternal (fun () -> snapshot)
        // Model: whether an invalidate is pending, and the last read snapshot.
        let mutable dirty = true
        let mutable lastSeen = 0

        let apply (op: ExternalOp) =
            match op with
            | Replace value -> // Replace the external snapshot.
                snapshot <- value
            | Invalidate -> // Invalidate: the next read re-reads the snapshot.
                invalidate ()
                dirty <- true

            let expected = if dirty then snapshot else lastSeen
            let actual = AVal.getValue ext

            if actual <> expected then
                false
            else
                dirty <- false
                lastSeen <- expected
                true

        let mutable ok = true

        for op in sc.ops do
            if not (apply op) then
                ok <- false

        ok

    Check.One (scenarioConfig, prop)

[<Fact>]
let ``CSet updateTo and perform match the sequential model`` () =
    let prop (ops: SetOp list) =
        let viaUpdate = CSet.empty<int>
        let viaPerform = CSet.empty<int>
        let model = HashSet<int>()
        let pendingRems = HashSet<int>()

        for op in ops do
            match op with
            | Add(e, _) -> model.Add e |> ignore
            | SetOp.Remove e -> pendingRems.Add e |> ignore
            | SetOp.SetValue _ -> ()

        // The batch semantics: adds and removes are separate phases (an add
        // and a remove of the same element cancel, regardless of order).
        for e in pendingRems do
            model.Remove e |> ignore

        CSet.updateTo model viaUpdate

        let d = SetDeltaBuilder<int>()

        for op in ops do
            match op with
            | Add(e, _) -> d.Add e
            | SetOp.Remove e -> d.Remove e
            | SetOp.SetValue _ -> ()

        CSet.perform d viaPerform

        let expected = Set.ofSeq model
        Set.ofSeq (ASet.toSet (CSet.value viaUpdate)) = expected
        && Set.ofSeq (ASet.toSet (CSet.value viaPerform)) = expected

    Check.One (scenarioConfig, prop)

[<Fact>]
let ``CMap updateTo and perform match the sequential model`` () =
    let prop (ops: MapOp list) =
        let viaUpdate = CMap.empty<int, int>
        let viaPerform = CMap.empty<int, int>
        let model = Dictionary<int, int>()
        let pendingRems = HashSet<int>()

        for op in ops do
            match op with
            | Upsert(k, v) -> model[k] <- v
            | MapOp.Remove k -> pendingRems.Add k |> ignore
            | MapOp.SetValue _ -> ()

        // The batch semantics: sets and removes are separate phases (a set
        // and a remove of the same key cancel, regardless of order).
        for k in pendingRems do
            model.Remove k |> ignore

        CMap.updateTo (Seq.map (fun (KeyValue(k, v)) -> k, v) model) viaUpdate

        let d = MapDeltaBuilder<int, int>()

        for op in ops do
            match op with
            | Upsert(k, v) -> d.Set(k, v)
            | MapOp.Remove k -> d.Remove k
            | MapOp.SetValue _ -> ()

        CMap.perform d viaPerform

        let expected = [ for KeyValue(k, v) in model -> k, v ] |> Map.ofList
        AMap.toMap (CMap.value viaUpdate) = expected
        && AMap.toMap (CMap.value viaPerform) = expected

    Check.One (scenarioConfig, prop)

[<Fact>]
let ``CList updateTo and perform match the sequential model`` () =
    let prop (ops: ListChange list) =
        let viaUpdate = CList.empty<int>
        let viaPerform = CList.empty<int>
        let model = ResizeArray<int>()
        let d = ListDeltaBuilder<int>()

        for op in ops do
            match op with
            | Insert(e, payload) ->
                let pos = payload % (model.Count + 1)
                model.Insert(pos, e)
                d.Insert(pos, e)
            | RemoveAt payload ->
                if model.Count > 0 then
                    let pos = payload % model.Count
                    model.RemoveAt pos
                    d.Remove(pos)
            | UpdateAt(e, payload) ->
                if model.Count > 0 then
                    let pos = payload % model.Count
                    model[pos] <- e
                    d.Update(pos, e)
            | ListChange.SetValue _ -> ()

        CList.updateTo (Array.ofSeq model) viaUpdate
        CList.perform d viaPerform

        let expected = Array.ofSeq model
        AList.force (CList.value viaUpdate) = expected
        && AList.force (CList.value viaPerform) = expected

    Check.One (scenarioConfig, prop)

[<Fact>]
let ``CList addRange appends the items`` () =
    let prop (xs: int list) =
        let l = CList.empty<int>
        CList.addRange xs l
        AList.force (CList.value l) = List.toArray xs

    Check.QuickThrowOnFailure prop

[<Fact>]
let ``ASet observe delivers the model content after every op`` () =
    let prop (sc: SetScenario) =
        let source = CSet.empty<int>

        for (e, _) in sc.initial do
            CSet.add e source

        let mutable observed = Set.ofSeq (ASet.toSet (CSet.value source))
        let obs = ASet.observe (fun view _ -> observed <- Set.ofSeq view) (CSet.value source)
        let model = HashSet<int>(List.map fst sc.initial)
        let mutable ok = true

        for op in sc.ops do
            match op with
            | Add(e, _) ->
                CSet.add e source
                model.Add e |> ignore
            | SetOp.Remove e ->
                CSet.remove e source
                model.Remove e |> ignore
            | SetOp.SetValue _ -> ()

            // The notification is delivered during the write; the callback
            // must have recorded the new content.
            if observed <> Set.ofSeq model then
                ok <- false

        obs.Dispose()
        ok

    Check.One (scenarioConfig, prop)

[<Fact>]
let ``AMap observe delivers the model content after every op`` () =
    let prop (sc: MapScenario) =
        let source = CMap.empty<int, int>

        for (k, v) in sc.initial do
            CMap.addOrUpdate k v source

        let mutable observed = AMap.toMap (CMap.value source)
        let obs = AMap.observe (fun view _ -> observed <- [ for KeyValue(k, v) in view -> k, v ] |> Map.ofList) (CMap.value source)
        let model = Dictionary<int, int>()

        for (k, v) in sc.initial do
            model[k] <- v

        let mutable ok = true

        for op in sc.ops do
            match op with
            | Upsert(k, v) ->
                CMap.addOrUpdate k v source
                model[k] <- v
            | MapOp.Remove k ->
                CMap.remove k source
                model.Remove k |> ignore
            | MapOp.SetValue _ -> ()

            let expected = [ for KeyValue(k, v) in model -> k, v ] |> Map.ofList

            if observed <> expected then
                ok <- false

        obs.Dispose()
        ok

    Check.One (scenarioConfig, prop)

[<Fact>]
let ``AList observe delivers the model content after every op`` () =
    let prop (sc: ListScenario) =
        let source = CList.empty<int>
        let model = ResizeArray<int>()

        for (e, _) in sc.initial do
            CList.append e source
            model.Add e

        let mutable observed = AList.toArray (CList.value source)
        let obs = AList.observe (fun view _ -> observed <- Seq.toArray view) (CList.value source)
        let mutable ok = true

        for op in sc.ops do
            match op with
            | Insert(e, payload) ->
                let pos = payload % (model.Count + 1)
                CList.insertAt pos e source
                model.Insert(pos, e)
            | RemoveAt payload ->
                if model.Count > 0 then
                    let pos = payload % model.Count
                    CList.removeAt pos source
                    model.RemoveAt pos
            | UpdateAt(e, payload) ->
                if model.Count > 0 then
                    let pos = payload % model.Count
                    CList.updateAt pos e source
                    model[pos] <- e
            | SetValue _ -> ()

            if observed <> Array.ofSeq model then
                ok <- false

        obs.Dispose()
        ok

    Check.One (scenarioConfig, prop)
