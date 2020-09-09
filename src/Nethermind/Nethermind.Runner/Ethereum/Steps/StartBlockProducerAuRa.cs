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
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Contracts;
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
        private IAuraConfig? _auraConfig;
        private IAuRaValidator? _validator;

        public StartBlockProducerAuRa(AuRaNethermindApi api) : base(api)
        {
            _api = api;
        }

        protected override void BuildProducer()
        {
            if (_api.Signer == null) throw new StepDependencyException(nameof(_api.Signer));
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));

            _auraConfig = _api.Config<IAuraConfig>();
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
            if (_api.Signer == null) throw new StepDependencyException(nameof(_api.Signer));

            var chainSpecAuRa = _api.ChainSpec.AuRa;

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
                    _api.Config<IMiningConfig>(),
                    _api.LogManager,
                    _api.Signer,
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
                GetTxPermissionFilter(readOnlyTxProcessingEnv, readOnlyTxProcessorSource),
                CreateGasLimitCalculator(readOnlyTxProcessorSource) as AuRaContractGasLimitOverride)
            {
                AuRaValidator = _validator
            };
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
                        _api.CryptoRandom);

                    list.Insert(0, randomContractTxSource);
                    return true;
                }

                return false;
            }

            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
            if (_api.BlockTree == null) throw new StepDependencyException(nameof(_api.BlockTree));
            if (_api.Signer == null) throw new StepDependencyException(nameof(_api.Signer));

            IList<ITxSource> txSources = new List<ITxSource> {base.CreateTxSourceForProducer(processingEnv, readOnlyTxProcessorSource)};
            bool needSigner = false;

            needSigner |= CheckAddPosdaoTransactions(txSources, _api.ChainSpec.AuRa.PosdaoTransition);
            needSigner |= CheckAddRandomnessTransactions(txSources, _api.ChainSpec.AuRa.RandomnessContractAddress, _api.Signer);

            ITxSource txSource = txSources.Count > 1 ? new CompositeTxSource(txSources.ToArray()) : txSources[0];

            if (needSigner)
            {
                TxSealer transactionSealer = new TxSealer(_api.Signer, _api.Timestamper); 
                txSource = new GeneratedTxSource(txSource, transactionSealer, processingEnv.StateReader, _api.LogManager);
            }

            var txPermissionFilter = GetTxPermissionFilter(processingEnv, readOnlyTxProcessorSource);
            if (txPermissionFilter != null)
            {
                txSource = new FilteredTxSource(txSource, txPermissionFilter);
            }

            return txSource;
        }

        protected override ITxFilter CreateGasPriceTxFilter(ReadOnlyTxProcessorSource readOnlyTxProcessorSource)
        {
            ITxFilter gasPriceTxFilter = base.CreateGasPriceTxFilter(readOnlyTxProcessorSource);
            Address? registrar = _api.ChainSpec?.Parameters.Registrar;
            if (registrar != null)
            {
                RegisterContract registerContract = new RegisterContract(_api.AbiEncoder, registrar, readOnlyTxProcessorSource);
                CertifierContract certifierContract = new CertifierContract(_api.AbiEncoder, registerContract, readOnlyTxProcessorSource);
                return new TxCertifierFilter(certifierContract, gasPriceTxFilter, _api.LogManager);
            }

            return gasPriceTxFilter;
        }
        
        private ITxFilter? GetTxPermissionFilter(
            ReadOnlyTxProcessingEnv environment,
            ReadOnlyTxProcessorSource readOnlyTxProcessorSource)
        {
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));

            if (_api.ChainSpec.Parameters.TransactionPermissionContract != null)
            {
                var txPermissionFilter = new PermissionBasedTxFilter(
                    new VersionedTransactionPermissionContract(_api.AbiEncoder,
                        _api.ChainSpec.Parameters.TransactionPermissionContract,
                        _api.ChainSpec.Parameters.TransactionPermissionContractTransition ?? 0,
                        readOnlyTxProcessorSource,
                        _api.TransactionPermissionContractVersions),
                    _api.TxFilterCache,
                    environment.StateProvider,
                    _api.LogManager);

                return txPermissionFilter;
            }

            return null;
        }

        private IGasLimitCalculator CreateGasLimitCalculator(ReadOnlyTxProcessorSource readOnlyTxProcessorSource)
        {
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
            var blockGasLimitContractTransitions = _api.ChainSpec.AuRa.BlockGasLimitContractTransitions;

            IGasLimitCalculator gasLimitCalculator =
                new TargetAdjustedGasLimitCalculator(_api.SpecProvider, _api.Config<IMiningConfig>());
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
