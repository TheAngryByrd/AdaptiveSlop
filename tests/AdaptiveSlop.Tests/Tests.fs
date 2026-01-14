#nowarn "893"

module AdaptiveSlop.Tests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open global.Xunit
open AdaptiveSlop.Core


let private runParallel taskCount iterations (work: int -> unit) =
    let errors = ConcurrentQueue<exn>()
    let tasks =
        Array.init taskCount (fun workerId ->
            Task.Run(fun () ->
                try
                    for i in 1..iterations do
                        work (workerId * iterations + i)
                with ex ->
                    errors.Enqueue(ex)))

    Task.WaitAll(tasks)
    errors

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
            |> List.fold (fun acc child ->
                let right = buildAdaptive child
                AVal.map2 (fun leftValue rightValue -> leftValue + rightValue) acc right
            ) initial

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
            Leaf (CVal.create (nextValue ()))
        else
            let left = build (currentDepth - 1)
            let right = build (currentDepth - 1)
            Node [ left; right ]

    build depth

[<Fact>]
let ``ChangeableValue supports concurrent write/read`` () =
    let changeable = CVal.create 0
    let adaptive = AVal.map (fun value -> value + 1) (CVal.value changeable)

    let errors =
        runParallel 8 5000 (fun value ->
            changeable.Set(value)
            let _ = AVal.getValue adaptive
            ())

    Assert.True(errors.IsEmpty, $"Errors found: {errors.Count}")
    let result = AVal.getValue adaptive
    Assert.InRange(result, 1, 8 * 5000 + 1)

[<Fact>]
let ``ChangeableSet supports concurrent adds`` () =
    let changeable = CSet.empty<int>

    let errors =
        runParallel 8 2000 (fun value ->
            changeable.Add(value))

    Assert.True(errors.IsEmpty, $"Errors found: {errors.Count}")
    let result = ASet.getValue (CSet.value changeable)
    Assert.Equal(8 * 2000, result.Count)

[<Fact>]
let ``ChangeableMap supports concurrent updates`` () =
    let changeable = CMap.empty<int, int>

    let errors =
        runParallel 8 2000 (fun value ->
            changeable.AddOrUpdate(value, value * 2))

    Assert.True(errors.IsEmpty, $"Errors found: {errors.Count}")
    let result = AMap.getValue (CMap.value changeable)
    Assert.Equal(8 * 2000, result.Count)

[<Fact>]
let ``AdaptiveNode recomputes safely under concurrency`` () =
    let changeable = CVal.create 0
    let adaptive = AVal.map (fun value -> value * 2) (CVal.value changeable)

    let errors =
        runParallel 8 5000 (fun value ->
            if value % 2 = 0 then
                changeable.Set(value)
            let _ = AVal.getValue adaptive
            ())

    Assert.True(errors.IsEmpty, $"Errors found: {errors.Count}")

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
        AVal.map (fun v ->
            recomputeCount <- recomputeCount + 1
            v * 2) (CVal.value input)

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
    let taskValue = AVal.mapValueTask (fun v -> ValueTask<int>(v + 2)) (CVal.value source)
    let mapped = AVal.mapValueTaskResult (fun v -> v * 4) taskValue
    let bound = AVal.bindValueTaskResult (fun v -> ValueTask<int>(v + 1)) mapped

    let initial = AVal.getValue bound
    Assert.Equal(13, initial.Result)
    source.Set(3)
    let updated = AVal.getValue bound
    Assert.Equal(21, updated.Result)

[<Fact>]
let ``ASet union matches expected output`` () =
    let left = CSet.ofSeq [1; 2; 3]
    let right = CSet.ofSeq [3; 4]

    let unioned = ASet.union (CSet.value left) (CSet.value right)
    let expectedInitial: Set<int> = Set.ofList [1; 2; 3; 4]
    Assert.Equal<Set<int>>(expectedInitial, ASet.getValue unioned)

    right.Add(5)
    let expectedAfterAdd: Set<int> = Set.ofList [1; 2; 3; 4; 5]
    Assert.Equal<Set<int>>(expectedAfterAdd, ASet.getValue unioned)

[<Fact>]
let ``ASet map and filter`` () =
    let source = CSet.ofSeq [1; 2; 3; 4]
    let mapped = ASet.map (fun v -> v * 2) (CSet.value source)
    let filtered = ASet.filter (fun v -> v > 4) mapped

    let expectedInitial: Set<int> = Set.ofList [6; 8]
    Assert.Equal<Set<int>>(expectedInitial, ASet.getValue filtered)

    source.Add(5)
    let expectedAfterAdd: Set<int> = Set.ofList [6; 8; 10]
    Assert.Equal<Set<int>>(expectedAfterAdd, ASet.getValue filtered)

    source.Remove(4)
    let expectedAfterRemove: Set<int> = Set.ofList [6; 10]
    Assert.Equal<Set<int>>(expectedAfterRemove, ASet.getValue filtered)

[<Fact>]
let ``AMap map and filter`` () =
    let source = CMap.ofSeq [1, 10; 2, 20; 3, 30]
    let mapped = AMap.map (fun _ v -> v + 1) (CMap.value source)
    let filtered = AMap.filter (fun _ v -> v > 15) mapped

    let expectedInitial: Map<int, int> = Map.ofList [2, 21; 3, 31]
    Assert.Equal<Map<int, int>>(expectedInitial, AMap.getValue filtered)

    source.AddOrUpdate(4, 40)
    let expectedAfterAdd: Map<int, int> = Map.ofList [2, 21; 3, 31; 4, 41]
    Assert.Equal<Map<int, int>>(expectedAfterAdd, AMap.getValue filtered)

    source.Remove(3)
    let expectedAfterRemove: Map<int, int> = Map.ofList [2, 21; 4, 41]
    Assert.Equal<Map<int, int>>(expectedAfterRemove, AMap.getValue filtered)

[<Fact>]
let ``Transaction defers ChangeableValue updates`` () =
    let value = CVal.create 1

    Transaction.run (fun () ->
        value.Set(5)
        Assert.Equal(1, AVal.getValue (CVal.value value))
    ) |> ignore

    Assert.Equal(5, AVal.getValue (CVal.value value))

[<Fact>]
let ``Transaction nesting defers updates until outer commit`` () =
    let value = CVal.create 1

    Transaction.run (fun () ->
        value.Set(2)
        Transaction.run (fun () ->
            value.Set(3)
        ) |> ignore
        Assert.Equal(1, AVal.getValue (CVal.value value))
    ) |> ignore

    Assert.Equal(3, AVal.getValue (CVal.value value))

[<Fact>]
let ``Transaction rollback on exception`` () =
    let value = CVal.create 1

    Assert.Throws<exn>(fun () ->
        Transaction.run (fun () ->
            value.Set(5)
            failwith "boom"
        ) |> ignore
    ) |> ignore

    Assert.Equal(1, AVal.getValue (CVal.value value))

[<Fact>]
let ``Transaction batches set updates`` () =
    let setValue = CSet.ofSeq [1; 2]

    Transaction.run (fun () ->
        setValue.Add(3)
        setValue.Remove(1)
        let expectedDuring: Set<int> = Set.ofList [1; 2]
        Assert.Equal<Set<int>>(expectedDuring, ASet.getValue (CSet.value setValue))
    ) |> ignore

    let expectedAfter: Set<int> = Set.ofList [2; 3]
    Assert.Equal<Set<int>>(expectedAfter, ASet.getValue (CSet.value setValue))

[<Fact>]
let ``Transaction batches map updates`` () =
    let mapValue = CMap.ofSeq [1, 10; 2, 20]

    Transaction.run (fun () ->
        mapValue.AddOrUpdate(3, 30)
        mapValue.Remove(1)
        let expectedDuring: Map<int, int> = Map.ofList [1, 10; 2, 20]
        Assert.Equal<Map<int, int>>(expectedDuring, AMap.getValue (CMap.value mapValue))
    ) |> ignore

    let expectedAfter: Map<int, int> = Map.ofList [2, 20; 3, 30]
    Assert.Equal<Map<int, int>>(expectedAfter, AMap.getValue (CMap.value mapValue))

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
    let mapped = AVal.mapValueTask (fun (v: int) -> ValueTask<int>(v * v)) (CVal.value input)

    let initial = AVal.getValue mapped
    Assert.Equal(4, initial.Result)

    input.Set(3)
    input.Set(5)
    let updated = AVal.getValue mapped
    Assert.Equal(25, updated.Result)

[<Fact>]
let ``ASet union updates with add/remove`` () =
    let left = CSet.ofSeq [1; 2]
    let right = CSet.ofSeq [2; 3]

    let unioned = ASet.union (CSet.value left) (CSet.value right)
    let expectedInitial: Set<int> = Set.ofList [1; 2; 3]
    Assert.Equal<Set<int>>(expectedInitial, ASet.getValue unioned)

    left.Remove(2)
    right.Add(4)
    let expectedAfterFirst: Set<int> = Set.ofList [1; 2; 3; 4]
    Assert.Equal<Set<int>>(expectedAfterFirst, ASet.getValue unioned)

    right.Remove(2)
    left.Add(5)
    let expectedAfterSecond: Set<int> = Set.ofList [1; 3; 4; 5]
    Assert.Equal<Set<int>>(expectedAfterSecond, ASet.getValue unioned)

[<Fact>]
let ``AMap map and filter respond to updates`` () =
    let source = CMap.ofSeq [1, 10; 2, 20]
    let mapped = AMap.map (fun _ v -> v + 5) (CMap.value source)
    let filtered = AMap.filter (fun _ v -> v > 20) mapped

    let expectedInitial: Map<int, int> = Map.ofList [2, 25]
    Assert.Equal<Map<int, int>>(expectedInitial, AMap.getValue filtered)

    source.AddOrUpdate(2, 12)
    let expectedAfterUpdate: Map<int, int> = Map.empty
    Assert.Equal<Map<int, int>>(expectedAfterUpdate, AMap.getValue filtered)

    source.AddOrUpdate(1, 30)
    let expectedAfterSecond: Map<int, int> = Map.ofList [1, 35]
    Assert.Equal<Map<int, int>>(expectedAfterSecond, AMap.getValue filtered)

[<Fact>]
let ``Transaction applies last value update`` () =
    let value = CVal.create 1

    Transaction.run (fun () ->
        value.Set(2)
        value.Set(5)
        Assert.Equal(1, AVal.getValue (CVal.value value))
    ) |> ignore

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
    let source = CSet.ofSeq [1; 2]
    let mapped = ASet.map (fun v -> v + 1) (CSet.value source)

    let expectedInitial: Set<int> = Set.ofList [2; 3]
    Assert.Equal<Set<int>>(expectedInitial, ASet.getValue mapped)

    CSet.set (Set.ofList [3; 4]) source
    let expectedAfter: Set<int> = Set.ofList [4; 5]
    Assert.Equal<Set<int>>(expectedAfter, ASet.getValue mapped)

[<Fact>]
let ``AMap map responds to CMap.set`` () =
    let source = CMap.ofSeq [1, 10; 2, 20]
    let mapped = AMap.map (fun key value -> value + key) (CMap.value source)

    let expectedInitial: Map<int, int> = Map.ofList [1, 11; 2, 22]
    Assert.Equal<Map<int, int>>(expectedInitial, AMap.getValue mapped)

    CMap.set (Map.ofList [2, 5; 3, 7]) source
    let expectedAfter: Map<int, int> = Map.ofList [2, 7; 3, 10]
    Assert.Equal<Map<int, int>>(expectedAfter, AMap.getValue mapped)

[<Fact>]
let ``Transaction defers CSet.set in unions`` () =
    let left = CSet.ofSeq [1; 2]
    let right = CSet.ofSeq [2; 3]
    let unioned = ASet.union (CSet.value left) (CSet.value right)

    Transaction.run (fun () ->
        CSet.set (Set.ofList [5]) left
        CSet.set (Set.ofList [6]) right
        let expectedDuring: Set<int> = Set.ofList [1; 2; 3]
        Assert.Equal<Set<int>>(expectedDuring, ASet.getValue unioned)
    ) |> ignore

    let expectedAfter: Set<int> = Set.ofList [5; 6]
    Assert.Equal<Set<int>>(expectedAfter, ASet.getValue unioned)

[<Fact>]
let ``ASet union preserves duplicates until fully removed`` () =
    let left = CSet.ofSeq [1; 2]
    let right = CSet.ofSeq [2; 3]
    let unioned = ASet.union (CSet.value left) (CSet.value right)

    let expectedInitial: Set<int> = Set.ofList [1; 2; 3]
    Assert.Equal<Set<int>>(expectedInitial, ASet.getValue unioned)

    left.Remove(2)
    let expectedAfterLeft: Set<int> = Set.ofList [1; 2; 3]
    Assert.Equal<Set<int>>(expectedAfterLeft, ASet.getValue unioned)

    right.Remove(2)
    let expectedAfterRight: Set<int> = Set.ofList [1; 3]
    Assert.Equal<Set<int>>(expectedAfterRight, ASet.getValue unioned)

[<Fact>]
let ``AMap filter ignores non-matching updates`` () =
    let source = CMap.ofSeq [1, 5; 2, 20]
    let filtered = AMap.filter (fun _ value -> value > 10) (CMap.value source)

    let expectedInitial: Map<int, int> = Map.ofList [2, 20]
    Assert.Equal<Map<int, int>>(expectedInitial, AMap.getValue filtered)

    source.AddOrUpdate(1, 8)
    source.AddOrUpdate(3, 9)
    let expectedAfter: Map<int, int> = Map.ofList [2, 20]
    Assert.Equal<Map<int, int>>(expectedAfter, AMap.getValue filtered)

[<Fact>]
let ``Transaction defers updates across multiple values`` () =
    let first = CVal.create 1
    let second = CVal.create 10

    Transaction.run (fun () ->
        first.Set(2)
        second.Set(20)
        Assert.Equal(1, AVal.getValue (CVal.value first))
        Assert.Equal(10, AVal.getValue (CVal.value second))
    ) |> ignore

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
    let left = CSet.ofSeq [1; 2]
    let right = CSet.ofSeq []
    let unioned = ASet.union (CSet.value left) (CSet.value right)

    let expectedInitial: Set<int> = Set.ofList [1; 2]
    Assert.Equal<Set<int>>(expectedInitial, ASet.getValue unioned)

    right.Add(3)
    let expectedAfterAdd: Set<int> = Set.ofList [1; 2; 3]
    Assert.Equal<Set<int>>(expectedAfterAdd, ASet.getValue unioned)

    left.Remove(1)
    let expectedAfterRemove: Set<int> = Set.ofList [2; 3]
    Assert.Equal<Set<int>>(expectedAfterRemove, ASet.getValue unioned)

[<Fact>]
let ``AMap filter updates on removals`` () =
    let source = CMap.ofSeq [1, 10; 2, 20; 3, 30]
    let filtered = AMap.filter (fun _ value -> value >= 20) (CMap.value source)

    let expectedInitial: Map<int, int> = Map.ofList [2, 20; 3, 30]
    Assert.Equal<Map<int, int>>(expectedInitial, AMap.getValue filtered)

    source.Remove(3)
    let expectedAfterRemove: Map<int, int> = Map.ofList [2, 20]
    Assert.Equal<Map<int, int>>(expectedAfterRemove, AMap.getValue filtered)

    source.AddOrUpdate(1, 25)
    let expectedAfterUpdate: Map<int, int> = Map.ofList [1, 25; 2, 20]
    Assert.Equal<Map<int, int>>(expectedAfterUpdate, AMap.getValue filtered)

[<Fact>]
let ``Transaction defers set and map together`` () =
    let setValue = CSet.ofSeq [1]
    let mapValue = CMap.ofSeq [1, 1]

    Transaction.run (fun () ->
        setValue.Add(2)
        mapValue.AddOrUpdate(2, 2)

        let expectedSet: Set<int> = Set.ofList [1]
        let expectedMap: Map<int, int> = Map.ofList [1, 1]
        Assert.Equal<Set<int>>(expectedSet, ASet.getValue (CSet.value setValue))
        Assert.Equal<Map<int, int>>(expectedMap, AMap.getValue (CMap.value mapValue))
    ) |> ignore

    let expectedSetAfter: Set<int> = Set.ofList [1; 2]
    let expectedMapAfter: Map<int, int> = Map.ofList [1, 1; 2, 2]
    Assert.Equal<Map<int, int>>(expectedMapAfter, AMap.getValue (CMap.value mapValue))

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
    let source = CSet.ofSeq [1; 3]
    let mapped = ASet.map (fun v -> v * 10) (CSet.value source)

    let expectedInitial: Set<int> = Set.ofList [10; 30]
    Assert.Equal<Set<int>>(expectedInitial, ASet.getValue mapped)

    source.Add(2)
    source.Remove(1)
    let expectedAfter: Set<int> = Set.ofList [20; 30]
    Assert.Equal<Set<int>>(expectedAfter, ASet.getValue mapped)

[<Fact>]
let ``AMap filter removes when threshold increases`` () =
    let source = CMap.ofSeq [1, 5; 2, 15; 3, 25]
    let filtered = AMap.filter (fun _ value -> value >= 10) (CMap.value source)

    let expectedInitial: Map<int, int> = Map.ofList [2, 15; 3, 25]
    Assert.Equal<Map<int, int>>(expectedInitial, AMap.getValue filtered)

    source.AddOrUpdate(2, 8)
    let expectedAfter: Map<int, int> = Map.ofList [3, 25]
    Assert.Equal<Map<int, int>>(expectedAfter, AMap.getValue filtered)

[<Fact>]
let ``Transaction defers map set updates`` () =
    let mapValue = CMap.ofSeq [1, 1; 2, 2]

    Transaction.run (fun () ->
        CMap.set (Map.ofList [3, 3]) mapValue
        let expectedDuring: Map<int, int> = Map.ofList [1, 1; 2, 2]
        Assert.Equal<Map<int, int>>(expectedDuring, AMap.getValue (CMap.value mapValue))
    ) |> ignore

    let expectedAfter: Map<int, int> = Map.ofList [3, 3]
    Assert.Equal<Map<int, int>>(expectedAfter, AMap.getValue (CMap.value mapValue))

[<FsCheck.Xunit.Property(MaxTest = 200)>]
let ``Deep dependency trees propagate updates`` (depth: FsCheck.PositiveInt) (updates: int list) =
    let actualDepth = min 6 depth.Get
    let values = if updates.IsEmpty then [ 0 ] else updates
    let tree = buildTree actualDepth values
    let root = buildAdaptive tree
    let leaves = collectLeaves tree

    let sumLeaves () =
        leaves
        |> List.sumBy (fun leaf -> AVal.getValue (leaf :> IAdaptiveValue<int>))

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
// Concurrency Hazard Tests
// =============================================================================

/// Test 1: Reentrancy test for nested dependency collection
/// Build a graph where AVal.map of one node reads another node that itself
/// triggers dependency collection; run in parallel with updates.
/// This exposes issues where the thread-static collector can be swapped
/// during nested recompute calls.
[<Fact>]
let ``Nested dependency collection handles reentrancy under concurrency`` () =
    // Create a chain: leaf -> middle -> outer
    // where middle's compute reads leaf, and outer's compute reads both middle AND leaf
    // This creates nested GetValue calls during a single Recompute
    let leaf = CVal.create 1
    
    // Middle node reads leaf
    let middle = AVal.map (fun v -> v * 2) (CVal.value leaf)
    
    // Outer node reads BOTH middle (which triggers leaf read) AND leaf directly
    // This creates nested dependency collection: outer.GetValue -> middle.GetValue -> leaf.GetValue
    let outer = AVal.map2 (fun m l -> m + l) middle (CVal.value leaf)
    
    // Add another layer to stress reentrancy further
    let deep = 
        AVal.bind (fun o -> 
            // During this bind's compute, we read leaf again
            AVal.map (fun l -> o + l) (CVal.value leaf)) outer
    
    let errors = ConcurrentQueue<exn>()
    let stabilized = ConcurrentQueue<int>()
    let iterations = 3000
    
    let readerTask = Task.Run(fun () ->
        try
            for _ in 1..iterations do
                let v = AVal.getValue deep
                stabilized.Enqueue(v)
        with ex ->
            errors.Enqueue(ex))
    
    let writerTask = Task.Run(fun () ->
        try
            for i in 1..iterations do
                leaf.Set(i)
        with ex ->
            errors.Enqueue(ex))
    
    Task.WaitAll([| readerTask; writerTask |])
    
    Assert.True(errors.IsEmpty, $"Errors during nested collection: {errors.Count}")
    
    // Verify final value is consistent
    let finalLeaf = AVal.getValue (CVal.value leaf)
    let expectedFinal = (finalLeaf * 2) + finalLeaf + finalLeaf // middle + leaf + leaf
    let actualFinal = AVal.getValue deep
    Assert.Equal(expectedFinal, actualFinal)

/// Test 2: Cross-thread transaction test
/// Start Transaction.run on one thread, mutate in another thread.
/// This exposes the thread-local nature of transactions - updates on other
/// threads bypass the transaction and apply immediately.
[<Fact>]
let ``Cross-thread updates bypass thread-local transaction`` () =
    let value1 = CVal.create 0
    let value2 = CVal.create 0
    let observedDuringTransaction = ConcurrentQueue<int * int>()
    let transactionStarted = new ManualResetEventSlim(false)
    let updateDone = new ManualResetEventSlim(false)
    
    // Thread 1: Start a transaction and wait for thread 2 to mutate
    let txThread = Task.Run(fun () ->
        Transaction.run (fun () ->
            value1.Set(100) // This should be deferred
            transactionStarted.Set()
            
            // Wait for the other thread to mutate value2
            updateDone.Wait()
            
            // Read both values INSIDE the transaction
            let v1 = AVal.getValue (CVal.value value1)
            let v2 = AVal.getValue (CVal.value value2)
            observedDuringTransaction.Enqueue((v1, v2))
        ))
    
    // Thread 2: Wait for transaction to start, then mutate
    let updateThread = Task.Run(fun () ->
        transactionStarted.Wait()
        // This update happens outside the transaction context (different thread)
        // so it should apply IMMEDIATELY, not be deferred
        value2.Set(200)
        updateDone.Set())
    
    Task.WaitAll([| txThread; updateThread |])
    
    // The key observation: value1.Set(100) should be deferred (seen as 0 inside tx)
    // but value2.Set(200) from another thread should apply immediately (seen as 200)
    let observed = observedDuringTransaction.ToArray()
    Assert.Single(observed) |> ignore
    let (v1Observed, v2Observed) = observed[0]
    
    // value1 should still be 0 inside the transaction (deferred)
    Assert.Equal(0, v1Observed)
    // value2 should be 200 because the cross-thread update bypassed the transaction
    Assert.Equal(200, v2Observed)
    
    // After transaction commits, value1 should be 100
    Assert.Equal(100, AVal.getValue (CVal.value value1))
    Assert.Equal(200, AVal.getValue (CVal.value value2))

/// Test 3: Concurrent ChangeableSet read/write stress test
/// Spin multiple reader threads calling AVal.getValue while a writer thread
/// mutates rapidly. Assert no exceptions and final state consistency.
[<Fact>]
let ``ChangeableSet concurrent rapid read/write stress test`` () =
    let changeable = CSet.ofSeq [0]
    let adaptive = CSet.value changeable
    let mapped = ASet.map (fun v -> v * 2) adaptive
    let filtered = ASet.filter (fun v -> v % 4 = 0) mapped
    
    let errors = ConcurrentQueue<exn>()
    let readerSnapshots = ConcurrentQueue<Set<int>>()
    let writerIterations = 5000
    let readerCount = 4
    
    // Writer thread: rapidly add and remove items
    let writerTask = Task.Run(fun () ->
        try
            for i in 1..writerIterations do
                changeable.Add(i)
                if i > 10 then
                    changeable.Remove(i - 10)
        with ex ->
            errors.Enqueue(ex))
    
    // Multiple reader threads: constantly read the filtered set
    let readerTasks = Array.init readerCount (fun _ ->
        Task.Run(fun () ->
            try
                for _ in 1..(writerIterations / 2) do
                    let snapshot = ASet.getValue filtered
                    readerSnapshots.Enqueue(snapshot)
            with ex ->
                errors.Enqueue(ex)))
    
    Task.WaitAll(Array.append [| writerTask |] readerTasks)
    
    Assert.True(errors.IsEmpty, $"Errors during concurrent Set access: {errors.Count}")
    
    // Verify final state is consistent
    let finalSet = ASet.getValue adaptive
    let finalFiltered = ASet.getValue filtered
    let expectedFiltered = finalSet |> Set.map (fun v -> v * 2) |> Set.filter (fun v -> v % 4 = 0)
    Assert.Equal<Set<int>>(expectedFiltered, finalFiltered)

/// Test 3b: Concurrent ChangeableMap read/write stress test
[<Fact>]
let ``ChangeableMap concurrent rapid read/write stress test`` () =
    let changeable = CMap.ofSeq [0, 0]
    let adaptive = CMap.value changeable
    let mapped = AMap.map (fun k v -> v + k) adaptive
    let filtered = AMap.filter (fun _ v -> v > 5) mapped
    
    let errors = ConcurrentQueue<exn>()
    let readerSnapshots = ConcurrentQueue<Map<int, int>>()
    let writerIterations = 5000
    let readerCount = 4
    
    // Writer thread: rapidly add and remove items
    let writerTask = Task.Run(fun () ->
        try
            for i in 1..writerIterations do
                changeable.AddOrUpdate(i, i * 2)
                if i > 10 then
                    changeable.Remove(i - 10)
        with ex ->
            errors.Enqueue(ex))
    
    // Multiple reader threads: constantly read the filtered map
    let readerTasks = Array.init readerCount (fun _ ->
        Task.Run(fun () ->
            try
                for _ in 1..(writerIterations / 2) do
                    let snapshot = AMap.getValue filtered
                    readerSnapshots.Enqueue(snapshot)
            with ex ->
                errors.Enqueue(ex)))
    
    Task.WaitAll(Array.append [| writerTask |] readerTasks)
    
    Assert.True(errors.IsEmpty, $"Errors during concurrent Map access: {errors.Count}")
    
    // Verify final state is consistent
    let finalMap = AMap.getValue adaptive
    let finalFiltered = AMap.getValue filtered
    let expectedFiltered = finalMap |> Map.map (fun k v -> v + k) |> Map.filter (fun _ v -> v > 5)
    Assert.Equal<Map<int, int>>(expectedFiltered, finalFiltered)

/// Test 4: Timer race regression test - version monotonicity
/// Use two timers with different periods; track render output version
/// and ensure it never regresses. This detects inconsistent snapshots.
[<Fact>]
let ``Timer race does not cause version regression`` () =
    let tickValue = CVal.create 0
    let statusValue = CVal.create "init"
    
    // Composite view that reads both values
    let compositeView = 
        AVal.map2 (fun tick status -> $"{status}:{tick}") (CVal.value tickValue) (CVal.value statusValue)
    
    let errors = ConcurrentQueue<string>()
    let versionHistory = ConcurrentQueue<int64>()
    let stopSignal = new CancellationTokenSource()
    let testDuration = TimeSpan.FromMilliseconds(500)
    
    // Fast tick timer (every 5ms)
    let tickTimer = Task.Run(fun () ->
        let mutable counter = 0
        while not stopSignal.Token.IsCancellationRequested do
            counter <- counter + 1
            tickValue.Set(counter)
            Thread.Sleep(5))
    
    // Slow status timer (every 17ms - prime to avoid sync)
    let statusTimer = Task.Run(fun () ->
        let statuses = [| "running"; "idle"; "busy"; "waiting" |]
        let mutable idx = 0
        while not stopSignal.Token.IsCancellationRequested do
            idx <- (idx + 1) % statuses.Length
            statusValue.Set(statuses[idx])
            Thread.Sleep(17))
    
    // Render loop (every 33ms - ~30fps)
    let renderTask = Task.Run(fun () ->
        let mutable lastVersion = -1L
        while not stopSignal.Token.IsCancellationRequested do
            let currentVersion = (compositeView :> IAdaptiveObject).Version
            versionHistory.Enqueue(currentVersion)
            
            // Check for version regression
            if currentVersion < lastVersion then
                errors.Enqueue($"Version regressed from {lastVersion} to {currentVersion}")
            lastVersion <- currentVersion
            
            // Also read the value to trigger recompute
            let _ = AVal.getValue compositeView
            Thread.Sleep(33))
    
    // Let it run
    Thread.Sleep(int testDuration.TotalMilliseconds)
    stopSignal.Cancel()
    
    try
        Task.WaitAll([| tickTimer; statusTimer; renderTask |], TimeSpan.FromSeconds(2)) |> ignore
    with
    | :? AggregateException -> () // Expected due to cancellation
    
    let errorMsg = String.Join("; ", errors)
    Assert.True(errors.IsEmpty, $"Version regressions detected: {errorMsg}")
    
    // Verify we actually ran multiple iterations
    Assert.True(versionHistory.Count > 5, $"Too few render iterations: {versionHistory.Count}")
    
    // Verify versions are monotonically non-decreasing
    let versions = versionHistory.ToArray()
    let regressions = 
        versions 
        |> Array.pairwise 
        |> Array.filter (fun (prev, curr) -> curr < prev)
    Assert.True(regressions.Length = 0, $"Found {regressions.Length} version regressions")

/// Test 4b: Deep nested graph with concurrent updates and reads
/// Tests version consistency across a complex dependency graph
[<Fact>]
let ``Deep graph maintains version consistency under concurrent updates`` () =
    // Create a 3-level deep graph
    let sources = Array.init 4 (fun i -> CVal.create i)
    
    // Level 1: Combine pairs
    let level1 = [|
        AVal.map2 (+) (CVal.value sources[0]) (CVal.value sources[1])
        AVal.map2 (+) (CVal.value sources[2]) (CVal.value sources[3])
    |]
    
    // Level 2: Combine level 1
    let level2 = AVal.map2 (+) level1[0] level1[1]
    
    // Level 3: Map the result
    let root = AVal.map (fun v -> v * 2) level2
    
    let errors = ConcurrentQueue<string>()
    let readValues = ConcurrentQueue<int>()
    let iterations = 3000
    
    // Writer tasks: update sources concurrently
    let writerTasks = sources |> Array.mapi (fun idx source ->
        Task.Run(fun () ->
            try
                for i in 1..iterations do
                    source.Set(i + idx * 1000)
            with ex ->
                errors.Enqueue($"Writer {idx} error: {ex.Message}")))
    
    // Reader task: continuously read the root
    let readerTask = Task.Run(fun () ->
        try
            for _ in 1..(iterations * 2) do
                let v = AVal.getValue root
                readValues.Enqueue(v)
        with ex ->
            errors.Enqueue($"Reader error: {ex.Message}"))
    
    Task.WaitAll(Array.append writerTasks [| readerTask |])
    
    let errorMsg = String.Join("; ", errors)
    Assert.True(errors.IsEmpty, $"Errors: {errorMsg}")
    
    // Verify final consistency
    let finalSources = sources |> Array.map (fun s -> AVal.getValue (CVal.value s))
    let expectedFinal = (finalSources |> Array.sum) * 2
    let actualFinal = AVal.getValue root
    Assert.Equal(expectedFinal, actualFinal)

/// Test 5: Snapshot thrash test - rapidly invalidate while building snapshot
[<Fact>]
let ``ChangeableSet snapshot building handles rapid invalidation`` () =
    let changeable = CSet.ofSeq (seq { 1..100 })
    let adaptive = CSet.value changeable
    
    let errors = ConcurrentQueue<exn>()
    let snapshots = ConcurrentQueue<int>() // Store snapshot sizes
    let iterations = 2000
    
    // Invalidator thread: rapidly add/remove to invalidate snapshots
    let invalidatorTask = Task.Run(fun () ->
        try
            for i in 1..iterations do
                changeable.Add(1000 + i)
                changeable.Remove(1000 + i - 1)
                // Also do bulk replace occasionally
                if i % 100 = 0 then
                    let current = ASet.getValue adaptive
                    changeable.Set(current) // Replace with same value
        with ex ->
            errors.Enqueue(ex))
    
    // Reader threads: try to get snapshots during rapid invalidation
    let readerTasks = Array.init 3 (fun _ ->
        Task.Run(fun () ->
            try
                for _ in 1..iterations do
                    let snapshot = ASet.getValue adaptive
                    snapshots.Enqueue(snapshot.Count)
            with ex ->
                errors.Enqueue(ex)))
    
    Task.WaitAll(Array.append [| invalidatorTask |] readerTasks)
    
    Assert.True(errors.IsEmpty, $"Errors during snapshot thrash: {errors.Count}")
    
    // All snapshots should have had positive counts
    let allSnapshots = snapshots.ToArray()
    let invalidSnapshots = allSnapshots |> Array.filter (fun c -> c <= 0)
    Assert.True(invalidSnapshots.Length = 0, $"Found {invalidSnapshots.Length} empty/invalid snapshots")

