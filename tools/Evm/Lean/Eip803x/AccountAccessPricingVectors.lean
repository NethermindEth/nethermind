-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Generated.AccountAccessPricingKernel

namespace Eip803x.AccountAccessPricingVectors

open Eip803x

namespace Generated

abbrev Decision := Eip803x.Generated.AccountAccessPricingKernel.Decision
abbrev AccessKind := Eip803x.Generated.AccountAccessPricingKernel.AccessKind
abbrev Result := Eip803x.Generated.AccountAccessPricingKernel.Result
abbrev price := Eip803x.Generated.AccountAccessPricingKernel.price

end Generated

structure PricingVector where
  name : String
  hotAndColdEnabled : Bool
  eip8038Enabled : Bool
  isCold : Bool
  isPrecompile : Bool
  kind : Generated.AccessKind
  coldAccountAccessGas : Nat
  warmAccessGas : Nat
  expected : Generated.Result
  deriving DecidableEq, Repr

private def expected (decision : Generated.Decision) (amount : Nat) : Generated.Result :=
  { decision, amount }

/-! Literal boundary vectors cover every generated branch, both account-access kinds, precompile
    facts, legacy/EIP-8038 SELFDESTRUCT behavior, and uint64 schedule normalization.  The expected
    decision and amount fields are normative values rather than calls to a second pricing formula. -/
def vectors : List PricingVector :=
  [ { name := "hot-off-cold-default"
      hotAndColdEnabled := false, eip8038Enabled := false, isCold := true, isPrecompile := false
      kind := .default, coldAccountAccessGas := 2600, warmAccessGas := 100
      expected := expected .noCharge 0 }
  , { name := "hot-off-warm-selfdestruct"
      hotAndColdEnabled := false, eip8038Enabled := false, isCold := false, isPrecompile := false
      kind := .selfDestructBeneficiary, coldAccountAccessGas := 3000, warmAccessGas := 100
      expected := expected .noCharge 0 }
  , { name := "eip8038-cold-default"
      hotAndColdEnabled := true, eip8038Enabled := true, isCold := true, isPrecompile := false
      kind := .default, coldAccountAccessGas := 3000, warmAccessGas := 100
      expected := expected .charge 3000 }
  , { name := "eip8038-warm-default"
      hotAndColdEnabled := true, eip8038Enabled := true, isCold := false, isPrecompile := false
      kind := .default, coldAccountAccessGas := 3000, warmAccessGas := 100
      expected := expected .charge 100 }
  , { name := "eip8038-cold-default-u64-wrap"
      hotAndColdEnabled := true, eip8038Enabled := true, isCold := true, isPrecompile := false
      kind := .default, coldAccountAccessGas := 18446744073709551623, warmAccessGas := 100
      expected := expected .charge 7 }
  , { name := "eip8038-warm-default-u64-wrap"
      hotAndColdEnabled := true, eip8038Enabled := true, isCold := false, isPrecompile := false
      kind := .default, coldAccountAccessGas := 3000, warmAccessGas := 18446744073709551621
      expected := expected .charge 5 }
  , { name := "eip8038-cold-precompile-default"
      hotAndColdEnabled := true, eip8038Enabled := true, isCold := true, isPrecompile := true
      kind := .default, coldAccountAccessGas := 3000, warmAccessGas := 100
      expected := expected .charge 100 }
  , { name := "eip8038-cold-selfdestruct"
      hotAndColdEnabled := true, eip8038Enabled := true, isCold := true, isPrecompile := false
      kind := .selfDestructBeneficiary, coldAccountAccessGas := 3000, warmAccessGas := 100
      expected := expected .charge 3000 }
  , { name := "eip8038-warm-selfdestruct"
      hotAndColdEnabled := true, eip8038Enabled := true, isCold := false, isPrecompile := false
      kind := .selfDestructBeneficiary, coldAccountAccessGas := 3000, warmAccessGas := 100
      expected := expected .charge 100 }
  , { name := "eip8038-cold-precompile-selfdestruct"
      hotAndColdEnabled := true, eip8038Enabled := true, isCold := true, isPrecompile := true
      kind := .selfDestructBeneficiary, coldAccountAccessGas := 3000, warmAccessGas := 100
      expected := expected .charge 100 }
  , { name := "legacy-cold-default"
      hotAndColdEnabled := true, eip8038Enabled := false, isCold := true, isPrecompile := false
      kind := .default, coldAccountAccessGas := 2600, warmAccessGas := 100
      expected := expected .charge 2600 }
  , { name := "legacy-warm-default"
      hotAndColdEnabled := true, eip8038Enabled := false, isCold := false, isPrecompile := false
      kind := .default, coldAccountAccessGas := 2600, warmAccessGas := 100
      expected := expected .charge 100 }
  , { name := "legacy-cold-precompile-default"
      hotAndColdEnabled := true, eip8038Enabled := false, isCold := true, isPrecompile := true
      kind := .default, coldAccountAccessGas := 2600, warmAccessGas := 100
      expected := expected .charge 100 }
  , { name := "legacy-cold-selfdestruct"
      hotAndColdEnabled := true, eip8038Enabled := false, isCold := true, isPrecompile := false
      kind := .selfDestructBeneficiary, coldAccountAccessGas := 2600, warmAccessGas := 100
      expected := expected .charge 2600 }
  , { name := "legacy-warm-selfdestruct"
      hotAndColdEnabled := true, eip8038Enabled := false, isCold := false, isPrecompile := false
      kind := .selfDestructBeneficiary, coldAccountAccessGas := 2600, warmAccessGas := 100
      expected := expected .noCharge 0 }
  , { name := "legacy-cold-precompile-selfdestruct"
      hotAndColdEnabled := true, eip8038Enabled := false, isCold := true, isPrecompile := true
      kind := .selfDestructBeneficiary, coldAccountAccessGas := 2600, warmAccessGas := 100
      expected := expected .noCharge 0 } ]

private def passes (vector : PricingVector) : Bool :=
  Generated.price
      vector.hotAndColdEnabled
      vector.eip8038Enabled
      vector.isCold
      vector.isPrecompile
      vector.kind
      vector.coldAccountAccessGas
      vector.warmAccessGas = vector.expected

theorem all_pass : vectors.all passes = true := by
  native_decide

theorem vector_count : vectors.length = 16 := rfl

end Eip803x.AccountAccessPricingVectors
