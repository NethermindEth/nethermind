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
                        Exception? innerException = t.Exception;
                        while (innerException?.InnerException != null)
                        {
                            innerException = innerException.InnerException;
                        }

                        if (innerException is SocketException socketException)
                        {
                            if (socketException.SocketErrorCode == SocketError.ConnectionReset)
                            {
                                if (_logger.IsTrace) _logger.Trace($"Client disconnected: {innerException.Message}.");
                            }
                            else
                            {
                                if (_logger.IsInfo) _logger.Info($"Not able to read from WebSockets ({socketException.SocketErrorCode}: {socketException.ErrorCode}). {innerException.Message}");
                            }
                        }
                        else if(innerException is WebSocketException webSocketException)
                        {
                            if (webSocketException.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
                            {
                                if (_logger.IsTrace) _logger.Trace($"Client disconnected: {innerException.Message}.");
                            }
                            else
                            {
                                if (_logger.IsInfo) _logger.Info($"Not able to read from WebSockets ({webSocketException.WebSocketErrorCode}: {webSocketException.ErrorCode}). {innerException.Message}");
                            }
                        } 
                        else
                        {
                            if (_logger.IsInfo) _logger.Info($"Not able to read from WebSockets. {innerException?.Message}");
                        }

                        result = new WebSocketsReceiveResult() { Closed = true };
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

        public Task CloseAsync(ReceiveResult? result)
        {
            if (_webSocket.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                return _webSocket.CloseAsync(result is WebSocketsReceiveResult { CloseStatus: { } } r ? r.CloseStatus.Value : WebSocketCloseStatus.Empty,
                    result?.CloseStatusDescription,
                    CancellationToken.None);
            }

            if (_webSocket.State is WebSocketState.CloseReceived)
            {
                return _webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, result?.CloseStatusDescription,
                    CancellationToken.None);
            }
            
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _webSocket?.Dispose();
        }
    }
}
