-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- Generated from round-tripped, source-admitted semantic IR. Definitions only; do not edit.
-- The imported module supplies representation types, never the executable transition.
-- Extractor version: 1.0.0
-- Canonical IR SHA-256: b74a9454441980789ee625402d8d37dae750c7767614e100f5502c20208b3aa5
-- Source closure SHA-256: 7d4e0ec140b314202330fa1f027cbce8c9093faaa0a1a1c5336df4904413615c

import CallCreateOpcodeExtractor.Specification.CallCreateExecution

namespace Eip803x.Generated.CallCreateOpcodeKernel

open Eip803x.GasMachine
open Eip803x.Evm.MemoryStackControl.Stack
open Eip803x.Evm.CallCreateExecution
abbrev EvmStack := Eip803x.Evm.MemoryStackControl.Stack
abbrev EvmMemory := Eip803x.Evm.MemoryStackControl.Memory
abbrev EvmByte := Eip803x.Evm.MemoryStackControl.Byte

def sourceRoot : String := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>"
def sourceFork : String := "Nethermind.Specs.Forks.Amsterdam"
def sourceGasPolicy : String := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
def sourceBuild : String := "Directory.Build.targets: EnableZkEvm != true selects *.std.cs and excludes *.zkevm.cs"

def amsterdamSchedule : Schedule :=
  {
    callBase := 0, callValue := 11300, callStipend := 2300
    warmAccess := 100, coldAccess := 3000
    createAccess := 12000, initCodeWord := 2, create2HashWord := 6
    accountWrite := 9000, selfDestructBase := 5000
    newAccountState := 183600, createState := 183600
    codeDepositExecutionPerWord := 6, codeDepositStatePerByte := 1530
    memoryLinear := 3, memoryQuadraticDivisor := 512
    maxMemorySize := 2147483616, maxInitCodeSize := 131072, maxCodeSize := 24576
    maxCallDepth := 1024, stackLimit := 1024
  }

structure Descriptor where
  opcode : Opcode
  instruction : String
  opcodeByte : Nat
  family : String
  handlerBody : String
  handlerTarget : String
  operation : String
  valueRule : String
  targetRule : String
  resultRule : String
  stackInputs : Nat
  stackOutputs : Nat
  terminates : Bool
  ownsPreChildTraceEnd : Bool
  effectOrder : List String
  deriving DecidableEq, Repr

def descriptors : List Descriptor :=
  [
    { opcode := .call, instruction := "CALL", opcodeByte := 241, family := "call", handlerBody := "CallOpcode<EvmInstructions.OpCall,TTracingInst,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>", handlerTarget := "EvmInstructions.InstructionCall<EthereumGasPolicy,EvmInstructions.OpCall,TTracingInst,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>", operation := "call", valueRule := "explicit", targetRule := "codeSource", resultRule := "successBool", stackInputs := 7, stackOutputs := 1, terminates := false, ownsPreChildTraceEnd := false, effectOrder := ["traceStart", "advancePcAndCount", "popRequestedGas", "popCodeSource", "normalizeCodeSourceLow160", "popValue", "popInputOffsetLengthAndOutputOffsetLength", "rejectStaticValue", "chargeValue", "chargeCallBase", "expandAndChargeInputMemory", "expandAndChargeOutputMemory", "warmAndChargeCodeSource", "discoverDelegation", "warmAndChargeDelegatedTarget", "classifyDeadTarget", "chargeNewAccountState", "reserveEip150Execution", "addStipend", "checkDepthAndBalance", "handleEarlyZeroOrRoute", "stageChildOrInlineResult", "closeOrSuspend", "resumeMerge", "pushResultBeforeResumeTrace"] },
    { opcode := .callcode, instruction := "CALLCODE", opcodeByte := 242, family := "call", handlerBody := "CallOpcode<EvmInstructions.OpCallCode,TTracingInst,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>", handlerTarget := "EvmInstructions.InstructionCall<EthereumGasPolicy,EvmInstructions.OpCallCode,TTracingInst,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>", operation := "callcode", valueRule := "explicit", targetRule := "executingAccount", resultRule := "successBool", stackInputs := 7, stackOutputs := 1, terminates := false, ownsPreChildTraceEnd := false, effectOrder := ["traceStart", "advancePcAndCount", "popRequestedGas", "popCodeSource", "normalizeCodeSourceLow160", "popValue", "popInputOffsetLengthAndOutputOffsetLength", "allowStaticCallcodeValue", "chargeValue", "chargeCallBase", "expandAndChargeInputMemory", "expandAndChargeOutputMemory", "warmAndChargeCodeSource", "discoverDelegation", "warmAndChargeDelegatedTarget", "skipDeadSelfTargetCharge", "reserveEip150Execution", "addStipend", "checkDepthAndBalance", "handleEarlyZeroOrRoute", "stageChild", "closeOrSuspend", "resumeMerge", "pushResultBeforeResumeTrace"] },
    { opcode := .delegatecall, instruction := "DELEGATECALL", opcodeByte := 244, family := "call", handlerBody := "CallOpcode<EvmInstructions.OpDelegateCall,TTracingInst,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>", handlerTarget := "EvmInstructions.InstructionCall<EthereumGasPolicy,EvmInstructions.OpDelegateCall,TTracingInst,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>", operation := "delegatecall", valueRule := "environment", targetRule := "executingAccount", resultRule := "successBool", stackInputs := 6, stackOutputs := 1, terminates := false, ownsPreChildTraceEnd := false, effectOrder := ["traceStart", "advancePcAndCount", "popRequestedGas", "popCodeSource", "normalizeCodeSourceLow160", "deriveEnvironmentValue", "popInputOffsetLengthAndOutputOffsetLength", "chargeCallBase", "expandAndChargeInputMemory", "expandAndChargeOutputMemory", "warmAndChargeCodeSource", "discoverDelegation", "warmAndChargeDelegatedTarget", "reserveEip150Execution", "checkDepth", "handleEarlyZeroOrRoute", "stageChild", "closeOrSuspend", "resumeMerge", "pushResultBeforeResumeTrace"] },
    { opcode := .staticcall, instruction := "STATICCALL", opcodeByte := 250, family := "call", handlerBody := "CallOpcode<EvmInstructions.OpStaticCall,TTracingInst,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>", handlerTarget := "EvmInstructions.InstructionCall<EthereumGasPolicy,EvmInstructions.OpStaticCall,TTracingInst,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>", operation := "staticcall", valueRule := "zero", targetRule := "codeSource", resultRule := "successBool", stackInputs := 6, stackOutputs := 1, terminates := false, ownsPreChildTraceEnd := false, effectOrder := ["traceStart", "advancePcAndCount", "popRequestedGas", "popCodeSource", "normalizeCodeSourceLow160", "deriveZeroValue", "popInputOffsetLengthAndOutputOffsetLength", "chargeCallBase", "expandAndChargeInputMemory", "expandAndChargeOutputMemory", "warmAndChargeCodeSource", "discoverDelegation", "warmAndChargeDelegatedTarget", "reserveEip150Execution", "checkDepth", "handleEarlyZeroOrRoute", "requireNoInstructionOrActionTrace", "excludeRipemd160DirectPrecompile", "boundReturnedGasToReservation", "tryStandardInlinePrecompile", "stageChildOtherwise", "closeOrSuspend", "resumeMerge", "pushResultBeforeResumeTrace"] },
    { opcode := .create, instruction := "CREATE", opcodeByte := 240, family := "create", handlerBody := "CreateOpcode<EvmInstructions.OpCreate,TTracingInst,OnFlag,EvmInstructions.CreateSpec<OnFlag,OnFlag,OnFlag,Eip8038On>>", handlerTarget := "EvmInstructions.InstructionCreate<EthereumGasPolicy,EvmInstructions.OpCreate,TTracingInst,OnFlag,EvmInstructions.CreateSpec<OnFlag,OnFlag,OnFlag,Eip8038On>>", operation := "create", valueRule := "explicit", targetRule := "derivedCreate", resultRule := "createdAddress", stackInputs := 3, stackOutputs := 1, terminates := false, ownsPreChildTraceEnd := true, effectOrder := ["traceStart", "advancePcAndCount", "rejectStatic", "popValueOffsetLength", "checkInitCodeLimit", "checkedCeilingWords", "chargeCreateAccessAndInitWords", "expandAndChargeInitMemory", "checkDepth", "loadExactOwnedInitCode", "checkBalance", "checkNonce", "deriveAddressOracle", "warmDestination", "classifyPhysicalLogicalCollision", "chargeCreateState", "traceFinishBeforeReservation", "reserveEip150AllExecution", "incrementCreatorNonce", "snapshotWorld", "analyzeInitCode", "collisionBurnAndRefillOrStageChild", "skipGenericCreateTraceClosure", "resumeRefundChildGas", "chargeParentDepositExecution", "chargeParentDepositState", "commitChild", "repayStateSpill", "pushAddressBeforeResumeTrace"] },
    { opcode := .create2, instruction := "CREATE2", opcodeByte := 245, family := "create", handlerBody := "CreateOpcode<EvmInstructions.OpCreate2,TTracingInst,OnFlag,EvmInstructions.CreateSpec<OnFlag,OnFlag,OnFlag,Eip8038On>>", handlerTarget := "EvmInstructions.InstructionCreate<EthereumGasPolicy,EvmInstructions.OpCreate2,TTracingInst,OnFlag,EvmInstructions.CreateSpec<OnFlag,OnFlag,OnFlag,Eip8038On>>", operation := "create2", valueRule := "explicit", targetRule := "derivedCreate2", resultRule := "createdAddress", stackInputs := 4, stackOutputs := 1, terminates := false, ownsPreChildTraceEnd := true, effectOrder := ["traceStart", "advancePcAndCount", "rejectStatic", "popValueOffsetLength", "popSalt", "checkInitCodeLimit", "checkedCeilingWords", "chargeCreateAccessInitAndHashWords", "expandAndChargeInitMemory", "checkDepth", "loadExactOwnedInitCode", "checkBalance", "checkNonce", "deriveAddressHashOracle", "warmDestination", "classifyPhysicalLogicalCollision", "chargeCreateState", "traceFinishBeforeReservation", "reserveEip150AllExecution", "incrementCreatorNonce", "snapshotWorld", "analyzeInitCode", "collisionBurnAndRefillOrStageChild", "skipGenericCreateTraceClosure", "resumeRefundChildGas", "chargeParentDepositExecution", "chargeParentDepositState", "commitChild", "repayStateSpill", "pushAddressBeforeResumeTrace"] },
    { opcode := .selfdestruct, instruction := "SELFDESTRUCT", opcodeByte := 255, family := "selfdestruct", handlerBody := "SelfDestructOpcode<OnFlag,OnFlag,EvmInstructions.SelfDestructSpec<EvmInstructions.AccessSpec<OnFlag,Eip8038On>,OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>", handlerTarget := "EvmInstructions.InstructionSelfDestruct<EthereumGasPolicy,OnFlag,OnFlag,EvmInstructions.SelfDestructSpec<EvmInstructions.AccessSpec<OnFlag,Eip8038On>,OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>", operation := "selfdestruct", valueRule := "none", targetRule := "beneficiary", resultRule := "stop", stackInputs := 1, stackOutputs := 0, terminates := true, ownsPreChildTraceEnd := false, effectOrder := ["traceStart", "advancePcAndCount", "rejectStatic", "chargeSelfDestructBase", "popBeneficiary", "normalizeBeneficiaryLow160", "warmAndChargeBeneficiary", "markDestroyIfCreatedThisTransaction", "readBalance", "traceAction", "classifyBeneficiary", "chargeAccountWrite", "chargeNewAccountState", "createOrCreditBeneficiary", "applySelfTargetException", "appendSelfDestructLog", "debitSource", "terminalStop", "outerTerminalTraceClosure"] }
  ]

def descriptor (opcode : Opcode) : Option Descriptor :=
  descriptors.find? fun item => item.opcode == opcode

def semanticOpcode : String -> Option Opcode
  | "call" => some .call
  | "callcode" => some .callcode
  | "delegatecall" => some .delegatecall
  | "staticcall" => some .staticcall
  | "create" => some .create
  | "create2" => some .create2
  | "selfdestruct" => some .selfdestruct
  | _ => none

structure Specialization where
  opcode : Opcode
  table : DispatchTable
  tracingFlag : String
  cancelableFlag : String
  closedRoot : String
  deriving DecidableEq, Repr

def specializations : List Specialization :=
  [
    { opcode := .call, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CallOpcode<EvmInstructions.OpCall,OffFlag,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .call, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CallOpcode<EvmInstructions.OpCall,OffFlag,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .call, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CallOpcode<EvmInstructions.OpCall,OnFlag,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .call, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CallOpcode<EvmInstructions.OpCall,OnFlag,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .callcode, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CallOpcode<EvmInstructions.OpCallCode,OffFlag,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .callcode, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CallOpcode<EvmInstructions.OpCallCode,OffFlag,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .callcode, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CallOpcode<EvmInstructions.OpCallCode,OnFlag,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .callcode, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CallOpcode<EvmInstructions.OpCallCode,OnFlag,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .delegatecall, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CallOpcode<EvmInstructions.OpDelegateCall,OffFlag,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .delegatecall, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CallOpcode<EvmInstructions.OpDelegateCall,OffFlag,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .delegatecall, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CallOpcode<EvmInstructions.OpDelegateCall,OnFlag,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .delegatecall, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CallOpcode<EvmInstructions.OpDelegateCall,OnFlag,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .staticcall, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CallOpcode<EvmInstructions.OpStaticCall,OffFlag,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .staticcall, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CallOpcode<EvmInstructions.OpStaticCall,OffFlag,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .staticcall, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CallOpcode<EvmInstructions.OpStaticCall,OnFlag,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .staticcall, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CallOpcode<EvmInstructions.OpStaticCall,OnFlag,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .create, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CreateOpcode<EvmInstructions.OpCreate,OffFlag,OnFlag,EvmInstructions.CreateSpec<OnFlag,OnFlag,OnFlag,Eip8038On>>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .create, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CreateOpcode<EvmInstructions.OpCreate,OffFlag,OnFlag,EvmInstructions.CreateSpec<OnFlag,OnFlag,OnFlag,Eip8038On>>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .create, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CreateOpcode<EvmInstructions.OpCreate,OnFlag,OnFlag,EvmInstructions.CreateSpec<OnFlag,OnFlag,OnFlag,Eip8038On>>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .create, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CreateOpcode<EvmInstructions.OpCreate,OnFlag,OnFlag,EvmInstructions.CreateSpec<OnFlag,OnFlag,OnFlag,Eip8038On>>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .create2, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CreateOpcode<EvmInstructions.OpCreate2,OffFlag,OnFlag,EvmInstructions.CreateSpec<OnFlag,OnFlag,OnFlag,Eip8038On>>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .create2, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CreateOpcode<EvmInstructions.OpCreate2,OffFlag,OnFlag,EvmInstructions.CreateSpec<OnFlag,OnFlag,OnFlag,Eip8038On>>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .create2, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CreateOpcode<EvmInstructions.OpCreate2,OnFlag,OnFlag,EvmInstructions.CreateSpec<OnFlag,OnFlag,OnFlag,Eip8038On>>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .create2, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CreateOpcode<EvmInstructions.OpCreate2,OnFlag,OnFlag,EvmInstructions.CreateSpec<OnFlag,OnFlag,OnFlag,Eip8038On>>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .selfdestruct, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<SelfDestructOpcode<OnFlag,OnFlag,EvmInstructions.SelfDestructSpec<EvmInstructions.AccessSpec<OnFlag,Eip8038On>,OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>,OffFlag,OffFlag,OffFlag>" },
    { opcode := .selfdestruct, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<SelfDestructOpcode<OnFlag,OnFlag,EvmInstructions.SelfDestructSpec<EvmInstructions.AccessSpec<OnFlag,Eip8038On>,OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>,OffFlag,OnFlag,OffFlag>" },
    { opcode := .selfdestruct, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<SelfDestructOpcode<OnFlag,OnFlag,EvmInstructions.SelfDestructSpec<EvmInstructions.AccessSpec<OnFlag,Eip8038On>,OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>,OnFlag,OffFlag,OffFlag>" },
    { opcode := .selfdestruct, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<SelfDestructOpcode<OnFlag,OnFlag,EvmInstructions.SelfDestructSpec<EvmInstructions.AccessSpec<OnFlag,Eip8038On>,OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>,OnFlag,OnFlag,OffFlag>" }
  ]

def profileAdmitted (opcode : Opcode) (table : DispatchTable) : Bool :=
  descriptors.length = 7 && specializations.length = 28 &&
    (specializations.any fun item => item.opcode == opcode && item.table == table) &&
    match descriptor opcode with
    | none => false
    | some item => semanticOpcode item.operation == some opcode && !item.effectOrder.isEmpty

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
    "CREATE and CREATE2 address derivation, RLP, init-code hashing, and Keccak are explicit oracles; no cryptographic implementation equivalence is claimed.",
    "Delegation discovery, code-cache behavior, runtime-code validation, precompile execution, and arbitrary child-frame execution are explicit oracle inputs.",
    "The generated trace projection claims event kind, order, and ownership only; capability-dependent stack, memory, return-data, and action payload equivalence remains open.",
    "World-state and journal tokens assume the admitted IWorldState, StackAccessTracker, VmState, and code-repository adapters implement the represented reads, snapshots, restores, merges, and writes.",
    "Roslyn exact-syntax admission does not prove C# compilation, CLR/JIT/AOT execution, function-pointer tail calls, unsafe stack layout, pooled allocation, native code, or hardware behavior.",
    "Tracing callbacks are assumed total and non-throwing; cancellation delivery, metrics counters, transaction and block composition, trie/database persistence, and zkEVM sources remain outside this slice."
  ]

def tableTracing : DispatchTable -> Bool
  | .noTrace | .noTraceCancelable => false
  | .traced | .tracedCancelable => true

def appendTrace (event : TraceEvent) (state : MachineState) : MachineState :=
  { state with trace := state.trace ++ [event] }

def beginInstruction (table : DispatchTable) (opcode : Opcode) (state : MachineState) : MachineState :=
  let started := if tableTracing table then appendTrace (.instructionStart opcode state.pc state.gas.gasLeft) state else state
  { started with pc := started.pc + 1, opcodeCount := started.opcodeCount + 1, stagedChild := none }

def finishInstruction (table : DispatchTable) (state : MachineState) : MachineState :=
  if tableTracing table then appendTrace (.instructionFinish state.gas.gasLeft) state else state

def fail (status : Status) (state : MachineState) : Outcome := ⟨status, state⟩

def exhaustExecution (state : MachineState) : MachineState :=
  { state with gas := { state.gas with gasLeft := 0 } }

def debitExecution (amount : Nat) (state : MachineState) : Debit :=
  match chargeExecution amount state.gas with
  | .error _ => .outOfGas (exhaustExecution state)
  | .ok gas => .paid { state with gas := gas }

def debitState (amount : Nat) (state : MachineState) : Debit :=
  match chargeState amount state.gas with
  | .error _ => .outOfGas state
  | .ok gas => .paid { state with gas := gas }

def memoryWords (memory : EvmMemory) : Nat := memory.bytes.length / 32

def memoryCost (schedule : Schedule) (words : Nat) : Nat :=
  words * schedule.memoryLinear + words * words / schedule.memoryQuadraticDivisor

def rangeAllowed (schedule : Schedule) (offset length : UInt256) : Bool :=
  length.val = 0 || (length.val <= schedule.maxMemorySize && offset.val <= schedule.maxMemorySize - length.val)

def prepareExpansion (schedule : Schedule) (memory : EvmMemory)
    (offset length : UInt256) : Expansion :=
  if length.val = 0 then .prepared 0 memory
  else if rangeAllowed schedule offset length then
    let expanded := memory.expand offset.val length.val
    .prepared (memoryCost schedule (memoryWords expanded) - memoryCost schedule (memoryWords memory)) expanded
  else .invalid

def chargeExpansion (schedule : Schedule) (offset length : UInt256) (state : MachineState) : Debit :=
  match prepareExpansion schedule state.memory offset length with
  | .invalid => .outOfGas state
  | .prepared cost memory => debitExecution cost { state with memory := memory }

def isWarm (address : Address) (state : MachineState) : Bool := address ∈ state.journal.warmAddresses

def warm (address : Address) (state : MachineState) : MachineState :=
  if isWarm address state then state
  else { state with journal := { state.journal with warmAddresses := state.journal.warmAddresses ++ [address] } }

def chargeAccess (schedule : Schedule) (address : Address) (state : MachineState) : Debit :=
  let trackingState := if state.traceAccess then warm address state else state
  let amount := if isWarm address trackingState then schedule.warmAccess else schedule.coldAccess
  debitExecution amount (warm address trackingState)

def pushResult (table : DispatchTable) (value : UInt256) (payload : List EvmByte)
    (ownsFinish : Bool) (state : MachineState) : Outcome :=
  match Eip803x.Evm.MemoryStackControl.Stack.push state.stack value with
  | none => fail .stackOverflow state
  | some stack =>
      let pushed := { state with stack := stack }
      let traced := if tableTracing table then appendTrace (.stackPush payload) pushed else pushed
      fail .continued (if ownsFinish then finishInstruction table traced else traced)

def pushZero (table : DispatchTable) (state : MachineState) : Outcome :=
  pushResult table Eip803x.Evm.Word.zero [0] true state

def pushOne (table : DispatchTable) (state : MachineState) : Outcome :=
  pushResult table (Eip803x.Evm.Word.ofNat 1) [1] true state

def pushAddress (table : DispatchTable) (address : Address) (state : MachineState) : Outcome :=
  pushResult table address ((Eip803x.Evm.MemoryStackControl.wordToBytes address).drop 12) true state

def pushZeroAfterOwnedFinish (table : DispatchTable) (state : MachineState) : Outcome :=
  pushResult table Eip803x.Evm.Word.zero [0] false state

def startChildAction (label : String) (frame : ChildFrame) (state : MachineState) : MachineState :=
  if state.traceActions then appendTrace (.actionStart label frame.gasEntry.child.gas.gasLeft frame.target) state
  else state

def endChildAction (child : ChildOutcome) (frame : ChildFrame) (state : MachineState) : MachineState :=
  if !state.traceActions then state
  else match child.exit with
  | .exceptional => appendTrace (.actionError child.exceptionStatus) state
  | .revert => appendTrace (.actionRevert child.gas.gas.gasLeft child.output) state
  | .success => appendTrace (.actionEnd child.gas.gas.gasLeft frame.target child.output) state

def addressOfWord (word : UInt256) : Address :=
  Eip803x.Evm.Word.ofNat (word.val % (2 ^ 160))

def popCallTail (stack : EvmStack) (requestedGas codeSource value : UInt256) : Option CallOperands × EvmStack :=
  match Eip803x.Evm.MemoryStackControl.Stack.pop stack with
  | none => (none, stack)
  | some (inputOffset, afterInputOffset) =>
    match Eip803x.Evm.MemoryStackControl.Stack.pop afterInputOffset with
    | none => (none, afterInputOffset)
    | some (inputLength, afterInputLength) =>
      match Eip803x.Evm.MemoryStackControl.Stack.pop afterInputLength with
      | none => (none, afterInputLength)
      | some (outputOffset, afterOutputOffset) =>
        match Eip803x.Evm.MemoryStackControl.Stack.pop afterOutputOffset with
        | none => (none, afterOutputOffset)
        | some (outputLength, tail) =>
          (some { requestedGas, codeSource, value, inputOffset, inputLength, outputOffset, outputLength }, tail)

def popCall (kind : CallKind) (environmentValue : UInt256) (stack : EvmStack) : Option CallOperands × EvmStack :=
  match Eip803x.Evm.MemoryStackControl.Stack.pop stack with
  | none => (none, stack)
  | some (requestedGas, afterGas) =>
    match Eip803x.Evm.MemoryStackControl.Stack.pop afterGas with
    | none => (none, afterGas)
    | some (codeSourceWord, afterSource) =>
      let codeSource := addressOfWord codeSourceWord
      match kind with
      | .call | .callcode =>
        match Eip803x.Evm.MemoryStackControl.Stack.pop afterSource with
        | none => (none, afterSource)
        | some (value, afterValue) => popCallTail afterValue requestedGas codeSource value
      | .delegatecall => popCallTail afterSource requestedGas codeSource environmentValue
      | .staticcall => popCallTail afterSource requestedGas codeSource Eip803x.Evm.Word.zero

def callKind (opcode : Opcode) : Option CallKind :=
  match opcode with
  | .call => some .call
  | .callcode => some .callcode
  | .delegatecall => some .delegatecall
  | .staticcall => some .staticcall
  | _ => none

def callHasTransfer (kind : CallKind) (value : UInt256) : Bool :=
  (kind == .call || kind == .callcode) && value.val != 0

def callCreatesAccount (kind : CallKind) (value : UInt256) (targetDead : Bool) : Bool :=
  kind == .call && value.val != 0 && targetDead

def callTarget (kind : CallKind) (operands : CallOperands) (state : MachineState) : Address :=
  match kind with
  | .call | .staticcall => operands.codeSource
  | .callcode | .delegatecall => state.environment.executingAccount

def canExecutePrecompileCallDirectly (codeSource : Address) : Bool :=
  (addressOfWord codeSource).val != 3

def inlinePrecompileGasConsistent (entry : FrameEntry) (oracle : HandlerOracle) : Bool :=
  oracle.precompileGasRemaining <= entry.child.gas.gasLeft

def addStipend (amount : Nat) (entry : FrameEntry) : FrameEntry :=
  { entry with child := { entry.child with gas := { entry.child.gas with gasLeft := entry.child.gas.gasLeft + amount } } }

def returnReserved (entry : FrameEntry) : GasState :=
  mergeSuccess entry entry.child

def inspectMemory (memory : EvmMemory) (offset length : Nat) : List EvmByte :=
  if offset + length <= memory.bytes.length then (memory.bytes.drop offset).take length else []

def completeBlockedCall (schedule : Schedule) (table : DispatchTable) (operands : CallOperands)
    (entry : FrameEntry) (chargedNew : Bool) (state : MachineState) : Outcome :=
  let reserved := { state with gas := entry.pausedParent, returnData := [] }
  let pushed : Outcome := match Eip803x.Evm.MemoryStackControl.Stack.push reserved.stack Eip803x.Evm.Word.zero with
    | none => fail .stackOverflow reserved
    | some stack =>
      let withStack := { reserved with stack := stack }
      let traced := if tableTracing table then appendTrace (.stackPush [0]) withStack else withStack
      fail .continued traced
  let inspected := if pushed.state.traceRefunds then
    appendTrace (.memoryInspect operands.inputOffset.val (inspectMemory pushed.state.memory operands.inputOffset.val 32)) pushed.state
    else pushed.state
  let preRefundTrace := if tableTracing table then
    appendTrace (.instructionError .notEnoughBalance) (finishInstruction table inspected)
    else inspected
  let returned := returnReserved entry
  let gas := if chargedNew then refillState schedule.newAccountState returned else returned
  let refunded := { preRefundTrace with gas := gas }
  let gasTraced := if tableTracing table then
    appendTrace (.gasUpdate entry.child.gas.gasLeft gas.gasLeft) refunded
    else refunded
  if pushed.status = .continued then fail .continued (finishInstruction table gasTraced)
  else { pushed with state := gasTraced }

def stageCallChild (kind : CallKind) (operands : CallOperands) (target caller : Address)
    (oracle : HandlerOracle) (chargedNew : Bool) (entry : FrameEntry)
    (table : DispatchTable) (state : MachineState) : Outcome :=
  let (_, input) := state.memory.readRange operands.inputOffset.val operands.inputLength.val
  let destination := if operands.outputLength.val = 0 then 0 else operands.outputOffset.val
  let frame : ChildFrame :=
    { opcode := (match kind with | .call => .call | .callcode => .callcode | .delegatecall => .delegatecall | .staticcall => .staticcall),
      gasEntry := entry, target := target, codeSource := operands.codeSource, caller := caller, value := operands.value, input := input,
      callDepth := state.environment.callDepth + 1,
      isStatic := kind == .staticcall || state.environment.isStatic,
      outputOffset := destination, outputLength := operands.outputLength.val,
      snapshot := oracle.rollbackWorld, journalSnapshot := state.journal,
      isPrecompile := oracle.facts.targetCodeRoute == .precompile,
      newAccountCharged := chargedNew, createStateCharged := false, createOnPhysicalAccount := false }
  let staged :=
    { state with
      gas := entry.pausedParent
      stagedChild := some frame
      world := oracle.nextWorld
      journal := oracle.nextJournal }
  let label := match kind with
    | .call => "CALL" | .callcode => "CALLCODE" | .delegatecall => "DELEGATECALL" | .staticcall => "STATICCALL"
  let instructionEnded := finishInstruction table staged
  fail .suspended (startChildAction label frame instructionEnded)

def finishCallRoute (schedule : Schedule) (table : DispatchTable) (kind : CallKind)
    (operands : CallOperands) (oracle : HandlerOracle) (chargedNew : Bool) (state : MachineState) : Outcome :=
  let requested := operands.requestedGas.val
  let baseEntry := enterFrame requested state.gas
  let entry := if callHasTransfer kind operands.value then addStipend schedule.callStipend baseEntry else baseEntry
  let stipendTraced := if callHasTransfer kind operands.value && state.traceRefunds then
    appendTrace (.extraGasPressure schedule.callStipend) state else state
  let target := callTarget kind operands state
  let caller := if kind = .delegatecall then state.environment.caller else state.environment.executingAccount
  if state.environment.callDepth >= schedule.maxCallDepth ||
      (callHasTransfer kind operands.value && oracle.facts.callerBalance < operands.value.val) then
    completeBlockedCall schedule table operands entry chargedNew stipendTraced
  else match oracle.facts.targetCodeRoute with
  | .empty | .delegatedEmpty =>
      if !tableTracing table && !state.traceActions then
        let reserved := { stipendTraced with gas := entry.pausedParent, returnData := [] }
        match Eip803x.Evm.MemoryStackControl.Stack.push reserved.stack (Eip803x.Evm.Word.ofNat 1) with
        | none => fail .stackOverflow reserved
        | some stack => fail .continued
          ({ reserved with
             stack := stack
             gas := returnReserved entry
             world := oracle.nextWorld
             journal := oracle.nextJournal })
      else stageCallChild kind operands target caller oracle chargedNew entry table stipendTraced
  | .precompile =>
      if kind == .staticcall && !tableTracing table && !state.traceActions &&
          canExecutePrecompileCallDirectly operands.codeSource then
        let child := { entry.child with gas := { entry.child.gas with gasLeft := oracle.precompileGasRemaining } }
        if !inlinePrecompileGasConsistent entry oracle then fail .oracleMismatch stipendTraced
        else if oracle.precompileSuccess then
          let clipped := oracle.precompileOutput.take operands.outputLength.val
          let completed :=
            { stipendTraced with
              gas := mergeSuccess entry child
              memory := stipendTraced.memory.writeRange operands.outputOffset.val clipped
              returnData := oracle.precompileOutput
              world := oracle.nextWorld
              journal := oracle.nextJournal }
          pushOne table completed
        else pushZero table { stipendTraced with gas := mergeException entry child, returnData := [] }
      else stageCallChild kind operands target caller oracle chargedNew entry table stipendTraced
  | .bytecode | .delegatedBytecode =>
      stageCallChild kind operands target caller oracle chargedNew entry table stipendTraced

def afterCallAccesses (schedule : Schedule) (table : DispatchTable) (kind : CallKind)
    (operands : CallOperands) (oracle : HandlerOracle) (state : MachineState) : Outcome :=
  match chargeAccess schedule operands.codeSource state with
  | .outOfGas failed => fail .outOfGas failed
  | .paid sourceCharged =>
    let delegated := oracle.facts.delegatedAddress
    let afterDelegated : Debit := match delegated with
      | none => .paid sourceCharged
      | some address => chargeAccess schedule address sourceCharged
    match afterDelegated with
    | .outOfGas failed => fail .outOfGas failed
    | .paid accessed =>
      let chargedNew := callCreatesAccount kind operands.value oracle.facts.targetDead
      if chargedNew then
        match debitState schedule.newAccountState accessed with
        | .outOfGas failed => fail .outOfGas failed
        | .paid charged => finishCallRoute schedule table kind operands oracle true charged
      else finishCallRoute schedule table kind operands oracle false accessed

def runCall (schedule : Schedule) (table : DispatchTable) (kind : CallKind)
    (oracle : HandlerOracle) (entered : MachineState) : Outcome :=
  let (operands?, tail) := popCall kind entered.environment.value entered.stack
  match operands? with
  | none => fail .stackUnderflow { entered with stack := tail }
  | some operands =>
    let popped := { entered with stack := tail }
    if entered.environment.isStatic && callHasTransfer kind operands.value && kind != .callcode then
      fail .staticViolation popped
    else
      let valueDebit : Debit := if callHasTransfer kind operands.value then debitExecution schedule.callValue popped else .paid popped
      match valueDebit with
      | .outOfGas failed => fail .outOfGas failed
      | .paid valueCharged =>
        match debitExecution schedule.callBase valueCharged with
        | .outOfGas failed => fail .outOfGas failed
        | .paid baseCharged =>
          match chargeExpansion schedule operands.inputOffset operands.inputLength baseCharged with
          | .outOfGas failed => fail .outOfGas failed
          | .paid inputExpanded =>
            match chargeExpansion schedule operands.outputOffset operands.outputLength inputExpanded with
            | .outOfGas failed => fail .outOfGas failed
            | .paid outputExpanded => afterCallAccesses schedule table kind operands oracle outputExpanded

def popCreate (kind : CreateKind) (stack : EvmStack) : Option CreateOperands × EvmStack :=
  match Eip803x.Evm.MemoryStackControl.Stack.pop stack with
  | none => (none, stack)
  | some (value, afterValue) =>
    match Eip803x.Evm.MemoryStackControl.Stack.pop afterValue with
    | none => (none, afterValue)
    | some (initOffset, afterOffset) =>
      match Eip803x.Evm.MemoryStackControl.Stack.pop afterOffset with
      | none => (none, afterOffset)
      | some (initLength, afterLength) =>
        match kind with
        | .create => (some { value, initOffset, initLength, salt := none }, afterLength)
        | .create2 =>
          match Eip803x.Evm.MemoryStackControl.Stack.pop afterLength with
          | none => (none, afterLength)
          | some (salt, tail) => (some { value, initOffset, initLength, salt := some salt }, tail)

def createWords (length : UInt256) : Option Nat :=
  if length.val < 2 ^ 64 then some ((length.val + 31) / 32) else none

def createCost (schedule : Schedule) (kind : CreateKind) (words : Nat) : Nat :=
  schedule.createAccess + schedule.initCodeWord * words +
    (if kind = .create2 then schedule.create2HashWord * words else 0)

def completeCreateWithoutChild (table : DispatchTable) (state : MachineState) : Outcome :=
  pushZero table { state with returnData := [] }

def createFactsConsistent (facts : WorldFacts) : Bool :=
  decide (
    (facts.createLogicalExists -> facts.createPhysicalExists) /\
    (facts.createCollision != .none -> facts.createPhysicalExists) /\
    ((facts.createCollision = .code || facts.createCollision = .nonce) ->
      facts.createLogicalExists))

def stageCreateChild (kind : CreateKind) (operands : CreateOperands) (oracle : HandlerOracle)
    (chargedState : Bool) (physical : Bool) (entry : FrameEntry) (state : MachineState) : Outcome :=
  let (_, input) := state.memory.readRange operands.initOffset.val operands.initLength.val
  let frame : ChildFrame :=
    { opcode := (if kind = .create then .create else .create2), gasEntry := entry,
      target := oracle.derivedCreateAddress, codeSource := oracle.derivedCreateAddress,
      caller := state.environment.executingAccount,
      value := operands.value, input := input, outputOffset := 0, outputLength := 0,
      callDepth := state.environment.callDepth + 1, isStatic := false,
      snapshot := oracle.rollbackWorld, journalSnapshot := state.journal,
      isPrecompile := false,
      newAccountCharged := false, createStateCharged := chargedState,
      createOnPhysicalAccount := physical }
  let staged :=
    { state with
      gas := entry.pausedParent
      stagedChild := some frame
      world := oracle.nextWorld
      journal := oracle.nextJournal }
  fail .suspended (startChildAction (if kind = .create then "CREATE" else "CREATE2") frame staged)

def afterCreateChecks (schedule : Schedule) (table : DispatchTable) (kind : CreateKind)
    (operands : CreateOperands) (oracle : HandlerOracle) (state : MachineState) : Outcome :=
  if !createFactsConsistent oracle.facts then fail .oracleMismatch state
  else
    let warmed := warm oracle.derivedCreateAddress state
    let chargedState := !oracle.facts.createLogicalExists
    let stateDebit : Debit := if chargedState then debitState schedule.createState warmed else .paid warmed
    match stateDebit with
    | .outOfGas failed => fail .outOfGas failed
    | .paid charged =>
      let preEnded := finishInstruction table charged
      let entry := enterFrame preEnded.gas.gasLeft preEnded.gas
      let nonceAndSnapshot := { preEnded with gas := entry.pausedParent, world := oracle.rollbackWorld }
      if oracle.facts.createCollision != .none then
        let gas := if chargedState then refillState schedule.createState entry.pausedParent else entry.pausedParent
        pushZeroAfterOwnedFinish table { nonceAndSnapshot with gas := gas, returnData := [] }
      else stageCreateChild kind operands oracle chargedState oracle.facts.createPhysicalExists entry nonceAndSnapshot

def runCreate (schedule : Schedule) (table : DispatchTable) (kind : CreateKind)
    (oracle : HandlerOracle) (entered : MachineState) : Outcome :=
  if entered.environment.isStatic then fail .staticViolation entered
  else
    let (operands?, tail) := popCreate kind entered.stack
    match operands? with
    | none => fail .stackUnderflow { entered with stack := tail }
    | some operands =>
      let popped := { entered with stack := tail }
      if operands.initLength.val > schedule.maxInitCodeSize then fail .outOfGas (exhaustExecution popped)
      else match createWords operands.initLength with
      | none => fail .outOfGas (exhaustExecution popped)
      | some words =>
        match debitExecution (createCost schedule kind words) popped with
        | .outOfGas failed => fail .outOfGas failed
        | .paid createCharged =>
          match chargeExpansion schedule operands.initOffset operands.initLength createCharged with
          | .outOfGas failed => fail .outOfGas failed
          | .paid expanded =>
            if expanded.environment.callDepth >= schedule.maxCallDepth then completeCreateWithoutChild table expanded
            else if !oracle.initCodeReadable then fail .outOfGas (exhaustExecution expanded)
            else if oracle.facts.callerBalance < operands.value.val || oracle.facts.creatorNonce >= 2 ^ 64 - 1 then
              completeCreateWithoutChild table expanded
            else afterCreateChecks schedule table kind operands oracle expanded

def selfDestructNeedsNewAccount (facts : WorldFacts) : Bool :=
  facts.callerBalance > 0 && facts.beneficiaryDead

def runSelfDestruct (schedule : Schedule) (_table : DispatchTable)
    (oracle : HandlerOracle) (entered : MachineState) : Outcome :=
  if entered.environment.isStatic then fail .staticViolation entered
  else match debitExecution schedule.selfDestructBase entered with
  | .outOfGas failed => fail .outOfGas failed
  | .paid baseCharged =>
    match Eip803x.Evm.MemoryStackControl.Stack.pop baseCharged.stack with
    | none => fail .stackUnderflow baseCharged
    | some (beneficiaryWord, tail) =>
      let beneficiary := addressOfWord beneficiaryWord
      let popped := { baseCharged with stack := tail }
      match chargeAccess schedule beneficiary popped with
      | .outOfGas failed => fail .outOfGas failed
      | .paid accessed =>
        let destroy := if oracle.facts.createdInTransaction then
          { accessed.journal with destroyList := accessed.journal.destroyList ++ [accessed.environment.executingAccount] }
          else accessed.journal
        let marked := { accessed with journal := destroy }
        let actionTraced := if marked.traceActions then
          appendTrace (.selfdestruct marked.environment.executingAccount beneficiary
            (Eip803x.Evm.Word.ofNat oracle.facts.callerBalance)) marked
          else marked
        let needsNew := selfDestructNeedsNewAccount oracle.facts
        let writeDebit : Debit := if needsNew then debitExecution schedule.accountWrite actionTraced else .paid actionTraced
        match writeDebit with
        | .outOfGas failed => fail .outOfGas failed
        | .paid writeCharged =>
          let stateDebit : Debit := if needsNew then debitState schedule.newAccountState writeCharged else .paid writeCharged
          match stateDebit with
          | .outOfGas failed => fail .outOfGas failed
          | .paid final => fail .stopped { final with world := oracle.nextWorld, journal := oracle.nextJournal }

def executeCore (schedule : Schedule) (table : DispatchTable) (opcode : Opcode)
    (oracle : HandlerOracle) (state : MachineState) : Outcome :=
  let entered := beginInstruction table opcode state
  match callKind opcode with
  | some kind => runCall schedule table kind oracle entered
  | none => match opcode with
    | .create => runCreate schedule table .create oracle entered
    | .create2 => runCreate schedule table .create2 oracle entered
    | .selfdestruct => runSelfDestruct schedule table oracle entered
    | _ => fail .extractionMismatch entered

def closeOuterFailure (table : DispatchTable) (result : Outcome) : Outcome :=
  if result.status == .continued || result.status == .suspended then result
  else
    let state := if result.status = .outOfGas then exhaustExecution result.state else result.state
    if tableTracing table then
      let finished := finishInstruction table state
      if result.status = .stopped then { result with state := finished }
      else { result with state := appendTrace (.instructionError result.status) finished }
    else { result with state := state }

def executeExtracted (schedule : Schedule) (table : DispatchTable) (opcode : Opcode)
    (oracle : HandlerOracle) (state : MachineState) : Outcome :=
  if profileAdmitted opcode table then closeOuterFailure table (executeCore schedule table opcode oracle state)
  else fail .extractionMismatch state

def executeAmsterdam (table : DispatchTable) (opcode : Opcode)
    (oracle : HandlerOracle) (state : MachineState) : Outcome :=
  executeExtracted amsterdamSchedule table opcode oracle state

def depositCost (schedule : Schedule) (output : List EvmByte) : Option (Nat × Nat) :=
  if output.length <= schedule.maxCodeSize then
    some (schedule.codeDepositExecutionPerWord * ((output.length + 31) / 32),
      schedule.codeDepositStatePerByte * output.length)
  else none

def restoreFailureWorld (frame : ChildFrame) (state : MachineState) : MachineState :=
  { state with world := frame.snapshot, journal := frame.journalSnapshot, returnData := [], stagedChild := none }

def mergeBeforeStateSpillRepayment (entry : FrameEntry) (child : FrameGasState) : GasState :=
  { child.gas with
    gasLeft := entry.pausedParent.gasLeft + child.gas.gasLeft
    stateReservoir := entry.pausedParent.stateReservoir + child.gas.stateReservoir
    stateFromGasLeft := entry.pausedParent.stateFromGasLeft + child.gas.stateFromGasLeft }

def extractedCreateDepositOrder : List CreateDepositPhase :=
  [.refundChild, .chargeParentExecution, .chargeParentState, .commitChild, .repayStateSpill]

def revertRefundedCreateToHalt (entry : FrameEntry) (child : FrameGasState) : GasState :=
  let restoredChild : FrameGasState :=
    { child with
      gas :=
        { child.gas with
          gasLeft := 0
          stateReservoir := child.stateGasBaseline
          stateFromGasLeft := 0
          stateUsed := child.stateUsedBaseline
          refundCounter := child.refundCounterBaseline } }
  repayStateFromGasLeft (mergeBeforeStateSpillRepayment entry restoredChild)

def debitFrameExecution (amount : Nat) (state : FrameGasState) : FrameDebit :=
  match chargeExecution amount state.gas with
  | .error _ => .outOfGas state
  | .ok gas => .paid { state with gas := gas }

def debitFrameState (amount : Nat) (state : FrameGasState) : FrameDebit :=
  match chargeState amount state.gas with
  | .error _ => .outOfGas state
  | .ok gas => .paid { state with gas := gas }

def reportPrecompileMemory (table : DispatchTable) (frame : ChildFrame)
    (output : List EvmByte) (state : MachineState) : MachineState :=
  if tableTracing table && frame.isPrecompile then
    appendTrace (.memoryWrite frame.outputOffset (output.take frame.outputLength)) state
  else state

def writeReturnedOutput (frame : ChildFrame) (output : List EvmByte) (result : Outcome) : Outcome :=
  if result.status = .continued && frame.outputLength != 0 then
    { result with state := { result.state with
        memory := result.state.memory.writeRange frame.outputOffset (output.take frame.outputLength) } }
  else result

def createDepositFailureGas (schedule : Schedule) (frame : ChildFrame)
    (childGas : FrameGasState) : GasState :=
  let merged := revertRefundedCreateToHalt frame.gasEntry childGas
  if frame.createStateCharged then refillState schedule.createState merged else merged

def completeCreateDepositFailure (schedule : Schedule) (table : DispatchTable)
    (frame : ChildFrame) (status : Status)
    (childGas : FrameGasState) (state : MachineState) : Outcome :=
  let restored := restoreFailureWorld frame { state with gas := createDepositFailureGas schedule frame childGas }
  let traced := if restored.traceActions then appendTrace (.actionError status) restored else restored
  pushZero table traced

def completeCreateSuccess (schedule : Schedule) (table : DispatchTable) (frame : ChildFrame)
    (child : ChildOutcome) (state : MachineState) : Outcome :=
  let kindConsistent := match child.runtimeCodeKind with
    | .empty => child.output.isEmpty
    | .valid | .invalid => !child.output.isEmpty
  if !kindConsistent then fail .oracleMismatch state
  else
    let refunded :=
      { state with
        gas := mergeBeforeStateSpillRepayment frame.gasEntry child.gas
        world := child.world
        returnData := []
        stagedChild := none }
    let cost := depositCost schedule child.output
    if child.runtimeCodeKind = .invalid then
      completeCreateDepositFailure schedule table frame .invalidCode child.gas refunded
    else match cost with
    | none => completeCreateDepositFailure schedule table frame .outOfGas child.gas refunded
    | some (executionCost, stateCost) =>
      match debitFrameExecution executionCost child.gas with
      | .outOfGas _ => completeCreateDepositFailure schedule table frame .outOfGas child.gas refunded
      | .paid executionPreview => match debitFrameState stateCost executionPreview with
        | .outOfGas _ => completeCreateDepositFailure schedule table frame .outOfGas child.gas refunded
        | .paid depositedPreview =>
          match debitExecution executionCost refunded with
          | .outOfGas _ => fail .oracleMismatch state
          | .paid executionCharged => match debitState stateCost executionCharged with
            | .outOfGas _ => fail .oracleMismatch state
            | .paid stateCharged =>
              let depositedOutcome := { child with gas := depositedPreview }
              let committed := { stateCharged with journal := child.journal }
              let repaid := { committed with gas := repayStateFromGasLeft committed.gas }
              pushAddress table frame.target (endChildAction depositedOutcome frame repaid)

def childGasConsistent (frame : ChildFrame) (child : ChildOutcome) : Bool :=
  decide (
    child.gas.stateGasBaseline = frame.gasEntry.child.stateGasBaseline /\
    child.gas.stateUsedBaseline = frame.gasEntry.child.stateUsedBaseline /\
    child.gas.refundCounterBaseline = frame.gasEntry.child.refundCounterBaseline /\
    child.gas.gas.gasLeft <= frame.gasEntry.child.gas.gasLeft /\
    child.gas.gas.stateFromGasLeft <= child.gas.gas.stateUsed /\
    child.gas.gas.stateUsed + child.gas.gas.stateReservoir =
      child.gas.stateUsedBaseline + child.gas.stateGasBaseline + child.gas.gas.stateFromGasLeft)

def resumeChild (schedule : Schedule) (table : DispatchTable) (child : ChildOutcome)
    (state : MachineState) : Outcome :=
  match state.stagedChild with
  | none => fail .oracleMismatch state
  | some frame =>
    if !childGasConsistent frame child then fail .oracleMismatch state
    else
      let isCreate := frame.opcode == .create || frame.opcode == .create2
      let refilled (gas : GasState) :=
        if frame.createStateCharged then refillState schedule.createState gas
        else if frame.newAccountCharged then refillState schedule.newAccountState gas
        else gas
      match child.exit with
      | .exceptional =>
        let gasMerged := { state with gas := refilled (mergeException frame.gasEntry child.gas) }
        let actionTraced := endChildAction child frame gasMerged
        pushZero table (restoreFailureWorld frame actionTraced)
      | .revert =>
        let restored := restoreFailureWorld frame { state with gas := refilled (mergeRevert frame.gasEntry child.gas) }
        let actionTraced := endChildAction child frame { restored with returnData := child.output }
        let pushed := pushZero table actionTraced
        if isCreate then pushed else writeReturnedOutput frame child.output pushed
      | .success =>
        if isCreate then completeCreateSuccess schedule table frame child state
        else
          let merged :=
            { state with
              gas := mergeSuccess frame.gasEntry child.gas
              world := child.world
              journal := child.journal
              returnData := child.output
              stagedChild := none }
          let memoryTraced := reportPrecompileMemory table frame child.output merged
          let pushed := pushOne table (endChildAction child frame memoryTraced)
          writeReturnedOutput frame child.output pushed
end Eip803x.Generated.CallCreateOpcodeKernel
