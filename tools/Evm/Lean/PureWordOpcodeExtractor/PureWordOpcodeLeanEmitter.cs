// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text;

namespace Nethermind.Evm.Lean.PureWordOpcodeExtractor;

internal static class PureWordOpcodeLeanEmitter
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static byte[] Emit(
        IrDocument document,
        string compilerVersion,
        string sourceManifestHash,
        string irHash)
    {
        PureWordOpcodeProfile.ValidateIr(document);

        IReadOnlyList<OpcodeDescriptor> opcodes = document.Opcodes;

        StringBuilder source = new();
        source.AppendLine("-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited");
        source.AppendLine("-- SPDX-License-Identifier: LGPL-3.0-only");
        source.AppendLine();
        source.AppendLine("-- This file is generated. Do not edit.");
        source.Append("-- Extractor version: ").AppendLine(document.ExtractorVersion);
        source.Append("-- Roslyn compiler version: ").AppendLine(compilerVersion);
        source.Append("-- Production closure manifest SHA-256: ").AppendLine(sourceManifestHash);
        source.Append("-- Canonical pure-word-opcode IR SHA-256: ").AppendLine(irHash);
        source.AppendLine();
        source.AppendLine("import Lean.Elab.Tactic.Omega");
        source.AppendLine("import Eip803x.Gas");
        source.AppendLine("import Eip803x.Evm.MemoryStackControl");
        source.AppendLine("import Eip803x.Evm.Word");
        source.AppendLine();
        source.AppendLine("namespace Eip803x.Generated.PureWordOpcodeKernel");
        source.AppendLine();
        source.AppendLine("open Eip803x");
        source.AppendLine("open Eip803x.Evm");
        source.AppendLine("open Eip803x.Evm.Word");
        source.AppendLine("open Eip803x.Evm.MemoryStackControl");
        source.AppendLine();
        source.AppendLine("inductive Opcode where");
        source.Append("  | ");
        for (int index = 0; index < opcodes.Count; index++)
        {
            if (index > 0)
                source.Append(" | ");
            source.Append(opcodes[index].Name);
        }
        source.AppendLine();
        source.AppendLine("  deriving DecidableEq, Repr");
        source.AppendLine();
        source.AppendLine("def allOpcodes : List Opcode :=");
        source.Append("  [ ");
        for (int index = 0; index < opcodes.Count; index++)
        {
            if (index > 0)
                source.Append(", ");
            source.Append('.').Append(opcodes[index].Name);
        }
        source.AppendLine(" ]");
        source.AppendLine();
        source.AppendLine("def opcodeByte : Opcode → Nat");
        for (int index = 0; index < opcodes.Count; index++)
        {
            OpcodeDescriptor opcode = opcodes[index];
            source.Append("  | .").Append(opcode.Name).Append(" => ")
                .AppendLine(opcode.OpcodeByte.ToString(CultureInfo.InvariantCulture));
        }
        source.AppendLine();
        source.AppendLine("def stackInputs : Opcode → Nat");
        EmitGroupedNatMatch(source, opcodes, static opcode => opcode.StackInputs);
        source.AppendLine();
        source.AppendLine("def stackGrowth (_opcode : Opcode) : Int := 0");
        source.AppendLine();
        source.AppendLine("structure Schedule where");
        source.AppendLine("  veryLow : Nat");
        source.AppendLine("  low : Nat");
        source.AppendLine("  mid : Nat");
        source.AppendLine("  expBase : Nat");
        source.AppendLine("  expByte : Nat");
        source.AppendLine("  deriving DecidableEq, Repr");
        source.AppendLine();
        source.AppendLine("def amsterdamSchedule : Schedule :=");
        source.Append("  { veryLow := ").Append(FindGas(opcodes, "veryLow")).AppendLine();
        source.Append("    low := ").Append(FindGas(opcodes, "low")).AppendLine();
        source.Append("    mid := ").Append(FindGas(opcodes, "mid")).AppendLine();
        source.Append("    expBase := ").Append(FindGas(opcodes, "exp")).AppendLine();
        source.Append("    expByte := ").Append(document.ExpByteGas.ToString(CultureInfo.InvariantCulture)).AppendLine(" }");
        source.AppendLine();
        source.AppendLine("def staticCost (schedule : Schedule) : Opcode → Nat");
        EmitGroupedStringMatch(source, opcodes, static opcode => "schedule." + GasField(opcode.FixedGasFamily));
        source.AppendLine();
        source.AppendLine("def exponentByteLength (exponent : UInt256) : Nat :=");
        source.AppendLine("  if exponent.val = 0 then 0 else exponent.val.log2 / 8 + 1");
        source.AppendLine();
        source.AppendLine("def expDynamicCost (schedule : Schedule) (exponent : UInt256) : Nat :=");
        source.AppendLine("  schedule.expByte * exponentByteLength exponent");
        source.AppendLine();
        source.AppendLine("structure State where");
        source.AppendLine("  stack : Stack");
        source.AppendLine("  gas : GasState");
        source.AppendLine("  pc : Nat");
        source.AppendLine("  deriving Repr");
        source.AppendLine();
        source.AppendLine("namespace State");
        source.AppendLine();
        source.AppendLine("def withGasLeft (state : State) (gasLeft : Nat) : State :=");
        source.AppendLine("  { state with gas := { state.gas with gasLeft } }");
        source.AppendLine();
        source.AppendLine("def advancePc (state : State) : State :=");
        source.AppendLine("  { state with pc := state.pc + 1 }");
        source.AppendLine();
        source.AppendLine("end State");
        source.AppendLine();
        source.AppendLine("inductive Error where");
        source.AppendLine("  | outOfGas");
        source.AppendLine("  | stackUnderflow");
        source.AppendLine("  deriving DecidableEq, Repr");
        source.AppendLine();
        source.AppendLine("inductive Outcome where");
        source.AppendLine("  | success (state : State)");
        source.AppendLine("  | error (reason : Error) (state : State)");
        source.AppendLine("  deriving Repr");
        source.AppendLine();
        source.AppendLine("namespace Outcome");
        source.AppendLine();
        source.AppendLine("def state : Outcome → State");
        source.AppendLine("  | .success state => state");
        source.AppendLine("  | .error _ state => state");
        source.AppendLine();
        source.AppendLine("def error? : Outcome → Option Error");
        source.AppendLine("  | .success _ => none");
        source.AppendLine("  | .error reason _ => some reason");
        source.AppendLine();
        source.AppendLine("end Outcome");
        source.AppendLine();
        source.AppendLine("inductive Debit where");
        source.AppendLine("  | paid (state : State)");
        source.AppendLine("  | outOfGas (state : State)");
        source.AppendLine("  deriving Repr");
        source.AppendLine();
        source.AppendLine("def debitExecution (cost : Nat) (state : State) : Debit :=");
        source.AppendLine("  if cost ≤ state.gas.gasLeft then");
        source.AppendLine("    .paid (state.withGasLeft (state.gas.gasLeft - cost))");
        source.AppendLine("  else");
        source.AppendLine("    .outOfGas (state.withGasLeft 0)");
        source.AppendLine();
        source.AppendLine("def unary (operation : UInt256 → UInt256) (stack : Stack) : Option Stack :=");
        source.AppendLine("  match hWords : stack.words with");
        source.AppendLine("  | [] => none");
        source.AppendLine("  | value :: rest =>");
        source.AppendLine("    some ⟨operation value :: rest, by");
        source.AppendLine("      have hBound := stack.bounded");
        source.AppendLine("      rw [hWords] at hBound");
        source.AppendLine("      simpa only [List.length_cons] using hBound⟩");
        source.AppendLine();
        source.AppendLine("def binary (operation : UInt256 → UInt256 → UInt256) (stack : Stack) : Option Stack :=");
        source.AppendLine("  match hWords : stack.words with");
        source.AppendLine("  | top :: next :: rest =>");
        source.AppendLine("    some ⟨operation top next :: rest, by");
        source.AppendLine("      have hBound := stack.bounded");
        source.AppendLine("      rw [hWords] at hBound");
        source.AppendLine("      simp only [List.length_cons] at hBound ⊢");
        source.AppendLine("      omega⟩");
        source.AppendLine("  | _ => none");
        source.AppendLine();
        source.AppendLine("def ternary (operation : UInt256 → UInt256 → UInt256 → UInt256) (stack : Stack) : Option Stack :=");
        source.AppendLine("  match hWords : stack.words with");
        source.AppendLine("  | top :: next :: third :: rest =>");
        source.AppendLine("    some ⟨operation top next third :: rest, by");
        source.AppendLine("      have hBound := stack.bounded");
        source.AppendLine("      rw [hWords] at hBound");
        source.AppendLine("      simp only [List.length_cons] at hBound ⊢");
        source.AppendLine("      omega⟩");
        source.AppendLine("  | _ => none");
        source.AppendLine();
        source.AppendLine("def stackExecute : Opcode → Stack → Option Stack");
        for (int index = 0; index < opcodes.Count; index++)
        {
            OpcodeDescriptor opcode = opcodes[index];
            source.Append("  | .").Append(opcode.Name).Append(" => ")
                .Append(ArityFunction(opcode.StackInputs)).Append(' ')
                .AppendLine(WordFunction(opcode.WordSemantics));
        }
        source.AppendLine();
        source.AppendLine("def executeExpAfterStatic (schedule : Schedule) (state : State) : Outcome :=");
        source.AppendLine("  match hWords : state.stack.words with");
        source.AppendLine("  | base :: exponent :: tail =>");
        source.AppendLine("    let hBound : (base :: exponent :: tail).length ≤ stackLimit := by");
        source.AppendLine("      have hBound := state.stack.bounded");
        source.AppendLine("      rw [hWords] at hBound");
        source.AppendLine("      exact hBound");
        source.AppendLine("    let popped : State :=");
        source.AppendLine("      { state with");
        source.AppendLine("        stack := ⟨tail, by");
        source.AppendLine("          simp only [List.length_cons] at hBound");
        source.AppendLine("          omega⟩ }");
        source.AppendLine("    match debitExecution (expDynamicCost schedule exponent) popped with");
        source.AppendLine("    | .outOfGas afterOutOfGas => .error .outOfGas afterOutOfGas");
        source.AppendLine("    | .paid afterDynamic =>");
        source.AppendLine("      .success");
        source.AppendLine("        { afterDynamic with");
        source.AppendLine("          stack := ⟨Word.exp base exponent :: tail, by");
        source.AppendLine("            simp only [List.length_cons] at hBound ⊢");
        source.AppendLine("            omega⟩ }");
        source.AppendLine("  | _ => .error .stackUnderflow state");
        source.AppendLine();
        source.AppendLine("def execute (schedule : Schedule) (opcode : Opcode) (state : State) : Outcome :=");
        source.AppendLine("  let dispatched := state.advancePc");
        source.AppendLine("  match debitExecution (staticCost schedule opcode) dispatched with");
        source.AppendLine("  | .outOfGas afterOutOfGas => .error .outOfGas afterOutOfGas");
        source.AppendLine("  | .paid afterStatic =>");
        source.AppendLine("    match opcode with");
        source.AppendLine("    | .exp => executeExpAfterStatic schedule afterStatic");
        source.AppendLine("    | _ =>");
        source.AppendLine("      match stackExecute opcode afterStatic.stack with");
        source.AppendLine("      | none => .error .stackUnderflow afterStatic");
        source.AppendLine("      | some finalStack => .success { afterStatic with stack := finalStack }");
        source.AppendLine();
        source.AppendLine("end Eip803x.Generated.PureWordOpcodeKernel");

        return Utf8WithoutBom.GetBytes(source.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static void EmitGroupedNatMatch(
        StringBuilder source,
        IReadOnlyList<OpcodeDescriptor> opcodes,
        Func<OpcodeDescriptor, int> selector)
    {
        for (int value = 1; value <= 3; value++)
        {
            List<OpcodeDescriptor> group = [];
            for (int index = 0; index < opcodes.Count; index++)
            {
                if (selector(opcodes[index]) == value)
                    group.Add(opcodes[index]);
            }
            if (group.Count == 0)
                continue;
            EmitPattern(source, group);
            source.Append(" => ").AppendLine(value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void EmitGroupedStringMatch(
        StringBuilder source,
        IReadOnlyList<OpcodeDescriptor> opcodes,
        Func<OpcodeDescriptor, string> selector)
    {
        List<string> emitted = [];
        for (int index = 0; index < opcodes.Count; index++)
        {
            string value = selector(opcodes[index]);
            if (emitted.Contains(value, StringComparer.Ordinal))
                continue;
            emitted.Add(value);
            List<OpcodeDescriptor> group = [];
            for (int candidate = 0; candidate < opcodes.Count; candidate++)
            {
                if (selector(opcodes[candidate]) == value)
                    group.Add(opcodes[candidate]);
            }
            EmitPattern(source, group);
            source.Append(" => ").AppendLine(value);
        }
    }

    private static void EmitPattern(StringBuilder source, IReadOnlyList<OpcodeDescriptor> group)
    {
        source.Append("  | ");
        for (int index = 0; index < group.Count; index++)
        {
            if (index > 0)
                source.Append(" | ");
            source.Append('.').Append(group[index].Name);
        }
    }

    private static ulong FindGas(IReadOnlyList<OpcodeDescriptor> opcodes, string family)
    {
        for (int index = 0; index < opcodes.Count; index++)
        {
            if (opcodes[index].FixedGasFamily == family)
                return opcodes[index].FixedGas;
        }
        throw new ExtractionException($"Gas family '{family}' has no opcode representative.");
    }

    private static string GasField(string family) => family switch
    {
        "veryLow" => "veryLow",
        "low" => "low",
        "mid" => "mid",
        "exp" => "expBase",
        _ => throw new ExtractionException($"Unknown gas family '{family}'."),
    };

    private static string ArityFunction(int arity) => arity switch
    {
        1 => "unary",
        2 => "binary",
        3 => "ternary",
        _ => throw new ExtractionException($"Unsupported pure-word arity {arity}."),
    };

    private static string WordFunction(string semantics) => semantics switch
    {
        "add" => "Word.add",
        "mul" => "Word.mul",
        "sub" => "Word.sub",
        "udiv" => "Word.udiv",
        "sdiv" => "Word.sdiv",
        "umod" => "Word.umod",
        "smod" => "Word.smod",
        "addmod" => "Word.addmod",
        "mulmod" => "Word.mulmod",
        "exp" => "Word.exp",
        "signextend" => "Word.signextend",
        "unsignedLt" => "Word.unsignedLt",
        "unsignedGt" => "Word.unsignedGt",
        "signedLt" => "Word.signedLt",
        "signedGt" => "Word.signedGt",
        "equal" => "Word.equal",
        "isZero" => "Word.isZero",
        "bitwiseAnd" => "Word.bitwiseAnd",
        "bitwiseOr" => "Word.bitwiseOr",
        "bitwiseXor" => "Word.bitwiseXor",
        "bitwiseNot" => "Word.bitwiseNot",
        "byte" => "Word.byte",
        "shl" => "Word.shl",
        "shr" => "Word.shr",
        "sar" => "Word.sar",
        "clz" => "Word.clz",
        _ => throw new ExtractionException($"Unknown word semantics '{semantics}'."),
    };
}
