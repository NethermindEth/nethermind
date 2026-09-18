-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import EvmFrameMachineExtractor.Specification.FrameMachineState

/-!
Stage E is the smallest source-attached parametric package above the already
admitted frame leaves.  Its Amsterdam/EthereumGasPolicy labels are declared
target context only; the source member and leaf algebra remain generic over
the gas policy and do not prove fork/policy wiring. It models one algebraic
pass shaped by the production
`ExecuteTransaction` shell:
prepare the current frame, choose bytecode or a full precompile, consume one
leaf result, classify a child return, and invoke the matching settlement leaf.

The leaf records are intentionally data-bearing boundaries. This file does
not assert that a C# implementation supplies them; `Refinement.FrameDriver`
only states same-leaf algebra agreement, while production simulation remains
open. In particular, `runOne` is a one-step fuel gate, not a recursive runner.
-/

namespace Eip803x.Evm.FrameDriver

open FrameMachineState

inductive DispatchSubject where
  | bytecode
  | fullPrecompile
  deriving DecidableEq, Repr

inductive CancellationBoundary where
  | beforeFirstOpcode
  /- The source reports the completed count and successor at this boundary.
     Positivity, 1024-opcode completion, and successor validity are admitted
     premises; this data constructor does not enforce those arithmetic facts. -/
  | afterCompleteBatch (completedOpcodeCount successorPc : Nat)
  deriving DecidableEq, Repr

inductive DirectInlineStaticPrecompileOutcome where
  | succeeded
  | insufficientGas
  | executionFailure (reason : String)
  deriving DecidableEq, Repr

inductive BytecodeInvocation where
  | returned (step : MachineStep)
  | thrownEvm (kind : ExceptionKind)
  | thrownOverflow
  | escaped (reason : String)
  | cancelled (boundary : CancellationBoundary) (reason : String)
  deriving Repr

inductive PrecompileInvocation where
  | returned (step : MachineStep)
  | outOfGas
  | returnedFailure (reason : Option String)
  | managedException (reason : Option String)
  | escaped (reason : String)
  deriving Repr

inductive Invocation where
  | bytecode (outcome : BytecodeInvocation)
      (directInlineStaticPrecompile : Option DirectInlineStaticPrecompileOutcome)
  | fullPrecompile (outcome : PrecompileInvocation)
  deriving Repr

def Invocation.directInline : Invocation → Option DirectInlineStaticPrecompileOutcome
  | .bytecode _ inline => inline
  | .fullPrecompile _ => none

def Invocation.machineStep : Invocation → Option MachineStep
  | .bytecode (.returned step) _
  | .fullPrecompile (.returned step) => some step
  | .bytecode (.thrownEvm _) _ => none
  | .bytecode .thrownOverflow _ => none
  | .bytecode (.escaped _) _ => none
  | .bytecode (.cancelled _ _) _ => none
  | .fullPrecompile .outOfGas => none
  | .fullPrecompile (.returnedFailure _) => none
  | .fullPrecompile (.managedException _) => none
  | .fullPrecompile (.escaped _) => none

inductive CodeDepositOutcome where
  | deposited
  | invalidCode
  | outOfGas
  deriving DecidableEq, Repr

inductive SettlementRoute where
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
  | fullPrecompileReturnedFailureNested
  | fullPrecompileReturnedFailureTop
  | fullPrecompileManagedExceptionNested
  | fullPrecompileManagedExceptionTop
  | cancelled
  | escaped
  | invalidControl
  deriving DecidableEq, Repr

inductive DriverTermination where
  | completed
  | suspended
  | cancelled
  | escaped
  | incomplete
  deriving DecidableEq, Repr

structure DriverResult where
  termination : DriverTermination
  route : SettlementRoute
  machine : Machine
  result : Option FrameResult
  reason : Option String
  directInlineStaticPrecompile : Option DirectInlineStaticPrecompileOutcome
  cleanupObserved : Bool
  deriving Repr

def DriverResult.incomplete (machine : Machine) (route : SettlementRoute := .invalidControl) : DriverResult :=
  { termination := .incomplete
    route
    machine
    result := none
    reason := some "invalidControlRoute"
    directInlineStaticPrecompile := none
    cleanupObserved := false }

structure PreparationLeaves where
  clearReturnData : Machine → Machine
  prepareFresh : Machine → Machine
  prepareContinuation : Machine → Machine

structure BytecodeFrameOutcome where
  outcome : BytecodeInvocation
  directInlineStaticPrecompile : Option DirectInlineStaticPrecompileOutcome

structure DispatchLeaves where
  runBytecode : Machine → BytecodeFrameOutcome
  runFullPrecompile : Machine → PrecompileInvocation

/- The generated kernel carries these records from the admitted Roslyn
topology.  They are evidence for which source node a semantic branch is
attached to; they are not a semantic substitute for the accepted leaves. -/
structure ControlNodeEvidence where
  member : String
  id : String
  kind : String
  parentId : String
  arm : String
  condition : String
  sha256 : String
  operations : List String
  deriving Repr

inductive ControlPlanStage where
  | prepare
  | dispatch
  | classify
  | settle
  | cleanup
  deriving DecidableEq, Repr

structure ControlPlanStep where
  stage : ControlPlanStage
  member : String
  nodeId : String
  kind : String
  sourceArm : String
  sourceSha256 : String
  deriving Repr

structure BranchBinding where
  branch : String
  member : String
  nodeId : String
  kind : String
  sourceArm : String
  sourceSha256 : String
  arm : String
  deriving Repr

/- The settlement record keeps production operation families distinct.  The
`childReturn` callbacks are the bridge to the accepted frame-journal,
gas/refund, and state-settlement leaves; they are not replaced by a generic
postcondition. -/
structure SettlementLeaves where
  continueStep : Invocation → Machine → DriverResult
  suspendStep : Invocation → Machine → DriverResult
  childReturn : SettlementRoute → Invocation → Machine → DriverResult
  topLevelReturn : SettlementRoute → Invocation → Machine → DriverResult
  fullPrecompileFailure : SettlementRoute → Invocation → Machine → DriverResult
  cancelled : Invocation → Machine → DriverResult
  escaped : Invocation → Machine → DriverResult
  cleanup : DriverResult → DriverResult

structure SourceControl where
  admittedMembers : List String
  admittedOperations : List String
  deriving DecidableEq, Repr

def sourceControlReady (control : SourceControl) : Bool :=
  let requiredMembers := ["ExecuteTransaction", "RunByteCode", "RunDispatchLoop", "Dispose"]
  let requiredOperations :=
    [ "ExecuteTransaction:while"
      , "ExecuteTransaction:call:ExecutePrecompile"
      , "ExecuteTransaction:call:ExecuteCall"
      , "ExecuteTransaction:call:PrepareNextCallFrame"
      , "ExecuteTransaction:call:HandleException"
      , "ExecuteTransaction:call:HandleRegularReturn"
      , "ExecuteTransaction:call:HandleCreate"
      , "ExecuteTransaction:call:HandleRevert"
      , "ExecuteTransaction:call:HandleFailure"
      , "RunByteCode:call:RunDispatchLoop"
      , "RunDispatchLoop:while"
      , "RunDispatchLoop:call:ThrowOperationCanceledException"
      , "Dispose:call:DisposeActiveFrames" ]
  requiredMembers.all (fun member => member ∈ control.admittedMembers) &&
    requiredOperations.all (fun operation => operation ∈ control.admittedOperations)

structure DriverLeaves where
  preparation : PreparationLeaves
  dispatch : DispatchLeaves
  settlement : SettlementLeaves
  classifyNestedCreateDeposit : Invocation → Machine → CodeDepositOutcome

structure Input where
  machine : Machine
  deriving Repr

def subjectOf (machine : Machine) : DispatchSubject :=
  match machine.current.kind with
  | .bytecode => .bytecode
  | .precompile _ => .fullPrecompile

def topLevel (machine : Machine) : Bool := machine.current.isTopLevel

def admitted (machine : Machine) : Prop :=
  machine.current.phase = .fresh ∨ machine.current.phase = .continuation

def phaseReady (machine : Machine) : Bool :=
  match machine.current.phase with
  | .fresh | .continuation => true
  | .running => false

def returnedRoute (machine : Machine) (result : FrameResult) : SettlementRoute :=
  match result.exit with
  | .success =>
      if topLevel machine then .topLevelSuccess
      else if machine.current.executionType.isCreate then .childCreateSuccess
      else .childSuccess
  | .revert => if topLevel machine then .topLevelRevert else .childRevert
  | .exception _ => if topLevel machine then .topLevelException else .childException

def classifyBytecodeReturned (machine : Machine) (step : MachineStep) : SettlementRoute :=
  match step.result with
  | .continue _ => .continued
  | .suspend _ _ => .suspended
  | .halt result => returnedRoute machine result

def classifyPrecompileReturned (machine : Machine) (step : MachineStep) : SettlementRoute :=
  match step.result with
  | .continue _ => .invalidControl
  | .suspend _ _ => .invalidControl
  | .halt result =>
      match result.precompileSuccess with
      | some false =>
          if topLevel machine then .fullPrecompileReturnedFailureTop
          else .fullPrecompileReturnedFailureNested
      | _ => returnedRoute machine result

def classify (machine : Machine) : Invocation → SettlementRoute
  | .bytecode (.returned step) _ => classifyBytecodeReturned machine step
  | .bytecode (.thrownEvm _) _ =>
      if topLevel machine then .topLevelException else .childException
  | .bytecode .thrownOverflow _ =>
      if topLevel machine then .topLevelException else .childException
  | .bytecode (.escaped _) _ => .escaped
  | .bytecode (.cancelled _ _) _ => .cancelled
  | .fullPrecompile (.returned step) => classifyPrecompileReturned machine step
  | .fullPrecompile .outOfGas =>
      if topLevel machine then .fullPrecompileOutOfGasTop
      else .fullPrecompileOutOfGasNested
  | .fullPrecompile (.returnedFailure _) =>
      if topLevel machine then .fullPrecompileReturnedFailureTop
      else .fullPrecompileReturnedFailureNested
  | .fullPrecompile (.managedException _) =>
      if topLevel machine then .fullPrecompileManagedExceptionTop
      else .fullPrecompileManagedExceptionNested
  | .fullPrecompile (.escaped _) => .escaped

def tag (route : SettlementRoute) (result : DriverResult) : DriverResult :=
  { result with route }

def settleCreate (leaves : DriverLeaves) (invocation : Invocation) (machine : Machine) : DriverResult :=
  match leaves.classifyNestedCreateDeposit invocation machine with
  | .deposited => tag .childCreateSuccess
      (leaves.settlement.childReturn .childCreateSuccess invocation machine)
  | .invalidCode => tag .childCreateInvalidCode
      (leaves.settlement.childReturn .childCreateInvalidCode invocation machine)
  | .outOfGas => tag .childCreateOutOfGas
      (leaves.settlement.childReturn .childCreateOutOfGas invocation machine)

def settle (leaves : DriverLeaves) (route : SettlementRoute)
    (invocation : Invocation) (machine : Machine) : DriverResult :=
  match route with
  | .continued => tag .continued (leaves.settlement.continueStep invocation machine)
  | .suspended => tag .suspended (leaves.settlement.suspendStep invocation machine)
  | .childSuccess | .childRevert | .childException =>
      tag route (leaves.settlement.childReturn route invocation machine)
  | .childCreateSuccess => settleCreate leaves invocation machine
  | .childCreateInvalidCode | .childCreateOutOfGas =>
      tag route (leaves.settlement.childReturn route invocation machine)
  | .topLevelSuccess | .topLevelRevert | .topLevelException =>
      tag route (leaves.settlement.topLevelReturn route invocation machine)
  | .fullPrecompileOutOfGasNested | .fullPrecompileOutOfGasTop
  | .fullPrecompileReturnedFailureNested | .fullPrecompileReturnedFailureTop
  | .fullPrecompileManagedExceptionNested | .fullPrecompileManagedExceptionTop =>
      tag route (leaves.settlement.fullPrecompileFailure route invocation machine)
  | .cancelled => tag .cancelled (leaves.settlement.cancelled invocation machine)
  | .escaped => tag .escaped (leaves.settlement.escaped invocation machine)
  | .invalidControl => DriverResult.incomplete machine

def cleanupTerminal (leaves : DriverLeaves) (result : DriverResult) : DriverResult :=
  match result.termination with
  | .completed | .suspended => result
  | .cancelled | .escaped | .incomplete => leaves.settlement.cleanup result

inductive FuelResult where
  | exhausted (machine : Machine)
  | stepped (result : DriverResult)
  deriving Repr

end Eip803x.Evm.FrameDriver
