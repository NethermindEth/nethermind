# Pinned production behavior graph

`source-map.json` contains 41 exact file hashes with explicit roles. This remains a bounded semantic/selection map, not a 41-tree compilation. The new source auditor separately reuses the exact accepted refund compiler inventories: 157 source/selection identities, 146 selected compile trees and 226 selected references. It resolves actual Release/non-zkEVM MSBuild inputs, pins the build epoch, and checks each reference's bytes, assembly name and MVID before constructing Roslyn's compilation.

Ten roots cover the private `Execute` caller, `ExecuteEvmTransaction`, `ExecuteEvmCall`, counter/fee routing, `PayFees`, `FinalizeTransaction`, top-level frame rental/initialization, normal counter participation and the gas-policy maximum. Forty-three selected sites retain complete typed operation trees; a frozen inventory checks fully qualified methods, parameter roles/ref kinds, receivers, conversions, operator methods and scoped local declaration identities. Full CFG blocks/regions, finally edges and capture identities are recorded. Named target, exact owner/polarity, reachability/dominance and cleanup-order checks run before complete-source drift rejection.

This is an exact restricted-source admission, not a general C# translator. The existing destruction helper and frame-initialization throw helper remain in the captured source/CFG; the selected empty-destroy and normal-hook/frame-entry domain excludes executing them. New operations/callables or unsupported control-flow changes require review and updated lowering, not an automatic pin refresh.

The tool's own default C# sources and test sources are discovered separately and must all be listed in its hashed dependency roster. Per-source omission, unexpected-source and baseline-neutral lowering-source drift controls protect that boundary. SDK output/cache directories are not source inputs. This does not turn the trusted Roslyn frontend or .NET tool runtime into a formally verified compiler.

The receipt implementation is not injected into the EVM assembly compilation. Its exact accepted artifacts and theorem closure remain separate; the new continuation binds the `ITxTracer` calls and requires the precise sequential base-receipt-tracer identity/normality adapter.

The selected processor is the sealed standard `EthereumTransactionProcessor` and its `EthereumTransactionProcessorBase`/`TransactionProcessorBase<EthereumGasPolicy>` chain in `TransactionProcessor.cs`. The normal block-processing registration and `src/Nethermind/Directory.Build.targets` standard/zkEVM selection are pinned. Amsterdam construction and its interfaces are pinned; the complete inherited fork flag derivation must still be admitted, or its exact flag projection remain an explicit input contract.

## Control and paired surfaces

- `TransactionProcessor.cs:292`: `ExecuteEvmTransaction` owns context, preparation, environment/access scope, call, fees and finalization.
- `:375` and `:376`: both tracing-specialized `ExecuteEvmCall` invocations must share the computed arguments; no alternative virtual processor override is admitted.
- `:1324`: `ExecuteEvmCall` snapshot/value/code/frame boundary, VM return, access reporting, rollback/status and frame disposal.
- `:1416`: exact `RentTopLevel` entry and identity; `VmState.cs:63` and `:158` determine the false CREATE flags and initial state-gas baseline.
- `VirtualMachine.cs:478` and `:718`: the top-level substate and already-applied REVERT state-gas refund. Exceptional top-level handlers and `VirtualMachine.CallResult.cs` constrain raw observation provenance.
- `TransactionSubstate.cs`, `EvmException.cs` and `StatusCode.cs`: constructor/status/error relationships, distinct from transaction inclusion rejection.
- `TransactionProcessor.cs:1776`: ordinary refund's local copy, authorization refund, error-only halt branch and settlement; `:1543`: exceptional halt cleanup. These remain linked to the accepted refund artifact rather than a new unchecked transcription.
- `:587`: normal sequential processor counter update followed by fees. `SystemTransactionRoutingKernel.cs` supplies participation; `GasConsumed.cs` supplies the zero-execution/nonzero-state effective-gas rule.
- `:619`: transaction-field writes, exact Commit path, receipt terminal and exact executed transaction result. `:1891` and `:1897` distinguish execution status from the lossy production equality.
- `BlockReceiptsTracer.cs:38/54/83/111`: receipt append, forwarding, independent prefix update and header overwrite; gas-history/receipt metadata and error/output projections are separately visible.
- `ExecutionEnvironment`, `StackAccessTracker`, `IWorldState`, `ITxTracer`, `IVirtualMachine`, transaction/header/receipt and snapshot types bound external identities, ownership and projections.

## Accepted dependencies

`upstream-artifacts.json` freezes IR, source manifest and generated Lean for preparation, ordinary refund and receipt terminal. `stage-dependencies.json` additionally freezes the exact three central theorem names, all 37 local Lean files in their textual import closure, and nine accepted axiom-audit scripts/inventories, including the preparation module-origin inventory. The selected standard library is pinned by `lean-toolchain`. Artifact dependencies additionally enumerate own code, templates, schemas, scripts and proofs; the manifest is published last after complete in-memory rendering. `IMPORTED_DECLARATIONS.txt` separately freezes compiler-discovered declaration names/kinds by module-origin hashes and public/private counts: 37 upstream modules plus five completion modules. All 13,336 declarations are checked transitively for standard-only axioms. Textual imports and triplet identity alone are not that proof gate.

The preparation central theorem is a conditional model-to-model source-attached bridge, not a full ordinary transaction theorem. The refund theorem begins at its helper input. The receipt theorem begins at a terminal input. The new package owns the missing input construction and caller order; artifact identity alone cannot discharge those obligations.

## No production defect claim

The distinction between intermediate VM-boundary tags, REVERT/halt baselines, copied gas values, executed failures and the two counter stores describes verified source facts and model hazards. None is a newly confirmed Nethermind production bug.
