// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Nethermind.Evm.Lean.SimpleTransferCompletionExtractor;

/// <summary>
/// Admits the ordinary Ethereum simple-transfer completion to a finite, typed IR boundary.
/// </summary>
/// <remarks>
/// This extractor deliberately stops at a request-issuance boundary. It records the receipt
/// callback inputs and callback ordering, but does not claim that the handwritten model executes
/// the production bodies of settlement, fee payment, finalization, callback dispatch, or world
/// state. Every admitted node is discovered from a Roslyn syntax node and an
/// <see cref="IOperation"/>/symbol projection; the closed model formulas are a separate
/// handwritten boundary. Unknown syntax, source drift, and an incomplete
/// production route fail closed; the resulting theorem is model-to-model under explicit normal-
/// return adapter premises.
/// </remarks>
internal static class Extractor
{
    internal const string TransactionProcessorPath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs";
    internal const string MetricsPath = "src/Nethermind/Nethermind.Evm/Metrics.cs";
    internal const string ExecutionOptionsPath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/ExecutionOptions.cs";
    internal const string SettlementKernelPath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionSettlementKernel.cs";
    internal const string RoutingKernelPath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionRoutingKernel.cs";
    internal const string MainnetDiPath = "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs";
    internal const string GasPolicyInterfacePath = "src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs";
    internal const string EthereumGasPolicyPath = "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs";
    internal const string StateGasChargeKernelPath = "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasChargeKernel.cs";
    internal const string StateGasTransitionKernelPath = "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs";
    internal const string StateGasTransitionAdapterKernelPath = "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionAdapterKernel.cs";
    internal const string GasCostInterfacePath = "src/Nethermind/Nethermind.Evm/GasPolicy/IGasCost.cs";
    internal const string TransactionGasInitializationKernelPath = "src/Nethermind/Nethermind.Evm/GasPolicy/TransactionGasInitializationKernel.cs";
    internal const string WorldStateInterfacePath = "src/Nethermind/Nethermind.Evm/State/IWorldState.cs";
    internal const string ReadOnlyStateProviderInterfacePath = "src/Nethermind/Nethermind.Evm/State/IReadOnlyStateProvider.cs";
    internal const string WorldStateExtensionsPath = "src/Nethermind/Nethermind.Evm/State/WorldStateExtensions.cs";
    internal const string WorldStatePath = "src/Nethermind/Nethermind.State/WorldState.cs";
    internal const string StateProviderPath = "src/Nethermind/Nethermind.State/StateProvider.cs";
    internal const string TransactionPath = "src/Nethermind/Nethermind.Core/Transaction.cs";
    internal const string TransactionStdPath = "src/Nethermind/Nethermind.Core/Transaction.std.cs";
    internal const string AddressPath = "src/Nethermind/Nethermind.Core/Address.cs";
    internal const string GasCostOfPath = "src/Nethermind/Nethermind.Core/GasCostOf.cs";
    internal const string BlockHeaderPath = "src/Nethermind/Nethermind.Core/BlockHeader.cs";
    internal const string TransactionSubstatePath = "src/Nethermind/Nethermind.Evm/TransactionSubstate.cs";
    internal const string TxTracerPath = "src/Nethermind/Nethermind.Evm/Tracing/ITxTracer.cs";
    internal const string TxTracerBasePath = "src/Nethermind/Nethermind.Evm/Tracing/TxTracer.cs";
    internal const string NullTxTracerPath = "src/Nethermind/Nethermind.Evm/Tracing/NullTxTracer.cs";
    internal const string WorldStateTracerPath = "src/Nethermind/Nethermind.Evm/Tracing/State/IWorldStateTracer.cs";
    internal const string StackAccessTrackerPath = "src/Nethermind/Nethermind.Evm/StackAccessTracker.cs";
    internal const string EvmObjectPoolPath = "src/Nethermind/Nethermind.Evm/EvmObjectPool.std.cs";
    internal const string LogEntryPath = "src/Nethermind/Nethermind.Core/LogEntry.cs";
    internal const string JournalCollectionPath = "src/Nethermind/Nethermind.Core/Collections/JournalCollection.cs";
    internal const string AccessListPath = "src/Nethermind/Nethermind.Core/Eip2930/AccessList.cs";
    internal const string GasConsumedPath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/GasConsumed.cs";
    internal const string StatusCodePath = "src/Nethermind/Nethermind.Evm/StatusCode.cs";
    internal const string TransferLogPath = "src/Nethermind/Nethermind.Evm/TransferLog.cs";
    internal const string IReleaseSpecPath = "src/Nethermind/Nethermind.Core/Specs/IReleaseSpec.cs";
    internal const string EvmExceptionPath = "src/Nethermind/Nethermind.Evm/EvmException.cs";
    internal const string ExecutionTypePath = "src/Nethermind/Nethermind.Evm/ExecutionType.cs";
    internal const string ITransactionProcessorPath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/ITransactionProcessor.cs";
    internal const string ReceiptAdapterPath =
        "src/Nethermind/Nethermind.Consensus/Processing/TransactionProcessorAdapterExtensions.cs";
    private const string ReceiptAdapterSha256 = "910820a7e8bba91c30b052f5818707c46d625a2fbf12727e2aa869512120aa9e";
    internal const string SystemTransactionProcessorPath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionProcessor.cs";
    internal const string CodeRepositoryInterfacePath = "src/Nethermind/Nethermind.Evm/ICodeInfoRepository.cs";
    internal const string CodeInfoPath = "src/Nethermind/Nethermind.Evm/CodeAnalysis/CodeInfo.cs";
    internal const string JumpDestinationAnalyzerPath =
        "src/Nethermind/Nethermind.Evm/CodeAnalysis/JumpDestinationAnalyzer.cs";
    internal const string JumpDestinationAnalyzerStandardPath =
        "src/Nethermind/Nethermind.Evm/CodeAnalysis/JumpDestinationAnalyzer.std.cs";
    internal const string IntrinsicGasCalculatorPath =
        "src/Nethermind/Nethermind.Evm/IntrinsicGasCalculator.cs";
    internal const string AccountAccessPricingKernelPath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/AccountAccessPricingKernel.cs";
    internal const string PrecompileGasPricingKernelPath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/PrecompileGasPricingKernel.cs";
    internal const string SStorePricingKernelPath =
        "src/Nethermind/Nethermind.Evm/GasPolicy/SStorePricingKernel.cs";
    internal const string CodeRepositoryPath = "src/Nethermind/Nethermind.Evm/CodeInfoRepository.cs";
    internal const string CacheCodeRepositoryPath = "src/Nethermind/Nethermind.Evm/CacheCodeInfoRepository.cs";
    internal const string VirtualMachineInterfacePath = "src/Nethermind/Nethermind.Evm/IVirtualMachine.cs";
    internal const string VirtualMachineSignaturePath = "src/Nethermind/Nethermind.Evm/VirtualMachine.cs";
    internal const string VmStatePath = "src/Nethermind/Nethermind.Evm/VmState.cs";
    internal const string PartialStorageProviderPath = "src/Nethermind/Nethermind.State/PartialStorageProviderBase.cs";
    internal const string PersistentStorageProviderPath = "src/Nethermind/Nethermind.State/PersistentStorageProvider.cs";
    internal const string PersistentStorageProviderStandardPath = "src/Nethermind/Nethermind.State/PersistentStorageProvider.std.cs";
    internal const string TransientStorageProviderPath = "src/Nethermind/Nethermind.State/TransientStorageProvider.cs";
    internal const string ChangeTypePath = "src/Nethermind/Nethermind.State/ChangeType.cs";
    internal const string DispatchFlagsStandardPath = "src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs";
    internal const string MetricsStandardPath = "src/Nethermind/Nethermind.Evm/Metrics.std.cs";
    internal const string LocalMetricsFlushStandardPath = "src/Nethermind/Nethermind.State/LocalMetricsFlush.std.cs";

    // Byte-pinned kernel dependencies are identities, not imported proof obligations. Their bytes
    // are checked exactly, while the composition theorem below remains handwritten in this package.
    internal const string SettlementLeanPath = "tools/Evm/Lean/Eip803x/Generated/TransactionSettlementKernel.lean";
    internal const string SettlementRefinementPath = "tools/Evm/Lean/Eip803x/Refinement/TransactionSettlement.lean";
    internal const string StateChargeLeanPath = "tools/Evm/Lean/Eip803x/Generated/StateGasChargeKernel.lean";
    internal const string StateChargeRefinementPath = "tools/Evm/Lean/Eip803x/Refinement/StateGasCharge.lean";
    internal const string RoutingLeanPath = "tools/Evm/Lean/Eip803x/Generated/SystemTransactionRoutingKernel.lean";
    internal const string RoutingRefinementPath = "tools/Evm/Lean/Eip803x/Refinement/SystemTransactionRouting.lean";

    private const int SchemaVersion = 2;
    private const string ExtractorVersion = "2.0.0";
    private const string RoslynVersion = "5.6.0.0";
    private const string LanguageVersionText = "14.0";
    private const string Kernel = "Nethermind ordinary standard-mainnet simple-transfer completion";
    private const string AcceptanceState = "hash-pinned-audited-handwritten-model-to-model-request-boundary-only";
    private const string IrFileName = "SimpleTransferCompletion.ir.json";
    private const string ManifestFileName = "SimpleTransferCompletion.source-manifest.json";
    private const string DefaultLeanRelativePath =
        "tools/Evm/Lean/SimpleTransferCompletionExtractor/Generated/SimpleTransferCompletion.lean";

    private static readonly SourceSpec[] SourceSpecs =
    [
        new(TransactionProcessorPath, "ExecuteSimpleTransfer and completion callers, including no-frame access completion", "0374c6f35a9a23361f37b41162ac281ca83b09846ab4295d5a592db6315c99cc"),
        new(MetricsPath, "empty-call metric implementation", "f056b9138021c4875f5a16893b243aac12f535c71ae88281987094fb6bcc2b90"),
        new(ExecutionOptionsPath, "execution option flags", "014a2598e1def284b7b98dc97185b6dbbc45ca04be4ab159e330a0675ac24f10"),
        new(SettlementKernelPath, "transaction settlement kernel body", "37add297aaaf07a4260b45c05c5f866a48f92eef2ac6fb16ef2bfd96103f8c44"),
        new(RoutingKernelPath, "normal-block counter routing body", "b884bb49ad7c109b907f4dfcac56a5eb13283d964310d45eb9ca85177ff5e5c1"),
        new(MainnetDiPath, "standard mainnet dependency route", "fce65ce5bb523c56fc8aa6ee0e4d092c940a5a90b174c820ffe262eeb5241c60"),
        new(GasPolicyInterfacePath, "gas-policy contract and default operations", "b4d26db87723243514f2f6b8a5e48d1176139105c3b012ad0f3956e59afc98a8"),
        new(EthereumGasPolicyPath, "Ethereum gas-policy operations", "3c31ba40c24a78bf11243b38859168350f02934806124e913da0560bc1a4350f"),
        new(StateGasChargeKernelPath, "state-gas charge kernel body", "ec3276958cbc6b0255fadedf15dbe49691195aa95570b332937f8d1703b103fd"),
        new(StateGasTransitionKernelPath, "state-gas transition kernel body", "4a31dc76fb8284a8ea5994de9a63810abe0fd7efbe6add4aa8b3d6cb564a75cc"),
        new(StateGasTransitionAdapterKernelPath, "state-gas transition adapter body", "ca16429e4cb1b728619b7fa0113b2302ca75600b0a32d9345e38721c7c2d7368"),
        new(GasCostInterfacePath, "gas-cost strategy contract", "16289cdc2f777a76fda3ad8ff4ce4275c30513aee04c9cf42d314cc6e1ec48c0"),
        new(TransactionGasInitializationKernelPath, "block-gas accounting kernel body", "65ab0742596ea7ebf49d2c79601eae00152d8d039ac8e417076bf63123be1e82"),
        new(WorldStateInterfacePath, "world-state adapter contract", "0680ebc7168472645d93df58d2b3240508c177361815c85cceb09cf960f189c7"),
        new(ReadOnlyStateProviderInterfacePath, "read-only state provider contract", "2bab0038c33735b330e25f6300384450835c092a5ff137935a1fd6c5ab58be68"),
        new(WorldStateExtensionsPath, "world-state extension forwarding bodies", "4c0a66d1de95b4340bc9a190f6bef29b6e4068ebffa19ebe9cce3f789131fc62"),
        new(WorldStatePath, "WorldState forwarding bodies", "2dec74bcc5748a1850d1bed1e8fe0221aed8c904bf2ba0c0827e76e603a71f18"),
        new(StateProviderPath, "StateProvider forwarding targets", "93eab6702f4e3ef6c52afb0f595ff2b0c17cdeabbce1de3f0507f2aa0e6f6858"),
        new(TransactionPath, "transaction value and fee projections", "3fdf94805739c6ccdfa8b4617c5d4cfa6bee9e9565f16d667b22106c434d6596"),
        new(TransactionStdPath, "transaction standard partial projection", "b08ebdcf7fb167cf028488d393037278e9dc33b8f2c927f96440ea28572530d9"),
        new(AddressPath, "Address.SystemUser transfer-log sender constant", "4b81e382e508a725d540880ec38ec36c796f81cfae3d48e23708f50a3ed9a3fc"),
        new(GasCostOfPath, "GasCostOf.NewAccountState schedule constant", "10f1f1a29e7bf10a8220422a6f7571af60df4f05ad4075268e3b26559a73c6f5"),
        new(BlockHeaderPath, "header gas and beneficiary projection", "f354ddd2d45afc8774739ac46a10535b915871ce4ad11d996d0ab972cf9e7349"),
        new(TransactionSubstatePath, "receipt-erased substate constructor and observations", "1312a5f1a9990976670914fcc0a6ad2c77dd92d768d92fd91b28f27fefe66a00"),
        new(TxTracerPath, "transaction tracer callback contract", "9f2550f8c3407f494452386699040feab8aa597437b1cd4180d038d9fd0a9169"),
        new(TxTracerBasePath, "transaction tracer base implementation", "02cead9095a0cd5da4879bef2299dca35d3d4bbef2835d30448f03ef3f9e3d2d"),
        new(NullTxTracerPath, "null state tracer branch", "fc5da81812866c5f0814729a14ed8cb2d7c669b7179e41cdc3d6f2ceaa7ba224"),
        new(WorldStateTracerPath, "world-state tracer callback type", "d4e95b993245b751fc97f71f3d47bc93bc477d79662ed17755e426d49e76335f"),
        new(StackAccessTrackerPath, "access warming and observation body", "ed5cdfdfe2ebf4d8c9f651572927efe7bdb5c75107db63a40577c5ec3ec2882f"),
        new(EvmObjectPoolPath, "EVM object pool implementation", "e38caf1904887b647b11b63bfe241c184958796b9fc3f1446c456a9373daadd8"),
        new(LogEntryPath, "transfer log value type", "e57411211976798af161df83a2cc338972d5846336603004f9eecc5e862a748a"),
        new(JournalCollectionPath, "substate log collection", "5c1395b025da5a03749a5b64da804e53c6ff0a7aec4c02d30fd4b1c0e059c720"),
        new(AccessListPath, "transaction access-list value type", "0e40456ef7dd5b34056b92e1d81cba8b82787a6b4108772579d93bedbe710a0f"),
        new(GasConsumedPath, "gas-consumed result value type", "1dc4e78d5a17056dcb00d496136a32a899245f3a7b3e5a9eef74c4760ef68686"),
        new(StatusCodePath, "transaction status values", "e896f55406c9fc1ce99199d80bea69b87fa8891c23afef59712b87cadb971190"),
        new(TransferLogPath, "EIP-7708 transfer log constructor", "4449d807777bf8eb49e4e1a7355c37998ca57c1bf7cea2542461028879aba315"),
        new(IReleaseSpecPath, "release specification feature flags", "3f44b3da5614c94d59eae2e4a6266a9e698614f75a7b5a9894a4828b0021b18a"),
        new(EvmExceptionPath, "EVM exception values", "8c503bfbe0c60c41597e64c96b5d823dc98b934b4511730217eceaaf13837450"),
        new(ExecutionTypePath, "action execution type", "b6689c7c928182f6d115ca57cc8ac45bb9312359a026704f2eec77b921028013"),
        new(ITransactionProcessorPath, "processor DI contract", "3cae1430ba38d54f9867b714efdc823f9db126b1bbf0e45eae59439701459e95"),
        new(SystemTransactionProcessorPath, "system processor semantic closure", "10c7370e5f6d5ca25e4cd0801967083f97f432e211314dc40126a18fbea48a95"),
        new(CodeRepositoryInterfacePath, "code repository contract retained by the handoff", "e0669f612f31fe4a353a60896f40325e493e1ff925a92614ad73f49bf1435015"),
        new(CodeInfoPath, "code emptiness predicate retained by the handoff", "41e5e0316853f5970b5f868a6a47e24105dcb6eeb9be9608378482d74bcbd254"),
        new(JumpDestinationAnalyzerPath, "jump-destination analyzer source closure", "7347218843f5e110c943753763cf6a962c5bde347d59c2e025dda30b8633a751"),
        new(JumpDestinationAnalyzerStandardPath, "standard jump-destination analyzer source closure", "7a90ac0ea3718878fd1d6a36bb5ec7a8ff23a4c2e4629ce660e9b96e6f95c0f2"),
        new(IntrinsicGasCalculatorPath, "intrinsic-gas source closure", "886772dce848f566ba97a112a9b12b9f535a2d951dace63647136ababd822909"),
        new(AccountAccessPricingKernelPath, "account-access gas source closure", "1988faefb55bb65bf4e915e77e99966f43b88675dec47689e04378bde4f31c92"),
        new(PrecompileGasPricingKernelPath, "precompile gas source closure", "dd23b8bd461a13c75ec203b1dbe853a885f61640edf4d47287c1dadd0d9ff576"),
        new(SStorePricingKernelPath, "SSTORE gas source closure", "6eb99aee5247c9d5d7c35659c41628d758b672923d0fc5ec76e7d5cb9a30a125"),
        new(CodeRepositoryPath, "normal code repository route", "76734fad45dd415deaf016b1b9ce6670580042e09f5493253b06c7a9c9f622b7"),
        new(CacheCodeRepositoryPath, "cached code repository route", "498e32be24e098f1540273f9780e548b52fbbbbc8cb55fbf7b8335eb7743d0f3"),
        new(VirtualMachineInterfacePath, "virtual-machine generic contract", "330da5f31bd7aca4f4212a42c912c33cf0ac5b3def0689b7f9985cd6510aa56d"),
        new(VirtualMachineSignaturePath, "virtual-machine static signature closure", "45edba3691e09185e749485785990662ddea1af849bc6e4564f92b68a5137a6b"),
        new(PartialStorageProviderPath, "partial storage provider contract", "0ade22ba2312b9ac665ca03afa2547f6f884232f20d3a58867b2c00402daa085"),
        new(PersistentStorageProviderPath, "persistent storage provider implementation", "fd04e010e7088473b3a007f2cb8d688cf1d389f42866c60af8baa8860202683a"),
        new(PersistentStorageProviderStandardPath, "persistent storage standard implementation", "c7fd031ff5561de32af150234720b9d41d509bc2f007b8ef743cf6e28862a0d9"),
        new(TransientStorageProviderPath, "transient storage provider implementation", "d9731e1bae54aeb296c7a3a22baae18c35a043167623149888b1cfa5f098ba4d"),
        new(ChangeTypePath, "state change kind", "ff3309a02a15e98db7dd4a8c262c66f7380edee3e992c39220e7a84db0e6cbe6"),
        new(DispatchFlagsStandardPath, "standard dispatch flags semantic closure", "99c58367e5b90fa5904484f6e2fbf10745a54f6cd7d07275810c27028ad0eb2b"),
        new(MetricsStandardPath, "standard metrics semantic closure", "e36b3f0f33db29043d820db99cf0577a3c7010bf1b93193ebeb92e954a54300e"),
        new(LocalMetricsFlushStandardPath, "standard local metrics semantic closure", "1ab3bfdb432a96003db3eb526a0b5006eec0f12c980eb55c5921df2b4bb266f3"),
    ];

    private static readonly DependencySpec[] DependencySpecs =
    [
        new(ReceiptAdapterPath, "exact Consensus receipt adapter source projection", ReceiptAdapterSha256),
        new(SettlementLeanPath, "byte-pinned generated settlement kernel identity", "21e4c3ef135618a96bd5e7d2111e0be0bb272840b7d9ca427f17ef7777e2401c"),
        new(SettlementRefinementPath, "byte-pinned settlement refinement identity", "bb34d4962f22231ae1d466dfe0302ebaa40ba07624e6f43e28f282b9700b0f25"),
        new(StateChargeLeanPath, "byte-pinned generated state-charge kernel identity", "d6a09be29c449e3f005d84cde988a619e03c4d2b0a69fcf0989f879b2fc87337"),
        new(StateChargeRefinementPath, "byte-pinned state-charge refinement identity", "4cb359f611ef04300618f9109ac89ca6b32c857c8e9e6c2305e11a0323d57ead"),
        new(RoutingLeanPath, "byte-pinned generated routing predicate identity", "8d4f20165c64a4ec0f04739b54275976aef72d3c7889e7b474823617d6fc30b3"),
        new(RoutingRefinementPath, "byte-pinned routing refinement identity", "c72acd94a1d8c1fe62101330213fca9c2cd65595f306c13c96b19fc89b951937"),
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 1024,
        WriteIndented = true,
        NewLine = "\n",
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default
        .WithLanguageVersion(LanguageVersion.CSharp14)
        .WithDocumentationMode(DocumentationMode.Parse)
        .WithKind(SourceCodeKind.Regular);

    private static readonly HashSet<SyntaxKind> ForbiddenNodeKinds =
    [
        SyntaxKind.WhileStatement,
        SyntaxKind.DoStatement,
        SyntaxKind.ForStatement,
        SyntaxKind.ForEachStatement,
        SyntaxKind.ForEachVariableStatement,
        SyntaxKind.SwitchStatement,
        SyntaxKind.TryStatement,
        SyntaxKind.GotoStatement,
        SyntaxKind.YieldReturnStatement,
        SyntaxKind.YieldBreakStatement,
    ];

    internal static ExtractionResult Extract(string repoRoot, string outputDirectory, string? leanOutputPath = null) =>
        ExtractCore(repoRoot, outputDirectory, leanOutputPath, requirePinnedSources: true, write: true);

    internal static ExtractionResult ExtractPinnedSourcesForTest(
        string repoRoot, string outputDirectory, string leanOutputPath, IReadOnlyDictionary<string, string> overrides) =>
        ExtractCore(repoRoot, outputDirectory, leanOutputPath, requirePinnedSources: true, write: true, overrides);

    /// <summary>Extracts a test fixture while allowing a deliberately mutated production source.</summary>
    internal static ExtractionResult ExtractWithoutPinnedSourcesForTest(
        string repoRoot,
        string outputDirectory,
        string? leanOutputPath = null,
        IReadOnlyDictionary<string, string>? overrides = null) =>
        ExtractCore(repoRoot, outputDirectory, leanOutputPath, requirePinnedSources: false, write: true, overrides);

    internal static void RequireSemanticCompilationForTest(string repoRoot, IReadOnlyDictionary<string, string>? overrides = null)
    {
        string root = Path.GetFullPath(repoRoot);
        ValidateOverrideKeys(root, overrides);
        _ = LoadCompilerClosure(root, ReadSources(root, overrides), overrides);
    }

    internal static StageASourcePlan RequireStageASourcePlanForTest(
        string repoRoot, IReadOnlyDictionary<string, string>? overrides = null)
    {
        string root = Path.GetFullPath(repoRoot);
        ValidateOverrideKeys(root, overrides);
        string adapterHash = ValidateReceiptAdapterProjection(root);
        CompilerClosure accepted = CompilerSources.Load(root);
        StageAMethodPlan[] expected = LowerStageAMethods(accepted);
        CompilerClosure candidate = overrides is null ? accepted : CompilerSources.Load(root, overrides);
        StageAMethodPlan[] actual = overrides is null ? expected : LowerStageAMethods(candidate);
        if (expected.Length != actual.Length)
            throw new ExtractionException("Stage-A source plan method roster changed.");
        for (int index = 0; index < actual.Length; index++)
        {
            StageAMethodPlan source = expected[index];
            StageAMethodPlan plan = actual[index];
            if (source.Path != plan.Path || source.Owner != plan.Owner || source.Member != plan.Member ||
                source.ParameterCount != plan.ParameterCount || source.BodySha256 != plan.BodySha256 ||
                !StageAStatementLedger(source.Statements).SequenceEqual(StageAStatementLedger(plan.Statements)) ||
                !StageAOperationLedger(source.Effects).SequenceEqual(StageAOperationLedger(plan.Effects)) ||
                !StageAOperationLedger(source.Branches).SequenceEqual(StageAOperationLedger(plan.Branches)) ||
                !StageAOperationLedger(source.Operands).SequenceEqual(StageAOperationLedger(plan.Operands)))
                throw new ExtractionException($"Stage-A typed source plan changed: {plan.Owner}.{plan.Member}/{plan.ParameterCount}.");
        }
        return new StageASourcePlan(
            "standard-mainnet sequential Commit, no restore, tracing-state=false, simple-recipient, post-successful-nonce",
            adapterHash,
            [
                "Consensus receipt adapter exact source projection; runtime selection and normal-return hooks are external premises",
                "receipt adapter passes receiptsTracer itself to Execute after StartNewTxTrace",
                "ITransactionProcessorExtensions.Execute passes exact ExecutionOptions.Commit",
                "Process/ExecuteCore excludes the system processor for the selected ordinary transaction",
                "tracer.IsTracingState=false; no pre-execution commit request on the selected simple-recipient path",
                "the selected AuthorizationList-null route has a zero intrinsic state baseline; its prefix provenance remains an explicit obligation",
            ],
            actual);
    }

    internal static void RequireMissingCompiledEvmTreeForTest(string repoRoot)
    {
        SourceFile[] observed = ReadSources(repoRoot);
        CompilerClosure compiler = LoadCompilerClosure(repoRoot, observed);
        CompilerClosure missing = compiler with
        {
            Sources = compiler.Sources.Where(static source => source.RelativePath != TransactionProcessorPath).ToArray(),
        };
        SourceFile[] sources = observed.Select(source =>
            missing.Sources.FirstOrDefault(compiled => compiled.RelativePath == source.RelativePath) is SourceFile compiled
                ? compiled with { Role = source.Role } : source).ToArray();
        _ = BuildSemanticContext(sources, missing);
    }

    internal static void RequireOwnedExpressionForTest(string methodSource, string expression) =>
        _ = FindExactExpression(ParseTestMethod(methodSource), expression);

    internal static void RequireOwnedInvocationForTest(string methodSource, string name) =>
        _ = FindInvocation(ParseTestMethod(methodSource), name);

    internal static void RequireCfgOwnershipForTest(string methodSource, string name)
    {
        MethodDeclarationSyntax method = ParseTestMethod(methodSource);
        InvocationExpressionSyntax invocation = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(node => InvocationName(node) == name);
        _ = RequireMethodCfgOwner(invocation, "Required test source node");
    }

    internal static void RequireFiniteSyntaxForTest(string methodSource) =>
        RejectForbiddenSyntax(ParseTestMethod(methodSource));

    private static MethodDeclarationSyntax ParseTestMethod(string source) =>
        SyntaxFactory.ParseCompilationUnit("class C { " + source + " }").DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Single();

    internal static void PublishArtifactsForTest(string repoRoot, string outputDirectory, string leanOutputPath,
        byte[] ir, byte[] lean, byte[] manifest, Action? afterIr = null) =>
        PublishArtifacts(Paths(Path.GetFullPath(repoRoot), outputDirectory, leanOutputPath), ir, lean, manifest, afterIr);

    internal static void ValidateExistingArtifacts(string repoRoot, string outputDirectory, string? leanOutputPath)
    {
        string root = Path.GetFullPath(repoRoot);
        BuiltArtifacts built = BuildArtifacts(root, requirePinnedSources: true);
        ArtifactPaths paths = Paths(root, outputDirectory, leanOutputPath);
        CompareBytes(paths.IrPath, built.IrBytes, "IR");
        CompareBytes(paths.ManifestPath, built.ManifestBytes, "manifest");
        CompareBytes(paths.LeanPath, built.LeanBytes, "Lean");
    }

    internal static void ValidateSerializedIr(byte[] admittedIr, byte[] candidateIr)
    {
        IrDocument source = DeserializeIr(admittedIr);
        IrDocument candidate = DeserializeIr(candidateIr);
        if (!Serialize(source).AsSpan().SequenceEqual(Serialize(candidate)))
            throw new ExtractionException("Candidate simple-transfer IR differs from the admitted model-boundary IR.");
    }

    internal static void ValidateSerializedManifest(byte[] admittedManifest, byte[] candidateManifest)
    {
        Manifest source = DeserializeManifest(admittedManifest);
        Manifest candidate = DeserializeManifest(candidateManifest);
        if (!Serialize(source).AsSpan().SequenceEqual(Serialize(candidate)))
            throw new ExtractionException("Candidate simple-transfer manifest differs from the admitted model-boundary manifest.");
    }

    internal static byte[] EmitLeanForTest(byte[] serializedIr)
    {
        IrDocument document = DeserializeIr(serializedIr);
        return LeanEmitter.Emit(document, Sha256(serializedIr));
    }

    private static ExtractionResult ExtractCore(
        string repoRoot,
        string outputDirectory,
        string? leanOutputPath,
        bool requirePinnedSources,
        bool write,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        string root = Path.GetFullPath(repoRoot);
        BuiltArtifacts built = BuildArtifacts(root, requirePinnedSources, overrides);
        ArtifactPaths paths = Paths(root, outputDirectory, leanOutputPath);
        if (write)
            PublishArtifacts(paths, built.IrBytes, built.LeanBytes, built.ManifestBytes);

        return new ExtractionResult(
            paths.IrPath,
            paths.ManifestPath,
            paths.LeanPath,
            built.Document.Sources.Length,
            built.Document.Completion.Operations.Length,
            built.Document.Completion.Effects.Length,
            Sha256(built.IrBytes),
            Sha256(built.ManifestBytes),
            Sha256(built.LeanBytes));
    }

    private static BuiltArtifacts BuildArtifacts(string root, bool requirePinnedSources, IReadOnlyDictionary<string, string>? overrides = null)
    {
        ValidateOverrideKeys(root, overrides);
        SourceFile[] observed = ReadSources(root, overrides);
        if (requirePinnedSources)
        {
            foreach ((SourceFile source, SourceSpec spec) in observed.Zip(SourceSpecs))
            {
                if (!string.Equals(source.Sha256, spec.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new ExtractionException($"Source drift in {source.RelativePath}: expected {spec.ExpectedSha256}, found {source.Sha256}.");
            }
        }

        DependencyIdentity[] dependencies = ReadDependencies(root);
        CompilerClosure compiler = LoadCompilerClosure(root, observed, overrides);
        SourceFile[] sources = observed.Select(source =>
            compiler.Sources.FirstOrDefault(compiled => compiled.RelativePath == source.RelativePath) is SourceFile compiled
                ? compiled with { Role = source.Role } : source).ToArray();
        SemanticContext context = BuildSemanticContext(sources, compiler);
        (CompletionShape completion, RouteShape route) = LowerCompletion(sources, context);
        ValidateCompletion(completion);
        ValidateRoute(route);
        StageASourcePlan stageAPlan = BuildStageASourcePlan(root, compiler);

        SourceIdentity[] sourceIdentities = sources
            .Select(static source => new SourceIdentity(source.RelativePath, source.Role, source.Sha256))
            .ToArray();
        string sourceClosureHash = SourceClosureHash(sourceIdentities, compiler.EffectiveSources, compiler.References);
        IrDocument document = new(
            SchemaVersion,
            ExtractorVersion,
            RoslynVersion,
            LanguageVersionText,
            Kernel,
            AcceptanceState,
            sourceClosureHash,
            sourceIdentities,
            dependencies,
            route,
            completion,
            [
                "UInt256 value, balance, storage-key, fee, refund-product, and fee-sum operands carry explicit <= UInt256.MaxValue premises",
                "UInt64 gas operands and derived header/cumulative counters carry explicit <= UInt64.MaxValue premises",
                "Int64 state-gas inputs and successful charge arithmetic carry explicit Int64 range premises",
                "TryConsumeStateGas is an ordered ref-state transition; its returned bool controls the OOG branch",
                "Refund invokes the exact byte-pinned TransactionSettlementKernel.Calculate projection",
                "PayFees caps collected base fee with min(header.BaseFeePerGas,effectiveGasPrice)",
                "EIP-8037 block counters use execution/state components; pre-EIP-8037 uses EffectiveBlockGas",
            ],
            [
                "production bodies after the admitted call/order boundary are not semantically lowered; the theorem is model-to-model only",
                "world-state reads/writes/roots and tracer values are normal-return request/oracle inputs, not proved live adapter results",
                "ReportAccess is interpreted extensionally as address/storage sets; live HashSet enumeration order is outside this boundary",
                "transfer topic fields use a scalar Address.ToHash().ToHash256() projection; production zero-left-padding needs an explicit UInt160/byte-encoding bridge",
                "TransactionResult is reduced to the status/receipt projection; CLR exception and description fields are outside this boundary",
                "the Lean universal theorem requires TraceAdapter.normalReturn=true; callback exceptions and partial callback prefixes are outside this normal-return request-issuance boundary",
                "ExecuteEvmCall.CompleteWithoutFrame goto sites are source-bound without a CFG-edge claim; SimpleTransfer.run does not execute the VM path",
                "receipt continuation carries MarkAsFailed/MarkAsSuccess inputs; receipt folding and roots are erased",
                "the direct caller and byte-pinned kernel identities do not by themselves prove production-body composition",
                "standard-mainnet Init registrations are exact source-order projections; runtime DI resolution is an external route premise",
            ],
            [
                "receipt encoding, receipt roots, state-root calculation internals",
                "VM execution, crypto, CLR, database, trie, system, XDC, Taiko, parallel, and BAL paths",
            ],
            compiler.EffectiveSources,
            compiler.References,
            stageAPlan);
        ValidateIr(document, compiler);
        byte[] irBytes = Serialize(document);
        byte[] leanBytes = LeanEmitter.Emit(document, Sha256(irBytes));
        SourceBinding[] allBindings = completion.MethodBindings
            .Concat(completion.RouteBindings)
            .Concat(completion.Stages.Select(static stage => stage.Binding))
            .Concat(completion.Branches.Select(static branch => branch.Binding))
            .Concat(completion.Effects.Select(static effect => effect.Binding))
            .Concat(completion.Operations.Select(static operation => operation.Binding))
            .Concat(completion.Adapters.SelectMany(static adapter => adapter.Bindings))
            .Distinct()
            .ToArray();
        string irSha = Sha256(irBytes);
        Manifest manifest = new(
            SchemaVersion,
            ExtractorVersion,
            RoslynVersion,
            LanguageVersionText,
            Kernel,
            AcceptanceState,
            sourceClosureHash,
            sourceIdentities,
            dependencies,
            allBindings,
            new(IrFileName, irSha),
            new(Path.GetFileName(DefaultLeanRelativePath), Sha256(leanBytes)),
            CombinedSourceHash(sourceIdentities, dependencies),
            irSha,
            compiler.EffectiveSources,
            compiler.References,
            Sha256(Serialize(stageAPlan)));
        ValidateManifest(manifest, compiler);
        byte[] manifestBytes = Serialize(manifest);
        return new BuiltArtifacts(document, irBytes, manifestBytes, leanBytes);
    }

    private static (CompletionShape Completion, RouteShape Route) LowerCompletion(
        SourceFile[] sources,
        SemanticContext context)
    {
        SourceFile processor = FindSource(sources, TransactionProcessorPath);
        MethodDeclarationSyntax process = FindMethod(processor, "TransactionProcessorBase", "Process", 3);
        SourceFile gasPolicy = FindSource(sources, EthereumGasPolicyPath);
        SourceFile gasInterface = FindSource(sources, GasPolicyInterfacePath);
        SourceFile gasCost = FindSource(sources, GasCostOfPath);
        SourceFile worldInterface = FindSource(sources, WorldStateInterfacePath);
        SourceFile readOnlyStateProviderInterface = FindSource(sources, ReadOnlyStateProviderInterfacePath);
        SourceFile worldExtensions = FindSource(sources, WorldStateExtensionsPath);
        SourceFile world = FindSource(sources, WorldStatePath);
        SourceFile stateProvider = FindSource(sources, StateProviderPath);
        SourceFile address = FindSource(sources, AddressPath);
        SourceFile txSubstate = FindSource(sources, TransactionSubstatePath);
        SourceFile tracer = FindSource(sources, TxTracerPath);
        SourceFile transferLog = FindSource(sources, TransferLogPath);
        SourceFile routeSource = FindSource(sources, MainnetDiPath);
        SourceFile routingKernel = FindSource(sources, RoutingKernelPath);
        SourceFile settlement = FindSource(sources, SettlementKernelPath);
        SourceFile metrics = FindSource(sources, MetricsPath);
        SourceFile stateChargeKernel = FindSource(sources, StateGasChargeKernelPath);
        SourceFile transactionGasInitializationKernel = FindSource(sources, TransactionGasInitializationKernelPath);
        SourceFile evmException = FindSource(sources, EvmExceptionPath);
        SourceFile accessTracker = FindSource(sources, StackAccessTrackerPath);

        MethodDeclarationSyntax simple = FindMethod(processor, "TransactionProcessorBase", "ExecuteSimpleTransfer", 15);
        SourceBinding[] callerBindings = AdmitSimpleTransferCaller(processor, context);
        MethodDeclarationSyntax executeEvmCall = FindUniqueMethod(processor, "ExecuteEvmCall");
        MethodDeclarationSyntax actionStart = FindMethod(processor, "TransactionProcessorBase", "TraceSimpleTransferActionStart", 5);
        MethodDeclarationSyntax accessReport = FindMethod(processor, "TransactionProcessorBase", "ReportSimpleTransferAccess", 4);
        MethodDeclarationSyntax warmAccesses = FindMethod(processor, "TransactionProcessorBase", "WarmUpTxAccesses", 5);
        MethodDeclarationSyntax headerFees = FindMethod(processor, "TransactionProcessorBase", "UpdateHeaderGasUsedAndPayFees", 11);
        MethodDeclarationSyntax finalize = FindMethod(processor, "TransactionProcessorBase", "FinalizeTransaction", 12);
        MethodDeclarationSyntax payValue = FindMethod(processor, "TransactionProcessorBase", "PayValue", 3);
        MethodDeclarationSyntax payFees = FindMethod(processor, "TransactionProcessorBase", "PayFees", 10);
        MethodDeclarationSyntax refund = FindMethod(processor, "TransactionProcessorBase", "Refund", 12);
        MethodDeclarationSyntax payRefund = FindMethod(processor, "TransactionProcessorBase", "PayRefund", 3);
        MethodDeclarationSyntax shouldRefund = FindMethod(processor, "TransactionProcessorBase", "ShouldRefundGas", 3);
        MethodDeclarationSyntax metric = FindMethod(metrics, "Metrics", "IncrementEmptyCalls", 0);
        MethodDeclarationSyntax stateCharge = FindMethod(gasPolicy, "EthereumGasPolicy", "TryConsumeStateGas", 2);
        MethodDeclarationSyntax policyRemaining = FindMethod(gasPolicy, "EthereumGasPolicy", "GetRemainingGas", 1);
        MethodDeclarationSyntax policyClear = FindMethod(gasPolicy, "EthereumGasPolicy", "ClearExecutionGas", 1);
        MethodDeclarationSyntax policyReservoir = FindMethod(gasPolicy, "EthereumGasPolicy", "GetStateReservoir", 1);
        MethodDeclarationSyntax policyStateUsed = FindMethod(gasPolicy, "EthereumGasPolicy", "GetStateGasUsed", 1);
        PropertyDeclarationSyntax effectiveBlockGas = FindProperty(FindSource(sources, GasConsumedPath), "GasConsumed", "EffectiveBlockGas");
        MethodDeclarationSyntax policyCombine = FindMethod(gasPolicy, "EthereumGasPolicy", "CombineBlockGas", 2);
        MethodDeclarationSyntax interfacePreRefund = FindMethod(gasInterface, "IGasPolicy", "GetPreRefundGas", 2);
        MethodDeclarationSyntax interfaceStateCost = FindMethod(gasInterface, "IGasPolicy", "GetNewAccountStateCost", 0);
        MethodDeclarationSyntax interfaceReservoir = FindMethod(gasInterface, "IGasPolicy", "GetStateReservoir", 1);
        MethodDeclarationSyntax interfaceStateUsed = FindMethod(gasInterface, "IGasPolicy", "GetStateGasUsed", 1);
        VariableDeclaratorSyntax newAccountStateCost = FindField(gasCost, "GasCostOf", "NewAccountState");
        VariableDeclaratorSyntax systemUserHex = FindField(address, "Address", "SystemUserHex");
        MethodDeclarationSyntax worldCommit = FindMethod(worldInterface, "IWorldState", "Commit", 4);
        MethodDeclarationSyntax worldExtensionCommit = FindMethod(worldExtensions, "WorldStateExtensions", "Commit", 4);
        MethodDeclarationSyntax worldExtensionAdd = FindMethod(worldExtensions, "WorldStateExtensions", "AddToBalanceAndCreateIfNotExists", 4);
        MethodDeclarationSyntax worldExtensionSubtract = FindMethod(worldExtensions, "WorldStateExtensions", "SubtractFromBalance", 4);
        MethodDeclarationSyntax worldExtensionAddBalance = FindMethod(worldExtensions, "WorldStateExtensions", "AddToBalance", 4);
        MethodDeclarationSyntax worldReset = FindMethod(world, "WorldState", "Reset", 1);
        MethodDeclarationSyntax worldAdd = FindMethod(world, "WorldState", "AddToBalanceAndCreateIfNotExists", 4);
        MethodDeclarationSyntax worldSubtract = FindMethod(world, "WorldState", "SubtractFromBalance", 4);
        MethodDeclarationSyntax worldCommitBody = FindMethod(world, "WorldState", "Commit", 4);
        MethodDeclarationSyntax worldRecalculateRoot = FindMethod(world, "WorldState", "RecalculateStateRoot", 0);
        PropertyDeclarationSyntax worldStateRoot = FindProperty(world, "WorldState", "StateRoot");
        MethodDeclarationSyntax providerCommit = FindMethod(stateProvider, "StateProvider", "Commit", 4);
        MethodDeclarationSyntax worldIsDeadAccount = FindMethod(world, "WorldState", "IsDeadAccount", 1);
        MethodDeclarationSyntax providerIsDeadAccount = FindMethod(stateProvider, "StateProvider", "IsDeadAccount", 1);
        MethodDeclarationSyntax providerRecalculateRoot = FindMethod(stateProvider, "StateProvider", "RecalculateStateRoot", 0);
        MethodDeclarationSyntax interfaceIsDeadAccount = FindMethod(readOnlyStateProviderInterface, "IReadOnlyStateProvider", "IsDeadAccount", 1);
        PropertyDeclarationSyntax providerStateRoot = FindProperty(stateProvider, "StateProvider", "StateRoot");
        ConstructorDeclarationSyntax substateCtor = FindConstructor(txSubstate, "TransactionSubstate", 8);
        MethodDeclarationSyntax createTransfer = FindMethod(transferLog, "TransferLog", "CreateTransfer", 3);
        MethodDeclarationSyntax createTransferInternal = FindMethod(transferLog, "TransferLog", "CreateTransferInternal", 4);
        MethodDeclarationSyntax stateChargeKernelTryCharge = FindMethod(stateChargeKernel, "StateGasChargeKernel", "TryCharge", 6);
        MethodDeclarationSyntax stateChargeKernelSpill = FindMethod(stateChargeKernel, "StateGasChargeKernel", "CalculateSpill", 2);
        MethodDeclarationSyntax blockGasCombine = FindMethod(transactionGasInitializationKernel, "BlockGasAccountingKernel", "Combine", 2);
        MethodDeclarationSyntax settlementCalculate = FindMethod(settlement, "TransactionSettlementKernel", "Calculate", 13);
        MethodDeclarationSyntax fastToString = FindMethod(evmException, "EvmExceptionTypeExtensions", "FastToString", 1);
        PropertyDeclarationSyntax substateIsError = FindProperty(txSubstate, "TransactionSubstate", "IsError");
        PropertyDeclarationSyntax substateError = FindProperty(txSubstate, "TransactionSubstate", "Error");
        MethodDeclarationSyntax accessListWarmup = FindMethodWithParameterToken(accessTracker, "StackAccessTracker", "WarmUp", 1, "AccessList");
        MethodDeclarationSyntax addressWarmup = FindMethodWithParameterToken(accessTracker, "StackAccessTracker", "WarmUp", 1, "Address");
        MethodDeclarationSyntax route = FindMethod(routingKernel, "SystemTransactionRoutingKernel", "ParticipatesInNormalBlockCounters", 2);
        ValidateFiniteSyntax(simple, actionStart, accessReport, warmAccesses, headerFees, finalize, payValue, payFees, refund, payRefund, shouldRefund);
        ValidateAccessWarmup(warmAccesses, context);
        ValidateHeaderAndReceiptCompletion(headerFees, finalize, context);
        ValidateNewAccountStateCost(interfaceStateCost, newAccountStateCost, context);
        (string transferLogPayloadGrammar, string transferLogAddress, string transferSignature) =
            ValidateTransferLogPayload(transferLog, createTransfer, createTransferInternal, systemUserHex, context);
        NoFrameProof noFrameProof = ValidateNoFrameCompletion(executeEvmCall, context);

        List<SourceBinding> methodBindings = [];
        methodBindings.AddRange(callerBindings);
        void AddMember(SourceFile source, string owner, string member, SyntaxNode node, string role) =>
            methodBindings.Add(Bind(source, owner, member, role, node, context, requireSymbol: true));
        void AddMethod(SourceFile source, string owner, MethodDeclarationSyntax method, string role) =>
            AddMember(source, owner, method.Identifier.ValueText, method, role);

        AddMethod(processor, "TransactionProcessorBase<TGasPolicy>", simple, "target completion body");
        AddMethod(processor, "TransactionProcessorBase<TGasPolicy>", process, "standard ITransactionProcessor.Process entry body");
        AddMethod(processor, "TransactionProcessorBase<TGasPolicy>", executeEvmCall, "no-frame CFG source body");
        AddMethod(processor, "TransactionProcessorBase<TGasPolicy>", actionStart, "action-start body");
        AddMethod(processor, "TransactionProcessorBase<TGasPolicy>", accessReport, "access-report body");
        AddMethod(processor, "TransactionProcessorBase<TGasPolicy>", warmAccesses, "access-warming body");
        AddMethod(processor, "TransactionProcessorBase<TGasPolicy>", headerFees, "header-and-fees body");
        AddMethod(processor, "TransactionProcessorBase<TGasPolicy>", finalize, "finalization body");
        AddMethod(processor, "TransactionProcessorBase<TGasPolicy>", payValue, "value-payment body");
        AddMethod(processor, "TransactionProcessorBase<TGasPolicy>", payFees, "fee-payment body");
        AddMethod(processor, "TransactionProcessorBase<TGasPolicy>", refund, "refund body");
        AddMethod(processor, "TransactionProcessorBase<TGasPolicy>", payRefund, "refund-world body");
        AddMethod(processor, "TransactionProcessorBase<TGasPolicy>", shouldRefund, "refund guard body");
        AddMethod(metrics, "Metrics", metric, "metric body");
        AddMethod(gasPolicy, "EthereumGasPolicy", stateCharge, "state-charge body");
        AddMethod(gasPolicy, "EthereumGasPolicy", policyRemaining, "remaining-gas body");
        AddMethod(gasPolicy, "EthereumGasPolicy", policyClear, "clear-execution-gas body");
        AddMethod(gasPolicy, "EthereumGasPolicy", policyReservoir, "state-reservoir body");
        AddMethod(gasPolicy, "EthereumGasPolicy", policyStateUsed, "state-used body");
        AddMember(FindSource(sources, GasConsumedPath), "GasConsumed", "EffectiveBlockGas", effectiveBlockGas, "effective-block-gas property");
        AddMethod(gasPolicy, "EthereumGasPolicy", policyCombine, "block-gas combination body");
        AddMethod(gasInterface, "IGasPolicy<TSelf>", interfacePreRefund, "pre-refund-gas body");
        AddMethod(gasInterface, "IGasPolicy<TSelf>", interfaceStateCost, "new-account-cost body");
        AddMethod(gasInterface, "IGasPolicy<TSelf>", interfaceReservoir, "interface-reservoir body");
        AddMethod(gasInterface, "IGasPolicy<TSelf>", interfaceStateUsed, "interface-state-used body");
        AddMember(gasCost, "GasCostOf", "NewAccountState", newAccountStateCost, "new-account-state schedule constant");
        AddMethod(worldInterface, "IWorldState", worldCommit, "world commit contract");
        AddMethod(worldExtensions, "WorldStateExtensions", worldExtensionCommit, "world commit extension body");
        AddMethod(worldExtensions, "WorldStateExtensions", worldExtensionAdd, "world recipient extension body");
        AddMethod(worldExtensions, "WorldStateExtensions", worldExtensionSubtract, "world subtraction extension body");
        AddMethod(worldExtensions, "WorldStateExtensions", worldExtensionAddBalance, "world balance extension body");
        AddMethod(world, "WorldState", worldReset, "world reset body");
        AddMethod(world, "WorldState", worldAdd, "world recipient body");
        AddMethod(world, "WorldState", worldSubtract, "world subtraction body");
        AddMethod(world, "WorldState", worldCommitBody, "world commit body");
        AddMethod(world, "WorldState", worldRecalculateRoot, "world state-root recalculation body");
        AddMember(world, "WorldState", "StateRoot", worldStateRoot, "world post-recalculation state-root property");
        AddMethod(stateProvider, "StateProvider", providerCommit, "provider commit body");
        AddMethod(stateProvider, "StateProvider", providerRecalculateRoot, "provider state-root recalculation body");
        AddMember(stateProvider, "StateProvider", "StateRoot", providerStateRoot, "provider post-recalculation state-root property");
        AddMember(txSubstate, "TransactionSubstate", ".ctor", substateCtor, "substate constructor");
        AddMember(txSubstate, "TransactionSubstate", "IsError", substateIsError, "substate error predicate");
        AddMember(txSubstate, "TransactionSubstate", "Error", substateError, "substate error value");
        AddMethod(transferLog, "TransferLog", createTransfer, "transfer-log body");
        AddMethod(transferLog, "TransferLog", createTransferInternal, "transfer-log internal body");
        AddMethod(stateChargeKernel, "StateGasChargeKernel", stateChargeKernelTryCharge, "state-charge kernel body");
        AddMethod(stateChargeKernel, "StateGasChargeKernel", stateChargeKernelSpill, "state-charge spill body");
        AddMethod(transactionGasInitializationKernel, "BlockGasAccountingKernel", blockGasCombine, "block-gas max body");
        AddMethod(settlement, "TransactionSettlementKernel", settlementCalculate, "settlement kernel body");
        AddMethod(evmException, "EvmExceptionTypeExtensions", fastToString, "reflection-free exception name body");
        AddMethod(accessTracker, "StackAccessTracker", accessListWarmup, "access-list warming body");
        AddMethod(routingKernel, "SystemTransactionRoutingKernel", route, "normal-counter predicate");

        SourceBinding BindInvocation(SourceFile source, SyntaxNode parent, string name, string role, int occurrence = 0, bool requireSymbol = true) =>
            Bind(source, "TransactionProcessorBase<TGasPolicy>", name, role, FindInvocation(parent, name, occurrence), context, requireSymbol);
        SourceBinding BindObject(SourceFile source, SyntaxNode parent, string name, string role) =>
            Bind(source, "TransactionProcessorBase<TGasPolicy>", name, role, FindObject(parent, name), context, requireSymbol: true);
        SourceBinding BindMember(SourceFile source, SyntaxNode parent, string name, string role, int occurrence = 0) =>
            Bind(source, "TransactionProcessorBase<TGasPolicy>", name, role, FindMemberAccess(parent, name, occurrence), context, requireSymbol: false);
        SourceBinding BindCondition(SourceFile source, SyntaxNode parent, Func<TypedAstNode, bool> predicate, string name, string role, int occurrence = 0) =>
            Bind(source, "TransactionProcessorBase<TGasPolicy>", name, role, FindCondition(parent, context, predicate, occurrence), context, requireSymbol: false);

        Dictionary<string, SourceBinding> b = new(StringComparer.Ordinal);
        SourceBinding Add(string id, SourceBinding binding)
        {
            b.Add(id, binding);
            return binding;
        }

        Add("metricsIncrement", BindInvocation(processor, simple, "IncrementEmptyCalls", "Metrics.IncrementEmptyCalls", requireSymbol: true));
        Add("newAccountCost", BindInvocation(processor, simple, "GetNewAccountStateCost", "new-account state cost"));
        Add("stateCharge", BindInvocation(processor, simple, "TryConsumeStateGas", "new-account state charge"));
        Add("deadRecipient", BindInvocation(processor, simple, "IsDeadAccount", "dead-recipient query"));
        Add("payValue", BindInvocation(processor, simple, "PayValue", "PayValue request"));
        Add("recipientBalance", BindInvocation(processor, simple, "AddToBalanceAndCreateIfNotExists", "recipient balance request"));
        Add("actionStart", BindInvocation(processor, simple, "TraceSimpleTransferActionStart", "action-start request"));
        Add("clearExecutionGas", BindInvocation(processor, simple, "ClearExecutionGas", "OOG execution-gas clear"));
        Add("transferLog", BindInvocation(processor, simple, "CreateTransfer", "EIP-7708 transfer-log creation"));
        Add("reportLog", BindInvocation(processor, simple, "ReportLog", "transfer-log observation"));
        Add("substate", BindObject(processor, simple, "TransactionSubstate", "substate construction"));
        Add("actionError", BindInvocation(processor, simple, "ReportActionError", "OOG action observation"));
        Add("actionEnd", BindInvocation(processor, simple, "ReportActionEnd", "successful action observation"));
        Add("remainingGas", BindInvocation(processor, simple, "GetRemainingGas", "action-end gas observation"));
        Add("stateReservoir", BindInvocation(processor, simple, "GetStateReservoir", "post-intrinsic reservoir"));
        Add("refund", BindInvocation(processor, simple, "Refund", "settlement request"));
        Add("accessReport", BindInvocation(processor, simple, "ReportSimpleTransferAccess", "access report request"));
        Add("headerFees", BindInvocation(processor, simple, "UpdateHeaderGasUsedAndPayFees", "header and fee request"));
        Add("finalize", BindInvocation(processor, simple, "FinalizeTransaction", "finalization request"));

        Add("actionReport", BindInvocation(processor, actionStart, "ReportAction", "action trace callback"));
        Add("codeReport", BindInvocation(processor, actionStart, "ReportByteCode", "empty-code observation"));
        Add("accessWarmup", BindInvocation(processor, accessReport, "WarmUpTxAccesses", "access warming request"));
        Add("accessCallback", BindInvocation(processor, accessReport, "ReportAccess", "access observation"));
        // The live processor now has a VM no-frame completion label that reports the same access
        // observation. Bind that reachability edge explicitly, while keeping the VM outside this
        // simple-transfer request-boundary admission claim.
        Add("completeWithoutFrameAccess", Bind(
            processor,
            "TransactionProcessorBase<TGasPolicy>",
            "CompleteWithoutFrame.ReportAccess",
            "no-frame access completion callback",
            FindInvocationInLabel(processor, "CompleteWithoutFrame", "ReportAccess"),
            context,
            requireSymbol: true));
        Add("normalCounters", BindInvocation(processor, headerFees, "ParticipatesInNormalBlockCounters", "normal-counter guard"));
        Add("combineBlockGas", BindInvocation(processor, headerFees, "CombineBlockGas", "EIP-8037 block gas combination"));
        Add("payFees", BindInvocation(processor, headerFees, "PayFees", "fee payment request"));
        Add("restoreReset", BindInvocation(processor, finalize, "Reset", "restore reset"));
        Add("deleteCaller", BindInvocation(processor, finalize, "DeleteAccount", "restore caller deletion"));
        Add("restoreBalance", BindInvocation(processor, finalize, "AddToBalance", "reserved payment restoration"));
        Add("decrementNonce", BindInvocation(processor, finalize, "DecrementNonce", "restore nonce"));
        Add("restoreCommit", BindInvocation(processor, finalize, "Commit", "restore commit"));
        Add("commit", BindInvocation(processor, finalize, "Commit", "normal commit", occurrence: 1));
        Add("resetTransient", BindInvocation(processor, finalize, "ResetTransient", "transient reset"));
        Add("reapEmpty", BindInvocation(processor, finalize, "ReapEmptyAccounts", "build-up empty-account reap"));
        Add("recalculateRoot", BindInvocation(processor, finalize, "RecalculateStateRoot", "legacy receipt root request"));
        Add("receiptFailed", BindInvocation(processor, finalize, "MarkAsFailed", "receipt failure continuation"));
        Add("receiptSuccess", BindInvocation(processor, finalize, "MarkAsSuccess", "receipt success continuation"));
        Add("resultException", BindInvocation(processor, finalize, "EvmException", "exception result construction"));
        Add("fastToString", BindInvocation(processor, finalize, "FastToString", "receipt exception name"));
        Add("resultOk", BindMember(processor, finalize, "Ok", "success result construction"));
        Add("warmupAccess", BindInvocation(processor, warmAccesses, "WarmUp", "access-list warming", occurrence: 0));
        Add("coinbaseWarmup", BindInvocation(processor, warmAccesses, "WarmUp", "coinbase warming", occurrence: 1));
        Add("recipientWarmup", BindInvocation(processor, warmAccesses, "WarmUp", "recipient warming", occurrence: 2));
        Add("senderWarmup", BindInvocation(processor, warmAccesses, "WarmUp", "sender warming", occurrence: 3));
        Add("headerCounterGuard", BindCondition(processor, headerFees, static ast => HasInvocation(ast, "ParticipatesInNormalBlockCounters"), "normal-counter-condition", "normal-counter branch"));
        Add("eip8037Counter", BindCondition(processor, headerFees, static ast => HasReference(ast, "IsEip8037Enabled"), "EIP-8037-counter-condition", "state-counter branch"));
        Add("restoreBranch", BindCondition(processor, finalize, static ast => IsExactReference(ast, "restore"), "restore-condition", "restore branch"));
        Add("commitBranch", BindCondition(processor, finalize, static ast => IsExactReference(ast, "commit"), "commit-condition", "commit branch"));
        Add("buildUpBranch", BindCondition(processor, finalize, IsExactBuildUpCondition, "build-up-condition", "exact build-up branch"));
        Add("receiptBranch", BindCondition(processor, finalize, static ast => HasReference(ast, "IsTracingReceipt"), "receipt-condition", "receipt branch"));
        Add("newAccountBranch", BindCondition(processor, simple, static ast => HasInvocation(ast, "IsDeadAccount"), "new-account-condition", "new-account branch"));
        Add("recipientBranch", BindCondition(processor, simple, static ast => HasReference(ast, "senderIsRecipient") && HasReference(ast, "newAccountOutOfGas"), "recipient-write-condition", "recipient-write branch"));
        Add("traceBranch", BindCondition(processor, simple, static ast => IsExactReference(ast, "isTracingActions"), "action-trace-condition", "action trace branch"));
        Add("logBranch", BindCondition(processor, simple, static ast => HasReference(ast, "IsEip7708Enabled"), "transfer-log-condition", "transfer-log branch"));
        Add("accessHotGuard", BindCondition(processor, warmAccesses, static ast => IsNegatedReference(ast, "UseHotAndColdStorage"), "access-hot-condition", "hot/cold access guard"));
        Add("accessListGuard", BindCondition(processor, warmAccesses, static ast => IsReferenceOnly(ast, "UseTxAccessLists"), "access-list-condition", "transaction access-list guard"));
        Add("accessCoinbaseGuard", BindCondition(processor, warmAccesses, static ast => IsReferenceOnly(ast, "AddCoinbaseToTxAccessList"), "access-coinbase-condition", "coinbase access guard"));
        Add("accessRecipientGuard", BindCondition(processor, warmAccesses, static ast => IsReferenceOnly(ast, "warmUpRecipient"), "access-recipient-condition", "recipient access guard"));
        Add("statusBranch", BindMember(processor, simple, "newAccountOutOfGas", "status construction"));

        string DeclarationIdentity(SourceFile source, SyntaxNode declaration, string role)
        {
            if (!context.TryModel(source.Tree, out SemanticModel? found))
                return BindExternalProjection(source, "external", role, role, declaration, context.Compilation).TargetSymbolIdentity;
            SemanticModel model = found ?? throw new ExtractionException("Missing semantic model for " + source.RelativePath);
            ISymbol? symbol = declaration switch
            {
                MethodDeclarationSyntax method => model.GetDeclaredSymbol(method),
                ConstructorDeclarationSyntax constructor => model.GetDeclaredSymbol(constructor),
                PropertyDeclarationSyntax property => model.GetDeclaredSymbol(property),
                TypeDeclarationSyntax type => model.GetDeclaredSymbol(type),
                _ => null,
            };
            if (symbol is null || IsErrorSymbol(symbol))
                throw new ExtractionException($"Could not resolve the exact declaration symbol for {role}.");
            return SymbolIdentity(symbol);
        }

        Dictionary<string, HashSet<string>> targetContracts = new(StringComparer.Ordinal)
        {
            ["incrementEmptyCalls"] = [DeclarationIdentity(metrics, metric, "Metrics.IncrementEmptyCalls")],
            ["newAccountStateCost"] = [DeclarationIdentity(gasInterface, interfaceStateCost, "IGasPolicy.GetNewAccountStateCost")],
            ["consumeStateGas"] = [
                DeclarationIdentity(gasInterface, FindMethod(gasInterface, "IGasPolicy", "TryConsumeStateGas", 2), "IGasPolicy.TryConsumeStateGas"),
                DeclarationIdentity(gasPolicy, stateCharge, "EthereumGasPolicy.TryConsumeStateGas")],
            ["payValueRequest"] = [DeclarationIdentity(processor, payValue, "TransactionProcessorBase.PayValue")],
            ["addRecipientRequest"] = [DeclarationIdentity(worldExtensions, worldExtensionAdd, "WorldStateExtensions.AddToBalanceAndCreateIfNotExists"), DeclarationIdentity(worldInterface, FindMethod(worldInterface, "IWorldState", "AddToBalanceAndCreateIfNotExists", 4), "IWorldState.AddToBalanceAndCreateIfNotExists")],
            ["actionStartRequest"] = [DeclarationIdentity(processor, actionStart, "TransactionProcessorBase.TraceSimpleTransferActionStart")],
            ["clearExecutionGas"] = [DeclarationIdentity(gasInterface, FindMethod(gasInterface, "IGasPolicy", "ClearExecutionGas", 1), "IGasPolicy.ClearExecutionGas"), DeclarationIdentity(gasPolicy, policyClear, "EthereumGasPolicy.ClearExecutionGas")],
            ["transferLogRequest"] = [DeclarationIdentity(transferLog, createTransfer, "TransferLog.CreateTransfer")],
            ["substateConstruction"] = [DeclarationIdentity(txSubstate, substateCtor, "TransactionSubstate..ctor")],
            ["refundRequest"] = [DeclarationIdentity(processor, refund, "TransactionProcessorBase.Refund")],
            ["accessRequest"] = [DeclarationIdentity(processor, accessReport, "TransactionProcessorBase.ReportSimpleTransferAccess")],
            ["headerAndFees"] = [DeclarationIdentity(processor, headerFees, "TransactionProcessorBase.UpdateHeaderGasUsedAndPayFees")],
            ["finalizeRequest"] = [DeclarationIdentity(processor, finalize, "TransactionProcessorBase.FinalizeTransaction")],
            ["receiptFailure"] = [DeclarationIdentity(tracer, FindMethod(tracer, "ITxTracer", "MarkAsFailed", 5), "ITxTracer.MarkAsFailed")],
            ["receiptSuccess"] = [DeclarationIdentity(tracer, FindMethod(tracer, "ITxTracer", "MarkAsSuccess", 5), "ITxTracer.MarkAsSuccess")],
        };

        string accessListWarmupIdentity = DeclarationIdentity(accessTracker, accessListWarmup, "StackAccessTracker.WarmUp(AccessList)");
        string addressWarmupIdentity = DeclarationIdentity(accessTracker, addressWarmup, "StackAccessTracker.WarmUp(Address)");
        if (b["warmupAccess"].TargetSymbolIdentity != accessListWarmupIdentity ||
            b["coinbaseWarmup"].TargetSymbolIdentity != addressWarmupIdentity ||
            b["recipientWarmup"].TargetSymbolIdentity != addressWarmupIdentity ||
            b["senderWarmup"].TargetSymbolIdentity != addressWarmupIdentity)
            throw new ExtractionException("WarmUpTxAccesses resolved a different StackAccessTracker overload than its source declarations: " +
                b["warmupAccess"].TargetSymbolIdentity + " != " + accessListWarmupIdentity + "; " +
                b["coinbaseWarmup"].TargetSymbolIdentity + " != " + addressWarmupIdentity);
        string deadAccountIdentity = DeclarationIdentity(readOnlyStateProviderInterface, interfaceIsDeadAccount, "IReadOnlyStateProvider.IsDeadAccount");
        string routingIdentity = DeclarationIdentity(routingKernel, route, "SystemTransactionRoutingKernel.ParticipatesInNormalBlockCounters");
        if (b["deadRecipient"].TargetSymbolIdentity != deadAccountIdentity ||
            b["normalCounters"].TargetSymbolIdentity != routingIdentity)
            throw new ExtractionException("A completion predicate resolved a different source implementation than its admitted declaration: " +
                b["deadRecipient"].TargetSymbolIdentity + " != " + deadAccountIdentity + "; " +
                b["normalCounters"].TargetSymbolIdentity + " != " + routingIdentity);
        RequireTypedInvocationTarget(b["newAccountBranch"].TypedAst, "IsDeadAccount", b["deadRecipient"].TargetSymbol, "new-account predicate");
        RequireTypedInvocationTarget(b["headerCounterGuard"].TypedAst, "ParticipatesInNormalBlockCounters", b["normalCounters"].TargetSymbol, "counter predicate");

        OperationShape Operation(
            string id,
            int ordinal,
            string owner,
            string member,
            string[] inputs,
            string[] outputs,
            SourceBinding binding)
        {
            OperationLowering lowering = LowerTypedOperation(id, binding);
            if (!targetContracts.TryGetValue(id, out HashSet<string>? targets) || !targets.Contains(binding.TargetSymbolIdentity))
                throw new ExtractionException($"Typed operation {id} resolved a symbol outside the admitted receiver/owner set: {binding.TargetSymbol}; expected {string.Join(" || ", targets ?? [])}.");
            return new OperationShape(id, ordinal, owner, member, lowering.Formula, inputs, outputs, binding, lowering);
        }

        // Bind the adapter declarations/bodies, not just the calls made by the target method. This
        // makes the world, settlement, tracer, and DI request boundaries auditable without treating
        // their bytes as a proof of production composition.
        SourceBinding WorldMethod(SourceFile source, string owner, string name, string role, int parameters) =>
            Bind(source, owner, name, role, FindMethod(source, owner.Split('<')[0], name, parameters), context, requireSymbol: true);
        List<SourceBinding> adapterBindings =
        [
            Bind(world, "WorldState", "IsDeadAccount", "dead-account implementation", worldIsDeadAccount, context, requireSymbol: true),
            WorldMethod(world, "WorldState", "AddToBalanceAndCreateIfNotExists", "recipient implementation", 4),
            WorldMethod(world, "WorldState", "SubtractFromBalance", "sender implementation", 4),
            WorldMethod(world, "WorldState", "ResetTransient", "transient implementation", 0),
            WorldMethod(world, "WorldState", "ReapEmptyAccounts", "empty-account implementation", 0),
            WorldMethod(world, "WorldState", "RecalculateStateRoot", "state-root recalculation implementation", 0),
            WorldMethod(worldExtensions, "WorldStateExtensions", "Commit", "world commit extension adapter", 4),
            WorldMethod(worldExtensions, "WorldStateExtensions", "AddToBalance", "world balance extension adapter", 4),
            WorldMethod(worldExtensions, "WorldStateExtensions", "AddToBalanceAndCreateIfNotExists", "recipient extension adapter", 4),
            WorldMethod(worldExtensions, "WorldStateExtensions", "SubtractFromBalance", "sender extension adapter", 4),
            Bind(stateProvider, "StateProvider", "IsDeadAccount", "dead-account provider", providerIsDeadAccount, context, requireSymbol: true),
            WorldMethod(stateProvider, "StateProvider", "AddToBalanceAndCreateIfNotExists", "recipient provider", 4),
            WorldMethod(stateProvider, "StateProvider", "SubtractFromBalance", "sender provider", 4),
            WorldMethod(stateProvider, "StateProvider", "RecalculateStateRoot", "state-root recalculation provider", 0),
            WorldMethod(settlement, "TransactionSettlementKernel", "Calculate", "settlement implementation", 13),
            Bind(tracer, "ITxTracer", "ReportAction", "action callback declaration", FindMethod(tracer, "ITxTracer", "ReportAction", 7), context, requireSymbol: true),
            Bind(tracer, "ITxTracer", "ReportActionEnd", "action callback declaration", FindMethod(tracer, "ITxTracer", "ReportActionEnd", 2), context, requireSymbol: true),
            Bind(tracer, "ITxTracer", "ReportActionError", "action callback declaration", FindMethod(tracer, "ITxTracer", "ReportActionError", 1), context, requireSymbol: true),
            Bind(tracer, "ITxTracer", "ReportByteCode", "code callback declaration", FindMethod(tracer, "ITxTracer", "ReportByteCode", 1), context, requireSymbol: true),
            Bind(tracer, "ITxTracer", "ReportAccess", "access callback declaration", FindMethod(tracer, "ITxTracer", "ReportAccess", 2), context, requireSymbol: true),
            Bind(tracer, "ITxTracer", "ReportFees", "fee callback declaration", FindMethod(tracer, "ITxTracer", "ReportFees", 2), context, requireSymbol: true),
            Bind(tracer, "ITxTracer", "ReportLog", "log callback declaration", FindMethod(tracer, "ITxTracer", "ReportLog", 1), context, requireSymbol: true),
            Bind(tracer, "ITxTracer", "MarkAsFailed", "receipt callback declaration", FindMethod(tracer, "ITxTracer", "MarkAsFailed", 5), context, requireSymbol: true),
            Bind(tracer, "ITxTracer", "MarkAsSuccess", "receipt callback declaration", FindMethod(tracer, "ITxTracer", "MarkAsSuccess", 5), context, requireSymbol: true),
        ];

        SourceBinding RouteBinding(string name, string text, bool requireSymbol = true) =>
            Bind(routeSource, "BlockProcessingModule", name, "standard DI registration", FindRouteInvocation(routeSource, text), context, requireSymbol);
        SourceBinding MethodBinding(string path, string member)
        {
            SourceBinding[] matches = methodBindings.Where(binding => binding.Path == path && binding.Member == member).ToArray();
            if (matches.Length != 1)
                throw new ExtractionException($"Expected one bound method {path}/{member}, found {matches.Length}. Bound members: {string.Join("; ", methodBindings.Select(binding => binding.Path + "/" + binding.Member))}.");
            return matches[0];
        }
        ValidateInitRegistrationProjection(routeSource);
        List<SourceBinding> routeBindings =
        [
            Bind(routeSource, "BlockProcessingModule", "Load", "standard DI body", FindMethod(routeSource, "BlockProcessingModule", "Load", 1), context, requireSymbol: true),
            RouteBinding("AddScoped<ITransactionProcessor.IBlobBaseFeeCalculator,BlobBaseFeeCalculator>", "AddScoped<ITransactionProcessor.IBlobBaseFeeCalculator,BlobBaseFeeCalculator>()"),
            RouteBinding("AddScoped<ITransactionProcessor,EthereumTransactionProcessor>", "AddScoped<ITransactionProcessor,EthereumTransactionProcessor>()"),
            RouteBinding("AddScoped<ICodeInfoRepository,CacheCodeInfoRepository>", "AddScoped<ICodeInfoRepository,CacheCodeInfoRepository>()"),
            RouteBinding("AddScoped<IWorldState,WorldState>", "AddScoped<IWorldState,WorldState>()"),
            Bind(processor, "EthereumTransactionProcessorBase", "class", "standard processor inheritance", FindType(processor, "EthereumTransactionProcessorBase"), context, requireSymbol: true),
        ];

        StageShape[] stages =
        [
            new("metrics", 1, CompletionStageKind.Metrics, "IncrementEmptyCalls before any transfer branch", b["metricsIncrement"]),
            new("stateCharge", 2, CompletionStageKind.StateCharge, "EIP-8037 dead-recipient charge and bool result", b["stateCharge"]),
            new("valueAndRecipient", 3, CompletionStageKind.ValueAndRecipient, "PayValue then AddToBalanceAndCreateIfNotExists", b["recipientBalance"]),
            new("actionStart", 4, CompletionStageKind.ActionStart, "action start and optional bytecode observation", b["actionStart"]),
            new("oogForfeit", 5, CompletionStageKind.OogForfeit, "ClearExecutionGas after action start on new-account OOG", b["clearExecutionGas"]),
            new("transferLog", 6, CompletionStageKind.TransferLog, "EIP-7708 transfer log construction and callback", b["transferLog"]),
            new("substate", 7, CompletionStageKind.Substate, "receipt-erased TransactionSubstate construction", b["substate"]),
            new("actionEnd", 8, CompletionStageKind.ActionEnd, "action error or remaining-gas action end", b["actionEnd"]),
            new("refundSettlement", 9, CompletionStageKind.RefundSettlement, "Refund through byte-pinned settlement-kernel projection", b["refund"]),
            new("accessReport", 10, CompletionStageKind.AccessReport, "access warming and access callback", b["accessReport"]),
            new("headerFees", 11, CompletionStageKind.HeaderAndFees, "block gas counters followed by PayFees", b["headerFees"]),
            new("finalize", 12, CompletionStageKind.Finalize, "transaction fields and world-state finalization", b["finalize"]),
            new("receiptContinuation", 13, CompletionStageKind.ReceiptContinuation, "MarkAsFailed/MarkAsSuccess callback inputs only", b["receiptBranch"]),
        ];

        BranchShape[] branches =
        [
            new("newAccountCharge", 1, b["newAccountBranch"].CanonicalSyntax, "EIP-8037 value transfer to dead non-self recipient", PredicateKind.NewAccountCharge, HasNegatedReference(b["newAccountBranch"].TypedAst, "senderIsRecipient"), false, b["newAccountBranch"]),
            new("recipientWrites", 2, b["recipientBranch"].CanonicalSyntax, "skip PayValue and recipient balance write for self-send or charge OOG", PredicateKind.RecipientWrites, HasNegatedReference(b["recipientBranch"].TypedAst, "senderIsRecipient"), HasNegatedReference(b["recipientBranch"].TypedAst, "newAccountOutOfGas"), b["recipientBranch"]),
            new("actions", 3, b["traceBranch"].CanonicalSyntax, "action callback branch", PredicateKind.Actions, false, false, b["traceBranch"]),
            new("forfeit", 4, "newAccountOutOfGas", "forfeit execution gas after action start", PredicateKind.Actions, false, true, b["clearExecutionGas"]),
            new("transferLogs", 5, b["logBranch"].CanonicalSyntax, "EIP-7708 non-self successful value transfer log", PredicateKind.TransferLogs, HasNegatedReference(b["logBranch"].TypedAst, "senderIsRecipient"), HasNegatedReference(b["logBranch"].TypedAst, "newAccountOutOfGas"), b["logBranch"]),
            new("counterMode", 6, b["headerCounterGuard"].CanonicalSyntax, "normal block counters only", PredicateKind.NormalCounters, false, false, b["headerCounterGuard"]),
            new("finalizationMode", 7, "restore else commit else transient", "restore/commit/build-up branches", PredicateKind.Restore, false, false, b["restoreBranch"]),
            new("receipt", 8, b["receiptBranch"].CanonicalSyntax, "receipt continuation is observed iff tracer requests it", PredicateKind.Receipt, false, false, b["receiptBranch"]),
        ];

        EffectShape[] effects =
        [
            new("metricsIncrement", 1, "metric", [], ["emptyCalls"], "observable metric", b["metricsIncrement"]),
            new("readTransferFlags", 2, "read", ["tx.ValueRef", "tx.SenderAddress", "tracer.IsTracingActions"], ["hasValueTransfer", "senderIsRecipient"], "none", b["newAccountBranch"]),
            new("chargeNewAccount", 3, "gas-adapter", ["spec.IsEip8037Enabled", "WorldState.IsDeadAccount", "value"], ["gasAvailable", "newAccountOutOfGas"], "OOG is visible after action start", b["stateCharge"]),
            new("payValue", 4, "world-request", ["tx.ValueRef", "opts.Warmup"], ["sender.balance"], "normal-return request issuance; world exceptions excluded", b["payValue"]),
            new("recipientBalance", 5, "world-request", ["recipient", "hasValueTransfer"], ["recipient.balance/account"], "normal-return request issuance; world exceptions excluded", b["recipientBalance"]),
            new("actionStart", 6, "tracer-request", ["remainingGas", "value", "tx.Data"], ["action observation"], "normal-return request issuance; tracer exceptions excluded", b["actionStart"]),
            new("clearExecutionGas", 7, "gas-adapter", ["newAccountOutOfGas"], ["executionGas=0"], "halt remains visible", b["clearExecutionGas"]),
            new("transferLog", 8, "log-construction", ["sender", "recipient", "value"], ["substate.logs"], "normal-return model construction; production failures excluded", b["transferLog"]),
            new("reportLog", 9, "tracer-request", ["transferLog", "tracer.IsTracingLogs"], ["log observation"], "normal-return request issuance; tracer exceptions excluded", b["reportLog"]),
            new("constructSubstate", 10, "substate", ["logs", "newAccountOutOfGas"], ["substate"], "none", b["substate"]),
            new("actionTerminal", 11, "tracer-request", ["newAccountOutOfGas", "remainingGas"], ["action error/end"], "normal-return request issuance; tracer exceptions excluded", b["actionError"]),
            new("refundSettlement", 12, "settlement-adapter", ["gasAvailable", "floorGas", "standardGas", "substate"], ["spentGas"], "byte-pinned model projection; production composition excluded", b["refund"]),
            new("accessObservation", 13, "tracer-request", ["access flags", "tx.AccessList", "recipient", "sender"], ["access observation"], "normal-return request issuance; tracer exceptions and enumeration order excluded", b["accessReport"]),
            new("headerCounters", 14, "header-write", ["spentGas", "opts", "parallel", "spec"], ["header.GasUsed"], "bounded model arithmetic; production body excluded", b["headerFees"]),
            new("feePayment", 15, "world-request", ["spentGas", "premium", "baseFee", "blobBaseFee", "status"], ["beneficiary/collector balances"], "normal-return request issuance; world exceptions excluded", b["payFees"]),
            new("feeObservation", 16, "tracer-request", ["fees", "burntFees"], ["fee observation"], "normal-return request issuance; tracer exceptions excluded", b["payFees"]),
            new("finalizeWorld", 17, "world-request", ["restore", "commit", "deleteCallerAccount", "senderReservedGasPayment", "tracer.IsTracingState"], ["journal/reset/commit/reap"], "normal-return request issuance; world exceptions excluded", b["finalize"]),
            new("transactionFields", 18, "transaction-write", ["spentGas", "opts.Warmup"], ["tx.BlockGasUsed", "tx.SpentGas"], "warmup suppresses fields", b["finalize"]),
            new("receiptContinuation", 19, "receipt-observation", ["status", "substate", "spentGas", "executingAccount"], ["MarkAsFailed/MarkAsSuccess input"], "normal-return request issuance; callback exceptions excluded", b["receiptBranch"]),
        ];

        OperationShape[] operations =
        [
            Operation("incrementEmptyCalls", 1, "Metrics", "IncrementEmptyCalls", [], ["emptyCalls"], b["metricsIncrement"]),
            Operation("newAccountStateCost", 2, "IGasPolicy<TSelf>", "GetNewAccountStateCost", ["spec"], ["stateGasCost"], b["newAccountCost"]),
            Operation("consumeStateGas", 3, "EthereumGasPolicy", "TryConsumeStateGas", ["gasAvailable", "stateGasCost"], ["gasAvailable", "bool"], b["stateCharge"]),
            Operation("payValueRequest", 4, "TransactionProcessorBase<TGasPolicy>", "PayValue", ["tx", "spec", "opts"], ["world request"], b["payValue"]),
            Operation("addRecipientRequest", 5, "IWorldState", "AddToBalanceAndCreateIfNotExists", ["recipient", "value", "spec"], ["world request"], b["recipientBalance"]),
            Operation("actionStartRequest", 6, "TransactionProcessorBase<TGasPolicy>", "TraceSimpleTransferActionStart", ["tx", "recipient", "tracer", "value", "gasAvailable"], ["trace"], b["actionStart"]),
            Operation("clearExecutionGas", 7, "IGasPolicy<TSelf>", "ClearExecutionGas", ["gasAvailable"], ["gasAvailable"], b["clearExecutionGas"]),
            Operation("transferLogRequest", 8, "TransferLog", "CreateTransfer", ["sender", "recipient", "value"], ["log"], b["transferLog"]),
            Operation("substateConstruction", 9, "TransactionSubstate", ".ctor", ["logs", "newAccountOutOfGas"], ["substate"], b["substate"]),
            Operation("refundRequest", 10, "TransactionProcessorBase<TGasPolicy>", "Refund", ["tx", "header", "spec", "opts", "substate", "gasAvailable", "gasPrice"], ["spentGas"], b["refund"]),
            Operation("accessRequest", 11, "TransactionProcessorBase<TGasPolicy>", "ReportSimpleTransferAccess", ["tx", "spec", "tracer", "recipient"], ["trace"], b["accessReport"]),
            Operation("headerAndFees", 12, "TransactionProcessorBase<TGasPolicy>", "UpdateHeaderGasUsedAndPayFees", ["spentGas", "status"], ["header", "world", "trace"], b["headerFees"]),
            Operation("finalizeRequest", 13, "TransactionProcessorBase<TGasPolicy>", "FinalizeTransaction", ["tx", "spec", "tracer", "opts", "restore", "commit", "substate", "spentGas"], ["transactionResult"], b["finalize"]),
            Operation("receiptFailure", 14, "ITxTracer", "MarkAsFailed", ["executingAccount", "spentGas", "output", "error", "stateRoot"], ["receipt continuation"], b["receiptFailed"]),
            Operation("receiptSuccess", 15, "ITxTracer", "MarkAsSuccess", ["executingAccount", "spentGas", "output", "logs", "stateRoot"], ["receipt continuation"], b["receiptSuccess"]),
        ];

        AdapterPremise[] adapters =
        [
            new("world", "world-state-request-boundary", WorldStatePath, "WorldState forwarding methods", "IWorldState -> WorldState -> StateProvider", "normal-return request issuance preserves the typed address/value/spec/root payload; live world mutation and root computation are not proved", adapterBindings.Where(static x => x.Path is WorldStatePath or WorldStateExtensionsPath or StateProviderPath).ToArray()),
            new("gas", "gas-policy", EthereumGasPolicyPath, "TryConsumeStateGas/GetRemainingGas/ClearExecutionGas/CombineBlockGas", "ref gas response plus result bool", "the numeric model assumes the byte-pinned kernel response and no-wrap premises; production policy composition is outside the model-to-model theorem", [b["stateCharge"], b["clearExecutionGas"], b["remainingGas"], b["stateReservoir"], MethodBinding(StateGasChargeKernelPath, "TryCharge"), MethodBinding(StateGasChargeKernelPath, "CalculateSpill"), MethodBinding(TransactionGasInitializationKernelPath, "Combine")]),
            new("settlement", "settlement-request-boundary", SettlementKernelPath, "TransactionSettlementKernel.Calculate", "settlement result fields", "the model consumes the imported kernel projection as an adapter input; no production Refund/PayRefund composition is claimed", [b["refund"], MethodBinding(SettlementKernelPath, "Calculate")]),
            new("tracer", "tracer-request-boundary", TxTracerPath, "ITxTracer callbacks", "typed normal-return callback requests", "the model records typed callback requests and guards; callback effects, exceptions, partial prefixes, and live HashSet order are outside the boundary", [b["actionStart"], b["actionError"], b["actionEnd"], b["accessReport"], b["completeWithoutFrameAccess"], b["reportLog"], b["payFees"], b["receiptFailed"], b["receiptSuccess"], b["fastToString"], b["buildUpBranch"], b["commitBranch"], b["accessHotGuard"], b["accessListGuard"], b["accessCoinbaseGuard"], b["accessRecipientGuard"]]),
            new("di", "source-projection-and-runtime-route-premise", MainnetDiPath, "BlockProcessingModule.Load", "exact ordered registration syntax", "runtime DI selection of EthereumTransactionProcessor, WorldState, and CacheCodeInfoRepository is an external premise", routeBindings.ToArray()),
            new("caller", "post-nonce-caller", TransactionProcessorPath, "Execute/6 and PrepareSimpleTransferFastPath/4", "typed completion input", "caller facts are admitted from the exact Execute and Prepare source; the earlier validation, gas purchase, and nonce update are external prefix premises", callerBindings),
        ];

        string[] receiptForbiddenMembers = ["FoldReceipt", "EncodeReceipt", "CalculateReceiptRoot", "ReceiptRoot"];
        string[] finalizeInvocations = finalize.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(InvocationName)
            .ToArray();
        if (finalizeInvocations.Any(name => receiptForbiddenMembers.Contains(name, StringComparer.Ordinal)))
            throw new ExtractionException("Receipt folding entered the simple-transfer completion boundary.");

        CompletionLowering lowering = LowerCompletionSemantics(
            b["combineBlockGas"],
            MethodBinding(TransactionGasInitializationKernelPath, "Combine"),
            MethodBinding(GasConsumedPath, "EffectiveBlockGas"),
            b["buildUpBranch"],
            MethodBinding(EvmExceptionPath, "FastToString"),
            b["warmupAccess"],
            b["coinbaseWarmup"],
            b["recipientWarmup"],
            b["senderWarmup"],
            b["accessHotGuard"],
            b["accessListGuard"],
            b["accessCoinbaseGuard"],
            b["accessRecipientGuard"],
            MethodBinding(TransactionSubstatePath, ".ctor"),
            MethodBinding(TransactionSubstatePath, "IsError"),
            MethodBinding(GasCostOfPath, "NewAccountState"),
            transferLogPayloadGrammar,
            transferLogAddress,
            transferSignature,
            noFrameProof);

        CompletionShape completion = new(
            "TransactionProcessorBase.Execute/6 -> ExecuteSimpleTransfer/15",
            "receipt-continuation-input-only",
            stages,
            branches,
            effects,
            operations,
            adapters,
            lowering,
            methodBindings.ToArray(),
            routeBindings.ToArray());
        RouteShape routeShape = new(
            "BlockProcessingModule.AddScoped<ITransactionProcessor,EthereumTransactionProcessor>",
            "BlockProcessingModule.AddScoped<IWorldState,WorldState>",
            "BlockProcessingModule.AddScoped<ICodeInfoRepository,CacheCodeInfoRepository>",
            "EthereumTransactionProcessorBase : TransactionProcessorBase<EthereumGasPolicy>",
            "Eip803x.Generated.TransactionSettlementKernel.calculate (exact identity checked)",
            ["ExecuteEvmTransaction", "VirtualMachine", "system transaction processor", "XDC/Taiko", "parallel", "BAL", "receipt roots", "database/trie/CLR"],
            routeBindings.ToArray());
        return (completion, routeShape);
    }

    private static SourceBinding[] AdmitSimpleTransferCaller(SourceFile processor, SemanticContext context)
    {
        MethodDeclarationSyntax execute = FindMethod(processor, "TransactionProcessorBase", "Execute", 6);
        MethodDeclarationSyntax prepare = FindMethod(processor, "TransactionProcessorBase", "PrepareSimpleTransferFastPath", 4);
        MethodDeclarationSyntax candidate = FindMethod(processor, "TransactionProcessorBase", "IsSimpleTransferFastPathCandidate", 2);
        MethodDeclarationSyntax noCode = FindMethod(processor, "TransactionProcessorBase", "HasNoExecutableCode", 2);
        MethodDeclarationSyntax available = FindMethod(processor, "TransactionProcessorBase", "CalculateAvailableGas", 4);
        foreach (MethodDeclarationSyntax method in new[] { execute, prepare, candidate, noCode })
            RejectNestedCallables(method);
        List<SourceBinding> bindings =
        [
            Bind(processor, "TransactionProcessorBase<TGasPolicy>", "Execute", "simple-transfer caller", execute, context, requireSymbol: true),
            Bind(processor, "TransactionProcessorBase<TGasPolicy>", "PrepareSimpleTransferFastPath", "simple-transfer candidate preparation", prepare, context, requireSymbol: true),
            Bind(processor, "TransactionProcessorBase<TGasPolicy>", "IsSimpleTransferFastPathCandidate", "simple-transfer candidate guard", candidate, context, requireSymbol: true),
            Bind(processor, "TransactionProcessorBase<TGasPolicy>", "HasNoExecutableCode", "simple-transfer code/delegation guard", noCode, context, requireSymbol: true),
            Bind(processor, "TransactionProcessorBase<TGasPolicy>", "CalculateAvailableGas", "available-gas caller boundary", available, context, requireSymbol: true),
        ];

        SourceBinding AddExpression(MethodDeclarationSyntax method, string role, string expected)
        {
            ExpressionSyntax expression = FindExactExpression(method, expected);
            SourceBinding binding = Bind(processor, "TransactionProcessorBase<TGasPolicy>", method.Identifier.ValueText,
                role, expression, context, requireSymbol: false);
            bindings.Add(binding);
            return binding;
        }

        SourceBinding restore = AddExpression(execute, "caller restore flag", "opts.HasFlag(ExecutionOptions.Restore)");
        SourceBinding commit = AddExpression(execute, "caller effective commit flag",
            "opts.HasFlag(ExecutionOptions.Commit)||(!opts.HasFlag(ExecutionOptions.SkipValidation)&&!spec.IsEip658Enabled)");
        SourceBinding prepareCall = AddExpression(execute, "caller candidate lookup",
            "PrepareSimpleTransferFastPath(tx,spec,out CodeInfo? preloadedCodeInfo,out Address? preloadedDelegationAddress)");
        SourceBinding precommit = AddExpression(execute, "caller pre-execution commit guard",
            "commit&&(simpleTransferRecipient is null||restore||tracer.IsTracingState)");
        SourceBinding precommitCall = AddExpression(execute, "caller guarded pre-execution commit",
            "WorldState.Commit(spec,tracer.IsTracingState?tracer:NullTxTracer.Instance,commitRoots:false)");
        SourceBinding availableCall = AddExpression(execute, "caller available-gas result",
            "CalculateAvailableGas(tx,spec,in intrinsicGas,out TGasPolicy gasAvailable)");
        AddExpression(execute, "caller available-gas failure exit",
            "!(result=CalculateAvailableGas(tx,spec,in intrinsicGas,out TGasPolicy gasAvailable))");
        SourceBinding simpleArm = AddExpression(execute, "caller selected simple-transfer arm", "simpleTransferRecipient is not null");
        SourceBinding dispatch = AddExpression(execute, "caller simple-transfer dispatch",
            "ExecuteSimpleTransfer(tx,header,spec,tracer,opts,restore,commit,deleteCallerAccount,simpleTransferRecipient,in intrinsicGas,gasAvailable,in opcodeGasPrice,in premiumPerGas,in senderReservedGasPayment,in blobBaseFee)");

        AddExpression(candidate, "candidate exclusions",
            "!isCodeOverridable&&tx.AuthorizationList is null&&!ForceSimpleTransferDisabled");
        AddExpression(noCode, "code and delegation exclusion", "delegationAddress is null&&codeInfo.IsEmpty");
        SourceBinding candidateGuard = AddExpression(prepare, "recipient and candidate guard",
            "recipient is null||!IsSimpleTransferFastPathCandidate(tx,_isCodeOverridable)");
        SourceBinding codeLookup = AddExpression(prepare, "normal code lookup with fork-selected delegation",
            "_codeInfoRepository.GetCachedCodeInfo(recipient,followDelegation:!spec.IsEip8037Enabled,spec,out preloadedDelegationAddress)");
        SourceBinding noCodeSelection = AddExpression(prepare, "selected recipient after code lookup",
            "HasNoExecutableCode(preloadedCodeInfo,preloadedDelegationAddress)?recipient:null");

        string[] expectedPreparationStatements =
        [
            "preloadedCodeInfo = null;",
            "preloadedDelegationAddress = null;",
            "Address? recipient = tx.To;",
            "if (recipient is null || !IsSimpleTransferFastPathCandidate(tx, _isCodeOverridable)) return null;",
            "preloadedCodeInfo = _codeInfoRepository.GetCachedCodeInfo(recipient, followDelegation: !spec.IsEip8037Enabled, spec, out preloadedDelegationAddress);",
            "return HasNoExecutableCode(preloadedCodeInfo, preloadedDelegationAddress) ? recipient : null;",
        ];
        if (prepare.Body is not { Statements: { Count: 6 } preparationStatements } ||
            !preparationStatements.Select(Canonical).SequenceEqual(
                expectedPreparationStatements.Select(statement => Canonical(SyntaxFactory.ParseStatement(statement))),
                StringComparer.Ordinal))
            throw new ExtractionException("Simple-transfer preparation has an unmodeled return, path, or effect.");

        if (candidate.ExpressionBody is null ||
            Canonical(candidate.ExpressionBody.Expression) != Canonical(FindExactExpression(candidate,
                "!isCodeOverridable&&tx.AuthorizationList is null&&!ForceSimpleTransferDisabled")) ||
            noCode.ExpressionBody is null ||
            Canonical(noCode.ExpressionBody.Expression) != Canonical(FindExactExpression(noCode,
                "delegationAddress is null&&codeInfo.IsEmpty")) ||
            prepare.Body?.Statements.LastOrDefault() is not ReturnStatementSyntax { Expression: ExpressionSyntax finalSelection } ||
            Canonical(finalSelection) != noCodeSelection.CanonicalSyntax)
            throw new ExtractionException("Simple-transfer candidate or no-code predicate is not the complete returned expression.");

        if (!(restore.StartLine <= commit.StartLine && commit.StartLine < prepareCall.StartLine &&
            prepareCall.StartLine < precommit.StartLine && precommit.StartLine < precommitCall.StartLine &&
            precommitCall.StartLine < availableCall.StartLine &&
            availableCall.StartLine < simpleArm.StartLine && simpleArm.StartLine < dispatch.StartLine &&
            candidateGuard.StartLine < codeLookup.StartLine && codeLookup.StartLine < noCodeSelection.StartLine))
            throw new ExtractionException("Simple-transfer caller order changed.");

        IfStatementSyntax ExactIf(MethodDeclarationSyntax method, string expected)
        {
            string canonical = Canonical(SyntaxFactory.ParseExpression(expected));
            IfStatementSyntax[] statements = OwnedDescendants(method).OfType<IfStatementSyntax>()
                .Where(statement => Canonical(statement.Condition) == canonical).ToArray();
            if (statements.Length != 1) throw new ExtractionException("Simple-transfer caller branch changed: " + expected);
            return statements[0];
        }

        IfStatementSyntax candidateExit = ExactIf(prepare,
            "recipient is null||!IsSimpleTransferFastPathCandidate(tx,_isCodeOverridable)");
        IfStatementSyntax commitBranch = ExactIf(execute, "commitBeforeExecution");
        IfStatementSyntax gasExit = ExactIf(execute,
            "!(result=CalculateAvailableGas(tx,spec,in intrinsicGas,out TGasPolicy gasAvailable))");
        IfStatementSyntax simpleBranch = ExactIf(execute, "simpleTransferRecipient is not null");
        if (candidateExit.Statement is not ReturnStatementSyntax { Expression: LiteralExpressionSyntax candidateNull } ||
            !candidateNull.IsKind(SyntaxKind.NullLiteralExpression) ||
            commitBranch.Statement is not ExpressionStatementSyntax commitStatement ||
            Canonical(commitStatement.Expression) != precommitCall.CanonicalSyntax ||
            gasExit.Statement is not ReturnStatementSyntax { Expression: IdentifierNameSyntax gasResult } ||
            gasResult.Identifier.ValueText != "result" ||
            simpleBranch.Statement is not BlockSyntax { Statements.Count: 1 } simpleBlock ||
            simpleBlock.Statements[0] is not ReturnStatementSyntax simpleReturn ||
            simpleReturn.Expression is null || Canonical(simpleReturn.Expression) != dispatch.CanonicalSyntax)
            throw new ExtractionException("Simple-transfer caller guard no longer controls the expected exit or dispatch.");

        if (context.Model(execute.SyntaxTree).GetOperation(
                FindInvocation(execute, "ExecuteSimpleTransfer")) is not IInvocationOperation call ||
            call.TargetMethod.Name != "ExecuteSimpleTransfer" ||
            call.TargetMethod.ContainingType.OriginalDefinition.ToDisplayString() !=
                "Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<TGasPolicy>" ||
            !call.TargetMethod.Parameters.Select(static parameter => parameter.Name).SequenceEqual(
                ["tx", "header", "spec", "tracer", "opts", "restore", "commit", "deleteCallerAccount", "recipient",
                 "intrinsicGas", "gasAvailable", "opcodeGasPrice", "premiumPerGas", "senderReservedGasPayment", "blobBaseFee"],
                StringComparer.Ordinal) ||
            !call.Arguments.Select(static argument => argument.Parameter!.RefKind).SequenceEqual(
                [RefKind.None, RefKind.None, RefKind.None, RefKind.None, RefKind.None, RefKind.None, RefKind.None,
                 RefKind.None, RefKind.None, RefKind.In, RefKind.None, RefKind.In, RefKind.In, RefKind.In, RefKind.In]))
            throw new ExtractionException("The simple-transfer caller no longer passes the exact typed arguments and ref kinds.");

        SyntaxNode dispatchNode = FindInvocation(execute, "ExecuteSimpleTransfer");
        ExpressionSyntax precommitInitializer = FindExactExpression(execute,
            "commit&&(simpleTransferRecipient is null||restore||tracer.IsTracingState)");
        SyntaxNode prepareNode = FindInvocation(execute, "PrepareSimpleTransferFastPath");
        SyntaxNode gasNode = FindInvocation(execute, "CalculateAvailableGas");
        SyntaxNode priceNode = FindInvocation(execute, "CalculateEffectiveGasPrice");
        SyntaxNode buyNode = FindInvocation(execute, "BuyGas");
        SyntaxNode deleteNode = FindInvocation(execute, "RecoverSenderIfNeeded");
        SyntaxNode lookupNode = FindInvocation(prepare, "GetCachedCodeInfo");
        SyntaxNode selectedNode = FindExactExpression(prepare,
            "HasNoExecutableCode(preloadedCodeInfo,preloadedDelegationAddress)?recipient:null");
        VariableDeclaratorSyntax[] preparedRecipients = OwnedDescendants(prepare).OfType<VariableDeclaratorSyntax>()
            .Where(variable => variable.Identifier.ValueText == "recipient").ToArray();
        if (preparedRecipients.Length != 1 || preparedRecipients[0].Initializer is not { Value: ExpressionSyntax recipientInitializer } ||
            Canonical(recipientInitializer) != Canonical(FindExactExpression(prepare, "tx.To")))
            throw new ExtractionException("Candidate recipient is no longer defined directly by tx.To.");
        context.RequireDominates(recipientInitializer, lookupNode, "recipient before code lookup");
        context.RequireDominates(FindExactExpression(prepare, "recipient is null||!IsSimpleTransferFastPathCandidate(tx,_isCodeOverridable)"),
            lookupNode, "candidate guard before code lookup");
        context.RequireDominates(lookupNode, selectedNode, "code lookup before candidate selection");
        context.RequireDominates(prepareNode, dispatchNode, "candidate preparation before simple dispatch");
        context.RequireDominates(gasNode, dispatchNode, "available gas before simple dispatch");

        SemanticModel prepareModel = context.Model(prepare.SyntaxTree);
        foreach (string name in new[] { "recipient", "preloadedCodeInfo", "preloadedDelegationAddress" })
        {
            IParameterSymbol? parameter = prepare.ParameterList.Parameters
                .Where(item => item.Identifier.ValueText == name)
                .Select(item => prepareModel.GetDeclaredSymbol(item))
                .OfType<IParameterSymbol>().SingleOrDefault();
            ISymbol symbol = parameter ?? OwnedDescendants(prepare).OfType<VariableDeclaratorSyntax>()
                .Where(item => item.Identifier.ValueText == name)
                .Select(item => prepareModel.GetDeclaredSymbol(item))
                .SingleOrDefault() ?? throw new ExtractionException("Candidate preparation declaration changed: " + name);
            foreach (IdentifierNameSyntax reference in OwnedDescendants(prepare).OfType<IdentifierNameSyntax>())
            {
                if (!SymbolEqualityComparer.Default.Equals(prepareModel.GetSymbolInfo(reference).Symbol, symbol) ||
                    !IsWritableReference(reference)) continue;
                bool expected = name switch
                {
                    "recipient" => false,
                    "preloadedCodeInfo" => reference.Ancestors().OfType<AssignmentExpressionSyntax>()
                        .Any(assignment => assignment.Left.Span.Contains(reference.Span) &&
                            (assignment.Right.IsKind(SyntaxKind.NullLiteralExpression) ||
                             Canonical(assignment.Right) == Canonical(lookupNode))),
                    "preloadedDelegationAddress" => reference.Ancestors().OfType<AssignmentExpressionSyntax>()
                        .Any(assignment => assignment.Left.Span.Contains(reference.Span) &&
                            assignment.Right.IsKind(SyntaxKind.NullLiteralExpression)) ||
                        lookupNode.Span.Contains(reference.Span) && reference.Parent is ArgumentSyntax argument &&
                        argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword),
                    _ => false,
                };
                if (!expected) throw new ExtractionException("Candidate preparation has an untracked write: " + name);
            }
        }

        ILocalSymbol RequireReachingLocal(string name, SyntaxNode definition, SyntaxNode use)
        {
            SyntaxNode[] declarations = OwnedDescendants(execute).Where(node => node switch
            {
                VariableDeclaratorSyntax variable => variable.Identifier.ValueText == name,
                SingleVariableDesignationSyntax designation => designation.Identifier.ValueText == name,
                _ => false,
            }).ToArray();
            if (declarations.Length != 1 || context.Model(execute.SyntaxTree).GetDeclaredSymbol(declarations[0]) is not ILocalSymbol symbol)
                throw new ExtractionException("Caller local declaration changed: " + name);
            if (declarations[0] is VariableDeclaratorSyntax variable &&
                (variable.Initializer is null || Canonical(variable.Initializer.Value) != Canonical(definition)) ||
                declarations[0] is SingleVariableDesignationSyntax designation &&
                !definition.Span.Contains(designation.Span))
                throw new ExtractionException("Caller local definition changed: " + name);
            context.RequireDominates(definition, use, name);
            SemanticModel model = context.Model(execute.SyntaxTree);
            foreach (IdentifierNameSyntax reference in OwnedDescendants(execute).OfType<IdentifierNameSyntax>())
            {
                if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(reference).Symbol, symbol)) continue;
                if (IsWritableReference(reference))
                    throw new ExtractionException("Caller local has a second reaching definition or an escaping alias: " + name);
            }
            return symbol;
        }

        ILocalSymbol restoreLocal = RequireReachingLocal("restore", FindExactExpression(execute,
            "opts.HasFlag(ExecutionOptions.Restore)"), dispatchNode);
        ILocalSymbol commitLocal = RequireReachingLocal("commit", FindExactExpression(execute,
            "opts.HasFlag(ExecutionOptions.Commit)||(!opts.HasFlag(ExecutionOptions.SkipValidation)&&!spec.IsEip658Enabled)"), dispatchNode);
        ILocalSymbol precommitLocal = RequireReachingLocal("commitBeforeExecution", precommitInitializer,
            commitBranch.Condition);
        if (!SymbolEqualityComparer.Default.Equals(context.Model(execute.SyntaxTree)
                .GetSymbolInfo(commitBranch.Condition).Symbol, precommitLocal))
            throw new ExtractionException("Pre-execution commit branch no longer reads the exact guard definition.");
        ILocalSymbol recipientLocal = RequireReachingLocal("simpleTransferRecipient", prepareNode, dispatchNode);
        ILocalSymbol availableLocal = RequireReachingLocal("gasAvailable", gasNode, dispatchNode);
        ILocalSymbol priceLocal = RequireReachingLocal("opcodeGasPrice", priceNode, dispatchNode);
        ILocalSymbol premiumLocal = RequireReachingLocal("premiumPerGas", buyNode, dispatchNode);
        ILocalSymbol paymentLocal = RequireReachingLocal("senderReservedGasPayment", buyNode, dispatchNode);
        ILocalSymbol blobLocal = RequireReachingLocal("blobBaseFee", buyNode, dispatchNode);
        ILocalSymbol deleteLocal = RequireReachingLocal("deleteCallerAccount", deleteNode, dispatchNode);
        _ = RequireReachingLocal("preloadedCodeInfo", prepareNode, dispatchNode);
        _ = RequireReachingLocal("preloadedDelegationAddress", prepareNode, dispatchNode);

        (int Index, ILocalSymbol Symbol)[] dispatchLocals =
        [
            (5, restoreLocal), (6, commitLocal), (7, deleteLocal), (8, recipientLocal),
            (10, availableLocal), (11, priceLocal), (12, premiumLocal), (13, paymentLocal), (14, blobLocal),
        ];
        foreach ((int index, ILocalSymbol symbol) in dispatchLocals)
        {
            ILocalReferenceOperation[] references = Descendants(call.Arguments[index].Value)
                .OfType<ILocalReferenceOperation>().ToArray();
            if (references.Length != 1 || !SymbolEqualityComparer.Default.Equals(references[0].Local, symbol))
                throw new ExtractionException("Caller dispatch argument no longer reads its unique source definition: " + symbol.Name);
        }

        return bindings.ToArray();
    }

    private static IEnumerable<IOperation> Descendants(IOperation operation)
    {
        yield return operation;
        foreach (IOperation child in operation.ChildOperations)
            foreach (IOperation descendant in Descendants(child)) yield return descendant;
    }

    private static bool IsWritableReference(IdentifierNameSyntax reference) =>
        reference.Ancestors().OfType<AssignmentExpressionSyntax>()
            .Any(assignment => assignment.Left.Span.Contains(reference.Span)) ||
        reference.Ancestors().OfType<ArgumentSyntax>()
            .Any(argument => argument.Expression.Span.Contains(reference.Span) &&
                (argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) ||
                 argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword))) ||
        reference.Ancestors().OfType<PrefixUnaryExpressionSyntax>()
            .Any(unary => unary.Operand.Span.Contains(reference.Span) &&
                (unary.IsKind(SyntaxKind.PreIncrementExpression) ||
                 unary.IsKind(SyntaxKind.PreDecrementExpression) ||
                 unary.IsKind(SyntaxKind.AddressOfExpression))) ||
        reference.Ancestors().OfType<PostfixUnaryExpressionSyntax>()
            .Any(unary => unary.Operand.Span.Contains(reference.Span) &&
                (unary.IsKind(SyntaxKind.PostIncrementExpression) ||
                 unary.IsKind(SyntaxKind.PostDecrementExpression))) ||
        reference.Ancestors().OfType<RefExpressionSyntax>()
            .Any(expression => expression.Expression.Span.Contains(reference.Span));

    private static void ValidateFiniteSyntax(params MethodDeclarationSyntax[] methods)
    {
        foreach (MethodDeclarationSyntax method in methods)
            RejectForbiddenSyntax(method);

        RequireSourceOrder(methods[0],
            FindInvocation(methods[0], "IncrementEmptyCalls"),
            FindInvocation(methods[0], "TryConsumeStateGas"),
            FindInvocation(methods[0], "PayValue"),
            FindInvocation(methods[0], "AddToBalanceAndCreateIfNotExists"),
            FindInvocation(methods[0], "TraceSimpleTransferActionStart"),
            FindInvocation(methods[0], "ClearExecutionGas"),
            FindInvocation(methods[0], "CreateTransfer"),
            FindObject(methods[0], "TransactionSubstate"),
            FindInvocation(methods[0], "Refund"),
            FindInvocation(methods[0], "ReportSimpleTransferAccess"),
            FindInvocation(methods[0], "UpdateHeaderGasUsedAndPayFees"),
            FindInvocation(methods[0], "FinalizeTransaction"));
    }

    private static void RejectForbiddenSyntax(MethodDeclarationSyntax method)
    {
        foreach (SyntaxNode node in Body(method).DescendantNodes())
        {
            if (ForbiddenNodeKinds.Contains(node.Kind()))
                throw new ExtractionException($"Unsupported syntax {node.Kind()} in admitted completion member {method.Identifier.ValueText}.");
        }
    }

    private static void RejectNestedCallables(MethodDeclarationSyntax method)
    {
        SyntaxNode? nested = Body(method).DescendantNodes().FirstOrDefault(static node =>
            node is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax);
        if (nested is not null)
            throw new ExtractionException($"Nested callable {nested.Kind()} in admitted caller member {method.Identifier.ValueText}.");
    }

    private static void ValidateAccessWarmup(MethodDeclarationSyntax warmAccesses, SemanticContext context)
    {
        InvocationExpressionSyntax[] calls = OwnedDescendants(warmAccesses).OfType<InvocationExpressionSyntax>()
            .Where(static invocation => InvocationName(invocation) == "WarmUp")
            .ToArray();
        if (calls.Length != 4)
            throw new ExtractionException($"WarmUpTxAccesses must retain exactly four ordered access warmups, found {calls.Length}.");
        RequireSourceOrder(warmAccesses, calls);

        SemanticModel model = context.Model(warmAccesses.SyntaxTree);
        IfStatementSyntax[] conditionals = OwnedDescendants(warmAccesses).OfType<IfStatementSyntax>().ToArray();
        if (conditionals.Length != 4 || conditionals[0].Statement is not ReturnStatementSyntax ||
            !IsNegatedReference(LowerTypedAst(model.GetOperation(conditionals[0].Condition)), "UseHotAndColdStorage"))
            throw new ExtractionException("WarmUpTxAccesses lost its typed hot/cold early-return guard.");
        if (!IsReferenceOnly(LowerTypedAst(model.GetOperation(conditionals[1].Condition)), "UseTxAccessLists") ||
            !IsReferenceOnly(LowerTypedAst(model.GetOperation(conditionals[2].Condition)), "AddCoinbaseToTxAccessList") ||
            !IsReferenceOnly(LowerTypedAst(model.GetOperation(conditionals[3].Condition)), "warmUpRecipient"))
            throw new ExtractionException("WarmUpTxAccesses lost a typed access-list, coinbase, or recipient guard.");
        TypedAstNode[] typedCalls = calls
            .Select(call => LowerTypedAst(model.GetOperation(call)))
            .ToArray();
        if (typedCalls.Any(call => !IsWarmUpCall(call, "", "", "")))
            throw new ExtractionException("WarmUpTxAccesses contains an unresolved or different invocation.");
        IOperation? warmBody = warmAccesses.Body is not null
            ? model.GetOperation(warmAccesses.Body)
            : warmAccesses.ExpressionBody is not null
                ? model.GetOperation(warmAccesses.ExpressionBody.Expression)
                : null;
        if (!HasReference(LowerTypedAst(warmBody), "UseHotAndColdStorage"))
            throw new ExtractionException("WarmUpTxAccesses lost the hot/cold storage guard.");
        (string Parameter, string Expression, string Type)[] expectedArguments =
        [
            ("accessList", "AccessList", "AccessList"),
            ("address", "GasBeneficiary", "Address"),
            ("address", "recipient", "Address"),
            ("address", "SenderAddress", "Address"),
        ];
        for (int index = 0; index < typedCalls.Length; index++)
        {
            if (!IsWarmUpCall(typedCalls[index], expectedArguments[index].Parameter, expectedArguments[index].Expression, expectedArguments[index].Type))
                throw new ExtractionException($"WarmUpTxAccesses changed source order, overload, receiver, or argument {index}.");
        }
    }

    private static void ValidateHeaderAndReceiptCompletion(
        MethodDeclarationSyntax headerFees,
        MethodDeclarationSyntax finalize,
        SemanticContext context)
    {
        SemanticModel headerModel = context.Model(headerFees.SyntaxTree);
        TypedAstNode headerBody = LowerTypedAst(headerModel.GetOperation(Body(headerFees)));
        if (!HasInvocation(headerBody, "ParticipatesInNormalBlockCounters") ||
            !HasReference(headerBody, "EffectiveBlockGas") ||
            !HasReference(headerBody, "BlockStateGas") ||
            !HasInvocation(headerBody, "CombineBlockGas") ||
            !HasReference(headerBody, "_blockCumulativeExecutionGas") ||
            !HasReference(headerBody, "_blockCumulativeStateGas") ||
            !HasReference(headerBody, "GasUsed") ||
            !HasInvocation(headerBody, "PayFees"))
            throw new ExtractionException("UpdateHeaderGasUsedAndPayFees lost a required typed counter or fee transition.");
        ValidateEip8037CumulativeCounters(headerFees, headerModel, context.Compilation);
        RequireSourceOrder(headerFees,
            FindInvocation(headerFees, "ParticipatesInNormalBlockCounters"),
            FindMemberAccess(headerFees, "EffectiveBlockGas", 0),
            FindMemberAccess(headerFees, "BlockStateGas", 0),
            FindInvocation(headerFees, "CombineBlockGas"),
            FindInvocation(headerFees, "PayFees"));

        SemanticModel finalizeModel = context.Model(finalize.SyntaxTree);
        TypedAstNode finalizeBody = LowerTypedAst(finalizeModel.GetOperation(Body(finalize)));
        if (!HasInvocation(finalizeBody, "RecalculateStateRoot") ||
            !HasReference(finalizeBody, "StateRoot") ||
            !HasInvocation(finalizeBody, "MarkAsFailed") ||
            !HasInvocation(finalizeBody, "MarkAsSuccess"))
            throw new ExtractionException("FinalizeTransaction lost the typed receipt-root or receipt-continuation operations.");
        RequireSourceOrder(finalize,
            FindInvocation(finalize, "RecalculateStateRoot"),
            FindMemberAccess(finalize, "StateRoot", 0),
            FindInvocation(finalize, "MarkAsFailed"),
            FindInvocation(finalize, "MarkAsSuccess"));
    }

    private static void ValidateEip8037CumulativeCounters(
        MethodDeclarationSyntax method, SemanticModel model, Compilation compilation)
    {
        const string diagnostic = "UpdateHeaderGasUsedAndPayFees EIP-8037 cumulative counter guard or true-arm effects changed.";
        IPropertySymbol? expectedProperty = compilation.GetTypeByMetadataName("Nethermind.Core.Specs.IReleaseSpec")?
            .GetMembers("IsEip8037Enabled").OfType<IPropertySymbol>().SingleOrDefault();
        ParameterSyntax? specParameter = method.ParameterList.Parameters.SingleOrDefault(static parameter => parameter.Identifier.ValueText == "spec");
        IParameterSymbol? expectedParameter = specParameter is null ? null : model.GetDeclaredSymbol(specParameter);
        if (expectedProperty is null || expectedParameter is null || method.Body is not { Statements.Count: 2 } body ||
            body.Statements[0] is not IfStatementSyntax { Statement: BlockSyntax { Statements.Count: 1 } normalCounters } ||
            normalCounters.Statements[0] is not IfStatementSyntax eip8037 ||
            eip8037.Condition is not MemberAccessExpressionSyntax ||
            Canonical(eip8037.Condition) != "spec.IsEip8037Enabled" ||
            model.GetOperation(eip8037.Condition) is not IPropertyReferenceOperation condition ||
            !SymbolEqualityComparer.Default.Equals(condition.Property, expectedProperty) ||
            condition.Instance is not IParameterReferenceOperation receiver ||
            !SymbolEqualityComparer.Default.Equals(receiver.Parameter, expectedParameter) ||
            eip8037.Statement is not BlockSyntax { Statements.Count: 3 } trueArm ||
            eip8037.Else?.Statement is not BlockSyntax { Statements.Count: 1 } falseArm ||
            Canonical(trueArm.Statements[0]) != "_blockCumulativeExecutionGas+=spentGas.EffectiveBlockGas;" ||
            Canonical(trueArm.Statements[1]) != "_blockCumulativeStateGas+=spentGas.BlockStateGas;" ||
            Canonical(trueArm.Statements[2]) != "header.GasUsed=TGasPolicy.CombineBlockGas(_blockCumulativeExecutionGas,_blockCumulativeStateGas);" ||
            Canonical(falseArm.Statements[0]) != "header.GasUsed+=spentGas.EffectiveBlockGas;")
            throw new ExtractionException(diagnostic);
    }

    private static void ValidateNewAccountStateCost(
        MethodDeclarationSyntax method, VariableDeclaratorSyntax sourceConstant, SemanticContext context)
    {
        SemanticModel model = context.Model(method.SyntaxTree);
        TypedAstNode body = UnwrapTyped(LowerTypedAst(model.GetOperation(Body(method))));
        IFieldSymbol field = RequireCoreConstant(context.Compilation, "Nethermind.Core.GasCostOf", "NewAccountState", sourceConstant);
        if (!IsReferenceOnly(body, "NewAccountState") || body.Kind != "FieldReference" ||
            !TypeMatches(body.Type, ["long", "Int64"]) ||
            method.ExpressionBody is null || model.GetOperation(method.ExpressionBody.Expression) is not IFieldReferenceOperation reference ||
            !SymbolEqualityComparer.Default.Equals(reference.Field, field))
            throw new ExtractionException("IGasPolicy.GetNewAccountStateCost no longer returns the exact GasCostOf.NewAccountState schedule field.");
    }

    private static (string Grammar, string Address, string Signature) ValidateTransferLogPayload(
        SourceFile transferLogSource,
        MethodDeclarationSyntax createTransfer,
        MethodDeclarationSyntax createTransferInternal,
        VariableDeclaratorSyntax systemUserHex,
        SemanticContext context)
    {
        string transferAddress = ReadHexFieldConstant(systemUserHex, "Address.SystemUserHex", expectedDigits: 40);
        ValidateAddressMetadataProjection(systemUserHex, transferAddress, context.Compilation);
        ValidateTransferSenderProjection(transferLogSource, context);
        VariableDeclaratorSyntax transferSignature = FindField(transferLogSource, "TransferLog", "TransferSignature");
        string transferSignatureValue = ReadHexInitializer(transferSignature, context, "TransferLog.TransferSignature", expectedDigits: 64);
        SemanticModel model = context.Model(createTransfer.SyntaxTree);
        TypedAstNode transferBody = LowerTypedAst(model.GetOperation(Body(createTransfer)));
        TypedAstNode[] forwardingCalls = TypedNodes(transferBody)
            .Where(static node => node.Kind == "Invocation" && node.Name == "CreateTransferInternal")
            .ToArray();
        if (forwardingCalls.Length != 1)
            throw new ExtractionException("TransferLog.CreateTransfer must forward exactly once to CreateTransferInternal.");
        InvocationExpressionSyntax forwardingSyntax = createTransfer.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(invocation => InvocationName(invocation) == "CreateTransferInternal");
        RequireInvocationTargetIdentity(model, forwardingSyntax, createTransferInternal, "TransferLog.CreateTransfer forwarding");
        (TypedAstNode[] receiver, TypedAstNode[] arguments) = SplitInvocationChildren(forwardingCalls[0]);
        if (receiver.Length != 0 || arguments.Length != 4 || arguments.Any(static argument => argument.Kind != "Argument" || argument.Children.Length != 1) ||
            arguments.Select(static argument => argument.ParameterName).ToArray() is not ["sender", "from", "to", "amount"] ||
            UnwrapTyped(arguments[0].Children[0]).Name != "Sender" ||
            UnwrapTyped(arguments[1].Children[0]).Name != "from" ||
            UnwrapTyped(arguments[2].Children[0]).Name != "to" ||
            UnwrapTyped(arguments[3].Children[0]).Name != "amount")
            throw new ExtractionException("TransferLog.CreateTransfer changed its sender/from/to/amount forwarding grammar.");

        SemanticModel internalModel = context.Model(createTransferInternal.SyntaxTree);
        TypedAstNode internalBody = LowerTypedAst(internalModel.GetOperation(Body(createTransferInternal)));
        TypedAstNode[] constructions = TypedNodes(internalBody)
            .Where(static node => node.Kind == "ObjectCreation" && node.Name == "LogEntry")
            .ToArray();
        if (constructions.Length != 1)
            throw new ExtractionException("TransferLog.CreateTransferInternal must construct exactly one LogEntry.");
        TypedAstNode construction = constructions[0];
        if (!TypeMatches(construction.Type, ["LogEntry"]))
            throw new ExtractionException("TransferLog.CreateTransferInternal changed its LogEntry result type.");
        (_, TypedAstNode[] logArguments) = SplitInvocationChildren(construction);
        if (logArguments.Length != 3 || logArguments.Any(static argument => argument.Kind != "Argument" || argument.Children.Length != 1) ||
            logArguments.Select(static argument => argument.ParameterName).ToArray() is not ["address", "data", "topics"] ||
            UnwrapTyped(logArguments[0].Children[0]).Name != "sender")
            throw new ExtractionException("TransferLog.CreateTransferInternal changed its address/data/topics argument order.");

        TypedAstNode data = UnwrapTyped(logArguments[1].Children[0]);
        if (data.Kind != "Invocation" || data.Name != "ToBigEndian" || !HasReference(data, "amount"))
            throw new ExtractionException("TransferLog.CreateTransferInternal changed its amount-to-data lowering.");
        TypedAstNode topics = UnwrapTyped(logArguments[2].Children[0]);
        string[] topicNames = TypedNodes(topics)
            .Select(static node => node.Name)
            .Where(static name => name is "TransferSignature" or "from" or "to" or "ToHash" or "ToHash256")
            .ToArray();
        string[] expectedTopics = ["TransferSignature", "ToHash256", "ToHash", "from", "ToHash256", "ToHash", "to"];
        if (!topicNames.SequenceEqual(expectedTopics, StringComparer.Ordinal))
            throw new ExtractionException("TransferLog.CreateTransferInternal changed the Transfer topic/address/hash payload order.");
        return (TypedShape(construction), transferAddress, transferSignatureValue);

        static IEnumerable<TypedAstNode> TypedNodes(TypedAstNode node)
        {
            yield return node;
            foreach (TypedAstNode child in node.Children)
                foreach (TypedAstNode descendant in TypedNodes(child)) yield return descendant;
        }
    }

    private static string ReadHexFieldConstant(
        VariableDeclaratorSyntax field,
        string role,
        int expectedDigits)
    {
        if (field.Parent?.Parent is not FieldDeclarationSyntax declaration ||
            !declaration.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.ConstKeyword)) ||
            Canonical(declaration.Declaration.Type) != "string" ||
            field.Initializer?.Value is not LiteralExpressionSyntax literal ||
            !literal.IsKind(SyntaxKind.StringLiteralExpression))
            throw new ExtractionException($"{role} is not a resolved const string.");
        return NormalizeHex(literal.Token.ValueText, role, expectedDigits);
    }

    private static void ValidateAddressMetadataProjection(
        VariableDeclaratorSyntax systemUserHex, string sourceHex, CSharpCompilation compilation)
    {
        INamedTypeSymbol? type = compilation.GetTypeByMetadataName("Nethermind.Core.Address");
        IFieldSymbol? hex = type?.GetMembers("SystemUserHex").OfType<IFieldSymbol>().SingleOrDefault();
        IPropertySymbol? address = type?.GetMembers("SystemUser").OfType<IPropertySymbol>().SingleOrDefault();
        PropertyDeclarationSyntax? declaration = systemUserHex.SyntaxTree.GetCompilationUnitRoot().DescendantNodes()
            .OfType<PropertyDeclarationSyntax>().SingleOrDefault(static property => property.Identifier.ValueText == "SystemUser");
        if (hex is not { HasConstantValue: true, IsStatic: true } || hex.ConstantValue is not string metadataHex ||
            !string.Equals(sourceHex, metadataHex, StringComparison.OrdinalIgnoreCase) ||
            address is not { IsStatic: true } || !SymbolEqualityComparer.Default.Equals(address.Type, type) ||
            declaration is null || !declaration.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.StaticKeyword)) ||
            declaration.Initializer is null || Canonical(declaration.Initializer.Value) != "new(SystemUserHex)")
            throw new ExtractionException("Address.SystemUser source initializer and accepted Core metadata disagree.");
    }

    private static void ValidateTransferSenderProjection(SourceFile source, SemanticContext context)
    {
        VariableDeclaratorSyntax sender = FindField(source, "TransferLog", "Sender");
        IPropertySymbol? systemUser = context.Compilation.GetTypeByMetadataName("Nethermind.Core.Address")?
            .GetMembers("SystemUser").OfType<IPropertySymbol>().SingleOrDefault();
        SemanticModel model = context.Model(source.Tree);
        if (sender.Initializer is null || Canonical(sender.Initializer.Value) != "Address.SystemUser" ||
            systemUser is null || model.GetOperation(sender.Initializer.Value) is not IPropertyReferenceOperation reference ||
            !SymbolEqualityComparer.Default.Equals(reference.Property, systemUser))
            throw new ExtractionException("TransferLog.Sender no longer binds the exact Address.SystemUser metadata property.");
    }

    private static string ReadHexInitializer(
        VariableDeclaratorSyntax field,
        SemanticContext context,
        string role,
        int expectedDigits)
    {
        if (field.Initializer is null)
            throw new ExtractionException($"{role} has no initializer.");
        SemanticModel model = context.Model(field.SyntaxTree);
        TypedAstNode initializer = LowerTypedAst(model.GetOperation(field.Initializer.Value));
        string[] literals = WalkTypedAst(initializer)
            .Where(static node => node.Kind == "Literal" && node.Constant.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            .Select(static node => node.Constant)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (literals.Length != 1)
            throw new ExtractionException($"{role} does not contain one resolved hexadecimal payload literal.");
        return NormalizeHex(literals[0], role, expectedDigits);
    }

    private static string NormalizeHex(string value, string role, int expectedDigits)
    {
        string normalized = value.Trim();
        if (!normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || normalized.Length != expectedDigits + 2 ||
            normalized[2..].Any(character => !Uri.IsHexDigit(character)))
            throw new ExtractionException($"{role} changed its fixed-width hexadecimal payload.");
        return "0x" + normalized[2..].ToLowerInvariant();
    }

    private static void RequireInvocationTargetIdentity(
        SemanticModel model,
        InvocationExpressionSyntax invocation,
        MethodDeclarationSyntax declaration,
        string role)
    {
        SymbolInfo info = model.GetSymbolInfo(invocation);
        if (info.CandidateReason != CandidateReason.None || info.CandidateSymbols.Length != 0 ||
            model.GetOperation(invocation) is not IInvocationOperation operation || operation.TargetMethod is null)
            throw new ExtractionException($"{role} has no unique resolved invocation target.");
        ISymbol? expected = model.GetDeclaredSymbol(declaration);
        if (expected is null || SymbolIdentity(operation.TargetMethod) != SymbolIdentity(expected))
            throw new ExtractionException($"{role} resolved an invocation outside its exact source declaration.");
    }

    private static NoFrameProof ValidateNoFrameCompletion(MethodDeclarationSyntax method, SemanticContext context)
    {
        LabeledStatementSyntax[] labels = OwnedDescendants(method).OfType<LabeledStatementSyntax>().ToArray();
        LabeledStatementSyntax noFrame = labels.SingleOrDefault(label => label.Identifier.ValueText == "CompleteWithoutFrame")
            ?? throw new ExtractionException("ExecuteEvmCall lost the CompleteWithoutFrame label.");
        LabeledStatementSyntax complete = labels.SingleOrDefault(label => label.Identifier.ValueText == "Complete")
            ?? throw new ExtractionException("ExecuteEvmCall lost the Complete label.");
        LabeledStatementSyntax fail = labels.SingleOrDefault(label => label.Identifier.ValueText == "FailContractCreate")
            ?? throw new ExtractionException("ExecuteEvmCall lost the FailContractCreate label.");

        GotoStatementSyntax[] noFrameGotos = OwnedDescendants(method).OfType<GotoStatementSyntax>()
            .Where(static statement => statement.Expression is IdentifierNameSyntax identifier && identifier.Identifier.ValueText == "CompleteWithoutFrame")
            .OrderBy(static statement => statement.SpanStart)
            .ToArray();
        GotoStatementSyntax[] failGotos = OwnedDescendants(method).OfType<GotoStatementSyntax>()
            .Where(statement => statement.SpanStart > fail.SpanStart && statement.SpanStart < noFrame.SpanStart &&
                statement.Expression is IdentifierNameSyntax identifier && identifier.Identifier.ValueText == "Complete")
            .ToArray();
        if (failGotos.Length != 1)
            throw new ExtractionException($"FailContractCreate must retain exactly one goto Complete bypass, found {failGotos.Length}.");
        GotoStatementSyntax failGoto = failGotos[0];
        if (noFrameGotos.Length != 4)
            throw new ExtractionException($"ExecuteEvmCall must retain exactly four no-frame gotos, found {noFrameGotos.Length}.");

        if (noFrameGotos.Any(statement => statement.SpanStart >= noFrame.SpanStart) ||
            !(failGoto.SpanStart < noFrame.SpanStart && noFrame.SpanStart < complete.SpanStart))
            throw new ExtractionException("ExecuteEvmCall no-frame and FailContractCreate labels changed order.");

        SemanticModel model = context.Model(method.SyntaxTree);
        if (method.Body is not { } methodBody ||
            model.GetDeclaredSymbol(noFrame) is not ILabelSymbol noFrameSymbol ||
            model.GetDeclaredSymbol(complete) is not ILabelSymbol completeSymbol ||
            model.GetDeclaredSymbol(fail) is null ||
            model.GetOperation(complete.Statement) is not IReturnOperation returnOperation ||
            returnOperation.ReturnedValue is null || Canonical(complete.Statement) != "returnstatusCode;" ||
            noFrame.Statement is not IfStatementSyntax { Else: null } accessReport ||
            Canonical(accessReport.Condition) != "tracer.IsTracingAccess" ||
            accessReport.Statement is not BlockSyntax { Statements.Count: 1 } accessBlock ||
            accessBlock.Statements[0] is not ExpressionStatementSyntax accessStatement ||
            !Canonical(accessStatement).StartsWith("tracer.ReportAccess(", StringComparison.Ordinal) ||
            fail.Parent != methodBody || noFrame.Parent != methodBody || complete.Parent != methodBody)
            throw new ExtractionException("ExecuteEvmCall no-frame labels or final return changed their direct source contract.");

        string[] guards =
        [
            "topFrameOutOfGas",
            "!TGasPolicy.TryConsumeCreateStateGas(refgasAvailable)",
            "!deploymentPrepared",
            "env.CodeInfoisnull",
        ];
        string[][] effects =
        [
            ["TraceHaltedTopFrameAction(", "substate=newTransactionSubstate(", "TGasPolicyoogIntrinsicGasStandard=gas.Standard;", "gasConsumed=CompleteEip8037Halt("],
            ["TraceHaltedTopFrameAction(", "substate=newTransactionSubstate(", "WorldState.Restore(snapshot);", "TGasPolicycreateStateOogIntrinsicGasStandard=gas.Standard;", "gasConsumed=CompleteEip8037Halt("],
            ["if(Logger.IsTrace)Logger.Trace(", "WorldState.Restore(snapshot);", "substate=newTransactionSubstate(", "TGasPolicycollisionIntrinsicGasStandard=gas.Standard;", "gasConsumed=RefundOnContractCollision("],
            ["ulongminimalGasLong=TGasPolicy.GetRemainingGas(gas.MinimalGas);", "gasConsumed=minimalGasLong;", "if(!opts.HasFlag(ExecutionOptions.SkipValidation))WorldState.AddToBalance("],
        ];
        for (int index = 0; index < noFrameGotos.Length; index++)
        {
            GotoStatementSyntax statement = noFrameGotos[index];
            IfStatementSyntax? guard = statement.Ancestors().OfType<IfStatementSyntax>().FirstOrDefault();
            if (guard is null || Canonical(guard.Condition) != guards[index] ||
                statement.Parent is not BlockSyntax block || block.Statements.LastOrDefault() != statement ||
                !ReferenceEquals(guard.Statement, block) ||
                model.AnalyzeControlFlow(statement) is not { Succeeded: true, StartPointIsReachable: true })
                throw new ExtractionException($"no-frame goto {index + 1} lost its direct final guard/effect position.");
            if (block.Statements.Count != effects[index].Length + 1 ||
                block.Statements.Take(effects[index].Length).Select(Canonical)
                    .Where((effect, effectIndex) => !effect.StartsWith(effects[index][effectIndex], StringComparison.Ordinal)).Any())
                throw new ExtractionException($"no-frame goto {index + 1} changed its ordered direct effects.");
            RequireGotoTarget(model, statement, noFrameSymbol, $"no-frame goto {index + 1}");
        }

        if (failGoto.Parent != methodBody ||
            model.AnalyzeControlFlow(failGoto) is not { Succeeded: true, StartPointIsReachable: true } ||
            !ReferenceEquals(methodBody.Statements.LastOrDefault(statement => statement.SpanStart < noFrame.SpanStart), failGoto))
            throw new ExtractionException("FailContractCreate goto Complete lost its direct bypass position.");
        RequireGotoTarget(model, failGoto, completeSymbol, "FailContractCreate goto Complete");

        return new NoFrameProof(noFrameGotos.Length, false, true, []);
    }

    private static void RequireGotoTarget(SemanticModel model, GotoStatementSyntax statement, ILabelSymbol target, string role)
    {
        if (model.GetOperation(statement) is not IBranchOperation { BranchKind: BranchKind.GoTo } branch ||
            !SymbolEqualityComparer.Default.Equals(branch.Target, target))
            throw new ExtractionException($"{role} no longer branches to its declared label.");
    }

    private static CompletionLowering LowerCompletionSemantics(
        SourceBinding combineCall,
        SourceBinding combineKernel,
        SourceBinding effectiveBlock,
        SourceBinding buildUp,
        SourceBinding fastToString,
        SourceBinding accessListWarmup,
        SourceBinding coinbaseWarmup,
        SourceBinding recipientWarmup,
        SourceBinding senderWarmup,
        SourceBinding accessHotGuard,
        SourceBinding accessListGuard,
        SourceBinding accessCoinbaseGuard,
        SourceBinding accessRecipientGuard,
        SourceBinding substateCtor,
        SourceBinding substateIsError,
        SourceBinding newAccountStateCost,
        string transferLogPayloadGrammar,
        string transferLogAddress,
        string transferSignature,
        NoFrameProof noFrameProof)
    {
        bool combineMax = combineCall.TypedAst.Kind == "Invocation" && combineCall.TypedAst.Name == "CombineBlockGas" &&
            HasInvocation(combineKernel.TypedAst, "Max") && !HasBinary(combineKernel.TypedAst, "Add");
        bool effectiveFallback = IsEffectiveBlockGasAst(effectiveBlock.TypedAst);
        bool buildUpExact = IsExactBuildUpCondition(buildUp.TypedAst);
        bool fastToStringEnumName = HasReference(fastToString.TypedAst, "OutOfGas") && HasConstant(fastToString.TypedAst, "OutOfGas");
        bool accessHot = IsNegatedReference(accessHotGuard.TypedAst, "UseHotAndColdStorage");
        bool accessTxList = IsReferenceOnly(accessListGuard.TypedAst, "UseTxAccessLists");
        bool accessCoinbase = IsReferenceOnly(accessCoinbaseGuard.TypedAst, "AddCoinbaseToTxAccessList");
        bool accessRecipient = IsReferenceOnly(accessRecipientGuard.TypedAst, "warmUpRecipient");
        bool accessListCall = IsWarmUpCall(accessListWarmup.TypedAst, "accessList", "AccessList", "AccessList");
        bool accessCoinbaseCall = IsWarmUpCall(coinbaseWarmup.TypedAst, "address", "GasBeneficiary", "Address");
        bool accessRecipientCall = IsWarmUpCall(recipientWarmup.TypedAst, "address", "recipient", "Address");
        bool accessSender = IsWarmUpCall(senderWarmup.TypedAst, "address", "SenderAddress", "Address");
        bool substateCtorPreservesNullError = HasReference(substateCtor.TypedAst, "ShouldRevert") &&
            HasConstant(substateCtor.TypedAst, "<null>");
        bool substateIsErrorExact = IsSubstateIsErrorAst(substateIsError.TypedAst);
        if (newAccountStateCost.TypedAst.Kind != "FieldDeclaration" || newAccountStateCost.TypedAst.Name != "NewAccountState" ||
            !long.TryParse(newAccountStateCost.TypedAst.Constant, NumberStyles.Integer, CultureInfo.InvariantCulture, out long newAccountCost) || newAccountCost <= 0)
            throw new ExtractionException("GasCostOf.NewAccountState did not lower to a positive resolved constant.");
        if (!combineMax || !effectiveFallback || !buildUpExact || !fastToStringEnumName ||
            !accessHot || !accessTxList || !accessCoinbase || !accessRecipient || !accessListCall ||
            !accessCoinbaseCall || !accessRecipientCall || !accessSender ||
            !substateCtorPreservesNullError || !substateIsErrorExact)
            throw new ExtractionException("A closed typed source lowering for simple-transfer completion is incomplete.");
        return new CompletionLowering(combineMax, effectiveFallback, buildUpExact, fastToStringEnumName,
            accessHot, accessTxList, accessCoinbase, accessRecipient, accessSender,
            transferLogPayloadGrammar,
            transferLogAddress,
            transferSignature,
            newAccountCost,
            noFrameProof.GotoCount, noFrameProof.CfgProven, noFrameProof.FailContractCreateBypassesNoFrame,
            noFrameProof.Paths);
    }

    private static bool IsEffectiveBlockGasAst(TypedAstNode node)
    {
        node = UnwrapTyped(node);
        if (node.Kind != "Conditional" || node.Children.Length != 3)
            return false;
        TypedAstNode guard = UnwrapTyped(node.Children[0]);
        if (guard.Kind != "Binary" || (guard.Name != "ConditionalOr" && guard.Name != "Or") || guard.Children.Length != 2)
            return false;
        return IsPositiveComparison(guard.Children[0], "BlockGas") &&
            IsPositiveComparison(guard.Children[1], "BlockStateGas") &&
            IsReferenceOnly(node.Children[1], "BlockGas") &&
            IsReferenceOnly(node.Children[2], "SpentGas");
    }

    private static bool IsPositiveComparison(TypedAstNode node, string reference)
    {
        node = UnwrapTyped(node);
        return node.Kind == "Binary" && (node.Name == "GreaterThan" || node.Name == "Greater") && node.Children.Length == 2 &&
            IsReferenceOnly(node.Children[0], reference) && HasConstant(node.Children[1], "0");
    }

    private static bool IsReferenceOnly(TypedAstNode node, string name)
    {
        node = UnwrapTyped(node);
        return (node.Kind is "PropertyReference" or "FieldReference" or "LocalReference" or "ParameterReference") && node.Name == name;
    }

    private static TypedAstNode UnwrapTyped(TypedAstNode node)
    {
        while (node.Kind is "Parenthesized" or "Conversion" && node.Children.Length == 1)
            node = node.Children[0];
        return node;
    }

    private static bool HasBinary(TypedAstNode node, string name) =>
        node.Kind == "Binary" && node.Name == name || node.Children.Any(child => HasBinary(child, name));

    private static bool HasConstant(TypedAstNode node, string value) =>
        node.Constant == value || node.Children.Any(child => HasConstant(child, value));

    private static bool HasInvocationOnField(TypedAstNode node, string method, string field) =>
        node.Kind == "Invocation" && node.Name == method && node.Children.Any(child =>
            (child.Kind == "FieldReference" || child.Kind == "PropertyReference") && child.Name == field) ||
        node.Children.Any(child => HasInvocationOnField(child, method, field));

    private static bool IsWarmUpCall(TypedAstNode node, string parameterName, string expressionName, string typeToken)
    {
        node = UnwrapTyped(node);
        if (node.Kind != "Invocation" || node.Name != "WarmUp") return false;
        (TypedAstNode[] receiver, TypedAstNode[] arguments) = SplitInvocationChildren(node);
        if (receiver.Length != 1 || !TypeMatches(receiver[0].Type, ["StackAccessTracker"]) || arguments.Length != 1)
            return false;
        TypedAstNode argument = arguments[0];
        if (argument.Kind != "Argument" || (parameterName.Length != 0 && argument.ParameterName != parameterName) ||
            (typeToken.Length != 0 && !TypeMatches(argument.Type, [typeToken])) || argument.Children.Length != 1)
            return false;
        return expressionName.Length == 0 || IsReferenceOnly(argument.Children[0], expressionName);
    }

    private static bool IsSubstateIsErrorAst(TypedAstNode node)
    {
        node = UnwrapTyped(node);
        return node.Kind == "Binary" &&
            (node.Name == "ConditionalAnd" || node.Name == "LogicalAnd" || node.Name == "And") &&
            node.Children.Length == 2 && HasReference(node.Children[0], "Error") &&
            IsNegatedReference(node.Children[1], "ShouldRevert");
    }

    private static void RequireSourceOrder(SyntaxNode parent, params SyntaxNode[] nodes)
    {
        for (int index = 1; index < nodes.Length; index++)
        {
            if (nodes[index - 1].SpanStart >= nodes[index].SpanStart)
                throw new ExtractionException($"Simple-transfer source order changed before '{nodes[index].Kind()}'.");
        }
    }

    private static void ValidateCompletion(CompletionShape completion)
    {
        if (completion.Stages.Length != 13 || completion.Branches.Length != 8 || completion.Effects.Length != 19 ||
            completion.Operations.Length != 15 || completion.Adapters.Length != 6 ||
            completion.Lowering is null || completion.Handoff != "TransactionProcessorBase.Execute/6 -> ExecuteSimpleTransfer/15" ||
            completion.ReceiptMode != "receipt-continuation-input-only" ||
            string.IsNullOrWhiteSpace(completion.Lowering.TransferLogPayloadGrammar) ||
            string.IsNullOrWhiteSpace(completion.Lowering.TransferLogAddress) ||
            string.IsNullOrWhiteSpace(completion.Lowering.TransferSignature) ||
            completion.Lowering.NewAccountStateCost <= 0 ||
            completion.Lowering.NoFrameGotoCount != 4 ||
            !completion.Lowering.FailContractCreateBypassesNoFrame || completion.Lowering.NoFrameCfgPaths is null ||
            completion.Lowering.NoFrameCfgPaths.Length != (completion.Lowering.NoFrameCfgProven ? completion.Lowering.NoFrameGotoCount : 0))
            throw new ExtractionException("Simple-transfer completion cardinality or boundary changed.");

        string[] callerRoles =
        [
            "simple-transfer caller", "simple-transfer candidate preparation", "simple-transfer candidate guard",
            "simple-transfer code/delegation guard", "available-gas caller boundary", "caller restore flag",
            "caller effective commit flag", "caller candidate lookup", "caller pre-execution commit guard",
            "caller guarded pre-execution commit", "caller available-gas result", "caller available-gas failure exit",
            "caller selected simple-transfer arm", "caller simple-transfer dispatch", "candidate exclusions",
            "code and delegation exclusion", "recipient and candidate guard",
            "normal code lookup with fork-selected delegation", "selected recipient after code lookup",
        ];
        if (completion.MethodBindings is null || callerRoles.Any(role =>
                completion.MethodBindings.Count(binding => binding.Role == role && binding.Path == TransactionProcessorPath) != 1))
            throw new ExtractionException("Simple-transfer caller provenance is incomplete.");
        foreach (SourceBinding binding in completion.MethodBindings) ValidateBinding(binding);

        for (int index = 0; index < completion.Stages.Length; index++)
        {
            StageShape stage = completion.Stages[index];
            if (stage is null || stage.Ordinal != index + 1 || string.IsNullOrWhiteSpace(stage.Id) || string.IsNullOrWhiteSpace(stage.Contract))
                throw new ExtractionException("Simple-transfer stage order is incomplete.");
            ValidateBinding(stage.Binding, requireReachable: true);
        }

        for (int index = 0; index < completion.Effects.Length; index++)
        {
            EffectShape effect = completion.Effects[index];
            if (effect is null || effect.Ordinal != index + 1 || effect.Reads is null || effect.Writes is null || string.IsNullOrWhiteSpace(effect.FailureVisibility))
                throw new ExtractionException("Simple-transfer effect order is incomplete.");
            ValidateBinding(effect.Binding, requireReachable: true);
        }

        foreach (BranchShape branch in completion.Branches)
        {
            if (branch is null || string.IsNullOrWhiteSpace(branch.Condition) || string.IsNullOrWhiteSpace(branch.Meaning) ||
                !Enum.IsDefined<PredicateKind>(branch.Predicate))
                throw new ExtractionException("Simple-transfer branch lowering is incomplete.");
            ValidateBinding(branch.Binding, requireReachable: true);
        }

        foreach (OperationShape operation in completion.Operations)
        {
            if (operation is null || operation.Ordinal <= 0 || string.IsNullOrWhiteSpace(operation.Formula) || operation.Inputs is null || operation.Outputs is null ||
                operation.Lowering is null || operation.Lowering.Formula != operation.Formula || string.IsNullOrWhiteSpace(operation.Lowering.Grammar) ||
                string.IsNullOrWhiteSpace(operation.Lowering.TargetSymbolIdentity) ||
                string.IsNullOrWhiteSpace(operation.Lowering.ExecutionTerm) ||
                operation.Lowering.TargetSymbolIdentity != operation.Binding.TargetSymbolIdentity ||
                string.IsNullOrWhiteSpace(operation.Lowering.ReturnType) || operation.Lowering.ArgumentNames is null ||
                operation.Lowering.ArgumentTypes is null || operation.Lowering.ArgumentKinds is null ||
                operation.Lowering.ArgumentNames.Length != operation.Lowering.ArgumentTypes.Length ||
                operation.Lowering.ArgumentNames.Length != operation.Lowering.ArgumentKinds.Length)
                throw new ExtractionException("Simple-transfer operation lowering is incomplete.");
            ValidateBinding(operation.Binding, requireReachable: true);
        }

        foreach (AdapterPremise adapter in completion.Adapters)
        {
            if (adapter is null || string.IsNullOrWhiteSpace(adapter.CoherencePredicate) || adapter.Bindings is null || adapter.Bindings.Length == 0)
                throw new ExtractionException("Simple-transfer adapter coherence is incomplete.");
            foreach (SourceBinding binding in adapter.Bindings) ValidateBinding(binding);
        }
    }

    private static void ValidateRoute(RouteShape route)
    {
        if (route.Bindings is null || route.Bindings.Length != 6 ||
            route.Bindings.Take(5).Any(static binding => binding is null ||
                binding.Path != MainnetDiPath || binding.OperationKind != "ExactExternalSourceProjection" || binding.IsReachable) ||
            route.Bindings[0].Member != "Load" ||
            route.Bindings.Skip(1).Take(4).Any(static binding => binding.NodeKind != "InvocationExpression") ||
            route.Bindings[5].Path != TransactionProcessorPath || !route.Bindings[5].SymbolResolved)
            throw new ExtractionException("Standard mainnet DI source projection or processor lineage changed.");
        foreach (SourceBinding binding in route.Bindings)
            ValidateBinding(binding, requireReachable: binding.Path == TransactionProcessorPath);
    }

    private static void ValidateIr(IrDocument document, CompilerClosure compiler)
    {
        if (document.SchemaVersion != SchemaVersion || document.ExtractorVersion != ExtractorVersion ||
            document.RoslynVersion != RoslynVersion || document.LanguageVersion != LanguageVersionText ||
            document.Kernel != Kernel || document.AcceptanceState != AcceptanceState || !IsSha256(document.SourceClosureSha256) ||
            document.Sources.Length != SourceSpecs.Length || document.Dependencies.Length != DependencySpecs.Length + 4 ||
            document.CompilerSources is not { Length: 157 } || document.CompilerReferences is not { Length: 226 })
            throw new ExtractionException("Simple-transfer IR header or source closure changed.");
        ValidateStageASourcePlan(document.StageAPlan);

        foreach (SourceIdentity source in document.Sources)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.Path) || !IsSha256(source.Sha256))
                throw new ExtractionException("Simple-transfer IR contains an invalid source identity.");
        }
        foreach (DependencyIdentity dependency in document.Dependencies)
        {
            if (dependency is null || string.IsNullOrWhiteSpace(dependency.Path) || !IsSha256(dependency.Sha256))
                throw new ExtractionException("Simple-transfer IR contains an invalid dependency identity.");
        }

        ValidateCompilerRoster(document.CompilerSources, document.CompilerReferences, compiler);
        if (document.SourceClosureSha256 != SourceClosureHash(document.Sources, document.CompilerSources, document.CompilerReferences))
            throw new ExtractionException("Simple-transfer IR source closure hash does not match effective compiler inputs.");
        ValidateCompletion(document.Completion);
        if (document.Route.ExcludedRoutes.Length == 0 || document.Exclusions.Length == 0 || document.ArithmeticRules.Length == 0 || document.ExternalCorrespondence.Length == 0)
            throw new ExtractionException("Simple-transfer IR lost its explicit boundary declarations.");
    }

    private static void ValidateManifest(Manifest manifest, CompilerClosure compiler)
    {
        if (manifest.SchemaVersion != SchemaVersion || manifest.ExtractorVersion != ExtractorVersion ||
            manifest.RoslynVersion != RoslynVersion || manifest.LanguageVersion != LanguageVersionText ||
            manifest.Kernel != Kernel || manifest.AcceptanceState != AcceptanceState ||
            !IsSha256(manifest.SourceClosureSha256) || !IsSha256(manifest.CombinedSourceSha256) ||
            !IsSha256(manifest.SemanticIrSha256) || manifest.Sources.Length != SourceSpecs.Length ||
            manifest.Dependencies.Length != DependencySpecs.Length + 4 ||
            manifest.CompilerSources is not { Length: 157 } || manifest.CompilerReferences is not { Length: 226 } ||
            !IsSha256(manifest.StageAPlanSha256))
            throw new ExtractionException("Simple-transfer source manifest header changed.");
        foreach (SourceBinding binding in manifest.Bindings) ValidateBinding(binding);
        ValidateCompilerRoster(manifest.CompilerSources, manifest.CompilerReferences, compiler);
        if (manifest.SourceClosureSha256 != SourceClosureHash(manifest.Sources, manifest.CompilerSources, manifest.CompilerReferences))
            throw new ExtractionException("Simple-transfer manifest source closure hash does not match effective compiler inputs.");
        if (!IsSha256(manifest.Ir.Sha256) || !IsSha256(manifest.Lean.Sha256))
            throw new ExtractionException("Simple-transfer source manifest artifact identities are incomplete.");
    }

    private static void ValidateCompilerRoster(
        SourceIdentity[] sources, ReferenceIdentity[] references, CompilerClosure compiler)
    {
        if (sources is not { Length: 157 } || references is not { Length: 226 } ||
            !sources.SequenceEqual(compiler.EffectiveSources) || !references.SequenceEqual(compiler.References) ||
            sources.Count(static source => source.Role is "semantic" or "compiler-support" or "compiler-generated") != 146 ||
            references.Any(static reference => !reference.Selected || !IsSha256(reference.Sha256) ||
                string.IsNullOrWhiteSpace(reference.Mvid) || string.IsNullOrWhiteSpace(reference.AssemblyName)))
            throw new ExtractionException("Simple-transfer compiler source or reference roster differs from accepted Release inputs.");
    }

    private static void ValidateBinding(SourceBinding? binding, bool requireReachable = false)
    {
        if (binding is null || string.IsNullOrWhiteSpace(binding.Path) || string.IsNullOrWhiteSpace(binding.Owner) ||
            string.IsNullOrWhiteSpace(binding.Member) || string.IsNullOrWhiteSpace(binding.Role) ||
            string.IsNullOrWhiteSpace(binding.NodeKind) || string.IsNullOrWhiteSpace(binding.OperationKind) ||
            string.IsNullOrWhiteSpace(binding.CanonicalSyntax) || string.IsNullOrWhiteSpace(binding.ContainingMember) ||
            binding.Receiver is null || binding.TargetSymbol is null || binding.StatementOrdinal < 0 || binding.ControlFlowBlock < -1 ||
            string.IsNullOrWhiteSpace(binding.TargetSymbolIdentity) ||
            binding.StartLine <= 0 || binding.StartColumn <= 0 || binding.EndLine <= 0 || binding.EndColumn <= 0 ||
            !binding.SymbolResolved && binding.OperationKind is not ("ExactExternalSourceProjection" or "ExternalHookPremise") || !IsClosedTypedAst(binding.TypedAst) ||
            !IsSha256(binding.TokenSha256) || !IsSha256(binding.CanonicalSyntaxSha256) ||
            binding.CanonicalSyntaxSha256 != Sha256(Encoding.UTF8.GetBytes(binding.CanonicalSyntax)) ||
            requireReachable && !binding.IsReachable && binding.OperationKind is not ("ExactExternalSourceProjection" or "ExternalHookPremise" or "CoreMetadataConstant"))
             throw new ExtractionException($"Simple-transfer IR contains an incomplete or tampered source binding: {binding?.Path}/{binding?.Member} ({binding?.Role}), node={binding?.NodeKind}, operation={binding?.OperationKind}, symbolResolved={binding?.SymbolResolved}, target={binding?.TargetSymbolIdentity}, typed={(binding is null ? "null" : TypedShape(binding.TypedAst))}.");
        if (binding.Path.StartsWith("src/Nethermind/Nethermind.Evm/", StringComparison.Ordinal))
        {
            if (!binding.SymbolResolved || binding.OperationKind is "CoreMetadataConstant" or "ExactExternalSourceProjection" or "ExternalHookPremise")
                throw new ExtractionException($"Required compiled EVM binding was replaced by an external projection: {binding.Path}/{binding.Member}.");
            return;
        }
        string mode = ExternalBindingMode(binding.Path, binding.Member);
        if (binding.OperationKind != mode || binding.IsReachable || binding.ControlFlowBlock != -1 ||
            mode == "CoreMetadataConstant" && (!binding.SymbolResolved || binding.TypedAst.Kind != "FieldDeclaration" ||
                binding.TypedAst.Symbol != binding.TargetSymbolIdentity) ||
            mode != "CoreMetadataConstant" && (binding.SymbolResolved || binding.TypedAst.Kind != mode ||
                binding.TargetSymbolIdentity != mode + "|" + binding.Path + "|" + binding.CanonicalSyntaxSha256 ||
                binding.TypedAst.Symbol != binding.TargetSymbolIdentity) ||
            mode == "ExternalHookPremise" && !binding.Role.StartsWith("normal-return external hook premise: ", StringComparison.Ordinal))
            throw new ExtractionException($"External source binding mode or identity changed: {binding.Path}/{binding.Member}.");
    }

    private static bool IsClosedTypedAst(TypedAstNode? node) =>
        node is not null && node.Kind is not (null or "" or "Unresolved") && node.Symbol is not null &&
        (node.Kind is not ("Invocation" or "ObjectCreation" or "PropertyReference" or "FieldReference" or "MethodReference") ||
         !string.IsNullOrWhiteSpace(node.Symbol)) &&
        node.Constant is not null && node.Type is not null && node.TypeIdentity is not null &&
        (string.IsNullOrWhiteSpace(node.Type) || !string.IsNullOrWhiteSpace(node.TypeIdentity)) &&
        node.ParameterName is not null &&
        node.ArgumentKind is not null && node.RefKind is not null && node.Children is not null &&
        node.Children.All(IsClosedTypedAst);

    private static SourceFile[] ReadSources(string root, IReadOnlyDictionary<string, string>? overrides = null) =>
        SourceSpecs.Select(spec => ReadSource(root, spec, overrides)).ToArray();

    private static StageASourcePlan BuildStageASourcePlan(string root, CompilerClosure compiler) =>
        new(
            "standard-mainnet sequential Commit, no restore, tracing-state=false, simple-recipient, post-successful-nonce",
            ValidateReceiptAdapterProjection(root),
            [
                "Consensus receipt adapter exact source projection; runtime selection and normal-return hooks are external premises",
                "receipt adapter passes receiptsTracer itself to Execute after StartNewTxTrace",
                "ITransactionProcessorExtensions.Execute passes exact ExecutionOptions.Commit",
                "Process/ExecuteCore excludes the system processor for the selected ordinary transaction",
                "tracer.IsTracingState=false; no pre-execution commit request on the selected simple-recipient path",
                "the selected AuthorizationList-null route has a zero intrinsic state baseline; its prefix provenance remains an explicit obligation",
            ],
            LowerStageAMethods(compiler));

    private static void ValidateStageASourcePlan(StageASourcePlan? plan)
    {
        if (plan is null || string.IsNullOrWhiteSpace(plan.Scope) ||
            plan.ReceiptAdapterProjectionSha256 != ReceiptAdapterSha256 ||
            plan.ExternalPremises is not { Length: 6 } || plan.Methods is not { Length: 19 })
            throw new ExtractionException("Stage-A typed source plan header or external route projection changed.");
        foreach (StageAMethodPlan method in plan.Methods)
        {
            if (method is null || string.IsNullOrWhiteSpace(method.Path) ||
                string.IsNullOrWhiteSpace(method.Owner) || string.IsNullOrWhiteSpace(method.Member) ||
                method.ParameterCount < 0 || !IsSha256(method.BodySha256) ||
                method.Statements is not { Length: > 0 } || method.Effects is null ||
                method.Branches is null || method.Operands is not { Length: > 0 } ||
                method.Statements.Any(static statement => statement is null || !IsSha256(statement.CanonicalSha256) ||
                    !IsClosedTypedAst(statement.Operation)) ||
                method.Effects.Concat(method.Branches).Concat(method.Operands).Any(static operation =>
                    operation is null || !IsSha256(operation.CanonicalSha256) || !IsClosedTypedAst(operation.Operation)))
                throw new ExtractionException($"Stage-A typed source plan is incomplete: {method?.Owner}.{method?.Member}.");
        }
    }

    private static StageAMethodPlan[] LowerStageAMethods(CompilerClosure compiler)
    {
        (string Path, string Owner, string Member, int Arity, int TopLevel, int Returns)[] admitted =
        [
            (ITransactionProcessorPath, "ITransactionProcessorExtensions", "Execute", 2, 1, 0),
            (TransactionProcessorPath, "TransactionProcessorBase", "Process", 3, -1, -1),
            (TransactionProcessorPath, "TransactionProcessorBase", "ExecuteCore", 3, -1, -1),
            (TransactionProcessorPath, "TransactionProcessorBase", "Execute", 3, -1, -1),
            (TransactionProcessorPath, "TransactionProcessorBase", "Execute", 6, 14, 5),
            (TransactionProcessorPath, "TransactionProcessorBase", "PrepareSimpleTransferFastPath", 4, 6, 2),
            (TransactionProcessorPath, "TransactionProcessorBase", "IsSimpleTransferFastPathCandidate", 2, 1, 0),
            (TransactionProcessorPath, "TransactionProcessorBase", "HasNoExecutableCode", 2, 1, 0),
            (TransactionProcessorPath, "TransactionProcessorBase", "CalculateAvailableGas", 4, 1, 0),
            (TransactionProcessorPath, "TransactionProcessorBase", "ExecuteSimpleTransfer", 15, 22, 1),
            (TransactionProcessorPath, "TransactionProcessorBase", "Refund", 12, -1, -1),
            (TransactionProcessorPath, "TransactionProcessorBase", "UpdateHeaderGasUsedAndPayFees", 11, -1, -1),
            (TransactionProcessorPath, "TransactionProcessorBase", "PayFees", 10, -1, -1),
            (TransactionProcessorPath, "TransactionProcessorBase", "FinalizeTransaction", 12, -1, -1),
            (EthereumGasPolicyPath, "EthereumGasPolicy", "TryCreateAvailableFromIntrinsic", 4, -1, -1),
            (EthereumGasPolicyPath, "EthereumGasPolicy", "TryConsumeStateGas", 2, -1, -1),
            (EthereumGasPolicyPath, "EthereumGasPolicy", "ClearExecutionGas", 1, 1, 0),
            (TransactionGasInitializationKernelPath, "TransactionGasInitializationKernel", "TryCreate", 5, -1, -1),
            (StateGasChargeKernelPath, "StateGasChargeKernel", "TryCharge", 6, -1, -1),
        ];
        List<StageAMethodPlan> plans = [];
        foreach ((string path, string owner, string member, int arity, int topLevel, int returns) in admitted)
        {
            SourceFile source = FindSource(compiler.Sources, path);
            MethodDeclarationSyntax method = owner == "ITransactionProcessorExtensions"
                ? FindCommitExecuteExtension(source, compiler.Compilation)
                : FindMethod(source, owner, member, arity);
            RejectNestedCallables(method);
            if (topLevel >= 0 && (method.Body?.Statements.Count ?? 1) != topLevel ||
                returns >= 0 && (method.Body?.DescendantNodes().OfType<ReturnStatementSyntax>().Count() ?? 0) != returns)
                throw new ExtractionException($"Stage-A {owner}.{member}/{arity} lost its complete statement or return skeleton.");
            if (owner == "ITransactionProcessorExtensions" &&
                (method.ExpressionBody is null || Canonical(method.ExpressionBody.Expression) !=
                    "transactionProcessor.Process(transaction,txTracer,ExecutionOptions.Commit)"))
                throw new ExtractionException("Stage-A standard Execute adapter changed its exact Commit dispatch.");
            plans.Add(LowerStageAMethod(source, method, compiler.Compilation.GetSemanticModel(source.Tree), owner));
        }
        return plans.ToArray();
    }

    private static MethodDeclarationSyntax FindCommitExecuteExtension(SourceFile source, CSharpCompilation compilation)
    {
        const string identity = "Nethermind.Evm.TransactionProcessing.ITransactionProcessorExtensions.Execute/2";
        ClassDeclarationSyntax[] owners = source.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(static declaration => SyntaxTypeIdentity(declaration) ==
                "Nethermind.Evm.TransactionProcessing.ITransactionProcessorExtensions").ToArray();
        if (owners is not [ClassDeclarationSyntax owner] ||
            !owner.Modifiers.Any(static token => token.IsKind(SyntaxKind.PublicKeyword)) ||
            !owner.Modifiers.Any(static token => token.IsKind(SyntaxKind.StaticKeyword)))
            throw new ExtractionException($"Stage-A {identity} lost its exact public static owner.");
        ExtensionBlockDeclarationSyntax[] blocks = owner.Members.OfType<ExtensionBlockDeclarationSyntax>().ToArray();
        if (blocks is not [ExtensionBlockDeclarationSyntax block])
            throw new ExtractionException($"Stage-A {identity} lost its single extension block.");
        ParameterListSyntax[] receiverLists = block.ChildNodes().OfType<ParameterListSyntax>().ToArray();
        if (receiverLists is not [ParameterListSyntax { Parameters: [ParameterSyntax receiver] }] ||
            receiver.Identifier.ValueText != "transactionProcessor" ||
            receiver.Type is null || TypeName(receiver.Type) != "ITransactionProcessor" ||
            receiver.Modifiers.Count != 0)
            throw new ExtractionException($"Stage-A {identity} lost its exact ITransactionProcessor receiver.");
        MethodDeclarationSyntax[] methods = block.Members.OfType<MethodDeclarationSyntax>()
            .Where(static method => method.Identifier.ValueText == "Execute" &&
                method.ParameterList.Parameters.Count == 2).ToArray();
        if (methods is not [MethodDeclarationSyntax method] ||
            !method.Modifiers.Any(static token => token.IsKind(SyntaxKind.PublicKeyword)) ||
            method.Body is not null || method.ExpressionBody is null ||
            method.ParameterList.Parameters[0].Identifier.ValueText != "transaction" ||
            method.ParameterList.Parameters[1].Identifier.ValueText != "txTracer")
            throw new ExtractionException($"Stage-A {identity} lost its unique public expression-bodied declaration.");

        SemanticModel model = compilation.GetSemanticModel(source.Tree);
        INamedTypeSymbol? ownerSymbol = model.GetDeclaredSymbol(owner);
        INamedTypeSymbol? processor = compilation.GetTypeByMetadataName(
            "Nethermind.Evm.TransactionProcessing.ITransactionProcessor");
        INamedTypeSymbol? transaction = compilation.GetTypeByMetadataName("Nethermind.Core.Transaction");
        INamedTypeSymbol? tracer = compilation.GetTypeByMetadataName("Nethermind.Evm.Tracing.ITxTracer");
        INamedTypeSymbol? result = compilation.GetTypeByMetadataName(
            "Nethermind.Evm.TransactionProcessing.TransactionResult");
        INamedTypeSymbol? options = compilation.GetTypeByMetadataName(
            "Nethermind.Evm.TransactionProcessing.ExecutionOptions");
        IParameterSymbol? receiverSymbol = model.GetDeclaredSymbol(receiver);
        IParameterSymbol? transactionSymbol = model.GetDeclaredSymbol(method.ParameterList.Parameters[0]);
        IParameterSymbol? tracerSymbol = model.GetDeclaredSymbol(method.ParameterList.Parameters[1]);
        IMethodSymbol? methodSymbol = model.GetDeclaredSymbol(method);
        IMethodSymbol? processSymbol = processor?.GetMembers("Process").OfType<IMethodSymbol>()
            .SingleOrDefault(static member => member.Parameters.Length == 3);
        IFieldSymbol? commitSymbol = options?.GetMembers("Commit").OfType<IFieldSymbol>().SingleOrDefault();
        if (ownerSymbol is null || !ownerSymbol.IsStatic || processor is null ||
            receiverSymbol is null || !SymbolEqualityComparer.Default.Equals(receiverSymbol.Type, processor) ||
            transactionSymbol is null || !SymbolEqualityComparer.Default.Equals(transactionSymbol.Type, transaction) ||
            tracerSymbol is null || !SymbolEqualityComparer.Default.Equals(tracerSymbol.Type, tracer) ||
            methodSymbol is null || methodSymbol.DeclaredAccessibility != Accessibility.Public ||
            !SymbolEqualityComparer.Default.Equals(methodSymbol.ReturnType, result) ||
            methodSymbol.Locations.Length != 1 || !ReferenceEquals(methodSymbol.Locations[0].SourceTree, source.Tree) ||
            processSymbol is null || commitSymbol is null ||
            model.GetOperation(method.ExpressionBody.Expression) is not IInvocationOperation invocation ||
            !SymbolEqualityComparer.Default.Equals(invocation.TargetMethod, processSymbol) ||
            invocation.Instance is not IParameterReferenceOperation instance ||
            !SymbolEqualityComparer.Default.Equals(instance.Parameter, receiverSymbol) ||
            invocation.Arguments is not [IArgumentOperation first, IArgumentOperation second, IArgumentOperation third] ||
            first.Value is not IParameterReferenceOperation firstReference ||
            !SymbolEqualityComparer.Default.Equals(firstReference.Parameter, transactionSymbol) ||
            second.Value is not IParameterReferenceOperation secondReference ||
            !SymbolEqualityComparer.Default.Equals(secondReference.Parameter, tracerSymbol) ||
            third.Value is not IFieldReferenceOperation field ||
            !SymbolEqualityComparer.Default.Equals(field.Field, commitSymbol))
            throw new ExtractionException($"Stage-A {identity} lost its resolved Commit-to-Process route.");
        return method;
    }

    private static StageAMethodPlan LowerStageAMethod(
        SourceFile source, MethodDeclarationSyntax method, SemanticModel model, string owner)
    {
        SyntaxNode body = Body(method);
        StatementSyntax[] syntaxStatements = method.Body is null ? [] : method.Body.DescendantNodesAndSelf()
            .OfType<StatementSyntax>().Where(static statement => statement is not BlockSyntax).ToArray();
        Dictionary<SyntaxNode, int> ordinals = new(ReferenceEqualityComparer.Instance);
        List<StageAStatementPlan> statements = [];
        if (method.ExpressionBody is not null)
        {
            TypedAstNode expression = LowerTypedAst(model.GetOperation(method.ExpressionBody.Expression));
            if (!IsClosedTypedAst(expression) || expression.Kind == "Absent")
                throw new ExtractionException($"Stage-A {owner}.{method.Identifier.ValueText} has an unresolved expression body.");
            statements.Add(new(0, -1, method.ExpressionBody.Expression.Kind().ToString(),
                Sha256(Encoding.UTF8.GetBytes(Canonical(body))), expression));
        }
        else
        {
            foreach (StatementSyntax statement in syntaxStatements)
            {
                int ordinal = statements.Count;
                int parent = statement.Ancestors().OfType<StatementSyntax>()
                    .Where(static ancestor => ancestor is not BlockSyntax)
                    .Select(ancestor => ordinals.TryGetValue(ancestor, out int index) ? index : -1)
                    .FirstOrDefault(static index => index >= 0, -1);
                TypedAstNode operation = LowerTypedAst(model.GetOperation(statement));
                if (!IsClosedTypedAst(operation) || operation.Kind == "Absent")
                    throw new ExtractionException($"Stage-A {owner}.{method.Identifier.ValueText} has an unresolved statement {ordinal}.");
                statements.Add(new(ordinal, parent, statement.Kind().ToString(),
                    Sha256(Encoding.UTF8.GetBytes(Canonical(statement))), operation));
                ordinals.Add(statement, ordinal);
            }
        }

        IOperation rootOperation = model.GetOperation(body)
            ?? throw new ExtractionException($"Stage-A {owner}.{method.Identifier.ValueText} has no typed body operation.");
        List<StageAOperationPlan> effects = [];
        List<StageAOperationPlan> branches = [];
        List<StageAOperationPlan> operands = [];
        foreach (IOperation operation in StageAOperations(rootOperation))
        {
            if (operation is IAnonymousFunctionOperation or ILocalFunctionOperation or IDelegateCreationOperation or
                ITranslatedQueryOperation or IAwaitOperation or IDynamicInvocationOperation or
                IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation or IDynamicObjectCreationOperation)
                throw new ExtractionException($"Stage-A {owner}.{method.Identifier.ValueText} contains unsupported deferred, callable, or dynamic operation {operation.Kind}.");
            bool effect = operation is IInvocationOperation or IObjectCreationOperation or IArrayCreationOperation or
                ICollectionExpressionOperation or ICoalesceAssignmentOperation or
                IAssignmentOperation or IIncrementOrDecrementOperation or IReturnOperation ||
                operation is IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out };
            bool branch = operation is IConditionalOperation or IConditionalAccessOperation or
                ICoalesceOperation or ICoalesceAssignmentOperation or ISwitchOperation or ILoopOperation ||
                operation is IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr };
            bool operand = operation is IFieldReferenceOperation or IPropertyReferenceOperation or
                ILocalReferenceOperation or IParameterReferenceOperation or IBinaryOperation or IUnaryOperation or ILiteralOperation;
            if (!effect && !branch && !operand) continue;
            int ordinal = method.ExpressionBody is not null ? 0 : operation.Syntax.AncestorsAndSelf()
                .OfType<StatementSyntax>().Where(static statement => statement is not BlockSyntax)
                .Select(statement => ordinals.TryGetValue(statement, out int index) ? index : -1)
                .FirstOrDefault(static index => index >= 0, -1);
            if (ordinal < 0)
                throw new ExtractionException($"Stage-A {owner}.{method.Identifier.ValueText} has an unowned typed operation {operation.Kind}.");
            TypedAstNode typed = LowerTypedAst(operation);
            if (!IsClosedTypedAst(typed))
                throw new ExtractionException($"Stage-A {owner}.{method.Identifier.ValueText} has an open typed operation {operation.Kind}.");
            StageAOperationPlan item = new(ordinal, operation.Kind.ToString(),
                SymbolIdentity(FirstResolvedOperationSymbol(operation)),
                Sha256(Encoding.UTF8.GetBytes(Canonical(operation.Syntax))), typed);
            if (effect) effects.Add(item);
            if (branch) branches.Add(item);
            if (operand) operands.Add(item);
        }
        if (statements.Count == 0 || operands.Count == 0)
            throw new ExtractionException($"Stage-A {owner}.{method.Identifier.ValueText} has an incomplete typed statement/effect/operand ledger.");
        return new StageAMethodPlan(source.RelativePath, owner, method.Identifier.ValueText,
            method.ParameterList.Parameters.Count, Sha256(Encoding.UTF8.GetBytes(Canonical(body))),
            statements.ToArray(), effects.ToArray(), branches.ToArray(), operands.ToArray());
    }

    private static IEnumerable<IOperation> StageAOperations(IOperation root)
    {
        yield return root;
        foreach (IOperation child in root.ChildOperations)
            foreach (IOperation operation in StageAOperations(child)) yield return operation;
    }

    private static IEnumerable<string> StageAStatementLedger(IEnumerable<StageAStatementPlan> statements) =>
        statements.Select(static statement => $"{statement.Ordinal}|{statement.ParentOrdinal}|{statement.SyntaxKind}|{statement.CanonicalSha256}|{TypedShape(statement.Operation)}");

    private static IEnumerable<string> StageAOperationLedger(IEnumerable<StageAOperationPlan> operations) =>
        operations.Select(static operation => $"{operation.StatementOrdinal}|{operation.OperationKind}|{operation.SymbolIdentity}|{operation.CanonicalSha256}|{TypedShape(operation.Operation)}");

    private static string ValidateReceiptAdapterProjection(string root)
    {
        byte[] bytes = File.ReadAllBytes(CompilerReferences.Within(root, ReceiptAdapterPath));
        string sha = Sha256(bytes);
        if (sha != ReceiptAdapterSha256)
            throw new ExtractionException("Stage-A Consensus receipt adapter source projection changed.");
        CompilationUnitSyntax syntax = CSharpSyntaxTree.ParseText(Encoding.UTF8.GetString(bytes), ParseOptions).GetCompilationUnitRoot();
        MethodDeclarationSyntax[] methods = syntax.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(static method => method.Identifier.ValueText == "ProcessTransaction").ToArray();
        if (methods is not [{ Body: { Statements.Count: 5 } body }] ||
            Canonical(body.Statements[1]) != "usingITxTracertracer=receiptsTracer.StartNewTxTrace(currentTx);" ||
            Canonical(body.Statements[2]) != "TransactionResultresult=transactionProcessor.Execute(currentTx,receiptsTracer);" ||
            Canonical(body.Statements[3]) != "receiptsTracer.EndTxTrace();" ||
            Canonical(body.Statements[4]) != "returnresult;")
            throw new ExtractionException("Stage-A receipt adapter must pass receiptsTracer itself after StartNewTxTrace.");
        return sha;
    }

    private static void ValidateOverrideKeys(string root, IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is null) return;
        HashSet<string> compiledPaths = CompilerSources.CompiledPaths(root);
        HashSet<string> admittedPaths = SourceSpecs
            .Where(static spec => !spec.Path.StartsWith("src/Nethermind/Nethermind.Evm/", StringComparison.Ordinal))
            .Select(static spec => spec.Path).ToHashSet(StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in overrides.Keys)
        {
            if (!seen.Add(path) || !compiledPaths.Contains(path) && !admittedPaths.Contains(path))
                throw new ExtractionException("A source override is unknown, duplicated, or differs in case from the admitted compiler/projection roster: " + path);
        }
    }

    private static CompilerClosure LoadCompilerClosure(
        string sourceRoot, SourceFile[] observed, IReadOnlyDictionary<string, string>? overrides = null)
    {
        Dictionary<string, string> compilerOverrides = observed
            .Where(static source => source.RelativePath.StartsWith("src/Nethermind/Nethermind.Evm/", StringComparison.Ordinal))
            .ToDictionary(static source => source.RelativePath, static source => Encoding.UTF8.GetString(source.Bytes), StringComparer.Ordinal);
        if (overrides is not null)
        {
            foreach ((string path, string replacement) in overrides)
                if (path.StartsWith("src/Nethermind/Nethermind.Evm/", StringComparison.Ordinal))
                    compilerOverrides[path] = replacement;
        }
        return CompilerSources.Load(sourceRoot, compilerOverrides);
    }

    private static SourceFile ReadSource(string root, SourceSpec spec, IReadOnlyDictionary<string, string>? overrides)
    {
        string path = Path.GetFullPath(Path.Combine(root, spec.Path));
        EnsureWithin(root, path);
        if (!File.Exists(path)) throw new ExtractionException($"Missing {spec.Role}: {spec.Path}.");
        byte[] bytes = overrides is not null && overrides.TryGetValue(spec.Path, out string? replacement)
            ? Encoding.UTF8.GetBytes(replacement) : File.ReadAllBytes(path);
        SourceText text;
        try
        {
            text = SourceText.From(bytes, bytes.Length, Encoding.UTF8, canBeEmbedded: true);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ExtractionException($"Source {spec.Path} is not strict UTF-8: {exception.Message}");
        }

        SyntaxTree tree = CSharpSyntaxTree.ParseText(text, ParseOptions, spec.Path);
        CompilationUnitSyntax syntax = tree.GetCompilationUnitRoot();
        Diagnostic[] errors = syntax.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0)
            throw new ExtractionException($"Cannot parse {spec.Path}: {string.Join("; ", errors.Select(static diagnostic => diagnostic.GetMessage()))}");
        return new SourceFile(spec.Path, spec.Role, Sha256(bytes), bytes, tree, syntax);
    }

    private static DependencyIdentity[] ReadDependencies(string root)
    {
        List<DependencyIdentity> dependencies = [];
        foreach (DependencySpec spec in DependencySpecs)
        {
            string path = Path.GetFullPath(Path.Combine(root, spec.Path));
            EnsureWithin(root, path);
            if (!File.Exists(path)) throw new ExtractionException($"Missing {spec.Role}: {spec.Path}.");
            string sha = Sha256(File.ReadAllBytes(path));
            if (!string.Equals(sha, spec.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new ExtractionException($"Pinned dependency drift in {spec.Path}: expected {spec.ExpectedSha256}, found {sha}.");
            dependencies.Add(new DependencyIdentity(spec.Path, spec.Role, sha));
        }
        foreach ((string path, string role, string? expected) in new[]
        {
            ("tools/Evm/Lean/SimpleTransferCompletionExtractor/CompilerReferences.cs", "package-local real compiler-reference helper", (string?)null),
            ("tools/Evm/Lean/SimpleTransferCompletionExtractor/CompilerSources.cs", "package-local real compiler-source helper", (string?)null),
            (CompilerSources.PinsPath, "accepted Refund source roster pin", CompilerSources.PinsSha256),
            (CompilerReferences.InventoryPath, "accepted Refund compiler-reference roster pin", CompilerReferences.InventorySha256),
        })
        {
            string absolute = Path.GetFullPath(Path.Combine(root, path));
            EnsureWithin(root, absolute);
            string sha = Sha256(File.ReadAllBytes(absolute));
            if (expected is not null && sha != expected)
                throw new ExtractionException($"Pinned compiler dependency drift in {path}.");
            dependencies.Add(new DependencyIdentity(path, role, sha));
        }
        return dependencies.ToArray();
    }

    private static SemanticContext BuildSemanticContext(SourceFile[] sources, CompilerClosure compiler)
    {
        Dictionary<SyntaxTree, SemanticModel> models = compiler.Sources.ToDictionary(
            static source => source.Tree,
            source => compiler.Compilation.GetSemanticModel(source.Tree));
        foreach (SourceFile source in sources.Where(static source =>
                     source.RelativePath.StartsWith("src/Nethermind/Nethermind.Evm/", StringComparison.Ordinal)))
            if (!models.ContainsKey(source.Tree))
                throw new ExtractionException("Required EVM source missing from compiled Roslyn tree roster: " + source.RelativePath);
        SourceFile processor = FindSource(sources, TransactionProcessorPath);
        SourceFile virtualMachine = FindSource(sources, VirtualMachineSignaturePath);
        ValidateVirtualMachineStaticsSignature(virtualMachine);
        ValidateVirtualMachineStaticsBindings(processor, virtualMachine, models[processor.Tree]);
        ValidateStandardProcessorLineage(processor, compiler.Compilation, models[processor.Tree]);
        return new SemanticContext(models, compiler.Compilation);
    }

    private static void ValidateStandardProcessorLineage(
        SourceFile processor, CSharpCompilation compilation, SemanticModel model)
    {
        TypeDeclarationSyntax genericDeclaration = processor.Root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Single(type => type.Identifier.ValueText == "TransactionProcessorBase" && type.TypeParameterList?.Parameters.Count == 1);
        TypeDeclarationSyntax standardDeclaration = FindType(processor, "EthereumTransactionProcessorBase");
        TypeDeclarationSyntax concreteDeclaration = FindType(processor, "EthereumTransactionProcessor");
        INamedTypeSymbol? generic = model.GetDeclaredSymbol(genericDeclaration);
        INamedTypeSymbol? standard = model.GetDeclaredSymbol(standardDeclaration);
        INamedTypeSymbol? concrete = model.GetDeclaredSymbol(concreteDeclaration);
        INamedTypeSymbol? gasPolicy = compilation.GetTypeByMetadataName("Nethermind.Evm.GasPolicy.EthereumGasPolicy");
        INamedTypeSymbol? processorInterface = compilation.GetTypeByMetadataName("Nethermind.Evm.TransactionProcessing.ITransactionProcessor");
        if (generic is not { Arity: 1 } || standard is null || concrete is null || gasPolicy is null ||
            generic.ContainingNamespace.ToDisplayString() != "Nethermind.Evm.TransactionProcessing" ||
            standard.ContainingNamespace.ToDisplayString() != "Nethermind.Evm.TransactionProcessing" ||
            concrete.ContainingNamespace.ToDisplayString() != "Nethermind.Evm.TransactionProcessing" ||
            standard.BaseType is not { } standardBase ||
            !SymbolEqualityComparer.Default.Equals(standardBase.OriginalDefinition, generic) ||
            standardBase.TypeArguments.Length != 1 ||
            !SymbolEqualityComparer.Default.Equals(standardBase.TypeArguments[0], gasPolicy) ||
            !SymbolEqualityComparer.Default.Equals(concrete.BaseType, standard) || !concrete.IsSealed ||
            concrete.Locations.Length != 1 || !ReferenceEquals(concrete.Locations[0].SourceTree, processor.Tree))
            throw new ExtractionException("Standard Ethereum processor source lineage or shadow identity changed.");
        MethodDeclarationSyntax processDeclaration = FindMethod(processor, "TransactionProcessorBase", "Process", 3);
        IMethodSymbol? declaredProcess = model.GetDeclaredSymbol(processDeclaration);
        IMethodSymbol? genericProcess = generic.GetMembers("Process").OfType<IMethodSymbol>()
            .SingleOrDefault(static method => method.Parameters.Length == 3);
        IMethodSymbol? interfaceProcess = processorInterface?.GetMembers("Process").OfType<IMethodSymbol>()
            .SingleOrDefault(static method => method.Parameters.Length == 3);
        IMethodSymbol? inheritedProcess = standardBase.GetMembers("Process").OfType<IMethodSymbol>()
            .SingleOrDefault(static method => method.Parameters.Length == 3);
        if (genericProcess is null || declaredProcess is null || interfaceProcess is null || inheritedProcess is null ||
            !SymbolEqualityComparer.Default.Equals(declaredProcess, genericProcess) ||
            !SymbolEqualityComparer.Default.Equals(inheritedProcess.OriginalDefinition, genericProcess) ||
            !SymbolEqualityComparer.Default.Equals(standard.FindImplementationForInterfaceMember(interfaceProcess), inheritedProcess) ||
            !SymbolEqualityComparer.Default.Equals(concrete.FindImplementationForInterfaceMember(interfaceProcess), inheritedProcess))
            throw new ExtractionException("Standard ITransactionProcessor.Process/3 no longer dispatches to the inherited generic Process source.");
        string[] ownedMethods =
        [
            "Process", "Execute", "PrepareSimpleTransferFastPath", "IsSimpleTransferFastPathCandidate", "HasNoExecutableCode",
            "CalculateAvailableGas", "ExecuteSimpleTransfer", "ExecuteEvmCall", "PayValue", "PayFees", "Refund",
            "PayRefund", "ShouldRefundGas", "FinalizeTransaction", "UpdateHeaderGasUsedAndPayFees",
            "ReportSimpleTransferAccess", "WarmUpTxAccesses", "RefundOnContractCollision",
        ];
        foreach (INamedTypeSymbol derived in new[] { standard, concrete })
            if (derived.GetMembers().OfType<IMethodSymbol>().Any(method => ownedMethods.Contains(method.Name, StringComparer.Ordinal)))
                throw new ExtractionException("Standard Ethereum processor lineage shadows an admitted completion helper.");
    }

    private static void ValidateVirtualMachineStaticsSignature(SourceFile source)
    {
        MethodDeclarationSyntax[] methods = source.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == "RestoreRipemdTouch" &&
                SyntaxTypeIdentity(method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()) ==
                "Nethermind.Evm.VirtualMachineStatics")
            .ToArray();
        if (methods.Length != 1)
            throw new ExtractionException("VirtualMachineStatics.RestoreRipemdTouch source must contain exactly one declaration.");

        MethodDeclarationSyntax method = methods[0];
        if (method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() is not ClassDeclarationSyntax type ||
            type.TypeParameterList is not null ||
            type.Members.Count(static member => member is MethodDeclarationSyntax candidate && candidate.Identifier.ValueText == "RestoreRipemdTouch") != 1)
            throw new ExtractionException("VirtualMachineStatics.RestoreRipemdTouch source type must be non-generic.");
        if (Canonical(method.ReturnType) != "void" ||
            Canonical(method.ParameterList) != "(IWorldStateworldState,IReleaseSpecspec,boolshouldRestore)" ||
            !method.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.InternalKeyword)) ||
            !method.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.StaticKeyword)))
            throw new ExtractionException("VirtualMachineStatics.RestoreRipemdTouch source signature changed.");
        if (method.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.PublicKeyword) || modifier.IsKind(SyntaxKind.PrivateKeyword) ||
            modifier.IsKind(SyntaxKind.ProtectedKeyword) || modifier.IsKind(SyntaxKind.FileKeyword)))
            throw new ExtractionException("VirtualMachineStatics.RestoreRipemdTouch source accessibility changed.");
    }

    private static void ValidateVirtualMachineStaticsBindings(SourceFile processor, SourceFile virtualMachine, SemanticModel model)
    {
        InvocationExpressionSyntax[] invocations = processor.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(static invocation => InvocationName(invocation) == "RestoreRipemdTouch" &&
                Canonical(invocation.Expression) == "VirtualMachineStatics.RestoreRipemdTouch").ToArray();
        if (invocations.Length != 2)
            throw new ExtractionException("TransactionProcessor must retain exactly two direct VM source calls.");
        foreach (InvocationExpressionSyntax invocation in invocations)
        {
            SymbolInfo symbolInfo = model.GetSymbolInfo(invocation);
            if (symbolInfo.CandidateReason != CandidateReason.None || symbolInfo.CandidateSymbols.Length != 0)
                throw new ExtractionException("VM source invocation resolved through a candidate or ambiguous symbol.");
            if (model.GetOperation(invocation) is not IInvocationOperation operation)
                throw new ExtractionException("VM source invocation did not resolve to an invocation operation.");
            IMethodSymbol method = operation.TargetMethod;
            if (IsErrorSymbol(method) || method.ContainingType is null || IsErrorSymbol(method.ContainingType) ||
                method.Name != "RestoreRipemdTouch" ||
                method.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != "Nethermind.Evm.VirtualMachineStatics" ||
                method.ContainingAssembly.Identity.Name != "Nethermind.Evm" || !method.IsStatic ||
                method.DeclaredAccessibility != Accessibility.Internal || !method.ReturnsVoid || method.Parameters.Length != 3 ||
                method.Parameters[0].Name != "worldState" || method.Parameters[1].Name != "spec" || method.Parameters[2].Name != "shouldRestore" ||
                method.Parameters.Any(static parameter => parameter.RefKind != RefKind.None) ||
                method.Parameters[0].Type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != "Nethermind.Evm.State.IWorldState" ||
                method.Parameters[1].Type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != "Nethermind.Core.Specs.IReleaseSpec" ||
                method.Parameters[2].Type.SpecialType != SpecialType.System_Boolean || method.Locations.Length != 1 ||
                !method.Locations[0].IsInSource || !ReferenceEquals(method.Locations[0].SourceTree, virtualMachine.Tree))
                throw new ExtractionException("VM source invocation did not bind to the exact production declaration.");
        }
    }

    private static string SyntaxTypeIdentity(TypeDeclarationSyntax? type)
    {
        if (type is null) return string.Empty;
        string namespaceName = string.Join('.', type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(static declaration => declaration.Name.ToString()));
        string[] containingTypes = type.Ancestors().OfType<TypeDeclarationSyntax>().Reverse().Append(type)
            .Select(static declaration => declaration.Identifier.ValueText +
                (declaration.TypeParameterList is null ? string.Empty : $"`{declaration.TypeParameterList.Parameters.Count}"))
            .ToArray();
        string typeName = string.Join('.', containingTypes);
        return namespaceName.Length == 0 ? typeName : namespaceName + "." + typeName;
    }

    private static SourceFile FindSource(SourceFile[] sources, string path) =>
        sources.Single(source => string.Equals(source.RelativePath, path, StringComparison.Ordinal));

    private static MethodDeclarationSyntax FindMethod(SourceFile source, string owner, string name, int parameterCount)
    {
        MethodDeclarationSyntax[] methods = source.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == name && method.ParameterList.Parameters.Count == parameterCount)
            .Where(static method => method.ExplicitInterfaceSpecifier is null)
            .Where(method => method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText == owner)
            .ToArray();
        if (methods.Length != 1)
            throw new ExtractionException($"Expected one {owner}.{name}/{parameterCount} declaration in {source.RelativePath}, found {methods.Length}.");
        return methods[0];
    }

    private static MethodDeclarationSyntax FindUniqueMethod(SourceFile source, string name)
    {
        MethodDeclarationSyntax[] methods = source.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == name)
            .ToArray();
        if (methods.Length != 1)
            throw new ExtractionException($"Expected one {name} declaration in {source.RelativePath}, found {methods.Length}.");
        return methods[0];
    }

    private static MethodDeclarationSyntax FindMethodWithParameterToken(SourceFile source, string owner, string name, int parameterCount, string token)
    {
        MethodDeclarationSyntax[] methods = source.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == name && method.ParameterList.Parameters.Count == parameterCount)
            .Where(method => method.Ancestors().OfType<TypeDeclarationSyntax>().Any(type => type.Identifier.ValueText == owner))
            .Where(method => method.ParameterList.Parameters[0].Type is TypeSyntax parameterType && TypeName(parameterType) == token)
            .ToArray();
        if (methods.Length != 1)
            throw new ExtractionException($"Expected one {owner}.{name}/{parameterCount} declaration containing {token} in {source.RelativePath}, found {methods.Length}.");
        return methods[0];
    }

    private static ConstructorDeclarationSyntax FindConstructor(SourceFile source, string owner, int parameterCount)
    {
        ConstructorDeclarationSyntax[] constructors = source.Root.DescendantNodes().OfType<ConstructorDeclarationSyntax>()
            .Where(constructor => constructor.Identifier.ValueText == owner && constructor.ParameterList.Parameters.Count == parameterCount)
            .ToArray();
        if (constructors.Length != 1)
            throw new ExtractionException($"Expected one {owner} constructor/{parameterCount} in {source.RelativePath}, found {constructors.Length}.");
        return constructors[0];
    }

    private static TypeDeclarationSyntax FindType(SourceFile source, string name)
    {
        TypeDeclarationSyntax[] types = source.Root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(type => type.Identifier.ValueText == name).ToArray();
        if (types.Length != 1) throw new ExtractionException($"Expected one type {name} in {source.RelativePath}, found {types.Length}.");
        return types[0];
    }

    private static PropertyDeclarationSyntax FindProperty(SourceFile source, string owner, string name)
    {
        PropertyDeclarationSyntax[] properties = source.Root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Where(property => property.Identifier.ValueText == name)
            .Where(property => property.Ancestors().OfType<TypeDeclarationSyntax>().Any(type => type.Identifier.ValueText == owner))
            .ToArray();
        if (properties.Length != 1)
            throw new ExtractionException($"Expected one {owner}.{name} property in {source.RelativePath}, found {properties.Length}.");
        return properties[0];
    }

    private static VariableDeclaratorSyntax FindField(SourceFile source, string owner, string name)
    {
        VariableDeclaratorSyntax[] fields = source.Root.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .Where(field => field.Declaration.Variables.Count == 1 && field.Declaration.Variables[0].Identifier.ValueText == name)
            .Where(field => field.Ancestors().OfType<TypeDeclarationSyntax>().Any(type => type.Identifier.ValueText == owner))
            .Select(static field => field.Declaration.Variables[0])
            .ToArray();
        if (fields.Length != 1)
            throw new ExtractionException($"Expected one {owner}.{name} field in {source.RelativePath}, found {fields.Length}.");
        return fields[0];
    }

    private static InvocationExpressionSyntax FindInvocation(SyntaxNode parent, string name, int occurrence = 0)
    {
        InvocationExpressionSyntax[] invocations = OwnedDescendants(parent).OfType<InvocationExpressionSyntax>()
            .Where(invocation => InvocationName(invocation) == name).ToArray();
        if ((uint)occurrence >= (uint)invocations.Length)
            throw new ExtractionException($"Expected invocation {name}/{occurrence} in {ContainingName(parent)}, found {invocations.Length}.");
        return invocations[occurrence];
    }

    private static IEnumerable<SyntaxNode> OwnedDescendants(SyntaxNode owner) =>
        owner.DescendantNodes(descendIntoChildren: static node =>
            node is not LocalFunctionStatementSyntax and not AnonymousFunctionExpressionSyntax);

    private static ExpressionSyntax FindExactExpression(MethodDeclarationSyntax method, string expected)
    {
        string expectedCanonical = Canonical(SyntaxFactory.ParseExpression(expected));
        ExpressionSyntax[] matches = OwnedDescendants(method).OfType<ExpressionSyntax>()
            .Where(expression => Canonical(expression) == expectedCanonical).ToArray();
        if (matches.Length != 1)
            throw new ExtractionException($"Simple-transfer caller expression changed: {method.Identifier.ValueText}/{expected}.");
        return matches[0];
    }

    private static MethodDeclarationSyntax RequireMethodCfgOwner(SyntaxNode node, string role)
    {
        if (node.AncestorsAndSelf().Any(static ancestor =>
                ancestor is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax))
            throw new ExtractionException($"{role} is inside a nested callable.");
        return node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault()
            ?? throw new ExtractionException($"{role} is not inside a method CFG.");
    }

    private static InvocationExpressionSyntax FindInvocationOnField(
        SyntaxNode parent,
        string name,
        string field,
        int occurrence = 0)
    {
        InvocationExpressionSyntax[] invocations = OwnedDescendants(parent).OfType<InvocationExpressionSyntax>()
            .Where(invocation => invocation.Expression is MemberAccessExpressionSyntax member &&
                member.Name.Identifier.ValueText == name &&
                member.Expression is IdentifierNameSyntax identifier &&
                identifier.Identifier.ValueText == field)
            .ToArray();
        if ((uint)occurrence >= (uint)invocations.Length)
            throw new ExtractionException($"Expected invocation {field}.{name}/{occurrence} in {ContainingName(parent)}, found {invocations.Length}.");
        return invocations[occurrence];
    }

    private static InvocationExpressionSyntax FindInvocationInLabel(SourceFile source, string label, string name)
    {
        LabeledStatementSyntax[] labels = source.Root.DescendantNodes().OfType<LabeledStatementSyntax>()
            .Where(statement => statement.Identifier.ValueText == label)
            .ToArray();
        if (labels.Length != 1)
            throw new ExtractionException($"Expected one {label}: label in {source.RelativePath}, found {labels.Length}.");
        InvocationExpressionSyntax[] invocations = OwnedDescendants(labels[0].Statement)
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => InvocationName(invocation) == name)
            .ToArray();
        if (invocations.Length != 1)
            throw new ExtractionException($"Expected one {name} invocation under {label}: in {source.RelativePath}, found {invocations.Length}.");
        return invocations[0];
    }

    private static InvocationExpressionSyntax FindRouteInvocation(SourceFile source, string canonical)
    {
        InvocationExpressionSyntax[] invocations = source.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => invocation.Expression is MemberAccessExpressionSyntax member &&
                Canonical(member.Name) + "()" == canonical && invocation.ArgumentList.Arguments.Count == 0 &&
                invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText == "Load").ToArray();
        if (invocations.Length != 1)
            throw new ExtractionException($"Expected one DI registration {canonical}, found {invocations.Length}.");
        return invocations[0];
    }

    private static void ValidateInitRegistrationProjection(SourceFile source)
    {
        MethodDeclarationSyntax load = FindMethod(source, "BlockProcessingModule", "Load", 1);
        string[] requiredNamespaces =
        [
            "Nethermind.Evm.TransactionProcessing", "Nethermind.Evm.State", "Nethermind.State",
        ];
        string[] registeredTypeNames =
        [
            "EthereumTransactionProcessor", "ITransactionProcessor", "WorldState", "IWorldState",
            "CacheCodeInfoRepository", "ICodeInfoRepository", "BlobBaseFeeCalculator",
        ];
        if (requiredNamespaces.Any(name => !source.Root.Usings.Any(usingDirective =>
                usingDirective.Alias is null && Canonical(usingDirective.Name!) == name)) ||
            source.Root.Usings.Any(usingDirective => usingDirective.Alias is not null &&
                registeredTypeNames.Contains(usingDirective.Alias.Name.Identifier.ValueText, StringComparer.Ordinal)) ||
            source.Root.DescendantNodes().OfType<TypeDeclarationSyntax>().Any(type =>
                registeredTypeNames.Contains(type.Identifier.ValueText, StringComparer.Ordinal)))
            throw new ExtractionException("Standard mainnet Init registration type imports or shadow declarations changed.");
        string[] registrations =
        [
            "AddScoped<ITransactionProcessor.IBlobBaseFeeCalculator,BlobBaseFeeCalculator>()",
            "AddScoped<ITransactionProcessor,EthereumTransactionProcessor>()",
            "AddScoped<ICodeInfoRepository,CacheCodeInfoRepository>()",
            "AddScoped<IWorldState,WorldState>()",
        ];
        int previous = load.SpanStart;
        List<InvocationExpressionSyntax> selected = [];
        foreach (string registration in registrations)
        {
            InvocationExpressionSyntax invocation = FindRouteInvocation(source, registration);
            if (invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() != load ||
                invocation.Expression is not MemberAccessExpressionSyntax member || member.Name.SpanStart <= previous ||
                !IsBuilderChain(member.Expression))
                throw new ExtractionException("Standard mainnet Init registration source order changed.");
            previous = member.Name.SpanStart;
            selected.Add(invocation);
        }
        string[] services =
        [
            "ITransactionProcessor.IBlobBaseFeeCalculator", "ITransactionProcessor", "ICodeInfoRepository", "IWorldState",
        ];
        foreach (string service in services)
        {
            InvocationExpressionSyntax[] registrationsForService = OwnedDescendants(load).OfType<InvocationExpressionSyntax>()
                .Where(invocation => invocation.Expression is MemberAccessExpressionSyntax
                {
                    Name: GenericNameSyntax { TypeArgumentList.Arguments.Count: > 0 } generic
                } && generic.Identifier.ValueText.StartsWith("Add", StringComparison.Ordinal) &&
                    Canonical(generic.TypeArgumentList.Arguments[0]) == service)
                .ToArray();
            if (registrationsForService.Length != 1 || !selected.Contains(registrationsForService[0]))
                throw new ExtractionException($"Standard mainnet Init registration for {service} is shadowed or overridden.");
        }

        static bool IsBuilderChain(ExpressionSyntax expression)
        {
            while (expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax nested })
                expression = nested.Expression;
            return expression is IdentifierNameSyntax { Identifier.ValueText: "builder" };
        }
    }

    private static SyntaxNode FindObject(SyntaxNode parent, string typeName)
    {
        ObjectCreationExpressionSyntax[] objects = OwnedDescendants(parent).OfType<ObjectCreationExpressionSyntax>()
            .Where(objectCreation => TypeName(objectCreation.Type) == typeName).ToArray();
        ImplicitObjectCreationExpressionSyntax[] implicitObjects = OwnedDescendants(parent).OfType<ImplicitObjectCreationExpressionSyntax>().ToArray();
        int count = objects.Length + implicitObjects.Length;
        if (count != 1) throw new ExtractionException($"Expected one object construction {typeName} in {ContainingName(parent)}, found {count}.");
        return objects.Length == 1 ? objects[0] : implicitObjects[0];
    }

    private static SyntaxNode FindMemberAccess(SyntaxNode parent, string name, int occurrence)
    {
        SyntaxNode[] accesses = OwnedDescendants(parent).Where(node => node switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText == name,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText == name,
            _ => false,
        }).ToArray();
        if ((uint)occurrence >= (uint)accesses.Length)
            throw new ExtractionException($"Expected member access {name}/{occurrence} in {ContainingName(parent)}, found {accesses.Length}.");
        return accesses[occurrence];
    }

    private static ExpressionSyntax FindCondition(
        SyntaxNode parent,
        SemanticContext context,
        Func<TypedAstNode, bool> predicate,
        int occurrence)
    {
        IfStatementSyntax[] statements = OwnedDescendants(parent).OfType<IfStatementSyntax>().ToArray();
        ExpressionSyntax[] roots = statements.Select(static statement => statement.Condition)
            .Where(condition => predicate(LowerTypedAst(context.Model(condition.SyntaxTree).GetOperation(condition))))
            .ToArray();
        ExpressionSyntax[] conditions = roots.Length != 0 ? roots : statements
            .SelectMany(static statement => statement.Condition.DescendantNodesAndSelf().OfType<ExpressionSyntax>())
            .Where(condition => predicate(LowerTypedAst(context.Model(condition.SyntaxTree).GetOperation(condition))))
            .ToArray();
        if ((uint)occurrence >= (uint)conditions.Length)
            throw new ExtractionException($"Expected admitted condition/{occurrence} in {ContainingName(parent)}, found {conditions.Length}.");
        return conditions[occurrence];
    }

    private static OperationLowering LowerTypedOperation(string id, SourceBinding binding) => id switch
    {
        "incrementEmptyCalls" => LowerInvocationOperation(
            id, binding, "IncrementEmptyCalls", "unconditionalMetric", ["Metrics"], ["void"], ReceiverMode.Static, []),
        "newAccountStateCost" => LowerInvocationOperation(
            id, binding, "GetNewAccountStateCost", "newAccountCost", ["IGasPolicy<TSelf>", "IGasPolicy", "EthereumGasPolicy"], ["long", "Int64"], ReceiverMode.Static, []),
        "consumeStateGas" => LowerInvocationOperation(
            id, binding, "TryConsumeStateGas", "stateChargeResult", ["IGasPolicy<TSelf>", "IGasPolicy", "EthereumGasPolicy"], ["bool", "Boolean"], ReceiverMode.Static,
            [Arg("gas", ["TSelf", "TGasPolicy", "EthereumGasPolicy"], "Ref", childKind: "ParameterReference", childName: "gasAvailable"),
             Arg("stateGasCost", ["long", "Int64"], "None", childKind: "Invocation", childName: "GetNewAccountStateCost")]),
        "payValueRequest" => LowerInvocationOperation(
            id, binding, "PayValue", "payValue", ["TransactionProcessorBase<TGasPolicy>", "TransactionProcessorBase"], ["void"], ReceiverMode.Instance,
            [Arg("tx", ["Transaction"], "None", childKind: "ParameterReference", childName: "tx"),
             Arg("spec", ["IReleaseSpec"], "None", childKind: "ParameterReference", childName: "spec"),
             Arg("opts", ["ExecutionOptions"], "None", childKind: "ParameterReference", childName: "opts")],
            receiverKind: "InstanceReference", receiverTypeTokens: ["TransactionProcessorBase<TGasPolicy>", "TransactionProcessorBase"]),
        // The source call is statically bound to the reduced extension method.  Roslyn
        // therefore retains the extension receiver as the first ordinary argument,
        // rather than as IInvocationOperation.Instance.  Keep that distinction in the
        // admitted grammar so a receiver/overload mutation cannot retain this lowering.
        "addRecipientRequest" => LowerInvocationOperation(
            id, binding, "AddToBalanceAndCreateIfNotExists", "recipientWrite", ["WorldStateExtensions"], ["bool", "Boolean"], ReceiverMode.Static,
            [Arg("worldState", ["IWorldState"], "None", childKind: "PropertyReference", childName: "WorldState"),
             Arg("address", ["Address"], "None", childKind: "ParameterReference", childName: "recipient"),
             Arg("balanceChange", ["UInt256"], "In", childKind: "Conditional"),
             Arg("spec", ["IReleaseSpec"], "None", childKind: "ParameterReference", childName: "spec")],
            sourceReceiverSyntax: "WorldState.AddToBalanceAndCreateIfNotExists"),
        "actionStartRequest" => LowerInvocationOperation(
            id, binding, "TraceSimpleTransferActionStart", "actionStart", ["TransactionProcessorBase<TGasPolicy>", "TransactionProcessorBase"], ["void"], ReceiverMode.Static,
            [Arg("tx", ["Transaction"], "None", childKind: "ParameterReference", childName: "tx"),
             Arg("recipient", ["Address"], "None", childKind: "ParameterReference", childName: "recipient"),
             Arg("tracer", ["ITxTracer"], "None", childKind: "ParameterReference", childName: "tracer"),
             Arg("value", ["UInt256"], "In", childKind: "LocalReference", childName: "value"),
             Arg("gasAvailable", ["TGasPolicy", "EthereumGasPolicy"], "In", childKind: "ParameterReference", childName: "gasAvailable")]),
        "clearExecutionGas" => LowerInvocationOperation(
            id, binding, "ClearExecutionGas", "clearGas", ["IGasPolicy<TSelf>", "IGasPolicy", "EthereumGasPolicy"], ["void"], ReceiverMode.Static,
            [Arg("gas", ["TSelf", "TGasPolicy", "EthereumGasPolicy"], "Ref", childKind: "ParameterReference", childName: "gasAvailable")]),
        "transferLogRequest" => LowerInvocationOperation(
            id, binding, "CreateTransfer", "transferLog", ["TransferLog"], ["LogEntry"], ReceiverMode.Static,
            [Arg("from", ["Address"], "None", childKind: "PropertyReference", childName: "SenderAddress"),
             Arg("to", ["Address"], "None", childKind: "ParameterReference", childName: "recipient"),
             Arg("amount", ["UInt256"], "In", childKind: "LocalReference", childName: "value")]),
        "substateConstruction" => LowerObjectCreationOperation(
            id, binding, "TransactionSubstate", "substate", ["TransactionSubstate"],
            [Arg("bytes", ["ReadOnlyMemory<>"], "None", childKind: "DefaultValue"),
             Arg("refund", ["long", "Int64"], "None", childKind: "Literal"),
             Arg("destroyList", ["JournalSet<>"], "None", childKind: "Literal"),
             Arg("logs", ["JournalCollection<>"], "None", childKind: "LocalReference", childName: "logs"),
             Arg("shouldRevert", ["bool", "Boolean"], "None", childKind: "Literal"),
             Arg("isTracerConnected", ["bool", "Boolean"], "None", childKind: "Literal"),
             Arg("evmExceptionType", ["EvmExceptionType"], "None", childKind: "Conditional"),
             Arg("logger", ["ILogger"], "None", childKind: "PropertyReference", childName: "Logger")]),
        "refundRequest" => LowerInvocationOperation(
            id, binding, "Refund", "settlement", ["TransactionProcessorBase<TGasPolicy>", "TransactionProcessorBase"], ["GasConsumed"], ReceiverMode.Instance,
            [Arg("tx", ["Transaction"], "None", childKind: "ParameterReference", childName: "tx"),
             Arg("header", ["BlockHeader"], "None", childKind: "ParameterReference", childName: "header"),
             Arg("spec", ["IReleaseSpec"], "None", childKind: "ParameterReference", childName: "spec"),
             Arg("opts", ["ExecutionOptions"], "None", childKind: "ParameterReference", childName: "opts"),
             Arg("substate", ["TransactionSubstate"], "In", childKind: "LocalReference", childName: "substate"),
             Arg("unspentGas", ["TGasPolicy", "EthereumGasPolicy"], "In", childKind: "ParameterReference", childName: "gasAvailable"),
             Arg("gasPrice", ["UInt256"], "In", childKind: "ParameterReference", childName: "opcodeGasPrice"),
             Arg("codeInsertRefunds", ["ulong", "UInt64"], "None", childKind: "Literal"),
             Arg("floorGas", ["TGasPolicy", "EthereumGasPolicy"], "In", childKind: "LocalReference", childName: "floorGas"),
             Arg("intrinsicGasStandard", ["TGasPolicy", "EthereumGasPolicy"], "In", childKind: "LocalReference", childName: "standardGas"),
             Arg("postIntrinsicStateReservoir", ["long", "Int64"], "None", childKind: "LocalReference", childName: "postIntrinsicStateReservoir"),
             Arg("topLevelCreateStateGasCharged", ["bool", "Boolean"], "None", Optional: true)],
            receiverKind: "InstanceReference", receiverTypeTokens: ["TransactionProcessorBase<>"]),
        "accessRequest" => LowerInvocationOperation(
            id, binding, "ReportSimpleTransferAccess", "access", ["TransactionProcessorBase<TGasPolicy>", "TransactionProcessorBase"], ["void"], ReceiverMode.Instance,
            [Arg("tx", ["Transaction"], "None", childKind: "ParameterReference", childName: "tx"),
             Arg("spec", ["IReleaseSpec"], "None", childKind: "ParameterReference", childName: "spec"),
             Arg("tracer", ["ITxTracer"], "None", childKind: "ParameterReference", childName: "tracer"),
             Arg("recipient", ["Address"], "None", childKind: "ParameterReference", childName: "recipient")],
            receiverKind: "InstanceReference", receiverTypeTokens: ["TransactionProcessorBase<>"]),
        "headerAndFees" => LowerInvocationOperation(
            id, binding, "UpdateHeaderGasUsedAndPayFees", "headerFees", ["TransactionProcessorBase<TGasPolicy>", "TransactionProcessorBase"], ["void"], ReceiverMode.Instance,
            [Arg("tx", ["Transaction"], "None", childKind: "ParameterReference", childName: "tx"),
             Arg("header", ["BlockHeader"], "None", childKind: "ParameterReference", childName: "header"),
             Arg("spec", ["IReleaseSpec"], "None", childKind: "ParameterReference", childName: "spec"),
             Arg("tracer", ["ITxTracer"], "None", childKind: "ParameterReference", childName: "tracer"),
             Arg("opts", ["ExecutionOptions"], "None", childKind: "ParameterReference", childName: "opts"),
             Arg("substate", ["TransactionSubstate"], "In", childKind: "LocalReference", childName: "substate"),
             Arg("spentGas", ["GasConsumed"], "In", childKind: "LocalReference", childName: "spentGas"),
             Arg("premiumPerGas", ["UInt256"], "In", childKind: "ParameterReference", childName: "premiumPerGas"),
             Arg("effectiveGasPrice", ["UInt256"], "In", childKind: "ParameterReference", childName: "opcodeGasPrice"),
             Arg("blobBaseFee", ["UInt256"], "In", childKind: "ParameterReference", childName: "blobBaseFee"),
             Arg("statusCode", ["int", "Int32"], "None", childKind: "LocalReference", childName: "statusCode")],
            receiverKind: "InstanceReference", receiverTypeTokens: ["TransactionProcessorBase<>"]),
        "finalizeRequest" => LowerInvocationOperation(
            id, binding, "FinalizeTransaction", "finalize", ["TransactionProcessorBase<TGasPolicy>", "TransactionProcessorBase"], ["TransactionResult"], ReceiverMode.Instance,
            [Arg("tx", ["Transaction"], "None", childKind: "ParameterReference", childName: "tx"),
             Arg("spec", ["IReleaseSpec"], "None", childKind: "ParameterReference", childName: "spec"),
             Arg("tracer", ["ITxTracer"], "None", childKind: "ParameterReference", childName: "tracer"),
             Arg("opts", ["ExecutionOptions"], "None", childKind: "ParameterReference", childName: "opts"),
             Arg("restore", ["bool", "Boolean"], "None", childKind: "ParameterReference", childName: "restore"),
             Arg("commit", ["bool", "Boolean"], "None", childKind: "ParameterReference", childName: "commit"),
             Arg("deleteCallerAccount", ["bool", "Boolean"], "None", childKind: "ParameterReference", childName: "deleteCallerAccount"),
             Arg("senderReservedGasPayment", ["UInt256"], "In", childKind: "ParameterReference", childName: "senderReservedGasPayment"),
             Arg("executingAccount", ["Address"], "None", childKind: "ParameterReference", childName: "recipient"),
             Arg("substate", ["TransactionSubstate"], "In", childKind: "LocalReference", childName: "substate"),
             Arg("spentGas", ["GasConsumed"], "None", childKind: "LocalReference", childName: "spentGas"),
             Arg("statusCode", ["int", "Int32"], "None", childKind: "LocalReference", childName: "statusCode")],
            receiverKind: "InstanceReference", receiverTypeTokens: ["TransactionProcessorBase<>"]),
        "receiptFailure" => LowerInvocationOperation(
            id, binding, "MarkAsFailed", "receiptFailure", ["ITxTracer"], ["void"], ReceiverMode.Reference,
            [Arg("recipient", ["Address"], "None", childKind: "ParameterReference", childName: "executingAccount"),
             Arg("gasSpent", ["GasConsumed"], "In", childKind: "ParameterReference", childName: "spentGas"),
             Arg("output", ["Byte[]"], "None", childKind: "LocalReference", childName: "output"),
             Arg("error", ["string", "String"], "None", childKind: "LocalReference", childName: "error"),
             Arg("stateRoot", ["Hash256"], "None", childKind: "LocalReference", childName: "stateRoot")],
            receiverKind: "ParameterReference", receiverName: "tracer", receiverTypeTokens: ["ITxTracer"]),
        "receiptSuccess" => LowerInvocationOperation(
            id, binding, "MarkAsSuccess", "receiptSuccess", ["ITxTracer"], ["void"], ReceiverMode.Reference,
            [Arg("recipient", ["Address"], "None", childKind: "ParameterReference", childName: "executingAccount"),
             Arg("gasSpent", ["GasConsumed"], "In", childKind: "ParameterReference", childName: "spentGas"),
             Arg("output", ["Byte[]"], "None", childKind: "Invocation", childName: "AsReadOnlyArray"),
             Arg("logs", ["LogEntry[]"], "None", childKind: "LocalReference", childName: "logs"),
             Arg("stateRoot", ["Hash256"], "None", childKind: "LocalReference", childName: "stateRoot")],
            receiverKind: "ParameterReference", receiverName: "tracer", receiverTypeTokens: ["ITxTracer"]),
        _ => throw new ExtractionException($"No closed typed operation lowering exists for {id}."),
    };

    private static OperationLowering LowerInvocationOperation(
        string id,
        SourceBinding binding,
        string name,
        string formula,
        string[] ownerTokens,
        string[] returnTypeTokens,
        ReceiverMode receiverMode,
        ExpectedArgument[] expected,
        string receiverKind = "",
        string receiverName = "",
        string[]? receiverTypeTokens = null,
        string sourceReceiverSyntax = "")
    {
        TypedAstNode node = UnwrapTyped(binding.TypedAst);
        if (node.Kind != "Invocation" || node.Name != name)
            throw new ExtractionException($"Typed source operation {id} is {node.Kind}/{node.Name}, expected invocation {name}.");
        if (sourceReceiverSyntax.Length != 0 &&
            !binding.CanonicalSyntax.StartsWith(sourceReceiverSyntax + "(", StringComparison.Ordinal))
            throw new ExtractionException($"Typed operation {id} changed its source receiver syntax: {binding.CanonicalSyntax}.");

        RequireTargetSignature(id, node, name, ownerTokens, returnTypeTokens);
        (TypedAstNode[] receiver, TypedAstNode[] arguments) = SplitInvocationChildren(node);
        RequireReceiver(id, receiver, receiverMode, receiverKind, receiverName, receiverTypeTokens ?? []);
        RequireArguments(id, arguments, expected);
        ValidateArgumentExpressions(id, arguments);
        string derivedFormula = DeriveBoundedFormula(node, formula);
        return new OperationLowering(
            derivedFormula,
            TypedGrammar(node, receiver, arguments),
            binding.TargetSymbolIdentity,
            DescribeReceiver(receiver),
            node.Type,
            arguments.Select(static argument => argument.ParameterName).ToArray(),
            arguments.Select(static argument => argument.Type).ToArray(),
            arguments.Select(static argument => argument.ArgumentKind + ":" + argument.RefKind).ToArray(),
            LowerExecutionTerm(node, receiver, arguments));
    }

    private static OperationLowering LowerObjectCreationOperation(
        string id,
        SourceBinding binding,
        string name,
        string formula,
        string[] ownerTokens,
        ExpectedArgument[] expected)
    {
        TypedAstNode node = UnwrapTyped(binding.TypedAst);
        if (node.Kind != "ObjectCreation" || node.Name != name)
            throw new ExtractionException($"Typed source operation {id} is {node.Kind}/{node.Name}, expected construction {name}.");
        RequireTargetSignature(id, node, name, ownerTokens, [name]);
        (TypedAstNode[] receiver, TypedAstNode[] arguments) = SplitInvocationChildren(node);
        if (receiver.Length != 0)
            throw new ExtractionException($"Object construction {id} unexpectedly has a receiver.");
        RequireArguments(id, arguments, expected);
        ValidateArgumentExpressions(id, arguments);
        string derivedFormula = DeriveBoundedFormula(node, formula);
        return new OperationLowering(
            derivedFormula,
            TypedGrammar(node, receiver, arguments),
            binding.TargetSymbolIdentity,
            "constructor",
            node.Type,
            arguments.Select(static argument => argument.ParameterName).ToArray(),
            arguments.Select(static argument => argument.Type).ToArray(),
            arguments.Select(static argument => argument.ArgumentKind + ":" + argument.RefKind).ToArray(),
            LowerExecutionTerm(node, receiver, arguments));
    }

    /// <summary>Compiles an already validated typed invocation into a closed executable-term key.</summary>
    /// <remarks>
    /// The key includes the resolved invocation kind, receiver grammar, and every ordered argument's
    /// typed child shape. It is consumed by <see cref="LeanEmitter"/> to select the executable Lean
    /// operation definition. A source edit that preserves a method name but changes a receiver,
    /// overload, argument, or returned expression therefore cannot retain the old term.
    /// </remarks>
    private static string LowerExecutionTerm(TypedAstNode node, TypedAstNode[] receiver, TypedAstNode[] arguments)
    {
        string operation = node.Name switch
        {
            "IncrementEmptyCalls" => "metric",
            "GetNewAccountStateCost" => "newAccountStateCost",
            "TryConsumeStateGas" => "stateCharge",
            "PayValue" => "payValue",
            "AddToBalanceAndCreateIfNotExists" => "recipientWrite",
            "TraceSimpleTransferActionStart" => "actionStart",
            "ClearExecutionGas" => "clearGas",
            "CreateTransfer" => "transferLog",
            "TransactionSubstate" => "substate",
            "Refund" => "settlement",
            "ReportSimpleTransferAccess" => "access",
            "UpdateHeaderGasUsedAndPayFees" => "headerFees",
            "FinalizeTransaction" => "finalize",
            "MarkAsFailed" => "receiptFailure",
            "MarkAsSuccess" => "receiptSuccess",
            _ => throw new ExtractionException($"No executable term exists for resolved source operation {node.Kind}/{node.Name}.")
        };

        string receiverShape = DescribeReceiver(receiver);
        string argumentShape = string.Join(",", arguments.Select(static argument =>
            argument.ParameterName + ":" + argument.Type + ":" + argument.ArgumentKind + ":" + argument.RefKind +
            ":" + (argument.Children.Length == 1 ? TypedShape(argument.Children[0]) : "invalid")));
        return operation + "|kind=" + node.Kind + "/" + node.Name +
            "|target=" + node.Symbol + "|type=" + node.Type +
            "|receiver=" + receiverShape + "|args=" + argumentShape;
    }

    private static string DeriveBoundedFormula(TypedAstNode node, string expected)
    {
        string actual = node.Name switch
        {
            "IncrementEmptyCalls" => "unconditionalMetric",
            "GetNewAccountStateCost" => "newAccountCost",
            "TryConsumeStateGas" => "stateChargeResult",
            "PayValue" => "payValue",
            "AddToBalanceAndCreateIfNotExists" => "recipientWrite",
            "TraceSimpleTransferActionStart" => "actionStart",
            "ClearExecutionGas" => "clearGas",
            "CreateTransfer" => "transferLog",
            "TransactionSubstate" => "substate",
            "Refund" => "settlement",
            "ReportSimpleTransferAccess" => "access",
            "UpdateHeaderGasUsedAndPayFees" => "headerFees",
            "FinalizeTransaction" => "finalize",
            "MarkAsFailed" => "receiptFailure",
            "MarkAsSuccess" => "receiptSuccess",
            _ => throw new ExtractionException($"No admitted bounded formula exists for {node.Kind}/{node.Name}.")
        };
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new ExtractionException($"Admitted bounded formula {actual} disagrees with operation contract {expected}.");
        return actual;
    }

    private static void ValidateArgumentExpressions(string id, TypedAstNode[] arguments)
    {
        switch (id)
        {
            case "consumeStateGas":
                RequireExactInvocationArgument(id, arguments[1], "GetNewAccountStateCost", receiverCount: 0, argumentCount: 0);
                break;
            case "addRecipientRequest":
                RequireBalanceChangeConditional(id, arguments[2]);
                break;
            case "receiptSuccess":
                RequireExactInvocationArgument(id, arguments[2], "AsReadOnlyArray", receiverCount: 0, argumentCount: 1);
                break;
            case "substateConstruction":
                RequireSubstateExceptionConditional(id, arguments[6]);
                break;
        }
    }

    private static void RequireExactInvocationArgument(string id, TypedAstNode argument, string name, int receiverCount, int argumentCount)
    {
        if (argument.Children.Length != 1) throw new ExtractionException($"Typed operation {id} has an invalid argument node.");
        TypedAstNode expression = UnwrapTyped(argument.Children[0]);
        if (expression.Kind != "Invocation" || expression.Name != name)
            throw new ExtractionException($"Typed operation {id} argument {argument.ParameterName} is not the exact {name} invocation.");
        (TypedAstNode[] receiver, TypedAstNode[] arguments) = SplitInvocationChildren(expression);
        if (receiver.Length != receiverCount || arguments.Length != argumentCount)
            throw new ExtractionException($"Typed operation {id} argument {argument.ParameterName} changed the receiver/argument grammar of {name}.");
    }

    private static void RequireBalanceChangeConditional(string id, TypedAstNode argument)
    {
        if (argument.Children.Length != 1) throw new ExtractionException($"Typed operation {id} has an invalid balance-change argument.");
        TypedAstNode expression = UnwrapTyped(argument.Children[0]);
        if (expression.Kind != "Conditional" || expression.Children.Length != 3 ||
            !IsExactReference(expression.Children[0], "hasValueTransfer") ||
            !HasReference(expression.Children[1], "value") ||
            !HasReference(expression.Children[2], "Zero"))
            throw new ExtractionException("Typed operation addRecipientRequest changed the value/zero conditional grammar.");
    }

    private static void RequireSubstateExceptionConditional(string id, TypedAstNode argument)
    {
        if (argument.Children.Length != 1) throw new ExtractionException($"Typed operation {id} has an invalid exception argument.");
        TypedAstNode expression = UnwrapTyped(argument.Children[0]);
        if (expression.Kind != "Conditional" || expression.Children.Length != 3 ||
            !IsExactReference(expression.Children[0], "newAccountOutOfGas") ||
            !HasReference(expression.Children[1], "OutOfGas") ||
            !HasReference(expression.Children[2], "None"))
            throw new ExtractionException("Typed operation substateConstruction changed the exception conditional grammar.");
    }

    private static void RequireTargetSignature(
        string id,
        TypedAstNode node,
        string name,
        string[] ownerTokens,
        string[] returnTypeTokens)
    {
        if (string.IsNullOrWhiteSpace(node.Symbol) || !string.Equals(SymbolMethodName(node.Symbol), name, StringComparison.Ordinal))
            throw new ExtractionException($"Typed operation {id} lost the resolved {name} target signature.");
        string containingType = SymbolContainingType(node.Symbol);
        if (ownerTokens.Length != 0 && !ownerTokens.Any(owner => TypeNamesEqual(containingType, owner)))
            throw new ExtractionException($"Typed operation {id} resolved {node.Symbol}, outside the admitted receiver/owner set.");
        if (returnTypeTokens.Length != 0 && !TypeMatches(node.Type, returnTypeTokens))
            throw new ExtractionException($"Typed operation {id} returned {node.Type}, outside the admitted result type set.");
    }

    private static string SymbolMethodName(string symbol)
    {
        int open = symbol.IndexOf('(');
        string head = (open < 0 ? symbol : symbol[..open]).Trim();
        int separator = head.LastIndexOf('.');
        return separator < 0 ? head : head[(separator + 1)..];
    }

    private static string SymbolContainingType(string symbol)
    {
        int open = symbol.IndexOf('(');
        string head = (open < 0 ? symbol : symbol[..open]).Trim();
        int methodSeparator = head.LastIndexOf('.');
        if (methodSeparator < 0) return string.Empty;
        head = head[..methodSeparator];
        int typeSeparator = head.LastIndexOf('.');
        return typeSeparator < 0 ? head : head[(typeSeparator + 1)..];
    }

    private static (TypedAstNode[] Receiver, TypedAstNode[] Arguments) SplitInvocationChildren(TypedAstNode node)
    {
        int firstArgument = Array.FindIndex(node.Children, static child => child.Kind == "Argument");
        if (firstArgument < 0) return (node.Children, []);
        if (node.Children.Skip(firstArgument).Any(static child => child.Kind != "Argument"))
            throw new ExtractionException($"Typed invocation {node.Name} interleaves receiver and argument children.");
        return (node.Children[..firstArgument], node.Children[firstArgument..]);
    }

    private static void RequireReceiver(
        string id,
        TypedAstNode[] receiver,
        ReceiverMode mode,
        string expectedKind,
        string expectedName,
        string[] expectedTypeTokens)
    {
        switch (mode)
        {
            case ReceiverMode.Static when receiver.Length != 0:
                throw new ExtractionException($"Static typed operation {id} unexpectedly has a receiver.");
            case ReceiverMode.Instance when receiver.Length != 1 || receiver[0].Kind != "InstanceReference":
                throw new ExtractionException($"Instance typed operation {id} does not retain its exact this receiver.");
            case ReceiverMode.Extension when receiver.Length != 1:
                throw new ExtractionException($"Extension typed operation {id} does not retain its reduced receiver: {DescribeReceiver(receiver)}.");
            case ReceiverMode.Reference when receiver.Length != 1:
                throw new ExtractionException($"Reference typed operation {id} does not retain its receiver.");
            case ReceiverMode.Static:
            case ReceiverMode.Instance:
            case ReceiverMode.Extension:
            case ReceiverMode.Reference:
                break;
            default:
                throw new ExtractionException($"Unknown receiver mode for typed operation {id}.");
        }

        if (receiver.Length == 1 &&
            (expectedKind.Length != 0 && receiver[0].Kind != expectedKind ||
             expectedName.Length != 0 && receiver[0].Name != expectedName ||
             expectedTypeTokens.Length != 0 && !TypeMatches(receiver[0].Type, expectedTypeTokens)))
            throw new ExtractionException($"Typed operation {id} changed its receiver grammar to {DescribeReceiver(receiver)}.");
    }

    private static void RequireArguments(string id, TypedAstNode[] arguments, ExpectedArgument[] expected)
    {
        int required = expected.Count(argument => !argument.Optional);
        if (arguments.Length < required || arguments.Length > expected.Length)
            throw new ExtractionException($"Typed operation {id} has {arguments.Length} arguments, expected {required}..{expected.Length}.");
        for (int index = 0; index < arguments.Length; index++)
        {
            TypedAstNode argument = arguments[index];
            ExpectedArgument specification = expected[index];
            if (argument.Kind != "Argument" || argument.ParameterName != specification.Name || argument.Name != specification.Name || argument.Children.Length != 1)
                throw new ExtractionException($"Typed operation {id} argument {index} is not the source parameter {specification.Name}.");
            if (!TypeMatches(argument.Type, specification.TypeTokens))
                throw new ExtractionException($"Typed operation {id} argument {specification.Name} has type {argument.Type}.");
            if (specification.RefKind.Length != 0 && !string.Equals(argument.RefKind, specification.RefKind, StringComparison.Ordinal))
                throw new ExtractionException($"Typed operation {id} argument {specification.Name} has ref-kind {argument.RefKind}, expected {specification.RefKind}.");
            TypedAstNode expression = UnwrapTyped(argument.Children[0]);
            if (specification.ChildKind.Length != 0 && expression.Kind != specification.ChildKind)
                throw new ExtractionException($"Typed operation {id} argument {specification.Name} has expression kind {expression.Kind}.");
            if (specification.ChildName.Length != 0 &&
                (expression.Kind == "Invocation" || expression.Kind == "PropertyReference" || expression.Kind == "FieldReference" ||
                 expression.Kind == "LocalReference" || expression.Kind == "ParameterReference") && expression.Name != specification.ChildName)
                throw new ExtractionException($"Typed operation {id} argument {specification.Name} has expression {expression.Name}.");
        }
        if (arguments.Length < expected.Length && expected[arguments.Length..].Any(static argument => !argument.Optional))
            throw new ExtractionException($"Typed operation {id} omitted a required trailing argument.");
    }

    private static bool TypeMatches(string? actual, string[] expected)
    {
        if (expected.Length == 0) return !string.IsNullOrWhiteSpace(actual);
        if (string.IsNullOrWhiteSpace(actual)) return false;
        return expected.Any(token => TypeNamesEqual(actual, token));
    }

    private static bool TypeNamesEqual(string actual, string expected)
    {
        static string Normalize(string value)
        {
            value = value.Trim().Replace("global::", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).TrimEnd('?');
            int genericStart = value.IndexOf('<');
            if (genericStart >= 0 && value.EndsWith('>'))
            {
                string outer = value[..genericStart];
                int outerNamespaceSeparator = outer.LastIndexOf('.');
                if (outerNamespaceSeparator >= 0) outer = outer[(outerNamespaceSeparator + 1)..];
                value = outer + "<>";
            }
            else
            {
                int namespaceSeparator = value.LastIndexOf('.');
                if (namespaceSeparator >= 0) value = value[(namespaceSeparator + 1)..];
            }
            return value switch
            {
                "bool" => "Boolean",
                "byte" => "Byte",
                "byte[]" => "Byte[]",
                "long" => "Int64",
                "ulong" => "UInt64",
                "int" => "Int32",
                "uint" => "UInt32",
                "short" => "Int16",
                "ushort" => "UInt16",
                "string" => "String",
                "object" => "Object",
                _ => value,
            };
        }

        string left = Normalize(actual);
        string right = Normalize(expected);
        return string.Equals(left, right, StringComparison.Ordinal);
    }

    private static string DescribeReceiver(TypedAstNode[] receiver) => receiver.Length switch
    {
        0 => "static",
        1 => receiver[0].Kind + ":" + receiver[0].Type + ":" + receiver[0].Name,
        _ => string.Join(",", receiver.Select(static child => child.Kind + ":" + child.Type + ":" + child.Name)),
    };

    private static string TypedGrammar(TypedAstNode node, TypedAstNode[] receiver, TypedAstNode[] arguments) =>
        node.Kind + "/" + node.Name +
            "|target=" + node.Symbol +
            "|targetIdentity=" + SymbolIdentityFromTypedNode(node) +
            "|type=" + node.Type + "|typeIdentity=" + node.TypeIdentity +
        "|receiver=" + DescribeReceiver(receiver) +
        "|args=" + string.Join(",", arguments.Select(static argument =>
            argument.ParameterName + ":" + argument.Type + ":" + argument.ArgumentKind + ":" + argument.RefKind +
            ":" + (argument.Children.Length == 1 ? TypedShape(argument.Children[0]) : "invalid")));

    private static string SymbolIdentityFromTypedNode(TypedAstNode node) =>
        node.Symbol.Length == 0 ? "unresolved" : "symbol=" + node.Symbol;

    private static string TypedShape(TypedAstNode node) =>
        node.Kind + "/" + node.Name + "/" + node.Symbol + "/" + node.Type + "/" + node.TypeIdentity + "/" + node.Constant +
        "/" + node.ParameterName + "/" + node.ArgumentKind + "/" + node.RefKind +
        "[" + string.Join(",", node.Children.Select(TypedShape)) + "]";

    private static ExpectedArgument Arg(
        string name,
        string[] typeTokens,
        string refKind,
        bool Optional = false,
        string childKind = "",
        string childName = "") =>
        new(name, typeTokens, refKind, Optional, childKind, childName);

    private enum ReceiverMode
    {
        Static,
        Instance,
        Extension,
        Reference,
    }

    private sealed record ExpectedArgument(
        string Name,
        string[] TypeTokens,
        string RefKind,
        bool Optional = false,
        string ChildKind = "",
        string ChildName = "");

    private static bool HasInvocation(TypedAstNode node, string name) =>
        node.Kind == "Invocation" && node.Name == name || node.Children.Any(child => HasInvocation(child, name));

    private static void RequireTypedInvocationTarget(TypedAstNode root, string name, string targetSymbol, string role)
    {
        TypedAstNode[] invocations = WalkTypedAst(root)
            .Where(node => node.Kind == "Invocation" && node.Name == name)
            .ToArray();
        if (invocations.Length != 1 || !string.Equals(invocations[0].Symbol, targetSymbol, StringComparison.Ordinal))
            throw new ExtractionException($"The {role} does not retain the exact resolved {name} target.");
    }

    private static IEnumerable<TypedAstNode> WalkTypedAst(TypedAstNode node)
    {
        yield return node;
        foreach (TypedAstNode child in node.Children)
            foreach (TypedAstNode descendant in WalkTypedAst(child)) yield return descendant;
    }

    private static bool HasReference(TypedAstNode node, string name) =>
        node.Kind is "LocalReference" or "ParameterReference" or "PropertyReference" or "FieldReference" && node.Name == name ||
        node.Children.Any(child => HasReference(child, name));

    private static bool IsExactReference(TypedAstNode node, string name) =>
        (node.Kind is "LocalReference" or "ParameterReference") && node.Name == name;

    private static bool IsExactBuildUpCondition(TypedAstNode node)
    {
        node = UnwrapTyped(node);
        return node.Kind == "Binary" &&
            (node.Name == "Equals" || node.Name == "Equal") &&
            node.Children.Length == 2 &&
            ((IsReferenceOnly(node.Children[0], "opts") && IsReferenceOnly(node.Children[1], "BuildUp")) ||
             (IsReferenceOnly(node.Children[0], "BuildUp") && IsReferenceOnly(node.Children[1], "opts")));
    }

    private static bool HasNegatedReference(TypedAstNode node, string name) =>
        node.Kind == "Unary" && node.Name == "Not" && node.Children.Length == 1 && HasReference(node.Children[0], name) ||
        node.Children.Any(child => HasNegatedReference(child, name));

    private static bool IsNegatedReference(TypedAstNode node, string name) =>
        node.Kind == "Unary" && node.Name == "Not" && node.Children.Length == 1 && IsReferenceTree(node.Children[0], name);

    private static bool IsReferenceTree(TypedAstNode node, string name) =>
        (node.Kind is "LocalReference" or "ParameterReference" or "PropertyReference" or "FieldReference") && node.Name == name ||
        node.Children.Count(child => child.Kind is "InstanceReference" or "PropertyReference" or "FieldReference") > 0 &&
        HasReference(node, name);

    private static string InvocationName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => string.Empty,
    };

    private static string TypeName(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        QualifiedNameSyntax qualified => TypeName(qualified.Right),
        AliasQualifiedNameSyntax alias => TypeName(alias.Name),
        NullableTypeSyntax nullable => TypeName(nullable.ElementType),
        ArrayTypeSyntax array => TypeName(array.ElementType),
        _ => string.Empty,
    };

    private static string ContainingName(SyntaxNode node) =>
        node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? node.Kind().ToString();

    private static SyntaxNode Body(MethodDeclarationSyntax method) =>
        method.Body ?? (SyntaxNode?)method.ExpressionBody?.Expression ?? throw new ExtractionException($"{method.Identifier.ValueText} has no body.");

    private static string SymbolIdentity(ISymbol? symbol)
    {
        if (symbol is null) return "unresolved";
        // Generic static-interface calls are bound to a constructed method at the call site. The
        // original definition is the exact overload identity shared by the declaration contract
        // and the rebound invocation; retaining the constructed display string separately in
        // TargetSymbol keeps the concrete receiver visible in the audit.
        if (symbol is IMethodSymbol method)
            symbol = method.OriginalDefinition;
        // Source declarations carry the synthetic compilation version 0.0.0.0 while the
        // admitted metadata carries the pinned production version. The closure identity pins
        // that metadata separately; member equality must compare the stable assembly name and
        // the complete resolved symbol signature, not an incidental compilation version.
        string assembly = symbol.ContainingAssembly?.Identity.Name ?? "source";
        return "kind=" + symbol.Kind + "|assembly=" + assembly + "|symbol=" +
            symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    private static SourceBinding Bind(
        SourceFile source,
        string owner,
        string member,
        string role,
        SyntaxNode node,
        SemanticContext context,
        bool requireSymbol)
    {
        if (!context.TryModel(source.Tree, out SemanticModel? found))
            return BindExternalProjection(source, owner, member, role, node, context.Compilation);
        SemanticModel model = found ?? throw new ExtractionException("Missing semantic model for " + source.RelativePath);
        IOperation? operation = null;
        if (node is not VariableDeclaratorSyntax)
        {
            try { operation = model.GetOperation(node); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        if (operation is null && node is PropertyDeclarationSyntax property && property.ExpressionBody is not null)
        {
            operation = model.GetOperation(property.ExpressionBody.Expression);
        }
        if (operation is null && node is MethodDeclarationSyntax method)
        {
            operation = method.Body is not null
                ? model.GetOperation(method.Body)
                : method.ExpressionBody is not null
                    ? model.GetOperation(method.ExpressionBody.Expression)
                    : null;
        }
        if (operation is null && node is ConstructorDeclarationSyntax constructor && constructor.Body is not null)
            operation = model.GetOperation(constructor.Body);
        if (operation is null &&
            (node is MethodDeclarationSyntax { Body: not null } ||
             node is MethodDeclarationSyntax { ExpressionBody: not null } ||
             node is ConstructorDeclarationSyntax { Body: not null }))
            throw new ExtractionException($"Roslyn could not lower the body of {role} {member} in {source.RelativePath}.");
        SymbolInfo symbolInfo = model.GetSymbolInfo(node);
        if (symbolInfo.CandidateReason != CandidateReason.None || symbolInfo.CandidateSymbols.Length != 0)
            throw new ExtractionException($"Roslyn produced candidate or ambiguous symbols for {role} {member} in {source.RelativePath}.");

        ISymbol? symbol = operation switch
        {
            IInvocationOperation invocationOperation => invocationOperation.TargetMethod,
            IPropertyReferenceOperation propertyOperation => propertyOperation.Property,
            IFieldReferenceOperation fieldOperation => fieldOperation.Field,
            IMethodReferenceOperation methodReferenceOperation => methodReferenceOperation.Method,
            IObjectCreationOperation creationOperation => creationOperation.Constructor,
            _ => symbolInfo.Symbol,
        };
        if (node is not (MethodDeclarationSyntax or ConstructorDeclarationSyntax or PropertyDeclarationSyntax or VariableDeclaratorSyntax or TypeDeclarationSyntax))
            symbol ??= FirstResolvedOperationSymbol(operation);
        symbol ??= node switch
        {
            MethodDeclarationSyntax declaredMethod => model.GetDeclaredSymbol(declaredMethod),
            ConstructorDeclarationSyntax declaredConstructor => model.GetDeclaredSymbol(declaredConstructor),
            PropertyDeclarationSyntax declaredProperty => model.GetDeclaredSymbol(declaredProperty),
            VariableDeclaratorSyntax declaredVariable => model.GetDeclaredSymbol(declaredVariable),
            TypeDeclarationSyntax declaredType => model.GetDeclaredSymbol(declaredType),
            _ => null,
        };

        bool resolved = symbol is not null && !IsErrorSymbol(symbol);
        if (requireSymbol && !resolved)
            throw new ExtractionException($"Roslyn could not resolve {role} {member} in {source.RelativePath}.");

        if (node is MethodDeclarationSyntax methodDeclaration &&
            (methodDeclaration.Body is not null || methodDeclaration.ExpressionBody is not null))
            context.RequireCfg(methodDeclaration, role);

        FileLinePositionSpan span = node.GetLocation().GetLineSpan();
        string canonical = Canonical(node);
        FlowInfo flow = context.Flow(node);
        string receiver = operation switch
        {
            IInvocationOperation receiverInvocation when receiverInvocation.Instance?.Type is ITypeSymbol receiverType =>
                InvocationReceiver(node as InvocationExpressionSyntax) + " :: " + receiverType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            IPropertyReferenceOperation receiverProperty when receiverProperty.Instance?.Type is ITypeSymbol propertyReceiverType => propertyReceiverType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            _ => node is InvocationExpressionSyntax invocationSyntax ? InvocationReceiver(invocationSyntax) : string.Empty,
        };
        return new SourceBinding(
            source.RelativePath,
            owner,
            member,
            role,
            node.Kind().ToString(),
            operation?.Kind.ToString() ?? "Unresolved",
            canonical,
            TokenFingerprint(node),
            Sha256(Encoding.UTF8.GetBytes(canonical)),
            ContainingMember(node),
            receiver,
            symbol?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? "unresolved",
            SymbolIdentity(symbol),
            resolved,
            StatementOrdinal(node),
            flow.Path,
            flow.Block,
            flow.Reachable,
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1,
            span.EndLinePosition.Character + 1,
            operation is null ? LowerDeclaredAst(node, model) : LowerTypedAst(operation));
    }

    private static SourceBinding BindExternalProjection(
        SourceFile source, string owner, string member, string role, SyntaxNode node, CSharpCompilation compilation)
    {
        string mode = ExternalBindingMode(source.RelativePath, member);
        string canonical = Canonical(node);
        IFieldSymbol? constant = mode == "CoreMetadataConstant"
            ? RequireCoreConstant(compilation, "Nethermind.Core.GasCostOf", "NewAccountState", node)
            : null;
        string identity = constant is null ? ExternalSourceIdentity(source, node, mode) : SymbolIdentity(constant);
        FileLinePositionSpan span = node.GetLocation().GetLineSpan();
        return new SourceBinding(
            source.RelativePath, owner, member,
            mode == "ExternalHookPremise" ? "normal-return external hook premise: " + role : role,
            node.Kind().ToString(), mode,
            canonical, TokenFingerprint(node), Sha256(Encoding.UTF8.GetBytes(canonical)),
            ContainingMember(node), string.Empty, constant?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? identity, identity, constant is not null,
            StatementOrdinal(node), ControlFlowPath(node), -1, false,
            span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1, span.EndLinePosition.Character + 1,
            constant is null
                ? new TypedAstNode(mode, member, identity, string.Empty, [])
                : new TypedAstNode("FieldDeclaration", member, identity,
                    Convert.ToString(constant.ConstantValue, CultureInfo.InvariantCulture) ?? string.Empty, [],
                    constant.Type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                    TypeIdentity: SymbolIdentity(constant.Type)));
    }

    private static IFieldSymbol RequireCoreConstant(CSharpCompilation compilation, string typeName, string member, SyntaxNode declaration)
    {
        if (declaration is not VariableDeclaratorSyntax variable || variable.Identifier.ValueText != member ||
            variable.Parent?.Parent is not FieldDeclarationSyntax { Modifiers: var modifiers } ||
            !modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.ConstKeyword)) ||
            variable.Initializer is null)
            throw new ExtractionException($"Core metadata constant {typeName}.{member} lost its exact source declaration.");
        IFieldSymbol? field = compilation.GetTypeByMetadataName(typeName)?.GetMembers(member).OfType<IFieldSymbol>().SingleOrDefault();
        if (field is not { HasConstantValue: true } || IsErrorSymbol(field))
            throw new ExtractionException($"Core metadata constant {typeName}.{member} is missing from the accepted reference closure.");
        if (member == "NewAccountState")
        {
            CompilationUnitSyntax syntax = variable.SyntaxTree.GetCompilationUnitRoot();
            VariableDeclaratorSyntax Factor(string name) => syntax.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .SingleOrDefault(candidate => candidate.Identifier.ValueText == name)
                ?? throw new ExtractionException($"GasCostOf.NewAccountState lost source factor {name}.");
            VariableDeclaratorSyntax stateBytes = Factor("StateBytesPerNewAccount");
            VariableDeclaratorSyntax costPerByte = Factor("CostPerStateByte");
            if (Canonical(variable.Initializer.Value) != "StateBytesPerNewAccount*CostPerStateByte" ||
                stateBytes.Initializer?.Value is not LiteralExpressionSyntax stateBytesValue ||
                costPerByte.Initializer?.Value is not LiteralExpressionSyntax costPerByteValue ||
                !long.TryParse(stateBytesValue.Token.ValueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long sourceStateBytes) ||
                !long.TryParse(costPerByteValue.Token.ValueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long sourceCostPerByte) ||
                compilation.GetTypeByMetadataName(typeName)?.GetMembers("StateBytesPerNewAccount").OfType<IFieldSymbol>().SingleOrDefault()?.ConstantValue is not long metadataStateBytes ||
                compilation.GetTypeByMetadataName(typeName)?.GetMembers("CostPerStateByte").OfType<IFieldSymbol>().SingleOrDefault()?.ConstantValue is not long metadataCostPerByte ||
                sourceStateBytes != metadataStateBytes || sourceCostPerByte != metadataCostPerByte ||
                field.ConstantValue is not long cost || cost != checked(sourceStateBytes * sourceCostPerByte))
                throw new ExtractionException("GasCostOf.NewAccountState source formula and accepted Core metadata disagree.");
        }
        return field;
    }

    private static string ExternalSourceIdentity(SourceFile source, SyntaxNode declaration, string mode) =>
        mode + "|" + source.RelativePath + "|" +
        Sha256(Encoding.UTF8.GetBytes(Canonical(declaration)));

    private static string ExternalBindingMode(string path, string member) => (path, member) switch
    {
        (GasCostOfPath, "NewAccountState") => "CoreMetadataConstant",
        (MainnetDiPath, "Load") => "ExactExternalSourceProjection",
        (MainnetDiPath, "AddScoped<ITransactionProcessor.IBlobBaseFeeCalculator,BlobBaseFeeCalculator>") => "ExactExternalSourceProjection",
        (MainnetDiPath, "AddScoped<ITransactionProcessor,EthereumTransactionProcessor>") => "ExactExternalSourceProjection",
        (MainnetDiPath, "AddScoped<ICodeInfoRepository,CacheCodeInfoRepository>") => "ExactExternalSourceProjection",
        (MainnetDiPath, "AddScoped<IWorldState,WorldState>") => "ExactExternalSourceProjection",
        (WorldStatePath, "Reset" or "AddToBalanceAndCreateIfNotExists" or "SubtractFromBalance" or "Commit" or
            "RecalculateStateRoot" or "StateRoot" or "IsDeadAccount" or "ResetTransient" or "ReapEmptyAccounts") => "ExternalHookPremise",
        (StateProviderPath, "Commit" or "RecalculateStateRoot" or "StateRoot" or "IsDeadAccount" or
            "AddToBalanceAndCreateIfNotExists" or "SubtractFromBalance") => "ExternalHookPremise",
        _ => throw new ExtractionException($"Required compiled EVM source or unadmitted external projection is missing: {path}/{member}."),
    };

    private static ISymbol? FirstResolvedOperationSymbol(IOperation? operation)
    {
        if (operation is null) return null;
        ISymbol? current = operation switch
        {
            IInvocationOperation invocation => invocation.TargetMethod,
            IPropertyReferenceOperation property => property.Property,
            IFieldReferenceOperation field => field.Field,
            IMethodReferenceOperation methodReference => methodReference.Method,
            IObjectCreationOperation creation => creation.Constructor,
            ILocalReferenceOperation local => local.Local,
            IParameterReferenceOperation parameter => parameter.Parameter,
            IInstanceReferenceOperation instance => instance.Type,
            _ => null,
        };
        if (current is not null && !IsErrorSymbol(current)) return current;
        foreach (IOperation child in operation.ChildOperations)
        {
            ISymbol? descendant = FirstResolvedOperationSymbol(child);
            if (descendant is not null) return descendant;
        }
        return null;
    }

    private static TypedAstNode LowerDeclaredAst(SyntaxNode node, SemanticModel model) => node switch
    {
        TypeDeclarationSyntax type => TypedNode("TypeDeclaration", type.Identifier.ValueText, model.GetDeclaredSymbol(type), []),
        ConstructorDeclarationSyntax constructor => TypedNode("ConstructorDeclaration", constructor.Identifier.ValueText, model.GetDeclaredSymbol(constructor), []),
        MethodDeclarationSyntax method => TypedNode("MethodDeclaration", method.Identifier.ValueText, model.GetDeclaredSymbol(method), []),
        PropertyDeclarationSyntax property => TypedNode("PropertyDeclaration", property.Identifier.ValueText, model.GetDeclaredSymbol(property), []),
        VariableDeclaratorSyntax variable => TypedNode("FieldDeclaration", variable.Identifier.ValueText, model.GetDeclaredSymbol(variable), [], constant: FieldConstant(model, variable), type: (model.GetDeclaredSymbol(variable) as IFieldSymbol)?.Type),
        _ => new TypedAstNode("Unresolved", string.Empty, string.Empty, string.Empty, []),
    };

    private static TypedAstNode LowerTypedAst(IOperation? operation) => operation switch
        {
        IInvocationOperation invocation => TypedNode(
            "Invocation",
            invocation.TargetMethod.Name,
            invocation.TargetMethod,
            (invocation.Instance is null ? Array.Empty<TypedAstNode>() : [LowerTypedAst(invocation.Instance)])
                .Concat(invocation.Arguments.Select(LowerArgument))
                .ToArray(),
            type: invocation.Type),
        IPropertyReferenceOperation property => TypedNode(
            "PropertyReference",
            property.Property.Name,
            property.Property,
            property.Instance is null ? [] : [LowerTypedAst(property.Instance)],
            type: property.Type),
        IFieldReferenceOperation field => TypedNode(
            "FieldReference",
            field.Field.Name,
            field.Field,
            field.Instance is null ? [] : [LowerTypedAst(field.Instance)],
            type: field.Type),
        IMethodReferenceOperation method => TypedNode(
            "MethodReference",
            method.Method.Name,
            method.Method,
            method.Instance is null ? [] : [LowerTypedAst(method.Instance)],
            type: method.Type),
        IObjectCreationOperation creation => TypedNode(
            "ObjectCreation",
            creation.Constructor?.ContainingType.Name ?? string.Empty,
            creation.Constructor,
            creation.Arguments.Select(LowerArgument).ToArray(),
            type: creation.Type),
        IBinaryOperation binary => TypedNode(
            "Binary",
            binary.OperatorKind.ToString(),
            null,
            [LowerTypedAst(binary.LeftOperand), LowerTypedAst(binary.RightOperand)],
            type: binary.Type),
        IUnaryOperation unary => TypedNode(
            "Unary",
            unary.OperatorKind.ToString(),
            null,
            [LowerTypedAst(unary.Operand)],
            type: unary.Type),
        IConditionalOperation conditional => TypedNode(
            "Conditional",
            string.Empty,
            null,
            [LowerTypedAst(conditional.Condition), LowerTypedAst(conditional.WhenTrue), LowerTypedAst(conditional.WhenFalse)],
            type: conditional.Type),
        IConversionOperation conversion => TypedNode(
            "Conversion",
            conversion.Type?.Name ?? string.Empty,
            conversion.Type,
            [LowerTypedAst(conversion.Operand)],
            type: conversion.Type),
        IParenthesizedOperation parenthesized => TypedNode(
            "Parenthesized",
            string.Empty,
            null,
            [LowerTypedAst(parenthesized.Operand)],
            type: parenthesized.Type),
        ILocalReferenceOperation local => TypedNode("LocalReference", local.Local.Name, local.Local, [], type: local.Type),
        IParameterReferenceOperation parameter => TypedNode("ParameterReference", parameter.Parameter.Name, parameter.Parameter, [], type: parameter.Type),
        IInstanceReferenceOperation instance => TypedNode("InstanceReference", string.Empty, instance.Type, [], type: instance.Type),
        ILiteralOperation literal => TypedNode("Literal", string.Empty, literal.Type, [], FormatConstant(literal.ConstantValue), type: literal.Type),
        ICompoundAssignmentOperation compound => TypedNode(
            "CompoundAssignment",
            compound.OperatorKind.ToString(),
            null,
            [LowerTypedAst(compound.Target), LowerTypedAst(compound.Value)],
            type: compound.Type),
        IAssignmentOperation assignment => TypedNode(
            "Assignment",
            string.Empty,
            null,
            [LowerTypedAst(assignment.Target), LowerTypedAst(assignment.Value)],
            type: assignment.Type),
        _ when operation is not null => TypedNode(
            operation.Kind.ToString(),
            string.Empty,
            operation.Type,
            operation.ChildOperations.Select(LowerTypedAst).ToArray(),
            FormatConstant(operation.ConstantValue),
            type: operation.Type),
        _ => new TypedAstNode("Absent", string.Empty, string.Empty, string.Empty, []),
    };

    private static TypedAstNode LowerArgument(IArgumentOperation argument) =>
        TypedNode(
            "Argument",
            argument.Parameter?.Name ?? string.Empty,
            argument.Parameter?.Type,
            [LowerTypedAst(argument.Value)],
            parameterName: argument.Parameter?.Name ?? string.Empty,
            argumentKind: argument.ArgumentKind.ToString(),
            refKind: argument.Parameter?.RefKind.ToString() ?? string.Empty,
            type: argument.Parameter?.Type);

    private static TypedAstNode TypedNode(
        string kind,
        string name,
        ISymbol? symbol,
        TypedAstNode[] children,
        string constant = "",
        ITypeSymbol? type = null,
        string parameterName = "",
        string argumentKind = "",
        string refKind = "")
    {
        if (IsErrorSymbol(symbol))
            throw new ExtractionException($"Roslyn lowered an error symbol in typed operation {kind}/{name}.");
        if (ContainsErrorType(type))
            throw new ExtractionException($"Roslyn lowered an error type in typed operation {kind}/{name}.");
        return new(
            kind,
            name,
            symbol?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? string.Empty,
            constant,
            children,
            type?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? string.Empty,
            parameterName,
            argumentKind,
            refKind,
            TypeIdentity(type));
    }

    private static string TypeIdentity(ITypeSymbol? type) => type is null ? string.Empty : SymbolIdentity(type);

    private static bool IsErrorSymbol(ISymbol? symbol) => symbol switch
    {
        null => false,
        IErrorTypeSymbol => true,
        ITypeSymbol type => ContainsErrorType(type),
        IPropertySymbol property => ContainsErrorType(property.Type),
        IFieldSymbol field => ContainsErrorType(field.Type),
        IMethodSymbol method => ContainsErrorType(method.ContainingType) || ContainsErrorType(method.ReturnType) || method.Parameters.Any(parameter => ContainsErrorType(parameter.Type)),
        IParameterSymbol parameter => ContainsErrorType(parameter.Type),
        ILocalSymbol local => ContainsErrorType(local.Type),
        _ => symbol.ContainingType is ITypeSymbol containing && ContainsErrorType(containing),
    };

    private static bool ContainsErrorType(ITypeSymbol? type) => type switch
    {
        null => false,
        IErrorTypeSymbol => true,
        IArrayTypeSymbol array => ContainsErrorType(array.ElementType),
        IPointerTypeSymbol pointer => ContainsErrorType(pointer.PointedAtType),
        INamedTypeSymbol named => named.TypeArguments.Any(ContainsErrorType),
        _ => false,
    };

    private static string FormatConstant(Optional<object?> value) =>
        !value.HasValue ? string.Empty :
        value.Value is null ? "<null>" :
        Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static string FieldConstant(SemanticModel model, VariableDeclaratorSyntax variable) =>
        model.GetDeclaredSymbol(variable) is IFieldSymbol field ? FormatConstant(field.ConstantValue) : string.Empty;

    private static string InvocationReceiver(InvocationExpressionSyntax? invocation) => invocation?.Expression switch
    {
        MemberAccessExpressionSyntax member => Canonical(member.Expression),
        _ => string.Empty,
    };

    private static string ContainingMember(SyntaxNode node)
    {
        SyntaxNode? member = node.AncestorsAndSelf().FirstOrDefault(candidate => candidate is MethodDeclarationSyntax or ConstructorDeclarationSyntax or TypeDeclarationSyntax);
        return member switch
        {
            MethodDeclarationSyntax method => method.Identifier.ValueText + Canonical(method.ParameterList),
            ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText + Canonical(constructor.ParameterList),
            TypeDeclarationSyntax type => type.Identifier.ValueText,
            _ => string.Empty,
        };
    }

    private static int StatementOrdinal(SyntaxNode node)
    {
        StatementSyntax? statement = node.AncestorsAndSelf().OfType<StatementSyntax>().LastOrDefault();
        if (statement?.Parent is not BlockSyntax block) return 0;
        for (int index = 0; index < block.Statements.Count; index++)
            if (block.Statements[index] == statement || block.Statements[index].Span.Contains(node.Span)) return index + 1;
        return 0;
    }

    private static string ControlFlowPath(SyntaxNode node)
    {
        List<string> path = [];
        foreach (SyntaxNode ancestor in node.Ancestors().Reverse())
        {
            switch (ancestor)
            {
                case IfStatementSyntax: path.Add("if"); break;
                case ConditionalExpressionSyntax: path.Add("conditional"); break;
                case ReturnStatementSyntax: path.Add("return"); break;
                case BlockSyntax: path.Add("block"); break;
            }
        }
        return string.Join('/', path);
    }

    private static string TokenFingerprint(SyntaxNode node)
    {
        StringBuilder builder = new();
        foreach (SyntaxToken token in node.DescendantTokens(descendIntoTrivia: false))
            builder.Append(token.RawKind).Append(':').Append(token.Text.Length).Append(':').Append(token.Text).Append('\n');
        return Sha256(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static string Canonical(SyntaxNode node) => string.Concat(node.DescendantTokens(descendIntoTrivia: false).Select(static token => token.Text));

    private static string SourceClosureHash(
        IEnumerable<SourceIdentity> sources,
        IEnumerable<SourceIdentity> compilerSources,
        IEnumerable<ReferenceIdentity> compilerReferences)
    {
        StringBuilder builder = new();
        foreach (SourceIdentity source in sources)
            builder.Append("projection|").Append(source.Path).Append('|').Append(source.Role).Append('|').Append(source.Sha256).Append('\n');
        foreach (SourceIdentity source in compilerSources)
            builder.Append("compiler-source|").Append(source.Path).Append('|').Append(source.Role).Append('|').Append(source.Sha256).Append('\n');
        foreach (ReferenceIdentity reference in compilerReferences)
            builder.Append("compiler-reference|").Append(reference.Path).Append('|').Append(reference.AssemblyName)
                .Append('|').Append(reference.Sha256).Append('|').Append(reference.Mvid).Append('|').Append(reference.Selected).Append('\n');
        return Sha256(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static string CombinedSourceHash(IEnumerable<SourceIdentity> sources, IEnumerable<DependencyIdentity> dependencies)
    {
        StringBuilder builder = new();
        foreach (SourceIdentity source in sources) builder.Append("source|").Append(source.Path).Append('|').Append(source.Sha256).Append('\n');
        foreach (DependencyIdentity dependency in dependencies) builder.Append("dependency|").Append(dependency.Path).Append('|').Append(dependency.Sha256).Append('\n');
        return Sha256(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static bool IsSha256(string? value) => value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static byte[] Serialize<T>(T value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        string normalized = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n";
        return new UTF8Encoding(false).GetBytes(normalized);
    }

    private static IrDocument DeserializeIr(byte[] bytes)
    {
        try
        {
            RejectDuplicateProperties(bytes);
            RequireCompleteTypedAstNodes(bytes);
            IrDocument document = JsonSerializer.Deserialize<IrDocument>(bytes, JsonOptions)
                ?? throw new ExtractionException("Simple-transfer IR is empty.");
            ValidateStageASourcePlan(document.StageAPlan);
            if (document.SourceClosureSha256 != SourceClosureHash(document.Sources, document.CompilerSources, document.CompilerReferences))
                throw new ExtractionException("Simple-transfer IR source closure hash does not match effective compiler inputs.");
            foreach (SourceBinding binding in document.Completion.MethodBindings
                         .Concat(document.Completion.RouteBindings)
                         .Concat(document.Completion.Stages.Select(static stage => stage.Binding))
                         .Concat(document.Completion.Branches.Select(static branch => branch.Binding))
                         .Concat(document.Completion.Effects.Select(static effect => effect.Binding))
                         .Concat(document.Completion.Operations.Select(static operation => operation.Binding))
                         .Concat(document.Completion.Adapters.SelectMany(static adapter => adapter.Bindings)))
                ValidateBinding(binding);
            return document;
        }
        catch (JsonException exception) { throw new ExtractionException("Simple-transfer IR is not valid JSON: " + exception.Message); }
    }

    private static void RequireCompleteTypedAstNodes(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 1024 });
        Visit(document.RootElement, inStageAPlan: false);

        static void Visit(JsonElement element, bool inStageAPlan)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (property.NameEquals("typedAst") || inStageAPlan && property.NameEquals("operation"))
                        RequireNode(property.Value);
                    else Visit(property.Value, inStageAPlan || property.NameEquals("stageAPlan"));
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray()) Visit(item, inStageAPlan);
            }
        }

        static void RequireNode(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object)
                throw new ExtractionException("Simple-transfer typed AST node must be an object.");
            foreach (string name in new[] { "kind", "name", "symbol", "constant", "type", "parameterName", "argumentKind", "refKind", "typeIdentity" })
            {
                if (!node.TryGetProperty(name, out JsonElement property) || property.ValueKind != JsonValueKind.String)
                    throw new ExtractionException("Simple-transfer typed AST node has a missing or wrongly typed property: " + name);
            }
            if (!node.TryGetProperty("children", out JsonElement children) || children.ValueKind != JsonValueKind.Array)
                throw new ExtractionException("Simple-transfer typed AST node has missing or wrongly typed children.");
            foreach (JsonElement child in children.EnumerateArray()) RequireNode(child);
        }
    }

    private static Manifest DeserializeManifest(byte[] bytes)
    {
        try
        {
            RejectDuplicateProperties(bytes);
            RequireCompleteTypedAstNodes(bytes);
            Manifest document = JsonSerializer.Deserialize<Manifest>(bytes, JsonOptions)
                ?? throw new ExtractionException("Simple-transfer manifest is empty.");
            if (!IsSha256(document.StageAPlanSha256))
                throw new ExtractionException("Simple-transfer manifest lost its Stage-A typed source plan identity.");
            if (document.SourceClosureSha256 != SourceClosureHash(document.Sources, document.CompilerSources, document.CompilerReferences))
                throw new ExtractionException("Simple-transfer manifest source closure hash does not match effective compiler inputs.");
            foreach (SourceBinding binding in document.Bindings) ValidateBinding(binding);
            return document;
        }
        catch (JsonException exception) { throw new ExtractionException("Simple-transfer manifest is not valid JSON: " + exception.Message); }
    }

    private static void RejectDuplicateProperties(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 1024 });
        Visit(document.RootElement);

        static void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new ExtractionException("Duplicate simple-transfer JSON property: " + property.Name);
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray()) Visit(item);
            }
        }
    }

    private static ArtifactPaths Paths(string root, string outputDirectory, string? leanOutputPath)
    {
        string output = Path.GetFullPath(outputDirectory);
        EnsureWithinRepositoryOrTemp(root, output);
        string ir = Path.Combine(output, IrFileName);
        string manifest = Path.Combine(output, ManifestFileName);
        string lean = leanOutputPath is null ? Path.Combine(root, DefaultLeanRelativePath) : Path.GetFullPath(leanOutputPath);
        EnsureWithinRepositoryOrTemp(root, ir);
        EnsureWithinRepositoryOrTemp(root, manifest);
        EnsureWithinRepositoryOrTemp(root, lean);
        return new ArtifactPaths(ir, manifest, lean);
    }

    private static void CompareBytes(string path, byte[] expected, string kind)
    {
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(expected))
            throw new ExtractionException($"Generated simple-transfer {kind} drifted: {path}.");
    }

    private static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void PublishArtifacts(ArtifactPaths paths, byte[] ir, byte[] lean, byte[] manifest, Action? afterIr = null)
    {
        Write(paths.IrPath, ir);
        afterIr?.Invoke();
        Write(paths.LeanPath, lean);
        Write(paths.ManifestPath, manifest);
    }

    private static void EnsureWithin(string parent, string child)
    {
        string normalizedParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedChild = Path.GetFullPath(child);
        if (!normalizedChild.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase))
            throw new ExtractionException($"Path escaped repository root: {child}.");
    }

    private static void EnsureWithinRepositoryOrTemp(string repositoryRoot, string path)
    {
        string normalizedRepository = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedTemp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedPath = Path.GetFullPath(path);
        if (!normalizedPath.StartsWith(normalizedRepository, StringComparison.OrdinalIgnoreCase) &&
            !normalizedPath.StartsWith(normalizedTemp, StringComparison.OrdinalIgnoreCase))
            throw new ExtractionException($"Artifact path escaped the repository and operating-system temporary roots: {path}.");
    }

    private static ControlFlowGraph TryGraph(SemanticModel model, MethodDeclarationSyntax method)
    {
        try
        {
            if (model.GetOperation(method) is not IMethodBodyOperation body)
                throw new ExtractionException($"Roslyn did not produce a method-body operation for {method.Identifier.ValueText}.");
            return ControlFlowGraph.Create(body);
        }
        catch (ExtractionException) { throw; }
        catch (ArgumentException exception)
        {
            throw new ExtractionException($"Could not construct a CFG for {method.Identifier.ValueText}: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            throw new ExtractionException($"Could not construct a CFG for {method.Identifier.ValueText}: {exception.Message}");
        }
        catch (NotSupportedException exception)
        {
            throw new ExtractionException($"Could not construct a CFG for {method.Identifier.ValueText}: {exception.Message}");
        }
    }

    private sealed class SemanticContext(Dictionary<SyntaxTree, SemanticModel> models, CSharpCompilation? compilation = null)
    {
        private readonly Dictionary<SyntaxTree, SemanticModel> _models = models;
        private readonly Dictionary<MethodDeclarationSyntax, ControlFlowGraph> _graphs = [];
        internal CSharpCompilation Compilation => compilation ?? throw new ExtractionException("The EVM compiler closure is unavailable.");
        internal SemanticModel Model(SyntaxTree tree) => _models[tree];
        internal bool TryModel(SyntaxTree tree, out SemanticModel? model) => _models.TryGetValue(tree, out model);

        internal FlowInfo Flow(SyntaxNode node)
        {
            if (node.AncestorsAndSelf().Any(static ancestor =>
                    ancestor is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax))
                throw new ExtractionException($"Required source node {node.Kind()} is inside a nested callable.");
            if (node is MethodDeclarationSyntax or ConstructorDeclarationSyntax or PropertyDeclarationSyntax or VariableDeclaratorSyntax or TypeDeclarationSyntax)
                return new FlowInfo(ControlFlowPath(node), -1, true);
            MethodDeclarationSyntax method = RequireMethodCfgOwner(node, $"Required source node {node.Kind()}");
            if (!_graphs.TryGetValue(method, out ControlFlowGraph? graph))
            {
                graph = TryGraph(Model(method.SyntaxTree), method);
                _graphs[method] = graph;
            }
            BasicBlock block = FindBlock(graph, node, $"Required source node {node.Kind()}");
            return new FlowInfo(ControlFlowPath(node), block.Ordinal, block.IsReachable);
        }

        internal void RequireCfg(MethodDeclarationSyntax method, string role)
        {
            if (!_graphs.ContainsKey(method)) _graphs[method] = TryGraph(Model(method.SyntaxTree), method);
            if (_graphs[method].Blocks.Length == 0)
                throw new ExtractionException($"Required method {role} has an empty CFG.");
        }

        internal void RequireDominates(SyntaxNode before, SyntaxNode after, string role)
        {
            (_, ControlFlowGraph graph, BasicBlock source, BasicBlock target) = ResolveBlocks(before, after, role);
            if (!source.IsReachable || !target.IsReachable || source.Ordinal == target.Ordinal && before.SpanStart >= after.SpanStart)
                throw new ExtractionException("CFG cannot establish caller definition before use: " + role);
            if (source.Ordinal == target.Ordinal) return;

            HashSet<int> visited = [];
            Stack<BasicBlock> pending = new([graph.Blocks[0]]);
            while (pending.TryPop(out BasicBlock? block))
            {
                if (block.Ordinal == source.Ordinal || !block.IsReachable || !visited.Add(block.Ordinal)) continue;
                if (block.Ordinal == target.Ordinal)
                    throw new ExtractionException("Caller definition does not dominate use: " + role);
                foreach (BasicBlock successor in Successors(block)) pending.Push(successor);
            }
        }

        private (MethodDeclarationSyntax Method, ControlFlowGraph Graph, BasicBlock Source, BasicBlock Target) ResolveBlocks(
            SyntaxNode from,
            SyntaxNode to,
            string role)
        {
            MethodDeclarationSyntax method = RequireMethodCfgOwner(from, role);
            MethodDeclarationSyntax targetMethod = RequireMethodCfgOwner(to, role);
            if (!ReferenceEquals(method, targetMethod))
                throw new ExtractionException($"{role} crosses method boundaries.");
            if (!_graphs.TryGetValue(method, out ControlFlowGraph? graph))
            {
                graph = TryGraph(Model(method.SyntaxTree), method);
                _graphs[method] = graph;
            }
            BasicBlock source = FindBlock(graph, from, role + " source");
            BasicBlock target = FindBlock(graph, to, role + " target");
            return (method, graph, source, target);
        }

        private BasicBlock FindBlock(ControlFlowGraph graph, SyntaxNode node, string role)
        {
            BasicBlock? direct = graph.Blocks.FirstOrDefault(candidate =>
                candidate.Operations.Any(operation => Contains(operation, node)) ||
                candidate.BranchValue is not null && Contains(candidate.BranchValue, node));
            if (direct is not null) return direct;
            foreach (SyntaxNode ancestor in node.Ancestors().Where(static candidate => candidate is StatementSyntax or BlockSyntax)
                         .OrderBy(static candidate => candidate.Span.Length))
            {
                BasicBlock? enclosing = graph.Blocks.FirstOrDefault(candidate =>
                    candidate.Operations.Any(operation => Contains(operation, ancestor)) ||
                    candidate.BranchValue is not null && Contains(candidate.BranchValue, ancestor));
                if (enclosing is not null) return enclosing;
            }
            foreach (SyntaxNode descendant in node.DescendantNodes().OrderBy(static child => child.SpanStart))
            {
                BasicBlock? nested = graph.Blocks.FirstOrDefault(candidate =>
                    candidate.Operations.Any(operation => Contains(operation, descendant)) ||
                    candidate.BranchValue is not null && Contains(candidate.BranchValue, descendant));
                if (nested is not null) return nested;
            }
            throw new ExtractionException($"{role} has no CFG block for syntax {node.Kind()}.");
        }

        private static IEnumerable<BasicBlock> Successors(BasicBlock block)
        {
            if (block.FallThroughSuccessor?.Destination is BasicBlock fallThrough) yield return fallThrough;
            if (block.ConditionalSuccessor?.Destination is BasicBlock conditional) yield return conditional;
        }

        private static bool Contains(IOperation operation, SyntaxNode node) =>
            operation.Syntax is not null && ReferenceEquals(operation.Syntax.SyntaxTree, node.SyntaxTree) && operation.Syntax.FullSpan.Contains(node.FullSpan);
    }

    private sealed record FlowInfo(string Path, int Block, bool Reachable);

    private sealed record NoFrameProof(int GotoCount, bool CfgProven, bool FailContractCreateBypassesNoFrame, string[] Paths);

    private sealed record ArtifactPaths(string IrPath, string ManifestPath, string LeanPath);

    private sealed record BuiltArtifacts(IrDocument Document, byte[] IrBytes, byte[] ManifestBytes, byte[] LeanBytes);
}
