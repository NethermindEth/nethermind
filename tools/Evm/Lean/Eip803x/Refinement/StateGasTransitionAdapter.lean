-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Refinement.StateGasTransition
import Lean.Elab.Tactic.Omega

namespace Eip803x.Refinement.StateGasTransitionAdapter

open Eip803x

namespace TransitionSpec

abbrev Result := Eip803x.Refinement.StateGasTransition.Spec.Result
abbrev Child := Eip803x.Refinement.StateGasTransition.Spec.Child
abbrev result := Eip803x.Refinement.StateGasTransition.Spec.result
abbrev resultState := Eip803x.Refinement.StateGasTransition.Spec.resultState
abbrev child := Eip803x.Refinement.StateGasTransition.Spec.child
abbrev refund := Eip803x.Refinement.StateGasTransition.Spec.refund
abbrev repayStateGasSpill := Eip803x.Refinement.StateGasTransition.Spec.repayStateGasSpill
abbrev restoreChildStateGas := Eip803x.Refinement.StateGasTransition.Spec.restoreChildStateGas
abbrev restoreChildStateGasOnHalt :=
  Eip803x.Refinement.StateGasTransition.Spec.restoreChildStateGasOnHalt
abbrev revertRefundToHalt := Eip803x.Refinement.StateGasTransition.Spec.revertRefundToHalt
abbrev refundableStateGas := Eip803x.Refinement.StateGasTransition.Spec.refundableStateGas
abbrev refundStateGas := Eip803x.Refinement.StateGasTransition.Spec.refundStateGas
abbrev discardStateGas := Eip803x.Refinement.StateGasTransition.Spec.discardStateGas
abbrev addStateGasRefundToReservoir :=
  Eip803x.Refinement.StateGasTransition.Spec.addStateGasRefundToReservoir
abbrev removeStateGasRefundFromReservoir :=
  Eip803x.Refinement.StateGasTransition.Spec.removeStateGasRefundFromReservoir
abbrev unrefundedSpill := Eip803x.Refinement.StateGasTransition.Spec.unrefundedSpill
abbrev positivePart := Eip803x.Refinement.StateGasTransition.Spec.positivePart
abbrev resultFits := Eip803x.Refinement.StateGasTransition.Spec.resultFits
abbrev childBounded := Eip803x.Refinement.StateGasTransition.Spec.childBounded
abbrev refundNoOverflow := Eip803x.Refinement.StateGasTransition.Spec.refundNoOverflow
abbrev repayStateGasSpillNoOverflow :=
  Eip803x.Refinement.StateGasTransition.Spec.repayStateGasSpillNoOverflow
abbrev restoreChildStateGasNoOverflow :=
  Eip803x.Refinement.StateGasTransition.Spec.restoreChildStateGasNoOverflow
abbrev restoreChildStateGasOnHaltNoOverflow :=
  Eip803x.Refinement.StateGasTransition.Spec.restoreChildStateGasOnHaltNoOverflow
abbrev revertRefundToHaltNoOverflow :=
  Eip803x.Refinement.StateGasTransition.Spec.revertRefundToHaltNoOverflow
abbrev refundStateGasNoOverflow :=
  Eip803x.Refinement.StateGasTransition.Spec.refundStateGasNoOverflow
abbrev discardStateGasNoOverflow :=
  Eip803x.Refinement.StateGasTransition.Spec.discardStateGasNoOverflow
abbrev addStateGasRefundToReservoirNoOverflow :=
  Eip803x.Refinement.StateGasTransition.Spec.addStateGasRefundToReservoirNoOverflow
abbrev removeStateGasRefundFromReservoirNoOverflow :=
  Eip803x.Refinement.StateGasTransition.Spec.removeStateGasRefundFromReservoirNoOverflow

abbrev discardStateGas_reports_unapplied :=
  Eip803x.Refinement.StateGasTransition.Spec.discardStateGas_reports_unapplied

end TransitionSpec

namespace TransitionRefinement

abbrev generatedResultToSpec :=
  Eip803x.Refinement.StateGasTransition.generatedResultToSpec

end TransitionRefinement

namespace GeneratedTransition

abbrev refund := Eip803x.Generated.StateGasTransitionKernel.refund
abbrev repayStateGasSpill := Eip803x.Generated.StateGasTransitionKernel.repayStateGasSpill
abbrev restoreChildStateGas := Eip803x.Generated.StateGasTransitionKernel.restoreChildStateGas
abbrev restoreChildStateGasOnHalt :=
  Eip803x.Generated.StateGasTransitionKernel.restoreChildStateGasOnHalt
abbrev revertRefundToHalt := Eip803x.Generated.StateGasTransitionKernel.revertRefundToHalt
abbrev refundStateGas := Eip803x.Generated.StateGasTransitionKernel.refundStateGas
abbrev discardStateGas := Eip803x.Generated.StateGasTransitionKernel.discardStateGas
abbrev addStateGasRefundToReservoir :=
  Eip803x.Generated.StateGasTransitionKernel.addStateGasRefundToReservoir
abbrev removeStateGasRefundFromReservoir :=
  Eip803x.Generated.StateGasTransitionKernel.removeStateGasRefundFromReservoir

end GeneratedTransition

/-- The caller-visible return channel of a production transition adapter. -/
inductive ReturnValue where
  | unit
  | amount (value : Int)
  deriving DecidableEq, Repr

/-- The five fields written by `ApplyStateGasTransition`, plus the adapter return value. -/
structure Completion where
  state : ProductionGasState
  returnValue : ReturnValue
  deriving DecidableEq, Repr

/--
`RemoveStateGasRefundFromReservoir` preserves the historical `ArgumentException`
for negative amounts before invoking the restricted kernel.  The state carried by
the exceptional outcome makes the pre-mutation contract explicit.
-/
inductive Outcome where
  | completed (completion : Completion)
  | argumentException (unchanged : ProductionGasState)
  deriving DecidableEq, Repr

/-- Exact projection performed by production's `ApplyStateGasTransition`. -/
def projectState (result : TransitionSpec.Result) : ProductionGasState :=
  TransitionSpec.resultState result

/-- Projection for the eight adapters whose C# return type is `void`. -/
def projectVoid (result : TransitionSpec.Result) : Completion :=
  { state := projectState result, returnValue := .unit }

/-- Projection for `DiscardStateGas`, which returns `UnappliedAmount`. -/
def projectDiscard (result : TransitionSpec.Result) : Completion :=
  { state := projectState result, returnValue := .amount result.unappliedAmount }

namespace Adapter

def refund (parent child : ProductionGasState) : Outcome :=
  .completed (projectVoid (TransitionSpec.refund parent child))

def repayStateGasSpill (state : ProductionGasState) : Outcome :=
  .completed (projectVoid (TransitionSpec.repayStateGasSpill state))

def restoreChildStateGas (parent child : ProductionGasState) : Outcome :=
  .completed (projectVoid
    (TransitionSpec.restoreChildStateGas parent (TransitionSpec.child child)))

def restoreChildStateGasOnHalt (parent child : ProductionGasState) : Outcome :=
  .completed (projectVoid
    (TransitionSpec.restoreChildStateGasOnHalt parent (TransitionSpec.child child)))

def revertRefundToHalt (parent child : ProductionGasState) : Outcome :=
  .completed (projectVoid
    (TransitionSpec.revertRefundToHalt parent (TransitionSpec.child child)))

def refundStateGas (state : ProductionGasState) (amount stateGasFloor : Int)
    (trackSpillRefund : Bool) : Outcome :=
  .completed (projectVoid
    (TransitionSpec.refundStateGas state amount stateGasFloor trackSpillRefund))

def discardStateGas (state : ProductionGasState) (amount stateGasFloor : Int) : Outcome :=
  .completed (projectDiscard (TransitionSpec.discardStateGas state amount stateGasFloor))

def addStateGasRefundToReservoir (state : ProductionGasState) (amount : Int)
    (trackSpillRefund : Bool) : Outcome :=
  .completed (projectVoid
    (TransitionSpec.addStateGasRefundToReservoir state amount trackSpillRefund))

def removeStateGasRefundFromReservoir (state : ProductionGasState) (amount : Int) : Outcome :=
  if amount < 0 then
    .argumentException state
  else
    .completed (projectVoid
      (TransitionSpec.removeStateGasRefundFromReservoir state amount))

end Adapter

theorem projectState_fields (result : TransitionSpec.Result) :
    (projectState result).gasLeft = result.value ∧
      (projectState result).stateReservoir = result.stateReservoir ∧
        (projectState result).stateGasUsed = result.stateGasUsed ∧
          (projectState result).stateGasSpill = result.stateGasSpill ∧
            (projectState result).stateGasSpillRefunded = result.stateGasSpillRefunded := by
  exact ⟨rfl, rfl, rfl, rfl, rfl⟩

/-- `ApplyStateGasTransition` does not copy the kernel-only `UnappliedAmount` field. -/
theorem projectVoid_ignores_unapplied
    (result : TransitionSpec.Result) (unappliedAmount : Int) :
    projectVoid { result with unappliedAmount } = projectVoid result := by
  rfl

/-- `DiscardStateGas` exposes exactly the amount the kernel could not discard. -/
theorem projectDiscard_returns_unapplied (result : TransitionSpec.Result) :
    (projectDiscard result).returnValue = .amount result.unappliedAmount := rfl

theorem discardStateGas_returns_remainder
    (state : ProductionGasState) (amount stateGasFloor : Int) :
    Adapter.discardStateGas state amount stateGasFloor =
      .completed
        { state := projectState (TransitionSpec.discardStateGas state amount stateGasFloor)
          returnValue := .amount
            (amount - min amount
              (TransitionSpec.refundableStateGas state.stateGasUsed stateGasFloor)) } := by
  simp only [Adapter.discardStateGas, projectDiscard]
  rw [Eip803x.Refinement.StateGasTransition.Spec.discardStateGas_reports_unapplied]

theorem remove_negative_throws_without_mutation
    (state : ProductionGasState) {amount : Int} (hAmount : amount < 0) :
    Adapter.removeStateGasRefundFromReservoir state amount = .argumentException state := by
  simp [Adapter.removeStateGasRefundFromReservoir, hAmount]

theorem remove_nonnegative_applies_kernel_projection
    (state : ProductionGasState) {amount : Int} (hAmount : 0 ≤ amount) :
    Adapter.removeStateGasRefundFromReservoir state amount =
      .completed (projectVoid
        (TransitionSpec.removeStateGasRefundFromReservoir state amount)) := by
  simp [Adapter.removeStateGasRefundFromReservoir, hAmount]

/-- A result in the no-wrap domain projects to a machine-bounded adapter state. -/
theorem resultFits_projectState_machineBounded
    {result : TransitionSpec.Result} (hFits : TransitionSpec.resultFits result) :
    ProductionGas.MachineBounded (projectState result) := by
  exact ⟨hFits.1, hFits.2.1, hFits.2.2.1, hFits.2.2.2.1, hFits.2.2.2.2.1⟩

theorem refund_domain
    {parent child : ProductionGasState}
    (h : TransitionSpec.refundNoOverflow parent child) :
    ProductionGas.MachineBounded parent ∧
      ProductionGas.MachineBounded child ∧
        ProductionGas.MachineBounded
          (projectState (TransitionSpec.refund parent child)) := by
  exact ⟨h.1, h.2.1, resultFits_projectState_machineBounded h.2.2⟩

theorem repayStateGasSpill_domain
    {state : ProductionGasState}
    (h : TransitionSpec.repayStateGasSpillNoOverflow state) :
    ProductionGas.MachineBounded state ∧
      ProductionGas.MachineBounded
        (projectState (TransitionSpec.repayStateGasSpill state)) := by
  rcases h with ⟨hState, _hSpillDifference, _hRepayment, hResult⟩
  exact ⟨hState, resultFits_projectState_machineBounded hResult⟩

theorem restoreChildStateGas_domain
    {parent : ProductionGasState} {child : TransitionSpec.Child}
    (h : TransitionSpec.restoreChildStateGasNoOverflow parent child) :
    ProductionGas.MachineBounded parent ∧ TransitionSpec.childBounded child ∧
      ProductionGas.MachineBounded
        (projectState (TransitionSpec.restoreChildStateGas parent child)) := by
  rcases h with ⟨hParent, hChild, _hSpillDifference, _hNetSpill,
    _hParentChildReservoir, _hWithChildUsed, hResult⟩
  exact ⟨hParent, hChild, resultFits_projectState_machineBounded hResult⟩

theorem restoreChildStateGasOnHalt_domain
    {parent : ProductionGasState} {child : TransitionSpec.Child}
    (h : TransitionSpec.restoreChildStateGasOnHaltNoOverflow parent child) :
    ProductionGas.MachineBounded parent ∧ TransitionSpec.childBounded child ∧
      ProductionGas.MachineBounded
        (projectState (TransitionSpec.restoreChildStateGasOnHalt parent child)) := by
  rcases h with ⟨hParent, hChild, _hSpillDifference, _hNetSpill,
    _hParentChildReservoir, _hWithChildUsed, hResult⟩
  exact ⟨hParent, hChild, resultFits_projectState_machineBounded hResult⟩

theorem revertRefundToHalt_domain
    {parent : ProductionGasState} {child : TransitionSpec.Child}
    (h : TransitionSpec.revertRefundToHaltNoOverflow parent child) :
    ProductionGas.MachineBounded parent ∧ TransitionSpec.childBounded child ∧
      ProductionGas.MachineBounded
        (projectState (TransitionSpec.revertRefundToHalt parent child)) := by
  rcases h with ⟨hParent, hChild, _hSpillDifference, _hNetSpill,
    _hParentWithChildUsed, hResult⟩
  exact ⟨hParent, hChild, resultFits_projectState_machineBounded hResult⟩

theorem refundStateGas_domain
    {state : ProductionGasState} {amount stateGasFloor : Int} {trackSpillRefund : Bool}
    (h : TransitionSpec.refundStateGasNoOverflow
      state amount stateGasFloor trackSpillRefund) :
    ProductionGas.MachineBounded state ∧
      ProductionGas.MachineBounded
        (projectState
          (TransitionSpec.refundStateGas state amount stateGasFloor trackSpillRefund)) := by
  rcases h with ⟨hState, _hAmount, _hFloor, _hUsedDifference, _hSpillDifference,
    _hToGasLeftNonnegative, _hToGasLeft, _hReservoirCredit, hResult⟩
  exact ⟨hState, resultFits_projectState_machineBounded hResult⟩

theorem discardStateGas_domain
    {state : ProductionGasState} {amount stateGasFloor : Int}
    (h : TransitionSpec.discardStateGasNoOverflow state amount stateGasFloor) :
    ProductionGas.MachineBounded state ∧
      ProductionGas.MachineBounded
        (projectState (TransitionSpec.discardStateGas state amount stateGasFloor)) := by
  rcases h with ⟨hState, _hAmount, _hFloor, _hUsedDifference, hResult⟩
  exact ⟨hState, resultFits_projectState_machineBounded hResult⟩

theorem addStateGasRefundToReservoir_domain
    {state : ProductionGasState} {amount : Int} {trackSpillRefund : Bool}
    (h : TransitionSpec.addStateGasRefundToReservoirNoOverflow
      state amount trackSpillRefund) :
    ProductionGas.MachineBounded state ∧
      ProductionGas.MachineBounded
        (projectState
          (TransitionSpec.addStateGasRefundToReservoir state amount trackSpillRefund)) := by
  rcases h with ⟨hState, _hAmount, _hSpillDifference, _hToGasLeftNonnegative,
    _hToGasLeft, _hReservoirCredit, hResult⟩
  exact ⟨hState, resultFits_projectState_machineBounded hResult⟩

theorem removeStateGasRefundFromReservoir_domain
    {state : ProductionGasState} {amount : Int}
    (h : TransitionSpec.removeStateGasRefundFromReservoirNoOverflow state amount) :
    ProductionGas.MachineBounded state ∧
      ProductionGas.MachineBounded
        (projectState
          (TransitionSpec.removeStateGasRefundFromReservoir state amount)) := by
  rcases h with ⟨hState, _hAmount, _hRemaining, _hReservoir,
    _hUsedRemainder, hResult⟩
  exact ⟨hState, resultFits_projectState_machineBounded hResult⟩

theorem refund_preserves_wellFormed
    {parent child : ProductionGasState}
    (hParent : ProductionGas.WellFormed parent)
    (hChild : ProductionGas.WellFormed child) :
    ProductionGas.WellFormed
      (projectState (TransitionSpec.refund parent child)) := by
  rcases hParent with ⟨hParentUsed, hParentSpill, hParentRefunded, hParentOrder⟩
  rcases hChild with ⟨hChildUsed, hChildSpill, hChildRefunded, hChildOrder⟩
  unfold ProductionGas.WellFormed projectState TransitionSpec.resultState
    TransitionSpec.refund Eip803x.Refinement.StateGasTransition.Spec.resultState
    Eip803x.Refinement.StateGasTransition.Spec.refund
  dsimp
  exact ⟨by omega, by omega, by omega, by omega⟩

theorem restoreChildStateGas_preserves_parent_wellFormed
    {parent : ProductionGasState} (child : TransitionSpec.Child)
    (hParent : ProductionGas.WellFormed parent) :
    ProductionGas.WellFormed
      (projectState (TransitionSpec.restoreChildStateGas parent child)) := by
  exact hParent

theorem restoreChildStateGasOnHalt_preserves_parent_wellFormed
    {parent : ProductionGasState} (child : TransitionSpec.Child)
    (hParent : ProductionGas.WellFormed parent) :
    ProductionGas.WellFormed
      (projectState (TransitionSpec.restoreChildStateGasOnHalt parent child)) := by
  exact hParent

/-- Conditions under which removing an already-merged child leaves valid parent counters. -/
def ChildRemovalWellFormed (parent : ProductionGasState) (child : TransitionSpec.Child) : Prop :=
  0 ≤ parent.stateGasUsed - child.stateGasUsed ∧
    0 ≤ parent.stateGasSpill - child.stateGasSpill ∧
      0 ≤ parent.stateGasSpillRefunded - child.stateGasSpillRefunded ∧
        parent.stateGasSpillRefunded - child.stateGasSpillRefunded ≤
          parent.stateGasSpill - child.stateGasSpill

theorem revertRefundToHalt_preserves_wellFormed
    {parent : ProductionGasState} {child : TransitionSpec.Child}
    (hRemoval : ChildRemovalWellFormed parent child) :
    ProductionGas.WellFormed
      (projectState (TransitionSpec.revertRefundToHalt parent child)) := by
  exact hRemoval

theorem generated_refund_refines_adapter
    {parent child : ProductionGasState}
    (h : TransitionSpec.refundNoOverflow parent child) :
    projectVoid (TransitionRefinement.generatedResultToSpec
      (GeneratedTransition.refund
        parent.gasLeft parent.stateReservoir parent.stateGasUsed parent.stateGasSpill
        parent.stateGasSpillRefunded child.gasLeft child.stateReservoir child.stateGasUsed
        child.stateGasSpill child.stateGasSpillRefunded)) =
      projectVoid (TransitionSpec.refund parent child) := by
  simpa only [TransitionRefinement.generatedResultToSpec, GeneratedTransition.refund,
    TransitionSpec.refund] using congrArg projectVoid
      (Eip803x.Refinement.StateGasTransition.generated_refund_matches_spec h)

theorem generated_repayStateGasSpill_refines_adapter
    {state : ProductionGasState}
    (h : TransitionSpec.repayStateGasSpillNoOverflow state) :
    projectVoid (TransitionRefinement.generatedResultToSpec
      (GeneratedTransition.repayStateGasSpill
        state.gasLeft state.stateReservoir state.stateGasUsed state.stateGasSpill
        state.stateGasSpillRefunded)) =
      projectVoid (TransitionSpec.repayStateGasSpill state) := by
  simpa only [TransitionRefinement.generatedResultToSpec,
    GeneratedTransition.repayStateGasSpill, TransitionSpec.repayStateGasSpill] using
      congrArg projectVoid
        (Eip803x.Refinement.StateGasTransition.generated_repayStateGasSpill_matches_spec h)

theorem generated_restoreChildStateGas_refines_adapter
    {parent : ProductionGasState} {child : TransitionSpec.Child}
    (h : TransitionSpec.restoreChildStateGasNoOverflow parent child) :
    projectVoid (TransitionRefinement.generatedResultToSpec
      (GeneratedTransition.restoreChildStateGas
        parent.gasLeft parent.stateReservoir parent.stateGasUsed parent.stateGasSpill
        parent.stateGasSpillRefunded child.stateReservoir child.stateGasUsed
        child.stateGasSpill child.stateGasSpillRefunded)) =
      projectVoid (TransitionSpec.restoreChildStateGas parent child) := by
  simpa only [TransitionRefinement.generatedResultToSpec,
    GeneratedTransition.restoreChildStateGas, TransitionSpec.restoreChildStateGas] using
      congrArg projectVoid
        (Eip803x.Refinement.StateGasTransition.generated_restoreChildStateGas_matches_spec h)

theorem generated_restoreChildStateGasOnHalt_refines_adapter
    {parent : ProductionGasState} {child : TransitionSpec.Child}
    (h : TransitionSpec.restoreChildStateGasOnHaltNoOverflow parent child) :
    projectVoid (TransitionRefinement.generatedResultToSpec
      (GeneratedTransition.restoreChildStateGasOnHalt
        parent.gasLeft parent.stateReservoir parent.stateGasUsed parent.stateGasSpill
        parent.stateGasSpillRefunded child.stateReservoir child.stateGasUsed
        child.stateGasSpill child.stateGasSpillRefunded)) =
      projectVoid (TransitionSpec.restoreChildStateGasOnHalt parent child) := by
  simpa only [TransitionRefinement.generatedResultToSpec,
    GeneratedTransition.restoreChildStateGasOnHalt,
    TransitionSpec.restoreChildStateGasOnHalt] using congrArg projectVoid
      (Eip803x.Refinement.StateGasTransition.generated_restoreChildStateGasOnHalt_matches_spec h)

theorem generated_revertRefundToHalt_refines_adapter
    {parent : ProductionGasState} {child : TransitionSpec.Child}
    (h : TransitionSpec.revertRefundToHaltNoOverflow parent child) :
    projectVoid (TransitionRefinement.generatedResultToSpec
      (GeneratedTransition.revertRefundToHalt
        parent.gasLeft parent.stateReservoir parent.stateGasUsed parent.stateGasSpill
        parent.stateGasSpillRefunded child.stateGasUsed child.stateGasSpill
        child.stateGasSpillRefunded)) =
      projectVoid (TransitionSpec.revertRefundToHalt parent child) := by
  simpa only [TransitionRefinement.generatedResultToSpec,
    GeneratedTransition.revertRefundToHalt, TransitionSpec.revertRefundToHalt] using
      congrArg projectVoid
        (Eip803x.Refinement.StateGasTransition.generated_revertRefundToHalt_matches_spec h)

theorem generated_refundStateGas_refines_adapter
    {state : ProductionGasState} {amount stateGasFloor : Int} {trackSpillRefund : Bool}
    (h : TransitionSpec.refundStateGasNoOverflow
      state amount stateGasFloor trackSpillRefund) :
    projectVoid (TransitionRefinement.generatedResultToSpec
      (GeneratedTransition.refundStateGas
        state.gasLeft state.stateReservoir state.stateGasUsed state.stateGasSpill
        state.stateGasSpillRefunded amount stateGasFloor trackSpillRefund)) =
      projectVoid
        (TransitionSpec.refundStateGas state amount stateGasFloor trackSpillRefund) := by
  simpa only [TransitionRefinement.generatedResultToSpec,
    GeneratedTransition.refundStateGas, TransitionSpec.refundStateGas] using
      congrArg projectVoid
        (Eip803x.Refinement.StateGasTransition.generated_refundStateGas_matches_spec h)

theorem generated_discardStateGas_refines_adapter
    {state : ProductionGasState} {amount stateGasFloor : Int}
    (h : TransitionSpec.discardStateGasNoOverflow state amount stateGasFloor) :
    projectDiscard (TransitionRefinement.generatedResultToSpec
      (GeneratedTransition.discardStateGas
        state.gasLeft state.stateReservoir state.stateGasUsed state.stateGasSpill
        state.stateGasSpillRefunded amount stateGasFloor)) =
      projectDiscard (TransitionSpec.discardStateGas state amount stateGasFloor) := by
  simpa only [TransitionRefinement.generatedResultToSpec,
    GeneratedTransition.discardStateGas, TransitionSpec.discardStateGas] using
      congrArg projectDiscard
        (Eip803x.Refinement.StateGasTransition.generated_discardStateGas_matches_spec h)

theorem generated_addStateGasRefundToReservoir_refines_adapter
    {state : ProductionGasState} {amount : Int} {trackSpillRefund : Bool}
    (h : TransitionSpec.addStateGasRefundToReservoirNoOverflow
      state amount trackSpillRefund) :
    projectVoid (TransitionRefinement.generatedResultToSpec
      (GeneratedTransition.addStateGasRefundToReservoir
        state.gasLeft state.stateReservoir state.stateGasUsed state.stateGasSpill
        state.stateGasSpillRefunded amount trackSpillRefund)) =
      projectVoid
        (TransitionSpec.addStateGasRefundToReservoir state amount trackSpillRefund) := by
  simpa only [TransitionRefinement.generatedResultToSpec,
    GeneratedTransition.addStateGasRefundToReservoir,
    TransitionSpec.addStateGasRefundToReservoir] using congrArg projectVoid
      (Eip803x.Refinement.StateGasTransition.generated_addStateGasRefundToReservoir_matches_spec h)

theorem generated_removeStateGasRefundFromReservoir_refines_adapter
    {state : ProductionGasState} {amount : Int}
    (hAmount : 0 ≤ amount)
    (h : TransitionSpec.removeStateGasRefundFromReservoirNoOverflow state amount) :
    .completed (projectVoid (TransitionRefinement.generatedResultToSpec
      (GeneratedTransition.removeStateGasRefundFromReservoir
        state.gasLeft state.stateReservoir state.stateGasUsed state.stateGasSpill
        state.stateGasSpillRefunded amount))) =
      Adapter.removeStateGasRefundFromReservoir state amount := by
  have hGenerated := congrArg projectVoid
    (Eip803x.Refinement.StateGasTransition.generated_removeStateGasRefundFromReservoir_matches_spec h)
  rw [hGenerated]
  symm
  exact remove_nonnegative_applies_kernel_projection state hAmount

/--
The common gas machine and the production policy agree on spill repayment when
the represented reservoir is positive.  In that domain the signed production
reservoir is exactly the natural reservoir rather than its clipped projection.
-/
theorem repayStateGasSpill_positive_reservoir_refines_gas_machine
    {production : ProductionGasState} {model : GasState}
    (hRepresents : ProductionGas.Represents production model)
    (hWellFormed : ProductionGas.WellFormed production)
    (hReservoirPositive : 0 < production.stateReservoir) :
    ProductionGas.Represents
      (TransitionSpec.resultState (TransitionSpec.repayStateGasSpill production))
      (GasMachine.repayStateFromGasLeft model) := by
  rcases hRepresents with ⟨hGasLeft, hReservoir, hFromGasLeft, hStateUsed⟩
  rcases hWellFormed with ⟨hUsedNonnegative, hSpillNonnegative,
    hRefundedNonnegative, hRefundedLeSpill⟩
  have hReservoirEq : (model.stateReservoir : Int) = production.stateReservoir := by
    rw [hReservoir, Int.max_eq_left (by omega : 0 ≤ production.stateReservoir)]
  have hUnrefundedEq :
      TransitionSpec.unrefundedSpill
          production.stateGasSpill production.stateGasSpillRefunded =
        production.stateGasSpill - production.stateGasSpillRefunded := by
    change (if production.stateGasSpill - production.stateGasSpillRefunded > 0 then
      production.stateGasSpill - production.stateGasSpillRefunded else 0) =
        production.stateGasSpill - production.stateGasSpillRefunded
    split <;> omega
  have hNetEq :
      ProductionGas.netUnrefundedSpill production =
        production.stateGasSpill - production.stateGasSpillRefunded := by
    exact ProductionGas.netUnrefundedSpill_eq production
      ⟨hUsedNonnegative, hSpillNonnegative, hRefundedNonnegative, hRefundedLeSpill⟩
  have hFromEq :
      (model.stateFromGasLeft : Int) =
        production.stateGasSpill - production.stateGasSpillRefunded := by
    rw [hFromGasLeft, hNetEq]
  let repayment : Nat := min model.stateReservoir model.stateFromGasLeft
  have hRepaymentLeReservoir : repayment ≤ model.stateReservoir :=
    Nat.min_le_left _ _
  have hRepaymentLeSpill : repayment ≤ model.stateFromGasLeft :=
    Nat.min_le_right _ _
  have hRepaymentEq :
      (repayment : Int) = min production.stateReservoir
        (TransitionSpec.unrefundedSpill
          production.stateGasSpill production.stateGasSpillRefunded) := by
    dsimp [repayment]
    by_cases hLe : model.stateReservoir ≤ model.stateFromGasLeft
    · rw [Nat.min_eq_left hLe, Int.min_eq_left]
      · exact hReservoirEq
      · omega
    · have hReverse : model.stateFromGasLeft ≤ model.stateReservoir := by omega
      rw [Nat.min_eq_right hReverse, Int.min_eq_right]
      · rw [hFromEq, hUnrefundedEq]
      · omega
  have hSpecState :
      TransitionSpec.resultState (TransitionSpec.repayStateGasSpill production) =
        { production with
          gasLeft := production.gasLeft + repayment
          stateReservoir := production.stateReservoir - repayment
          stateGasSpillRefunded := production.stateGasSpillRefunded + repayment } := by
    unfold TransitionSpec.resultState TransitionSpec.repayStateGasSpill
      Eip803x.Refinement.StateGasTransition.Spec.resultState
      Eip803x.Refinement.StateGasTransition.Spec.repayStateGasSpill
    by_cases hZero : repayment = 0
    · have hSignedZero :
          min production.stateReservoir
            (TransitionSpec.unrefundedSpill
              production.stateGasSpill production.stateGasSpillRefunded) = 0 := by
        omega
      simp [hSignedZero, hZero,
        Eip803x.Refinement.StateGasTransition.Spec.result]
    ·
      simp [hZero, ← hRepaymentEq]
  have hReservoirAfterNonnegative :
      0 ≤ production.stateReservoir - repayment := by
    omega
  have hUnrefundedAfter :
      ProductionGas.netUnrefundedSpill
        { production with
          gasLeft := production.gasLeft + repayment
          stateReservoir := production.stateReservoir - repayment
          stateGasSpillRefunded := production.stateGasSpillRefunded + repayment } =
        production.stateGasSpill - production.stateGasSpillRefunded - repayment := by
    unfold ProductionGas.netUnrefundedSpill
    change max
      (production.stateGasSpill - (production.stateGasSpillRefunded + repayment)) 0 =
        production.stateGasSpill - production.stateGasSpillRefunded - repayment
    rw [Int.max_eq_left (by omega)]
    omega
  rw [hSpecState]
  simp only [ProductionGas.Represents, GasMachine.repayStateFromGasLeft]
  constructor
  · omega
  constructor
  · rw [Int.max_eq_left hReservoirAfterNonnegative]
    omega
  constructor
  · rw [hUnrefundedAfter]
    omega
  · rw [Int.max_eq_left hUsedNonnegative]
    omega

theorem generated_repayStateGasSpill_positive_refines_gas_machine
    {production : ProductionGasState} {model : GasState}
    (hRepresents : ProductionGas.Represents production model)
    (hWellFormed : ProductionGas.WellFormed production)
    (hReservoirPositive : 0 < production.stateReservoir)
    (hNoOverflow : TransitionSpec.repayStateGasSpillNoOverflow production) :
    ProductionGas.Represents
      (projectState (TransitionRefinement.generatedResultToSpec
        (GeneratedTransition.repayStateGasSpill
          production.gasLeft production.stateReservoir production.stateGasUsed
          production.stateGasSpill production.stateGasSpillRefunded)))
      (GasMachine.repayStateFromGasLeft model) := by
  have hGenerated := congrArg projectState
    (Eip803x.Refinement.StateGasTransition.generated_repayStateGasSpill_matches_spec
      hNoOverflow)
  rw [hGenerated]
  exact repayStateGasSpill_positive_reservoir_refines_gas_machine
    hRepresents hWellFormed hReservoirPositive

/--
The restore kernels intentionally erase the `FrameGasState` baselines.  Two
frames can therefore have the same kernel-visible child fields but different
rollback results; a general `restoreRevert` theorem requires those baselines
and the refund journal from the production caller.
-/
def frameKernelProjection (frame : FrameGasState) : TransitionSpec.Child :=
  { stateReservoir := frame.gas.stateReservoir
    stateGasUsed := frame.gas.stateUsed
    stateGasSpill := frame.gas.stateFromGasLeft
    stateGasSpillRefunded := 0 }

theorem restoreRevert_not_determined_by_kernel_projection :
    ∃ left right : FrameGasState,
      GasMachine.FrameInvariant left ∧
        GasMachine.FrameInvariant right ∧
        frameKernelProjection left = frameKernelProjection right ∧
        GasMachine.restoreRevert left ≠ GasMachine.restoreRevert right := by
  let gas : GasState :=
    { gasLeft := 1
      stateReservoir := 2
      stateFromGasLeft := 3
      stateUsed := 4
      refundCounter := 5 }
  refine ⟨
    { gas, stateGasBaseline := 1, stateUsedBaseline := 2, refundCounterBaseline := 8 },
    { gas, stateGasBaseline := 2, stateUsedBaseline := 1, refundCounterBaseline := 8 },
    ?_, ?_, rfl, ?_⟩
  · rfl
  · rfl
  · simp [GasMachine.restoreRevert]

end Eip803x.Refinement.StateGasTransitionAdapter
