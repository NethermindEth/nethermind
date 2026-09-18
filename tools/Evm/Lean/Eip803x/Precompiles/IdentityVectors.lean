-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Precompiles.Identity

namespace Eip803x
namespace Precompiles
namespace IdentityVectors

open Evm.MemoryStackControl
open Identity

def schedule : Schedule := Schedule.amsterdam

def bytes (length : Nat) : List Byte :=
  (List.range length).map Evm.MemoryStackControl.byte

example : address = 4 := by native_decide

example : name = "ID" := by native_decide

example : supportsCaching = false := by native_decide

example : totalGasCost schedule (bytes 0) = 15 := by native_decide

example : totalGasCost schedule (bytes 1) = 18 := by native_decide

example : totalGasCost schedule (bytes 31) = 18 := by native_decide

example : totalGasCost schedule (bytes 32) = 18 := by native_decide

example : totalGasCost schedule (bytes 33) = 21 := by native_decide

example : totalGasCost schedule (bytes 64) = 21 := by native_decide

example : run [byte 0x00, byte 0x7f, byte 0xff] =
    (true, [byte 0x00, byte 0x7f, byte 0xff]) := by native_decide

/-- Mutation sentinel: floor division would undercharge a one-byte input. -/
def mutatedFloorDataGas (input : List Byte) : Nat :=
  schedule.word * (input.length / 32)

example : mutatedFloorDataGas (bytes 1) ≠ dataGasCost schedule (bytes 1) := by
  native_decide

/-- Mutation sentinel: returning an empty buffer is observably different on non-empty input. -/
example : (run [byte 0xaa]).2 ≠ ([] : List Byte) := by native_decide

end IdentityVectors
end Precompiles
end Eip803x
