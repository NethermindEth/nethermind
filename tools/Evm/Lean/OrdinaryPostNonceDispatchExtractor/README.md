# Ordinary post-nonce dispatch extractor

This package lowers a bounded, ordinary standard-mainnet slice of
`TransactionProcessorBase<TGasPolicy>.Execute(6)` into a theorem-free Lean
transition. The slice begins immediately after successful `IncrementNonce` and
ends at the typed call to `ExecuteSimpleTransfer` or `ExecuteEvmTransaction`.

The executable C# is a Roslyn source extractor, not a semantic lookup table. It
parses the admitted source files, binds the relevant declarations and
invocations to symbols, records the resolved `IOperation` kind/type and data-flow
sets together with receiver symbols and CFG blocks, checks the source
order/dominance boundary, and lowers the bound guard/effect nodes through a
finite typed Lean-term vocabulary. The live `restore`, effective-commit,
`prepare`, `simpleDecision`, lookup, commit, control, and available-gas
result-classification terms are emitted from the attached source-expression
trees; their source spelling is also retained as inspectable metadata. A
reviewed source/dependency closure is required for checked-in artifacts. The
emitter accepts only complete canonical forms in its finite lowering vocabulary,
so an unrecognised source change fails closed.

Roslyn compiles the admitted source units against an explicit, deterministic
`Nethermind.Init/release` production assembly closure. The transaction
processor unit includes the exact live interface, system-processor, standard
dispatch, metrics, substate, settlement, and virtual-machine-interface source
support listed in `Admission/ProductionClosure.txt`; the code-repository unit
also compiles the actual production `Metrics.cs` and `Metrics.std.cs` files.
The CodeInfo unit likewise includes its exact jump-destination analyzer support.
These units close the internal and source-defined signatures that the modeled
code refers to. The out-of-scope VM body is not compiled in this bounded slice:
the admitted `VirtualMachine.cs` declaration and the checked-in
`Admission/VirtualMachineStaticsAdapter.cs` declaration form an explicit
compiler-only adapter boundary. TransactionProcessor calls bind to that
adapter's empty body for source analysis, while the exact
`Nethermind.Evm.VirtualMachineStatics.RestoreRipemdTouch` production metadata
symbol is independently checked. The boundary is fail-closed on return type,
static/accessibility modifiers, parameter names/types/ref-kinds, target
assembly, candidate/ambiguous symbols, and error symbols. The adapter body is
not a semantic oracle, and its path and hash are recorded in the generated IR.
The compilation uses the source project's assembly identity for friend-access
checks, and rejects all compilation errors plus error-type, candidate, and
ambiguous bindings. This provides a source-closure semantic check for the
admitted nodes; it is not a claim about the full repository build, CLR, or JIT.

The production extractor still requires the reviewed canonical forms exactly.
The test-only unvalidated emitter path is deliberately fail-closed as well:
changing a source spelling, typed expression tree, binding, conditional arm, or
adapter premise without a matching source-derived identity is rejected before
Lean emission.

The generated module contains no proof. `Reference/` has an independently
named decision structure, and `Refinement/` states the universal generated
refinement and lifecycle erasure results. `SimpleHandoff` and `EvmHandoff` are
terminal observations carrying the typed call inputs; this package does not
compose either observation with the downstream execution implementation. The
ordinary-stateful prefix and transaction lifecycle artifacts are dependencies
for the explicit boundary only; their bytes are never interpreted as the
semantics of this package.

The model treats code lookup, `WorldState.Commit`, and available-gas creation as
effectful observations. The option initializers and the `CalculateAvailableGas`
conditional are parsed and symbol-bound as complete source expressions before
their finite Lean terms are emitted. The generated gas classifier maps the
observed condition result to the exact `TransactionResult` member names carried
by the bound source arms; it does not claim to implement the external gas policy.
The lookup request preserves its recipient,
`!spec.IsEip8037Enabled`, and spec; the commit request preserves the selected
tracer and `commitRoots:false`. Lookup and precommit may escape. A rejected available
gas request returns `GasLimitBelowIntrinsicGas`, carries the five zero
`EthereumGasPolicy` out fields, and performs no dispatch. A successful EIP-8037
lookup preserves its returned delegation designator; target code loading stays
downstream of the EVM handoff.

## Scope

See [SCOPE.md](SCOPE.md), [SOURCE_AUDIT.md](SOURCE_AUDIT.md), and
[TEST_PLAN.md](TEST_PLAN.md). The package is deliberately independent of the
parent `Evm.slnx` and root Lake project.

## Local commands

The serialized build lane is controlled by the parent task. Once that lane is
available, `Verify.ps1` performs the C# extractor tests, deterministic fresh
generation, checked-in artifact comparison, Lake build, direct Lean targets,
and the placeholder scan in a unique temporary directory.
