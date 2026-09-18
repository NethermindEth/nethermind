-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Generated.StateGasChargeKernel
import Eip803x.Production

namespace Eip803x.Refinement.StateGasCharge

open Eip803x

private theorem wrapInt64_of_machine_fits {value : Int}
    (h : ProductionGas.FitsInt64 value) :
    Eip803x.Generated.StateGasChargeKernel.wrapInt64 value = value := by
  unfold Eip803x.Generated.StateGasChargeKernel.wrapInt64
  have hBounds :
      Eip803x.Generated.StateGasChargeKernel.int64Min ≤ value ∧
        value ≤ Eip803x.Generated.StateGasChargeKernel.int64Max := by
    simpa [ProductionGas.FitsInt64, ProductionGas.int64Min, ProductionGas.int64Max,
      Eip803x.Generated.StateGasChargeKernel.int64Min,
      Eip803x.Generated.StateGasChargeKernel.int64Max,
      Eip803x.Generated.StateGasChargeKernel.int64SignBit] using h
  simp [hBounds]

private theorem normalizeUInt64_of_machine_fits {value : Nat}
    (h : ProductionGas.FitsUInt64 value) :
    Eip803x.Generated.StateGasChargeKernel.normalizeUInt64 value = value := by
  unfold Eip803x.Generated.StateGasChargeKernel.normalizeUInt64
  have hBound : value ≤ Eip803x.Generated.StateGasChargeKernel.uint64Max := by
    simpa [ProductionGas.FitsUInt64, ProductionGas.uint64Max,
      Eip803x.Generated.StateGasChargeKernel.uint64Max,
      Eip803x.Generated.StateGasChargeKernel.uint64Modulus] using h
  simp [hBound]

private theorem wrapUInt64_of_nonnegative_machine_fits {value : Int}
    (hNonnegative : 0 ≤ value)
    (hUpper : value ≤ Eip803x.Generated.StateGasChargeKernel.uint64Max) :
    Eip803x.Generated.StateGasChargeKernel.wrapUInt64 value = Int.toNat value := by
  unfold Eip803x.Generated.StateGasChargeKernel.wrapUInt64
  have hBounds : 0 ≤ value ∧ value ≤ (Eip803x.Generated.StateGasChargeKernel.uint64Max : Int) :=
    ⟨hNonnegative, hUpper⟩
  simp [hBounds]

private theorem wrapUInt64_of_nat_machine_fits {value : Nat}
    (h : ProductionGas.FitsUInt64 value) :
    Eip803x.Generated.StateGasChargeKernel.wrapUInt64 (value : Int) = value := by
  have hBounds :
      (0 : Int) ≤ value ∧
        (value : Int) ≤ Eip803x.Generated.StateGasChargeKernel.uint64Max := by
    unfold ProductionGas.FitsUInt64 ProductionGas.uint64Max at h
    unfold Eip803x.Generated.StateGasChargeKernel.uint64Max
      Eip803x.Generated.StateGasChargeKernel.uint64Modulus
    omega
  rw [wrapUInt64_of_nonnegative_machine_fits hBounds.1 hBounds.2]
  exact Int.toNat_natCast value

abbrev GeneratedOutcome :=
  Eip803x.Generated.StateGasChargeKernel.StateGasChargeOutcome

abbrev GeneratedResult :=
  Eip803x.Generated.StateGasChargeKernel.StateGasChargeResult

def generatedOutcomeToProduction : GeneratedOutcome → ProductionChargeOutcome
  | .success => .success
  | .outOfGas => .outOfGas

def generatedResultToProduction (result : GeneratedResult) : ProductionStateGasResult :=
  { outcome := generatedOutcomeToProduction result.outcome
    gasLeft := result.value
    stateReservoir := result.stateReservoir
    stateGasUsed := result.stateGasUsed
    stateGasSpill := result.stateGasSpill
    stateGasSpillRefunded := result.stateGasSpillRefunded }

private theorem addInt64_of_fits {left right : Int}
    (h : ProductionGas.FitsInt64 (left + right)) :
    Eip803x.Generated.StateGasChargeKernel.addInt64 left right = left + right := by
  unfold Eip803x.Generated.StateGasChargeKernel.addInt64
  rw [wrapInt64_of_machine_fits h]

private theorem subInt64_of_fits {left right : Int}
    (h : ProductionGas.FitsInt64 (left - right)) :
    Eip803x.Generated.StateGasChargeKernel.subInt64 left right = left - right := by
  unfold Eip803x.Generated.StateGasChargeKernel.subInt64
  rw [wrapInt64_of_machine_fits h]

private theorem int64ToUInt64_of_nonnegative_fits {value : Int}
    (hNonnegative : 0 ≤ value) (h : ProductionGas.FitsInt64 value) :
    Eip803x.Generated.StateGasChargeKernel.int64ToUInt64 value = Int.toNat value := by
  unfold Eip803x.Generated.StateGasChargeKernel.int64ToUInt64
  apply wrapUInt64_of_nonnegative_machine_fits hNonnegative
  unfold ProductionGas.FitsInt64 ProductionGas.int64Max at h
  unfold Eip803x.Generated.StateGasChargeKernel.uint64Max
    Eip803x.Generated.StateGasChargeKernel.uint64Modulus
  omega

private theorem subUInt64_of_fits {left right : Nat}
    (hRight : right ≤ left)
    (hResult : ProductionGas.FitsUInt64 (left - right)) :
    Eip803x.Generated.StateGasChargeKernel.subUInt64 left right = left - right := by
  unfold Eip803x.Generated.StateGasChargeKernel.subUInt64
  have hNonnegative : (0 : Int) ≤ (left : Int) - right := by omega
  have hUpper :
      (left : Int) - right ≤
        (Eip803x.Generated.StateGasChargeKernel.uint64Max : Int) := by
    unfold ProductionGas.FitsUInt64 ProductionGas.uint64Max at hResult
    unfold Eip803x.Generated.StateGasChargeKernel.uint64Max
      Eip803x.Generated.StateGasChargeKernel.uint64Modulus
    omega
  rw [wrapUInt64_of_nonnegative_machine_fits hNonnegative hUpper]
  apply Int.ofNat_inj.mp
  rw [Int.toNat_of_nonneg hNonnegative, Int.natCast_sub hRight]

private theorem uint64ToInt64_of_fits {value : Nat}
    (h : ProductionGas.FitsInt64 (value : Int)) :
    Eip803x.Generated.StateGasChargeKernel.uint64ToInt64 value = (value : Int) := by
  unfold Eip803x.Generated.StateGasChargeKernel.uint64ToInt64
  rw [wrapInt64_of_machine_fits h]

theorem calculateSpill_matches_production
    {production : ProductionGasState} {amount : Nat}
    (hMachine : ProductionGas.MachineBounded production)
    (hCost : ProductionGas.CostMachineBounded amount) :
    Eip803x.Generated.StateGasChargeKernel.calculateSpill
        production.stateReservoir (amount : Int) =
      Int.toNat (ProductionGas.calculateStateGasSpill production (amount : Int)) := by
  rcases hMachine with ⟨hGas, hReservoir, hUsed, hSpill, hRefunded⟩
  rcases hCost with ⟨hAmountNat, hAmountInt⟩
  have hReservoirWrap := wrapInt64_of_machine_fits hReservoir
  have hAmountWrap := wrapInt64_of_machine_fits hAmountInt
  change
    (if Eip803x.Generated.StateGasChargeKernel.wrapInt64 (amount : Int) <= 0 then
        0
     else if Eip803x.Generated.StateGasChargeKernel.wrapInt64 production.stateReservoir <= 0 then
        Eip803x.Generated.StateGasChargeKernel.int64ToUInt64
          (Eip803x.Generated.StateGasChargeKernel.wrapInt64 (amount : Int))
     else if Eip803x.Generated.StateGasChargeKernel.wrapInt64 (amount : Int) >
         Eip803x.Generated.StateGasChargeKernel.wrapInt64 production.stateReservoir then
        Eip803x.Generated.StateGasChargeKernel.int64ToUInt64
          (Eip803x.Generated.StateGasChargeKernel.subInt64
            (Eip803x.Generated.StateGasChargeKernel.wrapInt64 (amount : Int))
            (Eip803x.Generated.StateGasChargeKernel.wrapInt64 production.stateReservoir))
     else 0) =
      Int.toNat (ProductionGas.calculateStateGasSpill production (amount : Int))
  rw [hAmountWrap, hReservoirWrap]
  by_cases hAmountZero : amount = 0
  · subst amount
    simp [ProductionGas.calculateStateGasSpill]
  have hAmountPositive : 0 < (amount : Int) := by omega
  by_cases hReservoirNonpositive : production.stateReservoir ≤ 0
  · have hAmountToNat : Int.toNat (amount : Int) = amount := Int.toNat_natCast amount
    have hAmountToUInt :
        Eip803x.Generated.StateGasChargeKernel.int64ToUInt64 (amount : Int) = amount := by
      rw [int64ToUInt64_of_nonnegative_fits (Int.le_of_lt hAmountPositive) hAmountInt]
      exact Int.toNat_natCast amount
    simp [ProductionGas.calculateStateGasSpill,
      hReservoirNonpositive, hAmountZero, hAmountToNat, hAmountToUInt]
  by_cases hCovers : (amount : Int) ≤ production.stateReservoir
  · have hNotGreater : ¬ (amount : Int) > production.stateReservoir := by omega
    simp [ProductionGas.calculateStateGasSpill,
      hReservoirNonpositive, hNotGreater]
  · have hGreater : (amount : Int) > production.stateReservoir := by omega
    have hDifferenceNonnegative : 0 ≤ (amount : Int) - production.stateReservoir := by omega
    have hDifferenceFits :
        ProductionGas.FitsInt64 ((amount : Int) - production.stateReservoir) := by
      unfold ProductionGas.FitsInt64 ProductionGas.int64Min ProductionGas.int64Max at hAmountInt ⊢
      omega
    have hDifferenceSub := subInt64_of_fits hDifferenceFits
    have hDifferenceUInt := int64ToUInt64_of_nonnegative_fits
      hDifferenceNonnegative hDifferenceFits
    simp [ProductionGas.calculateStateGasSpill,
      hReservoirNonpositive, hAmountZero, hGreater, hDifferenceSub, hDifferenceUInt]

theorem tryCharge_matches_production
    {production : ProductionGasState} {amount : Nat}
    (hNoOverflow : ProductionGas.ChargeNoOverflow amount production) :
    generatedResultToProduction
        (Eip803x.Generated.StateGasChargeKernel.tryCharge
          production.gasLeft production.stateReservoir production.stateGasUsed
          production.stateGasSpill production.stateGasSpillRefunded (amount : Int)) =
      ProductionGas.tryConsumeStateGasNat amount production := by
  rcases hNoOverflow with ⟨hMachine, hCost, hSpillSafety, hResultBounded⟩
  rcases hMachine with ⟨hGas, hReservoir, hUsed, hSpill, hRefunded⟩
  have hReservoirWrap := wrapInt64_of_machine_fits hReservoir
  have hUsedWrap := wrapInt64_of_machine_fits hUsed
  have hSpillWrap := wrapInt64_of_machine_fits hSpill
  have hRefundedWrap := wrapInt64_of_machine_fits hRefunded
  have hAmountWrap := wrapInt64_of_machine_fits hCost.2
  have hSpillMatches := calculateSpill_matches_production
    ⟨hGas, hReservoir, hUsed, hSpill, hRefunded⟩ hCost
  have hGasNormalize := normalizeUInt64_of_machine_fits hGas
  unfold Eip803x.Generated.StateGasChargeKernel.tryCharge
  rw [hGasNormalize, hReservoirWrap, hUsedWrap, hSpillWrap, hRefundedWrap, hAmountWrap]
  change
    generatedResultToProduction
        (if production.stateReservoir ≥ (amount : Int) then
          { outcome := .success
            value := production.gasLeft
            stateReservoir :=
              Eip803x.Generated.StateGasChargeKernel.subInt64
                production.stateReservoir (amount : Int)
            stateGasUsed :=
              Eip803x.Generated.StateGasChargeKernel.addInt64
                production.stateGasUsed (amount : Int)
            stateGasSpill := production.stateGasSpill
            stateGasSpillRefunded := production.stateGasSpillRefunded }
        else
          let spillAmount : Nat :=
            if (amount : Int) <= 0 then
              0
            else if production.stateReservoir <= 0 then
              Eip803x.Generated.StateGasChargeKernel.int64ToUInt64 (amount : Int)
            else if (amount : Int) > production.stateReservoir then
              Eip803x.Generated.StateGasChargeKernel.int64ToUInt64
                (Eip803x.Generated.StateGasChargeKernel.subInt64
                  (amount : Int) production.stateReservoir)
            else
              0
          if production.gasLeft < spillAmount then
            { outcome := .outOfGas
              value := production.gasLeft
              stateReservoir := production.stateReservoir
              stateGasUsed := production.stateGasUsed
              stateGasSpill := production.stateGasSpill
              stateGasSpillRefunded := production.stateGasSpillRefunded }
          else
            { outcome := .success
              value :=
                Eip803x.Generated.StateGasChargeKernel.subUInt64
                  production.gasLeft spillAmount
              stateReservoir := min 0 production.stateReservoir
              stateGasUsed :=
                Eip803x.Generated.StateGasChargeKernel.addInt64
                  production.stateGasUsed (amount : Int)
              stateGasSpill :=
                Eip803x.Generated.StateGasChargeKernel.addInt64
                  production.stateGasSpill
                  (Eip803x.Generated.StateGasChargeKernel.uint64ToInt64 spillAmount)
              stateGasSpillRefunded := production.stateGasSpillRefunded }) =
      ProductionGas.tryConsumeStateGasNat amount production
  by_cases hReservoirCovers : production.stateReservoir ≥ (amount : Int)
  · have hBounded := hResultBounded
    simp [ProductionGas.tryConsumeStateGasNat, ProductionGas.tryConsumeStateGas,
      ProductionGas.resultOf, ProductionGas.resultState, hReservoirCovers] at hBounded
    rcases hBounded with ⟨hGasAfter, hReservoirAfter, hUsedAfter, hSpillAfter,
      hRefundedAfter⟩
    have hReservoirSub := subInt64_of_fits hReservoirAfter
    have hUsedAdd := addInt64_of_fits hUsedAfter
    simp [generatedResultToProduction, hReservoirCovers, hReservoirSub, hUsedAdd,
      generatedOutcomeToProduction,
      ProductionGas.tryConsumeStateGasNat, ProductionGas.tryConsumeStateGas,
      ProductionGas.resultOf]
  · have hEmittedSpill :
        (if (amount : Int) <= 0 then
            0
         else if production.stateReservoir <= 0 then
            Eip803x.Generated.StateGasChargeKernel.int64ToUInt64 (amount : Int)
         else if (amount : Int) > production.stateReservoir then
            Eip803x.Generated.StateGasChargeKernel.int64ToUInt64
              (Eip803x.Generated.StateGasChargeKernel.subInt64
                (amount : Int) production.stateReservoir)
         else
            0) =
          Int.toNat (ProductionGas.calculateStateGasSpill production (amount : Int)) := by
      change
        (if Eip803x.Generated.StateGasChargeKernel.wrapInt64 (amount : Int) <= 0 then
            0
         else if Eip803x.Generated.StateGasChargeKernel.wrapInt64 production.stateReservoir <= 0 then
            Eip803x.Generated.StateGasChargeKernel.int64ToUInt64
              (Eip803x.Generated.StateGasChargeKernel.wrapInt64 (amount : Int))
         else if Eip803x.Generated.StateGasChargeKernel.wrapInt64 (amount : Int) >
             Eip803x.Generated.StateGasChargeKernel.wrapInt64 production.stateReservoir then
            Eip803x.Generated.StateGasChargeKernel.int64ToUInt64
              (Eip803x.Generated.StateGasChargeKernel.subInt64
                (Eip803x.Generated.StateGasChargeKernel.wrapInt64 (amount : Int))
                (Eip803x.Generated.StateGasChargeKernel.wrapInt64 production.stateReservoir))
         else
            0) =
          Int.toNat (ProductionGas.calculateStateGasSpill production (amount : Int)) at hSpillMatches
      rw [hAmountWrap, hReservoirWrap] at hSpillMatches
      exact hSpillMatches
    rw [hEmittedSpill]
    by_cases hAffordable :
        Int.toNat (ProductionGas.calculateStateGasSpill production (amount : Int)) ≤
          production.gasLeft
    · have hBounded := hResultBounded
      simp [ProductionGas.tryConsumeStateGasNat, ProductionGas.tryConsumeStateGas,
        ProductionGas.resultOf, ProductionGas.resultState,
        hReservoirCovers, hAffordable] at hBounded
      rcases hBounded with ⟨hGasAfter, hReservoirAfter, hUsedAfter,
        hSpillAfter, hRefundedAfter⟩
      have hSpillNonnegative :=
        ProductionGas.calculateStateGasSpill_nonnegative_of_nat production amount
      have hSpillCast :
          (Int.toNat (ProductionGas.calculateStateGasSpill production (amount : Int)) : Int) =
            ProductionGas.calculateStateGasSpill production (amount : Int) :=
        Int.toNat_of_nonneg hSpillNonnegative
      have hGasNotLess :
          ¬ (production.gasLeft : Int) <
            ProductionGas.calculateStateGasSpill production (amount : Int) := by
        rw [← hSpillCast]
        omega
      have hSpillFits :
          ProductionGas.FitsInt64
            (Int.toNat (ProductionGas.calculateStateGasSpill production (amount : Int)) : Int) := by
        rw [hSpillCast]
        rcases hSpillSafety with hCover | hOog | hFits
        · omega
        · omega
        · exact hFits
      have hSpillToInt := uint64ToInt64_of_fits hSpillFits
      have hUsedAdd := addInt64_of_fits hUsedAfter
      have hSpillAdd := addInt64_of_fits hSpillAfter
      have hSubUInt := subUInt64_of_fits hAffordable hGasAfter
      simp [generatedResultToProduction, generatedOutcomeToProduction,
        ProductionGas.tryConsumeStateGasNat, ProductionGas.tryConsumeStateGas,
        ProductionGas.resultOf, hReservoirCovers, hAffordable, hSubUInt,
        hUsedAdd, hSpillToInt, hSpillAdd, hSpillCast, hGasNotLess]
    · have hSpillNonnegative :=
        ProductionGas.calculateStateGasSpill_nonnegative_of_nat production amount
      have hSpillCast :
          (Int.toNat (ProductionGas.calculateStateGasSpill production (amount : Int)) : Int) =
            ProductionGas.calculateStateGasSpill production (amount : Int) :=
        Int.toNat_of_nonneg hSpillNonnegative
      have hGasLtNat :
          production.gasLeft <
            Int.toNat (ProductionGas.calculateStateGasSpill production (amount : Int)) :=
        Nat.lt_of_not_ge hAffordable
      have hGasLt :
          (production.gasLeft : Int) <
            ProductionGas.calculateStateGasSpill production (amount : Int) := by
        rw [← hSpillCast]
        omega
      simp [generatedResultToProduction, generatedOutcomeToProduction,
        ProductionGas.tryConsumeStateGasNat, ProductionGas.tryConsumeStateGas,
        ProductionGas.resultOf, hReservoirCovers, hAffordable, hGasLtNat]

theorem generatedTryCharge_refines_chargeState
    {production : ProductionGasState} {model : GasState} (amount : Nat)
    (hRep : ProductionGas.Represents production model)
    (hWell : ProductionGas.WellFormed production)
    (hNoOverflow : ProductionGas.ChargeNoOverflow amount production) :
    let result :=
      generatedResultToProduction
        (Eip803x.Generated.StateGasChargeKernel.tryCharge
          production.gasLeft production.stateReservoir production.stateGasUsed
          production.stateGasSpill production.stateGasSpillRefunded (amount : Int))
    (result.outcome = .success →
      ∃ after, GasMachine.chargeState amount model = .ok after ∧
        ProductionGas.Represents (ProductionGas.resultState result) after) ∧
    (result.outcome = .outOfGas →
      GasMachine.chargeState amount model = .error .outOfGas ∧
        ProductionGas.resultState result = production) := by
  dsimp
  rw [tryCharge_matches_production hNoOverflow]
  exact ProductionGas.tryConsumeStateGasNat_refines_chargeState amount hRep hWell hNoOverflow

end Eip803x.Refinement.StateGasCharge
