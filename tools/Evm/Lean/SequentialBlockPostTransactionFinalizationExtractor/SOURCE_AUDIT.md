# Source audit

The source audit targets `BlockProcessor.ProcessBlock(Block, IBlockTracer, ProcessingOptions,
IReleaseSpec, CancellationToken)` in `src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs`.
Schema 8 / extractor 1.14.0 is independently accepted at the conditional finalization boundary.
Its refreshed 604-test shared gate, seven semantic mutations, deterministic artifacts, full Lean gate,
and complete 1305-export standard-only audit passed. The separate ProcessOne suffix is independently
accepted at schema 2 / extractor 1.0.0 after the complete 1599-export publication gate.

The extractor rejects source drift unless all of these identities remain unique and ordered:

- `CommitState(spec)` occurs three times in `ProcessBlock`; the admitted boundary is the second,
  after the transaction executor and `TransactionsExecuted?.Invoke()`.
- The commit map is explicit and ordered: post-transaction `CommitState(spec)` is
  `commitNoRoots(0)`, finalization `CommitState(spec)` is `commitNoRoots(1)`, and
  `CommitStateAndStorageRoots(spec)` is `commitRoots`; each event has its exact `ProcessBlock`
  call binding and typed helper binding. Every typed commit-helper and direct `_stateProvider.Commit`
  operation reachable in `ProcessBlock` is enumerated. Mutations that delete, duplicate, reorder,
  redirect, invert, add helper-equivalent/direct calls, or add a roots commit after `finally` are
  rejected with dedicated diagnostics.
  A typed source-local call closure rooted at `ProcessBlock` resolves every reachable executable
  declaration in the pinned compilation source trees, including nested helper types and the named
  calculation/reward hooks. Their calculations remain opaque observations, but their source-owned
  bodies are recursively checked for tracked effects and callable edges. Any unbound helper that
  transitively reaches `IWorldState.Commit` or writes `Header.StateRoot`, `Header.Hash`, or
  `Block.AccountChanges` fails closed independently of its method name. Every delegate invocation
  is typed and fails closed unless it is the exact `BlockProcessor.TransactionsExecuted` `Action`
  event hook; source delegate field/property initializers, accessors, method groups, and lambdas are
  therefore not silently treated as external. Source-local property/event accessors with executable
  bodies are rejected at their typed reference; accessor bodies are not double-walked as
  declaration-time execution. External or unpinned delegate implementations remain outside the
  closure and require the explicit adapter premise. The operation audit is fail-closed for
  source-owned constructors/object creation, interface or virtual dispatch without the exact
  receiver binding, user-defined conversions, unary/binary/compound/increment operators,
  address/function-pointer routes, dynamic object/member/indexer operations, and executable
  property/event accessors. Source-owned implicit `using` and custom-await edges are rejected except
  for the exact typed `MetricsTimer` instrumentation declarations in the bound helpers; the
  only admitted loops are the exact typed `CountLogs`, `CalculateBlooms`, and `AccumulateBlockBloom`
  loops. Nested callable bodies are excluded from the enclosing declaration walk; the exact
  `Task.Run` and bloom-parallel lambdas are audited through their own typed bodies. This keeps source-owned callable
  edges from being silently admitted as external while retaining genuine framework calls, exact
  executor/context calls, and explicitly named opaque hooks.
- `spec.IsEip4844Enabled` guards `BlobGasCalculator.CalculateBlobGas(block.Transactions)`.
- `header.BlobGasUsed` has one typed assignment in the EIP-4844 true arm. `header.ReceiptsRoot`
  has one typed synchronous assignment in the background-false arm and one explicitly recorded
  tuple assignment in the excluded background-result arm; competing or later writes are rejected.
- `ShouldCalculateReceiptsInBackground(receipts)` retains an else arm containing
  `CalculateBlooms(receipts)` before `CalculateReceiptsRoot(receipts, spec, block)`.
  The pinned partial standard source retains the 16-receipt and 64-log threshold implementation;
  vectors exercise both sides while the admitted theorem selects the false arm.
- The try-body order is rewards, withdrawals, no-root commit, execution requests,
  `EndBlockTrace(accumulateBlockBloom: bloomsAndReceiptsRootTask is null)`, roots commit, guarded
  account changes, guarded state root, the excluded background-result arm, and BAL finalization.
- The finally task-observation guard remains source-visible but is excluded under the false
  background premise.
- Synchronous `CalculateBlooms(receipts)` does not assign the header bloom. Because the
  `BlockReceiptsTracer` implementation is outside this closure, header-bloom mutation is omitted
  from the model; empty and non-empty vectors therefore assert preservation of the input bloom.
- `header.Hash = header.CalculateHash()` precedes the sole `return receipts`.

- `ComputeStateRoot(BlockHeader)` is bound to one direct typed
  `header.StateRoot = _stateProvider.StateRoot` helper write, and `SetAccountChanges(Block)` is
  bound to one direct typed `block.AccountChanges = _stateProvider.GetAccountChanges()` helper
  write. The tail and these helper bodies are globally audited for typed writes to
  `header.StateRoot`, `block.AccountChanges`, and `header.Hash`; only those helper writes and the
  canonical hash assignment are allowed. Direct, deconstruction, compound, ref-like, competing,
  and later writes fail closed.

The closure includes the partial standard implementation, interface, processing flags, and the
transaction-fold caller source so that the standard/normal boundary is not a metadata-only claim.
The ProcessOne, ProcessBlock, and ApplyMinerRewards ledgers bind the tracer parameter to
`Nethermind.Evm.Tracing.IBlockTracer`. A compiling alias to a same-named interface in
`Nethermind.Blockchain.Tracing` fails the exact method-identity gates, even when that interface
inherits the admitted tracer and the call-site syntax is unchanged.
The regenerated checked normal-tail IR records the corrected tracer namespace; the source-ledger
correction does not modify the normal-tail proof semantics.
The source-entry adapter records exact typed sites but explicitly remains static-layout-only; its
runtime exact-base, standard-executor, BAL-disabled, and guard choices are premises. The task-flow
  dataflow witness binds the actual `ILocalSymbol`, audits every IOperation reference/write, rejects
  ref aliases/ref-out, captures, helper uses, and non-whitelisted mutations, and uses CFG reaching
  definitions to prove the null initializer reaches the synchronous `EndBlockTrace`, background
  result guard, and finally guard. The normal-tail anchors also require expected conditional ancestry
  and dominance of the normal receipts return; wrapper conditionals therefore fail closed. CFG block
  span membership is serialized and checked so every non-method binding belongs to exactly one
  reachable owner flow; edge, exit, back-edge, and redirect mutations fail closed. The receipts
  local has one executor definition and all predicate/root/request/return reads use that same
  `ILocalSymbol`. The exact `TransactionsExecuted?.Invoke()` binding is the outer `ProcessBlock`
  expression statement, an unguarded reachable `IOperation` that dominates the post-transaction
  commit; its subscriber and that commit each carry separate normal-return adapter premises. Nested callable returns are excluded when binding
  the outer `return receipts`.
The refinement bridge names the actual `SequentialBlockTransactionFold.FoldResult` and checks
elementwise receipt identity, per-receipt log counts, terminal-result witnesses, completed indices,
terminal receipt/log witnesses, and the post-transaction commit; it does not equate whole result
  records. The independent handwritten specification is a relational event/header-effect relation;
  it does not reimplement the generated transition.

The upstream fold admits only `.ok` terminal observations on its completed path. The finalization
bridge records that as a separate premise while retaining an `evmException` projection for
conditional or non-completed witnesses; it does not infer the upstream restriction from receipt
counts or from equality of final results.
No production source file is modified by this package.

Instrumentation is an explicit source-owned callback boundary. The five complete sink declarations
are pinned alongside the 14 ordinary helper bodies. The exact metadata timer constructor/disposal
bindings identify the implicit getter and `AddTicks` routes, and getter/counter targets are typed
external symbols. Whole-type hashes reject new initialization, capture, conversion, disposal, or
helper re-entry code, including a callback using a processor/spec captured in `ProcessOne`.
The wrapper source has an independent fixed byte/token hash checked during extraction and
checked-artifact validation; its reference assembly is separately in the compiler inventory.
The global activation audit checks every timer-bearing expression in all five source trees,
including non-using constructors, explicit/default disposal receivers, and generic method type
arguments. A separate recursive source-bearing generic operation gate closes reachable metadata
callback routes. The serialized activation roster admits exactly the five pinned constructor sites;
coordinated sink/site mutations cannot extend it.
The exact complete `ProcessBlock` body is now preserved separately from the abbreviated member
canonical. Its closed executable syntax rejects additional metadata callback/comparer/constructor
sites, aliases, and implicit callback expressions even when metadata type arguments contain no
source type. Standalone `--check` reuses the pure extraction admission derivation against live
Roslyn source and compares the entire rederived IR, before accepting artifact consistency.
Coordinated source/pin/IR/manifest updates cannot retain stale helper, sink, activation, or entry
evidence. Hashes are consistency evidence, not a substitute for this rederivation.
Schema 8 / extractor 1.14.0 and these preservation tests passed serialized execution and
regeneration. They do not widen the supplied-FoldResult or receipt index/log-count claims.

The five semantic-owned trees are distinct from four compiler-support trees:
`BlockProcessor.SystemContractHandler.cs`, `BlockProcessor.BlockAccessListSystemContractHandler.cs`,
`TransactionProcessorAdapterExtensions.cs`, and `IBlockAccessListManager.cs`. The accepted Fold
support subset is compiled in the real same-assembly Roslyn model and its full path/hash roster is
serialized. Exact support byte pins and a typed boundary reject overload shadowing, callbacks,
re-entry into the semantic helper/sink/entry closure, and tree misclassification. Support bodies
are not verified by compilation; existing named external-hook leaves retain their explicit effect
and normal-return premises. The compiler-reference inventory contains 434 exact identities.

Artifact publication uses same-directory write-through temporary files and atomic replacement, with
the manifest written last as the serialized consistency marker. Pre-publication failures preserve the
prior set; an interrupted publication is rejected by the manifest/hash gate. Regeneration and the
full `Verify.ps1`/Lean gates are serialized build-lane operations. The accepted artifacts and all
38 semantic/proof dependency records passed those gates; hashes and evidence are in `README.md`.
The complete export census includes 485 finalization, 223 Fold, 407 Receipt, and 190 Eip
accounting-lineage theorems, including generated descendants but no ProcessOne exports.

## ProcessOne validated-publication audit

The next slice is intentionally an extension of this package. It does not widen the normal-tail
source boundary or add a second `ProcessBlock` extractor. The suffix admission uses
eight exact source trees and the source pins in `PROCESS_ONE_PUBLICATION_SOURCE_PINS.json`:

- `BlockProcessor.ProcessOne` is pinned as the exact-base entry and records the normal
  `ProcessBlock` result assignment, `processed=true`, both parallel-only BAL retry catches, the
  `processed`-guarded finally disposal, the validation call, `StoreReceipts` guard, and the
  processed-block/receipts return tuple.
- `BlockProcessor.ValidateProcessedBlock` is pinned as the NoValidation gate. Its false validator
  arm is recorded as `DisposeAccountChanges` followed by `new InvalidBlockException`, with no
  PostValidation, receipt store, or return edge.
  Both disposal anchors normalize reduced extension symbols to the exact
  `global::Nethermind.Core.BlockExtensions.DisposeAccountChanges(Nethermind.Core.Block)` declaration;
  an identically spelled call resolved to a different extension is rejected.
- `BlockProcessor.PostValidation` is pinned as four direct, ordered assignments. The fourth is
  the exact encoded-BAL `processedBlock.EncodedBlockAccessList ?? suggestedBlock.EncodedBlockAccessList`
  fallback rather than an unconditional overwrite.
- `BlockValidator.ValidateProcessedBlock` is pinned only for the typed validator observation. Its
  mismatch branch can write `suggestedBlock.GeneratedBlockAccessList` before returning false; the
  suffix models that proposed post-call value and does not pretend validator diagnostics are
  read-only.
- `IBlockValidator.ValidateProcessedBlock` and `ProcessingOptions` close the validator symbol and
  `NoValidation`/`StoreReceipts` flag identities. `StoreTxReceipts` binds the exact
  `IReceiptStorage.InsertDeferred(block, txReceipts, spec)` operation.

The processor standard partial, processor interface, standard executor, and receipt-storage
interface complete the eight semantic source trees. Four compiler-support trees are pinned
separately; compiling them does not verify their implementations. Metadata references are checked
against the same byte-pinned compiler inventory as the normal tail. No compilation stub is used.

The C# admission records source path, Roslyn FQN/symbol, operation, canonical syntax, line span,
actual CFG regions/edges/exit semantics and block memberships, and data-flow witnesses for the `receipts`, `processed`, suggested
GeneratedBAL, and encoded-BAL symbols. It rejects missing/duplicated/reordered anchors, source
pin drift, changed flag values, altered catch filters, validation disposal/throw order, any of the
four copy mutations, changed return tuple, and any `CommitTree`, reset, or head operation in the
ProcessOne body. Same-typed roles bind exact declarations, parameter ordinals and conversions;
lexical callable closure rejects helper shadowing. Synchronous helper lineage rejects async,
iterator and conditional execution, and source-selection guards reject directives. The strict
`ProcessOneValidatedPublication` IR and source-manifest schemas and mutation matrix are
deterministic and field-complete for this bounded admission.

The generator uses the separate `PublicationKernel.lean.template` only after the exact admitted
suffix skeleton, typed identities, CFG and dataflow checks pass. The generated site list comes
from the actual anchors. Artifact checking compares the entire IR with a fresh source extraction,
recomputes the kernel bytes, and verifies exact manifest/dependency hashes. Empty source/anchor
inventories, unknown JSON fields, nulls, duplicates, weakened obligations, or draft markers fail closed.
The manifest records exactly 52 dependencies. Each artifact is published using flushed
same-directory temporary storage and atomic replacement, with the manifest written last.

The generated suffix state is not a C# interpreter. Its explicit input is a normal ProcessBlock
result plus exact-base standard sequential BAL-disabled/nonparallel premises and separate normal
return/validator/store observations. Its independent reference distinguishes `Completed`,
`Rejected`, and `Escaped`: only validator false under validation enabled is rejection; generic
validator/PostValidation/InsertDeferred failures remain escapes. A completed trace publishes the
four fields, then optionally observes `InsertDeferred`, then returns the processed tuple. A
rejection applies the validator's proposed GeneratedBAL observation to the suggested state,
disposes account changes, and throws InvalidBlockException in that order. Cleanup and diagnostic
logging must return normally for that rejection outcome; a failure there is a generic escape.
Generic exceptions have no asserted final state. Block and receipt-array identifiers are opaque,
so preserving an identifier does not assert immutability of receipt content.

The suffix has no `CommitTree`, world reset, branch checkpoint, block-tree head, or outer-chain
publication. Those effects belong to `BranchProcessor`/`BlockchainProcessor` and are explicitly
excluded. Exact-base virtual dispatch, disposal behavior, validator internals, receipt durability,
and canonicalization remain adapter premises. The refinement file imports the suffix generated
kernel and independent suffix specification only; it does not use the existing finalization
`SourceAttachedRefinement` as a theorem premise. Its block/receipt identities, artifact snapshots,
flags and normal-return/effect observations remain adapter-supplied. It does not establish their
concrete runtime representation, derive flags from a `ProcessingOptions` mask, or compose a
computed finalization result. Escape traces describe only the known prefix.

The suffix checked-in IR, manifest, and Lean file are the independently accepted schema-2 /
extractor-1.0.0 artifacts. Default `--check` validates only finalization;
separate `--publication-check` validates live suffix source admission, strict serialized records,
exact dependencies and byte-exact re-emission. The package tests separately validate both published
JSON schemas and drift controls. The `source-admitted` marker alone does not confer
independent acceptance. `--publication-extract` requires ReceiptTerminal pins schema 3 and manifest
schema 9/extractor 1.9.2, checking the exact envelope fields, value kinds, nonempty inventories,
digest formats, and duplicate properties. Both documents must carry the independently enumerated
ordered 12-source inventory, including `TransactionProcessor`, and each digest must match the live
source bytes. The manifest must also match the exact IR/Lean paths and artifact bytes. Agreement
between two empty or jointly weakened inventories is not sufficient. Both slices additionally invoke the exact Fold checked-artifact validator, use its compiler-reference
reader, and freeze the enumerated own/Fold/Receipt/accounting semantic and proof dependencies.
`PROMOTION_PINS.json` now records `accepted-upstreams`, pinning independently accepted ReceiptTerminal
1.9.2 and Fold 1.8.0 artifacts. `PUBLICATION_PROMOTION_PINS.json` separately binds accepted
finalization. `Verify-Publication.ps1` runs the shared gate with its current 604-test minimum,
two deterministic suffix extractions, warning-failing Lake and four direct Lean targets,
fifteen semantic mutations (eight publication plus seven finalization), and the complete
1599-export axiom census before the final live suffix check.
The frozen publication roster contains 294 ProcessOne exports plus 1305 upstream exports and has
SHA-256 `9af770169f8539bf833aa4519a07b6a52fd0bffad5d513d40546f9ba0bf72312`.
Every descendant under the four ProcessOne namespaces is included, with transitive axioms limited
to `propext`, `Classical.choice`, and `Quot.sound` and negative/completeness controls. The full
604/1305/1599 gate, zero-warning/error Release solution build, and both post-solution checks passed;
independent executable review accepted the exact final hashes. Evidence is recorded in `README.md`.

The normal-tail CFG also records the nested observation catch as a Roslyn exception-handler
entry. Connectivity includes that region entry without inventing an ordinary exception edge.
Event evaluation and optional invocation retain separate source positions; synthesized captures
and enclosing statement spans do not imply repeated evaluation. Return anchors use the returned
expression's CFG position. These source-admission checks do not extend the normal-return theorem
to exceptional execution or establish that the supplied fold result was computed by `Fold.run`.

Normal-return dominance uses only ordinary paths from the actual source entry, independently of
the handler-connectivity audit. Preserved-value admission binds the exact `header = block.Header`
origin and all root-method identity uses. It also serializes the complete 14-helper reachable
source-local closure with exact member symbols, paths, canonical bodies, and independently fixed
body digests; the four directly modeled helper bindings must match that same closure. This includes
`ApplyMinerRewards`, its transitive reward helpers, `CalculateBlooms`, `CountLogs`, receipt-root and
bloom-accumulation helpers, the standard partial predicate, and the admitted callable bodies.
No extra alias, parameter redirection, or external mutator call can be added to those pinned bodies
and then discharged as an external-hook premise.

A separate typed audit across all five source trees rejects preserved receiver assignments,
receipt `Index`/`Logs` assignments, receipt-array element replacement, and ref/in/out escapes.
Only the exact pre-tail `ProcessOne` handler selection is allowed. Compile-first mutations cover
the reward-helper handler swap and bloom-helper receipt-index overwrite, plus direct, transitive,
aliased, and ref-based variants. Coordinated body/digest IR mutations must still fail the fixed
closure baseline. This protects only the modeled receipt indices and log counts; it does not add
a receipt-content theorem or verify the external implementations called by unchanged helpers.
