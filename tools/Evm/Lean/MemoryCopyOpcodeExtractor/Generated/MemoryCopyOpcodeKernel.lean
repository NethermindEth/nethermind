-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- Generated from round-tripped, source-admitted semantic IR. Definitions only; do not edit.
-- The imported operational module supplies representation types, not this executable body.
-- Extractor version: 1.0.0
-- Canonical IR SHA-256: 000230520fef8cff7975e07a6b87ab2f8a6286d16902a2662ffca828dfc2780f
-- Source closure SHA-256: 00c04ea98800c9f23885c9ec8ab74640d71e3831c5777f3704526408b94bddf0

import MemoryCopyOpcodeExtractor.Specification.MemoryCopyExecution

namespace Eip803x.Generated.MemoryCopyOpcodeKernel

open GasMachine
open Eip803x.Evm.MemoryStackControl
open Eip803x.Evm.MemoryStackControl.Stack

abbrev Opcode := Eip803x.Evm.MemoryCopyExecution.Opcode
abbrev DispatchTable := Eip803x.Evm.MemoryCopyExecution.DispatchTable
abbrev Activation := Eip803x.Evm.MemoryCopyExecution.Activation
abbrev Schedule := Eip803x.Evm.MemoryCopyExecution.Schedule
abbrev Status := Eip803x.Evm.MemoryCopyExecution.Status
abbrev TraceEvent := Eip803x.Evm.MemoryCopyExecution.TraceEvent
abbrev MachineState := Eip803x.Evm.MemoryCopyExecution.MachineState
abbrev Outcome := Eip803x.Evm.MemoryCopyExecution.Outcome

def sourceRoot : String := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>"
def sourceFork : String := "Nethermind.Specs.Forks.Amsterdam"
def sourceGasPolicy : String := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
def sourceBuild : String := "Directory.Build.targets: EnableZkEvm != true selects *.std.cs and excludes *.zkevm.cs"

def amsterdamSchedule : Schedule :=
  {
    veryLow := 3
    base := 2
    copyWord := 3
    memoryLinear := 3
    memoryQuadraticDivisor := 512
    memoryQuadraticDivisorPositive := by decide
    maxMemorySize := 2147483616
    maxUInt64 := 18446744073709551615
    maxUInt32 := 4294967295
    uint256Modulus := 115792089237316195423570985008687907853269984665640564039457584007913129639936
  }

def amsterdamActivation : Activation :=
  { eip211 := true, eip5656 := true }

inductive Operation where
  | loadWord | storeWord | storeByte | pushMemorySize | copyZeroExtended
  | copyReturnData | copyMemory | pushRemainingGas
  deriving DecidableEq, Repr

inductive GasClass where
  | veryLow | base | copy
  deriving DecidableEq, Repr

inductive ActivationRule where
  | always | eip211 | eip5656
  deriving DecidableEq, Repr

inductive CopySource where
  | none | calldata | code | returnData | memory
  deriving DecidableEq, Repr

inductive TraceRule where
  | none | parityLoad | destination | push | sourceThenDestination
  deriving DecidableEq, Repr

structure SemanticDescriptor where
  operation : Operation
  gasClass : GasClass
  accessWidth : Nat
  copySource : CopySource
  traceRule : TraceRule
  activation : ActivationRule
  deriving DecidableEq, Repr

structure Descriptor where
  opcode : Opcode
  instruction : String
  opcodeByte : Nat
  handlerBody : String
  handlerTarget : String
  stackInputs : Nat
  stackOutputs : Nat
  hasCheckedBody : String
  semantic : SemanticDescriptor
  effectOrder : List String
  deriving DecidableEq, Repr

def descriptors : List Descriptor :=
  [
    { opcode := .mload, instruction := "MLOAD", opcodeByte := 81, handlerBody := "MLoadOpcode<TTracingInst>", handlerTarget := "EvmInstructions.InstructionMLoad<TGasPolicy,TTracingInst>", stackInputs := 1, stackOutputs := 1, hasCheckedBody := "false", semantic := { operation := .loadWord, gasClass := .veryLow, accessWidth := 32, copySource := .none, traceRule := .parityLoad, activation := .always }, effectOrder := ["chargeVeryLow", "checkDepth1", "peekOffset", "installExpansion", "chargeExpansion", "load32", "traceParityMemory", "replaceTop", "tracePush"] },
    { opcode := .mstore, instruction := "MSTORE", opcodeByte := 82, handlerBody := "MStoreOpcode<TTracingInst>", handlerTarget := "EvmInstructions.InstructionMStore<TGasPolicy,TTracingInst>", stackInputs := 2, stackOutputs := 0, hasCheckedBody := "false", semantic := { operation := .storeWord, gasClass := .veryLow, accessWidth := 32, copySource := .none, traceRule := .destination, activation := .always }, effectOrder := ["chargeVeryLow", "popOffsetWordAtomically", "installExpansion", "chargeExpansion", "storeWord", "traceMemory"] },
    { opcode := .mstore8, instruction := "MSTORE8", opcodeByte := 83, handlerBody := "MStore8Opcode<TTracingInst>", handlerTarget := "EvmInstructions.InstructionMStore8<TGasPolicy,TTracingInst>", stackInputs := 2, stackOutputs := 0, hasCheckedBody := "false", semantic := { operation := .storeByte, gasClass := .veryLow, accessWidth := 1, copySource := .none, traceRule := .destination, activation := .always }, effectOrder := ["chargeVeryLow", "checkDepth2", "popTwoUnchecked", "readOffsetAndLowByte", "installExpansion", "chargeExpansion", "storeByte", "traceMemory"] },
    { opcode := .msize, instruction := "MSIZE", opcodeByte := 89, handlerBody := "EnvUInt64Opcode<EvmInstructions.OpMSize<TGasPolicy>,TTracingInst>", handlerTarget := "EvmInstructions.OpMSize<TGasPolicy>.Operation", stackInputs := 0, stackOutputs := 1, hasCheckedBody := "!TTracingInst.IsActive", semantic := { operation := .pushMemorySize, gasClass := .base, accessWidth := 0, copySource := .none, traceRule := .push, activation := .always }, effectOrder := ["chargeBase", "checkPushDepth", "readLogicalMemorySize", "push", "tracePush"] },
    { opcode := .calldatacopy, instruction := "CALLDATACOPY", opcodeByte := 55, handlerBody := "CallDataCopyOpcode<TTracingInst>", handlerTarget := "EvmInstructions.InstructionCallDataCopy<TGasPolicy,TTracingInst>", stackInputs := 3, stackOutputs := 0, hasCheckedBody := "false", semantic := { operation := .copyZeroExtended, gasClass := .copy, accessWidth := 0, copySource := .calldata, traceRule := .destination, activation := .always }, effectOrder := ["popThreeAtomically", "checkedWords", "chargeVeryLowAndWords", "rejectWordOverflowAfterCharge", "zeroLengthReturn", "installExpansion", "chargeExpansion", "copyZeroExtended", "traceMemory"] },
    { opcode := .codecopy, instruction := "CODECOPY", opcodeByte := 57, handlerBody := "CodeCopyOpcode<TTracingInst>", handlerTarget := "EvmInstructions.InstructionCodeCopy<TGasPolicy,TTracingInst>", stackInputs := 3, stackOutputs := 0, hasCheckedBody := "false", semantic := { operation := .copyZeroExtended, gasClass := .copy, accessWidth := 0, copySource := .code, traceRule := .destination, activation := .always }, effectOrder := ["popThreeAtomically", "checkedWords", "chargeVeryLowAndWords", "rejectWordOverflowAfterCharge", "zeroLengthReturn", "installExpansion", "chargeExpansion", "copyZeroExtended", "traceMemory"] },
    { opcode := .returndatacopy, instruction := "RETURNDATACOPY", opcodeByte := 62, handlerBody := "ReturnDataCopyOpcode<TTracingInst>", handlerTarget := "EvmInstructions.InstructionReturnDataCopy<TGasPolicy,TTracingInst>", stackInputs := 3, stackOutputs := 0, hasCheckedBody := "false", semantic := { operation := .copyReturnData, gasClass := .copy, accessWidth := 0, copySource := .returnData, traceRule := .destination, activation := .eip211 }, effectOrder := ["popThreeAtomically", "checkedWords", "chargeVeryLowAndWords", "rejectWordOverflowAfterCharge", "checkedSourceEnd", "rejectAccessBeforeExpansion", "zeroLengthReturn", "installExpansion", "chargeExpansion", "copyExact", "traceMemory"] },
    { opcode := .mcopy, instruction := "MCOPY", opcodeByte := 94, handlerBody := "MCopyOpcode<TTracingInst>", handlerTarget := "EvmInstructions.InstructionMCopy<TGasPolicy,TTracingInst>", stackInputs := 3, stackOutputs := 0, hasCheckedBody := "false", semantic := { operation := .copyMemory, gasClass := .copy, accessWidth := 0, copySource := .memory, traceRule := .sourceThenDestination, activation := .eip5656 }, effectOrder := ["popThreeAtomically", "checkedWords", "chargeVeryLowAndWords", "rejectWordOverflowAfterCharge", "zeroLengthReturn", "installMaxSourceDestinationExpansion", "chargeExpansion", "traceSource", "memmove", "traceDestination"] },
    { opcode := .gas, instruction := "GAS", opcodeByte := 90, handlerBody := "GasOpcode<TTracingInst>", handlerTarget := "EvmInstructions.InstructionGas<TGasPolicy,TTracingInst>", stackInputs := 0, stackOutputs := 1, hasCheckedBody := "!TTracingInst.IsActive", semantic := { operation := .pushRemainingGas, gasClass := .base, accessWidth := 0, copySource := .none, traceRule := .push, activation := .always }, effectOrder := ["chargeBase", "checkPushDepth", "readPostChargeExecutionGas", "push", "tracePush"] }
  ]

def invalidSemantic : SemanticDescriptor :=
  { operation := .loadWord, gasClass := .veryLow, accessWidth := 0,
    copySource := .none, traceRule := .none, activation := .always }

def descriptor (opcode : Opcode) : Descriptor :=
  match descriptors.find? fun item => item.opcode = opcode with
  | some item => item
  | none =>
    { opcode := opcode, instruction := "", opcodeByte := 0, handlerBody := "", handlerTarget := "",
      stackInputs := 0, stackOutputs := 0, hasCheckedBody := "",
      semantic := invalidSemantic, effectOrder := [] }

def descriptorAdmitted : Bool :=
  descriptors.length = 9 && descriptors.all fun item =>
    item.instruction = item.opcode.instruction && item.opcodeByte = item.opcode.byte

def fixedCost (schedule : Schedule) : GasClass -> Nat
  | .veryLow => schedule.veryLow
  | .base => schedule.base
  | .copy => schedule.veryLow

def dispatchEnter (table : DispatchTable) (opcode : Opcode)
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

def reportMemory (rule : TraceRule) (table : DispatchTable) (offset : Nat)
    (bytes : List Byte) (state : MachineState) : MachineState :=
  let enabled := rule = .parityLoad || rule = .destination || rule = .sourceThenDestination
  if table.tracing && enabled then
    { state with trace := state.trace ++ [.memoryChange offset bytes] }
  else state

def reportPush (rule : TraceRule) (table : DispatchTable) (value : UInt256)
    (state : MachineState) : MachineState :=
  let payload := match rule with
    | .parityLoad => wordToBytes value
    | .push => (wordToBytes value).drop 24
    | _ => []
  let enabled := rule = .parityLoad || rule = .push
  if table.tracing && enabled then
    { state with trace := state.trace ++ [.stackPush payload] }
  else state

def dispatchFinish (table : DispatchTable) (state : MachineState) : MachineState :=
  if table.tracing then { state with trace := state.trace ++ [.finish state.gas.gasLeft] } else state

def exhaust (state : MachineState) : MachineState :=
  { state with gas := { state.gas with gasLeft := 0 } }

def outcome (status : Status) (state : MachineState) : Outcome := ⟨status, state⟩

abbrev DebitResult := Eip803x.Evm.MemoryCopyExecution.Debit

def debitExecution (amount : Nat) (state : MachineState) : DebitResult :=
  match chargeExecution amount state.gas with
  | .error _ => .outOfGas (exhaust state)
  | .ok gas => .paid { state with gas := gas }

def memoryWords (memory : Memory) : Nat := memory.bytes.length / 32

def memoryCost (schedule : Schedule) (words : Nat) : Nat :=
  words * schedule.memoryLinear + words * words / schedule.memoryQuadraticDivisor

def rangeAllowed (schedule : Schedule) (offset length : UInt256) : Bool :=
  length.val = 0 ||
    (length.val <= schedule.maxMemorySize && offset.val <= schedule.maxMemorySize - length.val)

abbrev ExpansionPlan := Eip803x.Evm.MemoryCopyExecution.MemoryPreparation

def prepareExpansion (schedule : Schedule) (memory : Memory) (offset length : UInt256) :
    ExpansionPlan :=
  if length.val = 0 then .prepared 0 memory
  else if rangeAllowed schedule offset length then
    let expanded := memory.expand offset.val length.val
    .prepared (memoryCost schedule (memoryWords expanded) - memoryCost schedule (memoryWords memory)) expanded
  else .invalid

def expandAndCharge (prepared : ExpansionPlan) (state : MachineState) :
    Outcome ⊕ MachineState :=
  match prepared with
  | .invalid => .inl (outcome .outOfGas state)
  | .prepared cost memory =>
    let installed := { state with memory := memory }
    match debitExecution cost installed with
    | .outOfGas exhausted => .inl (outcome .outOfGas exhausted)
    | .paid paid => .inr paid

def checkedWords (schedule : Schedule) (length : UInt256) : Nat × Bool :=
  if length.val > schedule.maxUInt64 then (0, true)
  else
    let words := (length.val + 31) / 32
    if words > schedule.maxUInt32 then (0, true) else (words, false)

def copyCharge (schedule : Schedule) (semantic : SemanticDescriptor) (words : Nat) : Nat :=
  fixedCost schedule semantic.gasClass + schedule.copyWord * words

def replaceTop (stack : Stack) (value : UInt256) : Stack :=
  match stack.words with
  | [] => stack
  | _ :: tail => Stack.fromWords (value :: tail)

def runLoad (schedule : Schedule) (table : DispatchTable) (semantic : SemanticDescriptor)
    (entered : MachineState) : Outcome :=
  match debitExecution (fixedCost schedule semantic.gasClass) entered with
  | .outOfGas exhausted => outcome .outOfGas exhausted
  | .paid charged =>
    match charged.stack.words with
    | [] => outcome .stackUnderflow charged
    | offset :: _ =>
      match expandAndCharge
          (prepareExpansion schedule charged.memory offset (Eip803x.Evm.Word.ofNat semantic.accessWidth)) charged with
      | .inl failed => failed
      | .inr expanded =>
        let (loadedMemory, bytes) := charged.memory.readRange offset.val semantic.accessWidth
        let value := bytesToWord bytes
        let loaded := { expanded with memory := loadedMemory, stack := replaceTop expanded.stack value }
        let reportedMemory := reportMemory semantic.traceRule table offset.val bytes loaded
        outcome .ok (dispatchFinish table (reportPush semantic.traceRule table value reportedMemory))

def runStoreWord (schedule : Schedule) (table : DispatchTable) (semantic : SemanticDescriptor)
    (entered : MachineState) : Outcome :=
  match debitExecution (fixedCost schedule semantic.gasClass) entered with
  | .outOfGas exhausted => outcome .outOfGas exhausted
  | .paid charged =>
    match charged.stack.popTwo with
    | none => outcome .stackUnderflow charged
    | some (offset, value, tail) =>
      let popped := { charged with stack := tail }
      match expandAndCharge
          (prepareExpansion schedule popped.memory offset (Eip803x.Evm.Word.ofNat semantic.accessWidth)) popped with
      | .inl failed => failed
      | .inr expanded =>
        let bytes := wordToBytes value
        let stored := { expanded with memory := popped.memory.writeRange offset.val bytes }
        outcome .ok (dispatchFinish table (reportMemory semantic.traceRule table offset.val bytes stored))

def runStoreByte (schedule : Schedule) (table : DispatchTable) (semantic : SemanticDescriptor)
    (entered : MachineState) : Outcome :=
  match debitExecution (fixedCost schedule semantic.gasClass) entered with
  | .outOfGas exhausted => outcome .outOfGas exhausted
  | .paid charged =>
    match charged.stack.popTwo with
    | none => outcome .stackUnderflow charged
    | some (offset, value, tail) =>
      let popped := { charged with stack := tail }
      match expandAndCharge
          (prepareExpansion schedule popped.memory offset (Eip803x.Evm.Word.ofNat semantic.accessWidth)) popped with
      | .inl failed => failed
      | .inr expanded =>
        let bytes := [Eip803x.Evm.MemoryStackControl.byte value.val]
        let stored := { expanded with memory := popped.memory.writeRange offset.val bytes }
        outcome .ok (dispatchFinish table (reportMemory semantic.traceRule table offset.val bytes stored))

def runPush (schedule : Schedule) (table : DispatchTable) (semantic : SemanticDescriptor)
    (value : MachineState -> UInt256) (entered : MachineState) : Outcome :=
  match debitExecution (fixedCost schedule semantic.gasClass) entered with
  | .outOfGas exhausted => outcome .outOfGas exhausted
  | .paid charged =>
    let result := value charged
    match charged.stack.push result with
    | none => outcome .stackOverflow charged
    | some stack =>
      let pushed := reportPush semantic.traceRule table result { charged with stack := stack }
      outcome .ok (dispatchFinish table pushed)

def sourceBytes (source : CopySource) (state : MachineState) : List Byte :=
  match source with
  | .calldata => state.calldata
  | .code => state.code
  | .returnData => state.returnData
  | .memory => state.memory.bytes
  | .none => []

def runZeroExtendedCopy (schedule : Schedule) (table : DispatchTable)
    (semantic : SemanticDescriptor) (entered : MachineState) : Outcome :=
  match entered.stack.popThree with
  | none => outcome .stackUnderflow entered
  | some (destination, source, length, tail) =>
    let popped := { entered with stack := tail }
    let (words, invalidWords) := checkedWords schedule length
    match debitExecution (copyCharge schedule semantic words) popped with
    | .outOfGas exhausted => outcome .outOfGas exhausted
    | .paid charged =>
      if invalidWords then outcome .outOfGas charged
      else if length.val = 0 then outcome .ok (dispatchFinish table charged)
      else
        match expandAndCharge (prepareExpansion schedule charged.memory destination length) charged with
        | .inl failed => failed
        | .inr expanded =>
          let bytes := readRange (sourceBytes semantic.copySource expanded) source.val length.val
          let copied := { expanded with memory := charged.memory.writeRange destination.val bytes }
          outcome .ok (dispatchFinish table
            (reportMemory semantic.traceRule table destination.val bytes copied))

def returnRangeAllowed (schedule : Schedule) (returnData : List Byte)
    (source length : UInt256) : Bool :=
  source.val + length.val < schedule.uint256Modulus &&
    source.val + length.val <= returnData.length

def runReturnDataCopy (schedule : Schedule) (table : DispatchTable)
    (semantic : SemanticDescriptor) (entered : MachineState) : Outcome :=
  match entered.stack.popThree with
  | none => outcome .stackUnderflow entered
  | some (destination, source, length, tail) =>
    let popped := { entered with stack := tail }
    let (words, invalidWords) := checkedWords schedule length
    match debitExecution (copyCharge schedule semantic words) popped with
    | .outOfGas exhausted => outcome .outOfGas exhausted
    | .paid charged =>
      if invalidWords then outcome .outOfGas charged
      else if !returnRangeAllowed schedule (sourceBytes semantic.copySource charged) source length then
        outcome .accessViolation charged
      else if length.val = 0 then outcome .ok (dispatchFinish table charged)
      else
        match expandAndCharge (prepareExpansion schedule charged.memory destination length) charged with
        | .inl failed => failed
        | .inr expanded =>
          let bytes := readRange (sourceBytes semantic.copySource expanded) source.val length.val
          let copied := { expanded with memory := charged.memory.writeRange destination.val bytes }
          outcome .ok (dispatchFinish table
            (reportMemory semantic.traceRule table destination.val bytes copied))

def runMemoryCopy (schedule : Schedule) (table : DispatchTable)
    (semantic : SemanticDescriptor) (entered : MachineState) : Outcome :=
  match entered.stack.popThree with
  | none => outcome .stackUnderflow entered
  | some (destination, source, length, tail) =>
    let popped := { entered with stack := tail }
    let (words, invalidWords) := checkedWords schedule length
    match debitExecution (copyCharge schedule semantic words) popped with
    | .outOfGas exhausted => outcome .outOfGas exhausted
    | .paid charged =>
      if invalidWords then outcome .outOfGas charged
      else if length.val = 0 then outcome .ok (dispatchFinish table charged)
      else
        let greatest := if destination.val < source.val then source else destination
        match expandAndCharge (prepareExpansion schedule charged.memory greatest length) charged with
        | .inl failed => failed
        | .inr expanded =>
          let before := readRange expanded.memory.bytes source.val length.val
          let copied := { expanded with memory := charged.memory.mcopy destination.val source.val length.val }
          let reportedSource := reportMemory semantic.traceRule table source.val before copied
          let after := readRange copied.memory.bytes destination.val length.val
          let reportedDestination := reportMemory semantic.traceRule table destination.val after reportedSource
          outcome .ok (dispatchFinish table reportedDestination)

def operationActive (activation : Activation) (rule : ActivationRule) : Bool :=
  match rule with
  | .always => true
  | .eip211 => activation.eip211
  | .eip5656 => activation.eip5656

def executeSemantic (schedule : Schedule) (table : DispatchTable) (semantic : SemanticDescriptor)
    (entered : MachineState) : Outcome :=
  match semantic.operation with
  | .loadWord => runLoad schedule table semantic entered
  | .storeWord => runStoreWord schedule table semantic entered
  | .storeByte => runStoreByte schedule table semantic entered
  | .pushMemorySize => runPush schedule table semantic
      (fun state => Eip803x.Evm.Word.ofNat state.memory.bytes.length) entered
  | .copyZeroExtended => runZeroExtendedCopy schedule table semantic entered
  | .copyReturnData => runReturnDataCopy schedule table semantic entered
  | .copyMemory => runMemoryCopy schedule table semantic entered
  | .pushRemainingGas => runPush schedule table semantic
      (fun state => Eip803x.Evm.Word.ofNat state.gas.gasLeft) entered

def executeCore (schedule : Schedule) (activation : Activation) (table : DispatchTable)
    (opcode : Opcode) (state : MachineState) : Outcome :=
  let entered := dispatchEnter table opcode state
  let semantic := (descriptor opcode).semantic
  if operationActive activation semantic.activation then executeSemantic schedule table semantic entered
  else outcome .inactive entered

def closeFailureTrace (table : DispatchTable) (result : Outcome) : Outcome :=
  if result.status = .ok || result.status = .extractionMismatch then result
  else
    let state := if result.status = .outOfGas then exhaust result.state else result.state
    if table.tracing then
      { result with state := { state with trace := state.trace ++ [.finish state.gas.gasLeft, .error result.status] } }
    else { result with state := state }

def faultPc (result : Outcome) : Nat :=
  if result.status = .ok then result.state.pc else result.state.pc - 1

structure Specialization where
  opcode : Opcode
  table : DispatchTable
  tracingFlag : String
  cancelableFlag : String
  closedRoot : String
  deriving DecidableEq, Repr

def specializations : List Specialization :=
  [
    { opcode := .mload, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<MLoadOpcode<OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .mload, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<MLoadOpcode<OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .mload, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<MLoadOpcode<OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .mload, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<MLoadOpcode<OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .mstore, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<MStoreOpcode<OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .mstore, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<MStoreOpcode<OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .mstore, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<MStoreOpcode<OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .mstore, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<MStoreOpcode<OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .mstore8, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<MStore8Opcode<OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .mstore8, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<MStore8Opcode<OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .mstore8, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<MStore8Opcode<OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .mstore8, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<MStore8Opcode<OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .msize, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<EnvUInt64Opcode<EvmInstructions.OpMSize<EthereumGasPolicy>,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .msize, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<EnvUInt64Opcode<EvmInstructions.OpMSize<EthereumGasPolicy>,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .msize, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<EnvUInt64Opcode<EvmInstructions.OpMSize<EthereumGasPolicy>,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .msize, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<EnvUInt64Opcode<EvmInstructions.OpMSize<EthereumGasPolicy>,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .calldatacopy, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CallDataCopyOpcode<OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .calldatacopy, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CallDataCopyOpcode<OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .calldatacopy, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CallDataCopyOpcode<OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .calldatacopy, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CallDataCopyOpcode<OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .codecopy, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CodeCopyOpcode<OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .codecopy, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CodeCopyOpcode<OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .codecopy, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CodeCopyOpcode<OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .codecopy, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<CodeCopyOpcode<OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .returndatacopy, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<ReturnDataCopyOpcode<OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .returndatacopy, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<ReturnDataCopyOpcode<OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .returndatacopy, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<ReturnDataCopyOpcode<OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .returndatacopy, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<ReturnDataCopyOpcode<OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .mcopy, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<MCopyOpcode<OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .mcopy, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<MCopyOpcode<OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .mcopy, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<MCopyOpcode<OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .mcopy, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<MCopyOpcode<OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .gas, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<GasOpcode<OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .gas, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<GasOpcode<OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .gas, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<GasOpcode<OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .gas, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<GasOpcode<OnFlag>,OnFlag,OnFlag,OnFlag>" }
  ]

def specializationAdmitted (opcode : Opcode) (table : DispatchTable) : Bool :=
  specializations.any fun item => item.opcode = opcode && item.table = table

def executeExtracted (schedule : Schedule) (activation : Activation) (table : DispatchTable)
    (opcode : Opcode) (state : MachineState) : Outcome :=
  if descriptorAdmitted && specializationAdmitted opcode table then
    executeCore schedule activation table opcode state
  else outcome .extractionMismatch (dispatchEnter table opcode state)

def executeAmsterdam (table : DispatchTable) (opcode : Opcode) (state : MachineState) : Outcome :=
  executeExtracted amsterdamSchedule amsterdamActivation table opcode state

def executeAmsterdamClosed (table : DispatchTable) (opcode : Opcode) (state : MachineState) : Outcome :=
  closeFailureTrace table (executeAmsterdam table opcode state)

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
    "Roslyn exact-syntax admission does not prove C# compilation, CLR/JIT/AOT execution, function-pointer tail calls, or hardware behavior.",
    "The model assumes admitted UInt256 and unsafe EvmStack slots represent the shared Lean UInt256 and top-first Stack without corruption.",
    "The model assumes EvmPooledMemory allocation, pooling, zero-initialization, Span.CopyTo memmove behavior, and adapter aliasing satisfy the admitted byte semantics.",
    "Tracing callbacks are assumed total and non-throwing; cancellation polling, tracer implementation internals, and deliberate Parity MLOAD memory reporting are not bugs at this boundary.",
    "The admitted outer closure covers OOG execution-gas clearing, tracer finish/error order, and fault-PC normalization; rollback, transaction and block processing, persistence, and state-gas settlement remain outside this slice."
  ]

end Eip803x.Generated.MemoryCopyOpcodeKernel
