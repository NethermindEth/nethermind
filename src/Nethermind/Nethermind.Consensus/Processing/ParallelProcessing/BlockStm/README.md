# Block-STM Parallel Transaction Execution

This directory contains a Block-STM (Software Transactional Memory) parallel transaction
executor for Nethermind, derived from the work in PR #9803 and follow-up branch
`feature/block-stm-integration-codex`.

## Status: WORK IN PROGRESS — compiles, not yet wired

This is a rebase port of the codex branch on top of current master, with four critical
correctness bugs fixed and a refactored `ParallelStateKey` that also closes audit bug B2.
The full solution (including `Nethermind.Runner`) builds clean against current master.

What's still missing before the path can actually execute blocks:

- **`IFeeRecorder` integration** in `TransactionProcessor.PayFees`. Without this, fees in
  parallel mode would be written directly to the coinbase address, creating a chain-wide
  write-after-write dependency that defeats every parallel benefit.
- **Optimism / Taiko `PayFees`** must also route through `IFeeRecorder` for L1FeeReceiver /
  OperatorFeeRecipient / treasury. Otherwise running block-STM on OP Stack or Taiko
  fully serializes via those addresses.
- **DI wiring** — a `BlockStmModule` that registers `BlockStmTransactionsExecutor` as
  `IBlockProcessor.IBlockTransactionsExecutor` only when `IBlocksConfig.BlockStmEnabled`
  is true and master's BAL parallel path is inactive.
- **Code-bytes propagation across the parallel boundary** (audit issue). New contracts
  created by an in-tx CREATE have their code in the per-tx scope's CodeDb which is
  disposed before `PushChanges` runs. The `ApplyAccountUpdate` helper currently leaves
  this as a TODO — see `BlockStmTransactionsExecutor.cs:ApplyAccountUpdate`.
- Remaining audit items (system-tx fence, 7702 authority recovery, BlockReceiptsTracer.Index,
  hardcoded concurrency, static `Metrics`).

## Where it fits

Master already ships a parallel transaction executor based on EIP-7928 Block Access Lists
(`BlockProcessor.ParallelBlockValidationTransactionsExecutor`). When BALs are present the
BAL executor is preferable (it pre-declares conflict sets and avoids re-execution). Block-STM
is the fallback parallel strategy for blocks **without** a BAL, and a candidate strategy for
block production (where there is no suggested BAL to validate against).

The intended dispatch:

```
if balManager.Enabled && balManager.ParallelExecutionEnabled  →  BAL parallel (master)
elif IBlocksConfig.BlockStmEnabled                            →  Block-STM (this directory)
else                                                           →  sequential
```

## Files

| File | Purpose |
|---|---|
| `BlockStmTransactionsExecutor.cs` | Orchestrates one block: builds memory + scheduler + accumulator, runs the parallel runner, commits results. Implements `IBlockProcessor.IBlockTransactionsExecutor`. (Renamed from `ParallelBlockValidationTransactionsExecutor` to avoid collision with master's BAL executor.) |
| `ParallelScheduler.cs` | Block-STM scheduler from the Aptos paper (Algorithm 4) — tx status machine, dependency tracking, validation lowering. |
| `ParallelRunner.cs` | Worker-task pool that calls `NextTask` / executes / validates / records. |
| `MultiVersionMemory.cs` | MVCC store: per-tx versioned read/write sets, validation, estimates. |
| `MultiVersionMemoryScopeProvider.cs` | `IWorldStateScopeProvider` decorator that routes EVM state reads/writes through the MVCC store. |
| `ParallelEnvFactory.cs` | Builds per-tx-attempt `ITransactionProcessor` scopes layered above the prewarmer. |
| `ParallelFeeRecorder.cs` + `FeeAccumulator.cs` | Side-channel fee tracking that breaks the universal write-after-write dependency on the coinbase address. |
| `ParallelStateKey.cs` | Discriminated union of MVCC key kinds: `Storage` / `FeeGasBeneficiary` / `FeeCollector`. |
| `TxVersion.cs` | `(TxIndex, Incarnation)` execution identity. (Renamed from `Version` to avoid collision with `System.Version`.) |
| `AbortParallelExecutionException.cs` | Internal signal thrown by the scope provider on `ReadError`. |
| `Metrics.cs`, `ParallelBlockMetricsCollector.cs` | Per-block parallelization counters. |
| `ParallelTrace.cs` | Generic-flag (compile-time-elided) tracing helper. |

## Correctness bugs fixed in this commit

Discovered during a deep audit and fixed here. Each is independently testable against the
sequential path via the existing `DualBlockchain` test fixture.

### 1. `MultiVersionMemory.Record.wroteNewLocation` missed removals and re-writes

The original implementation only signalled "write-set changed" when a *new* location was
added to a tx's published write-set. It did not fire when:
- a previously-written location was *removed* from the new incarnation, or
- a location was re-written (the stored `TxVersion` advances even with identical bytes).

`ParallelScheduler.FinishExecution` uses this signal to decide whether to lower
`_validationIndex`. If `false`, already-validated higher txs were not re-validated against
the new incarnation; they could end up committed against a stale snapshot. Fix: re-defined as
`writeSetChanged` and fired for any of the three cases above.

### 2. Dependency-set double-return to the object pool

`ResumeDependencies` could be entered concurrently by `AbortExecution`'s post-add
race-detect path and by `FinishExecution`. Both observed the same non-empty `HashSet<int>`
and both returned it to the pool, leading to set aliasing and corrupt dependency state.
Fix: `ResumeDependencies` now atomically claims the slot via `Interlocked.Exchange` — only
the winner drains and returns. `AbortExecution` no longer calls `ResumeDependencies` for the
race case; it short-circuits to "re-execute now" instead, and serializes its add against the
drain via the dependency set lock. `FinishExecution` marks `Executed` *before* claiming, so
late-arriving aborts observe `Executed` under the lock and abandon their add.

### 3. `SetReady` torn `(Status, Incarnation)` write

Previously: `Interlocked.Exchange(Status, Ready)` followed by a plain `Incarnation++`. A
worker could observe `Ready` before the increment was visible, claim the tx via CAS, execute
under the *old* incarnation, then become silently un-abortable when the live state's
`Incarnation` no longer matched. x86 TSO mostly masked this; ARM did not. Fix: packed
`(Status, Incarnation)` CAS loop, same pattern as `TryValidationAbort`.

### 4. `accountDeleted` false-positive in `PushChanges`

`accountUpdates?.TryGetValue(address, out Account? _) ?? false` returns `true` for any
account update at the address, including a non-null re-creation. The flag is used to gate a
`ClearStorage` call. Under certain tx orderings (e.g. tx0 SSTORE → tx1 selfdestruct →
tx2 re-create + balance transfer) the storage of tx0's writes could be wiped because tx2's
non-null account update was being misread as a delete. Fix: test the value — only treat as
delete when the entry is *present and null*.

## Adaptations made for master

The port lives in a new `ParallelProcessing.BlockStm` sub-namespace to coexist with master's
BAL parallel executor. Master moved enough that the following interface adaptations were
required (all applied in the compile-fix commit):

- `IWorldState.SetAccount` was removed. `PushChanges` now uses the typed primitives
  (`DeleteAccount` for null; `CreateAccountIfNotExists` + `AddToBalance` /
  `SubtractFromBalance` + `SetNonce` for non-null) via a new `ApplyAccountUpdate` helper.
- `AddToBalanceAndCreateIfNotExists` now requires `out UInt256 oldBalance`.
- `ThrowInvalidTransactionException` moved to
  `BlockProcessor.BlockValidationTransactionsExecutor.ThrowInvalidTransactionException`.
- `BlockReceiptsTracer.AccumulateBlockBloom` was removed; the inline `AccumulateBlockBloom`
  helper at the bottom of `BlockStmTransactionsExecutor.cs` mirrors master's BAL pattern.
- `BlockReceiptsTracer` now requires `bool parallel` ctor; the pool uses a typed
  `PooledObjectPolicy`.
- `PrewarmerScopeProvider` ctor gained `ILogManager`; threaded through `ParallelEnvFactory`.
- `worldStateManager.CreateResettableWorldState()` now returns `IWorldStateScopeProvider`
  directly.
- `IStorageTree.HintGet` renamed to `HintSet`; `IWorldStateScopeProvider.IScope` gained
  `HintBal` (forwarded to base).
- IDE0028 (`new()` → `[]`) and IDE0290 (primary ctor) style errors fixed.

## Remaining known issues from the deep review

Ordered by severity. The four critical bugs flagged at the top of this README and the
selfdestruct / code-propagation issues have been fixed; what's left:

- **EIP-8037 + SELFDESTRUCT-of-coinbase** (critical when EIP-8037 activates on mainnet).
  Under EIP-8037 the fee is always paid to the coinbase then burned in the destroy-list
  finalization. The current FeeRecorder accumulates the fee into `_gasBeneficiaryFees`
  even when the destroying tx targets the coinbase, so PushChanges double-credits the
  destroying tx's own fee. Fix: when `coinbase ∈ substate.DestroyList && spec.IsEip8037Enabled`,
  bypass the recorder for that tx and write the burn directly to the per-tx writeSet.
  Reproducer: contract-as-coinbase that calls SELFDESTRUCT(self) under Cancun/Prague.
- **CREATE2-into-storage-only address pre-EIP-7610** (latent on chains that haven't enabled
  EIP-7610). `PushChanges` skips a redundant `ClearStorage` when `storageTouched.Contains(addr)`,
  which is the right behavior for SELFDESTRUCT but wrong for a CREATE2 collision that wipes
  pre-existing storage. Need to distinguish "clear from SELFDESTRUCT" from "clear from
  init-time wipe" at the writeSet level.
- **Dep-set ownership during AbortExecution race** (latent fragility). `AbortExecution`
  locks the dep-set after `GetDependencySet` returns it, but `FinishExecution` can claim
  the set via `Interlocked.Exchange` and return it to the pool. Today the re-check inside
  the lock makes the path safe (Abort returns false and modifies nothing), but the lock no
  longer pins the set to the original blocker. Future code added between the re-check and
  the if/else would break — re-confirm the slot identity inside the lock.
- **Autofac child-scope leak on per-tx env**. `ParallelEnvFactory.Create` builds a child
  scope per execution attempt and relies on `AutoReadOnlyTxProcessingEnv.Dispose` cleaning
  it. Verify that the base class actually disposes the lifetime scope — high-abort blocks
  could leak per-tx scopes otherwise.
- **`MarkCommitted` + `Record` fire even when `Execute` failed** (defensive). If the
  TransactionProcessor returns `!result`, we skip `scope.WorldState.Commit` but still run
  `EnsureFeeKeys`, `MarkCommitted`, and `multiVersionMemory.Record`. Downstream txs that
  read the fee keys treat them as committed; they're cleaned up only when
  `ThrowIfInvalidResults` fails the whole block. Skip these on failure for clarity.
- **`blockCodeWrites` accumulates code from aborted incarnations**. Append-only, by design
  benign (content-addressed; collisions are idempotent), but means rolled-back code is
  technically reachable until end-of-block. Filter to codeHashes referenced by the final
  writeSet before InsertCode, or attach code-writes to per-tx incarnation and merge only
  on successful Record.
- **`FindNonceDependencies` only chains the most recent predecessor**. STM corrects this
  via re-execution, but multi-sender / multi-7702-auth blocks pay O(n²) reschedules.
  Either chain all predecessors or rely entirely on STM (current is half-measure).
- **`MultiVersionMemoryScope.Get` of a fee recipient triggers `AddFeeReadDependencies`
  for every prior tx** even on prewarmer touches. Should fire only on real BALANCE/SLOAD
  reads — otherwise blocks where any tx reads coinbase produce O(n²) re-execution attempts.

For the broader simplifications (drop `<TLogger>` and `<TLocation,TData>` generics, split
oversized files, replace TrackingCodeDb with a concurrent dict at executor level) see the
review comments threaded into the PR — they're deletion-only changes worth ~150 net lines
once we're ready to do another pass.
