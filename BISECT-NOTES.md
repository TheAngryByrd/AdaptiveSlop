# Bisect notes — choose2 / two-source zero-allocation failures (7.3)

Session handoff. Active branch: `feature/collection-api` @ `0c0dd90` + uncommitted
edits (Api.fs, MapNodes.fs, SetNodes.fs, Shared.fs, Tests.fs). Do not reset or revert
anything.

## Status

- 140 tests, 138 pass. The 2 failures are the zero-allocation assertions:
  - `choose2 node drains allocate zero in steady state` (Tests.fs ~line 2728)
  - `two-source node drains allocate zero in steady state`
- Tests.fs contains the temporary probe test `choose2 probe split` (fails by
  design, prints the probe numbers). It now also prints `calib`, `pF`, `pD`,
  `pE` and verifies the writes landed. Remove it and restore the real tests
  (4 writes per iteration + final `Assert.Equal(0L, allocated)`) once the cause
  is fixed.
- Uncommitted changes this session (compile clean, behavior tests green):
  - `applyChoose2Out` (Shared.fs ~line 1184) rewritten: the
    `match newOut with | ValueSome v when ...` replaced by explicit
    `if newOut.IsSome then let v = newOut.Value` form. Semantically
    identical, MEASURED IDENTICAL (see pB/pD/pE below — no effect).
    Keep or revert freely.
  - Probe test extended: calibration allocation, `pF` (raw source), `pD`
    (choose2 changed), `pE` (choose2 elided), write-landing check.

## Measured facts (xUnit compiled, Debug, net8 — ground truth)

Single run, one probe test, all phases in order. `calib` = a known
`Array.zeroCreate<int> 1000` allocation inside the same measurement style:
4024 B, so the counter works. The writes were verified to land (maxVal >= 10000
after P1). Reproduces exactly across runs and across sessions.

| Probe | Setup | Measurement |
|---|---|---|
| P1 | 100 writes, no drain | 1864 B |
| P3 | 1 write + 100 reads | 32 B |
| pA | 100w+1r, choose2 stub `fun _ _ _ -> ValueNone`, Out stays empty | 1864 B |
| pB | 100w+1r, `AMap.union` (right-biased wrapper), output elided | 7136 B |
| pD | 100w+1r, choose2 `fun _ lv _ -> lv`, output changes | 9920 B |
| pE | 100w+1r, choose2 `fun _ _ rv -> rv`, output elided | 8056 B |
| pF | 100w, raw CMap, NO derived nodes at all | 0 B |
| OnSideDeltas no-op | ALL of P1/P3/pA/pB/pD/pE/pF | 0 B |

### Fact 1 — the delivery path is already zero-allocation

- pF = 0 B: the full source write path (`CMap.addOrUpdate` -> `ClaimOwner` ->
  `ApplyAndFlush` -> `PushAndMark` -> `pushMapDelta` -> `MarkFrom` ->
  `DeliverNotifications`) allocates nothing in steady state.
- No-opping the `Choose2MapNode.OnSideDeltas` body (removing
  `journalAppendMap` + `state.Version++` + `MarkFrom`) makes EVERY probe
  measure 0 B, with writes verified to land.
- Therefore the previous session's "~16 B/write delivery" was a
  mis-attribution. P1 = pA = 1864 is: fresh-node journal growth
  (JournalL.Sets 16 -> 32 -> 64 -> 128 = 1792 B for 100 single-entry appends
  into a never-drained journal) + ~72 B total noise. The earlier note
  "journal growth ~256 B" is wrong (256 B is only the 16 -> 32 step).
- The source's own `outDelta` never grows in steady state: `PushAndMark`
  calls `outDelta.Clear()` after every single write, so the count is 0 after
  each write and the 16-slot buffer is never exceeded.

### Fact 2 — the real cost is in the drain, and only when `Out.TryGetValue` HITS

Drain-only cost = probe minus the 1864 B baseline (delivery + fresh-journal
growth). pA is the control: same drain loop, but `Out` is empty so
`applyChoose2Out`'s `Out.TryGetValue(k, &old)` misses and `newOut` is
`ValueNone`.

| Probe | Drain total | B/entry | `Out.TryGetValue` | elision check |
|---|---|---|---|---|
| pA | 0 B | 0 | MISS | not evaluated |
| pB | 5272 B | 52.7 | HIT | evaluated, equal |
| pE | 6192 B | 61.9 | HIT | evaluated, equal |
| pD | 8056 B | 80.6 | HIT | evaluated, different |

pD includes +1792 B of fresh OutDelta growth (16 -> 128 for 100 entries),
which accounts for ~17.9 B/entry of its 80.6; pD and pE share a ~62 B/entry
base. pB/pE (elided) push NOTHING downstream (no out delta, no count-node
delivery) yet still cost 52-62 B/entry.

### Fact 3 — the `when`-guard hypothesis is REFUTED

Rewriting `applyChoose2Out` from
`match newOut with | ValueSome v when EqualityComparer<'V3>.Default.Equals(old, v) -> ()`
to explicit `if newOut.IsSome then let v = newOut.Value` + `if Equals then ()`:
pB=7136, pD=9920, pE=8056 — byte-for-byte identical. The match structure is
not the cost.

### Fact 4 — the 24 B `TryGetValue` match pattern is not present

Audited every `TryGetValue` call site in Collections/*.fs and Library.fs: all
use the explicit out-param form
(`let mutable v = Unchecked.defaultof<_>; if dict.TryGetValue(k, &v) then`).
The `match dict.TryGetValue key with | true, v ->` pattern does not occur in
any hot path.

## What differs between the 0 B/entry case and the 52-80 B/entry cases

All four probes share: side journal loop, `Sides.TryGetValue` hit,
`Sides[k] <- ...`, the mapping invocation, `applyChoose2Out` entry, `IsSome`
test. pA (0 B) differs from pD/pE only in:

1. `Out.TryGetValue(k, &old)` — MISS vs HIT (a hit returns the stored value).
2. mapping result — `ValueNone` vs `ValueSome` (pD/pE bind `v`).
3. `EqualityComparer<'V3>.Default.Equals(old, v)` — evaluated only in pD/pE
   (both, regardless of branch taken).

One or more of these three is the ~62 B/entry. `TryGetValue` itself cannot
allocate on a hit (pure dictionary probing), which points at (2) the
`ValueSome` value flow or (3) the generic `EqualityComparer<'V3>.Default`
evaluation inside the generic function.

## Next bisect steps (untested, in order)

1. In `applyChoose2Out`, replace `EqualityComparer<'V3>.Default.Equals(old, v)`
   with `old = v` (F# generic equality compiles to a different call shape).
   - If pB/pE drop to ~0 extra: the generic comparer evaluation allocates.
   - If unchanged: revert and go to step 2.
2. Remove the elision entirely (always emit the delta, semantics broken, bisect
   only): if pB/pE drop to pD's level, the comparison is the cost. If pD/pE
   both stay ~62 B/entry, the cost is the `ValueSome` flow itself (IsSome/Value
   or the mapping result construction at the call site in
   `processChoose2Side`).
3. If step 2 keeps the cost: hardcode `let v = newOut.Value` into a local at
   the `processChoose2Side` call site and pass the plain value to
   `applyChoose2Out` — this splits the `ValueSome` construction from the
   function boundary.
4. If still stuck, disassemble `applyChoose2Out` (no decompiler available in
   the environment; use `dotnet fsi` + `System.Reflection.Metadata` or install
   `ilspycmd`).
5. After the cause is found and fixed: re-measure the two-source SET test
   (24 B/iter with 2 count reads per iter — same class of problem, retest
   with the same fix) and the remaining 7.3 closeout list below.

## Remaining 7.3 closeout (after the two allocations are fixed)

1. Remove the `choose2 probe split` test; restore the real failing tests
   (4 writes per iteration, `Assert.Equal(0L, allocated)`).
2. Run the full suite Debug + Release.
3. `dotnet fantomas .` and commit on `feature/collection-api`.
4. Update docs/PLAN.md Section 7.3 with the recorded FDA deviations:
   static `unionMany` (FDA's is dynamic `aset<aset<'A>>`, needs 7.4 collect);
   `ofHashMap` -> `ofMap` (no HashMap type here); `custom`/`ofReader` are
   pull-based poll nodes (signatures recorded in Api.fs); `intersect` returns
   a struct pair (collapses FDA intersect/intersectV); `choose2` is
   voption-only (FDA's choose2V); `ofASetIgnoreDuplicates` is last-wins.
5. Then 7.4 (bind/collect, dynamic unionMany), 7.5 (hardening, reentrant
   write during drain investigation, benchmarks).

## Working rules (do not forget)

- Use the `edit` tool for file changes. NEVER sed/python/awk for edits.
- `[<InlineIfLambda>]` stays on stored lambdas (user directive).
- Report in ASD-STE100 Simplified Technical English.
- Benchmarks: `dotnet run -c Release` in benchmarks/AdaptiveSlop.Benchmarks.
