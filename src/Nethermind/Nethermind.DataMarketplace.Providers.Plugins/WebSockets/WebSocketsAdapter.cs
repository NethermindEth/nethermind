using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.DataMarketplace.Providers.Plugins.WebSockets
{
    // Since System.Net.WebSockets.ClientWebSocket is not mockable we nee some kind  
    // of 'proxy' to it for testing purposes.
    public class WebSocketsAdapter : IWebSocketsAdapter
    {
        private readonly ClientWebSocket _client;
        public WebSocketState State => _client.State;

        public WebSocketsAdapter()
        {
            _client  = new ClientWebSocket();
        }

        public async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            await _client.CloseAsync(closeStatus, statusDescription, cancellationToken);
        }

        public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            await _client.ConnectAsync(uri, cancellationToken);
        }

        public async Task<string> ReceiveAsync(CancellationToken cancellationToken)
        {
            ArraySegment<byte> buffer = new ArraySegment<byte>(new Byte[8192]);

            WebSocketReceiveResult result = null;
            string receivedMessage = null;

            using (var ms = new MemoryStream())
            {
                do
                {
                    result = await _client.ReceiveAsync(buffer, cancellationToken);
                    ms.Write(buffer.Array, buffer.Offset, result.Count);
                }
                while (!result.EndOfMessage);

                ms.Seek(0, SeekOrigin.Begin);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    using (var reader = new StreamReader(ms, Encoding.UTF8))
                    {
                        receivedMessage = await reader.ReadToEndAsync();
                    }
                }
            }

            return receivedMessage;
        }
    }
}