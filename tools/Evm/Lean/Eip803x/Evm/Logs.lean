-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Gas
import Eip803x.Evm.MemoryStackControl
import Lean.Elab.Tactic.Omega

namespace Eip803x
namespace Evm
namespace Logs

open GasMachine
open MemoryStackControl

/-- The fixed LOG-family and memory-expansion costs used by this reference slice. -/
structure Schedule where
  logBase : Nat
  logTopic : Nat
  logDataByte : Nat
  memoryLinear : Nat
  memoryQuadraticDivisor : Nat
  memoryQuadraticDivisorPositive : 0 < memoryQuadraticDivisor
  deriving DecidableEq, Repr

namespace Schedule

def amsterdam : Schedule where
  logBase := 375
  logTopic := 375
  logDataByte := 8
  memoryLinear := 3
  memoryQuadraticDivisor := 512
  memoryQuadraticDivisorPositive := by decide

end Schedule

inductive Opcode where
  | log0 | log1 | log2 | log3 | log4
  deriving DecidableEq, Repr

def topicCount : Opcode → Nat
  | .log0 => 0
  | .log1 => 1
  | .log2 => 2
  | .log3 => 3
  | .log4 => 4

structure LogEntry where
  address : UInt256
  data : List Byte
  topics : List UInt256
  deriving DecidableEq, Repr

structure MachineState where
  gas : GasState
  stack : Stack
  memory : Memory
  executingAccount : UInt256
  logs : List LogEntry
  deriving Repr

inductive Status where
  | ok
  | outOfGas
  | stackUnderflow
  | staticCallViolation
  deriving DecidableEq, Repr

structure Outcome where
  status : Status
  state : MachineState
  deriving Repr

/-- Production caps a non-empty memory range at `int.MaxValue - 31`. -/
def maxMemorySize : Nat := 2147483616

def memoryWords (memory : Memory) : Nat := memory.bytes.length / 32

def memoryCost (schedule : Schedule) (words : Nat) : Nat :=
  words * schedule.memoryLinear +
    words * words / schedule.memoryQuadraticDivisor

def memoryRangeValid (offset length : UInt256) : Bool :=
  length.val = 0 ||
    (length.val <= maxMemorySize && offset.val <= maxMemorySize - length.val)

/--
Computes and installs the logical memory expansion before charging it, matching
`EvmPooledMemory.CalculateMemoryCost`. A zero length ignores an arbitrarily large offset.
-/
def prepareMemory (schedule : Schedule) (memory : Memory) (offset length : UInt256) :
    Option (Nat × Memory) :=
  if length.val = 0 then
    some (0, memory)
  else if memoryRangeValid offset length then
    let expanded := memory.expand offset.val length.val
    let oldWords := memoryWords memory
    let newWords := memoryWords expanded
    some (memoryCost schedule newWords - memoryCost schedule oldWords, expanded)
  else
    none

def emissionCost (schedule : Schedule) (opcode : Opcode) (dataSize : Nat) : Nat :=
  schedule.logBase + topicCount opcode * schedule.logTopic + dataSize * schedule.logDataByte

def exhaustExecutionGas (state : MachineState) : MachineState :=
  { state with gas := { state.gas with gasLeft := 0 } }

def withGas (state : MachineState) (gas : GasState) : MachineState :=
  { state with gas }

def withStackMemory (state : MachineState) (stack : Stack) (memory : Memory) : MachineState :=
  { state with stack, memory }

inductive TopicPop where
  | success (topics : List UInt256) (stack : Stack)
  | underflow (popped : List UInt256) (stack : Stack)
  deriving Repr

/-- Pops topics in their receipt order and preserves the exact partial stack on underflow. -/
def popTopics : Nat → Stack → List UInt256 → TopicPop
  | 0, stack, popped => .success popped stack
  | count + 1, stack, popped =>
    match stack.pop with
    | none => .underflow popped stack
    | some (topic, tail) => popTopics count tail (popped ++ [topic])

/--
Handwritten reference for the shared production handler of LOG0 through LOG4.
The offset/length pair is popped atomically. Memory and both gas charges precede
topic pops; only a complete topic list appends a log.
-/
def execute (schedule : Schedule) (opcode : Opcode) (isStatic : Bool)
    (state : MachineState) : Outcome :=
  if isStatic then
    { status := .staticCallViolation, state }
  else
    match state.stack.popTwo with
    | none => { status := .stackUnderflow, state }
    | some (offset, length, afterHeader) =>
      match prepareMemory schedule state.memory offset length with
      | none =>
        { status := .outOfGas, state := { state with stack := afterHeader } }
      | some (expansionCost, expandedMemory) =>
        let prepared := withStackMemory state afterHeader expandedMemory
        match chargeExecution expansionCost state.gas with
        | .error _ =>
          { status := .outOfGas, state := exhaustExecutionGas prepared }
        | .ok afterExpansion =>
          let expandedAndCharged := withGas prepared afterExpansion
          match chargeExecution (emissionCost schedule opcode length.val) afterExpansion with
          | .error _ =>
            { status := .outOfGas, state := exhaustExecutionGas expandedAndCharged }
          | .ok afterEmission =>
            let charged := withGas expandedAndCharged afterEmission
            let data := MemoryStackControl.readRange expandedMemory.bytes offset.val length.val
            match popTopics (topicCount opcode) afterHeader [] with
            | .underflow _ partialStack =>
              { status := .stackUnderflow, state := { charged with stack := partialStack } }
            | .success topics finalStack =>
              let entry : LogEntry := { address := state.executingAccount, data, topics }
              { status := .ok
                state := { charged with stack := finalStack, logs := state.logs ++ [entry] } }

theorem static_preserves_state (schedule : Schedule) (opcode : Opcode) (state : MachineState) :
    execute schedule opcode true state = { status := .staticCallViolation, state } := by
  rfl

theorem header_underflow_precedes_memory_and_gas (schedule : Schedule) (opcode : Opcode)
    (state : MachineState) (h : state.stack.popTwo = none) :
    execute schedule opcode false state = { status := .stackUnderflow, state } := by
  simp [execute, h]

theorem invalid_memory_range_keeps_gas_and_memory (schedule : Schedule) (opcode : Opcode)
    (state : MachineState) (offset length : UInt256) (tail : Stack)
    (hPop : state.stack.popTwo = some (offset, length, tail))
    (hInvalid : length.val ≠ 0)
    (hRange : memoryRangeValid offset length = false) :
    let outcome := execute schedule opcode false state
    outcome.status = .outOfGas ∧
      outcome.state.stack.words = tail.words ∧
      outcome.state.gas = state.gas ∧
      outcome.state.memory.bytes = state.memory.bytes ∧
      outcome.state.logs = state.logs := by
  simp [execute, hPop, prepareMemory, hInvalid, hRange]

theorem expansion_oog_installs_size_and_burns_execution_gas
    (schedule : Schedule) (opcode : Opcode) (state : MachineState)
    (offset length : UInt256) (tail : Stack) (cost : Nat) (expanded : Memory)
    (hPop : state.stack.popTwo = some (offset, length, tail))
    (hPrepare : prepareMemory schedule state.memory offset length = some (cost, expanded))
    (hGas : state.gas.gasLeft < cost) :
    let outcome := execute schedule opcode false state
    outcome.status = .outOfGas ∧
      outcome.state.gas.gasLeft = 0 ∧
      outcome.state.memory.bytes = expanded.bytes ∧
      outcome.state.stack.words = tail.words ∧
      outcome.state.logs = state.logs := by
  simp [execute, hPop, hPrepare, chargeExecution, Nat.not_le_of_lt hGas,
    withStackMemory, exhaustExecutionGas]

theorem emission_oog_precedes_topic_pops
    (schedule : Schedule) (opcode : Opcode) (state : MachineState)
    (offset length : UInt256) (tail : Stack) (cost : Nat) (expanded : Memory)
    (hPop : state.stack.popTwo = some (offset, length, tail))
    (hPrepare : prepareMemory schedule state.memory offset length = some (cost, expanded))
    (hExpansion : cost ≤ state.gas.gasLeft)
    (hEmission : state.gas.gasLeft - cost < emissionCost schedule opcode length.val) :
    let outcome := execute schedule opcode false state
    outcome.status = .outOfGas ∧
      outcome.state.gas.gasLeft = 0 ∧
      outcome.state.stack.words = tail.words ∧
      outcome.state.memory.bytes = expanded.bytes ∧
      outcome.state.logs = state.logs := by
  simp [execute, hPop, hPrepare, chargeExecution, hExpansion,
    Nat.not_le_of_lt hEmission, withStackMemory, withGas, exhaustExecutionGas]

theorem topic_underflow_happens_after_both_charges
    (schedule : Schedule) (opcode : Opcode) (state : MachineState)
    (offset length : UInt256) (tail partialStack : Stack) (popped : List UInt256)
    (cost : Nat) (expanded : Memory)
    (hPop : state.stack.popTwo = some (offset, length, tail))
    (hPrepare : prepareMemory schedule state.memory offset length = some (cost, expanded))
    (hExpansion : cost ≤ state.gas.gasLeft)
    (hEmission : emissionCost schedule opcode length.val ≤ state.gas.gasLeft - cost)
    (hTopics : popTopics (topicCount opcode) tail [] = .underflow popped partialStack) :
    let outcome := execute schedule opcode false state
    outcome.status = .stackUnderflow ∧
      outcome.state.gas.gasLeft =
        state.gas.gasLeft - cost - emissionCost schedule opcode length.val ∧
      outcome.state.stack.words = partialStack.words ∧
      outcome.state.memory.bytes = expanded.bytes ∧
      outcome.state.logs = state.logs := by
  simp [execute, hPop, hPrepare, chargeExecution, hExpansion, hEmission, hTopics,
    withStackMemory, withGas]

theorem success_appends_exact_log
    (schedule : Schedule) (opcode : Opcode) (state : MachineState)
    (offset length : UInt256) (tail finalStack : Stack) (topics : List UInt256)
    (cost : Nat) (expanded : Memory)
    (hPop : state.stack.popTwo = some (offset, length, tail))
    (hPrepare : prepareMemory schedule state.memory offset length = some (cost, expanded))
    (hExpansion : cost ≤ state.gas.gasLeft)
    (hEmission : emissionCost schedule opcode length.val ≤ state.gas.gasLeft - cost)
    (hTopics : popTopics (topicCount opcode) tail [] = .success topics finalStack) :
    let outcome := execute schedule opcode false state
    outcome.status = .ok ∧
      outcome.state.gas.gasLeft =
        state.gas.gasLeft - cost - emissionCost schedule opcode length.val ∧
      outcome.state.stack.words = finalStack.words ∧
      outcome.state.memory.bytes = expanded.bytes ∧
      outcome.state.logs = state.logs ++
        [{ address := state.executingAccount
           data := MemoryStackControl.readRange expanded.bytes offset.val length.val
           topics }] := by
  simp [execute, hPop, hPrepare, chargeExecution, hExpansion, hEmission, hTopics,
    withStackMemory, withGas]

theorem two_execution_charges_preserve_other_gas_dimensions
    (first second : Nat) (before afterFirst afterSecond : GasState)
    (hFirst : chargeExecution first before = .ok afterFirst)
    (hSecond : chargeExecution second afterFirst = .ok afterSecond) :
    afterSecond.stateReservoir = before.stateReservoir ∧
      afterSecond.stateFromGasLeft = before.stateFromGasLeft ∧
      afterSecond.stateUsed = before.stateUsed ∧
      afterSecond.refundCounter = before.refundCounter := by
  rcases chargeExecution_changes_only_gasLeft hFirst with
    ⟨hReservoirFirst, hSpillFirst, hUsedFirst, hRefundFirst⟩
  rcases chargeExecution_changes_only_gasLeft hSecond with
    ⟨hReservoirSecond, hSpillSecond, hUsedSecond, hRefundSecond⟩
  exact ⟨hReservoirSecond.trans hReservoirFirst,
    hSpillSecond.trans hSpillFirst,
    hUsedSecond.trans hUsedFirst,
    hRefundSecond.trans hRefundFirst⟩

end Logs
end Evm
end Eip803x
