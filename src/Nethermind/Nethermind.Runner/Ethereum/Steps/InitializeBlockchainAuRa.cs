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

using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Rewards;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Evm;
using Nethermind.Runner.Ethereum.Context;
using Nethermind.Wallet;

namespace Nethermind.Runner.Ethereum.Steps
{
    public class InitializeBlockchainAuRa : InitializeBlockchain
    {
        private readonly AuRaEthereumRunnerContext _context;

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
                _context.AuRaBlockProcessorExtension);
        }

        protected override void InitSealEngine()
        {
            if (_context.DbProvider == null) throw new StepDependencyException(nameof(_context.DbProvider));
            if (_context.ChainSpec == null) throw new StepDependencyException(nameof(_context.ChainSpec));
            if (_context.EthereumEcdsa == null) throw new StepDependencyException(nameof(_context.EthereumEcdsa));
            if (_context.NodeKey == null) throw new StepDependencyException(nameof(_context.NodeKey));
            
            _context.ValidatorStore = new ValidatorStore(_context.DbProvider.BlockInfosDb);
            IReadOnlyTransactionProcessorSource readOnlyTransactionProcessorSource = new ReadOnlyTransactionProcessorSource(_context.DbProvider, _context.BlockTree, _context.SpecProvider, _context.LogManager);
            IAuRaValidatorProcessorExtension validatorProcessorExtension = new AuRaValidatorProcessorFactory(_context.StateProvider, _context.AbiEncoder, _context.TransactionProcessor, readOnlyTransactionProcessorSource, _context.BlockTree, _context.ReceiptStorage, _context.ValidatorStore, _context.LogManager)
                .CreateValidatorProcessor(_context.ChainSpec.AuRa.Validators);

            AuRaStepCalculator auRaStepCalculator = new AuRaStepCalculator(_context.ChainSpec.AuRa.StepDuration, _context.Timestamper);
            _context.SealValidator = new AuRaSealValidator(_context.ChainSpec.AuRa, auRaStepCalculator, _context.ValidatorStore, _context.EthereumEcdsa, _context.LogManager);
            _context.RewardCalculatorSource = AuRaRewardCalculator.GetSource(_context.ChainSpec.AuRa, _context.AbiEncoder);
            _context.Sealer = new AuRaSealer(_context.BlockTree, _context.ValidatorStore, auRaStepCalculator, _context.NodeKey.Address, new BasicWallet(_context.NodeKey), new ValidSealerStrategy(), _context.LogManager);
            _context.AuRaBlockProcessorExtension = validatorProcessorExtension;
        }
    }
}