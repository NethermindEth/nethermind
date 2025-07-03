// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.Api;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Data;
using Nethermind.Config;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.TxPool.Comparison;

namespace Nethermind.Consensus.AuRa.InitializationSteps;

public class InitializeBlockchainAuRa : InitializeBlockchain
{
    private readonly AuRaNethermindApi _api;
    private INethermindApi NethermindApi => _api;

    private readonly IAuRaBlockProcessorFactory _auRaBlockProcessorFactory;
    private IAbiEncoder _abiEncoder;

    public InitializeBlockchainAuRa(
        AuRaNethermindApi api,
        IAbiEncoder abiEncoder,
        IAuRaBlockProcessorFactory auRaBlockProcessorFactory) : base(api)
    {
        _api = api;
        _abiEncoder = abiEncoder;
        _auRaBlockProcessorFactory = auRaBlockProcessorFactory;
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
        _api.FinalizationManager.SetMainBlockProcessor(_api.MainProcessingContext!.BlockProcessor!);
    }

    protected override BlockProcessor CreateBlockProcessor(BlockCachePreWarmer? preWarmer, ITransactionProcessor transactionProcessor, IWorldState worldState)
    {
        return _auRaBlockProcessorFactory.Create(
            _api.BlockValidator!,
            _api.RewardCalculatorSource!.Get(transactionProcessor),
            new BlockProcessor.BlockValidationTransactionsExecutor(new ExecuteTransactionProcessorAdapter(transactionProcessor), worldState),
            worldState,
            _api.ReceiptStorage!,
            new BeaconBlockRootHandler(transactionProcessor!, worldState),
            transactionProcessor,
            new ExecutionRequestsProcessor(transactionProcessor),
            CreateAuRaValidator(worldState, transactionProcessor),
            preWarmer: preWarmer);
    }


    protected IAuRaValidator CreateAuRaValidator(IWorldState worldState, ITransactionProcessor transactionProcessor)
    {
        if (_api.ChainSpec is null) throw new StepDependencyException(nameof(_api.ChainSpec));
        if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));
        if (_api.EngineSigner is null) throw new StepDependencyException(nameof(_api.EngineSigner));
        if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));
        if (_api.NonceManager is null) throw new StepDependencyException(nameof(_api.NonceManager));

        var chainSpecAuRa = _api.ChainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<AuRaChainSpecEngineParameters>();

        IAuRaValidator validator = new AuRaValidatorFactory(
                _abiEncoder,
                worldState,
                transactionProcessor,
                _api.BlockTree,
                _api.ReadOnlyTxProcessingEnvFactory.Create(),
                _api.ReceiptStorage,
                _api.ValidatorStore,
                _api.FinalizationManager,
                new TxPoolSender(_api.TxPool, new TxSealer(_api.EngineSigner, _api.Timestamper), _api.NonceManager, _api.EthereumEcdsa),
                _api.TxPool,
                NethermindApi.Config<IBlocksConfig>(),
                _api.LogManager,
                _api.EngineSigner,
                _api.SpecProvider,
                _api.GasPriceOracle,
                _api.ReportingContractValidatorCache,
                chainSpecAuRa.PosdaoTransition)
            .CreateValidatorProcessor(chainSpecAuRa.Validators, _api.BlockTree.Head?.Header);

        if (validator is IDisposable disposableValidator)
        {
            _api.DisposeStack.Push(disposableValidator);
        }

        return validator;
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
            new TxFilterAdapter(_api.BlockTree, txPoolFilter, _api.LogManager),
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
