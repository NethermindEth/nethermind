// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Abi;
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
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.Consensus.AuRa.InitializationSteps;

public class StartBlockProducerAuRa
{
    private readonly AuRaNethermindApi _api;

    private BlockProducerEnv? _blockProducerContext;
    private INethermindApi NethermindApi => _api;

    private readonly IAuraConfig _auraConfig;
    private IAuRaValidator? _validator;
    private DictionaryContractDataStore<TxPriorityContract.Destination>? _minGasPricesContractDataStore;
    private TxPriorityContract? _txPriorityContract;
    private TxPriorityContract.LocalDataSource? _localDataSource;
    private IAuRaStepCalculator? _stepCalculator;

    public StartBlockProducerAuRa(AuRaNethermindApi api)
    {
        _api = api;
        _auraConfig = NethermindApi.Config<IAuraConfig>();
    }

    private IAuRaStepCalculator StepCalculator
    {
        get
        {
            return _stepCalculator ??= new AuRaStepCalculator(_api.ChainSpec.AuRa.StepDuration, _api.Timestamper, _api.LogManager);
        }
    }

    public IBlockProductionTrigger CreateTrigger()
    {
        BuildBlocksOnAuRaSteps onAuRaSteps = new(StepCalculator, _api.LogManager);
        BuildBlocksOnlyWhenNotProcessing onlyWhenNotProcessing = new(
            onAuRaSteps,
            _api.BlockProcessingQueue,
            _api.BlockTree,
            _api.LogManager,
            !_auraConfig.AllowAuRaPrivateChains);

        _api.DisposeStack.Push((IAsyncDisposable)onlyWhenNotProcessing);

        return onlyWhenNotProcessing;
    }

    public Task<IBlockProducer> BuildProducer(IBlockProductionTrigger blockProductionTrigger, ITxSource? additionalTxSource = null)
    {
        if (_api.EngineSigner is null) throw new StepDependencyException(nameof(_api.EngineSigner));
        if (_api.ChainSpec is null) throw new StepDependencyException(nameof(_api.ChainSpec));

        ILogger logger = _api.LogManager.GetClassLogger();
        if (logger.IsInfo) logger.Info("Starting AuRa block producer & sealer");

        BlockProducerEnv producerEnv = GetProducerChain(additionalTxSource);

        IGasLimitCalculator gasLimitCalculator = _api.AuraGasLimitCalculator = CreateGasLimitCalculator(_api);

        IBlockProducer blockProducer = new AuRaBlockProducer(
            producerEnv.TxSource,
            producerEnv.ChainProcessor,
            blockProductionTrigger,
            producerEnv.ReadOnlyStateProvider,
            _api.Sealer,
            _api.BlockTree,
            _api.Timestamper,
            StepCalculator,
            _api.ReportingValidator,
            _auraConfig,
            gasLimitCalculator,
            _api.SpecProvider,
            _api.LogManager,
            _api.ConfigProvider.GetConfig<IBlocksConfig>());

        return Task.FromResult(blockProducer);
    }

    private BlockProcessor CreateBlockProcessor(ReadOnlyTxProcessingEnv changeableTxProcessingEnv)
    {
        if (_api.RewardCalculatorSource is null) throw new StepDependencyException(nameof(_api.RewardCalculatorSource));
        if (_api.ValidatorStore is null) throw new StepDependencyException(nameof(_api.ValidatorStore));
        if (_api.ChainSpec is null) throw new StepDependencyException(nameof(_api.ChainSpec));
        if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));
        if (_api.EngineSigner is null) throw new StepDependencyException(nameof(_api.EngineSigner));
        if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));
        if (_api.GasPriceOracle is null) throw new StepDependencyException(nameof(_api.GasPriceOracle));

        var chainSpecAuRa = _api.ChainSpec.AuRa;

        ITxFilter auRaTxFilter = TxAuRaFilterBuilders.CreateAuRaTxFilter(
            _api,
            new LocalTxFilter(_api.EngineSigner));

        _validator = new AuRaValidatorFactory(_api.AbiEncoder,
                changeableTxProcessingEnv.StateProvider,
                changeableTxProcessingEnv.TransactionProcessor,
                changeableTxProcessingEnv.BlockTree,
                _api.CreateReadOnlyTransactionProcessorSource(),
                _api.ReceiptStorage,
                _api.ValidatorStore,
                _api.FinalizationManager,
                NullTxSender.Instance,
                NullTxPool.Instance,
                NethermindApi.Config<IBlocksConfig>(),
                _api.LogManager,
                _api.EngineSigner,
                _api.SpecProvider,
                _api.GasPriceOracle,
                _api.ReportingContractValidatorCache,
                chainSpecAuRa.PosdaoTransition,
                true)
            .CreateValidatorProcessor(chainSpecAuRa.Validators, _api.BlockTree.Head?.Header);

        if (_validator is IDisposable disposableValidator)
        {
            _api.DisposeStack.Push(disposableValidator);
        }

        IDictionary<long, IDictionary<Address, byte[]>> rewriteBytecode = chainSpecAuRa.RewriteBytecode;
        ContractRewriter? contractRewriter = rewriteBytecode?.Count > 0 ? new ContractRewriter(rewriteBytecode) : null;

        return new AuRaBlockProcessor(
            _api.SpecProvider,
            _api.BlockValidator,
            _api.RewardCalculatorSource.Get(changeableTxProcessingEnv.TransactionProcessor),
            _api.BlockProducerEnvFactory.TransactionsExecutorFactory.Create(changeableTxProcessingEnv),
            changeableTxProcessingEnv.StateProvider,
            _api.ReceiptStorage,
            _api.LogManager,
            changeableTxProcessingEnv.BlockTree,
            NullWithdrawalProcessor.Instance,
            _validator,
            auRaTxFilter,
            CreateGasLimitCalculator(_api) as AuRaContractGasLimitOverride,
            contractRewriter);
    }

    internal TxPoolTxSource CreateTxPoolTxSource(ReadOnlyTxProcessingEnv processingEnv)
    {
        _txPriorityContract = TxAuRaFilterBuilders.CreateTxPrioritySources(_api);
        _localDataSource = _api.TxPriorityContractLocalDataSource;

        if (_txPriorityContract is not null || _localDataSource is not null)
        {
            _minGasPricesContractDataStore = TxAuRaFilterBuilders.CreateMinGasPricesDataStore(_api, _txPriorityContract, _localDataSource)!;
            _api.DisposeStack.Push(_minGasPricesContractDataStore);

            ContractDataStore<Address> whitelistContractDataStore = new ContractDataStoreWithLocalData<Address>(
                new HashSetContractDataStoreCollection<Address>(),
                _txPriorityContract?.SendersWhitelist,
                _api.BlockTree,
                _api.ReceiptFinder,
                _api.LogManager,
                _localDataSource?.GetWhitelistLocalDataSource() ?? new EmptyLocalDataSource<IEnumerable<Address>>());

            DictionaryContractDataStore<TxPriorityContract.Destination> prioritiesContractDataStore =
                new DictionaryContractDataStore<TxPriorityContract.Destination>(
                    new TxPriorityContract.DestinationSortedListContractDataStoreCollection(),
                    _txPriorityContract?.Priorities,
                    _api.BlockTree,
                    _api.ReceiptFinder,
                    _api.LogManager,
                    _localDataSource?.GetPrioritiesLocalDataSource());

            _api.DisposeStack.Push(whitelistContractDataStore);
            _api.DisposeStack.Push(prioritiesContractDataStore);

            ITxFilter auraTxFilter =
                CreateAuraTxFilterForProducer();
            ITxFilterPipeline txFilterPipeline = new TxFilterPipelineBuilder(_api.LogManager)
                .WithCustomTxFilter(auraTxFilter)
                .WithBaseFeeFilter(_api.SpecProvider)
                .WithNullTxFilter()
                .Build;


            return new TxPriorityTxSource(
                _api.TxPool,
                processingEnv.StateReader,
                _api.LogManager,
                txFilterPipeline,
                whitelistContractDataStore,
                prioritiesContractDataStore,
                _api.SpecProvider,
                _api.TransactionComparerProvider);
        }
        else
        {
            return CreateStandardTxPoolTxSource();
        }
    }

    // TODO: Use BlockProducerEnvFactory
    private BlockProducerEnv GetProducerChain(ITxSource? additionalTxSource)
    {
        BlockProducerEnv Create()
        {
            ReadOnlyBlockTree readOnlyBlockTree = _api.BlockTree.AsReadOnly();

            ReadOnlyTxProcessingEnv txProcessingEnv = _api.CreateReadOnlyTransactionProcessorSource();
            BlockProcessor blockProcessor = CreateBlockProcessor(txProcessingEnv);

            IBlockchainProcessor blockchainProcessor =
                new BlockchainProcessor(
                    readOnlyBlockTree,
                    blockProcessor,
                    _api.BlockPreprocessor,
                    txProcessingEnv.StateReader,
                    _api.LogManager,
                    BlockchainProcessor.Options.NoReceipts);

            OneTimeChainProcessor chainProcessor = new(
                txProcessingEnv.StateProvider,
                blockchainProcessor);

            return new BlockProducerEnv()
            {
                BlockTree = readOnlyBlockTree,
                ChainProcessor = chainProcessor,
                ReadOnlyStateProvider = txProcessingEnv.StateProvider,
                TxSource = CreateTxSourceForProducer(txProcessingEnv, additionalTxSource),
                ReadOnlyTxProcessingEnv = _api.CreateReadOnlyTransactionProcessorSource(),
            };
        }

        return _blockProducerContext ??= Create();
    }

    private ITxSource CreateStandardTxSourceForProducer(ReadOnlyTxProcessingEnv processingEnv) =>
        CreateTxPoolTxSource(processingEnv);

    private TxPoolTxSource CreateStandardTxPoolTxSource()
    {
        ITxFilter txSourceFilter = CreateAuraTxFilterForProducer();
        ITxFilterPipeline txFilterPipeline = new TxFilterPipelineBuilder(_api.LogManager)
            .WithCustomTxFilter(txSourceFilter)
            .WithBaseFeeFilter(_api.SpecProvider)
            .Build;
        return new TxPoolTxSource(_api.TxPool, _api.SpecProvider, _api.TransactionComparerProvider, _api.LogManager, txFilterPipeline);
    }

    private ITxFilter CreateAuraTxFilterForProducer() => TxAuRaFilterBuilders.CreateAuRaTxFilterForProducer(_api, _minGasPricesContractDataStore);

    private ITxSource CreateTxSourceForProducer(ReadOnlyTxProcessingEnv processingEnv, ITxSource? additionalTxSource)
    {
        bool CheckAddPosdaoTransactions(IList<ITxSource> list, long auRaPosdaoTransition)
        {
            if (auRaPosdaoTransition < AuRaParameters.TransitionDisabled && _validator is ITxSource validatorSource)
            {
                list.Insert(0, validatorSource);
                return true;
            }

            return false;
        }

        bool CheckAddRandomnessTransactions(IList<ITxSource> list, IDictionary<long, Address>? randomnessContractAddress, ISigner signer)
        {
            IList<IRandomContract> GetRandomContracts(
                IDictionary<long, Address> randomnessContractAddressPerBlock,
                IAbiEncoder abiEncoder,
                IReadOnlyTxProcessorSource txProcessorSource,
                ISigner signerLocal) =>
                randomnessContractAddressPerBlock
                    .Select(kvp => new RandomContract(
                        abiEncoder,
                        kvp.Value,
                        txProcessorSource,
                        kvp.Key,
                        signerLocal))
                    .ToArray<IRandomContract>();

            if (randomnessContractAddress?.Any() == true)
            {
                RandomContractTxSource randomContractTxSource = new RandomContractTxSource(
                    GetRandomContracts(randomnessContractAddress, _api.AbiEncoder,
                        _api.CreateReadOnlyTransactionProcessorSource(),
                        signer),
                    new EciesCipher(_api.CryptoRandom),
                    signer,
                    _api.BaseContainer.ResolveKeyed<ProtectedPrivateKey>(ComponentKey.NodeKey),
                    _api.CryptoRandom,
                    _api.LogManager);

                list.Insert(0, randomContractTxSource);
                return true;
            }

            return false;
        }

        if (_api.ChainSpec is null) throw new StepDependencyException(nameof(_api.ChainSpec));
        if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));
        if (_api.EngineSigner is null) throw new StepDependencyException(nameof(_api.EngineSigner));

        IList<ITxSource> txSources = new List<ITxSource> { CreateStandardTxSourceForProducer(processingEnv) };
        bool needSigner = false;

        if (additionalTxSource is not null)
        {
            txSources.Insert(0, additionalTxSource);
        }
        needSigner |= CheckAddPosdaoTransactions(txSources, _api.ChainSpec.AuRa.PosdaoTransition);
        needSigner |= CheckAddRandomnessTransactions(txSources, _api.ChainSpec.AuRa.RandomnessContractAddress, _api.EngineSigner);

        ITxSource txSource = txSources.Count > 1 ? new CompositeTxSource(txSources.ToArray()) : txSources[0];

        if (needSigner)
        {
            TxSealer transactionSealer = new TxSealer(_api.EngineSigner, _api.Timestamper);
            txSource = new GeneratedTxSource(txSource, transactionSealer, processingEnv.StateReader, _api.LogManager);
        }

        ITxFilter? txPermissionFilter = TxAuRaFilterBuilders.CreateTxPermissionFilter(_api);
        if (txPermissionFilter is not null)
        {
            // we now only need to filter generated transactions here, as regular ones are filtered on TxPoolTxSource filter based on CreateTxSourceFilter method
            txSource = new FilteredTxSource<GeneratedTransaction>(txSource, txPermissionFilter, _api.LogManager);
        }

        return txSource;
    }

    private static IGasLimitCalculator CreateGasLimitCalculator(AuRaNethermindApi api)
    {
        if (api.ChainSpec is null) throw new StepDependencyException(nameof(api.ChainSpec));
        var blockGasLimitContractTransitions = api.ChainSpec.AuRa.BlockGasLimitContractTransitions;

        IGasLimitCalculator gasLimitCalculator =
            new TargetAdjustedGasLimitCalculator(api.SpecProvider, api.Config<IBlocksConfig>());
        if (blockGasLimitContractTransitions?.Any() == true)
        {
            AuRaContractGasLimitOverride auRaContractGasLimitOverride = new(
                    blockGasLimitContractTransitions.Select(blockGasLimitContractTransition =>
                            new BlockGasLimitContract(
                                api.AbiEncoder,
                                blockGasLimitContractTransition.Value,
                                blockGasLimitContractTransition.Key,
                                api.CreateReadOnlyTransactionProcessorSource()))
                        .ToArray<IBlockGasLimitContract>(),
                    api.GasLimitCalculatorCache,
                    api.Config<IAuraConfig>().Minimum2MlnGasPerBlockWhenUsingBlockGasLimitContract == true,
                    gasLimitCalculator,
                    api.LogManager);

            gasLimitCalculator = auRaContractGasLimitOverride;
        }

        return gasLimitCalculator;
    }
}
