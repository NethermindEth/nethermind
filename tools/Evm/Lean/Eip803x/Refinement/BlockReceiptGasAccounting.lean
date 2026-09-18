-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.BlockReceiptGas
import Eip803x.Generated.BlockReceiptGasAccountingKernel
import Eip803x.Refinement.TransactionGasInitialization
import Lean.Elab.Tactic.Omega

namespace Eip803x.Refinement.BlockReceiptGasAccounting

open Eip803x

/-- The natural-number range represented by a production `ulong`. -/
def FitsUInt64 (value : Nat) : Prop :=
  value ≤ Eip803x.Generated.TransactionGasInitializationKernel.uint64Max

/-- The fixed-width inputs for the restore boundary. -/
def FromTotalsValid (totals : Eip803x.BlockReceiptGas.Totals) : Prop :=
  FitsUInt64 totals.executionGas ∧
  FitsUInt64 totals.stateGas ∧
  FitsUInt64 totals.receiptGas

/-- The fixed-width and no-wrap obligations for a cumulative receipt update. -/
def AccumulateValid
    (previous : Eip803x.BlockReceiptGas.Totals)
    (transaction : Eip803x.BlockReceiptGas.Delta) : Prop :=
  FromTotalsValid previous ∧
  FitsUInt64 transaction.executionGas ∧
  FitsUInt64 transaction.stateGas ∧
  FitsUInt64 transaction.paidGas ∧
  previous.executionGas + transaction.executionGas ≤
    Eip803x.Generated.TransactionGasInitializationKernel.uint64Max ∧
  previous.stateGas + transaction.stateGas ≤
    Eip803x.Generated.TransactionGasInitializationKernel.uint64Max ∧
  previous.receiptGas + transaction.paidGas ≤
    Eip803x.Generated.TransactionGasInitializationKernel.uint64Max

/-- Reinterprets the production value result as the handwritten receipt-accounting result. -/
def toSpecResult
    (value : Eip803x.Generated.BlockReceiptGasAccountingKernel.Result) :
    Eip803x.BlockReceiptGas.Result :=
  { totals :=
      { executionGas := value.cumulativeExecutionGas
        stateGas := value.cumulativeStateGas
        receiptGas := value.cumulativeReceiptGas }
    headerGasUsed := value.headerGasUsed }

private theorem normalizeUInt64_of_fits {value : Nat} (h : FitsUInt64 value) :
    Eip803x.Generated.BlockReceiptGasAccountingKernel.normalizeUInt64 value = value := by
  unfold Eip803x.Generated.BlockReceiptGasAccountingKernel.normalizeUInt64
    Eip803x.Generated.TransactionGasInitializationKernel.normalizeUInt64 FitsUInt64 at *
  simp [h]

private theorem combineBlockGas_eq_header
    {executionGas stateGas : Nat}
    (hExecution : FitsUInt64 executionGas)
    (hState : FitsUInt64 stateGas) :
    Eip803x.Generated.BlockReceiptGasAccountingKernel.combineBlockGas executionGas stateGas =
      max executionGas stateGas := by
  unfold Eip803x.Generated.BlockReceiptGasAccountingKernel.combineBlockGas
  simpa [Eip803x.TransactionGas.headerGasUsed] using
    Eip803x.Refinement.TransactionGasInitialization.generatedCombine_eq_headerGasUsed_of_machine_inputs
      hExecution hState

private theorem wrapUInt64_of_nonnegative_fits {value : Int}
    (hNonnegative : 0 ≤ value)
    (hFits : value ≤ (Eip803x.Generated.TransactionGasInitializationKernel.uint64Max : Int)) :
    Eip803x.Generated.TransactionGasInitializationKernel.wrapUInt64 value = Int.toNat value := by
  unfold Eip803x.Generated.TransactionGasInitializationKernel.wrapUInt64
  simp [hNonnegative, hFits]

private theorem addUInt64_of_fits {left right : Nat}
    (hFits : left + right ≤ Eip803x.Generated.TransactionGasInitializationKernel.uint64Max) :
    Eip803x.Generated.BlockReceiptGasAccountingKernel.addUInt64 left right = left + right := by
  unfold Eip803x.Generated.BlockReceiptGasAccountingKernel.addUInt64
    Eip803x.Generated.TransactionGasInitializationKernel.addUInt64
  have hNonnegative : (0 : Int) ≤ (left : Int) + right := by omega
  have hBounded : (left : Int) + right ≤
      (Eip803x.Generated.TransactionGasInitializationKernel.uint64Max : Int) := by
    omega
  rw [wrapUInt64_of_nonnegative_fits hNonnegative hBounded]
  apply Int.ofNat_inj.mp
  rw [Int.toNat_of_nonneg hNonnegative]
  norm_cast

/-- The extracted restore boundary refines the handwritten cumulative-counter projection. -/
theorem generatedFromTotals_refines_spec
    (totals : Eip803x.BlockReceiptGas.Totals)
    (h : FromTotalsValid totals) :
    toSpecResult
      (Eip803x.Generated.BlockReceiptGasAccountingKernel.fromTotals
        totals.executionGas totals.stateGas totals.receiptGas) =
      Eip803x.BlockReceiptGas.fromTotals totals := by
  rcases h with ⟨hExecution, hState, hReceipt⟩
  unfold Eip803x.Generated.BlockReceiptGasAccountingKernel.fromTotals
  rw [normalizeUInt64_of_fits hExecution, normalizeUInt64_of_fits hState,
    normalizeUInt64_of_fits hReceipt]
  unfold Eip803x.Generated.BlockReceiptGasAccountingKernel.fromTotalsNormalized
    toSpecResult Eip803x.BlockReceiptGas.fromTotals
  rw [combineBlockGas_eq_header hExecution hState]

private theorem generatedFromTotalsNormalized_refines_spec
    (totals : Eip803x.BlockReceiptGas.Totals)
    (h : FromTotalsValid totals) :
    toSpecResult
      (Eip803x.Generated.BlockReceiptGasAccountingKernel.fromTotalsNormalized
        totals.executionGas totals.stateGas totals.receiptGas) =
      Eip803x.BlockReceiptGas.fromTotals totals := by
  rcases h with ⟨hExecution, hState, _hReceipt⟩
  unfold Eip803x.Generated.BlockReceiptGasAccountingKernel.fromTotalsNormalized
    toSpecResult Eip803x.BlockReceiptGas.fromTotals
  rw [combineBlockGas_eq_header hExecution hState]

/-- The extracted cumulative update refines the natural-number receipt fold when no counter wraps. -/
theorem generatedAccumulate_refines_spec
    (previous : Eip803x.BlockReceiptGas.Totals)
    (transaction : Eip803x.BlockReceiptGas.Delta)
    (h : AccumulateValid previous transaction) :
    toSpecResult
      (Eip803x.Generated.BlockReceiptGasAccountingKernel.accumulate
        previous.executionGas previous.stateGas previous.receiptGas
        transaction.executionGas transaction.stateGas transaction.paidGas) =
      Eip803x.BlockReceiptGas.accumulate previous transaction := by
  rcases h with ⟨hPrevious, hTransactionExecution, hTransactionState, hTransactionReceipt,
    hExecutionSum, hStateSum, hReceiptSum⟩
  rcases hPrevious with ⟨hPreviousExecution, hPreviousState, hPreviousReceipt⟩
  unfold Eip803x.Generated.BlockReceiptGasAccountingKernel.accumulate
  rw [normalizeUInt64_of_fits hPreviousExecution, normalizeUInt64_of_fits hPreviousState,
    normalizeUInt64_of_fits hPreviousReceipt, normalizeUInt64_of_fits hTransactionExecution,
    normalizeUInt64_of_fits hTransactionState, normalizeUInt64_of_fits hTransactionReceipt]
  unfold Eip803x.Generated.BlockReceiptGasAccountingKernel.accumulateNormalized
  rw [addUInt64_of_fits hExecutionSum, addUInt64_of_fits hStateSum,
    addUInt64_of_fits hReceiptSum]
  simpa [Eip803x.BlockReceiptGas.accumulate] using
    generatedFromTotalsNormalized_refines_spec
      { executionGas := previous.executionGas + transaction.executionGas
        stateGas := previous.stateGas + transaction.stateGas
        receiptGas := previous.receiptGas + transaction.paidGas }
      ⟨hExecutionSum, hStateSum, hReceiptSum⟩

/-- The source-level fixed-width behavior retained outside the no-wrap EIP refinement theorem. -/
def wrappingAccumulate
    (previous : Eip803x.BlockReceiptGas.Totals)
    (transaction : Eip803x.BlockReceiptGas.Delta) :
    Eip803x.Generated.BlockReceiptGasAccountingKernel.Result :=
  Eip803x.Generated.BlockReceiptGasAccountingKernel.fromTotalsNormalized
    (Eip803x.Generated.BlockReceiptGasAccountingKernel.addUInt64
      (Eip803x.Generated.BlockReceiptGasAccountingKernel.normalizeUInt64 previous.executionGas)
      (Eip803x.Generated.BlockReceiptGasAccountingKernel.normalizeUInt64 transaction.executionGas))
    (Eip803x.Generated.BlockReceiptGasAccountingKernel.addUInt64
      (Eip803x.Generated.BlockReceiptGasAccountingKernel.normalizeUInt64 previous.stateGas)
      (Eip803x.Generated.BlockReceiptGasAccountingKernel.normalizeUInt64 transaction.stateGas))
    (Eip803x.Generated.BlockReceiptGasAccountingKernel.addUInt64
      (Eip803x.Generated.BlockReceiptGasAccountingKernel.normalizeUInt64 previous.receiptGas)
      (Eip803x.Generated.BlockReceiptGasAccountingKernel.normalizeUInt64 transaction.paidGas))

/-- This exact transcription preserves the source's unchecked UInt64 wrap behavior. -/
theorem generatedAccumulate_eq_wrapping_transcription
    (previous : Eip803x.BlockReceiptGas.Totals)
    (transaction : Eip803x.BlockReceiptGas.Delta) :
    Eip803x.Generated.BlockReceiptGasAccountingKernel.accumulate
      previous.executionGas previous.stateGas previous.receiptGas
      transaction.executionGas transaction.stateGas transaction.paidGas =
      wrappingAccumulate previous transaction := rfl

theorem wrappingAccumulate_execution_projection
    (previous : Eip803x.BlockReceiptGas.Totals)
    (transaction : Eip803x.BlockReceiptGas.Delta) :
    (wrappingAccumulate previous transaction).cumulativeExecutionGas =
      Eip803x.Generated.BlockReceiptGasAccountingKernel.addUInt64
        (Eip803x.Generated.BlockReceiptGasAccountingKernel.normalizeUInt64 previous.executionGas)
        (Eip803x.Generated.BlockReceiptGasAccountingKernel.normalizeUInt64 transaction.executionGas) := rfl

theorem wrappingAccumulate_state_projection
    (previous : Eip803x.BlockReceiptGas.Totals)
    (transaction : Eip803x.BlockReceiptGas.Delta) :
    (wrappingAccumulate previous transaction).cumulativeStateGas =
      Eip803x.Generated.BlockReceiptGasAccountingKernel.addUInt64
        (Eip803x.Generated.BlockReceiptGasAccountingKernel.normalizeUInt64 previous.stateGas)
        (Eip803x.Generated.BlockReceiptGasAccountingKernel.normalizeUInt64 transaction.stateGas) := rfl

theorem wrappingAccumulate_receipt_projection
    (previous : Eip803x.BlockReceiptGas.Totals)
    (transaction : Eip803x.BlockReceiptGas.Delta) :
    (wrappingAccumulate previous transaction).cumulativeReceiptGas =
      Eip803x.Generated.BlockReceiptGasAccountingKernel.addUInt64
        (Eip803x.Generated.BlockReceiptGasAccountingKernel.normalizeUInt64 previous.receiptGas)
        (Eip803x.Generated.BlockReceiptGasAccountingKernel.normalizeUInt64 transaction.paidGas) := rfl

end Eip803x.Refinement.BlockReceiptGasAccounting
