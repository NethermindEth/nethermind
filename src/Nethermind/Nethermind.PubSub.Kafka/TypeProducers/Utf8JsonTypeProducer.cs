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
using System.Linq.Expressions;
using System.Threading.Tasks;
using Confluent.Kafka;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.PubSub.Models;
using Block = Nethermind.Core.Block;
using JsonSerializer = Utf8Json.JsonSerializer;

namespace Nethermind.PubSub.Kafka.TypeProducers
{
    public class Utf8JsonTypeProducer : IKafkaTypeProducer
    {
        private static readonly string Name = "Utf8Json";
        private bool _initialized;
        private readonly IPubSubModelMapper _mapper;
        private readonly ILogger _logger;
        private IProducer<Null, byte[]> _producer;

        public Utf8JsonTypeProducer(ProducerConfig config, IPubSubModelMapper mapper, ILogger logger)
        {
            _mapper = mapper;
            _logger = logger;
            Init(config);
        }

        private void Init(ProducerConfig config)
        {
            if (_logger.IsDebug)
            {
                _logger.Debug($"Initializing {Name} type producer for Kafka...");
            }
            try
            {
                var producerBuilder = new ProducerBuilder<Null, byte[]>(config);
                producerBuilder.SetErrorHandler((s, e) => _logger.Error(e.ToString()));
                _producer = producerBuilder.Build();
                _initialized = true;
                if (_logger.IsDebug)
                {
                    _logger.Debug($"Initialized {Name} type producer for Kafka.");
                }
            }
            catch (Exception e)
            {
                _logger.Error(e.Message, e);
            }
        }

        public async Task PublishAsync<T>(string topic, T data) where T : class
        {
            if (!_initialized)
            {
                throw new InvalidOperationException($"{Name} type producer for Kafka was not initialized.");
            }

            var value = Serialize(data);
            if (value == null || value.Length == 0)
            {
                return;
            }

            topic = $"{topic}.{Name}";
            var message = new Message<Null, byte[]> {Value = value};
            var deliveryReport = await _producer.ProduceAsync(topic, message);
            if (_logger.IsDebug)
            {
                _logger.Debug($"Delivered '{deliveryReport.Value}' to '{deliveryReport.TopicPartitionOffset}'");
            }
        }

        private byte[] Serialize(object data)
        {
            switch (data)
            {
                case Block block: return JsonSerializer.Serialize(_mapper.MapBlock(block));
                case FullTransaction transaction:
                    return JsonSerializer.Serialize(_mapper.MapTransaction(transaction.Transaction));
                case TxReceipt receipt:
                    return JsonSerializer.Serialize(_mapper.MapTransactionReceipt(receipt));
            }

            return null;
        }

        public void Dispose()
        {
            if (!_initialized)
            {
                return;
            }

            _producer?.Dispose();
        }
    }
}