using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;

namespace Nethermind.Pipeline.Publishers
{
    public class WebSocketsPublisher<TIn, TOut> : IPipelineElement<TIn, TOut>, IWebSocketsModule
    {
        private readonly ConcurrentDictionary<string, ISocketsClient> _clients = new();
        private readonly IJsonSerializer _jsonSerializer;
        private readonly ILogManager _logManager;
        public string Name { private set; get; }
        public Action<TOut> Emit { private get; set; }

        public WebSocketsPublisher(string name, IJsonSerializer jsonSerializer, ILogManager logManager)
        {
            Name = name;
            _jsonSerializer = jsonSerializer;
            _logManager = logManager;
        }

        public ISocketsClient CreateClient(WebSocket webSocket, string clientName)
        {
            var newClient = new SocketClient(clientName, new WebSocketHandler(webSocket, _logManager), _jsonSerializer);
            _clients.TryAdd(clientName, newClient);

            return newClient;
        }

        public void RemoveClient(string clientId)
        {
            _clients.TryRemove(clientId, out var webSocketsClient);
        }

        public async Task SendAsync(SocketsMessage message)
        {
            await Task.WhenAll(_clients.Values.Select(v => v.SendAsync(message)));
        }
        public async void SubscribeToData(TIn data)
        {
            try
            {
                var message = new SocketsMessage(nameof(TIn), null, data);
                await Task.WhenAll(_clients.Values.Select(v => v.SendAsync(message)));
            }
            catch (Exception ex)
            {
            };
        }
    }
}
