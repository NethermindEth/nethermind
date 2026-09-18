# Sequential block transaction fold extractor

The checked-in artifacts use schema 9 / extractor 1.8.0 and the independently accepted
Receipt schema 9 / extractor 1.9.2 dependency. `RECEIPT_DEPENDENCY_PINS.json` freezes its
source-attached artifacts; extraction and checking require their exact bytes and the complete
upstream validator. The theorem remains conditional on supplied terminal observations.

This snapshot is independently accepted after 314/314 C# cases, 16 Lean scenarios, ten
compile-valid semantic mutations, the standard-only 49-export axiom audit, deterministic
artifact checks, an `Evm.slnx` Release warning-as-error build, and post-solution `--check`.
Acceptance does not discharge the open source-composition obligation below.

This is a narrow, additive source-audit package for the exact-base direct-inner sequential
transaction executor slice under an explicit BAL-disabled route premise. It binds
`BlockValidationTransactionsExecutor.ProcessTransactions` and its
`ProcessTransaction` helper to a typed Roslyn source identity, operation, and control-flow
record. It also binds the caller's `ProcessBlock` transaction call and the two surrounding
`CommitState(spec)` calls (while retaining the later excluded commit as a source-count guard).
The normal ProcessBlock route is CFG-closed: `StartNewBlockTrace` dominates the pre-fold commit,
that commit dominates the fold, the `TransactionsExecuted` evaluation postdominates the fold,
and the post-fold commit postdominates that evaluation. The event capture and null test execute
on both subscriber paths; the exact `Action.Invoke` anchor executes only on the non-null path.
The IR separately records the typed event evaluation and both successors. Admission checks
that the same captured event drives the null test and invocation, that null skips the invocation,
and that both paths rejoin before the post-fold commit.

The source closure also binds `BlockProcessor.StartNewBlockTrace`, the transaction-adapter
`StartNewTxTrace -> Execute -> EndTxTrace` order, the exact-base `BlockReceiptsTracer` reset,
receipt append/index and end-index updates, `BlockAccessListManager.Enabled` derivation, the
BAL decorator's disabled sequential fallback, and the standard-mainnet base/decorator
registrations. These are typed Roslyn `IOperation` anchors and source-order relations; auxiliary
anchors also carry their owning method, reachable CFG block, and reachability bit. Local-function
and lambda relocation is rejected at the common main/auxiliary binding boundary rather than
treated as the enclosing method's source path. The
package does not claim runtime DI activation, CLR dispatch, or C# execution. Direct gas-limit and
invalid-result throws, and the BAL-disabled inner return, must be the sole statement in the
relevant guard true arm; bypass branches are rejected. Both exception helpers must directly throw
the admitted exception constructor with the exact source argument expressions. The gas-limit
helper is bound as the enclosing executor method's local function, including its message field.
The indexed loop must be a direct `ProcessTransactions` statement. Each iteration has exactly
three ordered statements: the indexed transaction declaration, one direct helper call, and the
gas-limit guard without an else arm. Conditional/repetition wrappers, early exits, and extra
statements are rejected. Freshly extracted and serialized IR must retain the exact admitted
executor CFG blocks, edges, exits, and back edge.
The indexed projection binds the exact metadata `Nethermind.Core.Transaction` type, the
`Block.Transactions` array and loop index, and the same local at the helper call with identity
conversions and no conversion operators. Main call arguments require contextual identity
conversions; all admitted argument trees reject user-defined conversion operators.
`ProcessTransaction` and both exception helpers must retain concrete synchronous void bodies.
Their typed and serialized identities reject async, iterator, partial, extern, abstract, and
bodyless variants; a direct throw expression alone cannot establish synchronous propagation.
Resolved symbol attributes must retain the exact existing inventory, with no arguments and
pinned metadata assembly identities. `ConditionalAttribute` is explicitly rejected,
including aliases, because it can erase calls without changing their Roslyn operations or CFG.
New execution-affecting attributes, including `MethodImpl`, are not admitted.
Callable lineage is also fixed: the transaction helper is virtual but not an override, both
throw helpers are static, and all three retain their exact declaring type, `System.Object`
base chain, executor interface closure, and empty implemented-interface-method inventory.
Every overridden method is inspected for inherited attributes; inherited conditional calls,
new base classes, and changed interface routes are rejected in source and serialized IR.

The generated Lean kernel consumes one or more already-settled ordinary transaction terminal
observations. For each observation it adopts the exact receipt/gas state from
`FinalizationObservation.trace.state`, appends `FinalizationObservation.result`, checks the
block gas limit, projects `StartNewTxTrace`/`EndTxTrace` receipt indices, advances the next
tracer index, and preserves source order. Only an `OnlyOkTerminal` observation is admitted as a
settled ordinary observation. A normal return alone is insufficient: a truthy `EvmException`/
revert result is not `.ok`, and a false `TransactionResult` is rejected as malformed input because
its source path can end the trace before throwing. The refinement-side adapter
predicate requires that terminal state to carry one-item receipt/gas-history extensions over the
prior state and to identify the transaction with the pre-`EndTxTrace` index. A normal exhausted prefix receives
`preTransactionCommit`, per-transaction trace-start/terminal/trace-end/gas-check events, then
`transactionFoldCompleted`, `transactionsExecuted`, and `postTransactionCommit`; an invalid
prefix retains the pre-commit marker but does not receive the post-transaction commit event.
The `FreshSequentialTracer` adapter and vector cover empty input explicitly: block start resets
current index to zero, clears receipt/gas histories, resets cumulative receipt gas, and projects
the production header gas before any transaction is folded.

The caller's `TransactionsExecuted` signal is source-bound between the normal executor return and
the post-fold commit. `generated_fold_refines_spec` is a conditional model theorem: it consumes
`ReceiptTerminalChain` to prove the independent inductive `FoldRelation`. Its event-tail statement
is conditional on the generated result having the completed constructor shape. There are no
caller-filled proposition fields or source-result equalities in that theorem. Production execution,
exact-base runtime route selection, fresh-tracer entry, terminal observations, and normal hook
returns are collected in `OpenSourceCompositionObligation`, an explicitly unproved definition.
Importing the Receipt refinement does not prove this composition obligation.

Roslyn metadata is admitted through the shared, byte-pinned
`ReceiptTerminalFoldExtractor/COMPILER_REFERENCE_PINS.json` closure. The IR records its exact
path, count, aggregate, and inventory hash; every platform/application reference is checked for
missing, changed, duplicate, or ambiguous path/assembly identity before binding.
`ReceiptDependencyAudit` calls the complete upstream `ValidateCheckedInArtifacts` through one
project reference and the upstream friend grant; no validator source is copied. It additionally
requires exact ordered inventories of 12 terminal sources, 38 binding sources, 434 compiler paths
(schema 2, hashes/MVIDs/selected identities), and 23 artifact/proof/implementation dependencies,
including all six delegated Lean files and the upstream control-flow, effect, and publication
validators. Source pins schema 3 includes twelve compilation support inputs: complete partial
declarations, referenced internal types, standard execution flags, and the MSBuild EvmWord alias.
Whole auxiliary compilations must have no errors. Roslyn kinds, normalized reduced
extension symbols, receiver symbols/types and argument bindings share one canonical representation.

This package deliberately does not execute C#, the EVM, or the transaction processor. It does not
model roots, trie/RLP/hash computation, rewards, withdrawals, requests, parallel worker execution,
system transactions, persistence, background work, runtime DI activation, CLR/JIT behavior, or
rollback/durability. The `balEnabled = false` input is the explicit direct-inner route boundary;
the source registration/guard evidence is included, but the runtime route remains an explicit
uninterpreted premise. Invalid-result and malformed entries are logical input projections: a
production false-result `EndTxTrace` poststate is not claimed. The adapter predicates in
`Refinement/` are explicit obligations for whoever supplies settled terminal observations.

## Files

- `Extractor.cs`, `ConditionalSignalAdmission.cs`, and `Models.cs` — fail-closed typed Roslyn extraction and serialized IR.
- `Generated/` — checked-in source-bound IR, manifest, and theorem-free Lean kernel.
- `Specification/` — independent declarative relation over the same settled terminal boundary.
- `Refinement/` — map/refinement relation and adapter obligations.
- `Vectors/` — executable success, invalid-prefix, gas-limit, and unsupported-control vectors.
- `Test/` — determinism, source/IR mutation, and generated-kernel checks.

`Verify.ps1` fixes `SOURCE_DATE_EPOCH=1789035784`, compares two fresh extractions byte-for-byte, uses
`lake --wfail build` plus direct warning-as-error Lean checks, and runs source/IR/dependency,
semantic-mutation, and frozen-axiom gates. The root verifier invokes it after Receipt.
Publication writes flushed same-directory temporary files and atomically replaces destinations,
with the manifest last. Its gates cover 314 C# cases, 16 named Lean scenarios, ten compile-valid
semantic mutations, and all 49 theorem exports under the Specification/Refinement/Vectors roots,
including generated descendants. The axiom gate allows only the standard Lean axioms and checks
nested unexpected exports and injected forbidden axioms. This is artifact publication, not a
database-durability claim.
