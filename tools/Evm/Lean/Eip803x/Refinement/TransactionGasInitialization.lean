-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Generated.TransactionGasInitializationKernel
import Eip803x.TransactionGas
import Lean.Elab.Tactic.Omega

namespace Eip803x.Refinement.TransactionGasInitialization

open Eip803x
open Eip803x.Generated.TransactionGasInitializationKernel

/-- The natural-number range represented by a production `ulong`. -/
def FitsUInt64 (value : Nat) : Prop :=
  value ≤ uint64Max

/-- The signed-integer range represented by a production `long`. -/
def FitsInt64 (value : Int) : Prop :=
  int64Min ≤ value ∧ value ≤ int64Max

/-- The independent mathematical input corresponding to the production kernel arguments. -/
def referenceInput
    (gasLimit intrinsicExecutionGas : Nat)
    (intrinsicStateGas : Int)
    (executionGasLimitCap : Nat) : TransactionGas.InitializationInput :=
  { txGas := gasLimit
    intrinsicGas := intrinsicExecutionGas
    intrinsicStateGas := Int.toNat intrinsicStateGas
    txMaxGasLimit := executionGasLimitCap }

/--
The adapter-level conditions under which the fixed-width kernel has the same
mathematical meaning as the EIP-8037 initialization relation.
-/
def RefinementValid
    (gasLimit intrinsicExecutionGas : Nat)
    (intrinsicStateGas : Int)
    (executionGasLimitCap : Nat) : Prop :=
  FitsUInt64 gasLimit ∧
  FitsUInt64 intrinsicExecutionGas ∧
  FitsInt64 intrinsicStateGas ∧
  0 ≤ intrinsicStateGas ∧
  intrinsicExecutionGas + Int.toNat intrinsicStateGas ≤ uint64Max ∧
  intrinsicExecutionGas + Int.toNat intrinsicStateGas ≤ gasLimit ∧
  intrinsicExecutionGas ≤ executionGasLimitCap ∧
  FitsUInt64 executionGasLimitCap ∧
  FitsInt64
    ((gasLimit - (intrinsicExecutionGas + Int.toNat intrinsicStateGas) -
      min (executionGasLimitCap - intrinsicExecutionGas)
        (gasLimit - (intrinsicExecutionGas + Int.toNat intrinsicStateGas)) : Nat) : Int)

private theorem normalizeUInt64_of_fits {value : Nat} (h : FitsUInt64 value) :
    normalizeUInt64 value = value := by
  unfold normalizeUInt64 FitsUInt64 at *
  simp [h]

private theorem wrapInt64_of_fits {value : Int} (h : FitsInt64 value) :
    wrapInt64 value = value := by
  unfold wrapInt64 FitsInt64 at *
  simp [h]

private theorem wrapUInt64_of_nonnegative_fits {value : Int}
    (hNonnegative : 0 ≤ value)
    (hFits : value ≤ (uint64Max : Int)) :
    wrapUInt64 value = Int.toNat value := by
  unfold wrapUInt64
  simp [hNonnegative, hFits]

private theorem int64ToUInt64_of_nonnegative_fits {value : Int}
    (hNonnegative : 0 ≤ value)
    (hFits : FitsInt64 value) :
    int64ToUInt64 value = Int.toNat value := by
  unfold int64ToUInt64
  apply wrapUInt64_of_nonnegative_fits hNonnegative
  rcases hFits with ⟨_hMinimum, hMaximum⟩
  have hRange : (2 ^ 63 : Int) - 1 ≤ (2 ^ 64 : Int) - 1 := by decide
  unfold int64Max int64SignBit at hMaximum
  unfold uint64Max uint64Modulus
  exact Int.le_trans hMaximum hRange

private theorem addUInt64_of_fits {left right : Nat}
    (hFits : left + right ≤ uint64Max) :
    addUInt64 left right = left + right := by
  unfold addUInt64
  have hNonnegative : (0 : Int) ≤ (left : Int) + right := by omega
  have hBounded : (left : Int) + right ≤ (uint64Max : Int) := by
    omega
  rw [wrapUInt64_of_nonnegative_fits hNonnegative hBounded]
  apply Int.ofNat_inj.mp
  rw [Int.toNat_of_nonneg hNonnegative]
  norm_cast

private theorem subUInt64_of_order {left right : Nat}
    (hLeft : FitsUInt64 left)
    (hOrder : right ≤ left) :
    subUInt64 left right = left - right := by
  unfold subUInt64
  have hNonnegative : (0 : Int) ≤ (left : Int) - right := by omega
  have hBounded : (left : Int) - right ≤ (uint64Max : Int) := by
    unfold FitsUInt64 at hLeft
    omega
  rw [wrapUInt64_of_nonnegative_fits hNonnegative hBounded]
  apply Int.ofNat_inj.mp
  rw [Int.toNat_of_nonneg hNonnegative]
  norm_cast

private theorem uint64ToInt64_of_fits {value : Nat}
    (hFits : FitsInt64 (value : Int)) :
    uint64ToInt64 value = (value : Int) := by
  unfold uint64ToInt64
  exact wrapInt64_of_fits hFits

private theorem reference_input_valid
    {gasLimit intrinsicExecutionGas executionGasLimitCap : Nat}
    {intrinsicStateGas : Int}
    (h : RefinementValid gasLimit intrinsicExecutionGas intrinsicStateGas executionGasLimitCap) :
    (referenceInput gasLimit intrinsicExecutionGas intrinsicStateGas executionGasLimitCap).Valid := by
  rcases h with ⟨hGasLimit, hExecution, hState, hStateNonnegative, hTotalFits, hTotalGas,
    hExecutionCap, hCapFits, hReservoirFits⟩
  unfold referenceInput TransactionGas.InitializationInput.Valid
  exact ⟨hTotalGas, hExecutionCap⟩

private theorem generated_tryCreate_success_shape
    {gasLimit intrinsicExecutionGas executionGasLimitCap : Nat}
    {intrinsicStateGas : Int}
    (h : RefinementValid gasLimit intrinsicExecutionGas intrinsicStateGas executionGasLimitCap) :
    tryCreate gasLimit intrinsicExecutionGas intrinsicStateGas true executionGasLimitCap =
      { outcome := .success
        value := min (executionGasLimitCap - intrinsicExecutionGas)
          (gasLimit - (intrinsicExecutionGas + Int.toNat intrinsicStateGas))
        stateReservoir :=
          (gasLimit - (intrinsicExecutionGas + Int.toNat intrinsicStateGas) -
            min (executionGasLimitCap - intrinsicExecutionGas)
              (gasLimit - (intrinsicExecutionGas + Int.toNat intrinsicStateGas)) : Nat)
        stateGasUsed := intrinsicStateGas
        stateGasSpill := 0
        stateGasSpillRefunded := 0 } := by
  rcases h with ⟨hGasLimit, hExecution, hState, hStateNonnegative, hTotalFits, hTotalGas,
    hExecutionCap, hCapFits, hReservoirFits⟩
  have hStateWrap := wrapInt64_of_fits hState
  have hStateToUInt := int64ToUInt64_of_nonnegative_fits hStateNonnegative hState
  have hTotalAdd := addUInt64_of_fits hTotalFits
  have hAvailableSub := subUInt64_of_order hGasLimit hTotalGas
  have hCapSub := subUInt64_of_order hCapFits hExecutionCap
  have hReservoirOrder :
      min (executionGasLimitCap - intrinsicExecutionGas)
          (gasLimit - (intrinsicExecutionGas + Int.toNat intrinsicStateGas)) ≤
        gasLimit - (intrinsicExecutionGas + Int.toNat intrinsicStateGas) := by
    exact Nat.min_le_right _ _
  have hAvailableFits : FitsUInt64 (gasLimit - (intrinsicExecutionGas + Int.toNat intrinsicStateGas)) := by
    unfold FitsUInt64
    exact Nat.le_trans (Nat.sub_le _ _) hGasLimit
  have hReservoirSub := subUInt64_of_order hAvailableFits hReservoirOrder
  have hReservoirCast := uint64ToInt64_of_fits hReservoirFits
  have hNotInsufficient : ¬ gasLimit < intrinsicExecutionGas + Int.toNat intrinsicStateGas :=
    Nat.not_lt_of_ge hTotalGas
  have hCapBranch :
      (if executionGasLimitCap ≤ intrinsicExecutionGas then
        0
      else
        executionGasLimitCap - intrinsicExecutionGas) =
      executionGasLimitCap - intrinsicExecutionGas := by
    by_cases hEqual : executionGasLimitCap = intrinsicExecutionGas
    · simp [hEqual]
    · have hNotCapLe : ¬ executionGasLimitCap ≤ intrinsicExecutionGas := by omega
      simp [hNotCapLe]
  unfold tryCreate tryCreateNormalized
  rw [normalizeUInt64_of_fits hGasLimit, normalizeUInt64_of_fits hExecution,
    hStateWrap, normalizeUInt64_of_fits hCapFits, hStateToUInt, hTotalAdd]
  simp [hNotInsufficient, hCapBranch, hAvailableSub, hCapSub]
  rw [Nat.min_comm (gasLimit - (intrinsicExecutionGas + Int.toNat intrinsicStateGas))
    (executionGasLimitCap - intrinsicExecutionGas)]
  rw [hReservoirSub, hReservoirCast]
  constructor <;> rfl

private theorem initialize_reference_shape
    {gasLimit intrinsicExecutionGas executionGasLimitCap : Nat}
    {intrinsicStateGas : Int} :
    TransactionGas.initializeTransactionGas
      (referenceInput gasLimit intrinsicExecutionGas intrinsicStateGas executionGasLimitCap) =
      { evmGas := gasLimit - (intrinsicExecutionGas + Int.toNat intrinsicStateGas)
        executionGasBudget := executionGasLimitCap - intrinsicExecutionGas
        gasLeft := min (executionGasLimitCap - intrinsicExecutionGas)
          (gasLimit - (intrinsicExecutionGas + Int.toNat intrinsicStateGas))
        stateGasReservoir :=
          gasLimit - (intrinsicExecutionGas + Int.toNat intrinsicStateGas) -
            min (executionGasLimitCap - intrinsicExecutionGas)
              (gasLimit - (intrinsicExecutionGas + Int.toNat intrinsicStateGas))
        stateGasUsed := Int.toNat intrinsicStateGas
        stateGasSpill := 0
        stateGasSpillRefunded := 0 } := by
  unfold TransactionGas.initializeTransactionGas TransactionGas.evmGas TransactionGas.totalIntrinsicGas
    TransactionGas.executionGasBudget TransactionGas.initialGasLeft
    TransactionGas.initialStateGasReservoir referenceInput
  rfl

/--
The extracted EIP-8037-enabled initialization kernel refines the independent
split for every nonnegative, fixed-width-representable intrinsic state baseline.
This includes values that an adapter can retain after folding EIP-7702
pre-execution state charges; it does not prove that authorization processing
produces such a baseline.
-/

theorem generatedTryCreate_refines_initializeTransactionGas
    {gasLimit intrinsicExecutionGas executionGasLimitCap : Nat}
    {intrinsicStateGas : Int}
    (h : RefinementValid gasLimit intrinsicExecutionGas intrinsicStateGas executionGasLimitCap) :
    let result := tryCreate gasLimit intrinsicExecutionGas intrinsicStateGas true executionGasLimitCap
    let input := referenceInput gasLimit intrinsicExecutionGas intrinsicStateGas executionGasLimitCap
    let model := TransactionGas.initializeTransactionGas input
    input.Valid ∧
      result.outcome = .success ∧
        result.value = model.gasLeft ∧
        result.stateReservoir = (model.stateGasReservoir : Int) ∧
        result.stateGasUsed = (model.stateGasUsed : Int) ∧
        result.stateGasSpill = (model.stateGasSpill : Int) ∧
        result.stateGasSpillRefunded = (model.stateGasSpillRefunded : Int) ∧
        model.evmGas = gasLimit - (intrinsicExecutionGas + Int.toNat intrinsicStateGas) ∧
        model.executionGasBudget = executionGasLimitCap - intrinsicExecutionGas := by
  dsimp
  constructor
  · exact reference_input_valid h
  · rw [generated_tryCreate_success_shape h]
    rw [initialize_reference_shape]
    rcases h with ⟨_hGasLimit, _hExecution, _hState, hStateNonnegative, _hTotalFits, _hTotalGas,
      _hExecutionCap, _hCapFits, _hReservoirFits⟩
    simp [Int.toNat_of_nonneg hStateNonnegative]

/-- The pinned-EIP transaction-admission case is the zero state-baseline specialization. -/
theorem generatedTryCreate_zero_state_corollary
    {gasLimit intrinsicExecutionGas executionGasLimitCap : Nat}
    (h : RefinementValid gasLimit intrinsicExecutionGas 0 executionGasLimitCap) :
    let result := tryCreate gasLimit intrinsicExecutionGas 0 true executionGasLimitCap
    let input := referenceInput gasLimit intrinsicExecutionGas 0 executionGasLimitCap
    let model := TransactionGas.initializeTransactionGas input
    input.Valid ∧ result.outcome = .success ∧ result.value = model.gasLeft ∧
      result.stateReservoir = (model.stateGasReservoir : Int) ∧
      result.stateGasUsed = 0 ∧ result.stateGasSpill = 0 ∧
      result.stateGasSpillRefunded = 0 := by
  have hRefinement := generatedTryCreate_refines_initializeTransactionGas h
  dsimp at hRefinement ⊢
  rcases hRefinement with
    ⟨hValid, hOutcome, hValue, hReservoir, hUsed, hSpill, hSpillRefunded,
      _hEvmGas, _hExecutionBudget⟩
  exact ⟨hValid, hOutcome, hValue, hReservoir,
    by simpa [referenceInput, TransactionGas.initializeTransactionGas] using hUsed,
    by simpa [referenceInput, TransactionGas.initializeTransactionGas] using hSpill,
    by simpa [referenceInput, TransactionGas.initializeTransactionGas] using hSpillRefunded⟩

/--
The source-kernel failure branch is a value-level default result, so it cannot
retain partial gas state. This covers every nonnegative, representable state
baseline whose intrinsic sum does not wrap, and is intentionally separate from
the successful EIP-8037 refinement claim.
-/
theorem generatedTryCreate_failure_is_default
    {gasLimit intrinsicExecutionGas executionGasLimitCap : Nat}
    {intrinsicStateGas : Int}
    (hGasLimit : FitsUInt64 gasLimit)
    (hExecution : FitsUInt64 intrinsicExecutionGas)
    (hState : FitsInt64 intrinsicStateGas)
    (hStateNonnegative : 0 ≤ intrinsicStateGas)
    (hTotalFits : intrinsicExecutionGas + Int.toNat intrinsicStateGas ≤ uint64Max)
    (hInsufficient : gasLimit < intrinsicExecutionGas + Int.toNat intrinsicStateGas)
    (hCap : FitsUInt64 executionGasLimitCap) :
    tryCreate gasLimit intrinsicExecutionGas intrinsicStateGas true executionGasLimitCap =
      { outcome := .intrinsicGasExceedsLimit
        value := 0
        stateReservoir := 0
        stateGasUsed := 0
        stateGasSpill := 0
        stateGasSpillRefunded := 0 } := by
  have hStateWrap := wrapInt64_of_fits hState
  have hStateToUInt := int64ToUInt64_of_nonnegative_fits hStateNonnegative hState
  have hTotalAdd := addUInt64_of_fits hTotalFits
  unfold tryCreate tryCreateNormalized
  rw [normalizeUInt64_of_fits hGasLimit, normalizeUInt64_of_fits hExecution,
    hStateWrap, normalizeUInt64_of_fits hCap, hStateToUInt, hTotalAdd]
  simp [hInsufficient]

/-- The generated block boundary exactly implements the header's two-dimensional maximum. -/
theorem generatedCombine_eq_headerGasUsed_of_machine_inputs
    {blockExecutionGas blockStateGas : Nat}
    (hExecution : FitsUInt64 blockExecutionGas)
    (hState : FitsUInt64 blockStateGas) :
    combine blockExecutionGas blockStateGas =
      TransactionGas.headerGasUsed
        { executionGasUsed := blockExecutionGas
          stateGasUsed := blockStateGas
          cumulativeReceiptGasUsed := 0 } := by
  unfold combine combineNormalized TransactionGas.headerGasUsed
  rw [normalizeUInt64_of_fits hExecution, normalizeUInt64_of_fits hState]

end Eip803x.Refinement.TransactionGasInitialization
