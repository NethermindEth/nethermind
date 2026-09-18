// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.SynchronousBlockPipelineMachineExtractor;

/// <summary>
/// Source-backed route metadata for ordinary standard-mainnet block processing.
/// Every entry is an audit obligation; none is an executable production callback.
/// </summary>
internal static class ProductionRoute
{
    internal const string RouteName = "standard-mainnet-synchronous-block-pipeline";
    internal const string ConsensusProcessing = "src/Nethermind/Nethermind.Consensus/Processing";
    internal const string EvmProcessing = "src/Nethermind/Nethermind.Evm/TransactionProcessing";

    internal static readonly PhaseContract[] PhasePlan =
    [
        new(PipelinePhase.SuggestedBlockValidation, "suggested-block validation",
            [HookId.SuggestedBlockSimpleChecks]),
        new(PipelinePhase.SynchronousBranchSelectionAndPreparation, "synchronous branch selection and preparation",
            [HookId.EvaluateEligibility, HookId.SelectAndPrepareBranch]),
        new(PipelinePhase.SenderAndAuthorityRecovery, "sender and EIP-7702 authority recovery",
            [HookId.RecoverSignatures, HookId.RecoverSignatureImplementation, HookId.RecoverAuthorities]),
        new(PipelinePhase.OpenWorldStateScope, "open world-state scope at parent root",
            [HookId.BeginBranchScope, HookId.BeginGenesisScope]),
        new(PipelinePhase.DaoTransition, "DAO transition when applicable",
            [HookId.PrepareBal, HookId.SelectSystemContractHandler, HookId.ApplyDaoTransition, HookId.PrepareBlockForProcessing]),
        new(PipelinePhase.BeaconRootSystemCall, "beacon-root system call",
            [HookId.SetOtherTracer, HookId.StartBlockTrace, HookId.SetBlockExecutionContext, HookId.SetupBlockAccessList,
             HookId.StoreBeaconRoot]),
        new(PipelinePhase.HistoricalBlockhashStateChange, "historical blockhash state change",
            [HookId.ApplyBlockhashStateChanges, HookId.CommitPreTransactionState]),
        new(PipelinePhase.UserTransactionFold, "user transaction fold",
            [HookId.ExecuteTransactionFold, HookId.TransactionsExecutedSignal, HookId.CommitPostTransactionState,
             HookId.BuildReceiptsAndCumulativePaidGas]),
        new(PipelinePhase.BlobGasReceiptRootAndBloom, "blob gas and receipt root and bloom",
            [HookId.CalculateBlobGas, HookId.CalculateReceiptBlooms, HookId.AccumulateBlockBloom,
             HookId.CalculateReceiptsRoot, HookId.InstallSynchronousReceiptArtifacts, HookId.ScheduleBackgroundReceiptArtifacts,
             HookId.AwaitBackgroundReceiptArtifacts]),
        new(PipelinePhase.Rewards, "rewards",
            [HookId.ApplyMinerRewards]),
        new(PipelinePhase.Withdrawals, "withdrawals",
            [HookId.ProcessWithdrawals, HookId.CommitPostSystemState]),
        new(PipelinePhase.ExecutionRequestsAndSystemCalls, "execution requests and system calls",
            [HookId.ProcessExecutionRequests, HookId.EndBlockTrace]),
        new(PipelinePhase.StorageAndStateRoots, "storage and state roots",
            [HookId.CommitStorageAndStateRoots, HookId.SetAccountChanges, HookId.ComputeStateRoot]),
        new(PipelinePhase.BlockAccessList, "block access list",
            [HookId.FinalizeBal]),
        new(PipelinePhase.ProcessedHeaderValidation, "processed-header validation",
            [HookId.ValidateProcessedBlock, HookId.DisposeAccountChanges]),
        new(PipelinePhase.CommitTreeInvocation, "CommitTree invocation",
            [HookId.DisposeRetryScope, HookId.ReopenRetryScope, HookId.RecordInclusionListSignal, HookId.WaitPrewarm,
             HookId.CommitTree, HookId.IncrementSuccessfulPrefix, HookId.DisposeCheckpointScope, HookId.ReopenCheckpointScope,
             HookId.ResetScope, HookId.DisposeBranchScope, HookId.CatchBranchException, HookId.CompleteBranchProcessing]),
        new(PipelinePhase.SynchronousResultClassificationAndHeadFinalization,
            "synchronous result classification and head/processed-chain finalization",
            [HookId.ClassifySynchronousResult, HookId.UpdateTotalDifficulty, HookId.TryUpdateMainChain,
             HookId.MarkChainAsProcessed]),
    ];

    internal static readonly SourceSymbol[] SourceSymbols =
    [
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "Nethermind.Consensus.Processing", "BlockProcessor", "ProcessOne", 5, "one-block entry"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "Nethermind.Consensus.Processing", "BlockProcessor", "ValidateProcessedBlock", 4, "processed-header validation"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "Nethermind.Consensus.Processing", "BlockProcessor", "ProcessBlock", 5, "one-block phases"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "Nethermind.Consensus.Processing", "BlockProcessor", "CommitState", 1, "pre/post-transaction and post-system journal boundary"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "Nethermind.Consensus.Processing", "BlockProcessor", "CommitStateAndStorageRoots", 1, "storage/state root boundary"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "Nethermind.Consensus.Processing", "BlockProcessor", "CalculateBlooms", 1, "receipt mutation"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "Nethermind.Consensus.Processing", "BlockProcessor", "AccumulateBlockBloom", 1, "block bloom"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "Nethermind.Consensus.Processing", "BlockProcessor", "CalculateReceiptsRoot", 3, "receipt root"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "Nethermind.Consensus.Processing", "BlockProcessor", "ApplyDaoTransition", 1, "DAO transition"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "Nethermind.Consensus.Processing", "BlockProcessor", "PrepareBlockForProcessing", 1, "processing header clone"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "Nethermind.Consensus.Processing", "BlockProcessor", "ShouldCalculateReceiptsInBackground", 1, "receipt scheduling threshold"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.std.cs", "Nethermind.Consensus.Processing", "BlockProcessor", "ShouldCalculateReceiptsInBackground", 1, "standard receipt scheduling implementation"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "Nethermind.Consensus.Processing", "BlockProcessor", "SetAccountChanges", 1, "account-change observation"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "Nethermind.Consensus.Processing", "BlockProcessor", "ComputeStateRoot", 1, "state-root observation"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockValidationTransactionsExecutor.cs", "Nethermind.Consensus.Processing", "BlockValidationTransactionsExecutor", "ProcessTransactions", 4, "sequential transaction fold"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockValidationTransactionsExecutor.cs", "Nethermind.Consensus.Processing", "BlockValidationTransactionsExecutor", "ProcessTransaction", 5, "ordinary/system transaction adapter boundary"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.ParallelBlockValidationTransactionsExecutor.cs", "Nethermind.Consensus.Processing", "ParallelBlockValidationTransactionsExecutor", "ProcessTransactions", 4, "parallel attempt boundary"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.ParallelBlockValidationTransactionsExecutor.cs", "Nethermind.Consensus.Processing", "ParallelBlockValidationTransactionsExecutor", "ProcessTransactionsParallel", 4, "parallel attempt"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.ParallelBlockValidationTransactionsExecutor.cs", "Nethermind.Consensus.Processing", "ParallelBlockValidationTransactionsExecutor", "CombineReceipts", 2, "parallel receipt combination"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "Nethermind.Consensus.Processing", "BranchProcessor", "Process", 5, "branch loop and scope lifecycle"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "Nethermind.Consensus.Processing", "BranchProcessor", "PreCommitBlock", 1, "CommitTree boundary"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "Nethermind.Consensus.Processing", "BlockchainProcessor", "Process", 5, "outer chain route"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "Nethermind.Consensus.Processing", "BlockchainProcessor", "ProcessBranch", 5, "invalid-block boundary"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "Nethermind.Consensus.Processing", "BlockchainProcessor", "PrepareBlocksToProcess", 3, "branch preprocessing"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "Nethermind.Consensus.Processing", "BlockchainProcessor", "PrepareProcessingBranch", 2, "branch selection"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "Nethermind.Consensus.Processing", "BlockchainProcessor", "RunSimpleChecksAheadOfProcessing", 2, "suggested-block checks"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "Nethermind.Consensus.Processing", "BlockchainProcessor", "Preprocess", 1, "sender/authority preprocessing"),
        new("src/Nethermind/Nethermind.Consensus/Processing/RecoverSignatures.cs", "Nethermind.Consensus.Processing", "RecoverSignatures", "RecoverData", 1, "block sender/authority recovery"),
        new("src/Nethermind/Nethermind.Consensus/Processing/RecoverSignatures.cs", "Nethermind.Consensus.Processing", "RecoverSignatures", "RecoverData", 3, "transaction sender/authority recovery"),
        new("src/Nethermind/Nethermind.Consensus/Processing/TransactionProcessorAdapterExtensions.cs", "Nethermind.Consensus.Processing", "TransactionProcessorAdapterExtensions", "ProcessTransaction", 5, "per-transaction tracer/adapter boundary"),
        new("src/Nethermind/Nethermind.Evm/TransactionProcessing/ITransactionProcessor.cs", "Nethermind.Evm.TransactionProcessing", "ITransactionProcessorExtensions", null, null, "Execute-to-Process extension boundary"),
        new("src/Nethermind/Nethermind.Evm/TransactionProcessing/ExecuteTransactionProcessorAdapter.cs", "Nethermind.Evm.TransactionProcessing", "ExecuteTransactionProcessorAdapter", "Execute", 2, "concrete Execute adapter forwarding"),
        new("src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "Nethermind.Evm.TransactionProcessing", "TransactionProcessorBase", "Process", 3, "ordinary transaction entry"),
        new("src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "Nethermind.Evm.TransactionProcessing", "TransactionProcessorBase", "ExecuteCore", 3, "ordinary/system dispatch"),
        new("src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "Nethermind.Evm.TransactionProcessing", "TransactionProcessorBase", "Execute", 3, "ordinary transaction preparation"),
        new("src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "Nethermind.Evm.TransactionProcessing", "TransactionProcessorBase", "Execute", 6, "ordinary transaction validated dispatch"),
        new("src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "Nethermind.Evm.TransactionProcessing", "TransactionProcessorBase", "ExecuteEvmTransaction", 16, "ordinary EVM terminal path"),
        new("src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "Nethermind.Evm.TransactionProcessing", "TransactionProcessorBase", "ExecuteSimpleTransfer", 15, "simple-transfer terminal path"),
        new("src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "Nethermind.Evm.TransactionProcessing", "TransactionProcessorBase", "FinalizeTransaction", 12, "receipt finalization boundary"),
        new("src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionProcessor.cs", "Nethermind.Evm.TransactionProcessing", "SystemTransactionProcessor", "Execute", 3, "system transaction entry"),
        new("src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs", "Nethermind.Init.Modules", "BlockProcessingModule", "Load", 1, "standard DI registrations"),
        new("src/Nethermind/Nethermind.Init/Modules/MainProcessingContext.cs", "Nethermind.Init.Modules", "MainProcessingContext", ".ctor", 10, "main processing scope"),
        new("src/Nethermind/Nethermind.Init/Modules/NethermindModule.cs", "Nethermind.Init.Modules", "NethermindModule", "Load", 1, "root production registrations"),
        new("src/Nethermind/Nethermind.Specs/ChainSpecStyle/ChainSpecBasedSpecProvider.cs", "Nethermind.Specs.ChainSpecStyle", "ChainSpecBasedSpecProvider", ".ctor", 2, "runtime chain-spec provider"),
        new("src/Nethermind/Nethermind.Specs/ChainSpecStyle/ChainSpecBasedSpecProvider.cs", "Nethermind.Specs.ChainSpecStyle", "ChainSpecBasedSpecProvider", "BuildTransitions", 0, "runtime transition-set construction"),
        new("src/Nethermind/Nethermind.Specs/ChainSpecStyle/ChainSpecBasedSpecProvider.cs", "Nethermind.Specs.ChainSpecStyle", "ChainSpecBasedSpecProvider", "CreateReleaseSpec", 3, "runtime EIP activation mapping"),
        new("src/Nethermind/Nethermind.Specs/ChainSpecStyle/ChainSpecFileLoader.cs", "Nethermind.Specs.ChainSpecStyle", "ChainSpecFileLoader", "LoadEmbeddedOrFromFile", 1, "chain-spec file/resource derivation"),
        new("src/Nethermind/Nethermind.Specs/ChainSpecStyle/ChainSpecFileLoader.cs", "Nethermind.Specs.ChainSpecStyle", "ChainSpecFileLoader", "FileNameToResource", 1, "embedded resource-name derivation"),
        new("src/Nethermind/Nethermind.Specs/ChainSpecStyle/ChainSpecFileLoader.cs", "Nethermind.Specs.ChainSpecStyle", "ChainSpecFileLoader", "NormalizeFileName", 1, "chain-spec extension normalization"),
        new("src/Nethermind/Nethermind.Specs/ChainSpecStyle/AutoDetectingChainSpecLoader.cs", "Nethermind.Specs.ChainSpecStyle", "AutoDetectingChainSpecLoader", "Load", 1, "chain-spec format-selection entry"),
        new("src/Nethermind/Nethermind.Specs/ChainSpecStyle/AutoDetectingChainSpecLoader.cs", "Nethermind.Specs.ChainSpecStyle", "AutoDetectingChainSpecLoader", "LoadSeekable", 1, "seekable chain-spec format-selection path"),
        new("src/Nethermind/Nethermind.Specs/ChainSpecStyle/AutoDetectingChainSpecLoader.cs", "Nethermind.Specs.ChainSpecStyle", "AutoDetectingChainSpecLoader", "DetectFormat", 1, "chain-spec format detection"),
        new("src/Nethermind/Nethermind.Specs/ChainSpecStyle/AutoDetectingChainSpecLoader.cs", "Nethermind.Specs.ChainSpecStyle", "AutoDetectingChainSpecLoader", "LoadDetected", 2, "Parity/Geth loader selection"),
        new("src/Nethermind/Nethermind.Specs/ChainSpecStyle/ChainSpecLoader.cs", "Nethermind.Specs.ChainSpecStyle", "ChainSpecLoader", "Load", 1, "Parity chain-spec entry"),
        new("src/Nethermind/Nethermind.Specs/ChainSpecStyle/ChainSpecLoader.cs", "Nethermind.Specs.ChainSpecStyle", "ChainSpecLoader", "InitChainSpecFrom", 1, "runtime ChainSpec construction"),
        new("src/Nethermind/Nethermind.Specs/ChainSpecStyle/ChainSpecLoader.cs", "Nethermind.Specs.ChainSpecStyle", "ChainSpecLoader", "LoadParameters", 3, "fork-parameter mapping"),
        new("src/Nethermind/Nethermind.Specs/ChainSpecStyle/ChainSpecLoader.cs", "Nethermind.Specs.ChainSpecStyle", "ChainSpecLoader", "LoadTransitions", 2, "named-fork timestamp mapping"),
        new("src/Nethermind/Nethermind.Runner/Ethereum/Api/ApiBuilder.cs", "Nethermind.Runner.Ethereum.Api", "ApiBuilder", "LoadChainSpec", 1, "mainnet chain-spec configuration route"),
        new("src/Nethermind/Nethermind.Specs/MainnetSpecProvider.cs", "Nethermind.Specs", "MainnetSpecProvider", null, null, "mainnet fork selector"),
        new("src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs", "Nethermind.Specs.Forks", "Amsterdam", null, null, "Amsterdam EIP schedule"),
        new("src/Nethermind/Nethermind.Core/BlockHeader.cs", "Nethermind.Core", "BlockHeader", "CloneForProcessing", 0, "processing-header projection"),
        new("src/Nethermind/Nethermind.Core/BlockHeader.cs", "Nethermind.Core", "BlockHeader", "CopyProcessingFields", 1, "processing-header preserved fields"),
        new("src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptsTracer.cs", "Nethermind.Blockchain.Tracing", "BlockReceiptsTracer", "BuildReceipt", null, "receipt settlement projection"),
        new("src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptGasAccountingKernel.cs", "Nethermind.Blockchain.Tracing", "BlockReceiptGasAccountingKernel", "Accumulate", 6, "two-dimensional cumulative block gas"),
        new("src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptGasAccountingKernel.cs", "Nethermind.Blockchain.Tracing", "BlockReceiptGasAccountingKernel", "FromTotals", 3, "header gas maximum"),
        new("src/Nethermind/Nethermind.Blockchain/Receipts/ReceiptsRootCalculator.cs", "Nethermind.Blockchain.Receipts", "ReceiptsRootCalculator", "GetReceiptsRoot", 3, "receipt-root implementation boundary"),
        new("src/Nethermind/Nethermind.State/WorldState.cs", "Nethermind.State", "WorldState", "CommitTree", 1, "persistence boundary"),
        new("src/Nethermind/Nethermind.State/WorldState.cs", "Nethermind.State", "WorldState", "BeginScope", 1, "scope implementation boundary"),
        new("src/Nethermind/Nethermind.Evm/State/IWorldState.cs", "Nethermind.Evm.State", "IWorldState", "Reset", 1, "journal reset boundary"),
        new("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "Nethermind.Evm.GasPolicy", "EthereumGasPolicy", "CombineBlockGas", 2, "header execution/state maximum"),
        new("src/Nethermind/Nethermind.Consensus/Validators/BlockValidator.cs", "Nethermind.Consensus.Validators", "BlockValidator", null, null, "registered block validator"),
        new("src/Nethermind/Nethermind.Blockchain/BlockhashProvider.cs", "Nethermind.Blockchain", "BlockhashProvider", null, null, "registered blockhash provider"),
        new("src/Nethermind/Nethermind.Blockchain/BeaconBlockRoot/BeaconBlockRootHandler.cs", "Nethermind.Blockchain.BeaconBlockRoot", "BeaconBlockRootHandler", null, null, "registered beacon-root handler"),
        new("src/Nethermind/Nethermind.Blockchain/Blocks/BlockhashStore.cs", "Nethermind.Blockchain.Blocks", "BlockhashStore", null, null, "registered blockhash store"),
        new("src/Nethermind/Nethermind.Consensus/Processing/InclusionListSatisfactionChecker.cs", "Nethermind.Consensus.Processing", "InclusionListSatisfactionChecker", null, null, "registered inclusion signal checker"),
        new("src/Nethermind/Nethermind.Evm/TransactionProcessing/ITransactionProcessorAdapter.cs", "Nethermind.Evm.TransactionProcessing", "ITransactionProcessorAdapter", null, null, "registered transaction adapter contract"),
        new("src/Nethermind/Nethermind.Evm/TransactionProcessing/ExecuteTransactionProcessorAdapter.cs", "Nethermind.Evm.TransactionProcessing", "ExecuteTransactionProcessorAdapter", null, null, "registered transaction adapter"),
        new("src/Nethermind/Nethermind.Consensus/Rewards/IRewardCalculator.cs", "Nethermind.Consensus.Rewards", "IRewardCalculator", null, null, "registered reward calculator contract"),
        new("src/Nethermind/Nethermind.Consensus/Rewards/RewardCalculator.cs", "Nethermind.Consensus.Rewards", "RewardCalculator", null, null, "registered reward calculator"),
        new("src/Nethermind/Nethermind.Consensus/Rewards/NoBlockRewards.cs", "Nethermind.Consensus.Rewards", "NoBlockRewards", null, null, "standard validation reward source"),
    ];

    internal static readonly string[] AuditedSourcePaths =
    [
        "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs",
        "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockValidationTransactionsExecutor.cs",
        "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.ParallelBlockValidationTransactionsExecutor.cs",
        "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.SystemContractHandler.cs",
        "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockAccessListSystemContractHandler.cs",
        "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.std.cs",
        "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs",
        "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs",
        "src/Nethermind/Nethermind.Consensus/Processing/IBlockProcessor.cs",
        "src/Nethermind/Nethermind.Consensus/Processing/IBlockAccessListManager.cs",
        "src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.cs",
        "src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.Validation.cs",
        "src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.SystemContracts.cs",
        "src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.StateChanges.cs",
        "src/Nethermind/Nethermind.Consensus/Processing/TransactionProcessorAdapterExtensions.cs",
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/ITransactionProcessor.cs",
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs",
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionProcessor.cs",
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionRoutingKernel.cs",
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionSettlementKernel.cs",
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/ExecutionOptions.cs",
        "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs",
        "src/Nethermind/Nethermind.Evm/BlockExecutionContext.cs",
        "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs",
        "src/Nethermind/Nethermind.Init/Modules/MainProcessingContext.cs",
        "src/Nethermind/Nethermind.Specs/MainnetSpecProvider.cs",
        "src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs",
        "src/Nethermind/Nethermind.Core/BlockHeader.cs",
        "src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptsTracer.cs",
        "src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptGasAccountingKernel.cs",
        "src/Nethermind/Nethermind.Blockchain/Receipts/ReceiptsRootCalculator.cs",
        "src/Nethermind/Nethermind.State/WorldState.cs",
        "src/Nethermind/Nethermind.Evm/State/IWorldState.cs",
        "src/Nethermind/Nethermind.Consensus/Processing/RecoverSignatures.cs",
        "src/Nethermind/Nethermind.Init/Modules/NethermindModule.cs",
        "src/Nethermind/Nethermind.Specs/ChainSpecStyle/ChainSpecBasedSpecProvider.cs",
        "src/Nethermind/Nethermind.Specs/ChainSpecStyle/ChainSpecFileLoader.cs",
        "src/Nethermind/Nethermind.Specs/ChainSpecStyle/AutoDetectingChainSpecLoader.cs",
        "src/Nethermind/Nethermind.Specs/ChainSpecStyle/ChainSpecLoader.cs",
        "src/Nethermind/Nethermind.Runner/Ethereum/Api/ApiBuilder.cs",
        "src/Nethermind/Nethermind.Consensus/Validators/BlockValidator.cs",
        "src/Nethermind/Nethermind.Blockchain/BlockhashProvider.cs",
        "src/Nethermind/Nethermind.Blockchain/BeaconBlockRoot/BeaconBlockRootHandler.cs",
        "src/Nethermind/Nethermind.Blockchain/Blocks/BlockhashStore.cs",
        "src/Nethermind/Nethermind.Consensus/Processing/InclusionListSatisfactionChecker.cs",
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/ITransactionProcessorAdapter.cs",
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/ExecuteTransactionProcessorAdapter.cs",
        "src/Nethermind/Nethermind.Consensus/Rewards/IRewardCalculator.cs",
        "src/Nethermind/Nethermind.Consensus/Rewards/RewardCalculator.cs",
        "src/Nethermind/Nethermind.Consensus/Rewards/NoBlockRewards.cs",
    ];

    internal static readonly string[] RequiredBlockProcessingRegistrations =
    [
        "AddFirst<IBlockPreprocessorStep,RecoverSignatures>()",
        "AddSingleton<IBlockValidator,BlockValidator>()",
        "AddScoped<ITransactionProcessor,EthereumTransactionProcessor>()",
        "AddScoped<IWorldState,WorldState>()",
        "AddScoped<IVirtualMachine,EthereumVirtualMachine>()",
        "AddScoped<IBlockhashProvider,BlockhashProvider>()",
        "AddScoped<IBeaconBlockRootHandler,BeaconBlockRootHandler>()",
        "AddScoped<IBlockhashStore,BlockhashStore>()",
        "AddScoped<IBranchProcessor,BranchProcessor>()",
        "AddScoped<IBlockProcessor,BlockProcessor>()",
        "AddScoped<IInclusionListSatisfactionChecker,InclusionListSatisfactionChecker>()",
        "AddScoped<IWithdrawalProcessor,WithdrawalProcessor>()",
        "AddScoped<IExecutionRequestsProcessor,ExecutionRequestsProcessor>()",
        "AddSingleton<IBlockValidationModule,StandardBlockValidationModule>()",
        "AddSingleton<IRewardCalculatorSource>(NoBlockRewards.Instance)",
        "AddScoped<TransactionProcessorAdapterFactory>(CreateExecuteAdapter)",
        "AddScoped<ITransactionProcessorAdapter,ITransactionProcessor,TransactionProcessorAdapterFactory>(",
        "AddScoped<IBlockAccessListManager,BlockAccessListManager>()",
        "AddScoped<IBlockchainProcessor,BlockchainProcessor>()",
        "AddScoped<IRewardCalculator,IRewardCalculatorSource,ITransactionProcessor>(",
        "AddSingleton<IMainProcessingContext,MainProcessingContext>()",
    ];

    internal static readonly string[] RequiredStandardValidationRegistrations =
    [
        "AddScoped<IBlockProcessor.IBlockTransactionsExecutor,BlockProcessor.BlockValidationTransactionsExecutor>()",
        "AddDecorator<IBlockProcessor.IBlockTransactionsExecutor,BlockProcessor.ParallelBlockValidationTransactionsExecutor>()",
    ];

    internal static readonly string[] RequiredRegistrations =
    [
        .. RequiredBlockProcessingRegistrations,
        .. RequiredStandardValidationRegistrations,
    ];

    internal static readonly string[] RequiredRootRegistrations =
    [
        "AddModule(newBlockProcessingModule(configProvider.GetConfig<IInitConfig>(),configProvider.GetConfig<IBlocksConfig>()))",
        "AddSingleton(chainSpec)",
        "AddSingleton<ISpecProvider,ChainSpecBasedSpecProvider>()",
    ];

    internal static readonly string[] RequiredMainProcessingRegistrations =
    [
        "AddSingleton<IWorldStateScopeProvider>(worldState)",
        "AddModule(blockValidationModules)",
        "AddModule(mainProcessingModules)",
        "AddScoped<BlockchainProcessor,IBranchProcessor,IProcessingStats,IEnumerable<IBlockTracer>>(",
        "AddScoped<IBlockchainProcessor>(ctx=>ctx.Resolve<BlockchainProcessor>())",
        "AddScoped<IBlockProcessingQueue>(ctx=>ctx.Resolve<BlockchainProcessor>())",
    ];

    internal static readonly string[] RequiredSpecMarkers =
    [
        "AmsterdamBlockTimestamp=ulong.MaxValue-1",
        "Amsterdam.Instance",
        "BPO2.Instance",
        "IsEip8037Enabled=true",
        "IsEip8038Enabled=true",
        "IsEip7928Enabled=true",
        "IsEip7778Enabled=true",
    ];

    internal const string MainnetConfigChainSpecPath = "chainspec/foundation.json";

    internal static readonly string[] TargetForkTransitionProperties =
    [
        "eip7778TransitionTimestamp",
        "eip7928TransitionTimestamp",
        "eip8037TransitionTimestamp",
        "eip8038TransitionTimestamp",
    ];

    internal static readonly FileRequirement[] ConfigurationRequirements =
    [
        new("src/Nethermind/Nethermind.Runner/configs/mainnet.json", "json"),
        new("src/Nethermind/Chains/foundation.json", "json"),
        new("src/Nethermind/Nethermind.Config/Nethermind.Config.csproj", "msbuild"),
    ];

    internal static readonly string[] ChainSpecEmbeddingAnchors =
    [
        "<ItemGroupCondition=\"'$(EnableZkEvm)'!='true'\">",
        "<EmbeddedResourceInclude=\"..\\Chains\\**\\*.*\">",
        "<Link>chainspec\\%(RecursiveDir)%(Filename)%(Extension)</Link>",
    ];

    internal static readonly string[] ChainSpecLoaderAnchors =
    [
        "AutoDetectingChainSpecLoaderjsonLoader=new(serializer,logManager)",
        "{\".json\",jsonLoader}",
        "stringresourceName=FileNameToResource(fileName)",
        "Assemblyassembly=typeof(IConfig).Assembly",
        "assembly.GetManifestResourceStream(resourceName)",
        "return_chainSpecLoaders[extension].Load(stream)",
        "sb.Append(\"Nethermind.Config.\")",
        "sb.Append(fileName)",
        "sb.Replace('/','.')",
    ];

    internal static readonly string[] AutoDetectingChainSpecAnchors =
    [
        "private readonly ChainSpecLoader_parityLoader=new(serializer,logManager)",
        "GenesisFormatformat=DetectFormat(streamData)",
        "returnLoadDetected(format,streamData)",
        "GenesisFormat.Geth=>_gethLoader.Load(streamData)",
        "_=>_parityLoader.Load(streamData)",
    ];

    internal static readonly string[] ChainSpecConstructionAnchors =
    [
        "LoadParameters(chainSpecJson,parameters,chainSpec)",
        "LoadGenesis(chainSpecJson,chainSpec)",
        "LoadEngine(engine,chainSpec)",
        "LoadAllocations(chainSpecJson,chainSpec)",
        "LoadBootnodes(chainSpecJson,chainSpec)",
        "LoadTransitions(chainSpecJson,chainSpec)",
    ];

    internal static readonly string[] ChainSpecTargetForkAnchors =
    [
        "Eip7778TransitionTimestamp=parameters.Eip7778TransitionTimestamp",
        "Eip7928TransitionTimestamp=parameters.Eip7928TransitionTimestamp",
        "Eip8037TransitionTimestamp=parameters.Eip8037TransitionTimestamp",
        "Eip8038TransitionTimestamp=parameters.Eip8038TransitionTimestamp",
    ];

    internal static readonly string[] ChainSpecProviderTargetForkAnchors =
    [
        "releaseSpec.IsEip7778Enabled=(chainSpec.Parameters.Eip7778TransitionTimestamp??ulong.MaxValue)<=releaseStartTimestamp",
        "releaseSpec.IsEip7928Enabled=(chainSpec.Parameters.Eip7928TransitionTimestamp??ulong.MaxValue)<=releaseStartTimestamp",
        "releaseSpec.IsEip8037Enabled=(chainSpec.Parameters.Eip8037TransitionTimestamp??ulong.MaxValue)<=releaseStartTimestamp",
        "releaseSpec.IsEip8038Enabled=(chainSpec.Parameters.Eip8038TransitionTimestamp??ulong.MaxValue)<=releaseStartTimestamp",
    ];

    internal static readonly FileRequirement[] SpecificationRequirements =
    [
        new("tools/Evm/Lean/SynchronousBlockPipelineMachineExtractor/Specification/BlockPipelineState.lean", "lean"),
        new("tools/Evm/Lean/SynchronousBlockPipelineMachineExtractor/Specification/BlockPipelinePlan.lean", "lean"),
        new("tools/Evm/Lean/SynchronousBlockPipelineMachineExtractor/Specification/CompositionBoundary.lean", "lean"),
        new("tools/Evm/Lean/SynchronousBlockPipelineMachineExtractor/Specification/MetadataMirror.lean", "lean"),
    ];

    internal static readonly OrderedAnchor[] ProcessOneAnchors =
    [
        new("bal-preparation", "BlockProcessor.cs", "BlockProcessor.ProcessOne", "_balManager.PrepareForProcessing(suggestedBlock,spec,options)"),
        new("system-handler-selection", "BlockProcessor.cs", "BlockProcessor.ProcessOne", "_systemContractHandler=_balManager.Enabled?_balSystemContractHandler.Value:_standardSystemContractHandler.Value"),
        new("dao-transition", "BlockProcessor.cs", "BlockProcessor.ProcessOne", "ApplyDaoTransition(suggestedBlock)"),
        new("processing-header", "BlockProcessor.cs", "BlockProcessor.ProcessOne", "PrepareBlockForProcessing(suggestedBlock)"),
        new("process-block", "BlockProcessor.cs", "BlockProcessor.ProcessOne", "ProcessBlock(block,blockTracer,options,spec,token)"),
        new("bal-level-retry-catch", "BlockProcessor.cs", "BlockProcessor.ProcessOne", "catch(BlockAccessListBasedWorldState.InvalidBlockLevelAccessListExceptionex)when(_balManager.ParallelExecutionEnabled)"),
        new("bal-level-retry-wrap", "BlockProcessor.cs", "BlockProcessor.ProcessOne", "thrownewBlockAccessListSequentialRetryException(ex)"),
        new("parallel-retry-catch", "BlockProcessor.cs", "BlockProcessor.ProcessOne", "catch(BlockAccessListManager.ParallelExecutionExceptionex)when("),
        new("parallel-retry-wrap", "BlockProcessor.cs", "BlockProcessor.ProcessOne", "thrownewBlockAccessListSequentialRetryException(blockAccessListException)"),
        new("failed-account-change-disposal", "BlockProcessor.cs", "BlockProcessor.ProcessOne", "if(!processed)block.DisposeAccountChanges()"),
        new("processed-validation", "BlockProcessor.cs", "BlockProcessor.ProcessOne", "ValidateProcessedBlock(suggestedBlock,options,block,receipts)"),
        new("store-receipts", "BlockProcessor.cs", "BlockProcessor.ProcessOne", "StoreTxReceipts(block,receipts,spec)"),
    ];

    internal static readonly string[] ProcessBlockAnchors =
    [
        "ReceiptsTracer.SetOtherTracer(blockTracer)",
        "ReceiptsTracer.StartNewBlockTrace(block)",
        "_blockTransactionsExecutor.SetBlockExecutionContext(CreateBlockExecutionContext(block.Header,spec))",
        "_balManager.Setup(block)",
        "_systemContractHandler.StoreBeaconRoot(block,spec,NullTxTracer.Instance)",
        "_systemContractHandler.ApplyBlockhashStateChanges(header,spec)",
        "CommitState(spec)",
        "_blockTransactionsExecutor.ProcessTransactions(block,options,ReceiptsTracer,token)",
        "TransactionsExecuted?.Invoke()",
        "CommitState(spec)",
        "BlobGasCalculator.CalculateBlobGas(block.Transactions)",
        "ShouldCalculateReceiptsInBackground(receipts)",
        "Task.Run(()=>",
        "CalculateBlooms(receipts)",
        "AccumulateBlockBloom(receipts)",
        "CalculateReceiptsRoot(receipts,spec,block)",
        "ApplyMinerRewards(block,blockTracer,spec)",
        "_systemContractHandler.ProcessWithdrawals(block,spec)",
        "CommitState(spec)",
        "_systemContractHandler.ProcessExecutionRequests(block,_stateProvider,receipts,spec)",
        "ReceiptsTracer.EndBlockTrace(accumulateBlockBloom:bloomsAndReceiptsRootTaskisnull)",
        "CommitStateAndStorageRoots(spec)",
        "SetAccountChanges(block)",
        "ComputeStateRoot(header)",
        "(header.Bloom,header.ReceiptsRoot)=bloomsAndReceiptsRootTask.GetAwaiter().GetResult()",
        "_balManager.SetBlockAccessList(block)",
        "header.Hash=header.CalculateHash()",
    ];

    /// <summary>
    /// ProcessBlockAnchors is a membership catalog. These edges are the checked
    /// main-thread source order; background task-body and join edges are kept in
    /// BackgroundReceiptOrderEdges and do not assert a cross-thread total order.
    /// </summary>
    internal static readonly OrderEdge[] ProcessBlockOrderEdges =
    [
        new("trace-context-order", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "BlockProcessor", "ProcessBlock", 5,
            "ReceiptsTracer.SetOtherTracer(blockTracer)", "ReceiptsTracer.StartNewBlockTrace(block)"),
        new("trace-before-execution-context", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "BlockProcessor", "ProcessBlock", 5,
            "ReceiptsTracer.StartNewBlockTrace(block)", "_blockTransactionsExecutor.SetBlockExecutionContext(CreateBlockExecutionContext(block.Header,spec))"),
        new("context-before-bal-setup", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "BlockProcessor", "ProcessBlock", 5,
            "_blockTransactionsExecutor.SetBlockExecutionContext(CreateBlockExecutionContext(block.Header,spec))", "_balManager.Setup(block)"),
        new("bal-setup-before-beacon", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "BlockProcessor", "ProcessBlock", 5,
            "_balManager.Setup(block)", "_systemContractHandler.StoreBeaconRoot(block,spec,NullTxTracer.Instance)"),
        new("beacon-before-blockhash", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "BlockProcessor", "ProcessBlock", 5,
            "_systemContractHandler.StoreBeaconRoot(block,spec,NullTxTracer.Instance)", "_systemContractHandler.ApplyBlockhashStateChanges(header,spec)"),
        new("blockhash-before-pre-commit", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "BlockProcessor", "ProcessBlock", 5,
            "_systemContractHandler.ApplyBlockhashStateChanges(header,spec)", "CommitState(spec)"),
        new("pre-commit-before-fold", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "BlockProcessor", "ProcessBlock", 5,
            "CommitState(spec)", "_blockTransactionsExecutor.ProcessTransactions(block,options,ReceiptsTracer,token)"),
        new("fold-before-completion-signal", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "BlockProcessor", "ProcessBlock", 5,
            "_blockTransactionsExecutor.ProcessTransactions(block,options,ReceiptsTracer,token)", "TransactionsExecuted?.Invoke()"),
        new("completion-before-post-tx-commit", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "BlockProcessor", "ProcessBlock", 5,
            "TransactionsExecuted?.Invoke()", "CommitState(spec)"),
        new("post-tx-commit-before-blob", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "BlockProcessor", "ProcessBlock", 5,
            "TransactionsExecuted?.Invoke();CommitState(spec)", "BlobGasCalculator.CalculateBlobGas(block.Transactions)"),
        new("blob-before-receipt-arm", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "BlockProcessor", "ProcessBlock", 5,
            "BlobGasCalculator.CalculateBlobGas(block.Transactions)", "ShouldCalculateReceiptsInBackground(receipts)"),
        new("receipt-arm-before-rewards", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "BlockProcessor", "ProcessBlock", 5,
            "ShouldCalculateReceiptsInBackground(receipts)", "ApplyMinerRewards(block,blockTracer,spec)"),
        new("rewards-before-withdrawals", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "BlockProcessor", "ProcessBlock", 5,
            "ApplyMinerRewards(block,blockTracer,spec)", "_systemContractHandler.ProcessWithdrawals(block,spec)"),
        new("withdrawals-before-system-commit", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "BlockProcessor", "ProcessBlock", 5,
            "_systemContractHandler.ProcessWithdrawals(block,spec)", "CommitState(spec)"),
        new("system-commit-before-requests", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "BlockProcessor", "ProcessBlock", 5,
            "_systemContractHandler.ProcessWithdrawals(block,spec)", "_systemContractHandler.ProcessExecutionRequests(block,_stateProvider,receipts,spec)"),
        new("requests-before-trace-end", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "BlockProcessor", "ProcessBlock", 5,
            "_systemContractHandler.ProcessExecutionRequests(block,_stateProvider,receipts,spec)", "ReceiptsTracer.EndBlockTrace(accumulateBlockBloom:"),
        new("trace-end-before-root-commit", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "BlockProcessor", "ProcessBlock", 5,
            "ReceiptsTracer.EndBlockTrace(accumulateBlockBloom:", "CommitStateAndStorageRoots(spec)"),
        new("storage-commit-before-account-changes", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "BlockProcessor", "ProcessBlock", 5,
            "CommitStateAndStorageRoots(spec)", "SetAccountChanges(block)"),
        new("account-changes-before-state-root", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "BlockProcessor", "ProcessBlock", 5,
            "SetAccountChanges(block)", "ComputeStateRoot(header)"),
        new("state-root-before-background-await", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "BlockProcessor", "ProcessBlock", 5,
            "ComputeStateRoot(header)", "(header.Bloom,header.ReceiptsRoot)=bloomsAndReceiptsRootTask.GetAwaiter().GetResult()"),
        new("background-await-before-bal-finalization", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "BlockProcessor", "ProcessBlock", 5,
            "(header.Bloom,header.ReceiptsRoot)=bloomsAndReceiptsRootTask.GetAwaiter().GetResult()", "_balManager.SetBlockAccessList(block)"),
        new("bal-before-header-hash", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "BlockProcessor", "ProcessBlock", 5,
            "_balManager.SetBlockAccessList(block)", "header.Hash=header.CalculateHash()"),
    ];

    internal static readonly OrderEdge[] SynchronousReceiptOrderEdges =
    [
        new("synchronous-bloom-before-root", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "BlockProcessor", "ProcessBlock", 5,
            "else{CalculateBlooms(receipts);", "header.ReceiptsRoot=CalculateReceiptsRoot(receipts,spec,block);"),
    ];

    /// <summary>Edges inside the background receipt task or its scheduling/join boundary.</summary>
    internal static readonly OrderEdge[] BackgroundReceiptOrderEdges =
    [
        new("background-task-body-order", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "BlockProcessor", "ProcessBlock", 5,
            "Task.Run(()=>", "CalculateBlooms(receipts)"),
        new("background-bloom-before-task-artifacts", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "BlockProcessor", "ProcessBlock", 5,
            "CalculateBlooms(receipts)", "AccumulateBlockBloom(receipts)"),
        new("background-bloom-before-task-root", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "BlockProcessor", "ProcessBlock", 5,
            "AccumulateBlockBloom(receipts)", "CalculateReceiptsRoot(receipts,spec,block)"),
        new("background-scheduled-before-await", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "BlockProcessor", "ProcessBlock", 5,
            "Task.Run(()=>", "(header.Bloom,header.ReceiptsRoot)=bloomsAndReceiptsRootTask.GetAwaiter().GetResult()"),
    ];

    internal static readonly string[] BranchAnchors =
    [
        "if(suggestedBlocks.Count==0)return[]",
        "if(stateProvider.IsInScope)",
        "if(baseBlockisnull&&suggestedBlock.IsGenesis)",
        "thrownewInvalidOperationException(",
        "worldStateCloser=stateProvider.BeginScope(baseBlock)",
        "blockProcessor.ProcessOne(suggestedBlock,blockOptions,blockTracer,spec,token)",
        "CancellationTokenExtensions.CancelDisposeAndClear(refbackgroundCancellation)",
        "worldStateCloser.Dispose()",
        "worldStateCloser=stateProvider.BeginScope(preBlockBaseBlock)",
        "ProcessingOptions retryOptions=blockOptions|ProcessingOptions.ForceSequentialBlockAccessList",
        "inclusionListSatisfactionChecker.IsSatisfied(processedBlock,suggestedBlock,stateProvider)",
        "processedBlock.IsInclusionListSatisfied=inclusionListSatisfied",
        "suggestedBlock.IsInclusionListSatisfied=inclusionListSatisfied",
        "WaitAndClear(refpreWarmTask)",
        "PreCommitBlock(suggestedBlock.Header)",
        "processedBlocksCount=i+1",
        "if(isCommitPoint&&notReadOnly)",
        "worldStateCloser=stateProvider.BeginScope(previousBranchStateRoot)",
        "stateProvider.Reset()",
        "worldStateCloser?.Dispose()",
    ];

    internal static readonly OrderEdge[] BranchOrderEdges =
    [
        new("inclusion-signal-before-processed-assignment", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "BranchProcessor", "Process", 5,
            "inclusionListSatisfactionChecker.IsSatisfied(processedBlock,suggestedBlock,stateProvider)", "processedBlock.IsInclusionListSatisfied=inclusionListSatisfied"),
        new("processed-assignment-before-suggested-assignment", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "BranchProcessor", "Process", 5,
            "processedBlock.IsInclusionListSatisfied=inclusionListSatisfied", "suggestedBlock.IsInclusionListSatisfied=inclusionListSatisfied"),
        new("signal-before-prewarm-wait", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "BranchProcessor", "Process", 5,
            "suggestedBlock.IsInclusionListSatisfied=inclusionListSatisfied", "WaitAndClear(refpreWarmTask)"),
        new("prewarm-before-commit-tree", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "BranchProcessor", "Process", 5,
            "WaitAndClear(refpreWarmTask)", "PreCommitBlock(suggestedBlock.Header)"),
        new("commit-tree-before-success-prefix", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "BranchProcessor", "Process", 5,
            "PreCommitBlock(suggestedBlock.Header)", "processedBlocksCount=i+1"),
        new("success-prefix-before-checkpoint-test", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "BranchProcessor", "Process", 5,
            "processedBlocksCount=i+1", "if(isCommitPoint&&notReadOnly)"),
        new("checkpoint-dispose-before-reopen", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "BranchProcessor", "Process", 5,
            "worldStateCloser?.Dispose()", "worldStateCloser=stateProvider.BeginScope(previousBranchStateRoot)"),
        new("checkpoint-reopen-before-reset", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "BranchProcessor", "Process", 5,
            "worldStateCloser=stateProvider.BeginScope(previousBranchStateRoot)", "stateProvider.Reset()"),
        new("retry-dispose-before-reopen", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "BranchProcessor", "Process", 5,
            "worldStateCloser.Dispose()", "worldStateCloser=stateProvider.BeginScope(preBlockBaseBlock)"),
        new("retry-reopen-before-forced-sequential", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "BranchProcessor", "Process", 5,
            "worldStateCloser=stateProvider.BeginScope(preBlockBaseBlock)", "ProcessingOptions retryOptions=blockOptions|ProcessingOptions.ForceSequentialBlockAccessList"),
        new("branch-catch-records-exception", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "BranchProcessor", "Process", 5,
            "catch(Exceptionex)", "processingException=ex"),
        new("branch-catch-rethrows", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "BranchProcessor", "Process", 5,
            "processingException=ex", "throw;"),
        new("exception-finally-disposes", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "BranchProcessor", "Process", 5,
            "throw;", "worldStateCloser?.Dispose()"),
        new("dispose-before-completion-event", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "BranchProcessor", "Process", 5,
            "worldStateCloser?.Dispose()", "BranchProcessingCompleted?.Invoke("),
        new("final-dispose-before-completion-event", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "BranchProcessor", "Process", 5,
            "worldStateCloser?.Dispose();}finally{if(blocksProcessingEventArgsisnotnull){BranchProcessingCompleted?.Invoke(",
            "newBranchProcessingCompletedEventArgs("),
    ];

    internal static readonly string[] ProcessBranchAnchors =
    [
        "_branchProcessor.Process(",
        "catch(InvalidBlockExceptionex)",
        "processedBlocks=null",
        "DeleteInvalidBlocks(inprocessingBranch,invalidBlockHash)",
    ];

    internal static readonly string[] ValidateProcessedAnchors =
    [
        "blockValidator.ValidateProcessedBlock(block,receipts,suggestedBlock,outstring?error)",
        "block.DisposeAccountChanges()",
        "thrownewInvalidBlockException(suggestedBlock,error)",
        "PostValidation(suggestedBlock,block,receipts,options)",
    ];

    internal static readonly string[] ChainAnchors =
    [
        "RunSimpleChecksAheadOfProcessing(suggestedBlock,options)",
        "UInt256totalDifficulty=suggestedBlock.TotalDifficulty??0",
        "boolshouldProcess=",
        "suggestedBlock.IsGenesis",
        "_blockTree.IsBetterThanHead(suggestedBlock.Header)",
        "options.ContainsFlag(ProcessingOptions.ForceProcessing)",
        "if(!shouldProcess)",
        "returnnull",
        "PrepareProcessingBranch(suggestedBlock,options)",
        "PrepareBlocksToProcess(suggestedBlock,options,processingBranch)",
        "ProcessBranch(processingBranch,options,tracer,token,outerror)",
        "lastProcessed.Header.TotalDifficulty=suggestedBlock.TotalDifficulty",
        "_blockTree.TryUpdateMainChain(suggestedBlock.Header,wereProcessed:true,preloadedBlocks:processingBranch.Blocks.AsSpan())",
        "_blockTree.MarkChainAsProcessed(processingBranch.Blocks)",
    ];

    internal static readonly string[] PreparationAnchors =
    [
        "Preprocess(blocksToProcess[i])",
    ];

    internal static readonly OrderEdge[] ChainOrderEdges =
    [
        new("simple-checks-before-eligibility", "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "BlockchainProcessor", "Process", 5,
            "RunSimpleChecksAheadOfProcessing(suggestedBlock,options)", "boolshouldProcess="),
        new("eligibility-uses-genesis", "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "BlockchainProcessor", "Process", 5,
            "boolshouldProcess=", "suggestedBlock.IsGenesis"),
        new("eligibility-uses-head", "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "BlockchainProcessor", "Process", 5,
            "suggestedBlock.IsGenesis", "_blockTree.IsBetterThanHead(suggestedBlock.Header)"),
        new("eligibility-uses-force-option", "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "BlockchainProcessor", "Process", 5,
            "_blockTree.IsBetterThanHead(suggestedBlock.Header)", "options.ContainsFlag(ProcessingOptions.ForceProcessing)"),
        new("eligibility-completes-before-test", "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "BlockchainProcessor", "Process", 5,
            "options.ContainsFlag(ProcessingOptions.ForceProcessing)", "if(!shouldProcess)"),
        new("not-eligible-returns-before-branch", "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "BlockchainProcessor", "Process", 5,
            "if(!shouldProcess)", "returnnull"),
        new("branch-selection-before-preparation", "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "BlockchainProcessor", "Process", 5,
            "PrepareProcessingBranch(suggestedBlock,options)", "PrepareBlocksToProcess(suggestedBlock,options,processingBranch)"),
        new("preparation-before-branch-processing", "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "BlockchainProcessor", "Process", 5,
            "PrepareBlocksToProcess(suggestedBlock,options,processingBranch)", "ProcessBranch(processingBranch,options,tracer,token,outerror)"),
        new("branch-before-td-assignment", "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "BlockchainProcessor", "Process", 5,
            "ProcessBranch(processingBranch,options,tracer,token,outerror)", "lastProcessed.Header.TotalDifficulty=suggestedBlock.TotalDifficulty"),
        new("td-before-head-update", "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "BlockchainProcessor", "Process", 5,
            "lastProcessed.Header.TotalDifficulty=suggestedBlock.TotalDifficulty", "_blockTree.TryUpdateMainChain(suggestedBlock.Header,wereProcessed:true,preloadedBlocks:processingBranch.Blocks.AsSpan())"),
        new("head-update-before-mark", "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "BlockchainProcessor", "Process", 5,
            "_blockTree.TryUpdateMainChain(suggestedBlock.Header,wereProcessed:true,preloadedBlocks:processingBranch.Blocks.AsSpan())", "_blockTree.MarkChainAsProcessed(processingBranch.Blocks)"),
    ];

    internal static readonly OrderEdge[] PreparationOrderEdges =
    [
        new("selected-blocks-before-recovery", "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "BlockchainProcessor", "PrepareBlocksToProcess", 3,
            "ArrayPoolList<Block>blocksToProcess=processingBranch.BlocksToProcess", "Preprocess(blocksToProcess[i])"),
    ];

    internal static readonly OrderEdge[] RecoveryOrderEdges =
    [
        new("recover-block-reads-fork", "src/Nethermind/Nethermind.Consensus/Processing/RecoverSignatures.cs", "RecoverSignatures", "RecoverData", 1,
            "IReleaseSpecreleaseSpec=_specProvider.GetSpec(block.Header)", "Transaction[]txs=block.Transactions"),
        new("recover-block-before-inclusion-list", "src/Nethermind/Nethermind.Consensus/Processing/RecoverSignatures.cs", "RecoverSignatures", "RecoverData", 1,
            "RecoverData(txs,releaseSpec)", "block.InclusionListTransactionsisnotnull"),
        new("recover-inclusion-list-skips-errors", "src/Nethermind/Nethermind.Consensus/Processing/RecoverSignatures.cs", "RecoverSignatures", "RecoverData", 1,
            "block.InclusionListTransactionsisnotnull", "RecoverData(block.InclusionListTransactions,releaseSpec,skipErrors:true)"),
        new("recover-tx-empty-before-all-recovered", "src/Nethermind/Nethermind.Consensus/Processing/RecoverSignatures.cs", "RecoverSignatures", "RecoverData", 3,
            "if(txs.Length==0)return", "if(AllSendersRecovered(txs,checkAuthorities:releaseSpec.IsAuthorizationListEnabled))return"),
        new("recover-tx-before-parallel-threshold", "src/Nethermind/Nethermind.Consensus/Processing/RecoverSignatures.cs", "RecoverSignatures", "RecoverData", 3,
            "if(AllSendersRecovered(txs,checkAuthorities:releaseSpec.IsAuthorizationListEnabled))return", "if(txs.Length>3)"),
        new("preprocess-dispatches-registered-steps", "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "BlockchainProcessor", "Preprocess", 1,
            "for(inti=0;i<_preprocessorSteps.Count;i++)", "_preprocessorSteps[i].RecoverData(block)"),
    ];

    internal static readonly OrderEdge[] TransactionFoldOrderEdges =
    [
        new("transaction-loop-before-adapter", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockValidationTransactionsExecutor.cs", "BlockValidationTransactionsExecutor", "ProcessTransactions", 4,
            "for(inti=0;i<block.Transactions.Length;i++)", "ProcessTransaction(block,currentTx,i,receiptsTracer,processingOptions)"),
        new("transaction-adapter-before-gas-limit-check", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockValidationTransactionsExecutor.cs", "BlockValidationTransactionsExecutor", "ProcessTransactions", 4,
            "ProcessTransaction(block,currentTx,i,receiptsTracer,processingOptions)", "if(shouldValidate&&block.Header.GasUsed>block.Header.GasLimit)"),
    ];

    internal static readonly OrderEdge[] AdapterOrderEdges =
    [
        new("transaction-trace-before-execute", "src/Nethermind/Nethermind.Consensus/Processing/TransactionProcessorAdapterExtensions.cs", "TransactionProcessorAdapterExtensions", "ProcessTransaction", 5,
            "receiptsTracer.StartNewTxTrace(currentTx)", "transactionProcessor.Execute(currentTx,receiptsTracer)"),
        new("transaction-execute-before-trace-end", "src/Nethermind/Nethermind.Consensus/Processing/TransactionProcessorAdapterExtensions.cs", "TransactionProcessorAdapterExtensions", "ProcessTransaction", 5,
            "transactionProcessor.Execute(currentTx,receiptsTracer)", "receiptsTracer.EndTxTrace()"),
    ];

    internal static readonly OrderEdge[] TransactionReceiptOrderEdges =
    [
        new("receipt-tracing-before-failure-receipt", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "TransactionProcessorBase", "FinalizeTransaction", 12,
            "if(tracer.IsTracingReceipt)", "tracer.MarkAsFailed(executingAccount,spentGas,output,error,stateRoot)"),
        new("receipt-tracing-before-success-receipt", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "TransactionProcessorBase", "FinalizeTransaction", 12,
            "if(tracer.IsTracingReceipt)", "tracer.MarkAsSuccess(executingAccount,spentGas,substate.Output.AsReadOnlyArray(),logs,stateRoot)"),
    ];

    internal static readonly string[] TransactionAnchors =
    [
        "WorldState.TakeSnapshot(true)",
        "ExecuteCore(transaction,txTracer,options)",
        "SystemTransactionRoutingKernel.UseSystemProcessor(tx.IsSystem(),opts)",
        "GetOrCreateSystemTransactionProcessor().Execute(tx,tracer,opts)",
        "Execute(tx,tracer,opts)",
        "RecoverSenderBeforeIntrinsicGas(tx,spec)",
        "CalculateIntrinsicGas(tx,spec,header.GasLimit)",
        "UpdateHeaderGasUsedAndPayFees(",
        "FinalizeTransaction(",
    ];

    internal static readonly string[] SystemTransactionAnchors =
    [
        "WorldState.BeginSystemAccountReadSuppression()",
        "OnBeforeSystemTransaction()",
        "SystemTransactionRoutingKernel.ShouldPayOriginalValue(opts)",
        "base.Execute(tx,tracer,SystemTransactionRoutingKernel.GetSystemExecutionOptions(opts,_payOriginalValue))",
    ];

    internal static readonly string[] TransactionGasMarkers =
    [
        "_blockCumulativeExecutionGas",
        "_blockCumulativeStateGas",
        "ParticipatesInNormalBlockCounters",
        "spec.IsEip8037Enabled",
        "TGasPolicy.CombineBlockGas(_blockCumulativeExecutionGas,_blockCumulativeStateGas)",
    ];

    internal static readonly string[] ReceiptGasMarkers =
    [
        "BlockReceiptGasAccountingKernel.Accumulate(",
        "GasUsedTotal=cumulativeReceiptGas",
        "GasUsed=gasConsumed.SpentGas",
        "Block.Header.GasUsed=accounting.HeaderGasUsed",
    ];

    internal static readonly string[] AdapterAnchors =
    [
        "transactionProcessor.Execute(currentTx,receiptsTracer)",
    ];

    internal static readonly string[] AdapterRegistrationAnchors =
    [
        "CreateExecuteAdapter",
        "newExecuteTransactionProcessorAdapter(transactionProcessor)",
    ];

    internal static readonly string[] ReceiptAnchors =
    [
        "BuildReceipt(recipient,gasSpent,StatusCode.Success,logs,stateRoot)",
        "BuildReceipt(recipient,gasSpent,StatusCode.Failure,[],stateRoot)",
        "GetReceiptsRoot",
    ];

    internal static readonly string[] SynchronousReceiptAnchors =
    [
        "else{CalculateBlooms(receipts);header.ReceiptsRoot=CalculateReceiptsRoot(receipts,spec,block);}",
    ];

    internal static readonly string[] BackgroundReceiptAnchors =
    [
        "Task.Run(()=>",
        "CalculateBlooms(receipts)",
        "AccumulateBlockBloom(receipts)",
        "CalculateReceiptsRoot(receipts,spec,block)",
        "(header.Bloom,header.ReceiptsRoot)=bloomsAndReceiptsRootTask.GetAwaiter().GetResult()",
    ];

    internal static readonly string[] HeaderProjectionMarkers =
    [
        "dst.Bloom=Core.Bloom.Empty",
        "dst.Author=Author",
        "dst.Hash=Hash",
        "dst.MixHash=MixHash",
        "dst.Nonce=Nonce",
        "dst.TxRoot=TxRoot",
        "dst.TotalDifficulty=TotalDifficulty",
        "dst.ReceiptsRoot=ReceiptsRoot",
        "dst.BaseFeePerGas=BaseFeePerGas",
        "dst.WithdrawalsRoot=WithdrawalsRoot",
        "dst.RequestsHash=RequestsHash",
        "dst.IsPostMerge=IsPostMerge",
        "dst.ParentBeaconBlockRoot=ParentBeaconBlockRoot",
        "dst.SlotNumber=SlotNumber",
        "dst.BlockAccessListHash=BlockAccessListHash",
        "dst.BlobGasUsed=BlobGasUsed",
        "dst.ExcessBlobGas=ExcessBlobGas",
    ];

    internal static readonly string[] OpenBoundaries =
    [
        "parallel transaction execution versus sequential execution and BAL equivalence",
        "ordinary EVM frame loop, world-state journals, and transaction semantic adapters",
        "precompile and cryptographic semantics",
        "receipt settlement object representation and exact UInt256/width behavior",
        "receipt bloom hashing, receipt-root trie/RLP hashing, state-root trie hashing, and header hashing",
        "database persistence, CommitTree durability, crash recovery, and restart behavior",
        "background receipt task scheduling, thread interleavings, cancellation and exception precedence",
        "DI container resolution, virtual dispatch, Fody/CLR/JIT/compiler correctness",
        "block access-list production/validation equivalence",
        "suggested-block and sender/EIP-7702 preprocessing semantic adapters",
        "DAO, beacon-root, historical-blockhash, rewards, withdrawals, execution-request and system-call semantics",
        "head selection, total-difficulty representation, and processed-chain persistence",
        "async queue scheduling and concurrency outside the synchronous route",
    ];

    internal static readonly CompositionRequirement[] CompositionRequirements =
    [
        new("BlockSystemComposition", "tools/Evm/Lean/BlockProcessorExtractor/Extractor.cs"),
        new("BlockSystemComposition", "tools/Evm/Lean/BlockProcessorExtractor/Generated/BlockProcessorControl.lean"),
        new("BlockSystemComposition", "tools/Evm/Lean/Eip803x/BlockReference.lean"),
        new("ParallelBlockReference", "tools/Evm/Lean/Eip803x/BranchReference.lean"),
        new("BlockSystemComposition", "tools/Evm/Lean/Eip803x/BlockSystemComposition.lean"),
        new("ParallelBlockReference", "tools/Evm/Lean/Eip803x/ParallelBlockReference.lean"),
        new("BlockSystemComposition", "tools/Evm/Lean/OrdinaryTransactionMachineExtractor/Specification/ExecutionBoundary.lean"),
        new("BlockSystemComposition", "tools/Evm/Lean/TransactionProcessorExtractor/Generated/TransactionProcessorLifecycle.lean"),
    ];

    internal static readonly HookDependency[] HookDependencies =
    [
        new(HookId.SuggestedBlockSimpleChecks, PipelinePhase.SuggestedBlockValidation, "BlockchainProcessor.cs", "BlockchainProcessor.RunSimpleChecksAheadOfProcessing", "suggested block + options", "bool signal or escaping missing-data exception", FailureDisposition.Signal, false, false, false),
        new(HookId.EvaluateEligibility, PipelinePhase.SynchronousBranchSelectionAndPreparation, "BlockchainProcessor.cs", "BlockchainProcessor.Process eligibility", "suggested block + head + options", "process/skip signal", FailureDisposition.Signal, false, false, false),
        new(HookId.SelectAndPrepareBranch, PipelinePhase.SynchronousBranchSelectionAndPreparation, "BlockchainProcessor.cs", "BlockchainProcessor.PrepareProcessingBranch", "suggested block + options + block tree", "selected branch + parent", FailureDisposition.Escape, false, false, false),
        new(HookId.RecoverSignatures, PipelinePhase.SenderAndAuthorityRecovery, "BlockchainProcessor.cs", "BlockchainProcessor.Preprocess", "selected blocks", "recovered transaction/auth fields", FailureDisposition.Escape, false, false, false),
        new(HookId.RecoverSignatureImplementation, PipelinePhase.SenderAndAuthorityRecovery, "RecoverSignatures.cs", "RecoverSignatures.RecoverData", "block/transaction signatures + fork", "sender and authority fields or escaping recovery failure", FailureDisposition.Escape, true, false, false),
        new(HookId.RecoverAuthorities, PipelinePhase.SenderAndAuthorityRecovery, "BlockchainProcessor.cs", "BlockchainProcessor.Preprocess", "selected blocks", "recovered sender/EIP-7702 authority fields", FailureDisposition.Escape, false, false, false),
        new(HookId.BeginBranchScope, PipelinePhase.OpenWorldStateScope, "BranchProcessor.cs", "BranchProcessor.Process", "parent root + scope provider", "owned scope", FailureDisposition.Escape, true, false, false),
        new(HookId.BeginGenesisScope, PipelinePhase.OpenWorldStateScope, "BranchProcessor.cs", "BranchProcessor.Process", "null base + genesis + externally open scope", "externally owned scope", FailureDisposition.Signal, false, false, false),
        new(HookId.PrepareBal, PipelinePhase.DaoTransition, "BlockProcessor.cs", "BlockProcessor.ProcessOne", "block + spec + options", "BAL mode prepared", FailureDisposition.Escape, false, false, true),
        new(HookId.SelectSystemContractHandler, PipelinePhase.DaoTransition, "BlockProcessor.cs", "BlockProcessor.ProcessOne", "BAL mode", "standard/BAL handler", FailureDisposition.Escape, false, false, false),
        new(HookId.ApplyDaoTransition, PipelinePhase.DaoTransition, "BlockProcessor.cs", "BlockProcessor.ApplyDaoTransition", "suggested block + spec", "journal effects", FailureDisposition.Escape, true, false, true),
        new(HookId.PrepareBlockForProcessing, PipelinePhase.DaoTransition, "BlockProcessor.cs", "BlockProcessor.PrepareBlockForProcessing", "proposed block", "processing clone", FailureDisposition.Escape, true, false, false),
        new(HookId.SetOtherTracer, PipelinePhase.BeaconRootSystemCall, "BlockProcessor.cs", "BlockReceiptsTracer.SetOtherTracer", "block tracer", "tracer context", FailureDisposition.Escape, false, false, false),
        new(HookId.StartBlockTrace, PipelinePhase.BeaconRootSystemCall, "BlockProcessor.cs", "BlockReceiptsTracer.StartNewBlockTrace", "processing block", "trace context", FailureDisposition.Escape, false, false, false),
        new(HookId.SetBlockExecutionContext, PipelinePhase.BeaconRootSystemCall, "BlockProcessor.cs", "IBlockTransactionsExecutor.SetBlockExecutionContext", "header + spec", "execution context", FailureDisposition.Escape, false, false, false),
        new(HookId.SetupBlockAccessList, PipelinePhase.BeaconRootSystemCall, "BlockProcessor.cs", "IBlockAccessListManager.Setup", "processing block", "BAL accumulator", FailureDisposition.RetrySequential, false, false, true),
        new(HookId.StoreBeaconRoot, PipelinePhase.BeaconRootSystemCall, "BlockProcessor.cs", "SystemContractHandler.StoreBeaconRoot", "block + spec + null tracer", "journal effects", FailureDisposition.Escape, true, false, true),
        new(HookId.ApplyBlockhashStateChanges, PipelinePhase.HistoricalBlockhashStateChange, "BlockProcessor.cs", "SystemContractHandler.ApplyBlockhashStateChanges", "header + spec", "journal effects", FailureDisposition.Escape, true, false, true),
        new(HookId.CommitPreTransactionState, PipelinePhase.HistoricalBlockhashStateChange, "BlockProcessor.cs", "BlockProcessor.CommitState", "spec", "journal boundary", FailureDisposition.Escape, true, false, true),
        new(HookId.ExecuteTransactionFold, PipelinePhase.UserTransactionFold, "BlockProcessor.BlockValidationTransactionsExecutor.cs", "IBlockTransactionsExecutor.ProcessTransactions", "block + options + receipt tracer + cancellation", "receipts + execution/state counters", FailureDisposition.RejectInvalidBlock, true, false, true),
        new(HookId.TransactionsExecutedSignal, PipelinePhase.UserTransactionFold, "BlockProcessor.cs", "BlockProcessor.TransactionsExecuted", "completed transaction fold", "cancellation signal", FailureDisposition.Signal, false, false, false),
        new(HookId.CommitPostTransactionState, PipelinePhase.UserTransactionFold, "BlockProcessor.cs", "BlockProcessor.CommitState", "spec + completed transaction fold", "journal boundary", FailureDisposition.Escape, true, true, true),
        new(HookId.BuildReceiptsAndCumulativePaidGas, PipelinePhase.UserTransactionFold, "BlockReceiptsTracer.cs", "BlockReceiptsTracer.BuildReceipt", "settlement + previous receipt counter", "receipt paid/cumulative gas", FailureDisposition.Escape, false, false, true),
        new(HookId.CalculateBlobGas, PipelinePhase.BlobGasReceiptRootAndBloom, "BlockProcessor.cs", "BlobGasCalculator.CalculateBlobGas", "transactions", "blob gas fields", FailureDisposition.Escape, true, false, false),
        new(HookId.CalculateReceiptBlooms, PipelinePhase.BlobGasReceiptRootAndBloom, "BlockProcessor.cs", "BlockProcessor.CalculateBlooms", "updated receipts + logs", "receipt blooms", FailureDisposition.Escape, true, true, false),
        new(HookId.AccumulateBlockBloom, PipelinePhase.BlobGasReceiptRootAndBloom, "BlockProcessor.cs", "BlockProcessor.AccumulateBlockBloom", "updated receipts", "block bloom", FailureDisposition.Escape, false, true, false),
        new(HookId.CalculateReceiptsRoot, PipelinePhase.BlobGasReceiptRootAndBloom, "BlockProcessor.cs", "BlockProcessor.CalculateReceiptsRoot", "updated receipts + spec + block", "receipt root", FailureDisposition.Escape, false, true, false),
        new(HookId.InstallSynchronousReceiptArtifacts, PipelinePhase.BlobGasReceiptRootAndBloom, "BlockProcessor.cs", "BlockProcessor.ProcessBlock", "synchronous bloom/root", "header bloom/root", FailureDisposition.Escape, true, true, false),
        new(HookId.ScheduleBackgroundReceiptArtifacts, PipelinePhase.BlobGasReceiptRootAndBloom, "BlockProcessor.cs", "BlockProcessor.ProcessBlock", "updated receipts + spec + block", "pending receipt task", FailureDisposition.Escape, false, true, false),
        new(HookId.AwaitBackgroundReceiptArtifacts, PipelinePhase.BlobGasReceiptRootAndBloom, "BlockProcessor.cs", "BlockProcessor.ProcessBlock", "pending task", "header bloom/root or escaping failure", FailureDisposition.Escape, true, true, false),
        new(HookId.ApplyMinerRewards, PipelinePhase.Rewards, "BlockProcessor.cs", "BlockProcessor.ApplyMinerRewards", "processed block + tracer + spec", "journal effects", FailureDisposition.Escape, true, false, true),
        new(HookId.ProcessWithdrawals, PipelinePhase.Withdrawals, "BlockProcessor.cs", "SystemContractHandler.ProcessWithdrawals", "block + spec", "journal effects", FailureDisposition.Escape, true, false, true),
        new(HookId.CommitPostSystemState, PipelinePhase.Withdrawals, "BlockProcessor.cs", "BlockProcessor.CommitState", "withdrawal/reward journal + spec", "journal boundary", FailureDisposition.Escape, true, false, true),
        new(HookId.ProcessExecutionRequests, PipelinePhase.ExecutionRequestsAndSystemCalls, "BlockProcessor.cs", "SystemContractHandler.ProcessExecutionRequests", "block + state provider + updated receipts + spec", "requests hash/observations", FailureDisposition.Escape, true, true, true),
        new(HookId.EndBlockTrace, PipelinePhase.ExecutionRequestsAndSystemCalls, "BlockProcessor.cs", "BlockReceiptsTracer.EndBlockTrace", "trace + receipt mode", "trace completion", FailureDisposition.Escape, false, true, false),
        new(HookId.CommitStorageAndStateRoots, PipelinePhase.StorageAndStateRoots, "BlockProcessor.cs", "BlockProcessor.CommitStateAndStorageRoots", "spec", "storage/state journal boundary", FailureDisposition.Escape, true, false, true),
        new(HookId.SetAccountChanges, PipelinePhase.StorageAndStateRoots, "BlockProcessor.cs", "BlockProcessor.SetAccountChanges", "processed block", "account-change observation", FailureDisposition.Escape, false, false, true),
        new(HookId.ComputeStateRoot, PipelinePhase.StorageAndStateRoots, "BlockProcessor.cs", "BlockProcessor.ComputeStateRoot", "processing header", "state root", FailureDisposition.Escape, true, false, true),
        new(HookId.FinalizeBal, PipelinePhase.BlockAccessList, "BlockProcessor.cs", "IBlockAccessListManager.SetBlockAccessList", "processed block", "BAL observation", FailureDisposition.Escape, true, false, true),
        new(HookId.ValidateProcessedBlock, PipelinePhase.ProcessedHeaderValidation, "BlockProcessor.cs", "BlockProcessor.ValidateProcessedBlock", "proposed + processing block + receipts + options", "accepted or caught InvalidBlock", FailureDisposition.RejectInvalidBlock, false, true, true),
        new(HookId.DisposeAccountChanges, PipelinePhase.ProcessedHeaderValidation, "BlockProcessor.cs", "BlockProcessor.ValidateProcessedBlock/ProcessOne finally", "failed block", "account changes disposed", FailureDisposition.Signal, true, false, true),
        new(HookId.DisposeRetryScope, PipelinePhase.CommitTreeInvocation, "BranchProcessor.cs", "BranchProcessor.Process BAL retry", "failed parallel attempt", "disposed attempt scope", FailureDisposition.Escape, true, false, true),
        new(HookId.ReopenRetryScope, PipelinePhase.CommitTreeInvocation, "BranchProcessor.cs", "BranchProcessor.Process BAL retry", "pre-block base root", "fresh sequential scope", FailureDisposition.Escape, true, false, false),
        new(HookId.DisposeCheckpointScope, PipelinePhase.CommitTreeInvocation, "BranchProcessor.cs", "BranchProcessor.Process checkpoint", "successful non-edge checkpoint", "disposed checkpoint scope", FailureDisposition.Escape, true, false, true),
        new(HookId.ReopenCheckpointScope, PipelinePhase.CommitTreeInvocation, "BranchProcessor.cs", "BranchProcessor.Process checkpoint", "previous branch state root", "reopened checkpoint scope", FailureDisposition.Escape, true, false, true),
        new(HookId.WaitPrewarm, PipelinePhase.CommitTreeInvocation, "BranchProcessor.cs", "BranchProcessor.Process", "prewarm task", "waited/cancelled task", FailureDisposition.Escape, false, false, true),
        new(HookId.CommitTree, PipelinePhase.CommitTreeInvocation, "BranchProcessor.cs", "BranchProcessor.PreCommitBlock", "successful processed header", "logical persistence invocation", FailureDisposition.Escape, false, false, true),
        new(HookId.RecordInclusionListSignal, PipelinePhase.CommitTreeInvocation, "BranchProcessor.cs", "BranchProcessor.Process inclusion signal", "processed block + suggested block + post-execution state", "Option Bool signal copied to both blocks", FailureDisposition.Signal, true, false, true),
        new(HookId.IncrementSuccessfulPrefix, PipelinePhase.CommitTreeInvocation, "BranchProcessor.cs", "BranchProcessor.Process success prefix", "successful CommitTree", "processed prefix count", FailureDisposition.Signal, false, false, true),
        new(HookId.ResetScope, PipelinePhase.CommitTreeInvocation, "BranchProcessor.cs", "IWorldState.Reset", "successful block", "next-block journal baseline", FailureDisposition.Escape, true, false, true),
        new(HookId.DisposeBranchScope, PipelinePhase.CommitTreeInvocation, "BranchProcessor.cs", "BranchProcessor.Process finally", "branch completion/exception", "disposed owned scope", FailureDisposition.Escape, true, false, true),
        new(HookId.CatchBranchException, PipelinePhase.CommitTreeInvocation, "BranchProcessor.cs", "BranchProcessor.Process catch", "escaping branch exception", "recorded exception then rethrow", FailureDisposition.Escape, false, false, true),
        new(HookId.CompleteBranchProcessing, PipelinePhase.CommitTreeInvocation, "BranchProcessor.cs", "BranchProcessor.Process finally", "disposed scope + prefix count + exception", "completion event", FailureDisposition.Signal, false, false, false),
        new(HookId.ClassifySynchronousResult, PipelinePhase.SynchronousResultClassificationAndHeadFinalization, "BlockchainProcessor.cs", "BlockchainProcessor.ProcessBranch", "branch result/error", "accepted, skipped, invalid or escaped", FailureDisposition.Signal, false, false, false),
        new(HookId.UpdateTotalDifficulty, PipelinePhase.SynchronousResultClassificationAndHeadFinalization, "BlockchainProcessor.cs", "BlockchainProcessor.Process", "last processed + suggested block", "total-difficulty observation", FailureDisposition.Escape, true, false, false),
        new(HookId.TryUpdateMainChain, PipelinePhase.SynchronousResultClassificationAndHeadFinalization, "BlockchainProcessor.cs", "IBlockTree.TryUpdateMainChain", "suggested header + processed blocks", "bool signal", FailureDisposition.Signal, true, false, false),
        new(HookId.MarkChainAsProcessed, PipelinePhase.SynchronousResultClassificationAndHeadFinalization, "BlockchainProcessor.cs", "IBlockTree.MarkChainAsProcessed", "processed branch", "processed-chain observation", FailureDisposition.Escape, true, false, false),
    ];
}
