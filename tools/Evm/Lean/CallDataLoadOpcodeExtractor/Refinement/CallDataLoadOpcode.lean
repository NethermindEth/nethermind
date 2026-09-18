-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import CallDataLoadOpcodeExtractor.Generated.CallDataLoadOpcodeKernel
import CallDataLoadOpcodeExtractor.Specification.CallDataLoadExecution

namespace Eip803x.Generated.CallDataLoadOpcodeRefinement

open Eip803x.Evm
open Eip803x.Evm.MemoryStackControl
open Eip803x.Evm.MemoryStackControl.Stack
open Eip803x.Evm.Word

namespace G
abbrev descriptor := Eip803x.Generated.CallDataLoadOpcodeKernel.descriptor
abbrev dispatchRoots := Eip803x.Generated.CallDataLoadOpcodeKernel.dispatchRoots
abbrev amsterdamSchedule := Eip803x.Generated.CallDataLoadOpcodeKernel.amsterdamSchedule
abbrev tableTracing := Eip803x.Generated.CallDataLoadOpcodeKernel.tableTracing
abbrev tableCancelable := Eip803x.Generated.CallDataLoadOpcodeKernel.tableCancelable
abbrev dispatchEnter := Eip803x.Generated.CallDataLoadOpcodeKernel.dispatchEnter
abbrev replaceTop := Eip803x.Generated.CallDataLoadOpcodeKernel.replaceTop
abbrev accessibleOffset := Eip803x.Generated.CallDataLoadOpcodeKernel.accessibleOffset
abbrev productionDomain := Eip803x.Generated.CallDataLoadOpcodeKernel.productionDomain
abbrev readCallDataWordBytes := Eip803x.Generated.CallDataLoadOpcodeKernel.readCallDataWordBytes
abbrev zeroWordBytes := Eip803x.Generated.CallDataLoadOpcodeKernel.zeroWordBytes
abbrev tracedPushPayload := Eip803x.Generated.CallDataLoadOpcodeKernel.tracedPushPayload
abbrev reportPush := Eip803x.Generated.CallDataLoadOpcodeKernel.reportPush
abbrev dispatchFinish := Eip803x.Generated.CallDataLoadOpcodeKernel.dispatchFinish
abbrev executeCheckedBody := Eip803x.Generated.CallDataLoadOpcodeKernel.executeCheckedBody
abbrev executeSemantic := Eip803x.Generated.CallDataLoadOpcodeKernel.executeSemantic
abbrev executeAmsterdam := Eip803x.Generated.CallDataLoadOpcodeKernel.executeAmsterdam
abbrev executeAmsterdamClosed := Eip803x.Generated.CallDataLoadOpcodeKernel.executeAmsterdamClosed
abbrev closeFailureTrace := Eip803x.Generated.CallDataLoadOpcodeKernel.closeFailureTrace
abbrev faultPc := Eip803x.Generated.CallDataLoadOpcodeKernel.faultPc
end G

namespace R
abbrev DispatchTable := Eip803x.Evm.CallDataLoadExecution.DispatchTable
abbrev Schedule := Eip803x.Evm.CallDataLoadExecution.Schedule
abbrev Status := Eip803x.Evm.CallDataLoadExecution.Status
abbrev TraceCapabilities := Eip803x.Evm.CallDataLoadExecution.TraceCapabilities
abbrev MachineState := Eip803x.Evm.CallDataLoadExecution.MachineState
abbrev Outcome := Eip803x.Evm.CallDataLoadExecution.Outcome
abbrev tableTracing := Eip803x.Evm.CallDataLoadExecution.DispatchTable.tracing
abbrev tableCancelable := Eip803x.Evm.CallDataLoadExecution.DispatchTable.cancelable
abbrev amsterdamSchedule := Eip803x.Evm.CallDataLoadExecution.Schedule.amsterdam
abbrev beginInstruction := Eip803x.Evm.CallDataLoadExecution.beginInstruction
abbrev replaceTop := Eip803x.Evm.CallDataLoadExecution.replaceTop
abbrev offsetAccessible := Eip803x.Evm.CallDataLoadExecution.offsetAccessible
abbrev productionDomain := Eip803x.Evm.CallDataLoadExecution.productionDomain
abbrev loadedBytes := Eip803x.Evm.CallDataLoadExecution.loadedBytes
abbrev pushPayload := Eip803x.Evm.CallDataLoadExecution.pushPayload
abbrev tracePush := Eip803x.Evm.CallDataLoadExecution.tracePush
abbrev finishInstruction := Eip803x.Evm.CallDataLoadExecution.finishInstruction
abbrev withStatus := Eip803x.Evm.CallDataLoadExecution.withStatus
abbrev executeActive := Eip803x.Evm.CallDataLoadExecution.executeActive
abbrev execute := Eip803x.Evm.CallDataLoadExecution.execute
abbrev closeFailureTrace := Eip803x.Evm.CallDataLoadExecution.closeFailureTrace
abbrev faultPc := Eip803x.Evm.CallDataLoadExecution.faultPc
end R

theorem opcode_count_exact : [G.descriptor].length = 1 := by native_decide

theorem closed_root_count_exact : G.dispatchRoots.length = 4 := by native_decide

theorem opcode_byte_exact : G.descriptor.opcodeByte = 0x35 := by native_decide

theorem fixed_gas_exact : G.amsterdamSchedule.veryLow = 3 := by native_decide

theorem word_width_exact : G.amsterdamSchedule.wordBytes = 32 := by native_decide

theorem table_tracing_refines (table : R.DispatchTable) :
    G.tableTracing table = R.tableTracing table := by
  cases table <;> rfl

theorem table_cancelable_refines (table : R.DispatchTable) :
    G.tableCancelable table = R.tableCancelable table := by
  cases table <;> rfl

theorem dispatch_entry_refines (table : R.DispatchTable) (state : R.MachineState) :
    G.dispatchEnter table state = R.beginInstruction table state := by
  rfl

theorem replace_top_refines (stack : Stack) (value : UInt256) :
    G.replaceTop stack value = R.replaceTop stack value := by
  cases stack with
  | mk words bounded => cases words <;> rfl

theorem offset_accessibility_refines (schedule : R.Schedule) (calldata : List Byte)
    (offset : UInt256) :
    G.accessibleOffset schedule calldata offset = R.offsetAccessible schedule calldata offset := by
  rfl

theorem production_domain_refines (schedule : R.Schedule) (state : R.MachineState) :
    G.productionDomain schedule state ↔ R.productionDomain schedule state := by
  rfl

theorem loaded_bytes_refine (schedule : R.Schedule) (calldata : List Byte) (offset : UInt256) :
    G.readCallDataWordBytes schedule calldata offset = R.loadedBytes schedule calldata offset := by
  rfl

theorem push_payload_refines (schedule : R.Schedule) (calldata : List Byte) (offset : UInt256) :
    G.tracedPushPayload schedule calldata offset = R.pushPayload schedule calldata offset := by
  rfl

theorem report_push_refines (table : R.DispatchTable) (payload : List Byte)
    (state : R.MachineState) :
    G.reportPush table payload state = R.tracePush table payload state := by
  cases table <;> rfl

theorem dispatch_finish_refines (table : R.DispatchTable) (state : R.MachineState) :
    G.dispatchFinish table state = R.finishInstruction table state := by
  cases table <;> rfl

theorem checked_body_refines (schedule : R.Schedule) (table : R.DispatchTable)
    (entered : R.MachineState) :
    G.executeCheckedBody schedule table entered = R.executeActive schedule table entered := by
  rfl

theorem generated_transition_refines (schedule : R.Schedule) (table : R.DispatchTable)
    (state : R.MachineState) :
    G.executeSemantic schedule table state = R.execute schedule table state := by
  rfl

theorem amsterdam_transition_refines (table : R.DispatchTable) (state : R.MachineState) :
    G.executeAmsterdam table state = R.execute R.amsterdamSchedule table state := by
  rfl

theorem outer_failure_closure_refines (table : R.DispatchTable) (outcome : R.Outcome) :
    G.closeFailureTrace table outcome = R.closeFailureTrace table outcome := by
  rfl

theorem closed_amsterdam_refines (table : R.DispatchTable) (state : R.MachineState) :
    G.executeAmsterdamClosed table state =
      R.closeFailureTrace table (R.execute R.amsterdamSchedule table state) := by
  rfl

theorem fault_pc_refines (outcome : R.Outcome) : G.faultPc outcome = R.faultPc outcome := by
  rfl

theorem every_load_is_one_word (schedule : R.Schedule) (calldata : List Byte)
    (offset : UInt256) :
    (G.readCallDataWordBytes schedule calldata offset).length = schedule.wordBytes := by
  change
    (Eip803x.Generated.CallDataLoadOpcodeKernel.readCallDataWordBytes schedule calldata offset).length =
      schedule.wordBytes
  unfold Eip803x.Generated.CallDataLoadOpcodeKernel.readCallDataWordBytes
  split <;> simp [Eip803x.Generated.CallDataLoadOpcodeKernel.zeroWordBytes, readRange_length]

theorem above_uint64_loads_zero (schedule : R.Schedule) (calldata : List Byte) (offset : UInt256)
    (high : schedule.maxUInt64 < offset.val) :
    G.readCallDataWordBytes schedule calldata offset = G.zeroWordBytes schedule := by
  have inaccessible :
      Eip803x.Generated.CallDataLoadOpcodeKernel.accessibleOffset schedule calldata offset = false := by
    simp [Eip803x.Generated.CallDataLoadOpcodeKernel.accessibleOffset, Nat.not_le_of_lt high]
  simp [Eip803x.Generated.CallDataLoadOpcodeKernel.readCallDataWordBytes, inaccessible]

theorem at_or_beyond_end_loads_zero (schedule : R.Schedule) (calldata : List Byte)
    (offset : UInt256) (past : calldata.length ≤ offset.val) :
    G.readCallDataWordBytes schedule calldata offset = G.zeroWordBytes schedule := by
  have inaccessible :
      Eip803x.Generated.CallDataLoadOpcodeKernel.accessibleOffset schedule calldata offset = false := by
    simp [Eip803x.Generated.CallDataLoadOpcodeKernel.accessibleOffset, Nat.not_lt_of_ge past]
  simp [Eip803x.Generated.CallDataLoadOpcodeKernel.readCallDataWordBytes, inaccessible]

theorem inaccessible_push_payload_is_one_zero (schedule : R.Schedule) (calldata : List Byte)
    (offset : UInt256) (inaccessible : G.accessibleOffset schedule calldata offset = false) :
    G.tracedPushPayload schedule calldata offset = [zeroByte] := by
  simp [Eip803x.Generated.CallDataLoadOpcodeKernel.tracedPushPayload, inaccessible]

theorem accessible_push_payload_is_full_word (schedule : R.Schedule) (calldata : List Byte)
    (offset : UInt256) (accessible : G.accessibleOffset schedule calldata offset = true) :
    (G.tracedPushPayload schedule calldata offset).length = schedule.wordBytes := by
  simp [Eip803x.Generated.CallDataLoadOpcodeKernel.tracedPushPayload, accessible,
    every_load_is_one_word]

theorem no_trace_cancelable_same_transition (state : R.MachineState) :
    G.executeAmsterdam .noTrace state = G.executeAmsterdam .noTraceCancelable state := by
  rfl

theorem traced_cancelable_same_opcode_transition (state : R.MachineState) :
    G.executeAmsterdam .traced state = G.executeAmsterdam .tracedCancelable state := by
  rfl

private def vectorCalldata : List Byte := (List.range 40).map MemoryStackControl.byte

private def vectorCaps : R.TraceCapabilities := ⟨true, true, true⟩

private def vectorState (offset : UInt256) (gas : Nat := 20) : R.MachineState :=
  { calldata := vectorCalldata
    memory := [MemoryStackControl.byte 9]
    returnData := [MemoryStackControl.byte 8]
    stack := Stack.fromWords [offset, ofNat 99]
    pc := 7
    opcodeCount := 11
    gasLeft := gas
    traceCapabilities := vectorCaps
    trace := [] }

private def emptyVectorState (gas : Nat := 20) : R.MachineState :=
  { vectorState (ofNat 0) gas with stack := Stack.empty }

theorem vector_offset_zero :
    (G.executeAmsterdam .noTrace (vectorState (ofNat 0))).state.stack.words.head? =
      some (bytesToWord (readRange vectorCalldata 0 32)) := by native_decide

theorem vector_state_is_in_production_domain :
    G.productionDomain G.amsterdamSchedule (vectorState (ofNat 0) 20) := by
  simp [Eip803x.Generated.CallDataLoadOpcodeKernel.productionDomain,
    Eip803x.Generated.CallDataLoadOpcodeKernel.amsterdamSchedule, vectorState, vectorCalldata]

theorem vector_tail_right_zero_pads :
    (G.readCallDataWordBytes G.amsterdamSchedule vectorCalldata (ofNat 39)) =
      [MemoryStackControl.byte 39] ++ List.replicate 31 zeroByte := by native_decide

theorem vector_exact_end_is_zero :
    G.readCallDataWordBytes G.amsterdamSchedule vectorCalldata (ofNat 40) =
      List.replicate 32 zeroByte := by native_decide

theorem vector_beyond_end_is_zero :
    G.readCallDataWordBytes G.amsterdamSchedule vectorCalldata (ofNat 400) =
      List.replicate 32 zeroByte := by native_decide

theorem vector_above_uint64_is_zero :
    G.readCallDataWordBytes G.amsterdamSchedule vectorCalldata (ofNat (2 ^ 64)) =
      List.replicate 32 zeroByte := by native_decide

theorem vector_max_uint256_is_zero :
    G.readCallDataWordBytes G.amsterdamSchedule vectorCalldata allOnes =
      List.replicate 32 zeroByte := by native_decide

theorem vector_one_short_oog :
    (G.executeAmsterdamClosed .traced (vectorState (ofNat 0) 2)).status = .outOfGas := by
  native_decide

theorem vector_one_short_oog_precedes_depth_and_preserves_stack :
    let result := G.executeAmsterdamClosed .noTrace (emptyVectorState 2)
    result.status = .outOfGas ∧ result.state.gasLeft = 0 ∧ result.state.stack.words = [] ∧
      G.faultPc result = 7 := by
  native_decide

theorem vector_underflow_retains_charge_and_stack :
    let result := G.executeAmsterdamClosed .noTrace (emptyVectorState 20)
    result.status = .stackUnderflow ∧ result.state.gasLeft = 17 ∧ result.state.stack.words = [] ∧
      G.faultPc result = 7 := by
  native_decide

theorem vector_success_replaces_top_and_preserves_tail :
    let result := G.executeAmsterdam .noTrace (vectorState (ofNat 0))
    result.status = .ok ∧ result.state.stack.words =
      [bytesToWord (readRange vectorCalldata 0 32), ofNat 99] ∧
      result.state.pc = 8 ∧ result.state.opcodeCount = 12 ∧ result.state.gasLeft = 17 := by
  native_decide

theorem vector_traced_event_order_and_payload :
    (G.executeAmsterdam .traced (vectorState (ofNat 0))).state.trace =
      [.start .calldataload 7 20,
       .operationMemory [MemoryStackControl.byte 9],
       .operationMemorySize 1,
       .operationStack [ofNat 99, ofNat 0],
       .operationReturnData [MemoryStackControl.byte 8],
       .stackPush (readRange vectorCalldata 0 32),
       .finish 17] := by
  native_decide

theorem vector_traced_stack_payload_is_bottom_first :
    (G.executeAmsterdam .traced (vectorState (ofNat 0))).state.trace[3]? =
      some (.operationStack [ofNat 99, ofNat 0]) := by
  native_decide

theorem vector_traced_underflow_closes_finish_before_error :
    (G.executeAmsterdamClosed .traced (emptyVectorState 20)).state.trace =
      [.start .calldataload 7 20,
       .operationMemory [MemoryStackControl.byte 9],
       .operationMemorySize 1,
       .operationStack [],
       .operationReturnData [MemoryStackControl.byte 8],
       .finish 17,
       .error .stackUnderflow] := by
  native_decide

theorem vector_traced_payload_at_end_is_single_zero :
    (G.tracedPushPayload G.amsterdamSchedule vectorCalldata (ofNat 40)) = [zeroByte] := by
  native_decide

end Eip803x.Generated.CallDataLoadOpcodeRefinement
