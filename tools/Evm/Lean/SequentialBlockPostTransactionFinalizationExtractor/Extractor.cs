// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Fold = Nethermind.Evm.Lean.SequentialBlockTransactionFoldExtractor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Evm.Lean.SequentialBlockPostTransactionFinalizationExtractor;

/// <summary>Extracts the source-bound normal synchronous post-transaction finalization tail.</summary>
/// <remarks>
/// This package records Roslyn identities and emits a small executable Lean transition. It does
/// not execute BlockProcessor or claim the implementation of roots, rewards, requests, hashes,
/// access lists, persistence, or the calculations performed by opaque hooks. Reachable
/// source-owned helper bodies are still statically closed by the pinned effect/callable audits.
/// </remarks>
internal static partial class Extractor
{
    internal const string BlockProcessorPath =
        "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs";
    internal const string BlockProcessorStandardPath =
        "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.std.cs";
    internal const string BlockProcessorInterfacePath =
        "src/Nethermind/Nethermind.Consensus/Processing/IBlockProcessor.cs";
    internal const string ProcessingOptionsPath =
        "src/Nethermind/Nethermind.Consensus/Processing/ProcessingOptions.cs";
    internal const string TransactionExecutorPath =
        "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockValidationTransactionsExecutor.cs";

    internal const string ArtifactName = "SequentialBlockPostTransactionFinalization";
    internal const string SourcePinsPath =
        "tools/Evm/Lean/SequentialBlockPostTransactionFinalizationExtractor/SOURCE_PINS.json";
    internal const string DefaultOutputPath =
        "tools/Evm/Lean/SequentialBlockPostTransactionFinalizationExtractor/Generated";
    internal const string DefaultLeanPath =
        "tools/Evm/Lean/SequentialBlockPostTransactionFinalizationExtractor/Generated/SequentialBlockPostTransactionFinalization.lean";
    internal const string CompilerReferenceInventoryPath =
        "tools/Evm/Lean/ReceiptTerminalFoldExtractor/COMPILER_REFERENCE_PINS.json";
    internal const string FoldGeneratedPath =
        "tools/Evm/Lean/SequentialBlockTransactionFoldExtractor/Generated/SequentialBlockTransactionFold.lean";
    internal const string FoldRefinementPath =
        "tools/Evm/Lean/SequentialBlockTransactionFoldExtractor/Refinement/SequentialBlockTransactionFold.lean";

    private const int SchemaVersion = 8;
    private const string ExtractorVersion = "1.14.0";
    private const string Kernel =
        "standard exact-base BAL-disabled normal-completion synchronous BlockProcessor post-transaction finalization tail";
    private const int CompilerReferenceCount = 434;

    private static readonly string[] SourcePaths =
    [
        BlockProcessorPath,
        BlockProcessorStandardPath,
        BlockProcessorInterfacePath,
        ProcessingOptionsPath,
        TransactionExecutorPath,
    ];

    private static readonly string[] SourceRoles =
    ["caller", "partial-standard-source", "interface-closure", "flag-closure", "fold-caller-closure"];

    private static readonly string[] IncludedScope =
    [
        "standard exact-base ProcessBlock normal synchronous post-transaction tail",
        "source-bound CommitState(spec, commitRoots:false) and CommitStateAndStorageRoots(spec, commitRoots:true)",
        "EIP-4844 blob-gas guard and synchronous bloom/receipts-root branch",
        "ordered reward, withdrawal, execution-request, tracer, root, account-change, BAL, hash, and receipt-return observables",
        "independent relational event/header-effect specification and generated refinement theorem",
        "explicit elementwise bridge premise to a completed SequentialBlockTransactionFold projection",
        "typed static-layout source-entry adapter and complete ILocalSymbol null-task reaching-definition evidence",
        "source-bound ComputeStateRoot/SetAccountChanges helper writes and global header write audit",
        "typed source-local call closure over every pinned executable declaration, including opaque-hook bodies",
        "typed outer-statement delegate-invocation closure with exact TransactionsExecuted event and normal-path CFG proof",
        "typed source-owned callable-edge closure with fail-closed constructors, dispatch, operators, conversions, and implicit edges",
        "whole-five-pinned-tree typed effect and source-implementation activation ledgers",
        "exact typed CountLogs foreach threshold helper; other source-owned implicit edges fail closed",
        "exact MetricsTimer wrapper and five complete sink declarations bind implicit constructor/getter/disposal callbacks",
        "whole-five-tree typed timer activation roster and recursive source-bearing generic callback rejection",
        "complete ProcessBlock executable-body preservation closes all additional explicit and implicit metadata callback sites",
        "full CFG edge/exit consistency and normal-tail action dominance",
        "typed Roslyn symbol, IOperation, CFG, and dataflow evidence",
    ];

    private static readonly string[] ExcludedScope =
    [
        "actual blob-gas, bloom, receipt-root, state-root, hash, reward, withdrawal, request, and BAL bodies",
        "BAL-enabled execution, background receipt work, rollback, durability, validation, storage, DI, CLR/JIT, and persistence",
        "ProcessOne validation/PostValidation and any downstream package composition",
        "non-standard, simulation, Optimism, Taiko, parallel, or asynchronous processing paths",
        "hook failure behavior beyond the normal-return theorem premise",
    ];

    private static readonly string[] OpenObligations =
    [
        "The source-entry adapter is static-layout-only: a caller must supply runtime exact-base, standard-executor, and BAL-disabled premises.",
        "An adapter must supply a completed SequentialBlockTransactionFold result with elementwise receipt, log, terminal-result, and index projections; no whole-result equality is assumed.",
        "An adapter must relate the supplied block, header, receipts, release spec, world, tracer, and standard handler identities.",
        "Each admitted opaque hook has an explicit normal-return observation; source-owned hook bodies are recursively audited for typed effects and callable edges, while their computations remain outside this theorem.",
        "Every delegate invocation is typed; only the exact TransactionsExecuted Action event is admitted, and source delegate callables are otherwise rejected.",
        "Pinned source-owned constructors, dispatch, operators, conversions, dynamic calls, pointers, and implicit using/foreach/await edges require an exact audit or fail closed.",
        "The five admitted timer sinks have exact whole-type preservation identities; their getters, callbacks, initializers, and timer constructor/disposal bindings cannot introduce source-local re-entry or captures.",
        "External boundaries outside the five pinned trees require explicit no-additional-modeled-effect and normal-return adapter premises; source-owned opaque hooks are closed by the pinned-tree effect and callable audits.",
        "EIP-4844, main-thread account-change, state-root, and synchronous null-task guards are source-bound premises with typed observations.",
        "The normal theorem excludes background receipt work, rollback/durability, validation, storage, DI, and CLR behavior.",
    ];

    private static readonly string[] FoldTerminalFields =
    [
        "outcome",
        "committed",
        "state.receipts[*].index/logs",
        "terminalResults[*]",
        "completedIndices",
        "events.postTransactionCommit",
    ];

    private static readonly string[] FoldPremises =
    [
        "fold.outcome=.completed",
        "fold.committed=true",
        "projectFoldReceipts(fold.state.receipts)=input.receipts (elementwise)",
        "projectFoldReceiptLogs(fold.state.receipts)=input.fold.terminalLogProjection (elementwise)",
        "projectFoldTerminalResults(fold.terminalResults)=input.fold.terminalResults (elementwise)",
        "fold.completedIndices=input.fold.completedIndices (elementwise)",
        "input.fold.terminalReceiptProjection=input.receipts (elementwise)",
        "input.fold.terminalLogProjection=projectInputReceiptLogs(input.receipts) (elementwise)",
        "fold.outcome=.completed -> fold.terminalResults are all .ok (upstream fold theorem)",
        "hasPostTransactionCommit(fold)=true",
    ];

    private static readonly string[] AnchorIds =
    [
        "block.post-transaction-commit",
        "block.blob-gas-guard",
        "block.blob-gas-calculation",
        "block.background-task-null",
        "block.receipts-background-guard",
        "block.sync-blooms",
        "block.sync-receipts-root",
        "block.rewards",
        "block.withdrawals",
        "block.finalization-commit",
        "block.execution-requests",
        "block.end-block-trace",
        "block.storage-roots-commit",
        "block.main-thread-guard",
        "block.account-changes",
        "block.state-root-guard",
        "block.state-root",
        "block.background-result-guard",
        "block.bal-finalization",
        "block.background-finally-guard",
        "block.hash",
        "block.return-receipts",
    ];

    private static readonly string[] AnchorRelations =
    [
        "normal synchronous boundary begins at the second post-transaction CommitState(spec)",
        "EIP-4844 guard selects blob-gas assignment only when enabled",
        "blob-gas calculation receives block.Transactions under the EIP-4844 guard",
        "receipt background task is initialized to null before the guard",
        "false background predicate selects the synchronous receipt arm",
        "synchronous arm calculates blooms before the receipts root",
        "synchronous arm assigns the receipts root from receipts, spec, and block",
        "opaque miner-reward delegate returns normally before withdrawals",
        "opaque withdrawals delegate returns normally before the finalization commit",
        "second post-transaction CommitState remains before execution requests",
        "opaque execution-request delegate returns normally before ending the block trace",
        "normal synchronous arm ends the block trace with accumulateBlockBloom:true",
        "storage-root commit follows EndBlockTrace",
        "main-thread guard controls account-change capture",
        "account changes are captured only on the main processing thread",
        "state-root guard controls ComputeStateRoot",
        "state root is computed only when ShouldComputeStateRoot(header) is true",
        "background result assignment is excluded by the false background premise",
        "BAL finalization is observed before the header hash",
        "finally task-observation guard is unreachable under the false background premise",
        "header hash assignment follows BAL finalization",
        "normal ProcessBlock return exposes the exact receipts array",
    ];

    private static readonly string[] StepIds =
    [
        "post-transaction-commit",
        "blob-gas-if-enabled",
        "receipt-task-null",
        "synchronous-blooms",
        "synchronous-receipts-root",
        "rewards",
        "withdrawals",
        "finalization-commit",
        "execution-requests",
        "end-block-trace-true",
        "storage-roots-commit",
        "main-thread-account-changes",
        "state-root-if-enabled",
        "bal-finalization",
        "header-hash",
        "return-receipts",
    ];

    private static readonly string[] GuardIds =
    [
        "eip4844", "background-receipts", "main-thread", "state-root", "background-result", "background-finally",
    ];

    private static readonly string[] OpaqueDelegateIds =
    [
        "miner-rewards", "withdrawals", "execution-requests", "calculate-blooms", "calculate-receipts-root",
        "end-block-trace", "account-changes", "state-root", "bal-finalization", "header-hash",
    ];

    private static readonly string[] OpaqueDelegateAnchorIds =
    [
        "block.rewards", "block.withdrawals", "block.execution-requests", "block.sync-blooms",
        "block.sync-receipts-root", "block.end-block-trace", "block.account-changes", "block.state-root",
        "block.bal-finalization", "block.hash",
    ];

    private static readonly string[] AnchorSymbolIds =
    [
        "global::Nethermind.Consensus.Processing.BlockProcessor.CommitState(Nethermind.Core.Specs.IReleaseSpec)",
        "global::Nethermind.Core.Specs.IReleaseSpec.IsEip4844Enabled",
        BlobGasValueSymbol,
        "global::Nethermind.Consensus.Processing.BlockProcessor.ProcessBlock.bloomsAndReceiptsRootTask",
        "global::Nethermind.Consensus.Processing.BlockProcessor.ShouldCalculateReceiptsInBackground(Nethermind.Core.TxReceipt[])",
        "global::Nethermind.Consensus.Processing.BlockProcessor.CalculateBlooms(Nethermind.Core.TxReceipt[])",
        "global::Nethermind.Consensus.Processing.BlockProcessor.CalculateReceiptsRoot(Nethermind.Core.TxReceipt[],Nethermind.Core.Specs.IReleaseSpec,Nethermind.Core.Block)",
        "global::Nethermind.Consensus.Processing.BlockProcessor.ApplyMinerRewards(Nethermind.Core.Block,Nethermind.Evm.Tracing.IBlockTracer,Nethermind.Core.Specs.IReleaseSpec)",
        "global::Nethermind.Consensus.Withdrawals.IWithdrawalProcessor.ProcessWithdrawals(Nethermind.Core.Block,Nethermind.Core.Specs.IReleaseSpec)",
        "global::Nethermind.Consensus.Processing.BlockProcessor.CommitState(Nethermind.Core.Specs.IReleaseSpec)",
        "global::Nethermind.Consensus.ExecutionRequests.IExecutionRequestsProcessor.ProcessExecutionRequests(Nethermind.Core.Block,Nethermind.Evm.State.IWorldState,Nethermind.Core.TxReceipt[],Nethermind.Core.Specs.IReleaseSpec)",
        "global::Nethermind.Blockchain.Tracing.BlockReceiptsTracer.EndBlockTrace(bool)",
        "global::Nethermind.Consensus.Processing.BlockProcessor.CommitStateAndStorageRoots(Nethermind.Core.Specs.IReleaseSpec)",
        "global::Nethermind.Consensus.Processing.BlockchainProcessor.IsMainProcessingThread",
        "global::Nethermind.Consensus.Processing.BlockProcessor.SetAccountChanges(Nethermind.Core.Block)",
        "global::Nethermind.Consensus.Processing.BlockProcessor.ShouldComputeStateRoot(Nethermind.Core.BlockHeader)",
        "global::Nethermind.Consensus.Processing.BlockProcessor.ComputeStateRoot(Nethermind.Core.BlockHeader)",
        TaskLocalSymbol,
        "global::Nethermind.Consensus.Processing.IBlockAccessListManager.SetBlockAccessList(Nethermind.Core.Block)",
        TaskLocalSymbol,
        "global::Nethermind.Core.BlockHeader.Hash",
        "global::Nethermind.Consensus.Processing.BlockProcessor.ProcessBlock(Nethermind.Core.Block,Nethermind.Evm.Tracing.IBlockTracer,Nethermind.Consensus.Processing.ProcessingOptions,Nethermind.Core.Specs.IReleaseSpec,System.Threading.CancellationToken)",
    ];

    private static readonly string[] AnchorOperationKinds =
    [
        "Invocation", "PropertyReference", "Invocation", "VariableDeclarator", "Invocation", "Invocation", "Invocation",
        "Invocation", "Invocation", "Invocation", "Invocation", "Invocation", "Invocation", "PropertyReference", "Invocation",
        "Invocation", "Invocation", "IsPattern", "Invocation", "IsPattern", "SimpleAssignment", "Return",
    ];

    private static readonly string[] AnchorNodeKinds =
    [
        "InvocationExpression", "SimpleMemberAccessExpression", "InvocationExpression", "VariableDeclarator",
        "InvocationExpression", "InvocationExpression", "InvocationExpression", "InvocationExpression",
        "InvocationExpression", "InvocationExpression", "InvocationExpression", "InvocationExpression",
        "InvocationExpression", "SimpleMemberAccessExpression", "InvocationExpression", "InvocationExpression",
        "InvocationExpression", "IsPatternExpression", "InvocationExpression", "IsPatternExpression",
        "SimpleAssignmentExpression", "ReturnStatement",
    ];

    private static readonly string[] AnchorOperationTypes =
    [
        "void", "bool", "ulong", "System.Threading.Tasks.Task<(Nethermind.Core.Bloom BlockBloom, Nethermind.Core.Crypto.Hash256 ReceiptsRoot)>", "bool", "void", "Nethermind.Core.Crypto.Hash256",
        "void", "void", "void", "void", "void", "void", "bool", "void", "bool", "void", "bool", "void", "bool",
        "Nethermind.Core.Crypto.Hash256", "Nethermind.Core.TxReceipt[]",
    ];

    private static readonly string[] AnchorSymbolKinds =
    [
        "Method", "Property", "Method", "Local", "Method", "Method", "Method", "Method", "Method", "Method",
        "Method", "Method", "Method", "Property", "Method", "Method", "Method", "Local", "Method", "Local",
        "Property", "Method",
    ];

    private static readonly string[] CommitEventIds =
    [
        "commitNoRoots(0)", "commitNoRoots(1)", "commitRoots",
    ];

    private static readonly string[] HeaderAssignmentIds =
    [
        "header.blob-gas-used", "header.receipts-root",
    ];

    private static readonly string[] HeaderAssignmentAnchors =
    [
        "block.blob-gas-calculation", "block.sync-receipts-root",
    ];

    private static readonly string[] HeaderAssignmentGuardAnchors =
    [
        "block.blob-gas-guard", "block.receipts-background-guard",
    ];

    private static readonly string[] HeaderAssignmentArms =
    [
        "true-arm", "false-arm",
    ];

    private static readonly string[] HeaderAssignmentTargets =
    [
        "header.BlobGasUsed", "header.ReceiptsRoot",
    ];

    private static readonly string[] HeaderAssignmentValues =
    [
        "BlobGasCalculator.CalculateBlobGas(block.Transactions)",
        "CalculateReceiptsRoot(receipts,spec,block)",
    ];

    private const string BackgroundHeaderAssignment =
        "(header.Bloom,header.ReceiptsRoot)=bloomsAndReceiptsRootTask.GetAwaiter().GetResult()";

    private const string BlobGasPropertySymbol = "global::Nethermind.Core.BlockHeader.BlobGasUsed";
    private const string ReceiptsRootPropertySymbol = "global::Nethermind.Core.BlockHeader.ReceiptsRoot";
    private const string BlobGasValueSymbol =
        "global::Nethermind.Evm.BlobGasCalculator.CalculateBlobGas(Nethermind.Core.Transaction[])";
    private const string ReceiptsRootValueSymbol =
        "global::Nethermind.Consensus.Processing.BlockProcessor.CalculateReceiptsRoot(Nethermind.Core.TxReceipt[],Nethermind.Core.Specs.IReleaseSpec,Nethermind.Core.Block)";
    private const string BackgroundPredicateSymbol =
        "global::Nethermind.Consensus.Processing.BlockProcessor.ShouldCalculateReceiptsInBackground(Nethermind.Core.TxReceipt[])";
    private const string BloomsSymbol =
        "global::Nethermind.Consensus.Processing.BlockProcessor.CalculateBlooms(Nethermind.Core.TxReceipt[])";
    private const string RequestsSymbol =
        "global::Nethermind.Consensus.ExecutionRequests.IExecutionRequestsProcessor.ProcessExecutionRequests(Nethermind.Core.Block,Nethermind.Evm.State.IWorldState,Nethermind.Core.TxReceipt[],Nethermind.Core.Specs.IReleaseSpec)";

    private const string CommitStateMethodSymbol =
        "global::Nethermind.Consensus.Processing.BlockProcessor.CommitState(Nethermind.Core.Specs.IReleaseSpec)";
    private const string CommitRootsMethodSymbol =
        "global::Nethermind.Consensus.Processing.BlockProcessor.CommitStateAndStorageRoots(Nethermind.Core.Specs.IReleaseSpec)";
    private const string ProcessBlockMethodSymbol =
        "global::Nethermind.Consensus.Processing.BlockProcessor.ProcessBlock(Nethermind.Core.Block,Nethermind.Evm.Tracing.IBlockTracer,Nethermind.Consensus.Processing.ProcessingOptions,Nethermind.Core.Specs.IReleaseSpec,System.Threading.CancellationToken)";
    private const string ComputeStateRootMethodSymbol =
        "global::Nethermind.Consensus.Processing.BlockProcessor.ComputeStateRoot(Nethermind.Core.BlockHeader)";
    private const string SetAccountChangesMethodSymbol =
        "global::Nethermind.Consensus.Processing.BlockProcessor.SetAccountChanges(Nethermind.Core.Block)";
    private const string CountLogsMethodSymbol =
        "global::Nethermind.Consensus.Processing.BlockProcessor.CountLogs(Nethermind.Core.TxReceipt[])";
    private const string CreateBlockExecutionContextMethodSymbol =
        "global::Nethermind.Consensus.Processing.BlockProcessor.CreateBlockExecutionContext(Nethermind.Core.BlockHeader,Nethermind.Core.Specs.IReleaseSpec)";
    private const string ExecutorProcessTransactionsMethodSymbol =
        "global::Nethermind.Consensus.Processing.IBlockProcessor.IBlockTransactionsExecutor.ProcessTransactions(Nethermind.Core.Block,Nethermind.Consensus.Processing.ProcessingOptions,Nethermind.Blockchain.Tracing.BlockReceiptsTracer,System.Threading.CancellationToken)";
    private const string ExecutorSetContextMethodSymbol =
        "global::Nethermind.Consensus.Processing.IBlockProcessor.IBlockTransactionsExecutor.SetBlockExecutionContext(Nethermind.Evm.BlockExecutionContext&)";
    private const string ExecutorSetContextCanonical =
        "_blockTransactionsExecutor.SetBlockExecutionContext(CreateBlockExecutionContext(block.Header,spec))";
    private const string CreateBlockExecutionContextCanonical =
        "CreateBlockExecutionContext(block.Header,spec)";
    private const string BlockProcessorTypeSymbol =
        "global::Nethermind.Consensus.Processing.BlockProcessor";
    private const string DirectCommitSymbol =
        "global::Nethermind.Evm.State.WorldStateExtensions.Commit(Nethermind.Evm.State.IWorldState,Nethermind.Core.Specs.IReleaseSpec,bool,bool)";
    private const string DirectInterfaceCommitSymbol =
        "global::Nethermind.Evm.State.IWorldState.Commit(Nethermind.Core.Specs.IReleaseSpec,Nethermind.Evm.Tracing.State.IWorldStateTracer,bool,bool)";
    private const string TransactionsExecutedEventName = "TransactionsExecuted";
    private const string SystemActionTypeSymbol = "global::System.Action";
    private const string ParallelUnbalancedWorkTypeSymbol =
        "global::Nethermind.Core.Threading.ParallelUnbalancedWork";
    private const string TransactionsExecutedInvokeSymbol = "global::System.Action.Invoke()";
    private const string TransactionsExecutedEventSymbol =
        "global::Nethermind.Consensus.Processing.BlockProcessor.TransactionsExecuted";

    // Calculations remain abstract normal-return/guard observations. Preservation is checked
    // across their pinned local bodies and cannot be delegated to an external-hook premise.
    private const string ApplyMinerRewardsMethodSymbol =
        "global::Nethermind.Consensus.Processing.BlockProcessor.ApplyMinerRewards(Nethermind.Core.Block,Nethermind.Evm.Tracing.IBlockTracer,Nethermind.Core.Specs.IReleaseSpec)";
    private const string CalculateBloomsMethodSymbol =
        "global::Nethermind.Consensus.Processing.BlockProcessor.CalculateBlooms(Nethermind.Core.TxReceipt[])";
    private const string CalculateReceiptsRootMethodSymbol =
        "global::Nethermind.Consensus.Processing.BlockProcessor.CalculateReceiptsRoot(Nethermind.Core.TxReceipt[],Nethermind.Core.Specs.IReleaseSpec,Nethermind.Core.Block)";
    private const string AccumulateBlockBloomMethodSymbol =
        "global::Nethermind.Consensus.Processing.BlockProcessor.AccumulateBlockBloom(Nethermind.Core.TxReceipt[])";

    private const string StateRootPropertySymbol = "global::Nethermind.Core.BlockHeader.StateRoot";
    private const string StateRootValueSymbol = "global::Nethermind.Evm.State.IReadOnlyStateProvider.StateRoot";
    private const string AccountChangesPropertySymbol = "global::Nethermind.Core.Block.AccountChanges";
    private const string AccountChangesValueSymbol =
        "global::Nethermind.Evm.State.IWorldState.GetAccountChanges()";
    private const string HashPropertySymbol = "global::Nethermind.Core.BlockHeader.Hash";
    private const string BloomPropertySymbol = "global::Nethermind.Core.BlockHeader.Bloom";
    private const string PostValidationMethodSymbol =
        "global::Nethermind.Consensus.Processing.BlockProcessor.PostValidation(Nethermind.Core.Block,Nethermind.Core.Block,Nethermind.Core.TxReceipt[],Nethermind.Consensus.Processing.ProcessingOptions)";
    private const string PrepareBlockForProcessingMethodSymbol =
        "global::Nethermind.Consensus.Processing.BlockProcessor.PrepareBlockForProcessing(Nethermind.Core.Block)";

    // This is a source-wide, not merely ProcessBlock-reachable, effect baseline.  It deliberately
    // retains the non-tail copy/prepare writes and the opaque reward-tracer commit: a new pinned
    // declaration cannot hide a modeled write or direct state commit behind an otherwise opaque edge.
    private static readonly PinnedEffectLedgerEntry[] PinnedEffectLedger =
    [
        new(BlockProcessorPath, PostValidationMethodSymbol, AccountChangesPropertySymbol, "property-write",
            "suggestedBlock.AccountChanges=processedBlock.AccountChanges", 1),
        new(BlockProcessorPath, ProcessBlockMethodSymbol, BlobGasPropertySymbol, "property-write",
            "header.BlobGasUsed=BlobGasCalculator.CalculateBlobGas(block.Transactions)", 1),
        new(BlockProcessorPath, ProcessBlockMethodSymbol, ReceiptsRootPropertySymbol, "property-write",
            "header.ReceiptsRoot=CalculateReceiptsRoot(receipts,spec,block)", 1),
        new(BlockProcessorPath, ProcessBlockMethodSymbol, BloomPropertySymbol, "property-write",
            BackgroundHeaderAssignment, 1),
        new(BlockProcessorPath, ProcessBlockMethodSymbol, ReceiptsRootPropertySymbol, "property-write",
            BackgroundHeaderAssignment, 1),
        new(BlockProcessorPath, ProcessBlockMethodSymbol, HashPropertySymbol, "property-write",
            "header.Hash=header.CalculateHash()", 1),
        new(BlockProcessorPath, CommitStateMethodSymbol, DirectCommitSymbol, "world-state-commit",
            "_stateProvider.Commit(spec,commitRoots:false)", 1),
        new(BlockProcessorPath, CommitRootsMethodSymbol, DirectCommitSymbol, "world-state-commit",
            "_stateProvider.Commit(spec,commitRoots:true)", 1),
        new(BlockProcessorPath, ComputeStateRootMethodSymbol, StateRootPropertySymbol, "property-write",
            "header.StateRoot=_stateProvider.StateRoot", 1),
        new(BlockProcessorPath, SetAccountChangesMethodSymbol, AccountChangesPropertySymbol, "property-write",
            "block.AccountChanges=_stateProvider.GetAccountChanges()", 1),
        new(BlockProcessorPath, PrepareBlockForProcessingMethodSymbol, StateRootPropertySymbol, "property-write",
            "headerForProcessing.StateRoot=bh.StateRoot", 1),
        new(BlockProcessorPath, ApplyMinerRewardsMethodSymbol, DirectInterfaceCommitSymbol, "world-state-commit",
            "_stateProvider.Commit(spec,txTracer)", 1),
    ];

    // There are no baseline property/indexer/event routes whose typed target has an executable
    // implementation in these five source trees.  The empty ledger is intentional: a source
    // implementation behind either a source or metadata interface is an executable activation
    // edge and must be modeled explicitly instead of being treated as an external accessor.
    private static readonly PinnedActivationLedgerEntry[] PinnedActivationLedger = [];

    private static readonly string[] ReflectionOrReentryTypePrefixes =
    [
        "global::System.Reflection.",
        "global::System.Linq.Expressions.",
    ];

    private const string ReceiptsLocal = "receipts";
    private const string ReceiptsInitializerSyntax =
        "receipts=_blockTransactionsExecutor.ProcessTransactions(block,options,ReceiptsTracer,token)";
    private const string ReceiptsLocalSymbol =
        "global::Nethermind.Consensus.Processing.BlockProcessor.ProcessBlock.receipts";
    private const string ReceiptsOperationType = "Nethermind.Core.TxReceipt[]";
    private const string ReceiptsRootOperationType = "Nethermind.Core.Crypto.Hash256";
    private const string TaskLocalSymbol =
        "global::Nethermind.Consensus.Processing.BlockProcessor.ProcessBlock.bloomsAndReceiptsRootTask";
    private const string AccumulateBlockBloomSymbol =
        "global::Nethermind.Consensus.Processing.BlockProcessor.AccumulateBlockBloom(Nethermind.Core.TxReceipt[])";
    private const int ReceiptsReferenceCount = 8;
    private const int ReceiptsWriteCount = 1;

    private static readonly string[] MemberSignatures =
    [
        "global::Nethermind.Consensus.Processing.BlockProcessor.ProcessBlock(Nethermind.Core.Block,Nethermind.Evm.Tracing.IBlockTracer,Nethermind.Consensus.Processing.ProcessingOptions,Nethermind.Core.Specs.IReleaseSpec,System.Threading.CancellationToken)",
        "global::Nethermind.Consensus.Processing.BlockProcessor.CommitState(Nethermind.Core.Specs.IReleaseSpec)",
        "global::Nethermind.Consensus.Processing.BlockProcessor.CommitStateAndStorageRoots(Nethermind.Core.Specs.IReleaseSpec)",
        "global::Nethermind.Consensus.Processing.BlockProcessor.ComputeStateRoot(Nethermind.Core.BlockHeader)",
        "global::Nethermind.Consensus.Processing.BlockProcessor.SetAccountChanges(Nethermind.Core.Block)",
    ];

    private static readonly string[] UnconditionalTailAnchorIds =
    [
        "block.post-transaction-commit", "block.background-task-null", "block.rewards", "block.withdrawals",
        "block.finalization-commit", "block.execution-requests", "block.end-block-trace",
        "block.storage-roots-commit", "block.bal-finalization", "block.hash", "block.return-receipts",
    ];

    private static readonly string[] GuardConditions =
    [
        "spec.IsEip4844Enabled", "ShouldCalculateReceiptsInBackground(receipts)",
        "BlockchainProcessor.IsMainProcessingThread", "ShouldComputeStateRoot(header)",
        TaskBackgroundResultPredicate, TaskFinallyPredicate,
    ];

    private static readonly string[] GuardPolarities =
    ["true-arm", "false-arm", "true-arm", "true-arm", "false-arm", "false-arm"];

    private static readonly string[] GuardSelectedArms =
    ["blob-gas", "synchronous-blooms-and-root", "account-changes", "state-root", "none", "none"];

    private static readonly string[] GuardExcludedArms =
    ["none", "Task.Run", "none", "none", "GetResult assignment", "task observation"];

    private static readonly string[] StepAnchorIds =
    [
        "block.post-transaction-commit", "block.blob-gas-calculation", "block.background-task-null",
        "block.sync-blooms", "block.sync-receipts-root", "block.rewards", "block.withdrawals",
        "block.finalization-commit", "block.execution-requests", "block.end-block-trace",
        "block.storage-roots-commit", "block.account-changes", "block.state-root", "block.bal-finalization",
        "block.hash", "block.return-receipts",
    ];

    private static readonly string[] StepBranches =
    [
        "always", "spec.IsEip4844Enabled", "always", "background=false", "background=false", "normal-return",
        "normal-return", "always", "normal-return", "background=false", "always", "main-thread=true",
        "state-root=true", "BAL-disabled", "normal-return", "normal-return",
    ];

    private static readonly string[] StepObservables =
    [
        "commit-no-roots", "header.BlobGasUsed", "background-task=null", "receipt-blooms", "header.ReceiptsRoot",
        "rewards-hook", "withdrawals-hook", "commit-no-roots", "execution-requests-hook", "EndBlockTrace(true)",
        "commit-roots", "account-changes", "state-root", "BAL-finalization-observation", "header.Hash", "return receipts",
    ];

    private const string SourceEntryAdapterId = "block.processBlock.standard-exact-base-adapter";
    private const string SourceEntryAdapterClaim =
        "static-layout-only; runtime caller supplies exact-base receiver, selected standard executor, BAL-disabled, named external normal-return/no-additional-effect premises, and separate TransactionsExecuted/post-transaction-CommitState normal-return premises";
    private const string SourceEntryExactBaseReceiver = "BlockProcessor.ProcessBlock exact base receiver";
    private const string SourceEntrySelectedExecutor =
        "_blockTransactionsExecutor.ProcessTransactions(block,options,ReceiptsTracer,token)";
    private const string SourceEntryBalPremise = "runtime premise: BAL disabled";
    private const string SourceEntryBackgroundPremise =
        "runtime premise: ShouldCalculateReceiptsInBackground(receipts)=false";
    private const string SourceEntryMainThreadPremise =
        "runtime premise: BlockchainProcessor.IsMainProcessingThread is selected as observed";
    private const string SourceEntryStateRootPremise =
        "runtime premise: ShouldComputeStateRoot(header) is selected as observed";
    private const string SourceEntrySpecPremise =
        "runtime premise: spec is the release-spec receiver used by the selected standard path";
    private const string SourceEntryTransactionsExecutedNormalReturnPremise =
        "runtime premise: TransactionsExecuted subscribers return normally";
    private const string SourceEntryPostTransactionCommitNormalReturnPremise =
        "runtime premise: post-transaction CommitState(spec) returns normally";
    private const string BridgeRelation =
        "elementwise projection bridge: a completed SequentialBlockTransactionFold FoldResult supplies receipt/log/terminal-result/index projections; it is not a whole-result equality premise";

    private const string TaskVariable = "bloomsAndReceiptsRootTask";
    private const string TaskInitializer = "bloomsAndReceiptsRootTask=null";
    private const string TaskSynchronousArm = "false background arm has no task assignment";
    private const string TaskEndTracePredicate = "bloomsAndReceiptsRootTaskisnull";
    private const string TaskBackgroundResultPredicate = "bloomsAndReceiptsRootTaskisnotnull";
    private const string TaskFinallyPredicate = "bloomsAndReceiptsRootTaskis{IsCompletedSuccessfully:false}";

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

    internal static void ValidateIrForTest(IrDocument document) => ValidateIr(document);

    internal static byte[] EmitLeanForTest(IrDocument document)
    {
        ValidateIr(document);
        return LeanEmitter.Emit(document);
    }

    internal static void ValidateIrForEmitter(IrDocument document) => ValidateIr(document);

    internal static void RequireCompilationForTest(string root, IReadOnlyDictionary<string, byte[]> overrides)
    {
        SourceFile[] sources = ReadSources(root, ReadPins(root), overrides, false, allowAliases: true, enforceSourceSelection: false);
        _ = ReadCompilerContext(root, sources, validateCompilation: true);
    }

    internal static void ValidateCheckedIn(string repoRoot, string? outputDirectory) =>
        ValidateCheckedInCore(repoRoot, outputDirectory, null, null);

    internal static void ValidateCheckedInWithSourceOverridesForTest(string repoRoot, string outputDirectory,
        IReadOnlyDictionary<string, byte[]> overrides, SourcePinDocument pins) =>
        ValidateCheckedInCore(repoRoot, outputDirectory, overrides, pins);

    private static void ValidateCheckedInCore(string repoRoot, string? outputDirectory,
        IReadOnlyDictionary<string, byte[]>? sourceOverrides, SourcePinDocument? pinOverrides)
    {
        string root = Path.GetFullPath(repoRoot);
        DependencyAudit.ValidateUpstream(root);
        SourcePinDocument pins = ReadPins(root, pinOverrides);
        SourceFile[] sources = ReadSources(root, pins, sourceOverrides, true);
        CompilerContext compiler = ReadCompilerContext(root, sources, validateCompilation: true);
        CompilerClosureIdentity closure = compiler.Closure;
        BridgeIdentity bridge = BuildBridge(root);
        string output = Path.GetFullPath(outputDirectory ?? Path.Combine(root, DefaultOutputPath));
        string irPath = Path.Combine(output, ArtifactName + ".ir.json");
        string manifestPath = Path.Combine(output, ArtifactName + ".source-manifest.json");
        string leanPath = Path.Combine(output, ArtifactName + ".lean");
        if (!File.Exists(irPath) || !File.Exists(manifestPath) || !File.Exists(leanPath))
        {
            throw new ExtractionException("Checked-in post-transaction finalization artifacts are incomplete.");
        }

        IrDocument document = Deserialize<IrDocument>(File.ReadAllBytes(irPath));
        ValidateIr(document);
        _ = ReadInstrumentationWrapper(root);
        if (!document.Sources.SequenceEqual(sources.Select(static source =>
                new SourceIdentity(source.RelativePath, source.Role, source.Sha256, source.SyntaxSha256))) ||
            !CompilerClosureMatches(document.CompilerClosure, closure) || !BridgeMatches(document.Bridge, bridge))
        {
            throw new ExtractionException("Checked-in post-transaction finalization source identities drifted.");
        }

        IrDocument rederived = DeriveDocument(root, sources, compiler);
        if (!Serialize(document).AsSpan().SequenceEqual(Serialize(rederived)))
            throw new ExtractionException("Checked-in post-transaction finalization IR differs from live source admission.");

        ArtifactManifest manifest = Deserialize<ArtifactManifest>(File.ReadAllBytes(manifestPath));
        ValidateManifest(manifest, document, Sha256(File.ReadAllBytes(irPath)), Sha256(File.ReadAllBytes(leanPath)));
        if (!File.ReadAllBytes(leanPath).AsSpan().SequenceEqual(LeanEmitter.Emit(document)))
            throw new ExtractionException("Generated finalization Lean differs from exact validated IR re-emission.");
        DependencyAudit.Validate(root, manifest.Dependencies);
        string lean = File.ReadAllText(leanPath);
        if (ContainsProofPlaceholder(lean))
        {
            throw new ExtractionException("Generated post-transaction finalization Lean contains a proof placeholder.");
        }
    }

    private static ExtractionResult ExtractCore(
        string root,
        string outputDirectory,
        string? leanOutputPath,
        IReadOnlyDictionary<string, byte[]>? sourceOverrides,
        bool enforcePins)
    {
        if (enforcePins) DependencyAudit.ValidateUpstream(root);
        SourcePinDocument pins = ReadPins(root);
        SourceFile[] sources = ReadSources(root, pins, sourceOverrides, enforcePins);
        CompilerContext compiler = ReadCompilerContext(root, sources, validateCompilation: true);
        return PublishDocument(root, outputDirectory, leanOutputPath, DeriveDocument(root, sources, compiler));
    }

    private static IrDocument DeriveDocument(string root, SourceFile[] sources, CompilerContext compiler)
    {
        SourceFile blockSource = FindSource(sources, BlockProcessorPath);
        SemanticModel blockModel = compiler.Compilation.GetSemanticModel(blockSource.Tree, ignoreAccessibility: true);
        ClassDeclarationSyntax blockClass = FindClass(blockSource, "BlockProcessor");
        MethodDeclarationSyntax processBlock = FindMethod(blockSource, blockClass, "ProcessBlock", 5);
        MethodDeclarationSyntax commitState = FindMethod(blockSource, blockClass, "CommitState", 1);
        MethodDeclarationSyntax commitRoots = FindMethod(blockSource, blockClass, "CommitStateAndStorageRoots", 1);
        MethodDeclarationSyntax computeStateRoot = FindMethod(blockSource, blockClass, "ComputeStateRoot", 1);
        MethodDeclarationSyntax setAccountChanges = FindMethod(blockSource, blockClass, "SetAccountChanges", 1);

        MemberIdentity[] members =
        [
            BindMethod("block.processBlock", blockSource, processBlock, blockModel, compiler.Compilation),
            BindMethod("block.commit-no-roots", blockSource, commitState, blockModel, compiler.Compilation),
            BindMethod("block.commit-roots", blockSource, commitRoots, blockModel, compiler.Compilation),
            BindMethod("block.compute-state-root", blockSource, computeStateRoot, blockModel, compiler.Compilation),
            BindMethod("block.set-account-changes", blockSource, setAccountChanges, blockModel, compiler.Compilation),
        ];

        List<AnchorIdentity> anchors = BuildAnchors(blockSource, processBlock, blockModel, compiler.Compilation,
            out SourceLocalMethod[] helperClosure);
        HeaderAssignmentIdentity[] headerAssignments = BuildHeaderAssignments(
            blockSource, processBlock, blockModel, compiler.Compilation, anchors);
        ControlFlowIdentity[] flows =
        [
            BuildControlFlow("block.processBlock", blockSource, processBlock, blockModel, compiler.Compilation),
            BuildControlFlow("block.commit-no-roots", blockSource, commitState, blockModel, compiler.Compilation),
            BuildControlFlow("block.commit-roots", blockSource, commitRoots, blockModel, compiler.Compilation),
            BuildControlFlow("block.compute-state-root", blockSource, computeStateRoot, blockModel, compiler.Compilation),
            BuildControlFlow("block.set-account-changes", blockSource, setAccountChanges, blockModel, compiler.Compilation),
        ];

        AnchorIdentity postCommit = Anchor(anchors, "block.post-transaction-commit");
        AnchorIdentity finalizationCommit = Anchor(anchors, "block.finalization-commit");
        InvocationExpressionSyntax noRootsCall = SingleInvocation(commitState,
            "_stateProvider.Commit(spec,commitRoots:false)", "CommitState underlying commit");
        InvocationExpressionSyntax rootsCall = SingleInvocation(commitRoots,
            "_stateProvider.Commit(spec,commitRoots:true)", "CommitStateAndStorageRoots underlying commit");
        CommitIdentity[] commits =
        [
            new("post-transaction-no-roots", postCommit.Id, "commitNoRoots(0)", "block.commit-no-roots", false,
                Canonical(noRootsCall), BindAnchorNode("post-transaction-no-roots", blockSource, commitState,
                    noRootsCall, blockModel, compiler.Compilation, "distinct no-root commit implementation").Binding,
                postCommit.Binding),
            new("finalization-no-roots", finalizationCommit.Id, "commitNoRoots(1)", "block.commit-no-roots", false,
                Canonical(noRootsCall), BindAnchorNode("finalization-no-roots", blockSource, commitState,
                    noRootsCall, blockModel, compiler.Compilation, "distinct no-root commit implementation").Binding,
                finalizationCommit.Binding),
            new("storage-roots", "block.storage-roots-commit", "commitRoots", "block.commit-roots", true,
                Canonical(rootsCall), BindAnchorNode("storage-roots", blockSource, commitRoots,
                    rootsCall, blockModel, compiler.Compilation, "distinct roots commit implementation").Binding,
                Anchor(anchors, "block.storage-roots-commit").Binding),
        ];

        GuardIdentity[] guards = BuildGuards(anchors);
        StepIdentity[] steps = BuildSteps(anchors);
        OpaqueDelegateIdentity[] delegates = BuildOpaqueDelegates(anchors);
        ValidatePinnedTreeEffectBoundary(sources, compiler.Compilation);
        SourceEntryAdapterIdentity sourceEntryAdapter = BuildSourceEntryAdapter(
            blockSource, processBlock, computeStateRoot, setAccountChanges, blockModel, compiler.Compilation, members, anchors);
        TaskFlowIdentity taskFlow = BuildTaskFlow(blockSource, processBlock, blockModel, compiler.Compilation, anchors);
        ValidatePinnedPreservationEffects(sources, compiler.Compilation);
        HelperPreservationIdentity[] helperPreservation = BuildHelperPreservation(helperClosure);
        InstrumentationBoundaryIdentity instrumentation = BuildInstrumentationBoundary(root, blockSource,
            processBlock, blockModel, helperClosure, compiler.Compilation);
        HelperPreservationIdentity entryPreservation = BuildEntryPreservation(processBlock);
        BridgeIdentity bridge = BuildBridge(root);
        SourceIdentity[] sourceIdentities = sources.Select(static source =>
            new SourceIdentity(source.RelativePath, source.Role, source.Sha256, source.SyntaxSha256)).ToArray();
        IrDocument document = new(
            SchemaVersion,
            ExtractorVersion,
            Kernel,
            IncludedScope,
            ExcludedScope,
            sourceIdentities,
            compiler.Closure,
            members,
            anchors.ToArray(),
            flows,
            commits,
            guards,
            steps,
            headerAssignments,
            delegates,
            bridge,
            [sourceEntryAdapter],
            taskFlow,
            entryPreservation,
            helperPreservation,
            instrumentation,
            OpenObligations);

        ValidateIr(document);
        return document;
    }

    private static ExtractionResult PublishDocument(string root, string outputDirectory, string? leanOutputPath, IrDocument document)
    {
        byte[] irBytes = Serialize(document);
        IrDocument roundTripped = Deserialize<IrDocument>(irBytes);
        ValidateIr(roundTripped);
        byte[] leanBytes = LeanEmitter.Emit(roundTripped);

        string output = Path.GetFullPath(outputDirectory);
        string irPath = Path.Combine(output, ArtifactName + ".ir.json");
        string manifestPath = Path.Combine(output, ArtifactName + ".source-manifest.json");
        string leanPath = Path.GetFullPath(leanOutputPath ?? Path.Combine(output, ArtifactName + ".lean"));
        EnsureWithin(output, irPath);
        EnsureWithin(output, manifestPath);
        EnsureWithin(output, leanPath);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(Path.GetDirectoryName(leanPath)!);
        ArtifactManifest manifest = new(
            SchemaVersion,
            ExtractorVersion,
            Kernel,
            roundTripped.Sources,
            roundTripped.CompilerClosure,
            roundTripped.Members.Select(static member => member.Id).ToArray(),
            roundTripped.Anchors.Select(static anchor => anchor.Id).ToArray(),
            roundTripped.ControlFlows.Select(static flow => flow.Id).ToArray(),
            roundTripped.Commits.Select(static commit => commit.Id).ToArray(),
            roundTripped.Steps.Select(static step => step.Id).ToArray(),
            roundTripped.HeaderAssignments.Select(static assignment => assignment.Id).ToArray(),
            roundTripped.SourceEntryAdapters.Select(static adapter => adapter.Id).ToArray(),
            roundTripped.TaskFlow.Variable,
            Sha256(irBytes),
            Sha256(leanBytes),
            DependencyAudit.Read(root));
        ValidateManifest(manifest, roundTripped, Sha256(irBytes), Sha256(leanBytes));
        WriteNewArtifact(irPath, irBytes);
        WriteNewArtifact(leanPath, leanBytes);
        WriteNewArtifact(manifestPath, Serialize(manifest));
        return new(irPath, manifestPath, leanPath, document.Sources.Length, document.Members.Length,
            document.Anchors.Length, document.ControlFlows.Length);
    }

    private static SourcePinDocument ReadPins(string root, SourcePinDocument? supplied = null)
    {
        string path = Path.Combine(root, SourcePinsPath);
        if (!File.Exists(path))
        {
            throw new ExtractionException($"Missing source pin document '{SourcePinsPath}'.");
        }

        SourcePinDocument document = supplied ?? Deserialize<SourcePinDocument>(File.ReadAllBytes(path));
        if (document.SchemaVersion != 1 || document.Sources is null ||
            !document.Sources.Select(static pin => pin.Path).SequenceEqual(SourcePaths, StringComparer.Ordinal) ||
            !document.Sources.Select(static pin => pin.Role).SequenceEqual(SourceRoles, StringComparer.Ordinal) ||
            document.Sources.Any(static pin => !IsSha256(pin.Sha256)))
        {
            throw new ExtractionException("The post-transaction finalization source pins have the wrong shape.");
        }

        return document;
    }

    private static SourceFile[] ReadSources(
        string root,
        SourcePinDocument pins,
        IReadOnlyDictionary<string, byte[]>? overrides,
        bool enforcePins,
        bool allowAliases = false,
        bool enforceSourceSelection = true)
    {
        if (overrides is not null && overrides.Keys.Any(path => !SourcePaths.Contains(path, StringComparer.Ordinal)))
        {
            throw new ExtractionException("A source override escaped the post-transaction finalization closure.");
        }

        SourceFile[] result = new SourceFile[SourcePaths.Length];
        for (int index = 0; index < SourcePaths.Length; index++)
        {
            string relativePath = SourcePaths[index];
            byte[] bytes;
            if (overrides is not null && overrides.TryGetValue(relativePath, out byte[]? overrideBytes))
            {
                bytes = overrideBytes ?? throw new ExtractionException($"Null source override for '{relativePath}'.");
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

            string text;
            try
            {
                text = new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new ExtractionException($"Source '{relativePath}' is not valid UTF-8: {exception.Message}");
            }

            SyntaxTree tree = CSharpSyntaxTree.ParseText(text, ParseOptions, relativePath);
            CompilationUnitSyntax syntax = (CompilationUnitSyntax)tree.GetRoot();
            if (enforceSourceSelection) ArtifactSafety.RequireUnconditionalSource(syntax, "Finalization");
            Diagnostic[] syntaxErrors = syntax.GetDiagnostics()
                .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
            if (syntaxErrors.Length != 0)
            {
                throw new ExtractionException($"Source '{relativePath}' has syntax errors: {string.Join("; ", syntaxErrors.Select(static diagnostic => diagnostic.ToString()))}");
            }

            string[] admittedAliases = relativePath == SourcePaths[4] ? ["usingMetrics=Nethermind.Evm.Metrics;"] : [];
            if (!allowAliases && (!syntax.DescendantNodes().OfType<UsingDirectiveSyntax>()
                    .Where(static usingDirective => usingDirective.Alias is not null).Select(Canonical).SequenceEqual(admittedAliases) ||
                syntax.DescendantNodes().OfType<ExternAliasDirectiveSyntax>().Any()))
            {
                throw new ExtractionException($"Source '{relativePath}' introduced an unsupported alias binding.");
            }

            string sourceHash = Sha256(bytes);
            if (enforcePins && !string.Equals(sourceHash, pins.Sources[index].Sha256, StringComparison.Ordinal))
            {
                throw new ExtractionException($"Source pin changed for '{relativePath}': expected {pins.Sources[index].Sha256}, got {sourceHash}.");
            }

            result[index] = new(
                relativePath,
                pins.Sources[index].Role,
                Path.Combine(root, relativePath),
                bytes,
                sourceHash,
                Sha256(Encoding.UTF8.GetBytes(Canonical(syntax))),
                syntax,
                tree);
        }

        return result;
    }

    internal static CompilerContext ReadCompilerContext(string root, SourceFile[] sources, bool validateCompilation,
        IReadOnlyDictionary<string, byte[]>? supportOverrides = null, bool enforceSupportPins = true, bool auditSupport = true)
    {
        CompilerReferenceIdentity[] references;
        try
        {
            references = Fold.Extractor.ReadCompilerReferenceIdentities(root).Select(static reference =>
                new CompilerReferenceIdentity(reference.Path, reference.AssemblyName, reference.Sha256,
                    reference.Mvid, reference.Selected)).ToArray();
        }
        catch (Fold.ExtractionException exception)
        {
            throw new ExtractionException("Finalization compiler dependency failed: " + exception.Message);
        }
        string platformDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)
            ?? throw new ExtractionException("The runtime platform assembly directory is unavailable.");
        List<MetadataReference> metadata = references.Where(static reference => reference.Selected)
            .Select(reference => MetadataReference.CreateFromFile(ResolveReferencePath(root, platformDirectory, reference.Path)))
            .Cast<MetadataReference>().ToList();
        CompilerClosureIdentity closure = new(
            CompilerReferenceInventoryPath,
            references.Length,
            CompilerReferenceAggregate(references),
            Sha256(File.ReadAllBytes(Path.Combine(root, CompilerReferenceInventoryPath))),
            CompilerSupportSources);
        if (!validateCompilation)
        {
            return new(null!, references, closure);
        }

        SyntaxTree[] support = ReadCompilerSupport(root, sources, supportOverrides, enforceSupportPins, auditSupport);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "Nethermind.Consensus",
            sources.Select(static source => source.Tree).Concat(support),
            metadata,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                warningLevel: 999,
                metadataImportOptions: MetadataImportOptions.All));
        Diagnostic[] errors = compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0)
        {
            throw new ExtractionException("The typed finalization source closure has compilation errors: " +
                string.Join("; ", errors.Select(static diagnostic => diagnostic.ToString())));
        }

        if (auditSupport) ValidateCompilerSupportBoundary(compilation, support);
        return new(compilation, references, closure);
    }

    private static void ValidatePinnedTreeEffectBoundary(
        IReadOnlyList<SourceFile> sources,
        Compilation compilation)
    {
        if (sources.Count != SourcePaths.Length || !sources.Select(static source => source.RelativePath)
                .SequenceEqual(SourcePaths, StringComparer.Ordinal) ||
            sources.Any(source => !compilation.ContainsSyntaxTree(source.Tree)))
        {
            throw new ExtractionException(
                "The typed whole-tree effect audit must be rooted in exactly the five pinned source trees.");
        }

        ValidatePinnedTreeInitializers(sources, compilation);
        ValidatePinnedTreeActivationLedger(sources, compilation);
        ValidatePinnedTreeLateBoundApis(sources, compilation);
        ValidatePinnedTreeEffectLedger(sources, compilation);
    }

    private static void ValidatePinnedTreeInitializers(
        IReadOnlyList<SourceFile> sources,
        Compilation compilation)
    {
        foreach (SourceFile source in sources)
        {
            SemanticModel model = compilation.GetSemanticModel(source.Tree, ignoreAccessibility: true);
            foreach (EqualsValueClauseSyntax initializer in source.Tree.GetRoot().DescendantNodes()
                         .OfType<EqualsValueClauseSyntax>())
            {
                if (!TryGetPinnedInitializerOwner(initializer, model, out ISymbol owner))
                {
                    continue;
                }

                IOperation initializerOperation = (model.GetOperation(initializer) switch
                {
                    IFieldInitializerOperation fieldInitializer => fieldInitializer.Value,
                    IPropertyInitializerOperation propertyInitializer => propertyInitializer.Value,
                    _ => model.GetOperation(initializer.Value),
                })
                    ?? throw new ExtractionException(
                        $"Pinned initializer '{SymbolId(owner)}' has no typed Roslyn operation.");
                foreach (IAnonymousFunctionOperation lambda in DescendantOperations(initializerOperation)
                             .OfType<IAnonymousFunctionOperation>())
                {
                    if (!IsExactPinnedLazyInitializer(source.RelativePath, owner, initializer))
                    {
                        throw new ExtractionException(
                            $"Pinned five-tree initializer ledger rejects an un-audited pinned initializer lambda in " +
                            $"'{SymbolId(owner)}' ({Canonical(lambda.Syntax)}).");
                    }
                }

                foreach (IObjectCreationOperation creation in DescendantOperations(initializerOperation)
                             .OfType<IObjectCreationOperation>())
                {
                    if (IsSourceOwnedType(creation.Type, compilation))
                    {
                        throw new ExtractionException(
                            $"Pinned five-tree initializer ledger rejects source-owned object creation " +
                            $"'{OperationCanonical(creation)}' in '{SymbolId(owner)}'.");
                    }
                }

                foreach (IMethodReferenceOperation reference in DescendantOperations(initializerOperation)
                             .OfType<IMethodReferenceOperation>())
                {
                    if (IsSourceOwnedMethod(reference.Method, compilation) &&
                        !IsExactPinnedLazyInitializer(source.RelativePath, owner, initializer))
                    {
                        throw new ExtractionException(
                            $"Pinned five-tree initializer ledger rejects source-owned method reference " +
                            $"'{MethodSymbolId(reference.Method)}' in '{SymbolId(owner)}'.");
                    }
                }
            }
        }
    }

    private static bool TryGetPinnedInitializerOwner(
        EqualsValueClauseSyntax initializer,
        SemanticModel model,
        out ISymbol owner)
    {
        if (initializer.Parent is VariableDeclaratorSyntax variable &&
            variable.Parent?.Parent is FieldDeclarationSyntax or EventFieldDeclarationSyntax &&
            model.GetDeclaredSymbol(variable) is ISymbol field)
        {
            owner = field;
            return true;
        }

        if (initializer.Parent is PropertyDeclarationSyntax property &&
            model.GetDeclaredSymbol(property) is ISymbol propertySymbol)
        {
            owner = propertySymbol;
            return true;
        }

        owner = null!;
        return false;
    }

    private static bool IsExactPinnedLazyInitializer(
        string path,
        ISymbol owner,
        EqualsValueClauseSyntax initializer)
    {
        if (path != BlockProcessorPath || owner is not IFieldSymbol field ||
            field.ContainingType?.ToSourceIdentity() != BlockProcessorTypeSymbol)
        {
            return false;
        }

        string canonical = Canonical(initializer.Value);
        return field.Name switch
        {
            "_balSystemContractHandler" =>
                canonical == "new(()=>new(beaconBlockRootHandler,blockHashStore,balManager))",
            "_standardSystemContractHandler" =>
                canonical == "new(()=>new(beaconBlockRootHandler,blockHashStore,withdrawalProcessor,executionRequestsProcessor))",
            _ => false,
        };
    }

    private static void ValidatePinnedTreeActivationLedger(
        IReadOnlyList<SourceFile> sources,
        Compilation compilation)
    {
        List<PinnedActivationLedgerEntry> observations = [];
        HashSet<string> observed = new(StringComparer.Ordinal);
        foreach (SourceFile source in sources)
        {
            SemanticModel model = compilation.GetSemanticModel(source.Tree, ignoreAccessibility: true);
            foreach (SyntaxNode candidate in source.Tree.GetRoot().DescendantNodesAndSelf().Where(static node =>
                         node is MemberAccessExpressionSyntax or IdentifierNameSyntax or ElementAccessExpressionSyntax or
                         AssignmentExpressionSyntax))
            {
                IOperation? operation = model.GetOperation(candidate);
                switch (operation)
                {
                    case IPropertyReferenceOperation property when
                        HasPinnedSourceAccessorImplementation(property.Property, compilation):
                        AddPinnedActivationObservation(observations, observed, source.RelativePath, model,
                            property.Syntax, SymbolId(property.Property), "property-reference");
                        break;
                    case IImplicitIndexerReferenceOperation { IndexerSymbol: IPropertySymbol indexerSymbol } indexer when
                        HasPinnedSourceAccessorImplementation(indexerSymbol, compilation):
                        AddPinnedActivationObservation(observations, observed, source.RelativePath, model,
                            indexer.Syntax, SymbolId(indexerSymbol), "indexer-reference");
                        break;
                    case IEventReferenceOperation eventReference when
                        HasPinnedSourceAccessorImplementation(eventReference.Event, compilation):
                        AddPinnedActivationObservation(observations, observed, source.RelativePath, model,
                            eventReference.Syntax, SymbolId(eventReference.Event), "event-reference");
                        break;
                    case IEventAssignmentOperation eventAssignment when
                        eventAssignment.EventReference is IEventReferenceOperation assignedEventReference &&
                        HasPinnedSourceAccessorImplementation(assignedEventReference.Event, compilation):
                        AddPinnedActivationObservation(observations, observed, source.RelativePath, model,
                            eventAssignment.Syntax, SymbolId(assignedEventReference.Event), "event-assignment");
                        break;
                }
            }
        }

        ValidateExactActivationLedger(observations);
    }

    private static void AddPinnedActivationObservation(
        ICollection<PinnedActivationLedgerEntry> observations,
        ISet<string> observed,
        string path,
        SemanticModel model,
        SyntaxNode? syntax,
        string target,
        string operationKind)
    {
        if (syntax is null)
        {
            throw new ExtractionException(
                $"Pinned five-tree property/indexer/event activation ledger has a typed {operationKind} without source syntax.");
        }

        PinnedActivationLedgerEntry entry = new(path, PinnedOwnerId(syntax, model), target,
            operationKind, Canonical(syntax), 1);
        string instanceKey = entry.Key + "\0" +
            syntax.SpanStart.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0" +
            syntax.Span.End.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (observed.Add(instanceKey))
        {
            observations.Add(entry);
        }
    }

    private static void ValidateExactActivationLedger(IReadOnlyList<PinnedActivationLedgerEntry> observations)
    {
        Dictionary<string, int> expected = CountActivationLedger(PinnedActivationLedger);
        Dictionary<string, int> actual = CountActivationLedger(observations);
        if (LedgerCountsEqual(expected, actual))
        {
            return;
        }

        throw new ExtractionException(
            "Pinned five-tree property/indexer/event activation ledger drift: " +
            DescribeLedgerDrift(expected, actual) +
            ". An executable source implementation behind a source or metadata contract requires an explicit typed boundary.");
    }

    private static Dictionary<string, int> CountActivationLedger(
        IEnumerable<PinnedActivationLedgerEntry> entries) => entries
        .GroupBy(static entry => entry.Key, StringComparer.Ordinal)
        .ToDictionary(static group => group.Key, static group => group.Sum(static entry => entry.Multiplicity),
            StringComparer.Ordinal);

    private static bool HasPinnedSourceAccessorImplementation(IPropertySymbol property, Compilation compilation)
    {
        if (HasExecutableSourceAccessor(property, compilation))
        {
            return true;
        }

        return PinnedSourceTypes(compilation).Any(type =>
            HasPinnedSourcePropertyImplementation(type, property, compilation));
    }

    private static bool HasPinnedSourceAccessorImplementation(IEventSymbol eventSymbol, Compilation compilation)
    {
        if (HasExecutableSourceAccessor(eventSymbol, compilation))
        {
            return true;
        }

        return PinnedSourceTypes(compilation).Any(type =>
            HasPinnedSourceEventImplementation(type, eventSymbol, compilation));
    }

    private static bool HasPinnedSourcePropertyImplementation(
        INamedTypeSymbol type,
        IPropertySymbol property,
        Compilation compilation)
    {
        bool interfaceImplementation = property.ContainingType?.TypeKind == TypeKind.Interface &&
            type.FindImplementationForInterfaceMember(property) is IPropertySymbol implementation &&
            HasExecutableSourceAccessor(implementation, compilation);
        return interfaceImplementation || type.GetMembers().OfType<IPropertySymbol>().Any(candidate =>
            HasExecutableSourceAccessor(candidate, compilation) && OverridesProperty(candidate, property));
    }

    private static bool HasPinnedSourceEventImplementation(
        INamedTypeSymbol type,
        IEventSymbol eventSymbol,
        Compilation compilation)
    {
        bool interfaceImplementation = eventSymbol.ContainingType?.TypeKind == TypeKind.Interface &&
            type.FindImplementationForInterfaceMember(eventSymbol) is IEventSymbol implementation &&
            HasExecutableSourceAccessor(implementation, compilation);
        return interfaceImplementation || type.GetMembers().OfType<IEventSymbol>().Any(candidate =>
            HasExecutableSourceAccessor(candidate, compilation) && OverridesEvent(candidate, eventSymbol));
    }

    private static bool OverridesProperty(IPropertySymbol candidate, IPropertySymbol target)
    {
        for (IPropertySymbol? current = candidate.OverriddenProperty; current is not null;
             current = current.OverriddenProperty)
        {
            if (SymbolEqualityComparer.Default.Equals(current, target))
            {
                return true;
            }
        }

        return false;
    }

    private static bool OverridesEvent(IEventSymbol candidate, IEventSymbol target)
    {
        for (IEventSymbol? current = candidate.OverriddenEvent; current is not null;
             current = current.OverriddenEvent)
        {
            if (SymbolEqualityComparer.Default.Equals(current, target))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<INamedTypeSymbol> PinnedSourceTypes(Compilation compilation)
    {
        foreach (SyntaxTree tree in compilation.SyntaxTrees.Where(IsSemanticTree))
        {
            SemanticModel model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            foreach (BaseTypeDeclarationSyntax declaration in tree.GetRoot().DescendantNodes()
                         .OfType<BaseTypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(declaration) is INamedTypeSymbol type)
                {
                    yield return type;
                }
            }
        }
    }

    private static void ValidatePinnedTreeLateBoundApis(
        IReadOnlyList<SourceFile> sources,
        Compilation compilation)
    {
        foreach (SourceFile source in sources)
        {
            SemanticModel model = compilation.GetSemanticModel(source.Tree, ignoreAccessibility: true);
            foreach (InvocationExpressionSyntax invocation in source.Tree.GetRoot().DescendantNodes()
                         .OfType<InvocationExpressionSyntax>())
            {
                IOperation? rawOperation = model.GetOperation(invocation);
                if (rawOperation is IDynamicInvocationOperation dynamicInvocation)
                {
                    throw new ExtractionException(
                        $"Pinned five-tree late-bound audit reaches an un-audited dynamic member invocation ({OperationCanonical(dynamicInvocation)}).");
                }

                if (rawOperation is IFunctionPointerInvocationOperation functionPointer)
                {
                    throw new ExtractionException(
                        $"Pinned five-tree late-bound audit reaches an un-audited function-pointer invocation ({OperationCanonical(functionPointer)}).");
                }

                if (rawOperation is not IInvocationOperation operation ||
                    operation.TargetMethod is null || HasErrorSymbol(operation.TargetMethod))
                {
                    throw new ExtractionException(
                        $"Pinned five-tree late-bound API audit has no exact typed target for '{Canonical(invocation)}'.");
                }

                if (IsReflectionOrReentryApi(operation.TargetMethod))
                {
                    throw new ExtractionException(
                        $"Pinned five-tree reflection/reentry API '{MethodSymbolId(operation.TargetMethod)}' is not in the exact external/opaque ledger ({Canonical(invocation)}).");
                }
            }

            foreach (TypeOfExpressionSyntax typeOf in source.Tree.GetRoot().DescendantNodes()
                         .OfType<TypeOfExpressionSyntax>())
            {
                throw new ExtractionException(
                    $"Pinned five-tree reflection/reentry API rejects '{Canonical(typeOf)}'; typeof values cannot escape to an external or opaque boundary.");
            }

            foreach (SyntaxNode candidate in source.Tree.GetRoot().DescendantNodesAndSelf().Where(static node =>
                         node is MemberAccessExpressionSyntax or ElementAccessExpressionSyntax or PrefixUnaryExpressionSyntax))
            {
                IOperation? operation = model.GetOperation(candidate);
                if (operation is IDynamicMemberReferenceOperation dynamicMember)
                {
                    throw new ExtractionException(
                        $"Pinned five-tree late-bound audit reaches an un-audited dynamic member reference ({OperationCanonical(dynamicMember)}).");
                }

                if (operation is IDynamicIndexerAccessOperation dynamicIndexer)
                {
                    throw new ExtractionException(
                        $"Pinned five-tree late-bound audit reaches an un-audited dynamic indexer access ({OperationCanonical(dynamicIndexer)}).");
                }

                if (operation is IAddressOfOperation addressOf)
                {
                    throw new ExtractionException(
                        $"Pinned five-tree late-bound audit reaches an un-audited function/address creation ({OperationCanonical(addressOf)}).");
                }
            }
        }
    }

    private static bool IsReflectionOrReentryApi(IMethodSymbol target)
    {
        string containingType = target.ContainingType?.ToSourceIdentity() ?? string.Empty;
        if (ReflectionOrReentryTypePrefixes.Any(prefix => containingType.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return true;
        }

        if (containingType == "global::System.Activator" ||
            containingType == "global::System.Runtime.InteropServices.Marshal")
        {
            return true;
        }

        if (containingType == "global::System.Delegate")
        {
            return target.Name is "DynamicInvoke" or "CreateDelegate";
        }

        if (containingType == "global::System.Runtime.CompilerServices.RuntimeHelpers")
        {
            return target.Name == "RunClassConstructor";
        }

        return containingType == "global::System.Type" && target.Name is
            "GetType" or "GetMember" or "GetMembers" or "GetMethod" or "GetMethods" or
            "GetProperty" or "GetProperties" or "GetField" or "GetFields" or "GetEvent" or
            "GetEvents" or "InvokeMember";
    }

    private static void ValidatePinnedTreeEffectLedger(
        IReadOnlyList<SourceFile> sources,
        Compilation compilation)
    {
        List<PinnedEffectLedgerEntry> observations = [];
        HashSet<string> observedProperties = new(StringComparer.Ordinal);
        foreach (SourceFile source in sources)
        {
            SemanticModel model = compilation.GetSemanticModel(source.Tree, ignoreAccessibility: true);
            SyntaxNode root = source.Tree.GetRoot();
            foreach (SyntaxNode candidate in root.DescendantNodesAndSelf().Where(static node =>
                         node is MemberAccessExpressionSyntax or IdentifierNameSyntax))
            {
                if (model.GetOperation(candidate) is not IPropertyReferenceOperation property ||
                    !IsPinnedEffectProperty(property.Property) || !IsPropertyWrite(property))
                {
                    continue;
                }

                if (property.Syntax is null)
                {
                    throw new ExtractionException(
                        "Pinned five-tree typed effect ledger has a property write without source syntax.");
                }

                string instanceKey = source.RelativePath + "\0" + SymbolId(property.Property) + "\0" +
                    property.Syntax.SpanStart.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0" +
                    property.Syntax.Span.End.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (observedProperties.Add(instanceKey))
                {
                    observations.Add(new(source.RelativePath, PinnedOwnerId(property.Syntax, model),
                        SymbolId(property.Property), "property-write", PinnedEffectCanonical(property.Syntax), 1));
                }
            }

            foreach (InvocationExpressionSyntax invocation in root.DescendantNodes()
                         .OfType<InvocationExpressionSyntax>())
            {
                if (model.GetOperation(invocation) is IInvocationOperation { TargetMethod: not null } operation &&
                    !HasErrorSymbol(operation.TargetMethod) && IsDirectStateProviderCommit(operation.TargetMethod))
                {
                    observations.Add(new(source.RelativePath, PinnedOwnerId(invocation, model),
                        MethodSymbolId(operation.TargetMethod), "world-state-commit", Canonical(invocation), 1));
                }
            }

            foreach (SyntaxNode candidate in root.DescendantNodesAndSelf().Where(static node =>
                         node is MemberAccessExpressionSyntax or IdentifierNameSyntax))
            {
                if (model.GetOperation(candidate) is not IMethodReferenceOperation reference ||
                    !IsDirectStateProviderCommit(reference.Method) || IsDirectInvocationMethodReference(reference))
                {
                    continue;
                }

                throw new ExtractionException(
                    $"Pinned five-tree typed effect ledger rejects direct IWorldState.Commit method reference " +
                    $"'{OperationCanonical(reference)}'; it cannot escape through a delegate or callback.");
            }
        }

        Dictionary<string, int> expected = CountEffectLedger(PinnedEffectLedger);
        Dictionary<string, int> actual = CountEffectLedger(observations);
        if (!LedgerCountsEqual(expected, actual))
        {
            throw new ExtractionException(
                "Pinned five-tree typed effect ledger drift: " + DescribeLedgerDrift(expected, actual) +
                ". All direct Header.BlobGasUsed/Bloom/ReceiptsRoot/StateRoot/Hash, Block.AccountChanges, and IWorldState.Commit effects require an exact row.");
        }
    }

    private static bool IsPinnedEffectProperty(IPropertySymbol property)
    {
        string symbol = SymbolId(property);
        return symbol == BlobGasPropertySymbol || symbol == BloomPropertySymbol ||
            symbol == ReceiptsRootPropertySymbol || symbol == StateRootPropertySymbol ||
            symbol == HashPropertySymbol || symbol == AccountChangesPropertySymbol;
    }

    private static bool IsDirectInvocationMethodReference(IMethodReferenceOperation reference)
    {
        InvocationExpressionSyntax? invocation = reference.Syntax?.AncestorsAndSelf()
            .OfType<InvocationExpressionSyntax>().FirstOrDefault();
        return invocation is not null && reference.Syntax is not null && IsWithin(invocation.Expression, reference.Syntax);
    }

    private static string PinnedEffectCanonical(SyntaxNode syntax)
    {
        AssignmentExpressionSyntax? assignment = syntax.AncestorsAndSelf().OfType<AssignmentExpressionSyntax>()
            .FirstOrDefault();
        return assignment is null ? Canonical(syntax) : Canonical(assignment);
    }

    private static Dictionary<string, int> CountEffectLedger(IEnumerable<PinnedEffectLedgerEntry> entries) => entries
        .GroupBy(static entry => entry.Key, StringComparer.Ordinal)
        .ToDictionary(static group => group.Key, static group => group.Sum(static entry => entry.Multiplicity),
            StringComparer.Ordinal);

    private static bool LedgerCountsEqual(
        IReadOnlyDictionary<string, int> expected,
        IReadOnlyDictionary<string, int> actual) => expected.Count == actual.Count &&
        expected.All(pair => actual.TryGetValue(pair.Key, out int count) && count == pair.Value);

    private static string DescribeLedgerDrift(
        IReadOnlyDictionary<string, int> expected,
        IReadOnlyDictionary<string, int> actual)
    {
        string[] missing = expected.Where(pair => !actual.TryGetValue(pair.Key, out int count) || count < pair.Value)
            .Select(pair => $"missing {pair.Value}x {pair.Key}").OrderBy(static entry => entry, StringComparer.Ordinal).ToArray();
        string[] additional = actual.Where(pair => !expected.TryGetValue(pair.Key, out int count) || count > pair.Value)
            .Select(pair => $"additional {pair.Value}x {pair.Key}").OrderBy(static entry => entry, StringComparer.Ordinal).ToArray();
        return string.Join("; ", missing.Concat(additional));
    }

    private static string PinnedOwnerId(SyntaxNode syntax, SemanticModel model)
    {
        EqualsValueClauseSyntax? initializer = syntax.AncestorsAndSelf().OfType<EqualsValueClauseSyntax>()
            .FirstOrDefault(candidate => TryGetPinnedInitializerOwner(candidate, model, out _));
        if (initializer is not null && TryGetPinnedInitializerOwner(initializer, model, out ISymbol initializerOwner))
        {
            return "initializer:" + SymbolId(initializerOwner);
        }

        foreach (SyntaxNode ancestor in syntax.AncestorsAndSelf())
        {
            switch (ancestor)
            {
                case AnonymousFunctionExpressionSyntax lambda when
                    model.GetOperation(lambda) is IAnonymousFunctionOperation lambdaOperation:
                    return MethodSymbolId(lambdaOperation.Symbol);
                case MethodDeclarationSyntax method when model.GetDeclaredSymbol(method) is IMethodSymbol methodSymbol:
                    return MethodSymbolId(methodSymbol);
                case ConstructorDeclarationSyntax constructor when model.GetDeclaredSymbol(constructor) is IMethodSymbol constructorSymbol:
                    return MethodSymbolId(constructorSymbol);
                case DestructorDeclarationSyntax destructor when model.GetDeclaredSymbol(destructor) is IMethodSymbol destructorSymbol:
                    return MethodSymbolId(destructorSymbol);
                case OperatorDeclarationSyntax operatorDeclaration when model.GetDeclaredSymbol(operatorDeclaration) is IMethodSymbol operatorSymbol:
                    return MethodSymbolId(operatorSymbol);
                case ConversionOperatorDeclarationSyntax conversion when model.GetDeclaredSymbol(conversion) is IMethodSymbol conversionSymbol:
                    return MethodSymbolId(conversionSymbol);
                case LocalFunctionStatementSyntax localFunction when model.GetDeclaredSymbol(localFunction) is IMethodSymbol localFunctionSymbol:
                    return MethodSymbolId(localFunctionSymbol);
                case AccessorDeclarationSyntax accessor when model.GetDeclaredSymbol(accessor) is IMethodSymbol accessorSymbol:
                    return MethodSymbolId(accessorSymbol);
                case PropertyDeclarationSyntax property when model.GetDeclaredSymbol(property) is IPropertySymbol propertySymbol:
                    return SymbolId(propertySymbol);
                case IndexerDeclarationSyntax indexer when model.GetDeclaredSymbol(indexer) is IPropertySymbol indexerSymbol:
                    return SymbolId(indexerSymbol);
                case EventDeclarationSyntax eventDeclaration when model.GetDeclaredSymbol(eventDeclaration) is IEventSymbol eventSymbol:
                    return SymbolId(eventSymbol);
            }
        }

        throw new ExtractionException(
            $"Pinned five-tree ledger cannot identify an owning method or initializer for '{Canonical(syntax)}'.");
    }

    private static string SymbolId(ISymbol symbol) =>
        symbol.ToSourceIdentity();

    private static string ResolveReferencePath(string root, string platformDirectory, string logicalPath)
    {
        if (logicalPath.StartsWith("platform/", StringComparison.Ordinal))
        {
            string fileName = logicalPath["platform/".Length..];
            if (fileName.Length == 0 || Path.GetFileName(fileName) != fileName)
            {
                throw new ExtractionException($"Invalid platform compiler reference path '{logicalPath}'.");
            }

            return Path.Combine(platformDirectory, fileName);
        }

        string fullPath = Path.GetFullPath(Path.Combine(root, logicalPath));
        EnsureWithin(root, fullPath);
        return fullPath;
    }

    private static MemberIdentity BindMethod(
        string id,
        SourceFile source,
        MethodDeclarationSyntax method,
        SemanticModel model,
        Compilation compilation)
    {
        IMethodSymbol symbol = model.GetDeclaredSymbol(method)
            ?? throw new ExtractionException($"No declared symbol for {OwnerPath(method)}.{method.Identifier.ValueText}.");
        IOperation operation = model.GetOperation(method)
            ?? throw new ExtractionException($"No IOperation for {OwnerPath(method)}.{method.Identifier.ValueText}.");
        if (operation is IInvalidOperation || HasErrorSymbol(symbol))
        {
            throw new ExtractionException($"Invalid typed operation for {OwnerPath(method)}.{method.Identifier.ValueText}.");
        }
        if (id == "block.processBlock" && symbol.ToSourceIdentity() != ProcessBlockMethodSymbol)
            throw new ExtractionException("ProcessBlock did not bind to the pinned BlockProcessor source method.");

        TypedBinding binding = BindNode(source, method, method, model, compilation, null, operation, symbol);
        return new(
            id,
            source.RelativePath,
            OwnerPath(method),
            method.Identifier.ValueText,
            method.ParameterList.Parameters.Count,
            symbol.ToSourceIdentity(),
            binding);
    }

    private static List<AnchorIdentity> BuildAnchors(
        SourceFile source,
        MethodDeclarationSyntax method,
        SemanticModel model,
        Compilation compilation,
        out SourceLocalMethod[] helperClosure)
    {
        List<AnchorIdentity> anchors = [];
        InvocationExpressionSyntax[] commits = ExecutableDescendantNodes<InvocationExpressionSyntax>(method)
            .Where(static invocation => Canonical(invocation) == "CommitState(spec)")
            .OrderBy(static invocation => invocation.SpanStart).ToArray();
        if (commits.Length != 3)
        {
            throw new ExtractionException("ProcessBlock must retain exactly three CommitState(spec) calls.");
        }

        InvocationExpressionSyntax transactionFold = SingleInvocation(method,
            "_blockTransactionsExecutor.ProcessTransactions(block,options,ReceiptsTracer,token)", "transaction fold caller");
        InvocationExpressionSyntax signal = SingleInvocation(method, "TransactionsExecuted?.Invoke()", "TransactionsExecuted signal");
        if (model.GetOperation(signal) is not IInvocationOperation signalOperation ||
            !IsAuditedTransactionsExecutedHook(signal, signalOperation, model))
        {
            throw new ExtractionException("TransactionsExecuted must bind to the exact BlockProcessor Action event invocation.");
        }
        if (commits[0].SpanStart >= transactionFold.SpanStart || transactionFold.SpanStart >= signal.SpanStart ||
            signal.SpanStart >= commits[1].SpanStart)
        {
            throw new ExtractionException("The post-transaction CommitState(spec) is not after a normal fold signal.");
        }
        ControlFlowGraph processGraph = TryCreateGraph(model, method)
            ?? throw new ExtractionException("ProcessBlock has no Roslyn control-flow graph for tail validation.");
        ValidateTransactionsExecutedPath(method, processGraph, signal, commits[1]);

        anchors.Add(BindAnchorNode("block.post-transaction-commit", source, method, commits[1], model, compilation,
            AnchorRelations[0]));

        IfStatementSyntax blobGuard = FindIf(method, condition => Canonical(condition) == "spec.IsEip4844Enabled",
            "EIP-4844 guard");
        InvocationExpressionSyntax blobGas = SingleInvocation(blobGuard,
            "BlobGasCalculator.CalculateBlobGas(block.Transactions)", "blob gas calculation");
        anchors.Add(BindAnchorNode("block.blob-gas-guard", source, method, blobGuard.Condition, model, compilation, AnchorRelations[1]));
        anchors.Add(BindAnchorNode("block.blob-gas-calculation", source, method, blobGas, model, compilation, AnchorRelations[2]));

        VariableDeclaratorSyntax taskNull = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .SingleOrDefault(variable => variable.Identifier.ValueText == "bloomsAndReceiptsRootTask" &&
                variable.Initializer is not null && Canonical(variable.Initializer.Value) == "null")
            ?? throw new ExtractionException("The receipt background task must be initialized to null.");
        anchors.Add(BindAnchorNode("block.background-task-null", source, method, taskNull, model, compilation, AnchorRelations[3]));

        IfStatementSyntax backgroundGuard = FindIf(method,
            condition => Canonical(condition) == "ShouldCalculateReceiptsInBackground(receipts)", "background receipt guard");
        if (backgroundGuard.Else is null)
        {
            throw new ExtractionException("The false receipt-background arm is missing.");
        }

        StatementSyntax synchronousArm = backgroundGuard.Else.Statement;
        InvocationExpressionSyntax syncBlooms = synchronousArm.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .SingleOrDefault(invocation => Canonical(invocation) == "CalculateBlooms(receipts)")
            ?? throw new ExtractionException("The synchronous CalculateBlooms(receipts) arm is missing.");
        InvocationExpressionSyntax syncRoot = synchronousArm.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .SingleOrDefault(invocation => Canonical(invocation) == "CalculateReceiptsRoot(receipts,spec,block)")
            ?? throw new ExtractionException("The synchronous CalculateReceiptsRoot(receipts,spec,block) arm is missing.");
        if (syncBlooms.SpanStart >= syncRoot.SpanStart)
        {
            throw new ExtractionException("Synchronous CalculateBlooms must precede CalculateReceiptsRoot.");
        }

        anchors.Add(BindAnchorNode("block.receipts-background-guard", source, method, backgroundGuard.Condition, model, compilation, AnchorRelations[4]));
        anchors.Add(BindAnchorNode("block.sync-blooms", source, method, syncBlooms, model, compilation, AnchorRelations[5]));
        anchors.Add(BindAnchorNode("block.sync-receipts-root", source, method, syncRoot, model, compilation, AnchorRelations[6]));

        TryStatementSyntax tryStatement = method.Body?.Statements.OfType<TryStatementSyntax>().SingleOrDefault()
            ?? throw new ExtractionException("The finalization try/finally is missing or ambiguous.");
        if (tryStatement.Finally is null)
        {
            throw new ExtractionException("The finalization finally task-observation scope is missing.");
        }

        InvocationExpressionSyntax rewards = SingleInvocation(tryStatement.Block,
            "ApplyMinerRewards(block,blockTracer,spec)", "miner rewards");
        InvocationExpressionSyntax withdrawals = SingleInvocation(tryStatement.Block,
            "_systemContractHandler.ProcessWithdrawals(block,spec)", "withdrawals");
        InvocationExpressionSyntax finalizationCommit = commits[2];
        if (!IsWithin(tryStatement.Block, finalizationCommit))
        {
            throw new ExtractionException("The finalization CommitState(spec) is not the third source commit in the finalization try block.");
        }
        InvocationExpressionSyntax requests = SingleInvocation(tryStatement.Block,
            "_systemContractHandler.ProcessExecutionRequests(block,_stateProvider,receipts,spec)", "execution requests");
        InvocationExpressionSyntax endTrace = tryStatement.Block.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .SingleOrDefault(invocation => Canonical(invocation) ==
                "ReceiptsTracer.EndBlockTrace(accumulateBlockBloom:bloomsAndReceiptsRootTaskisnull)")
            ?? throw new ExtractionException("EndBlockTrace must retain the source null-task argument.");
        InvocationExpressionSyntax roots = SingleInvocation(tryStatement.Block,
            "CommitStateAndStorageRoots(spec)", "storage-root commit");
        IfStatementSyntax mainThread = FindIf(tryStatement.Block,
            condition => Canonical(condition) == "BlockchainProcessor.IsMainProcessingThread", "main-thread guard");
        InvocationExpressionSyntax[] accountChangesCalls = ExecutableDescendantNodes<InvocationExpressionSyntax>(method)
            .Where(invocation => Canonical(invocation) == "SetAccountChanges(block)").ToArray();
        if (accountChangesCalls.Length != 1 || !IsWithin(mainThread.Statement, accountChangesCalls[0]))
        {
            throw new ExtractionException("SetAccountChanges(block) must have exactly one true-arm source call.");
        }
        InvocationExpressionSyntax accountChanges = accountChangesCalls[0];
        IfStatementSyntax stateRootGuard = FindIf(tryStatement.Block,
            condition => Canonical(condition) == "ShouldComputeStateRoot(header)", "state-root guard");
        InvocationExpressionSyntax[] stateRootCalls = ExecutableDescendantNodes<InvocationExpressionSyntax>(method)
            .Where(invocation => Canonical(invocation) == "ComputeStateRoot(header)").ToArray();
        if (stateRootCalls.Length != 1 || !IsWithin(stateRootGuard.Statement, stateRootCalls[0]))
        {
            throw new ExtractionException("ComputeStateRoot(header) must have exactly one true-arm source call.");
        }
        InvocationExpressionSyntax stateRoot = stateRootCalls[0];
        IfStatementSyntax backgroundResultGuard = FindIf(tryStatement.Block,
            condition => Canonical(condition) == TaskBackgroundResultPredicate, "background-result guard");
        InvocationExpressionSyntax bal = SingleInvocation(tryStatement.Block,
            "_balManager.SetBlockAccessList(block)", "BAL finalization");
        IfStatementSyntax finallyGuard = FindIf(tryStatement.Finally.Block,
            condition => Canonical(condition) == TaskFinallyPredicate, "finally task-observation guard");
        AssignmentExpressionSyntax[] hashAssignments = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(assignment => Canonical(assignment) == "header.Hash=header.CalculateHash()").ToArray();
        if (hashAssignments.Length != 1)
        {
            throw new ExtractionException("The header hash assignment is missing or duplicated.");
        }
        AssignmentExpressionSyntax hash = hashAssignments[0];
        ReturnStatementSyntax returnReceipts = FindOuterReceiptsReturn(method);
        if (ExecutableDescendantNodes<ReturnStatementSyntax>(method).Count() != 1 ||
            rewards.SpanStart >= withdrawals.SpanStart || withdrawals.SpanStart >= finalizationCommit.SpanStart ||
            finalizationCommit.SpanStart >= requests.SpanStart || requests.SpanStart >= endTrace.SpanStart ||
            endTrace.SpanStart >= roots.SpanStart || roots.SpanStart >= mainThread.SpanStart ||
            mainThread.SpanStart >= stateRootGuard.SpanStart || stateRootGuard.SpanStart >= backgroundResultGuard.SpanStart ||
            backgroundResultGuard.SpanStart >= bal.SpanStart || bal.SpanStart >= finallyGuard.SpanStart ||
            finallyGuard.SpanStart >= hash.SpanStart || hash.SpanStart >= returnReceipts.SpanStart)
        {
            throw new ExtractionException("The post-transaction finalization observables are out of source order.");
        }

        anchors.Add(BindAnchorNode("block.rewards", source, method, rewards, model, compilation, AnchorRelations[7]));
        anchors.Add(BindAnchorNode("block.withdrawals", source, method, withdrawals, model, compilation, AnchorRelations[8]));
        anchors.Add(BindAnchorNode("block.finalization-commit", source, method, finalizationCommit, model, compilation, AnchorRelations[9]));
        anchors.Add(BindAnchorNode("block.execution-requests", source, method, requests, model, compilation, AnchorRelations[10]));
        anchors.Add(BindAnchorNode("block.end-block-trace", source, method, endTrace, model, compilation, AnchorRelations[11]));
        anchors.Add(BindAnchorNode("block.storage-roots-commit", source, method, roots, model, compilation, AnchorRelations[12]));
        anchors.Add(BindAnchorNode("block.main-thread-guard", source, method, mainThread.Condition, model, compilation, AnchorRelations[13]));
        anchors.Add(BindAnchorNode("block.account-changes", source, method, accountChanges, model, compilation, AnchorRelations[14]));
        anchors.Add(BindAnchorNode("block.state-root-guard", source, method, stateRootGuard.Condition, model, compilation, AnchorRelations[15]));
        anchors.Add(BindAnchorNode("block.state-root", source, method, stateRoot, model, compilation, AnchorRelations[16]));
        anchors.Add(BindAnchorNode("block.background-result-guard", source, method, backgroundResultGuard.Condition, model, compilation, AnchorRelations[17]));
        anchors.Add(BindAnchorNode("block.bal-finalization", source, method, bal, model, compilation, AnchorRelations[18]));
        anchors.Add(BindAnchorNode("block.background-finally-guard", source, method, finallyGuard.Condition, model, compilation, AnchorRelations[19]));
        anchors.Add(BindAnchorNode("block.hash", source, method, hash, model, compilation, AnchorRelations[20]));
        anchors.Add(BindAnchorNode("block.return-receipts", source, method, returnReceipts, model, compilation, AnchorRelations[21]));

        AnchorIdentity[] ordered = anchors.OrderBy(static anchor => anchor.Binding.Position).ToArray();
        if (!ordered.Select(static anchor => anchor.Id).SequenceEqual(
                AnchorIds.OrderBy(id => anchors.Single(anchor => anchor.Id == id).Binding.Position), StringComparer.Ordinal))
        {
            throw new ExtractionException("The admitted finalization anchors are not source ordered.");
        }

        ValidateProcessCommitCalls(method, model, processGraph, commits, tryStatement);
        helperClosure = ValidateProcessSourceLocalCallClosure(method, model, compilation);
        ValidateNormalTailControlFlow(method, processGraph, anchors);
        ProveSynchronousReceiptRoute(
            method, processGraph, backgroundGuard, syncBlooms, syncRoot, endTrace, backgroundResultGuard, finallyGuard);

        return anchors;
    }

    private static void ValidateProcessCommitCalls(
        MethodDeclarationSyntax method,
        SemanticModel model,
        ControlFlowGraph graph,
        IReadOnlyList<InvocationExpressionSyntax> expectedNoRootCalls,
        TryStatementSyntax finalizationTry)
    {
        InvocationExpressionSyntax[] invocations = method.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .OrderBy(static invocation => invocation.SpanStart)
            .ToArray();

        InvocationExpressionSyntax[] noRootCalls = invocations
            .Where(invocation => IsExactInvocation(invocation, model, CommitStateMethodSymbol))
            .ToArray();
        InvocationExpressionSyntax[] rootCalls = invocations
            .Where(invocation => IsExactInvocation(invocation, model, CommitRootsMethodSymbol))
            .ToArray();
        InvocationExpressionSyntax[] directCalls = invocations
            .Where(invocation => IsDirectStateProviderCommit(invocation, model))
            .ToArray();

        if (directCalls.Length != 0)
        {
            throw new ExtractionException(
                "ProcessBlock contains a direct typed _stateProvider.Commit invocation; only CommitState and CommitStateAndStorageRoots helpers are admitted.");
        }

        if (noRootCalls.Length != 3 || !noRootCalls.SequenceEqual(expectedNoRootCalls))
        {
            throw new ExtractionException(
                $"ProcessBlock must contain exactly three typed CommitState invocations (initial, post-transaction, finalization); found {noRootCalls.Length}.");
        }

        if (rootCalls.Length != 1)
        {
            throw new ExtractionException(
                $"ProcessBlock must contain exactly one typed CommitStateAndStorageRoots invocation inside the finalization try; found {rootCalls.Length} (an extra roots commit after finally is rejected).");
        }

        if (!IsWithin(finalizationTry.Block, rootCalls[0]))
        {
            throw new ExtractionException(
                "The sole typed CommitStateAndStorageRoots invocation must be inside the finalization try block.");
        }

        foreach (InvocationExpressionSyntax invocation in noRootCalls.Concat(rootCalls))
        {
            int block = FindControlFlowBlock(graph, invocation.SpanStart);
            if (block < 0 || graph.Blocks.SingleOrDefault(candidate => candidate.Ordinal == block) is not { IsReachable: true })
            {
                throw new ExtractionException(
                    $"Typed commit invocation '{Canonical(invocation)}' is not on a reachable ProcessBlock CFG block.");
            }
        }
    }

    private static bool IsDirectStateProviderCommit(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        if (model.GetOperation(invocation) is not IInvocationOperation operation || operation.TargetMethod is null)
        {
            return false;
        }

        return IsDirectStateProviderCommit(operation.TargetMethod);
    }

    private static bool IsDirectStateProviderCommit(IMethodSymbol target)
    {
        string symbol = MethodSymbolId(target);
        return symbol == DirectCommitSymbol || symbol == DirectInterfaceCommitSymbol;
    }

    private static void ValidateTransactionsExecutedPath(
        MethodDeclarationSyntax method,
        ControlFlowGraph graph,
        InvocationExpressionSyntax signal,
        InvocationExpressionSyntax postTransactionCommit)
    {
        ValidateOnlyExpectedGuarding(signal, "TransactionsExecuted signal", static _ => false);

        ExpressionStatementSyntax? signalStatement = signal.AncestorsAndSelf()
            .OfType<ExpressionStatementSyntax>().SingleOrDefault();
        if (signalStatement is null || method.Body is null ||
            !ReferenceEquals(signalStatement.Parent, method.Body))
        {
            throw new ExtractionException(
                "TransactionsExecuted signal must be the exact outer ProcessBlock expression statement before the post-transaction CommitState(spec).");
        }

        if (signal.Parent is not ConditionalAccessExpressionSyntax conditional)
            throw new ExtractionException(SignalTopologyDiagnostic);
        BasicBlock signalBlock = RequireReachableBlock(graph, conditional.Expression, "TransactionsExecuted evaluation");
        BasicBlock commitBlock = RequireReachableBlock(graph, postTransactionCommit, "post-transaction CommitState");
        BasicBlock[] reachable = graph.Blocks.Where(static block => block.IsReachable).ToArray();
        Dictionary<int, HashSet<int>> dominators = ComputeDominators(graph, reachable);
        if (!dominators.TryGetValue(commitBlock.Ordinal, out HashSet<int>? commitDominators) ||
            !commitDominators.Contains(signalBlock.Ordinal))
        {
            throw new ExtractionException(
                "TransactionsExecuted must be an unguarded reachable normal-path operation that dominates the post-transaction CommitState(spec)." +
                $" Signal block={signalBlock.Ordinal}, commit block={commitBlock.Ordinal}.");
        }

        if (signal.SpanStart >= postTransactionCommit.SpanStart ||
            method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Count(invocation => Canonical(invocation) == "TransactionsExecuted?.Invoke()") != 1)
        {
            throw new ExtractionException("The exact TransactionsExecuted signal must occur once before the post-transaction CommitState(spec).");
        }
    }

    private static bool IsDelegateInvocation(IInvocationOperation operation) =>
        operation.TargetMethod.MethodKind == MethodKind.DelegateInvoke ||
        operation.TargetMethod.ContainingType?.TypeKind == TypeKind.Delegate;

    private static bool IsAuditedTransactionsExecutedHook(
        InvocationExpressionSyntax invocation,
        IInvocationOperation operation,
        SemanticModel model)
    {
        bool exactEventSyntax = Canonical(invocation) == "TransactionsExecuted?.Invoke()" ||
            invocation.Parent is ConditionalAccessExpressionSyntax eventConditional &&
            ReferenceEquals(eventConditional.WhenNotNull, invocation) &&
            Canonical(eventConditional) == "TransactionsExecuted?.Invoke()";
        if (!exactEventSyntax ||
            operation.TargetMethod.MethodKind != MethodKind.DelegateInvoke ||
            operation.TargetMethod.ContainingType?.ToSourceIdentity() != SystemActionTypeSymbol)
        {
            return false;
        }

        IOperation eventRoot = invocation.Parent is ConditionalAccessExpressionSyntax conditionalAccess
            ? model.GetOperation(conditionalAccess) ?? operation
            : operation;
        IEventReferenceOperation[] eventReferences = DescendantOperations(eventRoot)
            .OfType<IEventReferenceOperation>()
            .ToArray();
        IEventSymbol? eventSymbol = eventReferences.Length == 1
            ? eventReferences[0].Event
            : invocation.Parent is ConditionalAccessExpressionSyntax eventReceiver
                ? model.GetSymbolInfo(eventReceiver.Expression).Symbol as IEventSymbol
                : null;
        if (eventReferences.Length > 1 || eventSymbol is null)
        {
            return false;
        }

        return eventSymbol.Name == TransactionsExecutedEventName &&
            eventSymbol.ContainingType?.ToSourceIdentity() == BlockProcessorTypeSymbol;
    }

    private static bool IsSourceOwnedSymbol(ISymbol? symbol, Compilation compilation) =>
        symbol is not null && symbol.DeclaringSyntaxReferences.Any(reference =>
            compilation.ContainsSyntaxTree(reference.SyntaxTree) && IsSemanticTree(reference.SyntaxTree) &&
            (symbol is not ITypeSymbol || reference.GetSyntax() is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or
                AnonymousObjectCreationExpressionSyntax or TypeParameterSyntax));

    private static bool IsSourceOwnedMethod(IMethodSymbol method, Compilation compilation) =>
        IsSourceOwnedSymbol(method, compilation) || IsSourceOwnedSymbol(method.ContainingType, compilation);

    private static bool IsSourceOwnedType(ITypeSymbol? type, Compilation compilation)
    {
        if (type is INamedTypeSymbol { IsTupleType: true, TupleUnderlyingType: { } underlying }) type = underlying;
        if (type is null)
        {
            return false;
        }

        if (IsSourceOwnedSymbol(type, compilation) || IsSourceOwnedSymbol(type.OriginalDefinition, compilation))
        {
            return true;
        }

        return type switch
        {
            IArrayTypeSymbol array => IsSourceOwnedType(array.ElementType, compilation),
            INamedTypeSymbol named => named.TypeArguments.Any(argument => IsSourceOwnedType(argument, compilation)),
            _ => false,
        };
    }

    private static bool IsExactBoundSourceInvocation(
        InvocationExpressionSyntax invocation,
        IInvocationOperation operation,
        Compilation compilation)
    {
        string target = MethodSymbolId(operation.TargetMethod);
        return target switch
        {
            ExecutorProcessTransactionsMethodSymbol => Canonical(invocation) == SourceEntrySelectedExecutor &&
                IsExactExecutorReceiver(operation, compilation),
            ExecutorSetContextMethodSymbol => Canonical(invocation) == ExecutorSetContextCanonical &&
                IsExactExecutorReceiver(operation, compilation),
            CreateBlockExecutionContextMethodSymbol =>
                Canonical(invocation) == CreateBlockExecutionContextCanonical &&
                IsExactBlockProcessorReceiver(operation, compilation),
            _ => false,
        };
    }

    private static bool IsExactExecutorReceiver(IInvocationOperation operation, Compilation compilation) =>
        operation.Instance is IFieldReferenceOperation field && field.Field.Name == "_blockTransactionsExecutor" &&
        field.Field.ContainingType?.ToSourceIdentity() == BlockProcessorTypeSymbol &&
        IsSourceOwnedSymbol(field.Field, compilation);

    private static bool IsExactExecutorContextArgument(IArgumentOperation argument, Compilation compilation) =>
        argument.Parameter?.RefKind == RefKind.In && argument.Parent is IInvocationOperation call &&
        MethodSymbolId(call.TargetMethod) == ExecutorSetContextMethodSymbol &&
        call.Syntax is InvocationExpressionSyntax syntax && IsExactBoundSourceInvocation(syntax, call, compilation) &&
        argument.Value is IInvocationOperation value && MethodSymbolId(value.TargetMethod) == CreateBlockExecutionContextMethodSymbol &&
        value.Syntax is InvocationExpressionSyntax valueSyntax && IsExactBoundSourceInvocation(valueSyntax, value, compilation);

    private static bool IsExactBlockProcessorReceiver(IInvocationOperation operation, Compilation compilation) =>
        operation.ConstrainedToType is null &&
        operation.Instance is IInstanceReferenceOperation instance &&
        instance.Type?.ToSourceIdentity() == BlockProcessorTypeSymbol &&
        IsSourceOwnedSymbol(instance.Type, compilation) &&
        instance.ReferenceKind is InstanceReferenceKind.ContainingTypeInstance or InstanceReferenceKind.ImplicitReceiver;

    private static bool IsDispatch(IMethodSymbol target, IInvocationOperation operation) =>
        operation.IsVirtual || target.IsAbstract || target.IsVirtual || target.IsOverride ||
        target.ContainingType?.TypeKind == TypeKind.Interface;

    private static bool HasPinnedSourceImplementation(
        IMethodSymbol target,
        IReadOnlyList<SourceLocalMethod> localMethods,
        Compilation compilation)
    {
        foreach (SourceLocalMethod candidate in localMethods)
        {
            if (!HasExecutableSourceDeclaration(candidate.Symbol, compilation))
            {
                continue;
            }

            IMethodSymbol? overridden = candidate.Symbol.OverriddenMethod;
            while (overridden is not null)
            {
                if (SameMethodSymbol(overridden, target))
                {
                    return true;
                }

                overridden = overridden.OverriddenMethod;
            }

            if (target.ContainingType is INamedTypeSymbol interfaceType &&
                interfaceType.TypeKind == TypeKind.Interface &&
                candidate.Symbol.ContainingType?.FindImplementationForInterfaceMember(target) is IMethodSymbol implementation &&
                HasExecutableSourceDeclaration(implementation, compilation))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnboundSourceDispatch(
        InvocationExpressionSyntax invocation,
        IInvocationOperation operation,
        IReadOnlyList<SourceLocalMethod> localMethods,
        Compilation compilation) =>
        IsDispatch(operation.TargetMethod, operation) &&
        !IsExactBoundSourceInvocation(invocation, operation, compilation) &&
        (IsSourceOwnedMethod(operation.TargetMethod, compilation) ||
            HasPinnedSourceImplementation(operation.TargetMethod, localMethods, compilation));

    private static bool IsUnboundSourceMethodReferenceDispatch(
        IMethodReferenceOperation reference,
        IReadOnlyList<SourceLocalMethod> localMethods,
        Compilation compilation) =>
        (reference.IsVirtual || reference.Method.IsAbstract || reference.Method.IsVirtual || reference.Method.IsOverride ||
            reference.Method.ContainingType?.TypeKind == TypeKind.Interface) &&
        (IsSourceOwnedMethod(reference.Method, compilation) ||
            HasPinnedSourceImplementation(reference.Method, localMethods, compilation));

    private static bool IsExactBoundSourceMethodReference(
        IMethodReferenceOperation reference,
        SemanticModel model,
        Compilation compilation)
    {
        InvocationExpressionSyntax? invocation = reference.Syntax as InvocationExpressionSyntax ??
            reference.Syntax?.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
        if (invocation is null || model.GetOperation(invocation) is not IInvocationOperation operation)
        {
            return false;
        }

        return IsExactBoundSourceInvocation(invocation, operation, compilation);
    }

    private static bool IsKnownTaskRunLambda(
        IOperation operation,
        SemanticModel model,
        SourceLocalMethod current)
    {
        if (MethodSymbolId(current.Symbol) != ProcessBlockMethodSymbol ||
            operation.Syntax is not SyntaxNode callableSyntax)
        {
            return false;
        }

        InvocationExpressionSyntax? taskRun = callableSyntax.AncestorsAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault();
        if (taskRun is null || Canonical(taskRun.Expression) != "Task.Run")
        {
            return false;
        }

        AssignmentExpressionSyntax? taskAssignment = taskRun.AncestorsAndSelf()
            .OfType<AssignmentExpressionSyntax>()
            .FirstOrDefault(assignment => assignment.Right is InvocationExpressionSyntax invocation &&
                ReferenceEquals(invocation, taskRun));
        if (taskAssignment is null ||
            Canonical(taskAssignment.Left) != TaskVariable ||
            !taskRun.ArgumentList.Arguments.Any(argument =>
                IsWithin(argument.Expression, callableSyntax) || IsWithin(callableSyntax, argument.Expression)) ||
            model.GetOperation(taskRun) is not IInvocationOperation taskRunOperation)
        {
            return false;
        }

        return taskRunOperation.TargetMethod.Name == "Run" &&
            taskRunOperation.TargetMethod.ContainingType?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ==
                "System.Threading.Tasks.Task" &&
            !HasErrorSymbol(taskRunOperation.TargetMethod);
    }

    private static bool IsKnownParallelBloomLambda(
        IOperation operation,
        SemanticModel model,
        SourceLocalMethod current)
    {
        AnonymousFunctionExpressionSyntax? lambda = operation.Syntax as AnonymousFunctionExpressionSyntax;
        if (lambda is null)
        {
            AnonymousFunctionExpressionSyntax[] nestedLambdas = operation.ChildOperations
                .OfType<IAnonymousFunctionOperation>()
                .Select(static child => child.Syntax)
                .OfType<AnonymousFunctionExpressionSyntax>()
                .ToArray();
            if (nestedLambdas.Length != 1)
            {
                return false;
            }

            lambda = nestedLambdas[0];
        }

        if (MethodSymbolId(current.Symbol) != CalculateBloomsMethodSymbol ||
            lambda is null ||
            !Canonical(lambda).StartsWith("static", StringComparison.Ordinal))
        {
            return false;
        }

        InvocationExpressionSyntax? parallel = lambda.AncestorsAndSelf().OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(invocation => Canonical(invocation.Expression) == "ParallelUnbalancedWork.For");
        if (parallel is null || model.GetOperation(parallel) is not IInvocationOperation parallelOperation ||
            parallelOperation.TargetMethod.Name != "For" ||
            parallelOperation.TargetMethod.ContainingType?.ToSourceIdentity() !=
                ParallelUnbalancedWorkTypeSymbol ||
            parallel.ArgumentList.Arguments.Count != 5 ||
            !ReferenceEquals(parallel.ArgumentList.Arguments[4].Expression, lambda) ||
            Canonical(parallel.ArgumentList.Arguments[0].Expression) != "0" ||
            Canonical(parallel.ArgumentList.Arguments[1].Expression) != "receipts.Length" ||
            Canonical(parallel.ArgumentList.Arguments[2].Expression) != "ParallelUnbalancedWork.DefaultOptions" ||
            Canonical(parallel.ArgumentList.Arguments[3].Expression) != "receipts" ||
            HasErrorSymbol(parallelOperation.TargetMethod))
        {
            return false;
        }

        return lambda.Body is BlockSyntax body && body.Statements.Count == 2 &&
            body.Statements[0] is ExpressionStatementSyntax
            {
                Expression: InvocationExpressionSyntax invocation,
            } && Canonical(invocation) == "receipts[i].CalculateBloom()" &&
            body.Statements[1] is ReturnStatementSyntax returnStatement &&
            Canonical(returnStatement) == "returnreceipts;";
    }

    private static bool IsKnownAuditedLambda(
        IOperation operation,
        SemanticModel model,
        SourceLocalMethod current) =>
        IsKnownTaskRunLambda(operation, model, current) || IsKnownParallelBloomLambda(operation, model, current);

    private static string OperationCanonical(IOperation operation) =>
        operation.Syntax is SyntaxNode syntax ? Canonical(syntax) : operation.Kind.ToString();

    private static void ValidateSourceOwnedOperationEdges(
        SourceLocalMethod current,
        string path,
        IOperation currentOperation,
        IReadOnlyList<SourceLocalMethod> localMethods,
        Compilation compilation,
        HashSet<string> visited)
    {
        if (current.Syntax.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Any())
        {
            throw new ExtractionException(
                $"ProcessBlock reaches a source-local method with a nested local function via '{path}'; local callable bodies require an explicit typed audit.");
        }

        // A nested callable is an executable edge only when its delegate is invoked.  Keep its
        // body out of the enclosing declaration's walk; the exact audited Task.Run lambda is
        // re-entered below through its own body operation.
        IOperation[] operations = ExecutableDescendantOperations(currentOperation).ToArray();
        ValidateSourceGenericCallbackEdges(current, path, operations, compilation);
        foreach (IObjectCreationOperation creation in operations.OfType<IObjectCreationOperation>())
        {
            IMethodSymbol? constructor = creation.Constructor;
            bool exactInstrumentation = IsExactInstrumentationUsing(current, creation.Syntax);
            if ((IsSourceOwnedTypeDefinition(creation.Type, compilation) && !exactInstrumentation) ||
                constructor is not null && (HasExecutableSourceDeclaration(constructor, compilation) ||
                    IsSourceOwnedSymbol(constructor.ContainingType, compilation)))
            {
                throw new ExtractionException(
                    $"ProcessBlock reaches source-owned constructor/object creation '{(constructor is null ? OperationCanonical(creation) : MethodSymbolId(constructor))}' via '{path}' ({OperationCanonical(creation)}); constructor bodies and source type initializers require an explicit typed callable audit.");
            }
        }

        foreach (IConversionOperation conversion in operations.OfType<IConversionOperation>())
        {
            if (conversion.Conversion.IsUserDefined && conversion.OperatorMethod is IMethodSymbol operatorMethod &&
                IsSourceOwnedMethod(operatorMethod, compilation))
            {
                throw new ExtractionException(
                    $"ProcessBlock reaches source-owned user-defined conversion '{MethodSymbolId(operatorMethod)}' via '{path}' ({OperationCanonical(conversion)}).");
            }
        }

        foreach (IUnaryOperation unary in operations.OfType<IUnaryOperation>())
        {
            if (unary.OperatorMethod is IMethodSymbol operatorMethod && IsSourceOwnedMethod(operatorMethod, compilation))
            {
                throw new ExtractionException(
                    $"ProcessBlock reaches source-owned unary operator '{MethodSymbolId(operatorMethod)}' via '{path}' ({OperationCanonical(unary)}).");
            }
        }

        foreach (IBinaryOperation binary in operations.OfType<IBinaryOperation>())
        {
            if (binary.OperatorMethod is IMethodSymbol operatorMethod && IsSourceOwnedMethod(operatorMethod, compilation))
            {
                throw new ExtractionException(
                    $"ProcessBlock reaches source-owned binary operator '{MethodSymbolId(operatorMethod)}' via '{path}' ({OperationCanonical(binary)}).");
            }
        }

        foreach (ICompoundAssignmentOperation compound in operations.OfType<ICompoundAssignmentOperation>())
        {
            if (compound.OperatorMethod is IMethodSymbol operatorMethod && IsSourceOwnedMethod(operatorMethod, compilation))
            {
                throw new ExtractionException(
                    $"ProcessBlock reaches source-owned compound operator '{MethodSymbolId(operatorMethod)}' via '{path}' ({OperationCanonical(compound)}).");
            }
        }

        foreach (IIncrementOrDecrementOperation increment in operations.OfType<IIncrementOrDecrementOperation>())
        {
            if (increment.OperatorMethod is IMethodSymbol operatorMethod && IsSourceOwnedMethod(operatorMethod, compilation))
            {
                throw new ExtractionException(
                    $"ProcessBlock reaches source-owned increment/decrement operator '{MethodSymbolId(operatorMethod)}' via '{path}' ({OperationCanonical(increment)}).");
            }
        }

        foreach (ICollectionExpressionOperation collection in operations.OfType<ICollectionExpressionOperation>())
        {
            if (collection.ConstructMethod is IMethodSymbol constructMethod &&
                IsSourceOwnedMethod(constructMethod, compilation))
            {
                throw new ExtractionException(
                    $"ProcessBlock reaches source-owned collection construction method '{MethodSymbolId(constructMethod)}' via '{path}' ({OperationCanonical(collection)}).");
            }
        }

        foreach (IDelegateCreationOperation delegateCreation in operations.OfType<IDelegateCreationOperation>())
        {
            if (!IsKnownAuditedLambda(delegateCreation, current.Model, current) ||
                delegateCreation.Target is not IAnonymousFunctionOperation)
            {
                throw new ExtractionException(
                    $"ProcessBlock reaches an un-audited delegate creation via '{path}' ({OperationCanonical(delegateCreation)}); source delegate fields, method groups, and lambdas require an explicit typed callable audit.");
            }
        }

        foreach (IAnonymousFunctionOperation lambda in operations.OfType<IAnonymousFunctionOperation>())
        {
            if (!IsKnownAuditedLambda(lambda, current.Model, current))
            {
                throw new ExtractionException(
                    $"ProcessBlock reaches an un-audited lambda/anonymous callable via '{path}' ({OperationCanonical(lambda)}); source lambdas require an explicit typed callable audit.");
            }
        }

        foreach (IAddressOfOperation addressOf in operations.OfType<IAddressOfOperation>())
        {
            throw new ExtractionException(
                $"ProcessBlock reaches an un-audited function/address creation via '{path}' ({OperationCanonical(addressOf)}).");
        }

        foreach (IFunctionPointerInvocationOperation functionPointer in operations.OfType<IFunctionPointerInvocationOperation>())
        {
            throw new ExtractionException(
                $"ProcessBlock reaches an un-audited function-pointer invocation via '{path}' ({OperationCanonical(functionPointer)}).");
        }

        foreach (IDynamicObjectCreationOperation dynamicCreation in operations.OfType<IDynamicObjectCreationOperation>())
        {
            throw new ExtractionException(
                $"ProcessBlock reaches an un-audited dynamic object invocation via '{path}' ({OperationCanonical(dynamicCreation)}).");
        }

        foreach (IDynamicInvocationOperation dynamicInvocation in operations.OfType<IDynamicInvocationOperation>())
        {
            throw new ExtractionException(
                $"ProcessBlock reaches an un-audited dynamic member invocation via '{path}' ({OperationCanonical(dynamicInvocation)}).");
        }

        foreach (IDynamicMemberReferenceOperation dynamicMember in operations.OfType<IDynamicMemberReferenceOperation>())
        {
            throw new ExtractionException(
                $"ProcessBlock reaches an un-audited dynamic member reference via '{path}' ({OperationCanonical(dynamicMember)}).");
        }

        foreach (IDynamicIndexerAccessOperation dynamicIndexer in operations.OfType<IDynamicIndexerAccessOperation>())
        {
            throw new ExtractionException(
                $"ProcessBlock reaches an un-audited dynamic indexer access via '{path}' ({OperationCanonical(dynamicIndexer)}).");
        }

        foreach (IAwaitOperation awaitOperation in operations.OfType<IAwaitOperation>())
        {
            throw new ExtractionException(
                $"ProcessBlock reaches an un-audited await/custom await edge via '{path}' ({OperationCanonical(awaitOperation)}).");
        }

        foreach (IForEachLoopOperation forEach in operations.OfType<IForEachLoopOperation>())
        {
            bool exactLoop = MethodSymbolId(current.Symbol) == CountLogsMethodSymbol && IsExactCountLogsLoop(forEach) ||
                MethodSymbolId(current.Symbol) == CalculateBloomsMethodSymbol && IsExactCalculateBloomsLoop(forEach) ||
                MethodSymbolId(current.Symbol) == AccumulateBlockBloomMethodSymbol && IsExactAccumulateBlockBloomLoop(forEach);
            if (!exactLoop)
            {
                throw new ExtractionException(
                    $"ProcessBlock reaches an un-audited foreach/implicit enumerator edge via '{path}' ({OperationCanonical(forEach)}); only exact typed source loops are admitted.");
            }
        }

        foreach (IUsingOperation usingOperation in operations.OfType<IUsingOperation>())
        {
            bool sourceOwned = DescendantOperations(usingOperation.Resources)
                .Any(operation => IsSourceOwnedType(operation.Type, compilation));
            if ((sourceOwned && !IsExactInstrumentationUsing(current, usingOperation.Syntax)) ||
                HasSourceOwnedMethodReference(usingOperation, compilation))
            {
                throw new ExtractionException(
                    $"ProcessBlock reaches a source-owned using/dispose edge via '{path}' ({OperationCanonical(usingOperation)}).");
            }
        }

        foreach (IUsingDeclarationOperation usingDeclaration in operations.OfType<IUsingDeclarationOperation>())
        {
            bool sourceOwned = DescendantOperations(usingDeclaration.DeclarationGroup)
                .Any(operation => IsSourceOwnedType(operation.Type, compilation));
            if ((sourceOwned && !IsExactInstrumentationUsing(current, usingDeclaration.Syntax)) ||
                HasSourceOwnedMethodReference(usingDeclaration, compilation))
            {
                throw new ExtractionException(
                    $"ProcessBlock reaches a source-owned using/dispose declaration via '{path}' ({OperationCanonical(usingDeclaration)}).");
            }
        }

        foreach (IPropertyReferenceOperation propertyReference in operations.OfType<IPropertyReferenceOperation>())
        {
            if (HasExecutableSourceAccessor(propertyReference.Property, compilation))
            {
                throw new ExtractionException(
                    $"ProcessBlock reaches source-local property '{propertyReference.Property.Name}' with an executable accessor via '{path}'; source property accessors require an explicit typed callable audit.");
            }

            if (propertyReference.Property.Type.TypeKind == TypeKind.Delegate &&
                IsSourceOwnedSymbol(propertyReference.Property, compilation))
            {
                throw new ExtractionException(
                    $"ProcessBlock reaches source-local delegate property '{propertyReference.Property.Name}' via '{path}'; delegate property reads and accessors require an explicit typed callable audit.");
            }
        }

        foreach (IImplicitIndexerReferenceOperation indexerReference in operations.OfType<IImplicitIndexerReferenceOperation>())
        {
            if (indexerReference.IndexerSymbol is IPropertySymbol property && HasExecutableSourceAccessor(property, compilation))
            {
                throw new ExtractionException(
                    $"ProcessBlock reaches source-local indexer '{property.Name}' with an executable accessor via '{path}'; source property accessors require an explicit typed callable audit.");
            }
        }

        foreach (IFieldReferenceOperation fieldReference in operations.OfType<IFieldReferenceOperation>())
        {
            if (fieldReference.Field.Type.TypeKind == TypeKind.Delegate &&
                IsSourceOwnedSymbol(fieldReference.Field, compilation) &&
                !IsTransactionsExecutedBackingField(fieldReference.Field))
            {
                throw new ExtractionException(
                    $"ProcessBlock reaches source-local delegate field '{fieldReference.Field.Name}' via '{path}'; delegate field reads and initializers require an explicit typed callable audit.");
            }
        }

        foreach (IEventReferenceOperation eventReference in operations.OfType<IEventReferenceOperation>())
        {
            if (HasExecutableSourceAccessor(eventReference.Event, compilation))
            {
                throw new ExtractionException(
                    $"ProcessBlock reaches source-local event '{eventReference.Event.Name}' with an executable accessor via '{path}'; source event accessors require an explicit typed callable audit.");
            }

            if (eventReference.Event.Type.TypeKind == TypeKind.Delegate &&
                IsSourceOwnedSymbol(eventReference.Event, compilation) &&
                !IsExactTransactionsExecutedEventReference(eventReference, current.Model))
            {
                throw new ExtractionException(
                    $"ProcessBlock reaches source-local delegate event '{eventReference.Event.Name}' via '{path}'; event reads and accessors require an explicit typed callable audit.");
            }
        }

        foreach (IEventAssignmentOperation eventAssignment in operations.OfType<IEventAssignmentOperation>())
        {
            if (eventAssignment.EventReference is IEventReferenceOperation eventReference &&
                IsSourceOwnedSymbol(eventReference.Event, compilation))
            {
                throw new ExtractionException(
                    $"ProcessBlock reaches source-local event assignment '{OperationCanonical(eventAssignment)}' via '{path}'; source event add/remove routes require an explicit typed callable audit.");
            }
        }

        foreach (IAnonymousFunctionOperation lambda in operations.OfType<IAnonymousFunctionOperation>())
        {
            if (!IsKnownAuditedLambda(lambda, current.Model, current))
            {
                continue;
            }

            IOperation body = lambda.Body ?? throw new ExtractionException(
                $"ProcessBlock reaches the audited Task.Run lambda via '{path}', but its typed body is missing.");
            if (body.Syntax is not SyntaxNode bodySyntax)
            {
                throw new ExtractionException(
                    $"ProcessBlock reaches the audited Task.Run lambda via '{path}', but its body has no source syntax.");
            }

            string lambdaPath = IsKnownTaskRunLambda(lambda, current.Model, current)
                ? $"{path}->Task.Run lambda"
                : $"{path}->ParallelUnbalancedWork.For lambda";
            ValidateSourceOwnedOperationEdges(current, lambdaPath, body, localMethods, compilation, visited);
            VisitSourceLocalInvocations(current, bodySyntax, lambdaPath, false, localMethods, compilation, visited);
            VisitSourceLocalMethodReferences(current, body, lambdaPath, false, localMethods, compilation, visited);
        }
    }

    private static bool HasSourceOwnedMethodReference(IOperation operation, Compilation compilation) =>
        ExecutableDescendantOperations(operation).OfType<IMethodReferenceOperation>()
            .Any(reference => IsSourceOwnedMethod(reference.Method, compilation));

    private static bool IsSourceOwnedTypeDefinition(ITypeSymbol? type, Compilation compilation) =>
        type is not null && (IsSourceOwnedSymbol(type, compilation) ||
            IsSourceOwnedSymbol(type.OriginalDefinition, compilation));

    private static bool IsExactTransactionsExecutedEventReference(
        IEventReferenceOperation reference,
        SemanticModel model)
    {
        ConditionalAccessExpressionSyntax? conditional = reference.Syntax?.AncestorsAndSelf()
            .OfType<ConditionalAccessExpressionSyntax>()
            .FirstOrDefault();
        InvocationExpressionSyntax? invocation = reference.Syntax?.AncestorsAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault() ?? reference.Syntax?.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault() ?? conditional?.WhenNotNull.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault();
        return invocation is not null && model.GetOperation(invocation) is IInvocationOperation operation &&
            IsAuditedTransactionsExecutedHook(invocation, operation, model);
    }

    private static bool IsTransactionsExecutedBackingField(IFieldSymbol symbol) =>
        symbol.AssociatedSymbol is IEventSymbol eventSymbol &&
        eventSymbol.Name == TransactionsExecutedEventName &&
        eventSymbol.ContainingType?.ToSourceIdentity() == BlockProcessorTypeSymbol;

    private static bool IsExactInstrumentationUsing(SourceLocalMethod current, SyntaxNode syntax)
    {
        string member = MethodSymbolId(current.Symbol);
        LocalDeclarationStatementSyntax? localDeclaration = syntax as LocalDeclarationStatementSyntax ??
            syntax.AncestorsAndSelf().OfType<LocalDeclarationStatementSyntax>().FirstOrDefault();
        if (localDeclaration is not null)
        {
            return member switch
            {
                CommitStateMethodSymbol => Canonical(localDeclaration) == "usingMetricsTimer<CommitTimeSink>_=new();",
                CommitRootsMethodSymbol => Canonical(localDeclaration) == "usingMetricsTimer<StorageMerkleTimeSink>_=new();",
                CalculateBloomsMethodSymbol => Canonical(localDeclaration) == "usingMetricsTimer<BloomsTimeSink>_=new();",
                CalculateReceiptsRootMethodSymbol => Canonical(localDeclaration) == "usingMetricsTimer<ReceiptsRootTimeSink>_=new();",
                _ => false,
            };
        }

        UsingStatementSyntax? usingStatement = syntax as UsingStatementSyntax ??
            syntax.AncestorsAndSelf().OfType<UsingStatementSyntax>().FirstOrDefault();
        return usingStatement?.Declaration is VariableDeclarationSyntax declaration &&
            (member == ComputeStateRootMethodSymbol && Canonical(declaration) == "MetricsTimer<StateRootTimeSink>_=new()" ||
             member == CalculateBloomsMethodSymbol && Canonical(declaration) == "MetricsTimer<BloomsTimeSink>_=new()" ||
             member == CalculateReceiptsRootMethodSymbol && Canonical(declaration) == "MetricsTimer<ReceiptsRootTimeSink>_=new()");
    }

    private static bool IsExactCountLogsLoop(IForEachLoopOperation operation)
    {
        if (operation.Syntax is not ForEachStatementSyntax syntax ||
            Canonical(syntax.Expression) != "receipts" ||
            !IsExactReceiptArrayCollection(operation.Collection) ||
            syntax.Statement is not BlockSyntax body || body.Statements.Count != 1)
        {
            return false;
        }

        return body.Statements[0] is ExpressionStatementSyntax
        {
            Expression: AssignmentExpressionSyntax assignment
        } && Canonical(assignment) == "count+=t.Logs?.Length??0";
    }

    private static bool IsExactCalculateBloomsLoop(IForEachLoopOperation operation)
    {
        if (operation.Syntax is not ForEachStatementSyntax syntax ||
            Canonical(syntax.Expression) != "receipts" ||
            !IsExactReceiptArrayCollection(operation.Collection) ||
            syntax.Statement is not BlockSyntax body || body.Statements.Count != 1)
        {
            return false;
        }

        return body.Statements[0] is ExpressionStatementSyntax
        {
            Expression: InvocationExpressionSyntax invocation,
        } && Canonical(invocation) == "t.CalculateBloom()";
    }

    private static bool IsExactAccumulateBlockBloomLoop(IForEachLoopOperation operation)
    {
        if (operation.Syntax is not ForEachStatementSyntax syntax ||
            Canonical(syntax.Expression) != "receipts" ||
            !IsExactReceiptArrayCollection(operation.Collection) ||
            syntax.Statement is not BlockSyntax body || body.Statements.Count != 1)
        {
            return false;
        }

        return body.Statements[0] is ExpressionStatementSyntax
        {
            Expression: InvocationExpressionSyntax invocation,
        } && Canonical(invocation) == "blockBloom.Accumulate(t.Bloom!)";
    }

    private static bool IsExactReceiptArrayCollection(IOperation collection)
    {
        while (collection is IConversionOperation { IsImplicit: true, OperatorMethod: null,
                   Conversion.IsUserDefined: false } conversion) collection = conversion.Operand;
        return collection is IParameterReferenceOperation { Parameter.Name: "receipts", Type: IArrayTypeSymbol { Rank: 1 } } &&
            ArtifactSafety.TypeIdentity(collection.Type) == ReceiptsOperationType;
    }

    private static SourceLocalMethod[] ValidateProcessSourceLocalCallClosure(
        MethodDeclarationSyntax processBlock,
        SemanticModel processModel,
        Compilation compilation)
    {
        IMethodSymbol processSymbol = processModel.GetDeclaredSymbol(processBlock) as IMethodSymbol
            ?? throw new ExtractionException("ProcessBlock has no typed source-local method symbol.");
        if (MethodSymbolId(processSymbol) != ProcessBlockMethodSymbol)
        {
            throw new ExtractionException("ProcessBlock did not bind to the pinned BlockProcessor source method.");
        }

        SourceLocalMethod[] localMethods = BuildSourceLocalMethods(compilation);
        SourceLocalMethod root = ResolveSourceLocalMethod(localMethods, processSymbol, compilation)
            ?? throw new ExtractionException("ProcessBlock has no pinned source-local declaration in the source closure.");
        HashSet<string> visited = new(StringComparer.Ordinal);
        VisitSourceLocalMethod(root, "ProcessBlock", true, localMethods, compilation, visited);
        return localMethods.Where(method => MethodSymbolId(method.Symbol) != ProcessBlockMethodSymbol &&
                visited.Contains(MethodSymbolId(method.Symbol)) &&
                (method.Syntax.Body is not null || method.Syntax.ExpressionBody is not null))
            .OrderBy(static method => MethodSymbolId(method.Symbol), StringComparer.Ordinal).ToArray();
    }

    private static SourceLocalMethod[] BuildSourceLocalMethods(Compilation compilation)
    {
        List<SourceLocalMethod> methods = [];
        foreach (SyntaxTree tree in compilation.SyntaxTrees.Where(IsSemanticTree))
        {
            SemanticModel model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            foreach (MethodDeclarationSyntax method in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(method) is not IMethodSymbol symbol)
                {
                    continue;
                }

                methods.Add(new(symbol, method, model, tree.FilePath));
            }
        }

        return methods.ToArray();
    }

    private static SourceLocalMethod? ResolveSourceLocalMethod(
        IReadOnlyList<SourceLocalMethod> methods,
        IMethodSymbol target,
        Compilation compilation)
    {
        SourceLocalMethod[] matches = methods
            .Where(candidate => SameMethodSymbol(candidate.Symbol, target))
            .ToArray();
        if (matches.Length == 0)
        {
            if (HasExecutableSourceDeclaration(target, compilation))
            {
                throw new ExtractionException(
                    $"Source-local method '{MethodSymbolId(target)}' has an executable pinned declaration that could not be resolved into the typed closure.");
            }

            return null;
        }

        SourceLocalMethod[] executable = matches.Where(static candidate =>
            candidate.Syntax.Body is not null || candidate.Syntax.ExpressionBody is not null).ToArray();
        if (executable.Length > 1)
        {
            throw new ExtractionException(
                $"Source-local method '{MethodSymbolId(target)}' does not resolve to exactly one executable pinned declaration.");
        }

        if (executable.Length == 1)
        {
            return executable[0];
        }

        if (HasExecutableSourceDeclaration(target, compilation))
        {
            throw new ExtractionException(
                $"Source-local method '{MethodSymbolId(target)}' has an executable pinned declaration that could not be resolved into the typed closure.");
        }

        return null;
    }

    private static bool HasExecutableSourceDeclaration(IMethodSymbol target, Compilation compilation)
    {
        foreach (SyntaxReference reference in target.DeclaringSyntaxReferences.Where(reference =>
                     compilation.ContainsSyntaxTree(reference.SyntaxTree) && IsSemanticTree(reference.SyntaxTree)))
        {
            SyntaxNode syntax = reference.GetSyntax();
            if (IsExecutableSourceCallableSyntax(syntax))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExecutableSourceCallableSyntax(SyntaxNode syntax) =>
        syntax switch
        {
            MethodDeclarationSyntax method => method.Body is not null || method.ExpressionBody is not null,
            ConstructorDeclarationSyntax constructor => constructor.Body is not null || constructor.ExpressionBody is not null,
            OperatorDeclarationSyntax operatorDeclaration => operatorDeclaration.Body is not null || operatorDeclaration.ExpressionBody is not null,
            ConversionOperatorDeclarationSyntax conversion => conversion.Body is not null || conversion.ExpressionBody is not null,
            LocalFunctionStatementSyntax localFunction => localFunction.Body is not null || localFunction.ExpressionBody is not null,
            _ => false,
        };

    private static bool SameMethodSymbol(IMethodSymbol left, IMethodSymbol right) =>
        MethodSymbolVariants(left).Any(leftVariant =>
            MethodSymbolVariants(right).Any(rightVariant =>
                SymbolEqualityComparer.Default.Equals(leftVariant, rightVariant)));

    private static IEnumerable<IMethodSymbol> MethodSymbolVariants(IMethodSymbol symbol)
    {
        Queue<IMethodSymbol> pending = new();
        List<IMethodSymbol> visited = [];
        pending.Enqueue(symbol);
        while (pending.TryDequeue(out IMethodSymbol? current))
        {
            if (visited.Any(existing => SymbolEqualityComparer.Default.Equals(existing, current)))
            {
                continue;
            }

            visited.Add(current);
            yield return current;
            if (current.OriginalDefinition is IMethodSymbol original && !SymbolEqualityComparer.Default.Equals(original, current))
            {
                pending.Enqueue(original);
            }

            if (current.PartialDefinitionPart is IMethodSymbol definition)
            {
                pending.Enqueue(definition);
            }

            if (current.PartialImplementationPart is IMethodSymbol implementation)
            {
                pending.Enqueue(implementation);
            }
        }
    }

    private static void VisitSourceLocalMethod(
        SourceLocalMethod current,
        string path,
        bool isRoot,
        IReadOnlyList<SourceLocalMethod> localMethods,
        Compilation compilation,
        HashSet<string> visited)
    {
        if (!visited.Add(MethodSymbolId(current.Symbol)))
        {
            return;
        }

        bool isCommitState = MethodSymbolId(current.Symbol) == CommitStateMethodSymbol;
        bool isCommitRoots = MethodSymbolId(current.Symbol) == CommitRootsMethodSymbol;
        bool isComputeStateRoot = MethodSymbolId(current.Symbol) == ComputeStateRootMethodSymbol;
        bool isSetAccountChanges = MethodSymbolId(current.Symbol) == SetAccountChangesMethodSymbol;
        if (isCommitState || isCommitRoots)
        {
            ValidateSelectedCommitHelperBody(current, isCommitRoots);
            ValidateNoTrackedWritesInSourceLocalMethod(current, path);
        }
        else if (!isRoot && !isComputeStateRoot && !isSetAccountChanges)
        {
            ValidateUnboundSourceLocalWrites(current, path);
        }

        VisitSourceLocalInvocations(current, current.Syntax, path, isRoot, localMethods, compilation, visited);

        IOperation currentOperation = current.Model.GetOperation(current.Syntax)
            ?? throw new ExtractionException($"ProcessBlock source-local helper '{path}' has no typed body operation.");
        ValidateSourceOwnedOperationEdges(current, path, currentOperation, localMethods, compilation, visited);

        VisitSourceLocalMethodReferences(current, currentOperation, path, isRoot, localMethods, compilation, visited);
    }

    private static IEnumerable<InvocationExpressionSyntax> ExecutableInvocationNodes(SyntaxNode root)
    {
        if (root is InvocationExpressionSyntax rootInvocation)
        {
            yield return rootInvocation;
        }

        foreach (InvocationExpressionSyntax candidate in root.DescendantNodes(static node =>
                         node is not AnonymousFunctionExpressionSyntax && node is not LocalFunctionStatementSyntax)
                     .OfType<InvocationExpressionSyntax>()
                     .OrderBy(static invocation => invocation.SpanStart))
        {
            yield return candidate;
        }
    }

    private static void VisitSourceLocalInvocations(
        SourceLocalMethod current,
        SyntaxNode syntaxRoot,
        string path,
        bool isRoot,
        IReadOnlyList<SourceLocalMethod> localMethods,
        Compilation compilation,
        HashSet<string> visited)
    {
        bool isCommitState = MethodSymbolId(current.Symbol) == CommitStateMethodSymbol;
        bool isCommitRoots = MethodSymbolId(current.Symbol) == CommitRootsMethodSymbol;
        foreach (InvocationExpressionSyntax invocation in ExecutableInvocationNodes(syntaxRoot))
        {
            IOperation? rawOperation = current.Model.GetOperation(invocation);
            if (rawOperation is INameOfOperation) continue;
            if (rawOperation is IDynamicInvocationOperation dynamicInvocation)
            {
                throw new ExtractionException(
                    $"ProcessBlock reaches an un-audited dynamic member invocation via '{path}' ({OperationCanonical(dynamicInvocation)}).");
            }

            if (rawOperation is IFunctionPointerInvocationOperation functionPointer)
            {
                throw new ExtractionException(
                    $"ProcessBlock reaches an un-audited function-pointer invocation via '{path}' ({OperationCanonical(functionPointer)}).");
            }

            if (rawOperation is not IInvocationOperation operation ||
                operation.TargetMethod is null || HasErrorSymbol(operation.TargetMethod))
            {
                throw new ExtractionException(
                    $"ProcessBlock source-local call '{Canonical(invocation)}' in '{path}' has no exact typed target.");
            }

            IMethodSymbol target = operation.TargetMethod;
            if (IsDelegateInvocation(operation))
            {
                if (MethodSymbolId(current.Symbol) != ProcessBlockMethodSymbol ||
                    !IsAuditedTransactionsExecutedHook(invocation, operation, current.Model))
                {
                    throw new ExtractionException(
                        $"ProcessBlock reaches an un-audited delegate invocation '{Canonical(invocation)}' via '{path}'; source delegate fields, properties, accessors, method groups, and lambdas require an explicit typed callable audit.");
                }

                continue;
            }

            if (IsUnboundSourceDispatch(invocation, operation, localMethods, compilation))
            {
                throw new ExtractionException(
                    $"ProcessBlock reaches unbound source interface/virtual dispatch '{Canonical(invocation)}' -> '{MethodSymbolId(target)}' via '{path}'; exact receiver implementation is not bound.");
            }

            if (IsDirectStateProviderCommit(invocation, current.Model))
            {
                if (!isCommitState && !isCommitRoots && MethodSymbolId(current.Symbol) != ApplyMinerRewardsMethodSymbol)
                {
                    throw new ExtractionException(
                        $"ProcessBlock reaches unwhitelisted source-local helper '{current.Syntax.Identifier.ValueText}' ({MethodSymbolId(current.Symbol)}) via '{path}' that transitively reaches IWorldState.Commit.");
                }

                continue;
            }

            SourceLocalMethod? callee = ResolveSourceLocalMethod(localMethods, target, compilation);
            if (callee is null)
            {
                if (HasExecutableSourceDeclaration(target, compilation))
                {
                    throw new ExtractionException(
                        $"ProcessBlock source-local call '{Canonical(invocation)}' via '{path}' has an executable pinned declaration that could not be resolved into the typed closure.");
                }

                if (target.ContainingType?.ToSourceIdentity() == BlockProcessorTypeSymbol)
                {
                    throw new ExtractionException(
                        $"ProcessBlock source-local call '{Canonical(invocation)}' via '{path}' has no pinned BlockProcessor declaration.");
                }

                continue;
            }

            VisitSourceLocalCallee(current, callee, path, isRoot, localMethods, compilation, visited);
        }
    }

    private static void VisitSourceLocalMethodReferences(
        SourceLocalMethod current,
        IOperation currentOperation,
        string path,
        bool isRoot,
        IReadOnlyList<SourceLocalMethod> localMethods,
        Compilation compilation,
        HashSet<string> visited)
    {
        bool isCommitState = MethodSymbolId(current.Symbol) == CommitStateMethodSymbol;
        bool isCommitRoots = MethodSymbolId(current.Symbol) == CommitRootsMethodSymbol;
        foreach (IMethodReferenceOperation reference in ExecutableDescendantOperations(currentOperation)
                     .OfType<IMethodReferenceOperation>())
        {
            if (IsUnboundSourceMethodReferenceDispatch(reference, localMethods, compilation) &&
                !IsExactBoundSourceMethodReference(reference, current.Model, compilation))
            {
                throw new ExtractionException(
                    $"ProcessBlock reaches unbound source interface/virtual method reference '{MethodSymbolId(reference.Method)}' via '{path}'; exact receiver implementation is not bound.");
            }

            if (IsDirectStateProviderCommit(reference.Method))
            {
                if (!isCommitState && !isCommitRoots && MethodSymbolId(current.Symbol) != ApplyMinerRewardsMethodSymbol)
                {
                    throw new ExtractionException(
                        $"ProcessBlock reaches unwhitelisted source-local helper '{current.Syntax.Identifier.ValueText}' ({MethodSymbolId(current.Symbol)}) via '{path}' that transitively reaches IWorldState.Commit.");
                }

                continue;
            }

            SourceLocalMethod? callee = ResolveSourceLocalMethod(localMethods, reference.Method, compilation);
            if (callee is null)
            {
                if (HasExecutableSourceDeclaration(reference.Method, compilation))
                {
                    throw new ExtractionException(
                        $"ProcessBlock source-local method reference '{MethodSymbolId(reference.Method)}' via '{path}' has an executable pinned declaration that could not be resolved into the typed closure.");
                }

                if (reference.Method.ContainingType?.ToSourceIdentity() == BlockProcessorTypeSymbol)
                {
                    throw new ExtractionException(
                        $"ProcessBlock source-local method reference '{MethodSymbolId(reference.Method)}' via '{path}' has no pinned BlockProcessor declaration.");
                }

                continue;
            }

            VisitSourceLocalCallee(current, callee, path, isRoot, localMethods, compilation, visited);
        }
    }

    private static void VisitSourceLocalCallee(
        SourceLocalMethod current,
        SourceLocalMethod callee,
        string path,
        bool isRoot,
        IReadOnlyList<SourceLocalMethod> localMethods,
        Compilation compilation,
        HashSet<string> visited)
    {
        string calleeId = MethodSymbolId(callee.Symbol);
        if (!isRoot && (calleeId == CommitStateMethodSymbol || calleeId == CommitRootsMethodSymbol ||
            calleeId == ComputeStateRootMethodSymbol || calleeId == SetAccountChangesMethodSymbol))
        {
            throw new ExtractionException(
                $"ProcessBlock reaches bound helper '{callee.Syntax.Identifier.ValueText}' through unwhitelisted source-local helper '{current.Syntax.Identifier.ValueText}' via '{path}'.");
        }

        VisitSourceLocalMethod(callee, $"{path}->{callee.Syntax.Identifier.ValueText}", false, localMethods, compilation, visited);
    }

    private static void ValidateSelectedCommitHelperBody(SourceLocalMethod helper, bool expectedRoots)
    {
        string expectedCanonical = expectedRoots
            ? "_stateProvider.Commit(spec,commitRoots:true)"
            : "_stateProvider.Commit(spec,commitRoots:false)";
        InvocationExpressionSyntax[] directCalls = helper.Syntax.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => IsDirectStateProviderCommit(invocation, helper.Model))
            .ToArray();
        if (directCalls.Length != 1 || Canonical(directCalls[0]) != expectedCanonical)
        {
            throw new ExtractionException(
                $"{helper.Syntax.Identifier.ValueText} must contain exactly one typed direct _stateProvider.Commit with commitRoots:{expectedRoots.ToString().ToLowerInvariant()}.");
        }
    }

    private static void ValidateNoTrackedWritesInSourceLocalMethod(SourceLocalMethod method, string path)
    {
        foreach ((string propertySymbol, string propertyName) in TrackedPropertySymbols())
        {
            ValidatePropertyWriteAudit(method.Model, method.Syntax, propertySymbol, null,
                $"bound commit helper '{method.Syntax.Identifier.ValueText}' ({method.Path}) via '{path}' {propertyName}");
        }
    }

    private static void ValidateUnboundSourceLocalWrites(SourceLocalMethod method, string path)
    {
        foreach ((string propertySymbol, string propertyName) in TrackedPropertySymbols())
        {
            try
            {
                ValidatePropertyWriteAudit(method.Model, method.Syntax, propertySymbol, null,
                    $"source-local helper '{method.Syntax.Identifier.ValueText}' ({method.Path}) via '{path}' {propertyName}");
            }
            catch (ExtractionException exception)
            {
                throw new ExtractionException(
                    $"ProcessBlock reaches unwhitelisted source-local helper '{method.Syntax.Identifier.ValueText}' ({MethodSymbolId(method.Symbol)}) via '{path}' that writes tracked Header.StateRoot/Header.Hash/Block.AccountChanges. {exception.Message}");
            }
        }
    }

    private static IEnumerable<(string PropertySymbol, string PropertyName)> TrackedPropertySymbols()
    {
        yield return (StateRootPropertySymbol, "Header.StateRoot");
        yield return (HashPropertySymbol, "Header.Hash");
        yield return (AccountChangesPropertySymbol, "Block.AccountChanges");
    }

    private static bool HasExecutableSourceAccessor(ISymbol symbol, Compilation compilation)
    {
        foreach (SyntaxReference reference in symbol.DeclaringSyntaxReferences.Where(reference =>
                     compilation.ContainsSyntaxTree(reference.SyntaxTree) && IsSemanticTree(reference.SyntaxTree)))
        {
            SyntaxNode syntax = reference.GetSyntax();
            if (syntax is AccessorDeclarationSyntax accessor &&
                (accessor.Body is not null || accessor.ExpressionBody is not null))
            {
                return true;
            }

            if (syntax is PropertyDeclarationSyntax property &&
                (property.ExpressionBody is not null || property.AccessorList?.Accessors.Any(static accessor =>
                    accessor.Body is not null || accessor.ExpressionBody is not null) == true))
            {
                return true;
            }

            if (syntax is IndexerDeclarationSyntax indexer &&
                indexer.AccessorList?.Accessors.Any(static accessor =>
                    accessor.Body is not null || accessor.ExpressionBody is not null) == true)
            {
                return true;
            }

            if (syntax is EventDeclarationSyntax eventDeclaration &&
                eventDeclaration.AccessorList?.Accessors.Any(static accessor =>
                    accessor.Body is not null || accessor.ExpressionBody is not null) == true)
            {
                return true;
            }
        }

        return false;
    }

    private static string MethodSymbolId(IMethodSymbol symbol) =>
        symbol.ToSourceIdentity();

    private static HeaderAssignmentIdentity[] BuildHeaderAssignments(
        SourceFile source,
        MethodDeclarationSyntax method,
        SemanticModel model,
        Compilation compilation,
        IReadOnlyList<AnchorIdentity> anchors)
    {
        IfStatementSyntax blobGuard = FindIf(method,
            condition => Canonical(condition) == "spec.IsEip4844Enabled", "EIP-4844 assignment guard");
        IfStatementSyntax backgroundGuard = FindIf(method,
            condition => Canonical(condition) == "ShouldCalculateReceiptsInBackground(receipts)",
            "receipt assignment guard");
        IfStatementSyntax backgroundResultGuard = FindIf(method,
            condition => Canonical(condition) == TaskBackgroundResultPredicate,
            "background receipt-root assignment guard");

        AssignmentExpressionSyntax[] blobWrites = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(assignment => AssignmentTargetsProperty(assignment, model, BlobGasPropertySymbol)).ToArray();
        AssignmentExpressionSyntax[] selectedBlobAssignments = blobWrites.Where(assignment =>
            IsWithin(blobGuard.Statement, assignment) &&
            Canonical(assignment.Left) == HeaderAssignmentTargets[0] &&
            Canonical(assignment.Right) == HeaderAssignmentValues[0]).ToArray();
        if (blobWrites.Length != 1 || selectedBlobAssignments.Length != 1)
        {
            throw new ExtractionException("The EIP-4844 BlobGasUsed assignment is missing or redirected.");
        }

        AssignmentExpressionSyntax blobAssignment = selectedBlobAssignments[0];
        if (!IsDirectPropertyAssignment(blobAssignment, model, BlobGasPropertySymbol) ||
            blobAssignment.Right is not InvocationExpressionSyntax)
        {
            throw new ExtractionException("BlobGasUsed has a competing write or an untyped calculation source.");
        }
        InvocationExpressionSyntax blobValue = (InvocationExpressionSyntax)blobAssignment.Right;
        if (!IsExactInvocation(blobValue, model, BlobGasValueSymbol))
        {
            throw new ExtractionException("BlobGasUsed has an untyped calculation source.");
        }

        AssignmentExpressionSyntax[] receiptsRootWrites = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(assignment => AssignmentTargetsProperty(assignment, model, ReceiptsRootPropertySymbol)).ToArray();
        AssignmentExpressionSyntax[] selectedReceiptsRootAssignments = receiptsRootWrites.Where(assignment =>
            IsWithin(backgroundGuard.Else!.Statement, assignment) &&
            IsDirectPropertyAssignment(assignment, model, ReceiptsRootPropertySymbol) &&
            Canonical(assignment.Left) == HeaderAssignmentTargets[1] &&
            Canonical(assignment.Right) == HeaderAssignmentValues[1]).ToArray();
        if (receiptsRootWrites.Length != 2 || selectedReceiptsRootAssignments.Length != 1)
        {
            throw new ExtractionException("The synchronous ReceiptsRoot assignment is missing or redirected.");
        }

        AssignmentExpressionSyntax receiptsRootAssignment = selectedReceiptsRootAssignments[0];
        if (receiptsRootWrites.Count(assignment => ReferenceEquals(assignment, receiptsRootAssignment)) != 1 ||
            receiptsRootAssignment.Right is not InvocationExpressionSyntax)
        {
            throw new ExtractionException("ReceiptsRoot has an unexpected competing write or an untyped calculation source.");
        }
        InvocationExpressionSyntax receiptsRootValue = (InvocationExpressionSyntax)receiptsRootAssignment.Right;
        if (!IsExactInvocation(receiptsRootValue, model, ReceiptsRootValueSymbol))
        {
            throw new ExtractionException("ReceiptsRoot has an untyped calculation source.");
        }

        AssignmentExpressionSyntax excludedReceiptsRootAssignment = receiptsRootWrites.Single(assignment =>
            !ReferenceEquals(assignment, receiptsRootAssignment));
        if (!IsWithin(backgroundResultGuard.Statement, excludedReceiptsRootAssignment) ||
            Canonical(excludedReceiptsRootAssignment) != BackgroundHeaderAssignment)
        {
            throw new ExtractionException("The only excluded ReceiptsRoot write is not the guarded background result assignment.");
        }
        MemberAccessExpressionSyntax[] excludedReceiptsRootTargets = excludedReceiptsRootAssignment.Left
            .DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>()
            .Where(member => Canonical(member) == HeaderAssignmentTargets[1]).ToArray();
        if (excludedReceiptsRootTargets.Length != 1)
        {
            throw new ExtractionException("The excluded background result has no unique typed ReceiptsRoot target.");
        }
        MemberAccessExpressionSyntax excludedReceiptsRootTarget = excludedReceiptsRootTargets[0];

        ValidateOnlyExpectedGuarding(blobAssignment, "BlobGasUsed assignment",
            condition => condition == "spec.IsEip4844Enabled");
        ValidateOnlyExpectedGuarding(blobValue, "BlobGasUsed calculation",
            condition => condition == "spec.IsEip4844Enabled");
        ValidateOnlyExpectedGuarding(receiptsRootAssignment, "synchronous ReceiptsRoot assignment",
            condition => condition == "ShouldCalculateReceiptsInBackground(receipts)");
        ValidateOnlyExpectedGuarding(receiptsRootValue, "synchronous ReceiptsRoot calculation",
            condition => condition == "ShouldCalculateReceiptsInBackground(receipts)");
        ValidateOnlyExpectedGuarding(excludedReceiptsRootAssignment, "background ReceiptsRoot assignment",
            condition => condition == TaskBackgroundResultPredicate);

        IOperation blobAssignmentOperation = model.GetOperation(blobAssignment)
            ?? throw new ExtractionException("BlobGasUsed has no assignment operation.");
        IOperation blobValueOperation = model.GetOperation(blobValue)
            ?? throw new ExtractionException("BlobGasUsed has no calculation operation.");
        IOperation receiptsRootAssignmentOperation = model.GetOperation(receiptsRootAssignment)
            ?? throw new ExtractionException("ReceiptsRoot has no assignment operation.");
        IOperation receiptsRootValueOperation = model.GetOperation(receiptsRootValue)
            ?? throw new ExtractionException("ReceiptsRoot has no calculation operation.");
        IOperation excludedOperation = model.GetOperation(excludedReceiptsRootTarget)
            ?? throw new ExtractionException("The excluded background ReceiptsRoot target has no operation.");

        return
        [
            new(
                HeaderAssignmentIds[0],
                HeaderAssignmentAnchors[0],
                HeaderAssignmentGuardAnchors[0],
                 HeaderAssignmentArms[0],
                 HeaderAssignmentTargets[0],
                 HeaderAssignmentValues[0],
                 [],
                 BindNode(source, method, blobAssignment, model, compilation, null, blobAssignmentOperation,
                    model.GetSymbolInfo(blobAssignment.Left).Symbol),
                BindNode(source, method, blobValue, model, compilation, null, blobValueOperation,
                    model.GetSymbolInfo(blobValue).Symbol),
                Anchor(anchors, HeaderAssignmentGuardAnchors[0]).Binding,
                []),
            new(
                HeaderAssignmentIds[1],
                HeaderAssignmentAnchors[1],
                HeaderAssignmentGuardAnchors[1],
                 HeaderAssignmentArms[1],
                 HeaderAssignmentTargets[1],
                 HeaderAssignmentValues[1],
                 [BackgroundHeaderAssignment],
                 BindNode(source, method, receiptsRootAssignment, model, compilation, null,
                    receiptsRootAssignmentOperation, model.GetSymbolInfo(receiptsRootAssignment.Left).Symbol),
                BindNode(source, method, receiptsRootValue, model, compilation, null, receiptsRootValueOperation,
                    model.GetSymbolInfo(receiptsRootValue).Symbol),
                Anchor(anchors, HeaderAssignmentGuardAnchors[1]).Binding,
                 [BindNode(source, method, excludedReceiptsRootTarget, model, compilation, null, excludedOperation,
                     model.GetSymbolInfo(excludedReceiptsRootTarget).Symbol)]),
        ];
    }

    private static GuardIdentity[] BuildGuards(IReadOnlyList<AnchorIdentity> anchors) =>
    [
        new("eip4844", "spec.IsEip4844Enabled", "true-arm", "blob-gas", "none", Anchor(anchors, "block.blob-gas-guard").Binding),
        new("background-receipts", "ShouldCalculateReceiptsInBackground(receipts)", "false-arm", "synchronous-blooms-and-root", "Task.Run", Anchor(anchors, "block.receipts-background-guard").Binding),
        new("main-thread", "BlockchainProcessor.IsMainProcessingThread", "true-arm", "account-changes", "none", Anchor(anchors, "block.main-thread-guard").Binding),
        new("state-root", "ShouldComputeStateRoot(header)", "true-arm", "state-root", "none", Anchor(anchors, "block.state-root-guard").Binding),
        new("background-result", TaskBackgroundResultPredicate, "false-arm", "none", "GetResult assignment", Anchor(anchors, "block.background-result-guard").Binding),
        new("background-finally", TaskFinallyPredicate, "false-arm", "none", "task observation", Anchor(anchors, "block.background-finally-guard").Binding),
    ];

    private static StepIdentity[] BuildSteps(IReadOnlyList<AnchorIdentity> anchors) =>
    [
        new(0, StepIds[0], "block.post-transaction-commit", "always", "commit-no-roots", Anchor(anchors, "block.post-transaction-commit").Binding),
        new(1, StepIds[1], "block.blob-gas-calculation", "spec.IsEip4844Enabled", "header.BlobGasUsed", Anchor(anchors, "block.blob-gas-calculation").Binding),
        new(2, StepIds[2], "block.background-task-null", "always", "background-task=null", Anchor(anchors, "block.background-task-null").Binding),
        new(3, StepIds[3], "block.sync-blooms", "background=false", "receipt-blooms", Anchor(anchors, "block.sync-blooms").Binding),
        new(4, StepIds[4], "block.sync-receipts-root", "background=false", "header.ReceiptsRoot", Anchor(anchors, "block.sync-receipts-root").Binding),
        new(5, StepIds[5], "block.rewards", "normal-return", "rewards-hook", Anchor(anchors, "block.rewards").Binding),
        new(6, StepIds[6], "block.withdrawals", "normal-return", "withdrawals-hook", Anchor(anchors, "block.withdrawals").Binding),
        new(7, StepIds[7], "block.finalization-commit", "always", "commit-no-roots", Anchor(anchors, "block.finalization-commit").Binding),
        new(8, StepIds[8], "block.execution-requests", "normal-return", "execution-requests-hook", Anchor(anchors, "block.execution-requests").Binding),
        new(9, StepIds[9], "block.end-block-trace", "background=false", "EndBlockTrace(true)", Anchor(anchors, "block.end-block-trace").Binding),
        new(10, StepIds[10], "block.storage-roots-commit", "always", "commit-roots", Anchor(anchors, "block.storage-roots-commit").Binding),
        new(11, StepIds[11], "block.account-changes", "main-thread=true", "account-changes", Anchor(anchors, "block.account-changes").Binding),
        new(12, StepIds[12], "block.state-root", "state-root=true", "state-root", Anchor(anchors, "block.state-root").Binding),
        new(13, StepIds[13], "block.bal-finalization", "BAL-disabled", "BAL-finalization-observation", Anchor(anchors, "block.bal-finalization").Binding),
        new(14, StepIds[14], "block.hash", "normal-return", "header.Hash", Anchor(anchors, "block.hash").Binding),
        new(15, StepIds[15], "block.return-receipts", "normal-return", "return receipts", Anchor(anchors, "block.return-receipts").Binding),
    ];

    private static OpaqueDelegateIdentity[] BuildOpaqueDelegates(IReadOnlyList<AnchorIdentity> anchors) =>
    [
        new("miner-rewards", "block.rewards", true, "throws are outside the normal-return theorem"),
        new("withdrawals", "block.withdrawals", true, "throws are outside the normal-return theorem"),
        new("execution-requests", "block.execution-requests", true, "throws are outside the normal-return theorem"),
        new("calculate-blooms", "block.sync-blooms", true, "throws are outside the normal-return theorem"),
        new("calculate-receipts-root", "block.sync-receipts-root", true, "throws are outside the normal-return theorem"),
        new("end-block-trace", "block.end-block-trace", true, "throws are outside the normal-return theorem"),
        new("account-changes", "block.account-changes", true, "throws are outside the normal-return theorem"),
        new("state-root", "block.state-root", true, "throws are outside the normal-return theorem"),
        new("bal-finalization", "block.bal-finalization", true, "throws are outside the normal-return theorem"),
        new("header-hash", "block.hash", true, "throws are outside the normal-return theorem"),
    ];

    private static void ValidateNormalTailControlFlow(
        MethodDeclarationSyntax method,
        ControlFlowGraph graph,
        IReadOnlyList<AnchorIdentity> anchors)
    {
        BasicBlock[] reachable = graph.Blocks.Where(static block => block.IsReachable).ToArray();
        if (reachable.Length == 0)
        {
            throw new ExtractionException("ProcessBlock tail has no reachable CFG blocks.");
        }

        ReturnStatementSyntax returnStatement = FindOuterReceiptsReturn(method);
        BasicBlock returnBlock = RequireReachableBlock(graph, returnStatement, "normal receipts return");
        Dictionary<int, HashSet<int>> dominators = ComputeDominators(graph, reachable);

        foreach (AnchorIdentity anchor in anchors)
        {
            ValidateNoUnexpectedGuarding(anchor.Id,
                FindNodeAtPosition(method, anchor.Binding.Position, anchor.Id));
        }

        foreach (string anchorId in UnconditionalTailAnchorIds)
        {
            AnchorIdentity anchor = Anchor(anchors, anchorId);
            SyntaxNode node = FindNodeAtPosition(method, anchor.Binding.Position, anchorId);

            BasicBlock actionBlock = RequireReachableBlock(graph, node, anchorId);
            if (!dominators.TryGetValue(returnBlock.Ordinal, out HashSet<int>? returnDominators) ||
                !returnDominators.Contains(actionBlock.Ordinal))
            {
                throw new ExtractionException($"Normal-tail action '{anchorId}' does not dominate the admitted receipts return.");
            }
        }
    }

    private static void ValidateNoUnexpectedGuarding(string anchorId, SyntaxNode node) =>
        ValidateOnlyExpectedGuarding(node, $"admitted tail action '{anchorId}'",
            condition => IsExpectedConditional(anchorId, condition));

    private static void ValidateOnlyExpectedGuarding(
        SyntaxNode node,
        string description,
        Func<string, bool> expectedCondition)
    {
        int expectedGuardCount = 0;
        int enclosingTryCount = 0;
        foreach (SyntaxNode ancestor in node.Ancestors())
        {
            if (ancestor is IfStatementSyntax conditional)
            {
                string condition = Canonical(conditional.Condition);
                if (!expectedCondition(condition))
                {
                    throw new ExtractionException($"{description} has an unexpected conditional guard.");
                }

                if (++expectedGuardCount != 1)
                {
                    throw new ExtractionException($"{description} has a duplicated conditional guard.");
                }
            }
            else if (ancestor is ConditionalExpressionSyntax or SwitchStatementSyntax or SwitchExpressionSyntax or
                      SwitchExpressionArmSyntax or ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or
                      DoStatementSyntax or CatchClauseSyntax)
            {
                throw new ExtractionException($"{description} has an unexpected control-flow wrapper.");
            }
            else if (ancestor is TryStatementSyntax tryStatement &&
                (++enclosingTryCount != 1 || tryStatement.Finally is null))
            {
                throw new ExtractionException($"{description} has an unexpected try/finally wrapper.");
            }
        }
    }

    private static bool IsExpectedConditional(string anchorId, string condition) =>
        anchorId is "block.blob-gas-guard" or "block.blob-gas-calculation"
            ? condition == "spec.IsEip4844Enabled"
            : anchorId is "block.receipts-background-guard" or "block.sync-blooms" or "block.sync-receipts-root"
                ? condition == "ShouldCalculateReceiptsInBackground(receipts)"
                : anchorId is "block.main-thread-guard" or "block.account-changes"
                    ? condition == "BlockchainProcessor.IsMainProcessingThread"
                    : anchorId is "block.state-root-guard" or "block.state-root"
                        ? condition == "ShouldComputeStateRoot(header)"
                         : anchorId == "block.background-result-guard"
                            ? condition == TaskBackgroundResultPredicate
                            : anchorId == "block.background-finally-guard" && condition == TaskFinallyPredicate;

    private static SyntaxNode FindNodeAtPosition(MethodDeclarationSyntax method, int position, string description) =>
        method.DescendantNodesAndSelf()
            .Where(node => node.SpanStart == position)
            .OrderBy(node => node.Span.Length)
            .FirstOrDefault()
        ?? throw new ExtractionException($"The source node for tail action '{description}' is missing.");

    private static BasicBlock RequireReachableBlock(ControlFlowGraph graph, SyntaxNode node, string description)
    {
        int block = FindControlFlowBlock(graph, ControlFlowPosition(node));
        BasicBlock? result = graph.Blocks.SingleOrDefault(candidate => candidate.Ordinal == block);
        if (result is null || !result.IsReachable)
        {
            throw new ExtractionException($"Tail node '{description}' is not on exactly one reachable CFG block.");
        }

        return result;
    }

    private static Dictionary<int, HashSet<int>> ComputeDominators(ControlFlowGraph graph, IReadOnlyList<BasicBlock> reachable)
    {
        BasicBlock entry = reachable.SingleOrDefault(static block => block.Kind == BasicBlockKind.Entry)
            ?? reachable.OrderBy(static block => block.Ordinal).First();
        return ComputeDominatorsFrom(graph, entry);
    }

    private static void ProveSynchronousReceiptRoute(
        MethodDeclarationSyntax method,
        ControlFlowGraph graph,
        IfStatementSyntax backgroundGuard,
        InvocationExpressionSyntax synchronousBlooms,
        InvocationExpressionSyntax synchronousRoot,
        InvocationExpressionSyntax endTrace,
        IfStatementSyntax backgroundResultGuard,
        IfStatementSyntax finallyGuard)
    {
        BasicBlock guardBlock = RequireReachableBlock(graph, backgroundGuard.Condition, "background receipt guard route");
        AssignmentExpressionSyntax taskAssignment = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .SingleOrDefault(assignment => Canonical(assignment.Left) == TaskVariable &&
                assignment.Right is InvocationExpressionSyntax invocation &&
                Canonical(invocation.Expression) == "Task.Run")
            ?? throw new ExtractionException("The background task assignment is missing for false-edge CFG proof.");
        BasicBlock synchronousBloomBlock = RequireReachableBlock(graph, synchronousBlooms, "synchronous bloom route");
        BasicBlock synchronousRootBlock = RequireReachableBlock(graph, synchronousRoot, "synchronous root route");
        BasicBlock endTraceBlock = RequireReachableBlock(graph, endTrace, "EndBlockTrace route");
        BasicBlock falseSuccessor = SingleGuardSuccessor(
            graph, guardBlock, synchronousBloomBlock, "synchronous false receipt arm");
        BasicBlock trueSuccessor = SingleGuardSuccessor(
            graph, guardBlock, RequireReachableBlock(graph, taskAssignment, "background task route"),
            "background true receipt arm");
        if (falseSuccessor.Ordinal == trueSuccessor.Ordinal)
        {
            throw new ExtractionException("The background receipt guard true and false CFG arms are not distinct.");
        }

        Dictionary<int, HashSet<int>> falseRouteDominators = ComputeDominatorsFrom(graph, falseSuccessor);
        if (!falseRouteDominators.TryGetValue(endTraceBlock.Ordinal, out HashSet<int>? endDominators) ||
            !endDominators.Contains(synchronousBloomBlock.Ordinal) ||
            !endDominators.Contains(synchronousRootBlock.Ordinal))
        {
            throw new ExtractionException("The synchronous false edge does not dominate blooms, root, and EndBlockTrace in order.");
        }

        ProveGuardedStatementExcludedFromFalseRoute(
            graph, falseSuccessor, backgroundResultGuard, "background result assignment");
        ProveGuardedStatementExcludedFromFalseRoute(
            graph, falseSuccessor, finallyGuard, "finally task observation");
    }

    private static void ProveGuardedStatementExcludedFromFalseRoute(
        ControlFlowGraph graph,
        BasicBlock falseSuccessor,
        IfStatementSyntax guard,
        string description)
    {
        BasicBlock guardBlock = RequireReachableBlock(graph, guard.Condition, description + " guard");
        SyntaxNode bodyNode = guard.Statement.DescendantNodesAndSelf().FirstOrDefault(node =>
            node is InvocationExpressionSyntax invocation &&
            Canonical(invocation) == "bloomsAndReceiptsRootTask.GetAwaiter().GetResult()")
            ?? throw new ExtractionException($"The {description} body observation is missing.");
        BasicBlock bodyBlock = RequireReachableBlock(graph, bodyNode, description + " body");
        BasicBlock trueSuccessor = SingleGuardSuccessor(graph, guardBlock, bodyBlock, description + " true arm");
        if (ReachableWithoutEdge(graph, falseSuccessor, bodyBlock, guardBlock, trueSuccessor))
        {
            throw new ExtractionException($"The {description} is reachable from the synchronous false edge.");
        }
    }

    private static BasicBlock SingleGuardSuccessor(ControlFlowGraph graph, BasicBlock guardBlock, BasicBlock containedBlock, string description)
    {
        BasicBlock[] matches = Successors(graph, guardBlock)
            .Where(static block => block.IsReachable)
            .Where(block => block.Ordinal == containedBlock.Ordinal ||
                BlockContainsPosition(block, FirstOperationPosition(containedBlock)))
            .GroupBy(static block => block.Ordinal)
            .Select(static group => group.First())
            .ToArray();
        if (matches.Length != 1)
        {
            throw new ExtractionException($"The {description} does not identify exactly one CFG successor.");
        }

        return matches[0];
    }

    private static int FirstOperationPosition(BasicBlock block) =>
        block.Operations.Select(operation => operation.Syntax?.SpanStart ?? -1)
            .Concat(block.BranchValue?.Syntax is SyntaxNode syntax ? [syntax.SpanStart] : [])
            .Where(static position => position >= 0)
            .DefaultIfEmpty(-1)
            .Min();

    private static Dictionary<int, HashSet<int>> ComputeDominatorsFrom(ControlFlowGraph graph, BasicBlock entry)
        => ComputeEntryDominators(entry.Ordinal,
            graph.Blocks.Where(static block => block.IsReachable).Select(static block => block.Ordinal).ToArray(),
            TransferEdges(ReadTransfers(graph)));

    private static bool ReachableWithoutEdge(
        ControlFlowGraph graph,
        BasicBlock start,
        BasicBlock target,
        BasicBlock excludedSource,
        BasicBlock excludedDestination)
    {
        Queue<BasicBlock> pending = new();
        HashSet<int> visited = [];
        pending.Enqueue(start);
        while (pending.TryDequeue(out BasicBlock? block))
        {
            if (!visited.Add(block.Ordinal))
            {
                continue;
            }

            if (block.Ordinal == target.Ordinal)
            {
                return true;
            }

            foreach (BasicBlock successor in Successors(graph, block).Where(static successor => successor.IsReachable))
            {
                if (block.Ordinal == excludedSource.Ordinal && successor.Ordinal == excludedDestination.Ordinal)
                {
                    continue;
                }

                pending.Enqueue(successor);
            }
        }

        return false;
    }

    private static (AssignmentExpressionSyntax Assignment, ExpressionSyntax Value) FindSingleHelperPropertyWrite(
        MethodDeclarationSyntax method,
        SemanticModel model,
        string propertySymbol,
        string expectedCanonical,
        string description)
    {
        AssignmentExpressionSyntax[] assignments = method.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(assignment => AssignmentTargetsProperty(assignment, model, propertySymbol))
            .ToArray();
        if (assignments.Length != 1)
        {
            throw new ExtractionException(
                $"{description} requires exactly one typed property write; found {assignments.Length}.");
        }

        AssignmentExpressionSyntax assignment = assignments[0];
        if (Canonical(assignment) != expectedCanonical ||
            !IsDirectPropertyAssignment(assignment, model, propertySymbol))
        {
            throw new ExtractionException(
                $"{description} is not the exact direct '{expectedCanonical}' assignment.");
        }

        ValidateHelperPropertyValue(assignment, model, propertySymbol, expectedCanonical, description);
        return (assignment, assignment.Right);
    }

    private static void ValidateHelperPropertyValue(
        AssignmentExpressionSyntax assignment,
        SemanticModel model,
        string propertySymbol,
        string expectedCanonical,
        string description)
    {
        IOperation value = model.GetOperation(assignment.Right)
            ?? throw new ExtractionException($"{description} has no typed value operation.");
        string expectedValueSymbol;
        string expectedValueCanonical;
        if (propertySymbol == StateRootPropertySymbol)
        {
            expectedValueSymbol = StateRootValueSymbol;
            expectedValueCanonical = "_stateProvider.StateRoot";
            if (value is not IPropertyReferenceOperation)
            {
                throw new ExtractionException($"{description} does not read the typed state-root property.");
            }
        }
        else if (propertySymbol == AccountChangesPropertySymbol)
        {
            expectedValueSymbol = AccountChangesValueSymbol;
            expectedValueCanonical = "_stateProvider.GetAccountChanges()";
            if (value is not IInvocationOperation)
            {
                throw new ExtractionException($"{description} does not call the typed account-change helper.");
            }
        }
        else
        {
            throw new ExtractionException($"Unsupported helper property binding '{propertySymbol}'.");
        }

        string actualSymbol = value switch
        {
            IInvocationOperation invocation => invocation.TargetMethod.ToSourceIdentity(),
            IPropertyReferenceOperation property => property.Property.ToSourceIdentity(),
            _ => string.Empty,
        };
        if (Canonical(assignment.Right) != expectedValueCanonical || actualSymbol != expectedValueSymbol ||
            Canonical(assignment) != expectedCanonical)
        {
            throw new ExtractionException(
                $"{description} has a redirected typed value; expected '{expectedCanonical}'.");
        }
    }

    private static void ValidateHeaderAccountHashWrites(
        SemanticModel model,
        MethodDeclarationSyntax processBlock,
        MethodDeclarationSyntax computeStateRoot,
        MethodDeclarationSyntax setAccountChanges,
        AssignmentExpressionSyntax stateRootWrite,
        AssignmentExpressionSyntax accountChangesWrite)
    {
        AssignmentExpressionSyntax hashWrite = processBlock.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .SingleOrDefault(assignment =>
                AssignmentTargetsProperty(assignment, model, HashPropertySymbol) &&
                Canonical(assignment) == "header.Hash=header.CalculateHash()")
            ?? throw new ExtractionException("The canonical header.Hash write is missing from ProcessBlock.");

        ValidatePropertyWriteAudit(model, processBlock, StateRootPropertySymbol, null, "ProcessBlock header.StateRoot");
        ValidatePropertyWriteAudit(model, processBlock, AccountChangesPropertySymbol, null, "ProcessBlock block.AccountChanges");
        ValidatePropertyWriteAudit(model, processBlock, HashPropertySymbol, hashWrite, "ProcessBlock header.Hash");

        ValidatePropertyWriteAudit(model, computeStateRoot, StateRootPropertySymbol, stateRootWrite,
            "ComputeStateRoot header.StateRoot");
        ValidatePropertyWriteAudit(model, computeStateRoot, AccountChangesPropertySymbol, null,
            "ComputeStateRoot block.AccountChanges");
        ValidatePropertyWriteAudit(model, computeStateRoot, HashPropertySymbol, null,
            "ComputeStateRoot header.Hash");

        ValidatePropertyWriteAudit(model, setAccountChanges, StateRootPropertySymbol, null,
            "SetAccountChanges header.StateRoot");
        ValidatePropertyWriteAudit(model, setAccountChanges, AccountChangesPropertySymbol, accountChangesWrite,
            "SetAccountChanges block.AccountChanges");
        ValidatePropertyWriteAudit(model, setAccountChanges, HashPropertySymbol, null,
            "SetAccountChanges header.Hash");
    }

    private static void ValidatePropertyWriteAudit(
        SemanticModel model,
        MethodDeclarationSyntax method,
        string propertySymbol,
        AssignmentExpressionSyntax? allowedAssignment,
        string description)
    {
        IOperation operation = model.GetOperation(method)
            ?? throw new ExtractionException($"{description} has no typed method operation.");
        ControlFlowGraph graph = TryCreateGraph(model, method)
            ?? throw new ExtractionException($"{description} has no owning control-flow graph.");
        IPropertyReferenceOperation[] writes = DescendantOperations(operation)
            .OfType<IPropertyReferenceOperation>()
            .Where(property => property.Property.ToSourceIdentity() == propertySymbol)
            .Where(IsPropertyWrite)
            .ToArray();
        foreach (IPropertyReferenceOperation write in writes)
        {
            if (write.Syntax is null)
            {
                throw new ExtractionException($"{description} contains a typed property write without source syntax.");
            }

            int block = FindControlFlowBlock(graph, write.Syntax.SpanStart);
            if (block < 0 || graph.Blocks.SingleOrDefault(candidate => candidate.Ordinal == block) is not { IsReachable: true })
            {
                throw new ExtractionException($"{description} contains a property write outside one reachable CFG block.");
            }
        }
        if (allowedAssignment is null)
        {
            if (writes.Length != 0)
            {
                throw new ExtractionException(
                    $"{description} contains an unwhitelisted reachable typed write (direct, deconstruction, compound, or ref-like).");
            }

            return;
        }

        if (writes.Length != 1 || !IsDirectPropertyAssignment(allowedAssignment, model, propertySymbol) ||
            writes[0].Syntax is null || writes[0].Syntax.SpanStart != allowedAssignment.Left.SpanStart ||
            writes[0].Syntax.Span.End != allowedAssignment.Left.Span.End)
        {
            throw new ExtractionException(
                $"{description} does not have exactly one bound direct property write; competing or redirected writes are rejected.");
        }
    }

    private static bool IsPropertyWrite(IPropertyReferenceOperation property)
    {
        SyntaxNode? syntax = property.Syntax;
        if (syntax is null)
        {
            return true;
        }

        if (syntax.AncestorsAndSelf().OfType<AssignmentExpressionSyntax>()
            .Any(assignment => IsWithin(assignment.Left, syntax)))
        {
            return true;
        }

        if (syntax.AncestorsAndSelf().OfType<ArgumentSyntax>()
            .Any(argument => argument.RefKindKeyword.RawKind != 0) ||
            syntax.AncestorsAndSelf().OfType<RefExpressionSyntax>().Any())
        {
            return true;
        }

        return syntax.AncestorsAndSelf().OfType<PrefixUnaryExpressionSyntax>()
            .Any(unary => unary.IsKind(SyntaxKind.PreIncrementExpression) ||
                unary.IsKind(SyntaxKind.PreDecrementExpression) ||
                unary.IsKind(SyntaxKind.PostIncrementExpression) ||
                unary.IsKind(SyntaxKind.PostDecrementExpression));
    }

    private static SourceEntryAdapterIdentity BuildSourceEntryAdapter(
        SourceFile source,
        MethodDeclarationSyntax method,
        MethodDeclarationSyntax computeStateRootMethod,
        MethodDeclarationSyntax setAccountChangesMethod,
        SemanticModel model,
        Compilation compilation,
        IReadOnlyList<MemberIdentity> members,
        IReadOnlyList<AnchorIdentity> anchors)
    {
        InvocationExpressionSyntax executor = SingleInvocation(method,
            SourceEntrySelectedExecutor, "standard transaction executor");
        InvocationExpressionSyntax transactionsExecuted = SingleInvocation(method,
            "TransactionsExecuted?.Invoke()", "TransactionsExecuted signal");
        if (model.GetOperation(transactionsExecuted) is not IInvocationOperation transactionsExecutedOperation ||
            !IsAuditedTransactionsExecutedHook(transactionsExecuted, transactionsExecutedOperation, model))
        {
            throw new ExtractionException("The source-entry adapter requires the exact typed TransactionsExecuted event invocation.");
        }
        ControlFlowGraph processGraph = TryCreateGraph(model, method)
            ?? throw new ExtractionException("ProcessBlock has no CFG for source-entry normal-return boundary bindings.");
        InvocationExpressionSyntax postTransactionCommit =
            ExecutableDescendantNodes<InvocationExpressionSyntax>(method)
                .Where(static invocation => Canonical(invocation) == "CommitState(spec)")
                .OrderBy(static invocation => invocation.SpanStart)
                .Skip(1)
                .FirstOrDefault()
            ?? throw new ExtractionException("The source-entry adapter cannot locate the post-transaction CommitState(spec).");
        ValidateTransactionsExecutedPath(method, processGraph, transactionsExecuted, postTransactionCommit);
        VariableDeclaratorSyntax receiptsDeclarator = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .SingleOrDefault(variable => variable.Identifier.ValueText == ReceiptsLocal &&
                variable.Initializer?.Value is InvocationExpressionSyntax initializer &&
                initializer.SpanStart == executor.SpanStart &&
                Canonical(initializer) == SourceEntrySelectedExecutor)
            ?? throw new ExtractionException("The standard executor result must initialize the exact receipts local.");
        ILocalSymbol receiptsSymbol = model.GetDeclaredSymbol(receiptsDeclarator) as ILocalSymbol
            ?? throw new ExtractionException("The receipts result has no exact ILocalSymbol.");
        if (receiptsSymbol.Kind != SymbolKind.Local || receiptsSymbol.Name != ReceiptsLocal ||
            receiptsSymbol.RefKind != RefKind.None ||
            receiptsSymbol.ToSourceIdentity() != ReceiptsLocalSymbol)
        {
            throw new ExtractionException("The standard executor result did not bind to the expected receipts ILocalSymbol.");
        }
        AnchorIdentity background = Anchor(anchors, "block.receipts-background-guard");
        AnchorIdentity mainThread = Anchor(anchors, "block.main-thread-guard");
        AnchorIdentity stateRoot = Anchor(anchors, "block.state-root-guard");
        AnchorIdentity spec = Anchor(anchors, "block.blob-gas-guard");
        AnchorIdentity bal = Anchor(anchors, "block.bal-finalization");
        TypedBinding executorBinding = BindNode(source, method, executor, model, compilation, null,
            model.GetOperation(executor) ?? throw new ExtractionException("No IOperation for the standard transaction executor."),
            model.GetDeclaredSymbol(method));
        TypedBinding transactionsExecutedBinding = BindNode(source, method, transactionsExecuted, model, compilation,
            processGraph, transactionsExecutedOperation, model.GetDeclaredSymbol(method));
        TypedBinding postTransactionCommitBinding = BindNode(source, method, postTransactionCommit, model, compilation,
            processGraph, model.GetOperation(postTransactionCommit) ??
                throw new ExtractionException("No IOperation for the post-transaction CommitState(spec)."),
            model.GetDeclaredSymbol(method));
        TypedBinding receiptsBinding = BindNode(source, method, receiptsDeclarator, model, compilation, null,
            model.GetOperation(receiptsDeclarator) ?? throw new ExtractionException("No IOperation for the receipts result."),
            receiptsSymbol);
        (int receiptsReferences, int receiptsWrites) = ValidateReceiptsLocalAudit(
            model, method, receiptsSymbol, receiptsDeclarator);
        (AssignmentExpressionSyntax stateRootWrite, ExpressionSyntax stateRootValue) =
            FindSingleHelperPropertyWrite(computeStateRootMethod, model, StateRootPropertySymbol,
                "header.StateRoot=_stateProvider.StateRoot", "ComputeStateRoot header.StateRoot write");
        (AssignmentExpressionSyntax accountChangesWrite, ExpressionSyntax accountChangesValue) =
            FindSingleHelperPropertyWrite(setAccountChangesMethod, model, AccountChangesPropertySymbol,
                "block.AccountChanges=_stateProvider.GetAccountChanges()", "SetAccountChanges block.AccountChanges write");
        ValidateHeaderAccountHashWrites(
            model, method, computeStateRootMethod, setAccountChangesMethod,
            stateRootWrite, accountChangesWrite);

        ControlFlowGraph computeStateRootGraph = TryCreateGraph(model, computeStateRootMethod)
            ?? throw new ExtractionException("ComputeStateRoot has no Roslyn control-flow graph for typed helper writes.");
        ControlFlowGraph setAccountChangesGraph = TryCreateGraph(model, setAccountChangesMethod)
            ?? throw new ExtractionException("SetAccountChanges has no Roslyn control-flow graph for typed helper writes.");
        TypedBinding stateRootWriteBinding = BindNode(source, computeStateRootMethod, stateRootWrite, model, compilation,
            computeStateRootGraph, model.GetOperation(stateRootWrite) ??
                throw new ExtractionException("No IOperation for ComputeStateRoot header.StateRoot write."),
            model.GetSymbolInfo(stateRootWrite.Left).Symbol);
        TypedBinding stateRootValueBinding = BindNode(source, computeStateRootMethod, stateRootValue, model, compilation,
            computeStateRootGraph, model.GetOperation(stateRootValue) ??
                throw new ExtractionException("No IOperation for _stateProvider.StateRoot value."),
            model.GetDeclaredSymbol(computeStateRootMethod));
        TypedBinding accountChangesWriteBinding = BindNode(source, setAccountChangesMethod, accountChangesWrite, model, compilation,
            setAccountChangesGraph, model.GetOperation(accountChangesWrite) ??
                throw new ExtractionException("No IOperation for SetAccountChanges block.AccountChanges write."),
            model.GetSymbolInfo(accountChangesWrite.Left).Symbol);
        TypedBinding accountChangesValueBinding = BindNode(source, setAccountChangesMethod, accountChangesValue, model, compilation,
            setAccountChangesGraph, model.GetOperation(accountChangesValue) ??
                throw new ExtractionException("No IOperation for _stateProvider.GetAccountChanges() value."),
            model.GetDeclaredSymbol(setAccountChangesMethod));

        PreservedValueIdentity[] preservedValues = BuildPreservedValues(method, model);
        VariableDeclaratorSyntax headerDeclaration = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(static variable => variable.Identifier.ValueText == "header");
        ExpressionSyntax headerValue = headerDeclaration.Initializer!.Value;
        TypedBinding headerBinding = BindNode(source, method, headerDeclaration, model, compilation, null,
            model.GetOperation(headerDeclaration) ?? throw new ExtractionException("Header declaration operation is missing."),
            model.GetDeclaredSymbol(headerDeclaration));
        TypedBinding headerValueBinding = BindNode(source, method, headerValue, model, compilation, null,
            model.GetOperation(headerValue) ?? throw new ExtractionException("Header source value operation is missing."),
            model.GetSymbolInfo(headerValue).Symbol);

        return new(
            SourceEntryAdapterId,
            SourceEntryAdapterClaim,
            "block.processBlock",
            SourceEntryExactBaseReceiver,
            SourceEntrySelectedExecutor,
            SourceEntryBalPremise,
            SourceEntryBackgroundPremise,
            SourceEntryMainThreadPremise,
            SourceEntryStateRootPremise,
            SourceEntrySpecPremise,
            members.Single(member => member.Id == "block.processBlock").Binding,
            executorBinding,
            receiptsReferences,
            receiptsWrites,
            receiptsBinding,
            bal.Binding,
            background.Binding,
            mainThread.Binding,
            stateRoot.Binding,
            spec.Binding,
            stateRootWriteBinding,
            stateRootValueBinding,
            accountChangesWriteBinding,
            accountChangesValueBinding,
            SourceEntryTransactionsExecutedNormalReturnPremise,
            SourceEntryPostTransactionCommitNormalReturnPremise,
            TransactionsExecutedEventSymbol,
            transactionsExecutedBinding,
            postTransactionCommitBinding,
            BindTransactionsExecuted(source, method, model, compilation),
            headerBinding,
            headerValueBinding,
            preservedValues);
    }

    private static (int ReferenceCount, int WriteCount) ValidateReceiptsLocalAudit(
        SemanticModel model,
        MethodDeclarationSyntax method,
        ILocalSymbol receiptsSymbol,
        VariableDeclaratorSyntax receiptsDeclarator)
    {
        IfStatementSyntax backgroundGuard = FindIf(method,
            condition => Canonical(condition) == "ShouldCalculateReceiptsInBackground(receipts)",
            "receipt local background guard");
        InvocationExpressionSyntax synchronousBlooms = SingleInvocation(backgroundGuard.Else!.Statement,
            "CalculateBlooms(receipts)", "synchronous receipts bloom call");
        InvocationExpressionSyntax synchronousRoot = SingleInvocation(backgroundGuard.Else.Statement,
            "CalculateReceiptsRoot(receipts,spec,block)", "synchronous receipts root call");
        InvocationExpressionSyntax requests = SingleInvocation(method,
            "_systemContractHandler.ProcessExecutionRequests(block,_stateProvider,receipts,spec)",
            "receipts execution-request call");
        ReturnStatementSyntax returnReceipts = FindOuterReceiptsReturn(method);
        InvocationExpressionSyntax[] taskRuns = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => Canonical(invocation.Expression) == "Task.Run").ToArray();
        if (taskRuns.Length != 1 || taskRuns[0].ArgumentList.Arguments.Count != 1 ||
            taskRuns[0].ArgumentList.Arguments[0].Expression is not AnonymousFunctionExpressionSyntax taskLambda)
        {
            throw new ExtractionException("The receipts local audit requires one exact Task.Run lambda.");
        }

        InvocationExpressionSyntax[] bloomCalls = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => Canonical(invocation) == "CalculateBlooms(receipts)").ToArray();
        InvocationExpressionSyntax[] rootCalls = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => Canonical(invocation) == "CalculateReceiptsRoot(receipts,spec,block)").ToArray();
        InvocationExpressionSyntax[] accumulateCalls = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => Canonical(invocation) == "AccumulateBlockBloom(receipts)").ToArray();
        if (bloomCalls.Length != 2 || rootCalls.Length != 2 || accumulateCalls.Length != 1 ||
            !ReferenceEquals(bloomCalls.SingleOrDefault(call => ReferenceEquals(call, synchronousBlooms)), synchronousBlooms) ||
            !ReferenceEquals(rootCalls.SingleOrDefault(call => ReferenceEquals(call, synchronousRoot)), synchronousRoot) ||
            !IsWithin(taskLambda, bloomCalls.Single(call => !ReferenceEquals(call, synchronousBlooms))) ||
            !IsWithin(taskLambda, rootCalls.Single(call => !ReferenceEquals(call, synchronousRoot))) ||
            !IsWithin(taskLambda, accumulateCalls[0]))
        {
            throw new ExtractionException("The receipts local predicate/root call set is not the exact selected standard path.");
        }

        if (!IsExactInvocation(backgroundGuard.Condition as InvocationExpressionSyntax, model, BackgroundPredicateSymbol) ||
            !IsExactInvocation(synchronousBlooms, model, BloomsSymbol) ||
            !IsExactInvocation(synchronousRoot, model, ReceiptsRootValueSymbol) ||
            !IsExactInvocation(requests, model, RequestsSymbol) ||
            bloomCalls.Any(call => !IsExactInvocation(call, model, BloomsSymbol)) ||
            rootCalls.Any(call => !IsExactInvocation(call, model, ReceiptsRootValueSymbol)) ||
            !IsExactInvocation(accumulateCalls[0], model, AccumulateBlockBloomSymbol))
        {
            throw new ExtractionException("The receipts local audit found a redirected or unresolved call symbol.");
        }

        List<SyntaxNode> allowedContexts =
        [
            backgroundGuard.Condition,
            synchronousBlooms,
            synchronousRoot,
            bloomCalls[0],
            bloomCalls[1],
            rootCalls[0],
            rootCalls[1],
            accumulateCalls[0],
            requests,
            returnReceipts.Expression ?? throw new ExtractionException("The receipts return has no expression."),
        ];
        IOperation operation = model.GetOperation(method)
            ?? throw new ExtractionException("ProcessBlock has no IOperation for receipts local audit.");
        IVariableDeclaratorOperation[] declarations = DescendantOperations(operation)
            .OfType<IVariableDeclaratorOperation>()
            .Where(declaration => SymbolEqualityComparer.Default.Equals(declaration.Symbol, receiptsSymbol))
            .ToArray();
        if (declarations.Length != 1 || declarations[0].Syntax.SpanStart != receiptsDeclarator.SpanStart)
        {
            throw new ExtractionException("The receipts local definition is missing or duplicated.");
        }

        List<ILocalReferenceOperation> references = DescendantOperations(operation)
            .OfType<ILocalReferenceOperation>()
            .Where(reference => SymbolEqualityComparer.Default.Equals(reference.Local, receiptsSymbol))
            .ToList();
        if (references.Count != ReceiptsReferenceCount)
        {
            throw new ExtractionException($"The receipts local reference count changed; expected {ReceiptsReferenceCount}, found {references.Count}.");
        }

        foreach (SyntaxNode context in allowedContexts)
        {
            if (CountLocalReferencesWithin(model, context, receiptsSymbol) != 1)
            {
                throw new ExtractionException($"The receipts local is missing from or duplicated in '{Canonical(context)}'.");
            }
        }

        foreach (ILocalReferenceOperation reference in references)
        {
            SyntaxNode syntax = reference.Syntax;
            if (syntax is null || !allowedContexts.Any(context => IsWithin(context, syntax)))
            {
                throw new ExtractionException("The receipts local has an unwhitelisted use.");
            }

            ArgumentSyntax? argument = syntax.AncestorsAndSelf().OfType<ArgumentSyntax>().FirstOrDefault();
            if (argument is not null && argument.RefKindKeyword.RawKind != 0)
            {
                throw new ExtractionException("The receipts local is passed through a ref, in, or out argument.");
            }

            if (syntax.AncestorsAndSelf().OfType<RefExpressionSyntax>().Any())
            {
                throw new ExtractionException("The receipts local is used through a ref alias.");
            }
        }

        int writes = 0;
        foreach (IOperation candidate in DescendantOperations(operation))
        {
            if (candidate is IArgumentOperation argumentOperation && argumentOperation.Parameter is { RefKind: not RefKind.None } &&
                UsesLocal(argumentOperation.Value, receiptsSymbol))
            {
                throw new ExtractionException("The receipts local is used by a ref/out operation.");
            }

            if (candidate is ICompoundAssignmentOperation compound && UsesLocal(compound.Target, receiptsSymbol) ||
                candidate is IIncrementOrDecrementOperation increment && UsesLocal(increment.Target, receiptsSymbol))
            {
                throw new ExtractionException("The receipts local has a compound or increment write.");
            }

            if (candidate is ISimpleAssignmentOperation simple && UsesLocal(simple.Target, receiptsSymbol))
            {
                throw new ExtractionException("The receipts local has an unexpected assignment write.");
            }
        }

        writes = declarations.Length;
        if (writes != ReceiptsWriteCount)
        {
            throw new ExtractionException("The receipts local does not have exactly one executor definition.");
        }

        return (references.Count, writes);
    }

    private static int CountLocalReferencesWithin(SemanticModel model, SyntaxNode context, ILocalSymbol local)
    {
        IOperation? operation = model.GetOperation(context);
        return operation is null
            ? 0
            : DescendantOperations(operation).OfType<ILocalReferenceOperation>()
                .Count(reference => SymbolEqualityComparer.Default.Equals(reference.Local, local));
    }

    private static TaskFlowIdentity BuildTaskFlow(
        SourceFile source,
        MethodDeclarationSyntax method,
        SemanticModel model,
        Compilation compilation,
        IReadOnlyList<AnchorIdentity> anchors)
    {
        ControlFlowGraph graph = TryCreateGraph(model, method)
            ?? throw new ExtractionException("ProcessBlock has no Roslyn control-flow graph for task-flow validation.");
        IfStatementSyntax backgroundGuard = FindIf(method,
            condition => Canonical(condition) == "ShouldCalculateReceiptsInBackground(receipts)", "background receipt guard");
        StatementSyntax synchronousArm = backgroundGuard.Else?.Statement
            ?? throw new ExtractionException("The false receipt-background arm is missing.");
        VariableDeclaratorSyntax taskDeclarator = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .SingleOrDefault(variable => variable.Identifier.ValueText == TaskVariable &&
                variable.Initializer is not null && Canonical(variable.Initializer.Value) == "null")
            ?? throw new ExtractionException("The receipt task null initializer is missing or ambiguous.");
        if (taskDeclarator.Ancestors().OfType<LocalDeclarationStatementSyntax>()
                .Any(statement => statement.Declaration.Type is RefTypeSyntax))
        {
            throw new ExtractionException("The receipt task must not be a ref local.");
        }

        ILocalSymbol taskSymbol = model.GetDeclaredSymbol(taskDeclarator) as ILocalSymbol
            ?? throw new ExtractionException("The receipt task declaration has no exact ILocalSymbol.");
        if (taskSymbol.Kind != SymbolKind.Local || taskSymbol.Name != TaskVariable || taskSymbol.RefKind != RefKind.None ||
            taskSymbol.ToSourceIdentity() != TaskLocalSymbol)
        {
            throw new ExtractionException("The receipt task declaration did not bind to the expected ILocalSymbol.");
        }

        AssignmentExpressionSyntax[] assignments = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(assignment => AssignmentTargetsLocal(assignment, model, taskSymbol)).ToArray();
        AssignmentExpressionSyntax[] backgroundAssignments = assignments
            .Where(assignment => IsWithin(backgroundGuard.Statement, assignment)).ToArray();
        AssignmentExpressionSyntax[] synchronousAssignments = assignments
            .Where(assignment => IsWithin(synchronousArm, assignment)).ToArray();
        if (assignments.Length != 1 || backgroundAssignments.Length != 1 || synchronousAssignments.Length != 0)
        {
            throw new ExtractionException("The receipt task reaching definitions changed: the synchronous arm must preserve null.");
        }

        ValidateOnlyExpectedGuarding(backgroundAssignments[0], "background task assignment",
            condition => condition == "ShouldCalculateReceiptsInBackground(receipts)");

        if (backgroundAssignments[0].Right is not InvocationExpressionSyntax taskRun ||
            Canonical(taskRun.Expression) != "Task.Run" ||
            taskRun.ArgumentList.Arguments.Count != 1 ||
            taskRun.ArgumentList.Arguments[0].RefKindKeyword.RawKind != 0 ||
            taskRun.ArgumentList.Arguments[0].Expression is not AnonymousFunctionExpressionSyntax ||
            model.GetOperation(taskRun) is not IInvocationOperation taskRunOperation ||
            taskRunOperation.TargetMethod.Name != "Run" ||
            taskRunOperation.TargetMethod.ContainingType?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) !=
                "System.Threading.Tasks.Task" || HasErrorSymbol(taskRunOperation.TargetMethod))
        {
            throw new ExtractionException("The background arm must assign the receipt task from the exact Task.Run invocation.");
        }

        ValidateTaskLocalAudit(model, method, taskSymbol, taskDeclarator, backgroundAssignments[0], synchronousArm);

        TypedBinding initializer = Anchor(anchors, "block.background-task-null").Binding;
        TypedBinding endTrace = Anchor(anchors, "block.end-block-trace").Binding;
        TypedBinding backgroundResult = Anchor(anchors, "block.background-result-guard").Binding;
        TypedBinding finallyObservation = Anchor(anchors, "block.background-finally-guard").Binding;
        bool nullPreserved = initializer.CanonicalSyntax == TaskInitializer &&
            ProveNullReachesFalseArm(graph, method, backgroundGuard, backgroundAssignments[0], synchronousArm,
                initializer, endTrace, backgroundResult, finallyObservation);
        AnchorIdentity background = Anchor(anchors, "block.receipts-background-guard");
        bool backgroundResultExcluded = nullPreserved &&
            backgroundResult.CanonicalSyntax == TaskBackgroundResultPredicate &&
            initializer.Position < background.Binding.Position && background.Binding.Position < endTrace.Position &&
            endTrace.Position < backgroundResult.Position;
        bool finallyObservationExcluded = backgroundResultExcluded &&
            finallyObservation.CanonicalSyntax == TaskFinallyPredicate &&
            backgroundResult.Position < finallyObservation.Position;
        if (!nullPreserved || !backgroundResultExcluded || !finallyObservationExcluded)
        {
            throw new ExtractionException("The false receipt-background arm does not preserve the null-task reaching definition.");
        }

        return new(
            TaskVariable,
            TaskLocalSymbol,
            TaskInitializer,
            TaskSynchronousArm,
            TaskEndTracePredicate,
            TaskBackgroundResultPredicate,
            TaskFinallyPredicate,
            backgroundAssignments.Length,
            synchronousAssignments.Length,
            nullPreserved,
            backgroundResultExcluded,
            finallyObservationExcluded,
            Anchor(anchors, "block.background-task-null").Binding,
            Anchor(anchors, "block.end-block-trace").Binding,
            Anchor(anchors, "block.background-result-guard").Binding,
            Anchor(anchors, "block.background-finally-guard").Binding);
    }

    private static void ValidateTaskLocalAudit(
        SemanticModel model,
        MethodDeclarationSyntax method,
        ILocalSymbol taskSymbol,
        VariableDeclaratorSyntax taskDeclarator,
        AssignmentExpressionSyntax backgroundAssignment,
        StatementSyntax synchronousArm)
    {
        SyntaxNode endTrace = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .SingleOrDefault(invocation => Canonical(invocation) ==
                "ReceiptsTracer.EndBlockTrace(accumulateBlockBloom:bloomsAndReceiptsRootTaskisnull)")
            ?? throw new ExtractionException("The EndBlockTrace task read is missing or ambiguous.");
        IfStatementSyntax backgroundResultGuard = FindIf(method,
            condition => Canonical(condition) == TaskBackgroundResultPredicate,
            "background-result guard for task audit");
        IfStatementSyntax finallyGuard = FindIf(method,
            condition => Canonical(condition) == TaskFinallyPredicate, "finally guard for task audit");
        SyntaxNode backgroundResult = backgroundResultGuard.Condition;
        SyntaxNode finallyObservation = finallyGuard.Condition;
        InvocationExpressionSyntax[] resultObservations = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => Canonical(invocation) == "bloomsAndReceiptsRootTask.GetAwaiter().GetResult()")
            .ToArray();
        if (resultObservations.Length != 2)
        {
            throw new ExtractionException("The receipt task result observations are missing or duplicated.");
        }

        InvocationExpressionSyntax backgroundResultObservation = resultObservations.SingleOrDefault(
            invocation => IsWithin(backgroundResultGuard.Statement, invocation))
            ?? throw new ExtractionException("The background result observation is missing.");
        InvocationExpressionSyntax finallyObservationCall = resultObservations.SingleOrDefault(
            invocation => IsWithin(finallyGuard.Statement, invocation))
            ?? throw new ExtractionException("The finally task observation is missing.");

        IOperation operation = model.GetOperation(method)
            ?? throw new ExtractionException("ProcessBlock has no IOperation for task-flow audit.");
        IVariableDeclaratorOperation[] declarations = DescendantOperations(operation)
            .OfType<IVariableDeclaratorOperation>()
            .Where(declaration => SymbolEqualityComparer.Default.Equals(declaration.Symbol, taskSymbol))
            .ToArray();
        if (declarations.Length != 1 || declarations[0].Syntax.SpanStart != taskDeclarator.SpanStart)
        {
            throw new ExtractionException("The receipt task ILocalSymbol declaration is missing or duplicated.");
        }

        List<ILocalReferenceOperation> references = DescendantOperations(operation)
            .OfType<ILocalReferenceOperation>()
            .Where(reference => SymbolEqualityComparer.Default.Equals(reference.Local, taskSymbol))
            .ToList();
        if (references.Count == 0)
        {
            throw new ExtractionException("The receipt task has no ILocalReferenceOperation uses.");
        }

        int allowedReads = 0;
        int allowedWrites = 0;
        foreach (ILocalReferenceOperation reference in references)
        {
            SyntaxNode syntax = reference.Syntax;

            if (syntax.AncestorsAndSelf().Any(static node => node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            {
                throw new ExtractionException("The receipt task is captured by a lambda or local function.");
            }

            ArgumentSyntax? argument = syntax.AncestorsAndSelf().OfType<ArgumentSyntax>().FirstOrDefault();
            if (argument is not null && argument.RefKindKeyword.RawKind != 0)
            {
                throw new ExtractionException("The receipt task is passed through a ref, in, or out argument.");
            }

            AssignmentExpressionSyntax? assignment = syntax.AncestorsAndSelf()
                .OfType<AssignmentExpressionSyntax>()
                .FirstOrDefault(candidate => candidate.Left.Span.Contains(syntax.Span));
            if (assignment is not null)
            {
                if (!ReferenceEquals(assignment, backgroundAssignment))
                {
                    throw new ExtractionException("The receipt task has a non-whitelisted assignment target.");
                }

                allowedWrites++;
                continue;
            }

            if (syntax.AncestorsAndSelf().Any(static node => node is RefExpressionSyntax))
            {
                throw new ExtractionException("The receipt task is used through a ref alias.");
            }

            if (IsWithin(endTrace, syntax) || IsWithin(backgroundResult, syntax) ||
                IsWithin(backgroundResultObservation, syntax) || IsWithin(finallyObservation, syntax) ||
                IsWithin(finallyObservationCall, syntax))
            {
                allowedReads++;
                continue;
            }

            throw new ExtractionException($"The receipt task has an unwhitelisted local use at position {syntax.SpanStart}.");
        }

        if (allowedWrites != 1 || allowedReads != 5)
        {
            throw new ExtractionException("The receipt task ILocalSymbol reference/write audit is incomplete.");
        }

        foreach (IOperation candidate in DescendantOperations(operation))
        {
            if (candidate is IArgumentOperation argumentOperation && argumentOperation.Parameter is { RefKind: not RefKind.None } &&
                UsesLocal(argumentOperation.Value, taskSymbol))
            {
                throw new ExtractionException("The receipt task is used by a ref/out operation.");
            }

            if (candidate is ICompoundAssignmentOperation compound && UsesLocal(compound.Target, taskSymbol) ||
                candidate is IIncrementOrDecrementOperation increment && UsesLocal(increment.Target, taskSymbol))
            {
                throw new ExtractionException("The receipt task has a compound or increment write.");
            }

            if (candidate is ISimpleAssignmentOperation simple && UsesLocal(simple.Target, taskSymbol) &&
                (simple.IsRef || simple.Syntax.SpanStart != backgroundAssignment.SpanStart))
            {
                throw new ExtractionException("The receipt task has a non-whitelisted IOperation write.");
            }
        }

        foreach (AnonymousFunctionExpressionSyntax lambda in method.DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>())
        {
            if (lambda.DescendantNodes().OfType<IdentifierNameSyntax>()
                    .Any(identifier => IsLocalReference(identifier, model, taskSymbol)))
            {
                throw new ExtractionException("The receipt task is captured by an anonymous function.");
            }
        }

        foreach (LocalFunctionStatementSyntax localFunction in method.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
        {
            if (localFunction.DescendantNodes().OfType<IdentifierNameSyntax>()
                    .Any(identifier => IsLocalReference(identifier, model, taskSymbol)))
            {
                throw new ExtractionException("The receipt task is captured by a local function.");
            }
        }

        if (synchronousArm.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Any(assignment => AssignmentTargetsLocal(assignment, model, taskSymbol)))
        {
            throw new ExtractionException("The synchronous receipt arm writes the background task local.");
        }
    }

    private static bool ProveNullReachesFalseArm(
        ControlFlowGraph graph,
        MethodDeclarationSyntax method,
        IfStatementSyntax backgroundGuard,
        AssignmentExpressionSyntax backgroundAssignment,
        StatementSyntax synchronousArm,
        TypedBinding initializer,
        TypedBinding endTrace,
        TypedBinding backgroundResult,
        TypedBinding finallyObservation)
    {
        BasicBlock initializerBlock = RequireReachableBlock(graph,
            FindNodeAtPosition(method, initializer.Position, "task initializer"), "task initializer");
        BasicBlock guardBlock = RequireReachableBlock(graph, backgroundGuard.Condition, "background receipt guard");
        InvocationExpressionSyntax synchronousBloom = synchronousArm.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(invocation => Canonical(invocation) == "CalculateBlooms(receipts)")
            ?? throw new ExtractionException("The synchronous bloom operation is missing for task-flow proof.");
        BasicBlock synchronousBlock = RequireReachableBlock(graph, synchronousBloom,
            "synchronous receipt arm");
        BasicBlock backgroundAssignmentBlock = RequireReachableBlock(graph, backgroundAssignment, "background task assignment");

        BasicBlock[] guardSuccessors = Successors(graph, guardBlock).Where(static block => block.IsReachable)
            .GroupBy(static block => block.Ordinal).Select(static group => group.First()).ToArray();
        int[] synchronousSuccessors = guardSuccessors
            .Where(successor => BlockContainsPosition(successor, synchronousBloom.SpanStart))
            .Select(static successor => successor.Ordinal).ToArray();
        int[] backgroundSuccessors = guardSuccessors
            .Where(successor => BlockContainsPosition(successor, backgroundAssignment.SpanStart))
            .Select(static successor => successor.Ordinal).ToArray();
        if (synchronousSuccessors.Length != 1 || backgroundSuccessors.Length != 1 ||
            synchronousSuccessors[0] == backgroundSuccessors[0])
        {
            throw new ExtractionException("The background receipt guard does not expose distinct false and true CFG arms.");
        }

        BasicBlock[] reachable = graph.Blocks.Where(static block => block.IsReachable).ToArray();
        Dictionary<int, HashSet<int>> dominators = ComputeDominators(graph, reachable);
        if (!dominators.TryGetValue(synchronousBlock.Ordinal, out HashSet<int>? synchronousDominators) ||
            !synchronousDominators.Contains(initializerBlock.Ordinal) ||
            !ReachingDefinitionContainsOnlyNull(graph, initializerBlock, backgroundAssignmentBlock, synchronousBlock))
        {
            throw new ExtractionException("The null task initializer does not reach the synchronous arm by CFG dominance.");
        }

        int[] targets =
        [
            RequireReachableBlock(graph, FindNodeAtPosition(method, endTrace.Position, "EndBlockTrace task read"),
                "EndBlockTrace task read").Ordinal,
            RequireReachableBlock(graph, FindNodeAtPosition(method, backgroundResult.Position, "background result task read"),
                "background result task read").Ordinal,
            RequireReachableBlock(graph, FindNodeAtPosition(method, finallyObservation.Position, "finally task read"),
                "finally task read").Ordinal,
        ];
        HashSet<(int Block, bool Null)> visited = [];
        Queue<(BasicBlock Block, bool Null)> pending = new();
        pending.Enqueue((synchronousBlock, true));
        while (pending.TryDequeue(out (BasicBlock Block, bool Null) state))
        {
            if (!visited.Add((state.Block.Ordinal, state.Null)))
            {
                continue;
            }

            bool nullDefinition = state.Null;
            if (state.Block.Ordinal == backgroundAssignmentBlock.Ordinal)
            {
                nullDefinition = false;
            }
            else if (state.Block.Ordinal == initializerBlock.Ordinal)
            {
                nullDefinition = true;
            }

            foreach (BasicBlock successor in Successors(graph, state.Block).Where(static block => block.IsReachable))
            {
                pending.Enqueue((successor, nullDefinition));
            }
        }

        foreach (int target in targets)
        {
            if (!visited.Any(state => state.Block == target && state.Null))
            {
                throw new ExtractionException($"The null task reaching definition does not reach CFG block {target} on the false arm.");
            }

            if (visited.Any(state => state.Block == target && !state.Null))
            {
                throw new ExtractionException($"A background task definition reaches CFG block {target} on the false arm.");
            }
        }

        return true;
    }

    private static bool ReachingDefinitionContainsOnlyNull(
        ControlFlowGraph graph,
        BasicBlock initializerBlock,
        BasicBlock backgroundAssignmentBlock,
        BasicBlock targetBlock)
    {
        BasicBlock[] reachable = graph.Blocks.Where(static block => block.IsReachable).ToArray();
        BasicBlock entry = reachable.SingleOrDefault(static block => block.Kind == BasicBlockKind.Entry)
            ?? reachable.OrderBy(static block => block.Ordinal).First();
        Dictionary<int, HashSet<int>> predecessors = reachable.ToDictionary(
            static block => block.Ordinal,
            static _ => new HashSet<int>());
        foreach (BasicBlock block in reachable)
        {
            foreach (BasicBlock successor in Successors(graph, block).Where(static block => block.IsReachable))
            {
                if (predecessors.TryGetValue(successor.Ordinal, out HashSet<int>? incoming))
                {
                    incoming.Add(block.Ordinal);
                }
            }
        }

        Dictionary<int, HashSet<string>> incomingDefinitions = reachable.ToDictionary(
            static block => block.Ordinal,
            static _ => new HashSet<string>(StringComparer.Ordinal));
        Dictionary<int, HashSet<string>> outgoingDefinitions = reachable.ToDictionary(
            static block => block.Ordinal,
            static _ => new HashSet<string>(StringComparer.Ordinal));
        Queue<BasicBlock> pending = new();
        pending.Enqueue(entry);
        HashSet<int> queued = [entry.Ordinal];
        HashSet<int> processed = [];
        while (pending.TryDequeue(out BasicBlock? block))
        {
            queued.Remove(block.Ordinal);
            HashSet<string> before = new(StringComparer.Ordinal);
            if (block.Ordinal != entry.Ordinal && predecessors.TryGetValue(block.Ordinal, out HashSet<int>? incoming))
            {
                foreach (int predecessor in incoming)
                {
                    before.UnionWith(outgoingDefinitions[predecessor]);
                }
            }

            HashSet<string> after = new(before, StringComparer.Ordinal);
            if (block.Ordinal == initializerBlock.Ordinal)
            {
                after.Clear();
                after.Add("null");
            }
            else if (block.Ordinal == backgroundAssignmentBlock.Ordinal)
            {
                after.Clear();
                after.Add("background");
            }

            bool changed = processed.Add(block.Ordinal) ||
                !before.SetEquals(incomingDefinitions[block.Ordinal]) ||
                !after.SetEquals(outgoingDefinitions[block.Ordinal]);
            incomingDefinitions[block.Ordinal] = before;
            outgoingDefinitions[block.Ordinal] = after;
            if (!changed)
            {
                continue;
            }

            foreach (BasicBlock successor in Successors(graph, block).Where(static block => block.IsReachable))
            {
                if (queued.Add(successor.Ordinal))
                {
                    pending.Enqueue(successor);
                }
            }
        }

        return incomingDefinitions.TryGetValue(targetBlock.Ordinal, out HashSet<string>? definitions) &&
            definitions.Count == 1 && definitions.Contains("null");
    }

    private static bool AssignmentTargetsLocal(AssignmentExpressionSyntax assignment, SemanticModel model, ILocalSymbol local) =>
        assignment.Left.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Any(identifier => IsLocalReference(identifier, model, local));

    private static bool AssignmentTargetsProperty(
        AssignmentExpressionSyntax assignment,
        SemanticModel model,
        string propertySymbol)
    {
        IOperation? target = model.GetOperation(assignment.Left) ?? model.GetOperation(assignment);
        return target is not null && DescendantOperations(target).OfType<IPropertyReferenceOperation>()
            .Any(property => property.Property.ToSourceIdentity() == propertySymbol);
    }

    private static bool IsDirectPropertyAssignment(
        AssignmentExpressionSyntax assignment,
        SemanticModel model,
        string propertySymbol) =>
        model.GetOperation(assignment) is ISimpleAssignmentOperation
        {
            IsRef: false,
            Target: IPropertyReferenceOperation property,
        } && property.Property.ToSourceIdentity() == propertySymbol;

    private static bool IsExactInvocation(
        InvocationExpressionSyntax? invocation,
        SemanticModel model,
        string symbolId) =>
        invocation is not null && model.GetOperation(invocation) is IInvocationOperation operation &&
        !HasErrorSymbol(operation.TargetMethod) &&
        operation.TargetMethod.ToSourceIdentity() == symbolId;

    private static bool IsLocalReference(IdentifierNameSyntax identifier, SemanticModel model, ILocalSymbol local) =>
        model.GetSymbolInfo(identifier).Symbol is ILocalSymbol symbol &&
        SymbolEqualityComparer.Default.Equals(symbol, local);

    private static IEnumerable<IOperation> DescendantOperations(IOperation operation)
    {
        yield return operation;
        foreach (IOperation child in operation.ChildOperations)
        {
            foreach (IOperation descendant in DescendantOperations(child))
            {
                yield return descendant;
            }
        }
    }

    private static IEnumerable<IOperation> ExecutableDescendantOperations(IOperation operation)
    {
        yield return operation;
        if (operation is IAnonymousFunctionOperation or ILocalFunctionOperation)
        {
            yield break;
        }

        foreach (IOperation child in operation.ChildOperations)
        {
            foreach (IOperation descendant in ExecutableDescendantOperations(child))
            {
                yield return descendant;
            }
        }
    }

    private static bool UsesLocal(IOperation? operation, ILocalSymbol local) =>
        operation is not null && DescendantOperations(operation)
            .OfType<ILocalReferenceOperation>()
            .Any(reference => SymbolEqualityComparer.Default.Equals(reference.Local, local));

    private static bool BlockContainsPosition(BasicBlock block, int position) =>
        block.Operations.Any(operation => ContainsPosition(operation, position)) ||
        block.BranchValue is not null && ContainsPosition(block.BranchValue, position);

    private static bool IsWithin(SyntaxNode parent, SyntaxNode child) =>
        child.SpanStart >= parent.SpanStart && child.Span.End <= parent.Span.End;

    private static BridgeIdentity BuildBridge(string root)
    {
        string generated = ReadAndHash(root, FoldGeneratedPath);
        string refinement = ReadAndHash(root, FoldRefinementPath);
        return new(
            "SequentialBlockTransactionFoldExtractor",
            FoldGeneratedPath,
            generated,
            FoldRefinementPath,
            refinement,
            BridgeRelation,
            FoldTerminalFields,
            FoldPremises);
    }

    private static string ReadAndHash(string root, string relativePath)
    {
        string path = Path.Combine(root, relativePath);
        if (!File.Exists(path))
        {
            throw new ExtractionException($"Missing bridge artifact '{relativePath}'.");
        }

        return Sha256(File.ReadAllBytes(path));
    }

    private static AnchorIdentity BindAnchorNode(
        string id,
        SourceFile source,
        MethodDeclarationSyntax method,
        SyntaxNode node,
        SemanticModel model,
        Compilation compilation,
        string relation) =>
        new(id, source.RelativePath, OwnerPath(method), Canonical(node), relation,
            BindNode(source, method, node, model, compilation, null,
                model.GetOperation(node) ?? throw new ExtractionException($"No IOperation for anchor '{id}'."),
                model.GetDeclaredSymbol(method)));

    private static TypedBinding BindNode(
        SourceFile source,
        MethodDeclarationSyntax method,
        SyntaxNode node,
        SemanticModel model,
        Compilation compilation,
        ControlFlowGraph? graph,
        IOperation operation,
        ISymbol? fallbackSymbol)
    {
        if (operation is IInvalidOperation)
        {
            throw new ExtractionException($"Source node '{Canonical(node)}' has an invalid Roslyn operation.");
        }

        SymbolInfo info = model.GetSymbolInfo(node);
        if (info.CandidateReason != CandidateReason.None || info.CandidateSymbols.Length != 0)
        {
            throw new ExtractionException($"Candidate or ambiguous symbol at '{source.RelativePath}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}'.");
        }

        ISymbol? symbol = operation switch
        {
            IInvocationOperation call => call.TargetMethod,
            IVariableDeclaratorOperation variable => variable.Symbol,
            ISimpleAssignmentOperation { Target: IPropertyReferenceOperation propertyTarget } => propertyTarget.Property,
            IIsPatternOperation { Value: ILocalReferenceOperation local } => local.Local,
            IPropertyReferenceOperation property => property.Property,
            IEventReferenceOperation eventReference => eventReference.Event,
            ILocalReferenceOperation local => local.Local,
            _ => info.Symbol ?? model.GetDeclaredSymbol(node) ?? fallbackSymbol,
        };
        ControlFlowGraph? resolvedGraph = graph ?? TryCreateGraph(model, method);
        int position = ControlFlowPosition(node);
        int block = node is MethodDeclarationSyntax ? -1 : FindControlFlowBlock(resolvedGraph, position);
        BasicBlock? containing = block < 0 || resolvedGraph is null
            ? null
            : resolvedGraph.Blocks.SingleOrDefault(candidate => candidate.Ordinal == block);
        if (node is not MethodDeclarationSyntax &&
            (resolvedGraph is null || containing is null || block < 0 || !containing.IsReachable))
        {
            throw new ExtractionException($"Source node '{Canonical(node)}' is not bound to exactly one reachable CFG block.");
        }
        FileLinePositionSpan span = node.GetLocation().GetLineSpan();
        bool error = HasErrorSymbol(symbol) || HasErrorType(operation.Type) ||
            operation is IInvocationOperation invocation &&
            (HasErrorSymbol(invocation.TargetMethod) || HasErrorType(invocation.Instance?.Type));
        if (operation is IInvocationOperation invocationOperation && invocationOperation.TargetMethod is null)
        {
            throw new ExtractionException($"Invocation at '{source.RelativePath}:{span.StartLinePosition.Line + 1}' has no target symbol.");
        }

        DataFlowAnalysis? dataFlow = TryAnalyzeDataFlow(model, method);
        string canonical = Canonical(node);
        string symbolId = operation is IInvocationOperation target
            ? target.TargetMethod.ToSourceIdentity()
            : symbol?.ToSourceIdentity() ?? string.Empty;
        return new(
            source.RelativePath,
            OwnerPath(method),
            method.Identifier.ValueText,
            node.Kind().ToString(),
            canonical,
            Sha256(Encoding.UTF8.GetBytes(canonical)),
            symbolId,
            operation is IInvocationOperation invocationKind ? invocationKind.TargetMethod.Kind.ToString() : symbol?.Kind.ToString() ?? string.Empty,
            operation.Kind.ToString(),
            ArtifactSafety.TypeIdentity(operation switch
            {
                IVariableDeclaratorOperation variable => variable.Symbol.Type,
                IReturnOperation { ReturnedValue: not null } returned => returned.ReturnedValue.Type,
                _ => operation.Type,
            }),
            operation is IInvocationOperation instance && instance.Instance?.Type is ITypeSymbol instanceType
                ? ArtifactSafety.TypeIdentity(instanceType)
                : operation is IInvocationOperation { TargetMethod.IsExtensionMethod: true } extension && extension.Arguments.Length != 0
                    ? ArtifactSafety.TypeIdentity(extension.Arguments[0].Value.Type) : string.Empty,
            method.Identifier.ValueText,
            position,
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            block,
            node is not MethodDeclarationSyntax && containing?.IsReachable == true,
            info.CandidateSymbols.Length != 0,
            info.CandidateReason.ToString(),
            error,
            dataFlow?.Succeeded == true,
            dataFlow?.ReadInside.Select(static item => item.Name).OrderBy(static name => name, StringComparer.Ordinal).ToArray() ?? [],
            dataFlow?.WrittenInside.Select(static item => item.Name).OrderBy(static name => name, StringComparer.Ordinal).ToArray() ?? []);
    }

    private static ControlFlowIdentity BuildControlFlow(
        string id,
        SourceFile source,
        MethodDeclarationSyntax method,
        SemanticModel model,
        Compilation compilation)
    {
        ControlFlowGraph graph = TryCreateGraph(model, method)
            ?? throw new ExtractionException($"No CFG for '{id}'.");
        BasicBlock[] reachable = graph.Blocks.Where(static block => block.IsReachable).ToArray();
        if (reachable.Length == 0)
        {
            throw new ExtractionException($"CFG '{id}' has no reachable blocks.");
        }

        ControlFlowTransferIdentity[] transfers = ReadTransfers(graph);
        List<string> edges = TransferEdges(transfers).ToList();
        List<string> backEdges = edges.Where(edge =>
        {
            TryParseEdge(edge, out int from, out int to);
            return to <= from;
        }).ToList();
        List<int> normalExits = reachable.Where(static block => block.Kind == BasicBlockKind.Exit)
            .Select(static block => block.Ordinal).ToList();
        HashSet<int> edgeSources = edges.Select(edge =>
        {
            TryParseEdge(edge, out int from, out _);
            return from;
        }).ToHashSet();
        List<int> exceptionalExits = transfers.Where(transfer => transfer.Destination == -1 &&
                !edgeSources.Contains(transfer.Source))
            .Select(static transfer => transfer.Source).ToList();
        ValidateTransfers(transfers, reachable.Select(static block => block.Ordinal).ToArray(), edges.ToArray());
        int[] reachableOrdinals = reachable.Select(static block => block.Ordinal).OrderBy(static ordinal => ordinal).ToArray();
        string[] orderedEdges = edges.Distinct(StringComparer.Ordinal).OrderBy(static edge => edge, StringComparer.Ordinal).ToArray();
        int[] orderedNormalExits = normalExits.Distinct().OrderBy(static ordinal => ordinal).ToArray();
        int[] orderedExceptionalExits = exceptionalExits.Distinct().OrderBy(static ordinal => ordinal).ToArray();
        int[] exceptionHandlerEntries = ReadExceptionHandlerEntries(graph);
        string[] orderedBackEdges = backEdges.Distinct(StringComparer.Ordinal).OrderBy(static edge => edge, StringComparer.Ordinal).ToArray();
        ControlFlowBlockMembership[] blockMemberships = reachable.OrderBy(static block => block.Ordinal)
            .Select(static block => new ControlFlowBlockMembership(block.Ordinal, CollectSyntaxStarts(block)))
            .ToArray();
        ValidateControlFlowShape(id, reachableOrdinals, orderedEdges, orderedNormalExits, orderedExceptionalExits,
            exceptionHandlerEntries, orderedBackEdges, blockMemberships, transfers);
        string shapeSha256 = ControlFlowShapeSha256(id, reachableOrdinals, orderedEdges, orderedNormalExits,
            orderedExceptionalExits, exceptionHandlerEntries, orderedBackEdges, blockMemberships, transfers);

        IOperation operation = model.GetOperation(method)
            ?? throw new ExtractionException($"No method operation for CFG '{id}'.");
        TypedBinding binding = BindNode(source, method, method, model, compilation, graph, operation,
            model.GetDeclaredSymbol(method));
        return new(
            id,
            source.RelativePath,
            OwnerPath(method),
            method.Identifier.ValueText,
            reachableOrdinals,
            orderedEdges,
            orderedNormalExits,
            orderedExceptionalExits,
            exceptionHandlerEntries,
            orderedBackEdges,
            blockMemberships,
            transfers,
            shapeSha256,
            binding);
    }

    private static int[] CollectSyntaxStarts(BasicBlock block)
    {
        HashSet<int> starts = [];
        foreach (IOperation operation in block.Operations)
        {
            CollectSyntaxStarts(operation, starts);
        }

        if (block.BranchValue is not null)
        {
            CollectSyntaxStarts(block.BranchValue, starts);
        }

        return starts.OrderBy(static start => start).ToArray();
    }

    private static void CollectSyntaxStarts(IOperation operation, HashSet<int> starts)
    {
        if (OwnsSourcePosition(operation))
        {
            starts.Add(operation.Syntax.SpanStart);
        }

        foreach (IOperation child in operation.ChildOperations)
        {
            CollectSyntaxStarts(child, starts);
        }
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

    private static void ValidateControlFlowShape(
        string id,
        int[] reachableBlocks,
        string[] edges,
        int[] normalExitBlocks,
        int[] exceptionalExitBlocks,
        int[] exceptionHandlerEntries,
        string[] backEdges,
        ControlFlowBlockMembership[] blockMemberships,
        ControlFlowTransferIdentity[] transfers)
    {
        if (reachableBlocks is null || edges is null || normalExitBlocks is null || exceptionalExitBlocks is null ||
            exceptionHandlerEntries is null || backEdges is null ||
            blockMemberships is null ||
            reachableBlocks.Length == 0 || !IsStrictlySortedDistinct(reachableBlocks) ||
            !edges.SequenceEqual(edges.Distinct(StringComparer.Ordinal).OrderBy(static edge => edge, StringComparer.Ordinal), StringComparer.Ordinal) ||
            !IsStrictlySortedDistinct(normalExitBlocks) || !IsStrictlySortedDistinct(exceptionalExitBlocks) ||
            !IsStrictlySortedDistinct(exceptionHandlerEntries) ||
            !backEdges.SequenceEqual(backEdges.Distinct(StringComparer.Ordinal).OrderBy(static edge => edge, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new ExtractionException($"CFG '{id}' has malformed, duplicate, or unsorted identity arrays.");
        }

        if (blockMemberships.Length != reachableBlocks.Length ||
            !blockMemberships.Select(static membership => membership.Ordinal)
                .SequenceEqual(reachableBlocks) ||
            blockMemberships.Any(static membership => membership.SyntaxStarts is null ||
                !IsStrictlySortedDistinct(membership.SyntaxStarts) || membership.SyntaxStarts.Any(static start => start < 0)))
        {
            throw new ExtractionException($"CFG '{id}' has incomplete syntax-start block membership evidence.");
        }

        ValidateTransfers(transfers, reachableBlocks, edges);
        HashSet<int> syntaxStarts = [];
        foreach (ControlFlowBlockMembership membership in blockMemberships)
        {
            foreach (int start in membership.SyntaxStarts)
            {
                if (!syntaxStarts.Add(start))
                {
                    throw new ExtractionException($"CFG '{id}' assigns one source syntax start to multiple blocks.");
                }
            }
        }

        if (normalExitBlocks.Length == 0 && exceptionalExitBlocks.Length == 0)
        {
            throw new ExtractionException($"CFG '{id}' has no reachable normal or exceptional exit endpoint.");
        }

        HashSet<int> reachable = reachableBlocks.ToHashSet();
        if (exceptionHandlerEntries.Length != (id == "block.processBlock" ? 1 : 0) ||
            exceptionHandlerEntries.Any(entry => !reachable.Contains(entry) || entry == reachableBlocks[0]))
        {
            throw new ExtractionException($"CFG '{id}' exception-handler entry regions changed.");
        }
        HashSet<string> edgeSet = edges.ToHashSet(StringComparer.Ordinal);
        HashSet<int> outgoingSources = [];
        foreach (string edge in edges)
        {
            if (!TryParseEdge(edge, out int source, out int destination) ||
                !reachable.Contains(source) || !reachable.Contains(destination))
            {
                throw new ExtractionException($"CFG '{id}' contains an edge with an unreachable or malformed endpoint: '{edge}'.");
            }

            outgoingSources.Add(source);
        }

        foreach (int exit in normalExitBlocks.Concat(exceptionalExitBlocks))
        {
            if (!reachable.Contains(exit))
            {
                throw new ExtractionException($"CFG '{id}' contains an exit endpoint outside its reachable blocks: {exit}.");
            }

            outgoingSources.Add(exit);
        }

        if (reachableBlocks.Any(block => !outgoingSources.Contains(block)))
        {
            throw new ExtractionException($"CFG '{id}' has a reachable block without an edge or exit endpoint.");
        }

        Dictionary<int, List<int>> successors = reachableBlocks.ToDictionary(
            static block => block,
            static _ => new List<int>());
        foreach (string edge in edges)
        {
            TryParseEdge(edge, out int source, out int destination);
            successors[source].Add(destination);
        }

        HashSet<int> graphReachable = [];
        Queue<int> pending = new();
        pending.Enqueue(reachableBlocks[0]);
        foreach (int handlerEntry in exceptionHandlerEntries) pending.Enqueue(handlerEntry);
        while (pending.TryDequeue(out int block))
        {
            if (!graphReachable.Add(block))
            {
                continue;
            }

            foreach (int successor in successors[block])
            {
                pending.Enqueue(successor);
            }
        }

        if (graphReachable.Count != reachableBlocks.Length)
        {
            throw new ExtractionException($"CFG '{id}' contains a disconnected reachable block.");
        }

        if (normalExitBlocks.Intersect(exceptionalExitBlocks).Any())
        {
            throw new ExtractionException($"CFG '{id}' classifies one exit as both normal and exceptional.");
        }

        foreach (string edge in backEdges)
        {
            if (!edgeSet.Contains(edge) || !TryParseEdge(edge, out int source, out int destination) || destination > source)
            {
                throw new ExtractionException($"CFG '{id}' contains a back edge that is not an edge or is not backward: '{edge}'.");
            }
        }
    }

    private static bool IsStrictlySortedDistinct(int[] values) =>
        values.SequenceEqual(values.Distinct().OrderBy(static value => value));

    private static string ControlFlowShapeSha256(
        string id,
        int[] reachableBlocks,
        string[] edges,
        int[] normalExitBlocks,
        int[] exceptionalExitBlocks,
        int[] exceptionHandlerEntries,
        string[] backEdges,
        ControlFlowBlockMembership[] blockMemberships,
        ControlFlowTransferIdentity[] transfers)
    {
        string memberships = string.Join(";", blockMemberships.Select(static membership =>
            $"{membership.Ordinal}:{string.Join(",", membership.SyntaxStarts)}"));
        string payload = string.Join("\n", new[]
        {
            id,
            string.Join(",", reachableBlocks),
            string.Join(",", edges),
            string.Join(",", normalExitBlocks),
            string.Join(",", exceptionalExitBlocks),
            string.Join(",", exceptionHandlerEntries),
            string.Join(",", backEdges),
            memberships,
            JsonSerializer.Serialize(transfers, JsonOptions),
        }) + "\n";
        return Sha256(Encoding.UTF8.GetBytes(payload));
    }

    private static bool TryParseEdge(string value, out int source, out int destination)
    {
        source = 0;
        destination = 0;
        string[] parts = value?.Split("->", StringSplitOptions.None) ?? [];
        return parts.Length == 2 && int.TryParse(parts[0], out source) && int.TryParse(parts[1], out destination) &&
            value == $"{source}->{destination}";
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

        (int Ordinal, int Width)[] candidates = graph.Blocks
            .SelectMany(block => block.Operations
                .Concat(block.BranchValue is null ? [] : [block.BranchValue])
                .SelectMany(DescendantOperations)
                .Where(operation => OwnsSourcePosition(operation) && ContainsPosition(operation, position))
                .Select(operation => (block.Ordinal, operation.Syntax.Span.Length)))
            .ToArray();
        int shortest = candidates.Length == 0 ? -1 : candidates.Min(static candidate => candidate.Width);
        int[] matches = candidates.Where(candidate => candidate.Width == shortest)
            .Select(static candidate => candidate.Ordinal).Distinct().ToArray();
        if (matches.Length > 1)
        {
            throw new ExtractionException($"Source position {position} resolves to multiple CFG blocks.");
        }

        return matches.Length == 1 ? matches[0] : -1;
    }

    private static bool ContainsPosition(IOperation operation, int position) =>
        operation.Syntax is not null && operation.Syntax.Span.Contains(position);

    private static int ControlFlowPosition(SyntaxNode node) =>
        node is ReturnStatementSyntax { Expression: not null } returned
            ? returned.Expression.SpanStart : node.SpanStart;

    // Lowered captures refer back to the event expression without reevaluating it. Container
    // statement spans can likewise cover both sides of a conditional-access split.
    private static bool OwnsSourcePosition(IOperation operation) =>
        (!operation.IsImplicit || operation is ISimpleAssignmentOperation { Syntax: VariableDeclaratorSyntax }) &&
        operation is not (IExpressionStatementOperation or IFlowCaptureOperation or
            IFlowCaptureReferenceOperation or IIsNullOperation or IConditionalAccessInstanceOperation);

    private static DataFlowAnalysis? TryAnalyzeDataFlow(SemanticModel model, MethodDeclarationSyntax method)
    {
        try
        {
            if (method.Body is not null)
            {
                return model.AnalyzeDataFlow(method.Body);
            }

            return method.ExpressionBody is null ? null : model.AnalyzeDataFlow(method.ExpressionBody.Expression);
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

    private static ClassDeclarationSyntax FindClass(SourceFile source, string name) =>
        source.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .SingleOrDefault(type => type.Identifier.ValueText == name)
        ?? throw new ExtractionException($"{name} declaration is missing or ambiguous.");

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

    private static IfStatementSyntax FindIf(SyntaxNode owner, Func<ExpressionSyntax, bool> predicate, string description)
    {
        IfStatementSyntax[] matches = owner.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(statement => predicate(statement.Condition)).ToArray();
        if (matches.Length != 1)
        {
            throw new ExtractionException($"{description} is missing or ambiguous.");
        }

        return matches[0];
    }

    private static InvocationExpressionSyntax SingleInvocation(SyntaxNode owner, string canonical, string description)
    {
        InvocationExpressionSyntax[] matches = ExecutableDescendantNodes<InvocationExpressionSyntax>(owner)
            .Where(invocation => Canonical(invocation) == canonical).ToArray();
        if (matches.Length != 1)
        {
            throw new ExtractionException($"{description} expected one '{canonical}' invocation, found {matches.Length}.");
        }

        return matches[0];
    }

    private static ReturnStatementSyntax FindOuterReceiptsReturn(MethodDeclarationSyntax method)
    {
        ReturnStatementSyntax[] returns = ExecutableDescendantNodes<ReturnStatementSyntax>(method)
            .Where(static statement => Canonical(statement) == "returnreceipts;")
            .ToArray();
        if (method.Body is null || returns.Length != 1 ||
            returns[0].Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() != method ||
            !IsWithin(method.Body, returns[0]))
        {
            throw new ExtractionException("The normal receipts return must be the unique return in the outer ProcessBlock body.");
        }

        return returns[0];
    }

    private static IEnumerable<T> ExecutableDescendantNodes<T>(SyntaxNode root)
        where T : SyntaxNode => root.DescendantNodes()
        .OfType<T>()
        .Where(static node => !node.Ancestors().Any(static ancestor =>
            ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax));

    private static AnchorIdentity Anchor(IReadOnlyList<AnchorIdentity> anchors, string id) =>
        anchors.Single(anchor => anchor.Id == id);

    private static void ValidateIr(IrDocument document)
    {
        if (document is null || document.SchemaVersion != SchemaVersion || document.ExtractorVersion != ExtractorVersion ||
            document.Kernel != Kernel || document.Included is null || document.Excluded is null || document.Sources is null ||
            document.CompilerClosure is null || document.Members is null || document.Anchors is null ||
            document.ControlFlows is null || document.Commits is null || document.Guards is null || document.Steps is null ||
            document.HeaderAssignments is null || document.OpaqueDelegates is null || document.SourceEntryAdapters is null || document.TaskFlow is null ||
            document.Bridge is null || document.OpenObligations is null ||
            document.Sources.Any(static source => source is null) ||
            document.Members.Any(static member => member is null) ||
            document.Anchors.Any(static anchor => anchor is null) ||
            document.ControlFlows.Any(static flow => flow is null) ||
            document.Commits.Any(static commit => commit is null) ||
            document.Guards.Any(static guard => guard is null) ||
            document.Steps.Any(static step => step is null) ||
            document.HeaderAssignments.Any(static assignment => assignment is null) ||
            document.HeaderAssignments.Any(static assignment => assignment.ExcludedBindings is null ||
                assignment.ExcludedBindings.Any(static binding => binding is null)) ||
            document.OpaqueDelegates.Any(static item => item is null) ||
            document.SourceEntryAdapters.Any(static adapter => adapter is null) ||
            document.Bridge.RequiredTerminalFields is null || document.Bridge.RequiredFoldPremises is null ||
            document.Bridge.RequiredTerminalFields.Any(static field => string.IsNullOrWhiteSpace(field)) ||
            document.Bridge.RequiredFoldPremises.Any(static premise => string.IsNullOrWhiteSpace(premise)))
        {
            throw new ExtractionException("The post-transaction finalization IR header or required arrays changed.");
        }

        if (!document.Included.SequenceEqual(IncludedScope, StringComparer.Ordinal) ||
            !document.Excluded.SequenceEqual(ExcludedScope, StringComparer.Ordinal) ||
            !document.OpenObligations.SequenceEqual(OpenObligations, StringComparer.Ordinal))
        {
            throw new ExtractionException("The post-transaction finalization scope or obligations changed.");
        }

        if (document.Sources.Length != SourcePaths.Length ||
            !document.Sources.Select(static source => source.Path).SequenceEqual(SourcePaths, StringComparer.Ordinal) ||
            !document.Sources.Select(static source => source.Role).SequenceEqual(SourceRoles, StringComparer.Ordinal) ||
            document.Sources.Any(static source => !IsSha256(source.Sha256) || !IsSha256(source.SyntaxSha256)))
        {
            throw new ExtractionException("The source identity set is incomplete.");
        }

        if (document.CompilerClosure.InventoryPath != CompilerReferenceInventoryPath ||
            document.CompilerClosure.Count != CompilerReferenceCount ||
            !IsSha256(document.CompilerClosure.AggregateSha256) ||
            !IsSha256(document.CompilerClosure.InventorySha256) ||
            document.CompilerClosure.SupportSources is null || !document.CompilerClosure.SupportSources.SequenceEqual(CompilerSupportSources))
        {
            throw new ExtractionException("The deterministic compiler-reference closure changed.");
        }

        string[] expectedMembers =
            ["block.processBlock", "block.commit-no-roots", "block.commit-roots", "block.compute-state-root", "block.set-account-changes"];
        if (document.Members.Length != expectedMembers.Length ||
            !document.Members.Select(static member => member.Id).SequenceEqual(expectedMembers, StringComparer.Ordinal) ||
            !document.Members.Select(static member => member.Path).SequenceEqual(
                [BlockProcessorPath, BlockProcessorPath, BlockProcessorPath, BlockProcessorPath, BlockProcessorPath], StringComparer.Ordinal) ||
            !document.Members.Select(static member => member.Owner).SequenceEqual(
                ["BlockProcessor", "BlockProcessor", "BlockProcessor", "BlockProcessor", "BlockProcessor"], StringComparer.Ordinal) ||
            !document.Members.Select(static member => member.Name).SequenceEqual(
                ["ProcessBlock", "CommitState", "CommitStateAndStorageRoots", "ComputeStateRoot", "SetAccountChanges"], StringComparer.Ordinal) ||
            !document.Members.Select(static member => member.ParameterCount).SequenceEqual([5, 1, 1, 1, 1]) ||
            !document.Members.Select(static member => member.Signature).SequenceEqual(MemberSignatures, StringComparer.Ordinal) ||
            document.Members.Any(static member => member.Binding is null || member.Binding.Path != member.Path ||
                member.Binding.Owner != member.Owner || member.Binding.Member != member.Name ||
                member.Binding.SymbolId != member.Signature || member.Binding.SymbolKind != "Method" ||
                member.Binding.NodeKind != "MethodDeclaration" || member.Binding.ControlFlowBlock != -1 || member.Binding.IsReachable ||
                member.Binding.OperationKind.Length == 0 || !member.Binding.DataFlowSucceeded ||
                member.Binding.IsErrorSymbol || member.Binding.HasCandidateSymbols ||
                member.Binding.SyntaxSha256 != Sha256(Encoding.UTF8.GetBytes(member.Binding.CanonicalSyntax))))
        {
            throw new ExtractionException("The typed finalization member identities changed.");
        }

        ValidateHelperPreservation(document.HelperPreservation, document.Members);
        ValidateInstrumentationBoundary(document.Instrumentation, document.HelperPreservation);
        ValidateEntryPreservation(document.EntryPreservation);

        if (document.Anchors.Length != AnchorIds.Length ||
            !document.Anchors.Select(static anchor => anchor.Id).SequenceEqual(AnchorIds, StringComparer.Ordinal) ||
            !document.Anchors.Select(static anchor => anchor.Relation).SequenceEqual(AnchorRelations, StringComparer.Ordinal) ||
            document.Anchors.Any(static anchor => anchor is null || anchor.Path != BlockProcessorPath ||
                anchor.Owner != "BlockProcessor" || anchor.Binding is null || anchor.Binding.Path != anchor.Path ||
                anchor.Binding.Owner != anchor.Owner || anchor.Binding.Member != "ProcessBlock" ||
                anchor.Binding.ContainingMember != "ProcessBlock" || anchor.CanonicalSyntax != anchor.Binding.CanonicalSyntax ||
                !anchor.Binding.IsReachable || anchor.Binding.ControlFlowBlock < 0 ||
                anchor.Binding.HasCandidateSymbols || anchor.Binding.IsErrorSymbol || !anchor.Binding.DataFlowSucceeded ||
                anchor.Binding.SyntaxSha256 != Sha256(Encoding.UTF8.GetBytes(anchor.CanonicalSyntax))))
        {
            throw new ExtractionException("The typed finalization anchor admission changed.");
        }

        if (!document.Anchors.Select(static anchor => anchor.Binding.SymbolId).ToArray()
                .SequenceEqual(AnchorSymbolIds, StringComparer.Ordinal) ||
            !document.Anchors.Select(static anchor => anchor.Binding.OperationKind).ToArray()
                .SequenceEqual(AnchorOperationKinds, StringComparer.Ordinal) ||
            !document.Anchors.Select(static anchor => anchor.Binding.NodeKind).ToArray()
                .SequenceEqual(AnchorNodeKinds, StringComparer.Ordinal) ||
            !document.Anchors.Select(static anchor => anchor.Binding.OperationType).ToArray()
                .SequenceEqual(AnchorOperationTypes, StringComparer.Ordinal) ||
            !document.Anchors.Select(static anchor => anchor.Binding.SymbolKind).ToArray()
                .SequenceEqual(AnchorSymbolKinds, StringComparer.Ordinal))
        {
            string[] drift = document.Anchors.Select((anchor, index) =>
                (anchor.Binding.SymbolId, anchor.Binding.OperationKind, anchor.Binding.NodeKind, anchor.Binding.OperationType, anchor.Binding.SymbolKind) ==
                (AnchorSymbolIds[index], AnchorOperationKinds[index], AnchorNodeKinds[index], AnchorOperationTypes[index], AnchorSymbolKinds[index])
                    ? "" : anchor.Id + ": " + anchor.Binding.SymbolId + " / " + anchor.Binding.OperationKind + " / " +
                        anchor.Binding.NodeKind + " / " + anchor.Binding.OperationType + " / " + anchor.Binding.SymbolKind)
                .Where(static detail => detail.Length != 0).ToArray();
            throw new ExtractionException("A finalization anchor was redirected to a different typed Roslyn symbol or operation: " + string.Join("; ", drift));
        }

        ValidateAnchorSyntax(document.Anchors);
        if (document.ControlFlows.Length != 5 ||
            !document.ControlFlows.Select(static flow => flow.Id).SequenceEqual(
                ["block.processBlock", "block.commit-no-roots", "block.commit-roots", "block.compute-state-root", "block.set-account-changes"], StringComparer.Ordinal) ||
            !document.ControlFlows.Select(static flow => flow.Path).SequenceEqual(
                [BlockProcessorPath, BlockProcessorPath, BlockProcessorPath, BlockProcessorPath, BlockProcessorPath], StringComparer.Ordinal) ||
            !document.ControlFlows.Select(static flow => flow.Owner).SequenceEqual(
                ["BlockProcessor", "BlockProcessor", "BlockProcessor", "BlockProcessor", "BlockProcessor"], StringComparer.Ordinal) ||
            !document.ControlFlows.Select(static flow => flow.Member).SequenceEqual(
                ["ProcessBlock", "CommitState", "CommitStateAndStorageRoots", "ComputeStateRoot", "SetAccountChanges"], StringComparer.Ordinal) ||
            document.ControlFlows.Any(static flow => flow is null || flow.Binding is null || flow.ReachableBlocks is null ||
                flow.Edges is null || flow.NormalExitBlocks is null || flow.ExceptionalExitBlocks is null ||
                flow.ExceptionHandlerEntries is null || flow.BackEdges is null ||
                flow.BlockMemberships is null || flow.BlockMemberships.Any(static membership => membership is null) ||
                !IsSha256(flow.ShapeSha256) ||
                flow.ReachableBlocks.Length == 0 ||
                flow.Binding.Path != flow.Path || flow.Binding.Owner != flow.Owner || flow.Binding.Member != flow.Member ||
                flow.Binding.NodeKind != "MethodDeclaration" || flow.Binding.SymbolKind != "Method" ||
                flow.Binding.ContainingMember != flow.Member || flow.Binding.HasCandidateSymbols || flow.Binding.IsErrorSymbol ||
                !flow.Binding.DataFlowSucceeded || flow.Binding.ControlFlowBlock != -1 || flow.Binding.IsReachable ||
                flow.Binding.SyntaxSha256 != Sha256(Encoding.UTF8.GetBytes(flow.Binding.CanonicalSyntax))))
        {
            throw new ExtractionException("The typed finalization CFG evidence is incomplete.");
        }

        foreach (ControlFlowIdentity flow in document.ControlFlows)
        {
            ValidateControlFlowShape(flow.Id, flow.ReachableBlocks, flow.Edges, flow.NormalExitBlocks,
                flow.ExceptionalExitBlocks, flow.ExceptionHandlerEntries, flow.BackEdges, flow.BlockMemberships, flow.Transfers);
            if (flow.ShapeSha256 != ControlFlowShapeSha256(flow.Id, flow.ReachableBlocks, flow.Edges,
                    flow.NormalExitBlocks, flow.ExceptionalExitBlocks, flow.ExceptionHandlerEntries,
                    flow.BackEdges, flow.BlockMemberships, flow.Transfers))
            {
                throw new ExtractionException($"CFG '{flow.Id}' shape digest does not match its serialized graph.");
            }
        }

        if (document.Commits.Length != CommitEventIds.Length || document.Commits.Any(static commit =>
                commit.Binding is null || commit.InvocationBinding is null))
        {
            throw new ExtractionException("The three distinct post-finalization commit identities changed.");
        }

        string[] expectedCommitIds = ["post-transaction-no-roots", "finalization-no-roots", "storage-roots"];
        string[] expectedCommitAnchors =
            ["block.post-transaction-commit", "block.finalization-commit", "block.storage-roots-commit"];
        string[] expectedCommitMethods = ["block.commit-no-roots", "block.commit-no-roots", "block.commit-roots"];
        bool[] expectedCommitRoots = [false, false, true];
        string[] expectedCommitCalls =
            ["_stateProvider.Commit(spec,commitRoots:false)", "_stateProvider.Commit(spec,commitRoots:false)",
                "_stateProvider.Commit(spec,commitRoots:true)"];
        string[] expectedCommitMembers = ["CommitState", "CommitState", "CommitStateAndStorageRoots"];
        if (!document.Commits.Select(static commit => commit.Id).SequenceEqual(expectedCommitIds, StringComparer.Ordinal) ||
            !document.Commits.Select(static commit => commit.InvocationAnchorId).SequenceEqual(expectedCommitAnchors, StringComparer.Ordinal) ||
            !document.Commits.Select(static commit => commit.Event).SequenceEqual(CommitEventIds, StringComparer.Ordinal) ||
            !document.Commits.Select(static commit => commit.MethodId).SequenceEqual(expectedCommitMethods, StringComparer.Ordinal) ||
            !document.Commits.Select(static commit => commit.CommitRoots).SequenceEqual(expectedCommitRoots) ||
            !document.Commits.Select(static commit => commit.StateProviderCall).SequenceEqual(expectedCommitCalls, StringComparer.Ordinal) ||
            !document.Commits.Select(static commit => commit.Binding.Member).SequenceEqual(expectedCommitMembers, StringComparer.Ordinal) ||
            document.Commits.Any(static commit => commit.Binding.Path != BlockProcessorPath || commit.Binding.Owner != "BlockProcessor" ||
                commit.Binding.ContainingMember != commit.Binding.Member) ||
            document.Commits.Any(static commit => commit.Binding.OperationKind != "Invocation" ||
                commit.Binding.NodeKind != "InvocationExpression" || commit.Binding.OperationType != "void" ||
                commit.Binding.ReceiverType != "Nethermind.Evm.State.IWorldState" ||
                commit.Binding.CanonicalSyntax != commit.StateProviderCall ||
                commit.Binding.SymbolId != DirectCommitSymbol ||
                commit.Binding.HasCandidateSymbols || commit.Binding.IsErrorSymbol || !commit.Binding.DataFlowSucceeded) ||
            document.Commits.Zip(expectedCommitAnchors).Any(static pair =>
                pair.First.InvocationBinding.CanonicalSyntax !=
                    (pair.Second == "block.storage-roots-commit"
                        ? "CommitStateAndStorageRoots(spec)"
                        : "CommitState(spec)")))
        {
            throw new ExtractionException("The three post-finalization commit identities or source mappings changed.");
        }

        if (document.Guards.Length != GuardIds.Length ||
            !document.Guards.Select(static guard => guard.Id).SequenceEqual(GuardIds, StringComparer.Ordinal) ||
            !document.Guards.Select(static guard => guard.Condition).SequenceEqual(GuardConditions, StringComparer.Ordinal) ||
            !document.Guards.Select(static guard => guard.ExpectedPolarity).SequenceEqual(GuardPolarities, StringComparer.Ordinal) ||
            !document.Guards.Select(static guard => guard.SelectedArm).SequenceEqual(GuardSelectedArms, StringComparer.Ordinal) ||
            !document.Guards.Select(static guard => guard.ExcludedArm).SequenceEqual(GuardExcludedArms, StringComparer.Ordinal) ||
            document.Guards.Any(static guard => guard.Binding is null || string.IsNullOrWhiteSpace(guard.Condition) ||
                string.IsNullOrWhiteSpace(guard.ExpectedPolarity) || guard.Binding.HasCandidateSymbols || guard.Binding.IsErrorSymbol))
        {
            throw new ExtractionException("The guard polarity/adaptor premises changed.");
        }

        if (document.Steps.Length != StepIds.Length ||
            !document.Steps.Select(static step => step.Ordinal).SequenceEqual(Enumerable.Range(0, StepIds.Length)) ||
            !document.Steps.Select(static step => step.Id).SequenceEqual(StepIds, StringComparer.Ordinal) ||
            !document.Steps.Select(static step => step.AnchorId).SequenceEqual(StepAnchorIds, StringComparer.Ordinal) ||
            !document.Steps.Select(static step => step.Branch).SequenceEqual(StepBranches, StringComparer.Ordinal) ||
            !document.Steps.Select(static step => step.Observable).SequenceEqual(StepObservables, StringComparer.Ordinal) ||
            document.Steps.Any(static step => step.Binding is null || step.Binding.HasCandidateSymbols || step.Binding.IsErrorSymbol ||
                string.IsNullOrWhiteSpace(step.Branch) || string.IsNullOrWhiteSpace(step.Observable)))
        {
            throw new ExtractionException("The finalization observable order changed.");
        }

        ValidateHeaderAssignments(document);

        if (document.OpaqueDelegates.Length != OpaqueDelegateIds.Length ||
            !document.OpaqueDelegates.Select(static item => item.Id).SequenceEqual(OpaqueDelegateIds, StringComparer.Ordinal) ||
            !document.OpaqueDelegates.Select(static item => item.AnchorId).SequenceEqual(OpaqueDelegateAnchorIds, StringComparer.Ordinal) ||
            document.OpaqueDelegates.Any(static item => item is null || !item.RequiresNormalReturn ||
                string.IsNullOrWhiteSpace(item.AnchorId) ||
                item.ThrowScope != "throws are outside the normal-return theorem"))
        {
            throw new ExtractionException("The normal-return opaque delegate premises changed.");
        }

        if (document.SourceEntryAdapters.Length != 1)
        {
            throw new ExtractionException("The typed source-entry adapter set changed.");
        }

        SourceEntryAdapterIdentity sourceEntry = document.SourceEntryAdapters[0];
        ValidatePreservedValues(sourceEntry.PreservedValues);
        if (sourceEntry.Id != SourceEntryAdapterId || sourceEntry.Claim != SourceEntryAdapterClaim ||
            sourceEntry.EntryMemberId != "block.processBlock" ||
            sourceEntry.ExactBaseReceiver != SourceEntryExactBaseReceiver ||
            sourceEntry.SelectedStandardExecutor != SourceEntrySelectedExecutor ||
            sourceEntry.BalPremise != SourceEntryBalPremise ||
            sourceEntry.BackgroundPremise != SourceEntryBackgroundPremise ||
            sourceEntry.MainThreadPremise != SourceEntryMainThreadPremise ||
            sourceEntry.StateRootPremise != SourceEntryStateRootPremise ||
            sourceEntry.SpecPremise != SourceEntrySpecPremise)
        {
            throw new ExtractionException("The exact-base/source-entry premises changed.");
        }

        if (sourceEntry.TransactionsExecutedNormalReturnPremise != SourceEntryTransactionsExecutedNormalReturnPremise ||
            sourceEntry.PostTransactionCommitNormalReturnPremise != SourceEntryPostTransactionCommitNormalReturnPremise ||
            sourceEntry.TransactionsExecutedEventSymbol != TransactionsExecutedEventSymbol)
        {
            throw new ExtractionException("The separate source-entry normal-return boundary premises changed.");
        }

        if (sourceEntry.EntryBinding is null || sourceEntry.EntryBinding.Member != "ProcessBlock" ||
            sourceEntry.ExecutorBinding is null || sourceEntry.ExecutorBinding.CanonicalSyntax != SourceEntrySelectedExecutor ||
            sourceEntry.ReceiptsReferenceCount != ReceiptsReferenceCount || sourceEntry.ReceiptsWriteCount != ReceiptsWriteCount ||
            sourceEntry.ReceiptsBinding is null || sourceEntry.ReceiptsBinding.NodeKind != "VariableDeclarator" ||
            sourceEntry.ReceiptsBinding.OperationKind != "VariableDeclarator" ||
            sourceEntry.ReceiptsBinding.OperationType != ReceiptsOperationType ||
            sourceEntry.ReceiptsBinding.SymbolKind != "Local" || sourceEntry.ReceiptsBinding.SymbolId != ReceiptsLocalSymbol ||
            sourceEntry.ReceiptsBinding.CanonicalSyntax != ReceiptsInitializerSyntax ||
            sourceEntry.BalBinding is null || sourceEntry.BalBinding.CanonicalSyntax !=
                "_balManager.SetBlockAccessList(block)" ||
            sourceEntry.BackgroundBinding is null || sourceEntry.BackgroundBinding.CanonicalSyntax !=
                "ShouldCalculateReceiptsInBackground(receipts)" ||
            sourceEntry.MainThreadBinding is null || sourceEntry.MainThreadBinding.CanonicalSyntax !=
                "BlockchainProcessor.IsMainProcessingThread" ||
            sourceEntry.StateRootBinding is null || sourceEntry.StateRootBinding.CanonicalSyntax !=
                "ShouldComputeStateRoot(header)" || sourceEntry.SpecBinding is null ||
            sourceEntry.SpecBinding.CanonicalSyntax != "spec.IsEip4844Enabled" ||
            sourceEntry.StateRootWriteBinding is null || sourceEntry.StateRootValueBinding is null ||
            sourceEntry.AccountChangesWriteBinding is null || sourceEntry.AccountChangesValueBinding is null ||
            sourceEntry.ConditionalSignal is null || sourceEntry.ConditionalSignal.Evaluation is null ||
            sourceEntry.HeaderBinding is null || sourceEntry.HeaderValueBinding is null ||
            sourceEntry.TransactionsExecutedBinding is null ||
            sourceEntry.TransactionsExecutedBinding.NodeKind != "InvocationExpression" ||
            sourceEntry.TransactionsExecutedBinding.OperationKind != "Invocation" ||
            sourceEntry.TransactionsExecutedBinding.SymbolKind != "Method" ||
            sourceEntry.TransactionsExecutedBinding.SymbolId != TransactionsExecutedInvokeSymbol ||
            sourceEntry.TransactionsExecutedBinding.CanonicalSyntax != "TransactionsExecuted?.Invoke()" ||
            sourceEntry.TransactionsExecutedBinding.OperationType != "void" ||
            sourceEntry.PostTransactionCommitBinding is null ||
            !SameBindingIdentity(sourceEntry.PostTransactionCommitBinding,
                Anchor(document.Anchors, "block.post-transaction-commit").Binding))
        {
            throw new ExtractionException("The typed source-entry identity is incomplete.");
        }

        TaskFlowIdentity taskFlow = document.TaskFlow;
        if (taskFlow.Variable != TaskVariable || taskFlow.Initializer != TaskInitializer ||
            taskFlow.SynchronousArm != TaskSynchronousArm || taskFlow.EndTracePredicate != TaskEndTracePredicate ||
            taskFlow.BackgroundResultPredicate != TaskBackgroundResultPredicate || taskFlow.FinallyPredicate != TaskFinallyPredicate ||
            taskFlow.BackgroundAssignments != 1 || taskFlow.SynchronousAssignments != 0 ||
            !taskFlow.NullPreservedInSynchronousArm || !taskFlow.BackgroundResultExcluded ||
            !taskFlow.FinallyObservationExcluded || taskFlow.InitializerBinding is null ||
            taskFlow.EndTraceBinding is null || taskFlow.BackgroundResultBinding is null || taskFlow.FinallyBinding is null)
        {
            throw new ExtractionException("The synchronous null-task reaching-definition evidence changed.");
        }

        if (taskFlow.LocalSymbol != TaskLocalSymbol)
        {
            throw new ExtractionException("The task-flow bindings do not use one exact ILocalSymbol.");
        }

        if (document.Bridge.FoldPackage != "SequentialBlockTransactionFoldExtractor" ||
            document.Bridge.FoldArtifact != FoldGeneratedPath ||
            !IsSha256(document.Bridge.FoldArtifactSha256) ||
            !IsSha256(document.Bridge.FoldRefinementSha256) ||
            document.Bridge.FoldRefinement != FoldRefinementPath ||
            document.Bridge.Relation != BridgeRelation ||
             !document.Bridge.RequiredTerminalFields.SequenceEqual(FoldTerminalFields, StringComparer.Ordinal) ||
             !document.Bridge.RequiredFoldPremises.SequenceEqual(FoldPremises, StringComparer.Ordinal))
        {
            throw new ExtractionException("The explicit fold projection bridge changed or became circular.");
        }

        foreach (TypedBinding binding in document.Members.Select(static member => member.Binding)
                     .Concat(document.Anchors.Select(static anchor => anchor.Binding))
                     .Concat(document.ControlFlows.Select(static flow => flow.Binding))
                     .Concat(document.Commits.SelectMany(static commit => new[] { commit.Binding, commit.InvocationBinding }))
                      .Concat(document.Guards.Select(static guard => guard.Binding))
                      .Concat(document.Steps.Select(static step => step.Binding))
                      .Concat(document.HeaderAssignments.SelectMany(static assignment =>
                          new[] { assignment.AssignmentBinding, assignment.ValueBinding, assignment.GuardBinding }
                              .Concat(assignment.ExcludedBindings)))
                      .Concat(document.SourceEntryAdapters.SelectMany(static adapter => new[]
                      {
                          adapter.EntryBinding, adapter.ExecutorBinding, adapter.ReceiptsBinding, adapter.BalBinding,
                         adapter.BackgroundBinding, adapter.MainThreadBinding, adapter.StateRootBinding,
                         adapter.SpecBinding, adapter.StateRootWriteBinding, adapter.StateRootValueBinding,
                         adapter.AccountChangesWriteBinding, adapter.AccountChangesValueBinding,
                         adapter.TransactionsExecutedBinding, adapter.PostTransactionCommitBinding, adapter.ConditionalSignal.Evaluation,
                         adapter.HeaderBinding, adapter.HeaderValueBinding,
                     }))
                     .Concat(new[]
                     {
                         document.TaskFlow.InitializerBinding, document.TaskFlow.EndTraceBinding,
                         document.TaskFlow.BackgroundResultBinding, document.TaskFlow.FinallyBinding,
                     }))
        {
            ValidateBinding(binding);
        }

        ValidateTypedBindingFlowConsistency(document);
        ValidateExactBindingMappings(document);
        ValidateSerializedTailDominance(document);
    }

    internal static void ValidateSerializedTailDominance(IrDocument document)
    {
        int[] anchorPositions = document.Anchors.Select(static anchor => anchor.Binding.Position).ToArray();
        if (!IsStrictlySortedDistinct(anchorPositions))
        {
            throw new ExtractionException("The serialized finalization anchors are not source ordered.");
        }

        ControlFlowIdentity flow = document.ControlFlows.Single(item => item.Id == "block.processBlock");
        Dictionary<int, HashSet<int>> dominators = ComputeSerializedDominators(flow);
        int returnBlock = Anchor(document.Anchors, "block.return-receipts").Binding.ControlFlowBlock;
        if (!dominators.TryGetValue(returnBlock, out HashSet<int>? returnDominators))
        {
            throw new ExtractionException("The serialized ProcessBlock CFG has no normal-return dominator set.");
        }

        foreach (string anchorId in UnconditionalTailAnchorIds)
        {
            int actionBlock = Anchor(document.Anchors, anchorId).Binding.ControlFlowBlock;
            if (!returnDominators.Contains(actionBlock))
            {
                throw new ExtractionException($"Serialized normal-tail action '{anchorId}' does not dominate the receipts return.");
            }
        }

        SourceEntryAdapterIdentity adapter = document.SourceEntryAdapters.Single();
        ValidateTransactionsExecuted(adapter.ConditionalSignal, adapter.TransactionsExecutedBinding, flow);
        int signalBlock = adapter.ConditionalSignal.Evaluation.ControlFlowBlock;
        int postTransactionCommitBlock = document.SourceEntryAdapters.Single().PostTransactionCommitBinding.ControlFlowBlock;
        if (!dominators.TryGetValue(postTransactionCommitBlock, out HashSet<int>? commitDominators) ||
            !commitDominators.Contains(signalBlock))
        {
            throw new ExtractionException("Serialized TransactionsExecuted binding does not dominate the post-transaction CommitState(spec).");
        }
        if (!returnDominators.Contains(adapter.HeaderBinding.ControlFlowBlock) ||
            !commitDominators.Contains(adapter.HeaderBinding.ControlFlowBlock))
            throw new ExtractionException("The exact header initialization must dominate the normal finalization boundary.");
    }

    private static Dictionary<int, HashSet<int>> ComputeSerializedDominators(ControlFlowIdentity flow) =>
        ComputeEntryDominators(flow.ReachableBlocks[0], flow.ReachableBlocks, flow.Edges);

    private static void ValidateHeaderAssignments(IrDocument document)
    {
        if (document.HeaderAssignments.Length != HeaderAssignmentIds.Length ||
            !document.HeaderAssignments.Select(static assignment => assignment.Id)
                .SequenceEqual(HeaderAssignmentIds, StringComparer.Ordinal) ||
            !document.HeaderAssignments.Select(static assignment => assignment.AnchorId)
                .SequenceEqual(HeaderAssignmentAnchors, StringComparer.Ordinal) ||
            !document.HeaderAssignments.Select(static assignment => assignment.GuardAnchorId)
                .SequenceEqual(HeaderAssignmentGuardAnchors, StringComparer.Ordinal) ||
            !document.HeaderAssignments.Select(static assignment => assignment.SelectedArm)
                .SequenceEqual(HeaderAssignmentArms, StringComparer.Ordinal) ||
            !document.HeaderAssignments.Select(static assignment => assignment.TargetCanonical)
                .SequenceEqual(HeaderAssignmentTargets, StringComparer.Ordinal) ||
            !document.HeaderAssignments.Select(static assignment => assignment.ValueCanonical)
                .SequenceEqual(HeaderAssignmentValues, StringComparer.Ordinal) ||
            !document.HeaderAssignments[0].ExcludedWriteCanonicals.SequenceEqual(Array.Empty<string>(), StringComparer.Ordinal) ||
            !document.HeaderAssignments[1].ExcludedWriteCanonicals.SequenceEqual(new[] { BackgroundHeaderAssignment }, StringComparer.Ordinal) ||
            document.HeaderAssignments.Any(static assignment => assignment is null ||
                assignment.AssignmentBinding is null || assignment.ValueBinding is null || assignment.GuardBinding is null ||
                assignment.ExcludedWriteCanonicals is null || assignment.ExcludedBindings is null))
        {
            throw new ExtractionException("The typed header assignment set or selected arms changed.");
        }

        HeaderAssignmentIdentity blobGas = document.HeaderAssignments[0];
        HeaderAssignmentIdentity receiptsRoot = document.HeaderAssignments[1];
        if (blobGas.ExcludedWriteCanonicals.Length != 0 || receiptsRoot.ExcludedWriteCanonicals.Length != 1 ||
            receiptsRoot.ExcludedWriteCanonicals[0] != BackgroundHeaderAssignment ||
            blobGas.ExcludedBindings.Length != 0 || receiptsRoot.ExcludedBindings.Length != 1 ||
            receiptsRoot.ExcludedBindings[0] is null ||
            !SameBindingIdentity(blobGas.GuardBinding, Anchor(document.Anchors, HeaderAssignmentGuardAnchors[0]).Binding) ||
            !SameBindingIdentity(receiptsRoot.GuardBinding, Anchor(document.Anchors, HeaderAssignmentGuardAnchors[1]).Binding))
        {
            throw new ExtractionException("The typed header assignment guard or excluded-write mapping changed.");
        }

        ValidateHeaderAssignment(blobGas, BlobGasPropertySymbol, BlobGasValueSymbol, "ulong", expectedExcluded: null);
        ValidateHeaderAssignment(receiptsRoot, ReceiptsRootPropertySymbol, ReceiptsRootValueSymbol,
            ReceiptsRootOperationType, BackgroundHeaderAssignment);
    }

    private static void ValidateHeaderAssignment(
        HeaderAssignmentIdentity assignment,
        string targetSymbol,
        string valueSymbol,
        string valueType,
        string? expectedExcluded)
    {
        if (assignment.AssignmentBinding.NodeKind != "SimpleAssignmentExpression" ||
            assignment.AssignmentBinding.OperationKind != "SimpleAssignment" ||
            assignment.AssignmentBinding.SymbolKind != "Property" ||
            assignment.AssignmentBinding.SymbolId != targetSymbol ||
            assignment.AssignmentBinding.OperationType != (targetSymbol == BlobGasPropertySymbol ? "ulong?" : valueType) ||
            assignment.AssignmentBinding.CanonicalSyntax !=
                $"{assignment.TargetCanonical}={assignment.ValueCanonical}" ||
            assignment.ValueBinding.NodeKind != "InvocationExpression" ||
            assignment.ValueBinding.OperationKind != "Invocation" ||
            assignment.ValueBinding.SymbolKind != "Method" ||
            assignment.ValueBinding.SymbolId != valueSymbol ||
            assignment.ValueBinding.OperationType != valueType ||
            assignment.AssignmentBinding.ControlFlowBlock != assignment.ValueBinding.ControlFlowBlock ||
            assignment.AssignmentBinding.Position >= assignment.ValueBinding.Position ||
            (expectedExcluded is null && (assignment.ExcludedWriteCanonicals.Length != 0 || assignment.ExcludedBindings.Length != 0)) ||
            (expectedExcluded is not null && (assignment.ExcludedWriteCanonicals.Length != 1 ||
                assignment.ExcludedWriteCanonicals[0] != expectedExcluded || assignment.ExcludedBindings.Length != 1 ||
                assignment.ExcludedBindings[0].NodeKind != "SimpleMemberAccessExpression" ||
                assignment.ExcludedBindings[0].OperationKind != "PropertyReference" ||
                assignment.ExcludedBindings[0].SymbolKind != "Property" ||
                assignment.ExcludedBindings[0].SymbolId != targetSymbol ||
                assignment.ExcludedBindings[0].OperationType != valueType ||
                assignment.ExcludedBindings[0].CanonicalSyntax != assignment.TargetCanonical)))
        {
            throw new ExtractionException($"Header assignment '{assignment.Id}' is not the exact typed selected-arm write.");
        }
    }

    private static void ValidateTypedBindingFlowConsistency(IrDocument document)
    {
        IEnumerable<(string Name, TypedBinding Binding)> bindings =
            document.Members.SelectMany(static member => new[] { ($"member:{member.Id}", member.Binding) })
                .Concat(document.Anchors.SelectMany(static anchor => new[] { ($"anchor:{anchor.Id}", anchor.Binding) }))
                .Concat(document.ControlFlows.SelectMany(static flow => new[] { ($"flow:{flow.Id}", flow.Binding) }))
                .Concat(document.Commits.SelectMany(static commit => new[]
                {
                    ($"commit:{commit.Id}:implementation", commit.Binding),
                    ($"commit:{commit.Id}:invocation", commit.InvocationBinding),
                }))
                .Concat(document.Guards.SelectMany(static guard => new[] { ($"guard:{guard.Id}", guard.Binding) }))
                .Concat(document.Steps.SelectMany(static step => new[] { ($"step:{step.Id}", step.Binding) }))
                .Concat(document.HeaderAssignments.SelectMany(static assignment => new[]
                {
                    ($"header-assignment:{assignment.Id}:assignment", assignment.AssignmentBinding),
                    ($"header-assignment:{assignment.Id}:value", assignment.ValueBinding),
                    ($"header-assignment:{assignment.Id}:guard", assignment.GuardBinding),
                }.Concat(assignment.ExcludedBindings.Select(binding =>
                    ($"header-assignment:{assignment.Id}:excluded", binding)))))
                .Concat(document.SourceEntryAdapters.SelectMany(static adapter => new[]
                {
                    ($"source-entry:{adapter.Id}:entry", adapter.EntryBinding),
                    ($"source-entry:{adapter.Id}:executor", adapter.ExecutorBinding),
                    ($"source-entry:{adapter.Id}:receipts", adapter.ReceiptsBinding),
                    ($"source-entry:{adapter.Id}:bal", adapter.BalBinding),
                    ($"source-entry:{adapter.Id}:background", adapter.BackgroundBinding),
                     ($"source-entry:{adapter.Id}:main-thread", adapter.MainThreadBinding),
                     ($"source-entry:{adapter.Id}:state-root", adapter.StateRootBinding),
                     ($"source-entry:{adapter.Id}:spec", adapter.SpecBinding),
                     ($"source-entry:{adapter.Id}:state-root-write", adapter.StateRootWriteBinding),
                     ($"source-entry:{adapter.Id}:state-root-value", adapter.StateRootValueBinding),
                     ($"source-entry:{adapter.Id}:account-changes-write", adapter.AccountChangesWriteBinding),
                     ($"source-entry:{adapter.Id}:account-changes-value", adapter.AccountChangesValueBinding),
                     ($"source-entry:{adapter.Id}:transactions-executed", adapter.TransactionsExecutedBinding),
                     ($"source-entry:{adapter.Id}:event-evaluation", adapter.ConditionalSignal.Evaluation),
                     ($"source-entry:{adapter.Id}:header-local", adapter.HeaderBinding),
                     ($"source-entry:{adapter.Id}:header-source", adapter.HeaderValueBinding),
                     ($"source-entry:{adapter.Id}:post-transaction-commit", adapter.PostTransactionCommitBinding),
                 }))
                .Concat(new[]
                {
                    ("task-flow:initializer", document.TaskFlow.InitializerBinding),
                    ("task-flow:end-trace", document.TaskFlow.EndTraceBinding),
                    ("task-flow:background-result", document.TaskFlow.BackgroundResultBinding),
                    ("task-flow:finally", document.TaskFlow.FinallyBinding),
                });

        foreach ((string name, TypedBinding binding) in bindings)
        {
            if (binding.NodeKind == "MethodDeclaration")
            {
                if (document.Members.Count(member => SameBindingIdentity(member.Binding, binding)) != 1)
                    throw new ExtractionException($"Method binding '{name}' does not match its exact admitted member.");
                continue;
            }

            ControlFlowIdentity[] matches = document.ControlFlows
                .Where(flow => flow.Path == binding.Path && flow.Owner == binding.Owner && flow.Member == binding.Member)
                .ToArray();
            if (matches.Length != 1)
            {
                throw new ExtractionException($"Non-method binding '{name}' does not resolve to exactly one owning CFG flow.");
            }

            ControlFlowIdentity flow = matches[0];
            bool memberOfReachableSet = flow.ReachableBlocks.Contains(binding.ControlFlowBlock);
            int[] positionBlocks = flow.BlockMemberships
                .Where(membership => membership.SyntaxStarts.Contains(binding.Position))
                .Select(static membership => membership.Ordinal)
                .ToArray();
            if (binding.ContainingMember != flow.Member || !memberOfReachableSet || binding.IsReachable != memberOfReachableSet ||
                positionBlocks.Length != 1 || positionBlocks[0] != binding.ControlFlowBlock)
            {
                throw new ExtractionException($"Binding '{name}' is not consistent with its owning CFG block membership.");
            }
        }
    }

    private static void ValidateExactBindingMappings(IrDocument document)
    {
        string[] guardAnchorIds =
        [
            "block.blob-gas-guard", "block.receipts-background-guard", "block.main-thread-guard",
            "block.state-root-guard", "block.background-result-guard", "block.background-finally-guard",
        ];
        foreach ((GuardIdentity guard, string anchorId) in document.Guards.Zip(guardAnchorIds))
        {
            if (!SameBindingIdentity(guard.Binding, Anchor(document.Anchors, anchorId).Binding))
            {
                throw new ExtractionException($"Guard '{guard.Id}' is redirected from its exact source anchor.");
            }
        }

        foreach ((StepIdentity step, string anchorId) in document.Steps.Zip(StepAnchorIds))
        {
            if (!SameBindingIdentity(step.Binding, Anchor(document.Anchors, anchorId).Binding))
            {
                throw new ExtractionException($"Step '{step.Id}' is redirected from its exact source anchor.");
            }
        }

        foreach ((HeaderAssignmentIdentity assignment, string anchorId, string guardAnchorId) in
                 document.HeaderAssignments.Zip(HeaderAssignmentAnchors, HeaderAssignmentGuardAnchors))
        {
            AnchorIdentity valueAnchor = Anchor(document.Anchors, anchorId);
            AnchorIdentity guardAnchor = Anchor(document.Anchors, guardAnchorId);
            if (!SameBindingIdentity(assignment.ValueBinding, valueAnchor.Binding) ||
                !SameBindingIdentity(assignment.GuardBinding, guardAnchor.Binding) ||
                assignment.AssignmentBinding.Path != valueAnchor.Binding.Path ||
                assignment.AssignmentBinding.Owner != valueAnchor.Binding.Owner ||
                 assignment.AssignmentBinding.Member != valueAnchor.Binding.Member ||
                 assignment.AssignmentBinding.ContainingMember != "ProcessBlock" ||
                 assignment.AssignmentBinding.ControlFlowBlock != assignment.ValueBinding.ControlFlowBlock ||
                 assignment.AssignmentBinding.ControlFlowBlock < 0 ||
                 assignment.AssignmentBinding.Position >= assignment.ValueBinding.Position ||
                 assignment.GuardBinding.Position >= assignment.AssignmentBinding.Position ||
                 assignment.ExcludedBindings.Any(binding => binding.Path != valueAnchor.Binding.Path ||
                     binding.Owner != valueAnchor.Binding.Owner || binding.Member != valueAnchor.Binding.Member ||
                     binding.ContainingMember != "ProcessBlock" || binding.ControlFlowBlock < 0))
            {
                throw new ExtractionException($"Header assignment '{assignment.Id}' is redirected from its exact source arm.");
            }
        }

        SourceEntryAdapterIdentity adapter = document.SourceEntryAdapters.Single();
        TypedBinding header = adapter.HeaderBinding;
        TypedBinding headerValue = adapter.HeaderValueBinding;
        if (header.Path != BlockProcessorPath || header.Member != "ProcessBlock" || header.Owner != "BlockProcessor" ||
            header.ContainingMember != "ProcessBlock" || header.NodeKind != "VariableDeclarator" ||
            header.OperationKind != "VariableDeclarator" || header.SymbolKind != "Local" || header.SymbolId != HeaderLocalSymbol ||
            header.CanonicalSyntax != HeaderInitializer || header.OperationType != "Nethermind.Core.BlockHeader" ||
            headerValue.Path != header.Path || headerValue.Member != header.Member || headerValue.Owner != header.Owner ||
            headerValue.ContainingMember != header.ContainingMember || headerValue.ControlFlowBlock != header.ControlFlowBlock ||
            headerValue.SymbolId != HeaderValueSymbol || headerValue.CanonicalSyntax != "block.Header" ||
            headerValue.SymbolKind != "Property" || headerValue.OperationKind != "PropertyReference" ||
            headerValue.NodeKind != "SimpleMemberAccessExpression" || headerValue.OperationType != header.OperationType ||
            header.Position >= headerValue.Position || headerValue.Position >= adapter.ReceiptsBinding.Position ||
            !adapter.PreservedValues[1].ReadPositions.Contains(headerValue.Position) ||
            adapter.PreservedValues[0].ReadPositions.Any(position => position <= headerValue.Position))
            throw new ExtractionException("The preserved header initialization/source binding was redirected.");
        if (!SameBindingIdentity(adapter.EntryBinding, document.Members.Single(member => member.Id == "block.processBlock").Binding) ||
            adapter.ExecutorBinding.Path != BlockProcessorPath || adapter.ExecutorBinding.Owner != "BlockProcessor" ||
            adapter.ExecutorBinding.Member != "ProcessBlock" || adapter.ExecutorBinding.NodeKind != "InvocationExpression" ||
            adapter.ExecutorBinding.SymbolId !=
                "global::Nethermind.Consensus.Processing.IBlockProcessor.IBlockTransactionsExecutor.ProcessTransactions(Nethermind.Core.Block,Nethermind.Consensus.Processing.ProcessingOptions,Nethermind.Blockchain.Tracing.BlockReceiptsTracer,System.Threading.CancellationToken)" ||
            adapter.ExecutorBinding.SymbolKind != "Method" || adapter.ExecutorBinding.OperationKind != "Invocation" ||
             adapter.ExecutorBinding.OperationType != ReceiptsOperationType ||
            adapter.ExecutorBinding.ReceiverType != "Nethermind.Consensus.Processing.IBlockProcessor.IBlockTransactionsExecutor" ||
            !adapter.ExecutorBinding.ReadInside.SequenceEqual(adapter.EntryBinding.ReadInside, StringComparer.Ordinal) ||
            !adapter.ExecutorBinding.WrittenInside.SequenceEqual(adapter.EntryBinding.WrittenInside, StringComparer.Ordinal) ||
            adapter.ReceiptsReferenceCount != ReceiptsReferenceCount || adapter.ReceiptsWriteCount != ReceiptsWriteCount ||
            adapter.ReceiptsBinding.Path != BlockProcessorPath || adapter.ReceiptsBinding.Owner != "BlockProcessor" ||
             adapter.ReceiptsBinding.Member != "ProcessBlock" || adapter.ReceiptsBinding.ContainingMember != "ProcessBlock" ||
             adapter.ReceiptsBinding.SymbolId != ReceiptsLocalSymbol ||
             adapter.ReceiptsBinding.OperationType != ReceiptsOperationType ||
             adapter.ReceiptsBinding.Position >= adapter.ExecutorBinding.Position ||
             adapter.ReceiptsBinding.CanonicalSyntax != ReceiptsInitializerSyntax ||
            !SameBindingIdentity(adapter.BalBinding, Anchor(document.Anchors, "block.bal-finalization").Binding) ||
            !SameBindingIdentity(adapter.BackgroundBinding, Anchor(document.Anchors, "block.receipts-background-guard").Binding) ||
            !SameBindingIdentity(adapter.MainThreadBinding, Anchor(document.Anchors, "block.main-thread-guard").Binding) ||
            !SameBindingIdentity(adapter.StateRootBinding, Anchor(document.Anchors, "block.state-root-guard").Binding) ||
            !SameBindingIdentity(adapter.SpecBinding, Anchor(document.Anchors, "block.blob-gas-guard").Binding) ||
            !SameBindingIdentity(adapter.PostTransactionCommitBinding,
                Anchor(document.Anchors, "block.post-transaction-commit").Binding) ||
            adapter.TransactionsExecutedBinding.Path != BlockProcessorPath ||
            adapter.TransactionsExecutedBinding.Owner != "BlockProcessor" ||
            adapter.TransactionsExecutedBinding.Member != "ProcessBlock" ||
            adapter.TransactionsExecutedBinding.ContainingMember != "ProcessBlock" ||
             adapter.TransactionsExecutedBinding.NodeKind != "InvocationExpression" ||
             adapter.TransactionsExecutedBinding.OperationKind != "Invocation" ||
             adapter.TransactionsExecutedBinding.SymbolKind != "Method" ||
             adapter.TransactionsExecutedBinding.SymbolId != TransactionsExecutedInvokeSymbol ||
             adapter.TransactionsExecutedBinding.CanonicalSyntax != "TransactionsExecuted?.Invoke()" ||
            adapter.TransactionsExecutedBinding.OperationType != "void")
        {
            throw new ExtractionException("The source-entry adapter typed bindings are redirected.");
        }

        ValidateHelperWriteBinding(adapter.StateRootWriteBinding, adapter.StateRootValueBinding,
            "ComputeStateRoot", StateRootPropertySymbol, StateRootValueSymbol,
            "header.StateRoot=_stateProvider.StateRoot", "_stateProvider.StateRoot");
        ValidateHelperWriteBinding(adapter.AccountChangesWriteBinding, adapter.AccountChangesValueBinding,
            "SetAccountChanges", AccountChangesPropertySymbol, AccountChangesValueSymbol,
            "block.AccountChanges=_stateProvider.GetAccountChanges()", "_stateProvider.GetAccountChanges()");

        TaskFlowIdentity task = document.TaskFlow;
        if (!SameBindingIdentity(task.InitializerBinding, Anchor(document.Anchors, "block.background-task-null").Binding) ||
            !SameBindingIdentity(task.EndTraceBinding, Anchor(document.Anchors, "block.end-block-trace").Binding) ||
            !SameBindingIdentity(task.BackgroundResultBinding, Anchor(document.Anchors, "block.background-result-guard").Binding) ||
            !SameBindingIdentity(task.FinallyBinding, Anchor(document.Anchors, "block.background-finally-guard").Binding))
        {
            throw new ExtractionException("The task-flow typed bindings are redirected.");
        }

        string[] commitAnchorIds =
            ["block.post-transaction-commit", "block.finalization-commit", "block.storage-roots-commit"];
        string[] commitMethodIds = ["block.commit-no-roots", "block.commit-no-roots", "block.commit-roots"];
        if (!document.Commits.Select(static commit => commit.InvocationAnchorId).SequenceEqual(commitAnchorIds, StringComparer.Ordinal) ||
            !document.Commits.Select(static commit => commit.MethodId).SequenceEqual(commitMethodIds, StringComparer.Ordinal) ||
            !document.Commits.Select(static commit => commit.Event).SequenceEqual(CommitEventIds, StringComparer.Ordinal) ||
            document.Commits.Zip(commitAnchorIds).Any(pair =>
                !SameBindingIdentity(pair.First.InvocationBinding, Anchor(document.Anchors, pair.Second).Binding)))
        {
            throw new ExtractionException("The ordered commit anchor or method mapping is redirected.");
        }
    }

    private static void ValidateHelperWriteBinding(
        TypedBinding write,
        TypedBinding value,
        string member,
        string propertySymbol,
        string valueSymbol,
        string writeCanonical,
        string valueCanonical)
    {
        bool stateRootValue = valueSymbol == StateRootValueSymbol;
        if (write.Path != BlockProcessorPath || write.Owner != "BlockProcessor" || write.Member != member ||
            write.ContainingMember != member || write.NodeKind != "SimpleAssignmentExpression" ||
            write.OperationKind != "SimpleAssignment" || write.SymbolKind != "Property" ||
            write.SymbolId != propertySymbol || write.CanonicalSyntax != writeCanonical ||
            !write.IsReachable || value.Path != BlockProcessorPath || value.Owner != "BlockProcessor" ||
            value.Member != member || value.ContainingMember != member || value.NodeKind !=
                (stateRootValue ? "SimpleMemberAccessExpression" : "InvocationExpression") ||
            value.OperationKind != (stateRootValue ? "PropertyReference" : "Invocation") ||
            value.SymbolKind != (stateRootValue ? "Property" : "Method") || value.SymbolId != valueSymbol ||
            value.CanonicalSyntax != valueCanonical || !value.IsReachable ||
            write.ControlFlowBlock != value.ControlFlowBlock || write.Position >= value.Position ||
            write.OperationType != value.OperationType)
        {
            throw new ExtractionException(
                $"The {member} helper body is not bound to the exact typed '{writeCanonical}' assignment and value.");
        }
    }

    private static bool SameBindingIdentity(TypedBinding left, TypedBinding right) =>
        left.Path == right.Path && left.Owner == right.Owner && left.Member == right.Member &&
        left.NodeKind == right.NodeKind && left.CanonicalSyntax == right.CanonicalSyntax &&
        left.SyntaxSha256 == right.SyntaxSha256 && left.SymbolId == right.SymbolId &&
        left.SymbolKind == right.SymbolKind && left.OperationKind == right.OperationKind &&
        left.OperationType == right.OperationType && left.ReceiverType == right.ReceiverType &&
        left.ContainingMember == right.ContainingMember && left.Position == right.Position &&
        left.StartLine == right.StartLine && left.StartColumn == right.StartColumn &&
        left.ControlFlowBlock == right.ControlFlowBlock && left.IsReachable == right.IsReachable &&
        left.HasCandidateSymbols == right.HasCandidateSymbols && left.CandidateReason == right.CandidateReason &&
        left.IsErrorSymbol == right.IsErrorSymbol && left.DataFlowSucceeded == right.DataFlowSucceeded &&
        left.ReadInside.SequenceEqual(right.ReadInside, StringComparer.Ordinal) &&
        left.WrittenInside.SequenceEqual(right.WrittenInside, StringComparer.Ordinal);

    private static void ValidateAnchorSyntax(IReadOnlyList<AnchorIdentity> anchors)
    {
        static string Syntax(IReadOnlyList<AnchorIdentity> source, string id) => source.Single(anchor => anchor.Id == id).CanonicalSyntax;
        if (Syntax(anchors, "block.post-transaction-commit") != "CommitState(spec)" ||
            Syntax(anchors, "block.blob-gas-guard") != "spec.IsEip4844Enabled" ||
            Syntax(anchors, "block.blob-gas-calculation") != "BlobGasCalculator.CalculateBlobGas(block.Transactions)" ||
            Syntax(anchors, "block.background-task-null") != "bloomsAndReceiptsRootTask=null" ||
            Syntax(anchors, "block.receipts-background-guard") != "ShouldCalculateReceiptsInBackground(receipts)" ||
            Syntax(anchors, "block.sync-blooms") != "CalculateBlooms(receipts)" ||
            Syntax(anchors, "block.sync-receipts-root") != "CalculateReceiptsRoot(receipts,spec,block)" ||
            Syntax(anchors, "block.rewards") != "ApplyMinerRewards(block,blockTracer,spec)" ||
            Syntax(anchors, "block.withdrawals") != "_systemContractHandler.ProcessWithdrawals(block,spec)" ||
            Syntax(anchors, "block.finalization-commit") != "CommitState(spec)" ||
            Syntax(anchors, "block.execution-requests") != "_systemContractHandler.ProcessExecutionRequests(block,_stateProvider,receipts,spec)" ||
            Syntax(anchors, "block.end-block-trace") != "ReceiptsTracer.EndBlockTrace(accumulateBlockBloom:bloomsAndReceiptsRootTaskisnull)" ||
            Syntax(anchors, "block.storage-roots-commit") != "CommitStateAndStorageRoots(spec)" ||
            Syntax(anchors, "block.main-thread-guard") != "BlockchainProcessor.IsMainProcessingThread" ||
            Syntax(anchors, "block.account-changes") != "SetAccountChanges(block)" ||
            Syntax(anchors, "block.state-root-guard") != "ShouldComputeStateRoot(header)" ||
            Syntax(anchors, "block.state-root") != "ComputeStateRoot(header)" ||
             Syntax(anchors, "block.background-result-guard") != TaskBackgroundResultPredicate ||
             Syntax(anchors, "block.bal-finalization") != "_balManager.SetBlockAccessList(block)" ||
             Syntax(anchors, "block.background-finally-guard") != TaskFinallyPredicate ||
            Syntax(anchors, "block.hash") != "header.Hash=header.CalculateHash()" ||
            Syntax(anchors, "block.return-receipts") != "returnreceipts;")
        {
            throw new ExtractionException("A finalization anchor was rebound, inverted, or argument-swapped.");
        }
    }

    private static void ValidateBinding(TypedBinding binding)
    {
        if (binding is null || string.IsNullOrWhiteSpace(binding.Path) || string.IsNullOrWhiteSpace(binding.Owner) ||
            string.IsNullOrWhiteSpace(binding.Member) || string.IsNullOrWhiteSpace(binding.NodeKind) ||
            string.IsNullOrWhiteSpace(binding.CanonicalSyntax) || !IsSha256(binding.SyntaxSha256) ||
            binding.SyntaxSha256 != Sha256(Encoding.UTF8.GetBytes(binding.CanonicalSyntax)) ||
            string.IsNullOrWhiteSpace(binding.SymbolId) || string.IsNullOrWhiteSpace(binding.SymbolKind) ||
            string.IsNullOrWhiteSpace(binding.OperationKind) || string.IsNullOrWhiteSpace(binding.OperationType) ||
            binding.HasCandidateSymbols || binding.IsErrorSymbol || !binding.DataFlowSucceeded || binding.Position <= 0 ||
            binding.StartLine <= 0 || binding.StartColumn <= 0 || binding.ControlFlowBlock < -1 ||
            binding.ReadInside is null || binding.WrittenInside is null ||
            binding.ReadInside.Any(static name => string.IsNullOrWhiteSpace(name)) ||
            binding.WrittenInside.Any(static name => string.IsNullOrWhiteSpace(name)) ||
            !string.Equals(binding.CandidateReason, CandidateReason.None.ToString(), StringComparison.Ordinal))
        {
            throw new ExtractionException("A typed source binding is incomplete or unresolved.");
        }
    }

    private static void ValidateManifest(ArtifactManifest manifest, IrDocument document, string irHash, string leanHash)
    {
        if (manifest is null || manifest.Sources is null || manifest.CompilerClosure is null || manifest.Members is null ||
            manifest.Anchors is null || manifest.ControlFlows is null || manifest.Commits is null || manifest.Steps is null ||
            manifest.HeaderAssignments is null ||
            manifest.SourceEntryAdapters is null || manifest.TaskFlowVariable is null ||
            manifest.SchemaVersion != SchemaVersion || manifest.ExtractorVersion != ExtractorVersion ||
            manifest.Kernel != Kernel || !manifest.Sources.SequenceEqual(document.Sources) ||
            !CompilerClosureMatches(manifest.CompilerClosure, document.CompilerClosure) || !manifest.Members.SequenceEqual(document.Members.Select(static member => member.Id)) ||
            !manifest.Anchors.SequenceEqual(document.Anchors.Select(static anchor => anchor.Id)) ||
            !manifest.ControlFlows.SequenceEqual(document.ControlFlows.Select(static flow => flow.Id)) ||
            !manifest.Commits.SequenceEqual(document.Commits.Select(static commit => commit.Id)) ||
            !manifest.Steps.SequenceEqual(document.Steps.Select(static step => step.Id)) ||
            !manifest.HeaderAssignments.SequenceEqual(document.HeaderAssignments.Select(static assignment => assignment.Id)) ||
            !manifest.SourceEntryAdapters.SequenceEqual(document.SourceEntryAdapters.Select(static adapter => adapter.Id)) ||
            manifest.TaskFlowVariable != document.TaskFlow.Variable ||
            manifest.IrSha256 != irHash || manifest.LeanSha256 != leanHash)
        {
            throw new ExtractionException("The post-transaction finalization source manifest does not match the typed IR.");
        }
    }

    private static bool ContainsProofPlaceholder(string source) =>
        ArtifactSafety.ContainsProofToken(source, generated: true);

    private static bool BridgeMatches(BridgeIdentity actual, BridgeIdentity expected) =>
        actual.FoldPackage == expected.FoldPackage && actual.FoldArtifact == expected.FoldArtifact &&
        actual.FoldArtifactSha256 == expected.FoldArtifactSha256 && actual.FoldRefinement == expected.FoldRefinement &&
        actual.FoldRefinementSha256 == expected.FoldRefinementSha256 && actual.Relation == expected.Relation &&
        actual.RequiredTerminalFields.SequenceEqual(expected.RequiredTerminalFields, StringComparer.Ordinal) &&
        actual.RequiredFoldPremises.SequenceEqual(expected.RequiredFoldPremises, StringComparer.Ordinal);

    private static T Deserialize<T>(byte[] bytes)
    {
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(bytes);
            ArtifactSafety.ValidateJson(parsed.RootElement);
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
                ?? throw new ExtractionException("A serialized finalization artifact was null.");
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"A serialized finalization artifact is invalid: {exception.Message}");
        }
    }

    private static byte[] Serialize<T>(T value) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions) + "\n");

    private static void WriteNewArtifact(string path, byte[] bytes) => ArtifactSafety.AtomicWrite(path, bytes);

    private static SourceFile FindSource(SourceFile[] sources, string path) => sources.Single(source => source.RelativePath == path);

    private static string OwnerPath(SyntaxNode node) => string.Join(".", node.AncestorsAndSelf().OfType<ClassDeclarationSyntax>()
        .Reverse().Select(static type => type.Identifier.ValueText));

    private static string Canonical(SyntaxNode node)
    {
        if (node is InvocationExpressionSyntax { Parent: ConditionalAccessExpressionSyntax conditional } &&
            ReferenceEquals(conditional.WhenNotNull, node)) node = conditional;
        if (node is MethodDeclarationSyntax { Identifier.ValueText: "ProcessBlock", Body: not null } method)
        {
            string signature = string.Concat(method.DescendantTokens()
                .TakeWhile(static token => !token.IsKind(SyntaxKind.OpenBraceToken))
                .Select(static token => token.Text));
            return signature + "{...}";
        }

        return string.Concat(node.DescendantTokens().Select(static token => token.Text));
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string CompilerReferenceAggregate(IEnumerable<CompilerReferenceIdentity> references) =>
        Sha256(Encoding.UTF8.GetBytes(string.Join('\n', references.Select(static reference =>
            $"{reference.Path}\0{reference.AssemblyName}\0{reference.Sha256}\0{reference.Mvid}\0{reference.Selected}")) + "\n"));

    private static bool IsSha256(string? value) => value is not null && value.Length == 64 &&
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

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

        return symbol switch
        {
            IMethodSymbol method => HasErrorType(method.ReturnType) || method.Parameters.Any(parameter => HasErrorType(parameter.Type)) ||
                HasErrorSymbol(method.ContainingSymbol),
            IPropertySymbol property => HasErrorType(property.Type) || HasErrorSymbol(property.ContainingSymbol),
            IFieldSymbol field => HasErrorType(field.Type) || HasErrorSymbol(field.ContainingSymbol),
            _ => symbol.ContainingSymbol is not null && HasErrorSymbol(symbol.ContainingSymbol),
        };
    }

    private static bool HasErrorType(ITypeSymbol? type) => type is IErrorTypeSymbol || type?.TypeKind == TypeKind.Error;

    private sealed record PinnedEffectLedgerEntry(
        string Path,
        string Owner,
        string Target,
        string OperationKind,
        string Canonical,
        int Multiplicity)
    {
        internal string Key => string.Join("\0", Path, Owner, Target, OperationKind, Canonical);
    }

    private sealed record PinnedActivationLedgerEntry(
        string Path,
        string Owner,
        string Target,
        string OperationKind,
        string Canonical,
        int Multiplicity)
    {
        internal string Key => string.Join("\0", Path, Owner, Target, OperationKind, Canonical);
    }

    private sealed record SourceLocalMethod(
        IMethodSymbol Symbol,
        MethodDeclarationSyntax Syntax,
        SemanticModel Model,
        string Path);

    private sealed record ArtifactManifest(
        int SchemaVersion,
        string ExtractorVersion,
        string Kernel,
        SourceIdentity[] Sources,
        CompilerClosureIdentity CompilerClosure,
        string[] Members,
        string[] Anchors,
        string[] ControlFlows,
        string[] Commits,
        string[] Steps,
        string[] HeaderAssignments,
        string[] SourceEntryAdapters,
        string TaskFlowVariable,
        string IrSha256,
        string LeanSha256,
        PublicationDependencyIdentity[] Dependencies);
}
