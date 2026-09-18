-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.MemoryStackControl

namespace Eip803x
namespace Evm
namespace ExtendedStack

open Word
open MemoryStackControl

abbrev Byte := MemoryStackControl.Byte
abbrev Stack := MemoryStackControl.Stack

inductive Fault where
  | invalidImmediate
  | underflow
  | overflow
  deriving DecidableEq, Repr

def allBytes : List Byte :=
  (List.range 256).map MemoryStackControl.byte

/-- EIP-8024 index-shifting decoder for DUPN and SWAPN. -/
def decodeSingle (immediate : Byte) : Option Nat :=
  let value := immediate.val
  if value ≤ 90 ∨ 128 ≤ value then
    some ((value + 145) % 256)
  else
    none

/-- EIP-8024 pair decoder for EXCHANGE. -/
def decodePair (immediate : Byte) : Option (Nat × Nat) :=
  let value := immediate.val
  if value ≤ 81 ∨ 128 ≤ value then
    let shifted := Nat.xor value 143
    let quotient := shifted / 16
    let remainder := shifted % 16
    if quotient < remainder then
      some (quotient + 1, remainder + 1)
    else
      some (remainder + 1, 29 - quotient)
  else
    none

def mapStackFault : MemoryStackControl.StackFault → Fault
  | .underflow => .underflow
  | .overflow => .overflow

def duplicate (immediate : Byte) (stack : Stack) : Except Fault Stack :=
  match decodeSingle immediate with
  | none => .error .invalidImmediate
  | some depth =>
    match stack.duplicate depth with
    | .ok result => .ok result
    | .error fault => .error (mapStackFault fault)

def swapTop (immediate : Byte) (stack : Stack) : Except Fault Stack :=
  match decodeSingle immediate with
  | none => .error .invalidImmediate
  | some depth =>
    match stack.swap depth with
    | some result => .ok result
    | none => .error .underflow

def exchangeAt (first second : Nat) (stack : Stack) : Except Fault Stack :=
  match hFirst : stack.words[first]?, hSecond : stack.words[second]? with
  | some firstValue, some secondValue =>
    match hReplaceFirst : MemoryStackControl.Stack.replaceAt first secondValue stack.words with
    | none => .error .underflow
    | some firstReplacement =>
      match hReplaceSecond : MemoryStackControl.Stack.replaceAt second firstValue firstReplacement with
      | none => .error .underflow
      | some result =>
        .ok ⟨result, by
          rw [MemoryStackControl.Stack.replaceAt_length second firstValue firstReplacement result
            hReplaceSecond]
          rw [MemoryStackControl.Stack.replaceAt_length first secondValue stack.words firstReplacement
            hReplaceFirst]
          exact stack.bounded⟩
  | _, _ => .error .underflow

def exchange (immediate : Byte) (stack : Stack) : Except Fault Stack :=
  match decodePair immediate with
  | none => .error .invalidImmediate
  | some (first, second) => exchangeAt first second stack

def singleDecodeInRange (immediate : Byte) : Bool :=
  match decodeSingle immediate with
  | none => true
  | some depth => decide (17 ≤ depth ∧ depth ≤ 235)

def pairDecodeInRange (immediate : Byte) : Bool :=
  match decodePair immediate with
  | none => true
  | some (first, second) =>
    decide (1 ≤ first ∧ first ≤ 14 ∧ first < second ∧ second ≤ 30 - first)

/--
A valid EIP-8024 immediate is never `JUMPDEST` or a legacy `PUSH1`..`PUSH32`
opcode. Consequently, treating E6-E8 as width one in a legacy JUMPDEST scan is
observationally equivalent to skipping the valid immediate: the extra byte
cannot be a destination and cannot change the next legacy instruction boundary.
-/
def singleImmediateIsLegacyBoundaryNeutral (immediate : Byte) : Bool :=
  match decodeSingle immediate with
  | none => true
  | some _ => decide (immediate.val ≠ 0x5b ∧ ¬ (0x60 ≤ immediate.val ∧ immediate.val ≤ 0x7f))

def pairImmediateIsLegacyBoundaryNeutral (immediate : Byte) : Bool :=
  match decodePair immediate with
  | none => true
  | some _ => decide (immediate.val ≠ 0x5b ∧ ¬ (0x60 ≤ immediate.val ∧ immediate.val ≤ 0x7f))

theorem every_single_decode_is_in_range :
    allBytes.all singleDecodeInRange = true := by native_decide

theorem every_pair_decode_is_in_range :
    allBytes.all pairDecodeInRange = true := by native_decide

theorem every_valid_single_immediate_is_legacy_boundary_neutral :
    allBytes.all singleImmediateIsLegacyBoundaryNeutral = true := by native_decide

theorem every_valid_pair_immediate_is_legacy_boundary_neutral :
    allBytes.all pairImmediateIsLegacyBoundaryNeutral = true := by native_decide

theorem single_forbidden_range_is_rejected (value : Nat)
    (hLower : 91 ≤ value) (hUpper : value ≤ 127) :
    decodeSingle (MemoryStackControl.byte value) = none := by
  simp [decodeSingle, MemoryStackControl.byte, Nat.mod_eq_of_lt (by omega : value < 256),
    Nat.not_le_of_gt (by omega : 90 < value), Nat.not_le_of_gt (by omega : value < 128)]

theorem pair_forbidden_range_is_rejected (value : Nat)
    (hLower : 82 ≤ value) (hUpper : value ≤ 127) :
    decodePair (MemoryStackControl.byte value) = none := by
  simp [decodePair, MemoryStackControl.byte, Nat.mod_eq_of_lt (by omega : value < 256),
    Nat.not_le_of_gt (by omega : 81 < value), Nat.not_le_of_gt (by omega : value < 128)]

theorem duplicate_invalid_immediate (immediate : Byte)
    (h : decodeSingle immediate = none) (stack : Stack) :
    duplicate immediate stack = .error .invalidImmediate := by
  simp [duplicate, h]

theorem swap_invalid_immediate (immediate : Byte)
    (h : decodeSingle immediate = none) (stack : Stack) :
    swapTop immediate stack = .error .invalidImmediate := by
  simp [swapTop, h]

theorem exchange_invalid_immediate (immediate : Byte)
    (h : decodePair immediate = none) (stack : Stack) :
    exchange immediate stack = .error .invalidImmediate := by
  simp [exchange, h]

end ExtendedStack
end Evm
end Eip803x
