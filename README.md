# AdaptiveSlop

A high-performance, low-allocation incremental/adaptive computation library for F#,
inspired by [FSharp.Data.Adaptive](https://github.com/fsprojects/FSharp.Data.Adaptive) (FDA).

AdaptiveSlop tracks dependencies automatically and recomputes only what changed, with a
focus on memory efficiency and tight-loop (game/simulation) workloads.

## Features

- **Automatic dependency tracking** — no manual subscription management
- **Lazy pull evaluation** — values recompute only when read and dirty
- **Incremental collections** — adaptive sets/maps propagate element-level deltas
- **Low allocation** — zero allocation on steady-state reads and writes
- **Owner-thread confinement** — one thread per graph; foreign threads send changes with lock-free `Post`, applied automatically at the owner's next graph operation (optional explicit `Posting.pump()`)
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

### Cross-thread posting

A graph belongs to one **owner thread**. Only the owner reads and writes the graph.
Foreign threads send changes with `Post`; the owner's next graph operation applies them
automatically:

```fsharp
// worker thread
CVal.post (health - 1) health

// owner thread: no pump call needed
let h = AVal.getValue health
```

Posting rules:

- `Post` is lock-free and allocates nothing. It writes a typed pending field and, if the
  source is not queued yet, pushes the source onto a bounded preallocated ring.
- Pending posts are applied automatically at the start of the next graph operation on
  the owner thread, as one batch with one notification delivery. Several posts to one
  source before the application collapse to one application of the last value.
- The source equality check still applies at application: posting an equal value marks
  nothing.
- `Posting.pump()` is optional: it forces application at a chosen boundary (for
  example, once per frame). It runs on the owner thread only and is cheap and
  allocation-free when the queue is empty.

### Adaptive collections

Sets and maps propagate **element-level deltas** (added/removed) instead of recomputing
wholesale. Writes are journaled (zero allocation); nodes process pending deltas on read:

```fsharp
let items = CSet.ofSeq [1; 2; 3]
let doubled  = ASet.map (fun x -> x * 2) (CSet.value items)
let filtered = ASet.filter (fun x -> x > 2) (CSet.value items)
items.Add(4)    // downstream nodes process one element, not the whole set
```

- `ASet.getValue` / `AMap.getValue` return a **transient view** of the internal state:
  valid only until the next write on the owner thread. Computations consume it; do not
  retain or mutate it.
- `ASet.force` / `AMap.force` materialize an immutable `FrozenSet`/`FrozenDictionary`
  checkpoint. This is the only collection operation that allocates, and the only result
  safe to retain: the library never touches a forced value again.
- `ASet.toSet` / `AMap.toMap` (and `CSet.toSet` / `CMap.toMap`) materialize the F#
  `Set`/`Map` counterparts for sorted iteration and F# interop.
- Derived collections register with their dependencies lazily (first read) and are
  `IDisposable`; disposal stops all delta processing. Reading a disposed node throws.
- The collection interfaces do not require `: comparison` (hash-based internally); the
  F#-interop helpers re-impose it at their boundary.

## API Reference

| Module | Functions |
|--------|-----------|
| `AVal` | `constant`, `map`, `map2`, `map3`, `map4`, `mapN`, `reduce`, `sum`, `bind`, `observe`, `getValue` (+ `Task`/`ValueTask` variants) |
| `CVal` | `create`, `value`, `set`, `post` |
| `CSet` / `CMap` | `empty`, `ofSeq`, `add` / `addOrUpdate`, `remove`, `set`, `value`, `force`, `toSet` / `toMap` |
| `ASet` / `AMap` | `map`, `filter` (+ `union` for sets), `getValue`, `force`, `toSet` / `toMap` |
| `Transaction` | `run` |
| `Posting` | `pump` |

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

- **Push-mark, pull-evaluate.** Writes mark observed subgraphs dirty; reads recompute a
  dirty node exactly once per change and return cached values otherwise. Unobserved
  nodes fall back to dependency version checks.
- **Lazy edges.** Dependencies are re-discovered on every recompute, so dynamic graphs
  (`bind`) stay correct. Edge sets mutate only when they really change.
- **Collections push deltas.** Changeable sets/maps journal added/removed elements and push
  them to derived nodes, which update per-element (with ref-counting for shared outputs).
- **Owner-thread confinement.** One thread owns a graph. The core has no locks.
  Cross-thread changes go through `Post` + `Posting.pump()` (see above).
- **Observation.** `AVal.observe` registers a strong parent edge and delivers the callback
  after a batch or a write. Dispose the observation to stop; edges are strong, so an
  undropped observation keeps the subgraph alive.

## Known Limitations

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
