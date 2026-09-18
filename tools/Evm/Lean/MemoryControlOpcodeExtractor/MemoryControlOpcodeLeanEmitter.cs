// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.Evm.Lean.MemoryControlOpcodeExtractor;

internal static class MemoryControlOpcodeLeanEmitter
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    internal static byte[] Emit(IrDocument document, string irSha256, string manifestSourceSha256)
    {
        MemoryControlOpcodeProfile.ValidateIr(document);

        StringBuilder text = new();
        text.AppendLine("-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited");
        text.AppendLine("-- SPDX-License-Identifier: LGPL-3.0-only");
        text.AppendLine();
        text.AppendLine("-- This file is generated from deserialized source-derived IR. Do not edit.");
        text.AppendLine($"-- Extractor version: {document.ExtractorVersion}");
        text.AppendLine($"-- Canonical IR SHA-256: {irSha256}");
        text.AppendLine($"-- Source closure SHA-256: {manifestSourceSha256}");
        text.AppendLine();
        text.AppendLine("import Init.Data.String.Search");
        text.AppendLine();
        text.AppendLine("namespace Eip803x.Generated.MemoryControlOpcodeKernel");
        text.AppendLine();
        EmitEnum(text, "Opcode", document.Opcodes.Select(static opcode => opcode.Name));
        EmitEnum(text, "HandlerRoot", document.Opcodes.Select(static opcode => opcode.HandlerRoot));
        EmitEnum(text, "GasClass", document.Opcodes.Select(static opcode => opcode.GasClass));
        EmitEnum(text, "DynamicGas", document.Opcodes.Select(static opcode => opcode.DynamicGas));
        EmitEnum(text, "MemoryAccess", document.Opcodes.Select(static opcode => opcode.MemoryAccess));
        EmitEnum(text, "ExitKind", document.Opcodes.Select(static opcode => opcode.ExitKind));
        EmitEnum(text, "Activation", document.Opcodes.Select(static opcode => opcode.Activation));
        text.AppendLine("inductive DispatchTable where");
        text.AppendLine("  | noTrace | noTraceCancelable | traced | tracedCancelable");
        text.AppendLine("  deriving DecidableEq, Repr");
        text.AppendLine();
        text.AppendLine("structure Descriptor where");
        text.AppendLine("  opcodeByte : Nat");
        text.AppendLine("  handlerBody : String");
        text.AppendLine("  handlerRoot : HandlerRoot");
        text.AppendLine("  gasClass : GasClass");
        text.AppendLine("  fixedGas : Nat");
        text.AppendLine("  dynamicGas : DynamicGas");
        text.AppendLine("  memoryAccess : MemoryAccess");
        text.AppendLine("  exitKind : ExitKind");
        text.AppendLine("  stackInputs : Nat");
        text.AppendLine("  stackGrowth : Nat");
        text.AppendLine("  immediateWidth : Nat");
        text.AppendLine("  activation : Activation");
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
        text.AppendLine("def allOpcodes : List Opcode :=");
        EmitList(text, document.Opcodes.Select(static opcode => "." + opcode.Name));
        text.AppendLine();
        text.AppendLine("def allDispatchTables : List DispatchTable :=");
        text.AppendLine("  [.noTrace, .noTraceCancelable, .traced, .tracedCancelable]");
        text.AppendLine();
        text.AppendLine("def descriptor : Opcode → Descriptor");
        foreach (OpcodeDescriptor opcode in document.Opcodes)
        {
            text.AppendLine($"  | .{opcode.Name} =>");
            text.AppendLine("    {");
            text.AppendLine($"      opcodeByte := {opcode.OpcodeByte}");
            text.AppendLine($"      handlerBody := \"{Escape(opcode.HandlerBody)}\"");
            text.AppendLine($"      handlerRoot := .{opcode.HandlerRoot}");
            text.AppendLine($"      gasClass := .{opcode.GasClass}");
            text.AppendLine($"      fixedGas := {opcode.FixedGas}");
            text.AppendLine($"      dynamicGas := .{opcode.DynamicGas}");
            text.AppendLine($"      memoryAccess := .{opcode.MemoryAccess}");
            text.AppendLine($"      exitKind := .{opcode.ExitKind}");
            text.AppendLine($"      stackInputs := {opcode.StackInputs}");
            text.AppendLine($"      stackGrowth := {opcode.StackGrowth}");
            text.AppendLine($"      immediateWidth := {opcode.ImmediateWidth}");
            text.AppendLine($"      activation := .{opcode.Activation}");
            text.AppendLine("    }");
        }
        text.AppendLine();
        text.AppendLine($"def copyWordGas : Nat := {document.CopyWordGas}");
        text.AppendLine();
        text.AppendLine($"def memoryLinearGas : Nat := {document.MemoryLinearGas}");
        text.AppendLine();
        text.AppendLine("def activeOnAmsterdam (_opcode : Opcode) : Bool := true");
        text.AppendLine();
        text.AppendLine("def tracingFlag : DispatchTable → String");
        text.AppendLine("  | .noTrace | .noTraceCancelable => \"OffFlag\"");
        text.AppendLine("  | .traced | .tracedCancelable => \"OnFlag\"");
        text.AppendLine();
        text.AppendLine("def cancelableFlag : DispatchTable → String");
        text.AppendLine("  | .noTrace | .traced => \"OffFlag\"");
        text.AppendLine("  | .noTraceCancelable | .tracedCancelable => \"OnFlag\"");
        text.AppendLine();
        text.AppendLine("def closedHandlerBody (opcode : Opcode) (table : DispatchTable) : String :=");
        text.AppendLine("  ((descriptor opcode).handlerBody.replace \"TTracingInst\" (tracingFlag table)).replace");
        text.AppendLine("    \"TGasPolicy\" \"EthereumGasPolicy\"");
        text.AppendLine();
        text.AppendLine("def expectedClosedRoot (opcode : Opcode) (table : DispatchTable) : String :=");
        text.AppendLine("  match (descriptor opcode).handlerRoot with");
        text.AppendLine("  | .ordinary => \"VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<\" ++ closedHandlerBody opcode table ++ \",\" ++");
        text.AppendLine("      tracingFlag table ++ \",\" ++ cancelableFlag table ++ \",OnFlag>\"");
        text.AppendLine("  | .terminating => \"VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<\" ++ closedHandlerBody opcode table ++ \",\" ++");
        text.AppendLine("      tracingFlag table ++ \",\" ++ cancelableFlag table ++ \",OffFlag>\"");
        text.AppendLine("  | .jumpIf => \"VirtualMachine<EthereumGasPolicy>.ExecuteJumpIfOpcode<\" ++ tracingFlag table ++ \",\" ++");
        text.AppendLine("      cancelableFlag table ++ \">\"");
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
        text.AppendLine("def forkLineage : List String :=");
        EmitList(text, document.ForkLineage.Select(static value => $"\"{Escape(value)}\""));
        text.AppendLine();
        text.AppendLine("end Eip803x.Generated.MemoryControlOpcodeKernel");
        return Utf8WithoutBom.GetBytes(text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static void EmitEnum(StringBuilder text, string name, IEnumerable<string> values)
    {
        string[] unique = values.Distinct(StringComparer.Ordinal).ToArray();
        text.Append("inductive ").Append(name).AppendLine(" where");
        for (int index = 0; index < unique.Length; index += 8)
            text.Append("  | ").AppendLine(string.Join(" | ", unique.Skip(index).Take(8)));
        text.AppendLine("  deriving DecidableEq, Repr");
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

    private static string LeanTable(string table) => table switch
    {
        "NoTrace" => "noTrace",
        "NoTraceCancelable" => "noTraceCancelable",
        "Traced" => "traced",
        "TracedCancelable" => "tracedCancelable",
        _ => throw new ExtractionException($"Unknown dispatch table {table}."),
    };
}
