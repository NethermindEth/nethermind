-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.ExtendedStack

namespace Eip803x
namespace Evm
namespace ExtendedStack
namespace BoundaryVector

open Word
open MemoryStackControl

def b (value : Nat) : Byte := MemoryStackControl.byte value

def w (value : Nat) : UInt256 := Word.ofNat value

def words (count : Nat) : List UInt256 :=
  (List.range count).map fun index => w (index + 1)

def stack (count : Nat) : Stack := Stack.fromWords (words count)

def observe : Except Fault Stack → Except Fault (List UInt256)
  | .ok result => .ok result.words
  | .error fault => .error fault

def sameOutcome : Except Fault (List UInt256) → Except Fault (List UInt256) → Bool
  | .ok left, .ok right => decide (left = right)
  | .error left, .error right => decide (left = right)
  | _, _ => false

theorem decoder_boundaries :
    decodeSingle (b 0) = some 145 ∧
      decodeSingle (b 90) = some 235 ∧
      decodeSingle (b 91) = none ∧
      decodeSingle (b 127) = none ∧
      decodeSingle (b 128) = some 17 ∧
      decodeSingle (b 255) = some 144 ∧
      decodePair (b 0) = some (9, 16) ∧
      decodePair (b 81) = some (14, 15) ∧
      decodePair (b 82) = none ∧
      decodePair (b 127) = none ∧
      decodePair (b 128) = some (1, 16) ∧
      decodePair (b 255) = some (1, 22) := by
  native_decide

theorem duplicate_boundary_vectors :
    sameOutcome (observe (duplicate (b 128) (stack 17))) (.ok (w 17 :: words 17)) = true ∧
      sameOutcome (observe (duplicate (b 128) (stack 16))) (.error .underflow) = true ∧
      sameOutcome (observe (duplicate (b 91) (stack 17))) (.error .invalidImmediate) = true ∧
      sameOutcome (observe (duplicate (b 128) Stack.full)) (.error .overflow) = true := by
  native_decide

def swap128Expected : List UInt256 :=
  [w 18] ++ ((words 18).drop 1).take 16 ++ [w 1]

theorem swap_boundary_vectors :
    sameOutcome (observe (swapTop (b 128) (stack 18))) (.ok swap128Expected) = true ∧
      sameOutcome (observe (swapTop (b 128) (stack 17))) (.error .underflow) = true ∧
      sameOutcome (observe (swapTop (b 91) (stack 18))) (.error .invalidImmediate) = true := by
  native_decide

def exchange128Expected : List UInt256 :=
  [w 1, w 17] ++ ((words 17).drop 2).take 14 ++ [w 2]

theorem exchange_boundary_vectors :
    sameOutcome (observe (exchange (b 128) (stack 17))) (.ok exchange128Expected) = true ∧
      sameOutcome (observe (exchange (b 128) (stack 16))) (.error .underflow) = true ∧
      sameOutcome (observe (exchange (b 82) (stack 17))) (.error .invalidImmediate) = true := by
  native_decide

/-- These cases kill the decoder shift, XOR, branch, and position-offset mutations. -/
theorem decoder_mutation_boundaries :
    decodeSingle (b 0) ≠ some 0 ∧
      decodeSingle (b 128) ≠ some 16 ∧
      decodePair (b 0) ≠ some (8, 15) ∧
      decodePair (b 255) ≠ some (0, 21) := by
  native_decide

theorem decoder_boundary_neutrality_vectors :
    singleImmediateIsLegacyBoundaryNeutral (b 0x5a) = true ∧
      decodeSingle (b 0x5b) = none ∧
      decodeSingle (b 0x60) = none ∧
      decodeSingle (b 0x7f) = none ∧
      singleImmediateIsLegacyBoundaryNeutral (b 0x80) = true ∧
      pairImmediateIsLegacyBoundaryNeutral (b 0x51) = true ∧
      decodePair (b 0x52) = none ∧
      decodePair (b 0x5b) = none ∧
      decodePair (b 0x60) = none ∧
      decodePair (b 0x7f) = none ∧
      pairImmediateIsLegacyBoundaryNeutral (b 0x80) = true := by
  native_decide

end BoundaryVector
end ExtendedStack
end Evm
end Eip803x
