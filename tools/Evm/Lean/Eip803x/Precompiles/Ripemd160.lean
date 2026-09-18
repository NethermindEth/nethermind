-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.MemoryStackControl
import Lean.Elab.Tactic.Omega

namespace Eip803x
namespace Precompiles
namespace Ripemd160

open Evm.MemoryStackControl

/-!
  A handwritten executable reference for the pure RIPEMD-160 precompile leaf.

  The cryptographic operation is an explicit oracle. A `Digest` is a
  length-indexed 20-byte value; the leaf constructs the required 32-byte result
  by prepending twelve zero bytes. Production metrics, allocation and CLR
  buffer behavior, registration, wrapper and world-state behavior (including
  the sticky RIPEMD touch), and production refinement remain separate
  obligations.
-/

structure Schedule where
  base : Nat
  word : Nat
  deriving DecidableEq, Repr

namespace Schedule

def amsterdam : Schedule := { base := 600, word := 120 }

end Schedule

def address : Nat := 3

def name : String := "RIPEMD160"

def supportsCaching : Bool := true

def wordsForBytes (length : Nat) : Nat := (length + 31) / 32

def baseGasCost (schedule : Schedule) : Nat := schedule.base

def dataGasCost (schedule : Schedule) (input : List Byte) : Nat :=
  schedule.word * wordsForBytes input.length

def totalGasCost (schedule : Schedule) (input : List Byte) : Nat :=
  baseGasCost schedule + dataGasCost schedule input

structure Digest where
  bytes : List Byte
  length_eq : bytes.length = 20
  deriving DecidableEq, Repr

structure Ripemd160Oracle where
  hash : List Byte -> Digest

def zeroDigest : Digest :=
  { bytes := List.replicate 20 zeroByte
    length_eq := by simp }

def digestOfBytes (bytes : List Byte) (length_eq : bytes.length = 20) : Digest :=
  { bytes, length_eq }

def zeroPrefix : List Byte := List.replicate 12 zeroByte

def paddedOutput (digest : Digest) : List Byte := zeroPrefix ++ digest.bytes

/- The oracle is deliberately separate from the leaf so no cryptographic
   implementation is hidden in this model. -/
def run (oracle : Ripemd160Oracle) (input : List Byte) : Bool × List Byte :=
  (true, paddedOutput (oracle.hash input))

theorem run_succeeds (oracle : Ripemd160Oracle) (input : List Byte) :
    (run oracle input).1 = true := rfl

theorem run_returns_padded_digest (oracle : Ripemd160Oracle) (input : List Byte) :
    (run oracle input).2 = zeroPrefix ++ (oracle.hash input).bytes := rfl

theorem padded_output_length (digest : Digest) :
    (paddedOutput digest).length = 32 := by
  simp [paddedOutput, zeroPrefix, digest.length_eq]

theorem padded_output_prefix (digest : Digest) :
    (paddedOutput digest).take 12 = zeroPrefix := by
  simp [paddedOutput, zeroPrefix]

theorem padded_output_suffix (digest : Digest) :
    (paddedOutput digest).drop 12 = digest.bytes := by
  simp [paddedOutput, zeroPrefix]

theorem output_length (oracle : Ripemd160Oracle) (input : List Byte) :
    (run oracle input).2.length = 32 := by
  exact padded_output_length (oracle.hash input)

theorem output_prefix (oracle : Ripemd160Oracle) (input : List Byte) :
    (run oracle input).2.take 12 = zeroPrefix := by
  exact padded_output_prefix (oracle.hash input)

theorem output_suffix (oracle : Ripemd160Oracle) (input : List Byte) :
    (run oracle input).2.drop 12 = (oracle.hash input).bytes := by
  exact padded_output_suffix (oracle.hash input)

theorem run_contract (oracle : Ripemd160Oracle) (input : List Byte) :
    (run oracle input).1 = true ∧
      (run oracle input).2 = zeroPrefix ++ (oracle.hash input).bytes ∧
      (run oracle input).2.length = 32 ∧
      (run oracle input).2.take 12 = zeroPrefix ∧
      (run oracle input).2.drop 12 = (oracle.hash input).bytes := by
  exact ⟨run_succeeds oracle input, run_returns_padded_digest oracle input,
    output_length oracle input, output_prefix oracle input, output_suffix oracle input⟩

theorem wordsForBytes_zero : wordsForBytes 0 = 0 := by native_decide

theorem wordsForBytes_positive (length : Nat) (h : 0 < length) :
    wordsForBytes length = (length - 1) / 32 + 1 := by
  unfold wordsForBytes
  omega

theorem word_boundary (words : Nat) :
    wordsForBytes (32 * words) = words := by
  unfold wordsForBytes
  rw [Nat.mul_add_div (by decide) words 31]
  simp

theorem one_past_word_boundary (words : Nat) :
    wordsForBytes (32 * words + 1) = words + 1 := by
  unfold wordsForBytes
  calc
    (32 * words + 1 + 31) / 32 = (32 * words + 32) / 32 := by
      congr 1 <;> omega
    _ = words + 32 / 32 := by rw [Nat.mul_add_div (by decide) words 32]
    _ = words + 1 := by simp

theorem empty_data_cost (schedule : Schedule) :
    dataGasCost schedule [] = 0 := by
  simp [dataGasCost, wordsForBytes]

theorem data_cost_at_word_boundary (schedule : Schedule) (words : Nat)
    (input : List Byte) (length_eq : input.length = 32 * words) :
    dataGasCost schedule input = schedule.word * words := by
  simp [dataGasCost, length_eq, word_boundary]

theorem data_cost_one_past_word_boundary (schedule : Schedule) (words : Nat)
    (input : List Byte) (length_eq : input.length = 32 * words + 1) :
    dataGasCost schedule input = schedule.word * (words + 1) := by
  simp [dataGasCost, length_eq, one_past_word_boundary]

theorem total_cost_at_word_boundary (schedule : Schedule) (words : Nat)
    (input : List Byte) (length_eq : input.length = 32 * words) :
    totalGasCost schedule input = schedule.base + schedule.word * words := by
  simp [totalGasCost, baseGasCost, data_cost_at_word_boundary schedule words input length_eq]

theorem total_cost_one_past_word_boundary (schedule : Schedule) (words : Nat)
    (input : List Byte) (length_eq : input.length = 32 * words + 1) :
    totalGasCost schedule input = schedule.base + schedule.word * (words + 1) := by
  simp [totalGasCost, baseGasCost,
    data_cost_one_past_word_boundary schedule words input length_eq]

theorem total_cost_depends_on_length (schedule : Schedule)
    (left right : List Byte) (length_eq : left.length = right.length) :
    totalGasCost schedule left = totalGasCost schedule right := by
  simp [totalGasCost, dataGasCost, length_eq]

theorem metadata_contract :
    address = 3 ∧ name = "RIPEMD160" ∧ supportsCaching = true := by
  native_decide

end Ripemd160
end Precompiles
end Eip803x
