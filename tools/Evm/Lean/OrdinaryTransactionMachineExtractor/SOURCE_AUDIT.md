# Source and route audit

This is a static Roslyn admission map for the bounded ordinary machine. It is not a claim that
the client was executed. Every listed source file is byte-pinned in `MACHINE_SOURCE_PINS.json`;
the extractor also records syntax hashes, symbol/operation identities, CFG block ownership,
data-flow sets, and deterministic source order.

## Transaction route

`TransactionProcessorBase.SetBlockExecutionContext` resets both block gas accumulators before
forwarding the context to the VM. `Process` forwards to `ExecuteCore`; the ordinary branch is
selected only when `SystemTransactionRoutingKernel.UseSystemProcessor` is false. The ordinary
overload recovers the sender before intrinsic gas, then hands the cached intrinsic value to the
six-parameter overload.

The admitted normal path is source-anchored in this order:

`ValidateStatic` -> sender validation -> `BuyGas` -> `IncrementNonce` ->
`PrepareSimpleTransferFastPath` -> optional
`IWorldState.Commit(spec, IWorldStateTracer, commitRoots:false)` ->
`CalculateAvailableGas` -> `ExecuteSimpleTransfer` -> `Refund` ->
`UpdateHeaderGasUsedAndPayFees` -> `FinalizeTransaction`.

The simple-transfer guard is the live-recipient/no-code/no-delegation branch and additionally
requires `!_isCodeOverridable`, `tx.AuthorizationList is null`, and
`!ForceSimpleTransferDisabled`. The extractor binds the exact candidate expression, its call,
the preparation guard, and the no-executable-code predicate. It excludes CREATE and the
`ExecuteEvmTransaction` frame branch. Exact-Commit selection is source-bound through
`ExecuteTransactionProcessorAdapter.Execute` -> `ITransactionProcessorExtensions.Execute` ->
`Process(..., ExecutionOptions.Commit)`, including `CreateExecuteAdapter` construction and both
factory/interface registrations. The finalization observation requires a normal
`IWorldState.Commit(spec, IWorldStateTracer, commitRoots:!spec.IsEip658Enabled)` before the base
receipt terminal. The distinct tracer-free `WorldStateExtensions.Commit` overload is not either
selected Commit anchor; that extension remains in the source closure only for the admitted
balance helper.
Gas and fee behavior is represented by typed observations; no UInt256/ulong arithmetic or CLR
exception behavior is inferred by the extractor.

## Sequential block route

`BlockProcessor.ProcessOne` is included to close the route selection before the outer lifecycle:
`PrepareForProcessing` derives `Enabled` before the standard `ProcessBlock` call. The package
does not execute that preparation or resolve DI; it records the typed invocation and normal-return
premise only. `BlockProcessor.ProcessBlock` is included to close the outer lifecycle. Its typed CFG
anchors require `StartNewBlockTrace` -> exact constructed `SetBlockExecutionContext` handoff ->
pre-fold `CommitState` -> `ProcessTransactions` ->
`TransactionsExecuted?.Invoke()` -> post-fold `CommitState` on the normal path. The callback and
post-commit are post-dominance requirements, so early-return or conditional bypass mutations are
rejected. Transaction processing is the direct `BlockValidationTransactionsExecutor` only.

`ParallelBlockValidationTransactionsExecutor` is admitted only as a route guard: the exact
`!balManager.Enabled` true arm must return the inner executor directly with the source-typed
`(block, processingOptions, receiptsTracer, token)` argument order, and the `Enabled` member,
release-spec derivation, BAL-manager lifetime registration, direct executor registration, and
decorator order are all typed anchors. The outer
`IBlockValidationModule -> StandardBlockValidationModule` registration binds that nested module
to the standard validation route. Context propagation is separately pinned through the
decorator's ordered BAL-manager/inner calls, the BAL manager field assignment, the direct
executor, the execute adapter, and the processor. The primary-constructor parameter types for
the decorator, direct executor, and execute adapter are exact bindings, as is the
`BlockProcessor._blockTransactionsExecutor = blockTransactionsExecutor` capture. The direct
`BlockValidationTransactionsExecutor.ProcessTransactions` entry is required to be the
non-virtual concrete outer method on the object-base executor type; its virtual per-transaction
helper is reached only under that exact DI-selected instance. That helper passes the same
transaction, base receipts tracer, execution options, and world state to
`TransactionProcessorAdapterExtensions.ProcessTransaction`; the extension then passes the same
transaction and base receipts tracer to `ExecuteTransactionProcessorAdapter.Execute`. The public
semantic claim requires
`balEnabled = false` and `parallel = false`; BAL-enabled and parallel execution are excluded.

## Receipt/tracer route

The exact base `BlockReceiptsTracer(parallel=false)` is source-pinned. `StartNewBlockTrace`
unconditionally resets current index to zero, current transaction/tracer, receipt and gas-history
capacities, and cumulative receipt gas. `MarkAsSuccess` unconditionally appends the result of
`BuildReceipt`, conditionally forwards to the nested tracer, then conditionally forwards to the
current transaction tracer. The two guard values are explicit `SettledEntry` observations; the
generated and specification event lists include exactly the callbacks enabled by those flags.
`BuildReceipt` updates cumulative gas before constructing the receipt with the current index;
`EndTxTrace` forwards the end callback before unconditionally incrementing the index. These
assignments, append, index projection, and callback order are typed CFG/source-order anchors.
The generated adapter composes these operations through the settled ReceiptTerminal kernel
identity and checks receipt/gas observations fieldwise.

## Compiler closure

The ReceiptTerminal source pins, generated source manifest, generated kernel, refinement, and
delegated accounting artifacts are transitively byte-pinned. The extractor parses both source
pin arrays, validates every listed source file, and requires the generated manifest's source and
binding arrays to match those pins fieldwise. It also requires the manifest compiler-reference
array to match the authoritative inventory by ordered path, assembly name, and SHA-256, then
checks the manifest's IR/Lean/accounting paths and hashes against the pinned bytes. The compiler
inventory is additionally checked by path, assembly name, SHA-256, MVID, selected bit, sorted
metadata assembly dependencies, count, inventory hash, and aggregate hash. Missing, changed,
ambiguous, out-of-order, or non-canonical references fail closed before source lowering. The
machine IR and machine source manifest carry the transitive ReceiptTerminal closure identity
alongside the complete compiler identities and closure hash. The current
checked ReceiptTerminal manifest has a stale TransactionProcessor source identity relative to
its source-pin file, so this package intentionally remains blocked until that settled package is
re-emitted; no stale artifact is silently accepted.

Local semantic binding is independently fail-closed: the extractor's hard-coded source-member
ledger names the exact relative syntax tree, fully qualified metadata owner (including nesting and
arity), member kind, canonical declaration signature, parameter ref-kinds, and multiplicity.
Target ledgers cover invocation/fluent symbols and receivers, object construction, declaration,
primary-constructor parameter, field/property assignment, enum, and base-type identities. All are
resolved in the declared compilation group and compared by `SymbolEqualityComparer.Default`, so a
compile-valid same-simple-name shadow or relocated member cannot satisfy the route by text alone.
The source closure separately includes the exact VM, read-only state, world-state extension,
block-tracer, block-executor interface, adapter-factory, validation-module, and core DI-extension
declarations reached by those targets. Framework symbols and the registered
`Nethermind.State.WorldState` implementation type remain compiler-inventory-owned; every DI
generic argument is nevertheless compared by exact metadata identity.

## Explicit exclusions and obligations

- invalid-result `EndTxTrace` poststate, failure receipts, and `EvmException` semantics;
- EVM frame, CREATE, delegation, code execution, rewards, withdrawals, requests, BAL workers;
- roots, trie, RLP, hashes, persistence, background tasks, DI resolution and CLR/JIT execution;
- exact gas/fee arithmetic, construction of settled entries from `Block.Transactions`, and source
  equivalence for all opaque adapters;
- source/compiler artifact re-emission and Lean verification, pending the serialized build lane.

The test project includes compile-valid source overrides for each of these route, constructor
capture, CFG, tracer,
simple-transfer fast-path guard, exact-Commit/context/forwarding guard, and DI identities. The
identity set separately mutates a moved method, an unledgered overload, a same-simple-name method,
a constructor alias, a field-local shadow, an enum shadow, a base type argument alias, and
a fluent generic type-argument alias, and redirected fluent and ordinary invocation targets. The
test-only override hook bypasses only ordinary source byte pins and the currently stale settled
ReceiptTerminal source-manifest pair; it keeps
settled ordinary dependencies, compiler/reference bytes, receipt artifacts, and local
Roslyn/IOperation/CFG checks active.

The generated theorem-free model therefore names `OnlyOkTerminal` and normal-return premises
explicitly. The refinement wraps each normal-return Boolean in a proof-carrying proposition and
relates per-entry proofs positionally to the adapter-supplied settled list. It does not call all
normal-returning transactions successful, connect the settled list to source transactions, or
compare `TransactionResult` values as whole records.
