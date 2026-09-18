# Standard-mainnet system transaction reference

This directory contains a handwritten, executable Lean model of the pinned
standard-mainnet route into `SystemTransactionProcessor`. It is a reference
model, not an extraction of C# and not a production-refinement proof.

Pinned source revision: `b2478235e71e6a7ec2a509aa0155e25d5fdfff80`.

## Modeled route

`SystemTransactionReference.lean` models the following production order:

1. `TransactionProcessorBase.Process` takes the special outer snapshot only
   for the exact `BuildUp` option.
2. `ExecuteCore` selects the lazily-created system processor when
   `tx.IsSystem()` is true, or when the options value is exactly
   `SkipValidation`. `SystemCall` type membership by itself is not a routing
   condition.
3. The standard processor suppresses system-account BAL reads for
   `Address.SystemUser`, invokes the no-op standard hook, derives
   `_payOriginalValue`, and conditionally ORs `Commit | SkipValidation` into
   the options passed to base execution.
4. `Warmup` is masked only while deriving `_payOriginalValue`; the original
   option remains in the options passed to base execution. This distinction
   controls final transaction-field writes.
5. Base execution selects the system spec, performs inherited static checks,
   bypasses sender validation, and invokes the gas-purchase and nonce operations
   supplied by `OverrideKernel` against the current world before selecting the
   simple-transfer or EVM path. The production operations preserve that world;
   charging, incrementing, or stopping mutants change the world and/or prevent
   body execution. Refund and fee payment remain bypassed by their production
   overrides.
6. A `SystemCall` receives the fixed state reservoir and an execution budget
   equal to the remaining gas limit. A non-`SystemCall` uses the ordinary
   exact Amsterdam intrinsic calculation derived from its transaction. The
   supplied observation must equal that calculation and must have no intrinsic
   state reservoir, matching the production debug invariant. Contract creation
   includes EIP-3860's execution-only `ceil(initcode bytes / 32) * 2` charge;
   that term is deliberately absent from the EIP-7976 floor.
   Inputs above signed 64-bit range fail closed before the production
   `ulong`-to-`long` reservoir calculation; both pinned call-site gas limits
   have proved `Int64` bounds.
7. Simple-transfer state-gas failure occurs before value movement. Otherwise
   original value is debited only for a validating entry (and not for a
   self-send). A successful non-self positive-value simple transfer under the
   pinned EIP-7708 spec exposes the exact transfer-log projection: SystemUser
   emitter, transfer signature, from/to addresses, and amount. A successful
   positive-value top-level creation must additionally supply a nonempty
   executing-address observation; that derived created address is the log's
   `to` address. Missing or empty observations fail closed. The EVM path takes
   a top-level snapshot and restores it on
   REVERT, exception, collision, CREATE state-gas failure, and deployment
   failure. REVERT returns `TransactionResult.Ok` while its receipt status is
   failure; exceptional halt returns an EVM exception.
8. Settlement is an explicit oracle observation. The processor-owned normal
   header, block-execution, and block-state counters remain unchanged because
   effective system options contain `SkipValidation`. Final reset/commit,
   transient reset, transaction gas-field writes, receipt tracing, suppression
   disposal, and return are ordered events.

Authorization-list system transactions fail closed before body execution.
The reference intentionally does not approximate `ProcessDelegations`, its
authorization snapshot, or partial-authorization out-of-gas restoration.

The event relation is deliberately path-sensitive. Unsupported engines,
non-`ReleaseSpec` representations, non-Amsterdam/EIP-8037 specs, OP system
transactions, invalid call-site shapes, and observations inconsistent with
the modeled input world terminate with `unmodeled` instead of silently taking
an approximate path.

Receipt tracing is not an independently trusted oracle fact. In the admitted
domain both pinned tracer kinds require `tracingState = false`;
`TracerKind.null` additionally requires `tracingReceipt = false`, while
`TracerKind.callOutput` requires `tracingReceipt = true`. The abstract `other`
tracer kind is unmodeled. Receipt or state-tracing contradictions fail closed
before processor routing.

The accepted domain is exactly `Engine.standardMainnet`,
`Spec.pinnedAmsterdam`, and `Schedule.pinnedAmsterdam`. The pinned spec is a
non-genesis concrete `ReleaseSpec` with EIP-158 and EIP-658 enabled before the
system-spec wrapper and all modeled Amsterdam flags enabled: EIP-2780, 7708,
7778, 7843, 7928, 7954, 7976, 7981, 8024, 8037, 8038, 8246, and 8282. The
system-spec projection then disables EIP-158. Any engine, fork flag, spec
representation, genesis bit, or gas-schedule difference is unmodeled.

## Block-internal adapters

`runBlockCall` models only the standard-mainnet call sites that actually use
the transaction processor at the pinned revision:

- EIP-4788 beacon-root storage is pinned to the standard-mainnet beacon-roots
  address, a `SystemCall` from `SystemUser`, exactly 32 bytes of parent
  beacon-root calldata with every element byte-bounded, zero value and gas
  price, a 31,566,720 gas limit that splits into 30,000,000 execution gas and
  the 1,566,720 state reservoir,
  `Execute`/commit options, the exact address-only access list with no storage
  keys, and `NullTxTracer`. The handler deliberately ignores an ordinary
  production `TransactionResult`, but the Lean adapter never converts its own
  fail-closed `unmodeled` disposition into `applied`. A code-bearing target is
  bound to an EVM-body observation and a code-empty target to a simple-transfer
  observation; contradictory target/body facts fail shape validation. The
  block shape also requires the processor's tracer-consistency predicate, so
  neither pinned internal call admits state tracing.
- EIP-7002, EIP-7251, and the two EIP-8282 request reads use `SystemCall`
  instances from `SystemUser`, their exact four pinned predeploy addresses,
  empty calldata, zero value and gas price, the 31,566,720 gas limit,
  `Execute`/commit options, and a `CallOutputTracer`. Their request-type bytes
  are exactly 1, 2, 3, and 4 and are proved byte-bounded. Missing target code
  or tracer failure invalidates the block; empty tracer return data adds no
  request; nonempty tracer return data is prefixed by the request-type byte.
- The pinned historical-blockhash path writes storage directly through
  `BlockhashStore`; it does not invoke `SystemTransactionProcessor`, so the
  adapter rejects it as unmodeled.

The theorem `exact_block_call_sites_do_not_append_normal_receipts` is an
adapter theorem. Receipt-list and cumulative-receipt-gas exclusion are not
owned by `SystemTransactionProcessor`; they follow because `BlockProcessor`
passes a null/non-receipt tracer to these calls and builds its ordinary receipt
array only from `ProcessTransactions`. In contrast,
`system_processor_never_charges_normal_block_counters` describes the
processor-owned `SkipValidation` gate on the header and EIP-8037 block gas
counters.

`CallOutputTrace.initial` models the newly-created tracer's failure-status
zero and empty return value. Finalization replaces it with the values supplied
to `MarkAsSuccess`/`MarkAsFailed`. Request interpretation reads only this
tracer projection; it never reads the model's direct VM result, receipt-status,
or returndata fields.

The block adapter checks the exact pinned processor domain before call-shape
validation. `unpinned_block_call_fails_before_shape` proves this ordering and
`block_call_never_applies_unmodeled_processor` proves that a fail-closed
processor result cannot become an applied block call.

This is deliberately a leaf adapter. `blockIntegrationStatus` is
`leafOnlyUnconnected`: there is no import of, composition theorem with, or
ordering theorem about `BlockReference`. Connecting beacon execution before
ordinary transactions and execution requests after them remains open work.

## Production anchors

All anchors below are relative to the pinned revision.

- `src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs`
  - `Process`, lazy processor creation, and routing: lines 158-198.
  - base header/spec/intrinsic entry and common execution: lines 200-263.
  - EVM snapshot/execute/restore/finalize path: lines 292-424.
  - simple-transfer state charge/value movement/finalize path: lines 429-524.
  - normal header and multidimensional counter gate: lines 587-615.
  - final transaction fields, reset/commit, and receipt trace: lines 619-699.
  - inherited static validation: lines 895-1013.
- `src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionProcessor.cs`
  - suppression, hook, option/value decision: lines 55-64.
  - zero purchase, system spec, validation/nonce/fee/value overrides: lines
    67-94.
  - `SystemCall` intrinsic and available gas versus ordinary fallback: lines
    96-118.
  - conditional sender recovery and refund bypass: lines 121-128.
- `src/Nethermind/Nethermind.Core/TransactionExtensions.cs`: `IsSystem`, lines
  10-16.
- `src/Nethermind/Nethermind.Specs/ReleaseSpecExtensions.cs`: system-spec
  dispatch, lines 8-28; concrete `ReleaseSpec` selects `SystemSpec` before the
  genesis fallback.
- `src/Nethermind/Nethermind.Specs/ReleaseSpec.cs`: cloned system spec with
  EIP-158 disabled, lines 232-241.
- `src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs`: system
  intrinsic and available-gas construction, lines 55-79, and ordinary
  Amsterdam intrinsic composition, lines 774-893.
- `src/Nethermind/Nethermind.Evm/ReleaseSpecExtensions.cs`: EIP-3860
  transaction-initcode word charge, lines 18-23.
- `src/Nethermind/Nethermind.Evm/IntrinsicGasCalculator.cs`: calldata tokens,
  EIP-8038 access-list prices, and EIP-7976 floor calculation, lines 44-122.
- `src/Nethermind/Nethermind.Evm/TransferLog.cs`: exact EIP-7708 transfer-log
  emitter, signature, indexed from/to addresses, and amount, lines 10-35.
- `src/Nethermind/Nethermind.Evm/VirtualMachine.cs`: top-level frame uses the
  derived execution environment's `From`/`To` for its EIP-7708 transfer log,
  lines 226-234; the precompile path does the same at lines 915-925.
- `src/Nethermind/Nethermind.Core/Eip8037Constants.cs`: fixed system-call base,
  reservoir, and total gas constants, lines 7-19.
- `src/Nethermind/Nethermind.Blockchain/BeaconBlockRoot/BeaconBlockRootHandler.cs`:
  eligibility/account-existence gates and `SystemCall` construction,
  lines 17-78.
- `src/Nethermind/Nethermind.Consensus/ExecutionRequests/ExecutionRequestsProcessor.cs`:
  four `SystemCall` instances, lines 28-82, and request execution/result
  interpretation, lines 231-257.
- `src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs`: beacon
  root before user transactions and execution requests after user receipts,
  lines 155-203.
- `src/Nethermind/Nethermind.Blockchain/Blocks/BlockhashStore.cs`: direct
  storage update, lines 23-29.
- `src/Nethermind/Nethermind.Blockchain/Tracing/CallOutputTracer.cs`: receipt
  tracing flag and success/failure status and return-data projections, lines
  11-48.

## Proof and vector surface

The model proves option rewriting, concrete-system-spec EIP-158 behavior, the
fixed gas partition, the pinned 30,000,000/1,566,720 split, zero purchase and
normal block-counter effects, signed-64-bit fit for both internal gas limits,
nonce identity, request-type byte bounds, and the exact block-adapter
receipt/cumulative-gas obligation. Gas-purchase and nonce results are explicit
outputs of an injectable `OverrideKernel`. Production `run` supplies
`buyGasOverride` and `incrementNonceOverride`; the
`completed_or_reached_execution_uses_bypassed_overrides` theorem proves every
completed execution, and every execution that reached the override stage,
returns `.bypassed` purchase and `.bypassed originalNonce`. Universal
projection theorems prove every production `run` result observes zero premium,
reserved payment, blob fee, and nonce delta. `runCoreWithOverrides` invokes the
two supplied functions in sequence on the evolving world and threads their
worlds and continuation decisions into execution. Mutation vectors therefore
execute genuinely charging, incrementing, and early-stopping operations and
observe changed state and control flow rather than literal sentinel values.

Contract creation is derived from an absent destination and sender/recipient
equality is derived from their addresses; neither is an independently supplied
boolean. The beacon call takes the `SystemCall` intrinsic path, so ordinary
transaction intrinsic gas is deliberately ignored. Its executable vector pins
the exact 31,566,720 split into 30,000,000 execution gas and a 1,566,720 state
reservoir.

Independent 0-, 1-, 32-, and 33-byte creation vectors pin EIP-3860 word
rounding and demonstrate that the initcode term affects execution gas only.

`SystemTransactionReferenceVectors.lean` executes success, REVERT, exception,
top-frame out-of-gas, simple transfer, pre-value state-gas failure, missing
sender, skipped nonce-overflow validation, exact fallback routing, unsupported
engine, exact `BuildUp`, `CallAndRestore`, warmup transient reset and gas-field
suppression, receipt tracing, successful/failed destroy-list finalization,
authorization-list fail-closed behavior with an adversarial partial-auth OOG
oracle, the EIP-7708 simple-transfer log, successful positive-value creation
with its executing-address recipient, successful zero-value and self-send
no-log cases, reverted/failed/top-frame-OOG/state-OOG creation and state-OOG
transfer no-log cases,
receipt/state-tracer fact contradictions, code-bearing and code-empty beacon behavior, target/body
mismatch rejection, all four request call shapes,
empty/missing/failed request behavior, and historical-blockhash rejection.
The exact-shape mutation suite independently changes gas limit, value, gas
price, destination, calldata, access-list contents, intrinsic components,
sender, transaction kind, options, tracer, and request type. The behavioral
mutation suite negates routing, option,
intrinsic-gas, rollback, authorization, causal gas-purchase/nonce execution,
each transfer-log field and no-log guard, tracer consistency, finalization,
block-counter, receipt, request-failure, and call-site guards. Its two `all`
theorems prove that every listed mutant changes an observable.

The current isolated surface contains 27 named model theorems and 76 executable
vector examples. The two aggregate mutation theorems cover 28 exact-shape and
47 behavioral mutants (75 total).

Destroy-list modeling carries concrete `(address, balance)` entries. Successful
pinned EIP-7708/EIP-8246 finalization records deleted addresses, recreates the
nonzero-balance accounts in `preservedDestroyedBalances`, and emits no burn
logs; failed execution performs none of those effects. Vectors mutate both the
preserved state and log outcome.

## Explicit limitations

- VM execution, simple-transfer recipient effects, settlement arithmetic,
  returndata, ordinary VM logs, and intermediate VM worlds
  are typed abstract observations. Consistency guards bind world inputs and
  rollback, but these oracles are not proofs about the VM or settlement code.
  The top-level EIP-7708 log is modeled separately as an exact abstract
  projection of the production `LogEntry` fields used by this path. For
  creation, the production-derived executing address remains an explicit
  observation; this model checks its presence and log use but does not prove
  CREATE-address derivation.
- The model does not extract C#, prove CLR/JIT behavior, or prove that the
  handwritten event labels are bisimilar to production. It therefore must not
  be described as formal verification of Nethermind.
- Only the pinned standard-mainnet `ReleaseSpec` route is modeled. AuRa,
  Optimism, decorators/test specs, plugins, parallel execution, tracing detail,
  pre-EIP-8037 forks, and exception throws outside explicit result values fail
  closed or are out of scope.
- Authorization processing is deliberately absent; every authorization-list
  system transaction returns `unmodeled` before execution.
- The adapter theorem does not prove `BlockProcessor` refinement, receipt-root
  correctness, execution-request hashing, deposit-log parsing, withdrawal or
  reward processing, world-state persistence, call ordering, or block validity
  as a whole. There is no connection to `BlockReference`.
- Current call-site enablement and target account/code presence are abstracted
  into `enabled`, `targetExists`, and `targetHasCode`; the fork predicates and
  state lookup remain block-adapter preconditions. Addresses and transaction
  shapes themselves are pinned and checked exactly.

## Verification

From `tools/Evm/Lean` with the pinned Lean toolchain:

```text
lake build Eip803x.SystemTransactionReference Eip803x.SystemTransactionReferenceVectors
```

The sources intentionally contain no `sorry`, `admit`, or `axiom` declarations.
