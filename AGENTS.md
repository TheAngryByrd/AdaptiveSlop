# AdaptiveSlop - LLM Implementation Guide

This document provides implementation details and context for LLMs working with the AdaptiveSlop codebase.

## Project Structure

```
AdaptiveSlop/
├── src/
│   └── AdaptiveSlop.Core/
│       └── Library.fs          # Main implementation (all types and modules)
├── tests/
│   └── AdaptiveSlop.Tests/
│       └── Tests.fs            # Unit tests (68 tests)
├── benchmarks/
│   └── AdaptiveSlop.Benchmarks/
│       └── Program.fs          # BenchmarkDotNet benchmarks
├── README.md                   # User documentation
├── ralph-todo.md              # Development notes and optimization history
└── docs/
    └── LLM-GUIDE.md           # This file
```

## Core Architecture

### Dependency Graph Model

AdaptiveSlop implements an **incremental computation** system where:

1. **Changeable values** (`ChangeableValue<'T>`) are the leaves/inputs
2. **Adaptive nodes** compute derived values from dependencies
3. **Reads are lazy** - computation only happens when `GetValue()` is called
4. **Changes propagate** via version numbers and optional dirty flags

### Key Types (in Library.fs)

| Type | Purpose |
|------|---------|
| `IAdaptiveObject` | Base interface with `Version: int64` for change detection |
| `IAdaptiveValue<'T>` | Readable adaptive value with `GetValue()` |
| `IMarkable` | Internal interface for dirty propagation |
| `ChangeableValue<'T>` | Mutable input value |
| `AdaptiveNode<'T>` | Generic computed node (used by `map`, `map2`, `bind`) |
| `Map3Node<'A,'B,'C,'T>` | Optimized 3-input node with inline fields |
| `Map4Node<'A,'B,'C,'D,'T>` | Optimized 4-input node with inline fields |
| `MapNNode<'T,'U>` | N-input node for wide fan-in |
| `ReduceNode<'T>` | N-input reduction node |

### Hybrid Pull/Push Model

The system uses a **hybrid approach**:

#### Pull (Default)
- Each node stores `depVersions` (snapshot of dependency versions at last compute)
- On `GetValue()`, compares current dependency versions to snapshot
- Recomputes if any version changed
- Per-evaluation dirty cache prevents O(depth^2) work in deep chains

#### Push (When Observed)
- Specialized nodes (`Map3Node`, `Map4Node`, `MapNNode`, `ReduceNode`) support lazy push
- When a node has parents (is observed), it registers with `ChangeableValue` dependencies
- On source change, `MarkDirty()` propagates up the graph
- Avoids version checking for nodes known to be clean

### Thread Safety Model

- All node state is protected by `Monitor.Enter/Exit` (lock)
- `ChangeableValue.Version` uses `Interlocked.Read`
- Thread-static `EvaluationContext` tracks current evaluation (prevents re-entrancy issues)
- Transactions are thread-local (via `[<ThreadStatic>]`)

## Implementation Details

### AdaptiveNode (Generic Computed Node)

```fsharp
type AdaptiveNode<'T>(compute: unit -> 'T) =
    // Uses DependencyCollector to track which values were read during compute
    // Stores (deps, versions) snapshot for dirty checking
    // Re-entrant safe via frame-based collector
```

### Specialized N-ary Nodes

The `Map3Node`, `Map4Node`, `MapNNode`, `ReduceNode` types are optimized for:

1. **Fewer allocations** - inline fields instead of arrays for Map3/Map4
2. **Single node** - instead of O(N) nodes from chained `map2`
3. **Push invalidation** - registers with `ChangeableValue` when observed

**Critical IsDirty() Logic:**

```fsharp
member private this.IsDirty() =
    // If explicitly dirty from push notification
    if dirtyState = DirtyState.Dirty then true
    // If NOT observed, always check versions (no push possible)
    elif not isObserved then
        // ... version check ...
    // If observed and clean, trust dirty state
    elif dirtyState = DirtyState.Clean then false
    // MaybeDirty - fall back to version check
    else // ... version check ...
```

This was a critical bug fix - without the `not isObserved` check, nodes would return cached values after source changes.

### DependencyCollector

The `DependencyCollector` is a thread-static, frame-based system for tracking dependencies:

```fsharp
// During compute:
AdaptiveRuntime.enterEvaluation()  // Push frame
try
    let result = compute()
    // Dependencies collected via addDependency calls
finally
    AdaptiveRuntime.exitEvaluation()  // Pop frame
```

This allows nested reads (computing one value reads another) without corrupting dependency tracking.

### Transaction System

```fsharp
Transaction.run (fun () ->
    // Changes enqueued to thread-local TransactionState
    // Not applied until transaction commits
)
```

Uses `[<ThreadStatic>]` for thread-local transaction context.

## Performance Characteristics

### Memory

| Metric | AdaptiveSlop | FSharp.Data.Adaptive |
|--------|--------------|----------------------|
| Deep chain (depth 20) | 143 KB | 230 KB |
| Wide fan-in (100 inputs) | 3.1 KB | 46 KB |

Key optimizations:
- Struct tuples for hot path returns
- Array pooling for dependency snapshots
- Inline fields in specialized nodes

### Speed

| Scenario | Performance |
|----------|-------------|
| Deep chains | 1.2x faster than FDA |
| Wide fan-in (reduce) | 3x faster than FDA |
| Wide fan-in (map2 chain) | Similar to FDA |

Key optimizations:
- Per-evaluation dirty cache (eliminates O(depth^2))
- Single-node N-ary operations (eliminates O(N) node overhead)
- Lazy push invalidation (avoids scanning unchanged branches)

## Common Modifications

### Adding a New Combinator

1. For small arity (5-6 inputs), consider adding `Map5Node`/`Map6Node` following the Map3/Map4 pattern
2. For variadic operations, add to `AVal` module using `MapNNode` or `ReduceNode`
3. Add XML doc comments with examples
4. Add unit tests in `Tests.fs`

### Performance Optimization

1. Profile with BenchmarkDotNet (`benchmarks/AdaptiveSlop.Benchmarks/`)
2. Check for allocations in hot paths (use struct tuples, pooled arrays)
3. Consider specialized node types for common patterns
4. Add benchmarks for new scenarios

### Debugging Tips

1. Check `Version` property to see if values are updating
2. Use `dirtyState` field to verify push propagation
3. The `isObserved` flag indicates if push invalidation is active
4. Thread-static state can cause issues in async code - be careful with evaluation context

## Test Coverage

68 tests covering:
- Basic operations (map, map2, bind)
- N-ary operations (map3, map4, mapN, reduce, sum)
- Collections (ASet, AMap)
- Transactions
- Concurrency hazards
- Edge cases (empty arrays, single elements)

Run tests:
```bash
dotnet test tests/AdaptiveSlop.Tests/AdaptiveSlop.Tests.fsproj
```

## Benchmark Suite

```bash
cd benchmarks/AdaptiveSlop.Benchmarks
dotnet run -c Release -- --filter "*" --job short
```

Key benchmark classes:
- `DeepChainBenchmarks` - Deep dependency chains
- `WideTreeBenchmarks` - Wide fan-in patterns
- `OptimizedWideTreeBenchmarks` - Compares map2 chain vs reduce

## Known Limitations

1. **Observation API not yet public** - The `IObservation` interface exists but `AVal.observe` is not implemented
2. **No incremental collections** - Unlike FDA, set/map operations fully recompute
3. **No weak reference support** - Long-lived graphs may retain memory
4. **Dynamic dependencies recompute fully** - `bind` doesn't track dependency changes incrementally

## Development Notes

See `ralph-todo.md` for:
- Optimization history and benchmarks
- Oracle consultation summaries
- Future optimization ideas
- Implementation decision rationale
