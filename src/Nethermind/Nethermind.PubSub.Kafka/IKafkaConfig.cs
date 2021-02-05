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

using Nethermind.Config;

namespace Nethermind.PubSub.Kafka
{
    [ConfigCategory(HiddenFromDocs = true)]
    public interface IKafkaConfig : IConfig
    {
        [ConfigItem(Description = "If 'true' then it enables the Kafka producer which can be configured to stream the transactions data.", DefaultValue = "false")]
        bool Enabled { get; set; }
        [ConfigItem(HiddenFromDocs = true, DefaultValue = "localhost:19092,localhost:29092,localhost:39092")]
        string Servers { get; }
        [ConfigItem(Description = "Security protocol used", DefaultValue = "SASL_Plaintext")]
        string SecurityProtocol { get; }
        [ConfigItem(Description = "SASL enabled", DefaultValue = "true")]
        bool SaslEnabled { get; }
        [ConfigItem(Description = "SASL username", DefaultValue = "nethermind")]
        string SaslUsername { get; }
        [ConfigItem(Description = "SASL password", DefaultValue = "secret")]
        string SaslPassword { get; }
        [ConfigItem(Description = "SSL key location", DefaultValue = "certs/nethermind.pem")]
        string SslKeyLocation { get; }
        [ConfigItem(Description = "Topic to publish block messages on", DefaultValue = "Nethermind.Blocks")]
        string TopicBlocks { get; }
        [ConfigItem(Description = "Topic to publish receipt messages on", DefaultValue = "Nethermind.Receipts")]
        string TopicReceipts { get; }
        [ConfigItem(Description = "Topic to publish transaction messages on", DefaultValue = "Nethermind.Transactions")]
        string TopicTransactions { get; }
        [ConfigItem(Description = "Schema registry URL", DefaultValue = "http://localhost:8081")]
        string SchemaRegistryUrl { get; }
        [ConfigItem(Description = "Whether to produce Avro formatted output", DefaultValue = "true")]
        bool ProduceAvro { get; }
        [ConfigItem(Description = "Whether to produce JSON formatted output", DefaultValue = "true")]
        bool ProduceJson { get; }
        [ConfigItem(Description = "Whether to produce UTF8 formatted output", DefaultValue = "true")]
        bool ProduceUtf8Json { get; }
    }
}
