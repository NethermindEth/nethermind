// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.Evm.Lean.ExtendedStackOpcodeExtractor;

internal static class ExtendedStackOpcodeLeanEmitter
{
    internal static byte[] Emit(IrDocument document, string irSha256, string sourceSha256)
    {
        ExtendedStackOpcodeProfile.ValidateIr(document);
        ExtendedStackOpcodeProfile.RequireSha256(irSha256, "canonical IR hash");
        ExtendedStackOpcodeProfile.RequireSha256(sourceSha256, "production closure hash");

        StringBuilder output = new();
        output.AppendLine("-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited");
        output.AppendLine("-- SPDX-License-Identifier: LGPL-3.0-only");
        output.AppendLine();
        output.AppendLine("-- This file is generated. Do not edit.");
        output.AppendLine($"-- Extractor version: {document.ExtractorVersion}");
        output.AppendLine($"-- Production closure SHA-256: {sourceSha256}");
        output.AppendLine($"-- Canonical IR SHA-256: {irSha256}");
        output.AppendLine();
        output.AppendLine("import Eip803x.Gas");
        output.AppendLine("import Eip803x.Evm.MemoryStackControl");
        output.AppendLine("import Eip803x.Generated.ExtendedStackDecoderKernel");
        output.AppendLine();
        output.AppendLine("namespace ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel");
        output.AppendLine();
        output.AppendLine("open Eip803x");
        output.AppendLine("open Eip803x.Evm");
        output.AppendLine("open Eip803x.Evm.MemoryStackControl");
        output.AppendLine();
        output.AppendLine($"def schemaVersion : Nat := {document.SchemaVersion}");
        output.AppendLine($"def extractorVersion : String := {Quote(document.ExtractorVersion)}");
        output.AppendLine($"def kernel : String := {Quote(document.Kernel)}");
        output.AppendLine($"def root : String := {Quote(document.Root)}");
        output.AppendLine($"def gasPolicy : String := {Quote(document.GasPolicy)}");
        output.AppendLine($"def fork : String := {Quote(document.Fork)}");
        output.AppendLine($"def buildFlavor : String := {Quote(document.BuildFlavor)}");
        output.AppendLine($"def forkGate : String := {Quote(document.ForkGate)}");
        output.AppendLine($"def defaultHandler : String := {Quote(document.DefaultHandler)}");
        output.AppendLine($"def pcOrder : String := {Quote(document.PcOrder)}");
        output.AppendLine($"def gasOrder : String := {Quote(document.GasOrder)}");
        output.AppendLine($"def faultOrder : String := {Quote(document.FaultOrder)}");
        output.AppendLine($"def stackLimit : Nat := {document.StackLimit}");
        output.AppendLine($"def veryLowGas : Nat := {document.VeryLowGas}");
        output.AppendLine();
        output.AppendLine("inductive Opcode where");
        foreach (OpcodeDescriptor descriptor in document.Opcodes)
            output.AppendLine($"  | {descriptor.Name}");
        output.AppendLine("  deriving DecidableEq, Repr");
        output.AppendLine();
        output.AppendLine("def allOpcodes : List Opcode :=");
        output.AppendLine("  [" + string.Join(", ", document.Opcodes.Select(static value => "." + value.Name)) + "]");
        output.AppendLine();
        output.AppendLine("structure Descriptor where");
        output.AppendLine("  name : String");
        output.AppendLine("  instruction : String");
        output.AppendLine("  opcodeByte : Nat");
        output.AppendLine("  handlerBody : String");
        output.AppendLine("  forwarder : String");
        output.AppendLine("  instructionMethod : String");
        output.AppendLine("  decoderMethod : String");
        output.AppendLine("  stackMethod : String");
        output.AppendLine("  dispatchAssignment : String");
        output.AppendLine("  gasClass : String");
        output.AppendLine("  fixedGas : Nat");
        output.AppendLine("  stackGrowth : Nat");
        output.AppendLine("  pcOrder : String");
        output.AppendLine("  gasOrder : String");
        output.AppendLine("  faultOrder : String");
        output.AppendLine("  activation : String");
        output.AppendLine("  deriving DecidableEq, Repr");
        output.AppendLine();
        output.AppendLine("def descriptor : Opcode → Descriptor");
        foreach (OpcodeDescriptor descriptor in document.Opcodes)
        {
            output.AppendLine($"  | .{descriptor.Name} => {{");
            output.AppendLine($"      name := {Quote(descriptor.Name)}");
            output.AppendLine($"      instruction := {Quote(descriptor.Instruction)}");
            output.AppendLine($"      opcodeByte := {descriptor.OpcodeByte}");
            output.AppendLine($"      handlerBody := {Quote(descriptor.HandlerBody)}");
            output.AppendLine($"      forwarder := {Quote(descriptor.Forwarder)}");
            output.AppendLine($"      instructionMethod := {Quote(descriptor.InstructionMethod)}");
            output.AppendLine($"      decoderMethod := {Quote(descriptor.DecoderMethod)}");
            output.AppendLine($"      stackMethod := {Quote(descriptor.StackMethod)}");
            output.AppendLine($"      dispatchAssignment := {Quote(descriptor.DispatchAssignment)}");
            output.AppendLine($"      gasClass := {Quote(descriptor.GasClass)}");
            output.AppendLine($"      fixedGas := {descriptor.FixedGas}");
            output.AppendLine($"      stackGrowth := {descriptor.StackGrowth}");
            output.AppendLine($"      pcOrder := {Quote(descriptor.PcOrder)}");
            output.AppendLine($"      gasOrder := {Quote(descriptor.GasOrder)}");
            output.AppendLine($"      faultOrder := {Quote(descriptor.FaultOrder)}");
            output.AppendLine($"      activation := {Quote(descriptor.Activation)} }}");
        }
        output.AppendLine();
        output.AppendLine("def descriptorText (opcode : Opcode) : List String :=");
        output.AppendLine("  let d := descriptor opcode");
        output.AppendLine("  [d.name, d.instruction, d.handlerBody, d.forwarder, d.instructionMethod,");
        output.AppendLine("   d.decoderMethod, d.stackMethod, d.dispatchAssignment, d.gasClass, d.pcOrder,");
        output.AppendLine("   d.gasOrder, d.faultOrder, d.activation]");
        output.AppendLine("def descriptorNumbers (opcode : Opcode) : List Nat :=");
        output.AppendLine("  let d := descriptor opcode");
        output.AppendLine("  [d.opcodeByte, d.fixedGas, d.stackGrowth]");
        output.AppendLine();
        output.AppendLine("inductive DispatchTable where");
        output.AppendLine("  | noTrace | noTraceCancelable | traced | tracedCancelable");
        output.AppendLine("  deriving DecidableEq, Repr");
        output.AppendLine();
        output.AppendLine("structure Specialization where");
        output.AppendLine("  opcode : Opcode");
        output.AppendLine("  dispatchTable : DispatchTable");
        output.AppendLine("  tracingFlag : String");
        output.AppendLine("  cancelableFlag : String");
        output.AppendLine("  continuableFlag : String");
        output.AppendLine("  enabledRoot : String");
        output.AppendLine("  disabledRoot : String");
        output.AppendLine("  deriving DecidableEq, Repr");
        output.AppendLine();
        output.AppendLine("def dispatchTables : List (String × String × String) :=");
        output.AppendLine("  [");
        for (int index = 0; index < document.DispatchTables.Count; index++)
        {
            DispatchTableBinding table = document.DispatchTables[index];
            output.AppendLine($"    ({Quote(table.Name)}, {Quote(table.TracingFlag)}, {Quote(table.CancelableFlag)})" +
                (index + 1 == document.DispatchTables.Count ? string.Empty : ","));
        }
        output.AppendLine("  ]");
        output.AppendLine();
        output.AppendLine("def specializations : List Specialization :=");
        output.AppendLine("  [");
        for (int index = 0; index < document.Specializations.Count; index++)
        {
            OpcodeSpecialization specialization = document.Specializations[index];
            output.AppendLine($"    {{ opcode := .{specialization.Opcode}, " +
                $"dispatchTable := .{DispatchConstructor(specialization.DispatchTable)}, " +
                $"tracingFlag := {Quote(specialization.TracingFlag)}, " +
                $"cancelableFlag := {Quote(specialization.CancelableFlag)}, " +
                $"continuableFlag := {Quote(specialization.ContinuableFlag)}, " +
                $"enabledRoot := {Quote(specialization.EnabledRoot)}, " +
                $"disabledRoot := {Quote(specialization.DisabledRoot)} }}" +
                (index + 1 == document.Specializations.Count ? string.Empty : ","));
        }
        output.AppendLine("  ]");
        output.AppendLine("def exactRootCount : Nat := specializations.length");
        output.AppendLine();
        output.AppendLine("def forkLineage : List String :=");
        output.AppendLine("  [" + string.Join(", ", document.ForkLineage.Select(Quote)) + "]");
        output.AppendLine("def openExtractionObligations : List String :=");
        output.AppendLine("  [");
        for (int index = 0; index < document.OpenExtractionObligations.Count; index++)
            output.AppendLine($"    {Quote(document.OpenExtractionObligations[index])}" +
                (index + 1 == document.OpenExtractionObligations.Count ? string.Empty : ","));
        output.AppendLine("  ]");
        output.AppendLine();
        output.AppendLine("abbrev Byte := MemoryStackControl.Byte");
        output.AppendLine("abbrev OperandStack := MemoryStackControl.Stack");
        output.AppendLine();
        output.AppendLine("inductive DecodedOperation where");
        output.AppendLine("  | duplicate (depth : Nat)");
        output.AppendLine("  | swap (depth : Nat)");
        output.AppendLine("  | exchange (first second : Nat)");
        output.AppendLine("  deriving DecidableEq, Repr");
        output.AppendLine();
        output.AppendLine("inductive Error where");
        output.AppendLine("  | outOfGas | badInstruction | stackUnderflow | stackOverflow");
        output.AppendLine("  deriving DecidableEq, Repr");
        output.AppendLine();
        output.AppendLine("structure State where");
        output.AppendLine("  code : List Byte");
        output.AppendLine("  stack : OperandStack");
        output.AppendLine("  gas : GasState");
        output.AppendLine("  pc : Nat");
        output.AppendLine("  deriving Repr");
        output.AppendLine();
        output.AppendLine("inductive Outcome where");
        output.AppendLine("  | success (state : State)");
        output.AppendLine("  | error (reason : Error) (state : State)");
        output.AppendLine("  deriving Repr");
        output.AppendLine();
        output.AppendLine("namespace Outcome");
        output.AppendLine("def state : Outcome → State");
        output.AppendLine("  | .success state => state");
        output.AppendLine("  | .error _ state => state");
        output.AppendLine("def error? : Outcome → Option Error");
        output.AppendLine("  | .success _ => none");
        output.AppendLine("  | .error reason _ => some reason");
        output.AppendLine("end Outcome");
        output.AppendLine();
        output.AppendLine("namespace State");
        output.AppendLine("def advancePc (state : State) : State := { state with pc := state.pc + 1 }");
        output.AppendLine("def withGasLeft (state : State) (gasLeft : Nat) : State :=");
        output.AppendLine("  { state with gas := { state.gas with gasLeft } }");
        output.AppendLine("end State");
        output.AppendLine();
        output.AppendLine("structure Plan where");
        output.AppendLine("  fixedGas : Nat");
        output.AppendLine("  opcodePcFirst : Bool");
        output.AppendLine("  validImmediatePcBeforeStack : Bool");
        output.AppendLine("  gasBeforeDecode : Bool");
        output.AppendLine("  gasExhaustsOnFailure : Bool");
        output.AppendLine("  deriving DecidableEq, Repr");
        output.AppendLine();
        output.AppendLine("inductive Debit where");
        output.AppendLine("  | paid (state : State) | outOfGas (state : State)");
        output.AppendLine("  deriving Repr");
        output.AppendLine();
        output.AppendLine("def debit (plan : Plan) (state : State) : Debit :=");
        output.AppendLine("  if plan.fixedGas ≤ state.gas.gasLeft then");
        output.AppendLine("    .paid (state.withGasLeft (state.gas.gasLeft - plan.fixedGas))");
        output.AppendLine("  else");
        output.AppendLine("    .outOfGas (if plan.gasExhaustsOnFailure then state.withGasLeft 0 else state)");
        output.AppendLine();
        output.AppendLine("def readAt? {α : Type} : Nat → List α → Option α");
        output.AppendLine("  | _, [] => none");
        output.AppendLine("  | 0, head :: _ => some head");
        output.AppendLine("  | index + 1, _ :: tail => readAt? index tail");
        output.AppendLine();
        output.AppendLine("def replaceAt {α : Type} : Nat → α → List α → Option (List α)");
        output.AppendLine("  | _, _, [] => none");
        output.AppendLine("  | 0, value, _ :: tail => some (value :: tail)");
        output.AppendLine("  | index + 1, value, head :: tail =>");
        output.AppendLine("    (replaceAt index value tail).map (head :: ·)");
        output.AppendLine();
        output.AppendLine("def immediateOrZero (code : List Byte) (pc : Nat) : Byte :=");
        output.AppendLine("  (readAt? pc code).getD MemoryStackControl.zeroByte");
        output.AppendLine();
        output.AppendLine("def decodeSingle? (immediate : Byte) : Option Nat :=");
        output.AppendLine("  let decoded := Eip803x.Generated.ExtendedStackDecoderKernel.decodeSingle immediate.val");
        output.AppendLine("  if decoded.isValid then some decoded.depth else none");
        output.AppendLine();
        output.AppendLine("def decodePair? (immediate : Byte) : Option (Nat × Nat) :=");
        output.AppendLine("  let decoded := Eip803x.Generated.ExtendedStackDecoderKernel.decodePair immediate.val");
        output.AppendLine("  if decoded.isValid then");
        output.AppendLine("    some (decoded.firstPosition - 1, decoded.secondPosition - 1)");
        output.AppendLine("  else none");
        output.AppendLine();
        output.AppendLine("def decodeOperation (opcode : Opcode) (immediate : Byte) : Except Error DecodedOperation :=");
        output.AppendLine("  match opcode with");
        output.AppendLine("  | .dupN =>");
        output.AppendLine("    match decodeSingle? immediate with");
        output.AppendLine("    | some depth => .ok (.duplicate depth)");
        output.AppendLine("    | none => .error .badInstruction");
        output.AppendLine("  | .swapN =>");
        output.AppendLine("    match decodeSingle? immediate with");
        output.AppendLine("    | some depth => .ok (.swap depth)");
        output.AppendLine("    | none => .error .badInstruction");
        output.AppendLine("  | .exchange =>");
        output.AppendLine("    match decodePair? immediate with");
        output.AppendLine("    | some pair => .ok (.exchange pair.1 pair.2)");
        output.AppendLine("    | none => .error .badInstruction");
        output.AppendLine();
        output.AppendLine("def duplicateWords (depth : Nat) (words : List UInt256) : Except Error (List UInt256) :=");
        output.AppendLine("  if 0 < depth ∧ depth ≤ words.length then");
        output.AppendLine("    match readAt? (depth - 1) words with");
        output.AppendLine("    | none => .error .stackUnderflow");
        output.AppendLine("    | some value =>");
        output.AppendLine("      if words.length < stackLimit then .ok (value :: words) else .error .stackOverflow");
        output.AppendLine("  else .error .stackUnderflow");
        output.AppendLine();
        output.AppendLine("def swapWords (depth : Nat) (words : List UInt256) : Except Error (List UInt256) :=");
        output.AppendLine("  match depth, words with");
        output.AppendLine("  | 0, _ => .error .stackUnderflow");
        output.AppendLine("  | _, [] => .error .stackUnderflow");
        output.AppendLine("  | depth, top :: tail =>");
        output.AppendLine("    match readAt? (depth - 1) tail with");
        output.AppendLine("    | none => .error .stackUnderflow");
        output.AppendLine("    | some other =>");
        output.AppendLine("      match replaceAt (depth - 1) top tail with");
        output.AppendLine("      | none => .error .stackUnderflow");
        output.AppendLine("      | some changedTail => .ok (other :: changedTail)");
        output.AppendLine();
        output.AppendLine("def exchangeWords (first second : Nat) (words : List UInt256) : Except Error (List UInt256) :=");
        output.AppendLine("  match readAt? first words, readAt? second words with");
        output.AppendLine("  | some firstValue, some secondValue =>");
        output.AppendLine("    match replaceAt first secondValue words with");
        output.AppendLine("    | none => .error .stackUnderflow");
        output.AppendLine("    | some firstReplacement =>");
        output.AppendLine("      match replaceAt second firstValue firstReplacement with");
        output.AppendLine("      | none => .error .stackUnderflow");
        output.AppendLine("      | some result => .ok result");
        output.AppendLine("  | _, _ => .error .stackUnderflow");
        output.AppendLine();
        output.AppendLine("def toStackResult : Except Error (List UInt256) → Except Error OperandStack");
        output.AppendLine("  | .error reason => .error reason");
        output.AppendLine("  | .ok words => .ok (MemoryStackControl.Stack.fromWords words)");
        output.AppendLine();
        output.AppendLine("def applyDecoded : DecodedOperation → OperandStack → Except Error OperandStack");
        output.AppendLine("  | .duplicate depth, stack => toStackResult (duplicateWords depth stack.words)");
        output.AppendLine("  | .swap depth, stack => toStackResult (swapWords depth stack.words)");
        output.AppendLine("  | .exchange first second, stack => toStackResult (exchangeWords first second stack.words)");
        output.AppendLine();
        output.AppendLine("def plan (opcode : Opcode) : Plan :=");
        output.AppendLine("  let d := descriptor opcode");
        output.AppendLine("  { fixedGas := d.fixedGas");
        output.AppendLine("    opcodePcFirst := d.pcOrder == pcOrder");
        output.AppendLine("    validImmediatePcBeforeStack := d.pcOrder == pcOrder");
        output.AppendLine("    gasBeforeDecode := d.gasOrder == gasOrder");
        output.AppendLine("    gasExhaustsOnFailure := d.faultOrder == faultOrder }");
        output.AppendLine();
        output.AppendLine("def executePlanned (opcode : Opcode) (plan : Plan) (eip8024Enabled : Bool)");
        output.AppendLine("    (state : State) : Outcome :=");
        output.AppendLine("  let entered := if plan.opcodePcFirst then state.advancePc else state");
        output.AppendLine("  if !eip8024Enabled then .error .badInstruction entered");
        output.AppendLine("  else match debit plan entered with");
        output.AppendLine("  | .outOfGas afterOutOfGas => .error .outOfGas afterOutOfGas");
        output.AppendLine("  | .paid afterGas =>");
        output.AppendLine("    let immediate := immediateOrZero afterGas.code afterGas.pc");
        output.AppendLine("    match decodeOperation opcode immediate with");
        output.AppendLine("    | .error reason => .error reason afterGas");
        output.AppendLine("    | .ok operation =>");
        output.AppendLine("      let afterImmediate :=");
        output.AppendLine("        if plan.validImmediatePcBeforeStack then afterGas.advancePc else afterGas");
        output.AppendLine("      match applyDecoded operation afterImmediate.stack with");
        output.AppendLine("      | .ok stack => .success { afterImmediate with stack }");
        output.AppendLine("      | .error reason => .error reason afterImmediate");
        output.AppendLine();
        output.AppendLine("def executeAtFork (eip8024Enabled : Bool) (opcode : Opcode) (state : State) : Outcome :=");
        output.AppendLine("  executePlanned opcode (plan opcode) eip8024Enabled state");
        output.AppendLine();
        output.AppendLine("def execute (opcode : Opcode) (state : State) : Outcome := executeAtFork true opcode state");
        output.AppendLine();
        output.AppendLine("end ExtendedStackOpcodeExtractor.Generated.ExtendedStackOpcodeKernel");

        return Encoding.UTF8.GetBytes(output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static string DispatchConstructor(string value) => value switch
    {
        "NoTrace" => "noTrace",
        "NoTraceCancelable" => "noTraceCancelable",
        "Traced" => "traced",
        "TracedCancelable" => "tracedCancelable",
        _ => throw new ExtractionException($"Unknown dispatch table {value}."),
    };

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
