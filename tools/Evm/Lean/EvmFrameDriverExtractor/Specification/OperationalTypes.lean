-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import EvmFrameMachineExtractor.Specification.FrameMachineState

/-!
The Stage F carrier for the source-derived frame driver.

This module is deliberately independent of both the generated kernel and its
reference.  The carrier is typed at the same granularity as the production
loop: a prepared frame, one dispatch invocation, one child/top-level
settlement, and one exceptional cleanup.  The admitted source control plan is
lowered to typed `OperationalControlInstruction` values and interpreted by the
generated driver before any of those adapters run.  An adapter returns `Option`
at every production boundary.  `none` is an unresolved adapter and is converted to an
explicit incomplete result by the generated driver; it is never silently
treated as a successful EVM transition.

The `Semantics` member is the already accepted frame-machine settlement
carrier.  Its `SettlementPrimitives` fields are intentionally retained rather
than collapsed into a postcondition: state-gas reservoir/spill/used values,
refund counters, world snapshots, logs, access/destroy sets, RIPEMD restore,
return-data copying, and child disposal are all part of the operational
adapter surface.
-/

namespace Eip803x.Evm.FrameDriver.Operational

open Eip803x.Evm.FrameMachineState

inductive CancellationPoint where
  | beforeFirstOpcode
  | afterCompleteBatch (completedOpcodeCount successorPc : Nat)
  deriving DecidableEq, Repr

inductive ControlPlanStage where
  | prepare
  | dispatch
  | classify
  | settle
  | cleanup
  deriving DecidableEq, Repr

/- These finite tags are the typed lowering of source branch predicates,
   effects, and settlement labels.  They are interpreted against a runtime
   observation; a branch label or topology digest alone cannot select a
   driver transition. -/
inductive OperationalControlPredicate where
  | freshFrame
  | continuationFrame
  | bytecodeFrame
  | fullPrecompileFrame
  | bytecodeContinue
  | bytecodeSuspend
  | nestedRegularSuccess
  | nestedCreateSuccess
  | createDepositInvalidCode
  | createDepositOutOfGas
  | nestedRevert
  | nestedException
  | resumeParent
  | topLevelSuccess
  | topLevelRevert
  | topLevelException
  | precompileOutOfGasNested
  | precompileOutOfGasTop
  | precompileReturnedFailure
  | precompileManagedException
  | cancelled
  | escaped
  | invalidControl
  | completed
  deriving DecidableEq, Repr

inductive OperationalControlEffect where
  | clearReturnData
  | prepareFresh
  | retainReturnData
  | prepareContinuation
  | runBytecode
  | runDispatchLoop
  | runFullPrecompile
  | executePrecompile
  | retainCurrentFrame
  | prepareChildFrame
  | retainParent
  | popParent
  | mergeChild
  | repayStateGasSpill
  | prepareCreateData
  | handleCreate
  | restoreWorld
  | creditParent
  | burnDepositGas
  | restoreSnapshot
  | restoreChildGas
  | handleRevert
  | restoreFailureControl
  | resumeParent
  | prepareTopLevelSubstate
  | refundRevertedStateGas
  | handleExceptionOrFailure
  | failureSettlement
  | dispose
  | failClosed
  | disposeActiveFrames
  deriving DecidableEq, Repr

inductive OperationalControlSettlement where
  | preparation
  | invocation
  | continued
  | suspended
  | childSuccess
  | childCreateSuccess
  | childCreateInvalidCode
  | childCreateOutOfGas
  | childRevert
  | childException
  | topLevelSuccess
  | topLevelRevert
  | topLevelException
  | fullPrecompileOutOfGasNested
  | fullPrecompileOutOfGasTop
  | fullPrecompileReturnedFailure
  | fullPrecompileManagedException
  | cancelled
  | escaped
  | invalidControl
  | completed
  deriving DecidableEq, Repr

inductive OperationalControlAction where
  | prepareFresh
  | prepareContinuation
  | dispatchBytecode
  | dispatchFullPrecompile
  | precompileFailure
  | classifyContinue
  | classifySuspend
  | classifyHalt
  | settleNestedRegularSuccess
  | settleNestedCreateSuccess
  | settleNestedCreateInvalidCode
  | settleNestedCreateOutOfGas
  | settleNestedRevert
  | settleNestedException
  | settleResume
  | settleTopLevelSuccess
  | settleTopLevelRevert
  | settleTopLevelException
  | cleanupCancelled
  | cleanupEscaped
  | cleanupInvalidControl
  | cleanupCompleted
  deriving DecidableEq, Repr

structure OperationalControlInstruction where
  stage : ControlPlanStage
  predicate : OperationalControlPredicate
  effects : List OperationalControlEffect
  settlement : OperationalControlSettlement
  /- The action is lowered from the source-bound instruction itself.  It is
     data in the executable control AST, not reconstructed by a generated
     profile-name switch. -/
  action : OperationalControlAction
  branch : String
  member : String
  nodeId : String
  kind : String
  sourceArm : String
  bindingArm : String
  sourceSha256 : String
  deriving DecidableEq, Repr

structure OperationalControlPlanStep where
  stage : ControlPlanStage
  member : String
  nodeId : String
  kind : String
  sourceArm : String
  sourceSha256 : String
  actions : List OperationalControlAction
  deriving DecidableEq, Repr

structure OperationalControlNode where
  member : String
  id : String
  kind : String
  parentId : String
  arm : String
  condition : String
  sha256 : String
  operations : List String
  deriving DecidableEq, Repr

structure OperationalBranchBinding where
  branch : String
  stages : List ControlPlanStage
  member : String
  nodeId : String
  kind : String
  sourceArm : String
  sourceSha256 : String
  bindingArm : String
  predicate : OperationalControlPredicate
  effects : List OperationalControlEffect
  settlement : OperationalControlSettlement
  actions : List OperationalControlAction
  deriving DecidableEq, Repr

structure OpcodeBatch where
  startPc : Nat
  startOpcodeCount : Nat
  completedOpcodeCount : Nat
  successorPc : Nat
  terminal : Bool
  deriving DecidableEq, Repr

inductive BytecodeRunOutcome where
  | returned (step : MachineStep)
  | thrownEvm (machine : Machine) (kind : ExceptionKind)
  | thrownOverflow (machine : Machine)
  | escaped (machine : Machine) (reason : String)
  | cancelled (machine : Machine) (point : CancellationPoint) (reason : String)
  deriving Repr

structure BytecodeExecution where
  dispatchTable : DispatchTable
  routes : List OpcodeRoute
  /- Adapter-supplied evidence for each reported completed opcode.  The driver
  checks cardinality and byte lookup for these witnesses;
     those checks do not prove the C# handler's exact execution order or its
     per-op successor/control transitions. -/
  routePcs : List Nat
  batches : List OpcodeBatch
  cancellationPolls : List CancellationPoint
  outcome : BytecodeRunOutcome
  deriving Repr

inductive PrecompileRunOutcome where
  | returned (step : MachineStep)
  | outOfGas (machine : Machine)
  | returnedFailure (machine : Machine) (reason : Option String)
  | managedException (machine : Machine) (reason : Option String)
  | escaped (machine : Machine) (reason : String)
  deriving Repr

structure PrecompileExecution where
  route : Option PrecompileRoute
  outcome : PrecompileRunOutcome
  deriving Repr

/- The C# `TransactionSubstate` is an output value, not a route tag.  Keep all
   externally visible fields in the operational carrier so a top-level adapter
   cannot discharge the boundary by returning only a `FrameResult`. -/
structure TransactionSubstate where
  status : FrameExit
  error : Option String
  substateError : Option String
  exceptionType : Option ExceptionKind
  output : List Byte
  shouldRevert : Bool
  refund : Int
  logs : List Nat
  destroyList : List Nat
  shouldRestoreRipemdTouch : Bool
  deriving Repr

inductive CreateDepositOutcome where
  | deposited
  | invalidCode
  | outOfGas
  deriving DecidableEq, Repr

/- The deposit leaf reports the concrete CREATE branch before handing the
   resulting parent/complete transition to the common settlement carrier. -/
structure CreateDepositExecution where
  outcome : CreateDepositOutcome
  settlement : Settlement
  deriving Repr

inductive OperationalControlObservation where
  | machine (machine : Machine)
  | machineStep (step : MachineStep)
  | frameResult (machine : Machine) (result : FrameResult)
  | createDeposit (machine : Machine) (execution : CreateDepositExecution)
  | precompile (machine : Machine) (outcome : PrecompileRunOutcome)
  | resume (machine : Machine)
  | cancelled (machine : Machine)
  | escaped (machine : Machine)
  | invalidControl (machine : Machine)
  deriving Repr

/- The production CREATE deposit branch resumes the captured parent frame.  A
   successful deposit reports a successful child result; invalid code and a
   fatal deposit gas failure report the failed child result.  Any other
   settlement shape is rejected instead of allowing the outcome tag to become
   inert metadata. -/
def createDepositOutcomeValid (execution : CreateDepositExecution) : Prop :=
  match execution.outcome, execution.settlement with
  | .deposited, .resume resumed => resumed.previousCallResult.success = some true
  | .invalidCode, .resume resumed | .outOfGas, .resume resumed =>
      resumed.previousCallResult.success = some false
  | _, _ => False

inductive Invocation where
  | bytecode (execution : BytecodeExecution)
  | fullPrecompile (execution : PrecompileExecution)
  deriving Repr

inductive DriverStep where
  | continue (machine : Machine)
  | suspend (machine : Machine) (parent : SuspendedFrame) (child : Frame)
  | halt (machine : Machine) (result : FrameResult)
  | topLevelHalt (machine : Machine) (result : FrameResult) (substate : TransactionSubstate)
  | cancelled (machine : Machine) (reason : String)
  | escaped (machine : Machine) (reason : String)
  | incomplete (machine : Machine) (reason : IncompleteReason)
  deriving Repr

inductive OperationalRunResult where
  | completed (result : FrameResult)
  | completedTopLevel (result : FrameResult) (substate : TransactionSubstate)
  | incomplete (reason : IncompleteReason) (machine : Machine)
  deriving Repr

def DriverStep.machine : DriverStep → Machine
  | .continue machine
  | .suspend machine _ _
  | .halt machine _
  | .topLevelHalt machine _ _
  | .cancelled machine _
  | .escaped machine _
  | .incomplete machine _ => machine

structure OperationalSemantics where
  /- The accepted frame-machine carrier supplies the source settlement
     primitives.  It is not a production proof: each primitive still needs a
     production adapter obligation in `ProductionAdapterObligations`. -/
  frame : Semantics
  clearReturnData : Machine → Option Machine
  prepareFresh : Machine → Option Machine
  prepareContinuation : Machine → Option Machine
  runNoTrace : Machine → Option BytecodeExecution
  runNoTraceCancelable : Machine → Option BytecodeExecution
  runTraced : Machine → Option BytecodeExecution
  runTracedCancelable : Machine → Option BytecodeExecution
  runFullPrecompile : Machine → Option PrecompileExecution
  failureResult : ExceptionKind → Machine → Option FrameResult
  precompileFailureResult : PrecompileRunOutcome → Machine → Option FrameResult
  settleChild : Machine → FrameResult → Option Settlement
  createDeposit : Machine → FrameResult → Option CreateDepositExecution
  prepareTopLevelSubstate : Machine → FrameResult → Option TransactionSubstate
  settleTopLevel : Machine → FrameResult → TransactionSubstate → Option FrameResult
  cleanup : Machine → Option Machine

def runBytecode (semantics : OperationalSemantics) (machine : Machine) :
    Option BytecodeExecution :=
  match machine.current.dispatchTable with
  | .noTrace => semantics.runNoTrace machine
  | .noTraceCancelable => semantics.runNoTraceCancelable machine
  | .traced => semantics.runTraced machine
  | .tracedCancelable => semantics.runTracedCancelable machine

def dispatchSubject : FrameKind → String
  | .bytecode => "bytecode"
  | .precompile _ => "full-precompile"

def validBatch (machine : Machine) (batch : OpcodeBatch) : Prop :=
  batch.startPc < machine.current.code.length ∧
    0 < batch.completedOpcodeCount ∧
    batch.successorPc ≤ machine.current.code.length

def validNonterminalBatch (machine : Machine) (batch : OpcodeBatch) : Prop :=
  validBatch machine batch ∧ batch.terminal = false ∧
    batch.successorPc < machine.current.code.length

/- `RunDispatchLoop` returns to its outer cancellation poll only at the end of
   a complete 1024-opcode epoch.  A terminal handler may end a final epoch
   early; a nonterminal epoch may not be split into arbitrary sub-batches.
   Cancellation counts are cumulative across completed epochs (1024, 2048,
   ...), so the second poll cannot be confused with a fresh epoch.  The
   non-cancelable tail-call chain has no polling epoch, so it is represented
   by one terminal batch whose count is the exact chain count. -/
def cancelableEpochsValid (machine : Machine) : List OpcodeBatch → Prop
  | [] => True
  | [batch] => validBatch machine batch ∧
      (batch.terminal = true ∨
        (validNonterminalBatch machine batch ∧ batch.completedOpcodeCount = 1024))
  | batch :: batches => validBatch machine batch ∧
      validNonterminalBatch machine batch ∧ batch.completedOpcodeCount = 1024 ∧
      cancelableEpochsValid machine batches

def dispatchEpochsValid (machine : Machine) (table : DispatchTable) : List OpcodeBatch → Prop
  | [] => True
  | [batch] => validBatch machine batch ∧
      (if table.cancelable then
         batch.terminal = true ∨
           (validNonterminalBatch machine batch ∧ batch.completedOpcodeCount = 1024)
       else batch.terminal = true)
  | batch :: batches =>
      if table.cancelable then
        validNonterminalBatch machine batch ∧
          batch.completedOpcodeCount = 1024 ∧ cancelableEpochsValid machine batches
      else
        False

def validCancellation (machine : Machine) : CancellationPoint → Prop
  | .beforeFirstOpcode => machine.current.pc < machine.current.code.length
  | .afterCompleteBatch completedOpcodeCount successorPc =>
      completedOpcodeCount > 0 ∧ completedOpcodeCount % 1024 = 0 ∧
        successorPc ≤ machine.current.code.length

/- The universal presentation is equivalent to checking the finite batch list,
   but the executable form matters: the operational driver must be reducible
   for the concrete mutation vectors without importing a classical oracle. -/
def allBatchesValid (machine : Machine) (batches : List OpcodeBatch) : Prop :=
  batches.all (fun batch => decide (validBatch machine batch)) = true

def batchesAccountedFrom (machine : Machine) (pc opcodeCount : Nat) :
    List OpcodeBatch → Prop
  | [] => True
  | batch :: batches =>
      batch.startPc = pc ∧
        batch.startOpcodeCount = opcodeCount ∧
        batchesAccountedFrom machine batch.successorPc
          (opcodeCount + batch.completedOpcodeCount) batches

def batchesAccounted (machine : Machine) (batches : List OpcodeBatch) : Prop :=
  batchesAccountedFrom machine machine.current.pc machine.current.opcodeCount batches

def completedEpochPollsFrom (completedOpcodeCount : Nat) : List OpcodeBatch →
    List CancellationPoint
  | [] => []
  | batch :: batches =>
      let completed := completedOpcodeCount + batch.completedOpcodeCount
      if batch.terminal then []
      else [CancellationPoint.afterCompleteBatch completed batch.successorPc] ++
        completedEpochPollsFrom completed batches

def completedEpochPolls : List OpcodeBatch → List CancellationPoint :=
  completedEpochPollsFrom 0

def cancellationPollSchedule (machine : Machine) (table : DispatchTable)
    (batches : List OpcodeBatch) : List CancellationPoint :=
  if table.cancelable then
    (if machine.current.pc < machine.current.code.length then
      [CancellationPoint.beforeFirstOpcode]
    else []) ++ completedEpochPolls batches
  else []

def lastCancellationPoll : List CancellationPoint → Option CancellationPoint
  | [] => none
  | [poll] => some poll
  | _ :: polls => lastCancellationPoll polls

def completedOpcodeTotal : List OpcodeBatch → Nat
  | [] => 0
  | batch :: batches => batch.completedOpcodeCount + completedOpcodeTotal batches

def stepRouteValid (step : MachineStep) : Prop :=
  MachineStepControlValid step

def finalSuccessorPc : List OpcodeBatch → Option Nat
  | [] => none
  | [batch] => some batch.successorPc
  | _ :: batches => finalSuccessorPc batches

def lastBatchTerminal : List OpcodeBatch → Option Bool
  | [] => none
  | [batch] => some batch.terminal
  | _ :: batches => lastBatchTerminal batches

def terminalOutcome : BytecodeRunOutcome → Bool
  | .returned step =>
      match step.result with
      | .continue _ => false
      | .suspend _ _ | .halt _ => true
  | .thrownEvm _ _ | .thrownOverflow _ | .escaped _ _ | .cancelled _ _ _ => true

def terminalBatchMatches (execution : BytecodeExecution) : Prop :=
  match execution.outcome, lastBatchTerminal execution.batches with
  | .cancelled _ _ _, none => True
  | .cancelled _ _ _, some terminal => terminal = false
  | outcome, none => terminalOutcome outcome = true
  | outcome, some terminal => terminal = terminalOutcome outcome

def batchStateAccounting (machine : Machine) (batches : List OpcodeBatch)
    (finalPc finalOpcodeCount : Nat) : Prop :=
  match finalSuccessorPc batches with
  | some successorPc =>
      batchesAccounted machine batches ∧
        finalPc = successorPc ∧
        finalOpcodeCount = machine.current.opcodeCount + completedOpcodeTotal batches
  | none =>
      batches = [] ∧
        finalPc = machine.current.pc ∧
        finalOpcodeCount = machine.current.opcodeCount

def batchFuelAccounting (machine : Machine) (execution : BytecodeExecution) : Prop :=
  match execution.outcome with
  | .returned step =>
      match finalSuccessorPc execution.batches with
      | some successorPc =>
          batchesAccounted machine execution.batches ∧
            match step.result with
            | .continue frame =>
                frame.opcodeCount = machine.current.opcodeCount + completedOpcodeTotal execution.batches ∧
                  frame.pc = successorPc
            | .halt result =>
                result.frame.opcodeCount = machine.current.opcodeCount + completedOpcodeTotal execution.batches ∧
                  result.frame.pc = successorPc
            | .suspend _ _ =>
                step.machine.current.pc = successorPc ∧
                  step.machine.current.opcodeCount =
                    machine.current.opcodeCount + completedOpcodeTotal execution.batches
      | none =>
          execution.batches = [] ∧
            match step.result with
            | .continue frame =>
                frame.pc = machine.current.pc ∧
                  frame.opcodeCount = machine.current.opcodeCount
            | .halt result =>
                result.frame.pc = machine.current.pc ∧
                  result.frame.opcodeCount = machine.current.opcodeCount
            | .suspend _ _ =>
                step.machine.current.pc = machine.current.pc ∧
                  step.machine.current.opcodeCount = machine.current.opcodeCount
  | .cancelled cancelledMachine .beforeFirstOpcode _ =>
      execution.batches = [] ∧
        cancelledMachine.current.pc = machine.current.pc ∧
        cancelledMachine.current.opcodeCount = machine.current.opcodeCount
  | .cancelled cancelledMachine (.afterCompleteBatch completedOpcodeCount successorPc) _ =>
      batchesAccounted machine execution.batches ∧ execution.batches ≠ [] ∧
        completedOpcodeTotal execution.batches = completedOpcodeCount ∧
        finalSuccessorPc execution.batches = some successorPc ∧
        cancelledMachine.current.pc = successorPc ∧
        cancelledMachine.current.opcodeCount = machine.current.opcodeCount + completedOpcodeCount
  | .thrownEvm failureMachine _ | .thrownOverflow failureMachine |
      .escaped failureMachine _ =>
      batchStateAccounting machine execution.batches failureMachine.current.pc
        failureMachine.current.opcodeCount

/- The route stream is an adapter-supplied dispatch-evidence stream, not a
   route-label set.  The driver requires one route and PC witness for every
   reported completed opcode, checks that each witness is in range, and binds
   each route to the byte at that witness.  The list order is the adapter's
   source-reported execution order; it is deliberately not constrained to be
   numerically monotone because valid EVM jumps can revisit a PC.  This is a
   well-formedness check on adapter evidence, not validation of the C#
   handler's exact execution order.  The enclosing batch still carries only a
   final successor, so per-op successor/control transitions (including
   variable-width PUSH handling) remain an explicit adapter obligation. -/
def routeEvidenceAligned (code : List Byte) : List Nat → List OpcodeRoute → Prop
  | [], [] => True
  | pc :: pcs, route :: routes =>
      pc < code.length ∧
        (match code[pc]? with
         | some byte => route.byte = byte.val
         | none => False) ∧
        routeEvidenceAligned code pcs routes
  | _, _ => False

/- This is the explicit adapter premise consumed by the bytecode validator.
   It checks only the shape of the supplied trace evidence; it does not claim
   that the evidence was produced by the source handler or reconstruct its
   per-op control transitions. -/
def adapterSuppliedRouteEvidence (machine : Machine) (execution : BytecodeExecution) : Prop :=
  execution.routes.length = completedOpcodeTotal execution.batches ∧
    execution.routePcs.length = completedOpcodeTotal execution.batches ∧
    routeEvidenceAligned machine.current.code execution.routePcs execution.routes

def bytecodeRoutesValid (semantics : OperationalSemantics) (machine : Machine)
    (execution : BytecodeExecution) : Prop :=
  adapterSuppliedRouteEvidence machine execution ∧
    execution.routes.all (fun route => decide (
      route.dispatchTable = machine.current.dispatchTable ∧ route.byte < 256 ∧
        route.instruction ≠ "" ∧ route.activationRule ≠ "" ∧
        route.package ≠ "" ∧ route.closedHandlerRoot ≠ "")) = true

def bytecodeExecutionValid (semantics : OperationalSemantics) (machine : Machine)
    (execution : BytecodeExecution) : Prop :=
    execution.dispatchTable = machine.current.dispatchTable ∧
    bytecodeRoutesValid semantics machine execution ∧
    allBatchesValid machine execution.batches ∧
    dispatchEpochsValid machine execution.dispatchTable execution.batches ∧
    terminalBatchMatches execution ∧
    batchesAccounted machine execution.batches ∧
    execution.cancellationPolls = cancellationPollSchedule machine execution.dispatchTable
      execution.batches ∧
    batchFuelAccounting machine execution ∧
    match execution.outcome with
    | .returned step =>
        stepRouteValid step ∧
          step.machine.current.kind = machine.current.kind ∧
          step.machine.current.dispatchTable = machine.current.dispatchTable
    | .thrownEvm failureMachine _ | .thrownOverflow failureMachine |
      .escaped failureMachine _ =>
        failureMachine.current.kind = machine.current.kind ∧
          failureMachine.current.dispatchTable = machine.current.dispatchTable
    | .cancelled cancelledMachine point _ =>
        execution.dispatchTable.cancelable = true ∧
          cancelledMachine.current.kind = machine.current.kind ∧
          cancelledMachine.current.dispatchTable = machine.current.dispatchTable ∧
          validCancellation machine point ∧
          lastCancellationPoll execution.cancellationPolls = some point

def precompileRouteValid (semantics : OperationalSemantics) (machine : Machine)
    (execution : PrecompileExecution) : Prop :=
  match machine.current.kind, execution.route with
  | .precompile address, some route =>
      route.address = address ∧ route.name ≠ "" ∧ route.activationRule ≠ "" ∧
        route.providerRoot ≠ "" ∧ route.wrapperManifestPath ≠ "" ∧
        route.wrapperManifestSha256 ≠ "" ∧ route.leanModule ≠ ""
  | _, _ => False

def fullPrecompileExecutionValid (semantics : OperationalSemantics) (machine : Machine)
    (execution : PrecompileExecution) : Prop :=
    precompileRouteValid semantics machine execution ∧
    match execution.outcome with
    | .returned step =>
        stepRouteValid step ∧
          step.machine.current.kind = machine.current.kind ∧
          step.machine.current.dispatchTable = machine.current.dispatchTable ∧
          match step.result with
          | .suspend _ _ | .continue _ => False
          | .halt result => result.frame.kind = machine.current.kind ∧ FrameResultControlValid result
    | .outOfGas failureMachine | .returnedFailure failureMachine _ |
      .managedException failureMachine _ | .escaped failureMachine _ =>
        failureMachine.current.kind = machine.current.kind ∧
        failureMachine.current.dispatchTable = machine.current.dispatchTable

def transactionSubstateFieldsValid (result : FrameResult) (substate : TransactionSubstate) : Prop :=
  substate.status = result.exit ∧
    (match result.exit with
     | .exception _ => substate.error ≠ none
     | .success | .revert => substate.error = none) ∧
    (match result.exit with
     | .exception kind => substate.exceptionType = some kind
     | .success | .revert => substate.exceptionType = none) ∧
    substate.substateError = result.substateError ∧
    substate.output = result.output ∧
    substate.shouldRevert = decide (result.exit = .revert) ∧
    substate.refund = result.frame.refund ∧
    substate.logs = result.frame.world.logs ∧
    substate.destroyList = result.frame.world.destroyList ∧
    substate.shouldRestoreRipemdTouch = result.shouldRestoreRipemdTouch

def pushChild (machine : Machine) (parent : SuspendedFrame) (child : Frame) : Machine :=
  { machine with
    current := child
    parents := parent :: machine.parents }

def childEntryValid (child : Frame) : Prop := child.phase = .fresh

def resumeParent (machine : Machine) (parents : List SuspendedFrame) (parent : Frame) : Machine :=
  { machine with
    current := { parent with phase := .continuation }
    parents }

/- Resume validation is intentionally scoped to the captured stack shape.  It
   does not compare the parent's PC, gas, operand stack, memory, world, or
   output; those continuation fields remain a separate production adapter
   obligation. -/
def parentStackShape (parents : List SuspendedFrame) : List Nat :=
  parents.map (fun suspended => suspended.parent.callDepth)

def validLifoStackShape (before resumed : Machine) : Prop :=
  match before.parents with
  | [] => False
  | suspended :: parents =>
      parentStackShape resumed.parents = parentStackShape parents ∧
        resumed.current.phase = .continuation ∧
        resumed.current.callDepth = suspended.parent.callDepth

def settleResume (machine resumed : Machine) : DriverStep :=
  if validLifoStackShape machine resumed then
    .continue resumed
  else
    .incomplete machine .invalidControlRoute

def cleanupFailure (semantics : OperationalSemantics) (machine : Machine) : DriverStep :=
  match semantics.cleanup machine with
  | some cleaned => .incomplete cleaned .invalidControlRoute
  | none => .incomplete machine (.unresolvedAdapter "FrameCleanupScope.Dispose/DisposeActiveFrames")

def settleComplete (semantics : OperationalSemantics) (machine : Machine)
    (result : FrameResult) : DriverStep :=
  if FrameResultControlValid result then
    match machine.parents with
    | [] =>
        if result.frame.isTopLevel then
          match semantics.prepareTopLevelSubstate machine result with
          | some substate =>
              if transactionSubstateFieldsValid result substate then
                match semantics.settleTopLevel machine result substate with
                | some settled => .topLevelHalt machine settled substate
                | none => cleanupFailure semantics machine
              else
                cleanupFailure semantics machine
          | none => cleanupFailure semantics machine
        else
          .incomplete machine .invalidControlRoute
    | _ => .incomplete machine .invalidControlRoute
  else
    cleanupFailure semantics machine

def settleSettlement (semantics : OperationalSemantics) (machine : Machine) : Settlement → DriverStep
  | .complete completed => settleComplete semantics machine completed
  | .resume resumed => settleResume machine resumed
  | .invalidControl invalid => .incomplete invalid .invalidControlRoute

def invokeFailure (semantics : OperationalSemantics) (machine : Machine)
    (kind : ExceptionKind) : DriverStep :=
  match semantics.failureResult kind machine with
  | some result =>
      if FrameResultControlValid result then .halt machine result
      else cleanupFailure semantics machine
  | none => cleanupFailure semantics machine

def invokePrecompileFailure (semantics : OperationalSemantics) (machine : Machine)
    (outcome : PrecompileRunOutcome) : DriverStep :=
  match semantics.precompileFailureResult outcome machine with
  | some result =>
      if FrameResultControlValid result then .halt machine result
      else cleanupFailure semantics machine
  | none => cleanupFailure semantics machine

def interpretBytecode (semantics : OperationalSemantics) (machine : Machine)
    (execution : BytecodeExecution) : DriverStep :=
  if h : bytecodeExecutionValid semantics machine execution then
    match execution.outcome with
    | .returned step =>
        match step.result with
        | .continue frame => .continue { step.machine with current := frame }
        | .suspend parent child => .suspend step.machine parent child
        | .halt result => .halt step.machine result
    | .thrownEvm failureMachine kind => invokeFailure semantics failureMachine kind
    | .thrownOverflow failureMachine => invokeFailure semantics failureMachine .other
    | .escaped escapedMachine reason => .escaped escapedMachine reason
    | .cancelled cancelledMachine _ reason => .cancelled cancelledMachine reason
  else
    .incomplete machine (.unresolvedAdapter "RunDispatchLoop/batch-or-cancellation-boundary")

def interpretPrecompile (semantics : OperationalSemantics) (machine : Machine)
    (execution : PrecompileExecution) : DriverStep :=
  if h : fullPrecompileExecutionValid semantics machine execution then
    match execution.outcome with
    | .returned step =>
        match step.result with
        | .halt result => .halt step.machine result
        | .continue _ | .suspend _ _ =>
            .incomplete machine .invalidControlRoute
    | .outOfGas failureMachine | .returnedFailure failureMachine _ |
      .managedException failureMachine _ =>
        invokePrecompileFailure semantics failureMachine execution.outcome
    | .escaped escapedMachine reason => .escaped escapedMachine reason
  else
    .incomplete machine (.unresolvedAdapter "ExecutePrecompile/full-frame-domain")

def enterFrame (semantics : OperationalSemantics) (machine : Machine) : Option Machine :=
  match machine.current.phase with
  | .fresh => semantics.clearReturnData machine >>= semantics.prepareFresh
  | .continuation => semantics.prepareContinuation machine
  | .running => none

def invoke (semantics : OperationalSemantics) (machine : Machine) : Option Invocation :=
  match machine.current.kind with
  | .bytecode =>
      (runBytecode semantics machine).map Invocation.bytecode
  | .precompile _ =>
      (semantics.runFullPrecompile machine).map Invocation.fullPrecompile

def step (semantics : OperationalSemantics) (machine : Machine) : DriverStep :=
  match enterFrame semantics machine with
  | none => .incomplete machine (.unresolvedAdapter "ExecuteTransaction/fresh-or-continuation-preparation")
  | some prepared =>
      match invoke semantics prepared with
      | none =>
          match prepared.current.kind with
          | .bytecode => .incomplete prepared (.unresolvedAdapter "RunDispatchLoop/dispatch-table-mode")
          | .precompile _ => .incomplete prepared (.unresolvedAdapter "ExecutePrecompile")
      | some (.bytecode execution) => interpretBytecode semantics prepared execution
      | some (.fullPrecompile execution) => interpretPrecompile semantics prepared execution

def settleHalt (semantics : OperationalSemantics) (machine : Machine)
    (result : FrameResult) : DriverStep :=
  if FrameResultControlValid result then
    if machine.parents ≠ [] then
      if machine.current.executionType.isCreate then
        if result.exit = .success then
          match semantics.createDeposit machine result with
          | some execution =>
              if createDepositOutcomeValid execution then
                settleSettlement semantics machine execution.settlement
              else
                cleanupFailure semantics machine
          | none => cleanupFailure semantics machine
        else
          match semantics.settleChild machine result with
          | some settlement => settleSettlement semantics machine settlement
          | none => cleanupFailure semantics machine
      else
        match semantics.settleChild machine result with
        | some settlement => settleSettlement semantics machine settlement
        | none => cleanupFailure semantics machine
    else
      if result.frame.isTopLevel then
        match semantics.prepareTopLevelSubstate machine result with
        | some substate =>
            if transactionSubstateFieldsValid result substate then
              match semantics.settleTopLevel machine result substate with
              | some settled => .topLevelHalt machine settled substate
              | none => cleanupFailure semantics machine
            else
              cleanupFailure semantics machine
        | none => cleanupFailure semantics machine
      else
        .incomplete machine .invalidControlRoute
  else
    cleanupFailure semantics machine

def settleStep (semantics : OperationalSemantics) : DriverStep → DriverStep
  | .halt machine result => settleHalt semantics machine result
  | other => other

def cleanupCompleted (semantics : OperationalSemantics) (machine : Machine)
    (result : FrameResult) : OperationalRunResult :=
  match semantics.cleanup machine with
  | some _ => .completed result
  | none => .incomplete (.unresolvedAdapter "FrameCleanupScope.Dispose/DisposeActiveFrames") machine

def cleanupCompletedTopLevel (semantics : OperationalSemantics) (machine : Machine)
    (result : FrameResult) (substate : TransactionSubstate) : OperationalRunResult :=
  match semantics.cleanup machine with
  | some _ => .completedTopLevel result substate
  | none => .incomplete (.unresolvedAdapter "FrameCleanupScope.Dispose/DisposeActiveFrames") machine

def runFuel : Nat → OperationalSemantics → Machine → OperationalRunResult
  | 0, _, machine => .incomplete .fuelExhausted machine
  | fuel + 1, semantics, machine =>
      match settleStep semantics (step semantics machine) with
      | .continue next => runFuel fuel semantics next
      | .suspend beforePush parent child =>
          if childEntryValid child then
            runFuel fuel semantics (pushChild beforePush parent child)
          else
            .incomplete .invalidControlRoute beforePush
      | .halt current result => cleanupCompleted semantics current result
      | .topLevelHalt current result substate =>
          cleanupCompletedTopLevel semantics current result substate
      | .cancelled current _ =>
          match semantics.cleanup current with
          | some cleaned => .incomplete .invalidControlRoute cleaned
          | none => .incomplete (.unresolvedAdapter "FrameCleanupScope.Dispose/DisposeActiveFrames") current
      | .escaped current _ =>
          match semantics.cleanup current with
          | some cleaned => .incomplete .invalidControlRoute cleaned
          | none => .incomplete (.unresolvedAdapter "FrameCleanupScope.Dispose/DisposeActiveFrames") current
      | .incomplete current reason => .incomplete reason current

def loopMeasure (machine : Machine) : Nat :=
  let remainingCode :=
    if machine.current.pc ≤ machine.current.code.length then
      machine.current.code.length - machine.current.pc
    else
      0
  machine.current.gas.gas.gasLeft + remainingCode + machine.parents.length

structure FuelAdequacyObligation (semantics : OperationalSemantics) where
  /- This is an explicit obligation, not an axiom or a theorem.  A production
     adapter must prove that every admitted continue and suspend/push
     transition decreases a well-founded gas/PC/frame-stack measure. -/
  admitted : Machine → Prop
  admittedNonempty : ∃ machine, admitted machine
  fuelBound : Machine → Nat
  measureBound : ∀ machine, admitted machine → loopMeasure machine ≤ fuelBound machine
  nonterminalDecreases : ∀ machine next,
    admitted machine →
      (settleStep semantics (step semantics machine) = .continue next) →
        loopMeasure next < loopMeasure machine
  suspendPushDecreases : ∀ machine beforePush parent child,
    admitted machine →
      (settleStep semantics (step semantics machine) =
        .suspend beforePush parent child) →
      childEntryValid child →
        loopMeasure (pushChild beforePush parent child) < loopMeasure machine

end Eip803x.Evm.FrameDriver.Operational
