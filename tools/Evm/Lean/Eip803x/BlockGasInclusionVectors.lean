-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Refinement.BlockGasInclusion

namespace Eip803x.BlockGasInclusionVector

open Eip803x.Generated.Eip8037BlockGasInclusionCheck
open Eip803x.Refinement.BlockGasInclusion

structure InclusionVector where
  name : String
  blockGasLimit : Nat
  cumulativeBlockExecution : Nat
  cumulativeBlockState : Nat
  txGas : Nat
  expected : Outcome
  deriving DecidableEq, Repr

structure ExecutionGasVector where
  name : String
  preRefundGas : Nat
  blockStateGas : Nat
  calldataFloor : Nat
  expected : Nat
  deriving DecidableEq, Repr

def inclusionVectors : List InclusionVector :=
  [ { name := "empty-block-simple-call"
      blockGasLimit := 30_000_000
      cumulativeBlockExecution := 0
      cumulativeBlockState := 0
      txGas := 21_000
      expected := .ok }
  , { name := "execution-exact-fit"
      blockGasLimit := 16_777_216
      cumulativeBlockExecution := 0
      cumulativeBlockState := 0
      txGas := 16_777_216
      expected := .ok }
  , { name := "execution-one-over"
      blockGasLimit := 16_777_215
      cumulativeBlockExecution := 0
      cumulativeBlockState := 0
      txGas := 16_777_216
      expected := .executionDimensionExceeded }
  , { name := "state-exact-fit-after-prior-state"
      blockGasLimit := 16_777_316
      cumulativeBlockExecution := 0
      cumulativeBlockState := 100
      txGas := 16_777_216
      expected := .ok }
  , { name := "state-one-over-after-prior-state"
      blockGasLimit := 16_777_316
      cumulativeBlockExecution := 0
      cumulativeBlockState := 100
      txGas := 16_777_217
      expected := .stateDimensionExceeded }
  , { name := "cumulative-execution-already-over"
      blockGasLimit := 100
      cumulativeBlockExecution := 101
      cumulativeBlockState := 0
      txGas := 0
      expected := .executionDimensionExceeded }
  , { name := "cumulative-state-already-over"
      blockGasLimit := 100
      cumulativeBlockExecution := 0
      cumulativeBlockState := 101
      txGas := 0
      expected := .stateDimensionExceeded }
  , { name := "execution-cap-does-not-cap-state-reservation"
      blockGasLimit := 16_777_316
      cumulativeBlockExecution := 0
      cumulativeBlockState := 0
      txGas := 167_772_160
      expected := .stateDimensionExceeded } ]

def executionGasVectors : List ExecutionGasVector :=
  [ { name := "subtract-state-component"
      preRefundGas := 379_970
      blockStateGas := 281_520
      calldataFloor := 0
      expected := 98_450 }
  , { name := "state-dominates-saturates-to-zero"
      preRefundGas := 12_625
      blockStateGas := 1_566_720
      calldataFloor := 0
      expected := 0 }
  , { name := "calldata-floor-applies-after-subtraction"
      preRefundGas := 100_000
      blockStateGas := 60_000
      calldataFloor := 50_000
      expected := 50_000 }
  , { name := "equal-state-gas-uses-floor"
      preRefundGas := 100
      blockStateGas := 100
      calldataFloor := 1
      expected := 1 }
  , { name := "uint64-boundary-subtraction"
      preRefundGas := 18_446_744_073_709_551_615
      blockStateGas := 1
      calldataFloor := 0
      expected := 18_446_744_073_709_551_614 } ]

private def inclusionPasses (vector : InclusionVector) : Bool :=
  decide (
    validate vector.blockGasLimit vector.cumulativeBlockExecution vector.cumulativeBlockState vector.txGas = vector.expected ∧
    inclusionFormula vector.blockGasLimit vector.cumulativeBlockExecution vector.cumulativeBlockState vector.txGas = vector.expected)

private def executionGasPasses (vector : ExecutionGasVector) : Bool :=
  decide (
    calculateBlockExecutionGas vector.preRefundGas vector.blockStateGas vector.calldataFloor = vector.expected ∧
    executionGasFormula vector.preRefundGas vector.blockStateGas vector.calldataFloor = vector.expected)

theorem all_pass :
    inclusionVectors.all inclusionPasses = true ∧
      executionGasVectors.all executionGasPasses = true := by
  native_decide

theorem inclusion_vector_count : inclusionVectors.length = 8 := rfl

theorem execution_gas_vector_count : executionGasVectors.length = 5 := rfl

end Eip803x.BlockGasInclusionVector
