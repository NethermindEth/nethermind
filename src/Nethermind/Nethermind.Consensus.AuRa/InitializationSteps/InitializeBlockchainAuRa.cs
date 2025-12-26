// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain.Data;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.TxPool.Comparison;

namespace Nethermind.Consensus.AuRa.InitializationSteps;

public class InitializeBlockchainAuRa(AuRaNethermindApi api, IChainHeadInfoProvider chainHeadInfoProvider)
    : InitializeBlockchain(api, chainHeadInfoProvider)
{
    private INethermindApi NethermindApi => api;

    protected override async Task InitBlockchain()
    {
        var chainSpecAuRa = api.ChainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<AuRaChainSpecEngineParameters>();
        api.FinalizationManager = new AuRaBlockFinalizationManager(
            api.BlockTree!,
            api.ChainLevelInfoRepository!,
            api.ValidatorStore!,
            new ValidSealerStrategy(),
            api.LogManager,
            chainSpecAuRa.TwoThirdsMajorityTransition);

        await base.InitBlockchain();

        // Got cyclic dependency. AuRaBlockFinalizationManager -> IAuraValidator -> AuraBlockProcessor -> AuraBlockFinalizationManager.
        api.FinalizationManager.SetMainBlockBranchProcessor(api.MainProcessingContext!.BranchProcessor!);
    }

    private IComparer<Transaction> CreateTxPoolTxComparer(TxPriorityContract? txPriorityContract, TxPriorityContract.LocalDataSource? localDataSource)
    {
        if (txPriorityContract is not null || localDataSource is not null)
        {
            ContractDataStore<Address> whitelistContractDataStore = new ContractDataStoreWithLocalData<Address>(
                new HashSetContractDataStoreCollection<Address>(),
                txPriorityContract?.SendersWhitelist,
                api.BlockTree,
                api.ReceiptFinder,
                api.LogManager,
                localDataSource?.GetWhitelistLocalDataSource() ?? new EmptyLocalDataSource<IEnumerable<Address>>());

            DictionaryContractDataStore<TxPriorityContract.Destination> prioritiesContractDataStore =
                new DictionaryContractDataStore<TxPriorityContract.Destination>(
                    new TxPriorityContract.DestinationSortedListContractDataStoreCollection(),
                    txPriorityContract?.Priorities,
                    api.BlockTree,
                    api.ReceiptFinder,
                    api.LogManager,
                    localDataSource?.GetPrioritiesLocalDataSource());

            api.DisposeStack.Push(whitelistContractDataStore);
            api.DisposeStack.Push(prioritiesContractDataStore);
            IComparer<Transaction> txByPriorityComparer = new CompareTxByPriorityOnHead(whitelistContractDataStore, prioritiesContractDataStore, api.BlockTree);
            IComparer<Transaction> sameSenderNonceComparer = new CompareTxSameSenderNonce(new GasPriceTxComparer(api.BlockTree, api.SpecProvider!), txByPriorityComparer);

            return sameSenderNonceComparer
                .ThenBy(CompareTxByTimestamp.Instance)
                .ThenBy(CompareTxByPoolIndex.Instance)
                .ThenBy(CompareTxByGasLimit.Instance);
        }

        return CreateTxPoolTxComparer();
    }

    protected override TxPool.TxPool CreateTxPool(IChainHeadInfoProvider chainHeadInfoProvider)
    {
        // This has to be different object than the _processingReadOnlyTransactionProcessorSource as this is in separate thread
        TxPriorityContract txPriorityContract = api.TxAuRaFilterBuilders.CreateTxPrioritySources();
        TxPriorityContract.LocalDataSource? localDataSource = api.AuraStatefulComponents.TxPriorityContractLocalDataSource;

        ReportTxPriorityRules(txPriorityContract, localDataSource);

        DictionaryContractDataStore<TxPriorityContract.Destination>? minGasPricesContractDataStore
            = api.TxAuRaFilterBuilders.CreateMinGasPricesDataStore(txPriorityContract, localDataSource);

        ITxFilter txPoolFilter = api.TxAuRaFilterBuilders.CreateAuRaTxFilterForProducer(minGasPricesContractDataStore);

        return new TxPool.TxPool(
            api.EthereumEcdsa!,
            api.BlobTxStorage ?? NullBlobTxStorage.Instance,
            chainHeadInfoProvider,
            NethermindApi.Config<ITxPoolConfig>(),
            api.TxValidator!,
            api.LogManager,
            CreateTxPoolTxComparer(txPriorityContract, localDataSource),
            api.TxGossipPolicy,
            new TxFilterAdapter(api.BlockTree, txPoolFilter, api.LogManager, api.SpecProvider),
            api.HeadTxValidator,
            txPriorityContract is not null || localDataSource is not null);
    }

    private void ReportTxPriorityRules(TxPriorityContract? txPriorityContract, TxPriorityContract.LocalDataSource? localDataSource)
    {
        ILogger logger = api.LogManager.GetClassLogger();

        if (localDataSource?.FilePath is not null)
        {
            if (logger.IsInfo) logger.Info($"Using TxPriority rules from local file: {localDataSource.FilePath}.");
        }

        if (txPriorityContract is not null)
        {
            if (logger.IsInfo) logger.Info($"Using TxPriority rules from contract at address: {txPriorityContract.ContractAddress}.");
        }
    }
}
