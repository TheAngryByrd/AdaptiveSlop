# AdaptiveSlop

A high-performance, low-allocation incremental/adaptive computation library for F#,
inspired by [FSharp.Data.Adaptive](https://github.com/fsprojects/FSharp.Data.Adaptive) (FDA).

AdaptiveSlop tracks dependencies automatically and recomputes only what changed, with a
focus on memory efficiency and tight-loop (game/simulation) workloads.

## Features

- **Automatic dependency tracking** — no manual subscription management
- **Lazy pull evaluation** — values recompute only when read and dirty
- **Incremental collections** — adaptive sets/maps propagate element-level deltas
- **Low allocation** — up to 14x less memory than FDA
- **Thread-safe** — concurrent read/write via per-node locking
- **Transactions** — batch updates applied atomically at commit

## Installation

```bash
dotnet add package AdaptiveSlop.Core
```

## Quick Start

```fsharp
open AdaptiveSlop.Core

let width  = CVal.create 10.0
let height = CVal.create 20.0

// Computed values track dependencies automatically
let area = AVal.map2 (*) (CVal.value width) (CVal.value height)

AVal.getValue area   // 200.0
width.Set(15.0)
AVal.getValue area   // 300.0
```

## Core Concepts

### Changeable values (`CVal`) — the inputs

```fsharp
let counter = CVal.create 0
counter.Set(42)
let v = CVal.value counter   // IAdaptiveValue<int> view, for building computations
```

### Adaptive values (`AVal`) — computed nodes

```fsharp
let doubled = AVal.map  (fun x -> x * 2) (CVal.value counter)
let sum     = AVal.map2 (+) (CVal.value a) (CVal.value b)
let rgb     = AVal.map3 (fun r g b -> (r, g, b)) (CVal.value r) (CVal.value g) (CVal.value b)
```

Recomputation is lazy: nothing recomputes until you call `GetValue()`, and then only if a
dependency changed since the last read.

**Wide fan-in (5+ inputs):** use the single-node operations — dramatically faster than
chaining `map2`:

```fsharp
let deps = sensors |> Array.map (fun s -> CVal.value s :> IAdaptiveValue<float>)
let average = AVal.mapN (fun values -> Array.average values) deps
let total   = AVal.reduce 0.0 (+) deps      // no intermediate array
let intSum  = AVal.sum intDeps              // convenience for int
```

### Transactions

```fsharp
Transaction.run (fun () ->
    width.Set(100.0)
    height.Set(50.0))
// both changes apply atomically at commit
```

Note: changes inside a transaction are applied at commit — reads *inside* the transaction
still see the pre-transaction values.

### Adaptive collections

Sets and maps propagate **element-level deltas** (added/removed) instead of recomputing
wholesale:

```fsharp
let items = CSet.ofSeq [1; 2; 3]
let doubled  = ASet.map (fun x -> x * 2) (CSet.value items)
let filtered = ASet.filter (fun x -> x > 2) (CSet.value items)
items.Add(4)    // downstream nodes process one element, not the whole set

let scores = CMap.ofSeq [("Alice", 95)]
scores.AddOrUpdate("Bob", 87)
```

## API Reference

| Module | Functions |
|--------|-----------|
| `AVal` | `constant`, `map`, `map2`, `map3`, `map4`, `mapN`, `reduce`, `sum`, `bind`, `getValue` (+ `Task`/`ValueTask` variants) |
| `CVal` | `create`, `value`, `set` |
| `CSet` / `CMap` | `empty`, `ofSeq`, `add` / `addOrUpdate`, `remove`, `set`, `value` |
| `ASet` / `AMap` | `map`, `filter` (+ `union` for sets) |
| `Transaction` | `run` |

## Performance

vs FSharp.Data.Adaptive (BenchmarkDotNet):

| Scenario | Speed | Memory |
|----------|-------|--------|
| Deep chains (depth 20) | 1.2x faster | 143 KB vs 230 KB |
| Wide fan-in (100 inputs) | 3.1x faster | 3.1 KB vs 46 KB |

Main optimizations: single-node N-ary combinators, per-evaluation dirty cache, pooled
arrays, struct tuples on hot paths.

**Guidance:** `map`/`map2` for 1–2 deps, `map3`/`map4` for 3–4, `mapN`/`reduce`/`sum` for
5+; consume collection **deltas** on hot paths rather than re-reading whole snapshots.

## Architecture

- **Pull-based with version checking.** Each node caches its value plus a snapshot of its
  dependencies' version numbers. `GetValue()` recomputes only when a dependency's version
  has changed. Dependencies are re-discovered on every recompute, so dynamic graphs
  (`bind`) stay correct.
- **Collections push deltas.** Changeable sets/maps journal added/removed elements and push
  them to derived nodes, which update per-element (with ref-counting for shared outputs).
- **Thread safety** via per-node locks; transactions are thread-local.
- Marking/push-invalidation machinery for *observation* scenarios exists but is not yet
  active — see the roadmap.

## Known Limitations

- **No observation API yet** (`IObservation` exists but `AVal.observe` is unimplemented) —
  all recomputation is pull-driven.
- **Async hazard**: `[<ThreadStatic>]` dependency tracking does not flow across `Task`
  continuations; keep compute functions synchronous.
- Derived collection nodes register with their source at construction and are retained by
  it (explicit lifecycle management is on the roadmap).
- No incremental `bind` — switching the inner value recomputes it fully.

## Roadmap & Design Docs

The library is being rebuilt towards a push-mark/pull-evaluate core with owner-thread
confinement and zero-allocation hot paths. See:

- `docs/PLAN.md` — the phased rebuild plan and threading model
- `docs/ANALYSIS-FDA.md` — verified analysis of FDA's internals
- `docs/SIGNALS-COMPARISON.md` — comparison with JS signals and the underlying research

## License

MIT
