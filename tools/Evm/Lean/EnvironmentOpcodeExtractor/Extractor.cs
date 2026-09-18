// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Nethermind.Evm.Lean.EnvironmentOpcodeExtractor;

internal static class Extractor
{
    internal const string AncestorBaselineCommit = "b2478235e71e6a7ec2a509aa0155e25d5fdfff80";
    internal const string InstructionPath = "src/Nethermind/Nethermind.Evm/Instruction.cs";
    internal const string HandlersPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs";
    internal const string DispatchPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs";
    internal const string EnvironmentPath = "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Environment.cs";
    internal const string GasCostPath = "src/Nethermind/Nethermind.Core/GasCostOf.cs";
    internal const string GasTagsPath = "src/Nethermind/Nethermind.Evm/GasPolicy/IGasCost.cs";
    internal const string GasPolicyInterfacePath = "src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs";
    internal const string EthereumGasPolicyPath = "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs";
    internal const string ReleaseExtensionsPath = "src/Nethermind/Nethermind.Core/Specs/IReleaseSpecExtensions.cs";
    internal const string VirtualMachinePath = "src/Nethermind/Nethermind.Evm/VirtualMachine.cs";
    internal const string StandardVirtualMachinePath = "src/Nethermind/Nethermind.Evm/VirtualMachine.std.cs";
    internal const string MainnetDiPath = "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs";
    internal const string NamedReleaseSpecPath = "src/Nethermind/Nethermind.Specs/Forks/NamedReleaseSpec.cs";
    internal const string BuildTargetsPath = "src/Nethermind/Directory.Build.targets";

    private const int SchemaVersion = 2;
    private const string ExtractorVersion = "1.1.0";
    private const string IrFileName = "EnvironmentOpcodeKernel.ir.json";
    private const string ManifestFileName = "EnvironmentOpcodeKernel.source-manifest.json";
    private const string DefaultLeanPath = "tools/Evm/Lean/Eip803x/Generated/EnvironmentOpcodeKernel.lean";
    private const string Kernel = "Nethermind standard-mainnet Amsterdam context/environment opcode slice";

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
        EnvironmentPath,
        GasCostPath,
        GasTagsPath,
        GasPolicyInterfacePath,
        EthereumGasPolicyPath,
        ReleaseExtensionsPath,
        VirtualMachinePath,
        StandardVirtualMachinePath,
        MainnetDiPath,
        NamedReleaseSpecPath,
        BuildTargetsPath,
        .. ForkPaths,
    ];

    private static readonly DispatchTableBinding[] DispatchTables =
    [
        new("NoTrace", "OffFlag", "OffFlag"),
        new("NoTraceCancelable", "OffFlag", "OnFlag"),
        new("Traced", "OnFlag", "OffFlag"),
        new("TracedCancelable", "OnFlag", "OnFlag"),
    ];

    private static readonly Seed[] Seeds =
    [
        new("address", "ADDRESS", "EnvAddressOpcode<EvmInstructions.OpAddress<TGasPolicy>,TTracingInst>", "unconditional", "executingAccount", 2, 0, 1, 0, 1, false, "OpAddress", "vmState.Env.ExecutingAccount"),
        new("origin", "ORIGIN", "Env32BytesOpcode<EvmInstructions.OpOrigin<TGasPolicy>,TTracingInst>", "unconditional", "origin", 2, 0, 1, 0, 1, false, "OpOrigin", "vm.TxExecutionContext.Origin"),
        new("caller", "CALLER", "EnvAddressOpcode<EvmInstructions.OpCaller<TGasPolicy>,TTracingInst>", "unconditional", "caller", 2, 0, 1, 0, 1, false, "OpCaller", "vmState.Env.Caller"),
        new("callvalue", "CALLVALUE", "EnvUInt256Opcode<EvmInstructions.OpCallValue<TGasPolicy>,TTracingInst>", "unconditional", "callValue", 2, 0, 1, 0, 1, false, "OpCallValue", "vmState.Env.Value"),
        new("calldatasize", "CALLDATASIZE", "EnvUInt32Opcode<EvmInstructions.OpCallDataSize<TGasPolicy>,TTracingInst>", "unconditional", "calldataSize", 2, 0, 1, 0, 1, false, "OpCallDataSize", "vmState.Env.InputData.Length"),
        new("codesize", "CODESIZE", "CodeSizeOpcode<TTracingInst>", "unconditional", "codeSize", 2, 0, 1, 0, 1, false, null, "stack.CodeLength"),
        new("gasprice", "GASPRICE", "BlkUInt256Opcode<EvmInstructions.OpGasPrice<TGasPolicy>,TTracingInst>", "unconditional", "gasPrice", 2, 0, 1, 0, 1, false, "OpGasPrice", "vm.TxExecutionContext.GasPrice"),
        new("returndatasize", "RETURNDATASIZE", "ReturnDataSizeOpcode<TTracingInst>", "spec.ReturnDataOpcodesEnabled", "returnDataSize", 2, 0, 1, 0, 1, false, null, "vm.ReturnDataBuffer.Length"),
        new("blockhash", "BLOCKHASH", "BlockHashOpcode<TTracingInst>", "unconditional", "blockHash", 20, 0, 0, 1, 1, false, null, "vm.BlockHashProvider.GetBlockhash"),
        new("coinbase", "COINBASE", "BlkAddressOpcode<EvmInstructions.OpCoinbase<TGasPolicy>,TTracingInst>", "unconditional", "coinbase", 2, 0, 1, 0, 1, false, "OpCoinbase", "vm.BlockExecutionContext.Coinbase"),
        new("timestamp", "TIMESTAMP", "BlkUInt64Opcode<EvmInstructions.OpTimestamp<TGasPolicy>,TTracingInst>", "unconditional", "timestamp", 2, 0, 1, 0, 1, false, "OpTimestamp", "vm.BlockExecutionContext.Header.Timestamp"),
        new("number", "NUMBER", "BlkUInt64Opcode<EvmInstructions.OpNumber<TGasPolicy>,TTracingInst>", "unconditional", "number", 2, 0, 1, 0, 1, false, "OpNumber", "vm.BlockExecutionContext.Number"),
        new("prevrandao", "PREVRANDAO", "PrevRandaoOpcode<TTracingInst>", "unconditional", "prevRandao", 2, 0, 1, 0, 1, false, null, "vm.BlockExecutionContext.PrevRandao"),
        new("gaslimit", "GASLIMIT", "BlkUInt64Opcode<EvmInstructions.OpGasLimit<TGasPolicy>,TTracingInst>", "unconditional", "gasLimit", 2, 0, 1, 0, 1, false, "OpGasLimit", "vm.BlockExecutionContext.GasLimit"),
        new("chainid", "CHAINID", "Env32BytesOpcode<EvmInstructions.OpChainId<TGasPolicy>,TTracingInst>", "spec.ChainIdOpcodeEnabled", "chainId", 2, 0, 1, 0, 1, false, "OpChainId", "vm.ChainId"),
        new("selfbalance", "SELFBALANCE", "SelfBalanceOpcode<TTracingInst>", "spec.SelfBalanceOpcodeEnabled", "selfBalance", 5, 0, 1, 0, 1, false, null, "vm.WorldState.GetBalance(vm.VmState.Env.ExecutingAccount)"),
        new("basefee", "BASEFEE", "BlkUInt256Opcode<EvmInstructions.OpBaseFee<TGasPolicy>,TTracingInst>", "spec.BaseFeeEnabled", "baseFee", 2, 0, 1, 0, 1, false, "OpBaseFee", "vm.BlockExecutionContext.Header.BaseFeePerGas"),
        new("blobhash", "BLOBHASH", "BlobHashOpcode<TTracingInst>", "spec.IsEip4844Enabled", "blobHash", 3, 0, 0, 1, 1, false, null, "vm.TxExecutionContext.BlobVersionedHashes"),
        new("blobbasefee", "BLOBBASEFEE", "BlobBaseFeeOpcode<TTracingInst>", "spec.BlobBaseFeeEnabled", "blobBaseFee", 2, 0, 0, 0, 1, true, null, "context.BlobBaseFee"),
        new("slotnum", "SLOTNUM", "SlotNumOpcode<TTracingInst>", "spec.IsEip7843Enabled", "slotNumber", 2, 0, 0, 0, 1, true, null, "context.Header.SlotNumber"),
    ];

    private static readonly Dictionary<string, string> ExpectedNodeFingerprints = new(StringComparer.Ordinal)
    {
        // Filled from a reviewed source snapshot. Every selected declaration is admitted by complete token hash.
        ["BaseGasCost"] = "616dae6d5a41ce01c4d923b412877b936950a370b60bc194c4b1f7b48533aaaf",
        ["BlkAddressOpcode"] = "014762fff1571c2e9cb929a2a055a8895a853f89ab24d0d83ab570ba8e9fa5a0",
        ["BlkUInt256Opcode"] = "6a18deb1280b3d04da7062a364f919af77a371ff02d8e2ca369a607934461d97",
        ["BlkUInt64Opcode"] = "85cb6cac25edde10e14fab7e2472f8372e62325ea38bd05076d0d6bc67c4683d",
        ["BlobBaseFeeOpcode"] = "fc81242d4afa88c787d54b77fd659e3bbe1ba41fe1790fdbc78907090a7dce43",
        ["BlobHashGasCost"] = "d8b911c49752f0d5ebe90c3093551313414f43c334f96019f9d62299aadaef14",
        ["BlobHashOpcode"] = "b04cf1f8682b4d05e0dcf3857a487f38d49b7814cbe78a1dac3ad611181f75c1",
        ["BlockHashGasCost"] = "1f6819e17b8c30f39319a654b86a34e99a74f2f1b280fdee6df86caa75e25032",
        ["BlockHashOpcode"] = "53f3fbcaafb58c29448e0dff9e5f985a27a950720580038cbda1e69eccc4bda3",
        ["BlockProcessingModule.Load/1"] = "3440f8d4a409a93055ceabd96d1c6aa7d028b3cb0f91f7c3ccfcc0d570b19f02",
        ["CodeSizeOpcode"] = "51ded880d8fc5dc40e6340c92b9344d4209141e10116bd45c354e609b2660714",
        ["Env32BytesOpcode"] = "1d4744342aec95584f7fe0601066cda0c306637d6fdbd5bc78923d652121d55e",
        ["EnvAddressOpcode"] = "b76ec5c91027e6cdd928da4d332ef1aeda382211d5a70c64ac5b4ea05aeb6456",
        ["EnvUInt256Opcode"] = "6539164a4ad275c15f4044346f2ba421c50252c4b1c9d54ad6c80487c4c56fe4",
        ["EnvUInt32Opcode"] = "064d6bba471e386dba58ffda578d0ba1f30c6ada8badd87dcb0796d7bcba3c22",
        ["EthereumGasPolicy.ConsumeRaw/2"] = "66e1bfb4f30bb99826ed00a69e83a71853f1336723ebce7df8ff9f510f622475",
        ["EthereumGasPolicy.GetRemainingGas/1"] = "2a5dd85e25eca7af471fa6eff86cf2a59933ea2b1236283b092e354d374828d2",
        ["EthereumGasPolicy.UpdateGas/2"] = "03603bdc2e651c10cd04098a1176a6389d517665999b5ef6ddd44b901428adf1",
        ["EthereumVirtualMachine"] = "e13aa6be01f10b34a21a58401cf4a1ee17c7e02ce1e4a1f37f7ad3f960ae99b0",
        ["EvmInstructions.InstructionBlkAddress/3"] = "f1ec177667ae068238843979ee10474c4e1a49b1eb0f52bb58d49e290a816c76",
        ["EvmInstructions.InstructionBlkUInt256/3"] = "939d0452e9062c78a5165d7fff23bfbe2d9061436e11eedc93ecd128d14170b0",
        ["EvmInstructions.InstructionBlkUInt64/3"] = "d5a29a8402d944e6fdfc22b0f846e3e0c64ed60139baaab679794dd46e5eba7a",
        ["EvmInstructions.InstructionBlobBaseFee/3"] = "575ff7c73f6382a9a670e9d8b38eb8070a76da2d11eef9a076b61e40650a12fe",
        ["EvmInstructions.InstructionBlobHash/3"] = "074186e58d598f5d9891610cf6bf8a00c583d0f499e762b5693aa8bc7cdc8bb3",
        ["EvmInstructions.InstructionBlockHash/3"] = "24d5855c6c7a38da5325ef58760d050d4a01154f2499871860bd16f7cadc4941",
        ["EvmInstructions.InstructionCodeSize/2"] = "a628beb8aec04e027e1778db30be81e92a763d061416a72d62161150f7642c41",
        ["EvmInstructions.InstructionEnv32Bytes/3"] = "53371b94496c7be8f079f9385458891541416e99051b7b6abd37e46ad9aced3e",
        ["EvmInstructions.InstructionEnvAddress/3"] = "df74260b5c9240ef767a29dee5f9dedfdcc2e0c40f3f9b3b97f5644c8c250d89",
        ["EvmInstructions.InstructionEnvUInt256/3"] = "0b77eacecfbbf92084b5da88ab5cf0b1e25095b1b260f9c120da2245e15934a2",
        ["EvmInstructions.InstructionEnvUInt32/3"] = "ec69de5c73b17923393751c9dc69652ef8b633b2d041819441bdf12e3272f336",
        ["EvmInstructions.InstructionPrevRandao/3"] = "dc945f97df786dbd85243232f606d85cd9d17266b8efdee085c43ff44646f166",
        ["EvmInstructions.InstructionReturnDataSize/3"] = "f14898076912aaede730a1d01679722296141da23aa60144849f3e7315d88e84",
        ["EvmInstructions.InstructionSelfBalance/3"] = "0fd092d4c11e2b2547643e207e7a97a001ba8ea34d11f7280519981f5d7537f6",
        ["EvmInstructions.InstructionSlotNum/3"] = "0ef45ea95ff7db6c595f5dd62bf2d5763e23e0064b17e05c1fc19344a1ff524c",
        ["EvmInstructions.PushBalance/2"] = "12eb6124fd195d548525f0afe2dd1b3c7e6ca7c360405faec6b0885952787b22",
        ["GasCostOf class"] = "a7f7043e7bb651797016c729210f29761e7bfbcb5a0c45ddf797f78533470ca2",
        ["IGasPolicy.UpdateGas<TCost>/1"] = "caa1431c748d1c087210e56a4300d7105afb4d0dd0242a86ec6838fff2a5ebab",
        ["IOpBlkAddress"] = "a4b39d3f513f477f91f2ba99fa9e99e58abcd4505d2273d512def75fc3cca401",
        ["IOpBlkUInt256"] = "9b4360b153f9d3e52a07033abce158986c7ff137a8f46646195a94dbb021c675",
        ["IOpBlkUInt64"] = "25271cc1aff7865887e0b3949c7a294db1c3cbcdd89c4e380c28cf3bf034caad",
        ["IOpEnv32Bytes"] = "c6bb50bf9ab9bbe9dfda90ba8b01a0220a37766122bdef7c90fb512ef345dff2",
        ["IOpEnvAddress"] = "fa403eda21c4390538007b385a2b7e966c7674c29353fb5195bc191c1fb1878d",
        ["IOpEnvUInt256"] = "611c3f734208a8a69569ccc0e652ddddda94d003950c1412ae6d2cff8a74a0f3",
        ["IOpEnvUInt32"] = "62beed4a404e52ec6323122f90c65dea3ca9c05874eb693ebfcfff35233f7699",
        ["IReleaseSpecExtensions"] = "ff8ed560fee73463e02344518facc38e0804d249cf64e4ff0812c05c0f7644b2",
        ["Instruction enum"] = "2304600d40aff5aca2cfe1d38415621cb3a4b38f22985472bf6f3c6b9e814c73",
        ["NamedReleaseSpec"] = "27580d1144b608d4073dffd90bc6cf41131ee88111d90f38f8a3c9abe806cd9e",
        ["NamedReleaseSpec<TSelf>"] = "38453abaa4725d671b85fe02af18e5b347bd62e865ddd0de90975463376df72a",
        ["OpAddress"] = "8ec0101c4a6997c5a6e66f334dd8d25fb0b6c4c9fe690ea1d1082084a233dca1",
        ["OpBaseFee"] = "30f6932a9a51c12ae3d25deecbdbfaa9c18bcdbe323637c0f85d62794093c748",
        ["OpCallDataSize"] = "095de63a2e21cb5bbdffa35b226c4577462b572688c1154154e6ea27e3b7a38c",
        ["OpCallValue"] = "898b072555d31005f1f59ae563ae067e3ca67a9f6847848336b13f763dfaf00c",
        ["OpCaller"] = "3e4044f5375fec83837b68c4245ab91dfaf3e9db296d1f03906f9b11dba34032",
        ["OpChainId"] = "83f1dc87704cda118bfa53867daaae209ea9a9f13183e3e7fbe59f98af6584a3",
        ["OpCoinbase"] = "d88e6a7a43b2203a12badbde5d8004443d6dce5487cfddba2bc6697444c3955c",
        ["OpGasLimit"] = "b51e5f949c495ee7cffd4ea4ce73afa34b7019b4f7e3e67be83b794699ff8fac",
        ["OpGasPrice"] = "4219270c670e97bd5da2fe18e2a945b69c80345267864d3be1531c5779225da5",
        ["OpNumber"] = "3c79c9c48d5f0d8ac3c255638e94f07d90fefd766e057800c524e9cbb0b509f1",
        ["OpOrigin"] = "63cbf68f09d74b419401c9416da1a53c51004eccb6f192a283ccd73a42e2a023",
        ["OpTimestamp"] = "b8df9ff25ad872e0eefbfa3cf6cfacf0a9fc66c1a3a33008405ad9eafbd5b97d",
        ["OpcodeTable.GetHandlers<TTracingInst,TCancelable>/1"] = "51190a7984712cbdcdf696e9012e89a096416479a08a7a5c56e2df8a299000fa",
        ["PrevRandaoOpcode"] = "b7bc5452d8cfe9152d149d7b96bcfd4fb8d4555696865837daf3b3183d43256a",
        ["ReturnDataSizeOpcode"] = "66db0f2cef44a4e78ecd28e109de70ef71928cddf791fbc110b9f1f6db425a47",
        ["SelfBalanceGasCost"] = "c7c864c28e3d930275d1de50f188ead211af861fc50e33bab38970bc98b86d0a",
        ["SelfBalanceOpcode"] = "1d1a032d312d76323020752ee8a8d19c80bcbcc1ee9c61ada6504c8c66665632",
        ["SlotNumOpcode"] = "5a2c3f83295c461215e6b3bc894bd7fdae9328de9cf8f4a139d8d13991c4b5bb",
        ["VirtualMachine.ExecuteOpcode<TOpcode,TTracingInst,TCancelable,TContinuable>/5"] = "57f66b4943a78716c342cae8e9877e61158fa254927c21226085ad683f868112",
        ["VirtualMachine.ExitCheckedOpcode/4"] = "418f4a900fafd15379651a6ce8e7be222e2f6820caaba4e624cafbfaca9b6409",
        ["VirtualMachine.GenerateOpcodeHandlers<TTracingInst,TCancelable>/1"] = "668f7e54f6c8ea211b14b012709b3b0796d80881765148e0e6957eb9c6c143af",
        ["VirtualMachine.GetOpcodeHandlers<TTracingInst,TCancelable>/0"] = "a78c615080c3521863e72d23e2d78d2f9181bfe085e7000bdbd0926da1fd459e",
        ["VirtualMachine.OpcodeHandler<TOpcode,TTracingInst,TCancelable>/0"] = "1baab10ac6604c84308404683fce8edc9462627d0ebfec38b1e6abac4d534288",
        ["VirtualMachine.PrepareOpcodes<TTracingInst,TCancelable>/0"] = "ddb410d0588f82abcc0dfb6fb676f53786efcfcaf7f323ff508b8c71f9052059",
        ["VirtualMachine.PrepareOpcodes<TTracingInst>/0"] = "cd972966980612759bbf08e3a2d87532b36adbbdce8a8a186b58faa8887a243e",
        ["VirtualMachine.RunDispatchLoop<TTracingInst,TCancelable>/3"] = "ae0e92e449246802c2f1d3ce62a22ffa380572cadb058d4bd9a4e3bbdc440b0e",
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
    };

    private static readonly Dictionary<string, string> ExpectedRawFingerprints = new(StringComparer.Ordinal)
    {
        // The standard-vs-zkEVM source selection target is admitted as complete raw bytes.
        [BuildTargetsPath] = "0598cebaef1df41102a18a3f9ace810bed1e4e64b8471d055724b4a394dccec6",
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
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static IReadOnlyList<string> SourcePaths => SourceRelativePaths;

    internal static ExtractionResult Extract(string repoRoot, string outputDirectory, string? leanOutputPath = null)
    {
        string canonicalRoot = Path.GetFullPath(repoRoot);
        Dictionary<string, SourceFile> sources = SourceRelativePaths.ToDictionary(
            static path => path,
            path => Read(canonicalRoot, path, path.EndsWith(".cs", StringComparison.Ordinal)),
            StringComparer.Ordinal);
        foreach (SourceFile source in sources.Values)
        {
            RejectDiagnosticsAndDirectives(source);
        }

        List<NodeAdmission> admissions = [];
        Dictionary<string, int> instructionValues = ValidateInstructionEnum(Get(sources, InstructionPath), admissions);
        ValidateGas(Get(sources, GasCostPath), Get(sources, GasTagsPath), Get(sources, GasPolicyInterfacePath), Get(sources, EthereumGasPolicyPath), admissions);
        ValidateReachability(sources, admissions);
        OpcodeDescriptor[] opcodes = ValidateOpcodes(sources, instructionValues, admissions);
        ValidateFingerprints(admissions, sources);
        OpcodeSpecialization[] specializations = BuildSpecializations(opcodes);

        IrDocument document = new(
            SchemaVersion,
            ExtractorVersion,
            AncestorBaselineCommit,
            Kernel,
            new ReachabilityDescriptor(
                "IVirtualMachine",
                "EthereumVirtualMachine",
                "VirtualMachine<EthereumGasPolicy>",
                "EthereumGasPolicy",
                "Amsterdam",
                "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode",
                ["IsEip211Enabled", "IsEip1344Enabled", "IsEip1884Enabled", "IsEip3198Enabled", "IsEip4844Enabled", "IsEip7843Enabled"]),
            opcodes,
            specializations,
            [
                "C# language, Roslyn binding, CLR/JIT, unsafe function-pointer dispatch, and hardware correctness remain outside the theorem.",
                "Address, ValueHash256, UInt256, byte order, EvmStack storage, and push/pop method representation are explicit adapter premises.",
                "IBlockhashProvider correctness and its 256-block rule are provider premises; the extracted model consumes the admitted provider table.",
                "World-state balance lookup, transaction/block context construction, blob arrays, and cached blob-base-fee calculation are provider premises.",
                "Tracing and cancellation callbacks are admitted for route preservation but their side effects and exception behavior are outside this slice.",
            ]);
        byte[] irBytes = Serialize(document);
        string irHash = Sha256(irBytes);
        IrDocument roundTripped = DeserializeIr(irBytes);
        byte[] leanBytes = LeanEmitter.Emit(roundTripped, irHash);

        string output = Path.GetFullPath(outputDirectory);
        string leanPath = Path.GetFullPath(leanOutputPath ?? Path.Combine(canonicalRoot, DefaultLeanPath));
        EnsureWithin(leanOutputPath is null ? canonicalRoot : output, leanPath);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(Path.GetDirectoryName(leanPath)!);
        string irPath = Path.Combine(output, IrFileName);
        string manifestPath = Path.Combine(output, ManifestFileName);
        Write(irPath, irBytes);
        Write(leanPath, leanBytes);

        NodeFingerprint[] fingerprints = admissions
            .OrderBy(AdmissionKey, StringComparer.Ordinal)
            .Select(admission => new NodeFingerprint(AdmissionKey(admission), admission.Source.RelativePath, admission.Node.Kind().ToString(), TokenHash(admission.Node)))
            .ToArray();
        Manifest manifest = new(
            SchemaVersion,
            ExtractorVersion,
            typeof(CSharpCompilation).Assembly.GetName().Version?.ToString() ?? "unknown",
            LanguageVersion.CSharp14.ToDisplayString(),
            AncestorBaselineCommit,
            Kernel,
            sources.Values.OrderBy(static source => source.RelativePath, StringComparer.Ordinal)
                .Select(static source => new SourceIdentity(source.RelativePath, source.Hash)).ToArray(),
            fingerprints,
            new ArtifactIdentity(IrFileName, irHash),
            new ArtifactIdentity(Normalize(DefaultLeanPath), Sha256(leanBytes)),
            [
                "BlockProcessingModule.Load -> IVirtualMachine/EthereumVirtualMachine -> VirtualMachine<EthereumGasPolicy>",
                "PrepareOpcodes -> OpcodeTable.GetHandlers -> GenerateOpcodeHandlers -> OpcodeHandler -> ExecuteOpcode",
                "Every environment opcode is closed through all four standard tracing/cancellation tables over EthereumGasPolicy",
                "Amsterdam ancestor replay enables every gated environment opcode in this slice",
                "Instruction byte -> unique dispatch assignment -> exact wrapper -> exact provider/instruction body -> semantic IR",
                "ExecuteOpcode increments PC and applies checked gas/stack guards; unchecked handlers preserve their admitted internal ordering",
            ]);
        Write(manifestPath, Serialize(manifest));
        return new ExtractionResult(irPath, manifestPath, leanPath, opcodes.Length, specializations.Length, sources.Count);
    }

    internal static IrDocument DeserializeIr(byte[] bytes)
    {
        if (bytes is null)
            throw new ExtractionException("The serialized environment-opcode IR input was null.");
        try
        {
            IrDocument document = JsonSerializer.Deserialize<IrDocument>(bytes, JsonOptions)
                ?? throw new ExtractionException("The serialized environment-opcode IR was empty.");
            ValidateIr(document);
            return document;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"The serialized environment-opcode IR is not valid JSON: {exception.Message}");
        }
    }

    internal static void ValidateIr(IrDocument document)
    {
        if (document is null || document.Reachability is null || document.Opcodes is null ||
            document.Specializations is null || document.ExternalObligations is null ||
            document.Reachability.EnabledForkFlags is null ||
            document.Opcodes.Any(static opcode => opcode is null || opcode.Name is null ||
                opcode.Instruction is null || opcode.HandlerRoute is null || opcode.ActivationGate is null ||
                opcode.ValueSelector is null) ||
            document.Specializations.Any(static specialization => specialization is null ||
                specialization.Opcode is null || specialization.DispatchTable is null ||
                specialization.TracingFlag is null || specialization.CancelableFlag is null ||
                specialization.ClosedRoot is null) ||
            document.ExternalObligations.Any(static obligation => obligation is null))
            throw new ExtractionException("The serialized environment-opcode IR contains a null value.");
        if (document.SchemaVersion != SchemaVersion || document.ExtractorVersion != ExtractorVersion ||
            document.AncestorBaselineCommit != AncestorBaselineCommit || document.Kernel != Kernel)
            throw new ExtractionException("The serialized environment-opcode IR header changed.");

        ReachabilityDescriptor expectedReachability = new(
            "IVirtualMachine", "EthereumVirtualMachine", "VirtualMachine<EthereumGasPolicy>",
            "EthereumGasPolicy", "Amsterdam", "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode",
            ["IsEip211Enabled", "IsEip1344Enabled", "IsEip1884Enabled", "IsEip3198Enabled", "IsEip4844Enabled", "IsEip7843Enabled"]);
        if ((document.Reachability with { EnabledForkFlags = expectedReachability.EnabledForkFlags }) != expectedReachability ||
            !document.Reachability.EnabledForkFlags.SequenceEqual(expectedReachability.EnabledForkFlags, StringComparer.Ordinal))
            throw new ExtractionException("The serialized environment-opcode reachability descriptor changed.");

        int[] opcodeBytes = [0x30, 0x32, 0x33, 0x34, 0x36, 0x38, 0x3a, 0x3d, 0x40, 0x41,
            0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x4b];
        OpcodeDescriptor[] expectedOpcodes = Seeds.Select((seed, index) => new OpcodeDescriptor(
            seed.Name, seed.Instruction, opcodeBytes[index], seed.HandlerRoute, seed.ActivationGate,
            seed.ValueSelector, seed.FixedGas, seed.DispatchStackInputs, seed.DispatchStackGrowth,
            seed.SemanticPops, seed.SemanticPushes, seed.ChecksAvailabilityBeforeGas)).ToArray();
        if (!document.Opcodes.SequenceEqual(expectedOpcodes))
            throw new ExtractionException("The serialized environment-opcode descriptors changed.");

        OpcodeSpecialization[] expectedSpecializations = expectedOpcodes
            .SelectMany(static opcode => DispatchTables.Select(table => ExpectedSpecialization(opcode, table)))
            .ToArray();
        ValidateSpecializations(document.Opcodes, document.Specializations);
        if (!document.Specializations.SequenceEqual(expectedSpecializations))
            throw new ExtractionException("The serialized environment-opcode specialization order or roots changed.");

        string[] expectedObligations =
        [
            "C# language, Roslyn binding, CLR/JIT, unsafe function-pointer dispatch, and hardware correctness remain outside the theorem.",
            "Address, ValueHash256, UInt256, byte order, EvmStack storage, and push/pop method representation are explicit adapter premises.",
            "IBlockhashProvider correctness and its 256-block rule are provider premises; the extracted model consumes the admitted provider table.",
            "World-state balance lookup, transaction/block context construction, blob arrays, and cached blob-base-fee calculation are provider premises.",
            "Tracing and cancellation callbacks are admitted for route preservation but their side effects and exception behavior are outside this slice.",
        ];
        if (!document.ExternalObligations.SequenceEqual(expectedObligations, StringComparer.Ordinal))
            throw new ExtractionException("The serialized environment-opcode extraction obligations changed.");
    }

    private static OpcodeSpecialization[] BuildSpecializations(IReadOnlyList<OpcodeDescriptor> opcodes)
    {
        OpcodeSpecialization[] specializations = opcodes
            .SelectMany(static opcode => DispatchTables.Select(table => ExpectedSpecialization(opcode, table)))
            .ToArray();
        ValidateSpecializations(opcodes, specializations);
        return specializations;
    }

    private static OpcodeSpecialization ExpectedSpecialization(OpcodeDescriptor opcode, DispatchTableBinding table)
    {
        string closedHandler = opcode.HandlerRoute
            .Replace("TTracingInst", table.TracingFlag, StringComparison.Ordinal)
            .Replace("TGasPolicy", "EthereumGasPolicy", StringComparison.Ordinal);
        string closedRoot =
            $"VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<{closedHandler},{table.TracingFlag},{table.CancelableFlag},OnFlag>";
        if (closedRoot.Contains("TTracingInst", StringComparison.Ordinal) ||
            closedRoot.Contains("TGasPolicy", StringComparison.Ordinal))
            throw new ExtractionException($"Opcode {opcode.Instruction} specialization {table.Name} is not closed: {closedRoot}.");
        return new(opcode.Name, table.Name, table.TracingFlag, table.CancelableFlag, closedRoot);
    }

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
            if (specialization.ClosedRoot.Contains("TTracingInst", StringComparison.Ordinal) ||
                specialization.ClosedRoot.Contains("TGasPolicy", StringComparison.Ordinal))
                throw new ExtractionException($"Specialization {specialization.Opcode}/{specialization.DispatchTable} is not closed.");
            if (!keys.Add((specialization.Opcode, specialization.DispatchTable)))
                throw new ExtractionException($"Duplicate specialization {specialization.Opcode}/{specialization.DispatchTable}.");
            if (!descriptors.TryGetValue(specialization.Opcode, out OpcodeDescriptor? opcode) ||
                !tables.TryGetValue(specialization.DispatchTable, out DispatchTableBinding? table))
                throw new ExtractionException($"Unknown specialization key {specialization.Opcode}/{specialization.DispatchTable}.");
            if (specialization != ExpectedSpecialization(opcode, table))
                throw new ExtractionException($"Specialization {specialization.Opcode}/{specialization.DispatchTable} does not match its exact closed production root.");
        }

        int expectedCount = opcodes.Count * DispatchTables.Length;
        if (specializations.Count != expectedCount || keys.Count != expectedCount)
            throw new ExtractionException($"Expected {expectedCount} exact closed roots but found {specializations.Count}.");
    }

    private static Dictionary<string, int> ValidateInstructionEnum(SourceFile source, List<NodeAdmission> admissions)
    {
        EnumDeclarationSyntax instruction = RequireSingle(source.Root.DescendantNodes().OfType<EnumDeclarationSyntax>(),
            static node => node.Identifier.ValueText == "Instruction", "Instruction enum must be declared exactly once.");
        Admit(admissions, source, "Instruction enum", instruction);
        Dictionary<string, int> values = new(StringComparer.Ordinal);
        HashSet<int> selected = [];
        foreach (Seed seed in Seeds)
        {
            EnumMemberDeclarationSyntax member = RequireSingle(instruction.Members, node => node.Identifier.ValueText == seed.Instruction,
                $"Instruction.{seed.Instruction} must be declared exactly once.");
            if (member.EqualsValue?.Value is not LiteralExpressionSyntax literal ||
                !int.TryParse(literal.Token.ValueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ||
                value is < byte.MinValue or > byte.MaxValue)
            {
                throw new ExtractionException($"Instruction.{seed.Instruction} must have an explicit byte-range numeric literal.");
            }
            if (!selected.Add(value))
                throw new ExtractionException($"Selected environment opcode byte 0x{value:x2} is duplicated.");
            values.Add(seed.Instruction, value);
        }
        if (values.Count != 20)
            throw new ExtractionException($"Expected 20 environment opcode bytes but found {values.Count}.");
        return values;
    }

    private static void ValidateGas(SourceFile costs, SourceFile tags, SourceFile policyInterface, SourceFile policy, List<NodeAdmission> admissions)
    {
        ClassDeclarationSyntax costType = RequireType<ClassDeclarationSyntax>(costs.Root, "GasCostOf", 0);
        Admit(admissions, costs, "GasCostOf class", costType);
        Dictionary<string, ulong> expected = new(StringComparer.Ordinal) { ["Base"] = 2, ["BlobHash"] = 3, ["BlockHash"] = 20, ["SelfBalance"] = 5 };
        foreach ((string name, ulong value) in expected)
        {
            FieldDeclarationSyntax field = RequireSingle(costType.Members.OfType<FieldDeclarationSyntax>(),
                node => node.Declaration.Variables.Count == 1 && node.Declaration.Variables[0].Identifier.ValueText == name,
                $"GasCostOf.{name} must be declared exactly once.");
            VariableDeclaratorSyntax variable = field.Declaration.Variables[0];
            if (variable.Initializer?.Value is not LiteralExpressionSyntax literal ||
                !ulong.TryParse(literal.Token.ValueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong actual) || actual != value)
                throw new ExtractionException($"GasCostOf.{name} must remain {value}.");
        }
        foreach (string typeName in new[] { "BaseGasCost", "BlobHashGasCost", "BlockHashGasCost", "SelfBalanceGasCost" })
        {
            StructDeclarationSyntax type = RequireType<StructDeclarationSyntax>(tags.Root, typeName, 0);
            Admit(admissions, tags, typeName, type);
            RequireContains(type, $"GasCostOf.{typeName[..^7]}", $"{typeName} must select its corresponding GasCostOf constant.");
        }
        InterfaceDeclarationSyntax gasPolicy = RequireType<InterfaceDeclarationSyntax>(policyInterface.Root, "IGasPolicy", 1);
        MethodDeclarationSyntax genericUpdate = RequireMethod(gasPolicy, "UpdateGas", 1, 1);
        Admit(admissions, policyInterface, "IGasPolicy.UpdateGas<TCost>/1", genericUpdate);
        RequireContains(genericUpdate, "TSelf.UpdateGas(refgas,TCost.GasCost)", "IGasPolicy generic fixed-gas forwarding changed.");

        StructDeclarationSyntax ethereum = RequireType<StructDeclarationSyntax>(policy.Root, "EthereumGasPolicy", 0);
        MethodDeclarationSyntax update = RequireMethod(ethereum, "UpdateGas", 0, 2);
        MethodDeclarationSyntax remaining = RequireMethod(ethereum, "GetRemainingGas", 0, 1);
        MethodDeclarationSyntax raw = RequireMethod(ethereum, "ConsumeRaw", 0, 2);
        Admit(admissions, policy, "EthereumGasPolicy.UpdateGas/2", update);
        Admit(admissions, policy, "EthereumGasPolicy.GetRemainingGas/1", remaining);
        Admit(admissions, policy, "EthereumGasPolicy.ConsumeRaw/2", raw);
        RequireContains(update, "if(GetRemainingGas(ingas)<gasCost){gas.Value=0;returnfalse;}ConsumeRaw(refgas,gasCost);returntrue;",
            "EthereumGasPolicy fixed-gas debit and OOG behavior changed.");
    }

    private static void ValidateReachability(Dictionary<string, SourceFile> sources, List<NodeAdmission> admissions)
    {
        SourceFile vmSource = Get(sources, VirtualMachinePath);
        ClassDeclarationSyntax vm = RequireType<ClassDeclarationSyntax>(vmSource.Root, "EthereumVirtualMachine", 0);
        Admit(admissions, vmSource, "EthereumVirtualMachine", vm);
        if (vm.BaseList?.Types is not [BaseTypeSyntax closed, BaseTypeSyntax service] ||
            Canonical(closed.Type) != "VirtualMachine<EthereumGasPolicy>" || Canonical(service.Type) != "IVirtualMachine" ||
            !vm.Modifiers.Any(SyntaxKind.PublicKeyword) || !vm.Modifiers.Any(SyntaxKind.SealedKeyword))
            throw new ExtractionException("EthereumVirtualMachine must remain the public sealed VirtualMachine<EthereumGasPolicy> specialization.");

        SourceFile moduleSource = Get(sources, MainnetDiPath);
        ClassDeclarationSyntax module = RequireType<ClassDeclarationSyntax>(moduleSource.Root, "BlockProcessingModule", 0);
        MethodDeclarationSyntax load = RequireMethod(module, "Load", 0, 1);
        Admit(admissions, moduleSource, "BlockProcessingModule.Load/1", load);
        InvocationExpressionSyntax[] registrations = ExecutableDescendants(load).OfType<InvocationExpressionSyntax>()
            .Where(static invocation => Canonical(invocation.Expression).EndsWith("AddScoped<IVirtualMachine,EthereumVirtualMachine>", StringComparison.Ordinal))
            .ToArray();
        if (registrations.Length != 1 || registrations[0].Ancestors().Any(static node =>
                node is IfStatementSyntax or ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax))
            throw new ExtractionException("BlockProcessingModule must contain one unconditional standard IVirtualMachine/EthereumVirtualMachine registration.");

        SourceFile dispatchSource = Get(sources, DispatchPath);
        ClassDeclarationSyntax dispatchVm = RequireType<ClassDeclarationSyntax>(dispatchSource.Root, "VirtualMachine", 1);
        foreach ((string name, int genericArity, int parameters, string identity) in new[]
        {
            ("GetOpcodeHandlers", 2, 0, "VirtualMachine.GetOpcodeHandlers<TTracingInst,TCancelable>/0"),
            ("PrepareOpcodes", 1, 0, "VirtualMachine.PrepareOpcodes<TTracingInst>/0"),
            ("PrepareOpcodes", 2, 0, "VirtualMachine.PrepareOpcodes<TTracingInst,TCancelable>/0"),
            ("RunDispatchLoop", 2, 3, "VirtualMachine.RunDispatchLoop<TTracingInst,TCancelable>/3"),
            ("ExecuteOpcode", 4, 5, "VirtualMachine.ExecuteOpcode<TOpcode,TTracingInst,TCancelable,TContinuable>/5"),
            ("ExitCheckedOpcode", 0, 4, "VirtualMachine.ExitCheckedOpcode/4"),
        })
        {
            Admit(admissions, dispatchSource, identity, RequireMethod(dispatchVm, name, genericArity, parameters));
        }
        ClassDeclarationSyntax table = RequireNestedType<ClassDeclarationSyntax>(dispatchVm, "OpcodeTable", 0);
        Admit(admissions, dispatchSource, "OpcodeTable.GetHandlers<TTracingInst,TCancelable>/1", RequireMethod(table, "GetHandlers", 2, 1));

        SourceFile standardSource = Get(sources, StandardVirtualMachinePath);
        ClassDeclarationSyntax standardVm = RequireType<ClassDeclarationSyntax>(standardSource.Root, "VirtualMachine", 1);
        MethodDeclarationSyntax getTable = RequireMethod(standardVm, "GetOpcodeTable", 0, 0);
        MethodDeclarationSyntax shouldRefresh = RequireMethod(standardVm, "ShouldRefreshOpcodes", 0, 0);
        FieldDeclarationSyntax opcodeTables = RequireSingle(standardVm.Members.OfType<FieldDeclarationSyntax>(),
            static field => field.Declaration.Variables is [VariableDeclaratorSyntax variable] &&
                variable.Identifier.ValueText == "_opcodeTablesBySpec",
            "The standard VM opcode-table cache must be declared exactly once.");
        Admit(admissions, standardSource, "VirtualMachine.std._opcodeTablesBySpec", opcodeTables);
        Admit(admissions, standardSource, "VirtualMachine.std.GetOpcodeTable/0", getTable);
        Admit(admissions, standardSource, "VirtualMachine.std.ShouldRefreshOpcodes/0", shouldRefresh);
        RequireContains(opcodeTables, "ConditionalWeakTable<IReleaseSpec,OpcodeTable>_opcodeTablesBySpec=[]",
            "The standard opcode-table cache must start empty and be keyed by release spec.");
        RequireContains(getTable, "_opcodeTablesBySpec.GetValue(Spec,static_=>newOpcodeTable())",
            "The standard VM must obtain a fresh empty opcode table per release spec.");

        SourceFile targets = Get(sources, BuildTargetsPath);
        ValidateBuildSourceSelection(targets);

        SourceFile handlersSource = Get(sources, HandlersPath);
        ClassDeclarationSyntax handlersVm = RequireType<ClassDeclarationSyntax>(handlersSource.Root, "VirtualMachine", 1);
        MethodDeclarationSyntax opcodeHandler = RequireMethod(handlersVm, "OpcodeHandler", 3, 0);
        MethodDeclarationSyntax generate = RequireMethod(handlersVm, "GenerateOpcodeHandlers", 2, 1);
        Admit(admissions, handlersSource, "VirtualMachine.OpcodeHandler<TOpcode,TTracingInst,TCancelable>/0", opcodeHandler);
        Admit(admissions, handlersSource, "VirtualMachine.GenerateOpcodeHandlers<TTracingInst,TCancelable>/1", generate);
        RequireContains(opcodeHandler, "&ExecuteOpcode<TOpcode,TTracingInst,TCancelable,OnFlag>", "Ordinary opcode handlers must route to continuable ExecuteOpcode.");

        SourceFile namedSource = Get(sources, NamedReleaseSpecPath);
        ClassDeclarationSyntax named = RequireType<ClassDeclarationSyntax>(namedSource.Root, "NamedReleaseSpec", 0);
        ClassDeclarationSyntax namedGeneric = RequireType<ClassDeclarationSyntax>(namedSource.Root, "NamedReleaseSpec", 1);
        Admit(admissions, namedSource, "NamedReleaseSpec", named);
        Admit(admissions, namedSource, "NamedReleaseSpec<TSelf>", namedGeneric);
        RequireContains(named, "ReplayAncestors(this)", "NamedReleaseSpec construction must replay ancestors.");
        RequireContains(named, "ReplayAncestors(fork.Parent);fork.Apply(this);", "NamedReleaseSpec must apply ancestors from root to child.");
        RequireContains(namedGeneric, "publicstaticNamedReleaseSpecInstance{get;}=newTSelf();",
            "NamedReleaseSpec<TSelf>.Instance must construct the requested fork type.");

        Dictionary<string, (SourceFile Source, ClassDeclarationSyntax Type)> forks = new(StringComparer.Ordinal);
        foreach (string path in ForkPaths)
        {
            SourceFile source = Get(sources, path);
            ClassDeclarationSyntax type = RequireSingle(source.Root.DescendantNodes().OfType<ClassDeclarationSyntax>(),
                static candidate => candidate.BaseList?.Types.Any(baseType => Canonical(baseType.Type).StartsWith("NamedReleaseSpec<", StringComparison.Ordinal)) == true,
                $"{path} must declare exactly one named mainnet fork.");
            forks.Add(type.Identifier.ValueText, (source, type));
            Admit(admissions, source, $"fork {type.Identifier.ValueText}", type);
        }
        string[] requiredFlags = ["IsEip211Enabled", "IsEip1344Enabled", "IsEip1884Enabled", "IsEip3198Enabled", "IsEip4844Enabled", "IsEip7843Enabled"];
        HashSet<string> requiredFlagSet = requiredFlags.ToHashSet(StringComparer.Ordinal);
        List<ClassDeclarationSyntax> ancestry = [];
        HashSet<string> visited = new(StringComparer.Ordinal);
        string current = "Amsterdam";
        while (true)
        {
            if (!visited.Add(current) || !forks.TryGetValue(current, out (SourceFile Source, ClassDeclarationSyntax Type) fork))
                throw new ExtractionException("Amsterdam named-fork ancestry is cyclic or leaves the admitted mainnet chain.");
            ancestry.Add(fork.Type);
            string? parent = ParentFork(fork.Type);
            if (parent is null)
                break;
            current = parent;
        }
        if (visited.Count != ForkPaths.Length)
            throw new ExtractionException($"Amsterdam ancestry must traverse all {ForkPaths.Length} admitted fork classes; traversed {visited.Count}.");
        Dictionary<string, bool> enabled = new(StringComparer.Ordinal);
        foreach (ClassDeclarationSyntax fork in ancestry.AsEnumerable().Reverse())
        {
            foreach (AssignmentExpressionSyntax assignment in ExecutableDescendants(fork).OfType<AssignmentExpressionSyntax>())
            {
                string left = Canonical(assignment.Left);
                const string prefix = "spec.";
                if (!left.StartsWith(prefix, StringComparison.Ordinal) || !requiredFlagSet.Contains(left[prefix.Length..]))
                    continue;
                enabled[left[prefix.Length..]] = assignment.Right.Kind() switch
                {
                    SyntaxKind.TrueLiteralExpression => true,
                    SyntaxKind.FalseLiteralExpression => false,
                    _ => throw new ExtractionException($"Fork {fork.Identifier.ValueText} assigns {left} from an unmodeled expression."),
                };
            }
        }
        foreach (string flag in requiredFlags)
        {
            if (!enabled.TryGetValue(flag, out bool isEnabled) || !isEnabled)
                throw new ExtractionException($"Amsterdam ancestry no longer enables {flag}.");
        }

        SourceFile extensionsSource = Get(sources, ReleaseExtensionsPath);
        ClassDeclarationSyntax extensions = RequireType<ClassDeclarationSyntax>(extensionsSource.Root, "IReleaseSpecExtensions", 0);
        Admit(admissions, extensionsSource, "IReleaseSpecExtensions", extensions);
        foreach ((string property, string flag) in new[]
        {
            ("ReturnDataOpcodesEnabled", "IsEip211Enabled"),
            ("ChainIdOpcodeEnabled", "IsEip1344Enabled"),
            ("SelfBalanceOpcodeEnabled", "IsEip1884Enabled"),
            ("BaseFeeEnabled", "IsEip3198Enabled"),
            ("BlobBaseFeeEnabled", "IsEip4844Enabled"),
        })
        {
            PropertyDeclarationSyntax node = RequireSingle(extensions.DescendantNodes().OfType<PropertyDeclarationSyntax>(),
                candidate => candidate.Identifier.ValueText == property, $"{property} extension must be declared exactly once.");
            RequireContains(node, $"spec.{flag}", $"{property} must project {flag}.");
        }
    }

    private static void ValidateBuildSourceSelection(SourceFile targets)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(targets.Text.ToString(), LoadOptions.PreserveWhitespace);
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or InvalidOperationException)
        {
            throw new ExtractionException($"Directory.Build.targets is not valid XML: {exception.Message}");
        }

        XElement[] itemGroups = document.Root?.Elements("ItemGroup").ToArray() ?? [];
        ValidateBuildGroup(itemGroups, "'$(EnableZkEvm)' == 'true'", "**/*.std.cs", "zkEVM");
        ValidateBuildGroup(itemGroups, "'$(EnableZkEvm)' != 'true'", "**/*.zkevm.cs", "standard");
    }

    private static void ValidateBuildGroup(
        IReadOnlyList<XElement> itemGroups,
        string condition,
        string excludedSources,
        string flavor)
    {
        XElement[] matching = itemGroups
            .Where(group => (string?)group.Attribute("Condition") == condition)
            .ToArray();
        if (matching.Length != 1 ||
            matching[0].Elements("Compile").Count(element => (string?)element.Attribute("Remove") == excludedSources) != 1 ||
            matching[0].Elements("None").Count(element => (string?)element.Attribute("Include") == excludedSources) != 1)
        {
            throw new ExtractionException(
                $"Directory.Build.targets must contain one exact {flavor} source-selection group for {excludedSources}.");
        }
    }

    private static OpcodeDescriptor[] ValidateOpcodes(Dictionary<string, SourceFile> sources, Dictionary<string, int> instructionValues, List<NodeAdmission> admissions)
    {
        SourceFile handlersSource = Get(sources, HandlersPath);
        ClassDeclarationSyntax vm = RequireType<ClassDeclarationSyntax>(handlersSource.Root, "VirtualMachine", 1);
        MethodDeclarationSyntax generate = RequireMethod(vm, "GenerateOpcodeHandlers", 2, 1);
        AssignmentExpressionSyntax[] assignments = ExecutableDescendants(generate).OfType<AssignmentExpressionSyntax>()
            .Where(static assignment => TryInstructionAssignment(assignment, out _)).ToArray();

        SourceFile environmentSource = Get(sources, EnvironmentPath);
        ClassDeclarationSyntax instructions = RequireType<ClassDeclarationSyntax>(environmentSource.Root, "EvmInstructions", 0);
        foreach (string interfaceName in new[] { "IOpBlkAddress", "IOpEnv32Bytes", "IOpEnvAddress", "IOpEnvUInt256", "IOpBlkUInt256", "IOpEnvUInt32", "IOpBlkUInt64" })
            Admit(admissions, environmentSource, interfaceName, RequireNestedType<InterfaceDeclarationSyntax>(instructions, interfaceName, 1));
        foreach ((string method, int genericArity, int parameters) in new[]
        {
            ("InstructionEnvAddress", 3, 3), ("InstructionBlkAddress", 3, 3),
            ("InstructionEnvUInt256", 3, 3), ("InstructionBlkUInt256", 3, 3),
            ("InstructionEnvUInt32", 3, 3), ("InstructionBlkUInt64", 3, 3),
            ("InstructionEnv32Bytes", 3, 3), ("InstructionCodeSize", 2, 2),
            ("InstructionReturnDataSize", 2, 3), ("InstructionBlobBaseFee", 2, 3),
            ("InstructionSelfBalance", 3, 3), ("InstructionPrevRandao", 2, 3),
            ("InstructionBlobHash", 2, 3), ("InstructionBlockHash", 2, 3),
            ("InstructionSlotNum", 2, 3), ("PushBalance", 2, 2),
        })
            Admit(admissions, environmentSource, $"EvmInstructions.{method}/{parameters}", RequireMethod(instructions, method, genericArity, parameters));

        HashSet<string> providerTypes = Seeds.Where(static seed => seed.ProviderType is not null)
            .Select(static seed => seed.ProviderType!).ToHashSet(StringComparer.Ordinal);
        foreach (string providerType in providerTypes)
        {
            StructDeclarationSyntax provider = RequireNestedType<StructDeclarationSyntax>(instructions, providerType, 1);
            Seed seed = Seeds.Single(item => item.ProviderType == providerType);
            RequireContains(provider, seed.SourceFragment, $"{providerType} value provider target changed.");
            Admit(admissions, environmentSource, providerType, provider);
        }

        HashSet<string> wrapperTypes = Seeds.Select(static seed => GenericRoot(seed.HandlerRoute)).ToHashSet(StringComparer.Ordinal);
        foreach (string wrapperType in wrapperTypes)
        {
            TypeDeclarationSyntax wrapper = RequireSingle(vm.Members.OfType<TypeDeclarationSyntax>(),
                candidate => candidate.Identifier.ValueText == wrapperType, $"{wrapperType} wrapper must be declared exactly once.");
            Admit(admissions, handlersSource, wrapperType, wrapper);
        }

        List<OpcodeDescriptor> result = new(Seeds.Length);
        foreach (Seed seed in Seeds)
        {
            AssignmentExpressionSyntax[] selected = assignments.Where(assignment =>
            {
                TryInstructionAssignment(assignment, out string? instruction);
                return instruction == seed.Instruction;
            }).ToArray();
            if (selected.Length != 1)
                throw new ExtractionException($"Instruction.{seed.Instruction} must have exactly one executable dispatch assignment; found {selected.Length}.");
            AssignmentExpressionSyntax assignment = selected[0];
            string right = Canonical(assignment.Right);
            string expectedRight = $"OpcodeHandler<{seed.HandlerRoute},TTracingInst,TCancelable>()";
            if (right != expectedRight)
                throw new ExtractionException($"Instruction.{seed.Instruction} dispatch target or generic arguments changed: '{right}'.");
            string actualGate = DispatchGate(assignment, generate);
            string expectedGate = seed.ActivationGate.StartsWith("spec.", StringComparison.Ordinal) ? seed.ActivationGate : "unconditional";
            if (actualGate != expectedGate)
                throw new ExtractionException($"Instruction.{seed.Instruction} activation gate changed from '{expectedGate}' to '{actualGate}'.");

            if (seed.ProviderType is null)
            {
                string specialMethod = seed.Name switch
                {
                    "codesize" => "InstructionCodeSize",
                    "returndatasize" => "InstructionReturnDataSize",
                    "blockhash" => "InstructionBlockHash",
                    "prevrandao" => "InstructionPrevRandao",
                    "selfbalance" => "InstructionSelfBalance",
                    "blobhash" => "InstructionBlobHash",
                    "blobbasefee" => "InstructionBlobBaseFee",
                    "slotnum" => "InstructionSlotNum",
                    _ => throw new ExtractionException($"No special source binding exists for {seed.Name}."),
                };
                MethodDeclarationSyntax method = instructions.Members.OfType<MethodDeclarationSyntax>().Single(candidate => candidate.Identifier.ValueText == specialMethod);
                RequireContains(method, seed.SourceFragment, $"{seed.Instruction} value selector changed.");
                ValidateSpecialOrdering(seed, method);
            }
            result.Add(new OpcodeDescriptor(seed.Name, seed.Instruction, instructionValues[seed.Instruction], seed.HandlerRoute,
                seed.ActivationGate, seed.ValueSelector, seed.FixedGas, seed.DispatchStackInputs, seed.DispatchStackGrowth,
                seed.SemanticPops, seed.SemanticPushes, seed.ChecksAvailabilityBeforeGas));
        }
        if (result.Select(static opcode => opcode.OpcodeByte).Distinct().Count() != Seeds.Length)
            throw new ExtractionException("Environment opcode bytes must remain unique.");
        return result.ToArray();
    }

    private static void ValidateSpecialOrdering(Seed seed, MethodDeclarationSyntax method)
    {
        string body = Canonical(method);
        string gas = seed.FixedGas switch
        {
            2 => "UpdateGas<BaseGasCost>",
            3 => "UpdateGas<BlobHashGasCost>",
            5 => "UpdateGas<SelfBalanceGasCost>",
            20 => "UpdateGas<BlockHashGasCost>",
            _ => throw new UnreachableException(),
        };
        int gasIndex = body.IndexOf(gas, StringComparison.Ordinal);
        if (gasIndex < 0)
            throw new ExtractionException($"{seed.Instruction} no longer charges the admitted fixed gas tag.");
        if (seed.ChecksAvailabilityBeforeGas)
        {
            int availability = seed.Name == "blobbasefee"
                ? body.IndexOf("ExcessBlobGas.HasValue", StringComparison.Ordinal)
                : body.IndexOf("slotNumber.HasValue", StringComparison.Ordinal);
            if (availability < 0 || availability > gasIndex)
                throw new ExtractionException($"{seed.Instruction} must check context availability before charging gas.");
        }
        else if (seed.SemanticPops == 1)
        {
            int pop = body.IndexOf("PopUInt256", StringComparison.Ordinal);
            if (pop < 0 || gasIndex > pop)
                throw new ExtractionException($"{seed.Instruction} must charge gas before popping its argument.");
        }
    }

    private static void ValidateFingerprints(List<NodeAdmission> admissions, Dictionary<string, SourceFile> sources)
    {
        Dictionary<string, NodeAdmission> unique = new(StringComparer.Ordinal);
        foreach (NodeAdmission admission in admissions)
        {
            if (!unique.TryAdd(admission.Identity, admission))
                throw new ExtractionException($"Admission identity '{admission.Identity}' was selected more than once.");
        }
        List<string> mismatches = [];
        foreach (NodeAdmission admission in unique.Values.OrderBy(static item => item.Identity, StringComparer.Ordinal))
        {
            string actual = TokenHash(admission.Node);
            if (!ExpectedNodeFingerprints.TryGetValue(admission.Identity, out string? expected) || expected != actual)
                mismatches.Add($"[\"{admission.Identity}\"] = \"{actual}\",");
        }
        foreach (string expected in ExpectedNodeFingerprints.Keys)
        {
            if (!unique.ContainsKey(expected))
                mismatches.Add($"unexpected admitted fingerprint identity: {expected}");
        }
        ValidateAdmissionKeys(admissions.Select(AdmissionKey));
        SourceFile buildTargets = Get(sources, BuildTargetsPath);
        if (!ExpectedRawFingerprints.TryGetValue(BuildTargetsPath, out string? expectedRaw) || expectedRaw != buildTargets.Hash)
            mismatches.Add($"[\"{BuildTargetsPath}\"] = \"{buildTargets.Hash}\",");
        foreach (string expected in ExpectedRawFingerprints.Keys)
        {
            if (expected != BuildTargetsPath)
                mismatches.Add($"unexpected raw fingerprint identity: {expected}");
        }
        if (mismatches.Count != 0)
            throw new ExtractionException("Complete selected-node token fingerprints changed:\n" + string.Join("\n", mismatches));
    }

    internal static void ValidateAdmissionKeys(IEnumerable<string> identities)
    {
        HashSet<string> unique = new(StringComparer.Ordinal);
        foreach (string identity in identities)
            if (string.IsNullOrWhiteSpace(identity) || !unique.Add(identity))
                throw new ExtractionException($"Duplicate or empty owner-qualified admission identity '{identity}'.");
    }

    private static string AdmissionKey(NodeAdmission admission)
    {
        string owner = string.Join(".", admission.Node.Ancestors().OfType<TypeDeclarationSyntax>()
            .Reverse().Select(static type => $"{type.Identifier.ValueText}/{type.TypeParameterList?.Parameters.Count ?? 0}"));
        if (owner.Length == 0)
            owner = "compilation-unit/0";
        string selector = admission.Node switch
        {
            TypeDeclarationSyntax type => $"type:{type.Identifier.ValueText}/{type.TypeParameterList?.Parameters.Count ?? 0}",
            EnumDeclarationSyntax @enum => $"enum:{@enum.Identifier.ValueText}/0",
            MethodDeclarationSyntax method => $"method:{method.Identifier.ValueText}/{method.TypeParameterList?.Parameters.Count ?? 0}/" +
                string.Join(",", method.ParameterList.Parameters.Select(static parameter =>
                    parameter.Type is null ? "?" : Canonical(parameter.Type))),
            FieldDeclarationSyntax field => "field:" + string.Join(",",
                field.Declaration.Variables.Select(static variable => variable.Identifier.ValueText)),
            _ => throw new ExtractionException($"Unsupported admitted node kind {admission.Node.Kind()} for identity construction."),
        };
        return $"{admission.Source.RelativePath}:{admission.Identity}:{owner}:{selector}";
    }

    private static void RejectDiagnosticsAndDirectives(SourceFile source)
    {
        if (!source.IsCSharp)
            return;
        Diagnostic? error = source.Tree.GetDiagnostics().FirstOrDefault(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (error is not null)
            throw new ExtractionException($"{source.RelativePath} contains a parse error: {error.GetMessage(CultureInfo.InvariantCulture)}");
    }

    private static string? ParentFork(ClassDeclarationSyntax type)
    {
        if (type.BaseList?.Types is not [BaseTypeSyntax baseType] || baseType.Type is not GenericNameSyntax generic ||
            generic.Identifier.ValueText != "NamedReleaseSpec")
            throw new ExtractionException($"Fork {type.Identifier.ValueText} must directly specialize NamedReleaseSpec.");
        if (type.ParameterList?.Parameters.Count != 0)
            throw new ExtractionException($"Fork {type.Identifier.ValueText} must retain its parameterless primary constructor.");
        ArgumentListSyntax? arguments = baseType is PrimaryConstructorBaseTypeSyntax primary ? primary.ArgumentList : null;
        if (arguments?.Arguments.Count != 1)
            throw new ExtractionException($"Fork {type.Identifier.ValueText} must name exactly one parent argument.");
        string parent = Canonical(arguments.Arguments[0].Expression);
        if (parent == "null")
            return null;
        const string suffix = ".Instance";
        if (!parent.EndsWith(suffix, StringComparison.Ordinal))
            throw new ExtractionException($"Fork {type.Identifier.ValueText} parent must use the named-fork singleton.");
        return parent[..^suffix.Length];
    }

    private static string DispatchGate(AssignmentExpressionSyntax assignment, MethodDeclarationSyntax root)
    {
        SyntaxNode? current = assignment.Parent;
        string? gate = null;
        while (current is not null && current != root)
        {
            if (current is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or ForStatementSyntax or ForEachStatementSyntax or
                WhileStatementSyntax or DoStatementSyntax or SwitchStatementSyntax)
                throw new ExtractionException("Environment dispatch assignments must not be nested in closures, local functions, loops, or switches.");
            if (current is IfStatementSyntax conditional)
            {
                if (gate is not null || conditional.Else is not null && conditional.Else.Span.Contains(assignment.Span))
                    throw new ExtractionException("Environment dispatch assignments must have at most one positive activation gate.");
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
            element.ArgumentList.Arguments is not [ArgumentSyntax argument])
            return false;
        string index = Canonical(argument.Expression);
        const string prefix = "(int)Instruction.";
        if (!index.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        instruction = index[prefix.Length..];
        return true;
    }

    private static IEnumerable<SyntaxNode> ExecutableDescendants(SyntaxNode node) => node.DescendantNodes(static candidate =>
        candidate is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax);

    private static void Admit(List<NodeAdmission> admissions, SourceFile source, string identity, SyntaxNode node)
    {
        if (node.DescendantTrivia(descendIntoTrivia: true).Any(static trivia =>
                trivia.IsDirective || trivia.IsKind(SyntaxKind.DisabledTextTrivia) || trivia.IsKind(SyntaxKind.SkippedTokensTrivia)))
            throw new ExtractionException($"{source.RelativePath} selected node '{identity}' contains preprocessor, disabled, or skipped source and is not admitted.");
        admissions.Add(new(identity, source, node));
    }

    private static T RequireType<T>(CompilationUnitSyntax root, string name, int genericArity) where T : TypeDeclarationSyntax =>
        RequireSingle(root.DescendantNodes().OfType<T>(), node => node.Identifier.ValueText == name && (node.TypeParameterList?.Parameters.Count ?? 0) == genericArity,
            $"Type {name}`{genericArity} must be declared exactly once.");

    private static T RequireNestedType<T>(TypeDeclarationSyntax parent, string name, int genericArity) where T : TypeDeclarationSyntax =>
        RequireSingle(parent.Members.OfType<T>(), node => node.Identifier.ValueText == name && (node.TypeParameterList?.Parameters.Count ?? 0) == genericArity,
            $"Nested type {parent.Identifier.ValueText}.{name}`{genericArity} must be declared exactly once.");

    private static MethodDeclarationSyntax RequireMethod(TypeDeclarationSyntax type, string name, int genericArity, int parameters) =>
        RequireSingle(type.Members.OfType<MethodDeclarationSyntax>(), node => node.Identifier.ValueText == name &&
            (node.TypeParameterList?.Parameters.Count ?? 0) == genericArity && node.ParameterList.Parameters.Count == parameters,
            $"Method {type.Identifier.ValueText}.{name}`{genericArity}/{parameters} must be declared exactly once.");

    private static T RequireSingle<T>(IEnumerable<T> source, Func<T, bool> predicate, string message)
    {
        T[] result = source.Where(predicate).ToArray();
        return result.Length == 1 ? result[0] : throw new ExtractionException(message);
    }

    private static void RequireContains(SyntaxNode node, string expected, string message)
    {
        if (!Canonical(node).Contains(expected, StringComparison.Ordinal))
            throw new ExtractionException(message);
    }

    private static string GenericRoot(string route)
    {
        int generic = route.IndexOf('<');
        return generic < 0 ? route : route[..generic];
    }

    private static string Canonical(SyntaxNode node) => string.Concat(node.DescendantTokens(descendIntoTrivia: false).Select(static token => token.Text));

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

    private static SourceFile Get(Dictionary<string, SourceFile> sources, string path) =>
        sources.TryGetValue(path, out SourceFile? source) ? source : throw new ExtractionException($"Required source '{path}' was not loaded.");

    private static byte[] Serialize<T>(T value) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");

    private static void Write(string path, byte[] bytes) => File.WriteAllBytes(path, bytes);

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void EnsureWithin(string root, string path)
    {
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ExtractionException($"Path escapes the admitted root: {path}");
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private sealed record SourceFile(string RelativePath, string Hash, SourceText Text, SyntaxTree Tree, CompilationUnitSyntax Root, bool IsCSharp);
    private sealed record NodeAdmission(string Identity, SourceFile Source, SyntaxNode Node);
    private sealed record DispatchTableBinding(string Name, string TracingFlag, string CancelableFlag);
    private sealed record Seed(string Name, string Instruction, string HandlerRoute, string ActivationGate, string ValueSelector,
        ulong FixedGas, int DispatchStackInputs, int DispatchStackGrowth, int SemanticPops, int SemanticPushes,
        bool ChecksAvailabilityBeforeGas, string? ProviderType, string SourceFragment);
}
