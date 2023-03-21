// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
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
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Trie.Pruning;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Modules.DebugModule
{
    public class DebugModuleFactory : ModuleFactoryBase<IDebugRpcModule>
    {
        private readonly IJsonRpcConfig _jsonRpcConfig;
        private readonly IBlockValidator _blockValidator;
        private readonly IRewardCalculatorSource _rewardCalculatorSource;
        private readonly IReceiptStorage _receiptStorage;
        private readonly IReceiptsMigration _receiptsMigration;
        private readonly IReadOnlyTrieStore _trieStore;
        private readonly IConfigProvider _configProvider;
        private readonly ISpecProvider _specProvider;
        private readonly ILogManager _logManager;
        private readonly IBlockPreprocessorStep _recoveryStep;
        private readonly IReadOnlyDbProvider _dbProvider;
        private readonly IReadOnlyBlockTree _blockTree;
        private readonly ISyncModeSelector _syncModeSelector;
        private ILogger _logger;

        public DebugModuleFactory(
            IDbProvider dbProvider,
            IBlockTree blockTree,
            IJsonRpcConfig jsonRpcConfig,
            IBlockValidator blockValidator,
            IBlockPreprocessorStep recoveryStep,
            IRewardCalculatorSource rewardCalculator,
            IReceiptStorage receiptStorage,
            IReceiptsMigration receiptsMigration,
            IReadOnlyTrieStore trieStore,
            IConfigProvider configProvider,
            ISpecProvider specProvider,
            ISyncModeSelector syncModeSelector,
            ILogManager logManager)
        {
            _dbProvider = dbProvider.AsReadOnly(false);
            _blockTree = blockTree.AsReadOnly();
            _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _recoveryStep = recoveryStep ?? throw new ArgumentNullException(nameof(recoveryStep));
            _rewardCalculatorSource = rewardCalculator ?? throw new ArgumentNullException(nameof(rewardCalculator));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _receiptsMigration = receiptsMigration ?? throw new ArgumentNullException(nameof(receiptsMigration));
            _trieStore = (trieStore ?? throw new ArgumentNullException(nameof(trieStore)));
            _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _syncModeSelector = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
            _logger = logManager.GetClassLogger();
        }

        public override IDebugRpcModule Create()
        {
            ReadOnlyTxProcessingEnv txEnv = new(
                _dbProvider,
                _trieStore,
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
                _dbProvider,
                _specProvider,
                _logManager,
                transactionsExecutor);

            GethStyleTracer tracer = new(
                chainProcessingEnv.ChainProcessor,
                _receiptStorage,
                _blockTree,
                transactionProcessorAdapter);

            DebugBridge debugBridge = new(
                _configProvider,
                _dbProvider,
                tracer,
                _blockTree,
                _receiptStorage,
                _receiptsMigration,
                _specProvider,
                _syncModeSelector);

            return new DebugRpcModule(_logManager, debugBridge, _jsonRpcConfig);
        }
    }
}
