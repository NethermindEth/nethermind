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
using Nethermind.Runner.Ethereum.Context;
using Nethermind.Facade.Transactions;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.TxPool;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(InitializeNetwork), typeof(SetupKeyStore))]
    public class StartBlockProducerAuRa : StartBlockProducer
    {
        private readonly AuRaEthereumRunnerContext _context;
        private IAuraConfig? _auraConfig;
        private IAuRaValidator? _validator;

        public StartBlockProducerAuRa(AuRaEthereumRunnerContext context) : base(context)
        {
            _context = context;
        }

        protected override void BuildProducer()
        {
            if (_context.Signer == null) throw new StepDependencyException(nameof(_context.Signer));
            if (_context.ChainSpec == null) throw new StepDependencyException(nameof(_context.ChainSpec));
            
            _auraConfig = _context.Config<IAuraConfig>();
            ILogger logger = _context.LogManager.GetClassLogger();
            if (logger.IsWarn) logger.Warn("Starting AuRa block producer & sealer");
            
            IAuRaStepCalculator stepCalculator = new AuRaStepCalculator(_context.ChainSpec.AuRa.StepDuration, _context.Timestamper, _context.LogManager);
            BlockProducerContext producerContext = GetProducerChain();
            _context.BlockProducer = new AuRaBlockProducer(
                producerContext.TxSource,
                producerContext.ChainProcessor,
                producerContext.ReadOnlyStateProvider,
                _context.Sealer,
                _context.BlockTree,
                _context.BlockProcessingQueue,
                _context.Timestamper,
                stepCalculator,
                _context.ReportingValidator,
                _auraConfig,
                CreateGasLimitCalculator(producerContext.ReadOnlyTxProcessorSource),
                _context.LogManager);
        }

        protected override BlockProcessor CreateBlockProcessor(
            ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv, 
            ReadOnlyTxProcessorSource readOnlyTxProcessorSource, 
            IReadOnlyDbProvider readOnlyDbProvider)
        {
            if (_context.RewardCalculatorSource == null) throw new StepDependencyException(nameof(_context.RewardCalculatorSource));
            if (_context.ValidatorStore == null) throw new StepDependencyException(nameof(_context.ValidatorStore));
            if (_context.ChainSpec == null) throw new StepDependencyException(nameof(_context.ChainSpec));
            if (_context.BlockTree == null) throw new StepDependencyException(nameof(_context.BlockTree));
            if (_context.Signer == null) throw new StepDependencyException(nameof(_context.Signer));

            var chainSpecAuRa = _context.ChainSpec.AuRa;
            
            _validator = new AuRaValidatorFactory(
                    readOnlyTxProcessingEnv.StateProvider,
                    _context.AbiEncoder,
                    readOnlyTxProcessingEnv.TransactionProcessor,
                    readOnlyTxProcessorSource,
                    readOnlyTxProcessingEnv.BlockTree,
                    _context.ReceiptStorage,
                    _context.ValidatorStore,
                    _context.FinalizationManager,
                    NullTxSender.Instance,
                    NullTxPool.Instance, 
                    _context.LogManager,
                    _context.Signer,
                    _context.ReportingContractValidatorCache,
                    chainSpecAuRa.PosdaoTransition,
                    true)
                .CreateValidatorProcessor(chainSpecAuRa.Validators, _context.BlockTree.Head?.Header);
            
            if (_validator is IDisposable disposableValidator)
            {
                _context.DisposeStack.Push(disposableValidator);
            }

            return new AuRaBlockProcessor(
                _context.SpecProvider,
                _context.BlockValidator,
                _context.RewardCalculatorSource.Get(readOnlyTxProcessingEnv.TransactionProcessor),
                readOnlyTxProcessingEnv.TransactionProcessor,
                readOnlyDbProvider.StateDb,
                readOnlyDbProvider.CodeDb,
                readOnlyTxProcessingEnv.StateProvider,
                readOnlyTxProcessingEnv.StorageProvider,
                _context.TxPool,
                _context.ReceiptStorage,
                _context.LogManager,
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
                        GetRandomContracts(randomnessContractAddress, _context.AbiEncoder,
                            readOnlyTxProcessorSource,
                            signer),
                        new EciesCipher(_context.CryptoRandom),
                        _context.NodeKey,
                        _context.CryptoRandom);

                    list.Insert(0, randomContractTxSource);
                    return true;
                }

                return false;
            }
            
            if (_context.ChainSpec == null) throw new StepDependencyException(nameof(_context.ChainSpec));
            if (_context.BlockTree == null) throw new StepDependencyException(nameof(_context.BlockTree));
            if (_context.Signer == null) throw new StepDependencyException(nameof(_context.Signer));

            IList<ITxSource> txSources = new List<ITxSource> { base.CreateTxSourceForProducer(processingEnv, readOnlyTxProcessorSource) };
            bool needSigner = false;
            
            needSigner |= CheckAddPosdaoTransactions(txSources, _context.ChainSpec.AuRa.PosdaoTransition);
            needSigner |= CheckAddRandomnessTransactions(txSources, _context.ChainSpec.AuRa.RandomnessContractAddress, _context.Signer);

            ITxSource txSource = txSources.Count > 1 ? new CompositeTxSource(txSources.ToArray()) : txSources[0];

            if (needSigner)
            {
                TxSealer transactionSealer = new TxSealer(_context.Signer, _context.Timestamper); 
                txSource = new GeneratedTxSourceSealer(txSource, transactionSealer, processingEnv.StateReader, _context.LogManager);
            }

            var txPermissionFilter = GetTxPermissionFilter(processingEnv, readOnlyTxProcessorSource);
            
            if (txPermissionFilter != null)
            {
                txSource = new FilteredTxSource(txSource, txPermissionFilter);
            }

            return txSource;
        }

        private ITxFilter? GetTxPermissionFilter(
            ReadOnlyTxProcessingEnv environment, 
            ReadOnlyTxProcessorSource readOnlyTxProcessorSource)
        {
            if (_context.ChainSpec == null) throw new StepDependencyException(nameof(_context.ChainSpec));
            
            if (_context.ChainSpec.Parameters.TransactionPermissionContract != null)
            {
                var txPermissionFilter = new PermissionBasedTxFilter(
                    new VersionedTransactionPermissionContract(_context.AbiEncoder,
                        _context.ChainSpec.Parameters.TransactionPermissionContract,
                        _context.ChainSpec.Parameters.TransactionPermissionContractTransition ?? 0, 
                        readOnlyTxProcessorSource,
                        _context.TransactionPermissionContractVersions),
                    _context.TxFilterCache,
                    environment.StateProvider,
                    _context.LogManager);
                
                return txPermissionFilter;
            }

            return null;
        }
        
        private IGasLimitCalculator? CreateGasLimitCalculator(ReadOnlyTxProcessorSource readOnlyTxProcessorSource)
        {
            if (_context.ChainSpec == null) throw new StepDependencyException(nameof(_context.ChainSpec));
            var blockGasLimitContractTransitions = _context.ChainSpec.AuRa.BlockGasLimitContractTransitions;

            IGasLimitCalculator gasLimitCalculator =
                new TargetAdjustedGasLimitCalculator(_context.SpecProvider, _context.Config<IMiningConfig>());
            if (blockGasLimitContractTransitions?.Any() == true)
            {
                AuRaContractGasLimitOverride auRaContractGasLimitOverride =
                    new AuRaContractGasLimitOverride(
                        blockGasLimitContractTransitions.Select(blockGasLimitContractTransition => 
                                new BlockGasLimitContract(
                                    _context.AbiEncoder, 
                                    blockGasLimitContractTransition.Value, 
                                    blockGasLimitContractTransition.Key, 
                                    readOnlyTxProcessorSource))
                        .ToArray<IBlockGasLimitContract>(), 
                        _context.GasLimitCalculatorCache, 
                        _auraConfig?.Minimum2MlnGasPerBlockWhenUsingBlockGasLimitContract == true, 
                        gasLimitCalculator, 
                        _context.LogManager);

                gasLimitCalculator = auRaContractGasLimitOverride;
            }

            return gasLimitCalculator;
        }
    }
}
