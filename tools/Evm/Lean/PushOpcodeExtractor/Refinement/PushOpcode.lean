-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import PushOpcodeExtractor.Generated.PushOpcodeKernel
import PushOpcodeExtractor.Specification.PushOpcode

namespace PushOpcodeExtractor.Refinement.PushOpcode

open Eip803x.Evm
open PushOpcodeExtractor.Specification.PushTypes

namespace G
open PushOpcodeExtractor.Generated.PushOpcodeKernel
end G

namespace R
open PushOpcodeExtractor.Specification.PushOpcode
end R

theorem opcode_count_exact :
    PushOpcodeExtractor.Generated.PushOpcodeKernel.opcodes.length = 33 := by native_decide

theorem root_count_exact :
    PushOpcodeExtractor.Generated.PushOpcodeKernel.dispatchRoots.length = 132 := by native_decide

theorem opcode_bytes_exact :
    PushOpcodeExtractor.Generated.PushOpcodeKernel.opcodes.map (·.opcodeByte) =
      (List.range 33).map (0x5f + ·) := by native_decide

theorem opcode_bytes_unique :
    (PushOpcodeExtractor.Generated.PushOpcodeKernel.opcodes.map (·.opcodeByte)).Nodup := by native_decide

theorem root_keys_unique :
    (PushOpcodeExtractor.Generated.PushOpcodeKernel.dispatchRoots.map fun root =>
      (root.opcode, root.table)).Nodup := by native_decide

theorem every_opcode_has_four_roots :
    PushOpcodeExtractor.Generated.PushOpcodeKernel.opcodes.all (fun opcode =>
      decide ((PushOpcodeExtractor.Generated.PushOpcodeKernel.dispatchRoots.filter
        (fun root => root.opcode = opcode.name)).length = 4)) = true := by native_decide

theorem every_root_is_closed_standard_mainnet :
    PushOpcodeExtractor.Generated.PushOpcodeKernel.dispatchRoots.all (fun root =>
      root.closedRoot.startsWith "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<") = true := by native_decide

theorem immediate_width_agrees (raw : Nat) :
    PushOpcodeExtractor.Generated.PushOpcodeKernel.immediateWidth raw =
      PushOpcodeExtractor.Specification.PushOpcode.immediateWidth raw := by
  rfl

theorem fixed_gas_agrees (raw : Nat) :
    PushOpcodeExtractor.Generated.PushOpcodeKernel.fixedGas raw =
      PushOpcodeExtractor.Specification.PushOpcode.fixedGas raw := by
  rfl

theorem push_value_agrees_with_memory_stack_reference (width : Nat) (state : MachineState) :
    PushOpcodeExtractor.Specification.PushOpcode.immediateValue width state =
      MemoryStackControl.bytesToWord (MemoryStackControl.readRange state.code state.pc width) := rfl

theorem immediate_read_has_declared_width (width : Nat) (state : MachineState) :
    (MemoryStackControl.readRange state.code state.pc width).length = width :=
  MemoryStackControl.readRange_length state.code state.pc width

theorem missing_immediate_byte_is_zero_extended (code : List MemoryStackControl.Byte) (offset : Nat)
    (h : code.length ≤ offset) :
    MemoryStackControl.readByte code offset = MemoryStackControl.zeroByte :=
  MemoryStackControl.readByte_zero_extended code offset h

theorem generated_push_at_pc_agrees (width : Nat) (state : MachineState) :
    PushOpcodeExtractor.Generated.PushOpcodeKernel.pushAtPc width state =
      PushOpcodeExtractor.Specification.PushOpcode.pushAtPc width state := by
  rfl

theorem generated_checked_push_agrees (tracing : Bool) (width gas : Nat) (state : MachineState) :
    PushOpcodeExtractor.Generated.PushOpcodeKernel.executeCheckedPush tracing width gas state =
      PushOpcodeExtractor.Specification.PushOpcode.executeCheckedPush tracing width gas state := by
  rfl

theorem generated_finish_fused_jump_agrees (validJumpDestination : Nat → Bool) (state : MachineState) :
    PushOpcodeExtractor.Generated.PushOpcodeKernel.finishFusedJump validJumpDestination state =
      PushOpcodeExtractor.Specification.PushOpcode.finishFusedJump validJumpDestination state := by
  rfl

theorem generated_fused_jump_agrees (validJumpDestination : Nat → Bool) (state : MachineState) :
    PushOpcodeExtractor.Generated.PushOpcodeKernel.executeFusedJump validJumpDestination state =
      PushOpcodeExtractor.Specification.PushOpcode.executeFusedJump validJumpDestination state := by
  rfl

theorem generated_fused_jumpi_agrees (validJumpDestination : Nat → Bool) (state : MachineState) :
    PushOpcodeExtractor.Generated.PushOpcodeKernel.executeFusedJumpi validJumpDestination state =
      PushOpcodeExtractor.Specification.PushOpcode.executeFusedJumpi validJumpDestination state := by
  rfl

theorem generated_push2_agrees (tracing : Bool) (validJumpDestination : Nat → Bool) (state : MachineState) :
    PushOpcodeExtractor.Generated.PushOpcodeKernel.executePush2 tracing validJumpDestination state =
      PushOpcodeExtractor.Specification.PushOpcode.executePush2 tracing validJumpDestination state := by
  rfl

theorem generated_execute_agrees (tracing : Bool) (validJumpDestination : Nat → Bool) (state : MachineState) :
    PushOpcodeExtractor.Generated.PushOpcodeKernel.execute tracing validJumpDestination state =
      PushOpcodeExtractor.Specification.PushOpcode.execute tracing validJumpDestination state := by
  rfl

private theorem stack_push_then_pop (stack : MemoryStackControl.Stack) (value : Eip803x.UInt256)
    (hRoom : stack.words.length < MemoryStackControl.stackLimit) :
    ∃ pushed, stack.push value = some pushed ∧ pushed.pop = some (value, stack) := by
  simp [MemoryStackControl.Stack.push, hRoom, MemoryStackControl.Stack.pop]

theorem push2_jump_fusion_matches_ordinary_execution
    (validJumpDestination : Nat → Bool) (entered : MachineState)
    (hGas : 12 ≤ entered.gasLeft)
    (hRoom : entered.stack.words.length < MemoryStackControl.stackLimit)
    (hCode : 2 < entered.code.length - entered.pc)
    (hNext : (MemoryStackControl.readByte entered.code (entered.pc + 2)).val = 0x56)
    (hValid : validJumpDestination
      (PushOpcodeExtractor.Specification.PushOpcode.immediateValue 2 entered).val = true) :
    PushOpcodeExtractor.Specification.PushOpcode.executePush2 false validJumpDestination entered =
      PushOpcodeExtractor.Specification.PushOpcode.executeOrdinaryPush2Jump validJumpDestination entered := by
  have hPushGas : 3 ≤ entered.gasLeft := by omega
  have hJumpGas : 8 ≤ entered.gasLeft - 3 := by omega
  have hJumpdestGas : 1 ≤ entered.gasLeft - 3 - 8 := by omega
  obtain ⟨pushed, hPush, hPop⟩ := stack_push_then_pop entered.stack
    (PushOpcodeExtractor.Specification.PushOpcode.immediateValue 2 entered) hRoom
  simp_all [PushOpcodeExtractor.Specification.PushOpcode.executePush2,
    PushOpcodeExtractor.Specification.PushOpcode.executeOrdinaryPush2Jump,
    PushOpcodeExtractor.Specification.PushOpcode.executeFusedJump,
    PushOpcodeExtractor.Specification.PushOpcode.finishFusedJump,
    PushOpcodeExtractor.Specification.PushOpcode.finishOrdinaryJump,
    PushOpcodeExtractor.Specification.PushOpcode.push2Destination,
    PushOpcodeExtractor.Specification.PushOpcode.immediateValue,
    PushOpcodeExtractor.Specification.PushOpcode.charge?,
    PushOpcodeExtractor.Specification.PushOpcode.stackLimit,
    PushOpcodeExtractor.Specification.PushOpcode.jumpByte,
    PushOpcodeExtractor.Specification.PushOpcode.enterOpcode,
    PushOpcodeExtractor.Specification.PushOpcode.finish,
    MemoryStackControl.Stack.push,
    MemoryStackControl.Stack.pop]
  all_goals repeat' first | split | simp_all [Nat.add_assoc] | omega

theorem push2_jumpi_fusion_matches_ordinary_execution
    (validJumpDestination : Nat → Bool) (entered : MachineState)
    (condition : Eip803x.UInt256) (rest : MemoryStackControl.Stack)
    (hGas : 14 ≤ entered.gasLeft)
    (hRoom : entered.stack.words.length < MemoryStackControl.stackLimit)
    (hCode : 2 < entered.code.length - entered.pc)
    (hNext : (MemoryStackControl.readByte entered.code (entered.pc + 2)).val = 0x57)
    (hCondition : entered.stack.pop = some (condition, rest))
    (hValid : validJumpDestination
      (PushOpcodeExtractor.Specification.PushOpcode.immediateValue 2 entered).val = true) :
    PushOpcodeExtractor.Specification.PushOpcode.executePush2 false validJumpDestination entered =
      PushOpcodeExtractor.Specification.PushOpcode.executeOrdinaryPush2Jumpi validJumpDestination entered := by
  have hPushGas : 3 ≤ entered.gasLeft := by omega
  have hJumpiGas : 10 ≤ entered.gasLeft - 3 := by omega
  have hJumpdestGas : 1 ≤ entered.gasLeft - 3 - 10 := by omega
  obtain ⟨pushed, hPush, hPop⟩ := stack_push_then_pop entered.stack
    (PushOpcodeExtractor.Specification.PushOpcode.immediateValue 2 entered) hRoom
  by_cases hZero : condition.val = 0
  all_goals simp_all [PushOpcodeExtractor.Specification.PushOpcode.executePush2,
    PushOpcodeExtractor.Specification.PushOpcode.executeOrdinaryPush2Jumpi,
    PushOpcodeExtractor.Specification.PushOpcode.executeFusedJumpi,
    PushOpcodeExtractor.Specification.PushOpcode.finishFusedJump,
    PushOpcodeExtractor.Specification.PushOpcode.finishOrdinaryJump,
    PushOpcodeExtractor.Specification.PushOpcode.push2Destination,
    PushOpcodeExtractor.Specification.PushOpcode.immediateValue,
    PushOpcodeExtractor.Specification.PushOpcode.charge?,
    PushOpcodeExtractor.Specification.PushOpcode.stackLimit,
    PushOpcodeExtractor.Specification.PushOpcode.jumpByte,
    PushOpcodeExtractor.Specification.PushOpcode.jumpiByte,
    PushOpcodeExtractor.Specification.PushOpcode.enterOpcode,
    PushOpcodeExtractor.Specification.PushOpcode.finish,
    MemoryStackControl.Stack.push,
    MemoryStackControl.Stack.pop]
  all_goals repeat' first | split | simp_all [Nat.add_assoc] | omega

theorem out_of_gas_precedes_stack_overflow (tracing : Bool) (width gas : Nat) (state : MachineState)
    (hGas : state.gasLeft < gas) :
    (PushOpcodeExtractor.Specification.PushOpcode.executeCheckedPush tracing width gas state).status = .outOfGas := by
  simp [PushOpcodeExtractor.Specification.PushOpcode.executeCheckedPush,
    PushOpcodeExtractor.Specification.PushOpcode.charge?, Nat.not_le_of_lt hGas,
    PushOpcodeExtractor.Specification.PushOpcode.outOfGas,
    PushOpcodeExtractor.Specification.PushOpcode.finish]

theorem push2_out_of_gas_precedes_overflow_and_fusion
    (tracing : Bool) (validJumpDestination : Nat → Bool) (state : MachineState)
    (hGas : state.gasLeft < 3) :
    (PushOpcodeExtractor.Specification.PushOpcode.executePush2 tracing validJumpDestination state).status =
      .outOfGas := by
  simp [PushOpcodeExtractor.Specification.PushOpcode.executePush2,
    PushOpcodeExtractor.Specification.PushOpcode.charge?, Nat.not_le_of_lt hGas,
    PushOpcodeExtractor.Specification.PushOpcode.outOfGas,
    PushOpcodeExtractor.Specification.PushOpcode.finish]

theorem push_at_pc_always_advances_declared_immediate_width (width : Nat) (state : MachineState) :
    (PushOpcodeExtractor.Specification.PushOpcode.pushAtPc width state).state.pc = state.pc + width := by
  unfold PushOpcodeExtractor.Specification.PushOpcode.pushAtPc
  split <;> rfl

theorem untraced_terminal_push_elides_unobservable_stack_write
    (width gas : Nat) (state : MachineState)
    (hGas : gas ≤ state.gasLeft)
    (hRoom : state.stack.words.length < MemoryStackControl.stackLimit)
    (hTerminal : state.code.length ≤ state.pc + width) :
    PushOpcodeExtractor.Specification.PushOpcode.executeCheckedPush false width gas state =
    PushOpcodeExtractor.Specification.PushOpcode.finish .success
        { state with gasLeft := state.gasLeft - gas, pc := state.pc + width } := by
  have hNotFull : ¬ MemoryStackControl.stackLimit ≤ state.stack.words.length := Nat.not_le_of_lt hRoom
  simp [PushOpcodeExtractor.Specification.PushOpcode.executeCheckedPush,
    PushOpcodeExtractor.Specification.PushOpcode.charge?, hGas,
    PushOpcodeExtractor.Specification.PushOpcode.stackLimit, hNotFull, hTerminal]

theorem untraced_terminal_push2_checks_overflow_then_overshoots_pc
    (validJumpDestination : Nat → Bool) (state : MachineState)
    (hGas : 3 ≤ state.gasLeft)
    (hRoom : state.stack.words.length < MemoryStackControl.stackLimit)
    (hTerminal : state.code.length - state.pc ≤ 2) :
    PushOpcodeExtractor.Specification.PushOpcode.executePush2 false validJumpDestination state =
    PushOpcodeExtractor.Specification.PushOpcode.finish .success
        { state with gasLeft := state.gasLeft - 3, pc := state.pc + 2 } := by
  have hNotFull : ¬ MemoryStackControl.stackLimit ≤ state.stack.words.length := Nat.not_le_of_lt hRoom
  simp [PushOpcodeExtractor.Specification.PushOpcode.executePush2,
    PushOpcodeExtractor.Specification.PushOpcode.charge?, hGas,
    PushOpcodeExtractor.Specification.PushOpcode.stackLimit, hNotFull, hTerminal]

end PushOpcodeExtractor.Refinement.PushOpcode
