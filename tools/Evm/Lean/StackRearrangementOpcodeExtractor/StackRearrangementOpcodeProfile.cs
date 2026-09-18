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

namespace Nethermind.Evm.Lean.StackRearrangementOpcodeExtractor;

internal static class StackRearrangementOpcodeProfile
{
    internal const int SchemaVersion = 1;
    internal const string ExtractorVersion = "1.0.0";
    internal const string IrFileName = "StackRearrangementOpcodeKernel.ir.json";
    internal const string ManifestFileName = "StackRearrangementOpcodeKernel.source-manifest.json";
    internal const string DefaultLeanRelativePath =
        "tools/Evm/Lean/Eip803x/Generated/StackRearrangementOpcodeKernel.lean";

    internal const string OpcodeHandlersPath =
        "src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs";
    internal const string DispatchPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs";
    internal const string InstructionPath = "src/Nethermind/Nethermind.Evm/Instruction.cs";
    internal const string GasCostPath = "src/Nethermind/Nethermind.Core/GasCostOf.cs";
    internal const string GasTagsPath = "src/Nethermind/Nethermind.Evm/GasPolicy/IGasCost.cs";
    internal const string EvmStackPath = "src/Nethermind/Nethermind.Evm/EvmStack.cs";
    internal const string StackInstructionsPath =
        "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Stack.cs";
    internal const string IgGasPolicyPath = "src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs";
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
    internal const string BuildTargetsSha256 =
        "0598cebaef1df41102a18a3f9ace810bed1e4e64b8471d055724b4a394dccec6";
    internal const int StackLimit = 1024;

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
        "Roslyn admission and source routing do not prove the CLR, JIT, or unsafe EvmStack execution.",
        "The bounded reference models top-first UInt256 words and the 1024-word stack only; bytecode frames, memory, tracing, cancellation, and allocation limits remain provider obligations.",
        "The theorem-free generated machine consumes the admitted descriptor plan; its correspondence to the handwritten reference is proved only in the separate refinement, not by generated code.",
        "Exceptional-frame settlement, opcode counters, tail calls, trace callbacks, and transaction/block integration are outside this single-opcode projection.",
    ];

    private static readonly AdmissionSpec[] AdmissionSpecs = BuildAdmissionSpecs();
    private static readonly OpcodeSeed[] Seeds = BuildSeeds();

    internal static IReadOnlyList<string> SourcePaths => AdmissionSpecs
        .Select(static specification => specification.SourcePath)
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
        ValidateProductionClosure(root, sources);

        Dictionary<string, int> gasConstants = ReadGasConstants(sources[GasCostPath].Syntax);
        IReadOnlyList<OpcodeDescriptor> opcodes = ExtractOpcodes(sources, gasConstants);
        IReadOnlyList<OpcodeSpecialization> specializations = BuildSpecializations(opcodes);
        IrDocument document = new(
            SchemaVersion,
            ExtractorVersion,
            "StackRearrangementOpcodeKernel",
            Root,
            GasPolicy,
            Fork,
            BuildFlavor,
            "EthereumVirtualMachine -> VirtualMachine<EthereumGasPolicy> -> IVirtualMachine",
            "Olympic -> ... -> BPO1 -> BPO2 -> Amsterdam",
            "pc-before-gas",
            "gas-before-stack",
            "out-of-gas-before-underflow-before-overflow",
            StackLimit,
            gasConstants["Base"],
            gasConstants["VeryLow"],
            DispatchTables,
            ForkLineage,
            OpenExtractionObligations,
            opcodes,
            specializations);

        byte[] irBytes = Serialize(document);
        IrDocument serializedDocument = DeserializeIr(irBytes);
        List<SourceIdentity> sourceIdentities = BuildSourceIdentities(sources);
        string combinedSourceSha256 = Hash(Encoding.UTF8.GetBytes(string.Join(
            "\n",
            sourceIdentities.Select(static identity =>
                    $"{identity.Path}\0{identity.Sha256}\0{identity.RoslynSyntaxSha256}")
                .Append($"{buildTargets.Path}\0{buildTargets.Sha256}\0raw"))));
        byte[] leanBytes = StackRearrangementOpcodeLeanEmitter.Emit(
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
            throw new ExtractionException($"Serialized source manifest is malformed or has unknown members: {exception.Message}");
        }
    }

    internal static void ValidateIr(IrDocument document)
    {
        RequireNonEmpty(document.ExtractorVersion, document.Kernel, document.Root, document.GasPolicy,
            document.Fork, document.BuildFlavor, document.DiClosure, document.ForkClosure,
            document.PcOrder, document.GasOrder, document.FaultOrder);
        if (document.SchemaVersion != SchemaVersion || document.ExtractorVersion != ExtractorVersion ||
            document.Kernel != "StackRearrangementOpcodeKernel" || document.Root != Root ||
            document.GasPolicy != GasPolicy || document.Fork != Fork || document.BuildFlavor != BuildFlavor ||
            document.DiClosure != "EthereumVirtualMachine -> VirtualMachine<EthereumGasPolicy> -> IVirtualMachine" ||
            document.ForkClosure != "Olympic -> ... -> BPO1 -> BPO2 -> Amsterdam" ||
            document.PcOrder != "pc-before-gas" || document.GasOrder != "gas-before-stack" ||
            document.FaultOrder != "out-of-gas-before-underflow-before-overflow" ||
            document.StackLimit != StackLimit || document.PopGas != 2 || document.RearrangementGas != 3)
            throw new ExtractionException("Serialized IR header, fork, gas, stack, or order metadata changed.");

        if (document.DispatchTables is null || document.ForkLineage is null ||
            document.OpenExtractionObligations is null || document.Opcodes is null ||
            document.Specializations is null || document.DispatchTables.Any(static table => table is null) ||
            document.ForkLineage.Any(static value => value is null) ||
            document.OpenExtractionObligations.Any(static value => value is null) ||
            document.Opcodes.Any(static descriptor => descriptor is null) ||
            document.Specializations.Any(static specialization => specialization is null))
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
            RequireNonEmpty(
                descriptor.Name,
                descriptor.Instruction,
                descriptor.HandlerBody,
                descriptor.HandlerRoot,
                descriptor.DispatchAssignment,
                descriptor.GasClass,
                descriptor.StackEffect,
                descriptor.PcOrder,
                descriptor.GasOrder,
                descriptor.FaultOrder,
                descriptor.Activation,
                descriptor.Fork,
                descriptor.GasPolicy,
                descriptor.VmRoot);
            if (descriptor.OpcodeByte is < 0 or > 255 || descriptor.FixedGas < 0 ||
                descriptor.StackInputs < 0 || descriptor.StackGrowth < 0 || descriptor.OperandDepth < 0)
                throw new ExtractionException("Serialized IR contains an invalid opcode numeric field.");
        }

        if (!document.Opcodes.SequenceEqual(ExpectedDescriptors()) ||
            !document.Specializations.SequenceEqual(ExpectedSpecializations(document.Opcodes)))
            throw new ExtractionException("Serialized IR descriptor, stack, gas, activation, order, or root metadata changed.");
        if (document.Opcodes.Count != 33 || document.Specializations.Count != 132 ||
            document.Opcodes.Select(static opcode => opcode.Name).Distinct(StringComparer.Ordinal).Count() != 33 ||
            document.Opcodes.Select(static opcode => opcode.OpcodeByte).Distinct().Count() != 33)
            throw new ExtractionException("Serialized IR must contain exactly 33 unique standard-mainnet opcodes and 132 roots.");
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
            manifest.Sources.Any(static source => source is null) ||
            manifest.RawSources.Any(static source => source is null) ||
            manifest.Admissions.Any(static admission => admission is null))
            throw new ExtractionException("Serialized source manifest contains invalid header or null entries.");
        string[] expectedSourcePaths = SourcePaths.OrderBy(static path => path, StringComparer.Ordinal).ToArray();
        if (manifest.Sources.Count != SourcePaths.Count || manifest.RawSources.Count != 1 ||
            manifest.RawSources[0].Path != BuildTargetsPath || manifest.RawSources[0].Sha256 != BuildTargetsSha256 ||
            manifest.Admissions.Count != AdmissionSpecs.Sum(static specification => specification.Members.Count) ||
            manifest.Sources.Select(static source => source.Path).Distinct(StringComparer.Ordinal).Count() != manifest.Sources.Count ||
            !manifest.Sources.Select(static source => source.Path).SequenceEqual(expectedSourcePaths, StringComparer.Ordinal))
            throw new ExtractionException("Serialized source manifest source or admission closure changed.");
        Dictionary<string, ReviewedSource> reviewed = LoadReviewedSourceIndex();
        foreach (SourceIdentity source in manifest.Sources)
        {
            RequireNonEmpty(source.Path, source.Sha256, source.RoslynSyntaxSha256);
            RequireSha256(source.Sha256, $"source hash for {source.Path}");
            RequireSha256(source.RoslynSyntaxSha256, $"Roslyn syntax hash for {source.Path}");
            if (!reviewed.TryGetValue(source.Path, out ReviewedSource? expected) ||
                !source.Sha256.Equals(expected.Sha256, StringComparison.Ordinal) ||
                (expected.RoslynSyntaxSha256 is not null &&
                 !source.RoslynSyntaxSha256.Equals(expected.RoslynSyntaxSha256, StringComparison.Ordinal)))
                throw new ExtractionException($"Serialized source manifest changed admitted source {source.Path}.");
        }
        foreach (RawSourceIdentity source in manifest.RawSources)
        {
            RequireNonEmpty(source.Path, source.Sha256);
            RequireSha256(source.Sha256, $"raw source hash for {source.Path}");
        }
        string expectedCombinedSourceSha256 = Hash(Encoding.UTF8.GetBytes(string.Join(
            "\n",
            manifest.Sources.Select(static source =>
                    $"{source.Path}\0{source.Sha256}\0{source.RoslynSyntaxSha256}")
                .Append($"{manifest.RawSources[0].Path}\0{manifest.RawSources[0].Sha256}\0raw"))));
        if (!manifest.CombinedSourceSha256.Equals(expectedCombinedSourceSha256, StringComparison.Ordinal))
            throw new ExtractionException("Serialized source manifest combined source hash changed.");
        IrDocument expectedDocument = ExpectedIrDocument();
        byte[] expectedIrBytes = Serialize(expectedDocument);
        byte[] expectedLeanBytes = StackRearrangementOpcodeLeanEmitter.Emit(
            expectedDocument,
            Hash(expectedIrBytes),
            expectedCombinedSourceSha256);
        if (!manifest.IrSha256.Equals(Hash(expectedIrBytes), StringComparison.Ordinal) ||
            !manifest.LeanSha256.Equals(Hash(expectedLeanBytes), StringComparison.Ordinal))
            throw new ExtractionException("Serialized source manifest IR or Lean hash changed.");
        ValidateAdmissionIdentities(manifest.Admissions);
        string[] expectedAdmissionKeys = AdmissionSpecs
            .SelectMany(static specification => specification.Members.Select(selector => AdmissionKey(specification, selector)))
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();
        if (!manifest.Admissions.Select(static admission => admission.Key)
                .OrderBy(static key => key, StringComparer.Ordinal)
                .SequenceEqual(expectedAdmissionKeys, StringComparer.Ordinal))
            throw new ExtractionException("Serialized source manifest admission identities changed.");

        Dictionary<string, string> sourceSyntaxHashes = manifest.Sources.ToDictionary(
            static source => source.Path,
            static source => source.RoslynSyntaxSha256,
            StringComparer.Ordinal);
        Dictionary<string, string> expectedAdmissionTemplates = [];
        foreach (AdmissionSpec specification in AdmissionSpecs)
        {
            ReviewedSource expected = reviewed[specification.SourcePath];
            foreach (MemberSelector selector in specification.Members)
            {
                string key = AdmissionKey(specification, selector);
                string template =
                    $"{specification.SourcePath}|{expected.Sha256}|{expected.RoslynSyntaxSha256 ?? "syntax-pinned-at-extraction"}";
                expectedAdmissionTemplates.Add(key, Hash(Encoding.UTF8.GetBytes(template)));
            }
        }
        foreach (AdmissionIdentity admission in manifest.Admissions)
        {
            if (!expectedAdmissionTemplates.TryGetValue(admission.Key, out string? expectedTemplate) ||
                !admission.TemplateSha256.Equals(expectedTemplate, StringComparison.Ordinal))
                throw new ExtractionException($"Serialized admission template identity changed: {admission.Key}.");
            string sourcePath = admission.Key[..admission.Key.IndexOf(':', StringComparison.Ordinal)];
            if (!sourceSyntaxHashes.TryGetValue(sourcePath, out string? syntaxHash) ||
                !admission.SourceSyntaxSha256.Equals(syntaxHash, StringComparison.Ordinal))
                throw new ExtractionException($"Serialized admission syntax identity changed: {admission.Key}.");
        }
    }

    internal static void ValidateAdmissionIdentities(IReadOnlyList<AdmissionIdentity> identities)
    {
        if (identities?.Any(static identity => identity is null) != false)
            throw new ExtractionException("Admission identities contain null entries.");
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (AdmissionIdentity identity in identities)
        {
            RequireNonEmpty(identity.Key, identity.TemplateSha256, identity.SourceSyntaxSha256);
            RequireSha256(identity.TemplateSha256, $"admission template for {identity.Key}");
            RequireSha256(identity.SourceSyntaxSha256, $"admission syntax for {identity.Key}");
            if (!keys.Add(identity.Key))
                throw new ExtractionException($"Duplicate owner/path/resource-qualified admission identity {identity.Key}.");
        }
    }

    internal static IReadOnlyList<OpcodeDescriptor> ExpectedDescriptors() =>
        Seeds.Select(static seed => new OpcodeDescriptor(
            seed.Name,
            seed.Instruction,
            seed.ExpectedByte,
            seed.HandlerBody,
            "ordinary",
            $"lookup[(int)Instruction.{seed.Instruction}]=OpcodeHandler<{seed.HandlerBody},TTracingInst,TCancelable>();",
            seed.GasClass,
            seed.FixedGas,
            seed.StackInputs,
            seed.StackGrowth,
            seed.OperandDepth,
            seed.StackEffect,
            "pc-before-gas",
            "gas-before-stack",
            "out-of-gas-before-underflow-before-overflow",
            "Amsterdam",
            Fork,
            GasPolicy,
            Root)).ToArray();

    private static IrDocument ExpectedIrDocument()
    {
        IReadOnlyList<OpcodeDescriptor> opcodes = ExpectedDescriptors();
        return new(
            SchemaVersion,
            ExtractorVersion,
            "StackRearrangementOpcodeKernel",
            Root,
            GasPolicy,
            Fork,
            BuildFlavor,
            "EthereumVirtualMachine -> VirtualMachine<EthereumGasPolicy> -> IVirtualMachine",
            "Olympic -> ... -> BPO1 -> BPO2 -> Amsterdam",
            "pc-before-gas",
            "gas-before-stack",
            "out-of-gas-before-underflow-before-overflow",
            StackLimit,
            2,
            3,
            DispatchTables,
            ForkLineage,
            OpenExtractionObligations,
            opcodes,
            ExpectedSpecializations(opcodes));
    }

    internal static IReadOnlyList<OpcodeSpecialization> ExpectedSpecializations(
        IReadOnlyList<OpcodeDescriptor> opcodes) =>
        opcodes.SelectMany(static opcode => DispatchTables.Select(table =>
        {
            string handler = opcode.HandlerBody.Replace("TTracingInst", table.TracingFlag, StringComparison.Ordinal);
            string closedRoot =
                $"VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<{handler},{table.TracingFlag},{table.CancelableFlag},OnFlag>";
            return new OpcodeSpecialization(
                opcode.Name,
                table.Name,
                table.TracingFlag,
                table.CancelableFlag,
                "OnFlag",
                closedRoot,
                opcode.HandlerRoot);
        })).ToArray();

    private static AdmissionSpec[] BuildAdmissionSpecs()
    {
        List<AdmissionSpec> specifications =
        [
            Spec(GasCostPath, "GasCostOf", "Nethermind.Core", "GasCostOf", 0, F("Base"), F("VeryLow")),
            Spec(GasTagsPath, "GasTags", "Nethermind.Evm.GasPolicy", null, 0,
                T("IGasCost"), T("BaseGasCost"), T("VeryLowGasCost")),
            Spec(TypeFlagsPath, "TypeFlags", "Nethermind.Core", null, 0,
                T("IFlag"), T("OffFlag"), T("OnFlag")),
            Spec(InstructionPath, "Instruction", "Nethermind.Evm", null, 0, T("Instruction")),
            Spec(DispatchFlagsPath, "DispatchFlags", "Nethermind.Evm", null, 0, T("DispatchFlags")),
            Spec(VirtualMachinePath, "EthereumVirtualMachine", "Nethermind.Evm", null, 0,
                T("EthereumVirtualMachine")),
            Spec(VirtualMachineStandardPath, "VirtualMachineStandard", "Nethermind.Evm", "VirtualMachine", 1,
                F("_opcodeTablesBySpec"), MAny("GetOpcodeTable"), MAny("ShouldRefreshOpcodes")),
            Spec(BlockProcessingModulePath, "BlockProcessingModule", "Nethermind.Init.Modules",
                "BlockProcessingModule", 0, MAny("Load")),
            Spec(NamedReleaseSpecPath, "NamedReleaseSpec", "Nethermind.Specs.Forks", null, 0,
                T("NamedReleaseSpec"), T("NamedReleaseSpec", 1)),
            Spec(DispatchPath, "VirtualMachineDispatch", "Nethermind.Evm", "VirtualMachine", 1,
                T("OpcodeTable"), MAny("ExecuteOpcode")),
            Spec(OpcodeHandlersPath, "VirtualMachineOpcodeHandlers", "Nethermind.Evm", "VirtualMachine", 1,
                T("PopOpcode"), T("DupOpcode", 2), T("SwapOpcode", 2)),
            Spec(EvmStackPath, "EvmStack", "Nethermind.Evm", null, 0, T("EvmStack")),
            Spec(StackInstructionsPath, "InstructionStack", "Nethermind.Evm", null, 0, T("EvmInstructions")),
            Spec(IgGasPolicyPath, "IGasPolicy", "Nethermind.Evm.GasPolicy", null, 0, T("IGasPolicy", 1)),
            Spec(EthereumGasPolicyPath, "EthereumGasPolicy", "Nethermind.Evm.GasPolicy", null, 0,
                T("EthereumGasPolicy")),
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
            specifications.Add(Spec(
                $"{ForkDirectory}/{forkFiles[index]}.cs",
                "Fork" + ForkLineage[index],
                "Nethermind.Specs.Forks",
                null,
                0,
                T(ForkLineage[index])));
        return specifications.ToArray();
    }

    private static OpcodeSeed[] BuildSeeds()
    {
        List<OpcodeSeed> seeds =
        [new("pop", "POP", 0x50, "PopOpcode", "base", 2, 1, 0, 0, "pop")];
        for (int depth = 1; depth <= 16; depth++)
        {
            seeds.Add(new(
                $"dup{depth}",
                $"DUP{depth}",
                0x7f + depth,
                $"DupOpcode<EvmInstructions.Op{depth},TTracingInst>",
                "veryLow",
                3,
                depth,
                1,
                depth,
                "duplicate"));
        }
        for (int depth = 1; depth <= 16; depth++)
        {
            seeds.Add(new(
                $"swap{depth}",
                $"SWAP{depth}",
                0x8f + depth,
                $"SwapOpcode<EvmInstructions.Op{depth},TTracingInst>",
                "veryLow",
                3,
                depth + 1,
                0,
                depth,
                "swap"));
        }
        return seeds.ToArray();
    }

    private static IReadOnlyList<OpcodeDescriptor> ExtractOpcodes(
        Dictionary<string, SourceFile> sources,
        IReadOnlyDictionary<string, int> gasConstants)
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
            if (!instructionValues.TryGetValue(seed.Instruction, out int actualByte) || actualByte != seed.ExpectedByte)
                throw new ExtractionException($"Instruction.{seed.Instruction} is not the admitted byte 0x{seed.ExpectedByte:X2}.");
            if (!bytes.Add(actualByte)) throw new ExtractionException($"Duplicate opcode byte 0x{actualByte:X2}.");
            if (!assignments.TryGetValue(seed.Instruction, out (string HandlerBody, string DispatchAssignment) assignment))
                throw new ExtractionException($"No live dispatch assignment for Instruction.{seed.Instruction}.");
            if (assignment.HandlerBody != seed.HandlerBody)
                throw new ExtractionException($"Instruction.{seed.Instruction} has an unadmitted handler body.");

            descriptors.Add(new(
                seed.Name,
                seed.Instruction,
                actualByte,
                assignment.HandlerBody,
                "ordinary",
                assignment.DispatchAssignment,
                seed.GasClass,
                gasConstants[seed.GasClass == "base" ? "Base" : "VeryLow"],
                seed.StackInputs,
                seed.StackGrowth,
                seed.OperandDepth,
                seed.StackEffect,
                "pc-before-gas",
                "gas-before-stack",
                "out-of-gas-before-underflow-before-overflow",
                "Amsterdam",
                Fork,
                GasPolicy,
                Root));
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
            if (assignment.Parent is not ExpressionStatementSyntax statement || statement.Parent != generator.Body)
                throw new ExtractionException($"Instruction.{instruction} dispatch assignment is not a live direct assignment.");
            if (assignment.Right is not InvocationExpressionSyntax invocation ||
                invocation.Expression is not GenericNameSyntax generic ||
                generic.Identifier.ValueText != "OpcodeHandler" || generic.TypeArgumentList.Arguments.Count != 3)
                throw new ExtractionException($"Instruction.{instruction} dispatch assignment has an unexpected root.");
            string handlerBody = Canonical(generic.TypeArgumentList.Arguments[0]);
            string dispatchAssignment = Canonical(statement);
            if (!assignments.TryAdd(instruction, (handlerBody, dispatchAssignment)))
                throw new ExtractionException($"Duplicate live dispatch assignment for Instruction.{instruction}.");
        }
        return assignments;
    }

    private static IReadOnlyList<OpcodeSpecialization> BuildSpecializations(
        IReadOnlyList<OpcodeDescriptor> opcodes) => ExpectedSpecializations(opcodes);

    private static void ValidateProductionClosure(string root, Dictionary<string, SourceFile> sources)
    {
        ValidateBuildSelection(root, sources);
        ValidateGasAndStack(sources);
        ValidateHandlers(sources);
        ValidateDispatch(sources);
        ValidateRuntimeAndDi(sources);
        ValidateForkReachability(sources);
    }

    private static void ValidateBuildSelection(string root, Dictionary<string, SourceFile> sources)
    {
        string targets = File.ReadAllText(Path.Combine(root, BuildTargetsPath.Replace('/', Path.DirectorySeparatorChar)));
        Require(targets, "<ItemGroup Condition=\"'$(EnableZkEvm)' == 'true'\">", "zkEVM source exclusion group");
        Require(targets, "<Compile Remove=\"**/std/**/*.cs\" />", "standard source exclusion");
        Require(targets, "<Compile Remove=\"**/*.std.cs\" />", "standard source exclusion");
        Require(targets, "<ItemGroup Condition=\"'$(EnableZkEvm)' != 'true'\">", "standard source selection group");
        Require(targets, "<Compile Remove=\"**/zkevm/**/*.cs\" />", "zkEVM source exclusion");
        Require(targets, "<Compile Remove=\"**/*.zkevm.cs\" />", "zkEVM source exclusion");

        string flags = Canonical(sources[DispatchFlagsPath].Syntax);
        Require(flags, "publicconstboolConstTracing=true;", "standard tracing selector");
        Require(flags, "publicstaticboolTracing(boolisTracing)=>isTracing;", "standard tracing identity");
        Require(flags, "publicstaticboolCancelable(booltracerIsCancelable)=>tracerIsCancelable;", "standard cancellation identity");
    }

    private static void ValidateGasAndStack(Dictionary<string, SourceFile> sources)
    {
        string gasTags = Canonical(sources[GasTagsPath].Syntax);
        Require(gasTags,
            "publicreadonlystructBaseGasCost:IGasCost{publicstaticulongGasCost=>GasCostOf.Base;}",
            "base gas tag forwarding");
        Require(gasTags,
            "publicreadonlystructVeryLowGasCost:IGasCost{publicstaticulongGasCost=>GasCostOf.VeryLow;}",
            "very-low gas tag forwarding");

        string gas = Canonical(sources[EthereumGasPolicyPath].Syntax);
        Require(gas, "if(GetRemainingGas(ingas)<gasCost)", "Ethereum gas affordability check");
        Require(gas, "gas.Value=0;", "Ethereum out-of-gas exhaustion");
        Require(gas, "ConsumeRaw(refgas,gasCost);", "Ethereum gas debit");

        string stack = Canonical(sources[EvmStackPath].Syntax);
        Require(stack, "publicconstintMaxStackSize=1025;", "EVM stack sentinel");
        Require(stack, "publicreadonlyboolEnsureDepth(intdepth)=>Head>=depth;", "EVM stack depth check");
        Require(stack, "publicEvmExceptionTypeDup<TTracingInst,TCheckDepth>(intdepth)", "EVM duplicate primitive");
        Require(stack, "publicreadonlyEvmExceptionTypeSwap<TTracingInst,TCheckDepth>(intdepth)", "EVM swap primitive");

        string instructions = Canonical(sources[StackInstructionsPath].Syntax);
        Require(instructions, "publicstaticEvmExceptionTypeInstructionDup<TGasPolicy,TOpCount,TTracingInst>", "DUP gas route");
        Require(instructions, "if(!TGasPolicy.UpdateGas<VeryLowGasCost>(refgas))returnEvmExceptionType.OutOfGas;", "DUP gas charge");
        Require(instructions, "returnstack.Dup<TTracingInst,OnFlag>(TOpCount.Count);", "DUP stack route");
        Require(instructions, "publicstaticEvmExceptionTypeInstructionSwap<TGasPolicy,TOpCount,TTracingInst>", "SWAP gas route");
        Require(instructions, "returnstack.Swap<TTracingInst,OnFlag>(TOpCount.Count+1);", "SWAP stack route");
        string policy = Canonical(sources[IgGasPolicyPath].Syntax);
        Require(policy, "staticvirtualboolUpdateGas<TCost>(refTSelfgas)", "generic gas policy route");
    }

    private static void ValidateHandlers(Dictionary<string, SourceFile> sources)
    {
        TypeDeclarationSyntax owner = FindOwner(sources[OpcodeHandlersPath].Syntax, "VirtualMachine", 1);
        string pop = Canonical(FindNestedType(owner, "PopOpcode", 0));
        Require(pop, "publicstaticboolHasCheckedBody=>true;", "POP checked body");
        Require(pop, "TGasPolicy.UpdateGas<GasPolicy.BaseGasCost>(refgas);", "POP gas route");
        Require(pop, "publicstaticintStackInputs=>1;", "POP stack arity");
        Require(pop, "stack.Head--;", "POP stack mutation");

        string dup = Canonical(FindNestedType(owner, "DupOpcode", 2));
        Require(dup, "get=>!TTracingInst.IsActive;", "DUP tracing specialization");
        Require(dup, "TGasPolicy.UpdateGas<GasPolicy.VeryLowGasCost>(refgas);", "DUP handler gas route");
        Require(dup, "publicstaticintStackInputs=>TOpCount.Count;", "DUP handler stack arity");
        Require(dup, "publicstaticintStackGrowth=>1;", "DUP handler stack growth");
        Require(dup, "stack.Dup<TTracingInst,OffFlag>(TOpCount.Count)", "DUP checked primitive");
        Require(dup, "EvmInstructions.InstructionDup<TGasPolicy,TOpCount,TTracingInst>", "DUP traced primitive");

        string swap = Canonical(FindNestedType(owner, "SwapOpcode", 2));
        Require(swap, "get=>!TTracingInst.IsActive;", "SWAP tracing specialization");
        Require(swap, "TGasPolicy.UpdateGas<GasPolicy.VeryLowGasCost>(refgas);", "SWAP handler gas route");
        Require(swap, "publicstaticintStackInputs=>TOpCount.Count+1;", "SWAP handler stack arity");
        Require(swap, "stack.Swap<TTracingInst,OffFlag>(TOpCount.Count+1)", "SWAP checked primitive");
        Require(swap, "EvmInstructions.InstructionSwap<TGasPolicy,TOpCount,TTracingInst>", "SWAP traced primitive");
    }

    private static void ValidateDispatch(Dictionary<string, SourceFile> sources)
    {
        TypeDeclarationSyntax owner = FindOwner(sources[DispatchPath].Syntax, "VirtualMachine", 1);
        string dispatch = Canonical(sources[DispatchPath].Syntax);
        RequireOrdered(dispatch,
            "pc++;",
            "opCodeCount++;",
            "if(TOpcode.HasCheckedBody)",
            "if(!TOpcode.TryConsumeGas(refgas))",
            "stack.EnsureDepth(TOpcode.StackInputs)",
            "stack.Head>=EvmStack.MaxStackSize-TOpcode.StackGrowth",
            "TOpcode.Execute(refstack,refgas",
            "if(!TContinuable.IsActive)");
        string exit = Canonical(FindMethod(owner, "ExitCheckedOpcode", 0, 4));
        Require(exit, "state.OpCodeCount=opCodeCount;", "dispatch opcode-count exit");
        Require(exit, "state.FinalProgramCounter=pc;", "dispatch PC exit");
        string table = Canonical(FindNestedType(owner, "OpcodeTable", 0));
        Require(table, "NoTrace", "no-trace table");
        Require(table, "NoTraceCancelable", "cancelable table");
        Require(table, "Traced", "traced table");
        Require(table, "TracedCancelable", "traced-cancelable table");
        Require(table, "TTracingInst.IsActive", "tracing table selector");
        Require(table, "TCancelable.IsActive", "cancellation table selector");

        string handlers = Canonical(sources[OpcodeHandlersPath].Syntax);
        Require(handlers, "&ExecuteOpcode<TOpcode,TTracingInst,TCancelable,OnFlag>", "ordinary closed root");
        Require(handlers, "GenerateOpcodeHandlers<TTracingInst,TCancelable>(IReleaseSpecspec)", "handler generator closure");

        string standard = Canonical(sources[VirtualMachineStandardPath].Syntax);
        Require(standard, "_opcodeTablesBySpec.GetValue(Spec,static_=>newOpcodeTable())", "fork-keyed opcode cache");
        Require(standard, "ShouldRefreshOpcodes", "standard opcode refresh hook");
    }

    private static void ValidateRuntimeAndDi(Dictionary<string, SourceFile> sources)
    {
        string vm = Canonical(sources[VirtualMachinePath].Syntax);
        Require(vm, "EthereumVirtualMachine", "standard Ethereum VM");
        Require(vm, "VirtualMachine<EthereumGasPolicy>", "Ethereum gas-policy closure");
        string module = Canonical(sources[BlockProcessingModulePath].Syntax);
        Require(module, ".AddScoped<IVirtualMachine,EthereumVirtualMachine>()", "Ethereum VM DI registration");
    }

    private static void ValidateForkReachability(Dictionary<string, SourceFile> sources)
    {
        for (int index = 0; index < ForkLineage.Length; index++)
        {
            string forkName = ForkLineage[index];
            string path = $"{ForkDirectory}/{ForkFileName(index)}.cs";
            string source = Canonical(sources[path].Syntax);
            string parent = index == 0 ? "null" : $"{ForkLineage[index - 1]}.Instance";
            Require(source,
                $"class{forkName}():NamedReleaseSpec<{forkName}>({parent})",
                $"{forkName} parent closure");
        }

        string named = Canonical(sources[NamedReleaseSpecPath].Syntax);
        RequireOrdered(named,
            "Parent=parent;",
            "ReplayAncestors(this);",
            "ReplayAncestors(fork.Parent);",
            "fork.Apply(this);");
        Require(named, "publicstaticNamedReleaseSpecInstance{get;}=newTSelf();", "fork singleton closure");
        string amsterdam = Canonical(sources[$"{ForkDirectory}/25_Amsterdam.cs"].Syntax);
        Require(amsterdam, "spec.IsEip8037Enabled=true;", "Amsterdam EIP-8037 activation");
        Require(amsterdam, "spec.IsEip8038Enabled=true;", "Amsterdam EIP-8038 activation");
    }

    private static Dictionary<string, int> ReadGasConstants(CompilationUnitSyntax syntax)
    {
        TypeDeclarationSyntax owner = FindOwner(syntax, "GasCostOf", 0);
        Dictionary<string, int> expected = new(StringComparer.Ordinal) { ["Base"] = 2, ["VeryLow"] = 3 };
        Dictionary<string, int> result = new(StringComparer.Ordinal);
        foreach ((string name, int value) in expected)
        {
            FieldDeclarationSyntax field = FindField(owner, name);
            VariableDeclaratorSyntax variable = field.Declaration.Variables
                .Single(candidate => candidate.Identifier.ValueText == name);
            if (variable.Initializer?.Value is not LiteralExpressionSyntax literal)
                throw new ExtractionException($"GasCostOf.{name} has no literal initializer.");
            int actual = Convert.ToInt32(literal.Token.Value, CultureInfo.InvariantCulture);
            if (actual != value) throw new ExtractionException($"GasCostOf.{name} must remain {value}.");
            result.Add(name, actual);
        }
        return result;
    }

    private static Dictionary<string, int> ReadInstructionValues(CompilationUnitSyntax syntax)
    {
        EnumDeclarationSyntax instruction = (EnumDeclarationSyntax)FindTopLevelType(syntax, "Instruction", 0);
        Dictionary<string, int> result = new(StringComparer.Ordinal);
        foreach (EnumMemberDeclarationSyntax member in instruction.Members)
        {
            if (member.EqualsValue?.Value is not LiteralExpressionSyntax literal) continue;
            result[member.Identifier.ValueText] = Convert.ToInt32(literal.Token.Value, CultureInfo.InvariantCulture);
        }
        return result;
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

            SyntaxTree tree = CSharpSyntaxTree.ParseText(
                Encoding.UTF8.GetString(bytes),
                ParseOptions,
                relativePath);
            CompilationUnitSyntax syntax = tree.GetCompilationUnitRoot();
            Diagnostic? error = tree.GetDiagnostics().FirstOrDefault(static diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error);
            if (error is not null) throw new ExtractionException($"Roslyn rejected {relativePath}: {error}");
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

    private static List<AdmissionIdentity> AdmitReviewedSyntax(
        Dictionary<string, SourceFile> sources,
        IReadOnlyDictionary<string, string>? overrides)
    {
        Dictionary<string, ReviewedSource> reviewed = LoadReviewedSourceIndex();
        List<AdmissionIdentity> identities = [];
        foreach (AdmissionSpec specification in AdmissionSpecs)
        {
            if (!reviewed.TryGetValue(specification.SourcePath, out ReviewedSource? expected))
                throw new ExtractionException($"Missing reviewed source resource for {specification.SourcePath}.");
            string expectedSha = expected.Sha256;
            string? overrideValue = null;
            if (overrides is not null)
            {
                overrides.TryGetValue(specification.SourcePath, out overrideValue);
                overrideValue ??= overrides.TryGetValue(specification.ResourceStem, out string? stemValue)
                    ? stemValue
                    : null;
            }
            if (overrideValue is not null)
                expectedSha = overrideValue.Contains('|', StringComparison.Ordinal)
                    ? overrideValue.Split('|', StringSplitOptions.None)[1]
                    : overrideValue;

            SourceFile source = sources[specification.SourcePath];
            string sourceSha = Hash(source.Bytes);
            if (!string.Equals(sourceSha, expectedSha, StringComparison.Ordinal))
                throw new ExtractionException($"Unadmitted complete syntax in {specification.SourcePath}; source fingerprint changed.");
            string syntaxSha = Hash(Encoding.UTF8.GetBytes(Canonical(source.Syntax)));
            if (expected.RoslynSyntaxSha256 is not null &&
                !string.Equals(syntaxSha, expected.RoslynSyntaxSha256, StringComparison.Ordinal))
                throw new ExtractionException($"Unadmitted Roslyn syntax in {specification.SourcePath}; syntax fingerprint changed.");

            SyntaxNode actualContainer = specification.OwnerName is null
                ? source.Syntax
                : FindOwner(source.Syntax, specification.OwnerName, specification.OwnerGenericArity);
            foreach (MemberSelector selector in specification.Members)
            {
                _ = FindSelectedMember(actualContainer, selector);
                string template =
                    $"{specification.SourcePath}|{expectedSha}|{expected.RoslynSyntaxSha256 ?? "syntax-pinned-at-extraction"}";
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
        Assembly assembly = typeof(StackRearrangementOpcodeProfile).Assembly;
        string resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(AdmissionResourceSuffix, StringComparison.Ordinal))
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
            if (fields.Length is < 2 or > 3 || string.IsNullOrWhiteSpace(fields[0]) ||
                string.IsNullOrWhiteSpace(fields[1]))
                throw new ExtractionException($"Malformed source admission resource line {lineNumber}.");
            if (!result.TryAdd(fields[0], new(fields[0], fields[1], fields.Length == 3 && fields[2].Length > 0 ? fields[2] : null)))
                throw new ExtractionException($"Duplicate source admission resource entry {fields[0]}.");
        }
        return result;
    }

    private static void ValidateNoCompetingMembers(string root, Dictionary<string, SourceFile> sources)
    {
        HashSet<string> known = sources.Values
            .Select(static source => Path.GetFullPath(source.FullPath))
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
            string absoluteDirectory = Path.Combine(root, directory.Replace('/', Path.DirectorySeparatorChar));
            foreach (string file in Directory.EnumerateFiles(absoluteDirectory, "*.cs", SearchOption.AllDirectories))
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
                    foreach (TypeDeclarationSyntax owner in syntax.DescendantNodesAndSelf().OfType<TypeDeclarationSyntax>())
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

    private static MemberDeclarationSyntax FindSelectedMember(SyntaxNode container, MemberSelector selector)
    {
        MemberDeclarationSyntax[] matches = DirectMembers(container)
            .Where(member => Matches(member, selector))
            .ToArray();
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
            (selector.GenericArity < 0 ||
             (method.TypeParameterList?.Parameters.Count ?? 0) == selector.GenericArity) &&
            (selector.ParameterCount < 0 || method.ParameterList.Parameters.Count == selector.ParameterCount) &&
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
            .Where(type => type.Identifier.ValueText == name && GenericArity(type) == genericArity)
            .ToArray();
        if (matches.Length != 1)
            throw new ExtractionException($"Expected exactly one owner {name}/{genericArity}; found {matches.Length}.");
        return matches[0];
    }

    private static BaseTypeDeclarationSyntax FindTopLevelType(CompilationUnitSyntax syntax, string name, int genericArity)
    {
        BaseTypeDeclarationSyntax[] matches = DirectMembers(syntax).OfType<BaseTypeDeclarationSyntax>()
            .Where(type => type.Identifier.ValueText == name && GenericArity(type) == genericArity)
            .ToArray();
        if (matches.Length != 1)
            throw new ExtractionException($"Expected exactly one top-level type {name}/{genericArity}; found {matches.Length}.");
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

    private static void RequireOrdered(string source, params string[] fragments)
    {
        int position = -1;
        foreach (string fragment in fragments)
        {
            int next = source.IndexOf(fragment, position + 1, StringComparison.Ordinal);
            if (next < 0)
                throw new ExtractionException($"Rejected production order; expected '{fragment}' after position {position}.");
            position = next;
        }
    }

    private static void RequireNonEmpty(params string?[] values)
    {
        if (values.Any(static value => string.IsNullOrWhiteSpace(value)))
            throw new ExtractionException("Serialized IR contains null or empty claim-relevant fields.");
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

    private static MemberSelector MAny(string name) => new("method", name, -1, -1);

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
