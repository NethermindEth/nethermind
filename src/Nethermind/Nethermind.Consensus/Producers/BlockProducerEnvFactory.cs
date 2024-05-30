// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Producers
{
    public class BlockProducerEnvFactory : IBlockProducerEnvFactory
    {
        protected readonly IWorldStateManager _worldStateManager;
        protected readonly IBlockTree _blockTree;
        protected readonly ISpecProvider _specProvider;
        protected readonly IBlockValidator _blockValidator;
        protected readonly IRewardCalculatorSource _rewardCalculatorSource;
        protected readonly IReceiptStorage _receiptStorage;
        protected readonly IBlockPreprocessorStep _blockPreprocessorStep;
        protected readonly ITxPool _txPool;
        protected readonly ITransactionComparerProvider _transactionComparerProvider;
        protected readonly IBlocksConfig _blocksConfig;
        protected readonly ILogManager _logManager;

        public IBlockTransactionsExecutorFactory TransactionsExecutorFactory { get; set; }

        public BlockProducerEnvFactory(
            IWorldStateManager worldStateManager,
            IBlockTree blockTree,
            ISpecProvider specProvider,
            IBlockValidator blockValidator,
            IRewardCalculatorSource rewardCalculatorSource,
            IReceiptStorage receiptStorage,
            IBlockPreprocessorStep blockPreprocessorStep,
            ITxPool txPool,
            ITransactionComparerProvider transactionComparerProvider,
            IBlocksConfig blocksConfig,
            ILogManager logManager)
        {
            _worldStateManager = worldStateManager;
            _blockTree = blockTree;
            _specProvider = specProvider;
            _blockValidator = blockValidator;
            _rewardCalculatorSource = rewardCalculatorSource;
            _receiptStorage = receiptStorage;
            _blockPreprocessorStep = blockPreprocessorStep;
            _txPool = txPool;
            _transactionComparerProvider = transactionComparerProvider;
            _blocksConfig = blocksConfig;
            _logManager = logManager;

            TransactionsExecutorFactory = new BlockProducerTransactionsExecutorFactory(specProvider, logManager);
        }

        public virtual BlockProducerEnv Create(ITxSource? additionalTxSource = null)
        {
            ReadOnlyBlockTree readOnlyBlockTree = _blockTree.AsReadOnly();

            ReadOnlyTxProcessingEnv txProcessingEnv =
                CreateReadonlyTxProcessingEnv(_worldStateManager, readOnlyBlockTree);

            IReadOnlyTxProcessingScope scope = txProcessingEnv.Build(Keccak.EmptyTreeHash);

            BlockProcessor blockProcessor =
                CreateBlockProcessor(
                    scope,
                    _specProvider,
                    _blockValidator,
                    _rewardCalculatorSource,
                    _receiptStorage,
                    _logManager,
                    _blocksConfig);

            IBlockchainProcessor blockchainProcessor =
                new BlockchainProcessor(
                    readOnlyBlockTree,
                    blockProcessor,
                    _blockPreprocessorStep,
                    txProcessingEnv.StateReader,
                    _logManager,
                    BlockchainProcessor.Options.NoReceipts);

            OneTimeChainProcessor chainProcessor = new(
                scope.WorldState,
                blockchainProcessor);

            return new BlockProducerEnv
            {
                BlockTree = readOnlyBlockTree,
                ChainProcessor = chainProcessor,
                ReadOnlyStateProvider = scope.WorldState,
                TxSource = CreateTxSourceForProducer(additionalTxSource, txProcessingEnv, _txPool, _blocksConfig, _transactionComparerProvider, _logManager),
                ReadOnlyTxProcessingEnv = txProcessingEnv
            };
        }

        protected virtual ReadOnlyTxProcessingEnv CreateReadonlyTxProcessingEnv(IWorldStateManager worldStateManager, ReadOnlyBlockTree readOnlyBlockTree) =>
            new(worldStateManager, readOnlyBlockTree, _specProvider, _logManager);

        protected virtual ITxSource CreateTxSourceForProducer(
            ITxSource? additionalTxSource,
            ReadOnlyTxProcessingEnv processingEnv,
            ITxPool txPool,
            IBlocksConfig blocksConfig,
            ITransactionComparerProvider transactionComparerProvider,
            ILogManager logManager)
        {
            TxPoolTxSource txPoolSource = CreateTxPoolTxSource(processingEnv, txPool, blocksConfig, transactionComparerProvider, logManager);
            return additionalTxSource.Then(txPoolSource);
        }

        protected virtual TxPoolTxSource CreateTxPoolTxSource(
            ReadOnlyTxProcessingEnv processingEnv,
            ITxPool txPool,
            IBlocksConfig blocksConfig,
            ITransactionComparerProvider transactionComparerProvider,
            ILogManager logManager)
        {
            ITxFilterPipeline txSourceFilterPipeline = CreateTxSourceFilter(blocksConfig);
            return new TxPoolTxSource(txPool, _specProvider, transactionComparerProvider, logManager, txSourceFilterPipeline);
        }

        protected virtual ITxFilterPipeline CreateTxSourceFilter(IBlocksConfig blocksConfig) =>
            TxFilterPipelineBuilder.CreateStandardFilteringPipeline(_logManager, _specProvider, blocksConfig);

        protected virtual BlockProcessor CreateBlockProcessor(
            IReadOnlyTxProcessingScope readOnlyTxProcessingEnv,
            ISpecProvider specProvider,
            IBlockValidator blockValidator,
            IRewardCalculatorSource rewardCalculatorSource,
            IReceiptStorage receiptStorage,
            ILogManager logManager, IBlocksConfig blocksConfig) =>
            new(specProvider,
                blockValidator,
                rewardCalculatorSource.Get(readOnlyTxProcessingEnv.TransactionProcessor),
                TransactionsExecutorFactory.Create(readOnlyTxProcessingEnv),
                readOnlyTxProcessingEnv.WorldState,
                receiptStorage,
                new BlockhashStore(_blockTree, _specProvider, readOnlyTxProcessingEnv.WorldState),
                logManager,
                new BlockProductionWithdrawalProcessor(new WithdrawalProcessor(readOnlyTxProcessingEnv.WorldState, logManager)));

    }
}
