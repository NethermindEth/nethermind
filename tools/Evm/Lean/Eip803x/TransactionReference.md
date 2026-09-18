# Transaction lifecycle reference

`TransactionReference.lean` is a handwritten, executable Lean reference for
the ordinary pinned standard-mainnet `EthereumTransactionProcessor` outer
lifecycle. It is deliberately an abstract reference, not a production
refinement theorem. Recovery, validation, world-state/adapter calls, VM and
frame execution, roots, fee movement, receipts, and commit/reset are explicit
oracle operations.

The model records runtime events on each path. It does not turn the source
method inventory into an artificial list of events. Optional events are
omitted when their route does not execute them, and every adapter route ends
with `endTxTrace`.

## Runtime order

The ordinary prefix is:

```text
optional LoadNonceFromState -> StartNewTxTrace -> Process
-> optional BuildUp snapshot -> ExecuteCore route
-> RecoverSenderBeforeIntrinsicGas -> CalculateIntrinsicGas -> ValidateStatic
-> CalculateEffectiveGasPrice -> RecoverSenderIfNeeded -> ValidateSender
-> BuyGas -> IncrementNonce -> PrepareSimpleTransferFastPath
-> optional CommitBeforeExecution -> CalculateAvailableGas
```

`commitBeforeExecution` is guarded by the production condition
`commit && (simpleTransferRecipient == null || restore ||
tracer.IsTracingState)`. The model carries `tracerIsTracingState` explicitly,
including the state-traced simple-transfer mutation vector.

The EVM continuation is:

```text
authorization snapshot -> ProcessDelegations -> BuildEnvironment
-> capture post-intrinsic halt baseline -> [non-CREATE recipient state charge]
-> [authorization or delegated-target preparation OOG: authorization restore when present
   + prePreparationGas reset]
-> top execution snapshot -> [CREATE: one destination read/classification
   -> NEW_ACCOUNT state charge when logically dead -> collision exit]
-> PayValue -> VM
-> rollback/deployment -> Refund (including sender) -> header gas
-> PayFees -> deferred destroy-list finalization -> restore/commit/reset
-> receipt open/observation/close -> EndTxTrace
```

`Input` carries one typed CREATE destination classification for the
destination-dependent EIP-8037/EIP-7610 rule. It separates logical EIP-161
account existence from the collision fact, and records the observed balance,
nonce, code emptiness, and storage occupancy. Logical existence is exactly
`balance != 0 OR nonce != 0 OR code is nonempty`; physical storage alone does
not establish it. `run` invokes `admitCreate` through
its `runWithCreateAdmission` seam before the value transfer. The result is
saved in state, including its exactly-one read count. A logically dead
destination is charged `NEW_ACCOUNT` through the state-gas machine before the
collision branch. A storage collision may have either account-trie existence:
the model includes both a trie-dead storage collision and a balance-bearing,
nonce-zero, code-empty, storage-nonempty trie-existent collision. The latter
still collides but does not receive the new-account charge. A one-short total-
gas charge is exceptional OOG. An affordable collision refills the just-
applied state charge before clearing execution gas and entering no child.
Existing balance-only, nonce-collision, code-collision, and storage-collision
accounts do not receive the new-account charge. Nonzero nonce, nonempty code,
and storage occupancy each forward-imply a collision fact, matching the
production collision predicate; balance alone establishes logical existence
but not collision. Inconsistent
destination facts, or a fixture directive inconsistent with the admitted
collision/OOG result, fail closed. Terminal collision and CREATE state-OOG
paths never emit `PayValue`; `createStateCharge` is emitted only for a nonzero
dynamic charge. The reverse storage-only collision fact still permits either
account-existence value, including the balance-bearing existent collision.
The ordinary pre-execution recipient charge is explicitly excluded for
contract creation, matching the production
`!tx.IsContractCreation` guard; CREATE instead reaches its dynamic destination
charge only after the top-frame snapshot.

The simple-transfer route emits its fast-path execution, recipient state
charge, value transfer, settlement, finalization, and receipt events. Before
that path is entered, `routeDirectiveAdmissible` requires the simple route,
the sole ordinary fast-path directive (`success`), and no pre-existing
top-frame OOG; a mismatched route or directive ends as
`invalidExecutionRoute` before either fast-path execution or `PayValue`.
The simple execution continuation is deterministic after this guard. Its only
other terminal result is the derived recipient state-charge OOG, whose charge
attempt is atomic. Successful simple execution applies the value effect without
replacing the already charged gas state, so a seven-unit recipient charge
settles as 20 execution plus 7 state gas rather than disappearing under the
fixture execution-gas observation. System and skip-validation system routes are explicit
`systemBypass` outcomes rather than being silently identified with ordinary
execution. `ValidateStatic` is
always an event on the ordinary route: SkipValidation gates only its gated
nonce/block-limit checks, while unconditional checks still stop the route.
Nonce mismatches are ignored by SkipValidation. Max-fee and balance failures
stop at `BuyGas`,
sender-code failure stops at `ValidateSender`, and nonce failure stops at
`IncrementNonce`. The fast-path preparation cannot reject; an unresolved
recipient is represented as the production-thrown escaping
`BuildExecutionEnvironment` exception: it has no `Restore` and no
`EndTxTrace`, including under CallAndRestore. There is no generic
effective-price failure in this reference. Restore modes append cleanup for
sender, gas-purchase, nonce, and overflow failures. The five explicit
`forcedRestoreFailure` fixtures exercise each distinct CallAndRestore cleanup
branch without changing normal SkipValidation semantics. BuildUp retains its
entry snapshot on an early failure and does not claim a restore that
production does not perform.

`traceOrdered` checks the numeric order of every concrete event trace and the
vector file checks the actual path traces. `Phase` is only an auxiliary coarse
taxonomy; it is not the coverage surface.

The principal production anchors are:

- `src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs`:
  `Process`/`ExecuteCore` (around lines 158-206), validation and reservation
  (232-263), authorization/environment (324-370 and 708-792), EVM settlement
  (372-424), simple transfer (429-524), header/fees (587-614), finalization
  (619-668), and VM/rollback/deployment (1308-1488).
- `TransactionProcessor.cs` `PayFees` (around 1706-1713) and its sender refund
  path, plus the deferred EIP-8037/EIP-7708 destroy-list handling around
  378-424 and 1426-1453.
- `BuildExecutionEnvironment` throws when the recipient cannot be resolved;
  that exception escapes the transaction result/trace epilogue. The
  `commitBeforeExecution` condition uses `tracer.IsTracingState` alongside
  the commit/restore and simple-transfer tests.
- `TransactionProcessor.cs` `PrepareDeployment`/`TryConsumeCreateStateGas`
  (around 1342-1385): it performs one destination classification, charges a
  logically non-existent target before the collision exit, and invokes
  `PayValue` only after that branch has entered execution.
- `TransactionProcessor.cs` recipient charge (352-358) excludes contract
  creation. Its preparation-OOG branch (318-370) restores `prePreparationGas`
  independently of `hasPreExecutionSnapshot`. `CompleteEip8037Halt`/
  `RefundOnTopLevelHalt` (1525-1546 and 1588-1620) reset the state-gas
  baseline, clear execution gas, preserve the post-halt reservoir, and settle
  it from `txGas - stateGasReservoir`.
- `src/Nethermind/Nethermind.Consensus/Processing/TransactionProcessorAdapterExtensions.cs`
  (`ProcessTransaction`, including `EndTxTrace` around line 27), and the
  execute/build-up/trace adapters under
  `src/Nethermind/Nethermind.Evm/TransactionProcessing/`.

## State and explicit boundaries

`EntryKind` distinguishes message calls, contract creation, simple transfers,
and system calls. `ExecutionMode` distinguishes execute, call-and-restore,
BuildUp, trace, warmup, and skip-validation system routing. State contains
durable/reversible world markers, nonce, sender/recipient/beneficiary and fee
collector balances, reservation, gas/refund/state-gas observations,
authorization, preparation/top snapshots, rollback, substate flags, logs,
destroy list, receipt state, and commit/reset observations.
`stateGasFromGasLeft` is retained alongside the settlement gas record so the
CREATE admission conversion to the common state-gas machine preserves a
charge spill and its LIFO refill. EIP-8037 EVM state additionally records the
full pre-preparation gas snapshot, whether that snapshot was restored, and the
gas snapshot presented at the top-frame boundary, plus the post-intrinsic halt
baseline consumed by `completeEip8037HaltState`.

REVERT is represented as `isError = false, shouldRevert = true`; its unused gas
is returned by settlement while state refunds are suppressed. A top-level
CREATE revert LIFO-refills the state-gas delta charged after its execution
snapshot, while retaining the VM's post-execution gas observation. Exceptions,
top-frame OOG, collision, CREATE state-gas OOG, invalid-code deposit failure,
and code-deposit OOG each have separate `ExecutionDirective`/`TransitionKind`
values and traces. The exceptional settlement selects the EIP-8037 halt scalar
input whenever the modeled result routes through
`CompleteEip8037Halt`: ordinary VM exceptions, initial/explicit top-frame
OOG, preparation OOG, collision, CREATE state-gas OOG, invalid-code deposit,
and code-deposit OOG. Before either settlement view is calculated,
`completeEip8037HaltState` restores the captured post-intrinsic state-gas
baseline (reservoir, state-used, and spill), clears exposed `gasLeft`, and
zeros the model refund counter. Thus both the scalar and dimensional records
observe the same normalized halt state; they are not relying on a fixture that
happens to supply zero execution gas. The preserved state reservoir remains
unspent and the pre-refund value is `txGas - stateGasReservoir` rather than the
generic full-gas error rule. Normal refund/destroy and code-insertion execution
refunds are zero on this EIP-8037 halt path, matching the production helper's
default deposit-failure call and EIP-8037 code-insert rule. This is a local
composition around the accepted generic settlement function; it does not prove
production routing or extraction. Code-deposit invalid-code and deposit-OOG
retain their production non-error substate flags, but `FailContractCreate`
still routes them through `CompleteEip8037Halt`.
The simple-transfer fast path is deliberately excluded from this EVM-frame
normalization: production sends its new-account OOG through its distinct
`Refund` route. The model records that state-OOG as a non-error, non-revert
terminal result, clears exposed execution gas, and returns any surviving state
reservoir through normal settlement. The
`simple_transfer_oog_does_not_select_the_evm_halt_normalization` and
`simple_transfer_state_oog_returns_its_reservoir_without_error` theorems guard
that boundary. `tryConsumeRecipientStateGas` directly calls the common
state-gas machine and preserves the entire pre-charge state on failure. The
nonzero-reservoir simple-transfer vector starts with 78 execution gas and a
two-unit reservoir from transaction initialization, then proves its one-short
charge failure preserves those gas fields rather than receiving fixture-added
reservoir gas.

The model captures full `prePreparationGas` before authorizations and a
post-intrinsic halt baseline after environment construction. `PreparationSnapshot`
is taken after the transaction nonce increment and contains world markers,
sender nonce, all modeled participant balances, full gas observation, logs,
and destroy-list state. A preparation rollback restores those fields while
retaining authorization metadata and prepaid reservation outside the world
snapshot. Thus self-authorization OOG restores nonce one, rather than leaving
a second authorization-time increment. Every modeled EIP-8037 preparation OOG
(authorization, recipient state charge, or the explicit preparation fixture)
restores the full pre-preparation gas snapshot before top-frame handling, even
when no authorization snapshot exists. This also applies when
`BuildExecutionEnvironment` reports delegated-target OOG or returns its
explicit top-frame-OOG flag with no failure; the combined OOG flag and
preserved authorization snapshot drive restoration before the independent gas
reset. The separate `preparationGasRestore` observation records that reset; it
is not inferred from a coincident final reservoir. Authorization OOG
deliberately mutates durable/reversible markers, logs, and gas before
restoring the authorization snapshot when present, then applies the
independent gas reset and halts before the top frame.

`ExecutionSnapshot` captures the corresponding execution-time world, nonce,
balances, gas/refund state, logs, and destroy-list state after authorization.
Rollback therefore restores an executing delegated sender's EVM-time nonce
increment and beneficiary/collector VM credits, while retaining the earlier
included-transaction nonce increment, authorization effect, and prepaid
reservation. Its gas state still refills only the state-gas delta charged
after that snapshot, rather than replacing the VM's execution-gas result.
The literal delegated-CREATE-REVERT regression fixes exact post-fee balances
and conservation, alongside field-by-field snapshot mutation sentinels.

Every returned adapter `State` is consumed through a stage-specific
`ExecutionControlBoundary` projection. It preserves the immutable baseline,
route and preparation/rollback snapshots, execution/substate control,
settlement/header/cumulative accounting, receipt flags, and cleanup/commit/run
setup control. `OracleEffect` may change only the controls owned by its event:
successful buy-gas reserves exactly transaction gas and enables cleanup,
successful nonce increment enables cleanup, and the explicit
commit/finalize/restore/reset events set only their own control fields.
Authorization, preparation, environment, and EVM results otherwise preserve
the projection; settlement kernels may vary only settlement, scalar, header,
and cumulative fields. An environment-reported path must equal the already
selected route, and an EVM result must have the entry-kind-admissible directive
exactly declared by `Input`. Any inconsistent control or directive terminates
as unmodeled without adopting the returned state. World, balance, gas, log,
and destroy-list contents outside that projection remain explicit oracle
obligations.

`commitBeforeExecution` is recorded as a separate state mutation from the
final commit. Call-and-restore first restores the baseline and then commits the
restored nonce and reserved-gas correction for an ordinary sender. The
`deleteCallerAccount` result returned by sender recovery selects the production
temporary-sender branch, which deletes that account instead of committing; the
account deletion itself remains a world-state adapter obligation. Both paths
retain substate logs for the receipt and close the receipt before ending the
trace on ordinary completion. An escaping environment exception never reaches
that epilogue.

Settlement keeps two distinct accounting views. The scalar result is paid gas
and receipt cumulative gas. The dimensional result carries execution and
state gas. For EIP-8037, the model accepts prior block execution/state
cumulatives, adds the current dimensions, and sets header gas to their
maximum; it never substitutes paid transaction gas for this header value.
The state-bottleneck vector exercises the state cumulative as the maximum.
Fee inputs explicitly include effective price, premium, base fee, blob fee,
collector selection, and an overflow flag. The collector formula is
`min(baseFeePerGas, effectiveGasPrice) * spentGas + blobBaseFee`; the fee
vector therefore expects 44 rather than charging the uncapped base fee.
`nonsemanticFixtureOracle` and its
one-million-unit fixture balances are named as nonsemantic test plumbing and
are excluded from any production lifecycle claim. Overflow is an explicit
unmodeled outcome. Receipt observations retain status, paid gas, prior-plus-
current cumulative paid gas, logs, and an optional post-state-root value (the
Amsterdam fixtures use no root unless one is supplied). Destroy-list
finalization is a distinct post-fee event.

## Compile-time vectors and theorems

`TransactionReferenceVectors.lean` contains 53 independently written,
`native_decide`-checked lifecycle vectors. They cover ordinary success; REVERT with
nonzero gas, suppressed refund, calldata floor, and prior receipt gas; a
top-level CREATE REVERT that refills new-account state gas; a zero-reservoir
exception; top OOG; collision; CREATE state OOG; code-deposit
invalid-code and deposit-OOG; successful creation; authorization
OOG/applied/skipped routes; delegated-target OOG with authorization rollback;
simple transfer with retained recipient state gas and zero/nonzero-reservoir
state-OOG; failures at
unconditional and gated static validation, sender validation, max-fee/balance
and gas-purchase checks at BuyGas, nonce increment, BuildUp, warmup, and
trace; five distinct CallAndRestore cleanup failures (sender, max-fee,
balance, nonce, and fee overflow); escaping unresolved environment setup;
state-traced simple transfer; system and skip-validation routes; load-nonce,
two call-and-restore finalization paths (ordinary restored-state commit and
temporary-sender deletion without commit), fee-collector/blob pricing, deferred
SELFDESTRUCT/destroy-list finalization, an unmodeled VM result, and the
state-bottleneck accounting edge. The expected traces and observations are
literal records written independently from the model helpers. In particular,
the SkipValidation vector carries both a gated nonce-overflow/mismatch pair
that is ignored and a malformed unconditional failure that is not ignored.

It also contains 17 literal CREATE-admission unit vectors covering fresh and
storage collisions, reservoir/execution-gas spill, one-short OOG, existing
balance/nonce/code targets, a balance-bearing trie-existent, nonce-zero,
code-empty, storage-nonempty collision, storage-without-collision,
logically-empty-existent and storage-only-existent facts, nonce-without-
collision, code-without-collision, and other inconsistent oracle facts, and
zero-cost boundaries. A separate fourteen-vector
`run` matrix carries those facts through the full transaction lifecycle:
fresh admission, trie-dead and
trie-existent storage collision, one-short OOG, balance-only entry, nonce/code
collision, and inconsistent facts or directives. Its literal observations
include both settlement records, sender refund/balance, tip and fee-collector
burn, header and cumulative block counters, cumulative receipt gas, and the
full receipt record. The storage-collision vectors initialize a two-unit
reservoir and nonzero price/tip/base fee so the CREATE-halt result must return
exactly two gas units to the sender, pay a 33-gas fee, produce a 33-gas
receipt, and retain the matching block counter values. The trie-existent
storage-collision vector fixes a zero NEW_ACCOUNT charge, a collision trace
before any `PayValue`, and that same common EIP-8037 halt settlement. A
separate eight-vector EIP-8037 halt matrix uses the same
nonzero reservoir for a stack-underflow exception with adversarial positive
execution/state gas, explicit and initial top-frame OOG, adversarial
preparation OOG with no authorization snapshot, CREATE top-frame OOG,
one-short CREATE state OOG, invalid code deposit, and code-deposit OOG. Each
literal record fixes scalar and dimensional 33-gas settlement, the two-gas
sender refund, tip/burn balances, receipt, and header/cumulative counters. The
two deposit vectors supply a nonzero code-insert-refund fixture, so their
expected 33-gas result proves that the common EIP-8037 halt path—not the
generic non-error settlement—was selected. The preparation vector observes
the exact restored pre-preparation gas snapshot, the gas actually presented at
the top-frame snapshot, and the final halt baseline.

Fourteen mutation kernels are injected through eighteen full-run assertions:
charge-after-collision, storage-only-as-existing, failure to clear CREATE OOG
gas, charging an existing target, charging or rejecting a trie-existent storage
collision, accepting storage-without-collision, generic full-gas burn on the
simple-transfer reservoir path and storage-only, ordinary-exception, and
CREATE state-OOG paths, skipping the common halt state reset for an ordinary
exception and each deposit failure, omitting the CREATE-revert state-gas
refund, suppressing delegated-target OOG, skipping the preparation-gas restore,
and an erroneous generic recipient charge on CREATE.

The ninth correction adds two literal self-authorization preparation-OOG
regressions (direct authorization OOG and environment top-frame OOG), a
delegated-CREATE-REVERT balance-conservation regression, and nine field-omission
snapshot sentinels. It also adds a sixteen-hook `OracleEffect` baseline-swap
sweep, six early-hook lifecycle-control substitutions plus a malformed stopped
hook, and preparation, authorization, and settlement boundary substitutions.
Those cases prove that a returned state cannot silently change the baseline,
cleanup, snapshots, route, receipt/accounting controls, or destroy-finalization
ownership before a later lifecycle stage.

The executable checks `all_vectors_pass`,
`all_vector_traces_are_ordered`, `all_vector_phase_traces_are_ordered`, and
`vector_count = 53`. It additionally checks
`all_create_admission_vectors_pass`, `create_admission_vector_count = 17`,
`create_admission_boundary_theorems_hold`,
`all_create_admission_run_vectors_pass`,
`create_admission_run_vector_count = 14`,
`all_eip8037_halt_run_vectors_pass`,
`eip8037_halt_run_vector_count = 8`,
`all_eip8037_halt_run_traces_are_ordered`,
`deposit_failure_directives_route_through_eip8037_halt`,
`positive_gas_exception_is_normalized_before_settlement`,
`preparation_oog_restores_pre_preparation_gas_without_authorization_snapshot`,
`code_deposit_halts_preserve_the_reservoir_without_code_insert_refund`,
`simple_transfer_oog_does_not_select_the_evm_halt_normalization`,
`simple_transfer_state_oog_returns_its_reservoir_without_error`,
`simple_transfer_success_preserves_charged_gas`,
`simple_transfer_state_charge_failure_starts_with_and_preserves_reservoir`,
`simple_transfer_revert_fails_closed_before_value_transfer`, and
`forged_simple_transfer_route_fails_closed_before_value_transfer`, and
`prehalted_simple_transfer_fails_closed_before_value_transfer`,
`environment_route_mutation_fails_closed_before_value_transfer`,
`environment_advertised_route_switch_fails_closed_before_value_transfer`,
`evm_simple_transfer_directive_fails_closed_without_state_adoption`,
`evm_directive_mismatch_fails_closed_without_state_adoption`, and
`evm_control_mutation_fails_closed_without_state_adoption`,
`preparation_baseline_substitution_fails_closed_before_call_and_restore_cleanup`,
`authorization_destroy_finalization_mutation_fails_closed_without_state_adoption`,
`settlement_baseline_substitution_fails_closed_without_state_adoption`,
`all_oracle_effect_hooks_reject_baseline_replacement`, and
`oracle_effect_hook_count = 16`,
`early_effect_control_mutations_fail_closed_without_state_adoption`, and
`early_effect_control_mutation_count = 6`,
`self_authorization_oog_restores_complete_preparation_snapshot`,
`self_authorization_environment_top_oog_restores_complete_preparation_snapshot`,
`delegated_create_revert_restores_complete_execution_snapshot_before_fees`, and
`snapshot_restore_mutation_sentinels_are_detected`,
`top_level_create_revert_refills_new_account_state_gas`, and
`delegated_target_oog_restores_preparation_before_halt`,
`delegated_target_oog_restores_environment_mutated_preparation`, and
`environment_reported_top_oog_restores_authorization_preparation`,
`all_create_admission_run_traces_are_ordered`,
`run_create_admission_reads_once`, and
`terminal_create_admission_never_pays_value`,
`run_existing_destinations_have_no_new_account_charge`,
`run_storage_only_collision_refills_exact_charge`,
`run_balance_bearing_storage_collision_uses_common_halt_settlement`, and
`run_level_mutation_sentinels_are_detected`. It also checks
`invalid_settlement_domain_reaches_run`: the invalid settlement domain is
reached through `run` and terminates without scalar/dimensional settlement;
`state_traced_simple_transfer_commits_before_execution` and
`environment_exception_escapes_without_epilogue`; and the model's
`failed_directive_rollback_stage`/`rollback_directives_are_staged` theorems.
Settlement requires both scalar and dimensional `SettlementInput.Valid` before
any settlement arithmetic. The
expected observations are literal, independently authored records; they do
not call `classifyDirective`, `flagsOf`, `executionStatus`, or `transitionOf`.
`revert_substate_flags` and the snapshot theorems in the reference file cover
the local rollback/flag contracts.

## Deliberate gaps

This module does not claim equality with production. Cryptographic sender and
authority recovery, transaction validity arithmetic, trie/world-state
effects, VM opcodes/precompiles/nested frames, adapter reachability, actual
receipt encoding, root hashing, and system-transaction semantics remain
explicit oracle obligations. The fixture oracle is useful for ordering and
mutation boundaries only. `forcedRestoreFailure` is explicitly non-production
fixture plumbing for exercising cleanup branches. No source-hash assumption
appears in the model, and there is no claim that its toy balances or
arithmetic implement a complete mainnet economic settlement. Prior block
aggregates are inputs to this single-transaction reference, not a block-fold
proof. The typed CREATE-admission boundary is integrated into the handwritten
`run` lifecycle and proves destination-fact consistency, one-read/charge
ordering, reservoir charge/refill behavior, and exceptional collision/OOG
metadata there. It does not prove destination derivation, real trie reads,
nonce/balance/depth prechecks, journal persistence, or full
transaction-processor adapter reachability. This correction does not change
the separate README or verification-manifest acceptance state: the
transaction-processor claim remains unaccepted pending a ninth unrelated
independent rereview.
The numeric `recipientStateCharge` remains an adapter-observed input; only its
consumption is fixed here as an atomic state-machine transition rather than an
injectable charge-result fixture.

## Targeted verification

From `tools/Evm/Lean`, with the pinned portable toolchain:

```text
lake build Eip803x.TransactionReference Eip803x.TransactionReferenceVectors
```

The files are standalone and intentionally require no root-import, manifest,
or dependency change.
