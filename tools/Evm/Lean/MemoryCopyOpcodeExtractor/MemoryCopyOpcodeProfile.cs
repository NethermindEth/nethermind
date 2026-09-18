// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.MemoryCopyOpcodeExtractor;

internal static class MemoryCopyOpcodeProfile
{
    internal const string IrFileName = "MemoryCopyOpcodeKernel.ir.json";
    internal const string ManifestFileName = "MemoryCopyOpcodeKernel.source-manifest.json";
    internal const string LeanFileName = "MemoryCopyOpcodeKernel.lean";

    private const string ExtractorVersion = "1.0.0";
    private const string KernelName = "standard-mainnet-amsterdam-memory-copy-gas";
    private const string BuildPropsPath = "src/Nethermind/Directory.Build.props";
    private const string BuildPropsSha256 = "ad544425af1b0ec60334511abf79896a77843088457ef81b98a06b56d13e4e3c";
    private const string BuildTargetsPath = "src/Nethermind/Directory.Build.targets";
    private const string BuildTargetsSha256 = "0598cebaef1df41102a18a3f9ace810bed1e4e64b8471d055724b4a394dccec6";
    private const string OperationalSpecPath = "tools/Evm/Lean/MemoryCopyOpcodeExtractor/Specification/MemoryCopyExecution.lean";
    private const string OperationalSpecSha256 = "532eb5b98f5a9336bc252995323c7b92f8a5c525834ebecb28464b213b629354";
    private const string ExpectedCombinedSourceSha256 = "00c04ea98800c9f23885c9ec8ab74640d71e3831c5777f3704526408b94bddf0";
    private const string OpcodeHandlersPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs";
    private const string DispatchPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs";
    private const string StoragePath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Storage.cs";
    private const string CopyPath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.CodeCopy.cs";
    private const string EnvironmentPath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Environment.cs";
    private const string StackPath = "src/Nethermind/Nethermind.Evm/EvmStack.cs";
    private const string StackStandardPath = "src/Nethermind/Nethermind.Evm/EvmStack.std.cs";
    private const string BytesPath = "src/Nethermind/Nethermind.Core/Extensions/Bytes.cs";
    private const string BytesStandardPath = "src/Nethermind/Nethermind.Core/Extensions/Bytes.std.cs";
    private const string EvmWordExtensionsPath = "src/Nethermind/Nethermind.Core/Extensions/EvmWordExtensions.cs";
    private const string MemoryPath = "src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs";
    private const string GasInterfacePath = "src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs";
    private const string GasPath = "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs";
    private const string VmPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.cs";
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
        [EvmWordExtensionsPath] = "0d2e127a715dc4b752fdeda2c553eb896802bf4def235f93f4e6996db3ec0e78",
        ["src/Nethermind/Nethermind.Core/TypeFlags.cs"] = "33d8404d3ec39fffbc9f665126255f9799a0ba433206a141f480f8144be95747",
        ["src/Nethermind/Nethermind.Core/Specs/IReleaseSpec.cs"] = "3f44b3da5614c94d59eae2e4a6266a9e698614f75a7b5a9894a4828b0021b18a",
        ["src/Nethermind/Nethermind.Core/Specs/IReleaseSpecExtensions.cs"] = "5a22587aa81b5f0e4276e61bc1970ff89e4e4cecbed8c9b08dd338576c7d041a",
        ["src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs"] = "99c58367e5b90fa5904484f6e2fbf10745a54f6cd7d07275810c27028ad0eb2b",
        ["src/Nethermind/Nethermind.Evm/Instruction.cs"] = "6895d06277ac4f9369d7bdc748976576bf384f6f3a5a348a2e4fd3767a46a030",
        ["src/Nethermind/Nethermind.Evm/Instructions/EvmCalculations.cs"] = "1912ef2add71990f29ab0cb7ef1de745b44f76bd201d4ac76c3032d5f60dc244",
        [StackPath] = "561520be4f0fe1d533ddea9cfea7ff48edb2745b1f3adf1b45c24e6c0bed02ad",
        [StackStandardPath] = "01a1b26ce105fa69fff94b8df171dcf11b85fe5462318a3ff9da3889ff41577e",
        [MemoryPath] = "853f8f9b46e5deef1c655442694dfbb1f862de80d0ecdbee2d66e7282f974e84",
        ["src/Nethermind/Nethermind.Evm/VmState.cs"] = "7b7b6ddb753118b426d14976a3fe8e79f808320d6a09ea9cace9df19197f9f23",
        ["src/Nethermind/Nethermind.Evm/ExecutionEnvironment.cs"] = "c076225b688b1e04ed83cddc90a2ddbda69862333dc927945eb32d376e43e1db",
        ["src/Nethermind/Nethermind.Evm/EvmFrameMemory.cs"] = "4bef331a3b36589c51e0560b3b288cacf2c85e582deb79f9c2f25284ccb56507",
        ["src/Nethermind/Nethermind.Evm/StackPool.cs"] = "4fa29adf34149fc192f9fc13140b8c8bdc3a8fa7b0f0b2aec94717670d6131c4",
        ["src/Nethermind/Nethermind.Evm/StackPool.std.cs"] = "62042951d6b5cf767cce1a1929dbf82ce26127b8884c784ab6708320f418c8f2",
        ["src/Nethermind/Nethermind.Evm/GasPolicy/IGasCost.cs"] = "16289cdc2f777a76fda3ad8ff4ce4275c30513aee04c9cf42d314cc6e1ec48c0",
        [GasInterfacePath] = "b4d26db87723243514f2f6b8a5e48d1176139105c3b012ad0f3956e59afc98a8",
        [GasPath] = "3c31ba40c24a78bf11243b38859168350f02934806124e913da0560bc1a4350f",
        [StoragePath] = "0101effa0c71c4a602021f18de7075e047974b75b37b9d672364499db0bf390d",
        [CopyPath] = "fd948f4f4ab64699c496e320019ff69d7dd58b0a33e906a5579c46240605c104",
        [EnvironmentPath] = "1174827863b40d80b974c361815a3ab5de0d37e184049c977ddf1716180d09f9",
        ["src/Nethermind/Nethermind.Evm/Tracing/ITxTracer.cs"] = "9f2550f8c3407f494452386699040feab8aa597437b1cd4180d038d9fd0a9169",
        ["src/Nethermind/Nethermind.Evm/Tracing/TracerExtensions.cs"] = "4704eb408436ecb82614bbf1db26f11fcfcfa65ef613146e37a31f7e30a8d662",
        ["src/Nethermind/Nethermind.Evm/Tracing/TraceStack.cs"] = "a2e06210112f3a47bca6f95e4b3a89433c78f4d39b5ec4cbafe485f21191acb8",
        ["src/Nethermind/Nethermind.Evm/Tracing/TraceMemory.cs"] = "0e8bda869acd599a94a8cd6a9a79a7ff457c4acf1534a0c7f4ebd39939b8fca6",
        [DispatchPath] = "4f36bb20057caec9c85bcd3621372563f4d47ba36a9cbf01be267af4e379bca1",
        [OpcodeHandlersPath] = "5148dd594e40ebb976179b1048f233914f54acc8d033d358cf3e2e7594e112b6",
        [VmPath] = "45edba3691e09185e749485785990662ddea1af849bc6e4564f92b68a5137a6b",
        ["src/Nethermind/Nethermind.Evm/VirtualMachine.std.cs"] = "357fbd35369424a379c8c29338f491627512d40a12199d7a3d9aac44c87ca92a",
        ["src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs"] = "fce65ce5bb523c56fc8aa6ee0e4d092c940a5a90b174c820ffe262eeb5241c60",
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
        byte[] leanBytes = MemoryCopyOpcodeLeanEmitter.Emit(roundTrip, irSha, sourceSha);

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
                ?? throw new ExtractionException("Serialized memory/copy opcode IR is empty.");
            ValidateIr(value);
            return value;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Serialized memory/copy opcode IR is not valid JSON: {exception.Message}");
        }
    }

    internal static SourceManifest DeserializeManifest(byte[] bytes)
    {
        RejectDuplicateProperties(bytes, "manifest");
        try
        {
            SourceManifest value = JsonSerializer.Deserialize<SourceManifest>(bytes, JsonOptions)
                ?? throw new ExtractionException("Serialized memory/copy opcode manifest is empty.");
            ValidateManifestShape(value);
            return value;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Serialized memory/copy opcode manifest is not valid JSON: {exception.Message}");
        }
    }

    internal static void ValidateIr(IrDocument value)
    {
        if (value.Schedule is null || value.Activation is null || value.OuterFailure is null || value.DispatchTables is null || value.Opcodes is null ||
            value.Specializations is null || value.ForkLineage is null || value.OpenExtractionObligations is null ||
            value.DispatchTables.Any(static item => item is null) || value.Opcodes.Any(static item => item is null || item.Semantics is null || item.EffectOrder is null) ||
            value.Specializations.Any(static item => item is null))
            throw new ExtractionException("Serialized memory/copy opcode IR contains null fields, collections, or entries.");
        if (!Serialize(value).AsSpan().SequenceEqual(Serialize(ExpectedIr())))
            throw new ExtractionException("Serialized memory/copy opcode IR is not the exact admitted profile.");
    }

    internal static void ValidateManifest(SourceManifest value, SourceManifest expected)
    {
        ValidateManifestShape(value);
        if (!Serialize(value).AsSpan().SequenceEqual(Serialize(expected)))
            throw new ExtractionException("Serialized memory/copy opcode manifest does not exactly match extracted lineage.");
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
            throw new ExtractionException("Serialized memory/copy opcode manifest contains null fields, collections, or entries.");
        if (value.SchemaVersion != 1 || value.ExtractorVersion != ExtractorVersion || value.Kernel != KernelName ||
            value.LanguageVersion != "CSharp14" || value.RoslynVersion != (typeof(CSharpSyntaxTree).Assembly.GetName().Version?.ToString() ?? "unknown"))
            throw new ExtractionException("Serialized memory/copy opcode manifest header changed.");
        if (value.Sources.Length != ExpectedSourceSha256.Count ||
            value.Sources.Select(static item => item.Path).Distinct(StringComparer.Ordinal).Count() != value.Sources.Length)
            throw new ExtractionException("Serialized memory/copy source identity set changed or collides.");
        foreach (SourceIdentity source in value.Sources)
        {
            ValidateSha256(source.Sha256, source.Path);
            ValidateSha256(source.RoslynSyntaxSha256, source.Path + " syntax");
            if (!ExpectedSourceSha256.TryGetValue(source.Path, out string? expected) || source.Sha256 != expected)
                throw new ExtractionException($"Serialized memory/copy source digest changed for {source.Path}.");
        }
        if (value.RawSources.Length != 3 ||
            value.RawSources.Select(static item => item.Path).Distinct(StringComparer.Ordinal).Count() != 3 ||
            !value.RawSources.Any(static item => item.Path == BuildPropsPath && item.Sha256 == BuildPropsSha256) ||
            !value.RawSources.Any(static item => item.Path == BuildTargetsPath && item.Sha256 == BuildTargetsSha256) ||
            !value.RawSources.Any(static item => item.Path == OperationalSpecPath && item.Sha256 == OperationalSpecSha256))
            throw new ExtractionException("Serialized memory/copy raw-source identity set changed.");
        foreach (RawSourceIdentity source in value.RawSources) ValidateSha256(source.Sha256, source.Path);
        if (value.Admissions.Length != Admissions.Length ||
            value.Admissions.Select(AdmissionKey).Distinct(StringComparer.Ordinal).Count() != value.Admissions.Length)
            throw new ExtractionException("Serialized memory/copy exact-admission set changed or collides.");
        for (int index = 0; index < Admissions.Length; index++)
        {
            AdmissionSpec expected = Admissions[index];
            AdmissionIdentity actual = value.Admissions[index];
            ValidateSha256(actual.Sha256, AdmissionKey(actual));
            if (actual.SourcePath != expected.SourcePath || actual.Namespace != expected.Namespace ||
                actual.OwnerPath != expected.OwnerPath || actual.MemberKind != expected.MemberKind ||
                actual.MemberName != expected.MemberName || actual.MemberGenericArity != expected.MemberGenericArity ||
                actual.ParameterTypes != expected.ParameterTypes || string.IsNullOrWhiteSpace(actual.SyntaxKind))
                throw new ExtractionException($"Serialized memory/copy admission qualifier changed at index {index}.");
        }
        if (value.Ir.Path != IrFileName || value.Lean.Path != LeanFileName)
            throw new ExtractionException("Serialized memory/copy artifact path changed.");
        ValidateSha256(value.Ir.Sha256, value.Ir.Path);
        ValidateSha256(value.Lean.Sha256, value.Lean.Path);
        byte[] canonicalIr = Serialize(ExpectedIr());
        string canonicalIrSha = Hash(canonicalIr);
        if (value.Ir.Sha256 != canonicalIrSha)
            throw new ExtractionException("Serialized memory/copy IR artifact digest is not canonical.");
        ValidateSha256(value.CombinedSourceSha256, "combined source");
        if (value.CombinedSourceSha256 != CombinedSourceHash(value.Sources, value.RawSources, value.Admissions))
            throw new ExtractionException("Serialized memory/copy combined source digest is inconsistent.");
        if (value.CombinedSourceSha256 != ExpectedCombinedSourceSha256)
            throw new ExtractionException(
                $"Serialized memory/copy source closure is not the admitted closure; found {value.CombinedSourceSha256}.");
        byte[] canonicalLean = MemoryCopyOpcodeLeanEmitter.Emit(
            DeserializeIr(canonicalIr), canonicalIrSha, value.CombinedSourceSha256);
        if (value.Lean.Sha256 != Hash(canonicalLean))
            throw new ExtractionException("Serialized memory/copy Lean artifact digest is not canonical.");
        if (!value.SemanticBindings.SequenceEqual(SemanticBindings(), StringComparer.Ordinal))
            throw new ExtractionException("Serialized memory/copy semantic bindings changed.");
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
        string copy = Canonical(sources[CopyPath].Root);
        string environment = Canonical(sources[EnvironmentPath].Root);
        string stack = Canonical(sources[StackPath].Root);
        string evmWordExtensions = Canonical(sources[EvmWordExtensionsPath].Root);
        string memory = Canonical(sources[MemoryPath].Root);
        string gasInterface = Canonical(sources[GasInterfacePath].Root);
        string gas = Canonical(sources[GasPath].Root);
        string vm = Canonical(sources[VmPath].Root);
        string vmState = Canonical(sources["src/Nethermind/Nethermind.Evm/VmState.cs"].Root);
        string environmentState = Canonical(sources["src/Nethermind/Nethermind.Evm/ExecutionEnvironment.cs"].Root);
        string stackPool = Canonical(sources["src/Nethermind/Nethermind.Evm/StackPool.std.cs"].Root);

        foreach (string assignment in new[]
        {
            "Instruction.CALLDATACOPY]=OpcodeHandler<CallDataCopyOpcode<TTracingInst>,TTracingInst,TCancelable>()",
            "Instruction.CODECOPY]=OpcodeHandler<CodeCopyOpcode<TTracingInst>,TTracingInst,TCancelable>()",
            "Instruction.RETURNDATACOPY]=OpcodeHandler<ReturnDataCopyOpcode<TTracingInst>,TTracingInst,TCancelable>()",
            "Instruction.MLOAD]=OpcodeHandler<MLoadOpcode<TTracingInst>,TTracingInst,TCancelable>()",
            "Instruction.MSTORE]=OpcodeHandler<MStoreOpcode<TTracingInst>,TTracingInst,TCancelable>()",
            "Instruction.MSTORE8]=OpcodeHandler<MStore8Opcode<TTracingInst>,TTracingInst,TCancelable>()",
            "Instruction.MSIZE]=OpcodeHandler<EnvUInt64Opcode<EvmInstructions.OpMSize<TGasPolicy>,TTracingInst>,TTracingInst,TCancelable>()",
            "Instruction.GAS]=OpcodeHandler<GasOpcode<TTracingInst>,TTracingInst,TCancelable>()",
            "Instruction.MCOPY]=OpcodeHandler<MCopyOpcode<TTracingInst>,TTracingInst,TCancelable>()",
        }) RequireExactlyOnce(handlers, assignment, $"standard dispatch assignment {assignment}");
        Require(handlers, "if(spec.ReturnDataOpcodesEnabled)", "RETURNDATACOPY EIP-211 dispatch gate");
        Require(handlers, "if(spec.MCopyIncluded)", "MCOPY EIP-5656 dispatch gate");
        string specExtensions = Canonical(sources["src/Nethermind/Nethermind.Core/Specs/IReleaseSpecExtensions.cs"].Root);
        Require(specExtensions, "ReturnDataOpcodesEnabled=>spec.IsEip211Enabled", "RETURNDATACOPY gate mapping");
        Require(specExtensions, "MCopyIncluded=>spec.IsEip5656Enabled", "MCOPY gate mapping");
        Require(dispatch, "StartInstructionTrace(instruction,TGasPolicy.GetRemainingGas(ingas),(int)pc,instack)", "pre-increment instruction trace");
        RequireOrdered(dispatch, ["StartInstructionTrace", "pc++", "opCodeCount++", "TOpcode.Execute"], "dispatch trace/PC/counter/body order");
        Require(dispatch, "if(TTracingInst.IsActive&&!TOpcode.EndsInstructionTrace)", "dispatch trace closure ownership gate");
        Require(handlers, "staticvirtualboolEndsInstructionTrace=>false", "ordinary opcode trace closure ownership");
        Require(vm, "EndInstructionTrace(TGasPolicy.GetRemainingGas(ingas))", "post-charge successful trace closure");
        RequireOrdered(storage, ["InstructionMLoad", "UpdateGas<VeryLowGasCost>", "EnsureDepth(1)", "UpdateMemoryCost", "Load32BytesAfterGas", "ReportMemoryChange", "WriteRightPaddedBytes"], "MLOAD operational order");
        RequireOrdered(storage, ["InstructionMStore", "UpdateGas<VeryLowGasCost>", "PopMemoryPositionAndWord256", "UpdateMemoryCost", "StoreWordAfterGas", "ReportMemoryChange"], "MSTORE operational order");
        RequireOrdered(storage, ["InstructionMStore8", "UpdateGas<VeryLowGasCost>", "EnsureDepth(2)", "Pop2BytesByRefUnchecked", "UpdateMemoryCost", "StoreByteAfterGas", "ReportMemoryChange"], "MSTORE8 operational order");
        RequireOrdered(storage, ["InstructionMCopy", "PopUInt256", "Div32Ceiling", "TryConsumeMemoryCopy", "if(c.IsZero)", "UpdateMemoryCost", "LoadSpanAfterGas", "ReportMemoryChange", "CopyAfterGas", "LoadSpanAfterGas", "ReportMemoryChange"], "MCOPY operational order");
        RequireOrdered(copy, ["DataCopy", "PopMemoryPositionAndUInt256", "Div32Ceiling", "TryConsumeDataCopyGas", "if(outOfGas)", "if(!result.IsZero)", "UpdateMemoryCost", "CopyFromZeroExtendedAfterGas", "ReportMemoryChange"], "CALLDATACOPY/CODECOPY operational order");
        RequireOrdered(copy, ["InstructionReturnDataCopy", "PopMemoryPositionAndUInt256", "Div32Ceiling", "TryConsumeDataCopyGas", "if(outOfGas)", "AddOverflow", "if(!size.IsZero)", "UpdateMemoryCost", "SaveAfterGas", "ReportMemoryChange"], "RETURNDATACOPY operational order");
        RequireOrdered(environment, ["InstructionGas", "UpdateGas<BaseGasCost>", "GetRemainingGas", "PushUInt64"], "GAS post-charge observation order");
        Require(environment, "OpMSize<TGasPolicy>", "MSIZE environment operation");
        Require(environment, "vmState.Memory.Size", "MSIZE logical-size observation");
        Require(stack, "publicconstintMaxStackSize=1025", "1024-value stack limit plus empty-head sentinel");
        Require(stack, "PopMemoryPositionAndUInt256", "atomic three-operand memory pop");
        Require(stack, "beBytes.ByteSwap()", "accelerated copy-operand big-endian decoding");
        Require(evmWordExtensions, "publicEvmWordByteSwap()", "EVM word byte-swap implementation");
        Require(memory, "internalconstulongMaxMemorySize=int.MaxValue-WordSize+1", "memory cap");
        RequireOrdered(memory, ["ComputeMemoryExpansionCost", "Size=newActiveWords<<5", "GasCostOf.Memory", ">>9"], "logical-size-before-charge memory expansion");
        Require(gasInterface, "TryConsumeMemoryCopy", "MCOPY gas helper");
        Require(gasInterface, "GasCostOf.VeryLow+GasCostOf.VeryLow*words", "MCOPY fixed and word gas formula");
        Require(gasInterface, "TryConsumeDataCopyGas", "data-copy gas helper");
        Require(gas, "(isExternalCode?spec.GasCosts.ExtCodeCost:GasCostOf.VeryLow)+GasCostOf.Memory*words", "Ethereum data-copy fixed and word gas formula");
        Require(gas, "gas.Value=0;returnfalse", "Ethereum gas exhaustion behavior");
        Require(vmState, "publicrefEvmPooledMemoryMemory", "frame logical memory provider");
        Require(vmState, "publicExecutionEnvironmentEnv", "frame environment provider");
        Require(environmentState, "publicReadOnlyMemory<byte>InputData", "CALLDATACOPY source provider");
        Require(vmState, "EvmFrameMemory_inlineMemory", "inline frame-memory allocation provider");
        Require(stackPool, "GC.AllocateUninitializedArray<byte>", "standard unsafe-stack allocation provider");
        Require(Canonical(sources["src/Nethermind/Nethermind.Specs/Forks/06_Byzantium.cs"].Root), "spec.IsEip211Enabled=true", "EIP-211 activation");
        Require(Canonical(sources["src/Nethermind/Nethermind.Specs/Forks/17_Cancun.cs"].Root), "spec.IsEip5656Enabled=true", "EIP-5656 activation");
        Require(Canonical(sources[AmsterdamPath].Root), "NamedReleaseSpec<Amsterdam>(BPO2.Instance)", "Amsterdam fork ancestry");
        ValidateNoCompetingStandardDeclarations(root);
    }

    private static void ValidateNoCompetingStandardDeclarations(string root)
    {
        string evmRoot = ResolveExactPath(root, "src/Nethermind/Nethermind.Evm");
        string[] handlerNames = ["CallDataCopyOpcode", "CodeCopyOpcode", "ReturnDataCopyOpcode", "MLoadOpcode", "MStoreOpcode", "MStore8Opcode", "GasOpcode", "MCopyOpcode"];
        Dictionary<string, int> declarations = handlerNames.ToDictionary(static name => name, static _ => 0, StringComparer.Ordinal);
        string[] instructionMethods = ["InstructionCallDataCopy", "InstructionCodeCopy", "InstructionReturnDataCopy", "InstructionMLoad", "InstructionMStore", "InstructionMStore8", "InstructionMCopy", "InstructionGas"];
        Dictionary<string, int> methods = instructionMethods.ToDictionary(static name => name, static _ => 0, StringComparer.Ordinal);
        string[] opcodeAssignments = ["CALLDATACOPY", "CODECOPY", "RETURNDATACOPY", "MLOAD", "MSTORE", "MSTORE8", "MSIZE", "GAS", "MCOPY"];
        Dictionary<string, int> assignments = opcodeAssignments.ToDictionary(static name => name, static _ => 0, StringComparer.Ordinal);
        int msizeOperations = 0;
        foreach (string file in Directory.EnumerateFiles(evmRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(static path => !path.EndsWith(".zkevm.cs", StringComparison.Ordinal)))
        {
            CompilationUnitSyntax rootSyntax = CSharpSyntaxTree.ParseText(File.ReadAllText(file),
                CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp14)).GetCompilationUnitRoot();
            foreach (TypeDeclarationSyntax type in rootSyntax.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (declarations.ContainsKey(type.Identifier.ValueText)) declarations[type.Identifier.ValueText]++;
                if (type.Identifier.ValueText == "OpMSize") msizeOperations++;
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
        if (msizeOperations != 1)
            throw new ExtractionException($"Expected one standard-build declaration of OpMSize; found {msizeOperations}.");
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
            D("mload", "MLOAD", 0x51, "MLoadOpcode<TTracingInst>", "EvmInstructions.InstructionMLoad<TGasPolicy,TTracingInst>", 1, 1, "false", "always", S("loadWord", "veryLow", 32, "none", "parityLoad"), ["chargeVeryLow", "checkDepth1", "peekOffset", "installExpansion", "chargeExpansion", "load32", "traceParityMemory", "replaceTop", "tracePush"]),
            D("mstore", "MSTORE", 0x52, "MStoreOpcode<TTracingInst>", "EvmInstructions.InstructionMStore<TGasPolicy,TTracingInst>", 2, 0, "false", "always", S("storeWord", "veryLow", 32, "none", "destination"), ["chargeVeryLow", "popOffsetWordAtomically", "installExpansion", "chargeExpansion", "storeWord", "traceMemory"]),
            D("mstore8", "MSTORE8", 0x53, "MStore8Opcode<TTracingInst>", "EvmInstructions.InstructionMStore8<TGasPolicy,TTracingInst>", 2, 0, "false", "always", S("storeByte", "veryLow", 1, "none", "destination"), ["chargeVeryLow", "checkDepth2", "popTwoUnchecked", "readOffsetAndLowByte", "installExpansion", "chargeExpansion", "storeByte", "traceMemory"]),
            D("msize", "MSIZE", 0x59, "EnvUInt64Opcode<EvmInstructions.OpMSize<TGasPolicy>,TTracingInst>", "EvmInstructions.OpMSize<TGasPolicy>.Operation", 0, 1, "!TTracingInst.IsActive", "always", S("pushMemorySize", "base", 0, "none", "push"), ["chargeBase", "checkPushDepth", "readLogicalMemorySize", "push", "tracePush"]),
            D("calldatacopy", "CALLDATACOPY", 0x37, "CallDataCopyOpcode<TTracingInst>", "EvmInstructions.InstructionCallDataCopy<TGasPolicy,TTracingInst>", 3, 0, "false", "always", S("copyZeroExtended", "copy", 0, "calldata", "destination"), ["popThreeAtomically", "checkedWords", "chargeVeryLowAndWords", "rejectWordOverflowAfterCharge", "zeroLengthReturn", "installExpansion", "chargeExpansion", "copyZeroExtended", "traceMemory"]),
            D("codecopy", "CODECOPY", 0x39, "CodeCopyOpcode<TTracingInst>", "EvmInstructions.InstructionCodeCopy<TGasPolicy,TTracingInst>", 3, 0, "false", "always", S("copyZeroExtended", "copy", 0, "code", "destination"), ["popThreeAtomically", "checkedWords", "chargeVeryLowAndWords", "rejectWordOverflowAfterCharge", "zeroLengthReturn", "installExpansion", "chargeExpansion", "copyZeroExtended", "traceMemory"]),
            D("returndatacopy", "RETURNDATACOPY", 0x3e, "ReturnDataCopyOpcode<TTracingInst>", "EvmInstructions.InstructionReturnDataCopy<TGasPolicy,TTracingInst>", 3, 0, "false", "EIP-211", S("copyReturnData", "copy", 0, "returnData", "destination"), ["popThreeAtomically", "checkedWords", "chargeVeryLowAndWords", "rejectWordOverflowAfterCharge", "checkedSourceEnd", "rejectAccessBeforeExpansion", "zeroLengthReturn", "installExpansion", "chargeExpansion", "copyExact", "traceMemory"]),
            D("mcopy", "MCOPY", 0x5e, "MCopyOpcode<TTracingInst>", "EvmInstructions.InstructionMCopy<TGasPolicy,TTracingInst>", 3, 0, "false", "EIP-5656", S("copyMemory", "copy", 0, "memory", "sourceThenDestination"), ["popThreeAtomically", "checkedWords", "chargeVeryLowAndWords", "rejectWordOverflowAfterCharge", "zeroLengthReturn", "installMaxSourceDestinationExpansion", "chargeExpansion", "traceSource", "memmove", "traceDestination"]),
            D("gas", "GAS", 0x5a, "GasOpcode<TTracingInst>", "EvmInstructions.InstructionGas<TGasPolicy,TTracingInst>", 0, 1, "!TTracingInst.IsActive", "always", S("pushRemainingGas", "base", 0, "none", "push"), ["chargeBase", "checkPushDepth", "readPostChargeExecutionGas", "push", "tracePush"]),
        ];
        List<OpcodeSpecialization> roots = new(36);
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
            new(3, 2, 3, 3, 512, 2147483616, ulong.MaxValue, uint.MaxValue,
                "115792089237316195423570985008687907853269984665640564039457584007913129639936"),
            new(true, true),
            new(true, true, true),
            tables,
            opcodes,
            [.. roots],
            ForkLineage,
            [
                "Roslyn exact-syntax admission does not prove C# compilation, CLR/JIT/AOT execution, function-pointer tail calls, or hardware behavior.",
                "The model assumes admitted UInt256 and unsafe EvmStack slots represent the shared Lean UInt256 and top-first Stack without corruption.",
                "The model assumes EvmPooledMemory allocation, pooling, zero-initialization, Span.CopyTo memmove behavior, and adapter aliasing satisfy the admitted byte semantics.",
                "Tracing callbacks are assumed total and non-throwing; cancellation polling, tracer implementation internals, and deliberate Parity MLOAD memory reporting are not bugs at this boundary.",
                "The admitted outer closure covers OOG execution-gas clearing, tracer finish/error order, and fault-PC normalization; rollback, transaction and block processing, persistence, and state-gas settlement remain outside this slice.",
            ]);
    }

    private static OpcodeDescriptor D(string name, string instruction, int opcodeByte, string handlerBody,
        string target, int inputs, int outputs, string checkedBody, string activation, OpcodeSemantics semantics,
        string[] order) =>
        new(name, instruction, opcodeByte, handlerBody, target, inputs, outputs, checkedBody, activation, semantics, order);

    private static OpcodeSemantics S(string operation, string gasClass, int accessWidth, string copySource,
        string traceRule) => new(operation, gasClass, accessWidth, copySource, traceRule);

    private static string[] SemanticBindings() =>
    [
        "9 opcodes and 4 trace/cancellation tables yield exactly 36 closed generic roots",
        "dispatch trace starts before PC and opcode-count increments; success trace ends with post-charge gas",
        "copy word overflow is rejected only after the three-gas attempt; zero-length operations skip memory access",
        "memory logical size is installed before expansion gas affordability is known",
        "RETURNDATACOPY validates checked source end before destination memory expansion",
        "MCOPY traces the source before overlap-safe copy and destination after copy",
        "GAS observes only post-base-charge Ethereum execution gas",
        "outer OOG closure clears execution gas, reports finish before error, and normalizes the post-dispatch fault PC",
    ];

    private static AdmissionSpec[] BuildAdmissionSpecs()
    {
        List<AdmissionSpec> result =
        [
            A("src/Nethermind/Nethermind.Core/GasCostOf.cs", "Nethermind.Core", "namespace", "type", "GasCostOf"),
            A(BytesPath, "Nethermind.Core.Extensions", "namespace", "type", "Bytes"),
            A(BytesStandardPath, "Nethermind.Core.Extensions", "namespace", "type", "Bytes"),
            A(EvmWordExtensionsPath, "Nethermind.Core.Extensions", "namespace", "type", "EvmWordExtensions"),
            A("src/Nethermind/Nethermind.Core/TypeFlags.cs", "Nethermind.Core", "namespace", "type", "IFlag"),
            A("src/Nethermind/Nethermind.Core/TypeFlags.cs", "Nethermind.Core", "namespace", "type", "OffFlag"),
            A("src/Nethermind/Nethermind.Core/TypeFlags.cs", "Nethermind.Core", "namespace", "type", "OnFlag"),
            A("src/Nethermind/Nethermind.Core/Specs/IReleaseSpec.cs", "Nethermind.Core.Specs", "namespace", "type", "IReleaseSpec"),
            A("src/Nethermind/Nethermind.Core/Specs/IReleaseSpecExtensions.cs", "Nethermind.Core.Specs", "namespace", "type", "IReleaseSpecExtensions"),
            A("src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs", "Nethermind.Evm", "namespace", "type", "DispatchFlags"),
            A("src/Nethermind/Nethermind.Evm/Instruction.cs", "Nethermind.Evm", "namespace", "type", "Instruction"),
            A("src/Nethermind/Nethermind.Evm/Instructions/EvmCalculations.cs", "Nethermind.Evm", "namespace", "type", "EvmCalculations"),
            A(StackPath, "Nethermind.Evm", "namespace", "type", "EvmStack"),
            A(StackStandardPath, "Nethermind.Evm", "namespace", "type", "EvmStack"),
            A(MemoryPath, "Nethermind.Evm", "namespace", "type", "EvmPooledMemory"),
            A("src/Nethermind/Nethermind.Evm/VmState.cs", "Nethermind.Evm", "namespace", "type", "VmState", 1),
            A("src/Nethermind/Nethermind.Evm/ExecutionEnvironment.cs", "Nethermind.Evm", "namespace", "type", "ExecutionEnvironment"),
            A("src/Nethermind/Nethermind.Evm/EvmFrameMemory.cs", "Nethermind.Evm", "namespace", "type", "EvmFrameMemory"),
            A("src/Nethermind/Nethermind.Evm/StackPool.cs", "Nethermind.Evm", "namespace", "type", "StackPool"),
            A("src/Nethermind/Nethermind.Evm/StackPool.std.cs", "Nethermind.Evm", "namespace", "type", "StackPool"),
            A("src/Nethermind/Nethermind.Evm/GasPolicy/IGasCost.cs", "Nethermind.Evm.GasPolicy", "namespace", "type", "IGasCost"),
            A(GasInterfacePath, "Nethermind.Evm.GasPolicy", "namespace", "type", "IGasPolicy", 1),
            A(GasPath, "Nethermind.Evm.GasPolicy", "namespace", "type", "EthereumGasPolicy"),
            A(StoragePath, "Nethermind.Evm", "namespace", "type", "EvmInstructions"),
            A(CopyPath, "Nethermind.Evm", "namespace", "type", "EvmInstructions"),
            A(EnvironmentPath, "Nethermind.Evm", "namespace", "type", "EvmInstructions"),
            A("src/Nethermind/Nethermind.Evm/Tracing/ITxTracer.cs", "Nethermind.Evm.Tracing", "namespace", "type", "ITxTracer"),
            A("src/Nethermind/Nethermind.Evm/Tracing/TracerExtensions.cs", "Nethermind.Evm.Tracing", "namespace", "type", "TracerExtensions"),
            A("src/Nethermind/Nethermind.Evm/Tracing/TraceStack.cs", "Nethermind.Evm.Tracing", "namespace", "type", "TraceStack"),
            A("src/Nethermind/Nethermind.Evm/Tracing/TraceMemory.cs", "Nethermind.Evm.Tracing", "namespace", "type", "TraceMemory"),
            A(DispatchPath, "Nethermind.Evm", "namespace", "type", "VirtualMachine", 1),
            A(OpcodeHandlersPath, "Nethermind.Evm", "namespace", "type", "VirtualMachine", 1),
            A(VmPath, "Nethermind.Evm", "namespace", "type", "VirtualMachine", 1),
            A("src/Nethermind/Nethermind.Evm/VirtualMachine.std.cs", "Nethermind.Evm", "namespace", "type", "VirtualMachine", 1),
            A("src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs", "Nethermind.Init.Modules", "namespace", "type", "BlockProcessingModule"),
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
        SyntaxNode container = namespaces[0];
        MemberDeclarationSyntax[] matches = DirectMembers(container).Where(member => Matches(member, spec)).ToArray();
        if (matches.Length != 1)
            throw new ExtractionException($"Expected exactly one {AdmissionKey(spec)}; found {matches.Length}.");
        return matches[0];
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
        MemoryCopyOpcodeLeanEmitter.ValidateSha256(value, description);

    private static void WriteDeterministic(string path, byte[] bytes)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return;
        File.WriteAllBytes(path, bytes);
    }
}
