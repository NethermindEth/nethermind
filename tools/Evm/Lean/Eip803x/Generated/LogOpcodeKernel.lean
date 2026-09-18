-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- Generated from deserialized, source-derived IR. Definitions only; do not edit.
-- Extractor version: 1.0.0
-- Canonical IR SHA-256: df5f09ca0a1d49a01bbc72c015f65a1658ea9e5e5c362d74c175a10761069920
-- Source closure SHA-256: 3f948be61b30a6d1250526962596dc2d5c60eeb49bb153d21e7a968e3221cb6f

import Eip803x.Gas
import Eip803x.Evm.MemoryStackControl
import Init.Data.String.Search

namespace Eip803x.Generated.LogOpcodeKernel

open GasMachine
open Eip803x.Evm.MemoryStackControl

def sourceRoot : String := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>"
def sourceGasPolicy : String := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
def sourceFork : String := "Nethermind.Specs.Forks.Amsterdam"

inductive Opcode where
  | log0 | log1 | log2 | log3 | log4
  deriving DecidableEq, Repr

inductive Effect where
  | traceStartIfInstructionTracing
  | incrementProgramCounter
  | staticGuard
  | popOffsetLengthAtomically
  | readTopicCount
  | prepareMemoryAndInstallSize
  | chargeMemoryExpansion
  | chargeLogEmission
  | loadPayload
  | allocateTopicArray
  | popTopicsSequentially
  | copyPayload
  | constructLog
  | appendJournal
  | reportLogIfEnabled
  | traceEndIfInstructionTracing
  | dispatchContinue
  deriving DecidableEq, Repr

inductive DispatchTable where
  | noTrace | noTraceCancelable | traced | tracedCancelable
  deriving DecidableEq, Repr

structure Descriptor where
  instruction : String
  opcodeByte : Nat
  topicCount : Nat
  handlerBody : String
  programCounterDelta : Nat
  headerStackInputs : Nat
  effectOrder : List Effect
  deriving DecidableEq, Repr

structure Specialization where
  opcode : Opcode
  table : DispatchTable
  tracingFlag : String
  cancelableFlag : String
  closedRoot : String
  deriving DecidableEq, Repr

def allOpcodes : List Opcode := [.log0, .log1, .log2, .log3, .log4]
def allDispatchTables : List DispatchTable :=
  [.noTrace, .noTraceCancelable, .traced, .tracedCancelable]

def expectedEffectOrder : List Effect :=
  [
    .traceStartIfInstructionTracing,
    .incrementProgramCounter,
    .staticGuard,
    .popOffsetLengthAtomically,
    .readTopicCount,
    .prepareMemoryAndInstallSize,
    .chargeMemoryExpansion,
    .chargeLogEmission,
    .loadPayload,
    .allocateTopicArray,
    .popTopicsSequentially,
    .copyPayload,
    .constructLog,
    .appendJournal,
    .reportLogIfEnabled,
    .traceEndIfInstructionTracing,
    .dispatchContinue
  ]

def descriptor : Opcode → Descriptor
  | .log0 =>
    {
      instruction := "LOG0"
      opcodeByte := 160
      topicCount := 0
      handlerBody := "LogOpcode<EvmInstructions.Op0>"
      programCounterDelta := 1
      headerStackInputs := 2
      effectOrder := expectedEffectOrder
    }
  | .log1 =>
    {
      instruction := "LOG1"
      opcodeByte := 161
      topicCount := 1
      handlerBody := "LogOpcode<EvmInstructions.Op1>"
      programCounterDelta := 1
      headerStackInputs := 2
      effectOrder := expectedEffectOrder
    }
  | .log2 =>
    {
      instruction := "LOG2"
      opcodeByte := 162
      topicCount := 2
      handlerBody := "LogOpcode<EvmInstructions.Op2>"
      programCounterDelta := 1
      headerStackInputs := 2
      effectOrder := expectedEffectOrder
    }
  | .log3 =>
    {
      instruction := "LOG3"
      opcodeByte := 163
      topicCount := 3
      handlerBody := "LogOpcode<EvmInstructions.Op3>"
      programCounterDelta := 1
      headerStackInputs := 2
      effectOrder := expectedEffectOrder
    }
  | .log4 =>
    {
      instruction := "LOG4"
      opcodeByte := 164
      topicCount := 4
      handlerBody := "LogOpcode<EvmInstructions.Op4>"
      programCounterDelta := 1
      headerStackInputs := 2
      effectOrder := expectedEffectOrder
    }

structure Schedule where
  logBase : Nat
  logTopic : Nat
  logDataByte : Nat
  memoryLinear : Nat
  memoryQuadraticDivisor : Nat
  memoryQuadraticDivisorPositive : 0 < memoryQuadraticDivisor
  maxMemorySize : Nat
  deriving DecidableEq, Repr

def amsterdamSchedule : Schedule :=
  {
    logBase := 375
    logTopic := 375
    logDataByte := 8
    memoryLinear := 3
    memoryQuadraticDivisor := 512
    memoryQuadraticDivisorPositive := by decide
    maxMemorySize := 2147483616
  }

structure LogEntry where
  address : UInt256
  data : List Byte
  topics : List UInt256
  deriving DecidableEq, Repr

structure MachineState where
  pc : Nat
  gas : GasState
  stack : Stack
  memory : Memory
  executingAccount : UInt256
  logs : List LogEntry
  traceLogs : Bool
  reportedLogs : List LogEntry
  deriving Repr

inductive Status where
  | ok | outOfGas | stackUnderflow | staticCallViolation | extractionMismatch
  deriving DecidableEq, Repr

structure Outcome where
  status : Status
  state : MachineState
  deriving Repr

def memoryWords (memory : Memory) : Nat := memory.bytes.length / 32

def memoryCost (schedule : Schedule) (words : Nat) : Nat :=
  words * schedule.memoryLinear + words * words / schedule.memoryQuadraticDivisor

def memoryRangeValid (schedule : Schedule) (offset length : UInt256) : Bool :=
  length.val = 0 ||
    (length.val ≤ schedule.maxMemorySize && offset.val ≤ schedule.maxMemorySize - length.val)

def prepareMemory (schedule : Schedule) (memory : Memory) (offset length : UInt256) :
    Option (Nat × Memory) :=
  if length.val = 0 then
    some (0, memory)
  else if memoryRangeValid schedule offset length then
    let expanded := memory.expand offset.val length.val
    let oldWords := memoryWords memory
    let newWords := memoryWords expanded
    some (memoryCost schedule newWords - memoryCost schedule oldWords, expanded)
  else
    none

def emissionCost (schedule : Schedule) (opcode : Opcode) (dataSize : Nat) : Nat :=
  schedule.logBase + (descriptor opcode).topicCount * schedule.logTopic +
    dataSize * schedule.logDataByte

def advancePc (opcode : Opcode) (state : MachineState) : MachineState :=
  { state with pc := state.pc + (descriptor opcode).programCounterDelta }

def exhaustExecutionGas (state : MachineState) : MachineState :=
  { state with gas := { state.gas with gasLeft := 0 } }

inductive TopicPop where
  | success (topics : List UInt256) (stack : Stack)
  | underflow (popped : List UInt256) (stack : Stack)
  deriving Repr

def popTopics : Nat → Stack → List UInt256 → TopicPop
  | 0, stack, popped => .success popped stack
  | count + 1, stack, popped =>
    match stack.pop with
    | none => .underflow popped stack
    | some (topic, tail) => popTopics count tail (popped ++ [topic])

def executeCore (schedule : Schedule) (opcode : Opcode) (isStatic : Bool)
    (state : MachineState) : Outcome :=
  let advanced := advancePc opcode state
  if isStatic then
    { status := .staticCallViolation, state := advanced }
  else
    match state.stack.popTwo with
    | none => { status := .stackUnderflow, state := advanced }
    | some (offset, length, afterHeader) =>
      match prepareMemory schedule state.memory offset length with
      | none => { status := .outOfGas, state := { advanced with stack := afterHeader } }
      | some (expansionCost, expandedMemory) =>
        let prepared := { advanced with stack := afterHeader, memory := expandedMemory }
        match chargeExecution expansionCost state.gas with
        | .error _ => { status := .outOfGas, state := exhaustExecutionGas prepared }
        | .ok afterExpansion =>
          let expandedAndCharged := { prepared with gas := afterExpansion }
          match chargeExecution (emissionCost schedule opcode length.val) afterExpansion with
          | .error _ => { status := .outOfGas, state := exhaustExecutionGas expandedAndCharged }
          | .ok afterEmission =>
            let charged := { expandedAndCharged with gas := afterEmission }
            let data := readRange expandedMemory.bytes offset.val length.val
            match popTopics (descriptor opcode).topicCount afterHeader [] with
            | .underflow _ partialStack =>
              { status := .stackUnderflow, state := { charged with stack := partialStack } }
            | .success topics finalStack =>
              let entry : LogEntry := { address := state.executingAccount, data, topics }
              let journaled := { charged with stack := finalStack, logs := state.logs ++ [entry] }
              let reported := if state.traceLogs then
                { journaled with reportedLogs := state.reportedLogs ++ [entry] }
              else journaled
              { status := .ok, state := reported }

def execute (schedule : Schedule) (opcode : Opcode) (isStatic : Bool)
    (state : MachineState) : Outcome :=
  if (descriptor opcode).effectOrder = expectedEffectOrder ∧
      (descriptor opcode).headerStackInputs = 2 then
    executeCore schedule opcode isStatic state
  else
    { status := .extractionMismatch, state := advancePc opcode state }

def tracingFlag : DispatchTable → String
  | .noTrace | .noTraceCancelable => "OffFlag"
  | .traced | .tracedCancelable => "OnFlag"

def cancelableFlag : DispatchTable → String
  | .noTrace | .traced => "OffFlag"
  | .noTraceCancelable | .tracedCancelable => "OnFlag"

def expectedClosedRoot (opcode : Opcode) (table : DispatchTable) : String :=
  "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<" ++ (descriptor opcode).handlerBody ++ "," ++
    tracingFlag table ++ "," ++ cancelableFlag table ++ ",OnFlag>"

def specializations : List Specialization :=
  [
    { opcode := .log0, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<LogOpcode<EvmInstructions.Op0>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .log0, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<LogOpcode<EvmInstructions.Op0>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .log0, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<LogOpcode<EvmInstructions.Op0>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .log0, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<LogOpcode<EvmInstructions.Op0>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .log1, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<LogOpcode<EvmInstructions.Op1>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .log1, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<LogOpcode<EvmInstructions.Op1>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .log1, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<LogOpcode<EvmInstructions.Op1>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .log1, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<LogOpcode<EvmInstructions.Op1>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .log2, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<LogOpcode<EvmInstructions.Op2>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .log2, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<LogOpcode<EvmInstructions.Op2>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .log2, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<LogOpcode<EvmInstructions.Op2>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .log2, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<LogOpcode<EvmInstructions.Op2>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .log3, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<LogOpcode<EvmInstructions.Op3>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .log3, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<LogOpcode<EvmInstructions.Op3>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .log3, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<LogOpcode<EvmInstructions.Op3>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .log3, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<LogOpcode<EvmInstructions.Op3>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .log4, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<LogOpcode<EvmInstructions.Op4>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .log4, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<LogOpcode<EvmInstructions.Op4>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .log4, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<LogOpcode<EvmInstructions.Op4>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .log4, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<LogOpcode<EvmInstructions.Op4>,OnFlag,OnFlag,OnFlag>" }
  ]

def forkLineage : List String :=
  [
    "Olympic",
    "Frontier",
    "Homestead",
    "Dao",
    "TangerineWhistle",
    "SpuriousDragon",
    "Byzantium",
    "Constantinople",
    "ConstantinopleFix",
    "Istanbul",
    "MuirGlacier",
    "Berlin",
    "London",
    "ArrowGlacier",
    "GrayGlacier",
    "Paris",
    "Shanghai",
    "Cancun",
    "Prague",
    "Osaka",
    "BPO1",
    "BPO2",
    "Amsterdam"
  ]

def openExtractionObligations : List String :=
  [
    "Roslyn syntax admission does not prove C# compilation, CLR/JIT execution, function-pointer dispatch, or tail-call behavior.",
    "The proof assumes the admitted unsafe big-endian stack operations, UInt256 conversions, Address representation, and Hash256 span constructor implement the shared Lean word values.",
    "The proof models logical EVM memory bytes and size; backing-array allocation, pooling, initialization, aliasing, and allocation failure remain provider and CLR obligations.",
    "The log journal and optional ReportLog callback are modeled as ordered append-only observations; callback exceptions, tracer internals, frame rollback, receipt construction, and Bloom accumulation remain outside this opcode step.",
    "Cancellation polling, opcode counters, instruction-trace payloads, exceptional-frame settlement, and database persistence remain outside this projection."
  ]

end Eip803x.Generated.LogOpcodeKernel
