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

using Nethermind.Blockchain.Comparers;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;

namespace Nethermind.Blockchain
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
        private readonly IMiningConfig _miningConfig;
        private readonly ILogManager _logManager;

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
            IMiningConfig miningConfig,
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
            _miningConfig = miningConfig;
            _logManager = logManager;
        }
        
        public BlockProducerEnv Create(ITxSource? additionalTxSource = null)
        {
            ReadOnlyDbProvider readOnlyDbProvider = _dbProvider.AsReadOnly(false);
            ReadOnlyBlockTree readOnlyBlockTree = _blockTree.AsReadOnly();

            ReadOnlyTxProcessingEnv txProcessingEnv =
                new(readOnlyDbProvider, _readOnlyTrieStore, readOnlyBlockTree, _specProvider, _logManager);
                
            BlockProcessor blockProcessor =
                CreateBlockProcessor(txProcessingEnv, 
                    _specProvider, 
                    _blockValidator, 
                    _rewardCalculatorSource, 
                    _receiptStorage, 
                    _logManager);

            IBlockchainProcessor blockchainProcessor =
                new BlockchainProcessor(
                    readOnlyBlockTree,
                    blockProcessor,
                    _blockPreprocessorStep,
                    _logManager,
                    BlockchainProcessor.Options.NoReceipts);

            OneTimeChainProcessor chainProcessor = new(
                readOnlyDbProvider,
                blockchainProcessor);

            TransactionComparerProvider transactionComparerProvider = new(_specProvider, readOnlyBlockTree);
            
            return new BlockProducerEnv
            {
                ChainProcessor = chainProcessor,
                ReadOnlyStateProvider = txProcessingEnv.StateProvider,
                TxSource = GetTxSource(additionalTxSource, txProcessingEnv, _txPool, _miningConfig, transactionComparerProvider, _logManager),
                ReadOnlyTxProcessingEnv = txProcessingEnv
            };
        }
        
        protected virtual ITxSource GetTxSource(
            ITxSource? additionalTxSource, 
            ReadOnlyTxProcessingEnv txProcessingEnv, 
            ITxPool txPool, 
            IMiningConfig miningConfig,
            ITransactionComparerProvider transactionComparerProvider,
            ILogManager logManager)
        {
            ITxSource txSourceForProducer = CreateTxSourceForProducer(txProcessingEnv, txPool, miningConfig, transactionComparerProvider, logManager);
            return additionalTxSource.Then(txSourceForProducer); 
        }

        protected virtual ITxSource CreateTxSourceForProducer(
            ReadOnlyTxProcessingEnv processingEnv, 
            ITxPool txPool, 
            IMiningConfig miningConfig, 
            ITransactionComparerProvider transactionComparerProvider, 
            ILogManager logManager) =>
            CreateTxPoolTxSource(processingEnv, txPool, miningConfig, transactionComparerProvider, logManager);

        protected virtual TxPoolTxSource CreateTxPoolTxSource(
            ReadOnlyTxProcessingEnv processingEnv, 
            ITxPool txPool, 
            IMiningConfig miningConfig, 
            ITransactionComparerProvider transactionComparerProvider, 
            ILogManager logManager)
        {
            ITxFilterPipeline txSourceFilterPipeline  = CreateTxSourceFilter(miningConfig);
            return new TxPoolTxSource(txPool, _specProvider, transactionComparerProvider, logManager, txSourceFilterPipeline);
        }

        protected virtual ITxFilterPipeline CreateTxSourceFilter(IMiningConfig miningConfig) =>
            TxFilterPipelineBuilder.CreateStandardFilteringPipeline(_logManager, _specProvider, miningConfig);

        protected virtual BlockProcessor CreateBlockProcessor(
            ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv,
            ISpecProvider specProvider, 
            IBlockValidator blockValidator, 
            IRewardCalculatorSource rewardCalculatorSource, 
            IReceiptStorage receiptStorage, 
            ILogManager logManager) =>
            new(specProvider,
                blockValidator,
                rewardCalculatorSource.Get(readOnlyTxProcessingEnv.TransactionProcessor),
                new BlockProcessor.BlockProductionTransactionsExecutor(readOnlyTxProcessingEnv, specProvider, logManager),
                readOnlyTxProcessingEnv.StateProvider,
                readOnlyTxProcessingEnv.StorageProvider,
                receiptStorage,
                NullWitnessCollector.Instance,
                logManager);
    }
}
