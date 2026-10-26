# Issue (local): gossiped frame-tx validation is not paused or preempted during block building

Status: **draft PR #14085** (option 1, `feat/frame-tx-preempt-during-block-building`, `bc4b5801`), opened 2026-09-30 to evaluate with tests and Claude Code Review. Full template body: `~/frame-tx-campaign-keep/drafts/pr-preempt-during-building-body.md` (hook allows only a short body).
Code references: master `c3a29a59` (2026-09-30).

## Summary

Nethermind protects **block import** from gossiped frame-tx validation twice. It protects **block building** zero times. Signature recovery is protected in neither case. The producer arm binds every campaign `R_max` from 235,800 up.

## The two import protections

1. **Gossip scheduler pause** (pre-existing).
   - `BackgroundTaskScheduler` subscribes to `BlocksProcessing` / `BranchProcessingCompleted` of the **main** branch processor only: `Nethermind.Init/Modules/BlockProcessingModule.cs:121-122` (`mainProcessingContext.BranchProcessor`).
   - While a main-chain branch runs, new gossip tasks wait; the running task gets a cancelled token, but `SubmitTx` takes none (`Nethermind.Consensus/Scheduler/BackgroundTaskScheduler.cs`, `Eth62ProtocolHandler.HandleSlow`).
2. **Preemption, PR #13994** (merged 2026-09-29, Daniil).
   - Signal: `IChainHeadInfoProvider.IsProcessingBlock` → `BlockTree.IsProcessingBlock` (`Nethermind.Blockchain/ChainHeadInfoProvider.cs:74`, `BlockTree.cs:2025`).
   - Passed as `preempt` for gossip only: `Nethermind.TxPool/Filters/FrameTxSimulationFilter.cs:24, 40-45` (local submissions get `null`).
   - Checked before start: `Nethermind.Consensus/Processing/FrameTxPrefixSimulator.cs:81-84` → `RejectYielded`, counted in `FrameTxSimulationsPreempted`.
   - Checked while running: `Nethermind.Evm/Tracing/FrameTxValidationTracer.cs:62`, polled by the interpreter every 1,024 opcodes → stop, `FrameTxPrefixSimulator.cs:169-173`.
   - Effect: a block waits at most one poll interval plus scheduling, independent of `MAX_VERIFY_GAS` (PR text).

## Why neither covers block building

- `IsProcessingBlock` is set only in the main import loop: `Nethermind.Consensus/Processing/BlockchainProcessor.cs:584` (setter), `:603` (true), `:615` (false), inside `RunProcessingLoop` (`:586`).
- Block building runs in a separate producer env with its own lifetime scope and scoped branch processor:
  - `BlockProcessingModule.cs:76` (`AddScoped<IBranchProcessor, BranchProcessor>`), `GlobalWorldStateBlockProducerEnvFactory.cs:55` (`BeginLifetimeScope`).
  - Default (`BuildBlocksOnMainState = false`, `BlocksConfig.cs:109`): `BlockProducerEnvFactory` (`BlockProcessingModule.cs:163-166`) with `OneTimeChainProcessor`, which calls `_branchProcessor.Process` directly (`OneTimeChainProcessor.cs:43, 71`).
  - Opt-in (`BuildBlocksOnMainState = true`, `BlockProcessingModule.cs:158-161`): `MainStateBlockBuildingChainProcessor` (`GlobalWorldStateBlockProducerEnvFactory.cs:31`); it "never touches the main processing queue" (`MainStateBlockBuildingChainProcessor.cs:17-25`).
  - Neither sets `IsProcessingBlock`, and neither is the branch processor the scheduler listens to.
- Call path: `BlockProducerBase.ProcessPreparedBlock` → `Processor.Process` (`BlockProducerBase.cs:140, 192-193`).
- After building, the produced block is suggested to the main tree (`ProducedBlockSuggester`) and imported by the main loop: protection applies then, not while building.

## Signature recovery

- `FrameTxSignatureFilter.Accept` (`Nethermind.TxPool/Filters/FrameTxSignatureFilter.cs:35-57`) takes no preempt callback and no per-head budget.
- A signature-stuffed tx in flight when a block starts runs to completion: ~2.7 ms at 300k, ~4.5 ms at 500k on the runner (v2 idle cost). This residual still grows with the ceiling.

## How long and how often building runs

`Nethermind.Merge.Plugin/BlockProduction/PayloadPreparationService.cs`:
- On `StartPreparingPayload` (fcU with attributes): an empty block at once, then `ImproveBlock` (`:95-105`).
- Improvement loop (`:218-245`): waits `CalculateImprovementDelay`, rebuilds **only if new pending txs arrived** (`:240`, `added == _txPool.PendingTransactionsAdded`). Rejected spam never enters the pending pool, so spam alone does not trigger rebuilds.
- Delay (`:256-320`): fraction of the remaining slot time, from 1/6 at the start to 1/960 at the end, cubic in slot progress. About 2 s early in the slot, down to ms near the end.
- So building is a series of full block builds, spread over up to a slot and denser near `getPayload`. Pausing gossip for all of it would stall admission for seconds.

## Options (for a PR)

1. **Preempt during each build, not the whole preparation.** Raise the flag (or a producer-specific one) only inside `BlockProducerBase.BuildBlock` / `ProcessPreparedBlock`. Each build is tens to hundreds of ms, like an import. Cheapest and closest to #13994.
2. **Preempt signature recovery.** Check the same callback between recoveries in `FrameTxSignatureFilter`. Independent of building; closes the C-dependent residual on import too.
3. **Per-head budget for signature recovery**, like the 1,000 ms prefix budget.
4. **Reserve CPU for the builder:** cap concurrent gossip validation while a payload is being prepared.
5. **Scale the per-head budget down** while a payload is being prepared.

Trade-offs:
- Options 1 and 2 defer txs (`FrameSimulationDeferred`). Only an **announced** tx gets a `RetryCache` refetch. A **pushed** opaque frame tx that yields stays in the known cache and is never simulated again: in practice it is lost (pinned by `TxPoolTests.Gossiped_frame_tx_whose_simulation_yielded_is_refetchable_once_per_request`, case `(true, false, 1)`; `FrameTxPrefixSimulator.cs:81-85`, `RetryCache.cs:698-712`). Council review 2026-09-30 found this; #14085's Remarks and Claude's review say the retry cache absorbs it, which is wrong for pushes.
- With the rebuild cadence, build preemption can be active up to ~85% of the time near the end of the slot (council estimate), so the loss falls on the proposer's own block.
- Option 6 (council): **pause the gossip scheduler** for producer builds too (it listens only to the main branch processor, `BlockProcessingModule.cs:121-126`). New checks wait instead of being lost; keep preemption only for a simulation already running.
- Option 1 applies only to local builders; mev-boost users do not build locally.
- Options 3 to 5 cut honest admission too; they need the "honest tx loss" measurement (Daniil point 6).

## How to measure

- Producer arm with the flag raised during each build (option 1), signature-stuffed, 250k to 400k, p99 (Daniil point 5).
- Import periodic arm, signature-stuffed, with option 2 on and off.
- Honest-tx stream over a full slot for options 3 to 5.

## Next step

Discuss with Daniil (author of #13994). Before more work on #14085: fix its Remarks (push case, opaque-prefix shapes only, signature recovery uncovered, mev-boost line to verify; draft in `~/frame-tx-campaign-keep/drafts/pr-14085-remarks-v2.md`) and prototype option 6. Keep #14085 out of the first campaign runs (SPEC §6).
