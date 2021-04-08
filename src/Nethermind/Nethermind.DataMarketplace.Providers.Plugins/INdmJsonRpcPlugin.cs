using Nethermind.DataMarketplace.Providers.Plugins.JsonRpc;

namespace Nethermind.DataMarketplace.Providers.Plugins
{
    public interface INdmJsonRpcPlugin : INdmPlugin
    {
        string? Host { get; }
        IJsonRpcClient? JsonRpcClient { get; set; }
    }
}