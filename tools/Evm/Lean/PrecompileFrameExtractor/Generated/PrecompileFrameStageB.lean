-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- This file is generated. Do not edit.
-- Stage B IR SHA-256: 2a0eae886fb03eefe831ebd70286840adc7b2a2db26b8fdb2f66a96031632507
-- Accepted Stage A manifest SHA-256: 205affc3199397f707109d7e2ccb167affed0e4116f6a4256b03fe09894c30ae
-- Independent reference SHA-256: 36dae43c6ec05f9f1c7899389fbdc319c6808344efebe7500920551d6c735337
-- Refinement theorem source SHA-256: 3a926b90ce1322300b842a804691047f9297347afc06726b3e7b0b9951a33435
-- The leaf oracle is explicit; this module proves no cryptographic or native computation.

namespace Eip803x.PrecompileFrame.StageB.Generated

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

def oracleAddresses : List Nat := [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 256]

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

def executeBody (oracle : LeafOracle) (input : Input) : Outcome :=
  if !directEligible input then
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
  else if !input.inputMemoryValid then
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
  else
    let entry := createChild input.parentGas input.forwardedGas
    match price input.baseCost input.dataCost entry.child with
    | .overflow child =>
      { status := .pricingOverflow
        result := .stackFailure
        completion := .returned
        parentGas := restoreChildStateGasOnHalt entry.parent child
        childGasAtExit := some child
        returnData := []
        copiedOutput := []
        outputWritten := false
        stackValue := some false
        accountTouched := false
        oracleInvoked := false
        effects := [.inputLoad, .childCreate, .pricing, .stateGasRestore,
          .returnDataClear, .stackFailure] }
    | .outOfGas child =>
      { status := .pricingOutOfGas
        result := .stackFailure
        completion := .returned
        parentGas := restoreChildStateGasOnHalt entry.parent child
        childGasAtExit := some child
        returnData := []
        copiedOutput := []
        outputWritten := false
        stackValue := some false
        accountTouched := false
        oracleInvoked := false
        effects := [.inputLoad, .childCreate, .pricing, .stateGasRestore,
          .returnDataClear, .stackFailure] }
    | .success child =>
      match oracle input.address input.callData with
      | .returnedFailure =>
        let cleared := clearExecutionGas child
        { status := .returnedFailure
          result := .stackFailure
          completion := .returned
          parentGas := restoreChildStateGasOnHalt entry.parent cleared
          childGasAtExit := some cleared
          returnData := []
          copiedOutput := []
          outputWritten := false
          stackValue := some false
          accountTouched := false
          oracleInvoked := true
          effects := [.inputLoad, .childCreate, .pricing, .runOracle,
            .executionGasClear, .stateGasRestore, .returnDataClear,
            .stackFailure] }
      | .managedException =>
        let cleared := clearExecutionGas child
        { status := .managedException
          result := .stackFailure
          completion := .returned
          parentGas := restoreChildStateGasOnHalt entry.parent cleared
          childGasAtExit := some cleared
          returnData := []
          copiedOutput := []
          outputWritten := false
          stackValue := some false
          accountTouched := false
          oracleInvoked := true
          effects := [.inputLoad, .childCreate, .pricing, .runOracle,
            .executionGasClear, .stateGasRestore, .returnDataClear,
            .stackFailure] }
      | .success output =>
        let copied := output.take input.outputLength
        let refunded := refundChildGas entry.parent child
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

def executePrecompile (oracle : LeafOracle) (input : Input) : Outcome :=
  if input.cancelable && input.cancelledBeforeDispatch then
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
  else
    applyBoundaryCancellation input (executeBody oracle input)

end Eip803x.PrecompileFrame.StageB.Generated
