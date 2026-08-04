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
    let cs = source :> AdaptiveSlop.Core.ChangeableSet<int>
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
