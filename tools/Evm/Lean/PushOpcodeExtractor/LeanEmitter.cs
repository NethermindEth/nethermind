// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;

namespace Nethermind.Evm.Lean.PushOpcodeExtractor;

internal static class LeanEmitter
{
    internal static void Validate(IrDocument document)
    {
        if (document is null || document.ExtractorVersion is null || document.Kernel is null ||
            document.Reachability is null || document.Opcodes is null || document.Specializations is null ||
            document.Push2Fusion is null || document.ForkLineage is null || document.OpenExtractionObligations is null ||
            document.Opcodes.Any(static opcode => opcode is null || opcode.Name is null || opcode.Instruction is null ||
                opcode.Handler is null || opcode.Wrapper is null || opcode.OrderedEffects is null ||
                opcode.OrderedEffects.Any(static effect => effect is null)) ||
            document.Specializations.Any(static root => root is null || root.Opcode is null || root.Table is null ||
                root.TracingFlag is null || root.CancellationFlag is null || root.ClosedRoot is null) ||
            document.ForkLineage.Any(static fork => fork is null) ||
            document.OpenExtractionObligations.Any(static obligation => obligation is null) ||
            document.Push2Fusion.Eligibility is null || document.Push2Fusion.StackLimitPrecheck is null ||
            document.Push2Fusion.TerminalRule is null || document.Push2Fusion.JumpRule is null ||
            document.Push2Fusion.JumpiRule is null || document.Push2Fusion.DestinationRule is null ||
            document.Push2Fusion.OrderedEffects is null || document.Push2Fusion.OrderedEffects.Any(static effect => effect is null))
            throw new ExtractionException("Serialized PUSH opcode IR contains null collections, entries, or fields.");

        ReachabilityDescriptor reachability = document.Reachability;
        if (reachability.Service is null || reachability.ConcreteVm is null || reachability.ClosedVm is null ||
            reachability.GasPolicy is null || reachability.Fork is null || reachability.Push0Activation is null ||
            reachability.BuildSelection is null || reachability.DispatchRoot is null)
            throw new ExtractionException("Serialized PUSH opcode reachability contains null fields.");
        if (document.SchemaVersion != PushOpcodeProfile.SchemaVersion || document.ExtractorVersion != PushOpcodeProfile.ExtractorVersion ||
            document.Kernel != PushOpcodeProfile.Kernel || document.StackLimit != 1024)
            throw new ExtractionException("Serialized PUSH opcode IR header or stack limit changed.");
        if (document.Opcodes.Length != 33 || document.Specializations.Length != 132)
            throw new ExtractionException("Serialized PUSH opcode IR must contain exactly 33 opcodes and 132 roots.");
        if (document.Opcodes.Select(static opcode => opcode.Name).Distinct(StringComparer.Ordinal).Count() != 33 ||
            document.Opcodes.Select(static opcode => opcode.Instruction).Distinct(StringComparer.Ordinal).Count() != 33 ||
            document.Opcodes.Select(static opcode => opcode.OpcodeByte).Distinct().Count() != 33 ||
            document.Opcodes.Any(static opcode => opcode.OpcodeByte is < 0 or > byte.MaxValue))
            throw new ExtractionException("Serialized PUSH opcode identities are duplicate or out of byte range.");
        if (document.Specializations.Select(static root => (root.Opcode, root.Table)).Distinct().Count() != 132 ||
            document.Specializations.Select(static root => root.ClosedRoot).Distinct(StringComparer.Ordinal).Count() != 132 ||
            document.Specializations.Any(static root => root.ClosedRoot.Contains("TGasPolicy", StringComparison.Ordinal) ||
                root.ClosedRoot.Contains("TTracingInst", StringComparison.Ordinal) ||
                root.ClosedRoot.Contains("TCancelable", StringComparison.Ordinal)))
            throw new ExtractionException("Serialized PUSH opcode roots are duplicate, incomplete, or contain unbound placeholders.");

        IrDocument expected = PushOpcodeProfile.BuildExpectedIr();
        if (JsonSerializer.Serialize(document) != JsonSerializer.Serialize(expected))
            throw new ExtractionException("Serialized PUSH opcode IR differs from the exact admitted semantic document.");
    }

    internal static byte[] Emit(IrDocument document, string irHash)
    {
        Validate(document);
        RequireSha256(irHash);
        OpcodeDescriptor push0 = document.Opcodes[0];
        OpcodeDescriptor push1 = document.Opcodes[1];
        OpcodeDescriptor push32 = document.Opcodes[^1];
        string opcodeRows = string.Join(",\n", document.Opcodes.Select(opcode =>
            $"  {{ name := {Quote(opcode.Name)}, instruction := {Quote(opcode.Instruction)}, opcodeByte := {opcode.OpcodeByte}, immediateBytes := {opcode.ImmediateBytes}, handler := {Quote(opcode.Handler)}, wrapper := {Quote(opcode.Wrapper)}, fixedGas := {opcode.FixedGas}, stackInputs := {opcode.StackInputs}, stackGrowth := {opcode.StackGrowth}, hasCheckedBodyWhenUntraced := {Bool(opcode.HasCheckedBodyWhenUntraced)}, terminalPushElisionWhenUntraced := {Bool(opcode.TerminalPushElisionWhenUntraced)}, orderedEffects := {StringList(opcode.OrderedEffects)} }}"));
        string rootRows = string.Join(",\n", document.Specializations.Select(root =>
            $"  {{ opcode := {Quote(root.Opcode)}, table := {Quote(root.Table)}, tracingFlag := {Quote(root.TracingFlag)}, cancellationFlag := {Quote(root.CancellationFlag)}, closedRoot := {Quote(root.ClosedRoot)} }}"));
        ReachabilityDescriptor reachability = document.Reachability;
        Push2FusionDescriptor fusion = document.Push2Fusion;

        string lean = $$"""
        -- Generated by PushOpcodeExtractor {{document.ExtractorVersion}}.
        -- IR SHA-256: {{irHash}}
        -- Definitions only; proofs and the independent operational reference live outside this file.

        import PushOpcodeExtractor.Specification.PushTypes

        namespace PushOpcodeExtractor.Generated.PushOpcodeKernel

        open Eip803x.Evm
        open PushOpcodeExtractor.Specification.PushTypes
        abbrev Byte := MemoryStackControl.Byte
        abbrev Stack := MemoryStackControl.Stack
        abbrev UInt256 := Eip803x.UInt256

        structure Reachability where
          service : String
          concreteVm : String
          closedVm : String
          gasPolicy : String
          fork : String
          push0Activation : String
          buildSelection : String
          dispatchRoot : String
          deriving DecidableEq, Repr

        def reachability : Reachability :=
          { service := {{Quote(reachability.Service)}}
            concreteVm := {{Quote(reachability.ConcreteVm)}}
            closedVm := {{Quote(reachability.ClosedVm)}}
            gasPolicy := {{Quote(reachability.GasPolicy)}}
            fork := {{Quote(reachability.Fork)}}
            push0Activation := {{Quote(reachability.Push0Activation)}}
            buildSelection := {{Quote(reachability.BuildSelection)}}
            dispatchRoot := {{Quote(reachability.DispatchRoot)}} }

        structure OpcodeMeta where
          name : String
          instruction : String
          opcodeByte : Nat
          immediateBytes : Nat
          handler : String
          wrapper : String
          fixedGas : Nat
          stackInputs : Nat
          stackGrowth : Nat
          hasCheckedBodyWhenUntraced : Bool
          terminalPushElisionWhenUntraced : Bool
          orderedEffects : List String
          deriving DecidableEq, Repr

        def opcodes : List OpcodeMeta :=
        [
        {{opcodeRows}}
        ]

        structure DispatchRoot where
          opcode : String
          table : String
          tracingFlag : String
          cancellationFlag : String
          closedRoot : String
          deriving DecidableEq, Repr

        def dispatchRoots : List DispatchRoot :=
        [
        {{rootRows}}
        ]

        structure FusionMeta where
          jumpOpcodeByte : Nat
          jumpiOpcodeByte : Nat
          jumpdestOpcodeByte : Nat
          jumpGas : Nat
          jumpiGas : Nat
          jumpdestGas : Nat
          eligibility : String
          stackLimitPrecheck : String
          terminalRule : String
          jumpRule : String
          jumpiRule : String
          destinationRule : String
          orderedEffects : List String
          deriving DecidableEq, Repr

        def push2Fusion : FusionMeta :=
          { jumpOpcodeByte := {{fusion.JumpOpcodeByte}}
            jumpiOpcodeByte := {{fusion.JumpiOpcodeByte}}
            jumpdestOpcodeByte := {{fusion.JumpdestOpcodeByte}}
            jumpGas := {{fusion.JumpGas}}
            jumpiGas := {{fusion.JumpiGas}}
            jumpdestGas := {{fusion.JumpdestGas}}
            eligibility := {{Quote(fusion.Eligibility)}}
            stackLimitPrecheck := {{Quote(fusion.StackLimitPrecheck)}}
            terminalRule := {{Quote(fusion.TerminalRule)}}
            jumpRule := {{Quote(fusion.JumpRule)}}
            jumpiRule := {{Quote(fusion.JumpiRule)}}
            destinationRule := {{Quote(fusion.DestinationRule)}}
            orderedEffects := {{StringList(fusion.OrderedEffects)}} }

        def forkLineage : List String := {{StringList(document.ForkLineage)}}
        def openExtractionObligations : List String := {{StringList(document.OpenExtractionObligations)}}
        def stackLimit : Nat := {{document.StackLimit}}

        def immediateWidth (raw : Nat) : Option Nat :=
          if {{push0.OpcodeByte}} ≤ raw ∧ raw ≤ {{push32.OpcodeByte}}
          then some (raw - {{push0.OpcodeByte}})
          else none

        def fixedGas (raw : Nat) : Option Nat :=
          if raw = {{push0.OpcodeByte}} then some {{push0.FixedGas}}
          else if {{push0.OpcodeByte}} < raw ∧ raw ≤ {{push32.OpcodeByte}} then some {{push1.FixedGas}}
          else none

        def finish (status : ExecStatus) (state : MachineState) : Outcome := { status, state }

        def enterOpcode (state : MachineState) : MachineState :=
          { state with pc := state.pc + 1, opcodeCount := state.opcodeCount + 1 }

        def charge? (amount : Nat) (state : MachineState) : Option MachineState :=
          if amount ≤ state.gasLeft then some { state with gasLeft := state.gasLeft - amount } else none

        def outOfGas (state : MachineState) : Outcome :=
          finish .outOfGas { state with gasLeft := 0 }

        def pushAtPc (width : Nat) (state : MachineState) : Outcome :=
          let value := MemoryStackControl.bytesToWord (MemoryStackControl.readRange state.code state.pc width)
          match state.stack.push value with
          | none => finish .stackOverflow { state with pc := state.pc + width }
          | some stack => finish .success { state with stack := stack, pc := state.pc + width }

        def executeCheckedPush (tracing : Bool) (width gas : Nat) (entered : MachineState) : Outcome :=
          match charge? gas entered with
          | none => outOfGas entered
          | some charged =>
            if charged.stack.words.length ≥ stackLimit then
              finish .stackOverflow { charged with pc := if tracing then charged.pc + width else charged.pc }
            else if !tracing && charged.pc + width ≥ charged.code.length then
              finish .success { charged with pc := charged.pc + width }
            else
              pushAtPc width charged

        def push2Destination (state : MachineState) : Nat :=
          (MemoryStackControl.bytesToWord (MemoryStackControl.readRange state.code state.pc 2)).val

        def finishFusedJump (validJumpDestination : Nat → Bool) (state : MachineState) : Outcome :=
          let target := push2Destination state
          if validJumpDestination target then
            let atDestination := { state with pc := target + 1, opcodeCount := state.opcodeCount + 1 }
            match charge? push2Fusion.jumpdestGas atDestination with
            | none => outOfGas atDestination
            | some charged => finish .success charged
          else
            finish .invalidJump state

        def executeFusedJump (validJumpDestination : Nat → Bool) (state : MachineState) : Outcome :=
          let counted := { state with opcodeCount := state.opcodeCount + 1 }
          match charge? push2Fusion.jumpGas counted with
          | none => outOfGas counted
          | some charged => finishFusedJump validJumpDestination charged

        def executeFusedJumpi (validJumpDestination : Nat → Bool) (state : MachineState) : Outcome :=
          let counted := { state with opcodeCount := state.opcodeCount + 1 }
          match charge? push2Fusion.jumpiGas counted with
          | none => outOfGas counted
          | some charged =>
            match charged.stack.pop with
            | none => finish .stackUnderflow charged
            | some (condition, stack) =>
              let popped := { charged with stack := stack }
              if condition.val = 0 then finish .success { popped with pc := popped.pc + 3 }
              else finishFusedJump validJumpDestination popped

        def executePush2 (tracing : Bool) (validJumpDestination : Nat → Bool) (entered : MachineState) : Outcome :=
          match charge? 3 entered with
          | none => outOfGas entered
          | some charged =>
            if tracing then pushAtPc 2 charged
            else if charged.stack.words.length ≥ stackLimit then
              finish .stackOverflow { charged with pc := charged.pc + 2 }
            else if charged.code.length - charged.pc ≤ 2 then
              finish .success { charged with pc := charged.pc + 2 }
            else
              let next := (MemoryStackControl.readByte charged.code (charged.pc + 2)).val
              if next = push2Fusion.jumpOpcodeByte then executeFusedJump validJumpDestination charged
              else if next = push2Fusion.jumpiOpcodeByte then executeFusedJumpi validJumpDestination charged
              else pushAtPc 2 charged

        def execute (tracing : Bool) (validJumpDestination : Nat → Bool) (state : MachineState) : Outcome :=
          let raw := (MemoryStackControl.readByte state.code state.pc).val
          match immediateWidth raw, fixedGas raw with
          | some width, some gas =>
            let entered := enterOpcode state
            if width = 2 then executePush2 tracing validJumpDestination entered
            else executeCheckedPush tracing width gas entered
          | _, _ => finish .unmodeledOpcode state

        end PushOpcodeExtractor.Generated.PushOpcodeKernel
        """ + "\n";
        return Encoding.UTF8.GetBytes(lean);
    }

    private static void RequireSha256(string value)
    {
        if (value is null || value.Length != 64 ||
            value.Any(static character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ExtractionException("Generated PUSH opcode IR fingerprint must be exactly 64 lowercase hexadecimal characters.");
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string StringList(IEnumerable<string> values) =>
        "[" + string.Join(", ", values.Select(Quote)) + "]";

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
}
