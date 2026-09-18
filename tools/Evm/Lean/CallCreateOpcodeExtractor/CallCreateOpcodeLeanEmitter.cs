// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.RegularExpressions;

namespace Nethermind.Evm.Lean.CallCreateOpcodeExtractor;

internal static class CallCreateOpcodeLeanEmitter
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] Emit(IrDocument document, string irSha256, string sourceSha256)
    {
        CallCreateOpcodeProfile.ValidateIr(document);
        return EmitFromSemanticIr(document, irSha256, sourceSha256);
    }

    internal static byte[] EmitFromSemanticIr(IrDocument document, string irSha256, string sourceSha256)
    {
        ValidateSemanticIr(document);
        ValidateSha256(irSha256, "Canonical IR");
        ValidateSha256(sourceSha256, "Source closure");

        StringBuilder text = new(60_000);
        text.AppendLine("-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited");
        text.AppendLine("-- SPDX-License-Identifier: LGPL-3.0-only");
        text.AppendLine();
        text.AppendLine("-- Generated from round-tripped, source-admitted semantic IR. Definitions only; do not edit.");
        text.AppendLine("-- The imported module supplies representation types, never the executable transition.");
        text.AppendLine($"-- Extractor version: {document.ExtractorVersion}");
        text.AppendLine($"-- Canonical IR SHA-256: {irSha256}");
        text.AppendLine($"-- Source closure SHA-256: {sourceSha256}");
        text.AppendLine();
        text.AppendLine("import CallCreateOpcodeExtractor.Specification.CallCreateExecution");
        text.AppendLine();
        text.AppendLine("namespace Eip803x.Generated.CallCreateOpcodeKernel");
        text.AppendLine();
        text.AppendLine("open Eip803x.GasMachine");
        text.AppendLine("open Eip803x.Evm.MemoryStackControl.Stack");
        text.AppendLine("open Eip803x.Evm.CallCreateExecution");
        text.AppendLine("abbrev EvmStack := Eip803x.Evm.MemoryStackControl.Stack");
        text.AppendLine("abbrev EvmMemory := Eip803x.Evm.MemoryStackControl.Memory");
        text.AppendLine("abbrev EvmByte := Eip803x.Evm.MemoryStackControl.Byte");
        text.AppendLine();
        EmitConfiguration(text, document);
        text.Append(IndependentBody);
        text.AppendLine();
        text.AppendLine("end Eip803x.Generated.CallCreateOpcodeKernel");

        string result = text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        if (result.Contains('\r') || ContainsProofDeclaration(result))
            throw new ExtractionException("Generated Lean must be LF-only and theorem-free.");
        if (result.Contains("Eip803x.Evm.CallCreateFrame", StringComparison.Ordinal) ||
            result.Contains("Eip803x.Evm.SelfDestruct", StringComparison.Ordinal) ||
            result.Contains("CallCreateOpcodeExtractor.Specification.CallCreateOperational", StringComparison.Ordinal) ||
            result.Contains("CallCreateExecution.Reference", StringComparison.Ordinal))
            throw new ExtractionException("Generated Lean must not import or call an independent refinement target.");
        if (result.Contains("DispatchTable.tracing", StringComparison.Ordinal) ||
            result.Contains("DispatchTable.cancelable", StringComparison.Ordinal) ||
            result.Contains("productionAddressOfWord", StringComparison.Ordinal) ||
            result.Contains("productionCreateDepositOrder", StringComparison.Ordinal))
            throw new ExtractionException("Generated Lean must independently own operational projections.");
        return StrictUtf8.GetBytes(result);
    }

    private static void EmitConfiguration(StringBuilder text, IrDocument document)
    {
        text.AppendLine($"def sourceRoot : String := \"{Escape(document.ClosedVm)}\"");
        text.AppendLine($"def sourceFork : String := \"{Escape(document.Fork)}\"");
        text.AppendLine($"def sourceGasPolicy : String := \"{Escape(document.GasPolicy)}\"");
        text.AppendLine($"def sourceBuild : String := \"{Escape(document.StandardBuildSelector)}\"");
        text.AppendLine();
        GasScheduleDescriptor schedule = document.Schedule;
        text.AppendLine("def amsterdamSchedule : Schedule :=");
        text.AppendLine("  {");
        text.AppendLine($"    callBase := {schedule.CallBase}, callValue := {schedule.CallValue}, callStipend := {schedule.CallStipend}");
        text.AppendLine($"    warmAccess := {schedule.WarmAccess}, coldAccess := {schedule.ColdAccess}");
        text.AppendLine($"    createAccess := {schedule.CreateAccess}, initCodeWord := {schedule.InitCodeWord}, create2HashWord := {schedule.Create2HashWord}");
        text.AppendLine($"    accountWrite := {schedule.AccountWrite}, selfDestructBase := {schedule.SelfDestructBase}");
        text.AppendLine($"    newAccountState := {schedule.NewAccountState}, createState := {schedule.CreateState}");
        text.AppendLine($"    codeDepositExecutionPerWord := {schedule.CodeDepositExecutionPerWord}, codeDepositStatePerByte := {schedule.CodeDepositStatePerByte}");
        text.AppendLine($"    memoryLinear := {schedule.MemoryLinear}, memoryQuadraticDivisor := {schedule.MemoryQuadraticDivisor}");
        text.AppendLine($"    maxMemorySize := {schedule.MaxMemorySize}, maxInitCodeSize := {schedule.MaxInitCodeSize}, maxCodeSize := {schedule.MaxCodeSize}");
        text.AppendLine($"    maxCallDepth := {schedule.MaxCallDepth}, stackLimit := {schedule.StackLimit}");
        text.AppendLine("  }");
        text.AppendLine();
        text.AppendLine("structure Descriptor where");
        text.AppendLine("  opcode : Opcode");
        text.AppendLine("  instruction : String");
        text.AppendLine("  opcodeByte : Nat");
        text.AppendLine("  family : String");
        text.AppendLine("  handlerBody : String");
        text.AppendLine("  handlerTarget : String");
        text.AppendLine("  operation : String");
        text.AppendLine("  valueRule : String");
        text.AppendLine("  targetRule : String");
        text.AppendLine("  resultRule : String");
        text.AppendLine("  stackInputs : Nat");
        text.AppendLine("  stackOutputs : Nat");
        text.AppendLine("  terminates : Bool");
        text.AppendLine("  ownsPreChildTraceEnd : Bool");
        text.AppendLine("  effectOrder : List String");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("def descriptors : List Descriptor :=");
        text.AppendLine("  [");
        for (int index = 0; index < document.Opcodes.Length; index++)
        {
            OpcodeDescriptor opcode = document.Opcodes[index];
            text.Append("    { opcode := .").Append(opcode.Name)
                .Append(", instruction := \"").Append(Escape(opcode.Instruction))
                .Append("\", opcodeByte := ").Append(opcode.OpcodeByte)
                .Append(", family := \"").Append(Escape(opcode.Family))
                .Append("\", handlerBody := \"").Append(Escape(opcode.HandlerBody))
                .Append("\", handlerTarget := \"").Append(Escape(opcode.HandlerTarget))
                .Append("\", operation := \"").Append(Escape(opcode.Semantics.Operation))
                .Append("\", valueRule := \"").Append(Escape(opcode.Semantics.ValueRule))
                .Append("\", targetRule := \"").Append(Escape(opcode.Semantics.TargetRule))
                .Append("\", resultRule := \"").Append(Escape(opcode.Semantics.ResultRule))
                .Append("\", stackInputs := ").Append(opcode.StackInputs)
                .Append(", stackOutputs := ").Append(opcode.StackOutputs)
                .Append(", terminates := ").Append(LeanBool(opcode.Terminates))
                .Append(", ownsPreChildTraceEnd := ").Append(LeanBool(opcode.OwnsPreChildTraceEnd))
                .Append(", effectOrder := ");
            EmitStringListInline(text, opcode.EffectOrder);
            text.Append(" }").AppendLine(index + 1 == document.Opcodes.Length ? string.Empty : ",");
        }
        text.AppendLine("  ]");
        text.AppendLine();
        text.AppendLine("def descriptor (opcode : Opcode) : Option Descriptor :=");
        text.AppendLine("  descriptors.find? fun item => item.opcode == opcode");
        text.AppendLine();
        text.AppendLine("def semanticOpcode : String -> Option Opcode");
        foreach (OpcodeDescriptor opcode in document.Opcodes)
            text.AppendLine($"  | \"{Escape(opcode.Semantics.Operation)}\" => some .{opcode.Name}");
        text.AppendLine("  | _ => none");
        text.AppendLine();
        text.AppendLine("structure Specialization where");
        text.AppendLine("  opcode : Opcode");
        text.AppendLine("  table : DispatchTable");
        text.AppendLine("  tracingFlag : String");
        text.AppendLine("  cancelableFlag : String");
        text.AppendLine("  closedRoot : String");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("def specializations : List Specialization :=");
        text.AppendLine("  [");
        for (int index = 0; index < document.Specializations.Length; index++)
        {
            OpcodeSpecialization root = document.Specializations[index];
            text.Append("    { opcode := .").Append(root.Opcode)
                .Append(", table := .").Append(LeanTable(root.DispatchTable))
                .Append(", tracingFlag := \"").Append(Escape(root.TracingFlag))
                .Append("\", cancelableFlag := \"").Append(Escape(root.CancelableFlag))
                .Append("\", closedRoot := \"").Append(Escape(root.ClosedRoot)).Append("\" }")
                .AppendLine(index + 1 == document.Specializations.Length ? string.Empty : ",");
        }
        text.AppendLine("  ]");
        text.AppendLine();
        text.AppendLine("def profileAdmitted (opcode : Opcode) (table : DispatchTable) : Bool :=");
        text.AppendLine("  descriptors.length = 7 && specializations.length = 28 &&");
        text.AppendLine("    (specializations.any fun item => item.opcode == opcode && item.table == table) &&");
        text.AppendLine("    match descriptor opcode with");
        text.AppendLine("    | none => false");
        text.AppendLine("    | some item => semanticOpcode item.operation == some opcode && !item.effectOrder.isEmpty");
        text.AppendLine();
        text.AppendLine("def forkLineage : List String :=");
        EmitList(text, document.ForkLineage);
        text.AppendLine();
        text.AppendLine("def openExtractionObligations : List String :=");
        EmitList(text, document.OpenExtractionObligations);
        text.AppendLine();
    }

    private const string IndependentBody = """
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
""";

    private static void ValidateSemanticIr(IrDocument document)
    {
        if (document.Opcodes is null || document.Opcodes.Length != 7 ||
            document.Opcodes.Any(static opcode => opcode is null || opcode.Semantics is null || opcode.EffectOrder is null))
            throw new ExtractionException("Semantic IR must contain seven complete opcode descriptors.");
        foreach (OpcodeDescriptor opcode in document.Opcodes)
        {
            ValidateToken(opcode.Name, ["call", "callcode", "delegatecall", "staticcall", "create", "create2", "selfdestruct"], opcode.Instruction + " name");
            ValidateToken(opcode.Semantics.Operation, ["call", "callcode", "delegatecall", "staticcall", "create", "create2", "selfdestruct"], opcode.Instruction + " operation");
            ValidateToken(opcode.Family, ["call", "create", "selfdestruct"], opcode.Instruction + " family");
            ValidateToken(opcode.Semantics.ValueRule, ["explicit", "environment", "zero", "none"], opcode.Instruction + " value rule");
            ValidateToken(opcode.Semantics.TargetRule, ["codeSource", "executingAccount", "derivedCreate", "derivedCreate2", "beneficiary"], opcode.Instruction + " target rule");
            ValidateToken(opcode.Semantics.ResultRule, ["successBool", "createdAddress", "stop"], opcode.Instruction + " result rule");
        }
        if (document.Specializations is null || document.Specializations.Length != 28)
            throw new ExtractionException("Semantic IR must contain exactly 28 opcode/table specializations.");
    }

    private static void ValidateToken(string value, string[] admitted, string description)
    {
        if (!admitted.Contains(value, StringComparer.Ordinal))
            throw new ExtractionException($"Unknown {description} '{value}'.");
    }

    internal static void ValidateSha256(string? value, string description)
    {
        if (value is not { Length: 64 } || value.Any(static character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ExtractionException($"{description} SHA-256 must be a 64-character lowercase hexadecimal digest.");
    }

    private static bool ContainsProofDeclaration(string source) =>
        Regex.IsMatch(source, @"(?m)^\s*(theorem|axiom|example|admit|sorry)\b", RegexOptions.CultureInvariant);

    private static string LeanBool(bool value) => value ? "true" : "false";

    private static string LeanTable(string table) => table switch
    {
        "NoTrace" => "noTrace",
        "NoTraceCancelable" => "noTraceCancelable",
        "Traced" => "traced",
        "TracedCancelable" => "tracedCancelable",
        _ => throw new ExtractionException($"Unknown dispatch table '{table}'."),
    };

    private static void EmitStringListInline(StringBuilder text, IEnumerable<string> values)
    {
        text.Append('[');
        bool first = true;
        foreach (string value in values)
        {
            if (!first) text.Append(", ");
            text.Append('"').Append(Escape(value)).Append('"');
            first = false;
        }
        text.Append(']');
    }

    private static void EmitList(StringBuilder text, IEnumerable<string> values)
    {
        string[] items = values.ToArray();
        text.AppendLine("  [");
        for (int index = 0; index < items.Length; index++)
            text.Append("    \"").Append(Escape(items[index])).Append('"')
                .AppendLine(index + 1 == items.Length ? string.Empty : ",");
        text.AppendLine("  ]");
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);
}
