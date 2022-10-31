//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Producers
{
    public class BlockProducerEnvFactory : IBlockProducerEnvFactory
    {
        private readonly IDbProvider _dbProvider;
        private readonly IBlockTree _blockTree;
        private readonly IReadOnlyTrieStore _readOnlyTrieStore;
        private readonly ISpecProvider _specProvider;
        private readonly IBlockValidator _blockValidator;
        private readonly IRewardCalculatorSource _rewardCalculatorSource;
        private readonly IReceiptStorage _receiptStorage;
        private readonly IBlockPreprocessorStep _blockPreprocessorStep;
        private readonly ITxPool _txPool;
        private readonly ITransactionComparerProvider _transactionComparerProvider;
        private readonly IBlocksConfig _blocksConfig;
        private readonly ILogManager _logManager;

        public IBlockTransactionsExecutorFactory TransactionsExecutorFactory { get; set; }

        public BlockProducerEnvFactory(
            IDbProvider dbProvider,
            IBlockTree blockTree,
            IReadOnlyTrieStore readOnlyTrieStore,
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
            _dbProvider = dbProvider;
            _blockTree = blockTree;
            _readOnlyTrieStore = readOnlyTrieStore;
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
            ReadOnlyDbProvider readOnlyDbProvider = _dbProvider.AsReadOnly(false);
            ReadOnlyBlockTree readOnlyBlockTree = _blockTree.AsReadOnly();

            ReadOnlyTxProcessingEnv txProcessingEnv =
                CreateReadonlyTxProcessingEnv(readOnlyDbProvider, readOnlyBlockTree);

            BlockProcessor blockProcessor =
                CreateBlockProcessor(txProcessingEnv,
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
                readOnlyDbProvider,
                blockchainProcessor);

            return new BlockProducerEnv
            {
                BlockTree = readOnlyBlockTree,
                ChainProcessor = chainProcessor,
                ReadOnlyStateProvider = txProcessingEnv.StateProvider,
                TxSource = CreateTxSourceForProducer(additionalTxSource, txProcessingEnv, _txPool, _blocksConfig, _transactionComparerProvider, _logManager),
                ReadOnlyTxProcessingEnv = txProcessingEnv
            };
        }

        protected virtual ReadOnlyTxProcessingEnv CreateReadonlyTxProcessingEnv(ReadOnlyDbProvider readOnlyDbProvider, ReadOnlyBlockTree readOnlyBlockTree) =>
            new(readOnlyDbProvider, _readOnlyTrieStore, readOnlyBlockTree, _specProvider, _logManager);

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

        protected virtual BlockProcessor CreateBlockProcessor(ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv,
            ISpecProvider specProvider,
            IBlockValidator blockValidator,
            IRewardCalculatorSource rewardCalculatorSource,
            IReceiptStorage receiptStorage,
            ILogManager logManager, IBlocksConfig blocksConfig) =>
            new(specProvider,
                blockValidator,
                rewardCalculatorSource.Get(readOnlyTxProcessingEnv.TransactionProcessor),
                TransactionsExecutorFactory.Create(readOnlyTxProcessingEnv),
                readOnlyTxProcessingEnv.StateProvider,
                readOnlyTxProcessingEnv.StorageProvider,
                receiptStorage,
                NullWitnessCollector.Instance,
                logManager);
    }
}
