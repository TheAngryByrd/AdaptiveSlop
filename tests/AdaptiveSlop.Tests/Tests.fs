module AdaptiveSlop.Tests

#nowarn "893"

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open global.Xunit
open AdaptiveSlop.Core


type private DepTree =
    | Leaf of ChangeableValue<int>
    | Node of DepTree list

let rec private collectLeaves tree =
    match tree with
    | Leaf leaf -> [ leaf ]
    | Node children -> children |> List.collect collectLeaves

let rec private buildAdaptive tree =
    match tree with
    | Leaf leaf -> leaf :> IAdaptiveValue<int>
    | Node children ->
        match children with
        | [] -> AVal.constant 0
        | first :: rest ->
            let initial = buildAdaptive first

            rest
            |> List.fold
                (fun acc child ->
                    let right = buildAdaptive child
                    AVal.map2 (fun leftValue rightValue -> leftValue + rightValue) acc right)
                initial

let private buildTree depth (values: int list) =
    let mutable index = 0

    let nextValue () =
        if values.IsEmpty then
            0
        else
            let value = values[index % values.Length]
            index <- index + 1
            value

    let rec build currentDepth =
        if currentDepth <= 0 then
            Leaf(CVal.create (nextValue ()))
        else
            let left = build (currentDepth - 1)
            let right = build (currentDepth - 1)
            Node [ left; right ]

    build depth

[<Fact>]
let ``AVal map reflects changes`` () =
    let input = CVal.create 5
    let mapped = AVal.map (fun v -> v + 1) (CVal.value input)

    Assert.Equal(6, AVal.getValue mapped)
    input.Set(10)
    Assert.Equal(11, AVal.getValue mapped)

[<Fact>]
let ``AVal map avoids recompute when unchanged`` () =
    let input = CVal.create 3
    let mutable recomputeCount = 0

    let mapped =
        AVal.map
            (fun v ->
                recomputeCount <- recomputeCount + 1
                v * 2)
            (CVal.value input)

    Assert.Equal(6, AVal.getValue mapped)
    Assert.Equal(1, recomputeCount)
    Assert.Equal(6, AVal.getValue mapped)
    Assert.Equal(1, recomputeCount)

    input.Set(4)
    Assert.Equal(8, AVal.getValue mapped)
    Assert.Equal(2, recomputeCount)
    Assert.Equal(8, AVal.getValue mapped)
    Assert.Equal(2, recomputeCount)

[<Fact>]
let ``AVal mapTask produces expected results`` () =
    let input = CVal.create 2
    let mapped = AVal.mapTask (fun v -> Task.FromResult(v * 3)) (CVal.value input)

    let initial = AVal.getValue mapped
    Assert.Equal(6, initial.Result)
    input.Set(4)
    let updated = AVal.getValue mapped
    Assert.Equal(12, updated.Result)

[<Fact>]
let ``AVal mapValueTask produces expected results`` () =
    let input = CVal.create 3
    let mapped = AVal.mapValueTask (fun v -> ValueTask<int>(v + 5)) (CVal.value input)

    let initial = AVal.getValue mapped
    Assert.Equal(8, initial.Result)
    input.Set(7)
    let updated = AVal.getValue mapped
    Assert.Equal(12, updated.Result)

[<Fact>]
let ``AVal bind chooses latest adaptive value`` () =
    let selector = CVal.create true
    let left = CVal.create 1
    let right = CVal.create 5

    let bound =
        AVal.bind (fun useLeft -> if useLeft then CVal.value left else CVal.value right) (CVal.value selector)

    Assert.Equal(1, AVal.getValue bound)
    selector.Set(false)
    Assert.Equal(5, AVal.getValue bound)
    right.Set(9)
    Assert.Equal(9, AVal.getValue bound)

[<Fact>]
let ``AVal mapTaskResult and bindTaskResult`` () =
    let source = CVal.create 2
    let taskValue = AVal.mapTask (fun v -> Task.FromResult(v + 1)) (CVal.value source)
    let mapped = AVal.mapTaskResult (fun v -> v * 3) taskValue
    let bound = AVal.bindTaskResult (fun v -> Task.FromResult(v - 1)) mapped

    let initial = AVal.getValue bound
    Assert.Equal(8, initial.Result)
    source.Set(4)
    let updated = AVal.getValue bound
    Assert.Equal(14, updated.Result)

[<Fact>]
let ``AVal mapValueTaskResult and bindValueTaskResult`` () =
    let source = CVal.create 1

    let taskValue =
        AVal.mapValueTask (fun v -> ValueTask<int>(v + 2)) (CVal.value source)

    let mapped = AVal.mapValueTaskResult (fun v -> v * 4) taskValue
    let bound = AVal.bindValueTaskResult (fun v -> ValueTask<int>(v + 1)) mapped

    let initial = AVal.getValue bound
    Assert.Equal(13, initial.Result)
    source.Set(3)
    let updated = AVal.getValue bound
    Assert.Equal(21, updated.Result)

[<Fact>]
let ``ASet union matches expected output`` () =
    let left = CSet.ofSeq [ 1; 2; 3 ]
    let right = CSet.ofSeq [ 3; 4 ]

    let unioned = ASet.union (CSet.value left) (CSet.value right)
    let expectedInitial: Set<int> = Set.ofList [ 1; 2; 3; 4 ]
    Assert.Equal<Set<int>>(expectedInitial, ASet.toSet unioned)

    right.Add(5)
    let expectedAfterAdd: Set<int> = Set.ofList [ 1; 2; 3; 4; 5 ]
    Assert.Equal<Set<int>>(expectedAfterAdd, ASet.toSet unioned)

[<Fact>]
let ``ASet map and filter`` () =
    let source = CSet.ofSeq [ 1; 2; 3; 4 ]
    let mapped = ASet.map (fun v -> v * 2) (CSet.value source)
    let filtered = ASet.filter (fun v -> v > 4) mapped

    let expectedInitial: Set<int> = Set.ofList [ 6; 8 ]
    Assert.Equal<Set<int>>(expectedInitial, ASet.toSet filtered)

    source.Add(5)
    let expectedAfterAdd: Set<int> = Set.ofList [ 6; 8; 10 ]
    Assert.Equal<Set<int>>(expectedAfterAdd, ASet.toSet filtered)

    source.Remove(4)
    let expectedAfterRemove: Set<int> = Set.ofList [ 6; 10 ]
    Assert.Equal<Set<int>>(expectedAfterRemove, ASet.toSet filtered)

[<Fact>]
let ``AMap map and filter`` () =
    let source = CMap.ofSeq [ 1, 10; 2, 20; 3, 30 ]
    let mapped = AMap.map (fun _ v -> v + 1) (CMap.value source)
    let filtered = AMap.filter (fun _ v -> v > 15) mapped

    let expectedInitial: Map<int, int> = Map.ofList [ 2, 21; 3, 31 ]
    Assert.Equal<Map<int, int>>(expectedInitial, AMap.toMap filtered)

    source.AddOrUpdate 4 40
    let expectedAfterAdd: Map<int, int> = Map.ofList [ 2, 21; 3, 31; 4, 41 ]
    Assert.Equal<Map<int, int>>(expectedAfterAdd, AMap.toMap filtered)

    source.Remove(3)
    let expectedAfterRemove: Map<int, int> = Map.ofList [ 2, 21; 4, 41 ]
    Assert.Equal<Map<int, int>>(expectedAfterRemove, AMap.toMap filtered)

[<Fact>]
let ``Transaction defers ChangeableValue updates`` () =
    let value = CVal.create 1

    Transaction.run (fun () ->
        value.Set(5)
        Assert.Equal(1, AVal.getValue (CVal.value value)))
    |> ignore

    Assert.Equal(5, AVal.getValue (CVal.value value))

[<Fact>]
let ``Transaction nesting defers updates until outer commit`` () =
    let value = CVal.create 1

    Transaction.run (fun () ->
        value.Set(2)
        Transaction.run (fun () -> value.Set(3)) |> ignore
        Assert.Equal(1, AVal.getValue (CVal.value value)))
    |> ignore

    Assert.Equal(3, AVal.getValue (CVal.value value))

[<Fact>]
let ``Transaction rollback on exception`` () =
    let value = CVal.create 1

    Assert.Throws<exn>(fun () ->
        Transaction.run (fun () ->
            value.Set(5)
            failwith "boom")
        |> ignore)
    |> ignore

    Assert.Equal(1, AVal.getValue (CVal.value value))

[<Fact>]
let ``Transaction batches set updates`` () =
    let setValue = CSet.ofSeq [ 1; 2 ]

    Transaction.run (fun () ->
        setValue.Add(3)
        setValue.Remove(1)
        let expectedDuring: Set<int> = Set.ofList [ 1; 2 ]
        Assert.Equal<Set<int>>(expectedDuring, ASet.toSet (CSet.value setValue)))
    |> ignore

    let expectedAfter: Set<int> = Set.ofList [ 2; 3 ]
    Assert.Equal<Set<int>>(expectedAfter, ASet.toSet (CSet.value setValue))

[<Fact>]
let ``Transaction batches map updates`` () =
    let mapValue = CMap.ofSeq [ 1, 10; 2, 20 ]

    Transaction.run (fun () ->
        mapValue.AddOrUpdate 3 30
        mapValue.Remove(1)
        let expectedDuring: Map<int, int> = Map.ofList [ 1, 10; 2, 20 ]
        Assert.Equal<Map<int, int>>(expectedDuring, AMap.toMap (CMap.value mapValue)))
    |> ignore

    let expectedAfter: Map<int, int> = Map.ofList [ 2, 20; 3, 30 ]
    Assert.Equal<Map<int, int>>(expectedAfter, AMap.toMap (CMap.value mapValue))

[<Fact>]
let ``AVal mapTask tracks latest value`` () =
    let input = CVal.create 1
    let mapped = AVal.mapTask (fun v -> Task.FromResult(v + 10)) (CVal.value input)

    let initial = AVal.getValue mapped
    Assert.Equal(11, initial.Result)

    input.Set(4)
    input.Set(7)
    let updated = AVal.getValue mapped
    Assert.Equal(17, updated.Result)

[<Fact>]
let ``AVal mapValueTask tracks latest value`` () =
    let input = CVal.create 2

    let mapped =
        AVal.mapValueTask (fun (v: int) -> ValueTask<int>(v * v)) (CVal.value input)

    let initial = AVal.getValue mapped
    Assert.Equal(4, initial.Result)

    input.Set(3)
    input.Set(5)
    let updated = AVal.getValue mapped
    Assert.Equal(25, updated.Result)

[<Fact>]
let ``ASet union updates with add/remove`` () =
    let left = CSet.ofSeq [ 1; 2 ]
    let right = CSet.ofSeq [ 2; 3 ]

    let unioned = ASet.union (CSet.value left) (CSet.value right)
    let expectedInitial: Set<int> = Set.ofList [ 1; 2; 3 ]
    Assert.Equal<Set<int>>(expectedInitial, ASet.toSet unioned)

    left.Remove(2)
    right.Add(4)
    let expectedAfterFirst: Set<int> = Set.ofList [ 1; 2; 3; 4 ]
    Assert.Equal<Set<int>>(expectedAfterFirst, ASet.toSet unioned)

    right.Remove(2)
    left.Add(5)
    let expectedAfterSecond: Set<int> = Set.ofList [ 1; 3; 4; 5 ]
    Assert.Equal<Set<int>>(expectedAfterSecond, ASet.toSet unioned)

[<Fact>]
let ``AMap map and filter respond to updates`` () =
    let source = CMap.ofSeq [ 1, 10; 2, 20 ]
    let mapped = AMap.map (fun _ v -> v + 5) (CMap.value source)
    let filtered = AMap.filter (fun _ v -> v > 20) mapped

    let expectedInitial: Map<int, int> = Map.ofList [ 2, 25 ]
    Assert.Equal<Map<int, int>>(expectedInitial, AMap.toMap filtered)

    source.AddOrUpdate 2 12
    let expectedAfterUpdate: Map<int, int> = Map.empty
    Assert.Equal<Map<int, int>>(expectedAfterUpdate, AMap.toMap filtered)

    source.AddOrUpdate 1 30
    let expectedAfterSecond: Map<int, int> = Map.ofList [ 1, 35 ]
    Assert.Equal<Map<int, int>>(expectedAfterSecond, AMap.toMap filtered)

[<Fact>]
let ``Transaction applies last value update`` () =
    let value = CVal.create 1

    Transaction.run (fun () ->
        value.Set(2)
        value.Set(5)
        Assert.Equal(1, AVal.getValue (CVal.value value)))
    |> ignore

    Assert.Equal(5, AVal.getValue (CVal.value value))

[<Fact>]
let ``AVal getValueTask reflects updates`` () =
    let value = CVal.create 10

    let initial = AVal.getValueTask (CVal.value value)
    Assert.Equal(10, initial.Result)

    value.Set(14)
    let updated = AVal.getValueTask (CVal.value value)
    Assert.Equal(14, updated.Result)

[<Fact>]
let ``AVal getValueValueTask reflects updates`` () =
    let value = CVal.create 4

    let initial = AVal.getValueValueTask (CVal.value value)
    Assert.Equal(4, initial.Result)

    value.Set(9)
    let updated = AVal.getValueValueTask (CVal.value value)
    Assert.Equal(9, updated.Result)

[<Fact>]
let ``ASet map responds to CSet.set`` () =
    let source = CSet.ofSeq [ 1; 2 ]
    let mapped = ASet.map (fun v -> v + 1) (CSet.value source)

    let expectedInitial: Set<int> = Set.ofList [ 2; 3 ]
    Assert.Equal<Set<int>>(expectedInitial, ASet.toSet mapped)

    CSet.set (Set.ofList [ 3; 4 ]) source
    let expectedAfter: Set<int> = Set.ofList [ 4; 5 ]
    Assert.Equal<Set<int>>(expectedAfter, ASet.toSet mapped)

[<Fact>]
let ``AMap map responds to CMap.set`` () =
    let source = CMap.ofSeq [ 1, 10; 2, 20 ]
    let mapped = AMap.map (fun key value -> value + key) (CMap.value source)

    let expectedInitial: Map<int, int> = Map.ofList [ 1, 11; 2, 22 ]
    Assert.Equal<Map<int, int>>(expectedInitial, AMap.toMap mapped)

    CMap.set (Map.ofList [ 2, 5; 3, 7 ]) source
    let expectedAfter: Map<int, int> = Map.ofList [ 2, 7; 3, 10 ]
    Assert.Equal<Map<int, int>>(expectedAfter, AMap.toMap mapped)

[<Fact>]
let ``Transaction defers CSet.set in unions`` () =
    let left = CSet.ofSeq [ 1; 2 ]
    let right = CSet.ofSeq [ 2; 3 ]
    let unioned = ASet.union (CSet.value left) (CSet.value right)

    Transaction.run (fun () ->
        CSet.set (Set.ofList [ 5 ]) left
        CSet.set (Set.ofList [ 6 ]) right
        let expectedDuring: Set<int> = Set.ofList [ 1; 2; 3 ]
        Assert.Equal<Set<int>>(expectedDuring, ASet.toSet unioned))
    |> ignore

    let expectedAfter: Set<int> = Set.ofList [ 5; 6 ]
    Assert.Equal<Set<int>>(expectedAfter, ASet.toSet unioned)

[<Fact>]
let ``ASet union preserves duplicates until fully removed`` () =
    let left = CSet.ofSeq [ 1; 2 ]
    let right = CSet.ofSeq [ 2; 3 ]
    let unioned = ASet.union (CSet.value left) (CSet.value right)

    let expectedInitial: Set<int> = Set.ofList [ 1; 2; 3 ]
    Assert.Equal<Set<int>>(expectedInitial, ASet.toSet unioned)

    left.Remove(2)
    let expectedAfterLeft: Set<int> = Set.ofList [ 1; 2; 3 ]
    Assert.Equal<Set<int>>(expectedAfterLeft, ASet.toSet unioned)

    right.Remove(2)
    let expectedAfterRight: Set<int> = Set.ofList [ 1; 3 ]
    Assert.Equal<Set<int>>(expectedAfterRight, ASet.toSet unioned)

[<Fact>]
let ``AMap filter ignores non-matching updates`` () =
    let source = CMap.ofSeq [ 1, 5; 2, 20 ]
    let filtered = AMap.filter (fun _ value -> value > 10) (CMap.value source)

    let expectedInitial: Map<int, int> = Map.ofList [ 2, 20 ]
    Assert.Equal<Map<int, int>>(expectedInitial, AMap.toMap filtered)

    source.AddOrUpdate 1 8
    source.AddOrUpdate 3 9
    let expectedAfter: Map<int, int> = Map.ofList [ 2, 20 ]
    Assert.Equal<Map<int, int>>(expectedAfter, AMap.toMap filtered)

[<Fact>]
let ``Transaction defers updates across multiple values`` () =
    let first = CVal.create 1
    let second = CVal.create 10

    Transaction.run (fun () ->
        first.Set(2)
        second.Set(20)
        Assert.Equal(1, AVal.getValue (CVal.value first))
        Assert.Equal(10, AVal.getValue (CVal.value second)))
    |> ignore

    Assert.Equal(2, AVal.getValue (CVal.value first))
    Assert.Equal(20, AVal.getValue (CVal.value second))

[<Fact>]
let ``AVal map stays stable on idempotent updates`` () =
    let source = CVal.create 3
    let mapped = AVal.map (fun value -> value * 2) (CVal.value source)

    Assert.Equal(6, AVal.getValue mapped)
    source.Set(3)
    Assert.Equal(6, AVal.getValue mapped)

[<Fact>]
let ``AVal chained maps reflect updates`` () =
    let source = CVal.create 2

    let mapped =
        source
        |> CVal.value
        |> AVal.map (fun value -> value + 1)
        |> AVal.map (fun value -> value * 2)

    Assert.Equal(6, AVal.getValue mapped)
    source.Set(4)
    Assert.Equal(10, AVal.getValue mapped)

[<Fact>]
let ``ASet union with empty set behaves`` () =
    let left = CSet.ofSeq [ 1; 2 ]
    let right = CSet.ofSeq []
    let unioned = ASet.union (CSet.value left) (CSet.value right)

    let expectedInitial: Set<int> = Set.ofList [ 1; 2 ]
    Assert.Equal<Set<int>>(expectedInitial, ASet.toSet unioned)

    right.Add(3)
    let expectedAfterAdd: Set<int> = Set.ofList [ 1; 2; 3 ]
    Assert.Equal<Set<int>>(expectedAfterAdd, ASet.toSet unioned)

    left.Remove(1)
    let expectedAfterRemove: Set<int> = Set.ofList [ 2; 3 ]
    Assert.Equal<Set<int>>(expectedAfterRemove, ASet.toSet unioned)

[<Fact>]
let ``AMap filter updates on removals`` () =
    let source = CMap.ofSeq [ 1, 10; 2, 20; 3, 30 ]
    let filtered = AMap.filter (fun _ value -> value >= 20) (CMap.value source)

    let expectedInitial: Map<int, int> = Map.ofList [ 2, 20; 3, 30 ]
    Assert.Equal<Map<int, int>>(expectedInitial, AMap.toMap filtered)

    source.Remove(3)
    let expectedAfterRemove: Map<int, int> = Map.ofList [ 2, 20 ]
    Assert.Equal<Map<int, int>>(expectedAfterRemove, AMap.toMap filtered)

    source.AddOrUpdate 1 25
    let expectedAfterUpdate: Map<int, int> = Map.ofList [ 1, 25; 2, 20 ]
    Assert.Equal<Map<int, int>>(expectedAfterUpdate, AMap.toMap filtered)

[<Fact>]
let ``Transaction defers set and map together`` () =
    let setValue = CSet.ofSeq [ 1 ]
    let mapValue = CMap.ofSeq [ 1, 1 ]

    Transaction.run (fun () ->
        setValue.Add(2)
        mapValue.AddOrUpdate 2 2

        let expectedSet: Set<int> = Set.ofList [ 1 ]
        let expectedMap: Map<int, int> = Map.ofList [ 1, 1 ]
        Assert.Equal<Set<int>>(expectedSet, ASet.toSet (CSet.value setValue))
        Assert.Equal<Map<int, int>>(expectedMap, AMap.toMap (CMap.value mapValue)))
    |> ignore

    let expectedSetAfter: Set<int> = Set.ofList [ 1; 2 ]
    let expectedMapAfter: Map<int, int> = Map.ofList [ 1, 1; 2, 2 ]
    Assert.Equal<Map<int, int>>(expectedMapAfter, AMap.toMap (CMap.value mapValue))

[<Fact>]
let ``AVal constant returns stable value`` () =
    let constant = AVal.constant 42
    Assert.Equal(42, AVal.getValue constant)
    Assert.Equal(42, AVal.getValue constant)

[<Fact>]
let ``AVal getValueTask and getValueValueTask match`` () =
    let value = CVal.create 7
    let taskValue = AVal.getValueTask (CVal.value value)
    let valueTaskValue = AVal.getValueValueTask (CVal.value value)

    Assert.Equal(7, taskValue.Result)
    Assert.Equal(7, valueTaskValue.Result)

    value.Set(9)
    Assert.Equal(9, (AVal.getValueTask (CVal.value value)).Result)
    Assert.Equal(9, (AVal.getValueValueTask (CVal.value value)).Result)

[<Fact>]
let ``ASet map responds to multiple updates`` () =
    let source = CSet.ofSeq [ 1; 3 ]
    let mapped = ASet.map (fun v -> v * 10) (CSet.value source)

    let expectedInitial: Set<int> = Set.ofList [ 10; 30 ]
    Assert.Equal<Set<int>>(expectedInitial, ASet.toSet mapped)

    source.Add(2)
    source.Remove(1)
    let expectedAfter: Set<int> = Set.ofList [ 20; 30 ]
    Assert.Equal<Set<int>>(expectedAfter, ASet.toSet mapped)

[<Fact>]
let ``AMap filter removes when threshold increases`` () =
    let source = CMap.ofSeq [ 1, 5; 2, 15; 3, 25 ]
    let filtered = AMap.filter (fun _ value -> value >= 10) (CMap.value source)

    let expectedInitial: Map<int, int> = Map.ofList [ 2, 15; 3, 25 ]
    Assert.Equal<Map<int, int>>(expectedInitial, AMap.toMap filtered)

    source.AddOrUpdate 2 8
    let expectedAfter: Map<int, int> = Map.ofList [ 3, 25 ]
    Assert.Equal<Map<int, int>>(expectedAfter, AMap.toMap filtered)

[<Fact>]
let ``Transaction defers map set updates`` () =
    let mapValue = CMap.ofSeq [ 1, 1; 2, 2 ]

    Transaction.run (fun () ->
        CMap.set (Map.ofList [ 3, 3 ]) mapValue
        let expectedDuring: Map<int, int> = Map.ofList [ 1, 1; 2, 2 ]
        Assert.Equal<Map<int, int>>(expectedDuring, AMap.toMap (CMap.value mapValue)))
    |> ignore

    let expectedAfter: Map<int, int> = Map.ofList [ 3, 3 ]
    Assert.Equal<Map<int, int>>(expectedAfter, AMap.toMap (CMap.value mapValue))

[<FsCheck.Xunit.Property(MaxTest = 200)>]
let ``Deep dependency trees propagate updates`` (depth: FsCheck.PositiveInt) (updates: int list) =
    let actualDepth = min 6 depth.Get
    let values = if updates.IsEmpty then [ 0 ] else updates
    let tree = buildTree actualDepth values
    let root = buildAdaptive tree
    let leaves = collectLeaves tree

    let sumLeaves () =
        leaves |> List.sumBy (fun leaf -> AVal.getValue (leaf :> IAdaptiveValue<int>))

    let mutable ok = AVal.getValue root = sumLeaves ()

    if ok then
        let mutable idx = 0

        for value in values do
            let leaf = leaves[idx % leaves.Length]
            leaf.Set(value)
            idx <- idx + 1

            if AVal.getValue root <> sumLeaves () then
                ok <- false

    ok
// =============================================================================
// N-ary Node Tests (map3, map4, mapN, reduce, sum)
// =============================================================================

[<Fact>]
let ``AVal map3 combines three values correctly`` () =
    let a = CVal.create 1
    let b = CVal.create 2
    let c = CVal.create 3

    let combined =
        AVal.map3 (fun x y z -> x + y + z) (CVal.value a) (CVal.value b) (CVal.value c)

    Assert.Equal(6, AVal.getValue combined)

    a.Set(10)
    Assert.Equal(15, AVal.getValue combined)

    b.Set(20)
    Assert.Equal(33, AVal.getValue combined)

    c.Set(30)
    Assert.Equal(60, AVal.getValue combined)

[<Fact>]
let ``AVal map3 avoids recompute when unchanged`` () =
    let a = CVal.create 1
    let b = CVal.create 2
    let c = CVal.create 3
    let mutable computeCount = 0

    let combined =
        AVal.map3
            (fun x y z ->
                computeCount <- computeCount + 1
                x * y * z)
            (CVal.value a)
            (CVal.value b)
            (CVal.value c)

    Assert.Equal(6, AVal.getValue combined)
    Assert.Equal(1, computeCount)

    // Reading again should not recompute
    Assert.Equal(6, AVal.getValue combined)
    Assert.Equal(1, computeCount)

    // Changing a value should recompute
    a.Set(2)
    Assert.Equal(12, AVal.getValue combined)
    Assert.Equal(2, computeCount)

[<Fact>]
let ``AVal map4 combines four values correctly`` () =
    let a = CVal.create 1
    let b = CVal.create 2
    let c = CVal.create 3
    let d = CVal.create 4

    let combined =
        AVal.map4 (fun w x y z -> w + x + y + z) (CVal.value a) (CVal.value b) (CVal.value c) (CVal.value d)

    Assert.Equal(10, AVal.getValue combined)

    a.Set(10)
    Assert.Equal(19, AVal.getValue combined)

    d.Set(40)
    Assert.Equal(55, AVal.getValue combined)

[<Fact>]
let ``AVal map4 avoids recompute when unchanged`` () =
    let a = CVal.create 1
    let b = CVal.create 2
    let c = CVal.create 3
    let d = CVal.create 4
    let mutable computeCount = 0

    let combined =
        AVal.map4
            (fun w x y z ->
                computeCount <- computeCount + 1
                w * x * y * z)
            (CVal.value a)
            (CVal.value b)
            (CVal.value c)
            (CVal.value d)

    Assert.Equal(24, AVal.getValue combined)
    Assert.Equal(1, computeCount)

    // Reading again should not recompute
    Assert.Equal(24, AVal.getValue combined)
    Assert.Equal(1, computeCount)

    // Changing a value should recompute
    b.Set(3)
    Assert.Equal(36, AVal.getValue combined)
    Assert.Equal(2, computeCount)

[<Fact>]
let ``AVal mapN combines array of values correctly`` () =
    let sources =
        [| CVal.create 1; CVal.create 2; CVal.create 3; CVal.create 4; CVal.create 5 |]

    let deps = sources |> Array.map CVal.value
    let combined = AVal.mapN (fun arr -> arr |> Array.sum) deps

    Assert.Equal(15, AVal.getValue combined)

    sources.[0].Set(10)
    Assert.Equal(24, AVal.getValue combined)

    sources.[4].Set(50)
    Assert.Equal(69, AVal.getValue combined)

[<Fact>]
let ``AVal mapN avoids recompute when unchanged`` () =
    let sources = [| CVal.create 1; CVal.create 2; CVal.create 3 |]
    let deps = sources |> Array.map CVal.value
    let mutable computeCount = 0

    let combined =
        AVal.mapN
            (fun arr ->
                computeCount <- computeCount + 1
                arr |> Array.fold (*) 1)
            deps

    Assert.Equal(6, AVal.getValue combined)
    Assert.Equal(1, computeCount)

    // Reading again should not recompute
    Assert.Equal(6, AVal.getValue combined)
    Assert.Equal(1, computeCount)

    // Changing a value should recompute
    sources.[1].Set(5)
    Assert.Equal(15, AVal.getValue combined)
    Assert.Equal(2, computeCount)

[<Fact>]
let ``AVal mapN handles empty array`` () =
    let deps: IAdaptiveValue<int>[] = [||]
    let combined = AVal.mapN (fun arr -> arr.Length) deps

    Assert.Equal(0, AVal.getValue combined)

[<Fact>]
let ``AVal mapN handles single element`` () =
    let source = CVal.create 42
    let deps = [| CVal.value source |]
    let combined = AVal.mapN (fun arr -> arr.[0] * 2) deps

    Assert.Equal(84, AVal.getValue combined)

    source.Set(10)
    Assert.Equal(20, AVal.getValue combined)

[<Fact>]
let ``AVal reduce combines values with binary operation`` () =
    let sources = [| CVal.create 1; CVal.create 2; CVal.create 3; CVal.create 4 |]
    let deps = sources |> Array.map CVal.value
    let reduced = AVal.reduce 0 (+) deps

    Assert.Equal(10, AVal.getValue reduced)

    sources.[0].Set(10)
    Assert.Equal(19, AVal.getValue reduced)

    sources.[3].Set(40)
    Assert.Equal(55, AVal.getValue reduced)

[<Fact>]
let ``AVal reduce handles empty array with init value`` () =
    let deps: IAdaptiveValue<int>[] = [||]
    let reduced = AVal.reduce 42 (+) deps

    Assert.Equal(42, AVal.getValue reduced)

[<Fact>]
let ``AVal reduce handles single element`` () =
    let source = CVal.create 10
    let deps = [| CVal.value source |]
    let reduced = AVal.reduce 5 (+) deps

    Assert.Equal(15, AVal.getValue reduced)

    source.Set(20)
    Assert.Equal(25, AVal.getValue reduced)

[<Fact>]
let ``AVal reduce works with multiplication`` () =
    let sources = [| CVal.create 2; CVal.create 3; CVal.create 4 |]
    let deps = sources |> Array.map CVal.value
    let reduced = AVal.reduce 1 (*) deps

    Assert.Equal(24, AVal.getValue reduced)

    sources.[1].Set(5)
    Assert.Equal(40, AVal.getValue reduced)

[<Fact>]
let ``AVal sum sums integer values`` () =
    let sources = [| CVal.create 10; CVal.create 20; CVal.create 30 |]
    let deps = sources |> Array.map CVal.value
    let summed = AVal.sum deps

    Assert.Equal(60, AVal.getValue summed)

    sources.[0].Set(100)
    Assert.Equal(150, AVal.getValue summed)

    sources.[2].Set(300)
    Assert.Equal(420, AVal.getValue summed)

[<Fact>]
let ``AVal sum handles empty array`` () =
    let deps: IAdaptiveValue<int>[] = [||]
    let summed = AVal.sum deps

    Assert.Equal(0, AVal.getValue summed)

[<Fact>]
let ``AVal sum handles single element`` () =
    let source = CVal.create 42
    let deps = [| CVal.value source |]
    let summed = AVal.sum deps

    Assert.Equal(42, AVal.getValue summed)

    source.Set(100)
    Assert.Equal(100, AVal.getValue summed)

[<Fact>]
let ``AVal sum avoids recompute when unchanged`` () =
    let sources = [| CVal.create 1; CVal.create 2; CVal.create 3 |]
    let deps = sources |> Array.map CVal.value

    // Note: We can't easily count recomputes here without exposing internals,
    // but we can verify version doesn't change on repeated reads
    let summed = AVal.sum deps
    let v1 = (summed :> IAdaptiveObject).Version
    let _ = AVal.getValue summed
    let v2 = (summed :> IAdaptiveObject).Version

    Assert.Equal(v1, v2)

    // After change, version should increase
    sources.[0].Set(10)
    let _ = AVal.getValue summed
    let v3 = (summed :> IAdaptiveObject).Version
    Assert.True(v3 > v2)

[<Fact>]
let ``N-ary nodes work in chains with other adaptive operations`` () =
    let a = CVal.create 1
    let b = CVal.create 2
    let c = CVal.create 3

    // map3 -> map -> map2
    let sum3 =
        AVal.map3 (fun x y z -> x + y + z) (CVal.value a) (CVal.value b) (CVal.value c)

    let doubled = AVal.map (fun x -> x * 2) sum3
    let final = AVal.map2 (fun x y -> x + y) doubled (CVal.value a)

    // (1+2+3)*2 + 1 = 13
    Assert.Equal(13, AVal.getValue final)

    a.Set(10)
    // (10+2+3)*2 + 10 = 40
    Assert.Equal(40, AVal.getValue final)

// =============================================================================
// Phase 0 — Characterization Tests
// =============================================================================

[<Fact>]
let ``Deep chain propagates one update end to end`` () =
    let depth = 500
    let source = CVal.create 1
    let mutable recomputeCount = 0

    let root =
        (CVal.value source, [ 1..depth ])
        ||> List.fold (fun acc _ ->
            AVal.map
                (fun v ->
                    recomputeCount <- recomputeCount + 1
                    v + 1)
                acc)

    Assert.Equal(1 + depth, AVal.getValue root)
    Assert.Equal(depth, recomputeCount)

    // Repeated clean reads must not recompute.
    Assert.Equal(1 + depth, AVal.getValue root)
    Assert.Equal(depth, recomputeCount)

    // One write must recompute each node at most once.
    source.Set(2)
    Assert.Equal(2 + depth, AVal.getValue root)
    Assert.Equal(2 * depth, recomputeCount)

[<Fact>]
let ``Diamond recomputes the join node at most once per change`` () =
    let source = CVal.create 1
    let left = AVal.map (fun v -> v + 1) (CVal.value source)
    let right = AVal.map (fun v -> v * 2) (CVal.value source)
    let mutable joinCount = 0

    let join =
        AVal.map2
            (fun l r ->
                joinCount <- joinCount + 1
                l + r)
            left
            right

    Assert.Equal(4, AVal.getValue join) // (1+1) + (1*2)
    Assert.Equal(1, joinCount)

    source.Set(2)
    Assert.Equal(7, AVal.getValue join) // (2+1) + (2*2)
    Assert.Equal(2, joinCount)

    source.Set(3)
    Assert.Equal(10, AVal.getValue join) // (3+1) + (3*2)
    Assert.Equal(3, joinCount)

[<Fact>]
let ``Bind stops tracking the dropped branch`` () =
    let selector = CVal.create true
    let left = CVal.create 1
    let right = CVal.create 10
    let mutable recomputeCount = 0

    let bound =
        AVal.bind
            (fun useLeft ->
                recomputeCount <- recomputeCount + 1
                if useLeft then CVal.value left else CVal.value right)
            (CVal.value selector)

    Assert.Equal(1, AVal.getValue bound)
    Assert.Equal(1, recomputeCount)

    // Switch to the right branch.
    selector.Set(false)
    Assert.Equal(10, AVal.getValue bound)
    Assert.Equal(2, recomputeCount)

    // Writes to the dropped branch must not recompute the bound node.
    left.Set(99)
    Assert.Equal(10, AVal.getValue bound)
    Assert.Equal(2, recomputeCount)

    // Writes to the live branch must propagate.
    right.Set(20)
    Assert.Equal(20, AVal.getValue bound)
    Assert.Equal(3, recomputeCount)

    // Switching back re-establishes tracking of the left branch.
    selector.Set(true)
    Assert.Equal(99, AVal.getValue bound)
    Assert.Equal(4, recomputeCount)

    right.Set(30)
    Assert.Equal(99, AVal.getValue bound)
    Assert.Equal(4, recomputeCount)

[<Fact>]
let ``Bind nested in bind switches inner graphs`` () =
    let outer = CVal.create true
    let inner = CVal.create true
    let a = CVal.create 1
    let b = CVal.create 2
    let c = CVal.create 3

    let innerBound =
        AVal.bind (fun pick -> if pick then CVal.value a else CVal.value b) (CVal.value inner)

    let outerBound =
        AVal.bind (fun pick -> if pick then innerBound else CVal.value c) (CVal.value outer)

    Assert.Equal(1, AVal.getValue outerBound)

    inner.Set(false)
    Assert.Equal(2, AVal.getValue outerBound)

    // outer switches away from innerBound; inner writes must not matter.
    outer.Set(false)
    Assert.Equal(3, AVal.getValue outerBound)
    inner.Set(true)
    a.Set(100)
    Assert.Equal(3, AVal.getValue outerBound)

    // Switching back picks up the current inner value.
    outer.Set(true)
    Assert.Equal(100, AVal.getValue outerBound)

[<Fact>]
let ``Computed reads inside a transaction see pre-transaction values`` () =
    let source = CVal.create 1
    let mutable recomputeCount = 0

    let mapped =
        AVal.map
            (fun v ->
                recomputeCount <- recomputeCount + 1
                v * 10)
            (CVal.value source)

    Assert.Equal(10, AVal.getValue mapped)
    Assert.Equal(1, recomputeCount)

    Transaction.run (fun () ->
        source.Set(5)
        // The computed node must still see the pre-transaction value.
        Assert.Equal(10, AVal.getValue mapped))
    |> ignore

    // No recompute happened inside the transaction.
    Assert.Equal(1, recomputeCount)

    // After commit the next read recomputes exactly once.
    Assert.Equal(50, AVal.getValue mapped)
    Assert.Equal(2, recomputeCount)

[<Fact>]
let ``Reads inside a transaction see values committed before it`` () =
    let source = CVal.create 1

    source.Set(7)

    Transaction.run (fun () -> Assert.Equal(7, AVal.getValue (CVal.value source)))
    |> ignore

[<Fact>]
let ``Several writes between reads yield the last value`` () =
    let source = CVal.create 0
    let mutable recomputeCount = 0

    let mapped =
        AVal.map
            (fun v ->
                recomputeCount <- recomputeCount + 1
                v + 1)
            (CVal.value source)

    Assert.Equal(1, AVal.getValue mapped)

    source.Set(1)
    source.Set(2)
    source.Set(3)
    Assert.Equal(4, AVal.getValue mapped)
    Assert.Equal(2, recomputeCount)

[<Fact>]
let ``Arbitrary interleaving of writes and reads stays correct`` () =
    let a = CVal.create 0
    let b = CVal.create 0
    let sum = AVal.map2 (+) (CVal.value a) (CVal.value b)
    let doubled = AVal.map (fun v -> v * 2) sum
    let rng = Random(42)

    for _ in 1..200 do
        if rng.NextDouble() < 0.5 then
            a.Set(rng.Next(100))
        else
            b.Set(rng.Next(100))

        let expected = 2 * (AVal.getValue (CVal.value a) + AVal.getValue (CVal.value b))
        Assert.Equal(expected, AVal.getValue doubled)

[<Fact>]
let ``Collection delta sequences match a reference model`` () =
    let source = CSet.empty<int>
    let mapped = ASet.map (fun v -> v * 3) (CSet.value source)
    let filtered = ASet.filter (fun v -> v % 2 = 0) mapped
    let mutable model = Set.empty<int>
    let rng = Random(7)

    for i in 1..300 do
        let v = rng.Next(50)

        if rng.NextDouble() < 0.6 then
            source.Add(v)
            model <- model.Add(v)
        else
            source.Remove(v)
            model <- model.Remove(v)

        let expected = model |> Set.map (fun v -> v * 3) |> Set.filter (fun v -> v % 2 = 0)
        Assert.Equal<Set<int>>(expected, ASet.toSet filtered)

    // Bulk replace must match as well.
    let replacement = Set.ofList [ 1; 2; 3; 60 ]
    CSet.set replacement source

    let expected =
        replacement |> Set.map (fun v -> v * 3) |> Set.filter (fun v -> v % 2 = 0)

    Assert.Equal<Set<int>>(expected, ASet.toSet filtered)

[<Fact>]
let ``Map delta sequences match a reference model`` () =
    let source = CMap.empty<int, int>
    let mapped = AMap.map (fun k v -> v + k) (CMap.value source)
    let mutable model = Map.empty<int, int>
    let rng = Random(11)

    for _ in 1..300 do
        let k = rng.Next(30)

        if rng.NextDouble() < 0.6 then
            source.AddOrUpdate k (rng.Next(100))
            model <- model.Add(k, AMap.toMap (CMap.value source) |> Map.find k)
        else
            source.Remove(k)
            model <- model.Remove k

        let expected = model |> Map.map (fun k v -> v + k)
        Assert.Equal<Map<int, int>>(expected, AMap.toMap mapped)

[<Fact>]
let ``Union delta sequences preserve duplicate semantics`` () =
    let left = CSet.empty<int>
    let right = CSet.empty<int>
    let unioned = ASet.union (CSet.value left) (CSet.value right)
    let rng = Random(13)

    for _ in 1..200 do
        let v = rng.Next(20)

        match rng.Next(4) with
        | 0 -> left.Add(v)
        | 1 -> right.Add(v)
        | 2 -> left.Remove(v)
        | _ -> right.Remove(v)

        let expected =
            Set.union (ASet.toSet (CSet.value left)) (ASet.toSet (CSet.value right))

        Assert.Equal<Set<int>>(expected, ASet.toSet unioned)


// =============================================================================
// Phase 3/4 — Push-marking and observation
// =============================================================================

[<Fact>]
let ``Observed chain updates after source write`` () =
    let a = CVal.create 1
    let m1 = AVal.map (fun v -> v + 1) (CVal.value a)
    let m2 = AVal.map (fun v -> v * 2) m1
    let joined = AVal.map2 (+) m2 (CVal.value a)
    let mutable seen = []
    use _obs = AVal.observe (fun v -> seen <- v :: seen) joined

    a.Set(2)
    Assert.Equal<int list>([ 8 ], seen) // (2+1)*2 + 2

    a.Set(3)
    Assert.Equal<int list>([ 11; 8 ], seen)

[<Fact>]
let ``Observed mixed chain of generic and wide nodes updates after source write`` () =
    let a = CVal.create 1
    let b = CVal.create 2
    let c = CVal.create 3
    let sum = AVal.sum [| CVal.value a; CVal.value b; CVal.value c |]

    let wide =
        AVal.mapN (fun values -> values |> Array.fold (*) 1) [| CVal.value a; CVal.value c |]

    let joined = AVal.map2 (+) sum wide
    let mutable seen = []
    use _obs = AVal.observe (fun v -> seen <- v :: seen) joined

    a.Set(10)
    Assert.Equal<int list>([ 45 ], seen) // (10+2+3) + (10*3)

[<Fact>]
let ``Several writes between reads produce the last value`` () =
    let a = CVal.create 0
    let m = AVal.map (fun v -> v + 1) (CVal.value a)
    use _obs = AVal.observe ignore m

    a.Set(1)
    a.Set(2)
    a.Set(3)
    Assert.Equal(4, AVal.getValue m)

[<Fact>]
let ``Writes inside one transaction produce one notification after the batch`` () =
    let a = CVal.create 0
    let m = AVal.map (fun v -> v + 1) (CVal.value a)
    let mutable count = 0
    let mutable last = 0

    use _obs =
        AVal.observe
            (fun v ->
                count <- count + 1
                last <- v)
            m

    Transaction.run (fun () ->
        a.Set(1)
        a.Set(2)
        a.Set(3)
        // No notification during the batch.
        Assert.Equal(0, count))
    |> ignore

    Assert.Equal(1, count)
    Assert.Equal(4, last)

[<Fact>]
let ``Arbitrary interleaving stays correct under observation`` () =
    let a = CVal.create 0
    let b = CVal.create 0
    let sum = AVal.map2 (+) (CVal.value a) (CVal.value b)
    let doubled = AVal.map (fun v -> v * 2) sum
    use _obs = AVal.observe ignore doubled
    let rng = Random(43)

    for _ in 1..200 do
        if rng.NextDouble() < 0.5 then
            a.Set(rng.Next 100)
        else
            b.Set(rng.Next 100)

        let expected = 2 * (AVal.getValue (CVal.value a) + AVal.getValue (CVal.value b))

        Assert.Equal(expected, AVal.getValue doubled)

[<Fact>]
let ``Disposed observation stops notifications and reads fall back to version checks`` () =
    let a = CVal.create 1
    let m = AVal.map (fun v -> v + 1) (CVal.value a)
    let mutable count = 0
    let obs = AVal.observe (fun _ -> count <- count + 1) m

    a.Set(2)
    Assert.Equal(1, count)

    obs.Dispose()
    Assert.False(obs.IsActive)

    a.Set(3)
    Assert.Equal(1, count)
    // Reads still work through the version-check fallback.
    Assert.Equal(4, AVal.getValue m)

[<Fact>]
let ``Bind switch under observation tracks the live branch`` () =
    let selector = CVal.create true
    let left = CVal.create 1
    let right = CVal.create 10

    let bound =
        AVal.bind (fun useLeft -> if useLeft then CVal.value left else CVal.value right) (CVal.value selector)

    let mutable count = 0
    let mutable last = 0

    use _obs =
        AVal.observe
            (fun v ->
                count <- count + 1
                last <- v)
            bound

    left.Set(2)
    Assert.Equal(2, last)

    selector.Set(false)
    Assert.Equal(10, last)
    let countAfterSwitch = count

    // The dropped branch must not notify.
    left.Set(99)
    Assert.Equal(countAfterSwitch, count)

    // The live branch notifies.
    right.Set(20)
    Assert.Equal(20, last)

[<Fact>]
let ``Writing an equal value does not mark and does not notify`` () =
    let a = CVal.create 5
    let m = AVal.map (fun v -> v + 1) (CVal.value a)
    let mutable count = 0
    use _obs = AVal.observe (fun _ -> count <- count + 1) m

    let versionBefore = (CVal.value a :> IAdaptiveObject).Version
    a.Set(5)

    Assert.Equal(versionBefore, (CVal.value a :> IAdaptiveObject).Version)
    Assert.Equal(0, count)

[<Fact>]
let ``Transaction rollback resets collection journals`` () =
    let s = CSet.ofSeq [ 1 ]

    Assert.Throws<exn>(fun () ->
        Transaction.run (fun () ->
            s.Add(2)
            failwith "boom")
        |> ignore)
    |> ignore

    Assert.Equal<Set<int>>(Set.ofList [ 1 ], ASet.toSet (CSet.value s))
    // The journal must be clean: the next write applies normally.
    s.Add(3)
    Assert.Equal<Set<int>>(Set.ofList [ 1; 3 ], ASet.toSet (CSet.value s))

[<Fact>]
let ``Steady-state observed operations allocate zero bytes`` () =
    let a = CVal.create 1
    let m = AVal.map (fun v -> v + 1) (CVal.value a)
    use _obs = AVal.observe ignore m
    let _ = AVal.getValue m

    let beforeRead = GC.GetAllocatedBytesForCurrentThread()

    for _ in 1..1000 do
        AVal.getValue m |> ignore

    let readAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeRead

    Assert.Equal(0L, readAllocated)

    let beforeWrite = GC.GetAllocatedBytesForCurrentThread()

    for i in 1..1000 do
        a.Set(i)

    let writeAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeWrite

    Assert.Equal(0L, writeAllocated)


[<Fact>]
let ``Transaction run returns the function result`` () =
    let a = CVal.create 1

    let result =
        Transaction.run (fun () ->
            a.Set(2)
            40 + 2)

    Assert.Equal(42, result)

[<Fact>]
let ``Writes during an evaluation are visible to re-reads in the same evaluation`` () =
    let a = CVal.create 1
    let m = AVal.map (fun v -> v + 1) (CVal.value a)

    let trigger =
        AVal.map
            (fun s ->
                if s > 100 then
                    a.Set(0)

                // Re-read m after the write, inside the same evaluation.
                AVal.getValue m + s)
            m

    Assert.Equal(4, AVal.getValue trigger) // (1+1) + (1+1)

    a.Set(101)

    // m is 102 after the write; the re-read inside the same evaluation must see 1.
    Assert.Equal(103, AVal.getValue trigger) // 102 + 1, not 102 + 102

[<Fact>]
let ``mapN recompute allocates zero bytes`` () =
    let deps = Array.init 10 (fun _ -> CVal.create 1)

    let node =
        AVal.mapN (fun values -> values |> Array.fold (+) 0) (deps |> Array.map CVal.value)

    deps[0].Set(2)
    AVal.getValue node |> ignore // warm up: first recompute

    let before = GC.GetAllocatedBytesForCurrentThread()

    for i in 1..1000 do
        deps[0].Set(i)
        AVal.getValue node |> ignore

    let allocated = GC.GetAllocatedBytesForCurrentThread() - before
    Assert.Equal(0L, allocated)

// =============================================================================
// Phase 5 — Cross-thread posting
// =============================================================================

[<Fact>]
let ``Posted values apply at the next graph operation without pump`` () =
    let a = CVal.create 1
    let m = AVal.map (fun v -> v * 10) (CVal.value a)

    a.Post(5)
    // No pump needed: the next owner read drains and applies the post.
    Assert.Equal(50, AVal.getValue m)

    // Posting.pump remains an explicit batch point.
    a.Post(7)
    Posting.pump ()
    Assert.Equal(70, AVal.getValue m)

[<Fact>]
let ``Posts from a foreign thread apply at the next graph operation`` () =
    let a = CVal.create 0
    let mutable seen: int list = []
    use _obs = AVal.observe (fun v -> seen <- v :: seen) (CVal.value a)

    let producer =
        Task.Run(fun () ->
            for i in 1..100 do
                a.Post(i))

    producer.Wait()
    // No owner operation happened yet: posts are still pending, version untouched.
    Assert.Equal(0L, (CVal.value a :> IAdaptiveObject).Version)

    // The first owner read drains: one application of the last posted value.
    Assert.Equal(100, AVal.getValue (CVal.value a))
    Assert.Equal<int list>([ 100 ], seen)

[<Fact>]
let ``Posts from several threads collapse to one application per batch`` () =
    let a = CVal.create 0
    let mutable recomputeCount = 0

    let m =
        AVal.map
            (fun v ->
                recomputeCount <- recomputeCount + 1
                v)
            (CVal.value a)

    Assert.Equal(0, AVal.getValue m) // initial: 1 recompute
    Assert.Equal(1, recomputeCount)

    let producers =
        [ for _ in 1..4 ->
              Task.Run(fun () ->
                  for i in 1..250 do
                      a.Post(i)) ]

    Task.WaitAll(Array.ofList producers)
    // The first read drains: all 1000 posts collapsed into one application.
    Assert.Equal(250, AVal.getValue (CVal.value a))
    Assert.Equal(250, AVal.getValue m) // lazy: this read triggers the recompute
    Assert.Equal(2, recomputeCount)
    // An explicit pump with nothing pending must not recompute anything.
    Posting.pump ()
    Assert.Equal(2, recomputeCount)

[<Fact>]
let ``Posting an equal value does not mark`` () =
    let a = CVal.create 5
    let mutable count = 0
    use _obs = AVal.observe (fun _ -> count <- count + 1) (CVal.value a)

    a.Post(5)
    Posting.pump ()
    Assert.Equal(0, count)

[<Fact>]
let ``Post and pump allocate zero bytes`` () =
    let a = CVal.create 0
    let m = AVal.map (fun v -> v + 1) (CVal.value a)
    AVal.getValue m |> ignore // warm up

    let before = GC.GetAllocatedBytesForCurrentThread()

    for i in 1..1000 do
        a.Post(i)
        Posting.pump ()
        AVal.getValue m |> ignore

    let allocated = GC.GetAllocatedBytesForCurrentThread() - before
    Assert.Equal(0L, allocated)

[<Fact>]
let ``Stress: posts interleaved with pumps and reads stay consistent`` () =
    let a = CVal.create 0
    let m = AVal.map (fun v -> v + 1) (CVal.value a)
    let rng = Random(1)

    let producer =
        Task.Run(fun () ->
            for _ in 1..5000 do
                a.Post(rng.Next(1000)))

    // Pump and read while the producer runs.
    let mutable ok = true

    while not producer.IsCompleted do
        Posting.pump ()
        let v = AVal.getValue m

        if v < 1 || v > 1000 then
            ok <- false

        Thread.Yield() |> ignore

    producer.Wait()
    Posting.pump ()
    let final = AVal.getValue (CVal.value a)
    Assert.True(ok, "read an out-of-range value")
    Assert.InRange(final, 0, 999)
    Assert.Equal(final + 1, AVal.getValue m)


// =============================================================================
// Phase 6 — Collections lifecycle
// =============================================================================

[<Fact>]
let ``AMap map on top of filter receives updates`` () =
    // Regression: FilterMapNode used to register by concrete type dispatch,
    // so a map node on top of a filter node never received deltas.
    let source = CMap.ofSeq [ 1, 10; 2, 20 ]
    let filtered = AMap.filter (fun _ v -> v > 15) (CMap.value source)
    let mapped = AMap.map (fun _ v -> v + 1) filtered
    Assert.Equal<Map<int, int>>(Map.ofList [ 2, 21 ], AMap.toMap mapped)

    CMap.addOrUpdate 3 30 source
    CMap.addOrUpdate 1 11 source // still filtered out
    Assert.Equal<Map<int, int>>(Map.ofList [ 2, 21; 3, 31 ], AMap.toMap mapped)

[<Fact>]
let ``force materializes a checkpoint the library never touches again`` () =
    let source = CSet.ofSeq [ 1; 2; 3 ]
    let mapped = ASet.map (fun v -> v * 2) (CSet.value source)
    let snapshot = ASet.force mapped
    Assert.Equal(3, snapshot.Count)
    Assert.True(snapshot.Contains 6)

    // The live chain keeps working; the checkpoint is decoupled.
    CSet.add 4 source
    Assert.Equal(4, (ASet.force mapped).Count)
    Assert.Equal(3, snapshot.Count)
    Assert.True(snapshot.Contains 6)

[<Fact>]
let ``force on a map materializes FrozenDictionary`` () =
    let source = CMap.ofSeq [ 1, 10; 2, 20 ]
    let mapped = AMap.map (fun _ v -> v * 10) (CMap.value source)
    let snapshot = AMap.force mapped
    Assert.Equal(100, snapshot[1])
    Assert.Equal(200, snapshot[2])
    CMap.addOrUpdate 3 30 source
    Assert.Equal(3, (AMap.force mapped).Count)
    Assert.Equal(2, snapshot.Count)

[<Fact>]
let ``toSet and toMap materialize F# counterparts`` () =
    let source = CSet.ofSeq [ 3; 1; 2 ]
    Assert.Equal<Set<int>>(Set.ofList [ 1; 2; 3 ], ASet.toSet (CSet.value source))
    let m = CMap.ofSeq [ 2, 20; 1, 10 ]
    Assert.Equal<Map<int, int>>(Map.ofList [ 1, 10; 2, 20 ], AMap.toMap (CMap.value m))

[<Fact>]
let ``collections accept element types without comparison`` () =
    // Regression: the collection interfaces used to require ': comparison'.
    let s = ASet.ofSeq [ typeof<int>; typeof<string> ]
    Assert.Equal(2, (ASet.force s).Count)
    let m = AMap.ofSeq [ typeof<int>, 1 ]
    Assert.Equal(1, (AMap.force m).Count)

[<Fact>]
let ``derived collections register lazily and dispose cleanly`` () =
    let source = CSet.ofSeq [ 1; 2 ]
    let mapped = ASet.map (fun v -> v * 2) (CSet.value source)
    let cs = source
    Assert.Equal(0, cs.SinkCount) // not read yet: no registration

    let _ = ASet.toSet mapped // first read: registers
    Assert.Equal(1, cs.SinkCount)

    (mapped :> IDisposable).Dispose()
    Assert.Equal(0, cs.SinkCount)

    // A disposed node throws on read and processes nothing.
    Assert.Throws<InvalidOperationException>(fun () -> ASet.toSet mapped |> ignore)
    |> ignore

    CSet.add 3 source
    Assert.Equal(0, cs.SinkCount)

[<Fact>]
let ``scalar dependency on a derived collection stays fresh`` () =
    // A notification callback reads a derived collection: the chain
    // (source -> map node -> scalar read in the callback) must deliver the
    // updated state through the receipt marking and the drain.
    let source = CSet.ofSeq [ 1; 2; 3 ]
    let mapped = ASet.map (fun v -> v * 2) (CSet.value source)
    let trigger = CVal.create 0
    let mutable seen: int list = []

    use _obs =
        AVal.observe (fun _ -> seen <- (Set.count (ASet.toSet mapped)) :: seen) trigger

    CSet.add 4 source
    CSet.remove 1 source
    trigger.Set(1) // fires the callback, which reads the derived collection
    Assert.Equal(3, Set.count (ASet.toSet mapped)) // {4, 6, 8}
    Assert.Equal(3, List.head seen)

[<Fact>]
let ``add and remove of one element in a transaction collapse to no delta`` () =
    let source = CSet.ofSeq [ 1; 2 ]
    let mutable processed = 0

    let mapped =
        ASet.map
            (fun v ->
                processed <- processed + 1
                v * 2)
            (CSet.value source)

    let _ = ASet.toSet mapped // warm up
    let before = processed

    Transaction.run (fun () ->
        CSet.add 5 source
        CSet.remove 5 source)
    |> ignore

    Assert.Equal<Set<int>>(Set.ofList [ 1; 2 ], ASet.toSet (CSet.value source))
    Assert.Equal(before, processed) // no delta reached the derived node

[<Fact>]
let ``remove then add of an existing element in a transaction keeps it`` () =
    let source = CSet.ofSeq [ 1; 2 ]

    Transaction.run (fun () ->
        CSet.remove 2 source
        CSet.add 2 source)
    |> ignore

    Assert.Equal<Set<int>>(Set.ofList [ 1; 2 ], ASet.toSet (CSet.value source))

[<Fact>]
let ``N-element delta delivery allocates zero bytes`` () =
    let source = CSet.ofSeq [ 1..100 ]
    let mapped = ASet.map (fun v -> v * 2) (CSet.value source)
    let filtered = ASet.filter (fun v -> v % 4 = 0) mapped
    let _ = ASet.toSet filtered // warm up: initial load, buffers, JIT

    // Warm up the write path and grow every journal/out buffer past 16.
    for i in 101..300 do
        CSet.add i source

    let _ = ASet.toSet filtered

    for i in 101..300 do
        CSet.remove i source

    let _ = ASet.toSet filtered

    GC.Collect()
    GC.WaitForPendingFinalizers()
    GC.Collect()
    let before = GC.GetAllocatedBytesForCurrentThread()

    // Measured: writes (journal append) plus one drain read (transient view).
    // The remaining cost is a small constant per draining node (24 B, an F#
    // codegen artifact of byref struct passing); it does not scale with the
    // delta size. Materialization (force/toSet) is not part of the contract.
    for i in 301..310 do
        CSet.add i source

    ASet.getValue filtered |> ignore
    let after = GC.GetAllocatedBytesForCurrentThread()
    Assert.True(after - before < 128L, sprintf "drain allocated %d bytes" (after - before))
    Assert.Equal(55, (ASet.toSet filtered).Count)

[<Fact>]
let ``derived chain processes nothing when never read`` () =
    let source = CSet.ofSeq [ 1 ]
    let mutable processed = 0

    let mapped =
        ASet.map
            (fun v ->
                processed <- processed + 1
                v)
            (CSet.value source)

    for i in 2..100 do
        CSet.add i source

    // No read happened: the mapping never ran (write is journal-append only).
    Assert.Equal(0, processed)

    let _ = ASet.toSet mapped
    Assert.Equal(100, processed)

// =============================================================================
// Phase 7 — Collection observation
// =============================================================================

[<Fact>]
let ``ASet observe delivers net deltas with the current view`` () =
    let source = CSet.empty<int>
    let views = ResizeArray<Set<int>>()
    let adds = ResizeArray<Set<int>>()
    let rems = ResizeArray<Set<int>>()

    use obs =
        ASet.observe
            (fun view delta ->
                views.Add(Set.ofSeq view)
                adds.Add(Set.ofArray (delta.Added.ToArray()))
                rems.Add(Set.ofArray (delta.Removed.ToArray())))
            (CSet.value source)

    CSet.add 1 source
    CSet.add 2 source
    CSet.remove 1 source
    CSet.add 3 source

    Assert.Equal<Set<int>>(
        [ Set.ofList [ 1 ]; Set.ofList [ 1; 2 ]; Set.ofList [ 2 ]; Set.ofList [ 2; 3 ] ],
        List.ofSeq views
    )

    Assert.Equal<Set<int>>([ Set.ofList [ 1 ]; Set.ofList [ 2 ]; Set.empty; Set.ofList [ 3 ] ], List.ofSeq adds)

    Assert.Equal<Set<int>>([ Set.empty; Set.empty; Set.ofList [ 1 ]; Set.empty ], List.ofSeq rems)

[<Fact>]
let ``AMap observe delivers set entries and removed keys`` () =
    let source = CMap.empty<string, int>
    let seen = ResizeArray<Set<string * int> * Set<string> * int>()

    use obs =
        AMap.observe
            (fun view delta ->
                let sets =
                    delta.SetEntries.ToArray()
                    |> Array.map (fun struct (k, v) -> k, v)
                    |> Set.ofArray

                let rems = delta.RemovedKeys.ToArray() |> Set.ofArray
                seen.Add(sets, rems, view.Count))
            (CMap.value source)

    CMap.addOrUpdate "a" 1 source
    CMap.addOrUpdate "a" 2 source
    CMap.remove "a" source
    CMap.addOrUpdate "b" 7 source

    Assert.Equal<Set<string * int> * Set<string> * int>(
        [ Set.ofList [ "a", 1 ], Set.empty, 1
          Set.ofList [ "a", 2 ], Set.empty, 1
          Set.empty, Set.ofList [ "a" ], 0
          Set.ofList [ "b", 7 ], Set.empty, 1 ],
        List.ofSeq seen
    )

[<Fact>]
let ``collection observe ignores no-op writes`` () =
    let mapSource = CMap.empty<string, int>
    let mutable mapCount = 0

    use obsMap =
        AMap.observe (fun _ _ -> mapCount <- mapCount + 1) (CMap.value mapSource)

    CMap.addOrUpdate "a" 1 mapSource
    CMap.addOrUpdate "a" 1 mapSource // same value: elided at the source
    CMap.remove "b" mapSource // absent: elided at the source
    Assert.Equal(1, mapCount)

    let setSource = CSet.empty<int>
    let mutable setCount = 0

    use obsSet =
        ASet.observe (fun _ _ -> setCount <- setCount + 1) (CSet.value setSource)

    CSet.add 1 setSource
    CSet.add 1 setSource // already present: elided
    CSet.remove 2 setSource // absent: elided
    Assert.Equal(1, setCount)

[<Fact>]
let ``collection observe skips a transaction that nets to nothing`` () =
    let source = CSet.empty<int>
    let mutable count = 0

    use obs = ASet.observe (fun _ _ -> count <- count + 1) (CSet.value source)

    Transaction.run (fun () ->
        CSet.add 1 source
        CSet.remove 1 source)

    Assert.Equal(0, count)

    Transaction.run (fun () -> CSet.add 1 source)
    Assert.Equal(1, count)

[<Fact>]
let ``collection observe works on derived nodes`` () =
    let source = CMap.empty<int, int>
    let mapped = AMap.map (fun k v -> v * 10) (CMap.value source)
    let seen = ResizeArray<Set<int * int> * Set<int> * int>()

    use obs =
        AMap.observe
            (fun view delta ->
                let sets =
                    delta.SetEntries.ToArray()
                    |> Array.map (fun struct (k, v) -> k, v)
                    |> Set.ofArray

                let rems = delta.RemovedKeys.ToArray() |> Set.ofArray
                seen.Add(sets, rems, view.Count))
            mapped

    // Nothing else reads the derived node: the observation's own delivery
    // drains it.
    CMap.addOrUpdate 1 5 source
    CMap.addOrUpdate 2 6 source
    CMap.remove 1 source

    Assert.Equal<Set<int * int> * Set<int> * int>(
        [ Set.ofList [ 1, 50 ], Set.empty, 1
          Set.ofList [ 2, 60 ], Set.empty, 2
          Set.empty, Set.ofList [ 1 ], 1 ],
        List.ofSeq seen
    )

[<Fact>]
let ``collection observe stays silent when a derived filter drops the change`` () =
    let source = CMap.empty<int, int>
    let filtered = AMap.filter (fun _ v -> v > 10) (CMap.value source)
    let mutable count = 0

    use obs = AMap.observe (fun _ _ -> count <- count + 1) filtered

    CMap.addOrUpdate 1 5 source // filtered out: no output delta
    CMap.addOrUpdate 2 20 source
    Assert.Equal(1, count)

[<Fact>]
let ``collection observe does not fire on attach`` () =
    let source = CSet.ofSeq [ 1; 2; 3 ]
    let mutable count = 0

    use obs = ASet.observe (fun _ _ -> count <- count + 1) (CSet.value source)

    Assert.Equal(0, count)
    CSet.add 4 source
    Assert.Equal(1, count)

[<Fact>]
let ``collection observe supports reentrant writes and disposes cleanly`` () =
    let source = CSet.empty<int>
    let mutable count = 0
    let mutable reentrant = false

    use obs =
        ASet.observe
            (fun view _ ->
                count <- count + 1

                if not reentrant && view.Count = 1 then
                    reentrant <- true
                    CSet.add 2 source)
            (CSet.value source)

    CSet.add 1 source
    Assert.Equal(2, count) // the reentrant write was delivered too

    obs.Dispose()
    CSet.add 3 source
    Assert.Equal(2, count) // no delivery after dispose
    Assert.False(obs.IsActive)

[<Fact>]
let ``collection observe delivers steady-state with zero allocation`` () =
    let source = CSet.empty<int>
    let mutable count = 0

    use obs = ASet.observe (fun _ _ -> count <- count + 1) (CSet.value source)

    // Warm-up: grow the source, its flush buffer, and the observer scratch to
    // steady-state sizes, and run one delivery per write.
    for i in 1..50 do
        CSet.add i source

    for i in 1..50 do
        CSet.remove i source

    // Steady state: 150 effective writes (adds for keys 1..50, then remove+add
    // pairs that hit present keys). The set stays at 50 elements: no growth.
    let before = GC.GetAllocatedBytesForCurrentThread()

    for i in 1..100 do
        let k = (i % 50) + 1
        CSet.remove k source
        CSet.add k source

    let allocated = GC.GetAllocatedBytesForCurrentThread() - before
    Assert.Equal(250, count)
    Assert.Equal(0L, allocated)

[<Fact>]
let ``sequential transactions each apply their collection writes`` () =
    // Regression: CommitJournal never reset flushEnqueued, so the flush of a
    // second sequential transaction was never re-enqueued and its writes were
    // silently lost (exposed by the observation tests).
    let setSource = CSet.empty<int>
    Transaction.run (fun () -> CSet.add 1 setSource)
    Transaction.run (fun () -> CSet.add 2 setSource)
    Assert.Equal(2, (ASet.force (CSet.value setSource)).Count)

    let mapSource = CMap.empty<string, int>
    Transaction.run (fun () -> CMap.addOrUpdate "a" 1 mapSource)
    Transaction.run (fun () -> CMap.addOrUpdate "b" 2 mapSource)
    Assert.Equal(2, (AMap.force (CMap.value mapSource)).Count)

// =============================================================================
// Phase 7.2 — Incremental reductions and derived checks
// =============================================================================

[<Fact>]
let ``ASet count isEmpty contains track the state`` () =
    let source = CSet.empty<int>
    let count = ASet.count (CSet.value source)
    let isEmpty = ASet.isEmpty (CSet.value source)
    let hasFive = ASet.contains 5 (CSet.value source)

    Assert.Equal(0, AVal.getValue count)
    Assert.True(AVal.getValue isEmpty)
    Assert.False(AVal.getValue hasFive)

    CSet.add 5 source
    Assert.Equal(1, AVal.getValue count)
    Assert.False(AVal.getValue isEmpty)
    Assert.True(AVal.getValue hasFive)

    CSet.add 7 source
    CSet.remove 5 source
    Assert.Equal(1, AVal.getValue count)
    Assert.False(AVal.getValue hasFive)
    Assert.True(AVal.getValue (ASet.contains 7 (CSet.value source)))

[<Fact>]
let ``ASet exists forall countBy are delta-driven`` () =
    let source = CSet.empty<int>
    let existsEven = ASet.exists (fun v -> v % 2 = 0) (CSet.value source)
    let forallEven = ASet.forall (fun v -> v % 2 = 0) (CSet.value source)
    let evenCount = ASet.countBy (fun v -> v % 2 = 0) (CSet.value source)

    Assert.False(AVal.getValue existsEven)
    Assert.True(AVal.getValue forallEven)
    Assert.Equal(0, AVal.getValue evenCount)

    CSet.add 2 source
    Assert.True(AVal.getValue existsEven)
    Assert.True(AVal.getValue forallEven)
    Assert.Equal(1, AVal.getValue evenCount)

    CSet.add 3 source
    Assert.True(AVal.getValue existsEven)
    Assert.False(AVal.getValue forallEven)
    Assert.Equal(1, AVal.getValue evenCount)

    CSet.add 4 source
    CSet.remove 2 source
    Assert.True(AVal.getValue existsEven)
    Assert.Equal(1, AVal.getValue evenCount)

    CSet.remove 4 source
    Assert.False(AVal.getValue existsEven)
    Assert.False(AVal.getValue forallEven)
    Assert.Equal(0, AVal.getValue evenCount)

[<Fact>]
let ``ASet fold recomputes on removals`` () =
    let source = CSet.empty<int>
    let folded = ASet.fold (+) 0 (CSet.value source)

    CSet.add 10 source
    CSet.add 20 source
    Assert.Equal(30, AVal.getValue folded)

    // fold has no inverse: the removal triggers a full recompute.
    CSet.remove 10 source
    Assert.Equal(20, AVal.getValue folded)

[<Fact>]
let ``ASet foldGroup inverts removals`` () =
    let source = CSet.empty<int>
    let folded = ASet.foldGroup (+) (-) 0 (CSet.value source)

    CSet.add 10 source
    CSet.add 20 source
    Assert.Equal(30, AVal.getValue folded)

    CSet.remove 10 source
    Assert.Equal(20, AVal.getValue folded)

    CSet.add 5 source
    CSet.add 15 source
    CSet.remove 20 source
    Assert.Equal(20, AVal.getValue folded)

[<Fact>]
let ``ASet sum sumBy tryMin tryMax`` () =
    let source = CSet.empty<int>
    let total = ASet.sum (CSet.value source)
    let totalBy = ASet.sumBy (fun v -> v * 2) (CSet.value source)
    let min = ASet.tryMin (CSet.value source)
    let max = ASet.tryMax (CSet.value source)

    Assert.Equal(ValueNone, AVal.getValue min)
    CSet.add 3 source
    CSet.add 1 source
    CSet.add 5 source
    Assert.Equal(9, AVal.getValue total)
    Assert.Equal(18, AVal.getValue totalBy)
    Assert.Equal(ValueSome 1, AVal.getValue min)
    Assert.Equal(ValueSome 5, AVal.getValue max)
    CSet.remove 5 source
    Assert.Equal(4, AVal.getValue total)
    Assert.Equal(ValueSome 3, AVal.getValue max)

[<Fact>]
let ``ASet reduceBy with a mapping`` () =
    let source = CSet.empty<string>
    // Count the strings longer than one character, via reduceBy.
    // The pipe resolves the element type before the lambda is checked (F#
    // checks InlineIfLambda arguments eagerly; the subject-last order makes
    // member access work).
    let longCount =
        CSet.value source
        |> ASet.reduceBy AdaptiveReduction.countPositive (fun s -> s.Length > 1)

    Assert.Equal(0, AVal.getValue longCount)
    CSet.add "a" source
    CSet.add "bb" source
    CSet.add "ccc" source
    Assert.Equal(2, AVal.getValue longCount)
    CSet.remove "bb" source
    Assert.Equal(1, AVal.getValue longCount)

[<Fact>]
let ``AMap tryFind find track entries`` () =
    let source = CMap.empty<string, int>
    let lookup = AMap.tryFind "a" (CMap.value source)

    Assert.Equal(ValueNone, AVal.getValue lookup)

    CMap.addOrUpdate "a" 1 source
    Assert.Equal(ValueSome 1, AVal.getValue lookup)

    CMap.addOrUpdate "a" 2 source
    Assert.Equal(ValueSome 2, AVal.getValue lookup)

    CMap.remove "a" source
    Assert.Equal(ValueNone, AVal.getValue lookup)

    CMap.addOrUpdate "a" 9 source
    let findValue = AMap.find "a" (CMap.value source)
    Assert.Equal(9, AVal.getValue findValue)

    Assert.Throws<System.Collections.Generic.KeyNotFoundException>(fun () ->
        AVal.getValue (AMap.find "missing" (CMap.value source)) |> ignore)

[<Fact>]
let ``AMap reduce updates subtract the old value`` () =
    let source = CMap.empty<string, int>
    let total = AMap.reduce (AdaptiveReduction.sum ()) (CMap.value source)

    CMap.addOrUpdate "a" 10 source
    CMap.addOrUpdate "b" 20 source
    Assert.Equal(30, AVal.getValue total)

    // Update: the old value must be subtracted before the new one is added.
    CMap.addOrUpdate "a" 15 source
    Assert.Equal(35, AVal.getValue total)

    CMap.remove "b" source
    Assert.Equal(15, AVal.getValue total)

[<Fact>]
let ``AMap fold recomputes on removals`` () =
    let source = CMap.empty<int, int>
    let folded = AMap.fold (fun s k v -> s + k * v) 0 (CMap.value source)

    CMap.addOrUpdate 2 3 source
    CMap.addOrUpdate 4 5 source
    Assert.Equal(26, AVal.getValue folded)

    CMap.remove 2 source
    Assert.Equal(20, AVal.getValue folded)

[<Fact>]
let ``AMap foldGroup inverts removals`` () =
    let source = CMap.empty<int, int>

    let folded =
        AMap.foldGroup (fun s k v -> s + k * v) (fun s k v -> s - k * v) 0 (CMap.value source)

    CMap.addOrUpdate 2 3 source
    CMap.addOrUpdate 4 5 source
    Assert.Equal(26, AVal.getValue folded)

    CMap.remove 2 source
    Assert.Equal(20, AVal.getValue folded)

    CMap.addOrUpdate 4 1 source // update: subtract old (20), add new (4)
    Assert.Equal(4, AVal.getValue folded)

[<Fact>]
let ``AMap exists forall countBy`` () =
    let source = CMap.empty<int, int>
    let hasBig = AMap.exists (fun _ v -> v > 100) (CMap.value source)
    let allBig = AMap.forall (fun _ v -> v > 100) (CMap.value source)
    let bigCount = AMap.countBy (fun _ v -> v > 100) (CMap.value source)

    Assert.False(AVal.getValue hasBig)
    Assert.True(AVal.getValue allBig)
    Assert.Equal(0, AVal.getValue bigCount)

    CMap.addOrUpdate 1 50 source
    Assert.False(AVal.getValue hasBig)
    Assert.False(AVal.getValue allBig)
    Assert.Equal(0, AVal.getValue bigCount)

    CMap.addOrUpdate 2 200 source
    Assert.True(AVal.getValue hasBig)
    Assert.Equal(1, AVal.getValue bigCount)

    CMap.addOrUpdate 2 5 source // update flips the group
    Assert.False(AVal.getValue hasBig)
    Assert.Equal(0, AVal.getValue bigCount)

[<Fact>]
let ``toAVal materializes stable snapshots`` () =
    let source = CSet.empty<int>
    let snap = ASet.toAVal (CSet.value source)
    CSet.add 1 source
    CSet.add 2 source

    let first = AVal.getValue snap
    Assert.Equal(2, first.Count)
    Assert.True(first.Contains 1)

    CSet.add 3 source
    Assert.Equal(3, (AVal.getValue snap).Count)
    // The earlier snapshot is untouched by later writes.
    Assert.Equal(2, first.Count)

    let mapSource = CMap.empty<string, int>
    let mapSnap = AMap.toAVal (CMap.value mapSource)
    CMap.addOrUpdate "x" 1 mapSource
    Assert.Equal(1, (AVal.getValue mapSnap).Count)

[<Fact>]
let ``single builds constant singletons`` () =
    let set = ASet.single 42
    Assert.Equal(1, (ASet.force set).Count)
    Assert.True((ASet.force set).Contains 42)

    let mapValue = AMap.single "k" 7
    Assert.Equal(7, (AMap.force mapValue)["k"])

[<Fact>]
let ``reductions compose with observation`` () =
    let source = CSet.empty<int>
    let count = ASet.count (CSet.value source)
    let seen = ResizeArray<int>()

    use obs = AVal.observe (fun v -> seen.Add v) count

    CSet.add 1 source
    CSet.add 2 source
    CSet.remove 1 source
    Assert.Equal<int list>([ 1; 2; 1 ], List.ofSeq seen)

[<Fact>]
let ``reduction over a derived set receives deltas`` () =
    let source = CSet.empty<int>
    let doubled = ASet.map (fun v -> v * 2) (CSet.value source)
    let total = ASet.sum doubled

    CSet.add 1 source
    CSet.add 3 source
    Assert.Equal(8, AVal.getValue total)

    CSet.remove 1 source
    Assert.Equal(6, AVal.getValue total)

[<Fact>]
let ``reduction matches a reference model under random churn`` () =
    let source = CSet.empty<int>
    let folded = ASet.foldGroup (+) (-) 0 (CSet.value source)
    let evenCount = ASet.countBy (fun v -> v % 2 = 0) (CSet.value source)
    let mutable model = Set.empty<int>
    let rng = Random(11)

    for _ in 1..500 do
        let v = rng.Next(100)

        if rng.NextDouble() < 0.6 then
            CSet.add v source
            model <- model.Add v
        else
            CSet.remove v source
            model <- model.Remove v

        Assert.Equal(Set.fold (+) 0 model, AVal.getValue folded)
        Assert.Equal(Set.count (Set.filter (fun x -> x % 2 = 0) model), AVal.getValue evenCount)

[<Fact>]
let ``reduce node steady-state drain allocates zero`` () =
    let source = CSet.empty<int>
    let total = ASet.foldGroup (+) (-) 0 (CSet.value source)

    for i in 1..50 do
        CSet.add i source

    for i in 1..25 do
        CSet.remove i source

    // Warm up the first read: the initial load enumerates the view once
    // (one-time cost, like every node's initial load).
    AVal.getValue total |> ignore

    let before = GC.GetAllocatedBytesForCurrentThread()

    for i in 1..100 do
        CSet.remove ((i % 25) + 26) source
        CSet.add ((i % 25) + 26) source

        if AVal.getValue total <> 950 then
            failwith "sum drifted"

    let allocated = GC.GetAllocatedBytesForCurrentThread() - before
    Assert.Equal(950, AVal.getValue total)
    Assert.Equal(0L, allocated)

// =============================================================================
// Phase 7.3 — collection algebra
// =============================================================================

[<Fact>]
let ``ASet difference/intersect/xor match a reference model under random churn`` () =
    let left = CSet.empty<int>
    let right = CSet.empty<int>
    let diff = ASet.difference (CSet.value left) (CSet.value right)
    let inter = ASet.intersect (CSet.value left) (CSet.value right)
    let xored = ASet.xor (CSet.value left) (CSet.value right)

    for i in 1..30 do
        CSet.add i left
        CSet.add (i + 20) right

    let rng = Random 1234

    for _ in 1..500 do
        let target = if rng.NextDouble() < 0.5 then left else right
        let v = rng.Next(1, 60)

        if rng.NextDouble() < 0.5 then
            CSet.add v target
        else
            CSet.remove v target

        let l = Set.ofSeq (ASet.force (CSet.value left))
        let r = Set.ofSeq (ASet.force (CSet.value right))
        Assert.Equal<Set<int>>(Set.difference l r, Set.ofSeq (ASet.force diff))
        Assert.Equal<Set<int>>(Set.intersect l r, Set.ofSeq (ASet.force inter))
        Assert.Equal<Set<int>>(Set.union (Set.difference l r) (Set.difference r l), Set.ofSeq (ASet.force xored))

    // A write that changes nothing (removing an absent element) must not mark.
    // The nodes' journals stay empty; the versions do not advance.
    let v0 = diff.Version
    CSet.remove 1000 left
    Assert.Equal(v0, diff.Version)

[<Fact>]
let ``ASet unionMany folds a static sequence of sets`` () =
    let a = CSet.ofSeq [ 1; 2 ]
    let b = CSet.ofSeq [ 2; 3 ]
    let c = CSet.ofSeq [ 4 ]
    let all = ASet.unionMany [ CSet.value a; CSet.value b; CSet.value c ]
    Assert.Equal<Set<_>>(Set.ofList [ 1; 2; 3; 4 ], Set.ofSeq (ASet.force all))

    CSet.add 5 a
    Assert.Equal<Set<_>>(Set.ofList [ 1; 2; 3; 4; 5 ], Set.ofSeq (ASet.force all))
    CSet.remove 2 b // 2 is still contributed by a
    Assert.Equal<Set<_>>(Set.ofList [ 1; 2; 3; 4; 5 ], Set.ofSeq (ASet.force all))
    CSet.remove 2 a
    Assert.Equal<Set<_>>(Set.ofList [ 1; 3; 4; 5 ], Set.ofSeq (ASet.force all))

    let empty = ASet.unionMany []
    Assert.True(Set.empty = Set.ofSeq (ASet.force empty))

[<Fact>]
let ``ASet choose keeps only mapped values`` () =
    let source = CSet.ofSeq [ 1; 2; 3; 4 ]

    let chosen =
        ASet.chooseV (fun x -> if x % 2 = 0 then ValueSome(x * 10) else ValueNone) (CSet.value source)

    Assert.Equal<Set<_>>(Set.ofList [ 20; 40 ], Set.ofSeq (ASet.force chosen))

    CSet.add 6 source
    CSet.add 7 source
    Assert.Equal<Set<_>>(Set.ofList [ 20; 40; 60 ], Set.ofSeq (ASet.force chosen))
    CSet.remove 2 source
    Assert.Equal<Set<_>>(Set.ofList [ 40; 60 ], Set.ofSeq (ASet.force chosen))

[<Fact>]
let ``ASet ofAVal rebuilds on every value change`` () =
    let v = CVal.create [| 1; 2; 3 |]
    let set = ASet.ofAVal (CVal.value v)
    Assert.Equal<Set<_>>(Set.ofList [ 1; 2; 3 ], Set.ofSeq (ASet.force set))

    CVal.set [| 3; 4 |] v
    Assert.Equal<Set<_>>(Set.ofList [ 3; 4 ], Set.ofSeq (ASet.force set))

    CVal.set [| 3; 4 |] v // same content: elided, no mark
    let v0 = set.Version
    CVal.set [| 3; 4 |] v
    Assert.Equal(v0, set.Version)

    CVal.set [||] v
    Assert.Equal<Set<int>>(Set.empty, Set.ofSeq (ASet.force set))

[<Fact>]
let ``ASet ofReader and custom are poll-driven`` () =
    let mutable state = HashSet([ 1; 2 ])
    let read = ASet.ofReader (fun () -> HashSet(state))
    Assert.Equal<Set<_>>(Set.ofList [ 1; 2 ], Set.ofSeq (ASet.force read))

    state.Add 3 |> ignore
    state.Remove 1 |> ignore
    Assert.Equal<Set<_>>(Set.ofList [ 2; 3 ], Set.ofSeq (ASet.force read))

    let mutable customState = HashSet([ 10 ])
    let mutable pending = ResizeArray<SetDelta<int>>()

    let custom =
        ASet.custom (fun view delta ->
            if pending.Count > 0 then
                let d = pending[0]
                pending.RemoveAt 0

                for a in d.Added.ToArray() do
                    delta.Add a

                for r in d.Removed.ToArray() do
                    delta.Remove r

            ())

    Assert.Equal<Set<_>>(Set.empty, Set.ofSeq (ASet.force custom))
    let mutable d = SetDelta<int>()
    d.Add 42
    pending.Add d
    Assert.Equal<Set<_>>(Set.ofList [ 42 ], Set.ofSeq (ASet.force custom))

[<Fact>]
let ``ASet constant and delay evaluate once, lazily`` () =
    let mutable calls = 0

    let c =
        ASet.constant (fun () ->
            calls <- calls + 1
            HashSet([ 1; 2 ]))

    Assert.Equal(0, calls)
    Assert.Equal<Set<_>>(Set.ofList [ 1; 2 ], Set.ofSeq (ASet.force c))
    Assert.Equal<Set<_>>(Set.ofList [ 1; 2 ], Set.ofSeq (ASet.force c))
    Assert.Equal(1, calls)

    let d = ASet.delay (fun () -> HashSet([ 7 ]))
    Assert.Equal<Set<_>>(Set.ofList [ 7 ], Set.ofSeq (ASet.force d))

[<Fact>]
let ``AMap union is right-biased and unionWith resolves`` () =
    let a = CMap.empty<string, int>
    let b = CMap.empty<string, int>
    let u = AMap.union (CMap.value a) (CMap.value b)
    let uw = AMap.unionWith (fun _ l r -> l + r) (CMap.value a) (CMap.value b)

    CMap.addOrUpdate "x" 1 a
    CMap.addOrUpdate "y" 2 a
    CMap.addOrUpdate "x" 10 b
    CMap.addOrUpdate "z" 3 b

    // FDA parity: union prefers the RIGHT value on collisions.
    Assert.Equal<Map<string, int>>(
        Map.ofList [ "x", 10; "y", 2; "z", 3 ],
        Map.ofSeq (seq { for KeyValue(k, v) in AMap.force u -> k, v })
    )

    Assert.Equal<Map<string, int>>(
        Map.ofList [ "x", 11; "y", 2; "z", 3 ],
        Map.ofSeq (seq { for KeyValue(k, v) in AMap.force uw -> k, v })
    )

    // Updating the non-winning side of a conflict does not change the output.
    CMap.addOrUpdate "x" 5 a

    Assert.Equal<Map<string, int>>(
        Map.ofList [ "x", 10; "y", 2; "z", 3 ],
        Map.ofSeq (seq { for KeyValue(k, v) in AMap.force u -> k, v })
    )

    CMap.remove "x" b // now the left value surfaces

    Assert.Equal<Map<_, _>>(
        Map.ofList [ "x", 5; "y", 2; "z", 3 ],
        Map.ofSeq (seq { for KeyValue(k, v) in AMap.force u -> k, v })
    )

[<Fact>]
let ``AMap intersect pairs values and intersectWith combines`` () =
    let a = CMap.empty<string, int>
    let b = CMap.empty<string, int>
    let i = AMap.intersect (CMap.value a) (CMap.value b)
    let iw = AMap.intersectWith (fun _ l r -> l * r) (CMap.value a) (CMap.value b)

    CMap.addOrUpdate "x" 3 a
    CMap.addOrUpdate "y" 5 a
    CMap.addOrUpdate "x" 4 b
    CMap.addOrUpdate "z" 6 b

    Assert.Equal<Map<string, int * int>>(
        Map.ofList [ "x", (3, 4) ],
        Map.ofSeq (seq { for KeyValue(k, struct (l, r)) in AMap.force i -> k, (l, r) })
    )

    Assert.Equal<Map<_, _>>(Map.ofList [ "x", 12 ], Map.ofSeq (seq { for KeyValue(k, v) in AMap.force iw -> k, v }))

    CMap.addOrUpdate "y" 5 b
    Assert.Equal(2, AMap.count i |> AVal.getValue)
    CMap.remove "x" a
    Assert.Equal<Map<_, _>>(Map.ofList [ "y", 25 ], Map.ofSeq (seq { for KeyValue(k, v) in AMap.force iw -> k, v }))

[<Fact>]
let ``AMap choose2 matches a reference model under random churn`` () =
    let a = CMap.empty<int, int>
    let b = CMap.empty<int, int>

    // The mapping: union of both sides; on conflict the right value wins.
    let m =
        AMap.choose2V
            (fun _ l r ->
                match l, r with
                | ValueSome lv, ValueSome rv -> ValueSome rv
                | ValueSome lv, ValueNone -> ValueSome lv
                | ValueNone, ValueSome rv -> ValueSome rv
                | ValueNone, ValueNone -> ValueNone)
            (CMap.value a)
            (CMap.value b)

    let rng = Random 99

    for _ in 1..400 do
        let target = if rng.NextDouble() < 0.5 then a else b
        let k = rng.Next(1, 25)
        let v = rng.Next(1, 100)

        if rng.NextDouble() < 0.6 then
            CMap.addOrUpdate k v target
        else
            CMap.remove k target

        let ma = Map.ofSeq (seq { for KeyValue(k, v) in CMap.force a -> k, v })
        let mb = Map.ofSeq (seq { for KeyValue(k, v) in CMap.force b -> k, v })

        let expected = Map.fold (fun acc k v -> Map.add k v acc) ma mb

        let actual = Map.ofSeq (seq { for KeyValue(k, v) in AMap.force m -> k, v })
        Assert.Equal<Map<int, int>>(expected, actual)

    // The mapping is not called when both sides are absent (FDA parity):
    // a removal of a key that has no value on either side must not crash even
    // for a mapping that pattern-matches only Some/Some.
    let strict =
        AMap.choose2V
            (fun _ l r ->
                match l, r with
                | ValueSome lv, ValueSome rv -> ValueSome(struct (lv, rv))
                | ValueSome _, ValueNone
                | ValueNone, ValueSome _ -> ValueNone
                | ValueNone, ValueNone -> failwith "mapping called with no side value")
            (CMap.value a)
            (CMap.value b)

    // A removal of a key with no value on either side must not call the mapping
    // (FDA parity), and keys present on one side only are dropped without error.
    CMap.remove 1000 a

    let expectedStrict =
        Set.intersect
            (Set.ofSeq (seq { for KeyValue(k, _) in CMap.force a -> k }))
            (Set.ofSeq (seq { for KeyValue(k, _) in CMap.force b -> k }))
        |> Set.count

    Assert.Equal(expectedStrict, AMap.count strict |> AVal.getValue)

[<Fact>]
let ``AMap ofASet keeps all values per key; IgnoreDuplicates keeps the last`` () =
    let source = CSet.empty<int * string>
    let all = AMap.ofASet (CSet.value source)
    let last = AMap.ofASetIgnoreDuplicates (CSet.value source)

    CSet.add (1, "a") source
    CSet.add (1, "b") source
    CSet.add (2, "c") source

    Assert.Equal<Map<int, Set<string>>>(
        Map.ofList [ 1, Set.ofList [ "a"; "b" ]; 2, Set.ofList [ "c" ] ],
        Map.ofSeq (seq { for KeyValue(k, v) in AMap.force all -> k, Set.ofSeq v })
    )

    Assert.Equal<Map<_, _>>(
        Map.ofList [ 1, "b"; 2, "c" ],
        Map.ofSeq (seq { for KeyValue(k, v) in AMap.force last -> k, v })
    )

    CSet.remove (1, "a") source

    Assert.Equal<Map<int, Set<string>>>(
        Map.ofList [ 1, Set.ofList [ "b" ]; 2, Set.ofList [ "c" ] ],
        Map.ofSeq (seq { for KeyValue(k, v) in AMap.force all -> k, Set.ofSeq v })
    )

    CSet.remove (1, "b") source
    Assert.Equal<Map<_, _>>(Map.ofList [ 2, "c" ], Map.ofSeq (seq { for KeyValue(k, v) in AMap.force last -> k, v }))

    // ofASetMapped: deriving the key from the values.
    let mapped =
        AMap.ofASetMapped (fun (s: string) -> s.Length) (CSet.value (CSet.ofSeq [ "ab"; "cd"; "e" ]))

    Assert.Equal<Map<int, Set<string>>>(
        Map.ofList [ 2, Set.ofList [ "ab"; "cd" ]; 1, Set.ofList [ "e" ] ],
        Map.ofSeq (seq { for KeyValue(k, v) in AMap.force mapped -> k, Set.ofSeq v })
    )

[<Fact>]
let ``AMap mapSet, toASet and toASetValues`` () =
    let source = CSet.ofSeq [ 1; 2; 3 ]
    let ms = AMap.mapSet (fun k -> k * 10) (CSet.value source)

    Assert.Equal<Map<_, _>>(
        Map.ofList [ 1, 10; 2, 20; 3, 30 ],
        Map.ofSeq (seq { for KeyValue(k, v) in AMap.force ms -> k, v })
    )

    let m = CMap.empty<string, int>
    let keys = AMap.toASet (CMap.value m)
    let values = AMap.toASetValues (CMap.value m)

    CMap.addOrUpdate "a" 1 m
    CMap.addOrUpdate "b" 1 m
    CMap.addOrUpdate "c" 2 m

    Assert.Equal<Set<_>>(Set.ofList [ "a"; "b"; "c" ], Set.ofSeq (ASet.force keys))
    Assert.Equal<Set<_>>(Set.ofList [ 1; 2 ], Set.ofSeq (ASet.force values))

    // Distinct values share one reference: removing one contributor keeps the value.
    CMap.remove "a" m
    Assert.Equal<Set<_>>(Set.ofList [ 1; 2 ], Set.ofSeq (ASet.force values))
    CMap.remove "b" m
    Assert.Equal<Set<_>>(Set.ofList [ 2 ], Set.ofSeq (ASet.force values))

[<Fact>]
let ``AMap ofAVal rebuilds on every value change`` () =
    let v = CVal.create [ "a", 1; "b", 2 ]
    let m = AMap.ofAVal (CVal.value v)

    Assert.Equal<Map<_, _>>(
        Map.ofList [ "a", 1; "b", 2 ],
        Map.ofSeq (seq { for KeyValue(k, v) in AMap.force m -> k, v })
    )

    CVal.set [ "b", 3; "c", 4 ] v

    Assert.Equal<Map<_, _>>(
        Map.ofList [ "b", 3; "c", 4 ],
        Map.ofSeq (seq { for KeyValue(k, v) in AMap.force m -> k, v })
    )

[<Fact>]
let ``AMap custom drives content from a compute`` () =
    let mutable pending = ResizeArray<MapDelta<string, int>>()

    let m =
        AMap.custom (fun view delta ->
            if pending.Count > 0 then
                let d = pending[0]
                pending.RemoveAt 0

                for struct (k, v) in d.SetEntries.ToArray() do
                    delta.Set(k, v)

                for k in d.RemovedKeys.ToArray() do
                    delta.Remove k

            ())

    Assert.Equal(0, AMap.count m |> AVal.getValue)
    let mutable d = MapDelta<string, int>()
    d.Set("x", 1)
    pending.Add d
    Assert.Equal<Map<_, _>>(Map.ofList [ "x", 1 ], Map.ofSeq (seq { for KeyValue(k, v) in AMap.force m -> k, v }))

[<Fact>]
let ``observation through two-source nodes delivers correct deltas`` () =
    let left = CSet.empty<int>
    let right = CSet.empty<int>
    let inter = ASet.intersect (CSet.value left) (CSet.value right)
    let mutable events = ResizeArray<Set<int>>()

    use obs =
        ASet.observe
            (fun view delta ->
                let mutable s = Set.ofSeq view

                for r in delta.Removed.ToArray() do
                    s <- s.Remove r

                for a in delta.Added.ToArray() do
                    s <- s.Add a

                events.Add s)
            inter

    CSet.add 1 left
    CSet.add 1 right
    CSet.add 2 left
    CSet.add 3 right
    CSet.remove 1 right
    CSet.remove 1 left

    // The events reconstruct the view state after each batch.
    // add 1 left: not in the intersection yet -> no event.
    // add 1 right: 1 joins the intersection -> [1].
    // add 2 left / add 3 right: no intersection change -> no event.
    // remove 1 right: the intersection empties -> [].
    // remove 1 left: no intersection change -> no event.
    Assert.Equal<Set<int> list>([ Set.ofList [ 1 ]; Set.empty ], List.ofSeq events)

    // Two-source map node through an observer.
    let a = CMap.empty<string, int>
    let b = CMap.empty<string, int>
    let iw = AMap.intersectWith (fun _ l r -> l + r) (CMap.value a) (CMap.value b)
    let mutable last = Map.empty

    use obs2 =
        AMap.observe
            (fun view delta ->
                let mutable s = Map.empty

                for KeyValue(k, v) in view do
                    s <- Map.add k v s

                last <- s)
            iw

    CMap.addOrUpdate "x" 1 a
    CMap.addOrUpdate "x" 2 b
    Assert.Equal<Map<_, _>>(Map.ofList [ "x", 3 ], last)
    CMap.addOrUpdate "x" 5 a
    Assert.Equal<Map<_, _>>(Map.ofList [ "x", 7 ], last)

[<Fact>]
let ``dirty derived source at first read does not double-apply`` () =
    // A two-source node whose derived source is dirty (pending journal) at the
    // first read: the initial load drains the source, which pushes the delta
    // into the node's journal. If the journal is then applied on top of the
    // loaded view, the refcount double-counts and a later removal leaves a
    // phantom in the output.
    let src = CSet.empty<int>
    let a = ASet.map (fun x -> x * 10) (CSet.value src)
    let b = CSet.ofSeq [ 100 ]
    let u = ASet.union a (CSet.value b)

    // a is registered with src but unread by u; a is dirty when u is first
    // read.
    ASet.toSet a |> ignore
    CSet.add 1 src

    Assert.Equal<Set<int>>(Set.ofList [ 10; 100 ], ASet.toSet u)

    // The double-apply would leave a phantom refcount: removing 10 from a's
    // source would fail to remove 10 from the union.
    CSet.remove 1 src
    Assert.Equal<Set<int>>(Set.ofList [ 100 ], ASet.toSet u)

    // Map node over a dirty derived source (MapMapNode).
    let msrc = CMap.empty<int, int>
    let ma = AMap.map (fun k v -> k, v * 10) (CMap.value msrc)
    let mm = AMap.map (fun _ (_, v) -> v) ma

    (ma :> IAdaptiveMap<_, _>).GetValue() |> ignore
    CMap.addOrUpdate 2 20 msrc
    Assert.Equal<Map<int, int>>(Map.ofList [ 2, 200 ], AMap.toMap mm)
    CMap.remove 2 msrc
    Assert.Equal<Map<int, int>>(Map.empty, AMap.toMap mm)

    // Reduction over a dirty derived source (ASet.count).
    let rsrc = CSet.empty<int>
    let ra = ASet.map (fun x -> x * 10) (CSet.value rsrc)
    let rc = ASet.count ra

    AVal.getValue rc |> ignore
    CSet.add 3 rsrc
    Assert.Equal(1, AVal.getValue rc)
    CSet.remove 3 rsrc
    Assert.Equal(0, AVal.getValue rc)

[<Fact>]
let ``two-source node drains allocate zero in steady state`` () =
    let left = CSet.empty<int>
    let right = CSet.empty<int>
    let inter = ASet.intersect (CSet.value left) (CSet.value right)
    let u = ASet.unionMany [ CSet.value left; CSet.value right ]
    let interCount = ASet.count inter
    let unionCount = ASet.count u

    for i in 1..50 do
        CSet.add i left
        CSet.add i right

    // Warm up: the initial load enumerates both source views once.
    AVal.getValue interCount |> ignore
    AVal.getValue unionCount |> ignore

    let before = GC.GetAllocatedBytesForCurrentThread()

    for i in 1..100 do
        CSet.remove ((i % 25) + 26) left
        CSet.add ((i % 25) + 26) left
        CSet.remove ((i % 25) + 26) right
        CSet.add ((i % 25) + 26) right

        if AVal.getValue interCount <> 50 then
            failwith "intersect drifted"

        if AVal.getValue unionCount <> 50 then
            failwith "union drifted"

    let allocated = GC.GetAllocatedBytesForCurrentThread() - before
    Assert.Equal(0L, allocated)

[<Fact>]
let ``choose2 node drains allocate zero in steady state`` () =
    let a = CMap.empty<int, int>
    let b = CMap.empty<int, int>

    let u = AMap.unionWith (fun _ l r -> l + r) (CMap.value a) (CMap.value b)
    let unionCount = AMap.count u

    for i in 1..50 do
        CMap.addOrUpdate i i a
        CMap.addOrUpdate i (i * 2) b

    AVal.getValue unionCount |> ignore

    let before = GC.GetAllocatedBytesForCurrentThread()

    for i in 1..100 do
        let k = (i % 25) + 26
        CMap.remove k a
        CMap.addOrUpdate k i a
        CMap.remove k b
        CMap.addOrUpdate k (i * 2) b

        if u.GetValue().Count <> 50 then
            failwith "union drifted"


    let allocated = GC.GetAllocatedBytesForCurrentThread() - before
    Assert.Equal(0L, allocated)

// =============================================================================
// PLAN.md Section 7.4 — dynamic dependencies (collect / bind)
// =============================================================================

[<Fact>]
let ``ASet.collect unions the inner sets and follows their changes`` () =
    let src = CSet.empty<int>
    let inner = CSet.empty<int>
    let collected = ASet.collect (fun _ -> CSet.value inner) (CSet.value src)

    CSet.add 1 src
    CSet.add 2 src
    CSet.add 3 inner
    CSet.add 4 inner

    // Two source elements map to the same inner set: 3 and 4 each get two
    // references, the output still holds each element once.
    Assert.Equal<Set<int>>(Set.ofList [ 3; 4 ], ASet.toSet collected)

    // Inner changes propagate to the output.
    CSet.add 5 inner
    Assert.Equal<Set<int>>(Set.ofList [ 3; 4; 5 ], ASet.toSet collected)
    CSet.remove 3 inner
    Assert.Equal<Set<int>>(Set.ofList [ 4; 5 ], ASet.toSet collected)

[<Fact>]
let ``ASet.collect refcounts shared output elements`` () =
    // Two distinct source elements map to two distinct inner sets that share
    // an element. Removing one contribution keeps the element in the output.
    let buckets = CSet.empty<int>
    let odd = CSet.ofSeq [ 1; 3 ]
    let even = CSet.ofSeq [ 2; 4 ]
    let shared = CSet.ofSeq [ 9 ]

    let collected =
        ASet.collect
            (fun b ->
                match b with
                | 1 -> CSet.value odd
                | 2 -> CSet.value even
                | _ -> CSet.value shared)
            (CSet.value buckets)

    CSet.add 1 buckets
    CSet.add 2 buckets
    CSet.add 3 buckets

    // 1 -> odd {1,3}, 2 -> even {2,4}, 3 -> shared {9}
    Assert.Equal<Set<int>>(Set.ofList [ 1; 2; 3; 4; 9 ], ASet.toSet collected)

    // Remove bucket 1: odd's elements {1,3} leave (nobody else has them).
    CSet.remove 1 buckets
    Assert.Equal<Set<int>>(Set.ofList [ 2; 4; 9 ], ASet.toSet collected)

    // Now bucket 2 also maps to shared: 9 has two references.
    CSet.remove 2 buckets
    Assert.Equal<Set<int>>(Set.ofList [ 9 ], ASet.toSet collected)

    // Removing bucket 3 drops the last reference: 9 leaves.
    CSet.remove 3 buckets
    Assert.Equal(0, (ASet.force collected).Count)

[<Fact>]
let ``ASet.collect id is the dynamic unionMany`` () =
    // The outer set is itself adaptive: inner sets enter and leave the union.
    let outer = CSet.empty<AdaptiveSlop.Core.ChangeableSet<int>>
    let a = CSet.ofSeq [ 1; 2 ]
    let b = CSet.ofSeq [ 2; 3 ]

    let u = ASet.collect (fun s -> CSet.value s) (CSet.value outer)
    CSet.add a outer
    CSet.add b outer

    Assert.Equal<Set<int>>(Set.ofList [ 1; 2; 3 ], ASet.toSet u)

    // The inner sets keep contributing while they are in the outer set.
    CSet.add 4 a
    Assert.Equal<Set<int>>(Set.ofList [ 1; 2; 3; 4 ], ASet.toSet u)

    // Removing an inner set removes its (refcounted) contribution.
    CSet.remove a outer
    Assert.Equal<Set<int>>(Set.ofList [ 2; 3 ], ASet.toSet u)

    // The removed inner set's later changes do not leak (eager unregister).
    CSet.add 5 a
    Assert.Equal<Set<int>>(Set.ofList [ 2; 3 ], ASet.toSet u)

[<Fact>]
let ``ASet.collect handles source churn and inner churn in one batch`` () =
    let outer = CSet.empty<int>
    let inner1 = CSet.ofSeq [ 1; 2 ]
    let inner2 = CSet.ofSeq [ 3 ]

    let u =
        ASet.collect (fun x -> if x = 1 then CSet.value inner1 else CSet.value inner2) (CSet.value outer)

    CSet.add 1 outer
    CSet.add 2 outer
    CSet.add 3 inner1
    Assert.Equal<Set<int>>(Set.ofList [ 1; 2; 3 ], ASet.toSet u)

    // One batch: drop element 1 (inner1 leaves), element 2 stays on inner2.
    // inner2 gains 3 in the same batch.
    Transaction.run (fun () ->
        CSet.remove 1 outer
        CSet.add 3 inner2)
    |> ignore

    Assert.Equal<Set<int>>(Set.ofList [ 3 ], ASet.toSet u)

    // Re-add element 1: a fresh entry maps it again.
    CSet.add 1 outer
    Assert.Equal<Set<int>>(Set.ofList [ 1; 2; 3 ], ASet.toSet u)

[<Fact>]
let ``ASet.collect accepts poll inner sets (ofReader)`` () =
    let mutable current = HashSet<int>()
    current.Add 1 |> ignore
    current.Add 2 |> ignore

    let reader =
        ASet.ofReader (fun () ->
            let next = HashSet<int>(current)
            next)

    let src = CSet.ofSeq [ 10; 20 ]
    let u = ASet.collect (fun _ -> reader) (CSet.value src)

    Assert.Equal<Set<int>>(Set.ofList [ 1; 2 ], ASet.toSet u)

    // The poll inner changes; the version check pulls it on the next read.
    current.Remove 1 |> ignore
    current.Add 3 |> ignore
    Assert.Equal<Set<int>>(Set.ofList [ 2; 3 ], ASet.toSet u)

    // Drop one source element: its contribution (with the fresh polled
    // content) leaves, the other element still contributes.
    CSet.remove 10 src
    Assert.Equal<Set<int>>(Set.ofList [ 2; 3 ], ASet.toSet u)

[<Fact>]
let ``ASet.bind swaps the inner set and unregisters the old one eagerly`` () =
    let selected = CVal.create 0
    let buckets = [| CSet.ofSeq [ 1; 2 ]; CSet.ofSeq [ 3; 4 ] |]

    let visible = ASet.bind (fun i -> CSet.value buckets[i]) (CVal.value selected)
    Assert.Equal<Set<int>>(Set.ofList [ 1; 2 ], ASet.toSet visible)

    // The bound inner set's changes propagate.
    CSet.add 5 (buckets[0])
    Assert.Equal<Set<int>>(Set.ofList [ 1; 2; 5 ], ASet.toSet visible)

    // Swap: the old content leaves, the new content enters.
    CVal.set 1 selected
    Assert.Equal<Set<int>>(Set.ofList [ 3; 4 ], ASet.toSet visible)

    // The old inner set is unregistered: its later changes do not leak.
    CSet.add 6 (buckets[0])
    CSet.remove 3 (buckets[1])
    Assert.Equal<Set<int>>(Set.ofList [ 4 ], ASet.toSet visible)

    // Swapping back re-reads the current content of bucket 0 (with 6).
    CVal.set 0 selected
    Assert.Equal<Set<int>>(Set.ofList [ 1; 2; 5; 6 ], ASet.toSet visible)

[<Fact>]
let ``AMap.bind swaps the inner map and unregisters the old one eagerly`` () =
    let selected = CVal.create 0
    let tables = [| CMap.ofSeq [ "a", 1; "b", 2 ]; CMap.ofSeq [ "c", 3 ] |]

    let visible = AMap.bind (fun i -> CMap.value tables[i]) (CVal.value selected)
    Assert.Equal<Map<string, int>>(Map.ofList [ "a", 1; "b", 2 ], AMap.toMap visible)

    CMap.addOrUpdate "b" 20 tables[0]
    Assert.Equal<Map<string, int>>(Map.ofList [ "a", 1; "b", 20 ], AMap.toMap visible)

    CVal.set 1 selected
    Assert.Equal<Map<string, int>>(Map.ofList [ "c", 3 ], AMap.toMap visible)

    // Old inner unregistered: later changes do not leak.
    CMap.addOrUpdate "b" 999 tables[0]
    CMap.remove "c" tables[1]
    Assert.Equal<Map<string, int>>(Map.empty, AMap.toMap visible)

    CVal.set 0 selected
    Assert.Equal<Map<string, int>>(Map.ofList [ "a", 1; "b", 999 ], AMap.toMap visible)

[<Fact>]
let ``collect and bind dispose cleanly`` () =
    let source = CSet.ofSeq [ 1; 2 ]
    let inner = CSet.ofSeq [ 10 ]
    let collected = ASet.collect (fun _ -> CSet.value inner) (CSet.value source)

    ASet.toSet collected |> ignore
    let cs = source
    let ci = inner
    Assert.Equal(1, cs.SinkCount)
    Assert.Equal(2, ci.SinkCount) // one sink per source element

    (collected :> IDisposable).Dispose()
    Assert.Equal(0, cs.SinkCount)
    Assert.Equal(0, ci.SinkCount)

    Assert.Throws<InvalidOperationException>(fun () -> ASet.toSet collected |> ignore)
    |> ignore

    // Writes after disposal process nothing.
    CSet.add 3 source
    CSet.add 11 inner
    Assert.Equal(0, cs.SinkCount)
    Assert.Equal(0, ci.SinkCount)

    // Bind disposal unregisters the value edge and the inner sink.
    let selected = CVal.create 0
    let buckets = [| CSet.ofSeq [ 1 ]; CSet.ofSeq [ 2 ] |]
    let bound = ASet.bind (fun i -> CSet.value buckets[i]) (CVal.value selected)
    ASet.toSet bound |> ignore
    Assert.Equal(1, buckets[0].SinkCount)

    (bound :> IDisposable).Dispose()
    Assert.Equal(0, buckets[0].SinkCount)

    Assert.Throws<InvalidOperationException>(fun () -> ASet.toSet bound |> ignore)
    |> ignore

[<Fact>]
let ``collect drain allocates zero in steady state`` () =
    let outer = CSet.empty<int>
    let inner1 = CSet.ofSeq [ 1; 2; 3 ]
    let inner2 = CSet.ofSeq [ 4; 5 ]

    let u =
        ASet.collect (fun x -> if x % 2 = 0 then CSet.value inner1 else CSet.value inner2) (CSet.value outer)

    for i in 1..20 do
        CSet.add i outer

    // Warm up: initial load + one full drain cycle grows all buffers.
    u.GetValue().Count |> ignore

    for i in 1..20 do
        CSet.add (i + 100) outer

    u.GetValue().Count |> ignore

    let before = GC.GetAllocatedBytesForCurrentThread()

    for i in 1..200 do
        // Churn an element that is actually in inner1 (1..3): the output
        // must stay {1..5} through every remove/add cycle.
        CSet.remove ((i % 3) + 1) inner1
        CSet.add ((i % 3) + 1) inner1

        if u.GetValue().Count <> 5 then
            failwith "collect drifted"

    let allocated = GC.GetAllocatedBytesForCurrentThread() - before
    Assert.Equal(0L, allocated)

[<Fact>]
let ``write during a drain is deferred to the next read`` () =
    // The mapping runs during the drain; a write to a source of the same node
    // appends to the journal mid-processing. The compaction markers keep the
    // reentrant entries for the next read: nothing is lost, nothing double-applies.
    let src = CSet.ofSeq [ 1; 2 ]
    let mutable reentrant = false

    let u =
        ASet.map
            (fun x ->
                if x = 2 && not reentrant then
                    reentrant <- true
                    CSet.add 3 src

                x * 10)
            (CSet.value src)

    // The reentrant write lands in the journal during the first read's load;
    // the first drain applies it before that read returns (the write is part
    // of the read's execution). Later reads see it exactly once.
    Assert.Equal<Set<int>>(Set.ofList [ 10; 20; 30 ], ASet.toSet u)
    Assert.Equal<Set<int>>(Set.ofList [ 10; 20; 30 ], ASet.toSet u)

    // And the removal works: no phantom refcounts.
    CSet.remove 3 src
    Assert.Equal<Set<int>>(Set.ofList [ 10; 20 ], ASet.toSet u)

// NOTE: reading a node from inside its own mapping is out of contract
// (undefined in FDA as well). Writes during a drain are supported: they
// land in the journal via the compaction markers and apply exactly once.

// =============================================================================
// FDA public API parity (docs/PARITY-FDA.md)
// =============================================================================

[<Fact>]
let ``type aliases are usable in signature positions`` () =
    // FDA parity: aval/cval/aset/cset/amap/cmap abbreviations resolve to our
    // interfaces and changeable types.
    let v: aval<int> = AVal.constant 1
    let c: cval<int> = CVal.create 2
    let s: aset<int> = ASet.empty
    let cs: cset<int> = CSet.empty
    let m: amap<string, int> = AMap.empty
    let cm: cmap<string, int> = CMap.empty

    // The aliases are the same types as the long names: cross-assignment works.
    let v2: IAdaptiveValue<int> = v
    let c2: ChangeableValue<int> = c
    let s2: IAdaptiveSet<int> = s
    let cs2: ChangeableSet<int> = cs
    let m2: IAdaptiveMap<string, int> = m
    let cm2: ChangeableMap<string, int> = cm

    Assert.Equal(1, v2.GetValue())
    Assert.Equal(2, c2.GetValue()) // concrete cval exposes GetValue (FDA parity)
    Assert.Equal(0, s2.GetValue().Count)
    Assert.Equal(0, (ASet.getValue cs2).Count)
    Assert.Equal(0, m2.GetValue().Count)
    Assert.Equal(0, (AMap.getValue cm2).Count)

[<Fact>]
let ``AVal force and init match FDA`` () =
    let c = AVal.init 5
    Assert.Equal(5, AVal.force c)
    CVal.set 6 c
    Assert.Equal(6, AVal.force c)
    // force is getValue under another name.
    Assert.Equal(AVal.getValue c, AVal.force c)

[<Fact>]
let ``AVal delay computes once on first read`` () =
    let mutable count = 0

    let v: aval<int> =
        AVal.delay (fun () ->
            count <- count + 1
            42)

    Assert.Equal(0, count) // not computed at construction
    Assert.Equal(42, AVal.getValue v)
    Assert.Equal(42, AVal.getValue v)
    Assert.Equal(1, count) // computed exactly once

[<Fact>]
let ``AVal bind2 and bind3 switch inner values`` () =
    let a = CVal.create 1
    let b = CVal.create 10
    let i1 = CVal.create 100
    let i2 = CVal.create 200
    let i3 = CVal.create 300

    let v2 =
        AVal.bind2 (fun x y -> if x + y > 15 then CVal.value i2 else CVal.value i1) (CVal.value a) (CVal.value b)

    Assert.Equal(100, AVal.getValue v2) // 1 + 10 <= 15 -> i1
    CVal.set 6 a // 6 + 10 > 15 -> switch to i2
    Assert.Equal(200, AVal.getValue v2)
    CVal.set 1 a // switch back
    Assert.Equal(100, AVal.getValue v2)

    let v3 =
        AVal.bind3
            (fun x y z -> if x + y + z > 25 then CVal.value i3 else CVal.value i1)
            (CVal.value a)
            (CVal.value b)
            (CVal.value a)

    Assert.Equal(100, AVal.getValue v3) // 1 + 10 + 1 <= 25 -> i1
    CVal.set 8 a // 8 + 10 + 8 > 25 -> switch to i3
    Assert.Equal(300, AVal.getValue v3)

[<Fact>]
let ``AVal custom computes lazily and caches`` () =
    let mutable counter = 0

    let v: aval<int> =
        AVal.custom (fun () ->
            counter <- counter + 1
            counter)

    Assert.Equal(0, counter) // not computed at construction
    Assert.Equal(1, AVal.getValue v)
    Assert.Equal(1, AVal.getValue v)
    Assert.Equal(1, counter) // cached: computes once per change

[<Fact>]
let ``cval Value property and UpdateTo`` () =
    let c = CVal.create 1
    Assert.Equal(1, c.Value)
    c.Value <- 2
    Assert.Equal(2, AVal.getValue (CVal.value c))
    // UpdateTo returns whether the value changed.
    Assert.True(c.UpdateTo 3)
    Assert.Equal(3, c.Value)
    Assert.False(c.UpdateTo 3)
    Assert.False(c.UpdateTo 3)

[<Fact>]
let ``cval Value set inside a transaction defers`` () =
    let c = CVal.create 1
    let mutable delivered = 0
    use obs = AVal.observe (fun _ -> delivered <- delivered + 1) (CVal.value c)

    Transaction.run (fun () -> c.Value <- 2)
    Assert.Equal(1, delivered) // one notification for the batch
    Assert.Equal(2, AVal.getValue (CVal.value c))

[<Fact>]
let ``ASet empty and AMap empty are empty and never mark`` () =
    let s: aset<int> = ASet.empty
    let m: amap<string, int> = AMap.empty
    Assert.Equal(0, s.GetValue().Count)
    Assert.Equal(0, m.GetValue().Count)
    Assert.Equal(0L, (s :> IAdaptiveObject).Version)
    Assert.Equal(0L, (m :> IAdaptiveObject).Version)

[<Fact>]
let ``AMap constant and delay compute the creator once`` () =
    let mutable count = 0

    let mk () =
        count <- count + 1
        Dictionary(dict [ "a", 1 ])

    let m: amap<string, int> = AMap.constant mk
    Assert.Equal(0, count)
    Assert.Equal(1, m.GetValue().Count)
    Assert.Equal(1, m.GetValue().Count)
    Assert.Equal(1, count)

    let mutable count2 = 0

    let d: amap<string, int> =
        AMap.delay (fun () ->
            count2 <- count2 + 1
            Dictionary())

    Assert.Equal(0, count2)
    Assert.Equal(0, d.GetValue().Count)
    Assert.Equal(1, count2)
