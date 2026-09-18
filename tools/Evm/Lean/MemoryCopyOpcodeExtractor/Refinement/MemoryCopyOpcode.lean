-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import MemoryCopyOpcodeExtractor.Generated.MemoryCopyOpcodeKernel
import MemoryCopyOpcodeExtractor.Specification.MemoryCopyExecution

namespace Eip803x.Generated.MemoryCopyOpcodeRefinement

open Eip803x.Generated.MemoryCopyOpcodeKernel

theorem descriptor_admitted : descriptorAdmitted = true := by native_decide

theorem every_closed_root_admitted (opcode : Opcode) (table : DispatchTable) :
    specializationAdmitted opcode table = true := by
  cases opcode <;> cases table <;> native_decide

theorem operation_gate_refines (activation : Activation) (opcode : Opcode) :
    operationActive activation (descriptor opcode).semantic.activation =
      Eip803x.Evm.MemoryCopyExecution.active activation opcode := by
  cases opcode <;> rfl

theorem dispatch_enter_refines (table : DispatchTable) (opcode : Opcode) (state : MachineState) :
    dispatchEnter table opcode state =
      Eip803x.Evm.MemoryCopyExecution.beginInstruction table opcode state := by
  rfl

theorem exhaust_refines (state : MachineState) :
    exhaust state = Eip803x.Evm.MemoryCopyExecution.exhaustExecutionGas state := by
  rfl

theorem memory_cost_refines (schedule : Schedule) (words : Nat) :
    memoryCost schedule words = Eip803x.Evm.MemoryCopyExecution.memoryCost schedule words := by
  rfl

theorem range_allowed_refines (schedule : Schedule) (offset length : UInt256) :
    rangeAllowed schedule offset length =
      Eip803x.Evm.MemoryCopyExecution.memoryRangeValid schedule offset length := by
  rfl

theorem checked_words_refines (schedule : Schedule) (length : UInt256) :
    checkedWords schedule length = Eip803x.Evm.MemoryCopyExecution.checkedWords schedule length := by
  rfl

theorem report_memory_refines (rule : TraceRule) (table : DispatchTable) (offset : Nat)
    (bytes : List Eip803x.Evm.MemoryStackControl.Byte) (state : MachineState)
    (hRule : rule = .parityLoad ∨ rule = .destination ∨ rule = .sourceThenDestination) :
    reportMemory rule table offset bytes state =
      Eip803x.Evm.MemoryCopyExecution.traceMemory table offset bytes state := by
  rcases hRule with rfl | rfl | rfl <;> cases table <;> rfl

theorem report_push_parity_refines (table : DispatchTable) (value : UInt256)
    (state : MachineState) :
    reportPush .parityLoad table value state =
      Eip803x.Evm.MemoryCopyExecution.tracePush table
        (Eip803x.Evm.MemoryStackControl.wordToBytes value) state := by
  cases table <;> rfl

theorem report_push_uint64_refines (table : DispatchTable) (value : UInt256)
    (state : MachineState) :
    reportPush .push table value state =
      Eip803x.Evm.MemoryCopyExecution.tracePush table
        ((Eip803x.Evm.MemoryStackControl.wordToBytes value).drop 24) state := by
  cases table <;> rfl

theorem dispatch_finish_refines (table : DispatchTable) (state : MachineState) :
    dispatchFinish table state =
      Eip803x.Evm.MemoryCopyExecution.finishInstruction table state := by
  rfl

theorem close_failure_trace_refines (table : DispatchTable) (result : Outcome) :
    closeFailureTrace table result =
      Eip803x.Evm.MemoryCopyExecution.closeFailureTrace table result := by
  cases table <;> cases result.status <;> rfl

theorem fault_pc_refines (result : Outcome) :
    faultPc result = Eip803x.Evm.MemoryCopyExecution.faultPc result := by
  cases result.status <;> rfl

theorem execute_semantic_refines (schedule : Schedule) (table : DispatchTable)
    (opcode : Opcode) (entered : MachineState) :
    executeSemantic schedule table (descriptor opcode).semantic entered =
      Eip803x.Evm.MemoryCopyExecution.executeActive schedule table opcode entered := by
  cases opcode <;> cases table <;>
    simp [descriptor, descriptors, executeSemantic, runLoad, runStoreWord, runStoreByte,
      runPush, runZeroExtendedCopy, runReturnDataCopy, runMemoryCopy, fixedCost, copyCharge,
      sourceBytes, debitExecution, expandAndCharge, prepareExpansion, rangeAllowed, memoryCost,
      memoryWords, checkedWords, returnRangeAllowed, replaceTop, outcome, exhaust, reportMemory,
      reportPush, dispatchFinish, Eip803x.Evm.MemoryCopyExecution.executeActive,
      Eip803x.Evm.MemoryCopyExecution.executeMload,
      Eip803x.Evm.MemoryCopyExecution.executeMstore,
      Eip803x.Evm.MemoryCopyExecution.executeMstore8,
      Eip803x.Evm.MemoryCopyExecution.pushFixed,
      Eip803x.Evm.MemoryCopyExecution.copyFrom,
      Eip803x.Evm.MemoryCopyExecution.executeReturnDataCopy,
      Eip803x.Evm.MemoryCopyExecution.executeMcopy,
      Eip803x.Evm.MemoryCopyExecution.debit,
      Eip803x.Evm.MemoryCopyExecution.chargePreparedMemory,
      Eip803x.Evm.MemoryCopyExecution.prepareMemory,
      Eip803x.Evm.MemoryCopyExecution.memoryRangeValid,
      Eip803x.Evm.MemoryCopyExecution.memoryCost,
      Eip803x.Evm.MemoryCopyExecution.memoryWords,
      Eip803x.Evm.MemoryCopyExecution.checkedWords,
      Eip803x.Evm.MemoryCopyExecution.copyCost,
      Eip803x.Evm.MemoryCopyExecution.returnDataRangeValid,
      Eip803x.Evm.MemoryCopyExecution.replaceTop,
      Eip803x.Evm.MemoryCopyExecution.withStatus,
      Eip803x.Evm.MemoryCopyExecution.exhaustExecutionGas,
      Eip803x.Evm.MemoryCopyExecution.traceMemory,
      Eip803x.Evm.MemoryCopyExecution.tracePush,
      Eip803x.Evm.MemoryCopyExecution.finishInstruction,
      Eip803x.Evm.MemoryCopyExecution.DispatchTable.tracing] <;> rfl

theorem generated_body_refines (schedule : Schedule) (activation : Activation)
    (table : DispatchTable) (opcode : Opcode) (state : MachineState) :
    executeCore schedule activation table opcode state =
      Eip803x.Evm.MemoryCopyExecution.execute schedule activation table opcode state := by
  simp [executeCore, Eip803x.Evm.MemoryCopyExecution.execute, dispatch_enter_refines,
    operation_gate_refines, execute_semantic_refines, outcome,
    Eip803x.Evm.MemoryCopyExecution.withStatus]

theorem extracted_transition_refines (schedule : Schedule) (activation : Activation)
    (table : DispatchTable) (opcode : Opcode) (state : MachineState) :
    executeExtracted schedule activation table opcode state =
      Eip803x.Evm.MemoryCopyExecution.execute schedule activation table opcode state := by
  simp [executeExtracted, descriptor_admitted, every_closed_root_admitted, generated_body_refines]

theorem amsterdam_schedule_refines :
    amsterdamSchedule = Eip803x.Evm.MemoryCopyExecution.Schedule.amsterdam := by
  rfl

theorem amsterdam_activation_refines :
    amsterdamActivation = Eip803x.Evm.MemoryCopyExecution.Activation.amsterdam := by
  rfl

theorem extracted_amsterdam_refines (table : DispatchTable) (opcode : Opcode) (state : MachineState) :
    executeAmsterdam table opcode state =
      Eip803x.Evm.MemoryCopyExecution.execute
        Eip803x.Evm.MemoryCopyExecution.Schedule.amsterdam
        Eip803x.Evm.MemoryCopyExecution.Activation.amsterdam table opcode state := by
  simp [executeAmsterdam, amsterdam_schedule_refines, amsterdam_activation_refines,
    extracted_transition_refines]

theorem extracted_amsterdam_closed_refines
    (table : DispatchTable) (opcode : Opcode) (state : MachineState) :
    executeAmsterdamClosed table opcode state =
      Eip803x.Evm.MemoryCopyExecution.closeFailureTrace table
        (Eip803x.Evm.MemoryCopyExecution.execute
          Eip803x.Evm.MemoryCopyExecution.Schedule.amsterdam
          Eip803x.Evm.MemoryCopyExecution.Activation.amsterdam table opcode state) := by
  simp [executeAmsterdamClosed, extracted_amsterdam_refines, close_failure_trace_refines]

structure NonExecutionGas where
  stateReservoir : Nat
  stateFromGasLeft : Nat
  stateUsed : Nat
  refundCounter : Int
  deriving DecidableEq, Repr

def nonExecutionGas (state : MachineState) : NonExecutionGas :=
  { stateReservoir := state.gas.stateReservoir
    stateFromGasLeft := state.gas.stateFromGasLeft
    stateUsed := state.gas.stateUsed
    refundCounter := state.gas.refundCounter }

theorem debit_execution_preserves_nonexecution_gas (amount : Nat) (state after : MachineState)
    (h : debitExecution amount state = .paid after) :
    nonExecutionGas after = nonExecutionGas state := by
  unfold debitExecution at h
  cases hCharge : GasMachine.chargeExecution amount state.gas with
  | error error => simp [hCharge] at h
  | ok gas =>
    simp [hCharge] at h
    subst after
    unfold GasMachine.chargeExecution at hCharge
    split at hCharge
    · cases hCharge
      rfl
    · simp_all

theorem exhaust_preserves_nonexecution_gas (state : MachineState) :
    nonExecutionGas (exhaust state) = nonExecutionGas state := by
  rfl

theorem trace_start_is_preincrement (table : DispatchTable) (opcode : Opcode)
    (state : MachineState) :
    (dispatchEnter table opcode state).pc = state.pc + 1 /\
      (dispatchEnter table opcode state).opcodeCount = state.opcodeCount + 1 := by
  cases table <;>
    cases hMemory : state.traceCapabilities.memory <;>
    cases hStack : state.traceCapabilities.stack <;>
    cases hReturnData : state.traceCapabilities.returnData <;>
    simp [dispatchEnter, Eip803x.Evm.MemoryCopyExecution.DispatchTable.tracing,
      hMemory, hStack, hReturnData]

theorem traced_start_payload_order (opcode : Opcode) (state : MachineState)
    (hMemory : state.traceCapabilities.memory = true)
    (hStack : state.traceCapabilities.stack = true)
    (hReturnData : state.traceCapabilities.returnData = true) :
    (dispatchEnter .traced opcode state).trace = state.trace ++
      [.start opcode state.pc state.gas.gasLeft,
       .operationMemory state.memory.bytes,
       .operationMemorySize state.memory.bytes.length,
       .operationStack state.stack.words,
       .operationReturnData state.returnData] := by
  simp [dispatchEnter, Eip803x.Evm.MemoryCopyExecution.DispatchTable.tracing,
    hMemory, hStack, hReturnData, List.append_assoc]

theorem traced_success_reports_postcharge_gas (state : MachineState) :
    (dispatchFinish .traced state).trace = state.trace ++ [.finish state.gas.gasLeft] := by
  rfl

theorem fault_pc_normalizes_dispatch_increment (status : Status) (state : MachineState)
    (hStatus : status ≠ .ok) :
    faultPc { status := status, state := state } = state.pc - 1 := by
  simp [faultPc, hStatus]

structure ControlView where
  code : List Eip803x.Evm.MemoryStackControl.Byte
  calldata : List Eip803x.Evm.MemoryStackControl.Byte
  returnData : List Eip803x.Evm.MemoryStackControl.Byte
  memory : List Eip803x.Evm.MemoryStackControl.Byte
  stack : List UInt256
  pc : Nat
  deriving DecidableEq, Repr

def viewMachine (state : MachineState) : ControlView :=
  { code := state.code
    calldata := state.calldata
    returnData := state.returnData
    memory := state.memory.bytes
    stack := state.stack.words
    pc := state.pc }

@[simp] theorem view_machine_trace_memory (table : DispatchTable) (offset : Nat)
    (bytes : List Eip803x.Evm.MemoryStackControl.Byte) (state : MachineState) :
    viewMachine (Eip803x.Evm.MemoryCopyExecution.traceMemory table offset bytes state) =
      viewMachine state := by
  cases table <;> rfl

@[simp] theorem view_machine_trace_push (table : DispatchTable)
    (bytes : List Eip803x.Evm.MemoryStackControl.Byte) (state : MachineState) :
    viewMachine (Eip803x.Evm.MemoryCopyExecution.tracePush table bytes state) =
      viewMachine state := by
  cases table <;> rfl

@[simp] theorem view_machine_finish_instruction (table : DispatchTable) (state : MachineState) :
    viewMachine (Eip803x.Evm.MemoryCopyExecution.finishInstruction table state) =
      viewMachine state := by
  cases table <;> rfl

def referenceInput (schedule : Schedule) (opcode : Opcode) (state : MachineState) :
    Eip803x.Evm.MemoryStackControl.State :=
  { code := state.code
    calldata := state.calldata
    returnData := state.returnData
    memory := state.memory
    stack := state.stack
    pc := state.pc
    gasLeft := if opcode = .gas then state.gas.gasLeft - schedule.base else state.gas.gasLeft
    status := .running }

def referenceInputAfterDispatch (schedule : Schedule) (opcode : Opcode) (state : MachineState) :
    Eip803x.Evm.MemoryStackControl.State :=
  { code := state.code
    calldata := state.calldata
    returnData := state.returnData
    memory := state.memory
    stack := state.stack
    pc := state.pc - 1
    gasLeft := if opcode = .gas then state.gas.gasLeft - schedule.base else state.gas.gasLeft
    status := .running }

def referenceExecute (opcode : Opcode) (state : Eip803x.Evm.MemoryStackControl.State) :
    Eip803x.Evm.MemoryStackControl.State :=
  match opcode with
  | .mload => Eip803x.Evm.MemoryStackControl.loadMemory state
  | .mstore => Eip803x.Evm.MemoryStackControl.storeMemory state
  | .mstore8 => Eip803x.Evm.MemoryStackControl.storeMemoryByte state
  | .msize => Eip803x.Evm.MemoryStackControl.pushAndAdvance state 1
      (Eip803x.Evm.Word.ofNat state.memory.bytes.length)
  | .calldatacopy => Eip803x.Evm.MemoryStackControl.copyCallData state
  | .codecopy => Eip803x.Evm.MemoryStackControl.copyCode state
  | .returndatacopy => Eip803x.Evm.MemoryStackControl.copyReturnData state
  | .mcopy => Eip803x.Evm.MemoryStackControl.copyMemory state
  | .gas => Eip803x.Evm.MemoryStackControl.pushAndAdvance state 1
      (Eip803x.Evm.Word.ofNat state.gasLeft)

def viewReference (state : Eip803x.Evm.MemoryStackControl.State) : ControlView :=
  { code := state.code
    calldata := state.calldata
    returnData := state.returnData
    memory := state.memory.bytes
    stack := state.stack.words
    pc := state.pc }

theorem reference_input_after_dispatch (schedule : Schedule) (table : DispatchTable)
    (opcode : Opcode) (state : MachineState) :
    referenceInputAfterDispatch schedule opcode
        (Eip803x.Evm.MemoryCopyExecution.beginInstruction table opcode state) =
      referenceInput schedule opcode state := by
  cases table <;>
    cases hMemory : state.traceCapabilities.memory <;>
    cases hStack : state.traceCapabilities.stack <;>
    cases hReturnData : state.traceCapabilities.returnData <;>
    simp [referenceInputAfterDispatch, referenceInput,
      Eip803x.Evm.MemoryCopyExecution.beginInstruction,
      Eip803x.Evm.MemoryCopyExecution.DispatchTable.tracing,
      hMemory, hStack, hReturnData]

@[simp] theorem take_stack_tail (value : UInt256) (tail : List UInt256)
    (h : (value :: tail).length ≤ Eip803x.Evm.MemoryStackControl.stackLimit) :
    List.take 1023 tail = tail := by
  apply List.take_of_length_le
  simp [Eip803x.Evm.MemoryStackControl.stackLimit] at h ⊢
  omega

private theorem read_byte_eq_getElem
    (bytes : List Eip803x.Evm.MemoryStackControl.Byte) (index : Nat)
    (h : index < bytes.length) :
    Eip803x.Evm.MemoryStackControl.readByte bytes index = bytes[index] := by
  induction bytes generalizing index with
  | nil => simp at h
  | cons head tail ih =>
    cases index with
    | zero => rfl
    | succ index =>
      simp only [Eip803x.Evm.MemoryStackControl.readByte, List.getElem_cons_succ]
      exact ih index (by simpa using h)

private theorem read_range_zero_self
    (bytes : List Eip803x.Evm.MemoryStackControl.Byte) :
    Eip803x.Evm.MemoryStackControl.readRange bytes 0 bytes.length = bytes := by
  apply List.ext_get
  · simp [Eip803x.Evm.MemoryStackControl.readRange]
  · intro index hLeft hRight
    simp only [Eip803x.Evm.MemoryStackControl.readRange]
    simpa using read_byte_eq_getElem bytes index hRight

@[simp] private theorem read_range_zero
    (bytes : List Eip803x.Evm.MemoryStackControl.Byte) (offset : Nat) :
    Eip803x.Evm.MemoryStackControl.readRange bytes offset 0 = [] := by
  rfl

private theorem rounded_memory_size_of_aligned (length : Nat) (h : 32 ∣ length) :
    Eip803x.Evm.MemoryStackControl.roundedMemorySize length = length := by
  rcases h with ⟨words, rfl⟩
  cases words with
  | zero => simp [Eip803x.Evm.MemoryStackControl.roundedMemorySize]
  | succ words =>
    unfold Eip803x.Evm.MemoryStackControl.roundedMemorySize
    rw [if_neg (Nat.mul_ne_zero (by decide) (Nat.succ_ne_zero words))]
    omega

@[simp] private theorem memory_write_range_empty
    (memory : Eip803x.Evm.MemoryStackControl.Memory) (offset : Nat) :
    (memory.writeRange offset []).bytes = memory.bytes := by
  change (Eip803x.Evm.MemoryStackControl.Memory.ofBytes
    (Eip803x.Evm.MemoryStackControl.expandMemory memory.bytes offset 0)).bytes = memory.bytes
  rw [Eip803x.Evm.MemoryStackControl.expandMemory_zero_size]
  change Eip803x.Evm.MemoryStackControl.readRange memory.bytes 0
    (Eip803x.Evm.MemoryStackControl.roundedMemorySize memory.bytes.length) = memory.bytes
  rw [rounded_memory_size_of_aligned memory.bytes.length memory.wordAligned]
  exact read_range_zero_self memory.bytes

@[simp] private theorem memory_mcopy_zero
    (memory : Eip803x.Evm.MemoryStackControl.Memory) (destination source : Nat) :
    (memory.mcopy destination source 0).bytes = memory.bytes := by
  simp [Eip803x.Evm.MemoryStackControl.Memory.mcopy,
    Eip803x.Evm.MemoryStackControl.Memory.expand,
    Eip803x.Evm.MemoryStackControl.requestedMemorySize]

set_option maxHeartbeats 1000000 in
private theorem active_success_projects_to_memory_stack_control
    (schedule : Schedule) (table : DispatchTable) (opcode : Opcode) (state : MachineState)
    (hPc : 0 < state.pc)
    (hSuccess : (Eip803x.Evm.MemoryCopyExecution.executeActive schedule table opcode state).status = .ok) :
    viewMachine (Eip803x.Evm.MemoryCopyExecution.executeActive schedule table opcode state).state =
      viewReference (referenceExecute opcode (referenceInputAfterDispatch schedule opcode state)) := by
  rcases state with
    ⟨code, calldata, returnData, memory, stack, pc, opcodeCount, gas, traceCapabilities, trace⟩
  rcases stack with ⟨words, bounded⟩
  cases opcode
  all_goals cases words
  all_goals
    simp_all [Eip803x.Evm.MemoryCopyExecution.executeActive,
      Eip803x.Evm.MemoryCopyExecution.executeMload,
      Eip803x.Evm.MemoryCopyExecution.executeMstore,
      Eip803x.Evm.MemoryCopyExecution.executeMstore8,
      Eip803x.Evm.MemoryCopyExecution.pushFixed,
      Eip803x.Evm.MemoryCopyExecution.copyFrom,
      Eip803x.Evm.MemoryCopyExecution.executeReturnDataCopy,
      Eip803x.Evm.MemoryCopyExecution.executeMcopy,
      Eip803x.Evm.MemoryCopyExecution.debit,
      Eip803x.Evm.MemoryCopyExecution.chargePreparedMemory,
      Eip803x.Evm.MemoryCopyExecution.prepareMemory,
      Eip803x.Evm.MemoryCopyExecution.withStatus,
      Eip803x.Evm.MemoryCopyExecution.replaceTop,
      GasMachine.chargeExecution, referenceInputAfterDispatch, referenceExecute,
      viewReference,
      Eip803x.Evm.MemoryStackControl.loadMemory,
      Eip803x.Evm.MemoryStackControl.storeMemory,
      Eip803x.Evm.MemoryStackControl.storeMemoryByte,
      Eip803x.Evm.MemoryStackControl.copyCallData,
      Eip803x.Evm.MemoryStackControl.copyCode,
      Eip803x.Evm.MemoryStackControl.copyReturnData,
      Eip803x.Evm.MemoryStackControl.performReturnDataCopy,
      Eip803x.Evm.MemoryStackControl.copyMemory,
      Eip803x.Evm.MemoryStackControl.pushAndAdvance,
      Eip803x.Evm.MemoryStackControl.State.advance,
      Eip803x.Evm.MemoryStackControl.Stack.pop,
      Eip803x.Evm.MemoryStackControl.Stack.popTwo,
      Eip803x.Evm.MemoryStackControl.Stack.popThree,
      Eip803x.Evm.MemoryStackControl.Stack.push,
      Eip803x.Evm.MemoryStackControl.Stack.fromWords,
      Eip803x.Evm.MemoryStackControl.stackLimit,
      Eip803x.Evm.Word.ofNat,
      Eip803x.Evm.Word.normalize,
      Eip803x.Evm.Word.modulus,
      Eip803x.Evm.Word.bitWidth,
      Eip803x.Evm.Word.byteCount]
  all_goals
    repeat split at *
  all_goals try subst_vars
  all_goals try simp_all
  all_goals try omega
  all_goals try grind
  all_goals
    repeat split at *
  all_goals try subst_vars
  all_goals try simp_all
  all_goals try omega
  all_goals try grind
  all_goals
    repeat split at *
  all_goals try subst_vars
  all_goals try simp_all
  all_goals try omega
  all_goals try grind
  all_goals
    repeat split at *
  all_goals try subst_vars
  all_goals try simp_all
  all_goals try omega
  all_goals try grind
  all_goals
    repeat split at *
  all_goals try subst_vars
  all_goals try simp_all
  all_goals try omega
  all_goals try grind
  all_goals
    repeat split at *
  all_goals try subst_vars
  all_goals try simp_all
  all_goals try omega
  all_goals try grind
  all_goals
    repeat split at *
  all_goals try subst_vars
  all_goals try simp_all
  all_goals try omega
  all_goals try grind
  all_goals
    repeat split at *
  all_goals try subst_vars
  all_goals try simp_all
  all_goals try omega
  all_goals try grind
  all_goals
    repeat split at *
  all_goals try subst_vars
  all_goals try simp_all
  all_goals try omega
  all_goals try grind
  all_goals
    repeat split at *
  all_goals try subst_vars
  all_goals try simp_all
  all_goals try omega
  all_goals try grind
  all_goals
    repeat split at *
  all_goals try subst_vars
  all_goals try simp_all
  all_goals try omega
  all_goals try grind
  all_goals
    repeat split at *
  all_goals try subst_vars
  all_goals try simp_all
  all_goals try omega
  all_goals try grind
  all_goals
    repeat split at *
  all_goals try subst_vars
  all_goals try simp_all
  all_goals try omega
  all_goals try grind
  all_goals
    repeat split at *
  all_goals try subst_vars
  all_goals try simp_all
  all_goals try omega
  all_goals try grind
  all_goals try simp_all only [view_machine_trace_memory, view_machine_trace_push,
    view_machine_finish_instruction]
  all_goals try subst_vars
  all_goals unfold viewMachine
  all_goals try simp_all [Eip803x.Evm.MemoryCopyExecution.returnDataRangeValid,
    Eip803x.Evm.MemoryStackControl.State.exceptional]
  all_goals try omega

/-!
The earlier handwritten reference intentionally carries no memory-gas schedule.
The projection therefore compares control, stack, and byte-memory observables on
successful Amsterdam executions. `GAS` receives the already-debited value.
-/
theorem operational_success_projects_to_memory_stack_control
    (schedule : Schedule) (table : DispatchTable) (opcode : Opcode) (state : MachineState)
    (hSuccess : (Eip803x.Evm.MemoryCopyExecution.execute schedule .amsterdam table opcode state).status = .ok) :
    viewMachine (Eip803x.Evm.MemoryCopyExecution.execute schedule .amsterdam table opcode state).state =
      viewReference (referenceExecute opcode (referenceInput schedule opcode state)) := by
  have hPc : 0 <
      (Eip803x.Evm.MemoryCopyExecution.beginInstruction table opcode state).pc := by
    rw [← dispatch_enter_refines]
    rw [(trace_start_is_preincrement table opcode state).1]
    omega
  cases opcode <;>
    simp only [Eip803x.Evm.MemoryCopyExecution.execute,
      Eip803x.Evm.MemoryCopyExecution.Activation.amsterdam,
      Eip803x.Evm.MemoryCopyExecution.active, if_true] at hSuccess ⊢
  all_goals
    rw [← reference_input_after_dispatch]
    exact active_success_projects_to_memory_stack_control schedule table _ _ hPc hSuccess

theorem extracted_success_projects_to_memory_stack_control
    (schedule : Schedule) (table : DispatchTable) (opcode : Opcode) (state : MachineState)
    (hSuccess : (executeExtracted schedule .amsterdam table opcode state).status = .ok) :
    viewMachine (executeExtracted schedule .amsterdam table opcode state).state =
      viewReference (referenceExecute opcode (referenceInput schedule opcode state)) := by
  rw [extracted_transition_refines] at hSuccess ⊢
  exact operational_success_projects_to_memory_stack_control schedule table opcode state hSuccess

end Eip803x.Generated.MemoryCopyOpcodeRefinement
