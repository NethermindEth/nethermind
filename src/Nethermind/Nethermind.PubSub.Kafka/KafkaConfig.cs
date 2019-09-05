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

namespace Nethermind.PubSub.Kafka
{
    public class KafkaConfig : IKafkaConfig
    {
        public bool Enabled { get; set; }
        public string Servers { get; set; } = "localhost:19092,localhost:29092,localhost:39092";
        public string SecurityProtocol { get; set; } = "SASL_Plaintext";
        public bool SaslEnabled { get; set; } = true;
        public string SaslUsername { get; set; } = "nethermind";
        public string SaslPassword { get; set; } = "secret";
        public string SslKeyLocation { get; set; } = "certs/nethermind.pem";
        public string SchemaRegistryUrl { get; set; } = "http://localhost:8081";
        public string TopicBlocks { get; set; } = "Nethermind.Blocks";
        public string TopicReceipts { get; set; } = "Nethermind.Receipts";
        public string TopicTransactions { get; set; } = "Nethermind.Transactions";
        public bool ProduceAvro { get; set; } = true;
        public bool ProduceJson { get; set; } = true;
        public bool ProduceUtf8Json { get; set; } = true;
    }
}