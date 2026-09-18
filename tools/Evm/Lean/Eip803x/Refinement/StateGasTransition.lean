-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Generated.StateGasTransitionKernel
import Eip803x.Production
import Lean.Elab.Tactic.Omega

namespace Eip803x.Refinement.StateGasTransition

open Eip803x

namespace Spec

/-- The result observed after a pure state-gas transition. -/
structure Result where
  value : Nat
  stateReservoir : Int
  stateGasUsed : Int
  stateGasSpill : Int
  stateGasSpillRefunded : Int
  unappliedAmount : Int
  deriving DecidableEq, Repr

/-- The child fields used by frame restoration paths, which deliberately have no execution-gas value. -/
structure Child where
  stateReservoir : Int
  stateGasUsed : Int
  stateGasSpill : Int
  stateGasSpillRefunded : Int
  deriving DecidableEq, Repr

def result (state : ProductionGasState) (unappliedAmount : Int := 0) : Result :=
  { value := state.gasLeft
    stateReservoir := state.stateReservoir
    stateGasUsed := state.stateGasUsed
    stateGasSpill := state.stateGasSpill
    stateGasSpillRefunded := state.stateGasSpillRefunded
    unappliedAmount }

def resultState (result : Result) : ProductionGasState :=
  { gasLeft := result.value
    stateReservoir := result.stateReservoir
    stateGasUsed := result.stateGasUsed
    stateGasSpill := result.stateGasSpill
    stateGasSpillRefunded := result.stateGasSpillRefunded }

def child (state : ProductionGasState) : Child :=
  { stateReservoir := state.stateReservoir
    stateGasUsed := state.stateGasUsed
    stateGasSpill := state.stateGasSpill
    stateGasSpillRefunded := state.stateGasSpillRefunded }

def positivePart (value : Int) : Int :=
  if value > 0 then value else 0

def unrefundedSpill (stateGasSpill stateGasSpillRefunded : Int) : Int :=
  positivePart (stateGasSpill - stateGasSpillRefunded)

def refund (parent child : ProductionGasState) : Result :=
  { value := parent.gasLeft + child.gasLeft
    stateReservoir := parent.stateReservoir + child.stateReservoir
    stateGasUsed := parent.stateGasUsed + child.stateGasUsed
    stateGasSpill := parent.stateGasSpill + child.stateGasSpill
    stateGasSpillRefunded := parent.stateGasSpillRefunded + child.stateGasSpillRefunded
    unappliedAmount := 0 }

def repayStateGasSpill (state : ProductionGasState) : Result :=
  let repayment := min state.stateReservoir
    (unrefundedSpill state.stateGasSpill state.stateGasSpillRefunded)
  if repayment <= 0 then
    result state
  else
    { value := state.gasLeft + Int.toNat repayment
      stateReservoir := state.stateReservoir - repayment
      stateGasUsed := state.stateGasUsed
      stateGasSpill := state.stateGasSpill
      stateGasSpillRefunded := state.stateGasSpillRefunded + repayment
      unappliedAmount := 0 }

def restoreChildStateGas (parent : ProductionGasState) (child : Child) : Result :=
  let childNetSpill := unrefundedSpill child.stateGasSpill child.stateGasSpillRefunded
  { value := parent.gasLeft + Int.toNat childNetSpill
    stateReservoir := parent.stateReservoir + child.stateReservoir + child.stateGasUsed - childNetSpill
    stateGasUsed := parent.stateGasUsed
    stateGasSpill := parent.stateGasSpill
    stateGasSpillRefunded := parent.stateGasSpillRefunded
    unappliedAmount := 0 }

def restoreChildStateGasOnHalt (parent : ProductionGasState) (child : Child) : Result :=
  let childNetSpill := unrefundedSpill child.stateGasSpill child.stateGasSpillRefunded
  { value := parent.gasLeft
    stateReservoir := parent.stateReservoir + child.stateReservoir + child.stateGasUsed - childNetSpill
    stateGasUsed := parent.stateGasUsed
    stateGasSpill := parent.stateGasSpill
    stateGasSpillRefunded := parent.stateGasSpillRefunded
    unappliedAmount := 0 }

def revertRefundToHalt (parent : ProductionGasState) (child : Child) : Result :=
  let childNetSpill := unrefundedSpill child.stateGasSpill child.stateGasSpillRefunded
  { value := parent.gasLeft
    stateReservoir := parent.stateReservoir + child.stateGasUsed - childNetSpill
    stateGasUsed := parent.stateGasUsed - child.stateGasUsed
    stateGasSpill := parent.stateGasSpill - child.stateGasSpill
    stateGasSpillRefunded := parent.stateGasSpillRefunded - child.stateGasSpillRefunded
    unappliedAmount := 0 }

def refundableStateGas (stateGasUsed stateGasFloor : Int) : Int :=
  positivePart (stateGasUsed - stateGasFloor)

def refundStateGas (state : ProductionGasState) (amount stateGasFloor : Int)
    (trackSpillRefund : Bool) : Result :=
  let appliedRefund := min amount (refundableStateGas state.stateGasUsed stateGasFloor)
  let toGasLeft := if trackSpillRefund then
    min appliedRefund (unrefundedSpill state.stateGasSpill state.stateGasSpillRefunded)
  else
    0
  { value := state.gasLeft + Int.toNat toGasLeft
    stateReservoir := state.stateReservoir + (appliedRefund - toGasLeft)
    stateGasUsed := state.stateGasUsed - appliedRefund
    stateGasSpill := state.stateGasSpill
    stateGasSpillRefunded := if trackSpillRefund then
      state.stateGasSpillRefunded + toGasLeft
    else
      state.stateGasSpillRefunded
    unappliedAmount := 0 }

def discardStateGas (state : ProductionGasState) (amount stateGasFloor : Int) : Result :=
  let appliedRefund := min amount (refundableStateGas state.stateGasUsed stateGasFloor)
  { value := state.gasLeft
    stateReservoir := state.stateReservoir
    stateGasUsed := state.stateGasUsed - appliedRefund
    stateGasSpill := state.stateGasSpill
    stateGasSpillRefunded := state.stateGasSpillRefunded
    unappliedAmount := amount - appliedRefund }

def addStateGasRefundToReservoir (state : ProductionGasState) (amount : Int)
    (trackSpillRefund : Bool) : Result :=
  let toGasLeft := if trackSpillRefund then
    min amount (unrefundedSpill state.stateGasSpill state.stateGasSpillRefunded)
  else
    0
  { value := state.gasLeft + Int.toNat toGasLeft
    stateReservoir := state.stateReservoir + (amount - toGasLeft)
    stateGasUsed := state.stateGasUsed
    stateGasSpill := state.stateGasSpill
    stateGasSpillRefunded := if trackSpillRefund then
      state.stateGasSpillRefunded + toGasLeft
    else
      state.stateGasSpillRefunded
    unappliedAmount := 0 }

def clampToRefundAmount (stateReservoir amount : Int) : Int :=
  if stateReservoir <= 0 then 0
  else if stateReservoir >= amount then amount
  else stateReservoir

def removeStateGasRefundFromReservoir (state : ProductionGasState) (amount : Int) : Result :=
  let fromReservoir := clampToRefundAmount state.stateReservoir amount
  let remainingAmount := amount - fromReservoir
  let nextStateReservoir := state.stateReservoir - fromReservoir
  if remainingAmount <= 0 then
    { value := state.gasLeft
      stateReservoir := nextStateReservoir
      stateGasUsed := state.stateGasUsed
      stateGasSpill := state.stateGasSpill
      stateGasSpillRefunded := state.stateGasSpillRefunded
      unappliedAmount := 0 }
  else
    let fromUsed := min remainingAmount state.stateGasUsed
    { value := state.gasLeft
      stateReservoir := nextStateReservoir - (remainingAmount - fromUsed)
      stateGasUsed := state.stateGasUsed - fromUsed
      stateGasSpill := state.stateGasSpill
      stateGasSpillRefunded := state.stateGasSpillRefunded
      unappliedAmount := 0 }

def resultFits (result : Result) : Prop :=
  ProductionGas.FitsUInt64 result.value ∧
    ProductionGas.FitsInt64 result.stateReservoir ∧
      ProductionGas.FitsInt64 result.stateGasUsed ∧
        ProductionGas.FitsInt64 result.stateGasSpill ∧
          ProductionGas.FitsInt64 result.stateGasSpillRefunded ∧
            ProductionGas.FitsInt64 result.unappliedAmount

def childBounded (child : Child) : Prop :=
  ProductionGas.FitsInt64 child.stateReservoir ∧
    ProductionGas.FitsInt64 child.stateGasUsed ∧
      ProductionGas.FitsInt64 child.stateGasSpill ∧
        ProductionGas.FitsInt64 child.stateGasSpillRefunded

def refundNoOverflow (parent child : ProductionGasState) : Prop :=
  ProductionGas.MachineBounded parent ∧
    ProductionGas.MachineBounded child ∧ resultFits (refund parent child)

def repayStateGasSpillNoOverflow (state : ProductionGasState) : Prop :=
  ProductionGas.MachineBounded state ∧
    ProductionGas.FitsInt64 (state.stateGasSpill - state.stateGasSpillRefunded) ∧
      let repayment := min state.stateReservoir
        (unrefundedSpill state.stateGasSpill state.stateGasSpillRefunded)
      ProductionGas.FitsInt64 repayment ∧ resultFits (repayStateGasSpill state)

def restoreChildStateGasNoOverflow (parent : ProductionGasState) (child : Child) : Prop :=
  ProductionGas.MachineBounded parent ∧ childBounded child ∧
    ProductionGas.FitsInt64 (child.stateGasSpill - child.stateGasSpillRefunded) ∧
      ProductionGas.FitsInt64 (unrefundedSpill child.stateGasSpill child.stateGasSpillRefunded) ∧
        ProductionGas.FitsInt64 (parent.stateReservoir + child.stateReservoir) ∧
          ProductionGas.FitsInt64
            (parent.stateReservoir + child.stateReservoir + child.stateGasUsed) ∧
            resultFits (restoreChildStateGas parent child)

def restoreChildStateGasOnHaltNoOverflow (parent : ProductionGasState) (child : Child) : Prop :=
  ProductionGas.MachineBounded parent ∧ childBounded child ∧
    ProductionGas.FitsInt64 (child.stateGasSpill - child.stateGasSpillRefunded) ∧
      ProductionGas.FitsInt64 (unrefundedSpill child.stateGasSpill child.stateGasSpillRefunded) ∧
        ProductionGas.FitsInt64 (parent.stateReservoir + child.stateReservoir) ∧
          ProductionGas.FitsInt64
            (parent.stateReservoir + child.stateReservoir + child.stateGasUsed) ∧
            resultFits (restoreChildStateGasOnHalt parent child)

def revertRefundToHaltNoOverflow (parent : ProductionGasState) (child : Child) : Prop :=
  ProductionGas.MachineBounded parent ∧ childBounded child ∧
    ProductionGas.FitsInt64 (child.stateGasSpill - child.stateGasSpillRefunded) ∧
      ProductionGas.FitsInt64 (unrefundedSpill child.stateGasSpill child.stateGasSpillRefunded) ∧
        ProductionGas.FitsInt64 (parent.stateReservoir + child.stateGasUsed) ∧
          resultFits (revertRefundToHalt parent child)

def refundStateGasNoOverflow (state : ProductionGasState) (amount stateGasFloor : Int)
    (trackSpillRefund : Bool) : Prop :=
  ProductionGas.MachineBounded state ∧ ProductionGas.FitsInt64 amount ∧
    ProductionGas.FitsInt64 stateGasFloor ∧
      ProductionGas.FitsInt64 (state.stateGasUsed - stateGasFloor) ∧
        ProductionGas.FitsInt64 (state.stateGasSpill - state.stateGasSpillRefunded) ∧
          let appliedRefund := min amount (refundableStateGas state.stateGasUsed stateGasFloor)
          let toGasLeft := if trackSpillRefund then
            min appliedRefund (unrefundedSpill state.stateGasSpill state.stateGasSpillRefunded)
          else 0
          0 ≤ toGasLeft ∧ ProductionGas.FitsInt64 toGasLeft ∧
            ProductionGas.FitsInt64 (appliedRefund - toGasLeft) ∧
              resultFits (refundStateGas state amount stateGasFloor trackSpillRefund)

def discardStateGasNoOverflow (state : ProductionGasState) (amount stateGasFloor : Int) : Prop :=
  ProductionGas.MachineBounded state ∧ ProductionGas.FitsInt64 amount ∧
    ProductionGas.FitsInt64 stateGasFloor ∧
      ProductionGas.FitsInt64 (state.stateGasUsed - stateGasFloor) ∧
        resultFits (discardStateGas state amount stateGasFloor)

def addStateGasRefundToReservoirNoOverflow (state : ProductionGasState) (amount : Int)
    (trackSpillRefund : Bool) : Prop :=
  ProductionGas.MachineBounded state ∧ ProductionGas.FitsInt64 amount ∧
    ProductionGas.FitsInt64 (state.stateGasSpill - state.stateGasSpillRefunded) ∧
      let toGasLeft := if trackSpillRefund then
        min amount (unrefundedSpill state.stateGasSpill state.stateGasSpillRefunded)
      else 0
      0 ≤ toGasLeft ∧ ProductionGas.FitsInt64 toGasLeft ∧
        ProductionGas.FitsInt64 (amount - toGasLeft) ∧
          resultFits (addStateGasRefundToReservoir state amount trackSpillRefund)

def removeStateGasRefundFromReservoirNoOverflow (state : ProductionGasState) (amount : Int) : Prop :=
  ProductionGas.MachineBounded state ∧ ProductionGas.FitsInt64 amount ∧
    let fromReservoir := clampToRefundAmount state.stateReservoir amount
    let remainingAmount := amount - fromReservoir
    let nextStateReservoir := state.stateReservoir - fromReservoir
    ProductionGas.FitsInt64 remainingAmount ∧ ProductionGas.FitsInt64 nextStateReservoir ∧
      ProductionGas.FitsInt64 (remainingAmount - min remainingAmount state.stateGasUsed) ∧
        resultFits (removeStateGasRefundFromReservoir state amount)

/-- Mutation-sensitive field equations for child success merging. -/
theorem refund_fields (parent child : ProductionGasState) :
    (refund parent child).value = parent.gasLeft + child.gasLeft ∧
      (refund parent child).stateReservoir = parent.stateReservoir + child.stateReservoir ∧
        (refund parent child).stateGasUsed = parent.stateGasUsed + child.stateGasUsed ∧
          (refund parent child).stateGasSpill = parent.stateGasSpill + child.stateGasSpill ∧
            (refund parent child).stateGasSpillRefunded =
              parent.stateGasSpillRefunded + child.stateGasSpillRefunded := by
  exact ⟨rfl, rfl, rfl, rfl, rfl⟩

/-- Explicit reversion returns only the child's outstanding spill to execution gas. -/
theorem restoreChildStateGas_fields (parent : ProductionGasState) (child : Child) :
    (restoreChildStateGas parent child).value = parent.gasLeft +
        Int.toNat (unrefundedSpill child.stateGasSpill child.stateGasSpillRefunded) ∧
      (restoreChildStateGas parent child).stateReservoir =
        parent.stateReservoir + child.stateReservoir + child.stateGasUsed -
          unrefundedSpill child.stateGasSpill child.stateGasSpillRefunded := by
  exact ⟨rfl, rfl⟩

/-- Exceptional child exit must not return its execution gas. -/
theorem restoreChildStateGasOnHalt_preserves_parent_execution
    (parent : ProductionGasState) (child : Child) :
    (restoreChildStateGasOnHalt parent child).value = parent.gasLeft := rfl

/-- Spill-refund tracking is the only branch allowed to mutate the refunded-spill counter. -/
theorem refundStateGas_spill_refund_field
    (state : ProductionGasState) (amount stateGasFloor : Int) (trackSpillRefund : Bool) :
    (refundStateGas state amount stateGasFloor trackSpillRefund).stateGasSpillRefunded =
      let appliedRefund := min amount (refundableStateGas state.stateGasUsed stateGasFloor)
      let toGasLeft := if trackSpillRefund then
        min appliedRefund (unrefundedSpill state.stateGasSpill state.stateGasSpillRefunded)
      else 0
      if trackSpillRefund then state.stateGasSpillRefunded + toGasLeft
      else state.stateGasSpillRefunded := rfl

/-- Discarded state gas leaves a precisely reported remainder for the adapter. -/
theorem discardStateGas_reports_unapplied
    (state : ProductionGasState) (amount stateGasFloor : Int) :
    (discardStateGas state amount stateGasFloor).unappliedAmount =
      amount - min amount (refundableStateGas state.stateGasUsed stateGasFloor) := rfl

/-
The common `GasState` has natural counters, while this kernel is deliberately
production-shaped: it carries signed reservoir, spill, and refund deltas and
does not receive a `FrameGasState` baseline or journal.  `ProductionGas.Represents`
is therefore the bridge for later frame-level refinement; the equations above
are the complete local transition contract, not a claim that a partial adapter
is a whole-frame proof.
-/

end Spec

abbrev GeneratedResult := Eip803x.Generated.StateGasTransitionKernel.StateGasTransitionResult

def generatedResultToSpec (result : GeneratedResult) : Spec.Result :=
  { value := result.value
    stateReservoir := result.stateReservoir
    stateGasUsed := result.stateGasUsed
    stateGasSpill := result.stateGasSpill
    stateGasSpillRefunded := result.stateGasSpillRefunded
    unappliedAmount := result.unappliedAmount }

private theorem wrapInt64_of_fits {value : Int}
    (h : ProductionGas.FitsInt64 value) :
    Eip803x.Generated.StateGasTransitionKernel.wrapInt64 value = value := by
  unfold Eip803x.Generated.StateGasTransitionKernel.wrapInt64
  have hBounds :
      Eip803x.Generated.StateGasTransitionKernel.int64Min ≤ value ∧
        value ≤ Eip803x.Generated.StateGasTransitionKernel.int64Max := by
    simpa [ProductionGas.FitsInt64, ProductionGas.int64Min, ProductionGas.int64Max,
      Eip803x.Generated.StateGasTransitionKernel.int64Min,
      Eip803x.Generated.StateGasTransitionKernel.int64Max,
      Eip803x.Generated.StateGasTransitionKernel.int64SignBit] using h
  simp [hBounds]

private theorem normalizeUInt64_of_fits {value : Nat}
    (h : ProductionGas.FitsUInt64 value) :
    Eip803x.Generated.StateGasTransitionKernel.normalizeUInt64 value = value := by
  unfold Eip803x.Generated.StateGasTransitionKernel.normalizeUInt64
  have hBound : value ≤ Eip803x.Generated.StateGasTransitionKernel.uint64Max := by
    simpa [ProductionGas.FitsUInt64, ProductionGas.uint64Max,
      Eip803x.Generated.StateGasTransitionKernel.uint64Max,
      Eip803x.Generated.StateGasTransitionKernel.uint64Modulus] using h
  simp [hBound]

private theorem wrapUInt64_of_nonnegative_fits {value : Int}
    (hNonnegative : 0 ≤ value)
    (hUpper : value ≤ Eip803x.Generated.StateGasTransitionKernel.uint64Max) :
    Eip803x.Generated.StateGasTransitionKernel.wrapUInt64 value = Int.toNat value := by
  unfold Eip803x.Generated.StateGasTransitionKernel.wrapUInt64
  have hBounds : 0 ≤ value ∧ value ≤ (Eip803x.Generated.StateGasTransitionKernel.uint64Max : Int) :=
    ⟨hNonnegative, hUpper⟩
  simp [hBounds]

private theorem addInt64_of_fits {left right : Int}
    (h : ProductionGas.FitsInt64 (left + right)) :
    Eip803x.Generated.StateGasTransitionKernel.addInt64 left right = left + right := by
  unfold Eip803x.Generated.StateGasTransitionKernel.addInt64
  exact wrapInt64_of_fits h

private theorem subInt64_of_fits {left right : Int}
    (h : ProductionGas.FitsInt64 (left - right)) :
    Eip803x.Generated.StateGasTransitionKernel.subInt64 left right = left - right := by
  unfold Eip803x.Generated.StateGasTransitionKernel.subInt64
  exact wrapInt64_of_fits h

private theorem int64ToUInt64_of_nonnegative_fits {value : Int}
    (hNonnegative : 0 ≤ value) (h : ProductionGas.FitsInt64 value) :
    Eip803x.Generated.StateGasTransitionKernel.int64ToUInt64 value = Int.toNat value := by
  unfold Eip803x.Generated.StateGasTransitionKernel.int64ToUInt64
  apply wrapUInt64_of_nonnegative_fits hNonnegative
  unfold ProductionGas.FitsInt64 ProductionGas.int64Max at h
  unfold Eip803x.Generated.StateGasTransitionKernel.uint64Max
    Eip803x.Generated.StateGasTransitionKernel.uint64Modulus
  omega

private theorem addUInt64_of_fits {left right : Nat}
    (h : ProductionGas.FitsUInt64 (left + right)) :
    Eip803x.Generated.StateGasTransitionKernel.addUInt64 left right = left + right := by
  unfold Eip803x.Generated.StateGasTransitionKernel.addUInt64
  have hNonnegative : (0 : Int) ≤ (left : Int) + right := by omega
  have hUpper : (left : Int) + right ≤
      (Eip803x.Generated.StateGasTransitionKernel.uint64Max : Int) := by
    unfold ProductionGas.FitsUInt64 ProductionGas.uint64Max at h
    unfold Eip803x.Generated.StateGasTransitionKernel.uint64Max
      Eip803x.Generated.StateGasTransitionKernel.uint64Modulus
    omega
  rw [wrapUInt64_of_nonnegative_fits hNonnegative hUpper]
  omega

private theorem unrefundedSpill_nonnegative (stateGasSpill stateGasSpillRefunded : Int) :
    0 ≤ Spec.unrefundedSpill stateGasSpill stateGasSpillRefunded := by
  unfold Spec.unrefundedSpill Spec.positivePart
  split <;> omega

theorem generated_refund_matches_spec
    {parent child : ProductionGasState}
    (hNoOverflow : Spec.refundNoOverflow parent child) :
    generatedResultToSpec
        (Eip803x.Generated.StateGasTransitionKernel.refund
          parent.gasLeft parent.stateReservoir parent.stateGasUsed parent.stateGasSpill
          parent.stateGasSpillRefunded child.gasLeft child.stateReservoir child.stateGasUsed
          child.stateGasSpill child.stateGasSpillRefunded) =
      Spec.refund parent child := by
  rcases hNoOverflow with ⟨hParent, hChild, hResult⟩
  rcases hParent with ⟨hParentValue, hParentReservoir, hParentUsed, hParentSpill, hParentRefunded⟩
  rcases hChild with ⟨hChildValue, hChildReservoir, hChildUsed, hChildSpill, hChildRefunded⟩
  rcases hResult with ⟨hValue, hReservoir, hUsed, hSpill, hRefunded, hUnapplied⟩
  rw [Eip803x.Generated.StateGasTransitionKernel.refund]
  rw [normalizeUInt64_of_fits hParentValue, wrapInt64_of_fits hParentReservoir,
    wrapInt64_of_fits hParentUsed, wrapInt64_of_fits hParentSpill,
    wrapInt64_of_fits hParentRefunded, normalizeUInt64_of_fits hChildValue,
    wrapInt64_of_fits hChildReservoir, wrapInt64_of_fits hChildUsed,
    wrapInt64_of_fits hChildSpill, wrapInt64_of_fits hChildRefunded]
  change generatedResultToSpec
      { value := Eip803x.Generated.StateGasTransitionKernel.addUInt64 parent.gasLeft child.gasLeft
        stateReservoir := Eip803x.Generated.StateGasTransitionKernel.addInt64
          parent.stateReservoir child.stateReservoir
        stateGasUsed := Eip803x.Generated.StateGasTransitionKernel.addInt64
          parent.stateGasUsed child.stateGasUsed
        stateGasSpill := Eip803x.Generated.StateGasTransitionKernel.addInt64
          parent.stateGasSpill child.stateGasSpill
        stateGasSpillRefunded := Eip803x.Generated.StateGasTransitionKernel.addInt64
          parent.stateGasSpillRefunded child.stateGasSpillRefunded
        unappliedAmount := 0 } = Spec.refund parent child
  rw [addUInt64_of_fits hValue, addInt64_of_fits hReservoir, addInt64_of_fits hUsed,
    addInt64_of_fits hSpill, addInt64_of_fits hRefunded]
  rfl

theorem generated_repayStateGasSpill_matches_spec
    {state : ProductionGasState}
    (hNoOverflow : Spec.repayStateGasSpillNoOverflow state) :
    generatedResultToSpec
        (Eip803x.Generated.StateGasTransitionKernel.repayStateGasSpill
          state.gasLeft state.stateReservoir state.stateGasUsed state.stateGasSpill
          state.stateGasSpillRefunded) =
      Spec.repayStateGasSpill state := by
  rcases hNoOverflow with ⟨hState, hDifference, hRepaymentFits, hResult⟩
  rcases hState with ⟨hValue, hReservoir, hUsed, hSpill, hRefunded⟩
  rw [Eip803x.Generated.StateGasTransitionKernel.repayStateGasSpill]
  rw [normalizeUInt64_of_fits hValue, wrapInt64_of_fits hReservoir,
    wrapInt64_of_fits hUsed, wrapInt64_of_fits hSpill, wrapInt64_of_fits hRefunded]
  change generatedResultToSpec
      (let repayment := min state.stateReservoir
        (if Eip803x.Generated.StateGasTransitionKernel.subInt64
            state.stateGasSpill state.stateGasSpillRefunded > 0 then
          Eip803x.Generated.StateGasTransitionKernel.subInt64
            state.stateGasSpill state.stateGasSpillRefunded
        else 0)
       if repayment <= 0 then
         { value := state.gasLeft
           stateReservoir := state.stateReservoir
           stateGasUsed := state.stateGasUsed
           stateGasSpill := state.stateGasSpill
           stateGasSpillRefunded := state.stateGasSpillRefunded
           unappliedAmount := 0 }
       else
         { value := Eip803x.Generated.StateGasTransitionKernel.addUInt64 state.gasLeft
             (Eip803x.Generated.StateGasTransitionKernel.int64ToUInt64 repayment)
           stateReservoir := Eip803x.Generated.StateGasTransitionKernel.subInt64
             state.stateReservoir repayment
           stateGasUsed := state.stateGasUsed
           stateGasSpill := state.stateGasSpill
           stateGasSpillRefunded := Eip803x.Generated.StateGasTransitionKernel.addInt64
             state.stateGasSpillRefunded repayment
           unappliedAmount := 0 }) = Spec.repayStateGasSpill state
  have hUnrefunded :
      (if Eip803x.Generated.StateGasTransitionKernel.subInt64
          state.stateGasSpill state.stateGasSpillRefunded > 0 then
        Eip803x.Generated.StateGasTransitionKernel.subInt64
          state.stateGasSpill state.stateGasSpillRefunded
      else 0) = Spec.unrefundedSpill state.stateGasSpill state.stateGasSpillRefunded := by
    rw [subInt64_of_fits hDifference]
    rfl
  rw [hUnrefunded]
  let repayment := min state.stateReservoir
    (Spec.unrefundedSpill state.stateGasSpill state.stateGasSpillRefunded)
  change generatedResultToSpec
      (if repayment <= 0 then
        { value := state.gasLeft
          stateReservoir := state.stateReservoir
          stateGasUsed := state.stateGasUsed
          stateGasSpill := state.stateGasSpill
          stateGasSpillRefunded := state.stateGasSpillRefunded
          unappliedAmount := 0 }
      else
        { value := Eip803x.Generated.StateGasTransitionKernel.addUInt64 state.gasLeft
            (Eip803x.Generated.StateGasTransitionKernel.int64ToUInt64 repayment)
          stateReservoir := Eip803x.Generated.StateGasTransitionKernel.subInt64
            state.stateReservoir repayment
          stateGasUsed := state.stateGasUsed
          stateGasSpill := state.stateGasSpill
          stateGasSpillRefunded := Eip803x.Generated.StateGasTransitionKernel.addInt64
            state.stateGasSpillRefunded repayment
          unappliedAmount := 0 }) =
    if repayment <= 0 then Spec.result state else
      { value := state.gasLeft + Int.toNat repayment
        stateReservoir := state.stateReservoir - repayment
        stateGasUsed := state.stateGasUsed
        stateGasSpill := state.stateGasSpill
        stateGasSpillRefunded := state.stateGasSpillRefunded + repayment
        unappliedAmount := 0 }
  change Spec.resultFits
      (if repayment <= 0 then Spec.result state else
        { value := state.gasLeft + Int.toNat repayment
          stateReservoir := state.stateReservoir - repayment
          stateGasUsed := state.stateGasUsed
          stateGasSpill := state.stateGasSpill
          stateGasSpillRefunded := state.stateGasSpillRefunded + repayment
          unappliedAmount := 0 }) at hResult
  by_cases hRepayment : repayment <= 0
  · simp [hRepayment]
    rfl
  · have hRepaymentPositive : 0 < repayment := by omega
    rw [if_neg hRepayment] at hResult
    rcases hResult with ⟨hResultValue, hResultReservoir, _hResultUsed,
      _hResultSpill, hResultRefunded, _hResultUnapplied⟩
    simp only [hRepayment, ↓reduceIte, generatedResultToSpec, Spec.Result.mk.injEq]
    change ProductionGas.FitsInt64 repayment at hRepaymentFits
    change ProductionGas.FitsUInt64 (state.gasLeft + Int.toNat repayment) at hResultValue
    change ProductionGas.FitsInt64 (state.stateReservoir - repayment) at hResultReservoir
    change ProductionGas.FitsInt64 (state.stateGasSpillRefunded + repayment) at hResultRefunded
    rw [int64ToUInt64_of_nonnegative_fits (Int.le_of_lt hRepaymentPositive) hRepaymentFits]
    rw [addUInt64_of_fits hResultValue, subInt64_of_fits hResultReservoir,
      addInt64_of_fits hResultRefunded]
    simp

theorem generated_restoreChildStateGas_matches_spec
    {parent : ProductionGasState} {child : Spec.Child}
    (hNoOverflow : Spec.restoreChildStateGasNoOverflow parent child) :
    generatedResultToSpec
        (Eip803x.Generated.StateGasTransitionKernel.restoreChildStateGas
          parent.gasLeft parent.stateReservoir parent.stateGasUsed parent.stateGasSpill
          parent.stateGasSpillRefunded child.stateReservoir child.stateGasUsed
          child.stateGasSpill child.stateGasSpillRefunded) =
      Spec.restoreChildStateGas parent child := by
  rcases hNoOverflow with ⟨hParent, hChild, hDifference, hChildNetSpill,
    hFirstReservoirAdd, hSecondReservoirAdd, hResult⟩
  rcases hParent with ⟨hParentValue, hParentReservoir, hParentUsed, hParentSpill,
    hParentRefunded⟩
  rcases hChild with ⟨hChildReservoir, hChildUsed, hChildSpill, hChildRefunded⟩
  rcases hResult with ⟨hResultValue, hResultReservoir, _hResultUsed,
    _hResultSpill, _hResultRefunded, _hResultUnapplied⟩
  rw [Eip803x.Generated.StateGasTransitionKernel.restoreChildStateGas]
  rw [normalizeUInt64_of_fits hParentValue, wrapInt64_of_fits hParentReservoir,
    wrapInt64_of_fits hParentUsed, wrapInt64_of_fits hParentSpill,
    wrapInt64_of_fits hParentRefunded, wrapInt64_of_fits hChildReservoir,
    wrapInt64_of_fits hChildUsed, wrapInt64_of_fits hChildSpill,
    wrapInt64_of_fits hChildRefunded]
  change generatedResultToSpec
      (let childNetSpill :=
        if Eip803x.Generated.StateGasTransitionKernel.subInt64
            child.stateGasSpill child.stateGasSpillRefunded > 0 then
          Eip803x.Generated.StateGasTransitionKernel.subInt64
            child.stateGasSpill child.stateGasSpillRefunded
        else 0
       { value := Eip803x.Generated.StateGasTransitionKernel.addUInt64 parent.gasLeft
           (Eip803x.Generated.StateGasTransitionKernel.int64ToUInt64 childNetSpill)
         stateReservoir := Eip803x.Generated.StateGasTransitionKernel.subInt64
           (Eip803x.Generated.StateGasTransitionKernel.addInt64
             (Eip803x.Generated.StateGasTransitionKernel.addInt64
               parent.stateReservoir child.stateReservoir)
             child.stateGasUsed)
           childNetSpill
         stateGasUsed := parent.stateGasUsed
         stateGasSpill := parent.stateGasSpill
         stateGasSpillRefunded := parent.stateGasSpillRefunded
         unappliedAmount := 0 }) = Spec.restoreChildStateGas parent child
  have hUnrefunded :
      (if Eip803x.Generated.StateGasTransitionKernel.subInt64
          child.stateGasSpill child.stateGasSpillRefunded > 0 then
        Eip803x.Generated.StateGasTransitionKernel.subInt64
          child.stateGasSpill child.stateGasSpillRefunded
      else 0) = Spec.unrefundedSpill child.stateGasSpill child.stateGasSpillRefunded := by
    rw [subInt64_of_fits hDifference]
    rfl
  rw [hUnrefunded]
  change generatedResultToSpec
      { value := Eip803x.Generated.StateGasTransitionKernel.addUInt64 parent.gasLeft
          (Eip803x.Generated.StateGasTransitionKernel.int64ToUInt64
            (Spec.unrefundedSpill child.stateGasSpill child.stateGasSpillRefunded))
        stateReservoir := Eip803x.Generated.StateGasTransitionKernel.subInt64
          (Eip803x.Generated.StateGasTransitionKernel.addInt64
            (Eip803x.Generated.StateGasTransitionKernel.addInt64
              parent.stateReservoir child.stateReservoir)
            child.stateGasUsed)
          (Spec.unrefundedSpill child.stateGasSpill child.stateGasSpillRefunded)
        stateGasUsed := parent.stateGasUsed
        stateGasSpill := parent.stateGasSpill
        stateGasSpillRefunded := parent.stateGasSpillRefunded
        unappliedAmount := 0 } = Spec.restoreChildStateGas parent child
  change ProductionGas.FitsUInt64
      (parent.gasLeft + Int.toNat
        (Spec.unrefundedSpill child.stateGasSpill child.stateGasSpillRefunded)) at hResultValue
  change ProductionGas.FitsInt64
      (parent.stateReservoir + child.stateReservoir + child.stateGasUsed -
        Spec.unrefundedSpill child.stateGasSpill child.stateGasSpillRefunded) at hResultReservoir
  rw [int64ToUInt64_of_nonnegative_fits
    (unrefundedSpill_nonnegative child.stateGasSpill child.stateGasSpillRefunded) hChildNetSpill]
  rw [addUInt64_of_fits hResultValue, addInt64_of_fits hFirstReservoirAdd,
    addInt64_of_fits hSecondReservoirAdd, subInt64_of_fits hResultReservoir]
  rfl

theorem generated_restoreChildStateGasOnHalt_matches_spec
    {parent : ProductionGasState} {child : Spec.Child}
    (hNoOverflow : Spec.restoreChildStateGasOnHaltNoOverflow parent child) :
    generatedResultToSpec
        (Eip803x.Generated.StateGasTransitionKernel.restoreChildStateGasOnHalt
          parent.gasLeft parent.stateReservoir parent.stateGasUsed parent.stateGasSpill
          parent.stateGasSpillRefunded child.stateReservoir child.stateGasUsed
          child.stateGasSpill child.stateGasSpillRefunded) =
      Spec.restoreChildStateGasOnHalt parent child := by
  rcases hNoOverflow with ⟨hParent, hChild, hDifference, hChildNetSpill,
    hFirstReservoirAdd, hSecondReservoirAdd, hResult⟩
  rcases hParent with ⟨hParentValue, hParentReservoir, hParentUsed, hParentSpill,
    hParentRefunded⟩
  rcases hChild with ⟨hChildReservoir, hChildUsed, hChildSpill, hChildRefunded⟩
  rcases hResult with ⟨_hResultValue, hResultReservoir, _hResultUsed,
    _hResultSpill, _hResultRefunded, _hResultUnapplied⟩
  rw [Eip803x.Generated.StateGasTransitionKernel.restoreChildStateGasOnHalt]
  rw [normalizeUInt64_of_fits hParentValue, wrapInt64_of_fits hParentReservoir,
    wrapInt64_of_fits hParentUsed, wrapInt64_of_fits hParentSpill,
    wrapInt64_of_fits hParentRefunded, wrapInt64_of_fits hChildReservoir,
    wrapInt64_of_fits hChildUsed, wrapInt64_of_fits hChildSpill,
    wrapInt64_of_fits hChildRefunded]
  change generatedResultToSpec
      (let childNetSpill :=
        if Eip803x.Generated.StateGasTransitionKernel.subInt64
            child.stateGasSpill child.stateGasSpillRefunded > 0 then
          Eip803x.Generated.StateGasTransitionKernel.subInt64
            child.stateGasSpill child.stateGasSpillRefunded
        else 0
       { value := parent.gasLeft
         stateReservoir := Eip803x.Generated.StateGasTransitionKernel.subInt64
           (Eip803x.Generated.StateGasTransitionKernel.addInt64
             (Eip803x.Generated.StateGasTransitionKernel.addInt64
               parent.stateReservoir child.stateReservoir)
             child.stateGasUsed)
           childNetSpill
         stateGasUsed := parent.stateGasUsed
         stateGasSpill := parent.stateGasSpill
         stateGasSpillRefunded := parent.stateGasSpillRefunded
         unappliedAmount := 0 }) = Spec.restoreChildStateGasOnHalt parent child
  have hUnrefunded :
      (if Eip803x.Generated.StateGasTransitionKernel.subInt64
          child.stateGasSpill child.stateGasSpillRefunded > 0 then
        Eip803x.Generated.StateGasTransitionKernel.subInt64
          child.stateGasSpill child.stateGasSpillRefunded
      else 0) = Spec.unrefundedSpill child.stateGasSpill child.stateGasSpillRefunded := by
    rw [subInt64_of_fits hDifference]
    rfl
  rw [hUnrefunded]
  change generatedResultToSpec
      { value := parent.gasLeft
        stateReservoir := Eip803x.Generated.StateGasTransitionKernel.subInt64
          (Eip803x.Generated.StateGasTransitionKernel.addInt64
            (Eip803x.Generated.StateGasTransitionKernel.addInt64
              parent.stateReservoir child.stateReservoir)
            child.stateGasUsed)
          (Spec.unrefundedSpill child.stateGasSpill child.stateGasSpillRefunded)
        stateGasUsed := parent.stateGasUsed
        stateGasSpill := parent.stateGasSpill
        stateGasSpillRefunded := parent.stateGasSpillRefunded
        unappliedAmount := 0 } = Spec.restoreChildStateGasOnHalt parent child
  change ProductionGas.FitsInt64
      (parent.stateReservoir + child.stateReservoir + child.stateGasUsed -
        Spec.unrefundedSpill child.stateGasSpill child.stateGasSpillRefunded) at hResultReservoir
  rw [addInt64_of_fits hFirstReservoirAdd, addInt64_of_fits hSecondReservoirAdd,
    subInt64_of_fits hResultReservoir]
  rfl

theorem generated_revertRefundToHalt_matches_spec
    {parent : ProductionGasState} {child : Spec.Child}
    (hNoOverflow : Spec.revertRefundToHaltNoOverflow parent child) :
    generatedResultToSpec
        (Eip803x.Generated.StateGasTransitionKernel.revertRefundToHalt
          parent.gasLeft parent.stateReservoir parent.stateGasUsed parent.stateGasSpill
          parent.stateGasSpillRefunded child.stateGasUsed child.stateGasSpill
          child.stateGasSpillRefunded) =
      Spec.revertRefundToHalt parent child := by
  rcases hNoOverflow with ⟨hParent, hChild, hDifference, _hChildNetSpill,
    hReservoirAdd, hResult⟩
  rcases hParent with ⟨hParentValue, hParentReservoir, hParentUsed, hParentSpill,
    hParentRefunded⟩
  rcases hChild with ⟨_hChildReservoir, hChildUsed, hChildSpill, hChildRefunded⟩
  rcases hResult with ⟨_hResultValue, hResultReservoir, hResultUsed,
    hResultSpill, hResultRefunded, _hResultUnapplied⟩
  rw [Eip803x.Generated.StateGasTransitionKernel.revertRefundToHalt]
  rw [normalizeUInt64_of_fits hParentValue, wrapInt64_of_fits hParentReservoir,
    wrapInt64_of_fits hParentUsed, wrapInt64_of_fits hParentSpill,
    wrapInt64_of_fits hParentRefunded, wrapInt64_of_fits hChildUsed,
    wrapInt64_of_fits hChildSpill, wrapInt64_of_fits hChildRefunded]
  change generatedResultToSpec
      (let childNetSpill :=
        if Eip803x.Generated.StateGasTransitionKernel.subInt64
            child.stateGasSpill child.stateGasSpillRefunded > 0 then
          Eip803x.Generated.StateGasTransitionKernel.subInt64
            child.stateGasSpill child.stateGasSpillRefunded
        else 0
       { value := parent.gasLeft
         stateReservoir := Eip803x.Generated.StateGasTransitionKernel.subInt64
           (Eip803x.Generated.StateGasTransitionKernel.addInt64
             parent.stateReservoir child.stateGasUsed)
           childNetSpill
         stateGasUsed := Eip803x.Generated.StateGasTransitionKernel.subInt64
           parent.stateGasUsed child.stateGasUsed
         stateGasSpill := Eip803x.Generated.StateGasTransitionKernel.subInt64
           parent.stateGasSpill child.stateGasSpill
         stateGasSpillRefunded := Eip803x.Generated.StateGasTransitionKernel.subInt64
           parent.stateGasSpillRefunded child.stateGasSpillRefunded
         unappliedAmount := 0 }) = Spec.revertRefundToHalt parent child
  have hUnrefunded :
      (if Eip803x.Generated.StateGasTransitionKernel.subInt64
          child.stateGasSpill child.stateGasSpillRefunded > 0 then
        Eip803x.Generated.StateGasTransitionKernel.subInt64
          child.stateGasSpill child.stateGasSpillRefunded
      else 0) = Spec.unrefundedSpill child.stateGasSpill child.stateGasSpillRefunded := by
    rw [subInt64_of_fits hDifference]
    rfl
  rw [hUnrefunded]
  change generatedResultToSpec
      { value := parent.gasLeft
        stateReservoir := Eip803x.Generated.StateGasTransitionKernel.subInt64
          (Eip803x.Generated.StateGasTransitionKernel.addInt64
            parent.stateReservoir child.stateGasUsed)
          (Spec.unrefundedSpill child.stateGasSpill child.stateGasSpillRefunded)
        stateGasUsed := Eip803x.Generated.StateGasTransitionKernel.subInt64
          parent.stateGasUsed child.stateGasUsed
        stateGasSpill := Eip803x.Generated.StateGasTransitionKernel.subInt64
          parent.stateGasSpill child.stateGasSpill
        stateGasSpillRefunded := Eip803x.Generated.StateGasTransitionKernel.subInt64
          parent.stateGasSpillRefunded child.stateGasSpillRefunded
        unappliedAmount := 0 } = Spec.revertRefundToHalt parent child
  change ProductionGas.FitsInt64
      (parent.stateReservoir + child.stateGasUsed -
        Spec.unrefundedSpill child.stateGasSpill child.stateGasSpillRefunded) at hResultReservoir
  change ProductionGas.FitsInt64 (parent.stateGasUsed - child.stateGasUsed) at hResultUsed
  change ProductionGas.FitsInt64 (parent.stateGasSpill - child.stateGasSpill) at hResultSpill
  change ProductionGas.FitsInt64
      (parent.stateGasSpillRefunded - child.stateGasSpillRefunded) at hResultRefunded
  rw [addInt64_of_fits hReservoirAdd, subInt64_of_fits hResultReservoir,
    subInt64_of_fits hResultUsed, subInt64_of_fits hResultSpill,
    subInt64_of_fits hResultRefunded]
  rfl

theorem generated_refundStateGas_matches_spec
    {state : ProductionGasState} {amount stateGasFloor : Int} {trackSpillRefund : Bool}
    (hNoOverflow : Spec.refundStateGasNoOverflow state amount stateGasFloor trackSpillRefund) :
    generatedResultToSpec
        (Eip803x.Generated.StateGasTransitionKernel.refundStateGas
          state.gasLeft state.stateReservoir state.stateGasUsed state.stateGasSpill
          state.stateGasSpillRefunded amount stateGasFloor trackSpillRefund) =
      Spec.refundStateGas state amount stateGasFloor trackSpillRefund := by
  rcases hNoOverflow with ⟨hState, hAmount, hFloor, hUsedFloor, hSpillRefunded,
    hToGasLeftNonnegative, hToGasLeftFits, hAppliedMinusToGasLeft, hResult⟩
  rcases hState with ⟨hValue, hReservoir, hUsed, hSpill, hRefunded⟩
  rw [Eip803x.Generated.StateGasTransitionKernel.refundStateGas]
  rw [normalizeUInt64_of_fits hValue, wrapInt64_of_fits hReservoir,
    wrapInt64_of_fits hUsed, wrapInt64_of_fits hSpill, wrapInt64_of_fits hRefunded,
    wrapInt64_of_fits hAmount, wrapInt64_of_fits hFloor]
  change generatedResultToSpec
      (let refundableStateGas :=
        if Eip803x.Generated.StateGasTransitionKernel.subInt64
            state.stateGasUsed stateGasFloor > 0 then
          Eip803x.Generated.StateGasTransitionKernel.subInt64 state.stateGasUsed stateGasFloor
        else 0
       let appliedRefund := min amount refundableStateGas
       let toGasLeft := if trackSpillRefund then
         min appliedRefund
           (if Eip803x.Generated.StateGasTransitionKernel.subInt64
               state.stateGasSpill state.stateGasSpillRefunded > 0 then
             Eip803x.Generated.StateGasTransitionKernel.subInt64
               state.stateGasSpill state.stateGasSpillRefunded
           else 0)
       else 0
       { value := Eip803x.Generated.StateGasTransitionKernel.addUInt64 state.gasLeft
           (Eip803x.Generated.StateGasTransitionKernel.int64ToUInt64 toGasLeft)
         stateReservoir := Eip803x.Generated.StateGasTransitionKernel.addInt64
           state.stateReservoir
           (Eip803x.Generated.StateGasTransitionKernel.subInt64 appliedRefund toGasLeft)
         stateGasUsed := Eip803x.Generated.StateGasTransitionKernel.subInt64
           state.stateGasUsed appliedRefund
         stateGasSpill := state.stateGasSpill
         stateGasSpillRefunded := if trackSpillRefund then
           Eip803x.Generated.StateGasTransitionKernel.addInt64
             state.stateGasSpillRefunded toGasLeft
         else state.stateGasSpillRefunded
         unappliedAmount := 0 }) =
    Spec.refundStateGas state amount stateGasFloor trackSpillRefund
  have hRefundable :
      (if Eip803x.Generated.StateGasTransitionKernel.subInt64
          state.stateGasUsed stateGasFloor > 0 then
        Eip803x.Generated.StateGasTransitionKernel.subInt64 state.stateGasUsed stateGasFloor
      else 0) = Spec.refundableStateGas state.stateGasUsed stateGasFloor := by
    rw [subInt64_of_fits hUsedFloor]
    rfl
  have hUnrefunded :
      (if Eip803x.Generated.StateGasTransitionKernel.subInt64
          state.stateGasSpill state.stateGasSpillRefunded > 0 then
        Eip803x.Generated.StateGasTransitionKernel.subInt64
          state.stateGasSpill state.stateGasSpillRefunded
      else 0) = Spec.unrefundedSpill state.stateGasSpill state.stateGasSpillRefunded := by
    rw [subInt64_of_fits hSpillRefunded]
    rfl
  rw [hRefundable, hUnrefunded]
  let appliedRefund := min amount (Spec.refundableStateGas state.stateGasUsed stateGasFloor)
  let toGasLeft := if trackSpillRefund then
    min appliedRefund (Spec.unrefundedSpill state.stateGasSpill state.stateGasSpillRefunded)
  else 0
  change 0 ≤ toGasLeft at hToGasLeftNonnegative
  change ProductionGas.FitsInt64 toGasLeft at hToGasLeftFits
  change ProductionGas.FitsInt64 (appliedRefund - toGasLeft) at hAppliedMinusToGasLeft
  change Spec.resultFits
      { value := state.gasLeft + Int.toNat toGasLeft
        stateReservoir := state.stateReservoir + (appliedRefund - toGasLeft)
        stateGasUsed := state.stateGasUsed - appliedRefund
        stateGasSpill := state.stateGasSpill
        stateGasSpillRefunded := if trackSpillRefund then
          state.stateGasSpillRefunded + toGasLeft
        else state.stateGasSpillRefunded
        unappliedAmount := 0 } at hResult
  change generatedResultToSpec
      { value := Eip803x.Generated.StateGasTransitionKernel.addUInt64 state.gasLeft
          (Eip803x.Generated.StateGasTransitionKernel.int64ToUInt64 toGasLeft)
        stateReservoir := Eip803x.Generated.StateGasTransitionKernel.addInt64
          state.stateReservoir
          (Eip803x.Generated.StateGasTransitionKernel.subInt64 appliedRefund toGasLeft)
        stateGasUsed := Eip803x.Generated.StateGasTransitionKernel.subInt64
          state.stateGasUsed appliedRefund
        stateGasSpill := state.stateGasSpill
        stateGasSpillRefunded := if trackSpillRefund then
          Eip803x.Generated.StateGasTransitionKernel.addInt64
            state.stateGasSpillRefunded toGasLeft
        else state.stateGasSpillRefunded
        unappliedAmount := 0 } =
      { value := state.gasLeft + Int.toNat toGasLeft
        stateReservoir := state.stateReservoir + (appliedRefund - toGasLeft)
        stateGasUsed := state.stateGasUsed - appliedRefund
        stateGasSpill := state.stateGasSpill
        stateGasSpillRefunded := if trackSpillRefund then
          state.stateGasSpillRefunded + toGasLeft
        else state.stateGasSpillRefunded
        unappliedAmount := 0 }
  rcases hResult with ⟨hResultValue, hResultReservoir, hResultUsed,
    _hResultSpill, hResultRefunded, _hResultUnapplied⟩
  rw [int64ToUInt64_of_nonnegative_fits hToGasLeftNonnegative hToGasLeftFits,
    addUInt64_of_fits hResultValue, subInt64_of_fits hAppliedMinusToGasLeft,
    addInt64_of_fits hResultReservoir, subInt64_of_fits hResultUsed]
  cases trackSpillRefund with
  | false => rfl
  | true =>
    change ProductionGas.FitsInt64 (state.stateGasSpillRefunded + toGasLeft) at hResultRefunded
    rw [addInt64_of_fits hResultRefunded]
    rfl

theorem generated_discardStateGas_matches_spec
    {state : ProductionGasState} {amount stateGasFloor : Int}
    (hNoOverflow : Spec.discardStateGasNoOverflow state amount stateGasFloor) :
    generatedResultToSpec
        (Eip803x.Generated.StateGasTransitionKernel.discardStateGas
          state.gasLeft state.stateReservoir state.stateGasUsed state.stateGasSpill
          state.stateGasSpillRefunded amount stateGasFloor) =
      Spec.discardStateGas state amount stateGasFloor := by
  rcases hNoOverflow with ⟨hState, hAmount, hFloor, hUsedFloor, hResult⟩
  rcases hState with ⟨hValue, hReservoir, hUsed, hSpill, hRefunded⟩
  rcases hResult with ⟨_hResultValue, _hResultReservoir, hResultUsed,
    _hResultSpill, _hResultRefunded, hResultUnapplied⟩
  rw [Eip803x.Generated.StateGasTransitionKernel.discardStateGas]
  rw [normalizeUInt64_of_fits hValue, wrapInt64_of_fits hReservoir,
    wrapInt64_of_fits hUsed, wrapInt64_of_fits hSpill, wrapInt64_of_fits hRefunded,
    wrapInt64_of_fits hAmount, wrapInt64_of_fits hFloor]
  change generatedResultToSpec
      (let discardableStateGas :=
        if Eip803x.Generated.StateGasTransitionKernel.subInt64
            state.stateGasUsed stateGasFloor > 0 then
          Eip803x.Generated.StateGasTransitionKernel.subInt64 state.stateGasUsed stateGasFloor
        else 0
       let appliedRefund := min amount discardableStateGas
       { value := state.gasLeft
         stateReservoir := state.stateReservoir
         stateGasUsed := Eip803x.Generated.StateGasTransitionKernel.subInt64
           state.stateGasUsed appliedRefund
         stateGasSpill := state.stateGasSpill
         stateGasSpillRefunded := state.stateGasSpillRefunded
         unappliedAmount := Eip803x.Generated.StateGasTransitionKernel.subInt64
           amount appliedRefund }) = Spec.discardStateGas state amount stateGasFloor
  have hRefundable :
      (if Eip803x.Generated.StateGasTransitionKernel.subInt64
          state.stateGasUsed stateGasFloor > 0 then
        Eip803x.Generated.StateGasTransitionKernel.subInt64 state.stateGasUsed stateGasFloor
      else 0) = Spec.refundableStateGas state.stateGasUsed stateGasFloor := by
    rw [subInt64_of_fits hUsedFloor]
    rfl
  rw [hRefundable]
  let appliedRefund := min amount (Spec.refundableStateGas state.stateGasUsed stateGasFloor)
  change ProductionGas.FitsInt64 (state.stateGasUsed - appliedRefund) at hResultUsed
  change ProductionGas.FitsInt64 (amount - appliedRefund) at hResultUnapplied
  change generatedResultToSpec
      { value := state.gasLeft
        stateReservoir := state.stateReservoir
        stateGasUsed := Eip803x.Generated.StateGasTransitionKernel.subInt64
          state.stateGasUsed appliedRefund
        stateGasSpill := state.stateGasSpill
        stateGasSpillRefunded := state.stateGasSpillRefunded
        unappliedAmount := Eip803x.Generated.StateGasTransitionKernel.subInt64
          amount appliedRefund } =
      { value := state.gasLeft
        stateReservoir := state.stateReservoir
        stateGasUsed := state.stateGasUsed - appliedRefund
        stateGasSpill := state.stateGasSpill
        stateGasSpillRefunded := state.stateGasSpillRefunded
        unappliedAmount := amount - appliedRefund }
  rw [subInt64_of_fits hResultUsed, subInt64_of_fits hResultUnapplied]
  rfl

theorem generated_addStateGasRefundToReservoir_matches_spec
    {state : ProductionGasState} {amount : Int} {trackSpillRefund : Bool}
    (hNoOverflow : Spec.addStateGasRefundToReservoirNoOverflow state amount trackSpillRefund) :
    generatedResultToSpec
        (Eip803x.Generated.StateGasTransitionKernel.addStateGasRefundToReservoir
          state.gasLeft state.stateReservoir state.stateGasUsed state.stateGasSpill
          state.stateGasSpillRefunded amount trackSpillRefund) =
      Spec.addStateGasRefundToReservoir state amount trackSpillRefund := by
  rcases hNoOverflow with ⟨hState, hAmount, hSpillRefunded, hToGasLeftNonnegative,
    hToGasLeftFits, hAmountMinusToGasLeft, hResult⟩
  rcases hState with ⟨hValue, hReservoir, hUsed, hSpill, hRefunded⟩
  rw [Eip803x.Generated.StateGasTransitionKernel.addStateGasRefundToReservoir]
  rw [normalizeUInt64_of_fits hValue, wrapInt64_of_fits hReservoir,
    wrapInt64_of_fits hUsed, wrapInt64_of_fits hSpill, wrapInt64_of_fits hRefunded,
    wrapInt64_of_fits hAmount]
  change generatedResultToSpec
      (let toGasLeft := if trackSpillRefund then
        min amount
          (if Eip803x.Generated.StateGasTransitionKernel.subInt64
              state.stateGasSpill state.stateGasSpillRefunded > 0 then
            Eip803x.Generated.StateGasTransitionKernel.subInt64
              state.stateGasSpill state.stateGasSpillRefunded
          else 0)
       else 0
       { value := Eip803x.Generated.StateGasTransitionKernel.addUInt64 state.gasLeft
           (Eip803x.Generated.StateGasTransitionKernel.int64ToUInt64 toGasLeft)
         stateReservoir := Eip803x.Generated.StateGasTransitionKernel.addInt64
           state.stateReservoir
           (Eip803x.Generated.StateGasTransitionKernel.subInt64 amount toGasLeft)
         stateGasUsed := state.stateGasUsed
         stateGasSpill := state.stateGasSpill
         stateGasSpillRefunded := if trackSpillRefund then
           Eip803x.Generated.StateGasTransitionKernel.addInt64
             state.stateGasSpillRefunded toGasLeft
         else state.stateGasSpillRefunded
         unappliedAmount := 0 }) =
      Spec.addStateGasRefundToReservoir state amount trackSpillRefund
  have hUnrefunded :
      (if Eip803x.Generated.StateGasTransitionKernel.subInt64
          state.stateGasSpill state.stateGasSpillRefunded > 0 then
        Eip803x.Generated.StateGasTransitionKernel.subInt64
          state.stateGasSpill state.stateGasSpillRefunded
      else 0) = Spec.unrefundedSpill state.stateGasSpill state.stateGasSpillRefunded := by
    rw [subInt64_of_fits hSpillRefunded]
    rfl
  rw [hUnrefunded]
  let toGasLeft := if trackSpillRefund then
    min amount (Spec.unrefundedSpill state.stateGasSpill state.stateGasSpillRefunded)
  else 0
  change 0 ≤ toGasLeft at hToGasLeftNonnegative
  change ProductionGas.FitsInt64 toGasLeft at hToGasLeftFits
  change ProductionGas.FitsInt64 (amount - toGasLeft) at hAmountMinusToGasLeft
  change Spec.resultFits
      { value := state.gasLeft + Int.toNat toGasLeft
        stateReservoir := state.stateReservoir + (amount - toGasLeft)
        stateGasUsed := state.stateGasUsed
        stateGasSpill := state.stateGasSpill
        stateGasSpillRefunded := if trackSpillRefund then
          state.stateGasSpillRefunded + toGasLeft
        else state.stateGasSpillRefunded
        unappliedAmount := 0 } at hResult
  change generatedResultToSpec
      { value := Eip803x.Generated.StateGasTransitionKernel.addUInt64 state.gasLeft
          (Eip803x.Generated.StateGasTransitionKernel.int64ToUInt64 toGasLeft)
        stateReservoir := Eip803x.Generated.StateGasTransitionKernel.addInt64
          state.stateReservoir
          (Eip803x.Generated.StateGasTransitionKernel.subInt64 amount toGasLeft)
        stateGasUsed := state.stateGasUsed
        stateGasSpill := state.stateGasSpill
        stateGasSpillRefunded := if trackSpillRefund then
          Eip803x.Generated.StateGasTransitionKernel.addInt64
            state.stateGasSpillRefunded toGasLeft
        else state.stateGasSpillRefunded
        unappliedAmount := 0 } =
      { value := state.gasLeft + Int.toNat toGasLeft
        stateReservoir := state.stateReservoir + (amount - toGasLeft)
        stateGasUsed := state.stateGasUsed
        stateGasSpill := state.stateGasSpill
        stateGasSpillRefunded := if trackSpillRefund then
          state.stateGasSpillRefunded + toGasLeft
        else state.stateGasSpillRefunded
        unappliedAmount := 0 }
  rcases hResult with ⟨hResultValue, hResultReservoir, _hResultUsed,
    _hResultSpill, hResultRefunded, _hResultUnapplied⟩
  rw [int64ToUInt64_of_nonnegative_fits hToGasLeftNonnegative hToGasLeftFits,
    addUInt64_of_fits hResultValue, subInt64_of_fits hAmountMinusToGasLeft,
    addInt64_of_fits hResultReservoir]
  cases trackSpillRefund with
  | false => rfl
  | true =>
    change ProductionGas.FitsInt64 (state.stateGasSpillRefunded + toGasLeft) at hResultRefunded
    rw [addInt64_of_fits hResultRefunded]
    rfl

theorem generated_removeStateGasRefundFromReservoir_matches_spec
    {state : ProductionGasState} {amount : Int}
    (hNoOverflow : Spec.removeStateGasRefundFromReservoirNoOverflow state amount) :
    generatedResultToSpec
        (Eip803x.Generated.StateGasTransitionKernel.removeStateGasRefundFromReservoir
          state.gasLeft state.stateReservoir state.stateGasUsed state.stateGasSpill
          state.stateGasSpillRefunded amount) =
      Spec.removeStateGasRefundFromReservoir state amount := by
  rcases hNoOverflow with ⟨hState, hAmount, hRemainingFits, hNextReservoirFits,
    hRemainingMinusFromUsedFits, hResult⟩
  rcases hState with ⟨hValue, hReservoir, hUsed, hSpill, hRefunded⟩
  rw [Eip803x.Generated.StateGasTransitionKernel.removeStateGasRefundFromReservoir]
  rw [normalizeUInt64_of_fits hValue, wrapInt64_of_fits hReservoir,
    wrapInt64_of_fits hUsed, wrapInt64_of_fits hSpill, wrapInt64_of_fits hRefunded,
    wrapInt64_of_fits hAmount]
  change generatedResultToSpec
      (let fromReservoir := if state.stateReservoir <= 0 then 0 else
        if state.stateReservoir >= amount then amount else state.stateReservoir
       let remainingAmount := Eip803x.Generated.StateGasTransitionKernel.subInt64
         amount fromReservoir
       let nextStateReservoir := Eip803x.Generated.StateGasTransitionKernel.subInt64
         state.stateReservoir fromReservoir
       if remainingAmount <= 0 then
         { value := state.gasLeft
           stateReservoir := nextStateReservoir
           stateGasUsed := state.stateGasUsed
           stateGasSpill := state.stateGasSpill
           stateGasSpillRefunded := state.stateGasSpillRefunded
           unappliedAmount := 0 }
       else
         let fromUsed := min remainingAmount state.stateGasUsed
         { value := state.gasLeft
           stateReservoir := Eip803x.Generated.StateGasTransitionKernel.subInt64
             nextStateReservoir
             (Eip803x.Generated.StateGasTransitionKernel.subInt64 remainingAmount fromUsed)
           stateGasUsed := Eip803x.Generated.StateGasTransitionKernel.subInt64
             state.stateGasUsed fromUsed
           stateGasSpill := state.stateGasSpill
           stateGasSpillRefunded := state.stateGasSpillRefunded
           unappliedAmount := 0 }) = Spec.removeStateGasRefundFromReservoir state amount
  let fromReservoir := Spec.clampToRefundAmount state.stateReservoir amount
  have hClamp :
      (if state.stateReservoir <= 0 then 0 else
        if state.stateReservoir >= amount then amount else state.stateReservoir) = fromReservoir := by
    rfl
  rw [hClamp]
  let remainingAmount := amount - fromReservoir
  let nextStateReservoir := state.stateReservoir - fromReservoir
  change ProductionGas.FitsInt64 remainingAmount at hRemainingFits
  change ProductionGas.FitsInt64 nextStateReservoir at hNextReservoirFits
  change ProductionGas.FitsInt64
      (remainingAmount - min remainingAmount state.stateGasUsed) at hRemainingMinusFromUsedFits
  change generatedResultToSpec
      (let remainingAmount := Eip803x.Generated.StateGasTransitionKernel.subInt64
        amount fromReservoir
       let nextStateReservoir := Eip803x.Generated.StateGasTransitionKernel.subInt64
         state.stateReservoir fromReservoir
       if remainingAmount <= 0 then
         { value := state.gasLeft
           stateReservoir := nextStateReservoir
           stateGasUsed := state.stateGasUsed
           stateGasSpill := state.stateGasSpill
           stateGasSpillRefunded := state.stateGasSpillRefunded
           unappliedAmount := 0 }
       else
         let fromUsed := min remainingAmount state.stateGasUsed
         { value := state.gasLeft
           stateReservoir := Eip803x.Generated.StateGasTransitionKernel.subInt64
             nextStateReservoir
             (Eip803x.Generated.StateGasTransitionKernel.subInt64 remainingAmount fromUsed)
           stateGasUsed := Eip803x.Generated.StateGasTransitionKernel.subInt64
             state.stateGasUsed fromUsed
           stateGasSpill := state.stateGasSpill
           stateGasSpillRefunded := state.stateGasSpillRefunded
           unappliedAmount := 0 }) = Spec.removeStateGasRefundFromReservoir state amount
  rw [subInt64_of_fits hRemainingFits, subInt64_of_fits hNextReservoirFits]
  change generatedResultToSpec
      (if remainingAmount <= 0 then
        { value := state.gasLeft
          stateReservoir := nextStateReservoir
          stateGasUsed := state.stateGasUsed
          stateGasSpill := state.stateGasSpill
          stateGasSpillRefunded := state.stateGasSpillRefunded
          unappliedAmount := 0 }
      else
        let fromUsed := min remainingAmount state.stateGasUsed
        { value := state.gasLeft
          stateReservoir := Eip803x.Generated.StateGasTransitionKernel.subInt64
            nextStateReservoir
            (Eip803x.Generated.StateGasTransitionKernel.subInt64 remainingAmount fromUsed)
          stateGasUsed := Eip803x.Generated.StateGasTransitionKernel.subInt64
            state.stateGasUsed fromUsed
          stateGasSpill := state.stateGasSpill
          stateGasSpillRefunded := state.stateGasSpillRefunded
          unappliedAmount := 0 }) =
      if remainingAmount <= 0 then
        { value := state.gasLeft
          stateReservoir := nextStateReservoir
          stateGasUsed := state.stateGasUsed
          stateGasSpill := state.stateGasSpill
          stateGasSpillRefunded := state.stateGasSpillRefunded
          unappliedAmount := 0 }
      else
        let fromUsed := min remainingAmount state.stateGasUsed
        { value := state.gasLeft
          stateReservoir := nextStateReservoir - (remainingAmount - fromUsed)
          stateGasUsed := state.stateGasUsed - fromUsed
          stateGasSpill := state.stateGasSpill
          stateGasSpillRefunded := state.stateGasSpillRefunded
          unappliedAmount := 0 }
  change Spec.resultFits
      (if remainingAmount <= 0 then
        { value := state.gasLeft
          stateReservoir := nextStateReservoir
          stateGasUsed := state.stateGasUsed
          stateGasSpill := state.stateGasSpill
          stateGasSpillRefunded := state.stateGasSpillRefunded
          unappliedAmount := 0 }
      else
        let fromUsed := min remainingAmount state.stateGasUsed
        { value := state.gasLeft
          stateReservoir := nextStateReservoir - (remainingAmount - fromUsed)
          stateGasUsed := state.stateGasUsed - fromUsed
          stateGasSpill := state.stateGasSpill
          stateGasSpillRefunded := state.stateGasSpillRefunded
          unappliedAmount := 0 }) at hResult
  by_cases hRemaining : remainingAmount <= 0
  · simp [hRemaining]
    rfl
  · simp only [hRemaining, ↓reduceIte] at hResult ⊢
    rcases hResult with ⟨_hResultValue, hResultReservoir, hResultUsed,
      _hResultSpill, _hResultRefunded, _hResultUnapplied⟩
    let fromUsed := min remainingAmount state.stateGasUsed
    change ProductionGas.FitsInt64 (nextStateReservoir - (remainingAmount - fromUsed))
      at hResultReservoir
    change ProductionGas.FitsInt64 (state.stateGasUsed - fromUsed) at hResultUsed
    change generatedResultToSpec
        { value := state.gasLeft
          stateReservoir := Eip803x.Generated.StateGasTransitionKernel.subInt64
            nextStateReservoir
            (Eip803x.Generated.StateGasTransitionKernel.subInt64 remainingAmount fromUsed)
          stateGasUsed := Eip803x.Generated.StateGasTransitionKernel.subInt64
            state.stateGasUsed fromUsed
          stateGasSpill := state.stateGasSpill
          stateGasSpillRefunded := state.stateGasSpillRefunded
          unappliedAmount := 0 } =
        { value := state.gasLeft
          stateReservoir := nextStateReservoir - (remainingAmount - fromUsed)
          stateGasUsed := state.stateGasUsed - fromUsed
          stateGasSpill := state.stateGasSpill
          stateGasSpillRefunded := state.stateGasSpillRefunded
          unappliedAmount := 0 }
    rw [subInt64_of_fits hRemainingMinusFromUsedFits,
      subInt64_of_fits hResultReservoir, subInt64_of_fits hResultUsed]
    rfl

/--
When the production reservoir is nonpositive, neither the restricted kernel nor
the natural-number gas machine has reservoir credit to repay.  This is the
direct common-state bridge; the other frame transitions lack the journal and
baseline inputs required for a general `FrameGasState` theorem.
-/
theorem repayStateGasSpill_no_reservoir_refines_gas_machine
    {production : ProductionGasState} {model : GasState}
    (hRepresents : ProductionGas.Represents production model)
    (hReservoirNonpositive : production.stateReservoir <= 0) :
    ProductionGas.Represents
      (Spec.resultState (Spec.repayStateGasSpill production))
      (GasMachine.repayStateFromGasLeft model) := by
  rcases hRepresents with ⟨hGasLeft, hReservoir, hFromGasLeft, hUsed⟩
  have hModelReservoirZero : model.stateReservoir = 0 := by
    have hModelReservoirZeroInt : (model.stateReservoir : Int) = 0 := by
      rw [hReservoir, Int.max_eq_right hReservoirNonpositive]
    omega
  have hNoRepayment : min production.stateReservoir
      (Spec.unrefundedSpill production.stateGasSpill production.stateGasSpillRefunded) <= 0 := by
    exact Int.le_trans (Int.min_le_left _ _) hReservoirNonpositive
  have hResultState : Spec.resultState (Spec.result production) = production := rfl
  have hModelRepayment : GasMachine.repayStateFromGasLeft model = model := by
    cases model
    simp_all [GasMachine.repayStateFromGasLeft]
  unfold Spec.repayStateGasSpill
  rw [if_pos hNoRepayment]
  rw [hResultState, hModelRepayment]
  exact ⟨hGasLeft, hReservoir, hFromGasLeft, hUsed⟩
