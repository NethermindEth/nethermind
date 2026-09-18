// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.CallCreateOpcodeExtractor;

internal static class CallCreateOpcodeProfile
{
    internal const string IrFileName = "CallCreateOpcodeKernel.ir.json";
    internal const string ManifestFileName = "CallCreateOpcodeKernel.source-manifest.json";
    internal const string LeanFileName = "CallCreateOpcodeKernel.lean";

    private const string ExtractorVersion = "1.0.0";
    private const string KernelName = "standard-mainnet-amsterdam-call-create-selfdestruct-operational";
    private const string BuildPropsPath = "src/Nethermind/Directory.Build.props";
    private const string BuildPropsSha256 = "ad544425af1b0ec60334511abf79896a77843088457ef81b98a06b56d13e4e3c";
    private const string BuildTargetsPath = "src/Nethermind/Directory.Build.targets";
    private const string BuildTargetsSha256 = "0598cebaef1df41102a18a3f9ace810bed1e4e64b8471d055724b4a394dccec6";
    private const string OperationalSpecPath = "tools/Evm/Lean/CallCreateOpcodeExtractor/Specification/CallCreateExecution.lean";
    private const string OperationalSpecSha256 = "f8abc56e1fdf9584a16cf24621da4fc4acdf50d72c3baa9db4525189d07e6023";
    private const string ReferenceSpecPath = "tools/Evm/Lean/CallCreateOpcodeExtractor/Specification/CallCreateOperational.lean";
    private const string ReferenceSpecSha256 = "cf38777ec528334a70eeabea2fbbcbca0c62ff8cb10327ca65403c995826925f";
    private const string ExpectedCombinedSourceSha256 = "7d4e0ec140b314202330fa1f027cbce8c9093faaa0a1a1c5336df4904413615c";
    private const string OpcodeHandlersPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs";
    private const string DispatchPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs";
    private const string CallPath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.cs";
    private const string CallStandardPath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.std.cs";
    private const string CreatePath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Create.cs";
    private const string ControlFlowPath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.ControlFlow.cs";
    private const string SpecPath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Spec.cs";
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
            ["src/Nethermind/Nethermind.Core/Eip8037Constants.cs"] = "f3151bee6ed2d29b84ee92af59f4fb3bab782ba1c4ef97cf121c341b2b5babdb",
            ["src/Nethermind/Nethermind.Core/Eip8038Constants.cs"] = "e057620777fce018901690c428ecdf33fdfc5e0ba903544edd4f51724826171a",
            ["src/Nethermind/Nethermind.Core/TypeFlags.cs"] = "33d8404d3ec39fffbc9f665126255f9799a0ba433206a141f480f8144be95747",
            ["src/Nethermind/Nethermind.Core/Specs/IReleaseSpec.cs"] = "3f44b3da5614c94d59eae2e4a6266a9e698614f75a7b5a9894a4828b0021b18a",
            ["src/Nethermind/Nethermind.Core/Specs/IReleaseSpecExtensions.cs"] = "5a22587aa81b5f0e4276e61bc1970ff89e4e4cecbed8c9b08dd338576c7d041a",
            ["src/Nethermind/Nethermind.Core/Specs/SpecGasCosts.cs"] = "51cc74d453997f05752e23b458be86adc2d94c55b4b450d565d21d6a92546879",
            [BytesPath] = "03b84f9fc045b3cd2f6730924d1a85435613536db2f8bfca1d76b154d313cd03",
            [BytesStandardPath] = "92617ca661bbd2785d8eb08cf3694ae4a5e08b31c618c87c9aecd2f75c1a0a91",
            [EvmWordExtensionsPath] = "0d2e127a715dc4b752fdeda2c553eb896802bf4def235f93f4e6996db3ec0e78",
            ["src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs"] = "99c58367e5b90fa5904484f6e2fbf10745a54f6cd7d07275810c27028ad0eb2b",
            ["src/Nethermind/Nethermind.Evm/Instruction.cs"] = "6895d06277ac4f9369d7bdc748976576bf384f6f3a5a348a2e4fd3767a46a030",
            ["src/Nethermind/Nethermind.Evm/Instructions/EvmCalculations.cs"] = "1912ef2add71990f29ab0cb7ef1de745b44f76bd201d4ac76c3032d5f60dc244",
            [StackPath] = "561520be4f0fe1d533ddea9cfea7ff48edb2745b1f3adf1b45c24e6c0bed02ad",
            [StackStandardPath] = "01a1b26ce105fa69fff94b8df171dcf11b85fe5462318a3ff9da3889ff41577e",
            [MemoryPath] = "853f8f9b46e5deef1c655442694dfbb1f862de80d0ecdbee2d66e7282f974e84",
            ["src/Nethermind/Nethermind.Evm/EvmFrameMemory.cs"] = "4bef331a3b36589c51e0560b3b288cacf2c85e582deb79f9c2f25284ccb56507",
            ["src/Nethermind/Nethermind.Evm/StackPool.cs"] = "4fa29adf34149fc192f9fc13140b8c8bdc3a8fa7b0f0b2aec94717670d6131c4",
            ["src/Nethermind/Nethermind.Evm/StackPool.std.cs"] = "62042951d6b5cf767cce1a1929dbf82ce26127b8884c784ab6708320f418c8f2",
            ["src/Nethermind/Nethermind.Evm/VmState.cs"] = "7b7b6ddb753118b426d14976a3fe8e79f808320d6a09ea9cace9df19197f9f23",
            ["src/Nethermind/Nethermind.Evm/ExecutionEnvironment.cs"] = "c076225b688b1e04ed83cddc90a2ddbda69862333dc927945eb32d376e43e1db",
            ["src/Nethermind/Nethermind.Evm/ExecutionType.cs"] = "b6689c7c928182f6d115ca57cc8ac45bb9312359a026704f2eec77b921028013",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.CallResult.cs"] = "d21bebbb4caea938c42d369083d922f6e18d01eb797b1c534fb1541c242ca073",
            ["src/Nethermind/Nethermind.Evm/StackAccessTracker.cs"] = "ed5cdfdfe2ebf4d8c9f651572927efe7bdb5c75107db63a40577c5ec3ec2882f",
            ["src/Nethermind/Nethermind.Evm/ContractAddress.cs"] = "d21b1b6b2965a7b38bf51c6c2b11133e4308031e30e1a74eacf258665cb618b3",
            ["src/Nethermind/Nethermind.Evm/ICodeInfoRepository.cs"] = "e0669f612f31fe4a353a60896f40325e493e1ff925a92614ad73f49bf1435015",
            ["src/Nethermind/Nethermind.Evm/CodeInfoRepository.cs"] = "76734fad45dd415deaf016b1b9ce6670580042e09f5493253b06c7a9c9f622b7",
            ["src/Nethermind/Nethermind.Evm/CodeAnalysis/CodeInfo.cs"] = "41e5e0316853f5970b5f868a6a47e24105dcb6eeb9be9608378482d74bcbd254",
            ["src/Nethermind/Nethermind.Evm/CodeAnalysis/CodeInfoFactory.cs"] = "ef71341adb0d8d5691392359e7e2b0a1c1b483edf8963f205877a89b39ab7c7c",
            ["src/Nethermind/Nethermind.Evm/CodeDepositHandler.cs"] = "72248ff72f65f545d723bc75e3703ccc05333b165944d95905f310294be3cfc7",
            ["src/Nethermind/Nethermind.Evm/GasPolicy/IGasCost.cs"] = "16289cdc2f777a76fda3ad8ff4ce4275c30513aee04c9cf42d314cc6e1ec48c0",
            [GasInterfacePath] = "b4d26db87723243514f2f6b8a5e48d1176139105c3b012ad0f3956e59afc98a8",
            [GasPath] = "3c31ba40c24a78bf11243b38859168350f02934806124e913da0560bc1a4350f",
            ["src/Nethermind/Nethermind.Evm/GasPolicy/AccountAccessKind.cs"] = "a61bc07a6ca4077bf47206799304c8335a901d22bba5e77b3403c92a52eb5462",
            ["src/Nethermind/Nethermind.Evm/GasPolicy/AccountAccessPricingKernel.cs"] = "1988faefb55bb65bf4e915e77e99966f43b88675dec47689e04378bde4f31c92",
            ["src/Nethermind/Nethermind.Evm/GasPolicy/StateGasChargeKernel.cs"] = "ec3276958cbc6b0255fadedf15dbe49691195aa95570b332937f8d1703b103fd",
            ["src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs"] = "4a31dc76fb8284a8ea5994de9a63810abe0fd7efbe6add4aa8b3d6cb564a75cc",
            ["src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionAdapterKernel.cs"] = "ca16429e4cb1b728619b7fa0113b2302ca75600b0a32d9345e38721c7c2d7368",
            [SpecPath] = "2aee227767f2adf6d04f02a2d2d3ada8028d84dac60192678630b6af9212ae97",
            [CallPath] = "cfc87832eb304f8f8dccfb27b6b89605f6c74e88522f02b13c775834d14948a5",
            [CallStandardPath] = "42954af1ac17ea3a973d9ca57a01432637bd92ebc98857838ddafa8e7aef93d6",
            [CreatePath] = "23927529d13488932ec47b57972ab7084566a28deae83805d69a1fe977576719",
            [ControlFlowPath] = "282ba75f60c1ddd03fd51460b0e3273c0113430d02faee3abff991fe5f920c6c",
            ["src/Nethermind/Nethermind.Evm/State/IWorldState.cs"] = "0680ebc7168472645d93df58d2b3240508c177361815c85cceb09cf960f189c7",
            ["src/Nethermind/Nethermind.Evm/State/WorldStateExtensions.cs"] = "4c0a66d1de95b4340bc9a190f6bef29b6e4068ebffa19ebe9cce3f789131fc62",
            ["src/Nethermind/Nethermind.Evm/Tracing/ITxTracer.cs"] = "9f2550f8c3407f494452386699040feab8aa597437b1cd4180d038d9fd0a9169",
            ["src/Nethermind/Nethermind.Evm/Tracing/TracerExtensions.cs"] = "4704eb408436ecb82614bbf1db26f11fcfcfa65ef613146e37a31f7e30a8d662",
            ["src/Nethermind/Nethermind.Evm/Tracing/TraceStack.cs"] = "a2e06210112f3a47bca6f95e4b3a89433c78f4d39b5ec4cbafe485f21191acb8",
            ["src/Nethermind/Nethermind.Evm/Tracing/TraceMemory.cs"] = "0e8bda869acd599a94a8cd6a9a79a7ff457c4acf1534a0c7f4ebd39939b8fca6",
            [DispatchPath] = "4f36bb20057caec9c85bcd3621372563f4d47ba36a9cbf01be267af4e379bca1",
            [OpcodeHandlersPath] = "5148dd594e40ebb976179b1048f233914f54acc8d033d358cf3e2e7594e112b6",
            [VmPath] = "45edba3691e09185e749485785990662ddea1af849bc6e4564f92b68a5137a6b",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.std.cs"] = "357fbd35369424a379c8c29338f491627512d40a12199d7a3d9aac44c87ca92a",
            ["src/Nethermind/Nethermind.Evm/VirtualMachine.ExecutionHandlers.cs"] = "62714bd459a38cc13e7b7bd2d5cfc7bc2ffdcb50743eeb565fdc2ee2966e286a",
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
        [.. ExpectedSourceSha256.Keys, BuildPropsPath, BuildTargetsPath, OperationalSpecPath, ReferenceSpecPath];

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
        byte[] referenceSpec = File.ReadAllBytes(ResolveExactPath(root, ReferenceSpecPath));
        string referenceSpecSha = Hash(referenceSpec);
        if (referenceSpecSha != ReferenceSpecSha256)
            throw new ExtractionException($"Unadmitted independent operational reference; found SHA-256 {referenceSpecSha}.");
        List<AdmissionIdentity> admissions = AdmitSyntax(sources);
        ValidateProductionClosure(root, sources);

        IrDocument ir = ExpectedIr();
        byte[] irBytes = Serialize(ir);
        IrDocument roundTrip = DeserializeIr(irBytes);
        string irSha = Hash(irBytes);
        SourceIdentity[] identities = sources.Values.OrderBy(static source => source.RelativePath, StringComparer.Ordinal)
            .Select(static source => new SourceIdentity(source.RelativePath, source.Sha256, Hash(CompleteCanonical(source.Root)))).ToArray();
        RawSourceIdentity[] rawSources =
        [
            buildProps,
            build,
            new(OperationalSpecPath, operationalSpecSha),
            new(ReferenceSpecPath, referenceSpecSha),
        ];
        string sourceSha = CombinedSourceHash(identities, rawSources, admissions);
        byte[] leanBytes = CallCreateOpcodeLeanEmitter.Emit(roundTrip, irSha, sourceSha);

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
                ?? throw new ExtractionException("Serialized CALL/CREATE opcode IR is empty.");
            ValidateIr(value);
            return value;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Serialized CALL/CREATE opcode IR is not valid JSON: {exception.Message}");
        }
    }

    internal static SourceManifest DeserializeManifest(byte[] bytes)
    {
        RejectDuplicateProperties(bytes, "manifest");
        try
        {
            SourceManifest value = JsonSerializer.Deserialize<SourceManifest>(bytes, JsonOptions)
                ?? throw new ExtractionException("Serialized CALL/CREATE opcode manifest is empty.");
            ValidateManifestShape(value);
            return value;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"Serialized CALL/CREATE opcode manifest is not valid JSON: {exception.Message}");
        }
    }

    internal static void ValidateIr(IrDocument value)
    {
        if (value.Schedule is null || value.Activation is null || value.OuterFailure is null || value.DispatchTables is null || value.Opcodes is null ||
            value.Specializations is null || value.ForkLineage is null || value.OpenExtractionObligations is null ||
            value.DispatchTables.Any(static item => item is null) || value.Opcodes.Any(static item => item is null || item.Semantics is null || item.EffectOrder is null) ||
            value.Specializations.Any(static item => item is null))
            throw new ExtractionException("Serialized CALL/CREATE opcode IR contains null fields, collections, or entries.");
        if (!Serialize(value).AsSpan().SequenceEqual(Serialize(ExpectedIr())))
            throw new ExtractionException("Serialized CALL/CREATE opcode IR is not the exact admitted profile.");
    }

    internal static void ValidateManifest(SourceManifest value, SourceManifest expected)
    {
        ValidateManifestShape(value);
        if (!Serialize(value).AsSpan().SequenceEqual(Serialize(expected)))
            throw new ExtractionException("Serialized CALL/CREATE opcode manifest does not exactly match extracted lineage.");
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
            throw new ExtractionException("Serialized CALL/CREATE opcode manifest contains null fields, collections, or entries.");
        if (value.SchemaVersion != 1 || value.ExtractorVersion != ExtractorVersion || value.Kernel != KernelName ||
            value.LanguageVersion != "CSharp14" || value.RoslynVersion != (typeof(CSharpSyntaxTree).Assembly.GetName().Version?.ToString() ?? "unknown"))
            throw new ExtractionException("Serialized CALL/CREATE opcode manifest header changed.");
        if (value.Sources.Length != ExpectedSourceSha256.Count ||
            value.Sources.Select(static item => item.Path).Distinct(StringComparer.Ordinal).Count() != value.Sources.Length)
            throw new ExtractionException("Serialized CALL/CREATE source identity set changed or collides.");
        foreach (SourceIdentity source in value.Sources)
        {
            ValidateSha256(source.Sha256, source.Path);
            ValidateSha256(source.RoslynSyntaxSha256, source.Path + " syntax");
            if (!ExpectedSourceSha256.TryGetValue(source.Path, out string? expected) || source.Sha256 != expected)
                throw new ExtractionException($"Serialized CALL/CREATE source digest changed for {source.Path}.");
        }
        if (value.RawSources.Length != 4 ||
            value.RawSources.Select(static item => item.Path).Distinct(StringComparer.Ordinal).Count() != 4 ||
            !value.RawSources.Any(static item => item.Path == BuildPropsPath && item.Sha256 == BuildPropsSha256) ||
            !value.RawSources.Any(static item => item.Path == BuildTargetsPath && item.Sha256 == BuildTargetsSha256) ||
            !value.RawSources.Any(static item => item.Path == OperationalSpecPath && item.Sha256 == OperationalSpecSha256) ||
            !value.RawSources.Any(static item => item.Path == ReferenceSpecPath && item.Sha256 == ReferenceSpecSha256))
            throw new ExtractionException("Serialized CALL/CREATE raw-source identity set changed.");
        foreach (RawSourceIdentity source in value.RawSources) ValidateSha256(source.Sha256, source.Path);
        if (value.Admissions.Length != Admissions.Length ||
            value.Admissions.Select(AdmissionKey).Distinct(StringComparer.Ordinal).Count() != value.Admissions.Length)
            throw new ExtractionException("Serialized CALL/CREATE exact-admission set changed or collides.");
        for (int index = 0; index < Admissions.Length; index++)
        {
            AdmissionSpec expected = Admissions[index];
            AdmissionIdentity actual = value.Admissions[index];
            ValidateSha256(actual.Sha256, AdmissionKey(actual));
            if (actual.SourcePath != expected.SourcePath || actual.Namespace != expected.Namespace ||
                actual.OwnerPath != expected.OwnerPath || actual.MemberKind != expected.MemberKind ||
                actual.MemberName != expected.MemberName || actual.MemberGenericArity != expected.MemberGenericArity ||
                actual.ParameterTypes != expected.ParameterTypes || string.IsNullOrWhiteSpace(actual.SyntaxKind))
                throw new ExtractionException($"Serialized CALL/CREATE admission qualifier changed at index {index}.");
        }
        if (value.Ir.Path != IrFileName || value.Lean.Path != LeanFileName)
            throw new ExtractionException("Serialized CALL/CREATE artifact path changed.");
        ValidateSha256(value.Ir.Sha256, value.Ir.Path);
        ValidateSha256(value.Lean.Sha256, value.Lean.Path);
        byte[] canonicalIr = Serialize(ExpectedIr());
        string canonicalIrSha = Hash(canonicalIr);
        if (value.Ir.Sha256 != canonicalIrSha)
            throw new ExtractionException("Serialized CALL/CREATE IR artifact digest is not canonical.");
        ValidateSha256(value.CombinedSourceSha256, "combined source");
        if (value.CombinedSourceSha256 != CombinedSourceHash(value.Sources, value.RawSources, value.Admissions))
            throw new ExtractionException("Serialized CALL/CREATE combined source digest is inconsistent.");
        if (value.CombinedSourceSha256 != ExpectedCombinedSourceSha256)
            throw new ExtractionException(
                $"Serialized CALL/CREATE source closure is not the admitted closure; found {value.CombinedSourceSha256}.");
        byte[] canonicalLean = CallCreateOpcodeLeanEmitter.Emit(
            DeserializeIr(canonicalIr), canonicalIrSha, value.CombinedSourceSha256);
        if (value.Lean.Sha256 != Hash(canonicalLean))
            throw new ExtractionException("Serialized CALL/CREATE Lean artifact digest is not canonical.");
        if (!value.SemanticBindings.SequenceEqual(SemanticBindings(), StringComparer.Ordinal))
            throw new ExtractionException("Serialized CALL/CREATE semantic bindings changed.");
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
        string call = Canonical(sources[CallPath].Root);
        string callStandard = Canonical(sources[CallStandardPath].Root);
        string create = Canonical(sources[CreatePath].Root);
        string controlFlow = Canonical(sources[ControlFlowPath].Root);
        string spec = Canonical(sources[SpecPath].Root);
        string stack = Canonical(sources[StackPath].Root);
        string memory = Canonical(sources[MemoryPath].Root);
        string gasInterface = Canonical(sources[GasInterfacePath].Root);
        string gas = Canonical(sources[GasPath].Root);
        string vm = Canonical(sources[VmPath].Root);
        string vmState = Canonical(sources["src/Nethermind/Nethermind.Evm/VmState.cs"].Root);
        string access = Canonical(sources["src/Nethermind/Nethermind.Evm/StackAccessTracker.cs"].Root);
        string deposit = Canonical(sources["src/Nethermind/Nethermind.Evm/CodeDepositHandler.cs"].Root);

        foreach (string assignment in new[]
        {
            "Instruction.CALL]=GetCallHandler<EvmInstructions.OpCall,TTracingInst,TCancelable>",
            "Instruction.CALLCODE]=GetCallHandler<EvmInstructions.OpCallCode,TTracingInst,TCancelable>",
            "Instruction.DELEGATECALL]=GetCallHandler<EvmInstructions.OpDelegateCall,TTracingInst,TCancelable>",
            "Instruction.STATICCALL]=GetCallHandler<EvmInstructions.OpStaticCall,TTracingInst,TCancelable>",
            "Instruction.CREATE]=GetCreateHandler<EvmInstructions.OpCreate,TTracingInst,TCancelable>",
            "Instruction.CREATE2]=GetCreateHandler<EvmInstructions.OpCreate2,TTracingInst,TCancelable>",
            "Instruction.SELFDESTRUCT]=",
        }) RequireExactlyOnce(handlers, assignment, $"standard dispatch assignment {assignment}");

        Require(handlers, "OpcodeHandler<CallOpcode<TOpCall,TTracingInst,OnFlag,OnFlag,EvmInstructions.CallSpec<Eip2929,Eip150,Eip158,Eip2780,Eip8038>>,TTracingInst,TCancelable>()", "Amsterdam CALL leaf");
        Require(handlers, "OpcodeHandler<CreateOpcode<TOpCreate,TTracingInst,OnFlag,EvmInstructions.CreateSpec<Eip2929,Eip150,Eip3860,Eip8038>>,TTracingInst,TCancelable>()", "Amsterdam CREATE leaf");
        Require(handlers, "TerminatingOpcodeHandler<SelfDestructOpcode<OnFlag,OnFlag,EvmInstructions.SelfDestructSpec<TAccess,Eip150,Eip158,Eip6780,Eip8246,Eip8038>>,TTracingInst,TCancelable>()", "Amsterdam SELFDESTRUCT leaf");
        Require(handlers, "staticvirtualboolEndsInstructionTrace=>false", "ordinary opcode trace ownership default");
        RequireOrdered(handlers, ["structCreateOpcode", "staticboolEndsInstructionTrace=>true", "InstructionCreate"], "CREATE trace ownership marker and handler");
        Require(dispatch, "StartInstructionTrace(instruction,TGasPolicy.GetRemainingGas(ingas),(int)pc,instack)", "pre-increment instruction trace");
        RequireOrdered(dispatch, ["StartInstructionTrace", "pc++", "opCodeCount++", "TOpcode.Execute"], "dispatch trace/PC/counter/body order");
        Require(dispatch, "if(TTracingInst.IsActive&&!TOpcode.EndsInstructionTrace)", "continuable trace ownership gate");

        RequireOrdered(call, ["PopUInt256(outUInt256gasLimit)", "PopAddress(vm.AddressCache)", "callValue", "PopUInt256(outcallValue)", "PopUInt256(outUInt256dataOffset,outUInt256dataLength,outUInt256outputOffset,outUInt256outputLength)", "StaticCallViolation"], "CALL sequential pop and static order");
        Require(stack, "publicconstintAddressSize=20", "20-byte EVM address width");
        RequireOrdered(stack, ["PopAddress(PoppedAddressCachecache)", "WordSize-AddressSize),AddressSize"], "CALL address low-160-bit pop");
        RequireOrdered(call, ["TryConsumeCallValueTransferEip2780", "TryConsumeCallBaseGas", "UpdateMemoryCost(refgas,indataOffset", "UpdateMemoryCost(refgas,inoutputOffset", "TryConsumeAccountAccessGas", "GetCachedCodeInfo", "TryConsumeDelegatedAccountAccessGas", "chargesNewAccount", "TryConsumeNewAccountCreation", "TryReserveChildGas", "gasLimitUl+=GasCostOf.CallStipend", "env.CallDepth>=MaxCallDepth"], "CALL pricing, access, reservation, stipend, and precheck order");
        RequireOrdered(call, ["stack.PushZero", "UpdateGasUp(refgas,gasLimitUl)", "CreditStateGasRefund", "returnpushResult"], "CALL depth/balance failure residue");
        RequireOrdered(call, ["stack.PushBytes", "if(pushResult!=EvmExceptionType.None)", "UpdateGasUp(refgas,gasLimitUl)", "SubtractFromBalance"], "empty-code push-before-world-mutation");
        RequireOrdered(call, ["TakeSnapshot", "SubtractFromBalance", "Memory.TryLoad", "ExecutionEnvironment.Rent", "CreateChildFrameGas", "VmState<TGasPolicy>.RentFrame", "EvmExceptionType.Suspend"], "CALL child-frame staging order");
        RequireOrdered(callStandard, ["TTracingInst.IsActive||vm.IsTracingActions||!vm.CanExecutePrecompileCallDirectly", "Memory.TryLoad", "CreateChildFrameGas", "TryConsumePrecompileGas", "TryRunPrecompileDirectly", "Refund", "TrySave", "PushBytes"], "standard inline STATICCALL precompile eligibility and execution order");
        RequireOrdered(vm, ["CanExecutePrecompileCallDirectly", "!codeSource.Equals(Ripemd160Address)", "TryRunPrecompileDirectly", "precompile.Run"], "RIPEMD160 exclusion and direct precompile execution boundary");

        RequireOrdered(create, ["if(vm.VmState.IsStatic)", "PopUInt256(outUInt256value", "typeof(TOpCreate)==typeof(OpCreate2)", "initCodeLength>spec.MaxInitCodeSize", "Div32Ceiling", "TryConsumeCreateGas", "UpdateMemoryCost", "env.CallDepth>=MaxCallDepth", "TryLoadOwned", "GetBalance", "GetNonce", "ContractAddress.From"], "CREATE validation and derivation order");
        RequireOrdered(create, ["AccessTracker.WarmUp(contractAddress)", "IsCreateCollision", "chargeCreateStateGas", "TryConsumeCreateStateGas", "gasAvailable", "EndInstructionTrace(gasAvailable)", "TryReserveChildGas", "IncrementNonce", "TakeSnapshot", "CreateCodeInfo", "if(isCreateCollision)"], "CREATE access/state/trace/reservation/collision order");
        RequireOrdered(create, ["if(chargeCreateStateGas)", "CreditStateGasRefund", "ReturnDataBuffer=default", "stack.PushZero"], "CREATE collision burn/refill/result order");
        Require(create, "CompleteCreateWithoutChild<TGasPolicy,TTracingInst>", "CREATE early-continuation helper");
        RequireOrdered(create, ["privatestaticEvmExceptionTypeCompleteCreateWithoutChild", "vm.ReturnDataBuffer=default", "stack.PushZero", "vm.EndInstructionTrace", "returnresult"], "CREATE early-continuation push then closure");

        RequireOrdered(controlFlow, ["InstructionSelfDestruct", "if(vmState.IsStatic)", "TryConsumeSelfDestructGas", "stack.PopAddress", "TryConsumeAccountAccessGas", "CreateList.Contains", "ToBeDestroyed", "GetBalance", "ReportSelfDestruct", "AccountExists", "chargesNewAccount", "Eip8038Constants.AccountWrite", "TryConsumeNewAccountCreation", "CreateAccount", "AddSelfDestructLog", "SubtractFromBalance"], "SELFDESTRUCT Amsterdam order");
        Require(spec, "CallSpec<Eip2929,Eip150,Eip158,Eip2780,Eip8038>", "CALL fork projection");
        Require(spec, "CreateSpec<Eip2929,Eip150,Eip3860,Eip8038>", "CREATE fork projection");
        Require(spec, "SelfDestructSpec<TAccess,Eip150,Eip158,Eip6780,Eip8246,Eip8038>", "SELFDESTRUCT fork projection");

        Require(stack, "publicconstintMaxStackSize=1025", "1024-value stack limit plus sentinel");
        Require(memory, "internalconstulongMaxMemorySize=int.MaxValue-WordSize+1", "memory cap");
        Require(gasInterface, "TryReserveChildGas", "child-gas interface");
        Require(gas, "baseCost=Eip8038.IsActive?Eip8038Constants.CreateAccess", "Amsterdam CREATE execution pricing");
        Require(gas, "TryConsumeCallValueTransferEip2780", "Amsterdam CALL value pricing");
        RequireOrdered(gas, ["gasAvailable=GetRemainingGas(ingas)", "cap=gasAvailable-gasAvailable/64", "childGas=", "TryReserveChildGas(refgas,childGas)"], "EIP-150 requested reservation");
        Require(gas, "childGas=use63Over64Rule?gasAvailable-gasAvailable/64:gasAvailable", "EIP-150 CREATE reservation");
        Require(gas, "StateGasTransitionAdapterKernel.RestoreChildStateGas", "REVERT state-gas restore adapter");
        Require(gas, "StateGasTransitionAdapterKernel.RestoreChildStateGasOnHalt", "exception state-gas restore adapter");
        Require(deposit, "GasCostOf.CodeDepositExecutionPerWord*words", "Amsterdam code-deposit execution cost");
        Require(deposit, "TGasPolicy.GetCodeDepositStateCost(byteCodeLength)", "Amsterdam code-deposit state cost");

        Require(vmState, "VmState<TGasPolicy>RentFrame", "child-frame constructor");
        Require(vmState, "CommitToParent", "child journal commit");
        Require(access, "WarmUp(Addressaddress)", "address warmth journal");
        Require(access, "ToBeDestroyed(Addressaddress)", "destroy-list journal");
        RequireOrdered(vm, ["if(!callResult.IsReturn)", "PrepareNextCallFrame", "if(callResult.IsException)", "HandleException", "if(_currentState.IsTopLevel)", "previousState=_currentState", "_currentState=_stateStack.Pop"], "VM suspend/return/exception/parent order");
        RequireOrdered(vm, ["TGasPolicy.Refund", "PrepareCreateData", "HandleCreate", "CommitToParent", "IncorporateChildStateGasRefunds", "RepayStateGasSpill"], "successful CREATE merge/deposit/commit order");
        RequireOrdered(vm, ["UpdateGasUp", "RestoreChildStateGas", "CreditStateGasRefund", "HandleRevert"], "REVERT gas/refill/journal order");
        RequireOrdered(vm, ["previousCallResult.Success.HasValue", "stack.PushAddress", "stack.PushOne", "stack.PushZero", "ReportOperationRemainingGas", "SaveAfterGas", "RunByteCode"], "parent result push/report/output/resume order");
        Require(vm, "exceptionType!=EvmExceptionType.Suspend||ReturnDataisnotVmState<TGasPolicy>childState||!childState.ExecutionType.IsAnyCreate()", "CREATE suspend trace ownership");
        RequireOrdered(vm, ["TraceTransactionActionStart(_currentState)", "AddTransferLog(_currentState)", "ExecuteCall<TTracingInst>"], "child action-start and transfer-log order");
        RequireOrdered(vm, ["if(callResult.IsException)", "ReportActionError", "elseif(callResult.ShouldRevert)", "ReportActionRevert", "elseif(currentState.ExecutionType.IsAnyCreate())", "ReportActionEnd"], "child action completion classification");
        RequireOrdered(vm, ["TryChargeAndDepositCode", "TryConsumeStateAndExecutionGas", "InsertCode", "if(!chargedCodeDeposit", "RevertRefundToHalt", "_worldState.Restore", "_worldState.DeleteAccount"], "CREATE deposit success/failure order");

        Require(Canonical(sources[AmsterdamPath].Root), "NamedReleaseSpec<Amsterdam>(BPO2.Instance)", "Amsterdam fork ancestry");
        foreach (string flag in new[] { "IsEip2780Enabled=true", "IsEip7708Enabled=true", "IsEip8037Enabled=true", "IsEip8038Enabled=true", "IsEip8246Enabled=true" })
            Require(Canonical(sources[AmsterdamPath].Root), flag, $"Amsterdam activation {flag}");
        ValidateNoCompetingStandardDeclarations(root);
    }

    private static void ValidateNoCompetingStandardDeclarations(string root)
    {
        string evmRoot = ResolveExactPath(root, "src/Nethermind/Nethermind.Evm");
        string[] handlerNames = ["CallOpcode", "CreateOpcode", "SelfDestructOpcode"];
        Dictionary<string, int> declarations = handlerNames.ToDictionary(static name => name, static _ => 0, StringComparer.Ordinal);
        string[] instructionMethods = ["InstructionCall", "InstructionCreate", "CompleteCreateWithoutChild", "InstructionSelfDestruct"];
        Dictionary<string, int> methods = instructionMethods.ToDictionary(static name => name, static _ => 0, StringComparer.Ordinal);
        string[] opcodeAssignments = ["CALL", "CALLCODE", "DELEGATECALL", "STATICCALL", "CREATE", "CREATE2", "SELFDESTRUCT"];
        Dictionary<string, int> assignments = opcodeAssignments.ToDictionary(static name => name, static _ => 0, StringComparer.Ordinal);
        int inlineStaticPrecompileDeclarations = 0;
        foreach (string file in Directory.EnumerateFiles(evmRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(static path => !path.EndsWith(".zkevm.cs", StringComparison.Ordinal)))
        {
            CompilationUnitSyntax rootSyntax = CSharpSyntaxTree.ParseText(File.ReadAllText(file),
                CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp14)).GetCompilationUnitRoot();
            foreach (TypeDeclarationSyntax type in rootSyntax.DescendantNodes().OfType<TypeDeclarationSyntax>())
                if (declarations.ContainsKey(type.Identifier.ValueText)) declarations[type.Identifier.ValueText]++;
            foreach (MethodDeclarationSyntax method in rootSyntax.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (methods.ContainsKey(method.Identifier.ValueText)) methods[method.Identifier.ValueText]++;
                if (method.Identifier.ValueText == "TryInlineStaticPrecompileCall") inlineStaticPrecompileDeclarations++;
            }
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
        if (inlineStaticPrecompileDeclarations != 2)
            throw new ExtractionException($"Expected declaration plus standard implementation of TryInlineStaticPrecompileCall; found {inlineStaticPrecompileDeclarations}.");
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
            D("call", "CALL", 0xf1, "call", "CallOpcode<EvmInstructions.OpCall,TTracingInst,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>", "EvmInstructions.InstructionCall<EthereumGasPolicy,EvmInstructions.OpCall,TTracingInst,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>", 7, 1, false, false, S("call", "explicit", "codeSource", "successBool"), [
                "traceStart", "advancePcAndCount", "popRequestedGas", "popCodeSource", "normalizeCodeSourceLow160", "popValue", "popInputOffsetLengthAndOutputOffsetLength", "rejectStaticValue", "chargeValue", "chargeCallBase", "expandAndChargeInputMemory", "expandAndChargeOutputMemory", "warmAndChargeCodeSource", "discoverDelegation", "warmAndChargeDelegatedTarget", "classifyDeadTarget", "chargeNewAccountState", "reserveEip150Execution", "addStipend", "checkDepthAndBalance", "handleEarlyZeroOrRoute", "stageChildOrInlineResult", "closeOrSuspend", "resumeMerge", "pushResultBeforeResumeTrace"]),
            D("callcode", "CALLCODE", 0xf2, "call", "CallOpcode<EvmInstructions.OpCallCode,TTracingInst,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>", "EvmInstructions.InstructionCall<EthereumGasPolicy,EvmInstructions.OpCallCode,TTracingInst,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>", 7, 1, false, false, S("callcode", "explicit", "executingAccount", "successBool"), [
                "traceStart", "advancePcAndCount", "popRequestedGas", "popCodeSource", "normalizeCodeSourceLow160", "popValue", "popInputOffsetLengthAndOutputOffsetLength", "allowStaticCallcodeValue", "chargeValue", "chargeCallBase", "expandAndChargeInputMemory", "expandAndChargeOutputMemory", "warmAndChargeCodeSource", "discoverDelegation", "warmAndChargeDelegatedTarget", "skipDeadSelfTargetCharge", "reserveEip150Execution", "addStipend", "checkDepthAndBalance", "handleEarlyZeroOrRoute", "stageChild", "closeOrSuspend", "resumeMerge", "pushResultBeforeResumeTrace"]),
            D("delegatecall", "DELEGATECALL", 0xf4, "call", "CallOpcode<EvmInstructions.OpDelegateCall,TTracingInst,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>", "EvmInstructions.InstructionCall<EthereumGasPolicy,EvmInstructions.OpDelegateCall,TTracingInst,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>", 6, 1, false, false, S("delegatecall", "environment", "executingAccount", "successBool"), [
                "traceStart", "advancePcAndCount", "popRequestedGas", "popCodeSource", "normalizeCodeSourceLow160", "deriveEnvironmentValue", "popInputOffsetLengthAndOutputOffsetLength", "chargeCallBase", "expandAndChargeInputMemory", "expandAndChargeOutputMemory", "warmAndChargeCodeSource", "discoverDelegation", "warmAndChargeDelegatedTarget", "reserveEip150Execution", "checkDepth", "handleEarlyZeroOrRoute", "stageChild", "closeOrSuspend", "resumeMerge", "pushResultBeforeResumeTrace"]),
            D("staticcall", "STATICCALL", 0xfa, "call", "CallOpcode<EvmInstructions.OpStaticCall,TTracingInst,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>", "EvmInstructions.InstructionCall<EthereumGasPolicy,EvmInstructions.OpStaticCall,TTracingInst,OnFlag,OnFlag,EvmInstructions.CallSpec<OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>", 6, 1, false, false, S("staticcall", "zero", "codeSource", "successBool"), [
                "traceStart", "advancePcAndCount", "popRequestedGas", "popCodeSource", "normalizeCodeSourceLow160", "deriveZeroValue", "popInputOffsetLengthAndOutputOffsetLength", "chargeCallBase", "expandAndChargeInputMemory", "expandAndChargeOutputMemory", "warmAndChargeCodeSource", "discoverDelegation", "warmAndChargeDelegatedTarget", "reserveEip150Execution", "checkDepth", "handleEarlyZeroOrRoute", "requireNoInstructionOrActionTrace", "excludeRipemd160DirectPrecompile", "boundReturnedGasToReservation", "tryStandardInlinePrecompile", "stageChildOtherwise", "closeOrSuspend", "resumeMerge", "pushResultBeforeResumeTrace"]),
            D("create", "CREATE", 0xf0, "create", "CreateOpcode<EvmInstructions.OpCreate,TTracingInst,OnFlag,EvmInstructions.CreateSpec<OnFlag,OnFlag,OnFlag,Eip8038On>>", "EvmInstructions.InstructionCreate<EthereumGasPolicy,EvmInstructions.OpCreate,TTracingInst,OnFlag,EvmInstructions.CreateSpec<OnFlag,OnFlag,OnFlag,Eip8038On>>", 3, 1, false, true, S("create", "explicit", "derivedCreate", "createdAddress"), [
                "traceStart", "advancePcAndCount", "rejectStatic", "popValueOffsetLength", "checkInitCodeLimit", "checkedCeilingWords", "chargeCreateAccessAndInitWords", "expandAndChargeInitMemory", "checkDepth", "loadExactOwnedInitCode", "checkBalance", "checkNonce", "deriveAddressOracle", "warmDestination", "classifyPhysicalLogicalCollision", "chargeCreateState", "traceFinishBeforeReservation", "reserveEip150AllExecution", "incrementCreatorNonce", "snapshotWorld", "analyzeInitCode", "collisionBurnAndRefillOrStageChild", "skipGenericCreateTraceClosure", "resumeRefundChildGas", "chargeParentDepositExecution", "chargeParentDepositState", "commitChild", "repayStateSpill", "pushAddressBeforeResumeTrace"]),
            D("create2", "CREATE2", 0xf5, "create", "CreateOpcode<EvmInstructions.OpCreate2,TTracingInst,OnFlag,EvmInstructions.CreateSpec<OnFlag,OnFlag,OnFlag,Eip8038On>>", "EvmInstructions.InstructionCreate<EthereumGasPolicy,EvmInstructions.OpCreate2,TTracingInst,OnFlag,EvmInstructions.CreateSpec<OnFlag,OnFlag,OnFlag,Eip8038On>>", 4, 1, false, true, S("create2", "explicit", "derivedCreate2", "createdAddress"), [
                "traceStart", "advancePcAndCount", "rejectStatic", "popValueOffsetLength", "popSalt", "checkInitCodeLimit", "checkedCeilingWords", "chargeCreateAccessInitAndHashWords", "expandAndChargeInitMemory", "checkDepth", "loadExactOwnedInitCode", "checkBalance", "checkNonce", "deriveAddressHashOracle", "warmDestination", "classifyPhysicalLogicalCollision", "chargeCreateState", "traceFinishBeforeReservation", "reserveEip150AllExecution", "incrementCreatorNonce", "snapshotWorld", "analyzeInitCode", "collisionBurnAndRefillOrStageChild", "skipGenericCreateTraceClosure", "resumeRefundChildGas", "chargeParentDepositExecution", "chargeParentDepositState", "commitChild", "repayStateSpill", "pushAddressBeforeResumeTrace"]),
            D("selfdestruct", "SELFDESTRUCT", 0xff, "selfdestruct", "SelfDestructOpcode<OnFlag,OnFlag,EvmInstructions.SelfDestructSpec<EvmInstructions.AccessSpec<OnFlag,Eip8038On>,OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>", "EvmInstructions.InstructionSelfDestruct<EthereumGasPolicy,OnFlag,OnFlag,EvmInstructions.SelfDestructSpec<EvmInstructions.AccessSpec<OnFlag,Eip8038On>,OnFlag,OnFlag,OnFlag,OnFlag,Eip8038On>>", 1, 0, true, false, S("selfdestruct", "none", "beneficiary", "stop"), [
                "traceStart", "advancePcAndCount", "rejectStatic", "chargeSelfDestructBase", "popBeneficiary", "normalizeBeneficiaryLow160", "warmAndChargeBeneficiary", "markDestroyIfCreatedThisTransaction", "readBalance", "traceAction", "classifyBeneficiary", "chargeAccountWrite", "chargeNewAccountState", "createOrCreditBeneficiary", "applySelfTargetException", "appendSelfDestructLog", "debitSource", "terminalStop", "outerTerminalTraceClosure"]),
        ];
        List<OpcodeSpecialization> roots = new(28);
        foreach (OpcodeDescriptor opcode in opcodes)
            foreach (DispatchTableBinding table in tables)
            {
                string body = opcode.HandlerBody.Replace("TTracingInst", table.TracingFlag, StringComparison.Ordinal);
                string continuable = opcode.Terminates ? "OffFlag" : "OnFlag";
                roots.Add(new(opcode.Name, table.Name, table.TracingFlag, table.CancelableFlag,
                    $"Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.ExecuteOpcode<{body},{table.TracingFlag},{table.CancelableFlag},{continuable}>"));
            }
        return new(
            1,
            ExtractorVersion,
            KernelName,
            "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>",
            "Nethermind.Evm.GasPolicy.EthereumGasPolicy",
            "Nethermind.Specs.Forks.Amsterdam",
            "Directory.Build.targets: EnableZkEvm != true selects *.std.cs and excludes *.zkevm.cs",
            new(0, 11300, 2300, 100, 3000, 12000, 2, 6, 9000, 5000, 183600, 183600, 6, 1530, 3, 512, 2147483616, 131072, 24576, 1024, 1024,
                "115792089237316195423570985008687907853269984665640564039457584007913129639936"),
            new(true, true, true, true, true, true, true, true, true, true),
            new(true, true, true, true, true, true),
            tables,
            opcodes,
            [.. roots],
            ForkLineage,
            [
                "CREATE and CREATE2 address derivation, RLP, init-code hashing, and Keccak are explicit oracles; no cryptographic implementation equivalence is claimed.",
                "Delegation discovery, code-cache behavior, runtime-code validation, precompile execution, and arbitrary child-frame execution are explicit oracle inputs.",
                "The generated trace projection claims event kind, order, and ownership only; capability-dependent stack, memory, return-data, and action payload equivalence remains open.",
                "World-state and journal tokens assume the admitted IWorldState, StackAccessTracker, VmState, and code-repository adapters implement the represented reads, snapshots, restores, merges, and writes.",
                "Roslyn exact-syntax admission does not prove C# compilation, CLR/JIT/AOT execution, function-pointer tail calls, unsafe stack layout, pooled allocation, native code, or hardware behavior.",
                "Tracing callbacks are assumed total and non-throwing; cancellation delivery, metrics counters, transaction and block composition, trie/database persistence, and zkEVM sources remain outside this slice.",
            ]);
    }

    private static OpcodeDescriptor D(string name, string instruction, int opcodeByte, string family,
        string handlerBody, string target, int inputs, int outputs, bool terminates,
        bool ownsPreChildTraceEnd, OpcodeSemantics semantics, string[] order) =>
        new(name, instruction, opcodeByte, family, handlerBody, target, inputs, outputs, terminates,
            ownsPreChildTraceEnd, semantics, order);

    private static OpcodeSemantics S(string operation, string valueRule, string targetRule,
        string resultRule) => new(operation, valueRule, targetRule, resultRule);

    private static string[] SemanticBindings() =>
    [
        "7 opcodes and 4 trace/cancellation tables yield exactly 28 closed generic roots",
        "CALL/CALLCODE consume explicit value; DELEGATECALL inherits environment value; STATICCALL uses zero",
        "CALL memory and access ordering precedes NEW_ACCOUNT state charge and EIP-150 execution reservation",
        "CREATE/CREATE2 own the pre-reservation trace end; generic dispatch and suspend closure skip it",
        "CompleteCreateWithoutChild closes depth, balance, and nonce continuations exactly once after pushing zero",
        "CREATE collision follows reservation and nonce increment, burns forwarded execution, refills CREATE state gas, and stages no child",
        "child success, REVERT, exception, and code-deposit failure use distinct gas and journal merge paths before parent result push",
        "Every CALL-family code-source word is reduced to its low 160 bits before use; RIPEMD160 aliases are excluded from the inline untraced STATICCALL route and direct-precompile remaining gas cannot exceed the reserved child gas",
        "CREATE success refunds child gas into the parent before parent code-deposit execution/state charges, commit, and state-spill repayment",
        "CALL suspend is closed by RunByteCode; CREATE suspend is already closed before reservation; collision traces its one-byte zero push without a second finish",
        "continuation trace projection records result-event ordering before the resume finish without claiming capability-dependent payload equivalence",
        "instruction tracing and action tracing select empty/precompile fast paths independently and action completion precedes parent result push",
        "SELFDESTRUCT charges base before stack pop and beneficiary access; ACCOUNT_WRITE precedes NEW_ACCOUNT state gas",
        "cryptographic address derivation, precompile execution, runtime-code validation, child execution, and concrete world journals are explicit premises",
    ];

    private static AdmissionSpec[] BuildAdmissionSpecs()
    {
        List<AdmissionSpec> result =
        [
            A("src/Nethermind/Nethermind.Core/GasCostOf.cs", "Nethermind.Core", "namespace", "type", "GasCostOf"),
            A("src/Nethermind/Nethermind.Core/Eip8037Constants.cs", "Nethermind.Core", "namespace", "type", "Eip8037Constants"),
            A("src/Nethermind/Nethermind.Core/Eip8038Constants.cs", "Nethermind.Core", "namespace", "type", "Eip8038Constants"),
            A("src/Nethermind/Nethermind.Core/TypeFlags.cs", "Nethermind.Core", "namespace", "type", "IFlag"),
            A("src/Nethermind/Nethermind.Core/TypeFlags.cs", "Nethermind.Core", "namespace", "type", "OffFlag"),
            A("src/Nethermind/Nethermind.Core/TypeFlags.cs", "Nethermind.Core", "namespace", "type", "OnFlag"),
            A("src/Nethermind/Nethermind.Core/Specs/IReleaseSpec.cs", "Nethermind.Core.Specs", "namespace", "type", "IReleaseSpec"),
            A("src/Nethermind/Nethermind.Core/Specs/IReleaseSpecExtensions.cs", "Nethermind.Core.Specs", "namespace", "type", "IReleaseSpecExtensions"),
            A("src/Nethermind/Nethermind.Core/Specs/SpecGasCosts.cs", "Nethermind.Core.Specs", "namespace", "type", "SpecGasCosts"),
            A(BytesPath, "Nethermind.Core.Extensions", "namespace", "type", "Bytes"),
            A(BytesStandardPath, "Nethermind.Core.Extensions", "namespace", "type", "Bytes"),
            A(EvmWordExtensionsPath, "Nethermind.Core.Extensions", "namespace", "type", "EvmWordExtensions"),
            A("src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs", "Nethermind.Evm", "namespace", "type", "DispatchFlags"),
            A("src/Nethermind/Nethermind.Evm/Instruction.cs", "Nethermind.Evm", "namespace", "type", "Instruction"),
            A("src/Nethermind/Nethermind.Evm/Instructions/EvmCalculations.cs", "Nethermind.Evm", "namespace", "type", "EvmCalculations"),
            A(StackPath, "Nethermind.Evm", "namespace", "type", "EvmStack"),
            A(StackPath, "Nethermind.Evm", "EvmStack`0", "method", "PopAddress", 0, "PoppedAddressCache"),
            A(StackStandardPath, "Nethermind.Evm", "namespace", "type", "EvmStack"),
            A(MemoryPath, "Nethermind.Evm", "namespace", "type", "EvmPooledMemory"),
            A("src/Nethermind/Nethermind.Evm/EvmFrameMemory.cs", "Nethermind.Evm", "namespace", "type", "EvmFrameMemory"),
            A("src/Nethermind/Nethermind.Evm/StackPool.cs", "Nethermind.Evm", "namespace", "type", "StackPool"),
            A("src/Nethermind/Nethermind.Evm/StackPool.std.cs", "Nethermind.Evm", "namespace", "type", "StackPool"),
            A("src/Nethermind/Nethermind.Evm/VmState.cs", "Nethermind.Evm", "namespace", "type", "VmState", 1),
            A("src/Nethermind/Nethermind.Evm/ExecutionEnvironment.cs", "Nethermind.Evm", "namespace", "type", "ExecutionEnvironment"),
            A("src/Nethermind/Nethermind.Evm/ExecutionType.cs", "Nethermind.Evm", "namespace", "type", "ExecutionType"),
            A("src/Nethermind/Nethermind.Evm/VirtualMachine.CallResult.cs", "Nethermind.Evm", "namespace", "type", "VirtualMachine", 1),
            A("src/Nethermind/Nethermind.Evm/StackAccessTracker.cs", "Nethermind.Evm", "namespace", "type", "StackAccessTracker"),
            A("src/Nethermind/Nethermind.Evm/ContractAddress.cs", "Nethermind.Evm", "namespace", "type", "ContractAddress"),
            A("src/Nethermind/Nethermind.Evm/ICodeInfoRepository.cs", "Nethermind.Evm", "namespace", "type", "ICodeInfoRepository"),
            A("src/Nethermind/Nethermind.Evm/CodeInfoRepository.cs", "Nethermind.Evm", "namespace", "type", "CodeInfoRepository"),
            A("src/Nethermind/Nethermind.Evm/CodeAnalysis/CodeInfo.cs", "Nethermind.Evm.CodeAnalysis", "namespace", "type", "CodeInfo"),
            A("src/Nethermind/Nethermind.Evm/CodeAnalysis/CodeInfoFactory.cs", "Nethermind.Evm.CodeAnalysis", "namespace", "type", "CodeInfoFactory"),
            A("src/Nethermind/Nethermind.Evm/CodeDepositHandler.cs", "Nethermind.Evm", "namespace", "type", "CodeDepositHandler"),
            A("src/Nethermind/Nethermind.Evm/GasPolicy/IGasCost.cs", "Nethermind.Evm.GasPolicy", "namespace", "type", "IGasCost"),
            A(GasInterfacePath, "Nethermind.Evm.GasPolicy", "namespace", "type", "IGasPolicy", 1),
            A(GasPath, "Nethermind.Evm.GasPolicy", "namespace", "type", "EthereumGasPolicy"),
            A("src/Nethermind/Nethermind.Evm/GasPolicy/AccountAccessKind.cs", "Nethermind.Evm.GasPolicy", "namespace", "type", "AccountAccessKind"),
            A("src/Nethermind/Nethermind.Evm/GasPolicy/AccountAccessPricingKernel.cs", "Nethermind.Evm.GasPolicy", "namespace", "type", "AccountAccessPricingKernel"),
            A("src/Nethermind/Nethermind.Evm/GasPolicy/StateGasChargeKernel.cs", "Nethermind.Evm.GasPolicy", "namespace", "type", "StateGasChargeKernel"),
            A("src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs", "Nethermind.Evm.GasPolicy", "namespace", "type", "StateGasTransitionKernel"),
            A("src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionAdapterKernel.cs", "Nethermind.Evm.GasPolicy", "namespace", "type", "StateGasTransitionAdapterKernel"),
            A(SpecPath, "Nethermind.Evm", "namespace", "type", "EvmInstructions"),
            A(CallPath, "Nethermind.Evm", "namespace", "type", "EvmInstructions"),
            A(CallPath, "Nethermind.Evm", "EvmInstructions`0", "method", "InstructionCall", 6, "EvmStack,TGasPolicy,VirtualMachine<TGasPolicy>"),
            A(CallPath, "Nethermind.Evm", "EvmInstructions`0", "method", "CreateFullCallFrame", 3, "VirtualMachine<TGasPolicy>,EvmStack,TGasPolicy,UInt256,UInt256,UInt256,UInt256,CodeInfo,Address,Address,Address,ExecutionEnvironment,UInt256,ulong,bool"),
            A(CallStandardPath, "Nethermind.Evm", "namespace", "type", "EvmInstructions"),
            A(CallStandardPath, "Nethermind.Evm", "EvmInstructions`0", "method", "TryInlineStaticPrecompileCall", 2, "VirtualMachine<TGasPolicy>,EvmStack,TGasPolicy,UInt256,UInt256,UInt256,UInt256,IPrecompile,Address,Address,ulong,EvmExceptionType"),
            A(CreatePath, "Nethermind.Evm", "namespace", "type", "EvmInstructions"),
            A(CreatePath, "Nethermind.Evm", "EvmInstructions`0", "method", "InstructionCreate", 5, "EvmStack,TGasPolicy,VirtualMachine<TGasPolicy>"),
            A(CreatePath, "Nethermind.Evm", "EvmInstructions`0", "method", "CompleteCreateWithoutChild", 2, "EvmStack,TGasPolicy,VirtualMachine<TGasPolicy>"),
            A(ControlFlowPath, "Nethermind.Evm", "namespace", "type", "EvmInstructions"),
            A(ControlFlowPath, "Nethermind.Evm", "EvmInstructions`0", "method", "InstructionSelfDestruct", 4, "EvmStack,TGasPolicy,VirtualMachine<TGasPolicy>"),
            A("src/Nethermind/Nethermind.Evm/State/IWorldState.cs", "Nethermind.Evm.State", "namespace", "type", "IWorldState"),
            A("src/Nethermind/Nethermind.Evm/State/WorldStateExtensions.cs", "Nethermind.Evm.State", "namespace", "type", "WorldStateExtensions"),
            A("src/Nethermind/Nethermind.Evm/Tracing/ITxTracer.cs", "Nethermind.Evm.Tracing", "namespace", "type", "ITxTracer"),
            A("src/Nethermind/Nethermind.Evm/Tracing/TracerExtensions.cs", "Nethermind.Evm.Tracing", "namespace", "type", "TracerExtensions"),
            A("src/Nethermind/Nethermind.Evm/Tracing/TraceStack.cs", "Nethermind.Evm.Tracing", "namespace", "type", "TraceStack"),
            A("src/Nethermind/Nethermind.Evm/Tracing/TraceMemory.cs", "Nethermind.Evm.Tracing", "namespace", "type", "TraceMemory"),
            A(DispatchPath, "Nethermind.Evm", "namespace", "type", "VirtualMachine", 1),
            A(DispatchPath, "Nethermind.Evm", "VirtualMachine`1", "method", "ExecuteOpcode", 4, "EvmStack,TGasPolicy,DispatchState,nint,int"),
            A(OpcodeHandlersPath, "Nethermind.Evm", "namespace", "type", "VirtualMachine", 1),
            A(OpcodeHandlersPath, "Nethermind.Evm", "VirtualMachine`1", "type", "IOpcodeBody"),
            A(OpcodeHandlersPath, "Nethermind.Evm", "VirtualMachine`1", "type", "CreateOpcode", 4),
            A(OpcodeHandlersPath, "Nethermind.Evm", "VirtualMachine`1", "type", "CallOpcode", 5),
            A(OpcodeHandlersPath, "Nethermind.Evm", "VirtualMachine`1", "type", "SelfDestructOpcode", 3),
            A(VmPath, "Nethermind.Evm", "namespace", "type", "VirtualMachine", 1),
            A(VmPath, "Nethermind.Evm", "VirtualMachine`1", "method", "ExecuteTransaction", 1, "VmState<TGasPolicy>,IWorldState,ITxTracer"),
            A(VmPath, "Nethermind.Evm", "VirtualMachine`1", "method", "HandleCreate", 0, "CallResult,VmState<TGasPolicy>,ulong,bool"),
            A(VmPath, "Nethermind.Evm", "VirtualMachine`1", "method", "TryChargeAndDepositCode", 0, "VmState<TGasPolicy>,ulong,bool,ulong,long,bool,ReadOnlyMemory<byte>"),
            A(VmPath, "Nethermind.Evm", "VirtualMachine`1", "method", "CanExecutePrecompileCallDirectly", 0, "IPrecompile,Address"),
            A(VmPath, "Nethermind.Evm", "VirtualMachine`1", "method", "TryRunPrecompileDirectly", 0, "IPrecompile,ReadOnlyMemory<byte>,IReleaseSpec,Result<byte[]>"),
            A(VmPath, "Nethermind.Evm", "VirtualMachine`1", "method", "HandleRevert", 0, "VmState<TGasPolicy>,CallResult,nuint"),
            A(VmPath, "Nethermind.Evm", "VirtualMachine`1", "method", "HandleException", 0, "CallResult,nuint,bool"),
            A(VmPath, "Nethermind.Evm", "VirtualMachine`1", "method", "PopAndRestoreParentState"),
            A(VmPath, "Nethermind.Evm", "VirtualMachine`1", "method", "PrepareNextCallFrame", 0, "CallResult,nuint"),
            A(VmPath, "Nethermind.Evm", "VirtualMachine`1", "method", "ExecuteCall", 1, "(Address?CreatedAddress,bool?Success),nuint,UInt256"),
            A(VmPath, "Nethermind.Evm", "VirtualMachine`1", "method", "RunByteCode", 2, "EvmStack,TGasPolicy"),
            A("src/Nethermind/Nethermind.Evm/VirtualMachine.std.cs", "Nethermind.Evm", "namespace", "type", "VirtualMachine", 1),
            A("src/Nethermind/Nethermind.Evm/VirtualMachine.ExecutionHandlers.cs", "Nethermind.Evm", "namespace", "type", "VirtualMachine", 1),
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
        foreach (string segment in ownerPath.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = segment.LastIndexOf('`');
            if (separator <= 0 || !int.TryParse(segment.AsSpan(separator + 1), out int arity))
                throw new ExtractionException($"Invalid exact generic owner segment '{segment}' in {sourcePath}.");
            string name = segment[..separator];
            TypeDeclarationSyntax[] owners = DirectMembers(current).OfType<TypeDeclarationSyntax>()
                .Where(type => type.Identifier.ValueText == name && GenericArity(type) == arity).ToArray();
            if (owners.Length != 1)
                throw new ExtractionException($"Expected exact owner {segment} in {sourcePath}; found {owners.Length}.");
            current = owners[0];
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
        CallCreateOpcodeLeanEmitter.ValidateSha256(value, description);

    private static void WriteDeterministic(string path, byte[] bytes)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return;
        File.WriteAllBytes(path, bytes);
    }
}
