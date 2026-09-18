-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Refinement.StateGasTransitionAdapterKernel

namespace Eip803x.StateGasTransitionAdapterVector

open Eip803x.Generated.StateGasTransitionAdapterKernel
open Eip803x.Generated.StateGasTransitionKernel

private def result
    (value : Nat)
    (stateReservoir stateGasUsed stateGasSpill stateGasSpillRefunded unappliedAmount : Int) :
    StateGasTransitionResult :=
  { value, stateReservoir, stateGasUsed, stateGasSpill, stateGasSpillRefunded, unappliedAmount }

private def vectors : List Bool :=
  [ Eip803x.Generated.StateGasTransitionAdapterKernel.refund
      10 20 30 40 50 15 25 35 45 55 ==
      { kind := .completedVoid, transition := result 25 45 65 85 105 0 }
  , Eip803x.Generated.StateGasTransitionAdapterKernel.repayStateGasSpill
      100 8 9 10 3 ==
      { kind := .completedVoid, transition := result 107 1 9 10 10 0 }
  , Eip803x.Generated.StateGasTransitionAdapterKernel.restoreChildStateGas
      100 2 3 4 5 7 11 17 6 ==
      { kind := .completedVoid, transition := result 111 9 3 4 5 0 }
  , Eip803x.Generated.StateGasTransitionAdapterKernel.restoreChildStateGasOnHalt
      100 2 3 4 5 7 11 17 6 ==
      { kind := .completedVoid, transition := result 100 9 3 4 5 0 }
  , Eip803x.Generated.StateGasTransitionAdapterKernel.revertRefundToHalt
      100 20 30 40 50 7 11 3 ==
      { kind := .completedVoid, transition := result 100 19 23 29 47 0 }
  , Eip803x.Generated.StateGasTransitionAdapterKernel.refundStateGas
      100 0 200 100 20 150 75 true ==
      { kind := .completedVoid, transition := result 180 45 75 100 100 0 }
  , Eip803x.Generated.StateGasTransitionAdapterKernel.discardStateGas
      100 9 80 30 4 50 40 ==
      { kind := .completedDiscard, transition := result 100 9 40 30 4 10 }
  , Eip803x.Generated.StateGasTransitionAdapterKernel.addStateGasRefundToReservoir
      100 5 80 20 8 15 true ==
      { kind := .completedVoid, transition := result 112 8 80 20 20 0 }
  , Eip803x.Generated.StateGasTransitionAdapterKernel.removeStateGasRefundFromReservoir
      100 (-10) 5 20 8 20 ==
      { kind := .completedVoid, transition := result 100 (-25) 0 20 8 0 }
  , Eip803x.Generated.StateGasTransitionAdapterKernel.removeStateGasRefundFromReservoir
      17 1 2 3 4 (-1) ==
      { kind := .argumentException, transition := result 17 1 2 3 4 0 } ]

theorem all_vectors_pass : vectors.all id = true := by
  decide

theorem vector_count : vectors.length = 10 := by
  rfl

end Eip803x.StateGasTransitionAdapterVector
