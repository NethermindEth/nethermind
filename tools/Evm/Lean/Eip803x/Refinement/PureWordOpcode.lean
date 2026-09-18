-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Generated.PureWordOpcodeKernel
import Eip803x.Evm.PureStackExecution

/-!
  Refinement from the generated pure-word production profile to the accepted
  handwritten execution model.  The generated file contains definitions only;
  every proof and every interpretation of its source-derived operation tags is
  kept here.
-/

namespace Eip803x.Refinement.PureWordOpcode

open Eip803x
open Eip803x.Evm
open Eip803x.Evm.MemoryStackControl

namespace G

abbrev Opcode := Eip803x.Generated.PureWordOpcodeKernel.Opcode
abbrev Schedule := Eip803x.Generated.PureWordOpcodeKernel.Schedule
abbrev State := Eip803x.Generated.PureWordOpcodeKernel.State
abbrev Error := Eip803x.Generated.PureWordOpcodeKernel.Error
abbrev Outcome := Eip803x.Generated.PureWordOpcodeKernel.Outcome
abbrev Debit := Eip803x.Generated.PureWordOpcodeKernel.Debit
abbrev allOpcodes := Eip803x.Generated.PureWordOpcodeKernel.allOpcodes
abbrev opcodeByte := Eip803x.Generated.PureWordOpcodeKernel.opcodeByte
abbrev stackInputs := Eip803x.Generated.PureWordOpcodeKernel.stackInputs
abbrev amsterdamSchedule := Eip803x.Generated.PureWordOpcodeKernel.amsterdamSchedule
abbrev staticCost := Eip803x.Generated.PureWordOpcodeKernel.staticCost
abbrev exponentByteLength := Eip803x.Generated.PureWordOpcodeKernel.exponentByteLength
abbrev expDynamicCost := Eip803x.Generated.PureWordOpcodeKernel.expDynamicCost
abbrev stackExecute := Eip803x.Generated.PureWordOpcodeKernel.stackExecute
abbrev debitExecution := Eip803x.Generated.PureWordOpcodeKernel.debitExecution
abbrev executeExpAfterStatic := Eip803x.Generated.PureWordOpcodeKernel.executeExpAfterStatic
abbrev execute := Eip803x.Generated.PureWordOpcodeKernel.execute
abbrev unary := Eip803x.Generated.PureWordOpcodeKernel.unary
abbrev binary := Eip803x.Generated.PureWordOpcodeKernel.binary
abbrev ternary := Eip803x.Generated.PureWordOpcodeKernel.ternary

namespace State
abbrev withGasLeft := Eip803x.Generated.PureWordOpcodeKernel.State.withGasLeft
abbrev advancePc := Eip803x.Generated.PureWordOpcodeKernel.State.advancePc
end State

end G

namespace P

abbrev Schedule := Eip803x.Evm.PureStackExecution.Schedule
abbrev State := Eip803x.Evm.PureStackExecution.State
abbrev Error := Eip803x.Evm.PureStackExecution.Error
abbrev Outcome := Eip803x.Evm.PureStackExecution.Outcome
abbrev Debit := Eip803x.Evm.PureStackExecution.Debit
abbrev staticCost := Eip803x.Evm.PureStackExecution.staticCost
abbrev exponentByteLength := Eip803x.Evm.PureStackExecution.exponentByteLength
abbrev expDynamicCost := Eip803x.Evm.PureStackExecution.expDynamicCost
abbrev stackExecute := Eip803x.Evm.PureStackExecution.stackExecute
abbrev debitExecution := Eip803x.Evm.PureStackExecution.debitExecution
abbrev executeExpAfterStatic := Eip803x.Evm.PureStackExecution.executeExpAfterStatic
abbrev execute := Eip803x.Evm.PureStackExecution.execute
abbrev unary := Eip803x.Evm.PureStackExecution.unary
abbrev binary := Eip803x.Evm.PureStackExecution.binary
abbrev ternary := Eip803x.Evm.PureStackExecution.ternary

namespace Schedule
abbrev amsterdam := Eip803x.Evm.PureStackExecution.Schedule.amsterdam
end Schedule

namespace State
abbrev withGasLeft := Eip803x.Evm.PureStackExecution.State.withGasLeft
abbrev advancePc := Eip803x.Evm.PureStackExecution.State.advancePc
end State

end P

namespace S

abbrev Opcode := Eip803x.Evm.PureStack.Opcode
abbrev allOpcodes := Eip803x.Evm.PureStack.allOpcodes
abbrev arity := Eip803x.Evm.PureStack.arity

end S

def toSpecOpcode : G.Opcode → S.Opcode
  | .add => .add
  | .mul => .mul
  | .sub => .sub
  | .div => .div
  | .sdiv => .sdiv
  | .mod => .mod
  | .smod => .smod
  | .addmod => .addmod
  | .mulmod => .mulmod
  | .exp => .exp
  | .signextend => .signextend
  | .lt => .lt
  | .gt => .gt
  | .slt => .slt
  | .sgt => .sgt
  | .eq => .eq
  | .iszero => .iszero
  | .and => .and
  | .or => .or
  | .xor => .xor
  | .not => .not
  | .byte => .byte
  | .shl => .shl
  | .shr => .shr
  | .sar => .sar
  | .clz => .clz

def specOpcodeByte : S.Opcode → Nat
  | .add => 0x01
  | .mul => 0x02
  | .sub => 0x03
  | .div => 0x04
  | .sdiv => 0x05
  | .mod => 0x06
  | .smod => 0x07
  | .addmod => 0x08
  | .mulmod => 0x09
  | .exp => 0x0a
  | .signextend => 0x0b
  | .lt => 0x10
  | .gt => 0x11
  | .slt => 0x12
  | .sgt => 0x13
  | .eq => 0x14
  | .iszero => 0x15
  | .and => 0x16
  | .or => 0x17
  | .xor => 0x18
  | .not => 0x19
  | .byte => 0x1a
  | .shl => 0x1b
  | .shr => 0x1c
  | .sar => 0x1d
  | .clz => 0x1e

def toSpecSchedule (schedule : G.Schedule) : P.Schedule :=
  { veryLow := schedule.veryLow
    low := schedule.low
    mid := schedule.mid
    expBase := schedule.expBase
    expByte := schedule.expByte }

def toSpecState (state : G.State) : P.State :=
  { stack := state.stack, gas := state.gas, pc := state.pc }

def toSpecError : G.Error → P.Error
  | .outOfGas => .outOfGas
  | .stackUnderflow => .stackUnderflow

def toSpecOutcome : G.Outcome → P.Outcome
  | .success state => .success (toSpecState state)
  | .error reason state => .error (toSpecError reason) (toSpecState state)

def toSpecDebit : G.Debit → P.Debit
  | .paid state => .paid (toSpecState state)
  | .outOfGas state => .outOfGas (toSpecState state)

theorem opcode_count : G.allOpcodes.length = 26 := by native_decide

theorem opcode_mapping_complete : G.allOpcodes.map toSpecOpcode = S.allOpcodes := by rfl

theorem opcode_byte_identity (opcode : G.Opcode) :
    G.opcodeByte opcode = specOpcodeByte (toSpecOpcode opcode) := by
  cases opcode <;> rfl

theorem opcode_byte_injective : Function.Injective G.opcodeByte := by
  intro left right equalBytes
  cases left <;> cases right <;>
    simp_all [Eip803x.Generated.PureWordOpcodeKernel.opcodeByte]

theorem amsterdam_schedule_refines : toSpecSchedule G.amsterdamSchedule = P.Schedule.amsterdam := by rfl

theorem stack_inputs_refine (opcode : G.Opcode) :
    G.stackInputs opcode = S.arity (toSpecOpcode opcode) := by
  cases opcode <;> rfl

theorem static_cost_refines (schedule : G.Schedule) (opcode : G.Opcode) :
    G.staticCost schedule opcode = P.staticCost (toSpecSchedule schedule) (toSpecOpcode opcode) := by
  cases opcode <;> rfl

theorem exponent_byte_length_refines (exponent : UInt256) :
    G.exponentByteLength exponent = P.exponentByteLength exponent := by rfl

theorem exp_dynamic_cost_refines (schedule : G.Schedule) (exponent : UInt256) :
    G.expDynamicCost schedule exponent = P.expDynamicCost (toSpecSchedule schedule) exponent := by rfl

theorem stack_execute_refines (opcode : G.Opcode) (stack : Stack) :
    G.stackExecute opcode stack = P.stackExecute (toSpecOpcode opcode) stack := by
  cases opcode <;> rfl

theorem stack_outcome_refines (opcode : G.Opcode) (state : G.State) :
    toSpecOutcome
        (match G.stackExecute opcode state.stack with
         | none => .error .stackUnderflow state
         | some finalStack => .success { state with stack := finalStack }) =
      match P.stackExecute (toSpecOpcode opcode) (toSpecState state).stack with
      | none => .error .stackUnderflow (toSpecState state)
      | some finalStack => .success { toSpecState state with stack := finalStack } := by
  rw [stack_execute_refines]
  simp only [toSpecState]
  generalize hStack : P.stackExecute (toSpecOpcode opcode) state.stack = result
  cases result <;> simp [toSpecOutcome, toSpecError, toSpecState]

theorem with_gas_left_refines (state : G.State) (gasLeft : Nat) :
    toSpecState (G.State.withGasLeft state gasLeft) = P.State.withGasLeft (toSpecState state) gasLeft := by
  cases state
  rfl

theorem advance_pc_refines (state : G.State) :
    toSpecState (G.State.advancePc state) = P.State.advancePc (toSpecState state) := by
  cases state
  rfl

theorem debit_execution_refines (cost : Nat) (state : G.State) :
    toSpecDebit (G.debitExecution cost state) = P.debitExecution cost (toSpecState state) := by
  rcases state with ⟨stack, gas, pc⟩
  by_cases h : cost ≤ gas.gasLeft
  · simp [Eip803x.Generated.PureWordOpcodeKernel.debitExecution,
      Eip803x.Evm.PureStackExecution.debitExecution, h, toSpecDebit, toSpecState,
      Eip803x.Generated.PureWordOpcodeKernel.State.withGasLeft,
      Eip803x.Evm.PureStackExecution.State.withGasLeft]
  · simp [Eip803x.Generated.PureWordOpcodeKernel.debitExecution,
      Eip803x.Evm.PureStackExecution.debitExecution, h, toSpecDebit, toSpecState,
      Eip803x.Generated.PureWordOpcodeKernel.State.withGasLeft,
      Eip803x.Evm.PureStackExecution.State.withGasLeft]

theorem execute_exp_after_static_refines (schedule : G.Schedule) (state : G.State) :
    toSpecOutcome (G.executeExpAfterStatic schedule state) =
      P.executeExpAfterStatic (toSpecSchedule schedule) (toSpecState state) := by
  rcases state with ⟨stack, gas, pc⟩
  rcases stack with ⟨words, hBound⟩
  cases words with
  | nil => rfl
  | cons base tail =>
    cases tail with
    | nil => rfl
    | cons exponent rest =>
      have hTail : rest.length ≤ stackLimit := by
        simp only [List.length_cons] at hBound
        omega
      have hResult : (Word.exp base exponent :: rest).length ≤ stackLimit := by
        simp only [List.length_cons] at hBound ⊢
        omega
      let poppedStack : Stack := ⟨rest, hTail⟩
      let resultStack : Stack := ⟨Word.exp base exponent :: rest, hResult⟩
      let popped : G.State := { stack := poppedStack, gas := gas, pc := pc }
      change toSpecOutcome
          (match G.debitExecution (G.expDynamicCost schedule exponent) popped with
           | .outOfGas afterOutOfGas => .error .outOfGas afterOutOfGas
           | .paid afterDynamic => .success { afterDynamic with stack := resultStack }) =
        match P.debitExecution (G.expDynamicCost schedule exponent) (toSpecState popped) with
        | .outOfGas afterOutOfGas => .error .outOfGas afterOutOfGas
        | .paid afterDynamic => .success { afterDynamic with stack := resultStack }
      generalize hDebit : G.debitExecution (G.expDynamicCost schedule exponent) popped = debit
      have hDebitRefines := debit_execution_refines (G.expDynamicCost schedule exponent) popped
      rw [hDebit] at hDebitRefines
      cases debit with
      | outOfGas afterOutOfGas =>
        simp only [toSpecDebit] at hDebitRefines
        rw [← hDebitRefines]
        rfl
      | paid afterDynamic =>
        simp only [toSpecDebit] at hDebitRefines
        rw [← hDebitRefines]
        rfl

theorem after_static_refines (schedule : G.Schedule) (opcode : G.Opcode) (state : G.State) :
    toSpecOutcome
        (match opcode with
         | .exp => G.executeExpAfterStatic schedule state
         | _ =>
           match G.stackExecute opcode state.stack with
           | none => .error .stackUnderflow state
           | some finalStack => .success { state with stack := finalStack }) =
      match toSpecOpcode opcode with
      | .exp => P.executeExpAfterStatic (toSpecSchedule schedule) (toSpecState state)
      | _ =>
        match P.stackExecute (toSpecOpcode opcode) (toSpecState state).stack with
        | none => .error .stackUnderflow (toSpecState state)
        | some finalStack => .success { toSpecState state with stack := finalStack } := by
  cases opcode
  case exp => exact execute_exp_after_static_refines schedule state
  all_goals exact stack_outcome_refines _ state

/--
  The extracted, theorem-free opcode machine has exactly the same single-step
  stack/gas/program-counter state and status as the handwritten reference for
  all 26 opcodes.
-/
theorem extracted_execute_refines (schedule : G.Schedule) (opcode : G.Opcode) (state : G.State) :
    toSpecOutcome (G.execute schedule opcode state) =
      P.execute (toSpecSchedule schedule) (toSpecOpcode opcode) (toSpecState state) := by
  change toSpecOutcome
      (Eip803x.Generated.PureWordOpcodeKernel.execute schedule opcode state) =
    Eip803x.Evm.PureStackExecution.execute
      (toSpecSchedule schedule) (toSpecOpcode opcode) (toSpecState state)
  unfold Eip803x.Generated.PureWordOpcodeKernel.execute
  unfold Eip803x.Evm.PureStackExecution.execute
  have hCost := static_cost_refines schedule opcode
  change Eip803x.Generated.PureWordOpcodeKernel.staticCost schedule opcode =
    Eip803x.Evm.PureStackExecution.staticCost (toSpecSchedule schedule) (toSpecOpcode opcode) at hCost
  conv =>
    rhs
    rw [← hCost]
  have hAdvance := advance_pc_refines state
  change toSpecState (Eip803x.Generated.PureWordOpcodeKernel.State.advancePc state) =
    Eip803x.Evm.PureStackExecution.State.advancePc (toSpecState state) at hAdvance
  conv =>
    rhs
    rw [← hAdvance]
  dsimp only
  generalize hDebit :
    Eip803x.Generated.PureWordOpcodeKernel.debitExecution
      (Eip803x.Generated.PureWordOpcodeKernel.staticCost schedule opcode)
      (Eip803x.Generated.PureWordOpcodeKernel.State.advancePc state) = debit
  have hDebitRefines := debit_execution_refines
    (G.staticCost schedule opcode)
    (Eip803x.Generated.PureWordOpcodeKernel.State.advancePc state)
  change toSpecDebit
      (Eip803x.Generated.PureWordOpcodeKernel.debitExecution
        (Eip803x.Generated.PureWordOpcodeKernel.staticCost schedule opcode)
        (Eip803x.Generated.PureWordOpcodeKernel.State.advancePc state)) =
    Eip803x.Evm.PureStackExecution.debitExecution
      (Eip803x.Generated.PureWordOpcodeKernel.staticCost schedule opcode)
      (toSpecState (Eip803x.Generated.PureWordOpcodeKernel.State.advancePc state)) at hDebitRefines
  rw [hDebit] at hDebitRefines
  cases debit with
  | outOfGas afterOutOfGas =>
    simp only [toSpecDebit] at hDebitRefines
    rw [← hDebitRefines]
    rfl
  | paid afterStatic =>
    simp only [toSpecDebit] at hDebitRefines
    rw [← hDebitRefines]
    exact after_static_refines schedule opcode afterStatic

end Eip803x.Refinement.PureWordOpcode
