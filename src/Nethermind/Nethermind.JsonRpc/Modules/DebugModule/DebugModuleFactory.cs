// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Tracing;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Trie.Pruning;

namespace Nethermind.JsonRpc.Modules.DebugModule;

public class DebugModuleFactory : ModuleFactoryBase<IDebugRpcModule>
{
    private readonly IWorldStateManager _worldStateManager;
    private readonly IJsonRpcConfig _jsonRpcConfig;
    private readonly IBlockValidator _blockValidator;
    private readonly IRewardCalculatorSource _rewardCalculatorSource;
    private readonly IReceiptStorage _receiptStorage;
    private readonly IReceiptsMigration _receiptsMigration;
    private readonly IConfigProvider _configProvider;
    private readonly ISpecProvider _specProvider;
    private readonly ILogManager _logManager;
    private readonly IBlockPreprocessorStep _recoveryStep;
    private readonly IReadOnlyDbProvider _dbProvider;
    private readonly IReadOnlyBlockTree _blockTree;
    private readonly ISyncModeSelector _syncModeSelector;
    private readonly IBlockStore _badBlockStore;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger _logger;

    public DebugModuleFactory(
        IWorldStateManager worldStateManager,
        IDbProvider dbProvider,
        IBlockTree blockTree,
        IJsonRpcConfig jsonRpcConfig,
        IBlockValidator blockValidator,
        IBlockPreprocessorStep recoveryStep,
        IRewardCalculatorSource rewardCalculator,
        IReceiptStorage receiptStorage,
        IReceiptsMigration receiptsMigration,
        IConfigProvider configProvider,
        ISpecProvider specProvider,
        ISyncModeSelector syncModeSelector,
        IBlockStore badBlockStore,
        IFileSystem fileSystem,
        ILogManager logManager)
    {
        _worldStateManager = worldStateManager;
        _dbProvider = dbProvider.AsReadOnly(false);
        _blockTree = blockTree.AsReadOnly();
        _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));
        _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
        _recoveryStep = recoveryStep ?? throw new ArgumentNullException(nameof(recoveryStep));
        _rewardCalculatorSource = rewardCalculator ?? throw new ArgumentNullException(nameof(rewardCalculator));
        _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
        _receiptsMigration = receiptsMigration ?? throw new ArgumentNullException(nameof(receiptsMigration));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        _syncModeSelector = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
        _badBlockStore = badBlockStore;
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logManager.GetClassLogger();
    }

    public override IDebugRpcModule Create()
    {
        ReadOnlyTxProcessingEnv txEnv = new(
            _worldStateManager,
            _blockTree,
            _specProvider,
            _logManager);

        ChangeableTransactionProcessorAdapter transactionProcessorAdapter = new(txEnv.TransactionProcessor);
        BlockProcessor.BlockValidationTransactionsExecutor transactionsExecutor = new(transactionProcessorAdapter, txEnv.StateProvider);
        ReadOnlyChainProcessingEnv chainProcessingEnv = new(
            txEnv,
            _blockValidator,
            _recoveryStep,
            _rewardCalculatorSource.Get(txEnv.TransactionProcessor),
            _receiptStorage,
            _specProvider,
            _logManager,
            transactionsExecutor);

        GethStyleTracer tracer = new(
            chainProcessingEnv.ChainProcessor,
            chainProcessingEnv.StateProvider,
            _receiptStorage,
            _blockTree,
            _specProvider,
            transactionProcessorAdapter,
            _fileSystem);

        DebugBridge debugBridge = new(
            _configProvider,
            _dbProvider,
            tracer,
            _blockTree,
            _receiptStorage,
            _receiptsMigration,
            _specProvider,
            _syncModeSelector,
            _badBlockStore);

        return new DebugRpcModule(_logManager, debugBridge, _jsonRpcConfig, _specProvider);
    }
}
