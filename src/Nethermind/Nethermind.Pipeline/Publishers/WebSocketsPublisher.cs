using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.WebSockets;

namespace Nethermind.Pipeline.Publishers
{
    public class WebSocketsPublisher : IPublisher, IWebSocketsModule
    {
        private readonly ConcurrentDictionary<string, IWebSocketsClient> _clients = new();
        private readonly IJsonSerializer _jsonSerializer;
        public string Name { get; }
        private readonly ILogger _logger;

        public WebSocketsPublisher(string name, IJsonSerializer jsonSerializer, ILogger logger)
        {
            Name = name;
            _jsonSerializer = jsonSerializer;
            _logger = logger;
        }

        public IWebSocketsClient CreateClient(WebSocket webSocket, string client)
        {
            var newClient = new WebSocketsClient(webSocket, client, _jsonSerializer);
            _clients.TryAdd(newClient.Id, newClient);

            if(_logger.IsInfo) _logger.Info($"Creating new WS client for {client}");

            return newClient;
        }

        public void RemoveClient(string clientId)
        {
            _clients.TryRemove(clientId, out var webSocketsClient);
        }

        public async Task SendAsync(WebSocketsMessage message)
        {
            await Task.WhenAll(_clients.Values.Select(v => v.SendAsync(message)));
        }

        public async Task SendRawAsync(string rawMessage)
        {
            await Task.WhenAll(_clients.Values.Select(v => v.SendRawAsync(rawMessage)));
        }

        public bool TryInit(HttpRequest request)
        {
            return true; 
        }
        
        public async void SubscribeToData<T>(T data)
        {
            try
            {
                var message = _jsonSerializer.Serialize(data);
                await Task.WhenAll(_clients.Values.Select(v => v.SendRawAsync(message)));
            }
            catch (Exception ex)
            {
                if(_logger.IsInfo) _logger.Info($"Exception during sending data with websockets, inner exception: {ex.InnerException}");
            };
        }
    }
}
