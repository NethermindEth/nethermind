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
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Rewards;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Evm;
using Nethermind.Facade.Transactions;
using Nethermind.Runner.Ethereum.Context;
using Nethermind.Wallet;

namespace Nethermind.Runner.Ethereum.Steps
{
    public class InitializeBlockchainAuRa : InitializeBlockchain
    {
        private readonly AuRaEthereumRunnerContext _context;
        private ReadOnlyTransactionProcessorSource? _readOnlyTransactionProcessorSource;
        private AuRaSealValidator? _sealValidator;

        public InitializeBlockchainAuRa(AuRaEthereumRunnerContext context) : base(context)
        {
            _context = context;
        }

        protected override BlockProcessor CreateBlockProcessor()
        {
            if (_context.SpecProvider == null) throw new StepDependencyException(nameof(_context.SpecProvider));
            if (_context.BlockValidator == null) throw new StepDependencyException(nameof(_context.BlockValidator));
            if (_context.RewardCalculatorSource == null) throw new StepDependencyException(nameof(_context.RewardCalculatorSource));
            if (_context.TransactionProcessor == null) throw new StepDependencyException(nameof(_context.TransactionProcessor));
            if (_context.DbProvider == null) throw new StepDependencyException(nameof(_context.DbProvider));
            if (_context.StateProvider == null) throw new StepDependencyException(nameof(_context.StateProvider));
            if (_context.StorageProvider == null) throw new StepDependencyException(nameof(_context.StorageProvider));
            if (_context.TxPool == null) throw new StepDependencyException(nameof(_context.TxPool));
            if (_context.ReceiptStorage == null) throw new StepDependencyException(nameof(_context.ReceiptStorage));

            var processor = new AuRaBlockProcessor(
                _context.SpecProvider,
                _context.BlockValidator,
                _context.RewardCalculatorSource.Get(_context.TransactionProcessor),
                _context.TransactionProcessor,
                _context.DbProvider.StateDb,
                _context.DbProvider.CodeDb,
                _context.StateProvider,
                _context.StorageProvider,
                _context.TxPool,
                _context.ReceiptStorage,
                _context.LogManager,
                _context.BlockTree,
                GetTxPermissionFilter(),
                GetGasLimitOverride());
            
            var auRaValidator = CreateAuRaValidator(processor);
            processor.AuRaValidator = auRaValidator;
            var reportingValidator = auRaValidator.GetReportingValidator();
            _context.ReportingValidator = reportingValidator;
            if (_sealValidator != null)
            {
                _sealValidator.ReportingValidator = reportingValidator;
            }
            
            return processor;
        }

        private IAuRaValidator CreateAuRaValidator(IBlockProcessor processor)
        {
            if (_context.ChainSpec == null) throw new StepDependencyException(nameof(_context.ChainSpec));
            if (_context.BlockTree == null) throw new StepDependencyException(nameof(_context.BlockTree));
            if (_context.Signer == null) throw new StepDependencyException(nameof(_context.Signer));

            var chainSpecAuRa = _context.ChainSpec.AuRa;
            
            _context.FinalizationManager = new AuRaBlockFinalizationManager(
                _context.BlockTree, 
                _context.ChainLevelInfoRepository, 
                processor, 
                _context.ValidatorStore, 
                new ValidSealerStrategy(), 
                _context.LogManager, 
                chainSpecAuRa.TwoThirdsMajorityTransition);
            
            IAuRaValidator validator = new AuRaValidatorFactory(
                    _context.StateProvider, 
                    _context.AbiEncoder, 
                    _context.TransactionProcessor, 
                    GetReadOnlyTransactionProcessorSource(), 
                    _context.BlockTree, 
                    _context.ReceiptStorage, 
                    _context.ValidatorStore,
                    _context.FinalizationManager,
                    new TxPoolSender(_context.TxPool, new TxNonceTxPoolReserveSealer(_context.Signer, _context.Timestamper, _context.TxPool)), 
                    _context.TxPool,
                    _context.LogManager,
                    _context.Signer,
                    _context.ReportingContractValidatorCache,
                    chainSpecAuRa.PosdaoTransition,
                    false)
                .CreateValidatorProcessor(chainSpecAuRa.Validators, _context.BlockTree.Head?.Header);

            if (validator is IDisposable disposableValidator)
            {
                _context.DisposeStack.Push(disposableValidator);
            }

            return validator;
        }

        private ITxPermissionFilter? GetTxPermissionFilter()
        {
            if (_context.ChainSpec == null) throw new StepDependencyException(nameof(_context.ChainSpec));
            
            if (_context.ChainSpec.Parameters.TransactionPermissionContract != null)
            {
                _context.TxFilterCache = new ITxPermissionFilter.Cache();
                
                var txPermissionFilter = new TxPermissionFilter(
                    new VersionedTransactionPermissionContract(_context.AbiEncoder,
                        _context.ChainSpec.Parameters.TransactionPermissionContract,
                        _context.ChainSpec.Parameters.TransactionPermissionContractTransition ?? 0, 
                        GetReadOnlyTransactionProcessorSource(), 
                        _context.TransactionPermissionContractVersions),
                    _context.TxFilterCache,
                    _context.StateProvider,
                    _context.LogManager);
                
                return txPermissionFilter;
            }

            return null;
        }
        
        private IGasLimitOverride? GetGasLimitOverride()
        {
            if (_context.ChainSpec == null) throw new StepDependencyException(nameof(_context.ChainSpec));
            var blockGasLimitContractTransitions = _context.ChainSpec.AuRa.BlockGasLimitContractTransitions;
            
            if (blockGasLimitContractTransitions?.Any() == true)
            {
                _context.GasLimitOverrideCache = new IGasLimitOverride.Cache();
                
                var gasLimitOverride = new AuRaContractGasLimitOverride(
                    blockGasLimitContractTransitions.Select(blockGasLimitContractTransition =>
                        new BlockGasLimitContract(
                            _context.AbiEncoder,
                            blockGasLimitContractTransition.Value,
                            blockGasLimitContractTransition.Key,
                            GetReadOnlyTransactionProcessorSource()))
                        .ToArray<IBlockGasLimitContract>(),
                    _context.GasLimitOverrideCache,
                    _context.Config<IAuraConfig>().Minimum2MlnGasPerBlockWhenUsingBlockGasLimitContract,
                    _context.LogManager);
                
                return gasLimitOverride;
            }

            return null;
        }

        protected override void InitSealEngine()
        {
            if (_context.DbProvider == null) throw new StepDependencyException(nameof(_context.DbProvider));
            if (_context.ChainSpec == null) throw new StepDependencyException(nameof(_context.ChainSpec));
            if (_context.EthereumEcdsa == null) throw new StepDependencyException(nameof(_context.EthereumEcdsa));
            
            _context.ValidatorStore = new ValidatorStore(_context.DbProvider.BlockInfosDb);

            AuRaStepCalculator auRaStepCalculator = new AuRaStepCalculator(_context.ChainSpec.AuRa.StepDuration, _context.Timestamper, _context.LogManager);
            _context.SealValidator = _sealValidator = new AuRaSealValidator(_context.ChainSpec.AuRa, auRaStepCalculator, _context.ValidatorStore, _context.EthereumEcdsa, _context.LogManager);
            _context.RewardCalculatorSource = AuRaRewardCalculator.GetSource(_context.ChainSpec.AuRa, _context.AbiEncoder);
            _context.Sealer = new AuRaSealer(_context.BlockTree, _context.ValidatorStore, auRaStepCalculator, _context.Signer, new ValidSealerStrategy(), _context.LogManager);
        }

        private IReadOnlyTransactionProcessorSource GetReadOnlyTransactionProcessorSource() => 
            _readOnlyTransactionProcessorSource ??= new ReadOnlyTransactionProcessorSource(_context.DbProvider, _context.BlockTree, _context.SpecProvider, _context.LogManager);

        protected override HeaderValidator CreateHeaderValidator()
        {
            if (_context.ChainSpec == null) throw new StepDependencyException(nameof(_context.ChainSpec));
            var blockGasLimitContractTransitions = _context.ChainSpec.AuRa.BlockGasLimitContractTransitions;
            return blockGasLimitContractTransitions?.Any() == true
                ? new AuRaHeaderValidator(
                    _context.BlockTree,
                    _context.SealValidator,
                    _context.SpecProvider,
                    _context.LogManager,
                    blockGasLimitContractTransitions.Keys.ToArray())
                : base.CreateHeaderValidator();
        }
    }
}
