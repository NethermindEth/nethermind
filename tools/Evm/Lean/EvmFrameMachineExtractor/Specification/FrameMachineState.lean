-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Gas
import Eip803x.Evm.MemoryStackControl

/-!
Representation boundary shared by the independent reference and a future
source-extracted frame driver. The types retain the production discriminants
which select settlement paths; the executable composition is in
`FrameMachineExecution` and is not imported by generated code.
-/

namespace Eip803x.Evm.FrameMachineState

open MemoryStackControl

abbrev Byte := MemoryStackControl.Byte

inductive DispatchTable where
  | noTrace
  | noTraceCancelable
  | traced
  | tracedCancelable
  deriving DecidableEq, Repr

namespace DispatchTable

def all : List DispatchTable := [.noTrace, .noTraceCancelable, .traced, .tracedCancelable]

def sourceName : DispatchTable -> String
  | .noTrace => "NoTrace"
  | .noTraceCancelable => "NoTraceCancelable"
  | .traced => "Traced"
  | .tracedCancelable => "TracedCancelable"

def tracing : DispatchTable -> Bool
  | .noTrace | .noTraceCancelable => false
  | .traced | .tracedCancelable => true

def cancelable : DispatchTable -> Bool
  | .noTrace | .traced => false
  | .noTraceCancelable | .tracedCancelable => true

end DispatchTable

inductive OpcodeRouteKind where
  | enabled
  | disabled
  | badInstruction
  deriving DecidableEq, Repr

structure ProofTheoremIdentity where
  fullyQualifiedName : String
  signatureSha256 : String
  deriving DecidableEq, Repr

structure OpcodePackageBinding where
  name : String
  manifestPath : String
  manifestSha256 : String
  leanModule : String
  proofModulePath : String
  proofModuleSha256 : String
  requiredTheorems : List ProofTheoremIdentity
  declaredOpcodeCount : Nat
  deriving DecidableEq, Repr

structure OpcodeRoute where
  dispatchTable : DispatchTable
  byte : Nat
  instruction : String
  kind : OpcodeRouteKind
  activationRule : String
  package : String
  closedHandlerRoot : String
  deriving DecidableEq, Repr

structure PrecompileRoute where
  name : String
  address : Nat
  activationRule : String
  providerRoot : String
  wrapperManifestPath : String
  wrapperManifestSha256 : String
  leanModule : String
  fullyQualifiedTheorem : Option String
  deriving DecidableEq, Repr

inductive ExecutionType where
  | transaction
  | call
  | staticCall
  | delegateCall
  | callCode
  | create
  | create2
  deriving DecidableEq, Repr

namespace ExecutionType

def isCreate : ExecutionType -> Bool
  | .create | .create2 => true
  | _ => false

end ExecutionType

inductive FrameKind where
  | bytecode
  | precompile (address : Nat)
  deriving DecidableEq, Repr

inductive FramePhase where
  | fresh
  | continuation
  | running
  deriving DecidableEq, Repr

inductive ExceptionKind where
  | outOfGas
  | invalidCode
  | collision
  | stackUnderflow
  | stackOverflow
  | invalidJump
  | staticViolation
  | precompileFailure
  | other
  deriving DecidableEq, Repr

inductive FrameExit where
  | success
  | revert
  | exception (kind : ExceptionKind)
  deriving DecidableEq, Repr

inductive FailureControlRoute where
  | ordinary
  | handleException
  | handleFailure
  | nestedPrecompileSoftFailure
  | directPrecompileSoftFailure
  deriving DecidableEq, Repr

inductive IncompleteReason where
  | fuelExhausted
  | unresolvedAdapter (name : String)
  | invalidControlRoute
  deriving DecidableEq, Repr

structure WorldToken where
  durable : Nat
  reversible : Nat
  accessedAccounts : Nat
  accessedStorage : Nat
  logs : List Nat
  destroyList : List Nat
  deriving DecidableEq, Repr

structure PreviousCallResult where
  createdAddress : Option UInt256
  success : Option Bool
  deriving DecidableEq, Repr

structure Frame where
  kind : FrameKind
  executionType : ExecutionType
  phase : FramePhase
  dispatchTable : DispatchTable
  code : List Byte
  input : List Byte
  returnData : List Byte
  output : List Byte
  outputDestination : Nat
  outputLength : Nat
  pc : Nat
  opcodeCount : Nat
  gas : FrameGasState
  refund : Int
  initialStateGasUsed : Nat
  stateGasRefundAdvanced : Nat
  isTopLevel : Bool
  isStatic : Bool
  isCreateOnPreExistingAccount : Bool
  isCreateStateGasCharged : Bool
  newAccountCharged : Bool
  stack : Stack
  memory : Memory
  world : WorldToken
  snapshot : WorldToken
  callDepth : Nat
  trace : List String
  cancellationRequested : Bool
  deriving Repr

structure ChildSettlementBaseline where
  executionType : ExecutionType
  snapshot : WorldToken
  initialStateGasUsed : Nat
  stateGasRefundAdvancedAtEntry : Nat
  isCreateOnPreExistingAccount : Bool
  isCreateStateGasCharged : Bool
  newAccountCharged : Bool
  deriving Repr

structure SuspendedFrame where
  parent : Frame
  gasEntry : FrameEntry
  childBaseline : ChildSettlementBaseline
  actionTraceOpen : Bool
  deriving Repr

structure Machine where
  current : Frame
  parents : List SuspendedFrame
  previousCallResult : PreviousCallResult
  previousCallOutputDestination : Nat
  previousCallOutputLength : Nat
  returnDataBuffer : List Byte
  shouldRestoreRipemdTouch : Bool
  isTracingActions : Bool
  transactionTrace : List String
  controlTrace : List String
  deriving Repr

structure FrameResult where
  exit : FrameExit
  controlRoute : FailureControlRoute
  frame : Frame
  output : List Byte
  createdAddress : Option UInt256
  precompileSuccess : Option Bool
  substateError : Option String
  shouldRestoreRipemdTouch : Bool
  controlTrace : List String
  deriving Repr

inductive StepResult where
  | continue (frame : Frame)
  | suspend (parent : SuspendedFrame) (child : Frame)
  | halt (result : FrameResult)
  deriving Repr

structure MachineStep where
  machine : Machine
  controlRoute : FailureControlRoute
  result : StepResult
  deriving Repr

inductive Settlement where
  | complete (result : FrameResult)
  | resume (machine : Machine)
  | invalidControl (machine : Machine)
  deriving Repr

inductive RunResult where
  | completed (result : FrameResult)
  | incomplete (reason : IncompleteReason) (machine : Machine)
  deriving Repr

structure CodeDepositPlan where
  executionCost : Nat
  stateCost : Nat
  invalidCode : Bool
  failOnInsufficientGas : Bool
  deriving Repr

structure SettlementPrimitives where
  incorporateAdvancedRefund : Frame -> Frame -> Frame × Frame
  refundChildGas : FrameEntry -> FrameGasState -> GasState
  returnChildExecutionGas : Frame -> Frame -> Frame
  removeAdvancedRefund : Frame -> Frame -> Frame × Frame
  restoreChildStateGas : Frame -> Frame -> Frame
  restoreChildStateGasOnHalt : Frame -> Frame -> Frame
  revertRefundToHalt : Frame -> Frame -> Frame
  refundRevertedTopLevelStateGas : Frame -> Frame
  clearExecutionGas : Frame -> Frame
  repayStateGasSpill : Frame -> Frame
  creditStateGasRefund : Frame -> Nat -> Frame
  stateGasCreateCost : Nat
  stateGasNewAccountCost : Nat
  commitChild : Frame -> Frame -> Frame
  restoreSnapshot : WorldToken -> Frame -> Frame
  restoreRipemdTouch : Bool -> Frame -> Frame
  traceOperationRemainingGasZero : Machine -> Machine
  traceOperationFailure : ExceptionKind -> Machine -> Machine
  traceActionFailure : ExceptionKind -> Machine -> Machine
  calculateCodeDeposit : FrameResult -> Frame -> CodeDepositPlan
  chargeCodeDeposit : CodeDepositPlan -> Frame -> Option Frame
  consumeReturnedExecutionGas : Frame -> Nat -> Frame
  insertCode : FrameResult -> Frame -> Frame
  deleteCreatedAccount : Frame -> Frame

structure Semantics where
  enterFreshFrame : Frame -> Frame
  enterContinuation : Machine -> Machine
  runOpcode : OpcodeRoute -> Machine -> MachineStep
  precompileEnabled : PrecompileRoute -> Bool
  runPrecompile : PrecompileRoute -> Machine -> MachineStep
  missingPrecompile : Nat -> Machine -> MachineStep
  stopAtEnd : Machine -> MachineStep
  badInstruction : Machine -> Byte -> MachineStep
  settlement : SettlementPrimitives

def TableByteRoutesComplete (routes : List OpcodeRoute) : Prop :=
  ∀ table : DispatchTable, ∀ byte : Nat, byte < 256 ->
    ∃ route, route ∈ routes ∧ route.dispatchTable = table ∧ route.byte = byte ∧
      ∀ other, other ∈ routes -> other.dispatchTable = table -> other.byte = byte -> other = route

def PackageBindingsUnique (packages : List OpcodePackageBinding) : Prop :=
  ∀ first ∈ packages, ∀ second ∈ packages,
    first.name = second.name -> first = second

def PrecompileAddressesUnique (routes : List PrecompileRoute) : Prop :=
  ∀ first ∈ routes, ∀ second ∈ routes,
    first.address = second.address -> first = second

def RefundCountersAgree (frame : Frame) : Prop :=
  frame.gas.gas.refundCounter = frame.refund

def CompletedRefundValid (result : FrameResult) : Prop :=
  RefundCountersAgree result.frame ∧ 0 ≤ result.frame.refund

def FrameResultControlValid (result : FrameResult) : Prop :=
  match result.exit, result.controlRoute with
  | .success, .ordinary => True
  | .revert, .ordinary | .revert, .nestedPrecompileSoftFailure => True
  | .exception _, .handleException | .exception _, .handleFailure => True
  | _, _ => False

def MachineStepControlValid (step : MachineStep) : Prop :=
  match step.result with
  | .continue _ => step.controlRoute = .ordinary ∨ step.controlRoute = .directPrecompileSoftFailure
  | .suspend _ _ => step.controlRoute = .ordinary
  | .halt result => step.controlRoute = result.controlRoute ∧ FrameResultControlValid result

end Eip803x.Evm.FrameMachineState
