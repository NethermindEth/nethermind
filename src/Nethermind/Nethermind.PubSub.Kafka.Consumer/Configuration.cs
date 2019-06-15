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
            AutoOffsetReset = AutoOffsetResetType.Earliest,
            SecurityProtocol = SecurityProtocolType.Sasl_Plaintext,
            SaslMechanism = SaslMechanismType.Plain,
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