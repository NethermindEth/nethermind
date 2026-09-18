# Synchronous block-pipeline machine audit scaffold

This package is a package-only, source-backed architecture scaffold for the
standard-mainnet synchronous `BlockchainProcessor` route. It is deliberately
not a formal-verification claim. The package contains typed neutral state and
result records, a 17-phase route plan, explicit hook-dependency metadata, and a
Roslyn/SHA-256 source audit. It emits no generated Lean, executable kernel, or
refinement theorem.

The `audit-scaffold-unchecked` status in `SOURCE_PINS.json` means semantic
composition remains unchecked; the static source/metadata audit itself is the
independently accepted boundary described here.

The audit is anchored to the real standard DI path and the configured chain
spec, rather than treating a fork-selector helper as the runtime spec root:

`BlockProcessingModule` registers `EthereumTransactionProcessor`, `WorldState`,
`EthereumVirtualMachine`, `BranchProcessor`, `BlockProcessor`, the withdrawal,
execution-request and BAL processors, `IBlockValidator`, the blockhash and
beacon-root services, the inclusion-list checker, the transaction adapter and
reward services, and `BlockchainProcessor`; its standard validation module
registers the sequential transaction executor and the parallel decorator.
`MainProcessingContext` constructs the main `BlockchainProcessor` inside a
child lifetime scope. `ApiBuilder.LoadChainSpec` reads the configured
`chainspec/foundation.json` through `ChainSpecFileLoader`, its format detector,
and the Parity `ChainSpecLoader`; the runtime `ISpecProvider` registration is
`ChainSpecBasedSpecProvider`.
`Nethermind.Config.csproj` is pinned with the embedded-resource mapping that
connects that configured name to the pinned `Chains/foundation.json` bytes;
the loader derives `Nethermind.Config.chainspec.foundation.json` and resolves it
from the assembly containing `IConfig`.
The pinned fork markers include `MainnetSpecProvider`'s Amsterdam selector and
`Nethermind.Specs.Forks.Amsterdam`, with EIP-7778, EIP-7928, EIP-8037 and EIP-8038
enabled in that schedule; those static markers do not replace the runtime
chain-spec derivation. The config, embedded-resource mapping, and chain-spec
bytes are pinned separately. At the pinned bytes, `foundation.json` does not
configure those Amsterdam transition properties. Amsterdam is the target
schedule type, not a claim that the current mainnet config activates it.

The route labels are kept aligned with the processing manifest:

1. suggested-block validation
2. synchronous branch selection and preparation
3. sender and EIP-7702 authority recovery
4. open world-state scope at parent root
5. DAO transition when applicable
6. beacon-root system call
7. historical blockhash state change
8. user transaction fold
9. blob gas and receipt root and bloom
10. rewards
11. withdrawals
12. execution requests and system calls
13. storage and state roots
14. block access list
15. processed-header validation
16. `CommitTree` invocation
17. synchronous result classification and head/processed-chain finalization

`Specification/BlockPipelineState.lean` records logical state separately from
scope state, receipts, ordinary/system transaction observations, execution and
state gas counters, processing-header fields, BAL/requests observations, and
failure categories. `Specification/BlockPipelinePlan.lean` maps each phase to
the production hook IDs and names the dependency boundaries. A hook contract
is static metadata; no caller supplies an arbitrary relation or implementation.

`Specification/CompositionBoundary.lean` names the existing
`BlockProcessorExtractor`, `BlockReference`/`BranchReference`, ordinary
transaction, and transaction-lifecycle artifacts that the eventual proof must
compose. It records them as open composition inputs; it does not import their
current definitions or silently turn a source projection into a refinement.

The C# audit verifies pinned source identities, the checked JSON/Lean metadata
mirror (all fields of all 57 hook contracts and all 17 phase labels), the exact
handwritten Lean file roster and byte identities, 50 exact production source
pins, 80 Roslyn declarations, standard DI registrations in their owning load
methods, the configured ChainSpec derivation and target-EIP activation mapping,
Amsterdam schedule markers, `BlockHeader.CloneForProcessing` and
`CopyProcessingFields`, and source membership plus partial-order edges for
`ProcessOne`, `ProcessBlock`, `BranchProcessor.Process`, and
`BlockchainProcessor.Process`. The hook catalog is membership metadata; it is
not a false flat runtime order. The audit separately checks the transaction
loop, concrete adapter and Execute-to-Process forwarding, both ordinary
transaction terminal paths into receipt finalization, per-transaction
tracer/adapter receipt lifecycle, synchronous receipt
arm, task-local background arm, and their standard thresholds. It rejects
extra Lean files and operational proof declarations in this scaffold. Run it
from this directory with:

```powershell
dotnet run --project SynchronousBlockPipelineMachineExtractor.csproj -- --audit-pins --repo-root <nethermind-root>
```

After a Release build, run the preparation-boundary regression from this directory:

```powershell
pwsh -NoProfile -File Test/PreparationBoundary.Tests.ps1
```

It rejects the old retry disposition and misplaced preparation calls without
changing production files or source pins.

`--extract` is intentionally rejected after the audit and before any output is
created. The eight composition inputs, including `BlockSystemComposition` and
`ParallelBlockReference`, are accepted only as exact byte identities with the
expected artifact-to-path assignment; their semantic composition is still
refused. A changed pinned source, configuration, composition byte, or route
metadata entry must first receive a new reviewed source audit; changing a hash
is not admission.

The following remain open and are not hidden by the typed plan: EVM/frame and
world-journal semantics, ordinary/system transaction adapters and settlement,
precompiles and cryptography, BAL and parallel/sequential equivalence, receipt,
state and header hashing and trie/RLP details, persistence/crash/CommitTree
durability, background scheduling and exception interleavings, DI/virtual
dispatch, CLR/JIT/compiler behavior, and head/processed-chain persistence.

Connecting the pinned EIP-8037 system-call reference to the audited block route
exposed and corrected a production defect in the EIP-4788 beacon-root call: it
used a plain transaction with a 30,000,000 gas limit instead of a `SystemCall`
with 30,000,000 execution gas plus the 1,566,720 state reservoir.
