-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Refinement.PrecompileGasPricing

namespace Eip803x.PrecompileGasPricingVectors

open Precompiles.Wrapper
open Refinement.PrecompileGasPricing

def gas (gasLeft : Nat) : ProductionGasState :=
  { gasLeft
    stateReservoir := 41
    stateGasUsed := 17
    stateGasSpill := 29
    stateGasSpillRefunded := 11 }

example :
    toWrapperPricingResult (gas 18)
        (Eip803x.Generated.PrecompileGasPricingKernel.tryConsume 18 15 3) =
      .success (gas 0) 18 := by native_decide

example :
    toWrapperPricingResult (gas 17)
        (Eip803x.Generated.PrecompileGasPricingKernel.tryConsume 17 15 3) =
      .failure .outOfGas (gas 0) := by native_decide

example :
    toWrapperPricingResult (gas 73)
        (Eip803x.Generated.PrecompileGasPricingKernel.tryConsume 73
          Eip803x.Generated.PrecompileGasPricingKernel.uint64Max 1) =
      .failure .baseDataOverflow (gas 73) := by native_decide

example :
    Eip803x.Generated.PrecompileGasPricingKernel.tryConsume
        Eip803x.Generated.PrecompileGasPricingKernel.uint64Max 0
        Eip803x.Generated.PrecompileGasPricingKernel.uint64Max =
      { outcome := .success,
        remainingGas := 0,
        chargedGas := Eip803x.Generated.PrecompileGasPricingKernel.uint64Max } := by
  native_decide

example :
    Eip803x.Generated.PrecompileGasPricingKernel.tryConsume
        Eip803x.Generated.PrecompileGasPricingKernel.uint64Max
        Eip803x.Generated.PrecompileGasPricingKernel.uint64Max 0 =
      { outcome := .success,
        remainingGas := 0,
        chargedGas := Eip803x.Generated.PrecompileGasPricingKernel.uint64Max } := by
  native_decide

end Eip803x.PrecompileGasPricingVectors
