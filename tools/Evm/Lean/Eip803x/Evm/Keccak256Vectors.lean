-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.Keccak256

namespace Eip803x
namespace Evm
namespace Keccak256Vectors

open Keccak256
open MemoryStackControl
open Word

def schedule : Schedule := Schedule.amsterdam

def stackOf (values : List UInt256) (h : values.length <= stackLimit := by native_decide) : Stack :=
  { words := values, bounded := h }

def gas (left : Nat) : GasState :=
  { gasLeft := left
    stateReservoir := 17
    stateFromGasLeft := 3
    stateUsed := 9
    refundCounter := -4 }

def initial (left : Nat) (values : List UInt256)
    (h : values.length <= stackLimit := by native_decide) : MachineState :=
  { gas := gas left
    stack := stackOf values h
    memory := Memory.ofBytes [byte 0xaa, byte 0xbb, byte 0xcc, byte 0xdd] }

/-- An executable stand-in checks the exact byte sequence supplied to the hash boundary. -/
def recordingHash (input : List Byte) : UInt256 := bytesToWord input

example : checkedWords (ofNat 0) = (0, false) := by native_decide
example : checkedWords (ofNat 1) = (1, false) := by native_decide
example : checkedWords (ofNat 32) = (1, false) := by native_decide
example : checkedWords (ofNat 33) = (2, false) := by native_decide
example : checkedWords (ofNat (maxUInt32 * 32)) = (maxUInt32, false) := by native_decide
example : checkedWords (ofNat (maxUInt32 * 32 + 1)) = (0, true) := by native_decide
example : checkedWords (ofNat (maxUInt64 + 1)) = (0, true) := by native_decide

example :
    let outcome := execute schedule recordingHash (initial 99 [])
    outcome.status = .stackUnderflow /\ outcome.state.gas.gasLeft = 99 := by
  native_decide

/-- An oversized length still pays the fixed 30 gas before its explicit OOG flag. -/
example :
    let huge := ofNat (maxUInt32 * 32 + 1)
    let outcome := execute schedule recordingHash (initial 35 [zero, huge])
    outcome.status = .outOfGas /\ outcome.state.gas.gasLeft = 5 /\
      outcome.state.stack.words = [] := by
  native_decide

/-- Failure to afford the initial size-derived charge burns execution gas. -/
example :
    let outcome := execute schedule recordingHash (initial 35 [zero, ofNat 1])
    outcome.status = .outOfGas /\ outcome.state.gas.gasLeft = 0 := by
  native_decide

/-- A zero-sized hash ignores an otherwise invalid offset and charges only 30 gas. -/
example :
    let hugeOffset := ofNat (MemoryGas.maxMemorySize + 1)
    let outcome := execute schedule recordingHash (initial 30 [hugeOffset, zero])
    outcome.status = .ok /\ outcome.state.gas.gasLeft = 0 /\
      outcome.state.stack.words = [zero] := by
  native_decide

/-- The oracle sees bytes `bb cc`, in order, and its result replaces both operands. -/
example :
    let outcome := execute schedule recordingHash (initial 36 [ofNat 1, ofNat 2])
    outcome.status = .ok /\ outcome.state.gas.gasLeft = 0 /\
      outcome.state.stack.words = [ofNat 0xbbcc] /\
      outcome.state.gas.stateReservoir = 17 /\
      outcome.state.gas.stateFromGasLeft = 3 /\
      outcome.state.gas.stateUsed = 9 /\
      outcome.state.gas.refundCounter = -4 := by
  native_decide

/-- Two words from empty memory cost 42 dynamic plus 6 expansion gas. -/
example :
    let state : MachineState :=
      { gas := gas 48
        stack := stackOf [zero, ofNat 33]
        memory := Memory.empty }
    let outcome := execute schedule recordingHash state
    outcome.status = .ok /\ outcome.state.gas.gasLeft = 0 /\
      outcome.state.memory.bytes.length = 64 := by
  native_decide

/-- Expansion is logically installed before its charge fails. -/
example :
    let state : MachineState :=
      { gas := gas 44
        stack := stackOf [zero, ofNat 33]
        memory := Memory.empty }
    let outcome := execute schedule recordingHash state
    outcome.status = .outOfGas /\ outcome.state.gas.gasLeft = 0 /\
      outcome.state.memory.bytes.length = 64 := by
  native_decide

def linearOnlyExpansionCost (memory : Memory) (offset length : UInt256) : Nat :=
  let expanded := memory.expand offset.val length.val
  (MemoryGas.words expanded - MemoryGas.words memory) * schedule.memory.linear

def mutatedLinearOnlyAffordable (state : MachineState) (offset length : UInt256) : Bool :=
  let wordCount := (checkedWords length).1
  dynamicCost schedule wordCount + linearOnlyExpansionCost state.memory offset length <=
    state.gas.gasLeft

/-- At 1-to-23 words the quadratic term contributes one gas: `36 + 67 = 103`. -/
example :
    let quadraticOffset := ofNat 704
    let oneByte := ofNat 1
    let outcome := execute schedule recordingHash (initial 103 [quadraticOffset, oneByte])
    outcome.status = .ok ∧ outcome.state.gas.gasLeft = 0 ∧
      outcome.state.memory.bytes.length = 736 ∧
      outcome.state.stack.words = [zero] := by
  native_decide

/-- Removing the quadratic term accepts 102 gas, while the reference rejects and burns it. -/
example :
    let quadraticOffset := ofNat 704
    let oneByte := ofNat 1
    let state := initial 102 [quadraticOffset, oneByte]
    let outcome := execute schedule recordingHash state
    outcome.status = .outOfGas ∧ outcome.state.gas.gasLeft = 0 ∧
      outcome.state.memory.bytes.length = 736 ∧
      linearOnlyExpansionCost state.memory quadraticOffset oneByte = 66 ∧
      mutatedLinearOnlyAffordable state quadraticOffset oneByte = true := by
  native_decide

/-- Mutation sentinel: floor division undercharges a nonempty partial word. -/
def mutatedDynamicCost (length : UInt256) : Nat :=
  schedule.base + schedule.word * (length.val / 32)

example : mutatedDynamicCost (ofNat 1) != dynamicCost schedule 1 := by native_decide

end Keccak256Vectors
end Evm
end Eip803x
