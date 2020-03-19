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
using Avro.Specific;
using Confluent.Kafka;
using Confluent.Kafka.SyncOverAsync;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using Nethermind.PubSub.Kafka.Consumer.Avro.Models;
using JsonSerializer = Utf8Json.JsonSerializer;
using Block = Nethermind.PubSub.Models.Block;
using Transaction = Nethermind.PubSub.Models.Transaction;
using TransactionReceipt = Nethermind.PubSub.Models.TransactionReceipt;

namespace Nethermind.PubSub.Kafka.Consumer
{
    class Program
    {
        private static readonly Configuration Configuration = Configuration.Avro;

        static void Main(string[] args)
        {
            switch (Configuration.Type)
            {
                case Configuration.ConfigurationType.Avro:
                    StartConsumingAvroBlocks();
                    break;
                case Configuration.ConfigurationType.Json:
                    StartConsuming<string>();
                    break;
                case Configuration.ConfigurationType.Utf8Json:
                    StartConsuming<byte[]>();
                    break;
            }
        }

        private static void StartConsumingAvroBlocks()
        {
            StartConsumingAvro<Avro.Models.Block>(Configuration.TopicBlocks);
        }

        private static void StartConsumingAvroTransactions()
        {
            StartConsumingAvro<Avro.Models.FullTransaction>(Configuration.TopicTransactions);
        }

        private static void StartConsumingAvro<T>(string topic) where T : ISpecificRecord
        {
            bool consuming = true;
            ConsumerBuilder<Ignore, T> consumerBuilder = new ConsumerBuilder<Ignore, T>(Configuration.ConsumerConfig);
            consumerBuilder.SetErrorHandler((s, e) =>
            {
                consuming = !e.IsFatal;
                Log(e.ToString());
            });

            CachedSchemaRegistryClient schemaRegistry = new CachedSchemaRegistryClient(new[]
            {
                new KeyValuePair<string, string>(SchemaRegistryConfig.PropertyNames.SchemaRegistryUrl, Configuration.SchemaRegistryUrl)
            });
            
            var deserializer = new AvroDeserializer<T>(schemaRegistry).AsSyncOverAsync();
            consumerBuilder.SetValueDeserializer(deserializer);
            IConsumer<Ignore, T> consumer = consumerBuilder.Build();
            using (consumer)
            {
                ConsumerConfig consumerConfig = Configuration.ConsumerConfig;
                Log($"Consumer for group: '{consumerConfig.GroupId}' was created. Data type: '{Configuration.Type}'.");
                consumer.Subscribe(new[] {topic});
                Log($"Subscribed to topic: '{topic}'.");
                while (consuming)
                {
                    try
                    {
                        ConsumeResult<Ignore, T> consumeResult = consumer.Consume();
                        Type type = typeof(T);
                        if (type == typeof(Avro.Models.Block))
                        {
                            ConsumeResult<Ignore, Avro.Models.Block> result = consumeResult as ConsumeResult<Ignore, Avro.Models.Block>;
                            Avro.Models.Block block = result.Value;
                            Log($"Block: {block.blockNumber} {block.blockHash}");
                        }
                        else if (type == typeof(Avro.Models.FullTransaction))
                        {
                            ConsumeResult<Ignore, FullTransaction> result = consumeResult as ConsumeResult<Ignore, Avro.Models.FullTransaction>;
                            FullTransaction transaction = result.Value;
                            Log($"Transaction for block: {transaction.blockNumber} {transaction.receipt.blockHash}");
                        }
                        else
                        {
                            Log($"Unknown data type: {type.Name}");

                            continue;
                        }

                        Log($"Consumed value at '{consumeResult.TopicPartitionOffset}'.");
                    }
                    catch (ConsumeException exception)
                    {
                        Log($"Consumer error occured: {exception.Error.Reason}");
                    }
                    catch (Exception exception)
                    {
                        Log(exception.Message);
                    }
                }

                consumer.Close();
            }
        }

        private static void StartConsuming<T>()
        {
            bool consuming = true;
            ConsumerBuilder<Ignore, T> consumerBuilder = new ConsumerBuilder<Ignore, T>(Configuration.ConsumerConfig);
            consumerBuilder.SetErrorHandler((s, e) =>
            {
                consuming = !e.IsFatal;
                Log(e.ToString());
            });

            using (IConsumer<Ignore, T> consumer = consumerBuilder.Build())
            {
                ConsumerConfig consumerConfig = Configuration.ConsumerConfig;
                Log($"Consumer for group: '{consumerConfig.GroupId}' was created. Data type: '{Configuration.Type}'.");
                consumer.Subscribe(new[] {Configuration.TopicBlocks, Configuration.TopicTransactions, Configuration.TopicReceipts});
                Log($"Subscribed to topics: '{Configuration.TopicBlocks}', {Configuration.TopicTransactions}, '{Configuration.TopicReceipts}'.");
                while (consuming)
                {
                    try
                    {
                        ConsumeResult<Ignore, T> consumeResult = consumer.Consume();
                        Type type = typeof(T);
                        if (type == typeof(string))
                        {
                            ConsumeAsJson(consumeResult as ConsumeResult<Ignore, string>);
                        }
                        else if (type == typeof(byte[]))
                        {
                            ConsumeAsUtf8Json(consumeResult as ConsumeResult<Ignore, byte[]>);
                        }
                        else
                        {
                            Log($"Unknown data type: {type.Name}");

                            continue;
                        }

                        Log($"Consumed value at '{consumeResult.TopicPartitionOffset}'.");
                    }
                    catch (ConsumeException exception)
                    {
                        Log($"Consumer error occured: {exception.Error.Reason}");
                    }
                    catch (Exception exception)
                    {
                        Log(exception.Message);
                    }
                }

                consumer.Close();
            }
        }

        private static void ConsumeAsJson(ConsumeResult<Ignore, string> consumeResult)
            => Consume(consumeResult, JsonSerializer.Deserialize<Block>,
                JsonSerializer.Deserialize<Transaction>,
                JsonSerializer.Deserialize<TransactionReceipt>);

        private static void ConsumeAsUtf8Json(ConsumeResult<Ignore, byte[]> consumeResult)
            => Consume(consumeResult, JsonSerializer.Deserialize<Block>,
                JsonSerializer.Deserialize<Transaction>,
                JsonSerializer.Deserialize<TransactionReceipt>);

        private static void Consume<T>(ConsumeResult<Ignore, T> consumeResult,
            Func<T, Block> onBlock, Func<T, Transaction> onTransaction,
            Func<T, TransactionReceipt> onReceipt)
        {
            T value = consumeResult.Value;
            string topic = consumeResult.Topic;
            if (topic == Configuration.TopicBlocks)
            {
                Block block = onBlock(value);
                if (block is null)
                {
                    return;
                }

                Log($"Consumed block: {block.Number}");
            }
            else if (topic == Configuration.TopicTransactions)
            {
                Transaction transaction = onTransaction(value);
                if (transaction is null)
                {
                    return;
                }

                Log($"Consumed transaction: {transaction.Hash}");
            }
            else if (topic == Configuration.TopicReceipts)
            {
                TransactionReceipt receipt = onReceipt(value);
                if (receipt is null)
                {
                    return;
                }

                Log($"Consumed receipt for block: {receipt.BlockNumber}");
            }
            else
            {
                Log($"Unknown topic: {topic}");
            }
        }

        private static void Log(string message) => Console.WriteLine(message);
    }
}