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
// 

using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Logging;
using Nethermind.PubSub.Kafka.Avro;
using Nethermind.PubSub.Kafka.Models;

namespace Nethermind.PubSub.Kafka
{
    public class KafkaPlugin : INethermindPlugin
    {
        private IBasicApi _basicApi;

        private IBlockTree _blockTree;

        private IKafkaConfig _kafkaConfig;

        private KafkaPublisher _kafkaPublisher;

        private ILogger _logger;

        public void Dispose()
        {
            _kafkaPublisher.Dispose();
        }

        public string Name => "Kafka";

        public string Description => "Kafka Publisher for Nethermind";

        public string Author => "Nethermind";

        public Task Init(IBasicApi api)
        {
            _kafkaConfig = api.Config<IKafkaConfig>();
            _basicApi = api;
            _logger = api.LogManager.GetClassLogger();
            return Task.CompletedTask;
        }

        public Task InitBlockchain(IBlockchainApi api)
        {
            _blockTree = api.BlockTree;
            return Task.CompletedTask;
        }

        public async Task InitNetworkProtocol(INetworkApi api)
        {
            if (_kafkaConfig.Enabled)
            {
                _kafkaPublisher = new KafkaPublisher(
                    _kafkaConfig,
                    new PubSubModelMapper(),
                    new AvroMapper(_blockTree),
                    _basicApi.LogManager);
                api.Publishers.Add(_kafkaPublisher);

                IPublisher kafkaPublisher = await PrepareKafkaProducer();
                api.Publishers.Add(kafkaPublisher);
                _basicApi.DisposeStack.Push(kafkaPublisher);
            }
        }

        private async Task<IPublisher> PrepareKafkaProducer()
        {
            PubSubModelMapper pubSubModelMapper = new PubSubModelMapper();
            AvroMapper avroMapper = new AvroMapper(_blockTree);
            KafkaPublisher kafkaPublisher = new KafkaPublisher(
                _kafkaConfig, pubSubModelMapper, avroMapper, _basicApi.LogManager);
            
            await kafkaPublisher.InitAsync().ContinueWith(x =>
            {
                if (x.IsFaulted && _logger.IsError) _logger.Error("Error during Kafka initialization", x.Exception);
            });

            return kafkaPublisher;
        }

        public Task InitRpcModules(INethermindApi api)
        {
            return Task.CompletedTask;
        }
    }
}