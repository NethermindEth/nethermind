// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.Evm.Lean.LogOpcodeExtractor;

internal static class LogOpcodeLeanEmitter
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    internal static byte[] Emit(IrDocument document, string irSha256, string sourceSha256)
    {
        LogOpcodeProfile.ValidateIr(document);
        ValidateSha256(irSha256, "Canonical IR");
        ValidateSha256(sourceSha256, "Source closure");
        StringBuilder text = new();
        text.AppendLine("-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited");
        text.AppendLine("-- SPDX-License-Identifier: LGPL-3.0-only");
        text.AppendLine();
        text.AppendLine("-- Generated from deserialized, source-derived IR. Definitions only; do not edit.");
        text.AppendLine($"-- Extractor version: {document.ExtractorVersion}");
        text.AppendLine($"-- Canonical IR SHA-256: {irSha256}");
        text.AppendLine($"-- Source closure SHA-256: {sourceSha256}");
        text.AppendLine();
        text.AppendLine("import Eip803x.Gas");
        text.AppendLine("import Eip803x.Evm.MemoryStackControl");
        text.AppendLine("import Init.Data.String.Search");
        text.AppendLine();
        text.AppendLine("namespace Eip803x.Generated.LogOpcodeKernel");
        text.AppendLine();
        text.AppendLine("open GasMachine");
        text.AppendLine("open Eip803x.Evm.MemoryStackControl");
        text.AppendLine();
        text.AppendLine($"def sourceRoot : String := \"{Escape(document.Root)}\"");
        text.AppendLine($"def sourceGasPolicy : String := \"{Escape(document.GasPolicy)}\"");
        text.AppendLine($"def sourceFork : String := \"{Escape(document.Fork)}\"");
        text.AppendLine();
        text.AppendLine("inductive Opcode where");
        text.AppendLine("  | log0 | log1 | log2 | log3 | log4");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("inductive Effect where");
        foreach (string effect in document.Opcodes[0].EffectOrder)
            text.AppendLine($"  | {effect}");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("inductive DispatchTable where");
        text.AppendLine("  | noTrace | noTraceCancelable | traced | tracedCancelable");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("structure Descriptor where");
        text.AppendLine("  instruction : String");
        text.AppendLine("  opcodeByte : Nat");
        text.AppendLine("  topicCount : Nat");
        text.AppendLine("  handlerBody : String");
        text.AppendLine("  programCounterDelta : Nat");
        text.AppendLine("  headerStackInputs : Nat");
        text.AppendLine("  effectOrder : List Effect");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("structure Specialization where");
        text.AppendLine("  opcode : Opcode");
        text.AppendLine("  table : DispatchTable");
        text.AppendLine("  tracingFlag : String");
        text.AppendLine("  cancelableFlag : String");
        text.AppendLine("  closedRoot : String");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("def allOpcodes : List Opcode := [.log0, .log1, .log2, .log3, .log4]");
        text.AppendLine("def allDispatchTables : List DispatchTable :=");
        text.AppendLine("  [.noTrace, .noTraceCancelable, .traced, .tracedCancelable]");
        text.AppendLine();
        text.AppendLine("def expectedEffectOrder : List Effect :=");
        EmitList(text, document.Opcodes[0].EffectOrder.Select(static effect => "." + effect));
        text.AppendLine();
        text.AppendLine("def descriptor : Opcode → Descriptor");
        foreach (OpcodeDescriptor opcode in document.Opcodes)
        {
            text.AppendLine($"  | .{opcode.Name} =>");
            text.AppendLine("    {");
            text.AppendLine($"      instruction := \"{Escape(opcode.Instruction)}\"");
            text.AppendLine($"      opcodeByte := {opcode.OpcodeByte}");
            text.AppendLine($"      topicCount := {opcode.TopicCount}");
            text.AppendLine($"      handlerBody := \"{Escape(opcode.HandlerBody)}\"");
            text.AppendLine($"      programCounterDelta := {opcode.ProgramCounterDelta}");
            text.AppendLine($"      headerStackInputs := {opcode.HeaderStackInputs}");
            text.AppendLine("      effectOrder := expectedEffectOrder");
            text.AppendLine("    }");
        }
        text.AppendLine();
        text.AppendLine("structure Schedule where");
        text.AppendLine("  logBase : Nat");
        text.AppendLine("  logTopic : Nat");
        text.AppendLine("  logDataByte : Nat");
        text.AppendLine("  memoryLinear : Nat");
        text.AppendLine("  memoryQuadraticDivisor : Nat");
        text.AppendLine("  memoryQuadraticDivisorPositive : 0 < memoryQuadraticDivisor");
        text.AppendLine("  maxMemorySize : Nat");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("def amsterdamSchedule : Schedule :=");
        text.AppendLine("  {");
        text.AppendLine($"    logBase := {document.LogBaseGas}");
        text.AppendLine($"    logTopic := {document.LogTopicGas}");
        text.AppendLine($"    logDataByte := {document.LogDataByteGas}");
        text.AppendLine($"    memoryLinear := {document.MemoryLinearGas}");
        text.AppendLine($"    memoryQuadraticDivisor := {document.MemoryQuadraticDivisor}");
        text.AppendLine("    memoryQuadraticDivisorPositive := by decide");
        text.AppendLine($"    maxMemorySize := {document.MaxMemorySize}");
        text.AppendLine("  }");
        text.AppendLine();
        EmitOperationalMachine(text);
        EmitSpecializations(text, document);
        text.AppendLine("def forkLineage : List String :=");
        EmitList(text, document.ForkLineage.Select(static fork => $"\"{Escape(fork)}\""));
        text.AppendLine();
        text.AppendLine("def openExtractionObligations : List String :=");
        EmitList(text, document.OpenExtractionObligations.Select(static item => $"\"{Escape(item)}\""));
        text.AppendLine();
        text.AppendLine("end Eip803x.Generated.LogOpcodeKernel");
        return Utf8WithoutBom.GetBytes(text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static void EmitOperationalMachine(StringBuilder text)
    {
        text.AppendLine("structure LogEntry where");
        text.AppendLine("  address : UInt256");
        text.AppendLine("  data : List Byte");
        text.AppendLine("  topics : List UInt256");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("structure MachineState where");
        text.AppendLine("  pc : Nat");
        text.AppendLine("  gas : GasState");
        text.AppendLine("  stack : Stack");
        text.AppendLine("  memory : Memory");
        text.AppendLine("  executingAccount : UInt256");
        text.AppendLine("  logs : List LogEntry");
        text.AppendLine("  traceLogs : Bool");
        text.AppendLine("  reportedLogs : List LogEntry");
        text.AppendLine("  deriving Repr");
        text.AppendLine();
        text.AppendLine("inductive Status where");
        text.AppendLine("  | ok | outOfGas | stackUnderflow | staticCallViolation | extractionMismatch");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("structure Outcome where");
        text.AppendLine("  status : Status");
        text.AppendLine("  state : MachineState");
        text.AppendLine("  deriving Repr");
        text.AppendLine();
        text.AppendLine("def memoryWords (memory : Memory) : Nat := memory.bytes.length / 32");
        text.AppendLine();
        text.AppendLine("def memoryCost (schedule : Schedule) (words : Nat) : Nat :=");
        text.AppendLine("  words * schedule.memoryLinear + words * words / schedule.memoryQuadraticDivisor");
        text.AppendLine();
        text.AppendLine("def memoryRangeValid (schedule : Schedule) (offset length : UInt256) : Bool :=");
        text.AppendLine("  length.val = 0 ||");
        text.AppendLine("    (length.val ≤ schedule.maxMemorySize && offset.val ≤ schedule.maxMemorySize - length.val)");
        text.AppendLine();
        text.AppendLine("def prepareMemory (schedule : Schedule) (memory : Memory) (offset length : UInt256) :");
        text.AppendLine("    Option (Nat × Memory) :=");
        text.AppendLine("  if length.val = 0 then");
        text.AppendLine("    some (0, memory)");
        text.AppendLine("  else if memoryRangeValid schedule offset length then");
        text.AppendLine("    let expanded := memory.expand offset.val length.val");
        text.AppendLine("    let oldWords := memoryWords memory");
        text.AppendLine("    let newWords := memoryWords expanded");
        text.AppendLine("    some (memoryCost schedule newWords - memoryCost schedule oldWords, expanded)");
        text.AppendLine("  else");
        text.AppendLine("    none");
        text.AppendLine();
        text.AppendLine("def emissionCost (schedule : Schedule) (opcode : Opcode) (dataSize : Nat) : Nat :=");
        text.AppendLine("  schedule.logBase + (descriptor opcode).topicCount * schedule.logTopic +");
        text.AppendLine("    dataSize * schedule.logDataByte");
        text.AppendLine();
        text.AppendLine("def advancePc (opcode : Opcode) (state : MachineState) : MachineState :=");
        text.AppendLine("  { state with pc := state.pc + (descriptor opcode).programCounterDelta }");
        text.AppendLine();
        text.AppendLine("def exhaustExecutionGas (state : MachineState) : MachineState :=");
        text.AppendLine("  { state with gas := { state.gas with gasLeft := 0 } }");
        text.AppendLine();
        text.AppendLine("inductive TopicPop where");
        text.AppendLine("  | success (topics : List UInt256) (stack : Stack)");
        text.AppendLine("  | underflow (popped : List UInt256) (stack : Stack)");
        text.AppendLine("  deriving Repr");
        text.AppendLine();
        text.AppendLine("def popTopics : Nat → Stack → List UInt256 → TopicPop");
        text.AppendLine("  | 0, stack, popped => .success popped stack");
        text.AppendLine("  | count + 1, stack, popped =>");
        text.AppendLine("    match stack.pop with");
        text.AppendLine("    | none => .underflow popped stack");
        text.AppendLine("    | some (topic, tail) => popTopics count tail (popped ++ [topic])");
        text.AppendLine();
        text.AppendLine("def executeCore (schedule : Schedule) (opcode : Opcode) (isStatic : Bool)");
        text.AppendLine("    (state : MachineState) : Outcome :=");
        text.AppendLine("  let advanced := advancePc opcode state");
        text.AppendLine("  if isStatic then");
        text.AppendLine("    { status := .staticCallViolation, state := advanced }");
        text.AppendLine("  else");
        text.AppendLine("    match state.stack.popTwo with");
        text.AppendLine("    | none => { status := .stackUnderflow, state := advanced }");
        text.AppendLine("    | some (offset, length, afterHeader) =>");
        text.AppendLine("      match prepareMemory schedule state.memory offset length with");
        text.AppendLine("      | none => { status := .outOfGas, state := { advanced with stack := afterHeader } }");
        text.AppendLine("      | some (expansionCost, expandedMemory) =>");
        text.AppendLine("        let prepared := { advanced with stack := afterHeader, memory := expandedMemory }");
        text.AppendLine("        match chargeExecution expansionCost state.gas with");
        text.AppendLine("        | .error _ => { status := .outOfGas, state := exhaustExecutionGas prepared }");
        text.AppendLine("        | .ok afterExpansion =>");
        text.AppendLine("          let expandedAndCharged := { prepared with gas := afterExpansion }");
        text.AppendLine("          match chargeExecution (emissionCost schedule opcode length.val) afterExpansion with");
        text.AppendLine("          | .error _ => { status := .outOfGas, state := exhaustExecutionGas expandedAndCharged }");
        text.AppendLine("          | .ok afterEmission =>");
        text.AppendLine("            let charged := { expandedAndCharged with gas := afterEmission }");
        text.AppendLine("            let data := readRange expandedMemory.bytes offset.val length.val");
        text.AppendLine("            match popTopics (descriptor opcode).topicCount afterHeader [] with");
        text.AppendLine("            | .underflow _ partialStack =>");
        text.AppendLine("              { status := .stackUnderflow, state := { charged with stack := partialStack } }");
        text.AppendLine("            | .success topics finalStack =>");
        text.AppendLine("              let entry : LogEntry := { address := state.executingAccount, data, topics }");
        text.AppendLine("              let journaled := { charged with stack := finalStack, logs := state.logs ++ [entry] }");
        text.AppendLine("              let reported := if state.traceLogs then");
        text.AppendLine("                { journaled with reportedLogs := state.reportedLogs ++ [entry] }");
        text.AppendLine("              else journaled");
        text.AppendLine("              { status := .ok, state := reported }");
        text.AppendLine();
        text.AppendLine("def execute (schedule : Schedule) (opcode : Opcode) (isStatic : Bool)");
        text.AppendLine("    (state : MachineState) : Outcome :=");
        text.AppendLine("  if (descriptor opcode).effectOrder = expectedEffectOrder ∧");
        text.AppendLine("      (descriptor opcode).headerStackInputs = 2 then");
        text.AppendLine("    executeCore schedule opcode isStatic state");
        text.AppendLine("  else");
        text.AppendLine("    { status := .extractionMismatch, state := advancePc opcode state }");
        text.AppendLine();
    }

    private static void EmitSpecializations(StringBuilder text, IrDocument document)
    {
        text.AppendLine("def tracingFlag : DispatchTable → String");
        text.AppendLine("  | .noTrace | .noTraceCancelable => \"OffFlag\"");
        text.AppendLine("  | .traced | .tracedCancelable => \"OnFlag\"");
        text.AppendLine();
        text.AppendLine("def cancelableFlag : DispatchTable → String");
        text.AppendLine("  | .noTrace | .traced => \"OffFlag\"");
        text.AppendLine("  | .noTraceCancelable | .tracedCancelable => \"OnFlag\"");
        text.AppendLine();
        text.AppendLine("def expectedClosedRoot (opcode : Opcode) (table : DispatchTable) : String :=");
        text.AppendLine("  \"VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<\" ++ (descriptor opcode).handlerBody ++ \",\" ++");
        text.AppendLine("    tracingFlag table ++ \",\" ++ cancelableFlag table ++ \",OnFlag>\"");
        text.AppendLine();
        text.AppendLine("def specializations : List Specialization :=");
        text.AppendLine("  [");
        for (int index = 0; index < document.Specializations.Count; index++)
        {
            OpcodeSpecialization specialization = document.Specializations[index];
            text.Append("    { opcode := .").Append(specialization.Opcode)
                .Append(", table := .").Append(LeanTable(specialization.DispatchTable))
                .Append(", tracingFlag := \"").Append(Escape(specialization.TracingFlag))
                .Append("\", cancelableFlag := \"").Append(Escape(specialization.CancelableFlag))
                .Append("\", closedRoot := \"").Append(Escape(specialization.ClosedRoot)).Append("\" }");
            text.AppendLine(index + 1 == document.Specializations.Count ? string.Empty : ",");
        }
        text.AppendLine("  ]");
        text.AppendLine();
    }

    private static void EmitList(StringBuilder text, IEnumerable<string> values)
    {
        string[] items = values.ToArray();
        text.AppendLine("  [");
        for (int index = 0; index < items.Length; index++)
            text.Append("    ").Append(items[index]).AppendLine(index + 1 == items.Length ? string.Empty : ",");
        text.AppendLine("  ]");
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static void ValidateSha256(string? value, string description)
    {
        if (value is not { Length: 64 })
            throw new ExtractionException($"{description} SHA-256 must be a 64-character lowercase hexadecimal digest.");
        foreach (char character in value)
        {
            if ((character < '0' || character > '9') && (character < 'a' || character > 'f'))
                throw new ExtractionException($"{description} SHA-256 must be a 64-character lowercase hexadecimal digest.");
        }
    }

    private static string LeanTable(string table) => table switch
    {
        "NoTrace" => "noTrace",
        "NoTraceCancelable" => "noTraceCancelable",
        "Traced" => "traced",
        "TracedCancelable" => "tracedCancelable",
        _ => throw new ExtractionException($"Unknown dispatch table {table}."),
    };
}
