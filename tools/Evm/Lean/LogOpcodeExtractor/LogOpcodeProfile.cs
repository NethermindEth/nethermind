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

namespace Nethermind.Evm.Lean.LogOpcodeExtractor;

internal static class LogOpcodeProfile
{
    internal const int SchemaVersion = 1;
    internal const string ExtractorVersion = "1.0.0";
    internal const string IrFileName = "LogOpcodeKernel.ir.json";
    internal const string ManifestFileName = "LogOpcodeKernel.source-manifest.json";
    internal const string DefaultLeanRelativePath = "tools/Evm/Lean/Eip803x/Generated/LogOpcodeKernel.lean";

    private const string GasCostPath = "src/Nethermind/Nethermind.Core/GasCostOf.cs";
    private const string InstructionPath = "src/Nethermind/Nethermind.Evm/Instruction.cs";
    private const string StackPath = "src/Nethermind/Nethermind.Evm/EvmStack.cs";
    private const string StackStandardPath = "src/Nethermind/Nethermind.Evm/EvmStack.std.cs";
    private const string MemoryPath = "src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs";
    private const string GasPolicyPath = "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs";
    private const string InterfaceGasPolicyPath = "src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs";
    private const string InstructionLogPath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Stack.cs";
    private const string OpcodeHandlersPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs";
    private const string DispatchPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs";
    private const string VirtualMachinePath = "src/Nethermind/Nethermind.Evm/VirtualMachine.cs";
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

    private static readonly string[] EffectOrder =
    [
        "traceStartIfInstructionTracing",
        "incrementProgramCounter",
        "staticGuard",
        "popOffsetLengthAtomically",
        "readTopicCount",
        "prepareMemoryAndInstallSize",
        "chargeMemoryExpansion",
        "chargeLogEmission",
        "loadPayload",
        "allocateTopicArray",
        "popTopicsSequentially",
        "copyPayload",
        "constructLog",
        "appendJournal",
        "reportLogIfEnabled",
        "traceEndIfInstructionTracing",
        "dispatchContinue",
    ];

    private static readonly OpcodeSeed[] Seeds =
    [
        new("log0", "LOG0", 0xa0, 0, "LogOpcode<EvmInstructions.Op0>"),
        new("log1", "LOG1", 0xa1, 1, "LogOpcode<EvmInstructions.Op1>"),
        new("log2", "LOG2", 0xa2, 2, "LogOpcode<EvmInstructions.Op2>"),
        new("log3", "LOG3", 0xa3, 3, "LogOpcode<EvmInstructions.Op3>"),
        new("log4", "LOG4", 0xa4, 4, "LogOpcode<EvmInstructions.Op4>"),
    ];

    private static readonly string[] ForkLineage =
    [
        "Olympic", "Frontier", "Homestead", "Dao", "TangerineWhistle", "SpuriousDragon",
        "Byzantium", "Constantinople", "ConstantinopleFix", "Istanbul", "MuirGlacier", "Berlin",
        "London", "ArrowGlacier", "GrayGlacier", "Paris", "Shanghai", "Cancun", "Prague", "Osaka",
        "BPO1", "BPO2", "Amsterdam",
    ];

    private static readonly string[] OpenExtractionObligations =
    [
        "Roslyn syntax admission does not prove C# compilation, CLR/JIT execution, function-pointer dispatch, or tail-call behavior.",
        "The proof assumes the admitted unsafe big-endian stack operations, UInt256 conversions, Address representation, and Hash256 span constructor implement the shared Lean word values.",
        "The proof models logical EVM memory bytes and size; backing-array allocation, pooling, initialization, aliasing, and allocation failure remain provider and CLR obligations.",
        "The log journal and optional ReportLog callback are modeled as ordered append-only observations; callback exceptions, tracer internals, frame rollback, receipt construction, and Bloom accumulation remain outside this opcode step.",
        "Cancellation polling, opcode counters, instruction-trace payloads, exceptional-frame settlement, and database persistence remain outside this projection.",
    ];

    private static readonly AdmissionSpec[] AdmissionSpecs = BuildAdmissionSpecs();

    internal static IReadOnlyList<string> SourcePaths => AdmissionSpecs
        .Select(static spec => spec.SourcePath)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    internal static IReadOnlyList<string> InputPaths => [.. SourcePaths, BuildTargetsPath];

    internal static ExtractionResult Extract(
        string repoRoot,
        string outputDirectory,
        string? leanOutputPath = null,
        IReadOnlyDictionary<string, string>? admissionOverrides = null)
    {
        string root = Path.GetFullPath(repoRoot);
        Dictionary<string, SourceFile> sources = LoadSources(root);
        RawSourceIdentity buildTargets = LoadBuildTargets(root);
        List<AdmissionIdentity> admissions = AdmitReviewedSyntax(sources, admissionOverrides);
        ValidateNoCompetingMembers(root, sources);
        ValidateBuildFlavor(root);
        ValidateProductionClosure(sources);
        ValidateForkReachability(sources);

        Dictionary<string, int> gasConstants = ReadGasConstants(sources[GasCostPath].Syntax);
        IReadOnlyList<OpcodeDescriptor> opcodes = ExtractOpcodes(sources);
        IReadOnlyList<OpcodeSpecialization> specializations = BuildSpecializations(opcodes);
        IrDocument document = new(
            SchemaVersion,
            ExtractorVersion,
            Root,
            "Nethermind.Evm.GasPolicy.EthereumGasPolicy",
            "Nethermind.Specs.Forks.Amsterdam",
            gasConstants["Log"],
            gasConstants["LogTopic"],
            gasConstants["LogData"],
            gasConstants["Memory"],
            512,
            2_147_483_616,
            opcodes,
            specializations,
            ForkLineage,
            OpenExtractionObligations);

        byte[] irBytes = Serialize(document);
        IrDocument serialized = DeserializeIr(irBytes);
        ValidateIr(serialized);

        List<SourceIdentity> identities = BuildSourceIdentities(sources);
        string combinedSourceSha256 = CombinedSourceHash(identities, [buildTargets]);
        byte[] leanBytes = LogOpcodeLeanEmitter.Emit(serialized, Hash(irBytes), combinedSourceSha256);
        SourceManifest manifest = new(
            SchemaVersion,
            ExtractorVersion,
            Hash(irBytes),
            Hash(leanBytes),
            combinedSourceSha256,
            identities,
            [buildTargets],
            admissions);
        byte[] manifestBytes = Serialize(manifest);
        SourceManifest roundTrippedManifest = DeserializeManifest(manifestBytes);
        ValidateManifest(roundTrippedManifest, manifest);

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
        File.WriteAllBytes(manifestPath, manifestBytes);
        File.WriteAllBytes(leanPath, leanBytes);
        return new(irPath, manifestPath, leanPath, opcodes.Count, specializations.Count, admissions.Count);
    }

    internal static IrDocument DeserializeIr(byte[] bytes)
    {
        if (bytes is null)
            throw new ExtractionException("Serialized LOG opcode IR input is null.");
        try
        {
            IrDocument document = JsonSerializer.Deserialize<IrDocument>(bytes, JsonOptions)
                ?? throw new ExtractionException("Serialized LOG opcode IR is empty.");
            ValidateIr(document);
            return document;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Serialized LOG opcode IR is not valid JSON: {exception.Message}");
        }
    }

    internal static SourceManifest DeserializeManifest(byte[] bytes)
    {
        if (bytes is null)
            throw new ExtractionException("Serialized LOG opcode manifest input is null.");
        try
        {
            SourceManifest manifest = JsonSerializer.Deserialize<SourceManifest>(bytes, JsonOptions)
                ?? throw new ExtractionException("Serialized LOG opcode manifest is empty.");
            ValidateManifest(manifest);
            return manifest;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Serialized LOG opcode manifest is not valid JSON: {exception.Message}");
        }
    }

    internal static void ValidateIr(IrDocument document)
    {
        if (document is null)
            throw new ExtractionException("Serialized LOG opcode IR is empty.");
        ValidateIrCollections(document);
        if (document.SchemaVersion != SchemaVersion || document.ExtractorVersion != ExtractorVersion ||
            document.Root != Root || document.GasPolicy != "Nethermind.Evm.GasPolicy.EthereumGasPolicy" ||
            document.Fork != "Nethermind.Specs.Forks.Amsterdam")
            throw new ExtractionException("Serialized LOG opcode IR header is not the admitted profile.");
        if (document.LogBaseGas != 375 || document.LogTopicGas != 375 || document.LogDataByteGas != 8 ||
            document.MemoryLinearGas != 3 || document.MemoryQuadraticDivisor != 512 ||
            document.MaxMemorySize != 2_147_483_616)
            throw new ExtractionException("Serialized LOG opcode IR gas or memory constants changed.");
        if (document.Opcodes.Count != Seeds.Length)
            throw new ExtractionException($"Expected {Seeds.Length} serialized LOG descriptors but found {document.Opcodes.Count}.");

        HashSet<int> bytes = [];
        for (int index = 0; index < Seeds.Length; index++)
        {
            OpcodeSeed seed = Seeds[index];
            OpcodeDescriptor expected = ExpectedDescriptor(seed);
            if (!DescriptorMatches(document.Opcodes[index], expected) ||
                !document.Opcodes[index].EffectOrder.SequenceEqual(EffectOrder, StringComparer.Ordinal))
                throw new ExtractionException($"Serialized descriptor for {seed.Instruction} does not match source-derived semantics.");
            if (!bytes.Add(document.Opcodes[index].OpcodeByte))
                throw new ExtractionException($"Duplicate serialized LOG opcode byte {document.Opcodes[index].OpcodeByte}.");
        }
        ValidateSpecializations(document.Opcodes, document.Specializations);
        if (!document.ForkLineage.SequenceEqual(ForkLineage, StringComparer.Ordinal))
            throw new ExtractionException("Serialized Amsterdam fork lineage changed.");
        if (!document.OpenExtractionObligations.SequenceEqual(OpenExtractionObligations, StringComparer.Ordinal))
            throw new ExtractionException("Serialized LOG opcode IR extraction obligations changed.");
    }

    internal static void ValidateManifest(SourceManifest manifest)
    {
        if (manifest is null || manifest.ExtractorVersion is null || manifest.IrSha256 is null ||
            manifest.LeanSha256 is null || manifest.CombinedSourceSha256 is null || manifest.Sources is null ||
            manifest.RawSources is null || manifest.Admissions is null || manifest.Sources.Any(static source =>
                source is null || source.Path is null || source.Sha256 is null || source.RoslynSyntaxSha256 is null) ||
            manifest.RawSources.Any(static source => source is null || source.Path is null || source.Sha256 is null) ||
            manifest.Admissions.Any(static admission => admission is null || admission.Key is null ||
                admission.TemplateSha256 is null || admission.SourceSyntaxSha256 is null))
            throw new ExtractionException("Serialized LOG opcode manifest contains null collections, entries, or fields.");

        if (manifest.SchemaVersion != SchemaVersion || manifest.ExtractorVersion != ExtractorVersion ||
            !IsSha256(manifest.IrSha256) || !IsSha256(manifest.LeanSha256) ||
            !IsSha256(manifest.CombinedSourceSha256))
            throw new ExtractionException("Serialized LOG opcode manifest header or artifact digests changed.");

        string[] expectedSourcePaths = SourcePaths.OrderBy(static path => path, StringComparer.Ordinal).ToArray();
        if (manifest.Sources.Count != expectedSourcePaths.Length ||
            !manifest.Sources.Select(static source => source.Path).SequenceEqual(expectedSourcePaths, StringComparer.Ordinal) ||
            manifest.Sources.Select(static source => source.Path).Distinct(StringComparer.Ordinal).Count() != manifest.Sources.Count ||
            manifest.Sources.Any(static source => !IsSha256(source.Sha256) || !IsSha256(source.RoslynSyntaxSha256)))
            throw new ExtractionException("Serialized LOG opcode source identities changed or collide.");

        if (manifest.RawSources.Count != 1 || manifest.RawSources[0].Path != BuildTargetsPath ||
            manifest.RawSources.Select(static source => source.Path).Distinct(StringComparer.Ordinal).Count() != manifest.RawSources.Count ||
            manifest.RawSources.Any(static source => !IsSha256(source.Sha256)) ||
            manifest.RawSources[0].Sha256 != BuildTargetsSha256 ||
            manifest.RawSources.Any(raw => manifest.Sources.Any(source => source.Path == raw.Path)))
            throw new ExtractionException("Serialized LOG opcode build identities changed or collide.");

        if (manifest.CombinedSourceSha256 != CombinedSourceHash(manifest.Sources, manifest.RawSources))
            throw new ExtractionException("Serialized LOG opcode combined source fingerprint changed.");

        if (manifest.Admissions.Count != AdmissionSpecs.Sum(static spec => spec.Members.Count))
            throw new ExtractionException("Serialized LOG opcode admission count changed.");
        ValidateAdmissionIdentities(manifest.Admissions);
    }

    internal static void ValidateManifest(SourceManifest actual, SourceManifest expected)
    {
        ValidateManifest(actual);
        ValidateManifest(expected);
        if (!Serialize(actual).AsSpan().SequenceEqual(Serialize(expected)))
            throw new ExtractionException("Serialized LOG opcode manifest does not exactly match extracted lineage.");
    }

    private static void ValidateIrCollections(IrDocument document)
    {
        if (document.ForkLineage is null || document.OpenExtractionObligations is null)
            throw new ExtractionException("Serialized LOG opcode IR contains a null collection.");
        ValidateDescriptorCollection(document.Opcodes, "Serialized LOG opcode IR");
        ValidateSpecializationCollection(document.Specializations, "Serialized LOG opcode IR");
    }

    private static void ValidateDescriptorCollection(
        IReadOnlyList<OpcodeDescriptor>? opcodes,
        string description)
    {
        if (opcodes is null)
            throw new ExtractionException($"{description} contains a null collection.");
        for (int index = 0; index < opcodes.Count; index++)
        {
            OpcodeDescriptor? opcode = opcodes[index];
            if (opcode is null || opcode.Name is null || opcode.Instruction is null || opcode.HandlerBody is null ||
                opcode.EffectOrder is null)
                throw new ExtractionException($"{description} descriptor at index {index} is incomplete.");
        }
    }

    private static void ValidateSpecializationCollection(
        IReadOnlyList<OpcodeSpecialization>? specializations,
        string description)
    {
        if (specializations is null)
            throw new ExtractionException($"{description} contains a null collection.");
        for (int index = 0; index < specializations.Count; index++)
        {
            OpcodeSpecialization? specialization = specializations[index];
            if (specialization is null || specialization.Opcode is null || specialization.DispatchTable is null ||
                specialization.TracingFlag is null || specialization.CancelableFlag is null ||
                specialization.ClosedRoot is null)
                throw new ExtractionException($"{description} specialization at index {index} is incomplete.");
        }
    }

    internal static void ValidateSpecializations(
        IReadOnlyList<OpcodeDescriptor> opcodes,
        IReadOnlyList<OpcodeSpecialization> specializations)
    {
        ValidateDescriptorCollection(opcodes, "LOG opcode specialization validation");
        ValidateSpecializationCollection(specializations, "LOG opcode specialization validation");
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

    private static AdmissionSpec[] BuildAdmissionSpecs()
    {
        List<AdmissionSpec> specs =
        [
            Spec(GasCostPath, "GasCostOf", "GasCostOf", 0, F("Memory")),
            Spec(GasCostPath, "GasCostOfLog", "GasCostOf", 0, F("Log"), F("LogTopic"), F("LogData")),
            Spec("src/Nethermind/Nethermind.Core/TypeFlags.cs", "TypeFlags", null, 0,
                T("IFlag"), T("OffFlag"), T("OnFlag")),
            Spec(InstructionPath, "Instruction", null, 0, T("Instruction")),
            Spec(StackPath, "EvmStackLog", "EvmStack", 0,
                M("ReadUInt256FromSlot", 0, 2, "byte,UInt256"),
                M("ReadMemoryPositionFromSlot", 0, 2, "byte,UInt256"),
                M("PopMemoryPositionAndUInt256", 0, 2, "UInt256,UInt256"),
                M("PopWord256", 0, 1, "Span<byte>")),
            Spec(StackStandardPath, "EvmStackStandardLog", "EvmStack", 0, M("ReadBeWord", 0, 1, "byte")),
            Spec(MemoryPath, "EvmPooledMemory", "EvmPooledMemory", 0,
                F("WordSize"), F("MaxMemorySize"), P("Size"),
                M("CheckMemoryAccessViolation", 0, 4, "UInt256,UInt256,ulong,bool"),
                M("CheckMemoryAccessViolation", 0, 4, "UInt256,ulong,ulong,bool"),
                M("CalculateMemoryCost", 0, 3, "UInt256,UInt256,bool"),
                M("ComputeMemoryExpansionCost", 0, 1), M("UpdateSize", 0, 2)),
            Spec(MemoryPath, "EvmPooledMemoryLog", "EvmPooledMemory", 0,
                M("TryLoad", 0, 3, "UInt256,UInt256,ReadOnlyMemory<byte>"),
                M("GetBackingMemory", 0, 2, "int,int")),
            Spec(InterfaceGasPolicyPath, "IGasPolicy", "IGasPolicy", 1,
                M("UpdateMemoryCost", 0, 4, "TSelf,UInt256,UInt256,EvmPooledMemory")),
            Spec(InterfaceGasPolicyPath, "IGasPolicyLog", "IGasPolicy", 1, M("TryConsumeLogEmission", 0, 3)),
            Spec(GasPolicyPath, "EthereumGasPolicy", "EthereumGasPolicy", 0,
                M("GetRemainingGas", 0, 1), M("ConsumeRaw", 0, 2),
                M("UpdateMemoryCost", 0, 4, "EthereumGasPolicy,UInt256,UInt256,EvmPooledMemory"),
                M("UpdateGas", 0, 2)),
            Spec(GasPolicyPath, "EthereumGasPolicyLog", "EthereumGasPolicy", 0, M("TryConsumeLogEmission", 0, 3)),
            Spec(InstructionLogPath, "InstructionStack", "EvmInstructions", 0,
                T("IOpCount"), T("Op0"), T("Op1"), T("Op2"), T("Op3"), T("Op4")),
            Spec(InstructionLogPath, "InstructionLog", "EvmInstructions", 0, M("InstructionLog", 2, 3)),
            Spec("src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs", "DispatchFlags", null, 0, T("DispatchFlags")),
            Spec(VirtualMachinePath, "EthereumVirtualMachine", null, 0, T("EthereumVirtualMachine")),
            Spec(VirtualMachinePath, "VirtualMachineLog", "VirtualMachine", 1,
                P("TxTracer"), P("VmState"), M("AddLog", 0, 1)),
            Spec(VirtualMachineStandardPath, "VirtualMachineStandard", "VirtualMachine", 1,
                F("_opcodeTablesBySpec"), F("OpcodeRefreshInterval"), F("OpcodeRefreshLimit"), F("_txCount"),
                M("GetOpcodeTable", 0, 0), M("ShouldRefreshOpcodes", 0, 0)),
            Spec(DispatchPath, "VirtualMachineDispatch", "VirtualMachine", 1,
                M("GetOpcodeHandlers", 2, 0), M("PrepareOpcodes", 1, 0), M("PrepareOpcodes", 2, 0),
                T("OpcodeTable"), M("RunDispatchLoop", 2, 3), M("ExecuteOpcode", 4, 5),
                M("ExitCheckedOpcode", 0, 4)),
            Spec(OpcodeHandlersPath, "VirtualMachineOpcodeHandlers", "VirtualMachine", 1,
                T("IOpcodeBody"), M("OpcodeHandler", 3, 0), M("GenerateOpcodeHandlers", 2, 1, "IReleaseSpec")),
            Spec(OpcodeHandlersPath, "LogOpcode", "VirtualMachine", 1, T("LogOpcode", 1)),
            Spec("src/Nethermind/Nethermind.Evm/VmState.cs", "VmStateLog", "VmState", 1,
                P("IsStatic"), P("AccessTracker"), P("Env"), P("Memory")),
            Spec("src/Nethermind/Nethermind.Evm/ExecutionEnvironment.cs", "ExecutionEnvironmentLog", "ExecutionEnvironment", 0,
                P("ExecutingAccount")),
            Spec("src/Nethermind/Nethermind.Evm/StackAccessTracker.cs", "StackAccessTrackerLog", "StackAccessTracker", 0,
                P("Logs")),
            Spec("src/Nethermind/Nethermind.Evm/StackAccessTracker.cs", "TrackingStateLog", "TrackingState", 0,
                P("Logs")),
            Spec("src/Nethermind/Nethermind.Core/Collections/JournalCollection.cs", "JournalCollectionLog", "JournalCollection", 1,
                F("_list"), M("Add", 0, 1)),
            Spec("src/Nethermind/Nethermind.Core/LogEntry.cs", "LogEntry", null, 0, T("LogEntry")),
            Spec("src/Nethermind/Nethermind.Core/Crypto/Hash256.cs", "Hash256Log", "Hash256", 0,
                F("Size"), F("_hash256"), P("Bytes"), C("Hash256", 1, "ReadOnlySpan<byte>")),
            Spec("src/Nethermind/Nethermind.Evm/Tracing/ITxTracer.cs", "ITxTracerLog", "ITxTracer", 0,
                P("IsTracingLogs"), M("ReportLog", 0, 1)),
            Spec("src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs", "BlockProcessingModule", "BlockProcessingModule", 0,
                M("Load", 0, 1, "ContainerBuilder")),
            Spec(NamedReleaseSpecPath, "NamedReleaseSpec", null, 0,
                T("NamedReleaseSpec"), T("NamedReleaseSpec", 1)),
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
            specs.Add(Spec($"{ForkDirectory}/{forkFiles[index]}.cs", "Fork" + ForkLineage[index], null, 0, T(ForkLineage[index])));
        return specs.ToArray();
    }

    private static IReadOnlyList<OpcodeDescriptor> ExtractOpcodes(Dictionary<string, SourceFile> sources)
    {
        Dictionary<string, List<int>> instructionValues = ReadInstructionValues(sources[InstructionPath].Syntax);
        MethodDeclarationSyntax generator = FindMethod(FindOwner(sources[OpcodeHandlersPath].Syntax, "VirtualMachine", 1),
            "GenerateOpcodeHandlers", 2, 1);
        Dictionary<string, string> assignments = ExtractDispatchAssignments(generator);
        List<OpcodeDescriptor> result = new(Seeds.Length);
        HashSet<int> selectedBytes = [];
        foreach (OpcodeSeed seed in Seeds)
        {
            if (!instructionValues.TryGetValue(seed.Instruction, out List<int>? values) || values.Count != 1)
                throw new ExtractionException($"Instruction.{seed.Instruction} is missing or duplicated.");
            int opcodeByte = values[0];
            if (opcodeByte != seed.ExpectedByte)
                throw new ExtractionException($"Instruction.{seed.Instruction} changed from byte {seed.ExpectedByte} to {opcodeByte}.");
            if (!selectedBytes.Add(opcodeByte))
                throw new ExtractionException($"Instruction.{seed.Instruction} duplicates selected opcode byte {opcodeByte}.");
            string[] aliases = instructionValues.Where(item => item.Value.Contains(opcodeByte)).Select(static item => item.Key).ToArray();
            if (aliases.Length != 1)
                throw new ExtractionException($"Instruction.{seed.Instruction} byte {opcodeByte} has aliases: {string.Join(", ", aliases)}.");
            string expectedDispatch = $"OpcodeHandler<{seed.HandlerBody},TTracingInst,TCancelable>()";
            if (!assignments.TryGetValue(seed.Instruction, out string? dispatch) || dispatch != expectedDispatch)
                throw new ExtractionException($"Production dispatch for {seed.Instruction} changed from '{expectedDispatch}' to '{dispatch}'.");
            TypeDeclarationSyntax op = FindNestedType(
                FindOwner(sources[InstructionLogPath].Syntax, "EvmInstructions", 0), $"Op{seed.TopicCount}", 0);
            int topicCount = ReadTopicCount(op);
            if (topicCount != seed.TopicCount)
                throw new ExtractionException($"{op.Identifier.ValueText}.Count changed from {seed.TopicCount} to {topicCount}.");
            result.Add(new(seed.Name, seed.Instruction, opcodeByte, topicCount, seed.HandlerBody, 1, 2, EffectOrder));
        }
        return result;
    }

    private static OpcodeDescriptor ExpectedDescriptor(OpcodeSeed seed) =>
        new(seed.Name, seed.Instruction, seed.ExpectedByte, seed.TopicCount, seed.HandlerBody, 1, 2, EffectOrder);

    private static bool DescriptorMatches(OpcodeDescriptor actual, OpcodeDescriptor expected) =>
        actual.Name == expected.Name &&
        actual.Instruction == expected.Instruction &&
        actual.OpcodeByte == expected.OpcodeByte &&
        actual.TopicCount == expected.TopicCount &&
        actual.HandlerBody == expected.HandlerBody &&
        actual.ProgramCounterDelta == expected.ProgramCounterDelta &&
        actual.HeaderStackInputs == expected.HeaderStackInputs;

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
            counts[instruction] = counts.GetValueOrDefault(instruction) + 1;
            result[instruction] = Canonical(assignment.Right);
            if (Seeds.Any(seed => seed.Instruction == instruction) &&
                (assignment.Parent is not ExpressionStatementSyntax statement || statement.Parent != generator.Body))
                throw new ExtractionException($"Instruction.{instruction} dispatch must be an unconditional top-level statement.");
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
            foreach (DispatchTableBinding table in DispatchTables)
                result.Add(ExpectedSpecialization(opcode, table));
        ValidateSpecializations(opcodes, result);
        return result;
    }

    private static OpcodeSpecialization ExpectedSpecialization(OpcodeDescriptor opcode, DispatchTableBinding table)
    {
        string closedRoot = $"VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<{opcode.HandlerBody},{table.TracingFlag},{table.CancelableFlag},OnFlag>";
        if (closedRoot.Contains("TTracingInst", StringComparison.Ordinal) ||
            closedRoot.Contains("TCancelable", StringComparison.Ordinal) ||
            closedRoot.Contains("TGasPolicy", StringComparison.Ordinal) ||
            closedRoot.Contains("TOpCount", StringComparison.Ordinal))
            throw new ExtractionException($"Opcode {opcode.Instruction} specialization {table.Name} is not closed: {closedRoot}.");
        return new(opcode.Name, table.Name, table.TracingFlag, table.CancelableFlag, closedRoot);
    }

    private static void ValidateProductionClosure(Dictionary<string, SourceFile> sources)
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
        Require(Canonical(FindMethod(dispatchOwner, "PrepareOpcodes", 2, 0)),
            "_opcodeHandlers=table.GetHandlers<TTracingInst,TCancelable>(spec);", "transaction table selection");
        Require(Canonical(FindMethod(dispatchOwner, "ExecuteOpcode", 4, 5)), "pc++;opCodeCount++;",
            "program counter increment before LOG body");

        TypeDeclarationSyntax handlerOwner = FindOwner(sources[OpcodeHandlersPath].Syntax, "VirtualMachine", 1);
        Require(Canonical(FindMethod(handlerOwner, "OpcodeHandler", 3, 0)),
            "&ExecuteOpcode<TOpcode,TTracingInst,TCancelable,OnFlag>", "ordinary LOG handler root");
        TypeDeclarationSyntax logOpcode = FindNestedType(handlerOwner, "LogOpcode", 1);
        Require(Canonical(logOpcode),
            "EvmInstructions.InstructionLog<TGasPolicy,TOpCount>(refstack,refgas,vm)", "LOG wrapper target and arguments");

        string vm = Canonical(sources[VirtualMachinePath].Syntax);
        Require(vm, "):VirtualMachine<EthereumGasPolicy>(blockHashProvider,specProvider,logManager),IVirtualMachine",
            "Ethereum gas-policy closure");
        string module = Canonical(sources["src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs"].Syntax);
        Require(module, ".AddScoped<IVirtualMachine,EthereumVirtualMachine>()", "mainnet block-processing DI registration");

        ValidateInstructionOrder(sources);
        ValidateGasAndMemory(sources);
        ValidateLogBoundary(sources);
    }

    private static void ValidateInstructionOrder(Dictionary<string, SourceFile> sources)
    {
        MethodDeclarationSyntax method = FindMethod(FindOwner(sources[InstructionLogPath].Syntax, "EvmInstructions", 0),
            "InstructionLog", 2, 3);
        string body = Canonical(method.Body!);
        RequireOrdered(body,
        [
            "VmState<TGasPolicy>vmState=vm.VmState;",
            "if(vmState.IsStatic)gotoStaticCallViolation;",
            "if(!stack.PopMemoryPositionAndUInt256(outUInt256position,outUInt256length))gotoStackUnderflow;",
            "ulongtopicsCount=(ulong)TOpCount.Count;",
            "if(!TGasPolicy.UpdateMemoryCost(refgas,inposition,length,refvmState.Memory))gotoOutOfGas;",
            "ulongdataSize=(ulong)length;",
            "if(!TGasPolicy.TryConsumeLogEmission(refgas,topicsCount,dataSize))gotoOutOfGas;",
            "if(!vmState.Memory.TryLoad(inposition,length,outReadOnlyMemory<byte>data))gotoOutOfGas;",
            "Hash256[]topics=topicsCount==0?[]:newHash256[topicsCount];",
            "for(inti=0;i<topics.Length;i++){if(!stack.PopWord256(outSpan<byte>topic))gotoStackUnderflow;topics[i]=newHash256(topic);}",
            "LogEntrylogEntry=new(vmState.Env.ExecutingAccount,data.Length==0?[]:data.ToArray(),topics);",
            "vm.AddLog(logEntry);",
            "returnEvmExceptionType.None;",
        ], "LOG semantic effect order");
        Require(body, "StackUnderflow:returnEvmExceptionType.StackUnderflow;", "LOG stack-underflow exit");
        Require(body, "StaticCallViolation:returnEvmExceptionType.StaticCallViolation;", "LOG static exit");
        Require(body, "OutOfGas:returnEvmExceptionType.OutOfGas;", "LOG out-of-gas exit");

        for (int count = 0; count <= 4; count++)
        {
            TypeDeclarationSyntax op = FindNestedType(FindOwner(sources[InstructionLogPath].Syntax, "EvmInstructions", 0), $"Op{count}", 0);
            int extracted = ReadTopicCount(op);
            if (extracted != count)
                throw new ExtractionException($"Rejected LOG{count} topic count; extracted {extracted}.");
        }
    }

    private static int ReadTopicCount(TypeDeclarationSyntax op)
    {
        PropertyDeclarationSyntax[] properties = op.Members.OfType<PropertyDeclarationSyntax>()
            .Where(static property => property.Identifier.ValueText == "Count").ToArray();
        if (properties.Length != 1 || properties[0].ExpressionBody?.Expression is not ExpressionSyntax expression)
            throw new ExtractionException($"Expected one expression-bodied {op.Identifier.ValueText}.Count property.");
        if (expression is LiteralExpressionSyntax literal)
            return Convert.ToInt32(literal.Token.Value, CultureInfo.InvariantCulture);
        if (expression is not IdentifierNameSyntax { Identifier.ValueText: "Size" })
            throw new ExtractionException($"Unadmitted {op.Identifier.ValueText}.Count expression '{expression}'.");
        ExpressionSyntax initializer = FindField(op, "Size").Declaration.Variables
            .Single(static variable => variable.Identifier.ValueText == "Size").Initializer?.Value
            ?? throw new ExtractionException($"{op.Identifier.ValueText}.Size has no initializer.");
        if (initializer is LiteralExpressionSyntax sizeLiteral)
            return Convert.ToInt32(sizeLiteral.Token.Value, CultureInfo.InvariantCulture);
        if (initializer is SizeOfExpressionSyntax sizeOf && Canonical(sizeOf.Type) == "byte")
            return sizeof(byte);
        throw new ExtractionException($"Unadmitted {op.Identifier.ValueText}.Size expression '{initializer}'.");
    }

    private static void ValidateGasAndMemory(Dictionary<string, SourceFile> sources)
    {
        TypeDeclarationSyntax gas = FindOwner(sources[GasPolicyPath].Syntax, "EthereumGasPolicy", 0);
        Require(Canonical(FindMethod(gas, "TryConsumeLogEmission", 0, 3)),
            "ulongcost=GasCostOf.Log+topicCount*GasCostOf.LogTopic+dataSize*GasCostOf.LogData;returnUpdateGas(refgas,cost);",
            "LOG emission formula");
        Require(Canonical(FindMethod(gas, "UpdateGas", 0, 2)),
            "if(GetRemainingGas(ingas)<gasCost){gas.Value=0;returnfalse;}ConsumeRaw(refgas,gasCost);returntrue;",
            "execution-gas charge and exhaustion");
        Require(Canonical(FindMethod(gas, "UpdateMemoryCost", 0, 4, "EthereumGasPolicy,UInt256,UInt256,EvmPooledMemory")),
            "ulongmemoryCost=memory.CalculateMemoryCost(inposition,length,outbooloutOfGas);",
            "LOG memory charge route");

        TypeDeclarationSyntax memory = FindOwner(sources[MemoryPath].Syntax, "EvmPooledMemory", 0);
        Require(Canonical(FindMethod(memory, "ComputeMemoryExpansionCost", 0, 1)),
            "((newActiveWords*newActiveWords)>>9)-((activeWords*activeWords)>>9)", "quadratic memory divisor");
        Require(Canonical(FindField(memory, "MaxMemorySize")), "int.MaxValue-WordSize+1", "maximum addressable memory");
        Require(Canonical(FindMethod(memory, "CalculateMemoryCost", 0, 3, "UInt256,UInt256,bool")),
            "returnnewSize>Size?ComputeMemoryExpansionCost(newSize):0;", "logical memory expansion before charge");
        Require(Canonical(FindMethod(memory, "TryLoad", 0, 3, "UInt256,UInt256,ReadOnlyMemory<byte>")),
            "UpdateSize(newLength);data=GetBackingMemory", "payload load after admitted range check");

        TypeDeclarationSyntax stack = FindOwner(sources[StackPath].Syntax, "EvmStack", 0);
        Require(Canonical(FindMethod(stack, "PopMemoryPositionAndUInt256", 0, 2, "UInt256,UInt256")),
            "nintnewHead=Head-2;if(newHead<0){returnfalse;}Head=newHead;", "atomic LOG header pop");
        Require(Canonical(FindMethod(stack, "PopWord256", 0, 1, "Span<byte>")),
            "ninthead=Head-1;if(head<0){word=default;returnfalse;}Head=head;", "sequential topic pop");
    }

    private static void ValidateLogBoundary(Dictionary<string, SourceFile> sources)
    {
        TypeDeclarationSyntax vm = FindOwner(sources[VirtualMachinePath].Syntax, "VirtualMachine", 1);
        string addLog = Canonical(FindMethod(vm, "AddLog", 0, 1));
        RequireOrdered(addLog,
        [
            "VmState.AccessTracker.Logs.Add(logEntry);",
            "if(DispatchFlags.ConstTracing&&TxTracer.IsTracingLogs){TxTracer.ReportLog(logEntry);}",
        ], "journal append before optional log tracing");

        TypeDeclarationSyntax journal = FindOwner(
            sources["src/Nethermind/Nethermind.Core/Collections/JournalCollection.cs"].Syntax, "JournalCollection", 1);
        Require(Canonical(FindMethod(journal, "Add", 0, 1)), "=>_list.Add(item);", "append-only journal insertion");
        TypeDeclarationSyntax logEntry = (TypeDeclarationSyntax)FindTopLevelType(
            sources["src/Nethermind/Nethermind.Core/LogEntry.cs"].Syntax, "LogEntry", 0);
        Require(Canonical(logEntry),
            "publicAddressAddress{get;}=address;publicHash256[]Topics{get;}=topics;publicbyte[]Data{get;}=data;",
            "LOG address/payload/topic construction");
        TypeDeclarationSyntax hash = FindOwner(sources["src/Nethermind/Nethermind.Core/Crypto/Hash256.cs"].Syntax, "Hash256", 0);
        Require(Canonical(FindConstructor(hash, "Hash256", 1, "ReadOnlySpan<byte>")),
            "if(bytes.Length!=Size)", "32-byte topic constructor");
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
            string expected = index == 0
                ? "NamedReleaseSpec<Olympic>(null)"
                : $"NamedReleaseSpec<{name}>({ForkLineage[index - 1]}.Instance)";
            Require(Canonical(fork.BaseList!), expected, index == 0 ? "Olympic fork root" : $"{name} fork parent");
        }
    }

    private static Dictionary<string, int> ReadGasConstants(CompilationUnitSyntax syntax)
    {
        TypeDeclarationSyntax owner = FindOwner(syntax, "GasCostOf", 0);
        Dictionary<string, int> expected = new(StringComparer.Ordinal)
        {
            ["Memory"] = 3,
            ["Log"] = 375,
            ["LogTopic"] = 375,
            ["LogData"] = 8,
        };
        foreach (KeyValuePair<string, int> item in expected)
        {
            ExpressionSyntax initializer = FindField(owner, item.Key).Declaration.Variables
                .Single(variable => variable.Identifier.ValueText == item.Key).Initializer?.Value
                ?? throw new ExtractionException($"GasCostOf.{item.Key} has no initializer.");
            if (initializer is not LiteralExpressionSyntax literal ||
                Convert.ToInt32(literal.Token.Value, CultureInfo.InvariantCulture) != item.Value)
                throw new ExtractionException($"GasCostOf.{item.Key} must remain {item.Value}.");
        }
        return expected;
    }

    private static Dictionary<string, List<int>> ReadInstructionValues(CompilationUnitSyntax syntax)
    {
        EnumDeclarationSyntax instruction = (EnumDeclarationSyntax)FindTopLevelType(syntax, "Instruction", 0);
        Dictionary<string, List<int>> result = new(StringComparer.Ordinal);
        foreach (EnumMemberDeclarationSyntax member in instruction.Members)
        {
            ExpressionSyntax? expression = member.EqualsValue?.Value;
            if (expression is null) continue;
            string text = expression.ToString();
            int value = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToInt32(text[2..], 16)
                : int.Parse(text, CultureInfo.InvariantCulture);
            result.Add(member.Identifier.ValueText, [value]);
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
            string[] aliases = syntax.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Where(static directive => directive.Alias is not null)
                .Select(Canonical)
                .ToArray();
            bool admittedAliases = relativePath switch
            {
                StackPath => aliases.SequenceEqual(["usingHalfWord=Vector128<byte>;"], StringComparer.Ordinal),
                "src/Nethermind/Nethermind.Evm/ExecutionEnvironment.cs" => aliases.SequenceEqual([
                    "usingQueue=Nethermind.Evm.EvmObjectPool<Nethermind.Evm.ExecutionEnvironment>;",
                ], StringComparer.Ordinal),
                _ => aliases.Length == 0,
            };
            if (!admittedAliases)
                throw new ExtractionException($"Using aliases are not admitted in {relativePath}.");
            string[] directives = syntax.DescendantTrivia(descendIntoTrivia: true)
                .Where(static trivia => trivia.IsDirective)
                .Select(static trivia => trivia.ToString().Trim())
                .ToArray();
            bool admittedDirectives = relativePath switch
            {
                MemoryPath => directives.SequenceEqual(["#if ZK_EVM", "#else", "#endif"], StringComparer.Ordinal),
                VirtualMachinePath => directives.SequenceEqual([
                    "#pragma warning disable IDE0063 // Cannot simplify: the `goto Failure` above jumps past this scope, which a `using` declaration would forbid (CS8648)",
                    "#pragma warning restore IDE0063",
                ], StringComparer.Ordinal),
                "src/Nethermind/Nethermind.Evm/VmState.cs" => directives.SequenceEqual([
                    "#if ZK_EVM", "#endif", "#if DEBUG", "#endif",
                    "#if DEBUG", "#endif", "#if DEBUG", "#endif",
                ], StringComparer.Ordinal),
                "src/Nethermind/Nethermind.Evm/ExecutionEnvironment.cs" => directives.SequenceEqual([
                    "#if DEBUG", "#endif", "#if DEBUG", "#endif",
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

    private static List<AdmissionIdentity> AdmitReviewedSyntax(
        Dictionary<string, SourceFile> sources,
        IReadOnlyDictionary<string, string>? overrides)
    {
        List<AdmissionIdentity> identities = [];
        Assembly assembly = typeof(LogOpcodeProfile).Assembly;
        foreach (AdmissionSpec spec in AdmissionSpecs)
        {
            string template;
            if (overrides is not null && overrides.TryGetValue(spec.ResourceStem, out string? replacement))
            {
                template = replacement;
            }
            else
            {
                string resource = $"Nethermind.Evm.Lean.LogOpcodeExtractor.Admission.{spec.ResourceStem}.cs.txt";
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
                if (!EquivalentTokensAndTrivia(actual, admitted))
                    throw new ExtractionException($"Unadmitted complete syntax: {spec.ResourceStem}:{SelectorKey(selector)}.");
                identities.Add(new(
                    AdmissionKey(spec, selector),
                    Hash(Encoding.UTF8.GetBytes(CompleteCanonical(admitted))),
                    Hash(Encoding.UTF8.GetBytes(CompleteCanonical(actual)))));
            }
        }
        ValidateAdmissionIdentities(identities);
        return identities;
    }

    internal static void ValidateAdmissionIdentities(IReadOnlyList<AdmissionIdentity> identities)
    {
        if (identities is null || identities.Any(static identity => identity is null))
            throw new ExtractionException("Syntax admission identities contain null entries.");
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (AdmissionIdentity identity in identities)
        {
            if (string.IsNullOrWhiteSpace(identity.Key) || identity.TemplateSha256 is null ||
                identity.SourceSyntaxSha256 is null || !IsSha256(identity.TemplateSha256) ||
                !IsSha256(identity.SourceSyntaxSha256))
                throw new ExtractionException("Syntax admission identity is incomplete or has an invalid digest.");
            bool sourceQualified = InputPaths.Any(path => identity.Key.StartsWith(path + ":", StringComparison.Ordinal));
            if (!sourceQualified)
                throw new ExtractionException($"Syntax admission identity is not owner/path-qualified: {identity.Key}.");
            if (!keys.Add(identity.Key))
                throw new ExtractionException($"Duplicate owner/path-qualified syntax admission key '{identity.Key}'.");
        }
    }

    private static string AdmissionKey(AdmissionSpec spec, MemberSelector selector)
    {
        string owner = spec.OwnerName is null
            ? "compilation-unit/0"
            : $"{spec.OwnerName}/{spec.OwnerGenericArity}";
        return $"{spec.SourcePath}:{spec.ResourceStem}:{owner}:{SelectorKey(selector)}";
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
                if (known.Contains(Path.GetFullPath(file)) ||
                    file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
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
                                "method" => owner.Members.OfType<MethodDeclarationSyntax>().Any(method =>
                                    method.Identifier.ValueText == selector.Name &&
                                    (method.TypeParameterList?.Parameters.Count ?? 0) == selector.GenericArity &&
                                    method.ParameterList.Parameters.Count == selector.ParameterCount),
                                "constructor" => owner.Members.OfType<ConstructorDeclarationSyntax>().Any(constructor =>
                                    constructor.Identifier.ValueText == selector.Name &&
                                    constructor.ParameterList.Parameters.Count == selector.ParameterCount),
                                "type" => owner.Members.OfType<BaseTypeDeclarationSyntax>().Any(type =>
                                    type.Identifier.ValueText == selector.Name && GenericArity(type) == selector.GenericArity),
                                "field" => owner.Members.OfType<FieldDeclarationSyntax>().Any(field =>
                                    field.Declaration.Variables.Any(variable => variable.Identifier.ValueText == selector.Name)),
                                "property" => owner.Members.OfType<PropertyDeclarationSyntax>().Any(property =>
                                    property.Identifier.ValueText == selector.Name),
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
        MemberDeclarationSyntax[] matches = DirectMembers(container).Where(member => Matches(member, selector)).ToArray();
        if (matches.Length != 1)
            throw new ExtractionException($"Expected exactly one {SelectorKey(selector)} member; found {matches.Length}.");
        return matches[0];
    }

    private static IEnumerable<MemberDeclarationSyntax> DirectMembers(SyntaxNode container) => container switch
    {
        CompilationUnitSyntax unit => unit.Members.SelectMany(static member => member is BaseNamespaceDeclarationSyntax ns ? ns.Members : [member]),
        TypeDeclarationSyntax type => type.Members,
        _ => throw new ExtractionException($"Unsupported admission container {container.Kind()}.")
    };

    private static bool Matches(MemberDeclarationSyntax member, MemberSelector selector) => selector.Kind switch
    {
        "method" when member is MethodDeclarationSyntax method =>
            method.Identifier.ValueText == selector.Name &&
            (method.TypeParameterList?.Parameters.Count ?? 0) == selector.GenericArity &&
            method.ParameterList.Parameters.Count == selector.ParameterCount &&
            (selector.ParameterTypes.Length == 0 || ParameterTypes(method) == selector.ParameterTypes),
        "constructor" when member is ConstructorDeclarationSyntax constructor =>
            constructor.Identifier.ValueText == selector.Name &&
            constructor.ParameterList.Parameters.Count == selector.ParameterCount &&
            (selector.ParameterTypes.Length == 0 || ParameterTypes(constructor) == selector.ParameterTypes),
        "type" when member is BaseTypeDeclarationSyntax type =>
            type.Identifier.ValueText == selector.Name && GenericArity(type) == selector.GenericArity,
        "field" when member is FieldDeclarationSyntax field =>
            field.Declaration.Variables.Any(variable => variable.Identifier.ValueText == selector.Name),
        "property" when member is PropertyDeclarationSyntax property => property.Identifier.ValueText == selector.Name,
        _ => false,
    };

    private static string ParameterTypes(BaseMethodDeclarationSyntax method) => string.Join(",",
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

    private static MethodDeclarationSyntax FindMethod(
        TypeDeclarationSyntax owner,
        string name,
        int genericArity,
        int parameterCount,
        string parameterTypes = "") =>
        (MethodDeclarationSyntax)FindSelectedMember(owner, M(name, genericArity, parameterCount, parameterTypes));

    private static FieldDeclarationSyntax FindField(TypeDeclarationSyntax owner, string name) =>
        (FieldDeclarationSyntax)FindSelectedMember(owner, F(name));

    private static ConstructorDeclarationSyntax FindConstructor(
        TypeDeclarationSyntax owner,
        string name,
        int parameterCount,
        string parameterTypes = "") =>
        (ConstructorDeclarationSyntax)FindSelectedMember(owner, C(name, parameterCount, parameterTypes));

    private static int GenericArity(BaseTypeDeclarationSyntax type) => type is TypeDeclarationSyntax declaration
        ? declaration.TypeParameterList?.Parameters.Count ?? 0
        : 0;

    private static bool EquivalentTokensAndTrivia(SyntaxNode left, SyntaxNode right) =>
        CompleteCanonical(left) == CompleteCanonical(right);

    private static string CompleteCanonical(SyntaxNode node)
    {
        StringBuilder result = new();
        foreach (SyntaxNodeOrToken child in node.DescendantNodesAndTokens(descendIntoTrivia: false))
        {
            if (child.IsToken)
            {
                SyntaxToken token = child.AsToken();
                result.Append('T').Append(token.RawKind).Append(':').Append(token.Text).Append('\0');
            }
        }
        return result.ToString();
    }

    private static List<SourceIdentity> BuildSourceIdentities(Dictionary<string, SourceFile> sources) => sources.Values
        .OrderBy(static source => source.Path, StringComparer.Ordinal)
        .Select(static source => new SourceIdentity(
            source.Path,
            Hash(source.Bytes),
            Hash(Encoding.UTF8.GetBytes(CompleteCanonical(source.Syntax)))))
        .ToList();

    private static string CombinedSourceHash(
        IEnumerable<SourceIdentity> sources,
        IEnumerable<RawSourceIdentity> rawSources) => Hash(Encoding.UTF8.GetBytes(string.Join("\n",
        sources.Select(static source => $"{source.Path}\0{source.Sha256}\0{source.RoslynSyntaxSha256}")
            .Concat(rawSources.Select(static source => $"{source.Path}\0{source.Sha256}\0raw")))));

    private static byte[] Serialize<T>(T value) => Utf8WithoutBom.GetBytes(
        JsonSerializer.Serialize(value, JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(static character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Canonical(SyntaxNode node) => string.Concat(
        node.DescendantTokens(descendIntoTrivia: false).Select(static token => token.Text));

    private static void Require(string source, string required, string description)
    {
        if (!source.Contains(required, StringComparison.Ordinal))
            throw new ExtractionException($"Rejected {description}; expected '{required}'.");
    }

    private static void RequireOrdered(string source, IReadOnlyList<string> required, string description)
    {
        int position = 0;
        foreach (string fragment in required)
        {
            int next = source.IndexOf(fragment, position, StringComparison.Ordinal);
            if (next < 0)
                throw new ExtractionException($"Rejected {description}; ordered fragment '{fragment}' is missing or moved.");
            position = next + fragment.Length;
        }
    }

    private static AdmissionSpec Spec(
        string path,
        string stem,
        string? owner,
        int ownerArity,
        params MemberSelector[] members) => new(path, stem, owner, ownerArity, members);

    private static MemberSelector M(string name, int genericArity, int parameterCount, string parameterTypes = "") =>
        new("method", name, genericArity, parameterCount, parameterTypes);
    private static MemberSelector C(string name, int parameterCount, string parameterTypes = "") =>
        new("constructor", name, 0, parameterCount, parameterTypes);
    private static MemberSelector T(string name, int genericArity = 0) => new("type", name, genericArity);
    private static MemberSelector F(string name) => new("field", name);
    private static MemberSelector P(string name) => new("property", name);
    private static string SelectorKey(MemberSelector selector) =>
        $"{selector.Kind}:{selector.Name}/{selector.GenericArity}/{selector.ParameterCount}/{selector.ParameterTypes}";
}
