// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.PushOpcodeExtractor;

internal static class PushOpcodeProfile
{
    internal const int SchemaVersion = 1;
    internal const string ExtractorVersion = "1.0.0";
    internal const string Kernel = "Amsterdam standard-mainnet PUSH0 and PUSH1-PUSH32 dispatch and operational semantics";
    internal const string DefaultOutputRelativePath = "tools/Evm/Lean/PushOpcodeExtractor/Generated";
    internal const string DefaultLeanRelativePath = "tools/Evm/Lean/PushOpcodeExtractor/Generated/PushOpcodeKernel.lean";
    internal const string IrFileName = "PushOpcodeKernel.ir.json";
    internal const string ManifestFileName = "PushOpcodeKernel.source-manifest.json";

    internal const string InstructionPath = "src/Nethermind/Nethermind.Evm/Instruction.cs";
    internal const string HandlersPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs";
    internal const string DispatchPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs";
    internal const string StackInstructionsPath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Stack.cs";
    internal const string StackPath = "src/Nethermind/Nethermind.Evm/EvmStack.cs";
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
    internal const string MainnetDiPath = "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs";
    internal const string BuildTargetsPath = "src/Nethermind/Directory.Build.targets";

    private static readonly string[] ForkPaths =
    [
        "src/Nethermind/Nethermind.Specs/Forks/00_Olympic.cs",
        "src/Nethermind/Nethermind.Specs/Forks/01_Frontier.cs",
        "src/Nethermind/Nethermind.Specs/Forks/02_Homestead.cs",
        "src/Nethermind/Nethermind.Specs/Forks/03_Dao.cs",
        "src/Nethermind/Nethermind.Specs/Forks/04_TangerineWhistle.cs",
        "src/Nethermind/Nethermind.Specs/Forks/05_SpuriousDragon.cs",
        "src/Nethermind/Nethermind.Specs/Forks/06_Byzantium.cs",
        "src/Nethermind/Nethermind.Specs/Forks/07_Constantinople.cs",
        "src/Nethermind/Nethermind.Specs/Forks/08_ConstantinopleFix.cs",
        "src/Nethermind/Nethermind.Specs/Forks/09_Istanbul.cs",
        "src/Nethermind/Nethermind.Specs/Forks/10_MuirGlacier.cs",
        "src/Nethermind/Nethermind.Specs/Forks/11_Berlin.cs",
        "src/Nethermind/Nethermind.Specs/Forks/12_London.cs",
        "src/Nethermind/Nethermind.Specs/Forks/13_ArrowGlacier.cs",
        "src/Nethermind/Nethermind.Specs/Forks/14_GrayGlacier.cs",
        "src/Nethermind/Nethermind.Specs/Forks/15_Paris.cs",
        "src/Nethermind/Nethermind.Specs/Forks/16_Shanghai.cs",
        "src/Nethermind/Nethermind.Specs/Forks/17_Cancun.cs",
        "src/Nethermind/Nethermind.Specs/Forks/18_Prague.cs",
        "src/Nethermind/Nethermind.Specs/Forks/19_Osaka.cs",
        "src/Nethermind/Nethermind.Specs/Forks/20_BPO1.cs",
        "src/Nethermind/Nethermind.Specs/Forks/21_BPO2.cs",
        "src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs",
    ];

    private static readonly string[] ForkNames =
    [
        "Olympic", "Frontier", "Homestead", "Dao", "TangerineWhistle", "SpuriousDragon",
        "Byzantium", "Constantinople", "ConstantinopleFix", "Istanbul", "MuirGlacier", "Berlin",
        "London", "ArrowGlacier", "GrayGlacier", "Paris", "Shanghai", "Cancun", "Prague",
        "Osaka", "BPO1", "BPO2", "Amsterdam",
    ];

    private static readonly string[] SourceRelativePaths =
    [
        InstructionPath, HandlersPath, DispatchPath, StackInstructionsPath, StackPath, GasCostPath,
        GasTagsPath, GasPolicyInterfacePath, EthereumGasPolicyPath, ReleaseExtensionsPath,
        ReleaseInterfacePath, ReleaseSpecPath, NamedReleaseSpecPath, VirtualMachinePath,
        StandardVirtualMachinePath, StandardDispatchFlagsPath, TypeFlagsPath, MainnetDiPath,
        BuildTargetsPath, .. ForkPaths,
    ];

    private static readonly IReadOnlyDictionary<string, string> ExpectedSourceFingerprints =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [InstructionPath] = "6895d06277ac4f9369d7bdc748976576bf384f6f3a5a348a2e4fd3767a46a030",
            [HandlersPath] = "5148dd594e40ebb976179b1048f233914f54acc8d033d358cf3e2e7594e112b6",
            [DispatchPath] = "4f36bb20057caec9c85bcd3621372563f4d47ba36a9cbf01be267af4e379bca1",
            [StackInstructionsPath] = "e44274b07b1a130045664718fad215e2a3dfd247f06421863573196d4c58f531",
            [StackPath] = "561520be4f0fe1d533ddea9cfea7ff48edb2745b1f3adf1b45c24e6c0bed02ad",
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
            [MainnetDiPath] = "fce65ce5bb523c56fc8aa6ee0e4d092c940a5a90b174c820ffe262eeb5241c60",
            [BuildTargetsPath] = "0598cebaef1df41102a18a3f9ace810bed1e4e64b8471d055724b4a394dccec6",
            [ForkPaths[0]] = "8874f5ed3ae7bf5a0e0d1d127babd258393abcc3093b98abc296a1a347d61246",
            [ForkPaths[1]] = "28318120065dc43c084c998b4cdef83669fa7b036ebe850802f0f9ff860df075",
            [ForkPaths[2]] = "52dae1ff91fcc53fc95e4dc9adab9ce9686411620b88135a408728d56d6b0ad2",
            [ForkPaths[3]] = "9354856fe91fde3d1394828e94bb700b6c765b9d42425221b6e1554898c34455",
            [ForkPaths[4]] = "82a6ea651cb884be41e1105d73cf726e1f095195322bf3293f663658da40939f",
            [ForkPaths[5]] = "7fb950f37739ad6435c4dc5bcd3bf9b9566d3a6137479675276cea5a07196af5",
            [ForkPaths[6]] = "c47fcc9740483de8081c4591aa6b2994271602dacc0a0fb590e63e39f0288e73",
            [ForkPaths[7]] = "a0ee60c7fb44400a18e1b5d6cc3fe324de557023c34f5c7427ecaf72da17cfbe",
            [ForkPaths[8]] = "1a0336f8a06e64f40cdfbba3ecac79158ddf9f38d9694df346916c819819a6a5",
            [ForkPaths[9]] = "911764bbfd46d09c055d8a5a0a699c62b65ac96a528f76c294044a4c84b84714",
            [ForkPaths[10]] = "76508e7a64c3bf94a8da0145fedb4f277c1d06e75454a53d1c2d036fc9e9f5e8",
            [ForkPaths[11]] = "b02b45deb5dedddc9df19283c7491ffc3ab10ab0aaae313e44cdafca9ee547b0",
            [ForkPaths[12]] = "06ea8bd017ccff59759026a801138bd794919e8493d49f8ef05385276662f8ef",
            [ForkPaths[13]] = "267c4b5529156e692ac1d1827f189b988f07d398bda986befef9561234c94491",
            [ForkPaths[14]] = "a3904920bc25153c271057fe3b990b183c9cf93bba3a393c1f7561c750907a2e",
            [ForkPaths[15]] = "90c0f08765e2b50a8fadc2fdfcbe43965b9352e0a17ac692676403eb95972abd",
            [ForkPaths[16]] = "dcbfdacf3b6c7a8db0e8d5abc1ad3c388d407ae989395d52f1725721a5cc767e",
            [ForkPaths[17]] = "29b93ca43e919352495a0406242d7b49ce9423117aeec2e1bb94427fff20aa79",
            [ForkPaths[18]] = "949248f5f22162433e7f4e17a7e5a35b4cf54416836738831edd3b1c0e5a6b8c",
            [ForkPaths[19]] = "24da07f0c85e3c5822c10f010c4b35a5ca21046ac6b21f5bb26397de2ef5cf77",
            [ForkPaths[20]] = "6fc6d2c0fc7159882c03ad44006da8aad1354d68d2d1fe750ef8a551b43bbad8",
            [ForkPaths[21]] = "4788ce812f73163686b2b419970d70bdae4de73523fe6ad426a618dd1f1d40d9",
            [ForkPaths[22]] = "4dfbf9079e9dce30423327ae4433f32e49388d403a8fedfcf253f94f79450c6a",
        };

    private static readonly string[] ExpectedOpenExtractionObligations =
    [
        "Unsafe code-buffer reads and the production sentinel allocation satisfy the admitted bounds assumptions",
        "EthereumGasPolicy fixed-width representation and CLR arithmetic implement the modeled Nat gas transitions",
        "EvmStack byte-lane layout and optimized PushN writers implement big-endian UInt256 values",
        "JumpDestination and PrefetchCodeAtDestination implement the supplied valid-destination relation without side effects beyond prefetch",
        "Tracing callbacks and cancellation boundaries preserve the modeled opcode state projection",
        "Tail-call function pointers, JIT specialization, and CLR execution implement the admitted standard dispatch roots",
    ];

    private static readonly string[] ExpectedSemanticBindings =
    [
        "PUSH0/PUSH1-PUSH32 instruction bytes -> exact GenerateOpcodeHandlers assignments -> four standard table roots",
        "dispatch PC/count/gas/overflow/terminal ordering -> checked Push0 and generic PushN wrappers",
        "traced PushN -> InstructionPush -> OpN zero-padded immediate -> EvmStack push",
        "untraced PUSH2 -> terminal elision or PUSH2+JUMP/JUMPI fusion -> jump destination validation",
        "Amsterdam -> complete named-mainnet ancestry -> Shanghai IsEip3855Enabled activation",
        "BlockProcessingModule IVirtualMachine registration -> EthereumVirtualMachine -> VirtualMachine<EthereumGasPolicy>",
        "Directory.Build.targets standard selector -> VirtualMachine.std/DispatchFlags.std",
    ];

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
        ValidateGas(sources);
        ValidateActivationAndForkLineage(sources);
        ValidateStandardMainnetReachability(sources);
        ValidateHandlers(sources);
        ValidatePushOperations(sources);
        ValidatePinnedSourceFingerprints(sources);

        IrDocument document = BuildExpectedIr();
        byte[] irBytes = Serialize(document);
        IrDocument serialized = DeserializeIr(irBytes);
        ValidateIr(serialized, document);
        byte[] leanBytes = LeanEmitter.Emit(serialized, Sha256(irBytes));

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
            new ArtifactIdentity(IrFileName, Sha256(irBytes)),
            new ArtifactIdentity(DefaultLeanRelativePath, Sha256(leanBytes)),
            CombinedHash(sourceIdentities),
            ExpectedSemanticBindings);
        byte[] manifestBytes = Serialize(manifest);
        SourceManifest serializedManifest = DeserializeManifest(manifestBytes);
        ValidateManifest(serializedManifest, manifest);
        Write(manifestPath, manifestBytes);
        return new ExtractionResult(irPath, manifestPath, leanPath, sources.Count, document.Opcodes.Length, document.Specializations.Length);
    }

    internal static IrDocument BuildExpectedIr()
    {
        OpcodeDescriptor[] opcodes = Enumerable.Range(0, 33).Select(static width =>
        {
            string instruction = $"PUSH{width}";
            string handler = width switch
            {
                0 => "Push0Opcode<TTracingInst>",
                2 => "Push2Opcode<TTracingInst>",
                _ => $"PushOpcode<EvmInstructions.Op{width},TTracingInst>",
            };
            string wrapper = width switch
            {
                0 => "EvmInstructions.InstructionPush0<TGasPolicy,TTracingInst>",
                2 => "EvmInstructions.InstructionPush2<TGasPolicy,TTracingInst>",
                _ => $"EvmInstructions.InstructionPush<TGasPolicy,EvmInstructions.Op{width},TTracingInst>",
            };
            string[] effects = width switch
            {
                0 => ["advanceOpcodePc", "chargeBase", "checkStackOverflow", "terminalElisionOrPushZero"],
                2 => ["advanceOpcodePc", "chargeVeryLow", "checkStackOverflowWhenUntraced", "terminalElisionOrFusionOrPush", "advanceImmediatePc"],
                _ => ["advanceOpcodePc", "chargeVeryLow", "checkStackOverflow", "terminalElisionOrPushZeroPaddedImmediate", "advanceImmediatePc"],
            };
            return new OpcodeDescriptor(
                $"push{width}", instruction, 0x5f + width, width, handler, wrapper,
                width == 0 ? 2 : 3, 0, 1, width != 2, width != 2, effects);
        }).ToArray();

        (string Table, string Tracing, string Cancellation)[] tables =
        [
            ("NoTrace", "OffFlag", "OffFlag"),
            ("NoTraceCancelable", "OffFlag", "OnFlag"),
            ("Traced", "OnFlag", "OffFlag"),
            ("TracedCancelable", "OnFlag", "OnFlag"),
        ];
        DispatchSpecialization[] specializations = tables.SelectMany(table => opcodes.Select(opcode =>
            new DispatchSpecialization(
                opcode.Name,
                table.Table,
                table.Tracing,
                table.Cancellation,
                $"VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<{CloseHandler(opcode, table.Tracing)},{table.Tracing},{table.Cancellation},OnFlag>")))
            .ToArray();

        return new IrDocument(
            SchemaVersion,
            ExtractorVersion,
            Kernel,
            new ReachabilityDescriptor(
                "IVirtualMachine",
                "EthereumVirtualMachine",
                "VirtualMachine<EthereumGasPolicy>",
                "EthereumGasPolicy",
                "Amsterdam",
                "Shanghai.IsEip3855Enabled inherited through the complete named-mainnet ancestry",
                "Directory.Build.targets selects *.std.cs when EnableZkEvm != true",
                "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode"),
            opcodes,
            specializations,
            1024,
            new Push2FusionDescriptor(
                0x56,
                0x57,
                0x5b,
                8,
                10,
                1,
                "untraced PUSH2 with at least two immediate bytes followed by JUMP or JUMPI",
                "stack overflow is checked after PUSH2 gas and before terminal/fusion selection",
                "remaining immediate bytes <= 2 advances PC by 2 without a push after the overflow check",
                "charge JUMP, validate the big-endian immediate destination, skip JUMPDEST, charge JUMPDEST",
                "charge JUMPI, pop condition, zero falls through past PUSH2 immediate and JUMPI; nonzero follows JUMP",
                "JumpDestination accepts only a valid JUMPDEST and returns its code offset",
                ["chargePush2", "checkOverflow", "detectTerminalOrNextOpcode", "incrementFusedOpcodeCount", "chargeJumpOrJumpi", "popJumpiCondition", "validateDestination", "skipAndChargeJumpdest"]),
            ForkNames,
            ExpectedOpenExtractionObligations);
    }

    private static string CloseHandler(OpcodeDescriptor opcode, string tracing) => opcode.ImmediateBytes switch
    {
        0 => $"Push0Opcode<{tracing}>",
        2 => $"Push2Opcode<{tracing}>",
        _ => $"PushOpcode<EvmInstructions.Op{opcode.ImmediateBytes},{tracing}>",
    };

    internal static IrDocument DeserializeIr(byte[] bytes)
    {
        if (bytes is null) throw new ExtractionException("Serialized PUSH opcode IR input is null.");
        try
        {
            IrDocument document = JsonSerializer.Deserialize<IrDocument>(bytes, JsonOptions)
                ?? throw new ExtractionException("Serialized PUSH opcode IR is empty.");
            LeanEmitter.Validate(document);
            return document;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Serialized PUSH opcode IR is malformed or contains unknown members: {exception.Message}");
        }
    }

    internal static void ValidateIr(IrDocument actual, IrDocument expected)
    {
        LeanEmitter.Validate(actual);
        LeanEmitter.Validate(expected);
        if (!Serialize(actual).AsSpan().SequenceEqual(Serialize(expected)))
            throw new ExtractionException("Serialized PUSH opcode IR does not exactly match the source-derived semantic document.");
    }

    internal static SourceManifest DeserializeManifest(byte[] bytes)
    {
        if (bytes is null) throw new ExtractionException("Serialized PUSH opcode manifest input is null.");
        try
        {
            SourceManifest manifest = JsonSerializer.Deserialize<SourceManifest>(bytes, JsonOptions)
                ?? throw new ExtractionException("Serialized PUSH opcode manifest is empty.");
            ValidateManifest(manifest);
            return manifest;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Serialized PUSH opcode manifest is malformed or contains unknown members: {exception.Message}");
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
            manifest.Ir.Path is null || manifest.Ir.Sha256 is null || manifest.Lean.Path is null || manifest.Lean.Sha256 is null ||
            manifest.SemanticBindings.Any(static binding => binding is null))
            throw new ExtractionException("Serialized PUSH opcode manifest contains null collections, entries, or fields.");
        if (manifest.SchemaVersion != SchemaVersion || manifest.ExtractorVersion != ExtractorVersion ||
            string.IsNullOrWhiteSpace(manifest.CompilerVersion) || manifest.LanguageVersion != LanguageVersion.CSharp14.ToString() ||
            manifest.Kernel != Kernel || manifest.Ir.Path != IrFileName || manifest.Lean.Path != DefaultLeanRelativePath ||
            !IsSha256(manifest.Ir.Sha256) || !IsSha256(manifest.Lean.Sha256) || !IsSha256(manifest.CombinedSha256) ||
            manifest.Sources.Any(static source => !IsSha256(source.Sha256)) ||
            manifest.Admissions.Any(static admission => !IsSha256(admission.SourceSha256)) ||
            !manifest.SemanticBindings.SequenceEqual(ExpectedSemanticBindings, StringComparer.Ordinal))
            throw new ExtractionException("Serialized PUSH opcode manifest header, build, artifact, or semantic identity changed.");
        string[] expectedPaths = SourceRelativePaths.OrderBy(static path => path, StringComparer.Ordinal).ToArray();
        if (manifest.Sources.Length != expectedPaths.Length ||
            !manifest.Sources.Select(static source => source.Path).SequenceEqual(expectedPaths, StringComparer.Ordinal) ||
            manifest.Sources.Select(static source => source.Path).Distinct(StringComparer.Ordinal).Count() != manifest.Sources.Length)
            throw new ExtractionException("Serialized PUSH opcode source identities changed or collide.");
        foreach (SourceIdentity source in manifest.Sources)
            if (!ExpectedSourceFingerprints.TryGetValue(source.Path, out string? expected) || source.Sha256 != expected)
                throw new ExtractionException($"Serialized PUSH opcode source fingerprint changed for {source.Path}.");
        if (manifest.CombinedSha256 != CombinedHash(manifest.Sources))
            throw new ExtractionException("Serialized PUSH opcode combined source fingerprint changed.");
        ValidateAdmissionIdentities(manifest.Admissions);
        if (!manifest.Admissions.SequenceEqual(BuildAdmissions(manifest.Sources)))
            throw new ExtractionException("Serialized PUSH opcode admissions do not match the source identities.");
    }

    internal static void ValidateManifest(SourceManifest actual, SourceManifest expected)
    {
        ValidateManifest(actual);
        ValidateManifest(expected);
        if (!Serialize(actual).AsSpan().SequenceEqual(Serialize(expected)))
            throw new ExtractionException("Serialized PUSH opcode manifest does not exactly match extracted lineage.");
    }

    internal static void ValidateAdmissionIdentities(IReadOnlyList<AdmissionIdentity> admissions)
    {
        if (admissions is null || admissions.Any(static admission => admission is null ||
                string.IsNullOrWhiteSpace(admission.Key) || !IsSha256(admission.SourceSha256)))
            throw new ExtractionException("PUSH opcode admission identities contain null, empty, or invalid values.");
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (AdmissionIdentity admission in admissions)
        {
            if (!admission.Key.EndsWith(":compilation-unit/0:complete-source", StringComparison.Ordinal))
                throw new ExtractionException($"PUSH opcode admission identity is not path/owner-qualified: {admission.Key}.");
            if (!keys.Add(admission.Key))
                throw new ExtractionException($"Duplicate owner-qualified PUSH opcode admission identity {admission.Key}.");
        }
    }

    private static AdmissionIdentity[] BuildAdmissions(IEnumerable<SourceIdentity> sources) => sources
        .Select(static source => new AdmissionIdentity($"{source.Path}:compilation-unit/0:complete-source", source.Sha256))
        .ToArray();

    private static void ValidateInstructionBytes(SourceFile source)
    {
        EnumDeclarationSyntax instruction = Single(
            source.Root.DescendantNodes().OfType<EnumDeclarationSyntax>(),
            static declaration => declaration.Identifier.ValueText == "Instruction",
            "Instruction enum");
        Dictionary<string, int> values = new(StringComparer.Ordinal);
        foreach (EnumMemberDeclarationSyntax member in instruction.Members)
        {
            if (member.EqualsValue?.Value is LiteralExpressionSyntax literal && literal.Token.Value is int value)
                values.Add(member.Identifier.ValueText, value);
        }
        for (int width = 0; width <= 32; width++)
            if (!values.TryGetValue($"PUSH{width}", out int actual) || actual != 0x5f + width)
                throw new ExtractionException($"Instruction.PUSH{width} must remain 0x{0x5f + width:x2}.");
    }

    private static void ValidateGas(Dictionary<string, SourceFile> sources)
    {
        SourceFile costs = Get(sources, GasCostPath);
        RequireCompactContains(costs, "publicconstulongBase=2;", "GasCostOf.Base must remain 2");
        RequireCompactContains(costs, "publicconstulongVeryLow=3;", "GasCostOf.VeryLow must remain 3");
        RequireCompactContains(costs, "publicconstulongMid=8;", "GasCostOf.Mid must remain 8");
        RequireCompactContains(costs, "publicconstulongHigh=10;", "GasCostOf.High must remain 10");
        RequireCompactContains(costs, "publicconstulongJump=Mid;", "GasCostOf.Jump must remain Mid");
        RequireCompactContains(costs, "publicconstulongJumpI=High;", "GasCostOf.JumpI must remain High");
        RequireCompactContains(costs, "publicconstulongJumpDest=1;", "GasCostOf.JumpDest must remain 1");
        SourceFile tags = Get(sources, GasTagsPath);
        RequireCompactContains(tags, "publicstaticulongGasCost=>GasCostOf.Base;", "BaseGasCost must delegate to Base");
        RequireCompactContains(tags, "publicstaticulongGasCost=>GasCostOf.VeryLow;", "VeryLowGasCost must delegate to VeryLow");
        RequireCompactContains(tags, "publicstaticulongGasCost=>GasCostOf.Jump;", "JumpGasCost must delegate to Jump");
        RequireCompactContains(tags, "publicstaticulongGasCost=>GasCostOf.JumpI;", "JumpIGasCost must delegate to JumpI");
        RequireCompactContains(tags, "publicstaticulongGasCost=>GasCostOf.JumpDest;", "JumpDestGasCost must delegate to JumpDest");
        RequireCompactContains(Get(sources, GasPolicyInterfacePath), "TSelf.UpdateGas(refgas,TCost.GasCost)", "gas tags must flow into the active gas policy");
        MethodDeclarationSyntax updateGas = Method(Get(sources, EthereumGasPolicyPath), "UpdateGas", 2, typeParameters: 0);
        RequireOrdered(updateGas, "GetRemainingGas(ingas)<gasCost", "gas.Value=0", "returnfalse", "ConsumeRaw(refgas,gasCost)", "returntrue");
    }

    private static void ValidateActivationAndForkLineage(Dictionary<string, SourceFile> sources)
    {
        RequireCompactContains(Get(sources, ReleaseInterfacePath), "boolIsEip3855Enabled{get;}", "IReleaseSpec must expose IsEip3855Enabled");
        RequireCompactContains(Get(sources, ReleaseSpecPath), "publicboolIsEip3855Enabled{get;set;}", "ReleaseSpec must store IsEip3855Enabled");
        RequireCompactContains(Get(sources, ReleaseExtensionsPath), "publicboolIncludePush0Instruction=>spec.IsEip3855Enabled;", "IncludePush0Instruction must map to IsEip3855Enabled");
        SourceFile named = Get(sources, NamedReleaseSpecPath);
        RequireCompactContains(named, "ReplayAncestors(fork.Parent);fork.Apply(this);", "NamedReleaseSpec must replay the full ancestor chain root-first");
        RequireCompactContains(named, "publicstaticNamedReleaseSpecInstance{get;}=newTSelf();", "NamedReleaseSpec<TSelf>.Instance must construct TSelf");

        string? parent = null;
        for (int index = 0; index < ForkPaths.Length; index++)
        {
            ClassDeclarationSyntax declaration = Single(
                Get(sources, ForkPaths[index]).Root.DescendantNodes().OfType<ClassDeclarationSyntax>(),
                candidate => candidate.Identifier.ValueText == ForkNames[index],
                $"{ForkNames[index]} fork class");
            string expectedBase = parent is null
                ? $"NamedReleaseSpec<{ForkNames[index]}>(null)"
                : $"NamedReleaseSpec<{ForkNames[index]}>({parent}.Instance)";
            if (!Compact(declaration.BaseList).Contains(expectedBase, StringComparison.Ordinal))
                throw new ExtractionException($"{ForkNames[index]} must inherit {parent ?? "the null root"} in the complete Amsterdam ancestry.");
            parent = ForkNames[index];
        }
        RequireCompactContains(Get(sources, ForkPaths[16]), "spec.IsEip3855Enabled=true;", "Shanghai must activate EIP-3855");
        for (int index = 17; index < ForkPaths.Length; index++)
        {
            if (Compact(Get(sources, ForkPaths[index]).Root).Contains("IsEip3855Enabled=", StringComparison.Ordinal))
                throw new ExtractionException($"{ForkNames[index]} must inherit PUSH0 activation without replay deactivation or reassignment.");
        }
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
            .Where(static group => (string?)group.Attribute("Condition") == "'$(EnableZkEvm)' != 'true'").ToArray() ?? [];
        XElement[] zkGroups = targets.Root?.Elements("ItemGroup")
            .Where(static group => (string?)group.Attribute("Condition") == "'$(EnableZkEvm)' == 'true'").ToArray() ?? [];
        if (standardGroups.Length != 1 ||
            standardGroups[0].Elements("Compile").Count(static element => (string?)element.Attribute("Remove") == "**/*.zkevm.cs") != 1 ||
            zkGroups.Length != 1 || zkGroups[0].Elements("Compile").Count(static element => (string?)element.Attribute("Remove") == "**/*.std.cs") != 1)
            throw new ExtractionException("Directory.Build.targets must retain mutually exclusive standard and zkEVM source selectors.");

        RequireCompactContains(Get(sources, VirtualMachinePath), "publicsealedclassEthereumVirtualMachine", "EthereumVirtualMachine must remain the mainnet concrete VM");
        RequireCompactContains(Get(sources, VirtualMachinePath), ":VirtualMachine<EthereumGasPolicy>", "EthereumVirtualMachine must close VirtualMachine over EthereumGasPolicy");
        RequireCompactContains(Get(sources, MainnetDiPath), ".AddScoped<IVirtualMachine,EthereumVirtualMachine>()", "mainnet block processing must register EthereumVirtualMachine");
        SourceFile standardVm = Get(sources, StandardVirtualMachinePath);
        RequireCompactContains(standardVm, "privateOpcodeTableGetOpcodeTable()=>_opcodeTablesBySpec.GetValue(Spec,static_=>newOpcodeTable());", "standard VM must construct an empty per-spec opcode table");
        RequireCompactContains(standardVm, "if(_txCount>=OpcodeRefreshLimit||Interlocked.Increment(ref_txCount)%OpcodeRefreshInterval!=0)returnfalse;", "standard VM refresh must not replace table provenance");
        RequireCompactContains(Get(sources, StandardDispatchFlagsPath), "publicconstboolConstTracing=true;", "standard build must expose traced tables");
        RequireCompactContains(Get(sources, TypeFlagsPath), "publicstructOffFlag:IFlag", "OffFlag must close inactive roots");
        RequireCompactContains(Get(sources, TypeFlagsPath), "publicstaticboolIsActive=>false;", "OffFlag must remain inactive");
        RequireCompactContains(Get(sources, TypeFlagsPath), "publicstructOnFlag:IFlag", "OnFlag must close active roots");
        RequireCompactContains(Get(sources, TypeFlagsPath), "publicstaticboolIsActive=>true;", "OnFlag must remain active");

        SourceFile dispatch = Get(sources, DispatchPath);
        RequireCompactContains(dispatch, "GetOpcodeTable().GetHandlers<TTracingInst,TCancelable>(Spec)", "dispatch must read the standard opcode table");
        RequireCompactContains(dispatch, "returntable??=GenerateOpcodeHandlers<TTracingInst,TCancelable>(spec);", "all tables must originate in GenerateOpcodeHandlers");
        RequireCompactContains(dispatch,
            "refTTracingInst.IsActive?ref(TCancelable.IsActive?refTracedCancelable:refTraced):ref(TCancelable.IsActive?refNoTraceCancelable:refNoTrace)",
            "the four tables must remain bound to exact tracing and cancellation flags");
        MethodDeclarationSyntax prepare = Method(dispatch, "PrepareOpcodes", 0, typeParameters: 2);
        RequireOrdered(prepare, "IReleaseSpecspec=Spec", "SpecFlags.Validate(spec)", "OpcodeTabletable=GetOpcodeTable()",
            "if(!TTracingInst.IsActive&&ShouldRefreshOpcodes())", "table.RefreshNonTraced(spec)",
            "_executionHandlers=table.GetExecutionHandlers(spec)", "_opcodeHandlers=table.GetHandlers<TTracingInst,TCancelable>(spec)");
        MethodDeclarationSyntax getHandlers = Method(dispatch, "GetHandlers", 1, typeParameters: 2);
        RequireOrdered(getHandlers, "refTTracingInst.IsActive", "TracedCancelable", "Traced", "TCancelable.IsActive", "NoTraceCancelable", "NoTrace",
            "returntable??=GenerateOpcodeHandlers<TTracingInst,TCancelable>(spec)");
        MethodDeclarationSyntax refresh = Method(dispatch, "RefreshNonTraced", 1, typeParameters: 0);
        RequireOrdered(refresh, "NoTrace=GenerateOpcodeHandlers<OffFlag,OffFlag>(spec)",
            "NoTraceCancelable=GenerateOpcodeHandlers<OffFlag,OnFlag>(spec)");
        MethodDeclarationSyntax execute = Method(dispatch, "ExecuteOpcode", 5, typeParameters: 4);
        RequireOrdered(execute, "pc++", "opCodeCount++", "if(TOpcode.HasCheckedBody)", "TryConsumeGas", "EnsureDepth", "StackGrowth", "PushSize", "TOpcode.Execute");
        RejectDirectives(execute, "ExecuteOpcode");
    }

    private static void ValidateHandlers(Dictionary<string, SourceFile> sources)
    {
        SourceFile handlers = Get(sources, HandlersPath);
        RejectDirectives(handlers.Root, "PUSH dispatch and wrappers");
        MethodDeclarationSyntax opcodeHandler = Method(handlers, "OpcodeHandler", 0, typeParameters: 3);
        if (Compact(opcodeHandler.ExpressionBody?.Expression) != "&ExecuteOpcode<TOpcode,TTracingInst,TCancelable,OnFlag>")
            throw new ExtractionException("OpcodeHandler must bind the exact continuable ExecuteOpcode root.");
        MethodDeclarationSyntax generate = Method(handlers, "GenerateOpcodeHandlers", 1, typeParameters: 2);
        for (int width = 0; width <= 32; width++)
        {
            string right = width switch
            {
                0 => "OpcodeHandler<Push0Opcode<TTracingInst>,TTracingInst,TCancelable>()",
                2 => "OpcodeHandler<Push2Opcode<TTracingInst>,TTracingInst,TCancelable>()",
                _ => $"OpcodeHandler<PushOpcode<EvmInstructions.Op{width},TTracingInst>,TTracingInst,TCancelable>()",
            };
            ValidateDispatchAssignment(handlers, generate, $"PUSH{width}", right, width == 0 ? "spec.IncludePush0Instruction" : null);
        }

        StructDeclarationSyntax push0 = Struct(handlers, "Push0Opcode", 1);
        RequireOrdered(push0, "PushSize=>0", "get=>!TTracingInst.IsActive", "UpdateGas<GasPolicy.BaseGasCost>", "StackGrowth=>1", "stack.PushZero<TTracingInst,OffFlag>()", "InstructionPush0<TGasPolicy,TTracingInst>");
        StructDeclarationSyntax push2 = Struct(handlers, "Push2Opcode", 1);
        RequireCompactContains(push2, "InstructionPush2<TGasPolicy,TTracingInst>(refstack,refgas,vm,refprogramCounter)", "Push2Opcode must delegate to InstructionPush2");
        StructDeclarationSyntax push = Struct(handlers, "PushOpcode", 2);
        RequireOrdered(push, "PushSize=>TOpCount.Count", "get=>!TTracingInst.IsActive", "UpdateGas<GasPolicy.VeryLowGasCost>", "StackGrowth=>1", "if(!HasCheckedBody)", "InstructionPush<TGasPolicy,TOpCount,TTracingInst>", "TOpCount.Push<TTracingInst,OffFlag,OffFlag>", "programCounter+=TOpCount.Count");
    }

    private static void ValidatePushOperations(Dictionary<string, SourceFile> sources)
    {
        SourceFile instructions = Get(sources, StackInstructionsPath);
        RejectDirectives(instructions.Root, "PUSH instruction bodies");
        RequireCompactContains(instructions, "publicstructOp0:IOpCount", "Op0 must implement IOpCount");
        RequireCompactContains(instructions, "publicstaticintCount=>0;", "Op0 count must be zero");
        RequireCompactContains(instructions, "publicstructOp2:IOpCount", "Op2 must remain reserved for InstructionPush2");
        RequireCompactContains(instructions, "thrownewNotSupportedException", "generic Op2 must remain unreachable");
        RequireCompactContains(instructions, "nameof(InstructionPush2)", "generic Op2 failure must identify InstructionPush2");
        MethodDeclarationSyntax push0 = Method(instructions, "InstructionPush0", 3);
        RequireOrdered(push0, "UpdateGas<BaseGasCost>", "stack.PushZero<TTracingInst,OnFlag>");
        MethodDeclarationSyntax generic = Method(instructions, "InstructionPush", 3);
        RequireOrdered(generic, "UpdateGas<VeryLowGasCost>", "TOpCount.Push<TTracingInst,OnFlag,OnFlag>", "programCounter+=TOpCount.Count");
        MethodDeclarationSyntax push2 = Method(instructions, "InstructionPush2", 4);
        RequireOrdered(push2,
            "UpdateGas<VeryLowGasCost>", "remainingCode=stack.CodeLength-programCounter", "if(!TTracingInst.IsActive)",
            "stack.Head>=EvmStack.MaxStackSize-1", "programCounter+=Size", "remainingCode<=Size", "programCounter+=Size",
            "nextInstruction", "destination", "ReverseEndianness", "Instruction.JUMP", "vm.OpCodeCount++",
            "UpdateGas<JumpGasCost>", "UpdateGas<JumpIGasCost>", "stack.EnsureDepth(1)", "stack.PopBytesByRefUnchecked",
            "programCounter+=Size+1", "JumpDestination", "programCounter=jumpTarget+1", "PrefetchCodeAtDestination",
            "vm.OpCodeCount++", "UpdateGas<JumpDestGasCost>", "stack.Push2Bytes", "remainingCode==Op1.Count",
            "stack.PushUInt32", "stack.PushZero", "programCounter+=Size");
        RejectAnyBefore(push2, "UpdateGas<VeryLowGasCost>", "stack.Head", "PUSH2 must charge push gas before overflow checking");

        for (int width = 1; width <= 32; width++)
        {
            StructDeclarationSyntax op = Struct(instructions, $"Op{width}", 0);
            RequireCompactContains(op, $"publicstaticintCount=>{(width == 2 ? "2" : "Size")};", $"Op{width}.Count must match its immediate width");
            if (width != 2)
                RequireCompactContains(op, width == 1 ? "constintSize=sizeof(byte);" : $"constintSize={width};", $"Op{width}.Size must match its immediate width");
            if (width == 1)
                RequireOrdered(op, "usedFromCode=stack.CodeLength-programCounter", "usedFromCode>=Size", "stack.PushByte", "stack.PushZero");
            else if (width != 2)
                RequireOrdered(op, "usedFromCode=stack.CodeLength-programCounter", $"usedFromCode>=Size", $"stack.Push{width}Bytes", "stack.PushBothPaddedBytes", "usedFromCode", "Size");
        }

        SourceFile stack = Get(sources, StackPath);
        RequireCompactContains(stack, "publicconstintMaxStackSize=1025;", "EvmStack next-free head sentinel must retain 1024 usable slots");
        RequireCompactContains(stack, "publicnintHead;", "EvmStack Head must remain the next-free slot index");
        MethodDeclarationSyntax padded = Method(stack, "PushBothPaddedBytes", 3, typeParameters: 2);
        RequireOrdered(padded, "newOffset=headOffset+1", "newOffset>=MaxStackSize", "Head=newOffset", "pushSize==WordSize", "default", "if(used!=0)", "WordSize-pushSize", "ReportPushWord");
    }

    private static void ValidateDispatchAssignment(SourceFile handlers, MethodDeclarationSyntax generate, string instruction, string expectedRight, string? expectedGate)
    {
        string left = $"lookup[(int)Instruction.{instruction}]";
        AssignmentExpressionSyntax[] assignments = handlers.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(assignment => Compact(assignment.Left) == left).ToArray();
        if (assignments.Length != 1 || !assignments[0].Ancestors().Contains(generate))
            throw new ExtractionException($"Instruction.{instruction} must have exactly one live assignment inside GenerateOpcodeHandlers.");
        if (Compact(assignments[0].Right) != expectedRight)
            throw new ExtractionException($"Instruction.{instruction} dispatch changed from {expectedRight}.");
        IfStatementSyntax[] gates = assignments[0].Ancestors().TakeWhile(node => node != generate).OfType<IfStatementSyntax>().ToArray();
        if (expectedGate is null ? gates.Length != 0 : gates.Length != 1 || Compact(gates[0].Condition) != expectedGate)
            throw new ExtractionException($"Instruction.{instruction} dispatch activation gate changed.");
    }

    private static void ValidatePinnedSourceFingerprints(Dictionary<string, SourceFile> sources)
    {
        if (ExpectedSourceFingerprints.Count != SourceRelativePaths.Length)
            throw new ExtractionException("The reviewed PUSH source fingerprint closure is incomplete.");
        foreach (string path in SourceRelativePaths)
        {
            if (!ExpectedSourceFingerprints.TryGetValue(path, out string? expected) || Get(sources, path).Sha256 != expected)
                throw new ExtractionException($"Complete-source admission fingerprint changed for {path}.");
        }
    }

    private static Dictionary<string, SourceFile> LoadSources(string root)
    {
        Dictionary<string, SourceFile> sources = new(StringComparer.Ordinal);
        foreach (string relativePath in SourceRelativePaths)
        {
            string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
            EnsureWithin(root, fullPath);
            if (!File.Exists(fullPath)) throw new ExtractionException($"Required production source is missing: {relativePath}.");
            byte[] bytes = File.ReadAllBytes(fullPath);
            CompilationUnitSyntax? syntax = null;
            if (relativePath.EndsWith(".cs", StringComparison.Ordinal))
            {
                SyntaxTree tree = CSharpSyntaxTree.ParseText(Encoding.UTF8.GetString(bytes), ParseOptions, relativePath);
                Diagnostic[] errors = tree.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
                if (errors.Length != 0) throw new ExtractionException($"Roslyn rejected {relativePath}: {errors[0]}.");
                syntax = (CompilationUnitSyntax)tree.GetRoot();
            }
            if (!sources.TryAdd(relativePath, new SourceFile(relativePath, bytes, Sha256(bytes), syntax)))
                throw new ExtractionException($"Duplicate source path in PUSH extraction profile: {relativePath}.");
        }
        return sources;
    }

    private static MethodDeclarationSyntax Method(SourceFile source, string name, int parameters, int? typeParameters = null) => Single(
        source.Root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
        method => method.Identifier.ValueText == name && method.ParameterList.Parameters.Count == parameters &&
            (typeParameters is null || method.TypeParameterList?.Parameters.Count == typeParameters || typeParameters == 0 && method.TypeParameterList is null),
        $"method {name}/{parameters}");

    private static StructDeclarationSyntax Struct(SourceFile source, string name, int typeParameters) => Single(
        source.Root.DescendantNodes().OfType<StructDeclarationSyntax>(),
        declaration => declaration.Identifier.ValueText == name &&
            (declaration.TypeParameterList?.Parameters.Count ?? 0) == typeParameters,
        $"struct {name}/{typeParameters}");

    private static T Single<T>(IEnumerable<T> values, Func<T, bool> predicate, string description)
    {
        T[] matches = values.Where(predicate).ToArray();
        return matches.Length == 1 ? matches[0] : throw new ExtractionException($"Expected exactly one {description}; found {matches.Length}.");
    }

    private static void RequireCompactContains(SourceFile source, string expected, string description) => RequireCompactContains(source.Root, expected, description);

    private static void RequireCompactContains(SyntaxNode node, string expected, string description)
    {
        if (!Compact(node).Contains(expected, StringComparison.Ordinal))
            throw new ExtractionException($"Production binding changed: {description}.");
    }

    private static void RequireOrdered(SyntaxNode node, params string[] fragments)
    {
        string text = Compact(node);
        int position = -1;
        foreach (string fragment in fragments)
        {
            int next = text.IndexOf(fragment, position + 1, StringComparison.Ordinal);
            if (next < 0) throw new ExtractionException($"Ordered production fragment '{fragment}' is missing or moved.");
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

    private static void RejectDirectives(SyntaxNode node, string description)
    {
        if (node.DescendantTrivia(descendIntoTrivia: true).Any(static trivia => trivia.IsDirective))
            throw new ExtractionException($"Preprocessor-controlled or disabled code is not admitted in {description}.");
    }

    private static string Compact(SyntaxNode? node) => node is null ? string.Empty : string.Concat(node.DescendantTokens().Select(static token => token.Text));

    private static SourceFile Get(IReadOnlyDictionary<string, SourceFile> sources, string path) =>
        sources.TryGetValue(path, out SourceFile? source) ? source : throw new ExtractionException($"The PUSH extraction source set omits {path}.");

    private static byte[] Serialize<T>(T value) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");

    private static string CombinedHash(IEnumerable<SourceIdentity> sources)
    {
        StringBuilder text = new();
        foreach (SourceIdentity source in sources) text.Append(source.Path).Append('\n').Append(source.Sha256).Append('\n');
        return Sha256(Encoding.UTF8.GetBytes(text.ToString()));
    }

    private static bool IsSha256(string value) => value.Length == 64 &&
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
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
