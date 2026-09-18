-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Gas
import Eip803x.Evm.MemoryGas
import Lean.Elab.Tactic.Omega

namespace Eip803x
namespace Evm
namespace Keccak256

open GasMachine
open MemoryStackControl

/-- The hash primitive is explicit in the trust boundary of this opcode reference. -/
abbrev HashOracle := List Byte -> UInt256

structure Schedule where
  base : Nat
  word : Nat
  memory : MemoryGas.Schedule
  deriving DecidableEq, Repr

namespace Schedule

def amsterdam : Schedule :=
  { base := 30, word := 6, memory := MemoryGas.Schedule.amsterdam }

end Schedule

structure MachineState where
  gas : GasState
  stack : Stack
  memory : Memory
  deriving Repr

inductive Status where
  | ok
  | outOfGas
  | stackUnderflow
  | stackOverflow
  deriving DecidableEq, Repr

structure Outcome where
  status : Status
  state : MachineState
  deriving Repr

def maxUInt64 : Nat := 18446744073709551615

def maxUInt32 : Nat := 4294967295

/-- Exact result/flag shape of production's `EvmCalculations.Div32Ceiling`. -/
def checkedWords (length : UInt256) : Nat × Bool :=
  if length.val > maxUInt64 then
    (0, true)
  else
    let result := (length.val + 31) / 32
    if result > maxUInt32 then (0, true) else (result, false)

def dynamicCost (schedule : Schedule) (words : Nat) : Nat :=
  schedule.base + schedule.word * words

def exhaustExecutionGas (state : MachineState) : MachineState :=
  { state with gas := { state.gas with gasLeft := 0 } }

theorem popTwo_tail_has_room (stack tail : Stack) (first second : UInt256)
    (hPop : stack.popTwo = some (first, second, tail)) :
    tail.words.length < stackLimit := by
  rcases stack with ⟨words, hBound⟩
  cases words with
  | nil => simp [Stack.popTwo, Stack.pop] at hPop
  | cons head rest =>
    cases rest with
    | nil => simp [Stack.popTwo, Stack.pop] at hPop
    | cons next remaining =>
      simp [Stack.popTwo, Stack.pop] at hPop
      rcases hPop with ⟨rfl, rfl, rfl⟩
      simp only [List.length_cons] at hBound
      change remaining.length < stackLimit
      omega

theorem push_after_popTwo_succeeds (stack tail : Stack) (first second value : UInt256)
    (hPop : stack.popTwo = some (first, second, tail)) :
    ∃ pushed, tail.push value = some pushed := by
  have hRoom := popTwo_tail_has_room stack tail first second hPop
  simp [Stack.push, hRoom]

/--
Handwritten transition for production's `InstructionKeccak256`. The size-derived
charge precedes its overflow flag, then memory expansion is charged and the exact
zero-extended memory slice is passed to the explicit hash oracle.
-/
def execute (schedule : Schedule) (hash : HashOracle) (state : MachineState) : Outcome :=
  match state.stack.popTwo with
  | none => { status := .stackUnderflow, state }
  | some (offset, length, tail) =>
    let (wordCount, invalidWordCount) := checkedWords length
    match chargeExecution (dynamicCost schedule wordCount) state.gas with
    | .error _ =>
      { status := .outOfGas,
        state := exhaustExecutionGas { state with stack := tail } }
    | .ok afterDynamic =>
      let dynamicallyCharged := { state with gas := afterDynamic, stack := tail }
      if invalidWordCount then
        { status := .outOfGas, state := dynamicallyCharged }
      else
        match MemoryGas.prepare schedule.memory state.memory offset length with
        | none => { status := .outOfGas, state := dynamicallyCharged }
        | some (expansionCost, expanded) =>
          let prepared := { dynamicallyCharged with memory := expanded }
          match chargeExecution expansionCost afterDynamic with
          | .error _ =>
            { status := .outOfGas, state := exhaustExecutionGas prepared }
          | .ok afterExpansion =>
            let input := MemoryStackControl.readRange expanded.bytes offset.val length.val
            match tail.push (hash input) with
            | none =>
              { status := .stackOverflow,
                state := { prepared with gas := afterExpansion } }
            | some finalStack =>
              { status := .ok,
                state := { prepared with gas := afterExpansion, stack := finalStack } }

theorem header_underflow_precedes_charges (schedule : Schedule) (hash : HashOracle)
    (state : MachineState) (h : state.stack.popTwo = none) :
    execute schedule hash state = { status := .stackUnderflow, state } := by
  simp [execute, h]

theorem invalid_word_count_is_checked_after_base_charge
    (schedule : Schedule) (hash : HashOracle) (state : MachineState)
    (offset length : UInt256) (tail : Stack)
    (hPop : state.stack.popTwo = some (offset, length, tail))
    (hWords : checkedWords length = (0, true))
    (hGas : schedule.base <= state.gas.gasLeft) :
    let outcome := execute schedule hash state
    outcome.status = .outOfGas /\
      outcome.state.gas.gasLeft = state.gas.gasLeft - schedule.base /\
      outcome.state.stack.words = tail.words /\
      outcome.state.memory.bytes = state.memory.bytes := by
  simp [execute, hPop, hWords, dynamicCost, chargeExecution, hGas]

theorem memory_range_failure_preserves_post_dynamic_gas
    (schedule : Schedule) (hash : HashOracle) (state : MachineState)
    (offset length : UInt256) (tail : Stack) (wordCount : Nat)
    (hPop : state.stack.popTwo = some (offset, length, tail))
    (hWords : checkedWords length = (wordCount, false))
    (hGas : dynamicCost schedule wordCount <= state.gas.gasLeft)
    (hMemory : MemoryGas.prepare schedule.memory state.memory offset length = none) :
    let outcome := execute schedule hash state
    outcome.status = .outOfGas /\
      outcome.state.gas.gasLeft = state.gas.gasLeft - dynamicCost schedule wordCount /\
      outcome.state.stack.words = tail.words /\
      outcome.state.memory.bytes = state.memory.bytes := by
  simp [execute, hPop, hWords, chargeExecution, hGas, hMemory]

theorem success_hashes_exact_memory_slice
    (schedule : Schedule) (hash : HashOracle) (state : MachineState)
    (offset length : UInt256) (tail finalStack : Stack)
    (wordCount expansionCost : Nat) (expandedMemory : Memory)
    (hPop : state.stack.popTwo = some (offset, length, tail))
    (hWords : checkedWords length = (wordCount, false))
    (hDynamic : dynamicCost schedule wordCount <= state.gas.gasLeft)
    (hPrepare : MemoryGas.prepare schedule.memory state.memory offset length =
      some (expansionCost, expandedMemory))
    (hExpansion : expansionCost <= state.gas.gasLeft - dynamicCost schedule wordCount)
    (hPush : tail.push
      (hash (MemoryStackControl.readRange expandedMemory.bytes offset.val length.val)) =
        some finalStack) :
    let outcome := execute schedule hash state
    outcome.status = .ok /\
      outcome.state.gas.gasLeft =
        state.gas.gasLeft - dynamicCost schedule wordCount - expansionCost /\
      outcome.state.stack.words = finalStack.words /\
      outcome.state.memory.bytes = expandedMemory.bytes := by
  simp [execute, hPop, hWords, chargeExecution, hDynamic, hPrepare, hExpansion, hPush]

end Keccak256
end Evm
end Eip803x
