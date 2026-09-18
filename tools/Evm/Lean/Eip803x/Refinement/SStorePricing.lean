-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Generated.SStorePricingKernel
import Eip803x.SStore
import Lean.Elab.Tactic.Omega

namespace Eip803x.Refinement.SStorePricing

open Eip803x

namespace Generated

abbrev uint64Max := Eip803x.Generated.SStorePricingKernel.uint64Max
abbrev int64Min := Eip803x.Generated.SStorePricingKernel.int64Min
abbrev int64Max := Eip803x.Generated.SStorePricingKernel.int64Max
abbrev normalizeUInt64 := Eip803x.Generated.SStorePricingKernel.normalizeUInt64
abbrev wrapInt64 := Eip803x.Generated.SStorePricingKernel.wrapInt64
abbrev uint64ToInt64 := Eip803x.Generated.SStorePricingKernel.uint64ToInt64
abbrev negInt64 := Eip803x.Generated.SStorePricingKernel.negInt64
abbrev Input := Eip803x.Generated.SStorePricingKernel.Input
abbrev AccessStatus := Eip803x.Generated.SStorePricingKernel.AccessStatus
abbrev AccessSchedule := Eip803x.Generated.SStorePricingKernel.AccessSchedule
abbrev PostAccessSchedule := Eip803x.Generated.SStorePricingKernel.PostAccessSchedule
abbrev PostAccessResult := Eip803x.Generated.SStorePricingKernel.PostAccessResult
abbrev Result := Eip803x.Generated.SStorePricingKernel.Result
abbrev priceAfterAccess := Eip803x.Generated.SStorePricingKernel.priceAfterAccess
abbrev price := Eip803x.Generated.SStorePricingKernel.price

end Generated

/-- The natural-number range represented by a production `ulong`. -/
def FitsUInt64 (value : Nat) : Prop :=
  value ≤ Generated.uint64Max

/-- The signed-integer range represented by a production `long`. -/
def FitsInt64 (value : Int) : Prop :=
  Generated.int64Min ≤ value ∧ value ≤ Generated.int64Max

/-- The value facts supplied to the pricing kernel are derived from one storage situation. -/
def InputRepresents (input : Generated.Input) (storage : StorageSituation) : Prop :=
  input.originalIsZero = decide (storage.original = 0) ∧
  input.currentIsZero = decide (storage.current = 0) ∧
  input.newIsZero = decide (storage.newValue = 0) ∧
  input.currentSameAsOriginal = decide (storage.current = storage.original) ∧
  input.newSameAsCurrent = decide (storage.newValue = storage.current) ∧
  input.newSameAsOriginal = decide (storage.newValue = storage.original)

/-- The pure access wrapper receives the access part of the explicit gas schedule. -/
def AccessScheduleRepresents
    (access : Generated.AccessSchedule)
    (schedule : GasSchedule) : Prop :=
  access.coldStorageAccessGas = schedule.coldStorageAccess ∧
  access.warmAccessGas = schedule.warmAccess

/-- The production-used post-access kernel receives the non-access schedule inputs explicitly. -/
def PostAccessScheduleRepresents
    (postAccess : Generated.PostAccessSchedule)
    (schedule : GasSchedule) : Prop :=
  postAccess.storageWriteGas = schedule.storageWrite ∧
  postAccess.storageClearRefund = schedule.storageClearRefund ∧
  postAccess.storageSetStateGas = GasSchedule.storageSetGas schedule

/--
Fixed-width obligations for the generated C# kernel. These are adapter assumptions, not EIP rules:
the EIP schedule is mathematical, while C# stores access/write gas in `ulong` and refund/state
components in `long`. They do not establish the later unchecked `vmState.Refund` accumulator
bound, which remains an opcode-composition obligation.
-/
def PostAccessNoWrap (postAccess : Generated.PostAccessSchedule) : Prop :=
  FitsUInt64 postAccess.storageWriteGas ∧
  FitsInt64 postAccess.storageClearRefund ∧
  FitsInt64 postAccess.storageSetStateGas ∧
  FitsInt64 (postAccess.storageWriteGas : Int) ∧
  FitsInt64 (-postAccess.storageClearRefund) ∧
  0 ≤ postAccess.storageClearRefund ∧
  0 ≤ postAccess.storageSetStateGas

def NoWrap
    (access : Generated.AccessSchedule)
    (postAccess : Generated.PostAccessSchedule) : Prop :=
  FitsUInt64 access.coldStorageAccessGas ∧
  FitsUInt64 access.warmAccessGas ∧
  PostAccessNoWrap postAccess

def toPostAccessEffect (result : Generated.PostAccessResult) : StorageGasEffect :=
  { executionCharge := result.executionWriteGas
    executionRefund := result.storageClearRefund + result.storageClearRefundReversal + result.restoreOriginalRefund
    stateCharge := Int.toNat result.stateGasCharge
    stateRefill := Int.toNat result.stateGasRefund }

def toEffect (result : Generated.Result) : StorageGasEffect :=
  { executionCharge := result.accessGas + result.postAccess.executionWriteGas
    executionRefund := result.postAccess.storageClearRefund + result.postAccess.storageClearRefundReversal +
      result.postAccess.restoreOriginalRefund
    stateCharge := Int.toNat result.postAccess.stateGasCharge
    stateRefill := Int.toNat result.postAccess.stateGasRefund }

private theorem normalizeUInt64_of_fits {value : Nat} (h : FitsUInt64 value) :
    Generated.normalizeUInt64 value = value := by
  by_cases hFits : value ≤ Eip803x.Generated.SStorePricingKernel.uint64Max
  · simp [Eip803x.Generated.SStorePricingKernel.normalizeUInt64, hFits]
  · exact False.elim (hFits h)

private theorem wrapInt64_of_fits {value : Int} (h : FitsInt64 value) :
    Generated.wrapInt64 value = value := by
  simp [Eip803x.Generated.SStorePricingKernel.wrapInt64, h.1, h.2]

private theorem uint64ToInt64_of_fits {value : Nat} (h : FitsInt64 (value : Int)) :
    Generated.uint64ToInt64 value = (value : Int) := by
  unfold Generated.uint64ToInt64
  exact wrapInt64_of_fits h

private theorem negInt64_of_fits {value : Int} (h : FitsInt64 (-value)) :
    Generated.negInt64 value = -value := by
  unfold Generated.negInt64
  exact wrapInt64_of_fits h

private theorem postAccessPrice_refines
    {input : Generated.Input}
    {postAccess : Generated.PostAccessSchedule}
    {schedule : GasSchedule}
    {storage : StorageSituation}
    (hInput : InputRepresents input storage)
    (hSchedule : PostAccessScheduleRepresents postAccess schedule)
    (hNoWrap : PostAccessNoWrap postAccess) :
    toPostAccessEffect (Generated.priceAfterAccess input postAccess) =
      { executionCharge := if SStore.isFirstChange storage then schedule.storageWrite else 0
        executionRefund :=
          (if SStore.clearsOriginal storage then (schedule.storageClearRefund : Int) else 0) -
          (if SStore.reversesClear storage then (schedule.storageClearRefund : Int) else 0) +
          (if SStore.restoresOriginal storage then (schedule.storageWrite : Int) else 0)
        stateCharge := if SStore.createsSlot storage then GasSchedule.storageSetGas schedule else 0
        stateRefill := if SStore.removesCreatedSlot storage then GasSchedule.storageSetGas schedule else 0 } := by
  rcases hInput with ⟨hInputOriginalZero, hInputCurrentZero, hInputNewZero,
    hInputCurrentOriginal, hInputNewCurrent, hInputNewOriginal⟩
  rcases hSchedule with ⟨hWrite, hClear, hSet⟩
  rcases hNoWrap with ⟨hWriteUInt, hClearInt, hSetInt, hWriteInt, hNegClear, hClearNonnegative, hSetNonnegative⟩
  have hWriteNormalize := normalizeUInt64_of_fits hWriteUInt
  have hClearWrap := wrapInt64_of_fits hClearInt
  have hSetWrap := wrapInt64_of_fits hSetInt
  have hWriteNormalizeSchedule : Generated.normalizeUInt64 schedule.storageWrite = schedule.storageWrite := by
    rw [← hWrite]
    exact hWriteNormalize
  have hClearWrapSchedule : Generated.wrapInt64 (schedule.storageClearRefund : Int) = schedule.storageClearRefund := by
    rw [← hClear]
    exact hClearWrap
  have hSetWrapSchedule : Generated.wrapInt64 (GasSchedule.storageSetGas schedule : Int) = GasSchedule.storageSetGas schedule := by
    rw [← hSet]
    exact hSetWrap
  have hWriteCast := uint64ToInt64_of_fits hWriteInt
  have hNegClearWrap := negInt64_of_fits hNegClear
  have hWriteCastSchedule : Generated.uint64ToInt64 schedule.storageWrite = (schedule.storageWrite : Int) := by
    rw [← hWrite]
    exact hWriteCast
  have hNegClearWrapSchedule : Generated.negInt64 (schedule.storageClearRefund : Int) = -(schedule.storageClearRefund : Int) := by
    rw [← hClear]
    exact hNegClearWrap
  unfold toPostAccessEffect
  unfold Generated.priceAfterAccess
  unfold Eip803x.Generated.SStorePricingKernel.priceAfterAccess
  unfold Eip803x.Generated.SStorePricingKernel.priceAfterAccessNormalized
  by_cases hOriginal : storage.original = 0 <;>
    by_cases hCurrent : storage.current = 0 <;>
    by_cases hNew : storage.newValue = 0 <;>
    by_cases hCurrentOriginal : storage.current = storage.original <;>
    by_cases hNewCurrent : storage.newValue = storage.current <;>
    by_cases hNewOriginal : storage.newValue = storage.original <;>
    simp [hWriteNormalizeSchedule, hClearWrapSchedule, hSetWrapSchedule,
      hInputOriginalZero, hInputCurrentZero, hInputNewZero,
      hInputCurrentOriginal, hInputNewCurrent, hInputNewOriginal,
      hOriginal, hCurrent, hNew, hCurrentOriginal, hNewCurrent, hNewOriginal,
      SStore.isFirstChange, SStore.clearsOriginal, SStore.reversesClear,
      SStore.restoresOriginal, SStore.createsSlot, SStore.removesCreatedSlot,
      hWriteCastSchedule, hNegClearWrapSchedule,
      hWrite, hClear, hSet]

/--
The extracted full reference wrapper refines the independent EIP-8038 `SStore.price` function.
It assumes the kernel booleans are derived from one storage triple and all C# fixed-width schedule
conversions are no-wrap. This is a value-kernel claim only.
-/
theorem price_refines_sstore_price
    {input : Generated.Input}
    {accessStatus : Generated.AccessStatus}
    {accessSchedule : Generated.AccessSchedule}
    {postAccessSchedule : Generated.PostAccessSchedule}
    {schedule : GasSchedule}
    {storage : StorageSituation}
    (hInput : InputRepresents input storage)
    (hAccess : AccessScheduleRepresents accessSchedule schedule)
    (hPostAccess : PostAccessScheduleRepresents postAccessSchedule schedule)
    (hNoWrap : NoWrap accessSchedule postAccessSchedule)
    (hAccessStatus : (accessStatus = .cold ∧ storage.access = .cold) ∨
      (accessStatus = .warm ∧ storage.access = .warm)) :
    toEffect (Generated.price input accessStatus accessSchedule postAccessSchedule) =
      SStore.price schedule storage := by
  rcases hAccess with ⟨hCold, hWarm⟩
  rcases hNoWrap with ⟨hColdFits, hWarmFits, hPostNoWrap⟩
  have hPost := postAccessPrice_refines hInput hPostAccess hPostNoWrap
  rcases hPostNoWrap with ⟨hWriteFits, hClearFits, hSetFits,
    hWriteInt, hNegClear, hClearNonnegative, hSetNonnegative⟩
  have hColdNormalize := normalizeUInt64_of_fits hColdFits
  have hWarmNormalize := normalizeUInt64_of_fits hWarmFits
  have hColdNormalizeSchedule : Generated.normalizeUInt64 schedule.coldStorageAccess = schedule.coldStorageAccess := by
    rw [← hCold]
    exact hColdNormalize
  have hWarmNormalizeSchedule : Generated.normalizeUInt64 schedule.warmAccess = schedule.warmAccess := by
    rw [← hWarm]
    exact hWarmNormalize
  have hPostExecutionCharge :
      (Generated.priceAfterAccess input postAccessSchedule).executionWriteGas =
        if SStore.isFirstChange storage then schedule.storageWrite else 0 := by
    simpa [toPostAccessEffect] using congrArg StorageGasEffect.executionCharge hPost
  have hPostExecutionRefund :
      (Generated.priceAfterAccess input postAccessSchedule).storageClearRefund +
          (Generated.priceAfterAccess input postAccessSchedule).storageClearRefundReversal +
          (Generated.priceAfterAccess input postAccessSchedule).restoreOriginalRefund =
        (if SStore.clearsOriginal storage then (schedule.storageClearRefund : Int) else 0) -
          (if SStore.reversesClear storage then (schedule.storageClearRefund : Int) else 0) +
          (if SStore.restoresOriginal storage then (schedule.storageWrite : Int) else 0) := by
    simpa [toPostAccessEffect] using congrArg StorageGasEffect.executionRefund hPost
  have hPostStateCharge :
      Int.toNat (Generated.priceAfterAccess input postAccessSchedule).stateGasCharge =
        if SStore.createsSlot storage then GasSchedule.storageSetGas schedule else 0 := by
    simpa [toPostAccessEffect] using congrArg StorageGasEffect.stateCharge hPost
  have hPostStateRefill :
      Int.toNat (Generated.priceAfterAccess input postAccessSchedule).stateGasRefund =
        if SStore.removesCreatedSlot storage then GasSchedule.storageSetGas schedule else 0 := by
    simpa [toPostAccessEffect] using congrArg StorageGasEffect.stateRefill hPost
  unfold toEffect SStore.price
  unfold Generated.price
  unfold Eip803x.Generated.SStorePricingKernel.price
  unfold Eip803x.Generated.SStorePricingKernel.priceNormalized
  rcases hAccessStatus with ⟨hGeneratedAccess, hReferenceAccess⟩ | ⟨hGeneratedAccess, hReferenceAccess⟩
  · subst accessStatus
    congr 1 <;>
    first
    | simp [hColdNormalizeSchedule, hReferenceAccess, hCold,
        SStore.accessCharge, hPostExecutionCharge]
    | exact hPostExecutionRefund
    | exact hPostStateCharge
    | exact hPostStateRefill
  · subst accessStatus
    congr 1 <;>
    first
    | simp [hWarmNormalizeSchedule, hReferenceAccess, hWarm,
        SStore.accessCharge, hPostExecutionCharge]
    | exact hPostExecutionRefund
    | exact hPostStateCharge
    | exact hPostStateRefill

/--
The actual opcode adapter invokes only `priceAfterAccess` after the existing access-policy call.
It does not establish the `accessStatus` premise above: access-before-slot-read, BAL/tracing-access
behavior, access warming, and its OOG ordering remain explicit composition obligations.
-/
theorem production_post_access_boundary
    {input : Generated.Input}
    {postAccessSchedule : Generated.PostAccessSchedule}
    {schedule : GasSchedule}
    {storage : StorageSituation}
    (hInput : InputRepresents input storage)
    (hPostAccess : PostAccessScheduleRepresents postAccessSchedule schedule)
    (hNoWrap : PostAccessNoWrap postAccessSchedule) :
    toPostAccessEffect (Generated.priceAfterAccess input postAccessSchedule) =
      { executionCharge := if SStore.isFirstChange storage then schedule.storageWrite else 0
        executionRefund :=
          (if SStore.clearsOriginal storage then (schedule.storageClearRefund : Int) else 0) -
          (if SStore.reversesClear storage then (schedule.storageClearRefund : Int) else 0) +
          (if SStore.restoresOriginal storage then (schedule.storageWrite : Int) else 0)
        stateCharge := if SStore.createsSlot storage then GasSchedule.storageSetGas schedule else 0
        stateRefill := if SStore.removesCreatedSlot storage then GasSchedule.storageSetGas schedule else 0 } :=
  postAccessPrice_refines hInput hPostAccess hNoWrap

end Eip803x.Refinement.SStorePricing
