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
using Nethermind.AuRa;
using Nethermind.AuRa.Rewards;
using Nethermind.AuRa.Validators;
using Nethermind.Blockchain;
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

            AbiEncoder abiEncoder = new AbiEncoder();
            _context.ValidatorStore = new ValidatorStore(_context.DbProvider.BlockInfosDb);
            IReadOnlyTransactionProcessorSource readOnlyTransactionProcessorSource = new ReadOnlyTransactionProcessorSource(_context.DbProvider, _context.BlockTree, _context.SpecProvider, _context.LogManager);
            IAuRaValidatorProcessorExtension validatorProcessorExtension = new AuRaValidatorProcessorFactory(_context.StateProvider, abiEncoder, _context.TransactionProcessor, readOnlyTransactionProcessorSource, _context.BlockTree, _context.ReceiptStorage, _context.ValidatorStore, _context.LogManager)
                .CreateValidatorProcessor(_context.ChainSpec.AuRa.Validators);

            AuRaStepCalculator auRaStepCalculator = new AuRaStepCalculator(_context.ChainSpec.AuRa.StepDuration, _context.Timestamper);
            _context.SealValidator = new AuRaSealValidator(_context.ChainSpec.AuRa, auRaStepCalculator, _context.ValidatorStore, _context.EthereumEcdsa, _context.LogManager);
            _context.RewardCalculatorSource = AuRaRewardCalculator.GetSource(_context.ChainSpec.AuRa, abiEncoder);
            _context.Sealer = new AuRaSealer(_context.BlockTree, _context.ValidatorStore, auRaStepCalculator, _context.NodeKey.Address, new BasicWallet(_context.NodeKey), new ValidSealerStrategy(), _context.LogManager);
            _context.AuRaBlockProcessorExtension = validatorProcessorExtension;
        }
    }
}