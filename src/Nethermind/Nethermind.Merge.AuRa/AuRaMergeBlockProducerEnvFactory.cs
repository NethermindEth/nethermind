using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Data;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;

namespace Nethermind.Merge.AuRa
{
    public class AuRaMergeBlockProducerEnvFactory : BlockProducerEnvFactory
    {
        private readonly AuRaNethermindApi _auraApi;
        private readonly IAuraConfig _auraConfig;
        private readonly DisposableStack _disposeStack;

        public AuRaMergeBlockProducerEnvFactory(
            AuRaNethermindApi auraApi,
            IAuraConfig auraConfig,
            DisposableStack disposeStack,
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
            IMiningConfig miningConfig,
            ILogManager logManager) : base(
                dbProvider,
                blockTree,
                readOnlyTrieStore,
                specProvider,
                blockValidator,
                rewardCalculatorSource,
                receiptStorage,
                blockPreprocessorStep,
                txPool,
                transactionComparerProvider,
                miningConfig,
                logManager)
        {
            _auraApi = auraApi;
            _auraConfig = auraConfig;
            _disposeStack = disposeStack;
        }

        protected override BlockProcessor CreateBlockProcessor(
            ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv,
            ISpecProvider specProvider,
            IBlockValidator blockValidator,
            IRewardCalculatorSource rewardCalculatorSource,
            IReceiptStorage receiptStorage,
            ILogManager logManager,
            IMiningConfig miningConfig)
        {
            return new AuRaMergeBlockProcessor(
                specProvider,
                blockValidator,
                rewardCalculatorSource.Get(readOnlyTxProcessingEnv.TransactionProcessor),
                TransactionsExecutorFactory.Create(readOnlyTxProcessingEnv),
                readOnlyTxProcessingEnv.StateProvider,
                readOnlyTxProcessingEnv.StorageProvider,
                receiptStorage,
                logManager,
                _blockTree);
        }

        protected override TxPoolTxSource CreateTxPoolTxSource(
            ReadOnlyTxProcessingEnv processingEnv,
            ITxPool txPool,
            IMiningConfig miningConfig,
            ITransactionComparerProvider transactionComparerProvider,
            ILogManager logManager)
        {
            ReadOnlyTxProcessingEnv constantContractsProcessingEnv = CreateReadonlyTxProcessingEnv(
                _dbProvider.AsReadOnly(false),
                _blockTree.AsReadOnly());

            (TxPriorityContract? txPriorityContract, TxPriorityContract.LocalDataSource? localDataSource)
                = TxAuRaFilterBuilders.CreateTxPrioritySources(_auraConfig, _auraApi, constantContractsProcessingEnv);

            DictionaryContractDataStore<TxPriorityContract.Destination>? minGasPricesContractDataStore
                = TxAuRaFilterBuilders.CreateMinGasPricesDataStore(_auraApi, txPriorityContract, localDataSource);

            ITxFilter txSourceFilter = TxAuRaFilterBuilders.CreateAuRaTxFilterForProducer(
                miningConfig,
                _auraApi,
                processingEnv,
                minGasPricesContractDataStore,
                _specProvider);

            ITxFilterPipeline txFilterPipeline = new TxFilterPipelineBuilder(logManager)
                .WithCustomTxFilter(txSourceFilter)
                .WithBaseFeeFilter(_specProvider)
                .Build;

            if (txPriorityContract != null || localDataSource != null)
            {
                _disposeStack.Push(minGasPricesContractDataStore!);

                ContractDataStore<Address> whitelistContractDataStore = new ContractDataStoreWithLocalData<Address>(
                    new HashSetContractDataStoreCollection<Address>(),
                    txPriorityContract?.SendersWhitelist ?? new EmptyDataContract<Address>(),
                    _blockTree,
                    _receiptStorage,
                    logManager,
                    localDataSource?.GetWhitelistLocalDataSource() ?? new EmptyLocalDataSource<IEnumerable<Address>>());

                DictionaryContractDataStore<TxPriorityContract.Destination> prioritiesContractDataStore =
                    new DictionaryContractDataStore<TxPriorityContract.Destination>(
                        new TxPriorityContract.DestinationSortedListContractDataStoreCollection(),
                        txPriorityContract?.Priorities ?? new EmptyDataContract<TxPriorityContract.Destination>(),
                        _blockTree,
                        _receiptStorage,
                        logManager,
                        localDataSource?.GetPrioritiesLocalDataSource());

                _disposeStack.Push(whitelistContractDataStore);
                _disposeStack.Push(prioritiesContractDataStore);

                return new TxPriorityTxSource(
                    txPool,
                    processingEnv.StateReader,
                    logManager,
                    txFilterPipeline,
                    whitelistContractDataStore,
                    prioritiesContractDataStore,
                    _specProvider,
                    transactionComparerProvider);
            }

            return new TxPoolTxSource(
                txPool,
                _specProvider,
                transactionComparerProvider,
                logManager,
                txFilterPipeline);
        }
    }
}
