-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Gas
import Eip803x.Evm.MemoryStackControl

/-!
An independent operational reference for the Amsterdam memory, copy, and `GAS`
opcode slice.  Unlike `MemoryStackControl`, this boundary includes production
dispatch order, fixed and dynamic execution-gas debits, memory limits and
expansion, fork gates, instruction tracing, and immediate failure residue.

`closeFailureTrace` models the later `RunByteCode` trace/error closure.  It is
kept separate because an opcode handler returns with an open instruction trace;
only outer frame processing zeroes gas for an exceptional out-of-gas result.
-/

namespace Eip803x.Evm.MemoryCopyExecution

open GasMachine
open MemoryStackControl
open MemoryStackControl.Stack

inductive Opcode where
  | mload | mstore | mstore8 | msize | calldatacopy | codecopy
  | returndatacopy | mcopy | gas
  deriving DecidableEq, Repr

def Opcode.instruction : Opcode -> String
  | .mload => "MLOAD"
  | .mstore => "MSTORE"
  | .mstore8 => "MSTORE8"
  | .msize => "MSIZE"
  | .calldatacopy => "CALLDATACOPY"
  | .codecopy => "CODECOPY"
  | .returndatacopy => "RETURNDATACOPY"
  | .mcopy => "MCOPY"
  | .gas => "GAS"

def Opcode.byte : Opcode -> Nat
  | .calldatacopy => 0x37
  | .codecopy => 0x39
  | .returndatacopy => 0x3e
  | .mload => 0x51
  | .mstore => 0x52
  | .mstore8 => 0x53
  | .msize => 0x59
  | .gas => 0x5a
  | .mcopy => 0x5e

inductive DispatchTable where
  | noTrace | noTraceCancelable | traced | tracedCancelable
  deriving DecidableEq, Repr

def DispatchTable.tracing : DispatchTable -> Bool
  | .noTrace | .noTraceCancelable => false
  | .traced | .tracedCancelable => true

structure Activation where
  eip211 : Bool
  eip5656 : Bool
  deriving DecidableEq, Repr

def Activation.amsterdam : Activation := ⟨true, true⟩

def active (activation : Activation) : Opcode -> Bool
  | .returndatacopy => activation.eip211
  | .mcopy => activation.eip5656
  | _ => true

structure Schedule where
  veryLow : Nat
  base : Nat
  copyWord : Nat
  memoryLinear : Nat
  memoryQuadraticDivisor : Nat
  memoryQuadraticDivisorPositive : 0 < memoryQuadraticDivisor
  maxMemorySize : Nat
  maxUInt64 : Nat
  maxUInt32 : Nat
  uint256Modulus : Nat
  deriving DecidableEq, Repr

def Schedule.amsterdam : Schedule :=
  { veryLow := 3
    base := 2
    copyWord := 3
    memoryLinear := 3
    memoryQuadraticDivisor := 512
    memoryQuadraticDivisorPositive := by decide
    maxMemorySize := 2147483616
    maxUInt64 := 18446744073709551615
    maxUInt32 := 4294967295
    uint256Modulus := 2 ^ 256 }

inductive Status where
  | ok | outOfGas | stackUnderflow | stackOverflow | accessViolation | inactive
  | extractionMismatch
  deriving DecidableEq, Repr

inductive TraceEvent where
  | start (opcode : Opcode) (pc gasLeft : Nat)
  | operationMemory (memory : List Byte)
  | operationMemorySize (memorySize : Nat)
  | operationStack (stack : List UInt256)
  | operationReturnData (returnData : List Byte)
  | memoryChange (offset : Nat) (data : List Byte)
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
  calldata : List Byte
  returnData : List Byte
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

def beginInstruction (table : DispatchTable) (opcode : Opcode)
    (state : MachineState) : MachineState :=
  let traced := if table.tracing then
    let started := { state with trace := state.trace ++ [.start opcode state.pc state.gas.gasLeft] }
    let memory := if state.traceCapabilities.memory then
      { started with trace := started.trace ++
          [.operationMemory state.memory.bytes, .operationMemorySize state.memory.bytes.length] }
    else started
    let stack := if state.traceCapabilities.stack then
      { memory with trace := memory.trace ++ [.operationStack state.stack.words] }
    else memory
    if state.traceCapabilities.returnData then
      { stack with trace := stack.trace ++ [.operationReturnData state.returnData] }
    else stack
  else state
  { traced with pc := traced.pc + 1, opcodeCount := traced.opcodeCount + 1 }

def traceMemory (table : DispatchTable) (offset : Nat) (data : List Byte)
    (state : MachineState) : MachineState :=
  if table.tracing then { state with trace := state.trace ++ [.memoryChange offset data] } else state

def tracePush (table : DispatchTable) (payload : List Byte) (state : MachineState) : MachineState :=
  if table.tracing then { state with trace := state.trace ++ [.stackPush payload] } else state

def finishInstruction (table : DispatchTable) (state : MachineState) : MachineState :=
  if table.tracing then { state with trace := state.trace ++ [.finish state.gas.gasLeft] } else state

def exhaustExecutionGas (state : MachineState) : MachineState :=
  { state with gas := { state.gas with gasLeft := 0 } }

inductive Debit where
  | paid (state : MachineState)
  | outOfGas (state : MachineState)

def debit (amount : Nat) (state : MachineState) : Debit :=
  match chargeExecution amount state.gas with
  | .error _ => .outOfGas (exhaustExecutionGas state)
  | .ok gas => .paid { state with gas := gas }

def memoryWords (memory : Memory) : Nat := memory.bytes.length / 32

def memoryCost (schedule : Schedule) (words : Nat) : Nat :=
  words * schedule.memoryLinear + words * words / schedule.memoryQuadraticDivisor

def memoryRangeValid (schedule : Schedule) (offset length : UInt256) : Bool :=
  length.val = 0 ||
    (length.val <= schedule.maxMemorySize && offset.val <= schedule.maxMemorySize - length.val)

inductive MemoryPreparation where
  | invalid
  | prepared (expansionCost : Nat) (memory : Memory)
  deriving Repr

/-- Installs logical size before its gas debit, matching `ComputeMemoryExpansionCost`. -/
def prepareMemory (schedule : Schedule) (memory : Memory) (offset length : UInt256) :
    MemoryPreparation :=
  if length.val = 0 then
    .prepared 0 memory
  else if memoryRangeValid schedule offset length then
    let expanded := memory.expand offset.val length.val
    .prepared (memoryCost schedule (memoryWords expanded) - memoryCost schedule (memoryWords memory)) expanded
  else
    .invalid

def checkedWords (schedule : Schedule) (length : UInt256) : Nat × Bool :=
  if length.val > schedule.maxUInt64 then
    (0, true)
  else
    let words := (length.val + 31) / 32
    if words > schedule.maxUInt32 then (0, true) else (words, false)

def copyCost (schedule : Schedule) (words : Nat) : Nat :=
  schedule.veryLow + schedule.copyWord * words

def withStatus (status : Status) (state : MachineState) : Outcome := ⟨status, state⟩

/-- Exposes the installed-size residue on an expansion-gas failure. -/
def chargePreparedMemory (prepared : MemoryPreparation) (state : MachineState) : Outcome ⊕ MachineState :=
  match prepared with
  | .invalid => .inl (withStatus .outOfGas state)
  | .prepared expansionCost memory =>
    let installed := { state with memory := memory }
    match debit expansionCost installed with
    | .outOfGas exhausted => .inl (withStatus .outOfGas exhausted)
    | .paid paid => .inr paid

def replaceTop (stack : Stack) (value : UInt256) : Stack :=
  match stack.words with
  | [] => stack
  | _ :: tail => Stack.fromWords (value :: tail)

def executeMload (schedule : Schedule) (table : DispatchTable) (entered : MachineState) : Outcome :=
  match debit schedule.veryLow entered with
  | .outOfGas exhausted => withStatus .outOfGas exhausted
  | .paid charged =>
    match charged.stack.words with
    | [] => withStatus .stackUnderflow charged
    | offset :: _ =>
      match chargePreparedMemory (prepareMemory schedule charged.memory offset (Word.ofNat 32)) charged with
      | .inl failed => failed
      | .inr expanded =>
        let (loadedMemory, bytes) := charged.memory.readRange offset.val 32
        let value := bytesToWord bytes
        let loaded := { expanded with memory := loadedMemory, stack := replaceTop expanded.stack value }
        let tracedMemory := traceMemory table offset.val bytes loaded
        withStatus .ok (finishInstruction table (tracePush table (wordToBytes value) tracedMemory))

def executeMstore (schedule : Schedule) (table : DispatchTable) (entered : MachineState) : Outcome :=
  match debit schedule.veryLow entered with
  | .outOfGas exhausted => withStatus .outOfGas exhausted
  | .paid charged =>
    match charged.stack.popTwo with
    | none => withStatus .stackUnderflow charged
    | some (offset, value, tail) =>
      let popped := { charged with stack := tail }
      match chargePreparedMemory (prepareMemory schedule popped.memory offset (Word.ofNat 32)) popped with
      | .inl failed => failed
      | .inr expanded =>
        let bytes := wordToBytes value
        let stored := { expanded with memory := popped.memory.writeRange offset.val bytes }
        withStatus .ok (finishInstruction table (traceMemory table offset.val bytes stored))

def executeMstore8 (schedule : Schedule) (table : DispatchTable) (entered : MachineState) : Outcome :=
  match debit schedule.veryLow entered with
  | .outOfGas exhausted => withStatus .outOfGas exhausted
  | .paid charged =>
    match charged.stack.popTwo with
    | none => withStatus .stackUnderflow charged
    | some (offset, value, tail) =>
      let popped := { charged with stack := tail }
      match chargePreparedMemory (prepareMemory schedule popped.memory offset (Word.ofNat 1)) popped with
      | .inl failed => failed
      | .inr expanded =>
        let bytes := [MemoryStackControl.byte value.val]
        let stored := { expanded with memory := popped.memory.writeRange offset.val bytes }
        withStatus .ok (finishInstruction table (traceMemory table offset.val bytes stored))

def pushFixed (cost : Nat) (table : DispatchTable) (value : MachineState -> UInt256)
    (entered : MachineState) : Outcome :=
  match debit cost entered with
  | .outOfGas exhausted => withStatus .outOfGas exhausted
  | .paid charged =>
    let result := value charged
    match charged.stack.push result with
    | none => withStatus .stackOverflow charged
    | some stack =>
      let pushed := tracePush table ((wordToBytes result).drop 24) { charged with stack := stack }
      withStatus .ok (finishInstruction table pushed)

def copyFrom (schedule : Schedule) (table : DispatchTable) (sourceBytes : MachineState -> List Byte)
    (entered : MachineState) : Outcome :=
  match entered.stack.popThree with
  | none => withStatus .stackUnderflow entered
  | some (destination, source, length, tail) =>
    let popped := { entered with stack := tail }
    let (words, invalidWords) := checkedWords schedule length
    match debit (copyCost schedule words) popped with
    | .outOfGas exhausted => withStatus .outOfGas exhausted
    | .paid charged =>
      if invalidWords then withStatus .outOfGas charged
      else if length.val = 0 then withStatus .ok (finishInstruction table charged)
      else
        match chargePreparedMemory (prepareMemory schedule charged.memory destination length) charged with
        | .inl failed => failed
        | .inr expanded =>
          let bytes := readRange (sourceBytes expanded) source.val length.val
          let copied := { expanded with memory := charged.memory.writeRange destination.val bytes }
          withStatus .ok (finishInstruction table (traceMemory table destination.val bytes copied))

def returnDataRangeValid (schedule : Schedule) (returnData : List Byte)
    (source length : UInt256) : Bool :=
  source.val + length.val < schedule.uint256Modulus &&
    source.val + length.val <= returnData.length

def executeReturnDataCopy (schedule : Schedule) (table : DispatchTable)
    (entered : MachineState) : Outcome :=
  match entered.stack.popThree with
  | none => withStatus .stackUnderflow entered
  | some (destination, source, length, tail) =>
    let popped := { entered with stack := tail }
    let (words, invalidWords) := checkedWords schedule length
    match debit (copyCost schedule words) popped with
    | .outOfGas exhausted => withStatus .outOfGas exhausted
    | .paid charged =>
      if invalidWords then withStatus .outOfGas charged
      else if !returnDataRangeValid schedule charged.returnData source length then
        withStatus .accessViolation charged
      else if length.val = 0 then withStatus .ok (finishInstruction table charged)
      else
        match chargePreparedMemory (prepareMemory schedule charged.memory destination length) charged with
        | .inl failed => failed
        | .inr expanded =>
          let bytes := readRange expanded.returnData source.val length.val
          let copied := { expanded with memory := charged.memory.writeRange destination.val bytes }
          withStatus .ok (finishInstruction table (traceMemory table destination.val bytes copied))

def executeMcopy (schedule : Schedule) (table : DispatchTable) (entered : MachineState) : Outcome :=
  match entered.stack.popThree with
  | none => withStatus .stackUnderflow entered
  | some (destination, source, length, tail) =>
    let popped := { entered with stack := tail }
    let (words, invalidWords) := checkedWords schedule length
    match debit (copyCost schedule words) popped with
    | .outOfGas exhausted => withStatus .outOfGas exhausted
    | .paid charged =>
      if invalidWords then withStatus .outOfGas charged
      else if length.val = 0 then withStatus .ok (finishInstruction table charged)
      else
        let greatest := if destination.val < source.val then source else destination
        match chargePreparedMemory (prepareMemory schedule charged.memory greatest length) charged with
        | .inl failed => failed
        | .inr expanded =>
          let sourceSnapshot := readRange expanded.memory.bytes source.val length.val
          let copied := { expanded with memory := charged.memory.mcopy destination.val source.val length.val }
          let sourceTraced := traceMemory table source.val sourceSnapshot copied
          let destinationSnapshot := readRange copied.memory.bytes destination.val length.val
          let destinationTraced := traceMemory table destination.val destinationSnapshot sourceTraced
          withStatus .ok (finishInstruction table destinationTraced)

def executeActive (schedule : Schedule) (table : DispatchTable) (opcode : Opcode)
    (entered : MachineState) : Outcome :=
  match opcode with
  | .mload => executeMload schedule table entered
  | .mstore => executeMstore schedule table entered
  | .mstore8 => executeMstore8 schedule table entered
  | .msize => pushFixed schedule.base table (fun state => Word.ofNat state.memory.bytes.length) entered
  | .calldatacopy => copyFrom schedule table (fun state => state.calldata) entered
  | .codecopy => copyFrom schedule table (fun state => state.code) entered
  | .returndatacopy => executeReturnDataCopy schedule table entered
  | .mcopy => executeMcopy schedule table entered
  | .gas => pushFixed schedule.base table (fun state => Word.ofNat state.gas.gasLeft) entered

def execute (schedule : Schedule) (activation : Activation) (table : DispatchTable)
    (opcode : Opcode) (state : MachineState) : Outcome :=
  let entered := beginInstruction table opcode state
  if active activation opcode then executeActive schedule table opcode entered
  else withStatus .inactive entered

def closeFailureTrace (table : DispatchTable) (outcome : Outcome) : Outcome :=
  if outcome.status = .ok || outcome.status = .extractionMismatch then outcome
  else
    let state := if outcome.status = .outOfGas then exhaustExecutionGas outcome.state else outcome.state
    if table.tracing then
      { outcome with state := { state with trace := state.trace ++ [.finish state.gas.gasLeft, .error outcome.status] } }
    else
      { outcome with state := state }

/-- Normalizes Nethermind's post-dispatch internal fault PC back to the opcode PC. -/
def faultPc (outcome : Outcome) : Nat :=
  if outcome.status = .ok then outcome.state.pc else outcome.state.pc - 1

end Eip803x.Evm.MemoryCopyExecution
