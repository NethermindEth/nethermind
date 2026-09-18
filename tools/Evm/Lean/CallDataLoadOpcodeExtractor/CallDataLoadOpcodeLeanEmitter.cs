// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.Evm.Lean.CallDataLoadOpcodeExtractor;

internal static class CallDataLoadOpcodeLeanEmitter
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    internal static byte[] Emit(IrDocument document, string irSha256, string sourceSha256)
    {
        CallDataLoadOpcodeProfile.ValidateIr(document);
        return EmitFromSemanticIr(document, irSha256, sourceSha256);
    }

    internal static byte[] EmitFromSemanticIr(IrDocument document, string irSha256, string sourceSha256)
    {
        ValidateSemanticIr(document);
        ValidateSha256(irSha256, "Canonical IR");
        ValidateSha256(sourceSha256, "Source closure");

        OpcodeDescriptor opcode = document.Opcodes.Single();
        string roots = LeanList(document.Specializations.Select(static root => root.ClosedRoot));
        string effects = LeanList(opcode.EffectOrder);
        string obligations = LeanList(document.OpenExtractionObligations);
        string lineage = LeanList(document.ForkLineage);
        string clearOutOfGas = document.OuterFailure.ClearGasOnOutOfGas
            ? "if outcome.status = .outOfGas then { outcome.state with gasLeft := 0 } else outcome.state"
            : "outcome.state";
        string failureEvents = document.OuterFailure.FinishBeforeError
            ? "[.finish state.gasLeft, .error outcome.status]"
            : "[.error outcome.status, .finish state.gasLeft]";
        string faultPc = document.OuterFailure.DecrementFaultPc
            ? "if outcome.status = .ok then outcome.state.pc else outcome.state.pc - 1"
            : "outcome.state.pc";
        string underflowState = document.OuterFailure.RetainChargedGasOnUnderflow ? "charged" : "entered";
        string stackTraceWords = opcode.Semantics.StackTraceOrder switch
        {
            "bottomFirst" => "state.stack.words.reverse",
            "topFirst" => "state.stack.words",
            _ => throw new ExtractionException("Unsupported CALLDATALOAD instruction-start stack trace order."),
        };

        string lean = $$"""
        -- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
        -- SPDX-License-Identifier: LGPL-3.0-only

        -- Generated from round-tripped, source-admitted semantic IR. Definitions only; do not edit.
        -- The imported operational module supplies representation types, not this executable body.
        -- Extractor version: {{document.ExtractorVersion}}
        -- Canonical IR SHA-256: {{irSha256}}
        -- Source closure SHA-256: {{sourceSha256}}

        import CallDataLoadOpcodeExtractor.Specification.CallDataLoadExecution

        namespace Eip803x.Generated.CallDataLoadOpcodeKernel

        open Eip803x.Evm.MemoryStackControl
        open Eip803x.Evm.MemoryStackControl.Stack

        abbrev Opcode := Eip803x.Evm.CallDataLoadExecution.Opcode
        abbrev DispatchTable := Eip803x.Evm.CallDataLoadExecution.DispatchTable
        abbrev Schedule := Eip803x.Evm.CallDataLoadExecution.Schedule
        abbrev Status := Eip803x.Evm.CallDataLoadExecution.Status
        abbrev TraceEvent := Eip803x.Evm.CallDataLoadExecution.TraceEvent
        abbrev MachineState := Eip803x.Evm.CallDataLoadExecution.MachineState
        abbrev Outcome := Eip803x.Evm.CallDataLoadExecution.Outcome

        def sourceRoot : String := "{{Escape(document.ClosedVm)}}"
        def sourceFork : String := "{{Escape(document.Fork)}}"
        def sourceGasPolicy : String := "{{Escape(document.GasPolicy)}}"
        def sourceBuild : String := "{{Escape(document.StandardBuildSelector)}}"

        structure OpcodeDescriptor where
          name : String
          instruction : String
          opcodeByte : Nat
          handlerBody : String
          handlerTarget : String
          stackInputs : Nat
          stackOutputs : Nat
          stackGrowth : Int
          pushSize : Int
          hasCheckedBody : Bool
          usesVm : Bool
          replacesTop : Bool
          endsInstructionTrace : Bool
          activation : String
          operation : String
          gasClass : String
          accessWidth : Nat
          offsetRule : String
          paddingRule : String
          stackTraceOrder : String
          traceRule : String
          effectOrder : List String
          deriving DecidableEq, Repr

        def descriptor : OpcodeDescriptor :=
          { name := "{{Escape(opcode.Name)}}"
            instruction := "{{Escape(opcode.Instruction)}}"
            opcodeByte := {{opcode.OpcodeByte}}
            handlerBody := "{{Escape(opcode.HandlerBody)}}"
            handlerTarget := "{{Escape(opcode.HandlerTarget)}}"
            stackInputs := {{opcode.StackInputs}}
            stackOutputs := {{opcode.StackOutputs}}
            stackGrowth := {{opcode.StackGrowth}}
            pushSize := {{opcode.PushSize}}
            hasCheckedBody := {{LeanBool(opcode.HasCheckedBody)}}
            usesVm := {{LeanBool(opcode.UsesVm)}}
            replacesTop := {{LeanBool(opcode.ReplacesTop)}}
            endsInstructionTrace := {{LeanBool(opcode.EndsInstructionTrace)}}
            activation := "{{Escape(opcode.Activation)}}"
            operation := "{{Escape(opcode.Semantics.Operation)}}"
            gasClass := "{{Escape(opcode.Semantics.GasClass)}}"
            accessWidth := {{opcode.Semantics.AccessWidth}}
            offsetRule := "{{Escape(opcode.Semantics.OffsetRule)}}"
            paddingRule := "{{Escape(opcode.Semantics.PaddingRule)}}"
            stackTraceOrder := "{{Escape(opcode.Semantics.StackTraceOrder)}}"
            traceRule := "{{Escape(opcode.Semantics.TraceRule)}}"
            effectOrder := {{effects}} }

        def dispatchRoots : List String := {{roots}}
        def forkLineage : List String := {{lineage}}
        def openExtractionObligations : List String := {{obligations}}

        def amsterdamSchedule : Schedule :=
          { veryLow := {{document.Schedule.VeryLow}}
            wordBytes := {{opcode.Semantics.AccessWidth}}
            maxInt32 := {{document.Schedule.MaxInt32}}
            maxUInt64 := {{document.Schedule.MaxUInt64}}
            uint256Modulus := {{document.Schedule.UInt256Modulus}} }

        def tableTracing : DispatchTable → Bool
          | .noTrace | .noTraceCancelable => false
          | .traced | .tracedCancelable => true

        def tableCancelable : DispatchTable → Bool
          | .noTrace | .traced => false
          | .noTraceCancelable | .tracedCancelable => true

        def dispatchEnter (table : DispatchTable) (state : MachineState) : MachineState :=
          let traced := if tableTracing table then
            let started := { state with trace := state.trace ++ [.start .calldataload state.pc state.gasLeft] }
            let memory := if state.traceCapabilities.memory then
              { started with trace := started.trace ++
                  [.operationMemory state.memory, .operationMemorySize state.memory.length] }
            else started
            let stack := if state.traceCapabilities.stack then
              { memory with trace := memory.trace ++ [.operationStack {{stackTraceWords}}] }
            else memory
            if state.traceCapabilities.returnData then
              { stack with trace := stack.trace ++ [.operationReturnData state.returnData] }
            else stack
          else state
          { traced with pc := traced.pc + 1, opcodeCount := traced.opcodeCount + 1 }

        def replaceTop (stack : Stack) (value : UInt256) : Stack :=
          match stack with
          | ⟨[], bounded⟩ => ⟨[], bounded⟩
          | ⟨_ :: tail, bounded⟩ => ⟨value :: tail, bounded⟩

        def zeroWordBytes (schedule : Schedule) : List Byte :=
          List.replicate schedule.wordBytes zeroByte

        def accessibleOffset (schedule : Schedule) (calldata : List Byte) (offset : UInt256) : Bool :=
          offset.val ≤ schedule.maxUInt64 && offset.val < calldata.length

        def productionDomain (schedule : Schedule) (state : MachineState) : Prop :=
          state.calldata.length ≤ schedule.maxInt32 ∧ state.gasLeft ≤ schedule.maxUInt64 ∧
            state.pc < schedule.maxInt32 ∧ state.opcodeCount < schedule.maxInt32

        def readCallDataWordBytes (schedule : Schedule) (calldata : List Byte)
            (offset : UInt256) : List Byte :=
          if accessibleOffset schedule calldata offset then
            readRange calldata offset.val schedule.wordBytes
          else zeroWordBytes schedule

        def tracedPushPayload (schedule : Schedule) (calldata : List Byte)
            (offset : UInt256) : List Byte :=
          if accessibleOffset schedule calldata offset then
            readCallDataWordBytes schedule calldata offset
          else [zeroByte]

        def reportPush (table : DispatchTable) (payload : List Byte)
            (state : MachineState) : MachineState :=
          if tableTracing table then { state with trace := state.trace ++ [.stackPush payload] } else state

        def dispatchFinish (table : DispatchTable) (state : MachineState) : MachineState :=
          if tableTracing table then { state with trace := state.trace ++ [.finish state.gasLeft] } else state

        def executeCheckedBody (schedule : Schedule) (table : DispatchTable)
            (entered : MachineState) : Outcome :=
          if schedule.veryLow ≤ entered.gasLeft then
            let charged := { entered with gasLeft := entered.gasLeft - schedule.veryLow }
            match charged.stack.words with
            | [] => ⟨.stackUnderflow, {{underflowState}}⟩
            | offset :: _ =>
              let bytes := readCallDataWordBytes schedule charged.calldata offset
              let replaced := { charged with stack := replaceTop charged.stack (bytesToWord bytes) }
              let traced := reportPush table (tracedPushPayload schedule charged.calldata offset) replaced
              ⟨.ok, dispatchFinish table traced⟩
          else
            ⟨.outOfGas, { entered with gasLeft := 0 }⟩

        def executeSemantic (schedule : Schedule) (table : DispatchTable)
            (state : MachineState) : Outcome :=
          executeCheckedBody schedule table (dispatchEnter table state)

        def executeAmsterdam (table : DispatchTable) (state : MachineState) : Outcome :=
          executeSemantic amsterdamSchedule table state

        def closeFailureTrace (table : DispatchTable) (outcome : Outcome) : Outcome :=
          if outcome.status = .ok || outcome.status = .extractionMismatch then outcome
          else
            let state := {{clearOutOfGas}}
            if tableTracing table then
              { outcome with state := { state with trace := state.trace ++ {{failureEvents}} } }
            else { outcome with state := state }

        def executeAmsterdamClosed (table : DispatchTable) (state : MachineState) : Outcome :=
          closeFailureTrace table (executeAmsterdam table state)

        def faultPc (outcome : Outcome) : Nat :=
          {{faultPc}}

        end Eip803x.Generated.CallDataLoadOpcodeKernel
        """;

        return Utf8WithoutBom.GetBytes(lean.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    }

    private static void ValidateSemanticIr(IrDocument document)
    {
        if (document.Schedule is null || document.OuterFailure is null || document.DispatchTables is null ||
            document.Opcodes is null || document.Specializations is null || document.ForkLineage is null ||
            document.OpenExtractionObligations is null || document.DispatchTables.Any(static item => item is null) ||
            document.Opcodes.Any(static item => item is null || item.Semantics is null || item.EffectOrder is null) ||
            document.Specializations.Any(static item => item is null))
            throw new ExtractionException("Cannot emit null calldata-load semantic IR fields.");
        if (document.Opcodes.Length != 1 || document.DispatchTables.Length != 4 || document.Specializations.Length != 4)
            throw new ExtractionException("The calldata-load emitter requires one opcode and four closed roots.");
        if (document.Opcodes[0].Name != "calldataload" || document.Opcodes[0].Semantics.Operation != "loadCallDataWord")
            throw new ExtractionException("Unsupported calldata-load semantic operation.");
        if (document.Opcodes[0].Semantics.AccessWidth != document.Schedule.WordBytes)
            throw new ExtractionException("CALLDATALOAD semantic access width and pinned word width disagree.");
        if (document.Opcodes[0].Semantics.StackTraceOrder is not ("bottomFirst" or "topFirst"))
            throw new ExtractionException("Unsupported CALLDATALOAD instruction-start stack trace order.");
        if (document.Schedule.UInt256Modulus.Any(static character => character is < '0' or > '9'))
            throw new ExtractionException("UInt256 modulus must be an unsigned decimal literal.");
    }

    private static string LeanList(IEnumerable<string> values) =>
        "[" + string.Join(", ", values.Select(static value => $"\"{Escape(value)}\"")) + "]";

    private static string LeanBool(bool value) => value ? "true" : "false";

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    internal static void ValidateSha256(string? value, string description)
    {
        if (value is null || value.Length != 64 || value.Any(static character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ExtractionException($"{description} SHA-256 must be exactly 64 lowercase hexadecimal characters.");
    }
}
