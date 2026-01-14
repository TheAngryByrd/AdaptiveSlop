module AdaptiveSlop.Benchmarks

open System.Threading
open System.Threading.Tasks
open BenchmarkDotNet.Attributes
open BenchmarkDotNet.Running
open FSharp.Data.Adaptive

// =============================================================================
// Basic Value Benchmarks
// =============================================================================

[<MemoryDiagnoser>]
type ValueBenchmarks() =
    let mutable slopInput: AdaptiveSlop.Core.ChangeableValue<int> = Unchecked.defaultof<_>
    let mutable slopMapped: AdaptiveSlop.Core.IAdaptiveValue<int> = Unchecked.defaultof<_>
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

    [<Benchmark(Baseline=true)>]
    member this.AdaptiveSlop() =
        for i in 1..this.Iterations do
            slopInput.Set(i)
            let _ = AdaptiveSlop.Core.AVal.getValue slopMapped
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        for i in 1..this.Iterations do
            transact (fun () -> fdaInput.Value <- i)
            let _ = AVal.force fdaMapped
            ()

// =============================================================================
// Deep Dependency Chain Benchmark
// =============================================================================

[<MemoryDiagnoser>]
type DeepChainBenchmarks() =
    let mutable slopInput: AdaptiveSlop.Core.ChangeableValue<int> = Unchecked.defaultof<_>
    let mutable slopChain: AdaptiveSlop.Core.IAdaptiveValue<int> = Unchecked.defaultof<_>
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
        let mutable current: AdaptiveSlop.Core.IAdaptiveValue<int> = AdaptiveSlop.Core.CVal.value slopInput
        for _ in 1..this.Depth do
            current <- AdaptiveSlop.Core.AVal.map (fun v -> v + 1) current
        slopChain <- current

        // FDA chain
        fdaInput <- cval 0
        let mutable fdaCurrent: aval<int> = fdaInput
        for _ in 1..this.Depth do
            fdaCurrent <- AVal.map (fun v -> v + 1) fdaCurrent
        fdaChain <- fdaCurrent

    [<Benchmark(Baseline=true)>]
    member this.AdaptiveSlop() =
        for i in 1..this.Iterations do
            slopInput.Set(i)
            let _ = AdaptiveSlop.Core.AVal.getValue slopChain
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        for i in 1..this.Iterations do
            transact (fun () -> fdaInput.Value <- i)
            let _ = AVal.force fdaChain
            ()

// =============================================================================
// Map2/Combine Benchmark
// =============================================================================

[<MemoryDiagnoser>]
type Map2Benchmarks() =
    let mutable slopLeft: AdaptiveSlop.Core.ChangeableValue<int> = Unchecked.defaultof<_>
    let mutable slopRight: AdaptiveSlop.Core.ChangeableValue<int> = Unchecked.defaultof<_>
    let mutable slopCombined: AdaptiveSlop.Core.IAdaptiveValue<int> = Unchecked.defaultof<_>
    let mutable fdaLeft: cval<int> = Unchecked.defaultof<_>
    let mutable fdaRight: cval<int> = Unchecked.defaultof<_>
    let mutable fdaCombined: aval<int> = Unchecked.defaultof<_>

    [<Params(1000)>]
    member val Iterations = 0 with get, set

    [<GlobalSetup>]
    member _.Setup() =
        slopLeft <- AdaptiveSlop.Core.CVal.create 0
        slopRight <- AdaptiveSlop.Core.CVal.create 0
        slopCombined <- AdaptiveSlop.Core.AVal.map2 (+) (AdaptiveSlop.Core.CVal.value slopLeft) (AdaptiveSlop.Core.CVal.value slopRight)
        
        fdaLeft <- cval 0
        fdaRight <- cval 0
        fdaCombined <- AVal.map2 (+) fdaLeft fdaRight

    [<Benchmark(Baseline=true)>]
    member this.AdaptiveSlop() =
        for i in 1..this.Iterations do
            slopLeft.Set(i)
            slopRight.Set(i * 2)
            let _ = AdaptiveSlop.Core.AVal.getValue slopCombined
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        for i in 1..this.Iterations do
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
    let mutable slopSelector: AdaptiveSlop.Core.ChangeableValue<bool> = Unchecked.defaultof<_>
    let mutable slopLeft: AdaptiveSlop.Core.ChangeableValue<int> = Unchecked.defaultof<_>
    let mutable slopRight: AdaptiveSlop.Core.ChangeableValue<int> = Unchecked.defaultof<_>
    let mutable slopBound: AdaptiveSlop.Core.IAdaptiveValue<int> = Unchecked.defaultof<_>
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
        slopBound <- AdaptiveSlop.Core.AVal.bind 
            (fun sel -> if sel then AdaptiveSlop.Core.CVal.value slopLeft else AdaptiveSlop.Core.CVal.value slopRight) 
            (AdaptiveSlop.Core.CVal.value slopSelector)
        
        fdaSelector <- cval true
        fdaLeft <- cval 1
        fdaRight <- cval 2
        fdaBound <- AVal.bind (fun sel -> if sel then fdaLeft :> aval<_> else fdaRight :> aval<_>) fdaSelector

    [<Benchmark(Baseline=true)>]
    member this.AdaptiveSlop() =
        for i in 1..this.Iterations do
            slopSelector.Set(i % 2 = 0)
            slopLeft.Set(i)
            slopRight.Set(i * 2)
            let _ = AdaptiveSlop.Core.AVal.getValue slopBound
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        for i in 1..this.Iterations do
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
        let mutable sum: AdaptiveSlop.Core.IAdaptiveValue<int> = AdaptiveSlop.Core.AVal.constant 0
        for v in slopValues do
            sum <- AdaptiveSlop.Core.AVal.map2 (+) sum (AdaptiveSlop.Core.CVal.value v)
        slopSum <- sum

        // FDA
        fdaValues <- Array.init this.ValueCount (fun _ -> cval 0)
        let mutable fdaSumVal: aval<int> = AVal.constant 0
        for v in fdaValues do
            fdaSumVal <- AVal.map2 (+) fdaSumVal v
        fdaSum <- fdaSumVal

    [<Benchmark(Baseline=true)>]
    member this.AdaptiveSlop_Batched() =
        for i in 1..this.Iterations do
            AdaptiveSlop.Core.Transaction.run (fun () ->
                for j in 0..slopValues.Length - 1 do
                    slopValues[j].Set(i + j)) |> ignore
            let _ = AdaptiveSlop.Core.AVal.getValue slopSum
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive_Batched() =
        for i in 1..this.Iterations do
            transact (fun () ->
                for j in 0..fdaValues.Length - 1 do
                    fdaValues[j].Value <- i + j)
            let _ = AVal.force fdaSum
            ()

// =============================================================================
// Set Benchmarks
// =============================================================================

[<MemoryDiagnoser>]
type SetBenchmarks() =
    let mutable slopSet: AdaptiveSlop.Core.ChangeableSet<int> = Unchecked.defaultof<_>
    let mutable slopASet: AdaptiveSlop.Core.IAdaptiveValue<Set<int>> = Unchecked.defaultof<_>
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

    [<Benchmark(Baseline=true)>]
    member this.AdaptiveSlop() =
        for i in 1..this.Iterations do
            slopSet.Add(i)
            slopSet.Remove(i)
            let _ = AdaptiveSlop.Core.ASet.getValue slopASet
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        for i in 1..this.Iterations do
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
    let mutable slopFiltered: AdaptiveSlop.Core.IAdaptiveValue<Set<int>> = Unchecked.defaultof<_>
    let mutable fdaSet: cset<int> = Unchecked.defaultof<_>
    let mutable fdaFiltered: aset<int> = Unchecked.defaultof<_>

    [<Params(500)>]
    member val Iterations = 0 with get, set

    [<GlobalSetup>]
    member _.Setup() =
        slopSet <- AdaptiveSlop.Core.CSet.ofSeq (seq { 1..100 })
        let mapped = AdaptiveSlop.Core.ASet.map (fun v -> v * 2) (AdaptiveSlop.Core.CSet.value slopSet)
        slopFiltered <- AdaptiveSlop.Core.ASet.filter (fun v -> v % 4 = 0) mapped
        
        fdaSet <- cset (seq { 1..100 })
        let fdaMapped = ASet.map (fun v -> v * 2) fdaSet
        fdaFiltered <- ASet.filter (fun v -> v % 4 = 0) fdaMapped

    [<Benchmark(Baseline=true)>]
    member this.AdaptiveSlop() =
        for i in 1..this.Iterations do
            slopSet.Add(1000 + i)
            slopSet.Remove(1000 + i - 1)
            let _ = AdaptiveSlop.Core.ASet.getValue slopFiltered
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        for i in 1..this.Iterations do
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
    let mutable slopMap: AdaptiveSlop.Core.ChangeableMap<int, int> = Unchecked.defaultof<_>
    let mutable slopAMap: AdaptiveSlop.Core.IAdaptiveValue<Map<int, int>> = Unchecked.defaultof<_>
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

    [<Benchmark(Baseline=true)>]
    member this.AdaptiveSlop() =
        for i in 1..this.Iterations do
            slopMap.AddOrUpdate(i, i * 2)
            slopMap.Remove(i)
            let _ = AdaptiveSlop.Core.AMap.getValue slopAMap
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        for i in 1..this.Iterations do
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
    let mutable slopMap: AdaptiveSlop.Core.ChangeableMap<int, int> = Unchecked.defaultof<_>
    let mutable slopFiltered: AdaptiveSlop.Core.IAdaptiveValue<Map<int, int>> = Unchecked.defaultof<_>
    let mutable fdaMap: cmap<int, int> = Unchecked.defaultof<_>
    let mutable fdaFiltered: amap<int, int> = Unchecked.defaultof<_>

    [<Params(500)>]
    member val Iterations = 0 with get, set

    [<GlobalSetup>]
    member _.Setup() =
        slopMap <- AdaptiveSlop.Core.CMap.ofSeq (seq { for i in 1..100 -> i, i * 10 })
        let mapped = AdaptiveSlop.Core.AMap.map (fun _ v -> v + 1) (AdaptiveSlop.Core.CMap.value slopMap)
        slopFiltered <- AdaptiveSlop.Core.AMap.filter (fun _ v -> v > 50) mapped
        
        fdaMap <- cmap (seq { for i in 1..100 -> i, i * 10 })
        let fdaMapped = AMap.map (fun _ v -> v + 1) fdaMap
        fdaFiltered <- AMap.filter (fun _ v -> v > 50) fdaMapped

    [<Benchmark(Baseline=true)>]
    member this.AdaptiveSlop() =
        for i in 1..this.Iterations do
            slopMap.AddOrUpdate(1000 + i, (1000 + i) * 10)
            slopMap.Remove(1000 + i - 1)
            let _ = AdaptiveSlop.Core.AMap.getValue slopFiltered
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        for i in 1..this.Iterations do
            transact (fun () ->
                fdaMap.[1000 + i] <- (1000 + i) * 10
                fdaMap.Remove(1000 + i - 1) |> ignore)
            let _ = AMap.force fdaFiltered
            ()

// =============================================================================
// Large Collection Benchmark
// =============================================================================

[<MemoryDiagnoser>]
type LargeCollectionBenchmarks() =
    let mutable slopSet: AdaptiveSlop.Core.ChangeableSet<int> = Unchecked.defaultof<_>
    let mutable slopASet: AdaptiveSlop.Core.IAdaptiveValue<Set<int>> = Unchecked.defaultof<_>
    let mutable fdaSet: cset<int> = Unchecked.defaultof<_>
    let mutable fdaASet: aset<int> = Unchecked.defaultof<_>

    [<Params(10000)>]
    member val InitialSize = 0 with get, set

    [<Params(200)>]
    member val Iterations = 0 with get, set

    [<GlobalSetup>]
    member this.Setup() =
        slopSet <- AdaptiveSlop.Core.CSet.ofSeq (seq { 1..this.InitialSize })
        slopASet <- AdaptiveSlop.Core.CSet.value slopSet
        
        fdaSet <- cset (seq { 1..this.InitialSize })
        fdaASet <- fdaSet :> aset<int>

    [<Benchmark(Baseline=true)>]
    member this.AdaptiveSlop() =
        let baseIdx = this.InitialSize
        for i in 1..this.Iterations do
            slopSet.Add(baseIdx + i)
            let _ = AdaptiveSlop.Core.ASet.getValue slopASet
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        let baseIdx = this.InitialSize
        for i in 1..this.Iterations do
            transact (fun () -> fdaSet.Add(baseIdx + i) |> ignore)
            let _ = ASet.force fdaASet
            ()

// =============================================================================
// Read-Heavy Benchmark (many reads, few writes)
// =============================================================================

[<MemoryDiagnoser>]
type ReadHeavyBenchmarks() =
    let mutable slopInput: AdaptiveSlop.Core.ChangeableValue<int> = Unchecked.defaultof<_>
    let mutable slopMapped: AdaptiveSlop.Core.IAdaptiveValue<int> = Unchecked.defaultof<_>
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

    [<Benchmark(Baseline=true)>]
    member this.AdaptiveSlop() =
        for i in 1..this.WriteCount do
            slopInput.Set(i)
            for _ in 1..this.ReadsPerWrite do
                let _ = AdaptiveSlop.Core.AVal.getValue slopMapped
                ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        for i in 1..this.WriteCount do
            transact (fun () -> fdaInput.Value <- i)
            for _ in 1..this.ReadsPerWrite do
                let _ = AVal.force fdaMapped
                ()

// =============================================================================
// Concurrent Access Benchmark
// =============================================================================

[<MemoryDiagnoser>]
type ConcurrentBenchmarks() =
    let mutable slopInput: AdaptiveSlop.Core.ChangeableValue<int> = Unchecked.defaultof<_>
    let mutable slopMapped: AdaptiveSlop.Core.IAdaptiveValue<int> = Unchecked.defaultof<_>
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

    [<Benchmark(Baseline=true)>]
    member this.AdaptiveSlop() =
        let tasks = Array.init this.ThreadCount (fun threadId ->
            Task.Run(fun () ->
                for i in 1..this.IterationsPerThread do
                    slopInput.Set(threadId * 10000 + i)
                    let _ = AdaptiveSlop.Core.AVal.getValue slopMapped
                    ()))
        Task.WaitAll(tasks)

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        let tasks = Array.init this.ThreadCount (fun threadId ->
            Task.Run(fun () ->
                for i in 1..this.IterationsPerThread do
                    transact (fun () -> fdaInput.Value <- threadId * 10000 + i)
                    let _ = AVal.force fdaMapped
                    ()))
        Task.WaitAll(tasks)

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

    [<Benchmark(Baseline=true)>]
    member this.AdaptiveSlop() =
        for i in 1..this.Iterations do
            slopA.Set(i)
            let _ = AdaptiveSlop.Core.AVal.getValue slopD
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        for i in 1..this.Iterations do
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
        let mutable sum: AdaptiveSlop.Core.IAdaptiveValue<int> = AdaptiveSlop.Core.CVal.value slopInputs.[0]
        for i in 1..this.Width - 1 do
            sum <- AdaptiveSlop.Core.AVal.map2 (+) sum (AdaptiveSlop.Core.CVal.value slopInputs.[i])
        slopSum <- sum

        // FDA wide tree
        fdaInputs <- Array.init this.Width (fun i -> cval i)
        let mutable fdaSumVal: aval<int> = fdaInputs.[0]
        for i in 1..this.Width - 1 do
            fdaSumVal <- AVal.map2 (+) fdaSumVal fdaInputs.[i]
        fdaSum <- fdaSumVal

    [<Benchmark(Baseline=true)>]
    member this.AdaptiveSlop() =
        for i in 1..this.Iterations do
            // Change one input in the middle
            slopInputs.[this.Width / 2].Set(i)
            let _ = AdaptiveSlop.Core.AVal.getValue slopSum
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        for i in 1..this.Iterations do
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
            if nodes.Length = 1 then nodes.[0]
            else
                let parentCount = (nodes.Length + this.BranchingFactor - 1) / this.BranchingFactor
                let parents = Array.init parentCount (fun i ->
                    let start = i * this.BranchingFactor
                    let endIdx = min (start + this.BranchingFactor) nodes.Length
                    let mutable combined = nodes.[start]
                    for j in (start + 1)..(endIdx - 1) do
                        combined <- AdaptiveSlop.Core.AVal.map2 (+) combined nodes.[j]
                    combined)
                buildLevel parents
        
        slopRoot <- buildLevel (slopInputs |> Array.map AdaptiveSlop.Core.CVal.value)

        // FDA tree
        fdaInputs <- Array.init leafCount (fun i -> cval i)
        
        let rec buildFdaLevel (nodes: aval<int>[]) =
            if nodes.Length = 1 then nodes.[0]
            else
                let parentCount = (nodes.Length + this.BranchingFactor - 1) / this.BranchingFactor
                let parents = Array.init parentCount (fun i ->
                    let start = i * this.BranchingFactor
                    let endIdx = min (start + this.BranchingFactor) nodes.Length
                    let mutable combined = nodes.[start]
                    for j in (start + 1)..(endIdx - 1) do
                        combined <- AVal.map2 (+) combined nodes.[j]
                    combined)
                buildFdaLevel parents
        
        fdaRoot <- buildFdaLevel (fdaInputs |> Array.map (fun x -> x :> aval<int>))

    [<Benchmark(Baseline=true)>]
    member this.AdaptiveSlop() =
        for i in 1..this.Iterations do
            // Change a leaf node
            slopInputs.[slopInputs.Length / 2].Set(i)
            let _ = AdaptiveSlop.Core.AVal.getValue slopRoot
            ()

    [<Benchmark>]
    member this.FSharpDataAdaptive() =
        for i in 1..this.Iterations do
            transact (fun () -> fdaInputs.[fdaInputs.Length / 2].Value <- i)
            let _ = AVal.force fdaRoot
            ()

// =============================================================================
// Unbalanced Tree Benchmark (asymmetric structure)
// =============================================================================

[<MemoryDiagnoser>]
type UnbalancedTreeBenchmarks() =
    // Unbalanced: One deep branch + many shallow branches
    // Deep branch: input -> map -> map -> ... -> map (depth levels)
    // Shallow branches: input -> map (1 level each)
    // All combine at the end
    let mutable slopDeepInput: AdaptiveSlop.Core.ChangeableValue<int> = Unchecked.defaultof<_>
    let mutable slopShallowInputs: AdaptiveSlop.Core.ChangeableValue<int>[] = [||]
    let mutable slopResult: AdaptiveSlop.Core.IAdaptiveValue<int> = Unchecked.defaultof<_>
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
        let mutable deepChain: AdaptiveSlop.Core.IAdaptiveValue<int> = AdaptiveSlop.Core.CVal.value slopDeepInput
        for _ in 1..this.DeepBranchDepth do
            deepChain <- AdaptiveSlop.Core.AVal.map (fun v -> v + 1) deepChain

        slopShallowInputs <- Array.init this.ShallowBranchCount (fun i -> AdaptiveSlop.Core.CVal.create i)
        let shallowMapped = slopShallowInputs |> Array.map (fun cv -> 
            AdaptiveSlop.Core.AVal.map (fun v -> v * 2) (AdaptiveSlop.Core.CVal.value cv))

        // Combine deep chain with all shallow branches
        let mutable combined = deepChain
        for shallow in shallowMapped do
            combined <- AdaptiveSlop.Core.AVal.map2 (+) combined shallow
        slopResult <- combined

        // FDA unbalanced tree
        fdaDeepInput <- cval 0
        let mutable fdaDeepChain: aval<int> = fdaDeepInput
        for _ in 1..this.DeepBranchDepth do
            fdaDeepChain <- AVal.map (fun v -> v + 1) fdaDeepChain

        fdaShallowInputs <- Array.init this.ShallowBranchCount (fun i -> cval i)
        let fdaShallowMapped = fdaShallowInputs |> Array.map (fun cv -> 
            AVal.map (fun v -> v * 2) cv)

        let mutable fdaCombined = fdaDeepChain
        for shallow in fdaShallowMapped do
            fdaCombined <- AVal.map2 (+) fdaCombined shallow
        fdaResult <- fdaCombined

    [<Benchmark(Baseline=true, Description="AdaptiveSlop_DeepChange")>]
    member this.AdaptiveSlop_ChangeDeep() =
        for i in 1..this.Iterations do
            slopDeepInput.Set(i)
            let _ = AdaptiveSlop.Core.AVal.getValue slopResult
            ()

    [<Benchmark(Description="FDA_DeepChange")>]
    member this.FSharpDataAdaptive_ChangeDeep() =
        for i in 1..this.Iterations do
            transact (fun () -> fdaDeepInput.Value <- i)
            let _ = AVal.force fdaResult
            ()

    [<Benchmark(Description="AdaptiveSlop_ShallowChange")>]
    member this.AdaptiveSlop_ChangeShallow() =
        for i in 1..this.Iterations do
            slopShallowInputs.[0].Set(i)
            let _ = AdaptiveSlop.Core.AVal.getValue slopResult
            ()

    [<Benchmark(Description="FDA_ShallowChange")>]
    member this.FSharpDataAdaptive_ChangeShallow() =
        for i in 1..this.Iterations do
            transact (fun () -> fdaShallowInputs.[0].Value <- i)
            let _ = AVal.force fdaResult
            ()

// =============================================================================
// Entry Point
// =============================================================================

[<EntryPoint>]
let main args =
    BenchmarkSwitcher.FromAssembly(typeof<ValueBenchmarks>.Assembly).Run(args) |> ignore
    0
