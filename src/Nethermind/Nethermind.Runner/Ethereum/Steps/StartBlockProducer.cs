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
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.AuRa;
using Nethermind.AuRa.Config;
using Nethermind.AuRa.Rewards;
using Nethermind.AuRa.Validators;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Producers;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.Validators;
using Nethermind.Clique;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Subsystems;
using Nethermind.Store;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependency(typeof(InitializeNetwork), typeof(InitializeFinalization))]
    public class StartBlockProducer : IStep, ISubsystemStateAware
    {
        private readonly EthereumRunnerContext _context;

        public StartBlockProducer(EthereumRunnerContext context)
        {
            _context = context;
        }

        public Task Execute()
        {
            IInitConfig jsonRpcConfig = _context.Config<IInitConfig>();
            if (!jsonRpcConfig.IsMining)
            {
                return Task.CompletedTask;
            }
            
            // TODO: use ReadOnlyChainProcessingEnv here
            ReadOnlyChain GetProducerChain(
                Func<ITransactionProcessor, IRewardCalculator> rewardCalculatorFactory = null,
                Func<IDb, IStateProvider, IBlockTree, ITransactionProcessor, ILogManager, IEnumerable<IAdditionalBlockProcessor>> createAdditionalBlockProcessors = null,
                bool allowStateModification = false)
            {
                var logManager = _context.LogManager;
                var specProvider = _context.SpecProvider;
                var blockValidator = _context.BlockValidator;
                var recoveryStep = _context.RecoveryStep;
                var txPool = _context.TxPool;

                var readOnlyDbProvider = new ReadOnlyDbProvider(_context.DbProvider, allowStateModification);
                var readOnlyBlockTree = new ReadOnlyBlockTree(_context.BlockTree);
                var readOnlyStateProvider = new StateProvider(readOnlyDbProvider.StateDb, readOnlyDbProvider.CodeDb, logManager);
                var readOnlyStorageProvider = new StorageProvider(readOnlyDbProvider.StateDb, readOnlyStateProvider, logManager);
                var readOnlyBlockHashProvider = new BlockhashProvider(readOnlyBlockTree, logManager);
                var readOnlyVirtualMachine = new VirtualMachine(readOnlyStateProvider, readOnlyStorageProvider, readOnlyBlockHashProvider, specProvider, logManager);
                var readOnlyTxProcessor = new TransactionProcessor(specProvider, readOnlyStateProvider, readOnlyStorageProvider, readOnlyVirtualMachine, logManager);
                
                IEnumerable<IAdditionalBlockProcessor> additionalBlockProcessors = createAdditionalBlockProcessors?.Invoke(readOnlyDbProvider.StateDb, readOnlyStateProvider, readOnlyBlockTree, readOnlyTxProcessor, logManager);
                IBlockProcessor blockProcessor = new BlockProcessor(specProvider, blockValidator, rewardCalculatorFactory(readOnlyTxProcessor), readOnlyTxProcessor, readOnlyDbProvider.StateDb, readOnlyDbProvider.CodeDb, readOnlyStateProvider, readOnlyStorageProvider, txPool, _context.ReceiptStorage, logManager, additionalBlockProcessors);
                IBlockchainProcessor chainProcessor = new OneTimeChainProcessor(readOnlyDbProvider, new BlockchainProcessor(readOnlyBlockTree, blockProcessor, recoveryStep, logManager, false));
                
                ReadOnlyChain producerChain = new ReadOnlyChain();
                producerChain.ChainProcessor = chainProcessor;
                producerChain.ReadOnlyStateProvider = readOnlyStateProvider;
                return producerChain;
            }

            switch (_context.ChainSpec.SealEngineType)
            {
                case SealEngineType.Clique:
                {
                    ReadOnlyChain producerChain = GetProducerChain();
                    PendingTransactionSelector pendingTransactionSelector = new PendingTransactionSelector(_context.TxPool, producerChain.ReadOnlyStateProvider, _context.LogManager);
                    if (_context.Logger.IsWarn) _context.Logger.Warn("Starting Clique block producer & sealer");
                    CliqueConfig cliqueConfig = new CliqueConfig();
                    cliqueConfig.BlockPeriod = _context.ChainSpec.Clique.Period;
                    cliqueConfig.Epoch = _context.ChainSpec.Clique.Epoch;
                    _context.BlockProducer = new CliqueBlockProducer(pendingTransactionSelector, producerChain.ChainProcessor, _context.BlockTree, _context.Timestamper, _context.CryptoRandom, producerChain.ReadOnlyStateProvider, _context.SnapshotManager, (CliqueSealer) _context.Sealer, _context.NodeKey.Address, cliqueConfig, _context.LogManager);
                    break;
                }

                case SealEngineType.NethDev:
                {
                    ReadOnlyChain producerChain = GetProducerChain();
                    PendingTransactionSelector pendingTransactionSelector = new PendingTransactionSelector(_context.TxPool, producerChain.ReadOnlyStateProvider, _context.LogManager);
                    if (_context.Logger.IsWarn) _context.Logger.Warn("Starting Dev block producer & sealer");
                    _context.BlockProducer = new DevBlockProducer(pendingTransactionSelector, producerChain.ChainProcessor, _context.BlockTree, _context.BlockProcessingQueue, producerChain.ReadOnlyStateProvider, _context.Timestamper, _context.LogManager, _context.TxPool);
                    break;
                }

                case SealEngineType.AuRa:
                {
                    IAuRaValidatorProcessor validator = null;
                    var abiEncoder = new AbiEncoder();                    
                    ReadOnlyChain producerChain = GetProducerChain(t => new AuRaRewardCalculator(_context.ChainSpec.AuRa, abiEncoder, t),
                        (db, s, b, t, l)  => new[] {validator = new AuRaAdditionalBlockProcessorFactory(s, abiEncoder, t, new SingletonTransactionProcessorFactory(t), b, _context.ReceiptStorage, _context.ValidatorStore, l).CreateValidatorProcessor(_context.ChainSpec.AuRa.Validators)});
                    PendingTransactionSelector pendingTransactionSelector = new PendingTransactionSelector(_context.TxPool, producerChain.ReadOnlyStateProvider, _context.LogManager);
                    if (_context.Logger.IsWarn) _context.Logger.Warn("Starting AuRa block producer & sealer");
                    _context.BlockProducer = new AuRaBlockProducer(pendingTransactionSelector, producerChain.ChainProcessor, _context.Sealer, _context.BlockTree, _context.BlockProcessingQueue, producerChain.ReadOnlyStateProvider, _context.Timestamper, _context.LogManager, new AuRaStepCalculator(_context.ChainSpec.AuRa.StepDuration, _context.Timestamper), _context.Config<IAuraConfig>(), _context.NodeKey.Address);
                    validator.SetFinalizationManager(_context.FinalizationManager, true);
                    break;
                }


                default:
                    throw new NotSupportedException($"Mining in {_context.ChainSpec.SealEngineType} mode is not supported");
            }

            _context.BlockProducer.Start();
            
            SubsystemStateChanged?.Invoke(this, new SubsystemStateEventArgs(EthereumSubsystemState.Running));
            
            return Task.CompletedTask;
        }

        public event EventHandler<SubsystemStateEventArgs> SubsystemStateChanged;

        public EthereumSubsystem MonitoredSubsystem => EthereumSubsystem.Mining;
    }
}