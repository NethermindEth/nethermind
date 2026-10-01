# Source audit

The current extractor observes 61 production source projections and compiles one real Release
`Nethermind.Evm` closure using the accepted Refund inventories: 157 source identities, 146 compiled
trees, and 226 selected metadata references. The compiler roster and source projections are
recorded separately, and `sourceClosureSha256` covers both effective rosters and their reference
identities. This compiler migration alone does not establish a production refinement claim. It admits the
target's source identity/order surface and the exact `Execute/6` to `ExecuteSimpleTransfer/15` caller route,
gas-policy and settlement identities, world/tracer request signatures, transaction/header/substate/
log values, access guards, metrics, the `GasCostOf.NewAccountState` schedule constant, and the
standard DI registrations. It does not claim that those identities compose the production bodies
into the generated model.

The standard route is checked from `BlockProcessingModule.Load`:

* `ITransactionProcessor -> EthereumTransactionProcessor`;
* `IWorldState -> WorldState -> WorldStateExtensions`;
* `ICodeInfoRepository -> CacheCodeInfoRepository`; and
* `EthereumTransactionProcessorBase -> TransactionProcessorBase<EthereumGasPolicy>`.

The concrete processor is sealed, neither it nor its wrapper shadows admitted completion helpers,
and their `ITransactionProcessor.Process/3` interface mapping resolves to the inherited generic
`TransactionProcessorBase<EthereumGasPolicy>.Process` source declaration. This rules out an
explicit-interface bypass of the observed Execute route.

The source audit explicitly lists system, EVM, parallel, XDC/Taiko, BAL, receipt-folding/root,
DB/trie, CLR, cryptographic hashing, and callback exception behavior as exclusions. Source hashes
are intentionally strict for the public extractor. The test-only entry point is the only way a
fixture mutation may be rebound. The Roslyn closure is the real EVM source set under the exact
hash-pinned Release metadata inventory, including the real VM declaration. The former empty VM
adapter is removed. Core constants and Init registrations are exact external source projections;
State forwarding bodies are normal-return hook premises, not stand-alone compiled semantic models.

Each selected node is retained as a closed Roslyn `IOperation` tree with resolved symbols. The tree
is fingerprinted into binding metadata, and each admitted invocation records its target signature,
receiver, return type, ordered parameter names/types/ref-kinds, and recursively lowered argument
expressions. These facts enforce admission and mutation rejection. The emitted Lean formulas remain
closed handwritten model formulas; they are not source-body lowering for `PayValue`, settlement,
fees, finalization, world-state mutation, tracer dispatch, or callback exceptions.

The live `ExecuteEvmCall` `CompleteWithoutFrame` label and its four gotos are bound as exact
source label branches, with ordered guards/effects and the `FailContractCreate -> Complete`
bypass. `NoFrameCfgProven=false`: no CFG path/edge is asserted for these gotos because Roslyn
does not expose an unambiguous edge at those sites. The source order includes `PayValue` before
the recipient balance request. The simple-transfer model does not execute VM code.

The model-to-model refinement exposes fixed-width no-wrap premises and consumes the semantic,
byte-pinned state-charge/settlement kernel projections as model inputs. World reads/writes and
before/after state roots are opaque normal-return request-boundary values, not proved live adapter
results. `ReportAccess` is represented extensionally by address/storage-cell membership; the
production `HashSet` iteration order is excluded while event positions remain ordered. Transfer
topics are derived by the generated boundary's scalar `addressHashProjection` from source-bound
sender and recipient. Production `Address.ToHash().ToHash256()` zero-left-pads the 20-byte address
to 32 bytes; the current model does not yet prove the required `UInt160` bound or byte-encoding
bridge. Emitted data/signature/address fields are derived by the generated transfer-log request
from the source-bound transaction value. The result boundary retains the model's status and
receipt projection only; complete CLR exception/description details and callback throw/prefix
behavior are outside the claim. The universal theorem's explicit `normalReturnDomain` premise
(`TraceAdapter.normalReturn = true`) excludes callback-exception prefixes.

The live transaction-processor hash is
`0374c6f35a9a23361f37b41162ac281ca83b09846ab4295d5a592db6315c99cc`. The bounded differential
audit confirmed one production defect, fixed in the current live source: the EIP-8037 state-charge
OOG no-frame completion path omitted `ReportAccess`. No additional production defects were
confirmed.
