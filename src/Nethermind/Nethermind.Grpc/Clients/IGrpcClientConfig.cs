using Nethermind.Config;

namespace Nethermind.Grpc.Clients
{
    public interface IGrpcClientConfig : IConfig
    {
        [ConfigItem(Description = "If 'true' then it enables gRPC client capabilities (Nethermind Runner instance will connect to the provided host and will act as an extension)", DefaultValue = "false")]
        bool Enabled { get; }
        [ConfigItem(Description = "An address of the host to connect to as gRPC client (e.g. NDM extension)", DefaultValue = "localhost")]
        string Host { get; }
        [ConfigItem(Description = "Port of the host to connect to", DefaultValue = "50000")]
        int Port { get; }
        [ConfigItem(Description = "Unique name of the gRPC client (used for internal purposes only)", DefaultValue = "nethermind")]
        string Name { get; }
        [ConfigItem(Description = "Display name of the gRPC client (extension)", DefaultValue = "Nethermind")]
        string DisplayName { get; }
        [ConfigItem(Description = "Type of the gRPC client (extension), possible values: stream, webapi", DefaultValue = "Nethermind")]
        string Type { get; }
        [ConfigItem(Description = "If 'true' then host will handle all of the data assets using this gRPC client (extension)", DefaultValue = "false")]
        bool AcceptAllHeaders { get; }
        [ConfigItem(Description = "Comma separated list of data assets ID that should be handled by this gRPC client (extension)", DefaultValue = "")]
        string AcceptedHeaders { get; }
    }
}