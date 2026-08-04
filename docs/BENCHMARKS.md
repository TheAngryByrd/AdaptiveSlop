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
