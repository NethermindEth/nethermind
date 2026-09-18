-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- This file is generated. Do not edit.
-- Extractor version: 1.0.0
-- Production closure SHA-256: f18096b02985e8f1c8eebf76cf490a224fcfc0bde0e28e04297f4d3814fa7982
-- Canonical IR SHA-256: 000ac1c52939114a4d81de554763d1f9248e4b57c1dec4508f488a8ad1fc2d87

import Eip803x.Gas
import Eip803x.Evm.MemoryStackControl
import Eip803x.Generated.ExtendedStackDecoderKernel

namespace ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel

open Eip803x
open Eip803x.Evm
open Eip803x.Evm.MemoryStackControl

def schemaVersion : Nat := 1
def extractorVersion : String := "1.0.0"
def kernel : String := "ExtendedStackOpcodeKernel"
def root : String := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>"
def gasPolicy : String := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
def fork : String := "Nethermind.Specs.Forks.Amsterdam"
def buildFlavor : String := "standard-mainnet"
def forkGate : String := "IReleaseSpec.IsEip8024Enabled"
def defaultHandler : String := "BadInstructionOpcode"
def pcOrder : String := "opcode-pc-before-gas;valid-immediate-pc-before-stack"
def gasOrder : String := "very-low-before-decode-and-stack"
def faultOrder : String := "fork-disabled-before-gas;out-of-gas-before-invalid-immediate-before-underflow-before-overflow"
def stackLimit : Nat := 1024
def veryLowGas : Nat := 3

inductive Opcode where
  | dupN
  | swapN
  | exchange
  deriving DecidableEq, Repr

def allOpcodes : List Opcode :=
  [.dupN, .swapN, .exchange]

structure Descriptor where
  name : String
  instruction : String
  opcodeByte : Nat
  handlerBody : String
  forwarder : String
  instructionMethod : String
  decoderMethod : String
  stackMethod : String
  dispatchAssignment : String
  gasClass : String
  fixedGas : Nat
  stackGrowth : Nat
  pcOrder : String
  gasOrder : String
  faultOrder : String
  activation : String
  deriving DecidableEq, Repr

def descriptor : Opcode → Descriptor
  | .dupN => {
      name := "dupN"
      instruction := "DUPN"
      opcodeByte := 230
      handlerBody := "DupNOpcode<TTracingInst>"
      forwarder := "DupNOpcode<TTracingInst>.Execute"
      instructionMethod := "InstructionDupN"
      decoderMethod := "TryDecodeSingle"
      stackMethod := "Dup"
      dispatchAssignment := "lookup[(int)Instruction.DUPN]=OpcodeHandler<DupNOpcode<TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := "veryLow"
      fixedGas := 3
      stackGrowth := 1
      pcOrder := "opcode-pc-before-gas;valid-immediate-pc-before-stack"
      gasOrder := "very-low-before-decode-and-stack"
      faultOrder := "fork-disabled-before-gas;out-of-gas-before-invalid-immediate-before-underflow-before-overflow"
      activation := "IReleaseSpec.IsEip8024Enabled" }
  | .swapN => {
      name := "swapN"
      instruction := "SWAPN"
      opcodeByte := 231
      handlerBody := "SwapNOpcode<TTracingInst>"
      forwarder := "SwapNOpcode<TTracingInst>.Execute"
      instructionMethod := "InstructionSwapN"
      decoderMethod := "TryDecodeSingle"
      stackMethod := "Swap"
      dispatchAssignment := "lookup[(int)Instruction.SWAPN]=OpcodeHandler<SwapNOpcode<TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := "veryLow"
      fixedGas := 3
      stackGrowth := 0
      pcOrder := "opcode-pc-before-gas;valid-immediate-pc-before-stack"
      gasOrder := "very-low-before-decode-and-stack"
      faultOrder := "fork-disabled-before-gas;out-of-gas-before-invalid-immediate-before-underflow-before-overflow"
      activation := "IReleaseSpec.IsEip8024Enabled" }
  | .exchange => {
      name := "exchange"
      instruction := "EXCHANGE"
      opcodeByte := 232
      handlerBody := "ExchangeOpcode<TTracingInst>"
      forwarder := "ExchangeOpcode<TTracingInst>.Execute"
      instructionMethod := "InstructionExchange"
      decoderMethod := "TryDecodePair"
      stackMethod := "Exchange"
      dispatchAssignment := "lookup[(int)Instruction.EXCHANGE]=OpcodeHandler<ExchangeOpcode<TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := "veryLow"
      fixedGas := 3
      stackGrowth := 0
      pcOrder := "opcode-pc-before-gas;valid-immediate-pc-before-stack"
      gasOrder := "very-low-before-decode-and-stack"
      faultOrder := "fork-disabled-before-gas;out-of-gas-before-invalid-immediate-before-underflow-before-overflow"
      activation := "IReleaseSpec.IsEip8024Enabled" }

def descriptorText (opcode : Opcode) : List String :=
  let d := descriptor opcode
  [d.name, d.instruction, d.handlerBody, d.forwarder, d.instructionMethod,
   d.decoderMethod, d.stackMethod, d.dispatchAssignment, d.gasClass, d.pcOrder,
   d.gasOrder, d.faultOrder, d.activation]
def descriptorNumbers (opcode : Opcode) : List Nat :=
  let d := descriptor opcode
  [d.opcodeByte, d.fixedGas, d.stackGrowth]

inductive DispatchTable where
  | noTrace | noTraceCancelable | traced | tracedCancelable
  deriving DecidableEq, Repr

structure Specialization where
  opcode : Opcode
  dispatchTable : DispatchTable
  tracingFlag : String
  cancelableFlag : String
  continuableFlag : String
  enabledRoot : String
  disabledRoot : String
  deriving DecidableEq, Repr

def dispatchTables : List (String × String × String) :=
  [
    ("NoTrace", "OffFlag", "OffFlag"),
    ("NoTraceCancelable", "OffFlag", "OnFlag"),
    ("Traced", "OnFlag", "OffFlag"),
    ("TracedCancelable", "OnFlag", "OnFlag")
  ]

def specializations : List Specialization :=
  [
    { opcode := .dupN, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", enabledRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupNOpcode<OffFlag>,OffFlag,OffFlag,OnFlag>", disabledRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<BadInstructionOpcode,OffFlag,OffFlag,OffFlag>" },
    { opcode := .dupN, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", enabledRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupNOpcode<OffFlag>,OffFlag,OnFlag,OnFlag>", disabledRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<BadInstructionOpcode,OffFlag,OnFlag,OffFlag>" },
    { opcode := .dupN, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", enabledRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupNOpcode<OnFlag>,OnFlag,OffFlag,OnFlag>", disabledRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<BadInstructionOpcode,OnFlag,OffFlag,OffFlag>" },
    { opcode := .dupN, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", enabledRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupNOpcode<OnFlag>,OnFlag,OnFlag,OnFlag>", disabledRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<BadInstructionOpcode,OnFlag,OnFlag,OffFlag>" },
    { opcode := .swapN, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", enabledRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapNOpcode<OffFlag>,OffFlag,OffFlag,OnFlag>", disabledRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<BadInstructionOpcode,OffFlag,OffFlag,OffFlag>" },
    { opcode := .swapN, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", enabledRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapNOpcode<OffFlag>,OffFlag,OnFlag,OnFlag>", disabledRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<BadInstructionOpcode,OffFlag,OnFlag,OffFlag>" },
    { opcode := .swapN, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", enabledRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapNOpcode<OnFlag>,OnFlag,OffFlag,OnFlag>", disabledRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<BadInstructionOpcode,OnFlag,OffFlag,OffFlag>" },
    { opcode := .swapN, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", enabledRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapNOpcode<OnFlag>,OnFlag,OnFlag,OnFlag>", disabledRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<BadInstructionOpcode,OnFlag,OnFlag,OffFlag>" },
    { opcode := .exchange, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", enabledRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<ExchangeOpcode<OffFlag>,OffFlag,OffFlag,OnFlag>", disabledRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<BadInstructionOpcode,OffFlag,OffFlag,OffFlag>" },
    { opcode := .exchange, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", enabledRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<ExchangeOpcode<OffFlag>,OffFlag,OnFlag,OnFlag>", disabledRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<BadInstructionOpcode,OffFlag,OnFlag,OffFlag>" },
    { opcode := .exchange, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", enabledRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<ExchangeOpcode<OnFlag>,OnFlag,OffFlag,OnFlag>", disabledRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<BadInstructionOpcode,OnFlag,OffFlag,OffFlag>" },
    { opcode := .exchange, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", enabledRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<ExchangeOpcode<OnFlag>,OnFlag,OnFlag,OnFlag>", disabledRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<BadInstructionOpcode,OnFlag,OnFlag,OffFlag>" }
  ]
def exactRootCount : Nat := specializations.length

def forkLineage : List String :=
  ["Olympic", "Frontier", "Homestead", "Dao", "TangerineWhistle", "SpuriousDragon", "Byzantium", "Constantinople", "ConstantinopleFix", "Istanbul", "MuirGlacier", "Berlin", "London", "ArrowGlacier", "GrayGlacier", "Paris", "Shanghai", "Cancun", "Prague", "Osaka", "BPO1", "BPO2", "Amsterdam"]
def openExtractionObligations : List String :=
  [
    "Roslyn admission and source routing do not prove CLR, JIT, function-pointer, or unsafe EvmStack execution.",
    "Tracing, cancellation, opcode counters, tail calls, frame settlement, memory, allocation, and transaction or block integration remain outside this one-opcode projection.",
    "The generated semantics models only standard-mainnet EthereumGasPolicy and the Amsterdam EIP-8024 gate; zkEVM and alternate gas policies are excluded.",
    "The source-derived decoder refinement is imported as an independent prior result; this package does not claim whole-loop, frame, or CLR verification."
  ]

abbrev Byte := MemoryStackControl.Byte
abbrev OperandStack := MemoryStackControl.Stack

inductive DecodedOperation where
  | duplicate (depth : Nat)
  | swap (depth : Nat)
  | exchange (first second : Nat)
  deriving DecidableEq, Repr

inductive Error where
  | outOfGas | badInstruction | stackUnderflow | stackOverflow
  deriving DecidableEq, Repr

structure State where
  code : List Byte
  stack : OperandStack
  gas : GasState
  pc : Nat
  deriving Repr

inductive Outcome where
  | success (state : State)
  | error (reason : Error) (state : State)
  deriving Repr

namespace Outcome
def state : Outcome → State
  | .success state => state
  | .error _ state => state
def error? : Outcome → Option Error
  | .success _ => none
  | .error reason _ => some reason
end Outcome

namespace State
def advancePc (state : State) : State := { state with pc := state.pc + 1 }
def withGasLeft (state : State) (gasLeft : Nat) : State :=
  { state with gas := { state.gas with gasLeft } }
end State

structure Plan where
  fixedGas : Nat
  opcodePcFirst : Bool
  validImmediatePcBeforeStack : Bool
  gasBeforeDecode : Bool
  gasExhaustsOnFailure : Bool
  deriving DecidableEq, Repr

inductive Debit where
  | paid (state : State) | outOfGas (state : State)
  deriving Repr

def debit (plan : Plan) (state : State) : Debit :=
  if plan.fixedGas ≤ state.gas.gasLeft then
    .paid (state.withGasLeft (state.gas.gasLeft - plan.fixedGas))
  else
    .outOfGas (if plan.gasExhaustsOnFailure then state.withGasLeft 0 else state)

def readAt? {α : Type} : Nat → List α → Option α
  | _, [] => none
  | 0, head :: _ => some head
  | index + 1, _ :: tail => readAt? index tail

def replaceAt {α : Type} : Nat → α → List α → Option (List α)
  | _, _, [] => none
  | 0, value, _ :: tail => some (value :: tail)
  | index + 1, value, head :: tail =>
    (replaceAt index value tail).map (head :: ·)

def immediateOrZero (code : List Byte) (pc : Nat) : Byte :=
  (readAt? pc code).getD MemoryStackControl.zeroByte

def decodeSingle? (immediate : Byte) : Option Nat :=
  let decoded := Eip803x.Generated.ExtendedStackDecoderKernel.decodeSingle immediate.val
  if decoded.isValid then some decoded.depth else none

def decodePair? (immediate : Byte) : Option (Nat × Nat) :=
  let decoded := Eip803x.Generated.ExtendedStackDecoderKernel.decodePair immediate.val
  if decoded.isValid then
    some (decoded.firstPosition - 1, decoded.secondPosition - 1)
  else none

def decodeOperation (opcode : Opcode) (immediate : Byte) : Except Error DecodedOperation :=
  match opcode with
  | .dupN =>
    match decodeSingle? immediate with
    | some depth => .ok (.duplicate depth)
    | none => .error .badInstruction
  | .swapN =>
    match decodeSingle? immediate with
    | some depth => .ok (.swap depth)
    | none => .error .badInstruction
  | .exchange =>
    match decodePair? immediate with
    | some pair => .ok (.exchange pair.1 pair.2)
    | none => .error .badInstruction

def duplicateWords (depth : Nat) (words : List UInt256) : Except Error (List UInt256) :=
  if 0 < depth ∧ depth ≤ words.length then
    match readAt? (depth - 1) words with
    | none => .error .stackUnderflow
    | some value =>
      if words.length < stackLimit then .ok (value :: words) else .error .stackOverflow
  else .error .stackUnderflow

def swapWords (depth : Nat) (words : List UInt256) : Except Error (List UInt256) :=
  match depth, words with
  | 0, _ => .error .stackUnderflow
  | _, [] => .error .stackUnderflow
  | depth, top :: tail =>
    match readAt? (depth - 1) tail with
    | none => .error .stackUnderflow
    | some other =>
      match replaceAt (depth - 1) top tail with
      | none => .error .stackUnderflow
      | some changedTail => .ok (other :: changedTail)

def exchangeWords (first second : Nat) (words : List UInt256) : Except Error (List UInt256) :=
  match readAt? first words, readAt? second words with
  | some firstValue, some secondValue =>
    match replaceAt first secondValue words with
    | none => .error .stackUnderflow
    | some firstReplacement =>
      match replaceAt second firstValue firstReplacement with
      | none => .error .stackUnderflow
      | some result => .ok result
  | _, _ => .error .stackUnderflow

def toStackResult : Except Error (List UInt256) → Except Error OperandStack
  | .error reason => .error reason
  | .ok words => .ok (MemoryStackControl.Stack.fromWords words)

def applyDecoded : DecodedOperation → OperandStack → Except Error OperandStack
  | .duplicate depth, stack => toStackResult (duplicateWords depth stack.words)
  | .swap depth, stack => toStackResult (swapWords depth stack.words)
  | .exchange first second, stack => toStackResult (exchangeWords first second stack.words)

def plan (opcode : Opcode) : Plan :=
  let d := descriptor opcode
  { fixedGas := d.fixedGas
    opcodePcFirst := d.pcOrder == pcOrder
    validImmediatePcBeforeStack := d.pcOrder == pcOrder
    gasBeforeDecode := d.gasOrder == gasOrder
    gasExhaustsOnFailure := d.faultOrder == faultOrder }

def executePlanned (opcode : Opcode) (plan : Plan) (eip8024Enabled : Bool)
    (state : State) : Outcome :=
  let entered := if plan.opcodePcFirst then state.advancePc else state
  if !eip8024Enabled then .error .badInstruction entered
  else match debit plan entered with
  | .outOfGas afterOutOfGas => .error .outOfGas afterOutOfGas
  | .paid afterGas =>
    let immediate := immediateOrZero afterGas.code afterGas.pc
    match decodeOperation opcode immediate with
    | .error reason => .error reason afterGas
    | .ok operation =>
      let afterImmediate :=
        if plan.validImmediatePcBeforeStack then afterGas.advancePc else afterGas
      match applyDecoded operation afterImmediate.stack with
      | .ok stack => .success { afterImmediate with stack }
      | .error reason => .error reason afterImmediate

def executeAtFork (eip8024Enabled : Bool) (opcode : Opcode) (state : State) : Outcome :=
  executePlanned opcode (plan opcode) eip8024Enabled state

def execute (opcode : Opcode) (state : State) : Outcome := executeAtFork true opcode state

end ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel
