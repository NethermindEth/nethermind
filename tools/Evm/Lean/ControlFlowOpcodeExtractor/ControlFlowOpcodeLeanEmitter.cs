// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.RegularExpressions;

namespace Nethermind.Evm.Lean.ControlFlowOpcodeExtractor;

internal static class ControlFlowOpcodeLeanEmitter
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] Emit(
        IrDocument document,
        string irSha256,
        string sourceSha256,
        byte[] operationalTemplate)
    {
        ControlFlowOpcodeProfile.ValidateIr(document);
        return EmitFromSemanticIr(document, irSha256, sourceSha256, operationalTemplate);
    }

    internal static byte[] EmitFromSemanticIr(
        IrDocument document,
        string irSha256,
        string sourceSha256,
        byte[] operationalTemplate)
    {
        ValidateSemanticIr(document);
        ValidateSha256(irSha256, "Canonical IR");
        ValidateSha256(sourceSha256, "Source closure");
        _ = DecodeTemplate(operationalTemplate);

        StringBuilder text = new(40_000);
        text.AppendLine("-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited");
        text.AppendLine("-- SPDX-License-Identifier: LGPL-3.0-only");
        text.AppendLine();
        text.AppendLine("-- Generated from round-tripped, source-admitted semantic IR. Definitions only; do not edit.");
        text.AppendLine("-- The imported operational module supplies representation types, not this executable body.");
        text.AppendLine($"-- Extractor version: {document.ExtractorVersion}");
        text.AppendLine($"-- Canonical IR SHA-256: {irSha256}");
        text.AppendLine($"-- Source closure SHA-256: {sourceSha256}");
        text.AppendLine();
        text.AppendLine("import ControlFlowOpcodeExtractor.Specification.ControlFlowExecution");
        text.AppendLine();
        text.AppendLine("namespace Eip803x.Generated.ControlFlowOpcodeKernel");
        text.AppendLine();
        text.AppendLine("open Eip803x.GasMachine");
        text.AppendLine("open Eip803x.Evm.MemoryStackControl");
        text.AppendLine("open Eip803x.Evm.MemoryStackControl.Stack");
        text.AppendLine();
        text.AppendLine("abbrev Opcode := Eip803x.Evm.ControlFlowExecution.Opcode");
        text.AppendLine("abbrev DispatchTable := Eip803x.Evm.ControlFlowExecution.DispatchTable");
        text.AppendLine("abbrev Activation := Eip803x.Evm.ControlFlowExecution.Activation");
        text.AppendLine("abbrev Schedule := Eip803x.Evm.ControlFlowExecution.Schedule");
        text.AppendLine("abbrev Status := Eip803x.Evm.ControlFlowExecution.Status");
        text.AppendLine("abbrev TraceEvent := Eip803x.Evm.ControlFlowExecution.TraceEvent");
        text.AppendLine("abbrev MachineState := Eip803x.Evm.ControlFlowExecution.MachineState");
        text.AppendLine("abbrev Outcome := Eip803x.Evm.ControlFlowExecution.Outcome");
        text.AppendLine();
        text.AppendLine("inductive Operation where");
        text.AppendLine("  | stop | slotNumber | jump | jumpIf | programCounter | jumpDestination | return_ | revert");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("inductive ActivationRule where");
        text.AppendLine("  | always | eip7843 | eip140");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        EmitExtractedProfile(text, document);
        EmitIndependentExecution(text, document);
        text.AppendLine();
        text.AppendLine("end Eip803x.Generated.ControlFlowOpcodeKernel");
        string result = text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        if (result.Contains("\r", StringComparison.Ordinal) || ContainsProofDeclaration(result))
            throw new ExtractionException("Generated Lean must be LF-only and theorem-free.");
        return StrictUtf8.GetBytes(result);
    }

    private static string DecodeTemplate(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes is [0xef, 0xbb, 0xbf, ..])
            throw new ExtractionException("Operational template must be non-empty UTF-8 without BOM.");
        string template;
        try
        {
            template = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ExtractionException($"Operational template is not valid UTF-8: {exception.Message}");
        }
        if (template.Contains('\r') || !template.EndsWith('\n'))
            throw new ExtractionException("Operational template must use exact LF newlines and end with one newline.");
        if (ContainsProofDeclaration(template))
            throw new ExtractionException("Operational template must contain definitions only.");
        return template;
    }

    private static bool ContainsProofDeclaration(string source) =>
        Regex.IsMatch(source, @"(?m)^\s*(theorem|axiom|example|admit|sorry)\b", RegexOptions.CultureInvariant);

    private static void EmitExtractedProfile(StringBuilder text, IrDocument document)
    {
        text.AppendLine();
        text.AppendLine($"def sourceRoot : String := \"{Escape(document.ClosedVm)}\"");
        text.AppendLine($"def sourceFork : String := \"{Escape(document.Fork)}\"");
        text.AppendLine($"def sourceGasPolicy : String := \"{Escape(document.GasPolicy)}\"");
        text.AppendLine($"def sourceBuild : String := \"{Escape(document.StandardBuildSelector)}\"");
        text.AppendLine();
        text.AppendLine("structure Descriptor where");
        text.AppendLine("  opcode : Opcode");
        text.AppendLine("  instruction : String");
        text.AppendLine("  opcodeByte : Nat");
        text.AppendLine("  handlerBody : String");
        text.AppendLine("  handlerKind : String");
        text.AppendLine("  handlerTarget : String");
        text.AppendLine("  operation : Operation");
        text.AppendLine("  activationRule : ActivationRule");
        text.AppendLine("  stackInputs : Nat");
        text.AppendLine("  stackOutputs : Nat");
        text.AppendLine("  checkedBody : String");
        text.AppendLine("  endsInstructionTrace : Bool");
        text.AppendLine("  activation : String");
        text.AppendLine("  gasClass : String");
        text.AppendLine("  fixedGas : Nat");
        text.AppendLine("  tracePushWidth : Nat");
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
                .Append(", handlerBody := \"").Append(Escape(opcode.HandlerBody))
                .Append("\", handlerKind := \"").Append(Escape(opcode.HandlerKind))
                .Append("\", handlerTarget := \"").Append(Escape(opcode.HandlerTarget))
                .Append("\", operation := .").Append(LeanOperation(opcode.Name))
                .Append(", activationRule := .").Append(LeanActivation(opcode.Activation))
                .Append(", stackInputs := ").Append(opcode.StackInputs)
                .Append(", stackOutputs := ").Append(opcode.StackOutputs)
                .Append(", checkedBody := \"").Append(Escape(opcode.HasCheckedBody))
                .Append("\", endsInstructionTrace := ").Append(opcode.EndsInstructionTrace ? "true" : "false")
                .Append(", activation := \"").Append(Escape(opcode.Activation))
                .Append("\", gasClass := \"").Append(Escape(opcode.GasClass))
                .Append("\", fixedGas := ").Append(opcode.FixedGas)
                .Append(", tracePushWidth := ").Append(opcode.TracePushWidth)
                .Append(", effectOrder := ");
            EmitStringListInline(text, opcode.EffectOrder);
            text.Append(" }").AppendLine(index + 1 == document.Opcodes.Length ? string.Empty : ",");
        }
        text.AppendLine("  ]");
        text.AppendLine();
        text.AppendLine("def descriptorAdmitted : Bool :=");
        text.AppendLine("  descriptors.length = 8 && descriptors.all fun item =>");
        text.AppendLine("    item.instruction = item.opcode.instruction && item.opcodeByte = item.opcode.byte &&");
        text.AppendLine("      !item.endsInstructionTrace");
        text.AppendLine();
        text.AppendLine("structure Specialization where");
        text.AppendLine("  opcode : Opcode");
        text.AppendLine("  table : DispatchTable");
        text.AppendLine("  tracingFlag : String");
        text.AppendLine("  cancelableFlag : String");
        text.AppendLine("  continuableFlag : String");
        text.AppendLine("  enabledRoot : String");
        text.AppendLine("  disabledRoot : String");
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
                .Append("\", continuableFlag := \"").Append(root.ContinuableFlag)
                .Append("\", enabledRoot := \"").Append(Escape(root.EnabledRoot))
                .Append("\", disabledRoot := \"").Append(Escape(root.DisabledRoot)).Append("\" }")
                .AppendLine(index + 1 == document.Specializations.Length ? string.Empty : ",");
        }
        text.AppendLine("  ]");
        text.AppendLine();
        text.AppendLine("def specializationAdmitted (opcode : Opcode) (table : DispatchTable) : Bool :=");
        text.AppendLine("  specializations.any fun item => item.opcode = opcode && item.table = table");
        text.AppendLine();
        text.AppendLine("def forkLineage : List String :=");
        EmitList(text, document.ForkLineage);
        text.AppendLine();
        text.AppendLine("def semanticBindings : List String :=");
        EmitList(text, document.SemanticBindings);
        text.AppendLine();
        text.AppendLine("def openExtractionObligations : List String :=");
        EmitList(text, document.OpenExtractionObligations);
    }

    private static void EmitIndependentExecution(StringBuilder text, IrDocument document)
    {
        GasScheduleDescriptor schedule = document.Schedule;
        text.AppendLine();
        text.AppendLine("def descriptor (opcode : Opcode) : Descriptor :=");
        text.AppendLine("  match descriptors.find? fun item => item.opcode = opcode with");
        text.AppendLine("  | some item => item");
        text.AppendLine("  | none =>");
        text.AppendLine("    { opcode := opcode, instruction := \"\", opcodeByte := 0, handlerBody := \"\",");
        text.AppendLine("      handlerKind := \"\", handlerTarget := \"\", operation := .stop,");
        text.AppendLine("      activationRule := .always, stackInputs := 0, stackOutputs := 0,");
        text.AppendLine("      checkedBody := \"\", endsInstructionTrace := false, activation := \"\", gasClass := \"\", fixedGas := 0,");
        text.AppendLine("      tracePushWidth := 0,");
        text.AppendLine("      effectOrder := [] }");
        text.AppendLine();
        text.AppendLine("def amsterdamSchedule : Schedule :=");
        text.AppendLine("  { zero := " + schedule.Zero);
        text.AppendLine("    base := " + schedule.Base);
        text.AppendLine("    mid := " + schedule.Mid);
        text.AppendLine("    high := " + schedule.High);
        text.AppendLine("    jumpdest := " + schedule.JumpDest);
        text.AppendLine("    memoryLinear := " + schedule.MemoryLinear);
        text.AppendLine("    memoryQuadraticDivisor := " + schedule.MemoryQuadraticDivisor);
        text.AppendLine("    maxMemorySize := " + schedule.MaxMemorySize + " }");
        text.AppendLine();
        text.AppendLine("def amsterdamActivation : Activation := ⟨true, true⟩");
        text.AppendLine();
        JumpValidationDescriptor jump = document.JumpValidation;
        text.AppendLine("structure JumpValidationProfile where");
        text.AppendLine("  stopOpcode : Nat");
        text.AppendLine("  jumpDestinationOpcode : Nat");
        text.AppendLine("  pushFirstOpcode : Nat");
        text.AppendLine("  pushLastOpcode : Nat");
        text.AppendLine("  maximumDestination : Nat");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("def jumpValidationProfile : JumpValidationProfile :=");
        text.AppendLine("  { stopOpcode := " + jump.StopOpcode);
        text.AppendLine("    jumpDestinationOpcode := " + jump.JumpDestinationOpcode);
        text.AppendLine("    pushFirstOpcode := " + jump.PushFirstOpcode);
        text.AppendLine("    pushLastOpcode := " + jump.PushLastOpcode);
        text.AppendLine("    maximumDestination := " + jump.MaximumDestination + " }");
        text.AppendLine();
        text.AppendLine("def extractedInstructionWidth (profile : JumpValidationProfile) (opcode : Byte) : Nat :=");
        text.AppendLine("  if profile.pushFirstOpcode ≤ opcode.val && opcode.val ≤ profile.pushLastOpcode then");
        text.AppendLine("    opcode.val - profile.pushFirstOpcode + 2");
        text.AppendLine("  else 1");
        text.AppendLine();
        text.AppendLine("def scanExtractedJumpDestination (profile : JumpValidationProfile)");
        text.AppendLine("    (code : List Byte) (target pc fuel : Nat) : Bool :=");
        text.AppendLine("  match fuel with");
        text.AppendLine("  | 0 => false");
        text.AppendLine("  | fuel + 1 =>");
        text.AppendLine("    if pc = target then");
        text.AppendLine("      match code[pc]? with");
        text.AppendLine("      | some value => value.val = profile.jumpDestinationOpcode");
        text.AppendLine("      | none => false");
        text.AppendLine("    else");
        text.AppendLine("      match code[pc]? with");
        text.AppendLine("      | none => false");
        text.AppendLine("      | some value => scanExtractedJumpDestination profile code target");
        text.AppendLine("          (pc + extractedInstructionWidth profile value) fuel");
        text.AppendLine();
        text.AppendLine("def extractedValidJumpDestination (profile : JumpValidationProfile)");
        text.AppendLine("    (code : List Byte) (target : Nat) : Bool :=");
        text.AppendLine("  match code with");
        text.AppendLine("  | [] => false");
        text.AppendLine("  | first :: _ =>");
        text.AppendLine("    if first.val = profile.stopOpcode then false");
        text.AppendLine("    else target ≤ profile.maximumDestination && target < code.length &&");
        text.AppendLine("      scanExtractedJumpDestination profile code target 0 code.length");
        text.AppendLine();
        text.AppendLine("def operationActive (activation : Activation) : ActivationRule → Bool");
        text.AppendLine("  | .always => true");
        text.AppendLine("  | .eip7843 => activation.eip7843");
        text.AppendLine("  | .eip140 => activation.revert");
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
        text.AppendLine("      { memory with trace := memory.trace ++ [.operationStack state.stack.words.reverse] }");
        text.AppendLine("    else memory");
        text.AppendLine("    if state.traceCapabilities.returnData then");
        text.AppendLine("      { stack with trace := stack.trace ++ [.operationReturnData state.previousReturnData] }");
        text.AppendLine("    else stack");
        text.AppendLine("  else state");
        text.AppendLine("  { traced with pc := traced.pc + 1, opcodeCount := traced.opcodeCount + 1 }");
        text.AppendLine();
        text.AppendLine("def reportPush (table : DispatchTable) (width : Nat) (value : Eip803x.UInt256)");
        text.AppendLine("    (state : MachineState) : MachineState :=");
        text.AppendLine("  if table.tracing then");
        text.AppendLine("    { state with trace := state.trace ++ [.stackPush ((wordToBytes value).drop (32 - width))] }");
        text.AppendLine("  else state");
        text.AppendLine();
        text.AppendLine("def dispatchFinish (table : DispatchTable) (endsInstructionTrace : Bool)");
        text.AppendLine("    (state : MachineState) : MachineState :=");
        text.AppendLine("  if table.tracing && !endsInstructionTrace then");
        text.AppendLine("    { state with trace := state.trace ++ [.finish state.gas.gasLeft] }");
        text.AppendLine("  else state");
        text.AppendLine();
        text.AppendLine("def exhaust (state : MachineState) : MachineState :=");
        text.AppendLine("  { state with gas := { state.gas with gasLeft := 0 } }");
        text.AppendLine();
        text.AppendLine("def outcome (status : Status) (state : MachineState) : Outcome := ⟨status, state⟩");
        text.AppendLine();
        text.AppendLine("abbrev DebitResult := Eip803x.Evm.ControlFlowExecution.Debit");
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
        text.AppendLine("def rangeAllowed (schedule : Schedule) (offset length : Eip803x.UInt256) : Bool :=");
        text.AppendLine("  length.val = 0 ||");
        text.AppendLine("    (length.val <= schedule.maxMemorySize && offset.val <= schedule.maxMemorySize - length.val)");
        text.AppendLine();
        text.AppendLine("abbrev ExpansionPlan := Eip803x.Evm.ControlFlowExecution.MemoryPreparation");
        text.AppendLine();
        text.AppendLine("def prepareExpansion (schedule : Schedule) (memory : Memory)");
        text.AppendLine("    (offset length : Eip803x.UInt256) : ExpansionPlan :=");
        text.AppendLine("  if length.val = 0 then .prepared 0");
        text.AppendLine("  else if rangeAllowed schedule offset length then");
        text.AppendLine("    let expanded := memory.expand offset.val length.val");
        text.AppendLine("    .prepared (memoryCost schedule (memoryWords expanded) - memoryCost schedule (memoryWords memory))");
        text.AppendLine("  else .invalid");
        text.AppendLine();
        text.AppendLine("def finishSuccess (table : DispatchTable) (endsInstructionTrace : Bool)");
        text.AppendLine("    (state : MachineState) : Outcome :=");
        text.AppendLine("  outcome .ok (dispatchFinish table endsInstructionTrace state)");
        text.AppendLine();
        text.AppendLine("def pushFixed (cost traceWidth : Nat) (table : DispatchTable) (endsInstructionTrace : Bool)");
        text.AppendLine("    (value : MachineState → Eip803x.UInt256) (entered : MachineState) : Outcome :=");
        text.AppendLine("  match debitExecution cost entered with");
        text.AppendLine("  | .outOfGas failed => outcome .outOfGas failed");
        text.AppendLine("  | .paid charged =>");
        text.AppendLine("    let pushed := value charged");
        text.AppendLine("    match charged.stack.push pushed with");
        text.AppendLine("    | none => outcome .stackOverflow charged");
        text.AppendLine("    | some stack => finishSuccess table endsInstructionTrace");
        text.AppendLine("        (reportPush table traceWidth pushed { charged with stack := stack })");
        text.AppendLine();
        text.AppendLine("def runSlotNumber (descriptor : Descriptor) (table : DispatchTable)");
        text.AppendLine("    (entered : MachineState) : Outcome :=");
        text.AppendLine("  match entered.slotNumber with");
        text.AppendLine("  | none => outcome .badInstruction entered");
        text.AppendLine("  | some slot => pushFixed descriptor.fixedGas descriptor.tracePushWidth table descriptor.endsInstructionTrace");
        text.AppendLine("      (fun _ => Eip803x.Evm.Word.ofNat slot) entered");
        text.AppendLine();
        text.AppendLine("def runJumpDestination (descriptor : Descriptor) (table : DispatchTable)");
        text.AppendLine("    (entered : MachineState) : Outcome :=");
        text.AppendLine("  match debitExecution descriptor.fixedGas entered with");
        text.AppendLine("  | .outOfGas failed => outcome .outOfGas failed");
        text.AppendLine("  | .paid charged => finishSuccess table descriptor.endsInstructionTrace charged");
        text.AppendLine();
        text.AppendLine("def fuseJumpDestination (schedule : Schedule) (table : DispatchTable)");
        text.AppendLine("    (endsInstructionTrace : Bool) (target : Nat)");
        text.AppendLine("    (state : MachineState) : Outcome :=");
        text.AppendLine("  if table.tracing then finishSuccess table endsInstructionTrace { state with pc := target }");
        text.AppendLine("  else");
        text.AppendLine("    let skipped := { state with pc := target + 1, opcodeCount := state.opcodeCount + 1 }");
        text.AppendLine("    match debitExecution schedule.jumpdest skipped with");
        text.AppendLine("    | .outOfGas failed => outcome .outOfGas failed");
        text.AppendLine("    | .paid charged => finishSuccess table endsInstructionTrace charged");
        text.AppendLine();
        text.AppendLine("def runJump (schedule : Schedule) (descriptor : Descriptor) (table : DispatchTable)");
        text.AppendLine("    (entered : MachineState) : Outcome :=");
        text.AppendLine("  match debitExecution descriptor.fixedGas entered with");
        text.AppendLine("  | .outOfGas failed => outcome .outOfGas failed");
        text.AppendLine("  | .paid charged =>");
        text.AppendLine("    match charged.stack.pop with");
        text.AppendLine("    | none => outcome .stackUnderflow charged");
        text.AppendLine("    | some (target, tail) =>");
        text.AppendLine("      let popped := { charged with stack := tail }");
        text.AppendLine("      if extractedValidJumpDestination jumpValidationProfile popped.code target.val then");
        text.AppendLine("        fuseJumpDestination schedule table descriptor.endsInstructionTrace target.val popped");
        text.AppendLine("      else outcome .invalidJump popped");
        text.AppendLine();
        text.AppendLine("def runJumpIf (schedule : Schedule) (descriptor : Descriptor) (table : DispatchTable)");
        text.AppendLine("    (entered : MachineState) : Outcome :=");
        text.AppendLine("  match debitExecution descriptor.fixedGas entered with");
        text.AppendLine("  | .outOfGas failed => outcome .outOfGas failed");
        text.AppendLine("  | .paid charged =>");
        text.AppendLine("    match charged.stack.popTwo with");
        text.AppendLine("    | none => outcome .stackUnderflow charged");
        text.AppendLine("    | some (target, condition, tail) =>");
        text.AppendLine("      let popped := { charged with stack := tail }");
        text.AppendLine("      if condition.val = 0 then finishSuccess table false popped");
        text.AppendLine("      else if extractedValidJumpDestination jumpValidationProfile popped.code target.val then");
        text.AppendLine("        fuseJumpDestination schedule table false target.val popped");
        text.AppendLine("      else outcome .invalidJump popped");
        text.AppendLine();
        text.AppendLine("def runReturnLike (schedule : Schedule) (terminal : Status) (entered : MachineState) : Outcome :=");
        text.AppendLine("  match entered.stack.popTwo with");
        text.AppendLine("  | none => outcome .stackUnderflow entered");
        text.AppendLine("  | some (offset, length, tail) =>");
        text.AppendLine("    let popped := { entered with stack := tail }");
        text.AppendLine("    match prepareExpansion schedule popped.memory offset length with");
        text.AppendLine("    | .invalid => outcome .outOfGas popped");
        text.AppendLine("    | .prepared expansionCost =>");
        text.AppendLine("      match debitExecution expansionCost popped with");
        text.AppendLine("      | .outOfGas failed => outcome .outOfGas failed");
        text.AppendLine("      | .paid charged =>");
        text.AppendLine("        let (memory, data) := charged.memory.readRange offset.val length.val");
        text.AppendLine("        outcome terminal { charged with memory := memory, stagedOutput := some data }");
        text.AppendLine();
        text.AppendLine("def executeSemantic (schedule : Schedule) (table : DispatchTable) (descriptor : Descriptor)");
        text.AppendLine("    (entered : MachineState) : Outcome :=");
        text.AppendLine("  match descriptor.operation with");
        text.AppendLine("  | .stop => outcome .stop entered");
        text.AppendLine("  | .slotNumber => runSlotNumber descriptor table entered");
        text.AppendLine("  | .jump => runJump schedule descriptor table entered");
        text.AppendLine("  | .jumpIf => runJumpIf schedule descriptor table entered");
        text.AppendLine("  | .programCounter => pushFixed descriptor.fixedGas descriptor.tracePushWidth table descriptor.endsInstructionTrace");
        text.AppendLine("      (fun state => Eip803x.Evm.Word.ofNat (state.pc - 1)) entered");
        text.AppendLine("  | .jumpDestination => runJumpDestination descriptor table entered");
        text.AppendLine("  | .return_ => runReturnLike schedule .stop entered");
        text.AppendLine("  | .revert => runReturnLike schedule .revert entered");
        text.AppendLine();
        text.AppendLine("def executeCore (schedule : Schedule) (activation : Activation) (table : DispatchTable)");
        text.AppendLine("    (opcode : Opcode) (state : MachineState) : Outcome :=");
        text.AppendLine("  let entered := dispatchEnter table opcode state");
        text.AppendLine("  let selected := descriptor opcode");
        text.AppendLine("  if operationActive activation selected.activationRule then");
        text.AppendLine("    executeSemantic schedule table selected entered");
        text.AppendLine("  else outcome .badInstruction entered");
        text.AppendLine();
        text.AppendLine("def closeFrame (table : DispatchTable) (result : Outcome) : Outcome :=");
        text.AppendLine("  let closed := if result.status = .outOfGas then");
        text.AppendLine("    { result with state := exhaust result.state }");
        text.AppendLine("  else result");
        text.AppendLine("  if closed.status = .ok || closed.status = .extractionMismatch then closed");
        text.AppendLine("  else if table.tracing then");
        text.AppendLine("    let finished := { closed.state with trace := closed.state.trace ++ [.finish closed.state.gas.gasLeft] }");
        text.AppendLine("    if closed.status = .stop || closed.status = .revert then");
        text.AppendLine("      { closed with state := finished }");
        text.AppendLine("    else");
        text.AppendLine("      { closed with state := { finished with trace := finished.trace ++ [.error closed.status] } }");
        text.AppendLine("  else closed");
        text.AppendLine();
        text.AppendLine("def executeExtracted (schedule : Schedule) (activation : Activation)");
        text.AppendLine("    (table : DispatchTable) (opcode : Opcode) (state : MachineState) : Outcome :=");
        text.AppendLine("  if descriptorAdmitted && specializationAdmitted opcode table then");
        text.AppendLine("    closeFrame table (executeCore schedule activation table opcode state)");
        text.AppendLine("  else outcome .extractionMismatch state");
        text.AppendLine();
        text.AppendLine("def executeAmsterdam (table : DispatchTable) (opcode : Opcode) (state : MachineState) : Outcome :=");
        text.AppendLine("  executeExtracted amsterdamSchedule amsterdamActivation table opcode state");
    }

    private static void ValidateSemanticIr(IrDocument document)
    {
        if (document.JumpValidation is null || document.Opcodes is null || document.Opcodes.Length != 8 ||
            document.Opcodes.Any(static opcode => opcode is null || opcode.EffectOrder is null))
            throw new ExtractionException("Semantic IR must contain eight complete opcode descriptors.");
        JumpValidationDescriptor jump = document.JumpValidation;
        if (jump.StopOpcode is < 0 or > byte.MaxValue ||
            jump.JumpDestinationOpcode is < 0 or > byte.MaxValue ||
            jump.PushFirstOpcode is < 0 or > byte.MaxValue ||
            jump.PushLastOpcode is < 0 or > byte.MaxValue ||
            jump.PushFirstOpcode > jump.PushLastOpcode || jump.MaximumDestination < 0)
            throw new ExtractionException("Semantic IR contains an invalid jump-validation profile.");
        foreach (OpcodeDescriptor opcode in document.Opcodes)
        {
            ValidateToken(opcode.Name, ["stop", "slotnum", "jump", "jumpi", "pc", "jumpdest", "return_", "revert"], opcode.Instruction + " name");
            ValidateToken(opcode.HandlerKind, ["continuable", "terminating", "jumpIf"], opcode.Instruction + " handler kind");
            ValidateToken(opcode.Activation, ["always", "EIP-7843", "Byzantium REVERT"], opcode.Instruction + " activation");
            ValidateToken(opcode.GasClass, ["zero", "base", "mid", "high", "jumpDest", "memory"], opcode.Instruction + " gas class");
        }
        if (document.Specializations is null || document.Specializations.Length != 32)
            throw new ExtractionException("Semantic IR must contain exactly 32 opcode/table specializations.");
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

    private static string LeanTable(string table) => table switch
    {
        "NoTrace" => "noTrace",
        "NoTraceCancelable" => "noTraceCancelable",
        "Traced" => "traced",
        "TracedCancelable" => "tracedCancelable",
        _ => throw new ExtractionException($"Unknown dispatch table '{table}'."),
    };

    private static string LeanOperation(string opcode) => opcode switch
    {
        "stop" => "stop",
        "slotnum" => "slotNumber",
        "jump" => "jump",
        "jumpi" => "jumpIf",
        "pc" => "programCounter",
        "jumpdest" => "jumpDestination",
        "return_" => "return_",
        "revert" => "revert",
        _ => throw new ExtractionException($"Unknown opcode semantic '{opcode}'."),
    };

    private static string LeanActivation(string activation) => activation switch
    {
        "always" => "always",
        "EIP-7843" => "eip7843",
        "Byzantium REVERT" => "eip140",
        _ => throw new ExtractionException($"Unknown activation semantic '{activation}'."),
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
