using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.Sockets
{
    public class WebSocketHandler : ISocketHandler
    {
        private readonly WebSocket _webSocket;
        private readonly ILogger _logger;

        public WebSocketHandler(WebSocket webSocket, ILogManager logManager)
        {
            _webSocket = webSocket;
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public Task SendRawAsync(ArraySegment<byte> data) => 
            _webSocket.State != WebSocketState.Open 
                ? Task.CompletedTask 
                : _webSocket.SendAsync(data, WebSocketMessageType.Text, true, CancellationToken.None);

        public async Task<ReceiveResult?> GetReceiveResult(ArraySegment<byte> buffer)
        {
            ReceiveResult? result = null;
            if (_webSocket.State == WebSocketState.Open)
            {
                Task<WebSocketReceiveResult> resultTask = _webSocket.ReceiveAsync(buffer, CancellationToken.None);

                await resultTask.ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        result = null;

                        Exception? innerException = t.Exception;
                        while (innerException?.InnerException != null)
                        {
                            innerException = innerException.InnerException;
                        }

                        if (innerException is SocketException socketException && socketException.SocketErrorCode == SocketError.ConnectionReset)
                        {
                            _logger.Debug("Client disconnected.");
                        }
                        else
                        {
                            _logger.Error($"Error when reading from WebSockets.", t.Exception);
                        }
                    }

                    if (t.IsCompletedSuccessfully)
                    {
                        result = new WebSocketsReceiveResult()
                        {
                            Closed = t.Result.MessageType == WebSocketMessageType.Close,
                            Read = t.Result.Count,
                            EndOfMessage = t.Result.EndOfMessage,
                            CloseStatus = t.Result.CloseStatus,
                            CloseStatusDescription = t.Result.CloseStatusDescription
                        };
                    }
                });
            }

            return result;
        }

        public Task CloseAsync(ReceiveResult? result) =>
            _webSocket.CloseAsync(result is WebSocketsReceiveResult { CloseStatus: { } } r ? r.CloseStatus.Value : WebSocketCloseStatus.Empty, 
                result?.CloseStatusDescription, 
                CancellationToken.None);

        public void Dispose()
        {
            _webSocket?.Dispose();
        }
    }
}
