# Sequential block post-transaction finalization extractor

Schema 8 / extractor 1.14.0 is independently accepted for the conditional boundary below.
The refreshed serialized gate passed 604 tests, seven finalization compile-valid semantic mutations, deterministic
artifact comparison, all four direct Lean targets, and the complete 1305-export standard-only
axiom audit. The separate `ProcessOneValidatedPublication` schema 2 / extractor 1.0.0 boundary is
also independently accepted as detailed below. Neither result completes the full block-processing claim.

This is a narrow source-audit package for the standard exact-base, BAL-disabled, normal-return
path of `BlockProcessor.ProcessBlock`. Its admitted source boundary starts at the post-transaction
`CommitState(spec)` after the exact `TransactionsExecuted?.Invoke()` callback and ends at `return receipts`, before
`ProcessOne` validation.

The extractor binds the boundary to C#14 Roslyn declarations, typed `IOperation` identities,
control-flow blocks, and method dataflow. It distinguishes the no-root commit from the roots
commit by following each private helper to `_stateProvider.Commit(spec, commitRoots:false/true)`.
It also binds the EIP-4844 guard, the false receipt-background arm, synchronous blooms and receipt
root, ordered opaque delegates, `EndBlockTrace(true)`, main-thread and state-root guards, BAL
finalization, header hash, and the receipts return.

Commit evidence is three-way and positional: the post-transaction and finalization
`CommitState(spec)` calls are separate `commitNoRoots(0)`/`commitNoRoots(1)` events, while
`CommitStateAndStorageRoots(spec)` is the distinct `commitRoots` event. Each event retains both
its exact `ProcessBlock` call binding and the typed helper-body `_stateProvider.Commit` binding.
The extractor globally enumerates typed commit-helper and direct `_stateProvider.Commit` calls on
the `ProcessBlock` CFG: exactly those three helper calls and one roots-helper call inside the
finalization `try` are admitted; direct, duplicate, helper-equivalent, and post-`finally` commits
fail closed. A typed source-local call closure is rooted at `ProcessBlock` and resolved through
the pinned compilation source trees. It traverses every reachable source-owned helper (including
the named calculation/reward hooks and nested helper types), while retaining their calculations as
opaque observations. Any unbound reachable helper that transitively commits or writes
`Header.StateRoot`, `Header.Hash`, or `Block.AccountChanges` fails closed, regardless of its name.
Delegate invocations are also typed and fail closed: source delegate fields, properties, accessors,
method groups, and lambdas are not treated as external calls. Source-local property and event
accessors are likewise rejected when reached through typed property/event references; their bodies
are not traversed as if declaration-time execution. The sole admitted delegate call is the exact
`BlockProcessor.TransactionsExecuted` `Action` event invocation; its subscriber normal return is a
separate adapter premise. This is a typed lexical/CFG audit of the admitted `ProcessBlock` body and
its source-local closure.

The implicit `MetricsTimer` routes have a separate preservation boundary: all five sink types
are pinned as complete declarations, covering `IsEnabled`, `AddTicks`, and the absence of extra
initializers, captures, conversions, or disposal members. Each timer site binds its exact source
helper, constructed wrapper type, constructor, and disposal method. The wrapper source is pinned
in extraction and checked-artifact admission; getters and counter calls bind exact external
metadata symbols. Source-local re-entry from a sink cannot be justified by an external-hook premise.
The wrapper source and reference assembly are separately pinned evidence, not a proof of CLR or
source-to-binary equivalence. The schema 8 / extractor 1.14.0 artifacts include this admitted
instrumentation evidence and passed serialized regeneration and verification.

The timer activation inventory scans all expressions in the five pinned source trees, including
ordinary constructors, explicit disposal calls, default values, receiver reads, and recursively
nested generic arguments. Only the five exact constructor sites are admitted and serialized.
The reachable operation closure separately rejects unbound generic methods, members, and values
carrying source-owned type arguments, so a metadata wrapper cannot hide an additional sink callback.

The complete `ProcessBlock` executable body has its own token identity, supplementing its shortened
member signature and the helper identities. This fixes every admitted explicit and implicit call
site, including metadata APIs that can call back through a nongeneric receiver, comparer, delegate,
constructor, or formatter. New executable expressions fail the entry-body gate even when the
framework target has no source-owned generic argument. Normal-return and effect premises still
apply to the existing named external hooks; the gate does not prove their implementations.

Standalone `--check` recompiles the pinned source closure in Roslyn and runs the same pure admission
derivation used by extraction, then compares the complete rederived IR. It performs no artifact
writes. Matching source pins, artifact hashes, or self-consistent serialized callback identities
cannot replace these live helper, sink, activation, and entry-body checks.

The real same-assembly Roslyn compilation also includes four separately pinned compiler-support
trees: `BlockProcessor.SystemContractHandler.cs`,
`BlockProcessor.BlockAccessListSystemContractHandler.cs`, `TransactionProcessorAdapterExtensions.cs`,
and `IBlockAccessListManager.cs`. Their complete path/hash roster is serialized in
`compilerClosure.supportSources`; these are distinct from the five semantic-owned trees below.
Support overload shadowing, callback/re-entry into the semantic closure, roster drift, and
semantic/support misclassification fail closed. Compiling these support bodies does not verify
their implementation: reachable named external-hook leaves retain explicit effect and normal-return
premises. The compiler inventory separately pins 434 metadata references.

The source-entry adapter carries distinct normal-return premises for the `TransactionsExecuted`
subscriber and the post-transaction `CommitState(spec)`. The generated and handwritten Lean inputs
retain both predicates separately; either false predicate selects the unsupported non-normal outcome.

Before an artifact is admitted, a separate fail-closed effect boundary audits **all executable
declarations in exactly five pinned syntax trees**: method, constructor/static-constructor, operator,
conversion, local-function, lambda, accessor, and member-initializer bodies. Its typed ledger keys each
direct `Header.BlobGasUsed`, `Header.Bloom`, `Header.ReceiptsRoot`, `Header.StateRoot`, `Header.Hash`,
`Block.AccountChanges`, and direct `IWorldState.Commit` by source path, owner/initializer, typed target,
operation kind, canonical syntax, and multiplicity. The baseline intentionally includes the non-tail
`PostValidation` account-change copy, `PrepareBlockForProcessing` state-root copy, and the opaque
reward-tracer commit as well as the modeled tail rows. Any missing, extra, redirected, helper-hidden, or
initializer-hidden effect fails extraction. The non-tail rows are negative boundary evidence only; they
do not add `ProcessOne` or `PostValidation` behavior to the theorem.

A companion typed property/indexer/event activation ledger scans every such reference and assignment.
The current five-tree baseline has no route whose target has an executable implementation in those trees:
an accessor implemented by a pinned class, including an implementation behind a metadata interface or
virtual metadata base member, therefore fails closed rather than being mistaken for an external contract.
Only a genuinely external
target with no executable declaration in the five-tree closure can cross the explicit
no-additional-modeled-effect/normal-return adapter. Field and property initializers admit only the two
canonical system-handler `Lazy` initializers; source-owned construction and any other initializer lambda
are rejected. Reflection, `Type` member lookup/invocation, delegate dynamic invocation, activator,
expression, marshal, and other late-bound re-entry APIs are likewise rejected before extraction.
Every other executable edge whose target or implementation is declared in a pinned syntax tree is
either traversed through its exact body or rejected: constructors/object creation, interface or
virtual dispatch, user-defined conversions, unary/binary/compound/increment operators, function
addresses and pointers, dynamic object/member/indexer calls, and source-owned accessor routes. The
audit also stops at source-owned implicit `using` and custom-await edges, except for the exact typed
`MetricsTimer` instrumentation declarations in the bound helpers, and admits only the exact typed
`CountLogs`, `CalculateBlooms`, and `AccumulateBlockBloom` loops. The exact executor/context calls,
audited `Task.Run` lambda, and audited bloom-parallel lambda are the only admitted nested callable
edges. Genuine framework calls remain explicit external boundaries; named opaque hooks are
source-audited rather than cut from the closure.
Header writes are bound
as typed selected-arm assignments: the EIP-4844 `BlobGasUsed` write and synchronous
`ReceiptsRoot` write are each unique; the only other root write recorded is the excluded
background-result tuple assignment.

`ComputeStateRoot` and `SetAccountChanges` are source-bound helper members with exact typed bodies:
the former has one direct `header.StateRoot = _stateProvider.StateRoot` write and the latter one
direct `block.AccountChanges = _stateProvider.GetAccountChanges()` write. A complete property-write
audit over the reachable tail and these helper bodies rejects direct, deconstruction, compound,
ref-like, competing, and later writes to `header.StateRoot`, `block.AccountChanges`, or
`header.Hash`; only the two bound helper writes and the canonical hash assignment are admitted.

The generated Lean module is an executable typed transition over observations. It preserves the
exact block/header/receipts/spec/world/tracer/standard-handler identities and reports the ordered
observables. Reward, withdrawal, execution-request, bloom, root, access-list, and hash bodies are
opaque normal-return observations; no production arithmetic, trie, RLP, storage, or cryptography
is reimplemented. Synchronous `CalculateBlooms` is a receipt observation only. Header-bloom
mutation is intentionally omitted because the `BlockReceiptsTracer` implementation is outside
this source closure; empty and non-empty vectors assert that the modeled header bloom is preserved.

The handwritten specification is independent and relational: it describes an ordered event trace
and a separate header-effect relation, rather than copying the generated `run`. The refinement
file supplies a single `SourceAttachedRefinement` relation. It bundles the generated outcome,
an independently constructed reference observation, source-entry identity, and an extensional
projection of the actual `SequentialBlockTransactionFold.FoldResult`; there is no whole-output
relation premise and no discarded `.1` theorem projection. The bridge requires completed/committed
status, per-receipt indices and log counts, terminal results, completed indices, terminal receipt and log witnesses,
and the post-transaction commit witness. The source adapter carries a modeled-effect list and an
explicit `noAdditionalModeledEffects` equality, which is used to derive the reference
header/effect equality. The upstream fold's completed theorem separately supplies the `.ok`
terminal-result restriction; the bridge retains the conditional `evmException` projection and
never derives success from counts. This is composition over a supplied `FoldResult`, not a witness
that the result was computed by `Fold.run`; receipt log contents are not proved.

The source-entry adapter is intentionally static-layout-only. It records typed identities for the
exact-base receiver, selected standard executor, BAL, background, main-thread, state-root, release-
spec, and exact `TransactionsExecuted`/post-transaction `CommitState` sites, plus the exact
`ComputeStateRoot` and `SetAccountChanges` helper writes and value symbols; runtime dispatch and
those boolean premises remain explicit inputs. The task
flow witness audits the actual `ILocalSymbol` references and writes, rejects ref aliases/ref-out,
captures, helper uses, and non-whitelisted mutations, and uses CFG reaching definitions to prove
that the null initializer reaches `EndBlockTrace`, the background-result guard, and the finally
guard on the false arm. The receipts result is one exact local with one executor definition and
all predicate/root/request/return uses audited against that symbol. Every admitted normal-tail
header use binds the one exact `BlockHeader header = block.Header` initialization. A seven-value
identity ledger pins declarations and all 48 ordered read/receiver contexts for header, block,
spec, tracer, world state, standard handler, and receipts tracer. Competing definitions, ref/in/out
escapes, aliases, and receiver redirection fail closed. Preservation extends through the complete
14-method source-local closure, including transitive reward helpers, the standard partial receipt
predicate, and both admitted receipt lambdas. Its exact symbol/path/body-digest roster rejects
added aliases, changed parameters, and new external mutator calls in those bodies; the four
directly modeled helper bindings are cross-linked to that same roster. A separate typed five-tree
audit rejects receiver rebinding, receipt `Index`/`Logs` writes, receipt array-element replacement,
and ref/in/out escapes. The sole pre-`ProcessBlock` handler-selection assignment is retained
explicitly. External-hook preservation premises do not excuse effects in pinned helper code.

Every admitted normal-tail action is source-checked for its expected conditional ancestors; every
admitted unconditional action must dominate the normal receipts return in the Roslyn CFG. The
callback must be the outer `ProcessBlock` expression statement. Its unconditional event evaluation
dominates the post-transaction commit, while the null-test/optional-Invoke topology is checked
separately. Serialized CFG block membership uses the actual lowered source operation rather than enclosing statement spans;
synthetic event captures do not become second evaluation sites, and returns bind their returned
expression. Method dataflow remains method-wide. The outer tail try is selected separately from
the nested finally observation try; its catch entry is retained as an exception-handler region
entry, because Roslyn does not encode the exceptional transfer as an ordinary successor edge.
Normal-return dominance uses only ordinary paths reachable from the actual source entry; a
disconnected catch entry cannot erase preceding dominators when its finally exit rejoins the
normal continuation. Handler connectivity is audited separately. An ordinary entry-to-handler
or entry-to-return bypass is still rejected by the same serialized dominance check.
Block span membership is checked for every non-method binding, including edge/exit ownership and
redirected reachable ordinals. Emission writes each artifact through a same-directory write-through
temporary and atomic replace; the manifest is written last and acts as the serialized consistency
  marker. Pre-publication extraction failures leave the prior set unchanged; a publication interruption
  is detected by the manifest/hash gate. Regeneration of the checked-in set remains a serialized build-lane operation.

## Deliberate exclusions

The theorem/model does not cover BAL-enabled processing, background receipt tasks, callback/commit
or hook throws,
rollback or durability, `ProcessOne` validation/PostValidation, storage/DI/CLR behavior, or
non-standard, parallel, simulation, Optimism, and Taiko paths. Hook failures are therefore only
represented by an unsupported `normalReturn = false` vector. The fold handoff is a projection
obligation over the actual `SequentialBlockTransactionFold.FoldResult`; it is not result equality
or downstream composition.

The five-tree ledger is deliberately not a whole-repository or runtime-dispatch proof. Executable code
in an unpinned partial, dependency, generated assembly, framework implementation, reflection target, or
callback remains outside this claim and must either be absent by the checks above or satisfy the explicit
external no-additional-modeled-effect adapter premise. Source-owned opaque helper bodies are included
in the pinned callable/effect closure. Expanding the source boundary requires a new pinned-tree ledger
and serialized regeneration.

## Files

- `Extractor.cs`, `Models.cs`, and `LeanEmitter.cs` — fail-closed typed source extraction and emission.
- `Generated/` — checked-in generated IR, source manifest, and theorem-free executable kernel.
- `Specification/` — independent handwritten event/header-effect relation.
- `Refinement/` — the source-attached generated/reference relation, effect adapter, and elementwise fold bridge.
- `Vectors/` — receipt/log thresholds, empty/non-empty bloom, guards, toggles, unsupported-hook vectors,
  and source-effect rejection witnesses.
- `Test/` — source, IR, order, guard, argument, alias, early-return, initializer, accessor, disposal,
  metrics-sink, and reflection mutation tests.
- `Verify.ps1` — serialized build, deterministic extraction, and Lean gates for the build lane.

The accepted finalization gate runs the extractor and tests plus Lake/Lean; it does not execute
the modeled production C# path or establish CLR/source-to-binary equivalence.

## ProcessOne validated-publication suffix

This package now also owns the next bounded slice, `ProcessOneValidatedPublication`. It starts
from an adapter-supplied normal `ProcessBlock` result (`block`, `receipts`) and ends at the exact
`return (block, receipts)` in `BlockProcessor.ProcessOne`. It is a separate suffix/kernel/
specification/refinement and preserves the normal-tail `SourceAttachedRefinement` boundary.
Schema 2 / extractor 1.0.0 is independently accepted for the conditional source-audited boundary;
its `source-admitted` artifact marker is not a claim of independent acceptance.

The source admission pins `ProcessOne`, its private `ValidateProcessedBlock`, exact-base
`PostValidation`, `StoreTxReceipts`, the `IBlockValidator` contract and `BlockValidator`'s
validator-side `suggestedBlock.GeneratedBlockAccessList` observation, plus the `NoValidation` and
`StoreReceipts` enum values. Each anchor records its source path, FQN/symbol, canonical syntax,
line span, typed operation, actual Roslyn CFG block/edges and block membership, and data-flow
relation. Eight semantic source trees close the processor partials, interfaces, executor, and receipt
storage contract. Four separately pinned compiler-support trees and the normal tail's pinned metadata
inventory supply the real compilation context; support compilation does not prove its implementations.
The adapter carries separate
premises for exact-base dispatch, standard sequential/nonparallel BAL-disabled routing, a normal
`ProcessBlock` result, validator observation, `PostValidation` normal return, `InsertDeferred`
normal return, rejection cleanup/diagnostic normal return, and absence of additional modeled
field/identity mutations or `CommitTree`, reset, or head effects.

The suffix state is deliberately explicit:

- `Completed` copies `AccountChanges`, `ExecutionRequests`, `GeneratedBlockAccessList`, and
  `EncodedBlockAccessList` in source order, using `processed ?? suggested` for the encoded-BAL
  field, observes `InsertDeferred` only after publication when `StoreReceipts` is selected, and
  returns the processed block together with the same receipts.
- `Rejected` is only the validator-false path with validation enabled. It retains any proposed
  `GeneratedBlockAccessList` validator observation, disposes account changes first, and ends in
  `InvalidBlockException`; it has no PostValidation, store, or return event.
- `Escaped` covers unsupported boundaries and generic validator/cleanup/PostValidation/storage
  exceptions. Its final state is unknown, since a throwing external call can partially mutate
  state. `NoValidation` has an explicit skipped event and requires the proposed BAL to be unchanged.

`Generated/ProcessOneValidatedPublication.*` is the independently accepted schema-2 artifact set.
Default `--check` validates finalization only. The separate `--publication-check` rederives the
suffix IR from live compiled source, validates its strict serialized records, and
requires byte-exact Lean re-emission and the exact 52-entry dependency closure. The package test
gate additionally checks both published JSON schemas and their drift controls. Draft markers,
unknown or duplicate JSON fields, nulls, missing entries, and stale identities fail closed.
`--publication-extract` first requires the settled ReceiptTerminal pins schema 3 and manifest
schema 9/extractor 1.9.2, with an independently enumerated
exact 12-source inventory, matching live source hashes, and matching IR/Lean artifact hashes.
Malformed envelopes, unknown fields, duplicate properties, and jointly omitted sources fail closed.
Both normal-tail and suffix admission call the exact Fold `ValidateCheckedIn` implementation through
the project reference; compiler references come from Fold's own validated reader. `PROMOTION_PINS.json`
records `accepted-upstreams` and pins accepted Fold schema 9/extractor 1.8.0 and ReceiptTerminal
schema 9/extractor 1.9.2 artifacts, while `DependencyAudit` freezes the complete enumerated
own/Fold/Receipt/accounting semantic and proof lineage. Suffix admission adds its own inputs and the
normal-tail checked artifacts. No upstream acceptance is inferred merely from mutually matching files.
`PUBLICATION_PROMOTION_PINS.json` separately pins the accepted finalization artifacts. Upstream
acceptance does not independently accept the suffix. Artifact publication uses flushed same-directory
temporary files and atomic replacement, with the manifest written last as the consistency marker.

The suffix-specific inputs are `ProcessOneValidatedPublicationExtractor.cs`, `PublicationSemanticAudit.cs`,
`PublicationReceiptDependencyAudit.cs`,
`PublicationModels.cs`, `PublicationKernel.lean.template`,
`PROCESS_ONE_PUBLICATION_SOURCE_PINS.json`, both `Schema/process-one-validated-publication.*.schema.json` files,
`Specification/ProcessOneValidatedPublication.lean`, `Refinement/ProcessOneValidatedPublication.lean`,
and `Vectors/ProcessOneValidatedPublicationVectors.lean`. The relation uses field-by-field publication constraints
and exact traces from an independent specification; it has no whole-output relation premise.
The normal source-attached theorem retains the emitted source closure and actual FQN/site records.
Block/receipt identities, artifact snapshots, flag values, and external normal-return/effect
observations remain adapter inputs. The theorem does not derive the flags from a concrete
`ProcessingOptions` mask or consume a proved finalization result. Escapes retain only the known
event prefix and assert no final state; receipt contents, runtime dispatch, CLR behavior, disposal
implementation, validator internals, receipt durability, and downstream branch/chain behavior remain open.

`Verify-Publication.ps1` runs `Verify.ps1` with the current 604-test minimum, checks the suffix,
compares two fresh suffix extractions against all three checked artifacts, builds its Lake target
with `--wfail`, checks all four direct Lean targets, and runs fifteen semantic mutations
(eight publication plus seven finalization) and the complete
descendant-axiom gates before a final `--publication-check`. The frozen
`PUBLICATION_EXPORTED_THEOREMS.txt` inventory has 1599 entries: 294 ProcessOne exports and the
1305-entry upstream lineage. Its SHA-256 is
`9af770169f8539bf833aa4519a07b6a52fd0bffad5d513d40546f9ba0bf72312`.
The census includes generated descendants under all four ProcessOne namespaces, with only
`propext`, `Classical.choice`, and `Quot.sound` allowed transitively. The 294 publication exports
partition into 125 generated, 93 specification, 56 refinement, and 20 vector exports. Independent
executable review accepted the full gate and final artifact hashes. This does not complete the block-processing claim.

## Verification and accepted artifacts

The package uses `SOURCE_DATE_EPOCH=1789035784`, `MSBUILDDISABLENODEREUSE=1`, `lake --wfail`,
and an explicit `Eip803x` library. The current independently accepted shared test gate passed
604/604 cases, with no failures or skips, after publication hardening.
A passing unchanged Roslyn extraction baseline precedes compile-first mutations with named
diagnostics. Seven finalization Lean mutations first compile their altered kernel and then must
fail the complete proofs semantically. Eight additional publication mutations run through
`Verify-PublicationMutationGates.ps1` using `-IncludePublication`; they belong to the accepted
publication gate, bringing its combined total to fifteen.
Recursive duplicate-JSON rejection, token-aware proof-placeholder rejection, exact Lean re-emission,
and atomic replacement failure-preservation controls are part of the same gate.

`DECLARED_THEOREMS.txt` records 46 source declarations across both accepted slices.
It is not the complete compiler export
inventory: `Verify-Axioms.ps1` enumerates every descendant theorem, including generated equation
theorems, compares the complete export set, and checks transitive `collectAxioms` results against
only `propext`, `Classical.choice`, and `Quot.sound`. The frozen `EXPORTED_THEOREMS.txt` contains
1305 exports: 485 finalization, 223 Fold, 407 Receipt, and 190 Eip accounting-lineage exports.
No ProcessOne export is included. Missing, unexpected, duplicate, nested, forbidden-axiom, and
omitted-export controls passed, including controls under all 12 audited namespace roots.
The inventory SHA-256 is `dea78ac75c216204477c271343fedacf453649ca3097f77e92867f8efdbcdfcd`.

Accepted SHA-256 identities:

The current publication promotion includes the independently reviewed shared directive-admission
and gate-dependency cascade. Finalization's IR and Lean bytes are unchanged; its manifest is refreshed.

| Artifact | SHA-256 |
| --- | --- |
| Finalization IR | `1d599ef71c083c5aa4a80fbc0e44d99f807d09dc0fa17f9e2d5b94047e45145a` |
| Finalization manifest | `99823606ef2143b4fb00aeb6af02994d4f707dc757ae719d9df6ccdb34ce0e7b` |
| Finalization Lean | `f84cd3be1182df43429d6ea75ec8df360059c2fc4a183270748118cc78a8064b` |
| ProcessOne IR | `201d8d2e6163d3a75b439cebf0e5fa7754792c3ba63de70b246939bc9d4cad3a` |
| ProcessOne manifest | `547dbea87c2c26539a11b4e3db1f2d22db4d7590a6a68c1695a46f70518bdf3d` |
| ProcessOne Lean | `7f47700c2f38717b37c6f8bb7e187021d9af8e6a641389fa0bd27865122eec5d` |
| Fold IR | `0580b07867d42ffcd210a2044cf15f6e30193ba4170486c97b20caad54cbdda5` |
| Fold manifest | `f531d9ca1bf15382ef74b2c055ad45ca8352163815a35b07e382a20f257c5a56` |
| Fold Lean | `e1c7c38d5f90662a649fff77b8fa7bbc2543ea25e8eea1ac845a4d15ffbf5516` |
| Receipt IR | `0bf061d43d54e3eb9bdc7ccff4d0541d6dee189ac1ccea7f139095d3e1b7945a` |
| Receipt manifest | `3d1ec03bce750645bc29d8fb51ac94321508364735b9a65a987043100f74bd8e` |
| Receipt Lean | `028d11a14e031aaedce27c71b26e2c69cf16e759ff3ac870373ead10f6971180` |

Executable evidence from 2026-09-16 is in `D:/tmp/formal-verify/`:
`process-one-full-604-first-20260916.log`, `evm-solution-after-publication-20260916.log`,
`finalization-post-publication-solution-check-20260916.log`, and `publication-post-solution-check-20260916.log`.
They record full 604/1305/1599 package success, the Release `Evm.slnx` warning-as-error build with
zero warnings/errors, and both post-solution live-source/re-emission checks. The root verification
script invokes `Verify-Publication.ps1`, which runs `Verify.ps1` once without recursion.
Accepted-status documentation checks are recorded in `publication-accepted-doc-consistency-20260916.log`,
`finalization-accepted-doc-check-20260916.log`, and `publication-accepted-doc-check-20260916.log`.
The 38-entry `DependencyAudit` closure includes `PROMOTION_PINS.json`, proof modules, the export
inventory, and mutation/axiom scripts, but not these package docs or global status docs. Those
documentation-only changes do not require changing generated bytes; deterministic extraction and
live checks still verify the exact artifacts. All supplied-FoldResult, receipt index/log-count,
static-dispatch, external-hook, and separate subscriber/commit normal-return premises remain.
