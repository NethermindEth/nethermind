-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- Generated from round-tripped, source-admitted semantic IR. Definitions only; do not edit.
-- The imported operational module supplies representation types, not this executable body.
-- Extractor version: 1.0.0
-- Canonical IR SHA-256: 7b1c3dd3beb0942a5c15e25657598bdc52b34d849cec8909efb863edec0827b3
-- Source closure SHA-256: aef29a36090ffa29e8d96b903bdadd58fb6019a78da11d81f4fe07966942c768

import ControlFlowOpcodeExtractor.Specification.ControlFlowExecution

namespace Eip803x.Generated.ControlFlowOpcodeKernel

open Eip803x.GasMachine
open Eip803x.Evm.MemoryStackControl
open Eip803x.Evm.MemoryStackControl.Stack

abbrev Opcode := Eip803x.Evm.ControlFlowExecution.Opcode
abbrev DispatchTable := Eip803x.Evm.ControlFlowExecution.DispatchTable
abbrev Activation := Eip803x.Evm.ControlFlowExecution.Activation
abbrev Schedule := Eip803x.Evm.ControlFlowExecution.Schedule
abbrev Status := Eip803x.Evm.ControlFlowExecution.Status
abbrev TraceEvent := Eip803x.Evm.ControlFlowExecution.TraceEvent
abbrev MachineState := Eip803x.Evm.ControlFlowExecution.MachineState
abbrev Outcome := Eip803x.Evm.ControlFlowExecution.Outcome

inductive Operation where
  | stop | slotNumber | jump | jumpIf | programCounter | jumpDestination | return_ | revert
  deriving DecidableEq, Repr

inductive ActivationRule where
  | always | eip7843 | eip140
  deriving DecidableEq, Repr


def sourceRoot : String := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>"
def sourceFork : String := "Nethermind.Specs.Forks.Amsterdam"
def sourceGasPolicy : String := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
def sourceBuild : String := "Directory.Build.props aliases EvmWord to Vector256<byte>; Directory.Build.targets EnableZkEvm != true selects *.std.cs and excludes *.zkevm.cs"

structure Descriptor where
  opcode : Opcode
  instruction : String
  opcodeByte : Nat
  handlerBody : String
  handlerKind : String
  handlerTarget : String
  operation : Operation
  activationRule : ActivationRule
  stackInputs : Nat
  stackOutputs : Nat
  checkedBody : String
  endsInstructionTrace : Bool
  activation : String
  gasClass : String
  fixedGas : Nat
  tracePushWidth : Nat
  effectOrder : List String
  deriving DecidableEq, Repr

def descriptors : List Descriptor :=
  [
    { opcode := .stop, instruction := "STOP", opcodeByte := 0, handlerBody := "StopOpcode", handlerKind := "terminating", handlerTarget := "EvmInstructions.InstructionStop<TGasPolicy>", operation := .stop, activationRule := .always, stackInputs := 0, stackOutputs := 0, checkedBody := "false", endsInstructionTrace := false, activation := "always", gasClass := "zero", fixedGas := 0, tracePushWidth := 0, effectOrder := ["traceStart", "incrementPc", "incrementOpcodeCount", "returnStop", "outerTraceFinish"] },
    { opcode := .slotnum, instruction := "SLOTNUM", opcodeByte := 75, handlerBody := "SlotNumOpcode<TTracingInst>", handlerKind := "continuable", handlerTarget := "EvmInstructions.InstructionSlotNum<TGasPolicy,TTracingInst>", operation := .slotNumber, activationRule := .eip7843, stackInputs := 0, stackOutputs := 1, checkedBody := "false", endsInstructionTrace := false, activation := "EIP-7843", gasClass := "base", fixedGas := 2, tracePushWidth := 8, effectOrder := ["traceStart", "incrementPc", "incrementOpcodeCount", "readNullableSlot", "missingBadInstruction", "chargeBase", "checkPushOverflow", "pushUInt64", "tracePush8", "checkBodyTraceOwnership", "traceFinish"] },
    { opcode := .jump, instruction := "JUMP", opcodeByte := 86, handlerBody := "JumpOpcode<TTracingInst>", handlerKind := "continuable", handlerTarget := "EvmInstructions.InstructionJump<TGasPolicy,TSkipJumpDest>", operation := .jump, activationRule := .always, stackInputs := 1, stackOutputs := 0, checkedBody := "false", endsInstructionTrace := false, activation := "always", gasClass := "mid", fixedGas := 8, tracePushWidth := 0, effectOrder := ["traceStart", "incrementPc", "incrementOpcodeCount", "chargeMid", "checkDepth", "popDestination", "validateInstructionBoundary", "untracedCountAndAdvanceJumpDest", "untracedChargeJumpDest", "checkBodyTraceOwnership", "traceFinish"] },
    { opcode := .jumpi, instruction := "JUMPI", opcodeByte := 87, handlerBody := "ExecuteJumpIfOpcode<TTracingInst,TCancelable>", handlerKind := "jumpIf", handlerTarget := "EvmInstructions.InstructionJumpIf<TGasPolicy,TSkipJumpDest>", operation := .jumpIf, activationRule := .always, stackInputs := 2, stackOutputs := 0, checkedBody := "false", endsInstructionTrace := false, activation := "always", gasClass := "high", fixedGas := 10, tracePushWidth := 0, effectOrder := ["traceStart", "incrementPc", "incrementOpcodeCount", "chargeHigh", "checkDepth", "popDestinationAndCondition", "zeroConditionFallthrough", "validateTakenDestination", "untracedCountAndAdvanceJumpDest", "untracedChargeJumpDest", "dedicatedTraceFinish"] },
    { opcode := .pc, instruction := "PC", opcodeByte := 88, handlerBody := "ProgramCounterOpcode<TTracingInst>", handlerKind := "continuable", handlerTarget := "EvmInstructions.InstructionProgramCounter<TGasPolicy,TTracingInst>", operation := .programCounter, activationRule := .always, stackInputs := 0, stackOutputs := 1, checkedBody := "!TTracingInst.IsActive", endsInstructionTrace := false, activation := "always", gasClass := "base", fixedGas := 2, tracePushWidth := 4, effectOrder := ["traceStart", "incrementPc", "incrementOpcodeCount", "chargeBase", "checkPushOverflow", "pushPreincrementPc", "tracePush4", "checkBodyTraceOwnership", "traceFinish"] },
    { opcode := .jumpdest, instruction := "JUMPDEST", opcodeByte := 91, handlerBody := "JumpDestOpcode", handlerKind := "continuable", handlerTarget := "EvmInstructions.InstructionJumpDest<TGasPolicy>", operation := .jumpDestination, activationRule := .always, stackInputs := 0, stackOutputs := 0, checkedBody := "false", endsInstructionTrace := false, activation := "always", gasClass := "jumpDest", fixedGas := 1, tracePushWidth := 0, effectOrder := ["traceStart", "incrementPc", "incrementOpcodeCount", "chargeJumpDest", "checkBodyTraceOwnership", "traceFinish"] },
    { opcode := .return_, instruction := "RETURN", opcodeByte := 243, handlerBody := "ReturnOpcode", handlerKind := "terminating", handlerTarget := "EvmInstructions.InstructionReturn<TGasPolicy>", operation := .return_, activationRule := .always, stackInputs := 2, stackOutputs := 0, checkedBody := "false", endsInstructionTrace := false, activation := "always", gasClass := "memory", fixedGas := 0, tracePushWidth := 0, effectOrder := ["traceStart", "incrementPc", "incrementOpcodeCount", "popOffsetAndLengthAtomically", "validateRange", "computeExpansionCost", "chargeExpansion", "installLogicalMemorySize", "loadBytes", "stageOwnedOutput", "returnStop", "outerTraceFinish"] },
    { opcode := .revert, instruction := "REVERT", opcodeByte := 253, handlerBody := "RevertOpcode", handlerKind := "terminating", handlerTarget := "EvmInstructions.InstructionRevert<TGasPolicy>", operation := .revert, activationRule := .eip140, stackInputs := 2, stackOutputs := 0, checkedBody := "false", endsInstructionTrace := false, activation := "Byzantium REVERT", gasClass := "memory", fixedGas := 0, tracePushWidth := 0, effectOrder := ["traceStart", "incrementPc", "incrementOpcodeCount", "popOffsetAndLengthAtomically", "validateRange", "computeExpansionCost", "chargeExpansion", "installLogicalMemorySize", "loadBytes", "stageOwnedOutput", "returnRevert", "outerTraceFinish"] }
  ]

def descriptorAdmitted : Bool :=
  descriptors.length = 8 && descriptors.all fun item =>
    item.instruction = item.opcode.instruction && item.opcodeByte = item.opcode.byte &&
      !item.endsInstructionTrace

structure Specialization where
  opcode : Opcode
  table : DispatchTable
  tracingFlag : String
  cancelableFlag : String
  continuableFlag : String
  enabledRoot : String
  disabledRoot : String
  deriving DecidableEq, Repr

def specializations : List Specialization :=
  [
    { opcode := .stop, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OffFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<StopOpcode,OffFlag,OffFlag,OffFlag>", disabledRoot := "" },
    { opcode := .stop, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OffFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<StopOpcode,OffFlag,OnFlag,OffFlag>", disabledRoot := "" },
    { opcode := .stop, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OffFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<StopOpcode,OnFlag,OffFlag,OffFlag>", disabledRoot := "" },
    { opcode := .stop, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OffFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<StopOpcode,OnFlag,OnFlag,OffFlag>", disabledRoot := "" },
    { opcode := .slotnum, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<SlotNumOpcode<OffFlag>,OffFlag,OffFlag,OnFlag>", disabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<BadInstructionOpcode,OffFlag,OffFlag,OffFlag>" },
    { opcode := .slotnum, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<SlotNumOpcode<OffFlag>,OffFlag,OnFlag,OnFlag>", disabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<BadInstructionOpcode,OffFlag,OnFlag,OffFlag>" },
    { opcode := .slotnum, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<SlotNumOpcode<OnFlag>,OnFlag,OffFlag,OnFlag>", disabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<BadInstructionOpcode,OnFlag,OffFlag,OffFlag>" },
    { opcode := .slotnum, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<SlotNumOpcode<OnFlag>,OnFlag,OnFlag,OnFlag>", disabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<BadInstructionOpcode,OnFlag,OnFlag,OffFlag>" },
    { opcode := .jump, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<JumpOpcode<OffFlag>,OffFlag,OffFlag,OnFlag>", disabledRoot := "" },
    { opcode := .jump, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<JumpOpcode<OffFlag>,OffFlag,OnFlag,OnFlag>", disabledRoot := "" },
    { opcode := .jump, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<JumpOpcode<OnFlag>,OnFlag,OffFlag,OnFlag>", disabledRoot := "" },
    { opcode := .jump, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<JumpOpcode<OnFlag>,OnFlag,OnFlag,OnFlag>", disabledRoot := "" },
    { opcode := .jumpi, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteJumpIfOpcode<OffFlag,OffFlag>", disabledRoot := "" },
    { opcode := .jumpi, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteJumpIfOpcode<OffFlag,OnFlag>", disabledRoot := "" },
    { opcode := .jumpi, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteJumpIfOpcode<OnFlag,OffFlag>", disabledRoot := "" },
    { opcode := .jumpi, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteJumpIfOpcode<OnFlag,OnFlag>", disabledRoot := "" },
    { opcode := .pc, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<ProgramCounterOpcode<OffFlag>,OffFlag,OffFlag,OnFlag>", disabledRoot := "" },
    { opcode := .pc, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<ProgramCounterOpcode<OffFlag>,OffFlag,OnFlag,OnFlag>", disabledRoot := "" },
    { opcode := .pc, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<ProgramCounterOpcode<OnFlag>,OnFlag,OffFlag,OnFlag>", disabledRoot := "" },
    { opcode := .pc, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<ProgramCounterOpcode<OnFlag>,OnFlag,OnFlag,OnFlag>", disabledRoot := "" },
    { opcode := .jumpdest, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<JumpDestOpcode,OffFlag,OffFlag,OnFlag>", disabledRoot := "" },
    { opcode := .jumpdest, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<JumpDestOpcode,OffFlag,OnFlag,OnFlag>", disabledRoot := "" },
    { opcode := .jumpdest, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<JumpDestOpcode,OnFlag,OffFlag,OnFlag>", disabledRoot := "" },
    { opcode := .jumpdest, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<JumpDestOpcode,OnFlag,OnFlag,OnFlag>", disabledRoot := "" },
    { opcode := .return_, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OffFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<ReturnOpcode,OffFlag,OffFlag,OffFlag>", disabledRoot := "" },
    { opcode := .return_, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OffFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<ReturnOpcode,OffFlag,OnFlag,OffFlag>", disabledRoot := "" },
    { opcode := .return_, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OffFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<ReturnOpcode,OnFlag,OffFlag,OffFlag>", disabledRoot := "" },
    { opcode := .return_, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OffFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<ReturnOpcode,OnFlag,OnFlag,OffFlag>", disabledRoot := "" },
    { opcode := .revert, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OffFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<RevertOpcode,OffFlag,OffFlag,OffFlag>", disabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<BadInstructionOpcode,OffFlag,OffFlag,OffFlag>" },
    { opcode := .revert, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OffFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<RevertOpcode,OffFlag,OnFlag,OffFlag>", disabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<BadInstructionOpcode,OffFlag,OnFlag,OffFlag>" },
    { opcode := .revert, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OffFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<RevertOpcode,OnFlag,OffFlag,OffFlag>", disabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<BadInstructionOpcode,OnFlag,OffFlag,OffFlag>" },
    { opcode := .revert, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OffFlag", enabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<RevertOpcode,OnFlag,OnFlag,OffFlag>", disabledRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<BadInstructionOpcode,OnFlag,OnFlag,OffFlag>" }
  ]

def specializationAdmitted (opcode : Opcode) (table : DispatchTable) : Bool :=
  specializations.any fun item => item.opcode = opcode && item.table = table

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

def semanticBindings : List String :=
  [
    "8 opcodes times 4 trace/cancellation tables yield exactly 32 enabled Amsterdam roots",
    "SLOTNUM and REVERT disabled gates share exactly 4 BadInstruction roots",
    "instruction trace starts before PC/opcode-count increment and closes at the admitted handler/RunByteCode boundary",
    "all 8 descriptors have false instruction-trace ownership; generic bodies inherit IOpcodeBody.EndsInstructionTrace false while JUMPI uses its dedicated close",
    "trace projection preserves distinct callback order, bottom-first logical stack snapshots, and 4-byte PC / 8-byte SLOTNUM push payloads without claiming concrete tracer adapter representation",
    "jump validity is independently scanned from source-admitted STOP, JUMPDEST, PUSH1, and PUSH32 facts and rejects PUSH immediate bytes and destinations above Int32.MaxValue",
    "untraced taken JUMP/JUMPI fuse the landed JUMPDEST count, PC advance, and one-gas charge",
    "RETURN/REVERT pop atomically, charge expansion before TryLoad installs logical memory size, and stage an owned output on success",
    "failed execution-gas charges leave gas zero while non-OOG failures preserve the preceding handler-local debit and mutation residue"
  ]

def openExtractionObligations : List String :=
  [
    "Roslyn source/member admission does not prove C# compilation, CLR/JIT/AOT execution, function-pointer tail calls, unsafe memory safety, or hardware behavior.",
    "Trace proofs use an explicit semantic projection; concrete ExecutionEnvironment, TraceMemory, TraceStack, and tracer implementation representations remain open.",
    "The admitted UInt256 and unsafe EvmStack representation is assumed to match the Lean top-first bounded stack without corruption.",
    "The generated jump scanner is independent; admitted scalar/SIMD bitmap implementations are assumed extensionally equal to its PUSH-aware instruction-boundary predicate on production-reachable code of signed-int length.",
    "EvmPooledMemory allocation, pooling, zero initialization, bounds, and ReadOnlyMemory ownership are assumed to implement the modeled byte memory.",
    "Handler-entry states are restricted to the production RunByteCode invariant that staged ReturnData is null; terminal RETURN/REVERT are the only covered handlers that install it.",
    "Fault PC and stack residues describe the dispatcher-local programCounter and EvmStack head; RunByteCode does not commit those locals to VmState on exceptional failure.",
    "Cancellation token state, polling, and OperationCanceledException propagation remain outside one-handler semantics; all four compiled roots are admitted.",
    "Frame rollback, caller returndata installation, transaction/block settlement, persistence, alternate gas policies, and zkEVM execution remain outside this package."
  ]

def descriptor (opcode : Opcode) : Descriptor :=
  match descriptors.find? fun item => item.opcode = opcode with
  | some item => item
  | none =>
    { opcode := opcode, instruction := "", opcodeByte := 0, handlerBody := "",
      handlerKind := "", handlerTarget := "", operation := .stop,
      activationRule := .always, stackInputs := 0, stackOutputs := 0,
      checkedBody := "", endsInstructionTrace := false, activation := "", gasClass := "", fixedGas := 0,
      tracePushWidth := 0,
      effectOrder := [] }

def amsterdamSchedule : Schedule :=
  { zero := 0
    base := 2
    mid := 8
    high := 10
    jumpdest := 1
    memoryLinear := 3
    memoryQuadraticDivisor := 512
    maxMemorySize := 2147483616 }

def amsterdamActivation : Activation := ⟨true, true⟩

structure JumpValidationProfile where
  stopOpcode : Nat
  jumpDestinationOpcode : Nat
  pushFirstOpcode : Nat
  pushLastOpcode : Nat
  maximumDestination : Nat
  deriving DecidableEq, Repr

def jumpValidationProfile : JumpValidationProfile :=
  { stopOpcode := 0
    jumpDestinationOpcode := 91
    pushFirstOpcode := 96
    pushLastOpcode := 127
    maximumDestination := 2147483647 }

def extractedInstructionWidth (profile : JumpValidationProfile) (opcode : Byte) : Nat :=
  if profile.pushFirstOpcode ≤ opcode.val && opcode.val ≤ profile.pushLastOpcode then
    opcode.val - profile.pushFirstOpcode + 2
  else 1

def scanExtractedJumpDestination (profile : JumpValidationProfile)
    (code : List Byte) (target pc fuel : Nat) : Bool :=
  match fuel with
  | 0 => false
  | fuel + 1 =>
    if pc = target then
      match code[pc]? with
      | some value => value.val = profile.jumpDestinationOpcode
      | none => false
    else
      match code[pc]? with
      | none => false
      | some value => scanExtractedJumpDestination profile code target
          (pc + extractedInstructionWidth profile value) fuel

def extractedValidJumpDestination (profile : JumpValidationProfile)
    (code : List Byte) (target : Nat) : Bool :=
  match code with
  | [] => false
  | first :: _ =>
    if first.val = profile.stopOpcode then false
    else target ≤ profile.maximumDestination && target < code.length &&
      scanExtractedJumpDestination profile code target 0 code.length

def operationActive (activation : Activation) : ActivationRule → Bool
  | .always => true
  | .eip7843 => activation.eip7843
  | .eip140 => activation.revert

def dispatchEnter (table : DispatchTable) (opcode : Opcode)
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

def reportPush (table : DispatchTable) (width : Nat) (value : Eip803x.UInt256)
    (state : MachineState) : MachineState :=
  if table.tracing then
    { state with trace := state.trace ++ [.stackPush ((wordToBytes value).drop (32 - width))] }
  else state

def dispatchFinish (table : DispatchTable) (endsInstructionTrace : Bool)
    (state : MachineState) : MachineState :=
  if table.tracing && !endsInstructionTrace then
    { state with trace := state.trace ++ [.finish state.gas.gasLeft] }
  else state

def exhaust (state : MachineState) : MachineState :=
  { state with gas := { state.gas with gasLeft := 0 } }

def outcome (status : Status) (state : MachineState) : Outcome := ⟨status, state⟩

abbrev DebitResult := Eip803x.Evm.ControlFlowExecution.Debit

def debitExecution (amount : Nat) (state : MachineState) : DebitResult :=
  match chargeExecution amount state.gas with
  | .error _ => .outOfGas (exhaust state)
  | .ok gas => .paid { state with gas := gas }

def memoryWords (memory : Memory) : Nat := memory.bytes.length / 32

def memoryCost (schedule : Schedule) (words : Nat) : Nat :=
  words * schedule.memoryLinear + words * words / schedule.memoryQuadraticDivisor

def rangeAllowed (schedule : Schedule) (offset length : Eip803x.UInt256) : Bool :=
  length.val = 0 ||
    (length.val <= schedule.maxMemorySize && offset.val <= schedule.maxMemorySize - length.val)

abbrev ExpansionPlan := Eip803x.Evm.ControlFlowExecution.MemoryPreparation

def prepareExpansion (schedule : Schedule) (memory : Memory)
    (offset length : Eip803x.UInt256) : ExpansionPlan :=
  if length.val = 0 then .prepared 0
  else if rangeAllowed schedule offset length then
    let expanded := memory.expand offset.val length.val
    .prepared (memoryCost schedule (memoryWords expanded) - memoryCost schedule (memoryWords memory))
  else .invalid

def finishSuccess (table : DispatchTable) (endsInstructionTrace : Bool)
    (state : MachineState) : Outcome :=
  outcome .ok (dispatchFinish table endsInstructionTrace state)

def pushFixed (cost traceWidth : Nat) (table : DispatchTable) (endsInstructionTrace : Bool)
    (value : MachineState → Eip803x.UInt256) (entered : MachineState) : Outcome :=
  match debitExecution cost entered with
  | .outOfGas failed => outcome .outOfGas failed
  | .paid charged =>
    let pushed := value charged
    match charged.stack.push pushed with
    | none => outcome .stackOverflow charged
    | some stack => finishSuccess table endsInstructionTrace
        (reportPush table traceWidth pushed { charged with stack := stack })

def runSlotNumber (descriptor : Descriptor) (table : DispatchTable)
    (entered : MachineState) : Outcome :=
  match entered.slotNumber with
  | none => outcome .badInstruction entered
  | some slot => pushFixed descriptor.fixedGas descriptor.tracePushWidth table descriptor.endsInstructionTrace
      (fun _ => Eip803x.Evm.Word.ofNat slot) entered

def runJumpDestination (descriptor : Descriptor) (table : DispatchTable)
    (entered : MachineState) : Outcome :=
  match debitExecution descriptor.fixedGas entered with
  | .outOfGas failed => outcome .outOfGas failed
  | .paid charged => finishSuccess table descriptor.endsInstructionTrace charged

def fuseJumpDestination (schedule : Schedule) (table : DispatchTable)
    (endsInstructionTrace : Bool) (target : Nat)
    (state : MachineState) : Outcome :=
  if table.tracing then finishSuccess table endsInstructionTrace { state with pc := target }
  else
    let skipped := { state with pc := target + 1, opcodeCount := state.opcodeCount + 1 }
    match debitExecution schedule.jumpdest skipped with
    | .outOfGas failed => outcome .outOfGas failed
    | .paid charged => finishSuccess table endsInstructionTrace charged

def runJump (schedule : Schedule) (descriptor : Descriptor) (table : DispatchTable)
    (entered : MachineState) : Outcome :=
  match debitExecution descriptor.fixedGas entered with
  | .outOfGas failed => outcome .outOfGas failed
  | .paid charged =>
    match charged.stack.pop with
    | none => outcome .stackUnderflow charged
    | some (target, tail) =>
      let popped := { charged with stack := tail }
      if extractedValidJumpDestination jumpValidationProfile popped.code target.val then
        fuseJumpDestination schedule table descriptor.endsInstructionTrace target.val popped
      else outcome .invalidJump popped

def runJumpIf (schedule : Schedule) (descriptor : Descriptor) (table : DispatchTable)
    (entered : MachineState) : Outcome :=
  match debitExecution descriptor.fixedGas entered with
  | .outOfGas failed => outcome .outOfGas failed
  | .paid charged =>
    match charged.stack.popTwo with
    | none => outcome .stackUnderflow charged
    | some (target, condition, tail) =>
      let popped := { charged with stack := tail }
      if condition.val = 0 then finishSuccess table false popped
      else if extractedValidJumpDestination jumpValidationProfile popped.code target.val then
        fuseJumpDestination schedule table false target.val popped
      else outcome .invalidJump popped

def runReturnLike (schedule : Schedule) (terminal : Status) (entered : MachineState) : Outcome :=
  match entered.stack.popTwo with
  | none => outcome .stackUnderflow entered
  | some (offset, length, tail) =>
    let popped := { entered with stack := tail }
    match prepareExpansion schedule popped.memory offset length with
    | .invalid => outcome .outOfGas popped
    | .prepared expansionCost =>
      match debitExecution expansionCost popped with
      | .outOfGas failed => outcome .outOfGas failed
      | .paid charged =>
        let (memory, data) := charged.memory.readRange offset.val length.val
        outcome terminal { charged with memory := memory, stagedOutput := some data }

def executeSemantic (schedule : Schedule) (table : DispatchTable) (descriptor : Descriptor)
    (entered : MachineState) : Outcome :=
  match descriptor.operation with
  | .stop => outcome .stop entered
  | .slotNumber => runSlotNumber descriptor table entered
  | .jump => runJump schedule descriptor table entered
  | .jumpIf => runJumpIf schedule descriptor table entered
  | .programCounter => pushFixed descriptor.fixedGas descriptor.tracePushWidth table descriptor.endsInstructionTrace
      (fun state => Eip803x.Evm.Word.ofNat (state.pc - 1)) entered
  | .jumpDestination => runJumpDestination descriptor table entered
  | .return_ => runReturnLike schedule .stop entered
  | .revert => runReturnLike schedule .revert entered

def executeCore (schedule : Schedule) (activation : Activation) (table : DispatchTable)
    (opcode : Opcode) (state : MachineState) : Outcome :=
  let entered := dispatchEnter table opcode state
  let selected := descriptor opcode
  if operationActive activation selected.activationRule then
    executeSemantic schedule table selected entered
  else outcome .badInstruction entered

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

def executeExtracted (schedule : Schedule) (activation : Activation)
    (table : DispatchTable) (opcode : Opcode) (state : MachineState) : Outcome :=
  if descriptorAdmitted && specializationAdmitted opcode table then
    closeFrame table (executeCore schedule activation table opcode state)
  else outcome .extractionMismatch state

def executeAmsterdam (table : DispatchTable) (opcode : Opcode) (state : MachineState) : Outcome :=
  executeExtracted amsterdamSchedule amsterdamActivation table opcode state

end Eip803x.Generated.ControlFlowOpcodeKernel
