// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.TransientStorageOpcodeExtractor;

internal static class TransientStorageOpcodeProfile
{
    internal const string InstructionPath = "src/Nethermind/Nethermind.Evm/Instruction.cs";
    internal const string HandlersPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs";
    internal const string DispatchPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs";
    internal const string StorageInstructionsPath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Storage.cs";
    internal const string GasCostPath = "src/Nethermind/Nethermind.Core/GasCostOf.cs";
    internal const string GasTagsPath = "src/Nethermind/Nethermind.Evm/GasPolicy/IGasCost.cs";
    internal const string GasPolicyInterfacePath = "src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs";
    internal const string EthereumGasPolicyPath = "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs";
    internal const string ReleaseExtensionsPath = "src/Nethermind/Nethermind.Core/Specs/IReleaseSpecExtensions.cs";
    internal const string ReleaseInterfacePath = "src/Nethermind/Nethermind.Core/Specs/IReleaseSpec.cs";
    internal const string ReleaseSpecPath = "src/Nethermind/Nethermind.Specs/ReleaseSpec.cs";
    internal const string NamedReleaseSpecPath = "src/Nethermind/Nethermind.Specs/Forks/NamedReleaseSpec.cs";
    internal const string VirtualMachinePath = "src/Nethermind/Nethermind.Evm/VirtualMachine.cs";
    internal const string StandardVirtualMachinePath = "src/Nethermind/Nethermind.Evm/VirtualMachine.std.cs";
    internal const string StandardDispatchFlagsPath = "src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs";
    internal const string TypeFlagsPath = "src/Nethermind/Nethermind.Core/TypeFlags.cs";
    internal const string StackPath = "src/Nethermind/Nethermind.Evm/EvmStack.cs";
    internal const string MainnetDiPath = "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs";
    internal const string WorldStateInterfacePath = "src/Nethermind/Nethermind.Evm/State/IWorldState.cs";
    internal const string StorageCellPath = "src/Nethermind/Nethermind.Core/StorageCell.cs";
    internal const string VmStatePath = "src/Nethermind/Nethermind.Evm/VmState.cs";
    internal const string ExecutionEnvironmentPath = "src/Nethermind/Nethermind.Evm/ExecutionEnvironment.cs";
    internal const string TransactionProcessorPath = "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs";
    internal const string BuildTargetsPath = "src/Nethermind/Directory.Build.targets";
    internal const string DefaultOutputRelativePath = "tools/Evm/Lean/TransientStorageOpcodeExtractor/Generated";
    internal const string DefaultLeanRelativePath = "tools/Evm/Lean/TransientStorageOpcodeExtractor/Generated/TransientStorageOpcodeKernel.lean";

    private const int SchemaVersion = 1;
    private const string ExtractorVersion = "1.0.0";
    internal const string IrFileName = "TransientStorageOpcodeKernel.ir.json";
    internal const string ManifestFileName = "TransientStorageOpcodeKernel.source-manifest.json";
    private const string Kernel = "Nethermind standard-mainnet Amsterdam TLOAD/TSTORE immediate-handler slice";
    private static readonly string[] ExpectedOpenExtractionObligations =
    [
        "Theorems cover the immediate TLOAD/TSTORE transition selected by standard-mainnet Amsterdam dispatch; C# language, Roslyn binding, CLR/JIT, unsafe function-pointer dispatch, and hardware correctness remain outside the theorem.",
        "UInt256 and Address representation, EvmStack unsafe storage and endian conversions, ReadOnlySpan lifetime, zero-value byte canonicalization, and PushBytes/PopUInt256/PopWord256 implementation equivalence remain adapter premises.",
        "IWorldState transient-provider lookup, journaling, nested-frame snapshot restore/merge, account destruction, transaction-boundary ResetTransient, and WorldState decorator behavior remain separate production-refinement obligations.",
        "Tracer callback behavior, callback exceptions, trace byte representation, cancellation, opcode-count accounting, and instruction trace side effects are outside the consensus-state refinement; the admitted handler preserves the fixed pre-write TSTORE trace ordering.",
    ];
    private static readonly string[] ExpectedSemanticBindings =
    [
        "TLOAD=0x5c and TSTORE=0x5d are unique byte-valued Instruction members.",
        "Amsterdam inherits Cancun's IsEip1153Enabled=true through every named-mainnet parent.",
        "The standard build registers EthereumVirtualMachine and closes VirtualMachine<EthereumGasPolicy>.",
        "All four tracing/cancellation opcode tables contribute the eight exact per-opcode TLOAD/TSTORE roots.",
        "Both wrappers delegate directly to the admitted EvmInstructions implementations.",
        "TLOAD charges 100, then pops key, reads the executing-account cell, and pushes the value.",
        "TSTORE checks static context before charging 100, pops key before value, snapshots the pre-write trace value, then writes.",
    ];

    private static readonly string[] ForkPaths =
    [
        .. Enumerable.Range(0, 22).Select(static index => index switch
        {
            0 => "src/Nethermind/Nethermind.Specs/Forks/00_Olympic.cs",
            1 => "src/Nethermind/Nethermind.Specs/Forks/01_Frontier.cs",
            2 => "src/Nethermind/Nethermind.Specs/Forks/02_Homestead.cs",
            3 => "src/Nethermind/Nethermind.Specs/Forks/03_Dao.cs",
            4 => "src/Nethermind/Nethermind.Specs/Forks/04_TangerineWhistle.cs",
            5 => "src/Nethermind/Nethermind.Specs/Forks/05_SpuriousDragon.cs",
            6 => "src/Nethermind/Nethermind.Specs/Forks/06_Byzantium.cs",
            7 => "src/Nethermind/Nethermind.Specs/Forks/07_Constantinople.cs",
            8 => "src/Nethermind/Nethermind.Specs/Forks/08_ConstantinopleFix.cs",
            9 => "src/Nethermind/Nethermind.Specs/Forks/09_Istanbul.cs",
            10 => "src/Nethermind/Nethermind.Specs/Forks/10_MuirGlacier.cs",
            11 => "src/Nethermind/Nethermind.Specs/Forks/11_Berlin.cs",
            12 => "src/Nethermind/Nethermind.Specs/Forks/12_London.cs",
            13 => "src/Nethermind/Nethermind.Specs/Forks/13_ArrowGlacier.cs",
            14 => "src/Nethermind/Nethermind.Specs/Forks/14_GrayGlacier.cs",
            15 => "src/Nethermind/Nethermind.Specs/Forks/15_Paris.cs",
            16 => "src/Nethermind/Nethermind.Specs/Forks/16_Shanghai.cs",
            17 => "src/Nethermind/Nethermind.Specs/Forks/17_Cancun.cs",
            18 => "src/Nethermind/Nethermind.Specs/Forks/18_Prague.cs",
            19 => "src/Nethermind/Nethermind.Specs/Forks/19_Osaka.cs",
            20 => "src/Nethermind/Nethermind.Specs/Forks/20_BPO1.cs",
            21 => "src/Nethermind/Nethermind.Specs/Forks/21_BPO2.cs",
            _ => throw new UnreachableException(),
        }),
        "src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs",
    ];

    private static readonly string[] SourceRelativePaths =
    [
        InstructionPath,
        HandlersPath,
        DispatchPath,
        StorageInstructionsPath,
        GasCostPath,
        GasTagsPath,
        GasPolicyInterfacePath,
        EthereumGasPolicyPath,
        ReleaseExtensionsPath,
        ReleaseInterfacePath,
        ReleaseSpecPath,
        NamedReleaseSpecPath,
        VirtualMachinePath,
        StandardVirtualMachinePath,
        StandardDispatchFlagsPath,
        TypeFlagsPath,
        StackPath,
        MainnetDiPath,
        WorldStateInterfacePath,
        StorageCellPath,
        VmStatePath,
        ExecutionEnvironmentPath,
        TransactionProcessorPath,
        BuildTargetsPath,
        .. ForkPaths,
    ];

    private static readonly Dictionary<string, string> ExpectedSourceFingerprints = new(StringComparer.Ordinal)
    {
        [InstructionPath] = "6895d06277ac4f9369d7bdc748976576bf384f6f3a5a348a2e4fd3767a46a030",
        [HandlersPath] = "5148dd594e40ebb976179b1048f233914f54acc8d033d358cf3e2e7594e112b6",
        [DispatchPath] = "4f36bb20057caec9c85bcd3621372563f4d47ba36a9cbf01be267af4e379bca1",
        [StorageInstructionsPath] = "0101effa0c71c4a602021f18de7075e047974b75b37b9d672364499db0bf390d",
        [GasCostPath] = "10f1f1a29e7bf10a8220422a6f7571af60df4f05ad4075268e3b26559a73c6f5",
        [GasTagsPath] = "16289cdc2f777a76fda3ad8ff4ce4275c30513aee04c9cf42d314cc6e1ec48c0",
        [GasPolicyInterfacePath] = "b4d26db87723243514f2f6b8a5e48d1176139105c3b012ad0f3956e59afc98a8",
        [EthereumGasPolicyPath] = "3c31ba40c24a78bf11243b38859168350f02934806124e913da0560bc1a4350f",
        [ReleaseExtensionsPath] = "5a22587aa81b5f0e4276e61bc1970ff89e4e4cecbed8c9b08dd338576c7d041a",
        [ReleaseInterfacePath] = "3f44b3da5614c94d59eae2e4a6266a9e698614f75a7b5a9894a4828b0021b18a",
        [ReleaseSpecPath] = "7a34da425c0ed0a7ba6776fd43262235170211f93e9563013ddc47e31651b7dc",
        [NamedReleaseSpecPath] = "ea9400ed44270fb18766a5dbd20d4dd7e7ee95b1bb7ab7ee6d5406cca03a46cc",
        [VirtualMachinePath] = "45edba3691e09185e749485785990662ddea1af849bc6e4564f92b68a5137a6b",
        [StandardVirtualMachinePath] = "357fbd35369424a379c8c29338f491627512d40a12199d7a3d9aac44c87ca92a",
        [StandardDispatchFlagsPath] = "99c58367e5b90fa5904484f6e2fbf10745a54f6cd7d07275810c27028ad0eb2b",
        [TypeFlagsPath] = "33d8404d3ec39fffbc9f665126255f9799a0ba433206a141f480f8144be95747",
        [StackPath] = "561520be4f0fe1d533ddea9cfea7ff48edb2745b1f3adf1b45c24e6c0bed02ad",
        [MainnetDiPath] = "fce65ce5bb523c56fc8aa6ee0e4d092c940a5a90b174c820ffe262eeb5241c60",
        [WorldStateInterfacePath] = "0680ebc7168472645d93df58d2b3240508c177361815c85cceb09cf960f189c7",
        [StorageCellPath] = "0221aaa2ea9b31a114e256de271c538ff8676844a75c8965160bc8bfcfc7bf55",
        [VmStatePath] = "7b7b6ddb753118b426d14976a3fe8e79f808320d6a09ea9cace9df19197f9f23",
        [ExecutionEnvironmentPath] = "c076225b688b1e04ed83cddc90a2ddbda69862333dc927945eb32d376e43e1db",
        [TransactionProcessorPath] = "0374c6f35a9a23361f37b41162ac281ca83b09846ab4295d5a592db6315c99cc",
        [BuildTargetsPath] = "0598cebaef1df41102a18a3f9ace810bed1e4e64b8471d055724b4a394dccec6",
        ["src/Nethermind/Nethermind.Specs/Forks/00_Olympic.cs"] = "8874f5ed3ae7bf5a0e0d1d127babd258393abcc3093b98abc296a1a347d61246",
        ["src/Nethermind/Nethermind.Specs/Forks/01_Frontier.cs"] = "28318120065dc43c084c998b4cdef83669fa7b036ebe850802f0f9ff860df075",
        ["src/Nethermind/Nethermind.Specs/Forks/02_Homestead.cs"] = "52dae1ff91fcc53fc95e4dc9adab9ce9686411620b88135a408728d56d6b0ad2",
        ["src/Nethermind/Nethermind.Specs/Forks/03_Dao.cs"] = "9354856fe91fde3d1394828e94bb700b6c765b9d42425221b6e1554898c34455",
        ["src/Nethermind/Nethermind.Specs/Forks/04_TangerineWhistle.cs"] = "82a6ea651cb884be41e1105d73cf726e1f095195322bf3293f663658da40939f",
        ["src/Nethermind/Nethermind.Specs/Forks/05_SpuriousDragon.cs"] = "7fb950f37739ad6435c4dc5bcd3bf9b9566d3a6137479675276cea5a07196af5",
        ["src/Nethermind/Nethermind.Specs/Forks/06_Byzantium.cs"] = "c47fcc9740483de8081c4591aa6b2994271602dacc0a0fb590e63e39f0288e73",
        ["src/Nethermind/Nethermind.Specs/Forks/07_Constantinople.cs"] = "a0ee60c7fb44400a18e1b5d6cc3fe324de557023c34f5c7427ecaf72da17cfbe",
        ["src/Nethermind/Nethermind.Specs/Forks/08_ConstantinopleFix.cs"] = "1a0336f8a06e64f40cdfbba3ecac79158ddf9f38d9694df346916c819819a6a5",
        ["src/Nethermind/Nethermind.Specs/Forks/09_Istanbul.cs"] = "911764bbfd46d09c055d8a5a0a699c62b65ac96a528f76c294044a4c84b84714",
        ["src/Nethermind/Nethermind.Specs/Forks/10_MuirGlacier.cs"] = "76508e7a64c3bf94a8da0145fedb4f277c1d06e75454a53d1c2d036fc9e9f5e8",
        ["src/Nethermind/Nethermind.Specs/Forks/11_Berlin.cs"] = "b02b45deb5dedddc9df19283c7491ffc3ab10ab0aaae313e44cdafca9ee547b0",
        ["src/Nethermind/Nethermind.Specs/Forks/12_London.cs"] = "06ea8bd017ccff59759026a801138bd794919e8493d49f8ef05385276662f8ef",
        ["src/Nethermind/Nethermind.Specs/Forks/13_ArrowGlacier.cs"] = "267c4b5529156e692ac1d1827f189b988f07d398bda986befef9561234c94491",
        ["src/Nethermind/Nethermind.Specs/Forks/14_GrayGlacier.cs"] = "a3904920bc25153c271057fe3b990b183c9cf93bba3a393c1f7561c750907a2e",
        ["src/Nethermind/Nethermind.Specs/Forks/15_Paris.cs"] = "90c0f08765e2b50a8fadc2fdfcbe43965b9352e0a17ac692676403eb95972abd",
        ["src/Nethermind/Nethermind.Specs/Forks/16_Shanghai.cs"] = "dcbfdacf3b6c7a8db0e8d5abc1ad3c388d407ae989395d52f1725721a5cc767e",
        ["src/Nethermind/Nethermind.Specs/Forks/17_Cancun.cs"] = "29b93ca43e919352495a0406242d7b49ce9423117aeec2e1bb94427fff20aa79",
        ["src/Nethermind/Nethermind.Specs/Forks/18_Prague.cs"] = "949248f5f22162433e7f4e17a7e5a35b4cf54416836738831edd3b1c0e5a6b8c",
        ["src/Nethermind/Nethermind.Specs/Forks/19_Osaka.cs"] = "24da07f0c85e3c5822c10f010c4b35a5ca21046ac6b21f5bb26397de2ef5cf77",
        ["src/Nethermind/Nethermind.Specs/Forks/20_BPO1.cs"] = "6fc6d2c0fc7159882c03ad44006da8aad1354d68d2d1fe750ef8a551b43bbad8",
        ["src/Nethermind/Nethermind.Specs/Forks/21_BPO2.cs"] = "4788ce812f73163686b2b419970d70bdae4de73523fe6ad426a618dd1f1d40d9",
        ["src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs"] = "4dfbf9079e9dce30423327ae4433f32e49388d403a8fedfcf253f94f79450c6a",
    };

    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default
        .WithLanguageVersion(LanguageVersion.CSharp14)
        .WithDocumentationMode(DocumentationMode.Parse)
        .WithKind(SourceCodeKind.Regular);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
    };

    internal static IReadOnlyList<string> SourcePaths => SourceRelativePaths;

    internal static ExtractionResult Extract(string repoRoot, string outputDirectory, string? leanOutputPath = null)
    {
        string canonicalRoot = Path.GetFullPath(repoRoot);
        Dictionary<string, SourceFile> sources = LoadSources(canonicalRoot);
        ValidateInstructionBytes(Get(sources, InstructionPath));
        ValidateGas(Get(sources, GasCostPath), Get(sources, GasTagsPath), Get(sources, GasPolicyInterfacePath), Get(sources, EthereumGasPolicyPath));
        ValidateActivationAndForkLineage(sources);
        ValidateStandardMainnetReachability(sources);
        ValidateHandlers(sources);
        ValidateWorldStateBoundary(sources);
        ValidatePinnedSourceFingerprints(sources);

        OpcodeDescriptor[] opcodes =
        [
            new(
                "tload",
                "TLOAD",
                0x5c,
                "TLoadOpcode<TTracingInst>",
                "EvmInstructions.InstructionTLoad<TGasPolicy,TTracingInst>",
                100,
                1,
                1,
                false,
                ["charge", "popKey", "readExecutingAccountCell", "pushValue"]),
            new(
                "tstore",
                "TSTORE",
                0x5d,
                "TStoreOpcode",
                "EvmInstructions.InstructionTStore<TGasPolicy>",
                100,
                2,
                0,
                true,
                ["staticCheck", "charge", "popKey", "popValue", "snapshotPreWriteValueWhenTracing", "write"]),
        ];
        DispatchSpecialization[] specializations =
        [
            Specialization("tload", "NoTrace", "OffFlag", "OffFlag", "TLoadOpcode<OffFlag>"),
            Specialization("tstore", "NoTrace", "OffFlag", "OffFlag", "TStoreOpcode"),
            Specialization("tload", "NoTraceCancelable", "OffFlag", "OnFlag", "TLoadOpcode<OffFlag>"),
            Specialization("tstore", "NoTraceCancelable", "OffFlag", "OnFlag", "TStoreOpcode"),
            Specialization("tload", "Traced", "OnFlag", "OffFlag", "TLoadOpcode<OnFlag>"),
            Specialization("tstore", "Traced", "OnFlag", "OffFlag", "TStoreOpcode"),
            Specialization("tload", "TracedCancelable", "OnFlag", "OnFlag", "TLoadOpcode<OnFlag>"),
            Specialization("tstore", "TracedCancelable", "OnFlag", "OnFlag", "TStoreOpcode"),
        ];
        IrDocument ir = new(
            SchemaVersion,
            ExtractorVersion,
            Kernel,
            new ReachabilityDescriptor(
                "IVirtualMachine",
                "EthereumVirtualMachine",
                "VirtualMachine<EthereumGasPolicy>",
                "EthereumGasPolicy",
                "Amsterdam",
                "Cancun.IsEip1153Enabled inherited through the complete named-mainnet ancestry",
                "Directory.Build.targets selects *.std.cs when EnableZkEvm != true",
                "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode"),
            opcodes,
            specializations,
            ExpectedOpenExtractionObligations);

        byte[] irBytes = Serialize(ir);
        IrDocument roundTripped = DeserializeIr(irBytes);
        byte[] leanBytes = LeanEmitter.Emit(roundTripped, Sha256(irBytes));

        string output = Path.GetFullPath(outputDirectory);
        string leanPath = Path.GetFullPath(leanOutputPath ?? Path.Combine(canonicalRoot, DefaultLeanRelativePath));
        EnsureWithin(leanOutputPath is null ? canonicalRoot : output, leanPath);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(Path.GetDirectoryName(leanPath)!);
        string irPath = Path.Combine(output, IrFileName);
        string manifestPath = Path.Combine(output, ManifestFileName);
        Write(irPath, irBytes);
        Write(leanPath, leanBytes);

        SourceIdentity[] sourceIdentities = sources.Values
            .OrderBy(static source => source.RelativePath, StringComparer.Ordinal)
            .Select(static source => new SourceIdentity(source.RelativePath, source.Sha256))
            .ToArray();
        SourceManifest manifest = new(
            SchemaVersion,
            ExtractorVersion,
            typeof(CSharpSyntaxTree).Assembly.GetName().Version?.ToString() ?? "unknown",
            LanguageVersion.CSharp14.ToString(),
            Kernel,
            sourceIdentities,
            BuildAdmissions(sourceIdentities),
            new ArtifactIdentity(Path.GetFileName(irPath), Sha256(irBytes)),
            new ArtifactIdentity(DefaultLeanRelativePath, Sha256(leanBytes)),
            CombinedHash(sourceIdentities),
            ExpectedSemanticBindings);
        byte[] manifestBytes = Serialize(manifest);
        SourceManifest roundTrippedManifest = DeserializeManifest(manifestBytes);
        ValidateManifest(roundTrippedManifest, manifest);
        Write(manifestPath, manifestBytes);
        return new ExtractionResult(irPath, manifestPath, leanPath, sources.Count, specializations.Length);
    }

    private static DispatchSpecialization Specialization(
        string opcode,
        string table,
        string tracing,
        string cancellation,
        string handler) =>
        new(opcode, table, tracing, cancellation,
            $"VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<{handler},{tracing},{cancellation},OnFlag>");

    internal static IrDocument DeserializeIr(byte[] bytes)
    {
        if (bytes is null) throw new ExtractionException("Serialized transient-storage opcode IR input is null.");
        try
        {
            IrDocument document = JsonSerializer.Deserialize<IrDocument>(bytes, JsonOptions)
                ?? throw new ExtractionException("Serialized transient-storage opcode IR is empty.");
            LeanEmitter.Validate(document);
            return document;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Serialized transient-storage opcode IR is malformed or contains unknown members: {exception.Message}");
        }
    }

    internal static SourceManifest DeserializeManifest(byte[] bytes)
    {
        if (bytes is null) throw new ExtractionException("Serialized transient-storage manifest input is null.");
        try
        {
            SourceManifest manifest = JsonSerializer.Deserialize<SourceManifest>(bytes, JsonOptions)
                ?? throw new ExtractionException("Serialized transient-storage manifest is empty.");
            ValidateManifest(manifest);
            return manifest;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Serialized transient-storage manifest is malformed or contains unknown members: {exception.Message}");
        }
    }

    internal static void ValidateManifest(SourceManifest manifest)
    {
        if (manifest is null || manifest.ExtractorVersion is null || manifest.CompilerVersion is null ||
            manifest.LanguageVersion is null || manifest.Kernel is null || manifest.Sources is null ||
            manifest.Admissions is null || manifest.Ir is null || manifest.Lean is null ||
            manifest.CombinedSha256 is null || manifest.SemanticBindings is null ||
            manifest.Sources.Any(static source => source is null || source.Path is null || source.Sha256 is null) ||
            manifest.Admissions.Any(static admission => admission is null || admission.Key is null || admission.SourceSha256 is null) ||
            manifest.Ir.Path is null || manifest.Ir.Sha256 is null || manifest.Lean.Path is null ||
            manifest.Lean.Sha256 is null || manifest.SemanticBindings.Any(static binding => binding is null))
            throw new ExtractionException("The serialized transient-storage manifest contains null collections, entries, or fields.");

        if (manifest.SchemaVersion != SchemaVersion || manifest.ExtractorVersion != ExtractorVersion ||
            string.IsNullOrWhiteSpace(manifest.CompilerVersion) || manifest.LanguageVersion != LanguageVersion.CSharp14.ToString() ||
            manifest.Kernel != Kernel || manifest.Ir.Path != IrFileName ||
            manifest.Lean.Path != DefaultLeanRelativePath || !IsSha256(manifest.Ir.Sha256) ||
            !IsSha256(manifest.Lean.Sha256) || !IsSha256(manifest.CombinedSha256) ||
            !manifest.SemanticBindings.SequenceEqual(ExpectedSemanticBindings, StringComparer.Ordinal))
            throw new ExtractionException("The serialized transient-storage manifest header, artifacts, or semantic bindings changed.");

        string[] expectedPaths = SourceRelativePaths.OrderBy(static path => path, StringComparer.Ordinal).ToArray();
        if (manifest.Sources.Length != expectedPaths.Length ||
            !manifest.Sources.Select(static source => source.Path).SequenceEqual(expectedPaths, StringComparer.Ordinal) ||
            manifest.Sources.Select(static source => source.Path).Distinct(StringComparer.Ordinal).Count() != manifest.Sources.Length)
            throw new ExtractionException("The serialized transient-storage source identities changed or collide.");
        foreach (SourceIdentity source in manifest.Sources)
            if (!ExpectedSourceFingerprints.TryGetValue(source.Path, out string? expected) || source.Sha256 != expected)
                throw new ExtractionException($"The serialized transient-storage source fingerprint changed for {source.Path}.");
        if (manifest.CombinedSha256 != CombinedHash(manifest.Sources))
            throw new ExtractionException("The serialized transient-storage combined source fingerprint changed.");

        ValidateAdmissionIdentities(manifest.Admissions);
        AdmissionIdentity[] expectedAdmissions = BuildAdmissions(manifest.Sources);
        if (!manifest.Admissions.SequenceEqual(expectedAdmissions))
            throw new ExtractionException("The serialized transient-storage admission identities changed.");
    }

    internal static void ValidateManifest(SourceManifest actual, SourceManifest expected)
    {
        ValidateManifest(actual);
        ValidateManifest(expected);
        if (!Serialize(actual).AsSpan().SequenceEqual(Serialize(expected)))
            throw new ExtractionException("The serialized transient-storage manifest does not exactly match extracted lineage.");
    }

    internal static void ValidateAdmissionIdentities(IReadOnlyList<AdmissionIdentity> admissions)
    {
        if (admissions is null || admissions.Any(static admission => admission is null ||
                string.IsNullOrWhiteSpace(admission.Key) || !IsSha256(admission.SourceSha256)))
            throw new ExtractionException("Transient-storage admission identities contain null, empty, or invalid values.");
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (AdmissionIdentity admission in admissions)
        {
            if (!admission.Key.EndsWith(":compilation-unit/0:complete-source", StringComparison.Ordinal))
                throw new ExtractionException($"Transient-storage admission identity is not path/owner-qualified: {admission.Key}.");
            if (!keys.Add(admission.Key))
                throw new ExtractionException($"Duplicate owner-qualified transient-storage admission identity {admission.Key}.");
        }
    }

    private static AdmissionIdentity[] BuildAdmissions(IEnumerable<SourceIdentity> sources) => sources
        .Select(static source => new AdmissionIdentity(
            $"{source.Path}:compilation-unit/0:complete-source", source.Sha256))
        .ToArray();

    private static bool IsSha256(string value) => value.Length == 64 && value.All(static character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateInstructionBytes(SourceFile source)
    {
        EnumDeclarationSyntax instruction = Single(source.Root.DescendantNodes().OfType<EnumDeclarationSyntax>(),
            static declaration => declaration.Identifier.ValueText == "Instruction", "Instruction enum");
        if (Compact(instruction.BaseList) != ":byte")
            throw new ExtractionException("Instruction must remain byte-backed.");

        Dictionary<string, int> values = new(StringComparer.Ordinal);
        Dictionary<int, string> owners = [];
        foreach (EnumMemberDeclarationSyntax member in instruction.Members)
        {
            if (member.EqualsValue?.Value is not LiteralExpressionSyntax literal || literal.Token.Value is not IConvertible convertible)
                throw new ExtractionException($"Instruction.{member.Identifier.ValueText} must have an explicit literal byte.");
            int value = convertible.ToInt32(CultureInfo.InvariantCulture);
            if (value is < byte.MinValue or > byte.MaxValue)
                throw new ExtractionException($"Instruction.{member.Identifier.ValueText} byte {value} is outside the EVM byte range.");
            if (!owners.TryAdd(value, member.Identifier.ValueText))
                throw new ExtractionException($"Instruction.{member.Identifier.ValueText} duplicates opcode byte {value} owned by {owners[value]}.");
            values.Add(member.Identifier.ValueText, value);
        }

        RequireValue(values, "TLOAD", 0x5c);
        RequireValue(values, "TSTORE", 0x5d);
    }

    private static void ValidateGas(SourceFile costs, SourceFile tags, SourceFile policyInterface, SourceFile ethereumPolicy)
    {
        FieldDeclarationSyntax warm = Field(costs, "WarmStateRead");
        FieldDeclarationSyntax load = Field(costs, "TLoad");
        FieldDeclarationSyntax store = Field(costs, "TStore");
        if (Compact(warm.Declaration.Variables.Single().Initializer?.Value) != "100")
            throw new ExtractionException("GasCostOf.WarmStateRead must remain 100.");
        if (Compact(load.Declaration.Variables.Single().Initializer?.Value) != "WarmStateRead" ||
            Compact(store.Declaration.Variables.Single().Initializer?.Value) != "WarmStateRead")
            throw new ExtractionException("TLOAD and TSTORE gas must remain aliases of WarmStateRead.");

        RequireCompactContains(tags, "publicstaticulongGasCost=>GasCostOf.TLoad;", "TLoadGasCost must delegate to GasCostOf.TLoad");
        RequireCompactContains(tags, "publicstaticulongGasCost=>GasCostOf.TStore;", "TStoreGasCost must delegate to GasCostOf.TStore");
        RequireCompactContains(policyInterface, "TSelf.UpdateGas(refgas,TCost.GasCost)", "generic gas tags must flow into the active gas policy");

        MethodDeclarationSyntax updateGas = Single(
            ethereumPolicy.Root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "UpdateGas" && method.ParameterList.Parameters.Count == 2 && method.TypeParameterList is null,
            "EthereumGasPolicy.UpdateGas(ref gas, gasCost)");
        RequireOrdered(updateGas, "GetRemainingGas(ingas)<gasCost", "gas.Value=0", "returnfalse", "ConsumeRaw(refgas,gasCost)", "returntrue");
    }

    private static void ValidateActivationAndForkLineage(Dictionary<string, SourceFile> sources)
    {
        RequireCompactContains(Get(sources, ReleaseInterfacePath), "boolIsEip1153Enabled{get;}", "IReleaseSpec must expose IsEip1153Enabled");
        RequireCompactContains(Get(sources, ReleaseSpecPath), "publicboolIsEip1153Enabled{get;set;}", "ReleaseSpec must store IsEip1153Enabled");
        RequireCompactContains(Get(sources, ReleaseExtensionsPath), "publicboolTransientStorageEnabled=>spec.IsEip1153Enabled;", "TransientStorageEnabled must map to IsEip1153Enabled");
        RequireCompactContains(Get(sources, NamedReleaseSpecPath), "ReplayAncestors(fork.Parent);fork.Apply(this);", "NamedReleaseSpec must replay the full ancestor chain root-first");

        string[] names =
        [
            "Olympic", "Frontier", "Homestead", "Dao", "TangerineWhistle", "SpuriousDragon",
            "Byzantium", "Constantinople", "ConstantinopleFix", "Istanbul", "MuirGlacier", "Berlin",
            "London", "ArrowGlacier", "GrayGlacier", "Paris", "Shanghai", "Cancun", "Prague",
            "Osaka", "BPO1", "BPO2", "Amsterdam",
        ];
        string? parent = null;
        for (int index = 0; index < ForkPaths.Length; index++)
        {
            SourceFile fork = Get(sources, ForkPaths[index]);
            ClassDeclarationSyntax declaration = Single(
                fork.Root.DescendantNodes().OfType<ClassDeclarationSyntax>(),
                candidate => candidate.Identifier.ValueText == names[index],
                $"{names[index]} fork class");
            string expectedBase = parent is null
                ? $"NamedReleaseSpec<{names[index]}>(null)"
                : $"NamedReleaseSpec<{names[index]}>({parent}.Instance)";
            if (!Compact(declaration.BaseList).Contains(expectedBase, StringComparison.Ordinal))
                throw new ExtractionException($"{names[index]} must inherit {parent ?? "the null root"} in the complete Amsterdam ancestry.");
            parent = names[index];
        }

        RequireCompactContains(Get(sources, ForkPaths[17]), "spec.IsEip1153Enabled=true;", "Cancun must activate EIP-1153");
    }

    private static void ValidateStandardMainnetReachability(Dictionary<string, SourceFile> sources)
    {
        XDocument targets;
        try
        {
            targets = XDocument.Parse(Encoding.UTF8.GetString(Get(sources, BuildTargetsPath).Bytes), LoadOptions.PreserveWhitespace);
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or InvalidOperationException)
        {
            throw new ExtractionException($"Cannot parse the standard-build source-selection target: {exception.Message}");
        }

        XElement[] standardGroups = targets.Root?.Elements("ItemGroup")
            .Where(static group => (string?)group.Attribute("Condition") == "'$(EnableZkEvm)' != 'true'")
            .ToArray() ?? [];
        if (standardGroups.Length != 1 ||
            standardGroups[0].Elements("Compile").Count(static element => (string?)element.Attribute("Remove") == "**/*.zkevm.cs") != 1)
            throw new ExtractionException("Directory.Build.targets must select the standard *.std.cs implementation when EnableZkEvm is not true.");

        RequireCompactContains(Get(sources, VirtualMachinePath), "publicsealedclassEthereumVirtualMachine", "EthereumVirtualMachine must remain the mainnet concrete VM");
        RequireCompactContains(Get(sources, VirtualMachinePath), ":VirtualMachine<EthereumGasPolicy>", "EthereumVirtualMachine must close VirtualMachine over EthereumGasPolicy");
        RequireCompactContains(Get(sources, MainnetDiPath), ".AddScoped<IVirtualMachine,EthereumVirtualMachine>()", "mainnet block processing must register EthereumVirtualMachine");
        RequireCompactContains(Get(sources, StandardVirtualMachinePath), "privateOpcodeTableGetOpcodeTable()=>_opcodeTablesBySpec.GetValue(Spec,static_=>newOpcodeTable());", "standard VirtualMachine must supply the opcode table");
        RequireCompactContains(Get(sources, StandardDispatchFlagsPath), "publicconstboolConstTracing=true;", "standard dispatch must retain tracing specializations");

        SourceFile dispatch = Get(sources, DispatchPath);
        RequireCompactContains(dispatch, "GetOpcodeTable().GetHandlers<TTracingInst,TCancelable>(Spec)", "GetOpcodeHandlers must read the standard opcode table");
        RequireCompactContains(dispatch, "GetHandlers<TTracingInst,TCancelable>(IReleaseSpecspec)", "OpcodeTable must select a tracing/cancellation table");
        RequireCompactContains(dispatch,
            "refTTracingInst.IsActive?ref(TCancelable.IsActive?refTracedCancelable:refTraced):ref(TCancelable.IsActive?refNoTraceCancelable:refNoTrace)",
            "the four opcode tables must remain bound to their exact tracing and cancellation flags");
        RequireCompactContains(dispatch, "returntable??=GenerateOpcodeHandlers<TTracingInst,TCancelable>(spec);", "all opcode tables must originate in GenerateOpcodeHandlers");

        MethodDeclarationSyntax execute = Single(
            dispatch.Root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "ExecuteOpcode" && method.TypeParameterList?.Parameters.Count == 4,
            "VirtualMachine.ExecuteOpcode");
        RequireOrdered(execute, "pc++", "opCodeCount++", "TOpcode.Execute(refstack,refgas,state.Vm,refpc)");
    }

    private static void ValidateHandlers(Dictionary<string, SourceFile> sources)
    {
        SourceFile handlers = Get(sources, HandlersPath);
        MethodDeclarationSyntax opcodeHandler = Single(
            handlers.Root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "OpcodeHandler" && method.TypeParameterList?.Parameters.Count == 3,
            "OpcodeHandler continuation root");
        if (Compact(opcodeHandler.ExpressionBody?.Expression) != "&ExecuteOpcode<TOpcode,TTracingInst,TCancelable,OnFlag>")
            throw new ExtractionException("OpcodeHandler must bind the exact continuable ExecuteOpcode root.");
        MethodDeclarationSyntax generate = Single(
            handlers.Root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "GenerateOpcodeHandlers" && method.TypeParameterList?.Parameters.Count == 2,
            "GenerateOpcodeHandlers");
        ValidateDispatchAssignment(generate, "TLOAD", "OpcodeHandler<TLoadOpcode<TTracingInst>,TTracingInst,TCancelable>()");
        ValidateDispatchAssignment(generate, "TSTORE", "OpcodeHandler<TStoreOpcode,TTracingInst,TCancelable>()");
        ValidateWrapper(handlers, "TLoadOpcode", "EvmInstructions.InstructionTLoad<TGasPolicy,TTracingInst>(refstack,refgas,vm)");
        ValidateWrapper(handlers, "TStoreOpcode", "EvmInstructions.InstructionTStore(refstack,refgas,vm)");

        SourceFile storage = Get(sources, StorageInstructionsPath);
        MethodDeclarationSyntax tload = Method(storage, "InstructionTLoad", 3);
        RequireOrdered(
            tload,
            "TGasPolicy.UpdateGas<TLoadGasCost>(refgas)",
            "stack.PopUInt256(outUInt256result)",
            "new(vm.VmState.Env.ExecutingAccount,inresult)",
            "vm.WorldState.GetTransientState(instorageCell)",
            "stack.PushBytes<TTracingInst>(value)",
            "returnpushResult");
        RejectAnyBefore(tload, "TGasPolicy.UpdateGas<TLoadGasCost>(refgas)", "return", "TLOAD has an early return before its gas charge");

        MethodDeclarationSyntax tstore = Method(storage, "InstructionTStore", 3);
        RequireOrdered(
            tstore,
            "if(vmState.IsStatic)gotoStaticCallViolation",
            "TGasPolicy.UpdateGas<TStoreGasCost>(refgas)",
            "stack.PopUInt256(outUInt256result)",
            "new(vmState.Env.ExecutingAccount,inresult)",
            "stack.PopWord256(outSpan<byte>bytes)",
            "boolisTracingOpLevelStorage=vm.IsTracingOpLevelStorage",
            "vm.WorldState.GetTransientState(instorageCell)",
            ".ToArray()",
            "vm.WorldState.SetTransientState(instorageCell",
            "vm.TxTracer.SetOperationTransientStorage(storageCell.Address,result,bytes,currentValue)",
            "returnEvmExceptionType.None");
        RejectAnyBefore(tstore, "if(vmState.IsStatic)gotoStaticCallViolation", "UpdateGas", "TSTORE must check static context before every charge");
    }

    private static void ValidateWorldStateBoundary(Dictionary<string, SourceFile> sources)
    {
        RequireCompactContains(Get(sources, WorldStateInterfacePath), "ReadOnlySpan<byte>GetTransientState(inStorageCellstorageCell);", "IWorldState must expose transient reads");
        RequireCompactContains(Get(sources, WorldStateInterfacePath), "voidSetTransientState(inStorageCellstorageCell,byte[]newValue);", "IWorldState must expose transient writes");
        RequireCompactContains(Get(sources, WorldStateInterfacePath), "voidResetTransient();", "IWorldState must expose transaction reset");
        RequireCompactContains(Get(sources, StorageCellPath), "publicreadonlystructStorageCell", "StorageCell must remain a value cell");
        RequireCompactContains(Get(sources, VmStatePath), "publicboolIsStatic{get;privateset;}", "VmState must carry static context");
        RequireCompactContains(Get(sources, ExecutionEnvironmentPath), "publicAddressExecutingAccount{get;privateset;}", "ExecutionEnvironment must carry the executing account");
        RequireCompactContains(Get(sources, TransactionProcessorPath), "WorldState.ResetTransient();", "transaction processing must retain an explicit transient reset boundary");
    }

    private static void ValidateDispatchAssignment(MethodDeclarationSyntax generate, string instruction, string expectedRight)
    {
        string left = $"lookup[(int)Instruction.{instruction}]";
        AssignmentExpressionSyntax[] assignments = generate.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(assignment => Compact(assignment.Left) == left)
            .ToArray();
        if (assignments.Length != 1)
            throw new ExtractionException($"Instruction.{instruction} must have exactly one live dispatch assignment; found {assignments.Length}.");
        if (Compact(assignments[0].Right) != expectedRight)
            throw new ExtractionException($"Instruction.{instruction} dispatch changed from {expectedRight}.");

        IfStatementSyntax[] gates = assignments[0].Ancestors()
            .TakeWhile(node => node != generate)
            .OfType<IfStatementSyntax>()
            .ToArray();
        if (gates.Length != 1 || Compact(gates[0].Condition) != "spec.TransientStorageEnabled")
            throw new ExtractionException($"Instruction.{instruction} must be guarded only by spec.TransientStorageEnabled.");
    }

    private static void ValidateWrapper(SourceFile handlers, string wrapper, string expectedCall)
    {
        StructDeclarationSyntax declaration = Single(
            handlers.Root.DescendantNodes().OfType<StructDeclarationSyntax>(),
            candidate => candidate.Identifier.ValueText == wrapper,
            wrapper);
        MethodDeclarationSyntax execute = Single(
            declaration.Members.OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "Execute",
            $"{wrapper}.Execute");
        if (!Compact(execute).Contains($"=>{expectedCall};", StringComparison.Ordinal))
            throw new ExtractionException($"{wrapper} must directly delegate to {expectedCall}.");
    }

    private static void ValidatePinnedSourceFingerprints(Dictionary<string, SourceFile> sources)
    {
        if (ExpectedSourceFingerprints.Count != SourceRelativePaths.Length)
            throw new ExtractionException($"The reviewed source fingerprint closure is incomplete: expected {SourceRelativePaths.Length}, found {ExpectedSourceFingerprints.Count}.");
        foreach (string path in SourceRelativePaths)
        {
            if (!ExpectedSourceFingerprints.TryGetValue(path, out string? expected))
                throw new ExtractionException($"The reviewed source fingerprint closure omits {path}.");
            string actual = Get(sources, path).Sha256;
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new ExtractionException($"Complete-source admission fingerprint changed for {path}: expected {expected}, actual {actual}.");
        }
    }

    private static Dictionary<string, SourceFile> LoadSources(string root)
    {
        Dictionary<string, SourceFile> sources = new(StringComparer.Ordinal);
        foreach (string relativePath in SourceRelativePaths)
        {
            string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
            EnsureWithin(root, fullPath);
            if (!File.Exists(fullPath))
                throw new ExtractionException($"Required production source is missing: {relativePath}.");
            byte[] bytes = File.ReadAllBytes(fullPath);
            CompilationUnitSyntax? syntax = null;
            if (relativePath.EndsWith(".cs", StringComparison.Ordinal))
            {
                SyntaxTree tree = CSharpSyntaxTree.ParseText(Encoding.UTF8.GetString(bytes), ParseOptions, relativePath);
                Diagnostic[] errors = tree.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
                if (errors.Length != 0)
                    throw new ExtractionException($"Roslyn rejected {relativePath}: {errors[0]}.");
                syntax = (CompilationUnitSyntax)tree.GetRoot();
            }
            if (!sources.TryAdd(relativePath, new SourceFile(relativePath, bytes, Sha256(bytes), syntax)))
                throw new ExtractionException($"Duplicate source path in extraction profile: {relativePath}.");
        }
        return sources;
    }

    private static FieldDeclarationSyntax Field(SourceFile source, string name) => Single(
        source.Root.DescendantNodes().OfType<FieldDeclarationSyntax>(),
        declaration => declaration.Declaration.Variables.Count == 1 && declaration.Declaration.Variables[0].Identifier.ValueText == name,
        $"field {name}");

    private static MethodDeclarationSyntax Method(SourceFile source, string name, int parameters) => Single(
        source.Root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
        method => method.Identifier.ValueText == name && method.ParameterList.Parameters.Count == parameters,
        $"method {name}/{parameters}");

    private static T Single<T>(IEnumerable<T> values, Func<T, bool> predicate, string description)
    {
        T[] matches = values.Where(predicate).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new ExtractionException($"Expected exactly one {description}; found {matches.Length}.");
    }

    private static void RequireValue(IReadOnlyDictionary<string, int> values, string name, int expected)
    {
        if (!values.TryGetValue(name, out int actual) || actual != expected)
            throw new ExtractionException($"Instruction.{name} must remain 0x{expected:x2}; found {(values.ContainsKey(name) ? $"0x{actual:x}" : "missing")}.");
    }

    private static void RequireCompactContains(SourceFile source, string expected, string description)
    {
        if (!Compact(source.Root).Contains(expected, StringComparison.Ordinal))
            throw new ExtractionException($"Production binding changed: {description}.");
    }

    private static void RequireOrdered(SyntaxNode node, params string[] fragments)
    {
        string text = Compact(node);
        int position = -1;
        foreach (string fragment in fragments)
        {
            int next = text.IndexOf(fragment, position + 1, StringComparison.Ordinal);
            if (next < 0)
                throw new ExtractionException($"Ordered production fragment '{fragment}' is missing or moved.");
            position = next;
        }
    }

    private static void RejectAnyBefore(SyntaxNode node, string anchor, string rejected, string message)
    {
        string text = Compact(node);
        int anchorPosition = text.IndexOf(anchor, StringComparison.Ordinal);
        int rejectedPosition = text.IndexOf(rejected, StringComparison.Ordinal);
        if (anchorPosition < 0 || rejectedPosition >= 0 && rejectedPosition < anchorPosition)
            throw new ExtractionException(message);
    }

    private static string Compact(SyntaxNode? node) => node is null
        ? string.Empty
        : string.Concat(node.DescendantTokens().Select(static token => token.Text));

    private static SourceFile Get(IReadOnlyDictionary<string, SourceFile> sources, string path) =>
        sources.TryGetValue(path, out SourceFile? source)
            ? source
            : throw new ExtractionException($"The extraction source set omits {path}.");

    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

    private static string CombinedHash(IEnumerable<SourceIdentity> sources)
    {
        StringBuilder text = new();
        foreach (SourceIdentity source in sources)
            text.Append(source.Path).Append('\n').Append(source.Sha256).Append('\n');
        return Sha256(Encoding.UTF8.GetBytes(text.ToString()));
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void Write(string path, byte[] bytes)
    {
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
    }

    private static void EnsureWithin(string root, string path)
    {
        string canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
            throw new ExtractionException($"Refusing to access path outside extraction root: {path}.");
    }

    private sealed record SourceFile(string RelativePath, byte[] Bytes, string Sha256, CompilationUnitSyntax? Syntax)
    {
        public CompilationUnitSyntax Root => Syntax ?? throw new ExtractionException($"{RelativePath} is not a C# source file.");
    }
}
