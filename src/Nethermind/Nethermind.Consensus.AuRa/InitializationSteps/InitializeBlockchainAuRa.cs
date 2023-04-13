// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Data;
using Nethermind.Blockchain.Services;
using Nethermind.Config;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Consensus.AuRa.Rewards;
using Nethermind.Consensus.AuRa.Services;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.TxPool.Comparison;

namespace Nethermind.Consensus.AuRa.InitializationSteps
{
    public class InitializeBlockchainAuRa : InitializeBlockchain
    {
        private readonly AuRaNethermindApi _api;
        private INethermindApi NethermindApi => _api;

        private AuRaSealValidator? _sealValidator;
        private IAuRaStepCalculator? _auRaStepCalculator;
        private readonly IAuraConfig _auraConfig;

        public InitializeBlockchainAuRa(AuRaNethermindApi api) : base(api)
        {
            _api = api;
            _auraConfig = NethermindApi.Config<IAuraConfig>();
        }

        protected override BlockProcessor CreateBlockProcessor()
        {
            if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));
            if (_api.ChainHeadStateProvider is null) throw new StepDependencyException(nameof(_api.ChainHeadStateProvider));
            if (_api.BlockValidator is null) throw new StepDependencyException(nameof(_api.BlockValidator));
            if (_api.RewardCalculatorSource is null) throw new StepDependencyException(nameof(_api.RewardCalculatorSource));
            if (_api.TransactionProcessor is null) throw new StepDependencyException(nameof(_api.TransactionProcessor));
            if (_api.DbProvider is null) throw new StepDependencyException(nameof(_api.DbProvider));
            if (_api.StateProvider is null) throw new StepDependencyException(nameof(_api.StateProvider));
            if (_api.StorageProvider is null) throw new StepDependencyException(nameof(_api.StorageProvider));
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
            if (_sealValidator is not null)
            {
                _sealValidator.ReportingValidator = reportingValidator;
            }

            return processor;
        }

        protected virtual BlockProcessor NewBlockProcessor(AuRaNethermindApi api, ITxFilter txFilter, ContractRewriter contractRewriter) =>
            new AuRaBlockProcessor(
                _api.SpecProvider,
                _api.BlockValidator,
                _api.RewardCalculatorSource.Get(_api.TransactionProcessor),
                new BlockProcessor.BlockValidationTransactionsExecutor(_api.TransactionProcessor, _api.StateProvider),
                _api.StateProvider,
                _api.StorageProvider,
                _api.ReceiptStorage,
                _api.LogManager,
                _api.BlockTree,
                new WithdrawalProcessor(_api.StateProvider, _api.LogManager),
                txFilter,
                GetGasLimitCalculator(),
                contractRewriter
            );

        protected ReadOnlyTxProcessingEnv CreateReadOnlyTransactionProcessorSource() =>
            new ReadOnlyTxProcessingEnv(_api.DbProvider, _api.ReadOnlyTrieStore, _api.BlockTree, _api.SpecProvider, _api.LogManager);

        protected override IHealthHintService CreateHealthHintService() =>
            new AuraHealthHintService(_auRaStepCalculator, _api.ValidatorStore);


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
                    _api.StateProvider,
                    _api.TransactionProcessor,
                    _api.BlockTree,
                    readOnlyTxProcessorSource,
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
                    _api.ReportingContractValidatorCache, chainSpecAuRa.PosdaoTransition, false)
                .CreateValidatorProcessor(chainSpecAuRa.Validators, _api.BlockTree.Head?.Header);

            if (validator is IDisposable disposableValidator)
            {
                _api.DisposeStack.Push(disposableValidator);
            }

            return validator;
        }

        protected AuRaContractGasLimitOverride? GetGasLimitCalculator()
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
                    new TargetAdjustedGasLimitCalculator(_api.SpecProvider, NethermindApi.Config<IBlocksConfig>()),
                    _api.LogManager);

                return gasLimitCalculator;
            }

            // do not return target gas limit calculator here - this is used for validation to check if the override should have been used
            return null;
        }

        protected override void InitSealEngine()
        {
            if (_api.DbProvider is null) throw new StepDependencyException(nameof(_api.DbProvider));
            if (_api.ChainSpec is null) throw new StepDependencyException(nameof(_api.ChainSpec));
            if (_api.EthereumEcdsa is null) throw new StepDependencyException(nameof(_api.EthereumEcdsa));
            if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));

            _api.ValidatorStore = new ValidatorStore(_api.DbProvider.BlockInfosDb);

            ValidSealerStrategy validSealerStrategy = new ValidSealerStrategy();
            AuRaStepCalculator auRaStepCalculator = new AuRaStepCalculator(_api.ChainSpec.AuRa.StepDuration, _api.Timestamper, _api.LogManager);
            _api.SealValidator = _sealValidator = new AuRaSealValidator(_api.ChainSpec.AuRa, auRaStepCalculator, _api.BlockTree, _api.ValidatorStore, validSealerStrategy, _api.EthereumEcdsa, _api.LogManager);
            _api.RewardCalculatorSource = AuRaRewardCalculator.GetSource(_api.ChainSpec.AuRa, _api.AbiEncoder);
            _api.Sealer = new AuRaSealer(_api.BlockTree, _api.ValidatorStore, auRaStepCalculator, _api.EngineSigner, validSealerStrategy, _api.LogManager);
            _auRaStepCalculator = auRaStepCalculator;
        }

        // private IReadOnlyTransactionProcessorSource GetReadOnlyTransactionProcessorSource() =>
        //     _readOnlyTransactionProcessorSource ??= new ReadOnlyTxProcessorSource(
        //         _api.DbProvider, _api.ReadOnlyTrieStore, _api.BlockTree, _api.SpecProvider, _api.LogManager);

        protected override IHeaderValidator CreateHeaderValidator()
        {
            if (_api.ChainSpec is null) throw new StepDependencyException(nameof(_api.ChainSpec));
            var blockGasLimitContractTransitions = _api.ChainSpec.AuRa.BlockGasLimitContractTransitions;
            return blockGasLimitContractTransitions?.Any() == true
                ? new AuRaHeaderValidator(
                    _api.BlockTree,
                    _api.SealValidator,
                    _api.SpecProvider,
                    _api.LogManager,
                    blockGasLimitContractTransitions.Keys.ToArray())
                : base.CreateHeaderValidator();
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
                new ChainHeadInfoProvider(_api.SpecProvider, _api.BlockTree, _api.StateReader),
                NethermindApi.Config<ITxPoolConfig>(),
                _api.TxValidator,
                _api.LogManager,
                CreateTxPoolTxComparer(txPriorityContract, localDataSource),
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
}
