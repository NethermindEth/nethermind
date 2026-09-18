-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- Generated from deserialized, source-derived IR. Definitions only; do not edit.
-- Extractor version: 1.0.0
-- Canonical IR SHA-256: 6d32c82548c9768a084893c0401a83105d95f5eed6aa5714d8dfa64ecfa14667
-- Source closure SHA-256: 91020ccf6f718b948c79e8e57ce6bfa0b3ed2362fb3d0d691398da777e766534

import Eip803x.Gas
import Eip803x.Evm.MemoryStackControl

namespace Eip803x.Generated.Keccak256OpcodeKernel

open GasMachine
open Eip803x.Evm.MemoryStackControl

def sourceRoot : String := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>"
def sourceFork : String := "Nethermind.Specs.Forks.Amsterdam"
def hashBoundary : String := "Nethermind.Core.Crypto.KeccakCache.ComputeTo:oracle-only"

abbrev HashOracle := List Byte -> UInt256

inductive Effect where
  | traceStartIfInstructionTracing
  | incrementProgramCounter
  | incrementOpcodeCount
  | popOffsetLengthAtomically
  | calculateCheckedCeilingWords
  | chargeBaseAndWords
  | rejectWordOverflowAfterCharge
  | installLogicalMemoryExpansion
  | chargeMemoryExpansion
  | loadExactZeroExtendedSlice
  | callHashOracle
  | checkPushDepth
  | traceHashPushIfInstructionTracing
  | replaceTwoStackInputsWithHash
  | traceEndIfInstructionTracing
  | dispatchContinue
  deriving DecidableEq, Repr

inductive DispatchTable where
  | noTrace | noTraceCancelable | traced | tracedCancelable
  deriving DecidableEq, Repr

structure Descriptor where
  instruction : String
  opcodeByte : Nat
  handlerBody : String
  handlerTarget : String
  programCounterDelta : Nat
  opcodeCountDelta : Nat
  stackInputs : Nat
  stackOutputs : Nat
  hasCheckedBody : Bool
  pushDepthFlag : String
  effectOrder : List Effect
  deriving DecidableEq, Repr

def expectedEffectOrder : List Effect :=
  [
    .traceStartIfInstructionTracing,
    .incrementProgramCounter,
    .incrementOpcodeCount,
    .popOffsetLengthAtomically,
    .calculateCheckedCeilingWords,
    .chargeBaseAndWords,
    .rejectWordOverflowAfterCharge,
    .installLogicalMemoryExpansion,
    .chargeMemoryExpansion,
    .loadExactZeroExtendedSlice,
    .callHashOracle,
    .checkPushDepth,
    .traceHashPushIfInstructionTracing,
    .replaceTwoStackInputsWithHash,
    .traceEndIfInstructionTracing,
    .dispatchContinue
  ]

def descriptor : Descriptor :=
  {
    instruction := "KECCAK256"
    opcodeByte := 32
    handlerBody := "KeccakOpcode<TTracingInst>"
    handlerTarget := "EvmInstructions.InstructionKeccak256<TGasPolicy,TTracingInst>"
    programCounterDelta := 1
    opcodeCountDelta := 1
    stackInputs := 2
    stackOutputs := 1
    hasCheckedBody := false
    pushDepthFlag := "OnFlag"
    effectOrder := expectedEffectOrder
  }

structure Schedule where
  base : Nat
  word : Nat
  memoryLinear : Nat
  memoryQuadraticDivisor : Nat
  memoryQuadraticDivisorPositive : 0 < memoryQuadraticDivisor
  maxMemorySize : Nat
  maxUInt64 : Nat
  maxUInt32 : Nat
  deriving DecidableEq, Repr

def amsterdamSchedule : Schedule :=
  {
    base := 30
    word := 6
    memoryLinear := 3
    memoryQuadraticDivisor := 512
    memoryQuadraticDivisorPositive := by decide
    maxMemorySize := 2147483616
    maxUInt64 := 18446744073709551615
    maxUInt32 := 4294967295
  }

inductive TraceEvent where
  | start (instruction : String) (pc gasLeft : Nat)
  | push (value : UInt256)
  | finish (gasLeft : Nat)
  deriving DecidableEq, Repr

structure MachineState where
  pc : Nat
  opcodeCount : Nat
  gas : GasState
  stack : Stack
  memory : Memory
  trace : List TraceEvent
  deriving Repr

inductive Status where
  | ok | outOfGas | stackUnderflow | stackOverflow | extractionMismatch
  deriving DecidableEq, Repr

structure Outcome where
  status : Status
  state : MachineState
  deriving Repr

def isTracing : DispatchTable -> Bool
  | .noTrace | .noTraceCancelable => false
  | .traced | .tracedCancelable => true

def beginInstruction (table : DispatchTable) (state : MachineState) : MachineState :=
  let traced := if isTracing table then
    { state with trace := state.trace ++ [.start descriptor.instruction state.pc state.gas.gasLeft] }
  else state
  { traced with pc := traced.pc + descriptor.programCounterDelta, opcodeCount := traced.opcodeCount + descriptor.opcodeCountDelta }

def checkedWords (schedule : Schedule) (length : UInt256) : Nat × Bool :=
  if length.val > schedule.maxUInt64 then
    (0, true)
  else
    let result := (length.val + 31) / 32
    if result > schedule.maxUInt32 then (0, true) else (result, false)

def dynamicCost (schedule : Schedule) (words : Nat) : Nat :=
  schedule.base + schedule.word * words

def memoryWords (memory : Memory) : Nat := memory.bytes.length / 32

def memoryCost (schedule : Schedule) (words : Nat) : Nat :=
  words * schedule.memoryLinear +
    words * words / schedule.memoryQuadraticDivisor

def memoryRangeValid (schedule : Schedule) (offset length : UInt256) : Bool :=
  length.val = 0 ||
    (length.val <= schedule.maxMemorySize && offset.val <= schedule.maxMemorySize - length.val)

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

def exhaustExecutionGas (state : MachineState) : MachineState :=
  { state with gas := { state.gas with gasLeft := 0 } }

def tracePush (table : DispatchTable) (value : UInt256) (state : MachineState) : MachineState :=
  if isTracing table then { state with trace := state.trace ++ [.push value] } else state

def finishInstruction (table : DispatchTable) (state : MachineState) : MachineState :=
  if isTracing table then { state with trace := state.trace ++ [.finish state.gas.gasLeft] } else state

def executeCore (schedule : Schedule) (hash : HashOracle) (table : DispatchTable)
    (state : MachineState) : Outcome :=
  let entered := beginInstruction table state
  match state.stack.popTwo with
  | none => { status := .stackUnderflow, state := entered }
  | some (offset, length, tail) =>
    let (wordCount, invalidWordCount) := checkedWords schedule length
    match chargeExecution (dynamicCost schedule wordCount) state.gas with
    | .error _ =>
      { status := .outOfGas, state := exhaustExecutionGas { entered with stack := tail } }
    | .ok afterDynamic =>
      let dynamicallyCharged := { entered with gas := afterDynamic, stack := tail }
      if invalidWordCount then
        { status := .outOfGas, state := dynamicallyCharged }
      else
        match prepareMemory schedule state.memory offset length with
        | none => { status := .outOfGas, state := dynamicallyCharged }
        | some (expansionCost, expanded) =>
          let prepared := { dynamicallyCharged with memory := expanded }
          match chargeExecution expansionCost afterDynamic with
          | .error _ => { status := .outOfGas, state := exhaustExecutionGas prepared }
          | .ok afterExpansion =>
            let input := readRange expanded.bytes offset.val length.val
            let value := hash input
            match tail.push value with
            | none => { status := .stackOverflow, state := { prepared with gas := afterExpansion } }
            | some finalStack =>
              let pushed := tracePush table value { prepared with gas := afterExpansion, stack := finalStack }
              { status := .ok, state := finishInstruction table pushed }

structure Specialization where
  opcode : String
  table : DispatchTable
  tracingFlag : String
  cancellationFlag : String
  closedRoot : String
  deriving DecidableEq, Repr

def tracingFlag : DispatchTable -> String
  | .noTrace | .noTraceCancelable => "OffFlag"
  | .traced | .tracedCancelable => "OnFlag"

def cancellationFlag : DispatchTable -> String
  | .noTrace | .traced => "OffFlag"
  | .noTraceCancelable | .tracedCancelable => "OnFlag"

def expectedClosedRoot (table : DispatchTable) : String :=
  sourceRoot ++ ".ExecuteOpcode<KeccakOpcode<" ++ tracingFlag table ++ ">," ++
    tracingFlag table ++ "," ++ cancellationFlag table ++ ",OnFlag>"

def specializations : List Specialization :=
  [
    { opcode := "keccak256", table := .noTrace, tracingFlag := "OffFlag", cancellationFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<KeccakOpcode<OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := "keccak256", table := .noTraceCancelable, tracingFlag := "OffFlag", cancellationFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<KeccakOpcode<OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := "keccak256", table := .traced, tracingFlag := "OnFlag", cancellationFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<KeccakOpcode<OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := "keccak256", table := .tracedCancelable, tracingFlag := "OnFlag", cancellationFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<KeccakOpcode<OnFlag>,OnFlag,OnFlag,OnFlag>" }
  ]

def descriptorAdmitted : Bool :=
  descriptor.instruction = "KECCAK256" && descriptor.opcodeByte = 32 &&
    descriptor.handlerBody = "KeccakOpcode<TTracingInst>" &&
    descriptor.handlerTarget = "EvmInstructions.InstructionKeccak256<TGasPolicy,TTracingInst>" &&
    descriptor.programCounterDelta = 1 && descriptor.opcodeCountDelta = 1 &&
    descriptor.stackInputs = 2 && descriptor.stackOutputs = 1 &&
    descriptor.hasCheckedBody = false && descriptor.pushDepthFlag = "OnFlag" &&
    descriptor.effectOrder = expectedEffectOrder

def specializationAdmitted (table : DispatchTable) : Bool :=
  specializations.any fun item => item.opcode = "keccak256" && item.table = table &&
    item.tracingFlag = tracingFlag table && item.cancellationFlag = cancellationFlag table &&
    item.closedRoot = expectedClosedRoot table

def execute (schedule : Schedule) (hash : HashOracle) (table : DispatchTable)
    (state : MachineState) : Outcome :=
  if descriptorAdmitted && specializationAdmitted table then
    executeCore schedule hash table state
  else
    { status := .extractionMismatch, state := beginInstruction table state }

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
    "The hash is an oracle: no equivalence claim covers Keccak, ValueKeccak, KeccakCache, cache synchronization, native code, or CLR representation.",
    "Roslyn syntax admission does not prove C# compilation, CLR/JIT/AOT execution, function-pointer tail calls, or hardware behavior.",
    "The logical model assumes the admitted UInt256, ValueHash256, unsafe stack, and memory adapters represent the shared Lean words and zero-extended bytes.",
    "Backing allocation, pooling, fresh-array initialization, aliasing, allocation failure, and optional trace payload callbacks or exceptions remain outside this immediate opcode step.",
    "Cancellation polling, counter overflow, outer terminal-opcode and error trace closure, exceptional-frame settlement, rollback, transaction composition, and persistence remain outside this projection."
  ]

end Eip803x.Generated.Keccak256OpcodeKernel
