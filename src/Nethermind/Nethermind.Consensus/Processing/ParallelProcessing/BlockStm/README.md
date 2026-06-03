# Block-STM Parallel Transaction Execution

This directory contains a Block-STM (Software Transactional Memory) parallel transaction
executor for Nethermind, derived from the work in PR #9803 and follow-up branch
`feature/block-stm-integration-codex`.

## Status: WORK IN PROGRESS — does not yet compile against current master

This is a rebase port of the codex branch on top of current master, with four critical
correctness bugs fixed in this initial commit. Master moved substantially since the codex
branch (BAL parallel executor, world-state pipeline v1, prewarmer rewrite); finishing the
port requires further work outside this directory — see "Outstanding work" below.

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

## Outstanding work to make this build and run

The port lives in a new `ParallelProcessing.BlockStm` sub-namespace to coexist with master's
BAL parallel executor. To finish:

1. **`PrewarmerScopeProvider` ctor changed on master.** It now takes `ILogManager` and a
   `IWorldStateScopeProvider` rather than `IWorldState`. `ParallelEnvFactory.Create` must be
   updated.

2. **`worldStateManager.CreateResettableWorldState()` return type may have changed.**
   Confirm whether it still produces an `IWorldStateScopeProvider`-equivalent.

3. **`IFeeRecorder` integration in `TransactionProcessor`.** The codex branch made the
   following pattern across `TransactionProcessor`, `SystemTransactionProcessor`,
   `OptimismTransactionProcessor`, `TaikoTransactionProcessor`:
   ```csharp
   if (FeeRecorder is not null) FeeRecorder.RecordFee(...); else WorldState.AddToBalance(...);
   ```
   `IFeeRecorder.cs` is ported here but the integration in `TransactionProcessor.PayFees`
   is not yet applied to master. Apply it carefully against master's pipeline-v1 reshape.

4. **Optimism and Taiko `PayFees` must also route through `IFeeRecorder`.** The codex
   branch only wired `TransactionProcessor`'s base path; Optimism's `L1FeeReceiver` /
   `OperatorFeeRecipient` and Taiko's treasury credit still write directly to `WorldState`,
   eliminating any parallel benefit on those L2s.

5. **Add DI wiring (`BlockStmModule`).** A small Autofac module that registers the executor
   as `IBlockProcessor.IBlockTransactionsExecutor` only when
   `IBlocksConfig.BlockStmEnabled == true` (new config) and master's BAL parallel path is
   not active. Replace the hard-coded `4` worker count with `IBlocksConfig.BlockStmConcurrency`.

6. **Tests:**
   - Port `ParallelBlockValidationTransactionsExecutorTests.cs` and `ParallelRunnerTests.cs`,
     adjusting to master's `DualBlockchain` shape.
   - Add bug-1 regression test (MVMemory.Record with removed/re-written keys).
   - Add bug-4 regression test (`SSTORE → SELFDESTRUCT → re-create + balance transfer`
     across three txs).
   - Add Cancun-spec EIP-6780 selfdestruct test.
   - Add a `beneficiary == self` selfdestruct test.

## Remaining known issues (not yet fixed in this commit)

From the audit, ordered by severity:

- **Storage `clearKey` sometimes omitted from read-set.** `MultiVersionMemoryStorageTree.GetStorageValue`
  only adds the clear-key dependency when `valueStatus == Ok || (NotFound && !baseIsZero)`.
  The omitted branch (`NotFound && baseIsZero && clearStatus == NotFound`) can miss a later
  selfdestruct invalidation. Always-add fix is one line.
- **`SelfDestructMonit` aliases a real storage slot.** The clear marker is keyed at
  `StorageCell(address, Keccak.EmptyTreeHash)`. A contract that SSTOREs at exactly that
  index collides. Fix: add `ParallelStateKeyKind.StorageClear` keyed by address only.
- **`Get(Address)` does not consult the clear-key.** Pairs OK with selfdestruct (which
  always writes both account-null + clear), but breaks for CREATE revival paths that clear
  storage without nulling the account.
- **Optimism / Taiko `PayFees` bypasses `IFeeRecorder`.** See item 4 above.
- **System txs and Optimism deposit txs flow through the scheduler with no fence.** They
  assume sequential pre-hash bookkeeping; add an `if (tx.IsSystem) sequential` fork.
- **EIP-7702 nonce dependency uses `AuthorizationTuple.Authority` before signature
  recovery.** `FindNonceDependencies` runs pre-execution so `Authority` may be null;
  cross-tx 7702 deps are silently missed. Either recover signatures upfront or fall back to
  a conservative all-pairs dependency for blocks containing 7702 txs.
- **`BlockReceiptsTracer.Index` is always 0 under parallel.** Per-tx tracers reset
  `CurrentIndex` and never see the real index. Set it explicitly after harvesting.
- **Code reads bypass MVCC.** `MultiVersionMemoryScopeProvider.CodeDb => baseScope.CodeDb`
  is fine for committed code (content-addressed) but breaks for code inserted earlier in the
  same block: the inserts live in the per-tx scope's `_codeBatch` which is disposed before
  PushChanges runs.
- **Hardcoded concurrency = 4.** `BlockStmTransactionsExecutor.cs:54`. Add a config knob.
- **Static `Metrics` state.** Forces tests `[NonParallelizable]`. Move to scoped DI.

See the audit notes in the parent PR thread for the full list with file:line references and
reproduction scenarios.
