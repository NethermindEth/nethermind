using Nethermind.Config;

namespace Nethermind.Grpc
{
    public interface IGrpcConfig : IConfig
    {
        [ConfigItem(Description = "If 'false' then it disables gRPC protocol", DefaultValue = "true")]
        bool Enabled { get; }
        [ConfigItem(Description = "An address of the host under which gRPC will be running", DefaultValue = "localhost")]
        string Host { get; }
        [ConfigItem(Description = "Port of the host under which gRPC will be exposed", DefaultValue = "50000")]
        int Port { get; }
        [ConfigItem(Description = "If 'true' then block and transaction data will be available to subscribe", DefaultValue = "false")]
        bool ProducerEnabled { get; }
    }
}