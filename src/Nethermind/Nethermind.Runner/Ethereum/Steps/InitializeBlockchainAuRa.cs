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

using System.Linq;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Rewards;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Runner.Ethereum.Context;
using Nethermind.Wallet;

namespace Nethermind.Runner.Ethereum.Steps
{
    public class InitializeBlockchainAuRa : InitializeBlockchain
    {
        private readonly AuRaEthereumRunnerContext _context;
        private ReadOnlyTransactionProcessorSource? _readOnlyTransactionProcessorSource;

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
            if (_context.AuRaBlockProcessorExtension == null) throw new StepDependencyException(nameof(_context.AuRaBlockProcessorExtension));

            return new AuRaBlockProcessor(
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
                _context.AuRaBlockProcessorExtension,
                _context.BlockTree,
                GetTxPermissionFilter(),
                GetGasLimitOverride());
        }

        private ITxPermissionFilter? GetTxPermissionFilter()
        {
            if (_context.ChainSpec == null) throw new StepDependencyException(nameof(_context.ChainSpec));
            
            if (_context.ChainSpec.Parameters.TransactionPermissionContract != null)
            {
                _context.TxFilterCache = new ITxPermissionFilter.Cache();
                
                var txPermissionFilter = new TxPermissionFilter(
                    new TransactionPermissionContract(
                        _context.TransactionProcessor,
                        _context.AbiEncoder,
                        _context.ChainSpec.Parameters.TransactionPermissionContract,
                        _context.ChainSpec.Parameters.TransactionPermissionContractTransition ?? 0, 
                        GetReadOnlyTransactionProcessorSource()),
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
                            _context.TransactionProcessor,
                            _context.AbiEncoder,
                            blockGasLimitContractTransition.Value,
                            blockGasLimitContractTransition.Key,
                            GetReadOnlyTransactionProcessorSource())).ToArray(),
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
            if (_context.NodeKey == null) throw new StepDependencyException(nameof(_context.NodeKey));
            
            _context.ValidatorStore = new ValidatorStore(_context.DbProvider.BlockInfosDb);
            
            IAuRaValidatorProcessorExtension validatorProcessorExtension = new AuRaValidatorProcessorFactory(_context.StateProvider, _context.AbiEncoder, _context.TransactionProcessor, GetReadOnlyTransactionProcessorSource(), _context.BlockTree, _context.ReceiptStorage, _context.ValidatorStore, _context.LogManager)
                .CreateValidatorProcessor(_context.ChainSpec.AuRa.Validators);

            AuRaStepCalculator auRaStepCalculator = new AuRaStepCalculator(_context.ChainSpec.AuRa.StepDuration, _context.Timestamper);
            _context.SealValidator = new AuRaSealValidator(_context.ChainSpec.AuRa, auRaStepCalculator, _context.ValidatorStore, _context.EthereumEcdsa, _context.LogManager);
            _context.RewardCalculatorSource = AuRaRewardCalculator.GetSource(_context.ChainSpec.AuRa, _context.AbiEncoder);
            _context.Sealer = new AuRaSealer(_context.BlockTree, _context.ValidatorStore, auRaStepCalculator, _context.NodeKey.Address, new BasicWallet(_context.NodeKey), new ValidSealerStrategy(), _context.LogManager);
            _context.AuRaBlockProcessorExtension = validatorProcessorExtension;
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