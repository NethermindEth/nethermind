namespace Nethermind.DataMarketplace.Providers.Plugins.WebSockets
{
    public interface INdmWebSocketsPlugin : INdmPlugin
    {
        string? Scheme { get; }
        string? Host { get; }
        int? Port { get; }
        string? Resource { get; }
        IWebSocketsAdapter? WebSocketsAdapter { get; set; }
    }
}