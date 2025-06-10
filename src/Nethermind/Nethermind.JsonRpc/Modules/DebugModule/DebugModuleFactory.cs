// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Tracing;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Trie.Pruning;
using Nethermind.Facade;

namespace Nethermind.JsonRpc.Modules.DebugModule;

public class DebugModuleFactory(
    IWorldStateManager stateManager,
    IDbProvider dbProvider,
    IBlockTree blockTree,
    IJsonRpcConfig jsonRpcConfig,
    IBlockchainBridge blockchainBridge,
    ulong secondsPerSlot,
    IBlockValidator blockValidator,
    IBlockPreprocessorStep recoveryStep,
    IRewardCalculatorSource rewardCalculator,
    IReceiptStorage receiptStorage,
    IReceiptsMigration receiptsMigration,
    IConfigProvider configProvider,
    ISpecProvider specProvider,
    ISyncModeSelector syncModeSelector,
    IBadBlockStore badBlockStore,
    IFileSystem fileSystem,
    ILogManager logManager)
    : ModuleFactoryBase<IDebugRpcModule>
{
    protected readonly IBlockValidator _blockValidator = blockValidator;
    protected readonly IRewardCalculatorSource _rewardCalculatorSource = rewardCalculator;
    protected readonly IReceiptStorage _receiptStorage = receiptStorage;
    protected readonly ISpecProvider _specProvider = specProvider;
    protected readonly ILogManager _logManager = logManager;
    protected readonly IBlockPreprocessorStep _recoveryStep = recoveryStep;
    private readonly IReadOnlyDbProvider _dbProvider = dbProvider.AsReadOnly(false);
    protected readonly IReadOnlyBlockTree _blockTree = blockTree.AsReadOnly();

    public override IDebugRpcModule Create()
    {
        IOverridableWorldScope worldStateManager = stateManager.CreateOverridableWorldScope();
        OverridableTxProcessingEnv txEnv = new(worldStateManager, _blockTree, _specProvider, _logManager);

        IReadOnlyTxProcessingScope scope = txEnv.Build(Keccak.EmptyTreeHash);

        ChangeableTransactionProcessorAdapter transactionProcessorAdapter = new(scope.TransactionProcessor); // It want to execute by default. But sometime, it cchange to
        ITransactionProcessorAdapter adapter = transactionProcessorAdapter;
        IBlockProcessor.IBlockTransactionsExecutor transactionsExecutor = CreateBlockTransactionsExecutor(adapter, scope.WorldState);
        ReadOnlyChainProcessingEnv chainProcessingEnv = CreateReadOnlyChainProcessingEnv(scope, worldStateManager, transactionsExecutor);

        GethStyleTracer tracer = new(
            chainProcessingEnv.ChainProcessor,
            scope.WorldState,
            _receiptStorage,
            _blockTree,
            badBlockStore,
            _specProvider,
            transactionProcessorAdapter,
            fileSystem,
            txEnv);

        DebugBridge debugBridge = new(
            configProvider,
            _dbProvider,
            tracer,
            _blockTree,
            _receiptStorage,
            receiptsMigration,
            _specProvider,
            syncModeSelector,
            badBlockStore);

        return new DebugRpcModule(_logManager, debugBridge, jsonRpcConfig, _specProvider, blockchainBridge, secondsPerSlot, _blockTree);
    }

    protected virtual IBlockProcessor.IBlockTransactionsExecutor CreateBlockTransactionsExecutor(ITransactionProcessorAdapter transactionProcessor, IWorldState worldState)
        => new BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, worldState);

    protected virtual ReadOnlyChainProcessingEnv CreateReadOnlyChainProcessingEnv(IReadOnlyTxProcessingScope scope,
        IOverridableWorldScope worldStateManager, IBlockProcessor.IBlockTransactionsExecutor transactionsExecutor)
    {
        return new ReadOnlyChainProcessingEnv(
            scope,
            _blockValidator,
            _recoveryStep,
            _rewardCalculatorSource.Get(scope.TransactionProcessor),
            _receiptStorage,
            _specProvider,
            _blockTree,
            worldStateManager.GlobalStateReader,
            _logManager,
            transactionsExecutor);
    }
}
