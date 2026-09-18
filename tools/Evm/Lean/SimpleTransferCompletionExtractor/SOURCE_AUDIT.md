# Source audit

The extractor pins 63 source files in `Extractor.cs` (62 live production files plus the checked-in
compiler-only VM adapter). The selected closure admits the target's source identity/order surface,
gas-policy and settlement identities, world/tracer request signatures, transaction/header/substate/
log values, access guards, metrics, the `GasCostOf.NewAccountState` schedule constant, and the
standard DI registrations. It does not claim that those identities compose the production bodies
into the generated model.

The standard route is checked from `BlockProcessingModule.Load`:

* `ITransactionProcessor -> EthereumTransactionProcessor`;
* `IWorldState -> WorldState -> WorldStateExtensions`;
* `ICodeInfoRepository -> CacheCodeInfoRepository`; and
* `EthereumTransactionProcessorBase -> TransactionProcessorBase<EthereumGasPolicy>`.

The source audit explicitly lists system, EVM, parallel, XDC/Taiko, BAL, receipt-folding/root,
DB/trie, CLR, cryptographic hashing, and callback exception behavior as exclusions. Source hashes
are intentionally strict for the public extractor. The test-only entry point is the only way a
fixture mutation may be rebound. The Roslyn closure is compiled as source-correspondent units
against the exact hash-pinned metadata/package inventory; the VM body is admitted only through the
signature source, the checked-in empty adapter, and independent metadata identity checks.

Each selected node is retained as a closed Roslyn `IOperation` tree with resolved symbols. The tree
is fingerprinted into binding metadata, and each admitted invocation records its target signature,
receiver, return type, ordered parameter names/types/ref-kinds, and recursively lowered argument
expressions. These facts enforce admission and mutation rejection. The emitted Lean formulas remain
closed handwritten model formulas; they are not source-body lowering for `PayValue`, settlement,
fees, finalization, world-state mutation, tracer dispatch, or callback exceptions.

The live `ExecuteEvmCall` `CompleteWithoutFrame` label is bound as a typed no-frame `ReportAccess`
reachability edge. It is an admission fact only; the simple-transfer model does not execute VM code.
The source order includes `PayValue` before the recipient balance request. The four
`CompleteWithoutFrame` edges and the `FailContractCreate -> Complete` bypass are checked in the
source CFG, and CFG failure rejects extraction.

The model-to-model refinement exposes fixed-width no-wrap premises and consumes the semantic,
byte-pinned state-charge/settlement kernel projections as model inputs. The OrdinaryPost artifacts
listed above are identity-only handoff records and are not semantic imports. World reads/writes and
before/after state roots are opaque normal-return request-boundary values, not proved live adapter
results. `ReportAccess` is represented extensionally by address/storage-cell membership; the
production `HashSet` iteration order is excluded while event positions remain ordered. Transfer
topics are derived by the generated boundary's bounded `addressHashProjection` from source-bound
sender and recipient. This abstracts `Address.ToHash().ToHash256()` without proving Keccak
correctness; emitted data/signature/address fields are derived by the generated transfer-log
request from the source-bound transaction value. The result boundary retains the model's status and
receipt projection only; complete CLR exception/description details and callback throw/prefix
behavior are outside the claim. The universal theorem's explicit `normalReturnDomain` premise
(`TraceAdapter.normalReturn = true`) excludes callback-exception prefixes.

The live transaction-processor hash is
`0374c6f35a9a23361f37b41162ac281ca83b09846ab4295d5a592db6315c99cc`. The bounded differential
audit confirmed one production defect, fixed in the current live source: the EIP-8037 state-charge
OOG no-frame completion path omitted `ReportAccess`. No additional production defects were
confirmed.
