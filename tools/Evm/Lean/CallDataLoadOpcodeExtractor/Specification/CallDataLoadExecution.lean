-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.MemoryStackControl

/-!
An independent executable reference for standard-mainnet Amsterdam `CALLDATALOAD`.
It models dispatch entry, fixed execution gas, checked stack access, the exact
zero-extended calldata read, top-slot replacement, stack-push tracing, and outer
exception trace closure. The generated transition imports these types but does
not call this reference transition.
-/

namespace Eip803x.Evm.CallDataLoadExecution

open MemoryStackControl
open MemoryStackControl.Stack

inductive Opcode where
  | calldataload
  deriving DecidableEq, Repr

def Opcode.instruction : Opcode → String
  | .calldataload => "CALLDATALOAD"

def Opcode.byte : Opcode → Nat
  | .calldataload => 0x35

inductive DispatchTable where
  | noTrace | noTraceCancelable | traced | tracedCancelable
  deriving DecidableEq, Repr

def DispatchTable.tracing : DispatchTable → Bool
  | .noTrace | .noTraceCancelable => false
  | .traced | .tracedCancelable => true

def DispatchTable.cancelable : DispatchTable → Bool
  | .noTrace | .traced => false
  | .noTraceCancelable | .tracedCancelable => true

structure Schedule where
  veryLow : Nat
  wordBytes : Nat
  maxInt32 : Nat
  maxUInt64 : Nat
  uint256Modulus : Nat
  deriving DecidableEq, Repr

def Schedule.amsterdam : Schedule :=
  { veryLow := 3
    wordBytes := 32
    maxInt32 := 2147483647
    maxUInt64 := 18446744073709551615
    uint256Modulus := 2 ^ 256 }

inductive Status where
  | ok | outOfGas | stackUnderflow | extractionMismatch
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
  calldata : List Byte
  memory : List Byte
  returnData : List Byte
  stack : Stack
  pc : Nat
  opcodeCount : Nat
  gasLeft : Nat
  traceCapabilities : TraceCapabilities
  trace : List TraceEvent
  deriving Repr

structure Outcome where
  status : Status
  state : MachineState
  deriving Repr

def beginInstruction (table : DispatchTable) (state : MachineState) : MachineState :=
  let traced := if table.tracing then
    let started := { state with trace := state.trace ++ [.start .calldataload state.pc state.gasLeft] }
    let memory := if state.traceCapabilities.memory then
      { started with trace := started.trace ++
          [.operationMemory state.memory, .operationMemorySize state.memory.length] }
    else started
    let stack := if state.traceCapabilities.stack then
      { memory with trace := memory.trace ++ [.operationStack state.stack.words.reverse] }
    else memory
    if state.traceCapabilities.returnData then
      { stack with trace := stack.trace ++ [.operationReturnData state.returnData] }
    else stack
  else state
  { traced with pc := traced.pc + 1, opcodeCount := traced.opcodeCount + 1 }

def replaceTop (stack : Stack) (value : UInt256) : Stack :=
  match stack with
  | ⟨[], bounded⟩ => ⟨[], bounded⟩
  | ⟨_ :: tail, bounded⟩ => ⟨value :: tail, bounded⟩

def zeroWordBytes (schedule : Schedule) : List Byte :=
  List.replicate schedule.wordBytes zeroByte

def offsetAccessible (schedule : Schedule) (calldata : List Byte) (offset : UInt256) : Bool :=
  offset.val ≤ schedule.maxUInt64 && offset.val < calldata.length

def productionDomain (schedule : Schedule) (state : MachineState) : Prop :=
  state.calldata.length ≤ schedule.maxInt32 ∧ state.gasLeft ≤ schedule.maxUInt64 ∧
    state.pc < schedule.maxInt32 ∧ state.opcodeCount < schedule.maxInt32

def loadedBytes (schedule : Schedule) (calldata : List Byte) (offset : UInt256) : List Byte :=
  if offsetAccessible schedule calldata offset then readRange calldata offset.val schedule.wordBytes
  else zeroWordBytes schedule

def loadedWord (schedule : Schedule) (calldata : List Byte) (offset : UInt256) : UInt256 :=
  bytesToWord (loadedBytes schedule calldata offset)

def pushPayload (schedule : Schedule) (calldata : List Byte) (offset : UInt256) : List Byte :=
  if offsetAccessible schedule calldata offset then loadedBytes schedule calldata offset else [zeroByte]

def tracePush (table : DispatchTable) (payload : List Byte) (state : MachineState) : MachineState :=
  if table.tracing then { state with trace := state.trace ++ [.stackPush payload] } else state

def finishInstruction (table : DispatchTable) (state : MachineState) : MachineState :=
  if table.tracing then { state with trace := state.trace ++ [.finish state.gasLeft] } else state

def withStatus (status : Status) (state : MachineState) : Outcome := ⟨status, state⟩

def executeActive (schedule : Schedule) (table : DispatchTable) (entered : MachineState) : Outcome :=
  if schedule.veryLow ≤ entered.gasLeft then
    let charged := { entered with gasLeft := entered.gasLeft - schedule.veryLow }
    match charged.stack.words with
    | [] => withStatus .stackUnderflow charged
    | offset :: _ =>
      let bytes := loadedBytes schedule charged.calldata offset
      let replaced := { charged with stack := replaceTop charged.stack (bytesToWord bytes) }
      let traced := tracePush table (pushPayload schedule charged.calldata offset) replaced
      withStatus .ok (finishInstruction table traced)
  else
    withStatus .outOfGas { entered with gasLeft := 0 }

def execute (schedule : Schedule) (table : DispatchTable) (state : MachineState) : Outcome :=
  executeActive schedule table (beginInstruction table state)

def closeFailureTrace (table : DispatchTable) (outcome : Outcome) : Outcome :=
  if outcome.status = .ok || outcome.status = .extractionMismatch then outcome
  else
    let state := if outcome.status = .outOfGas then { outcome.state with gasLeft := 0 } else outcome.state
    if table.tracing then
      { outcome with state := { state with trace := state.trace ++ [.finish state.gasLeft, .error outcome.status] } }
    else { outcome with state := state }

/-- The dispatch-local PC advances first; the frame fault PC remains the opcode PC. -/
def faultPc (outcome : Outcome) : Nat :=
  if outcome.status = .ok then outcome.state.pc else outcome.state.pc - 1

end Eip803x.Evm.CallDataLoadExecution
