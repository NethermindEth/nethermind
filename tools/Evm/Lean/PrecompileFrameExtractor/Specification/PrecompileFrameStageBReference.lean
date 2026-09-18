-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

namespace Eip803x.PrecompileFrame.StageB.Reference

abbrev Bytes := List UInt8

def uint64Max : Nat := 2 ^ 64 - 1

structure GasState where
  gasLeft : Nat
  stateReservoir : Int
  stateGasUsed : Int
  stateGasSpill : Int
  stateGasSpillRefunded : Int
  deriving DecidableEq, Repr

inductive LeafOracleResult where
  | success (output : Bytes)
  | returnedFailure
  | managedException
  deriving DecidableEq, Repr

abbrev LeafOracle := Nat -> Bytes -> LeafOracleResult

inductive Status where
  | notStarted
  | declined
  | inputMemoryOutOfGas
  | pricingOverflow
  | pricingOutOfGas
  | returnedFailure
  | managedException
  | outputCopyOutOfGas
  | success
  deriving DecidableEq, Repr

inductive Result where
  | cancelled
  | declined
  | outOfGas
  | stackFailure
  | stackSuccess
  deriving DecidableEq, Repr

inductive Completion where
  | returned
  | cancelledBeforeDispatch
  | cancelledAtBoundary
  deriving DecidableEq, Repr

inductive Effect where
  | preDispatchCancellationPoll
  | inputLoad
  | childCreate
  | pricing
  | runOracle
  | executionGasClear
  | stateGasRestore
  | accountTouch
  | childRefund
  | returnDataClear
  | returnDataSet
  | outputCopy
  | returnOutOfGas
  | stackFailure
  | stackSuccess
  | boundaryCancellationPoll
  deriving DecidableEq, Repr

structure Input where
  address : Nat
  callData : Bytes
  instructionTracing : Bool
  actionTracing : Bool
  isRipemd160 : Bool
  cancelable : Bool
  cancelledBeforeDispatch : Bool
  completedWithoutException : Bool
  opcodeCount : Nat
  cancelledAtBoundary : Bool
  nextProgramCounter : Nat
  codeLength : Nat
  inputMemoryValid : Bool
  parentGas : GasState
  forwardedGas : Nat
  baseCost : Nat
  dataCost : Nat
  outputLength : Nat
  outputCopyValid : Bool
  priorReturnData : Bytes
  deriving DecidableEq, Repr

structure Outcome where
  status : Status
  result : Result
  completion : Completion
  parentGas : GasState
  childGasAtExit : Option GasState
  returnData : Bytes
  copiedOutput : Bytes
  outputWritten : Bool
  stackValue : Option Bool
  accountTouched : Bool
  oracleInvoked : Bool
  effects : List Effect
  deriving DecidableEq, Repr

structure ChildEntry where
  parent : GasState
  child : GasState
  deriving DecidableEq, Repr

inductive PricingResult where
  | overflow (gas : GasState)
  | outOfGas (gas : GasState)
  | success (gas : GasState)
  deriving DecidableEq, Repr

def directEligible (input : Input) : Bool :=
  !input.instructionTracing && !input.actionTracing && !input.isRipemd160

def cancellationBoundary (input : Input) : Bool :=
  input.cancelable && input.completedWithoutException &&
    input.opcodeCount % 1024 = 0 && input.nextProgramCounter < input.codeLength

def unrefundedSpill (gas : GasState) : Int :=
  max (gas.stateGasSpill - gas.stateGasSpillRefunded) 0

def createChild (parent : GasState) (forwarded : Nat) : ChildEntry :=
  { parent := { parent with stateReservoir := 0 }
    child :=
      { gasLeft := forwarded
        stateReservoir := parent.stateReservoir
        stateGasUsed := 0
        stateGasSpill := 0
        stateGasSpillRefunded := 0 } }

def clearExecutionGas (gas : GasState) : GasState :=
  { gas with gasLeft := 0 }

def restoreChildStateGasOnHalt (parent child : GasState) : GasState :=
  let spill := unrefundedSpill child
  { gasLeft := parent.gasLeft
    stateReservoir := parent.stateReservoir + child.stateReservoir + child.stateGasUsed - spill
    stateGasUsed := parent.stateGasUsed
    stateGasSpill := parent.stateGasSpill
    stateGasSpillRefunded := parent.stateGasSpillRefunded }

def refundChildGas (parent child : GasState) : GasState :=
  { gasLeft := parent.gasLeft + child.gasLeft
    stateReservoir := parent.stateReservoir + child.stateReservoir
    stateGasUsed := parent.stateGasUsed + child.stateGasUsed
    stateGasSpill := parent.stateGasSpill + child.stateGasSpill
    stateGasSpillRefunded := parent.stateGasSpillRefunded + child.stateGasSpillRefunded }

def price (baseCost dataCost : Nat) (gas : GasState) : PricingResult :=
  if dataCost <= uint64Max && baseCost <= uint64Max - dataCost then
    let total := baseCost + dataCost
    if total <= gas.gasLeft then
      .success { gas with gasLeft := gas.gasLeft - total }
    else
      .outOfGas { gas with gasLeft := 0 }
  else
    .overflow gas

def canReachBoundary : Status -> Bool
  | .pricingOverflow | .pricingOutOfGas | .returnedFailure | .managedException | .success => true
  | _ => false

def applyBoundaryCancellation (input : Input) (outcome : Outcome) : Outcome :=
  if canReachBoundary outcome.status && cancellationBoundary input && input.cancelledAtBoundary then
    { outcome with
      result := .cancelled
      completion := .cancelledAtBoundary
      effects := outcome.effects ++ [.boundaryCancellationPoll] }
  else
    outcome

inductive PricingFailureKind where
  | overflow
  | outOfGas
  deriving DecidableEq, Repr

inductive LeafFailureKind where
  | returnedFailure
  | managedException
  deriving DecidableEq, Repr

inductive BodyDecision where
  | declined
  | inputMemoryOutOfGas
  | pricingFailure (kind : PricingFailureKind) (parent child : GasState)
  | leafFailure (kind : LeafFailureKind) (parent child : GasState)
  | leafSuccess (parent child : GasState) (output : Bytes)
  deriving DecidableEq, Repr

inductive DispatchDecision where
  | cancelledBeforeDispatch
  | completed (outcome : Outcome)
  deriving DecidableEq, Repr

def decideBody (oracle : LeafOracle) (input : Input) : BodyDecision :=
  if !directEligible input then
    .declined
  else if !input.inputMemoryValid then
    .inputMemoryOutOfGas
  else
    let entry := createChild input.parentGas input.forwardedGas
    match price input.baseCost input.dataCost entry.child with
    | .overflow child => .pricingFailure .overflow entry.parent child
    | .outOfGas child => .pricingFailure .outOfGas entry.parent child
    | .success child =>
      match oracle input.address input.callData with
      | .returnedFailure => .leafFailure .returnedFailure entry.parent child
      | .managedException => .leafFailure .managedException entry.parent child
      | .success output => .leafSuccess entry.parent child output

def renderSuccessfulLeaf (input : Input) (parent child : GasState) (output : Bytes) : Outcome :=
  let refunded := refundChildGas parent child
  let copied := output.take input.outputLength
  if !copied.isEmpty && !input.outputCopyValid then
    { status := .outputCopyOutOfGas
      result := .outOfGas
      completion := .returned
      parentGas := refunded
      childGasAtExit := some child
      returnData := output
      copiedOutput := []
      outputWritten := false
      stackValue := none
      accountTouched := true
      oracleInvoked := true
      effects := [.inputLoad, .childCreate, .pricing, .runOracle,
        .accountTouch, .childRefund, .returnDataSet, .returnOutOfGas] }
  else
    { status := .success
      result := .stackSuccess
      completion := .returned
      parentGas := refunded
      childGasAtExit := some child
      returnData := output
      copiedOutput := copied
      outputWritten := !copied.isEmpty
      stackValue := some true
      accountTouched := true
      oracleInvoked := true
      effects := [.inputLoad, .childCreate, .pricing, .runOracle,
        .accountTouch, .childRefund, .returnDataSet] ++
        (if copied.isEmpty then [] else [.outputCopy]) ++ [.stackSuccess] }

def renderBodyDecision (input : Input) : BodyDecision -> Outcome
  | .declined =>
    { status := .declined
      result := .declined
      completion := .returned
      parentGas := input.parentGas
      childGasAtExit := none
      returnData := input.priorReturnData
      copiedOutput := []
      outputWritten := false
      stackValue := none
      accountTouched := false
      oracleInvoked := false
      effects := [] }
  | .inputMemoryOutOfGas =>
    { status := .inputMemoryOutOfGas
      result := .outOfGas
      completion := .returned
      parentGas := input.parentGas
      childGasAtExit := none
      returnData := input.priorReturnData
      copiedOutput := []
      outputWritten := false
      stackValue := none
      accountTouched := false
      oracleInvoked := false
      effects := [.inputLoad, .returnOutOfGas] }
  | .pricingFailure kind parent child =>
    { status := match kind with | .overflow => .pricingOverflow | .outOfGas => .pricingOutOfGas
      result := .stackFailure
      completion := .returned
      parentGas := restoreChildStateGasOnHalt parent child
      childGasAtExit := some child
      returnData := []
      copiedOutput := []
      outputWritten := false
      stackValue := some false
      accountTouched := false
      oracleInvoked := false
      effects := [.inputLoad, .childCreate, .pricing, .stateGasRestore,
        .returnDataClear, .stackFailure] }
  | .leafFailure kind parent child =>
    let cleared := clearExecutionGas child
    { status := match kind with | .returnedFailure => .returnedFailure | .managedException => .managedException
      result := .stackFailure
      completion := .returned
      parentGas := restoreChildStateGasOnHalt parent cleared
      childGasAtExit := some cleared
      returnData := []
      copiedOutput := []
      outputWritten := false
      stackValue := some false
      accountTouched := false
      oracleInvoked := true
      effects := [.inputLoad, .childCreate, .pricing, .runOracle,
        .executionGasClear, .stateGasRestore, .returnDataClear, .stackFailure] }
  | .leafSuccess parent child output => renderSuccessfulLeaf input parent child output

def executeBody (oracle : LeafOracle) (input : Input) : Outcome :=
  renderBodyDecision input (decideBody oracle input)

def decideDispatch (oracle : LeafOracle) (input : Input) : DispatchDecision :=
  if input.cancelable && input.cancelledBeforeDispatch then
    .cancelledBeforeDispatch
  else
    .completed (applyBoundaryCancellation input (executeBody oracle input))

def renderDispatch (input : Input) : DispatchDecision -> Outcome
  | .cancelledBeforeDispatch =>
    { status := .notStarted
      result := .cancelled
      completion := .cancelledBeforeDispatch
      parentGas := input.parentGas
      childGasAtExit := none
      returnData := input.priorReturnData
      copiedOutput := []
      outputWritten := false
      stackValue := none
      accountTouched := false
      oracleInvoked := false
      effects := [.preDispatchCancellationPoll] }
  | .completed outcome => outcome

def executePrecompile (oracle : LeafOracle) (input : Input) : Outcome :=
  renderDispatch input (decideDispatch oracle input)

end Eip803x.PrecompileFrame.StageB.Reference
