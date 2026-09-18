-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Gas
import Lean.Elab.Tactic.Omega

namespace Eip803x

/-- The two outcomes returned by the extracted state-gas charge kernel. -/
inductive ProductionChargeOutcome where
  | success
  | outOfGas
  deriving DecidableEq, Repr

/--
The production-shaped state passed through `EthereumGasPolicy`.

`gasLeft` is the unsigned execution budget.  The four state-gas fields are
signed because production permits a negative reservoir after a child spill and
tracks spill/refund deltas in signed `long` fields.
-/
structure ProductionGasState where
  gasLeft : Nat
  stateReservoir : Int
  stateGasUsed : Int
  stateGasSpill : Int
  stateGasSpillRefunded : Int
  deriving DecidableEq, Repr

/--
The value result corresponding to the production `TryCharge` kernel.  The
state is present on both outcomes so that the failed-call nonmutation contract
is explicit rather than hidden in an exception type.
-/
structure ProductionStateGasResult where
  outcome : ProductionChargeOutcome
  gasLeft : Nat
  stateReservoir : Int
  stateGasUsed : Int
  stateGasSpill : Int
  stateGasSpillRefunded : Int
  deriving DecidableEq, Repr

abbrev TryConsumeStateGasResult := ProductionStateGasResult

namespace ProductionStateGasResult

def state (result : ProductionStateGasResult) : ProductionGasState :=
  { gasLeft := result.gasLeft
    stateReservoir := result.stateReservoir
    stateGasUsed := result.stateGasUsed
    stateGasSpill := result.stateGasSpill
    stateGasSpillRefunded := result.stateGasSpillRefunded }

theorem state_of_result (result : ProductionStateGasResult) :
    state result =
      { gasLeft := result.gasLeft
        stateReservoir := result.stateReservoir
        stateGasUsed := result.stateGasUsed
        stateGasSpill := result.stateGasSpill
        stateGasSpillRefunded := result.stateGasSpillRefunded } := rfl

end ProductionStateGasResult

namespace ProductionGas

/-- The signed machine limits used by the production `long` fields. -/
def int64Min : Int := -(2 ^ 63)
def int64Max : Int := 2 ^ 63 - 1
def uint64Max : Nat := 2 ^ 64 - 1

def FitsInt64 (value : Int) : Prop :=
  int64Min ≤ value ∧ value ≤ int64Max

def FitsUInt64 (value : Nat) : Prop :=
  value ≤ uint64Max

/--
The explicit machine-width obligation for a production state.  Lean's `Int`
and `Nat` are unbounded, so this predicate records the range that an extracted
CLR `long`/`ulong` state must satisfy.
-/
def MachineBounded (state : ProductionGasState) : Prop :=
  FitsUInt64 state.gasLeft ∧
  FitsInt64 state.stateReservoir ∧
  FitsInt64 state.stateGasUsed ∧
  FitsInt64 state.stateGasSpill ∧
  FitsInt64 state.stateGasSpillRefunded

def CostMachineBounded (amount : Nat) : Prop :=
  FitsUInt64 amount ∧ FitsInt64 (amount : Int)

/--
The state-gas accounting invariant needed by the leaf representation.  It is
deliberately separate from `MachineBounded`: machine width and accounting
well-formedness are distinct adapter obligations.
-/
def WellFormed (state : ProductionGasState) : Prop :=
  0 ≤ state.stateGasUsed ∧
  0 ≤ state.stateGasSpill ∧
  0 ≤ state.stateGasSpillRefunded ∧
  state.stateGasSpillRefunded ≤ state.stateGasSpill

/-- The net spill that has not been repaid to execution gas. -/
def netUnrefundedSpill (state : ProductionGasState) : Int :=
  max (state.stateGasSpill - state.stateGasSpillRefunded) 0

/--
The C# `CalculateStateGasSpill` arithmetic, before the `ulong` carrier cast.
The caller's state-cost path is normally nonnegative, but retaining the
`<= 0` branch keeps the transcription faithful for the complete signed kernel.
-/
def calculateStateGasSpill (state : ProductionGasState) (stateGasCost : Int) : Int :=
  if stateGasCost <= 0 then
    0
  else if state.stateReservoir <= 0 then
    stateGasCost
  else if stateGasCost > state.stateReservoir then
    stateGasCost - state.stateReservoir
  else
    0

def resultOf (outcome : ProductionChargeOutcome) (state : ProductionGasState) :
    ProductionStateGasResult :=
  { outcome
    gasLeft := state.gasLeft
    stateReservoir := state.stateReservoir
    stateGasUsed := state.stateGasUsed
    stateGasSpill := state.stateGasSpill
    stateGasSpillRefunded := state.stateGasSpillRefunded }

/--
Pure value transcription of `EthereumGasPolicy.TryConsumeStateGas`.

The reservoir is charged first.  If it cannot cover the cost, the spill is
charged from execution gas; only after that affordability check succeeds are
the reservoir, used, and spill fields updated.  Consequently the `outOfGas`
result carries the original five fields unchanged, including a negative
reservoir.
-/
def tryConsumeStateGas (stateGasCost : Int) (state : ProductionGasState) :
    ProductionStateGasResult :=
  let reservoir := state.stateReservoir
  if reservoir >= stateGasCost then
    resultOf .success
      { state with
        stateReservoir := reservoir - stateGasCost
        stateGasUsed := state.stateGasUsed + stateGasCost }
  else
    let spillAmount := calculateStateGasSpill state stateGasCost
    if Int.toNat spillAmount ≤ state.gasLeft then
      resultOf .success
        { state with
          gasLeft := state.gasLeft - Int.toNat spillAmount
          stateReservoir := min 0 reservoir
          stateGasUsed := state.stateGasUsed + stateGasCost
          stateGasSpill := state.stateGasSpill + spillAmount }
    else
      resultOf .outOfGas state

def tryConsumeStateGasNat (amount : Nat) (state : ProductionGasState) :
    ProductionStateGasResult :=
  tryConsumeStateGas (amount : Int) state

/-- The production five-field state represented by a flat result. -/
def resultState (result : ProductionStateGasResult) : ProductionGasState :=
  ProductionStateGasResult.state result

/--
The C# kernel performs unchecked `long` arithmetic for the used and spill
updates, and casts the nonnegative spill to `long` on the successful spill
path.  This predicate records the range obligation for the actual charge,
rather than assuming that bounded inputs alone make those updates safe.  The
spill cast is required only when that path is actually taken.
-/
def ChargeNoOverflow (amount : Nat) (state : ProductionGasState) : Prop :=
  MachineBounded state ∧
  CostMachineBounded amount ∧
  (state.stateReservoir ≥ (amount : Int) ∨
    state.gasLeft < Int.toNat (calculateStateGasSpill state (amount : Int)) ∨
    FitsInt64 (calculateStateGasSpill state (amount : Int))) ∧
  MachineBounded (resultState (tryConsumeStateGasNat amount state))

/--
Representation of the fields observed by `GasMachine.chargeState`.

The signed reservoir is represented by its nonnegative part.  Production spill
and spill repayment are represented by their net outstanding amount in
`stateFromGasLeft`.  `GasState.refundCounter` is intentionally unconstrained:
the production five-field leaf has no SSTORE refund-counter field, and this
charge preserves that orthogonal model field.
-/
def Represents (production : ProductionGasState) (model : GasState) : Prop :=
  model.gasLeft = production.gasLeft ∧
  (model.stateReservoir : Int) = max production.stateReservoir 0 ∧
  (model.stateFromGasLeft : Int) = netUnrefundedSpill production ∧
  (model.stateUsed : Int) = max production.stateGasUsed 0

theorem netUnrefundedSpill_eq (state : ProductionGasState) (h : WellFormed state) :
    netUnrefundedSpill state = state.stateGasSpill - state.stateGasSpillRefunded := by
  unfold netUnrefundedSpill
  rcases h with ⟨hUsed, hSpill, hRefunded, hOrder⟩
  apply Int.max_eq_left
  omega

theorem netUnrefundedSpill_add (state : ProductionGasState) (spill : Int)
    (hNet : 0 ≤ state.stateGasSpill - state.stateGasSpillRefunded)
    (hSpill : 0 ≤ spill) :
    netUnrefundedSpill
        { state with stateGasSpill := state.stateGasSpill + spill } =
      netUnrefundedSpill state + spill := by
  change max (state.stateGasSpill + spill - state.stateGasSpillRefunded) 0 =
    max (state.stateGasSpill - state.stateGasSpillRefunded) 0 + spill
  have hAfter :
      max (state.stateGasSpill + spill - state.stateGasSpillRefunded) 0 =
        state.stateGasSpill + spill - state.stateGasSpillRefunded := by
    apply Int.max_eq_left
    omega
  rw [hAfter, Int.max_eq_left hNet]
  omega

theorem represents_state_fields {production : ProductionGasState} {model : GasState}
    (h : Represents production model) :
    model.gasLeft = production.gasLeft ∧
    (model.stateReservoir : Int) = max production.stateReservoir 0 ∧
    (model.stateFromGasLeft : Int) = netUnrefundedSpill production ∧
    (model.stateUsed : Int) = max production.stateGasUsed 0 := h

theorem calculateStateGasSpill_nonnegative_of_nat
    (state : ProductionGasState) (amount : Nat) :
    0 ≤ calculateStateGasSpill state (amount : Int) := by
  by_cases hAmount : (amount : Int) ≤ 0
  · have hAmountZero : amount = 0 := by omega
    subst amount
    simp [calculateStateGasSpill]
  by_cases hReservoir : state.stateReservoir ≤ 0
  · have hAmountZero : amount ≠ 0 := by omega
    simp [calculateStateGasSpill, hReservoir, hAmountZero]
  by_cases hCost : (amount : Int) > state.stateReservoir
  · have hAmountZero : amount ≠ 0 := by omega
    simp [calculateStateGasSpill, hReservoir, hCost, hAmountZero]
    omega
  · have hAmountZero : amount ≠ 0 := by omega
    simp [calculateStateGasSpill, hReservoir, hCost, hAmountZero]

theorem calculateStateGasSpill_matches_charge
    {production : ProductionGasState} {model : GasState}
    (hRep : Represents production model) (amount : Nat) :
    Int.toNat (calculateStateGasSpill production (amount : Int)) =
      GasMachine.stateChargeFromGasLeft amount model := by
  rcases hRep with ⟨hGas, hReservoir, hFromGasLeft, hUsed⟩
  unfold GasMachine.stateChargeFromGasLeft GasMachine.stateChargeFromReservoir
  by_cases hAmountZero : amount = 0
  · subst amount
    simp [calculateStateGasSpill]
  have hAmountPos : (0 : Int) < amount := by omega
  by_cases hReservoirNonPos : production.stateReservoir ≤ 0
  · have hModelReservoir : model.stateReservoir = 0 := by
      have hMax : max production.stateReservoir 0 = 0 :=
        Int.max_eq_right hReservoirNonPos
      omega
    simp [calculateStateGasSpill, hReservoirNonPos,
      hAmountZero, hModelReservoir]
  have hReservoirPos : 0 < production.stateReservoir := by omega
  by_cases hReservoirCovers : (amount : Int) ≤ production.stateReservoir
  · have hModelCovers : amount ≤ model.stateReservoir := by
      omega
    have hNotSpill : ¬ production.stateReservoir < (amount : Int) := by omega
    simp [calculateStateGasSpill, hReservoirNonPos,
      hNotSpill, hAmountZero, Nat.min_eq_left hModelCovers]
  have hSpillPos : production.stateReservoir < (amount : Int) := by omega
  have hSpillNonneg : 0 ≤ (amount : Int) - production.stateReservoir := by omega
  have hSpillCast :
      (Int.toNat ((amount : Int) - production.stateReservoir) : Int) =
        (amount : Int) - production.stateReservoir :=
    Int.toNat_of_nonneg hSpillNonneg
  simp [calculateStateGasSpill, hReservoirNonPos,
      hSpillPos, hAmountZero]
  apply Int.ofNat_inj.mp
  rw [hSpillCast]
  omega

def chargedModel (amount : Nat) (model : GasState) : GasState :=
  let fromReservoir := GasMachine.stateChargeFromReservoir amount model
  let fromGasLeft := GasMachine.stateChargeFromGasLeft amount model
  { model with
    gasLeft := model.gasLeft - fromGasLeft
    stateReservoir := model.stateReservoir - fromReservoir
    stateFromGasLeft := model.stateFromGasLeft + fromGasLeft
    stateUsed := model.stateUsed + amount }

theorem chargeState_eq_ok_chargedModel
    {amount : Nat} {model : GasState}
    (h : GasMachine.stateChargeFromGasLeft amount model ≤ model.gasLeft) :
    GasMachine.chargeState amount model = .ok (chargedModel amount model) := by
  rw [GasMachine.chargeState_of_affordable h]
  rfl

theorem tryConsumeStateGas_failure_preserves_state
    {state : ProductionGasState} {stateGasCost : Int}
    (h : (tryConsumeStateGas stateGasCost state).outcome = .outOfGas) :
    resultState (tryConsumeStateGas stateGasCost state) = state := by
  by_cases hReservoir : state.stateReservoir ≥ stateGasCost
  · simp [tryConsumeStateGas, hReservoir] at h
    cases h
  · simp [tryConsumeStateGas, hReservoir] at h ⊢
    by_cases hAffordable :
        calculateStateGasSpill state stateGasCost ≤ (state.gasLeft : Int)
    · simp [hAffordable] at h
      cases h
    · simp [hAffordable, resultOf, resultState,
        ProductionStateGasResult.state]

theorem tryConsumeStateGasNat_refines_chargeState
    {production : ProductionGasState} {model : GasState} (amount : Nat)
    (hRep : Represents production model)
    (hWell : WellFormed production)
    (hNoOverflow : ChargeNoOverflow amount production) :
    let result := tryConsumeStateGasNat amount production
    (result.outcome = .success →
      ∃ after, GasMachine.chargeState amount model = .ok after ∧
        Represents (resultState result) after) ∧
    (result.outcome = .outOfGas →
      GasMachine.chargeState amount model = .error .outOfGas ∧
        resultState result = production) := by
  dsimp
  rcases hNoOverflow with ⟨hMachine, hCost, _hSpillFits, _hResultBounded⟩
  rcases hRep with ⟨hGas, hReservoir, hFromGasLeft, hUsed⟩
  rcases hWell with ⟨hUsedNonneg, hSpillNonneg, hRefundedNonneg, hRefundOrder⟩
  constructor
  · intro hSuccess
    by_cases hReservoirCovers : production.stateReservoir ≥ (amount : Int)
    · have hModelCovers : amount ≤ model.stateReservoir := by
        omega
      have hChargeAffordable :
          GasMachine.stateChargeFromGasLeft amount model ≤ model.gasLeft := by
        simp [GasMachine.stateChargeFromGasLeft,
          GasMachine.stateChargeFromReservoir,
          Nat.min_eq_left hModelCovers]
      have hModelReservoirEq : (model.stateReservoir : Int) = production.stateReservoir := by
        rw [hReservoir, Int.max_eq_left (by omega : 0 ≤ production.stateReservoir)]
      have hMaxReservoirAfter :
          max (production.stateReservoir - (amount : Int)) 0 =
            production.stateReservoir - amount := by
        apply Int.max_eq_left
        omega
      have hModelUsedEq : (model.stateUsed : Int) = production.stateGasUsed := by
        rw [hUsed, Int.max_eq_left hUsedNonneg]
      have hMaxUsedAfter :
          max (production.stateGasUsed + (amount : Int)) 0 =
            production.stateGasUsed + amount := by
        apply Int.max_eq_left
        omega
      have hNetAfter :
          netUnrefundedSpill
              { production with
                stateReservoir := production.stateReservoir - (amount : Int)
                stateGasUsed := production.stateGasUsed + amount } =
            netUnrefundedSpill production := rfl
      refine ⟨chargedModel amount model,
        chargeState_eq_ok_chargedModel hChargeAffordable, ?_⟩
      simp [tryConsumeStateGasNat, tryConsumeStateGas, hReservoirCovers,
        resultOf, resultState, Represents, chargedModel,
        ProductionStateGasResult.state,
        GasMachine.stateChargeFromReservoir, GasMachine.stateChargeFromGasLeft,
         hMaxReservoirAfter, hModelUsedEq, hMaxUsedAfter,
        hNetAfter, Nat.min_eq_left hModelCovers]
      constructor
      · omega
      constructor
      · omega
      · omega
    · have hSpill := calculateStateGasSpill_matches_charge
          (production := production) (model := model)
          ⟨hGas, hReservoir, hFromGasLeft, hUsed⟩ amount
      by_cases hAffordable :
          calculateStateGasSpill production (amount : Int) ≤ (production.gasLeft : Int)
      · have hChargeAffordable :
            GasMachine.stateChargeFromGasLeft amount model ≤ model.gasLeft := by
          rw [← hSpill]
          omega
        have hSpillNonnegative :
            0 ≤ calculateStateGasSpill production (amount : Int) :=
          calculateStateGasSpill_nonnegative_of_nat production amount
        have hSpillCast :
            (Int.toNat (calculateStateGasSpill production (amount : Int)) : Int) =
              calculateStateGasSpill production (amount : Int) :=
          Int.toNat_of_nonneg hSpillNonnegative
        have hAffordableNat :
            Int.toNat (calculateStateGasSpill production (amount : Int)) ≤
              production.gasLeft := by
          have hCast :
              (Int.toNat (calculateStateGasSpill production (amount : Int)) : Int) ≤
                (production.gasLeft : Int) := by
            rw [hSpillCast]
            exact hAffordable
          omega
        have hAffordableModel :
            amount - min amount model.stateReservoir ≤ production.gasLeft := by
          simpa [GasMachine.stateChargeFromGasLeft,
            GasMachine.stateChargeFromReservoir] using
            (show GasMachine.stateChargeFromGasLeft amount model ≤ production.gasLeft by
              rw [← hSpill]
              exact hAffordableNat)
        have hAffordableNormalized :
            amount ≤ production.gasLeft + min amount model.stateReservoir := by
          omega
        have hSpillNatExpr :
            Int.toNat (calculateStateGasSpill production (amount : Int)) =
              amount - min amount model.stateReservoir := by
          simpa [GasMachine.stateChargeFromGasLeft,
            GasMachine.stateChargeFromReservoir] using hSpill
        have hNet :
            0 ≤ production.stateGasSpill - production.stateGasSpillRefunded := by
          omega
        have hNetAfter :
            netUnrefundedSpill
                { production with
                    gasLeft := production.gasLeft -
                      Int.toNat (calculateStateGasSpill production (amount : Int))
                    stateReservoir := min 0 production.stateReservoir
                    stateGasUsed := production.stateGasUsed + amount
                    stateGasSpill := production.stateGasSpill +
                      calculateStateGasSpill production (amount : Int) } =
              netUnrefundedSpill production +
                calculateStateGasSpill production (amount : Int) := by
          change max
              (production.stateGasSpill + calculateStateGasSpill production (amount : Int) -
                production.stateGasSpillRefunded) 0 =
            max (production.stateGasSpill - production.stateGasSpillRefunded) 0 +
              calculateStateGasSpill production (amount : Int)
          have hAfter := Int.max_eq_left (show
              0 ≤ production.stateGasSpill + calculateStateGasSpill production (amount : Int) -
                production.stateGasSpillRefunded by omega)
          rw [hAfter, Int.max_eq_left hNet]
          omega
        have hSpillNatCast :
            ((amount - min amount model.stateReservoir : Nat) : Int) =
              calculateStateGasSpill production (amount : Int) := by
          rw [← hSpillNatExpr, hSpillCast]
        have hNetAfterNormalized :
            netUnrefundedSpill
                { production with
                    gasLeft := production.gasLeft -
                      (amount - min amount model.stateReservoir)
                    stateReservoir := min 0 production.stateReservoir
                    stateGasUsed := production.stateGasUsed + amount
                    stateGasSpill := production.stateGasSpill +
                      calculateStateGasSpill production (amount : Int) } =
              netUnrefundedSpill production +
                calculateStateGasSpill production (amount : Int) := by
          change max
              (production.stateGasSpill + calculateStateGasSpill production (amount : Int) -
                production.stateGasSpillRefunded) 0 =
            max (production.stateGasSpill - production.stateGasSpillRefunded) 0 +
              calculateStateGasSpill production (amount : Int)
          have hAfter := Int.max_eq_left (show
              0 ≤ production.stateGasSpill + calculateStateGasSpill production (amount : Int) -
                production.stateGasSpillRefunded by omega)
          rw [hAfter, Int.max_eq_left hNet]
          omega
        have hModelReservoirCharge :
            (model.stateReservoir -
                min amount model.stateReservoir : Int) =
              max (min 0 production.stateReservoir) 0 := by
          by_cases hReservoirNonPos : production.stateReservoir ≤ 0
          · have hModelReservoirZero : model.stateReservoir = 0 := by
              have hMax : max production.stateReservoir 0 = 0 :=
                Int.max_eq_right hReservoirNonPos
              omega
            simp [hModelReservoirZero, Int.min_eq_right hReservoirNonPos,
              Int.max_eq_right hReservoirNonPos]
          · have hReservoirPos : 0 < production.stateReservoir := by omega
            have hModelReservoirEq :
                (model.stateReservoir : Int) = production.stateReservoir := by
              rw [hReservoir, Int.max_eq_left (by omega : 0 ≤ production.stateReservoir)]
            have hModelLess : model.stateReservoir ≤ amount := by omega
            rw [Nat.min_eq_right hModelLess]
            simp [hModelReservoirEq, Int.min_eq_left (by omega : 0 ≤ production.stateReservoir)]
        have hModelUsedEq : (model.stateUsed : Int) = production.stateGasUsed := by
          rw [hUsed, Int.max_eq_left hUsedNonneg]
        have hMaxUsedAfter :
            max (production.stateGasUsed + (amount : Int)) 0 =
              production.stateGasUsed + amount := by
          apply Int.max_eq_left
          omega
        refine ⟨chargedModel amount model,
          chargeState_eq_ok_chargedModel hChargeAffordable, ?_⟩
        simp [tryConsumeStateGasNat, tryConsumeStateGas, hReservoirCovers,
           hAffordableNormalized, resultOf, resultState, Represents,
          ProductionStateGasResult.state,
          chargedModel, GasMachine.stateChargeFromReservoir,
           GasMachine.stateChargeFromGasLeft, hSpill,
           hGas, hModelUsedEq, hMaxUsedAfter]
        constructor
        · omega
        · rw [hNetAfterNormalized, hFromGasLeft, hSpillNatCast]
      · exfalso
        have hOut :
            (tryConsumeStateGasNat amount production).outcome = .outOfGas := by
          simp [tryConsumeStateGasNat, tryConsumeStateGas, hReservoirCovers,
            hAffordable, resultOf]
        have hContr : ProductionChargeOutcome.success = ProductionChargeOutcome.outOfGas :=
          hSuccess.symm.trans hOut
        cases hContr
  · intro hOutOfGas
    by_cases hReservoirCovers : production.stateReservoir ≥ (amount : Int)
    · have hSuccess :
          (tryConsumeStateGasNat amount production).outcome = .success := by
        simp [tryConsumeStateGasNat, tryConsumeStateGas, hReservoirCovers, resultOf]
      have hContr : ProductionChargeOutcome.success = ProductionChargeOutcome.outOfGas :=
        hSuccess.symm.trans hOutOfGas
      cases hContr
    · by_cases hAffordable :
          calculateStateGasSpill production (amount : Int) ≤ (production.gasLeft : Int)
      · have hSuccess :
            (tryConsumeStateGasNat amount production).outcome = .success := by
          simp [tryConsumeStateGasNat, tryConsumeStateGas, hReservoirCovers,
            hAffordable, resultOf]
        have hContr : ProductionChargeOutcome.success = ProductionChargeOutcome.outOfGas :=
          hSuccess.symm.trans hOutOfGas
        cases hContr
      · have hChargeOog :
            GasMachine.chargeState amount model = .error .outOfGas := by
          apply GasMachine.chargeState_of_insufficient
          have hSpill := calculateStateGasSpill_matches_charge
            (production := production) (model := model)
            ⟨hGas, hReservoir, hFromGasLeft, hUsed⟩ amount
          rw [← hSpill]
          omega
        exact ⟨hChargeOog,
          tryConsumeStateGas_failure_preserves_state hOutOfGas⟩

theorem tryConsumeStateGasNat_failure_preserves_state
    {state : ProductionGasState} {amount : Nat}
    (h : (tryConsumeStateGasNat amount state).outcome = .outOfGas) :
    resultState (tryConsumeStateGasNat amount state) = state := by
  exact tryConsumeStateGas_failure_preserves_state h

end ProductionGas
end Eip803x
