-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Generated.StateGasTransitionAdapterKernel
import Eip803x.Refinement.StateGasTransitionAdapter
import Lean.Elab.Tactic.Omega

namespace Eip803x.Refinement.StateGasTransitionAdapterKernel

open Eip803x

namespace AdapterSpec

/-- The value-level completion class selected by the production adapter boundary. -/
inductive OutcomeKind where
  | completedVoid
  | completedDiscard
  | argumentException
  deriving DecidableEq, Repr

/-- The complete result retained before the public adapter projects its five written fields. -/
structure Outcome where
  kind : OutcomeKind
  transition : Eip803x.Refinement.StateGasTransition.Spec.Result
  deriving DecidableEq, Repr

def refund (parent child : ProductionGasState) : Outcome :=
  { kind := .completedVoid
    transition := Eip803x.Refinement.StateGasTransition.Spec.refund parent child }

def repayStateGasSpill (state : ProductionGasState) : Outcome :=
  { kind := .completedVoid
    transition := Eip803x.Refinement.StateGasTransition.Spec.repayStateGasSpill state }

def restoreChildStateGas (parent child : ProductionGasState) : Outcome :=
  { kind := .completedVoid
    transition := Eip803x.Refinement.StateGasTransition.Spec.restoreChildStateGas parent
      (Eip803x.Refinement.StateGasTransition.Spec.child child) }

def restoreChildStateGasOnHalt (parent child : ProductionGasState) : Outcome :=
  { kind := .completedVoid
    transition := Eip803x.Refinement.StateGasTransition.Spec.restoreChildStateGasOnHalt parent
      (Eip803x.Refinement.StateGasTransition.Spec.child child) }

def revertRefundToHalt (parent child : ProductionGasState) : Outcome :=
  { kind := .completedVoid
    transition := Eip803x.Refinement.StateGasTransition.Spec.revertRefundToHalt parent
      (Eip803x.Refinement.StateGasTransition.Spec.child child) }

def refundStateGas (state : ProductionGasState) (amount stateGasFloor : Int)
    (trackSpillRefund : Bool) : Outcome :=
  { kind := .completedVoid
    transition := Eip803x.Refinement.StateGasTransition.Spec.refundStateGas
      state amount stateGasFloor trackSpillRefund }

def discardStateGas (state : ProductionGasState) (amount stateGasFloor : Int) : Outcome :=
  { kind := .completedDiscard
    transition := Eip803x.Refinement.StateGasTransition.Spec.discardStateGas
      state amount stateGasFloor }

def addStateGasRefundToReservoir (state : ProductionGasState) (amount : Int)
    (trackSpillRefund : Bool) : Outcome :=
  { kind := .completedVoid
    transition := Eip803x.Refinement.StateGasTransition.Spec.addStateGasRefundToReservoir
      state amount trackSpillRefund }

def removeStateGasRefundFromReservoir (state : ProductionGasState) (amount : Int) : Outcome :=
  if amount < 0 then
    { kind := .argumentException
      transition := Eip803x.Refinement.StateGasTransition.Spec.result state }
  else
    { kind := .completedVoid
      transition := Eip803x.Refinement.StateGasTransition.Spec.removeStateGasRefundFromReservoir
        state amount }

end AdapterSpec

/-- Maps the full generated outcome, including `UnappliedAmount`, into the independent adapter spec. -/
def generatedOutcomeToSpec
    (outcome : Eip803x.Generated.StateGasTransitionAdapterKernel.Outcome) : AdapterSpec.Outcome :=
  { kind := match outcome.kind with
      | .completedVoid => .completedVoid
      | .completedDiscard => .completedDiscard
      | .argumentException => .argumentException
    transition := Eip803x.Refinement.StateGasTransition.generatedResultToSpec outcome.transition }

/-- The exact public projection after `ApplyStateGasTransition` and the adapter return channel. -/
def projectOutcome (outcome : AdapterSpec.Outcome) :
    Eip803x.Refinement.StateGasTransitionAdapter.Outcome :=
  match outcome.kind with
  | .completedVoid => .completed
      (Eip803x.Refinement.StateGasTransitionAdapter.projectVoid outcome.transition)
  | .completedDiscard => .completed
      (Eip803x.Refinement.StateGasTransitionAdapter.projectDiscard outcome.transition)
  | .argumentException => .argumentException
      (Eip803x.Refinement.StateGasTransition.Spec.resultState outcome.transition)

theorem generatedOutcomeToSpec_preserves_transition
    (outcome : Eip803x.Generated.StateGasTransitionAdapterKernel.Outcome) :
    (generatedOutcomeToSpec outcome).transition =
      Eip803x.Refinement.StateGasTransition.generatedResultToSpec outcome.transition := by
  rfl

theorem projectOutcome_completedVoid_fields
    (result : Eip803x.Refinement.StateGasTransition.Spec.Result) :
    projectOutcome { kind := .completedVoid, transition := result } =
      .completed (Eip803x.Refinement.StateGasTransitionAdapter.projectVoid result) := by
  rfl

theorem projectOutcome_completedDiscard_fields
    (result : Eip803x.Refinement.StateGasTransition.Spec.Result) :
    projectOutcome { kind := .completedDiscard, transition := result } =
      .completed (Eip803x.Refinement.StateGasTransitionAdapter.projectDiscard result) := by
  rfl

theorem projectOutcome_argumentException_fields
    (result : Eip803x.Refinement.StateGasTransition.Spec.Result) :
    projectOutcome { kind := .argumentException, transition := result } =
      .argumentException (Eip803x.Refinement.StateGasTransition.Spec.resultState result) := by
  rfl

theorem generated_refund_outcome_eq_spec
    {parent child : ProductionGasState}
    (h : Eip803x.Refinement.StateGasTransition.Spec.refundNoOverflow parent child) :
    generatedOutcomeToSpec
      (Eip803x.Generated.StateGasTransitionAdapterKernel.refund
        parent.gasLeft parent.stateReservoir parent.stateGasUsed parent.stateGasSpill
        parent.stateGasSpillRefunded child.gasLeft child.stateReservoir child.stateGasUsed
        child.stateGasSpill child.stateGasSpillRefunded) =
      AdapterSpec.refund parent child := by
  simp only [Eip803x.Generated.StateGasTransitionAdapterKernel.refund, generatedOutcomeToSpec,
    AdapterSpec.refund]
  rw [Eip803x.Refinement.StateGasTransition.generated_refund_matches_spec h]

theorem generated_repayStateGasSpill_outcome_eq_spec
    {state : ProductionGasState}
    (h : Eip803x.Refinement.StateGasTransition.Spec.repayStateGasSpillNoOverflow state) :
    generatedOutcomeToSpec
      (Eip803x.Generated.StateGasTransitionAdapterKernel.repayStateGasSpill
        state.gasLeft state.stateReservoir state.stateGasUsed state.stateGasSpill
        state.stateGasSpillRefunded) =
      AdapterSpec.repayStateGasSpill state := by
  simp only [Eip803x.Generated.StateGasTransitionAdapterKernel.repayStateGasSpill,
    generatedOutcomeToSpec, AdapterSpec.repayStateGasSpill]
  rw [Eip803x.Refinement.StateGasTransition.generated_repayStateGasSpill_matches_spec h]

theorem generated_restoreChildStateGas_outcome_eq_spec
    {parent child : ProductionGasState}
    (h : Eip803x.Refinement.StateGasTransition.Spec.restoreChildStateGasNoOverflow parent
      (Eip803x.Refinement.StateGasTransition.Spec.child child)) :
    generatedOutcomeToSpec
      (Eip803x.Generated.StateGasTransitionAdapterKernel.restoreChildStateGas
        parent.gasLeft parent.stateReservoir parent.stateGasUsed parent.stateGasSpill
        parent.stateGasSpillRefunded child.stateReservoir child.stateGasUsed child.stateGasSpill
        child.stateGasSpillRefunded) =
      AdapterSpec.restoreChildStateGas parent child := by
  simpa only [Eip803x.Generated.StateGasTransitionAdapterKernel.restoreChildStateGas,
    generatedOutcomeToSpec, AdapterSpec.restoreChildStateGas,
    Eip803x.Refinement.StateGasTransition.Spec.child] using congrArg
      (fun result => AdapterSpec.Outcome.mk .completedVoid result)
      (Eip803x.Refinement.StateGasTransition.generated_restoreChildStateGas_matches_spec h)

theorem generated_restoreChildStateGasOnHalt_outcome_eq_spec
    {parent child : ProductionGasState}
    (h : Eip803x.Refinement.StateGasTransition.Spec.restoreChildStateGasOnHaltNoOverflow parent
      (Eip803x.Refinement.StateGasTransition.Spec.child child)) :
    generatedOutcomeToSpec
      (Eip803x.Generated.StateGasTransitionAdapterKernel.restoreChildStateGasOnHalt
        parent.gasLeft parent.stateReservoir parent.stateGasUsed parent.stateGasSpill
        parent.stateGasSpillRefunded child.stateReservoir child.stateGasUsed child.stateGasSpill
        child.stateGasSpillRefunded) =
      AdapterSpec.restoreChildStateGasOnHalt parent child := by
  simpa only [Eip803x.Generated.StateGasTransitionAdapterKernel.restoreChildStateGasOnHalt,
    generatedOutcomeToSpec, AdapterSpec.restoreChildStateGasOnHalt,
    Eip803x.Refinement.StateGasTransition.Spec.child] using congrArg
      (fun result => AdapterSpec.Outcome.mk .completedVoid result)
      (Eip803x.Refinement.StateGasTransition.generated_restoreChildStateGasOnHalt_matches_spec h)

theorem generated_revertRefundToHalt_outcome_eq_spec
    {parent child : ProductionGasState}
    (h : Eip803x.Refinement.StateGasTransition.Spec.revertRefundToHaltNoOverflow parent
      (Eip803x.Refinement.StateGasTransition.Spec.child child)) :
    generatedOutcomeToSpec
      (Eip803x.Generated.StateGasTransitionAdapterKernel.revertRefundToHalt
        parent.gasLeft parent.stateReservoir parent.stateGasUsed parent.stateGasSpill
        parent.stateGasSpillRefunded child.stateGasUsed child.stateGasSpill child.stateGasSpillRefunded) =
      AdapterSpec.revertRefundToHalt parent child := by
  simpa only [Eip803x.Generated.StateGasTransitionAdapterKernel.revertRefundToHalt,
    generatedOutcomeToSpec, AdapterSpec.revertRefundToHalt,
    Eip803x.Refinement.StateGasTransition.Spec.child] using congrArg
      (fun result => AdapterSpec.Outcome.mk .completedVoid result)
      (Eip803x.Refinement.StateGasTransition.generated_revertRefundToHalt_matches_spec h)

theorem generated_refundStateGas_outcome_eq_spec
    {state : ProductionGasState} {amount stateGasFloor : Int} {trackSpillRefund : Bool}
    (h : Eip803x.Refinement.StateGasTransition.Spec.refundStateGasNoOverflow
      state amount stateGasFloor trackSpillRefund) :
    generatedOutcomeToSpec
      (Eip803x.Generated.StateGasTransitionAdapterKernel.refundStateGas
        state.gasLeft state.stateReservoir state.stateGasUsed state.stateGasSpill
        state.stateGasSpillRefunded amount stateGasFloor trackSpillRefund) =
      AdapterSpec.refundStateGas state amount stateGasFloor trackSpillRefund := by
  simp only [Eip803x.Generated.StateGasTransitionAdapterKernel.refundStateGas,
    generatedOutcomeToSpec, AdapterSpec.refundStateGas]
  rw [Eip803x.Refinement.StateGasTransition.generated_refundStateGas_matches_spec h]

theorem generated_discardStateGas_outcome_eq_spec
    {state : ProductionGasState} {amount stateGasFloor : Int}
    (h : Eip803x.Refinement.StateGasTransition.Spec.discardStateGasNoOverflow
      state amount stateGasFloor) :
    generatedOutcomeToSpec
      (Eip803x.Generated.StateGasTransitionAdapterKernel.discardStateGas
        state.gasLeft state.stateReservoir state.stateGasUsed state.stateGasSpill
        state.stateGasSpillRefunded amount stateGasFloor) =
      AdapterSpec.discardStateGas state amount stateGasFloor := by
  simp only [Eip803x.Generated.StateGasTransitionAdapterKernel.discardStateGas,
    generatedOutcomeToSpec, AdapterSpec.discardStateGas]
  rw [Eip803x.Refinement.StateGasTransition.generated_discardStateGas_matches_spec h]

theorem generated_addStateGasRefundToReservoir_outcome_eq_spec
    {state : ProductionGasState} {amount : Int} {trackSpillRefund : Bool}
    (h : Eip803x.Refinement.StateGasTransition.Spec.addStateGasRefundToReservoirNoOverflow
      state amount trackSpillRefund) :
    generatedOutcomeToSpec
      (Eip803x.Generated.StateGasTransitionAdapterKernel.addStateGasRefundToReservoir
        state.gasLeft state.stateReservoir state.stateGasUsed state.stateGasSpill
        state.stateGasSpillRefunded amount trackSpillRefund) =
      AdapterSpec.addStateGasRefundToReservoir state amount trackSpillRefund := by
  simp only [Eip803x.Generated.StateGasTransitionAdapterKernel.addStateGasRefundToReservoir,
    generatedOutcomeToSpec, AdapterSpec.addStateGasRefundToReservoir]
  rw [Eip803x.Refinement.StateGasTransition.generated_addStateGasRefundToReservoir_matches_spec h]

theorem generated_remove_nonnegative_outcome_eq_spec
    {state : ProductionGasState} {amount : Int}
    (hAmount : 0 ≤ amount)
    (h : Eip803x.Refinement.StateGasTransition.Spec.removeStateGasRefundFromReservoirNoOverflow
      state amount) :
    generatedOutcomeToSpec
      (Eip803x.Generated.StateGasTransitionAdapterKernel.removeStateGasRefundFromReservoir
        state.gasLeft state.stateReservoir state.stateGasUsed state.stateGasSpill
        state.stateGasSpillRefunded amount) =
      AdapterSpec.removeStateGasRefundFromReservoir state amount := by
  have hNotNegative : ¬ amount < 0 := by omega
  simp only [Eip803x.Generated.StateGasTransitionAdapterKernel.removeStateGasRefundFromReservoir,
    if_neg hNotNegative, generatedOutcomeToSpec,
    AdapterSpec.removeStateGasRefundFromReservoir]
  rw [Eip803x.Refinement.StateGasTransition.generated_removeStateGasRefundFromReservoir_matches_spec h]

theorem generated_unchanged_eq_spec_result
    (state : ProductionGasState)
    (h : ProductionGas.MachineBounded state) :
    Eip803x.Refinement.StateGasTransition.generatedResultToSpec
      (Eip803x.Generated.StateGasTransitionAdapterKernel.unchanged
        state.gasLeft state.stateReservoir state.stateGasUsed state.stateGasSpill
        state.stateGasSpillRefunded) =
      Eip803x.Refinement.StateGasTransition.Spec.result state := by
  rcases h with ⟨hValue, hReservoir, hUsed, hSpill, hRefunded⟩
  have hValueCore : state.gasLeft ≤ Eip803x.Generated.StateGasTransitionKernel.uint64Max := by
    simpa [Eip803x.Generated.StateGasTransitionKernel.uint64Max,
      Eip803x.Generated.StateGasTransitionKernel.uint64Modulus,
      ProductionGas.FitsUInt64, ProductionGas.uint64Max] using hValue
  have hReservoirLower : Eip803x.Generated.StateGasTransitionKernel.int64Min ≤ state.stateReservoir := by
    simpa [Eip803x.Generated.StateGasTransitionKernel.int64Min,
      Eip803x.Generated.StateGasTransitionKernel.int64SignBit,
      ProductionGas.int64Min] using hReservoir.1
  have hReservoirUpper : state.stateReservoir ≤ Eip803x.Generated.StateGasTransitionKernel.int64Max := by
    simpa [Eip803x.Generated.StateGasTransitionKernel.int64Max,
      Eip803x.Generated.StateGasTransitionKernel.int64SignBit,
      ProductionGas.int64Max] using hReservoir.2
  have hUsedLower : Eip803x.Generated.StateGasTransitionKernel.int64Min ≤ state.stateGasUsed := by
    simpa [Eip803x.Generated.StateGasTransitionKernel.int64Min,
      Eip803x.Generated.StateGasTransitionKernel.int64SignBit,
      ProductionGas.int64Min] using hUsed.1
  have hUsedUpper : state.stateGasUsed ≤ Eip803x.Generated.StateGasTransitionKernel.int64Max := by
    simpa [Eip803x.Generated.StateGasTransitionKernel.int64Max,
      Eip803x.Generated.StateGasTransitionKernel.int64SignBit,
      ProductionGas.int64Max] using hUsed.2
  have hSpillLower : Eip803x.Generated.StateGasTransitionKernel.int64Min ≤ state.stateGasSpill := by
    simpa [Eip803x.Generated.StateGasTransitionKernel.int64Min,
      Eip803x.Generated.StateGasTransitionKernel.int64SignBit,
      ProductionGas.int64Min] using hSpill.1
  have hSpillUpper : state.stateGasSpill ≤ Eip803x.Generated.StateGasTransitionKernel.int64Max := by
    simpa [Eip803x.Generated.StateGasTransitionKernel.int64Max,
      Eip803x.Generated.StateGasTransitionKernel.int64SignBit,
      ProductionGas.int64Max] using hSpill.2
  have hRefundedLower : Eip803x.Generated.StateGasTransitionKernel.int64Min ≤ state.stateGasSpillRefunded := by
    simpa [Eip803x.Generated.StateGasTransitionKernel.int64Min,
      Eip803x.Generated.StateGasTransitionKernel.int64SignBit,
      ProductionGas.int64Min] using hRefunded.1
  have hRefundedUpper : state.stateGasSpillRefunded ≤ Eip803x.Generated.StateGasTransitionKernel.int64Max := by
    simpa [Eip803x.Generated.StateGasTransitionKernel.int64Max,
      Eip803x.Generated.StateGasTransitionKernel.int64SignBit,
      ProductionGas.int64Max] using hRefunded.2
  simp [Eip803x.Generated.StateGasTransitionAdapterKernel.unchanged,
    Eip803x.Refinement.StateGasTransition.generatedResultToSpec,
    Eip803x.Generated.StateGasTransitionKernel.normalizeUInt64, hValueCore,
    Eip803x.Generated.StateGasTransitionKernel.wrapInt64,
    hReservoirLower, hReservoirUpper, hUsedLower, hUsedUpper,
    hSpillLower, hSpillUpper, hRefundedLower, hRefundedUpper,
    Eip803x.Refinement.StateGasTransition.Spec.result]

theorem generated_remove_negative_outcome_eq_spec
    {state : ProductionGasState} {amount : Int}
    (hState : ProductionGas.MachineBounded state)
    (hAmount : amount < 0) :
    generatedOutcomeToSpec
      (Eip803x.Generated.StateGasTransitionAdapterKernel.removeStateGasRefundFromReservoir
        state.gasLeft state.stateReservoir state.stateGasUsed state.stateGasSpill
        state.stateGasSpillRefunded amount) =
      AdapterSpec.removeStateGasRefundFromReservoir state amount := by
  rcases hState with ⟨hValue, hReservoir, hUsed, hSpill, hRefunded⟩
  simp only [Eip803x.Generated.StateGasTransitionAdapterKernel.removeStateGasRefundFromReservoir,
    if_pos hAmount, generatedOutcomeToSpec,
    AdapterSpec.removeStateGasRefundFromReservoir]
  exact congrArg (fun transition =>
    AdapterSpec.Outcome.mk .argumentException transition)
    (generated_unchanged_eq_spec_result state ⟨hValue, hReservoir, hUsed, hSpill, hRefunded⟩)

theorem project_refund_eq_public_adapter (parent child : ProductionGasState) :
    projectOutcome (AdapterSpec.refund parent child) =
      Eip803x.Refinement.StateGasTransitionAdapter.Adapter.refund parent child := by
  rfl

theorem project_discardStateGas_eq_public_adapter
    (state : ProductionGasState) (amount stateGasFloor : Int) :
    projectOutcome (AdapterSpec.discardStateGas state amount stateGasFloor) =
      Eip803x.Refinement.StateGasTransitionAdapter.Adapter.discardStateGas state amount stateGasFloor := by
  rfl

theorem project_remove_eq_public_adapter
    (state : ProductionGasState) (amount : Int) :
    projectOutcome (AdapterSpec.removeStateGasRefundFromReservoir state amount) =
      Eip803x.Refinement.StateGasTransitionAdapter.Adapter.removeStateGasRefundFromReservoir state amount := by
  by_cases h : amount < 0 <;>
    simp [projectOutcome, AdapterSpec.removeStateGasRefundFromReservoir,
      Eip803x.Refinement.StateGasTransitionAdapter.Adapter.removeStateGasRefundFromReservoir,
      Eip803x.Refinement.StateGasTransition.Spec.resultState,
      Eip803x.Refinement.StateGasTransition.Spec.result, h]

theorem generated_refund_projects_to_public_adapter
    {parent child : ProductionGasState}
    (h : Eip803x.Refinement.StateGasTransition.Spec.refundNoOverflow parent child) :
    projectOutcome (generatedOutcomeToSpec
      (Eip803x.Generated.StateGasTransitionAdapterKernel.refund
        parent.gasLeft parent.stateReservoir parent.stateGasUsed parent.stateGasSpill
        parent.stateGasSpillRefunded child.gasLeft child.stateReservoir child.stateGasUsed
        child.stateGasSpill child.stateGasSpillRefunded)) =
      Eip803x.Refinement.StateGasTransitionAdapter.Adapter.refund parent child := by
  rw [generated_refund_outcome_eq_spec h, project_refund_eq_public_adapter]

theorem generated_discardStateGas_projects_to_public_adapter
    {state : ProductionGasState} {amount stateGasFloor : Int}
    (h : Eip803x.Refinement.StateGasTransition.Spec.discardStateGasNoOverflow
      state amount stateGasFloor) :
    projectOutcome (generatedOutcomeToSpec
      (Eip803x.Generated.StateGasTransitionAdapterKernel.discardStateGas
        state.gasLeft state.stateReservoir state.stateGasUsed state.stateGasSpill
        state.stateGasSpillRefunded amount stateGasFloor)) =
      Eip803x.Refinement.StateGasTransitionAdapter.Adapter.discardStateGas state amount stateGasFloor := by
  rw [generated_discardStateGas_outcome_eq_spec h, project_discardStateGas_eq_public_adapter]

theorem generated_remove_projects_to_public_adapter
    {state : ProductionGasState} {amount : Int}
    (hAmount : 0 ≤ amount)
    (h : Eip803x.Refinement.StateGasTransition.Spec.removeStateGasRefundFromReservoirNoOverflow
      state amount) :
    projectOutcome (generatedOutcomeToSpec
      (Eip803x.Generated.StateGasTransitionAdapterKernel.removeStateGasRefundFromReservoir
        state.gasLeft state.stateReservoir state.stateGasUsed state.stateGasSpill
        state.stateGasSpillRefunded amount)) =
      Eip803x.Refinement.StateGasTransitionAdapter.Adapter.removeStateGasRefundFromReservoir state amount := by
  rw [generated_remove_nonnegative_outcome_eq_spec hAmount h, project_remove_eq_public_adapter]

end Eip803x.Refinement.StateGasTransitionAdapterKernel
