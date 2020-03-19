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
using Confluent.Kafka;
using Confluent.Kafka.SyncOverAsync;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using Nethermind.Logging;
using Nethermind.PubSub.Kafka.Avro.Models;

namespace Nethermind.PubSub.Kafka.Avro
{
    public class AvroTypeProducer : IKafkaTypeProducer
    {
        private static readonly string Name = "Avro";
        private bool _initialized;
        private readonly string _schemaRegistryUrl;
        private readonly IAvroMapper _mapper;
        private readonly ILogger _logger;
        private IProducer<Null, Block> _producerBlocks;
        private IProducer<Null, FullTransaction> _producerTransactions;

        public AvroTypeProducer(ProducerConfig config, string schemaRegistryUrl, IAvroMapper mapper, ILogger logger)
        {
            _schemaRegistryUrl = schemaRegistryUrl;
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
                CachedSchemaRegistryClient schemaRegistry = new CachedSchemaRegistryClient(new[]
                {
                    new KeyValuePair<string, string>(SchemaRegistryConfig.PropertyNames.SchemaRegistryUrl, _schemaRegistryUrl)
                });

                var blockAvroSerializer = new AvroSerializer<Block>(schemaRegistry).AsSyncOverAsync();
                var txAvroSerializer = new AvroSerializer<FullTransaction>(schemaRegistry).AsSyncOverAsync();
                ProducerBuilder<Null, Block> blockProducerBuilder = new ProducerBuilder<Null, Block>(config);
                blockProducerBuilder.SetValueSerializer(blockAvroSerializer);
                blockProducerBuilder.SetErrorHandler((s, e) => _logger.Error(e.ToString()));
                ProducerBuilder<Null, FullTransaction> txProducerBuilder = new ProducerBuilder<Null, FullTransaction>(config);
                txProducerBuilder.SetValueSerializer(txAvroSerializer);
                txProducerBuilder.SetErrorHandler((s, e) => _logger.Error(e.ToString()));
                
                _producerBlocks = blockProducerBuilder.Build();
                _producerTransactions = txProducerBuilder.Build();
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

            topic = $"{topic}.{Name}";
            var block = data as Core.Block;
            if (block != null)
            {
                await ProduceAvroBlockAsync(topic, block);

                return;
            }

            var transaction = data as PubSub.Models.FullTransaction;
            if (transaction != null)
            {
                await ProduceAvroTransactionAsync(topic, transaction);
            }
        }

        private async Task ProduceAvroBlockAsync(string topic, Core.Block block)
        {
            var message = new Message<Null, Block> {Value = _mapper.MapBlock(block)};
            var deliveryReport = await _producerBlocks.ProduceAsync(topic, message);
            if (_logger.IsDebug)
            {
                _logger.Debug($"Delivered '{deliveryReport.Value}' to '{deliveryReport.TopicPartitionOffset}'");
            }
        }

        private async Task ProduceAvroTransactionAsync(string topic, PubSub.Models.FullTransaction fullTransaction)
        {

            var message = new Message<Null, FullTransaction> {Value = _mapper.MapFullTransaction(fullTransaction)};
            var deliveryReport = await _producerTransactions.ProduceAsync(topic, message);
            if (_logger.IsDebug)
            {
                _logger.Debug($"Delivered '{deliveryReport.Value}' to '{deliveryReport.TopicPartitionOffset}'");
            }
        }

        public void Dispose()
        {
            if (!_initialized)
            {
                return;
            }

            _producerBlocks?.Dispose();
            _producerTransactions?.Dispose();
        }
    }
}