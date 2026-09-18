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
using Microsoft.CodeAnalysis.Text;

namespace Nethermind.Evm.Lean.AccountReadOpcodeExtractor;

internal static class AccountReadOpcodeProfile
{
    internal const string DefaultOutputRelativePath = "tools/Evm/Lean/AccountReadOpcodeExtractor/Generated";
    internal const string DefaultLeanRelativePath = "tools/Evm/Lean/Eip803x/Generated/AccountReadOpcodeKernel.lean";
    internal const string IrFileName = "AccountReadOpcodeKernel.ir.json";
    internal const string ManifestFileName = "AccountReadOpcodeKernel.source-manifest.json";

    internal const string InstructionPath = "src/Nethermind/Nethermind.Evm/Instruction.cs";
    internal const string HandlersPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs";
    internal const string DispatchPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs";
    internal const string EnvironmentPath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Environment.cs";
    internal const string CodeCopyPath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.CodeCopy.cs";
    internal const string InstructionSpecPath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Spec.cs";
    internal const string GasCostPath = "src/Nethermind/Nethermind.Core/GasCostOf.cs";
    internal const string Eip8038ConstantsPath = "src/Nethermind/Nethermind.Core/Eip8038Constants.cs";
    internal const string SpecGasCostsPath = "src/Nethermind/Nethermind.Core/Specs/SpecGasCosts.cs";
    internal const string GasTagsPath = "src/Nethermind/Nethermind.Evm/GasPolicy/IGasCost.cs";
    internal const string GasPolicyInterfacePath = "src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs";
    internal const string EthereumGasPolicyPath = "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs";
    internal const string AccountPricingPath = "src/Nethermind/Nethermind.Evm/GasPolicy/AccountAccessPricingKernel.cs";
    internal const string AccountAccessKindPath = "src/Nethermind/Nethermind.Evm/GasPolicy/AccountAccessKind.cs";
    internal const string StackPath = "src/Nethermind/Nethermind.Evm/EvmStack.cs";
    internal const string AccessTrackerPath = "src/Nethermind/Nethermind.Evm/StackAccessTracker.cs";
    internal const string MemoryPath = "src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs";
    internal const string CalculationsPath = "src/Nethermind/Nethermind.Evm/Instructions/EvmCalculations.cs";
    internal const string WorldStatePath = "src/Nethermind/Nethermind.Evm/State/IWorldState.cs";
    internal const string ReadOnlyStatePath = "src/Nethermind/Nethermind.Evm/State/IReadOnlyStateProvider.cs";
    internal const string CodeRepositoryInterfacePath = "src/Nethermind/Nethermind.Evm/ICodeInfoRepository.cs";
    internal const string CodeRepositoryPath = "src/Nethermind/Nethermind.Evm/CodeInfoRepository.cs";
    internal const string CacheCodeRepositoryPath = "src/Nethermind/Nethermind.Evm/CacheCodeInfoRepository.cs";
    internal const string CodeInfoPath = "src/Nethermind/Nethermind.Evm/CodeAnalysis/CodeInfo.cs";
    internal const string SpecFlagsPath = "src/Nethermind/Nethermind.Evm/SpecFlags.std.cs";
    internal const string TypeFlagsPath = "src/Nethermind/Nethermind.Core/TypeFlags.cs";
    internal const string Eip8038FlagPath = "src/Nethermind/Nethermind.Evm/Eip8038Flag.cs";
    internal const string VirtualMachinePath = "src/Nethermind/Nethermind.Evm/VirtualMachine.cs";
    internal const string StandardVirtualMachinePath = "src/Nethermind/Nethermind.Evm/VirtualMachine.std.cs";
    internal const string MainnetDiPath = "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs";
    internal const string ReleaseExtensionsPath = "src/Nethermind/Nethermind.Core/Specs/IReleaseSpecExtensions.cs";
    internal const string StandardReleaseExtensionsPath = "src/Nethermind/Nethermind.Core/Specs/IReleaseSpecExtensions.std.cs";
    internal const string NamedReleaseSpecPath = "src/Nethermind/Nethermind.Specs/Forks/NamedReleaseSpec.cs";
    internal const string BuildTargetsPath = "src/Nethermind/Directory.Build.targets";

    private const int SchemaVersion = 1;
    private const string ExtractorVersion = "1.0.0";
    private const string Kernel = "Nethermind standard-mainnet Amsterdam account-read opcode slice";
    private const string BuildTargetsSha256 = "0598cebaef1df41102a18a3f9ace810bed1e4e64b8471d055724b4a394dccec6";

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

    private static readonly string[] ForkNames =
    [
        "Olympic", "Frontier", "Homestead", "Dao", "TangerineWhistle", "SpuriousDragon",
        "Byzantium", "Constantinople", "ConstantinopleFix", "Istanbul", "MuirGlacier", "Berlin",
        "London", "ArrowGlacier", "GrayGlacier", "Paris", "Shanghai", "Cancun", "Prague", "Osaka",
        "BPO1", "BPO2", "Amsterdam",
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

    internal static readonly string[] SourcePaths =
    [
        InstructionPath, HandlersPath, DispatchPath, EnvironmentPath, CodeCopyPath, InstructionSpecPath, GasCostPath,
        Eip8038ConstantsPath, SpecGasCostsPath, GasTagsPath, GasPolicyInterfacePath, EthereumGasPolicyPath,
        AccountPricingPath, AccountAccessKindPath, StackPath, AccessTrackerPath, MemoryPath, CalculationsPath,
        WorldStatePath, ReadOnlyStatePath, CodeRepositoryInterfacePath, CodeRepositoryPath, CacheCodeRepositoryPath, CodeInfoPath,
        SpecFlagsPath, TypeFlagsPath, Eip8038FlagPath, VirtualMachinePath, StandardVirtualMachinePath, MainnetDiPath,
        ReleaseExtensionsPath, StandardReleaseExtensionsPath,
        NamedReleaseSpecPath, BuildTargetsPath, .. ForkPaths,
    ];

    private static readonly DispatchTableBinding[] DispatchTables =
    [
        new("NoTrace", "OffFlag", "OffFlag"),
        new("NoTraceCancelable", "OffFlag", "OnFlag"),
        new("Traced", "OnFlag", "OffFlag"),
        new("TracedCancelable", "OnFlag", "OnFlag"),
    ];

    private static readonly string[] ExpectedSemanticBindings =
    [
        "Amsterdam root-first fork replay closes EIP-2929, EIP-8038, and EXTCODEHASH activation.",
        "All four standard tracing/cancellation tables close each handler over EthereumGasPolicy.",
        "Opcode bytes, dispatch routes, gas/access ordering, provider calls, stack/memory operations, and explicit read records are admitted.",
        "Generated Lean is produced only after serialize-deserialize validation of every IR field and exact specialization key.",
    ];

    private static readonly Seed[] Seeds =
    [
        new("balance", "BALANCE", 0x31, "BalanceOpcode<TTracingInst,EvmInstructions.AccessSpec<OnFlag,Eip8038On>>", "balance", 1, 1, false, false,
            "enter>baseGas>popAddress>warm>accessGas>getBalance>push"),
        new("extCodeSize", "EXTCODESIZE", 0x3b, "ExtCodeSizeOpcode<TTracingInst,Eip8038On,OnFlag>", "codeLength", 1, 1, false, true,
            "enter>baseGas>popAddress>warm>accessGas>secondWarmGas>accountRead>fusionOrCodeNoDelegation>push"),
        new("extCodeCopy", "EXTCODECOPY", 0x3c, "ExtCodeCopyOpcode<TTracingInst,Eip8038On,OnFlag>", "zeroExtendedCodeCopy", 4, 0, true, true,
            "enter>popAddress>popDestinationSourceLength>copyGas>warm>accessGas>secondWarmGas>zeroLengthRecordOrMemoryGas>accountRead>codeNoDelegation>zeroExtendedCopy"),
        new("extCodeHash", "EXTCODEHASH", 0x3f, "ExtCodeHashOpcode<TTracingInst,EvmInstructions.AccessSpec<OnFlag,Eip8038On>>", "zeroIfDeadElseCodeHash", 1, 1, false, false,
            "enter>baseGas>popAddress>warm>accessGas>isDead>getCodeHashIfLive>push"),
    ];

    // Filled only from a reviewed extractor mismatch report. Empty entries deliberately fail closed.
    private static readonly Dictionary<string, string> ExpectedNodeFingerprints = new(StringComparer.Ordinal)
    {
        ["AccountAccessKind"] = "9677bb21c56da9c09bd2050a9ea50609ee0258f663328985c3d661559b853f73",
        ["AccountAccessPricingDecision"] = "3c844f894b2890defbd4d0a6fe80bfc9b980c5a97f6e9674217299db91623d0b",
        ["AccountAccessPricingKernel"] = "21a835b0b478e01a3d353f4a7e55f290b72a4ff3f40c709ac927ec17868710fc",
        ["AccountAccessPricingResult"] = "f4c5eab0266b154297dd21bf93f36c0706da68ef85dd4c5b5a835090705170ee",
        ["BalanceGasCost"] = "3ce2cc4f42e7e300899d8ef759c70008aeea60891b497c481448b1ea575d6066",
        ["BalanceOpcode"] = "18983aa332727428e0512e88d78db5fa3ca0a27ca25f0a4eaf607fbed5ebc306",
        ["BlockProcessingModule.Load/1"] = "3440f8d4a409a93055ceabd96d1c6aa7d028b3cb0f91f7c3ccfcc0d570b19f02",
        ["CacheCodeInfoRepository"] = "79e3b742ff2cea830ce05da1cf48cb0b7b7b55fd5fc4697fa963f180f9605b13",
        ["CodeInfo"] = "50a84c5defe04fbefe29785d83713862be03f9738d744aebff74ab6ea5916460",
        ["CodeInfoRepository.GetCachedCodeInfo/4"] = "7430eee4a016f25532e6ab1a8c79b8bb61dd630482d6023435da800efddf7d35",
        ["CodeInfoRepository.GetCodeInfo/3"] = "edcb35b8e85d4ae39d6636ba9c8c0c26e5bc93e71199bd39bd7482e24de51e9c",
        ["CodeInfoRepository.InternalGetCodeInfo/2"] = "db0b0c40e4cbb306ee2e84303c40feb797b280897ec2231de7d21d99fb119bac",
        ["Eip8038Constants"] = "d5a26b46495b5ef3aa02c2b8b975213d29c707e2d46f28e994b8d2fcb791c037",
        ["Eip8038Off"] = "2bbe0b92ebc7a145180660c924524b09af1cdbc29a6d66f14c36ca6410c82612",
        ["Eip8038On"] = "595314cba01ce0a66c6b5c764dcc8f8507ad9fe0c993683430537665e42b20ff",
        ["EthereumGasPolicy.TryConsumeAccountAccessGas/6"] = "00ddc3648f81a446794ea2ba7de2eff1cb0eda4ccd6da7040de077d67dc1f124",
        ["EthereumGasPolicy.TryConsumeAccountAccessGasCore/6"] = "d2ac67a88556be9d9a597c2d75215c3324f5b72670d3453b2aa1118cda1dae2e",
        ["EthereumGasPolicy.TryConsumeDataCopyGas/4"] = "c3fbd6147146d4ea323c5f4bd0be74e01d59681cf498bfef17d875c415368104",
        ["EthereumGasPolicy.UpdateGas/2"] = "03603bdc2e651c10cd04098a1176a6389d517665999b5ef6ddd44b901428adf1",
        ["EthereumGasPolicy.UpdateMemoryCost.UInt256/4"] = "992e1fda7c15aa6a06c5806b35e39aa08529f0041f58962b81ba176640e7ae0c",
        ["EthereumVirtualMachine"] = "e13aa6be01f10b34a21a58401cf4a1ee17c7e02ce1e4a1f37f7ad3f960ae99b0",
        ["EvmCalculations.Div32Ceiling.UInt256/2"] = "12d5c1ca9a5d6089d7c181a88cfd90f04e5e976571eba9975bf2c56d73b09c92",
        ["EvmInstructions.AccessSpec"] = "bc3ae85555815f732d36798b4223f8093ecfd282b93f4d3229a714a5f527aa62",
        ["EvmInstructions.IAccessSpec"] = "fed257ac1b56495ba30797cda9cd32792f88d25d915f4e038fe1fb0ead11b7ec",
        ["EvmInstructions.InstructionBalance/3"] = "f14a094b08af8d9640aebd19361c557388feb2969d1491d511c7bafa4d715624",
        ["EvmInstructions.InstructionExtCodeCopy/3"] = "306c4a3963d6d51957422374b322271c09b876ecf3eb56a8d39a90949ea1e9c2",
        ["EvmInstructions.InstructionExtCodeHash/3"] = "c52762e15040dea5a61b62ed365fbbe90e1219bb69d2aab990bf07819c06f45c",
        ["EvmInstructions.InstructionExtCodeSize/4"] = "b655b1ffdd31184df94de0bf4ed617804e5321c7b10b72bd7e50931e62ea673c",
        ["EvmPooledMemory.CalculateMemoryCost.UInt256/3"] = "14a8006bbb91fb529354f84987088aed3b4bfe00760e095162943f800172fd76",
        ["EvmPooledMemory.CopyFromZeroExtendedAfterGas/4"] = "d17083ab7c4f10e9f198dc25989eac18d7422587144407a6c0ffce0f83b3e9f8",
        ["EvmStack.PeekUInt256IsZero/0"] = "fe744e7f32a0e108ea8b2589bba541f012afd27cac2ce42a9d5ce9c83dddb164",
        ["EvmStack.PopAddress/1"] = "46dfe1e1ad0a558777f55d7645b242ca0f15cfb448fb3280ade40b10a7d1d3a3",
        ["EvmStack.PopLimbo/0"] = "4241f248b2a3f9d681aebed596e8f1a404397c9ccf4025b8670bb10dc41b8070",
        ["EvmStack.PopUInt256/3"] = "e9c232a9ea3c3eb9db33815d0f9bb4d245d9866a3f5828c6165506949fb64ee3",
        ["EvmStack.Push32Bytes/1"] = "f822a7db2041690e750ce545cc5fb35b6a3ab6c0b1bd66d92082eb0f62bfd4e0",
        ["EvmStack.PushOne/0"] = "2dafadd12fd6ec1a67214b8b2fcdf9dc41f95fc5b2fc0e1b11058edff7db7733",
        ["EvmStack.PushUInt32/1"] = "875b11857f657ee27ddda0da1d8418b7c9e541370c6a59115bd9808ef0e59c77",
        ["EvmStack.PushZero/0"] = "07a52bcf9a79e8b748d932f248e908be0c04109e71cc8c0d3c109ac8d3dfcd6b",
        ["ExtCodeCopyOpcode"] = "349702155293e7fcbfef2eebf0f43f0be2421b0dc05dc4a7a1775796d13fa551",
        ["ExtCodeHashGasCost"] = "a822d9f0e6c3e23b5a9513ecfeedeb8103a5995c2c18a42fc23d0e05e8329300",
        ["ExtCodeHashOpcode"] = "9e80e583748bde8c2cd84a40c1808f2f30be3d613ad82100d715b9f9874dfe63",
        ["ExtCodeSizeGasCost"] = "c87fb9de7e8eaab88b87c223fb618c866bc8c1f8cdd5097973ada0c70e4c1668",
        ["ExtCodeSizeOpcode"] = "a02b7d1ce062f6e22482091a2068a00cea36fa738dddde1d286e75d7e0a30504",
        ["GasCostOf"] = "a7f7043e7bb651797016c729210f29761e7bfbcb5a0c45ddf797f78533470ca2",
        ["ICodeInfoRepository.GetCachedCodeInfo/4"] = "738d950df483911c0c51894bb638ffcd0784aa3d8564dc45a3afa5d93203d530",
        ["IEip8038Flag"] = "dfdefb0947905860db284bf534961bae986b53bcc5b3cd7bd59688e4616279d3",
        ["IFlag"] = "0969a5eef23ccefb624c65c4d32e32b38d3428cde83f26e466ed0bb3f59dba33",
        ["IGasPolicy.TryConsumeAccountAccessGas/6"] = "4729d17fc4a3c3d48755d1342543876d22f0fad88b7fb8ed2b452390210c64fe",
        ["IGasPolicy.TryConsumeDataCopyGas/4"] = "a18e5deb01779e64f95f91e09e8cac4915783f820129409b56a684c989c71e3b",
        ["IGasPolicy.UpdateGas/2"] = "6ac637773b18e20c4515bb20495da285c4f361e1f44b879ca97251b7340598a3",
        ["IGasPolicy.UpdateGas<T>/2"] = "152177530c7244c67093ea20c77c60dd1cbaa3d167968671d510e615e65735d6",
        ["IGasPolicy.UpdateMemoryCost.UInt256/4"] = "2dbf695a075d7f5b9ac7d8dccec23b73887c293084511aca5a1e557fce67f9e6",
        ["IReadOnlyStateProvider.IsContract"] = "039ec8069633fb5c93135b0793cf6e2c370b4d67df2de4f9a47862098ab7822f",
        ["IReadOnlyStateProvider.IsDeadAccount"] = "a4dd3c5b1502387246bf19817224eeb9bfa5f05b8a1a329a2d02471ee567c41c",
        ["IReleaseSpecExtensions.ExtCodeHashOpcodeEnabled"] = "3d958463d0d26dd6d511765eb93d1dc956aa34c752a7297076f57b625fe66ff4",
        ["IReleaseSpecExtensions.std.UseHotAndColdStorage"] = "8a08ede2e869eacb2574cfcd9d3175777f1896deabef9247f45d79a9103f7958",
        ["IWorldState.AddAccountRead"] = "c4063a74a89dd033a27d20aa3da1901ab34342cd2422e4b54f90e6dad2aab106",
        ["IWorldState.GetBalance"] = "fd0b8f33c871fe69a3de93616337c25d56a5c66ae112103055b35dc3ed14bd10",
        ["IWorldState.GetCodeHash"] = "a2c3888f197046356e9850b6d5d0f7592c8b4bb26efc17430558d19f5966b707",
        ["IWorldState.RecordBytecodeAccess"] = "3bf7808c5a5a6129f02236af5de3c99be8e52be12a0c91e80c390bb4e7ae00f2",
        ["Instruction enum"] = "2304600d40aff5aca2cfe1d38415621cb3a4b38f22985472bf6f3c6b9e814c73",
        ["NamedReleaseSpec"] = "27580d1144b608d4073dffd90bc6cf41131ee88111d90f38f8a3c9abe806cd9e",
        ["NamedReleaseSpec<TSelf>"] = "38453abaa4725d671b85fe02af18e5b347bd62e865ddd0de90975463376df72a",
        ["OffFlag"] = "8583d26a1581dc0bb59f99838d86125a8ab673cc5563d1c38efd1118e2ac12f2",
        ["OnFlag"] = "c2d881346185d771a7a0b422bb063ad3f4b10bdd50247342a4a28b6f9e72e2a1",
        ["OpcodeTable.GetHandlers/1"] = "51190a7984712cbdcdf696e9012e89a096416479a08a7a5c56e2df8a299000fa",
        ["SpecFlags.Eip2929"] = "f2732d9f495b8559132bbc01daa53ca8745b88a21aaa2cf42acc953ea665e3c0",
        ["SpecFlags.Eip8038"] = "b26c0d4c335db7b294a62ce10923960dbe6a95cbc5033f2fcb62769f1b638f7e",
        ["SpecGasCosts"] = "a2c8ab4302f84be517993249b2faccc094f3d42846b3a65930423fcfd8e332cc",
        ["StackAccessTracker.WarmUp/1"] = "c6d037678f5cd2c59ab64a012373c3008127a77f8f69a9ac66d4b89c11d4b845",
        ["VeryLowGasCost"] = "1d1ffd5a08fda51bb4998f77edcbe632e5141a8be964af4cb916d7f35c92fc02",
        ["VirtualMachine.ConfigureAccessOpcodes/3"] = "3f6ffb4d8c2ce5513f7124a9ce50b5c9aaa4a90123409e2557b4d982f440a165",
        ["VirtualMachine.ConfigureAccessOpcodes/4"] = "f1b145f5a8b3967c9308af8d89c51bf733c4848a004fdcddb21b9b9f148bf499",
        ["VirtualMachine.ExecuteOpcode/5"] = "57f66b4943a78716c342cae8e9877e61158fa254927c21226085ad683f868112",
        ["VirtualMachine.ExitCheckedOpcode/4"] = "418f4a900fafd15379651a6ce8e7be222e2f6820caaba4e624cafbfaca9b6409",
        ["VirtualMachine.GenerateOpcodeHandlers/1"] = "668f7e54f6c8ea211b14b012709b3b0796d80881765148e0e6957eb9c6c143af",
        ["VirtualMachine.GetOpcodeHandlers/0"] = "a78c615080c3521863e72d23e2d78d2f9181bfe085e7000bdbd0926da1fd459e",
        ["VirtualMachine.OpcodeHandler/0"] = "1baab10ac6604c84308404683fce8edc9462627d0ebfec38b1e6abac4d534288",
        ["VirtualMachine.PrepareOpcodes/1"] = "cd972966980612759bbf08e3a2d87532b36adbbdce8a8a186b58faa8887a243e",
        ["VirtualMachine.PrepareOpcodes/2"] = "ddb410d0588f82abcc0dfb6fb676f53786efcfcaf7f323ff508b8c71f9052059",
        ["VirtualMachine.RunDispatchLoop/3"] = "ae0e92e449246802c2f1d3ce62a22ffa380572cadb058d4bd9a4e3bbdc440b0e",
        ["VirtualMachine.std.GetOpcodeTable/0"] = "f1b453ab01bc7cac9dd0c7539ce1b188c76cbb6a59f3a5b4e04af66ea1ffe5e7",
        ["VirtualMachine.std.ShouldRefreshOpcodes/0"] = "aba009e105367b734e4c837ca37faba3d7df0f48ec455e721761db0f153f5996",
        ["VirtualMachine.std._opcodeTablesBySpec"] = "c3d45a3d62f50fbc005f66012e139a2ee06ec5b76baeb84c3e5f1eaeea0a4177",
        ["fork Amsterdam"] = "40c1c24ae1f2af1bcbb10222f5ce95e1af1b2934082775e2ecca49659394462e",
        ["fork ArrowGlacier"] = "5b90da3d0f112018d10d64455b534e2297033f68c8397c313b79003258d503d6",
        ["fork BPO1"] = "be258c08c9fcf5fc784e0c67cd80fc9c8e438b9eb0812ae59f57c8a67faa38b6",
        ["fork BPO2"] = "8e549474118c684d4695b8309a25c4ca756e25fad733265615eb853e4ee8c384",
        ["fork Berlin"] = "3d2dc4ca4655968f9463669e888b5e93a8713f51c9b04867a6b5164cfe91eb72",
        ["fork Byzantium"] = "e7c285b4029ec27d76d951f088f319e02c7704122b8afff504579a576b2bffc6",
        ["fork Cancun"] = "c7f0e0e6e5738ee9119a40797a676ecd9ec9e1615ece8625a5960feff9441592",
        ["fork Constantinople"] = "51bf58232971afa1766bedc1d57d190bca2fff5eac8ecd2580f681c2dbe43da2",
        ["fork ConstantinopleFix"] = "342bc062e00a8bba7e4769ac99903a90e2d51cf1a7fdae27ff17544d8102bcc1",
        ["fork Dao"] = "3844254297bdcf240044d796cda66c3a619987e8f4dc7efedb1006f32aa2ac9a",
        ["fork Frontier"] = "b3a10ae682c490657666b85e1ef6bc8890d47b57f22e179219b7ee8ae1439096",
        ["fork GrayGlacier"] = "108f9bf261f9c0668448138f54b83de5dbb767961639bbc5d0f0bbd6f8c4af3a",
        ["fork Homestead"] = "939d87a10e279bb2a9bb99d29681f77d165a18f3bccccddf23a8e7ade3c82f18",
        ["fork Istanbul"] = "bd27e84607381f46512d0adefdea018e98d0066f0e30983dcc2042afb93a1688",
        ["fork London"] = "d9d6409dde1740301c7160e8a44ec8055c8a5876658a41052b919523046623ee",
        ["fork MuirGlacier"] = "b545472f283761ccf0cc064a8d21b35f314d28dee4bf1dab940b812b49def20f",
        ["fork Olympic"] = "87413b5771acc69ee5119028a9dc430e14878dd018cb2ae74b13f76ad3925181",
        ["fork Osaka"] = "de6bfea81a1ccaf025825548a1db86da1563dbe634c798f9a07c4c67927e4c0c",
        ["fork Paris"] = "92deeb9064f1ab67eaa90b0261f1c25735081eda1fe52311452850a454c770a6",
        ["fork Prague"] = "a709b4f79f36ed2e17adfa5a6ea567de864e3db192a4386c324478b89697e57a",
        ["fork Shanghai"] = "c7f39d5d0edc4533473244f179bb2ac5e325a7fa186e7cb7fbbde92fb1b69ebd",
        ["fork SpuriousDragon"] = "53c19e40a77238c3ec7a5125f977b9bdd3391e1adce6058c1a0b76ea6c696df6",
        ["fork TangerineWhistle"] = "37901854ff827fcbfbc0a9d57b2f8415cf0604a35f88e639ba43546d403f4497",
        ["using-alias src/Nethermind/Nethermind.Evm/EvmStack.cs#0"] = "0308f2f53ed96f05e1d57e99be56b6898ddcef7b606bcf18d5c3d65ecd24e65e",
    };

    internal static ExtractionResult Extract(string repoRoot, string outputDirectory, string? leanOutputPath = null)
    {
        string root = Path.GetFullPath(repoRoot);
        Dictionary<string, SourceFile> sources = SourcePaths.ToDictionary(
            static path => path,
            path => Read(root, path, path.EndsWith(".cs", StringComparison.Ordinal)),
            StringComparer.Ordinal);
        List<NodeAdmission> admissions = [];
        RejectDiagnosticsAndDirectives(sources, admissions);
        Dictionary<string, int> instructionValues = ValidateInstructionEnum(sources[InstructionPath], admissions);
        ValidateReachability(sources, admissions);
        ScheduleDescriptor schedule = ValidateGasAndAccess(sources, admissions);
        OpcodeDescriptor[] opcodes = ValidateOpcodes(root, sources, instructionValues, admissions);
        OpcodeSpecialization[] specializations = BuildSpecializations(opcodes);
        ValidateProviderAndRepresentationSurface(sources, admissions);
        ValidateFingerprints(admissions, sources);

        IrDocument document = new(
            SchemaVersion,
            ExtractorVersion,
            Kernel,
            new ReachabilityDescriptor(
                "IVirtualMachine",
                "EthereumVirtualMachine",
                "VirtualMachine<EthereumGasPolicy>",
                "EthereumGasPolicy",
                "WorldState",
                "CacheCodeInfoRepository",
                "EthereumPrecompileProvider",
                "StaticCodeCache.Instance",
                "Amsterdam",
                "PrepareOpcodes->OpcodeTable.GetHandlers->GenerateOpcodeHandlers->ConfigureAccessOpcodes->OpcodeHandler->ExecuteOpcode",
                "EnableZkEvm != true selects *.std.cs and excludes *.zkevm.cs"),
            schedule,
            opcodes,
            specializations,
            [
                "C# and CLR execution correctness are trusted; complete Roslyn token admission only fails closed on source drift.",
                "Address is the low 160 bits of the popped UInt256; UInt256 and ValueHash256 byte/word representations are adapter premises.",
                "IWorldState and ICodeInfoRepository values, IsPrecompile, IsContract, IsDeadAccount, and code-cache coherence are provider premises.",
                "The operational theorem assumes IsTracingAccess=false; access-list-generation tracing intentionally pre-warms before pricing.",
                "Instruction trace callbacks, cancellation polling, unsafe stack storage, cache metrics, and frame rollback are outside this slice.",
                "PC in this model is the dispatch-local program counter; exceptional VM-state publication is a later frame/dispatcher obligation.",
            ]);

        byte[] irBytes = Serialize(document);
        IrDocument roundTripped = DeserializeIr(irBytes);
        AccountReadOpcodeLeanEmitter.Validate(roundTripped);
        byte[] leanBytes = AccountReadOpcodeLeanEmitter.Emit(roundTripped, Sha256(irBytes));

        string output = Path.GetFullPath(outputDirectory);
        string leanPath = Path.GetFullPath(leanOutputPath ?? Path.Combine(root, DefaultLeanRelativePath));
        EnsureWithin(leanOutputPath is null ? root : output, leanPath);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(Path.GetDirectoryName(leanPath)!);
        string irPath = Path.Combine(output, IrFileName);
        string manifestPath = Path.Combine(output, ManifestFileName);
        File.WriteAllBytes(irPath, irBytes);
        File.WriteAllBytes(leanPath, leanBytes);

        SourceManifest manifest = new(
            SchemaVersion,
            ExtractorVersion,
            typeof(CSharpCompilation).Assembly.GetName().Version?.ToString() ?? "unknown",
            LanguageVersion.CSharp14.ToDisplayString(),
            Kernel,
            sources.Values.OrderBy(static source => source.RelativePath, StringComparer.Ordinal)
                .Select(static source => new SourceIdentity(source.RelativePath, source.Hash)).ToArray(),
            admissions.OrderBy(static admission => admission.Identity, StringComparer.Ordinal)
                .Select(admission => new NodeFingerprint(admission.Identity, admission.Source.RelativePath,
                    admission.Node.Kind().ToString(), TokenHash(admission.Node))).ToArray(),
            new ArtifactIdentity(IrFileName, Sha256(irBytes)),
            new ArtifactIdentity(DefaultLeanRelativePath, Sha256(leanBytes)),
            ExpectedSemanticBindings);
        byte[] manifestBytes = Serialize(manifest);
        SourceManifest roundTrippedManifest = DeserializeManifest(manifestBytes);
        ValidateManifest(roundTrippedManifest, manifest);
        File.WriteAllBytes(manifestPath, manifestBytes);
        return new(irPath, manifestPath, leanPath, opcodes.Length, specializations.Length, sources.Count, admissions.Count);
    }

    internal static IrDocument DeserializeIr(byte[] bytes)
    {
        if (bytes is null)
            throw new ExtractionException("The serialized account-read IR input was null.");
        try
        {
            IrDocument document = JsonSerializer.Deserialize<IrDocument>(bytes, JsonOptions)
                ?? throw new ExtractionException("The serialized account-read IR was empty.");
            AccountReadOpcodeLeanEmitter.Validate(document);
            return document;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"The serialized account-read IR is not valid JSON: {exception.Message}");
        }
    }

    internal static SourceManifest DeserializeManifest(byte[] bytes)
    {
        if (bytes is null)
            throw new ExtractionException("The serialized account-read manifest input was null.");
        try
        {
            SourceManifest manifest = JsonSerializer.Deserialize<SourceManifest>(bytes, JsonOptions)
                ?? throw new ExtractionException("The serialized account-read manifest was empty.");
            ValidateManifest(manifest);
            return manifest;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"The serialized account-read manifest is not valid JSON: {exception.Message}");
        }
    }

    internal static void ValidateManifest(SourceManifest manifest)
    {
        if (manifest is null || manifest.ExtractorVersion is null || manifest.RoslynVersion is null ||
            manifest.LanguageVersion is null || manifest.Kernel is null || manifest.Sources is null ||
            manifest.Admissions is null || manifest.Ir is null || manifest.Lean is null ||
            manifest.SemanticBindings is null || manifest.Sources.Any(static source => source is null ||
                source.Path is null || source.Sha256 is null) || manifest.Admissions.Any(static admission =>
                admission is null || admission.Identity is null || admission.SourcePath is null ||
                admission.SyntaxKind is null || admission.Sha256 is null) || manifest.Ir.Path is null ||
            manifest.Ir.Sha256 is null || manifest.Lean.Path is null || manifest.Lean.Sha256 is null ||
            manifest.SemanticBindings.Any(static binding => binding is null))
            throw new ExtractionException("The serialized account-read manifest contains null collections, entries, or fields.");

        if (manifest.SchemaVersion != SchemaVersion || manifest.ExtractorVersion != ExtractorVersion ||
            string.IsNullOrWhiteSpace(manifest.RoslynVersion) ||
            manifest.LanguageVersion != LanguageVersion.CSharp14.ToDisplayString() || manifest.Kernel != Kernel ||
            manifest.Ir.Path != IrFileName || manifest.Lean.Path != DefaultLeanRelativePath ||
            !IsSha256(manifest.Ir.Sha256) || !IsSha256(manifest.Lean.Sha256) ||
            !manifest.SemanticBindings.SequenceEqual(ExpectedSemanticBindings, StringComparer.Ordinal))
            throw new ExtractionException("The serialized account-read manifest header, artifacts, or semantic bindings changed.");

        string[] expectedPaths = SourcePaths.OrderBy(static path => path, StringComparer.Ordinal).ToArray();
        if (manifest.Sources.Length != expectedPaths.Length ||
            !manifest.Sources.Select(static source => source.Path).SequenceEqual(expectedPaths, StringComparer.Ordinal) ||
            manifest.Sources.Select(static source => source.Path).Distinct(StringComparer.Ordinal).Count() != manifest.Sources.Length ||
            manifest.Sources.Any(static source => !IsSha256(source.Sha256)))
            throw new ExtractionException("The serialized account-read source identities changed or collide.");

        ValidateAdmissionIdentities(manifest.Admissions, expectedPaths);
        string[] expectedAdmissions = ExpectedNodeFingerprints.Keys.OrderBy(static identity => identity, StringComparer.Ordinal).ToArray();
        if (manifest.Admissions.Length != expectedAdmissions.Length ||
            !manifest.Admissions.Select(static admission => admission.Identity).SequenceEqual(expectedAdmissions, StringComparer.Ordinal))
            throw new ExtractionException("The serialized account-read admission identities changed.");
        foreach (NodeFingerprint admission in manifest.Admissions)
            if (!ExpectedNodeFingerprints.TryGetValue(admission.Identity, out string? expected) || admission.Sha256 != expected)
                throw new ExtractionException($"The serialized account-read admission fingerprint changed for {admission.Identity}.");
    }

    internal static void ValidateManifest(SourceManifest actual, SourceManifest expected)
    {
        ValidateManifest(actual);
        ValidateManifest(expected);
        if (!Serialize(actual).AsSpan().SequenceEqual(Serialize(expected)))
            throw new ExtractionException("The serialized account-read manifest does not exactly match extracted lineage.");
    }

    private static void ValidateAdmissionIdentities(IReadOnlyList<NodeFingerprint> admissions, IReadOnlyList<string> sourcePaths)
    {
        HashSet<string> identities = new(StringComparer.Ordinal);
        HashSet<string> qualifiedIdentities = new(StringComparer.Ordinal);
        HashSet<string> knownPaths = new(sourcePaths, StringComparer.Ordinal);
        foreach (NodeFingerprint admission in admissions)
        {
            if (string.IsNullOrWhiteSpace(admission.Identity) || string.IsNullOrWhiteSpace(admission.SourcePath) ||
                string.IsNullOrWhiteSpace(admission.SyntaxKind) || !IsSha256(admission.Sha256) ||
                !knownPaths.Contains(admission.SourcePath))
                throw new ExtractionException("The serialized account-read admission identity is incomplete or has an unknown source path.");
            if (!identities.Add(admission.Identity) || !qualifiedIdentities.Add($"{admission.SourcePath}:{admission.Identity}"))
                throw new ExtractionException($"Duplicate owner/path-qualified account-read admission identity {admission.SourcePath}:{admission.Identity}.");
        }
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(static character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static void ValidateSpecializations(
        IReadOnlyList<OpcodeDescriptor> opcodes,
        IReadOnlyList<OpcodeSpecialization> specializations)
    {
        if (opcodes.Count != Seeds.Length)
            throw new ExtractionException($"Expected {Seeds.Length} opcode descriptors but found {opcodes.Count}.");
        Dictionary<string, OpcodeDescriptor> descriptors = new(StringComparer.Ordinal);
        foreach (OpcodeDescriptor opcode in opcodes)
            if (!descriptors.TryAdd(opcode.Name, opcode))
                throw new ExtractionException($"Duplicate opcode descriptor {opcode.Name}.");
        Dictionary<string, DispatchTableBinding> tables = DispatchTables.ToDictionary(static table => table.Name, StringComparer.Ordinal);
        HashSet<(string Opcode, string Table)> keys = [];
        foreach (OpcodeSpecialization specialization in specializations)
        {
            if (!keys.Add((specialization.Opcode, specialization.DispatchTable)))
                throw new ExtractionException($"Duplicate specialization {specialization.Opcode}/{specialization.DispatchTable}.");
            if (!descriptors.TryGetValue(specialization.Opcode, out OpcodeDescriptor? opcode) ||
                !tables.TryGetValue(specialization.DispatchTable, out DispatchTableBinding? table))
                throw new ExtractionException($"Unknown specialization key {specialization.Opcode}/{specialization.DispatchTable}.");
            if (specialization.ClosedRoot.Contains("TTracingInst", StringComparison.Ordinal) ||
                specialization.ClosedRoot.Contains("TGasPolicy", StringComparison.Ordinal) ||
                specialization.ClosedRoot.Contains("Eip2929", StringComparison.Ordinal) ||
                specialization.ClosedRoot.Contains("Eip8038>", StringComparison.Ordinal))
                throw new ExtractionException($"Specialization {specialization.Opcode}/{specialization.DispatchTable} contains an open generic placeholder.");
            if (specialization != ExpectedSpecialization(opcode, table))
                throw new ExtractionException($"Specialization {specialization.Opcode}/{specialization.DispatchTable} is not its exact closed production root.");
        }
        int expected = Seeds.Length * DispatchTables.Length;
        if (specializations.Count != expected || keys.Count != expected)
            throw new ExtractionException($"Expected {expected} exact closed roots but found {specializations.Count}.");
    }

    private static OpcodeSpecialization[] BuildSpecializations(IReadOnlyList<OpcodeDescriptor> opcodes)
    {
        OpcodeSpecialization[] result = opcodes.SelectMany(static opcode =>
            DispatchTables.Select(table => ExpectedSpecialization(opcode, table))).ToArray();
        ValidateSpecializations(opcodes, result);
        return result;
    }

    private static OpcodeSpecialization ExpectedSpecialization(OpcodeDescriptor opcode, DispatchTableBinding table)
    {
        string handler = opcode.HandlerRoute.Replace("TTracingInst", table.TracingFlag, StringComparison.Ordinal);
        string root = $"VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<{handler},{table.TracingFlag},{table.CancelableFlag},OnFlag>";
        return new(opcode.Name, table.Name, table.TracingFlag, table.CancelableFlag, root);
    }

    private static Dictionary<string, int> ValidateInstructionEnum(SourceFile source, List<NodeAdmission> admissions)
    {
        EnumDeclarationSyntax instruction = RequireSingle(source.Root.DescendantNodes().OfType<EnumDeclarationSyntax>(),
            static node => node.Identifier.ValueText == "Instruction", "Instruction enum must be declared exactly once.");
        Admit(admissions, source, "Instruction enum", instruction);
        Dictionary<string, int> result = new(StringComparer.Ordinal);
        HashSet<int> bytes = [];
        foreach (Seed seed in Seeds)
        {
            EnumMemberDeclarationSyntax member = RequireSingle(instruction.Members,
                candidate => candidate.Identifier.ValueText == seed.Instruction,
                $"Instruction.{seed.Instruction} must be declared exactly once.");
            if (member.EqualsValue?.Value is not LiteralExpressionSyntax literal ||
                !int.TryParse(literal.Token.ValueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ||
                value is < byte.MinValue or > byte.MaxValue || value != seed.ExpectedByte)
                throw new ExtractionException($"Instruction.{seed.Instruction} must remain exact byte 0x{seed.ExpectedByte:x2}.");
            if (!bytes.Add(value))
                throw new ExtractionException($"Selected account-read opcode byte 0x{value:x2} is duplicated.");
            result.Add(seed.Instruction, value);
        }
        return result;
    }

    private static void ValidateReachability(Dictionary<string, SourceFile> sources, List<NodeAdmission> admissions)
    {
        ClassDeclarationSyntax ethereumVm = RequireType<ClassDeclarationSyntax>(sources[VirtualMachinePath].Root, "EthereumVirtualMachine", 0);
        Admit(admissions, sources[VirtualMachinePath], "EthereumVirtualMachine", ethereumVm);
        if (ethereumVm.BaseList?.Types is not [BaseTypeSyntax closed, BaseTypeSyntax service] ||
            Canonical(closed.Type) != "VirtualMachine<EthereumGasPolicy>" || Canonical(service.Type) != "IVirtualMachine")
            throw new ExtractionException("Rejected standard VM closed type.");
        Require(Canonical(ethereumVm), "publicsealedclassEthereumVirtualMachine", "public sealed standard VM implementation");

        ClassDeclarationSyntax module = RequireType<ClassDeclarationSyntax>(sources[MainnetDiPath].Root, "BlockProcessingModule", 0);
        MethodDeclarationSyntax load = RequireMethod(module, "Load", 0, 1);
        Admit(admissions, sources[MainnetDiPath], "BlockProcessingModule.Load/1", load);
        Require(Canonical(load), "AddScoped<IVirtualMachine,EthereumVirtualMachine>", "mainnet IVirtualMachine registration");
        Require(Canonical(load), "AddScoped<IWorldState,WorldState>", "mainnet IWorldState registration");
        Require(Canonical(load), "AddScoped<ICodeInfoRepository,CacheCodeInfoRepository>", "mainnet code repository registration");
        Require(Canonical(load), "AddSingleton<IPrecompileProvider,EthereumPrecompileProvider>", "mainnet precompile provider registration");
        Require(Canonical(load), "AddSingleton<ICodeCache>(StaticCodeCache.Instance)", "mainnet code cache registration");
        RejectNestedControl(load, "mainnet IVirtualMachine registration", static node => node is InvocationExpressionSyntax invocation &&
            Canonical(invocation.Expression).EndsWith("AddScoped<IVirtualMachine,EthereumVirtualMachine>", StringComparison.Ordinal));
        RejectNestedControl(load, "mainnet IWorldState registration", static node => node is InvocationExpressionSyntax invocation &&
            Canonical(invocation.Expression).EndsWith("AddScoped<IWorldState,WorldState>", StringComparison.Ordinal));
        RejectNestedControl(load, "mainnet code repository registration", static node => node is InvocationExpressionSyntax invocation &&
            Canonical(invocation.Expression).EndsWith("AddScoped<ICodeInfoRepository,CacheCodeInfoRepository>", StringComparison.Ordinal));
        RejectNestedControl(load, "mainnet precompile provider registration", static node => node is InvocationExpressionSyntax invocation &&
            Canonical(invocation.Expression).EndsWith("AddSingleton<IPrecompileProvider,EthereumPrecompileProvider>", StringComparison.Ordinal));
        RejectNestedControl(load, "mainnet code cache registration", static node => node is InvocationExpressionSyntax invocation &&
            Canonical(invocation.Expression).EndsWith("AddSingleton<ICodeCache>", StringComparison.Ordinal));

        ClassDeclarationSyntax dispatchVm = RequireType<ClassDeclarationSyntax>(sources[DispatchPath].Root, "VirtualMachine", 1);
        foreach ((string Name, int Arity, int Parameters, string Identity) item in new[]
        {
            ("GetOpcodeHandlers", 2, 0, "VirtualMachine.GetOpcodeHandlers/0"),
            ("PrepareOpcodes", 1, 0, "VirtualMachine.PrepareOpcodes/1"),
            ("PrepareOpcodes", 2, 0, "VirtualMachine.PrepareOpcodes/2"),
            ("RunDispatchLoop", 2, 3, "VirtualMachine.RunDispatchLoop/3"),
            ("ExecuteOpcode", 4, 5, "VirtualMachine.ExecuteOpcode/5"),
            ("ExitCheckedOpcode", 0, 4, "VirtualMachine.ExitCheckedOpcode/4"),
        })
            Admit(admissions, sources[DispatchPath], item.Identity, RequireMethod(dispatchVm, item.Name, item.Arity, item.Parameters));
        ClassDeclarationSyntax opcodeTable = RequireNestedType<ClassDeclarationSyntax>(dispatchVm, "OpcodeTable", 0);
        Admit(admissions, sources[DispatchPath], "OpcodeTable.GetHandlers/1", RequireMethod(opcodeTable, "GetHandlers", 2, 1));

        ClassDeclarationSyntax stdVm = RequireType<ClassDeclarationSyntax>(sources[StandardVirtualMachinePath].Root, "VirtualMachine", 1);
        FieldDeclarationSyntax tableCache = RequireSingle(stdVm.Members.OfType<FieldDeclarationSyntax>(), static field =>
            field.Declaration.Variables.Any(variable => variable.Identifier.ValueText == "_opcodeTablesBySpec"),
            "The standard opcode table cache must be declared once.");
        MethodDeclarationSyntax getTable = RequireMethod(stdVm, "GetOpcodeTable", 0, 0);
        MethodDeclarationSyntax refresh = RequireMethod(stdVm, "ShouldRefreshOpcodes", 0, 0);
        Admit(admissions, sources[StandardVirtualMachinePath], "VirtualMachine.std._opcodeTablesBySpec", tableCache);
        Admit(admissions, sources[StandardVirtualMachinePath], "VirtualMachine.std.GetOpcodeTable/0", getTable);
        Admit(admissions, sources[StandardVirtualMachinePath], "VirtualMachine.std.ShouldRefreshOpcodes/0", refresh);
        Require(Canonical(tableCache), "ConditionalWeakTable<IReleaseSpec,OpcodeTable>_opcodeTablesBySpec=[]", "empty per-spec opcode table cache");
        Require(Canonical(getTable), "_opcodeTablesBySpec.GetValue(Spec,static_=>newOpcodeTable())", "fresh standard opcode table factory");

        ClassDeclarationSyntax handlersVm = RequireType<ClassDeclarationSyntax>(sources[HandlersPath].Root, "VirtualMachine", 1);
        foreach ((string Name, int Arity, int Parameters, string Identity) item in new[]
        {
            ("OpcodeHandler", 3, 0, "VirtualMachine.OpcodeHandler/0"),
            ("GenerateOpcodeHandlers", 2, 1, "VirtualMachine.GenerateOpcodeHandlers/1"),
            ("ConfigureAccessOpcodes", 3, 2, "VirtualMachine.ConfigureAccessOpcodes/3"),
            ("ConfigureAccessOpcodes", 4, 2, "VirtualMachine.ConfigureAccessOpcodes/4"),
        })
            Admit(admissions, sources[HandlersPath], item.Identity, RequireMethod(handlersVm, item.Name, item.Arity, item.Parameters));
        Require(Canonical(RequireMethod(handlersVm, "OpcodeHandler", 3, 0)),
            "&ExecuteOpcode<TOpcode,TTracingInst,TCancelable,OnFlag>", "ordinary opcode execution root");
        MethodDeclarationSyntax generate = RequireMethod(handlersVm, "GenerateOpcodeHandlers", 2, 1);
        Require(Canonical(generate), "if(SpecFlags.Eip2929(spec))ConfigureAccessOpcodes<TTracingInst,TCancelable,OnFlag>(lookup,spec);elseConfigureAccessOpcodes<TTracingInst,TCancelable,OffFlag>(lookup,spec);", "EIP-2929 access dispatch");
        MethodDeclarationSyntax access3 = RequireMethod(handlersVm, "ConfigureAccessOpcodes", 3, 2);
        Require(Canonical(access3), "if(SpecFlags.Eip8038(spec))ConfigureAccessOpcodes<TTracingInst,TCancelable,Eip2929,Eip8038On>(lookup,spec);elseConfigureAccessOpcodes<TTracingInst,TCancelable,Eip2929,Eip8038Off>(lookup,spec);", "EIP-8038 access dispatch");

        ClassDeclarationSyntax extensions = RequireType<ClassDeclarationSyntax>(sources[ReleaseExtensionsPath].Root, "IReleaseSpecExtensions", 0);
        PropertyDeclarationSyntax extCodeHash = RequireSingle(extensions.DescendantNodes().OfType<PropertyDeclarationSyntax>(),
            static property => property.Identifier.ValueText == "ExtCodeHashOpcodeEnabled",
            "ExtCodeHashOpcodeEnabled must be declared exactly once.");
        Admit(admissions, sources[ReleaseExtensionsPath], "IReleaseSpecExtensions.ExtCodeHashOpcodeEnabled", extCodeHash);
        Require(Canonical(extCodeHash), "spec.IsEip1052Enabled", "EXTCODEHASH activation projection");
        ClassDeclarationSyntax standardExtensions = RequireType<ClassDeclarationSyntax>(sources[StandardReleaseExtensionsPath].Root, "IReleaseSpecExtensions", 0);
        PropertyDeclarationSyntax hotCold = RequireSingle(standardExtensions.DescendantNodes().OfType<PropertyDeclarationSyntax>(),
            static property => property.Identifier.ValueText == "UseHotAndColdStorage",
            "UseHotAndColdStorage must be declared exactly once.");
        Admit(admissions, sources[StandardReleaseExtensionsPath], "IReleaseSpecExtensions.std.UseHotAndColdStorage", hotCold);
        Require(Canonical(hotCold), "spec.IsEip2929Enabled", "EIP-2929 activation projection");

        ValidateSpecFlags(sources, admissions);
        ValidateForks(sources, admissions);
        ValidateBuildSelection(sources[BuildTargetsPath]);
    }

    private static void ValidateSpecFlags(Dictionary<string, SourceFile> sources, List<NodeAdmission> admissions)
    {
        ClassDeclarationSyntax flags = RequireType<ClassDeclarationSyntax>(sources[SpecFlagsPath].Root, "SpecFlags", 0);
        foreach (string name in new[] { "Eip2929", "Eip8038" })
        {
            MethodDeclarationSyntax method = RequireSingle(flags.Members.OfType<MethodDeclarationSyntax>(),
                candidate => candidate.Identifier.ValueText == name, $"SpecFlags.{name} must be declared once.");
            Admit(admissions, sources[SpecFlagsPath], $"SpecFlags.{name}", method);
            Require(Canonical(method), name == "Eip2929" ? "spec.UseHotAndColdStorage" : "spec.IsEip8038Enabled",
                $"SpecFlags.{name} release-spec projection");
        }
        foreach (string type in new[] { "IFlag", "OnFlag", "OffFlag" })
            Admit(admissions, sources[TypeFlagsPath], type, RequireType<TypeDeclarationSyntax>(sources[TypeFlagsPath].Root, type, 0));
        foreach (string type in new[] { "IEip8038Flag", "Eip8038On", "Eip8038Off" })
            Admit(admissions, sources[Eip8038FlagPath], type,
                RequireType<TypeDeclarationSyntax>(sources[Eip8038FlagPath].Root, type, 0));
    }

    private static void ValidateForks(Dictionary<string, SourceFile> sources, List<NodeAdmission> admissions)
    {
        ClassDeclarationSyntax named = RequireType<ClassDeclarationSyntax>(sources[NamedReleaseSpecPath].Root, "NamedReleaseSpec", 0);
        ClassDeclarationSyntax generic = RequireType<ClassDeclarationSyntax>(sources[NamedReleaseSpecPath].Root, "NamedReleaseSpec", 1);
        Admit(admissions, sources[NamedReleaseSpecPath], "NamedReleaseSpec", named);
        Admit(admissions, sources[NamedReleaseSpecPath], "NamedReleaseSpec<TSelf>", generic);
        Require(Canonical(named), "ReplayAncestors(this)", "named-fork construction replay");
        Require(Canonical(named), "ReplayAncestors(fork.Parent);fork.Apply(this);", "root-first fork replay");
        Require(Canonical(generic), "publicstaticNamedReleaseSpecInstance{get;}=newTSelf();", "named-fork singleton construction");

        Dictionary<string, ClassDeclarationSyntax> forks = new(StringComparer.Ordinal);
        for (int index = 0; index < ForkPaths.Length; index++)
        {
            SourceFile source = sources[ForkPaths[index]];
            ClassDeclarationSyntax fork = RequireType<ClassDeclarationSyntax>(source.Root, ForkNames[index], 0);
            Admit(admissions, source, $"fork {ForkNames[index]}", fork);
            string expectedBase = index == 0
                ? "NamedReleaseSpec<Olympic>(null)"
                : $"NamedReleaseSpec<{ForkNames[index]}>({ForkNames[index - 1]}.Instance)";
            if (Canonical(fork.BaseList!) != ":" + expectedBase)
                throw new ExtractionException($"Fork {ForkNames[index]} no longer has exact parent {expectedBase}.");
            forks.Add(ForkNames[index], fork);
        }
        Require(Canonical(forks["Berlin"]), "spec.IsEip2929Enabled=true;", "Berlin EIP-2929 activation");
        Require(Canonical(forks["Constantinople"]), "spec.IsEip1052Enabled=true;", "Constantinople EXTCODEHASH activation");
        Require(Canonical(forks["Amsterdam"]), "spec.IsEip8038Enabled=true;", "Amsterdam EIP-8038 activation");
    }

    private static void ValidateBuildSelection(SourceFile source)
    {
        if (source.Hash != BuildTargetsSha256)
            throw new ExtractionException($"Unadmitted Directory.Build.targets content; expected SHA-256 {BuildTargetsSha256}.");
        XDocument document;
        try
        {
            document = XDocument.Parse(source.Text.ToString(), LoadOptions.PreserveWhitespace);
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or InvalidOperationException)
        {
            throw new ExtractionException($"Directory.Build.targets is not valid XML: {exception.Message}");
        }
        XElement[] groups = document.Root?.Elements("ItemGroup").ToArray() ?? [];
        ValidateBuildGroup(groups, "'$(EnableZkEvm)' == 'true'", "**/std/**/*.cs", "**/*.std.cs", "zkEVM");
        ValidateBuildGroup(groups, "'$(EnableZkEvm)' != 'true'", "**/zkevm/**/*.cs", "**/*.zkevm.cs", "standard");
    }

    private static void ValidateBuildGroup(IReadOnlyList<XElement> groups, string condition,
        string directoryExcluded, string suffixExcluded, string flavor)
    {
        XElement[] matching = groups.Where(group => (string?)group.Attribute("Condition") == condition).ToArray();
        string[] exclusions = [directoryExcluded, suffixExcluded];
        if (matching.Length != 1 || matching[0].Elements().Count() != 4 || exclusions.Any(excluded =>
                matching[0].Elements("Compile").Count(element => (string?)element.Attribute("Remove") == excluded && element.Attributes().Count() == 1) != 1 ||
                matching[0].Elements("None").Count(element => (string?)element.Attribute("Include") == excluded && element.Attributes().Count() == 1) != 1))
            throw new ExtractionException($"Directory.Build.targets must contain one exact mutually exclusive {flavor} selection group for {directoryExcluded} and {suffixExcluded}.");
    }

    private static ScheduleDescriptor ValidateGasAndAccess(Dictionary<string, SourceFile> sources, List<NodeAdmission> admissions)
    {
        ClassDeclarationSyntax costs = RequireType<ClassDeclarationSyntax>(sources[GasCostPath].Root, "GasCostOf", 0);
        ClassDeclarationSyntax constants = RequireType<ClassDeclarationSyntax>(sources[Eip8038ConstantsPath].Root, "Eip8038Constants", 0);
        ClassDeclarationSyntax specCosts = RequireType<ClassDeclarationSyntax>(sources[SpecGasCostsPath].Root, "SpecGasCosts", 0);
        Admit(admissions, sources[GasCostPath], "GasCostOf", costs);
        Admit(admissions, sources[Eip8038ConstantsPath], "Eip8038Constants", constants);
        Admit(admissions, sources[SpecGasCostsPath], "SpecGasCosts", specCosts);
        Require(Canonical(costs), "publicconstulongVeryLow=3;", "very-low gas constant");
        Require(Canonical(costs), "publicconstulongMemory=3;", "copy-word/memory linear gas constant");
        Require(Canonical(costs), "publicconstulongWarmStateRead=100;", "warm access gas constant");
        Require(Canonical(constants), "publicconstulongColdAccountAccess=3000;", "EIP-8038 cold account gas");
        Require(Canonical(constants), "publicconstulongWarmAccess=GasCostOf.WarmStateRead;", "EIP-8038 warm account gas");
        string specGas = Canonical(specCosts);
        Require(specGas, "BalanceCost=hotCold?GasCostOf.Free", "hot/cold BALANCE base gas");
        Require(specGas, "ExtCodeHashCost=hotCold?GasCostOf.Free", "hot/cold EXTCODEHASH base gas");
        Require(specGas, "ExtCodeCost=hotCold?GasCostOf.Free", "hot/cold EXTCODESIZE/EXTCODECOPY base gas");

        foreach (string tag in new[] { "BalanceGasCost", "ExtCodeHashGasCost", "ExtCodeSizeGasCost", "VeryLowGasCost" })
            Admit(admissions, sources[GasTagsPath], tag, RequireType<StructDeclarationSyntax>(sources[GasTagsPath].Root, tag, 0));

        InterfaceDeclarationSyntax policyInterface = RequireType<InterfaceDeclarationSyntax>(sources[GasPolicyInterfacePath].Root, "IGasPolicy", 1);
        foreach ((string Name, int Arity, int Count, string Identity) item in new[]
        {
            ("UpdateGas", 0, 2, "IGasPolicy.UpdateGas/2"),
            ("UpdateGas", 1, 2, "IGasPolicy.UpdateGas<T>/2"),
            ("TryConsumeAccountAccessGas", 2, 6, "IGasPolicy.TryConsumeAccountAccessGas/6"),
            ("TryConsumeDataCopyGas", 0, 4, "IGasPolicy.TryConsumeDataCopyGas/4"),
        })
            Admit(admissions, sources[GasPolicyInterfacePath], item.Identity, RequireMethod(policyInterface, item.Name, item.Arity, item.Count));
        Admit(admissions, sources[GasPolicyInterfacePath], "IGasPolicy.UpdateMemoryCost.UInt256/4",
            RequireMethodByParameterTypes(policyInterface, "UpdateMemoryCost", 0, "TSelf,UInt256,UInt256,EvmPooledMemory"));

        StructDeclarationSyntax policy = RequireType<StructDeclarationSyntax>(sources[EthereumGasPolicyPath].Root, "EthereumGasPolicy", 0);
        foreach ((string Name, int Arity, int Count, string Identity) item in new[]
        {
            ("TryConsumeAccountAccessGasCore", 2, 6, "EthereumGasPolicy.TryConsumeAccountAccessGasCore/6"),
            ("TryConsumeAccountAccessGas", 2, 6, "EthereumGasPolicy.TryConsumeAccountAccessGas/6"),
            ("UpdateGas", 0, 2, "EthereumGasPolicy.UpdateGas/2"),
            ("TryConsumeDataCopyGas", 0, 4, "EthereumGasPolicy.TryConsumeDataCopyGas/4"),
        })
            Admit(admissions, sources[EthereumGasPolicyPath], item.Identity, RequireMethod(policy, item.Name, item.Arity, item.Count));
        Admit(admissions, sources[EthereumGasPolicyPath], "EthereumGasPolicy.UpdateMemoryCost.UInt256/4",
            RequireMethodByParameterTypes(policy, "UpdateMemoryCost", 0, "EthereumGasPolicy,UInt256,UInt256,EvmPooledMemory"));
        MethodDeclarationSyntax access = RequireMethod(policy, "TryConsumeAccountAccessGasCore", 2, 6);
        string accessBody = Canonical(access);
        RequireOrdered(accessBody, "accessTracker.WarmUp(address);", "boolisCold=accessTracker.WarmUp(address);", "access-tracing pre-warm before priced warm-up");
        RequireOrdered(accessBody, "boolisCold=accessTracker.WarmUp(address);", "boolisPrecompile=isCold&&spec.IsPrecompile(address);", "warm-up before precompile classification");
        RequireOrdered(accessBody, "AccountAccessPricingKernel.Price(", "UpdateGas(refgas,pricing.Amount)", "pricing before gas debit");

        foreach (string name in new[] { "AccountAccessPricingDecision", "AccountAccessPricingResult", "AccountAccessPricingKernel" })
            Admit(admissions, sources[AccountPricingPath], name,
                name == "AccountAccessPricingDecision"
                    ? RequireType<EnumDeclarationSyntax>(sources[AccountPricingPath].Root, name, 0)
                    : RequireType<TypeDeclarationSyntax>(sources[AccountPricingPath].Root, name, 0));
        Admit(admissions, sources[AccountAccessKindPath], "AccountAccessKind",
            RequireType<EnumDeclarationSyntax>(sources[AccountAccessKindPath].Root, "AccountAccessKind", 0));
        return new(0, 3000, 100, 3, 3, 512, 3);
    }

    private static OpcodeDescriptor[] ValidateOpcodes(string root, Dictionary<string, SourceFile> sources,
        Dictionary<string, int> instructionValues, List<NodeAdmission> admissions)
    {
        ClassDeclarationSyntax vm = RequireType<ClassDeclarationSyntax>(sources[HandlersPath].Root, "VirtualMachine", 1);
        MethodDeclarationSyntax access4 = RequireMethod(vm, "ConfigureAccessOpcodes", 4, 2);
        AssignmentExpressionSyntax[] allAssignments = vm.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(static assignment => TryInstructionAssignment(assignment, out _)).ToArray();
        AssignmentExpressionSyntax[] assignments = ExecutableDescendants(access4).OfType<AssignmentExpressionSyntax>()
            .Where(static assignment => TryInstructionAssignment(assignment, out _)).ToArray();
        ValidateNoCompetingDispatchAssignments(root);

        foreach (string wrapper in new[] { "BalanceOpcode", "ExtCodeSizeOpcode", "ExtCodeCopyOpcode", "ExtCodeHashOpcode" })
            Admit(admissions, sources[HandlersPath], wrapper, RequireNestedType<StructDeclarationSyntax>(vm, wrapper, wrapper is "BalanceOpcode" or "ExtCodeHashOpcode" ? 2 : 3));

        ClassDeclarationSyntax environment = RequireType<ClassDeclarationSyntax>(sources[EnvironmentPath].Root, "EvmInstructions", 0);
        MethodDeclarationSyntax balance = RequireMethod(environment, "InstructionBalance", 3, 3);
        MethodDeclarationSyntax hash = RequireMethod(environment, "InstructionExtCodeHash", 3, 3);
        Admit(admissions, sources[EnvironmentPath], "EvmInstructions.InstructionBalance/3", balance);
        Admit(admissions, sources[EnvironmentPath], "EvmInstructions.InstructionExtCodeHash/3", hash);
        ClassDeclarationSyntax instructionSpec = RequireType<ClassDeclarationSyntax>(sources[InstructionSpecPath].Root, "EvmInstructions", 0);
        TypeDeclarationSyntax accessSpec = RequireNestedType<TypeDeclarationSyntax>(instructionSpec, "AccessSpec", 2);
        InterfaceDeclarationSyntax accessInterface = RequireNestedType<InterfaceDeclarationSyntax>(instructionSpec, "IAccessSpec", 0);
        Admit(admissions, sources[InstructionSpecPath], "EvmInstructions.AccessSpec", accessSpec);
        Admit(admissions, sources[InstructionSpecPath], "EvmInstructions.IAccessSpec", accessInterface);

        ClassDeclarationSyntax copy = RequireType<ClassDeclarationSyntax>(sources[CodeCopyPath].Root, "EvmInstructions", 0);
        MethodDeclarationSyntax size = RequireMethod(copy, "InstructionExtCodeSize", 4, 4);
        MethodDeclarationSyntax extCopy = RequireMethod(copy, "InstructionExtCodeCopy", 4, 3);
        Admit(admissions, sources[CodeCopyPath], "EvmInstructions.InstructionExtCodeSize/4", size);
        Admit(admissions, sources[CodeCopyPath], "EvmInstructions.InstructionExtCodeCopy/3", extCopy);

        ValidateOpcodeBody(balance, ["UpdateGas<BalanceGasCost>", "PopAddress(vm.AddressCache)", "TryConsumeAccountAccessGas", "WorldState.GetBalance(address)", "PushBalance"], "BALANCE");
        ValidateOpcodeBody(hash, ["UpdateGas<ExtCodeHashGasCost>", "PopAddress(vm.AddressCache)", "TryConsumeAccountAccessGas", "state.IsDeadAccount(address)", "state.GetCodeHash(address)", "Push32Bytes"], "EXTCODEHASH");
        ValidateOpcodeBody(size, ["UpdateGas<ExtCodeSizeGasCost>", "PopAddress(vm.AddressCache)", "TryConsumeAccountAccessGas", "Eip8038Constants.WarmAccess", "AddAccountRead(address)", "CodeSpan", "Instruction.ISZERO", "Instruction.GT", "Instruction.EQ", "PopLimbo", "OpCodeCount++", "programCounter++", "UpdateGas<VeryLowGasCost>", "IsContract(address)", "GetCachedCodeInfo(address,followDelegation:false", "PushUInt32"], "EXTCODESIZE");
        ValidateOpcodeBody(extCopy, ["PopAddress(vm.AddressCache)", "PopUInt256(outUInt256a,outUInt256b,outUInt256result)", "Div32Ceiling", "TryConsumeDataCopyGas", "outOfGas", "TryConsumeAccountAccessGas", "Eip8038Constants.WarmAccess", "UpdateMemoryCost", "AddAccountRead(address)", "GetCachedCodeInfo(address,followDelegation:false", "CopyFromZeroExtendedAfterGas(ina,externalCode,inb,(int)result)", "RecordBytecodeAccess(address)"], "EXTCODECOPY");

        List<OpcodeDescriptor> result = new(Seeds.Length);
        foreach (Seed seed in Seeds)
        {
            AssignmentExpressionSyntax[] selected = allAssignments.Where(assignment =>
            {
                TryInstructionAssignment(assignment, out string? instruction);
                return instruction == seed.Instruction;
            }).ToArray();
            if (selected.Length != 1 || !assignments.Contains(selected[0]))
                throw new ExtractionException($"Instruction.{seed.Instruction} must have exactly one production assignment in the closed access configurator.");
            string actual = Canonical(selected[0].Right);
            string expected = $"OpcodeHandler<{OpenHandlerRoute(seed)},TTracingInst,TCancelable>()";
            if (actual != expected)
                throw new ExtractionException($"Instruction.{seed.Instruction} dispatch target changed: {actual}.");
            string gate = DispatchGate(selected[0], access4);
            string expectedGate = seed.Instruction == "EXTCODEHASH" ? "spec.ExtCodeHashOpcodeEnabled" : "unconditional";
            if (gate != expectedGate)
                throw new ExtractionException($"Instruction.{seed.Instruction} activation gate changed from {expectedGate} to {gate}.");
            result.Add(new(seed.Name, seed.Instruction, instructionValues[seed.Instruction], seed.HandlerRoute,
                seed.ValueRule, seed.StackInputs, seed.StackOutputs, seed.PopsBeforeGas, seed.ChargesSecondRead,
                seed.EffectOrder));
        }
        return result.ToArray();
    }

    private static void ValidateOpcodeBody(MethodDeclarationSyntax method, string[] orderedTokens, string opcode)
    {
        string body = Canonical(method);
        int previous = -1;
        foreach (string token in orderedTokens)
        {
            int index = body.IndexOf(token, previous + 1, StringComparison.Ordinal);
            if (index < 0)
                throw new ExtractionException($"{opcode} no longer contains admitted ordered effect '{token}'.");
            previous = index;
        }
        foreach (SyntaxNode ancestor in method.DescendantNodes())
            if (ancestor is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax)
                throw new ExtractionException($"{opcode} must not hide selected effects in a closure or local function.");
    }

    private static void ValidateProviderAndRepresentationSurface(Dictionary<string, SourceFile> sources, List<NodeAdmission> admissions)
    {
        StructDeclarationSyntax tracker = RequireType<StructDeclarationSyntax>(sources[AccessTrackerPath].Root, "StackAccessTracker", 0);
        Admit(admissions, sources[AccessTrackerPath], "StackAccessTracker.WarmUp/1",
            RequireMethodByParameterTypes(tracker, "WarmUp", 0, "Address"));

        StructDeclarationSyntax stack = RequireType<StructDeclarationSyntax>(sources[StackPath].Root, "EvmStack", 0);
        foreach ((string Name, int Arity, string Parameters, string Identity) item in new[]
        {
            ("PopAddress", 0, "PoppedAddressCache", "EvmStack.PopAddress/1"),
            ("PopUInt256", 0, "UInt256,UInt256,UInt256", "EvmStack.PopUInt256/3"),
            ("PeekUInt256IsZero", 0, "", "EvmStack.PeekUInt256IsZero/0"),
            ("PopLimbo", 0, "", "EvmStack.PopLimbo/0"),
            ("PushUInt32", 2, "uint", "EvmStack.PushUInt32/1"),
            ("Push32Bytes", 2, "ValueHash256", "EvmStack.Push32Bytes/1"),
            ("PushZero", 2, "", "EvmStack.PushZero/0"),
            ("PushOne", 1, "", "EvmStack.PushOne/0"),
        })
            Admit(admissions, sources[StackPath], item.Identity,
                RequireMethodByParameterTypes(stack, item.Name, item.Arity, item.Parameters));

        StructDeclarationSyntax memory = RequireType<StructDeclarationSyntax>(sources[MemoryPath].Root, "EvmPooledMemory", 0);
        Admit(admissions, sources[MemoryPath], "EvmPooledMemory.CopyFromZeroExtendedAfterGas/4",
            RequireMethod(memory, "CopyFromZeroExtendedAfterGas", 0, 4));
        Admit(admissions, sources[MemoryPath], "EvmPooledMemory.CalculateMemoryCost.UInt256/3",
            RequireMethodByParameterTypes(memory, "CalculateMemoryCost", 0, "UInt256,UInt256,bool"));

        ClassDeclarationSyntax calculations = RequireType<ClassDeclarationSyntax>(sources[CalculationsPath].Root, "EvmCalculations", 0);
        Admit(admissions, sources[CalculationsPath], "EvmCalculations.Div32Ceiling.UInt256/2",
            RequireMethodByParameterTypes(calculations, "Div32Ceiling", 0, "UInt256,bool"));

        InterfaceDeclarationSyntax worldState = RequireType<InterfaceDeclarationSyntax>(sources[WorldStatePath].Root, "IWorldState", 0);
        foreach (string method in new[] { "GetBalance", "GetCodeHash", "AddAccountRead", "RecordBytecodeAccess" })
            Admit(admissions, sources[WorldStatePath], $"IWorldState.{method}",
                RequireSingle(worldState.Members.OfType<MethodDeclarationSyntax>(), candidate => candidate.Identifier.ValueText == method,
                    $"IWorldState.{method} must be declared once."));
        InterfaceDeclarationSyntax readOnly = RequireType<InterfaceDeclarationSyntax>(sources[ReadOnlyStatePath].Root, "IReadOnlyStateProvider", 0);
        foreach (string method in new[] { "IsContract", "IsDeadAccount" })
            Admit(admissions, sources[ReadOnlyStatePath], $"IReadOnlyStateProvider.{method}",
                RequireSingle(readOnly.Members.OfType<MethodDeclarationSyntax>(), candidate => candidate.Identifier.ValueText == method,
                    $"IReadOnlyStateProvider.{method} must be declared once."));

        InterfaceDeclarationSyntax repository = RequireType<InterfaceDeclarationSyntax>(sources[CodeRepositoryInterfacePath].Root, "ICodeInfoRepository", 0);
        Admit(admissions, sources[CodeRepositoryInterfacePath], "ICodeInfoRepository.GetCachedCodeInfo/4",
            RequireMethod(repository, "GetCachedCodeInfo", 0, 4));
        ClassDeclarationSyntax productionRepository = RequireType<ClassDeclarationSyntax>(sources[CodeRepositoryPath].Root, "CodeInfoRepository", 0);
        Admit(admissions, sources[CodeRepositoryPath], "CodeInfoRepository.GetCachedCodeInfo/4",
            RequireMethod(productionRepository, "GetCachedCodeInfo", 0, 4));
        Admit(admissions, sources[CodeRepositoryPath], "CodeInfoRepository.InternalGetCodeInfo/2",
            RequireMethod(productionRepository, "InternalGetCodeInfo", 0, 2));
        Admit(admissions, sources[CodeRepositoryPath], "CodeInfoRepository.GetCodeInfo/3",
            RequireMethod(productionRepository, "GetCodeInfo", 0, 3));
        ClassDeclarationSyntax cacheRepository = RequireType<ClassDeclarationSyntax>(sources[CacheCodeRepositoryPath].Root,
            "CacheCodeInfoRepository", 0);
        Admit(admissions, sources[CacheCodeRepositoryPath], "CacheCodeInfoRepository", cacheRepository);
        Require(Canonical(cacheRepository),
            "GetCachedCodeInfo(AddresscodeSource,boolfollowDelegation,IReleaseSpecvmSpec,outAddress?delegationAddress)=>_inner.GetCachedCodeInfo(codeSource,followDelegation,vmSpec,outdelegationAddress);",
            "cache code repository delegation route");
        ClassDeclarationSyntax codeInfo = RequireType<ClassDeclarationSyntax>(sources[CodeInfoPath].Root, "CodeInfo", 0);
        Admit(admissions, sources[CodeInfoPath], "CodeInfo", codeInfo);
    }

    private static void ValidateNoCompetingDispatchAssignments(string root)
    {
        Dictionary<string, int> counts = Seeds.ToDictionary(static seed => seed.Instruction, static _ => 0, StringComparer.Ordinal);
        string evmRoot = Path.Combine(root, "src", "Nethermind", "Nethermind.Evm");
        foreach (string path in Directory.EnumerateFiles(evmRoot, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(evmRoot, path).Replace('\\', '/');
            if (relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("zkevm/", StringComparison.OrdinalIgnoreCase) ||
                relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
                relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
                relative.Contains("/zkevm/", StringComparison.OrdinalIgnoreCase) ||
                relative.EndsWith(".zkevm.cs", StringComparison.OrdinalIgnoreCase))
                continue;
            CompilationUnitSyntax syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(path), ParseOptions, path).GetCompilationUnitRoot();
            foreach (AssignmentExpressionSyntax assignment in syntax.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (TryInstructionAssignment(assignment, out string? instruction) && instruction is not null && counts.ContainsKey(instruction))
                    counts[instruction]++;
            }
        }
        foreach ((string instruction, int count) in counts)
            if (count != 1)
                throw new ExtractionException($"Instruction.{instruction} has {count} standard-build dispatch assignments; exactly one is admitted.");
    }

    private static void ValidateFingerprints(List<NodeAdmission> admissions, Dictionary<string, SourceFile> sources)
    {
        Dictionary<string, NodeAdmission> unique = new(StringComparer.Ordinal);
        foreach (NodeAdmission admission in admissions)
            if (!unique.TryAdd(admission.Identity, admission))
                throw new ExtractionException($"Admission identity '{admission.Identity}' was selected more than once.");
        List<string> mismatches = [];
        foreach (NodeAdmission admission in unique.Values.OrderBy(static item => item.Identity, StringComparer.Ordinal))
        {
            string actual = TokenHash(admission.Node);
            if (!ExpectedNodeFingerprints.TryGetValue(admission.Identity, out string? expected) || expected != actual)
                mismatches.Add($"[\"{admission.Identity}\"] = \"{actual}\",");
        }
        foreach (string expected in ExpectedNodeFingerprints.Keys)
            if (!unique.ContainsKey(expected)) mismatches.Add($"unexpected admitted fingerprint identity: {expected}");
        if (sources[BuildTargetsPath].Hash != BuildTargetsSha256)
            mismatches.Add($"[\"{BuildTargetsPath}\"] = \"{sources[BuildTargetsPath].Hash}\",");
        if (mismatches.Count != 0)
            throw new ExtractionException("Complete selected-node token fingerprints changed:\n" + string.Join("\n", mismatches));
    }

    private static void RejectDiagnosticsAndDirectives(Dictionary<string, SourceFile> sources, List<NodeAdmission> admissions)
    {
        foreach (SourceFile source in sources.Values)
        {
            if (!source.IsCSharp) continue;
            Diagnostic? error = source.Tree.GetDiagnostics().FirstOrDefault(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            if (error is not null)
                throw new ExtractionException($"{source.RelativePath} contains a parse error: {error.GetMessage(CultureInfo.InvariantCulture)}");
            int index = 0;
            foreach (UsingDirectiveSyntax directive in source.Root.DescendantNodes().OfType<UsingDirectiveSyntax>().Where(static directive => directive.Alias is not null))
                Admit(admissions, source, $"using-alias {source.RelativePath}#{index++}", directive);
        }
    }

    private static string DispatchGate(AssignmentExpressionSyntax assignment, MethodDeclarationSyntax root)
    {
        SyntaxNode? current = assignment.Parent;
        string? gate = null;
        while (current is not null && current != root)
        {
            if (current is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or ForStatementSyntax or ForEachStatementSyntax or
                WhileStatementSyntax or DoStatementSyntax or SwitchStatementSyntax)
                throw new ExtractionException("Account-read dispatch must not be nested in closures, local functions, loops, or switches.");
            if (current is IfStatementSyntax conditional)
            {
                if (gate is not null || conditional.Else is not null && conditional.Else.Span.Contains(assignment.Span))
                    throw new ExtractionException("Account-read dispatch must have at most one positive activation gate.");
                gate = Canonical(conditional.Condition);
            }
            current = current.Parent;
        }
        return gate ?? "unconditional";
    }

    private static bool TryInstructionAssignment(AssignmentExpressionSyntax assignment, out string? instruction)
    {
        instruction = null;
        if (assignment.Left is not ElementAccessExpressionSyntax element || Canonical(element.Expression) != "lookup" ||
            element.ArgumentList.Arguments is not [ArgumentSyntax argument]) return false;
        string index = Canonical(argument.Expression);
        const string prefix = "(int)Instruction.";
        if (!index.StartsWith(prefix, StringComparison.Ordinal)) return false;
        instruction = index[prefix.Length..];
        return true;
    }

    private static void RejectNestedControl(SyntaxNode root, string description, Func<SyntaxNode, bool> predicate)
    {
        SyntaxNode selected = RequireSingle(root.DescendantNodes(), predicate, $"Expected one {description}.");
        if (selected.Ancestors().TakeWhile(ancestor => ancestor != root).Any(static ancestor =>
                ancestor is IfStatementSyntax or ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax or
                    SwitchStatementSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax))
            throw new ExtractionException($"{description} must be unconditional executable source.");
    }

    private static void RequireOrdered(string source, string first, string second, string description)
    {
        int firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        int secondIndex = source.IndexOf(second, StringComparison.Ordinal);
        if (firstIndex < 0 || secondIndex <= firstIndex)
            throw new ExtractionException($"Rejected {description}; expected '{first}' before '{second}'.");
    }

    private static string OpenHandlerRoute(Seed seed) => seed.Name switch
    {
        "balance" => "BalanceOpcode<TTracingInst,EvmInstructions.AccessSpec<Eip2929,Eip8038>>",
        "extCodeSize" => "ExtCodeSizeOpcode<TTracingInst,Eip8038,Eip2929>",
        "extCodeCopy" => "ExtCodeCopyOpcode<TTracingInst,Eip8038,Eip2929>",
        "extCodeHash" => "ExtCodeHashOpcode<TTracingInst,EvmInstructions.AccessSpec<Eip2929,Eip8038>>",
        _ => throw new UnreachableException(),
    };

    private static void Require(string source, string expected, string description)
    {
        if (!source.Contains(expected, StringComparison.Ordinal))
            throw new ExtractionException($"Rejected {description}; expected '{expected}'.");
    }

    private static IEnumerable<SyntaxNode> ExecutableDescendants(SyntaxNode node) => node.DescendantNodes(static candidate =>
        candidate is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax);

    private static void Admit(List<NodeAdmission> admissions, SourceFile source, string identity, SyntaxNode node)
    {
        if (node.DescendantTrivia(descendIntoTrivia: true).Any(static trivia =>
                trivia.IsDirective || trivia.IsKind(SyntaxKind.DisabledTextTrivia) || trivia.IsKind(SyntaxKind.SkippedTokensTrivia)))
            throw new ExtractionException($"{source.RelativePath} selected node '{identity}' contains unadmitted source.");
        admissions.Add(new(identity, source, node));
    }

    private static T RequireType<T>(CompilationUnitSyntax root, string name, int arity) where T : BaseTypeDeclarationSyntax =>
        RequireSingle(root.DescendantNodes().OfType<T>(), node => node.Identifier.ValueText == name && GenericArity(node) == arity,
            $"Type {name}`{arity} must be declared exactly once.");

    private static T RequireNestedType<T>(TypeDeclarationSyntax parent, string name, int arity) where T : TypeDeclarationSyntax =>
        RequireSingle(parent.Members.OfType<T>(), node => node.Identifier.ValueText == name && GenericArity(node) == arity,
            $"Nested type {parent.Identifier.ValueText}.{name}`{arity} must be declared exactly once.");

    private static MethodDeclarationSyntax RequireMethod(TypeDeclarationSyntax type, string name, int arity, int parameters) =>
        RequireSingle(type.Members.OfType<MethodDeclarationSyntax>(), node => node.Identifier.ValueText == name &&
            (node.TypeParameterList?.Parameters.Count ?? 0) == arity && node.ParameterList.Parameters.Count == parameters,
            $"Method {type.Identifier.ValueText}.{name}`{arity}/{parameters} must be declared exactly once.");

    private static MethodDeclarationSyntax RequireMethodByParameterTypes(TypeDeclarationSyntax type, string name, int arity, string parameterTypes) =>
        RequireSingle(type.Members.OfType<MethodDeclarationSyntax>(), node => node.Identifier.ValueText == name &&
            (node.TypeParameterList?.Parameters.Count ?? 0) == arity &&
            string.Join(',', node.ParameterList.Parameters.Select(static parameter => Canonical(parameter.Type!))) == parameterTypes,
            $"Method {type.Identifier.ValueText}.{name}`{arity}({parameterTypes}) must be declared exactly once.");

    private static int GenericArity(BaseTypeDeclarationSyntax node) => node is TypeDeclarationSyntax type
        ? type.TypeParameterList?.Parameters.Count ?? 0
        : 0;

    private static T RequireSingle<T>(IEnumerable<T> source, Func<T, bool> predicate, string message)
    {
        T[] matches = source.Where(predicate).ToArray();
        return matches.Length == 1 ? matches[0] : throw new ExtractionException($"{message} Found {matches.Length}.");
    }

    private static string Canonical(SyntaxNode node) => string.Concat(
        node.DescendantTokens(descendIntoTrivia: false).Select(static token => token.Text));

    private static string TokenHash(SyntaxNode node)
    {
        StringBuilder tokens = new();
        foreach (SyntaxToken token in node.DescendantTokens(descendIntoTrivia: false))
            tokens.Append(token.RawKind).Append(':').Append(token.Text.Length).Append(':').Append(token.Text).Append('\n');
        return Sha256(Encoding.UTF8.GetBytes(tokens.ToString()));
    }

    private static SourceFile Read(string root, string relativePath, bool isCSharp)
    {
        string path = Path.GetFullPath(Path.Combine(root, relativePath));
        EnsureWithin(root, path);
        byte[] bytes = File.ReadAllBytes(path);
        SourceText text = SourceText.From(bytes, bytes.Length, Encoding.UTF8, canBeEmbedded: true);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(isCSharp ? text : SourceText.From(string.Empty), ParseOptions, relativePath);
        return new(relativePath, Sha256(bytes), text, tree, (CompilationUnitSyntax)tree.GetRoot(), isCSharp);
    }

    private static byte[] Serialize<T>(T value) => Utf8WithoutBom.GetBytes(
        JsonSerializer.Serialize(value, JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void EnsureWithin(string root, string path)
    {
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ExtractionException($"Path escapes the admitted root: {path}");
    }

    private sealed record SourceFile(string RelativePath, string Hash, SourceText Text, SyntaxTree Tree, CompilationUnitSyntax Root, bool IsCSharp);
    private sealed record NodeAdmission(string Identity, SourceFile Source, SyntaxNode Node);
    private sealed record DispatchTableBinding(string Name, string TracingFlag, string CancelableFlag);
    private sealed record Seed(string Name, string Instruction, int ExpectedByte, string HandlerRoute, string ValueRule,
        int StackInputs, int StackOutputs, bool PopsBeforeGas, bool ChargesSecondRead, string EffectOrder);
}
