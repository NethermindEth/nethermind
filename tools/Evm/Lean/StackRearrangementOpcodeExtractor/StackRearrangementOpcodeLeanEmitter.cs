// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.Evm.Lean.StackRearrangementOpcodeExtractor;

internal static class StackRearrangementOpcodeLeanEmitter
{
    internal static byte[] Emit(IrDocument document, string irSha256, string sourceSha256)
    {
        StackRearrangementOpcodeProfile.ValidateIr(document);
        StackRearrangementOpcodeProfile.RequireSha256(irSha256, "canonical IR hash");
        StackRearrangementOpcodeProfile.RequireSha256(sourceSha256, "production closure hash");
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
        output.AppendLine("import Eip803x.Evm.Word");
        output.AppendLine();
        output.AppendLine("namespace Eip803x.Generated.StackRearrangementOpcodeKernel");
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
        output.AppendLine($"def diClosure : String := {Quote(document.DiClosure)}");
        output.AppendLine($"def forkClosure : String := {Quote(document.ForkClosure)}");
        output.AppendLine($"def pcOrder : String := {Quote(document.PcOrder)}");
        output.AppendLine($"def gasOrder : String := {Quote(document.GasOrder)}");
        output.AppendLine($"def faultOrder : String := {Quote(document.FaultOrder)}");
        output.AppendLine($"def stackLimit : Nat := {document.StackLimit}");
        output.AppendLine($"def popGas : Nat := {document.PopGas}");
        output.AppendLine($"def rearrangementGas : Nat := {document.RearrangementGas}");
        output.AppendLine("def claimHeader : List (String × String) :=");
        output.AppendLine("  [(\"extractorVersion\", extractorVersion), (\"kernel\", kernel), (\"root\", root),");
        output.AppendLine("   (\"gasPolicy\", gasPolicy), (\"fork\", fork), (\"buildFlavor\", buildFlavor),");
        output.AppendLine("   (\"diClosure\", diClosure), (\"forkClosure\", forkClosure), (\"pcOrder\", pcOrder),");
        output.AppendLine("   (\"gasOrder\", gasOrder), (\"faultOrder\", faultOrder)]");
        output.AppendLine("def claimNumbers : List Nat := [schemaVersion, stackLimit, popGas, rearrangementGas]");
        output.AppendLine();

        output.AppendLine("inductive Opcode where");
        foreach (OpcodeDescriptor descriptor in document.Opcodes)
            output.AppendLine($"  | {descriptor.Name}");
        output.AppendLine("  deriving DecidableEq, Repr");
        output.AppendLine();

        output.AppendLine("def allOpcodes : List Opcode :=");
        output.AppendLine("  [");
        for (int index = 0; index < document.Opcodes.Count; index++)
        {
            string comma = index + 1 == document.Opcodes.Count ? "" : ",";
            output.AppendLine($"    .{document.Opcodes[index].Name}{comma}");
        }
        output.AppendLine("  ]");
        output.AppendLine();

        output.AppendLine("inductive GasClass where");
        output.AppendLine("  | base");
        output.AppendLine("  | veryLow");
        output.AppendLine("  deriving DecidableEq, Repr");
        output.AppendLine();
        output.AppendLine("inductive StackEffect where");
        output.AppendLine("  | pop");
        output.AppendLine("  | duplicate");
        output.AppendLine("  | swap");
        output.AppendLine("  deriving DecidableEq, Repr");
        output.AppendLine();

        output.AppendLine("structure Descriptor where");
        output.AppendLine("  name : String");
        output.AppendLine("  instruction : String");
        output.AppendLine("  opcodeByte : Nat");
        output.AppendLine("  handlerBody : String");
        output.AppendLine("  handlerRoot : String");
        output.AppendLine("  dispatchAssignment : String");
        output.AppendLine("  gasClass : GasClass");
        output.AppendLine("  fixedGas : Nat");
        output.AppendLine("  stackInputs : Nat");
        output.AppendLine("  stackGrowth : Nat");
        output.AppendLine("  operandDepth : Nat");
        output.AppendLine("  stackEffect : StackEffect");
        output.AppendLine("  pcOrder : String");
        output.AppendLine("  gasOrder : String");
        output.AppendLine("  faultOrder : String");
        output.AppendLine("  activation : String");
        output.AppendLine("  fork : String");
        output.AppendLine("  gasPolicy : String");
        output.AppendLine("  vmRoot : String");
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
            output.AppendLine($"      handlerRoot := {Quote(descriptor.HandlerRoot)}");
            output.AppendLine($"      dispatchAssignment := {Quote(descriptor.DispatchAssignment)}");
            output.AppendLine($"      gasClass := .{descriptor.GasClass}");
            output.AppendLine($"      fixedGas := {descriptor.FixedGas}");
            output.AppendLine($"      stackInputs := {descriptor.StackInputs}");
            output.AppendLine($"      stackGrowth := {descriptor.StackGrowth}");
            output.AppendLine($"      operandDepth := {descriptor.OperandDepth}");
            output.AppendLine($"      stackEffect := .{descriptor.StackEffect}");
            output.AppendLine($"      pcOrder := {Quote(descriptor.PcOrder)}");
            output.AppendLine($"      gasOrder := {Quote(descriptor.GasOrder)}");
            output.AppendLine($"      faultOrder := {Quote(descriptor.FaultOrder)}");
            output.AppendLine($"      activation := {Quote(descriptor.Activation)}");
            output.AppendLine($"      fork := {Quote(descriptor.Fork)}");
            output.AppendLine($"      gasPolicy := {Quote(descriptor.GasPolicy)}");
            output.AppendLine($"      vmRoot := {Quote(descriptor.VmRoot)} }}");
        }
        output.AppendLine();

        output.AppendLine("def opcodeByte (opcode : Opcode) : Nat := (descriptor opcode).opcodeByte");
        output.AppendLine("def staticGas (opcode : Opcode) : Nat := (descriptor opcode).fixedGas");
        output.AppendLine("def stackInputs (opcode : Opcode) : Nat := (descriptor opcode).stackInputs");
        output.AppendLine("def stackGrowth (opcode : Opcode) : Nat := (descriptor opcode).stackGrowth");
        output.AppendLine("def operandDepth (opcode : Opcode) : Nat := (descriptor opcode).operandDepth");
        output.AppendLine("def stackEffect (opcode : Opcode) : StackEffect := (descriptor opcode).stackEffect");
        output.AppendLine();

        output.AppendLine("def descriptorText (opcode : Opcode) : List String :=");
        output.AppendLine("  let d := descriptor opcode");
        output.AppendLine("  [d.name, d.instruction, d.handlerBody, d.handlerRoot, d.dispatchAssignment, d.pcOrder,");
        output.AppendLine("   d.gasOrder, d.faultOrder, d.activation, d.fork, d.gasPolicy, d.vmRoot]");
        output.AppendLine("def descriptorNumbers (opcode : Opcode) : List Nat :=");
        output.AppendLine("  let d := descriptor opcode");
        output.AppendLine("  [d.opcodeByte, d.fixedGas, d.stackInputs, d.stackGrowth, d.operandDepth]");
        output.AppendLine("def descriptorGasClasses : List GasClass := allOpcodes.map fun opcode => (descriptor opcode).gasClass");
        output.AppendLine("def descriptorStackEffects : List StackEffect := allOpcodes.map fun opcode => (descriptor opcode).stackEffect");
        output.AppendLine();

        output.AppendLine("inductive DispatchTable where");
        output.AppendLine("  | noTrace");
        output.AppendLine("  | noTraceCancelable");
        output.AppendLine("  | traced");
        output.AppendLine("  | tracedCancelable");
        output.AppendLine("  deriving DecidableEq, Repr");
        output.AppendLine();
        output.AppendLine("structure Specialization where");
        output.AppendLine("  opcode : Opcode");
        output.AppendLine("  dispatchTable : DispatchTable");
        output.AppendLine("  tracingFlag : String");
        output.AppendLine("  cancelableFlag : String");
        output.AppendLine("  continuableFlag : String");
        output.AppendLine("  closedRoot : String");
        output.AppendLine("  handlerRoot : String");
        output.AppendLine("  deriving DecidableEq, Repr");
        output.AppendLine();

        output.AppendLine("def dispatchTables : List (String × String × String) :=");
        output.AppendLine("  [");
        for (int index = 0; index < document.DispatchTables.Count; index++)
        {
            DispatchTableBinding table = document.DispatchTables[index];
            output.AppendLine(
                $"    ({Quote(table.Name)}, {Quote(table.TracingFlag)}, {Quote(table.CancelableFlag)})" +
                (index + 1 == document.DispatchTables.Count ? string.Empty : ","));
        }
        output.AppendLine("  ]");
        output.AppendLine();

        output.AppendLine("def specializations : List Specialization :=");
        output.AppendLine("  [");
        for (int opcodeIndex = 0; opcodeIndex < document.Opcodes.Count; opcodeIndex++)
        {
            OpcodeDescriptor opcode = document.Opcodes[opcodeIndex];
            foreach (OpcodeSpecialization specialization in document.Specializations.Where(
                         specialization => specialization.Opcode == opcode.Name))
            {
                output.AppendLine(
                    $"    {{ opcode := .{opcode.Name}, dispatchTable := .{DispatchConstructor(specialization.DispatchTable)}, " +
                    $"tracingFlag := {Quote(specialization.TracingFlag)}, cancelableFlag := {Quote(specialization.CancelableFlag)}, " +
                    $"continuableFlag := {Quote(specialization.ContinuableFlag)}, closedRoot := {Quote(specialization.ClosedRoot)}, " +
                    $"handlerRoot := {Quote(specialization.HandlerRoot)} }},");
            }
        }
        output.AppendLine("  ]");
        output.AppendLine();
        output.AppendLine("def exactRootCount : Nat := specializations.length");
        output.AppendLine("def specializationOpcodes : List Opcode := specializations.map fun item => item.opcode");
        output.AppendLine("def specializationTables : List DispatchTable := specializations.map fun item => item.dispatchTable");
        output.AppendLine("def specializationTracingFlags : List String := specializations.map fun item => item.tracingFlag");
        output.AppendLine("def specializationCancelableFlags : List String := specializations.map fun item => item.cancelableFlag");
        output.AppendLine("def specializationContinuableFlags : List String := specializations.map fun item => item.continuableFlag");
        output.AppendLine("def specializationClosedRoots : List String := specializations.map fun item => item.closedRoot");
        output.AppendLine("def specializationHandlerRoots : List String := specializations.map fun item => item.handlerRoot");
        output.AppendLine();

        output.AppendLine("def forkLineage : List String :=");
        output.AppendLine("  [");
        for (int index = 0; index < document.ForkLineage.Count; index++)
            output.AppendLine($"    {Quote(document.ForkLineage[index])}{(index + 1 == document.ForkLineage.Count ? "" : ",")}");
        output.AppendLine("  ]");
        output.AppendLine("def openExtractionObligations : List String :=");
        output.AppendLine("  [");
        for (int index = 0; index < document.OpenExtractionObligations.Count; index++)
            output.AppendLine($"    {Quote(document.OpenExtractionObligations[index])}{(index + 1 == document.OpenExtractionObligations.Count ? "" : ",")}");
        output.AppendLine("  ]");
        output.AppendLine();

        output.AppendLine("inductive Operation where");
        output.AppendLine("  | pop");
        output.AppendLine("  | dup (depth : Nat)");
        output.AppendLine("  | swap (depth : Nat)");
        output.AppendLine("  deriving DecidableEq, Repr");
        output.AppendLine();
        output.AppendLine("structure Plan where");
        output.AppendLine("  fixedGas : Nat");
        output.AppendLine("  stackInputs : Nat");
        output.AppendLine("  stackGrowth : Nat");
        output.AppendLine("  stackLimit : Nat");
        output.AppendLine("  pcBeforeGas : Bool");
        output.AppendLine("  gasExhaustsOnFailure : Bool");
        output.AppendLine("  faultsAfterGas : Bool");
        output.AppendLine("  deriving DecidableEq, Repr");
        output.AppendLine();
        output.AppendLine("abbrev OperandStack := MemoryStackControl.Stack");
        output.AppendLine();
        output.AppendLine("structure State where");
        output.AppendLine("  stack : OperandStack");
        output.AppendLine("  gas : GasState");
        output.AppendLine("  pc : Nat");
        output.AppendLine("  deriving Repr");
        output.AppendLine();
        output.AppendLine("inductive Error where");
        output.AppendLine("  | outOfGas");
        output.AppendLine("  | stackUnderflow");
        output.AppendLine("  | stackOverflow");
        output.AppendLine("  deriving DecidableEq, Repr");
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
        output.AppendLine("def withGasLeft (state : State) (gasLeft : Nat) : State :=");
        output.AppendLine("  { state with gas := { state.gas with gasLeft } }");
        output.AppendLine("def advancePc (state : State) : State :=");
        output.AppendLine("  { state with pc := state.pc + 1 }");
        output.AppendLine("end State");
        output.AppendLine();
        output.AppendLine("inductive Debit where");
        output.AppendLine("  | paid (state : State)");
        output.AppendLine("  | outOfGas (state : State)");
        output.AppendLine("  deriving Repr");
        output.AppendLine();
        output.AppendLine("def debit (plan : Plan) (state : State) : Debit :=");
        output.AppendLine("  if plan.fixedGas ≤ state.gas.gasLeft then");
        output.AppendLine("    .paid (state.withGasLeft (state.gas.gasLeft - plan.fixedGas))");
        output.AppendLine("  else");
        output.AppendLine("    .outOfGas (if plan.gasExhaustsOnFailure = true then state.withGasLeft 0 else state)");
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
        output.AppendLine("def popWords : List UInt256 → Except Error (List UInt256)");
        output.AppendLine("  | [] => .error .stackUnderflow");
        output.AppendLine("  | _ :: tail => .ok tail");
        output.AppendLine();
        output.AppendLine("def duplicateWords (depth : Nat) (stack : List UInt256) : Except Error (List UInt256) :=");
        output.AppendLine("  if 0 < depth ∧ depth ≤ stack.length then");
        output.AppendLine("    match readAt? (depth - 1) stack with");
        output.AppendLine("    | none => .error .stackUnderflow");
        output.AppendLine("    | some value =>");
        output.AppendLine("      if stack.length < stackLimit then .ok (value :: stack) else .error .stackOverflow");
        output.AppendLine("  else");
        output.AppendLine("    .error .stackUnderflow");
        output.AppendLine();
        output.AppendLine("def swapWords (depth : Nat) (stack : List UInt256) : Except Error (List UInt256) :=");
        output.AppendLine("  match depth, stack with");
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
        output.AppendLine("def toOperation : Opcode → Operation");
        output.AppendLine("  | .pop => .pop");
        foreach (OpcodeDescriptor descriptor in document.Opcodes.Where(static descriptor => descriptor.StackEffect == "duplicate"))
            output.AppendLine($"  | .{descriptor.Name} => .dup {descriptor.OperandDepth}");
        foreach (OpcodeDescriptor descriptor in document.Opcodes.Where(static descriptor => descriptor.StackEffect == "swap"))
            output.AppendLine($"  | .{descriptor.Name} => .swap {descriptor.OperandDepth}");
        output.AppendLine();

        output.AppendLine("def toStackResult : Except Error (List UInt256) → Except Error OperandStack");
        output.AppendLine("  | .error reason => .error reason");
        output.AppendLine("  | .ok words => .ok (MemoryStackControl.Stack.fromWords words)");
        output.AppendLine();
        output.AppendLine("def applyOperation : Operation → OperandStack → Except Error OperandStack");
        output.AppendLine("  | .pop, stack => toStackResult (popWords stack.words)");
        output.AppendLine("  | .dup depth, stack => toStackResult (duplicateWords depth stack.words)");
        output.AppendLine("  | .swap depth, stack => toStackResult (swapWords depth stack.words)");
        output.AppendLine();
        output.AppendLine("def executePlanned (operation : Operation) (plan : Plan) (state : State) : Outcome :=");
        output.AppendLine("  let dispatched := if plan.pcBeforeGas = true then state.advancePc else state");
        output.AppendLine("  match debit plan dispatched with");
        output.AppendLine("  | .outOfGas afterOutOfGas => .error .outOfGas afterOutOfGas");
        output.AppendLine("  | .paid afterGas =>");
        output.AppendLine("    if plan.faultsAfterGas = false then");
        output.AppendLine("      match applyOperation operation afterGas.stack with");
        output.AppendLine("      | .ok result => .success { afterGas with stack := result }");
        output.AppendLine("      | .error reason => .error reason afterGas");
        output.AppendLine("    else if afterGas.stack.words.length < plan.stackInputs then");
        output.AppendLine("      .error .stackUnderflow afterGas");
        output.AppendLine("    else if plan.stackGrowth > 0 ∧");
        output.AppendLine("        afterGas.stack.words.length + plan.stackGrowth > plan.stackLimit then");
        output.AppendLine("      .error .stackOverflow afterGas");
        output.AppendLine("    else");
        output.AppendLine("      match applyOperation operation afterGas.stack with");
        output.AppendLine("      | .ok result => .success { afterGas with stack := result }");
        output.AppendLine("      | .error reason => .error reason afterGas");
        output.AppendLine();
        output.AppendLine("def plan (opcode : Opcode) : Plan :=");
        output.AppendLine("  let d := descriptor opcode");
        output.AppendLine("  { fixedGas := d.fixedGas");
        output.AppendLine("    stackInputs := d.stackInputs");
        output.AppendLine("    stackGrowth := d.stackGrowth");
        output.AppendLine("    stackLimit := stackLimit");
        output.AppendLine("    pcBeforeGas := d.pcOrder == \"pc-before-gas\"");
        output.AppendLine("    gasExhaustsOnFailure := d.faultOrder == \"out-of-gas-before-underflow-before-overflow\"");
        output.AppendLine("    faultsAfterGas := d.gasOrder == \"gas-before-stack\" }");
        output.AppendLine();
        output.AppendLine("def execute (opcode : Opcode) (state : State) : Outcome :=");
        output.AppendLine("  executePlanned (toOperation opcode) (plan opcode) state");
        output.AppendLine();
        output.AppendLine("end Eip803x.Generated.StackRearrangementOpcodeKernel");

        return Encoding.UTF8.GetBytes(output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static string DispatchConstructor(string table) => table switch
    {
        "NoTrace" => "noTrace",
        "NoTraceCancelable" => "noTraceCancelable",
        "Traced" => "traced",
        "TracedCancelable" => "tracedCancelable",
        _ => throw new ExtractionException($"Unknown dispatch table {table}."),
    };

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
