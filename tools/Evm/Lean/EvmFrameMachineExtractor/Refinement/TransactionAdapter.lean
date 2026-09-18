-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.TransactionReference
import EvmFrameMachineExtractor.Specification.FrameMachineExecution

/-!
Proof-carrying projection from a completed frame result toward
`TransactionReference.Oracle.executeEvmCall`. This module deliberately exposes
no installable oracle: admission requires a source-extracted initial machine,
an actually completed fuel-bounded run, valid control routing, and agreement
between production `VmState.Refund` and the opcode gas refund counter.

The completed projection retains the transaction-wide RIPEMD dirty-touch latch.
`TransactionReference.State` has no corresponding field, so the latch is
consumed by the explicit post-rollback world model below rather than discarded.
-/

namespace Eip803x.Evm.FrameMachineTransactionAdapter

open FrameMachineState

structure TracerCapabilities where
  isTracingInstructions : Bool
  isCancelable : Bool
  isTracingActions : Bool
  deriving Repr

def dispatchTableOfCapabilities (capabilities : TracerCapabilities) : DispatchTable :=
  match capabilities.isTracingInstructions, capabilities.isCancelable with
  | false, false => .noTrace
  | false, true => .noTraceCancelable
  | true, false => .traced
  | true, true => .tracedCancelable

abbrev ProductionInputExtractor :=
  Eip803x.TransactionReference.Input -> Eip803x.TransactionReference.State ->
    TracerCapabilities -> Option Machine

def admittedEntryKind : Eip803x.TransactionReference.EntryKind -> Prop
  | .messageCall | .contractCreation => True
  | .simpleTransfer | .systemCall => False

def executionTypeOfEntry : Eip803x.TransactionReference.EntryKind -> Option ExecutionType
  | .messageCall => some .transaction
  | .contractCreation => some .create
  | .simpleTransfer | .systemCall => none

def snapshotProjectsToFrame
    (snapshot : Option Eip803x.TransactionReference.ExecutionSnapshot)
    (frameSnapshot : WorldToken) : Prop :=
  match snapshot with
  | none => False
  | some value =>
    frameSnapshot.durable = value.durableWorld ∧
    frameSnapshot.reversible = value.reversibleWorld ∧
    frameSnapshot.logs = value.logs ∧
    frameSnapshot.destroyList = value.destroyList

def inputStateProjectsToInitial (input : Eip803x.TransactionReference.Input)
    (state : Eip803x.TransactionReference.State) (capabilities : TracerCapabilities)
    (initial : Machine) : Prop :=
  executionTypeOfEntry input.entryKind = some initial.current.executionType ∧
  initial.current.phase = .fresh ∧
  initial.current.dispatchTable = dispatchTableOfCapabilities capabilities ∧
  initial.current.isTopLevel = true ∧
  initial.current.callDepth = 0 ∧
  initial.current.isStatic = false ∧
  initial.current.pc = 0 ∧
  initial.current.opcodeCount = 0 ∧
  initial.current.gas.gas.gasLeft = state.gas.gasLeft ∧
  initial.current.gas.gas.stateReservoir = state.gas.stateGasReservoir ∧
  initial.current.gas.gas.stateFromGasLeft = state.stateGasFromGasLeft ∧
  initial.current.gas.gas.stateUsed = state.gas.evmStateGasUsed ∧
  initial.current.gas.gas.refundCounter = 0 ∧
  initial.current.gas.stateGasBaseline = state.gas.stateGasReservoir ∧
  initial.current.gas.stateUsedBaseline = state.gas.evmStateGasUsed ∧
  initial.current.gas.refundCounterBaseline = 0 ∧
  initial.current.refund = 0 ∧
  RefundCountersAgree initial.current ∧
  state.gas.refundCounter = 0 ∧
  initial.current.initialStateGasUsed = state.gas.evmStateGasUsed ∧
  initial.current.stateGasRefundAdvanced = 0 ∧
  initial.current.isCreateOnPreExistingAccount = false ∧
  initial.current.isCreateStateGasCharged = false ∧
  initial.current.newAccountCharged = false ∧
  initial.current.cancellationRequested = false ∧
  initial.current.world.durable = state.durableWorld ∧
  initial.current.world.reversible = state.reversibleWorld ∧
  initial.current.world.logs = state.logs ∧
  initial.current.world.destroyList = state.destroyList ∧
  snapshotProjectsToFrame state.topLevelSnapshot initial.current.snapshot ∧
  state.path = some .evm ∧
  initial.parents = [] ∧
  initial.previousCallResult.createdAddress = none ∧
  initial.previousCallResult.success = none ∧
  initial.previousCallOutputDestination = 0 ∧
  initial.previousCallOutputLength = 0 ∧
  initial.returnDataBuffer = [] ∧
  initial.shouldRestoreRipemdTouch = false ∧
  initial.isTracingActions = capabilities.isTracingActions ∧
  initial.transactionTrace = [] ∧
  initial.controlTrace = []

def ProductionInputProjectionRefines (extracted : ProductionInputExtractor) : Prop :=
  (∀ input state capabilities, admittedEntryKind input.entryKind ->
    ∃ initial, extracted input state capabilities = some initial) ∧
  (∀ input state capabilities initial,
    extracted input state capabilities = some initial ->
    inputStateProjectsToInitial input state capabilities initial)

def FuelAdequate (semantics : Semantics) (opcodeRoutes : List OpcodeRoute)
    (precompileRoutes : List PrecompileRoute) (fuel : Nat) (initial : Machine) : Prop :=
  ∃ result, FrameMachineExecution.executeAmsterdam semantics opcodeRoutes
    precompileRoutes fuel initial = .completed result

structure AdmittedCompletedExecution (extractedInput : ProductionInputExtractor)
    (semantics : Semantics) (opcodeRoutes : List OpcodeRoute)
    (precompileRoutes : List PrecompileRoute)
    (input : Eip803x.TransactionReference.Input)
    (state : Eip803x.TransactionReference.State)
    (capabilities : TracerCapabilities) where
  entryAdmitted : admittedEntryKind input.entryKind
  inputProjectionRefines : ProductionInputProjectionRefines extractedInput
  initial : Machine
  extractedInitial : extractedInput input state capabilities = some initial
  fuel : Nat
  result : FrameResult
  completion : FrameMachineExecution.executeAmsterdam semantics opcodeRoutes
    precompileRoutes fuel initial = .completed result
  controlValid : FrameResultControlValid result
  refundValid : CompletedRefundValid result

def exceptionDirective : ExceptionKind -> Eip803x.TransactionReference.ExecutionDirective
  | .outOfGas => .exception .outOfGas
  | .invalidCode => .exception .invalidCode
  | .collision => .exception .collision
  | .stackUnderflow => .exception .stackUnderflow
  | .stackOverflow => .exception .stackOverflow
  | .invalidJump => .exception .invalidJump
  | .staticViolation => .exception .staticViolation
  | .precompileFailure | .other => .unmodeled

def directiveOfCompleted (result : FrameResult) :
    Eip803x.TransactionReference.ExecutionDirective :=
  match result.exit with
  | .success => .success
  | .revert => .revert
  | .exception kind => exceptionDirective kind

def projectCompletedState (state : Eip803x.TransactionReference.State)
    (result : FrameResult) : Eip803x.TransactionReference.State :=
  let gas := result.frame.gas.gas
  { state with
    reversibleWorld := result.frame.world.reversible
    gas := { state.gas with
      gasLeft := gas.gasLeft
      stateGasReservoir := gas.stateReservoir
      refundCounter := result.frame.refund.toNat
      evmStateGasUsed := gas.stateUsed }
    stateGasFromGasLeft := gas.stateFromGasLeft
    logs := result.frame.world.logs
    destroyList := result.frame.world.destroyList }

structure CompletedProjection where
  execution : Eip803x.TransactionReference.ExecutionResult
  shouldRestoreRipemdTouch : Bool
  controlTrace : List String

def completedProjection (state : Eip803x.TransactionReference.State)
    (result : FrameResult) : CompletedProjection :=
  { execution :=
      { state := projectCompletedState state result
        outcome := directiveOfCompleted result }
    shouldRestoreRipemdTouch := result.shouldRestoreRipemdTouch
    controlTrace := result.controlTrace }

def projectAdmitted {extractedInput : ProductionInputExtractor}
    {semantics : Semantics} {opcodeRoutes : List OpcodeRoute}
    {precompileRoutes : List PrecompileRoute}
    {input : Eip803x.TransactionReference.Input}
    {state : Eip803x.TransactionReference.State}
    {capabilities : TracerCapabilities}
    (execution : AdmittedCompletedExecution extractedInput semantics opcodeRoutes
      precompileRoutes input state capabilities) : CompletedProjection :=
  completedProjection state execution.result

structure RipemdTouchState where
  ripemdAccountExists : Bool
  ripemdTouched : Bool
  deriving Repr

def restoreRipemdTouch (shouldRestore : Bool) (state : RipemdTouchState) : RipemdTouchState :=
  if shouldRestore && state.ripemdAccountExists then
    { state with ripemdTouched := true }
  else
    state

structure PostRollbackProjection where
  transactionState : Eip803x.TransactionReference.State
  ripemdTouch : RipemdTouchState
  controlTrace : List String
  deriving Repr

def consumeAfterOuterRollback (projection : CompletedProjection)
    (snapshot : Eip803x.TransactionReference.ExecutionSnapshot)
    (ripemdTouch : RipemdTouchState)
    (_ : Eip803x.TransactionReference.executionNeedsRollback
      projection.execution.outcome = true) : PostRollbackProjection :=
  let rolledBack := Eip803x.TransactionReference.restoreExecutionSnapshot
    snapshot projection.execution.state
  { transactionState := rolledBack
    ripemdTouch := restoreRipemdTouch projection.shouldRestoreRipemdTouch ripemdTouch
    controlTrace := projection.controlTrace ++ ["outerSnapshotRestore", "restoreRipemdTouch"] }

def ExtractedCompletedProjectionRefines
    (extracted : Eip803x.TransactionReference.State -> FrameResult -> CompletedProjection) : Prop :=
  ∀ state result, extracted state result = completedProjection state result

def AdmittedExecutionFuelAdequate {extractedInput : ProductionInputExtractor}
    {semantics : Semantics} {opcodeRoutes : List OpcodeRoute}
    {precompileRoutes : List PrecompileRoute}
    {input : Eip803x.TransactionReference.Input}
    {state : Eip803x.TransactionReference.State}
    {capabilities : TracerCapabilities}
    (execution : AdmittedCompletedExecution extractedInput semantics opcodeRoutes
      precompileRoutes input state capabilities) : Prop :=
  FuelAdequate semantics opcodeRoutes precompileRoutes execution.fuel execution.initial

def AdapterPreservesLifecycleControl {extractedInput : ProductionInputExtractor}
    {semantics : Semantics} {opcodeRoutes : List OpcodeRoute}
    {precompileRoutes : List PrecompileRoute}
    {input : Eip803x.TransactionReference.Input}
    {state : Eip803x.TransactionReference.State}
    {capabilities : TracerCapabilities}
    (execution : AdmittedCompletedExecution extractedInput semantics opcodeRoutes
      precompileRoutes input state capabilities) : Prop :=
  Eip803x.TransactionReference.preservesLifecycleControl state
    (projectAdmitted execution).execution.state = true

def AdapterAgreesWithRequestedDirective {extractedInput : ProductionInputExtractor}
    {semantics : Semantics} {opcodeRoutes : List OpcodeRoute}
    {precompileRoutes : List PrecompileRoute}
    {input : Eip803x.TransactionReference.Input}
    {state : Eip803x.TransactionReference.State}
    {capabilities : TracerCapabilities}
    (execution : AdmittedCompletedExecution extractedInput semantics opcodeRoutes
      precompileRoutes input state capabilities) : Prop :=
  directiveOfCompleted execution.result = input.execution

def AdapterDirectiveAdmissible {extractedInput : ProductionInputExtractor}
    {semantics : Semantics} {opcodeRoutes : List OpcodeRoute}
    {precompileRoutes : List PrecompileRoute}
    {input : Eip803x.TransactionReference.Input}
    {state : Eip803x.TransactionReference.State}
    {capabilities : TracerCapabilities}
    (execution : AdmittedCompletedExecution extractedInput semantics opcodeRoutes
      precompileRoutes input state capabilities) : Prop :=
  Eip803x.TransactionReference.evmExecutionDirectiveAdmissible input
    (directiveOfCompleted execution.result) = true

end Eip803x.Evm.FrameMachineTransactionAdapter
