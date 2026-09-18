-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Keccak256OpcodeExtractor.Generated.Keccak256OpcodeKernel
import Eip803x.Evm.Keccak256

namespace Eip803x.Generated.Keccak256OpcodeRefinement

open Eip803x.Evm.MemoryStackControl

def eraseState (state : Keccak256OpcodeKernel.MachineState) : Eip803x.Evm.Keccak256.MachineState :=
  { gas := state.gas, stack := state.stack, memory := state.memory }

def eraseStatus : Keccak256OpcodeKernel.Status -> Eip803x.Evm.Keccak256.Status
  | .ok => .ok
  | .outOfGas => .outOfGas
  | .stackUnderflow => .stackUnderflow
  | .stackOverflow => .stackOverflow
  | .extractionMismatch => .outOfGas

def eraseOutcome (outcome : Keccak256OpcodeKernel.Outcome) : Eip803x.Evm.Keccak256.Outcome :=
  { status := eraseStatus outcome.status, state := eraseState outcome.state }

theorem noTrace_root_admitted :
    Keccak256OpcodeKernel.specializationAdmitted .noTrace = true := by native_decide

theorem noTraceCancelable_root_admitted :
    Keccak256OpcodeKernel.specializationAdmitted .noTraceCancelable = true := by native_decide

theorem traced_root_admitted :
    Keccak256OpcodeKernel.specializationAdmitted .traced = true := by native_decide

theorem tracedCancelable_root_admitted :
    Keccak256OpcodeKernel.specializationAdmitted .tracedCancelable = true := by native_decide

theorem descriptor_admitted : Keccak256OpcodeKernel.descriptorAdmitted = true := by native_decide

@[simp] theorem checkedWords_refines (length : UInt256) :
    Keccak256OpcodeKernel.checkedWords Keccak256OpcodeKernel.amsterdamSchedule length =
      Eip803x.Evm.Keccak256.checkedWords length := by
  rfl

@[simp] theorem dynamicCost_refines (words : Nat) :
    Keccak256OpcodeKernel.dynamicCost Keccak256OpcodeKernel.amsterdamSchedule words =
      Eip803x.Evm.Keccak256.dynamicCost Eip803x.Evm.Keccak256.Schedule.amsterdam words := by
  rfl

def generatedMemorySchedule : Eip803x.Evm.MemoryGas.Schedule :=
  { linear := Keccak256OpcodeKernel.amsterdamSchedule.memoryLinear
    quadraticDivisor := Keccak256OpcodeKernel.amsterdamSchedule.memoryQuadraticDivisor
    quadraticDivisorPositive := Keccak256OpcodeKernel.amsterdamSchedule.memoryQuadraticDivisorPositive }

@[simp] theorem memorySchedule_refines :
    generatedMemorySchedule = Eip803x.Evm.Keccak256.Schedule.amsterdam.memory := by
  rfl

@[simp] theorem prepareMemory_refines (memory : Memory) (offset length : UInt256) :
    Keccak256OpcodeKernel.prepareMemory Keccak256OpcodeKernel.amsterdamSchedule memory offset length =
      Eip803x.Evm.MemoryGas.prepare Eip803x.Evm.Keccak256.Schedule.amsterdam.memory memory offset length := by
  rfl

@[simp] theorem erase_beginInstruction (table : Keccak256OpcodeKernel.DispatchTable)
    (state : Keccak256OpcodeKernel.MachineState) :
    eraseState (Keccak256OpcodeKernel.beginInstruction table state) = eraseState state := by
  cases table <;> rfl

@[simp] theorem erase_tracePush (table : Keccak256OpcodeKernel.DispatchTable) (value : UInt256)
    (state : Keccak256OpcodeKernel.MachineState) :
    eraseState (Keccak256OpcodeKernel.tracePush table value state) = eraseState state := by
  cases table <;> rfl

@[simp] theorem erase_finishInstruction (table : Keccak256OpcodeKernel.DispatchTable)
    (state : Keccak256OpcodeKernel.MachineState) :
    eraseState (Keccak256OpcodeKernel.finishInstruction table state) = eraseState state := by
  cases table <;> rfl

theorem trace_start_uses_preincrement_state (state : Keccak256OpcodeKernel.MachineState) :
    (Keccak256OpcodeKernel.beginInstruction .traced state).trace =
        state.trace ++ [.start Keccak256OpcodeKernel.descriptor.instruction state.pc state.gas.gasLeft] /\
      (Keccak256OpcodeKernel.beginInstruction .traced state).pc = state.pc + 1 /\
      (Keccak256OpcodeKernel.beginInstruction .traced state).opcodeCount = state.opcodeCount + 1 := by
  simp [Keccak256OpcodeKernel.beginInstruction, Keccak256OpcodeKernel.isTracing,
    Keccak256OpcodeKernel.descriptor]

theorem trace_push_reports_hash (state : Keccak256OpcodeKernel.MachineState) (value : UInt256) :
    (Keccak256OpcodeKernel.tracePush .traced value state).trace = state.trace ++ [.push value] := by
  rfl

theorem trace_end_reports_postcharge_gas (state : Keccak256OpcodeKernel.MachineState) :
    (Keccak256OpcodeKernel.finishInstruction .traced state).trace = state.trace ++ [.finish state.gas.gasLeft] := by
  rfl

theorem executeCore_refines (hash : Keccak256OpcodeKernel.HashOracle) (table : Keccak256OpcodeKernel.DispatchTable)
    (state : Keccak256OpcodeKernel.MachineState) :
    eraseOutcome (Keccak256OpcodeKernel.executeCore Keccak256OpcodeKernel.amsterdamSchedule hash table state) =
      Eip803x.Evm.Keccak256.execute Eip803x.Evm.Keccak256.Schedule.amsterdam hash (eraseState state) := by
  cases table <;> cases hPop : state.stack.popTwo <;>
    simp [Keccak256OpcodeKernel.executeCore, Eip803x.Evm.Keccak256.execute, hPop,
      eraseOutcome, eraseStatus, eraseState, checkedWords_refines, dynamicCost_refines,
      prepareMemory_refines, Keccak256OpcodeKernel.exhaustExecutionGas,
      Eip803x.Evm.Keccak256.exhaustExecutionGas, Keccak256OpcodeKernel.beginInstruction,
      Keccak256OpcodeKernel.isTracing, Keccak256OpcodeKernel.descriptor,
      Keccak256OpcodeKernel.tracePush, Keccak256OpcodeKernel.finishInstruction]
  all_goals
    rename_i popped
    rcases popped with ⟨offset, length, tail⟩
    cases hDynamic : GasMachine.chargeExecution
        (Eip803x.Evm.Keccak256.dynamicCost Eip803x.Evm.Keccak256.Schedule.amsterdam
          (Eip803x.Evm.Keccak256.checkedWords length).fst) state.gas with
    | error error =>
      simp_all
    | ok afterDynamic =>
      cases hInvalid : (Eip803x.Evm.Keccak256.checkedWords length).snd with
      | true =>
        simp_all
      | false =>
        cases hMemory : Eip803x.Evm.MemoryGas.prepare
            Eip803x.Evm.Keccak256.Schedule.amsterdam.memory state.memory offset length with
        | none =>
          simp_all
        | some prepared =>
          rcases prepared with ⟨expansionCost, expanded⟩
          cases hExpansion : GasMachine.chargeExecution expansionCost afterDynamic with
          | error error =>
            simp_all
          | ok afterExpansion =>
            cases hPush : tail.push
                (hash (readRange expanded.bytes offset.val length.val)) with
            | none =>
              simp_all
            | some finalStack =>
              simp_all

theorem execute_refines (hash : Keccak256OpcodeKernel.HashOracle) (table : Keccak256OpcodeKernel.DispatchTable)
    (state : Keccak256OpcodeKernel.MachineState) :
    eraseOutcome (Keccak256OpcodeKernel.execute Keccak256OpcodeKernel.amsterdamSchedule hash table state) =
      Eip803x.Evm.Keccak256.execute Eip803x.Evm.Keccak256.Schedule.amsterdam hash (eraseState state) := by
  have hRoot : Keccak256OpcodeKernel.specializationAdmitted table = true := by
    cases table <;> native_decide
  simp [Keccak256OpcodeKernel.execute, descriptor_admitted, hRoot, executeCore_refines]

end Eip803x.Generated.Keccak256OpcodeRefinement
