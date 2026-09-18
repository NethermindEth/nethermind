# Production source audit

Status: Stage A routing/dependency extraction is independently accepted. It is not an admitted transitive closure for executable frame settlement, concrete adapters, or runtime behavior. `BuildSources` and `BuildAdmissions` name 114 source/raw inputs and 181 exact selectors. `Admission/ProductionClosure.txt` pins every complete byte stream and canonical Roslyn tree, while the generated manifest pins every resolved syntax kind and member digest.

## Closed root and control order

The standard mainnet service wiring constructs `EthereumVirtualMachine`, the Ethereum precompile provider, and the code-info repository in `src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs`. `EthereumVirtualMachine` closes `VirtualMachine<TGasPolicy>` to `EthereumGasPolicy` in `src/Nethermind/Nethermind.Evm/VirtualMachine.cs`.

The control roots currently audited are:

| Behavior | Production root |
| --- | --- |
| transaction/frame loop | `VirtualMachine.cs:172` `ExecuteTransaction<TTracingInst>` |
| transaction projection/outer rollback | `TransactionProcessing/TransactionProcessor.cs:1312` `ExecuteEvmCall<TTracingInst>` |
| child suspension | `VirtualMachine.cs:805` `PrepareNextCallFrame` |
| exceptional child/top closure | `VirtualMachine.cs:627` `HandleFailure`; `:835` `HandleException` |
| precompile path | `VirtualMachine.cs:908` `ExecutePrecompile`; `:1108` `RunPrecompile`; `:1154` `ExecutePrecompileCall` |
| bytecode path | `VirtualMachine.cs:1236` `ExecuteCall`; `:1334` `RunByteCode` |
| handler specialization | `VirtualMachine.ExecutionHandlers.cs:17` `GetExecutionHandlers`; `:43` `InitializeFrameCore`; `:60` `RunPrecompileCore` |
| four dispatch tables | `VirtualMachine.Dispatch.cs:56` `PrepareOpcodes`; `:141` `RunDispatchLoop`; `:210` `ExecuteOpcode`; `:316` `ExecuteJumpIfOpcode` |
| 256-entry construction | `VirtualMachine.OpcodeHandlers.cs:64` `GenerateOpcodeHandlers` |
| parent commit/restore | `VmState.cs:274` `RestoreStack`; `:321` `CommitToParent`; `StackAccessTracker.cs:68` `Restore` |
| bounded parent stack | `VmStateStack.cs` `Push` and `Pop` |
| mainnet provider map | `Nethermind.Blockchain/EthereumPrecompileProvider.cs` private `Precompiles` property and `GetPrecompiles` |
| fork precompile membership | `Nethermind.Specs/ReleaseSpec.cs` `BuildPrecompilesCache` and `IsPrecompile` over the inherited Amsterdam feature flags |

The required production order is represented as `enterFrame -> dispatch -> suspend -> runChild -> merge -> resume -> closeTop`. The independent reference now derives the success, revert, exception, and code-deposit-failure sequences from retained frame discriminants. Individual gas-policy, snapshot, RIPEMD restoration, code insertion/deletion, and parent-commit operations remain typed source-bound leaves for future generated definitions and composition theorems.

Within `ExecuteTransaction`, the observed order is more specific:

1. cache tracer capabilities, select the fork-specialized opcode table, install world/code/state dependencies, and open `FrameCleanupScope`;
2. for a fresh frame, clear prior returndata; then choose precompile or bytecode;
3. for fresh bytecode, report action start before the transfer log; for a precompile, perform the analogous actions inside `ExecutePrecompile`;
4. on `Suspend`, push the parent and make the child current without recursively consuming the CLR stack;
5. on an ordinary child success, restore the parent, merge gas, process RETURN or CREATE/code-deposit data, commit the child only when successful, then repay the state-gas spill;
6. on explicit revert, restore remaining execution/state gas and snapshots without committing child world/log/destroy changes;
7. on `HandleException`, report the action error before restoring the child snapshot; on `HandleFailure`, restore the snapshot before reporting zero remaining instruction gas, the operation error, and the action error;
8. on a nested non-direct precompile soft failure, clear child execution gas and take the ordinary `ShouldRevert` merge; an inline direct precompile soft failure instead pushes failure in the current opcode frame and continues there;
9. on invalid or unaffordable code deposit, remain inside successful child CREATE settlement and apply `RevertRefundToHalt -> CREATE state-gas credit -> advanced-refund removal -> snapshot restore -> conditional physical-account deletion -> RIPEMD restoration -> action error`;
10. preserve `TransactionSubstate.ShouldRestoreRipemdTouch`; after an outer transaction rollback, `TransactionProcessor.ExecuteEvmCall` restores the snapshot and then consumes that latch through `VirtualMachineStatics.RestoreRipemdTouch`;
11. dispose completed/abandoned frames on every exit from the boundary.

## Direct source groups

The profile includes the following exact source groups. The first row shows full raw paths; every later `Nethermind.*` path in the table is relative to the common `src/Nethermind/` prefix. `BuildSources` contains the full case-sensitive path for every entry and will make them fail-closed inputs once pinned.

| Group | Included files |
| --- | --- |
| standard build selection | `src/Nethermind/Directory.Build.props`, `src/Nethermind/Directory.Build.targets` |
| production wiring | `Nethermind.Init/Modules/BlockProcessingModule.cs`, `Nethermind.Blockchain/EthereumPrecompileProvider.cs` |
| frame root | `Nethermind.Evm/IVirtualMachine.cs`, `VirtualMachine.cs`, `VirtualMachine.CallResult.cs`, `VirtualMachine.Dispatch.cs`, `VirtualMachine.ExecutionHandlers.cs`, `VirtualMachine.OpcodeHandlers.cs`, `VirtualMachine.std.cs` |
| closed execution state | `DispatchFlags.std.cs`, `Instruction.cs`, `EvmException.cs`, `StatusCode.cs`, `BlockExecutionContext.cs`, `TxExecutionContext.cs`, `ExecutionEnvironment.cs`, `ExecutionType.cs`, `ExecutionMetricsCounters.cs`, `TransactionSubstate.cs`, `VmState.cs`, `VmStateStack.cs`, `PoppedAddressCache.cs` |
| outer rollback consumer | `Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs` |
| standard stack/memory | `EvmStack.cs`, `EvmStack.std.cs`, `StackPool.cs`, `StackPool.std.cs`, `EvmPooledMemory.cs`, `StackAccessTracker.cs` |
| code selection/analysis | `CodeAnalysis/CodeInfo.cs`, `CodeAnalysis/CodeInfoFactory.cs`, `CodeAnalysis/JumpDestinationAnalyzer.cs`, `CodeAnalysis/JumpDestinationAnalyzer.std.cs`, `ICodeInfoRepository.cs`, `CodeInfoRepository.cs`, `CacheCodeInfoRepository.cs` |
| precompile boundary | `IPrecompileProvider.cs`, `Precompiles/IPrecompile.cs`, `CodeDepositHandler.cs`, `Instructions/EvmCalculations.cs` |
| provider address owners | the 18 concrete base declarations under `Nethermind.Evm.Precompiles`, from `ECRecoverPrecompile.cs` through `SecP256r1Precompile.cs`, listed individually in the profile |
| gas/frame merge | `GasPolicy/IGasCost.cs`, `IGasPolicy.cs`, `EthereumGasPolicy.cs`, `PrecompileGasPricingKernel.cs`, `StateGasChargeKernel.cs`, `StateGasTransitionKernel.cs`, `StateGasTransitionAdapterKernel.cs` |
| world adapter boundary | `State/IWorldState.cs`, `State/Snapshot.cs`, `State/WorldStateExtensions.cs` |
| tracing boundary | `Tracing/ITxTracer.cs`, `Tracing/TracerExtensions.cs`, `Tracing/TraceStack.cs`, `Tracing/TraceMemory.cs` |
| word/build primitives | `Nethermind.Core/Address.cs`, `GasCostOf.cs`, `IJournal.cs`, `Collections/JournalCollection.cs`, `Collections/JournalSet.cs`, `Precompiles/PrecompiledAddresses.cs`, `TypeFlags.cs`, `Extensions/Bytes.cs`, `Extensions/Bytes.std.cs`, `Extensions/EvmWordExtensions.cs` |
| block/spec inputs | `Nethermind.Core/BlockHeader.cs`, `Specs/IReleaseSpec.cs`, `Specs/IReleaseSpecExtensions.cs`, `Specs/IReleaseSpecExtensions.std.cs`, `Nethermind.Specs/ReleaseSpec.cs` |
| Amsterdam lineage | `NamedReleaseSpec.cs` plus `00_Olympic.cs` through `21_BPO2.cs` and `25_Amsterdam.cs` as listed explicitly in the profile |

## Audited direct dependency edges

- `ExecuteTransaction` selects tracing/cancellation through `DispatchFlags`, specializes fork features through `SpecFlags.std.cs`, resolves execution handlers and opcode tables, and alternates `ExecutePrecompile` with `ExecuteCall`. The source-projected input includes instruction-tracing, cancellation, and action-tracing capabilities; instruction tracing selects the table half, cancellation selects its specialization, and action error callbacks remain independently conditional.
- `ExecuteCall -> RunByteCode -> RunDispatchLoop -> ExecuteOpcode/ExecuteJumpIfOpcode -> generated handler table` is the bytecode dispatch chain.
- `PrepareOpcodes<TTracingInst> -> PrepareOpcodes<TTracingInst, TCancelable> -> OpcodeTable.GetExecutionHandlers/GetHandlers -> GenerateOpcodeHandlers<TTracingInst, TCancelable>` is admitted explicitly; it owns the four distinct table identities and every table/byte handler root.
- A non-return `CallResult` carries `StateToExecute`; `PrepareNextCallFrame` pushes the parent through `VmStateStack` and makes the child current.
- A child return pops the parent, calls `EthereumGasPolicy` refund/restore operations, performs create/deposit handling or regular returndata handling, and commits `VmState` only on the successful path.
- The settlement admission set explicitly includes `Refund`, `RepayStateGasSpill`, `RestoreChildStateGas`, `RestoreChildStateGasOnHalt`, `RevertRefundToHalt`, `RefundStateGas`, `DiscardStateGas`, `AddStateGasRefundToReservoir`, `RemoveStateGasRefundFromReservoir`, `TryConsumeStateAndExecutionGas`, `CalculateStateGasSpill`, `UpdateGas`, `UpdateGasUp`, and `ClearExecutionGas`, plus the three state-gas kernel types they call.
- `VmState.Dispose -> StackAccessTracker.Restore -> JournalSet.Restore/JournalCollection.Restore` is admitted as the direct access/log/destroy rollback chain. The concrete world-state journal behind `IWorldState.Restore` remains an adapter premise.
- `TryChargeAndDepositCode -> ICodeInfoRepository.InsertCode -> CodeInfoRepository/CacheCodeInfoRepository.InsertCode -> IWorldState.InsertCode` is admitted separately from the failure sequence. The failure sequence retains `IsCreateOnPreExistingAccount` (the physical-leaf result produced by CREATE collision analysis), removes `StateGasRefundAdvanced`, restores the snapshot, and calls `IWorldState.DeleteAccount` only for a newly physical account.
- Revert and exception paths restore world/access state from `VmState` snapshots, retain the specified returndata behavior, and take different gas merge paths. `HandleException` traces before restore; `HandleFailure` restores before operation/action traces.
- Failed nested precompiles with `PrecompileSuccess == false` but no exception have execution gas cleared inside `ExecutePrecompile` and then follow the ordinary `ShouldRevert` parent merge. The direct STATICCALL fast path is owned by the admitted CallCreate package and continues in its existing opcode frame.
- Code-deposit invalid/OOG is not a `CallResult` exit kind: `TryChargeAndDepositCode` reaches it only after successful child CREATE return and applies halt refund restoration, CREATE credit, advanced-refund removal, world rollback, physical-account deletion, RIPEMD restoration, and action tracing in that order.
- `PrepareTopLevelSubstate` copies `_currentState.Refund` and `_shouldRestoreRipemdTouch` into `TransactionSubstate`. `TransactionProcessorBase<EthereumGasPolicy>.ExecuteEvmCall` restores the outer snapshot before replaying the sticky RIPEMD touch; this consumer is included as a bounded source/admission edge, not as a transaction-processor verification claim.
- `CodeInfoRepository` recognizes a precompile only when `ReleaseSpec.IsPrecompile(address)` accepts it; the membership cache is built from canonical `PrecompiledAddresses` under the inherited Amsterdam feature flags. `ExecutePrecompile` then obtains `CodeInfo.Precompile`, debits `PrecompileGasPricingKernel`, invokes `IPrecompile.Run`, and returns through the same frame settlement loop.
- Standard stack operand decoding crosses `EvmStack.std.cs -> EvmWordExtensions.cs -> Bytes.std.cs`; all three implementation paths and the `EvmWord` build alias are therefore source inputs.
- Amsterdam behavior is inherited through the complete named-fork chain, so no single `Amsterdam.cs` pin is sufficient.

## Adapter premises, not silently accepted source

The current frame model represents the following through tokens/callbacks rather than claiming their implementations:

- concrete `IWorldState`, trie, storage, code-cache, and journal implementations;
- concrete `ITxTracer` implementations and cancellation-token scheduling;
- native/cryptographic bodies behind `IPrecompile.Run`;
- BCL/runtime behavior (`Span`, `Unsafe`, `BinaryPrimitives`, pooling, exceptions, function pointers, and the JIT).

These are named open obligations in the IR. A future source-closure pass must either add and admit a concrete implementation or retain a precisely stated adapter theorem.

## Stage A result and known incompleteness

- All 114 inputs and 181 selectors resolve exactly once and carry byte/syntax digests. Whole-input mutation testing covers the 114 production/raw files, 14 package manifests, 14 routing IRs, and 18 precompile identity files.
- The four tables contain exactly 1024 ordered routes: 612 enabled and 412 explicit bad-instruction routes, exactly 103 per table. The explicit `INVALID` byte retains its source-derived `InvalidOpcode` root; every unassigned byte retains the source-derived table-specific `BadInstructionOpcode` initializer root. SLOTNUM is the sole sibling overlap and is resolved to `ControlFlowOpcodeExtractor`; no wildcard owner exists.
- CALLDATALOAD has four exact production-derived roots composed with the independently accepted `CallDataLoadOpcodeExtractor` manifest, routing IR, proof module, and closed Amsterdam operational theorem identity.
- Production table selection and handler assignment lowering use exact Roslyn nodes: the four `GetHandlers<TTracingInst, TCancelable>` flag substitutions, the 256-entry default loop, each `lookup[(int)Instruction.X]` left-hand side, and each direct or transitive handler-helper right-hand side. The evaluator replays the pinned `NamedReleaseSpec` inheritance chain to Amsterdam using only unconditional simple assignments and evaluates active `ReleaseSpec`, extension, and `SpecFlags` predicates, including conditional and two-flag switch selectors; inactive assignments do not enter the route set. Duplicate active terminal assignments are rejected rather than overwritten. Sibling ownership is applied only after the single active production terminal assignment lowers to the same closed root.
- All 14 admitted opcode dependencies bind unique package manifests, routing IRs, proof modules, and 16 adequate fully qualified theorem/signature identities. Persistent and Transient each require both byte-level operational theorems. Environment and CallCreate bind their independently accepted package-wide `closed_amsterdam_refines` theorems. The generated module imports all 14 artifact modules and type-checks all 16 adequate theorem identities. The 18 precompile wrappers intentionally carry no theorem identity and remain unadmitted.
- All 612 enabled table-byte routes are operationally admitted, including the 76 Environment routes and 28 CallCreate routes through their package-wide theorems. All 412 bad-instruction routing records remain admitted as exact Stage A dispatch facts, not opcode-body refinements.
- The 18 precompile addresses and Lean identity files are pinned, globally path-unique, and explicitly unadmitted. Their production wrapper/body refinement remains open.
- The 114-file closure is accepted only for Stage A routing/dependency facts. It is a candidate, not yet an admitted transitive closure for executable frame settlement, concrete adapters, or runtime behavior.
