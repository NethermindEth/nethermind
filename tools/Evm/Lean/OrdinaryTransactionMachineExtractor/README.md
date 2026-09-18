# OrdinaryTransactionMachineExtractor

This additive package records a compiler-closed Roslyn extraction boundary for one ordinary
transaction success route. It is deliberately narrower than `TransactionProcessor`: exact
`ExecutionOptions.Commit`, standard Ethereum processor, `ProcessOne` BAL route selection,
sequential direct-inner/non-BAL block processing, non-create live-recipient simple transfer,
the exact fast-path guards (`!_isCodeOverridable`, null authorization list, and
`!ForceSimpleTransferDisabled`), common fee/finalization, and the pinned base ReceiptTerminal fold.

The package does not execute C#, model the EVM frame, or certify production equivalence. Effects
and normal returns are opaque typed observations. `Generated/OrdinaryTransactionMachine.lean`
uses independent types and rejects malformed settled `.ok` entries without mutating the receipt
or gas prefix. `Specification/OrdinaryTransactionMachine.lean` is an independent observational
specification; `Refinement/OrdinaryTransactionMachine.lean` provides a `ReceiptTerminalChain`,
proof-carrying callback normal-return premises, and local fieldwise bridge obligations. Its
primary theorem proves an adapter-supplied successful settled-list fold by induction, including
receipt/gas/conditional-forwarding event order, indices, and result constructors, without
assuming a whole-run relation. It does not relate that list to `Block.Transactions` and therefore
is not a source-composition or production-refinement theorem.

The direct executor boundary is source-closed to the concrete non-virtual
`BlockValidationTransactionsExecutor.ProcessTransactions` outer entry selected by the DI route;
the BAL-disabled decorator arm is admitted only for its sole, typed
`inner.ProcessTransactions(block, processingOptions, receiptsTracer, token)` return. The
BAL-enabled decorator and its parallel implementation remain excluded. The admitted context
route is source-bound through `ProcessBlock`, the parallel decorator's BAL/inner forwarding,
the direct executor, the execute adapter, and the processor. Exact-Commit routing is bound through
`ExecuteTransactionProcessorAdapter.Execute`, `ITransactionProcessorExtensions.Execute`, and
the `CreateExecuteAdapter` registration/factory chain. The exact primary-constructor types and
`BlockProcessor._blockTransactionsExecutor` capture close the registered decorator/inner/adapter
objects to those calls. Receipt forwarding records both typed
guards rather than fixing them to false.
The `IBlockValidationModule -> StandardBlockValidationModule` registration is pinned as the owner
of the direct-executor/decorator registration pair.

Every admitted source declaration is selected from a hard-coded member ledger by exact relative
source unit, fully qualified metadata name (including generic arity and nested `+` names), member
kind, canonical signature, ref-kinds, and multiplicity. Invocation/fluent targets, receivers,
constructors, primary-constructor parameters, fields/properties, enum members, and base types are
resolved in the caller's specified Roslyn compilation group and compared with
`SymbolEqualityComparer.Default`; zero, duplicate, relocated, overload, alias, and simple-name
shadow routes fail at the local identity gate.
The pinned source set includes the reached VM, read-only state, exact `IWorldState.Commit`
contract, world-state balance extension,
block-tracer, block-executor interface, adapter-factory, validation-module, and core DI-extension
declarations. Framework symbols and the registered `Nethermind.State.WorldState` implementation
type are instead owned by the exact compiler-reference inventory; its generic registration type
argument is still compared by exact metadata identity.

`MACHINE_SOURCE_PINS.json` is the sole local source-pin authority; the compiler inventory is the
fail-closed reference identity input. The checked
draft artifacts are marked `static-draft` because this lane cannot run dotnet/Lake/Lean while the
serialized transaction-preparation lane is active. Run `Verify.ps1` after that lane releases the
build tools; it regenerates independently into two scratch directories, checks byte identity,
validates the manifest, runs mutation tests, and directly checks generated, specification,
transaction-state, economics, execution-boundary, refinement, and vector Lean targets.
Publication uses the extractor's
atomic replacement after all source/IR validation has completed; a later gate should still treat
the three artifact hashes as one publication set. The extractor also checks the transitive
ReceiptTerminal source-pin/manifest closure, delegated accounting artifacts, and compiler
inventory. The presently checked ReceiptTerminal source manifest is stale against its source-pin
file, so re-emission of that settled dependency is an explicit prerequisite; the ordinary package
fails closed until then.
