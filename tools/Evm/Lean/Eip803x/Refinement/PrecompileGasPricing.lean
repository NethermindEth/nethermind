-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Generated.PrecompileGasPricingKernel
import Eip803x.Precompiles.Wrapper

namespace Eip803x.Refinement.PrecompileGasPricing

open Eip803x

namespace Generated

abbrev Outcome := Eip803x.Generated.PrecompileGasPricingKernel.Outcome
abbrev Result := Eip803x.Generated.PrecompileGasPricingKernel.Result
abbrev uint64Max := Eip803x.Generated.PrecompileGasPricingKernel.uint64Max
abbrev normalizeUInt64 := Eip803x.Generated.PrecompileGasPricingKernel.normalizeUInt64
abbrev tryConsumeNormalized := Eip803x.Generated.PrecompileGasPricingKernel.tryConsumeNormalized
abbrev tryConsume := Eip803x.Generated.PrecompileGasPricingKernel.tryConsume

end Generated

/-- Maps the extracted value-only result onto the handwritten wrapper outcome. -/
def toWrapperPricingResult (original : ProductionGasState) (result : Generated.Result) :
    Precompiles.Wrapper.PricingResult :=
  match result.outcome with
  | .success => .success { original with gasLeft := result.remainingGas } result.chargedGas
  | .baseDataOverflow => .failure .baseDataOverflow
      { original with gasLeft := result.remainingGas }
  | .outOfGas => .failure .outOfGas { original with gasLeft := result.remainingGas }

private theorem normalizeUInt64_of_fits {value : Nat}
    (h : value ≤ Generated.uint64Max) :
    Eip803x.Generated.PrecompileGasPricingKernel.normalizeUInt64 value = value := by
  simp [Eip803x.Generated.PrecompileGasPricingKernel.normalizeUInt64, h]

private theorem generated_uint64Max_eq_wrapper_uint64Max :
    Generated.uint64Max = Precompiles.Wrapper.uint64Max := by
  rfl

/--
The extracted `ulong` kernel refines the accepted handwritten wrapper pricing relation
when the three production `ulong` inputs are represented by their exact natural values.
-/
theorem tryConsume_refines_wrapper
    (gas : ProductionGasState) (baseGasCost dataGasCost : Nat)
    (hGas : gas.gasLeft ≤ Generated.uint64Max)
    (hBase : baseGasCost ≤ Generated.uint64Max)
    (hData : dataGasCost ≤ Generated.uint64Max) :
    toWrapperPricingResult gas
        (Eip803x.Generated.PrecompileGasPricingKernel.tryConsume
          gas.gasLeft baseGasCost dataGasCost) =
      Precompiles.Wrapper.tryConsumePrecompileGas
        { base := baseGasCost, data := dataGasCost } gas := by
  unfold Eip803x.Generated.PrecompileGasPricingKernel.tryConsume
  rw [normalizeUInt64_of_fits hGas,
    normalizeUInt64_of_fits hBase, normalizeUInt64_of_fits hData]
  have hDataWrapper : dataGasCost ≤ Precompiles.Wrapper.uint64Max := by
    simpa [generated_uint64Max_eq_wrapper_uint64Max] using hData
  by_cases hOverflow : baseGasCost > Generated.uint64Max - dataGasCost
  · have hNoWrapperCost : ¬ baseGasCost ≤ Precompiles.Wrapper.uint64Max - dataGasCost := by
      simpa [generated_uint64Max_eq_wrapper_uint64Max] using Nat.not_le_of_gt hOverflow
    simp [Eip803x.Generated.PrecompileGasPricingKernel.tryConsumeNormalized, toWrapperPricingResult,
      Precompiles.Wrapper.tryConsumePrecompileGas, Precompiles.Wrapper.totalCost?,
      hOverflow, hDataWrapper, hNoWrapperCost]
  · have hGeneratedCost : baseGasCost ≤ Generated.uint64Max - dataGasCost :=
      Nat.le_of_not_gt hOverflow
    have hWrapperCost : baseGasCost ≤ Precompiles.Wrapper.uint64Max - dataGasCost := by
      simpa [generated_uint64Max_eq_wrapper_uint64Max] using hGeneratedCost
    by_cases hAffordable : baseGasCost + dataGasCost ≤ gas.gasLeft
    · have hNotOutOfGas : ¬ gas.gasLeft < baseGasCost + dataGasCost :=
        Nat.not_lt_of_ge hAffordable
      simp [Eip803x.Generated.PrecompileGasPricingKernel.tryConsumeNormalized, toWrapperPricingResult,
        Precompiles.Wrapper.tryConsumePrecompileGas, Precompiles.Wrapper.totalCost?,
        hOverflow, hDataWrapper, hWrapperCost, hAffordable, hNotOutOfGas]
    · have hOutOfGas : gas.gasLeft < baseGasCost + dataGasCost :=
        Nat.lt_of_not_ge hAffordable
      simp [Eip803x.Generated.PrecompileGasPricingKernel.tryConsumeNormalized, toWrapperPricingResult,
        Precompiles.Wrapper.tryConsumePrecompileGas, Precompiles.Wrapper.totalCost?,
        hOverflow, hDataWrapper, hWrapperCost, hAffordable, hOutOfGas]

end Eip803x.Refinement.PrecompileGasPricing
