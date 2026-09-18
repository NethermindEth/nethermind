-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Generated.TransactionSettlementKernel
import Eip803x.TransactionSettlement
import Eip803x.TransactionGas
import Lean.Elab.Tactic.Omega

namespace Eip803x.Refinement.TransactionSettlement

/-- The extracted production calculation applied to the handwritten scalar input. -/
def extracted (input : Eip803x.TransactionSettlement.Input) :
    Eip803x.Generated.TransactionSettlementKernel.TransactionSettlementResult :=
  Eip803x.Generated.TransactionSettlementKernel.calculate
    input.transactionGasLimit input.preRefundGas input.refundCounter input.destroyCount
    input.destroyRefund input.codeInsertExecutionRefund input.calldataFloorGas input.stateGasUsed
    input.refundQuotient input.isError input.shouldRevert input.isEip8037Enabled input.isEip7778Enabled

private theorem normalizeUInt64_eq :
    Eip803x.Generated.TransactionSettlementKernel.normalizeUInt64 =
      Eip803x.TransactionSettlement.normalizeUInt64 := by rfl

private theorem wrapInt64_eq :
    Eip803x.Generated.TransactionSettlementKernel.wrapInt64 =
      Eip803x.TransactionSettlement.wrapInt64 := by rfl

private theorem wrapInt32_eq :
    Eip803x.Generated.TransactionSettlementKernel.wrapInt32 =
      Eip803x.TransactionSettlement.wrapInt32 := by rfl

private theorem uint64ToInt64_eq :
    Eip803x.Generated.TransactionSettlementKernel.uint64ToInt64 =
      Eip803x.TransactionSettlement.uint64ToInt64 := by rfl

private theorem int64ToUInt64_eq :
    Eip803x.Generated.TransactionSettlementKernel.int64ToUInt64 =
      Eip803x.TransactionSettlement.int64ToUInt64 := by rfl

private theorem addInt64_eq :
    Eip803x.Generated.TransactionSettlementKernel.addInt64 =
      Eip803x.TransactionSettlement.addInt64 := by rfl

private theorem mulInt64_eq :
    Eip803x.Generated.TransactionSettlementKernel.mulInt64 =
      Eip803x.TransactionSettlement.mulInt64 := by rfl

private theorem negInt64_eq :
    Eip803x.Generated.TransactionSettlementKernel.negInt64 =
      Eip803x.TransactionSettlement.negInt64 := by rfl

private theorem addUInt64_eq :
    Eip803x.Generated.TransactionSettlementKernel.addUInt64 =
      Eip803x.TransactionSettlement.addUInt64 := by rfl

private theorem subUInt64_eq :
    Eip803x.Generated.TransactionSettlementKernel.subUInt64 =
      Eip803x.TransactionSettlement.subUInt64 := by rfl

private theorem saturatingSubUInt64_eq :
    Eip803x.Generated.TransactionSettlementKernel.saturatingSubUInt64 =
      Eip803x.TransactionSettlement.saturatingSubUInt64 := by rfl

private theorem extracted_fields_match
    (input : Eip803x.TransactionSettlement.Input) (_h : input.Valid) :
    (extracted input).spentGas = (Eip803x.TransactionSettlement.settle input).spentGas ∧
    (extracted input).operationGas = (Eip803x.TransactionSettlement.settle input).operationGas ∧
    (extracted input).blockGas = (Eip803x.TransactionSettlement.settle input).blockGas ∧
    (extracted input).blockStateGas = (Eip803x.TransactionSettlement.settle input).blockStateGas ∧
    (extracted input).maxUsedGas = (Eip803x.TransactionSettlement.settle input).maxUsedGas ∧
    (extracted input).gasRefund = (Eip803x.TransactionSettlement.settle input).gasRefund := by
  simp only [extracted, Eip803x.Generated.TransactionSettlementKernel.calculate,
    Eip803x.Generated.TransactionSettlementKernel.calculateNormalized,
    Eip803x.TransactionSettlement.settle, Eip803x.TransactionSettlement.totalRefund,
    Eip803x.TransactionSettlement.applySignedRefund]
  rw [normalizeUInt64_eq, wrapInt64_eq, wrapInt32_eq, uint64ToInt64_eq,
    int64ToUInt64_eq, addInt64_eq, mulInt64_eq, negInt64_eq, addUInt64_eq,
    subUInt64_eq, saturatingSubUInt64_eq]
  simp

/-- Paid gas, after the signed refund and calldata floor, refines the handwritten model. -/
theorem spentGas_refines (input : Eip803x.TransactionSettlement.Input) (h : input.Valid) :
    (extracted input).spentGas = (Eip803x.TransactionSettlement.settle input).spentGas :=
  (extracted_fields_match input h).1

/-- Gas after signed refund application and before the calldata floor refines the model. -/
theorem operationGas_refines (input : Eip803x.TransactionSettlement.Input) (h : input.Valid) :
    (extracted input).operationGas = (Eip803x.TransactionSettlement.settle input).operationGas :=
  (extracted_fields_match input h).2.1

/-- The block execution-gas projection refines the model. -/
theorem blockGas_refines (input : Eip803x.TransactionSettlement.Input) (h : input.Valid) :
    (extracted input).blockGas = (Eip803x.TransactionSettlement.settle input).blockGas :=
  (extracted_fields_match input h).2.2.1

/-- The block state-gas projection refines the model. -/
theorem blockStateGas_refines (input : Eip803x.TransactionSettlement.Input) (h : input.Valid) :
    (extracted input).blockStateGas = (Eip803x.TransactionSettlement.settle input).blockStateGas :=
  (extracted_fields_match input h).2.2.2.1

/-- The maximum pre-refund/floor observation refines the model. -/
theorem maxUsedGas_refines (input : Eip803x.TransactionSettlement.Input) (h : input.Valid) :
    (extracted input).maxUsedGas = (Eip803x.TransactionSettlement.settle input).maxUsedGas :=
  (extracted_fields_match input h).2.2.2.2.1

/-- The positive-only refund observation refines the model. -/
theorem gasRefund_refines (input : Eip803x.TransactionSettlement.Input) (h : input.Valid) :
    (extracted input).gasRefund = (Eip803x.TransactionSettlement.settle input).gasRefund :=
  (extracted_fields_match input h).2.2.2.2.2

/--
The natural transaction-gas view of a normal production settlement. The kernel
receives only aggregate pre-refund gas, so the projection chooses a zero state
reservoir and places the remaining gas in `gasLeft`; settlement observes only
their sum.
-/
def transactionGasInput
    (input : Eip803x.TransactionSettlement.Input) (totalRefund : Int) :
    Eip803x.TransactionGas.SettlementInput :=
  { txGas := input.transactionGasLimit
    gasLeft := input.transactionGasLimit - input.preRefundGas
    stateGasReservoir := 0
    refundCounter := Int.toNat totalRefund
    evmStateGasUsed := Int.toNat input.stateGasUsed
    calldataFloorGasCost := input.calldataFloorGas }

/-- The unwrapped success-path refund assembled from all production inputs. -/
def successTotalRefund (input : Eip803x.TransactionSettlement.Input) : Int :=
  (input.codeInsertExecutionRefund : Int) +
    (input.refundCounter + input.destroyCount * (input.destroyRefund : Int))

/--
Shared range, fork, and ordering premises for the pinned normal settlement path.
They exclude input normalization, quotient division, state casts, and natural
subtraction from changing the mathematical values.
-/
def PinnedNormalDomain (input : Eip803x.TransactionSettlement.Input) : Prop :=
  input.Valid ∧
    input.refundQuotient = 5 ∧
    input.isError = false ∧
    input.isEip8037Enabled = true ∧
    input.isEip7778Enabled = true ∧
    input.preRefundGas ≤ input.transactionGasLimit ∧
    input.calldataFloorGas ≤ input.transactionGasLimit ∧
    0 ≤ input.stateGasUsed ∧
    Int.toNat input.stateGasUsed ≤ input.preRefundGas ∧
    Eip803x.TransactionSettlement.FitsInt64 ((input.preRefundGas / 5 : Nat) : Int)

/-- No signed operation wraps while assembling the success-path refund. -/
def SuccessNoWrap (input : Eip803x.TransactionSettlement.Input) : Prop :=
  0 ≤ input.refundCounter ∧
    0 ≤ input.destroyCount ∧
    Eip803x.TransactionSettlement.FitsInt64 (input.destroyRefund : Int) ∧
    Eip803x.TransactionSettlement.FitsInt64 (input.codeInsertExecutionRefund : Int) ∧
    Eip803x.TransactionSettlement.FitsInt64
      (input.destroyCount * (input.destroyRefund : Int)) ∧
    Eip803x.TransactionSettlement.FitsInt64
      (input.refundCounter + input.destroyCount * (input.destroyRefund : Int)) ∧
    Eip803x.TransactionSettlement.FitsInt64 (successTotalRefund input)

/-- No signed cast wraps for the code-insertion refund retained on revert. -/
def RevertNoWrap (input : Eip803x.TransactionSettlement.Input) : Prop :=
  Eip803x.TransactionSettlement.FitsInt64 (input.codeInsertExecutionRefund : Int)

private theorem normalizeUInt64_of_fits {value : Nat}
    (h : Eip803x.TransactionSettlement.FitsUInt64 value) :
    Eip803x.TransactionSettlement.normalizeUInt64 value = value := by
  unfold Eip803x.TransactionSettlement.normalizeUInt64
    Eip803x.TransactionSettlement.FitsUInt64 at *
  simp [h]

private theorem wrapInt64_of_fits {value : Int}
    (h : Eip803x.TransactionSettlement.FitsInt64 value) :
    Eip803x.TransactionSettlement.wrapInt64 value = value := by
  unfold Eip803x.TransactionSettlement.wrapInt64
    Eip803x.TransactionSettlement.FitsInt64 at *
  simp [h]

private theorem wrapInt32_of_fits {value : Int}
    (h : Eip803x.TransactionSettlement.FitsInt32 value) :
    Eip803x.TransactionSettlement.wrapInt32 value = value := by
  unfold Eip803x.TransactionSettlement.wrapInt32
    Eip803x.TransactionSettlement.FitsInt32 at *
  simp [h]

private theorem uint64ToInt64_of_fits {value : Nat}
    (h : Eip803x.TransactionSettlement.FitsInt64 (value : Int)) :
    Eip803x.TransactionSettlement.uint64ToInt64 value = (value : Int) := by
  unfold Eip803x.TransactionSettlement.uint64ToInt64
  exact wrapInt64_of_fits h

private theorem wrapUInt64_of_nonnegative_fits {value : Int}
    (hNonnegative : 0 ≤ value)
    (hFits : value ≤ (Eip803x.TransactionSettlement.uint64Max : Int)) :
    Eip803x.TransactionSettlement.wrapUInt64 value = Int.toNat value := by
  unfold Eip803x.TransactionSettlement.wrapUInt64
  simp [hNonnegative, hFits]

private theorem int64ToUInt64_of_nonnegative_fits {value : Int}
    (hNonnegative : 0 ≤ value)
    (hFits : Eip803x.TransactionSettlement.FitsInt64 value) :
    Eip803x.TransactionSettlement.int64ToUInt64 value = Int.toNat value := by
  unfold Eip803x.TransactionSettlement.int64ToUInt64
  rcases hFits with ⟨_hMinimum, hMaximum⟩
  have hRange : Eip803x.TransactionSettlement.int64Max ≤
      (Eip803x.TransactionSettlement.uint64Max : Int) := by decide
  exact wrapUInt64_of_nonnegative_fits hNonnegative (Int.le_trans hMaximum hRange)

private theorem addInt64_of_fits {left right : Int}
    (h : Eip803x.TransactionSettlement.FitsInt64 (left + right)) :
    Eip803x.TransactionSettlement.addInt64 left right = left + right := by
  unfold Eip803x.TransactionSettlement.addInt64
  exact wrapInt64_of_fits h

private theorem mulInt64_of_fits {left right : Int}
    (h : Eip803x.TransactionSettlement.FitsInt64 (left * right)) :
    Eip803x.TransactionSettlement.mulInt64 left right = left * right := by
  unfold Eip803x.TransactionSettlement.mulInt64
  exact wrapInt64_of_fits h

private theorem subUInt64_of_order {left right : Nat}
    (hLeft : Eip803x.TransactionSettlement.FitsUInt64 left)
    (hOrder : right ≤ left) :
    Eip803x.TransactionSettlement.subUInt64 left right = left - right := by
  unfold Eip803x.TransactionSettlement.subUInt64
  have hNonnegative : (0 : Int) ≤ (left : Int) - right := by omega
  have hBounded : (left : Int) - right ≤
      (Eip803x.TransactionSettlement.uint64Max : Int) := by
    unfold Eip803x.TransactionSettlement.FitsUInt64 at hLeft
    omega
  rw [wrapUInt64_of_nonnegative_fits hNonnegative hBounded]
  apply Int.ofNat_inj.mp
  rw [Int.toNat_of_nonneg hNonnegative]
  norm_cast

private theorem saturatingSubUInt64_eq_sub (left right : Nat)
    (hLeft : Eip803x.TransactionSettlement.FitsUInt64 left) :
    Eip803x.TransactionSettlement.saturatingSubUInt64 left right = left - right := by
  unfold Eip803x.TransactionSettlement.saturatingSubUInt64
  by_cases hGreater : left > right
  · rw [if_pos hGreater, subUInt64_of_order hLeft (Nat.le_of_lt hGreater)]
  · rw [if_neg hGreater, Nat.sub_eq_zero_of_le (Nat.le_of_not_gt hGreater)]

private theorem successTotalRefund_nonnegative {input : Eip803x.TransactionSettlement.Input}
    (h : SuccessNoWrap input) : 0 ≤ successTotalRefund input := by
  unfold SuccessNoWrap successTotalRefund at *
  have hDestroyProduct : 0 ≤ input.destroyCount * (input.destroyRefund : Int) :=
    Int.mul_nonneg h.2.1 (Int.natCast_nonneg input.destroyRefund)
  omega

private theorem success_total_refund_no_wrap {input : Eip803x.TransactionSettlement.Input}
    (h : SuccessNoWrap input) :
    Eip803x.TransactionSettlement.totalRefund input.codeInsertExecutionRefund
      input.refundCounter input.destroyCount input.destroyRefund false false =
        successTotalRefund input := by
  rcases h with ⟨_hCounterNonnegative, _hDestroyNonnegative, hDestroyRefund,
    hCodeRefund, hDestroyProduct, hCounterAndDestroy, hTotal⟩
  unfold Eip803x.TransactionSettlement.totalRefund successTotalRefund
  simp only [Bool.not_false, Bool.true_and, if_true]
  rw [uint64ToInt64_of_fits hCodeRefund, uint64ToInt64_of_fits hDestroyRefund,
    mulInt64_of_fits hDestroyProduct, addInt64_of_fits hCounterAndDestroy,
    addInt64_of_fits hTotal]

private theorem revert_total_refund_no_wrap {input : Eip803x.TransactionSettlement.Input}
    (h : RevertNoWrap input) :
    Eip803x.TransactionSettlement.totalRefund input.codeInsertExecutionRefund
      input.refundCounter input.destroyCount input.destroyRefund false true =
        (input.codeInsertExecutionRefund : Int) := by
  unfold Eip803x.TransactionSettlement.totalRefund
  rw [uint64ToInt64_of_fits h]
  rfl

private theorem transactionGasInput_before_refund
    {input : Eip803x.TransactionSettlement.Input} {totalRefund : Int}
    (hPreRefund : input.preRefundGas ≤ input.transactionGasLimit) :
    Eip803x.TransactionGas.txGasUsedBeforeRefund (transactionGasInput input totalRefund) =
      input.preRefundGas := by
  simp only [Eip803x.TransactionGas.txGasUsedBeforeRefund, transactionGasInput]
  omega

private theorem transactionGasInput_valid
    {input : Eip803x.TransactionSettlement.Input} {totalRefund : Int}
    (h : PinnedNormalDomain input) :
    (transactionGasInput input totalRefund).Valid := by
  rcases h with ⟨_hInput, _hQuotient, _hError, _hEip8037, _hEip7778,
    hPreRefund, hFloor, _hStateNonnegative, hState, _hCap⟩
  unfold Eip803x.TransactionGas.SettlementInput.Valid
  constructor
  · simp only [transactionGasInput]
    omega
  constructor
  · rw [transactionGasInput_before_refund hPreRefund]
    exact hState
  · exact hFloor

/-- The ordinary mathematical result after removing all machine wrapping. -/
private def naturalResult
    (input : Eip803x.TransactionSettlement.Input) (totalRefund : Int) :
    Eip803x.TransactionSettlement.Result :=
  let refund := min (input.preRefundGas / 5) (Int.toNat totalRefund)
  { spentGas := max (input.preRefundGas - refund) input.calldataFloorGas
    operationGas := input.preRefundGas - refund
    blockGas := max
      (input.preRefundGas - Int.toNat input.stateGasUsed) input.calldataFloorGas
    blockStateGas := Int.toNat input.stateGasUsed
    maxUsedGas := max input.preRefundGas input.calldataFloorGas
    gasRefund := refund }

private theorem refund_toNat_eq
    {preRefundGas : Nat} {totalRefund : Int}
    (hTotalNonnegative : 0 ≤ totalRefund) :
    Int.toNat (min ((preRefundGas / 5 : Nat) : Int) totalRefund) =
      min (preRefundGas / 5) (Int.toNat totalRefund) := by
  have hCapNonnegative : (0 : Int) ≤ (preRefundGas / 5 : Nat) := by omega
  have hTotalCast : (Int.toNat totalRefund : Int) = totalRefund :=
    Int.toNat_of_nonneg hTotalNonnegative
  by_cases hCap : ((preRefundGas / 5 : Nat) : Int) ≤ totalRefund
  · have hCapNat : preRefundGas / 5 ≤ Int.toNat totalRefund := by omega
    rw [Int.min_eq_left hCap, Nat.min_eq_left hCapNat]
    apply Int.ofNat_inj.mp
    rw [Int.toNat_of_nonneg hCapNonnegative]
  · have hTotal : totalRefund ≤ ((preRefundGas / 5 : Nat) : Int) :=
      Int.le_of_lt (Int.lt_of_not_ge hCap)
    have hTotalNat : Int.toNat totalRefund ≤ preRefundGas / 5 := by omega
    rw [Int.min_eq_right hTotal, Nat.min_eq_right hTotalNat]

private theorem settle_eq_naturalResult
    {input : Eip803x.TransactionSettlement.Input} {totalRefund : Int}
    (h : PinnedNormalDomain input)
    (hTotalNonnegative : 0 ≤ totalRefund)
    (hTotalFits : Eip803x.TransactionSettlement.FitsInt64 totalRefund)
    (hTotal : Eip803x.TransactionSettlement.totalRefund input.codeInsertExecutionRefund
      input.refundCounter input.destroyCount input.destroyRefund false input.shouldRevert =
        totalRefund) :
    Eip803x.TransactionSettlement.settle input = naturalResult input totalRefund := by
  rcases h with ⟨hInput, hQuotient, hError, hEip8037, hEip7778,
    hPreRefundOrder, _hFloorOrder, hStateNonnegative, _hStateOrder, hCapFits⟩
  rcases hInput with ⟨hTransactionGas, hPreRefund, hRefundCounter, hDestroyCount,
    hDestroyRefund, hCodeRefund, hFloor, hState, hRefundQuotient, _hQuotientPositive⟩
  have hRefundNonnegative :
      0 ≤ min ((input.preRefundGas / 5 : Nat) : Int) totalRefund := by
    rw [Int.le_min]
    exact ⟨by omega, hTotalNonnegative⟩
  have hRefundFits : Eip803x.TransactionSettlement.FitsInt64
      (min ((input.preRefundGas / 5 : Nat) : Int) totalRefund) := by
    unfold Eip803x.TransactionSettlement.FitsInt64 at *
    constructor
    · rw [Int.le_min]
      exact ⟨hCapFits.1, hTotalFits.1⟩
    · exact Int.le_trans (Int.min_le_left _ _) hCapFits.2
  have hRefundOrder :
      Int.toNat (min ((input.preRefundGas / 5 : Nat) : Int) totalRefund) ≤
        input.preRefundGas := by
    have hRefundCap := Int.min_le_left ((input.preRefundGas / 5 : Nat) : Int) totalRefund
    have hRefundCast := Int.toNat_of_nonneg hRefundNonnegative
    have hDivision := Nat.div_le_self input.preRefundGas 5
    omega
  have hRefundToUInt := int64ToUInt64_of_nonnegative_fits hRefundNonnegative hRefundFits
  have hOperationSub := subUInt64_of_order hPreRefund hRefundOrder
  have hStateToUInt := int64ToUInt64_of_nonnegative_fits hStateNonnegative hState
  have hBlockSub := saturatingSubUInt64_eq_sub input.preRefundGas
    (Int.toNat input.stateGasUsed) hPreRefund
  have hRefundNat := refund_toNat_eq (preRefundGas := input.preRefundGas) hTotalNonnegative
  have hPositiveRefund :
      (if 0 < min ((input.preRefundGas / 5 : Nat) : Int) totalRefund then
          Int.toNat (min ((input.preRefundGas / 5 : Nat) : Int) totalRefund)
        else 0) =
        Int.toNat (min ((input.preRefundGas / 5 : Nat) : Int) totalRefund) := by
    by_cases hPositive : 0 < min ((input.preRefundGas / 5 : Nat) : Int) totalRefund
    · rw [if_pos hPositive]
    · have hZero : min ((input.preRefundGas / 5 : Nat) : Int) totalRefund = 0 := by omega
      rw [if_neg hPositive, hZero]
      rfl
  unfold Eip803x.TransactionSettlement.settle
  rw [normalizeUInt64_of_fits hTransactionGas, normalizeUInt64_of_fits hPreRefund,
    wrapInt64_of_fits hRefundCounter, wrapInt32_of_fits hDestroyCount,
    normalizeUInt64_of_fits hDestroyRefund, normalizeUInt64_of_fits hCodeRefund,
    normalizeUInt64_of_fits hFloor, wrapInt64_of_fits hState,
    normalizeUInt64_of_fits hRefundQuotient]
  simp only [hQuotient, hError, hEip8037, hEip7778, Bool.false_eq_true,
    if_false, if_true]
  rw [hTotal, uint64ToInt64_of_fits hCapFits]
  unfold Eip803x.TransactionSettlement.applySignedRefund
  rw [if_pos hRefundNonnegative, hRefundToUInt, hOperationSub,
    hStateToUInt, hBlockSub, hPositiveRefund, hRefundNat]
  simp [naturalResult]

private theorem extracted_refines_transactionGas_with_total
    {input : Eip803x.TransactionSettlement.Input} {totalRefund : Int}
    (h : PinnedNormalDomain input)
    (hTotalNonnegative : 0 ≤ totalRefund)
    (hTotalFits : Eip803x.TransactionSettlement.FitsInt64 totalRefund)
    (hTotal : Eip803x.TransactionSettlement.totalRefund input.codeInsertExecutionRefund
      input.refundCounter input.destroyCount input.destroyRefund false input.shouldRevert =
        totalRefund) :
    (transactionGasInput input totalRefund).Valid ∧
      (extracted input).spentGas =
        (Eip803x.TransactionGas.settle (transactionGasInput input totalRefund)).paidGas ∧
      (extracted input).operationGas =
        (Eip803x.TransactionGas.settle (transactionGasInput input totalRefund)).gasUsedAfterRefund ∧
      (extracted input).blockGas =
        (Eip803x.TransactionGas.settle (transactionGasInput input totalRefund)).executionGas ∧
      (extracted input).blockStateGas =
        (Eip803x.TransactionGas.settle (transactionGasInput input totalRefund)).stateGas ∧
      (extracted input).gasRefund =
        (Eip803x.TransactionGas.settle (transactionGasInput input totalRefund)).gasRefund := by
  have hReferenceValid := transactionGasInput_valid (totalRefund := totalRefund) h
  have hNatural := settle_eq_naturalResult h hTotalNonnegative hTotalFits hTotal
  have hExact := extracted_fields_match input h.1
  have hBefore := transactionGasInput_before_refund (totalRefund := totalRefund) h.2.2.2.2.2.1
  constructor
  · exact hReferenceValid
  constructor
  · rw [hExact.1, hNatural]
    unfold naturalResult Eip803x.TransactionGas.settle
      Eip803x.TransactionGas.paidGas Eip803x.TransactionGas.txGasUsedAfterRefund
      Eip803x.TransactionGas.txGasRefund
    rw [hBefore]
    rfl
  constructor
  · rw [hExact.2.1, hNatural]
    unfold naturalResult Eip803x.TransactionGas.settle
      Eip803x.TransactionGas.txGasUsedAfterRefund
      Eip803x.TransactionGas.txGasRefund
    rw [hBefore]
    rfl
  constructor
  · rw [hExact.2.2.1, hNatural]
    unfold naturalResult Eip803x.TransactionGas.settle
      Eip803x.TransactionGas.executionGas Eip803x.TransactionGas.stateGas
    rw [hBefore]
    rfl
  constructor
  · rw [hExact.2.2.2.1, hNatural]
    rfl
  · rw [hExact.2.2.2.2.2, hNatural]
    unfold naturalResult Eip803x.TransactionGas.settle
      Eip803x.TransactionGas.txGasRefund
    rw [hBefore]
    rfl

/--
On a successful pinned EIP-8037/EIP-7778 transaction with quotient five and
explicitly non-wrapping refund arithmetic, all settlement fields consumed by
`TransactionGas` refine its natural-number model.
-/
theorem success_refines_transactionGas
    {input : Eip803x.TransactionSettlement.Input}
    (hPinned : PinnedNormalDomain input)
    (hSuccess : input.shouldRevert = false)
    (hNoWrap : SuccessNoWrap input) :
    (transactionGasInput input (successTotalRefund input)).Valid ∧
      (extracted input).spentGas =
        (Eip803x.TransactionGas.settle
          (transactionGasInput input (successTotalRefund input))).paidGas ∧
      (extracted input).operationGas =
        (Eip803x.TransactionGas.settle
          (transactionGasInput input (successTotalRefund input))).gasUsedAfterRefund ∧
      (extracted input).blockGas =
        (Eip803x.TransactionGas.settle
          (transactionGasInput input (successTotalRefund input))).executionGas ∧
      (extracted input).blockStateGas =
        (Eip803x.TransactionGas.settle
          (transactionGasInput input (successTotalRefund input))).stateGas ∧
      (extracted input).gasRefund =
        (Eip803x.TransactionGas.settle
          (transactionGasInput input (successTotalRefund input))).gasRefund := by
  apply extracted_refines_transactionGas_with_total hPinned
    (successTotalRefund_nonnegative hNoWrap) hNoWrap.2.2.2.2.2.2
  rw [hSuccess]
  exact success_total_refund_no_wrap hNoWrap

/--
On a reverted pinned EIP-8037/EIP-7778 transaction, state and destroy refunds
are discarded; the retained code-insertion refund refines `TransactionGas`.
-/
theorem revert_refines_transactionGas
    {input : Eip803x.TransactionSettlement.Input}
    (hPinned : PinnedNormalDomain input)
    (hRevert : input.shouldRevert = true)
    (hNoWrap : RevertNoWrap input) :
    (transactionGasInput input (input.codeInsertExecutionRefund : Int)).Valid ∧
      (extracted input).spentGas =
        (Eip803x.TransactionGas.settle
          (transactionGasInput input (input.codeInsertExecutionRefund : Int))).paidGas ∧
      (extracted input).operationGas =
        (Eip803x.TransactionGas.settle
          (transactionGasInput input (input.codeInsertExecutionRefund : Int))).gasUsedAfterRefund ∧
      (extracted input).blockGas =
        (Eip803x.TransactionGas.settle
          (transactionGasInput input (input.codeInsertExecutionRefund : Int))).executionGas ∧
      (extracted input).blockStateGas =
        (Eip803x.TransactionGas.settle
          (transactionGasInput input (input.codeInsertExecutionRefund : Int))).stateGas ∧
      (extracted input).gasRefund =
        (Eip803x.TransactionGas.settle
          (transactionGasInput input (input.codeInsertExecutionRefund : Int))).gasRefund := by
  have hNonnegative : (0 : Int) ≤ input.codeInsertExecutionRefund := Int.natCast_nonneg _
  apply extracted_refines_transactionGas_with_total hPinned hNonnegative hNoWrap
  rw [hRevert]
  exact revert_total_refund_no_wrap hNoWrap

end Eip803x.Refinement.TransactionSettlement
