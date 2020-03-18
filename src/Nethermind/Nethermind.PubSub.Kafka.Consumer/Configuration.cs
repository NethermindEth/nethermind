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

using Confluent.Kafka;

namespace Nethermind.PubSub.Kafka.Consumer
{
    public class Configuration
    {
        public ConfigurationType Type { get; }
        public string TopicBlocks => $"Nethermind.Blocks.{Type}";
        public string TopicReceipts => $"Nethermind.Receipts.{Type}";
        public string TopicTransactions => $"Nethermind.Transactions.{Type}";
        public string SchemaRegistryUrl = "http://localhost:8081";

        public ConsumerConfig ConsumerConfig => new ConsumerConfig
        {
            BootstrapServers = "localhost:19092,localhost:29092,localhost:39092",
            GroupId = "Nethermind",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            SecurityProtocol = SecurityProtocol.SaslPlaintext,
            SaslMechanism = SaslMechanism.Plain,
            SaslUsername = "nethermind",
            SaslPassword = "secret",
            SslKeyLocation = "certs/nethermind.pem",
        };

        private Configuration(ConfigurationType type)
        {
            Type = type;
        }

        public static Configuration Avro => new Configuration(ConfigurationType.Avro);
        public static Configuration Json => new Configuration(ConfigurationType.Json);
        public static Configuration Utf8Json => new Configuration(ConfigurationType.Utf8Json);

        public enum ConfigurationType
        {
            Avro,
            Json,
            Utf8Json
        }
    }
}