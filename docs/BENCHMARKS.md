# Benchmark History

Baseline and regression log for AdaptiveSlop benchmarks. Run before and after any
performance-relevant change. Append a new dated section per run. Never edit old sections.

Run command:

```bash
cd benchmarks/AdaptiveSlop.Benchmarks && dotnet run -c Release -- --filter "*" --job short
```

Compare new runs against the most recent section. Treat these as regressions:

- Mean of an `AdaptiveSlop` row grows beyond the error margin of the baseline row.
- Allocated of an `AdaptiveSlop` row grows at all on a steady-state path.

Each table compares AdaptiveSlop against FSharp.Data.Adaptive (FDA) on the same workload.
Ratio 1.00 = the AdaptiveSlop row itself (baseline row per group).

---

## 2026-08-03 — Phase 0 baseline (pre-rebuild)

- Commit: ab69811 (+ uncommitted Phase 0 test additions; core code unchanged)
- Machine: macOS Tahoe 26.5.2, Intel Core i9-9980HK 2.40GHz, .NET 8.0.23, x64 RyuJIT
- Job: ShortRun (IterationCount=3, LaunchCount=1, WarmupCount=3)
- State of the code: pull-based with locks, ThreadStatics, and dead push machinery.
  This is the reference for all rebuild phases. ConcurrentBenchmarks measures the old
  multi-threaded model; the rebuild removes that model and this class stops being
  comparable until cross-thread posting exists again.

<!-- Generated from the Phase 0 baseline run. Edit only by appending new entries. -->

### BindBenchmarks

| Method             | Iterations | Mean     | Error      | StdDev   | Ratio | RatioSD | Gen0    | Allocated | Alloc Ratio |
|------------------- |----------- |---------:|-----------:|---------:|------:|--------:|--------:|----------:|------------:|
| AdaptiveSlop       | 1000       | 543.6 μs |   834.0 μs | 45.71 μs |  1.00 |    0.10 | 10.7422 | 349.75 KB |        1.00 |
| FSharpDataAdaptive | 1000       | 858.6 μs | 1,220.6 μs | 66.91 μs |  1.59 |    0.15 | 69.3359 | 570.31 KB |        1.63 |

### ConcurrentBenchmarks

| Method             | ThreadCount | IterationsPerThread | Mean     | Error    | StdDev   | Ratio | RatioSD | Gen0     | Allocated | Alloc Ratio |
|------------------- |------------ |-------------------- |---------:|---------:|---------:|------:|--------:|---------:|----------:|------------:|
| AdaptiveSlop       | 4           | 500                 | 697.7 μs | 494.7 μs | 27.12 μs |  1.00 |    0.05 |   6.8359 | 351.33 KB |        1.00 |
| FSharpDataAdaptive | 4           | 500                 | 936.4 μs | 726.2 μs | 39.81 μs |  1.34 |    0.07 | 112.3047 |  922.7 KB |        2.63 |

### DeepChainBenchmarks

| Method             | Depth | Iterations | Mean         | Error       | StdDev     | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------- |------ |----------- |-------------:|------------:|-----------:|------:|--------:|-------:|----------:|------------:|
| **AdaptiveSlop**       | **5**     | **100**        |     **63.61 μs** |    **62.67 μs** |   **3.435 μs** |  **1.00** |    **0.07** | **0.3662** |   **3.13 KB** |        **1.00** |
| FSharpDataAdaptive | 5     | 100        |    111.49 μs |    52.00 μs |   2.850 μs |  1.76 |    0.09 | 5.6152 |  46.09 KB |       14.75 |
|                    |       |            |              |             |            |       |         |        |           |             |
| **AdaptiveSlop**       | **10**    | **100**        |    **124.23 μs** |    **62.19 μs** |   **3.409 μs** |  **1.00** |    **0.03** | **0.3662** |   **3.13 KB** |        **1.00** |
| FSharpDataAdaptive | 10    | 100        |    186.19 μs |    12.94 μs |   0.709 μs |  1.50 |    0.04 | 5.6152 |  46.09 KB |       14.75 |
|                    |       |            |              |             |            |       |         |        |           |             |
| **AdaptiveSlop**       | **20**    | **100**        |    **271.97 μs** |    **91.42 μs** |   **5.011 μs** |  **1.00** |    **0.02** |      **-** |   **3.13 KB** |        **1.00** |
| FSharpDataAdaptive | 20    | 100        |    354.03 μs |    21.57 μs |   1.182 μs |  1.30 |    0.02 | 5.3711 |  46.09 KB |       14.75 |
|                    |       |            |              |             |            |       |         |        |           |             |
| **AdaptiveSlop**       | **100**   | **100**        |  **1,312.37 μs** | **1,346.92 μs** |  **73.829 μs** |  **1.00** |    **0.07** |      **-** |   **3.13 KB** |        **1.00** |
| FSharpDataAdaptive | 100   | 100        |  1,599.49 μs |   142.50 μs |   7.811 μs |  1.22 |    0.06 | 3.9063 |  46.09 KB |       14.75 |
|                    |       |            |              |             |            |       |         |        |           |             |
| **AdaptiveSlop**       | **1000**  | **100**        | **12,469.03 μs** |   **620.76 μs** |  **34.026 μs** |  **1.00** |    **0.00** |      **-** |  **35.13 KB** |        **1.00** |
| FSharpDataAdaptive | 1000  | 100        | 17,938.90 μs | 3,042.26 μs | 166.756 μs |  1.44 |    0.01 |      - |  46.09 KB |        1.31 |

### DeepWideBenchmarks

| Method             | Depth | BranchingFactor | Iterations | Mean         | Error        | StdDev     | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------- |------ |---------------- |----------- |-------------:|-------------:|-----------:|------:|--------:|-------:|----------:|------------:|
| **AdaptiveSlop**       | **3**     | **2**               | **50**         |     **31.78 μs** |    **42.400 μs** |   **2.324 μs** |  **1.00** |    **0.09** | **0.1831** |   **1.56 KB** |        **1.00** |
| FSharpDataAdaptive | 3     | 2               | 50         |     50.28 μs |     6.298 μs |   0.345 μs |  1.59 |    0.10 | 2.8076 |  23.05 KB |       14.75 |
|                    |       |                 |            |              |              |            |       |         |        |           |             |
| **AdaptiveSlop**       | **3**     | **3**               | **50**         |     **78.18 μs** |    **22.465 μs** |   **1.231 μs** |  **1.00** |    **0.02** | **0.1221** |   **1.56 KB** |        **1.00** |
| FSharpDataAdaptive | 3     | 3               | 50         |     86.57 μs |     6.856 μs |   0.376 μs |  1.11 |    0.02 | 2.8076 |  23.05 KB |       14.75 |
|                    |       |                 |            |              |              |            |       |         |        |           |             |
| **AdaptiveSlop**       | **3**     | **4**               | **50**         |    **140.98 μs** |    **38.880 μs** |   **2.131 μs** |  **1.00** |    **0.02** |      **-** |   **1.56 KB** |        **1.00** |
| FSharpDataAdaptive | 3     | 4               | 50         |    108.23 μs |     2.836 μs |   0.155 μs |  0.77 |    0.01 | 2.8076 |  23.05 KB |       14.75 |
|                    |       |                 |            |              |              |            |       |         |        |           |             |
| **AdaptiveSlop**       | **5**     | **2**               | **50**         |     **84.15 μs** |    **17.119 μs** |   **0.938 μs** |  **1.00** |    **0.01** | **0.1221** |   **1.56 KB** |        **1.00** |
| FSharpDataAdaptive | 5     | 2               | 50         |     72.72 μs |     6.729 μs |   0.369 μs |  0.86 |    0.01 | 2.8076 |  23.05 KB |       14.75 |
|                    |       |                 |            |              |              |            |       |         |        |           |             |
| **AdaptiveSlop**       | **5**     | **3**               | **50**         |    **438.31 μs** |    **64.160 μs** |   **3.517 μs** |  **1.00** |    **0.01** |      **-** |   **1.56 KB** |        **1.00** |
| FSharpDataAdaptive | 5     | 3               | 50         |    136.84 μs |     9.642 μs |   0.528 μs |  0.31 |    0.00 | 2.6855 |  23.05 KB |       14.75 |
|                    |       |                 |            |              |              |            |       |         |        |           |             |
| **AdaptiveSlop**       | **5**     | **4**               | **50**         |  **2,257.11 μs** |    **91.045 μs** |   **4.990 μs** |  **1.00** |    **0.00** |      **-** |   **1.56 KB** |        **1.00** |
| FSharpDataAdaptive | 5     | 4               | 50         |    169.13 μs |    55.287 μs |   3.030 μs |  0.07 |    0.00 | 2.6855 |  23.05 KB |       14.75 |
|                    |       |                 |            |              |              |            |       |         |        |           |             |
| **AdaptiveSlop**       | **7**     | **2**               | **50**         |    **325.16 μs** |    **18.016 μs** |   **0.988 μs** |  **1.00** |    **0.00** |      **-** |   **1.56 KB** |        **1.00** |
| FSharpDataAdaptive | 7     | 2               | 50         |     99.06 μs |    12.927 μs |   0.709 μs |  0.30 |    0.00 | 2.8076 |  23.05 KB |       14.75 |
|                    |       |                 |            |              |              |            |       |         |        |           |             |
| **AdaptiveSlop**       | **7**     | **3**               | **50**         |  **5,030.82 μs** |   **121.570 μs** |   **6.664 μs** |  **1.00** |    **0.00** |      **-** |   **1.56 KB** |        **1.00** |
| FSharpDataAdaptive | 7     | 3               | 50         |    190.18 μs |    28.463 μs |   1.560 μs |  0.04 |    0.00 | 2.6855 |  23.05 KB |       14.75 |
|                    |       |                 |            |              |              |            |       |         |        |           |             |
| **AdaptiveSlop**       | **7**     | **4**               | **50**         | **40,659.66 μs** | **8,545.159 μs** | **468.389 μs** | **1.000** |    **0.01** |      **-** |   **1.56 KB** |        **1.00** |
| FSharpDataAdaptive | 7     | 4               | 50         |    265.11 μs |    24.973 μs |   1.369 μs | 0.007 |    0.00 | 2.4414 |  23.05 KB |       14.75 |

### DiamondGraphBenchmarks

| Method             | Iterations | Mean       | Error     | StdDev  | Ratio | RatioSD | Gen0    | Allocated | Alloc Ratio |
|------------------- |----------- |-----------:|----------:|--------:|------:|--------:|--------:|----------:|------------:|
| AdaptiveSlop       | 1000       |   433.3 μs |  91.19 μs | 5.00 μs |  1.00 |    0.01 |  3.4180 | 287.25 KB |        1.00 |
| FSharpDataAdaptive | 1000       | 1,020.4 μs | 129.12 μs | 7.08 μs |  2.36 |    0.03 | 72.2656 | 601.56 KB |        2.09 |

### IncrementalChainBenchmarks

| Method             | InitialSize | Mutations | Mean     | Error     | StdDev  | Ratio | RatioSD | Gen0     | Allocated | Alloc Ratio |
|------------------- |------------ |---------- |---------:|----------:|--------:|------:|--------:|---------:|----------:|------------:|
| **AdaptiveSlop**       | **100**         | **200**       | **314.5 μs** |  **16.80 μs** | **0.92 μs** |  **1.00** |    **0.00** |  **25.3906** | **209.92 KB** |        **1.00** |
| FSharpDataAdaptive | 100         | 200       | 770.9 μs | 160.54 μs | 8.80 μs |  2.45 |    0.03 |  76.1719 | 623.73 KB |        2.97 |
|                    |             |           |          |           |         |       |         |          |           |             |
| **AdaptiveSlop**       | **1000**        | **200**       | **339.2 μs** |  **28.15 μs** | **1.54 μs** |  **1.00** |    **0.01** |  **34.1797** | **279.53 KB** |        **1.00** |
| FSharpDataAdaptive | 1000        | 200       | 767.0 μs | 154.26 μs | 8.46 μs |  2.26 |    0.02 |  78.1250 | 641.13 KB |        2.29 |
|                    |             |           |          |           |         |       |         |          |           |             |
| **AdaptiveSlop**       | **10000**       | **200**       | **397.1 μs** |  **70.77 μs** | **3.88 μs** |  **1.00** |    **0.01** |  **45.4102** | **372.34 KB** |        **1.00** |
| FSharpDataAdaptive | 10000       | 200       | 903.5 μs | 145.03 μs | 7.95 μs |  2.28 |    0.03 | 101.5625 | 832.75 KB |        2.24 |

### LargeCollectionBenchmarks

| Method             | InitialSize | Iterations | Mean      | Error     | StdDev   | Ratio | RatioSD | Gen0    | Gen1   | Allocated | Alloc Ratio |
|------------------- |------------ |----------- |----------:|----------:|---------:|------:|--------:|--------:|-------:|----------:|------------:|
| AdaptiveSlop       | 10000       | 200        |  12.53 μs |  4.693 μs | 0.257 μs |  1.00 |    0.03 |  0.7629 |      - |   6.25 KB |        1.00 |
| FSharpDataAdaptive | 10000       | 200        | 124.49 μs | 94.920 μs | 5.203 μs |  9.94 |    0.40 | 28.0762 | 1.7090 | 229.94 KB |       36.79 |

### Map2Benchmarks

| Method             | Iterations | Mean     | Error     | StdDev  | Ratio | RatioSD | Gen0    | Allocated | Alloc Ratio |
|------------------- |----------- |---------:|----------:|--------:|------:|--------:|--------:|----------:|------------:|
| AdaptiveSlop       | 1000       | 336.0 μs |  11.31 μs | 0.62 μs |  1.00 |    0.00 |  7.3242 |  318.5 KB |        1.00 |
| FSharpDataAdaptive | 1000       | 664.4 μs | 109.38 μs | 6.00 μs |  1.98 |    0.02 | 62.5000 | 515.63 KB |        1.62 |

### MapBenchmarks

| Method             | Iterations | Mean     | Error     | StdDev   | Ratio | RatioSD | Gen0     | Allocated | Alloc Ratio |
|------------------- |----------- |---------:|----------:|---------:|------:|--------:|---------:|----------:|------------:|
| AdaptiveSlop       | 1000       | 236.5 μs |   5.81 μs |  0.32 μs |  1.00 |    0.00 |   7.3242 |   62.5 KB |        1.00 |
| FSharpDataAdaptive | 1000       | 456.3 μs | 643.46 μs | 35.27 μs |  1.93 |    0.13 | 104.9805 | 859.38 KB |       13.75 |

### MapTransformBenchmarks

| Method             | Iterations | Mean       | Error      | StdDev   | Ratio | RatioSD | Gen0     | Allocated  | Alloc Ratio |
|------------------- |----------- |-----------:|-----------:|---------:|------:|--------:|---------:|-----------:|------------:|
| AdaptiveSlop       | 500        |   857.4 μs |   357.8 μs | 19.61 μs |  1.00 |    0.03 |  88.8672 |  732.88 KB |        1.00 |
| FSharpDataAdaptive | 500        | 1,748.8 μs | 1,314.0 μs | 72.03 μs |  2.04 |    0.08 | 210.9375 | 1727.94 KB |        2.36 |

### OptimizedWideTreeBenchmarks

| Method                 | Width | Iterations | Mean        | Error         | StdDev     | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------------- |------ |----------- |------------:|--------------:|-----------:|------:|--------:|-------:|----------:|------------:|
| **AdaptiveSlop_Map2Chain** | **10**    | **100**        |    **92.52 μs** |     **30.871 μs** |   **1.692 μs** |  **1.00** |    **0.02** | **0.3662** |   **3.13 KB** |        **1.00** |
| AdaptiveSlop_Reduce    | 10    | 100        |    49.71 μs |     39.826 μs |   2.183 μs |  0.54 |    0.02 | 0.3662 |   3.13 KB |        1.00 |
| FSharpDataAdaptive     | 10    | 100        |   146.54 μs |      4.553 μs |   0.250 μs |  1.58 |    0.02 | 5.6152 |  46.09 KB |       14.75 |
|                        |       |            |             |               |            |       |         |        |           |             |
| **AdaptiveSlop_Map2Chain** | **50**    | **100**        |   **517.82 μs** |    **152.761 μs** |   **8.373 μs** |  **1.00** |    **0.02** |      **-** |   **3.13 KB** |        **1.00** |
| AdaptiveSlop_Reduce    | 50    | 100        |   182.94 μs |     22.216 μs |   1.218 μs |  0.35 |    0.01 | 0.2441 |   3.13 KB |        1.00 |
| FSharpDataAdaptive     | 50    | 100        |   622.56 μs |    173.708 μs |   9.522 μs |  1.20 |    0.02 | 4.8828 |  46.09 KB |       14.75 |
|                        |       |            |             |               |            |       |         |        |           |             |
| **AdaptiveSlop_Map2Chain** | **100**   | **100**        | **1,079.14 μs** |    **955.741 μs** |  **52.387 μs** |  **1.00** |    **0.06** |      **-** |   **3.13 KB** |        **1.00** |
| AdaptiveSlop_Reduce    | 100   | 100        |   354.20 μs |     13.201 μs |   0.724 μs |  0.33 |    0.01 |      - |   3.13 KB |        1.00 |
| FSharpDataAdaptive     | 100   | 100        | 1,213.92 μs |    957.391 μs |  52.478 μs |  1.13 |    0.06 | 3.9063 |  46.09 KB |       14.75 |
|                        |       |            |             |               |            |       |         |        |           |             |
| **AdaptiveSlop_Map2Chain** | **500**   | **100**        | **5,619.55 μs** | **10,329.346 μs** | **566.186 μs** |  **1.01** |    **0.12** |      **-** |   **3.13 KB** |        **1.00** |
| AdaptiveSlop_Reduce    | 500   | 100        | 1,675.51 μs |     89.197 μs |   4.889 μs |  0.30 |    0.02 |      - |   3.13 KB |        1.00 |
| FSharpDataAdaptive     | 500   | 100        | 5,873.70 μs |  1,193.119 μs |  65.399 μs |  1.05 |    0.09 |      - |  46.09 KB |       14.75 |

### ReadHeavyBenchmarks

| Method             | WriteCount | ReadsPerWrite | Mean     | Error       | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------- |----------- |-------------- |---------:|------------:|----------:|------:|--------:|-------:|----------:|------------:|
| AdaptiveSlop       | 100        | 50            | 367.5 μs | 2,870.29 μs | 157.33 μs |  1.11 |    0.55 |      - |   3.13 KB |        1.00 |
| FSharpDataAdaptive | 100        | 50            | 189.8 μs |    41.02 μs |   2.25 μs |  0.57 |    0.18 | 5.6152 |  46.09 KB |       14.75 |

### SetBenchmarks

| Method             | Iterations | Mean     | Error    | StdDev  | Ratio | RatioSD | Gen0     | Allocated | Alloc Ratio |
|------------------- |----------- |---------:|---------:|--------:|------:|--------:|---------:|----------:|------------:|
| AdaptiveSlop       | 1000       | 311.9 μs | 72.10 μs | 3.95 μs |  1.00 |    0.02 |   7.3242 |   62.5 KB |        1.00 |
| FSharpDataAdaptive | 1000       | 499.4 μs | 55.85 μs | 3.06 μs |  1.60 |    0.02 | 105.4688 | 867.19 KB |       13.88 |

### SetTransformBenchmarks

| Method             | Iterations | Mean       | Error     | StdDev  | Ratio | Gen0     | Allocated  | Alloc Ratio |
|------------------- |----------- |-----------:|----------:|--------:|------:|---------:|-----------:|------------:|
| AdaptiveSlop       | 500        |   790.7 μs |  54.44 μs | 2.98 μs |  1.00 |  68.3594 |  558.43 KB |        1.00 |
| FSharpDataAdaptive | 500        | 1,878.6 μs | 156.46 μs | 8.58 μs |  2.38 | 185.5469 | 1524.86 KB |        2.73 |

### TransactionBenchmarks

| Method                     | ValueCount | Iterations | Mean     | Error     | StdDev    | Ratio | RatioSD | Gen0    | Allocated | Alloc Ratio |
|--------------------------- |----------- |----------- |---------:|----------:|----------:|------:|--------:|--------:|----------:|------------:|
| AdaptiveSlop_Batched       | 10         | 500        | 1.453 ms | 0.6780 ms | 0.0372 ms |  1.00 |    0.03 | 39.0625 | 456.13 KB |        1.00 |
| FSharpDataAdaptive_Batched | 10         | 500        | 2.643 ms | 0.0801 ms | 0.0044 ms |  1.82 |    0.04 | 85.9375 | 726.56 KB |        1.59 |

### UnbalancedTreeBenchmarks

| Method                     | DeepBranchDepth | ShallowBranchCount | Iterations | Mean        | Error        | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|--------------------------- |---------------- |------------------- |----------- |------------:|-------------:|----------:|------:|--------:|-------:|----------:|------------:|
| **AdaptiveSlop_DeepChange**    | **10**              | **5**                  | **50**         |   **120.45 μs** |    **31.820 μs** |  **1.744 μs** |  **1.00** |    **0.02** | **0.1221** |   **1.56 KB** |        **1.00** |
| FDA_DeepChange             | 10              | 5                  | 50         |   207.57 μs |     3.733 μs |  0.205 μs |  1.72 |    0.02 | 2.6855 |  23.05 KB |       14.75 |
| AdaptiveSlop_ShallowChange | 10              | 5                  | 50         |    80.89 μs |    33.422 μs |  1.832 μs |  0.67 |    0.02 | 0.1221 |   1.56 KB |        1.00 |
| FDA_ShallowChange          | 10              | 5                  | 50         |   104.44 μs |    73.888 μs |  4.050 μs |  0.87 |    0.03 | 2.8076 |  23.05 KB |       14.75 |
|                            |                 |                    |            |             |              |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **10**              | **20**                 | **50**         |   **286.46 μs** |    **30.854 μs** |  **1.691 μs** |  **1.00** |    **0.01** |      **-** |   **1.56 KB** |        **1.00** |
| FDA_DeepChange             | 10              | 20                 | 50         |   413.18 μs |    35.402 μs |  1.941 μs |  1.44 |    0.01 | 2.4414 |  23.05 KB |       14.75 |
| AdaptiveSlop_ShallowChange | 10              | 20                 | 50         |   228.77 μs |    15.449 μs |  0.847 μs |  0.80 |    0.00 |      - |   1.56 KB |        1.00 |
| FDA_ShallowChange          | 10              | 20                 | 50         |   301.37 μs |   101.712 μs |  5.575 μs |  1.05 |    0.02 | 2.4414 |  23.05 KB |       14.75 |
|                            |                 |                    |            |             |              |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **10**              | **50**                 | **50**         |   **596.07 μs** |   **532.232 μs** | **29.173 μs** |  **1.00** |    **0.06** |      **-** |   **1.56 KB** |        **1.00** |
| FDA_DeepChange             | 10              | 50                 | 50         |   828.29 μs |    19.570 μs |  1.073 μs |  1.39 |    0.06 | 1.9531 |  23.05 KB |       14.75 |
| AdaptiveSlop_ShallowChange | 10              | 50                 | 50         |   547.03 μs |   134.649 μs |  7.381 μs |  0.92 |    0.04 |      - |   1.56 KB |        1.00 |
| FDA_ShallowChange          | 10              | 50                 | 50         |   751.48 μs |    15.956 μs |  0.875 μs |  1.26 |    0.05 | 1.9531 |  23.05 KB |       14.75 |
|                            |                 |                    |            |             |              |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **50**              | **5**                  | **50**         |   **472.11 μs** |   **373.143 μs** | **20.453 μs** |  **1.00** |    **0.05** |      **-** |   **1.56 KB** |        **1.00** |
| FDA_DeepChange             | 50              | 5                  | 50         |   690.97 μs |   237.653 μs | 13.027 μs |  1.47 |    0.06 | 1.9531 |  23.05 KB |       14.75 |
| AdaptiveSlop_ShallowChange | 50              | 5                  | 50         |   198.65 μs |    32.172 μs |  1.763 μs |  0.42 |    0.02 |      - |   1.56 KB |        1.00 |
| FDA_ShallowChange          | 50              | 5                  | 50         |   112.00 μs |     1.851 μs |  0.101 μs |  0.24 |    0.01 | 2.8076 |  23.05 KB |       14.75 |
|                            |                 |                    |            |             |              |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **50**              | **20**                 | **50**         |   **691.93 μs** |    **42.700 μs** |  **2.341 μs** |  **1.00** |    **0.00** |      **-** |   **1.56 KB** |        **1.00** |
| FDA_DeepChange             | 50              | 20                 | 50         |   980.60 μs |    16.813 μs |  0.922 μs |  1.42 |    0.00 | 1.9531 |  23.05 KB |       14.75 |
| AdaptiveSlop_ShallowChange | 50              | 20                 | 50         |   382.98 μs |   237.906 μs | 13.040 μs |  0.55 |    0.02 |      - |   1.56 KB |        1.00 |
| FDA_ShallowChange          | 50              | 20                 | 50         |   368.63 μs |   242.305 μs | 13.282 μs |  0.53 |    0.02 | 2.4414 |  23.05 KB |       14.75 |
|                            |                 |                    |            |             |              |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **50**              | **50**                 | **50**         | **1,096.84 μs** | **1,000.787 μs** | **54.857 μs** |  **1.00** |    **0.06** |      **-** |   **1.56 KB** |        **1.00** |
| FDA_DeepChange             | 50              | 50                 | 50         | 1,495.43 μs |    38.535 μs |  2.112 μs |  1.37 |    0.06 | 1.9531 |  23.05 KB |       14.75 |
| AdaptiveSlop_ShallowChange | 50              | 50                 | 50         |   718.54 μs |    40.149 μs |  2.201 μs |  0.66 |    0.03 |      - |   1.56 KB |        1.00 |
| FDA_ShallowChange          | 50              | 50                 | 50         |   847.71 μs |   471.440 μs | 25.841 μs |  0.77 |    0.04 | 1.9531 |  23.05 KB |       14.75 |
|                            |                 |                    |            |             |              |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **100**             | **5**                  | **50**         |   **965.59 μs** |    **40.776 μs** |  **2.235 μs** |  **1.00** |    **0.00** |      **-** |   **1.56 KB** |        **1.00** |
| FDA_DeepChange             | 100             | 5                  | 50         | 1,252.11 μs |    33.282 μs |  1.824 μs |  1.30 |    0.00 | 1.9531 |  23.05 KB |       14.75 |
| AdaptiveSlop_ShallowChange | 100             | 5                  | 50         |   337.78 μs |    32.145 μs |  1.762 μs |  0.35 |    0.00 |      - |   1.56 KB |        1.00 |
| FDA_ShallowChange          | 100             | 5                  | 50         |   112.02 μs |     1.792 μs |  0.098 μs |  0.12 |    0.00 | 2.8076 |  23.05 KB |       14.75 |
|                            |                 |                    |            |             |              |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **100**             | **20**                 | **50**         | **1,152.35 μs** |    **34.459 μs** |  **1.889 μs** |  **1.00** |    **0.00** |      **-** |   **1.56 KB** |        **1.00** |
| FDA_DeepChange             | 100             | 20                 | 50         | 1,558.95 μs |   906.669 μs | 49.698 μs |  1.35 |    0.04 | 1.9531 |  23.05 KB |       14.75 |
| AdaptiveSlop_ShallowChange | 100             | 20                 | 50         |   511.39 μs |    33.860 μs |  1.856 μs |  0.44 |    0.00 |      - |   1.56 KB |        1.00 |
| FDA_ShallowChange          | 100             | 20                 | 50         |   356.59 μs |     3.027 μs |  0.166 μs |  0.31 |    0.00 | 2.4414 |  23.05 KB |       14.75 |
|                            |                 |                    |            |             |              |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **100**             | **50**                 | **50**         | **1,508.22 μs** |    **76.897 μs** |  **4.215 μs** |  **1.00** |    **0.00** |      **-** |   **1.56 KB** |        **1.00** |
| FDA_DeepChange             | 100             | 50                 | 50         | 2,074.41 μs |    46.807 μs |  2.566 μs |  1.38 |    0.00 |      - |  23.05 KB |       14.75 |
| AdaptiveSlop_ShallowChange | 100             | 50                 | 50         |   867.45 μs |    22.558 μs |  1.236 μs |  0.58 |    0.00 |      - |   1.56 KB |        1.00 |
| FDA_ShallowChange          | 100             | 50                 | 50         |   816.16 μs |     5.074 μs |  0.278 μs |  0.54 |    0.00 | 1.9531 |  23.05 KB |       14.75 |

### ValueBenchmarks

| Method             | Iterations | Mean     | Error    | StdDev   | Ratio | RatioSD | Gen0    | Allocated | Alloc Ratio |
|------------------- |----------- |---------:|---------:|---------:|------:|--------:|--------:|----------:|------------:|
| AdaptiveSlop       | 1000       | 291.5 μs | 223.8 μs | 12.27 μs |  1.00 |    0.05 |  3.4180 | 287.25 KB |        1.00 |
| FSharpDataAdaptive | 1000       | 621.6 μs | 477.6 μs | 26.18 μs |  2.13 |    0.11 | 55.6641 | 460.94 KB |        1.60 |

### WideTreeBenchmarks

| Method             | Width | Iterations | Mean       | Error       | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------- |------ |----------- |-----------:|------------:|----------:|------:|--------:|-------:|----------:|------------:|
| **AdaptiveSlop**       | **10**    | **100**        |   **143.4 μs** |   **215.72 μs** |  **11.82 μs** |  **1.00** |    **0.10** | **0.2441** |   **3.13 KB** |        **1.00** |
| FSharpDataAdaptive | 10    | 100        |   224.5 μs |     3.31 μs |   0.18 μs |  1.57 |    0.11 | 5.6152 |  46.09 KB |       14.75 |
|                    |       |            |            |             |           |       |         |        |           |             |
| **AdaptiveSlop**       | **50**    | **100**        |   **786.0 μs** |    **68.45 μs** |   **3.75 μs** |  **1.00** |    **0.01** |      **-** |   **3.13 KB** |        **1.00** |
| FSharpDataAdaptive | 50    | 100        |   937.1 μs |    16.37 μs |   0.90 μs |  1.19 |    0.01 | 4.8828 |  46.09 KB |       14.75 |
|                    |       |            |            |             |           |       |         |        |           |             |
| **AdaptiveSlop**       | **100**   | **100**        | **1,626.9 μs** | **1,075.66 μs** |  **58.96 μs** |  **1.00** |    **0.04** |      **-** |   **3.13 KB** |        **1.00** |
| FSharpDataAdaptive | 100   | 100        | 1,836.3 μs |   111.05 μs |   6.09 μs |  1.13 |    0.03 | 3.9063 |  46.09 KB |       14.75 |
|                    |       |            |            |             |           |       |         |        |           |             |
| **AdaptiveSlop**       | **500**   | **100**        | **8,571.6 μs** |   **596.20 μs** |  **32.68 μs** |  **1.00** |    **0.00** |      **-** |  **35.13 KB** |        **1.00** |
| FSharpDataAdaptive | 500   | 100        | 9,739.8 μs | 8,151.94 μs | 446.84 μs |  1.14 |    0.05 |      - |  46.09 KB |        1.31 |


---

## 2026-08-03 — Post-Phase-4 (push-mark, pull-evaluate)

- Branch: core-redesign. Core rebuilt per docs/PLAN.md Phases 0-4:
  no locks, owner-thread confinement, edge protocol, push-marking, transaction
  coalescing, observation.
- Machine and job: same as the Phase 0 baseline.
- Note: ConcurrentBenchmarks still exercises the old multi-thread model. The core
  no longer supports that model; treat its numbers as void until Phase 5 (Post/Pump).

### BindBenchmarks

| Method             | Iterations | Mean      | Error      | StdDev    | Ratio | RatioSD | Gen0    | Allocated | Alloc Ratio |
|------------------- |----------- |----------:|-----------:|----------:|------:|--------:|--------:|----------:|------------:|
| AdaptiveSlop       | 1000       |  82.51 μs |   8.658 μs |  0.475 μs |  1.00 |    0.01 |       - |         - |          NA |
| FSharpDataAdaptive | 1000       | 739.68 μs | 709.837 μs | 38.909 μs |  8.97 |    0.41 | 69.3359 |  584000 B |          NA |

### ConcurrentBenchmarks

| Method             | ThreadCount | IterationsPerThread | Mean     | Error    | StdDev  | Ratio | RatioSD | Gen0     | Allocated | Alloc Ratio |
|------------------- |------------ |-------------------- |---------:|---------:|--------:|------:|--------:|---------:|----------:|------------:|
| AdaptiveSlop       | 4           | 500                 |       NA |       NA |      NA |     ? |       ? |       NA |        NA |           ? |
| FSharpDataAdaptive | 4           | 500                 | 763.5 μs | 39.05 μs | 2.14 μs |     ? |       ? | 112.3047 |  922.7 KB |           ? |

### DeepChainBenchmarks

| Method             | Depth | Iterations | Mean         | Error        | StdDev     | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------- |------ |----------- |-------------:|-------------:|-----------:|------:|--------:|-------:|----------:|------------:|
| **AdaptiveSlop**       | **5**     | **100**        |     **35.11 μs** |     **2.102 μs** |   **0.115 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 5     | 100        |    107.65 μs |    95.469 μs |   5.233 μs |  3.07 |    0.13 | 5.6152 |   47200 B |          NA |
|                    |       |            |              |              |            |       |         |        |           |             |
| **AdaptiveSlop**       | **10**    | **100**        |     **74.26 μs** |    **33.087 μs** |   **1.814 μs** |  **1.00** |    **0.03** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 10    | 100        |    182.88 μs |    11.311 μs |   0.620 μs |  2.46 |    0.05 | 5.6152 |   47200 B |          NA |
|                    |       |            |              |              |            |       |         |        |           |             |
| **AdaptiveSlop**       | **20**    | **100**        |    **161.41 μs** |     **2.355 μs** |   **0.129 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 20    | 100        |    339.47 μs |   208.456 μs |  11.426 μs |  2.10 |    0.06 | 5.3711 |   47200 B |          NA |
|                    |       |            |              |              |            |       |         |        |           |             |
| **AdaptiveSlop**       | **100**   | **100**        |  **1,043.44 μs** |   **583.141 μs** |  **31.964 μs** |  **1.00** |    **0.04** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 100   | 100        |  1,768.78 μs |   846.425 μs |  46.395 μs |  1.70 |    0.06 | 3.9063 |   47200 B |          NA |
|                    |       |            |              |              |            |       |         |        |           |             |
| **AdaptiveSlop**       | **1000**  | **100**        |  **9,923.58 μs** | **1,415.095 μs** |  **77.566 μs** |  **1.00** |    **0.01** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 1000  | 100        | 16,053.04 μs | 1,855.103 μs | 101.684 μs |  1.62 |    0.01 |      - |   47200 B |          NA |

### DeepWideBenchmarks

| Method             | Depth | BranchingFactor | Iterations | Mean         | Error        | StdDev     | Ratio | RatioSD | Gen0   | Gen1   | Gen2   | Allocated | Alloc Ratio |
|------------------- |------ |---------------- |----------- |-------------:|-------------:|-----------:|------:|--------:|-------:|-------:|-------:|----------:|------------:|
| **AdaptiveSlop**       | **3**     | **2**               | **50**         |     **13.17 μs** |     **1.313 μs** |   **0.072 μs** |  **1.00** |    **0.01** |      **-** |      **-** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 3     | 2               | 50         |     45.00 μs |     3.749 μs |   0.206 μs |  3.42 |    0.02 | 2.8076 |      - |      - |   23600 B |          NA |
|                    |       |                 |            |              |              |            |       |         |        |        |        |           |             |
| **AdaptiveSlop**       | **3**     | **3**               | **50**         |     **36.44 μs** |     **2.364 μs** |   **0.130 μs** |  **1.00** |    **0.00** |      **-** |      **-** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 3     | 3               | 50         |     75.84 μs |     7.816 μs |   0.428 μs |  2.08 |    0.01 | 2.8076 |      - |      - |   23600 B |          NA |
|                    |       |                 |            |              |              |            |       |         |        |        |        |           |             |
| **AdaptiveSlop**       | **3**     | **4**               | **50**         |     **68.50 μs** |     **3.626 μs** |   **0.199 μs** |  **1.00** |    **0.00** |      **-** |      **-** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 3     | 4               | 50         |    105.41 μs |    29.186 μs |   1.600 μs |  1.54 |    0.02 | 2.8076 |      - |      - |   23600 B |          NA |
|                    |       |                 |            |              |              |            |       |         |        |        |        |           |             |
| **AdaptiveSlop**       | **5**     | **2**               | **50**         |     **34.79 μs** |    **35.261 μs** |   **1.933 μs** |  **1.00** |    **0.07** |      **-** |      **-** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 5     | 2               | 50         |     67.82 μs |     9.119 μs |   0.500 μs |  1.95 |    0.09 | 2.8076 |      - |      - |   23600 B |          NA |
|                    |       |                 |            |              |              |            |       |         |        |        |        |           |             |
| **AdaptiveSlop**       | **5**     | **3**               | **50**         |    **215.03 μs** |   **175.837 μs** |   **9.638 μs** |  **1.00** |    **0.05** |      **-** |      **-** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 5     | 3               | 50         |    122.02 μs |    61.471 μs |   3.369 μs |  0.57 |    0.03 | 2.6855 |      - |      - |   23600 B |          NA |
|                    |       |                 |            |              |              |            |       |         |        |        |        |           |             |
| **AdaptiveSlop**       | **5**     | **4**               | **50**         |  **1,151.20 μs** |   **211.314 μs** |  **11.583 μs** |  **1.00** |    **0.01** |      **-** |      **-** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 5     | 4               | 50         |    161.20 μs |    59.068 μs |   3.238 μs |  0.14 |    0.00 | 2.6855 |      - |      - |   23600 B |          NA |
|                    |       |                 |            |              |              |            |       |         |        |        |        |           |             |
| **AdaptiveSlop**       | **7**     | **2**               | **50**         |    **145.71 μs** |   **146.155 μs** |   **8.011 μs** |  **1.00** |    **0.07** |      **-** |      **-** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 7     | 2               | 50         |     87.05 μs |    14.344 μs |   0.786 μs |  0.60 |    0.03 | 2.8076 |      - |      - |   23600 B |          NA |
|                    |       |                 |            |              |              |            |       |         |        |        |        |           |             |
| **AdaptiveSlop**       | **7**     | **3**               | **50**         |  **2,468.37 μs** | **4,670.618 μs** | **256.012 μs** |  **1.01** |    **0.12** |      **-** |      **-** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 7     | 3               | 50         |    168.16 μs |    30.716 μs |   1.684 μs |  0.07 |    0.01 | 2.6855 |      - |      - |   23600 B |          NA |
|                    |       |                 |            |              |              |            |       |         |        |        |        |           |             |
| **AdaptiveSlop**       | **7**     | **4**               | **50**         | **23,156.76 μs** | **8,445.975 μs** | **462.952 μs** | **1.000** |    **0.02** |      **-** |      **-** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 7     | 4               | 50         |    225.52 μs |    10.491 μs |   0.575 μs | 0.010 |    0.00 | 2.9297 | 0.7324 | 0.2441 |   23600 B |          NA |

### DiamondGraphBenchmarks

| Method             | Iterations | Mean     | Error    | StdDev  | Ratio | RatioSD | Gen0    | Allocated | Alloc Ratio |
|------------------- |----------- |---------:|---------:|--------:|------:|--------:|--------:|----------:|------------:|
| AdaptiveSlop       | 1000       | 167.0 μs |  9.27 μs | 0.51 μs |  1.00 |    0.00 |       - |         - |          NA |
| FSharpDataAdaptive | 1000       | 920.6 μs | 62.47 μs | 3.42 μs |  5.51 |    0.02 | 73.2422 |  616000 B |          NA |

### IncrementalChainBenchmarks

| Method             | InitialSize | Mutations | Mean     | Error     | StdDev   | Ratio | RatioSD | Gen0     | Allocated | Alloc Ratio |
|------------------- |------------ |---------- |---------:|----------:|---------:|------:|--------:|---------:|----------:|------------:|
| **AdaptiveSlop**       | **100**         | **200**       | **242.9 μs** |  **53.25 μs** |  **2.92 μs** |  **1.00** |    **0.01** |  **23.9258** | **197.42 KB** |        **1.00** |
| FSharpDataAdaptive | 100         | 200       | 682.2 μs | 326.45 μs | 17.89 μs |  2.81 |    0.07 |  76.1719 | 623.73 KB |        3.16 |
|                    |             |           |          |           |          |       |         |          |           |             |
| **AdaptiveSlop**       | **1000**        | **200**       | **290.8 μs** | **453.07 μs** | **24.83 μs** |  **1.00** |    **0.10** |  **32.2266** | **267.03 KB** |        **1.00** |
| FSharpDataAdaptive | 1000        | 200       | 678.4 μs | 169.46 μs |  9.29 μs |  2.34 |    0.17 |  78.1250 | 641.13 KB |        2.40 |
|                    |             |           |          |           |          |       |         |          |           |             |
| **AdaptiveSlop**       | **10000**       | **200**       | **329.9 μs** |  **58.87 μs** |  **3.23 μs** |  **1.00** |    **0.01** |  **43.9453** | **359.84 KB** |        **1.00** |
| FSharpDataAdaptive | 10000       | 200       | 802.4 μs | 429.07 μs | 23.52 μs |  2.43 |    0.07 | 101.5625 | 832.75 KB |        2.31 |

### LargeCollectionBenchmarks

| Method             | InitialSize | Iterations | Mean       | Error      | StdDev    | Ratio | RatioSD | Gen0    | Gen1   | Allocated | Alloc Ratio |
|------------------- |------------ |----------- |-----------:|-----------:|----------:|------:|--------:|--------:|-------:|----------:|------------:|
| AdaptiveSlop       | 10000       | 200        |   2.219 μs |  0.6778 μs | 0.0372 μs |  1.00 |    0.02 |       - |      - |         - |          NA |
| FSharpDataAdaptive | 10000       | 200        | 111.465 μs | 42.6052 μs | 2.3353 μs | 50.23 |    1.17 | 28.0762 | 1.7090 |  235456 B |          NA |

### Map2Benchmarks

| Method             | Iterations | Mean      | Error      | StdDev    | Ratio | RatioSD | Gen0    | Allocated | Alloc Ratio |
|------------------- |----------- |----------:|-----------:|----------:|------:|--------:|--------:|----------:|------------:|
| AdaptiveSlop       | 1000       |  71.11 μs |   8.527 μs |  0.467 μs |  1.00 |    0.01 |       - |         - |          NA |
| FSharpDataAdaptive | 1000       | 659.07 μs | 459.833 μs | 25.205 μs |  9.27 |    0.31 | 62.5000 |  528000 B |          NA |

### MapBenchmarks

| Method             | Iterations | Mean     | Error     | StdDev   | Ratio | RatioSD | Gen0     | Allocated | Alloc Ratio |
|------------------- |----------- |---------:|----------:|---------:|------:|--------:|---------:|----------:|------------:|
| AdaptiveSlop       | 1000       | 116.8 μs |  19.42 μs |  1.06 μs |  1.00 |    0.01 |        - |         - |          NA |
| FSharpDataAdaptive | 1000       | 469.4 μs | 302.62 μs | 16.59 μs |  4.02 |    0.13 | 104.9805 |  880000 B |          NA |

### MapTransformBenchmarks

| Method             | Iterations | Mean       | Error    | StdDev   | Ratio | RatioSD | Gen0     | Allocated  | Alloc Ratio |
|------------------- |----------- |-----------:|---------:|---------:|------:|--------:|---------:|-----------:|------------:|
| AdaptiveSlop       | 500        |   653.6 μs | 252.9 μs | 13.86 μs |  1.00 |    0.03 |  84.9609 |  701.63 KB |        1.00 |
| FSharpDataAdaptive | 500        | 1,584.7 μs | 135.8 μs |  7.45 μs |  2.43 |    0.05 | 210.9375 | 1727.94 KB |        2.46 |

### OptimizedWideTreeBenchmarks

| Method                 | Width | Iterations | Mean        | Error        | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------------- |------ |----------- |------------:|-------------:|----------:|------:|--------:|-------:|----------:|------------:|
| **AdaptiveSlop_Map2Chain** | **10**    | **100**        |    **45.33 μs** |     **8.012 μs** |  **0.439 μs** |  **1.00** |    **0.01** |      **-** |         **-** |          **NA** |
| AdaptiveSlop_Reduce    | 10    | 100        |    13.59 μs |    17.176 μs |  0.941 μs |  0.30 |    0.02 |      - |         - |          NA |
| FSharpDataAdaptive     | 10    | 100        |   139.19 μs |    84.708 μs |  4.643 μs |  3.07 |    0.09 | 5.6152 |   47200 B |          NA |
|                        |       |            |             |              |           |       |         |        |           |             |
| **AdaptiveSlop_Map2Chain** | **50**    | **100**        |   **313.64 μs** |    **39.006 μs** |  **2.138 μs** |  **1.00** |    **0.01** |      **-** |         **-** |          **NA** |
| AdaptiveSlop_Reduce    | 50    | 100        |    52.34 μs |    38.738 μs |  2.123 μs |  0.17 |    0.01 |      - |         - |          NA |
| FSharpDataAdaptive     | 50    | 100        |   628.02 μs |   396.881 μs | 21.754 μs |  2.00 |    0.06 | 4.8828 |   47200 B |          NA |
|                        |       |            |             |              |           |       |         |        |           |             |
| **AdaptiveSlop_Map2Chain** | **100**   | **100**        |   **718.38 μs** |   **197.649 μs** | **10.834 μs** |  **1.00** |    **0.02** |      **-** |         **-** |          **NA** |
| AdaptiveSlop_Reduce    | 100   | 100        |   113.80 μs |    72.554 μs |  3.977 μs |  0.16 |    0.01 |      - |         - |          NA |
| FSharpDataAdaptive     | 100   | 100        | 1,218.66 μs |   114.816 μs |  6.293 μs |  1.70 |    0.02 | 3.9063 |   47200 B |          NA |
|                        |       |            |             |              |           |       |         |        |           |             |
| **AdaptiveSlop_Map2Chain** | **500**   | **100**        | **4,814.18 μs** | **1,037.049 μs** | **56.844 μs** |  **1.00** |    **0.01** |      **-** |         **-** |          **NA** |
| AdaptiveSlop_Reduce    | 500   | 100        |   460.85 μs |   102.543 μs |  5.621 μs |  0.10 |    0.00 |      - |         - |          NA |
| FSharpDataAdaptive     | 500   | 100        | 5,413.10 μs |   791.776 μs | 43.400 μs |  1.12 |    0.01 |      - |   47200 B |          NA |

### ReadHeavyBenchmarks

| Method             | WriteCount | ReadsPerWrite | Mean      | Error      | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------- |----------- |-------------- |----------:|-----------:|---------:|------:|--------:|-------:|----------:|------------:|
| AdaptiveSlop       | 100        | 50            |  71.00 μs |   2.616 μs | 0.143 μs |  1.00 |    0.00 |      - |         - |          NA |
| FSharpDataAdaptive | 100        | 50            | 162.77 μs | 162.112 μs | 8.886 μs |  2.29 |    0.11 | 5.6152 |   47200 B |          NA |

### SetBenchmarks

| Method             | Iterations | Mean     | Error     | StdDev   | Ratio | RatioSD | Gen0     | Allocated | Alloc Ratio |
|------------------- |----------- |---------:|----------:|---------:|------:|--------:|---------:|----------:|------------:|
| AdaptiveSlop       | 1000       | 159.6 μs |  33.52 μs |  1.84 μs |  1.00 |    0.01 |        - |         - |          NA |
| FSharpDataAdaptive | 1000       | 452.1 μs | 479.23 μs | 26.27 μs |  2.83 |    0.15 | 105.9570 |  888000 B |          NA |

### SetTransformBenchmarks

| Method             | Iterations | Mean       | Error    | StdDev   | Ratio | RatioSD | Gen0     | Allocated  | Alloc Ratio |
|------------------- |----------- |-----------:|---------:|---------:|------:|--------:|---------:|-----------:|------------:|
| AdaptiveSlop       | 500        |   582.6 μs | 279.9 μs | 15.34 μs |  1.00 |    0.03 |  64.4531 |  527.18 KB |        1.00 |
| FSharpDataAdaptive | 500        | 1,532.2 μs | 122.4 μs |  6.71 μs |  2.63 |    0.06 | 185.5469 | 1524.86 KB |        2.89 |

### TransactionBenchmarks

| Method                     | ValueCount | Iterations | Mean       | Error      | StdDev    | Ratio | RatioSD | Gen0    | Allocated | Alloc Ratio |
|--------------------------- |----------- |----------- |-----------:|-----------:|----------:|------:|--------:|--------:|----------:|------------:|
| AdaptiveSlop_Batched       | 10         | 500        |   496.1 μs |   762.3 μs |  41.78 μs |  1.00 |    0.10 |  0.9766 |  15.63 KB |        1.00 |
| FSharpDataAdaptive_Batched | 10         | 500        | 2,624.3 μs | 2,157.3 μs | 118.25 μs |  5.31 |    0.42 | 85.9375 | 726.56 KB |       46.50 |

### UnbalancedTreeBenchmarks

| Method                     | DeepBranchDepth | ShallowBranchCount | Iterations | Mean        | Error      | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|--------------------------- |---------------- |------------------- |----------- |------------:|-----------:|----------:|------:|--------:|-------:|----------:|------------:|
| **AdaptiveSlop_DeepChange**    | **10**              | **5**                  | **50**         |    **72.69 μs** |  **50.284 μs** |  **2.756 μs** |  **1.00** |    **0.05** |      **-** |         **-** |          **NA** |
| FDA_DeepChange             | 10              | 5                  | 50         |   151.99 μs |  34.196 μs |  1.874 μs |  2.09 |    0.07 | 2.6855 |   23600 B |          NA |
| AdaptiveSlop_ShallowChange | 10              | 5                  | 50         |    33.65 μs |  20.860 μs |  1.143 μs |  0.46 |    0.02 |      - |         - |          NA |
| FDA_ShallowChange          | 10              | 5                  | 50         |    87.97 μs |  51.199 μs |  2.806 μs |  1.21 |    0.05 | 2.8076 |   23600 B |          NA |
|                            |                 |                    |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **10**              | **20**                 | **50**         |   **179.97 μs** |  **29.717 μs** |  **1.629 μs** |  **1.00** |    **0.01** |      **-** |         **-** |          **NA** |
| FDA_DeepChange             | 10              | 20                 | 50         |   313.06 μs |  15.649 μs |  0.858 μs |  1.74 |    0.01 | 2.4414 |   23600 B |          NA |
| AdaptiveSlop_ShallowChange | 10              | 20                 | 50         |   122.30 μs |  20.980 μs |  1.150 μs |  0.68 |    0.01 |      - |         - |          NA |
| FDA_ShallowChange          | 10              | 20                 | 50         |   236.57 μs | 352.966 μs | 19.347 μs |  1.31 |    0.09 | 2.4414 |   23600 B |          NA |
|                            |                 |                    |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **10**              | **50**                 | **50**         |   **372.58 μs** |  **89.450 μs** |  **4.903 μs** |  **1.00** |    **0.02** |      **-** |         **-** |          **NA** |
| FDA_DeepChange             | 10              | 50                 | 50         |   731.75 μs | 530.663 μs | 29.087 μs |  1.96 |    0.07 | 1.9531 |   23600 B |          NA |
| AdaptiveSlop_ShallowChange | 10              | 50                 | 50         |   352.05 μs |  90.208 μs |  4.945 μs |  0.95 |    0.02 |      - |         - |          NA |
| FDA_ShallowChange          | 10              | 50                 | 50         |   621.50 μs |  40.431 μs |  2.216 μs |  1.67 |    0.02 | 1.9531 |   23600 B |          NA |
|                            |                 |                    |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **50**              | **5**                  | **50**         |   **281.10 μs** |  **13.947 μs** |  **0.765 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| FDA_DeepChange             | 50              | 5                  | 50         |   512.41 μs |  10.853 μs |  0.595 μs |  1.82 |    0.00 | 1.9531 |   23600 B |          NA |
| AdaptiveSlop_ShallowChange | 50              | 5                  | 50         |    85.27 μs |   1.441 μs |  0.079 μs |  0.30 |    0.00 |      - |         - |          NA |
| FDA_ShallowChange          | 50              | 5                  | 50         |    70.58 μs |   7.248 μs |  0.397 μs |  0.25 |    0.00 | 2.8076 |   23600 B |          NA |
|                            |                 |                    |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **50**              | **20**                 | **50**         |   **325.79 μs** |   **8.259 μs** |  **0.453 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| FDA_DeepChange             | 50              | 20                 | 50         |   622.39 μs | 204.429 μs | 11.205 μs |  1.91 |    0.03 | 1.9531 |   23600 B |          NA |
| AdaptiveSlop_ShallowChange | 50              | 20                 | 50         |   153.03 μs | 112.626 μs |  6.173 μs |  0.47 |    0.02 |      - |         - |          NA |
| FDA_ShallowChange          | 50              | 20                 | 50         |   262.79 μs |  57.690 μs |  3.162 μs |  0.81 |    0.01 | 2.4414 |   23600 B |          NA |
|                            |                 |                    |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **50**              | **50**                 | **50**         |   **508.51 μs** |  **54.045 μs** |  **2.962 μs** |  **1.00** |    **0.01** |      **-** |         **-** |          **NA** |
| FDA_DeepChange             | 50              | 50                 | 50         |   988.96 μs | 655.927 μs | 35.954 μs |  1.94 |    0.06 | 1.9531 |   23600 B |          NA |
| AdaptiveSlop_ShallowChange | 50              | 50                 | 50         |   387.62 μs | 146.200 μs |  8.014 μs |  0.76 |    0.01 |      - |         - |          NA |
| FDA_ShallowChange          | 50              | 50                 | 50         |   588.95 μs | 717.199 μs | 39.312 μs |  1.16 |    0.07 | 1.9531 |   23600 B |          NA |
|                            |                 |                    |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **100**             | **5**                  | **50**         |   **483.05 μs** | **567.041 μs** | **31.081 μs** |  **1.00** |    **0.08** |      **-** |         **-** |          **NA** |
| FDA_DeepChange             | 100             | 5                  | 50         |   818.80 μs |  97.138 μs |  5.324 μs |  1.70 |    0.10 | 1.9531 |   23600 B |          NA |
| AdaptiveSlop_ShallowChange | 100             | 5                  | 50         |   148.15 μs |  21.580 μs |  1.183 μs |  0.31 |    0.02 |      - |         - |          NA |
| FDA_ShallowChange          | 100             | 5                  | 50         |    74.19 μs |  28.196 μs |  1.546 μs |  0.15 |    0.01 | 2.8076 |   23600 B |          NA |
|                            |                 |                    |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **100**             | **20**                 | **50**         |   **653.27 μs** | **152.272 μs** |  **8.347 μs** |  **1.00** |    **0.02** |      **-** |         **-** |          **NA** |
| FDA_DeepChange             | 100             | 20                 | 50         | 1,035.64 μs |  57.529 μs |  3.153 μs |  1.59 |    0.02 | 1.9531 |   23600 B |          NA |
| AdaptiveSlop_ShallowChange | 100             | 20                 | 50         |   224.23 μs | 457.735 μs | 25.090 μs |  0.34 |    0.03 |      - |         - |          NA |
| FDA_ShallowChange          | 100             | 20                 | 50         |   251.97 μs | 275.707 μs | 15.112 μs |  0.39 |    0.02 | 2.6855 |   23600 B |          NA |
|                            |                 |                    |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **100**             | **50**                 | **50**         |   **743.56 μs** | **183.105 μs** | **10.037 μs** |  **1.00** |    **0.02** |      **-** |         **-** |          **NA** |
| FDA_DeepChange             | 100             | 50                 | 50         | 1,383.19 μs | 111.821 μs |  6.129 μs |  1.86 |    0.02 | 1.9531 |   23600 B |          NA |
| AdaptiveSlop_ShallowChange | 100             | 50                 | 50         |   395.19 μs | 783.617 μs | 42.953 μs |  0.53 |    0.05 |      - |         - |          NA |
| FDA_ShallowChange          | 100             | 50                 | 50         |   540.73 μs | 172.336 μs |  9.446 μs |  0.73 |    0.01 | 1.9531 |   23600 B |          NA |

### ValueBenchmarks

| Method             | Iterations | Mean      | Error     | StdDev   | Ratio | RatioSD | Gen0    | Allocated | Alloc Ratio |
|------------------- |----------- |----------:|----------:|---------:|------:|--------:|--------:|----------:|------------:|
| AdaptiveSlop       | 1000       |  62.25 μs |  3.924 μs | 0.215 μs |  1.00 |    0.00 |       - |         - |          NA |
| FSharpDataAdaptive | 1000       | 440.11 μs | 69.113 μs | 3.788 μs |  7.07 |    0.06 | 56.1523 |  472000 B |          NA |

### WideTreeBenchmarks

| Method             | Width | Iterations | Mean        | Error        | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------- |------ |----------- |------------:|-------------:|----------:|------:|--------:|-------:|----------:|------------:|
| **AdaptiveSlop**       | **10**    | **100**        |    **45.04 μs** |    **47.347 μs** |  **2.595 μs** |  **1.00** |    **0.07** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 10    | 100        |   148.37 μs |     8.271 μs |  0.453 μs |  3.30 |    0.16 | 5.6152 |   47200 B |          NA |
|                    |       |            |             |              |           |       |         |        |           |             |
| **AdaptiveSlop**       | **50**    | **100**        |   **331.82 μs** |    **15.481 μs** |  **0.849 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 50    | 100        |   508.30 μs |    25.145 μs |  1.378 μs |  1.53 |    0.00 | 4.8828 |   47200 B |          NA |
|                    |       |            |             |              |           |       |         |        |           |             |
| **AdaptiveSlop**       | **100**   | **100**        |   **684.82 μs** |    **21.483 μs** |  **1.178 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 100   | 100        | 1,028.99 μs |   188.324 μs | 10.323 μs |  1.50 |    0.01 | 3.9063 |   47200 B |          NA |
|                    |       |            |             |              |           |       |         |        |           |             |
| **AdaptiveSlop**       | **500**   | **100**        | **3,898.88 μs** | **1,063.281 μs** | **58.282 μs** |  **1.00** |    **0.02** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 500   | 100        | 5,721.17 μs |   506.619 μs | 27.770 μs |  1.47 |    0.02 |      - |   47200 B |          NA |


---

## 2026-08-04 — WriteGen dirty cache + Kipo physics shape

- Commit: (pending — writeGen cache + Polling removed + KipoPhysicsBenchmarks added)
- Machine: WSL2 (Ubuntu), .NET 8.0, x64 RyuJIT
- Job: ShortRun (IterationCount=3, LaunchCount=1, WarmupCount=3)

### What changed

- Scalar nodes (AdaptiveNode, MapNNode, ReduceNode) cache the dirty verdict keyed by
  the global write generation: repeated reads at the same generation are O(1) per node.
  The per-evaluation (evalId) cache was removed; the generation key subsumes it.
- Recompute keys the cache to the generation at which it started; a write from user
  code mid-compute keeps the node Dirty (fixes a latent mid-compute-mark staleness hole).
- `PollingBenchmarks` (map2 aggregation tree, 1 write + N reads) was removed: the shape
  follows FDA's synthetic stress pattern, not real usage. `DeepWideBenchmarks` stays as
  the documented unobserved-tree corner (unchanged: write+read per iteration still walks
  the subtree — the generation key does not help a fresh-generation first read).
- `KipoPhysicsBenchmarks` added: a faithful clone of Pomo.Core `Projections.fs`
  `PhysicsCache` — per frame the sim advances every entity position, the render side
  materializes (variant 1: force per frame — the current Kipo shape) or reads the graph
  directly (variant 2: derived positions/rotations nodes + transient views + the spatial
  grid rebuild — the graph-as-cache extension).

### KipoPhysicsBenchmarks (50 frames per op)

Variant 1 — force per frame (current Kipo shape):

| Method | Entities | Mean | Ratio vs FDA | Allocated | Alloc ratio |
|---|---|---|---|---|---|
| AdaptiveSlop | 250 | 2.758 ms | 1.00 | 5,501.97 KB | 1.000 |
| FSharpDataAdaptive | 250 | 9.257 ms | 3.36 | 14,115.15 KB | 2.565 |
| AdaptiveSlop | 1000 | 11.109 ms | 1.00 | 23,783.71 KB | 1.000 |
| FSharpDataAdaptive | 1000 | 41.402 ms | 3.73 | 62,581.01 KB | 2.631 |

Variant 2 — graph direct (derived nodes + transient views):

| Method | Entities | Mean | Ratio vs FDA | Allocated | Alloc ratio |
|---|---|---|---|---|---|
| AdaptiveSlop_GraphDirect | 250 | 1.066 ms | 1.00 | 22.66 KB* | 0.004 |
| FSharpDataAdaptive_GraphDirect | 250 | 80.963 ms | 76.0 | 97,674.31 KB | 17.753 |
| AdaptiveSlop_GraphDirect | 1000 | 4.016 ms | 1.00 | 22.66 KB* | 0.001 |
| FSharpDataAdaptive_GraphDirect | 1000 | 592.519 ms | 147.5 | 433,005.78 KB | 18.206 |

* The BD `Allocated` column for the graph-direct variant is a short-job artifact
(contradicts its own frame-1 cost). Direct counter
(`GC.GetAllocatedBytesForCurrentThread`) per steady-state frame: library = 24 B
(writes 0 B + derived drain constant), user-code snapshot dictionaries ≈ 62 KB at 250
entities (identical in both libraries — the graph does not allocate it).

### Notes

- The 1.22 MB/frame FDA allocation at 1000 entities (variant 1) is the burden that made
  Kipo abandon adaptive maps for positions. AdaptiveSlop cuts it to ~38% and runs the
  frame 3.7× faster; the graph-direct variant removes the library allocation entirely
  (24 B/frame) and is 147× faster than FDA's per-element adaptive blocks.
- The spatial grid stays a per-frame user-code rebuild in both variants; a
  delta-maintained grouped node is Phase 7 work.

## 2026-08-07 — 7.4/7.5 hardening (collect/bind, initial-load ordering)

- Commit: 191bdeb + uncommitted 7.5 changes (snapshot/register-between initial loads, reentrant-write test)
- Machine: WSL2 (Linux Fedora Remix), AMD Ryzen 9 6900HX 3.29GHz, 8 cores, .NET 8.0.29, x64 RyuJIT (x86-64-v3)
- Job: ShortRun (IterationCount=3, LaunchCount=1, WarmupCount=3)

### What changed

- 7.4 dynamic dependencies: CollectSetNode (ASet.collect), BindSetNode/BindMapNode (ASet.bind/AMap.bind over aval).
- Initial-load ordering fix: every node now snapshots the source view, registers its sink, then runs the user mapping (a dirty source at first read no longer double-applies its delta; a reentrant write from the mapping lands in the journal exactly once).
- F# KeyValue dictionary iteration replaced by explicit struct enumerators in hot loops (88 B/entry measured).
- Means comparison: the 2026-08-04 section (same machine shape) is the valid baseline; earlier sections ran on other machines.

### ValueBenchmarks

| Method             | Iterations | Mean      | Error     | StdDev   | Ratio | RatioSD | Gen0    | Allocated | Alloc Ratio |
|------------------- |----------- |----------:|----------:|---------:|------:|--------:|--------:|----------:|------------:|
| AdaptiveSlop       | 1000       |  49.59 μs |  3.400 μs | 0.186 μs |  1.00 |    0.00 |       - |         - |          NA |
| FSharpDataAdaptive | 1000       | 453.41 μs | 35.438 μs | 1.942 μs |  9.14 |    0.05 | 56.1523 |  472000 B |          NA |

### DeepChainBenchmarks

| Method             | Depth | Iterations | Mean         | Error      | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------- |------ |----------- |-------------:|-----------:|----------:|------:|--------:|-------:|----------:|------------:|
| **AdaptiveSlop**       | **5**     | **100**        |     **24.56 μs** |   **1.698 μs** |  **0.093 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 5     | 100        |     78.33 μs |  14.751 μs |  0.809 μs |  3.19 |    0.03 | 5.6152 |   47200 B |          NA |
|                    |       |            |              |            |           |       |         |        |           |             |
| **AdaptiveSlop**       | **10**    | **100**        |     **52.94 μs** |   **0.276 μs** |  **0.015 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 10    | 100        |    133.05 μs |  18.724 μs |  1.026 μs |  2.51 |    0.02 | 5.6152 |   47200 B |          NA |
|                    |       |            |              |            |           |       |         |        |           |             |
| **AdaptiveSlop**       | **20**    | **100**        |    **116.74 μs** |  **10.416 μs** |  **0.571 μs** |  **1.00** |    **0.01** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 20    | 100        |    267.64 μs |  55.752 μs |  3.056 μs |  2.29 |    0.02 | 5.3711 |   47200 B |          NA |
|                    |       |            |              |            |           |       |         |        |           |             |
| **AdaptiveSlop**       | **100**   | **100**        |    **723.90 μs** | **119.462 μs** |  **6.548 μs** |  **1.00** |    **0.01** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 100   | 100        |  1,237.01 μs |  95.391 μs |  5.229 μs |  1.71 |    0.01 | 3.9063 |   47200 B |          NA |
|                    |       |            |              |            |           |       |         |        |           |             |
| **AdaptiveSlop**       | **1000**  | **100**        |  **7,898.13 μs** | **429.251 μs** | **23.529 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 1000  | 100        | 12,100.83 μs | 657.173 μs | 36.022 μs |  1.53 |    0.01 |      - |   47200 B |          NA |

### Map2Benchmarks

| Method             | Iterations | Mean      | Error     | StdDev   | Ratio | RatioSD | Gen0    | Allocated | Alloc Ratio |
|------------------- |----------- |----------:|----------:|---------:|------:|--------:|--------:|----------:|------------:|
| AdaptiveSlop       | 1000       |  64.79 μs |  2.823 μs | 0.155 μs |  1.00 |    0.00 |       - |         - |          NA |
| FSharpDataAdaptive | 1000       | 449.92 μs | 63.215 μs | 3.465 μs |  6.94 |    0.05 | 62.9883 |  528000 B |          NA |

### BindBenchmarks

| Method             | Iterations | Mean      | Error     | StdDev   | Ratio | RatioSD | Gen0    | Allocated | Alloc Ratio |
|------------------- |----------- |----------:|----------:|---------:|------:|--------:|--------:|----------:|------------:|
| AdaptiveSlop       | 1000       |  76.26 μs |  26.64 μs | 1.460 μs |  1.00 |    0.02 |       - |         - |          NA |
| FSharpDataAdaptive | 1000       | 608.53 μs | 172.09 μs | 9.433 μs |  7.98 |    0.17 | 69.3359 |  584000 B |          NA |

### TransactionBenchmarks

| Method                     | ValueCount | Iterations | Mean       | Error     | StdDev   | Ratio | RatioSD | Gen0    | Allocated | Alloc Ratio |
|--------------------------- |----------- |----------- |-----------:|----------:|---------:|------:|--------:|--------:|----------:|------------:|
| AdaptiveSlop_Batched       | 10         | 500        |   369.4 μs |  76.09 μs |  4.17 μs |  1.00 |    0.01 |  1.4648 |  15.63 KB |        1.00 |
| FSharpDataAdaptive_Batched | 10         | 500        | 1,515.3 μs | 391.78 μs | 21.47 μs |  4.10 |    0.06 | 87.8906 | 726.56 KB |       46.50 |

### SetBenchmarks

| Method             | Iterations | Mean      | Error      | StdDev   | Ratio | RatioSD | Gen0     | Allocated | Alloc Ratio |
|------------------- |----------- |----------:|-----------:|---------:|------:|--------:|---------:|----------:|------------:|
| AdaptiveSlop       | 1000       |  42.48 μs |   1.558 μs | 0.085 μs |  1.00 |    0.00 |        - |         - |          NA |
| FSharpDataAdaptive | 1000       | 385.20 μs | 103.398 μs | 5.668 μs |  9.07 |    0.12 | 105.9570 |  888000 B |          NA |

### SetTransformBenchmarks

| Method             | Iterations | Mean       | Error     | StdDev   | Ratio | RatioSD | Gen0     | Allocated | Alloc Ratio |
|------------------- |----------- |-----------:|----------:|---------:|------:|--------:|---------:|----------:|------------:|
| AdaptiveSlop       | 500        |   118.0 μs |  25.85 μs |  1.42 μs |  1.00 |    0.01 |        - |         - |          NA |
| FSharpDataAdaptive | 500        | 1,484.9 μs | 237.94 μs | 13.04 μs | 12.59 |    0.16 | 185.5469 | 1561456 B |          NA |

### MapBenchmarks

| Method             | Iterations | Mean      | Error     | StdDev   | Ratio | RatioSD | Gen0     | Allocated | Alloc Ratio |
|------------------- |----------- |----------:|----------:|---------:|------:|--------:|---------:|----------:|------------:|
| AdaptiveSlop       | 1000       |  45.08 μs |  2.981 μs | 0.163 μs |  1.00 |    0.00 |        - |         - |          NA |
| FSharpDataAdaptive | 1000       | 354.00 μs | 38.976 μs | 2.136 μs |  7.85 |    0.05 | 104.9805 |  880000 B |          NA |

### MapTransformBenchmarks

| Method             | Iterations | Mean       | Error     | StdDev  | Ratio | RatioSD | Gen0     | Allocated | Alloc Ratio |
|------------------- |----------- |-----------:|----------:|--------:|------:|--------:|---------:|----------:|------------:|
| AdaptiveSlop       | 500        |   123.7 μs |  33.61 μs | 1.84 μs |  1.00 |    0.02 |        - |         - |          NA |
| FSharpDataAdaptive | 500        | 1,421.0 μs | 124.41 μs | 6.82 μs | 11.49 |    0.15 | 210.9375 | 1769408 B |          NA |

### LargeCollectionBenchmarks

| Method             | InitialSize | Iterations | Mean      | Error      | StdDev    | Ratio | RatioSD | Gen0    | Gen1   | Allocated | Alloc Ratio |
|------------------- |------------ |----------- |----------:|-----------:|----------:|------:|--------:|--------:|-------:|----------:|------------:|
| AdaptiveSlop       | 10000       | 200        |  2.646 μs |  0.2014 μs | 0.0110 μs |  1.00 |    0.01 |       - |      - |         - |          NA |
| FSharpDataAdaptive | 10000       | 200        | 92.147 μs | 55.9439 μs | 3.0665 μs | 34.82 |    1.01 | 28.0762 | 1.7090 |  235456 B |          NA |

### ReadHeavyBenchmarks

| Method             | WriteCount | ReadsPerWrite | Mean      | Error     | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------- |----------- |-------------- |----------:|----------:|---------:|------:|--------:|-------:|----------:|------------:|
| AdaptiveSlop       | 100        | 50            |  50.34 μs |  1.081 μs | 0.059 μs |  1.00 |    0.00 |      - |         - |          NA |
| FSharpDataAdaptive | 100        | 50            | 127.16 μs | 67.097 μs | 3.678 μs |  2.53 |    0.06 | 5.6152 |   47200 B |          NA |

### DiamondGraphBenchmarks

| Method             | Iterations | Mean     | Error     | StdDev  | Ratio | RatioSD | Gen0    | Allocated | Alloc Ratio |
|------------------- |----------- |---------:|----------:|--------:|------:|--------:|--------:|----------:|------------:|
| AdaptiveSlop       | 1000       | 147.4 μs |   2.30 μs | 0.13 μs |  1.00 |    0.00 |       - |         - |          NA |
| FSharpDataAdaptive | 1000       | 657.4 μs | 143.23 μs | 7.85 μs |  4.46 |    0.05 | 73.2422 |  616000 B |          NA |

### WideTreeBenchmarks

| Method             | Width | Iterations | Mean        | Error      | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------- |------ |----------- |------------:|-----------:|----------:|------:|--------:|-------:|----------:|------------:|
| **AdaptiveSlop**       | **10**    | **100**        |    **32.65 μs** |  **21.191 μs** |  **1.162 μs** |  **1.00** |    **0.04** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 10    | 100        |    97.41 μs |  15.575 μs |  0.854 μs |  2.99 |    0.09 | 5.6152 |   47200 B |          NA |
|                    |       |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop**       | **50**    | **100**        |   **229.88 μs** |   **6.477 μs** |  **0.355 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 50    | 100        |   386.42 μs |  75.159 μs |  4.120 μs |  1.68 |    0.02 | 5.3711 |   47200 B |          NA |
|                    |       |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop**       | **100**   | **100**        |   **488.78 μs** |   **3.817 μs** |  **0.209 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 100   | 100        |   746.44 μs | 133.724 μs |  7.330 μs |  1.53 |    0.01 | 4.8828 |   47200 B |          NA |
|                    |       |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop**       | **500**   | **100**        | **2,667.45 μs** |  **52.386 μs** |  **2.871 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 500   | 100        | 3,669.49 μs | 801.220 μs | 43.918 μs |  1.38 |    0.01 | 3.9063 |   47200 B |          NA |

### OptimizedWideTreeBenchmarks

| Method                 | Width | Iterations | Mean        | Error        | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------------- |------ |----------- |------------:|-------------:|----------:|------:|--------:|-------:|----------:|------------:|
| **AdaptiveSlop_Map2Chain** | **10**    | **100**        |    **35.53 μs** |     **3.264 μs** |  **0.179 μs** |  **1.00** |    **0.01** |      **-** |         **-** |          **NA** |
| AdaptiveSlop_Reduce    | 10    | 100        |    10.37 μs |     0.491 μs |  0.027 μs |  0.29 |    0.00 |      - |         - |          NA |
| FSharpDataAdaptive     | 10    | 100        |    94.94 μs |    10.710 μs |  0.587 μs |  2.67 |    0.02 | 5.6152 |   47200 B |          NA |
|                        |       |            |             |              |           |       |         |        |           |             |
| **AdaptiveSlop_Map2Chain** | **50**    | **100**        |   **236.95 μs** |    **10.358 μs** |  **0.568 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| AdaptiveSlop_Reduce    | 50    | 100        |    43.93 μs |     7.031 μs |  0.385 μs |  0.19 |    0.00 |      - |         - |          NA |
| FSharpDataAdaptive     | 50    | 100        |   394.20 μs |    84.288 μs |  4.620 μs |  1.66 |    0.02 | 5.3711 |   47200 B |          NA |
|                        |       |            |             |              |           |       |         |        |           |             |
| **AdaptiveSlop_Map2Chain** | **100**   | **100**        |   **474.24 μs** |    **49.237 μs** |  **2.699 μs** |  **1.00** |    **0.01** |      **-** |         **-** |          **NA** |
| AdaptiveSlop_Reduce    | 100   | 100        |    84.68 μs |     8.475 μs |  0.465 μs |  0.18 |    0.00 |      - |         - |          NA |
| FSharpDataAdaptive     | 100   | 100        |   763.61 μs |   249.936 μs | 13.700 μs |  1.61 |    0.03 | 4.8828 |   47200 B |          NA |
|                        |       |            |             |              |           |       |         |        |           |             |
| **AdaptiveSlop_Map2Chain** | **500**   | **100**        | **2,674.28 μs** |   **187.082 μs** | **10.255 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| AdaptiveSlop_Reduce    | 500   | 100        |   421.23 μs |    14.639 μs |  0.802 μs |  0.16 |    0.00 |      - |         - |          NA |
| FSharpDataAdaptive     | 500   | 100        | 3,934.42 μs | 1,480.487 μs | 81.151 μs |  1.47 |    0.03 |      - |   47200 B |          NA |

### DeepWideBenchmarks

| Method             | Depth | BranchingFactor | Iterations | Mean         | Error         | StdDev       | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------- |------ |---------------- |----------- |-------------:|--------------:|-------------:|------:|--------:|-------:|----------:|------------:|
| **AdaptiveSlop**       | **3**     | **2**               | **50**         |     **11.78 μs** |      **4.072 μs** |     **0.223 μs** |  **1.00** |    **0.02** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 3     | 2               | 50         |     35.58 μs |     25.415 μs |     1.393 μs |  3.02 |    0.11 | 2.8076 |   23600 B |          NA |
|                    |       |                 |            |              |               |              |       |         |        |           |             |
| **AdaptiveSlop**       | **3**     | **3**               | **50**         |     **32.36 μs** |     **14.026 μs** |     **0.769 μs** |  **1.00** |    **0.03** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 3     | 3               | 50         |     56.43 μs |     10.092 μs |     0.553 μs |  1.74 |    0.04 | 2.8076 |   23600 B |          NA |
|                    |       |                 |            |              |               |              |       |         |        |           |             |
| **AdaptiveSlop**       | **3**     | **4**               | **50**         |     **55.82 μs** |      **7.164 μs** |     **0.393 μs** |  **1.00** |    **0.01** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 3     | 4               | 50         |     71.37 μs |     13.907 μs |     0.762 μs |  1.28 |    0.01 | 2.8076 |   23600 B |          NA |
|                    |       |                 |            |              |               |              |       |         |        |           |             |
| **AdaptiveSlop**       | **5**     | **2**               | **50**         |     **27.24 μs** |      **3.841 μs** |     **0.211 μs** |  **1.00** |    **0.01** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 5     | 2               | 50         |     48.61 μs |      8.511 μs |     0.466 μs |  1.78 |    0.02 | 2.8076 |   23600 B |          NA |
|                    |       |                 |            |              |               |              |       |         |        |           |             |
| **AdaptiveSlop**       | **5**     | **3**               | **50**         |    **162.18 μs** |      **3.635 μs** |     **0.199 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 5     | 3               | 50         |     90.76 μs |     16.791 μs |     0.920 μs |  0.56 |    0.00 | 2.8076 |   23600 B |          NA |
|                    |       |                 |            |              |               |              |       |         |        |           |             |
| **AdaptiveSlop**       | **5**     | **4**               | **50**         |    **982.09 μs** |     **34.246 μs** |     **1.877 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 5     | 4               | 50         |    116.24 μs |      8.059 μs |     0.442 μs |  0.12 |    0.00 | 2.8076 |   23600 B |          NA |
|                    |       |                 |            |              |               |              |       |         |        |           |             |
| **AdaptiveSlop**       | **7**     | **2**               | **50**         |     **85.18 μs** |     **14.842 μs** |     **0.814 μs** |  **1.00** |    **0.01** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 7     | 2               | 50         |     62.43 μs |      6.936 μs |     0.380 μs |  0.73 |    0.01 | 2.8076 |   23600 B |          NA |
|                    |       |                 |            |              |               |              |       |         |        |           |             |
| **AdaptiveSlop**       | **7**     | **3**               | **50**         |  **2,028.85 μs** |    **116.987 μs** |     **6.412 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 7     | 3               | 50         |    120.28 μs |     24.418 μs |     1.338 μs |  0.06 |    0.00 | 2.8076 |   23600 B |          NA |
|                    |       |                 |            |              |               |              |       |         |        |           |             |
| **AdaptiveSlop**       | **7**     | **4**               | **50**         | **19,066.16 μs** | **22,235.971 μs** | **1,218.829 μs** | **1.003** |    **0.08** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 7     | 4               | 50         |    165.69 μs |     51.470 μs |     2.821 μs | 0.009 |    0.00 | 2.6855 |   23600 B |          NA |

### KipoPhysicsBenchmarks

| Method                         | EntityCount | Iterations | Mean         | Error         | StdDev       | Ratio | RatioSD | Gen0       | Gen1       | Gen2       | Allocated    | Alloc Ratio |
|------------------------------- |------------ |----------- |-------------:|--------------:|-------------:|------:|--------:|-----------:|-----------:|-----------:|-------------:|------------:|
| **AdaptiveSlop**                   | **250**         | **50**         |   **2,561.7 μs** |     **137.66 μs** |      **7.55 μs** |  **1.00** |    **0.00** |   **671.8750** |   **128.9063** |          **-** |   **5501.97 KB** |       **1.000** |
| FSharpDataAdaptive             | 250         | 50         |   8,248.0 μs |   1,215.68 μs |     66.64 μs |  3.22 |    0.02 |  1718.7500 |   250.0000 |          - |  14115.15 KB |       2.565 |
| AdaptiveSlop_GraphDirect       | 250         | 50         |     917.2 μs |      14.70 μs |      0.81 μs |  0.36 |    0.00 |     1.9531 |          - |          - |     21.48 KB |       0.004 |
| FSharpDataAdaptive_GraphDirect | 250         | 50         |  82,494.0 μs | 433,487.26 μs | 23,760.90 μs | 32.20 |    8.03 | 12000.0000 |  9600.0000 |  7600.0000 |  97674.31 KB |      17.753 |
|                                |             |            |              |               |              |       |         |            |            |            |              |             |
| **AdaptiveSlop**                   | **1000**        | **50**         |  **10,683.4 μs** |     **281.44 μs** |     **15.43 μs** |  **1.00** |    **0.00** |  **2906.2500** |   **937.5000** |          **-** |  **23783.71 KB** |       **1.000** |
| FSharpDataAdaptive             | 1000        | 50         |  41,602.9 μs |  11,021.34 μs |    604.12 μs |  3.89 |    0.05 |  7615.3846 |  2230.7692 |          - |  62581.01 KB |       2.631 |
| AdaptiveSlop_GraphDirect       | 1000        | 50         |   3,643.6 μs |     468.99 μs |     25.71 μs |  0.34 |    0.00 |          - |          - |          - |     21.48 KB |       0.001 |
| FSharpDataAdaptive_GraphDirect | 1000        | 50         | 568,077.1 μs | 214,499.38 μs | 11,757.43 μs | 53.17 |    0.96 | 51000.0000 | 47000.0000 | 33000.0000 | 433005.69 KB |      18.206 |

### UnbalancedTreeBenchmarks

| Method                     | DeepBranchDepth | ShallowBranchCount | Iterations | Mean        | Error      | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|--------------------------- |---------------- |------------------- |----------- |------------:|-----------:|----------:|------:|--------:|-------:|----------:|------------:|
| **AdaptiveSlop_DeepChange**    | **10**              | **5**                  | **50**         |    **51.13 μs** |  **22.804 μs** |  **1.250 μs** |  **1.00** |    **0.03** |      **-** |         **-** |          **NA** |
| FDA_DeepChange             | 10              | 5                  | 50         |   117.97 μs |  14.881 μs |  0.816 μs |  2.31 |    0.05 | 2.8076 |   23600 B |          NA |
| AdaptiveSlop_ShallowChange | 10              | 5                  | 50         |    24.57 μs |   7.054 μs |  0.387 μs |  0.48 |    0.01 |      - |         - |          NA |
| FDA_ShallowChange          | 10              | 5                  | 50         |    53.92 μs |   9.026 μs |  0.495 μs |  1.06 |    0.02 | 2.8076 |   23600 B |          NA |
|                            |                 |                    |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **10**              | **20**                 | **50**         |   **118.15 μs** |  **19.253 μs** |  **1.055 μs** |  **1.00** |    **0.01** |      **-** |         **-** |          **NA** |
| FDA_DeepChange             | 10              | 20                 | 50         |   235.60 μs |  18.007 μs |  0.987 μs |  1.99 |    0.02 | 2.6855 |   23600 B |          NA |
| AdaptiveSlop_ShallowChange | 10              | 20                 | 50         |    84.87 μs |   7.893 μs |  0.433 μs |  0.72 |    0.01 |      - |         - |          NA |
| FDA_ShallowChange          | 10              | 20                 | 50         |   170.94 μs |   8.457 μs |  0.464 μs |  1.45 |    0.01 | 2.6855 |   23600 B |          NA |
|                            |                 |                    |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **10**              | **50**                 | **50**         |   **257.27 μs** |  **10.779 μs** |  **0.591 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| FDA_DeepChange             | 10              | 50                 | 50         |   487.16 μs | 100.252 μs |  5.495 μs |  1.89 |    0.02 | 2.4414 |   23600 B |          NA |
| AdaptiveSlop_ShallowChange | 10              | 50                 | 50         |   222.49 μs |  42.344 μs |  2.321 μs |  0.86 |    0.01 |      - |         - |          NA |
| FDA_ShallowChange          | 10              | 50                 | 50         |   415.22 μs |  51.272 μs |  2.810 μs |  1.61 |    0.01 | 2.4414 |   23600 B |          NA |
|                            |                 |                    |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **50**              | **5**                  | **50**         |   **196.05 μs** |  **56.765 μs** |  **3.111 μs** |  **1.00** |    **0.02** |      **-** |         **-** |          **NA** |
| FDA_DeepChange             | 50              | 5                  | 50         |   336.94 μs |  53.731 μs |  2.945 μs |  1.72 |    0.03 | 2.4414 |   23600 B |          NA |
| AdaptiveSlop_ShallowChange | 50              | 5                  | 50         |    51.70 μs |   0.679 μs |  0.037 μs |  0.26 |    0.00 |      - |         - |          NA |
| FDA_ShallowChange          | 50              | 5                  | 50         |    57.54 μs |   4.260 μs |  0.233 μs |  0.29 |    0.00 | 2.8076 |   23600 B |          NA |
|                            |                 |                    |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **50**              | **20**                 | **50**         |   **283.93 μs** | **147.356 μs** |  **8.077 μs** |  **1.00** |    **0.03** |      **-** |         **-** |          **NA** |
| FDA_DeepChange             | 50              | 20                 | 50         |   500.19 μs | 150.602 μs |  8.255 μs |  1.76 |    0.05 | 1.9531 |   23600 B |          NA |
| AdaptiveSlop_ShallowChange | 50              | 20                 | 50         |   133.95 μs |   5.595 μs |  0.307 μs |  0.47 |    0.01 |      - |         - |          NA |
| FDA_ShallowChange          | 50              | 20                 | 50         |   171.72 μs |  10.075 μs |  0.552 μs |  0.61 |    0.01 | 2.6855 |   23600 B |          NA |
|                            |                 |                    |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **50**              | **50**                 | **50**         |   **422.45 μs** |  **58.950 μs** |  **3.231 μs** |  **1.00** |    **0.01** |      **-** |         **-** |          **NA** |
| FDA_DeepChange             | 50              | 50                 | 50         |   750.26 μs | 210.597 μs | 11.544 μs |  1.78 |    0.03 | 1.9531 |   23600 B |          NA |
| AdaptiveSlop_ShallowChange | 50              | 50                 | 50         |   271.87 μs |  11.802 μs |  0.647 μs |  0.64 |    0.00 |      - |         - |          NA |
| FDA_ShallowChange          | 50              | 50                 | 50         |   408.78 μs |  11.853 μs |  0.650 μs |  0.97 |    0.01 | 2.4414 |   23600 B |          NA |
|                            |                 |                    |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **100**             | **5**                  | **50**         |   **373.86 μs** |  **53.907 μs** |  **2.955 μs** |  **1.00** |    **0.01** |      **-** |         **-** |          **NA** |
| FDA_DeepChange             | 100             | 5                  | 50         |   622.09 μs | 132.899 μs |  7.285 μs |  1.66 |    0.02 | 1.9531 |   23600 B |          NA |
| AdaptiveSlop_ShallowChange | 100             | 5                  | 50         |   113.33 μs |  15.983 μs |  0.876 μs |  0.30 |    0.00 |      - |         - |          NA |
| FDA_ShallowChange          | 100             | 5                  | 50         |    54.43 μs |   4.328 μs |  0.237 μs |  0.15 |    0.00 | 2.8076 |   23600 B |          NA |
|                            |                 |                    |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **100**             | **20**                 | **50**         |   **469.67 μs** |  **57.935 μs** |  **3.176 μs** |  **1.00** |    **0.01** |      **-** |         **-** |          **NA** |
| FDA_DeepChange             | 100             | 20                 | 50         |   766.82 μs |  24.881 μs |  1.364 μs |  1.63 |    0.01 | 1.9531 |   23600 B |          NA |
| AdaptiveSlop_ShallowChange | 100             | 20                 | 50         |   181.52 μs |   7.279 μs |  0.399 μs |  0.39 |    0.00 |      - |         - |          NA |
| FDA_ShallowChange          | 100             | 20                 | 50         |   169.66 μs |  11.516 μs |  0.631 μs |  0.36 |    0.00 | 2.6855 |   23600 B |          NA |
|                            |                 |                    |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **100**             | **50**                 | **50**         |   **590.81 μs** |   **9.318 μs** |  **0.511 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| FDA_DeepChange             | 100             | 50                 | 50         | 1,038.37 μs |  58.052 μs |  3.182 μs |  1.76 |    0.00 | 1.9531 |   23600 B |          NA |
| AdaptiveSlop_ShallowChange | 100             | 50                 | 50         |   326.31 μs |  26.509 μs |  1.453 μs |  0.55 |    0.00 |      - |         - |          NA |
| FDA_ShallowChange          | 100             | 50                 | 50         |   410.85 μs |  62.947 μs |  3.450 μs |  0.70 |    0.01 | 2.4414 |   23600 B |          NA |

### IncrementalChainBenchmarks

| Method             | InitialSize | Mutations | Mean      | Error      | StdDev   | Ratio | RatioSD | Gen0     | Allocated | Alloc Ratio |
|------------------- |------------ |---------- |----------:|-----------:|---------:|------:|--------:|---------:|----------:|------------:|
| **AdaptiveSlop**       | **100**         | **200**       |  **49.95 μs** |   **7.847 μs** | **0.430 μs** |  **1.00** |    **0.01** |        **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 100         | 200       | 596.57 μs | 106.191 μs | 5.821 μs | 11.94 |    0.13 |  76.1719 |  638704 B |          NA |
|                    |             |           |           |            |          |       |         |          |           |             |
| **AdaptiveSlop**       | **1000**        | **200**       |  **49.77 μs** |   **2.525 μs** | **0.138 μs** |  **1.00** |    **0.00** |        **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 1000        | 200       | 617.11 μs |  22.354 μs | 1.225 μs | 12.40 |    0.04 |  78.1250 |  656512 B |          NA |
|                    |             |           |           |            |          |       |         |          |           |             |
| **AdaptiveSlop**       | **10000**       | **200**       |  **49.29 μs** |   **2.735 μs** | **0.150 μs** |  **1.00** |    **0.00** |        **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 10000       | 200       | 661.95 μs |  68.922 μs | 3.778 μs | 13.43 |    0.08 | 101.5625 |  852736 B |          NA |

### ConcurrentBenchmarks

| Method             | ThreadCount | IterationsPerThread | Mean      | Error      | StdDev   | Ratio | RatioSD | Gen0     | Gen1   | Allocated | Alloc Ratio |
|------------------- |------------ |-------------------- |----------:|-----------:|---------:|------:|--------:|---------:|-------:|----------:|------------:|
| AdaptiveSlop       | 4           | 500                 |  53.82 μs |   6.926 μs | 0.380 μs |  1.00 |    0.01 |   0.0610 |      - |     849 B |        1.00 |
| FSharpDataAdaptive | 4           | 500                 | 593.65 μs | 110.121 μs | 6.036 μs | 11.03 |    0.12 | 112.3047 | 0.9766 |  944848 B |    1,112.90 |

---

## 2026-08-05 — Hostile-review fixes + weak-reference sinks

- Commit: c992e76 (weak-reference sinks) on top of fee8035 (hostile-review fixes); docs/2026-08-05-DESIGN-WEAK-SINK-REFERENCES.md
- Machine: WSL2 (Linux Fedora Remix), AMD Ryzen 9 6900HX 3.29GHz, 8 cores, .NET 8.0.29, x64 RyuJIT (x86-64-v3)
- Job: ShortRun (IterationCount=3, LaunchCount=1, WarmupCount=3)

### What changed

- Correctness fixes from the hostile reviews (docs/2026-08-05-GLM_REVIEW_FINDINGS.md,
  docs/2026-08-05-KIMI_REVIEW_FINDINGS.md): ofAVal poll-on-version, net deltas at all
  producers, list replay validation, ReduceNode write-generation guard, exception-safe
  drain compaction, notification isolation, Set-supersedes-batch, real changeable
  Dispose, plus allocation fixes (de-boxed enumerators, pre-sized scratch sets).
- Weak-reference sinks (GLM 10): SinkList entries are now WeakReference; a derived
  collection node that was read and then dropped is collected (FDA precedent:
  WeakOutputSet). Delivery compacts dead entries at batch start; reentrant
  registrations during delivery are not delivered (bound captured).

### Regressions vs the 2026-08-07 section (same machine, doc rule: AdaptiveSlop mean beyond the baseline error margin)

- Map 45.08 → 59.90 (+33%), Set 42.48 → 58.10 (+37%), Map2 64.79 → 76.87 (+19%),
  Bind 76.26 → 86.84 (+14%), IncrementalChain ~50 → ~59 (+17-20%),
  Transaction_Batched 369.4 → 453.5 (+23%), SetTransform 118.0 → 140.3 (+19%),
  MapTransform 123.7 → 140.8 (+14%), KipoPhysics +12-14%, Concurrent 53.82 → 58.62 (+9%).
- Allocated stays zero on every steady-state path (Gen0 "-" everywhere except the
  documented ListWriteRead/Kipo/Concurrent/Transaction non-steady cases).
- Suspected causes (cheap fixes identified, not yet applied): per-evaluation
  DependencyCollector.Clear (KIMI 13), compactDeadSinks pass at delivery start
  (weak sinks), per-item try/with in TransactionBuffer.Commit (GLM 5).
- FDA-relative: still 3-38x faster everywhere except the pre-existing losses
  (Polling ~0.5, DeepWide wide/deep 0.01-0.73, Unbalanced ShallowChange deep 0.14-0.68,
  ListWriteRead 100k 0.88). The FDA ratios that moved are all within ±0.3 points of the
  08-07 section except Value 9.14 → 6.91 (FDA absolute improved 453 → 350 μs; our mean
  flat at ~50 μs) and Map/Set/Map2/Bind (our slowdown above, FDA flat).

### ValueBenchmarks


### ValueBenchmarks

| Method             | Iterations | Mean      | Error     | StdDev   | Ratio | RatioSD | Gen0    | Allocated | Alloc Ratio |
|------------------- |----------- |----------:|----------:|---------:|------:|--------:|--------:|----------:|------------:|
| AdaptiveSlop       | 1000       |  50.65 μs |  3.433 μs | 0.188 μs |  1.00 |    0.00 |       - |         - |          NA |
| FSharpDataAdaptive | 1000       | 349.74 μs | 54.841 μs | 3.006 μs |  6.91 |    0.06 | 56.1523 |  472000 B |          NA |

### DeepChainBenchmarks

| Method             | Depth | Iterations | Mean         | Error        | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------- |------ |----------- |-------------:|-------------:|----------:|------:|--------:|-------:|----------:|------------:|
| **AdaptiveSlop**       | **5**     | **100**        |     **24.71 μs** |     **0.900 μs** |  **0.049 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 5     | 100        |     80.46 μs |     8.318 μs |  0.456 μs |  3.26 |    0.02 | 5.6152 |   47200 B |          NA |
|                    |       |            |              |              |           |       |         |        |           |             |
| **AdaptiveSlop**       | **10**    | **100**        |     **53.74 μs** |     **7.865 μs** |  **0.431 μs** |  **1.00** |    **0.01** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 10    | 100        |    133.81 μs |    19.814 μs |  1.086 μs |  2.49 |    0.02 | 5.6152 |   47200 B |          NA |
|                    |       |            |              |              |           |       |         |        |           |             |
| **AdaptiveSlop**       | **20**    | **100**        |    **121.16 μs** |    **25.031 μs** |  **1.372 μs** |  **1.00** |    **0.01** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 20    | 100        |    245.19 μs |    38.307 μs |  2.100 μs |  2.02 |    0.02 | 5.3711 |   47200 B |          NA |
|                    |       |            |              |              |           |       |         |        |           |             |
| **AdaptiveSlop**       | **100**   | **100**        |    **712.92 μs** |   **196.601 μs** | **10.776 μs** |  **1.00** |    **0.02** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 100   | 100        |  1,172.98 μs |   125.633 μs |  6.886 μs |  1.65 |    0.02 | 3.9063 |   47200 B |          NA |
|                    |       |            |              |              |           |       |         |        |           |             |
| **AdaptiveSlop**       | **1000**  | **100**        |  **7,506.88 μs** |   **454.983 μs** | **24.939 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 1000  | 100        | 12,484.61 μs | 1,535.741 μs | 84.179 μs |  1.66 |    0.01 |      - |   47200 B |          NA |

### Map2Benchmarks

| Method             | Iterations | Mean      | Error    | StdDev   | Ratio | RatioSD | Gen0    | Allocated | Alloc Ratio |
|------------------- |----------- |----------:|---------:|---------:|------:|--------:|--------:|----------:|------------:|
| AdaptiveSlop       | 1000       |  76.87 μs | 67.72 μs | 3.712 μs |  1.00 |    0.06 |       - |         - |          NA |
| FSharpDataAdaptive | 1000       | 466.65 μs | 41.70 μs | 2.286 μs |  6.08 |    0.25 | 62.9883 |  528000 B |          NA |

### BindBenchmarks

| Method             | Iterations | Mean      | Error    | StdDev   | Ratio | RatioSD | Gen0    | Allocated | Alloc Ratio |
|------------------- |----------- |----------:|---------:|---------:|------:|--------:|--------:|----------:|------------:|
| AdaptiveSlop       | 1000       |  86.84 μs | 27.84 μs | 1.526 μs |  1.00 |    0.02 |       - |         - |          NA |
| FSharpDataAdaptive | 1000       | 641.76 μs | 90.88 μs | 4.981 μs |  7.39 |    0.12 | 69.3359 |  584000 B |          NA |

### TransactionBenchmarks

| Method                     | ValueCount | Iterations | Mean       | Error    | StdDev  | Ratio | Gen0    | Allocated | Alloc Ratio |
|--------------------------- |----------- |----------- |-----------:|---------:|--------:|------:|--------:|----------:|------------:|
| AdaptiveSlop_Batched       | 10         | 500        |   453.5 μs | 20.42 μs | 1.12 μs |  1.00 |  1.4648 |  15.63 KB |        1.00 |
| FSharpDataAdaptive_Batched | 10         | 500        | 1,624.5 μs | 69.51 μs | 3.81 μs |  3.58 | 87.8906 | 726.56 KB |       46.50 |

### SetBenchmarks

| Method             | Iterations | Mean      | Error      | StdDev   | Ratio | RatioSD | Gen0     | Allocated | Alloc Ratio |
|------------------- |----------- |----------:|-----------:|---------:|------:|--------:|---------:|----------:|------------:|
| AdaptiveSlop       | 1000       |  58.10 μs |   4.824 μs | 0.264 μs |  1.00 |    0.01 |        - |         - |          NA |
| FSharpDataAdaptive | 1000       | 399.69 μs | 115.560 μs | 6.334 μs |  6.88 |    0.10 | 105.9570 |  888000 B |          NA |

### SetTransformBenchmarks

| Method             | Iterations | Mean       | Error     | StdDev   | Ratio | RatioSD | Gen0     | Allocated | Alloc Ratio |
|------------------- |----------- |-----------:|----------:|---------:|------:|--------:|---------:|----------:|------------:|
| AdaptiveSlop       | 500        |   140.3 μs |   2.61 μs |  0.14 μs |  1.00 |    0.00 |        - |         - |          NA |
| FSharpDataAdaptive | 500        | 1,596.0 μs | 372.38 μs | 20.41 μs | 11.37 |    0.13 | 185.5469 | 1561456 B |          NA |

### MapBenchmarks

| Method             | Iterations | Mean      | Error     | StdDev   | Ratio | RatioSD | Gen0     | Allocated | Alloc Ratio |
|------------------- |----------- |----------:|----------:|---------:|------:|--------:|---------:|----------:|------------:|
| AdaptiveSlop       | 1000       |  59.90 μs |  0.986 μs | 0.054 μs |  1.00 |    0.00 |        - |         - |          NA |
| FSharpDataAdaptive | 1000       | 362.90 μs | 29.484 μs | 1.616 μs |  6.06 |    0.02 | 104.9805 |  880000 B |          NA |

### MapTransformBenchmarks

| Method             | Iterations | Mean       | Error       | StdDev   | Ratio | RatioSD | Gen0     | Allocated | Alloc Ratio |
|------------------- |----------- |-----------:|------------:|---------:|------:|--------:|---------:|----------:|------------:|
| AdaptiveSlop       | 500        |   140.8 μs |    13.36 μs |  0.73 μs |  1.00 |    0.01 |        - |         - |          NA |
| FSharpDataAdaptive | 500        | 1,419.8 μs | 1,694.10 μs | 92.86 μs | 10.08 |    0.57 | 210.9375 | 1769408 B |          NA |

### LargeCollectionBenchmarks

| Method             | InitialSize | Iterations | Mean      | Error     | StdDev    | Ratio | RatioSD | Gen0    | Gen1   | Allocated | Alloc Ratio |
|------------------- |------------ |----------- |----------:|----------:|----------:|------:|--------:|--------:|-------:|----------:|------------:|
| AdaptiveSlop       | 10000       | 200        |  2.656 μs | 0.3015 μs | 0.0165 μs |  1.00 |    0.01 |       - |      - |         - |          NA |
| FSharpDataAdaptive | 10000       | 200        | 94.839 μs | 9.7671 μs | 0.5354 μs | 35.71 |    0.26 | 28.0762 | 1.7090 |  235456 B |          NA |

### ReadHeavyBenchmarks

| Method             | WriteCount | ReadsPerWrite | Mean      | Error    | StdDev   | Ratio | Gen0   | Allocated | Alloc Ratio |
|------------------- |----------- |-------------- |----------:|---------:|---------:|------:|-------:|----------:|------------:|
| AdaptiveSlop       | 100        | 50            |  52.06 μs | 1.894 μs | 0.104 μs |  1.00 |      - |         - |          NA |
| FSharpDataAdaptive | 100        | 50            | 132.67 μs | 3.099 μs | 0.170 μs |  2.55 | 5.6152 |   47200 B |          NA |

### DiamondGraphBenchmarks

| Method             | Iterations | Mean     | Error    | StdDev   | Ratio | RatioSD | Gen0    | Allocated | Alloc Ratio |
|------------------- |----------- |---------:|---------:|---------:|------:|--------:|--------:|----------:|------------:|
| AdaptiveSlop       | 1000       | 160.1 μs | 137.1 μs |  7.52 μs |  1.00 |    0.06 |       - |         - |          NA |
| FSharpDataAdaptive | 1000       | 751.7 μs | 561.4 μs | 30.77 μs |  4.70 |    0.25 | 73.2422 |  616000 B |          NA |

### WideTreeBenchmarks

| Method             | Width | Iterations | Mean        | Error        | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------- |------ |----------- |------------:|-------------:|----------:|------:|--------:|-------:|----------:|------------:|
| **AdaptiveSlop**       | **10**    | **100**        |    **34.42 μs** |     **7.446 μs** |  **0.408 μs** |  **1.00** |    **0.01** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 10    | 100        |    96.65 μs |    23.455 μs |  1.286 μs |  2.81 |    0.04 | 5.6152 |   47200 B |          NA |
|                    |       |            |             |              |           |       |         |        |           |             |
| **AdaptiveSlop**       | **50**    | **100**        |   **226.06 μs** |    **36.661 μs** |  **2.010 μs** |  **1.00** |    **0.01** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 50    | 100        |   389.74 μs |   229.360 μs | 12.572 μs |  1.72 |    0.05 | 5.3711 |   47200 B |          NA |
|                    |       |            |             |              |           |       |         |        |           |             |
| **AdaptiveSlop**       | **100**   | **100**        |   **496.57 μs** |    **19.887 μs** |  **1.090 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 100   | 100        |   732.22 μs |    81.078 μs |  4.444 μs |  1.47 |    0.01 | 4.8828 |   47200 B |          NA |
|                    |       |            |             |              |           |       |         |        |           |             |
| **AdaptiveSlop**       | **500**   | **100**        | **2,367.44 μs** |   **161.674 μs** |  **8.862 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 500   | 100        | 3,668.36 μs | 1,269.609 μs | 69.592 μs |  1.55 |    0.03 | 3.9063 |   47200 B |          NA |

### OptimizedWideTreeBenchmarks

| Method                 | Width | Iterations | Mean        | Error      | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------------- |------ |----------- |------------:|-----------:|----------:|------:|--------:|-------:|----------:|------------:|
| **AdaptiveSlop_Map2Chain** | **10**    | **100**        |    **37.28 μs** |   **0.731 μs** |  **0.040 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| AdaptiveSlop_Reduce    | 10    | 100        |    11.78 μs |  13.681 μs |  0.750 μs |  0.32 |    0.02 |      - |         - |          NA |
| FSharpDataAdaptive     | 10    | 100        |   103.24 μs |   6.499 μs |  0.356 μs |  2.77 |    0.01 | 5.6152 |   47200 B |          NA |
|                        |       |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop_Map2Chain** | **50**    | **100**        |   **239.59 μs** |  **13.165 μs** |  **0.722 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| AdaptiveSlop_Reduce    | 50    | 100        |    47.15 μs |  23.795 μs |  1.304 μs |  0.20 |    0.00 |      - |         - |          NA |
| FSharpDataAdaptive     | 50    | 100        |   421.08 μs |  49.163 μs |  2.695 μs |  1.76 |    0.01 | 5.3711 |   47200 B |          NA |
|                        |       |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop_Map2Chain** | **100**   | **100**        |   **517.17 μs** |  **50.103 μs** |  **2.746 μs** |  **1.00** |    **0.01** |      **-** |         **-** |          **NA** |
| AdaptiveSlop_Reduce    | 100   | 100        |    89.78 μs |   4.869 μs |  0.267 μs |  0.17 |    0.00 |      - |         - |          NA |
| FSharpDataAdaptive     | 100   | 100        |   798.61 μs | 186.620 μs | 10.229 μs |  1.54 |    0.02 | 4.8828 |   47200 B |          NA |
|                        |       |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop_Map2Chain** | **500**   | **100**        | **2,556.44 μs** | **434.509 μs** | **23.817 μs** |  **1.00** |    **0.01** |      **-** |         **-** |          **NA** |
| AdaptiveSlop_Reduce    | 500   | 100        |   444.75 μs |  14.891 μs |  0.816 μs |  0.17 |    0.00 |      - |         - |          NA |
| FSharpDataAdaptive     | 500   | 100        | 3,801.70 μs | 337.485 μs | 18.499 μs |  1.49 |    0.01 | 3.9063 |   47200 B |          NA |

### DeepWideBenchmarks

| Method             | Depth | BranchingFactor | Iterations | Mean         | Error        | StdDev     | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------- |------ |---------------- |----------- |-------------:|-------------:|-----------:|------:|--------:|-------:|----------:|------------:|
| **AdaptiveSlop**       | **3**     | **2**               | **50**         |     **11.64 μs** |     **0.608 μs** |   **0.033 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 3     | 2               | 50         |     35.65 μs |     8.745 μs |   0.479 μs |  3.06 |    0.04 | 2.8076 |   23600 B |          NA |
|                    |       |                 |            |              |              |            |       |         |        |           |             |
| **AdaptiveSlop**       | **3**     | **3**               | **50**         |     **34.95 μs** |     **3.068 μs** |   **0.168 μs** |  **1.00** |    **0.01** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 3     | 3               | 50         |     58.58 μs |    22.070 μs |   1.210 μs |  1.68 |    0.03 | 2.8076 |   23600 B |          NA |
|                    |       |                 |            |              |              |            |       |         |        |           |             |
| **AdaptiveSlop**       | **3**     | **4**               | **50**         |     **62.51 μs** |    **25.428 μs** |   **1.394 μs** |  **1.00** |    **0.03** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 3     | 4               | 50         |     72.10 μs |     6.843 μs |   0.375 μs |  1.15 |    0.02 | 2.8076 |   23600 B |          NA |
|                    |       |                 |            |              |              |            |       |         |        |           |             |
| **AdaptiveSlop**       | **5**     | **2**               | **50**         |     **30.14 μs** |     **0.526 μs** |   **0.029 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 5     | 2               | 50         |     50.47 μs |     5.044 μs |   0.276 μs |  1.67 |    0.01 | 2.8076 |   23600 B |          NA |
|                    |       |                 |            |              |              |            |       |         |        |           |             |
| **AdaptiveSlop**       | **5**     | **3**               | **50**         |    **161.28 μs** |    **20.298 μs** |   **1.113 μs** |  **1.00** |    **0.01** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 5     | 3               | 50         |     91.54 μs |    28.005 μs |   1.535 μs |  0.57 |    0.01 | 2.8076 |   23600 B |          NA |
|                    |       |                 |            |              |              |            |       |         |        |           |             |
| **AdaptiveSlop**       | **5**     | **4**               | **50**         |    **770.86 μs** |   **223.852 μs** |  **12.270 μs** |  **1.00** |    **0.02** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 5     | 4               | 50         |    120.74 μs |     0.917 μs |   0.050 μs |  0.16 |    0.00 | 2.6855 |   23600 B |          NA |
|                    |       |                 |            |              |              |            |       |         |        |           |             |
| **AdaptiveSlop**       | **7**     | **2**               | **50**         |     **86.24 μs** |    **26.637 μs** |   **1.460 μs** |  **1.00** |    **0.02** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 7     | 2               | 50         |     63.24 μs |     6.884 μs |   0.377 μs |  0.73 |    0.01 | 2.8076 |   23600 B |          NA |
|                    |       |                 |            |              |              |            |       |         |        |           |             |
| **AdaptiveSlop**       | **7**     | **3**               | **50**         |  **1,475.40 μs** |    **54.078 μs** |   **2.964 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 7     | 3               | 50         |    122.98 μs |    15.981 μs |   0.876 μs |  0.08 |    0.00 | 2.6855 |   23600 B |          NA |
|                    |       |                 |            |              |              |            |       |         |        |           |             |
| **AdaptiveSlop**       | **7**     | **4**               | **50**         | **11,903.02 μs** | **3,527.563 μs** | **193.358 μs** |  **1.00** |    **0.02** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 7     | 4               | 50         |    169.34 μs |    23.658 μs |   1.297 μs |  0.01 |    0.00 | 2.6855 |   23600 B |          NA |

### KipoPhysicsBenchmarks

| Method                         | EntityCount | Iterations | Mean       | Error       | StdDev     | Ratio | RatioSD | Gen0       | Gen1       | Gen2       | Allocated    | Alloc Ratio |
|------------------------------- |------------ |----------- |-----------:|------------:|-----------:|------:|--------:|-----------:|-----------:|-----------:|-------------:|------------:|
| **AdaptiveSlop**                   | **250**         | **50**         |   **2.857 ms** |   **0.2385 ms** |  **0.0131 ms** |  **1.00** |    **0.01** |   **671.8750** |   **128.9063** |          **-** |   **5501.97 KB** |       **1.000** |
| FSharpDataAdaptive             | 250         | 50         |   8.713 ms |   1.3929 ms |  0.0764 ms |  3.05 |    0.03 |  1718.7500 |   250.0000 |          - |  14115.15 KB |       2.565 |
| AdaptiveSlop_GraphDirect       | 250         | 50         |   1.109 ms |   0.0860 ms |  0.0047 ms |  0.39 |    0.00 |     1.9531 |          - |          - |     21.48 KB |       0.004 |
| FSharpDataAdaptive_GraphDirect | 250         | 50         |  85.754 ms | 446.9634 ms | 24.4996 ms | 30.01 |    7.43 | 12000.0000 |  9600.0000 |  7600.0000 |  97674.31 KB |      17.753 |
|                                |             |            |            |             |            |       |         |            |            |            |              |             |
| **AdaptiveSlop**                   | **1000**        | **50**         |  **12.209 ms** |   **1.0536 ms** |  **0.0578 ms** |  **1.00** |    **0.01** |  **2906.2500** |   **937.5000** |          **-** |  **23783.71 KB** |       **1.000** |
| FSharpDataAdaptive             | 1000        | 50         |  41.361 ms |   8.7723 ms |  0.4808 ms |  3.39 |    0.04 |  7583.3333 |  2250.0000 |          - |  62583.42 KB |       2.631 |
| AdaptiveSlop_GraphDirect       | 1000        | 50         |   4.214 ms |   0.1966 ms |  0.0108 ms |  0.35 |    0.00 |          - |          - |          - |     21.48 KB |       0.001 |
| FSharpDataAdaptive_GraphDirect | 1000        | 50         | 571.817 ms |  40.1666 ms |  2.2017 ms | 46.84 |    0.25 | 51000.0000 | 47000.0000 | 33000.0000 | 433005.69 KB |      18.206 |

### UnbalancedTreeBenchmarks

| Method                     | DeepBranchDepth | ShallowBranchCount | Iterations | Mean        | Error      | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|--------------------------- |---------------- |------------------- |----------- |------------:|-----------:|----------:|------:|--------:|-------:|----------:|------------:|
| **AdaptiveSlop_DeepChange**    | **10**              | **5**                  | **50**         |    **52.82 μs** |  **29.500 μs** |  **1.617 μs** |  **1.00** |    **0.04** |      **-** |         **-** |          **NA** |
| FDA_DeepChange             | 10              | 5                  | 50         |   117.21 μs |   3.203 μs |  0.176 μs |  2.22 |    0.06 | 2.6855 |   23600 B |          NA |
| AdaptiveSlop_ShallowChange | 10              | 5                  | 50         |    24.98 μs |   1.704 μs |  0.093 μs |  0.47 |    0.01 |      - |         - |          NA |
| FDA_ShallowChange          | 10              | 5                  | 50         |    55.87 μs |   2.302 μs |  0.126 μs |  1.06 |    0.03 | 2.8076 |   23600 B |          NA |
|                            |                 |                    |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **10**              | **20**                 | **50**         |   **123.22 μs** |   **5.020 μs** |  **0.275 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| FDA_DeepChange             | 10              | 20                 | 50         |   254.67 μs |  83.466 μs |  4.575 μs |  2.07 |    0.03 | 2.4414 |   23600 B |          NA |
| AdaptiveSlop_ShallowChange | 10              | 20                 | 50         |    93.09 μs |  23.722 μs |  1.300 μs |  0.76 |    0.01 |      - |         - |          NA |
| FDA_ShallowChange          | 10              | 20                 | 50         |   183.81 μs |  53.012 μs |  2.906 μs |  1.49 |    0.02 | 2.6855 |   23600 B |          NA |
|                            |                 |                    |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **10**              | **50**                 | **50**         |   **287.04 μs** | **185.472 μs** | **10.166 μs** |  **1.00** |    **0.04** |      **-** |         **-** |          **NA** |
| FDA_DeepChange             | 10              | 50                 | 50         |   488.50 μs |  77.460 μs |  4.246 μs |  1.70 |    0.05 | 1.9531 |   23600 B |          NA |
| AdaptiveSlop_ShallowChange | 10              | 50                 | 50         |   226.58 μs |  10.353 μs |  0.567 μs |  0.79 |    0.02 |      - |         - |          NA |
| FDA_ShallowChange          | 10              | 50                 | 50         |   434.02 μs | 296.380 μs | 16.246 μs |  1.51 |    0.07 | 2.4414 |   23600 B |          NA |
|                            |                 |                    |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **50**              | **5**                  | **50**         |   **199.81 μs** |  **64.698 μs** |  **3.546 μs** |  **1.00** |    **0.02** |      **-** |         **-** |          **NA** |
| FDA_DeepChange             | 50              | 5                  | 50         |   350.94 μs | 102.139 μs |  5.599 μs |  1.76 |    0.04 | 2.4414 |   23600 B |          NA |
| AdaptiveSlop_ShallowChange | 50              | 5                  | 50         |    65.05 μs |   9.572 μs |  0.525 μs |  0.33 |    0.01 |      - |         - |          NA |
| FDA_ShallowChange          | 50              | 5                  | 50         |    58.18 μs |   4.579 μs |  0.251 μs |  0.29 |    0.00 | 2.8076 |   23600 B |          NA |
|                            |                 |                    |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **50**              | **20**                 | **50**         |   **288.96 μs** |  **11.414 μs** |  **0.626 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| FDA_DeepChange             | 50              | 20                 | 50         |   547.21 μs | 111.425 μs |  6.108 μs |  1.89 |    0.02 | 1.9531 |   23600 B |          NA |
| AdaptiveSlop_ShallowChange | 50              | 20                 | 50         |   138.84 μs |  14.756 μs |  0.809 μs |  0.48 |    0.00 |      - |         - |          NA |
| FDA_ShallowChange          | 50              | 20                 | 50         |   178.28 μs |  12.831 μs |  0.703 μs |  0.62 |    0.00 | 2.6855 |   23600 B |          NA |
|                            |                 |                    |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **50**              | **50**                 | **50**         |   **444.31 μs** |  **75.382 μs** |  **4.132 μs** |  **1.00** |    **0.01** |      **-** |         **-** |          **NA** |
| FDA_DeepChange             | 50              | 50                 | 50         |   812.97 μs | 426.764 μs | 23.392 μs |  1.83 |    0.05 | 1.9531 |   23600 B |          NA |
| AdaptiveSlop_ShallowChange | 50              | 50                 | 50         |   276.80 μs |  27.846 μs |  1.526 μs |  0.62 |    0.01 |      - |         - |          NA |
| FDA_ShallowChange          | 50              | 50                 | 50         |   435.84 μs |  30.613 μs |  1.678 μs |  0.98 |    0.01 | 2.4414 |   23600 B |          NA |
|                            |                 |                    |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **100**             | **5**                  | **50**         |   **388.65 μs** |  **17.267 μs** |  **0.946 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| FDA_DeepChange             | 100             | 5                  | 50         |   638.74 μs |  42.979 μs |  2.356 μs |  1.64 |    0.01 | 1.9531 |   23600 B |          NA |
| AdaptiveSlop_ShallowChange | 100             | 5                  | 50         |   113.56 μs |  18.107 μs |  0.993 μs |  0.29 |    0.00 |      - |         - |          NA |
| FDA_ShallowChange          | 100             | 5                  | 50         |    55.98 μs |  24.155 μs |  1.324 μs |  0.14 |    0.00 | 2.8076 |   23600 B |          NA |
|                            |                 |                    |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **100**             | **20**                 | **50**         |   **451.66 μs** |  **80.497 μs** |  **4.412 μs** |  **1.00** |    **0.01** |      **-** |         **-** |          **NA** |
| FDA_DeepChange             | 100             | 20                 | 50         |   787.29 μs |  23.976 μs |  1.314 μs |  1.74 |    0.02 | 1.9531 |   23600 B |          NA |
| AdaptiveSlop_ShallowChange | 100             | 20                 | 50         |   179.59 μs |  12.087 μs |  0.663 μs |  0.40 |    0.00 |      - |         - |          NA |
| FDA_ShallowChange          | 100             | 20                 | 50         |   177.82 μs | 102.243 μs |  5.604 μs |  0.39 |    0.01 | 2.6855 |   23600 B |          NA |
|                            |                 |                    |            |             |            |           |       |         |        |           |             |
| **AdaptiveSlop_DeepChange**    | **100**             | **50**                 | **50**         |   **605.85 μs** |  **98.173 μs** |  **5.381 μs** |  **1.00** |    **0.01** |      **-** |         **-** |          **NA** |
| FDA_DeepChange             | 100             | 50                 | 50         | 1,108.45 μs | 523.472 μs | 28.693 μs |  1.83 |    0.04 | 1.9531 |   23600 B |          NA |
| AdaptiveSlop_ShallowChange | 100             | 50                 | 50         |   316.11 μs |   4.763 μs |  0.261 μs |  0.52 |    0.00 |      - |         - |          NA |
| FDA_ShallowChange          | 100             | 50                 | 50         |   410.73 μs | 226.817 μs | 12.433 μs |  0.68 |    0.02 | 2.4414 |   23600 B |          NA |

### IncrementalChainBenchmarks

| Method             | InitialSize | Mutations | Mean      | Error      | StdDev    | Ratio | RatioSD | Gen0     | Allocated | Alloc Ratio |
|------------------- |------------ |---------- |----------:|-----------:|----------:|------:|--------:|---------:|----------:|------------:|
| **AdaptiveSlop**       | **100**         | **200**       |  **60.18 μs** |  **14.686 μs** |  **0.805 μs** |  **1.00** |    **0.02** |        **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 100         | 200       | 658.98 μs | 591.662 μs | 32.431 μs | 10.95 |    0.48 |  76.1719 |  638704 B |          NA |
|                    |             |           |           |            |           |       |         |          |           |             |
| **AdaptiveSlop**       | **1000**        | **200**       |  **58.12 μs** |   **2.967 μs** |  **0.163 μs** |  **1.00** |    **0.00** |        **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 1000        | 200       | 630.61 μs |  11.287 μs |  0.619 μs | 10.85 |    0.03 |  78.1250 |  656512 B |          NA |
|                    |             |           |           |            |           |       |         |          |           |             |
| **AdaptiveSlop**       | **10000**       | **200**       |  **58.54 μs** |  **58.478 μs** |  **3.205 μs** |  **1.00** |    **0.07** |        **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 10000       | 200       | 734.08 μs | 347.676 μs | 19.057 μs | 12.56 |    0.65 | 101.5625 |  852736 B |          NA |

### ConcurrentBenchmarks

| Method             | ThreadCount | IterationsPerThread | Mean      | Error     | StdDev   | Ratio | RatioSD | Gen0     | Gen1   | Allocated | Alloc Ratio |
|------------------- |------------ |-------------------- |----------:|----------:|---------:|------:|--------:|---------:|-------:|----------:|------------:|
| AdaptiveSlop       | 4           | 500                 |  58.62 μs |  12.97 μs | 0.711 μs |  1.00 |    0.01 |   0.0610 |      - |     849 B |        1.00 |
| FSharpDataAdaptive | 4           | 500                 | 595.04 μs | 159.16 μs | 8.724 μs | 10.15 |    0.17 | 112.3047 | 0.9766 |  944848 B |    1,112.90 |

### ListAppendBenchmarks

| Method             | Iterations | Mean        | Error      | StdDev    | Ratio | RatioSD | Gen0     | Gen1     | Gen2   | Allocated | Alloc Ratio |
|------------------- |----------- |------------:|-----------:|----------:|------:|--------:|---------:|---------:|-------:|----------:|------------:|
| AdaptiveSlop       | 500        |    76.76 μs |   7.655 μs |  0.420 μs |  1.00 |    0.01 |        - |        - |      - |         - |          NA |
| FSharpDataAdaptive | 500        | 2,968.47 μs | 203.501 μs | 11.155 μs | 38.67 |    0.22 | 289.0625 | 285.1563 | 7.8125 | 2435024 B |          NA |

### ListTransformBenchmarks

| Method             | Iterations | Mean       | Error       | StdDev    | Ratio | RatioSD | Gen0     | Gen1     | Gen2   | Allocated | Alloc Ratio |
|------------------- |----------- |-----------:|------------:|----------:|------:|--------:|---------:|---------:|-------:|----------:|------------:|
| AdaptiveSlop       | 500        |   179.6 μs |    13.46 μs |   0.74 μs |  1.00 |    0.01 |        - |        - |      - |         - |          NA |
| FSharpDataAdaptive | 500        | 3,695.9 μs | 6,575.34 μs | 360.42 μs | 20.58 |    1.74 | 343.7500 | 167.9688 | 7.8125 | 2899813 B |          NA |

### ListWriteReadBenchmarks

| Method             | Count  | Mean      | Error       | StdDev    | Ratio | RatioSD | Allocated  | Alloc Ratio |
|------------------- |------- |----------:|------------:|----------:|------:|--------:|-----------:|------------:|
| **AdaptiveSlop**       | **0**      |  **23.61 μs** |    **10.80 μs** |  **0.592 μs** |  **1.00** |    **0.03** |   **17.29 KB** |        **1.00** |
| FSharpDataAdaptive | 0      | 308.24 μs |   879.10 μs | 48.186 μs | 13.06 |    1.79 |  141.05 KB |        8.16 |
|                    |        |           |             |           |       |         |            |             |
| **AdaptiveSlop**       | **1000**   |  **35.47 μs** |    **88.90 μs** |  **4.873 μs** |  **1.01** |    **0.17** |   **52.34 KB** |        **1.00** |
| FSharpDataAdaptive | 1000   | 420.59 μs |   628.12 μs | 34.429 μs | 12.00 |    1.60 |  180.94 KB |        3.46 |
|                    |        |           |             |           |       |         |            |             |
| **AdaptiveSlop**       | **10000**  |  **65.36 μs** |   **151.79 μs** |  **8.320 μs** |  **1.01** |    **0.15** |  **247.23 KB** |        **1.00** |
| FSharpDataAdaptive | 10000  | 452.37 μs |   591.90 μs | 32.444 μs |  6.99 |    0.84 |  203.28 KB |        0.82 |
|                    |        |           |             |           |       |         |            |             |
| **AdaptiveSlop**       | **100000** | **329.97 μs** | **1,512.64 μs** | **82.913 μs** |  **1.04** |    **0.32** | **2356.61 KB** |        **1.00** |
| FSharpDataAdaptive | 100000 | 277.89 μs |   437.38 μs | 23.974 μs |  0.88 |    0.19 |  222.34 KB |        0.09 |

### PollingBenchmarks

| Method             | ReadsPerWrite | Iterations | Mean      | Error     | StdDev   | Ratio | Gen0   | Allocated | Alloc Ratio |
|------------------- |-------------- |----------- |----------:|----------:|---------:|------:|-------:|----------:|------------:|
| **AdaptiveSlop**       | **2**             | **50**         | **203.15 μs** | **12.659 μs** | **0.694 μs** |  **1.00** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 2             | 50         |  91.00 μs |  5.803 μs | 0.318 μs |  0.45 | 2.8076 |   23600 B |          NA |
|                    |               |            |           |           |          |       |        |           |             |
| **AdaptiveSlop**       | **4**             | **50**         | **163.80 μs** | **22.822 μs** | **1.251 μs** |  **1.00** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 4             | 50         |  92.10 μs |  6.020 μs | 0.330 μs |  0.56 | 2.8076 |   23600 B |          NA |
|                    |               |            |           |           |          |       |        |           |             |
| **AdaptiveSlop**       | **8**             | **50**         | **170.52 μs** | **21.173 μs** | **1.161 μs** |  **1.00** |      **-** |         **-** |          **NA** |
| FSharpDataAdaptive | 8             | 50         |  93.64 μs |  1.245 μs | 0.068 μs |  0.55 | 2.8076 |   23600 B |          NA |
