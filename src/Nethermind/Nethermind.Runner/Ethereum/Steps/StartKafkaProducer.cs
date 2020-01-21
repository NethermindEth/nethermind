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
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.PubSub;
using Nethermind.PubSub.Kafka;
using Nethermind.PubSub.Kafka.Avro;
using Nethermind.Runner.Ethereum.Subsystems;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependency(typeof(InitializeNetwork))]
    public class StartKafkaProducer : IStep, ISubsystemStateAware
    {
        private readonly EthereumRunnerContext _context;

        public StartKafkaProducer(EthereumRunnerContext context)
        {
            _context = context;
            EthereumSubsystemState newState = _context.Config<IKafkaConfig>().Enabled
                ? EthereumSubsystemState.AwaitingInitialization
                : EthereumSubsystemState.Disabled;

            SubsystemStateChanged?.Invoke(this, new SubsystemStateEventArgs(newState));
        }

        public async Task Execute()
        {
            if (!_context.Config<IKafkaConfig>().Enabled)
            {
                return;
            }

            IProducer kafkaProducer = await PrepareKafkaProducer(_context.BlockTree, _context.Config<IKafkaConfig>());
            _context.Producers.Add(kafkaProducer);
        }

        private async Task<IProducer> PrepareKafkaProducer(IBlockTree blockTree, IKafkaConfig kafkaConfig)
        {
            PubSubModelMapper pubSubModelMapper = new PubSubModelMapper();
            AvroMapper avroMapper = new AvroMapper(blockTree);
            KafkaProducer kafkaProducer = new KafkaProducer(kafkaConfig, pubSubModelMapper, avroMapper, _context.LogManager);
            await kafkaProducer.InitAsync().ContinueWith(x =>
            {
                if (x.IsFaulted && _context.Logger.IsError) _context.Logger.Error("Error during Kafka initialization", x.Exception);
            });

            return kafkaProducer;
        }
        
        public event EventHandler<SubsystemStateEventArgs> SubsystemStateChanged;

        public EthereumSubsystem MonitoredSubsystem => EthereumSubsystem.Kafka;
    }
}