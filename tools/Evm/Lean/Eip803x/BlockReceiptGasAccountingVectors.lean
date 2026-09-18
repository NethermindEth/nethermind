-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Refinement.BlockReceiptGasAccounting

namespace Eip803x.BlockReceiptGasAccountingVector

open Eip803x.Generated.BlockReceiptGasAccountingKernel

structure AccumulateVector where
  name : String
  previousExecutionGas : Nat
  previousStateGas : Nat
  previousReceiptGas : Nat
  transactionExecutionGas : Nat
  transactionStateGas : Nat
  transactionPaidGas : Nat
  expected : Result
  deriving DecidableEq, Repr

structure FromTotalsVector where
  name : String
  cumulativeExecutionGas : Nat
  cumulativeStateGas : Nat
  cumulativeReceiptGas : Nat
  expected : Result
  deriving DecidableEq, Repr

private def result
    (cumulativeExecutionGas cumulativeStateGas cumulativeReceiptGas headerGasUsed : Nat) : Result :=
  { cumulativeExecutionGas, cumulativeStateGas, cumulativeReceiptGas, headerGasUsed }

def accumulateVectors : List AccumulateVector :=
  [ { name := "zero"
      previousExecutionGas := 0
      previousStateGas := 0
      previousReceiptGas := 0
      transactionExecutionGas := 0
      transactionStateGas := 0
      transactionPaidGas := 0
      expected := result 0 0 0 0 }
  , { name := "state-dimension-controls-header"
      previousExecutionGas := 10
      previousStateGas := 20
      previousReceiptGas := 30
      transactionExecutionGas := 4
      transactionStateGas := 5
      transactionPaidGas := 6
      expected := result 14 25 36 25 }
  , { name := "execution-dimension-controls-header"
      previousExecutionGas := 20
      previousStateGas := 10
      previousReceiptGas := 30
      transactionExecutionGas := 5
      transactionStateGas := 4
      transactionPaidGas := 6
      expected := result 25 14 36 25 }
  , { name := "equal-dimensions"
      previousExecutionGas := 10
      previousStateGas := 10
      previousReceiptGas := 30
      transactionExecutionGas := 5
      transactionStateGas := 5
      transactionPaidGas := 6
      expected := result 15 15 36 15 }
  , { name := "maximum-uint64-exact-addition"
      previousExecutionGas := 18_446_744_073_709_551_614
      previousStateGas := 0
      previousReceiptGas := 0
      transactionExecutionGas := 1
      transactionStateGas := 0
      transactionPaidGas := 1
      expected := result 18_446_744_073_709_551_615 0 1 18_446_744_073_709_551_615 }
  , { name := "unchecked-wrap-boundary"
      previousExecutionGas := 18_446_744_073_709_551_615
      previousStateGas := 18_446_744_073_709_551_615
      previousReceiptGas := 18_446_744_073_709_551_615
      transactionExecutionGas := 1
      transactionStateGas := 2
      transactionPaidGas := 3
      expected := result 0 1 2 1 } ]

def fromTotalsVectors : List FromTotalsVector :=
  [ { name := "restore-state-dimension-controls-header"
      cumulativeExecutionGas := 9
      cumulativeStateGas := 12
      cumulativeReceiptGas := 7
      expected := result 9 12 7 12 }
  , { name := "restore-maximum-execution"
      cumulativeExecutionGas := 18_446_744_073_709_551_615
      cumulativeStateGas := 0
      cumulativeReceiptGas := 18_446_744_073_709_551_615
      expected := result 18_446_744_073_709_551_615 0 18_446_744_073_709_551_615 18_446_744_073_709_551_615 } ]

private def accumulatePasses (vector : AccumulateVector) : Bool :=
  accumulate
    vector.previousExecutionGas vector.previousStateGas vector.previousReceiptGas
    vector.transactionExecutionGas vector.transactionStateGas vector.transactionPaidGas == vector.expected

private def fromTotalsPasses (vector : FromTotalsVector) : Bool :=
  fromTotals vector.cumulativeExecutionGas vector.cumulativeStateGas vector.cumulativeReceiptGas == vector.expected

theorem all_vectors_pass :
    accumulateVectors.all accumulatePasses = true ∧
      fromTotalsVectors.all fromTotalsPasses = true := by
  native_decide

theorem accumulate_vector_count : accumulateVectors.length = 6 := rfl

theorem from_totals_vector_count : fromTotalsVectors.length = 2 := rfl

end Eip803x.BlockReceiptGasAccountingVector
