// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.Evm.Lean.MemoryCopyOpcodeExtractor;

internal static class MemoryCopyOpcodeLeanEmitter
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    internal static byte[] Emit(IrDocument document, string irSha256, string sourceSha256)
    {
        MemoryCopyOpcodeProfile.ValidateIr(document);
        ValidateSha256(irSha256, "Canonical IR");
        ValidateSha256(sourceSha256, "Source closure");
        return EmitFromSemanticIr(document, irSha256, sourceSha256);
    }

    internal static byte[] EmitFromSemanticIr(IrDocument document, string irSha256, string sourceSha256)
    {
        ValidateSemanticIr(document);
        ValidateSha256(irSha256, "Canonical IR");
        ValidateSha256(sourceSha256, "Source closure");

        StringBuilder text = new();
        text.AppendLine("-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited");
        text.AppendLine("-- SPDX-License-Identifier: LGPL-3.0-only");
        text.AppendLine();
        text.AppendLine("-- Generated from round-tripped, source-admitted semantic IR. Definitions only; do not edit.");
        text.AppendLine("-- The imported operational module supplies representation types, not this executable body.");
        text.AppendLine($"-- Extractor version: {document.ExtractorVersion}");
        text.AppendLine($"-- Canonical IR SHA-256: {irSha256}");
        text.AppendLine($"-- Source closure SHA-256: {sourceSha256}");
        text.AppendLine();
        text.AppendLine("import MemoryCopyOpcodeExtractor.Specification.MemoryCopyExecution");
        text.AppendLine();
        text.AppendLine("namespace Eip803x.Generated.MemoryCopyOpcodeKernel");
        text.AppendLine();
        text.AppendLine("open GasMachine");
        text.AppendLine("open Eip803x.Evm.MemoryStackControl");
        text.AppendLine("open Eip803x.Evm.MemoryStackControl.Stack");
        text.AppendLine();
        text.AppendLine("abbrev Opcode := Eip803x.Evm.MemoryCopyExecution.Opcode");
        text.AppendLine("abbrev DispatchTable := Eip803x.Evm.MemoryCopyExecution.DispatchTable");
        text.AppendLine("abbrev Activation := Eip803x.Evm.MemoryCopyExecution.Activation");
        text.AppendLine("abbrev Schedule := Eip803x.Evm.MemoryCopyExecution.Schedule");
        text.AppendLine("abbrev Status := Eip803x.Evm.MemoryCopyExecution.Status");
        text.AppendLine("abbrev TraceEvent := Eip803x.Evm.MemoryCopyExecution.TraceEvent");
        text.AppendLine("abbrev MachineState := Eip803x.Evm.MemoryCopyExecution.MachineState");
        text.AppendLine("abbrev Outcome := Eip803x.Evm.MemoryCopyExecution.Outcome");
        text.AppendLine();
        text.AppendLine($"def sourceRoot : String := \"{Escape(document.ClosedVm)}\"");
        text.AppendLine($"def sourceFork : String := \"{Escape(document.Fork)}\"");
        text.AppendLine($"def sourceGasPolicy : String := \"{Escape(document.GasPolicy)}\"");
        text.AppendLine($"def sourceBuild : String := \"{Escape(document.StandardBuildSelector)}\"");
        text.AppendLine();
        EmitPinnedConfiguration(text, document);
        EmitSemanticTypes(text);
        EmitDescriptors(text, document);
        EmitMachine(text, document);
        EmitSpecializations(text, document);
        text.AppendLine("def forkLineage : List String :=");
        EmitList(text, document.ForkLineage);
        text.AppendLine();
        text.AppendLine("def openExtractionObligations : List String :=");
        EmitList(text, document.OpenExtractionObligations);
        text.AppendLine();
        text.AppendLine("end Eip803x.Generated.MemoryCopyOpcodeKernel");
        return Utf8WithoutBom.GetBytes(text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static void EmitPinnedConfiguration(StringBuilder text, IrDocument document)
    {
        text.AppendLine("def amsterdamSchedule : Schedule :=");
        text.AppendLine("  {");
        text.AppendLine($"    veryLow := {document.Schedule.VeryLow}");
        text.AppendLine($"    base := {document.Schedule.Base}");
        text.AppendLine($"    copyWord := {document.Schedule.CopyWord}");
        text.AppendLine($"    memoryLinear := {document.Schedule.MemoryLinear}");
        text.AppendLine($"    memoryQuadraticDivisor := {document.Schedule.MemoryQuadraticDivisor}");
        text.AppendLine("    memoryQuadraticDivisorPositive := by decide");
        text.AppendLine($"    maxMemorySize := {document.Schedule.MaxMemorySize}");
        text.AppendLine($"    maxUInt64 := {document.Schedule.MaxUInt64}");
        text.AppendLine($"    maxUInt32 := {document.Schedule.MaxUInt32}");
        text.AppendLine($"    uint256Modulus := {document.Schedule.UInt256Modulus}");
        text.AppendLine("  }");
        text.AppendLine();
        text.AppendLine("def amsterdamActivation : Activation :=");
        text.AppendLine($"  {{ eip211 := {LeanBool(document.Activation.Eip211)}, eip5656 := {LeanBool(document.Activation.Eip5656)} }}");
        text.AppendLine();
    }

    private static void EmitSemanticTypes(StringBuilder text)
    {
        text.AppendLine("inductive Operation where");
        text.AppendLine("  | loadWord | storeWord | storeByte | pushMemorySize | copyZeroExtended");
        text.AppendLine("  | copyReturnData | copyMemory | pushRemainingGas");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("inductive GasClass where");
        text.AppendLine("  | veryLow | base | copy");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("inductive ActivationRule where");
        text.AppendLine("  | always | eip211 | eip5656");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("inductive CopySource where");
        text.AppendLine("  | none | calldata | code | returnData | memory");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("inductive TraceRule where");
        text.AppendLine("  | none | parityLoad | destination | push | sourceThenDestination");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("structure SemanticDescriptor where");
        text.AppendLine("  operation : Operation");
        text.AppendLine("  gasClass : GasClass");
        text.AppendLine("  accessWidth : Nat");
        text.AppendLine("  copySource : CopySource");
        text.AppendLine("  traceRule : TraceRule");
        text.AppendLine("  activation : ActivationRule");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("structure Descriptor where");
        text.AppendLine("  opcode : Opcode");
        text.AppendLine("  instruction : String");
        text.AppendLine("  opcodeByte : Nat");
        text.AppendLine("  handlerBody : String");
        text.AppendLine("  handlerTarget : String");
        text.AppendLine("  stackInputs : Nat");
        text.AppendLine("  stackOutputs : Nat");
        text.AppendLine("  hasCheckedBody : String");
        text.AppendLine("  semantic : SemanticDescriptor");
        text.AppendLine("  effectOrder : List String");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
    }

    private static void EmitDescriptors(StringBuilder text, IrDocument document)
    {
        text.AppendLine("def descriptors : List Descriptor :=");
        text.AppendLine("  [");
        for (int index = 0; index < document.Opcodes.Length; index++)
        {
            OpcodeDescriptor opcode = document.Opcodes[index];
            text.Append("    { opcode := .").Append(opcode.Name)
                .Append(", instruction := \"").Append(Escape(opcode.Instruction))
                .Append("\", opcodeByte := ").Append(opcode.OpcodeByte)
                .Append(", handlerBody := \"").Append(Escape(opcode.HandlerBody))
                .Append("\", handlerTarget := \"").Append(Escape(opcode.HandlerTarget))
                .Append("\", stackInputs := ").Append(opcode.StackInputs)
                .Append(", stackOutputs := ").Append(opcode.StackOutputs)
                .Append(", hasCheckedBody := \"").Append(Escape(opcode.HasCheckedBody))
                .Append("\", semantic := { operation := .").Append(opcode.Semantics.Operation)
                .Append(", gasClass := .").Append(opcode.Semantics.GasClass)
                .Append(", accessWidth := ").Append(opcode.Semantics.AccessWidth)
                .Append(", copySource := .").Append(opcode.Semantics.CopySource)
                .Append(", traceRule := .").Append(opcode.Semantics.TraceRule)
                .Append(", activation := .").Append(LeanActivation(opcode.Activation))
                .Append(" }, effectOrder := ");
            EmitStringListInline(text, opcode.EffectOrder);
            text.Append(" }").AppendLine(index + 1 == document.Opcodes.Length ? string.Empty : ",");
        }
        text.AppendLine("  ]");
        text.AppendLine();
        text.AppendLine("def invalidSemantic : SemanticDescriptor :=");
        text.AppendLine("  { operation := .loadWord, gasClass := .veryLow, accessWidth := 0,");
        text.AppendLine("    copySource := .none, traceRule := .none, activation := .always }");
        text.AppendLine();
        text.AppendLine("def descriptor (opcode : Opcode) : Descriptor :=");
        text.AppendLine("  match descriptors.find? fun item => item.opcode = opcode with");
        text.AppendLine("  | some item => item");
        text.AppendLine("  | none =>");
        text.AppendLine("    { opcode := opcode, instruction := \"\", opcodeByte := 0, handlerBody := \"\", handlerTarget := \"\",");
        text.AppendLine("      stackInputs := 0, stackOutputs := 0, hasCheckedBody := \"\",");
        text.AppendLine("      semantic := invalidSemantic, effectOrder := [] }");
        text.AppendLine();
        text.AppendLine("def descriptorAdmitted : Bool :=");
        text.AppendLine("  descriptors.length = 9 && descriptors.all fun item =>");
        text.AppendLine("    item.instruction = item.opcode.instruction && item.opcodeByte = item.opcode.byte");
        text.AppendLine();
    }

    private static void EmitMachine(StringBuilder text, IrDocument document)
    {
        text.AppendLine("def fixedCost (schedule : Schedule) : GasClass -> Nat");
        text.AppendLine("  | .veryLow => schedule.veryLow");
        text.AppendLine("  | .base => schedule.base");
        text.AppendLine("  | .copy => schedule.veryLow");
        text.AppendLine();
        text.AppendLine("def dispatchEnter (table : DispatchTable) (opcode : Opcode)");
        text.AppendLine("    (state : MachineState) : MachineState :=");
        text.AppendLine("  let traced := if table.tracing then");
        text.AppendLine("    let started := { state with trace := state.trace ++ [.start opcode state.pc state.gas.gasLeft] }");
        text.AppendLine("    let memory := if state.traceCapabilities.memory then");
        text.AppendLine("      { started with trace := started.trace ++");
        text.AppendLine("          [.operationMemory state.memory.bytes, .operationMemorySize state.memory.bytes.length] }");
        text.AppendLine("    else started");
        text.AppendLine("    let stack := if state.traceCapabilities.stack then");
        text.AppendLine("      { memory with trace := memory.trace ++ [.operationStack state.stack.words] }");
        text.AppendLine("    else memory");
        text.AppendLine("    if state.traceCapabilities.returnData then");
        text.AppendLine("      { stack with trace := stack.trace ++ [.operationReturnData state.returnData] }");
        text.AppendLine("    else stack");
        text.AppendLine("  else state");
        text.AppendLine("  { traced with pc := traced.pc + 1, opcodeCount := traced.opcodeCount + 1 }");
        text.AppendLine();
        text.AppendLine("def reportMemory (rule : TraceRule) (table : DispatchTable) (offset : Nat)");
        text.AppendLine("    (bytes : List Byte) (state : MachineState) : MachineState :=");
        text.AppendLine("  let enabled := rule = .parityLoad || rule = .destination || rule = .sourceThenDestination");
        text.AppendLine("  if table.tracing && enabled then");
        text.AppendLine("    { state with trace := state.trace ++ [.memoryChange offset bytes] }");
        text.AppendLine("  else state");
        text.AppendLine();
        text.AppendLine("def reportPush (rule : TraceRule) (table : DispatchTable) (value : UInt256)");
        text.AppendLine("    (state : MachineState) : MachineState :=");
        text.AppendLine("  let payload := match rule with");
        text.AppendLine("    | .parityLoad => wordToBytes value");
        text.AppendLine("    | .push => (wordToBytes value).drop 24");
        text.AppendLine("    | _ => []");
        text.AppendLine("  let enabled := rule = .parityLoad || rule = .push");
        text.AppendLine("  if table.tracing && enabled then");
        text.AppendLine("    { state with trace := state.trace ++ [.stackPush payload] }");
        text.AppendLine("  else state");
        text.AppendLine();
        text.AppendLine("def dispatchFinish (table : DispatchTable) (state : MachineState) : MachineState :=");
        text.AppendLine("  if table.tracing then { state with trace := state.trace ++ [.finish state.gas.gasLeft] } else state");
        text.AppendLine();
        text.AppendLine("def exhaust (state : MachineState) : MachineState :=");
        text.AppendLine("  { state with gas := { state.gas with gasLeft := 0 } }");
        text.AppendLine();
        text.AppendLine("def outcome (status : Status) (state : MachineState) : Outcome := ⟨status, state⟩");
        text.AppendLine();
        text.AppendLine("abbrev DebitResult := Eip803x.Evm.MemoryCopyExecution.Debit");
        text.AppendLine();
        text.AppendLine("def debitExecution (amount : Nat) (state : MachineState) : DebitResult :=");
        text.AppendLine("  match chargeExecution amount state.gas with");
        text.AppendLine("  | .error _ => .outOfGas (exhaust state)");
        text.AppendLine("  | .ok gas => .paid { state with gas := gas }");
        text.AppendLine();
        text.AppendLine("def memoryWords (memory : Memory) : Nat := memory.bytes.length / 32");
        text.AppendLine();
        text.AppendLine("def memoryCost (schedule : Schedule) (words : Nat) : Nat :=");
        text.AppendLine("  words * schedule.memoryLinear + words * words / schedule.memoryQuadraticDivisor");
        text.AppendLine();
        text.AppendLine("def rangeAllowed (schedule : Schedule) (offset length : UInt256) : Bool :=");
        text.AppendLine("  length.val = 0 ||");
        text.AppendLine("    (length.val <= schedule.maxMemorySize && offset.val <= schedule.maxMemorySize - length.val)");
        text.AppendLine();
        text.AppendLine("abbrev ExpansionPlan := Eip803x.Evm.MemoryCopyExecution.MemoryPreparation");
        text.AppendLine();
        text.AppendLine("def prepareExpansion (schedule : Schedule) (memory : Memory) (offset length : UInt256) :");
        text.AppendLine("    ExpansionPlan :=");
        text.AppendLine("  if length.val = 0 then .prepared 0 memory");
        text.AppendLine("  else if rangeAllowed schedule offset length then");
        text.AppendLine("    let expanded := memory.expand offset.val length.val");
        text.AppendLine("    .prepared (memoryCost schedule (memoryWords expanded) - memoryCost schedule (memoryWords memory)) expanded");
        text.AppendLine("  else .invalid");
        text.AppendLine();
        text.AppendLine("def expandAndCharge (prepared : ExpansionPlan) (state : MachineState) :");
        text.AppendLine("    Outcome ⊕ MachineState :=");
        text.AppendLine("  match prepared with");
        text.AppendLine("  | .invalid => .inl (outcome .outOfGas state)");
        text.AppendLine("  | .prepared cost memory =>");
        text.AppendLine("    let installed := { state with memory := memory }");
        text.AppendLine("    match debitExecution cost installed with");
        text.AppendLine("    | .outOfGas exhausted => .inl (outcome .outOfGas exhausted)");
        text.AppendLine("    | .paid paid => .inr paid");
        text.AppendLine();
        text.AppendLine("def checkedWords (schedule : Schedule) (length : UInt256) : Nat × Bool :=");
        text.AppendLine("  if length.val > schedule.maxUInt64 then (0, true)");
        text.AppendLine("  else");
        text.AppendLine("    let words := (length.val + 31) / 32");
        text.AppendLine("    if words > schedule.maxUInt32 then (0, true) else (words, false)");
        text.AppendLine();
        text.AppendLine("def copyCharge (schedule : Schedule) (semantic : SemanticDescriptor) (words : Nat) : Nat :=");
        text.AppendLine("  fixedCost schedule semantic.gasClass + schedule.copyWord * words");
        text.AppendLine();
        text.AppendLine("def replaceTop (stack : Stack) (value : UInt256) : Stack :=");
        text.AppendLine("  match stack.words with");
        text.AppendLine("  | [] => stack");
        text.AppendLine("  | _ :: tail => Stack.fromWords (value :: tail)");
        text.AppendLine();
        text.AppendLine("def runLoad (schedule : Schedule) (table : DispatchTable) (semantic : SemanticDescriptor)");
        text.AppendLine("    (entered : MachineState) : Outcome :=");
        text.AppendLine("  match debitExecution (fixedCost schedule semantic.gasClass) entered with");
        text.AppendLine("  | .outOfGas exhausted => outcome .outOfGas exhausted");
        text.AppendLine("  | .paid charged =>");
        text.AppendLine("    match charged.stack.words with");
        text.AppendLine("    | [] => outcome .stackUnderflow charged");
        text.AppendLine("    | offset :: _ =>");
        text.AppendLine("      match expandAndCharge");
        text.AppendLine("          (prepareExpansion schedule charged.memory offset (Eip803x.Evm.Word.ofNat semantic.accessWidth)) charged with");
        text.AppendLine("      | .inl failed => failed");
        text.AppendLine("      | .inr expanded =>");
        text.AppendLine("        let (loadedMemory, bytes) := charged.memory.readRange offset.val semantic.accessWidth");
        text.AppendLine("        let value := bytesToWord bytes");
        text.AppendLine("        let loaded := { expanded with memory := loadedMemory, stack := replaceTop expanded.stack value }");
        text.AppendLine("        let reportedMemory := reportMemory semantic.traceRule table offset.val bytes loaded");
        text.AppendLine("        outcome .ok (dispatchFinish table (reportPush semantic.traceRule table value reportedMemory))");
        text.AppendLine();
        text.AppendLine("def runStoreWord (schedule : Schedule) (table : DispatchTable) (semantic : SemanticDescriptor)");
        text.AppendLine("    (entered : MachineState) : Outcome :=");
        text.AppendLine("  match debitExecution (fixedCost schedule semantic.gasClass) entered with");
        text.AppendLine("  | .outOfGas exhausted => outcome .outOfGas exhausted");
        text.AppendLine("  | .paid charged =>");
        text.AppendLine("    match charged.stack.popTwo with");
        text.AppendLine("    | none => outcome .stackUnderflow charged");
        text.AppendLine("    | some (offset, value, tail) =>");
        text.AppendLine("      let popped := { charged with stack := tail }");
        text.AppendLine("      match expandAndCharge");
        text.AppendLine("          (prepareExpansion schedule popped.memory offset (Eip803x.Evm.Word.ofNat semantic.accessWidth)) popped with");
        text.AppendLine("      | .inl failed => failed");
        text.AppendLine("      | .inr expanded =>");
        text.AppendLine("        let bytes := wordToBytes value");
        text.AppendLine("        let stored := { expanded with memory := popped.memory.writeRange offset.val bytes }");
        text.AppendLine("        outcome .ok (dispatchFinish table (reportMemory semantic.traceRule table offset.val bytes stored))");
        text.AppendLine();
        text.AppendLine("def runStoreByte (schedule : Schedule) (table : DispatchTable) (semantic : SemanticDescriptor)");
        text.AppendLine("    (entered : MachineState) : Outcome :=");
        text.AppendLine("  match debitExecution (fixedCost schedule semantic.gasClass) entered with");
        text.AppendLine("  | .outOfGas exhausted => outcome .outOfGas exhausted");
        text.AppendLine("  | .paid charged =>");
        text.AppendLine("    match charged.stack.popTwo with");
        text.AppendLine("    | none => outcome .stackUnderflow charged");
        text.AppendLine("    | some (offset, value, tail) =>");
        text.AppendLine("      let popped := { charged with stack := tail }");
        text.AppendLine("      match expandAndCharge");
        text.AppendLine("          (prepareExpansion schedule popped.memory offset (Eip803x.Evm.Word.ofNat semantic.accessWidth)) popped with");
        text.AppendLine("      | .inl failed => failed");
        text.AppendLine("      | .inr expanded =>");
        text.AppendLine("        let bytes := [Eip803x.Evm.MemoryStackControl.byte value.val]");
        text.AppendLine("        let stored := { expanded with memory := popped.memory.writeRange offset.val bytes }");
        text.AppendLine("        outcome .ok (dispatchFinish table (reportMemory semantic.traceRule table offset.val bytes stored))");
        text.AppendLine();
        text.AppendLine("def runPush (schedule : Schedule) (table : DispatchTable) (semantic : SemanticDescriptor)");
        text.AppendLine("    (value : MachineState -> UInt256) (entered : MachineState) : Outcome :=");
        text.AppendLine("  match debitExecution (fixedCost schedule semantic.gasClass) entered with");
        text.AppendLine("  | .outOfGas exhausted => outcome .outOfGas exhausted");
        text.AppendLine("  | .paid charged =>");
        text.AppendLine("    let result := value charged");
        text.AppendLine("    match charged.stack.push result with");
        text.AppendLine("    | none => outcome .stackOverflow charged");
        text.AppendLine("    | some stack =>");
        text.AppendLine("      let pushed := reportPush semantic.traceRule table result { charged with stack := stack }");
        text.AppendLine("      outcome .ok (dispatchFinish table pushed)");
        text.AppendLine();
        text.AppendLine("def sourceBytes (source : CopySource) (state : MachineState) : List Byte :=");
        text.AppendLine("  match source with");
        text.AppendLine("  | .calldata => state.calldata");
        text.AppendLine("  | .code => state.code");
        text.AppendLine("  | .returnData => state.returnData");
        text.AppendLine("  | .memory => state.memory.bytes");
        text.AppendLine("  | .none => []");
        text.AppendLine();
        text.AppendLine("def runZeroExtendedCopy (schedule : Schedule) (table : DispatchTable)");
        text.AppendLine("    (semantic : SemanticDescriptor) (entered : MachineState) : Outcome :=");
        text.AppendLine("  match entered.stack.popThree with");
        text.AppendLine("  | none => outcome .stackUnderflow entered");
        text.AppendLine("  | some (destination, source, length, tail) =>");
        text.AppendLine("    let popped := { entered with stack := tail }");
        text.AppendLine("    let (words, invalidWords) := checkedWords schedule length");
        text.AppendLine("    match debitExecution (copyCharge schedule semantic words) popped with");
        text.AppendLine("    | .outOfGas exhausted => outcome .outOfGas exhausted");
        text.AppendLine("    | .paid charged =>");
        text.AppendLine("      if invalidWords then outcome .outOfGas charged");
        text.AppendLine("      else if length.val = 0 then outcome .ok (dispatchFinish table charged)");
        text.AppendLine("      else");
        text.AppendLine("        match expandAndCharge (prepareExpansion schedule charged.memory destination length) charged with");
        text.AppendLine("        | .inl failed => failed");
        text.AppendLine("        | .inr expanded =>");
        text.AppendLine("          let bytes := readRange (sourceBytes semantic.copySource expanded) source.val length.val");
        text.AppendLine("          let copied := { expanded with memory := charged.memory.writeRange destination.val bytes }");
        text.AppendLine("          outcome .ok (dispatchFinish table");
        text.AppendLine("            (reportMemory semantic.traceRule table destination.val bytes copied))");
        text.AppendLine();
        text.AppendLine("def returnRangeAllowed (schedule : Schedule) (returnData : List Byte)");
        text.AppendLine("    (source length : UInt256) : Bool :=");
        text.AppendLine("  source.val + length.val < schedule.uint256Modulus &&");
        text.AppendLine("    source.val + length.val <= returnData.length");
        text.AppendLine();
        text.AppendLine("def runReturnDataCopy (schedule : Schedule) (table : DispatchTable)");
        text.AppendLine("    (semantic : SemanticDescriptor) (entered : MachineState) : Outcome :=");
        text.AppendLine("  match entered.stack.popThree with");
        text.AppendLine("  | none => outcome .stackUnderflow entered");
        text.AppendLine("  | some (destination, source, length, tail) =>");
        text.AppendLine("    let popped := { entered with stack := tail }");
        text.AppendLine("    let (words, invalidWords) := checkedWords schedule length");
        text.AppendLine("    match debitExecution (copyCharge schedule semantic words) popped with");
        text.AppendLine("    | .outOfGas exhausted => outcome .outOfGas exhausted");
        text.AppendLine("    | .paid charged =>");
        text.AppendLine("      if invalidWords then outcome .outOfGas charged");
        text.AppendLine("      else if !returnRangeAllowed schedule (sourceBytes semantic.copySource charged) source length then");
        text.AppendLine("        outcome .accessViolation charged");
        text.AppendLine("      else if length.val = 0 then outcome .ok (dispatchFinish table charged)");
        text.AppendLine("      else");
        text.AppendLine("        match expandAndCharge (prepareExpansion schedule charged.memory destination length) charged with");
        text.AppendLine("        | .inl failed => failed");
        text.AppendLine("        | .inr expanded =>");
        text.AppendLine("          let bytes := readRange (sourceBytes semantic.copySource expanded) source.val length.val");
        text.AppendLine("          let copied := { expanded with memory := charged.memory.writeRange destination.val bytes }");
        text.AppendLine("          outcome .ok (dispatchFinish table");
        text.AppendLine("            (reportMemory semantic.traceRule table destination.val bytes copied))");
        text.AppendLine();
        text.AppendLine("def runMemoryCopy (schedule : Schedule) (table : DispatchTable)");
        text.AppendLine("    (semantic : SemanticDescriptor) (entered : MachineState) : Outcome :=");
        text.AppendLine("  match entered.stack.popThree with");
        text.AppendLine("  | none => outcome .stackUnderflow entered");
        text.AppendLine("  | some (destination, source, length, tail) =>");
        text.AppendLine("    let popped := { entered with stack := tail }");
        text.AppendLine("    let (words, invalidWords) := checkedWords schedule length");
        text.AppendLine("    match debitExecution (copyCharge schedule semantic words) popped with");
        text.AppendLine("    | .outOfGas exhausted => outcome .outOfGas exhausted");
        text.AppendLine("    | .paid charged =>");
        text.AppendLine("      if invalidWords then outcome .outOfGas charged");
        text.AppendLine("      else if length.val = 0 then outcome .ok (dispatchFinish table charged)");
        text.AppendLine("      else");
        text.AppendLine("        let greatest := if destination.val < source.val then source else destination");
        text.AppendLine("        match expandAndCharge (prepareExpansion schedule charged.memory greatest length) charged with");
        text.AppendLine("        | .inl failed => failed");
        text.AppendLine("        | .inr expanded =>");
        text.AppendLine("          let before := readRange expanded.memory.bytes source.val length.val");
        text.AppendLine("          let copied := { expanded with memory := charged.memory.mcopy destination.val source.val length.val }");
        text.AppendLine("          let reportedSource := reportMemory semantic.traceRule table source.val before copied");
        text.AppendLine("          let after := readRange copied.memory.bytes destination.val length.val");
        text.AppendLine("          let reportedDestination := reportMemory semantic.traceRule table destination.val after reportedSource");
        text.AppendLine("          outcome .ok (dispatchFinish table reportedDestination)");
        text.AppendLine();
        text.AppendLine("def operationActive (activation : Activation) (rule : ActivationRule) : Bool :=");
        text.AppendLine("  match rule with");
        text.AppendLine("  | .always => true");
        text.AppendLine("  | .eip211 => activation.eip211");
        text.AppendLine("  | .eip5656 => activation.eip5656");
        text.AppendLine();
        text.AppendLine("def executeSemantic (schedule : Schedule) (table : DispatchTable) (semantic : SemanticDescriptor)");
        text.AppendLine("    (entered : MachineState) : Outcome :=");
        text.AppendLine("  match semantic.operation with");
        text.AppendLine("  | .loadWord => runLoad schedule table semantic entered");
        text.AppendLine("  | .storeWord => runStoreWord schedule table semantic entered");
        text.AppendLine("  | .storeByte => runStoreByte schedule table semantic entered");
        text.AppendLine("  | .pushMemorySize => runPush schedule table semantic");
        text.AppendLine("      (fun state => Eip803x.Evm.Word.ofNat state.memory.bytes.length) entered");
        text.AppendLine("  | .copyZeroExtended => runZeroExtendedCopy schedule table semantic entered");
        text.AppendLine("  | .copyReturnData => runReturnDataCopy schedule table semantic entered");
        text.AppendLine("  | .copyMemory => runMemoryCopy schedule table semantic entered");
        text.AppendLine("  | .pushRemainingGas => runPush schedule table semantic");
        text.AppendLine("      (fun state => Eip803x.Evm.Word.ofNat state.gas.gasLeft) entered");
        text.AppendLine();
        text.AppendLine("def executeCore (schedule : Schedule) (activation : Activation) (table : DispatchTable)");
        text.AppendLine("    (opcode : Opcode) (state : MachineState) : Outcome :=");
        text.AppendLine("  let entered := dispatchEnter table opcode state");
        text.AppendLine("  let semantic := (descriptor opcode).semantic");
        text.AppendLine("  if operationActive activation semantic.activation then executeSemantic schedule table semantic entered");
        text.AppendLine("  else outcome .inactive entered");
        text.AppendLine();
        text.AppendLine("def closeFailureTrace (table : DispatchTable) (result : Outcome) : Outcome :=");
        text.AppendLine("  if result.status = .ok || result.status = .extractionMismatch then result");
        text.AppendLine("  else");
        text.AppendLine(document.OuterFailure.ClearGasOnOutOfGas
            ? "    let state := if result.status = .outOfGas then exhaust result.state else result.state"
            : "    let state := result.state");
        text.AppendLine("    if table.tracing then");
        text.AppendLine(document.OuterFailure.FinishBeforeError
            ? "      { result with state := { state with trace := state.trace ++ [.finish state.gas.gasLeft, .error result.status] } }"
            : "      { result with state := { state with trace := state.trace ++ [.error result.status, .finish state.gas.gasLeft] } }");
        text.AppendLine("    else { result with state := state }");
        text.AppendLine();
        text.AppendLine("def faultPc (result : Outcome) : Nat :=");
        text.AppendLine(document.OuterFailure.DecrementFaultPc
            ? "  if result.status = .ok then result.state.pc else result.state.pc - 1"
            : "  result.state.pc");
        text.AppendLine();
    }

    private static void EmitSpecializations(StringBuilder text, IrDocument document)
    {
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
                .Append(", tracingFlag := \"").Append(root.TracingFlag)
                .Append("\", cancelableFlag := \"").Append(root.CancelableFlag)
                .Append("\", closedRoot := \"").Append(Escape(root.ClosedRoot)).Append("\" }")
                .AppendLine(index + 1 == document.Specializations.Length ? string.Empty : ",");
        }
        text.AppendLine("  ]");
        text.AppendLine();
        text.AppendLine("def specializationAdmitted (opcode : Opcode) (table : DispatchTable) : Bool :=");
        text.AppendLine("  specializations.any fun item => item.opcode = opcode && item.table = table");
        text.AppendLine();
        text.AppendLine("def executeExtracted (schedule : Schedule) (activation : Activation) (table : DispatchTable)");
        text.AppendLine("    (opcode : Opcode) (state : MachineState) : Outcome :=");
        text.AppendLine("  if descriptorAdmitted && specializationAdmitted opcode table then");
        text.AppendLine("    executeCore schedule activation table opcode state");
        text.AppendLine("  else outcome .extractionMismatch (dispatchEnter table opcode state)");
        text.AppendLine();
        text.AppendLine("def executeAmsterdam (table : DispatchTable) (opcode : Opcode) (state : MachineState) : Outcome :=");
        text.AppendLine("  executeExtracted amsterdamSchedule amsterdamActivation table opcode state");
        text.AppendLine();
        text.AppendLine("def executeAmsterdamClosed (table : DispatchTable) (opcode : Opcode) (state : MachineState) : Outcome :=");
        text.AppendLine("  closeFailureTrace table (executeAmsterdam table opcode state)");
        text.AppendLine();
    }

    private static void ValidateSemanticIr(IrDocument document)
    {
        if (document.Schedule is null || document.Activation is null || document.OuterFailure is null || document.Schedule.MemoryQuadraticDivisor == 0 ||
            string.IsNullOrEmpty(document.Schedule.UInt256Modulus) ||
            document.Schedule.UInt256Modulus.Any(static character => character is < '0' or > '9') ||
            document.Opcodes is null || document.Opcodes.Length != 9 ||
            document.Opcodes.Any(static opcode => opcode is null || opcode.Semantics is null || opcode.EffectOrder is null))
            throw new ExtractionException("Semantic IR must contain nine complete opcode descriptors.");
        foreach (OpcodeDescriptor opcode in document.Opcodes)
        {
            OpcodeSemantics semantic = opcode.Semantics;
            ValidateToken(semantic.Operation,
                ["loadWord", "storeWord", "storeByte", "pushMemorySize", "copyZeroExtended", "copyReturnData", "copyMemory", "pushRemainingGas"],
                opcode.Name + " operation");
            ValidateToken(semantic.GasClass, ["veryLow", "base", "copy"], opcode.Name + " gas class");
            ValidateToken(semantic.CopySource, ["none", "calldata", "code", "returnData", "memory"], opcode.Name + " copy source");
            ValidateToken(semantic.TraceRule, ["none", "parityLoad", "destination", "push", "sourceThenDestination"], opcode.Name + " trace rule");
            _ = LeanActivation(opcode.Activation);
        }
    }

    private static void ValidateToken(string value, string[] admitted, string description)
    {
        if (!admitted.Contains(value, StringComparer.Ordinal))
            throw new ExtractionException($"Unknown {description} {value}.");
    }

    internal static void ValidateSha256(string? value, string description)
    {
        if (value is not { Length: 64 } || value.Any(static character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ExtractionException($"{description} SHA-256 must be a 64-character lowercase hexadecimal digest.");
    }

    private static string LeanActivation(string activation) => activation switch
    {
        "always" => "always",
        "EIP-211" => "eip211",
        "EIP-5656" => "eip5656",
        _ => throw new ExtractionException($"Unknown activation {activation}."),
    };

    private static string LeanBool(bool value) => value ? "true" : "false";

    private static string LeanTable(string table) => table switch
    {
        "NoTrace" => "noTrace",
        "NoTraceCancelable" => "noTraceCancelable",
        "Traced" => "traced",
        "TracedCancelable" => "tracedCancelable",
        _ => throw new ExtractionException($"Unknown dispatch table {table}."),
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
