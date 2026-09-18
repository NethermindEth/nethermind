-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.MemoryStackControl
import Lean.Elab.Tactic.Omega

namespace Eip803x
namespace Precompiles
namespace Identity

open Evm.MemoryStackControl

structure Schedule where
  base : Nat
  word : Nat
  deriving DecidableEq, Repr

namespace Schedule

def amsterdam : Schedule := { base := 15, word := 3 }

end Schedule

def address : Nat := 4

def name : String := "ID"

def supportsCaching : Bool := false

def wordsForBytes (length : Nat) : Nat := (length + 31) / 32

def baseGasCost (schedule : Schedule) : Nat := schedule.base

def dataGasCost (schedule : Schedule) (input : List Byte) : Nat :=
  schedule.word * wordsForBytes input.length

def totalGasCost (schedule : Schedule) (input : List Byte) : Nat :=
  baseGasCost schedule + dataGasCost schedule input

/-- The identity precompile always succeeds and returns an owned copy of its input bytes. -/
def run (input : List Byte) : Bool × List Byte := (true, input)

theorem run_succeeds (input : List Byte) : (run input).1 = true := rfl

theorem run_returns_input (input : List Byte) : (run input).2 = input := rfl

theorem output_length (input : List Byte) : (run input).2.length = input.length := rfl

theorem empty_data_cost (schedule : Schedule) : dataGasCost schedule [] = 0 := by
  simp [dataGasCost, wordsForBytes]

theorem wordsForBytes_zero : wordsForBytes 0 = 0 := by native_decide

theorem wordsForBytes_positive (length : Nat) (h : 0 < length) :
    wordsForBytes length = (length - 1) / 32 + 1 := by
  unfold wordsForBytes
  omega

theorem word_boundary (words : Nat) : wordsForBytes (32 * words) = words := by
  unfold wordsForBytes
  rw [Nat.mul_add_div (by decide) words 31]
  simp

theorem one_past_word_boundary (words : Nat) :
    wordsForBytes (32 * words + 1) = words + 1 := by
  unfold wordsForBytes
  calc
    (32 * words + 1 + 31) / 32 = (32 * words + 32) / 32 := by congr 1 <;> omega
    _ = words + 32 / 32 := by rw [Nat.mul_add_div (by decide) words 32]
    _ = words + 1 := by simp

end Identity
end Precompiles
end Eip803x
