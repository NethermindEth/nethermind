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
using Avro.Specific;
using Confluent.Kafka;
using Confluent.Kafka.Serialization;
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
            var avroSerdeProvider = new AvroSerdeProvider(new AvroSerdeProviderConfig
                {SchemaRegistryUrl = Configuration.SchemaRegistryUrl});

            using (var consumer = new Consumer<Ignore, T>(Configuration.ConsumerConfig, null,
                avroSerdeProvider.GetDeserializerGenerator<T>()))
            {
                var consumerConfig = Configuration.ConsumerConfig;
                Log($"Consumer for group: '{consumerConfig.GroupId}' was created. Data type: '{Configuration.Type}'.");
                var consuming = true;
                consumer.OnError += (s, e) =>
                {
                    consuming = !e.IsFatal;
                    Log(e.ToString());
                };
                consumer.Subscribe(new[] {topic});
                Log($"Subscribed to topic: '{topic}'.");
                while (consuming)
                {
                    try
                    {
                        var consumeResult = consumer.Consume();
                        var type = typeof(T);
                        if (type == typeof(Avro.Models.Block))
                        {
                            var result = consumeResult as ConsumeResult<Ignore, Avro.Models.Block>;
                            var block = result.Value;
                            Log($"Block: {block.blockNumber} {block.blockHash}");
                        }
                        else if (type == typeof(Avro.Models.FullTransaction))
                        {
                            var result = consumeResult as ConsumeResult<Ignore, Avro.Models.FullTransaction>;
                            var transaction = result.Value;
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
            using (var consumer = new Consumer<Ignore, T>(Configuration.ConsumerConfig))
            {
                var consumerConfig = Configuration.ConsumerConfig;
                Log($"Consumer for group: '{consumerConfig.GroupId}' was created. Data type: '{Configuration.Type}'.");
                var consuming = true;
                consumer.OnError += (s, e) =>
                {
                    consuming = !e.IsFatal;
                    Log(e.ToString());
                };
                consumer.Subscribe(new[] {Configuration.TopicBlocks, Configuration.TopicTransactions, Configuration.TopicReceipts});
                Log($"Subscribed to topics: '{Configuration.TopicBlocks}', {Configuration.TopicTransactions}, '{Configuration.TopicReceipts}'.");
                while (consuming)
                {
                    try
                    {
                        var consumeResult = consumer.Consume();
                        var type = typeof(T);
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
            var value = consumeResult.Value;
            var topic = consumeResult.Topic;
            if (topic == Configuration.TopicBlocks)
            {
                var block = onBlock(value);
                if (block is null)
                {
                    return;
                }

                Log($"Consumed block: {block.Number}");
            }
            else if (topic == Configuration.TopicTransactions)
            {
                var transaction = onTransaction(value);
                if (transaction is null)
                {
                    return;
                }

                Log($"Consumed transaction: {transaction.Hash}");
            }
            else if (topic == Configuration.TopicReceipts)
            {
                var receipt = onReceipt(value);
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