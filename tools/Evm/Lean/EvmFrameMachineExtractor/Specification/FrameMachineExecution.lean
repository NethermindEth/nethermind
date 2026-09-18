-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import EvmFrameMachineExtractor.Specification.FrameMachineState

/-!
Independent, fuel-bounded reference composition for the EVM frame machine.
Opcode and precompile bodies remain callbacks. Settlement path selection and
operation order are derived here from production frame discriminants; only the
leaf operations named by `SettlementPrimitives` remain refinement dependencies.
-/

namespace Eip803x.Evm.FrameMachineExecution

open FrameMachineState

def routeAt (routes : List OpcodeRoute) (table : DispatchTable)
    (byte : Byte) : Option OpcodeRoute :=
  routes.find? fun route => route.dispatchTable = table && route.byte = byte.val

def precompileAt (routes : List PrecompileRoute) (address : Nat) : Option PrecompileRoute :=
  routes.find? fun route => route.address = address

def prepareMachine (semantics : Semantics) (machine : Machine) : Machine :=
  match machine.current.phase with
  | .fresh => { machine with current := semantics.enterFreshFrame machine.current }
  | .continuation => semantics.enterContinuation machine
  | .running => machine

def withGas (frame : Frame) (gas : GasState) : Frame :=
  { frame with gas := { frame.gas with gas } }

def clearFrameRefund (frame : Frame) : Frame :=
  { frame with
    gas := { frame.gas with gas := { frame.gas.gas with refundCounter := 0 } }
    refund := 0 }

def resumeParent (machine : Machine) (parents : List SuspendedFrame)
    (parent : Frame) : Machine :=
  { machine with current := { parent with phase := .continuation }, parents }

def creditFailedCreation (operations : SettlementPrimitives)
    (baseline : ChildSettlementBaseline) (parent : Frame) : Frame :=
  if baseline.executionType.isCreate && baseline.isCreateStateGasCharged then
    operations.creditStateGasRefund parent operations.stateGasCreateCost
  else if baseline.newAccountCharged then
    operations.creditStateGasRefund parent operations.stateGasNewAccountCost
  else
    parent

def restoreFailedWorld (operations : SettlementPrimitives) (machine : Machine)
    (baseline : ChildSettlementBaseline) (parent : Frame) : Frame :=
  operations.restoreRipemdTouch machine.shouldRestoreRipemdTouch
    (operations.restoreSnapshot baseline.snapshot parent)

def recordControl (machine : Machine) (event : String) : Machine :=
  { machine with controlTrace := machine.controlTrace ++ [event] }

def precompileSucceeded : Option Bool -> Bool
  | some false => false
  | _ => true

def finishRegularReturn (machine : Machine) (result : FrameResult)
    (child : Frame) : Machine :=
  { machine with
    previousCallResult := PreviousCallResult.mk none
      (some (precompileSucceeded result.precompileSuccess))
    previousCallOutputDestination := child.outputDestination
    previousCallOutputLength := min result.output.length child.outputLength
    returnDataBuffer := result.output }

def finishCreateReturn (machine : Machine) (result : FrameResult) : Machine :=
  { machine with
    previousCallResult := PreviousCallResult.mk result.createdAddress (some true)
    previousCallOutputDestination := 0
    previousCallOutputLength := 0
    returnDataBuffer := [] }

def finishRevert (machine : Machine) (result : FrameResult) (child : Frame) : Machine :=
  { machine with
    previousCallResult := PreviousCallResult.mk none (some false)
    previousCallOutputDestination := child.outputDestination
    previousCallOutputLength := min result.output.length child.outputLength
    returnDataBuffer := result.output }

def finishException (machine : Machine) : Machine :=
  { machine with
    previousCallResult := PreviousCallResult.mk none (some false)
    previousCallOutputDestination := 0
    previousCallOutputLength := 0
    returnDataBuffer := [] }

def traceHandleException (operations : SettlementPrimitives) (kind : ExceptionKind)
    (machine : Machine) : Machine :=
  if machine.isTracingActions then
    recordControl (operations.traceActionFailure kind machine) "traceActionError"
  else
    machine

def traceHandleFailure (operations : SettlementPrimitives) (kind : ExceptionKind)
    (machine : Machine) : Machine :=
  let operation := if machine.current.dispatchTable.tracing then
      let zeroed := recordControl
        (operations.traceOperationRemainingGasZero machine) "traceOperationRemainingGasZero"
      recordControl (operations.traceOperationFailure kind zeroed) "traceOperationError"
    else
      machine
  if operation.isTracingActions then
    recordControl (operations.traceActionFailure kind operation) "traceActionError"
  else
    operation

def restoreFailureControl (operations : SettlementPrimitives) (machine : Machine) : Machine :=
  let snapshot := recordControl
    { machine with current := operations.restoreSnapshot machine.current.snapshot machine.current }
    "restoreSnapshot"
  recordControl
    { snapshot with current :=
        operations.restoreRipemdTouch machine.shouldRestoreRipemdTouch snapshot.current }
    "restoreRipemdTouch"

def settleTop (operations : SettlementPrimitives) (machine : Machine)
    (result : FrameResult) : Settlement :=
  match result.exit, result.controlRoute with
  | .success, .ordinary =>
    .complete { result with
      shouldRestoreRipemdTouch := machine.shouldRestoreRipemdTouch
      controlTrace := machine.controlTrace }
  | .revert, .ordinary =>
    .complete { result with
      frame := operations.refundRevertedTopLevelStateGas result.frame
      shouldRestoreRipemdTouch := machine.shouldRestoreRipemdTouch
      controlTrace := machine.controlTrace }
  | .exception kind, .handleException =>
    let traced := traceHandleException operations kind machine
    let restoredMachine := restoreFailureControl operations traced
    .complete { result with
      frame := clearFrameRefund restoredMachine.current
      shouldRestoreRipemdTouch := restoredMachine.shouldRestoreRipemdTouch
      controlTrace := restoredMachine.controlTrace }
  | .exception kind, .handleFailure =>
    let restoredMachine := restoreFailureControl operations machine
    let traced := traceHandleFailure operations kind restoredMachine
    .complete { result with
      frame := clearFrameRefund traced.current
      shouldRestoreRipemdTouch := traced.shouldRestoreRipemdTouch
      controlTrace := traced.controlTrace }
  | _, _ => .invalidControl machine

def settleRegularSuccess (operations : SettlementPrimitives) (machine : Machine)
    (parents : List SuspendedFrame) (suspended : SuspendedFrame)
    (result : FrameResult) : Settlement :=
  let reconciled := operations.incorporateAdvancedRefund suspended.parent result.frame
  let parentWithRefund := withGas reconciled.1
    (operations.refundChildGas suspended.gasEntry reconciled.2.gas)
  let resumed := resumeParent machine parents parentWithRefund
  let returned := finishRegularReturn resumed result reconciled.2
  let committed := operations.commitChild returned.current reconciled.2
  .resume { returned with current := operations.repayStateGasSpill committed }

def completeCreateSuccess (operations : SettlementPrimitives) (machine : Machine)
    (parents : List SuspendedFrame) (result : FrameResult)
    (parent child : Frame) : Settlement :=
  let returned := finishCreateReturn (resumeParent machine parents parent) result
  let committed := operations.commitChild returned.current child
  let reconciled := operations.incorporateAdvancedRefund committed child
  .resume { returned with current := operations.repayStateGasSpill reconciled.1 }

def settleCodeDepositFailure (operations : SettlementPrimitives) (machine : Machine)
    (parents : List SuspendedFrame) (suspended : SuspendedFrame)
    (kind : ExceptionKind) (parent child : Frame) : Settlement :=
  let burned := operations.consumeReturnedExecutionGas parent child.gas.gas.gasLeft
  let halted := operations.revertRefundToHalt burned child
  let credited := creditFailedCreation operations suspended.childBaseline halted
  let withoutAdvanced := operations.removeAdvancedRefund credited child
  let restored := restoreFailedWorld operations machine suspended.childBaseline withoutAdvanced.1
  let deleted := if suspended.childBaseline.isCreateOnPreExistingAccount then
      restored else operations.deleteCreatedAccount restored
  let resumed := resumeParent machine parents deleted
  let failed := finishException resumed
  .resume (traceHandleException operations kind failed)

def settleCreateSuccess (operations : SettlementPrimitives) (machine : Machine)
    (parents : List SuspendedFrame) (suspended : SuspendedFrame)
    (result : FrameResult) : Settlement :=
  let parentWithRefund := withGas suspended.parent
    (operations.refundChildGas suspended.gasEntry result.frame.gas)
  let plan := operations.calculateCodeDeposit result result.frame
  let charged := if plan.invalidCode then none else operations.chargeCodeDeposit plan parentWithRefund
  match charged with
  | some parent =>
    completeCreateSuccess operations machine parents result
      (operations.insertCode result parent) result.frame
  | none =>
    if plan.invalidCode || plan.failOnInsufficientGas then
      let kind := if plan.invalidCode then .invalidCode else .outOfGas
      settleCodeDepositFailure operations machine parents suspended kind parentWithRefund result.frame
    else
      completeCreateSuccess operations machine parents result parentWithRefund result.frame

def settleSuccess (operations : SettlementPrimitives) (machine : Machine)
    (parents : List SuspendedFrame) (suspended : SuspendedFrame)
    (result : FrameResult) : Settlement :=
  if suspended.childBaseline.executionType.isCreate then
    settleCreateSuccess operations machine parents suspended result
  else
    settleRegularSuccess operations machine parents suspended result

def settleRevert (operations : SettlementPrimitives) (machine : Machine)
    (parents : List SuspendedFrame) (suspended : SuspendedFrame)
    (result : FrameResult) : Settlement :=
  let returnedGas := operations.returnChildExecutionGas suspended.parent result.frame
  let withoutAdvanced := operations.removeAdvancedRefund returnedGas result.frame
  let restoredGas := operations.restoreChildStateGas withoutAdvanced.1 withoutAdvanced.2
  let parent := creditFailedCreation operations suspended.childBaseline restoredGas
  let restored := restoreFailedWorld operations machine suspended.childBaseline parent
  .resume (finishRevert (resumeParent machine parents restored) result result.frame)

def finishExceptionSettlement (operations : SettlementPrimitives) (machine : Machine)
    (parents : List SuspendedFrame) (suspended : SuspendedFrame)
    (result : FrameResult) : Settlement :=
  let cleared := finishException machine
  let restoredParent := { suspended.parent with world := cleared.current.world }
  let withoutAdvanced := operations.removeAdvancedRefund restoredParent result.frame
  let restoredGas := operations.restoreChildStateGasOnHalt withoutAdvanced.1 withoutAdvanced.2
  let parent := creditFailedCreation operations suspended.childBaseline restoredGas
  .resume (resumeParent cleared parents parent)

def settleHandleException (operations : SettlementPrimitives) (machine : Machine)
    (parents : List SuspendedFrame) (suspended : SuspendedFrame)
    (kind : ExceptionKind) (result : FrameResult) : Settlement :=
  let traced := traceHandleException operations kind machine
  let restored := restoreFailureControl operations traced
  finishExceptionSettlement operations restored parents suspended result

def settleHandleFailure (operations : SettlementPrimitives) (machine : Machine)
    (parents : List SuspendedFrame) (suspended : SuspendedFrame)
    (kind : ExceptionKind) (result : FrameResult) : Settlement :=
  let restored := restoreFailureControl operations machine
  let traced := traceHandleFailure operations kind restored
  finishExceptionSettlement operations traced parents suspended result

def settleChild (semantics : Semantics) (machine : Machine)
    (result : FrameResult) : Settlement :=
  match machine.parents with
  | [] => settleTop semantics.settlement machine result
  | suspended :: parents =>
    match result.exit, result.controlRoute with
    | .success, .ordinary => settleSuccess semantics.settlement machine parents suspended result
    | .revert, .ordinary => settleRevert semantics.settlement machine parents suspended result
    | .revert, .nestedPrecompileSoftFailure =>
      let cleared := recordControl machine "clearExecutionGas"
      let classified := recordControl cleared "classifyShouldRevert"
      settleRevert semantics.settlement classified parents suspended
        { result with frame := semantics.settlement.clearExecutionGas result.frame }
    | .exception kind, .handleException =>
      settleHandleException semantics.settlement machine parents suspended kind result
    | .exception kind, .handleFailure =>
      settleHandleFailure semantics.settlement machine parents suspended kind result
    | _, _ => .invalidControl machine

def stepFrame (semantics : Semantics) (routes : List OpcodeRoute)
    (precompiles : List PrecompileRoute) (machine : Machine) : MachineStep :=
  let entered := prepareMachine semantics machine
  match entered.current.kind with
  | .precompile address =>
    match precompileAt precompiles address with
    | none => semantics.missingPrecompile address entered
    | some route =>
      if semantics.precompileEnabled route then semantics.runPrecompile route entered
      else semantics.missingPrecompile address entered
  | .bytecode =>
    match entered.current.code[entered.current.pc]? with
    | none => semantics.stopAtEnd entered
    | some byte =>
      match routeAt routes entered.current.dispatchTable byte with
      | none => semantics.badInstruction entered byte
      | some route =>
        match route.kind with
        | .enabled => semantics.runOpcode route entered
        | .disabled | .badInstruction => semantics.badInstruction entered byte

def drive (semantics : Semantics) (routes : List OpcodeRoute)
    (precompiles : List PrecompileRoute) : Nat -> Machine -> RunResult
  | 0, machine => .incomplete .fuelExhausted machine
  | fuel + 1, machine =>
    let stepped := stepFrame semantics routes precompiles machine
    match stepped.result with
    | .continue frame =>
      if stepped.controlRoute = .ordinary ||
          stepped.controlRoute = .directPrecompileSoftFailure then
        drive semantics routes precompiles fuel { stepped.machine with current := frame }
      else
        .incomplete .invalidControlRoute stepped.machine
    | .suspend parent child =>
      if stepped.controlRoute = .ordinary then
        drive semantics routes precompiles fuel
          { stepped.machine with current := child, parents := parent :: stepped.machine.parents }
      else
        .incomplete .invalidControlRoute stepped.machine
    | .halt result =>
      if stepped.controlRoute = result.controlRoute then
        match settleChild semantics stepped.machine result with
        | .complete result => .completed result
        | .resume resumed => drive semantics routes precompiles fuel resumed
        | .invalidControl invalid => .incomplete .invalidControlRoute invalid
      else
        .incomplete .invalidControlRoute stepped.machine

def executeAmsterdam (semantics : Semantics) (routes : List OpcodeRoute)
    (precompiles : List PrecompileRoute) (fuel : Nat) (initial : Machine) : RunResult :=
  drive semantics routes precompiles fuel initial

end Eip803x.Evm.FrameMachineExecution
