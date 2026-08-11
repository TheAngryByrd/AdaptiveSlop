# Join + GroupBy Design — per-key joins and live per-group maps

Status: **implemented (2026-08-10)** on `feat/joinon-groupby-reductions`.
Motivation and measurements: `E:\Defli\docs\2026-08-07-adaptive-slop-join-assessment.md`,
`E:\Mibo\src\Defli.Raylib\README.md` (Homing = 28.5% of busy; ~5% of busy is
pure library allocation from the per-frame subgraph rebuild of the
`mapA` + `tryFind` join idiom).

## 1. The problem

Defli's join idiom is always `AMap.mapA`/`chooseA` over one map plus a
per-element `AMap.tryFind` into another — a hand-rolled equi-join. The
library offered no alternative: `Choose2MapNode` takes plain values and
combines on equal map keys only; it cannot express a computed join key (a
value inside the left row), a 3-way join, or a `voption` output.

The structural cost: `ElementMapNode.DrainJournal` re-runs the mapping
closure for every journal entry — including `addOrUpdate` of an existing key —
and rebuilds the per-key aval subgraph from scratch. In the game regime every
key updates every frame, so the join rebuilt `MapLookupNode + AdaptiveNode`
per key per frame. The first recompute of every fresh node allocates its
`deps`/`depVersions` arrays (the measured `ZeroCreate`).

## 2. The join node (`AMap.joinOn`)

`JoinMapNode<'K1,'V1,'K2,'V2,'U>` (ElementMapNode.fs):

- The left map is enumerated per key; the join key is computed from the left
  entry (`keyOfLeft`); the right map is looked up per entry
  (`MapLookupNode`: registers nothing, read-time gate), never enumerated or
  rebuilt.
- The mapping receives `'K1 -> aval<'V1> -> aval<'V2 voption> -> aval<'U voption>`:
  the left value as a **swappable input** (a `ChangeableValue` the node
  re-applies on every update) and the right-side lookup. A `ValueNone`
  output drops the entry (choose semantics); the right side as `voption`
  gives left-join semantics.
- The per-key subgraph is built **once** and updated in place: a left update
  re-applies the cell (equality-gated, marks the subgraph dirty) — no
  rebuild, no per-frame allocation. A join-key change (rare: a target
  switch) re-runs the mapping against the new lookup; the cell survives.
- Right-map changes reach the entries through the lookups (version-gated,
  re-read at force time) and through the element scan (gated on the write
  generation, which every delta delivery bumps).

Measured (docs/BENCHMARKS.md, 2026-08-10): on the Defli Homing workload
(100 projectiles, 200 enemies, per-frame left churn) the idiom allocates
~2.1 MB per operation; `joinOn` allocates 0 B and is ~36% faster.

### 2.1 Why not change `mapA` instead?

The generic `mapA` closure owns its subgraph; the library cannot reuse it
without changing the mapping contract. `joinOn` is the sanctioned low-churn
form: the node owns the subgraph construction, so it can swap the input.
`mapA` closures that are value-independent still rebuild — that is inherent
to the closure-owned subgraph. `tryFind` memoization was considered and
rejected for this PR (identity of returned nodes would change; `joinOn` owns
its lookups).

## 3. The groupBy node (`AMap.groupBy`)

`GroupByMapNode<'K,'V,'G>` + `GroupMapChildNode<'K,'V>` (MapNodes.fs):

- Every group is a **live adaptive map** (a `GroupMapChildNode`): the groupBy
  drain routes source deltas into the children by computed key; the children
  deliver their own deltas to their own consumers. Group-content changes
  never re-read the whole map and never re-run the key function for other
  groups.
- A group disappears when it becomes empty (removed at the next drain).
- A key whose value changes group is moved: removed from the old child,
  added to the new one. The per-key group is tracked in a `memberGroup` map,
  because a remove delta carries no value to compute the group from.
- The output is `amap<'G, amap<'K,'V>>`; the output map's version moves only
  for group add/remove.

Decisions recorded (open questions from the review):

- Output shape: `amap<'G, amap<'K,'V>>` — adaptive per-group maps.
- Key function: plain `'K -> 'V -> 'G`. An aval-returning key function
  (adaptive membership) is not provided: it would need per-entry key avals
  and re-keying machinery; revisit when usage appears.
- Empty-group timing: removed at the next drain (never visible as an empty
  group in the output).

## 4. Argument order

`joinOn` takes `(keyOfLeft) (mapping) (left) (right)`, matching `choose2`'s
convention (config first, then the collections, left before right). The pipe
form does not apply: a piped value lands in the `right` slot.

## 5. Non-goals (recorded)

- Radius/spatial query: userland composition (`filterA` with an adaptive
  predicate over `center`/`radius` avals) once the swap machinery landed; a
  grid-indexed collection is not warranted (small N, churn not asymptotics).
- `AList.collect`: deferred by decision (gap sheet).
- `tryFind` memoization: rejected for this PR (see §2.1).
