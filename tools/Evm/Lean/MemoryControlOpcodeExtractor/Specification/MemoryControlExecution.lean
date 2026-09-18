-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.MemoryGas

/-!
Gas and memory-expansion companion for the handwritten memory/stack/control
machine. `MemoryStackControl` intentionally omits gas; this module supplies the
exact Yellow-Paper charge plan after operands have been decoded. It does not
claim that unsafe C# stack or memory providers implement these values.
-/

namespace MemoryControlOpcodeExtractor.Specification

open Eip803x.Evm
open Eip803x.Evm.MemoryStackControl

inductive GasClass where
  | zero | base | veryLow | mid | high | jumpdest
  deriving DecidableEq, Repr

inductive DynamicGas where
  | none | copyWords
  deriving DecidableEq, Repr

inductive MemoryAccess where
  | none | word32 | byte1 | copyDestination | memoryCopy | returnRange
  deriving DecidableEq, Repr

inductive ExitKind where
  | stop | continues | jump | conditionalJump | returnData | revertData | invalid
  deriving DecidableEq, Repr

inductive Activation where
  | unconditional | eip140 | eip211 | eip3855 | eip5656
  deriving DecidableEq, Repr

structure Schedule where
  base : Nat
  veryLow : Nat
  mid : Nat
  high : Nat
  jumpdest : Nat
  copyWord : Nat
  memory : MemoryGas.Schedule
  deriving Repr

namespace Schedule

def amsterdam : Schedule :=
  { base := 2
    veryLow := 3
    mid := 8
    high := 10
    jumpdest := 1
    copyWord := 3
    memory := MemoryGas.Schedule.amsterdam }

end Schedule

def gasClass : Opcode → Option GasClass
  | .stop | .return | .revert | .invalid => some .zero
  | .pop | .pc | .msize | .gas => some .base
  | .mload | .mstore | .mstore8 | .calldataload | .calldatacopy | .codecopy |
      .returndatacopy | .mcopy => some .veryLow
  | .jump => some .mid
  | .jumpi => some .high
  | .jumpdest => some .jumpdest
  | .push width => if width ≤ 32 then some (if width = 0 then .base else .veryLow) else none
  | .dup depth | .swap depth => if 1 ≤ depth ∧ depth ≤ 16 then some .veryLow else none
  | _ => none

def fixedCost (schedule : Schedule) (opcode : Opcode) : Option Nat :=
  (gasClass opcode).map fun
    | .zero => 0
    | .base => schedule.base
    | .veryLow => schedule.veryLow
    | .mid => schedule.mid
    | .high => schedule.high
    | .jumpdest => schedule.jumpdest

def dynamicGas : Opcode → Option DynamicGas
  | .calldatacopy | .codecopy | .returndatacopy | .mcopy => some .copyWords
  | opcode => if (gasClass opcode).isSome then some .none else none

def memoryAccess : Opcode → Option MemoryAccess
  | .mload | .mstore => some .word32
  | .mstore8 => some .byte1
  | .calldatacopy | .codecopy | .returndatacopy => some .copyDestination
  | .mcopy => some .memoryCopy
  | .return | .revert => some .returnRange
  | opcode => if (gasClass opcode).isSome then some .none else none

def stackShape : Opcode → Option (Nat × Nat)
  | .stop | .jumpdest | .invalid => some (0, 0)
  | .pop => some (1, 0)
  | .mload | .calldataload => some (1, 0)
  | .mstore | .mstore8 | .jumpi | .return | .revert => some (2, 0)
  | .calldatacopy | .codecopy | .returndatacopy | .mcopy => some (3, 0)
  | .jump => some (1, 0)
  | .pc | .msize | .gas => some (0, 1)
  | .push width => if width ≤ 32 then some (0, 1) else none
  | .dup depth => if 1 ≤ depth ∧ depth ≤ 16 then some (depth, 1) else none
  | .swap depth => if 1 ≤ depth ∧ depth ≤ 16 then some (depth + 1, 0) else none
  | _ => none

def exitKind : Opcode → Option ExitKind
  | .stop => some .stop
  | .jump => some .jump
  | .jumpi => some .conditionalJump
  | .return => some .returnData
  | .revert => some .revertData
  | .invalid => some .invalid
  | opcode => if (gasClass opcode).isSome then some .continues else none

def activation : Opcode → Option Activation
  | .revert => some .eip140
  | .returndatacopy => some .eip211
  | .push 0 => some .eip3855
  | .mcopy => some .eip5656
  | opcode => if (gasClass opcode).isSome then some .unconditional else none

structure Operands where
  first : Eip803x.UInt256
  second : Eip803x.UInt256
  third : Eip803x.UInt256
  deriving DecidableEq, Repr

def copyWords (size : Eip803x.UInt256) : Nat := (size.val + 31) / 32

def maxWord (left right : Eip803x.UInt256) : Eip803x.UInt256 :=
  if left.val ≤ right.val then right else left

def memoryRequest : Opcode → Operands → Option (Eip803x.UInt256 × Eip803x.UInt256)
  | .mload, operands | .mstore, operands => some (operands.first, Word.ofNat 32)
  | .mstore8, operands => some (operands.first, Word.ofNat 1)
  | .calldatacopy, operands | .codecopy, operands | .returndatacopy, operands =>
      some (operands.first, operands.third)
  | .mcopy, operands => some (maxWord operands.first operands.second, operands.third)
  | .return, operands | .revert, operands => some (operands.first, operands.second)
  | _, _ => none

def dynamicCost (schedule : Schedule) (opcode : Opcode) (operands : Operands) : Option Nat :=
  match dynamicGas opcode with
  | none => none
  | some .none => some 0
  | some .copyWords => some (schedule.copyWord * copyWords operands.third)

def expansionCost (schedule : Schedule) (opcode : Opcode) (operands : Operands) (memory : Memory) : Option (Nat × Memory) :=
  match memoryAccess opcode with
  | none => none
  | some .none => some (0, memory)
  | some _ =>
      match memoryRequest opcode operands with
      | none => none
      | some (offset, length) => MemoryGas.prepare schedule.memory memory offset length

structure ChargePlan where
  fixed : Nat
  dynamic : Nat
  expansion : Nat
  resultingMemory : Memory
  deriving Repr

def chargePlan (schedule : Schedule) (opcode : Opcode) (operands : Operands) (memory : Memory) : Option ChargePlan := do
  let fixed ← fixedCost schedule opcode
  let dynamic ← dynamicCost schedule opcode operands
  let (expansion, resultingMemory) ← expansionCost schedule opcode operands memory
  pure { fixed, dynamic, expansion, resultingMemory }

def ChargePlan.total (plan : ChargePlan) : Nat := plan.fixed + plan.dynamic + plan.expansion

theorem zero_length_expansion_preserves_memory
    (schedule : Schedule) (memory : Memory) (offset : Eip803x.UInt256) :
    MemoryGas.prepare schedule.memory memory offset Word.zero = some (0, memory) :=
  MemoryGas.zero_length_preserves_memory schedule.memory memory offset

theorem copy_words_zero : copyWords Word.zero = 0 := by native_decide

theorem mcopy_uses_largest_start (operands : Operands) :
    memoryRequest (.mcopy) operands = some (maxWord operands.first operands.second, operands.third) := by
  rfl

end MemoryControlOpcodeExtractor.Specification
