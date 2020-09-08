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
using System.Linq;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Rewards;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Transactions;
using Nethermind.Evm;
using Nethermind.Runner.Ethereum.Api;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.Runner.Ethereum.Steps
{
    public class InitializeBlockchainAuRa : InitializeBlockchain
    {
        private readonly AuRaNethermindApi _api;
        private ReadOnlyTxProcessorSource? _readOnlyTransactionProcessorSource;
        private AuRaSealValidator? _sealValidator;

        public InitializeBlockchainAuRa(AuRaNethermindApi api) : base(api)
        {
            _api = api;
        }

        protected override BlockProcessor CreateBlockProcessor()
        {
            if (_api.SpecProvider == null) throw new StepDependencyException(nameof(_api.SpecProvider));
            if (_api.BlockValidator == null) throw new StepDependencyException(nameof(_api.BlockValidator));
            if (_api.RewardCalculatorSource == null) throw new StepDependencyException(nameof(_api.RewardCalculatorSource));
            if (_api.TransactionProcessor == null) throw new StepDependencyException(nameof(_api.TransactionProcessor));
            if (_api.DbProvider == null) throw new StepDependencyException(nameof(_api.DbProvider));
            if (_api.StateProvider == null) throw new StepDependencyException(nameof(_api.StateProvider));
            if (_api.StorageProvider == null) throw new StepDependencyException(nameof(_api.StorageProvider));
            if (_api.TxPool == null) throw new StepDependencyException(nameof(_api.TxPool));
            if (_api.ReceiptStorage == null) throw new StepDependencyException(nameof(_api.ReceiptStorage));

            var processor = new AuRaBlockProcessor(
                _api.SpecProvider,
                _api.BlockValidator,
                _api.RewardCalculatorSource.Get(_api.TransactionProcessor),
                _api.TransactionProcessor,
                _api.DbProvider.StateDb,
                _api.DbProvider.CodeDb,
                _api.StateProvider,
                _api.StorageProvider,
                _api.TxPool,
                _api.ReceiptStorage,
                _api.LogManager,
                _api.BlockTree,
                GetTxPermissionFilter(),
                GetGasLimitCalculator());
            
            var auRaValidator = CreateAuRaValidator(processor);
            processor.AuRaValidator = auRaValidator;
            var reportingValidator = auRaValidator.GetReportingValidator();
            _api.ReportingValidator = reportingValidator;
            if (_sealValidator != null)
            {
                _sealValidator.ReportingValidator = reportingValidator;
            }
            
            return processor;
        }

        private IAuRaValidator CreateAuRaValidator(IBlockProcessor processor)
        {
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
            if (_api.BlockTree == null) throw new StepDependencyException(nameof(_api.BlockTree));
            if (_api.Signer == null) throw new StepDependencyException(nameof(_api.Signer));

            var chainSpecAuRa = _api.ChainSpec.AuRa;
            
            _api.FinalizationManager = new AuRaBlockFinalizationManager(
                _api.BlockTree, 
                _api.ChainLevelInfoRepository, 
                processor, 
                _api.ValidatorStore, 
                new ValidSealerStrategy(), 
                _api.LogManager, 
                chainSpecAuRa.TwoThirdsMajorityTransition);
            
            IAuRaValidator validator = new AuRaValidatorFactory(
                    _api.StateProvider, 
                    _api.AbiEncoder, 
                    _api.TransactionProcessor, 
                    GetReadOnlyTransactionProcessorSource(), 
                    _api.BlockTree, 
                    _api.ReceiptStorage, 
                    _api.ValidatorStore,
                    _api.FinalizationManager,
                    new TxPoolSender(_api.TxPool, new NonceReservingTxSealer(_api.Signer, _api.Timestamper, _api.TxPool)), 
                    _api.TxPool,
                    _api.Config<IMiningConfig>(),
                    _api.LogManager,
                    _api.Signer,
                    _api.ReportingContractValidatorCache,
                    chainSpecAuRa.PosdaoTransition,
                    false)
                .CreateValidatorProcessor(chainSpecAuRa.Validators, _api.BlockTree.Head?.Header);

            if (validator is IDisposable disposableValidator)
            {
                _api.DisposeStack.Push(disposableValidator);
            }

            return validator;
        }

        private ITxFilter? GetTxPermissionFilter()
        {
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
            
            if (_api.ChainSpec.Parameters.TransactionPermissionContract != null)
            {
                _api.TxFilterCache = new PermissionBasedTxFilter.Cache();
                
                var txPermissionFilter = new PermissionBasedTxFilter(
                    new VersionedTransactionPermissionContract(_api.AbiEncoder,
                        _api.ChainSpec.Parameters.TransactionPermissionContract,
                        _api.ChainSpec.Parameters.TransactionPermissionContractTransition ?? 0, 
                        GetReadOnlyTransactionProcessorSource(), 
                        _api.TransactionPermissionContractVersions),
                    _api.TxFilterCache,
                    _api.StateProvider,
                    _api.LogManager);
                
                return txPermissionFilter;
            }

            return null;
        }
        
        private AuRaContractGasLimitOverride? GetGasLimitCalculator()
        {
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
            var blockGasLimitContractTransitions = _api.ChainSpec.AuRa.BlockGasLimitContractTransitions;
            
            if (blockGasLimitContractTransitions?.Any() == true)
            {
                _api.GasLimitCalculatorCache = new AuRaContractGasLimitOverride.Cache();
                
                AuRaContractGasLimitOverride gasLimitCalculator = new AuRaContractGasLimitOverride(
                    blockGasLimitContractTransitions.Select(blockGasLimitContractTransition =>
                        new BlockGasLimitContract(
                            _api.AbiEncoder,
                            blockGasLimitContractTransition.Value,
                            blockGasLimitContractTransition.Key,
                            GetReadOnlyTransactionProcessorSource()))
                        .ToArray<IBlockGasLimitContract>(),
                    _api.GasLimitCalculatorCache,
                    _api.Config<IAuraConfig>().Minimum2MlnGasPerBlockWhenUsingBlockGasLimitContract,
                    new TargetAdjustedGasLimitCalculator(_api.SpecProvider, _api.Config<IMiningConfig>()), 
                    _api.LogManager);
                
                return gasLimitCalculator;
            }

            // do not return target gas limit calculator here - this is used for validation to check if the override should have been used
            return null;
        }

        protected override void InitSealEngine()
        {
            if (_api.DbProvider == null) throw new StepDependencyException(nameof(_api.DbProvider));
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
            if (_api.EthereumEcdsa == null) throw new StepDependencyException(nameof(_api.EthereumEcdsa));
            if (_api.BlockTree == null) throw new StepDependencyException(nameof(_api.BlockTree));
            
            _api.ValidatorStore = new ValidatorStore(_api.DbProvider.BlockInfosDb);

            ValidSealerStrategy validSealerStrategy = new ValidSealerStrategy();
            AuRaStepCalculator auRaStepCalculator = new AuRaStepCalculator(_api.ChainSpec.AuRa.StepDuration, _api.Timestamper, _api.LogManager);
            _api.SealValidator = _sealValidator = new AuRaSealValidator(_api.ChainSpec.AuRa, auRaStepCalculator, _api.BlockTree, _api.ValidatorStore, validSealerStrategy, _api.EthereumEcdsa, _api.LogManager);
            _api.RewardCalculatorSource = AuRaRewardCalculator.GetSource(_api.ChainSpec.AuRa, _api.AbiEncoder);
            _api.Sealer = new AuRaSealer(_api.BlockTree, _api.ValidatorStore, auRaStepCalculator, _api.Signer, validSealerStrategy, _api.LogManager);
        }

        private IReadOnlyTransactionProcessorSource GetReadOnlyTransactionProcessorSource() => 
            _readOnlyTransactionProcessorSource ??= new ReadOnlyTxProcessorSource(_api.DbProvider, _api.BlockTree, _api.SpecProvider, _api.LogManager);

        protected override HeaderValidator CreateHeaderValidator()
        {
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
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
    }
}
