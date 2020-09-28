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
using Nethermind.PubSub.Kafka.Avro;
using Nethermind.PubSub.Kafka.Models;

namespace Nethermind.PubSub.Kafka
{
    public class KafkaPlugin : INethermindPlugin
    {
        private KafkaPublisher _kafkaPublisher;

        public void Dispose()
        {
            _kafkaPublisher.Dispose();
        }

        public string Name => "Kafka";
        
        public string Description => "Kafka Publisher for Nethermind";
        
        public string Author => "Nethermind";
        
        public Task Init(INethermindApi api)
        {
            IKafkaConfig kafkaConfig = api.Config<IKafkaConfig>();
            _kafkaPublisher = new KafkaPublisher(
                kafkaConfig,
                new PubSubModelMapper(),
                new AvroMapper(api.BlockTree),
                api.LogManager);
            api.Publishers.Add(_kafkaPublisher);
            
            return Task.CompletedTask;
        }

        public Task InitNetworkProtocol()
        {
            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            return Task.CompletedTask;
        }
    }
}