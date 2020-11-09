//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.Abi;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Data;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Api;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.TxPool;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(InitializeNetwork), typeof(SetupKeyStore))]
    public class StartBlockProducerAuRa : StartBlockProducer
    {
        private readonly AuRaNethermindApi _api;
        private INethermindApi NethermindApi => _api;
        
        private readonly IAuraConfig _auraConfig;
        private IAuRaValidator? _validator;
        private DictionaryContractDataStore<TxPriorityContract.Destination>? _minGasPricesContractDataStore;
        private TxPriorityContract? _txPriorityContract;
        private TxPriorityContract.LocalDataSource? _localDataSource;
        private ITxFilter? _txPermissionFilter;

        public StartBlockProducerAuRa(AuRaNethermindApi api) : base(api)
        {
            _api = api;
            _auraConfig = NethermindApi.Config<IAuraConfig>();
        }

        protected override void BuildProducer()
        {
            if (_api.EngineSigner == null) throw new StepDependencyException(nameof(_api.EngineSigner));
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
            
            ILogger logger = _api.LogManager.GetClassLogger();
            if (logger.IsWarn) logger.Warn("Starting AuRa block producer & sealer");

            IAuRaStepCalculator stepCalculator = new AuRaStepCalculator(_api.ChainSpec.AuRa.StepDuration, _api.Timestamper, _api.LogManager);
            BlockProducerContext producerContext = GetProducerChain();
            _api.BlockProducer = new AuRaBlockProducer(
                producerContext.TxSource,
                producerContext.ChainProcessor,
                producerContext.ReadOnlyStateProvider,
                _api.Sealer,
                _api.BlockTree,
                _api.BlockProcessingQueue,
                _api.Timestamper,
                stepCalculator,
                _api.ReportingValidator,
                _auraConfig,
                CreateGasLimitCalculator(producerContext.ReadOnlyTxProcessorSource),
                _api.LogManager);
        }

        protected override BlockProcessor CreateBlockProcessor(
            ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv,
            ReadOnlyTxProcessorSource readOnlyTxProcessorSource,
            IReadOnlyDbProvider readOnlyDbProvider)
        {
            if (_api.RewardCalculatorSource == null) throw new StepDependencyException(nameof(_api.RewardCalculatorSource));
            if (_api.ValidatorStore == null) throw new StepDependencyException(nameof(_api.ValidatorStore));
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
            if (_api.BlockTree == null) throw new StepDependencyException(nameof(_api.BlockTree));
            if (_api.EngineSigner == null) throw new StepDependencyException(nameof(_api.EngineSigner));

            var chainSpecAuRa = _api.ChainSpec.AuRa;
            _txPermissionFilter = TxFilterBuilders.CreateTxPermissionFilter(_api, readOnlyTxProcessorSource, readOnlyTxProcessingEnv.StateProvider);

            _validator = new AuRaValidatorFactory(
                    readOnlyTxProcessingEnv.StateProvider,
                    _api.AbiEncoder,
                    readOnlyTxProcessingEnv.TransactionProcessor,
                    readOnlyTxProcessorSource,
                    readOnlyTxProcessingEnv.BlockTree,
                    _api.ReceiptStorage,
                    _api.ValidatorStore,
                    _api.FinalizationManager,
                    NullTxSender.Instance,
                    NullTxPool.Instance,
                    NethermindApi.Config<IMiningConfig>(),
                    _api.LogManager,
                    _api.EngineSigner,
                    _api.ReportingContractValidatorCache,
                    chainSpecAuRa.PosdaoTransition,
                    true)
                .CreateValidatorProcessor(chainSpecAuRa.Validators, _api.BlockTree.Head?.Header);

            if (_validator is IDisposable disposableValidator)
            {
                _api.DisposeStack.Push(disposableValidator);
            }

            return new AuRaBlockProcessor(
                _api.SpecProvider,
                _api.BlockValidator,
                _api.RewardCalculatorSource.Get(readOnlyTxProcessingEnv.TransactionProcessor),
                readOnlyTxProcessingEnv.TransactionProcessor,
                readOnlyDbProvider.StateDb,
                readOnlyDbProvider.CodeDb,
                readOnlyTxProcessingEnv.StateProvider,
                readOnlyTxProcessingEnv.StorageProvider,
                _api.TxPool,
                _api.ReceiptStorage,
                _api.LogManager,
                readOnlyTxProcessingEnv.BlockTree,
                _txPermissionFilter,
                CreateGasLimitCalculator(readOnlyTxProcessorSource) as AuRaContractGasLimitOverride)
            {
                AuRaValidator = _validator
            };
        }

        protected override TxPoolTxSource CreateTxPoolTxSource(ReadOnlyTxProcessingEnv processingEnv, ReadOnlyTxProcessorSource readOnlyTxProcessorSource)
        {
            (_txPriorityContract, _localDataSource) = TxFilterBuilders.CreateTxPrioritySources(_auraConfig, _api, readOnlyTxProcessorSource);

            if (_txPriorityContract != null || _localDataSource != null)
            {
                _minGasPricesContractDataStore = TxFilterBuilders.CreateMinGasPricesDataStore(_api, _txPriorityContract, _localDataSource)!;
                _api.DisposeStack.Push(_minGasPricesContractDataStore);                

                IBlockProcessor? blockProcessor = _api.MainBlockProcessor;
                ContractDataStore<Address, IContractDataStoreCollection<Address>> whitelistContractDataStore = new ContractDataStoreWithLocalData<Address>(
                    new HashSetContractDataStoreCollection<Address>(),
                    _txPriorityContract?.SendersWhitelist,
                    blockProcessor,
                    _api.LogManager,
                    _localDataSource?.GetWhitelistLocalDataSource() ?? new EmptyLocalDataSource<IEnumerable<Address>>());
                
                DictionaryContractDataStore<TxPriorityContract.Destination> prioritiesContractDataStore = new DictionaryContractDataStore<TxPriorityContract.Destination>(
                    new TxPriorityContract.DestinationSortedListContractDataStoreCollection(),
                    _txPriorityContract?.Priorities,
                    blockProcessor,
                    _api.LogManager,
                    _localDataSource?.GetPrioritiesLocalDataSource());
                
                _api.DisposeStack.Push(whitelistContractDataStore);
                _api.DisposeStack.Push(prioritiesContractDataStore);

                
                return new TxPriorityTxSource(
                    _api.TxPool,
                    processingEnv.StateReader, 
                    _api.LogManager, 
                    CreateTxSourceFilter(processingEnv, readOnlyTxProcessorSource),
                    whitelistContractDataStore,
                    prioritiesContractDataStore);
            }
            else
            {
                return base.CreateTxPoolTxSource(processingEnv, readOnlyTxProcessorSource);
            }
        }

        protected override ITxSource CreateTxSourceForProducer(ReadOnlyTxProcessingEnv processingEnv, ReadOnlyTxProcessorSource readOnlyTxProcessorSource)
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

            bool CheckAddRandomnessTransactions(IList<ITxSource> list, IDictionary<long, Address> randomnessContractAddress, ISigner signer)
            {
                IList<IRandomContract> GetRandomContracts(
                    IDictionary<long, Address> randomnessContractAddressPerBlock,
                    IAbiEncoder abiEncoder,
                    IReadOnlyTransactionProcessorSource txProcessorSource,
                    ISigner signer) =>
                    randomnessContractAddressPerBlock
                        .Select(kvp => new RandomContract(
                            abiEncoder,
                            kvp.Value,
                            txProcessorSource,
                            kvp.Key,
                            signer))
                        .ToArray<IRandomContract>();

                if (randomnessContractAddress?.Any() == true)
                {
                    var randomContractTxSource = new RandomContractTxSource(
                        GetRandomContracts(randomnessContractAddress, _api.AbiEncoder,
                            readOnlyTxProcessorSource,
                            signer),
                        new EciesCipher(_api.CryptoRandom),
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

            IList<ITxSource> txSources = new List<ITxSource> {base.CreateTxSourceForProducer(processingEnv, readOnlyTxProcessorSource)};
            bool needSigner = false;

            needSigner |= CheckAddPosdaoTransactions(txSources, _api.ChainSpec.AuRa.PosdaoTransition);
            needSigner |= CheckAddRandomnessTransactions(txSources, _api.ChainSpec.AuRa.RandomnessContractAddress, _api.EngineSigner);

            ITxSource txSource = txSources.Count > 1 ? new CompositeTxSource(txSources.ToArray()) : txSources[0];

            if (needSigner)
            {
                TxSealer transactionSealer = new TxSealer(_api.EngineSigner, _api.Timestamper); 
                txSource = new GeneratedTxSource(txSource, transactionSealer, processingEnv.StateReader, _api.LogManager);
            }
            
            if (_txPermissionFilter != null)
            {
                // we now only need to filter generated transactions here, as regular ones are filtered on TxPoolTxSource filter based on CreateTxSourceFilter method
                txSource = new FilteredTxSource<GeneratedTransaction>(txSource, _txPermissionFilter);
            }

            return txSource;
        }

        protected override ITxFilter CreateTxSourceFilter(ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv, ReadOnlyTxProcessorSource readOnlyTxProcessorSource) => 
            TxFilterBuilders.CreateAuRaTxFilter(
                NethermindApi.Config<IMiningConfig>(),
                _api,
                readOnlyTxProcessorSource,
                readOnlyTxProcessingEnv.StateProvider,
                _minGasPricesContractDataStore);

        private IGasLimitCalculator CreateGasLimitCalculator(ReadOnlyTxProcessorSource readOnlyTxProcessorSource)
        {
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
            var blockGasLimitContractTransitions = _api.ChainSpec.AuRa.BlockGasLimitContractTransitions;

            IGasLimitCalculator gasLimitCalculator =
                new TargetAdjustedGasLimitCalculator(_api.SpecProvider, NethermindApi.Config<IMiningConfig>());
            if (blockGasLimitContractTransitions?.Any() == true)
            {
                AuRaContractGasLimitOverride auRaContractGasLimitOverride =
                    new AuRaContractGasLimitOverride(
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
