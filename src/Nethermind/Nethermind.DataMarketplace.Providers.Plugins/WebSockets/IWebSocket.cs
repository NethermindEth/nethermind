using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.DataMarketplace.Providers.Plugins.WebSockets
{
    public interface IWebSocketsAdapter
    {
        WebSocketState State { get; }
        Task ConnectAsync(Uri uri, CancellationToken cancellationToken);
        Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken);
        Task<string> ReceiveAsync(CancellationToken cancellationToken);
    }
}