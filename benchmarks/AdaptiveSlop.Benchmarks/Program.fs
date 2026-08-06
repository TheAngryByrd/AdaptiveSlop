module AdaptiveSlop.Benchmarks

open System.Threading
open System.Threading.Tasks
open System.Collections.Generic
open BenchmarkDotNet.Attributes
open BenchmarkDotNet.Running
open FSharp.Data.Adaptive

// =============================================================================
// Basic Value Benchmarks
// =============================================================================

[<MemoryDiagnoser>]
type ValueBenchmarks() =
    let mutable slopInput: AdaptiveSlop.Core.ChangeableValue<int> =
        Unchecked.defaultof<_>

    let mutable slopMapped: AdaptiveSlop.Core.IAdaptiveValue<int> =
        Unchecked.defaultof<_>

    let mutable fdaInput: cval<int> = Unchecked.defaultof<_>
    let mutable fdaMapped: aval<int> = Unchecked.defaultof<_>

    [<Params(1000)>]
    member val Iterations = 0 with get, set

    [<GlobalSetup>]
    member _.Setup() =
        slopInput <- AdaptiveSlop.Core.CVal.create 0
        slopMapped <- AdaptiveSlop.Core.AVal.map (fun value -> value + 1) (AdaptiveSlop.Core.CVal.value slopInput)
        fdaInput <- cval 0
        fdaMapped <- AVal.map (fun value -> value + 1) fdaInput

    [<Benchmark(Baseline = true)>]
    member this.AdaptiveSlop() =
        for i in 1 .. this.Iterations do
            slopInput.Set(i)
            let _ = AdaptiveSlop.Core.AVal.getValue slopMapped
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        for i in 1 .. this.Iterations do
            transact (fun () -> fdaInput.Value <- i)
            let _ = AVal.force fdaMapped
            ()

// =============================================================================
// Deep Dependency Chain Benchmark
// =============================================================================

[<MemoryDiagnoser>]
type DeepChainBenchmarks() =
    let mutable slopInput: AdaptiveSlop.Core.ChangeableValue<int> =
        Unchecked.defaultof<_>

    let mutable slopChain: AdaptiveSlop.Core.IAdaptiveValue<int> =
        Unchecked.defaultof<_>

    let mutable fdaInput: cval<int> = Unchecked.defaultof<_>
    let mutable fdaChain: aval<int> = Unchecked.defaultof<_>

    [<Params(5, 10, 20, 100, 1000)>]
    member val Depth = 0 with get, set

    [<Params(100)>]
    member val Iterations = 0 with get, set

    [<GlobalSetup>]
    member this.Setup() =
        // AdaptiveSlop chain
        slopInput <- AdaptiveSlop.Core.CVal.create 0

        let mutable current: AdaptiveSlop.Core.IAdaptiveValue<int> =
            AdaptiveSlop.Core.CVal.value slopInput

        for _ in 1 .. this.Depth do
            current <- AdaptiveSlop.Core.AVal.map (fun v -> v + 1) current

        slopChain <- current

        // FDA chain
        fdaInput <- cval 0
        let mutable fdaCurrent: aval<int> = fdaInput

        for _ in 1 .. this.Depth do
            fdaCurrent <- AVal.map (fun v -> v + 1) fdaCurrent

        fdaChain <- fdaCurrent

    [<Benchmark(Baseline = true)>]
    member this.AdaptiveSlop() =
        for i in 1 .. this.Iterations do
            slopInput.Set(i)
            let _ = AdaptiveSlop.Core.AVal.getValue slopChain
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        for i in 1 .. this.Iterations do
            transact (fun () -> fdaInput.Value <- i)
            let _ = AVal.force fdaChain
            ()

// =============================================================================
// Map2/Combine Benchmark
// =============================================================================

[<MemoryDiagnoser>]
type Map2Benchmarks() =
    let mutable slopLeft: AdaptiveSlop.Core.ChangeableValue<int> =
        Unchecked.defaultof<_>

    let mutable slopRight: AdaptiveSlop.Core.ChangeableValue<int> =
        Unchecked.defaultof<_>

    let mutable slopCombined: AdaptiveSlop.Core.IAdaptiveValue<int> =
        Unchecked.defaultof<_>

    let mutable fdaLeft: cval<int> = Unchecked.defaultof<_>
    let mutable fdaRight: cval<int> = Unchecked.defaultof<_>
    let mutable fdaCombined: aval<int> = Unchecked.defaultof<_>

    [<Params(1000)>]
    member val Iterations = 0 with get, set

    [<GlobalSetup>]
    member _.Setup() =
        slopLeft <- AdaptiveSlop.Core.CVal.create 0
        slopRight <- AdaptiveSlop.Core.CVal.create 0

        slopCombined <-
            AdaptiveSlop.Core.AVal.map2
                (+)
                (AdaptiveSlop.Core.CVal.value slopLeft)
                (AdaptiveSlop.Core.CVal.value slopRight)

        fdaLeft <- cval 0
        fdaRight <- cval 0
        fdaCombined <- AVal.map2 (+) fdaLeft fdaRight

    [<Benchmark(Baseline = true)>]
    member this.AdaptiveSlop() =
        for i in 1 .. this.Iterations do
            slopLeft.Set(i)
            slopRight.Set(i * 2)
            let _ = AdaptiveSlop.Core.AVal.getValue slopCombined
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        for i in 1 .. this.Iterations do
            transact (fun () ->
                fdaLeft.Value <- i
                fdaRight.Value <- i * 2)

            let _ = AVal.force fdaCombined
            ()

// =============================================================================
// Bind/Dynamic Graph Benchmark
// =============================================================================

[<MemoryDiagnoser>]
type BindBenchmarks() =
    let mutable slopSelector: AdaptiveSlop.Core.ChangeableValue<bool> =
        Unchecked.defaultof<_>

    let mutable slopLeft: AdaptiveSlop.Core.ChangeableValue<int> =
        Unchecked.defaultof<_>

    let mutable slopRight: AdaptiveSlop.Core.ChangeableValue<int> =
        Unchecked.defaultof<_>

    let mutable slopBound: AdaptiveSlop.Core.IAdaptiveValue<int> =
        Unchecked.defaultof<_>

    let mutable fdaSelector: cval<bool> = Unchecked.defaultof<_>
    let mutable fdaLeft: cval<int> = Unchecked.defaultof<_>
    let mutable fdaRight: cval<int> = Unchecked.defaultof<_>
    let mutable fdaBound: aval<int> = Unchecked.defaultof<_>

    [<Params(1000)>]
    member val Iterations = 0 with get, set

    [<GlobalSetup>]
    member _.Setup() =
        slopSelector <- AdaptiveSlop.Core.CVal.create true
        slopLeft <- AdaptiveSlop.Core.CVal.create 1
        slopRight <- AdaptiveSlop.Core.CVal.create 2

        slopBound <-
            AdaptiveSlop.Core.AVal.bind
                (fun sel ->
                    if sel then
                        AdaptiveSlop.Core.CVal.value slopLeft
                    else
                        AdaptiveSlop.Core.CVal.value slopRight)
                (AdaptiveSlop.Core.CVal.value slopSelector)

        fdaSelector <- cval true
        fdaLeft <- cval 1
        fdaRight <- cval 2
        fdaBound <- AVal.bind (fun sel -> if sel then fdaLeft :> aval<_> else fdaRight :> aval<_>) fdaSelector

    [<Benchmark(Baseline = true)>]
    member this.AdaptiveSlop() =
        for i in 1 .. this.Iterations do
            slopSelector.Set(i % 2 = 0)
            slopLeft.Set(i)
            slopRight.Set(i * 2)
            let _ = AdaptiveSlop.Core.AVal.getValue slopBound
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        for i in 1 .. this.Iterations do
            transact (fun () ->
                fdaSelector.Value <- (i % 2 = 0)
                fdaLeft.Value <- i
                fdaRight.Value <- i * 2)

            let _ = AVal.force fdaBound
            ()

// =============================================================================
// Transaction Batching Benchmark
// =============================================================================

[<MemoryDiagnoser>]
type TransactionBenchmarks() =
    let mutable slopValues: AdaptiveSlop.Core.ChangeableValue<int>[] = [||]
    let mutable slopSum: AdaptiveSlop.Core.IAdaptiveValue<int> = Unchecked.defaultof<_>
    let mutable fdaValues: cval<int>[] = [||]
    let mutable fdaSum: aval<int> = Unchecked.defaultof<_>

    [<Params(10)>]
    member val ValueCount = 0 with get, set

    [<Params(500)>]
    member val Iterations = 0 with get, set

    [<GlobalSetup>]
    member this.Setup() =
        // AdaptiveSlop
        slopValues <- Array.init this.ValueCount (fun _ -> AdaptiveSlop.Core.CVal.create 0)

        let mutable sum: AdaptiveSlop.Core.IAdaptiveValue<int> =
            AdaptiveSlop.Core.AVal.constant 0

        for v in slopValues do
            sum <- AdaptiveSlop.Core.AVal.map2 (+) sum (AdaptiveSlop.Core.CVal.value v)

        slopSum <- sum

        // FDA
        fdaValues <- Array.init this.ValueCount (fun _ -> cval 0)
        let mutable fdaSumVal: aval<int> = AVal.constant 0

        for v in fdaValues do
            fdaSumVal <- AVal.map2 (+) fdaSumVal v

        fdaSum <- fdaSumVal

    [<Benchmark(Baseline = true)>]
    member this.AdaptiveSlop_Batched() =
        for i in 1 .. this.Iterations do
            AdaptiveSlop.Core.Transaction.run (fun () ->
                for j in 0 .. slopValues.Length - 1 do
                    slopValues[j].Set(i + j))
            |> ignore

            let _ = AdaptiveSlop.Core.AVal.getValue slopSum
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive_Batched() =
        for i in 1 .. this.Iterations do
            transact (fun () ->
                for j in 0 .. fdaValues.Length - 1 do
                    fdaValues[j].Value <- i + j)

            let _ = AVal.force fdaSum
            ()

// =============================================================================
// Set Benchmarks
// =============================================================================

[<MemoryDiagnoser>]
type SetBenchmarks() =
    let mutable slopSet: AdaptiveSlop.Core.ChangeableSet<int> = Unchecked.defaultof<_>
    let mutable slopASet: AdaptiveSlop.Core.IAdaptiveSet<int> = Unchecked.defaultof<_>
    let mutable fdaSet: cset<int> = Unchecked.defaultof<_>
    let mutable fdaASet: aset<int> = Unchecked.defaultof<_>

    [<Params(1000)>]
    member val Iterations = 0 with get, set

    [<GlobalSetup>]
    member _.Setup() =
        slopSet <- AdaptiveSlop.Core.CSet.empty<int>
        slopASet <- AdaptiveSlop.Core.CSet.value slopSet
        fdaSet <- cset<int> []
        fdaASet <- fdaSet :> aset<int>

    [<Benchmark(Baseline = true)>]
    member this.AdaptiveSlop() =
        for i in 1 .. this.Iterations do
            slopSet.Add(i)
            slopSet.Remove(i)
            let _ = AdaptiveSlop.Core.ASet.getValue slopASet
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        for i in 1 .. this.Iterations do
            transact (fun () ->
                fdaSet.Add(i) |> ignore
                fdaSet.Remove(i) |> ignore)

            let _ = ASet.force fdaASet
            ()

// =============================================================================
// Set Filter/Map Benchmarks
// =============================================================================

[<MemoryDiagnoser>]
type SetTransformBenchmarks() =
    let mutable slopSet: AdaptiveSlop.Core.ChangeableSet<int> = Unchecked.defaultof<_>

    let mutable slopFiltered: AdaptiveSlop.Core.IAdaptiveSet<int> =
        Unchecked.defaultof<_>

    let mutable fdaSet: cset<int> = Unchecked.defaultof<_>
    let mutable fdaFiltered: aset<int> = Unchecked.defaultof<_>

    [<Params(500)>]
    member val Iterations = 0 with get, set

    [<GlobalSetup>]
    member _.Setup() =
        slopSet <- AdaptiveSlop.Core.CSet.ofSeq (seq { 1..100 })

        let mapped =
            AdaptiveSlop.Core.ASet.map (fun v -> v * 2) (AdaptiveSlop.Core.CSet.value slopSet)

        slopFiltered <- AdaptiveSlop.Core.ASet.filter (fun v -> v % 4 = 0) mapped

        fdaSet <- cset (seq { 1..100 })
        let fdaMapped = ASet.map (fun v -> v * 2) fdaSet
        fdaFiltered <- ASet.filter (fun v -> v % 4 = 0) fdaMapped

    [<Benchmark(Baseline = true)>]
    member this.AdaptiveSlop() =
        for i in 1 .. this.Iterations do
            slopSet.Add(1000 + i)
            slopSet.Remove(1000 + i - 1)
            let _ = AdaptiveSlop.Core.ASet.getValue slopFiltered
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        for i in 1 .. this.Iterations do
            transact (fun () ->
                fdaSet.Add(1000 + i) |> ignore
                fdaSet.Remove(1000 + i - 1) |> ignore)

            let _ = ASet.force fdaFiltered
            ()

// =============================================================================
// Map Benchmarks
// =============================================================================

[<MemoryDiagnoser>]
type MapBenchmarks() =
    let mutable slopMap: AdaptiveSlop.Core.ChangeableMap<int, int> =
        Unchecked.defaultof<_>

    let mutable slopAMap: AdaptiveSlop.Core.IAdaptiveMap<int, int> =
        Unchecked.defaultof<_>

    let mutable fdaMap: cmap<int, int> = Unchecked.defaultof<_>
    let mutable fdaAMap: amap<int, int> = Unchecked.defaultof<_>

    [<Params(1000)>]
    member val Iterations = 0 with get, set

    [<GlobalSetup>]
    member _.Setup() =
        slopMap <- AdaptiveSlop.Core.CMap.empty<int, int>
        slopAMap <- AdaptiveSlop.Core.CMap.value slopMap
        fdaMap <- cmap (Seq.empty<int * int>)
        fdaAMap <- fdaMap :> amap<int, int>

    [<Benchmark(Baseline = true)>]
    member this.AdaptiveSlop() =
        for i in 1 .. this.Iterations do
            slopMap.AddOrUpdate i (i * 2)
            slopMap.Remove(i)
            let _ = AdaptiveSlop.Core.AMap.getValue slopAMap
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        for i in 1 .. this.Iterations do
            transact (fun () ->
                fdaMap.[i] <- i * 2
                fdaMap.Remove(i) |> ignore)

            let _ = AMap.force fdaAMap
            ()

// =============================================================================
// Map Filter/Transform Benchmarks
// =============================================================================

[<MemoryDiagnoser>]
type MapTransformBenchmarks() =
    let mutable slopMap: AdaptiveSlop.Core.ChangeableMap<int, int> =
        Unchecked.defaultof<_>

    let mutable slopFiltered: AdaptiveSlop.Core.IAdaptiveMap<int, int> =
        Unchecked.defaultof<_>

    let mutable fdaMap: cmap<int, int> = Unchecked.defaultof<_>
    let mutable fdaFiltered: amap<int, int> = Unchecked.defaultof<_>

    [<Params(500)>]
    member val Iterations = 0 with get, set

    [<GlobalSetup>]
    member _.Setup() =
        slopMap <- AdaptiveSlop.Core.CMap.ofSeq (seq { for i in 1..100 -> i, i * 10 })

        let mapped =
            AdaptiveSlop.Core.AMap.map (fun _ v -> v + 1) (AdaptiveSlop.Core.CMap.value slopMap)

        slopFiltered <- AdaptiveSlop.Core.AMap.filter (fun _ v -> v > 50) mapped

        fdaMap <- cmap (seq { for i in 1..100 -> i, i * 10 })
        let fdaMapped = AMap.map (fun _ v -> v + 1) fdaMap
        fdaFiltered <- AMap.filter (fun _ v -> v > 50) fdaMapped

    [<Benchmark(Baseline = true)>]
    member this.AdaptiveSlop() =
        for i in 1 .. this.Iterations do
            slopMap.AddOrUpdate (1000 + i) ((1000 + i) * 10)
            slopMap.Remove(1000 + i - 1)
            let _ = AdaptiveSlop.Core.AMap.getValue slopFiltered
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        for i in 1 .. this.Iterations do
            transact (fun () ->
                fdaMap.[1000 + i] <- (1000 + i) * 10
                fdaMap.Remove(1000 + i - 1) |> ignore)

            let _ = AMap.force fdaFiltered
            ()

// =============================================================================
// List Benchmarks (docs/ALIST-DESIGN.md)
//
// The write/read benchmark mirrors FDA's CollectionUpdate.CList_Map_GetValue
// (100 appends in one transaction, then force the mapped list). The transform
// and append benchmarks mirror the set benchmarks above. FDA's IndexList/
// Index/ListDelta benchmarks do not apply: those measure the persistent
// structures we deliberately do not have (docs/ALIST-DESIGN.md §2).
// =============================================================================

[<MemoryDiagnoser>]
type ListWriteReadBenchmarks() =
    let mutable slopList: AdaptiveSlop.Core.ChangeableList<int> = Unchecked.defaultof<_>

    let mutable slopMapped: AdaptiveSlop.Core.IAdaptiveList<int> =
        Unchecked.defaultof<_>

    let mutable fdaList: clist<int> = Unchecked.defaultof<_>
    let mutable fdaMapped: alist<int> = Unchecked.defaultof<_>

    [<Params(0, 1000, 10000, 100000)>]
    member val Count = 0 with get, set

    // FDA's CollectionUpdate rebuilds per iteration: the measured op must be
    // stationary (our first run grew the list by 101 appends per iteration,
    // so the force array grew unbounded and the allocation column was
    // meaningless).
    [<IterationSetup>]
    member this.Setup() =
        let data = Array.init this.Count (fun i -> i)
        slopList <- AdaptiveSlop.Core.CList.ofArray data

        slopMapped <- AdaptiveSlop.Core.AList.map (fun i -> i * 2) (AdaptiveSlop.Core.CList.value slopList)

        AdaptiveSlop.Core.AList.force slopMapped |> ignore
        fdaList <- clist data
        fdaMapped <- AList.map (fun i -> i * 2) fdaList
        AList.force fdaMapped |> ignore

    [<Benchmark(Baseline = true)>]
    member this.AdaptiveSlop() =
        AdaptiveSlop.Core.Transaction.run (fun () ->
            for i in 0..100 do
                slopList.Append(i) |> ignore)

        AdaptiveSlop.Core.AList.force slopMapped |> ignore

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        transact (fun () ->
            for i in 0..100 do
                fdaList.Append(i) |> ignore)

        AList.force fdaMapped |> ignore

[<MemoryDiagnoser>]
type ListTransformBenchmarks() =
    let mutable slopList: AdaptiveSlop.Core.ChangeableList<int> = Unchecked.defaultof<_>

    let mutable slopFiltered: AdaptiveSlop.Core.IAdaptiveList<int> =
        Unchecked.defaultof<_>

    let mutable fdaList: clist<int> = Unchecked.defaultof<_>
    let mutable fdaFiltered: alist<int> = Unchecked.defaultof<_>

    [<Params(500)>]
    member val Iterations = 0 with get, set

    [<GlobalSetup>]
    member _.Setup() =
        slopList <- AdaptiveSlop.Core.CList.ofSeq (seq { 1..100 })

        let mapped =
            AdaptiveSlop.Core.AList.map (fun v -> v * 2) (AdaptiveSlop.Core.CList.value slopList)

        slopFiltered <- AdaptiveSlop.Core.AList.filter (fun v -> v % 4 = 0) mapped

        fdaList <- clist (seq { 1..100 })
        let fdaMapped = AList.map (fun v -> v * 2) fdaList
        fdaFiltered <- AList.filter (fun v -> v % 4 = 0) fdaMapped

    [<Benchmark(Baseline = true)>]
    member this.AdaptiveSlop() =
        for i in 1 .. this.Iterations do
            AdaptiveSlop.Core.CList.append (1000 + i) slopList
            AdaptiveSlop.Core.CList.removeAt 0 slopList
            let _ = AdaptiveSlop.Core.AList.getValue slopFiltered
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        for i in 1 .. this.Iterations do
            transact (fun () ->
                fdaList.Append(1000 + i) |> ignore
                fdaList.RemoveAt(0) |> ignore)

            let _ = AList.force fdaFiltered
            ()

[<MemoryDiagnoser>]
type ListAppendBenchmarks() =
    let mutable slopLeft: AdaptiveSlop.Core.ChangeableList<int> = Unchecked.defaultof<_>

    let mutable slopRight: AdaptiveSlop.Core.ChangeableList<int> =
        Unchecked.defaultof<_>

    let mutable slopAppended: AdaptiveSlop.Core.IAdaptiveList<int> =
        Unchecked.defaultof<_>

    let mutable fdaLeft: clist<int> = Unchecked.defaultof<_>
    let mutable fdaRight: clist<int> = Unchecked.defaultof<_>
    let mutable fdaAppended: alist<int> = Unchecked.defaultof<_>

    [<Params(500)>]
    member val Iterations = 0 with get, set

    [<GlobalSetup>]
    member _.Setup() =
        slopLeft <- AdaptiveSlop.Core.CList.ofSeq (seq { 1..50 })
        slopRight <- AdaptiveSlop.Core.CList.ofSeq (seq { 51..100 })

        slopAppended <-
            AdaptiveSlop.Core.AList.append
                (AdaptiveSlop.Core.CList.value slopLeft)
                (AdaptiveSlop.Core.CList.value slopRight)

        fdaLeft <- clist (seq { 1..50 })
        fdaRight <- clist (seq { 51..100 })
        fdaAppended <- AList.append fdaLeft fdaRight

    [<Benchmark(Baseline = true)>]
    member this.AdaptiveSlop() =
        for i in 1 .. this.Iterations do
            AdaptiveSlop.Core.CList.append (1000 + i) slopLeft
            AdaptiveSlop.Core.CList.removeAt 0 slopLeft
            let _ = AdaptiveSlop.Core.AList.getValue slopAppended
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        for i in 1 .. this.Iterations do
            transact (fun () ->
                fdaLeft.Append(1000 + i) |> ignore
                fdaLeft.RemoveAt(0) |> ignore)

            let _ = AList.force fdaAppended
            ()

// =============================================================================
// Large Collection Benchmark
// =============================================================================

[<MemoryDiagnoser>]
type LargeCollectionBenchmarks() =
    let mutable slopSet: AdaptiveSlop.Core.ChangeableSet<int> = Unchecked.defaultof<_>
    let mutable slopASet: AdaptiveSlop.Core.IAdaptiveSet<int> = Unchecked.defaultof<_>
    let mutable fdaSet: cset<int> = Unchecked.defaultof<_>
    let mutable fdaASet: aset<int> = Unchecked.defaultof<_>

    [<Params(10000)>]
    member val InitialSize = 0 with get, set

    [<Params(200)>]
    member val Iterations = 0 with get, set

    [<GlobalSetup>]
    member this.Setup() =
        slopSet <- AdaptiveSlop.Core.CSet.ofSeq (seq { 1 .. this.InitialSize })
        slopASet <- AdaptiveSlop.Core.CSet.value slopSet

        fdaSet <- cset (seq { 1 .. this.InitialSize })
        fdaASet <- fdaSet :> aset<int>

    [<Benchmark(Baseline = true)>]
    member this.AdaptiveSlop() =
        let baseIdx = this.InitialSize

        for i in 1 .. this.Iterations do
            slopSet.Add(baseIdx + i)
            let _ = AdaptiveSlop.Core.ASet.getValue slopASet
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        let baseIdx = this.InitialSize

        for i in 1 .. this.Iterations do
            transact (fun () -> fdaSet.Add(baseIdx + i) |> ignore)
            let _ = ASet.force fdaASet
            ()

// =============================================================================
// Read-Heavy Benchmark (many reads, few writes)
// =============================================================================

[<MemoryDiagnoser>]
type ReadHeavyBenchmarks() =
    let mutable slopInput: AdaptiveSlop.Core.ChangeableValue<int> =
        Unchecked.defaultof<_>

    let mutable slopMapped: AdaptiveSlop.Core.IAdaptiveValue<int> =
        Unchecked.defaultof<_>

    let mutable fdaInput: cval<int> = Unchecked.defaultof<_>
    let mutable fdaMapped: aval<int> = Unchecked.defaultof<_>

    [<Params(100)>]
    member val WriteCount = 0 with get, set

    [<Params(50)>]
    member val ReadsPerWrite = 0 with get, set

    [<GlobalSetup>]
    member _.Setup() =
        slopInput <- AdaptiveSlop.Core.CVal.create 0
        slopMapped <- AdaptiveSlop.Core.AVal.map (fun v -> v * 2) (AdaptiveSlop.Core.CVal.value slopInput)

        fdaInput <- cval 0
        fdaMapped <- AVal.map (fun v -> v * 2) fdaInput

    [<Benchmark(Baseline = true)>]
    member this.AdaptiveSlop() =
        for i in 1 .. this.WriteCount do
            slopInput.Set(i)

            for _ in 1 .. this.ReadsPerWrite do
                let _ = AdaptiveSlop.Core.AVal.getValue slopMapped
                ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        for i in 1 .. this.WriteCount do
            transact (fun () -> fdaInput.Value <- i)

            for _ in 1 .. this.ReadsPerWrite do
                let _ = AVal.force fdaMapped
                ()

// =============================================================================
// Diamond Dependency Graph Benchmark
// =============================================================================

[<MemoryDiagnoser>]
type DiamondGraphBenchmarks() =
    // Diamond pattern: A -> B, A -> C, B -> D, C -> D
    let mutable slopA: AdaptiveSlop.Core.ChangeableValue<int> = Unchecked.defaultof<_>
    let mutable slopD: AdaptiveSlop.Core.IAdaptiveValue<int> = Unchecked.defaultof<_>
    let mutable fdaA: cval<int> = Unchecked.defaultof<_>
    let mutable fdaD: aval<int> = Unchecked.defaultof<_>

    [<Params(1000)>]
    member val Iterations = 0 with get, set

    [<GlobalSetup>]
    member _.Setup() =
        // AdaptiveSlop diamond
        slopA <- AdaptiveSlop.Core.CVal.create 0
        let aVal = AdaptiveSlop.Core.CVal.value slopA
        let slopB = AdaptiveSlop.Core.AVal.map (fun v -> v + 1) aVal
        let slopC = AdaptiveSlop.Core.AVal.map (fun v -> v * 2) aVal
        slopD <- AdaptiveSlop.Core.AVal.map2 (+) slopB slopC

        // FDA diamond
        fdaA <- cval 0
        let fdaB = AVal.map (fun v -> v + 1) fdaA
        let fdaC = AVal.map (fun v -> v * 2) fdaA
        fdaD <- AVal.map2 (+) fdaB fdaC

    [<Benchmark(Baseline = true)>]
    member this.AdaptiveSlop() =
        for i in 1 .. this.Iterations do
            slopA.Set(i)
            let _ = AdaptiveSlop.Core.AVal.getValue slopD
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        for i in 1 .. this.Iterations do
            transact (fun () -> fdaA.Value <- i)
            let _ = AVal.force fdaD
            ()

// =============================================================================
// Wide Tree Benchmark (fan-in: single output depending on N inputs)
// =============================================================================

[<MemoryDiagnoser>]
type WideTreeBenchmarks() =
    // Wide pattern: N inputs all feeding into one output via map2 chain
    // input1 --\
    // input2 ---\
    // ...        --> sum
    // inputN ---/
    let mutable slopInputs: AdaptiveSlop.Core.ChangeableValue<int>[] = [||]
    let mutable slopSum: AdaptiveSlop.Core.IAdaptiveValue<int> = Unchecked.defaultof<_>
    let mutable fdaInputs: cval<int>[] = [||]
    let mutable fdaSum: aval<int> = Unchecked.defaultof<_>

    [<Params(10, 50, 100, 500)>]
    member val Width = 0 with get, set

    [<Params(100)>]
    member val Iterations = 0 with get, set

    [<GlobalSetup>]
    member this.Setup() =
        // AdaptiveSlop wide tree
        slopInputs <- Array.init this.Width (fun i -> AdaptiveSlop.Core.CVal.create i)

        let mutable sum: AdaptiveSlop.Core.IAdaptiveValue<int> =
            AdaptiveSlop.Core.CVal.value slopInputs.[0]

        for i in 1 .. this.Width - 1 do
            sum <- AdaptiveSlop.Core.AVal.map2 (+) sum (AdaptiveSlop.Core.CVal.value slopInputs.[i])

        slopSum <- sum

        // FDA wide tree
        fdaInputs <- Array.init this.Width (fun i -> cval i)
        let mutable fdaSumVal: aval<int> = fdaInputs.[0]

        for i in 1 .. this.Width - 1 do
            fdaSumVal <- AVal.map2 (+) fdaSumVal fdaInputs.[i]

        fdaSum <- fdaSumVal

    [<Benchmark(Baseline = true)>]
    member this.AdaptiveSlop() =
        for i in 1 .. this.Iterations do
            // Change one input in the middle
            slopInputs.[this.Width / 2].Set(i)
            let _ = AdaptiveSlop.Core.AVal.getValue slopSum
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        for i in 1 .. this.Iterations do
            transact (fun () -> fdaInputs.[this.Width / 2].Value <- i)
            let _ = AVal.force fdaSum
            ()

// =============================================================================
// Optimized Wide Tree Benchmark using reduce (single node instead of map2 chain)
// =============================================================================

[<MemoryDiagnoser>]
type OptimizedWideTreeBenchmarks() =
    // Compares: map2 chain vs reduce (single node) vs FDA
    let mutable slopInputsMap2: AdaptiveSlop.Core.ChangeableValue<int>[] = [||]

    let mutable slopSumMap2: AdaptiveSlop.Core.IAdaptiveValue<int> =
        Unchecked.defaultof<_>

    let mutable slopInputsReduce: AdaptiveSlop.Core.ChangeableValue<int>[] = [||]

    let mutable slopSumReduce: AdaptiveSlop.Core.IAdaptiveValue<int> =
        Unchecked.defaultof<_>

    let mutable fdaInputs: cval<int>[] = [||]
    let mutable fdaSum: aval<int> = Unchecked.defaultof<_>

    [<Params(10, 50, 100, 500)>]
    member val Width = 0 with get, set

    [<Params(100)>]
    member val Iterations = 0 with get, set

    [<GlobalSetup>]
    member this.Setup() =
        // AdaptiveSlop with map2 chain (baseline)
        slopInputsMap2 <- Array.init this.Width (fun i -> AdaptiveSlop.Core.CVal.create i)

        let mutable sum: AdaptiveSlop.Core.IAdaptiveValue<int> =
            AdaptiveSlop.Core.CVal.value slopInputsMap2.[0]

        for i in 1 .. this.Width - 1 do
            sum <- AdaptiveSlop.Core.AVal.map2 (+) sum (AdaptiveSlop.Core.CVal.value slopInputsMap2.[i])

        slopSumMap2 <- sum

        // AdaptiveSlop with reduce (optimized - single node)
        slopInputsReduce <- Array.init this.Width (fun i -> AdaptiveSlop.Core.CVal.create i)
        let deps = slopInputsReduce |> Array.map AdaptiveSlop.Core.CVal.value
        slopSumReduce <- AdaptiveSlop.Core.AVal.reduce 0 (+) deps

        // FDA with map2 chain
        fdaInputs <- Array.init this.Width (fun i -> cval i)
        let mutable fdaSumVal: aval<int> = fdaInputs.[0]

        for i in 1 .. this.Width - 1 do
            fdaSumVal <- AVal.map2 (+) fdaSumVal fdaInputs.[i]

        fdaSum <- fdaSumVal

    [<Benchmark(Baseline = true)>]
    member this.AdaptiveSlop_Map2Chain() =
        for i in 1 .. this.Iterations do
            slopInputsMap2.[this.Width / 2].Set(i)
            let _ = AdaptiveSlop.Core.AVal.getValue slopSumMap2
            ()

    [<Benchmark>]
    member this.AdaptiveSlop_Reduce() =
        for i in 1 .. this.Iterations do
            slopInputsReduce.[this.Width / 2].Set(i)
            let _ = AdaptiveSlop.Core.AVal.getValue slopSumReduce
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        for i in 1 .. this.Iterations do
            transact (fun () -> fdaInputs.[this.Width / 2].Value <- i)
            let _ = AVal.force fdaSum
            ()

// =============================================================================
// Deep+Wide Tree Benchmark (depth with branching factor)
// =============================================================================

[<MemoryDiagnoser>]
type DeepWideBenchmarks() =
    // Tree with depth D and branching factor B
    // Each level has B children, creating B^D leaf nodes
    let mutable slopInputs: AdaptiveSlop.Core.ChangeableValue<int>[] = [||]
    let mutable slopRoot: AdaptiveSlop.Core.IAdaptiveValue<int> = Unchecked.defaultof<_>
    let mutable fdaInputs: cval<int>[] = [||]
    let mutable fdaRoot: aval<int> = Unchecked.defaultof<_>

    [<Params(3, 5, 7)>]
    member val Depth = 0 with get, set

    [<Params(2, 3, 4)>]
    member val BranchingFactor = 0 with get, set

    [<Params(50)>]
    member val Iterations = 0 with get, set

    [<GlobalSetup>]
    member this.Setup() =
        let leafCount = pown this.BranchingFactor this.Depth

        // AdaptiveSlop tree
        slopInputs <- Array.init leafCount (fun i -> AdaptiveSlop.Core.CVal.create i)

        // Build tree bottom-up by combining nodes at each level
        let rec buildLevel (nodes: AdaptiveSlop.Core.IAdaptiveValue<int>[]) =
            if nodes.Length = 1 then
                nodes.[0]
            else
                let parentCount = (nodes.Length + this.BranchingFactor - 1) / this.BranchingFactor

                let parents =
                    Array.init parentCount (fun i ->
                        let start = i * this.BranchingFactor
                        let endIdx = min (start + this.BranchingFactor) nodes.Length
                        let mutable combined = nodes.[start]

                        for j in (start + 1) .. (endIdx - 1) do
                            combined <- AdaptiveSlop.Core.AVal.map2 (+) combined nodes.[j]

                        combined)

                buildLevel parents

        slopRoot <- buildLevel (slopInputs |> Array.map AdaptiveSlop.Core.CVal.value)

        // FDA tree
        fdaInputs <- Array.init leafCount (fun i -> cval i)

        let rec buildFdaLevel (nodes: aval<int>[]) =
            if nodes.Length = 1 then
                nodes.[0]
            else
                let parentCount = (nodes.Length + this.BranchingFactor - 1) / this.BranchingFactor

                let parents =
                    Array.init parentCount (fun i ->
                        let start = i * this.BranchingFactor
                        let endIdx = min (start + this.BranchingFactor) nodes.Length
                        let mutable combined = nodes.[start]

                        for j in (start + 1) .. (endIdx - 1) do
                            combined <- AVal.map2 (+) combined nodes.[j]

                        combined)

                buildFdaLevel parents

        fdaRoot <- buildFdaLevel (fdaInputs |> Array.map (fun x -> x :> aval<int>))

    [<Benchmark(Baseline = true)>]
    member this.AdaptiveSlop() =
        for i in 1 .. this.Iterations do
            // Change a leaf node
            slopInputs.[slopInputs.Length / 2].Set(i)
            let _ = AdaptiveSlop.Core.AVal.getValue slopRoot
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        for i in 1 .. this.Iterations do
            transact (fun () -> fdaInputs.[fdaInputs.Length / 2].Value <- i)
            let _ = AVal.force fdaRoot
            ()

// =============================================================================
// Kipo PhysicsCache Benchmark (Pomo.Core Projections.fs PhysicsCache module)
// The real 60 Hz update / render read shape: per frame the sim advances every
// entity position (amap writes, the shape that was abandoned in Kipo because
// FDA's allocations were unbearable), and the render side forces the entity
// maps and rebuilds the movement snapshot: interpolated positions
// (start + v * dt), velocities-derived rotations (Atan2), and a spatial grid.
// =============================================================================

[<MemoryDiagnoser>]
type KipoPhysicsBenchmarks() =
    let rng = System.Random 42
    let cellSize = 4.0f

    let mutable slopTime: AdaptiveSlop.Core.ChangeableValue<float32> =
        Unchecked.defaultof<_>

    let mutable slopPositions: AdaptiveSlop.Core.ChangeableMap<int, System.Numerics.Vector3> =
        Unchecked.defaultof<_>

    let mutable slopVelocities: AdaptiveSlop.Core.ChangeableMap<int, System.Numerics.Vector3> =
        Unchecked.defaultof<_>

    let mutable slopModelConfig: AdaptiveSlop.Core.ChangeableMap<int, string> =
        Unchecked.defaultof<_>

    let mutable slopEntityScenario: AdaptiveSlop.Core.ChangeableMap<int, int> =
        Unchecked.defaultof<_>

    let mutable slopScenarios: AdaptiveSlop.Core.ChangeableMap<int, int> =
        Unchecked.defaultof<_>

    let mutable slopDerivedPositions: AdaptiveSlop.Core.IAdaptiveMap<int, System.Numerics.Vector3> =
        Unchecked.defaultof<_>

    let mutable slopDerivedRotations: AdaptiveSlop.Core.IAdaptiveMap<int, float32> =
        Unchecked.defaultof<_>

    let mutable fdaTime: cval<float32> = Unchecked.defaultof<_>

    let mutable fdaPositions: cmap<int, System.Numerics.Vector3> =
        Unchecked.defaultof<_>

    let mutable fdaVelocities: cmap<int, System.Numerics.Vector3> =
        Unchecked.defaultof<_>

    let mutable fdaModelConfig: cmap<int, string> = Unchecked.defaultof<_>
    let mutable fdaEntityScenario: cmap<int, int> = Unchecked.defaultof<_>
    let mutable fdaScenarios: cmap<int, int> = Unchecked.defaultof<_>

    let mutable fdaDerivedPositions: amap<int, System.Numerics.Vector3> =
        Unchecked.defaultof<_>

    let mutable fdaDerivedRotations: amap<int, float32> = Unchecked.defaultof<_>

    [<Params(250, 1000)>]
    member val EntityCount = 0 with get, set

    [<Params(50)>]
    member val Iterations = 0 with get, set

    member private this.BuildSnapshot
        (dt: float32)
        (positions: seq<int * System.Numerics.Vector3>)
        (getVelocity: int -> voption<System.Numerics.Vector3>)
        (getModelConfig: int -> voption<string>)
        (getScenario: int -> voption<int>)
        =
        // Faithful clone of PhysicsCache.calculateSnapshot: interpolated
        // positions, velocity-derived rotations, per-cell spatial grid.
        let positionsBuilder = Dictionary<int, System.Numerics.Vector3>()
        let rotationsBuilder = Dictionary<int, float32>()
        let gridBuilder = Dictionary<int, ResizeArray<int>>()

        for (id, startPos) in positions do
            match getScenario id with
            | ValueSome _ ->
                let v = getVelocity id |> ValueOption.defaultValue System.Numerics.Vector3.Zero
                let currentPos = startPos + v * dt
                positionsBuilder[id] <- currentPos

                let rotation =
                    if v <> System.Numerics.Vector3.Zero then
                        float32 (System.Math.Atan2(float v.X, float v.Z))
                    else
                        0.0f

                rotationsBuilder[id] <- rotation

                match getModelConfig id with
                | ValueSome _ ->
                    let cell = (int (currentPos.X / cellSize)) * 100000 + int (currentPos.Z / cellSize)

                    match gridBuilder.TryGetValue cell with
                    | true, list -> list.Add id
                    | _ -> gridBuilder[cell] <- ResizeArray([| id |])
                | _ -> ()
            | _ -> ()

        positionsBuilder.Count + rotationsBuilder.Count + gridBuilder.Count

    [<GlobalSetup>]
    member this.Setup() =
        // ---- AdaptiveSlop world ----
        slopTime <- AdaptiveSlop.Core.CVal.create 0.0f
        slopPositions <- AdaptiveSlop.Core.CMap.empty
        slopVelocities <- AdaptiveSlop.Core.CMap.empty
        slopModelConfig <- AdaptiveSlop.Core.CMap.empty
        slopEntityScenario <- AdaptiveSlop.Core.CMap.empty
        slopScenarios <- AdaptiveSlop.Core.CMap.empty

        for i in 0 .. this.EntityCount - 1 do
            slopPositions.AddOrUpdate i (System.Numerics.Vector3(float32 i, 0.0f, 0.0f))
            slopVelocities.AddOrUpdate i (System.Numerics.Vector3(rng.NextSingle(), 0.0f, rng.NextSingle()))
            slopModelConfig.AddOrUpdate i ("config-" + string (i % 8))
            slopEntityScenario.AddOrUpdate i 0

        slopScenarios.AddOrUpdate 0 0

        // ---- FDA world ----
        fdaTime <- cval 0.0f
        fdaPositions <- cmap<int, System.Numerics.Vector3> ()
        fdaVelocities <- cmap<int, System.Numerics.Vector3> ()
        fdaModelConfig <- cmap<int, string> ()
        fdaEntityScenario <- cmap<int, int> ()
        fdaScenarios <- cmap<int, int> ()

        transact (fun () ->
            for i in 0 .. this.EntityCount - 1 do
                fdaPositions.[i] <- System.Numerics.Vector3(float32 i, 0.0f, 0.0f)
                fdaVelocities.[i] <- System.Numerics.Vector3(rng.NextSingle(), 0.0f, rng.NextSingle())
                fdaModelConfig.[i] <- "config-" + string (i % 8)
                fdaEntityScenario.[i] <- 0

            fdaScenarios.[0] <- 0)

        // Derived nodes: the graph holds the interpolated positions and the
        // velocity-derived rotations. The velocity lookup inside the mapping is a
        // dynamic read (valid while velocities never change; the fully dynamic
        // dependency is Phase 7 work).
        let velView = AdaptiveSlop.Core.CMap.value slopVelocities |> _.GetValue()

        slopDerivedPositions <-
            AdaptiveSlop.Core.AMap.map
                (fun id startPos ->
                    let v =
                        match velView.TryGetValue id with
                        | true, v -> v
                        | _ -> System.Numerics.Vector3.Zero

                    startPos + v * 0.016f)
                (AdaptiveSlop.Core.CMap.value slopPositions)

        slopDerivedRotations <-
            AdaptiveSlop.Core.AMap.map
                (fun _id v ->
                    if v <> System.Numerics.Vector3.Zero then
                        float32 (System.Math.Atan2(float v.X, float v.Z))
                    else
                        0.0f)
                (AdaptiveSlop.Core.CMap.value slopVelocities)

        fdaDerivedPositions <-
            fdaPositions
            |> AMap.mapA (fun id startPos ->
                adaptive {
                    let! v = AMap.tryFind id fdaVelocities

                    return startPos + (v |> Option.defaultValue System.Numerics.Vector3.Zero) * 0.016f
                })

        fdaDerivedRotations <-
            AMap.map
                (fun _id v ->
                    if v <> System.Numerics.Vector3.Zero then
                        float32 (System.Math.Atan2(float v.X, float v.Z))
                    else
                        0.0f)
                fdaVelocities

    [<Benchmark(Baseline = true)>]
    member this.AdaptiveSlop() =
        let mutable acc = 0

        for _ in 1 .. this.Iterations do
            // Sim side: advance time and every entity position (journal appends).
            AdaptiveSlop.Core.CVal.set 0.016f slopTime

            for i in 0 .. this.EntityCount - 1 do
                let positions = AdaptiveSlop.Core.CMap.value slopPositions
                let velocities = AdaptiveSlop.Core.CMap.value slopVelocities
                let v = velocities.GetValue().[i]
                slopPositions.AddOrUpdate i (positions.GetValue().[i] + v * 0.016f)

            // Render side: force the maps and rebuild the movement snapshot.
            let time = AdaptiveSlop.Core.AVal.getValue slopTime
            let positions = AdaptiveSlop.Core.CMap.value slopPositions |> _.GetValue()
            let velocities = AdaptiveSlop.Core.CMap.value slopVelocities |> _.GetValue()
            let positionSeq = positions |> Seq.map (fun (KeyValue(k, v)) -> k, v)

            let modelConfigs =
                AdaptiveSlop.Core.AMap.force (AdaptiveSlop.Core.CMap.value slopModelConfig)

            let entityScenarios =
                AdaptiveSlop.Core.AMap.force (AdaptiveSlop.Core.CMap.value slopEntityScenario)

            let scenarios =
                AdaptiveSlop.Core.AMap.force (AdaptiveSlop.Core.CMap.value slopScenarios)

            let getVelocity id =
                match velocities.TryGetValue id with
                | true, v -> ValueSome v
                | _ -> ValueNone

            let getModelConfig id =
                match modelConfigs.TryGetValue id with
                | true, c -> ValueSome c
                | _ -> ValueNone

            let getScenario id =
                match entityScenarios.TryGetValue id with
                | true, s -> ValueSome s
                | _ -> ValueNone

            acc <-
                acc
                + this.BuildSnapshot time positionSeq getVelocity getModelConfig getScenario
                + scenarios.Count

        if acc = -1 then
            failwith "unreachable"

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        let mutable acc = 0

        for _ in 1 .. this.Iterations do
            // Sim side: advance time and every entity position in one transact.
            transact (fun () ->
                fdaTime.Value <- 0.016f

                for i in 0 .. this.EntityCount - 1 do
                    let v = fdaVelocities.[i]
                    fdaPositions.[i] <- fdaPositions.[i] + v * 0.016f)

            // Render side: force the maps and rebuild the movement snapshot.
            let time = AVal.force fdaTime
            let positions = AMap.force fdaPositions
            let velocities = AMap.force fdaVelocities
            let positionSeq = positions |> HashMap.toSeq
            let modelConfigs = AMap.force fdaModelConfig
            let entityScenarios = AMap.force fdaEntityScenario
            let scenarios = AMap.force fdaScenarios

            let getVelocity id = HashMap.tryFindV id velocities
            let getModelConfig id = HashMap.tryFindV id modelConfigs
            let getScenario id = HashMap.tryFindV id entityScenarios

            acc <-
                acc
                + this.BuildSnapshot time positionSeq getVelocity getModelConfig getScenario
                + scenarios.Count

        if acc = -1 then
            failwith "unreachable"

    /// The graph-as-cache variant: derived nodes hold the interpolated positions
    /// and rotations; the render reads transient views only (no force), and the
    /// spatial grid is the only per-frame user-code rebuild.
    [<Benchmark>]
    member this.AdaptiveSlop_GraphDirect() =
        let mutable acc = 0

        for _ in 1 .. this.Iterations do
            // Sim side: advance time and every entity position (journal appends).
            AdaptiveSlop.Core.CVal.set 0.016f slopTime

            for i in 0 .. this.EntityCount - 1 do
                let positions = AdaptiveSlop.Core.CMap.value slopPositions
                let velocities = AdaptiveSlop.Core.CMap.value slopVelocities
                let v = velocities.GetValue().[i]
                slopPositions.AddOrUpdate i (positions.GetValue().[i] + v * 0.016f)

            // Render side: read the graph directly. The derived positions drain
            // the pending deltas in place (0 alloc); every view is transient.
            let positionsView = AdaptiveSlop.Core.AMap.getValue slopDerivedPositions
            let rotationsView = AdaptiveSlop.Core.AMap.getValue slopDerivedRotations
            let velocitiesView = AdaptiveSlop.Core.CMap.value slopVelocities |> _.GetValue()
            let modelConfigsView = AdaptiveSlop.Core.CMap.value slopModelConfig |> _.GetValue()

            let entityScenariosView =
                AdaptiveSlop.Core.CMap.value slopEntityScenario |> _.GetValue()

            let scenariosView = AdaptiveSlop.Core.CMap.value slopScenarios |> _.GetValue()
            let positionSeq = positionsView |> Seq.map (fun (KeyValue(k, v)) -> k, v)

            let getVelocity id =
                match velocitiesView.TryGetValue id with
                | true, v -> ValueSome v
                | _ -> ValueNone

            let getModelConfig id =
                match modelConfigsView.TryGetValue id with
                | true, c -> ValueSome c
                | _ -> ValueNone

            let getScenario id =
                match entityScenariosView.TryGetValue id with
                | true, s -> ValueSome s
                | _ -> ValueNone

            let getScenario id =
                match entityScenariosView.TryGetValue id with
                | true, s -> ValueSome s
                | _ -> ValueNone

            acc <-
                acc
                + this.BuildSnapshot 0.016f positionSeq getVelocity getModelConfig getScenario
                + rotationsView.Count
                + scenariosView.Count

        if acc = -1 then
            failwith "unreachable"

    /// The graph-as-cache variant for FDA: per-element adaptive blocks over the
    /// derived maps, forced per frame (their materialization idiom).
    [<Benchmark>]
    member this.FSharpDataAdaptive_GraphDirect() =
        let mutable acc = 0

        for _ in 1 .. this.Iterations do
            transact (fun () ->
                fdaTime.Value <- 0.016f

                for i in 0 .. this.EntityCount - 1 do
                    fdaPositions.[i] <- fdaPositions.[i] + fdaVelocities.[i] * 0.016f)

            // Render side: force the derived maps (FDA's materialization idiom).
            let positions = AMap.force fdaDerivedPositions
            let rotations = AMap.force fdaDerivedRotations
            let velocities = AMap.force fdaVelocities
            let modelConfigs = AMap.force fdaModelConfig
            let entityScenarios = AMap.force fdaEntityScenario
            let scenarios = AMap.force fdaScenarios
            let positionSeq = positions |> HashMap.toSeq

            let getVelocity id = HashMap.tryFindV id velocities
            let getModelConfig id = HashMap.tryFindV id modelConfigs
            let getScenario id = HashMap.tryFindV id entityScenarios

            acc <-
                acc
                + this.BuildSnapshot 0.016f positionSeq getVelocity getModelConfig getScenario
                + rotations.Count
                + scenarios.Count

        if acc = -1 then
            failwith "unreachable"

// =============================================================================
// Unbalanced Tree Benchmark (asymmetric structure)
// =============================================================================

[<MemoryDiagnoser>]
type UnbalancedTreeBenchmarks() =
    // Unbalanced: One deep branch + many shallow branches
    // Deep branch: input -> map -> map -> ... -> map (depth levels)
    // Shallow branches: input -> map (1 level each)
    // All combine at the end
    let mutable slopDeepInput: AdaptiveSlop.Core.ChangeableValue<int> =
        Unchecked.defaultof<_>

    let mutable slopShallowInputs: AdaptiveSlop.Core.ChangeableValue<int>[] = [||]

    let mutable slopResult: AdaptiveSlop.Core.IAdaptiveValue<int> =
        Unchecked.defaultof<_>

    let mutable fdaDeepInput: cval<int> = Unchecked.defaultof<_>
    let mutable fdaShallowInputs: cval<int>[] = [||]
    let mutable fdaResult: aval<int> = Unchecked.defaultof<_>

    [<Params(10, 50, 100)>]
    member val DeepBranchDepth = 0 with get, set

    [<Params(5, 20, 50)>]
    member val ShallowBranchCount = 0 with get, set

    [<Params(50)>]
    member val Iterations = 0 with get, set

    [<GlobalSetup>]
    member this.Setup() =
        // AdaptiveSlop unbalanced tree
        slopDeepInput <- AdaptiveSlop.Core.CVal.create 0

        let mutable deepChain: AdaptiveSlop.Core.IAdaptiveValue<int> =
            AdaptiveSlop.Core.CVal.value slopDeepInput

        for _ in 1 .. this.DeepBranchDepth do
            deepChain <- AdaptiveSlop.Core.AVal.map (fun v -> v + 1) deepChain

        slopShallowInputs <- Array.init this.ShallowBranchCount (fun i -> AdaptiveSlop.Core.CVal.create i)

        let shallowMapped =
            slopShallowInputs
            |> Array.map (fun cv -> AdaptiveSlop.Core.AVal.map (fun v -> v * 2) (AdaptiveSlop.Core.CVal.value cv))

        // Combine deep chain with all shallow branches
        let mutable combined = deepChain

        for shallow in shallowMapped do
            combined <- AdaptiveSlop.Core.AVal.map2 (+) combined shallow

        slopResult <- combined

        // FDA unbalanced tree
        fdaDeepInput <- cval 0
        let mutable fdaDeepChain: aval<int> = fdaDeepInput

        for _ in 1 .. this.DeepBranchDepth do
            fdaDeepChain <- AVal.map (fun v -> v + 1) fdaDeepChain

        fdaShallowInputs <- Array.init this.ShallowBranchCount (fun i -> cval i)

        let fdaShallowMapped =
            fdaShallowInputs |> Array.map (fun cv -> AVal.map (fun v -> v * 2) cv)

        let mutable fdaCombined = fdaDeepChain

        for shallow in fdaShallowMapped do
            fdaCombined <- AVal.map2 (+) fdaCombined shallow

        fdaResult <- fdaCombined

    [<Benchmark(Baseline = true, Description = "AdaptiveSlop_DeepChange")>]
    member this.AdaptiveSlop_ChangeDeep() =
        for i in 1 .. this.Iterations do
            slopDeepInput.Set(i)
            let _ = AdaptiveSlop.Core.AVal.getValue slopResult
            ()

    [<Benchmark(Description = "FDA_DeepChange")>]
    member this.FSharpDataAdaptive_ChangeDeep() =
        for i in 1 .. this.Iterations do
            transact (fun () -> fdaDeepInput.Value <- i)
            let _ = AVal.force fdaResult
            ()

    [<Benchmark(Description = "AdaptiveSlop_ShallowChange")>]
    member this.AdaptiveSlop_ChangeShallow() =
        for i in 1 .. this.Iterations do
            slopShallowInputs.[0].Set(i)
            let _ = AdaptiveSlop.Core.AVal.getValue slopResult
            ()

    [<Benchmark(Description = "FDA_ShallowChange")>]
    member this.FSharpDataAdaptive_ChangeShallow() =
        for i in 1 .. this.Iterations do
            transact (fun () -> fdaShallowInputs.[0].Value <- i)
            let _ = AVal.force fdaResult
            ()

// =============================================================================
// Incremental Delta Propagation Benchmark
// Tests mutations through a map→filter transform chain
// =============================================================================

[<MemoryDiagnoser>]
type IncrementalChainBenchmarks() =
    // AdaptiveSlop: source → *2 → filter even numbers
    let mutable slopSource: AdaptiveSlop.Core.ChangeableSet<int> =
        Unchecked.defaultof<_>

    let mutable slopChain: AdaptiveSlop.Core.IAdaptiveSet<int> = Unchecked.defaultof<_>
    // FDA: same chain
    let mutable fdaSource: cset<int> = Unchecked.defaultof<_>
    let mutable fdaChain: aset<int> = Unchecked.defaultof<_>

    [<Params(100, 1000, 10000)>]
    member val InitialSize = 0 with get, set

    [<Params(200)>]
    member val Mutations = 0 with get, set

    [<GlobalSetup>]
    member this.Setup() =
        // AdaptiveSlop
        slopSource <- AdaptiveSlop.Core.CSet.ofSeq (seq { 1 .. this.InitialSize })
        let mapped = AdaptiveSlop.Core.ASet.map (fun x -> x * 2) slopSource
        slopChain <- AdaptiveSlop.Core.ASet.filter (fun x -> x % 4 = 0) mapped

        // FDA
        fdaSource <- cset (seq { 1 .. this.InitialSize })
        let fdaMapped = ASet.map (fun x -> x * 2) fdaSource
        fdaChain <- ASet.filter (fun x -> x % 4 = 0) fdaMapped

    [<Benchmark(Baseline = true)>]
    member this.AdaptiveSlop() =
        let offset = this.InitialSize

        for i in 1 .. this.Mutations do
            slopSource.Add(offset + i)
            slopSource.Remove(offset + i - 1)
            let _ = AdaptiveSlop.Core.ASet.getValue slopChain
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        let offset = this.InitialSize

        for i in 1 .. this.Mutations do
            transact (fun () ->
                fdaSource.Add(offset + i) |> ignore
                fdaSource.Remove(offset + i - 1) |> ignore)

            let _ = ASet.force fdaChain
            ()

// =============================================================================
// Concurrent Post/Pump Benchmark
// =============================================================================
// AdaptiveSlop: foreign threads only Post; the owner thread pumps and reads.
// FDA: threads write and read concurrently (its locked model).
[<MemoryDiagnoser>]
type ConcurrentBenchmarks() =
    let mutable slopInput: AdaptiveSlop.Core.ChangeableValue<int> =
        Unchecked.defaultof<_>

    let mutable slopMapped: AdaptiveSlop.Core.IAdaptiveValue<int> =
        Unchecked.defaultof<_>

    let mutable fdaInput: cval<int> = Unchecked.defaultof<_>
    let mutable fdaMapped: aval<int> = Unchecked.defaultof<_>

    [<Params(4)>]
    member val ThreadCount = 0 with get, set

    [<Params(500)>]
    member val IterationsPerThread = 0 with get, set

    [<GlobalSetup>]
    member _.Setup() =
        slopInput <- AdaptiveSlop.Core.CVal.create 0
        slopMapped <- AdaptiveSlop.Core.AVal.map (fun v -> v + 1) (AdaptiveSlop.Core.CVal.value slopInput)
        fdaInput <- cval 0
        fdaMapped <- AVal.map (fun v -> v + 1) fdaInput

    [<Benchmark(Baseline = true)>]
    member this.AdaptiveSlop() =
        let tasks =
            Array.init this.ThreadCount (fun threadId ->
                Task.Run(fun () ->
                    for i in 1 .. this.IterationsPerThread do
                        slopInput.Post(threadId * 10000 + i)))

        // Owner thread: pump and read while the producers run.
        while not (Task.WaitAll(tasks, 1)) do
            AdaptiveSlop.Core.Posting.pump ()
            let _ = AdaptiveSlop.Core.AVal.getValue slopMapped
            ()

        AdaptiveSlop.Core.Posting.pump ()
        let _ = AdaptiveSlop.Core.AVal.getValue slopMapped
        ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        let tasks =
            Array.init this.ThreadCount (fun threadId ->
                Task.Run(fun () ->
                    for i in 1 .. this.IterationsPerThread do
                        transact (fun () -> fdaInput.Value <- threadId * 10000 + i)

                        let _ = AVal.force fdaMapped
                        ()))

        Task.WaitAll(tasks)

// =============================================================================
// Per-element adaptive map benchmark (docs/2026-08-05-MAPA-DESIGN.md §13.6)
//
// ASet.mapA: one element-aval write per iteration, targeted delta. The naive
// composition (ASet.map + AVal.getValue) cannot react to an aval write at all
// (its mapping runs only on source deltas), so its workload is a full source
// replace: every element re-mapped per iteration — the brute-force baseline
// the mapA design avoids.
//
// FDA benchmark pattern (src/Test/.../Benchmarks/Map.fs): the measured
// method is ONE operation (one write + one read); the IterationSetup restores
// the pre-change state and settles the graph, so the Mean is directly the
// per-operation cost.
// =============================================================================

[<MemoryDiagnoser>]
type MapABenchmarks() =
    let mutable slopElements: AdaptiveSlop.Core.ChangeableValue<int>[] = [||]
    let mutable slopSet: AdaptiveSlop.Core.ChangeableSet<int> = Unchecked.defaultof<_>
    let mutable slopMapped: AdaptiveSlop.Core.aset<int> = Unchecked.defaultof<_>
    let mutable slopNaive: AdaptiveSlop.Core.aset<int> = Unchecked.defaultof<_>
    let mutable fdaElements: cval<int>[] = [||]
    let mutable fdaSet: cset<int> = Unchecked.defaultof<_>
    let mutable fdaMapped: aset<int> = Unchecked.defaultof<_>
    let mutable counter = 0

    [<Params(100, 1000)>]
    member val ElementCount = 0 with get, set

    [<GlobalSetup>]
    member this.Setup() =
        counter <- 0
        slopElements <- Array.init this.ElementCount (fun i -> AdaptiveSlop.Core.CVal.create (i * 10))
        slopSet <- AdaptiveSlop.Core.CSet.ofSeq [ 0 .. this.ElementCount - 1 ]

        slopMapped <-
            slopSet
            |> AdaptiveSlop.Core.ASet.mapA (fun v -> AdaptiveSlop.Core.CVal.value slopElements[v % this.ElementCount])

        slopNaive <-
            slopSet
            |> AdaptiveSlop.Core.ASet.map (fun v ->
                AdaptiveSlop.Core.AVal.getValue (AdaptiveSlop.Core.CVal.value slopElements[v % this.ElementCount]))

        fdaElements <- Array.init this.ElementCount (fun i -> cval (i * 10))
        fdaSet <- cset [ 0 .. this.ElementCount - 1 ]
        fdaMapped <- fdaSet |> ASet.mapA (fun v -> fdaElements[v % this.ElementCount] :> aval<int>)
        // Settle: initialize the derived nodes outside the measurement.
        AdaptiveSlop.Core.ASet.getValue slopMapped |> ignore
        AdaptiveSlop.Core.ASet.getValue slopNaive |> ignore
        transact (fun () -> fdaElements[0].Value <- 0)
        ASet.force fdaMapped |> ignore

    [<IterationSetup>]
    member this.IterationSetup() =
        // Restore the pre-change state and settle the graph.
        slopElements[0].Set(0)
        slopSet.Set(seq { 0 .. this.ElementCount - 1 })
        AdaptiveSlop.Core.ASet.getValue slopMapped |> ignore
        AdaptiveSlop.Core.ASet.getValue slopNaive |> ignore
        transact (fun () -> fdaElements[0].Value <- 0)
        ASet.force fdaMapped |> ignore

    [<Benchmark(Baseline = true)>]
    member this.AdaptiveSlopMapA() =
        counter <- counter + 1
        slopElements[0].Set(counter)
        let _ = AdaptiveSlop.Core.ASet.getValue slopMapped
        ()

    [<Benchmark>]
    member this.NaiveMapForcesOnFullReplace() =
        counter <- counter + 1
        // Full replace with a disjoint range: the naive composition re-maps
        // every element (the delta is N removes + N adds).
        let start = 100000 + this.ElementCount + counter
        slopSet.Set(seq { start .. start + this.ElementCount - 1 })
        let _ = AdaptiveSlop.Core.ASet.getValue slopNaive
        ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        counter <- counter + 1
        transact (fun () -> fdaElements[0].Value <- counter)
        let _ = ASet.force fdaMapped
        ()

// =============================================================================
// Entry Point
// =============================================================================

[<EntryPoint>]
let main args =
    BenchmarkSwitcher.FromAssembly(typeof<ValueBenchmarks>.Assembly).Run(args)
    |> ignore

    0
