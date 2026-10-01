# Source-attached simple-transfer remediation

This is a static implementation checkpoint, not an acceptance record. The current generated
artifacts and refinement remain outside the accepted proof set.

## Audit result

The live standard-mainnet path has the required semantics, but the current package proves only a
generated-model/reference-model relation. No additional production defect was found during this
audit.

The checked-in package is not internally current:

* `Extractor.cs` now requires acceptance state
  `hash-pinned-audited-handwritten-model-to-model-request-boundary-only`, while the checked-in IR
  and manifest still contain `bounded-source-extraction-and-refinement-only`.
* The manifest records generated-Lean SHA-256
  `ba3c9b74b7c63fffe2b7e7a276b80cb135eb5bfb7a866094fae53ed82838bdb6`, but the checked-in Lean
  file is `d2a82cd6e4704f066c32296e8a6b453b87849cded6adc84cf84574cbffc94844`.
* The current emitter defines `addressHashProjection`, derives transfer topics/data, and gives
  `TraceAdapter` a `normalReturn` field. The checked-in generated module still consumes free
  transfer-payload fields and has no such `normalReturn` field, while the authored refinement
  already refers to the new declarations.
* The replacement theorem must bind the production address representation: an address is below
  `2^160`, and `Address.ToHash().ToHash256()` zero-left-pads its 20 bytes into the 32-byte topic.
  This conversion is not a Keccak operation; the current scalar projection has no encoding proof.
* The manifest labels the `OrdinaryPostNonceDispatchExtractor` triplet as accepted even though that
  package is explicitly outside the accepted proof set. Its hashes are current, but its theorem is
  not an admissible composition step.

There is no direct `native_decide`, `sorry`, `admit`, `axiom`, or `opaque` in the authored/generated
Lean searched for this package and its state-charge, accepted refund, and accepted receipt
dependencies. That search is insufficient evidence: the current gate has no complete transitive
module/declaration census, does not reject `native_decide` or `opaque`, and does not inspect every
loaded declaration's axiom set.

`AdapterCoherent` is not literally circular because it does not equate `Generated.run` with its
expected result. It is nevertheless oracle-shaped and redundant: caller-supplied `stateCharge` and
`preRefundGas` fields are equated to values that the generated transition already computes.
`sourceIdentityCoherent` is a tautology unused by `universal_refinement`, and
`receipt_continuation_preserved` is only a definitional projection. None attaches the result to the
production method. Remove these fields and propositions rather than carrying them into the source-
attached theorem.

## Smallest defensible claim

The first source-attached theorem should cover the standard sequential commit-only Amsterdam
simple-transfer continuation beginning immediately after a successful nonce increment and ending
after `FinalizeTransaction` and the base receipt terminal. It should not wait for or import the
unaccepted post-nonce dispatch theorem.

The theorem may assume the earlier validation, gas purchase, and nonce update returned normally,
but it must derive the remaining caller route from the live `Execute(6)` source. Its boundary is:

* `ExecutionOptions.Commit` exactly; no restore, warmup, build-up, skip-validation, or parallel
  execution;
* ordinary `EthereumTransactionProcessor` with `EthereumGasPolicy`, `WorldState`, and
  `CacheCodeInfoRepository` standard-mainnet registrations and no relevant overrides;
* pinned Amsterdam flags for EIP-8037, EIP-8038, EIP-7708, EIP-658, EIP-3529, EIP-7778, and
  EIP-1559; hot/cold storage, transaction access lists, and coinbase warming use their pinned values;
* base sequential receipt tracer with receipt tracing enabled and equal receipt/gas-history lengths;
* normal, non-reentrant world/tracer hooks with explicit heap noninterference and borrowed-lifetime
  obligations. A log callback must not mutate the `LogEntry` or its mutable topic/data arrays before
  receipt construction, and an access callback must consume rather than retain tracker-backed
  collections that disposal immediately clears. Repeated tracer/spec/transaction projections are
  stable across the continuation, and the borrowed `tx.ValueRef` alias observes the same unchanged
  value at every use. CLR callback exceptions and partial callback prefixes remain explicit external
  assumptions.

The claim is equality of the extracted continuation and an independent specification for status,
transaction gas fields, ordered world/tracer requests, execution/state gas, sender refund, fees,
the two distinct processor counters, the distinct receipt-tracer counters/receipt, and the returned
`TransactionResult`. It is not a world-state/trie implementation theorem: the observations of
`IsDeadAccount`, balance mutation hooks, commit, address/hash/byte conversion, and callbacks retain
typed provenance assumptions.

## Caller provenance and exact guards

Lower these nodes from the real Release `Nethermind.Evm` compilation, not from operation labels:

1. `restore = opts.HasFlag(Restore)` and the exact effective-commit expression.
2. `PrepareSimpleTransferFastPath`, including all candidate guards:
   `tx.To != null`, `!_isCodeOverridable`, `tx.AuthorizationList is null`, and
   `!ForceSimpleTransferDisabled`.
3. `_codeInfoRepository.GetCachedCodeInfo(recipient,
   followDelegation: !spec.IsEip8037Enabled, spec, out delegation)` returning normally.
4. `delegation is null && codeInfo.IsEmpty` and the exact selected recipient.
   `CodeInfo.IsEmpty` is the pinned analyzer-sentinel identity, not merely `Code.Length = 0`; retain
   that representation or consume an explicitly typed lookup/sentinel premise.
5. `commitBeforeExecution = commit &&
   (simpleRecipient is null || restore || tracer.IsTracingState)`. At the selected boundary this is
   false only under the explicit initial-scope premise `tracer.IsTracingState = false`, so no
   pre-execution commit occurs. A later widening must lower the state-tracing branch as an ordered
   external `Commit` request instead of inferring that receipt tracing disables state tracing.
6. successful `CalculateAvailableGas` and the exact `gasAvailable` out value, derived through
   `Eip803x.Refinement.TransactionGasInitialization.generatedTryCreate_refines_initializeTransactionGas`
   under its `RefinementValid` domain. The selected simple-transfer candidate requires
   `AuthorizationList = null`; the pinned intrinsic-gas calculation therefore supplies a zero
   `Standard.StateReservoir` baseline. Because intrinsic-gas calculation precedes this boundary,
   retain that zero as an explicit source-provenance obligation and use the accepted theorem's
   zero-state specialization. Access-list cost is execution gas, not intrinsic state gas.
7. the `simpleTransferRecipient is not null` arm and every argument/ref-kind passed to
   `ExecuteSimpleTransfer`.

The executable slice begins at the control-flow continuation after the successful
`IncrementNonce` result, not at a later declaration chosen by syntax. Every intervening effect must
be lowered; adding an effect before `simpleTransferRecipient` must not move it into an excluded
prefix. Scope facts used to prune restore/commit branches must also reject indirect writes through
tuple/deconstruction, compound/increment, and writable `ref`/`out` targets.

A `CallerBoundary` should state only external prefix provenance and source-derived identities. It
must not accept a preassembled `Handoff`, a completed result, or a premise equating a caller output
to the continuation output.

## Generated transition

Use one source-lowered plan for `ExecuteSimpleTransfer`, `Refund`,
`UpdateHeaderGasUsedAndPayFees`, `PayFees`, and the selected commit/receipt parts of
`FinalizeTransaction`. Generated definitions remain theorem-free.

The Stage-A typed statement plan is admission and change-detection evidence, not executable IR.
Its generic typed AST deliberately omits details needed to interpret arbitrary C#, including object
initializers, user-defined operator identities, checked/lifted conversion details, and variable-
declarator identity. Stage B must build a separate closed executable plan directly from the live
`IOperation`/CFG graph, classify every callee as a locally lowered helper, an accepted generated
stage, or an explicitly typed external request, and reject every unsupported operation kind.

The local helper closure includes `PayValue`, `TraceSimpleTransferActionStart`,
`ReportSimpleTransferAccess`, `WarmUpTxAccesses`, the used gas/counter accessors, the selected
`TransactionSubstate`, `TransferLog`, `LogEntry`, and `TransactionResult` constructors/projections,
and the selected world-state forwarding bodies. The refund stage already owns `PayRefund`,
`ShouldRefundGas`, validation/refund helpers, and settlement arithmetic; the receipt stage owns its
terminal fold. Do not duplicate those accepted bodies. A fresh `StackAccessTracker` rents reusable
state, so access-tracker freshness/clearing and disposal require either a lowered pool/collection
contract or a consumed typed external invariant; `new StackAccessTracker` alone does not establish
an empty tracker.

Static-interface gas-policy calls must resolve against the candidate `EthereumGasPolicy`, including
candidate implementations that replace a default-interface member; baseline-only dispatch is not
admission evidence. Selected readonly/static values must likewise close every relevant type-
initializer write. A field initializer plus a premise that type initialization completed does not
prove the final field value when an explicit static constructor can overwrite it.

Preserve object provenance as well as equal field values. Recipient/coinbase warmup reads the
virtual machine context header, fees read the processor's `header` argument, and receipt folding
mutates the receipt tracer's block header; the selected domain needs their exact alias relation.
`BlockHeader.GasBeneficiary` is `Author ?? Beneficiary`, not a primitive beneficiary input.

The initialization adapter still owns its outcome test and five-field gas object initializer around
the accepted initialization kernel. The state-charge adapter likewise owns the failure return and
five successful `ref` copy-backs around the accepted charge kernel. Those adapter effects must be
lowered locally even though the arithmetic stages are imported.

### State charge and exceptional completion

Derive the charge guard exactly:

```text
IsEip8037Enabled && value != 0 && sender != recipient && IsDeadAccount(recipient)
```

The charge amount is the source-selected `IGasPolicy.GetNewAccountStateCost()` / production
`GasCostOf.NewAccountState` value. Invoke the generated production state-charge kernel directly;
there is no caller-supplied charge response. Compose its result with
`Eip803x.Refinement.StateGasCharge.generatedTryCharge_refines_chargeState` under explicit
representation, well-formedness, and no-overflow premises.

On an uncovered charge:

* the failed kernel result preserves every gas field;
* action-start tracing observes the pre-clear remaining gas;
* only execution gas is then cleared; reservoir, used, spill, and spill-refunded fields are retained;
* no sender/recipient value request and no transfer log occurs;
* the substate has refund zero, no destruction, empty output/logs, `ShouldRevert=false`,
  `IsError=false`, and `EvmExceptionType.OutOfGas`;
* ordinary refund processing still runs. This is deliberately not the EIP-8037
  `CompleteEip8037Halt` branch because `TransactionSubstate.IsError` is false;
* status is failure and the returned result is the typed EVM exception.

### Value, self-send, logs, and access

For a non-self successful path, issue `PayValue` only for nonzero value, followed by
`AddToBalanceAndCreateIfNotExists`. A zero-value non-self call still issues the latter request with
zero. A self-send issues neither request, performs no dead-account query/charge, and emits no EIP-7708
transfer log.

Derive the EIP-7708 log only when enabled, value is nonzero, the recipient differs from the sender,
and the charge did not fail. Its address is `Address.SystemUser`, topics are the fixed Transfer
signature followed by the source-bound sender and recipient projections, and data is the 32-byte
big-endian transaction value. `ReportLog` is separately guarded by `IsTracingLogs`.

Action tracing must preserve source order: action start and optional empty bytecode, execution-gas
clear on OOG, then action error or action end. Access tracing constructs a fresh tracker and compares
address/storage carriers extensionally after the exact access-list, coinbase, recipient, and sender
warmups; event position remains ordered.

### Refund, fees, counters, and receipt

Construct the accepted refund input; do not call the raw settlement kernel or accept any refund
output as input:

* `entry = ordinaryRefund`, `isContractCreation=false`,
  `topLevelCreateStateGasCharged=false`;
* incoming gas is the post-charge/post-clear local gas;
* refund counter, destruction count, and code-insert refund count are zero;
* `isError=false` and `shouldRevert=false`, including state-charge OOG;
* original intrinsic standard/floor gas and post-charge state reservoir are passed exactly;
* EIP flags, price fields, and fixed-width values come from the caller/spec boundary.

Apply
`OrdinaryTransactionRefundAdapterExtractor.Refinement.OrdinaryTransactionRefund.source_attached_refines`
at that derived input. Sender credit and all six gas fields must be projections of the accepted
result.

Keep processor counters and receipt-tracer counters as different stores. For the selected ordinary
sequential path, update processor execution by `EffectiveBlockGas`, processor state by
`BlockStateGas`, and header gas by their maximum. Build the receipt entry from the distinct receipt
state and apply
`ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold.generatedFinalizeTransaction_refines_spec`
at the derived gas/finalize input. Require the same uint64 sum bounds and history/index invariants as
the accepted ordinary-EVM completion package. The pinned EIP-658 boundary makes the receipt state
root `none`; do not retain opaque before/after root inputs.

Fee arithmetic should use the same modular UInt256 specification as accepted
`OrdinaryEvmCompletion`: premium times paid gas, capped effective base fee, free-transaction guard,
optional EIP-1559/blob collector credit, and the fee trace's base-plus-blob burnt observation. For
this substate the destroy list is empty, so the beneficiary credit is always issued. Transaction
`BlockGasUsed` and `SpentGas`, commit with `commitRoots=false`, receipt status/error/logs, and the
returned EVM exception/OK value are derived, never supplied.

## Theorem shape

Mirror the accepted ordinary-EVM composition without claiming its VM route:

```lean
structure SourceWitness where
  sourceClosure : String
  sourceIr : String

structure ExternalPredicates where
  postNonceEntryProvenance : Input -> Prop
  codeLookupNormal : Input -> Prop
  deadAccountObservation : Input -> Prop
  normalWorldAndTracerHooks : Input -> Prop
  addressBytesHashAndPriceRepresentation : Input -> Prop
  uint256PrimitiveSemantics : Input -> Prop

structure Domain (predicates : ExternalPredicates) (i : Input) : Prop where
  caller : CallerBoundary i
  representation : Representation i
  counters : CounterRanges i
  refund : (derivedRefundSpecInput i).Valid
  external : ExternalAssumptions predicates i

structure AcceptedStages (i : Input) : Prop where
  initialization : InitializationStage i
  stateCharge : StateChargeStage i
  refund : AcceptedRefundAtDerivedInput i
  receipt : AcceptedReceiptAtDerivedInput i

structure SourceAttached (source : SourceWitness) (i : Input) : Prop where
  closureIdentity : source.sourceClosure = Generated.sourceClosure
  irIdentity : source.sourceIr = Generated.sourceIr
  continuation : Generated.evaluate (mapInput i) = Specification.evaluate (mapInput i)
  stages : AcceptedStages i
```

`source_attached_refines` should construct `AcceptedStages` from the initialization, state-charge,
refund, and receipt theorems. No domain field may mention `Generated.evaluate i`, the final result,
or an equality to the expected output. A separate field-projection theorem should expose status,
gas, processor counters, receipt counters/receipt, fee requests, exact returned-result fields, and
event ordering. In particular, the state-charge-OOG result has receipt failure while retaining
`TransactionResult.Error = none`, `EvmExceptionType.OutOfGas`, an empty error description, and
`TransactionExecuted = true`; the production result equality intentionally does not distinguish all
of those fields and is not the theorem's observational equality.

## Complete source and compiler closure

Replace the 63-file, separately compiled semantic groups, synthetic empty VM adapter, and
`Nethermind.Init` output-directory scan. Reuse the accepted Release compiler-closure mechanism used
by `OrdinaryEvmCompletionExtractor`: the complete real `Nethermind.Evm` source selection (currently
157 pinned identities / 146 compiled trees), exact defines and compilation properties, and the
frozen selected metadata inventory (currently 226 references). Reject additions, removals,
unselected duplicates, SHA/MVID drift, compilation errors, candidates/ambiguity, invalid operations,
and recursive `IErrorTypeSymbol` occurrences. Bind standard processor lineage and absence of
overrides. Pin selection files and the standard-mainnet DI registration separately, or compile its
real project closure; do not satisfy production symbols with extractor-owned declarations.

The source-lowering vocabulary must cover the complete claimed CFG and expressions. A coordinated
source/pin/IR change to `PayValue`, refund, fee, counter, finalization, or receipt input logic must
change generated semantics or fail closed. Merely binding a call and selecting a handwritten formula
by operation ID is not source-level attachment.

Pin the accepted artifacts actually composed:

* transaction gas initialization IR/manifest/Lean:
  `a48a5bf063439cd9a21f4eee0e9b2509dfe07a4fa98f075aa5430db59f900f52`,
  `c4081417da301771539d0509bf281a42f40a1a719cc4611c051e60fc8d6c7202`,
  `83ce996d6dcc93a588114e73737a25a14cb21689e9e8900e6dc68a07fb170b6f`;
* state-gas charge IR/manifest/Lean:
  `6e2fc2cf4904f95bf017a22056ce55da9c2d518c5ee5af759ebf2e380d88b690`,
  `bc62e03880343c8cd27dedd238fa386125a412e112548bec70d4a3bba4a0e575`,
  `d6a09be29c449e3f005d84cde988a619e03c4d2b0a69fcf0989f879b2fc87337`;
* ordinary refund IR/manifest/Lean:
  `b198837b7f2aaddd2f3d7c8653532d7a0ebc58dfd9566e6d7459121a0c051527`,
  `a0dd398a94994b65c3ea6bc59b6d62cd77958025208c423237e14a44d1ce30a1`,
  `367d0e3b71860d2bdb572cc0e9e7fd1951e02625b64200650e35e5085ca7b059`;
* receipt terminal IR/manifest/Lean:
  `0bf061d43d54e3eb9bdc7ccff4d0541d6dee189ac1ccea7f139095d3e1b7945a`,
  `c1637a38190eb1fdd74b8d0fccaa52f7151d9e8dfc657c5844b27cb8fd1afa6c`,
  `028d11a14e031aaedce27c71b26e2c69cf16e759ff3ac870373ead10f6971180`.

Also pin the four refinement sources composed by the new proof: transaction initialization
`8986504ad786cc621dcb0c74400980366847c43d832830973f52a5d38c290578`, state charge
`4cb359f611ef04300618f9109ac89ca6b32c857c8e9e6c2305e11a0323d57ead`, ordinary refund
`8f0a67d6da67a3068cc96cba94f77df24ad3a3dfe07b13b22bc21ec6ed76a13d`, and receipt terminal
`bd4a3556a98ec40875960ff2180523d16221b05cd6cf9a0b6ce1fc46a8cb3e93`.

The ordinary-EVM package is a comparison and reusable shape, not a dependency or semantic shortcut
for the simple route: its accepted domain requires present executable code, whereas this route
requires `HasNoExecutableCode`. Remove the `OrdinaryPostNonceDispatchExtractor` dependency until it
is independently accepted; direct caller lowering above replaces it.

## Required gates

Add strict IR, manifest, source-map, compiler-inventory, and stage-dependency schemas with
`additionalProperties:false`, exact versions/counts/enums, path ordering, and hash formats. Test
missing, extra, reordered, wrongly cased, wrong-typed, and tampered fields. Publish artifacts
atomically with the manifest last, generate twice at a pinned epoch, compare bytes, and recheck the
live sources after generation.

The semantic mutation matrix must cover at least:

* every candidate/code/delegation guard, `followDelegation`, precommit, available-gas result, dispatch
  arm, argument, receiver, ref-kind, and order;
* all four state-charge predicates, the schedule value, ref mutation, result negation, failure-state
  preservation, action-start-before-clear, clear target, and substate error/exception distinction;
* self-send, zero/nonzero PayValue, sender-before-recipient order, zero recipient credit, all transfer-
  log guards, address/signature/topic order, big-endian data, and `ReportLog`;
* every derived refund field, especially OOG `isError=false`, post-clear incoming gas, original floor,
  zero refunds/destructions/code inserts, payment guard, and sender credit;
* processor execution/state stores, `EffectiveBlockGas`, `BlockStateGas`, max header, normal-counter
  routing, fee cap/free/blob/collector/trace behavior, transaction gas fields, commit, status, and
  returned result;
* receipt state/gas/finalize mappings, sequential guard, history/index invariants, cumulative paid
  gas, separate receipt execution/state totals, logs/error, and EIP-658 root absence;
* coordinated mutations that also update source hashes, reviewed-binding hashes, and serialized IR,
  proving semantic lowering rather than identity-only rejection.

Executable vectors should cross zero/nonzero value, self/non-self, dead/live recipient, no/exact/
partial/insufficient reservoir, action/log/access/fee tracing, price/refund gates, free/blob/collector
cases, empty/nonempty receipt history, and exact/overflow-by-one counter bounds. Include exact event
order for the successful and state-charge-OOG paths.

Finally, freeze the complete transitive Lean module and declaration inventory. Reject missing or
unexpected modules/declarations, local/private/equation-name omissions, `opaque`, and every axiom
outside `propext`, `Classical.choice`, and `Quot.sound` by running `Lean.collectAxioms` on every
loaded declaration. Include a `native_decide` negative control, theorem export inventory, imported
declaration inventory, direct warning-as-error targets, semantic mutants, and the package/aggregate
gates. A token scan alone is not an axiom audit.
