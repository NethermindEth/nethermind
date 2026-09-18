-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Schedule
import Eip803x.Word256
import Lean.Elab.Tactic.Grind

namespace Eip803x

inductive AccessStatus where
  | cold
  | warm
  deriving DecidableEq, Repr

structure StorageSituation where
  original : UInt256
  current : UInt256
  newValue : UInt256
  access : AccessStatus
  deriving DecidableEq, Repr

structure StorageGasEffect where
  executionCharge : Nat
  executionRefund : Int
  stateCharge : Nat
  stateRefill : Nat
  deriving DecidableEq, Repr

inductive SStoreCase where
  | noOp
  | firstCreation
  | firstClear
  | firstUpdate
  | resetCreated
  | rewriteCreated
  | restoreDirty
  | clearDirty
  | restoreCleared
  | rewriteCleared
  | rewriteDirty
  deriving DecidableEq, Repr

namespace SStore

def accessCharge (schedule : GasSchedule) : AccessStatus → Nat
  | .cold => schedule.coldStorageAccess
  | .warm => schedule.warmAccess

abbrev isFirstChange (input : StorageSituation) : Prop :=
  input.newValue ≠ input.current ∧ input.current = input.original

abbrev clearsOriginal (input : StorageSituation) : Prop :=
  input.original ≠ 0 ∧ input.current ≠ 0 ∧ input.newValue = 0

abbrev reversesClear (input : StorageSituation) : Prop :=
  input.original ≠ 0 ∧ input.current = 0 ∧ input.newValue ≠ 0

abbrev restoresOriginal (input : StorageSituation) : Prop :=
  input.newValue = input.original ∧ input.newValue ≠ input.current

abbrev createsSlot (input : StorageSituation) : Prop :=
  input.original = 0 ∧ input.current = 0 ∧ input.newValue ≠ 0

abbrev removesCreatedSlot (input : StorageSituation) : Prop :=
  input.original = 0 ∧ input.current ≠ 0 ∧ input.newValue = 0

/-- Direct executable transcription of EIP-8038's three charge/refund rules. -/
def price (schedule : GasSchedule) (input : StorageSituation) : StorageGasEffect :=
  { executionCharge := accessCharge schedule input.access +
      if isFirstChange input then schedule.storageWrite else 0
    executionRefund :=
      (if clearsOriginal input then (schedule.storageClearRefund : Int) else 0) -
      (if reversesClear input then (schedule.storageClearRefund : Int) else 0) +
      (if restoresOriginal input then (schedule.storageWrite : Int) else 0)
    stateCharge := if createsSlot input then schedule.storageSetGas else 0
    stateRefill := if removesCreatedSlot input then schedule.storageSetGas else 0 }

/-- Classifies every equality/zero pattern in the complete EIP-8038 SSTORE table. -/
def classify (input : StorageSituation) : SStoreCase :=
  if input.newValue = input.current then .noOp
  else if input.current = input.original then
    if input.original = 0 then .firstCreation
    else if input.newValue = 0 then .firstClear else .firstUpdate
  else if input.original = 0 then
    if input.newValue = 0 then .resetCreated else .rewriteCreated
  else if input.current = 0 then
    if input.newValue = input.original then .restoreCleared else .rewriteCleared
  else if input.newValue = input.original then .restoreDirty
  else if input.newValue = 0 then .clearDirty
  else .rewriteDirty

def Matches (input : StorageSituation) : SStoreCase → Prop
  | .noOp => input.newValue = input.current
  | .firstCreation =>
      input.newValue ≠ input.current ∧ input.current = input.original ∧ input.original = 0
  | .firstClear =>
      input.newValue ≠ input.current ∧ input.current = input.original ∧
        input.original ≠ 0 ∧ input.newValue = 0
  | .firstUpdate =>
      input.newValue ≠ input.current ∧ input.current = input.original ∧
        input.original ≠ 0 ∧ input.newValue ≠ 0
  | .resetCreated =>
      input.newValue ≠ input.current ∧ input.current ≠ input.original ∧
        input.original = 0 ∧ input.newValue = 0
  | .rewriteCreated =>
      input.newValue ≠ input.current ∧ input.current ≠ input.original ∧
        input.original = 0 ∧ input.newValue ≠ 0
  | .restoreDirty =>
      input.newValue ≠ input.current ∧ input.current ≠ input.original ∧
        input.original ≠ 0 ∧ input.current ≠ 0 ∧ input.newValue = input.original
  | .clearDirty =>
      input.newValue ≠ input.current ∧ input.current ≠ input.original ∧
        input.original ≠ 0 ∧ input.current ≠ 0 ∧ input.newValue = 0
  | .restoreCleared =>
      input.newValue ≠ input.current ∧ input.current ≠ input.original ∧
        input.original ≠ 0 ∧ input.current = 0 ∧ input.newValue = input.original
  | .rewriteCleared =>
      input.newValue ≠ input.current ∧ input.current ≠ input.original ∧
        input.original ≠ 0 ∧ input.current = 0 ∧ input.newValue ≠ input.original
  | .rewriteDirty =>
      input.newValue ≠ input.current ∧ input.current ≠ input.original ∧
        input.original ≠ 0 ∧ input.current ≠ 0 ∧
        input.newValue ≠ input.original ∧ input.newValue ≠ 0

/-- Declarative EIP-8038 effect for one semantic table row. -/
def effectForCase (schedule : GasSchedule) (access : AccessStatus) :
    SStoreCase → StorageGasEffect
  | .noOp => ⟨accessCharge schedule access, 0, 0, 0⟩
  | .firstCreation =>
      ⟨accessCharge schedule access + schedule.storageWrite, 0, schedule.storageSetGas, 0⟩
  | .firstClear =>
      ⟨accessCharge schedule access + schedule.storageWrite,
        schedule.storageClearRefund, 0, 0⟩
  | .firstUpdate =>
      ⟨accessCharge schedule access + schedule.storageWrite, 0, 0, 0⟩
  | .resetCreated =>
      ⟨accessCharge schedule access, schedule.storageWrite, 0, schedule.storageSetGas⟩
  | .rewriteCreated => ⟨accessCharge schedule access, 0, 0, 0⟩
  | .restoreDirty =>
      ⟨accessCharge schedule access, schedule.storageWrite, 0, 0⟩
  | .clearDirty =>
      ⟨accessCharge schedule access, schedule.storageClearRefund, 0, 0⟩
  | .restoreCleared =>
      ⟨accessCharge schedule access,
        (schedule.storageWrite : Int) - schedule.storageClearRefund, 0, 0⟩
  | .rewriteCleared =>
      ⟨accessCharge schedule access, -(schedule.storageClearRefund : Int), 0, 0⟩
  | .rewriteDirty => ⟨accessCharge schedule access, 0, 0, 0⟩

theorem matches_classify_eq (input : StorageSituation) (case : SStoreCase)
    (h : Matches input case) : classify input = case := by
  cases case <;> simp_all [Matches, classify] <;> grind

theorem cases_exhaustive (input : StorageSituation) :
    ∃ case, Matches input case := by
  by_cases hNoOp : input.newValue = input.current
  · exact ⟨.noOp, hNoOp⟩
  by_cases hFirst : input.current = input.original
  · by_cases hOriginalZero : input.original = 0
    · exact ⟨.firstCreation, hNoOp, hFirst, hOriginalZero⟩
    by_cases hNewZero : input.newValue = 0
    · exact ⟨.firstClear, hNoOp, hFirst, hOriginalZero, hNewZero⟩
    · exact ⟨.firstUpdate, hNoOp, hFirst, hOriginalZero, hNewZero⟩
  by_cases hOriginalZero : input.original = 0
  · by_cases hNewZero : input.newValue = 0
    · exact ⟨.resetCreated, hNoOp, hFirst, hOriginalZero, hNewZero⟩
    · exact ⟨.rewriteCreated, hNoOp, hFirst, hOriginalZero, hNewZero⟩
  by_cases hCurrentZero : input.current = 0
  · by_cases hRestored : input.newValue = input.original
    · exact ⟨.restoreCleared, hNoOp, hFirst, hOriginalZero, hCurrentZero, hRestored⟩
    · exact ⟨.rewriteCleared, hNoOp, hFirst, hOriginalZero, hCurrentZero, hRestored⟩
  by_cases hRestored : input.newValue = input.original
  · exact ⟨.restoreDirty, hNoOp, hFirst, hOriginalZero, hCurrentZero, hRestored⟩
  by_cases hNewZero : input.newValue = 0
  · exact ⟨.clearDirty, hNoOp, hFirst, hOriginalZero, hCurrentZero, hNewZero⟩
  · exact ⟨.rewriteDirty, hNoOp, hFirst, hOriginalZero, hCurrentZero, hRestored,
      hNewZero⟩

theorem cases_mutually_exclusive {input : StorageSituation} {left right : SStoreCase}
    (hl : Matches input left) (hr : Matches input right) : left = right := by
  have hl' := matches_classify_eq input left hl
  have hr' := matches_classify_eq input right hr
  exact hl'.symm.trans hr'

theorem price_for_case (schedule : GasSchedule) (input : StorageSituation)
    (case : SStoreCase) (h : Matches input case) :
    price schedule input = effectForCase schedule input.access case := by
  cases case <;>
    simp_all [Matches, price, effectForCase, isFirstChange, clearsOriginal,
      reversesClear, restoresOriginal, createsSlot, removesCreatedSlot,
      Int.sub_eq_add_neg, Int.add_comm] <;>
    grind

theorem first_change_pays_storage_write (schedule : GasSchedule) (input : StorageSituation)
    (hChanged : input.newValue ≠ input.current)
    (hOriginal : input.current = input.original) :
    (price schedule input).executionCharge =
      accessCharge schedule input.access + schedule.storageWrite := by
  have hFirst : isFirstChange input := ⟨hChanged, hOriginal⟩
  change accessCharge schedule input.access +
    (if isFirstChange input then schedule.storageWrite else 0) = _
  rw [if_pos hFirst]

theorem restoring_original_refund_formula (schedule : GasSchedule)
    (input : StorageSituation)
    (hRestored : input.newValue = input.original)
    (hChanged : input.newValue ≠ input.current) :
    (price schedule input).executionRefund =
      (if clearsOriginal input then (schedule.storageClearRefund : Int) else 0) -
      (if reversesClear input then (schedule.storageClearRefund : Int) else 0) +
      schedule.storageWrite := by
  have hRestore : restoresOriginal input := ⟨hRestored, hChanged⟩
  change
    (if clearsOriginal input then (schedule.storageClearRefund : Int) else 0) -
      (if reversesClear input then (schedule.storageClearRefund : Int) else 0) +
      (if restoresOriginal input then (schedule.storageWrite : Int) else 0) = _
  rw [if_pos hRestore]

theorem clearing_existing_slot_grants_clear_refund (schedule : GasSchedule)
    (input : StorageSituation)
    (hOriginal : input.original ≠ 0)
    (hCurrent : input.current ≠ 0)
    (hNew : input.newValue = 0) :
    (price schedule input).executionRefund = schedule.storageClearRefund := by
  have hClear : clearsOriginal input := ⟨hOriginal, hCurrent, hNew⟩
  have hNotReverse : ¬ reversesClear input := by
    intro hReverse
    exact hCurrent hReverse.2.1
  have hNotRestore : ¬ restoresOriginal input := by
    intro hRestore
    exact hOriginal (hRestore.1.symm.trans hNew)
  change
    (if clearsOriginal input then (schedule.storageClearRefund : Int) else 0) -
      (if reversesClear input then (schedule.storageClearRefund : Int) else 0) +
      (if restoresOriginal input then (schedule.storageWrite : Int) else 0) = _
  simp [hClear, hNotReverse, hNotRestore]

theorem restoring_cleared_nonzero_reverses_clear_refund (schedule : GasSchedule)
    (input : StorageSituation)
    (hOriginal : input.original ≠ 0)
    (hCurrent : input.current = 0)
    (hNew : input.newValue = input.original) :
    (price schedule input).executionRefund =
      (schedule.storageWrite : Int) - schedule.storageClearRefund := by
  have hNewNonzero : input.newValue ≠ 0 := by simpa [hNew] using hOriginal
  have hChanged : input.newValue ≠ input.current := by simpa [hCurrent] using hNewNonzero
  have hReverse : reversesClear input := ⟨hOriginal, hCurrent, hNewNonzero⟩
  have hRestore : restoresOriginal input := ⟨hNew, hChanged⟩
  have hNotClear : ¬ clearsOriginal input := by
    intro hClear
    exact hClear.2.1 hCurrent
  change
    (if clearsOriginal input then (schedule.storageClearRefund : Int) else 0) -
      (if reversesClear input then (schedule.storageClearRefund : Int) else 0) +
      (if restoresOriginal input then (schedule.storageWrite : Int) else 0) = _
  rw [if_neg hNotClear, if_pos hReverse, if_pos hRestore]
  simp [Int.sub_eq_add_neg, Int.add_comm]

theorem clearing_created_slot_refills_state_gas (schedule : GasSchedule)
    (input : StorageSituation)
    (hOriginal : input.original = 0)
    (hCurrent : input.current ≠ 0)
    (hNew : input.newValue = 0) :
    (price schedule input).stateRefill = schedule.storageSetGas := by
  have hRemoves : removesCreatedSlot input := ⟨hOriginal, hCurrent, hNew⟩
  change (if removesCreatedSlot input then schedule.storageSetGas else 0) = _
  rw [if_pos hRemoves]

theorem new_slot_charges_state_gas (schedule : GasSchedule)
    (input : StorageSituation)
    (hOriginal : input.original = 0)
    (hCurrent : input.current = 0)
    (hNew : input.newValue ≠ 0) :
    (price schedule input).stateCharge = schedule.storageSetGas := by
  have hCreates : createsSlot input := ⟨hOriginal, hCurrent, hNew⟩
  change (if createsSlot input then schedule.storageSetGas else 0) = _
  rw [if_pos hCreates]

theorem unchanged_slot_pays_only_access (schedule : GasSchedule)
    (input : StorageSituation)
    (hUnchanged : input.newValue = input.original) :
    (price schedule input).executionCharge = accessCharge schedule input.access := by
  have hNotFirst : ¬ isFirstChange input := by
    intro hFirst
    exact hFirst.1 (hUnchanged.trans hFirst.2.symm)
  change accessCharge schedule input.access +
    (if isFirstChange input then schedule.storageWrite else 0) = _
  simp [hNotFirst]

theorem no_op_has_no_refund_or_state_effect (schedule : GasSchedule)
    (input : StorageSituation)
    (hNoOp : input.newValue = input.current) :
    (price schedule input).executionRefund = 0 ∧
    (price schedule input).stateCharge = 0 ∧
    (price schedule input).stateRefill = 0 := by
  have hNotClear : ¬ clearsOriginal input := by
    intro hClear
    exact hClear.2.1 (hNoOp.symm.trans hClear.2.2)
  have hNotReverse : ¬ reversesClear input := by
    intro hReverse
    exact hReverse.2.2 (hNoOp.trans hReverse.2.1)
  have hNotRestores : ¬ restoresOriginal input := by
    intro hRestore
    exact hRestore.2 hNoOp
  have hNotCreates : ¬ createsSlot input := by
    intro hCreates
    exact hCreates.2.2 (hNoOp.trans hCreates.2.1)
  have hNotRemoves : ¬ removesCreatedSlot input := by
    intro hRemoves
    exact hRemoves.2.1 (hNoOp.symm.trans hRemoves.2.2)
  change
    ((if clearsOriginal input then (schedule.storageClearRefund : Int) else 0) -
      (if reversesClear input then (schedule.storageClearRefund : Int) else 0) +
      (if restoresOriginal input then (schedule.storageWrite : Int) else 0) = 0) ∧
    (if createsSlot input then schedule.storageSetGas else 0) = 0 ∧
    (if removesCreatedSlot input then schedule.storageSetGas else 0) = 0
  simp [hNotClear, hNotReverse, hNotRestores, hNotCreates, hNotRemoves]

end SStore
end Eip803x
