// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.Evm.Lean.Keccak256OpcodeExtractor;

internal static class Keccak256OpcodeLeanEmitter
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    internal static byte[] Emit(IrDocument document, string irSha256, string sourceSha256)
    {
        Keccak256OpcodeProfile.ValidateIr(document);
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
        text.AppendLine();
        text.AppendLine("namespace Eip803x.Generated.Keccak256OpcodeKernel");
        text.AppendLine();
        text.AppendLine("open GasMachine");
        text.AppendLine("open Eip803x.Evm.MemoryStackControl");
        text.AppendLine();
        text.AppendLine($"def sourceRoot : String := \"{Escape(document.Reachability.ClosedVm)}\"");
        text.AppendLine($"def sourceFork : String := \"{Escape(document.Reachability.Fork)}\"");
        text.AppendLine($"def hashBoundary : String := \"{Escape(document.Reachability.HashBoundary)}\"");
        text.AppendLine();
        text.AppendLine("abbrev HashOracle := List Byte -> UInt256");
        text.AppendLine();
        text.AppendLine("inductive Effect where");
        foreach (string effect in document.Opcode.EffectOrder)
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
        text.AppendLine("  handlerBody : String");
        text.AppendLine("  handlerTarget : String");
        text.AppendLine("  programCounterDelta : Nat");
        text.AppendLine("  opcodeCountDelta : Nat");
        text.AppendLine("  stackInputs : Nat");
        text.AppendLine("  stackOutputs : Nat");
        text.AppendLine("  hasCheckedBody : Bool");
        text.AppendLine("  pushDepthFlag : String");
        text.AppendLine("  effectOrder : List Effect");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("def expectedEffectOrder : List Effect :=");
        EmitList(text, document.Opcode.EffectOrder.Select(static effect => "." + effect));
        text.AppendLine();
        text.AppendLine("def descriptor : Descriptor :=");
        text.AppendLine("  {");
        text.AppendLine($"    instruction := \"{Escape(document.Opcode.Instruction)}\"");
        text.AppendLine($"    opcodeByte := {document.Opcode.OpcodeByte}");
        text.AppendLine($"    handlerBody := \"{Escape(document.Opcode.HandlerBody)}\"");
        text.AppendLine($"    handlerTarget := \"{Escape(document.Opcode.HandlerTarget)}\"");
        text.AppendLine($"    programCounterDelta := {document.Opcode.ProgramCounterDelta}");
        text.AppendLine($"    opcodeCountDelta := {document.Opcode.OpcodeCountDelta}");
        text.AppendLine($"    stackInputs := {document.Opcode.StackInputs}");
        text.AppendLine($"    stackOutputs := {document.Opcode.StackOutputs}");
        text.AppendLine($"    hasCheckedBody := {document.Opcode.HasCheckedBody.ToString().ToLowerInvariant()}");
        text.AppendLine($"    pushDepthFlag := \"{Escape(document.Opcode.PushDepthFlag)}\"");
        text.AppendLine("    effectOrder := expectedEffectOrder");
        text.AppendLine("  }");
        text.AppendLine();
        text.AppendLine("structure Schedule where");
        text.AppendLine("  base : Nat");
        text.AppendLine("  word : Nat");
        text.AppendLine("  memoryLinear : Nat");
        text.AppendLine("  memoryQuadraticDivisor : Nat");
        text.AppendLine("  memoryQuadraticDivisorPositive : 0 < memoryQuadraticDivisor");
        text.AppendLine("  maxMemorySize : Nat");
        text.AppendLine("  maxUInt64 : Nat");
        text.AppendLine("  maxUInt32 : Nat");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("def amsterdamSchedule : Schedule :=");
        text.AppendLine("  {");
        text.AppendLine($"    base := {document.Schedule.Base}");
        text.AppendLine($"    word := {document.Schedule.Word}");
        text.AppendLine($"    memoryLinear := {document.Schedule.MemoryLinear}");
        text.AppendLine($"    memoryQuadraticDivisor := {document.Schedule.MemoryQuadraticDivisor}");
        text.AppendLine("    memoryQuadraticDivisorPositive := by decide");
        text.AppendLine($"    maxMemorySize := {document.Schedule.MaxMemorySize}");
        text.AppendLine($"    maxUInt64 := {document.Schedule.MaxUInt64}");
        text.AppendLine($"    maxUInt32 := {document.Schedule.MaxUInt32}");
        text.AppendLine("  }");
        text.AppendLine();
        EmitMachine(text);
        EmitDispatch(text, document);
        text.AppendLine("def forkLineage : List String :=");
        EmitList(text, document.ForkLineage.Select(static fork => $"\"{Escape(fork)}\""));
        text.AppendLine();
        text.AppendLine("def openExtractionObligations : List String :=");
        EmitList(text, document.OpenExtractionObligations.Select(static item => $"\"{Escape(item)}\""));
        text.AppendLine();
        text.AppendLine("end Eip803x.Generated.Keccak256OpcodeKernel");
        return Utf8WithoutBom.GetBytes(text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static void EmitMachine(StringBuilder text)
    {
        text.AppendLine("inductive TraceEvent where");
        text.AppendLine("  | start (instruction : String) (pc gasLeft : Nat)");
        text.AppendLine("  | push (value : UInt256)");
        text.AppendLine("  | finish (gasLeft : Nat)");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("structure MachineState where");
        text.AppendLine("  pc : Nat");
        text.AppendLine("  opcodeCount : Nat");
        text.AppendLine("  gas : GasState");
        text.AppendLine("  stack : Stack");
        text.AppendLine("  memory : Memory");
        text.AppendLine("  trace : List TraceEvent");
        text.AppendLine("  deriving Repr");
        text.AppendLine();
        text.AppendLine("inductive Status where");
        text.AppendLine("  | ok | outOfGas | stackUnderflow | stackOverflow | extractionMismatch");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("structure Outcome where");
        text.AppendLine("  status : Status");
        text.AppendLine("  state : MachineState");
        text.AppendLine("  deriving Repr");
        text.AppendLine();
        text.AppendLine("def isTracing : DispatchTable -> Bool");
        text.AppendLine("  | .noTrace | .noTraceCancelable => false");
        text.AppendLine("  | .traced | .tracedCancelable => true");
        text.AppendLine();
        text.AppendLine("def beginInstruction (table : DispatchTable) (state : MachineState) : MachineState :=");
        text.AppendLine("  let traced := if isTracing table then");
        text.AppendLine("    { state with trace := state.trace ++ [.start descriptor.instruction state.pc state.gas.gasLeft] }");
        text.AppendLine("  else state");
        text.AppendLine("  { traced with pc := traced.pc + descriptor.programCounterDelta, opcodeCount := traced.opcodeCount + descriptor.opcodeCountDelta }");
        text.AppendLine();
        text.AppendLine("def checkedWords (schedule : Schedule) (length : UInt256) : Nat × Bool :=");
        text.AppendLine("  if length.val > schedule.maxUInt64 then");
        text.AppendLine("    (0, true)");
        text.AppendLine("  else");
        text.AppendLine("    let result := (length.val + 31) / 32");
        text.AppendLine("    if result > schedule.maxUInt32 then (0, true) else (result, false)");
        text.AppendLine();
        text.AppendLine("def dynamicCost (schedule : Schedule) (words : Nat) : Nat :=");
        text.AppendLine("  schedule.base + schedule.word * words");
        text.AppendLine();
        text.AppendLine("def memoryWords (memory : Memory) : Nat := memory.bytes.length / 32");
        text.AppendLine();
        text.AppendLine("def memoryCost (schedule : Schedule) (words : Nat) : Nat :=");
        text.AppendLine("  words * schedule.memoryLinear +");
        text.AppendLine("    words * words / schedule.memoryQuadraticDivisor");
        text.AppendLine();
        text.AppendLine("def memoryRangeValid (schedule : Schedule) (offset length : UInt256) : Bool :=");
        text.AppendLine("  length.val = 0 ||");
        text.AppendLine("    (length.val <= schedule.maxMemorySize && offset.val <= schedule.maxMemorySize - length.val)");
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
        text.AppendLine("def exhaustExecutionGas (state : MachineState) : MachineState :=");
        text.AppendLine("  { state with gas := { state.gas with gasLeft := 0 } }");
        text.AppendLine();
        text.AppendLine("def tracePush (table : DispatchTable) (value : UInt256) (state : MachineState) : MachineState :=");
        text.AppendLine("  if isTracing table then { state with trace := state.trace ++ [.push value] } else state");
        text.AppendLine();
        text.AppendLine("def finishInstruction (table : DispatchTable) (state : MachineState) : MachineState :=");
        text.AppendLine("  if isTracing table then { state with trace := state.trace ++ [.finish state.gas.gasLeft] } else state");
        text.AppendLine();
        text.AppendLine("def executeCore (schedule : Schedule) (hash : HashOracle) (table : DispatchTable)");
        text.AppendLine("    (state : MachineState) : Outcome :=");
        text.AppendLine("  let entered := beginInstruction table state");
        text.AppendLine("  match state.stack.popTwo with");
        text.AppendLine("  | none => { status := .stackUnderflow, state := entered }");
        text.AppendLine("  | some (offset, length, tail) =>");
        text.AppendLine("    let (wordCount, invalidWordCount) := checkedWords schedule length");
        text.AppendLine("    match chargeExecution (dynamicCost schedule wordCount) state.gas with");
        text.AppendLine("    | .error _ =>");
        text.AppendLine("      { status := .outOfGas, state := exhaustExecutionGas { entered with stack := tail } }");
        text.AppendLine("    | .ok afterDynamic =>");
        text.AppendLine("      let dynamicallyCharged := { entered with gas := afterDynamic, stack := tail }");
        text.AppendLine("      if invalidWordCount then");
        text.AppendLine("        { status := .outOfGas, state := dynamicallyCharged }");
        text.AppendLine("      else");
        text.AppendLine("        match prepareMemory schedule state.memory offset length with");
        text.AppendLine("        | none => { status := .outOfGas, state := dynamicallyCharged }");
        text.AppendLine("        | some (expansionCost, expanded) =>");
        text.AppendLine("          let prepared := { dynamicallyCharged with memory := expanded }");
        text.AppendLine("          match chargeExecution expansionCost afterDynamic with");
        text.AppendLine("          | .error _ => { status := .outOfGas, state := exhaustExecutionGas prepared }");
        text.AppendLine("          | .ok afterExpansion =>");
        text.AppendLine("            let input := readRange expanded.bytes offset.val length.val");
        text.AppendLine("            let value := hash input");
        text.AppendLine("            match tail.push value with");
        text.AppendLine("            | none => { status := .stackOverflow, state := { prepared with gas := afterExpansion } }");
        text.AppendLine("            | some finalStack =>");
        text.AppendLine("              let pushed := tracePush table value { prepared with gas := afterExpansion, stack := finalStack }");
        text.AppendLine("              { status := .ok, state := finishInstruction table pushed }");
        text.AppendLine();
    }

    private static void EmitDispatch(StringBuilder text, IrDocument document)
    {
        text.AppendLine("structure Specialization where");
        text.AppendLine("  opcode : String");
        text.AppendLine("  table : DispatchTable");
        text.AppendLine("  tracingFlag : String");
        text.AppendLine("  cancellationFlag : String");
        text.AppendLine("  closedRoot : String");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("def tracingFlag : DispatchTable -> String");
        text.AppendLine("  | .noTrace | .noTraceCancelable => \"OffFlag\"");
        text.AppendLine("  | .traced | .tracedCancelable => \"OnFlag\"");
        text.AppendLine();
        text.AppendLine("def cancellationFlag : DispatchTable -> String");
        text.AppendLine("  | .noTrace | .traced => \"OffFlag\"");
        text.AppendLine("  | .noTraceCancelable | .tracedCancelable => \"OnFlag\"");
        text.AppendLine();
        text.AppendLine("def expectedClosedRoot (table : DispatchTable) : String :=");
        text.AppendLine("  sourceRoot ++ \".ExecuteOpcode<KeccakOpcode<\" ++ tracingFlag table ++ \">,\" ++");
        text.AppendLine("    tracingFlag table ++ \",\" ++ cancellationFlag table ++ \",OnFlag>\"");
        text.AppendLine();
        text.AppendLine("def specializations : List Specialization :=");
        text.AppendLine("  [");
        for (int index = 0; index < document.Specializations.Length; index++)
        {
            OpcodeSpecialization specialization = document.Specializations[index];
            text.Append("    { opcode := \"").Append(Escape(specialization.Opcode))
                .Append("\", table := .").Append(LeanTable(specialization.DispatchTable))
                .Append(", tracingFlag := \"").Append(Escape(specialization.TracingFlag))
                .Append("\", cancellationFlag := \"").Append(Escape(specialization.CancellationFlag))
                .Append("\", closedRoot := \"").Append(Escape(specialization.ClosedRoot)).Append("\" }");
            text.AppendLine(index + 1 == document.Specializations.Length ? string.Empty : ",");
        }
        text.AppendLine("  ]");
        text.AppendLine();
        text.AppendLine("def descriptorAdmitted : Bool :=");
        text.AppendLine("  descriptor.instruction = \"KECCAK256\" && descriptor.opcodeByte = 32 &&");
        text.AppendLine("    descriptor.handlerBody = \"KeccakOpcode<TTracingInst>\" &&");
        text.AppendLine("    descriptor.handlerTarget = \"EvmInstructions.InstructionKeccak256<TGasPolicy,TTracingInst>\" &&");
        text.AppendLine("    descriptor.programCounterDelta = 1 && descriptor.opcodeCountDelta = 1 &&");
        text.AppendLine("    descriptor.stackInputs = 2 && descriptor.stackOutputs = 1 &&");
        text.AppendLine("    descriptor.hasCheckedBody = false && descriptor.pushDepthFlag = \"OnFlag\" &&");
        text.AppendLine("    descriptor.effectOrder = expectedEffectOrder");
        text.AppendLine();
        text.AppendLine("def specializationAdmitted (table : DispatchTable) : Bool :=");
        text.AppendLine("  specializations.any fun item => item.opcode = \"keccak256\" && item.table = table &&");
        text.AppendLine("    item.tracingFlag = tracingFlag table && item.cancellationFlag = cancellationFlag table &&");
        text.AppendLine("    item.closedRoot = expectedClosedRoot table");
        text.AppendLine();
        text.AppendLine("def execute (schedule : Schedule) (hash : HashOracle) (table : DispatchTable)");
        text.AppendLine("    (state : MachineState) : Outcome :=");
        text.AppendLine("  if descriptorAdmitted && specializationAdmitted table then");
        text.AppendLine("    executeCore schedule hash table state");
        text.AppendLine("  else");
        text.AppendLine("    { status := .extractionMismatch, state := beginInstruction table state }");
        text.AppendLine();
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
        _ => throw new ExtractionException($"Unknown dispatch table {table}."),
    };

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
}
