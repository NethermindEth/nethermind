// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Receipt = Nethermind.Evm.Lean.ReceiptTerminalFoldExtractor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Evm.Lean.SequentialBlockTransactionFoldExtractor;

/// <summary>Extracts the bounded synchronous sequential transaction-fold boundary.</summary>
/// <remarks>
/// This is a source-audit package. It binds the block loop, auxiliary lifecycle/dispatch anchors,
/// and the caller's post-fold commit with Roslyn syntax, symbols, operations, and control-flow
/// graphs. It does not execute C# or claim
/// correctness of the EVM, world state, persistence, CLR, or dependency injection.
/// </remarks>
internal static partial class Extractor
{
    internal const string ExecutorPath =
        "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockValidationTransactionsExecutor.cs";
    internal const string BlockProcessorPath =
        "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs";
    internal const string BlockProcessorStandardPath =
        "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.std.cs";
    internal const string BlockProcessorInterfacePath =
        "src/Nethermind/Nethermind.Consensus/Processing/IBlockProcessor.cs";
    internal const string ProcessingOptionsPath =
        "src/Nethermind/Nethermind.Consensus/Processing/ProcessingOptions.cs";
    internal const string TransactionProcessorAdapterPath =
        "src/Nethermind/Nethermind.Consensus/Processing/TransactionProcessorAdapterExtensions.cs";
    internal const string BlockReceiptsTracerPath =
        "src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptsTracer.cs";
    internal const string BlockAccessListManagerPath =
        "src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.cs";
    internal const string BlockAccessListInterfacePath =
        "src/Nethermind/Nethermind.Consensus/Processing/IBlockAccessListManager.cs";
    internal const string ParallelExecutorPath =
        "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.ParallelBlockValidationTransactionsExecutor.cs";
    internal const string BlockProcessingModulePath =
        "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs";
    internal const string TransactionProcessorAdapterInterfacePath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/ITransactionProcessorAdapter.cs";

    internal const string ReceiptKernelPath =
        "tools/Evm/Lean/ReceiptTerminalFoldExtractor/Generated/ReceiptTerminalFoldKernel.lean";
    internal const string ReceiptRefinementPath =
        "tools/Evm/Lean/ReceiptTerminalFoldExtractor/Refinement/ReceiptTerminalFold.lean";

    internal const string ArtifactName = "SequentialBlockTransactionFold";
    internal const string SourcePinsPath =
        "tools/Evm/Lean/SequentialBlockTransactionFoldExtractor/SOURCE_PINS.json";
    internal const string DefaultOutputPath =
        "tools/Evm/Lean/SequentialBlockTransactionFoldExtractor/Generated";
    internal const string DefaultLeanPath =
        "tools/Evm/Lean/SequentialBlockTransactionFoldExtractor/Generated/SequentialBlockTransactionFold.lean";
    internal const string CompilerReferenceInventoryPath =
        "tools/Evm/Lean/ReceiptTerminalFoldExtractor/COMPILER_REFERENCE_PINS.json";
    internal const string ReceiptSourcePinsPath =
        "tools/Evm/Lean/ReceiptTerminalFoldExtractor/SOURCE_PINS.json";
    internal const string ReceiptSourceManifestPath =
        "tools/Evm/Lean/ReceiptTerminalFoldExtractor/Generated/ReceiptTerminalFoldKernel.source-manifest.json";

    private const int SchemaVersion = 9;
    private const string ExtractorVersion = "1.8.0";
    private const string Kernel =
        "direct-inner sequential BlockValidationTransactionsExecutor transaction fold (BAL disabled premise)";
    private const string ExactExecutorSignature =
        "M:Nethermind.Consensus.Processing.BlockProcessor.BlockValidationTransactionsExecutor.ProcessTransactions(Nethermind.Core.Block,Nethermind.Consensus.Processing.ProcessingOptions,Nethermind.Blockchain.Tracing.BlockReceiptsTracer,System.Threading.CancellationToken)";
    private const string ReceiptRelation =
        "each settled ordinary terminal observation supplies the next exact receipt/gas state; no terminal is re-executed";
    private const int CompilerReferenceInventorySchemaVersion = 2;
    private const int CompilerReferenceInventoryCount = 434;
    private const string CompilerReferenceInventoryAggregateSha256 =
        "6fdfa102a4190083ae62080355aa6691b5f46acc32d4f9da2212012686d9c2e2";
    private const string CompilerReferenceInventorySha256 =
        "1f73a3800a957dd02d9d7b06b3b955c81bb92fd9eb8d2831920905394bfc1d5c";
    private static readonly SourcePin[] ExpectedReceiptSourcePins =
    [
        new("src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptsTracer.cs",
            "exact-base receipt tracer terminal, snapshot owner, and IsTracingReceipt gate",
            "d4504f54b50dd43e2ab5bc7172ce2cf9e48453fcede990e5e667e743146262ff"),
        new("src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptGasAccountingKernel.cs",
            "delegated fixed-width gas kernel",
            "0882c798e6ffb2243735a9ec0cec5d442b20d51277feeba95e5b68ecfaf89e32"),
        new("src/Nethermind/Nethermind.Evm/TransactionProcessing/GasConsumed.cs",
            "gas input fields and effective block gas",
            "1dc4e78d5a17056dcb00d496136a32a899245f3a7b3e5a9eef74c4760ef68686"),
        new("src/Nethermind/Nethermind.Core/TransactionReceipt.cs",
            "receipt output fields including concrete zero-default diagnostics",
            "55b62b4e0708e590a001916e9a73d07ac70e225b16f711574e811c98e540cc63"),
        new("src/Nethermind/Nethermind.Core/Transaction.cs", "transaction receipt fields",
            "3fdf94805739c6ccdfa8b4617c5d4cfa6bee9e9565f16d667b22106c434d6596"),
        new("src/Nethermind/Nethermind.Core/TransactionExtensions.cs", "effective price oracle",
            "d3df04cb465668a3dcc842f33e5bc7a16d2420cdc1dcd03edf1c3275c2b24852"),
        new("src/Nethermind/Nethermind.Core/Block.cs", "block identity projections",
            "3cdd12ca52b00b6eefea372be868649653ac57043194aa081d41064d0ed749aa"),
        new("src/Nethermind/Nethermind.Core/BlockHeader.cs", "header gas and identity fields",
            "f354ddd2d45afc8774739ac46a10535b915871ce4ad11d996d0ab972cf9e7349"),
        new("src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs",
            "normally returning terminal caller, BuildUp/EIP-8037 account-reap ordering, and separate TransactionResult classification",
            "0374c6f35a9a23361f37b41162ac281ca83b09846ab4295d5a592db6315c99cc"),
        new("src/Nethermind/Nethermind.Evm/StatusCode.cs", "live receipt status constants",
            "e896f55406c9fc1ce99199d80bea69b87fa8891c23afef59712b87cadb971190"),
        new("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs",
            "live block-gas combine adapter",
            "3c31ba40c24a78bf11243b38859168350f02934806124e913da0560bc1a4350f"),
        new("src/Nethermind/Nethermind.Evm/GasPolicy/TransactionGasInitializationKernel.cs",
            "live block-gas combine dependency closure",
            "65ab0742596ea7ebf49d2c79601eae00152d8d039ac8e417076bf63123be1e82"),
    ];
    private static readonly CompilerReferenceClosureIdentity CompilerReferenceClosure = new(
        CompilerReferenceInventoryPath,
        CompilerReferenceInventorySha256,
        CompilerReferenceInventoryCount,
        CompilerReferenceInventoryAggregateSha256);

    private static readonly string[] SourcePaths =
    [
        ExecutorPath,
        BlockProcessorPath,
        BlockProcessorStandardPath,
        BlockProcessorInterfacePath,
        ProcessingOptionsPath,
    ];

    private static readonly string[] AuxiliarySourcePaths =
    [
        TransactionProcessorAdapterPath,
        BlockReceiptsTracerPath,
        BlockAccessListManagerPath,
        BlockAccessListInterfacePath,
        ParallelExecutorPath,
        BlockProcessingModulePath,
        TransactionProcessorAdapterInterfacePath,
    ];

    private static readonly string[] SupportSourcePaths =
    [
        "src/Nethermind/Directory.Build.props",
        "src/Nethermind/Nethermind.Consensus/Processing/ExecutionFlags.std.cs",
        "src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListValidationIndex.cs",
        "src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListValidationIndex.LaneStore.cs",
        "src/Nethermind/Nethermind.Consensus/Processing/BlockCachePreWarmer.cs",
        "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.SystemContractHandler.cs",
        "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockAccessListSystemContractHandler.cs",
        "src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.Validation.cs",
        "src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.StateChanges.cs",
        "src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.SystemContracts.cs",
        "src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.TxProcessorPool.cs",
        "src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptGasAccountingKernel.cs",
    ];

    private static readonly string[] AuxiliarySourceRoles =
    [
        "transaction-adapter-order",
        "exact-base-tracer-lifecycle",
        "bal-enabled-derivation",
        "bal-manager-interface",
        "bal-dispatch-and-sequential-fallback",
        "standard-mainnet-di-registration",
        "transaction-adapter-interface",
    ];

    private static readonly string[] IncludedScope =
    [
        "direct-inner BlockValidationTransactionsExecutor.ProcessTransactions loop",
        "direct-inner BlockValidationTransactionsExecutor.ProcessTransaction result and invalid-prefix control",
        "direct-inner BlockProcessor.ProcessBlock pre-commit, transaction invocation, and post-transaction CommitState(spec)",
        "typed receipt/gas state chaining from ReceiptTerminalFold finalization observations",
        "BAL-disabled direct-inner execution premise",
        "source-closed StartNewBlockTrace reset and exact-base BlockReceiptsTracer lifecycle",
        "source-closed transaction adapter StartNewTxTrace -> Execute -> EndTxTrace order",
        "source-closed BlockAccessListManager.Enabled derivation and BAL decorator sequential fallback",
        "source-closed standard-mainnet base executor registration and parallel decorator registration",
        "source-closed non-virtual exact-base BlockValidationTransactionsExecutor selection",
    ];

    private static readonly string[] ExcludedScope =
    [
        "parallel worker execution and parallel BlockReceiptsTracer semantics",
        "system, production, simulation, Optimism, Taiko, and other non-standard transaction routes",
        "EVM execution, gas production, world-state transitions, roots, trie, RLP, hashes, rewards, withdrawals, requests",
        "runtime DI activation, plugin replacements, receipt persistence, background work, CLR/JIT behavior, and callback implementation",
    ];

    private static readonly string[] OpenObligations =
    [
        "An adapter must establish that every supplied terminal observation is produced by the ordinary non-system source route.",
        "An adapter must establish receipt/gas-state equality between one observation's terminal state and the next transaction's start state.",
        "An adapter must relate terminal State.headerGasUsed to production block.Header.GasUsed for the guarded gas-limit comparison.",
        "The source does not prove world-state Commit(spec, commitRoots:false) durability or rollback after InvalidBlockException.",
        "The invalid-prefix state is a logical prefix projection; production rollback/disposal after InvalidTransactionException is not proved.",
        "OpenSourceCompositionObligation is explicitly unproved: BAL disabled, ordinary exact-base execution, a fresh sequential tracer, and production observations must be supplied by a separately justified source semantics.",
        "Source anchors close the BAL Enabled derivation, DI base/decorator registrations, and non-virtual executor declaration; runtime DI activation and virtual/CLR dispatch remain uninterpreted.",
        "The tracer lifecycle is projected: StartNewBlockTrace seeds currentIndex 0, clears receipts and gas histories, resets cumulative gas, each StartNewTxTrace/EndTxTrace pair emits receipt index i and advances the next currentIndex; the tracer source is not re-executed here.",
        "No opaque caller-filled proposition or result equality is an exported refinement premise; the conditional model theorem consumes ReceiptTerminalChain only. Production callbacks, executor normal returns and commit behavior remain in the unproved source-composition obligation.",
        "OnlyOkTerminal and the successful-only input predicate admit only ordinary .ok settled entries; a truthy EvmException or other normal-returning non-.ok result is outside that relation.",
        "Auxiliary lifecycle anchors must be owned by declared methods on reachable CFG blocks; " +
        "Start/Execute/End and delegate/index relations require normal-path dominance/postdominance, " +
        "direct receipt appends, and a BAL-disabled true-arm return. ProcessBlock additionally requires " +
        "StartNewBlockTrace -> pre-commit -> fold -> TransactionsExecuted -> post-commit on the " +
        "normal CFG path, and guarded direct actions must be sole true-arm statements.",
        "The invalid-result source path may run EndTxTrace before throwing; the model treats invalid entries as logical input rejection and does not claim that production false-result poststate.",
        "The source identities and CFG/IOperation evidence do not establish C# execution, virtual dispatch, DI, or native arithmetic semantics.",
    ];

    private static readonly string[] ExpectedAnchorCanonicalSyntax =
    [
        "SetupTxTimingMetrics(block)",
        "shouldValidate=!processingOptions.ContainsFlag(ProcessingOptions.NoValidation)",
        "ProcessTransaction(block,currentTx,i,receiptsTracer,processingOptions)",
        "shouldValidate&&block.Header.GasUsed>block.Header.GasLimit",
        "ThrowInvalidTransactionException(result,block.Header,currentTx,index)",
        "transactionProcessor.ProcessTransaction(currentTx,receiptsTracer,processingOptions,_stateProvider)",
        "_transactionProcessedEventHandler?.OnTransactionProcessed(newTxProcessedEventArgs(index,currentTx,block.Header,receiptsTracer.TxReceipts[index]))",
        "_blockTransactionsExecutor.ProcessTransactions(block,options,ReceiptsTracer,token)",
        "CommitState(spec)",
        "CommitState(spec)",
        "TransactionsExecuted?.Invoke()",
    ];

    private static readonly (string Id, string Owner, string Name, int ParameterCount)[] ExpectedMemberShape =
    [
        ("executor.processTransactions", "BlockProcessor.BlockValidationTransactionsExecutor", "ProcessTransactions", 4),
        ("executor.processTransaction", "BlockProcessor.BlockValidationTransactionsExecutor", "ProcessTransaction", 5),
        ("block.processBlock", "BlockProcessor", "ProcessBlock", 5),
    ];

    private static readonly string[] ExpectedAnchorRelations =
    [
        "metrics setup precedes the loop",
        "NoValidation controls the gas-limit guard",
        "loop invokes one transaction in source order",
        "successful prefix can still reject the block after a receipt",
        "false TransactionResult stops the prefix before the event",
        "ordinary transaction execution is an external settled observation",
        "event observes the receipt at the transaction index",
        "direct-inner caller enters the transaction executor; BAL is disabled by an explicit model premise",
        "pre-transaction journal boundary",
        "post-transaction journal boundary",
        "normal executor return precedes the TransactionsExecuted signal",
    ];

    private static readonly string[] ExpectedAnchorControlFlows =
    [
        "executor.processTransactions",
        "executor.processTransactions",
        "executor.processTransactions",
        "executor.processTransactions",
        "executor.processTransaction",
        "executor.processTransaction",
        "executor.processTransaction",
        "block.processBlock",
        "block.processBlock",
        "block.processBlock",
        "block.processBlock",
    ];


    private static readonly AuxiliaryAnchorExpectation[] ExpectedAuxiliaryAnchors =
    [
        new("block.receipts-tracer-start", BlockProcessorPath, "BlockProcessor", "ProcessBlock",
            "ReceiptsTracer.StartNewBlockTrace(block)", "block tracer reset precedes the pre-transaction commit", true),
        new("adapter.tx-trace-start", TransactionProcessorAdapterPath, "TransactionProcessorAdapterExtensions", "ProcessTransaction",
            "receiptsTracer.StartNewTxTrace(currentTx)", "adapter starts the tx trace before Execute", true),
        new("adapter.execute", TransactionProcessorAdapterPath, "TransactionProcessorAdapterExtensions", "ProcessTransaction",
            "transactionProcessor.Execute(currentTx,receiptsTracer)", "adapter executes between trace start and trace end", true),
        new("adapter.tx-trace-end", TransactionProcessorAdapterPath, "TransactionProcessorAdapterExtensions", "ProcessTransaction",
            "receiptsTracer.EndTxTrace()", "adapter ends the tx trace after Execute", true),
        new("tracer.reset-index", BlockReceiptsTracerPath, "BlockReceiptsTracer", "StartNewBlockTrace",
            "_currentIndex=0", "StartNewBlockTrace resets the receipt index", false),
        new("tracer.reset-receipts", BlockReceiptsTracerPath, "BlockReceiptsTracer", "StartNewBlockTrace",
            "_txReceipts.Clear()", "StartNewBlockTrace clears receipt history", true),
        new("tracer.reset-block-gas", BlockReceiptsTracerPath, "BlockReceiptsTracer", "StartNewBlockTrace",
            "_cumulativeBlockGasPerTx.Clear()", "StartNewBlockTrace clears block-gas history", true),
        new("tracer.reset-receipt-gas", BlockReceiptsTracerPath, "BlockReceiptsTracer", "StartNewBlockTrace",
            "_cumulativeReceiptGas=0", "StartNewBlockTrace resets cumulative receipt gas", false),
        new("tracer.success-append", BlockReceiptsTracerPath, "BlockReceiptsTracer", "MarkAsSuccess",
            "_txReceipts.Add(BuildReceipt(recipient,gasSpent,StatusCode.Success,logs,stateRoot))", "successful receipt is appended before forwarding", true),
        new("tracer.failure-append", BlockReceiptsTracerPath, "BlockReceiptsTracer", "MarkAsFailed",
            "_txReceipts.Add(BuildFailedReceipt(recipient,gasSpent,error,stateRoot))", "failed receipt is appended before forwarding", true),
        new("tracer.receipt-index", BlockReceiptsTracerPath, "BlockReceiptsTracer", "BuildReceipt",
            "Index=_currentIndex", "receipt index is read before EndTxTrace increments it", false),
        new("tracer.tx-start-current", BlockReceiptsTracerPath, "BlockReceiptsTracer", "StartNewTxTrace",
            "CurrentTx=tx", "StartNewTxTrace records the current transaction", false),
        new("tracer.tx-start-delegate", BlockReceiptsTracerPath, "BlockReceiptsTracer", "StartNewTxTrace",
            "_currentTxTracer=_otherTracer.StartNewTxTrace(tx)", "StartNewTxTrace delegates the nested tracer", false),
        new("tracer.tx-end-delegate", BlockReceiptsTracerPath, "BlockReceiptsTracer", "EndTxTrace",
            "_otherTracer.EndTxTrace()", "EndTxTrace delegates before incrementing the index", true),
        new("tracer.tx-end-index", BlockReceiptsTracerPath, "BlockReceiptsTracer", "EndTxTrace",
            "_currentIndex++", "EndTxTrace advances the next receipt index", false),
        new("bal.enabled-spec", BlockAccessListManagerPath, "BlockAccessListManager", "PrepareForProcessing",
            "_blockAccessListsEnabled=spec.BlockLevelAccessListsEnabled", "BAL Enabled derives from the release spec", false),
        new("bal.enabled-derived", BlockAccessListManagerPath, "BlockAccessListManager", "PrepareForProcessing",
            "Enabled=_blockAccessListsEnabled&&!suggestedBlock.IsGenesis", "BAL Enabled excludes genesis blocks", false),
        new("parallel.bal-disabled-gate", ParallelExecutorPath, "BlockProcessor.ParallelBlockValidationTransactionsExecutor", "ProcessTransactions",
            "!balManager.Enabled", "the decorator falls back when BAL is disabled", false),
        new("parallel.inner-sequential", ParallelExecutorPath, "BlockProcessor.ParallelBlockValidationTransactionsExecutor", "ProcessTransactions",
            "inner.ProcessTransactions(block,processingOptions,receiptsTracer,token)", "the BAL-disabled branch calls the exact inner executor", true),
        new("parallel.selection", ParallelExecutorPath, "BlockProcessor.ParallelBlockValidationTransactionsExecutor", "ProcessTransactions",
            "ExecutionFlags.ParallelExecution&&!block.IsGenesis&&balManager.ParallelExecutionEnabled", "only the enabled BAL route selects parallel execution", false),
        new("di.base-executor", BlockProcessingModulePath, "BlockProcessingModule.StandardBlockValidationModule", "Load",
            "BlockValidationTransactionsExecutor", "standard validation registers the exact base executor", true),
        new("di.parallel-decorator", BlockProcessingModulePath, "BlockProcessingModule.StandardBlockValidationModule", "Load",
            "ParallelBlockValidationTransactionsExecutor", "standard validation decorates the base executor", true),
        new("di.bal-manager", BlockProcessingModulePath, "BlockProcessingModule", "Load",
            "AddScoped<IBlockAccessListManager,BlockAccessListManager>()", "standard validation registers the BAL manager", true),
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default
        .WithLanguageVersion(LanguageVersion.CSharp14)
        .WithDocumentationMode(DocumentationMode.Parse)
        .WithKind(SourceCodeKind.Regular);

    private static readonly string[] CompilerReferenceClosurePaths =
    [
        "src/Nethermind/artifacts/bin/Nethermind.Init/release",
        "tools/artifacts/bin/Evm/release",
        "src/Nethermind/artifacts/bin/Nethermind.Network.Enr.Test/release",
    ];

    private sealed record AuxiliaryAnchorExpectation(
        string Id,
        string Path,
        string Owner,
        string Member,
        string CanonicalFragment,
        string Relation,
        bool RequireInvocation);

    internal static ExtractionResult Extract(string repoRoot, string outputDirectory, string? leanOutputPath = null) =>
        ExtractCore(Path.GetFullPath(repoRoot), Path.GetFullPath(outputDirectory), leanOutputPath, null, true);

    internal static ExtractionResult ExtractForTest(
        string repoRoot,
        string outputDirectory,
        IReadOnlyDictionary<string, byte[]> sourceOverrides,
        string? leanOutputPath = null) =>
        ExtractCore(Path.GetFullPath(repoRoot), Path.GetFullPath(outputDirectory), leanOutputPath, sourceOverrides, false);

    internal static IrDocument LoadIrForTest(string path)
    {
        IrDocument document = Deserialize<IrDocument>(File.ReadAllBytes(path));
        ValidateIr(document);
        return document;
    }

    internal static CompilerReferenceIdentity[] LoadCompilerReferencesForTest(string repoRoot) =>
        ReadCompilerReferenceIdentities(Path.GetFullPath(repoRoot));

    internal static void ValidateCompilerReferencesForTest(CompilerReferenceIdentity[] references) =>
        ValidateCompilerReferenceIdentities(references);

    internal static void ValidateCompilerInventoryForTest(string repoRoot) =>
        _ = ReadCompilerReferenceInventory(Path.GetFullPath(repoRoot));

    internal static byte[] EmitLeanForTest(IrDocument document)
    {
        ValidateIr(document);
        return LeanEmitter.Emit(document);
    }

    internal static void ValidateIrForTest(IrDocument document) => ValidateIr(document);

    internal static void ValidateIrForEmitter(IrDocument document) => ValidateIr(document);

    internal static void ValidateCheckedIn(string repoRoot, string? outputDirectory)
    {
        string root = Path.GetFullPath(repoRoot);
        SourcePinDocument pins = ReadPins(root);
        SourceFile[] sources = ReadSources(root, pins, null, enforcePins: true);
        SourceFile[] auxiliarySources = ReadAuxiliarySources(root, pins, null, enforcePins: true);
        SourceFile[] supportSources = ReadSupportSources(root, pins, null, enforcePins: true);
        CompositionIdentity composition = BuildComposition(root);
        if (supportSources.Length != SupportSourcePaths.Length) throw new ExtractionException("fold.support.identity: incomplete closure.");
        CompilerReferenceIdentity[] compilerReferences = ReadCompilerReferenceIdentities(root);

        string generated = Path.GetFullPath(outputDirectory ?? Path.Combine(root, DefaultOutputPath));
        string irPath = Path.Combine(generated, ArtifactName + ".ir.json");
        string leanPath = Path.Combine(generated, ArtifactName + ".lean");
        string manifestPath = Path.Combine(generated, ArtifactName + ".source-manifest.json");
        if (!File.Exists(irPath) || !File.Exists(leanPath) || !File.Exists(manifestPath))
        {
            throw new ExtractionException("Checked-in sequential transaction-fold artifacts are incomplete.");
        }

        IrDocument document = Deserialize<IrDocument>(File.ReadAllBytes(irPath));
        ValidateIr(document);
        if (!CompositionMatches(document.Composition, composition))
        {
            throw new ExtractionException("The checked-in sequential transaction-fold IR does not cover the pinned receipt composition artifacts.");
        }
        if (!document.Sources.Select(static source => source.Path).SequenceEqual(sources.Select(static source => source.RelativePath), StringComparer.Ordinal) ||
            !document.Sources.Zip(sources).All(static pair => pair.First.Role == pair.Second.Role &&
                pair.First.Sha256 == pair.Second.Sha256 && pair.First.SyntaxSha256 == pair.Second.SyntaxSha256))
        {
            throw new ExtractionException("The checked-in sequential transaction-fold IR does not cover the pinned source identities.");
        }
        if (!document.AuxiliarySources.Select(static source => source.Path)
                .SequenceEqual(auxiliarySources.Select(static source => source.RelativePath), StringComparer.Ordinal) ||
            !document.AuxiliarySources.Zip(auxiliarySources).All(static pair => pair.First.Role == pair.Second.Role &&
                pair.First.Sha256 == pair.Second.Sha256))
        {
            throw new ExtractionException("The checked-in sequential transaction-fold IR does not cover the pinned auxiliary source identities.");
        }
        if (!document.SupportSources.SequenceEqual(supportSources.Select(static source =>
                new AuxiliarySourceIdentity(source.RelativePath, source.Role, source.Sha256))))
        {
            throw new ExtractionException("fold.support.identity: checked-in support sources differ.");
        }
        if (!CompilerReferenceClosureMatches(document.CompilerReferences) ||
            compilerReferences.Length != CompilerReferenceInventoryCount ||
            !string.Equals(CompilerReferenceAggregateSha256(compilerReferences),
                CompilerReferenceInventoryAggregateSha256, StringComparison.Ordinal))
        {
            throw new ExtractionException("The checked-in sequential transaction-fold IR does not cover the pinned compiler/reference closure.");
        }

        ArtifactManifest manifest = Deserialize<ArtifactManifest>(File.ReadAllBytes(manifestPath));
        ValidateManifest(manifest, document, Sha256(File.ReadAllBytes(irPath)), Sha256(File.ReadAllBytes(leanPath)));
        if (RegexPlaceholder(File.ReadAllText(leanPath)))
        {
            throw new ExtractionException("Checked-in sequential transaction-fold Lean contains a proof placeholder.");
        }

        string regenerationDirectory = Path.Combine(
            Path.GetTempPath(), "sequential-block-fold-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(regenerationDirectory);
        try
        {
            _ = ExtractCore(root, regenerationDirectory, null, null, enforcePins: true);
            CompareArtifactBytes(irPath,
                Path.Combine(regenerationDirectory, ArtifactName + ".ir.json"),
                "IR");
            CompareArtifactBytes(leanPath,
                Path.Combine(regenerationDirectory, ArtifactName + ".lean"),
                "Lean");
            CompareArtifactBytes(manifestPath,
                Path.Combine(regenerationDirectory, ArtifactName + ".source-manifest.json"),
                "source manifest");
        }
        finally
        {
            if (Directory.Exists(regenerationDirectory))
            {
                Directory.Delete(regenerationDirectory, recursive: true);
            }
        }
    }

    private static ExtractionResult ExtractCore(
        string root,
        string outputDirectory,
        string? leanOutputPath,
        IReadOnlyDictionary<string, byte[]>? sourceOverrides,
        bool enforcePins)
    {
        SourcePinDocument pins = ReadPins(root);
        SourceFile[] sources = ReadSources(root, pins, sourceOverrides, enforcePins);
        SourceFile[] auxiliarySources = ReadAuxiliarySources(root, pins, sourceOverrides, enforcePins);
        SourceFile[] supportSources = ReadSupportSources(root, pins, sourceOverrides, enforcePins);
        CompositionIdentity composition = BuildComposition(root);

        CompilerContext compiler = BuildCompleteCompilerContext(root, sources, auxiliarySources, supportSources);
        SemanticModel executorModel = compiler.Compilation.GetSemanticModel(FindSource(sources, ExecutorPath).Tree, ignoreAccessibility: true);
        SemanticModel blockModel = compiler.Compilation.GetSemanticModel(FindSource(sources, BlockProcessorPath).Tree, ignoreAccessibility: true);

        (MethodDeclarationSyntax ProcessTransactions, MethodDeclarationSyntax ProcessTransaction) executorMethods =
            FindExecutorMethods(FindSource(sources, ExecutorPath));
        MethodDeclarationSyntax processBlock = FindMethod(
            FindSource(sources, BlockProcessorPath), "BlockProcessor", "ProcessBlock", 5);
        List<SynchronousCallableIdentity> synchronousCallables =
        [
            BindSynchronousCallable("executor.processTransaction", executorMethods.ProcessTransaction,
                executorModel.GetDeclaredSymbol(executorMethods.ProcessTransaction) ??
                    throw new ExtractionException("fold.synchronous.executor.processTransaction: missing declared method.")),
        ];

        List<MemberIdentity> members =
        [
            BindMethod("executor.processTransactions",
                FindSource(sources, ExecutorPath), executorMethods.ProcessTransactions, executorModel, compiler.Compilation),
            BindMethod("executor.processTransaction",
                FindSource(sources, ExecutorPath), executorMethods.ProcessTransaction, executorModel, compiler.Compilation),
            BindMethod("block.processBlock",
                FindSource(sources, BlockProcessorPath), processBlock, blockModel, compiler.Compilation),
        ];

        ConditionalSignalIdentity transactionsExecuted = BindTransactionsExecuted(
            FindSource(sources, BlockProcessorPath), processBlock, blockModel, compiler.Compilation);
        List<AnchorIdentity> anchors = BuildAnchors(
            sources,
            executorModel,
            blockModel,
            compiler.Compilation,
            executorMethods,
            processBlock,
            transactionsExecuted.Evaluation.ControlFlowBlock,
            synchronousCallables,
            out TransactionProjectionIdentity transactionProjection);
        List<ControlFlowIdentity> controlFlows =
        [
            BuildControlFlow("executor.processTransactions", FindSource(sources, ExecutorPath), executorMethods.ProcessTransactions, executorModel, compiler.Compilation),
            BuildControlFlow("executor.processTransaction", FindSource(sources, ExecutorPath), executorMethods.ProcessTransaction, executorModel, compiler.Compilation),
            BuildControlFlow("block.processBlock", FindSource(sources, BlockProcessorPath), processBlock, blockModel, compiler.Compilation),
        ];
        List<AuxiliaryAnchorIdentity> auxiliaryAnchors = BuildAuxiliaryAnchors(
            sources,
            auxiliarySources,
            compiler,
            executorMethods,
            processBlock,
            transactionsExecuted.Evaluation.ControlFlowBlock);
        SourceRouteIdentity sourceRoute = BuildSourceRoute(
            sources,
            auxiliarySources,
            compiler,
            executorMethods.ProcessTransactions);

        SourceIdentity[] sourceIdentities = sources
            .Select(static source => new SourceIdentity(source.RelativePath, source.Role, source.Sha256, source.SyntaxSha256))
            .ToArray();
        AuxiliarySourceIdentity[] auxiliarySourceIdentities = auxiliarySources
            .Select(static source => new AuxiliarySourceIdentity(source.RelativePath, source.Role, source.Sha256))
            .ToArray();
        IrDocument document = new(
            SchemaVersion,
            ExtractorVersion,
            Kernel,
            IncludedScope,
            ExcludedScope,
            sourceIdentities,
            auxiliarySourceIdentities,
            supportSources.Select(static source => new AuxiliarySourceIdentity(source.RelativePath, source.Role, source.Sha256)).ToArray(),
            CompilerReferenceClosure,
            members.ToArray(),
            anchors.ToArray(),
            auxiliaryAnchors.ToArray(),
            controlFlows.ToArray(),
            transactionsExecuted,
            transactionProjection,
            synchronousCallables.ToArray(),
            composition,
            sourceRoute,
            OpenObligations);
        ValidateIr(document);

        byte[] irBytes = Serialize(document);
        IrDocument roundTripped = Deserialize<IrDocument>(irBytes);
        ValidateIr(roundTripped);
        byte[] leanBytes = LeanEmitter.Emit(roundTripped);

        string output = Path.GetFullPath(outputDirectory);
        string leanPath = Path.GetFullPath(leanOutputPath ?? Path.Combine(output, ArtifactName + ".lean"));
        EnsureWithin(output, leanPath);
        EnsureWithin(output, Path.Combine(output, ArtifactName + ".ir.json"));
        EnsureWithin(output, Path.Combine(output, ArtifactName + ".source-manifest.json"));
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(Path.GetDirectoryName(leanPath)!);
        string irPath = Path.Combine(output, ArtifactName + ".ir.json");
        string manifestPath = Path.Combine(output, ArtifactName + ".source-manifest.json");
        ArtifactManifest manifest = new(
            SchemaVersion,
            ExtractorVersion,
            Kernel,
            roundTripped.Sources,
            roundTripped.AuxiliarySources,
            roundTripped.SupportSources,
            roundTripped.CompilerReferences,
            roundTripped.Members.Select(static member => member.Id).ToArray(),
            roundTripped.Anchors.Select(static anchor => anchor.Id).ToArray(),
            roundTripped.AuxiliaryAnchors.Select(static anchor => anchor.Id).ToArray(),
            roundTripped.ControlFlows.Select(static flow => flow.Id).ToArray(),
            roundTripped.Composition,
            roundTripped.SourceRoute,
            Sha256(irBytes),
            Sha256(leanBytes));
        ValidateManifest(manifest, roundTripped, Sha256(irBytes), Sha256(leanBytes));
        byte[] manifestBytes = Serialize(manifest);

        WriteNewArtifact(irPath, irBytes);
        WriteNewArtifact(leanPath, leanBytes);
        WriteNewArtifact(manifestPath, manifestBytes);

        return new(irPath, manifestPath, leanPath, sources.Length, members.Count, anchors.Count, controlFlows.Count);
    }

    private static SourcePinDocument ReadPins(string root)
    {
        string path = Path.Combine(root, SourcePinsPath);
        if (!File.Exists(path))
        {
            throw new ExtractionException($"Missing source pin document '{SourcePinsPath}'.");
        }

        try
        {
            SourcePinDocument document = Deserialize<SourcePinDocument>(File.ReadAllBytes(path));
            string[] expectedRoles = ["executor", "caller", "partial-standard-source", "interface-closure", "flag-closure"];
            if (document.SchemaVersion != 3 || document.Sources is null || document.AuxiliarySources is null || document.SupportSources is null ||
                document.Sources.Any(static source => source is null) ||
                !document.Sources.Select(static source => source.Path).SequenceEqual(SourcePaths, StringComparer.Ordinal) ||
                document.Sources.Any(static source => !IsSha256(source.Sha256) || string.IsNullOrWhiteSpace(source.Role)))
            {
                throw new ExtractionException("The sequential transaction-fold source pin document has the wrong shape.");
            }

            if (!document.Sources.Select(static source => source.Role)
                    .SequenceEqual(expectedRoles, StringComparer.Ordinal))
            {
                throw new ExtractionException("The sequential transaction-fold source pin roles changed.");
            }

            if (!document.AuxiliarySources.Select(static source => source.Path)
                    .SequenceEqual(AuxiliarySourcePaths, StringComparer.Ordinal) ||
                !document.AuxiliarySources.Select(static source => source.Role)
                    .SequenceEqual(AuxiliarySourceRoles, StringComparer.Ordinal) ||
                document.AuxiliarySources.Any(static source => source is null || !IsSha256(source.Sha256)))
            {
                throw new ExtractionException("The sequential transaction-fold auxiliary source pin set changed.");
            }

            if (document.SupportSources.Any(static source => source is null || !IsSha256(source.Sha256) || source.Role != "complete-partial-compilation-support") ||
                !document.SupportSources.Select(static source => source.Path).SequenceEqual(SupportSourcePaths, StringComparer.Ordinal))
            {
                throw new ExtractionException("fold.support.pins: complete partial-source inventory changed.");
            }
            return document;
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"The sequential transaction-fold source pin document is invalid: {exception.Message}");
        }
    }

    private static SourceFile[] ReadSources(
        string root,
        SourcePinDocument pins,
        IReadOnlyDictionary<string, byte[]>? sourceOverrides,
        bool enforcePins)
    {
        if (sourceOverrides is not null && sourceOverrides.Keys.Any(path =>
                !SourcePaths.Contains(path, StringComparer.Ordinal) &&
                !AuxiliarySourcePaths.Contains(path, StringComparer.Ordinal) &&
                !SupportSourcePaths.Contains(path, StringComparer.Ordinal)))
        {
            throw new ExtractionException("A source override is outside the sequential transaction-fold closure.");
        }

        SourceFile[] sources = new SourceFile[SourcePaths.Length];
        for (int index = 0; index < SourcePaths.Length; index++)
        {
            string relativePath = SourcePaths[index];
            SourcePin pin = pins.Sources[index];
            sources[index] = ReadSource(root, relativePath, pin, sourceOverrides, enforcePins);
        }

        return sources;
    }

    private static SourceFile[] ReadAuxiliarySources(
        string root,
        SourcePinDocument pins,
        IReadOnlyDictionary<string, byte[]>? sourceOverrides,
        bool enforcePins)
    {
        SourceFile[] sources = new SourceFile[AuxiliarySourcePaths.Length];
        for (int index = 0; index < AuxiliarySourcePaths.Length; index++)
        {
            string relativePath = AuxiliarySourcePaths[index];
            SourcePin pin = pins.AuxiliarySources[index];
            sources[index] = ReadSource(root, relativePath, pin, sourceOverrides, enforcePins);
        }

        return sources;
    }

    private static SourceFile[] ReadSupportSources(string root, SourcePinDocument pins,
        IReadOnlyDictionary<string, byte[]>? sourceOverrides, bool enforcePins) =>
        pins.SupportSources.Select(pin => ReadSource(root, pin.Path, pin, sourceOverrides, enforcePins)).ToArray();

    private static SourceFile ReadSource(
        string root,
        string relativePath,
        SourcePin pin,
        IReadOnlyDictionary<string, byte[]>? sourceOverrides,
        bool enforcePins)
    {
        byte[] bytes;
        if (sourceOverrides is not null && sourceOverrides.TryGetValue(relativePath, out byte[]? overrideBytes))
        {
            bytes = overrideBytes ?? throw new ExtractionException(
                $"The source override for '{relativePath}' is null.");
        }
        else
        {
            string fullPath = Path.Combine(root, relativePath);
            if (!File.Exists(fullPath))
            {
                throw new ExtractionException($"Missing source closure member '{relativePath}'.");
            }

            bytes = File.ReadAllBytes(fullPath);
        }

        string fullSourcePath = Path.Combine(root, relativePath);
        string sourceText;
        try
        {
            sourceText = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ExtractionException($"Source '{relativePath}' is not valid UTF-8: {exception.Message}");
        }

        if (relativePath == "src/Nethermind/Directory.Build.props")
        {
            System.Xml.Linq.XDocument props = System.Xml.Linq.XDocument.Parse(sourceText);
            System.Xml.Linq.XElement[] aliases = props.Descendants("Using").ToArray();
            if (aliases.Length != 1 || (string?)aliases[0].Attribute("Alias") != "EvmWord" ||
                (string?)aliases[0].Attribute("Include") != "System.Runtime.Intrinsics.Vector256<byte>" ||
                (string?)aliases[0].Parent?.Attribute("Condition") != "!$(TargetFramework.StartsWith('netstandard'))")
                throw new ExtractionException("fold.compiler.global-alias: standard EvmWord alias changed.");
            sourceText = "global using EvmWord = System.Runtime.Intrinsics.Vector256<byte>;";
        }
        SyntaxTree tree = CSharpSyntaxTree.ParseText(sourceText, ParseOptions, relativePath);
        CompilationUnitSyntax syntax = (CompilationUnitSyntax)tree.GetRoot();
        Diagnostic[] syntaxErrors = syntax.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (syntaxErrors.Length != 0)
        {
            throw new ExtractionException($"Source '{relativePath}' has syntax errors: {string.Join("; ", syntaxErrors.Select(static error => error.ToString()))}");
        }

        string sourceHash = Sha256(bytes);
        string syntaxHash = Sha256(Encoding.UTF8.GetBytes(Canonical(syntax)));
        if (enforcePins && !string.Equals(sourceHash, pin.Sha256, StringComparison.Ordinal))
        {
            throw new ExtractionException($"Source pin changed for '{relativePath}': expected {pin.Sha256}, got {sourceHash}.");
        }

        return new(relativePath, pin.Role, fullSourcePath, bytes, sourceHash, syntaxHash, syntax, tree);
    }

    internal static CompilerReferenceIdentity[] ReadCompilerReferenceIdentities(string root)
    {
        CompilerReferenceInventory inventory = ReadCompilerReferenceInventory(root);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string trustedAssemblies ||
            string.IsNullOrWhiteSpace(trustedAssemblies))
        {
            throw new ExtractionException("The trusted platform assembly list is unavailable for typed Roslyn binding.");
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
            throw new ExtractionException("The trusted platform assembly list has no runtime-platform entries for the compiler closure.");
        }

        string[] closureDirectories = CompilerReferenceClosurePaths
            .Select(relativePath =>
            {
                string directory = Path.GetFullPath(Path.Combine(root, relativePath));
                EnsureWithin(root, directory);
                if (!Directory.Exists(directory))
                {
                    throw new ExtractionException($"The typed Roslyn compiler closure is missing '{relativePath}'.");
                }

                return directory;
            })
            .ToArray();

        ValidateReferenceInventoryPaths(inventory, root, platformPaths, closureDirectories);
        ValidateCompilerReferenceIdentities(inventory.References);

        foreach (CompilerReferenceIdentity identity in inventory.References)
        {
            string fullPath = ResolveCompilerReferencePath(root, canonicalPlatformDirectory, identity.Path);
            if (!File.Exists(fullPath))
            {
                throw new ExtractionException(
                    $"The compiler reference inventory member does not exist: {identity.Path}.");
            }

            string actualSha;
            try
            {
                actualSha = Sha256(File.ReadAllBytes(fullPath));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new ExtractionException(
                    $"The compiler reference inventory member '{identity.Path}' could not be hashed: {exception.Message}");
            }
            if (!string.Equals(actualSha, identity.Sha256, StringComparison.Ordinal))
            {
                throw new ExtractionException(
                    $"The compiler reference inventory detected byte drift in '{identity.Path}': expected {identity.Sha256}, got {actualSha}.");
            }

            AssemblyName assemblyName;
            try
            {
                assemblyName = AssemblyName.GetAssemblyName(fullPath);
            }
            catch (Exception exception) when (exception is BadImageFormatException or FileLoadException or IOException)
            {
                throw new ExtractionException(
                    $"The compiler reference inventory member '{identity.Path}' has no readable assembly metadata: {exception.Message}");
            }
            using FileStream metadataStream = File.OpenRead(fullPath);
            using PEReader reader = new(metadataStream);
            MetadataReader metadata = reader.GetMetadataReader();
            if (metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToString("D") != identity.Mvid)
                throw new ExtractionException($"fold.compiler.mvid: {identity.Path} changed.");
            if (!string.Equals(assemblyName.Name, identity.AssemblyName, StringComparison.Ordinal))
            {
                throw new ExtractionException(
                    $"The compiler reference inventory member '{identity.Path}' has assembly identity '{assemblyName.FullName}', expected '{identity.AssemblyName}'.");
            }
        }

        return inventory.References;
    }

    private static CompilerReferenceInventory ReadCompilerReferenceInventory(string root)
    {
        string path = Path.GetFullPath(Path.Combine(root, CompilerReferenceInventoryPath));
        EnsureWithin(root, path);
        if (!File.Exists(path))
        {
            throw new ExtractionException(
                $"The checked-in compiler reference inventory does not exist: {CompilerReferenceInventoryPath}.");
        }

        byte[] bytes = File.ReadAllBytes(path);
        if (!string.Equals(Sha256(bytes), CompilerReferenceInventorySha256, StringComparison.Ordinal))
        {
            throw new ExtractionException(
                $"The checked-in compiler reference inventory bytes changed: expected {CompilerReferenceInventorySha256}.");
        }

        CompilerReferenceInventory inventory;
        try
        {
            inventory = Deserialize<CompilerReferenceInventory>(bytes);
        }
        catch (ExtractionException exception)
        {
            throw new ExtractionException($"The checked-in compiler reference inventory is invalid: {exception.Message}");
        }

        if (inventory.SchemaVersion != CompilerReferenceInventorySchemaVersion ||
            inventory.Count != CompilerReferenceInventoryCount ||
            !string.Equals(inventory.AggregateSha256, CompilerReferenceInventoryAggregateSha256, StringComparison.Ordinal) ||
            inventory.References is null || inventory.References.Any(static reference => reference is null))
        {
            throw new ExtractionException("The checked-in compiler reference inventory header changed.");
        }

        ValidateCompilerReferenceIdentities(inventory.References);
        if (inventory.Count != inventory.References.Length ||
            !string.Equals(inventory.AggregateSha256, CompilerReferenceAggregateSha256(inventory.References), StringComparison.Ordinal) ||
            !Serialize(inventory).AsSpan().SequenceEqual(bytes))
        {
            throw new ExtractionException("The checked-in compiler reference inventory count, aggregate, or canonical bytes changed.");
        }

        return inventory;
    }

    private static void ValidateReferenceInventoryPaths(CompilerReferenceInventory inventory, string root,
        string[] platformPaths, string[] closureDirectories)
    {
        try
        {
            Receipt.ReceiptTerminalFoldProfile.ValidateReferenceInventoryPaths(new(inventory.SchemaVersion, inventory.Count,
                inventory.AggregateSha256, inventory.References.Select(ToReceiptReference).ToArray()), root, platformPaths, closureDirectories);
        }
        catch (Receipt.ExtractionException exception)
        {
            throw new ExtractionException($"fold.compiler.paths: {exception.Message}");
        }
    }

    private static Receipt.CompilerReferenceIdentity ToReceiptReference(CompilerReferenceIdentity reference) =>
        new(reference.Path, reference.AssemblyName, reference.Sha256, reference.Mvid, reference.Selected);

    private static void ValidateCompilerReferenceIdentities(CompilerReferenceIdentity[] references)
    {
        if (references is null || references.Length == 0)
        {
            throw new ExtractionException("The sequential transaction-fold compiler/reference closure is empty.");
        }
        if (references.Any(static reference => reference is null))
        {
            throw new ExtractionException("The sequential transaction-fold compiler/reference closure contains a null identity.");
        }

        try
        {
            Receipt.ReceiptTerminalFoldProfile.ValidateCompilerReferenceIdentities(references.Select(ToReceiptReference).ToArray());
        }
        catch (Receipt.ExtractionException exception)
        {
            throw new ExtractionException($"fold.compiler.identity: {exception.Message}");
        }
        if (references.Length != CompilerReferenceInventoryCount ||
            CompilerReferenceAggregateSha256(references) != CompilerReferenceInventoryAggregateSha256)
        {
            throw new ExtractionException("fold.compiler.inventory: count or aggregate changed.");
        }
    }

    private static void ValidateCompilerReferenceClosureIdentity(CompilerReferenceClosureIdentity closure)
    {
        if (closure is null || !string.Equals(closure.InventoryPath, CompilerReferenceInventoryPath, StringComparison.Ordinal) ||
            !string.Equals(closure.InventorySha256, CompilerReferenceInventorySha256, StringComparison.Ordinal) ||
            closure.Count != CompilerReferenceInventoryCount ||
            !string.Equals(closure.AggregateSha256, CompilerReferenceInventoryAggregateSha256, StringComparison.Ordinal))
        {
            throw new ExtractionException("The sequential transaction-fold compiler/reference closure identity changed.");
        }
    }

    private static bool CompilerReferenceClosureMatches(CompilerReferenceClosureIdentity closure)
    {
        try
        {
            ValidateCompilerReferenceClosureIdentity(closure);
            return true;
        }
        catch (ExtractionException)
        {
            return false;
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

    private static string CompilerReferenceAggregateSha256(IEnumerable<CompilerReferenceIdentity> references) =>
        Sha256(Encoding.UTF8.GetBytes(string.Join('\n', references.Select(static reference =>
            $"{reference.Path}\0{reference.AssemblyName}\0{reference.Sha256}\0{reference.Mvid}\0{reference.Selected}")) + "\n"));

    private static (MethodDeclarationSyntax ProcessTransactions, MethodDeclarationSyntax ProcessTransaction) FindExecutorMethods(SourceFile source)
    {
        ClassDeclarationSyntax executor = source.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(static type => type.Identifier.ValueText == "BlockValidationTransactionsExecutor")
            .SingleOrDefault() ?? throw new ExtractionException("BlockValidationTransactionsExecutor declaration is missing or ambiguous.");
        MethodDeclarationSyntax processTransactions = FindMethod(source, executor, "ProcessTransactions", 4);
        MethodDeclarationSyntax processTransaction = FindMethod(source, executor, "ProcessTransaction", 5);
        return (processTransactions, processTransaction);
    }

    private static MethodDeclarationSyntax FindMethod(SourceFile source, string owner, string name, int parameterCount) =>
        FindMethod(
            source,
            source.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(type => type.Identifier.ValueText == owner)
                .SingleOrDefault() ?? throw new ExtractionException($"{owner} declaration is missing or ambiguous."),
            name,
            parameterCount);

    private static MethodDeclarationSyntax FindMethod(SourceFile source, ClassDeclarationSyntax owner, string name, int parameterCount)
    {
        MethodDeclarationSyntax[] methods = owner.Members.OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == name && method.ParameterList.Parameters.Count == parameterCount)
            .ToArray();
        if (methods.Length != 1)
        {
            throw new ExtractionException($"{source.RelativePath}: {owner.Identifier.ValueText}.{name}/{parameterCount} is missing or ambiguous.");
        }

        return methods[0];
    }

    private static MemberIdentity BindMethod(
        string id,
        SourceFile source,
        MethodDeclarationSyntax method,
        SemanticModel model,
        Compilation compilation)
    {
        ValidateDeclaredOwner(method, model, MainOwner(id), id);
        IMethodSymbol symbol = model.GetDeclaredSymbol(method)
            ?? throw new ExtractionException($"No declared symbol for {OwnerPath(method)}.{method.Identifier.ValueText}.");
        IOperation operation = model.GetOperation(method)
            ?? throw new ExtractionException($"No IOperation for {OwnerPath(method)}.{method.Identifier.ValueText}.");
        if (operation is IInvalidOperation || HasErrorSymbol(symbol))
        {
            throw new ExtractionException($"Invalid typed operation for {OwnerPath(method)}.{method.Identifier.ValueText}.");
        }

        TypedBinding binding = BindNode(id, source, method, method, model, compilation, null, operation);
        return new(
            id,
            source.RelativePath,
            OwnerPath(method),
            method.Identifier.ValueText,
            method.ParameterList.Parameters.Count,
            CanonicalSymbol(symbol),
            binding);
    }

    private static List<AnchorIdentity> BuildAnchors(
        SourceFile[] sources,
        SemanticModel executorModel,
        SemanticModel blockModel,
        Compilation compilation,
        (MethodDeclarationSyntax ProcessTransactions, MethodDeclarationSyntax ProcessTransaction) executorMethods,
        MethodDeclarationSyntax processBlock,
        int transactionsExecutedEvaluationBlock,
        List<SynchronousCallableIdentity> synchronousCallables,
        out TransactionProjectionIdentity transactionProjection)
    {
        SourceFile executor = FindSource(sources, ExecutorPath);
        SourceFile block = FindSource(sources, BlockProcessorPath);
        List<AnchorIdentity> anchors = [];

        InvocationExpressionSyntax setupMetrics = SingleInvocation(executorMethods.ProcessTransactions,
            "SetupTxTimingMetrics(block)", "executor.metrics-setup");
        anchors.Add(BindAnchor("executor.metrics-setup", executor, executorMethods.ProcessTransactions, setupMetrics,
            executorModel, compilation, "metrics setup precedes the loop"));

        VariableDeclaratorSyntax validationLocal = executorMethods.ProcessTransactions.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(variable => variable.Identifier.ValueText == "shouldValidate")
            .SingleOrDefault() ?? throw new ExtractionException("ProcessTransactions.shouldValidate initializer is missing or ambiguous.");
        anchors.Add(BindAnchor("executor.validation-mode", executor, executorMethods.ProcessTransactions, validationLocal,
            executorModel, compilation, "NoValidation controls the gas-limit guard"));

        ForStatementSyntax transactionLoop = executorMethods.ProcessTransactions.DescendantNodes()
            .OfType<ForStatementSyntax>()
            .SingleOrDefault() ?? throw new ExtractionException("ProcessTransactions transaction loop is missing or ambiguous.");
        EnsureDeclaredMethodOwner(transactionLoop, executorMethods.ProcessTransactions, "executor.transaction-loop");
        if (!ReferenceEquals(transactionLoop.Parent, executorMethods.ProcessTransactions.Body))
            throw new ExtractionException("fold.iteration: transaction loop must be a direct ProcessTransactions statement.");
        if (setupMetrics.SpanStart >= transactionLoop.SpanStart || validationLocal.SpanStart >= transactionLoop.SpanStart)
        {
            throw new ExtractionException("ProcessTransactions setup and validation must precede the transaction loop.");
        }

        VariableDeclaratorSyntax? loopVariable = transactionLoop.Declaration?.Variables.Count == 1
            ? transactionLoop.Declaration.Variables[0]
            : null;
        if (transactionLoop.Declaration is null || transactionLoop.Declaration.Type is null ||
            Canonical(transactionLoop.Declaration.Type) != "int" || transactionLoop.Declaration.Variables.Count != 1 ||
            loopVariable is null || loopVariable.Identifier.ValueText != "i" ||
            loopVariable.Initializer is null || Canonical(loopVariable.Initializer.Value) != "0" ||
            transactionLoop.Condition is null || Canonical(transactionLoop.Condition) != "i<block.Transactions.Length" ||
            transactionLoop.Incrementors.Count != 1 || Canonical(transactionLoop.Incrementors[0]) != "i++")
        {
            throw new ExtractionException("ProcessTransactions must retain the indexed block transaction loop shape.");
        }

        VariableDeclaratorSyntax currentTransaction = transactionLoop.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(variable => variable.Identifier.ValueText == "currentTx")
            .SingleOrDefault() ?? throw new ExtractionException("ProcessTransactions current transaction projection is missing or ambiguous.");
        if (currentTransaction.Initializer is null ||
            Canonical(currentTransaction.Initializer.Value) != "block.Transactions[i]")
        {
            throw new ExtractionException("ProcessTransactions must project the indexed block transaction before the helper call.");
        }

        InvocationExpressionSyntax processTransaction = SingleInvocation(executorMethods.ProcessTransactions,
            "ProcessTransaction(block,currentTx,i,receiptsTracer,processingOptions)", "executor.process-transaction-call");
        if (!processTransaction.Ancestors().Contains(transactionLoop))
        {
            throw new ExtractionException("ProcessTransactions must invoke ProcessTransaction inside its source-order loop.");
        }
        EnsureDeclaredMethodOwner(processTransaction, executorMethods.ProcessTransactions, "executor.process-transaction-call");
        transactionProjection = BindTransactionProjection(executorModel, compilation, currentTransaction,
            loopVariable, processTransaction);
        anchors.Add(BindAnchor("executor.process-transaction-call", executor, executorMethods.ProcessTransactions, processTransaction,
            executorModel, compilation, "loop invokes one transaction in source order"));

        IfStatementSyntax gasLimitGuard = executorMethods.ProcessTransactions.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .Where(statement => Canonical(statement.Condition) == "shouldValidate&&block.Header.GasUsed>block.Header.GasLimit")
            .SingleOrDefault() ?? throw new ExtractionException("ProcessTransactions gas-limit guard is missing or ambiguous.");
        if (!gasLimitGuard.Ancestors().Contains(transactionLoop) || gasLimitGuard.SpanStart <= processTransaction.SpanStart)
        {
            throw new ExtractionException("ProcessTransactions gas-limit guard must follow the transaction call inside its loop.");
        }
        anchors.Add(BindAnchor("executor.gas-limit-guard", executor, executorMethods.ProcessTransactions, gasLimitGuard.Condition,
            executorModel, compilation, "successful prefix can still reject the block after a receipt"));
        InvocationExpressionSyntax gasLimitThrow = FindDirectGuardInvocation(
            gasLimitGuard,
            "ThrowInvalidBlockForGasLimit(block)",
            "executor.gas-limit-throw");
        EnsureDeclaredMethodOwner(gasLimitThrow, executorMethods.ProcessTransactions, "executor.gas-limit-throw");
        synchronousCallables.Add(BindDirectThrowHelper(executorModel, executorMethods.ProcessTransactions, gasLimitThrow,
            "executor.gas-limit-helper", local: true,
            "newInvalidBlockException(block,Core.Messages.BlockErrorMessages.ExceededGasLimit)",
            "M:Nethermind.Core.Exceptions.InvalidBlockException.#ctor(Nethermind.Core.Block,System.String,System.Exception)"));
        ControlFlowGraph processTransactionsGraph = TryCreateGraph(executorModel, executorMethods.ProcessTransactions)
            ?? throw new ExtractionException("ProcessTransactions has no Roslyn control-flow graph.");
        TypedBinding gasLimitThrowBinding = BindNode(
            "executor.gas-limit-throw",
            executor,
            executorMethods.ProcessTransactions,
            gasLimitThrow,
            executorModel,
            compilation,
            processTransactionsGraph,
            executorModel.GetOperation(gasLimitThrow) ??
            throw new ExtractionException("The gas-limit throw has no typed operation."));
        ValidateBinding(gasLimitThrowBinding);
        if (!gasLimitThrowBinding.IsReachable ||
            !Dominates(processTransactionsGraph,
                FindControlFlowBlock(processTransactionsGraph, processTransaction.SpanStart),
                FindControlFlowBlock(processTransactionsGraph, gasLimitGuard.Condition.SpanStart),
                normalOnly: true) ||
            !Dominates(processTransactionsGraph,
                FindControlFlowBlock(processTransactionsGraph, gasLimitGuard.Condition.SpanStart),
                gasLimitThrowBinding.ControlFlowBlock,
                normalOnly: true))
        {
            throw new ExtractionException("The gas-limit throw must remain in the reachable direct guard body.");
        }
        if (gasLimitGuard.Else is not null)
            throw new ExtractionException("fold.iteration: gas-limit guard must not have an else arm.");
        if (transactionLoop.Statement is not BlockSyntax { Statements.Count: 3 } iteration ||
            iteration.Statements[0] is not LocalDeclarationStatementSyntax projection ||
            projection.Modifiers.Count != 0 || projection.UsingKeyword.RawKind != 0 || projection.AwaitKeyword.RawKind != 0 ||
            Canonical(projection.Declaration.Type) != "Transaction" || projection.Declaration.Variables.Count != 1 ||
            !ReferenceEquals(projection.Declaration.Variables[0], currentTransaction) ||
            iteration.Statements[1] is not ExpressionStatementSyntax invocation ||
            !ReferenceEquals(invocation.Expression, processTransaction) ||
            !ReferenceEquals(iteration.Statements[2], gasLimitGuard))
            throw new ExtractionException("fold.iteration: body must contain only the ordered transaction projection, helper call, and gas-limit guard.");

        InvocationExpressionSyntax invalidTransactionGuard = executorMethods.ProcessTransaction.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => Canonical(invocation) == "ThrowInvalidTransactionException(result,block.Header,currentTx,index)")
            .SingleOrDefault() ?? throw new ExtractionException("ProcessTransaction invalid-result throw is missing or ambiguous.");
        EnsureDeclaredMethodOwner(invalidTransactionGuard, executorMethods.ProcessTransaction, "executor.invalid-result-throw");
        synchronousCallables.Add(BindDirectThrowHelper(executorModel, executorMethods.ProcessTransaction, invalidTransactionGuard,
            "executor.invalid-result-helper", local: false,
            "newInvalidTransactionException(header,$\"Transaction {currentTx.Hash} at index {index} failed with error {result.ErrorDescription}\",result)",
            "M:Nethermind.Blockchain.InvalidTransactionException.#ctor(Nethermind.Core.BlockHeader,System.String,Nethermind.Evm.TransactionProcessing.TransactionResult,System.Exception)"));
        IfStatementSyntax invalidResultIf = invalidTransactionGuard.Ancestors().OfType<IfStatementSyntax>()
            .SingleOrDefault(statement => Canonical(statement.Condition) == "!result")
            ?? throw new ExtractionException("ProcessTransaction must guard the invalid-result throw with '!result'.");
        if (!IsDirectGuardStatement(invalidResultIf, invalidTransactionGuard))
        {
            throw new ExtractionException("ProcessTransaction invalid-result throw must be the direct '!result' guard body.");
        }
        anchors.Add(BindAnchor("executor.invalid-result-throw", executor, executorMethods.ProcessTransaction, invalidTransactionGuard,
            executorModel, compilation, "false TransactionResult stops the prefix before the event"));

        InvocationExpressionSyntax adapterCall = SingleInvocation(executorMethods.ProcessTransaction,
            "transactionProcessor.ProcessTransaction(currentTx,receiptsTracer,processingOptions,_stateProvider)",
            "executor.transaction-adapter-call");
        anchors.Add(BindAnchor("executor.transaction-adapter-call", executor, executorMethods.ProcessTransaction, adapterCall,
            executorModel, compilation, "ordinary transaction execution is an external settled observation"));

        InvocationExpressionSyntax processedEvent = executorMethods.ProcessTransaction.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => Canonical(invocation).StartsWith(
                "_transactionProcessedEventHandler?.OnTransactionProcessed(newTxProcessedEventArgs(", StringComparison.Ordinal))
            .SingleOrDefault() ?? throw new ExtractionException("ProcessTransaction processed-event call is missing or ambiguous.");
        if (adapterCall.SpanStart >= invalidTransactionGuard.SpanStart ||
            invalidTransactionGuard.SpanStart >= processedEvent.SpanStart)
        {
            throw new ExtractionException("ProcessTransaction must order adapter, invalid-result guard, and processed event.");
        }
        ControlFlowGraph processTransactionGraph = TryCreateGraph(executorModel, executorMethods.ProcessTransaction)
            ?? throw new ExtractionException("ProcessTransaction has no Roslyn control-flow graph.");
        RequireDominance(
            processTransactionGraph,
            FindControlFlowBlock(processTransactionGraph, invalidResultIf.Condition.Span.End - 1),
            FindControlFlowBlock(processTransactionGraph, processedEvent.SpanStart),
            "ProcessTransaction invalid-result guard must dominate the processed event on the normal path.");
        anchors.Add(BindAnchor("executor.processed-event", executor, executorMethods.ProcessTransaction, processedEvent,
            executorModel, compilation, "event observes the receipt at the transaction index"));

        InvocationExpressionSyntax blockTransactionCall = SingleInvocation(processBlock,
            "_blockTransactionsExecutor.ProcessTransactions(block,options,ReceiptsTracer,token)",
            "block.transaction-fold-call");
        anchors.Add(BindAnchor("block.transaction-fold-call", block, processBlock, blockTransactionCall,
            blockModel, compilation, "direct-inner caller enters the transaction executor; BAL is disabled by an explicit model premise"));

        InvocationExpressionSyntax[] commits = processBlock.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => Canonical(invocation) == "CommitState(spec)")
            .OrderBy(static invocation => invocation.SpanStart)
            .ToArray();
        InvocationExpressionSyntax[] preCommits = commits
            .Where(commit => commit.SpanStart < blockTransactionCall.SpanStart)
            .ToArray();
        InvocationExpressionSyntax[] postCommits = commits
            .Where(commit => commit.SpanStart > blockTransactionCall.SpanStart)
            .ToArray();
        if (commits.Length != 3 || preCommits.Length != 1 || postCommits.Length != 2)
        {
            throw new ExtractionException("ProcessBlock must retain exactly one pre-fold and two post-fold CommitState(spec) calls.");
        }

        anchors.Add(BindAnchor("block.pre-transaction-commit", block, processBlock, preCommits[^1], blockModel, compilation,
            "pre-transaction journal boundary"));
        anchors.Add(BindAnchor("block.post-transaction-commit", block, processBlock, postCommits[0], blockModel, compilation,
            "post-transaction journal boundary"));

        InvocationExpressionSyntax transactionsExecuted = SingleInvocation(
            processBlock, "TransactionsExecuted?.Invoke()", "block.transactions-executed-signal");
        if (blockTransactionCall.SpanStart >= transactionsExecuted.SpanStart ||
            transactionsExecuted.SpanStart >= postCommits[0].SpanStart)
        {
            throw new ExtractionException(
                "ProcessBlock must signal TransactionsExecuted after a normal transaction fold return and before the post-fold commit.");
        }
        ControlFlowGraph processBlockGraph = TryCreateGraph(blockModel, processBlock)
            ?? throw new ExtractionException("ProcessBlock has no Roslyn control-flow graph.");
        RequireDominance(
            processBlockGraph,
            FindControlFlowBlock(processBlockGraph, blockTransactionCall.SpanStart),
            transactionsExecutedEvaluationBlock,
            "TransactionsExecuted must remain on the normal path after the transaction fold call.");
        RequireDominance(
            processBlockGraph,
            transactionsExecutedEvaluationBlock,
            FindControlFlowBlock(processBlockGraph, postCommits[0].SpanStart),
            "The first post-fold CommitState(spec) must remain on the normal path after TransactionsExecuted.");
        anchors.Add(BindAnchor("block.transactions-executed-signal", block, processBlock, transactionsExecuted,
            blockModel, compilation, "normal executor return precedes the TransactionsExecuted signal"));
        return anchors;
    }

    private static List<AuxiliaryAnchorIdentity> BuildAuxiliaryAnchors(
        SourceFile[] sources,
        SourceFile[] auxiliarySources,
        CompilerContext compiler,
        (MethodDeclarationSyntax ProcessTransactions, MethodDeclarationSyntax ProcessTransaction) executorMethods,
        MethodDeclarationSyntax processBlock,
        int transactionsExecutedEvaluationBlock)
    {
        SourceFile block = FindSource(sources, BlockProcessorPath);
        SourceFile adapter = FindSource(auxiliarySources, TransactionProcessorAdapterPath);
        SourceFile tracer = FindSource(auxiliarySources, BlockReceiptsTracerPath);
        SourceFile bal = FindSource(auxiliarySources, BlockAccessListManagerPath);
        SourceFile parallel = FindSource(auxiliarySources, ParallelExecutorPath);
        SourceFile module = FindSource(auxiliarySources, BlockProcessingModulePath);

        Dictionary<string, SemanticModel> models = new(StringComparer.Ordinal)
        {
            [BlockProcessorPath] = compiler.Compilation.GetSemanticModel(block.Tree, ignoreAccessibility: true),
        };
        foreach (SourceFile source in auxiliarySources)
        {
            models.Add(source.RelativePath, compiler.Models[source.RelativePath]);
        }

        MethodDeclarationSyntax adapterMethod = FindMethod(adapter, "TransactionProcessorAdapterExtensions", "ProcessTransaction", 5);
        MethodDeclarationSyntax blockTraceStart = FindMethod(tracer, "BlockReceiptsTracer", "StartNewBlockTrace", 1);
        MethodDeclarationSyntax markSuccess = FindMethod(tracer, "BlockReceiptsTracer", "MarkAsSuccess", 5);
        MethodDeclarationSyntax markFailed = FindMethod(tracer, "BlockReceiptsTracer", "MarkAsFailed", 5);
        MethodDeclarationSyntax buildReceipt = FindMethod(tracer, "BlockReceiptsTracer", "BuildReceipt", 5);
        MethodDeclarationSyntax txTraceStart = FindMethod(tracer, "BlockReceiptsTracer", "StartNewTxTrace", 1);
        MethodDeclarationSyntax txTraceEnd = FindMethod(tracer, "BlockReceiptsTracer", "EndTxTrace", 0);
        MethodDeclarationSyntax balPrepare = FindMethod(bal, "BlockAccessListManager", "PrepareForProcessing", 3);
        MethodDeclarationSyntax parallelProcess = FindMethod(
            parallel,
            "ParallelBlockValidationTransactionsExecutor",
            "ProcessTransactions",
            4);
        MethodDeclarationSyntax moduleLoad = FindMethod(module, "StandardBlockValidationModule", "Load", 1);
        MethodDeclarationSyntax rootModuleLoad = FindMethod(module, "BlockProcessingModule", "Load", 1);

        Dictionary<string, SyntaxNode> nodes = new(StringComparer.Ordinal);
        List<AuxiliaryAnchorIdentity> anchors = [];
        foreach (AuxiliaryAnchorExpectation expectation in ExpectedAuxiliaryAnchors)
        {
            SyntaxNode owner;
            MethodDeclarationSyntax method;
            SourceFile source;
            switch (expectation.Id)
            {
                case "block.receipts-tracer-start":
                    source = block;
                    method = processBlock;
                    owner = processBlock;
                    break;
                case "adapter.tx-trace-start":
                case "adapter.execute":
                case "adapter.tx-trace-end":
                    source = adapter;
                    method = adapterMethod;
                    owner = adapterMethod;
                    break;
                case "tracer.reset-index":
                case "tracer.reset-receipts":
                case "tracer.reset-block-gas":
                case "tracer.reset-receipt-gas":
                    source = tracer;
                    method = blockTraceStart;
                    owner = blockTraceStart;
                    break;
                case "tracer.success-append":
                    source = tracer;
                    method = markSuccess;
                    owner = markSuccess;
                    break;
                case "tracer.failure-append":
                    source = tracer;
                    method = markFailed;
                    owner = markFailed;
                    break;
                case "tracer.receipt-index":
                    source = tracer;
                    method = buildReceipt;
                    owner = buildReceipt;
                    break;
                case "tracer.tx-start-current":
                case "tracer.tx-start-delegate":
                    source = tracer;
                    method = txTraceStart;
                    owner = txTraceStart;
                    break;
                case "tracer.tx-end-delegate":
                case "tracer.tx-end-index":
                    source = tracer;
                    method = txTraceEnd;
                    owner = txTraceEnd;
                    break;
                case "bal.enabled-spec":
                case "bal.enabled-derived":
                    source = bal;
                    method = balPrepare;
                    owner = balPrepare;
                    break;
                case "parallel.bal-disabled-gate":
                case "parallel.inner-sequential":
                case "parallel.selection":
                    source = parallel;
                    method = parallelProcess;
                    owner = parallelProcess;
                    break;
                case "di.base-executor":
                case "di.parallel-decorator":
                    source = module;
                    method = moduleLoad;
                    owner = moduleLoad;
                    break;
                case "di.bal-manager":
                    source = module;
                    method = rootModuleLoad;
                    owner = rootModuleLoad;
                    break;
                default:
                    throw new ExtractionException($"Unknown auxiliary anchor expectation '{expectation.Id}'.");
            }

            SemanticModel model = models[source.RelativePath];
            SyntaxNode node = FindAuxiliaryNode(
                owner, expectation.CanonicalFragment, expectation.Id, expectation.RequireInvocation);
            EnsureDeclaredMethodOwner(node, method, expectation.Id);
            nodes.Add(expectation.Id, node);
            ControlFlowGraph graph = TryCreateGraph(model, method)
                ?? throw new ExtractionException($"No Roslyn control-flow graph for auxiliary anchor '{expectation.Id}'.");
            anchors.Add(BindAuxiliaryAnchor(expectation, source, method, node, model, graph));
        }

        RequireSourceOrder(nodes,
            "adapter.tx-trace-start", "adapter.execute", "adapter.tx-trace-end",
            "TransactionProcessorAdapterExtensions must order StartNewTxTrace, Execute, and EndTxTrace.");
        RequireSourceOrder(nodes,
            "tracer.tx-start-current", "tracer.tx-start-delegate",
            "BlockReceiptsTracer.StartNewTxTrace must record CurrentTx before delegating.");
        RequireSourceOrder(nodes,
            "tracer.tx-end-delegate", "tracer.tx-end-index",
            "BlockReceiptsTracer.EndTxTrace must delegate before advancing currentIndex.");
        RequireSourceOrder(nodes,
            "tracer.reset-index", "tracer.reset-receipts", "tracer.reset-block-gas", "tracer.reset-receipt-gas",
            "BlockReceiptsTracer.StartNewBlockTrace reset order changed.");
        RequireSourceOrder(nodes,
            "tracer.success-append", "tracer.failure-append",
            "MarkAsSuccess and MarkAsFailed must retain distinct receipt append anchors.", allowDifferentMethods: true);
        RequireSourceOrder(nodes,
            "parallel.bal-disabled-gate", "parallel.inner-sequential", "parallel.selection",
            "BAL decorator guard and dispatch order changed.");
        RequireSourceOrder(nodes,
            "di.base-executor", "di.parallel-decorator",
            "standard validation must register the base executor before its decorator.");

        RequireAuxiliaryControlFlowRelations(anchors, nodes, models, adapterMethod, blockTraceStart, markSuccess,
            markFailed, buildReceipt, txTraceStart, txTraceEnd, parallelProcess, processBlock,
            transactionsExecutedEvaluationBlock);

        if (nodes["block.receipts-tracer-start"].SpanStart >=
            processBlock.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(invocation => Canonical(invocation) == "CommitState(spec)")
                .OrderBy(static invocation => invocation.SpanStart)
                .First().SpanStart)
        {
            throw new ExtractionException("ProcessBlock must reset ReceiptsTracer before the pre-transaction commit.");
        }

        IfStatementSyntax balGuard = parallelProcess.DescendantNodes().OfType<IfStatementSyntax>()
            .SingleOrDefault(statement => Canonical(statement.Condition) == "!balManager.Enabled")
            ?? throw new ExtractionException("The BAL decorator's disabled guard is missing or ambiguous.");
        InvocationExpressionSyntax innerCall = parallelProcess.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .SingleOrDefault(invocation => Canonical(invocation) ==
                "inner.ProcessTransactions(block,processingOptions,receiptsTracer,token)")
            ?? throw new ExtractionException("The BAL decorator's sequential inner call is missing or ambiguous.");
        if (!innerCall.Ancestors().Contains(balGuard) || innerCall.SpanStart <= balGuard.SpanStart ||
            !IsDirectReturnInTrueArm(balGuard, innerCall))
        {
            throw new ExtractionException("The BAL-disabled decorator branch must return the inner executor from the guard's true arm.");
        }

        return anchors;
    }

    private static SourceRouteIdentity BuildSourceRoute(
        SourceFile[] sources,
        SourceFile[] auxiliarySources,
        CompilerContext compiler,
        MethodDeclarationSyntax processTransactions)
    {
        SourceFile executor = FindSource(sources, ExecutorPath);
        SemanticModel model = compiler.Compilation.GetSemanticModel(executor.Tree, ignoreAccessibility: true);
        IMethodSymbol symbol = model.GetDeclaredSymbol(processTransactions)
            ?? throw new ExtractionException("The exact-base ProcessTransactions method has no declared symbol.");
        if (processTransactions.Modifiers.Any(static modifier =>
                modifier.IsKind(SyntaxKind.VirtualKeyword) ||
                modifier.IsKind(SyntaxKind.OverrideKeyword) ||
                modifier.IsKind(SyntaxKind.AbstractKeyword)) ||
            symbol.IsVirtual || symbol.IsOverride || symbol.IsAbstract)
        {
            throw new ExtractionException("The admitted BlockValidationTransactionsExecutor.ProcessTransactions route is virtual or abstract.");
        }
        string signature = CanonicalSymbol(symbol);
        if (!string.Equals(signature, ExactExecutorSignature, StringComparison.Ordinal))
        {
            throw new ExtractionException("The exact-base ProcessTransactions signature changed.");
        }

        return new(
            ExecutorPath,
            "BlockProcessor.BlockValidationTransactionsExecutor",
            "ProcessTransactions",
            processTransactions.ParameterList.Parameters.Count,
            signature,
            true,
            BlockAccessListManagerPath,
            "BlockAccessListManager.Enabled",
            ParallelExecutorPath,
            "IBlockProcessor.IBlockTransactionsExecutor -> BlockProcessor.BlockValidationTransactionsExecutor",
            "IBlockProcessor.IBlockTransactionsExecutor -> BlockProcessor.ParallelBlockValidationTransactionsExecutor");
    }

    private static SyntaxNode FindAuxiliaryNode(
        SyntaxNode owner, string fragment, string id, bool requireInvocation)
    {
        if (requireInvocation)
        {
            SyntaxNode[] invocations = owner.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .Where(node => id.StartsWith("di.", StringComparison.Ordinal)
                    ? node.Expression is MemberAccessExpressionSyntax access && ContainsCanonicalFragment(
                        Canonical(access.Name) + Canonical(node.ArgumentList), fragment)
                    : Canonical(node) == fragment || ContainsCanonicalFragment(Canonical(node), fragment))
                .ToArray();
            if (invocations.Length == 1)
            {
                return invocations[0];
            }

            throw new ExtractionException(
                $"Auxiliary anchor '{id}' expected one typed invocation containing '{fragment}', found {invocations.Length}.");
        }

        SyntaxNode[] exact = owner.DescendantNodesAndSelf()
            .Where(node => Canonical(node) == fragment)
            .ToArray();
        if (exact.Length == 1)
        {
            return exact[0];
        }

        SyntaxNode[] containing = owner.DescendantNodesAndSelf()
            .Where(node => node is InvocationExpressionSyntax &&
                ContainsCanonicalFragment(Canonical(node), fragment))
            .ToArray();
        if (containing.Length == 1)
        {
            return containing[0];
        }

        throw new ExtractionException(
            $"Auxiliary anchor '{id}' expected one canonical node or invocation containing '{fragment}', found {exact.Length + containing.Length}.");
    }

    private static bool ContainsCanonicalFragment(string canonical, string fragment)
    {
        if (fragment.Length == 0)
        {
            return false;
        }

        int searchStart = 0;
        while (searchStart < canonical.Length)
        {
            int position = canonical.IndexOf(fragment, searchStart, StringComparison.Ordinal);
            if (position < 0)
            {
                return false;
            }

            int end = position + fragment.Length;
            bool leftBoundary = position == 0 || !IsIdentifierCharacter(canonical[position - 1]);
            bool rightBoundary = end == canonical.Length || !IsIdentifierCharacter(canonical[end]);
            if (leftBoundary && rightBoundary)
            {
                return true;
            }

            searchStart = end;
        }

        return false;
    }

    private static bool IsIdentifierCharacter(char value) =>
        char.IsLetterOrDigit(value) || value == '_';

    private static void EnsureDeclaredMethodOwner(SyntaxNode node, MethodDeclarationSyntax method, string id)
    {
        if (node.AncestorsAndSelf().Any(static ancestor =>
                ancestor is LocalFunctionStatementSyntax || ancestor is AnonymousFunctionExpressionSyntax))
        {
            throw new ExtractionException($"Auxiliary anchor '{id}' is owned by a local function or lambda.");
        }

        MethodDeclarationSyntax? enclosingMethod = node.AncestorsAndSelf()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault();
        if (!ReferenceEquals(enclosingMethod, method))
        {
            throw new ExtractionException($"Auxiliary anchor '{id}' is not owned by its admitted method.");
        }
    }

    private static InvocationExpressionSyntax FindDirectGuardInvocation(
        IfStatementSyntax guard,
        string canonical,
        string id)
    {
        InvocationExpressionSyntax[] matches = guard.Statement.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => Canonical(invocation) == canonical)
            .ToArray();
        if (matches.Length != 1 || !IsDirectGuardStatement(guard, matches[0]))
        {
            throw new ExtractionException($"Guard '{id}' must contain exactly one direct '{canonical}' invocation.");
        }

        return matches[0];
    }

    private static bool IsDirectGuardStatement(IfStatementSyntax guard, InvocationExpressionSyntax invocation)
    {
        StatementSyntax? statement = invocation.AncestorsAndSelf()
            .OfType<StatementSyntax>()
            .FirstOrDefault();
        if (statement is not ExpressionStatementSyntax expression ||
            !ReferenceEquals(expression.Expression, invocation))
        {
            return false;
        }

        return guard.Statement switch
        {
            BlockSyntax block => block.Statements.Count == 1 &&
                ReferenceEquals(block.Statements[0], statement),
            _ => ReferenceEquals(guard.Statement, statement),
        };
    }

    private static bool IsDirectReturnInTrueArm(IfStatementSyntax guard, InvocationExpressionSyntax invocation)
    {
        if (guard.Else is not null || !guard.Statement.DescendantNodesAndSelf().Contains(invocation))
        {
            return false;
        }

        ReturnStatementSyntax? returnStatement = invocation.AncestorsAndSelf()
            .OfType<ReturnStatementSyntax>()
            .FirstOrDefault();
        if (returnStatement is null || !ReferenceEquals(returnStatement.Expression, invocation))
        {
            return false;
        }

        return guard.Statement switch
        {
            BlockSyntax block => block.Statements.Count == 1 &&
                ReferenceEquals(block.Statements[0], returnStatement),
            _ => ReferenceEquals(guard.Statement, returnStatement),
        };
    }

    private static void RequireAuxiliaryControlFlowRelations(
        IReadOnlyList<AuxiliaryAnchorIdentity> anchors,
        IReadOnlyDictionary<string, SyntaxNode> nodes,
        IReadOnlyDictionary<string, SemanticModel> models,
        MethodDeclarationSyntax adapterMethod,
        MethodDeclarationSyntax blockTraceStart,
        MethodDeclarationSyntax markSuccess,
        MethodDeclarationSyntax markFailed,
        MethodDeclarationSyntax buildReceipt,
        MethodDeclarationSyntax txTraceStart,
        MethodDeclarationSyntax txTraceEnd,
        MethodDeclarationSyntax parallelProcess,
        MethodDeclarationSyntax processBlock,
        int transactionsExecutedEvaluationBlock)
    {
        Dictionary<string, AuxiliaryAnchorIdentity> byId = anchors.ToDictionary(
            static anchor => anchor.Id,
            StringComparer.Ordinal);

        ControlFlowGraph adapterGraph = RequireGraph(models[TransactionProcessorAdapterPath], adapterMethod,
            "TransactionProcessorAdapterExtensions.ProcessTransaction");
        RequireDominance(adapterGraph, byId["adapter.tx-trace-start"].ControlFlowBlock,
            byId["adapter.execute"].ControlFlowBlock,
            "TransactionProcessorAdapterExtensions StartNewTxTrace must dominate Execute.");
        RequireDominance(adapterGraph, byId["adapter.execute"].ControlFlowBlock,
            byId["adapter.tx-trace-end"].ControlFlowBlock,
            "TransactionProcessorAdapterExtensions Execute must dominate EndTxTrace on the normal path.");
        RequirePostDominance(adapterGraph, byId["adapter.tx-trace-end"].ControlFlowBlock,
            byId["adapter.execute"].ControlFlowBlock,
            "TransactionProcessorAdapterExtensions EndTxTrace must postdominate Execute on the normal path.");

        ControlFlowGraph tracerGraph = RequireGraph(models[BlockReceiptsTracerPath], txTraceEnd,
            "BlockReceiptsTracer.EndTxTrace");
        RequireDominance(tracerGraph, byId["tracer.tx-end-delegate"].ControlFlowBlock,
            byId["tracer.tx-end-index"].ControlFlowBlock,
            "BlockReceiptsTracer delegate must dominate currentIndex increment.");
        RequirePostDominance(tracerGraph, byId["tracer.tx-end-index"].ControlFlowBlock,
            byId["tracer.tx-end-delegate"].ControlFlowBlock,
            "BlockReceiptsTracer currentIndex increment must postdominate delegation on the normal path.");

        ControlFlowGraph resetGraph = RequireGraph(models[BlockReceiptsTracerPath], blockTraceStart,
            "BlockReceiptsTracer.StartNewBlockTrace");
        RequireNormalExitDominance(resetGraph, byId["tracer.reset-index"].ControlFlowBlock,
            "BlockReceiptsTracer currentIndex reset must dominate every normal exit.");
        RequireNormalExitDominance(resetGraph, byId["tracer.reset-receipts"].ControlFlowBlock,
            "BlockReceiptsTracer receipt reset must dominate every normal exit.");
        RequireNormalExitDominance(resetGraph, byId["tracer.reset-block-gas"].ControlFlowBlock,
            "BlockReceiptsTracer block-gas reset must dominate every normal exit.");
        RequireNormalExitDominance(resetGraph, byId["tracer.reset-receipt-gas"].ControlFlowBlock,
            "BlockReceiptsTracer receipt-gas reset must dominate every normal exit.");

        ControlFlowGraph txStartGraph = RequireGraph(models[BlockReceiptsTracerPath], txTraceStart,
            "BlockReceiptsTracer.StartNewTxTrace");
        RequireDominance(txStartGraph, byId["tracer.tx-start-current"].ControlFlowBlock,
            byId["tracer.tx-start-delegate"].ControlFlowBlock,
            "BlockReceiptsTracer CurrentTx assignment must dominate delegation.");
        RequirePostDominance(txStartGraph, byId["tracer.tx-start-delegate"].ControlFlowBlock,
            byId["tracer.tx-start-current"].ControlFlowBlock,
            "BlockReceiptsTracer delegation must postdominate CurrentTx assignment on the normal path.");

        ControlFlowGraph receiptGraph = RequireGraph(models[BlockReceiptsTracerPath], buildReceipt,
            "BlockReceiptsTracer.BuildReceipt");
        RequireNormalExitDominance(receiptGraph, byId["tracer.receipt-index"].ControlFlowBlock,
            "BlockReceiptsTracer receipt index assignment must dominate every normal exit.");

        RequireDirectMethodStatement(nodes["tracer.success-append"], markSuccess,
            "tracer.success-append");
        RequireDirectMethodStatement(nodes["tracer.failure-append"], markFailed,
            "tracer.failure-append");
        ControlFlowGraph successGraph = RequireGraph(models[BlockReceiptsTracerPath], markSuccess,
            "BlockReceiptsTracer.MarkAsSuccess");
        RequireNormalExitDominance(successGraph, byId["tracer.success-append"].ControlFlowBlock,
            "BlockReceiptsTracer.MarkAsSuccess receipt append must dominate every normal exit.");
        ControlFlowGraph failureGraph = RequireGraph(models[BlockReceiptsTracerPath], markFailed,
            "BlockReceiptsTracer.MarkAsFailed");
        RequireNormalExitDominance(failureGraph, byId["tracer.failure-append"].ControlFlowBlock,
            "BlockReceiptsTracer.MarkAsFailed receipt append must dominate every normal exit.");

        // These checks make the source owner explicit even for anchors whose relation is cross-method.
        EnsureDeclaredMethodOwner(nodes["tracer.reset-index"], blockTraceStart, "tracer.reset-index");
        EnsureDeclaredMethodOwner(nodes["tracer.receipt-index"], buildReceipt, "tracer.receipt-index");
        EnsureDeclaredMethodOwner(nodes["tracer.tx-start-current"], txTraceStart, "tracer.tx-start-current");
        EnsureDeclaredMethodOwner(nodes["parallel.bal-disabled-gate"], parallelProcess, "parallel.bal-disabled-gate");
        EnsureDeclaredMethodOwner(nodes["block.receipts-tracer-start"], processBlock, "block.receipts-tracer-start");

        ControlFlowGraph processBlockGraph = RequireGraph(models[BlockProcessorPath], processBlock,
            "BlockProcessor.ProcessBlock");
        InvocationExpressionSyntax blockTransactionCall = SingleInvocation(
            processBlock,
            "_blockTransactionsExecutor.ProcessTransactions(block,options,ReceiptsTracer,token)",
            "block.transaction-fold-call");
        InvocationExpressionSyntax[] commits = processBlock.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => Canonical(invocation) == "CommitState(spec)")
            .OrderBy(static invocation => invocation.SpanStart)
            .ToArray();
        InvocationExpressionSyntax[] preCommits = commits
            .Where(commit => commit.SpanStart < blockTransactionCall.SpanStart)
            .ToArray();
        InvocationExpressionSyntax[] postCommits = commits
            .Where(commit => commit.SpanStart > blockTransactionCall.SpanStart)
            .ToArray();
        if (commits.Length != 3 || preCommits.Length != 1 || postCommits.Length != 2)
        {
            throw new ExtractionException(
                "ProcessBlock normal-path route requires one pre-fold and two post-fold CommitState(spec) calls.");
        }

        RequireDominance(
            processBlockGraph,
            byId["block.receipts-tracer-start"].ControlFlowBlock,
            FindControlFlowBlock(processBlockGraph, preCommits[0].SpanStart),
            "StartNewBlockTrace must dominate the normal pre-transaction CommitState(spec).");
        RequireDominance(
            processBlockGraph,
            FindControlFlowBlock(processBlockGraph, preCommits[0].SpanStart),
            FindControlFlowBlock(processBlockGraph, blockTransactionCall.SpanStart),
            "The normal pre-transaction CommitState(spec) must dominate the transaction fold.");
        RequirePostDominance(
            processBlockGraph,
            transactionsExecutedEvaluationBlock,
            FindControlFlowBlock(processBlockGraph, blockTransactionCall.SpanStart),
            "TransactionsExecuted must postdominate the normal transaction fold return.");
        RequirePostDominance(
            processBlockGraph,
            FindControlFlowBlock(processBlockGraph, postCommits[0].SpanStart),
            transactionsExecutedEvaluationBlock,
            "The normal post-transaction CommitState(spec) must postdominate TransactionsExecuted.");
    }

    private static void RequireDirectMethodStatement(SyntaxNode node, MethodDeclarationSyntax method, string id)
    {
        EnsureDeclaredMethodOwner(node, method, id);
        StatementSyntax? statement = node.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();
        if (statement is not ExpressionStatementSyntax || !ReferenceEquals(statement.Parent, method.Body))
        {
            throw new ExtractionException($"Auxiliary anchor '{id}' must be a direct statement in its admitted method body.");
        }
    }

    private static ControlFlowGraph RequireGraph(SemanticModel model, MethodDeclarationSyntax method, string id) =>
        TryCreateGraph(model, method)
        ?? throw new ExtractionException($"No Roslyn control-flow graph for {id}.");

    private static void RequireDominance(
        ControlFlowGraph graph,
        int dominator,
        int node,
        string message)
    {
        if (dominator < 0 || node < 0 || !Dominates(graph, dominator, node, normalOnly: true))
        {
            throw new ExtractionException(message);
        }
    }

    private static void RequirePostDominance(
        ControlFlowGraph graph,
        int postDominator,
        int node,
        string message)
    {
        if (postDominator < 0 || node < 0 || !PostDominates(graph, postDominator, node, normalOnly: true))
        {
            throw new ExtractionException(message);
        }
    }

    private static void RequireNormalExitDominance(ControlFlowGraph graph, int dominator, string message)
    {
        int[] normalExitPredecessors = graph.Blocks
            .Where(static block => block.IsReachable)
            .Where(block => Branches(block).Any(branch =>
                branch.Destination is BasicBlock destination &&
                destination.Kind == BasicBlockKind.Exit &&
                !IsExceptionalBranch(block, branch)))
            .Select(static block => block.Ordinal)
            .Distinct()
            .ToArray();
        if (normalExitPredecessors.Length == 0 ||
            normalExitPredecessors.Any(exit => !Dominates(graph, dominator, exit, normalOnly: true)))
        {
            throw new ExtractionException(message);
        }
    }

    private static bool Dominates(ControlFlowGraph graph, int dominator, int node, bool normalOnly)
    {
        HashSet<int>[] predecessors = BuildPredecessors(graph, normalOnly);
        int[] reachable = graph.Blocks.Where(static block => block.IsReachable)
            .Select(static block => block.Ordinal).ToArray();
        if (!reachable.Contains(dominator) || !reachable.Contains(node))
        {
            return false;
        }

        int entry = graph.Blocks.First(block => block.Kind == BasicBlockKind.Entry).Ordinal;
        Dictionary<int, HashSet<int>> dominators = reachable.ToDictionary(
            ordinal => ordinal,
            ordinal => ordinal == entry ? [entry] : reachable.ToHashSet());
        bool changed;
        do
        {
            changed = false;
            foreach (int ordinal in reachable.Where(ordinal => ordinal != entry))
            {
                HashSet<int>[] incoming = predecessors[ordinal]
                    .Where(dominators.ContainsKey)
                    .Select(predecessor => dominators[predecessor])
                    .ToArray();
                HashSet<int> next = incoming.Length == 0
                    ? [ordinal]
                    : incoming.Skip(1).Aggregate(
                        new HashSet<int>(incoming[0]),
                        static (set, candidate) =>
                        {
                            set.IntersectWith(candidate);
                            return set;
                        });
                next.Add(ordinal);
                if (!dominators[ordinal].SetEquals(next))
                {
                    dominators[ordinal] = next;
                    changed = true;
                }
            }
        } while (changed);

        return dominators[node].Contains(dominator);
    }

    private static bool PostDominates(ControlFlowGraph graph, int postDominator, int node, bool normalOnly)
    {
        Dictionary<int, HashSet<int>> successors = BuildSuccessors(graph, normalOnly);
        int[] reachable = graph.Blocks.Where(static block => block.IsReachable)
            .Select(static block => block.Ordinal).ToArray();
        if (!reachable.Contains(postDominator) || !reachable.Contains(node))
        {
            return false;
        }

        int[] exits = reachable.Where(ordinal => successors[ordinal].Count == 0).ToArray();
        Dictionary<int, HashSet<int>> postDominators = reachable.ToDictionary(
            ordinal => ordinal,
            ordinal => exits.Contains(ordinal) ? [ordinal] : reachable.ToHashSet());
        bool changed;
        do
        {
            changed = false;
            foreach (int ordinal in reachable.Where(ordinal => !exits.Contains(ordinal)))
            {
                HashSet<int>[] outgoing = successors[ordinal]
                    .Where(postDominators.ContainsKey)
                    .Select(successor => postDominators[successor])
                    .ToArray();
                HashSet<int> next = outgoing.Length == 0
                    ? [ordinal]
                    : outgoing.Skip(1).Aggregate(
                        new HashSet<int>(outgoing[0]),
                        static (set, candidate) =>
                        {
                            set.IntersectWith(candidate);
                            return set;
                        });
                next.Add(ordinal);
                if (!postDominators[ordinal].SetEquals(next))
                {
                    postDominators[ordinal] = next;
                    changed = true;
                }
            }
        } while (changed);

        return postDominators[node].Contains(postDominator);
    }

    private static HashSet<int>[] BuildPredecessors(ControlFlowGraph graph, bool normalOnly)
    {
        int maxOrdinal = graph.Blocks.Max(static block => block.Ordinal);
        HashSet<int>[] predecessors = Enumerable.Range(0, maxOrdinal + 1)
            .Select(static _ => new HashSet<int>()).ToArray();
        foreach (BasicBlock block in graph.Blocks.Where(static block => block.IsReachable))
        {
            foreach (ControlFlowBranch branch in Branches(block))
            {
                if (normalOnly && IsExceptionalBranch(block, branch))
                {
                    continue;
                }

                if (branch.Destination is BasicBlock destination && destination.IsReachable)
                {
                    predecessors[destination.Ordinal].Add(block.Ordinal);
                }
            }
        }

        return predecessors;
    }

    private static Dictionary<int, HashSet<int>> BuildSuccessors(ControlFlowGraph graph, bool normalOnly)
    {
        Dictionary<int, HashSet<int>> successors = graph.Blocks
            .Where(static block => block.IsReachable)
            .ToDictionary(static block => block.Ordinal, static _ => new HashSet<int>());
        foreach (BasicBlock block in graph.Blocks.Where(static block => block.IsReachable))
        {
            foreach (ControlFlowBranch branch in Branches(block))
            {
                if (normalOnly && IsExceptionalBranch(block, branch))
                {
                    continue;
                }

                if (branch.Destination is BasicBlock destination && destination.IsReachable)
                {
                    successors[block.Ordinal].Add(destination.Ordinal);
                }
            }
        }

        return successors;
    }

    private static bool IsExceptionalBranch(BasicBlock block, ControlFlowBranch branch) =>
        branch.Semantics.ToString().Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
        branch.Semantics.ToString().Contains("Throw", StringComparison.OrdinalIgnoreCase) ||
        block.Operations.Any(static operation => operation is IThrowOperation);

    private static AuxiliaryAnchorIdentity BindAuxiliaryAnchor(
        AuxiliaryAnchorExpectation expectation,
        SourceFile source,
        MethodDeclarationSyntax method,
        SyntaxNode node,
        SemanticModel model,
        ControlFlowGraph graph)
    {
        EnsureDeclaredMethodOwner(node, method, expectation.Id);
        ValidateDeclaredOwner(method, model, AuxiliaryOwner(expectation.Owner), expectation.Id);
        IOperation operation = model.GetOperation(node)
            ?? throw new ExtractionException($"No IOperation for auxiliary anchor '{expectation.Id}'.");
        SymbolInfo symbolInfo = model.GetSymbolInfo(node);
        if (symbolInfo.CandidateReason != CandidateReason.None || symbolInfo.CandidateSymbols.Length != 0 ||
            operation is IInvalidOperation || HasErrorType(operation.Type))
        {
            throw new ExtractionException($"Auxiliary anchor '{expectation.Id}' has an unresolved or invalid typed operation.");
        }

        string targetSymbol = operation switch
        {
            IInvocationOperation invocation => invocation.TargetMethod?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
                ?? throw new ExtractionException($"Auxiliary invocation '{expectation.Id}' has no target symbol."),
            ISimpleAssignmentOperation assignment => OperationTargetName(assignment.Target, model),
            IIncrementOrDecrementOperation increment => OperationTargetName(increment.Target, model),
            IUnaryOperation { OperatorMethod: null } or IBinaryOperation { OperatorMethod: null } => Canonical(node),
            _ => (symbolInfo.Symbol ?? model.GetDeclaredSymbol(node))?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ??
                Canonical(node),
        };
        if (operation is IInvocationOperation invocationOperation &&
            (invocationOperation.TargetMethod is null || HasErrorSymbol(invocationOperation.TargetMethod)))
        {
            throw new ExtractionException($"Auxiliary invocation '{expectation.Id}' has no valid target symbol.");
        }

        bool errorSymbol = HasErrorSymbol(symbolInfo.Symbol) || HasErrorType(operation.Type) ||
            operation is IInvocationOperation target && HasErrorSymbol(target.TargetMethod) ||
            operation is ISimpleAssignmentOperation assignmentTarget && HasErrorSymbol(OperationTargetSymbol(assignmentTarget.Target, model)) ||
            operation is IIncrementOrDecrementOperation incrementTarget && HasErrorSymbol(OperationTargetSymbol(incrementTarget.Target, model));
        if (errorSymbol)
        {
            throw new ExtractionException($"Auxiliary anchor '{expectation.Id}' has an error symbol.");
        }

        AssignmentValueBinding[] rightHandSideBindings = CaptureAssignmentValue(operation);
        ValidateAssignmentValue(expectation.Id, rightHandSideBindings);

        int controlFlowBlock = FindControlFlowBlock(graph,
            node is PrefixUnaryExpressionSyntax unary ? unary.Operand.SpanStart : node.SpanStart);
        BasicBlock? containingBlock = controlFlowBlock < 0
            ? null
            : graph.Blocks.SingleOrDefault(block => block.Ordinal == controlFlowBlock);
        if (containingBlock is null || !containingBlock.IsReachable)
        {
            throw new ExtractionException($"Auxiliary anchor '{expectation.Id}' is not attached to a reachable CFG block.");
        }

        return new(
            expectation.Id,
            source.RelativePath,
            OwnerPath(method),
            method.Identifier.ValueText,
            Canonical(node),
            expectation.Relation,
            node.Kind().ToString(),
            operation.Kind.ToString(),
            OperationType(operation),
            operation is IInvocationOperation invocationTarget
                ? CanonicalSymbol(invocationTarget.TargetMethod)
                : targetSymbol,
            Sha256(Encoding.UTF8.GetBytes(Canonical(node))),
            symbolInfo.CandidateSymbols.Length != 0,
            errorSymbol,
            controlFlowBlock,
            containingBlock.IsReachable,
            method.Identifier.ValueText,
            InvocationIdentity(expectation.Id, node, operation, model).Type,
            InvocationIdentity(expectation.Id, node, operation, model).Symbol,
            InvocationIdentity(expectation.Id, node, operation, model).Arguments,
            rightHandSideBindings);
    }

    private static string OperationTargetName(IOperation target, SemanticModel model) =>
        CanonicalSymbol(OperationTargetSymbol(target, model)) is { Length: > 0 } identity ? identity : Canonical(target.Syntax);

    private static ISymbol? OperationTargetSymbol(IOperation target, SemanticModel model) =>
        target switch
        {
            IFieldReferenceOperation field => field.Field,
            IPropertyReferenceOperation property => property.Property,
            _ => model.GetSymbolInfo(target.Syntax).Symbol,
        };

    private static int SourceOrderPosition(SyntaxNode node) =>
        node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member }
            ? member.Name.SpanStart : node.SpanStart;

    private static void RequireSourceOrder(
        IReadOnlyDictionary<string, SyntaxNode> nodes,
        string first,
        string second,
        string message,
        bool allowDifferentMethods = false)
    {
        if (!allowDifferentMethods && !string.Equals(
                nodes[first].Ancestors().OfType<MethodDeclarationSyntax>().First().Identifier.ValueText,
                nodes[second].Ancestors().OfType<MethodDeclarationSyntax>().First().Identifier.ValueText,
                StringComparison.Ordinal))
        {
            throw new ExtractionException(message);
        }

        if (SourceOrderPosition(nodes[first]) >= SourceOrderPosition(nodes[second]))
        {
            throw new ExtractionException(message);
        }
    }

    private static void RequireSourceOrder(
        IReadOnlyDictionary<string, SyntaxNode> nodes,
        string first,
        string second,
        string third,
        string message)
    {
        RequireSourceOrder(nodes, first, second, message);
        RequireSourceOrder(nodes, second, third, message);
    }

    private static void RequireSourceOrder(
        IReadOnlyDictionary<string, SyntaxNode> nodes,
        string first,
        string second,
        string third,
        string fourth,
        string message)
    {
        RequireSourceOrder(nodes, first, second, message);
        RequireSourceOrder(nodes, second, third, message);
        RequireSourceOrder(nodes, third, fourth, message);
    }

    private static AnchorIdentity BindAnchor(
        string id,
        SourceFile source,
        MethodDeclarationSyntax method,
        SyntaxNode node,
        SemanticModel model,
        Compilation compilation,
        string relation)
    {
        IOperation operation = model.GetOperation(node)
            ?? throw new ExtractionException($"No IOperation for anchor '{id}'.");
        TypedBinding binding = BindNode(id, source, method, node, model, compilation, null, operation);
        if (binding.HasCandidateSymbols || binding.IsErrorSymbol || !binding.IsReachable || operation is IInvalidOperation)
        {
            throw new ExtractionException($"Anchor '{id}' has an unresolved or invalid typed operation.");
        }

        return new(id, source.RelativePath, OwnerPath(method), Canonical(node), relation, binding);
    }

    private static TypedBinding BindNode(
        string id,
        SourceFile source,
        MethodDeclarationSyntax method,
        SyntaxNode node,
        SemanticModel model,
        Compilation compilation,
        ControlFlowGraph? graph,
        IOperation operation)
    {
        EnsureDeclaredMethodOwner(node, method, id);
        SymbolInfo info = model.GetSymbolInfo(node);
        ISymbol? symbol = info.Symbol ?? model.GetDeclaredSymbol(node);
        if (info.CandidateReason != CandidateReason.None || info.CandidateSymbols.Length != 0)
        {
            throw new ExtractionException($"Candidate or ambiguous symbol at '{source.RelativePath}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}'.");
        }

        ControlFlowGraph? resolvedGraph = graph ?? TryCreateGraph(model, method);
        int blockOrdinal = FindControlFlowBlock(resolvedGraph, node.SpanStart);
        BasicBlock? containingBlock = blockOrdinal < 0 || resolvedGraph is null
            ? null
            : resolvedGraph.Blocks.SingleOrDefault(block => block.Ordinal == blockOrdinal);
        bool reachable = containingBlock?.IsReachable == true;
        DataFlowAnalysis? dataFlow = TryAnalyzeDataFlow(model, method);
        FileLinePositionSpan span = node.GetLocation().GetLineSpan();

        string symbolId = CanonicalSymbol(symbol);
        string symbolKind = symbol?.Kind.ToString() ?? string.Empty;
        (string receiverType, string receiverSymbol, string[] argumentBindings) = InvocationIdentity(id, node, operation, model);
        string operationType = OperationType(operation);
        bool errorSymbol = HasErrorSymbol(symbol) || HasErrorType(operation.Type) ||
            operation is IInvocationOperation invocationOperation &&
            (HasErrorSymbol(invocationOperation.TargetMethod) || HasErrorType(invocationOperation.Instance?.Type));

        if (operation is IInvocationOperation invocationBinding && invocationBinding.TargetMethod is null)
        {
            throw new ExtractionException($"Invocation at '{source.RelativePath}:{span.StartLinePosition.Line + 1}' has no target symbol.");
        }

        return new(
            source.RelativePath,
            OwnerPath(method),
            method.Identifier.ValueText,
            node.Kind().ToString(),
            Canonical(node),
            Sha256(Encoding.UTF8.GetBytes(Canonical(node))),
            operation is IInvocationOperation invocationTarget
                ? CanonicalSymbol(invocationTarget.TargetMethod)
                : symbolId,
            operation is IInvocationOperation invocationKind ? invocationKind.TargetMethod.Kind.ToString() : symbolKind,
            operation.Kind.ToString(),
            operationType,
            receiverType,
            method.Identifier.ValueText,
            node.SpanStart,
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            blockOrdinal,
            reachable,
            info.CandidateSymbols.Length != 0,
            info.CandidateReason.ToString(),
            errorSymbol,
            dataFlow?.Succeeded == true,
            dataFlow?.ReadInside.Select(CanonicalSymbol).OrderBy(static name => name, StringComparer.Ordinal).ToArray() ?? [],
            dataFlow?.WrittenInside.Select(CanonicalSymbol).OrderBy(static name => name, StringComparer.Ordinal).ToArray() ?? [],
            receiverSymbol,
            argumentBindings);
    }

    private static ControlFlowIdentity BuildControlFlow(
        string id,
        SourceFile source,
        MethodDeclarationSyntax method,
        SemanticModel model,
        Compilation compilation)
    {
        ControlFlowGraph graph = TryCreateGraph(model, method)
            ?? throw new ExtractionException($"No Roslyn control-flow graph for {id}.");
        BasicBlock[] reachable = graph.Blocks.Where(static block => block.IsReachable).ToArray();
        if (reachable.Length == 0)
        {
            throw new ExtractionException($"No reachable CFG blocks for {id}.");
        }

        List<string> edges = [];
        List<int> normalExits = [];
        List<int> exceptionalExits = [];
        List<string> backEdges = [];
        foreach (BasicBlock block in reachable)
        {
            foreach (ControlFlowBranch branch in Branches(block))
            {
                if (branch.Destination is not BasicBlock destination)
                {
                    continue;
                }

                bool exceptional = branch.Semantics.ToString().Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
                    branch.Semantics.ToString().Contains("Throw", StringComparison.OrdinalIgnoreCase) ||
                    block.Operations.Any(static operation => operation is IThrowOperation);
                if (destination.Kind == BasicBlockKind.Exit)
                {
                    (exceptional ? exceptionalExits : normalExits).Add(block.Ordinal);
                }
                else if (destination.IsReachable)
                {
                    edges.Add($"{block.Ordinal}->{destination.Ordinal}");
                    if (destination.Ordinal <= block.Ordinal)
                    {
                        backEdges.Add($"{block.Ordinal}->{destination.Ordinal}");
                    }
                }
            }
        }

        TypedBinding binding = BindNode(id, source, method, method, model, compilation, graph, model.GetOperation(method)!);
        return new(
            id,
            source.RelativePath,
            OwnerPath(method),
            method.Identifier.ValueText,
            reachable.Select(static block => block.Ordinal).OrderBy(static ordinal => ordinal).ToArray(),
            edges.Distinct(StringComparer.Ordinal).OrderBy(static edge => edge, StringComparer.Ordinal).ToArray(),
            normalExits.Distinct().OrderBy(static ordinal => ordinal).ToArray(),
            exceptionalExits.Distinct().OrderBy(static ordinal => ordinal).ToArray(),
            backEdges.Distinct(StringComparer.Ordinal).OrderBy(static edge => edge, StringComparer.Ordinal).ToArray(),
            binding);
    }

    private static IEnumerable<ControlFlowBranch> Branches(BasicBlock block)
    {
        if (block.FallThroughSuccessor is ControlFlowBranch fallThrough)
        {
            yield return fallThrough;
        }

        if (block.ConditionalSuccessor is ControlFlowBranch conditional)
        {
            yield return conditional;
        }
    }

    private static ControlFlowGraph? TryCreateGraph(SemanticModel model, MethodDeclarationSyntax method)
    {
        IOperation? operation = model.GetOperation(method);
        return operation is IMethodBodyOperation body ? ControlFlowGraph.Create(body) : null;
    }

    private static int FindControlFlowBlock(ControlFlowGraph? graph, int position)
    {
        if (graph is null)
        {
            return -1;
        }

        foreach (BasicBlock block in graph.Blocks)
        {
            if (block.Operations.Any(operation => ContainsPosition(operation, position)) ||
                block.BranchValue is not null && ContainsPosition(block.BranchValue, position))
            {
                return block.Ordinal;
            }
        }

        return -1;
    }

    private static bool ContainsPosition(IOperation operation, int position) =>
        operation.Syntax is not null && operation.Syntax.Span.Start <= position && operation.Syntax.Span.End >= position;

    private static DataFlowAnalysis? TryAnalyzeDataFlow(SemanticModel model, MethodDeclarationSyntax method)
    {
        try
        {
            return method.Body is null ? null : model.AnalyzeDataFlow(method.Body);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static InvocationExpressionSyntax SingleInvocation(SyntaxNode owner, string canonical, string id)
    {
        InvocationExpressionSyntax[] matches = owner.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => Canonical(invocation) == canonical)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new ExtractionException($"{id} expected one '{canonical}' invocation, found {matches.Length}.");
        }

        return matches[0];
    }

    private static CompositionIdentity BuildComposition(string root)
    {
        string generated = ReadAndHashComposition(root, ReceiptKernelPath);
        string refinement = ReadAndHashComposition(root, ReceiptRefinementPath);
        ReceiptTerminalSourceClosureIdentity sourceClosure = ReadReceiptTerminalSourceClosure(root);
        return new(
            ReceiptKernelPath,
            generated,
            ReceiptRefinementPath,
            refinement,
            ReceiptRelation,
            [
                "State.receipts",
                "State.gasHistory",
                "State.cumulativeReceiptGas",
                "State.headerGasUsed",
                "FinalizationObservation.trace.state",
                "FinalizationObservation.result",
            ],
            sourceClosure);
    }

    private static ReceiptTerminalSourceClosureIdentity ReadReceiptTerminalSourceClosure(string root) =>
        ReceiptDependencyAudit.ReadValidated(root);

    private static bool ReceiptPinsMatch(SourcePin[]? expected, SourcePin[]? actual) =>
        expected is not null && actual is not null && expected.Length == actual.Length && expected.Zip(actual).All(static pair =>
            string.Equals(pair.First.Path, pair.Second.Path, StringComparison.Ordinal) &&
            string.Equals(pair.First.Sha256, pair.Second.Sha256, StringComparison.Ordinal));

    private static bool SourcePinsMatch(SourcePin[]? actual, SourcePin[] expected) =>
        actual is not null && actual.Length == expected.Length && actual.Zip(expected).All(static pair =>
            string.Equals(pair.First.Path, pair.Second.Path, StringComparison.Ordinal) &&
            string.Equals(pair.First.Role, pair.Second.Role, StringComparison.Ordinal) &&
            string.Equals(pair.First.Sha256, pair.Second.Sha256, StringComparison.Ordinal));

    private static string ReadAndHashComposition(string root, string relativePath)
    {
        string path = Path.Combine(root, relativePath);
        if (!File.Exists(path))
        {
            throw new ExtractionException($"The settled receipt composition artifact is missing: '{relativePath}'.");
        }

        return Sha256(File.ReadAllBytes(path));
    }

    private static void ValidateIr(IrDocument document)
    {
        if (document is null || document.SchemaVersion != SchemaVersion || document.ExtractorVersion != ExtractorVersion ||
            document.Kernel != Kernel || document.Included is null || document.Excluded is null ||
            document.Sources is null || document.AuxiliarySources is null || document.SupportSources is null || document.CompilerReferences is null ||
            document.Members is null || document.Anchors is null || document.AuxiliaryAnchors is null ||
            document.ControlFlows is null || document.TransactionsExecuted is null ||
            document.Composition is null || document.SourceRoute is null ||
            document.OpenObligations is null)
        {
            throw new ExtractionException("The sequential transaction-fold IR header or required arrays changed.");
        }

        if (!document.Included.SequenceEqual(IncludedScope, StringComparer.Ordinal) ||
            !document.Excluded.SequenceEqual(ExcludedScope, StringComparer.Ordinal) ||
            !document.OpenObligations.SequenceEqual(OpenObligations, StringComparer.Ordinal))
        {
            throw new ExtractionException("The sequential transaction-fold IR scope or obligations changed.");
        }

        string[] expectedSourceRoles = ["executor", "caller", "partial-standard-source", "interface-closure", "flag-closure"];
        if (document.Sources.Any(static source => source is null) ||
            !document.Sources.Select(static source => source.Path).SequenceEqual(SourcePaths, StringComparer.Ordinal) ||
            document.Sources.Any(static source => source is null || !IsSha256(source.Sha256) || !IsSha256(source.SyntaxSha256) ||
                string.IsNullOrWhiteSpace(source.Role)) ||
            !document.Sources.Select(static source => source.Role)
                .SequenceEqual(expectedSourceRoles, StringComparer.Ordinal))
        {
            throw new ExtractionException("The sequential transaction-fold IR source identity set is incomplete.");
        }

        if (document.AuxiliarySources.Any(static source => source is null) ||
            !document.AuxiliarySources.Select(static source => source.Path)
                .SequenceEqual(AuxiliarySourcePaths, StringComparer.Ordinal) ||
            !document.AuxiliarySources.Select(static source => source.Role)
                .SequenceEqual(AuxiliarySourceRoles, StringComparer.Ordinal) ||
            document.AuxiliarySources.Any(static source => source is null || !IsSha256(source.Sha256)))
        {
            throw new ExtractionException("The sequential transaction-fold IR auxiliary source identity set is incomplete.");
        }
        if (document.SupportSources.Any(static source => source is null || !IsSha256(source.Sha256) || source.Role != "complete-partial-compilation-support") ||
            !document.SupportSources.Select(static source => source.Path).SequenceEqual(SupportSourcePaths, StringComparer.Ordinal))
        {
            throw new ExtractionException("fold.support.identity: support source inventory changed.");
        }
        ValidateCompilerReferenceClosureIdentity(document.CompilerReferences);

        string[] expectedMembers = ExpectedMemberShape.Select(static member => member.Id).ToArray();
        if (document.Members.Any(static member => member is null) ||
            !document.Members.Select(static member => member.Id).SequenceEqual(expectedMembers, StringComparer.Ordinal) ||
            document.Members.Any(static member => member is null || member.Binding is null ||
                member.Path != member.Binding.Path || member.Owner != member.Binding.Owner ||
                member.Name != member.Binding.Member ||
                member.Binding.SymbolId != member.Signature ||
                member.Binding.SymbolKind != "Method" || member.Binding.NodeKind != "MethodDeclaration" ||
                member.Binding.OperationKind != "MethodBodyOperation" ||
                member.Binding.ContainingMember != member.Name ||
                !string.Equals(member.Binding.SyntaxSha256,
                    Sha256(Encoding.UTF8.GetBytes(member.Binding.CanonicalSyntax)), StringComparison.Ordinal)))
        {
            throw new ExtractionException("The sequential transaction-fold member admission changed.");
        }

        for (int index = 0; index < ExpectedMemberShape.Length; index++)
        {
            MemberIdentity member = document.Members[index];
            (string id, string owner, string name, int parameterCount) = ExpectedMemberShape[index];
            if (member.Id != id || member.Owner != owner || member.Name != name || member.ParameterCount != parameterCount)
            {
                throw new ExtractionException("The sequential transaction-fold member signature shape changed.");
            }
        }

        string[] expectedAnchors =
        [
            "executor.metrics-setup",
            "executor.validation-mode",
            "executor.process-transaction-call",
            "executor.gas-limit-guard",
            "executor.invalid-result-throw",
            "executor.transaction-adapter-call",
            "executor.processed-event",
            "block.transaction-fold-call",
            "block.pre-transaction-commit",
            "block.post-transaction-commit",
            "block.transactions-executed-signal",
        ];
        if (document.Anchors.Any(static anchor => anchor is null) ||
            !document.Anchors.Select(static anchor => anchor.Id).SequenceEqual(expectedAnchors, StringComparer.Ordinal) ||
            !document.Anchors.Select(static anchor => anchor.Relation)
                .SequenceEqual(ExpectedAnchorRelations, StringComparer.Ordinal) ||
            !document.Anchors.Select(static anchor => anchor.Binding?.Member ?? string.Empty)
                .SequenceEqual([
                    "ProcessTransactions", "ProcessTransactions", "ProcessTransactions", "ProcessTransactions",
                    "ProcessTransaction", "ProcessTransaction", "ProcessTransaction",
                    "ProcessBlock", "ProcessBlock", "ProcessBlock", "ProcessBlock",
                ], StringComparer.Ordinal) ||
            !document.Anchors.Select(static anchor => anchor.CanonicalSyntax)
                .SequenceEqual(ExpectedAnchorCanonicalSyntax, StringComparer.Ordinal) ||
            document.Anchors.Any(static anchor =>
                anchor.Binding is null || anchor.Path != anchor.Binding.Path || anchor.Owner != anchor.Binding.Owner ||
                anchor.CanonicalSyntax != anchor.Binding.CanonicalSyntax ||
                !anchor.Binding.IsReachable || anchor.Binding.ControlFlowBlock < 0 ||
                !string.Equals(anchor.Binding.SyntaxSha256,
                    Sha256(Encoding.UTF8.GetBytes(anchor.Binding.CanonicalSyntax)), StringComparison.Ordinal)))
        {
            throw new ExtractionException("The sequential transaction-fold anchor admission changed.");
        }

        string[] expectedControlFlows = ["executor.processTransactions", "executor.processTransaction", "block.processBlock"];
        if (document.ControlFlows.Length != 3 ||
            document.ControlFlows.Any(static flow =>
                flow is null || flow.Binding is null || flow.ReachableBlocks is null || flow.ReachableBlocks.Length == 0 || flow.Edges is null ||
                flow.NormalExitBlocks is null || flow.ExceptionalExitBlocks is null || flow.BackEdges is null ||
                flow.Path != flow.Binding.Path || flow.Owner != flow.Binding.Owner || flow.Member != flow.Binding.Member ||
                !string.Equals(flow.Binding.SyntaxSha256,
                    Sha256(Encoding.UTF8.GetBytes(flow.Binding.CanonicalSyntax)), StringComparison.Ordinal)) ||
            !document.ControlFlows.Select(static flow => flow.Id).SequenceEqual(expectedControlFlows, StringComparer.Ordinal))
        {
            throw new ExtractionException("The sequential transaction-fold CFG evidence is incomplete.");
        }

        for (int index = 0; index < document.Anchors.Length; index++)
        {
            TypedBinding binding = document.Anchors[index].Binding;
            string flowId = ExpectedAnchorControlFlows[index];
            ControlFlowIdentity flow = document.ControlFlows.Single(candidate => candidate.Id == flowId);
            if (!flow.ReachableBlocks.Contains(binding.ControlFlowBlock))
            {
                throw new ExtractionException("An admitted sequential transaction-fold anchor is not bound to its source CFG block.");
            }

            (string nodeKind, string operationKind, string operationType, string symbolKind, bool symbolRequired) = index switch
            {
                1 => ("VariableDeclarator", "VariableDeclarator", "bool", "Local", true),
                3 => ("LogicalAndExpression", "Binary", "bool", string.Empty, false),
                _ => ("InvocationExpression", "Invocation", index == 5 ? "Nethermind.Evm.TransactionProcessing.TransactionResult" :
                    index == 7 ? "Nethermind.Core.TxReceipt[]" : "void", "Method", true),
            };
            if (binding.NodeKind != nodeKind || binding.OperationKind != operationKind ||
                binding.OperationType != operationType || binding.SymbolKind != symbolKind ||
                symbolRequired && string.IsNullOrWhiteSpace(binding.SymbolId))
            {
                throw new ExtractionException("An admitted sequential transaction-fold anchor lost its expected typed operation shape.");
            }
            if (binding.OperationKind == "Invocation")
                ValidateInvocation(document.Anchors[index].Id, binding.SymbolId, binding.ReceiverType, binding.ReceiverSymbol, binding.ArgumentBindings);
        }

        string[] expectedAuxiliaryAnchorIds = ExpectedAuxiliaryAnchors
            .Select(static expectation => expectation.Id)
            .ToArray();
        if (document.AuxiliaryAnchors.Any(static anchor => anchor is null) ||
            !document.AuxiliaryAnchors.Select(static anchor => anchor.Id)
                .SequenceEqual(expectedAuxiliaryAnchorIds, StringComparer.Ordinal))
        {
            throw new ExtractionException("The sequential transaction-fold auxiliary anchor admission changed.");
        }

        for (int index = 0; index < ExpectedAuxiliaryAnchors.Length; index++)
        {
            AuxiliaryAnchorExpectation expectation = ExpectedAuxiliaryAnchors[index];
            AuxiliaryAnchorIdentity anchor = document.AuxiliaryAnchors[index];
            (string nodeKind, string operationKind, string operationType, string targetSymbol) =
                ExpectedAuxiliaryBindingShape(expectation.Id);
            if (anchor.Path != expectation.Path || anchor.Owner != expectation.Owner ||
                anchor.Member != expectation.Member || anchor.Relation != expectation.Relation ||
                !ContainsCanonicalFragment(anchor.CanonicalSyntax, expectation.CanonicalFragment) ||
                anchor.NodeKind != nodeKind || anchor.OperationKind != operationKind ||
                anchor.OperationType != operationType ||
                (anchor.OperationKind != "Invocation" && anchor.TargetSymbol != ExpectedAuxiliaryTarget(expectation.Id, targetSymbol)) ||
                string.IsNullOrWhiteSpace(anchor.TargetSymbol) ||
                !IsSha256(anchor.SyntaxSha256) ||
                !string.Equals(anchor.SyntaxSha256,
                    Sha256(Encoding.UTF8.GetBytes(anchor.CanonicalSyntax)), StringComparison.Ordinal) ||
                anchor.HasCandidateSymbols || anchor.IsErrorSymbol || anchor.ControlFlowBlock < 0 ||
                !anchor.IsReachable || !string.Equals(anchor.ContainingMember, anchor.Member, StringComparison.Ordinal))
            {
                throw new ExtractionException($"The typed auxiliary anchor '{expectation.Id}' is incomplete or changed.");
            }

            if (anchor.OperationKind == "Invocation")
                ValidateInvocation(anchor.Id, anchor.TargetSymbol, anchor.ReceiverType, anchor.ReceiverSymbol, anchor.ArgumentBindings);
            ValidateAssignmentValue(anchor.Id, anchor.RightHandSideBindings);
            if (anchor.Id.StartsWith("adapter.", StringComparison.Ordinal) &&
                anchor.OperationKind != OperationKind.Invocation.ToString())
            {
                throw new ExtractionException($"The transaction adapter anchor '{anchor.Id}' is not an invocation.");
            }
            if (anchor.Id.StartsWith("tracer.", StringComparison.Ordinal) &&
                anchor.OperationKind is not ("Invocation" or "SimpleAssignment" or nameof(OperationKind.Increment)))
            {
                throw new ExtractionException($"The tracer lifecycle anchor '{anchor.Id}' lost its typed operation shape.");
            }
        }

        ValidateSourceRoute(document.SourceRoute);
        ValidateTransactionProjection(document.TransactionProjection);
        ValidateSynchronousCallables(document.SynchronousCallables);

        ControlFlowIdentity executorFlow = document.ControlFlows.Single(static flow =>
            flow.Id == "executor.processTransactions");
        if (!executorFlow.ReachableBlocks.SequenceEqual([0, 1, 2, 3, 4, 5, 6, 7, 8, 9]) ||
            !executorFlow.Edges.SequenceEqual([
                "0->1", "1->2", "2->3", "3->4", "3->8", "4->5", "4->7", "5->6", "5->7", "6->7", "7->3",
            ], StringComparer.Ordinal) ||
            !executorFlow.NormalExitBlocks.SequenceEqual([8]) || executorFlow.ExceptionalExitBlocks.Length != 0 ||
            !executorFlow.BackEdges.SequenceEqual(["7->3"], StringComparer.Ordinal) ||
            document.Anchors.Single(static anchor => anchor.Id == "executor.process-transaction-call").Binding.ControlFlowBlock != 4 ||
            document.Anchors.Single(static anchor => anchor.Id == "executor.gas-limit-guard").Binding.ControlFlowBlock != 4)
        {
            throw new ExtractionException("fold.iteration.topology: executor CFG must retain the admitted ordered loop, guard, increment, and exit topology.");
        }

        foreach (TypedBinding binding in document.Members.Select(static member => member.Binding)
                     .Concat(document.Anchors.Select(static anchor => anchor.Binding))
                     .Concat(document.ControlFlows.Select(static flow => flow.Binding)))
        {
            ValidateBinding(binding);
        }

        TypedBinding[] invocationBindings = document.Anchors.Select(static anchor => anchor.Binding)
            .Where(static binding => binding.OperationKind == OperationKind.Invocation.ToString())
            .ToArray();
        if (invocationBindings.Length != 9 || invocationBindings.Any(static binding => string.IsNullOrWhiteSpace(binding.SymbolId)))
        {
            throw new ExtractionException("The sequential transaction-fold IR lost typed invocation identities.");
        }

        AnchorIdentity preCommit = document.Anchors.Single(static anchor => anchor is not null && anchor.Id == "block.pre-transaction-commit");
        AnchorIdentity fold = document.Anchors.Single(static anchor => anchor is not null && anchor.Id == "block.transaction-fold-call");
        AnchorIdentity postCommit = document.Anchors.Single(static anchor => anchor is not null && anchor.Id == "block.post-transaction-commit");
        AnchorIdentity transactionsExecuted = document.Anchors.Single(static anchor =>
            anchor is not null && anchor.Id == "block.transactions-executed-signal");
        ValidateTransactionsExecuted(document.TransactionsExecuted, transactionsExecuted,
            document.ControlFlows.Single(static flow => flow.Id == "block.processBlock"));
        if (preCommit.Binding.Position >= fold.Binding.Position || fold.Binding.Position >= transactionsExecuted.Binding.Position ||
            transactionsExecuted.Binding.Position >= postCommit.Binding.Position)
        {
            throw new ExtractionException("The sequential transaction-fold commit order is not source-backed.");
        }

        if (!string.Equals(document.Composition.GeneratedReceiptKernel, ReceiptKernelPath, StringComparison.Ordinal) ||
            !string.Equals(document.Composition.ReceiptRefinement, ReceiptRefinementPath, StringComparison.Ordinal) ||
            !string.Equals(document.Composition.Relation, ReceiptRelation, StringComparison.Ordinal) ||
            document.Composition.RequiredTerminalFields is null ||
            !document.Composition.RequiredTerminalFields.SequenceEqual([
                "State.receipts",
                "State.gasHistory",
                "State.cumulativeReceiptGas",
                "State.headerGasUsed",
                "FinalizationObservation.trace.state",
                "FinalizationObservation.result",
            ], StringComparer.Ordinal) ||
            !IsSha256(document.Composition.GeneratedReceiptKernelSha256) ||
            !IsSha256(document.Composition.ReceiptRefinementSha256) ||
            !ValidateReceiptTerminalSourceClosure(document.Composition.ReceiptTerminalSourceClosure))
        {
            throw new ExtractionException("The sequential transaction-fold receipt composition identity changed.");
        }
    }

    private static (string NodeKind, string OperationKind, string OperationType, string TargetSymbol)
        ExpectedAuxiliaryBindingShape(string id) =>
        id switch
        {
            "block.receipts-tracer-start" => ("InvocationExpression", "Invocation", "void", "StartNewBlockTrace"),
            "adapter.tx-trace-start" => ("InvocationExpression", "Invocation",
                "Nethermind.Evm.Tracing.ITxTracer", "StartNewTxTrace"),
            "adapter.execute" => ("InvocationExpression", "Invocation",
                "Nethermind.Evm.TransactionProcessing.TransactionResult", "Execute"),
            "adapter.tx-trace-end" => ("InvocationExpression", "Invocation", "void", "EndTxTrace"),
            "tracer.reset-index" => ("SimpleAssignmentExpression", "SimpleAssignment", "int", "_currentIndex"),
            "tracer.reset-receipts" => ("InvocationExpression", "Invocation", "void", "Clear"),
            "tracer.reset-block-gas" => ("InvocationExpression", "Invocation", "void", "Clear"),
            "tracer.reset-receipt-gas" => ("SimpleAssignmentExpression", "SimpleAssignment", "ulong",
                "_cumulativeReceiptGas"),
            "tracer.success-append" => ("InvocationExpression", "Invocation", "void", "Add"),
            "tracer.failure-append" => ("InvocationExpression", "Invocation", "void", "Add"),
            "tracer.receipt-index" => ("SimpleAssignmentExpression", "SimpleAssignment", "int", "Index"),
            "tracer.tx-start-current" => ("SimpleAssignmentExpression", "SimpleAssignment",
                "Nethermind.Core.Transaction", "CurrentTx"),
            "tracer.tx-start-delegate" => ("SimpleAssignmentExpression", "SimpleAssignment",
                "Nethermind.Evm.Tracing.ITxTracer", "_currentTxTracer"),
            "tracer.tx-end-delegate" => ("InvocationExpression", "Invocation", "void", "EndTxTrace"),
            "tracer.tx-end-index" => ("PostIncrementExpression", nameof(OperationKind.Increment), "int", "_currentIndex"),
            "bal.enabled-spec" => ("SimpleAssignmentExpression", "SimpleAssignment", "bool",
                "_blockAccessListsEnabled"),
            "bal.enabled-derived" => ("SimpleAssignmentExpression", "SimpleAssignment", "bool", "Enabled"),
            "parallel.bal-disabled-gate" => ("LogicalNotExpression", "Unary", "bool", "!balManager.Enabled"),
            "parallel.inner-sequential" => ("InvocationExpression", "Invocation",
                "Nethermind.Core.TxReceipt[]", "ProcessTransactions"),
            "parallel.selection" => ("LogicalAndExpression", "Binary", "bool",
                "ExecutionFlags.ParallelExecution&&!block.IsGenesis&&balManager.ParallelExecutionEnabled"),
            "di.base-executor" => ("InvocationExpression", "Invocation", "Autofac.ContainerBuilder", "AddScoped"),
            "di.parallel-decorator" => ("InvocationExpression", "Invocation", "Autofac.ContainerBuilder",
                "AddDecorator"),
            "di.bal-manager" => ("InvocationExpression", "Invocation", "Autofac.ContainerBuilder", "AddScoped"),
            _ => throw new ExtractionException($"Unknown auxiliary anchor '{id}'."),
        };

    private static void ValidateBinding(TypedBinding binding)
    {
        if (binding is null || string.IsNullOrWhiteSpace(binding.Path) || string.IsNullOrWhiteSpace(binding.Owner) ||
            string.IsNullOrWhiteSpace(binding.Member) || string.IsNullOrWhiteSpace(binding.CanonicalSyntax) ||
            !IsSha256(binding.SyntaxSha256) || string.IsNullOrWhiteSpace(binding.NodeKind) ||
            string.IsNullOrWhiteSpace(binding.OperationKind) ||
            string.IsNullOrWhiteSpace(binding.OperationType) || binding.HasCandidateSymbols ||
            !string.Equals(binding.CandidateReason, CandidateReason.None.ToString(), StringComparison.Ordinal) ||
            binding.IsErrorSymbol || !binding.DataFlowSucceeded || binding.Position <= 0 ||
            binding.StartLine <= 0 || binding.StartColumn <= 0 || binding.ControlFlowBlock < -1 ||
            !string.Equals(binding.Member, binding.ContainingMember, StringComparison.Ordinal) ||
            binding.ReadInside is null || binding.WrittenInside is null || binding.ArgumentBindings is null || binding.ReceiverSymbol is null ||
            binding.ReadInside.Any(static name => string.IsNullOrWhiteSpace(name)) ||
            binding.WrittenInside.Any(static name => string.IsNullOrWhiteSpace(name)))
        {
            throw new ExtractionException("A typed source binding is incomplete or unresolved.");
        }
    }

    private static void ValidateSourceRoute(SourceRouteIdentity route)
    {
        if (route is null || route.ExecutorPath != ExecutorPath ||
            route.ExecutorOwner != "BlockProcessor.BlockValidationTransactionsExecutor" ||
            route.ExecutorMember != "ProcessTransactions" || route.ExecutorParameterCount != 4 ||
            route.ExecutorSignature != ExactExecutorSignature || !route.ExecutorIsNonVirtual ||
            route.BalManagerPath != BlockAccessListManagerPath ||
            route.BalEnabledMember != "BlockAccessListManager.Enabled" ||
            route.ParallelDecoratorPath != ParallelExecutorPath ||
            route.BaseRegistration !=
                "IBlockProcessor.IBlockTransactionsExecutor -> BlockProcessor.BlockValidationTransactionsExecutor" ||
            route.DecoratorRegistration !=
                "IBlockProcessor.IBlockTransactionsExecutor -> BlockProcessor.ParallelBlockValidationTransactionsExecutor" ||
            string.IsNullOrWhiteSpace(route.ExecutorSignature))
        {
            throw new ExtractionException("The sequential transaction-fold exact-base source route identity changed.");
        }
    }

    private static bool ValidateReceiptTerminalSourceClosure(ReceiptTerminalSourceClosureIdentity closure) =>
        closure is not null && ReceiptDependencyAudit.MatchesFrozenClosure(closure) && closure.SourcePinsPath == ReceiptSourcePinsPath &&
        IsSha256(closure.SourcePinsSha256) && closure.SourceManifestPath == ReceiptSourceManifestPath &&
        IsSha256(closure.SourceManifestSha256) && closure.CompilerReferenceInventoryPath == CompilerReferenceInventoryPath &&
        closure.CompilerReferenceInventorySha256 == CompilerReferenceInventorySha256 &&
        closure.SourcePinsSchemaVersion == 3 && closure.ManifestSchemaVersion == 9 && closure.ReceiptExtractorVersion == "1.9.2" &&
        SourcePinsMatch(closure.Sources, ExpectedReceiptSourcePins) && closure.BindingSources is not null &&
        closure.BindingSources.All(static source => source is not null && IsSha256(source.Sha256) && !string.IsNullOrWhiteSpace(source.Role)) &&
        closure.BindingSources.Select(static source => source.Path).SequenceEqual(
            Receipt.ReceiptTerminalFoldProfile.BindingSourceRelativePaths, StringComparer.Ordinal) &&
        closure.Artifacts is not null && closure.Artifacts.All(static artifact => artifact is not null && IsSha256(artifact.Sha256)) &&
        closure.Artifacts.Select(static artifact => artifact.Path).SequenceEqual(ReceiptDependencyAudit.ArtifactPaths, StringComparer.Ordinal);

    private static void ValidateManifest(ArtifactManifest manifest, IrDocument document, string irSha256, string leanSha256)
    {
        if (manifest is null || document is null || manifest.SchemaVersion != SchemaVersion || manifest.ExtractorVersion != ExtractorVersion ||
            manifest.Kernel != Kernel || manifest.Sources is null || manifest.Members is null ||
            manifest.AuxiliarySources is null || manifest.SupportSources is null || manifest.CompilerReferences is null || manifest.Anchors is null ||
            manifest.AuxiliaryAnchors is null || manifest.ControlFlows is null || manifest.Composition is null ||
            manifest.SourceRoute is null ||
            manifest.IrSha256 != irSha256 || manifest.LeanSha256 != leanSha256 ||
            manifest.Sources.Any(static source => source is null) ||
            !manifest.Sources.Select(static source => source.Path).SequenceEqual(document.Sources.Select(static source => source.Path), StringComparer.Ordinal) ||
            !manifest.Sources.SequenceEqual(document.Sources) ||
            !manifest.AuxiliarySources.SequenceEqual(document.AuxiliarySources) ||
            !manifest.SupportSources.SequenceEqual(document.SupportSources) ||
            !manifest.CompilerReferences.Equals(document.CompilerReferences) ||
            manifest.Members.Any(static member => member is null) ||
            !manifest.Members.SequenceEqual(document.Members.Select(static member => member.Id), StringComparer.Ordinal) ||
            manifest.Anchors.Any(static anchor => anchor is null) ||
            !manifest.Anchors.SequenceEqual(document.Anchors.Select(static anchor => anchor.Id), StringComparer.Ordinal) ||
            manifest.AuxiliaryAnchors.Any(static anchor => anchor is null) ||
            !manifest.AuxiliaryAnchors.SequenceEqual(document.AuxiliaryAnchors.Select(static anchor => anchor.Id), StringComparer.Ordinal) ||
            manifest.ControlFlows.Any(static flow => flow is null) ||
            !manifest.ControlFlows.SequenceEqual(document.ControlFlows.Select(static flow => flow.Id), StringComparer.Ordinal) ||
            !manifest.SourceRoute.Equals(document.SourceRoute) ||
            !CompositionMatches(manifest.Composition, document.Composition))
        {
            throw new ExtractionException("The sequential transaction-fold source manifest does not match the typed IR.");
        }
    }

    private static bool CompositionMatches(CompositionIdentity actual, CompositionIdentity expected) =>
        actual.GeneratedReceiptKernel == expected.GeneratedReceiptKernel &&
        actual.GeneratedReceiptKernelSha256 == expected.GeneratedReceiptKernelSha256 &&
        actual.ReceiptRefinement == expected.ReceiptRefinement &&
        actual.ReceiptRefinementSha256 == expected.ReceiptRefinementSha256 &&
        actual.Relation == expected.Relation &&
        actual.RequiredTerminalFields is not null && expected.RequiredTerminalFields is not null &&
        actual.RequiredTerminalFields.SequenceEqual(expected.RequiredTerminalFields, StringComparer.Ordinal) &&
        ReceiptTerminalSourceClosureMatches(actual.ReceiptTerminalSourceClosure, expected.ReceiptTerminalSourceClosure);

    private static bool ReceiptTerminalSourceClosureMatches(
        ReceiptTerminalSourceClosureIdentity actual,
        ReceiptTerminalSourceClosureIdentity expected) =>
        actual is not null && expected is not null &&
        actual.SourcePinsPath == expected.SourcePinsPath &&
        actual.SourcePinsSha256 == expected.SourcePinsSha256 &&
        actual.SourceManifestPath == expected.SourceManifestPath &&
        actual.SourceManifestSha256 == expected.SourceManifestSha256 &&
        actual.CompilerReferenceInventoryPath == expected.CompilerReferenceInventoryPath &&
        actual.CompilerReferenceInventorySha256 == expected.CompilerReferenceInventorySha256 &&
        ReceiptPinsMatch(actual.Sources, expected.Sources) &&
        ReceiptPinsMatch(actual.BindingSources, expected.BindingSources) &&
        actual.SourcePinsSchemaVersion == expected.SourcePinsSchemaVersion && actual.ManifestSchemaVersion == expected.ManifestSchemaVersion &&
        actual.ReceiptExtractorVersion == expected.ReceiptExtractorVersion && actual.Artifacts.SequenceEqual(expected.Artifacts);

    private static bool RegexPlaceholder(string source) =>
        source.Contains("sorry", StringComparison.Ordinal) || source.Contains("admit", StringComparison.Ordinal) ||
        source.Contains("axiom", StringComparison.Ordinal) || source.Contains("theorem ", StringComparison.Ordinal) ||
        source.Contains("example ", StringComparison.Ordinal);

    private static T Deserialize<T>(byte[] bytes) => ReceiptDependencyAudit.ReadStrict<T>(bytes);

    private static byte[] Serialize<T>(T value) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions).ReplaceLineEndings("\n") + "\n");

    private static void WriteNewArtifact(string path, byte[] bytes)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ExtractionException($"Artifact path has no parent directory: '{path}'.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory,
            Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void CompareArtifactBytes(string expectedPath, string actualPath, string name)
    {
        if (!File.Exists(actualPath) || !File.ReadAllBytes(expectedPath).AsSpan()
                .SequenceEqual(File.ReadAllBytes(actualPath)))
        {
            throw new ExtractionException($"Checked-in {name} does not match a deterministic re-emission.");
        }
    }

    private static SourceFile FindSource(SourceFile[] sources, string path) =>
        sources.Single(source => source.RelativePath == path);

    private static string OwnerPath(SyntaxNode node) =>
        string.Join(".", node.AncestorsAndSelf().OfType<ClassDeclarationSyntax>()
            .Reverse()
            .Select(static type => type.Identifier.ValueText));

    private static string Canonical(SyntaxNode node)
    {
        if (node is InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax } &&
            node.Parent is ConditionalAccessExpressionSyntax conditional && ReferenceEquals(conditional.WhenNotNull, node))
            node = conditional;
        return string.Concat(node.DescendantTokens().Select(static token => token.Text));
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsSha256(string? value) =>
        value is not null && value.Length == 64 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Normalize(string path, string root) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static void EnsureWithin(string root, string path)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ExtractionException($"Artifact path escaped its output directory: '{path}'.");
        }
    }

    private static bool HasErrorSymbol(ISymbol? symbol)
    {
        if (symbol is null)
        {
            return false;
        }

        if (symbol is IErrorTypeSymbol || symbol.Kind == SymbolKind.ErrorType)
        {
            return true;
        }

        if (symbol is IMethodSymbol method &&
            (HasErrorType(method.ReturnType) || method.Parameters.Any(parameter => HasErrorType(parameter.Type))))
        {
            return true;
        }

        if (symbol is IPropertySymbol property)
        {
            return HasErrorType(property.Type);
        }

        if (symbol is IFieldSymbol field)
        {
            return HasErrorType(field.Type);
        }

        return symbol.ContainingSymbol is not null && HasErrorSymbol(symbol.ContainingSymbol);
    }

    private static bool HasErrorType(ITypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }

        if (type is IErrorTypeSymbol || type.TypeKind == TypeKind.Error)
        {
            return true;
        }

        return type switch
        {
            INamedTypeSymbol named => named.TypeArguments.Any(HasErrorType) ||
                named.ContainingType is not null && HasErrorType(named.ContainingType),
            IArrayTypeSymbol array => HasErrorType(array.ElementType),
            IPointerTypeSymbol pointer => HasErrorType(pointer.PointedAtType),
            _ => false,
        };
    }

    private sealed record ArtifactManifest(
        int SchemaVersion,
        string ExtractorVersion,
        string Kernel,
        SourceIdentity[] Sources,
        AuxiliarySourceIdentity[] AuxiliarySources,
        AuxiliarySourceIdentity[] SupportSources,
        CompilerReferenceClosureIdentity CompilerReferences,
        string[] Members,
        string[] Anchors,
        string[] AuxiliaryAnchors,
        string[] ControlFlows,
        CompositionIdentity Composition,
        SourceRouteIdentity SourceRoute,
        string IrSha256,
        string LeanSha256);
}
