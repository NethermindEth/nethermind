-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Generated.TransactionSettlementKernel
import Eip803x.TransactionSettlement

namespace Eip803x.TransactionSettlementVectors

structure Vector where
  name : String
  input : Eip803x.TransactionSettlement.Input
  expected : Eip803x.TransactionSettlement.Result
  deriving Repr

private def input
    (transactionGasLimit preRefundGas : Nat)
    (refundCounter destroyCount : Int)
    (destroyRefund codeInsertExecutionRefund calldataFloorGas : Nat)
    (stateGasUsed : Int)
    (refundQuotient : Nat := 5)
    (isError : Bool := false)
    (shouldRevert : Bool := false)
    (isEip8037Enabled : Bool := true)
    (isEip7778Enabled : Bool := true) : Eip803x.TransactionSettlement.Input :=
  { transactionGasLimit, preRefundGas, refundCounter, destroyCount, destroyRefund
    codeInsertExecutionRefund, calldataFloorGas, stateGasUsed, refundQuotient
    isError, shouldRevert, isEip8037Enabled, isEip7778Enabled }

private def result
    (spentGas operationGas blockGas blockStateGas maxUsedGas gasRefund : Nat) :
    Eip803x.TransactionSettlement.Result :=
  { spentGas, operationGas, blockGas, blockStateGas, maxUsedGas, gasRefund }

def boundaryVectors : List Vector :=
  [ { name := "positive refund"
      input := input 120 100 7 0 0 3 0 0
      expected := result 90 90 100 0 100 10 }
  , { name := "refund cap"
      input := input 120 100 30 0 0 0 0 0
      expected := result 80 80 100 0 100 20 }
  , { name := "destroy refund"
      input := input 120 100 0 2 7 1 0 0
      expected := result 85 85 100 0 100 15 }
  , { name := "zero refund"
      input := input 120 100 0 0 0 0 0 0
      expected := result 100 100 100 0 100 0 }
  , { name := "negative refund"
      input := input 120 100 (-7) 0 0 0 0 0
      expected := result 107 107 100 0 100 0 }
  , { name := "revert ignores journal refunds"
      input := input 120 100 1000 10 50 10 0 99 5 false true false true
      expected := result 90 90 100 0 100 10 }
  , { name := "legacy error selects transaction limit"
      input := input 120 100 1000 10 50 10 0 99 5 true false false true
      expected := result 110 110 120 0 120 10 }
  , { name := "calldata floor dominance"
      input := input 150 100 20 0 0 0 90 20
      expected := result 90 80 90 20 100 20 }
  , { name := "state dimension dominance"
      input := input 150 100 20 0 0 0 0 90
      expected := result 80 80 10 90 100 20 }
  , { name := "state subtraction saturation"
      input := input 150 100 20 0 0 0 0 150
      expected := result 80 80 0 150 100 20 }
  , { name := "pre-7778 block projection"
      input := input 150 100 20 0 0 0 110 0 5 false false false false
      expected := result 110 80 0 0 110 20 }
  , { name := "7778 block projection"
      input := input 150 100 20 0 0 0 110 0 5 false false false true
      expected := result 110 80 110 0 110 20 }
  , { name := "uint64 and int64 maxima"
      input := input 18446744073709551615 18446744073709551615 9223372036854775807 0 0 0 0 9223372036854775807 2
      expected := result 9223372036854775808 9223372036854775808 9223372036854775808 9223372036854775807 18446744073709551615 9223372036854775807 }
  , { name := "unsigned code refund converts to minus one"
      input := input 18446744073709551615 18446744073709551615 0 0 0 18446744073709551615 0 0
      expected := result 0 0 18446744073709551615 0 18446744073709551615 0 }
  , { name := "int64 minimum negation"
      input := input 18446744073709551615 0 (-9223372036854775808) 0 0 0 0 0
      expected := result 9223372036854775808 9223372036854775808 0 0 0 0 }
  , { name := "int32 maximum destroy count"
      input := input 120 100 0 2147483647 1 0 0 0
      expected := result 80 80 100 0 100 20 }
  , { name := "int32 minimum destroy count"
      input := input 3000000000 100 0 (-2147483648) 1 0 0 0
      expected := result 2147483748 2147483748 100 0 100 0 } ]

private def extracted (input : Eip803x.TransactionSettlement.Input) :
    Eip803x.Generated.TransactionSettlementKernel.TransactionSettlementResult :=
  Eip803x.Generated.TransactionSettlementKernel.calculate
    input.transactionGasLimit input.preRefundGas input.refundCounter input.destroyCount
    input.destroyRefund input.codeInsertExecutionRefund input.calldataFloorGas input.stateGasUsed
    input.refundQuotient input.isError input.shouldRevert input.isEip8037Enabled input.isEip7778Enabled

private def passes (vector : Vector) : Bool :=
  let actual := extracted vector.input
  let expected := vector.expected
  actual.spentGas == expected.spentGas &&
  actual.operationGas == expected.operationGas &&
  actual.blockGas == expected.blockGas &&
  actual.blockStateGas == expected.blockStateGas &&
  actual.maxUsedGas == expected.maxUsedGas &&
  actual.gasRefund == expected.gasRefund &&
  Eip803x.TransactionSettlement.settle vector.input == expected

private def isValid (vector : Vector) : Bool := decide vector.input.Valid

theorem all_boundary_vectors_pass : boundaryVectors.all passes = true := by
  decide

theorem all_boundary_vectors_are_in_the_CSharp_calling_domain :
    boundaryVectors.all isValid = true := by
  decide

theorem boundary_vector_count : boundaryVectors.length = 17 := rfl

end Eip803x.TransactionSettlementVectors
