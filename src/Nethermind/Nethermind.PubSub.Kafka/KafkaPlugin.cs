//  Copyright (c) 2021 Demerzel Solutions Limited
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
using Nethermind.Logging;
using Nethermind.PubSub.Kafka.Avro;
using Nethermind.PubSub.Kafka.Models;

namespace Nethermind.PubSub.Kafka
{
    public class KafkaPlugin : INethermindPlugin
    {
        private IKafkaConfig _kafkaConfig;

        private KafkaPublisher _kafkaPublisher;

        private ILogger _logger;
        private INethermindApi _api;

        public ValueTask DisposeAsync()
        {
            _kafkaPublisher.Dispose();
            return ValueTask.CompletedTask;
        }

        public string Name => "Kafka";

        public string Description => "Kafka Publisher for Nethermind";

        public string Author => "Nethermind";

        public Task Init(INethermindApi api)
        {
            _api = api;
            var (getFromAPi, _) = _api.ForInit;
            
            _kafkaConfig = getFromAPi.Config<IKafkaConfig>();
            _logger = getFromAPi.LogManager.GetClassLogger();
            return Task.CompletedTask;
        }

        public Task InitBlockchain()
        {
            return Task.CompletedTask;
        }

        public Task InitBlockProducer()
        {
            return Task.CompletedTask;
        }

        public async Task InitNetworkProtocol()
        {
            var (getFromAPi, _) = _api.ForNetwork;
            if (_kafkaConfig.Enabled)
            {
                _kafkaPublisher = new KafkaPublisher(
                    _kafkaConfig,
                    new PubSubModelMapper(),
                    new AvroMapper(getFromAPi.BlockTree),
                    getFromAPi.LogManager);
                getFromAPi.Publishers.Add(_kafkaPublisher);

                IPublisher kafkaPublisher = await PrepareKafkaProducer();
                getFromAPi.Publishers.Add(kafkaPublisher);
                getFromAPi.DisposeStack.Push(kafkaPublisher);
            }
        }

        private async Task<IPublisher> PrepareKafkaProducer()
        {
            var (getFromAPi, _) = _api.ForNetwork;
            
            PubSubModelMapper pubSubModelMapper = new PubSubModelMapper();
            AvroMapper avroMapper = new AvroMapper(getFromAPi.BlockTree);
            KafkaPublisher kafkaPublisher = new KafkaPublisher(
                _kafkaConfig, pubSubModelMapper, avroMapper, getFromAPi.LogManager);
            
            await kafkaPublisher.InitAsync().ContinueWith(x =>
            {
                if (x.IsFaulted && _logger.IsError) _logger.Error("Error during Kafka initialization", x.Exception);
            });

            return kafkaPublisher;
        }

        public Task InitRpcModules()
        {
            return Task.CompletedTask;
        }
    }
}
