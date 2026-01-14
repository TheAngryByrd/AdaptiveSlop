module AdaptiveSlop.Benchmarks

open BenchmarkDotNet.Attributes
open BenchmarkDotNet.Running
open FSharp.Data.Adaptive

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

[<EntryPoint>]
let main args =
    BenchmarkSwitcher.FromAssembly(typeof<ValueBenchmarks>.Assembly).Run(args) |> ignore
    0
