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

public class InitializeBlockchainAuRa : InitializeBlockchain
{
    private readonly AuRaNethermindApi _api;
    private INethermindApi NethermindApi => _api;

    public InitializeBlockchainAuRa(AuRaNethermindApi api, IChainHeadInfoProvider chainHeadInfoProvider) : base(api, chainHeadInfoProvider)
    {
        _api = api;
    }

    protected override async Task InitBlockchain()
    {
        var chainSpecAuRa = _api.ChainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<AuRaChainSpecEngineParameters>();
        _api.FinalizationManager = new AuRaBlockFinalizationManager(
            _api.BlockTree!,
            _api.ChainLevelInfoRepository!,
            _api.ValidatorStore!,
            new ValidSealerStrategy(),
            _api.LogManager,
            chainSpecAuRa.TwoThirdsMajorityTransition);

        await base.InitBlockchain();

        // Got cyclic dependency. AuRaBlockFinalizationManager -> IAuraValidator -> AuraBlockProcessor -> AuraBlockFinalizationManager.
        _api.FinalizationManager.SetMainBlockBranchProcessor(_api.MainProcessingContext!.BranchProcessor!);
    }

    private IComparer<Transaction> CreateTxPoolTxComparer(TxPriorityContract? txPriorityContract, TxPriorityContract.LocalDataSource? localDataSource)
    {
        if (txPriorityContract is not null || localDataSource is not null)
        {
            ContractDataStore<Address> whitelistContractDataStore = new ContractDataStoreWithLocalData<Address>(
                new HashSetContractDataStoreCollection<Address>(),
                txPriorityContract?.SendersWhitelist,
                _api.BlockTree,
                _api.ReceiptFinder,
                _api.LogManager,
                localDataSource?.GetWhitelistLocalDataSource() ?? new EmptyLocalDataSource<IEnumerable<Address>>());

            DictionaryContractDataStore<TxPriorityContract.Destination> prioritiesContractDataStore =
                new DictionaryContractDataStore<TxPriorityContract.Destination>(
                    new TxPriorityContract.DestinationSortedListContractDataStoreCollection(),
                    txPriorityContract?.Priorities,
                    _api.BlockTree,
                    _api.ReceiptFinder,
                    _api.LogManager,
                    localDataSource?.GetPrioritiesLocalDataSource());

            _api.DisposeStack.Push(whitelistContractDataStore);
            _api.DisposeStack.Push(prioritiesContractDataStore);
            IComparer<Transaction> txByPriorityComparer = new CompareTxByPriorityOnHead(whitelistContractDataStore, prioritiesContractDataStore, _api.BlockTree);
            IComparer<Transaction> sameSenderNonceComparer = new CompareTxSameSenderNonce(new GasPriceTxComparer(_api.BlockTree, _api.SpecProvider!), txByPriorityComparer);

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
        TxPriorityContract txPriorityContract = _api.TxAuRaFilterBuilders.CreateTxPrioritySources();
        TxPriorityContract.LocalDataSource? localDataSource = _api.AuraStatefulComponents.TxPriorityContractLocalDataSource;

        ReportTxPriorityRules(txPriorityContract, localDataSource);

        DictionaryContractDataStore<TxPriorityContract.Destination>? minGasPricesContractDataStore
            = _api.TxAuRaFilterBuilders.CreateMinGasPricesDataStore(txPriorityContract, localDataSource);

        ITxFilter txPoolFilter = _api.TxAuRaFilterBuilders.CreateAuRaTxFilterForProducer(minGasPricesContractDataStore);

        return new TxPool.TxPool(
            _api.EthereumEcdsa!,
            _api.BlobTxStorage ?? NullBlobTxStorage.Instance,
            chainHeadInfoProvider,
            NethermindApi.Config<ITxPoolConfig>(),
            _api.TxValidator!,
            _api.LogManager,
            CreateTxPoolTxComparer(txPriorityContract, localDataSource),
            _api.TxGossipPolicy,
            new TxFilterAdapter(_api.BlockTree, txPoolFilter, _api.LogManager, _api.SpecProvider),
            _api.HeadTxValidator,
            txPriorityContract is not null || localDataSource is not null);
    }

    private void ReportTxPriorityRules(TxPriorityContract? txPriorityContract, TxPriorityContract.LocalDataSource? localDataSource)
    {
        ILogger logger = _api.LogManager.GetClassLogger();

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
