-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import AuthorizationStateGasFoldExtractor.Generated.AuthorizationStateGasFold
import Eip803x.Production
import Eip803x.Refinement.TransactionGasInitialization
import Eip803x.Refinement.StateGasCharge
import Eip803x.Refinement.StateGasTransition
import Lean.Elab.Tactic.Omega

namespace Eip803x.Refinement.AuthorizationStateGasFold

namespace Generated

abbrev Stage := Eip803x.Generated.AuthorizationStateGasFold.Stage
abbrev GasOwner := Eip803x.Generated.AuthorizationStateGasFold.GasOwner
abbrev GasField := Eip803x.Generated.AuthorizationStateGasFold.GasField
abbrev GasPolicyState := Eip803x.Generated.AuthorizationStateGasFold.GasPolicyState
abbrev AuthorizationEffect := Eip803x.Generated.AuthorizationStateGasFold.AuthorizationEffect
abbrev AuthorizationOutcome := Eip803x.Generated.AuthorizationStateGasFold.AuthorizationOutcome
abbrev ProcessInput := Eip803x.Generated.AuthorizationStateGasFold.ProcessInput
abbrev ProcessOutcome := Eip803x.Generated.AuthorizationStateGasFold.ProcessOutcome
abbrev stateChargeSpill := Eip803x.Generated.AuthorizationStateGasFold.stateChargeSpill
abbrev chargeState := Eip803x.Generated.AuthorizationStateGasFold.chargeState
abbrev chargeExecution := Eip803x.Generated.AuthorizationStateGasFold.chargeExecution
abbrev applyAuthorization := Eip803x.Generated.AuthorizationStateGasFold.applyAuthorization
abbrev processLoop := Eip803x.Generated.AuthorizationStateGasFold.processLoop
abbrev processDelegations := Eip803x.Generated.AuthorizationStateGasFold.processDelegations
abbrev foldTopFrameStateGas := Eip803x.Generated.AuthorizationStateGasFold.foldTopFrameStateGas
abbrev callerVisible := Eip803x.Generated.AuthorizationStateGasFold.callerVisible

end Generated

namespace Spec

/-!
The reference side reuses the common production-shaped gas state and common
state-charge semantics. Authorization charging is described as a program of
primitive charge actions and interpreted generically; it is not a second copy
of the generated nested control flow.
-/

structure GasPolicyState where
  value : Nat
  stateReservoir : Int
  stateGasUsed : Int
  stateGasSpill : Int
  stateGasSpillRefunded : Int
  deriving DecidableEq, Repr

structure AuthorizationEffect where
  valid : Bool
  logicalAccountExists : Bool
  delegatedBefore : Bool
  clearsDelegation : Bool
  firstWrite : Bool
  firstDelegation : Bool
  newAccountStateCost : Int
  perAuthorizationStateCost : Int
  accountWriteExecutionCost : Nat
  deriving DecidableEq, Repr

structure ProcessInput where
  eip8037 : Bool
  available : GasPolicyState
  baseline : GasPolicyState
  authorizations : List AuthorizationEffect
  deriving DecidableEq, Repr

inductive ProcessOutcome where
  | completed (available : GasPolicyState) (baseline : GasPolicyState) (stateGasUsed : Int)
  | partialFailure (available : GasPolicyState) (baseline : GasPolicyState)
  deriving DecidableEq, Repr

inductive AuthorizationOutcome where
  | success (state : GasPolicyState)
  | failure (state : GasPolicyState)
  deriving DecidableEq, Repr

inductive ChargeAction where
  | state (amount : Int)
  | execution (amount : Nat)
  deriving DecidableEq, Repr

def toProductionState (state : GasPolicyState) : Eip803x.ProductionGasState :=
  { gasLeft := state.value
    stateReservoir := state.stateReservoir
    stateGasUsed := state.stateGasUsed
    stateGasSpill := state.stateGasSpill
    stateGasSpillRefunded := state.stateGasSpillRefunded }

def stateChargeSpill (available : GasPolicyState) (amount : Int) : Int :=
  if amount <= 0 then 0 else if available.stateReservoir <= 0 then amount else
    if amount > available.stateReservoir then amount - available.stateReservoir else 0

def chargeState (available : GasPolicyState) (amount : Int) : Option GasPolicyState :=
  if available.stateReservoir >= amount then
    some { available with
      stateReservoir := available.stateReservoir - amount
      stateGasUsed := available.stateGasUsed + amount }
  else
    let spill := stateChargeSpill available amount
    if available.value < Int.toNat spill then none else
      some { available with
        value := available.value - Int.toNat spill
        stateReservoir := min 0 available.stateReservoir
        stateGasUsed := available.stateGasUsed + amount
        stateGasSpill := available.stateGasSpill + spill }

def chargeExecution (available : GasPolicyState) (amount : Nat) : Option GasPolicyState :=
  if available.value < amount then none
  else some { available with value := available.value - amount }

def runCharge : ChargeAction → GasPolicyState → AuthorizationOutcome
  | .state amount, available =>
    match chargeState available amount with
    | some after => .success after
    | none => .failure available
  | .execution amount, available =>
    match chargeExecution available amount with
    | some after => .success after
    | none => .failure { available with value := 0 }

def runCharges : List ChargeAction → GasPolicyState → AuthorizationOutcome
  | [], available => .success available
  | action :: rest, available =>
    match runCharge action available with
    | .failure after => .failure after
    | .success after => runCharges rest after

def authorizationProgram (effect : AuthorizationEffect) : List ChargeAction :=
  if !effect.valid then [] else
  let delegationCharges :=
    if !effect.clearsDelegation && !effect.delegatedBefore && effect.firstDelegation then
      [.state effect.perAuthorizationStateCost]
    else []
  let writeAndDelegationCharges :=
    if effect.firstWrite then .execution effect.accountWriteExecutionCost :: delegationCharges
    else delegationCharges
  if effect.logicalAccountExists then writeAndDelegationCharges
  else .state effect.newAccountStateCost :: writeAndDelegationCharges

def applyAuthorization (effect : AuthorizationEffect) (available : GasPolicyState) : AuthorizationOutcome :=
  runCharges (authorizationProgram effect) available

def processLoop (eip8037 : Bool) (effects : List AuthorizationEffect)
    (available : GasPolicyState) (baseline : GasPolicyState) (beforeUsed : Int) : ProcessOutcome :=
  match effects with
  | [] => .completed available baseline (available.stateGasUsed - beforeUsed)
  | effect :: rest =>
    if !eip8037 then processLoop eip8037 rest available baseline beforeUsed else
    match applyAuthorization effect available with
    | .failure after => .partialFailure after baseline
    | .success after => processLoop eip8037 rest after baseline beforeUsed

def foldTopFrameStateGas (gas baseline : GasPolicyState) (stateGasUsed : Int) : GasPolicyState × GasPolicyState :=
  if stateGasUsed <= 0 then (gas, baseline)
  else
    ({ gas with stateGasSpill := 0, stateGasSpillRefunded := 0 },
      { baseline with
        stateReservoir := baseline.stateReservoir + stateGasUsed
        stateGasUsed := baseline.stateGasUsed + stateGasUsed })

def processDelegations (input : ProcessInput) : ProcessOutcome :=
  let beforeUsed := input.available.stateGasUsed
  match processLoop input.eip8037 input.authorizations input.available input.baseline beforeUsed with
  | .partialFailure available baseline => .partialFailure available baseline
  | .completed available baseline delta =>
    if !input.eip8037 then .completed available baseline delta else
      let folded := foldTopFrameStateGas available baseline delta
      .completed folded.1 folded.2 delta

def callerVisible (input : ProcessInput) (outcome : ProcessOutcome) : ProcessOutcome :=
  match outcome with
  | .completed available baseline delta => .completed available baseline delta
  | .partialFailure _ _ => .partialFailure input.available input.baseline

end Spec

def generatedStateToSpec (state : Generated.GasPolicyState) : Spec.GasPolicyState :=
  { value := state.value
    stateReservoir := state.stateReservoir
    stateGasUsed := state.stateGasUsed
    stateGasSpill := state.stateGasSpill
    stateGasSpillRefunded := state.stateGasSpillRefunded }

def generatedEffectToSpec (effect : Generated.AuthorizationEffect) : Spec.AuthorizationEffect :=
  { valid := effect.valid
    logicalAccountExists := effect.logicalAccountExists
    delegatedBefore := effect.delegatedBefore
    clearsDelegation := effect.clearsDelegation
    firstWrite := effect.firstWrite
    firstDelegation := effect.firstDelegation
    newAccountStateCost := effect.newAccountStateCost
    perAuthorizationStateCost := effect.perAuthorizationStateCost
    accountWriteExecutionCost := effect.accountWriteExecutionCost }

def generatedEffectsToSpec : List Generated.AuthorizationEffect → List Spec.AuthorizationEffect
  | [] => []
  | effect :: rest => generatedEffectToSpec effect :: generatedEffectsToSpec rest

def generatedInputToSpec (input : Generated.ProcessInput) : Spec.ProcessInput :=
  { eip8037 := input.eip8037
    available := generatedStateToSpec input.available
    baseline := generatedStateToSpec input.baseline
    authorizations := generatedEffectsToSpec input.authorizations }

def generatedOutcomeToSpec : Generated.ProcessOutcome → Spec.ProcessOutcome
  | .completed available baseline delta =>
    .completed (generatedStateToSpec available) (generatedStateToSpec baseline) delta
  | .partialFailure available baseline =>
    .partialFailure (generatedStateToSpec available) (generatedStateToSpec baseline)

def generatedOptionToSpec (result : Option Generated.GasPolicyState) : Option Spec.GasPolicyState :=
  result.map generatedStateToSpec

def generatedAuthorizationToSpec : Generated.AuthorizationOutcome → Spec.AuthorizationOutcome
  | .success state => .success (generatedStateToSpec state)
  | .failure state => .failure (generatedStateToSpec state)

def productionResultToSpecOption (result : Eip803x.ProductionStateGasResult) :
    Option Spec.GasPolicyState :=
  match result.outcome with
  | .success =>
    some
      { value := result.gasLeft
        stateReservoir := result.stateReservoir
        stateGasUsed := result.stateGasUsed
        stateGasSpill := result.stateGasSpill
        stateGasSpillRefunded := result.stateGasSpillRefunded }
  | .outOfGas => none

def runGeneratedCharge (action : Spec.ChargeAction) (available : Generated.GasPolicyState) :
    Generated.AuthorizationOutcome :=
  match action with
  | .state amount =>
    match Generated.chargeState available amount with
    | none => .failure available
    | some after => .success after
  | .execution amount =>
    match Generated.chargeExecution available amount with
    | none => .failure { available with value := 0 }
    | some after => .success after

def runGeneratedCharges : List Spec.ChargeAction → Generated.GasPolicyState →
    Generated.AuthorizationOutcome
  | [], available => .success available
  | action :: rest, available =>
    match runGeneratedCharge action available with
    | .failure after => .failure after
    | .success after => runGeneratedCharges rest after

/-!
The generated normalized model and reference interpreter use unbounded
`Nat`/`Int`. The equalities below are arithmetic-semantic claims, not CLR
fixed-width theorems. `ProcessNoWrap` records only entry ranges and constant
ranges; every reached charge and final fold still needs its own
`ChargeNoWrap` or `FoldNoOverflow` premise before composition with C#.
-/

abbrev FitsUInt64 := Eip803x.ProductionGas.FitsUInt64
abbrev FitsInt64 := Eip803x.ProductionGas.FitsInt64

def MachineBounded (state : Spec.GasPolicyState) : Prop :=
  FitsUInt64 state.value ∧
  FitsInt64 state.stateReservoir ∧
  FitsInt64 state.stateGasUsed ∧
  FitsInt64 state.stateGasSpill ∧
  FitsInt64 state.stateGasSpillRefunded

def NonnegativeStateCost (cost : Int) : Prop := 0 ≤ cost

def FoldNoOverflow (baseline : Spec.GasPolicyState) (delta : Int) : Prop :=
  0 ≤ delta ∧
  FitsInt64 (baseline.stateReservoir + delta) ∧
  FitsInt64 (baseline.stateGasUsed + delta)

def DeltaNoOverflow (beforeUsed afterUsed : Int) : Prop :=
  FitsInt64 beforeUsed ∧ FitsInt64 afterUsed ∧ FitsInt64 (afterUsed - beforeUsed)

def ChargeNoWrap (state : Spec.GasPolicyState) (amount : Int) : Prop :=
  NonnegativeStateCost amount ∧
  Eip803x.ProductionGas.ChargeNoOverflow (Int.toNat amount) (Spec.toProductionState state)

def ProcessNoWrap (input : Spec.ProcessInput) : Prop :=
  MachineBounded input.available ∧ MachineBounded input.baseline ∧
  (∀ effect ∈ input.authorizations,
    NonnegativeStateCost effect.newAccountStateCost ∧
    FitsInt64 effect.newAccountStateCost ∧
    NonnegativeStateCost effect.perAuthorizationStateCost ∧
    FitsInt64 effect.perAuthorizationStateCost ∧
    FitsUInt64 effect.accountWriteExecutionCost)

def accountWriteFailureState : Generated.GasPolicyState :=
  { value := 0, stateReservoir := 0, stateGasUsed := 1,
    stateGasSpill := 0, stateGasSpillRefunded := 0 }

def accountWriteFailureEffect : Generated.AuthorizationEffect :=
  { valid := true, logicalAccountExists := false, delegatedBefore := false,
    clearsDelegation := false, firstWrite := true, firstDelegation := true,
    newAccountStateCost := 1, perAuthorizationStateCost := 1,
    accountWriteExecutionCost := 3 }

def accountWriteFailureInput : Generated.GasPolicyState :=
  { value := 2, stateReservoir := 1, stateGasUsed := 0,
    stateGasSpill := 0, stateGasSpillRefunded := 0 }

def perAuthorizationFailureState : Generated.GasPolicyState :=
  { value := 0, stateReservoir := 0, stateGasUsed := 1,
    stateGasSpill := 0, stateGasSpillRefunded := 0 }

def perAuthorizationFailureEffect : Generated.AuthorizationEffect :=
  { valid := true, logicalAccountExists := false, delegatedBefore := false,
    clearsDelegation := false, firstWrite := true, firstDelegation := true,
    newAccountStateCost := 1, perAuthorizationStateCost := 1,
    accountWriteExecutionCost := 1 }

def perAuthorizationFailureInput : Generated.GasPolicyState :=
  { value := 1, stateReservoir := 1, stateGasUsed := 0,
    stateGasSpill := 0, stateGasSpillRefunded := 0 }

def prefixSuccessEffect : Generated.AuthorizationEffect :=
  { valid := true, logicalAccountExists := false, delegatedBefore := true,
    clearsDelegation := true, firstWrite := false, firstDelegation := false,
    newAccountStateCost := 1, perAuthorizationStateCost := 1,
    accountWriteExecutionCost := 0 }

def prefixFailureState : Generated.GasPolicyState :=
  { value := 0, stateReservoir := 0, stateGasUsed := 2,
    stateGasSpill := 1, stateGasSpillRefunded := 0 }

example : Generated.applyAuthorization accountWriteFailureEffect accountWriteFailureInput =
    .failure accountWriteFailureState := by decide

example : Generated.applyAuthorization perAuthorizationFailureEffect perAuthorizationFailureInput =
    .failure perAuthorizationFailureState := by decide

example : Generated.processLoop true [prefixSuccessEffect, accountWriteFailureEffect]
    accountWriteFailureInput accountWriteFailureInput 0 =
    .partialFailure prefixFailureState accountWriteFailureInput := by decide

theorem generated_stateChargeSpill_refines_spec
    (state : Generated.GasPolicyState) (amount : Int) :
    Generated.stateChargeSpill state amount =
      Eip803x.ProductionGas.calculateStateGasSpill
        (Spec.toProductionState (generatedStateToSpec state)) amount := by
  cases state
  simp [Eip803x.Generated.AuthorizationStateGasFold.stateChargeSpill,
    Eip803x.ProductionGas.calculateStateGasSpill, Spec.toProductionState,
    generatedStateToSpec]

theorem spec_chargeState_is_common_production_semantics
    (state : Spec.GasPolicyState) (amount : Int) :
    productionResultToSpecOption
        (Eip803x.ProductionGas.tryConsumeStateGas amount (Spec.toProductionState state)) =
      Spec.chargeState state amount := by
  cases state with
  | mk value reservoir used spill refunded =>
    by_cases hReservoir : reservoir ≥ amount
    · simp [productionResultToSpecOption, Eip803x.ProductionGas.tryConsumeStateGas,
        Eip803x.ProductionGas.resultOf, Spec.toProductionState, Spec.chargeState,
        hReservoir]
    · have hLess : reservoir < amount := by omega
      let spillAmount : Int :=
        if amount ≤ 0 then 0 else if reservoir ≤ 0 then amount else amount - reservoir
      by_cases hAffordable : Int.toNat spillAmount ≤ value
      · simp [productionResultToSpecOption, Eip803x.ProductionGas.tryConsumeStateGas,
          Eip803x.ProductionGas.resultOf, Eip803x.ProductionGas.calculateStateGasSpill,
          Spec.toProductionState, Spec.chargeState, Spec.stateChargeSpill, hReservoir,
          hLess, spillAmount, hAffordable, Nat.not_lt.mpr hAffordable]
      · have hTooSmall : value < Int.toNat spillAmount := Nat.lt_of_not_ge hAffordable
        simp [productionResultToSpecOption, Eip803x.ProductionGas.tryConsumeStateGas,
          Eip803x.ProductionGas.resultOf, Eip803x.ProductionGas.calculateStateGasSpill,
          Spec.toProductionState, Spec.chargeState, Spec.stateChargeSpill, hReservoir,
          hLess, spillAmount, hAffordable, hTooSmall]

theorem generated_charge_refines_spec
    (state : Generated.GasPolicyState) (amount : Int) :
    generatedOptionToSpec (Generated.chargeState state amount) =
      Spec.chargeState (generatedStateToSpec state) amount := by
  cases state
  simp only [Eip803x.Generated.AuthorizationStateGasFold.chargeState,
    Eip803x.Generated.AuthorizationStateGasFold.stateChargeSpill, Spec.chargeState,
    Spec.stateChargeSpill, generatedOptionToSpec, generatedStateToSpec]
  repeat' first | split | rfl
  all_goals simp_all [generatedStateToSpec] <;> omega

theorem generated_execution_charge_refines_spec
    (state : Generated.GasPolicyState) (amount : Nat) :
    generatedOptionToSpec (Generated.chargeExecution state amount) =
      Spec.chargeExecution (generatedStateToSpec state) amount := by
  by_cases h : state.value < amount <;>
    simp [Eip803x.Generated.AuthorizationStateGasFold.chargeExecution,
      Spec.chargeExecution, generatedOptionToSpec, generatedStateToSpec, h]

theorem generated_state_action_refines_spec
    (state : Generated.GasPolicyState) (amount : Int) :
    generatedAuthorizationToSpec
      (match Generated.chargeState state amount with
      | none => .failure state
      | some after => .success after) =
      Spec.runCharge (.state amount) (generatedStateToSpec state) := by
  simp only [Spec.runCharge, generatedStateToSpec]
  have hCharge := generated_charge_refines_spec state amount
  simp only [generatedStateToSpec] at hCharge
  rw [← hCharge]
  cases Generated.chargeState state amount <;>
    rfl

theorem generated_execution_action_refines_spec
    (state : Generated.GasPolicyState) (amount : Nat) :
    generatedAuthorizationToSpec
      (match Generated.chargeExecution state amount with
      | none => .failure { state with value := 0 }
      | some after => .success after) =
      Spec.runCharge (.execution amount) (generatedStateToSpec state) := by
  simp only [Spec.runCharge, generatedStateToSpec]
  have hCharge := generated_execution_charge_refines_spec state amount
  simp only [generatedStateToSpec] at hCharge
  rw [← hCharge]
  cases Generated.chargeExecution state amount <;>
    rfl

theorem generated_action_refines_spec
    (action : Spec.ChargeAction) (state : Generated.GasPolicyState) :
    generatedAuthorizationToSpec (runGeneratedCharge action state) =
      Spec.runCharge action (generatedStateToSpec state) := by
  cases action with
  | state amount =>
    simp only [runGeneratedCharge]
    exact generated_state_action_refines_spec state amount
  | execution amount =>
    simp only [runGeneratedCharge]
    exact generated_execution_action_refines_spec state amount

theorem generated_actions_refine_spec
    (actions : List Spec.ChargeAction) (state : Generated.GasPolicyState) :
    generatedAuthorizationToSpec (runGeneratedCharges actions state) =
      Spec.runCharges actions (generatedStateToSpec state) := by
  induction actions generalizing state with
  | nil => rfl
  | cons action rest ih =>
    have hAction := generated_action_refines_spec action state
    cases hGenerated : runGeneratedCharge action state with
    | failure after =>
      simp only [hGenerated, generatedAuthorizationToSpec] at hAction
      simp only [runGeneratedCharges, Spec.runCharges, hGenerated, generatedAuthorizationToSpec]
      rw [← hAction]
    | success after =>
      simp only [hGenerated, generatedAuthorizationToSpec] at hAction
      simp only [runGeneratedCharges, Spec.runCharges, hGenerated]
      rw [← hAction]
      exact ih after

theorem generated_authorization_uses_program
    (effect : Generated.AuthorizationEffect) (state : Generated.GasPolicyState) :
    Generated.applyAuthorization effect state =
      runGeneratedCharges (Spec.authorizationProgram (generatedEffectToSpec effect)) state := by
  cases effect with
  | mk valid logicalAccountExists delegatedBefore clearsDelegation firstWrite firstDelegation
      newAccountStateCost perAuthorizationStateCost accountWriteExecutionCost =>
    cases valid <;> cases logicalAccountExists <;> cases delegatedBefore <;>
      cases clearsDelegation <;> cases firstWrite <;> cases firstDelegation <;>
      simp [Eip803x.Generated.AuthorizationStateGasFold.applyAuthorization,
        runGeneratedCharges, runGeneratedCharge, Spec.authorizationProgram,
        generatedEffectToSpec] <;>
      repeat' split <;>
      simp_all

theorem generated_authorization_refines_spec
    (effect : Generated.AuthorizationEffect) (state : Generated.GasPolicyState) :
    generatedAuthorizationToSpec (Generated.applyAuthorization effect state) =
      Spec.applyAuthorization (generatedEffectToSpec effect) (generatedStateToSpec state) := by
  rw [generated_authorization_uses_program]
  exact generated_actions_refine_spec _ _

theorem generated_fold_refines_spec
    (gas baseline : Generated.GasPolicyState) (delta : Int) :
    (generatedStateToSpec (Generated.foldTopFrameStateGas gas baseline delta).1,
      generatedStateToSpec (Generated.foldTopFrameStateGas gas baseline delta).2) =
      Spec.foldTopFrameStateGas (generatedStateToSpec gas) (generatedStateToSpec baseline) delta := by
  cases gas
  cases baseline
  by_cases h : delta ≤ 0 <;>
    simp [Eip803x.Generated.AuthorizationStateGasFold.foldTopFrameStateGas,
      Spec.foldTopFrameStateGas, generatedStateToSpec, h]

theorem generated_processLoop_refines_spec
    (eip8037 : Bool) (effects : List Generated.AuthorizationEffect)
    (available baseline : Generated.GasPolicyState) (beforeUsed : Int) :
    generatedOutcomeToSpec (Generated.processLoop eip8037 effects available baseline beforeUsed) =
      Spec.processLoop eip8037 (generatedEffectsToSpec effects)
        (generatedStateToSpec available) (generatedStateToSpec baseline) beforeUsed := by
  induction effects generalizing available baseline with
  | nil => rfl
  | cons effect rest ih =>
    cases eip8037 with
    | false =>
      change generatedOutcomeToSpec
          (Eip803x.Generated.AuthorizationStateGasFold.processLoop false rest available baseline beforeUsed) =
        Spec.processLoop false (generatedEffectsToSpec rest) (generatedStateToSpec available)
          (generatedStateToSpec baseline) beforeUsed
      exact ih available baseline
    | true =>
      change generatedOutcomeToSpec
          (match Eip803x.Generated.AuthorizationStateGasFold.applyAuthorization effect available with
          | .failure after => .partialFailure after baseline
          | .success after =>
            Eip803x.Generated.AuthorizationStateGasFold.processLoop true rest after baseline beforeUsed) =
        match Spec.applyAuthorization (generatedEffectToSpec effect) (generatedStateToSpec available) with
        | .failure after => .partialFailure after (generatedStateToSpec baseline)
        | .success after =>
          Spec.processLoop true (generatedEffectsToSpec rest) after (generatedStateToSpec baseline) beforeUsed
      rw [← generated_authorization_refines_spec effect available]
      cases hAuth : Eip803x.Generated.AuthorizationStateGasFold.applyAuthorization effect available with
      | failure after =>
        simp only [hAuth, generatedOutcomeToSpec, generatedAuthorizationToSpec]
      | success after =>
        simpa only [hAuth, generatedOutcomeToSpec, generatedAuthorizationToSpec] using ih after baseline

theorem generated_process_refines_spec (input : Generated.ProcessInput) :
    generatedOutcomeToSpec (Generated.processDelegations input) =
      Spec.processDelegations (generatedInputToSpec input) := by
  cases input with
  | mk eip8037 availableInput baselineInput authorizations =>
    simp only [Eip803x.Generated.AuthorizationStateGasFold.processDelegations,
      Spec.processDelegations, generatedInputToSpec]
    cases hResult : Eip803x.Generated.AuthorizationStateGasFold.processLoop eip8037
        authorizations availableInput baselineInput availableInput.stateGasUsed with
    | partialFailure available baseline =>
        have hLoop := generated_processLoop_refines_spec eip8037 authorizations
          availableInput baselineInput availableInput.stateGasUsed
        simp only [hResult, generatedOutcomeToSpec, generatedStateToSpec] at hLoop ⊢
        rw [← hLoop]
    | completed available baseline delta =>
        have hLoop := generated_processLoop_refines_spec eip8037 authorizations
          availableInput baselineInput availableInput.stateGasUsed
        simp only [hResult, generatedOutcomeToSpec, generatedStateToSpec] at hLoop ⊢
        rw [← hLoop]
        cases h8037 : eip8037 with
        | false =>
          simp only [Bool.not_false, ↓reduceIte]
        | true =>
          simp only [Bool.not_true]
          have hFold := generated_fold_refines_spec available baseline delta
          change Spec.ProcessOutcome.completed
              (generatedStateToSpec
                (Eip803x.Generated.AuthorizationStateGasFold.foldTopFrameStateGas
                  available baseline delta).1)
              (generatedStateToSpec
                (Eip803x.Generated.AuthorizationStateGasFold.foldTopFrameStateGas
                  available baseline delta).2)
              delta =
            Spec.ProcessOutcome.completed
              (Spec.foldTopFrameStateGas (generatedStateToSpec available)
                (generatedStateToSpec baseline) delta).1
              (Spec.foldTopFrameStateGas (generatedStateToSpec available)
                (generatedStateToSpec baseline) delta).2
              delta
          exact congrArg
            (fun folded : Spec.GasPolicyState × Spec.GasPolicyState =>
              Spec.ProcessOutcome.completed folded.1 folded.2 delta) hFold

theorem partial_failure_preserves_caller_boundary (input : Spec.ProcessInput)
    (available baseline : Spec.GasPolicyState) :
    Spec.callerVisible input (.partialFailure available baseline) =
      .partialFailure input.available input.baseline := by
  rfl

theorem generated_caller_reset_refines_spec (input : Generated.ProcessInput)
    (outcome : Generated.ProcessOutcome) :
    generatedOutcomeToSpec (Generated.callerVisible input outcome) =
      Spec.callerVisible (generatedInputToSpec input) (generatedOutcomeToSpec outcome) := by
  cases outcome <;> rfl

/-! Exact accepted leaf-kernel dependencies. These wrappers expose the
imported theorem statements so composition cannot silently replace a pinned
kernel by a same-named or boolean-only assumption. -/

theorem initialization_dependency_is_zero_state
    {gasLimit intrinsicExecutionGas executionGasLimitCap : Nat}
    (h : Eip803x.Refinement.TransactionGasInitialization.RefinementValid
      gasLimit intrinsicExecutionGas 0 executionGasLimitCap) :
    let result := Eip803x.Generated.TransactionGasInitializationKernel.tryCreate
      gasLimit intrinsicExecutionGas 0 true executionGasLimitCap
    let input := Eip803x.Refinement.TransactionGasInitialization.referenceInput
      gasLimit intrinsicExecutionGas 0 executionGasLimitCap
    let model := Eip803x.TransactionGas.initializeTransactionGas input
    input.Valid ∧ result.outcome = .success ∧ result.value = model.gasLeft ∧
      result.stateReservoir = (model.stateGasReservoir : Int) ∧ result.stateGasUsed = 0 ∧
      result.stateGasSpill = 0 ∧ result.stateGasSpillRefunded = 0 := by
  exact Eip803x.Refinement.TransactionGasInitialization.generatedTryCreate_zero_state_corollary h

theorem state_charge_dependency_is_exact
    {production : Eip803x.ProductionGasState} {model : Eip803x.GasState} (amount : Nat)
    (hRep : Eip803x.ProductionGas.Represents production model)
    (hWell : Eip803x.ProductionGas.WellFormed production)
    (hNoOverflow : Eip803x.ProductionGas.ChargeNoOverflow amount production) :
    let result := Eip803x.Refinement.StateGasCharge.generatedResultToProduction
      (Eip803x.Generated.StateGasChargeKernel.tryCharge
        production.gasLeft production.stateReservoir production.stateGasUsed
        production.stateGasSpill production.stateGasSpillRefunded (amount : Int))
    (result.outcome = .success →
      ∃ after, Eip803x.GasMachine.chargeState amount model = .ok after ∧
        Eip803x.ProductionGas.Represents
          (Eip803x.ProductionGas.resultState result) after) ∧
    (result.outcome = .outOfGas →
      Eip803x.GasMachine.chargeState amount model = .error .outOfGas ∧
        Eip803x.ProductionGas.resultState result = production) := by
  exact Eip803x.Refinement.StateGasCharge.generatedTryCharge_refines_chargeState amount hRep hWell hNoOverflow

theorem state_transition_dependency_is_exact
    {parent child : Eip803x.ProductionGasState}
    (hNoOverflow : Eip803x.Refinement.StateGasTransition.Spec.refundNoOverflow parent child) :
    Eip803x.Refinement.StateGasTransition.generatedResultToSpec
        (Eip803x.Generated.StateGasTransitionKernel.refund
          parent.gasLeft parent.stateReservoir parent.stateGasUsed parent.stateGasSpill
          parent.stateGasSpillRefunded child.gasLeft child.stateReservoir child.stateGasUsed
          child.stateGasSpill child.stateGasSpillRefunded) =
      Eip803x.Refinement.StateGasTransition.Spec.refund parent child := by
  exact Eip803x.Refinement.StateGasTransition.generated_refund_matches_spec hNoOverflow

end Eip803x.Refinement.AuthorizationStateGasFold
