-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.MemoryStackControl

namespace Eip803x
namespace Evm
namespace MemoryGas

open MemoryStackControl

/-- Configurable Yellow-Paper memory-expansion coefficients. -/
structure Schedule where
  linear : Nat
  quadraticDivisor : Nat
  quadraticDivisorPositive : 0 < quadraticDivisor
  deriving DecidableEq, Repr

namespace Schedule

def amsterdam : Schedule :=
  { linear := 3, quadraticDivisor := 512, quadraticDivisorPositive := by decide }

end Schedule

/-- Production's largest non-empty addressable range end, `int.MaxValue - 31`. -/
def maxMemorySize : Nat := 2147483616

def words (memory : Memory) : Nat := memory.bytes.length / 32

def totalCost (schedule : Schedule) (wordCount : Nat) : Nat :=
  wordCount * schedule.linear +
    wordCount * wordCount / schedule.quadraticDivisor

def rangeValid (offset length : UInt256) : Bool :=
  length.val = 0 ||
    (length.val <= maxMemorySize && offset.val <= maxMemorySize - length.val)

/--
Computes and installs the logical expansion before charging it, as
`EvmPooledMemory.CalculateMemoryCost` does. A zero-length access ignores its offset.
-/
def prepare (schedule : Schedule) (memory : Memory) (offset length : UInt256) :
    Option (Nat × Memory) :=
  if length.val = 0 then
    some (0, memory)
  else if rangeValid offset length then
    let expanded := memory.expand offset.val length.val
    let oldWords := words memory
    let newWords := words expanded
    some (totalCost schedule newWords - totalCost schedule oldWords, expanded)
  else
    none

theorem zero_length_preserves_memory (schedule : Schedule) (memory : Memory) (offset : UInt256) :
    prepare schedule memory offset Word.zero = some (0, memory) := by
  have hZero : Word.zero.val = 0 := by native_decide
  simp [prepare, hZero]

theorem invalid_nonempty_range_is_rejected
    (schedule : Schedule) (memory : Memory) (offset length : UInt256)
    (hLength : length.val ≠ 0) (hInvalid : rangeValid offset length = false) :
    prepare schedule memory offset length = none := by
  simp [prepare, hLength, hInvalid]

end MemoryGas
end Evm
end Eip803x
