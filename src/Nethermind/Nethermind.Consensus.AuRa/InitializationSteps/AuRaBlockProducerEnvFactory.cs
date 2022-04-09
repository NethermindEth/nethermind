using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Data;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Init.Steps;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.TxPool;

namespace Nethermind.Consensus.AuRa.InitializationSteps
{
    public class AuRaBlockProducerEnvFactory : IBlockProducerEnvFactory
    {
        private readonly AuRaNethermindApi _api;
        private IAuraConfig _auraConfig;
        private DictionaryContractDataStore<TxPriorityContract.Destination>? _minGasPricesContractDataStore;

        private IAuRaValidator _validator;

        public AuRaBlockProducerEnvFactory(AuRaNethermindApi api, IAuraConfig auraConfig)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _auraConfig = auraConfig ?? throw new ArgumentNullException(nameof(auraConfig));
        }

        public IBlockTransactionsExecutorFactory TransactionsExecutorFactory { get; set; }

        public virtual BlockProducerEnv Create(ITxSource? additionalTxSource = null)
        {
            ReadOnlyDbProvider dbProvider = _api.DbProvider.AsReadOnly(false);
            ReadOnlyBlockTree readOnlyBlockTree = _api.BlockTree.AsReadOnly();

            ReadOnlyTxProcessingEnv txProcessingEnv = CreateReadonlyTxProcessingEnv(dbProvider, readOnlyBlockTree);
            ReadOnlyTxProcessingEnv constantContractsProcessingEnv = CreateReadonlyTxProcessingEnv(dbProvider, readOnlyBlockTree);

            IGasLimitCalculator gasLimitCalculator = CreateGasLimitCalculator(constantContractsProcessingEnv);

            BlockProcessor blockProcessor = CreateBlockProcessor(txProcessingEnv, constantContractsProcessingEnv, gasLimitCalculator as AuRaContractGasLimitOverride);
            IBlockchainProcessor blockchainProcessor =
                new BlockchainProcessor(
                    readOnlyBlockTree,
                    blockProcessor,
                    _api.BlockPreprocessor,
                    _api.LogManager,
                    BlockchainProcessor.Options.NoReceipts);

            OneTimeChainProcessor chainProcessor = new(
                dbProvider,
                blockchainProcessor);

            var txSource = CreateTxSourceForProducer(txProcessingEnv, constantContractsProcessingEnv, additionalTxSource);

            return new AuRaBlockProducerEnv()
            {
                BlockTree = readOnlyBlockTree,
                ChainProcessor = chainProcessor,
                ReadOnlyStateProvider = txProcessingEnv.StateProvider,
                TxSource = txSource,
                ReadOnlyTxProcessingEnv = constantContractsProcessingEnv,
                GasLimitCalculator = gasLimitCalculator,
            };
        }

        private BlockProcessor CreateBlockProcessor(ReadOnlyTxProcessingEnv changeableTxProcessingEnv, ReadOnlyTxProcessingEnv constantContractTxProcessingEnv, AuRaContractGasLimitOverride? gasLimitOverride)
        {
            if (_api.RewardCalculatorSource == null) throw new StepDependencyException(nameof(_api.RewardCalculatorSource));
            if (_api.ValidatorStore == null) throw new StepDependencyException(nameof(_api.ValidatorStore));
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
            if (_api.BlockTree == null) throw new StepDependencyException(nameof(_api.BlockTree));
            if (_api.EngineSigner == null) throw new StepDependencyException(nameof(_api.EngineSigner));
            if (_api.SpecProvider == null) throw new StepDependencyException(nameof(_api.SpecProvider));
            if (_api.GasPriceOracle == null) throw new StepDependencyException(nameof(_api.GasPriceOracle));

            var chainSpecAuRa = _api.ChainSpec.AuRa;

            ITxFilter auRaTxFilter = TxAuRaFilterBuilders.CreateAuRaTxFilter(
                _api,
                constantContractTxProcessingEnv,
                _api.SpecProvider,
                new LocalTxFilter(_api.EngineSigner));

            AuRaBlockProcessor processor = new(
                _api.SpecProvider,
                _api.BlockValidator,
                _api.RewardCalculatorSource.Get(changeableTxProcessingEnv.TransactionProcessor),
                _api.BlockProducerEnvFactory.TransactionsExecutorFactory.Create(changeableTxProcessingEnv),
                changeableTxProcessingEnv.StateProvider,
                changeableTxProcessingEnv.StorageProvider,
                _api.ReceiptStorage,
                _api.LogManager,
                changeableTxProcessingEnv.BlockTree,
                auRaTxFilter,
                gasLimitOverride);

            _validator = new AuRaValidatorFactory(_api.AbiEncoder,
                    changeableTxProcessingEnv.StateProvider,
                    changeableTxProcessingEnv.TransactionProcessor,
                    changeableTxProcessingEnv.BlockTree,
                    constantContractTxProcessingEnv,
                    _api.ReceiptStorage,
                    _api.ValidatorStore,
                    _api.FinalizationManager,
                    NullTxSender.Instance,
                    NullTxPool.Instance,
                    _api.Config<IMiningConfig>(),
                    _api.LogManager,
                    _api.EngineSigner,
                    _api.SpecProvider,
                    _api.GasPriceOracle,
                    _api.ReportingContractValidatorCache, chainSpecAuRa.PosdaoTransition, true)
                .CreateValidatorProcessor(chainSpecAuRa.Validators, _api.BlockTree.Head?.Header);

            if (_validator is IDisposable disposableValidator)
            {
                _api.DisposeStack.Push(disposableValidator);
            }

            processor.AuRaValidator = _validator;
            return processor;
        }


        protected ReadOnlyTxProcessingEnv CreateReadonlyTxProcessingEnv(ReadOnlyDbProvider dbProvider, ReadOnlyBlockTree blockTree)
        {
            return new(dbProvider, _api.ReadOnlyTrieStore, blockTree, _api.SpecProvider, _api.LogManager);
        }

        private ITxSource CreateTxSourceForProducer(ReadOnlyTxProcessingEnv processingEnv, ReadOnlyTxProcessingEnv constantContractsProcessingEnv, ITxSource? additionalTxSource)
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
                            constantContractsProcessingEnv,
                            signer),
                        new EciesCipher(_api.CryptoRandom),
                        signer,
                        _api.NodeKey,
                        _api.CryptoRandom,
                        _api.LogManager);

                    list.Insert(0, randomContractTxSource);
                    return true;
                }

                return false;
            }

            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
            if (_api.BlockTree == null) throw new StepDependencyException(nameof(_api.BlockTree));
            if (_api.EngineSigner == null) throw new StepDependencyException(nameof(_api.EngineSigner));

            IList<ITxSource> txSources = new List<ITxSource> {
                CreateTxPoolTxSource(processingEnv, constantContractsProcessingEnv)
            };
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

            ITxFilter? txPermissionFilter = TxAuRaFilterBuilders.CreateTxPermissionFilter(_api, constantContractsProcessingEnv);
            if (txPermissionFilter != null)
            {
                // we now only need to filter generated transactions here, as regular ones are filtered on TxPoolTxSource filter based on CreateTxSourceFilter method
                txSource = new FilteredTxSource<GeneratedTransaction>(txSource, txPermissionFilter, _api.LogManager);
            }

            return txSource;
        }

        private TxPoolTxSource CreateTxPoolTxSource(ReadOnlyTxProcessingEnv processingEnv, ReadOnlyTxProcessingEnv constantContractsProcessingEnv)
        {
            // We need special one for TxPriority as its following Head separately with events and we want rules from Head, not produced block
            IReadOnlyTxProcessorSource readOnlyTxProcessorSourceForTxPriority =
                new ReadOnlyTxProcessingEnv(_api.DbProvider, _api.ReadOnlyTrieStore, _api.BlockTree, _api.SpecProvider, _api.LogManager);

            (TxPriorityContract _txPriorityContract,TxPriorityContract.LocalDataSource _localDataSource) = TxAuRaFilterBuilders.CreateTxPrioritySources(_auraConfig, _api, readOnlyTxProcessorSourceForTxPriority);

            if (_txPriorityContract != null || _localDataSource != null)
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
                    CreateAuraTxFilterForProducer(constantContractsProcessingEnv, _api.SpecProvider);
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
                return CreateStandardTxPoolTxSource(processingEnv, constantContractsProcessingEnv);
            }
        }

        private TxPoolTxSource CreateStandardTxPoolTxSource(ReadOnlyTxProcessingEnv processingEnv, IReadOnlyTxProcessorSource readOnlyTxProcessorSource)
        {
            ITxFilter txSourceFilter = CreateAuraTxFilterForProducer(readOnlyTxProcessorSource, _api.SpecProvider);
            ITxFilterPipeline txFilterPipeline = new TxFilterPipelineBuilder(_api.LogManager)
                .WithCustomTxFilter(txSourceFilter)
                .WithBaseFeeFilter(_api.SpecProvider)
                .Build;
            return new TxPoolTxSource(_api.TxPool, _api.SpecProvider, _api.TransactionComparerProvider, _api.LogManager, txFilterPipeline);
        }

        private ITxFilter CreateAuraTxFilterForProducer(IReadOnlyTxProcessorSource readOnlyTxProcessorSource, ISpecProvider specProvider) =>
            TxAuRaFilterBuilders.CreateAuRaTxFilterForProducer(
                _api.Config<IMiningConfig>(),
                _api,
                readOnlyTxProcessorSource,
                _minGasPricesContractDataStore,
                specProvider);

        private IGasLimitCalculator CreateGasLimitCalculator(IReadOnlyTxProcessorSource readOnlyTxProcessorSource)
        {
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
            var blockGasLimitContractTransitions = _api.ChainSpec.AuRa.BlockGasLimitContractTransitions;

            IGasLimitCalculator gasLimitCalculator =
                new TargetAdjustedGasLimitCalculator(_api.SpecProvider, _api.Config<IMiningConfig>());
            if (blockGasLimitContractTransitions?.Any() == true)
            {
                AuRaContractGasLimitOverride auRaContractGasLimitOverride = new(
                        blockGasLimitContractTransitions.Select(blockGasLimitContractTransition =>
                                new BlockGasLimitContract(
                                    _api.AbiEncoder,
                                    blockGasLimitContractTransition.Value,
                                    blockGasLimitContractTransition.Key,
                                    readOnlyTxProcessorSource))
                            .ToArray<IBlockGasLimitContract>(),
                        _api.GasLimitCalculatorCache,
                        _auraConfig?.Minimum2MlnGasPerBlockWhenUsingBlockGasLimitContract == true,
                        gasLimitCalculator,
                        _api.LogManager);

                gasLimitCalculator = auRaContractGasLimitOverride;
            }

            return gasLimitCalculator;
        }

    }

    public class AuRaBlockProducerEnv : BlockProducerEnv
    {
        public IGasLimitCalculator GasLimitCalculator { get; set; }
    }
}
