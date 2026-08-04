# AdaptiveSlop — Rebuild Plan

This document specifies the rebuild of the AdaptiveSlop core library. It contains all the
information necessary for the implementation. It has no external references.

## Status

- Done: Phase 0, Phase 1, Phase 2, Phase 3, Phase 4 (2026-08-03).
- Next: Phase 5 (cross-thread posting).

## 1. Targets

In order of priority:

1. **Throughput.** Do the minimum runtime work per operation.
2. **Zero library-side allocation** on steady-state paths.
3. Engineering effort is not a constraint.

The library is general-purpose. It is not specific to game development. Any application
that writes frequently and reads frequently gets the performance for free.

## 2. Definitions

Use these terms with these exact meanings:

- **Source**: a changeable object (value, set, or map). The user writes to sources.
- **Node**: a computed object. The library creates nodes from combinators such as `map`.
- **Graph**: the sources, the nodes, and the edges between them.
- **Edge**: a link from an object to one of its dependents. Edges point up, from a
  dependency to a parent node.
- **Read**: a call to `GetValue` on a node or a source.
- **Write**: a call that changes a source (`Set`, `Add`, `Remove`).
- **Mark**: to set the dirty flag of a node.
- **Batch**: one transaction. The library applies the writes of a batch at commit.
- **Owner thread**: the single thread that operates on a graph.
- **Post**: to send a change from a foreign thread to the owner thread.
- **Pump**: the owner-thread procedure that applies posted changes.

## 3. Workload model

Make no assumptions about write rates or read rates. The design must support:

- Many writes to the same source between two reads.
- Many reads per time window.
- Arbitrary interleaving of writes and reads.
- Reads that cause further writes (the application updates the graph after a computation).
- One or more foreign threads that post changes to the owner thread.
- Large graphs of which only a part is read.

Correctness must not depend on batch boundaries. Batches improve efficiency. They are not
necessary for correctness.

## 4. Work budget

The work must be proportional to the changes and the reads. Nothing more.

Per operation:

- **Write**: O(m), where m is the number of nodes marked. Marking stops at nodes that are
  already dirty.
- **Clean read**: O(1) per node. One flag check. No lock. No dependency scan.
- **Dirty read**: the cost of the user function, plus O(fan-in) for the dependency
  comparison. No edge writes when the dependency set does not change.
- **Collection write**: O(k), where k is the number of elements added or removed.

A read must never scan the full graph. A write must never recompute.

## 5. Allocation budget

Library-side, in steady state:

| Path | Budget |
|---|---|
| Clean read | 0 |
| Dirty read, unchanged dependency set | 0 |
| Write + mark | 0 |
| Post / pump | 0 |
| Batch with N writes | 0 |
| Collection delta delivery | 0 |
| Edge change (`bind` switch, observe, dispose) | Amortized; array growth only |
| Collection snapshot read | O(n), only when read after a change |

Immutable snapshots are inherently allocating. Hot paths must consume deltas, not
snapshots. This is a usage rule, not a defect.

### Known violations in the current code

Remove these during the rebuild:

- `MapNNode.Recompute` allocates the values array on each recompute (`Library.fs:1102`).
- Each `ChangeableValue.Set` allocates a commit object and a closure (`Library.fs:566`).
- `ChangeableSet/Map.ApplyAndFlush` rents two pooled arrays per single-element operation
  (`Library.fs:1467-1478, 1755-1766`).
- `FlushDeltas` allocates a snapshot of the sink array on each flush (`Library.fs:1412`).
- `MapSetNode` updates a persistent snapshot for each delta element, also when never read
  (`AdaptiveCollections.fs:169, 186`).
- Locks, `Interlocked`, and `[<ThreadStatic>]` throughout the core. Confinement removes
  them (Section 7).

Keep these existing mechanisms: the reused transaction buffer, the reused dependency
collector buffers, the pooled journal arrays in the changeable collections.

## 6. Core architecture

### 6.1 Push-mark, pull-evaluate

- On write: mark the dependents of the source. Do not recompute.
- On read: if the node is dirty, recompute. If not, return the cached value.
- A node recomputes at most once per change.

### 6.2 Edges

Each object stores its edges in two parallel arrays:

- `parents[]`: the dependent nodes.
- `parentSlots[]`: for each parent, the position of this object in the dependency list of
  that parent.

Each node also stores `deps[]` and `depSlots[]`: for each dependency, the position of this
node in the `parents[]` array of that dependency.

Removal of an edge:

1. Swap the last entry of the array into the removed position.
2. Update the stored slot of the moved entry.

Removal is O(1). It does not scan and does not allocate.

Edge formation and maintenance:

- When a node recomputes, it reads its dependencies. The runtime collects these reads.
- Compare the new dependency set with the stored set:
  - If the sets are equal: update the version snapshots only. Do not touch the edges.
  - If the sets differ: add edges for the new dependencies. Remove the edges for the
    dropped dependencies. Use the slots for O(1) removal.
- A recompute with an unchanged dependency set writes no edges. This is the common case.

### 6.3 Two-tier reads

- A node with at least one parent is **observed**. Marking reaches it. Its dirty check is
  one flag read.
- A node without parents is **unobserved**. Its dirty check compares the versions of its
  dependencies against the stored snapshots.
- The third state, `MaybeDirty`, means: parent links are incomplete. Fall back to the
  version check.

This split keeps unobserved subgraphs free on write and keeps observed reads at O(1).

### 6.4 Registration cascade

- When a node gains its first parent, it registers itself with its dependencies. The
  dependencies become observed. This rule applies recursively.
- When a node loses its last parent, it unregisters from its dependencies.
- The cascade makes the dirty flag trustworthy for every observed node.

### 6.5 No evaluation during marking

- Marking must not trigger reads.
- Notification callbacks are queued during marking. The library delivers them after the
  batch or the pump completes.
- This rule removes the need for topological levels. Do not add levels, priority queues,
  or re-enqueue logic.

### 6.6 Equality at the source

- A write that does not change the value (per the equality check of the source) must not
  mark and must not increase the version.

### 6.7 Node types

- One generic node type implements the full protocol: edges, dirty state, registration.
- `ChangeableValue` implements the same parent protocol.
- Keep `MapNNode` and `ReduceNode` for wide fan-in.
- Remove `Map3Node` and `Map4Node`. Re-add them only if benchmarks show a need.

### 6.8 Transactions

- A write inside a transaction goes into a per-source pending slot. There is no object
  allocation per write.
- At commit: apply each pending write, then mark.
- If one source is written multiple times in one transaction, keep only the last value.
  This is correct because no read can observe the intermediate values inside a
  transaction.
- Reads inside a transaction see the pre-transaction values. Document this behavior.

## 7. Threading

### 7.1 Confinement

- One graph has exactly one owner thread. All graph operations occur on the owner thread.
- The core contains no locks, no `Interlocked`, no `[<ThreadStatic>]`.
- Rationale: concurrent lock-free graph mutation is complex and error-prone. Confinement
  gives equal or better throughput for this workload, at zero synchronization cost.

### 7.2 Graph context

Each graph has one context object. It holds:

- the evaluation id,
- the dependency collector,
- the transaction buffer,
- the marking stack,
- the notification queue,
- the owner thread id.

In debug builds, each entry point checks the current thread against the owner thread id.
A violation throws an exception.

### 7.3 Cross-thread changes

Foreign threads may only post. They must not read or write the graph.

`Post(value)` on a source:

1. Write the value into a per-source pending field. The field is typed. There is no
   boxing.
2. If the source is not already in the post queue, push the source reference onto the
   queue.

The queue is a bounded multi-producer, single-consumer ring buffer. It is preallocated.
Each slot carries a sequence number for synchronization.

`Pump()` on the owner thread:

1. Drain the ring.
2. In one transaction: apply each pending value. Apply includes the equality check and
   the mark.

Duplicate posts to one source in one window collapse to one application. This is safe
because no read occurs during the drain.

### 7.4 Multiple graphs

An application may run one graph per thread. To synchronize two graphs, post collection
deltas between them. The collections already produce deltas.

## 8. Phases

### Phase 0 — Safety net

- Run the existing test suite as the baseline.
- Add characterization tests: deep chains, diamonds, `bind` inner switching, reads inside
  transactions, collection delta sequences.
- Exit: the suite is green with the new coverage.

### Phase 1 — De-thread the core

- Add the graph context object (Section 7.2).
- Remove all locks, `Interlocked`, `[<ThreadStatic>]`, and per-node sync roots.
- Remove the inactive push machinery. Phase 2 replaces it.
- Exit: the suite is green. Pure pull, version-checked, single-threaded, no locks.

### Phase 2 — Real edges

- Implement the edge protocol of Section 6.2 on every node type.
- Implement the dependency comparison on recompute. Mutate edges only on a real change.
- Implement the registration cascade of Section 6.4.
- Exit: the suite is green, plus new tests that prove edges form and break on recompute,
  on `bind` switches, and on cascade disposal. No behavior change yet.

### Phase 3 — Activate push-mark

- On source write: mark iteratively with the pooled marking stack. Do not use recursion.
- Observed dirty check: one flag read, no version reads.
- Add the source equality check (Section 6.6).
- Add per-source pending slots and write coalescing in transactions (Section 6.8).
- Regression tests:
  - An observed chain of several computed nodes must update after a source write.
  - A mixed chain of generic and specialized nodes must update after a source write.
  - Several writes to one source between two reads must produce the last value.
  - Arbitrary interleaving of writes and reads must stay correct.
- Exit: the suite is green. A steady-state observed clean read allocates 0 bytes, reads
  no versions, and writes no edges.

### Phase 4 — Observation

- `observe` forces an initial read and registers a callback sink as a parent.
- Deliver notifications after the batch or the pump (Section 6.5).
- `Dispose` removes the link and starts the unregistration cascade.
- Edges are strong. The user must dispose. Document this contract.
- Exit: tests for notification delivery, dispose cascade, and absence of leaks.

### Phase 5 — Cross-thread posting

- Implement the per-source pending field and the preallocated ring (Section 7.3).
- Implement `Post` and `Pump`.
- Write the threading guidance for users.
- Exit: a two-thread test (a producer posts; the owner pumps and reads) with no
  synchronization in the core. A stress test with many posts per window, interleaved with
  reads on the owner thread.

### Phase 6 — Collections lifecycle

- Register derived collections with their source lazily: on first read or first sink, not
  at construction.
- Make derived collections disposable.
- Replace per-delta snapshot updates with lazy invalidation. Rebuild snapshots on read.
- Remove the per-operation array rentals and the per-flush sink snapshots.
- Exit: a leak test (derive, dispose, mutate the source, check the source has no sinks).
  An allocation test: delivery of an N-element delta allocates 0 bytes.

### Phase 7 — Hardening

- Benchmark against the Phase 0 baseline. Measure allocations with
  `GC.GetAllocatedBytesForCurrentThread`.
- Make the allocation assertions permanent tests.
- Only if the data demands it: re-add specialized small-arity nodes, output-equality
  predicates, or marking-stack tuning.

## 9. Do not build

- Levels or topological marking.
- Weak-reference edges.
- Checkpoint history for collections.
- Persistent tree structures for state or for deltas.
- Lock-free graph internals.
- Specialized small-arity nodes (unless benchmarks show a need).
- Output-equality predicates on computed nodes (unless benchmarks show a need).

## 10. Definition of done for the base (end of Phase 3)

- A write touches only O(m) nodes and allocates 0 bytes.
- A clean read does one flag check and allocates 0 bytes.
- A recompute with an unchanged dependency set writes no edges and allocates 0 bytes on
  the library side.
- Correctness holds for arbitrary interleaving of writes and reads, including several
  writes to one source between two reads.
- The full test suite is green.
