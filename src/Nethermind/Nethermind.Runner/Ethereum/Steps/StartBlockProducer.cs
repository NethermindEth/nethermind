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
using Nethermind.AuRa.Validators;
using Nethermind.Blockchain;
using Nethermind.Clique;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Subsystems;
using Nethermind.Store;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependency(typeof(InitializeNetwork))]
    public class StartBlockProducer : IStep, ISubsystemStateAware
    {
        private readonly EthereumRunnerContext _context;

        public StartBlockProducer(EthereumRunnerContext context)
        {
            _context = context;
        }

        public Task Execute()
        {
            IInitConfig jsonRpcConfig = _context._configProvider.GetConfig<IInitConfig>();
            if (!jsonRpcConfig.IsMining)
            {
                return Task.CompletedTask;
            }

            ReadOnlyChain GetProducerChain(
                Func<IDb, IStateProvider, IBlockTree, ITransactionProcessor, ILogManager, IEnumerable<IAdditionalBlockProcessor>> createAdditionalBlockProcessors = null,
                bool allowStateModification = false)
            {
                IReadOnlyDbProvider minerDbProvider = new ReadOnlyDbProvider(_context._dbProvider, allowStateModification);
                ReadOnlyBlockTree readOnlyBlockTree = new ReadOnlyBlockTree(_context.BlockTree);
                ReadOnlyChain producerChain = new ReadOnlyChain(readOnlyBlockTree, _context._blockValidator, _context._rewardCalculator,
                    _context.SpecProvider, minerDbProvider, _context._recoveryStep, _context.LogManager, _context._txPool, _context._receiptStorage,
                    createAdditionalBlockProcessors);
                return producerChain;
            }

            switch (_context._chainSpec.SealEngineType)
            {
                case SealEngineType.Clique:
                {
                    ReadOnlyChain producerChain = GetProducerChain();
                    PendingTransactionSelector pendingTransactionSelector = new PendingTransactionSelector(_context._txPool, producerChain.ReadOnlyStateProvider, _context.LogManager);
                    if (_context.Logger.IsWarn) _context.Logger.Warn("Starting Clique block producer & sealer");
                    CliqueConfig cliqueConfig = new CliqueConfig();
                    cliqueConfig.BlockPeriod = _context._chainSpec.Clique.Period;
                    cliqueConfig.Epoch = _context._chainSpec.Clique.Epoch;
                    _context._blockProducer = new CliqueBlockProducer(pendingTransactionSelector, producerChain.Processor,
                        _context.BlockTree, _context._timestamper, _context._cryptoRandom, producerChain.ReadOnlyStateProvider, _context._snapshotManager, (CliqueSealer) _context._sealer, _context._nodeKey.Address, cliqueConfig, _context.LogManager);
                    break;
                }

                case SealEngineType.NethDev:
                {
                    ReadOnlyChain producerChain = GetProducerChain();
                    PendingTransactionSelector pendingTransactionSelector = new PendingTransactionSelector(_context._txPool, producerChain.ReadOnlyStateProvider, _context.LogManager);
                    if (_context.Logger.IsWarn) _context.Logger.Warn("Starting Dev block producer & sealer");
                    _context._blockProducer = new DevBlockProducer(pendingTransactionSelector, producerChain.Processor, _context.BlockTree, producerChain.ReadOnlyStateProvider, _context._timestamper, _context.LogManager, _context._txPool);
                    break;
                }

                case SealEngineType.AuRa:
                {
                    IAuRaValidatorProcessor validator = null;
                    ReadOnlyChain producerChain = GetProducerChain((db, s, b, t, l) => new[] {validator = new AuRaAdditionalBlockProcessorFactory(db, s, new AbiEncoder(), t, b, _context._receiptStorage, l).CreateValidatorProcessor(_context._chainSpec.AuRa.Validators)});
                    PendingTransactionSelector pendingTransactionSelector = new PendingTransactionSelector(_context._txPool, producerChain.ReadOnlyStateProvider, _context.LogManager);
                    if (_context.Logger.IsWarn) _context.Logger.Warn("Starting AuRa block producer & sealer");
                    _context._blockProducer = new AuRaBlockProducer(pendingTransactionSelector, producerChain.Processor, _context._sealer, _context.BlockTree, producerChain.ReadOnlyStateProvider, _context._timestamper, _context.LogManager, new AuRaStepCalculator(_context._chainSpec.AuRa.StepDuration, _context._timestamper), _context._configProvider.GetConfig<IAuraConfig>(), _context._nodeKey.Address);
                    validator.SetFinalizationManager(_context._finalizationManager, true);
                    break;
                }

                default:
                    throw new NotSupportedException($"Mining in {_context._chainSpec.SealEngineType} mode is not supported");
            }

            _context._blockProducer.Start();
            
            SubsystemStateChanged?.Invoke(this, new SubsystemStateEventArgs(EthereumSubsystemState.Running));
            
            return Task.CompletedTask;
        }

        public event EventHandler<SubsystemStateEventArgs> SubsystemStateChanged;

        public EthereumSubsystem MonitoredSubsystem => EthereumSubsystem.Mining;
    }
}