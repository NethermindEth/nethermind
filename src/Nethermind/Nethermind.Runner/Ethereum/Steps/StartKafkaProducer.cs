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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Logging;
using Nethermind.PubSub;
using Nethermind.PubSub.Kafka;
using Nethermind.PubSub.Kafka.Avro;
using Nethermind.Runner.Ethereum.Api;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(StartBlockProcessor))]
    public class StartKafkaProducer : IStep
    {
        private readonly NethermindApi _api;
        private ILogger _logger;

        public StartKafkaProducer(NethermindApi api)
        {
            _api = api;
            _logger = api.LogManager.GetClassLogger();
        }

        public async Task Execute(CancellationToken _)
        {
            if (_api.BlockTree == null)
            {
                throw new InvalidOperationException("Kafka producer initialization started before the block tree is ready.");
            }
            
            IKafkaConfig kafkaConfig = _api.Config<IKafkaConfig>();
            if (kafkaConfig.Enabled)
            {
                IProducer kafkaProducer = await PrepareKafkaProducer(_api.BlockTree, kafkaConfig);
                _api.Producers.Add(kafkaProducer);
            }
        }

        private async Task<IProducer> PrepareKafkaProducer(IBlockTree blockTree, IKafkaConfig kafkaConfig)
        {
            PubSubModelMapper pubSubModelMapper = new PubSubModelMapper();
            AvroMapper avroMapper = new AvroMapper(blockTree);
            KafkaProducer kafkaProducer = new KafkaProducer(kafkaConfig, pubSubModelMapper, avroMapper, _api.LogManager);
            await kafkaProducer.InitAsync().ContinueWith(x =>
            {
                if (x.IsFaulted && _logger.IsError) _logger.Error("Error during Kafka initialization", x.Exception);
            });

            return kafkaProducer;
        }
    }
}