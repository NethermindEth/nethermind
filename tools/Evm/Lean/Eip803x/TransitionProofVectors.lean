-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Refinement.StateGasTransition

namespace Eip803x.TransitionProofVector

open Eip803x.Refinement.StateGasTransition
open Eip803x.Generated.StateGasTransitionKernel

private def state (value : Nat) (stateReservoir stateGasUsed stateGasSpill
    stateGasSpillRefunded : Int) : ProductionGasState :=
  { gasLeft := value
    stateReservoir
    stateGasUsed
    stateGasSpill
    stateGasSpillRefunded }

private def child (stateReservoir stateGasUsed stateGasSpill
    stateGasSpillRefunded : Int) : Spec.Child :=
  { stateReservoir
    stateGasUsed
    stateGasSpill
    stateGasSpillRefunded }

private def result (value : Nat) (stateReservoir stateGasUsed stateGasSpill
    stateGasSpillRefunded unappliedAmount : Int) : Spec.Result :=
  { value
    stateReservoir
    stateGasUsed
    stateGasSpill
    stateGasSpillRefunded
    unappliedAmount }

def refundSafe : Bool :=
  let parent := state 10 3 5 8 2
  let childState := state 7 4 6 3 1
  let expected := result 17 7 11 11 3 0
  generatedResultToSpec
      (refund parent.gasLeft parent.stateReservoir parent.stateGasUsed parent.stateGasSpill
        parent.stateGasSpillRefunded childState.gasLeft childState.stateReservoir
        childState.stateGasUsed childState.stateGasSpill childState.stateGasSpillRefunded) == expected &&
    Spec.refund parent childState == expected

def repayStateGasSpillSafe : Bool :=
  let input := state 9 4 11 7 2
  let expected := result 13 0 11 7 6 0
  generatedResultToSpec
      (repayStateGasSpill input.gasLeft input.stateReservoir input.stateGasUsed
        input.stateGasSpill input.stateGasSpillRefunded) == expected &&
    Spec.repayStateGasSpill input == expected

def restoreChildStateGasSafe : Bool :=
  let parent := state 11 0 9 4 1
  let childState := child 2 3 8 3
  let expected := result 16 0 9 4 1 0
  generatedResultToSpec
      (restoreChildStateGas parent.gasLeft parent.stateReservoir parent.stateGasUsed
        parent.stateGasSpill parent.stateGasSpillRefunded childState.stateReservoir
        childState.stateGasUsed childState.stateGasSpill childState.stateGasSpillRefunded) == expected &&
    Spec.restoreChildStateGas parent childState == expected

def restoreChildStateGasOnHaltSafe : Bool :=
  let parent := state 11 0 9 4 1
  let childState := child 2 3 8 3
  let expected := result 11 0 9 4 1 0
  generatedResultToSpec
      (restoreChildStateGasOnHalt parent.gasLeft parent.stateReservoir parent.stateGasUsed
        parent.stateGasSpill parent.stateGasSpillRefunded childState.stateReservoir
        childState.stateGasUsed childState.stateGasSpill childState.stateGasSpillRefunded) == expected &&
    Spec.restoreChildStateGasOnHalt parent childState == expected

def revertRefundToHaltSafe : Bool :=
  let parent := state 11 2 9 7 4
  let childState := child 0 3 5 1
  let expected := result 11 1 6 2 3 0
  generatedResultToSpec
      (revertRefundToHalt parent.gasLeft parent.stateReservoir parent.stateGasUsed
        parent.stateGasSpill parent.stateGasSpillRefunded childState.stateGasUsed
        childState.stateGasSpill childState.stateGasSpillRefunded) == expected &&
    Spec.revertRefundToHalt parent childState == expected

def refundStateGasSafe : Bool :=
  let input := state 5 1 10 6 2
  let expected := result 9 3 4 6 6 0
  generatedResultToSpec
      (refundStateGas input.gasLeft input.stateReservoir input.stateGasUsed input.stateGasSpill
        input.stateGasSpillRefunded 7 4 true) == expected &&
    Spec.refundStateGas input 7 4 true == expected

def discardStateGasSafe : Bool :=
  let input := state 5 1 10 6 2
  let expected := result 5 1 4 6 2 1
  generatedResultToSpec
      (discardStateGas input.gasLeft input.stateReservoir input.stateGasUsed input.stateGasSpill
        input.stateGasSpillRefunded 7 4) == expected &&
    Spec.discardStateGas input 7 4 == expected

def addStateGasRefundToReservoirSafe : Bool :=
  let input := state 5 1 10 6 2
  let expected := result 9 4 10 6 6 0
  generatedResultToSpec
      (addStateGasRefundToReservoir input.gasLeft input.stateReservoir input.stateGasUsed
        input.stateGasSpill input.stateGasSpillRefunded 7 true) == expected &&
    Spec.addStateGasRefundToReservoir input 7 true == expected

def removeStateGasRefundFromReservoirSafe : Bool :=
  let input := state 5 3 10 6 2
  let expected := result 5 0 6 6 2 0
  generatedResultToSpec
      (removeStateGasRefundFromReservoir input.gasLeft input.stateReservoir input.stateGasUsed
        input.stateGasSpill input.stateGasSpillRefunded 7) == expected &&
    Spec.removeStateGasRefundFromReservoir input 7 == expected

def repayNoOutstandingSpill : Bool :=
  let input := state 5 3 4 2 2
  let expected := result 5 3 4 2 2 0
  generatedResultToSpec
      (repayStateGasSpill input.gasLeft input.stateReservoir input.stateGasUsed
        input.stateGasSpill input.stateGasSpillRefunded) == expected &&
    Spec.repayStateGasSpill input == expected

def refundStateGasAtFloor : Bool :=
  let input := state 5 1 10 6 2
  let expected := result 5 1 10 6 2 0
  generatedResultToSpec
      (refundStateGas input.gasLeft input.stateReservoir input.stateGasUsed input.stateGasSpill
        input.stateGasSpillRefunded 3 10 true) == expected &&
    Spec.refundStateGas input 3 10 true == expected

def addStateGasRefundUntracked : Bool :=
  let input := state 5 1 10 6 2
  let expected := result 5 8 10 6 2 0
  generatedResultToSpec
      (addStateGasRefundToReservoir input.gasLeft input.stateReservoir input.stateGasUsed
        input.stateGasSpill input.stateGasSpillRefunded 7 false) == expected &&
    Spec.addStateGasRefundToReservoir input 7 false == expected

def removeStateGasRefundFromReservoirCovered : Bool :=
  let input := state 5 8 10 6 2
  let expected := result 5 5 10 6 2 0
  generatedResultToSpec
      (removeStateGasRefundFromReservoir input.gasLeft input.stateReservoir input.stateGasUsed
        input.stateGasSpill input.stateGasSpillRefunded 3) == expected &&
    Spec.removeStateGasRefundFromReservoir input 3 == expected

/-- A generated-only exact-width vector: this is intentionally outside the mathematical refinement domain. -/
def refundUInt64Wrap : Bool :=
  (refund uint64Max 0 0 0 0 1 0 0 0 0).value == 0

def all : List Bool :=
  [ refundSafe
  , repayStateGasSpillSafe
  , restoreChildStateGasSafe
  , restoreChildStateGasOnHaltSafe
  , revertRefundToHaltSafe
  , refundStateGasSafe
  , discardStateGasSafe
  , addStateGasRefundToReservoirSafe
  , removeStateGasRefundFromReservoirSafe
  , repayNoOutstandingSpill
  , refundStateGasAtFloor
  , addStateGasRefundUntracked
  , removeStateGasRefundFromReservoirCovered
  , refundUInt64Wrap
  ]

theorem all_pass : all.all id = true := by
  native_decide

theorem vector_count : all.length = 14 := rfl

end Eip803x.TransitionProofVector
