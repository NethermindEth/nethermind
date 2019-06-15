/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Confluent.Kafka;
using Nethermind.Blockchain;
using Nethermind.Logging;
using Nethermind.PubSub.Kafka.Avro;
using Nethermind.PubSub.Kafka.TypeProducers;

namespace Nethermind.PubSub.Kafka
{
    public class KafkaProducer : IProducer
    {
        private static readonly ISet<IKafkaTypeProducer> Producers = new HashSet<IKafkaTypeProducer>();
        private readonly IDictionary<Type, string> _topics;
        private readonly IKafkaConfig _kafkaConfig;
        private readonly IPubSubModelMapper _modelMapper;
        private readonly IAvroMapper _avroMapper;
        private readonly ILogger _logger;
        private bool _initialized;

        public KafkaProducer(IKafkaConfig kafkaConfig, IPubSubModelMapper modelMapper,
            IAvroMapper avroMapper, ILogManager logManager)
        {
            _kafkaConfig = kafkaConfig;
            _modelMapper = modelMapper;
            _avroMapper = avroMapper;
            _logger = logManager.GetClassLogger();
            _topics = GetTopics();
        }

        public Task InitAsync()
        {
            if (_initialized)
            {
                _logger.Info("Kafka producer(s) already initialized.");

                return Task.CompletedTask;
            }

            InitProducers();
            _initialized = true;

            return Task.CompletedTask;
        }

        private void InitProducers()
        {
            var config = GetProducerConfig();
            _logger.Info("Initializing Kafka producer(s)...");

            if (_kafkaConfig.ProduceAvro)
            {
                Producers.Add(new AvroTypeProducer(config, _kafkaConfig.SchemaRegistryUrl, _avroMapper, _logger));
            }

            if (_kafkaConfig.ProduceJson)
            {
                Producers.Add(new JsonTypeProducer(config, _modelMapper, _logger));
            }

            if (_kafkaConfig.ProduceUtf8Json)
            {
                Producers.Add(new Utf8JsonTypeProducer(config, _modelMapper, _logger));
            }

            _logger.Info(Producers.Any() ? "Kafka producer(s) initialized." : "No Kafka producers specified.");
        }

        private ProducerConfig GetProducerConfig()
        {
            var config = new ProducerConfig
            {
                BootstrapServers = _kafkaConfig.Servers,
            };
            switch (_kafkaConfig.SecurityProtocol?.ToLowerInvariant())
            {
                case "plaintext":
                    config.SecurityProtocol = SecurityProtocolType.Plaintext;
                    break;
                case "sasl_plaintext":
                    config.SecurityProtocol = SecurityProtocolType.Sasl_Plaintext;
                    break;
                case "sasl_ssl":
                    config.SecurityProtocol = SecurityProtocolType.Sasl_Ssl;
                    config.SslKeyLocation = _kafkaConfig.SslKeyLocation;
                    break;
                case "ssl":
                    config.SecurityProtocol = SecurityProtocolType.Ssl;
                    config.SslKeyLocation = _kafkaConfig.SslKeyLocation;
                    break;
            }

            if (_kafkaConfig.SaslEnabled)
            {
                config.SaslMechanism = SaslMechanismType.Plain;
                config.SaslUsername = _kafkaConfig.SaslUsername;
                config.SaslPassword = _kafkaConfig.SaslPassword;
            }

            return config;
        }

        public async Task PublishAsync<T>(T data) where T : class
        {
            if (!CanPublish(data))
            {
                return;
            }

            try
            {
                await Task.WhenAll(Producers.Select(p => p.PublishAsync(_topics[typeof(T)], data)));
            }
            catch (KafkaException exception)
            {
                if (_logger.IsDebug)
                {
                    _logger.Debug($"Delivery failed: {exception.Error.Reason}");
                }
            }
            catch (Exception exception)
            {
                _logger.Error(exception.StackTrace, exception);
            }
        }

        private bool CanPublish<T>(T data) where T : class
        {
            if (!_initialized)
            {
                _logger.Info("Kafka producer was not initialized.");

                return false;
            }

            if (data is null)
            {
                return false;
            }

            var key = typeof(T);
            if (_topics.ContainsKey(key))
            {
                return true;
            }

            _logger.Info($"Topic was not found for type: {key.Name}");

            return false;
        }

        public Task CloseAsync()
        {
            if (!_initialized)
            {
                _logger.Info("Kafka producer was not initialized.");

                return Task.CompletedTask;
            }

            _logger.Info("Closing Kafka producer...");
            foreach (var producer in Producers)
            {
                producer.Dispose();
            }

            _initialized = false;
            _logger.Info("Kafka producer closed.");

            return Task.CompletedTask;
        }

        private IDictionary<Type, string> GetTopics()
            => new Dictionary<Type, string>
            {
                [typeof(Core.Block)] = _kafkaConfig.TopicBlocks,
                [typeof(Core.FullTransaction)] = _kafkaConfig.TopicTransactions,
                [typeof(Core.TxReceipt)] = _kafkaConfig.TopicReceipts,
            };
    }
}