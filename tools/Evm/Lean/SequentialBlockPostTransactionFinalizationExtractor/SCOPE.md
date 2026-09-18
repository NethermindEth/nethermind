# Scope

Schema 8 / extractor 1.14.0 is accepted only for the conditional finalization boundary below.
Refreshed verification passed 604 tests, seven finalization compile-valid semantic mutations, deterministic artifact
comparison, the full Lean gate, and a complete 1305-export standard-only axiom audit (485
finalization, 223 Fold, 407 Receipt, 190 Eip accounting lineage). This is not a complete
block-processing proof. ProcessOne publication has its separate conditional acceptance below.

## Admitted path

The admitted path is the exact source path below:

1. `ProcessBlock` has returned normally from the sequential transaction fold.
2. The exact `TransactionsExecuted?.Invoke()` event evaluation is unconditional and dominates the
   second `CommitState(spec)`. Its optional subscriber call and the commit separately return normally.
3. The second `CommitState(spec)` is the no-storage-roots post-transaction boundary.
4. EIP-4844 blob gas is conditionally assigned from `block.Transactions`.
5. `ShouldCalculateReceiptsInBackground(receipts)` is false, so the synchronous bloom and receipt-root arm is selected.
6. Rewards, withdrawals, the second no-root commit, execution requests, `EndBlockTrace(true)`, and the roots commit occur in source order.
7. Main-thread account changes and state-root computation are each selected only by their source guards.
8. BAL finalization is observed before the header hash, followed by `return receipts`.

The three commit events are distinct source identities: `commitNoRoots(0)` at the post-transaction
boundary, `commitNoRoots(1)` in finalization, and `commitRoots` at
`CommitStateAndStorageRoots(spec)`. Each retains its outer call binding and typed helper-body
state-provider binding. The entire `ProcessBlock` body is globally enumerated for typed
`CommitState`, `CommitStateAndStorageRoots`, and direct `_stateProvider.Commit` operations: only
the three helper calls are admitted, with the roots helper inside finalization `try`; duplicate,
helper-equivalent, direct, or post-`finally` commits fail closed. The two modeled header assignments are also selected-arm
identities: one `BlobGasUsed` write under EIP-4844 and one synchronous `ReceiptsRoot` write under
the false background arm. The excluded background tuple root write is recorded separately, and
any competing or later header write is rejected.
A typed source-local call closure rooted at `ProcessBlock` resolves every reachable executable
method declaration in the pinned compilation source trees, including nested helper types. It
traverses every reachable source-owned helper, including the named calculation/reward hooks and
the exact bound commit/state-root/account-change helpers. An unbound helper that
transitively reaches a state commit or writes
`Header.StateRoot`, `Header.Hash`, or `Block.AccountChanges` fails closed independently of its
method name. Delegate invocations are not treated as external: source delegate fields, properties,
accessors, method groups, and lambdas fail closed unless the invocation is the exact typed
`BlockProcessor.TransactionsExecuted` `Action` event hook. Source-local property/event accessors
with executable bodies are rejected at their typed reference, rather than being traversed as if
their declaration executed. The exact `Task.Run` and bloom-parallel lambdas are recursively audited.

The operation-level closure is fail-closed for every executable source-owned edge in the pinned
syntax trees. It rejects source-owned constructors/object creation, interface/abstract/virtual
dispatch without an exact receiver binding, user-defined conversions, unary/binary/compound and
increment operators, address/function-pointer creation or invocation, dynamic object/member/indexer
operations, and source-owned property/event accessors. It also rejects source-owned implicit
    `using` and custom-await routes, except for the exact typed `MetricsTimer` instrumentation
    declarations in the bound helpers, and admits only the exact typed `CountLogs`, `CalculateBlooms`,
and `AccumulateBlockBloom` loops.
Nested lambda/local-function bodies are not treated as
executed merely because they are nested in a declaration; only the exact normal-path `Task.Run` and
bloom-parallel lambdas are entered through their body operations and audited there. Exact
executor/context bindings and genuine framework calls are the remaining explicit boundaries.

`ComputeStateRoot` and `SetAccountChanges` are included as typed source members and CFGs. Their
helper bodies are bound to exactly one direct state-root/account-change property write and its
typed value operation. The tail and both helper bodies undergo a complete write audit for
`header.StateRoot`, `block.AccountChanges`, and `header.Hash`; direct, deconstruction, compound,
ref-like, competing, and later writes are rejected.

Before an artifact is admitted, an independent whole-five-pinned-tree effect audit
walks every executable method, constructor/static constructor, operator, conversion, local function,
lambda, accessor, and member initializer in `BlockProcessor.cs`, `BlockProcessor.std.cs`,
`IBlockProcessor.cs`, `ProcessingOptions.cs`, and
`BlockProcessor.BlockValidationTransactionsExecutor.cs`. It compares a typed, multiplicity-sensitive
ledger keyed by source path, owner/initializer, target symbol, operation kind, and canonical syntax.
The ledger contains every direct write to `Header.BlobGasUsed`, `Header.Bloom`,
`Header.ReceiptsRoot`, `Header.StateRoot`, `Header.Hash`, and `Block.AccountChanges`, plus every direct
`IWorldState.Commit`. Its exact baseline deliberately retains the non-tail `PostValidation` and
`PrepareBlockForProcessing` writes and the opaque `ApplyMinerRewards` tracer commit. Thus a dormant
helper, nested callable, initializer, static initializer, or metrics sink cannot add a modeled effect
without failing extraction. The non-tail rows are negative boundary evidence only and do not enlarge the
admitted `ProcessBlock` theorem.

A separate property/indexer/event activation ledger is empty by construction for the current five trees:
every reference or assignment whose typed target has an executable source implementation, including a
source implementation of a metadata-interface or virtual-base member, is rejected. Initializer lambdas
are limited to the two canonical system-handler `Lazy` declarations; source-owned initialization is rejected. The
late-bound audit rejects reflection, `Type` lookup/invocation, dynamic delegate invocation, activator,
expression, marshal, and related re-entry APIs. A framework or opaque target with no executable
declaration in the five pinned trees is not silently modeled: it is covered only by the explicit
no-additional-modeled-effect and normal-return adapter premise.

## Evidence boundary

Every admitted source node carries its canonical syntax, source hash, Roslyn symbol/operation
identity, CFG block, reachability bit, and method dataflow sets. The compiler closure is pinned to
the checked-in reference inventory and byte hashes. The source pins are fail-closed. The source-entry
adapter is static-layout-only: typed syntax does not prove runtime DI or virtual dispatch selection.
The real same-assembly semantic model additionally compiles four exact support trees:
`BlockProcessor.SystemContractHandler.cs`, `BlockProcessor.BlockAccessListSystemContractHandler.cs`,
`TransactionProcessorAdapterExtensions.cs`, and `IBlockAccessListManager.cs`. Their serialized
path/hash roster and 434 metadata-reference identities are distinct from the five semantic-owned
trees. Support overloads, callbacks/re-entry, roster drift, and semantic/support misclassification
are rejected. Compiled support bodies are not proved; admitted named external-hook leaves keep
their explicit effect and normal-return premises.
A separate task-flow witness binds one `ILocalSymbol`, performs a complete typed reference/write
audit, records one exact `Task.Run` background-arm assignment and zero synchronous-arm writes, and
proves by CFG reaching definitions that the null initializer reaches the false-arm trace, result,
and finally guards. Exact edge/exit arrays and serialized CFG block-span ownership are validated;
every non-method binding must resolve to one reachable owner flow, and redirects to another
reachable ordinal fail closed. The receipts local has one executor definition and all predicate,
  root, request, and return reads use that same symbol. The `TransactionsExecuted` binding must be
  the outer `ProcessBlock` expression statement with an unguarded, reachable event evaluation that
  dominates the post-transaction commit. The null-test and optional invocation are bound separately; its subscriber and that commit each have separate normal-return adapter premises. Normal-tail actions must dominate the normal
receipts return and retain only their expected guard ancestry.
This dominance is over the source-entry ordinary-flow subgraph under the normal-return premise;
disconnected catch roots participate only in the separate full-CFG connectivity audit.
The exact `header = block.Header` initializer dominates the normal boundary. The header, block,
spec, block tracer, world-state field, standard-handler field, and receipts-tracer property have
exact declaration and ordered read/receiver ledgers, with no competing definitions or ref escapes.
All 14 admitted executable source-local helpers have exact symbol/path/canonical-body identities,
including the transitive reward calls and receipt lambdas; the four modeled commit/root/account
members are cross-linked to this closure. A typed five-tree audit rejects protected receiver
rebinding, receipt `Index`/`Logs` writes, receipt-array element replacement, and preserved-reference
ref escapes, retaining only the exact pre-tail handler selection. Alias creation, indirect external
mutator calls, or other changed local helper syntax also fail the exact helper-body gate.
These source checks cover pinned helper code; external-hook preservation premises apply only
outside that closure and cannot justify source-local mutations. They are not a proof of the
calculations performed by those external implementations.

The five timer sinks form an additional implicit-call preservation roster. Whole-type declaration
hashes close `IsEnabled`, `AddTicks`, all initializers, and any added capture, conversion, or
disposal members. Typed identities bind each sink's metadata interface, getter value, tick-counter
calls and arguments, exact helper timer site, constructed wrapper, constructor, and disposal.
The exact `MetricsTimer` source is checked in extraction and checked-artifact admission. Its
constructor reads `IsEnabled`; disposal reads it again and conditionally invokes `AddTicks`.
Adding a helper call through either route, even with a coordinated static capture outside
`ProcessBlock`, fails the sink declaration gate. All reachable using sites are limited to those
timers and the exact reward-tracer resource. External counter/tracer implementations and the
wrapper's CLR execution remain explicit external premises; separately pinning source and a
reference assembly does not establish their source-to-binary equivalence.

All timer-bearing expressions in the five pinned trees are inventoried independently of using
syntax, with exactly five source constructor sites serialized. Explicit disposal, default timer
receivers, non-using creation, and nested generic timer types fail closed outside that roster.
Reachable generic metadata methods, members, and values are also inspected recursively for
source-owned type arguments; an unbound source sink cannot be hidden behind an external wrapper.

The entry's complete executable-body token identity additionally closes all new explicit and
implicit metadata callback sites, including nongeneric `this` receivers, comparer aliases,
delegates, constructors, and formatting. It supplements the shortened `ProcessBlock` member
canonical and preserves the complete admitted expression language. Existing external calculation
and hook implementations still need the stated effect/normal-return premises.

Standalone checked-artifact admission recompiles and rederives the complete source IR through the
same pure admission function used for extraction, then compares it with the serialized IR. Source
pins and manifest hashes are insufficient on their own. No artifact writes occur during this
check; rederivation includes all helper, timer/sink/activation, entry, identity, and CFG gates.

Artifact emission uses same-directory write-through temporary files with atomic replacement, and
writes the manifest last as the serialized consistency marker. Pre-publication failures preserve the
prior set; an interrupted publication is rejected by the manifest/hash gate. Regeneration remains a
serialized build-lane obligation.

The source-wide ledger boundary is exactly those five syntax trees, not the whole repository, a runtime
dispatch proof, or an audit of external implementations. Code in unpinned partials, dependencies,
generated assemblies, framework implementations, callbacks, or reflection targets remains outside this
conditional claim; it must satisfy the stated adapter premise or be brought into a new pinned ledger and
serialized regeneration.

## Non-claims

This package does not prove the implementation of any opaque delegate or external calculation. It
does not run C#, infer dynamic dispatch, prove the ProcessOne caller/validation path, or compose
the finalization result with downstream packages. The handwritten adapter preserves exact mapped
block/header/receipts/spec/world/tracer/standard-handler identities. `SourceAttachedRefinement`
bundles the generated outcome with an independently constructed reference observation and the
actual fold projections; its proof fields are consumed to derive completed, receipt/log, terminal,
index, and post-commit agreement. The adapter's explicit `noAdditionalModeledEffects` equality is
used when deriving the reference header/effect relation, so an altered opaque-helper effect list
cannot satisfy the relation. A fold bridge is an elementwise projection obligation over a supplied
`SequentialBlockTransactionFold.FoldResult`, not a result-equality axiom or a `Fold.run` witness.
Receipt indices and log counts are related, but receipt log content is not proved. Header-bloom mutation is intentionally omitted because
`BlockReceiptsTracer` is outside this source closure; empty and non-empty vectors check that the
modeled header bloom is preserved.

The upstream fold's completed path restricts terminal observations to `.ok`. The finalization
bridge carries that as an explicit upstream premise while retaining a conditional `evmException`
projection for non-completed or otherwise conditional witnesses; it does not infer `.ok` from
receipt counts or whole-result equality.

## ProcessOne suffix boundary

The separate `ProcessOneValidatedPublication` boundary begins with a normal result of the exact
`BlockProcessor.ProcessBlock` call at `BlockProcessor.ProcessOne` and ends at its
`return (block, receipts)`. Its source closure has eight pinned trees: `BlockProcessor.cs`,
`BlockValidator.cs`, `IBlockValidator.cs`, `ProcessingOptions.cs`, `BlockProcessor.std.cs`,
`IBlockProcessor.cs`, `BlockProcessor.BlockValidationTransactionsExecutor.cs`, and `IReceiptStorage.cs`.
Four compiler-support trees are pinned separately from these eight semantic source trees;
their compilation closes the real source context without proving their implementations.
It records Roslyn-derived FQNs, operations, CFG edges and block membership, and local data-flow
symbol shapes for the ProcessOne call/try/finally, the two parallel-only BAL retry catches, the
`NoValidation` guard, validator invocation, rejection disposal and `InvalidBlockException`, exact
`PostValidation` copies, `StoreReceipts`/`InsertDeferred`, and the returned tuple. It also binds
the one validator mismatch write to `suggestedBlock.GeneratedBlockAccessList` so the proposed
artifact is retained as an observation even when validation rejects.

The suffix input state includes suggested and processed artifact identities, receipts, option
flags, exact-base/standard/sequential/BAL/nonparallel premises, normal-return premises, a typed
validator observation (`ran`, `accepted`, `normalReturn`, and proposed GeneratedBAL after the call;
the before value comes from the suggested artifacts),
and the no-additional-effect boundary. The output state includes the suggested artifact
projection, processed block/receipts, disposal bit, outcome, and ordered events. `Completed`
requires the NoValidation guard or an accepted validator, then copies AccountChanges,
ExecutionRequests, GeneratedBlockAccessList, and EncodedBlockAccessList with the exact
`processed ?? suggested` fallback; optional `InsertDeferred` follows publication and the returned
tuple retains the ProcessBlock result's block and receipt-array identities. `Rejected` requires
validator false and normal cleanup/diagnostics, retains the validator's proposed BAL mutation,
disposes account changes before `InvalidBlockException`, and has no PostValidation/store/return.
`Escaped` covers unsupported boundaries and generic validator/cleanup/PostValidation/storage
exceptions; no final state is asserted after an external exception. Receipt content mutation,
including synchronous storage recovery, is outside the opaque array-identity model.

No `CommitTree`, world reset, block-tree head update, BranchProcessor checkpoint, or
BlockchainProcessor publication is inside this boundary. Validator root/hash/BAL-size diagnostics,
virtual/CLR dispatch, disposal implementation, receipt durability/canonicalization, and all
downstream branch/outer-chain behavior remain adapter obligations or exclusions. The new Lean
specification is independent of the generated kernel; the refinement theorem consumes explicit
source/FQN identity and normal-return premises and does not reuse the existing normal-tail
`SourceAttachedRefinement` as a proof. Block/receipt identities, artifact snapshots, flag values,
and external effect observations remain adapter-supplied. No concrete `ProcessingOptions` mask
relation or computed finalization result is established. Escape traces retain only a known prefix.

The suffix is independently accepted at schema 2 / extractor 1.0.0 after executable review.
Its `source-admitted` marker alone remains source-admission evidence, not independent acceptance.
Separate `--publication-check` rederives the suffix IR from live compiled source, validates strict
serialized records, checks exact generated bytes and all 52 dependencies, and rejects stale
or draft artifacts. Default `--check` validates only finalization. `--publication-extract` emits
the suffix through flushed same-directory atomic replacement with the manifest last.
Both slices call the exact Fold validator and
require accepted upstream hashes in `PROMOTION_PINS.json`, which records `accepted-upstreams`
for ReceiptTerminal schema 9/extractor 1.9.2 and Fold schema 9/extractor 1.8.0. The accepted
finalization gate checks the complete enumerated own/Fold/Receipt/accounting proof lineage,
excluding publication. `PUBLICATION_PROMOTION_PINS.json` separately pins accepted finalization.
`Verify-Publication.ps1` adds live suffix checks, deterministic extraction, warning-failing Lake
and four direct Lean targets, fifteen semantic mutations (eight publication plus seven
finalization), and the complete 1599-export census: 294
ProcessOne exports plus 1305 upstream exports. The current shared test floor is 604. The frozen
publication inventory SHA-256 is `9af770169f8539bf833aa4519a07b6a52fd0bffad5d513d40546f9ba0bf72312`.
The package test gate also validates both published JSON schemas and their negative controls.
All 604 tests, fifteen semantic mutations, both complete axiom audits, deterministic comparisons,
direct targets, the zero-warning/error solution build and both post-solution checks passed.
Independent executable review accepted the exact final hashes. These gates preserve every
conditional boundary above; evidence is listed in `README.md`. Downstream branch/outer packages
remain unaccepted, and the full block-processing claim remains incomplete.
