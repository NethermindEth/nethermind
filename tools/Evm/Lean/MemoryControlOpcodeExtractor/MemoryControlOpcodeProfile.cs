// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.MemoryControlOpcodeExtractor;

internal static class MemoryControlOpcodeProfile
{
    internal const int SchemaVersion = 2;
    internal const string ExtractorVersion = "1.2.0";
    internal const string IrFileName = "MemoryControlOpcodeKernel.ir.json";
    internal const string ManifestFileName = "MemoryControlOpcodeKernel.source-manifest.json";
    internal const string DefaultLeanRelativePath = "tools/Evm/Lean/Eip803x/Generated/MemoryControlOpcodeKernel.lean";

    private const string OpcodeHandlersPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs";
    private const string DispatchPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs";
    private const string InstructionPath = "src/Nethermind/Nethermind.Evm/Instruction.cs";
    private const string GasCostPath = "src/Nethermind/Nethermind.Core/GasCostOf.cs";
    private const string VirtualMachineStandardPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.std.cs";
    private const string NamedReleaseSpecPath = "src/Nethermind/Nethermind.Specs/Forks/NamedReleaseSpec.cs";
    private const string BuildTargetsPath = "src/Nethermind/Directory.Build.targets";
    private const string BuildTargetsSha256 = "0598cebaef1df41102a18a3f9ace810bed1e4e64b8471d055724b4a394dccec6";
    private const string ForkDirectory = "src/Nethermind/Nethermind.Specs/Forks";
    private const string Root = "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>";

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp14);
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
    };

    private static readonly DispatchTableBinding[] DispatchTables =
    [
        new("NoTrace", "OffFlag", "OffFlag"),
        new("NoTraceCancelable", "OffFlag", "OnFlag"),
        new("Traced", "OnFlag", "OffFlag"),
        new("TracedCancelable", "OnFlag", "OnFlag"),
    ];

    private static readonly string[] ForkLineage =
    [
        "Olympic", "Frontier", "Homestead", "Dao", "TangerineWhistle", "SpuriousDragon",
        "Byzantium", "Constantinople", "ConstantinopleFix", "Istanbul", "MuirGlacier", "Berlin",
        "London", "ArrowGlacier", "GrayGlacier", "Paris", "Shanghai", "Cancun", "Prague", "Osaka",
        "BPO1", "BPO2", "Amsterdam",
    ];

    private static readonly AdmissionSpec[] AdmissionSpecs = BuildAdmissionSpecs();
    private static readonly OpcodeSeed[] Seeds = BuildSeeds();
    private static readonly string[] ExpectedOpenExtractionObligations =
    [
        "Roslyn syntax admission and the hand-reviewed semantic tags do not prove C# or CLR execution semantics.",
        "EvmStack unsafe slot layout, UInt256 conversion, EvmPooledMemory byte operations, and jump-bitmap construction remain provider obligations.",
        "The generated profile proves dispatch identity, opcode bytes, numeric fixed/copy-word/memory-linear gas metadata, arities, activation, and selected source routes; it is not an operational C# extraction.",
        "Tracing, cancellation polling, tail calls, opcode counting, trace callbacks, allocation limits, and exceptional-frame settlement remain outside this projection.",
    ];

    internal static IReadOnlyList<string> SourcePaths => AdmissionSpecs
        .Select(static spec => spec.SourcePath)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    internal static IReadOnlyList<string> InputPaths => [.. SourcePaths, BuildTargetsPath];

    internal static ExtractionResult Extract(string repoRoot, string outputDirectory, string? leanOutputPath = null,
        IReadOnlyDictionary<string, string>? admissionOverrides = null)
    {
        string root = Path.GetFullPath(repoRoot);
        Dictionary<string, SourceFile> sources = LoadSources(root);
        RawSourceIdentity buildTargets = LoadBuildTargets(root);
        List<AdmissionIdentity> admissions = AdmitReviewedSyntax(sources, admissionOverrides);
        ValidateNoCompetingMembers(root, sources);
        ValidateStandardReachability(sources);
        ValidateBuildFlavor(root);
        ValidateForkReachability(sources);
        Dictionary<string, int> gasConstants = ReadGasConstants(sources[GasCostPath].Syntax);
        IReadOnlyList<OpcodeDescriptor> opcodes = ExtractOpcodes(sources, gasConstants);
        IReadOnlyList<OpcodeSpecialization> specializations = BuildSpecializations(opcodes);

        IrDocument document = new(
            SchemaVersion,
            ExtractorVersion,
            Root,
            "Nethermind.Evm.GasPolicy.EthereumGasPolicy",
            "Nethermind.Specs.Forks.Amsterdam",
            gasConstants["Memory"],
            gasConstants["Memory"],
            opcodes,
            specializations,
            ForkLineage,
            ExpectedOpenExtractionObligations);

        byte[] irBytes = Serialize(document);
        IrDocument serialized = DeserializeIr(irBytes);
        List<SourceIdentity> identities = BuildSourceIdentities(sources);
        string combinedSourceSha256 = Hash(Encoding.UTF8.GetBytes(string.Join("\n",
            identities.Select(static identity => $"{identity.Path}\0{identity.Sha256}\0{identity.RoslynSyntaxSha256}")
                .Append($"{buildTargets.Path}\0{buildTargets.Sha256}\0raw"))));
        byte[] leanBytes = MemoryControlOpcodeLeanEmitter.Emit(serialized, Hash(irBytes), combinedSourceSha256);
        SourceManifest manifest = new(
            SchemaVersion,
            ExtractorVersion,
            Hash(irBytes),
            Hash(leanBytes),
            combinedSourceSha256,
            identities,
            [buildTargets],
            admissions);

        string output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        string irPath = Path.Combine(output, IrFileName);
        string manifestPath = Path.Combine(output, ManifestFileName);
        string leanPath = leanOutputPath is null
            ? Path.Combine(root, DefaultLeanRelativePath.Replace('/', Path.DirectorySeparatorChar))
            : Path.GetFullPath(leanOutputPath);
        string? leanDirectory = Path.GetDirectoryName(leanPath);
        if (leanDirectory is not null) Directory.CreateDirectory(leanDirectory);
        File.WriteAllBytes(irPath, irBytes);
        File.WriteAllBytes(manifestPath, Serialize(manifest));
        File.WriteAllBytes(leanPath, leanBytes);
        return new(irPath, manifestPath, leanPath, opcodes.Count, specializations.Count, admissions.Count);
    }

    private static AdmissionSpec[] BuildAdmissionSpecs()
    {
        List<AdmissionSpec> specs =
        [
            Spec(GasCostPath, "GasCostOf", "Nethermind.Core", "GasCostOf", 0,
                F("Base"), F("VeryLow"), F("Mid"), F("High"), F("JumpDest"), F("Memory")),
            Spec("src/Nethermind/Nethermind.Core/TypeFlags.cs", "TypeFlags", "Nethermind.Core", null, 0,
                T("IFlag"), T("OffFlag"), T("OnFlag")),
            Spec(InstructionPath, "Instruction", "Nethermind.Evm", null, 0, T("Instruction")),
            Spec("src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs", "DispatchFlags", "Nethermind.Evm", null, 0,
                T("DispatchFlags")),
            Spec("src/Nethermind/Nethermind.Evm/VirtualMachine.cs", "EthereumVirtualMachine", "Nethermind.Evm", null, 0,
                T("EthereumVirtualMachine")),
            Spec(VirtualMachineStandardPath, "VirtualMachineStandard", "Nethermind.Evm", "VirtualMachine", 1,
                F("_opcodeTablesBySpec"), F("OpcodeRefreshInterval"), F("OpcodeRefreshLimit"), F("_txCount"),
                M("GetOpcodeTable", 0, 0), M("ShouldRefreshOpcodes", 0, 0)),
            Spec("src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs", "BlockProcessingModule", "Nethermind.Init.Modules", "BlockProcessingModule", 0,
                M("Load", 0, 1, "ContainerBuilder")),
            Spec(NamedReleaseSpecPath, "NamedReleaseSpec", "Nethermind.Specs.Forks", null, 0,
                T("NamedReleaseSpec"), T("NamedReleaseSpec", 1)),
            Spec(DispatchPath, "VirtualMachineDispatch", "Nethermind.Evm", "VirtualMachine", 1,
                M("GetOpcodeHandlers", 2, 0), M("PrepareOpcodes", 1, 0), M("PrepareOpcodes", 2, 0),
                T("OpcodeTable"), M("RunDispatchLoop", 2, 3), M("ExecuteOpcode", 4, 5),
                M("ExecuteJumpIfOpcode", 2, 5), M("ExitCheckedOpcode", 0, 4)),
            Spec(OpcodeHandlersPath, "VirtualMachineOpcodeHandlers", "Nethermind.Evm", "VirtualMachine", 1,
                [
                    T("IOpcodeBody"), M("OpcodeHandler", 3, 0), M("TerminatingOpcodeHandler", 3, 0),
                    M("JumpIfOpcodeHandler", 2, 0), M("GenerateOpcodeHandlers", 2, 1, "IReleaseSpec"),
                    T("StopOpcode"), T("CallDataLoadOpcode", 1), T("CallDataCopyOpcode", 1), T("CodeCopyOpcode", 1),
                    T("ReturnDataCopyOpcode", 1), T("PopOpcode"), T("MLoadOpcode", 1), T("MStoreOpcode", 1),
                    T("MStore8Opcode", 1), T("JumpOpcode", 1), T("ProgramCounterOpcode", 1), T("GasOpcode", 1),
                    T("JumpDestOpcode"), T("MCopyOpcode", 1), T("Push0Opcode", 1), T("Push2Opcode", 1),
                    T("PushOpcode", 2), T("DupOpcode", 2), T("SwapOpcode", 2), T("ReturnOpcode"),
                    T("RevertOpcode"), T("InvalidOpcode"),
                ]),
            Spec("src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Storage.cs", "InstructionStorage", "Nethermind.Evm", "EvmInstructions", 0,
                M("InstructionMStore", 2, 3), M("InstructionMStore8", 2, 3), M("InstructionMLoad", 2, 3),
                M("InstructionMCopy", 2, 3), M("CallDataLoadCore", 2, 2)),
            Spec("src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.CodeCopy.cs", "InstructionCodeCopy", "Nethermind.Evm", "EvmInstructions", 0,
                M("DataCopy", 2, 4), M("InstructionCodeCopy", 2, 3), M("InstructionCallDataCopy", 2, 3),
                M("InstructionReturnDataCopy", 2, 3)),
            Spec("src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.ControlFlow.cs", "InstructionControlFlow", "Nethermind.Evm", "EvmInstructions", 0,
                [
                    M("InstructionProgramCounter", 2, 4), M("InstructionJumpDest", 1, 3),
                    M("InstructionJump", 1, 4), M("InstructionJumpAndSkipJumpDest", 1, 4), M("InstructionJump", 2, 4),
                    M("InstructionJumpIf", 1, 4), M("InstructionJumpIfAndSkipJumpDest", 1, 4), M("InstructionJumpIf", 2, 4),
                    M("SkipJumpDest", 2, 4), M("InstructionStop", 1, 3), M("InstructionRevert", 1, 3),
                    M("InstructionInvalid", 1, 3), M("JumpDestination", 0, 2, "byte,EvmStack"),
                    M("JumpDestination", 0, 2, "int,EvmStack"), M("PrefetchCodeAtDestination", 0, 2),
                ]),
            Spec("src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.cs", "InstructionReturn", "Nethermind.Evm", "EvmInstructions", 0,
                M("InstructionReturn", 1, 3)),
            Spec("src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Environment.cs", "InstructionEnvironment", "Nethermind.Evm", "EvmInstructions", 0,
                T("OpMSize", 1), M("InstructionGas", 2, 2)),
            Spec("src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Stack.cs", "InstructionStack", "Nethermind.Evm", "EvmInstructions", 0,
                BuildStackMembers()),
            Spec("src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs", "IGasPolicy", "Nethermind.Evm.GasPolicy", "IGasPolicy", 1,
                M("UpdateGas", 1, 1), M("TryConsumeMemoryCopy", 0, 2), M("UpdateMemoryCost", 0, 4, "TSelf,UInt256,UInt256,EvmPooledMemory"),
                M("UpdateMemoryCost", 0, 4, "TSelf,UInt256,ulong,EvmPooledMemory"), M("UpdateGas", 0, 2),
                M("TryConsumeDataCopyGas", 0, 4)),
            Spec("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "EthereumGasPolicy", "Nethermind.Evm.GasPolicy", "EthereumGasPolicy", 0,
                M("GetRemainingGas", 0, 1), M("ConsumeRaw", 0, 2),
                M("UpdateMemoryCost", 0, 4, "EthereumGasPolicy,UInt256,UInt256,EvmPooledMemory"),
                M("UpdateMemoryCost", 0, 4, "EthereumGasPolicy,UInt256,ulong,EvmPooledMemory"),
                M("UpdateGas", 0, 2), M("TryConsumeDataCopyGas", 0, 4)),
            Spec("src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs", "EvmPooledMemory", "Nethermind.Evm", "EvmPooledMemory", 0,
                F("WordSize"), F("MaxMemorySize"), P("Size"),
                M("CalculateMemoryCost", 0, 3, "UInt256,ulong,bool"),
                M("CalculateMemoryCost", 0, 3, "UInt256,UInt256,bool"), M("ComputeMemoryExpansionCost", 0, 1),
                M("CheckMemoryAccessViolation", 0, 4, "UInt256,ulong,ulong,bool"),
                M("CheckMemoryAccessViolation", 0, 4, "UInt256,UInt256,ulong,bool"), M("UpdateSize", 0, 2)),
            Spec("src/Nethermind/Nethermind.Evm/CodeAnalysis/JumpDestinationAnalyzer.cs", "JumpDestinationAnalyzer", "Nethermind.Evm.CodeAnalysis", "JumpDestinationAnalyzer", 0,
                M("IsJumpDestination", 0, 2)),
        ];

        string[] forkFiles =
        [
            "00_Olympic", "01_Frontier", "02_Homestead", "03_Dao", "04_TangerineWhistle",
            "05_SpuriousDragon", "06_Byzantium", "07_Constantinople", "08_ConstantinopleFix",
            "09_Istanbul", "10_MuirGlacier", "11_Berlin", "12_London", "13_ArrowGlacier",
            "14_GrayGlacier", "15_Paris", "16_Shanghai", "17_Cancun", "18_Prague", "19_Osaka",
            "20_BPO1", "21_BPO2", "25_Amsterdam",
        ];
        for (int index = 0; index < forkFiles.Length; index++)
            specs.Add(Spec($"{ForkDirectory}/{forkFiles[index]}.cs", "Fork" + ForkLineage[index],
                "Nethermind.Specs.Forks", null, 0, T(ForkLineage[index])));
        return specs.ToArray();
    }

    private static MemberSelector[] BuildStackMembers()
    {
        List<MemberSelector> members = [T("IOpCount")];
        for (int index = 0; index <= 32; index++) members.Add(T("Op" + index.ToString(CultureInfo.InvariantCulture)));
        members.Add(M("InstructionPush2", 2, 4));
        members.Add(M("InstructionPush0", 2, 3));
        members.Add(M("InstructionPush", 3, 3));
        members.Add(M("InstructionDup", 3, 3));
        members.Add(M("InstructionSwap", 3, 3));
        return members.ToArray();
    }

    private static OpcodeSeed[] BuildSeeds()
    {
        List<OpcodeSeed> seeds =
        [
            Seed("stop", "STOP", 0x00, "StopOpcode", "terminating", "zero", "none", "none", "stop", 0, 0),
            Seed("calldataload", "CALLDATALOAD", 0x35, "CallDataLoadOpcode<TTracingInst>", "ordinary", "veryLow", "none", "none", "continue", 1, 0),
            Seed("calldatacopy", "CALLDATACOPY", 0x37, "CallDataCopyOpcode<TTracingInst>", "ordinary", "veryLow", "copyWords", "copyDestination", "continue", 3, 0),
            Seed("codecopy", "CODECOPY", 0x39, "CodeCopyOpcode<TTracingInst>", "ordinary", "veryLow", "copyWords", "copyDestination", "continue", 3, 0),
            Seed("returndatacopy", "RETURNDATACOPY", 0x3e, "ReturnDataCopyOpcode<TTracingInst>", "ordinary", "veryLow", "copyWords", "copyDestination", "continue", 3, 0, activation: "eip211"),
            Seed("pop", "POP", 0x50, "PopOpcode", "ordinary", "base", "none", "none", "continue", 1, 0),
            Seed("mload", "MLOAD", 0x51, "MLoadOpcode<TTracingInst>", "ordinary", "veryLow", "none", "word32", "continue", 1, 0),
            Seed("mstore", "MSTORE", 0x52, "MStoreOpcode<TTracingInst>", "ordinary", "veryLow", "none", "word32", "continue", 2, 0),
            Seed("mstore8", "MSTORE8", 0x53, "MStore8Opcode<TTracingInst>", "ordinary", "veryLow", "none", "byte1", "continue", 2, 0),
            Seed("jump", "JUMP", 0x56, "JumpOpcode<TTracingInst>", "ordinary", "mid", "none", "none", "jump", 1, 0),
            Seed("jumpi", "JUMPI", 0x57, "JumpIfOpcode", "jumpIf", "high", "none", "none", "conditionalJump", 2, 0),
            Seed("pc", "PC", 0x58, "ProgramCounterOpcode<TTracingInst>", "ordinary", "base", "none", "none", "continue", 0, 1),
            Seed("msize", "MSIZE", 0x59, "EnvUInt64Opcode<EvmInstructions.OpMSize<TGasPolicy>,TTracingInst>", "ordinary", "base", "none", "none", "continue", 0, 1),
            Seed("gas", "GAS", 0x5a, "GasOpcode<TTracingInst>", "ordinary", "base", "none", "none", "continue", 0, 1),
            Seed("jumpdest", "JUMPDEST", 0x5b, "JumpDestOpcode", "ordinary", "jumpdest", "none", "none", "continue", 0, 0),
            Seed("mcopy", "MCOPY", 0x5e, "MCopyOpcode<TTracingInst>", "ordinary", "veryLow", "copyWords", "memoryCopy", "continue", 3, 0, activation: "eip5656"),
            Seed("push0", "PUSH0", 0x5f, "Push0Opcode<TTracingInst>", "ordinary", "base", "none", "none", "continue", 0, 1, 0, "eip3855"),
            Seed("return", "RETURN", 0xf3, "ReturnOpcode", "terminating", "zero", "none", "returnRange", "returnData", 2, 0),
            Seed("revert", "REVERT", 0xfd, "RevertOpcode", "terminating", "zero", "none", "returnRange", "revertData", 2, 0, activation: "eip140"),
            Seed("invalid", "INVALID", 0xfe, "InvalidOpcode", "terminating", "zero", "none", "none", "invalid", 0, 0),
        ];
        for (int width = 1; width <= 32; width++)
        {
            string body = width == 2 ? "Push2Opcode<TTracingInst>" : $"PushOpcode<EvmInstructions.Op{width},TTracingInst>";
            seeds.Add(Seed($"push{width}", $"PUSH{width}", 0x5f + width, body, "ordinary", "veryLow", "none", "none", "continue", 0, 1, width));
        }
        for (int depth = 1; depth <= 16; depth++)
            seeds.Add(Seed($"dup{depth}", $"DUP{depth}", 0x7f + depth, $"DupOpcode<EvmInstructions.Op{depth},TTracingInst>",
                "ordinary", "veryLow", "none", "none", "continue", depth, 1));
        for (int depth = 1; depth <= 16; depth++)
            seeds.Add(Seed($"swap{depth}", $"SWAP{depth}", 0x8f + depth, $"SwapOpcode<EvmInstructions.Op{depth},TTracingInst>",
                "ordinary", "veryLow", "none", "none", "continue", depth + 1, 0));
        return seeds.OrderBy(static seed => seed.ExpectedByte).ToArray();
    }

    private static OpcodeSeed Seed(string name, string instruction, int opcodeByte, string body, string root,
        string gas, string dynamicGas, string memory, string exit, int inputs, int growth,
        int immediateWidth = 0, string activation = "unconditional") =>
        new(name, instruction, opcodeByte, body, root, gas, dynamicGas, memory, exit, inputs, growth,
            immediateWidth, activation);

    private static IReadOnlyList<OpcodeDescriptor> ExtractOpcodes(
        Dictionary<string, SourceFile> sources,
        IReadOnlyDictionary<string, int> gasConstants)
    {
        Dictionary<string, int> instructionValues = ReadInstructionValues(sources[InstructionPath].Syntax);
        MethodDeclarationSyntax generator = FindMethod(FindOwner(sources[OpcodeHandlersPath].Syntax, "VirtualMachine", 1),
            "GenerateOpcodeHandlers", 2, 1);
        Dictionary<string, string> assignments = ExtractDispatchAssignments(generator);
        List<OpcodeDescriptor> opcodes = new(Seeds.Length);
        HashSet<int> bytes = [];
        foreach (OpcodeSeed seed in Seeds)
        {
            if (!instructionValues.TryGetValue(seed.Instruction, out int opcodeByte))
                throw new ExtractionException($"Instruction.{seed.Instruction} is missing.");
            if (opcodeByte != seed.ExpectedByte)
                throw new ExtractionException($"Instruction.{seed.Instruction} changed from byte {seed.ExpectedByte} to {opcodeByte}.");
            if (!bytes.Add(opcodeByte))
                throw new ExtractionException($"Instruction.{seed.Instruction} duplicates selected opcode byte {opcodeByte}.");
            if (!assignments.TryGetValue(seed.Instruction, out string? body))
                throw new ExtractionException($"Instruction.{seed.Instruction} has no unique admitted dispatch assignment.");
            string expected = ExpectedDispatch(seed);
            if (body != expected)
                throw new ExtractionException($"Production dispatch for {seed.Instruction} changed from '{expected}' to '{body}'.");
            int fixedGas = seed.GasClass == "zero"
                ? 0
                : gasConstants[seed.GasClass switch
                {
                    "base" => "Base",
                    "veryLow" => "VeryLow",
                    "mid" => "Mid",
                    "high" => "High",
                    "jumpdest" => "JumpDest",
                    _ => throw new ExtractionException($"Unknown gas class {seed.GasClass}."),
                }];
            opcodes.Add(new(seed.Name, seed.Instruction, opcodeByte, seed.HandlerBody, seed.HandlerRoot,
                seed.GasClass, fixedGas, seed.DynamicGas, seed.MemoryAccess, seed.ExitKind, seed.StackInputs,
                seed.StackGrowth, seed.ImmediateWidth, seed.Activation));
        }
        if (opcodes.Count != 84) throw new ExtractionException($"Expected 84 selected opcodes but found {opcodes.Count}.");
        return opcodes;
    }

    private static string ExpectedDispatch(OpcodeSeed seed) => seed.HandlerRoot switch
    {
        "ordinary" => $"OpcodeHandler<{seed.HandlerBody},TTracingInst,TCancelable>()",
        "terminating" => $"TerminatingOpcodeHandler<{seed.HandlerBody},TTracingInst,TCancelable>()",
        "jumpIf" => "JumpIfOpcodeHandler<TTracingInst,TCancelable>()",
        _ => throw new ExtractionException($"Unknown handler root {seed.HandlerRoot}."),
    };

    private static Dictionary<string, string> ExtractDispatchAssignments(MethodDeclarationSyntax generator)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (AssignmentExpressionSyntax assignment in generator.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            string left = Canonical(assignment.Left);
            const string prefix = "lookup[(int)Instruction.";
            if (!left.StartsWith(prefix, StringComparison.Ordinal) || !left.EndsWith(']')) continue;
            string instruction = left[prefix.Length..^1];
            counts.TryGetValue(instruction, out int count);
            counts[instruction] = count + 1;
            result[instruction] = Canonical(assignment.Right);
        }
        foreach (OpcodeSeed seed in Seeds)
            if (counts.GetValueOrDefault(seed.Instruction) != 1)
                throw new ExtractionException($"Instruction.{seed.Instruction} must have exactly one dispatch assignment; found {counts.GetValueOrDefault(seed.Instruction)}.");
        return result;
    }

    private static IReadOnlyList<OpcodeSpecialization> BuildSpecializations(IReadOnlyList<OpcodeDescriptor> opcodes)
    {
        List<OpcodeSpecialization> result = new(opcodes.Count * DispatchTables.Length);
        foreach (OpcodeDescriptor opcode in opcodes)
        {
            foreach (DispatchTableBinding table in DispatchTables)
                result.Add(ExpectedSpecialization(opcode, table));
        }
        ValidateSpecializations(opcodes, result);
        return result;
    }

    private static OpcodeSpecialization ExpectedSpecialization(OpcodeDescriptor opcode, DispatchTableBinding table)
    {
        string closedHandlerBody = opcode.HandlerBody
            .Replace("TTracingInst", table.TracingFlag, StringComparison.Ordinal)
            .Replace("TGasPolicy", "EthereumGasPolicy", StringComparison.Ordinal);
        string closedRoot = opcode.HandlerRoot switch
        {
            "ordinary" => $"VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<{closedHandlerBody},{table.TracingFlag},{table.CancelableFlag},OnFlag>",
            "terminating" => $"VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<{closedHandlerBody},{table.TracingFlag},{table.CancelableFlag},OffFlag>",
            "jumpIf" => $"VirtualMachine<EthereumGasPolicy>.ExecuteJumpIfOpcode<{table.TracingFlag},{table.CancelableFlag}>",
            _ => throw new ExtractionException($"Unknown handler root {opcode.HandlerRoot}."),
        };
        if (closedRoot.Contains("TTracingInst", StringComparison.Ordinal) ||
            closedRoot.Contains("TGasPolicy", StringComparison.Ordinal))
            throw new ExtractionException($"Opcode {opcode.Instruction} specialization {table.Name} is not closed: {closedRoot}.");
        return new(opcode.Name, table.Name, table.TracingFlag, table.CancelableFlag, closedRoot);
    }

    internal static void ValidateSpecializations(
        IReadOnlyList<OpcodeDescriptor> opcodes,
        IReadOnlyList<OpcodeSpecialization> specializations)
    {
        Dictionary<string, OpcodeDescriptor> descriptors = opcodes.ToDictionary(static opcode => opcode.Name, StringComparer.Ordinal);
        Dictionary<string, DispatchTableBinding> tables = DispatchTables.ToDictionary(static table => table.Name, StringComparer.Ordinal);
        HashSet<(string Opcode, string Table)> keys = [];
        foreach (OpcodeSpecialization specialization in specializations)
        {
            if (!keys.Add((specialization.Opcode, specialization.DispatchTable)))
                throw new ExtractionException($"Duplicate specialization {specialization.Opcode}/{specialization.DispatchTable}.");
            if (!descriptors.TryGetValue(specialization.Opcode, out OpcodeDescriptor? opcode) ||
                !tables.TryGetValue(specialization.DispatchTable, out DispatchTableBinding? table))
                throw new ExtractionException($"Unknown specialization key {specialization.Opcode}/{specialization.DispatchTable}.");
            OpcodeSpecialization expected = ExpectedSpecialization(opcode, table);
            if (specialization != expected)
                throw new ExtractionException($"Specialization {specialization.Opcode}/{specialization.DispatchTable} does not match its exact closed production root.");
        }
        int expectedCount = opcodes.Count * DispatchTables.Length;
        if (specializations.Count != expectedCount || keys.Count != expectedCount)
            throw new ExtractionException($"Expected {expectedCount} exact closed roots but found {specializations.Count}.");
    }

    internal static IrDocument DeserializeIr(byte[] bytes)
    {
        if (bytes is null)
            throw new ExtractionException("Serialized memory/control opcode IR input is null.");
        try
        {
            IrDocument document = JsonSerializer.Deserialize<IrDocument>(bytes, JsonOptions)
                ?? throw new ExtractionException("Serialized memory/control opcode IR is empty.");
            ValidateIr(document);
            return document;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Serialized memory/control opcode IR is malformed or contains unknown members: {exception.Message}");
        }
    }

    internal static void ValidateIr(IrDocument document)
    {
        if (document is null || document.Opcodes is null || document.Specializations is null ||
            document.ForkLineage is null || document.OpenExtractionObligations is null ||
            document.Opcodes.Any(static opcode => opcode is null || opcode.Name is null ||
                opcode.Instruction is null || opcode.HandlerBody is null || opcode.HandlerRoot is null ||
                opcode.GasClass is null || opcode.DynamicGas is null || opcode.MemoryAccess is null ||
                opcode.ExitKind is null || opcode.Activation is null) ||
            document.Specializations.Any(static specialization => specialization is null ||
                specialization.Opcode is null || specialization.DispatchTable is null ||
                specialization.TracingFlag is null || specialization.CancelableFlag is null ||
                specialization.ClosedRoot is null) ||
            document.ForkLineage.Any(static fork => fork is null) ||
            document.OpenExtractionObligations.Any(static obligation => obligation is null))
            throw new ExtractionException("The round-tripped memory/control opcode IR contains null collections, entries, or fields.");

        if (document.SchemaVersion != SchemaVersion || document.ExtractorVersion != ExtractorVersion ||
            document.Root != Root || document.GasPolicy != "Nethermind.Evm.GasPolicy.EthereumGasPolicy" ||
            document.Fork != "Nethermind.Specs.Forks.Amsterdam" || document.CopyWordGas != 3 ||
            document.MemoryLinearGas != 3)
            throw new ExtractionException("The serialized memory/control opcode IR header, root, fork, or gas constants changed.");

        OpcodeDescriptor[] expectedOpcodes = Seeds.Select(static seed => new OpcodeDescriptor(
            seed.Name, seed.Instruction, seed.ExpectedByte, seed.HandlerBody, seed.HandlerRoot,
            seed.GasClass, ExpectedFixedGas(seed.GasClass), seed.DynamicGas, seed.MemoryAccess,
            seed.ExitKind, seed.StackInputs, seed.StackGrowth, seed.ImmediateWidth, seed.Activation)).ToArray();
        if (!document.Opcodes.SequenceEqual(expectedOpcodes))
            throw new ExtractionException("The serialized memory/control opcode descriptors changed.");

        OpcodeSpecialization[] expectedSpecializations = expectedOpcodes
            .SelectMany(static opcode => DispatchTables.Select(table => ExpectedSpecialization(opcode, table)))
            .ToArray();
        ValidateSpecializations(document.Opcodes, document.Specializations);
        if (!document.Specializations.SequenceEqual(expectedSpecializations))
            throw new ExtractionException("The serialized memory/control specialization order or roots changed.");
        if (!document.ForkLineage.SequenceEqual(ForkLineage, StringComparer.Ordinal))
            throw new ExtractionException("The serialized memory/control fork lineage changed.");
        if (!document.OpenExtractionObligations.SequenceEqual(ExpectedOpenExtractionObligations, StringComparer.Ordinal))
            throw new ExtractionException("The serialized memory/control extraction obligations changed.");
    }

    private static int ExpectedFixedGas(string gasClass) => gasClass switch
    {
        "zero" => 0,
        "base" => 2,
        "veryLow" => 3,
        "mid" => 8,
        "high" => 10,
        "jumpdest" => 1,
        _ => throw new ExtractionException($"Unknown gas class {gasClass}."),
    };

    private static void ValidateStandardReachability(Dictionary<string, SourceFile> sources)
    {
        string flags = Canonical(sources["src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs"].Syntax);
        Require(flags, "publicconstboolConstTracing=true;", "standard tracing capability");
        Require(flags, "publicstaticboolTracing(boolisTracing)=>isTracing;", "standard tracing selection");
        Require(flags, "publicstaticboolCancelable(booltracerIsCancelable)=>tracerIsCancelable;", "standard cancellation selection");

        TypeDeclarationSyntax standardOwner = FindOwner(sources[VirtualMachineStandardPath].Syntax, "VirtualMachine", 1);
        Require(Canonical(FindMethod(standardOwner, "GetOpcodeTable", 0, 0)),
            "_opcodeTablesBySpec.GetValue(Spec,static_=>newOpcodeTable())", "standard per-spec opcode table selection");
        Require(Canonical(FindMethod(standardOwner, "ShouldRefreshOpcodes", 0, 0)),
            "Interlocked.Increment(ref_txCount)%OpcodeRefreshInterval", "standard opcode table refresh path");

        TypeDeclarationSyntax dispatchOwner = FindOwner(sources[DispatchPath].Syntax, "VirtualMachine", 1);
        string opcodeTable = Canonical(FindNestedType(dispatchOwner, "OpcodeTable", 0));
        Require(opcodeTable,
            "refTTracingInst.IsActive?ref(TCancelable.IsActive?refTracedCancelable:refTraced):ref(TCancelable.IsActive?refNoTraceCancelable:refNoTrace)",
            "four live tracing/cancellation table bindings");
        string prepare = Canonical(FindMethod(dispatchOwner, "PrepareOpcodes", 2, 0));
        Require(prepare, "_opcodeHandlers=table.GetHandlers<TTracingInst,TCancelable>(spec);", "transaction table selection");

        string vm = Canonical(sources["src/Nethermind/Nethermind.Evm/VirtualMachine.cs"].Syntax);
        Require(vm, "):VirtualMachine<EthereumGasPolicy>(blockHashProvider,specProvider,logManager),IVirtualMachine", "Ethereum gas-policy closure");
        string module = Canonical(sources["src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs"].Syntax);
        Require(module, ".AddScoped<IVirtualMachine,EthereumVirtualMachine>()", "mainnet block-processing DI registration");

        TypeDeclarationSyntax handlers = FindOwner(sources[OpcodeHandlersPath].Syntax, "VirtualMachine", 1);
        Require(Canonical(FindMethod(handlers, "OpcodeHandler", 3, 0)),
            "&ExecuteOpcode<TOpcode,TTracingInst,TCancelable,OnFlag>", "ordinary handler root");
        Require(Canonical(FindMethod(handlers, "TerminatingOpcodeHandler", 3, 0)),
            "&ExecuteOpcode<TOpcode,TTracingInst,TCancelable,OffFlag>", "terminating handler root");
        Require(Canonical(FindMethod(handlers, "JumpIfOpcodeHandler", 2, 0)),
            "&ExecuteJumpIfOpcode<TTracingInst,TCancelable>", "JUMPI handler root");
    }

    private static void ValidateForkReachability(Dictionary<string, SourceFile> sources)
    {
        CompilationUnitSyntax namedReleaseSyntax = sources[NamedReleaseSpecPath].Syntax;
        TypeDeclarationSyntax namedRelease = FindOwner(namedReleaseSyntax, "NamedReleaseSpec", 0);
        Require(Canonical(namedRelease), "Parent=parent;ReplayAncestors(this);", "named fork construction replay");
        Require(Canonical(FindMethod(namedRelease, "ReplayAncestors", 0, 1)),
            "ReplayAncestors(fork.Parent);fork.Apply(this);", "root-to-leaf fork replay");
        TypeDeclarationSyntax genericRelease = FindOwner(namedReleaseSyntax, "NamedReleaseSpec", 1);
        Require(Canonical(genericRelease), "publicstaticNamedReleaseSpecInstance{get;}=newTSelf();",
            "per-fork singleton construction");

        for (int index = 0; index < ForkLineage.Length; index++)
        {
            string name = ForkLineage[index];
            AdmissionSpec spec = AdmissionSpecs.Single(item => item.ResourceStem == "Fork" + name);
            TypeDeclarationSyntax fork = (TypeDeclarationSyntax)FindTopLevelType(sources[spec.SourcePath].Syntax, name, 0);
            if (index == 0)
            {
                Require(Canonical(fork.BaseList!), "NamedReleaseSpec<Olympic>(null)", "Olympic fork root");
            }
            else
            {
                string parent = ForkLineage[index - 1];
                Require(Canonical(fork.BaseList!), $"NamedReleaseSpec<{name}>({parent}.Instance)", $"{name} fork parent");
            }
        }
        string byzantium = Canonical(FindTopLevelType(
            sources[AdmissionSpecs.Single(static item => item.ResourceStem == "ForkByzantium").SourcePath].Syntax, "Byzantium", 0));
        Require(byzantium, "spec.IsEip140Enabled=true;", "EIP-140 activation");
        Require(byzantium, "spec.IsEip211Enabled=true;", "EIP-211 activation");
        string shanghai = Canonical(FindTopLevelType(
            sources[AdmissionSpecs.Single(static item => item.ResourceStem == "ForkShanghai").SourcePath].Syntax, "Shanghai", 0));
        Require(shanghai, "spec.IsEip3855Enabled=true;", "EIP-3855 activation");
        string cancun = Canonical(FindTopLevelType(
            sources[AdmissionSpecs.Single(static item => item.ResourceStem == "ForkCancun").SourcePath].Syntax, "Cancun", 0));
        Require(cancun, "spec.IsEip5656Enabled=true;", "EIP-5656 activation");
    }

    private static Dictionary<string, int> ReadGasConstants(CompilationUnitSyntax syntax)
    {
        TypeDeclarationSyntax owner = FindOwner(syntax, "GasCostOf", 0);
        Dictionary<string, int> expected = new(StringComparer.Ordinal)
        {
            ["Base"] = 2, ["VeryLow"] = 3, ["Mid"] = 8, ["High"] = 10,
            ["JumpDest"] = 1, ["Memory"] = 3,
        };
        Dictionary<string, int> result = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, int> item in expected)
        {
            FieldDeclarationSyntax field = FindField(owner, item.Key);
            ExpressionSyntax expression = field.Declaration.Variables.Single(variable => variable.Identifier.ValueText == item.Key)
                .Initializer?.Value ?? throw new ExtractionException($"GasCostOf.{item.Key} has no initializer.");
            if (expression is not LiteralExpressionSyntax literal ||
                Convert.ToInt32(literal.Token.Value, CultureInfo.InvariantCulture) != item.Value)
                throw new ExtractionException($"GasCostOf.{item.Key} must remain {item.Value}.");
            result.Add(item.Key, item.Value);
        }
        return result;
    }

    private static Dictionary<string, int> ReadInstructionValues(CompilationUnitSyntax syntax)
    {
        EnumDeclarationSyntax instruction = (EnumDeclarationSyntax)FindTopLevelType(syntax, "Instruction", 0);
        Dictionary<string, int> result = new(StringComparer.Ordinal);
        foreach (EnumMemberDeclarationSyntax member in instruction.Members)
        {
            ExpressionSyntax? expression = member.EqualsValue?.Value;
            if (expression is null) continue;
            string text = expression.ToString();
            int value = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToInt32(text[2..], 16)
                : int.Parse(text, CultureInfo.InvariantCulture);
            result.Add(member.Identifier.ValueText, value);
        }
        return result;
    }

    private static Dictionary<string, SourceFile> LoadSources(string root)
    {
        Dictionary<string, SourceFile> result = new(StringComparer.Ordinal);
        foreach (string relativePath in SourcePaths)
        {
            string fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            byte[] bytes = File.ReadAllBytes(fullPath);
            CompilationUnitSyntax syntax = CSharpSyntaxTree.ParseText(
                Encoding.UTF8.GetString(bytes), ParseOptions, relativePath).GetCompilationUnitRoot();
            Diagnostic? error = syntax.GetDiagnostics().FirstOrDefault(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            if (error is not null) throw new ExtractionException(error.ToString());
            foreach (UsingDirectiveSyntax directive in syntax.DescendantNodes().OfType<UsingDirectiveSyntax>())
                if (directive.Alias is not null)
                    throw new ExtractionException($"Using aliases are not admitted in {relativePath}.");
            string[] directives = syntax.DescendantTrivia(descendIntoTrivia: true)
                .Where(static trivia => trivia.IsDirective)
                .Select(static trivia => trivia.ToString().Trim())
                .ToArray();
            bool admittedDirectives = relativePath switch
            {
                "src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs" =>
                    directives.SequenceEqual(["#if ZK_EVM", "#else", "#endif"], StringComparer.Ordinal),
                "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.cs" =>
                    directives.SequenceEqual(["#if ZK_EVM", "#else", "#endif", "#if ZK_EVM", "#endif"], StringComparer.Ordinal),
                "src/Nethermind/Nethermind.Evm/VirtualMachine.cs" =>
                    directives.SequenceEqual([
                        "#pragma warning disable IDE0063 // Cannot simplify: the `goto Failure` above jumps past this scope, which a `using` declaration would forbid (CS8648)",
                        "#pragma warning restore IDE0063",
                    ], StringComparer.Ordinal),
                _ => directives.Length == 0,
            };
            if (!admittedDirectives)
                throw new ExtractionException($"Preprocessor directives are not admitted in {relativePath}.");
            result.Add(relativePath, new(relativePath, fullPath, bytes, syntax));
        }
        return result;
    }

    private static RawSourceIdentity LoadBuildTargets(string root)
    {
        string fullPath = Path.Combine(root, BuildTargetsPath.Replace('/', Path.DirectorySeparatorChar));
        string sha256 = Hash(File.ReadAllBytes(fullPath));
        if (sha256 != BuildTargetsSha256)
            throw new ExtractionException($"Unadmitted standard/zkEVM build-target content; expected SHA-256 {BuildTargetsSha256}.");
        return new(BuildTargetsPath, sha256);
    }

    private static void ValidateBuildFlavor(string root)
    {
        string source = File.ReadAllText(Path.Combine(root, BuildTargetsPath.Replace('/', Path.DirectorySeparatorChar)));
        Require(source, "<ItemGroup Condition=\"'$(EnableZkEvm)' == 'true'\">", "zkEVM source exclusion group");
        Require(source, "<Compile Remove=\"**/*.std.cs\" />", "standard-source zkEVM exclusion");
        Require(source, "<ItemGroup Condition=\"'$(EnableZkEvm)' != 'true'\">", "standard source selection group");
        Require(source, "<Compile Remove=\"**/*.zkevm.cs\" />", "zkEVM-source standard exclusion");
    }

    private static List<AdmissionIdentity> AdmitReviewedSyntax(Dictionary<string, SourceFile> sources,
        IReadOnlyDictionary<string, string>? overrides)
    {
        List<AdmissionIdentity> identities = [];
        Assembly assembly = typeof(MemoryControlOpcodeProfile).Assembly;
        foreach (AdmissionSpec spec in AdmissionSpecs)
        {
            string template;
            if (overrides is not null && overrides.TryGetValue(spec.ResourceStem, out string? replacement))
            {
                template = replacement;
            }
            else
            {
                string resource = $"Nethermind.Evm.Lean.MemoryControlOpcodeExtractor.Admission.{spec.ResourceStem}.cs.txt";
                using Stream stream = assembly.GetManifestResourceStream(resource)
                    ?? throw new ExtractionException($"Missing admission template {resource}.");
                using StreamReader reader = new(stream);
                template = reader.ReadToEnd();
            }
            CompilationUnitSyntax expected = ParseTemplate(template, spec.ResourceStem);
            SyntaxNode actualContainer = spec.OwnerName is null
                ? sources[spec.SourcePath].Syntax
                : FindOwner(sources[spec.SourcePath].Syntax, spec.OwnerName, spec.OwnerGenericArity);
            SyntaxNode expectedContainer = spec.OwnerName is null
                ? expected
                : FindOwner(expected, spec.OwnerName, spec.OwnerGenericArity);
            foreach (MemberSelector selector in spec.Members)
            {
                MemberDeclarationSyntax actual = FindSelectedMember(actualContainer, selector);
                MemberDeclarationSyntax admitted = FindSelectedMember(expectedContainer, selector);
                if (!EquivalentTokens(actual, admitted))
                    throw new ExtractionException($"Unadmitted complete syntax: {spec.ResourceStem}:{SelectorKey(selector)}.");
                identities.Add(new(
                    AdmissionKey(spec, selector),
                    Hash(Encoding.UTF8.GetBytes(Canonical(admitted))),
                    Hash(Encoding.UTF8.GetBytes(Canonical(actual)))));
            }
        }
        ValidateAdmissionIdentities(identities);
        return identities;
    }

    internal static void ValidateAdmissionIdentities(IReadOnlyList<AdmissionIdentity> identities)
    {
        if (identities is null || identities.Any(static identity => identity is null ||
                string.IsNullOrWhiteSpace(identity.Key) || identity.TemplateSha256 is null ||
                identity.SourceSyntaxSha256 is null))
            throw new ExtractionException("Admission identities contain null or empty values.");
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (AdmissionIdentity identity in identities)
            if (!keys.Add(identity.Key))
                throw new ExtractionException($"Duplicate owner-qualified admission identity {identity.Key}.");
    }

    private static string AdmissionKey(AdmissionSpec spec, MemberSelector selector)
    {
        string owner = spec.OwnerName is null
            ? "compilation-unit/0"
            : $"{spec.OwnerName}/{spec.OwnerGenericArity}";
        return $"{spec.SourcePath}:{spec.Namespace}:{spec.ResourceStem}:{owner}:{SelectorKey(selector)}";
    }

    private static void ValidateNoCompetingMembers(string root, Dictionary<string, SourceFile> sources)
    {
        HashSet<string> known = sources.Values.Select(static source => Path.GetFullPath(source.FullPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] directories =
        [
            "src/Nethermind/Nethermind.Evm", "src/Nethermind/Nethermind.Core",
            "src/Nethermind/Nethermind.Init", ForkDirectory,
        ];
        foreach (string directory in directories)
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(root, directory), "*.cs", SearchOption.AllDirectories))
            {
                string portablePath = file.Replace(Path.DirectorySeparatorChar, '/');
                if (known.Contains(Path.GetFullPath(file)) || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    portablePath.EndsWith(".zkevm.cs", StringComparison.OrdinalIgnoreCase) ||
                    portablePath.Contains("/zkevm/", StringComparison.OrdinalIgnoreCase)) continue;
                CompilationUnitSyntax syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(file), ParseOptions).GetCompilationUnitRoot();
                foreach (AdmissionSpec spec in AdmissionSpecs)
                {
                    if (spec.OwnerName is null) continue;
                    foreach (TypeDeclarationSyntax owner in syntax.DescendantNodesAndSelf().OfType<TypeDeclarationSyntax>())
                    {
                        if (owner.Identifier.ValueText != spec.OwnerName || GenericArity(owner) != spec.OwnerGenericArity) continue;
                        foreach (MemberSelector selector in spec.Members)
                        {
                            bool competes = selector.Kind switch
                            {
                                "method" => owner.Members.OfType<MethodDeclarationSyntax>()
                                    .Any(method => method.Identifier.ValueText == selector.Name),
                                "type" => owner.Members.OfType<BaseTypeDeclarationSyntax>()
                                    .Any(type => type.Identifier.ValueText == selector.Name),
                                "field" => owner.Members.OfType<FieldDeclarationSyntax>()
                                    .Any(field => field.Declaration.Variables.Any(variable => variable.Identifier.ValueText == selector.Name)),
                                "property" => owner.Members.OfType<PropertyDeclarationSyntax>()
                                    .Any(property => property.Identifier.ValueText == selector.Name),
                                _ => false,
                            };
                            if (competes)
                                throw new ExtractionException($"Competing selected declaration in {file}: {spec.OwnerName}.{SelectorKey(selector)}.");
                        }
                    }
                }
            }
        }
    }

    private static CompilationUnitSyntax ParseTemplate(string template, string stem)
    {
        CompilationUnitSyntax syntax = CSharpSyntaxTree.ParseText(template, ParseOptions, stem + ".cs.txt").GetCompilationUnitRoot();
        Diagnostic? error = syntax.GetDiagnostics().FirstOrDefault(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (error is not null) throw new ExtractionException($"Invalid admission template {stem}: {error}.");
        foreach (SyntaxTrivia trivia in syntax.DescendantTrivia(descendIntoTrivia: true))
            if (trivia.IsDirective) throw new ExtractionException($"Admission template {stem} contains a directive.");
        return syntax;
    }

    private static MemberDeclarationSyntax FindSelectedMember(SyntaxNode container, MemberSelector selector)
    {
        MemberDeclarationSyntax[] matches = DirectMembers(container)
            .Where(member => Matches(member, selector)).ToArray();
        if (matches.Length != 1)
            throw new ExtractionException($"Expected exactly one {SelectorKey(selector)} member; found {matches.Length}.");
        return matches[0];
    }

    private static IEnumerable<MemberDeclarationSyntax> DirectMembers(SyntaxNode container) => container switch
    {
        CompilationUnitSyntax unit => unit.Members.SelectMany(static member => member is BaseNamespaceDeclarationSyntax ns ? ns.Members : [member]),
        TypeDeclarationSyntax type => type.Members,
        _ => throw new ExtractionException($"Unsupported admission container {container.Kind()}."),
    };

    private static bool Matches(MemberDeclarationSyntax member, MemberSelector selector) => selector.Kind switch
    {
        "method" when member is MethodDeclarationSyntax method =>
            method.Identifier.ValueText == selector.Name &&
            (method.TypeParameterList?.Parameters.Count ?? 0) == selector.GenericArity &&
            method.ParameterList.Parameters.Count == selector.ParameterCount &&
            (selector.ParameterTypes.Length == 0 || ParameterTypes(method) == selector.ParameterTypes),
        "type" when member is BaseTypeDeclarationSyntax type =>
            type.Identifier.ValueText == selector.Name && GenericArity(type) == selector.GenericArity,
        "field" when member is FieldDeclarationSyntax field =>
            field.Declaration.Variables.Any(variable => variable.Identifier.ValueText == selector.Name),
        "property" when member is PropertyDeclarationSyntax property => property.Identifier.ValueText == selector.Name,
        _ => false,
    };

    private static string ParameterTypes(MethodDeclarationSyntax method) => string.Join(",",
        method.ParameterList.Parameters.Select(static parameter => Canonical(parameter.Type!)));

    private static TypeDeclarationSyntax FindOwner(CompilationUnitSyntax syntax, string name, int genericArity)
    {
        TypeDeclarationSyntax[] matches = syntax.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(type => type.Identifier.ValueText == name && GenericArity(type) == genericArity).ToArray();
        if (matches.Length != 1) throw new ExtractionException($"Expected one owner {name}/{genericArity}; found {matches.Length}.");
        return matches[0];
    }

    private static BaseTypeDeclarationSyntax FindTopLevelType(CompilationUnitSyntax syntax, string name, int genericArity)
    {
        BaseTypeDeclarationSyntax[] matches = DirectMembers(syntax).OfType<BaseTypeDeclarationSyntax>()
            .Where(type => type.Identifier.ValueText == name && GenericArity(type) == genericArity).ToArray();
        if (matches.Length != 1) throw new ExtractionException($"Expected one top-level type {name}/{genericArity}; found {matches.Length}.");
        return matches[0];
    }

    private static TypeDeclarationSyntax FindNestedType(TypeDeclarationSyntax owner, string name, int genericArity) =>
        (TypeDeclarationSyntax)FindSelectedMember(owner, T(name, genericArity));

    private static MethodDeclarationSyntax FindMethod(TypeDeclarationSyntax owner, string name, int genericArity, int parameterCount) =>
        (MethodDeclarationSyntax)FindSelectedMember(owner, M(name, genericArity, parameterCount));

    private static FieldDeclarationSyntax FindField(TypeDeclarationSyntax owner, string name) =>
        (FieldDeclarationSyntax)FindSelectedMember(owner, F(name));

    private static int GenericArity(BaseTypeDeclarationSyntax type) => type is TypeDeclarationSyntax declaration
        ? declaration.TypeParameterList?.Parameters.Count ?? 0
        : 0;

    private static bool EquivalentTokens(SyntaxNode left, SyntaxNode right)
    {
        SyntaxToken[] leftTokens = left.DescendantTokens(descendIntoTrivia: true).ToArray();
        SyntaxToken[] rightTokens = right.DescendantTokens(descendIntoTrivia: true).ToArray();
        if (leftTokens.Length != rightTokens.Length) return false;
        for (int index = 0; index < leftTokens.Length; index++)
            if (leftTokens[index].RawKind != rightTokens[index].RawKind || leftTokens[index].Text != rightTokens[index].Text)
                return false;
        return true;
    }

    private static List<SourceIdentity> BuildSourceIdentities(Dictionary<string, SourceFile> sources) => sources.Values
        .OrderBy(static source => source.Path, StringComparer.Ordinal)
        .Select(static source => new SourceIdentity(
            source.Path,
            Hash(source.Bytes),
            Hash(Encoding.UTF8.GetBytes(Canonical(source.Syntax)))))
        .ToList();

    private static byte[] Serialize<T>(T value) => Utf8WithoutBom.GetBytes(
        JsonSerializer.Serialize(value, JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Canonical(SyntaxNode node) => string.Concat(
        node.DescendantTokens(descendIntoTrivia: false).Select(static token => token.Text));

    private static void Require(string source, string required, string description)
    {
        if (!source.Contains(required, StringComparison.Ordinal))
            throw new ExtractionException($"Rejected {description}; expected '{required}'.");
    }

    private static AdmissionSpec Spec(string path, string stem, string ns, string? owner, int ownerArity,
        params MemberSelector[] members) => new(path, stem, ns, owner, ownerArity, members);

    private static AdmissionSpec Spec(string path, string stem, string ns, string? owner, int ownerArity,
        IReadOnlyList<MemberSelector> members) => new(path, stem, ns, owner, ownerArity, members);

    private static MemberSelector M(string name, int genericArity, int parameterCount, string parameterTypes = "") =>
        new("method", name, genericArity, parameterCount, parameterTypes);

    private static MemberSelector T(string name, int genericArity = 0) => new("type", name, genericArity);

    private static MemberSelector F(string name) => new("field", name);

    private static MemberSelector P(string name) => new("property", name);

    private static string SelectorKey(MemberSelector selector) =>
        $"{selector.Kind}:{selector.Name}/{selector.GenericArity}/{selector.ParameterCount}/{selector.ParameterTypes}";
}
