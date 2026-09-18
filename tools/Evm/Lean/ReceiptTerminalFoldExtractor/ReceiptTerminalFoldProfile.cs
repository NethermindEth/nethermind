// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Evm.Lean.ReceiptTerminalFoldExtractor;

internal static class ReceiptTerminalFoldProfile
{
    internal const string TracerPath = "src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptsTracer.cs";
    internal const string AccountingKernelPath = "src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptGasAccountingKernel.cs";
    internal const string GasConsumedPath = "src/Nethermind/Nethermind.Evm/TransactionProcessing/GasConsumed.cs";
    internal const string ReceiptPath = "src/Nethermind/Nethermind.Core/TransactionReceipt.cs";
    internal const string TransactionPath = "src/Nethermind/Nethermind.Core/Transaction.cs";
    internal const string TransactionExtensionsPath = "src/Nethermind/Nethermind.Core/TransactionExtensions.cs";
    internal const string BlockPath = "src/Nethermind/Nethermind.Core/Block.cs";
    internal const string HeaderPath = "src/Nethermind/Nethermind.Core/BlockHeader.cs";
    internal const string CallerPath = "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs";
    internal const string StatusCodePath = "src/Nethermind/Nethermind.Evm/StatusCode.cs";
    internal const string EthereumGasPolicyPath = "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs";
    internal const string TransactionGasInitializationKernelPath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/TransactionGasInitializationKernel.cs";
    internal const string TxTracerPath = "src/Nethermind/Nethermind.Evm/Tracing/ITxTracer.cs";
    internal const string BlockTracerPath = "src/Nethermind/Nethermind.Evm/Tracing/IBlockTracer.cs";
    internal const string TxTracerWrapperPath = "src/Nethermind/Nethermind.Evm/Tracing/ITxTracerWrapper.cs";
    internal const string WorldStateTracerPath = "src/Nethermind/Nethermind.Evm/Tracing/State/IWorldStateTracer.cs";
    internal const string JournalPath = "src/Nethermind/Nethermind.Core/IJournal.cs";
    internal const string TransactionSubstatePath = "src/Nethermind/Nethermind.Evm/TransactionSubstate.cs";
    internal const string EvmExceptionPath = "src/Nethermind/Nethermind.Evm/EvmException.cs";
    internal const string ReadOnlyMemoryExtensionsPath = "src/Nethermind/Nethermind.Evm/ReadOnlyMemoryExtensions.cs";
    internal const string ExecutionOptionsPath = "src/Nethermind/Nethermind.Evm/TransactionProcessing/ExecutionOptions.cs";
    internal const string WorldStatePath = "src/Nethermind/Nethermind.Evm/State/IWorldState.cs";
    internal const string ReadOnlyStateProviderPath = "src/Nethermind/Nethermind.Evm/State/IReadOnlyStateProvider.cs";
    internal const string ReleaseSpecPath = "src/Nethermind/Nethermind.Core/Specs/IReleaseSpec.cs";
    internal const string ReceiptSpecPath = "src/Nethermind/Nethermind.Core/Specs/IReceiptSpec.cs";
    internal const string Eip1559SpecPath = "src/Nethermind/Nethermind.Core/Specs/IEip1559Spec.cs";
    internal const string NullStateTracerPath = "src/Nethermind/Nethermind.Evm/Tracing/State/NullStateTracer.cs";
    internal const string StateTracerPath = "src/Nethermind/Nethermind.Evm/Tracing/State/IStateTracer.cs";
    internal const string StorageTracerPath = "src/Nethermind/Nethermind.Evm/Tracing/State/IStorageTracer.cs";
    internal const string JournalCollectionPath = "src/Nethermind/Nethermind.Core/Collections/JournalCollection.cs";

    internal const string IrFileName = "ReceiptTerminalFoldKernel.ir.json";
    internal const string ManifestFileName = "ReceiptTerminalFoldKernel.source-manifest.json";
    internal const string DefaultOutputRelativePath =
        "tools/Evm/Lean/ReceiptTerminalFoldExtractor/Generated";
    internal const string DefaultLeanRelativePath =
        "tools/Evm/Lean/ReceiptTerminalFoldExtractor/Generated/ReceiptTerminalFoldKernel.lean";
    internal const string CompilerReferenceInventoryRelativePath =
        "tools/Evm/Lean/ReceiptTerminalFoldExtractor/COMPILER_REFERENCE_PINS.json";
    private const string SourcePinsRelativePath =
        "tools/Evm/Lean/ReceiptTerminalFoldExtractor/SOURCE_PINS.json";
    internal const int SchemaVersion = 9;
    internal const string ExtractorVersion = "1.9.2";
    internal const string Kernel = "standard-mainnet sequential BlockReceiptsTracer terminal receipt fold";

    private const int CompilerReferenceInventorySchemaVersion = 2;
    private const int CompilerReferenceInventoryCount = 434;
    private const string CompilerReferenceInventoryAggregateSha256 =
        "6fdfa102a4190083ae62080355aa6691b5f46acc32d4f9da2212012686d9c2e2";
    private const string CompilerReferenceInventorySha256 =
        "1f73a3800a957dd02d9d7b06b3b955c81bb92fd9eb8d2831920905394bfc1d5c";
    private const int SourcePinsSchemaVersion = 3;

    private static readonly SourceIdentity[] LeanDependencyPins =
    [
        new("tools/Evm/Lean/Eip803x/BlockReceiptGas.lean", "51f1df6a272adf776aac2cea085e6d063e25997310eb77c29e99fe5970b62a6e"),
        new(AccountingKernelGeneratedPath, AccountingKernelGeneratedSha256),
        new("tools/Evm/Lean/Eip803x/Generated/TransactionGasInitializationKernel.lean", "83ce996d6dcc93a588114e73737a25a14cb21689e9e8900e6dc68a07fb170b6f"),
        new(AccountingKernelRefinementPath, AccountingKernelRefinementSha256),
        new("tools/Evm/Lean/Eip803x/Refinement/TransactionGasInitialization.lean", "8986504ad786cc621dcb0c74400980366847c43d832830973f52a5d38c290578"),
        new("tools/Evm/Lean/Eip803x/TransactionGas.lean", "2e87721125c6430eab9c76dc8f32a11f4898db0dec68b57a48043c18cbdeefcd"),
    ];

    private const string AccountingKernelGeneratedPath =
        "tools/Evm/Lean/Eip803x/Generated/BlockReceiptGasAccountingKernel.lean";
    private const string AccountingKernelRefinementPath =
        "tools/Evm/Lean/Eip803x/Refinement/BlockReceiptGasAccounting.lean";
    private const string AccountingKernelIrPath =
        "tools/Evm/Lean/Extractor/Generated/BlockReceiptGasAccountingKernel.ir.json";
    private const string AccountingKernelManifestPath =
        "tools/Evm/Lean/Extractor/Generated/BlockReceiptGasAccountingKernel.source-manifest.json";

    private const string AccountingKernelSourceSha256 =
        "0882c798e6ffb2243735a9ec0cec5d442b20d51277feeba95e5b68ecfaf89e32";
    private const string AccountingKernelGeneratedSha256 =
        "5d7851084945d5263dd87bcda4ae9e60ce7b779473a7c37617a96cf0e91026f2";
    private const string AccountingKernelRefinementSha256 =
        "6d8a1c1de3f99c43f24576a6358e083fcce43cfe23037bd54cfececfbda5dfe5";
    private const string AccountingKernelIrSha256 =
        "5278c354453a3adab99df5a68b4475d9feab798eba1e4df42ee80bf34a259c41";
    private const string AccountingKernelManifestSha256 =
        "1bc07122e230b88e69f5444f526d6c3654f4788c4479ea114185982471bdc2aa";

    private const string TracerSha256 =
        "d4504f54b50dd43e2ab5bc7172ce2cf9e48453fcede990e5e667e743146262ff";
    private const string GasConsumedSha256 =
        "1dc4e78d5a17056dcb00d496136a32a899245f3a7b3e5a9eef74c4760ef68686";
    private const string ReceiptSha256 =
        "55b62b4e0708e590a001916e9a73d07ac70e225b16f711574e811c98e540cc63";
    private const string TransactionSha256 =
        "3fdf94805739c6ccdfa8b4617c5d4cfa6bee9e9565f16d667b22106c434d6596";
    private const string TransactionExtensionsSha256 =
        "d3df04cb465668a3dcc842f33e5bc7a16d2420cdc1dcd03edf1c3275c2b24852";
    private const string BlockSha256 =
        "3cdd12ca52b00b6eefea372be868649653ac57043194aa081d41064d0ed749aa";
    private const string HeaderSha256 =
        "f354ddd2d45afc8774739ac46a10535b915871ce4ad11d996d0ab972cf9e7349";
    private const string CallerSha256 =
        "0374c6f35a9a23361f37b41162ac281ca83b09846ab4295d5a592db6315c99cc";
    private const string StatusCodeSha256 =
        "e896f55406c9fc1ce99199d80bea69b87fa8891c23afef59712b87cadb971190";
    private const string EthereumGasPolicySha256 =
        "3c31ba40c24a78bf11243b38859168350f02934806124e913da0560bc1a4350f";
    private const string TransactionGasInitializationKernelSha256 =
        "65ab0742596ea7ebf49d2c79601eae00152d8d039ac8e417076bf63123be1e82";

    private static readonly UTF8Encoding Utf8WithoutBom = new(false, true);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
    };

    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default
        .WithLanguageVersion(LanguageVersion.CSharp14)
        .WithDocumentationMode(DocumentationMode.Parse)
        .WithKind(SourceCodeKind.Regular);

    private static readonly string[] SourcePaths =
    [
        TracerPath,
        AccountingKernelPath,
        GasConsumedPath,
        ReceiptPath,
        TransactionPath,
        TransactionExtensionsPath,
        BlockPath,
        HeaderPath,
        CallerPath,
        StatusCodePath,
        EthereumGasPolicyPath,
        TransactionGasInitializationKernelPath,
    ];

    // Declaration and compiler support are pinned separately from the transition claim.
    private static readonly string[] BindingSourcePaths =
    [
        TxTracerPath,
        BlockTracerPath,
        TxTracerWrapperPath,
        WorldStateTracerPath,
        JournalPath,
        TransactionSubstatePath,
        EvmExceptionPath,
        ReadOnlyMemoryExtensionsPath,
        ExecutionOptionsPath,
        WorldStatePath,
        ReadOnlyStateProviderPath,
        ReleaseSpecPath,
        ReceiptSpecPath,
        Eip1559SpecPath,
        NullStateTracerPath,
        StateTracerPath,
        StorageTracerPath,
        JournalCollectionPath,
        TransactionStandardSupportPath,
        BlockBodySupportPath,
        SystemProcessorSupportPath,
        RoutingSupportPath,
        ProcessorInterfaceSupportPath,
        SettlementSupportPath,
        MetricsSupportPath,
        MetricsStandardSupportPath,
        DispatchFlagsSupportPath,
        VmInterfaceSupportPath,
        VmStaticsSupportPath,
        SignatureSupportPath,
        GasInterfaceSupportPath,
        AccountPricingSupportPath,
        PrecompilePricingSupportPath,
        StateChargeSupportPath,
        StateTransitionSupportPath,
        StateTransitionAdapterSupportPath,
        BlockGasSupportPath,
        IntrinsicGasSupportPath,
    ];

    private const string TransactionStandardSupportPath =
        "src/Nethermind/Nethermind.Core/Transaction.std.cs";
    private const string BlockBodySupportPath = "src/Nethermind/Nethermind.Core/BlockBody.cs";
    private const string SystemProcessorSupportPath = "src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionProcessor.cs";
    private const string RoutingSupportPath = "src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionRoutingKernel.cs";
    private const string ProcessorInterfaceSupportPath = "src/Nethermind/Nethermind.Evm/TransactionProcessing/ITransactionProcessor.cs";
    private const string SettlementSupportPath = "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionSettlementKernel.cs";
    private const string MetricsSupportPath = "src/Nethermind/Nethermind.Evm/Metrics.cs";
    private const string MetricsStandardSupportPath = "src/Nethermind/Nethermind.Evm/Metrics.std.cs";
    private const string DispatchFlagsSupportPath = "src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs";
    private const string VmInterfaceSupportPath = "src/Nethermind/Nethermind.Evm/IVirtualMachine.cs";
    private const string VmStaticsSupportPath = "src/Nethermind/Nethermind.Evm/VirtualMachine.cs";
    private const string SignatureSupportPath = "src/Nethermind/Nethermind.Core/Crypto/Signature.cs";
    private const string GasInterfaceSupportPath = "src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs";
    private const string AccountPricingSupportPath = "src/Nethermind/Nethermind.Evm/GasPolicy/AccountAccessPricingKernel.cs";
    private const string PrecompilePricingSupportPath = "src/Nethermind/Nethermind.Evm/GasPolicy/PrecompileGasPricingKernel.cs";
    private const string StateChargeSupportPath = "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasChargeKernel.cs";
    private const string StateTransitionSupportPath = "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs";
    private const string StateTransitionAdapterSupportPath = "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionAdapterKernel.cs";
    private const string BlockGasSupportPath = "src/Nethermind/Nethermind.Evm/GasPolicy/Eip8037BlockGasInclusionCheck.cs";
    private const string IntrinsicGasSupportPath = "src/Nethermind/Nethermind.Evm/IntrinsicGasCalculator.cs";

    private static readonly string[] CompilerSupportSourcePaths =
    [
        TransactionStandardSupportPath,
    ];

    private static readonly string[] CallerSupportSourcePaths =
    [
        SystemProcessorSupportPath, ExecutionOptionsPath, RoutingSupportPath, ProcessorInterfaceSupportPath,
        TransactionSubstatePath, SettlementSupportPath, MetricsSupportPath, MetricsStandardSupportPath,
        DispatchFlagsSupportPath, VmInterfaceSupportPath, VmStaticsSupportPath, SignatureSupportPath,
    ];

    private static readonly string[] GasPolicySupportSourcePaths =
    [
        GasInterfaceSupportPath, AccountPricingSupportPath, PrecompilePricingSupportPath,
        StateChargeSupportPath, StateTransitionSupportPath, StateTransitionAdapterSupportPath,
        BlockGasSupportPath, IntrinsicGasSupportPath,
    ];

    private static readonly IReadOnlyDictionary<string, string> ExpectedSourceHashes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TracerPath] = TracerSha256,
            [AccountingKernelPath] = AccountingKernelSourceSha256,
            [GasConsumedPath] = GasConsumedSha256,
            [ReceiptPath] = ReceiptSha256,
            [TransactionPath] = TransactionSha256,
            [TransactionExtensionsPath] = TransactionExtensionsSha256,
            [BlockPath] = BlockSha256,
            [HeaderPath] = HeaderSha256,
            [CallerPath] = CallerSha256,
            [StatusCodePath] = StatusCodeSha256,
            [EthereumGasPolicyPath] = EthereumGasPolicySha256,
            [TransactionGasInitializationKernelPath] = TransactionGasInitializationKernelSha256,
        };

    private static readonly IReadOnlyDictionary<string, string> ExpectedBindingSourceHashes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TxTracerPath] = "9f2550f8c3407f494452386699040feab8aa597437b1cd4180d038d9fd0a9169",
            [BlockTracerPath] = "6e6f14e4b8d1ba044e4ef5523ca2bdeb73284bf6e3863909d35daa5d43a156ac",
            [TxTracerWrapperPath] = "b892bed034c5c14dd50837b69f1fe7dd47bc325421545fb1d8ab859e95ece36d",
            [WorldStateTracerPath] = "d4e95b993245b751fc97f71f3d47bc93bc477d79662ed17755e426d49e76335f",
            [JournalPath] = "065a1fc97bcae6cb3237284af300a907c99bde40960fe84ae01ffb0c86839f91",
            [TransactionSubstatePath] = "1312a5f1a9990976670914fcc0a6ad2c77dd92d768d92fd91b28f27fefe66a00",
            [EvmExceptionPath] = "8c503bfbe0c60c41597e64c96b5d823dc98b934b4511730217eceaaf13837450",
            [ReadOnlyMemoryExtensionsPath] = "d5dec0389767197fffde80a3aa86e139b38722a5d9343519ef1235e1fdac1bf9",
            [ExecutionOptionsPath] = "014a2598e1def284b7b98dc97185b6dbbc45ca04be4ab159e330a0675ac24f10",
            [WorldStatePath] = "0680ebc7168472645d93df58d2b3240508c177361815c85cceb09cf960f189c7",
            [ReadOnlyStateProviderPath] = "2bab0038c33735b330e25f6300384450835c092a5ff137935a1fd6c5ab58be68",
            [ReleaseSpecPath] = "3f44b3da5614c94d59eae2e4a6266a9e698614f75a7b5a9894a4828b0021b18a",
            [ReceiptSpecPath] = "1b1c18dcf5e8c57fd9d19468d59412a822ea3313a800fedcd46798b4af9c4d87",
            [Eip1559SpecPath] = "534bdd465854855f6479e3b1d993b34e5e04156be78e733b2148a7ad18f31f76",
            [NullStateTracerPath] = "dde2127d6fd6bc4f8b9a2009517be1e3c1eeae8ed63351c14551ccfb01590eab",
            [StateTracerPath] = "4b75958ac4d921126364d9ddbbf3c1fce9490ec046a3bb43f2b5a673c53dddc2",
            [StorageTracerPath] = "d0ba6ca8c1a2c99fa6b3c5ff6d8ff6d6db84340d090a5d50816ba7f1b2077be0",
            [JournalCollectionPath] = "5c1395b025da5a03749a5b64da804e53c6ff0a7aec4c02d30fd4b1c0e059c720",
            [TransactionStandardSupportPath] = "b08ebdcf7fb167cf028488d393037278e9dc33b8f2c927f96440ea28572530d9",
            [BlockBodySupportPath] = "24da28dc4ef55da25c5ac7d14fab929e65b6bf072eb0abcec9d4ae2d33374f73",
            [SystemProcessorSupportPath] = "10c7370e5f6d5ca25e4cd0801967083f97f432e211314dc40126a18fbea48a95",
            [RoutingSupportPath] = "b884bb49ad7c109b907f4dfcac56a5eb13283d964310d45eb9ca85177ff5e5c1",
            [ProcessorInterfaceSupportPath] = "3cae1430ba38d54f9867b714efdc823f9db126b1bbf0e45eae59439701459e95",
            [SettlementSupportPath] = "37add297aaaf07a4260b45c05c5f866a48f92eef2ac6fb16ef2bfd96103f8c44",
            [MetricsSupportPath] = "f056b9138021c4875f5a16893b243aac12f535c71ae88281987094fb6bcc2b90",
            [MetricsStandardSupportPath] = "e36b3f0f33db29043d820db99cf0577a3c7010bf1b93193ebeb92e954a54300e",
            [DispatchFlagsSupportPath] = "99c58367e5b90fa5904484f6e2fbf10745a54f6cd7d07275810c27028ad0eb2b",
            [VmInterfaceSupportPath] = "330da5f31bd7aca4f4212a42c912c33cf0ac5b3def0689b7f9985cd6510aa56d",
            [VmStaticsSupportPath] = "45edba3691e09185e749485785990662ddea1af849bc6e4564f92b68a5137a6b",
            [SignatureSupportPath] = "8f59fdf1410f37f1cd589283dc800941ea37049fdbe39fe8469d267f091fdb82",
            [GasInterfaceSupportPath] = "b4d26db87723243514f2f6b8a5e48d1176139105c3b012ad0f3956e59afc98a8",
            [AccountPricingSupportPath] = "1988faefb55bb65bf4e915e77e99966f43b88675dec47689e04378bde4f31c92",
            [PrecompilePricingSupportPath] = "dd23b8bd461a13c75ec203b1dbe853a885f61640edf4d47287c1dadd0d9ff576",
            [StateChargeSupportPath] = "ec3276958cbc6b0255fadedf15dbe49691195aa95570b332937f8d1703b103fd",
            [StateTransitionSupportPath] = "4a31dc76fb8284a8ea5994de9a63810abe0fd7efbe6add4aa8b3d6cb564a75cc",
            [StateTransitionAdapterSupportPath] = "ca16429e4cb1b728619b7fa0113b2302ca75600b0a32d9345e38721c7c2d7368",
            [BlockGasSupportPath] = "79e4c84096c72bd0241553f114f08973279eb841cfa469f944566fb9490afe4e",
            [IntrinsicGasSupportPath] = "886772dce848f566ba97a112a9b12b9f535a2d951dace63647136ababd822909",
        };

    private static readonly MemberExpectation[] MemberExpectations =
    [
        Method("terminal.success", TracerPath, "BlockReceiptsTracer", "MarkAsSuccess", "void MarkAsSuccess(Address,in GasConsumed,byte[],LogEntry[],Hash256?)", 5,
            ["_txReceipts.Add(BuildReceipt", "otherTxTracer.MarkAsSuccess", "_currentTxTracer.IsTracingReceipt", "_currentTxTracer.MarkAsSuccess"], ["public"]),
        Method("terminal.failure", TracerPath, "BlockReceiptsTracer", "MarkAsFailed", "void MarkAsFailed(Address,in GasConsumed,byte[],string?,Hash256?)", 5,
            ["_txReceipts.Add(BuildFailedReceipt", "otherTxTracer.MarkAsFailed", "_currentTxTracer.IsTracingReceipt", "_currentTxTracer.MarkAsFailed"], ["public"]),
        Method("receipt.buildFailed", TracerPath, "BlockReceiptsTracer", "BuildFailedReceipt", "TxReceipt BuildFailedReceipt(Address,in GasConsumed,string,Hash256?)", 4,
            ["BuildReceipt", "receipt.Error = error"], ["protected"]),
        Method("receipt.build", TracerPath, "BlockReceiptsTracer", "BuildReceipt", "TxReceipt BuildReceipt(Address,in GasConsumed,byte,LogEntry[],Hash256?)", 5,
            ["UpdateCumulativeGasTracking", "Transaction transaction = CurrentTx!", "TxReceipt txReceipt = new()", "Logs = logEntries", "TxType = transaction.Type", "GasUsedTotal = cumulativeReceiptGas", "StatusCode = statusCode", "Recipient = transaction.IsContractCreation ? null : recipient", "BlockHash = Block.Hash", "BlockNumber = Block.Number", "Index = _currentIndex", "GasUsed = gasConsumed.SpentGas", "EffectiveGasPrice = effectiveGasPrice", "Sender = transaction.SenderAddress", "ContractAddress = transaction.IsContractCreation ? recipient : null", "TxHash = transaction.Hash", "PostTransactionState = stateRoot", "if (gasConsumed.BlockGas > 0)", "BlockGasUsed = gasConsumed.EffectiveBlockGas", "ExecutionGasUsed = gasConsumed.OperationGas", "if (gasConsumed.BlockStateGas > 0)", "StorageGasUsed = gasConsumed.BlockStateGas"], ["protected", "virtual"]),
        Method("gas.update", TracerPath, "BlockReceiptsTracer", "UpdateCumulativeGasTracking", "ulong UpdateCumulativeGasTracking(in GasConsumed)", 1,
            ["BlockReceiptGasAccountingKernel.Accumulate", "_cumulativeBlockGasPerTx.Add", "if (!parallel)", "Block.Header.GasUsed", "_cumulativeReceiptGas = accounting.CumulativeReceiptGas", "return _cumulativeReceiptGas"], ["protected"]),
        Method("snapshot.take", TracerPath, "BlockReceiptsTracer", "TakeSnapshot", "int TakeSnapshot()", 0, ["_txReceipts.Count"], ["public"]),
        Method("snapshot.restore", TracerPath, "BlockReceiptsTracer", "Restore", "void Restore(int)", 1,
            ["_txReceipts.RemoveRange(snapshot", "_cumulativeBlockGasPerTx.RemoveRange(snapshot", "BlockReceiptGasAccountingKernel.FromTotals", "Block.Header.GasUsed", "_cumulativeReceiptGas = accounting.CumulativeReceiptGas"], ["public"]),
        Method("caller.finalize", CallerPath, "TransactionProcessorBase", "FinalizeTransaction", "TransactionResult FinalizeTransaction(Transaction,IReleaseSpec,ITxTracer,ExecutionOptions,bool,bool,bool,in UInt256,Address,in TransactionSubstate,GasConsumed,int)", 12,
            ["if (tracer.IsTracingReceipt)", "if (!spec.IsEip658Enabled)", "if (statusCode == StatusCode.Failure)", "substate.ShouldRevert", "substate.Error", "substate.EvmExceptionType", "tracer.MarkAsFailed", "else", "substate.Logs.Count", "tracer.MarkAsSuccess", "return substate.EvmExceptionType", "TransactionResult.EvmException", "substate.SubstateError", "TransactionResult.Ok"], ["private"]),
        Property("tracer.isTracingReceipt", TracerPath, "BlockReceiptsTracer", "IsTracingReceipt", "bool IsTracingReceipt"),
        Field("status.failure", StatusCodePath, "StatusCode", "Failure", "byte Failure", ["public", "const"]),
        Field("status.success", StatusCodePath, "StatusCode", "Success", "byte Success", ["public", "const"]),
        Method("gas.combine.adapter", EthereumGasPolicyPath, "EthereumGasPolicy", "CombineBlockGas", "ulong CombineBlockGas(ulong,ulong)", 2,
            ["BlockGasAccountingKernel.Combine"], ["public", "static"]),
        Method("gas.combine.kernel", TransactionGasInitializationKernelPath, "BlockGasAccountingKernel", "Combine", "ulong Combine(ulong,ulong)", 2,
            ["Math.Max"], ["public", "static"]),

        PrimaryProperty("gas.spent", GasConsumedPath, "GasConsumed", "SpentGas", "ulong SpentGas"),
        PrimaryProperty("gas.operation", GasConsumedPath, "GasConsumed", "OperationGas", "ulong OperationGas"),
        PrimaryProperty("gas.block", GasConsumedPath, "GasConsumed", "BlockGas", "ulong BlockGas"),
        PrimaryProperty("gas.state", GasConsumedPath, "GasConsumed", "BlockStateGas", "ulong BlockStateGas"),
        PrimaryProperty("gas.max", GasConsumedPath, "GasConsumed", "MaxUsedGas", "ulong MaxUsedGas"),
        PrimaryProperty("gas.refund", GasConsumedPath, "GasConsumed", "GasRefund", "ulong GasRefund"),
        Property("gas.effectiveBlock", GasConsumedPath, "GasConsumed", "EffectiveBlockGas", "ulong EffectiveBlockGas"),
        Property("gas.effectiveMax", GasConsumedPath, "GasConsumed", "EffectiveMaxUsedGas", "ulong EffectiveMaxUsedGas"),

        Property("receipt.txType", ReceiptPath, "TxReceipt", "TxType", "TxType TxType"),
        Property("receipt.status", ReceiptPath, "TxReceipt", "StatusCode", "byte StatusCode"),
        Property("receipt.blockNumber", ReceiptPath, "TxReceipt", "BlockNumber", "ulong BlockNumber"),
        Property("receipt.blockHash", ReceiptPath, "TxReceipt", "BlockHash", "Hash256? BlockHash"),
        Property("receipt.txHash", ReceiptPath, "TxReceipt", "TxHash", "Hash256? TxHash"),
        Property("receipt.index", ReceiptPath, "TxReceipt", "Index", "int Index"),
        Property("receipt.gasUsed", ReceiptPath, "TxReceipt", "GasUsed", "ulong GasUsed"),
        Property("receipt.gasUsedTotal", ReceiptPath, "TxReceipt", "GasUsedTotal", "ulong GasUsedTotal"),
        Property("receipt.blockGasUsed", ReceiptPath, "TxReceipt", "BlockGasUsed", "ulong BlockGasUsed"),
        Property("receipt.storageGasUsed", ReceiptPath, "TxReceipt", "StorageGasUsed", "ulong StorageGasUsed"),
        Property("receipt.executionGasUsed", ReceiptPath, "TxReceipt", "ExecutionGasUsed", "ulong ExecutionGasUsed"),
        Property("receipt.effectiveGasPrice", ReceiptPath, "TxReceipt", "EffectiveGasPrice", "UInt256 EffectiveGasPrice"),
        Property("receipt.sender", ReceiptPath, "TxReceipt", "Sender", "Address? Sender"),
        Property("receipt.contractAddress", ReceiptPath, "TxReceipt", "ContractAddress", "Address? ContractAddress"),
        Property("receipt.recipient", ReceiptPath, "TxReceipt", "Recipient", "Address? Recipient"),
        Property("receipt.returnValue", ReceiptPath, "TxReceipt", "ReturnValue", "byte[]? ReturnValue"),
        Property("receipt.postState", ReceiptPath, "TxReceipt", "PostTransactionState", "Hash256? PostTransactionState"),
        Property("receipt.logs", ReceiptPath, "TxReceipt", "Logs", "LogEntry[]? Logs"),
        Property("receipt.error", ReceiptPath, "TxReceipt", "Error", "string? Error"),

        Property("transaction.type", TransactionPath, "Transaction", "Type", "TxType Type"),
        Property("transaction.to", TransactionPath, "Transaction", "To", "Address? To"),
        Property("transaction.sender", TransactionPath, "Transaction", "SenderAddress", "Address? SenderAddress"),
        Property("transaction.isCreation", TransactionPath, "Transaction", "IsContractCreation", "bool IsContractCreation"),
        Property("transaction.isMessageCall", TransactionPath, "Transaction", "IsMessageCall", "bool IsMessageCall"),
        Property("transaction.hash", TransactionPath, "Transaction", "Hash", "Hash256? Hash"),
        Method("transaction.effectivePrice", TransactionExtensionsPath, "TransactionExtensions", "CalculateEffectiveGasPrice", "UInt256 CalculateEffectiveGasPrice(bool,in UInt256)", 2, ["!eip1559Enabled", "tx.MaxPriorityFeePerGas", "UInt256.AddOverflow", "UInt256.Min"], []),

        Property("block.header", BlockPath, "Block", "Header", "BlockHeader Header"),
        Property("block.hash", BlockPath, "Block", "Hash", "Hash256? Hash"),
        Property("block.number", BlockPath, "Block", "Number", "ulong Number"),
        Field("header.baseFee", HeaderPath, "BlockHeader", "BaseFeePerGas", "UInt256 BaseFeePerGas"),
        Property("header.gasUsed", HeaderPath, "BlockHeader", "GasUsed", "ulong GasUsed"),
        Property("header.number", HeaderPath, "BlockHeader", "Number", "ulong Number"),
        Property("header.hash", HeaderPath, "BlockHeader", "Hash", "Hash256? Hash"),

        Method("accounting.accumulate", AccountingKernelPath, "BlockReceiptGasAccountingKernel", "Accumulate", "BlockReceiptGasAccountingResult Accumulate(ulong,ulong,ulong,ulong,ulong,ulong)", 6,
            ["previousExecutionGas + transactionExecutionGas", "previousStateGas + transactionStateGas", "previousReceiptGas + transactionPaidGas", "FromTotals"], ["public", "static"]),
        Method("accounting.fromTotals", AccountingKernelPath, "BlockReceiptGasAccountingKernel", "FromTotals", "BlockReceiptGasAccountingResult FromTotals(ulong,ulong,ulong)", 3,
            ["EthereumGasPolicy.CombineBlockGas"], ["public", "static"]),
    ];

    // A source-file pin alone is not a lowering admission. These complete syntax
    // fingerprints keep every modeled method bound to the reviewed owner/signature
    // body, including the live status and block-gas closure members.
    private static readonly IReadOnlyDictionary<string, string> ExpectedCanonicalSyntaxHashes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["terminal.success"] = "7bccebbcf46b029adfda8e8383cebb9dedde41c094e2dd77e2eaee4738c2ce25",
            ["terminal.failure"] = "e0a5de039a3d51eaa1e5a31f8e6349845eaab810fea0e859ecb40e0212d0183c",
            ["receipt.buildFailed"] = "8d960083a6d122f59ce092b331c1d5cadda3e78c520023b869a7a0cdff1921a6",
            ["receipt.build"] = "fe574214c3e985bcea6064d013f7fa23de0a746a829287d709e82a4dd64809b7",
            ["gas.update"] = "18ca4ce9c2417516021b7ae3b8b3b0cc021fe8dfffbc7200c27b0b274d137ae9",
            ["snapshot.take"] = "f7bfa263e478a6619e7b3a12087124c53d231fde9a37f6c80fd38c9f5f7d93be",
            ["snapshot.restore"] = "d3772dc02186b406ab682e8fb6af1a0e4c3eb8bb3d62182920c5e119a83a0406",
            ["caller.finalize"] = "7a23fe6a6dd94d216c5fed3afdbc83547ab02da8fcae8cdba34b9669e063b747",
            ["tracer.isTracingReceipt"] = "349b23825924dadbb60df681360bef19f62d0b648df7b484e262b365bffe5cd9",
            ["transaction.effectivePrice"] = "b3c07dd29a30b63ed035d49d76977153787062813cb15b190c6d6eb332e593e3",
            ["accounting.accumulate"] = "5076ed2bb136404645236793ca82f16e56040ddbbc7c3e82a9820a7ae105a7b7",
            ["accounting.fromTotals"] = "c10b64612478753ebd2ad41d8ba2357d6cbc406cae79b9a8875f4451aad20ace",
            ["status.failure"] = "b863d691afd4c03e5c85319404f7e142edaa143bca518afb9af9a140fa223504",
            ["status.success"] = "a828c117e353b2b0d902ef751d64edff7bc7661289443afef59105d4763cdf10",
            ["gas.combine.adapter"] = "46ddd69fa5b9ce57a93fbe33f0bd5addd1732e65da2ef533c49f556450dc3a44",
            ["gas.combine.kernel"] = "c213ffb57310e930cd94c860ef1d42f3adfe0af81cbf328a475a691c688e4012",
        };

    private static readonly string[] SemanticBindings =
    [
        "Standard mainnet uses an exact base BlockReceiptsTracer(parallel=false) instance whose IsTracingReceipt is always true; no subclass BuildReceipt override is admitted, and parallel/BAL reachability is out of scope.",
        "FinalizeTransaction emits exactly one terminal tracer call after normal commit, restore, or transient-reset work, forwards the single substate.Output on success and revert failure (empty output otherwise), then returns Ok exactly for EvmExceptionType.None or EvmException carrying SubstateError for a non-None type; the generated observation pairs both results.",
        "The live caller source also admits the BuildUp plus EIP-8037 branch: after WorldState.ResetTransient it calls WorldState.ReapEmptyAccounts before the receipt gate; that world-state cleanup is an ordered upstream effect and is not modeled by the terminal fold.",
        "MarkAsSuccess and MarkAsFailed build and append the receipt before forwarding the same terminal payload to nested and current tracers.",
        "BuildFailedReceipt delegates receipt construction and then sets Error; failure receipt logs are the empty array.",
        "UpdateCumulativeGasTracking delegates every three-counter addition and the header maximum to BlockReceiptGasAccountingKernel.",
        "Receipt gas is post-refund SpentGas, while block execution gas is EffectiveBlockGas and state gas is BlockStateGas.",
        "Header gas is updated only behind the sequential !parallel guard; parallel workers do not mutate the shared header.",
        "Receipt diagnostics are concrete zero-default ulong fields; BlockGas > 0 or BlockStateGas > 0 guards overwrite the corresponding fields, while an unguarded field remains zero.",
        "Transaction.IsContractCreation is admitted as To-is-null and the generated/spec models derive the same relation; contract creation places the terminal recipient in ContractAddress and leaves Recipient null, while message calls do the reverse.",
        "EffectiveGasPrice is an eagerly computed diagnostic oracle; ReturnValue is not a receipt field and output is forwarded only.",
        "Restore removes only a valid suffix, derives cumulative receipt gas from the retained last receipt, and recomputes header gas from the retained block totals.",
        "Unchecked ulong addition is retained exactly; the natural-number refinement requires fixed-width inputs and no-wrap sums.",
        "The exception-derived FastToString fallback is represented by a total vmError oracle and is ignored when EvmExceptionType is None.",
        "The generated transaction model derives IsContractCreation from To; ProductionFinalizeDomain is the explicit reachable-finalize adapter obligation excluding successful status with a non-None EvmExceptionType while retaining failure with None for ordinary revert results.",
        "Each admitted operation and projection carries Roslyn-derived syntax terms; the Lean emitter structurally lowers the admitted invocation arguments, local assignments, guards, projections, and return branches, and fails closed on an unsupported term rather than retaining a detached template.",
        "The consensus-side TransactionProcessorAdapterExtensions.ProcessTransaction wrapper and the block ProcessTransactions orchestration are outside this terminal source closure; normal-return handoff and post-transaction CommitState are future typed adapter boundaries.",
    ];

    // These release outputs form the compiler's production metadata closure.
    // The Evm bundle carries the locked third-party dependencies that are not
    // copied into the SaveDiskSpace production output; the Network.Enr test
    // bundle supplies Microsoft.Extensions.ObjectPool used by Transaction.cs.
    // Every directory is required: a partial closure is not an auditable
    // semantic compilation and fails before source lowering.
    private static readonly string[] CompilerReferenceClosurePaths =
    [
        "src/Nethermind/artifacts/bin/Nethermind.Init/release",
        "tools/artifacts/bin/Evm/release",
        "src/Nethermind/artifacts/bin/Nethermind.Network.Enr.Test/release",
    ];

    private static readonly string[] RequiredProductionAssemblyNames =
    [
        "Nethermind.Api",
        "Nethermind.Blockchain",
        "Nethermind.Config",
        "Nethermind.Consensus",
        "Nethermind.Core",
        "Nethermind.Crypto",
        "Nethermind.Db",
        "Nethermind.Evm",
        "Nethermind.Evm.Precompiles",
        "Nethermind.Facade",
        "Nethermind.Init",
        "Nethermind.Int256",
        "Nethermind.Logging",
        "Nethermind.Network",
        "Nethermind.Network.Contract",
        "Nethermind.Network.Enr",
        "Nethermind.Network.Stats",
        "Nethermind.Serialization.Json",
        "Nethermind.Serialization.Rlp",
        "Nethermind.Serialization.Ssz",
        "Nethermind.Specs",
        "Nethermind.State",
        "Nethermind.Trie",
        "Nethermind.TxPool",
    ];

    private static readonly string[] RequiredDependencyAssemblyNames =
    [
        "Collections.Pooled",
        "Microsoft.Extensions.ObjectPool",
    ];

    private static readonly IReadOnlyDictionary<string, string> RequiredCompilerAssemblyPaths =
        RequiredProductionAssemblyNames
            .Select(name => (name, path: $"src/Nethermind/artifacts/bin/Nethermind.Init/release/{name}.dll"))
            .Concat([
                ("Collections.Pooled", "tools/artifacts/bin/Evm/release/Collections.Pooled.dll"),
                ("Microsoft.Extensions.ObjectPool", "src/Nethermind/artifacts/bin/Nethermind.Network.Enr.Test/release/Microsoft.Extensions.ObjectPool.dll"),
            ])
            .ToDictionary(static item => item.Item1, static item => item.Item2, StringComparer.Ordinal);

    internal static IReadOnlyList<string> SourceRelativePaths => SourcePaths;
    internal static IReadOnlyList<string> BindingSourceRelativePaths => BindingSourcePaths;
    internal static IReadOnlyList<string> CompilerReferenceClosureRelativePaths => CompilerReferenceClosurePaths;
    internal static ReadOnlySpan<SourceIdentity> LeanDependencies => LeanDependencyPins;
    internal static IReadOnlyList<string> RequiredCompilerAssemblyNames =>
        RequiredProductionAssemblyNames.Concat(RequiredDependencyAssemblyNames).ToArray();

    private static IrDocument BuildDocument(BoundMember[] members) => new(
        SchemaVersion,
        ExtractorVersion,
        Kernel,
        new(
            "standard-mainnet sequential terminal path",
            "exact base BlockReceiptsTracer(parallel=false), with no BuildReceipt override, called from a normally returning FinalizeTransaction",
            [
                "parallel worker callbacks and BAL execution",
                "invalid snapshot values, unsynchronized receipt/gas histories, and SetReceipt states",
                "receipt Bloom/RLP/trie/root computation and receipt/database persistence",
                "VM execution, gas production, world-state and state-root correctness",
                "nested tracer totality, DI wiring, and CLR allocation behavior",
            ]),
        new(
            "Nethermind.Blockchain.Tracing.BlockReceiptGasAccountingKernel",
            "Accumulate(ulong,ulong,ulong,ulong,ulong,ulong)",
            "FromTotals(ulong,ulong,ulong)",
            AccountingKernelGeneratedPath,
            AccountingKernelRefinementPath,
            "exact unchecked ulong addition; no-wrap Nat corollary"),
        new(
            InputFields(members,
                "gas.spent", "gas.operation", "gas.block", "gas.state", "gas.max", "gas.refund",
                "gas.effectiveBlock", "gas.effectiveMax"),
            InputFields(members,
                "receipt.logs", "receipt.txType", "receipt.gasUsedTotal", "receipt.status", "receipt.recipient",
                "receipt.blockHash", "receipt.blockNumber", "receipt.index", "receipt.gasUsed",
                "receipt.effectiveGasPrice", "receipt.sender", "receipt.contractAddress", "receipt.txHash",
                "receipt.postState", "receipt.blockGasUsed", "receipt.executionGasUsed", "receipt.storageGasUsed",
                "receipt.error"),
            InputFields(members,
                "transaction.type", "transaction.to", "transaction.sender",
                "transaction.hash", "transaction.effectivePrice"),
            InputFields(members, "block.header", "block.hash", "block.number"),
            InputFields(members, "header.baseFee", "header.gasUsed", "header.number", "header.hash"),
            ["Status", "ShouldRevert", "Output", "SubstateError", "VmError", "EvmExceptionType", "SubstateResultError", "Logs", "StateRoot"]),
        Operations(members),
        new(
            "TakeSnapshot() = receipt count",
            "Restore(snapshot) truncates receipts and pre-refund block totals to the retained normal prefix",
            ["receipts", "cumulativeBlockGasPerTx", "cumulativeReceiptGas", "Block.Header.GasUsed"],
            "restore order: receipt and block-gas suffixes, then FromTotals(header) and cumulative receipt gas"),
        Mappings(members),
        new(
            EffectiveBlockGasExpression(members),
            BuildStatusCodeLowering(members),
            ReceiptFieldLowerings(members),
            FinalizationLowerings(members),
            FinalizationResultLowering(members)),
        SemanticBindings);

    internal static ExtractionResult Extract(string repoRoot, string outputDirectory, string? leanOutputPath = null)
        => ExtractCore(repoRoot, outputDirectory, leanOutputPath, enforcePinnedSources: true, referenceRoot: repoRoot);

    internal static void ValidateCheckedInArtifacts(string repoRoot)
    {
        string root = Path.GetFullPath(repoRoot);
        string output = Path.Combine(root, DefaultOutputRelativePath);
        string irPath = Path.Combine(output, IrFileName);
        string manifestPath = Path.Combine(output, ManifestFileName);
        string leanPath = Path.Combine(root, DefaultLeanRelativePath);
        if (!File.Exists(irPath) || !File.Exists(manifestPath) || !File.Exists(leanPath))
        {
            throw new ExtractionException("The checked-in receipt-terminal artifacts are incomplete.");
        }

        ValidateArtifacts(root, File.ReadAllBytes(manifestPath), File.ReadAllBytes(irPath), File.ReadAllBytes(leanPath));
    }

    // Test-only path: it preserves Roslyn binding, semantic validation, delegated-kernel
    // admission, and typed lowering while allowing a fixture to exercise source-to-IR
    // changes before the immutable production source admission is refreshed.
    internal static ExtractionResult ExtractForSemanticMutationTest(
        string repoRoot,
        string outputDirectory,
        string? leanOutputPath = null,
        string? semanticReferenceRoot = null) =>
        ExtractCore(repoRoot, outputDirectory, leanOutputPath, enforcePinnedSources: false,
            referenceRoot: semanticReferenceRoot ?? repoRoot);

    private static ExtractionResult ExtractCore(
        string repoRoot,
        string outputDirectory,
        string? leanOutputPath,
        bool enforcePinnedSources,
        string referenceRoot)
    {
        string root = Path.GetFullPath(repoRoot);
        string compilerRoot = Path.GetFullPath(referenceRoot);
        ValidateAdmissionTable();
        if (enforcePinnedSources)
        {
            // Check immutable source admission before opening the (potentially
            // large) compiler closure.  This keeps a source mutation diagnostic
            // deterministic even when a test fixture intentionally has no
            // metadata copy.
            ValidatePinnedSourceFiles(root);
            ValidateSourcePinsDocument(root);
        }
        SourceReadResult sourceRead = ReadSources(root, compilerRoot);
        SourceFile[] sources = sourceRead.Sources;
        if (enforcePinnedSources)
        {
            ValidateSourceFingerprints(sources);
        }
        ValidateBindingSourceFingerprints(root);
        BoundMember[] members = BindMembers(sources, enforcePinnedSources);
        ValidateSourceSemantics(sources, members);
        ValidateDelegatedKernel(root);

        IrDocument document = BuildDocument(members);
        ValidateDocument(document, members);
        byte[] irBytes = Serialize(document);
        string irHash = Sha256(irBytes);
        byte[] leanBytes = ReceiptTerminalFoldLeanEmitter.Emit(
            document,
            ExtractorVersion,
            CompilerVersion,
            TracerPath,
            Get(sources, TracerPath).Sha256,
            irHash);

        string output = Path.GetFullPath(outputDirectory);
        string leanPath = Path.GetFullPath(leanOutputPath ?? Path.Combine(root, DefaultLeanRelativePath));
        EnsureWithin(output, leanPath);
        string irPath = Path.Combine(output, IrFileName);
        string manifestPath = Path.Combine(output, ManifestFileName);

        SourceManifest manifest = BuildManifest(root, sources, members, sourceRead.CompilerReferences, irBytes, leanBytes);
        byte[] manifestBytes = Serialize(manifest);
        if (enforcePinnedSources)
        {
            ValidateArtifacts(root, manifestBytes, irBytes, leanBytes);
        }

        Directory.CreateDirectory(output);
        Directory.CreateDirectory(Path.GetDirectoryName(leanPath)!);
        ReceiptTerminalFoldArtifacts.Publish(irPath, irBytes, leanPath, leanBytes, manifestPath, manifestBytes);
        return new(irPath, manifestPath, leanPath, sources.Length, members.Length);
    }

    internal static void ValidateArtifacts(string repoRoot, byte[] manifestBytes, byte[] irBytes, byte[] leanBytes)
    {
        ValidateAdmissionTable();
        ValidateSourcePinsDocument(repoRoot);
        ValidateSerializedIr(irBytes);
        ValidateSerializedManifest(manifestBytes);
        string root = Path.GetFullPath(repoRoot);
        SourceReadResult sourceRead = ReadSources(root, root);
        SourceFile[] sources = sourceRead.Sources;
        ValidateSourceFingerprints(sources);
        ValidateBindingSourceFingerprints(root);
        ValidateDelegatedKernel(root);
        BoundMember[] members = BindMembers(sources);
        ValidateSourceSemantics(sources, members);
        IrDocument document = BuildDocument(members);
        ValidateDocument(document, members);
        byte[] expectedIr = Serialize(document);
        if (!irBytes.AsSpan().SequenceEqual(expectedIr))
        {
            throw new ExtractionException("The serialized receipt-terminal IR differs from the source-bound document.");
        }

        byte[] expectedLean = ReceiptTerminalFoldLeanEmitter.Emit(
            document,
            ExtractorVersion,
            CompilerVersion,
            TracerPath,
            TracerSha256,
            Sha256(expectedIr));
        ValidateGeneratedLean(leanBytes, expectedLean);
        SourceManifest expectedManifest = BuildManifest(root, sources, members, sourceRead.CompilerReferences, expectedIr, expectedLean);
        byte[] expectedManifestBytes = Serialize(expectedManifest);
        if (!manifestBytes.AsSpan().SequenceEqual(expectedManifestBytes))
        {
            throw new ExtractionException("The receipt-terminal manifest is not the exact source-derived identity.");
        }
    }

    internal static void ValidateSerializedIr(byte[] bytes)
    {
        RejectDuplicateJsonProperties(bytes, "IR");
        IrDocument document;
        try
        {
            document = JsonSerializer.Deserialize<IrDocument>(bytes, JsonOptions)
                ?? throw new ExtractionException("The receipt-terminal IR was empty.");
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"The receipt-terminal IR is invalid: {exception.Message}");
        }

        ValidateDocument(document, expectedMembers: null);
        ReceiptTerminalFoldLeanEmitter.ValidateIr(document);
        if (!Serialize(document).AsSpan().SequenceEqual(bytes))
        {
            throw new ExtractionException("The serialized receipt-terminal IR is not canonical.");
        }
    }

    internal static void ValidateSerializedManifest(byte[] bytes)
    {
        ValidateAdmissionTable();
        RejectDuplicateJsonProperties(bytes, "manifest");
        SourceManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SourceManifest>(bytes, JsonOptions)
                ?? throw new ExtractionException("The receipt-terminal manifest was empty.");
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"The receipt-terminal manifest is invalid: {exception.Message}");
        }

        if (manifest.SchemaVersion != SchemaVersion || manifest.ExtractorVersion != ExtractorVersion ||
            manifest.CompilerVersion != CompilerVersion || manifest.LanguageVersion != LanguageVersion.CSharp14.ToDisplayString() ||
            manifest.Kernel != Kernel)
        {
            throw new ExtractionException("The receipt-terminal manifest header changed.");
        }

        if (manifest.Sources is null || manifest.BindingSources is null || manifest.CompilerReferences is null ||
            manifest.Members is null || manifest.SemanticBindings is null ||
            manifest.Sources.Any(static source => source is null) || manifest.Members.Any(static member => member is null) ||
            manifest.BindingSources.Any(static source => source is null) ||
            manifest.CompilerReferences.Any(static reference => reference is null) ||
            manifest.SemanticBindings.Any(static binding => binding is null))
        {
            throw new ExtractionException("The receipt-terminal manifest contains null entries.");
        }

        if (manifest.Sources.Length != SourcePaths.Length || manifest.BindingSources.Length != BindingSourcePaths.Length ||
            manifest.Members.Length != MemberExpectations.Length ||
            !manifest.Sources.Select(static source => source.Path).SequenceEqual(SourcePaths, StringComparer.Ordinal))
        {
            throw new ExtractionException("The receipt-terminal manifest source or member closure changed.");
        }

        for (int index = 0; index < SourcePaths.Length; index++)
        {
            SourceIdentity source = manifest.Sources[index];
            if (source.Path != SourcePaths[index] || !ExpectedSourceHashes.TryGetValue(source.Path, out string? expected) ||
                source.Sha256 != expected)
            {
                throw new ExtractionException($"The receipt-terminal source identity at index {index} changed.");
            }
        }

        for (int index = 0; index < BindingSourcePaths.Length; index++)
        {
            SourceIdentity source = manifest.BindingSources[index];
            if (source.Path != BindingSourcePaths[index] || !ExpectedBindingSourceHashes.TryGetValue(source.Path, out string? expected) ||
                source.Sha256 != expected)
            {
                throw new ExtractionException($"The receipt-terminal binding source identity at index {index} changed.");
            }
        }

        ValidateCompilerReferenceIdentities(manifest.CompilerReferences);
        if (manifest.CompilerReferenceCount != manifest.CompilerReferences.Length ||
            manifest.CompilerReferenceAggregateSha256 != CompilerReferenceAggregateSha256(manifest.CompilerReferences))
        {
            throw new ExtractionException("The receipt-terminal compiler-reference count or aggregate changed.");
        }

        HashSet<string> ids = new(StringComparer.Ordinal);
        for (int index = 0; index < MemberExpectations.Length; index++)
        {
            MemberExpectation expected = MemberExpectations[index];
            MemberIdentity actual = manifest.Members[index];
            if (!ids.Add(actual.Id) || actual.Id != expected.Id || actual.SourcePath != expected.SourcePath ||
                actual.ContainingType != expected.ContainingType || actual.Member != expected.Member ||
                actual.Kind != expected.Kind || actual.Signature != expected.Signature ||
                actual.ParameterTypes is null || actual.SemanticType is null || actual.NodeKind is null ||
                actual.CanonicalSha256 is null || actual.ParameterTypes.Length != expected.ParameterCount ||
                ExpectedCanonicalSyntaxHashes.TryGetValue(expected.Id, out string? expectedCanonicalHash) &&
                actual.CanonicalSha256 != expectedCanonicalHash)
            {
                throw new ExtractionException($"The receipt-terminal member identity at index {index} changed.");
            }
            RequireSha(actual.CanonicalSha256, $"member hash for {actual.Id}");
        }

        ValidateKernelIdentity(manifest.AccountingKernel);
        if (manifest.Ir is null || manifest.Lean is null || manifest.Ir.Path != IrFileName ||
            manifest.Lean.Path != Normalize(DefaultLeanRelativePath) || manifest.SemanticBindings is null ||
            !manifest.SemanticBindings.SequenceEqual(SemanticBindings, StringComparer.Ordinal) ||
            manifest.CombinedSourceSha256 != CombinedHash(manifest.Sources
                .Concat(manifest.BindingSources)
                .Select(static source => $"{source.Path}\0{source.Sha256}")) ||
            manifest.CombinedMemberSha256 != CombinedHash(manifest.Members.Select(static member =>
                $"{member.SourcePath}\0{member.ContainingType}\0{member.Member}\0{member.Kind}\0{member.Signature}\0{member.CanonicalSha256}")))
        {
            throw new ExtractionException("The receipt-terminal manifest aggregates or semantic bindings changed.");
        }

        RequireSha(manifest.CombinedSourceSha256, "combined source hash");
        RequireSha(manifest.CombinedMemberSha256, "combined member hash");
        RequireSha(manifest.CompilerReferenceAggregateSha256, "compiler-reference aggregate hash");
        foreach (CompilerReferenceIdentity reference in manifest.CompilerReferences)
        {
            RequireSha(reference.Sha256, $"compiler reference hash for {reference.Path}");
        }
        RequireSha(manifest.Ir.Sha256, "IR hash");
        RequireSha(manifest.Lean.Sha256, "Lean hash");
        if (!Serialize(manifest).AsSpan().SequenceEqual(bytes))
        {
            throw new ExtractionException("The serialized receipt-terminal manifest is not canonical.");
        }
    }

    internal static IrDocument DeserializeIr(byte[] bytes)
    {
        ValidateSerializedIr(bytes);
        return JsonSerializer.Deserialize<IrDocument>(bytes, JsonOptions)!;
    }

    private static string CompilerVersion =>
        typeof(CSharpCompilation).Assembly.GetName().Version?.ToString() ?? "unknown";

    private static string[] InputFields(BoundMember[] members, params string[] memberIds) =>
        memberIds.Select(id => Get(members, id).Identity.Member).ToArray();

    private static OperationDescriptor[] Operations(BoundMember[] members) =>
    [
        SourceOperation(members, "markAsSuccess", "terminal", "terminal.success"),
        SourceOperation(members, "markAsFailed", "terminal", "terminal.failure"),
        SourceOperation(members, "buildFailedReceipt", "receipt", "receipt.buildFailed"),
        SourceOperation(members, "buildReceipt", "receipt", "receipt.build"),
        SourceOperation(members, "updateCumulativeGasTracking", "gas", "gas.update"),
        SourceOperation(members, "takeSnapshot", "snapshot", "snapshot.take"),
        SourceOperation(members, "restore", "snapshot", "snapshot.restore"),
        SourceOperation(members, "finalizeTransaction", "caller", "caller.finalize"),
    ];

    private static OperationDescriptor SourceOperation(
        BoundMember[] members,
        string name,
        string phase,
        string memberId)
    {
        BoundMember member = Get(members, memberId);
        SemanticStep[] steps = ExtractSemanticSteps(member.SemanticModel, member.Node);
        SemanticOperation body = ExtractSemanticOperation(member.SemanticModel, member.Node);
        return new(
            name,
            phase,
            member.Identity.SourcePath,
            member.Identity.ContainingType,
            member.Identity.Member,
            member.Identity.Signature,
            DeriveOperationInputs(member, steps),
            DeriveOperationOutputs(member, steps),
            DeriveOperationEffects(name, steps),
            DeriveOperationGuards(name, steps),
            DeriveOperationProjections(name, steps),
            DeriveOperationExclusions(name),
            steps,
            body);
    }

    private static string[] DeriveOperationInputs(BoundMember member, SemanticStep[] steps)
    {
        if (member.Node is not MethodDeclarationSyntax method)
        {
            return [];
        }

        List<string> inputs = method.ParameterList.Parameters
            .Select(static parameter => parameter.Identifier.ValueText)
            .Where(name => ContainsIdentifier(steps, name))
            .Select(NormalizeInputName)
            .ToList();
        // The state-owned histories are represented by model-facing aliases. They are
        // admitted only when the corresponding production state read is present.
        if (member.Identity.Id == "gas.update")
        {
            inputs.Clear();
            if (ContainsText(steps, "_cumulativeBlockGasPerTx")) inputs.Add("previousBlockTotals");
            if (ContainsText(steps, "_cumulativeReceiptGas")) inputs.Add("cumulativeReceiptGas");
            if (ContainsIdentifier(steps, "gasConsumed")) inputs.Add("gasConsumed");
        }
        else if (member.Identity.Id == "snapshot.take")
        {
            inputs.Clear();
            if (ContainsText(steps, "_txReceipts.Count")) inputs.Add("receipts");
        }
        else if (member.Identity.Id == "snapshot.restore")
        {
            inputs.Clear();
            if (ContainsIdentifier(steps, "snapshot")) inputs.Add("snapshotPosition");
            if (ContainsText(steps, "_txReceipts")) inputs.Add("receipts");
            if (ContainsText(steps, "_cumulativeBlockGasPerTx")) inputs.Add("gasHistory");
        }
        else if (member.Identity.Id == "caller.finalize")
        {
            inputs = new[] { "statusCode", "substate", "executingAccount", "spentGas", "tracer" }
                .Where(name => ContainsIdentifier(steps, name))
                .ToList();
        }
        if (member.Identity.Id == "receipt.build" && ContainsText(steps, "CurrentTx"))
        {
            inputs.Add("CurrentTx");
        }
        if (member.Identity.Id == "receipt.build" && ContainsText(steps, "Block"))
        {
            inputs.Add("Block");
        }
        return inputs.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string[] DeriveOperationOutputs(BoundMember member, SemanticStep[] steps)
    {
        string text = string.Join('\n', steps.Select(static step => step.Text));
        List<string> outputs = [];
        AddIf(text.Contains("_txReceipts.Add(", StringComparison.Ordinal), "receipt");
        AddIf(text.Contains("_cumulativeBlockGasPerTx", StringComparison.Ordinal), "gasHistory");
        AddIf(text.Contains("Block.Header.GasUsed", StringComparison.Ordinal), "headerGasUsed");
        AddIf(text.Contains("_cumulativeReceiptGas", StringComparison.Ordinal), "cumulativeReceiptGas");
        AddIf(member.Identity.SemanticType.Contains("TxReceipt", StringComparison.Ordinal), "receipt");
        AddIf(text.Contains("tracer.MarkAs", StringComparison.Ordinal), "terminalTracerCall");
        AddIf(member.Identity.SemanticType.Contains("TransactionResult", StringComparison.Ordinal), "TransactionResult");
        if (member.Identity.Id is "terminal.success" or "terminal.failure" && outputs.Contains("receipt", StringComparer.Ordinal))
        {
            outputs.Add("gasHistory");
            outputs.Add("headerGasUsed");
            outputs.Add("cumulativeReceiptGas");
        }
        if (member.Identity.Id == "receipt.buildFailed")
        {
            outputs.Remove("receipt");
            outputs.Add("failureReceipt");
        }
        if (member.Identity.Id == "snapshot.take" && text.Contains("_txReceipts.Count", StringComparison.Ordinal))
        {
            outputs.Add("snapshotPosition");
        }
        if (member.Identity.Id == "snapshot.restore")
        {
            outputs.Insert(0, "receipts");
            outputs.Add("gasHistory");
        }
        if (member.Identity.Id == "caller.finalize")
        {
            outputs.Add("FinalizationObservation");
        }
        return outputs.Distinct(StringComparer.Ordinal).ToArray();

        void AddIf(bool condition, string value)
        {
            if (condition) outputs.Add(value);
        }
    }

    private static string[] DeriveOperationEffects(string name, SemanticStep[] steps)
    {
        List<string> effects = [];
        string wholeText = string.Join('\n', steps.Select(static step => step.Text));
        if (name == "finalizeTransaction" &&
            (wholeText.Contains("WorldState.Commit", StringComparison.Ordinal) ||
             wholeText.Contains("WorldState.Reset", StringComparison.Ordinal)))
        {
            effects.Add("normalFinalize");
        }
        foreach (SemanticStep step in FlattenSteps(steps))
        {
            string text = step.Text;
            if ((name is "markAsSuccess" or "markAsFailed") &&
                text.Contains("_txReceipts.Add(Build", StringComparison.Ordinal))
            {
                Add("gasMutation");
                if (name == "markAsFailed")
                {
                    Add("emptyFailureLogs");
                    Add("errorAssignment");
                }
                Add("receiptAppend");
            }
            if (text.Contains("otherTxTracer.MarkAs", StringComparison.Ordinal)) Add("nestedTracerForward");
            if (text.Contains("_currentTxTracer.MarkAs", StringComparison.Ordinal)) Add("currentTracerForward");
            if (name == "buildFailedReceipt" && text.Contains("BuildReceipt", StringComparison.Ordinal)) Add("delegateBuildReceipt");
            if (name == "buildFailedReceipt" && text.Contains("receipt.Error", StringComparison.Ordinal)) Add("errorAssignment");
            if (name == "buildReceipt" && text.Contains("UpdateCumulativeGasTracking", StringComparison.Ordinal)) Add("updateGas");
            if (name == "buildReceipt" && text.Contains("TxReceipt txReceipt = new", StringComparison.Ordinal)) Add("receiptFieldProjection");
            if (name == "buildReceipt" && text.Contains("gasConsumed.BlockGas > 0", StringComparison.Ordinal)) Add("diagnosticBlockGasGuard");
            if (name == "buildReceipt" && text.Contains("gasConsumed.BlockStateGas > 0", StringComparison.Ordinal)) Add("diagnosticStateGasGuard");
            if (name == "updateCumulativeGasTracking" && text.Contains("BlockReceiptGasAccountingKernel.Accumulate", StringComparison.Ordinal)) Add("kernelAccumulate");
            if (name == "updateCumulativeGasTracking" && text.Contains("_cumulativeBlockGasPerTx.Add", StringComparison.Ordinal)) Add("historyAppend");
            if (name == "updateCumulativeGasTracking" && text.Contains("if (!parallel)", StringComparison.Ordinal)) Add("sequentialHeaderUpdate");
            if (name == "updateCumulativeGasTracking" && text.Contains("_cumulativeReceiptGas = accounting", StringComparison.Ordinal)) Add("receiptCounterUpdate");
            if (name == "takeSnapshot" && text.Contains("_txReceipts.Count", StringComparison.Ordinal)) Add("receiptCount");
            if (name == "restore" && text.Contains("RemoveRange", StringComparison.Ordinal)) Add("suffixRemoval");
            if (name == "restore" && text.Contains("BlockReceiptGasAccountingKernel.FromTotals", StringComparison.Ordinal)) Add("kernelFromTotals");
            if (name == "restore" && text.Contains("Block.Header.GasUsed =", StringComparison.Ordinal)) Add("headerRestore");
            if (name == "restore" && text.Contains("_cumulativeReceiptGas = accounting", StringComparison.Ordinal)) Add("receiptCounterRestore");
            if (name == "finalizeTransaction" && text.Contains("if (tracer.IsTracingReceipt)", StringComparison.Ordinal)) Add("receiptGate");
            if (name == "finalizeTransaction" && text.Contains("substate.ShouldRevert", StringComparison.Ordinal)) Add("failureOutputGuard");
            if (name == "finalizeTransaction" && text.Contains("substate.Error", StringComparison.Ordinal)) Add("failureErrorPrecedence");
            if (name == "finalizeTransaction" && text.Contains("substate.Logs.Count", StringComparison.Ordinal)) Add("successLogsProjection");
            if (name == "finalizeTransaction" && text.Contains("tracer.MarkAs", StringComparison.Ordinal)) Add("terminalCall");
            if (name == "finalizeTransaction" && text.Contains("return substate.EvmExceptionType", StringComparison.Ordinal)) Add("resultReturn");
            if (name == "finalizeTransaction" && text.Contains("WorldState.ReapEmptyAccounts", StringComparison.Ordinal)) Add("eip8037AccountReapBeforeTerminal");
        }
        return effects.ToArray();

        void Add(string effect)
        {
            if (!effects.Contains(effect, StringComparer.Ordinal)) effects.Add(effect);
        }
    }

    private static string[] DeriveOperationGuards(string name, SemanticStep[] steps)
    {
        List<string> guards = [];
        foreach (SemanticStep step in FlattenSteps(steps))
        {
            if (step.Text.Contains("if (!parallel)", StringComparison.Ordinal)) guards.Add("!parallel");
            if (name == "restore" && step.Text.Contains("if (numToRemove > 0)", StringComparison.Ordinal))
            {
                guards.Add("numToRemove > 0");
            }
        }
        if (name is "markAsSuccess" or "markAsFailed") guards.Add("parallel=false header guard");
        if (name == "finalizeTransaction") guards.Add("normally returning; exact base BlockReceiptsTracer.IsTracingReceipt = true");
        return guards.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string[] DeriveOperationProjections(string name, SemanticStep[] steps)
    {
        string text = string.Join('\n', steps.Select(static step => step.Text));
        return name switch
        {
            "markAsSuccess" => ["output, logs, stateRoot forwarded; receipt has no output"],
            "markAsFailed" => ["output, error, stateRoot forwarded; output is not stored in receipt"],
            "buildFailedReceipt" => ["logs=[]; error=error"],
            "buildReceipt" => ["all consensus and diagnostic fields listed in receiptFields; typed oracles for hashes/addresses/price; guarded diagnostics retain zero defaults"],
            "updateCumulativeGasTracking" => ["unchecked ulong additions delegated to the pinned kernel"],
            "takeSnapshot" => ["position is a valid normal prefix"],
            "restore" => ["valid synchronized prefix only"],
            "finalizeTransaction" => ["single substate.Output forwarded on success and ShouldRevert failure; non-revert failure forwards empty; error prefers substate.Error then EvmExceptionType.FastToString; BuildUp plus EIP-8037 ReapEmptyAccounts is admitted before the receipt gate but remains outside the fold; generated observation retains the returned result"],
            _ => [text],
        };
    }

    private static string[] DeriveOperationExclusions(string name) =>
        name switch
        {
            "markAsSuccess" or "markAsFailed" => ["nested tracer totality"],
            "buildReceipt" => ["Bloom/RLP/root"],
            "updateCumulativeGasTracking" => ["parallel/BAL"],
            "takeSnapshot" => ["invalid snapshot"],
            "restore" => ["invalid snapshot, SetReceipt, parallel/BAL"],
            "finalizeTransaction" => ["VM/world-state semantics"],
            _ => [],
        };

    private static string NormalizeInputName(string name) =>
        name switch
        {
            "logEntries" => "logs",
            _ => name,
        };

    private static SemanticStep[] ExtractSemanticSteps(SemanticModel model, SyntaxNode node) =>
        node switch
        {
            MethodDeclarationSyntax method when method.Body is not null =>
                method.Body.Statements.Select(statement => ToSemanticStep(model, statement)).ToArray(),
            MethodDeclarationSyntax method when method.ExpressionBody is not null =>
                [new("ArrowExpressionClause", Canonical(method.ExpressionBody.Expression),
                    ToSemanticExpression(model, method.ExpressionBody.Expression), [],
                    method.ExpressionBody.Expression.SpanStart, method.ExpressionBody.Expression.Span.End)],
            PropertyDeclarationSyntax property when property.ExpressionBody is not null =>
                [new("ArrowExpressionClause", Canonical(property.ExpressionBody.Expression),
                    ToSemanticExpression(model, property.ExpressionBody.Expression), [],
                    property.ExpressionBody.Expression.SpanStart, property.ExpressionBody.Expression.Span.End)],
            _ => [],
        };

    private static SemanticOperation ExtractSemanticOperation(SemanticModel model, SyntaxNode node)
    {
        if (node is not MethodDeclarationSyntax method)
        {
            return new([], [], [], null);
        }

        SemanticInvocation[] invocations = method.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(invocation =>
            {
                SemanticExpression expression = ToSemanticExpression(model, invocation)!;
                SemanticExpression member = expression.Children.FirstOrDefault() ??
                    throw new ExtractionException($"Invocation '{Canonical(invocation)}' has no member syntax term.");
                IMethodSymbol method = InvocationSymbol(model, invocation);
                SemanticExpression? receiver = member.Children.FirstOrDefault();
                string receiverSymbolId = receiver?.SymbolId ?? SymbolIdentity(method.ContainingType ??
                    throw new ExtractionException($"Invocation '{Canonical(invocation)}' has no containing type."));
                string receiverTypeName = receiver?.TypeName ??
                    (method.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ??
                        throw new ExtractionException($"Invocation '{Canonical(invocation)}' has no containing type."));
                return new SemanticInvocation(
                    InvocationReceiver(invocation),
                    method.Name,
                    invocation.ArgumentList.Arguments
                        .Select(argument => ToSemanticExpression(model, argument.Expression)!)
                        .ToArray(),
                    expression,
                    invocation.SpanStart,
                    EnclosingBranches(model, invocation),
                    expression.SymbolId,
                    expression.TypeName,
                    receiverSymbolId,
                    receiverTypeName);
            })
            .OrderBy(static invocation => invocation.Position)
            .ToArray();

        SemanticAssignment[] assignments = method.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Select(assignment => new SemanticAssignment(
                Canonical(assignment.Left),
                ToSemanticExpression(model, assignment.Right)!,
                EnclosingBranches(model, assignment),
                "assignment",
                assignment.SpanStart,
                ToSemanticExpression(model, assignment.Left)))
            .Concat(method.DescendantNodes()
                .OfType<VariableDeclaratorSyntax>()
                .Where(static variable => variable.Initializer is not null)
                .Select(variable => new SemanticAssignment(
                    variable.Identifier.ValueText,
                    ToSemanticExpression(model, variable.Initializer!.Value)!,
                    EnclosingBranches(model, variable),
                    "initializer",
                    variable.Initializer.Value.SpanStart,
                    LocalTargetExpression(model, variable))))
            .OrderBy(static assignment => assignment.Position)
            .ToArray();

        SemanticStep[] guards = method.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .Select(conditional => ToSemanticStep(model, conditional))
            .ToArray();
        ReturnStatementSyntax? returnStatement = method.DescendantNodes()
            .OfType<ReturnStatementSyntax>()
            .LastOrDefault();
        SemanticExpression? returnExpression = ToSemanticExpression(model, returnStatement?.Expression)
            ?? ToSemanticExpression(model, method.ExpressionBody?.Expression);
        return new(invocations, assignments, guards, returnExpression);
    }

    private static SemanticBranch[] EnclosingBranches(SemanticModel model, SyntaxNode node)
    {
        List<SemanticBranch> branches = [];
        foreach (IfStatementSyntax conditional in node.Ancestors().OfType<IfStatementSyntax>().Reverse())
        {
            if (conditional.Condition.Span.Contains(node.Span)) continue;
            bool whenTrue = conditional.Statement.Span.Contains(node.Span);
            if (!whenTrue && conditional.Else?.Statement.Span.Contains(node.Span) != true)
                throw new ExtractionException("Receipt-terminal branch path escaped its enclosing conditional.");
            branches.Add(new(conditional.SpanStart, ToSemanticExpression(model, conditional.Condition)!, whenTrue));
        }
        return branches.ToArray();
    }

    private static string InvocationReceiver(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => Canonical(member.Expression),
            _ => "",
        };

    private static IMethodSymbol InvocationSymbol(SemanticModel model, InvocationExpressionSyntax invocation)
    {
        SymbolInfo info = model.GetSymbolInfo(invocation);
        RejectCandidateSymbols(info, invocation, "invocation");
        if (model.GetOperation(invocation) is IInvocationOperation operation)
        {
            ValidateSymbol(operation.TargetMethod, "semantic invocation", $"invocation '{Canonical(invocation)}' target");
            return operation.TargetMethod;
        }

        throw new ExtractionException($"Roslyn did not bind invocation '{Canonical(invocation)}' to an invocation operation.");
    }

    private static SemanticStep ToSemanticStep(SemanticModel model, StatementSyntax statement)
    {
        SemanticStep step = statement switch
        {
            BlockSyntax block => new("Block", Canonical(block), null,
                block.Statements.Select(child => ToSemanticStep(model, child)).ToArray()),
            IfStatementSyntax conditional => new(
                "IfStatement",
                Canonical(conditional),
                ToSemanticExpression(model, conditional.Condition),
                ConditionalChildren(model, conditional)),
            LocalDeclarationStatementSyntax local => new(
                local.UsingKeyword.IsKind(SyntaxKind.None) ? "LocalDeclarationStatement" : "UsingDeclaration",
                Canonical(local),
                local.Declaration.Variables.Count == 1
                    ? ToSemanticExpression(model, local.Declaration.Variables[0].Initializer?.Value)
                    : null,
                []),
            ExpressionStatementSyntax expression => new(
                "ExpressionStatement",
                Canonical(expression),
                ToSemanticExpression(model, expression.Expression),
                []),
            ReturnStatementSyntax @return => new(
                "ReturnStatement",
                Canonical(@return),
                ToSemanticExpression(model, @return.Expression),
                []),
            _ => new(statement.Kind().ToString(), Canonical(statement), null, []),
        };
        return step with
        {
            Start = statement.SpanStart,
            End = statement.Span.End,
            DeclaredTarget = statement is LocalDeclarationStatementSyntax declaration && declaration.Declaration.Variables.Count == 1
                ? LocalTargetExpression(model, declaration.Declaration.Variables[0]) : null,
        };
    }

    private static SemanticStep[] ConditionalChildren(SemanticModel model, IfStatementSyntax conditional)
    {
        List<SemanticStep> children = [ToSemanticStep(model, conditional.Statement)];
        if (conditional.Else is ElseClauseSyntax @else)
        {
            children.Add(new("ElseClause", Canonical(@else), null,
                [ToSemanticStep(model, @else.Statement)], @else.SpanStart, @else.Span.End));
        }
        return children.ToArray();
    }

    private static SemanticExpression? ToSemanticExpression(SemanticModel model, SyntaxNode? node)
    {
        if (node is null) return null;
        return new(
            node.Kind().ToString(),
            Canonical(node),
            node.ChildNodes().Select(child => ToSemanticExpression(model, child)).Where(static child => child is not null)
                .Cast<SemanticExpression>().ToArray(),
            SymbolIdentity(model, node),
            TypeName(model, node),
            node.SpanStart);
    }

    private static string? SymbolIdentity(SemanticModel model, SyntaxNode node)
    {
        SymbolInfo info = model.GetSymbolInfo(node);
        RejectCandidateSymbols(info, node, "semantic expression");
        ISymbol? symbol = info.Symbol;
        if (symbol is not null)
        {
            ValidateSymbol(symbol, "semantic expression", $"symbol at '{Canonical(node)}'");
        }
        return symbol is null ? null : SymbolIdentity(symbol);
    }

    private static string SymbolIdentity(ISymbol symbol) =>
        $"{symbol.Kind}:{symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{symbol.Name}:" +
        symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static string? TypeName(SemanticModel model, SyntaxNode node)
    {
        Microsoft.CodeAnalysis.TypeInfo info = model.GetTypeInfo(node);
        ITypeSymbol? type = info.ConvertedType ?? info.Type;
        ValidateType(type, "semantic expression", $"type at '{Canonical(node)}'");
        SymbolInfo symbolInfo = model.GetSymbolInfo(node);
        RejectCandidateSymbols(symbolInfo, node, "semantic expression");
        if (type is null && symbolInfo.Symbol is ITypeSymbol namedType)
        {
            ValidateType(namedType, "semantic expression", $"named type at '{Canonical(node)}'");
            type = namedType;
        }
        return type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    private static SemanticExpression LocalTargetExpression(SemanticModel model, VariableDeclaratorSyntax variable)
    {
        ISymbol symbol = model.GetDeclaredSymbol(variable)
            ?? throw new ExtractionException($"Local '{variable.Identifier.ValueText}' has no declared symbol.");
        ValidateSymbol(symbol, "semantic local", $"local '{variable.Identifier.ValueText}'");
        string? type = variable.Initializer is null ? null : TypeName(model, variable.Initializer.Value);
        return new(
            "IdentifierName",
            variable.Identifier.ValueText,
            [],
            SymbolIdentity(symbol),
            type,
            variable.SpanStart);
    }

    private static string Canonical(SyntaxNode node) =>
        node.WithoutTrivia().NormalizeWhitespace().ToFullString();

    private static SemanticExpression EffectiveBlockGasExpression(BoundMember[] members)
    {
        BoundMember member = Get(members, "gas.effectiveBlock");
        SemanticExpression? expression = member.Node switch
        {
            PropertyDeclarationSyntax property when property.ExpressionBody is not null =>
                ToSemanticExpression(member.SemanticModel, property.ExpressionBody.Expression),
            _ => null,
        };
        return expression ?? throw new ExtractionException("EffectiveBlockGas is not an expression-bodied source property.");
    }

    private static StatusCodeLowering BuildStatusCodeLowering(BoundMember[] members) =>
        new(
            StatusConstantExpression(Get(members, "status.failure")),
            StatusConstantExpression(Get(members, "status.success")));

    private static SemanticExpression StatusConstantExpression(BoundMember member)
    {
        if (member.Node is not FieldDeclarationSyntax field ||
            field.Declaration.Variables.Count != 1 ||
            field.Declaration.Variables[0].Initializer is not EqualsValueClauseSyntax initializer)
        {
            throw new ExtractionException($"Status member {member.Identity.Id} has no source initializer.");
        }

        return ToSemanticExpression(member.SemanticModel, initializer.Value)
            ?? throw new ExtractionException($"Status member {member.Identity.Id} has no source expression.");
    }

    private static FinalizationResultLowering FinalizationResultLowering(BoundMember[] members)
    {
        BoundMember finalize = Get(members, "caller.finalize");
        if (finalize.Node is not MethodDeclarationSyntax method)
        {
            throw new ExtractionException("FinalizeTransaction is not a source method.");
        }

        ReturnStatementSyntax result = method.DescendantNodes().OfType<ReturnStatementSyntax>().LastOrDefault()
            ?? throw new ExtractionException("FinalizeTransaction has no terminal result return.");
        if (result.Expression is not ConditionalExpressionSyntax conditional)
        {
            throw new ExtractionException("FinalizeTransaction result is not the admitted conditional EvmException/Ok shape.");
        }

        InvocationExpressionSyntax? exceptionCall = conditional.WhenTrue.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(invocation => invocation.Expression.ToString() == "TransactionResult.EvmException");
        if (exceptionCall is null || exceptionCall.ArgumentList.Arguments.Count != 2)
        {
            throw new ExtractionException("FinalizeTransaction result is not the admitted conditional EvmException/Ok shape.");
        }

        return new(
            ToSemanticExpression(finalize.SemanticModel, conditional.Condition)!,
            ToSemanticExpression(finalize.SemanticModel, exceptionCall.ArgumentList.Arguments[0].Expression)!,
            ToSemanticExpression(finalize.SemanticModel, exceptionCall.ArgumentList.Arguments[1].Expression)!,
            ToSemanticExpression(finalize.SemanticModel, conditional.WhenFalse)!);
    }

    private static LoweringBinding[] ReceiptFieldLowerings(BoundMember[] members)
    {
        BoundMember buildReceipt = Get(members, "receipt.build");
        string[] fields =
        [
            "Logs", "TxType", "GasUsedTotal", "StatusCode", "Recipient", "BlockHash", "BlockNumber", "Index",
            "GasUsed", "EffectiveGasPrice", "Sender", "ContractAddress", "TxHash", "PostTransactionState",
            "BlockGasUsed", "ExecutionGasUsed", "StorageGasUsed", "Error",
        ];
        return fields.Select(field =>
        {
            SemanticExpression? expression = ExtractMappingExpression(buildReceipt.SemanticModel, buildReceipt.Node, $"receipt.{field}");
            expression ??= new("DefaultLiteral", "null", [], null, "global::System.String");
            return new LoweringBinding($"receipt.{field}", "BlockReceiptsTracer.BuildReceipt", expression,
                ExtractMappingGuard(buildReceipt.SemanticModel, buildReceipt.Node, field));
        }).ToArray();
    }

    private static SemanticExpression? ExtractMappingGuard(SemanticModel model, SyntaxNode node, string field)
    {
        if (node is not MethodDeclarationSyntax method) return null;
        IfStatementSyntax? guard = method.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .FirstOrDefault(candidate => candidate.Statement.DescendantNodesAndSelf()
                .OfType<AssignmentExpressionSyntax>()
                .Any(assignment => assignment.Left.ToString().EndsWith($".{field}", StringComparison.Ordinal)));
        return guard is null ? null : ToSemanticExpression(model, guard.Condition);
    }

    private static LoweringBinding[] FinalizationLowerings(BoundMember[] members)
    {
        BoundMember finalize = Get(members, "caller.finalize");
        return
        [
            new("terminal.output", "TransactionProcessorBase.FinalizeTransaction", ExtractMappingExpression(finalize.SemanticModel, finalize.Node, "terminal.output")),
            new("terminal.error", "TransactionProcessorBase.FinalizeTransaction", ExtractMappingExpression(finalize.SemanticModel, finalize.Node, "terminal.error")),
            new("finalization.result", "TransactionProcessorBase.FinalizeTransaction", ExtractMappingExpression(finalize.SemanticModel, finalize.Node, "finalization.result")),
            new("finalization.result.error", "TransactionProcessorBase.FinalizeTransaction", ExtractMappingExpression(finalize.SemanticModel, finalize.Node, "finalization.result.error")),
        ];
    }

    private static bool ContainsText(IEnumerable<SemanticStep> steps, string value) =>
        steps.Any(step => step.Text.Contains(value, StringComparison.Ordinal) || ContainsText(step.Children, value));

    private static bool ContainsIdentifier(IEnumerable<SemanticStep> steps, string identifier) =>
        steps.Any(step => step.Expression is not null && ContainsIdentifier(step.Expression, identifier) || ContainsIdentifier(step.Children, identifier));

    private static bool ContainsIdentifier(SemanticExpression expression, string identifier) =>
        expression.Kind == "IdentifierName" && expression.Text == identifier ||
        expression.Children.Any(child => ContainsIdentifier(child, identifier));

    private static IEnumerable<SemanticStep> FlattenSteps(IEnumerable<SemanticStep> steps)
    {
        foreach (SemanticStep step in steps)
        {
            yield return step;
            foreach (SemanticStep child in FlattenSteps(step.Children)) yield return child;
        }
    }

    private static MappingDescriptor[] Mappings(BoundMember[] members) =>
    [
        Mapping(members, "gas.effectiveBlock", "gasConsumed", "block.executionGas", "gasConsumed.BlockGas > 0 || gasConsumed.BlockStateGas > 0 ? BlockGas : SpentGas"),
        Mapping(members, "gas.state", "gasConsumed", "block.stateGas", "state-dimension cumulative counter"),
        Mapping(members, "gas.spent", "gasConsumed", "receipt.paidGas", "post-refund cumulative receipt counter"),
        Mapping(members, "gas.effectiveBlock", "buildReceipt.gasConsumed", "receipt.BlockGasUsed", "BlockGas > 0 assigns the effective block gas; otherwise the nonnullable receipt field remains zero"),
        Mapping(members, "gas.operation", "buildReceipt.gasConsumed", "receipt.ExecutionGasUsed", "BlockGas > 0 assigns operation gas; otherwise the nonnullable receipt field remains zero"),
        Mapping(members, "gas.state", "buildReceipt.gasConsumed", "receipt.StorageGasUsed", "BlockStateGas > 0 assigns state gas; otherwise the nonnullable receipt field remains zero"),
        Mapping(members, "receipt.logs", "receipt", "terminal.logs", "success logs; failure BuildFailedReceipt supplies []"),
        Mapping(members, "receipt.returnValue", "receipt", "excluded", "never assigned by BuildReceipt; output is forwarding payload only"),
        Mapping(members, "transaction.isCreation", "transaction", "receipt.Recipient/ContractAddress", "production IsContractCreation is To-is-null; model derives the same relation and maps creation recipient to ContractAddress, call recipient to Recipient"),
        Mapping(members, "transaction.effectivePrice", "transaction", "receipt.EffectiveGasPrice", "typed diagnostic price oracle"),
        SourceMapping(members, "caller.finalize", "FinalizeTransaction.substate.Error", "terminal.error",
            "takes precedence over EvmExceptionType.FastToString"),
        SourceMapping(members, "caller.finalize", "FinalizeTransaction.substate.EvmExceptionType", "finalization.result",
            "None returns TransactionResult.Ok; non-None returns TransactionResult.EvmException"),
        SourceMapping(members, "caller.finalize", "FinalizeTransaction.substate.SubstateError", "finalization.result.error",
            "the returned EvmException carries SubstateError"),
        SourceMapping(members, "caller.finalize", "FinalizeTransaction.substate.Output", "terminal.output",
            "the same output is forwarded on success and ShouldRevert failure; non-revert failure forwards empty"),
        Mapping(members, "tracer.isTracingReceipt", "BlockReceiptsTracer", "FinalizeTransaction.receiptGate", "exact base BlockReceiptsTracer.IsTracingReceipt is true; the normally returning caller gate is discharged"),
        Mapping(members, "snapshot.take", "BlockReceiptsTracer", "snapshotPosition", "receipt count"),
        Mapping(members, "snapshot.restore", "BlockReceiptsTracer", "prefixState", "retained receipt and block-gas prefix"),
    ];

    private static MappingDescriptor Mapping(
        BoundMember[] members,
        string memberId,
        string sourcePrefix,
        string target,
        string semantics)
    {
        BoundMember member = Get(members, memberId);
        string source = member.Identity.Member == sourcePrefix ? sourcePrefix :
            $"{sourcePrefix}.{member.Identity.Member}";
        return new(source, target, MappingSemantics(member, target, semantics),
            ExtractMappingExpression(member.SemanticModel, member.Node, target));
    }

    private static MappingDescriptor SourceMapping(
        BoundMember[] members,
        string memberId,
        string source,
        string target,
        string fallback)
    {
        BoundMember member = Get(members, memberId);
        SemanticExpression? expression = ExtractMappingExpression(member.SemanticModel, member.Node, target);
        return new(source, target, expression is null ? fallback : $"source:{expression.Kind}:{expression.Text}", expression);
    }

    private static string MappingSemantics(BoundMember member, string target, string fallback)
    {
        SemanticExpression? expression = ExtractMappingExpression(member.SemanticModel, member.Node, target);
        return expression is null ? fallback : $"source:{expression.Kind}:{expression.Text}";
    }

    private static SemanticExpression? ExtractMappingExpression(SemanticModel model, SyntaxNode node, string target)
    {
        if (node is PropertyDeclarationSyntax property && property.ExpressionBody is not null)
        {
            return ToSemanticExpression(model, property.ExpressionBody.Expression);
        }

        if (node is MethodDeclarationSyntax method)
        {
            string? receiptField = target.StartsWith("receipt.", StringComparison.Ordinal)
                ? target["receipt.".Length..].Split('/')[0]
                : null;
            if (receiptField is not null)
            {
                AssignmentExpressionSyntax? assignment = method.DescendantNodes()
                    .OfType<AssignmentExpressionSyntax>()
                    .Where(candidate => candidate.Left.ToString() == receiptField ||
                        candidate.Left.ToString().EndsWith($".{receiptField}", StringComparison.Ordinal))
                    .LastOrDefault();
                if (assignment is not null) return ToSemanticExpression(model, assignment.Right);

                ObjectCreationExpressionSyntax? creation = method.DescendantNodes()
                    .OfType<ObjectCreationExpressionSyntax>()
                    .FirstOrDefault(candidate => candidate.Initializer is not null);
                AssignmentExpressionSyntax? initializer = creation?.Initializer?.Expressions
                    .OfType<AssignmentExpressionSyntax>()
                    .FirstOrDefault(candidate => candidate.Left.ToString() == receiptField);
                if (initializer is not null) return ToSemanticExpression(model, initializer.Right);
            }

            if (target == "terminal.output")
            {
                VariableDeclaratorSyntax? output = method.DescendantNodes()
                    .OfType<VariableDeclaratorSyntax>()
                    .FirstOrDefault(candidate => candidate.Identifier.ValueText == "output");
                return ToSemanticExpression(model, output?.Initializer?.Value);
            }

            if (target == "terminal.error")
            {
                VariableDeclaratorSyntax? initializer = method.DescendantNodes()
                    .OfType<VariableDeclaratorSyntax>()
                    .FirstOrDefault(candidate => candidate.Identifier.ValueText == "error" &&
                        candidate.Initializer is not null);
                if (initializer?.Initializer is EqualsValueClauseSyntax value)
                {
                    return ToSemanticExpression(model, value.Value);
                }

                AssignmentExpressionSyntax? error = method.DescendantNodes()
                    .OfType<AssignmentExpressionSyntax>()
                    .FirstOrDefault(candidate => candidate.Left.ToString() == "error");
                return ToSemanticExpression(model, error?.Right);
            }

            if (target == "finalization.result")
            {
                ReturnStatementSyntax? result = method.DescendantNodes().OfType<ReturnStatementSyntax>().LastOrDefault();
                return ToSemanticExpression(model, result?.Expression);
            }

            if (target == "finalization.result.error")
            {
                ReturnStatementSyntax? result = method.DescendantNodes().OfType<ReturnStatementSyntax>().LastOrDefault();
                if (result?.Expression is not ConditionalExpressionSyntax conditional)
                {
                    return null;
                }

                InvocationExpressionSyntax? exception = conditional.WhenTrue.DescendantNodesAndSelf()
                    .OfType<InvocationExpressionSyntax>()
                    .FirstOrDefault(invocation => invocation.Expression.ToString() == "TransactionResult.EvmException");
                return exception?.ArgumentList.Arguments.Count == 2
                    ? ToSemanticExpression(model, exception.ArgumentList.Arguments[1].Expression)
                    : null;
            }
        }

        return null;
    }

    private static SourceReadResult ReadSources(string root, string referenceRoot)
    {
        SourceFile[] sources = new SourceFile[SourcePaths.Length];
        for (int index = 0; index < SourcePaths.Length; index++)
        {
            sources[index] = Read(root, SourcePaths[index]);
        }

        SourceFile[] bindingSources = new SourceFile[BindingSourcePaths.Length];
        for (int index = 0; index < BindingSourcePaths.Length; index++)
        {
            SourceFile bindingSource = Read(root, BindingSourcePaths[index]);
            if (!ExpectedBindingSourceHashes.TryGetValue(bindingSource.RelativePath, out string? expectedHash) ||
                bindingSource.Sha256 != expectedHash)
            {
                throw new ExtractionException($"The parsed semantic binding source fingerprint changed: {bindingSource.RelativePath}.");
            }
            bindingSources[index] = bindingSource;
        }

        CompilerReferenceClosure closure = BuildMetadataReferences(referenceRoot);
        SourceFile[] compilerSupport = bindingSources
            .Where(source => CompilerSupportSourcePaths.Contains(source.RelativePath, StringComparer.Ordinal))
            .ToArray();
        for (int index = 0; index < sources.Length; index++)
        {
            // Related production declarations compile together so internal access,
            // partial methods and extension receivers retain their actual identities.
            SourceFile source = sources[index];
            SyntaxTree[] trees = source.RelativePath is TransactionPath or TransactionExtensionsPath
                ? [.. sources.Where(static candidate => candidate.RelativePath is TransactionPath or TransactionExtensionsPath)
                    .Select(static candidate => candidate.Tree), .. compilerSupport.Select(static support => support.Tree)]
                : source.RelativePath is TracerPath or AccountingKernelPath
                    ? sources.Where(static candidate => candidate.RelativePath is TracerPath or AccountingKernelPath)
                        .Select(static candidate => candidate.Tree).ToArray()
                : source.RelativePath == BlockPath
                    ? [source.Tree, bindingSources.Single(static candidate => candidate.RelativePath == BlockBodySupportPath).Tree]
                : source.RelativePath == CallerPath
                    ? [source.Tree, .. bindingSources.Where(static candidate => CallerSupportSourcePaths.Contains(candidate.RelativePath))
                        .Select(static support => support.RelativePath == VmStaticsSupportPath ? SelectVmStatics(support) : support.Tree)]
                : source.RelativePath is EthereumGasPolicyPath or TransactionGasInitializationKernelPath
                    ? [.. sources.Where(static candidate => candidate.RelativePath is EthereumGasPolicyPath or TransactionGasInitializationKernelPath)
                        .Select(static candidate => candidate.Tree),
                        .. bindingSources.Where(static candidate => GasPolicySupportSourcePaths.Contains(candidate.RelativePath))
                            .Select(static candidate => candidate.Tree)]
                : [source.Tree];
            CSharpCompilation compilation = CSharpCompilation.Create(
                CompilerAssemblyName(source.RelativePath),
                trees,
                closure.References,
                new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    nullableContextOptions: NullableContextOptions.Enable,
                    allowUnsafe: true,
                    optimizationLevel: OptimizationLevel.Debug,
                    metadataImportOptions: MetadataImportOptions.All));
            ValidateCompilation(compilation);
            sources[index] = source with { SemanticModel = compilation.GetSemanticModel(source.Tree) };
        }
        return new(sources, closure.Identities);
    }

    private static SyntaxTree SelectVmStatics(SourceFile source)
    {
        FileScopedNamespaceDeclarationSyntax owner = source.Root.Members.OfType<FileScopedNamespaceDeclarationSyntax>().Single();
        ClassDeclarationSyntax declaration = owner.Members.OfType<ClassDeclarationSyntax>()
            .Single(static member => member.Identifier.ValueText == "VirtualMachineStatics");
        CompilationUnitSyntax selected = source.Root.WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(
            owner.WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(declaration))));
        return CSharpSyntaxTree.Create(selected, ParseOptions, source.Tree.FilePath);
    }

    private static string CompilerAssemblyName(string sourcePath) =>
        sourcePath is TracerPath or AccountingKernelPath
            ? "Nethermind.Blockchain"
            : sourcePath is TransactionPath or TransactionExtensionsPath or BlockPath or HeaderPath or ReceiptPath
                ? "Nethermind.Core"
                : "Nethermind.Evm";

    private static SourceFile Read(string root, string relativePath)
    {
        string path = Path.GetFullPath(Path.Combine(root, relativePath));
        EnsureWithin(root, path);
        if (!File.Exists(path))
        {
            throw new ExtractionException($"Required source does not exist: {relativePath}.");
        }

        byte[] bytes = File.ReadAllBytes(path);
        string text;
        try
        {
            text = Utf8WithoutBom.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ExtractionException($"Source {relativePath} is not strict UTF-8: {exception.Message}");
        }

        SyntaxTree tree = CSharpSyntaxTree.ParseText(text, ParseOptions, path);
        Diagnostic[] errors = tree.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0)
        {
            throw new ExtractionException($"Source {relativePath} does not parse as C# 14: {errors[0]}.");
        }

        return new(relativePath, text, tree, tree.GetCompilationUnitRoot(), null, Sha256(bytes));
    }

    private static BoundMember[] BindMembers(SourceFile[] sources, bool enforceCanonicalPins = true)
    {
        BoundMember[] result = new BoundMember[MemberExpectations.Length];
        for (int index = 0; index < MemberExpectations.Length; index++)
        {
            MemberExpectation expectation = MemberExpectations[index];
            SourceFile source = sources.Single(candidate => candidate.RelativePath == expectation.SourcePath);
            IEnumerable<SyntaxNode> candidates = source.Root.DescendantNodes()
                .Where(member => IsExpectedKind(member, expectation.Kind))
                .Where(member => member.Ancestors().OfType<TypeDeclarationSyntax>()
                    .Any(type => type.Identifier.ValueText == expectation.ContainingType))
                .Where(member => MemberName(member) == expectation.Member)
                .Where(member => ParameterCount(member) == expectation.ParameterCount);

            if (expectation.Signature is not null)
            {
                candidates = candidates.Where(member => CanonicalSignature(member, expectation.Member) == expectation.Signature);
            }

            SyntaxNode[] matches = candidates.ToArray();
            if (matches.Length != 1)
            {
                throw new ExtractionException($"Expected one exact {expectation.Kind} member {expectation.ContainingType}.{expectation.Member} in {expectation.SourcePath}, found {matches.Length}.");
            }

            SyntaxNode member = matches[0];
            SyntaxTokenList modifiers = Modifiers(member);
            foreach (string required in expectation.RequiredModifiers)
            {
                if (!modifiers.Any(modifier => modifier.ValueText == required))
                {
                    throw new ExtractionException($"Member {expectation.Id} lost required modifier '{required}'.");
                }
            }

            ISymbol symbol = DeclaredSymbol(source.SemanticModel!, member, expectation.Member)
                ?? throw new ExtractionException($"Roslyn did not bind member {expectation.Id}.");
            ValidateMemberBinding(source.SemanticModel!, member, symbol, expectation.Id);
            string canonical = member.WithoutTrivia().NormalizeWhitespace().ToFullString();
            string canonicalHash = Sha256(Encoding.UTF8.GetBytes(canonical));
            if (enforceCanonicalPins && ExpectedCanonicalSyntaxHashes.TryGetValue(expectation.Id, out string? expectedCanonicalHash) &&
                canonicalHash != expectedCanonicalHash)
            {
                throw new ExtractionException($"Member {expectation.Id} body changed. Expected canonical SHA-256 {expectedCanonicalHash}, got {canonicalHash}.");
            }
            string[] parameterTypes = symbol is IMethodSymbol methodSymbol
                ? methodSymbol.Parameters.Select(static parameter => parameter.Type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)).ToArray()
                : [];
            string semanticType = symbol switch
            {
                IMethodSymbol method => method.ReturnType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                IPropertySymbol property => property.Type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                IFieldSymbol field => field.Type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                _ => symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            };
            MemberIdentity identity = new(
                expectation.Id,
                expectation.SourcePath,
                expectation.ContainingType,
                expectation.Member,
                expectation.Kind,
                expectation.Signature!,
                parameterTypes,
                semanticType,
                symbol.Kind.ToString(),
                canonicalHash);
            result[index] = new(identity, member, canonical, symbol, source.SemanticModel!);
        }
        return result;
    }

    private static void ValidateMemberBinding(SemanticModel model, SyntaxNode member, ISymbol symbol, string memberId)
    {
        ValidateSymbol(symbol, memberId, "declared member");
        ValidateSyntaxBindings(model, member, memberId);

        switch (member)
        {
            case MethodDeclarationSyntax method:
            {
                IMethodSymbol methodSymbol = symbol as IMethodSymbol
                    ?? throw new ExtractionException($"Member {memberId} did not bind as a method symbol.");
                ValidateType(methodSymbol.ContainingType, memberId, "method receiver type");
                ValidateType(methodSymbol.ReturnType, memberId, "method return type");
                foreach (IParameterSymbol parameter in methodSymbol.Parameters)
                {
                    ValidateSymbol(parameter, memberId, $"parameter '{parameter.Name}'");
                    ValidateType(parameter.Type, memberId, $"parameter '{parameter.Name}' type");
                }

                SyntaxNode body = method.Body ?? (SyntaxNode?)method.ExpressionBody?.Expression
                    ?? throw new ExtractionException($"Member {memberId} has no bindable method body.");
                IOperation operation = model.GetOperation(body)
                    ?? throw new ExtractionException($"Member {memberId} has no semantic operation.");
                ValidateOperationTree(operation, memberId);
                break;
            }
            case PropertyDeclarationSyntax property when property.ExpressionBody is not null:
            {
                IOperation operation = model.GetOperation(property.ExpressionBody.Expression)
                    ?? throw new ExtractionException($"Member {memberId} has no semantic property operation.");
                ValidateOperationTree(operation, memberId);
                break;
            }
            case FieldDeclarationSyntax field:
                foreach (VariableDeclaratorSyntax variable in field.Declaration.Variables)
                {
                    if (variable.Initializer is not null && model.GetOperation(variable.Initializer.Value) is IOperation operation)
                    {
                        ValidateOperationTree(operation, memberId);
                    }
                }
                break;
        }
    }

    private static void ValidateSyntaxBindings(SemanticModel model, SyntaxNode member, string memberId)
    {
        foreach (SyntaxNode node in member.DescendantNodesAndSelf())
        {
            SymbolInfo symbolInfo = model.GetSymbolInfo(node);
            if (symbolInfo.CandidateReason != CandidateReason.None || symbolInfo.CandidateSymbols.Length != 0)
            {
                throw new ExtractionException(
                    $"Member {memberId} has a candidate, ambiguous, or invalid symbol at '{Canonical(node)}'.");
            }
            if (symbolInfo.Symbol is not null)
            {
                ValidateSymbol(symbolInfo.Symbol, memberId, $"symbol at '{Canonical(node)}'");
            }

            // Roslyn exposes the empty rank specifier in an array type (the
            // `[]` in `byte[]`) as an OmittedArraySizeExpressionSyntax node.
            // That node is not a type-bearing expression; asking for its
            // TypeInfo returns the sentinel error type `?` even when the
            // enclosing array type is fully bound.  Validate the enclosing
            // type and all operation types, but do not mistake this parser
            // placeholder for an unresolved user type.
            if (node is not OmittedArraySizeExpressionSyntax)
            {
                Microsoft.CodeAnalysis.TypeInfo typeInfo = model.GetTypeInfo(node);
                ValidateType(typeInfo.Type, memberId, $"type ({node.Kind()}) at '{Canonical(node)}'");
                ValidateType(typeInfo.ConvertedType, memberId, $"converted type ({node.Kind()}) at '{Canonical(node)}'");
            }
        }
    }

    private static void ValidateOperationTree(IOperation operation, string memberId)
    {
        if (operation is IInvalidOperation || operation.Kind == OperationKind.Invalid)
        {
            throw new ExtractionException(
                $"Member {memberId} contains an invalid or unresolved operation at '{Canonical(operation.Syntax)}'.");
        }

        ValidateType(operation.Type, memberId, $"operation type at '{Canonical(operation.Syntax)}'");
        switch (operation)
        {
            case IInvocationOperation invocation:
                ValidateSymbol(invocation.TargetMethod, memberId, $"invocation '{Canonical(invocation.Syntax)}' target");
                ValidateType(invocation.Instance?.Type, memberId, $"invocation '{Canonical(invocation.Syntax)}' receiver type");
                foreach (IArgumentOperation argument in invocation.Arguments)
                {
                    if (argument.Parameter is null)
                    {
                        throw new ExtractionException(
                            $"Member {memberId} contains an unresolved invocation parameter at '{Canonical(argument.Syntax)}'.");
                    }

                    ValidateSymbol(argument.Parameter, memberId, $"invocation '{Canonical(argument.Syntax)}' parameter");
                    ValidateType(argument.Parameter.Type, memberId, $"invocation '{Canonical(argument.Syntax)}' parameter type");
                }
                break;
            case IPropertyReferenceOperation property:
                ValidateSymbol(property.Property, memberId, $"property '{Canonical(property.Syntax)}'");
                ValidateType(property.Instance?.Type, memberId, $"property '{Canonical(property.Syntax)}' receiver type");
                break;
            case IFieldReferenceOperation field:
                ValidateSymbol(field.Field, memberId, $"field '{Canonical(field.Syntax)}'");
                ValidateType(field.Instance?.Type, memberId, $"field '{Canonical(field.Syntax)}' receiver type");
                break;
            case IEventReferenceOperation @event:
                ValidateSymbol(@event.Event, memberId, $"event '{Canonical(@event.Syntax)}'");
                ValidateType(@event.Instance?.Type, memberId, $"event '{Canonical(@event.Syntax)}' receiver type");
                break;
            case IMethodReferenceOperation methodReference:
                ValidateSymbol(methodReference.Method, memberId, $"method reference '{Canonical(methodReference.Syntax)}'");
                ValidateType(methodReference.Instance?.Type, memberId, $"method reference '{Canonical(methodReference.Syntax)}' receiver type");
                break;
            case IObjectCreationOperation creation when creation.Constructor is not null:
                ValidateSymbol(creation.Constructor, memberId, $"constructor '{Canonical(creation.Syntax)}'");
                break;
            case IArgumentOperation argument when argument.Parameter is not null:
                ValidateSymbol(argument.Parameter, memberId, $"argument '{Canonical(argument.Syntax)}' parameter");
                ValidateType(argument.Parameter.Type, memberId, $"argument '{Canonical(argument.Syntax)}' parameter type");
                break;
            case IArgumentOperation argument:
                throw new ExtractionException(
                    $"Member {memberId} contains an unresolved argument parameter at '{Canonical(argument.Syntax)}'.");
            case IConversionOperation conversion when conversion.OperatorMethod is not null:
                ValidateSymbol(conversion.OperatorMethod, memberId, $"conversion '{Canonical(conversion.Syntax)}' operator");
                break;
            case IBinaryOperation binary when binary.OperatorMethod is not null:
                ValidateSymbol(binary.OperatorMethod, memberId, $"binary operator '{Canonical(binary.Syntax)}'");
                break;
            case IUnaryOperation unary when unary.OperatorMethod is not null:
                ValidateSymbol(unary.OperatorMethod, memberId, $"unary operator '{Canonical(unary.Syntax)}'");
                break;
            case ILocalReferenceOperation local:
                ValidateSymbol(local.Local, memberId, $"local '{local.Local.Name}'");
                ValidateType(local.Local.Type, memberId, $"local '{local.Local.Name}' type");
                break;
            case IParameterReferenceOperation parameter:
                ValidateSymbol(parameter.Parameter, memberId, $"parameter '{parameter.Parameter.Name}' reference");
                ValidateType(parameter.Parameter.Type, memberId, $"parameter '{parameter.Parameter.Name}' type");
                break;
            case ITypeOfOperation typeOf:
                ValidateType(typeOf.TypeOperand, memberId, $"typeof operand at '{Canonical(typeOf.Syntax)}'");
                break;
        }

        foreach (IOperation child in operation.ChildOperations)
        {
            ValidateOperationTree(child, memberId);
        }
    }

    private static void ValidateSymbol(ISymbol? symbol, string memberId, string role)
    {
        if (symbol is null)
        {
            throw new ExtractionException($"Member {memberId} has an unresolved {role}.");
        }

        if (HasErrorSymbol(symbol))
        {
            throw new ExtractionException($"Member {memberId} has an error symbol in its {role}: '{symbol}'.");
        }
    }

    private static void ValidateType(ITypeSymbol? type, string memberId, string role)
    {
        if (ContainsErrorType(type))
        {
            throw new ExtractionException($"Member {memberId} has an error type in its {role}: '{type}'.");
        }
    }

    private static void ValidateSourceSemantics(SourceFile[] sources, BoundMember[] members)
    {
        foreach (OperationDescriptor operation in Operations(members))
            ReceiptTerminalFoldLeanEmitter.ValidateControlAndEffects(operation);
        foreach (BoundMember member in members)
        {
            if (member.Node is MethodDeclarationSyntax method && method.Identifier.ValueText is
                "CombineBlockGas" or "Combine" or "CalculateEffectiveGasPrice" or "Accumulate" or "FromTotals")
            {
                SemanticStep[] steps = ExtractSemanticSteps(member.SemanticModel, member.Node);
                ReceiptTerminalFoldControlFlow.Validate(method.Identifier.ValueText, steps);
                ReceiptTerminalFoldEffects.Validate(method.Identifier.ValueText, steps,
                    ExtractSemanticOperation(member.SemanticModel, member.Node));
            }
        }
        RequireOrdered(Get(sources, TracerPath).Text,
            ["public class BlockReceiptsTracer(bool parallel = false)", "public bool IsTracingReceipt"],
            "sequential tracer declaration");
        Require(Get(sources, TracerPath).Text, "public bool IsTracingReceipt => true;", "exact-base receipt gate");
        RequireOrdered(Get(sources, TransactionPath).Text,
            ["public bool IsContractCreation => To is null;", "public bool IsMessageCall => To is not null;"],
            "transaction creation/message relation");
        RequireOrdered(Get(sources, StatusCodePath).Text,
            ["public const byte Failure = 0", "public const byte Success = 1"],
            "terminal status constants");
        RequireOrdered(Get(sources, EthereumGasPolicyPath).Text,
            ["public static ulong CombineBlockGas(ulong blockExecutionGas, ulong blockStateGas)",
                "BlockGasAccountingKernel.Combine(blockExecutionGas, blockStateGas)"],
            "live block-gas combine adapter");
        RequireOrdered(Get(sources, TransactionGasInitializationKernelPath).Text,
            ["public static ulong Combine(ulong blockExecutionGas, ulong blockStateGas)",
                "Math.Max(blockExecutionGas, blockStateGas)"],
            "live block-gas combine kernel");
        Require(Get(sources, TracerPath).Text,
            "protected virtual TxReceipt BuildReceipt",
            "base receipt builder declaration");
        RequireOrdered(Get(members, "terminal.success").CanonicalSyntax,
            ["_txReceipts.Add(BuildReceipt", "otherTxTracer.MarkAsSuccess", "_currentTxTracer.IsTracingReceipt", "_currentTxTracer.MarkAsSuccess"],
            "success terminal order");
        RequireOrdered(Get(members, "terminal.failure").CanonicalSyntax,
            ["_txReceipts.Add(BuildFailedReceipt", "otherTxTracer.MarkAsFailed", "_currentTxTracer.IsTracingReceipt", "_currentTxTracer.MarkAsFailed"],
            "failure terminal order");
        RequireOrdered(Get(members, "receipt.buildFailed").CanonicalSyntax,
            ["BuildReceipt(recipient, gasSpent, StatusCode.Failure, [], stateRoot)", "receipt.Error = error"],
            "failed receipt construction order");
        RequireOrdered(Get(members, "receipt.build").CanonicalSyntax,
            ["UpdateCumulativeGasTracking", "TxReceipt txReceipt = new()", "Logs = logEntries", "GasUsedTotal = cumulativeReceiptGas", "Recipient = transaction.IsContractCreation ? null : recipient", "ContractAddress = transaction.IsContractCreation ? recipient : null", "if (gasConsumed.BlockGas > 0)", "BlockGasUsed = gasConsumed.EffectiveBlockGas", "ExecutionGasUsed = gasConsumed.OperationGas", "if (gasConsumed.BlockStateGas > 0)", "StorageGasUsed = gasConsumed.BlockStateGas"],
            "receipt projection order");
        if (Get(members, "receipt.build").CanonicalSyntax.Contains("ReturnValue", StringComparison.Ordinal))
        {
            throw new ExtractionException("BuildReceipt must not assign TxReceipt.ReturnValue.");
        }
        RequireOrdered(Get(members, "gas.update").CanonicalSyntax,
            ["BlockReceiptGasAccountingKernel.Accumulate", "_cumulativeBlockGasPerTx.Add", "if (!parallel)", "Block.Header.GasUsed", "_cumulativeReceiptGas = accounting.CumulativeReceiptGas", "return _cumulativeReceiptGas"],
            "cumulative gas update order");
        RequireOrdered(Get(members, "snapshot.restore").CanonicalSyntax,
            ["_txReceipts.RemoveRange(snapshot", "_cumulativeBlockGasPerTx.RemoveRange(snapshot", "BlockReceiptGasAccountingKernel.FromTotals", "Block.Header.GasUsed", "_cumulativeReceiptGas = accounting.CumulativeReceiptGas"],
            "normal restore order");
        RequireOrdered(Get(members, "caller.finalize").CanonicalSyntax,
            ["if (tracer.IsTracingReceipt)", "if (statusCode == StatusCode.Failure)", "substate.ShouldRevert", "substate.Error", "substate.EvmExceptionType", "tracer.MarkAsFailed", "else", "substate.Logs.Count", "tracer.MarkAsSuccess", "return substate.EvmExceptionType", "TransactionResult.EvmException", "substate.SubstateError", "TransactionResult.Ok"],
            "caller terminal order");

        string caller = Get(sources, CallerPath).Text;
        RequireOrdered(Get(members, "caller.finalize").CanonicalSyntax,
            ["WorldState.ResetTransient();", "if (opts == ExecutionOptions.BuildUp && spec.IsEip8037Enabled)",
                "WorldState.ReapEmptyAccounts();", "if (tracer.IsTracingReceipt)"],
            "BuildUp/EIP-8037 account-reap ordering");
        RequireOrdered(caller,
            ["error = substate.EvmExceptionType.FastToString()", "tracer.MarkAsFailed(executingAccount, spentGas, output, error, stateRoot)"],
            "error-source precedence");
        Require(caller, "byte[] output = substate.ShouldRevert ? substate.Output.AsReadOnlyArray() : [];", "failure output guard");
        Require(caller, "tracer.MarkAsSuccess(executingAccount, spentGas, substate.Output.AsReadOnlyArray(), logs, stateRoot);", "success output forwarding");
        Require(Get(sources, TracerPath).Text, "_cumulativeBlockGasPerTx.Count > 0 ? _cumulativeBlockGasPerTx[^1] : (0, 0)", "zero previous block totals");
        Require(Get(sources, TracerPath).Text, "_txReceipts.Count > 0 ? _txReceipts[^1].GasUsedTotal : 0", "restore receipt counter");
        Require(Get(sources, AccountingKernelPath).Text, "unchecked(previousExecutionGas + transactionExecutionGas)", "unchecked execution addition");
        Require(Get(sources, AccountingKernelPath).Text, "unchecked(previousStateGas + transactionStateGas)", "unchecked state addition");
        Require(Get(sources, AccountingKernelPath).Text, "unchecked(previousReceiptGas + transactionPaidGas)", "unchecked receipt addition");
    }

    private static SourceManifest BuildManifest(
        string root,
        SourceFile[] sources,
        BoundMember[] members,
        CompilerReferenceIdentity[] compilerReferences,
        byte[] irBytes,
        byte[] leanBytes) =>
        new(
            SchemaVersion,
            ExtractorVersion,
            CompilerVersion,
            LanguageVersion.CSharp14.ToDisplayString(),
            Kernel,
            sources.Select(static source => new SourceIdentity(source.RelativePath, source.Sha256)).ToArray(),
            BindingSourceIdentities(root),
            compilerReferences,
            compilerReferences.Length,
            CompilerReferenceAggregateSha256(compilerReferences),
            members.Select(static member => member.Identity).ToArray(),
            new(IrFileName, Sha256(irBytes)),
            new(Normalize(DefaultLeanRelativePath), Sha256(leanBytes)),
            BuildKernelIdentity(),
            CombinedHash(sources
                .Select(static source => new SourceIdentity(source.RelativePath, source.Sha256))
            .Concat(BindingSourceIdentities(root))
                .Select(static source => $"{source.Path}\0{source.Sha256}")),
            CombinedHash(members.Select(static member =>
                $"{member.Identity.SourcePath}\0{member.Identity.ContainingType}\0{member.Identity.Member}\0{member.Identity.Kind}\0{member.Identity.Signature}\0{member.Identity.CanonicalSha256}")),
            SemanticBindings);

    private static SourceIdentity[] BindingSourceIdentities(string root) =>
        BindingSourcePaths.Select(relativePath =>
        {
            string path = Path.GetFullPath(Path.Combine(root, relativePath));
            EnsureWithin(root, path);
            if (!File.Exists(path))
            {
                throw new ExtractionException($"Required semantic binding source does not exist: {relativePath}.");
            }

            return new SourceIdentity(relativePath, Sha256(File.ReadAllBytes(path)));
        }).ToArray();

    private static KernelIdentity BuildKernelIdentity() => new(
        "Nethermind.Blockchain.Tracing.BlockReceiptGasAccountingKernel",
        AccountingKernelPath,
        AccountingKernelSourceSha256,
        AccountingKernelIrPath,
        AccountingKernelIrSha256,
        AccountingKernelGeneratedPath,
        AccountingKernelGeneratedSha256,
        AccountingKernelRefinementPath,
        AccountingKernelRefinementSha256,
        AccountingKernelManifestPath,
        AccountingKernelManifestSha256,
        LeanDependencyPins);

    private static void ValidateDelegatedKernel(string root)
    {
        ValidateLeanDependencies(root);
        string source = Path.GetFullPath(Path.Combine(root, AccountingKernelPath));
        RequireHash(source, AccountingKernelSourceSha256, "delegated accounting source");
        RequireHash(Path.Combine(root, AccountingKernelIrPath), AccountingKernelIrSha256, "delegated accounting IR");
        RequireHash(Path.Combine(root, AccountingKernelGeneratedPath), AccountingKernelGeneratedSha256, "delegated accounting Lean");
        RequireHash(Path.Combine(root, AccountingKernelRefinementPath), AccountingKernelRefinementSha256, "delegated accounting refinement");
        RequireHash(Path.Combine(root, AccountingKernelManifestPath), AccountingKernelManifestSha256, "delegated accounting manifest");
        string generated = File.ReadAllText(Path.Combine(root, AccountingKernelGeneratedPath), Utf8WithoutBom);
        Require(generated, "namespace Eip803x.Generated.BlockReceiptGasAccountingKernel", "delegated generated kernel namespace");
        Require(generated, "def accumulate", "delegated generated accumulate");
        string refinement = File.ReadAllText(Path.Combine(root, AccountingKernelRefinementPath), Utf8WithoutBom);
        Require(refinement, "theorem generatedAccumulate_refines_spec", "delegated no-wrap theorem");
        Require(refinement, "theorem generatedAccumulate_eq_wrapping_transcription", "delegated wrapping transcription");
    }

    private static void ValidateKernelIdentity(KernelIdentity identity)
    {
        if (identity is null || identity.Root != "Nethermind.Blockchain.Tracing.BlockReceiptGasAccountingKernel" ||
            identity.SourcePath != AccountingKernelPath || identity.SourceSha256 != AccountingKernelSourceSha256 ||
            identity.IrPath != AccountingKernelIrPath || identity.IrSha256 != AccountingKernelIrSha256 ||
            identity.GeneratedLeanPath != AccountingKernelGeneratedPath || identity.GeneratedLeanSha256 != AccountingKernelGeneratedSha256 ||
            identity.RefinementPath != AccountingKernelRefinementPath || identity.RefinementSha256 != AccountingKernelRefinementSha256 ||
            identity.ManifestPath != AccountingKernelManifestPath || identity.ManifestSha256 != AccountingKernelManifestSha256 ||
            identity.LeanDependencies is null || !identity.LeanDependencies.SequenceEqual(LeanDependencyPins))
        {
            throw new ExtractionException("The receipt-terminal package does not identity-pin the existing accounting kernel and refinement.");
        }
    }

    internal static void ValidateLeanDependencies(string root)
    {
        foreach (SourceIdentity dependency in LeanDependencyPins)
        {
            RequireHash(Path.Combine(root, dependency.Path), dependency.Sha256, "delegated Lean dependency");
        }
    }

    private static void ValidateDocument(IrDocument document, BoundMember[]? expectedMembers)
    {
        if (document is null || document.SchemaVersion != SchemaVersion || document.ExtractorVersion != ExtractorVersion ||
            document.Scope is null || document.AccountingKernel is null ||
            document.Inputs is null || document.Operations is null || document.Snapshot is null ||
            document.Mappings is null || document.Lowering is null || document.SemanticBindings is null ||
            document.Scope.Exclusions is null || document.Inputs.GasFields is null ||
            document.Inputs.ReceiptFields is null || document.Inputs.TransactionFields is null ||
            document.Inputs.BlockFields is null || document.Inputs.HeaderFields is null ||
            document.Inputs.FinalizeFields is null || document.Operations.Any(static operation => operation is null) ||
            document.Mappings.Any(static mapping => mapping is null) ||
            document.Snapshot.RestoredSurfaces is null || document.Lowering.EffectiveBlockGas is null ||
            document.Lowering.StatusCode is null || document.Lowering.StatusCode.Failure is null ||
            document.Lowering.StatusCode.Success is null || document.Lowering.Result is null ||
            document.Lowering.Result.Condition is null || document.Lowering.Result.ExceptionType is null ||
            document.Lowering.Result.ExceptionError is null || document.Lowering.Result.OkResult is null ||
            document.Lowering.ReceiptFields is null || document.Lowering.Finalization is null ||
            document.Lowering.ReceiptFields.Any(static binding => binding is null) ||
            document.Lowering.Finalization.Any(static binding => binding is null) ||
            document.Mappings.Any(static mapping => mapping is null || string.IsNullOrWhiteSpace(mapping.Source) ||
                string.IsNullOrWhiteSpace(mapping.Target) || string.IsNullOrWhiteSpace(mapping.Semantics)) ||
            document.Operations.Any(static operation => operation is null || operation.Inputs is null || operation.Outputs is null ||
                operation.OrderedEffects is null || operation.Guards is null || operation.ForwardedOrProjected is null ||
                operation.Exclusions is null || operation.Steps is null || operation.Body is null))
        {
            throw new ExtractionException("The receipt-terminal IR has a null typed section.");
        }

        ValidateSemanticExpressionShape(document.Lowering.EffectiveBlockGas, "effective block gas lowering");
        ValidateSemanticExpressionShape(document.Lowering.StatusCode.Failure, "failure status lowering");
        ValidateSemanticExpressionShape(document.Lowering.StatusCode.Success, "success status lowering");
        ValidateSemanticExpressionShape(document.Lowering.Result.Condition, "result condition lowering");
        ValidateSemanticExpressionShape(document.Lowering.Result.ExceptionType, "result exception-type lowering");
        ValidateSemanticExpressionShape(document.Lowering.Result.ExceptionError, "result error lowering");
        ValidateSemanticExpressionShape(document.Lowering.Result.OkResult, "result ok lowering");
        foreach (LoweringBinding binding in document.Lowering.ReceiptFields.Concat(document.Lowering.Finalization))
        {
            ValidateSemanticExpressionShape(binding.Expression, $"lowering {binding.Target}");
            ValidateSemanticExpressionShape(binding.Guard, $"lowering {binding.Target} guard");
        }

        ValidateLowering(document.Lowering);

        if (expectedMembers is not null)
        {
            IrDocument expected = BuildDocument(expectedMembers);
            if (!Serialize(document).AsSpan().SequenceEqual(Serialize(expected)))
            {
                throw new ExtractionException("The receipt-terminal IR schema, operation order, scope, or typed mappings changed.");
            }
        }

        if (document.Scope.Mode != "standard-mainnet sequential terminal path" ||
            document.Scope.ExecutionContext != "exact base BlockReceiptsTracer(parallel=false), with no BuildReceipt override, called from a normally returning FinalizeTransaction" ||
            !document.Scope.Exclusions.SequenceEqual(
                [
                    "parallel worker callbacks and BAL execution",
                    "invalid snapshot values, unsynchronized receipt/gas histories, and SetReceipt states",
                    "receipt Bloom/RLP/trie/root computation and receipt/database persistence",
                    "VM execution, gas production, world-state and state-root correctness",
                    "nested tracer totality, DI wiring, and CLR allocation behavior",
                ], StringComparer.Ordinal))
        {
            throw new ExtractionException("The receipt-terminal IR scope is not admitted.");
        }

        if (document.AccountingKernel.KernelType != "Nethermind.Blockchain.Tracing.BlockReceiptGasAccountingKernel" ||
            document.AccountingKernel.AccumulateSignature != "Accumulate(ulong,ulong,ulong,ulong,ulong,ulong)" ||
            document.AccountingKernel.FromTotalsSignature != "FromTotals(ulong,ulong,ulong)" ||
            document.AccountingKernel.GeneratedLeanPath != AccountingKernelGeneratedPath ||
            document.AccountingKernel.RefinementPath != AccountingKernelRefinementPath ||
            document.AccountingKernel.ArithmeticContract != "exact unchecked ulong addition; no-wrap Nat corollary")
        {
            throw new ExtractionException("The receipt-terminal accounting dependency is not admitted.");
        }

        if (!document.Inputs.GasFields.SequenceEqual(
                ["SpentGas", "OperationGas", "BlockGas", "BlockStateGas", "MaxUsedGas", "GasRefund", "EffectiveBlockGas", "EffectiveMaxUsedGas"], StringComparer.Ordinal) ||
            !document.Inputs.ReceiptFields.SequenceEqual(
                ["Logs", "TxType", "GasUsedTotal", "StatusCode", "Recipient", "BlockHash", "BlockNumber", "Index", "GasUsed", "EffectiveGasPrice", "Sender", "ContractAddress", "TxHash", "PostTransactionState", "BlockGasUsed", "ExecutionGasUsed", "StorageGasUsed", "Error"], StringComparer.Ordinal) ||
            !document.Inputs.TransactionFields.SequenceEqual(
                ["Type", "To", "SenderAddress", "Hash", "CalculateEffectiveGasPrice"], StringComparer.Ordinal) ||
            !document.Inputs.BlockFields.SequenceEqual(["Header", "Hash", "Number"], StringComparer.Ordinal) ||
            !document.Inputs.HeaderFields.SequenceEqual(["BaseFeePerGas", "GasUsed", "Number", "Hash"], StringComparer.Ordinal) ||
            !document.Inputs.FinalizeFields.SequenceEqual(
                ["Status", "ShouldRevert", "Output", "SubstateError", "VmError", "EvmExceptionType", "SubstateResultError", "Logs", "StateRoot"], StringComparer.Ordinal))
        {
            throw new ExtractionException("The receipt-terminal IR input field schema is not admitted.");
        }

        if (document.Operations.Length != 8 || document.Mappings.Length != 17 || document.Scope.Exclusions.Length != 5)
        {
            throw new ExtractionException("The receipt-terminal IR is not field-complete.");
        }

        if (document.Snapshot.Take != "TakeSnapshot() = receipt count" ||
            document.Snapshot.Restore != "Restore(snapshot) truncates receipts and pre-refund block totals to the retained normal prefix" ||
            !document.Snapshot.RestoredSurfaces.SequenceEqual(
                ["receipts", "cumulativeBlockGasPerTx", "cumulativeReceiptGas", "Block.Header.GasUsed"], StringComparer.Ordinal) ||
            document.Snapshot.RestoreOrder != "restore order: receipt and block-gas suffixes, then FromTotals(header) and cumulative receipt gas")
        {
            throw new ExtractionException("The receipt-terminal snapshot contract is not admitted.");
        }

        if (!document.SemanticBindings.SequenceEqual(SemanticBindings, StringComparer.Ordinal))
        {
            throw new ExtractionException("The receipt-terminal semantic binding table is not admitted.");
        }

        RequireUnique(document.Operations.Select(static operation => operation.Name), "operation");
        RequireUnique(document.Mappings.Select(static mapping => mapping.Source), "mapping source");
        foreach (OperationDescriptor operation in document.Operations)
        {
            if (string.IsNullOrWhiteSpace(operation.Name) || string.IsNullOrWhiteSpace(operation.Phase) ||
                string.IsNullOrWhiteSpace(operation.SourcePath) || string.IsNullOrWhiteSpace(operation.ContainingType) ||
                string.IsNullOrWhiteSpace(operation.Member) || string.IsNullOrWhiteSpace(operation.Signature) ||
                operation.Steps.Length == 0 || operation.Steps.Any(static step => step is null || string.IsNullOrWhiteSpace(step.Kind) || string.IsNullOrWhiteSpace(step.Text) || step.Children is null) ||
                operation.Body.Invocations is null || operation.Body.Assignments is null || operation.Body.Guards is null ||
                operation.Body.Invocations.Any(static invocation => invocation is null || invocation.Arguments is null || invocation.Expression is null || invocation.Branches is null) ||
                operation.Body.Assignments.Any(static assignment => assignment is null || assignment.Value is null || assignment.Branches is null))
            {
                throw new ExtractionException($"Operation {operation.Name} has no source-derived semantic steps.");
            }

            foreach (SemanticStep step in operation.Steps) ValidateSemanticStepShape(step, $"operation {operation.Name}");
            foreach (SemanticStep guard in operation.Body.Guards) ValidateSemanticStepShape(guard, $"operation {operation.Name} guard");
            foreach (SemanticInvocation invocation in operation.Body.Invocations)
            {
                ValidateSemanticExpressionShape(invocation.Expression, $"operation {operation.Name} invocation");
                foreach (SemanticExpression argument in invocation.Arguments)
                {
                    ValidateSemanticExpressionShape(argument, $"operation {operation.Name} invocation argument");
                }
                foreach (SemanticBranch branch in invocation.Branches)
                {
                    if (branch is null || branch.Condition is null) throw new ExtractionException("RECEIPT_BRANCH_PATH: incomplete invocation branch.");
                    ValidateSemanticExpressionShape(branch.Condition, $"operation {operation.Name} invocation branch");
                }
            }
            foreach (SemanticAssignment assignment in operation.Body.Assignments)
            {
                ValidateSemanticExpressionShape(assignment.Value, $"operation {operation.Name} assignment");
                ValidateSemanticExpressionShape(assignment.TargetExpression, $"operation {operation.Name} assignment target");
                foreach (SemanticBranch branch in assignment.Branches)
                {
                    if (branch is null || branch.Condition is null) throw new ExtractionException("RECEIPT_BRANCH_PATH: incomplete assignment branch.");
                    ValidateSemanticExpressionShape(branch.Condition, $"operation {operation.Name} assignment branch");
                }
            }
            ValidateSemanticExpressionShape(operation.Body.ReturnExpression, $"operation {operation.Name} return");
        }
        foreach (OperationDescriptor operation in document.Operations)
        {
            MemberExpectation expected = MemberExpectations.Single(member => member.Id == OperationMemberId(operation.Name));
            if (operation.SourcePath != expected.SourcePath || operation.ContainingType != expected.ContainingType ||
                operation.Member != expected.Member || operation.Signature != expected.Signature)
            {
                throw new ExtractionException($"Operation {operation.Name} is not path/owner/signature-qualified to its admitted source member.");
            }
        }
        if (expectedMembers is not null)
        {
            foreach (OperationDescriptor operation in document.Operations)
            {
                int matches = expectedMembers.Count(member => member.Identity.SourcePath == operation.SourcePath &&
                    member.Identity.ContainingType == operation.ContainingType && member.Identity.Member == operation.Member &&
                    member.Identity.Signature == operation.Signature);
                if (matches != 1)
                {
                    throw new ExtractionException($"Operation {operation.Name} is not bound to one exact source member.");
                }
            }
        }
    }

    private static string OperationMemberId(string operationName) =>
        operationName switch
        {
            "markAsSuccess" => "terminal.success",
            "markAsFailed" => "terminal.failure",
            "buildFailedReceipt" => "receipt.buildFailed",
            "buildReceipt" => "receipt.build",
            "updateCumulativeGasTracking" => "gas.update",
            "takeSnapshot" => "snapshot.take",
            "restore" => "snapshot.restore",
            "finalizeTransaction" => "caller.finalize",
            _ => throw new ExtractionException($"Unknown receipt-terminal operation {operationName}.")
        };

    private static void ValidateSemanticStepShape(SemanticStep? step, string location)
    {
        if (step is null || string.IsNullOrWhiteSpace(step.Kind) || string.IsNullOrWhiteSpace(step.Text) ||
            step.Children is null || step.Children.Any(static child => child is null))
        {
            throw new ExtractionException($"The receipt-terminal {location} contains an incomplete semantic step.");
        }

        ValidateSemanticExpressionShape(step.Expression, $"{location} step");
        ValidateSemanticExpressionShape(step.DeclaredTarget, $"{location} declared target");
        foreach (SemanticStep child in step.Children) ValidateSemanticStepShape(child, location);
    }

    private static void ValidateSemanticExpressionShape(SemanticExpression? expression, string location)
    {
        if (expression is null) return;
        if (string.IsNullOrWhiteSpace(expression.Kind) || string.IsNullOrWhiteSpace(expression.Text) ||
            expression.Children is null || expression.Children.Any(static child => child is null))
        {
            throw new ExtractionException($"The receipt-terminal {location} contains an incomplete semantic expression.");
        }

        foreach (SemanticExpression child in expression.Children)
        {
            ValidateSemanticExpressionShape(child, location);
        }
    }

    private static void ValidateLowering(LoweringDescriptor lowering)
    {
        if (lowering is null || lowering.EffectiveBlockGas is null || lowering.StatusCode is null ||
            lowering.StatusCode.Failure is null || lowering.StatusCode.Success is null || lowering.Result is null ||
            lowering.Result.Condition is null || lowering.Result.ExceptionType is null ||
            lowering.Result.ExceptionError is null || lowering.Result.OkResult is null ||
            string.IsNullOrWhiteSpace(lowering.EffectiveBlockGas.Kind) ||
            string.IsNullOrWhiteSpace(lowering.EffectiveBlockGas.Text) ||
            lowering.ReceiptFields is null || lowering.Finalization is null ||
            lowering.ReceiptFields.Length != 18 || lowering.Finalization.Length != 4)
        {
            throw new ExtractionException("The receipt-terminal semantic lowering IR is incomplete.");
        }

        string[] receiptTargets =
        [
            "receipt.Logs", "receipt.TxType", "receipt.GasUsedTotal", "receipt.StatusCode", "receipt.Recipient",
            "receipt.BlockHash", "receipt.BlockNumber", "receipt.Index", "receipt.GasUsed", "receipt.EffectiveGasPrice",
            "receipt.Sender", "receipt.ContractAddress", "receipt.TxHash", "receipt.PostTransactionState",
            "receipt.BlockGasUsed", "receipt.ExecutionGasUsed", "receipt.StorageGasUsed", "receipt.Error",
        ];
        if (!lowering.ReceiptFields.Select(static binding => binding.Target).SequenceEqual(receiptTargets, StringComparer.Ordinal) ||
            lowering.ReceiptFields.Any(static binding => binding is null || binding.Expression is null) ||
            !lowering.Finalization.Select(static binding => binding.Target).SequenceEqual(
                ["terminal.output", "terminal.error", "finalization.result", "finalization.result.error"], StringComparer.Ordinal) ||
            lowering.Finalization.Any(static binding => binding is null) ||
            lowering.StatusCode.Failure.Kind != "NumericLiteralExpression" ||
            lowering.StatusCode.Success.Kind != "NumericLiteralExpression" ||
            lowering.Result.Condition.Kind != "NotEqualsExpression" ||
            lowering.Result.ExceptionType.Text != "substate.EvmExceptionType" ||
            lowering.Result.ExceptionError.Text != "substate.SubstateError" ||
            lowering.Result.OkResult.Text != "TransactionResult.Ok")
        {
            throw new ExtractionException("The receipt-terminal semantic lowering field order is not admitted.");
        }
    }

    private static BoundMember Get(BoundMember[] members, string id) =>
        members.Single(member => member.Identity.Id == id);

    private static SourceFile Get(SourceFile[] sources, string path) =>
        sources.Single(source => source.RelativePath == path);

    private static void ValidateAdmissionTable()
    {
        string[] modeled = MemberExpectations
            .Where(static expectation => expectation.Kind == "method" ||
                expectation.Id is "status.failure" or "status.success" or "tracer.isTracingReceipt")
            .Select(static expectation => expectation.Id)
            .ToArray();
        if (modeled.Length != ExpectedCanonicalSyntaxHashes.Count ||
            modeled.Any(id => !ExpectedCanonicalSyntaxHashes.ContainsKey(id)))
        {
            throw new ExtractionException("Complete canonical member admission is not one-to-one with the modeled method and status closure.");
        }
    }

    internal static void ValidateCompilerReferenceIdentities(CompilerReferenceIdentity[] references)
    {
        if (references.Length == 0)
        {
            throw new ExtractionException("The receipt-terminal manifest has no compiler/reference closure.");
        }

        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> selectedNames = new(StringComparer.OrdinalIgnoreCase);
        string[] orderedPaths = references
            .Select(static reference => reference.Path)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        if (!references.Select(static reference => reference.Path).SequenceEqual(orderedPaths, StringComparer.Ordinal))
        {
            throw new ExtractionException("The receipt-terminal compiler/reference closure is not in canonical path order.");
        }

        foreach (CompilerReferenceIdentity reference in references)
        {
            if (string.IsNullOrWhiteSpace(reference.Path) || !paths.Add(reference.Path) ||
                string.IsNullOrWhiteSpace(reference.AssemblyName) ||
                !Guid.TryParseExact(reference.Mvid, "D", out Guid mvid) || mvid == Guid.Empty ||
                reference.Mvid != mvid.ToString("D") ||
                reference.Selected && !selectedNames.Add(reference.AssemblyName) ||
                !string.Equals(reference.AssemblyName, Path.GetFileNameWithoutExtension(reference.Path), StringComparison.OrdinalIgnoreCase))
            {
                throw new ExtractionException("The receipt-terminal compiler/reference closure contains duplicate or malformed identities.");
            }
            names.Add(reference.AssemblyName);

            string normalized = Normalize(reference.Path);
            if (!string.Equals(normalized, reference.Path, StringComparison.Ordinal) ||
                normalized.Contains("../", StringComparison.Ordinal) || normalized.Contains("/..", StringComparison.Ordinal) ||
                !normalized.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                throw new ExtractionException($"The receipt-terminal compiler reference path is not canonical: {reference.Path}.");
            }

            bool isPlatform = normalized.StartsWith("platform/", StringComparison.Ordinal);
            if (isPlatform && Path.GetFileName(normalized["platform/".Length..]) != normalized["platform/".Length..])
            {
                throw new ExtractionException($"The receipt-terminal platform compiler reference is not a file name: {reference.Path}.");
            }
            bool isClosure = CompilerReferenceClosurePaths.Any(relativePath =>
                normalized.StartsWith(Normalize(relativePath) + "/", StringComparison.Ordinal));
            if (!isPlatform && !isClosure)
            {
                throw new ExtractionException($"The receipt-terminal compiler reference escaped the admitted closure: {reference.Path}.");
            }

            RequireSha(reference.Sha256, $"compiler reference {reference.Path}");
        }

        if (!names.SetEquals(selectedNames))
        {
            throw new ExtractionException("The receipt-terminal compiler/reference closure must select exactly one path for every assembly.");
        }

        string[] missing = RequiredCompilerAssemblyNames
            .Where(required => !names.Contains(required))
            .ToArray();
        if (missing.Length != 0)
        {
            throw new ExtractionException(
                "The receipt-terminal compiler/reference manifest is missing required assemblies: " +
                string.Join(", ", missing));
        }

        foreach (KeyValuePair<string, string> required in RequiredCompilerAssemblyPaths)
        {
            string assemblyName = required.Key;
            string expectedPath = required.Value;
            CompilerReferenceIdentity reference = references.Single(candidate =>
                candidate.Selected && string.Equals(candidate.AssemblyName, assemblyName, StringComparison.OrdinalIgnoreCase));
            if (!string.Equals(reference.Path, expectedPath, StringComparison.Ordinal))
            {
                throw new ExtractionException(
                    $"The receipt-terminal compiler/reference manifest maps '{assemblyName}' to '{reference.Path}', expected '{expectedPath}'.");
            }
        }
    }

    private static bool IsExpectedKind(SyntaxNode member, string kind) =>
        kind switch
        {
            "method" => member is MethodDeclarationSyntax,
            "property" => member is PropertyDeclarationSyntax,
            "parameter" => member is ParameterSyntax,
            "field" => member is FieldDeclarationSyntax,
            _ => false,
        };

    private static string MemberName(SyntaxNode member) =>
        member switch
        {
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            PropertyDeclarationSyntax property => property.Identifier.ValueText,
            ParameterSyntax parameter => parameter.Identifier.ValueText,
            FieldDeclarationSyntax field => field.Declaration.Variables.First().Identifier.ValueText,
            _ => "",
        };

    private static int ParameterCount(SyntaxNode member) =>
        member switch
        {
            MethodDeclarationSyntax method => method.ParameterList.Parameters.Count,
            _ => 0,
        };

    private static IEnumerable<ParameterSyntax> Parameters(SyntaxNode member) =>
        member is MethodDeclarationSyntax method ? method.ParameterList.Parameters : [];

    private static SyntaxTokenList Modifiers(SyntaxNode member) =>
        member switch
        {
            MethodDeclarationSyntax method => method.Modifiers,
            PropertyDeclarationSyntax property => property.Modifiers,
            ParameterSyntax parameter => parameter.Modifiers,
            FieldDeclarationSyntax field => field.Modifiers,
            _ => default,
        };

    private static string CanonicalSignature(SyntaxNode member, string memberName) =>
        member switch
        {
            MethodDeclarationSyntax method =>
                $"{method.ReturnType} {method.Identifier.ValueText}({CanonicalParameters(method.ParameterList.Parameters)})",
            PropertyDeclarationSyntax property => $"{property.Type} {property.Identifier.ValueText}",
            ParameterSyntax parameter => $"{parameter.Type} {parameter.Identifier.ValueText}",
            FieldDeclarationSyntax field => $"{field.Declaration.Type} {memberName}",
            _ => throw new ExtractionException($"Unsupported member syntax for {memberName}."),
        };

    private static string CanonicalParameters(SeparatedSyntaxList<ParameterSyntax> parameters) =>
        string.Join(",", parameters.Select(static parameter =>
        {
            string modifiers = string.Join(" ", parameter.Modifiers.Select(static modifier => modifier.ValueText));
            return modifiers.Length == 0 ? parameter.Type!.ToString() : $"{modifiers} {parameter.Type}";
        }));

    private static ISymbol? DeclaredSymbol(SemanticModel model, SyntaxNode member, string name) =>
        member switch
        {
            MethodDeclarationSyntax method => model.GetDeclaredSymbol(method),
            PropertyDeclarationSyntax property => model.GetDeclaredSymbol(property),
            ParameterSyntax parameter => model.GetDeclaredSymbol(parameter),
            FieldDeclarationSyntax field => model.GetDeclaredSymbol(field.Declaration.Variables.First(variable => variable.Identifier.ValueText == name)),
            _ => null,
        };

    private static void ValidateSourceFingerprints(SourceFile[] sources)
    {
        if (ExpectedSourceHashes.Count != SourcePaths.Length)
        {
            throw new ExtractionException("The receipt-terminal source fingerprint table is incomplete.");
        }
        foreach (SourceFile source in sources)
        {
            if (!ExpectedSourceHashes.TryGetValue(source.RelativePath, out string? expected) || source.Sha256 != expected)
            {
                throw new ExtractionException($"Pinned source changed: {source.RelativePath}. Expected {expected ?? "<missing>"}, got {source.Sha256}.");
            }
        }
    }

    private static void ValidatePinnedSourceFiles(string root)
    {
        if (ExpectedSourceHashes.Count != SourcePaths.Length)
        {
            throw new ExtractionException("The receipt-terminal source fingerprint table is incomplete.");
        }

        foreach (string relativePath in SourcePaths)
        {
            string path = Path.GetFullPath(Path.Combine(root, relativePath));
            EnsureWithin(root, path);
            if (!File.Exists(path))
            {
                throw new ExtractionException($"Required source does not exist: {relativePath}.");
            }

            string actual = Sha256(File.ReadAllBytes(path));
            string expected = ExpectedSourceHashes[relativePath];
            if (actual != expected)
            {
                throw new ExtractionException($"Pinned source changed: {relativePath}. Expected {expected}, got {actual}.");
            }
        }
    }

    private static void ValidateSourcePinsDocument(string root)
    {
        string path = Path.GetFullPath(Path.Combine(root, SourcePinsRelativePath));
        EnsureWithin(root, path);
        if (!File.Exists(path))
        {
            throw new ExtractionException($"The source pin document does not exist: {SourcePinsRelativePath}.");
        }

        byte[] bytes = File.ReadAllBytes(path);
        try
        {
            _ = Utf8WithoutBom.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ExtractionException($"The source pin document is not strict UTF-8: {exception.Message}");
        }

        SourcePinsDocument document;
        try
        {
            RejectDuplicateJsonProperties(bytes, "source pin document");
            document = JsonSerializer.Deserialize<SourcePinsDocument>(bytes, JsonOptions)
                ?? throw new ExtractionException("The source pin document was empty.");
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"The source pin document is invalid: {exception.Message}");
        }

        if (document.SchemaVersion != SourcePinsSchemaVersion ||
            document.Root != "Nethermind.Blockchain.Tracing.BlockReceiptsTracer (exact base; no BuildReceipt override)")
        {
            throw new ExtractionException("The source pin document header changed.");
        }

        ValidateSourcePinEntries(document.Sources, SourcePaths, ExpectedSourceHashes, "source");
        ValidateSourcePinEntries(document.BindingSources, BindingSourcePaths, ExpectedBindingSourceHashes, "binding source");
        if (document.DelegatedKernel is null ||
            document.DelegatedKernel.GeneratedLean != AccountingKernelGeneratedPath ||
            document.DelegatedKernel.GeneratedLeanSha256 != AccountingKernelGeneratedSha256 ||
            document.DelegatedKernel.Refinement != AccountingKernelRefinementPath ||
            document.DelegatedKernel.RefinementSha256 != AccountingKernelRefinementSha256 ||
            document.DelegatedKernel.Ir != AccountingKernelIrPath ||
            document.DelegatedKernel.IrSha256 != AccountingKernelIrSha256 ||
            document.DelegatedKernel.Manifest != AccountingKernelManifestPath ||
            document.DelegatedKernel.ManifestSha256 != AccountingKernelManifestSha256 ||
            document.DelegatedKernel.SourceSha256 != AccountingKernelSourceSha256 ||
            document.DelegatedKernel.LeanDependencies is null ||
            !document.DelegatedKernel.LeanDependencies.SequenceEqual(LeanDependencyPins))
        {
            throw new ExtractionException("The source pin document delegated-kernel identity changed.");
        }

        if (!Serialize(document).AsSpan().SequenceEqual(bytes))
        {
            throw new ExtractionException("The source pin document is not canonical.");
        }
    }

    private static void ValidateSourcePinEntries(
        SourcePinEntry[]? entries,
        string[] expectedPaths,
        IReadOnlyDictionary<string, string> expectedHashes,
        string label)
    {
        if (entries is null || entries.Length != expectedPaths.Length)
        {
            throw new ExtractionException(
                $"The source pin document has an incomplete {label} closure: expected {expectedPaths.Length} entries.");
        }

        HashSet<string> paths = new(StringComparer.Ordinal);
        for (int index = 0; index < expectedPaths.Length; index++)
        {
            SourcePinEntry? entry = entries[index];
            string expectedPath = expectedPaths[index];
            if (entry is null || !paths.Add(entry.Path) || entry.Path != expectedPath ||
                string.IsNullOrWhiteSpace(entry.Role) ||
                !expectedHashes.TryGetValue(entry.Path, out string? expectedHash) || entry.Sha256 != expectedHash)
            {
                throw new ExtractionException($"The source pin document {label} identity at index {index} changed.");
            }

            RequireSha(entry.Sha256, $"source pin {entry.Path}");
        }
    }

    private static void ValidateBindingSourceFingerprints(string root)
    {
        if (ExpectedBindingSourceHashes.Count != BindingSourcePaths.Length)
        {
            throw new ExtractionException("The receipt-terminal semantic binding source fingerprint table is incomplete.");
        }

        foreach (string relativePath in BindingSourcePaths)
        {
            string path = Path.GetFullPath(Path.Combine(root, relativePath));
            EnsureWithin(root, path);
            RequireHash(path, ExpectedBindingSourceHashes[relativePath], $"semantic binding source {relativePath}");
        }
    }

    private static void Require(string text, string token, string label)
    {
        if (!text.Contains(token, StringComparison.Ordinal))
        {
            throw new ExtractionException($"Missing {label}: '{token}'.");
        }
    }

    private static void RequireOrdered(string text, IReadOnlyList<string> tokens, string label)
    {
        int cursor = 0;
        foreach (string token in tokens)
        {
            int position = text.IndexOf(token, cursor, StringComparison.Ordinal);
            if (position < 0)
            {
                throw new ExtractionException($"Missing {label} token '{token}'.");
            }
            cursor = position + token.Length;
        }
    }

    private static void RequireUnique(IEnumerable<string> values, string label)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || !seen.Add(value))
            {
                throw new ExtractionException($"The {label} list contains a blank or duplicate entry.");
            }
        }
    }

    private static void RequireHash(string path, string expected, string label)
    {
        if (!File.Exists(path))
        {
            throw new ExtractionException($"Required {label} does not exist: {path}.");
        }
        string actual = Sha256(File.ReadAllBytes(path));
        if (actual != expected)
        {
            throw new ExtractionException($"Pinned {label} changed. Expected {expected}, got {actual}.");
        }
    }

    private static void RequireSha(string? value, string label)
    {
        if (value is null || value.Length != 64 ||
            value.Any(character => !Uri.IsHexDigit(character)) ||
            value.Any(char.IsUpper))
        {
            throw new ExtractionException($"The {label} is not a lowercase SHA-256 digest.");
        }
    }

    private static void RejectDuplicateJsonProperties(byte[] bytes, string label)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        Visit(document.RootElement);
        return;

        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                HashSet<string> names = new(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw new ExtractionException($"The serialized receipt-terminal {label} contains duplicate property '{property.Name}'.");
                    }
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray()) Visit(item);
            }
        }
    }

    private static void ValidateGeneratedLean(byte[] candidate, byte[] expected)
    {
        string text;
        try
        {
            text = Utf8WithoutBom.GetString(candidate);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ExtractionException($"The generated receipt-terminal Lean artifact is not strict UTF-8: {exception.Message}");
        }
        if (candidate.Length >= 3 && candidate[0] == 0xef && candidate[1] == 0xbb && candidate[2] == 0xbf ||
            text.Contains('\r') || !text.EndsWith('\n') || text.EndsWith("\n\n", StringComparison.Ordinal))
        {
            throw new ExtractionException("The generated receipt-terminal Lean artifact must be BOM-free LF-only UTF-8 with one final LF.");
        }
        if (Regex.IsMatch(text, @"\b(theorem|axiom|example|sorry|admit)\b", RegexOptions.CultureInvariant))
        {
            throw new ExtractionException("The generated receipt-terminal Lean artifact contains a proof declaration or placeholder.");
        }
        if (!candidate.AsSpan().SequenceEqual(expected))
        {
            throw new ExtractionException("The generated receipt-terminal Lean artifact differs from exact typed IR emission.");
        }
    }

    private static byte[] Serialize<T>(T value) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions).ReplaceLineEndings("\n") + "\n");

    private static string CombinedHash(IEnumerable<string> values) =>
        Sha256(Encoding.UTF8.GetBytes(string.Join('\n', values) + "\n"));

    private static string CompilerReferenceAggregateSha256(IEnumerable<CompilerReferenceIdentity> references) =>
        CombinedHash(references.Select(static reference =>
            $"{reference.Path}\0{reference.AssemblyName}\0{reference.Sha256}\0{reference.Mvid}\0{reference.Selected}"));

    internal static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static void EnsureWithin(string root, string path)
    {
        string canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ExtractionException($"Path escapes the admitted root: {path}.");
        }
    }

    private static void RejectCandidateSymbols(SymbolInfo info, SyntaxNode node, string role)
    {
        if (info.CandidateReason != CandidateReason.None || info.CandidateSymbols.Length != 0)
        {
            throw new ExtractionException(
                $"Roslyn produced a candidate, ambiguous, or invalid {role} binding at '{Canonical(node)}'.");
        }
    }

    private static bool HasErrorSymbol(ISymbol? symbol)
    {
        if (symbol is null)
        {
            return false;
        }

        if (symbol is ITypeSymbol type && ContainsErrorType(type))
        {
            return true;
        }

        if (symbol is IMethodSymbol method &&
            (ContainsErrorType(method.ContainingType) || ContainsErrorType(method.ReturnType) ||
             method.Parameters.Any(parameter => ContainsErrorType(parameter.Type)) ||
             method.TypeParameters.Any(parameter => parameter.ConstraintTypes.Any(ContainsErrorType))))
        {
            return true;
        }

        if (symbol is IPropertySymbol property && ContainsErrorType(property.Type) ||
            symbol is IFieldSymbol field && ContainsErrorType(field.Type) ||
            symbol is IEventSymbol @event && ContainsErrorType(@event.Type) ||
            symbol is IParameterSymbol parameter && ContainsErrorType(parameter.Type) ||
            symbol is ILocalSymbol local && ContainsErrorType(local.Type))
        {
            return true;
        }

        for (ISymbol? current = symbol; current is not null; current = current.ContainingSymbol)
        {
            if (current is IErrorTypeSymbol || current.Kind == SymbolKind.ErrorType)
            {
                return true;
            }

            if (current is ITypeSymbol containingType && ContainsErrorType(containingType))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsErrorType(ITypeSymbol? type) =>
        type is not null && ContainsErrorType(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));

    private static bool ContainsErrorType(ITypeSymbol type, HashSet<ITypeSymbol> seen)
    {
        if (!seen.Add(type))
        {
            return false;
        }

        if (type is IErrorTypeSymbol || type.TypeKind == TypeKind.Error)
        {
            return true;
        }

        if (type is INamedTypeSymbol named &&
            (named.ContainingType is not null && ContainsErrorType(named.ContainingType, seen) ||
             !named.IsUnboundGenericType && named.TypeArguments.Any(argument => ContainsErrorType(argument, seen))))
        {
            return true;
        }

        if (type is ITypeParameterSymbol typeParameter &&
            typeParameter.ConstraintTypes.Any(constraint => ContainsErrorType(constraint, seen)))
        {
            return true;
        }

        return type switch
        {
            IArrayTypeSymbol array => ContainsErrorType(array.ElementType, seen),
            IPointerTypeSymbol pointer => ContainsErrorType(pointer.PointedAtType, seen),
            IFunctionPointerTypeSymbol functionPointer =>
                ContainsErrorType(functionPointer.Signature.ReturnType, seen) ||
                functionPointer.Signature.Parameters.Any(parameter => ContainsErrorType(parameter.Type, seen)),
            _ => false,
        };
    }

    private static void ValidateCompilation(CSharpCompilation compilation)
    {
        Diagnostic[] diagnostics;
        try
        {
            // Materialize the complete diagnostic set.  Looking only at a first
            // diagnostic or at syntax-tree diagnostics can hide unresolved
            // metadata and later source errors.
            diagnostics = compilation.GetDiagnostics().ToArray();
        }
        catch (Exception exception)
        {
            throw new ExtractionException($"The production compiler could not produce diagnostics: {exception.Message}");
        }

        Diagnostic[] errors = diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length == 0)
        {
            ValidateCompilationSymbolBindings(compilation);
            return;
        }

        string detail = string.Join(Environment.NewLine, diagnostics.Select(static diagnostic => diagnostic.ToString()));
        throw new ExtractionException(
            $"The production compiler/reference closure produced {errors.Length} error diagnostic(s) across {diagnostics.Length} diagnostic(s):{Environment.NewLine}{detail}");
    }

    private static void ValidateCompilationSymbolBindings(CSharpCompilation compilation)
    {
        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            SemanticModel model = compilation.GetSemanticModel(tree);
            foreach (SyntaxNode node in tree.GetRoot().DescendantNodesAndSelf())
            {
                if (node is IdentifierNameSyntax { Identifier.ValueText: "nameof" } &&
                    node.Parent is InvocationExpressionSyntax invocation && invocation.Expression == node &&
                    model.GetOperation(invocation) is INameOfOperation)
                {
                    continue;
                }
                SymbolInfo info = model.GetSymbolInfo(node);
                if (info.CandidateReason != CandidateReason.None || info.CandidateSymbols.Length != 0)
                {
                    throw new ExtractionException(
                        $"The production compiler/reference closure produced a candidate or ambiguous symbol " +
                        $"in '{tree.FilePath}' at '{Canonical(node)}': {info.CandidateReason}; " +
                        string.Join(", ", info.CandidateSymbols.Select(static candidate => candidate.ToDisplayString())) + ".");
                }

                if (info.Symbol is not null && HasErrorSymbol(info.Symbol))
                {
                    throw new ExtractionException(
                        $"The production compiler/reference closure produced an error symbol in " +
                        $"'{tree.FilePath}' at '{Canonical(node)}': {info.Symbol}.");
                }

                // A type can be unresolved even when GetSymbolInfo returns no symbol
                // for the syntax node (for example, a malformed generic or conversion
                // site).  Complete diagnostics normally expose these cases, but inspect
                // both type slots as an independent fail-closed check.  The empty rank
                // specifier in a valid array type is the one parser sentinel that must
                // be judged through its enclosing array type instead.
                if (node is not OmittedArraySizeExpressionSyntax)
                {
                    Microsoft.CodeAnalysis.TypeInfo typeInfo = model.GetTypeInfo(node);
                    if (ContainsErrorType(typeInfo.Type) || ContainsErrorType(typeInfo.ConvertedType))
                    {
                        throw new ExtractionException(
                            $"The production compiler/reference closure produced an error type in " +
                            $"'{tree.FilePath}' at '{Canonical(node)}'.");
                    }
                }
            }
        }
    }

    private static CompilerReferenceClosure BuildMetadataReferences(string referenceRoot)
    {
        string root = Path.GetFullPath(referenceRoot);
        // The inventory is an admission boundary, not an output produced from
        // the current release directories.  Verify the live closure first so
        // a changed build can never rewrite its own compiler identity.
        CompilerReferenceInventory inventory = LoadCompilerReferenceInventory(root);

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string trustedAssemblies ||
            string.IsNullOrWhiteSpace(trustedAssemblies))
        {
            throw new ExtractionException("The trusted platform assembly list is unavailable; the compiler closure is incomplete.");
        }

        string platformDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)
            ?? throw new ExtractionException("The runtime platform assembly directory is unavailable.");
        string canonicalPlatformDirectory = Path.GetFullPath(platformDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string[] platformPaths = trustedAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(Path.GetFullPath)
            .Where(path => string.Equals(
                Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                canonicalPlatformDirectory,
                StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (platformPaths.Length == 0)
        {
            throw new ExtractionException("The trusted platform assembly list has no runtime-platform entries; the compiler closure is incomplete.");
        }

        string[] closureDirectories = CompilerReferenceClosurePaths
            .Select(relativePath =>
            {
                string directory = Path.GetFullPath(Path.Combine(root, relativePath));
                EnsureWithin(root, directory);
                if (!Directory.Exists(directory))
                {
                    throw new ExtractionException($"The compiler/reference closure is missing '{relativePath}'.");
                }

                return directory;
            })
            .ToArray();

        ValidateReferenceInventoryPaths(inventory, root, platformPaths, closureDirectories);

        List<MetadataReference> references = new(inventory.References.Length);
        for (int index = 0; index < inventory.References.Length; index++)
        {
            CompilerReferenceIdentity identity = inventory.References[index];
            string fullPath = ResolveCompilerReferencePath(root, canonicalPlatformDirectory, identity.Path);
            if (!File.Exists(fullPath))
            {
                throw new ExtractionException(
                    $"The compiler reference inventory member does not exist: {identity.Path}.");
            }

            byte[] image;
            try
            {
                image = File.ReadAllBytes(fullPath);
            }
            catch (Exception exception)
            {
                throw new ExtractionException(
                    $"The compiler reference inventory member '{identity.Path}' could not be hashed: {exception.Message}");
            }
            string actualSha = Sha256(image);
            if (actualSha != identity.Sha256)
            {
                throw new ExtractionException(
                    $"The compiler reference inventory detected byte drift in '{identity.Path}': expected {identity.Sha256}, got {actualSha}.");
            }

            string assemblyName;
            string mvid;
            try
            {
                using MemoryStream stream = new(image, writable: false);
                using PEReader reader = new(stream);
                if (!reader.HasMetadata)
                {
                    throw new ExtractionException($"The compiler reference '{identity.Path}' has no metadata.");
                }
                MetadataReader metadata = reader.GetMetadataReader();
                assemblyName = metadata.GetString(metadata.GetAssemblyDefinition().Name);
                mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToString("D");
            }
            catch (Exception exception)
            {
                throw new ExtractionException(
                    $"The compiler/reference inventory member '{identity.Path}' has no readable assembly metadata: {exception.Message}");
            }
            if (!string.Equals(assemblyName, identity.AssemblyName, StringComparison.Ordinal) || mvid != identity.Mvid)
            {
                throw new ExtractionException(
                    $"The compiler/reference inventory member '{identity.Path}' has assembly identity '{assemblyName}'/{mvid}, expected '{identity.AssemblyName}'/{identity.Mvid}.");
            }

            MetadataReference reference;
            try
            {
                reference = MetadataReference.CreateFromImage(image, filePath: fullPath);
            }
            catch (Exception exception)
            {
                throw new ExtractionException(
                    $"The compiler/reference inventory member '{identity.Path}' is not valid metadata: {exception.Message}");
            }

            if (identity.Selected)
            {
                references.Add(reference);
            }
        }

        return new(references.ToArray(), inventory.References);
    }

    private static CompilerReferenceInventory LoadCompilerReferenceInventory(string root)
    {
        string path = Path.GetFullPath(Path.Combine(root, CompilerReferenceInventoryRelativePath));
        EnsureWithin(root, path);
        if (!File.Exists(path))
        {
            throw new ExtractionException(
                $"The checked-in compiler reference inventory does not exist: {CompilerReferenceInventoryRelativePath}.");
        }

        byte[] bytes = File.ReadAllBytes(path);
        string inventorySha256 = Sha256(bytes);
        if (inventorySha256 != CompilerReferenceInventorySha256)
        {
            throw new ExtractionException(
                $"The checked-in compiler reference inventory bytes changed: expected {CompilerReferenceInventorySha256}, got {inventorySha256}.");
        }

        CompilerReferenceInventory inventory;
        try
        {
            RejectDuplicateJsonProperties(bytes, "compiler reference inventory");
            inventory = JsonSerializer.Deserialize<CompilerReferenceInventory>(bytes, JsonOptions)
                ?? throw new ExtractionException("The compiler reference inventory was empty.");
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"The compiler reference inventory is invalid: {exception.Message}");
        }

        if (inventory.SchemaVersion != CompilerReferenceInventorySchemaVersion ||
            inventory.Count != CompilerReferenceInventoryCount ||
            inventory.AggregateSha256 != CompilerReferenceInventoryAggregateSha256 ||
            inventory.References is null ||
            inventory.References.Any(static reference => reference is null))
        {
            throw new ExtractionException("The checked-in compiler reference inventory header changed.");
        }

        ValidateCompilerReferenceIdentities(inventory.References);
        if (inventory.Count != inventory.References.Length ||
            inventory.AggregateSha256 != CompilerReferenceAggregateSha256(inventory.References))
        {
            throw new ExtractionException("The checked-in compiler reference inventory count or aggregate changed.");
        }
        RequireSha(inventory.AggregateSha256, "compiler reference inventory aggregate hash");
        if (!Serialize(inventory).AsSpan().SequenceEqual(bytes))
        {
            throw new ExtractionException("The checked-in compiler reference inventory is not canonical.");
        }

        return inventory;
    }

    internal static void ValidateReferenceInventoryPaths(
        CompilerReferenceInventory inventory,
        string root,
        string[] platformPaths,
        string[] closureDirectories)
    {
        HashSet<string> expected = inventory.References
            .Select(static reference => reference.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> actual = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in platformPaths)
        {
            string logicalPath = $"platform/{Path.GetFileName(path)}";
            if (!actual.Add(logicalPath))
            {
                throw new ExtractionException($"The trusted platform assembly list contains a duplicate runtime entry: {logicalPath}.");
            }
        }

        foreach (string directory in closureDirectories)
        {
            foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                         .Where(static path => string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                         .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase))
            {
                string logicalPath = Normalize(Path.GetRelativePath(root, path));
                if (!actual.Add(logicalPath))
                {
                    throw new ExtractionException($"The compiler reference closure contains a duplicate inventory path: {logicalPath}.");
                }
            }
        }

        string[] additions = actual.Except(expected, StringComparer.OrdinalIgnoreCase).OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        string[] removals = expected.Except(actual, StringComparer.OrdinalIgnoreCase).OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (additions.Length != 0 || removals.Length != 0)
        {
            throw new ExtractionException(
                "The compiler reference inventory path set changed. " +
                $"Additions: [{string.Join(", ", additions)}]. " +
                $"Removals: [{string.Join(", ", removals)}].");
        }
    }

    private static string ResolveCompilerReferencePath(string root, string platformDirectory, string logicalPath)
    {
        if (logicalPath.StartsWith("platform/", StringComparison.Ordinal))
        {
            string fileName = logicalPath["platform/".Length..];
            if (fileName.Length == 0 || Path.GetFileName(fileName) != fileName)
            {
                throw new ExtractionException($"The platform compiler reference path is not a file name: {logicalPath}.");
            }

            return Path.Combine(platformDirectory, fileName);
        }

        string fullPath = Path.GetFullPath(Path.Combine(root, logicalPath));
        EnsureWithin(root, fullPath);
        return fullPath;
    }

    private sealed record CompilerReferenceClosure(
        MetadataReference[] References,
        CompilerReferenceIdentity[] Identities);

    private sealed record SourceReadResult(
        SourceFile[] Sources,
        CompilerReferenceIdentity[] CompilerReferences);

    private sealed record SourceFile(
        string RelativePath,
        string Text,
        SyntaxTree Tree,
        CompilationUnitSyntax Root,
        SemanticModel? SemanticModel,
        string Sha256);

    private sealed record BoundMember(
        MemberIdentity Identity,
        SyntaxNode Node,
        string CanonicalSyntax,
        ISymbol Symbol,
        SemanticModel SemanticModel);

    private sealed record MemberExpectation(
        string Id,
        string SourcePath,
        string ContainingType,
        string Member,
        string Kind,
        string Signature,
        int ParameterCount,
        string[] OrderedEffects,
        string[] RequiredModifiers);

    private static MemberExpectation Method(
        string id,
        string sourcePath,
        string containingType,
        string member,
        string signature,
        int parameterCount,
        string[] orderedEffects,
        string[] requiredModifiers) =>
        new(id, sourcePath, containingType, member, "method", signature, parameterCount, orderedEffects, requiredModifiers);

    private static MemberExpectation Property(
        string id,
        string sourcePath,
        string containingType,
        string member,
        string signature) =>
        new(id, sourcePath, containingType, member, "property", signature, 0, [], ["public"]);

    private static MemberExpectation PrimaryProperty(
        string id,
        string sourcePath,
        string containingType,
        string member,
        string signature) =>
        new(id, sourcePath, containingType, member, "parameter", signature, 0, [], []);

    private static MemberExpectation Field(
        string id,
        string sourcePath,
        string containingType,
        string member,
        string signature,
        string[]? requiredModifiers = null) =>
        new(id, sourcePath, containingType, member, "field", signature, 0, [], requiredModifiers ?? ["public"]);

}

internal sealed record ScopeDescriptor(string Mode, string ExecutionContext, string[] Exclusions);

internal sealed record AccountingDescriptor(
    string KernelType,
    string AccumulateSignature,
    string FromTotalsSignature,
    string GeneratedLeanPath,
    string RefinementPath,
    string ArithmeticContract);

internal sealed record InputDescriptor(
    string[] GasFields,
    string[] ReceiptFields,
    string[] TransactionFields,
    string[] BlockFields,
    string[] HeaderFields,
    string[] FinalizeFields);

internal sealed record OperationDescriptor(
    string Name,
    string Phase,
    string SourcePath,
    string ContainingType,
    string Member,
    string Signature,
    string[] Inputs,
    string[] Outputs,
    string[] OrderedEffects,
    string[] Guards,
    string[] ForwardedOrProjected,
    string[] Exclusions,
    SemanticStep[] Steps,
    SemanticOperation Body);

internal sealed record SnapshotDescriptor(
    string Take,
    string Restore,
    string[] RestoredSurfaces,
    string RestoreOrder);

internal sealed record MappingDescriptor(
    string Source,
    string Target,
    string Semantics,
    SemanticExpression? Expression = null);

internal sealed record IrDocument(
    int SchemaVersion,
    string ExtractorVersion,
    string Kernel,
    ScopeDescriptor Scope,
    AccountingDescriptor AccountingKernel,
    InputDescriptor Inputs,
    OperationDescriptor[] Operations,
    SnapshotDescriptor Snapshot,
    MappingDescriptor[] Mappings,
    LoweringDescriptor Lowering,
    string[] SemanticBindings);

internal sealed record LoweringDescriptor(
    SemanticExpression EffectiveBlockGas,
    StatusCodeLowering StatusCode,
    LoweringBinding[] ReceiptFields,
    LoweringBinding[] Finalization,
    FinalizationResultLowering Result);

internal sealed record StatusCodeLowering(
    SemanticExpression Failure,
    SemanticExpression Success);

internal sealed record FinalizationResultLowering(
    SemanticExpression Condition,
    SemanticExpression ExceptionType,
    SemanticExpression ExceptionError,
    SemanticExpression OkResult);

internal sealed record LoweringBinding(
    string Target,
    string Source,
    SemanticExpression? Expression,
    SemanticExpression? Guard = null);

internal sealed record SourceIdentity(string Path, string Sha256);

internal sealed record CompilerReferenceIdentity(string Path, string AssemblyName, string Sha256, string Mvid, bool Selected);

internal sealed record CompilerReferenceInventory(
    int SchemaVersion,
    int Count,
    string AggregateSha256,
    CompilerReferenceIdentity[] References);

internal sealed record SourcePinEntry(string Path, string Role, string Sha256);

internal sealed record DelegatedKernelPins(
    string GeneratedLean,
    string GeneratedLeanSha256,
    string Refinement,
    string RefinementSha256,
    string Ir,
    string IrSha256,
    string Manifest,
    string ManifestSha256,
    string SourceSha256,
    SourceIdentity[] LeanDependencies);

internal sealed record SourcePinsDocument(
    int SchemaVersion,
    string Root,
    SourcePinEntry[] Sources,
    SourcePinEntry[] BindingSources,
    DelegatedKernelPins DelegatedKernel);

internal sealed record MemberIdentity(
    string Id,
    string SourcePath,
    string ContainingType,
    string Member,
    string Kind,
    string Signature,
    string[] ParameterTypes,
    string SemanticType,
    string NodeKind,
    string CanonicalSha256);

internal sealed record ArtifactIdentity(string Path, string Sha256);

internal sealed record KernelIdentity(
    string Root,
    string SourcePath,
    string SourceSha256,
    string IrPath,
    string IrSha256,
    string GeneratedLeanPath,
    string GeneratedLeanSha256,
    string RefinementPath,
    string RefinementSha256,
    string ManifestPath,
    string ManifestSha256,
    SourceIdentity[] LeanDependencies);

internal sealed record SourceManifest(
    int SchemaVersion,
    string ExtractorVersion,
    string CompilerVersion,
    string LanguageVersion,
    string Kernel,
    SourceIdentity[] Sources,
    SourceIdentity[] BindingSources,
    CompilerReferenceIdentity[] CompilerReferences,
    int CompilerReferenceCount,
    string CompilerReferenceAggregateSha256,
    MemberIdentity[] Members,
    ArtifactIdentity Ir,
    ArtifactIdentity Lean,
    KernelIdentity AccountingKernel,
    string CombinedSourceSha256,
    string CombinedMemberSha256,
    string[] SemanticBindings);
