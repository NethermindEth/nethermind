namespace Nethermind.DataMarketplace.Providers.Plugins.Grpc
{
    public interface INdmGrpcPlugin : INdmPlugin
    {
        string? Host { get; }
        int Port { get; }
        int ReconnectionInterval { get; }
    }
}