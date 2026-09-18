-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import PushOpcodeExtractor.Specification.PushTypes

namespace PushOpcodeExtractor.Specification.PushOpcode

open Eip803x.Evm
open PushOpcodeExtractor.Specification.PushTypes

def push0Byte : Nat := 0x5f
def push32Byte : Nat := 0x7f
def jumpByte : Nat := 0x56
def jumpiByte : Nat := 0x57
def stackLimit : Nat := MemoryStackControl.stackLimit

def immediateWidth (raw : Nat) : Option Nat :=
  if push0Byte ≤ raw ∧ raw ≤ push32Byte then some (raw - push0Byte) else none

def fixedGas (raw : Nat) : Option Nat :=
  if raw = push0Byte then some 2
  else if push0Byte < raw ∧ raw ≤ push32Byte then some 3
  else none

def finish (status : ExecStatus) (state : MachineState) : Outcome := { status, state }

def enterOpcode (state : MachineState) : MachineState :=
  { state with pc := state.pc + 1, opcodeCount := state.opcodeCount + 1 }

def charge? (amount : Nat) (state : MachineState) : Option MachineState :=
  if amount ≤ state.gasLeft then some { state with gasLeft := state.gasLeft - amount } else none

def outOfGas (state : MachineState) : Outcome :=
  finish .outOfGas { state with gasLeft := 0 }

def immediateValue (width : Nat) (state : MachineState) : Eip803x.UInt256 :=
  MemoryStackControl.bytesToWord (MemoryStackControl.readRange state.code state.pc width)

def pushAtPc (width : Nat) (state : MachineState) : Outcome :=
  match state.stack.push (immediateValue width state) with
  | none => finish .stackOverflow { state with pc := state.pc + width }
  | some stack => finish .success { state with stack := stack, pc := state.pc + width }

def executeCheckedPush (tracing : Bool) (width gas : Nat) (entered : MachineState) : Outcome :=
  match charge? gas entered with
  | none => outOfGas entered
  | some charged =>
    if charged.stack.words.length ≥ stackLimit then
      finish .stackOverflow { charged with pc := if tracing then charged.pc + width else charged.pc }
    else if !tracing && charged.pc + width ≥ charged.code.length then
      finish .success { charged with pc := charged.pc + width }
    else
      pushAtPc width charged

def push2Destination (state : MachineState) : Nat := (immediateValue 2 state).val

def finishFusedJump (validJumpDestination : Nat → Bool) (state : MachineState) : Outcome :=
  let target := push2Destination state
  if validJumpDestination target then
    let atDestination := { state with pc := target + 1, opcodeCount := state.opcodeCount + 1 }
    match charge? 1 atDestination with
    | none => outOfGas atDestination
    | some charged => finish .success charged
  else
    finish .invalidJump state

def executeFusedJump (validJumpDestination : Nat → Bool) (state : MachineState) : Outcome :=
  let counted := { state with opcodeCount := state.opcodeCount + 1 }
  match charge? 8 counted with
  | none => outOfGas counted
  | some charged => finishFusedJump validJumpDestination charged

def executeFusedJumpi (validJumpDestination : Nat → Bool) (state : MachineState) : Outcome :=
  let counted := { state with opcodeCount := state.opcodeCount + 1 }
  match charge? 10 counted with
  | none => outOfGas counted
  | some charged =>
    match charged.stack.pop with
    | none => finish .stackUnderflow charged
    | some (condition, stack) =>
      let popped := { charged with stack := stack }
      if condition.val = 0 then finish .success { popped with pc := popped.pc + 3 }
      else finishFusedJump validJumpDestination popped

def executePush2 (tracing : Bool) (validJumpDestination : Nat → Bool) (entered : MachineState) : Outcome :=
  match charge? 3 entered with
  | none => outOfGas entered
  | some charged =>
    if tracing then pushAtPc 2 charged
    else if charged.stack.words.length ≥ stackLimit then
      finish .stackOverflow { charged with pc := charged.pc + 2 }
    else if charged.code.length - charged.pc ≤ 2 then
      finish .success { charged with pc := charged.pc + 2 }
    else
      let next := (MemoryStackControl.readByte charged.code (charged.pc + 2)).val
      if next = jumpByte then executeFusedJump validJumpDestination charged
      else if next = jumpiByte then executeFusedJumpi validJumpDestination charged
      else pushAtPc 2 charged

def execute (tracing : Bool) (validJumpDestination : Nat → Bool) (state : MachineState) : Outcome :=
  let raw := (MemoryStackControl.readByte state.code state.pc).val
  match immediateWidth raw, fixedGas raw with
  | some width, some gas =>
    let entered := enterOpcode state
    if width = 2 then executePush2 tracing validJumpDestination entered
    else executeCheckedPush tracing width gas entered
  | _, _ => finish .unmodeledOpcode state

def finishOrdinaryJump (validJumpDestination : Nat → Bool) (destination : Eip803x.UInt256)
    (state : MachineState) : Outcome :=
  if validJumpDestination destination.val then
    let atDestination := { state with pc := destination.val + 1, opcodeCount := state.opcodeCount + 1 }
    match charge? 1 atDestination with
    | none => outOfGas atDestination
    | some charged => finish .success charged
  else
    finish .invalidJump state

/--
The ordinary untraced `PUSH2; JUMP` execution. Unlike the optimized production
path, this definition materializes the immediate on the stack and the following
JUMP independently pops it.
-/
def executeOrdinaryPush2Jump (validJumpDestination : Nat → Bool) (entered : MachineState) : Outcome :=
  match charge? 3 entered with
  | none => outOfGas entered
  | some pushCharged =>
    let destination := immediateValue 2 pushCharged
    match pushCharged.stack.push destination with
    | none => finish .stackOverflow { pushCharged with pc := pushCharged.pc + 2 }
    | some pushedStack =>
      let afterPush := { pushCharged with stack := pushedStack, pc := pushCharged.pc + 2 }
      let enteredJump := enterOpcode afterPush
      match charge? 8 enteredJump with
      | none => outOfGas enteredJump
      | some jumpCharged =>
        match jumpCharged.stack.pop with
        | none => finish .stackUnderflow jumpCharged
        | some (poppedDestination, stack) =>
          finishOrdinaryJump validJumpDestination poppedDestination { jumpCharged with stack := stack }

/--
The ordinary untraced `PUSH2; JUMPI` execution. It independently pushes and
pops the destination, then pops the pre-existing condition.
-/
def executeOrdinaryPush2Jumpi (validJumpDestination : Nat → Bool) (entered : MachineState) : Outcome :=
  match charge? 3 entered with
  | none => outOfGas entered
  | some pushCharged =>
    let destination := immediateValue 2 pushCharged
    match pushCharged.stack.push destination with
    | none => finish .stackOverflow { pushCharged with pc := pushCharged.pc + 2 }
    | some pushedStack =>
      let afterPush := { pushCharged with stack := pushedStack, pc := pushCharged.pc + 2 }
      let enteredJumpi := enterOpcode afterPush
      match charge? 10 enteredJumpi with
      | none => outOfGas enteredJumpi
      | some jumpiCharged =>
        match jumpiCharged.stack.pop with
        | none => finish .stackUnderflow jumpiCharged
        | some (poppedDestination, withoutDestination) =>
          match withoutDestination.pop with
          | none => finish .stackUnderflow jumpiCharged
          | some (condition, stack) =>
            let popped := { jumpiCharged with stack := stack }
            if condition.val = 0 then finish .success popped
            else finishOrdinaryJump validJumpDestination poppedDestination popped

end PushOpcodeExtractor.Specification.PushOpcode
