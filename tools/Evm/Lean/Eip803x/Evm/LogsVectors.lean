-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.Logs

namespace Eip803x
namespace Evm
namespace LogsVectors

open MemoryStackControl
open Logs
open Word

def stackOf (words : List UInt256) (h : words.length ≤ stackLimit := by native_decide) : Stack :=
  ⟨words, h⟩

def gas (left : Nat) : GasState :=
  { gasLeft := left
    stateReservoir := 17
    stateFromGasLeft := 3
    stateUsed := 9
    refundCounter := -4 }

def bytes : List Byte := [byte 0xaa, byte 0xbb, byte 0xcc, byte 0xdd]
def address : UInt256 := Word.ofNat 0x1234
def offset : UInt256 := Word.ofNat 1
def length : UInt256 := Word.ofNat 2
def topicA : UInt256 := Word.ofNat 0xa1
def topicB : UInt256 := Word.ofNat 0xb2
def topicC : UInt256 := Word.ofNat 0xc3
def topicD : UInt256 := Word.ofNat 0xd4
def stackTail : UInt256 := Word.ofNat 0x55
def prior : LogEntry := { address := Word.ofNat 9, data := [], topics := [] }
def schedule : Schedule := Schedule.amsterdam

def initial (left : Nat) (words : List UInt256)
    (h : words.length ≤ stackLimit := by native_decide) : MachineState :=
  { gas := gas left
    stack := stackOf words h
    memory := Memory.ofBytes bytes
    executingAccount := address
    logs := [prior] }

example : emissionCost schedule .log2 2 = 1141 := by native_decide
example : emissionCost schedule .log1 2 = 766 := by native_decide
example : emissionCost schedule .log3 2 = 1516 := by native_decide
example : emissionCost schedule .log4 2 = 1891 := by native_decide

example :
    execute schedule .log4 true (initial 0 [offset, length, topicA, topicB]) =
      { status := .staticCallViolation,
        state := initial 0 [offset, length, topicA, topicB] } := by
  exact static_preserves_state schedule .log4 (initial 0 [offset, length, topicA, topicB])

example :
    let outcome := execute schedule .log0 false (initial 999 [])
    outcome.status = .stackUnderflow ∧ outcome.state.gas.gasLeft = 999 ∧
      outcome.state.stack.words = [] ∧ outcome.state.logs = [prior] := by
  native_decide

/-- The two header operands are an atomic pop on underflow. -/
example :
    let outcome := execute schedule .log0 false (initial 999 [offset])
    outcome.status = .stackUnderflow ∧ outcome.state.stack.words = [offset] := by
  native_decide

def hugeOffset : UInt256 := Word.ofNat (maxMemorySize + 1)

example : memoryRangeValid hugeOffset Word.zero = true := by native_decide
example : memoryRangeValid (Word.ofNat (maxMemorySize - 1)) (Word.ofNat 1) = true := by
  native_decide
example : memoryRangeValid (Word.ofNat maxMemorySize) (Word.ofNat 1) = false := by
  native_decide
example : memoryRangeValid Word.zero (Word.ofNat (maxMemorySize + 1)) = false := by
  native_decide
example : memoryRangeValid (Word.ofNat (2 ^ 64)) (Word.ofNat 1) = false := by
  native_decide

/-- A non-empty invalid range reports OOG after the header pop without burning gas. -/
example :
    let outcome := execute schedule .log0 false (initial 999 [hugeOffset, length])
    outcome.status = .outOfGas ∧ outcome.state.gas.gasLeft = 999 ∧
      outcome.state.stack.words = [] ∧ outcome.state.memory.bytes = (Memory.ofBytes bytes).bytes := by
  native_decide

/-- A zero-size range accepts an otherwise invalid offset and charges only LOG base gas. -/
example :
    let outcome := execute schedule .log0 false
      (initial 375 [hugeOffset, Word.zero])
    outcome.status = .ok ∧ outcome.state.gas.gasLeft = 0 ∧
      outcome.state.logs = [prior, { address, data := [], topics := [] }] := by
  native_decide

/-- Emission OOG occurs before topic pops and preserves the post-header stack. -/
example :
    let outcome := execute schedule .log2 false
      (initial 1140 [offset, length, topicA, topicB])
    outcome.status = .outOfGas ∧ outcome.state.gas.gasLeft = 0 ∧
      outcome.state.stack.words = [topicA, topicB] ∧ outcome.state.logs = [prior] := by
  native_decide

/-- Topic underflow occurs only after the full emission charge. -/
example :
    let outcome := execute schedule .log2 false
      (initial 1141 [offset, length, topicA])
    outcome.status = .stackUnderflow ∧ outcome.state.gas.gasLeft = 0 ∧
      outcome.state.stack.words = [] ∧ outcome.state.logs = [prior] := by
  native_decide

example :
    let outcome := execute schedule .log2 false
      (initial 1141 [offset, length, topicA, topicB])
    outcome.status = .ok ∧ outcome.state.gas.gasLeft = 0 ∧
      outcome.state.stack.words = [] ∧
      outcome.state.logs =
        [prior, { address, data := [byte 0xbb, byte 0xcc], topics := [topicA, topicB] }] ∧
      outcome.state.gas.stateReservoir = 17 ∧
      outcome.state.gas.stateFromGasLeft = 3 ∧
      outcome.state.gas.stateUsed = 9 ∧
      outcome.state.gas.refundCounter = -4 := by
  native_decide

/-- LOG1 uses exactly one topic and preserves the pre-existing stack tail. -/
example :
    let outcome := execute schedule .log1 false
      (initial 766 [offset, length, topicA, stackTail])
    outcome.status = .ok ∧ outcome.state.gas.gasLeft = 0 ∧
      outcome.state.stack.words = [stackTail] ∧
      outcome.state.logs =
        [prior, { address, data := [byte 0xbb, byte 0xcc], topics := [topicA] }] := by
  native_decide

/-- LOG3 pops three topics in receipt order. -/
example :
    let outcome := execute schedule .log3 false
      (initial 1516 [offset, length, topicA, topicB, topicC])
    outcome.status = .ok ∧ outcome.state.gas.gasLeft = 0 ∧
      outcome.state.stack.words = [] ∧
      outcome.state.logs =
        [prior,
          { address, data := [byte 0xbb, byte 0xcc], topics := [topicA, topicB, topicC] }] := by
  native_decide

/-- LOG4 pops four ordered topics and leaves unrelated deeper stack values intact. -/
example :
    let outcome := execute schedule .log4 false
      (initial 1891 [offset, length, topicA, topicB, topicC, topicD, stackTail])
    outcome.status = .ok ∧ outcome.state.gas.gasLeft = 0 ∧
      outcome.state.stack.words = [stackTail] ∧
      outcome.state.logs =
        [prior,
          { address, data := [byte 0xbb, byte 0xcc],
            topics := [topicA, topicB, topicC, topicD] }] := by
  native_decide

/-- LOG4 underflow after three topics preserves its exact partially consumed stack. -/
example :
    let outcome := execute schedule .log4 false
      (initial 1891 [offset, length, topicA, topicB, topicC])
    outcome.status = .stackUnderflow ∧ outcome.state.gas.gasLeft = 0 ∧
      outcome.state.stack.words = [] ∧ outcome.state.logs = [prior] := by
  native_decide

/-- Expanding from one word to two costs `3`, before the 383-gas LOG0 emission. -/
example :
    let expandingOffset := Word.ofNat 32
    let oneByte := Word.ofNat 1
    let outcome := execute schedule .log0 false
      (initial 386 [expandingOffset, oneByte])
    outcome.status = .ok ∧ outcome.state.gas.gasLeft = 0 ∧
      outcome.state.memory.bytes.length = 64 ∧
      outcome.state.logs =
        [prior, { address, data := [zeroByte], topics := [] }] := by
  native_decide

/-- The 1-to-23-word boundary activates the quadratic term: expansion is 67 gas. -/
example :
    let quadraticOffset := Word.ofNat 704
    let oneByte := Word.ofNat 1
    (prepareMemory schedule (Memory.ofBytes bytes) quadraticOffset oneByte).map
      (fun prepared => (prepared.1, prepared.2.bytes.length)) = some (67, 736) := by
  native_decide

def mutatedLinearOnlyExpansionCost (memory : Memory) (position size : UInt256) : Nat :=
  let expanded := memory.expand position.val size.val
  (memoryWords expanded - memoryWords memory) * schedule.memoryLinear

/-- Removing the quadratic term leaves one gas at the exact production-sensitive boundary. -/
example :
    let quadraticOffset := Word.ofNat 704
    let oneByte := Word.ofNat 1
    let state := initial 450 [quadraticOffset, oneByte]
    let outcome := execute schedule .log0 false state
    outcome.status = .ok ∧ outcome.state.gas.gasLeft = 0 ∧
      mutatedLinearOnlyExpansionCost state.memory quadraticOffset oneByte = 66 ∧
      450 - mutatedLinearOnlyExpansionCost state.memory quadraticOffset oneByte -
        emissionCost schedule .log0 1 = 1 := by
  native_decide

/-- Failed expansion charging still installs the expanded logical size. -/
example :
    let expandingOffset := Word.ofNat 32
    let oneByte := Word.ofNat 1
    let outcome := execute schedule .log0 false
      (initial 2 [expandingOffset, oneByte])
    outcome.status = .outOfGas ∧ outcome.state.gas.gasLeft = 0 ∧
      outcome.state.memory.bytes.length = 64 ∧ outcome.state.logs = [prior] := by
  native_decide

/-- Mutation sentinel: charging emission before expansion gives a different OOG memory size. -/
def mutatedEmissionFirst (state : MachineState) : Outcome :=
  match state.stack.popTwo with
  | none => { status := .stackUnderflow, state }
  | some (position, size, tail) =>
    match GasMachine.chargeExecution (emissionCost schedule .log0 size.val) state.gas with
    | .error _ => { status := .outOfGas, state := Logs.exhaustExecutionGas { state with stack := tail } }
    | .ok charged =>
      { status := .ok
        state := { state with
          gas := charged
          stack := tail
          memory := state.memory.expand position.val size.val } }

example :
    let expandingOffset := Word.ofNat 32
    let oneByte := Word.ofNat 1
    let state := initial 2 [expandingOffset, oneByte]
    (execute schedule .log0 false state).state.memory.bytes.length ≠
      (mutatedEmissionFirst state).state.memory.bytes.length := by
  native_decide

end LogsVectors
end Evm
end Eip803x
