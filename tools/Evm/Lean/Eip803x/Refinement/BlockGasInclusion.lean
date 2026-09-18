-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Generated.Eip8037BlockGasInclusionCheck
import Eip803x.TransactionGas
import Lean.Elab.Tactic.Omega

namespace Eip803x.Refinement.BlockGasInclusion

open Eip803x
open Eip803x.Generated.Eip8037BlockGasInclusionCheck

/-- The natural-number range represented by a production `ulong`. -/
def FitsUInt64 (value : Nat) : Prop :=
  value ≤ uint64Max

/-- EIP-8037 reserves the capped full transaction gas in the execution dimension. -/
def executionReservation (txGas : Nat) : Nat :=
  min txMaxGasLimit txGas

/-- EIP-8037 reserves the uncapped full transaction gas in the state dimension. -/
def stateReservation (txGas : Nat) : Nat :=
  txGas

/-- The pinned two-dimensional EIP-8037 transaction-inclusion formula. -/
def inclusionFormula
    (blockGasLimit cumulativeBlockExecution cumulativeBlockState txGas : Nat) : Outcome :=
  if cumulativeBlockExecution > blockGasLimit then
    .executionDimensionExceeded
  else if cumulativeBlockState > blockGasLimit then
    .stateDimensionExceeded
  else if executionReservation txGas > blockGasLimit - cumulativeBlockExecution then
    .executionDimensionExceeded
  else if stateReservation txGas > blockGasLimit - cumulativeBlockState then
    .stateDimensionExceeded
  else
    .ok

/-- The pinned EIP-7976 floor formula used for EIP-8037 block execution gas. -/
def executionGasFormula (preRefundGas blockStateGas calldataFloor : Nat) : Nat :=
  max (preRefundGas - blockStateGas) calldataFloor

/--
The zero-intrinsic projection exposes the transaction-gas reference quantities
used by the inclusion reservation. It is algebraic only: a state reservation
may deliberately exceed the per-transaction execution cap.
-/
def reservationInput (txGas : Nat) : TransactionGas.InitializationInput :=
  { txGas
    intrinsicGas := 0
    txMaxGasLimit }

/-- A transaction-gas settlement projection with the observed pre-refund gas. -/
def executionSettlementInput
    (preRefundGas blockStateGas calldataFloor : Nat) : TransactionGas.SettlementInput :=
  { txGas := preRefundGas
    gasLeft := 0
    stateGasReservoir := 0
    refundCounter := 0
    evmStateGasUsed := blockStateGas
    calldataFloorGasCost := calldataFloor }

private theorem normalizeUInt64_of_fits {value : Nat} (h : FitsUInt64 value) :
    normalizeUInt64 value = value := by
  unfold normalizeUInt64 FitsUInt64 at *
  simp [h]

private theorem subUInt64_of_order {left right : Nat} (h : right ≤ left) :
    subUInt64 left right = left - right := by
  unfold subUInt64
  simp [h]

private theorem validateNormalized_eq_inclusionFormula
    (blockGasLimit cumulativeBlockExecution cumulativeBlockState txGas : Nat) :
    validateNormalized blockGasLimit cumulativeBlockExecution cumulativeBlockState txGas =
      inclusionFormula blockGasLimit cumulativeBlockExecution cumulativeBlockState txGas := by
  unfold validateNormalized inclusionFormula executionReservation stateReservation
  by_cases hExecutionExceeded : cumulativeBlockExecution > blockGasLimit
  · simp [hExecutionExceeded]
  · have hExecutionAvailable : cumulativeBlockExecution ≤ blockGasLimit := Nat.le_of_not_gt hExecutionExceeded
    by_cases hStateExceeded : cumulativeBlockState > blockGasLimit
    · simp [hExecutionExceeded, hStateExceeded]
    · have hStateAvailable : cumulativeBlockState ≤ blockGasLimit := Nat.le_of_not_gt hStateExceeded
      rw [subUInt64_of_order hExecutionAvailable, subUInt64_of_order hStateAvailable]

/-- The generated `Validate` agrees with the pinned inclusion formula on machine inputs. -/
theorem generatedValidate_refines_inclusionFormula
    {blockGasLimit cumulativeBlockExecution cumulativeBlockState txGas : Nat}
    (hBlockGasLimit : FitsUInt64 blockGasLimit)
    (hCumulativeExecution : FitsUInt64 cumulativeBlockExecution)
    (hCumulativeState : FitsUInt64 cumulativeBlockState)
    (hTxGas : FitsUInt64 txGas) :
    validate blockGasLimit cumulativeBlockExecution cumulativeBlockState txGas =
      inclusionFormula blockGasLimit cumulativeBlockExecution cumulativeBlockState txGas := by
  unfold validate
  rw [normalizeUInt64_of_fits hBlockGasLimit,
    normalizeUInt64_of_fits hCumulativeExecution,
    normalizeUInt64_of_fits hCumulativeState,
    normalizeUInt64_of_fits hTxGas]
  exact validateNormalized_eq_inclusionFormula _ _ _ _

/-- The execution reservation is the zero-intrinsic `TransactionGas` gas-left formula. -/
theorem executionReservation_eq_transactionGasLeft (txGas : Nat) :
    executionReservation txGas =
      (TransactionGas.initializeTransactionGas (reservationInput txGas)).gasLeft := by
  rfl

/-- The state reservation remains the full transaction gas, not a cap-reduced amount. -/
theorem stateReservation_eq_transactionEvmGas (txGas : Nat) :
    stateReservation txGas =
      (TransactionGas.initializeTransactionGas (reservationInput txGas)).evmGas := by
  rfl

private theorem saturatingSubUInt64_eq_sub
    (preRefundGas blockStateGas : Nat) :
    saturatingSubUInt64 preRefundGas blockStateGas = preRefundGas - blockStateGas := by
  unfold saturatingSubUInt64
  by_cases hGreater : preRefundGas > blockStateGas
  · rw [if_pos hGreater, subUInt64_of_order (Nat.le_of_lt hGreater)]
  · rw [if_neg hGreater, Nat.sub_eq_zero_of_le (Nat.le_of_not_gt hGreater)]

/-- The generated execution-gas helper agrees with saturating subtraction followed by the calldata floor. -/
theorem generatedCalculateBlockExecutionGas_refines_executionGasFormula
    {preRefundGas blockStateGas calldataFloor : Nat}
    (hPreRefundGas : FitsUInt64 preRefundGas)
    (hBlockStateGas : FitsUInt64 blockStateGas)
    (hCalldataFloor : FitsUInt64 calldataFloor) :
    calculateBlockExecutionGas preRefundGas blockStateGas calldataFloor =
      executionGasFormula preRefundGas blockStateGas calldataFloor := by
  unfold calculateBlockExecutionGas calculateBlockExecutionGasNormalized executionGasFormula
  rw [normalizeUInt64_of_fits hPreRefundGas,
    normalizeUInt64_of_fits hBlockStateGas,
    normalizeUInt64_of_fits hCalldataFloor,
    saturatingSubUInt64_eq_sub preRefundGas blockStateGas]

private theorem executionSettlementInput_valid
    {preRefundGas blockStateGas calldataFloor : Nat}
    (hState : blockStateGas ≤ preRefundGas)
    (hFloor : calldataFloor ≤ preRefundGas) :
    (executionSettlementInput preRefundGas blockStateGas calldataFloor).Valid := by
  unfold TransactionGas.SettlementInput.Valid
  constructor
  · simp [executionSettlementInput]
  constructor
  · simpa [executionSettlementInput, TransactionGas.txGasUsedBeforeRefund] using hState
  · simpa [executionSettlementInput] using hFloor

/--
On the transaction processor's non-overflow domain, the extracted helper is
the `TransactionGas.executionGas` formula and its settlement projection is valid.
-/
theorem generatedCalculateBlockExecutionGas_refines_transactionGasExecution
    {preRefundGas blockStateGas calldataFloor : Nat}
    (hPreRefundGas : FitsUInt64 preRefundGas)
    (hBlockStateGas : FitsUInt64 blockStateGas)
    (hCalldataFloor : FitsUInt64 calldataFloor)
    (hState : blockStateGas ≤ preRefundGas)
    (hFloor : calldataFloor ≤ preRefundGas) :
    let input := executionSettlementInput preRefundGas blockStateGas calldataFloor
    input.Valid ∧
      calculateBlockExecutionGas preRefundGas blockStateGas calldataFloor =
        TransactionGas.executionGas input := by
  dsimp
  constructor
  · exact executionSettlementInput_valid hState hFloor
  · rw [generatedCalculateBlockExecutionGas_refines_executionGasFormula
      hPreRefundGas hBlockStateGas hCalldataFloor]
    rfl

end Eip803x.Refinement.BlockGasInclusion
