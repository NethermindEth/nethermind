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

namespace Nethermind.Evm.Lean.ExtendedStackOpcodeExtractor;

internal static class ExtendedStackOpcodeProfile
{
    internal const int SchemaVersion = 1;
    internal const string ExtractorVersion = "1.0.0";
    internal const string IrFileName = "ExtendedStackOpcodeKernel.ir.json";
    internal const string ManifestFileName = "ExtendedStackOpcodeKernel.source-manifest.json";
    internal const string DefaultLeanRelativePath =
        "tools/Evm/Lean/ExtendedStackOpcodeExtractor/Generated/ExtendedStackOpcodeKernel.lean";

    internal const string OpcodeHandlersPath =
        "src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs";
    internal const string DispatchPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs";
    internal const string InstructionPath = "src/Nethermind/Nethermind.Evm/Instruction.cs";
    internal const string GasCostPath = "src/Nethermind/Nethermind.Core/GasCostOf.cs";
    internal const string GasTagsPath = "src/Nethermind/Nethermind.Evm/GasPolicy/IGasCost.cs";
    internal const string EvmStackPath = "src/Nethermind/Nethermind.Evm/EvmStack.cs";
    internal const string StackInstructionsPath =
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Stack.cs";
    internal const string ControlFlowInstructionsPath =
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.ControlFlow.cs";
    internal const string DecoderKernelPath =
        "src/Nethermind/Nethermind.Evm/Instructions/ExtendedStackDecoderKernel.cs";
    internal const string GasPolicyInterfacePath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs";
    internal const string EthereumGasPolicyPath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs";
    internal const string VirtualMachinePath = "src/Nethermind/Nethermind.Evm/VirtualMachine.cs";
    internal const string VirtualMachineStandardPath =
        "src/Nethermind/Nethermind.Evm/VirtualMachine.std.cs";
    internal const string DispatchFlagsPath = "src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs";
    internal const string TypeFlagsPath = "src/Nethermind/Nethermind.Core/TypeFlags.cs";
    internal const string BlockProcessingModulePath =
        "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs";
    internal const string NamedReleaseSpecPath =
        "src/Nethermind/Nethermind.Specs/Forks/NamedReleaseSpec.cs";
    internal const string ForkDirectory = "src/Nethermind/Nethermind.Specs/Forks";
    internal const string BuildTargetsPath = "src/Nethermind/Directory.Build.targets";

    internal const string Root =
        "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>";
    internal const string GasPolicy = "Nethermind.Evm.GasPolicy.EthereumGasPolicy";
    internal const string Fork = "Nethermind.Specs.Forks.Amsterdam";
    internal const string BuildFlavor = "standard-mainnet";
    internal const string ForkGate = "IReleaseSpec.IsEip8024Enabled";
    internal const string DefaultHandler = "BadInstructionOpcode";
    internal const string PcOrder = "opcode-pc-before-gas;valid-immediate-pc-before-stack";
    internal const string GasOrder = "very-low-before-decode-and-stack";
    internal const string FaultOrder =
        "fork-disabled-before-gas;out-of-gas-before-invalid-immediate-before-underflow-before-overflow";
    internal const string BuildTargetsSha256 =
        "0598cebaef1df41102a18a3f9ace810bed1e4e64b8471d055724b4a394dccec6";
    internal const int StackLimit = 1024;
    internal const int VeryLowGas = 3;

    private const string AdmissionResourceSuffix = ".Admission.ProductionClosure.txt";
    private const string AdmissionResourceName = "ProductionClosure";
    private const string DispatchAssignmentPrefix = "lookup[(int)Instruction.";

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

    private static readonly string[] OpenExtractionObligations =
    [
        "Roslyn admission and source routing do not prove CLR, JIT, function-pointer, or unsafe EvmStack execution.",
        "Tracing, cancellation, opcode counters, tail calls, frame settlement, memory, allocation, and transaction or block integration remain outside this one-opcode projection.",
        "The generated semantics models only standard-mainnet EthereumGasPolicy and the Amsterdam EIP-8024 gate; zkEVM and alternate gas policies are excluded.",
        "The source-derived decoder refinement is imported as an independent prior result; this package does not claim whole-loop, frame, or CLR verification.",
    ];

    private static readonly OpcodeSeed[] Seeds =
    [
        new("dupN", "DUPN", 0xe6, "DupNOpcode<TTracingInst>", "DupNOpcode<TTracingInst>.Execute",
            "InstructionDupN", "TryDecodeSingle", "Dup", 1),
        new("swapN", "SWAPN", 0xe7, "SwapNOpcode<TTracingInst>", "SwapNOpcode<TTracingInst>.Execute",
            "InstructionSwapN", "TryDecodeSingle", "Swap", 0),
        new("exchange", "EXCHANGE", 0xe8, "ExchangeOpcode<TTracingInst>",
            "ExchangeOpcode<TTracingInst>.Execute", "InstructionExchange", "TryDecodePair", "Exchange", 0),
    ];

    private static readonly AdmissionSpec[] AdmissionSpecs = BuildAdmissionSpecs();

    internal static IReadOnlyList<string> SourcePaths => AdmissionSpecs
        .Select(static specification => specification.SourcePath)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    internal static IReadOnlyList<string> InputPaths => [.. SourcePaths, BuildTargetsPath];

    internal static ExtractionResult Extract(
        string repoRoot,
        string outputDirectory,
        string? leanOutputPath = null)
    {
        string root = Path.GetFullPath(repoRoot);
        Dictionary<string, SourceFile> sources = LoadSources(root);
        RawSourceIdentity buildTargets = LoadBuildTargets(root);
        List<AdmissionIdentity> admissions = AdmitReviewedSyntax(sources);
        ValidateNoCompetingMembers(root, sources);
        ValidateProductionClosure(root, sources);

        int gas = ReadVeryLowGas(sources[GasCostPath].Syntax);
        IReadOnlyList<OpcodeDescriptor> opcodes = ExtractOpcodes(sources, gas);
        IReadOnlyList<OpcodeSpecialization> specializations = ExpectedSpecializations(opcodes);
        IrDocument document = CreateDocument(opcodes, specializations);

        byte[] irBytes = Serialize(document);
        IrDocument serializedDocument = DeserializeIr(irBytes);
        List<SourceIdentity> sourceIdentities = BuildSourceIdentities(sources);
        string combinedSourceSha256 = CombinedSourceHash(sourceIdentities, buildTargets);
        byte[] leanBytes = ExtendedStackOpcodeLeanEmitter.Emit(
            serializedDocument,
            Hash(irBytes),
            combinedSourceSha256);

        SourceManifest manifest = new(
            SchemaVersion,
            ExtractorVersion,
            Hash(irBytes),
            Hash(leanBytes),
            combinedSourceSha256,
            sourceIdentities,
            [buildTargets],
            admissions);
        SourceManifest serializedManifest = DeserializeManifest(Serialize(manifest));

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
        File.WriteAllBytes(manifestPath, Serialize(serializedManifest));
        File.WriteAllBytes(leanPath, leanBytes);
        return new(irPath, manifestPath, leanPath, opcodes.Count, specializations.Count, admissions.Count);
    }

    internal static byte[] SerializeIr(IrDocument document) => Serialize(document);

    internal static IrDocument DeserializeIr(byte[] bytes)
    {
        if (bytes?.Length is not > 0) throw new ExtractionException("Serialized IR is empty.");
        try
        {
            ValidateNoDuplicateJsonProperties(bytes, "IR");
            IrDocument? document = JsonSerializer.Deserialize<IrDocument>(bytes, JsonOptions);
            if (document is not { } value) throw new ExtractionException("Serialized IR is empty.");
            ValidateIr(value);
            return value;
        }
        catch (ExtractionException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Serialized IR is malformed or has unknown members: {exception.Message}");
        }
    }

    internal static SourceManifest DeserializeManifest(byte[] bytes)
    {
        if (bytes?.Length is not > 0) throw new ExtractionException("Serialized source manifest is empty.");
        try
        {
            ValidateNoDuplicateJsonProperties(bytes, "source manifest");
            SourceManifest? manifest = JsonSerializer.Deserialize<SourceManifest>(bytes, JsonOptions);
            if (manifest is not { } value) throw new ExtractionException("Serialized source manifest is empty.");
            ValidateManifest(value);
            return value;
        }
        catch (ExtractionException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException(
                $"Serialized source manifest is malformed or has unknown members: {exception.Message}");
        }
    }

    internal static void ValidateIr(IrDocument document)
    {
        RequireNonEmpty(document.ExtractorVersion, document.Kernel, document.Root, document.GasPolicy,
            document.Fork, document.BuildFlavor, document.ForkGate, document.DefaultHandler,
            document.PcOrder, document.GasOrder, document.FaultOrder);
        if (document.SchemaVersion != SchemaVersion || document.ExtractorVersion != ExtractorVersion ||
            document.Kernel != "ExtendedStackOpcodeKernel" || document.Root != Root ||
            document.GasPolicy != GasPolicy || document.Fork != Fork || document.BuildFlavor != BuildFlavor ||
            document.ForkGate != ForkGate || document.DefaultHandler != DefaultHandler ||
            document.PcOrder != PcOrder || document.GasOrder != GasOrder || document.FaultOrder != FaultOrder ||
            document.StackLimit != StackLimit || document.VeryLowGas != VeryLowGas)
            throw new ExtractionException("Serialized IR header, fork, gas, stack, or order metadata changed.");

        if (document.DispatchTables is null || document.ForkLineage is null ||
            document.OpenExtractionObligations is null || document.Opcodes is null ||
            document.Specializations is null || document.DispatchTables.Any(static value => value is null) ||
            document.ForkLineage.Any(static value => value is null) ||
            document.OpenExtractionObligations.Any(static value => value is null) ||
            document.Opcodes.Any(static value => value is null) ||
            document.Specializations.Any(static value => value is null))
            throw new ExtractionException("Serialized IR contains null collections, entries, or fields.");

        if (!document.DispatchTables.SequenceEqual(DispatchTables) ||
            !document.ForkLineage.SequenceEqual(ForkLineage, StringComparer.Ordinal) ||
            !document.OpenExtractionObligations.SequenceEqual(OpenExtractionObligations, StringComparer.Ordinal))
            throw new ExtractionException("Serialized IR dispatch, fork, or obligation metadata changed.");

        foreach (DispatchTableBinding table in document.DispatchTables)
        {
            RequireNonEmpty(table.Name, table.TracingFlag, table.CancelableFlag);
            if (!DispatchTables.Contains(table))
                throw new ExtractionException("Serialized IR contains an unknown dispatch table.");
        }

        foreach (OpcodeDescriptor descriptor in document.Opcodes)
        {
            RequireNonEmpty(descriptor.Name, descriptor.Instruction, descriptor.HandlerBody,
                descriptor.Forwarder, descriptor.InstructionMethod, descriptor.DecoderMethod,
                descriptor.StackMethod, descriptor.DispatchAssignment, descriptor.GasClass,
                descriptor.PcOrder, descriptor.GasOrder, descriptor.FaultOrder, descriptor.Activation);
            if (descriptor.OpcodeByte is < 0 or > 255 || descriptor.FixedGas < 0 || descriptor.StackGrowth < 0)
                throw new ExtractionException("Serialized IR contains an invalid opcode numeric field.");
        }

        foreach (OpcodeSpecialization specialization in document.Specializations)
        {
            RequireNonEmpty(specialization.Opcode, specialization.DispatchTable, specialization.TracingFlag,
                specialization.CancelableFlag, specialization.ContinuableFlag, specialization.EnabledRoot,
                specialization.DisabledRoot);
        }

        IReadOnlyList<OpcodeDescriptor> expected = ExpectedDescriptors();
        if (!document.Opcodes.SequenceEqual(expected) ||
            !document.Specializations.SequenceEqual(ExpectedSpecializations(expected)))
            throw new ExtractionException("Serialized IR descriptor, gas, dispatch, fork, or root metadata changed.");
        if (document.Opcodes.Count != 3 || document.Specializations.Count != 12 ||
            document.Opcodes.Select(static value => value.Name).Distinct(StringComparer.Ordinal).Count() != 3 ||
            document.Opcodes.Select(static value => value.OpcodeByte).Distinct().Count() != 3)
            throw new ExtractionException("Serialized IR must contain exactly three opcodes and twelve roots.");
    }

    internal static void ValidateManifest(SourceManifest manifest)
    {
        RequireNonEmpty(manifest.ExtractorVersion, manifest.IrSha256, manifest.LeanSha256,
            manifest.CombinedSourceSha256);
        RequireSha256(manifest.IrSha256, "IR hash");
        RequireSha256(manifest.LeanSha256, "Lean hash");
        RequireSha256(manifest.CombinedSourceSha256, "combined source hash");
        if (manifest.SchemaVersion != SchemaVersion || manifest.ExtractorVersion != ExtractorVersion ||
            manifest.Sources is null || manifest.RawSources is null || manifest.Admissions is null ||
            manifest.Sources.Any(static value => value is null) ||
            manifest.RawSources.Any(static value => value is null) ||
            manifest.Admissions.Any(static value => value is null))
            throw new ExtractionException("Serialized source manifest contains invalid header or null entries.");

        string[] expectedSourcePaths = SourcePaths.OrderBy(static value => value, StringComparer.Ordinal).ToArray();
        if (manifest.Sources.Count != expectedSourcePaths.Length || manifest.RawSources.Count != 1 ||
            manifest.RawSources[0].Path != BuildTargetsPath ||
            manifest.RawSources[0].Sha256 != BuildTargetsSha256 ||
            manifest.Admissions.Count != AdmissionSpecs.Sum(static value => value.Members.Count) ||
            manifest.Sources.Select(static value => value.Path).Distinct(StringComparer.Ordinal).Count() !=
                manifest.Sources.Count ||
            manifest.RawSources.Select(static value => value.Path).Distinct(StringComparer.Ordinal).Count() !=
                manifest.RawSources.Count ||
            manifest.Admissions.Select(static value => value.Key).Distinct(StringComparer.Ordinal).Count() !=
                manifest.Admissions.Count ||
            !manifest.Sources.Select(static value => value.Path)
                .SequenceEqual(expectedSourcePaths, StringComparer.Ordinal))
            throw new ExtractionException("Serialized source manifest source or admission closure changed.");

        Dictionary<string, ReviewedSource> reviewed = LoadReviewedSourceIndex();
        if (reviewed.Count != expectedSourcePaths.Length)
            throw new ExtractionException("Reviewed source admission resource has extra or missing paths.");
        foreach (SourceIdentity source in manifest.Sources)
        {
            RequireNonEmpty(source.Path, source.Sha256, source.RoslynSyntaxSha256);
            RequireSha256(source.Sha256, $"source hash for {source.Path}");
            RequireSha256(source.RoslynSyntaxSha256, $"Roslyn syntax hash for {source.Path}");
            if (!reviewed.TryGetValue(source.Path, out ReviewedSource? expected) ||
                source.Sha256 != expected.Sha256 ||
                source.RoslynSyntaxSha256 != expected.RoslynSyntaxSha256)
                throw new ExtractionException($"Serialized source manifest changed admitted source {source.Path}.");
        }
        foreach (RawSourceIdentity source in manifest.RawSources)
        {
            RequireNonEmpty(source.Path, source.Sha256);
            RequireSha256(source.Sha256, $"raw source hash for {source.Path}");
        }

        string expectedCombinedSourceSha256 = CombinedSourceHash(manifest.Sources, manifest.RawSources[0]);
        if (manifest.CombinedSourceSha256 != expectedCombinedSourceSha256)
            throw new ExtractionException("Serialized source manifest combined source hash changed.");

        IrDocument expectedDocument = ExpectedIrDocument();
        byte[] expectedIr = Serialize(expectedDocument);
        byte[] expectedLean = ExtendedStackOpcodeLeanEmitter.Emit(
            expectedDocument,
            Hash(expectedIr),
            expectedCombinedSourceSha256);
        if (manifest.IrSha256 != Hash(expectedIr) || manifest.LeanSha256 != Hash(expectedLean))
            throw new ExtractionException("Serialized source manifest IR or Lean hash changed.");

        ValidateAdmissionIdentities(manifest.Admissions);
        string[] expectedKeys = AdmissionSpecs
            .SelectMany(static specification => specification.Members.Select(
                selector => AdmissionKey(specification, selector)))
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        if (!manifest.Admissions.Select(static value => value.Key)
                .OrderBy(static value => value, StringComparer.Ordinal)
                .SequenceEqual(expectedKeys, StringComparer.Ordinal))
            throw new ExtractionException("Serialized source manifest admission identities changed.");

        Dictionary<string, string> syntaxHashes = manifest.Sources.ToDictionary(
            static value => value.Path,
            static value => value.RoslynSyntaxSha256,
            StringComparer.Ordinal);
        foreach (AdmissionIdentity admission in manifest.Admissions)
        {
            AdmissionSpec specification = AdmissionSpecs.Single(value => admission.Key.StartsWith(
                value.SourcePath + ":", StringComparison.Ordinal) &&
                value.Members.Any(selector => AdmissionKey(value, selector) == admission.Key));
            ReviewedSource expected = reviewed[specification.SourcePath];
            string template =
                $"{specification.SourcePath}|{expected.Sha256}|{expected.RoslynSyntaxSha256}";
            if (admission.TemplateSha256 != Hash(Encoding.UTF8.GetBytes(template)) ||
                admission.SourceSyntaxSha256 != syntaxHashes[specification.SourcePath])
                throw new ExtractionException($"Serialized admission identity changed: {admission.Key}.");
        }
    }

    internal static void ValidateAdmissionIdentities(IReadOnlyList<AdmissionIdentity> identities)
    {
        if (identities?.Any(static value => value is null) != false)
            throw new ExtractionException("Admission identities contain null entries.");
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (AdmissionIdentity identity in identities)
        {
            RequireNonEmpty(identity.Key, identity.TemplateSha256, identity.SourceSyntaxSha256);
            RequireSha256(identity.TemplateSha256, $"admission template for {identity.Key}");
            RequireSha256(identity.SourceSyntaxSha256, $"admission syntax for {identity.Key}");
            if (!keys.Add(identity.Key))
                throw new ExtractionException($"Duplicate exact admission identity {identity.Key}.");
        }
    }

    internal static IReadOnlyList<OpcodeDescriptor> ExpectedDescriptors() => Seeds.Select(static seed =>
        new OpcodeDescriptor(
            seed.Name,
            seed.Instruction,
            seed.ExpectedByte,
            seed.HandlerBody,
            seed.Forwarder,
            seed.InstructionMethod,
            seed.DecoderMethod,
            seed.StackMethod,
            $"lookup[(int)Instruction.{seed.Instruction}]=OpcodeHandler<{seed.HandlerBody},TTracingInst,TCancelable>();",
            "veryLow",
            VeryLowGas,
            seed.StackGrowth,
            PcOrder,
            GasOrder,
            FaultOrder,
            ForkGate)).ToArray();

    internal static IReadOnlyList<OpcodeSpecialization> ExpectedSpecializations(
        IReadOnlyList<OpcodeDescriptor> opcodes) => opcodes.SelectMany(static opcode => DispatchTables.Select(table =>
        new OpcodeSpecialization(
            opcode.Name,
            table.Name,
            table.TracingFlag,
            table.CancelableFlag,
            "OnFlag",
            $"VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<{opcode.HandlerBody.Replace("TTracingInst", table.TracingFlag, StringComparison.Ordinal)},{table.TracingFlag},{table.CancelableFlag},OnFlag>",
            $"VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<BadInstructionOpcode,{table.TracingFlag},{table.CancelableFlag},OffFlag>"))).ToArray();

    private static IrDocument ExpectedIrDocument()
    {
        IReadOnlyList<OpcodeDescriptor> opcodes = ExpectedDescriptors();
        return CreateDocument(opcodes, ExpectedSpecializations(opcodes));
    }

    private static IrDocument CreateDocument(
        IReadOnlyList<OpcodeDescriptor> opcodes,
        IReadOnlyList<OpcodeSpecialization> specializations) => new(
        SchemaVersion,
        ExtractorVersion,
        "ExtendedStackOpcodeKernel",
        Root,
        GasPolicy,
        Fork,
        BuildFlavor,
        ForkGate,
        DefaultHandler,
        PcOrder,
        GasOrder,
        FaultOrder,
        StackLimit,
        VeryLowGas,
        DispatchTables,
        ForkLineage,
        OpenExtractionObligations,
        opcodes,
        specializations);

    private static AdmissionSpec[] BuildAdmissionSpecs()
    {
        List<AdmissionSpec> specifications =
        [
            Spec(GasCostPath, "GasCostOf", "Nethermind.Core", "GasCostOf", 0, F("VeryLow")),
            Spec(GasTagsPath, "GasTags", "Nethermind.Evm.GasPolicy", null, 0,
                T("IGasCost"), T("VeryLowGasCost")),
            Spec(TypeFlagsPath, "TypeFlags", "Nethermind.Core", null, 0,
                T("IFlag"), T("OffFlag"), T("OnFlag")),
            Spec(InstructionPath, "Instruction", "Nethermind.Evm", null, 0, T("Instruction")),
            Spec(DispatchFlagsPath, "DispatchFlags", "Nethermind.Evm", null, 0, T("DispatchFlags")),
            Spec(VirtualMachinePath, "EthereumVirtualMachine", "Nethermind.Evm", null, 0,
                T("EthereumVirtualMachine")),
            Spec(VirtualMachineStandardPath, "VirtualMachineStandard", "Nethermind.Evm", "VirtualMachine", 1,
                F("_opcodeTablesBySpec"), M("GetOpcodeTable", 0, 0), M("ShouldRefreshOpcodes", 0, 0)),
            Spec(BlockProcessingModulePath, "BlockProcessingModule", "Nethermind.Init.Modules",
                "BlockProcessingModule", 0, M("Load", 0, 1, "ContainerBuilder")),
            Spec(NamedReleaseSpecPath, "NamedReleaseSpec", "Nethermind.Specs.Forks", null, 0,
                T("NamedReleaseSpec"), T("NamedReleaseSpec", 1)),
            Spec(DispatchPath, "VirtualMachineDispatch", "Nethermind.Evm", "VirtualMachine", 1,
                T("OpcodeTable"), M("ExecuteOpcode", 4, 5,
                    "refEvmStack,refTGasPolicy,refDispatchState,nint,int"),
                M("ExitCheckedOpcode", 0, 4, "refDispatchState,nint,int,EvmExceptionType")),
            Spec(OpcodeHandlersPath, "VirtualMachineOpcodeHandlers", "Nethermind.Evm", "VirtualMachine", 1,
                T("IOpcodeBody"), M("OpcodeHandler", 3, 0), M("TerminatingOpcodeHandler", 3, 0),
                M("GenerateOpcodeHandlers", 2, 1, "IReleaseSpec"),
                T("BadInstructionOpcode"), T("DupNOpcode", 1), T("SwapNOpcode", 1),
                T("ExchangeOpcode", 1)),
            Spec(EvmStackPath, "EvmStack", "Nethermind.Evm", "EvmStack", 0,
                F("MaxStackSize"), M("Dup", 2, 1, "int"), M("EnsureDepth", 0, 1, "int"),
                M("Swap", 2, 1, "int"), M("Exchange", 1, 2, "int,int")),
            Spec(StackInstructionsPath, "InstructionStack", "Nethermind.Evm", "EvmInstructions", 0,
                M("InstructionDupN", 2, 3, "refEvmStack,refTGasPolicy,refnint"),
                M("InstructionSwapN", 2, 3, "refEvmStack,refTGasPolicy,refnint"),
                M("InstructionExchange", 2, 3, "refEvmStack,refTGasPolicy,refnint"),
                M("ReadEip8024ImmediateOrZero", 0, 3, "refbyte,nint,nint"),
                M("TryDecodeSingle", 0, 3, "refEvmStack,refnint,outint"),
                M("TryDecodePair", 0, 4, "refEvmStack,refnint,outint,outint")),
            Spec(ControlFlowInstructionsPath, "InstructionControlFlow", "Nethermind.Evm",
                "EvmInstructions", 0,
                M("InstructionBadInstruction", 1, 3,
                    "refEvmStack,refTGasPolicy,VirtualMachine<TGasPolicy>")),
            Spec(DecoderKernelPath, "ExtendedStackDecoderTypes", "Nethermind.Evm", null, 0,
                T("ExtendedStackSingleDecode"), T("ExtendedStackPairDecode")),
            Spec(DecoderKernelPath, "ExtendedStackDecoderKernel", "Nethermind.Evm",
                "ExtendedStackDecoderKernel", 0,
                M("DecodeSingle", 0, 1, "byte"), M("DecodePair", 0, 1, "byte")),
            Spec(GasPolicyInterfacePath, "IGasPolicy", "Nethermind.Evm.GasPolicy", "IGasPolicy", 1,
                M("UpdateGas", 1, 1, "refTSelf")),
            Spec(EthereumGasPolicyPath, "EthereumGasPolicy", "Nethermind.Evm.GasPolicy",
                "EthereumGasPolicy", 0, M("UpdateGas", 0, 2, "refEthereumGasPolicy,ulong")),
        ];

        for (int index = 0; index < ForkLineage.Length; index++)
        {
            specifications.Add(Spec(
                $"{ForkDirectory}/{ForkFileName(index)}.cs",
                "Fork" + ForkLineage[index],
                "Nethermind.Specs.Forks",
                null,
                0,
                T(ForkLineage[index])));
        }
        return specifications.ToArray();
    }

    private static IReadOnlyList<OpcodeDescriptor> ExtractOpcodes(
        Dictionary<string, SourceFile> sources,
        int gas)
    {
        Dictionary<string, int> instructionValues = ReadInstructionValues(sources[InstructionPath].Syntax);
        TypeDeclarationSyntax owner = FindOwner(sources[OpcodeHandlersPath].Syntax, "VirtualMachine", 1);
        MethodDeclarationSyntax generator = FindMethod(owner, "GenerateOpcodeHandlers", 2, 1);
        Dictionary<string, (string HandlerBody, string DispatchAssignment)> assignments =
            ExtractDispatchAssignments(generator);
        List<OpcodeDescriptor> descriptors = new(Seeds.Length);
        HashSet<int> bytes = [];
        foreach (OpcodeSeed seed in Seeds)
        {
            if (!instructionValues.TryGetValue(seed.Instruction, out int opcodeByte) ||
                opcodeByte != seed.ExpectedByte)
                throw new ExtractionException(
                    $"Instruction.{seed.Instruction} is not admitted byte 0x{seed.ExpectedByte:X2}.");
            if (!bytes.Add(opcodeByte)) throw new ExtractionException($"Duplicate opcode byte 0x{opcodeByte:X2}.");
            if (!assignments.TryGetValue(seed.Instruction, out (string HandlerBody, string DispatchAssignment) assignment) ||
                assignment.HandlerBody != seed.HandlerBody)
                throw new ExtractionException($"Instruction.{seed.Instruction} has no exact admitted live handler.");
            descriptors.Add(new(
                seed.Name,
                seed.Instruction,
                opcodeByte,
                assignment.HandlerBody,
                seed.Forwarder,
                seed.InstructionMethod,
                seed.DecoderMethod,
                seed.StackMethod,
                assignment.DispatchAssignment,
                "veryLow",
                gas,
                seed.StackGrowth,
                PcOrder,
                GasOrder,
                FaultOrder,
                ForkGate));
        }
        return descriptors;
    }

    private static Dictionary<string, (string HandlerBody, string DispatchAssignment)> ExtractDispatchAssignments(
        MethodDeclarationSyntax generator)
    {
        Dictionary<string, (string HandlerBody, string DispatchAssignment)> assignments = new(StringComparer.Ordinal);
        foreach (AssignmentExpressionSyntax assignment in generator.DescendantNodes()
                     .OfType<AssignmentExpressionSyntax>())
        {
            string left = Canonical(assignment.Left);
            if (!left.StartsWith(DispatchAssignmentPrefix, StringComparison.Ordinal) || !left.EndsWith(']')) continue;
            string instruction = left[DispatchAssignmentPrefix.Length..^1];
            if (!Seeds.Any(seed => seed.Instruction == instruction)) continue;
            if (assignment.Parent is not ExpressionStatementSyntax statement ||
                statement.Ancestors().OfType<IfStatementSyntax>().SingleOrDefault() is not { } gate ||
                Canonical(gate.Condition) != "spec.IsEip8024Enabled")
                throw new ExtractionException($"Instruction.{instruction} is not directly guarded by EIP-8024.");
            if (assignment.Right is not InvocationExpressionSyntax invocation ||
                invocation.Expression is not GenericNameSyntax generic ||
                generic.Identifier.ValueText != "OpcodeHandler" || generic.TypeArgumentList.Arguments.Count != 3)
                throw new ExtractionException($"Instruction.{instruction} dispatch root changed.");
            string handlerBody = Canonical(generic.TypeArgumentList.Arguments[0]);
            if (!assignments.TryAdd(instruction, (handlerBody, Canonical(statement))))
                throw new ExtractionException($"Duplicate dispatch assignment for Instruction.{instruction}.");
        }
        return assignments;
    }

    private static void ValidateProductionClosure(string root, Dictionary<string, SourceFile> sources)
    {
        ValidateBuildSelection(root, sources);
        ValidateGas(sources);
        ValidateDispatch(sources);
        ValidateHandlers(sources);
        ValidateDefaultHandler(sources);
        ValidateInstructionsAndDecoder(sources);
        ValidateStack(sources);
        ValidateRuntimeAndFork(sources);
    }

    private static void ValidateBuildSelection(string root, Dictionary<string, SourceFile> sources)
    {
        string targets = File.ReadAllText(Path.Combine(
            root,
            BuildTargetsPath.Replace('/', Path.DirectorySeparatorChar)));
        Require(targets, "<ItemGroup Condition=\"'$(EnableZkEvm)' == 'true'\">", "zkEVM selection group");
        Require(targets, "<Compile Remove=\"**/std/**/*.cs\" />", "standard exclusion in zkEVM");
        Require(targets, "<Compile Remove=\"**/*.std.cs\" />", "standard exclusion in zkEVM");
        Require(targets, "<ItemGroup Condition=\"'$(EnableZkEvm)' != 'true'\">", "standard selection group");
        Require(targets, "<Compile Remove=\"**/zkevm/**/*.cs\" />", "zkEVM exclusion in standard build");
        Require(targets, "<Compile Remove=\"**/*.zkevm.cs\" />", "zkEVM exclusion in standard build");

        string flags = Canonical(sources[DispatchFlagsPath].Syntax);
        Require(flags, "publicconstboolConstTracing=true;", "standard tracing selector");
        Require(flags, "publicstaticboolTracing(boolisTracing)=>isTracing;", "standard tracing identity");
        Require(flags, "publicstaticboolCancelable(booltracerIsCancelable)=>tracerIsCancelable;",
            "standard cancellation identity");
    }

    private static void ValidateGas(Dictionary<string, SourceFile> sources)
    {
        Require(Canonical(sources[GasTagsPath].Syntax),
            "publicreadonlystructVeryLowGasCost:IGasCost{publicstaticulongGasCost=>GasCostOf.VeryLow;}",
            "very-low gas tag forwarding");
        string gasPolicyInterface = Canonical(sources[GasPolicyInterfacePath].Syntax);
        Require(gasPolicyInterface,
            "staticvirtualboolUpdateGas<TCost>(refTSelfgas)whereTCost:struct,IGasCost=>TSelf.UpdateGas(refgas,TCost.GasCost);",
            "generic gas-cost forwarding route");
        string policy = Canonical(sources[EthereumGasPolicyPath].Syntax);
        RequireOrdered(policy,
            "publicstaticboolUpdateGas(refEthereumGasPolicygas,ulonggasCost)",
            "if(GetRemainingGas(ingas)<gasCost)",
            "gas.Value=0;",
            "returnfalse;",
            "ConsumeRaw(refgas,gasCost);",
            "returntrue;");
        Require(gasPolicyInterface,
            "staticvirtualboolUpdateGas<TCost>(refTSelfgas)", "generic gas policy route");
    }

    private static void ValidateDispatch(Dictionary<string, SourceFile> sources)
    {
        TypeDeclarationSyntax owner = FindOwner(sources[DispatchPath].Syntax, "VirtualMachine", 1);
        MethodDeclarationSyntax execute = FindMethod(owner, "ExecuteOpcode", 4, 5);
        string dispatch = Canonical(execute);
        RequireOrdered(dispatch,
            "pc++;",
            "opCodeCount++;",
            "if(TOpcode.HasCheckedBody)",
            "if(!TOpcode.TryConsumeGas(refgas))",
            "TOpcode.Execute(refstack,refgas",
            "else",
            "exceptionType=TOpcode.Execute(refstack,refgas,state.Vm,refpc);");
        string handlers = Canonical(sources[OpcodeHandlersPath].Syntax);
        Require(handlers, "staticvirtualboolHasCheckedBody=>false;", "ordinary unchecked handler default");
        Require(handlers, "&ExecuteOpcode<TOpcode,TTracingInst,TCancelable,OnFlag>",
            "ordinary continuable handler root");
        Require(handlers, "TerminatingOpcodeHandler<BadInstructionOpcode,TTracingInst,TCancelable>()",
            "default bad-instruction table fill");
    }

    private static void ValidateHandlers(Dictionary<string, SourceFile> sources)
    {
        TypeDeclarationSyntax owner = FindOwner(sources[OpcodeHandlersPath].Syntax, "VirtualMachine", 1);
        string dup = Canonical(FindNestedType(owner, "DupNOpcode", 1));
        string swap = Canonical(FindNestedType(owner, "SwapNOpcode", 1));
        string exchange = Canonical(FindNestedType(owner, "ExchangeOpcode", 1));
        Require(dup,
            "EvmInstructions.InstructionDupN<TGasPolicy,TTracingInst>(refstack,refgas,refprogramCounter);",
            "DUPN forwarder");
        Require(swap,
            "EvmInstructions.InstructionSwapN<TGasPolicy,TTracingInst>(refstack,refgas,refprogramCounter);",
            "SWAPN forwarder");
        Require(exchange,
            "EvmInstructions.InstructionExchange<TGasPolicy,TTracingInst>(refstack,refgas,refprogramCounter);",
            "EXCHANGE forwarder");

        MethodDeclarationSyntax generator = FindMethod(owner, "GenerateOpcodeHandlers", 2, 1);
        string generation = Canonical(generator);
        RequireOrdered(generation,
            "if(spec.IsEip8024Enabled)",
            "lookup[(int)Instruction.DUPN]=OpcodeHandler<DupNOpcode<TTracingInst>,TTracingInst,TCancelable>();",
            "lookup[(int)Instruction.SWAPN]=OpcodeHandler<SwapNOpcode<TTracingInst>,TTracingInst,TCancelable>();",
            "lookup[(int)Instruction.EXCHANGE]=OpcodeHandler<ExchangeOpcode<TTracingInst>,TTracingInst,TCancelable>();");
    }

    private static void ValidateDefaultHandler(Dictionary<string, SourceFile> sources)
    {
        TypeDeclarationSyntax virtualMachine = FindOwner(
            sources[OpcodeHandlersPath].Syntax,
            "VirtualMachine",
            1);
        string forwarder = Canonical(FindNestedType(virtualMachine, "BadInstructionOpcode", 0));
        Require(forwarder,
            "EvmInstructions.InstructionBadInstruction(refstack,refgas,vm);",
            "default bad-instruction forwarder");

        TypeDeclarationSyntax instructions = FindOwner(
            sources[ControlFlowInstructionsPath].Syntax,
            "EvmInstructions",
            0);
        string implementation = Canonical(FindMethod(
            instructions,
            "InstructionBadInstruction",
            1,
            3));
        RequireExact(implementation,
            "publicstaticEvmExceptionTypeInstructionBadInstruction<TGasPolicy>(refEvmStackstack,refTGasPolicygas,VirtualMachine<TGasPolicy>_)whereTGasPolicy:struct,IGasPolicy<TGasPolicy>=>EvmExceptionType.BadInstruction;",
            "default bad-instruction result without stack mutation or gas debit");
    }

    private static void ValidateInstructionsAndDecoder(Dictionary<string, SourceFile> sources)
    {
        TypeDeclarationSyntax owner = FindOwner(sources[StackInstructionsPath].Syntax, "EvmInstructions", 0);
        ValidateInstruction(
            FindMethod(owner, "InstructionDupN", 2, 3),
            "TryDecodeSingle(refstack,refprogramCounter,outintdepth)",
            "stack.Dup<TTracingInst,OnFlag>(depth)",
            "DUPN");
        ValidateInstruction(
            FindMethod(owner, "InstructionSwapN", 2, 3),
            "TryDecodeSingle(refstack,refprogramCounter,outintdepth)",
            "stack.Swap<TTracingInst,OnFlag>(depth+1)",
            "SWAPN");
        ValidateInstruction(
            FindMethod(owner, "InstructionExchange", 2, 3),
            "TryDecodePair(refstack,refprogramCounter,outintn,outintm)",
            "stack.Exchange<TTracingInst>(n,m)",
            "EXCHANGE");

        string reader = Canonical(FindMethod(owner, "ReadEip8024ImmediateOrZero", 0, 3));
        Require(reader, "programCounter<codeLength?Unsafe.Add(refcode,programCounter):(byte)0;",
            "zero-extended immediate read");
        string single = Canonical(FindMethod(owner, "TryDecodeSingle", 0, 3));
        RequireOrdered(single,
            "byteimm=ReadEip8024ImmediateOrZero(refstack.Code,stack.CodeLength,programCounter);",
            "ExtendedStackSingleDecodedecoded=ExtendedStackDecoderKernel.DecodeSingle(imm);",
            "depth=decoded.Depth;",
            "if(!decoded.IsValid)",
            "returnfalse;",
            "programCounter++;",
            "returntrue;");
        string pair = Canonical(FindMethod(owner, "TryDecodePair", 0, 4));
        RequireOrdered(pair,
            "byteimm=ReadEip8024ImmediateOrZero(refstack.Code,stack.CodeLength,programCounter);",
            "ExtendedStackPairDecodedecoded=ExtendedStackDecoderKernel.DecodePair(imm);",
            "n=decoded.FirstPosition;",
            "m=decoded.SecondPosition;",
            "if(!decoded.IsValid)",
            "returnfalse;",
            "programCounter++;",
            "returntrue;");

        TypeDeclarationSyntax kernel = FindOwner(sources[DecoderKernelPath].Syntax,
            "ExtendedStackDecoderKernel", 0);
        string decodeSingle = Canonical(FindMethod(kernel, "DecodeSingle", 0, 1));
        RequireOrdered(decodeSingle,
            "intdepth=(immediate+145)&0xFF;",
            "boolisValid=(uint)(immediate-0x5B)>0x24;",
            "returnnewExtendedStackSingleDecode(isValid,depth);");
        string decodePair = Canonical(FindMethod(kernel, "DecodePair", 0, 1));
        RequireOrdered(decodePair,
            "intshifted=immediate^0x8F;",
            "intquotient=shifted>>4;",
            "intremainder=shifted&0x0F;",
            "intmask=(quotient-remainder)>>31;",
            "intfirstPosition=((quotient&mask)|(remainder&~mask))+2;",
            "intsecondPosition=(((remainder+1)&mask)|((29-quotient)&~mask))+1;",
            "boolisValid=(uint)(immediate-0x52)>0x2D;",
            "returnnewExtendedStackPairDecode(isValid,firstPosition,secondPosition);");
    }

    private static void ValidateInstruction(
        MethodDeclarationSyntax method,
        string decoder,
        string stackMutation,
        string name)
    {
        string source = Canonical(method);
        RequireOrdered(source,
            "if(!TGasPolicy.UpdateGas<VeryLowGasCost>(refgas))returnEvmExceptionType.OutOfGas;",
            decoder,
            "?EvmExceptionType.BadInstruction:",
            stackMutation);
        Require(source, "whereTGasPolicy:struct,IGasPolicy<TGasPolicy>", $"{name} gas-policy owner");
        Require(source, "whereTTracingInst:struct,IFlag", $"{name} tracing owner");
    }

    private static void ValidateStack(Dictionary<string, SourceFile> sources)
    {
        TypeDeclarationSyntax stack = FindOwner(sources[EvmStackPath].Syntax, "EvmStack", 0);
        Require(Canonical(FindField(stack, "MaxStackSize")), "MaxStackSize=1025", "stack sentinel");
        string dup = Canonical(FindMethod(stack, "Dup", 2, 1));
        RequireOrdered(dup,
            "if(TCheckDepth.IsActive&&head<depth)",
            "returnEvmExceptionType.StackUnderflow;",
            "head++;",
            "if(TCheckDepth.IsActive&&head>=MaxStackSize)",
            "returnEvmExceptionType.StackOverflow;",
            "Head=head;",
            "Unsafe.WriteUnaligned(refto,Unsafe.ReadUnaligned<EvmWord>(reffrom));");
        string swap = Canonical(FindMethod(stack, "Swap", 2, 1));
        RequireOrdered(swap,
            "if(TCheckDepth.IsActive&&head<depth)",
            "returnEvmExceptionType.StackUnderflow;",
            "refbytebottom=refUnsafe.Add(refbytes,headOffset-depthBytes);",
            "refbytetop=refUnsafe.Add(refbytes,headOffset-WordSize);",
            "Unsafe.WriteUnaligned(refbottom,Unsafe.ReadUnaligned<EvmWord>(reftop));",
            "Unsafe.WriteUnaligned(reftop,buffer);");
        string exchange = Canonical(FindMethod(stack, "Exchange", 1, 2));
        RequireOrdered(exchange,
            "intmaxDepth=Math.Max(n,m);",
            "if(!EnsureDepth(maxDepth))returnEvmExceptionType.StackUnderflow;",
            "refbytefirst=refUnsafe.Add(refbytes,headOffset-(nuint)(uint)n*WordSize);",
            "refbytesecond=refUnsafe.Add(refbytes,headOffset-(nuint)(uint)m*WordSize);",
            "Unsafe.WriteUnaligned(reffirst,Unsafe.ReadUnaligned<EvmWord>(refsecond));",
            "Unsafe.WriteUnaligned(refsecond,buffer);");
    }

    private static void ValidateRuntimeAndFork(Dictionary<string, SourceFile> sources)
    {
        string vm = Canonical(sources[VirtualMachinePath].Syntax);
        Require(vm, "EthereumVirtualMachine", "standard Ethereum VM");
        Require(vm, "VirtualMachine<EthereumGasPolicy>", "Ethereum gas-policy closure");
        Require(Canonical(sources[BlockProcessingModulePath].Syntax),
            ".AddScoped<IVirtualMachine,EthereumVirtualMachine>()", "Ethereum VM DI registration");
        Require(Canonical(sources[VirtualMachineStandardPath].Syntax),
            "_opcodeTablesBySpec.GetValue(Spec,static_=>newOpcodeTable())", "fork-keyed opcode table");

        for (int index = 0; index < ForkLineage.Length; index++)
        {
            string fork = ForkLineage[index];
            string parent = index == 0 ? "null" : $"{ForkLineage[index - 1]}.Instance";
            Require(Canonical(sources[$"{ForkDirectory}/{ForkFileName(index)}.cs"].Syntax),
                $"class{fork}():NamedReleaseSpec<{fork}>({parent})", $"{fork} parent closure");
        }
        string named = Canonical(sources[NamedReleaseSpecPath].Syntax);
        RequireOrdered(named, "Parent=parent;", "ReplayAncestors(this);", "ReplayAncestors(fork.Parent);",
            "fork.Apply(this);");
        Require(named, "publicstaticNamedReleaseSpecInstance{get;}=newTSelf();", "fork singleton closure");
        Require(Canonical(sources[$"{ForkDirectory}/25_Amsterdam.cs"].Syntax),
            "spec.IsEip8024Enabled=true;", "Amsterdam EIP-8024 activation");
    }

    private static int ReadVeryLowGas(CompilationUnitSyntax syntax)
    {
        TypeDeclarationSyntax owner = FindOwner(syntax, "GasCostOf", 0);
        FieldDeclarationSyntax field = FindField(owner, "VeryLow");
        VariableDeclaratorSyntax variable = field.Declaration.Variables.Single(value =>
            value.Identifier.ValueText == "VeryLow");
        if (variable.Initializer?.Value is not LiteralExpressionSyntax literal)
            throw new ExtractionException("GasCostOf.VeryLow must have a literal initializer.");
        int actual = Convert.ToInt32(literal.Token.Value, CultureInfo.InvariantCulture);
        if (actual != VeryLowGas) throw new ExtractionException("GasCostOf.VeryLow must remain 3.");
        return actual;
    }

    private static Dictionary<string, int> ReadInstructionValues(CompilationUnitSyntax syntax)
    {
        EnumDeclarationSyntax instruction = (EnumDeclarationSyntax)FindTopLevelType(syntax, "Instruction", 0);
        Dictionary<string, int> values = new(StringComparer.Ordinal);
        foreach (EnumMemberDeclarationSyntax member in instruction.Members)
        {
            if (member.EqualsValue?.Value is LiteralExpressionSyntax literal)
                values[member.Identifier.ValueText] = Convert.ToInt32(literal.Token.Value, CultureInfo.InvariantCulture);
        }
        return values;
    }

    private static Dictionary<string, SourceFile> LoadSources(string root)
    {
        Dictionary<string, SourceFile> result = new(StringComparer.Ordinal);
        foreach (string relativePath in SourcePaths)
        {
            string fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(fullPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new ExtractionException($"Unable to read admitted source {relativePath}: {exception.Message}");
            }
            SyntaxTree tree = CSharpSyntaxTree.ParseText(Encoding.UTF8.GetString(bytes), ParseOptions, relativePath);
            CompilationUnitSyntax syntax = tree.GetCompilationUnitRoot();
            Diagnostic? error = tree.GetDiagnostics().FirstOrDefault(static value =>
                value.Severity == DiagnosticSeverity.Error);
            if (error is not null) throw new ExtractionException($"Roslyn rejected {relativePath}: {error}");
            result.Add(relativePath, new(relativePath, fullPath, bytes, syntax));
        }
        return result;
    }

    private static RawSourceIdentity LoadBuildTargets(string root)
    {
        string path = Path.Combine(root, BuildTargetsPath.Replace('/', Path.DirectorySeparatorChar));
        string sha256 = Hash(File.ReadAllBytes(path));
        if (sha256 != BuildTargetsSha256)
            throw new ExtractionException(
                $"Unadmitted standard/zkEVM build target; expected SHA-256 {BuildTargetsSha256}.");
        return new(BuildTargetsPath, sha256);
    }

    private static List<AdmissionIdentity> AdmitReviewedSyntax(Dictionary<string, SourceFile> sources)
    {
        Dictionary<string, ReviewedSource> reviewed = LoadReviewedSourceIndex();
        List<AdmissionIdentity> identities = [];
        foreach (AdmissionSpec specification in AdmissionSpecs)
        {
            if (!reviewed.TryGetValue(specification.SourcePath, out ReviewedSource? expected))
                throw new ExtractionException($"Missing reviewed source resource for {specification.SourcePath}.");
            string expectedRaw = expected.Sha256;
            string expectedSyntax = expected.RoslynSyntaxSha256;

            SourceFile source = sources[specification.SourcePath];
            string sourceSha = Hash(source.Bytes);
            string syntaxSha = Hash(Encoding.UTF8.GetBytes(Canonical(source.Syntax)));
            if (sourceSha != expectedRaw)
                throw new ExtractionException($"Unadmitted exact source in {specification.SourcePath}.");
            if (syntaxSha != expectedSyntax)
                throw new ExtractionException(
                    $"Unadmitted Roslyn syntax in {specification.SourcePath}; actual SHA-256 {syntaxSha}.");

            SyntaxNode container = specification.OwnerName is null
                ? source.Syntax
                : FindOwner(source.Syntax, specification.OwnerName, specification.OwnerGenericArity);
            foreach (MemberSelector selector in specification.Members)
            {
                MemberDeclarationSyntax member = FindSelectedMember(container, selector);
                string actualNamespace = string.Join(".", member.AncestorsAndSelf()
                    .OfType<BaseNamespaceDeclarationSyntax>()
                    .Reverse()
                    .Select(static value => Canonical(value.Name)));
                if (actualNamespace != specification.Namespace)
                    throw new ExtractionException(
                        $"Expected namespace {specification.Namespace} for {AdmissionKey(specification, selector)}.");
                string template =
                    $"{specification.SourcePath}|{expectedRaw}|{expectedSyntax}";
                identities.Add(new(
                    AdmissionKey(specification, selector),
                    Hash(Encoding.UTF8.GetBytes(template)),
                    syntaxSha));
            }
        }
        ValidateAdmissionIdentities(identities);
        return identities;
    }

    private static Dictionary<string, ReviewedSource> LoadReviewedSourceIndex()
    {
        Assembly assembly = typeof(ExtendedStackOpcodeProfile).Assembly;
        string resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(value => value.EndsWith(AdmissionResourceSuffix, StringComparison.Ordinal))
            ?? throw new ExtractionException("Missing embedded production source admission resource.");
        using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
        using StreamReader reader = new(stream);
        Dictionary<string, ReviewedSource> result = new(StringComparer.Ordinal);
        string? line;
        int lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            string[] fields = line.Split('|');
            if (fields.Length != 3 || fields.Any(static value =>
                    string.IsNullOrWhiteSpace(value) || value != value.Trim()))
                throw new ExtractionException($"Malformed source admission resource line {lineNumber}.");
            RequireSha256(fields[1], $"reviewed raw source hash at line {lineNumber}");
            RequireSha256(fields[2], $"reviewed syntax hash at line {lineNumber}");
            if (!result.TryAdd(fields[0], new(fields[0], fields[1], fields[2])))
                throw new ExtractionException($"Duplicate source admission resource entry {fields[0]}.");
        }
        return result;
    }

    private static void ValidateNoCompetingMembers(string root, Dictionary<string, SourceFile> sources)
    {
        HashSet<string> known = sources.Values.Select(static value => Path.GetFullPath(value.FullPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] directories =
        [
            "src/Nethermind/Nethermind.Evm",
            "src/Nethermind/Nethermind.Core",
            "src/Nethermind/Nethermind.Init",
            ForkDirectory,
        ];
        foreach (string directory in directories)
        {
            string absolute = Path.Combine(root, directory.Replace('/', Path.DirectorySeparatorChar));
            foreach (string file in Directory.EnumerateFiles(absolute, "*.cs", SearchOption.AllDirectories))
            {
                string portable = file.Replace(Path.DirectorySeparatorChar, '/');
                if (known.Contains(Path.GetFullPath(file)) ||
                    portable.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
                    portable.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
                    portable.EndsWith(".zkevm.cs", StringComparison.OrdinalIgnoreCase) ||
                    portable.Contains("/zkevm/", StringComparison.OrdinalIgnoreCase)) continue;
                CompilationUnitSyntax syntax = CSharpSyntaxTree.ParseText(
                    File.ReadAllText(file), ParseOptions, portable).GetCompilationUnitRoot();
                foreach (AdmissionSpec specification in AdmissionSpecs)
                {
                    if (specification.OwnerName is null) continue;
                    foreach (TypeDeclarationSyntax owner in syntax.DescendantNodesAndSelf()
                                 .OfType<TypeDeclarationSyntax>())
                    {
                        if (owner.Identifier.ValueText != specification.OwnerName ||
                            GenericArity(owner) != specification.OwnerGenericArity) continue;
                        foreach (MemberSelector selector in specification.Members)
                        {
                            if (Competes(owner, selector))
                                throw new ExtractionException(
                                    $"Competing selected declaration in {portable}: {specification.OwnerName}.{SelectorKey(selector)}.");
                        }
                    }
                }
            }
        }
    }

    private static bool Competes(TypeDeclarationSyntax owner, MemberSelector selector) => selector.Kind switch
    {
        "method" => owner.Members.OfType<MethodDeclarationSyntax>().Any(value => Matches(value, selector)),
        "type" => owner.Members.OfType<BaseTypeDeclarationSyntax>().Any(value => Matches(value, selector)),
        "field" => owner.Members.OfType<FieldDeclarationSyntax>().Any(value => Matches(value, selector)),
        _ => false,
    };

    private static MemberDeclarationSyntax FindSelectedMember(SyntaxNode container, MemberSelector selector)
    {
        MemberDeclarationSyntax[] matches = DirectMembers(container).Where(value => Matches(value, selector)).ToArray();
        if (matches.Length != 1)
            throw new ExtractionException($"Expected exactly one {SelectorKey(selector)} member; found {matches.Length}.");
        return matches[0];
    }

    private static IEnumerable<MemberDeclarationSyntax> DirectMembers(SyntaxNode container) => container switch
    {
        CompilationUnitSyntax unit => unit.Members.SelectMany(static member => member is BaseNamespaceDeclarationSyntax ns
            ? ns.Members
            : [member]),
        TypeDeclarationSyntax type => type.Members,
        _ => throw new ExtractionException($"Unsupported Roslyn admission container {container.Kind()}.")
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
            field.Declaration.Variables.Any(value => value.Identifier.ValueText == selector.Name),
        _ => false,
    };

    private static string ParameterTypes(MethodDeclarationSyntax method) => string.Join(",",
        method.ParameterList.Parameters.Select(static parameter =>
            string.Concat(parameter.Modifiers.Select(static modifier => modifier.Text)) + Canonical(parameter.Type!)));

    private static TypeDeclarationSyntax FindOwner(CompilationUnitSyntax syntax, string name, int genericArity)
    {
        TypeDeclarationSyntax[] matches = syntax.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(value => value.Identifier.ValueText == name && GenericArity(value) == genericArity)
            .ToArray();
        if (matches.Length != 1)
            throw new ExtractionException($"Expected exactly one owner {name}/{genericArity}; found {matches.Length}.");
        return matches[0];
    }

    private static BaseTypeDeclarationSyntax FindTopLevelType(
        CompilationUnitSyntax syntax,
        string name,
        int genericArity)
    {
        BaseTypeDeclarationSyntax[] matches = DirectMembers(syntax).OfType<BaseTypeDeclarationSyntax>()
            .Where(value => value.Identifier.ValueText == name && GenericArity(value) == genericArity)
            .ToArray();
        if (matches.Length != 1)
            throw new ExtractionException(
                $"Expected exactly one top-level type {name}/{genericArity}; found {matches.Length}.");
        return matches[0];
    }

    private static TypeDeclarationSyntax FindNestedType(TypeDeclarationSyntax owner, string name, int genericArity) =>
        (TypeDeclarationSyntax)FindSelectedMember(owner, T(name, genericArity));

    private static MethodDeclarationSyntax FindMethod(
        TypeDeclarationSyntax owner,
        string name,
        int genericArity,
        int parameterCount) =>
        (MethodDeclarationSyntax)FindSelectedMember(owner, M(name, genericArity, parameterCount));

    private static FieldDeclarationSyntax FindField(TypeDeclarationSyntax owner, string name) =>
        (FieldDeclarationSyntax)FindSelectedMember(owner, F(name));

    private static int GenericArity(BaseTypeDeclarationSyntax type) => type is TypeDeclarationSyntax declaration
        ? declaration.TypeParameterList?.Parameters.Count ?? 0
        : 0;

    private static List<SourceIdentity> BuildSourceIdentities(Dictionary<string, SourceFile> sources) => sources.Values
        .OrderBy(static value => value.Path, StringComparer.Ordinal)
        .Select(static value => new SourceIdentity(
            value.Path,
            Hash(value.Bytes),
            Hash(Encoding.UTF8.GetBytes(Canonical(value.Syntax)))))
        .ToList();

    private static string CombinedSourceHash(
        IReadOnlyList<SourceIdentity> sources,
        RawSourceIdentity buildTargets) => Hash(Encoding.UTF8.GetBytes(string.Join(
        "\n",
        sources.Select(static value => $"{value.Path}\0{value.Sha256}\0{value.RoslynSyntaxSha256}")
            .Append($"{buildTargets.Path}\0{buildTargets.Sha256}\0raw"))));

    private static byte[] Serialize<T>(T value) => Utf8WithoutBom.GetBytes(
        JsonSerializer.Serialize(value, JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");

    private static void ValidateNoDuplicateJsonProperties(byte[] bytes, string description)
    {
        Utf8JsonReader reader = new(bytes);
        Stack<HashSet<string>> objectProperties = new();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objectProperties.Push(new(StringComparer.Ordinal));
                    break;
                case JsonTokenType.EndObject:
                    _ = objectProperties.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    string property = reader.GetString() ??
                        throw new ExtractionException($"Serialized {description} contains a null property name.");
                    if (objectProperties.Count == 0 || !objectProperties.Peek().Add(property))
                        throw new ExtractionException(
                            $"Serialized {description} contains duplicate property '{property}'.");
                    break;
            }
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Canonical(SyntaxNode node) => string.Concat(
        node.DescendantTokens(descendIntoTrivia: false).Select(static token => token.Text));

    private static void Require(string source, string required, string description)
    {
        if (!source.Contains(required, StringComparison.Ordinal))
            throw new ExtractionException($"Rejected {description}; expected '{required}'.");
    }

    private static void RequireExact(string source, string expected, string description)
    {
        if (source != expected)
            throw new ExtractionException($"Rejected {description}; expected exact admitted member '{expected}'.");
    }

    private static void RequireOrdered(string source, params string[] fragments)
    {
        int position = -1;
        foreach (string fragment in fragments)
        {
            int next = source.IndexOf(fragment, position + 1, StringComparison.Ordinal);
            if (next < 0)
                throw new ExtractionException(
                    $"Rejected production order; expected '{fragment}' after position {position}.");
            position = next;
        }
    }

    private static void RequireNonEmpty(params string?[] values)
    {
        if (values.Any(static value => string.IsNullOrWhiteSpace(value)))
            throw new ExtractionException("Serialized document contains null or empty claim-relevant fields.");
    }

    internal static void RequireSha256(string? value, string description)
    {
        if (value is null || value.Length != 64 || value.Any(static character =>
                (character is < '0' or > '9') && (character is < 'a' or > 'f')))
            throw new ExtractionException($"Serialized source manifest contains an invalid {description}.");
    }

    private static AdmissionSpec Spec(
        string path,
        string stem,
        string ns,
        string? owner,
        int ownerArity,
        params MemberSelector[] members) => new(path, stem, ns, owner, ownerArity, members);

    private static MemberSelector M(string name, int genericArity, int parameterCount, string parameterTypes = "") =>
        new("method", name, genericArity, parameterCount, parameterTypes);

    private static MemberSelector T(string name, int genericArity = 0) => new("type", name, genericArity);

    private static MemberSelector F(string name) => new("field", name);

    private static string SelectorKey(MemberSelector selector) =>
        $"{selector.Kind}:{selector.Name}/{selector.GenericArity}/{selector.ParameterCount}/{selector.ParameterTypes}";

    private static string AdmissionKey(AdmissionSpec specification, MemberSelector selector)
    {
        string owner = specification.OwnerName is null
            ? "compilation-unit/0"
            : $"{specification.OwnerName}/{specification.OwnerGenericArity}";
        return $"{specification.SourcePath}:{specification.Namespace}:{AdmissionResourceName}:{specification.ResourceStem}:{owner}:{SelectorKey(selector)}";
    }

    private static string ForkFileName(int index) => index switch
    {
        0 => "00_Olympic",
        1 => "01_Frontier",
        2 => "02_Homestead",
        3 => "03_Dao",
        4 => "04_TangerineWhistle",
        5 => "05_SpuriousDragon",
        6 => "06_Byzantium",
        7 => "07_Constantinople",
        8 => "08_ConstantinopleFix",
        9 => "09_Istanbul",
        10 => "10_MuirGlacier",
        11 => "11_Berlin",
        12 => "12_London",
        13 => "13_ArrowGlacier",
        14 => "14_GrayGlacier",
        15 => "15_Paris",
        16 => "16_Shanghai",
        17 => "17_Cancun",
        18 => "18_Prague",
        19 => "19_Osaka",
        20 => "20_BPO1",
        21 => "21_BPO2",
        22 => "25_Amsterdam",
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };
}
