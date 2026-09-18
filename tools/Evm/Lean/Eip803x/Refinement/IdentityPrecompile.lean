-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Generated.IdentityPrecompileKernel
import Eip803x.Precompiles.Identity

namespace Eip803x
namespace Refinement
namespace IdentityPrecompile

theorem normalizeUInt32_eq
    (inputLength : Nat)
    (h : inputLength <= Eip803x.Generated.IdentityPrecompileKernel.uint32Max) :
    Eip803x.Generated.IdentityPrecompileKernel.normalizeUInt32 inputLength = inputLength := by
  simp [Eip803x.Generated.IdentityPrecompileKernel.normalizeUInt32, h]

theorem generated_baseGasCost_eq_spec :
    Eip803x.Generated.IdentityPrecompileKernel.baseGasCost =
      Eip803x.Precompiles.Identity.baseGasCost Eip803x.Precompiles.Identity.Schedule.amsterdam := rfl

theorem generated_wordsForBytes_eq_spec
    (inputLength : Nat)
    (h : inputLength <= Eip803x.Generated.IdentityPrecompileKernel.uint32Max) :
    Eip803x.Generated.IdentityPrecompileKernel.wordsForBytes inputLength =
      Eip803x.Precompiles.Identity.wordsForBytes inputLength := by
  simp [Eip803x.Generated.IdentityPrecompileKernel.wordsForBytes,
    Eip803x.Precompiles.Identity.wordsForBytes, normalizeUInt32_eq inputLength h]

/--
The extracted fixed-width pricing kernel refines the independent identity-precompile
schedule for every length representable by the production `ReadOnlyMemory<byte>` adapter.
-/
theorem generated_dataGasCost_eq_spec
    (input : List Eip803x.Evm.MemoryStackControl.Byte)
    (h : input.length <= Eip803x.Generated.IdentityPrecompileKernel.uint32Max) :
    Eip803x.Generated.IdentityPrecompileKernel.dataGasCost input.length =
      Eip803x.Precompiles.Identity.dataGasCost Eip803x.Precompiles.Identity.Schedule.amsterdam input := by
  simp [Eip803x.Generated.IdentityPrecompileKernel.dataGasCost,
    Eip803x.Precompiles.Identity.dataGasCost, Eip803x.Precompiles.Identity.Schedule.amsterdam,
    generated_wordsForBytes_eq_spec input.length h]

end IdentityPrecompile
end Refinement
end Eip803x
