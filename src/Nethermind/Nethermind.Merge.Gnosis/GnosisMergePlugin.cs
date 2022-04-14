using Nethermind.Merge.Plugin;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Processing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Data;
using System.Collections.Generic;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.State;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Merge.Gnosis
{
    public class GnosisMergePlugin : MergePlugin
    {
        protected override bool MatchChain(ChainSpec chainSpec)
        {
            return chainSpec.ChainId == ChainId.xDai;
        }

        protected override void InitRewardCalculatorSource() { }

        protected override ITxSource? CreateTxSource(IStateProvider stateProvider)
        {
            ReadOnlyTxProcessingEnv txProcessingEnv = new(
                _api.DbProvider!.AsReadOnly(false),
                _api.ReadOnlyTrieStore,
                _api.BlockTree!.AsReadOnly(),
                _api.SpecProvider,
                _api.LogManager
            );

            // We need special one for TxPriority as its following Head separately with events and we want rules from Head, not produced block
            IReadOnlyTxProcessorSource readOnlyTxProcessorSourceForTxPriority =
                new ReadOnlyTxProcessingEnv(_api.DbProvider, _api.ReadOnlyTrieStore, _api.BlockTree, _api.SpecProvider, _api.LogManager);

            (TxPriorityContract? _txPriorityContract, TxPriorityContract.LocalDataSource? _localDataSource) =
                TxAuRaFilterBuilders.CreateTxPrioritySources(
                    _api.Config<IAuraConfig>(),
                    (AuRaNethermindApi)_api,
                    readOnlyTxProcessorSourceForTxPriority
                );
            DictionaryContractDataStore<TxPriorityContract.Destination>? minGasPricesContractDataStore = null;

            if (_txPriorityContract != null || _localDataSource != null)
            {
                minGasPricesContractDataStore = TxAuRaFilterBuilders.CreateMinGasPricesDataStore(
                    (AuRaNethermindApi)_api,
                    _txPriorityContract,
                    _localDataSource
                );
                _api.DisposeStack.Push(minGasPricesContractDataStore!);

                ContractDataStore<Address> whitelistContractDataStore = new ContractDataStoreWithLocalData<Address>(
                    new HashSetContractDataStoreCollection<Address>(),
                    _txPriorityContract?.SendersWhitelist!,
                    _api.BlockTree!,
                    _api.ReceiptFinder!,
                    _api.LogManager,
                    _localDataSource?.GetWhitelistLocalDataSource() ?? new EmptyLocalDataSource<IEnumerable<Address>>()
                );

                DictionaryContractDataStore<TxPriorityContract.Destination> prioritiesContractDataStore =
                    new DictionaryContractDataStore<TxPriorityContract.Destination>(
                        new TxPriorityContract.DestinationSortedListContractDataStoreCollection(),
                        _txPriorityContract?.Priorities!,
                        _api.BlockTree!,
                        _api.ReceiptFinder!,
                        _api.LogManager,
                        _localDataSource?.GetPrioritiesLocalDataSource()!
                    );

                _api.DisposeStack.Push(whitelistContractDataStore);
                _api.DisposeStack.Push(prioritiesContractDataStore);

                ITxFilter auraTxFilter = TxAuRaFilterBuilders.CreateAuRaTxFilterForProducer(
                    _api.Config<IMiningConfig>(),
                    (AuRaNethermindApi)_api,
                    txProcessingEnv,
                    minGasPricesContractDataStore,
                    _api.SpecProvider!
                );
                ITxFilterPipeline txFilterPipeline = new TxFilterPipelineBuilder(_api.LogManager)
                    .WithCustomTxFilter(auraTxFilter)
                    .WithBaseFeeFilter(_api.SpecProvider!)
                    .WithNullTxFilter()
                    .Build;


                return new TxPriorityTxSource(
                    _api.TxPool!,
                    _api.StateReader!,
                    _api.LogManager,
                    txFilterPipeline,
                    whitelistContractDataStore,
                    prioritiesContractDataStore,
                    _api.SpecProvider!,
                    _api.TransactionComparerProvider!
                );
            }
            else
            {
                ITxFilter auraTxFilter = TxAuRaFilterBuilders.CreateAuRaTxFilterForProducer(
                    _api.Config<IMiningConfig>(),
                    (AuRaNethermindApi)_api,
                    txProcessingEnv,
                    minGasPricesContractDataStore,
                    _api.SpecProvider!
                );
                ITxFilterPipeline txFilterPipeline = new TxFilterPipelineBuilder(_api.LogManager)
                    .WithCustomTxFilter(auraTxFilter)
                    .WithBaseFeeFilter(_api.SpecProvider!)
                    .Build;
                return new TxPoolTxSource(
                    _api.TxPool,
                    _api.SpecProvider,
                    _api.TransactionComparerProvider,
                    _api.LogManager,
                    txFilterPipeline
                );
            }
        }
    }
}
