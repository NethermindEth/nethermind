-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Generated.LogOpcodeKernel
import Eip803x.Evm.Logs
import Eip803x.Evm.MemoryGas

/-!
The generated machine is independent of `Eip803x.Evm.Logs`; this file is the
only layer that imports both and states their restricted opcode-step relation.
-/

namespace LogOpcodeExtractor.Refinement

namespace G
abbrev Opcode := Eip803x.Generated.LogOpcodeKernel.Opcode
abbrev Effect := Eip803x.Generated.LogOpcodeKernel.Effect
abbrev DispatchTable := Eip803x.Generated.LogOpcodeKernel.DispatchTable
abbrev Descriptor := Eip803x.Generated.LogOpcodeKernel.Descriptor
abbrev Specialization := Eip803x.Generated.LogOpcodeKernel.Specialization
abbrev LogEntry := Eip803x.Generated.LogOpcodeKernel.LogEntry
abbrev MachineState := Eip803x.Generated.LogOpcodeKernel.MachineState
abbrev Status := Eip803x.Generated.LogOpcodeKernel.Status
abbrev Outcome := Eip803x.Generated.LogOpcodeKernel.Outcome
abbrev descriptor := Eip803x.Generated.LogOpcodeKernel.descriptor
abbrev allOpcodes := Eip803x.Generated.LogOpcodeKernel.allOpcodes
abbrev allDispatchTables := Eip803x.Generated.LogOpcodeKernel.allDispatchTables
abbrev specializations := Eip803x.Generated.LogOpcodeKernel.specializations
abbrev tracingFlag := Eip803x.Generated.LogOpcodeKernel.tracingFlag
abbrev cancelableFlag := Eip803x.Generated.LogOpcodeKernel.cancelableFlag
abbrev expectedClosedRoot := Eip803x.Generated.LogOpcodeKernel.expectedClosedRoot
abbrev expectedEffectOrder := Eip803x.Generated.LogOpcodeKernel.expectedEffectOrder
abbrev amsterdamSchedule := Eip803x.Generated.LogOpcodeKernel.amsterdamSchedule
abbrev execute := Eip803x.Generated.LogOpcodeKernel.execute
abbrev executeCore := Eip803x.Generated.LogOpcodeKernel.executeCore
abbrev popTopics := Eip803x.Generated.LogOpcodeKernel.popTopics
abbrev memoryCost := Eip803x.Generated.LogOpcodeKernel.memoryCost
abbrev memoryRangeValid := Eip803x.Generated.LogOpcodeKernel.memoryRangeValid
abbrev prepareMemory := Eip803x.Generated.LogOpcodeKernel.prepareMemory
end G

namespace R
abbrev Opcode := Eip803x.Evm.Logs.Opcode
abbrev LogEntry := Eip803x.Evm.Logs.LogEntry
abbrev MachineState := Eip803x.Evm.Logs.MachineState
abbrev Status := Eip803x.Evm.Logs.Status
abbrev Outcome := Eip803x.Evm.Logs.Outcome
abbrev amsterdamSchedule := Eip803x.Evm.Logs.Schedule.amsterdam
abbrev execute := Eip803x.Evm.Logs.execute
abbrev popTopics := Eip803x.Evm.Logs.popTopics
abbrev memoryCost := Eip803x.Evm.Logs.memoryCost
abbrev memoryRangeValid := Eip803x.Evm.Logs.memoryRangeValid
abbrev prepareMemory := Eip803x.Evm.Logs.prepareMemory
end R

namespace M
abbrev amsterdamSchedule := Eip803x.Evm.MemoryGas.Schedule.amsterdam
abbrev memoryCost := Eip803x.Evm.MemoryGas.totalCost
abbrev memoryRangeValid := Eip803x.Evm.MemoryGas.rangeValid
abbrev prepareMemory := Eip803x.Evm.MemoryGas.prepare
end M

open Eip803x
open Eip803x.Evm.MemoryStackControl

def toReferenceOpcode : G.Opcode → R.Opcode
  | .log0 => .log0
  | .log1 => .log1
  | .log2 => .log2
  | .log3 => .log3
  | .log4 => .log4

def toReferenceEntry (entry : G.LogEntry) : R.LogEntry :=
  { address := entry.address, data := entry.data, topics := entry.topics }

def fromReferenceEntry (entry : R.LogEntry) : G.LogEntry :=
  { address := entry.address, data := entry.data, topics := entry.topics }

def fromReferenceState (pc : Nat) (traceLogs : Bool) (reportedLogs : List G.LogEntry)
    (state : R.MachineState) : G.MachineState :=
  { pc
    gas := state.gas
    stack := state.stack
    memory := state.memory
    executingAccount := state.executingAccount
    logs := state.logs.map fromReferenceEntry
    traceLogs
    reportedLogs }

def toReferenceState (state : G.MachineState) : R.MachineState :=
  { gas := state.gas
    stack := state.stack
    memory := state.memory
    executingAccount := state.executingAccount
    logs := state.logs.map toReferenceEntry }

def toReferenceStatus : G.Status → Option R.Status
  | .ok => some .ok
  | .outOfGas => some .outOfGas
  | .stackUnderflow => some .stackUnderflow
  | .staticCallViolation => some .staticCallViolation
  | .extractionMismatch => none

def toReferenceOutcome (outcome : G.Outcome) : Option R.Outcome := do
  let status ← toReferenceStatus outcome.status
  pure { status, state := toReferenceState outcome.state }

def specializationRefines (specialization : G.Specialization) : Bool :=
  specialization.tracingFlag == G.tracingFlag specialization.table &&
  specialization.cancelableFlag == G.cancelableFlag specialization.table &&
  specialization.closedRoot == G.expectedClosedRoot specialization.opcode specialization.table &&
  !(specialization.closedRoot.contains "TTracingInst") &&
  !(specialization.closedRoot.contains "TCancelable") &&
  !(specialization.closedRoot.contains "TGasPolicy") &&
  !(specialization.closedRoot.contains "TOpCount")

def descriptorRefines (opcode : G.Opcode) : Bool :=
  let descriptor := G.descriptor opcode
  descriptor.effectOrder == G.expectedEffectOrder &&
  descriptor.programCounterDelta == 1 &&
  descriptor.headerStackInputs == 2 &&
  descriptor.topicCount == Eip803x.Evm.Logs.topicCount (toReferenceOpcode opcode)

theorem opcode_count : G.allOpcodes.length = 5 := by native_decide

theorem specialization_count : G.specializations.length = 20 := by native_decide

theorem opcode_bytes_are_exact :
    G.allOpcodes.map (fun opcode => (G.descriptor opcode).opcodeByte) = [0xa0, 0xa1, 0xa2, 0xa3, 0xa4] := by
  native_decide

theorem opcode_bytes_are_unique :
    (G.allOpcodes.map fun opcode => (G.descriptor opcode).opcodeByte).Nodup := by
  native_decide

theorem specialization_keys_are_exact :
    G.specializations.map (fun specialization => (specialization.opcode, specialization.table)) =
      G.allOpcodes.flatMap (fun opcode => G.allDispatchTables.map fun table => (opcode, table)) := by
  native_decide

theorem specialization_keys_are_unique :
    (G.specializations.map fun specialization => (specialization.opcode, specialization.table)).Nodup := by
  native_decide

theorem every_source_specialization_is_exact :
    G.specializations.all specializationRefines = true := by
  native_decide

theorem every_source_descriptor_refines_reference :
    G.allOpcodes.all descriptorRefines = true := by
  native_decide

theorem source_schedule_refines_reference :
    G.amsterdamSchedule.logBase = R.amsterdamSchedule.logBase ∧
    G.amsterdamSchedule.logTopic = R.amsterdamSchedule.logTopic ∧
    G.amsterdamSchedule.logDataByte = R.amsterdamSchedule.logDataByte ∧
    G.amsterdamSchedule.memoryLinear = R.amsterdamSchedule.memoryLinear ∧
    G.amsterdamSchedule.memoryQuadraticDivisor = R.amsterdamSchedule.memoryQuadraticDivisor := by
  native_decide

theorem generated_memory_cost_refines_memory_gas (words : Nat) :
    G.memoryCost G.amsterdamSchedule words = M.memoryCost M.amsterdamSchedule words := by
  rfl

theorem generated_memory_range_refines_memory_gas (offset length : UInt256) :
    G.memoryRangeValid G.amsterdamSchedule offset length = M.memoryRangeValid offset length := by
  rfl

theorem generated_prepare_memory_refines_memory_gas (memory : Memory) (offset length : UInt256) :
    G.prepareMemory G.amsterdamSchedule memory offset length =
      M.prepareMemory M.amsterdamSchedule memory offset length := by
  rfl

theorem logs_prepare_memory_refines_memory_gas (memory : Memory) (offset length : UInt256) :
    R.prepareMemory R.amsterdamSchedule memory offset length =
      M.prepareMemory M.amsterdamSchedule memory offset length := by
  rfl

theorem entry_round_trip (entry : R.LogEntry) :
    toReferenceEntry (fromReferenceEntry entry) = entry := by
  cases entry
  rfl

theorem state_round_trip (pc : Nat) (traceLogs : Bool) (reportedLogs : List G.LogEntry)
    (state : R.MachineState) :
    toReferenceState (fromReferenceState pc traceLogs reportedLogs state) = state := by
  cases state
  simp [toReferenceState, fromReferenceState, List.map_map, Function.comp_def, entry_round_trip]

def toReferenceTopicPop : Eip803x.Generated.LogOpcodeKernel.TopicPop → Eip803x.Evm.Logs.TopicPop
  | .success topics stack => .success topics stack
  | .underflow topics stack => .underflow topics stack

theorem popTopics_refines (count : Nat) (stack : Stack) (popped : List UInt256) :
    toReferenceTopicPop
        (Eip803x.Generated.LogOpcodeKernel.popTopics count stack popped) =
      Eip803x.Evm.Logs.popTopics count stack popped := by
  induction count generalizing stack popped with
  | zero => rfl
  | succ count ih =>
    cases h : stack.pop with
    | none =>
      simp [Eip803x.Generated.LogOpcodeKernel.popTopics, Eip803x.Evm.Logs.popTopics, h,
        toReferenceTopicPop]
    | some pair =>
      rcases pair with ⟨topic, tail⟩
      simpa [Eip803x.Generated.LogOpcodeKernel.popTopics, Eip803x.Evm.Logs.popTopics, h,
        toReferenceTopicPop] using ih tail (popped ++ [topic])

theorem generated_execution_guard (opcode : G.Opcode) :
    (G.descriptor opcode).effectOrder = G.expectedEffectOrder ∧
      (G.descriptor opcode).headerStackInputs = 2 := by
  cases opcode <;>
    simp [Eip803x.Generated.LogOpcodeKernel.descriptor,
      Eip803x.Generated.LogOpcodeKernel.expectedEffectOrder]

theorem generated_topic_count_refines_reference (opcode : G.Opcode) :
    (G.descriptor opcode).topicCount = Eip803x.Evm.Logs.topicCount (toReferenceOpcode opcode) := by
  cases opcode <;> rfl

theorem generated_prepare_memory_refines_logs (memory : Memory) (offset length : UInt256) :
    Eip803x.Generated.LogOpcodeKernel.prepareMemory G.amsterdamSchedule memory offset length =
      Eip803x.Evm.Logs.prepareMemory R.amsterdamSchedule memory offset length := by
  calc
    G.prepareMemory G.amsterdamSchedule memory offset length =
        M.prepareMemory M.amsterdamSchedule memory offset length :=
      generated_prepare_memory_refines_memory_gas memory offset length
    _ = R.prepareMemory R.amsterdamSchedule memory offset length :=
      (logs_prepare_memory_refines_memory_gas memory offset length).symm

theorem generated_emission_cost_refines_reference (opcode : G.Opcode) (dataSize : Nat) :
    Eip803x.Generated.LogOpcodeKernel.emissionCost G.amsterdamSchedule opcode dataSize =
      Eip803x.Evm.Logs.emissionCost R.amsterdamSchedule (toReferenceOpcode opcode) dataSize := by
  cases opcode <;> rfl

theorem generated_execute_refines_reference
    (opcode : G.Opcode) (isStatic traceLogs : Bool) (pc : Nat)
    (reportedLogs : List G.LogEntry) (state : R.MachineState) :
    toReferenceOutcome
        (G.execute G.amsterdamSchedule opcode isStatic
          (fromReferenceState pc traceLogs reportedLogs state)) =
      some (R.execute R.amsterdamSchedule (toReferenceOpcode opcode) isStatic state) := by
  simp only [Eip803x.Generated.LogOpcodeKernel.execute, generated_execution_guard]
  simp only [Eip803x.Generated.LogOpcodeKernel.executeCore, Eip803x.Evm.Logs.execute]
  cases hStatic : isStatic with
  | false =>
    simp only [Bool.false_eq_true, if_false, fromReferenceState]
    cases hHeader : state.stack.popTwo with
    | none =>
      simp [Eip803x.Generated.LogOpcodeKernel.advancePc,
        toReferenceOutcome, toReferenceStatus, toReferenceState,
        List.map_map, Function.comp_def, entry_round_trip]
    | some header =>
      rcases header with ⟨offset, length, afterHeader⟩
      simp
      rw [generated_prepare_memory_refines_logs]
      cases hPreparation : R.prepareMemory R.amsterdamSchedule state.memory offset length with
      | none =>
        simp [hPreparation, Eip803x.Generated.LogOpcodeKernel.advancePc,
          toReferenceOutcome, toReferenceStatus, toReferenceState,
          List.map_map, Function.comp_def, entry_round_trip]
      | some preparation =>
        rcases preparation with ⟨expansionCost, expandedMemory⟩
        cases hExpansion : GasMachine.chargeExecution expansionCost state.gas with
        | error fault =>
          simp [hPreparation, hExpansion,
            Eip803x.Generated.LogOpcodeKernel.advancePc,
            Eip803x.Generated.LogOpcodeKernel.exhaustExecutionGas,
            Eip803x.Evm.Logs.exhaustExecutionGas, Eip803x.Evm.Logs.withStackMemory,
            toReferenceOutcome, toReferenceStatus, toReferenceState,
            List.map_map, Function.comp_def, entry_round_trip]
        | ok afterExpansion =>
          rw [generated_emission_cost_refines_reference]
          cases hEmission : GasMachine.chargeExecution
              (Eip803x.Evm.Logs.emissionCost R.amsterdamSchedule (toReferenceOpcode opcode) length.val)
              afterExpansion with
          | error fault =>
            simp [hPreparation, hExpansion, hEmission,
              Eip803x.Generated.LogOpcodeKernel.advancePc,
              Eip803x.Generated.LogOpcodeKernel.exhaustExecutionGas,
              Eip803x.Evm.Logs.exhaustExecutionGas, Eip803x.Evm.Logs.withStackMemory,
              Eip803x.Evm.Logs.withGas,
              toReferenceOutcome, toReferenceStatus, toReferenceState,
              List.map_map, Function.comp_def, entry_round_trip]
          | ok afterEmission =>
            rw [generated_topic_count_refines_reference]
            rw [← popTopics_refines]
            cases hTopics : Eip803x.Generated.LogOpcodeKernel.popTopics
                (Eip803x.Evm.Logs.topicCount (toReferenceOpcode opcode)) afterHeader [] with
            | underflow popped partialStack =>
              simp [hPreparation, hExpansion, hEmission,
                Eip803x.Generated.LogOpcodeKernel.advancePc,
                Eip803x.Evm.Logs.withStackMemory, Eip803x.Evm.Logs.withGas,
                toReferenceOutcome, toReferenceStatus, toReferenceState,
                toReferenceTopicPop, toReferenceEntry, fromReferenceEntry,
                List.map_map, Function.comp_def]
            | success topics finalStack =>
              cases hTrace : traceLogs <;>
                simp [hPreparation, hExpansion, hEmission,
                  Eip803x.Generated.LogOpcodeKernel.advancePc,
                  Eip803x.Evm.Logs.withStackMemory, Eip803x.Evm.Logs.withGas,
                  toReferenceOutcome, toReferenceStatus, toReferenceState,
                  toReferenceTopicPop, toReferenceEntry, fromReferenceEntry,
                  List.map_map, Function.comp_def]
  | true =>
    simp [Eip803x.Generated.LogOpcodeKernel.advancePc,
      toReferenceOutcome, toReferenceStatus, toReferenceState, fromReferenceState,
      List.map_map, Function.comp_def, entry_round_trip]

theorem generated_pc_advances_once
    (opcode : G.Opcode) (isStatic : Bool) (state : G.MachineState) :
    (G.execute G.amsterdamSchedule opcode isStatic state).state.pc = state.pc + 1 := by
  cases opcode <;>
    simp [Eip803x.Generated.LogOpcodeKernel.execute, Eip803x.Generated.LogOpcodeKernel.executeCore,
      Eip803x.Generated.LogOpcodeKernel.descriptor, Eip803x.Generated.LogOpcodeKernel.expectedEffectOrder,
      Eip803x.Generated.LogOpcodeKernel.advancePc,
      Eip803x.Generated.LogOpcodeKernel.exhaustExecutionGas]
  all_goals repeat' first | split | simp_all [
    Eip803x.Generated.LogOpcodeKernel.descriptor, Eip803x.Generated.LogOpcodeKernel.expectedEffectOrder,
    Eip803x.Generated.LogOpcodeKernel.prepareMemory,
    Eip803x.Generated.LogOpcodeKernel.memoryRangeValid, Eip803x.Generated.LogOpcodeKernel.memoryCost,
    Eip803x.Generated.LogOpcodeKernel.memoryWords, Eip803x.Generated.LogOpcodeKernel.emissionCost]

theorem successful_log_tracing_preserves_journal_before_report
    (opcode : G.Opcode) (isStatic : Bool) (state : G.MachineState)
    (h : (G.execute G.amsterdamSchedule opcode isStatic state).status = .ok) :
    let outcome := G.execute G.amsterdamSchedule opcode isStatic state
    state.logs <+: outcome.state.logs ∧
      (state.traceLogs = true → state.reportedLogs <+: outcome.state.reportedLogs) := by
  cases opcode <;>
    simp_all [Eip803x.Generated.LogOpcodeKernel.execute, Eip803x.Generated.LogOpcodeKernel.executeCore,
      Eip803x.Generated.LogOpcodeKernel.descriptor, Eip803x.Generated.LogOpcodeKernel.expectedEffectOrder,
      Eip803x.Generated.LogOpcodeKernel.advancePc,
      Eip803x.Generated.LogOpcodeKernel.prepareMemory,
      Eip803x.Generated.LogOpcodeKernel.memoryRangeValid,
      Eip803x.Generated.LogOpcodeKernel.memoryCost,
      Eip803x.Generated.LogOpcodeKernel.memoryWords,
      Eip803x.Generated.LogOpcodeKernel.emissionCost,
      Eip803x.Generated.LogOpcodeKernel.exhaustExecutionGas,
      Eip803x.Generated.LogOpcodeKernel.popTopics]
  all_goals repeat' first | split | simp_all

end LogOpcodeExtractor.Refinement
