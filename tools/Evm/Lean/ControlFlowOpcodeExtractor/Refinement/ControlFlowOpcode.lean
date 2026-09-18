-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import ControlFlowOpcodeExtractor.Generated.ControlFlowOpcodeKernel
import ControlFlowOpcodeExtractor.Specification.ControlFlowExecution

namespace Eip803x.Generated.ControlFlowOpcodeRefinement

open Eip803x.GasMachine
open Eip803x.Evm.MemoryStackControl.Stack
open Eip803x.Generated.ControlFlowOpcodeKernel

theorem descriptor_admitted : descriptorAdmitted = true := by native_decide

theorem every_closed_root_admitted (opcode : Opcode) (table : DispatchTable) :
    specializationAdmitted opcode table = true := by
  cases opcode <;> cases table <;> native_decide

theorem operation_gate_refines (activation : Activation) (opcode : Opcode) :
    operationActive activation (descriptor opcode).activationRule =
      Eip803x.Evm.ControlFlowExecution.active activation opcode := by
  cases opcode <;> rfl

theorem dispatch_enter_refines (table : DispatchTable) (opcode : Opcode)
    (state : MachineState) :
    dispatchEnter table opcode state =
      Eip803x.Evm.ControlFlowExecution.beginInstruction table opcode state := by
  rfl

theorem report_push_refines (table : DispatchTable) (width : Nat) (value : Eip803x.UInt256)
    (state : MachineState) :
    reportPush table width value state =
      Eip803x.Evm.ControlFlowExecution.tracePush table width value state := by
  rfl

theorem dispatch_finish_refines (table : DispatchTable) (state : MachineState) :
    dispatchFinish table false state =
      Eip803x.Evm.ControlFlowExecution.finishInstruction table state := by
  cases table <;> rfl

theorem exhaust_refines (state : MachineState) :
    exhaust state = Eip803x.Evm.ControlFlowExecution.exhaust state := by
  rfl

theorem debit_refines (amount : Nat) (state : MachineState) :
    debitExecution amount state = Eip803x.Evm.ControlFlowExecution.debit amount state := by
  rfl

theorem memory_cost_refines (schedule : Schedule) (words : Nat) :
    memoryCost schedule words = Eip803x.Evm.ControlFlowExecution.memoryCost schedule words := by
  rfl

theorem range_allowed_refines (schedule : Schedule) (offset length : Eip803x.UInt256) :
    rangeAllowed schedule offset length =
      Eip803x.Evm.ControlFlowExecution.memoryRangeValid schedule offset length := by
  rfl

theorem extracted_instruction_width_refines
    (opcode : Eip803x.Evm.MemoryStackControl.Byte) :
    extractedInstructionWidth jumpValidationProfile opcode =
      Eip803x.Evm.ControlFlowExecution.productionInstructionWidth opcode := by
  rfl

theorem scan_extracted_jump_destination_refines
    (code : List Eip803x.Evm.MemoryStackControl.Byte) (target pc fuel : Nat) :
    scanExtractedJumpDestination jumpValidationProfile code target pc fuel =
      Eip803x.Evm.ControlFlowExecution.scanProductionJumpDestination code target pc fuel := by
  simp only [jumpValidationProfile]
  induction fuel generalizing pc with
  | zero => rfl
  | succ fuel ih =>
      by_cases hTarget : pc = target
      · subst pc
        cases hCode : code[target]? with
        | none =>
            simp [scanExtractedJumpDestination,
              Eip803x.Evm.ControlFlowExecution.scanProductionJumpDestination, hCode]
        | some value =>
            by_cases hOpcode : value.val = 0x5b <;>
              simp [scanExtractedJumpDestination,
                Eip803x.Evm.ControlFlowExecution.scanProductionJumpDestination,
                hCode, hOpcode]
      · cases hCode : code[pc]? with
        | none =>
            simp [scanExtractedJumpDestination,
              Eip803x.Evm.ControlFlowExecution.scanProductionJumpDestination,
              hTarget, hCode]
        | some value =>
            simp [scanExtractedJumpDestination,
              Eip803x.Evm.ControlFlowExecution.scanProductionJumpDestination,
              extractedInstructionWidth,
              Eip803x.Evm.ControlFlowExecution.productionInstructionWidth,
              hTarget, hCode, ih]

theorem extracted_valid_jump_destination_refines
    (code : List Eip803x.Evm.MemoryStackControl.Byte) (target : Nat) :
    extractedValidJumpDestination jumpValidationProfile code target =
      Eip803x.Evm.ControlFlowExecution.validProductionJumpDestination code target := by
  cases code with
  | nil => rfl
  | cons first rest =>
      simp only [extractedValidJumpDestination,
        Eip803x.Evm.ControlFlowExecution.validProductionJumpDestination,
        List.length_cons]
      rw [scan_extracted_jump_destination_refines]
      rfl

theorem prepare_expansion_refines (schedule : Schedule)
    (memory : Eip803x.Evm.MemoryStackControl.Memory)
    (offset length : Eip803x.UInt256) :
    prepareExpansion schedule memory offset length =
      Eip803x.Evm.ControlFlowExecution.prepareMemory schedule memory offset length := by
  rfl

theorem execute_semantic_refines (table : DispatchTable)
    (opcode : Opcode) (entered : MachineState) :
    executeSemantic amsterdamSchedule table (descriptor opcode) entered =
      Eip803x.Evm.ControlFlowExecution.executeActive
        Eip803x.Evm.ControlFlowExecution.Schedule.amsterdam table opcode entered := by
  change executeSemantic amsterdamSchedule table (descriptor opcode) entered =
    Eip803x.Evm.ControlFlowExecution.executeActive amsterdamSchedule table opcode entered
  cases opcode <;> cases table <;>
    first
    | rfl
    | simp only [executeSemantic, descriptor, descriptors, runJump, runJumpIf,
        Eip803x.Evm.ControlFlowExecution.executeActive,
        Eip803x.Evm.ControlFlowExecution.runJump,
        Eip803x.Evm.ControlFlowExecution.runJumpI,
        extracted_valid_jump_destination_refines]
      rfl

theorem generated_handler_refines (activation : Activation)
    (table : DispatchTable) (opcode : Opcode) (state : MachineState) :
    executeCore amsterdamSchedule activation table opcode state =
      Eip803x.Evm.ControlFlowExecution.executeHandler
        Eip803x.Evm.ControlFlowExecution.Schedule.amsterdam activation table opcode state := by
  simp only [executeCore, Eip803x.Evm.ControlFlowExecution.executeHandler,
    dispatch_enter_refines, operation_gate_refines]
  split
  · apply execute_semantic_refines
  · rfl

theorem close_frame_refines (table : DispatchTable) (result : Outcome) :
    closeFrame table result = Eip803x.Evm.ControlFlowExecution.closeFrame table result := by
  cases table <;> cases result with
  | mk status state => cases status <;> rfl

theorem extracted_transition_refines (activation : Activation)
    (table : DispatchTable) (opcode : Opcode) (state : MachineState) :
    executeExtracted amsterdamSchedule activation table opcode state =
      Eip803x.Evm.ControlFlowExecution.execute
        Eip803x.Evm.ControlFlowExecution.Schedule.amsterdam activation table opcode state := by
  simp only [executeExtracted, descriptor_admitted, every_closed_root_admitted, Bool.true_and,
    if_true, Eip803x.Evm.ControlFlowExecution.execute]
  rw [generated_handler_refines, close_frame_refines]

theorem amsterdam_schedule_refines :
    amsterdamSchedule = Eip803x.Evm.ControlFlowExecution.Schedule.amsterdam := by
  rfl

theorem amsterdam_activation_refines :
    amsterdamActivation = Eip803x.Evm.ControlFlowExecution.Activation.amsterdam := by
  rfl

theorem extracted_amsterdam_refines (table : DispatchTable) (opcode : Opcode)
    (state : MachineState) :
    executeAmsterdam table opcode state =
      Eip803x.Evm.ControlFlowExecution.execute
        Eip803x.Evm.ControlFlowExecution.Schedule.amsterdam
        Eip803x.Evm.ControlFlowExecution.Activation.amsterdam table opcode state := by
  simpa only [executeAmsterdam, amsterdamActivation,
    Eip803x.Evm.ControlFlowExecution.Activation.amsterdam] using
    extracted_transition_refines Eip803x.Evm.ControlFlowExecution.Activation.amsterdam table opcode state

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

theorem debit_preserves_nonexecution_gas (amount : Nat) (state after : MachineState)
    (h : debitExecution amount state = .paid after) :
    nonExecutionGas after = nonExecutionGas state := by
  unfold debitExecution at h
  cases hCharge : chargeExecution amount state.gas with
  | error error => simp [hCharge] at h
  | ok gas =>
    simp only [hCharge] at h
    cases h
    unfold chargeExecution at hCharge
    split at hCharge
    · cases hCharge
      rfl
    · contradiction

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
    simp [dispatchEnter, Eip803x.Evm.ControlFlowExecution.DispatchTable.tracing,
      hMemory, hStack, hReturnData]

theorem traced_start_callback_order (opcode : Opcode) (state : MachineState)
    (hMemory : state.traceCapabilities.memory = true)
    (hStack : state.traceCapabilities.stack = true)
    (hReturnData : state.traceCapabilities.returnData = true) :
    (dispatchEnter .traced opcode state).trace = state.trace ++
      [.start opcode state.pc state.gas.gasLeft,
       .operationMemory state.memory.bytes,
       .operationMemorySize state.memory.bytes.length,
       .operationStack state.stack.words.reverse,
       .operationReturnData state.previousReturnData] := by
  simp [dispatchEnter, Eip803x.Evm.ControlFlowExecution.DispatchTable.tracing,
    hMemory, hStack, hReturnData, List.append_assoc]

theorem traced_success_reports_postcharge_gas (state : MachineState) :
    (dispatchFinish .traced false state).trace = state.trace ++ [.finish state.gas.gasLeft] := by
  rfl

theorem traced_pc_push_is_exactly_four_bytes (value : Eip803x.UInt256)
    (state : MachineState) :
    (reportPush .traced 4 value state).trace =
      state.trace ++ [.stackPush ((Eip803x.Evm.MemoryStackControl.wordToBytes value).drop 28)] := by
  rfl

theorem traced_slotnum_push_is_exactly_eight_bytes (value : Eip803x.UInt256)
    (state : MachineState) :
    (reportPush .traced 8 value state).trace =
      state.trace ++ [.stackPush ((Eip803x.Evm.MemoryStackControl.wordToBytes value).drop 24)] := by
  rfl

theorem traced_failure_closes_remaining_gas_before_error (status : Status)
    (state : MachineState) (hFault : status ≠ .ok)
    (hFault' : status ≠ .stop) (hFault'' : status ≠ .revert)
    (hMismatch : status ≠ .extractionMismatch) :
    (closeFrame .traced { status := status, state := state }).state.trace =
      state.trace ++ [.finish (if status = .outOfGas then 0 else state.gas.gasLeft), .error status] := by
  cases status <;> simp_all [closeFrame, exhaust,
    Eip803x.Evm.ControlFlowExecution.DispatchTable.tracing, List.append_assoc]

theorem traced_terminal_has_no_error (status : Status) (state : MachineState)
    (hTerminal : status = .stop ∨ status = .revert) :
    (closeFrame .traced { status := status, state := state }).state.trace =
      state.trace ++ [.finish state.gas.gasLeft] := by
  rcases hTerminal with rfl | rfl <;> rfl

theorem out_of_gas_closure_exhausts (table : DispatchTable) (state : MachineState) :
    (closeFrame table { status := .outOfGas, state := state }).state.gas.gasLeft = 0 := by
  cases table <;> rfl

theorem disabled_slotnum_preserves_handler_state_after_dispatch
    (table : DispatchTable) (activation : Activation) (state : MachineState)
    (hDisabled : activation.eip7843 = false) :
    (executeCore amsterdamSchedule activation table .slotnum state).status = .badInstruction /\
      (executeCore amsterdamSchedule activation table .slotnum state).state.pc = state.pc + 1 /\
      (executeCore amsterdamSchedule activation table .slotnum state).state.stack.words = state.stack.words /\
      (executeCore amsterdamSchedule activation table .slotnum state).state.gas = state.gas := by
  cases table <;>
    cases hMemory : state.traceCapabilities.memory <;>
    cases hStack : state.traceCapabilities.stack <;>
    cases hReturnData : state.traceCapabilities.returnData <;>
    simp [executeCore, descriptor, descriptors, operationActive, dispatchEnter,
      Eip803x.Evm.ControlFlowExecution.DispatchTable.tracing, outcome, hDisabled,
      hMemory, hStack, hReturnData]

theorem missing_slot_is_bad_instruction_before_debit (table : DispatchTable)
    (state : MachineState) (hMissing : state.slotNumber = none) :
    (executeCore amsterdamSchedule amsterdamActivation table .slotnum state).status = .badInstruction /\
      (executeCore amsterdamSchedule amsterdamActivation table .slotnum state).state.gas = state.gas /\
      (executeCore amsterdamSchedule amsterdamActivation table .slotnum state).state.stack.words = state.stack.words := by
  cases table <;>
    cases hMemory : state.traceCapabilities.memory <;>
    cases hStack : state.traceCapabilities.stack <;>
    cases hReturnData : state.traceCapabilities.returnData <;>
    simp [executeCore, descriptor, descriptors, operationActive, executeSemantic,
      runSlotNumber, dispatchEnter, outcome, amsterdamActivation,
      Eip803x.Evm.ControlFlowExecution.DispatchTable.tracing, hMissing,
      hMemory, hStack, hReturnData]

theorem pc_out_of_gas_advances_then_exhausts (table : DispatchTable) (state : MachineState)
    (hGas : state.gas.gasLeft < 2) :
    (executeAmsterdam table .pc state).status = .outOfGas /\
      (executeAmsterdam table .pc state).state.pc = state.pc + 1 /\
      (executeAmsterdam table .pc state).state.gas.gasLeft = 0 /\
      (executeAmsterdam table .pc state).state.stack.words = state.stack.words := by
  have hInsufficient : ¬2 ≤ state.gas.gasLeft := by omega
  rw [extracted_amsterdam_refines]
  cases table <;>
    cases hMemory : state.traceCapabilities.memory <;>
    cases hStack : state.traceCapabilities.stack <;>
    cases hReturnData : state.traceCapabilities.returnData <;>
    simp [Eip803x.Evm.ControlFlowExecution.execute,
      Eip803x.Evm.ControlFlowExecution.executeHandler,
      Eip803x.Evm.ControlFlowExecution.executeActive,
      Eip803x.Evm.ControlFlowExecution.pushFixed,
      Eip803x.Evm.ControlFlowExecution.debit,
      Eip803x.Evm.ControlFlowExecution.closeFrame,
      Eip803x.Evm.ControlFlowExecution.beginInstruction,
      Eip803x.Evm.ControlFlowExecution.exhaust,
      Eip803x.Evm.ControlFlowExecution.Schedule.amsterdam,
      Eip803x.Evm.ControlFlowExecution.outcome,
      Eip803x.Evm.ControlFlowExecution.active,
      Eip803x.Evm.ControlFlowExecution.DispatchTable.tracing,
      chargeExecution, hInsufficient, hMemory, hStack, hReturnData]

theorem jump_underflow_preserves_stack_after_mid_debit (table : DispatchTable)
    (state : MachineState) (hEmpty : state.stack.words = []) (hGas : 8 ≤ state.gas.gasLeft) :
    (executeAmsterdam table .jump state).status = .stackUnderflow /\
      (executeAmsterdam table .jump state).state.stack.words = [] /\
      (executeAmsterdam table .jump state).state.gas.gasLeft = state.gas.gasLeft - 8 := by
  have hPop : state.stack.pop = none := by
    cases hStack : state.stack with
    | mk words bounded =>
      cases words with
      | nil => rfl
      | cons first rest => simp [hStack] at hEmpty
  rw [extracted_amsterdam_refines]
  cases table <;>
    cases hMemory : state.traceCapabilities.memory <;>
    cases hStack : state.traceCapabilities.stack <;>
    cases hReturnData : state.traceCapabilities.returnData <;>
    simp [Eip803x.Evm.ControlFlowExecution.execute,
      Eip803x.Evm.ControlFlowExecution.executeHandler,
      Eip803x.Evm.ControlFlowExecution.executeActive,
      Eip803x.Evm.ControlFlowExecution.runJump,
      Eip803x.Evm.ControlFlowExecution.debit,
      Eip803x.Evm.ControlFlowExecution.closeFrame,
      Eip803x.Evm.ControlFlowExecution.beginInstruction,
      Eip803x.Evm.ControlFlowExecution.Schedule.amsterdam,
      Eip803x.Evm.ControlFlowExecution.outcome,
      Eip803x.Evm.ControlFlowExecution.active,
      Eip803x.Evm.ControlFlowExecution.DispatchTable.tracing,
      chargeExecution, hPop, hEmpty, hGas, hMemory, hStack, hReturnData]

theorem return_underflow_has_zero_fixed_cost (table : DispatchTable)
    (opcode : Opcode) (state : MachineState)
    (hOpcode : opcode = .return_ ∨ opcode = .revert)
    (hShort : state.stack.words.length < 2) :
    (executeAmsterdam table opcode state).status = .stackUnderflow /\
      (executeAmsterdam table opcode state).state.gas = state.gas /\
      (executeAmsterdam table opcode state).state.stack.words = state.stack.words := by
  have hPop : state.stack.popTwo = none := by
    cases hStack : state.stack with
    | mk words bounded =>
      cases words with
      | nil => rfl
      | cons first tail =>
        cases tail with
        | nil => rfl
        | cons second rest =>
          have hWords : state.stack.words = first :: second :: rest :=
            congrArg Eip803x.Evm.MemoryStackControl.Stack.words hStack
          have hTwo : 2 ≤ state.stack.words.length := by simp [hWords]
          omega
  rcases hOpcode with rfl | rfl <;>
    rw [extracted_amsterdam_refines] <;>
    cases table <;>
    cases hMemory : state.traceCapabilities.memory <;>
    cases hStack : state.traceCapabilities.stack <;>
    cases hReturnData : state.traceCapabilities.returnData <;>
    simp [Eip803x.Evm.ControlFlowExecution.execute,
      Eip803x.Evm.ControlFlowExecution.executeHandler,
      Eip803x.Evm.ControlFlowExecution.executeActive,
      Eip803x.Evm.ControlFlowExecution.runReturnLike,
      Eip803x.Evm.ControlFlowExecution.closeFrame,
      Eip803x.Evm.ControlFlowExecution.beginInstruction,
      Eip803x.Evm.ControlFlowExecution.outcome,
      Eip803x.Evm.ControlFlowExecution.Activation.amsterdam,
      Eip803x.Evm.ControlFlowExecution.active,
      Eip803x.Evm.ControlFlowExecution.DispatchTable.tracing, hPop,
      hMemory, hStack, hReturnData]

theorem untraced_fused_jump_consumes_landed_jumpdest (state : MachineState)
    (tail : Eip803x.Evm.MemoryStackControl.Stack)
    (destination : Eip803x.UInt256)
    (hPop : state.stack.pop = some (destination, tail))
    (hValid : Eip803x.Evm.ControlFlowExecution.validProductionJumpDestination
      state.code destination.val = true)
    (hGas : 9 ≤ state.gas.gasLeft) :
    (executeAmsterdam .noTrace .jump state).state.pc = destination.val + 1 /\
      (executeAmsterdam .noTrace .jump state).state.opcodeCount = state.opcodeCount + 2 /\
      (executeAmsterdam .noTrace .jump state).state.gas.gasLeft = state.gas.gasLeft - 9 := by
  have hMid : 8 ≤ state.gas.gasLeft := by omega
  have hJumpDest : 1 ≤ state.gas.gasLeft - 8 := by omega
  rw [extracted_amsterdam_refines]
  simp [Eip803x.Evm.ControlFlowExecution.execute,
    Eip803x.Evm.ControlFlowExecution.executeHandler,
    Eip803x.Evm.ControlFlowExecution.executeActive,
    Eip803x.Evm.ControlFlowExecution.runJump,
    Eip803x.Evm.ControlFlowExecution.fuseJumpDest,
    Eip803x.Evm.ControlFlowExecution.debit,
    Eip803x.Evm.ControlFlowExecution.closeFrame,
    Eip803x.Evm.ControlFlowExecution.beginInstruction,
    Eip803x.Evm.ControlFlowExecution.finishSuccess,
    Eip803x.Evm.ControlFlowExecution.finishInstruction,
    Eip803x.Evm.ControlFlowExecution.Schedule.amsterdam,
    Eip803x.Evm.ControlFlowExecution.outcome,
    Eip803x.Evm.ControlFlowExecution.active,
    Eip803x.Evm.ControlFlowExecution.DispatchTable.tracing,
    chargeExecution, hPop, hValid, hMid, hJumpDest] <;> omega

structure ControlView where
  code : List Eip803x.Evm.MemoryStackControl.Byte
  memory : List Eip803x.Evm.MemoryStackControl.Byte
  stack : List Eip803x.UInt256
  pc : Nat
  deriving DecidableEq, Repr

def viewMachine (state : MachineState) : ControlView :=
  { code := state.code
    memory := state.memory.bytes
    stack := state.stack.words
    pc := state.pc }

def referenceInput (state : MachineState) : Eip803x.Evm.MemoryStackControl.State :=
  { code := state.code
    calldata := []
    returnData := state.previousReturnData
    memory := state.memory
    stack := state.stack
    pc := state.pc
    gasLeft := state.gas.gasLeft
    status := .running }

def viewReference (state : Eip803x.Evm.MemoryStackControl.State) : ControlView :=
  { code := state.code
    memory := state.memory.bytes
    stack := state.stack.words
    pc := state.pc }

/-!
`MemoryStackControl` has no gas schedule, tracing surface, opcode counter, or
`SLOTNUM`. This bridge intentionally covers only the unconditional `STOP`
control projection; the complete eight-opcode claim is the exact generated-to-
operational refinement above.
-/
theorem stop_projects_to_memory_stack_control (table : DispatchTable) (state : MachineState) :
    viewMachine (executeAmsterdam table .stop state).state =
      viewReference (Eip803x.Evm.MemoryStackControl.State.haltStopped (referenceInput state) 1) := by
  rw [extracted_amsterdam_refines]
  cases table <;>
    cases hMemory : state.traceCapabilities.memory <;>
    cases hStack : state.traceCapabilities.stack <;>
    cases hReturnData : state.traceCapabilities.returnData <;>
    simp [Eip803x.Evm.ControlFlowExecution.execute,
      Eip803x.Evm.ControlFlowExecution.executeHandler,
      Eip803x.Evm.ControlFlowExecution.executeActive,
      Eip803x.Evm.ControlFlowExecution.runStop,
      Eip803x.Evm.ControlFlowExecution.beginInstruction,
      Eip803x.Evm.ControlFlowExecution.closeFrame,
      Eip803x.Evm.ControlFlowExecution.outcome,
      Eip803x.Evm.ControlFlowExecution.active,
      Eip803x.Evm.ControlFlowExecution.DispatchTable.tracing,
      referenceInput, viewMachine, viewReference,
      Eip803x.Evm.MemoryStackControl.State.haltStopped,
      hMemory, hStack, hReturnData]

end Eip803x.Generated.ControlFlowOpcodeRefinement
