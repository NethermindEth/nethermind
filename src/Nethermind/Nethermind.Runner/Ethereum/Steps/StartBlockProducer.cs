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
        private readonly EthereumRunnerContext _ethereumContext;

        public StartBlockProducer(EthereumRunnerContext ethereumContext)
        {
            _ethereumContext = ethereumContext;
        }

        public Task Execute()
        {
            IInitConfig initConfig = _ethereumContext.Config<IInitConfig>();
            if (!initConfig.IsMining)
            {
                return Task.CompletedTask;
            }

            switch (_ethereumContext.ChainSpec.SealEngineType)
            {
                case SealEngineType.Clique:
                {
                    if (_ethereumContext.Logger.IsWarn) _ethereumContext.Logger.Warn("Starting Clique block producer & sealer");
                    BuildCliqueProducer();
                    break;
                }

                case SealEngineType.NethDev:
                {
                    if (_ethereumContext.Logger.IsWarn) _ethereumContext.Logger.Warn("Starting Neth Dev block producer & sealer");
                    BuildNethDevProducer();
                    break;
                }

                case SealEngineType.AuRa:
                {
                    if (_ethereumContext.Logger.IsWarn) _ethereumContext.Logger.Warn("Starting AuRa block producer & sealer");
                    BuildAuRaProducer();
                    break;
                }
                
                default:
                    throw new NotSupportedException($"Mining in {_ethereumContext.ChainSpec.SealEngineType} mode is not supported");
            }

            _ethereumContext.BlockProducer.Start();

            SubsystemStateChanged?.Invoke(this, new SubsystemStateEventArgs(EthereumSubsystemState.Running));

            return Task.CompletedTask;
        }

        private void BuildAuRaProducer()
        {
            IAuRaValidatorProcessor validator = null;
            IAuRaStepCalculator stepCalculator = new AuRaStepCalculator(_ethereumContext.ChainSpec.AuRa.StepDuration, _ethereumContext.Timestamper);
            AbiEncoder abiEncoder = new AbiEncoder();
            BlockProducerContext producerContext = GetProducerChain(
                t => new AuRaRewardCalculator(_ethereumContext.ChainSpec.AuRa, abiEncoder, t),
                (db, s, b, t, l) => new[] {validator = new AuRaAdditionalBlockProcessorFactory(s, abiEncoder, t, new SingletonTransactionProcessorFactory(t), b, _ethereumContext.ReceiptStorage, _ethereumContext.ValidatorStore, l).CreateValidatorProcessor(_ethereumContext.ChainSpec.AuRa.Validators)});
            _ethereumContext.BlockProducer = new AuRaBlockProducer(
                producerContext.PendingTxSelector,
                producerContext.ChainProcessor,
                producerContext.ReadOnlyStateProvider,
                _ethereumContext.Sealer,
                _ethereumContext.BlockTree,
                _ethereumContext.BlockProcessingQueue,
                _ethereumContext.Timestamper,
                _ethereumContext.LogManager,
                stepCalculator,
                _ethereumContext.Config<IAuraConfig>(),
                _ethereumContext.NodeKey.Address);
            validator.SetFinalizationManager(_ethereumContext.FinalizationManager, true);
        }

        private void BuildNethDevProducer()
        {
            BlockProducerContext producerChain = GetProducerChain();
            _ethereumContext.BlockProducer = new DevBlockProducer(
                producerChain.PendingTxSelector,
                producerChain.ChainProcessor,
                producerChain.ReadOnlyStateProvider,
                _ethereumContext.BlockTree,
                _ethereumContext.BlockProcessingQueue,
                _ethereumContext.TxPool,
                _ethereumContext.Timestamper,
                _ethereumContext.LogManager);
        }

        private void BuildCliqueProducer()
        {
            BlockProducerContext producerChain = GetProducerChain();
            CliqueConfig cliqueConfig = new CliqueConfig();
            cliqueConfig.BlockPeriod = _ethereumContext.ChainSpec.Clique.Period;
            cliqueConfig.Epoch = _ethereumContext.ChainSpec.Clique.Epoch;
            _ethereumContext.BlockProducer = new CliqueBlockProducer(
                producerChain.PendingTxSelector,
                producerChain.ChainProcessor,
                producerChain.ReadOnlyStateProvider,
                _ethereumContext.BlockTree,
                _ethereumContext.Timestamper,
                _ethereumContext.CryptoRandom,
                _ethereumContext.SnapshotManager,
                (CliqueSealer) _ethereumContext.Sealer,
                _ethereumContext.NodeKey.Address,
                cliqueConfig,
                _ethereumContext.LogManager);
        }
        
        private BlockProducerContext GetProducerChain(
            Func<ITransactionProcessor, IRewardCalculator> rewardCalculatorFactory = null,
            Func<IDb, IStateProvider, IBlockTree, ITransactionProcessor, ILogManager, IEnumerable<IAdditionalBlockProcessor>> createAdditionalBlockProcessors = null,
            bool allowStateModification = false)
        {
            // TODO: use ReadOnlyChainProcessingEnv here
            
            var logManager = _ethereumContext.LogManager;
            var specProvider = _ethereumContext.SpecProvider;
            var blockValidator = _ethereumContext.BlockValidator;
            var recoveryStep = _ethereumContext.RecoveryStep;
            var txPool = _ethereumContext.TxPool;

            var readOnlyDbProvider = new ReadOnlyDbProvider(_ethereumContext.DbProvider, allowStateModification);
            var readOnlyBlockTree = new ReadOnlyBlockTree(_ethereumContext.BlockTree);
            var readOnlyStateProvider = new StateProvider(readOnlyDbProvider.StateDb, readOnlyDbProvider.CodeDb, logManager);
            var readOnlyStorageProvider = new StorageProvider(readOnlyDbProvider.StateDb, readOnlyStateProvider, logManager);
            var readOnlyBlockHashProvider = new BlockhashProvider(readOnlyBlockTree, logManager);
            var readOnlyVirtualMachine = new VirtualMachine(readOnlyStateProvider, readOnlyStorageProvider, readOnlyBlockHashProvider, specProvider, logManager);
            var readOnlyTxProcessor = new TransactionProcessor(specProvider, readOnlyStateProvider, readOnlyStorageProvider, readOnlyVirtualMachine, logManager);

            var additionalBlockProcessors = createAdditionalBlockProcessors?.Invoke(readOnlyDbProvider.StateDb, readOnlyStateProvider, readOnlyBlockTree, readOnlyTxProcessor, logManager);
            var blockProcessor = new BlockProcessor(specProvider, blockValidator, rewardCalculatorFactory(readOnlyTxProcessor), readOnlyTxProcessor, readOnlyDbProvider.StateDb, readOnlyDbProvider.CodeDb, readOnlyStateProvider, readOnlyStorageProvider, txPool, _ethereumContext.ReceiptStorage, logManager, additionalBlockProcessors);
            var chainProcessor = new OneTimeChainProcessor(readOnlyDbProvider, new BlockchainProcessor(readOnlyBlockTree, blockProcessor, recoveryStep, logManager, false));
            var pendingTxSelector = new PendingTxSelector(_ethereumContext.TxPool, readOnlyStateProvider, _ethereumContext.LogManager);
            
            BlockProducerContext producerChain = new BlockProducerContext();
            producerChain.ChainProcessor = chainProcessor;
            producerChain.ReadOnlyStateProvider = readOnlyStateProvider;
            producerChain.PendingTxSelector = pendingTxSelector;
            return producerChain;
        }

        public event EventHandler<SubsystemStateEventArgs> SubsystemStateChanged;

        public EthereumSubsystem MonitoredSubsystem => EthereumSubsystem.Mining;
    }
}