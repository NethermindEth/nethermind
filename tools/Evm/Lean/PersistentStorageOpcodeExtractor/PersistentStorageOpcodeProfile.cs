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

namespace Nethermind.Evm.Lean.PersistentStorageOpcodeExtractor;

internal static class PersistentStorageOpcodeProfile
{
    internal const string InstructionPath = "src/Nethermind/Nethermind.Evm/Instruction.cs";
    internal const string HandlersPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs";
    internal const string DispatchPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs";
    internal const string StorageInstructionsPath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Storage.cs";
    internal const string GasCostPath = "src/Nethermind/Nethermind.Core/GasCostOf.cs";
    internal const string Eip8038ConstantsPath = "src/Nethermind/Nethermind.Core/Eip8038Constants.cs";
    internal const string RefundPath = "src/Nethermind/Nethermind.Core/RefundOf.cs";
    internal const string SpecGasCostsPath = "src/Nethermind/Nethermind.Core/Specs/SpecGasCosts.cs";
    internal const string GasPolicyInterfacePath = "src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs";
    internal const string EthereumGasPolicyPath = "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs";
    internal const string PricingKernelPath = "src/Nethermind/Nethermind.Evm/GasPolicy/SStorePricingKernel.cs";
    internal const string StateChargeKernelPath = "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasChargeKernel.cs";
    internal const string StateTransitionAdapterPath = "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionAdapterKernel.cs";
    internal const string ReleaseExtensionsPath = "src/Nethermind/Nethermind.Core/Specs/IReleaseSpecExtensions.cs";
    internal const string StandardReleaseExtensionsPath = "src/Nethermind/Nethermind.Core/Specs/IReleaseSpecExtensions.std.cs";
    internal const string ReleaseInterfacePath = "src/Nethermind/Nethermind.Core/Specs/IReleaseSpec.cs";
    internal const string ReleaseSpecPath = "src/Nethermind/Nethermind.Specs/ReleaseSpec.cs";
    internal const string NamedReleaseSpecPath = "src/Nethermind/Nethermind.Specs/Forks/NamedReleaseSpec.cs";
    internal const string VirtualMachinePath = "src/Nethermind/Nethermind.Evm/VirtualMachine.cs";
    internal const string StandardVirtualMachinePath = "src/Nethermind/Nethermind.Evm/VirtualMachine.std.cs";
    internal const string StandardDispatchFlagsPath = "src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs";
    internal const string StandardSpecFlagsPath = "src/Nethermind/Nethermind.Evm/SpecFlags.std.cs";
    internal const string TypeFlagsPath = "src/Nethermind/Nethermind.Core/TypeFlags.cs";
    internal const string StackPath = "src/Nethermind/Nethermind.Evm/EvmStack.cs";
    internal const string AccessTrackerPath = "src/Nethermind/Nethermind.Evm/StackAccessTracker.cs";
    internal const string StorageAccessTypePath = "src/Nethermind/Nethermind.Evm/Instructions/StorageAccessType.cs";
    internal const string MainnetDiPath = "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs";
    internal const string WorldStateInterfacePath = "src/Nethermind/Nethermind.Evm/State/IWorldState.cs";
    internal const string StorageCellPath = "src/Nethermind/Nethermind.Core/StorageCell.cs";
    internal const string VmStatePath = "src/Nethermind/Nethermind.Evm/VmState.cs";
    internal const string ExecutionEnvironmentPath = "src/Nethermind/Nethermind.Evm/ExecutionEnvironment.cs";
    internal const string BuildTargetsPath = "src/Nethermind/Directory.Build.targets";
    internal const string DefaultOutputRelativePath = "tools/Evm/Lean/PersistentStorageOpcodeExtractor/Generated";
    internal const string DefaultLeanRelativePath = "tools/Evm/Lean/PersistentStorageOpcodeExtractor/Generated/PersistentStorageOpcodeKernel.lean";
    internal const string IrFileName = "PersistentStorageOpcodeKernel.ir.json";
    internal const string ManifestFileName = "PersistentStorageOpcodeKernel.source-manifest.json";

    private const int SchemaVersion = 1;
    private const string ExtractorVersion = "1.0.0";
    private const string Kernel = "Nethermind standard-mainnet Amsterdam SLOAD/SSTORE immediate-handler slice";

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
        InstructionPath, HandlersPath, DispatchPath, StorageInstructionsPath,
        GasCostPath, Eip8038ConstantsPath, RefundPath, SpecGasCostsPath,
        GasPolicyInterfacePath, EthereumGasPolicyPath, PricingKernelPath,
        StateChargeKernelPath, StateTransitionAdapterPath, ReleaseExtensionsPath,
        StandardReleaseExtensionsPath, ReleaseInterfacePath, ReleaseSpecPath,
        NamedReleaseSpecPath, VirtualMachinePath, StandardVirtualMachinePath,
        StandardDispatchFlagsPath, StandardSpecFlagsPath, TypeFlagsPath, StackPath,
        AccessTrackerPath, StorageAccessTypePath, MainnetDiPath, WorldStateInterfacePath,
        StorageCellPath, VmStatePath, ExecutionEnvironmentPath, BuildTargetsPath,
        .. ForkPaths,
    ];

    private const string ExpectedFingerprintText = """
        src/Nethermind/Nethermind.Evm/Instruction.cs 6895d06277ac4f9369d7bdc748976576bf384f6f3a5a348a2e4fd3767a46a030
        src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs 5148dd594e40ebb976179b1048f233914f54acc8d033d358cf3e2e7594e112b6
        src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs 4f36bb20057caec9c85bcd3621372563f4d47ba36a9cbf01be267af4e379bca1
        src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Storage.cs 0101effa0c71c4a602021f18de7075e047974b75b37b9d672364499db0bf390d
        src/Nethermind/Nethermind.Core/GasCostOf.cs 10f1f1a29e7bf10a8220422a6f7571af60df4f05ad4075268e3b26559a73c6f5
        src/Nethermind/Nethermind.Core/Eip8038Constants.cs e057620777fce018901690c428ecdf33fdfc5e0ba903544edd4f51724826171a
        src/Nethermind/Nethermind.Core/RefundOf.cs db11ff39352957db40e56de2862daace42df0ef6510a96ed510ec8f0f0128a4d
        src/Nethermind/Nethermind.Core/Specs/SpecGasCosts.cs 51cc74d453997f05752e23b458be86adc2d94c55b4b450d565d21d6a92546879
        src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs b4d26db87723243514f2f6b8a5e48d1176139105c3b012ad0f3956e59afc98a8
        src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs 3c31ba40c24a78bf11243b38859168350f02934806124e913da0560bc1a4350f
        src/Nethermind/Nethermind.Evm/GasPolicy/SStorePricingKernel.cs 6eb99aee5247c9d5d7c35659c41628d758b672923d0fc5ec76e7d5cb9a30a125
        src/Nethermind/Nethermind.Evm/GasPolicy/StateGasChargeKernel.cs ec3276958cbc6b0255fadedf15dbe49691195aa95570b332937f8d1703b103fd
        src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionAdapterKernel.cs ca16429e4cb1b728619b7fa0113b2302ca75600b0a32d9345e38721c7c2d7368
        src/Nethermind/Nethermind.Core/Specs/IReleaseSpecExtensions.cs 5a22587aa81b5f0e4276e61bc1970ff89e4e4cecbed8c9b08dd338576c7d041a
        src/Nethermind/Nethermind.Core/Specs/IReleaseSpecExtensions.std.cs fb2c891d70641e8cafcd6bf6d3e2548fb74f1099d698ee7f595dd53b2f9acdb7
        src/Nethermind/Nethermind.Core/Specs/IReleaseSpec.cs 3f44b3da5614c94d59eae2e4a6266a9e698614f75a7b5a9894a4828b0021b18a
        src/Nethermind/Nethermind.Specs/ReleaseSpec.cs 7a34da425c0ed0a7ba6776fd43262235170211f93e9563013ddc47e31651b7dc
        src/Nethermind/Nethermind.Specs/Forks/NamedReleaseSpec.cs ea9400ed44270fb18766a5dbd20d4dd7e7ee95b1bb7ab7ee6d5406cca03a46cc
        src/Nethermind/Nethermind.Evm/VirtualMachine.cs 45edba3691e09185e749485785990662ddea1af849bc6e4564f92b68a5137a6b
        src/Nethermind/Nethermind.Evm/VirtualMachine.std.cs 357fbd35369424a379c8c29338f491627512d40a12199d7a3d9aac44c87ca92a
        src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs 99c58367e5b90fa5904484f6e2fbf10745a54f6cd7d07275810c27028ad0eb2b
        src/Nethermind/Nethermind.Evm/SpecFlags.std.cs 653891cfe2289e874dcdd98c9401b3b319637d5241fd9a4d6cd7ef1c146ee5a8
        src/Nethermind/Nethermind.Core/TypeFlags.cs 33d8404d3ec39fffbc9f665126255f9799a0ba433206a141f480f8144be95747
        src/Nethermind/Nethermind.Evm/EvmStack.cs 561520be4f0fe1d533ddea9cfea7ff48edb2745b1f3adf1b45c24e6c0bed02ad
        src/Nethermind/Nethermind.Evm/StackAccessTracker.cs ed5cdfdfe2ebf4d8c9f651572927efe7bdb5c75107db63a40577c5ec3ec2882f
        src/Nethermind/Nethermind.Evm/Instructions/StorageAccessType.cs 74e886f06c42aaaf70498a45e11dc1f3a04b2406a6f788063df0b9a54f221e12
        src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs fce65ce5bb523c56fc8aa6ee0e4d092c940a5a90b174c820ffe262eeb5241c60
        src/Nethermind/Nethermind.Evm/State/IWorldState.cs 0680ebc7168472645d93df58d2b3240508c177361815c85cceb09cf960f189c7
        src/Nethermind/Nethermind.Core/StorageCell.cs 0221aaa2ea9b31a114e256de271c538ff8676844a75c8965160bc8bfcfc7bf55
        src/Nethermind/Nethermind.Evm/VmState.cs 7b7b6ddb753118b426d14976a3fe8e79f808320d6a09ea9cace9df19197f9f23
        src/Nethermind/Nethermind.Evm/ExecutionEnvironment.cs c076225b688b1e04ed83cddc90a2ddbda69862333dc927945eb32d376e43e1db
        src/Nethermind/Directory.Build.targets 0598cebaef1df41102a18a3f9ace810bed1e4e64b8471d055724b4a394dccec6
        src/Nethermind/Nethermind.Specs/Forks/00_Olympic.cs 8874f5ed3ae7bf5a0e0d1d127babd258393abcc3093b98abc296a1a347d61246
        src/Nethermind/Nethermind.Specs/Forks/01_Frontier.cs 28318120065dc43c084c998b4cdef83669fa7b036ebe850802f0f9ff860df075
        src/Nethermind/Nethermind.Specs/Forks/02_Homestead.cs 52dae1ff91fcc53fc95e4dc9adab9ce9686411620b88135a408728d56d6b0ad2
        src/Nethermind/Nethermind.Specs/Forks/03_Dao.cs 9354856fe91fde3d1394828e94bb700b6c765b9d42425221b6e1554898c34455
        src/Nethermind/Nethermind.Specs/Forks/04_TangerineWhistle.cs 82a6ea651cb884be41e1105d73cf726e1f095195322bf3293f663658da40939f
        src/Nethermind/Nethermind.Specs/Forks/05_SpuriousDragon.cs 7fb950f37739ad6435c4dc5bcd3bf9b9566d3a6137479675276cea5a07196af5
        src/Nethermind/Nethermind.Specs/Forks/06_Byzantium.cs c47fcc9740483de8081c4591aa6b2994271602dacc0a0fb590e63e39f0288e73
        src/Nethermind/Nethermind.Specs/Forks/07_Constantinople.cs a0ee60c7fb44400a18e1b5d6cc3fe324de557023c34f5c7427ecaf72da17cfbe
        src/Nethermind/Nethermind.Specs/Forks/08_ConstantinopleFix.cs 1a0336f8a06e64f40cdfbba3ecac79158ddf9f38d9694df346916c819819a6a5
        src/Nethermind/Nethermind.Specs/Forks/09_Istanbul.cs 911764bbfd46d09c055d8a5a0a699c62b65ac96a528f76c294044a4c84b84714
        src/Nethermind/Nethermind.Specs/Forks/10_MuirGlacier.cs 76508e7a64c3bf94a8da0145fedb4f277c1d06e75454a53d1c2d036fc9e9f5e8
        src/Nethermind/Nethermind.Specs/Forks/11_Berlin.cs b02b45deb5dedddc9df19283c7491ffc3ab10ab0aaae313e44cdafca9ee547b0
        src/Nethermind/Nethermind.Specs/Forks/12_London.cs 06ea8bd017ccff59759026a801138bd794919e8493d49f8ef05385276662f8ef
        src/Nethermind/Nethermind.Specs/Forks/13_ArrowGlacier.cs 267c4b5529156e692ac1d1827f189b988f07d398bda986befef9561234c94491
        src/Nethermind/Nethermind.Specs/Forks/14_GrayGlacier.cs a3904920bc25153c271057fe3b990b183c9cf93bba3a393c1f7561c750907a2e
        src/Nethermind/Nethermind.Specs/Forks/15_Paris.cs 90c0f08765e2b50a8fadc2fdfcbe43965b9352e0a17ac692676403eb95972abd
        src/Nethermind/Nethermind.Specs/Forks/16_Shanghai.cs dcbfdacf3b6c7a8db0e8d5abc1ad3c388d407ae989395d52f1725721a5cc767e
        src/Nethermind/Nethermind.Specs/Forks/17_Cancun.cs 29b93ca43e919352495a0406242d7b49ce9423117aeec2e1bb94427fff20aa79
        src/Nethermind/Nethermind.Specs/Forks/18_Prague.cs 949248f5f22162433e7f4e17a7e5a35b4cf54416836738831edd3b1c0e5a6b8c
        src/Nethermind/Nethermind.Specs/Forks/19_Osaka.cs 24da07f0c85e3c5822c10f010c4b35a5ca21046ac6b21f5bb26397de2ef5cf77
        src/Nethermind/Nethermind.Specs/Forks/20_BPO1.cs 6fc6d2c0fc7159882c03ad44006da8aad1354d68d2d1fe750ef8a551b43bbad8
        src/Nethermind/Nethermind.Specs/Forks/21_BPO2.cs 4788ce812f73163686b2b419970d70bdae4de73523fe6ad426a618dd1f1d40d9
        src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs 4dfbf9079e9dce30423327ae4433f32e49388d403a8fedfcf253f94f79450c6a
        """;

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
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] ExpectedSemanticBindings =
    [
        "SLOAD=0x54 and SSTORE=0x55 are unique byte-valued Instruction members.",
        "Amsterdam's complete named-fork ancestry enables EIP-2200, EIP-2929, EIP-8037, and EIP-8038.",
        "The standard build and mainnet DI close VirtualMachine over EthereumGasPolicy.",
        "The operational refinement is restricted to normal consensus execution with VM.IsTracingAccess=false; access-list tracing follows a distinct warm-cost path.",
        "Four tracing/cancellation tables contribute eight exact SLOAD/SSTORE ExecuteOpcode roots.",
        "SLOAD and the joint EIP-8037/EIP-8038 SSTORE wrapper delegate directly to the admitted handlers.",
        "The admitted handler order covers stack, access warming, current/original reads, pricing, execution/state gas, refund/refill, failure, and final write.",
    ];

    internal static IReadOnlyList<string> SourcePaths => SourceRelativePaths;

    internal static ExtractionResult Extract(string repoRoot, string outputDirectory, string? leanOutputPath = null)
    {
        string canonicalRoot = Path.GetFullPath(repoRoot);
        Dictionary<string, SourceFile> sources = LoadSources(canonicalRoot);
        ValidateInstructionBytes(Get(sources, InstructionPath));
        ValidateScheduleAndGasPolicy(sources);
        ValidateForkLineage(sources);
        ValidateStandardMainnetReachability(sources);
        ValidateHandlers(sources);
        ValidateWorldStateBoundary(sources);
        ValidatePinnedSourceFingerprints(sources);

        OpcodeDescriptor[] opcodes =
        [
            new(
                "sload", "SLOAD", 0x54,
                "SLoadOpcode<TTracingInst,Eip8038On,OnFlag>",
                "EvmInstructions.InstructionSLoad<TGasPolicy,TTracingInst,Eip8038On,OnFlag>",
                1, 1, "always assigned; Amsterdam closes EIP-2929 and EIP-8038 on",
                ["advancePc", "chargeZeroBase", "popKey", "chargeAndWarmAccess", "readCurrent", "pushValue"]),
            new(
                "sstore", "SSTORE", 0x55,
                "SStoreMeteredOpcode<TTracingInst,OnFlag,OnFlag,Eip8038On,OnFlag>",
                "EvmInstructions.InstructionSStoreMetered<TGasPolicy,TTracingInst,OnFlag,OnFlag,Eip8038On,OnFlag>",
                2, 0, "always assigned; Amsterdam closes EIP-2200, EIP-2929, EIP-8037, and EIP-8038 on",
                ["advancePc", "staticCheck", "stipendCheck", "popKey", "popValue", "chargeAndWarmAccess",
                 "readCurrent", "readOriginalOnChange", "priceAfterAccess", "chargeExecutionThenState",
                 "clearRefund", "clearRefundReversal", "stateRefill", "restoreOriginalRefund", "writeOnChange"]),
        ];
        DispatchSpecialization[] specializations =
        [
            Specialization("sload", "NoTrace", "OffFlag", "OffFlag", "SLoadOpcode<OffFlag,Eip8038On,OnFlag>"),
            Specialization("sstore", "NoTrace", "OffFlag", "OffFlag", "SStoreMeteredOpcode<OffFlag,OnFlag,OnFlag,Eip8038On,OnFlag>"),
            Specialization("sload", "NoTraceCancelable", "OffFlag", "OnFlag", "SLoadOpcode<OffFlag,Eip8038On,OnFlag>"),
            Specialization("sstore", "NoTraceCancelable", "OffFlag", "OnFlag", "SStoreMeteredOpcode<OffFlag,OnFlag,OnFlag,Eip8038On,OnFlag>"),
            Specialization("sload", "Traced", "OnFlag", "OffFlag", "SLoadOpcode<OnFlag,Eip8038On,OnFlag>"),
            Specialization("sstore", "Traced", "OnFlag", "OffFlag", "SStoreMeteredOpcode<OnFlag,OnFlag,OnFlag,Eip8038On,OnFlag>"),
            Specialization("sload", "TracedCancelable", "OnFlag", "OnFlag", "SLoadOpcode<OnFlag,Eip8038On,OnFlag>"),
            Specialization("sstore", "TracedCancelable", "OnFlag", "OnFlag", "SStoreMeteredOpcode<OnFlag,OnFlag,OnFlag,Eip8038On,OnFlag>"),
        ];
        IrDocument ir = new(
            SchemaVersion,
            ExtractorVersion,
            Kernel,
            new ReachabilityDescriptor(
                "IVirtualMachine", "EthereumVirtualMachine", "VirtualMachine<EthereumGasPolicy>",
                "EthereumGasPolicy", "Amsterdam",
                "Directory.Build.targets selects *.std.cs when EnableZkEvm != true",
                "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode"),
            new ExecutionModeDescriptor(
                false,
                "Normal consensus execution only; VM.IsTracingAccess must be false because access-list tracing uses the distinct warm-cost path."),
            new GasScheduleDescriptor(0, 2100, 100, 10000, 11616, 97920, 2300),
            opcodes,
            specializations,
            [
                "Eip803x.Generated.SStorePricingKernel with Eip803x.Refinement.SStorePricing",
                "Eip803x.Generated.StateGasChargeKernel with Eip803x.Refinement.StateGasCharge",
                "Eip803x.Generated.StateGasTransitionAdapterKernel with Eip803x.Refinement.StateGasTransitionAdapterKernel",
            ],
            [
                "The theorem covers the immediate standard-mainnet Amsterdam SLOAD/SSTORE handler abstraction selected by the eight admitted dispatch roots; C#, Roslyn, CLR/JIT, unsafe function-pointer execution, and hardware remain trusted.",
                "UInt256, Address, EvmStack, byte-array normalization, zero canonicalization, span lifetime, and provider-value representation remain adapter premises.",
                "IWorldState Get/GetOriginal/Set correctness, database persistence, storage journaling, snapshots, frame rollback/merge, and decorators remain separate obligations.",
                "StackAccessTracker collection implementation and BAL behavior are abstracted as an extensional warm-cell set; its admitted charge-before-warm path is covered for VM.IsTracingAccess=false, while access-list tracing, provider allocation, and concurrency remain open.",
                "Tracing callbacks, callback failures, metrics, cancellation, opcode counter overflow, transaction refund cap, receipt accounting, and block accounting are outside this handler theorem.",
                "Fixed-width ulong/long overflow is excluded by the refinement's explicit Amsterdam schedule and well-formed production-gas representation premises.",
            ]);

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
        RequireValue(values, "SLOAD", 0x54);
        RequireValue(values, "SSTORE", 0x55);
    }

    private static void ValidateScheduleAndGasPolicy(Dictionary<string, SourceFile> sources)
    {
        RequireCompactContains(Get(sources, GasCostPath), "publicconstulongCallStipend=2300;", "SSTORE stipend must remain 2300");
        RequireCompactContains(Get(sources, GasCostPath), "publicconstulongWarmStateRead=100;", "warm storage access must remain 100");
        RequireCompactContains(Get(sources, GasCostPath), "publicconstlongCostPerStateByte=1530;", "state byte cost must remain 1530");
        RequireCompactContains(Get(sources, GasCostPath), "publicconstlongStateBytesPerStorageSet=64;", "storage set size must remain 64 bytes");
        RequireCompactContains(Get(sources, GasCostPath), "publicconstlongSSetState=StateBytesPerStorageSet*CostPerStateByte;", "storage state gas must derive from bytes and CPSB");
        RequireCompactContains(Get(sources, Eip8038ConstantsPath), "publicconstulongWarmAccess=GasCostOf.WarmStateRead;", "EIP-8038 warm access must use WarmStateRead");
        RequireCompactContains(Get(sources, Eip8038ConstantsPath), "publicconstulongColdStorageAccess=2100;", "EIP-8038 cold storage access must remain 2100");
        RequireCompactContains(Get(sources, Eip8038ConstantsPath), "publicconstulongStorageWrite=10000;", "EIP-8038 STORAGE_WRITE must remain 10000");
        RequireCompactContains(Get(sources, RefundPath), "publicconstulongSClearEip8038=(Eip8038Constants.StorageWrite+Eip8038Constants.ColdStorageAccess)*4800/5000;", "EIP-8038 clear refund formula changed");
        RequireCompactContains(Get(sources, SpecGasCostsPath), "SClearRefund=spec.IsEip8038Enabled?RefundOf.SClearEip8038", "Amsterdam SClearRefund must select EIP-8038");

        SourceFile policy = Get(sources, EthereumGasPolicyPath);
        RequireCompactContains(policy, "Eip2929.IsActive||UpdateGas(refgas,spec.GasCosts.SLoadCost)", "Amsterdam SLOAD base must specialize to zero");
        RequireCompactContains(policy, "Eip8038.IsActive||UpdateGas(refgas,spec.GasCosts.NetMeteredSStoreCost)", "Amsterdam net-metered no-op charge must specialize to zero");
        RequireCompactContains(policy, "TMode.IsEip8038Enabled(spec)?Eip8038Constants.ColdStorageAccess:GasCostOf.ColdSLoad", "cold storage access must select EIP-8038 cost");
        RequireCompactContains(policy, "storageAccessType==StorageAccessType.SLOAD||TMode.IsEip8038Enabled(spec)", "warm SSTORE must charge under EIP-8038");
        RequireCompactContains(policy, "if(!UpdateGas(refgas", "storage access must charge before warming");
        RequireCompactContains(policy, "accessTracker.WarmUp(instorageCell);returntrue;", "successful cold access must warm the cell");

        MethodDeclarationSyntax storageAccess = Method(policy, "TryConsumeStorageAccessGasCore", 6);
        RequireOrdered(storageAccess,
            "if(isTracingAccess)",
            "returngasAvailable",
            "if(accessTracker.IsCold(instorageCell))",
            "UpdateGas(refgas,TMode.IsEip8038Enabled(spec)?Eip8038Constants.ColdStorageAccess:GasCostOf.ColdSLoad)",
            "accessTracker.WarmUp(instorageCell)",
            "storageAccessType==StorageAccessType.SLOAD||TMode.IsEip8038Enabled(spec)",
            "UpdateGas(refgas,GasCostOf.WarmStateRead)");

        MethodDeclarationSyntax combinedCharge = Single(
            policy.Root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "TryConsumeStateAndExecutionGas" && method.ParameterList.Parameters.Count == 3,
            "EthereumGasPolicy.TryConsumeStateAndExecutionGas");
        RequireOrdered(combinedCharge, "UpdateGas(refgas,executionGasCost)", "TryConsumeStateGas(refgas,stateGasCost)");

        MethodDeclarationSyntax updateGas = Single(
            policy.Root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "UpdateGas" && method.ParameterList.Parameters.Count == 2 && method.TypeParameterList is null,
            "EthereumGasPolicy.UpdateGas(ref gas, gasCost)");
        RequireOrdered(updateGas, "GetRemainingGas(ingas)<gasCost", "gas.Value=0", "returnfalse", "ConsumeRaw(refgas,gasCost)", "returntrue");

        RequireCompactContains(Get(sources, GasPolicyInterfacePath), "staticabstractboolTryConsumeStateAndExecutionGas(refTSelfgas,longstateGasCost,ulongexecutionGasCost);", "gas-policy interface must expose combined state/execution charge");
        RequireCompactContains(Get(sources, PricingKernelPath), "publicstaticSStorePostAccessPricingResultPriceAfterAccess", "production SSTORE pricing kernel must remain composed");
        RequireCompactContains(Get(sources, StateChargeKernelPath), "publicstaticStateGasChargeResultTryCharge", "production state-charge kernel must remain composed");
        RequireCompactContains(Get(sources, StateTransitionAdapterPath), "publicstaticStateGasTransitionAdapterOutcomeRefundStateGas", "production state-refill adapter must remain composed");
    }

    private static void ValidateForkLineage(Dictionary<string, SourceFile> sources)
    {
        RequireCompactContains(Get(sources, ReleaseInterfacePath), "boolIsEip2200Enabled{get;}", "IReleaseSpec must expose EIP-2200");
        RequireCompactContains(Get(sources, ReleaseInterfacePath), "boolIsEip2929Enabled{get;}", "IReleaseSpec must expose EIP-2929");
        RequireCompactContains(Get(sources, ReleaseInterfacePath), "publicboolIsEip8037Enabled{get;}", "IReleaseSpec must expose EIP-8037");
        RequireCompactContains(Get(sources, ReleaseInterfacePath), "boolIsEip8038Enabled{get;}", "IReleaseSpec must expose EIP-8038");
        RequireCompactContains(Get(sources, StandardReleaseExtensionsPath), "publicboolUseHotAndColdStorage=>spec.IsEip2929Enabled;", "hot/cold storage must map to EIP-2929");
        RequireCompactContains(Get(sources, StandardReleaseExtensionsPath), "publicboolUseNetGasMeteringWithAStipendFix=>spec.UseIstanbulNetGasMetering;", "stipend fix must map to Istanbul net metering");
        SourceFile namedReleaseSpec = Get(sources, NamedReleaseSpecPath);
        RequireCompactContains(namedReleaseSpec, "ReplayAncestors(fork.Parent);fork.Apply(this);", "NamedReleaseSpec must replay every ancestor root-first");
        ClassDeclarationSyntax namedGeneric = Single(
            namedReleaseSpec.Root.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            static declaration => declaration.Identifier.ValueText == "NamedReleaseSpec" &&
                declaration.TypeParameterList?.Parameters.Count == 1,
            "NamedReleaseSpec<TSelf>");
        PropertyDeclarationSyntax instance = Single(
            namedGeneric.Members.OfType<PropertyDeclarationSyntax>(),
            static property => property.Identifier.ValueText == "Instance",
            "NamedReleaseSpec<TSelf>.Instance");
        if (Compact(instance) != "publicstaticNamedReleaseSpecInstance{get;}=newTSelf();")
            throw new ExtractionException("NamedReleaseSpec<TSelf>.Instance must construct the exact closed fork type with new TSelf().");
        RequireCompactContains(Get(sources, ForkPaths[9]), "spec.IsEip2200Enabled=true;", "Istanbul must activate EIP-2200");
        RequireCompactContains(Get(sources, ForkPaths[11]), "spec.IsEip2929Enabled=true;", "Berlin must activate EIP-2929");
        RequireCompactContains(Get(sources, ForkPaths[^1]), "spec.IsEip8037Enabled=true;", "Amsterdam must activate EIP-8037");
        RequireCompactContains(Get(sources, ForkPaths[^1]), "spec.IsEip8038Enabled=true;", "Amsterdam must activate EIP-8038");

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
            throw new ExtractionException($"Cannot parse standard-build source selection: {exception.Message}");
        }
        ValidateExclusiveBuildGroup(
            targets,
            "'$(EnableZkEvm)' == 'true'",
            "**/std/**/*.cs",
            "**/*.std.cs",
            "zkEVM");
        ValidateExclusiveBuildGroup(
            targets,
            "'$(EnableZkEvm)' != 'true'",
            "**/zkevm/**/*.cs",
            "**/*.zkevm.cs",
            "standard");

        RequireCompactContains(Get(sources, VirtualMachinePath), "publicsealedclassEthereumVirtualMachine", "EthereumVirtualMachine must remain the mainnet VM");
        RequireCompactContains(Get(sources, VirtualMachinePath), ":VirtualMachine<EthereumGasPolicy>", "EthereumVirtualMachine must close EthereumGasPolicy");
        RequireCompactContains(Get(sources, MainnetDiPath), ".AddScoped<IVirtualMachine,EthereumVirtualMachine>()", "mainnet DI must register EthereumVirtualMachine");
        RequireCompactContains(Get(sources, StandardVirtualMachinePath), "privateOpcodeTableGetOpcodeTable()=>_opcodeTablesBySpec.GetValue(Spec,static_=>newOpcodeTable());", "standard VM must supply the opcode table");
        RequireCompactContains(Get(sources, StandardDispatchFlagsPath), "publicconstboolConstTracing=true;", "standard dispatch must retain traced specializations");

        SourceFile dispatch = Get(sources, DispatchPath);
        RequireCompactContains(dispatch, "GetOpcodeTable().GetHandlers<TTracingInst,TCancelable>(Spec)", "dispatch must read the standard table");
        RequireCompactContains(dispatch, "refTTracingInst.IsActive?ref(TCancelable.IsActive?refTracedCancelable:refTraced):ref(TCancelable.IsActive?refNoTraceCancelable:refNoTrace)", "tables must match their tracing/cancellation flags");
        RequireCompactContains(dispatch, "returntable??=GenerateOpcodeHandlers<TTracingInst,TCancelable>(spec);", "all tables must originate in GenerateOpcodeHandlers");
        MethodDeclarationSyntax execute = Single(
            dispatch.Root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "ExecuteOpcode" && method.TypeParameterList?.Parameters.Count == 4,
            "VirtualMachine.ExecuteOpcode");
        RequireOrdered(execute, "pc++", "opCodeCount++", "TOpcode.Execute(refstack,refgas,state.Vm,refpc)");
    }

    private static void ValidateExclusiveBuildGroup(
        XDocument targets,
        string condition,
        string directoryPattern,
        string suffixPattern,
        string implementation)
    {
        XElement[] groups = targets.Root?.Elements("ItemGroup")
            .Where(group => (string?)group.Attribute("Condition") == condition)
            .ToArray() ?? [];
        (string Element, string Attribute, string Value)[] expected =
        [
            ("Compile", "Remove", directoryPattern),
            ("None", "Include", directoryPattern),
            ("Compile", "Remove", suffixPattern),
            ("None", "Include", suffixPattern),
        ];
        (string Element, string Attribute, string Value)[] actual = groups.Length == 1
            ? groups[0].Elements().Select(static element =>
            {
                XAttribute[] attributes = element.Attributes().ToArray();
                return attributes.Length == 1
                    ? (element.Name.LocalName, attributes[0].Name.LocalName, attributes[0].Value)
                    : (element.Name.LocalName, string.Empty, string.Empty);
            }).ToArray()
            : [];
        if (!actual.SequenceEqual(expected))
            throw new ExtractionException(
                $"Directory.Build.targets must contain the exact mutually exclusive {implementation} Compile/None directory and suffix selection pairs.");
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

        MethodDeclarationSyntax configure = Single(
            handlers.Root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "ConfigureAccessOpcodes" && method.TypeParameterList?.Parameters.Count == 4,
            "closed access-opcode configuration");
        ValidateDispatchAssignment(configure, "SLOAD", "OpcodeHandler<SLoadOpcode<TTracingInst,Eip8038,Eip2929>,TTracingInst,TCancelable>()");
        ValidateDispatchAssignment(configure, "SSTORE", "SStoreOpcodeHandler<TTracingInst,TCancelable,Eip8038,Eip2929>(spec)");

        MethodDeclarationSyntax generate = Single(
            handlers.Root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "GenerateOpcodeHandlers" && method.TypeParameterList?.Parameters.Count == 2,
            "GenerateOpcodeHandlers");
        RequireOrdered(generate,
            "if(SpecFlags.Eip2929(spec))ConfigureAccessOpcodes<TTracingInst,TCancelable,OnFlag>(lookup,spec)",
            "elseConfigureAccessOpcodes<TTracingInst,TCancelable,OffFlag>(lookup,spec)");
        MethodDeclarationSyntax outerConfigure = Single(
            handlers.Root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "ConfigureAccessOpcodes" && method.TypeParameterList?.Parameters.Count == 3,
            "EIP-8038 access-opcode configuration");
        RequireOrdered(outerConfigure,
            "if(SpecFlags.Eip8038(spec))ConfigureAccessOpcodes<TTracingInst,TCancelable,Eip2929,Eip8038On>(lookup,spec)",
            "elseConfigureAccessOpcodes<TTracingInst,TCancelable,Eip2929,Eip8038Off>(lookup,spec)");

        MethodDeclarationSyntax sstoreFactory = Single(
            handlers.Root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "SStoreOpcodeHandler" && method.TypeParameterList?.Parameters.Count == 4,
            "SSTORE specialization factory");
        RequireOrdered(sstoreFactory,
            "SpecFlags.NetGasMetering(spec)", "SpecFlags.Eip2200(spec)", "SpecFlags.Eip8037<Eip8038>(spec)",
            "OpcodeHandler<SStoreMeteredOpcode<TTracingInst,OnFlag,OnFlag,Eip8038,Eip2929>,TTracingInst,TCancelable>()");

        ValidateWrapper(handlers, "SLoadOpcode", "EvmInstructions.InstructionSLoad<TGasPolicy,TTracingInst,Eip8038,Eip2929>(refstack,refgas,vm)");
        ValidateWrapper(handlers, "SStoreMeteredOpcode", "EvmInstructions.InstructionSStoreMetered<TGasPolicy,TTracingInst,TStipendFix,TEip8037,Eip8038,Eip2929>(refstack,refgas,vm)");

        SourceFile storage = Get(sources, StorageInstructionsPath);
        MethodDeclarationSyntax sload = Method(storage, "InstructionSLoad", 3);
        RequireOrdered(sload,
            "TryConsumeSLoadBaseGas<Eip2929>(refgas,spec)",
            "stack.PopUInt256(outUInt256result)",
            "new(executingAccount,inresult)",
            "TryConsumeStorageAccessGas<Eip2929,Eip8038>(refgas,invm.VmState.AccessTracker,vm.IsTracingAccess,instorageCell,StorageAccessType.SLOAD,spec)",
            "vm.WorldState.Get(instorageCell)",
            "stack.PushZero<TTracingInst,OnFlag>()",
            "stack.PushBytes<TTracingInst>(value)",
            "returnpushResult");
        RejectAnyBefore(sload, "TryConsumeSLoadBaseGas<Eip2929>(refgas,spec)", "return", "SLOAD has an early return before its base charge");

        MethodDeclarationSyntax sstore = Method(storage, "InstructionSStoreMetered", 3);
        RequireOrdered(sstore,
            "if(vmState.IsStatic)gotoStaticCallViolation",
            "if(TUseNetGasStipendFix.IsActive)",
            "TGasPolicy.GetRemainingGas(ingas)<=GasCostOf.CallStipend",
            "stack.PopUInt256(outUInt256result)",
            "stack.PopWord256(outSpan<byte>bytesSpan)",
            "new(vmState.Env.ExecutingAccount,inresult)",
            "TryConsumeStorageAccessGas<Eip2929,Eip8038>(refgas,invmState.AccessTracker,vm.IsTracingAccess,instorageCell,StorageAccessType.SSTORE,spec)",
            "vm.WorldState.Get(instorageCell)",
            "if(Eip8038.IsActive&&TEip8037.IsActive)",
            "if(newSameAsCurrent)",
            "TryConsumeNetMeteredSStoreGas<Eip8038>(refgas,spec)",
            "vm.WorldState.GetOriginal(instorageCell)",
            "SStorePricingKernel.PriceAfterAccess(input,schedule)",
            "TryConsumeStateAndExecutionGas(refgas,pricing.StateGasCharge,pricing.ExecutionWriteGas)",
            "ApplySStoreRefund(vm,vmState,pricing.StorageClearRefund)",
            "ApplySStoreRefund(vm,vmState,pricing.StorageClearRefundReversal)",
            "vm.CreditStateGasRefund<TEip8037>(refgas,pricing.StateGasRefund)",
            "ApplySStoreRefund(vm,vmState,pricing.RestoreOriginalRefund)",
            "vm.WorldState.Set(instorageCell",
            "returnEvmExceptionType.None");
        int persistentWrites = sstore.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Count(static invocation => Compact(invocation.Expression) == "vm.WorldState.Set");
        if (persistentWrites != 1)
            throw new ExtractionException($"SSTORE must contain exactly one persistent write; found {persistentWrites}.");
        RejectAnyBefore(sstore, "if(vmState.IsStatic)gotoStaticCallViolation", "TryConsume", "SSTORE must check static context before all gas charges");
    }

    private static void ValidateWorldStateBoundary(Dictionary<string, SourceFile> sources)
    {
        SourceFile worldState = Get(sources, WorldStateInterfacePath);
        RequireCompactContains(worldState, "ReadOnlySpan<byte>Get(inStorageCellstorageCell);", "IWorldState must expose current storage reads");
        RequireCompactContains(worldState, "ReadOnlySpan<byte>GetOriginal(inStorageCellstorageCell);", "IWorldState must expose original storage reads");
        RequireCompactContains(worldState, "voidSet(inStorageCellstorageCell,byte[]newValue);", "IWorldState must expose persistent storage writes");
        RequireCompactContains(Get(sources, AccessTrackerPath), "publicreadonlyboolIsCold(inStorageCellstorageCell)", "access tracker must expose cold-cell lookup");
        RequireCompactContains(Get(sources, AccessTrackerPath), "publicreadonlyboolWarmUp(inStorageCellstorageCell)", "access tracker must expose cell warming");
        RequireCompactContains(Get(sources, StorageCellPath), "publicreadonlystructStorageCell", "StorageCell must remain a value cell");
        RequireCompactContains(Get(sources, VmStatePath), "publicboolIsStatic{get;privateset;}", "VmState must carry static context");
        RequireCompactContains(Get(sources, VmStatePath), "publiclongInitialStateGasUsed;", "VmState must carry the state-gas floor");
        RequireCompactContains(Get(sources, VmStatePath), "publiclongStateGasRefundAdvanced;", "VmState must carry advanced state refunds");
        RequireCompactContains(Get(sources, ExecutionEnvironmentPath), "publicAddressExecutingAccount{get;privateset;}", "execution environment must carry the executing account");
        RequireCompactContains(Get(sources, StorageAccessTypePath), "SLOAD", "storage access type must include SLOAD");
        RequireCompactContains(Get(sources, StorageAccessTypePath), "SSTORE", "storage access type must include SSTORE");
        RequireCompactContains(Get(sources, VirtualMachinePath), "internalvoidCreditStateGasRefund<Eip8037>", "VM must expose the specialized state-refill path");
    }

    private static void ValidateDispatchAssignment(MethodDeclarationSyntax configure, string instruction, string expectedRight)
    {
        string left = $"lookup[(int)Instruction.{instruction}]";
        AssignmentExpressionSyntax[] assignments = configure.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(assignment => Compact(assignment.Left) == left)
            .ToArray();
        if (assignments.Length != 1)
            throw new ExtractionException($"Instruction.{instruction} must have exactly one live dispatch assignment; found {assignments.Length}.");
        if (Compact(assignments[0].Right) != expectedRight)
            throw new ExtractionException($"Instruction.{instruction} dispatch changed from {expectedRight}.");
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
        Dictionary<string, string> expected = ParseExpectedFingerprints();
        if (expected.Count != SourceRelativePaths.Length)
            throw new ExtractionException($"The reviewed source fingerprint closure is incomplete: expected {SourceRelativePaths.Length}, found {expected.Count}.");
        foreach (string path in SourceRelativePaths)
        {
            if (!expected.TryGetValue(path, out string? fingerprint))
                throw new ExtractionException($"The reviewed source fingerprint closure omits {path}.");
            string actual = Get(sources, path).Sha256;
            if (!string.Equals(fingerprint, actual, StringComparison.Ordinal))
                throw new ExtractionException($"Complete-source admission fingerprint changed for {path}: expected {fingerprint}, actual {actual}.");
        }
    }

    private static Dictionary<string, string> ParseExpectedFingerprints()
    {
        Dictionary<string, string> expected = new(StringComparer.Ordinal);
        foreach (string line in ExpectedFingerprintText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = line.LastIndexOf(' ');
            if (separator <= 0 || !expected.TryAdd(line[..separator], line[(separator + 1)..]))
                throw new ExtractionException("The reviewed source fingerprint table is malformed or duplicated.");
        }
        return expected;
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

    internal static IrDocument DeserializeIr(byte[] bytes)
    {
        if (bytes is null)
            throw new ExtractionException("The serialized persistent-storage opcode IR input was null.");
        try
        {
            IrDocument document = JsonSerializer.Deserialize<IrDocument>(bytes, JsonOptions)
                ?? throw new ExtractionException("The serialized persistent-storage opcode IR was null.");
            LeanEmitter.Validate(document);
            return document;
        }
        catch (JsonException)
        {
            throw new ExtractionException("The serialized persistent-storage opcode IR is malformed or contains unknown members.");
        }
    }

    internal static SourceManifest DeserializeManifest(byte[] bytes)
    {
        if (bytes is null)
            throw new ExtractionException("The serialized persistent-storage manifest input was null.");
        try
        {
            SourceManifest manifest = JsonSerializer.Deserialize<SourceManifest>(bytes, JsonOptions)
                ?? throw new ExtractionException("The serialized persistent-storage manifest was null.");
            ValidateManifest(manifest);
            return manifest;
        }
        catch (JsonException)
        {
            throw new ExtractionException("The serialized persistent-storage manifest is malformed or contains unknown members.");
        }
    }

    internal static void ValidateManifest(SourceManifest manifest)
    {
        if (manifest is null || manifest.ExtractorVersion is null || manifest.CompilerVersion is null ||
            manifest.LanguageVersion is null || manifest.Kernel is null || manifest.Sources is null ||
            manifest.Ir is null || manifest.Lean is null || manifest.CombinedSha256 is null ||
            manifest.SemanticBindings is null || manifest.Sources.Any(static source => source is null ||
                source.Path is null || source.Sha256 is null) || manifest.Ir.Path is null ||
            manifest.Ir.Sha256 is null || manifest.Lean.Path is null || manifest.Lean.Sha256 is null ||
            manifest.SemanticBindings.Any(static binding => binding is null))
            throw new ExtractionException("The serialized persistent-storage manifest contains null collections, entries, or fields.");

        if (manifest.SchemaVersion != SchemaVersion || manifest.ExtractorVersion != ExtractorVersion ||
            string.IsNullOrWhiteSpace(manifest.CompilerVersion) ||
            manifest.LanguageVersion != LanguageVersion.CSharp14.ToString() || manifest.Kernel != Kernel ||
            manifest.Ir.Path != IrFileName || manifest.Lean.Path != DefaultLeanRelativePath ||
            !IsSha256(manifest.Ir.Sha256) || !IsSha256(manifest.Lean.Sha256) ||
            !IsSha256(manifest.CombinedSha256) ||
            !manifest.SemanticBindings.SequenceEqual(ExpectedSemanticBindings, StringComparer.Ordinal))
            throw new ExtractionException("The serialized persistent-storage manifest header, artifacts, or semantic bindings changed.");

        string[] expectedPaths = SourceRelativePaths.OrderBy(static path => path, StringComparer.Ordinal).ToArray();
        if (manifest.Sources.Length != expectedPaths.Length ||
            !manifest.Sources.Select(static source => source.Path).SequenceEqual(expectedPaths, StringComparer.Ordinal) ||
            manifest.Sources.Select(static source => source.Path).Distinct(StringComparer.Ordinal).Count() != manifest.Sources.Length ||
            manifest.Sources.Any(static source => !IsSha256(source.Sha256)))
            throw new ExtractionException("The serialized persistent-storage source identities changed or collide.");

        Dictionary<string, string> expectedFingerprints = ParseExpectedFingerprints();
        foreach (SourceIdentity source in manifest.Sources)
            if (!expectedFingerprints.TryGetValue(source.Path, out string? expected) || source.Sha256 != expected)
                throw new ExtractionException($"The serialized persistent-storage source fingerprint changed for {source.Path}.");
        if (manifest.CombinedSha256 != CombinedHash(manifest.Sources))
            throw new ExtractionException("The serialized persistent-storage combined source fingerprint changed.");
    }

    internal static void ValidateManifest(SourceManifest actual, SourceManifest expected)
    {
        ValidateManifest(actual);
        ValidateManifest(expected);
        if (!Serialize(actual).AsSpan().SequenceEqual(Serialize(expected)))
            throw new ExtractionException("The serialized persistent-storage manifest does not exactly match extracted lineage.");
    }

    private static string CombinedHash(IEnumerable<SourceIdentity> sources)
    {
        StringBuilder text = new();
        foreach (SourceIdentity source in sources)
            text.Append(source.Path).Append('\n').Append(source.Sha256).Append('\n');
        return Sha256(Encoding.UTF8.GetBytes(text.ToString()));
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(static character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f');

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
