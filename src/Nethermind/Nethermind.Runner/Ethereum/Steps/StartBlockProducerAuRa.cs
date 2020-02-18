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
using Nethermind.AuRa.Config;
using Nethermind.Blockchain;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Context;
using Nethermind.Store;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(InitializeNetwork), typeof(InitializeFinalizationAuRa), typeof(SetupKeyStore))]
    public class StartBlockProducerAuRa : StartBlockProducer
    {
        private readonly AuRaEthereumRunnerContext _context;

        public StartBlockProducerAuRa(AuRaEthereumRunnerContext context) : base(context)
        {
            _context = context;
        }

        protected override void BuildProducer()
        {
            ILogger logger = _context.LogManager.GetClassLogger();
            if (logger.IsWarn) logger.Warn("Starting AuRa block producer & sealer");
            
            IAuRaStepCalculator stepCalculator = new AuRaStepCalculator(_context.ChainSpec.AuRa.StepDuration, _context.Timestamper);
            BlockProducerContext producerContext = GetProducerChain();
            var auraConfig = _context.Config<IAuraConfig>();
            _context.BlockProducer = new AuRaBlockProducer(
                producerContext.PendingTxSelector,
                producerContext.ChainProcessor,
                producerContext.ReadOnlyStateProvider,
                _context.Sealer,
                _context.BlockTree,
                _context.BlockProcessingQueue,
                _context.Timestamper,
                _context.LogManager,
                stepCalculator,
                auraConfig,
                _context.NodeKey.Address);
        }

        protected override BlockProcessor CreateBlockProcessor(ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv, IReadOnlyDbProvider readOnlyDbProvider)
        {
            var validator = new AuRaValidatorProcessorFactory(
                    readOnlyTxProcessingEnv.StateProvider,
                    new AbiEncoder(),
                    readOnlyTxProcessingEnv.TransactionProcessor,
                    new ReadOnlyTransactionProcessorSource(readOnlyTxProcessingEnv),
                    readOnlyTxProcessingEnv.BlockTree,
                    _context.ReceiptStorage,
                    _context.ValidatorStore,
                    _context.LogManager)
                .CreateValidatorProcessor(_context.ChainSpec.AuRa.Validators);
            
            var blockProducer = new AuRaBlockProcessor(
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
                validator);
            
            validator.SetFinalizationManager(_context.FinalizationManager, true);

            return blockProducer;
        }
    }
}