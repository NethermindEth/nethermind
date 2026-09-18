-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- Generated from round-tripped, source-admitted semantic IR. Definitions only; do not edit.
-- The imported operational module supplies representation types, not this executable body.
-- Extractor version: 1.0.0
-- Canonical IR SHA-256: 222802c478c4ae70c58079664d9d53c2c8bd6387d5acf9d44fbcb9fd7cb3bec7
-- Source closure SHA-256: 266e7941aaae6cd07bd0cc3036ba436f2345f9c2bcb79c9827c0752524069416

import CallDataLoadOpcodeExtractor.Specification.CallDataLoadExecution

namespace Eip803x.Generated.CallDataLoadOpcodeKernel

open Eip803x.Evm.MemoryStackControl
open Eip803x.Evm.MemoryStackControl.Stack

abbrev Opcode := Eip803x.Evm.CallDataLoadExecution.Opcode
abbrev DispatchTable := Eip803x.Evm.CallDataLoadExecution.DispatchTable
abbrev Schedule := Eip803x.Evm.CallDataLoadExecution.Schedule
abbrev Status := Eip803x.Evm.CallDataLoadExecution.Status
abbrev TraceEvent := Eip803x.Evm.CallDataLoadExecution.TraceEvent
abbrev MachineState := Eip803x.Evm.CallDataLoadExecution.MachineState
abbrev Outcome := Eip803x.Evm.CallDataLoadExecution.Outcome

def sourceRoot : String := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>"
def sourceFork : String := "Nethermind.Specs.Forks.Amsterdam"
def sourceGasPolicy : String := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
def sourceBuild : String := "Directory.Build.targets: EnableZkEvm != true selects *.std.cs and excludes *.zkevm.cs"

structure OpcodeDescriptor where
  name : String
  instruction : String
  opcodeByte : Nat
  handlerBody : String
  handlerTarget : String
  stackInputs : Nat
  stackOutputs : Nat
  stackGrowth : Int
  pushSize : Int
  hasCheckedBody : Bool
  usesVm : Bool
  replacesTop : Bool
  endsInstructionTrace : Bool
  activation : String
  operation : String
  gasClass : String
  accessWidth : Nat
  offsetRule : String
  paddingRule : String
  stackTraceOrder : String
  traceRule : String
  effectOrder : List String
  deriving DecidableEq, Repr

def descriptor : OpcodeDescriptor :=
  { name := "calldataload"
    instruction := "CALLDATALOAD"
    opcodeByte := 53
    handlerBody := "CallDataLoadOpcode<TTracingInst>"
    handlerTarget := "EvmInstructions.CallDataLoadCore<TGasPolicy,TTracingInst>"
    stackInputs := 1
    stackOutputs := 1
    stackGrowth := 0
    pushSize := -1
    hasCheckedBody := true
    usesVm := true
    replacesTop := true
    endsInstructionTrace := false
    activation := "unconditional since Frontier"
    operation := "loadCallDataWord"
    gasClass := "veryLow"
    accessWidth := 32
    offsetRule := "uint64OrZero"
    paddingRule := "rightZero"
    stackTraceOrder := "bottomFirst"
    traceRule := "one zero byte for inaccessible offsets; raw 32-byte word for in-range offsets"
    effectOrder := ["traceStart", "traceStackBottomFirst", "advanceDispatchPc", "incrementOpcodeCount", "chargeVeryLow", "checkDepthOne", "peekTopSlot", "decodeUInt256Offset", "rejectAboveUInt64OrAtEnd", "zeroTopAndTraceZeroByte", "otherwiseCopyAtMost32", "rightZeroPadAndReplaceTop", "traceRawWord", "closeSuccessTrace", "tailDispatchOrCancelableBoundary"] }

def dispatchRoots : List String := ["Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CallDataLoadOpcode<OffFlag>,OffFlag,OffFlag,OnFlag>", "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CallDataLoadOpcode<OffFlag>,OffFlag,OnFlag,OnFlag>", "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CallDataLoadOpcode<OnFlag>,OnFlag,OffFlag,OnFlag>", "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CallDataLoadOpcode<OnFlag>,OnFlag,OnFlag,OnFlag>"]
def forkLineage : List String := ["Olympic", "Frontier", "Homestead", "Dao", "TangerineWhistle", "SpuriousDragon", "Byzantium", "Constantinople", "ConstantinopleFix", "Istanbul", "MuirGlacier", "Berlin", "London", "ArrowGlacier", "GrayGlacier", "Paris", "Shanghai", "Cancun", "Prague", "Osaka", "BPO1", "BPO2", "Amsterdam"]
def openExtractionObligations : List String := ["Roslyn exact-syntax admission does not prove C# compilation, CLR/JIT/AOT execution, function-pointer tail calls, or hardware behavior.", "The model assumes the external UInt256 IsUint64/u0 contract matches the shared Lean word modulo 2^256.", "The model assumes the admitted unsafe EvmWord stack slot, native endian conversion, unaligned reads/writes, vector lanes, and Span aliasing implement the modeled big-endian 32-byte word without corruption.", "The instruction-start stack event reverses the model's top-first words to match the admitted physical bottom-first VmState.MemoryStacks and TraceStack.ToRawBytes copy; per-word byte decoding remains under the EvmWord representation premise.", "Tracing callbacks are assumed total and non-throwing; capability-dependent memory, bottom-first stack, and return-data event order and modeled payloads are explicit, while the StartOperation environment object is abstracted.", "Cancellation is modeled only at the post-opcode boundary; token state, scheduling, CLR/JIT/AOT, function-pointer tail calls, and hardware behavior remain open.", "The outer closure covers OOG gas clearing, charged underflow residue, finish-before-error, and retention of the frame PC on fault; transaction rollback, persistence, and state-gas settlement are outside this slice."]

def amsterdamSchedule : Schedule :=
  { veryLow := 3
    wordBytes := 32
    maxInt32 := 2147483647
    maxUInt64 := 18446744073709551615
    uint256Modulus := 115792089237316195423570985008687907853269984665640564039457584007913129639936 }

def tableTracing : DispatchTable → Bool
  | .noTrace | .noTraceCancelable => false
  | .traced | .tracedCancelable => true

def tableCancelable : DispatchTable → Bool
  | .noTrace | .traced => false
  | .noTraceCancelable | .tracedCancelable => true

def dispatchEnter (table : DispatchTable) (state : MachineState) : MachineState :=
  let traced := if tableTracing table then
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

def accessibleOffset (schedule : Schedule) (calldata : List Byte) (offset : UInt256) : Bool :=
  offset.val ≤ schedule.maxUInt64 && offset.val < calldata.length

def productionDomain (schedule : Schedule) (state : MachineState) : Prop :=
  state.calldata.length ≤ schedule.maxInt32 ∧ state.gasLeft ≤ schedule.maxUInt64 ∧
    state.pc < schedule.maxInt32 ∧ state.opcodeCount < schedule.maxInt32

def readCallDataWordBytes (schedule : Schedule) (calldata : List Byte)
    (offset : UInt256) : List Byte :=
  if accessibleOffset schedule calldata offset then
    readRange calldata offset.val schedule.wordBytes
  else zeroWordBytes schedule

def tracedPushPayload (schedule : Schedule) (calldata : List Byte)
    (offset : UInt256) : List Byte :=
  if accessibleOffset schedule calldata offset then
    readCallDataWordBytes schedule calldata offset
  else [zeroByte]

def reportPush (table : DispatchTable) (payload : List Byte)
    (state : MachineState) : MachineState :=
  if tableTracing table then { state with trace := state.trace ++ [.stackPush payload] } else state

def dispatchFinish (table : DispatchTable) (state : MachineState) : MachineState :=
  if tableTracing table then { state with trace := state.trace ++ [.finish state.gasLeft] } else state

def executeCheckedBody (schedule : Schedule) (table : DispatchTable)
    (entered : MachineState) : Outcome :=
  if schedule.veryLow ≤ entered.gasLeft then
    let charged := { entered with gasLeft := entered.gasLeft - schedule.veryLow }
    match charged.stack.words with
    | [] => ⟨.stackUnderflow, charged⟩
    | offset :: _ =>
      let bytes := readCallDataWordBytes schedule charged.calldata offset
      let replaced := { charged with stack := replaceTop charged.stack (bytesToWord bytes) }
      let traced := reportPush table (tracedPushPayload schedule charged.calldata offset) replaced
      ⟨.ok, dispatchFinish table traced⟩
  else
    ⟨.outOfGas, { entered with gasLeft := 0 }⟩

def executeSemantic (schedule : Schedule) (table : DispatchTable)
    (state : MachineState) : Outcome :=
  executeCheckedBody schedule table (dispatchEnter table state)

def executeAmsterdam (table : DispatchTable) (state : MachineState) : Outcome :=
  executeSemantic amsterdamSchedule table state

def closeFailureTrace (table : DispatchTable) (outcome : Outcome) : Outcome :=
  if outcome.status = .ok || outcome.status = .extractionMismatch then outcome
  else
    let state := if outcome.status = .outOfGas then { outcome.state with gasLeft := 0 } else outcome.state
    if tableTracing table then
      { outcome with state := { state with trace := state.trace ++ [.finish state.gasLeft, .error outcome.status] } }
    else { outcome with state := state }

def executeAmsterdamClosed (table : DispatchTable) (state : MachineState) : Outcome :=
  closeFailureTrace table (executeAmsterdam table state)

def faultPc (outcome : Outcome) : Nat :=
  if outcome.status = .ok then outcome.state.pc else outcome.state.pc - 1

end Eip803x.Generated.CallDataLoadOpcodeKernel
