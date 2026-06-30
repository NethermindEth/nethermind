# R1 — "Self-BAL": optimistic parallel execution on non-BAL mainnet (design)

**Status:** design / prototype scaffold. Not production. Flagship of the perf-redesign series.

## Thesis

Nethermind's parallel transaction executor is **gated on a block carrying an EIP-7928 Block Access
List (BAL)** — without one (i.e. today's mainnet), it falls back to sequential execution
(`BlockProcessor.ParallelBlockValidationTransactionsExecutor.cs:49-59`). Meanwhile the speculative
**pre-warmer already executes every transaction** to warm caches and then throws the results away
(`BlockCachePreWarmer.WarmupSingleTransaction` → `scope.TransactionProcessor.Warmup(...)`,
`BlockCachePreWarmer.cs:276-297`). R1 turns that wasted speculative execution into a **locally derived
BAL ("self-BAL")** and feeds it into the existing BAL parallel-apply + incremental-validation path —
giving Block-STM-style parallel execution on **unmodified mainnet blocks**, years before EIP-7928 ships.

Hardware-agnostic: the win scales with core count, so it is ideal for Nethermind's 64-core AMD EPYC
infra (unlike R2, which needs AVX-512). 

## Components that already exist (reuse, don't rebuild)

| Capability | Where | Reuse as |
|---|---|---|
| Speculative per-tx execution (parallel across senders) | `BlockCachePreWarmer.WarmupTransactions/WarmupSingleTransaction` | The self-BAL **generation** pass |
| BAL **generation** during execution | `BlockAccessListManager` `GeneratedBlockAccessList` / `ForceConstructGeneratedBlockAccessList` (`:159`) | Record the access-set + post-state into a BAL |
| BAL-driven **parallel execution** + ordered apply | `ParallelBlockValidationTransactionsExecutor.ProcessTransactionsParallel` + `BlockAccessListManager.ApplyStateChanges` | The validation pass |
| **Incremental validation** (actual vs declared) | `BlockAccessListManager.IncrementalValidation` | Detect a self-BAL miss → fall back |
| Conflict-free scheduling, straggler sort | `BuildTxExecutionOrder` | unchanged |

## Design: two parallel passes ("generate, then validate")

```
NewPayload(block without BAL)
  │
  ├─ Pass A  (parallel, speculative)         ← promoted pre-warmer
  │    execute every tx against parent state on worker scopes,
  │    RECORD each tx's reads/writes + post-state  ──►  selfBal : GeneratedBlockAccessList
  │
  ├─ Pass B  (parallel, validating)          ← existing BAL executor, fed selfBal
  │    schedule conflict-free from selfBal, execute in parallel,
  │    ApplyStateChanges(selfBal), IncrementalValidation(actual == selfBal?)
  │       │
  │       ├─ all consistent ──► commit (state root as usual)
  │       └─ any tx diverged (read-after-write the speculative pass missed)
  │             └─ re-execute only the diverged suffix sequentially (correctness floor)
  │
  └─ state root + receipts  (unchanged)
```

Why two passes rather than one optimistic STM pass: Nethermind's engine is **declare-then-validate**
(BAL-shaped), not abort-and-retry. Pass A produces the declaration; Pass B is the *existing* fast path.
Both passes are parallel, so wall-clock ≈ 2 × (block / N cores) in the happy path — still far below
sequential for N ≫ 2 and compute-heavy blocks. (A later optimization can fuse A into the prewarmer that
already runs, making A nearly free.)

## New pieces needed

1. **Access-set recorder** in the speculative pass — capture, per tx: accounts read/written, storage
   slots read/written, balance/nonce/code deltas. This is exactly a `GeneratedBlockAccessList` entry;
   the recorder wraps the worker's `IWorldState` (or reuses the existing generation hook) during
   `Warmup`/execute.
2. **Self-BAL assembly** — merge per-tx recordings into one block-level `GeneratedBlockAccessList`
   ordered by tx index, with the same semantics the validator expects.
3. **Wiring switch** — when `!balManager.Enabled` (no producer BAL) but self-BAL is on, run Pass A then
   drive the existing parallel path with the self-BAL instead of falling back to sequential
   (`ParallelBlockValidationTransactionsExecutor.cs:49-59`).
4. **Divergence fallback** — if `IncrementalValidation` flags a mismatch (the speculative pass observed
   a stale read because a prior tx in the *real* order wrote that slot), re-execute from the first
   diverged tx sequentially. Guarantees bit-identical consensus output.
5. **Config flag** (default off) — `Blocks.SelfBalParallelExecution` (units/default documented in XML).

## Correctness / determinism requirements (non-negotiable — consensus)

- Final state root, receipts, gas, and logs **must be bit-identical** to canonical sequential execution.
  The divergence fallback (#4) is what guarantees this; the parallel passes are an *optimization* whose
  result is always validated before commit.
- Speculative execution must be **side-effect-free on shared state** (workers use throwaway scopes, as
  the prewarmer already does) — only the recorded self-BAL crosses the boundary.
- Self-BAL is **advisory**, never trusted: a wrong/incomplete self-BAL can only cause a fallback
  (slower), never a wrong block.
- Determinism of the *schedule* (already handled by `BuildTxExecutionOrder`) so traces/telemetry match.

## Risks & mitigations

| Risk | Mitigation |
|---|---|
| Speculative reads stale across dependent txs (sender already grouped, but cross-sender deps exist) | IncrementalValidation + sequential-suffix fallback; measure fallback rate |
| 2× execution cost dominates on low-core / low-conflict blocks | Gate on core count + tx count; fuse Pass A into the existing prewarmer so it's ~free |
| Re-using GeneratedBlockAccessList outside block production | Add tests pinning generated-BAL equivalence vs a real BAL on the same block |
| Memory: per-tx recordings | Pool/arena the recordings; bounded by block tx count |
| Contention on shared caches as N rises | **R6 (striped counters) already lands this** — prerequisite |

## Staged implementation plan (across sessions)

1. **Scaffold** the access-set recorder interface + a unit test that records a single tx's read/write set
   and asserts it matches a known BAL entry. *(buildable now)*
2. **Generate** a self-BAL for a whole block in a test harness; assert it equals the producer BAL that
   `GeneratedBlockAccessList` builds during block production for the same block.
3. **Wire** Pass A→B behind the config flag; run the EF/consensus test suite (must stay green —
   bit-identical).
4. **Fallback** path + a test that forces a divergence (a crafted cross-tx RAW) and asserts correct
   sequential recovery + a metric increment.
5. **Benchmark** end-to-end on `run-expb-reproducible-benchmarks.yml` (realblocks) vs master on the
   64-core EPYC — the hardware-agnostic win shows here.

## What this session delivered for R1

Feasibility confirmed + this design, grounded in the code. Next concrete step: scaffold piece (1) — the
access-set recorder + its unit test — which is buildable/testable locally without AVX-512 or CI.
