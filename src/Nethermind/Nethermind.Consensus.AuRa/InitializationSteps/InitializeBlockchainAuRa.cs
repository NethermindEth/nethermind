// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Data;
using Nethermind.Config;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.AuRa.Withdrawals;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.TxPool;
using Nethermind.TxPool.Comparison;

namespace Nethermind.Consensus.AuRa.InitializationSteps;

public class InitializeBlockchainAuRa : InitializeBlockchain
{
    private readonly AuRaNethermindApi _api;
    private INethermindApi NethermindApi => _api;

    private readonly IAuraConfig _auraConfig;

    public InitializeBlockchainAuRa(AuRaNethermindApi api) : base(api)
    {
        _api = api;
        _auraConfig = NethermindApi.Config<IAuraConfig>();
    }

    protected ReadOnlyTxProcessingEnv CreateReadOnlyTransactionProcessorSource() =>
        new ReadOnlyTxProcessingEnv(_api.DbProvider, _api.ReadOnlyTrieStore, _api.BlockTree, _api.SpecProvider, _api.LogManager);

    // private IReadOnlyTransactionProcessorSource GetReadOnlyTransactionProcessorSource() =>
    //     _readOnlyTransactionProcessorSource ??= new ReadOnlyTxProcessorSource(
    //         _api.DbProvider, _api.ReadOnlyTrieStore, _api.BlockTree, _api.SpecProvider, _api.LogManager);

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

    protected override TxPool.TxPool CreateTxPool()
    {
        // This has to be different object than the _processingReadOnlyTransactionProcessorSource as this is in separate thread
        var txPoolReadOnlyTransactionProcessorSource = CreateReadOnlyTransactionProcessorSource();
        var (txPriorityContract, localDataSource) = TxAuRaFilterBuilders.CreateTxPrioritySources(_auraConfig, _api, txPoolReadOnlyTransactionProcessorSource!);

        ReportTxPriorityRules(txPriorityContract, localDataSource);

        var minGasPricesContractDataStore = TxAuRaFilterBuilders.CreateMinGasPricesDataStore(_api, txPriorityContract, localDataSource);

        ITxFilter txPoolFilter = TxAuRaFilterBuilders.CreateAuRaTxFilterForProducer(
            NethermindApi.Config<IBlocksConfig>(),
            _api,
            txPoolReadOnlyTransactionProcessorSource,
            minGasPricesContractDataStore,
            _api.SpecProvider);

        return new TxPool.TxPool(
            _api.EthereumEcdsa,
            _api.BlobTxStorage ?? NullBlobTxStorage.Instance,
            new ChainHeadInfoProvider(_api.SpecProvider, _api.BlockTree, _api.StateReader),
            NethermindApi.Config<ITxPoolConfig>(),
            _api.TxValidator,
            _api.LogManager,
            CreateTxPoolTxComparer(txPriorityContract, localDataSource),
            _api.TxGossipPolicy,
            new TxFilterAdapter(_api.BlockTree, txPoolFilter, _api.LogManager),
            txPriorityContract is not null || localDataSource is not null);
    }

    private void ReportTxPriorityRules(TxPriorityContract? txPriorityContract, TxPriorityContract.LocalDataSource? localDataSource)
    {
        ILogger? logger = _api.LogManager.GetClassLogger();

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

public class AuraHeaderValidatorFactory
{
    private ChainSpec _chainSpec;
    private ISealValidator _sealValidator;

    private readonly BlockTree _blockTree;
    private readonly ISpecProvider _specProvider;
    private readonly ILogManager _logManager;

    public AuraHeaderValidatorFactory(
        ChainSpec chainSpec,
        BlockTree blockTree,
        ISealValidator sealValidator,
        ISpecProvider specProvider,
        ILogManager logManager
    )
    {
        _chainSpec = chainSpec;
        _blockTree = blockTree;
        _sealValidator = sealValidator;
        _specProvider = specProvider;
        _logManager = logManager;
    }

    public IHeaderValidator CreateHeaderValidator()
    {
        if (_chainSpec is null) throw new StepDependencyException(nameof(_chainSpec));
        var blockGasLimitContractTransitions = _chainSpec.AuRa.BlockGasLimitContractTransitions;
        return blockGasLimitContractTransitions?.Any() == true
            ? new AuRaHeaderValidator(
                _blockTree,
                _sealValidator,
                _specProvider,
                _logManager,
                blockGasLimitContractTransitions.Keys.ToArray())
            : new HeaderValidator(
                _blockTree,
                _sealValidator,
                _specProvider,
                _logManager);
    }
}

/// <summary>
/// A class used to aid migrating to DI. Really, I've given up here, and just copy paste things.
/// </summary>
public class AuraBlockchainStack
{
    protected readonly AuRaNethermindApi _api;
    private readonly IAuraConfig _auraConfig;
    protected readonly ITransactionProcessor _transactionProcessor;

    public AuraBlockchainStack(
        INethermindApi api,
        ITransactionProcessor transactionProcessor
    )
    {
        _api = (AuRaNethermindApi) api;
        _auraConfig = api.Config<IAuraConfig>();
        _transactionProcessor = transactionProcessor;
    }

    public BlockProcessor CreateBlockProcessor()
    {
        if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));
        if (_api.ChainHeadStateProvider is null) throw new StepDependencyException(nameof(_api.ChainHeadStateProvider));
        if (_api.BlockValidator is null) throw new StepDependencyException(nameof(_api.BlockValidator));
        if (_api.RewardCalculatorSource is null) throw new StepDependencyException(nameof(_api.RewardCalculatorSource));
        if (_api.DbProvider is null) throw new StepDependencyException(nameof(_api.DbProvider));
        if (_api.WorldState is null) throw new StepDependencyException(nameof(_api.WorldState));
        if (_api.TxPool is null) throw new StepDependencyException(nameof(_api.TxPool));
        if (_api.ReceiptStorage is null) throw new StepDependencyException(nameof(_api.ReceiptStorage));
        if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));
        if (_api.GasPriceOracle is null) throw new StepDependencyException(nameof(_api.GasPriceOracle));
        if (_api.ChainSpec is null) throw new StepDependencyException(nameof(_api.ChainSpec));

        var processingReadOnlyTransactionProcessorSource = CreateReadOnlyTransactionProcessorSource();
        var txPermissionFilterOnlyTxProcessorSource = CreateReadOnlyTransactionProcessorSource();
        ITxFilter auRaTxFilter = TxAuRaFilterBuilders.CreateAuRaTxFilter(
            _api,
            txPermissionFilterOnlyTxProcessorSource,
            _api.SpecProvider,
            new ServiceTxFilter(_api.SpecProvider));

        IDictionary<long, IDictionary<Address, byte[]>> rewriteBytecode = _api.ChainSpec.AuRa.RewriteBytecode;
        ContractRewriter? contractRewriter = rewriteBytecode?.Count > 0 ? new ContractRewriter(rewriteBytecode) : null;

        var processor = (AuRaBlockProcessor)NewBlockProcessor(_api, auRaTxFilter, contractRewriter);

        var auRaValidator = CreateAuRaValidator(processor, processingReadOnlyTransactionProcessorSource);
        processor.AuRaValidator = auRaValidator;
        var reportingValidator = auRaValidator.GetReportingValidator();
        _api.ReportingValidator = reportingValidator;

        var sealValidator = _api.Container.Resolve<ISealValidator>();
        if (sealValidator is AuRaSealValidator auRaSealValidator)
        {
            auRaSealValidator.ReportingValidator = reportingValidator;
        }

        return processor;
    }

    protected virtual BlockProcessor NewBlockProcessor(AuRaNethermindApi api, ITxFilter txFilter, ContractRewriter contractRewriter)
    {
        return new AuRaBlockProcessor(
            _api.SpecProvider,
            _api.BlockValidator,
            _api.RewardCalculatorSource.Get(_transactionProcessor),
            new BlockProcessor.BlockValidationTransactionsExecutor(_transactionProcessor, _api.WorldState),
            _api.WorldState,
            _api.ReceiptStorage,
            _api.LogManager,
            _api.BlockTree,
            NullWithdrawalProcessor.Instance,
            txFilter,
            GetGasLimitCalculator(),
            contractRewriter
        );
    }

    private IAuRaValidator CreateAuRaValidator(IBlockProcessor processor, IReadOnlyTxProcessorSource readOnlyTxProcessorSource)
    {
        if (_api.ChainSpec is null) throw new StepDependencyException(nameof(_api.ChainSpec));
        if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));
        if (_api.EngineSigner is null) throw new StepDependencyException(nameof(_api.EngineSigner));
        if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));
        if (_api.NonceManager is null) throw new StepDependencyException(nameof(_api.NonceManager));

        var chainSpecAuRa = _api.ChainSpec.AuRa;

        _api.FinalizationManager = new AuRaBlockFinalizationManager(
            _api.BlockTree,
            _api.ChainLevelInfoRepository,
            processor,
            _api.ValidatorStore,
            new ValidSealerStrategy(),
            _api.LogManager,
            chainSpecAuRa.TwoThirdsMajorityTransition);

        IAuRaValidator validator = new AuRaValidatorFactory(_api.AbiEncoder,
                _api.WorldState,
                _transactionProcessor,
                _api.BlockTree,
                readOnlyTxProcessorSource,
                _api.ReceiptStorage,
                _api.ValidatorStore,
                _api.FinalizationManager,
                new TxPoolSender(_api.TxPool, new TxSealer(_api.EngineSigner, _api.Timestamper), _api.NonceManager, _api.EthereumEcdsa),
                _api.TxPool,
                _api.Config<IBlocksConfig>(),
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

    public AuRaContractGasLimitOverride? GetGasLimitCalculator()
    {
        if (_api.ChainSpec is null) throw new StepDependencyException(nameof(_api.ChainSpec));
        var blockGasLimitContractTransitions = _api.ChainSpec.AuRa.BlockGasLimitContractTransitions;

        if (blockGasLimitContractTransitions?.Any() == true)
        {
            _api.GasLimitCalculatorCache = new AuRaContractGasLimitOverride.Cache();

            AuRaContractGasLimitOverride gasLimitCalculator = new(
                blockGasLimitContractTransitions.Select(blockGasLimitContractTransition =>
                        new BlockGasLimitContract(
                            _api.AbiEncoder,
                            blockGasLimitContractTransition.Value,
                            blockGasLimitContractTransition.Key,
                            CreateReadOnlyTransactionProcessorSource()))
                    .ToArray<IBlockGasLimitContract>(),
                _api.GasLimitCalculatorCache,
                _auraConfig.Minimum2MlnGasPerBlockWhenUsingBlockGasLimitContract,
                new TargetAdjustedGasLimitCalculator(_api.SpecProvider, _api.Config<IBlocksConfig>()),
                _api.LogManager);

            return gasLimitCalculator;
        }

        // do not return target gas limit calculator here - this is used for validation to check if the override should have been used
        return null;
    }

    protected ReadOnlyTxProcessingEnv CreateReadOnlyTransactionProcessorSource() =>
        new ReadOnlyTxProcessingEnv(_api.DbProvider, _api.ReadOnlyTrieStore, _api.BlockTree, _api.SpecProvider, _api.LogManager);

}
