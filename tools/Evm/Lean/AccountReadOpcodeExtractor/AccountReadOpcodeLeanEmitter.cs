// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.Evm.Lean.AccountReadOpcodeExtractor;

internal static class AccountReadOpcodeLeanEmitter
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    private static readonly OpcodeDescriptor[] ExpectedOpcodes =
    [
        new("balance", "BALANCE", 0x31, "BalanceOpcode<TTracingInst,EvmInstructions.AccessSpec<OnFlag,Eip8038On>>", "balance", 1, 1, false, false,
            "enter>baseGas>popAddress>warm>accessGas>getBalance>push"),
        new("extCodeSize", "EXTCODESIZE", 0x3b, "ExtCodeSizeOpcode<TTracingInst,Eip8038On,OnFlag>", "codeLength", 1, 1, false, true,
            "enter>baseGas>popAddress>warm>accessGas>secondWarmGas>accountRead>fusionOrCodeNoDelegation>push"),
        new("extCodeCopy", "EXTCODECOPY", 0x3c, "ExtCodeCopyOpcode<TTracingInst,Eip8038On,OnFlag>", "zeroExtendedCodeCopy", 4, 0, true, true,
            "enter>popAddress>popDestinationSourceLength>copyGas>warm>accessGas>secondWarmGas>zeroLengthRecordOrMemoryGas>accountRead>codeNoDelegation>zeroExtendedCopy"),
        new("extCodeHash", "EXTCODEHASH", 0x3f, "ExtCodeHashOpcode<TTracingInst,EvmInstructions.AccessSpec<OnFlag,Eip8038On>>", "zeroIfDeadElseCodeHash", 1, 1, false, false,
            "enter>baseGas>popAddress>warm>accessGas>isDead>getCodeHashIfLive>push"),
    ];

    private static readonly string[] ExpectedObligations =
    [
        "C# and CLR execution correctness are trusted; complete Roslyn token admission only fails closed on source drift.",
        "Address is the low 160 bits of the popped UInt256; UInt256 and ValueHash256 byte/word representations are adapter premises.",
        "IWorldState and ICodeInfoRepository values, IsPrecompile, IsContract, IsDeadAccount, and code-cache coherence are provider premises.",
        "The operational theorem assumes IsTracingAccess=false; access-list-generation tracing intentionally pre-warms before pricing.",
        "Instruction trace callbacks, cancellation polling, unsafe stack storage, cache metrics, and frame rollback are outside this slice.",
        "PC in this model is the dispatch-local program counter; exceptional VM-state publication is a later frame/dispatcher obligation.",
    ];

    internal static void Validate(IrDocument document)
    {
        if (document is null || document.Reachability is null || document.Schedule is null ||
            document.Opcodes is null || document.Specializations is null ||
            document.OpenExtractionObligations is null ||
            document.Opcodes.Any(static opcode => opcode is null || opcode.Name is null ||
                opcode.Instruction is null || opcode.HandlerRoute is null || opcode.ValueRule is null ||
                opcode.EffectOrder is null) ||
            document.Specializations.Any(static specialization => specialization is null ||
                specialization.Opcode is null || specialization.DispatchTable is null ||
                specialization.TracingFlag is null || specialization.CancelableFlag is null ||
                specialization.ClosedRoot is null) ||
            document.OpenExtractionObligations.Any(static obligation => obligation is null))
            throw new ExtractionException("The round-tripped account-read IR contains a null value.");
        if (document.SchemaVersion != 1 || document.ExtractorVersion != "1.0.0" ||
            document.Kernel != "Nethermind standard-mainnet Amsterdam account-read opcode slice")
            throw new ExtractionException("The round-tripped account-read IR has an unsupported identity.");
        ReachabilityDescriptor expectedReachability = new(
            "IVirtualMachine",
            "EthereumVirtualMachine",
            "VirtualMachine<EthereumGasPolicy>",
            "EthereumGasPolicy",
            "WorldState",
            "CacheCodeInfoRepository",
            "EthereumPrecompileProvider",
            "StaticCodeCache.Instance",
            "Amsterdam",
            "PrepareOpcodes->OpcodeTable.GetHandlers->GenerateOpcodeHandlers->ConfigureAccessOpcodes->OpcodeHandler->ExecuteOpcode",
            "EnableZkEvm != true selects *.std.cs and excludes *.zkevm.cs");
        if (document.Reachability != expectedReachability)
            throw new ExtractionException("The round-tripped account-read reachability descriptor changed.");
        if (document.Schedule != new ScheduleDescriptor(0, 3000, 100, 3, 3, 512, 3))
            throw new ExtractionException("The round-tripped Amsterdam account-read schedule changed.");
        if (!document.OpenExtractionObligations.SequenceEqual(ExpectedObligations, StringComparer.Ordinal))
            throw new ExtractionException("The round-tripped account-read trust boundary changed.");
        if (!document.Opcodes.SequenceEqual(ExpectedOpcodes))
            throw new ExtractionException("The round-tripped account-read opcode descriptors changed.");
        if (document.Opcodes.Select(static opcode => opcode.Name).Distinct(StringComparer.Ordinal).Count() != ExpectedOpcodes.Length ||
            document.Opcodes.Select(static opcode => opcode.OpcodeByte).Distinct().Count() != ExpectedOpcodes.Length ||
            document.Opcodes.Any(static opcode => opcode.OpcodeByte is < 0 or > 255))
            throw new ExtractionException("The serialized IR contains duplicate or out-of-range opcodes.");
        AccountReadOpcodeProfile.ValidateSpecializations(document.Opcodes, document.Specializations);
    }

    internal static byte[] Emit(IrDocument document, string irHash)
    {
        Validate(document);
        StringBuilder source = new();
        source.AppendLine("-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited");
        source.AppendLine("-- SPDX-License-Identifier: LGPL-3.0-only");
        source.AppendLine();
        source.AppendLine("-- Generated by AccountReadOpcodeExtractor from round-tripped semantic IR; do not edit.");
        source.Append("-- Extractor: ").AppendLine(document.ExtractorVersion);
        source.Append("-- Semantic IR SHA-256: ").AppendLine(irHash);
        source.AppendLine();
        source.AppendLine("import Init.Data.String.Search");
        source.AppendLine("import Eip803x.Gas");
        source.AppendLine("import Eip803x.Evm.AccountReadTypes");
        source.AppendLine("import Eip803x.Evm.MemoryGas");
        source.AppendLine();
        source.AppendLine("namespace Eip803x.Generated.AccountReadOpcodeKernel");
        source.AppendLine();
        source.AppendLine("open Eip803x");
        source.AppendLine("open Eip803x.GasMachine");
        source.AppendLine("open Eip803x.Evm");
        source.AppendLine("open Eip803x.Evm.Word");
        source.AppendLine("open Eip803x.Evm.MemoryStackControl");
        source.AppendLine();
        EmitReachability(source, document);
        EmitSchedule(source, document.Schedule);
        EmitDescriptors(source, document.Opcodes);
        EmitSpecializations(source, document.Specializations);
        EmitMachine(source, document.Opcodes);
        source.AppendLine("end Eip803x.Generated.AccountReadOpcodeKernel");
        return Utf8WithoutBom.GetBytes(source.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static void EmitReachability(StringBuilder source, IrDocument document)
    {
        source.Append("def sourceKernel : String := \"").Append(Escape(document.Kernel)).AppendLine("\"");
        source.Append("def sourceInterface : String := \"").Append(Escape(document.Reachability.Interface)).AppendLine("\"");
        source.Append("def sourceImplementation : String := \"").Append(Escape(document.Reachability.Implementation)).AppendLine("\"");
        source.Append("def sourceClosedVm : String := \"").Append(Escape(document.Reachability.ClosedVm)).AppendLine("\"");
        source.Append("def sourceGasPolicy : String := \"").Append(Escape(document.Reachability.GasPolicy)).AppendLine("\"");
        source.Append("def sourceWorldState : String := \"").Append(Escape(document.Reachability.WorldState)).AppendLine("\"");
        source.Append("def sourceCodeRepository : String := \"").Append(Escape(document.Reachability.CodeRepository)).AppendLine("\"");
        source.Append("def sourcePrecompileProvider : String := \"").Append(Escape(document.Reachability.PrecompileProvider)).AppendLine("\"");
        source.Append("def sourceCodeCache : String := \"").Append(Escape(document.Reachability.CodeCache)).AppendLine("\"");
        source.Append("def sourceFork : String := \"").Append(Escape(document.Reachability.Fork)).AppendLine("\"");
        source.Append("def sourceDispatchRoot : String := \"").Append(Escape(document.Reachability.DispatchRoot)).AppendLine("\"");
        source.Append("def sourceBuildSelection : String := \"").Append(Escape(document.Reachability.BuildSelection)).AppendLine("\"");
        source.AppendLine("def openExtractionObligations : List String :=");
        source.AppendLine("  [");
        for (int index = 0; index < document.OpenExtractionObligations.Length; index++)
        {
            source.Append("    \"").Append(Escape(document.OpenExtractionObligations[index])).Append('"');
            source.AppendLine(index + 1 == document.OpenExtractionObligations.Length ? string.Empty : ",");
        }
        source.AppendLine("  ]");
        source.AppendLine();
    }

    private static void EmitSchedule(StringBuilder source, ScheduleDescriptor schedule)
    {
        source.AppendLine("structure Schedule where");
        source.AppendLine("  accountReadBase : Nat");
        source.AppendLine("  coldAccountAccess : Nat");
        source.AppendLine("  warmAccess : Nat");
        source.AppendLine("  copyWord : Nat");
        source.AppendLine("  memory : MemoryGas.Schedule");
        source.AppendLine("  veryLow : Nat");
        source.AppendLine("  deriving DecidableEq, Repr");
        source.AppendLine();
        source.AppendLine("def amsterdamSchedule : Schedule :=");
        source.Append("  { accountReadBase := ").Append(schedule.AccountReadBase).AppendLine();
        source.Append("    coldAccountAccess := ").Append(schedule.ColdAccountAccess).AppendLine();
        source.Append("    warmAccess := ").Append(schedule.WarmAccess).AppendLine();
        source.Append("    copyWord := ").Append(schedule.CopyWord).AppendLine();
        source.Append("    memory := { linear := ").Append(schedule.MemoryLinear)
            .Append(", quadraticDivisor := ").Append(schedule.MemoryQuadraticDivisor)
            .AppendLine(", quadraticDivisorPositive := by decide }");
        source.Append("    veryLow := ").Append(schedule.VeryLow).AppendLine(" }");
        source.AppendLine();
    }

    private static void EmitDescriptors(StringBuilder source, OpcodeDescriptor[] opcodes)
    {
        source.AppendLine("inductive Opcode where");
        foreach (OpcodeDescriptor opcode in opcodes) source.Append("  | ").AppendLine(opcode.Name);
        source.AppendLine("  deriving DecidableEq, Repr");
        source.AppendLine();
        source.Append("def allOpcodes : List Opcode := [");
        for (int index = 0; index < opcodes.Length; index++)
        {
            if (index != 0) source.Append(", ");
            source.Append('.').Append(opcodes[index].Name);
        }
        source.AppendLine("]");
        source.AppendLine();
        EmitNatTable(source, "opcodeByte", opcodes, static opcode => opcode.OpcodeByte);
        EmitStringTable(source, "instruction", opcodes, static opcode => opcode.Instruction);
        EmitStringTable(source, "handlerRoute", opcodes, static opcode => opcode.HandlerRoute);
        EmitStringTable(source, "valueRule", opcodes, static opcode => opcode.ValueRule);
        EmitNatTable(source, "stackInputs", opcodes, static opcode => opcode.StackInputs);
        EmitNatTable(source, "stackOutputs", opcodes, static opcode => opcode.StackOutputs);
        EmitBoolTable(source, "popsBeforeGas", opcodes, static opcode => opcode.PopsBeforeGas);
        EmitBoolTable(source, "chargesSecondRead", opcodes, static opcode => opcode.ChargesSecondRead);
        EmitStringTable(source, "effectOrder", opcodes, static opcode => opcode.EffectOrder);
    }

    private static void EmitSpecializations(StringBuilder source, OpcodeSpecialization[] specializations)
    {
        source.AppendLine("inductive DispatchTable where");
        source.AppendLine("  | noTrace | noTraceCancelable | traced | tracedCancelable");
        source.AppendLine("  deriving DecidableEq, Repr");
        source.AppendLine();
        source.AppendLine("def allDispatchTables : List DispatchTable :=");
        source.AppendLine("  [.noTrace, .noTraceCancelable, .traced, .tracedCancelable]");
        source.AppendLine("def tracingFlag : DispatchTable → String");
        source.AppendLine("  | .noTrace | .noTraceCancelable => \"OffFlag\"");
        source.AppendLine("  | .traced | .tracedCancelable => \"OnFlag\"");
        source.AppendLine("def cancelableFlag : DispatchTable → String");
        source.AppendLine("  | .noTrace | .traced => \"OffFlag\"");
        source.AppendLine("  | .noTraceCancelable | .tracedCancelable => \"OnFlag\"");
        source.AppendLine("def closedHandlerRoute (opcode : Opcode) (table : DispatchTable) : String :=");
        source.AppendLine("  (handlerRoute opcode).replace \"TTracingInst\" (tracingFlag table)");
        source.AppendLine("def expectedClosedRoot (opcode : Opcode) (table : DispatchTable) : String :=");
        source.AppendLine("  \"VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<\" ++ closedHandlerRoute opcode table ++ \",\" ++");
        source.AppendLine("    tracingFlag table ++ \",\" ++ cancelableFlag table ++ \",OnFlag>\"");
        source.AppendLine();
        source.AppendLine("structure Specialization where");
        source.AppendLine("  opcode : Opcode");
        source.AppendLine("  table : DispatchTable");
        source.AppendLine("  tracingFlag : String");
        source.AppendLine("  cancelableFlag : String");
        source.AppendLine("  closedRoot : String");
        source.AppendLine("  deriving DecidableEq, Repr");
        source.AppendLine();
        source.AppendLine("def specializations : List Specialization :=");
        source.AppendLine("  [");
        for (int index = 0; index < specializations.Length; index++)
        {
            OpcodeSpecialization item = specializations[index];
            source.Append("    { opcode := .").Append(item.Opcode)
                .Append(", table := .").Append(LeanTable(item.DispatchTable))
                .Append(", tracingFlag := \"").Append(Escape(item.TracingFlag))
                .Append("\", cancelableFlag := \"").Append(Escape(item.CancelableFlag))
                .Append("\", closedRoot := \"").Append(Escape(item.ClosedRoot)).Append("\" }");
            source.AppendLine(index + 1 == specializations.Length ? string.Empty : ",");
        }
        source.AppendLine("  ]");
        source.AppendLine();
    }

    private static void EmitMachine(StringBuilder source, OpcodeDescriptor[] opcodes)
    {
        source.AppendLine("abbrev AccountView := AccountReadTypes.AccountView");
        source.AppendLine("abbrev Provider := AccountReadTypes.Provider");
        source.AppendLine();
        source.AppendLine("abbrev Status := AccountReadTypes.Status");
        source.AppendLine("abbrev State := AccountReadTypes.State");
        source.AppendLine("abbrev Outcome := AccountReadTypes.Outcome");
        source.AppendLine();
        source.AppendLine("def addressOfWord (word : UInt256) : UInt256 := Word.ofNat (word.val % (2 ^ 160))");
        source.AppendLine("def enter (state : State) : State := { state with pc := state.pc + 1, opcodeCount := state.opcodeCount + 1 }");
        source.AppendLine("def exhaust (state : State) : State := { state with gas := { state.gas with gasLeft := 0 } }");
        source.AppendLine("def charge (amount : Nat) (state : State) : Except Outcome State :=");
        source.AppendLine("  match chargeExecution amount state.gas with");
        source.AppendLine("  | .error _ => .error { status := .outOfGas, state := exhaust state }");
        source.AppendLine("  | .ok gas => .ok { state with gas }");
        source.AppendLine("def containsAddress (addresses : List UInt256) (address : UInt256) : Bool := addresses.contains address");
        source.AppendLine("def warmAccount (addresses : List UInt256) (address : UInt256) : List UInt256 :=");
        source.AppendLine("  if containsAddress addresses address then addresses else address :: addresses");
        source.AppendLine("def accessIsCold (provider : Provider) (state : State) (address : UInt256) : Bool :=");
        source.AppendLine("  !(containsAddress state.warmAccounts address) && !(provider address).precompile");
        source.AppendLine("def accessCharge (schedule : Schedule) (provider : Provider) (state : State) (address : UInt256) : Nat :=");
        source.AppendLine("  if accessIsCold provider state address then schedule.coldAccountAccess else schedule.warmAccess");
        source.AppendLine("def warmForAccess (state : State) (address : UInt256) : State :=");
        source.AppendLine("  { state with warmAccounts := warmAccount state.warmAccounts address }");
        source.AppendLine("def recordAccountRead (state : State) (address : UInt256) : State :=");
        source.AppendLine("  { state with accountReads := state.accountReads ++ [address] }");
        source.AppendLine("def recordBytecodeRead (state : State) (address : UInt256) : State :=");
        source.AppendLine("  { state with bytecodeReads := state.bytecodeReads ++ [address] }");
        source.AppendLine("def pushKnownRoom (stack : Stack) (value : UInt256) : Stack := Stack.fromWords (value :: stack.words)");
        source.AppendLine("def topIsZero (stack : Stack) : Bool := match stack.words.head? with | none => false | some value => value.val = 0");
        source.AppendLine("def isFusionOpcode (value : Byte) : Bool := value = MemoryStackControl.byte 0x15 || value = MemoryStackControl.byte 0x11 || value = MemoryStackControl.byte 0x14");
        source.AppendLine("def fusionResult (next : Byte) (isContract : Bool) : UInt256 :=");
        source.AppendLine("  let condition := if next = MemoryStackControl.byte 0x11 then isContract else !isContract");
        source.AppendLine("  if condition then Word.one else Word.zero");
        source.AppendLine();
        source.AppendLine("def valueFor (provider : Provider) (opcode : Opcode) (address : UInt256) : UInt256 :=");
        source.AppendLine("  match opcode with");
        foreach (OpcodeDescriptor opcode in opcodes)
        {
            string expression = opcode.ValueRule switch
            {
                "balance" => "(provider address).balance",
                "codeLength" => "Word.ofNat (provider address).code.length",
                "zeroExtendedCodeCopy" => "Word.zero",
                "zeroIfDeadElseCodeHash" => "if (provider address).dead then Word.zero else (provider address).codeHash",
                _ => throw new ExtractionException($"No Lean value rule exists for {opcode.ValueRule}."),
            };
            source.Append("  | .").Append(opcode.Name).Append(" => ").AppendLine(expression);
        }
        source.AppendLine();
        source.AppendLine("def executeRead (schedule : Schedule) (provider : Provider) (instructionTracing : Bool)");
        source.AppendLine("    (code : List Byte) (opcode : Opcode) (state : State) : Outcome :=");
        source.AppendLine("  let entered := enter state");
        source.AppendLine("  match charge schedule.accountReadBase entered with");
        source.AppendLine("  | .error outcome => outcome");
        source.AppendLine("  | .ok baseCharged =>");
        source.AppendLine("    match baseCharged.stack.pop with");
        source.AppendLine("    | none => { status := .stackUnderflow, state := baseCharged }");
        source.AppendLine("    | some (rawAddress, tail) =>");
        source.AppendLine("      let address := addressOfWord rawAddress");
        source.AppendLine("      let access := accessCharge schedule provider baseCharged address");
        source.AppendLine("      let warmed := warmForAccess { baseCharged with stack := tail } address");
        source.AppendLine("      match charge access warmed with");
        source.AppendLine("      | .error outcome => outcome");
        source.AppendLine("      | .ok accessCharged =>");
        source.AppendLine("        let second := if chargesSecondRead opcode then schedule.warmAccess else 0");
        source.AppendLine("        match charge second accessCharged with");
        source.AppendLine("        | .error outcome => outcome");
        source.AppendLine("        | .ok charged =>");
        source.AppendLine("          let readState := if opcode = .extCodeSize then recordAccountRead charged address else charged");
        source.AppendLine("          if opcode = .extCodeSize && !instructionTracing then");
        source.AppendLine("            match code[entered.pc]? with");
        source.AppendLine("            | some next =>");
        source.AppendLine("              if isFusionOpcode next && (next = MemoryStackControl.byte 0x15 || topIsZero tail) then");
        source.AppendLine("                let fusionTail := if next = MemoryStackControl.byte 0x15 then tail else Stack.fromWords tail.words.tail");
        source.AppendLine("                let fused := { readState with pc := readState.pc + 1, opcodeCount := readState.opcodeCount + 1, stack := fusionTail }");
        source.AppendLine("                match charge schedule.veryLow fused with");
        source.AppendLine("                | .error outcome => outcome");
        source.AppendLine("                | .ok fusionCharged => { status := .ok, state := { fusionCharged with stack := pushKnownRoom fusionCharged.stack (fusionResult next (provider address).isContract) } }");
        source.AppendLine("              else { status := .ok, state := { readState with stack := pushKnownRoom tail (valueFor provider opcode address) } }");
        source.AppendLine("            | none => { status := .ok, state := { readState with stack := pushKnownRoom tail (valueFor provider opcode address) } }");
        source.AppendLine("          else { status := .ok, state := { readState with stack := pushKnownRoom tail (valueFor provider opcode address) } }");
        source.AppendLine();
        source.AppendLine("def copyWords (length : UInt256) : Option Nat :=");
        source.AppendLine("  if length.val ≤ (2 ^ 32 - 1) * 32 then some ((length.val + 31) / 32) else none");
        source.AppendLine("def executeExtCodeCopy (schedule : Schedule) (provider : Provider) (state : State) : Outcome :=");
        source.AppendLine("  let entered := enter state");
        source.AppendLine("  match entered.stack.pop with");
        source.AppendLine("  | none => { status := .stackUnderflow, state := entered }");
        source.AppendLine("  | some (rawAddress, afterAddress) =>");
        source.AppendLine("    match afterAddress.popThree with");
        source.AppendLine("    | none => { status := .stackUnderflow, state := { entered with stack := afterAddress } }");
        source.AppendLine("    | some (destination, sourceOffset, length, tail) =>");
        source.AppendLine("      let popped := { entered with stack := tail }");
        source.AppendLine("      match copyWords length with");
        source.AppendLine("      | none => { status := .outOfGas, state := exhaust popped }");
        source.AppendLine("      | some words =>");
        source.AppendLine("        match charge (schedule.accountReadBase + schedule.copyWord * words) popped with");
        source.AppendLine("        | .error outcome => outcome");
        source.AppendLine("        | .ok copyCharged =>");
        source.AppendLine("          let address := addressOfWord rawAddress");
        source.AppendLine("          let access := accessCharge schedule provider copyCharged address");
        source.AppendLine("          let warmed := warmForAccess copyCharged address");
        source.AppendLine("          match charge access warmed with");
        source.AppendLine("          | .error outcome => outcome");
        source.AppendLine("          | .ok accessCharged =>");
        source.AppendLine("            match charge schedule.warmAccess accessCharged with");
        source.AppendLine("            | .error outcome => outcome");
        source.AppendLine("            | .ok secondCharged =>");
        source.AppendLine("              if length.val = 0 then");
        source.AppendLine("                { status := .ok, state := recordBytecodeRead (recordAccountRead secondCharged address) address }");
        source.AppendLine("              else");
        source.AppendLine("                match MemoryGas.prepare schedule.memory secondCharged.memory destination length with");
        source.AppendLine("                | none => { status := .outOfGas, state := exhaust secondCharged }");
        source.AppendLine("                | some (memoryCost, expanded) =>");
        source.AppendLine("                  let memoryPrepared := { secondCharged with memory := expanded }");
        source.AppendLine("                  match charge memoryCost memoryPrepared with");
        source.AppendLine("                  | .error outcome => outcome");
        source.AppendLine("                  | .ok memoryCharged =>");
        source.AppendLine("                    let bytes := readRange (provider address).code sourceOffset.val length.val");
        source.AppendLine("                    { status := .ok, state := recordAccountRead { memoryCharged with memory := memoryCharged.memory.writeRange destination.val bytes } address }");
        source.AppendLine();
        source.AppendLine("def execute (schedule : Schedule) (provider : Provider) (instructionTracing : Bool)");
        source.AppendLine("    (code : List Byte) (opcode : Opcode) (state : State) : Outcome :=");
        source.AppendLine("  if popsBeforeGas opcode then executeExtCodeCopy schedule provider state");
        source.AppendLine("  else executeRead schedule provider instructionTracing code opcode state");
        source.AppendLine();
    }

    private static void EmitNatTable(StringBuilder source, string name, OpcodeDescriptor[] opcodes, Func<OpcodeDescriptor, int> value)
    {
        source.Append("def ").Append(name).AppendLine(" : Opcode → Nat");
        foreach (OpcodeDescriptor opcode in opcodes)
            source.Append("  | .").Append(opcode.Name).Append(" => ").AppendLine(value(opcode).ToString(System.Globalization.CultureInfo.InvariantCulture));
        source.AppendLine();
    }

    private static void EmitStringTable(StringBuilder source, string name, OpcodeDescriptor[] opcodes, Func<OpcodeDescriptor, string> value)
    {
        source.Append("def ").Append(name).AppendLine(" : Opcode → String");
        foreach (OpcodeDescriptor opcode in opcodes)
            source.Append("  | .").Append(opcode.Name).Append(" => \"").Append(Escape(value(opcode))).AppendLine("\"");
        source.AppendLine();
    }

    private static void EmitBoolTable(StringBuilder source, string name, OpcodeDescriptor[] opcodes, Func<OpcodeDescriptor, bool> value)
    {
        source.Append("def ").Append(name).AppendLine(" : Opcode → Bool");
        foreach (OpcodeDescriptor opcode in opcodes)
            source.Append("  | .").Append(opcode.Name).Append(" => ").AppendLine(value(opcode) ? "true" : "false");
        source.AppendLine();
    }

    private static string LeanTable(string table) => table switch
    {
        "NoTrace" => "noTrace",
        "NoTraceCancelable" => "noTraceCancelable",
        "Traced" => "traced",
        "TracedCancelable" => "tracedCancelable",
        _ => throw new ExtractionException($"Unknown dispatch table {table}."),
    };

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);
}
