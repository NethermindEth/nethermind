﻿//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Data;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;

namespace Nethermind.Consensus.AuRa.InitializationSteps
{
    public class StartBlockProducerAuRa
    {
        private new readonly AuRaNethermindApi _api;
        
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
                return _stepCalculator ?? (_stepCalculator = new AuRaStepCalculator(_api.ChainSpec.AuRa.StepDuration, _api.Timestamper, _api.LogManager));
            }
        }

        public IBlockProductionTrigger CreateTrigger()
        {
            BuildBlocksOnAuRaSteps onAuRaSteps = new(_api.LogManager, StepCalculator);
            BuildBlocksOnlyWhenNotProcessing onlyWhenNotProcessing = new(
                onAuRaSteps, 
                _api.BlockProcessingQueue, 
                _api.BlockTree, 
                _api.LogManager, 
                !_auraConfig.AllowAuRaPrivateChains);
            
            _api.DisposeStack.Push(onlyWhenNotProcessing);

            return onlyWhenNotProcessing;
        }

        public Task<IBlockProducer> BuildProducer(IBlockProductionTrigger blockProductionTrigger, ITxSource? additionalTxSource = null)
        {
            if (_api.EngineSigner == null) throw new StepDependencyException(nameof(_api.EngineSigner));
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
            
            ILogger logger = _api.LogManager.GetClassLogger();
            if (logger.IsWarn) logger.Warn("Starting AuRa block producer & sealer");
            
            BlockProducerEnv producerEnv = GetProducerChain(additionalTxSource);

            IGasLimitCalculator gasLimitCalculator = _api.GasLimitCalculator = CreateGasLimitCalculator(producerEnv.ReadOnlyTxProcessingEnv);
            
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
                _api.LogManager);
            
            return Task.FromResult(blockProducer);
        }

        private BlockProcessor CreateBlockProcessor(ReadOnlyTxProcessingEnv changeableTxProcessingEnv, ReadOnlyTxProcessingEnv constantContractTxProcessingEnv)
        {
            if (_api.RewardCalculatorSource == null) throw new StepDependencyException(nameof(_api.RewardCalculatorSource));
            if (_api.ValidatorStore == null) throw new StepDependencyException(nameof(_api.ValidatorStore));
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
            if (_api.BlockTree == null) throw new StepDependencyException(nameof(_api.BlockTree));
            if (_api.EngineSigner == null) throw new StepDependencyException(nameof(_api.EngineSigner));
            if (_api.SpecProvider == null) throw new StepDependencyException(nameof(_api.SpecProvider));

            var chainSpecAuRa = _api.ChainSpec.AuRa;

            ITxFilter auRaTxFilter = TxAuRaFilterBuilders.CreateAuRaTxFilter(
                _api,
                constantContractTxProcessingEnv,
                _api.SpecProvider);

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
                    NethermindApi.Config<IMiningConfig>(),
                    _api.LogManager,
                    _api.EngineSigner,
                    _api.SpecProvider,
                    _api.ReportingContractValidatorCache, chainSpecAuRa.PosdaoTransition, true)
                .CreateValidatorProcessor(chainSpecAuRa.Validators, _api.BlockTree.Head?.Header);

            if (_validator is IDisposable disposableValidator)
            {
                _api.DisposeStack.Push(disposableValidator);
            }

            return new AuRaBlockProcessor(
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
                CreateGasLimitCalculator(constantContractTxProcessingEnv) as AuRaContractGasLimitOverride)
            {
                AuRaValidator = _validator
            };
        }

        private TxPoolTxSource CreateTxPoolTxSource(ReadOnlyTxProcessingEnv processingEnv, IReadOnlyTxProcessorSource readOnlyTxProcessorSource)
        {
            // We need special one for TxPriority as its following Head separately with events and we want rules from Head, not produced block
            IReadOnlyTxProcessorSource readOnlyTxProcessorSourceForTxPriority = 
                new ReadOnlyTxProcessingEnv(_api.DbProvider, _api.ReadOnlyTrieStore, _api.BlockTree, _api.SpecProvider, _api.LogManager);
            
            (_txPriorityContract, _localDataSource) = TxAuRaFilterBuilders.CreateTxPrioritySources(_auraConfig, _api, readOnlyTxProcessorSourceForTxPriority);

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
                    CreateAuraTxFilterForProducer(readOnlyTxProcessorSource, _api.SpecProvider);
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
                return CreateStandardTxPoolTxSource(processingEnv, readOnlyTxProcessorSource);
            }
        }
        
        // TODO: Use BlockProducerEnvFactory
        private BlockProducerEnv GetProducerChain(ITxSource? additionalTxSource)
        {
            ReadOnlyTxProcessingEnv CreateReadonlyTxProcessingEnv(ReadOnlyDbProvider dbProvider, ReadOnlyBlockTree blockTree)
            {
                return new(dbProvider, _api.ReadOnlyTrieStore, blockTree, _api.SpecProvider, _api.LogManager);
            }

            BlockProducerEnv Create()
            {
                ReadOnlyDbProvider dbProvider = _api.DbProvider.AsReadOnly(false);
                ReadOnlyBlockTree blockTree = _api.BlockTree.AsReadOnly();

                ReadOnlyTxProcessingEnv txProcessingEnv = CreateReadonlyTxProcessingEnv(dbProvider, blockTree);
                ReadOnlyTxProcessingEnv constantContractsProcessingEnv = CreateReadonlyTxProcessingEnv(dbProvider, blockTree);
                BlockProcessor blockProcessor = CreateBlockProcessor(txProcessingEnv, constantContractsProcessingEnv);

                IBlockchainProcessor blockchainProcessor =
                    new BlockchainProcessor(
                        blockTree,
                        blockProcessor,
                        _api.BlockPreprocessor,
                        _api.LogManager,
                        BlockchainProcessor.Options.NoReceipts);

                OneTimeChainProcessor chainProcessor = new(
                    dbProvider,
                    blockchainProcessor);

                return new BlockProducerEnv()
                {
                    ChainProcessor = chainProcessor,
                    ReadOnlyStateProvider = txProcessingEnv.StateProvider,
                    TxSource = CreateTxSourceForProducer(txProcessingEnv, constantContractsProcessingEnv, additionalTxSource),
                    ReadOnlyTxProcessingEnv = constantContractsProcessingEnv
                };
            }

            return _blockProducerContext ??= Create();
        }

        private ITxSource CreateStandardTxSourceForProducer(
            ReadOnlyTxProcessingEnv processingEnv,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource) =>
            CreateTxPoolTxSource(processingEnv, readOnlyTxProcessorSource);

        private TxPoolTxSource CreateStandardTxPoolTxSource(ReadOnlyTxProcessingEnv processingEnv, IReadOnlyTxProcessorSource readOnlyTxProcessorSource)
        {
            ITxFilter txSourceFilter = CreateAuraTxFilterForProducer(readOnlyTxProcessorSource,_api.SpecProvider);
            ITxFilterPipeline txFilterPipeline = new TxFilterPipelineBuilder(_api.LogManager)
                .WithCustomTxFilter(txSourceFilter)
                .WithBaseFeeFilter(_api.SpecProvider)
                .Build;
            return new TxPoolTxSource(_api.TxPool, _api.SpecProvider, _api.TransactionComparerProvider, _api.LogManager, txFilterPipeline);
        }

        private ITxFilter CreateAuraTxFilterForProducer(IReadOnlyTxProcessorSource readOnlyTxProcessorSource, ISpecProvider specProvider) =>
            TxAuRaFilterBuilders.CreateAuRaTxFilterForProducer(
                NethermindApi.Config<IMiningConfig>(),
                _api,
                readOnlyTxProcessorSource,
                _minGasPricesContractDataStore,
                specProvider);

        private ITxSource CreateTxSourceForProducer(ReadOnlyTxProcessingEnv processingEnv, IReadOnlyTxProcessorSource readOnlyTxProcessorSource, ITxSource? additionalTxSource)
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
                            readOnlyTxProcessorSource,
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

            IList<ITxSource> txSources = new List<ITxSource> {CreateStandardTxSourceForProducer(processingEnv, readOnlyTxProcessorSource)};
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
            
            ITxFilter? txPermissionFilter = TxAuRaFilterBuilders.CreateTxPermissionFilter(_api, readOnlyTxProcessorSource);
            if (txPermissionFilter != null)
            {
                // we now only need to filter generated transactions here, as regular ones are filtered on TxPoolTxSource filter based on CreateTxSourceFilter method
                txSource = new FilteredTxSource<GeneratedTransaction>(txSource, txPermissionFilter, _api.LogManager);
            }

            return txSource;
        }

        private IGasLimitCalculator CreateGasLimitCalculator(IReadOnlyTxProcessorSource readOnlyTxProcessorSource)
        {
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
            var blockGasLimitContractTransitions = _api.ChainSpec.AuRa.BlockGasLimitContractTransitions;

            IGasLimitCalculator gasLimitCalculator =
                new TargetAdjustedGasLimitCalculator(_api.SpecProvider, NethermindApi.Config<IMiningConfig>());
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
}
