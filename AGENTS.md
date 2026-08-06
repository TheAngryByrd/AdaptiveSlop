# AdaptiveSlop — Agent Guide

Guidance for AI agents working in this repo. For API/usage documentation see @README.md.

## Imperatives

1. **NEVER PUSH WITHOUT PERMISSION.** Always ask before pushing to the remote.
2. **NEVER FORCE PUSH.** Tell the user they have to force push instead of you.
3. **Always run `dotnet fantomas .` before committing code.** Format all F# files before staging.
4. Pull requests made with the `gh` command should use a markdown file as the PR body, not inline escaped markdown strings.
5. Only report to me in ASD-STE100 Simplified Technical English.

Suggestion:
sub agents running in parallel should not build or test as they may trip each other's results. Orchestrate, send work in parallel and verify/fix in the main agent when subagents are done.

## Commands

```bash
dotnet build AdaptiveSlop.sln
dotnet test tests/AdaptiveSlop.Tests/AdaptiveSlop.Tests.fsproj
dotnet test tests/AdaptiveSlop.Tests/AdaptiveSlop.Tests.fsproj -c Release   # before release claims
cd benchmarks/AdaptiveSlop.Benchmarks && dotnet run -c Release -- --filter "*" --job short
```

## Layout

- `src/AdaptiveSlop.Core/Library.fs` — scalar core: `ChangeableValue`, `AdaptiveNode`
  (generic), `Map3/4/N/ReduceNode`, `Transaction`, `DependencyCollector`, `AVal`/`CVal`
- `src/AdaptiveSlop.Core/Collections/` — the collection layer, in compile order:
  `Shared.fs` (delta buffers, `Collections` internal module), `Changeable.fs`
  (`ChangeableSet/Map/List`), `ElementSetNode.fs`, `ElementMapNode.fs`,
  `ElementListNode.fs` (the per-element mapA/filterA/chooseA nodes), `SetNodes.fs`,
  `MapNodes.fs`, `ListNodes.fs`, `Reductions.fs`, `ExternalNodes.fs`
  (ofExternal/custom), `ObserveNodes.fs`, `Api.fs` (the public modules)
- `tests/AdaptiveSlop.Tests/Tests.fs` — xUnit unit tests
- `tests/AdaptiveSlop.Tests/Properties.fs` — the FsCheck property suite (reference
  models, algebraic laws, incremental laws, scenario generators)
- `benchmarks/AdaptiveSlop.Benchmarks/` — BenchmarkDotNet; run before/after any perf change;
  raw run output goes to `benchmark-results/` (gitignored), summaries to `docs/BENCHMARKS.md`
- `src/AdaptiveSlop.{Demo,Tui,Mibo}` — consumers of the core
- `docs/` — design research and planning documents, when present

## Invariants

Hold these in all core changes; violating any of them breaks the design:

1. **Pull-lazy only.** Writes mark/notify; nothing recomputes on write. Recomputation
   happens exclusively on read, per dirty node, at most once per change.
2. **Recompute = re-read all dependencies.** Never cache the dep set without re-reading;
   dynamic dependencies (`bind`) and edge self-healing depend on it.
3. **No evaluation during marking.** Notifications are deferred until marking/transaction
   completes. Do not add level/topological-ordering machinery; this rule replaces it.
4. **Owner-thread confinement.** Core code must not add locks, `Interlocked`, or
   `[<ThreadStatic>]`; shared state lives on a graph context object. Cross-thread
   interaction goes through explicit handoff (post/drain), never shared mutable access.
5. **Zero library-side allocation on hot paths** (clean read, mark, static recompute,
   delta delivery). Prove allocation claims with `GC.GetAllocatedBytesForCurrentThread`.
6. **Transactions defer application**: writes inside `Transaction.run` apply at commit;
   reads inside see pre-transaction values.

If a change requires breaking an invariant, that is a design change — get explicit sign-off
from the user before proceeding.

## Working in this repo

- **Verify behavior by reading the code, not prose.** Comments and docs may describe
  intended rather than actual behavior; when they disagree, the code wins — and fix the
  comment.
- Structural changes to edges, observation, threading, or node types are architectural
  work: discuss the approach with the user before implementing.
- XML doc comments on all public types/functions, with `<example>` blocks.
- `[<InlineIfLambda>]` on function parameters in hot paths.
- No new dependencies without asking; the core currently depends only on the BCL.
- Performance claims require a BenchmarkDotNet before/after; allocation claims require
  measurement evidence.
- **Test at the public API level only.** Never test node internals. Property tests use the
  DSL (module functions: `CVal.set`, `CSet.add`, `CList.insertAt`, `CMap.addOrUpdate`,
  `AVal.map`), never instance methods, and live in `Properties.fs` (100 iterations;
  the op models must mirror the real semantics, not the library's implementation).
