// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Data;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Consensus.AuRa;

/// <summary>
/// Creates the AuRa tx-pool-backed tx source, using the tx priority contract or local data source when configured.
/// </summary>
internal sealed class AuRaTxPoolTxSourceFactory(
    TxAuRaFilterBuilders txAuRaFilterBuilders,
    AuraStatefulComponents auraStatefulComponents,
    IBlockTree blockTree,
    IReceiptStorage receiptStorage,
    ITxPool txPool,
    ISpecProvider specProvider,
    ITransactionComparerProvider transactionComparerProvider,
    IBlocksConfig blocksConfig,
    IDisposableStack disposeStack,
    ILogManager logManager) : IBlockProducerTxSourceFactory
{
    public ITxSource Create()
    {
        TxPriorityContract? txPriorityContract = txAuRaFilterBuilders.CreateTxPrioritySources();
        TxPriorityContract.LocalDataSource? localDataSource = auraStatefulComponents.TxPriorityContractLocalDataSource;

        if (txPriorityContract is not null || localDataSource is not null)
        {
            DictionaryContractDataStore<TxPriorityContract.Destination> minGasPricesContractDataStore =
                txAuRaFilterBuilders.CreateMinGasPricesDataStore(txPriorityContract, localDataSource)!;
            disposeStack.Push(minGasPricesContractDataStore);

            ContractDataStore<Address> whitelistContractDataStore = new ContractDataStoreWithLocalData<Address>(
                new HashSetContractDataStoreCollection<Address>(),
                txPriorityContract?.SendersWhitelist,
                blockTree,
                receiptStorage,
                logManager,
                localDataSource?.GetWhitelistLocalDataSource() ?? new EmptyLocalDataSource<IEnumerable<Address>>());

            DictionaryContractDataStore<TxPriorityContract.Destination> prioritiesContractDataStore =
                new(
                    new TxPriorityContract.DestinationSortedListContractDataStoreCollection(),
                    txPriorityContract?.Priorities,
                    blockTree,
                    receiptStorage,
                    logManager,
                    localDataSource?.GetPrioritiesLocalDataSource());

            disposeStack.Push(whitelistContractDataStore);
            disposeStack.Push(prioritiesContractDataStore);

            ITxFilter auraTxFilter =
                txAuRaFilterBuilders.CreateAuRaTxFilterForProducer(minGasPricesContractDataStore);
            ITxFilterPipeline txFilterPipeline = new TxFilterPipelineBuilder(logManager)
                .WithCustomTxFilter(auraTxFilter)
                .WithBaseFeeFilter()
                .WithNullTxFilter()
                .Build;

            return new TxPriorityTxSource(
                txPool,
                logManager,
                txFilterPipeline,
                whitelistContractDataStore,
                prioritiesContractDataStore,
                specProvider,
                transactionComparerProvider,
                blocksConfig);
        }
        else
        {
            ITxFilter txSourceFilter = txAuRaFilterBuilders.CreateAuRaTxFilterForProducer(null);
            ITxFilterPipeline txFilterPipeline = new TxFilterPipelineBuilder(logManager)
                .WithCustomTxFilter(txSourceFilter)
                .WithBaseFeeFilter()
                .WithHeadTxFilter()
                .Build;

            return new TxPoolTxSource(txPool, specProvider, transactionComparerProvider, logManager, txFilterPipeline, blocksConfig);
        }
    }
}
