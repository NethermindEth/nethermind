# Computed ordinary completion

## Inputs, not answers

The entry contains the preparation input, the shared transaction/header and initial processor/receipt-prefix stores, plus a raw typed VM-return observation attached to the **computed final** preparation frame. Raw metadata missing from the preparation record (transaction limit, fee caps, premium, blob fee, receipt metadata and floor intrinsic gas) must have explicit source/projection coherence; none is an independently supplied settlement value.

Forbidden entry fields include `SettledEntry`, `GasConsumed`, status code, pre-refund gas after local transformations, halt state floor, code-insert execution refund, sender refund, fee totals, final counters, completed receipt and completed transaction result.

A constructor-based substate domain distinguishes normal success, REVERT and exceptional halt. It derives `IsError`, `ShouldRevert`, exception, output/log/refund restrictions and receipt status. REVERT has `ShouldRevert = true`, `IsError = false`, exception `Revert`; it is not an invalid transaction. Both REVERT and VM halt return an executed transaction result with `TransactionResult.Error = None`. Failed receipt error is a different field and can be nonnull. Production `TransactionResult.Equals` collapses executed results, so exact result fields—not that equality—are the observation.

## Four distinct gas values

1. The outer `ExecuteEvmTransaction` by-value gas parameter is the computed preparation gas.
2. The top-level `VmState.Gas` after actual VM return is the raw VM observation.
3. `ExecuteEvmCall` copies that frame value into its local gas parameter.
4. `Refund` copies that local value again and mutates its own working gas.

Ordinary refund does not write its working copy back to the call-local or outer parameter. The result exposes these values separately. Post-VM gas already incorporates the VM's top-level REVERT state-gas refund; exceptional halt cleanup is subsequently derived by the accepted refund helper.

## Ordered derived stages

| Stage | Derived data / observable effect |
| --- | --- |
| Preparation | Run the exact pinned preparation; require the final VM boundary, non-CREATE, nonnull code and exact frame identities. |
| VM return | Attach constructor-constrained substate and five frame gas fields to that frame. VM correctness is an explicit external obligation. |
| Call completion | Copy frame gas; report access when enabled; derive failure from `ShouldRevert || IsError`; restore exact top-level snapshot then RIPEMD touch, or derive success; dispose the frame. |
| Refund | Construct `ordinaryRefund` input from post-VM gas, prepared intrinsic standard/baseline/delegation count and shared transaction/gas price; evaluate the accepted helper. |
| Processor counters | On exact normal sequential options, add effective execution and state gas to the processor's own counters and write header maximum. |
| Fees | Derive beneficiary and collector amounts from spent gas and shared transaction/header; invoke normal hooks in source order. |
| Finalization | Write transaction spent/block gas, invoke commit, derive terminal input/output/error/logs and call the accepted receipt terminal. |
| Scope exit | Dispose environment and access tracker after the finalization expression returns. |

Empty destroy-list restriction removes both immediate and deferred destruction, including deferred burn-log augmentation; it is an explicit domain restriction, not a silent omission.

## Refund and fee derivation

Refund entry is `ordinaryRefund`; creation and top-level CREATE-charge flags are false. Delegation count is the prepared count converted with its bound. Intrinsic standard and post-intrinsic reservoir come from the preparation result; floor gas is the shared intrinsic-gas projection. Destroy count is zero. The error and revert flags are derived from the substate constructor, never independent booleans.

All six refund `GasConsumed` fields are projected unchanged: spent, operation, block execution, block state, max used and refund. REVERT follows ordinary settlement, not `CompleteEip8037Halt`; only exceptional `IsError` selects that halt path.

Fees use `premiumPerGas * SpentGas`. Effective base fee is `min(header.BaseFeePerGas, opcodeGasPrice)`; the free-transaction guard, EIP-1559 collector guard, optional blob collection and distinct `ReportFees` payload remain explicit. The normal-world-state contract records credits and their order, not a caller-supplied final balance.

## Two counter stores, two header writes

Processor cumulative execution/state counters and `BlockReceiptsTracer` gas history/cumulative paid gas are separate stores. The processor first writes `max(updated execution, updated state)`; the sequential receipt terminal later overwrites the same header using its own prefix fold. The model computes both. Equality of the two prefixes is a separate upstream history invariant if desired; it is not assumed as a final-output equality.

Effective block gas is `BlockGas > 0 || BlockStateGas > 0 ? BlockGas : SpentGas`. Thus zero execution with nonzero state must not fall back to spent gas. Receipt paid/cumulative gas uses spent gas. Optional receipt execution gas is present only when raw block execution gas is positive.

The receipt theorem's individual operand and cumulative-sum bounds are derived from the three existing receipt-prefix sum bounds in `CounterRanges`. They are not accepted as another independent `AccountingValid` premise.

## Refinement architecture and open work

The independent reference composes the three handwritten upstream specifications. Stage bridges instantiate exact accepted theorems at derived inputs. Preparation source identity, representation and final-frame presence are required before attachment; upstream theorem names and artifact hashes are frozen.

No theorem may receive a completed output or an equality asserting that the source already produced the specification result. Hook relations describe primitive normal observations/effects, not settlement. The generated continuation computes its own projections and result over the shared reference input/output types. Its component proof establishes equality with the independent reference, and its source witness contains only artifact identities. The adapter adds the existing domain obligations; the completed equality and three accepted stage equalities are conclusions, never supplied premises.

The current Roslyn candidate resolves the exact selected source/reference closure, metadata identities/MVIDs and compiler options. Its frozen signatures, typed sites, exact control ownership and CFG checks reject admitted role/control changes before the whole-source fallback. This remains a reviewed restricted translator plus template, not a formally verified general C# frontend. The artifact writer publishes the manifest last after complete in-memory rendering. Executable promotion still requires independent bridge/artifact review, compile-first generated semantic mutations, and complete frozen compiler-discovered exports including private declarations, descendants and upstream dependencies, with transitive standard-only axiom checks and adversarial census controls.
