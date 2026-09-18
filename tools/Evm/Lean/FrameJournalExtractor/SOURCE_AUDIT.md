# Nested-frame journal source audit

## Production control graph

The standard-mainnet child-frame graph is rooted in
`VirtualMachine<TGasPolicy>.ExecuteTransaction<TTracingInst>` at
`src/Nethermind/Nethermind.Evm/VirtualMachine.cs:172`.

- A suspended opcode result reaches `PrepareNextCallFrame` (`VirtualMachine.cs:252,805`), which pushes the current frame before selecting `StateToExecute`.
- A normal nested return pops the child at `VirtualMachine.cs:283`; success calls `VmState.CommitToParent` before child disposal (`VirtualMachine.cs:322`). `CommitToParent` clears `_canRestore` (`VmState.cs:321`), so disposal retains access, log, and destroy changes.
- Explicit REVERT calls `HandleRevert` (`VirtualMachine.cs:348,585`). It restores `previousState.Snapshot`, replays a latched RIPEMD touch, and leaving the `using` scope then disposes the uncommitted child, causing `StackAccessTracker.Restore` through `VmState.Dispose` (`VmState.cs:203`).
- Returned exceptional results reach `HandleException` (`VirtualMachine.cs:259,835`); thrown `EvmException`/`OverflowException` results reach `HandleFailure` (`VirtualMachine.cs:366,627`). Both restore the current world snapshot, replay a latched RIPEMD touch, then `PopAndRestoreParentState` disposes the uncommitted child (`VirtualMachine.cs:707`).

CALL child construction is rooted at
`EvmInstructions.CreateFullCallFrame` (`Instructions/EvmInstructions.Call.cs:295`).
It takes the world snapshot before the value-transfer debit and passes that
snapshot plus the copied parent access tracker to `VmState.RentFrame`.
Depth/balance failure occurs before this helper and therefore rents no child;
account-access warming and other parent-owned admission effects that occurred
earlier remain in the parent baseline.

CREATE child construction is rooted at
`EvmInstructions.InstructionCreate` (`Instructions/EvmInstructions.Create.cs`).
Depth, memory readability, balance, and nonce failure return before the parent
nonce mutation. Once admitted, the parent nonce is incremented before the world
snapshot (`Create.cs:186-189`). `VmState.Initialize` then marks the destination
in `CreateList` before taking the child access-tracker snapshot
(`VmState.cs:151-155`). `CreateList` is transaction-wide rather than journaled,
so it survives a child rollback. Collision, gas, and code-deposit settlement are
not modeled here; a no-child event is a stutter relative to the parent state at
the package boundary, not a claim that earlier opcode pricing or warming did
nothing.

The abstract CREATE entry writes `createdThisTx` before taking its combined
checkpoint. This commutes the concrete world-snapshot/`WasCreated` order only
because `FrameSnapshot` excludes `createdThisTx` and persistent originals. The
source-order admission remains concrete and the normalization is explicit.

`WorldState.TakeSnapshot`/`Restore` cover persistent storage, transient storage,
and accounts (`Nethermind.State/WorldState.cs:397-416`).
`StackAccessTracker.TakeSnapshot`/`Restore` cover warm accounts, warm cells,
destroy entries, and ordered logs (`StackAccessTracker.cs:60-78`); warm sets are
restored only in the admitted `isTracingAccess=false` mode.

LOG effects enter through `VirtualMachine.AddLog` (`VirtualMachine.cs:1440`).
SELFDESTRUCT appends to the destroy set and performs its immediate account/log
effects in `InstructionSelfDestruct`
(`Instructions/EvmInstructions.ControlFlow.cs:240-325`). Those effects are
ordinary child journal mutations and therefore follow the same success/rollback
rule. Final account reaping remains outside this package.

`VirtualMachine.RunPrecompile<Eip158>` sets `_shouldRestoreRipemdTouch` after an
existing, zero-value RIPEMD-160 invocation has touched an EIP-161-dead account.
The flag is reset once at transaction execution entry, not at child entry.
`HandleRevert`, `HandleFailure`, and `HandleException` call
`RestoreRipemdTouch` immediately after `WorldState.Restore`; the helper checks
that address 3 still exists and performs a zero-balance update. `Account.IsEmpty`
ignores the storage root, so a storage-only account is also retouched. The Lean
machine therefore carries the latch outside frame snapshots and models rollback
as restore followed by a read and same-account update when the restored account
is empty. `WorldState.AccountExists`/`AddToBalance`, `StateProvider.IsDeadAccount`,
`SetNewBalance`, `PushTouch`, and its consecutive-touch deduplication are all
source-admitted. The Lean relation is nevertheless extensional: the production
`Touch` change kind is represented by a same-value semantic update, so exact raw
change-kind identity and raw entry counts are not claimed.

The standard-mainnet root is explicit rather than inferred: the pinned
`src/Nethermind/Directory.Build.targets` selects `.std.cs` when `EnableZkEvm` is
not true; `SpecFlags.std.cs` reads the release spec; Amsterdam inherits BPO2 and
enables EIP-7928/8037/8038; `MainnetSpecProvider` selects Amsterdam at its pinned
timestamp; `BlockProcessingModule.Load` registers `IWorldState` as concrete
`WorldState`; and `TransactionProcessorBase.ExecuteEvmTransaction` constructs
`StackAccessTracker` from `tracer.IsTracingAccess`. The theorem is conditional
on the admitted false normal-access branch and does not cover the separate BAL
processor pool.

`VirtualMachine.Dispatch.PrepareOpcodes` resolves an `ExecutionHandlers` table
from that same active spec. Its `RunPrecompile` function pointer selects
`RunPrecompileCore<OnFlag>` exactly when standard `SpecFlags.Eip158(spec)` reads
`ClearEmptyAccountWhenTouched`; the core then invokes the generic
`RunPrecompile<Eip158>` body containing the historical RIPEMD predicate.

## Minimal package boundary

The new package composes the theorem-free generated `WorldJournalExtractor`
transition rather than duplicating account/storage/access/log journals. Its
frame alphabet has nine operations: apply one non-snapshot world operation;
enter CALL; enter CREATE; no-child CALL failure; no-child CREATE failure; and
set the RIPEMD touch latch; then success, REVERT, or exceptional child exit.
Frame operations own the snapshot
stack, so attempts to smuggle `takeSnapshot` or `restoreSnapshot` through a
world-body operation fail closed.

The generated transition and independent handwritten specification share only
the neutral types in `Specification/FrameJournalState.lean`. The generated file
imports the accepted theorem-free WorldJournal kernel and never imports either
handwritten transition. The refinement layer alone imports both and the pinned
dependency proofs.

## Dependency boundary

Production-refinement theorem identities are pinned for WorldJournal,
PersistentStorage, TransientStorage, and LOG. CALL/CREATE and SELFDESTRUCT are
explicitly labeled accepted handwritten references, not production refinement
theorems. Their facts remain adapter premises until their production extractors
carry adequate operational theorems. This package proves frame/journal
composition once leaf effects have been mapped to admitted world operations; it
does not silently promote either handwritten leaf into extracted C# semantics.
The exact WorldJournal `transition_refines` signature is admitted because the
composition proof invokes it directly; `finite_trace_refines` remains an
explicit dependency check. The generated WorldJournal kernel imported by the
generated frame kernel is hashed directly and its bytes must match the Lean
artifact digest in the independently pinned upstream manifest.

No production defect was found during this audit.
