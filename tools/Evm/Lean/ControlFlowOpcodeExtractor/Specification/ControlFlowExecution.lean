-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Gas
import Eip803x.Evm.MemoryStackControl

/-!
Independent operational reference for the standard Amsterdam `STOP`, `SLOTNUM`,
`JUMP`, `JUMPI`, `PC`, `JUMPDEST`, `RETURN`, and `REVERT` production roots.
It records dispatcher-first PC/counter updates, execution-gas failure residue,
the untraced jump/JUMPDEST fusion, staged output, and instruction-trace closure.
-/

namespace Eip803x.Evm.ControlFlowExecution

open GasMachine
open MemoryStackControl
open MemoryStackControl.Stack

inductive Opcode where
  | stop | slotnum | jump | jumpi | pc | jumpdest | return_ | revert
  deriving DecidableEq, Repr

def Opcode.instruction : Opcode -> String
  | .stop => "STOP"
  | .slotnum => "SLOTNUM"
  | .jump => "JUMP"
  | .jumpi => "JUMPI"
  | .pc => "PC"
  | .jumpdest => "JUMPDEST"
  | .return_ => "RETURN"
  | .revert => "REVERT"

def Opcode.byte : Opcode -> Nat
  | .stop => 0x00
  | .slotnum => 0x4b
  | .jump => 0x56
  | .jumpi => 0x57
  | .pc => 0x58
  | .jumpdest => 0x5b
  | .return_ => 0xf3
  | .revert => 0xfd

inductive DispatchTable where
  | noTrace | noTraceCancelable | traced | tracedCancelable
  deriving DecidableEq, Repr

def DispatchTable.tracing : DispatchTable -> Bool
  | .noTrace | .noTraceCancelable => false
  | .traced | .tracedCancelable => true

def DispatchTable.cancelable : DispatchTable -> Bool
  | .noTrace | .traced => false
  | .noTraceCancelable | .tracedCancelable => true

structure Activation where
  eip7843 : Bool
  revert : Bool
  deriving DecidableEq, Repr

def Activation.amsterdam : Activation := ⟨true, true⟩

def active (activation : Activation) : Opcode -> Bool
  | .slotnum => activation.eip7843
  | .revert => activation.revert
  | _ => true

structure Schedule where
  zero : Nat
  base : Nat
  mid : Nat
  high : Nat
  jumpdest : Nat
  memoryLinear : Nat
  memoryQuadraticDivisor : Nat
  maxMemorySize : Nat
  deriving DecidableEq, Repr

def Schedule.amsterdam : Schedule :=
  { zero := 0
    base := 2
    mid := 8
    high := 10
    jumpdest := 1
    memoryLinear := 3
    memoryQuadraticDivisor := 512
    maxMemorySize := 2147483616 }

def productionInstructionWidth (opcode : Byte) : Nat :=
  if 0x60 ≤ opcode.val && opcode.val ≤ 0x7f then opcode.val - 0x60 + 2 else 1

def scanProductionJumpDestination (code : List Byte) (target pc fuel : Nat) : Bool :=
  match fuel with
  | 0 => false
  | fuel + 1 =>
    if pc = target then
      match code[pc]? with
      | some value => value.val = 0x5b
      | none => false
    else
      match code[pc]? with
      | none => false
      | some value => scanProductionJumpDestination code target
          (pc + productionInstructionWidth value) fuel

def validProductionJumpDestination (code : List Byte) (target : Nat) : Bool :=
  match code with
  | [] => false
  | first :: _ =>
    if first.val = 0x00 then false
    else target ≤ 2147483647 && target < code.length &&
      scanProductionJumpDestination code target 0 code.length

inductive Status where
  | ok | stop | revert | outOfGas | stackUnderflow | stackOverflow
  | invalidJump | badInstruction | extractionMismatch
  deriving DecidableEq, Repr

inductive TraceEvent where
  | start (opcode : Opcode) (pc gasLeft : Nat)
  | operationMemory (memory : List Byte)
  | operationMemorySize (memorySize : Nat)
  | operationStack (stack : List UInt256)
  | operationReturnData (returnData : List Byte)
  | stackPush (payload : List Byte)
  | finish (gasLeft : Nat)
  | error (status : Status)
  deriving DecidableEq, Repr

structure TraceCapabilities where
  memory : Bool
  stack : Bool
  returnData : Bool
  deriving DecidableEq, Repr

structure MachineState where
  code : List Byte
  previousReturnData : List Byte
  stagedOutput : Option (List Byte)
  slotNumber : Option Nat
  memory : Memory
  stack : Stack
  pc : Nat
  opcodeCount : Nat
  gas : GasState
  traceCapabilities : TraceCapabilities
  trace : List TraceEvent
  deriving Repr

structure Outcome where
  status : Status
  state : MachineState
  deriving Repr

def outcome (status : Status) (state : MachineState) : Outcome := ⟨status, state⟩

def beginInstruction (table : DispatchTable) (opcode : Opcode)
    (state : MachineState) : MachineState :=
  let traced := if table.tracing then
    let started := { state with trace := state.trace ++ [.start opcode state.pc state.gas.gasLeft] }
    let memory := if state.traceCapabilities.memory then
      { started with trace := started.trace ++
          [.operationMemory state.memory.bytes, .operationMemorySize state.memory.bytes.length] }
    else started
    let stack := if state.traceCapabilities.stack then
      { memory with trace := memory.trace ++ [.operationStack state.stack.words.reverse] }
    else memory
    if state.traceCapabilities.returnData then
      { stack with trace := stack.trace ++ [.operationReturnData state.previousReturnData] }
    else stack
  else state
  { traced with pc := traced.pc + 1, opcodeCount := traced.opcodeCount + 1 }

def tracePush (table : DispatchTable) (width : Nat) (value : UInt256)
    (state : MachineState) : MachineState :=
  if table.tracing then
    { state with trace := state.trace ++ [.stackPush ((wordToBytes value).drop (32 - width))] }
  else state

def finishInstruction (table : DispatchTable) (state : MachineState) : MachineState :=
  if table.tracing then { state with trace := state.trace ++ [.finish state.gas.gasLeft] } else state

def exhaust (state : MachineState) : MachineState :=
  { state with gas := { state.gas with gasLeft := 0 } }

inductive Debit where
  | paid (state : MachineState)
  | outOfGas (state : MachineState)

def debit (amount : Nat) (state : MachineState) : Debit :=
  match chargeExecution amount state.gas with
  | .error _ => .outOfGas (exhaust state)
  | .ok gas => .paid { state with gas := gas }

def memoryWords (memory : Memory) : Nat := memory.bytes.length / 32

def memoryCost (schedule : Schedule) (words : Nat) : Nat :=
  words * schedule.memoryLinear + words * words / schedule.memoryQuadraticDivisor

def memoryRangeValid (schedule : Schedule) (offset length : UInt256) : Bool :=
  length.val = 0 ||
    (length.val <= schedule.maxMemorySize && offset.val <= schedule.maxMemorySize - length.val)

inductive MemoryPreparation where
  | invalid
  | prepared (expansionCost : Nat)
  deriving Repr

def prepareMemory (schedule : Schedule) (memory : Memory) (offset length : UInt256) :
    MemoryPreparation :=
  if length.val = 0 then .prepared 0
  else if memoryRangeValid schedule offset length then
    let expanded := memory.expand offset.val length.val
    .prepared (memoryCost schedule (memoryWords expanded) - memoryCost schedule (memoryWords memory))
  else .invalid

def finishSuccess (table : DispatchTable) (state : MachineState) : Outcome :=
  outcome .ok (finishInstruction table state)

def runBadInstruction (entered : MachineState) : Outcome := outcome .badInstruction entered

def runStop (entered : MachineState) : Outcome := outcome .stop entered

def pushFixed (cost traceWidth : Nat) (table : DispatchTable) (value : MachineState -> UInt256)
    (entered : MachineState) : Outcome :=
  match debit cost entered with
  | .outOfGas failed => outcome .outOfGas failed
  | .paid charged =>
    let pushed := value charged
    match charged.stack.push pushed with
    | none => outcome .stackOverflow charged
    | some stack => finishSuccess table (tracePush table traceWidth pushed { charged with stack := stack })

def runSlotNum (schedule : Schedule) (table : DispatchTable) (entered : MachineState) : Outcome :=
  match entered.slotNumber with
  | none => runBadInstruction entered
  | some slot => pushFixed schedule.base 8 table (fun _ => Word.ofNat slot) entered

def runJumpDest (schedule : Schedule) (table : DispatchTable) (entered : MachineState) : Outcome :=
  match debit schedule.jumpdest entered with
  | .outOfGas failed => outcome .outOfGas failed
  | .paid charged => finishSuccess table charged

def fuseJumpDest (schedule : Schedule) (table : DispatchTable) (target : Nat)
    (state : MachineState) : Outcome :=
  if table.tracing then finishSuccess table { state with pc := target }
  else
    let skipped := { state with pc := target + 1, opcodeCount := state.opcodeCount + 1 }
    match debit schedule.jumpdest skipped with
    | .outOfGas failed => outcome .outOfGas failed
    | .paid charged => finishSuccess table charged

def runJump (schedule : Schedule) (table : DispatchTable) (entered : MachineState) : Outcome :=
  match debit schedule.mid entered with
  | .outOfGas failed => outcome .outOfGas failed
  | .paid charged =>
    match charged.stack.pop with
    | none => outcome .stackUnderflow charged
    | some (target, tail) =>
      let popped := { charged with stack := tail }
      if validProductionJumpDestination popped.code target.val then
        fuseJumpDest schedule table target.val popped
      else outcome .invalidJump popped

def runJumpI (schedule : Schedule) (table : DispatchTable) (entered : MachineState) : Outcome :=
  match debit schedule.high entered with
  | .outOfGas failed => outcome .outOfGas failed
  | .paid charged =>
    match charged.stack.popTwo with
    | none => outcome .stackUnderflow charged
    | some (target, condition, tail) =>
      let popped := { charged with stack := tail }
      if condition.val = 0 then finishSuccess table popped
      else if validProductionJumpDestination popped.code target.val then
        fuseJumpDest schedule table target.val popped
      else outcome .invalidJump popped

def runReturnLike (schedule : Schedule) (terminal : Status) (entered : MachineState) : Outcome :=
  match entered.stack.popTwo with
  | none => outcome .stackUnderflow entered
  | some (offset, length, tail) =>
    let popped := { entered with stack := tail }
    match prepareMemory schedule popped.memory offset length with
    | .invalid => outcome .outOfGas popped
    | .prepared expansionCost =>
      match debit expansionCost popped with
      | .outOfGas failed => outcome .outOfGas failed
      | .paid charged =>
        let (memory, data) := charged.memory.readRange offset.val length.val
        outcome terminal { charged with memory := memory, stagedOutput := some data }

def executeActive (schedule : Schedule) (table : DispatchTable) (opcode : Opcode)
    (entered : MachineState) : Outcome :=
  match opcode with
  | .stop => runStop entered
  | .slotnum => runSlotNum schedule table entered
  | .jump => runJump schedule table entered
  | .jumpi => runJumpI schedule table entered
  | .pc => pushFixed schedule.base 4 table (fun state => Word.ofNat (state.pc - 1)) entered
  | .jumpdest => runJumpDest schedule table entered
  | .return_ => runReturnLike schedule .stop entered
  | .revert => runReturnLike schedule .revert entered

def executeHandler (schedule : Schedule) (activation : Activation) (table : DispatchTable)
    (opcode : Opcode) (state : MachineState) : Outcome :=
  let entered := beginInstruction table opcode state
  if active activation opcode then executeActive schedule table opcode entered
  else runBadInstruction entered

/-- Models `RunByteCode` closure after an opcode handler leaves its trace open. -/
def closeFrame (table : DispatchTable) (result : Outcome) : Outcome :=
  let closed := if result.status = .outOfGas then
    { result with state := exhaust result.state }
  else result
  if closed.status = .ok || closed.status = .extractionMismatch then closed
  else if table.tracing then
    let finished := { closed.state with trace := closed.state.trace ++ [.finish closed.state.gas.gasLeft] }
    if closed.status = .stop || closed.status = .revert then
      { closed with state := finished }
    else
      { closed with state := { finished with trace := finished.trace ++ [.error closed.status] } }
  else closed

def execute (schedule : Schedule) (activation : Activation) (table : DispatchTable)
    (opcode : Opcode) (state : MachineState) : Outcome :=
  closeFrame table (executeHandler schedule activation table opcode state)

end Eip803x.Evm.ControlFlowExecution
