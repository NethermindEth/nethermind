-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Generated.TransactionGasInitializationKernel

namespace Eip803x.TransactionGasInitializationVector

open Eip803x.Generated.TransactionGasInitializationKernel

structure InitializationVector where
  name : String
  gasLimit : Nat
  intrinsicExecutionGas : Nat
  intrinsicStateGas : Int
  eip8037Enabled : Bool
  executionGasLimitCap : Nat
  expected : TransactionGasInitializationResult
  deriving DecidableEq, Repr

structure BlockVector where
  name : String
  executionGas : Nat
  stateGas : Nat
  expected : Nat
  deriving DecidableEq, Repr

private def success
    (value : Nat)
    (stateReservoir stateGasUsed : Int) : TransactionGasInitializationResult :=
  { outcome := .success
    value
    stateReservoir
    stateGasUsed
    stateGasSpill := 0
    stateGasSpillRefunded := 0 }

private def insufficient : TransactionGasInitializationResult :=
  { outcome := .intrinsicGasExceedsLimit
    value := 0
    stateReservoir := 0
    stateGasUsed := 0
    stateGasSpill := 0
    stateGasSpillRefunded := 0 }

private def txMaxGasLimit : Nat := 16_777_216
private def perAuthorizationStateGas : Int := 35_190
private def newAccountAndAuthorizationStateGas : Int := 218_790

def initializationVectors : List InitializationVector :=
  [ { name := "enabled-below-cap"
      gasLimit := 200
      intrinsicExecutionGas := 100
      intrinsicStateGas := 50
      eip8037Enabled := true
      executionGasLimitCap := 1_000
      expected := success 50 0 50 }
  , { name := "enabled-cap-reservoir"
      gasLimit := 20_000_000
      intrinsicExecutionGas := 21_000
      intrinsicStateGas := 0
      eip8037Enabled := true
      executionGasLimitCap := 16_777_216
      expected := success 16_756_216 3_222_784 0 }
  , { name := "enabled-exact-post-intrinsic-cap"
      gasLimit := 1_150
      intrinsicExecutionGas := 100
      intrinsicStateGas := 50
      eip8037Enabled := true
      executionGasLimitCap := 1_000
      expected := success 900 100 50 }
  , { name := "execution-intrinsic-at-cap"
      gasLimit := 1_000
      intrinsicExecutionGas := 1_000
      intrinsicStateGas := 0
      eip8037Enabled := true
      executionGasLimitCap := 1_000
      expected := success 0 0 0 }
  , { name := "disabled-ignores-execution-cap"
      gasLimit := 1_000
      intrinsicExecutionGas := 100
      intrinsicStateGas := 50
      eip8037Enabled := false
      executionGasLimitCap := 0
      expected := success 850 0 50 }
  , { name := "intrinsic-total-exceeds-limit-defaults"
      gasLimit := 149
      intrinsicExecutionGas := 100
      intrinsicStateGas := 50
      eip8037Enabled := true
      executionGasLimitCap := 1_000
      expected := insufficient }
  , { name := "wrapped-intrinsic-total-before-limit-check"
      gasLimit := 0
      intrinsicExecutionGas := 18_446_744_073_709_551_615
      intrinsicStateGas := 1
      eip8037Enabled := false
      executionGasLimitCap := 0
      expected := success 0 0 1 }
  , { name := "enabled-zero-state-baseline-exact"
      gasLimit := 100
      intrinsicExecutionGas := 100
      intrinsicStateGas := 0
      eip8037Enabled := true
      executionGasLimitCap := 1_000
      expected := success 0 0 0 }
  , { name := "authorization-state-baseline-exact"
      gasLimit := 56_190
      intrinsicExecutionGas := 21_000
      intrinsicStateGas := perAuthorizationStateGas
      eip8037Enabled := true
      executionGasLimitCap := txMaxGasLimit
      expected := success 0 0 perAuthorizationStateGas }
  , { name := "authorization-state-baseline-one-short"
      gasLimit := 56_189
      intrinsicExecutionGas := 21_000
      intrinsicStateGas := perAuthorizationStateGas
      eip8037Enabled := true
      executionGasLimitCap := txMaxGasLimit
      expected := insufficient }
  , { name := "authorization-state-baseline-at-cap-boundary"
      gasLimit := 16_812_406
      intrinsicExecutionGas := 21_000
      intrinsicStateGas := perAuthorizationStateGas
      eip8037Enabled := true
      executionGasLimitCap := txMaxGasLimit
      expected := success 16_756_216 0 perAuthorizationStateGas }
  , { name := "authorization-state-baseline-one-above-cap-boundary"
      gasLimit := 16_812_407
      intrinsicExecutionGas := 21_000
      intrinsicStateGas := perAuthorizationStateGas
      eip8037Enabled := true
      executionGasLimitCap := txMaxGasLimit
      expected := success 16_756_216 1 perAuthorizationStateGas }
  , { name := "new-authority-state-baseline-exact"
      gasLimit := 239_790
      intrinsicExecutionGas := 21_000
      intrinsicStateGas := newAccountAndAuthorizationStateGas
      eip8037Enabled := true
      executionGasLimitCap := txMaxGasLimit
      expected := success 0 0 newAccountAndAuthorizationStateGas }
  , { name := "new-authority-state-baseline-at-cap-boundary"
      gasLimit := 16_996_006
      intrinsicExecutionGas := 21_000
      intrinsicStateGas := newAccountAndAuthorizationStateGas
      eip8037Enabled := true
      executionGasLimitCap := txMaxGasLimit
      expected := success 16_756_216 0 newAccountAndAuthorizationStateGas }
  , { name := "execution-intrinsic-at-cap-with-state-reservoir"
      gasLimit := 16_812_407
      intrinsicExecutionGas := txMaxGasLimit
      intrinsicStateGas := perAuthorizationStateGas
      eip8037Enabled := true
      executionGasLimitCap := txMaxGasLimit
      expected := success 0 1 perAuthorizationStateGas }
  , { name := "execution-intrinsic-above-cap-avoids-subtraction-underflow"
      gasLimit := 1_002
      intrinsicExecutionGas := 1_001
      intrinsicStateGas := 0
      eip8037Enabled := true
      executionGasLimitCap := 1_000
      expected := success 0 1 0 }
  , { name := "maximum-reservoir-wraps-to-signed-long"
      gasLimit := 18_446_744_073_709_551_615
      intrinsicExecutionGas := 0
      intrinsicStateGas := 0
      eip8037Enabled := true
      executionGasLimitCap := txMaxGasLimit
      expected := success txMaxGasLimit (-16_777_217) 0 }
  , { name := "maximum-signed-state-baseline-exact"
      gasLimit := 9_223_372_036_854_775_807
      intrinsicExecutionGas := 0
      intrinsicStateGas := 9_223_372_036_854_775_807
      eip8037Enabled := true
      executionGasLimitCap := txMaxGasLimit
      expected := success 0 0 9_223_372_036_854_775_807 }
  , { name := "negative-state-baseline-wraps-before-limit-check"
      gasLimit := 0
      intrinsicExecutionGas := 1
      intrinsicStateGas := -1
      eip8037Enabled := true
      executionGasLimitCap := txMaxGasLimit
      expected := success 0 0 (-1) } ]

def blockVectors : List BlockVector :=
  [ { name := "execution-dimension-controls-header"
      executionGas := 100
      stateGas := 40
      expected := 100 }
  , { name := "state-dimension-controls-header"
      executionGas := 40
      stateGas := 100
      expected := 100 }
  , { name := "equal-dimensions"
      executionGas := 77
      stateGas := 77
      expected := 77 }
  , { name := "zero-dimensions"
      executionGas := 0
      stateGas := 0
      expected := 0 }
  , { name := "maximum-uint64-execution"
      executionGas := 18_446_744_073_709_551_615
      stateGas := 0
      expected := 18_446_744_073_709_551_615 }
  , { name := "maximum-uint64-state"
      executionGas := 0
      stateGas := 18_446_744_073_709_551_615
      expected := 18_446_744_073_709_551_615 } ]

private def initializationPasses (vector : InitializationVector) : Bool :=
  tryCreate
    vector.gasLimit
    vector.intrinsicExecutionGas
    vector.intrinsicStateGas
    vector.eip8037Enabled
    vector.executionGasLimitCap == vector.expected

private def droppedIntrinsicStatePasses (vector : InitializationVector) : Bool :=
  tryCreate
    vector.gasLimit
    vector.intrinsicExecutionGas
    0
    vector.eip8037Enabled
    vector.executionGasLimitCap == vector.expected

private def stateGasUsedZeroedPasses (vector : InitializationVector) : Bool :=
  let result := tryCreate
    vector.gasLimit
    vector.intrinsicExecutionGas
    vector.intrinsicStateGas
    vector.eip8037Enabled
    vector.executionGasLimitCap
  { result with stateGasUsed := 0 } == vector.expected

private def capAlsoSubtractsStatePasses (vector : InitializationVector) : Bool :=
  tryCreate
    vector.gasLimit
    vector.intrinsicExecutionGas
    vector.intrinsicStateGas
    vector.eip8037Enabled
    (vector.executionGasLimitCap - Int.toNat vector.intrinsicStateGas) == vector.expected

private def reservoirZeroedPasses (vector : InitializationVector) : Bool :=
  let result := tryCreate
    vector.gasLimit
    vector.intrinsicExecutionGas
    vector.intrinsicStateGas
    vector.eip8037Enabled
    vector.executionGasLimitCap
  { result with stateReservoir := 0 } == vector.expected

private def blockPasses (vector : BlockVector) : Bool :=
  combine vector.executionGas vector.stateGas == vector.expected

theorem all_pass :
    initializationVectors.all initializationPasses = true ∧
      blockVectors.all blockPasses = true := by
  native_decide

theorem intrinsic_state_and_cap_mutations_are_detected :
    initializationVectors.all droppedIntrinsicStatePasses = false ∧
      initializationVectors.all stateGasUsedZeroedPasses = false ∧
      initializationVectors.all capAlsoSubtractsStatePasses = false ∧
      initializationVectors.all reservoirZeroedPasses = false := by
  native_decide

theorem initialization_vector_count : initializationVectors.length = 19 := rfl

theorem block_vector_count : blockVectors.length = 6 := rfl

end Eip803x.TransactionGasInitializationVector
