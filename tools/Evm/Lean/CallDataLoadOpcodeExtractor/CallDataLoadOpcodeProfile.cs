// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.CallDataLoadOpcodeExtractor;

internal static class CallDataLoadOpcodeProfile
{
    internal const string IrFileName = "CallDataLoadOpcodeKernel.ir.json";
    internal const string ManifestFileName = "CallDataLoadOpcodeKernel.source-manifest.json";
    internal const string LeanFileName = "CallDataLoadOpcodeKernel.lean";

    private const string ExtractorVersion = "1.0.0";
    private const string KernelName = "standard-mainnet-amsterdam-calldataload-operational";
    private const string BuildPropsPath = "src/Nethermind/Directory.Build.props";
    private const string BuildPropsSha256 = "ad544425af1b0ec60334511abf79896a77843088457ef81b98a06b56d13e4e3c";
    private const string BuildTargetsPath = "src/Nethermind/Directory.Build.targets";
    private const string BuildTargetsSha256 = "0598cebaef1df41102a18a3f9ace810bed1e4e64b8471d055724b4a394dccec6";
    private const string OperationalSpecPath = "tools/Evm/Lean/CallDataLoadOpcodeExtractor/Specification/CallDataLoadExecution.lean";
    private const string OperationalSpecSha256 = "b33f168d40e558264fb398d6f44112a8a2d1adc77c413ab5e2fcc8fa79c2526b";
    private const string ExpectedCombinedSourceSha256 = "266e7941aaae6cd07bd0cc3036ba436f2345f9c2bcb79c9827c0752524069416";
    private const string OpcodeHandlersPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs";
    private const string DispatchPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs";
    private const string StoragePath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Storage.cs";
    private const string StackPath = "src/Nethermind/Nethermind.Evm/EvmStack.cs";
    private const string StackStandardPath = "src/Nethermind/Nethermind.Evm/EvmStack.std.cs";
    private const string VmStatePath = "src/Nethermind/Nethermind.Evm/VmState.cs";
    private const string TraceStackPath = "src/Nethermind/Nethermind.Evm/Tracing/TraceStack.cs";
    private const string BytesPath = "src/Nethermind/Nethermind.Core/Extensions/Bytes.cs";
    private const string BytesStandardPath = "src/Nethermind/Nethermind.Core/Extensions/Bytes.std.cs";
    private const string ReleaseSpecPath = "src/Nethermind/Nethermind.Core/Specs/IReleaseSpec.cs";
    private const string MemoryPath = "src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs";
    private const string GasInterfacePath = "src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs";
    private const string GasPath = "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs";
    private const string VirtualMachineInterfacePath = "src/Nethermind/Nethermind.Evm/IVirtualMachine.cs";
    private const string VmPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.cs";
    private const string VmStandardPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.std.cs";
    private const string BlockProcessingModulePath = "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs";
    private const string AmsterdamPath = "src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs";

    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    private static readonly string[] ForkLineage =
    [
        "Olympic", "Frontier", "Homestead", "Dao", "TangerineWhistle", "SpuriousDragon",
        "Byzantium", "Constantinople", "ConstantinopleFix", "Istanbul", "MuirGlacier",
        "Berlin", "London", "ArrowGlacier", "GrayGlacier", "Paris", "Shanghai", "Cancun",
        "Prague", "Osaka", "BPO1", "BPO2", "Amsterdam",
    ];

    private static readonly IReadOnlyDictionary<string, string> ExpectedSourceSha256 =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/Nethermind/Nethermind.Core/GasCostOf.cs"] = "10f1f1a29e7bf10a8220422a6f7571af60df4f05ad4075268e3b26559a73c6f5",
            [BytesPath] = "03b84f9fc045b3cd2f6730924d1a85435613536db2f8bfca1d76b154d313cd03",
            [BytesStandardPath] = "92617ca661bbd2785d8eb08cf3694ae4a5e08b31c618c87c9aecd2f75c1a0a91",
            [ReleaseSpecPath] = "3f44b3da5614c94d59eae2e4a6266a9e698614f75a7b5a9894a4828b0021b18a",
            ["src/Nethermind/Nethermind.Core/TypeFlags.cs"] = "33d8404d3ec39fffbc9f665126255f9799a0ba433206a141f480f8144be95747",
            ["src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs"] = "99c58367e5b90fa5904484f6e2fbf10745a54f6cd7d07275810c27028ad0eb2b",
            ["src/Nethermind/Nethermind.Evm/Instruction.cs"] = "6895d06277ac4f9369d7bdc748976576bf384f6f3a5a348a2e4fd3767a46a030",
            [StackPath] = "561520be4f0fe1d533ddea9cfea7ff48edb2745b1f3adf1b45c24e6c0bed02ad",
            [StackStandardPath] = "01a1b26ce105fa69fff94b8df171dcf11b85fe5462318a3ff9da3889ff41577e",
            [MemoryPath] = "853f8f9b46e5deef1c655442694dfbb1f862de80d0ecdbee2d66e7282f974e84",
            [VmStatePath] = "7b7b6ddb753118b426d14976a3fe8e79f808320d6a09ea9cace9df19197f9f23",
            ["src/Nethermind/Nethermind.Evm/ExecutionEnvironment.cs"] = "c076225b688b1e04ed83cddc90a2ddbda69862333dc927945eb32d376e43e1db",
            ["src/Nethermind/Nethermind.Evm/EvmFrameMemory.cs"] = "4bef331a3b36589c51e0560b3b288cacf2c85e582deb79f9c2f25284ccb56507",
            ["src/Nethermind/Nethermind.Evm/StackPool.cs"] = "4fa29adf34149fc192f9fc13140b8c8bdc3a8fa7b0f0b2aec94717670d6131c4",
            ["src/Nethermind/Nethermind.Evm/StackPool.std.cs"] = "62042951d6b5cf767cce1a1929dbf82ce26127b8884c784ab6708320f418c8f2",
            ["src/Nethermind/Nethermind.Evm/GasPolicy/IGasCost.cs"] = "16289cdc2f777a76fda3ad8ff4ce4275c30513aee04c9cf42d314cc6e1ec48c0",
            [GasInterfacePath] = "b4d26db87723243514f2f6b8a5e48d1176139105c3b012ad0f3956e59afc98a8",
            [GasPath] = "3c31ba40c24a78bf11243b38859168350f02934806124e913da0560bc1a4350f",
            [VirtualMachineInterfacePath] = "330da5f31bd7aca4f4212a42c912c33cf0ac5b3def0689b7f9985cd6510aa56d",
            [StoragePath] = "0101effa0c71c4a602021f18de7075e047974b75b37b9d672364499db0bf390d",
            ["src/Nethermind/Nethermind.Evm/Tracing/ITxTracer.cs"] = "9f2550f8c3407f494452386699040feab8aa597437b1cd4180d038d9fd0a9169",
            [TraceStackPath] = "a2e06210112f3a47bca6f95e4b3a89433c78f4d39b5ec4cbafe485f21191acb8",
            ["src/Nethermind/Nethermind.Evm/Tracing/TraceMemory.cs"] = "0e8bda869acd599a94a8cd6a9a79a7ff457c4acf1534a0c7f4ebd39939b8fca6",
            [DispatchPath] = "4f36bb20057caec9c85bcd3621372563f4d47ba36a9cbf01be267af4e379bca1",
            [OpcodeHandlersPath] = "5148dd594e40ebb976179b1048f233914f54acc8d033d358cf3e2e7594e112b6",
            [VmPath] = "45edba3691e09185e749485785990662ddea1af849bc6e4564f92b68a5137a6b",
            [VmStandardPath] = "357fbd35369424a379c8c29338f491627512d40a12199d7a3d9aac44c87ca92a",
            [BlockProcessingModulePath] = "fce65ce5bb523c56fc8aa6ee0e4d092c940a5a90b174c820ffe262eeb5241c60",
            ["src/Nethermind/Nethermind.Specs/Forks/NamedReleaseSpec.cs"] = "ea9400ed44270fb18766a5dbd20d4dd7e7ee95b1bb7ab7ee6d5406cca03a46cc",
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
            [AmsterdamPath] = "4dfbf9079e9dce30423327ae4433f32e49388d403a8fedfcf253f94f79450c6a",
        };

    private static readonly AdmissionSpec[] Admissions = BuildAdmissionSpecs();

    internal static IReadOnlyList<string> SourcePaths => ExpectedSourceSha256.Keys.ToArray();
    internal static IReadOnlyList<string> InputPaths =>
        [.. ExpectedSourceSha256.Keys, BuildPropsPath, BuildTargetsPath, OperationalSpecPath];

    internal static ExtractionResult Extract(string repoRoot, string outputDirectory, string? leanOutputPath = null)
    {
        string root = Path.GetFullPath(repoRoot);
        Dictionary<string, SourceFile> sources = LoadSources(root);
        RawSourceIdentity buildProps = LoadBuildAliases(root);
        RawSourceIdentity build = LoadBuildSelector(root);
        byte[] operationalSpec = File.ReadAllBytes(ResolveExactPath(root, OperationalSpecPath));
        string operationalSpecSha = Hash(operationalSpec);
        if (operationalSpecSha != OperationalSpecSha256)
            throw new ExtractionException($"Unadmitted operational specification template; found SHA-256 {operationalSpecSha}.");
        List<AdmissionIdentity> admissions = AdmitSyntax(sources);
        ValidateProductionClosure(root, sources);

        IrDocument ir = ExpectedIr();
        byte[] irBytes = Serialize(ir);
        IrDocument roundTrip = DeserializeIr(irBytes);
        string irSha = Hash(irBytes);
        SourceIdentity[] identities = sources.Values.OrderBy(static source => source.RelativePath, StringComparer.Ordinal)
            .Select(static source => new SourceIdentity(source.RelativePath, source.Sha256, Hash(CompleteCanonical(source.Root)))).ToArray();
        RawSourceIdentity[] rawSources = [buildProps, build, new(OperationalSpecPath, operationalSpecSha)];
        string sourceSha = CombinedSourceHash(identities, rawSources, admissions);
        byte[] leanBytes = CallDataLoadOpcodeLeanEmitter.Emit(roundTrip, irSha, sourceSha);

        SourceManifest manifest = new(
            1,
            ExtractorVersion,
            typeof(CSharpSyntaxTree).Assembly.GetName().Version?.ToString() ?? "unknown",
            "CSharp14",
            KernelName,
            identities,
            rawSources,
            [.. admissions],
            new(IrFileName, irSha),
            new(LeanFileName, Hash(leanBytes)),
            sourceSha,
            SemanticBindings());
        byte[] manifestBytes = Serialize(manifest);
        ValidateManifest(DeserializeManifest(manifestBytes), manifest);

        Directory.CreateDirectory(outputDirectory);
        string irPath = Path.Combine(outputDirectory, IrFileName);
        string manifestPath = Path.Combine(outputDirectory, ManifestFileName);
        string leanPath = leanOutputPath ?? Path.Combine(outputDirectory, LeanFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(leanPath)!);
        WriteDeterministic(irPath, irBytes);
        WriteDeterministic(manifestPath, manifestBytes);
        WriteDeterministic(leanPath, leanBytes);
        return new(irPath, manifestPath, leanPath, ir.Opcodes.Length, ir.Specializations.Length, admissions.Count);
    }

    internal static IrDocument DeserializeIr(byte[] bytes)
    {
        RejectDuplicateProperties(bytes, "IR");
        try
        {
            IrDocument value = JsonSerializer.Deserialize<IrDocument>(bytes, JsonOptions)
                ?? throw new ExtractionException("Serialized calldata-load opcode IR is empty.");
            ValidateIr(value);
            return value;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Serialized calldata-load opcode IR is not valid JSON: {exception.Message}");
        }
    }

    internal static SourceManifest DeserializeManifest(byte[] bytes)
    {
        RejectDuplicateProperties(bytes, "manifest");
        try
        {
            SourceManifest value = JsonSerializer.Deserialize<SourceManifest>(bytes, JsonOptions)
                ?? throw new ExtractionException("Serialized calldata-load opcode manifest is empty.");
            ValidateManifestShape(value);
            return value;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Serialized calldata-load opcode manifest is not valid JSON: {exception.Message}");
        }
    }

    internal static void ValidateIr(IrDocument value)
    {
        if (value.Schedule is null || value.OuterFailure is null || value.DispatchTables is null || value.Opcodes is null ||
            value.Specializations is null || value.ForkLineage is null || value.OpenExtractionObligations is null ||
            value.DispatchTables.Any(static item => item is null) || value.Opcodes.Any(static item => item is null || item.Semantics is null || item.EffectOrder is null) ||
            value.Specializations.Any(static item => item is null))
            throw new ExtractionException("Serialized calldata-load opcode IR contains null fields, collections, or entries.");
        if (!Serialize(value).AsSpan().SequenceEqual(Serialize(ExpectedIr())))
            throw new ExtractionException("Serialized calldata-load opcode IR is not the exact admitted profile.");
    }

    internal static void ValidateManifest(SourceManifest value, SourceManifest expected)
    {
        ValidateManifestShape(value);
        if (!Serialize(value).AsSpan().SequenceEqual(Serialize(expected)))
            throw new ExtractionException("Serialized calldata-load opcode manifest does not exactly match extracted lineage.");
    }

    private static void ValidateManifestShape(SourceManifest value)
    {
        if (value.Sources is null || value.RawSources is null || value.Admissions is null || value.Ir is null ||
            value.Lean is null || value.SemanticBindings is null || value.Sources.Any(static item => item is null) ||
            value.RawSources.Any(static item => item is null) || value.Admissions.Any(static item => item is null) ||
            value.Sources.Any(static item => string.IsNullOrEmpty(item.Path) || string.IsNullOrEmpty(item.Sha256) ||
                string.IsNullOrEmpty(item.RoslynSyntaxSha256)) ||
            value.RawSources.Any(static item => string.IsNullOrEmpty(item.Path) || string.IsNullOrEmpty(item.Sha256)) ||
            value.Admissions.Any(static item => string.IsNullOrEmpty(item.SourcePath) || string.IsNullOrEmpty(item.Namespace) ||
                string.IsNullOrEmpty(item.OwnerPath) || string.IsNullOrEmpty(item.MemberKind) ||
                string.IsNullOrEmpty(item.MemberName) || item.ParameterTypes is null ||
                string.IsNullOrEmpty(item.SyntaxKind) || string.IsNullOrEmpty(item.Sha256)) ||
            string.IsNullOrEmpty(value.Ir.Path) || string.IsNullOrEmpty(value.Ir.Sha256) ||
            string.IsNullOrEmpty(value.Lean.Path) || string.IsNullOrEmpty(value.Lean.Sha256) ||
            string.IsNullOrEmpty(value.CombinedSourceSha256) ||
            value.SemanticBindings.Any(static binding => binding is null))
            throw new ExtractionException("Serialized calldata-load opcode manifest contains null fields, collections, or entries.");
        if (value.SchemaVersion != 1 || value.ExtractorVersion != ExtractorVersion || value.Kernel != KernelName ||
            value.LanguageVersion != "CSharp14" || value.RoslynVersion != (typeof(CSharpSyntaxTree).Assembly.GetName().Version?.ToString() ?? "unknown"))
            throw new ExtractionException("Serialized calldata-load opcode manifest header changed.");
        if (value.Sources.Length != ExpectedSourceSha256.Count ||
            value.Sources.Select(static item => item.Path).Distinct(StringComparer.Ordinal).Count() != value.Sources.Length)
            throw new ExtractionException("Serialized calldata-load source identity set changed or collides.");
        foreach (SourceIdentity source in value.Sources)
        {
            ValidateSha256(source.Sha256, source.Path);
            ValidateSha256(source.RoslynSyntaxSha256, source.Path + " syntax");
            if (!ExpectedSourceSha256.TryGetValue(source.Path, out string? expected) || source.Sha256 != expected)
                throw new ExtractionException($"Serialized calldata-load source digest changed for {source.Path}.");
        }
        if (value.RawSources.Length != 3 ||
            value.RawSources.Select(static item => item.Path).Distinct(StringComparer.Ordinal).Count() != 3 ||
            !value.RawSources.Any(static item => item.Path == BuildPropsPath && item.Sha256 == BuildPropsSha256) ||
            !value.RawSources.Any(static item => item.Path == BuildTargetsPath && item.Sha256 == BuildTargetsSha256) ||
            !value.RawSources.Any(static item => item.Path == OperationalSpecPath && item.Sha256 == OperationalSpecSha256))
            throw new ExtractionException("Serialized calldata-load raw-source identity set changed.");
        foreach (RawSourceIdentity source in value.RawSources) ValidateSha256(source.Sha256, source.Path);
        if (value.Admissions.Length != Admissions.Length ||
            value.Admissions.Select(AdmissionKey).Distinct(StringComparer.Ordinal).Count() != value.Admissions.Length)
            throw new ExtractionException("Serialized calldata-load exact-admission set changed or collides.");
        for (int index = 0; index < Admissions.Length; index++)
        {
            AdmissionSpec expected = Admissions[index];
            AdmissionIdentity actual = value.Admissions[index];
            ValidateSha256(actual.Sha256, AdmissionKey(actual));
            if (actual.SourcePath != expected.SourcePath || actual.Namespace != expected.Namespace ||
                actual.OwnerPath != expected.OwnerPath || actual.MemberKind != expected.MemberKind ||
                actual.MemberName != expected.MemberName || actual.MemberGenericArity != expected.MemberGenericArity ||
                actual.ParameterTypes != expected.ParameterTypes || string.IsNullOrWhiteSpace(actual.SyntaxKind))
                throw new ExtractionException($"Serialized calldata-load admission qualifier changed at index {index}.");
        }
        if (value.Ir.Path != IrFileName || value.Lean.Path != LeanFileName)
            throw new ExtractionException("Serialized calldata-load artifact path changed.");
        ValidateSha256(value.Ir.Sha256, value.Ir.Path);
        ValidateSha256(value.Lean.Sha256, value.Lean.Path);
        byte[] canonicalIr = Serialize(ExpectedIr());
        string canonicalIrSha = Hash(canonicalIr);
        if (value.Ir.Sha256 != canonicalIrSha)
            throw new ExtractionException("Serialized calldata-load IR artifact digest is not canonical.");
        ValidateSha256(value.CombinedSourceSha256, "combined source");
        if (value.CombinedSourceSha256 != CombinedSourceHash(value.Sources, value.RawSources, value.Admissions))
            throw new ExtractionException("Serialized calldata-load combined source digest is inconsistent.");
        if (value.CombinedSourceSha256 != ExpectedCombinedSourceSha256)
            throw new ExtractionException(
                $"Serialized calldata-load source closure is not the admitted closure; found {value.CombinedSourceSha256}.");
        byte[] canonicalLean = CallDataLoadOpcodeLeanEmitter.Emit(
            DeserializeIr(canonicalIr), canonicalIrSha, value.CombinedSourceSha256);
        if (value.Lean.Sha256 != Hash(canonicalLean))
            throw new ExtractionException("Serialized calldata-load Lean artifact digest is not canonical.");
        if (!value.SemanticBindings.SequenceEqual(SemanticBindings(), StringComparer.Ordinal))
            throw new ExtractionException("Serialized calldata-load semantic bindings changed.");
    }

    internal static byte[] Serialize<T>(T value) => Utf8WithoutBom.GetBytes(
        JsonSerializer.Serialize(value, JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");

    private static Dictionary<string, SourceFile> LoadSources(string root)
    {
        Dictionary<string, SourceFile> result = new(StringComparer.Ordinal);
        foreach ((string path, string expectedHash) in ExpectedSourceSha256)
        {
            string fullPath = ResolveExactPath(root, path);
            byte[] bytes = File.ReadAllBytes(fullPath);
            string hash = Hash(bytes);
            if (hash != expectedHash)
                throw new ExtractionException($"Unadmitted complete source content for {path}; found SHA-256 {hash}.");
            CSharpParseOptions options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp14);
            CompilationUnitSyntax syntax = CSharpSyntaxTree.ParseText(Encoding.UTF8.GetString(bytes), options, fullPath).GetCompilationUnitRoot();
            Diagnostic? error = syntax.SyntaxTree.GetDiagnostics().FirstOrDefault(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            if (error is not null)
                throw new ExtractionException($"Invalid C# source {path}: {error}.");
            result.Add(path, new(path, fullPath, bytes, hash, syntax));
        }
        return result;
    }

    private static RawSourceIdentity LoadBuildSelector(string root)
    {
        string fullPath = ResolveExactPath(root, BuildTargetsPath);
        byte[] bytes = File.ReadAllBytes(fullPath);
        string hash = Hash(bytes);
        if (hash != BuildTargetsSha256)
            throw new ExtractionException($"Unadmitted standard build selector; found SHA-256 {hash}.");
        string source = Encoding.UTF8.GetString(bytes);
        Require(source, "<Compile Remove=\"**/*.std.cs\" />", "zkEVM exclusion of standard sources");
        Require(source, "<Compile Remove=\"**/*.zkevm.cs\" />", "standard exclusion of zkEVM sources");
        return new(BuildTargetsPath, hash);
    }

    private static RawSourceIdentity LoadBuildAliases(string root)
    {
        string fullPath = ResolveExactPath(root, BuildPropsPath);
        byte[] bytes = File.ReadAllBytes(fullPath);
        string hash = Hash(bytes);
        if (hash != BuildPropsSha256)
            throw new ExtractionException($"Unadmitted standard build aliases; found SHA-256 {hash}.");
        string source = Encoding.UTF8.GetString(bytes);
        Require(source, "<Using Include=\"System.Runtime.Intrinsics.Vector256&lt;byte&gt;\" Alias=\"EvmWord\" />",
            "standard EVM word representation alias");
        return new(BuildPropsPath, hash);
    }

    private static List<AdmissionIdentity> AdmitSyntax(IReadOnlyDictionary<string, SourceFile> sources)
    {
        List<AdmissionIdentity> result = new(Admissions.Length);
        foreach (AdmissionSpec spec in Admissions)
        {
            MemberDeclarationSyntax member = FindAdmissionNode(sources[spec.SourcePath].Root, spec);
            result.Add(new(spec.SourcePath, spec.Namespace, spec.OwnerPath, spec.MemberKind, spec.MemberName,
                spec.MemberGenericArity, spec.ParameterTypes, member.Kind().ToString(), Hash(CompleteCanonical(member))));
        }
        if (result.Select(AdmissionKey).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw new ExtractionException("Duplicate exact syntax admission.");
        return result;
    }

    private static void ValidateProductionClosure(string root, IReadOnlyDictionary<string, SourceFile> sources)
    {
        string handlers = Canonical(sources[OpcodeHandlersPath].Root);
        string dispatch = Canonical(sources[DispatchPath].Root);
        string storage = Canonical(sources[StoragePath].Root);
        string stack = Canonical(sources[StackPath].Root);
        string gasInterface = Canonical(sources[GasInterfacePath].Root);
        string gas = Canonical(sources[GasPath].Root);
        string virtualMachineInterface = Canonical(sources[VirtualMachineInterfacePath].Root);
        string vm = Canonical(sources[VmPath].Root);
        string vmState = Canonical(sources[VmStatePath].Root);
        string environmentState = Canonical(sources["src/Nethermind/Nethermind.Evm/ExecutionEnvironment.cs"].Root);
        string stackPool = Canonical(sources["src/Nethermind/Nethermind.Evm/StackPool.std.cs"].Root);
        string traceStack = Canonical(sources[TraceStackPath].Root);
        string blockProcessingModule = Canonical(sources[BlockProcessingModulePath].Root);

        RequireExactlyOnce(handlers,
            "Instruction.CALLDATALOAD]=OpcodeHandler<CallDataLoadOpcode<TTracingInst>,TTracingInst,TCancelable>()",
            "unconditional standard CALLDATALOAD dispatch assignment");
        RequireOrdered(handlers,
            ["CallDataLoadOpcode<TTracingInst>", "HasCheckedBody=>true", "UsesVm=>true", "StackInputs=>1",
                "UpdateGas<GasPolicy.VeryLowGasCost>", "CallDataLoadCore<TGasPolicy,TTracingInst>"],
            "checked CALLDATALOAD handler contract");
        Require(handlers, "staticvirtualboolEndsInstructionTrace=>false", "dispatch-owned instruction trace closure");
        Require(handlers, "staticvirtualintStackGrowth=>0", "in-place checked-stack growth");
        Require(handlers, "staticvirtualintPushSize=>-1", "absence of terminal PUSH elision");
        Require(dispatch, "StartInstructionTrace(instruction,TGasPolicy.GetRemainingGas(ingas),(int)pc,instack)", "pre-increment instruction trace");
        RequireOrdered(dispatch,
            ["TTracingInst.IsActive", "TCancelable.IsActive?refTracedCancelable:refTraced",
                "TCancelable.IsActive?refNoTraceCancelable:refNoTrace", "GenerateOpcodeHandlers<TTracingInst,TCancelable>"],
            "four tracing and cancellation dispatch-table roots");
        RequireOrdered(dispatch,
            ["StartInstructionTrace", "pc++", "opCodeCount++", "TOpcode.TryConsumeGas", "stack.EnsureDepth", "TOpcode.Execute"],
            "trace, PC, counter, gas, depth, and body order");
        Require(dispatch, "if(TTracingInst.IsActive&&!TOpcode.EndsInstructionTrace)", "dispatch trace closure ownership gate");
        RequireOrdered(dispatch,
            ["if(TCancelable.IsActive&&(opCodeCount&CancellationCheckMask)==0)", "gotoExit", "FinalProgramCounter=pc"],
            "post-opcode cancellation boundary unwind");
        RequireOrdered(storage,
            ["CallDataLoadCore", "PeekBytesByRefUnchecked", "ReadMemoryPositionFromSlot", "InputData.Span", "result.u0",
                "!result.IsUint64||offset>=(uint)inputData.Length", "InitBlockUnaligned", "ReportStackPush(Bytes.ZeroByteSpan)",
                "available", "copiedLength", "WriteRightPaddedBytes"],
            "CALLDATALOAD offset, zero, slice, and trace order");
        Require(stack, "publicconstintMaxStackSize=1025", "1024-value stack limit plus empty-head sentinel");
        RequireOrdered(stack, ["ReadMemoryPositionFromSlot", "unreachable=", "Bswap64", "newUInt256(addressable,0,0,unreachable)"],
            "UInt256 offset low/high preservation");
        RequireOrdered(stack,
            ["WriteRightPaddedBytes", "length!=WordSize", "PushBytesPartialZeroPadded", "ReportPushWord"],
            "full or partial 32-byte top replacement");
        RequireOrdered(stack,
            ["PushBytesPartialZeroPadded", "length>>3", "length&7", "PackLoU64", "Vector256.Create", "ReportPushWord"],
            "partial right-zero padding and raw-word trace");
        RequireOrdered(stack,
            ["PushBytesRef()", "headOffset=Head", "newOffset=headOffset+1", "Head=newOffset",
                "Unsafe.Add(ref_stack,headOffset*WordSize)"],
            "bottom-to-top physical stack word storage");
        Require(stack, "EnsureDepth(intdepth)=>Head>=depth", "non-mutating checked stack depth");
        Require(gasInterface, "UpdateGas<TCost>", "generic fixed-gas helper");
        Require(gasInterface, "TCost.GasCost", "fixed gas-cost selection");
        Require(gas, "gas.Value=0;returnfalse", "Ethereum gas exhaustion behavior");
        Require(vmState, "publicExecutionEnvironmentEnv", "frame environment provider");
        Require(environmentState, "publicReadOnlyMemory<byte>InputData", "CALLDATALOAD calldata provider");
        Require(stackPool, "GC.AllocateUninitializedArray<byte>", "standard unsafe-stack allocation provider");
        RequireOrdered(vmState,
            ["MemoryStacks(intcount)", "AsAligned32Memory(dataStack,size:count*EvmStack.WordSize)"],
            "active physical stack prefix exposed to tracing");
        RequireOrdered(vm,
            ["StartInstructionTrace", "SetOperationStack(newTraceStack(vmState.MemoryStacks((int)stackValue.Head)))"],
            "instruction-start stack trace construction");
        RequireOrdered(traceStack,
            ["ToRawBytes()", "_stack.Length==0", "newbyte[_stack.Length]", "_stack.Span.CopyTo(raw)", "returnraw"],
            "bottom-first raw stack trace copy");
        RequireOrdered(vm,
            ["publicsealedclassEthereumVirtualMachine", ":VirtualMachine<EthereumGasPolicy>", ",IVirtualMachine"],
            "standard Ethereum VM gas-policy specialization");
        Require(virtualMachineInterface, "publicinterfaceIVirtualMachine:IVirtualMachine<EthereumGasPolicy>",
            "non-generic standard Ethereum VM interface specialization");
        RequireExactlyOnce(blockProcessingModule, ".AddScoped<IVirtualMachine,EthereumVirtualMachine>()",
            "standard scoped Ethereum VM registration");
        RequireOrdered(vm, ["ReturnFailure", "exceptionType==EvmExceptionType.OutOfGas", "ClearExecutionGas", "GetFailureReturn"],
            "outer OOG clearing before failure trace");
        RequireOrdered(vm, ["GetFailureReturn", "EndInstructionTraceError", "ReportOperationRemainingGas", "ReportOperationError"],
            "outer failure trace closure");
        Require(vm, "state.ProgramCounter=(int)programCounter", "success frame-PC commit");
        Require(Canonical(sources[AmsterdamPath].Root), "NamedReleaseSpec<Amsterdam>(BPO2.Instance)", "Amsterdam fork ancestry");
        ValidateNoCompetingStandardDeclarations(root);
    }

    private static void ValidateNoCompetingStandardDeclarations(string root)
    {
        string evmRoot = ResolveExactPath(root, "src/Nethermind/Nethermind.Evm");
        string[] handlerNames = ["CallDataLoadOpcode"];
        Dictionary<string, int> declarations = handlerNames.ToDictionary(static name => name, static _ => 0, StringComparer.Ordinal);
        string[] instructionMethods = ["CallDataLoadCore"];
        Dictionary<string, int> methods = instructionMethods.ToDictionary(static name => name, static _ => 0, StringComparer.Ordinal);
        string[] opcodeAssignments = ["CALLDATALOAD"];
        Dictionary<string, int> assignments = opcodeAssignments.ToDictionary(static name => name, static _ => 0, StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(evmRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(static path => !path.EndsWith(".zkevm.cs", StringComparison.Ordinal)))
        {
            CompilationUnitSyntax rootSyntax = CSharpSyntaxTree.ParseText(File.ReadAllText(file),
                CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp14)).GetCompilationUnitRoot();
            foreach (TypeDeclarationSyntax type in rootSyntax.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (declarations.ContainsKey(type.Identifier.ValueText)) declarations[type.Identifier.ValueText]++;
            }
            foreach (MethodDeclarationSyntax method in rootSyntax.DescendantNodes().OfType<MethodDeclarationSyntax>())
                if (methods.ContainsKey(method.Identifier.ValueText)) methods[method.Identifier.ValueText]++;
            string canonical = Canonical(rootSyntax);
            foreach (string opcode in opcodeAssignments)
            {
                string marker = $"Instruction.{opcode}]=";
                int position = 0;
                while ((position = canonical.IndexOf(marker, position, StringComparison.Ordinal)) >= 0)
                {
                    assignments[opcode]++;
                    position += marker.Length;
                }
            }
        }
        foreach ((string name, int count) in declarations)
            if (count != 1) throw new ExtractionException($"Expected one standard-build declaration of {name}; found {count}.");
        foreach ((string name, int count) in methods)
            if (count != 1) throw new ExtractionException($"Expected one standard-build declaration of {name}; found {count}.");
        foreach ((string name, int count) in assignments)
            if (count != 1) throw new ExtractionException($"Expected one standard-build dispatch assignment for {name}; found {count}.");
    }

    private static IrDocument ExpectedIr()
    {
        DispatchTableBinding[] tables =
        [
            new("NoTrace", "OffFlag", "OffFlag"),
            new("NoTraceCancelable", "OffFlag", "OnFlag"),
            new("Traced", "OnFlag", "OffFlag"),
            new("TracedCancelable", "OnFlag", "OnFlag"),
        ];
        OpcodeDescriptor[] opcodes =
        [
            new("calldataload", "CALLDATALOAD", 0x35, "CallDataLoadOpcode<TTracingInst>",
                "EvmInstructions.CallDataLoadCore<TGasPolicy,TTracingInst>", 1, 1, 0, -1, true, true, true, false,
                "unconditional since Frontier",
                new("loadCallDataWord", "veryLow", 32, "uint64OrZero", "rightZero", "bottomFirst",
                    "one zero byte for inaccessible offsets; raw 32-byte word for in-range offsets"),
                ["traceStart", "traceStackBottomFirst", "advanceDispatchPc", "incrementOpcodeCount", "chargeVeryLow",
                    "checkDepthOne", "peekTopSlot", "decodeUInt256Offset", "rejectAboveUInt64OrAtEnd",
                    "zeroTopAndTraceZeroByte", "otherwiseCopyAtMost32", "rightZeroPadAndReplaceTop",
                    "traceRawWord", "closeSuccessTrace", "tailDispatchOrCancelableBoundary"]),
        ];
        List<OpcodeSpecialization> roots = new(4);
        foreach (OpcodeDescriptor opcode in opcodes)
            foreach (DispatchTableBinding table in tables)
            {
                string body = opcode.HandlerBody.Replace("TTracingInst", table.TracingFlag, StringComparison.Ordinal)
                    .Replace("TGasPolicy", "EthereumGasPolicy", StringComparison.Ordinal);
                roots.Add(new(opcode.Name, table.Name, table.TracingFlag, table.CancelableFlag,
                    $"Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<{body},{table.TracingFlag},{table.CancelableFlag},OnFlag>"));
            }
        return new(
            1,
            ExtractorVersion,
            KernelName,
            "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>",
            "Nethermind.Evm.GasPolicy.EthereumGasPolicy",
            "Nethermind.Specs.Forks.Amsterdam",
            "Directory.Build.targets: EnableZkEvm != true selects *.std.cs and excludes *.zkevm.cs",
            new(3, 32, int.MaxValue, ulong.MaxValue,
                "115792089237316195423570985008687907853269984665640564039457584007913129639936"),
            new(true, true, true, true),
            tables,
            opcodes,
            [.. roots],
            ForkLineage,
            [
                "Roslyn exact-syntax admission does not prove C# compilation, CLR/JIT/AOT execution, function-pointer tail calls, or hardware behavior.",
                "The model assumes the external UInt256 IsUint64/u0 contract matches the shared Lean word modulo 2^256.",
                "The model assumes the admitted unsafe EvmWord stack slot, native endian conversion, unaligned reads/writes, vector lanes, and Span aliasing implement the modeled big-endian 32-byte word without corruption.",
                "The instruction-start stack event reverses the model's top-first words to match the admitted physical bottom-first VmState.MemoryStacks and TraceStack.ToRawBytes copy; per-word byte decoding remains under the EvmWord representation premise.",
                "Tracing callbacks are assumed total and non-throwing; capability-dependent memory, bottom-first stack, and return-data event order and modeled payloads are explicit, while the StartOperation environment object is abstracted.",
                "Cancellation is modeled only at the post-opcode boundary; token state, scheduling, CLR/JIT/AOT, function-pointer tail calls, and hardware behavior remain open.",
                "The outer closure covers OOG gas clearing, charged underflow residue, finish-before-error, and retention of the frame PC on fault; transaction rollback, persistence, and state-gas settlement are outside this slice.",
            ]);
    }

    private static string[] SemanticBindings() =>
    [
        "CALLDATALOAD and 4 tracing/cancellation tables yield exactly 4 closed EthereumGasPolicy roots",
        "EthereumVirtualMachine specializes VirtualMachine<EthereumGasPolicy>, non-generic IVirtualMachine specializes IVirtualMachine<EthereumGasPolicy>, and BlockProcessingModule.Load registers the implementation as scoped IVirtualMachine in the admitted standard build",
        "instruction trace starts before local PC and opcode-count increments; VeryLow gas precedes depth validation",
        "instruction-start stack trace payload is bottom-first, reversing the model's top-first stack words to match the physical TraceStack copy",
        "the checked body peeks and replaces the existing top slot, preserving stack height",
        "StackGrowth zero and PushSize minus one preserve normal success-trace and tail-dispatch behavior",
        "offsets above UInt64 or at or beyond calldata end yield zero; in-range reads copy at most 32 bytes and right-zero-pad",
        "the production domain bounds calldata length by the admitted ReadOnlyMemory length limit of Int32.MaxValue",
        "inaccessible offsets trace one zero byte while in-range reads trace the raw 32-byte result word",
        "successful dispatch reports post-charge remaining gas before tail dispatch or cancellation boundary",
        "outer OOG closure clears execution gas; underflow retains the three-gas charge; both finish trace before error and retain the original frame PC",
        "Amsterdam inherits the unconditional CALLDATALOAD assignment through the pinned standard-build fork lineage",
    ];

    private static AdmissionSpec[] BuildAdmissionSpecs()
    {
        List<AdmissionSpec> result =
        [
            A("src/Nethermind/Nethermind.Core/GasCostOf.cs", "Nethermind.Core", "namespace", "type", "GasCostOf"),
            A(BytesPath, "Nethermind.Core.Extensions", "namespace", "type", "Bytes"),
            A(BytesStandardPath, "Nethermind.Core.Extensions", "namespace", "type", "Bytes"),
            A(ReleaseSpecPath, "Nethermind.Core.Specs", "namespace", "type", "IReleaseSpec"),
            A("src/Nethermind/Nethermind.Core/TypeFlags.cs", "Nethermind.Core", "namespace", "type", "IFlag"),
            A("src/Nethermind/Nethermind.Core/TypeFlags.cs", "Nethermind.Core", "namespace", "type", "OffFlag"),
            A("src/Nethermind/Nethermind.Core/TypeFlags.cs", "Nethermind.Core", "namespace", "type", "OnFlag"),
            A("src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs", "Nethermind.Evm", "namespace", "type", "DispatchFlags"),
            A("src/Nethermind/Nethermind.Evm/Instruction.cs", "Nethermind.Evm", "namespace", "type", "Instruction"),
            A(StackPath, "Nethermind.Evm", "namespace", "type", "EvmStack"),
            A(StackStandardPath, "Nethermind.Evm", "namespace", "type", "EvmStack"),
            A(StackPath, "Nethermind.Evm", "EvmStack`0", "method", "PushBytesRef"),
            A(StackPath, "Nethermind.Evm", "EvmStack`0", "method", "PeekBytesByRefUnchecked"),
            A(StackPath, "Nethermind.Evm", "EvmStack`0", "method", "ReadMemoryPositionFromSlot", 0, "byte,UInt256"),
            A(StackPath, "Nethermind.Evm", "EvmStack`0", "method", "WriteRightPaddedBytes", 1, "byte,byte,uint"),
            A(StackPath, "Nethermind.Evm", "EvmStack`0", "method", "PushBytesPartialZeroPadded", 1, "byte,byte,nuint"),
            A(StackPath, "Nethermind.Evm", "EvmStack`0", "method", "PackLoU64", 0, "byte,nuint"),
            A(StackPath, "Nethermind.Evm", "EvmStack`0", "method", "ReportPushWord", 0, "byte"),
            A(StackPath, "Nethermind.Evm", "EvmStack`0", "method", "EnsureDepth", 0, "int"),
            A(MemoryPath, "Nethermind.Evm", "namespace", "type", "EvmPooledMemory"),
            A(VmStatePath, "Nethermind.Evm", "namespace", "type", "VmState", 1),
            A(VmStatePath, "Nethermind.Evm", "VmState`1", "method", "MemoryStacks", 0, "int"),
            A("src/Nethermind/Nethermind.Evm/ExecutionEnvironment.cs", "Nethermind.Evm", "namespace", "type", "ExecutionEnvironment"),
            A("src/Nethermind/Nethermind.Evm/EvmFrameMemory.cs", "Nethermind.Evm", "namespace", "type", "EvmFrameMemory"),
            A("src/Nethermind/Nethermind.Evm/StackPool.cs", "Nethermind.Evm", "namespace", "type", "StackPool"),
            A("src/Nethermind/Nethermind.Evm/StackPool.std.cs", "Nethermind.Evm", "namespace", "type", "StackPool"),
            A("src/Nethermind/Nethermind.Evm/GasPolicy/IGasCost.cs", "Nethermind.Evm.GasPolicy", "namespace", "type", "IGasCost"),
            A("src/Nethermind/Nethermind.Evm/GasPolicy/IGasCost.cs", "Nethermind.Evm.GasPolicy", "namespace", "type", "VeryLowGasCost"),
            A(GasInterfacePath, "Nethermind.Evm.GasPolicy", "namespace", "type", "IGasPolicy", 1),
            A(GasPath, "Nethermind.Evm.GasPolicy", "namespace", "type", "EthereumGasPolicy"),
            A(GasPath, "Nethermind.Evm.GasPolicy", "EthereumGasPolicy`0", "method", "UpdateGas", 0, "EthereumGasPolicy,ulong"),
            A(GasPath, "Nethermind.Evm.GasPolicy", "EthereumGasPolicy`0", "method", "ClearExecutionGas", 0, "EthereumGasPolicy"),
            A(GasPath, "Nethermind.Evm.GasPolicy", "EthereumGasPolicy`0", "method", "GetRemainingGas", 0, "EthereumGasPolicy"),
            A(VirtualMachineInterfacePath, "Nethermind.Evm", "namespace", "type", "IVirtualMachine", 1),
            A(VirtualMachineInterfacePath, "Nethermind.Evm", "namespace", "type", "IVirtualMachine"),
            A(StoragePath, "Nethermind.Evm", "namespace", "type", "EvmInstructions"),
            A(StoragePath, "Nethermind.Evm", "EvmInstructions`0", "method", "CallDataLoadCore", 2, "EvmStack,VirtualMachine<TGasPolicy>"),
            A("src/Nethermind/Nethermind.Evm/Tracing/ITxTracer.cs", "Nethermind.Evm.Tracing", "namespace", "type", "ITxTracer"),
            A("src/Nethermind/Nethermind.Evm/Tracing/ITxTracer.cs", "Nethermind.Evm.Tracing", "ITxTracer`0", "method", "ReportStackPush", 0, "ReadOnlySpan<byte>"),
            A("src/Nethermind/Nethermind.Evm/Tracing/ITxTracer.cs", "Nethermind.Evm.Tracing", "ITxTracer`0", "method", "ReportStackPush", 0, "byte"),
            A("src/Nethermind/Nethermind.Evm/Tracing/ITxTracer.cs", "Nethermind.Evm.Tracing", "ITxTracer`0", "method", "SetOperationStack", 0, "TraceStack"),
            A(TraceStackPath, "Nethermind.Evm.Tracing", "namespace", "type", "TraceStack"),
            A(TraceStackPath, "Nethermind.Evm.Tracing", "TraceStack`0", "method", "ToRawBytes"),
            A("src/Nethermind/Nethermind.Evm/Tracing/TraceMemory.cs", "Nethermind.Evm.Tracing", "namespace", "type", "TraceMemory"),
            A(DispatchPath, "Nethermind.Evm", "namespace", "type", "VirtualMachine", 1),
            A(DispatchPath, "Nethermind.Evm", "VirtualMachine`1", "method", "GetOpcodeHandlers", 2),
            A(DispatchPath, "Nethermind.Evm", "VirtualMachine`1", "method", "PrepareOpcodes", 1),
            A(DispatchPath, "Nethermind.Evm", "VirtualMachine`1", "method", "PrepareOpcodes", 2),
            A(DispatchPath, "Nethermind.Evm", "VirtualMachine`1", "method", "RunDispatchLoop", 2, "EvmStack,TGasPolicy,nint"),
            A(DispatchPath, "Nethermind.Evm", "VirtualMachine`1", "method", "ExecuteOpcode", 4, "EvmStack,TGasPolicy,DispatchState,nint,int"),
            A(DispatchPath, "Nethermind.Evm", "VirtualMachine`1", "method", "ExitCheckedOpcode", 0, "DispatchState,nint,int,EvmExceptionType"),
            A(DispatchPath, "Nethermind.Evm", "VirtualMachine`1", "type", "OpcodeTable"),
            A(DispatchPath, "Nethermind.Evm", "VirtualMachine`1.OpcodeTable`0", "method", "GetHandlers", 2, "IReleaseSpec"),
            A(OpcodeHandlersPath, "Nethermind.Evm", "namespace", "type", "VirtualMachine", 1),
            A(OpcodeHandlersPath, "Nethermind.Evm", "VirtualMachine`1", "method", "OpcodeHandler", 3),
            A(OpcodeHandlersPath, "Nethermind.Evm", "VirtualMachine`1", "method", "GenerateOpcodeHandlers", 2, "IReleaseSpec"),
            A(OpcodeHandlersPath, "Nethermind.Evm", "VirtualMachine`1", "type", "IOpcodeBody"),
            A(OpcodeHandlersPath, "Nethermind.Evm", "VirtualMachine`1", "type", "CallDataLoadOpcode", 1),
            A(VmPath, "Nethermind.Evm", "namespace", "type", "EthereumVirtualMachine"),
            A(VmPath, "Nethermind.Evm", "namespace", "type", "VirtualMachine", 1),
            A(VmPath, "Nethermind.Evm", "VirtualMachine`1", "method", "RunByteCode", 2, "EvmStack,TGasPolicy"),
            A(VmPath, "Nethermind.Evm", "VirtualMachine`1", "method", "GetFailureReturn", 0, "ulong,EvmExceptionType"),
            A(VmPath, "Nethermind.Evm", "VirtualMachine`1", "method", "StartInstructionTrace", 0, "Instruction,ulong,int,EvmStack"),
            A(VmPath, "Nethermind.Evm", "VirtualMachine`1", "method", "EndInstructionTrace", 0, "ulong"),
            A(VmPath, "Nethermind.Evm", "VirtualMachine`1", "method", "EndInstructionTraceError", 0, "ulong,EvmExceptionType"),
            A(VmStandardPath, "Nethermind.Evm", "namespace", "type", "VirtualMachine", 1),
            A(BlockProcessingModulePath, "Nethermind.Init.Modules", "namespace", "type", "BlockProcessingModule"),
            A(BlockProcessingModulePath, "Nethermind.Init.Modules", "BlockProcessingModule`0", "method", "Load", 0, "ContainerBuilder"),
            A("src/Nethermind/Nethermind.Specs/Forks/NamedReleaseSpec.cs", "Nethermind.Specs.Forks", "namespace", "type", "NamedReleaseSpec"),
            A("src/Nethermind/Nethermind.Specs/Forks/NamedReleaseSpec.cs", "Nethermind.Specs.Forks", "namespace", "type", "NamedReleaseSpec", 1),
        ];
        string[] files = ["00_Olympic", "01_Frontier", "02_Homestead", "03_Dao", "04_TangerineWhistle", "05_SpuriousDragon", "06_Byzantium", "07_Constantinople", "08_ConstantinopleFix", "09_Istanbul", "10_MuirGlacier", "11_Berlin", "12_London", "13_ArrowGlacier", "14_GrayGlacier", "15_Paris", "16_Shanghai", "17_Cancun", "18_Prague", "19_Osaka", "20_BPO1", "21_BPO2", "25_Amsterdam"];
        for (int index = 0; index < files.Length; index++)
            result.Add(A($"src/Nethermind/Nethermind.Specs/Forks/{files[index]}.cs", "Nethermind.Specs.Forks", "namespace", "type", ForkLineage[index]));
        return [.. result];
    }

    private static MemberDeclarationSyntax FindAdmissionNode(CompilationUnitSyntax root, AdmissionSpec spec)
    {
        BaseNamespaceDeclarationSyntax[] namespaces = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>()
            .Where(ns => ns.Name.ToString() == spec.Namespace).ToArray();
        if (namespaces.Length != 1)
            throw new ExtractionException($"Expected exact namespace {spec.Namespace} in {spec.SourcePath}; found {namespaces.Length}.");
        SyntaxNode container = ResolveOwner(namespaces[0], spec.OwnerPath, spec.SourcePath);
        MemberDeclarationSyntax[] matches = DirectMembers(container).Where(member => Matches(member, spec)).ToArray();
        if (matches.Length != 1)
            throw new ExtractionException($"Expected exactly one {AdmissionKey(spec)}; found {matches.Length}.");
        return matches[0];
    }

    private static SyntaxNode ResolveOwner(BaseNamespaceDeclarationSyntax ns, string ownerPath, string sourcePath)
    {
        if (ownerPath == "namespace") return ns;
        SyntaxNode current = ns;
        foreach (string segment in ownerPath.Split('.'))
        {
            int separator = segment.LastIndexOf('`');
            string name = separator < 0 ? segment : segment[..separator];
            int arity = separator < 0 ? 0 : int.Parse(segment[(separator + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            TypeDeclarationSyntax[] matches = DirectMembers(current).OfType<TypeDeclarationSyntax>()
                .Where(type => type.Identifier.ValueText == name && GenericArity(type) == arity).ToArray();
            if (matches.Length != 1)
                throw new ExtractionException($"Expected exact owner {ownerPath} in {sourcePath}; segment {segment} matched {matches.Length} declarations.");
            current = matches[0];
        }
        return current;
    }

    private static bool Matches(MemberDeclarationSyntax member, AdmissionSpec spec) => spec.MemberKind switch
    {
        "type" when member is BaseTypeDeclarationSyntax type =>
            type.Identifier.ValueText == spec.MemberName && GenericArity(type) == spec.MemberGenericArity,
        "method" when member is MethodDeclarationSyntax method =>
            method.Identifier.ValueText == spec.MemberName &&
            (method.TypeParameterList?.Parameters.Count ?? 0) == spec.MemberGenericArity &&
            ParameterTypes(method) == spec.ParameterTypes,
        _ => false,
    };

    private static IEnumerable<MemberDeclarationSyntax> DirectMembers(SyntaxNode container) => container switch
    {
        BaseNamespaceDeclarationSyntax ns => ns.Members,
        TypeDeclarationSyntax type => type.Members,
        _ => throw new ExtractionException($"Unsupported exact-admission owner {container.Kind()}.")
    };

    private static int GenericArity(BaseTypeDeclarationSyntax type) => type is TypeDeclarationSyntax declaration
        ? declaration.TypeParameterList?.Parameters.Count ?? 0
        : 0;

    private static string ParameterTypes(BaseMethodDeclarationSyntax method) => string.Join(",",
        method.ParameterList.Parameters.Select(static parameter => Canonical(parameter.Type!)));

    private static AdmissionSpec A(string path, string ns, string owner, string kind, string name,
        int arity = 0, string parameters = "") => new(path, ns, owner, kind, name, arity, parameters);

    private static string AdmissionKey(AdmissionSpec spec) =>
        $"{spec.SourcePath}:{spec.Namespace}:{spec.OwnerPath}:{spec.MemberKind}:{spec.MemberName}/{spec.MemberGenericArity}({spec.ParameterTypes})";

    private static string AdmissionKey(AdmissionIdentity identity) =>
        $"{identity.SourcePath}:{identity.Namespace}:{identity.OwnerPath}:{identity.MemberKind}:{identity.MemberName}/{identity.MemberGenericArity}({identity.ParameterTypes})";

    private static string Canonical(SyntaxNode node) => string.Concat(
        node.DescendantTokens(descendIntoTrivia: false).Select(static token => token.Text));

    private static byte[] CompleteCanonical(SyntaxNode node)
    {
        StringBuilder result = new();
        foreach (SyntaxToken token in node.DescendantTokens(descendIntoTrivia: false))
            result.Append('T').Append(token.RawKind).Append(':').Append(token.Text).Append('\0');
        return Encoding.UTF8.GetBytes(result.ToString());
    }

    private static string ResolveExactPath(string root, string relativePath)
    {
        string current = root;
        foreach (string segment in relativePath.Split('/'))
        {
            string[] matches = Directory.EnumerateFileSystemEntries(current)
                .Where(path => Path.GetFileName(path).Equals(segment, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
                throw new ExtractionException($"Required case-sensitive path '{relativePath}' is missing or ambiguous at '{segment}'.");
            current = matches[0];
        }
        return current;
    }

    private static void RejectDuplicateProperties(byte[] bytes, string description)
    {
        try
        {
            Utf8JsonReader reader = new(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
            Stack<HashSet<string>?> containers = new();
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        containers.Push(new(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.StartArray:
                        containers.Push(null);
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        if (containers.Count == 0) throw new ExtractionException($"Serialized {description} has malformed nesting.");
                        containers.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        if (containers.Count == 0 || containers.Peek() is not HashSet<string> properties || !properties.Add(reader.GetString()!))
                            throw new ExtractionException($"Serialized {description} contains a duplicate property '{reader.GetString()}'.");
                        break;
                }
            }
            if (containers.Count != 0) throw new ExtractionException($"Serialized {description} has malformed nesting.");
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Serialized {description} is not valid JSON: {exception.Message}");
        }
    }

    private static void Require(string source, string fragment, string description)
    {
        if (!source.Contains(fragment, StringComparison.Ordinal))
            throw new ExtractionException($"Rejected {description}; expected '{fragment}'.");
    }

    private static void RequireExactlyOnce(string source, string fragment, string description)
    {
        int first = source.IndexOf(fragment, StringComparison.Ordinal);
        if (first < 0 || source.IndexOf(fragment, first + fragment.Length, StringComparison.Ordinal) >= 0)
            throw new ExtractionException($"Rejected {description}; expected exactly one '{fragment}'.");
    }

    private static void RequireOrdered(string source, IReadOnlyList<string> fragments, string description)
    {
        int position = 0;
        foreach (string fragment in fragments)
        {
            int found = source.IndexOf(fragment, position, StringComparison.Ordinal);
            if (found < 0) throw new ExtractionException($"Rejected {description}; missing ordered fragment '{fragment}'.");
            position = found + fragment.Length;
        }
    }

    private static string CombinedSourceHash(IEnumerable<SourceIdentity> sources, IEnumerable<RawSourceIdentity> raw,
        IEnumerable<AdmissionIdentity> admissions) => Hash(Encoding.UTF8.GetBytes(string.Join("\n",
        sources.Select(static item => $"{item.Path}\0{item.Sha256}\0{item.RoslynSyntaxSha256}\0csharp14")
            .Concat(raw.Select(static item => $"{item.Path}\0{item.Sha256}\0raw"))
            .Concat(admissions.Select(static item =>
                $"{AdmissionKey(item)}\0{item.SyntaxKind}\0{item.Sha256}\0admission")))));

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void ValidateSha256(string? value, string description) =>
        CallDataLoadOpcodeLeanEmitter.ValidateSha256(value, description);

    private static void WriteDeterministic(string path, byte[] bytes)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return;
        File.WriteAllBytes(path, bytes);
    }
}
