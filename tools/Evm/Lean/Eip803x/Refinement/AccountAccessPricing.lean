-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.AccountPricing
import Eip803x.Generated.AccountAccessPricingKernel

namespace Eip803x.Refinement.AccountAccessPricing

open Eip803x

namespace Generated

abbrev Decision := Eip803x.Generated.AccountAccessPricingKernel.Decision
abbrev AccessKind := Eip803x.Generated.AccountAccessPricingKernel.AccessKind
abbrev Result := Eip803x.Generated.AccountAccessPricingKernel.Result
abbrev uint64Max := Eip803x.Generated.AccountAccessPricingKernel.uint64Max
abbrev price := Eip803x.Generated.AccountAccessPricingKernel.price
abbrev priceNormalized := Eip803x.Generated.AccountAccessPricingKernel.priceNormalized
abbrev normalizeUInt64 := Eip803x.Generated.AccountAccessPricingKernel.normalizeUInt64

end Generated

open Eip803x.AccountPricing

/-!
  The generated kernel is refined only at the value-policy boundary.  The main theorem below
  covers the non-precompile path after the adapter's existing WarmUp/precompile short-circuit;
  explicit precompile theorems cover the warm-price branch.  The hypotheses state the schedule
  representation and fixed-width obligations explicitly; no tracker, gas mutation, tracing, or
  opcode reachability claim is made here.
-/

def accessBool : AccessStatus → Bool
  | .cold => true
  | .warm => false

def chargeOf (result : Generated.Result) : Nat :=
  match result.decision with
  | .charge => result.amount
  | .noCharge => 0

def FitsUInt64 (value : Nat) : Prop :=
  value ≤ Generated.uint64Max

def AccessScheduleRepresents
    (schedule : AccountGasSchedule) (coldGas warmGas : Nat) : Prop :=
  coldGas = schedule.base.coldAccountAccess ∧
    warmGas = schedule.base.warmAccess

theorem eip8038_access_decision_refines_accountAccessCharge
    (schedule : AccountGasSchedule)
    (access : AccessStatus)
    (kind : Generated.AccessKind)
    (coldGas warmGas : Nat)
    (hSchedule : AccessScheduleRepresents schedule coldGas warmGas)
    (hCold : FitsUInt64 coldGas)
    (hWarm : FitsUInt64 warmGas) :
    let result := Generated.price true true (accessBool access) false kind coldGas warmGas
    result.decision = .charge ∧
      result.amount = accountAccessCharge schedule access := by
  rcases hSchedule with ⟨rfl, rfl⟩
  unfold FitsUInt64 at hCold hWarm
  cases access <;>
    simp [Eip803x.Generated.AccountAccessPricingKernel.price,
      Eip803x.Generated.AccountAccessPricingKernel.priceNormalized, accessBool,
      Eip803x.Generated.AccountAccessPricingKernel.normalizeUInt64,
      accountAccessCharge, hCold, hWarm]

theorem eip8038_access_charge_refines_accountAccessCharge
    (schedule : AccountGasSchedule)
    (access : AccessStatus)
    (kind : Generated.AccessKind)
    (coldGas warmGas : Nat)
    (hSchedule : AccessScheduleRepresents schedule coldGas warmGas)
    (hCold : FitsUInt64 coldGas)
    (hWarm : FitsUInt64 warmGas) :
    chargeOf (Generated.price true true (accessBool access) false kind coldGas warmGas) =
      accountAccessCharge schedule access := by
  have hDecision := eip8038_access_decision_refines_accountAccessCharge
    schedule access kind coldGas warmGas hSchedule hCold hWarm
  simp only [chargeOf]
  rw [hDecision.1, hDecision.2]

theorem legacy_default_access_decision_refines_accountAccessCharge
    (schedule : AccountGasSchedule)
    (access : AccessStatus)
    (coldGas warmGas : Nat)
    (hSchedule : AccessScheduleRepresents schedule coldGas warmGas)
    (hCold : FitsUInt64 coldGas)
    (hWarm : FitsUInt64 warmGas) :
    let result := Generated.price true false (accessBool access) false .default coldGas warmGas
    result.decision = .charge ∧
      result.amount = accountAccessCharge schedule access := by
  rcases hSchedule with ⟨rfl, rfl⟩
  unfold FitsUInt64 at hCold hWarm
  cases access <;>
    simp [Eip803x.Generated.AccountAccessPricingKernel.price,
      Eip803x.Generated.AccountAccessPricingKernel.priceNormalized, accessBool,
      Eip803x.Generated.AccountAccessPricingKernel.normalizeUInt64,
      accountAccessCharge, hCold, hWarm]

theorem legacy_selfdestruct_warm_access_is_free
    (coldGas warmGas : Nat) :
    Generated.price true false false false .selfDestructBeneficiary coldGas warmGas =
      { decision := .noCharge, amount := 0 } := by
  simp [Eip803x.Generated.AccountAccessPricingKernel.price,
    Eip803x.Generated.AccountAccessPricingKernel.priceNormalized]

theorem legacy_selfdestruct_cold_nonprecompile_uses_cold_access
    (coldGas warmGas : Nat) (hCold : FitsUInt64 coldGas) :
    Generated.price true false true false .selfDestructBeneficiary coldGas warmGas =
      { decision := .charge, amount := coldGas } := by
  unfold FitsUInt64 at hCold
  simp [Eip803x.Generated.AccountAccessPricingKernel.price,
    Eip803x.Generated.AccountAccessPricingKernel.priceNormalized,
    Eip803x.Generated.AccountAccessPricingKernel.normalizeUInt64,
    hCold]

theorem hot_cold_disabled_is_free
    (eip8038Enabled isCold isPrecompile : Bool)
    (kind : Generated.AccessKind)
    (coldGas warmGas : Nat) :
    Generated.price false eip8038Enabled isCold isPrecompile kind coldGas warmGas =
      { decision := .noCharge, amount := 0 } := by
  simp [Eip803x.Generated.AccountAccessPricingKernel.price,
    Eip803x.Generated.AccountAccessPricingKernel.priceNormalized]

theorem eip8038_precompile_access_uses_warm_schedule
    (kind : Generated.AccessKind)
    (coldGas warmGas : Nat)
    (hWarm : FitsUInt64 warmGas) :
    Generated.price true true true true kind coldGas warmGas =
      { decision := .charge, amount := warmGas } := by
  unfold FitsUInt64 at hWarm
  simp [Eip803x.Generated.AccountAccessPricingKernel.price,
    Eip803x.Generated.AccountAccessPricingKernel.priceNormalized,
    Eip803x.Generated.AccountAccessPricingKernel.normalizeUInt64, hWarm]

theorem legacy_precompile_default_uses_warm_schedule
    (coldGas warmGas : Nat) :
    Generated.price true false true true .default coldGas warmGas =
      { decision := .charge,
        amount := Generated.normalizeUInt64 warmGas } := by
  simp [Eip803x.Generated.AccountAccessPricingKernel.price,
    Eip803x.Generated.AccountAccessPricingKernel.priceNormalized]

theorem legacy_precompile_selfdestruct_is_free
    (coldGas warmGas : Nat) :
    Generated.price true false true true .selfDestructBeneficiary coldGas warmGas =
      { decision := .noCharge, amount := 0 } := by
  simp [Eip803x.Generated.AccountAccessPricingKernel.price,
    Eip803x.Generated.AccountAccessPricingKernel.priceNormalized]

end Eip803x.Refinement.AccountAccessPricing
