using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.PubSub;
using Nethermind.Serialization.Json;
using Nethermind.WebSockets;

namespace Nethermind.Pipeline.Publishers
{
    public class WebSocketsPublisher<TIn, TOut> : IPipelineElement<TIn, TOut>, IPublisher, IWebSocketsModule
    {
        private readonly ConcurrentDictionary<string, IWebSocketsClient> _clients = new();
        private IJsonSerializer _jsonSerializer;
        public string Name { get; } = "pipeline";
        public Action<TOut> Emit { private get; set; }

        public WebSocketsPublisher(IJsonSerializer jsonSerializer)
        {
            _jsonSerializer = jsonSerializer;
        }

        public IWebSocketsClient CreateClient(WebSocket webSocket, string client)
        {
            var newClient = new WebSocketsClient(webSocket, client, _jsonSerializer);
            _clients.TryAdd(client, newClient);

            return newClient;
        }

        public async Task PublishAsync<T>(T data) where T : class
        {
            await SendAsync(new WebSocketsMessage(nameof(T), null, data));
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
        
        public void Dispose()
        {
        }

        public async void SubscribeToData(TIn data)
        {
            var message = new WebSocketsMessage(nameof(TIn), null, data);
            await Task.WhenAll(_clients.Values.Select(v => v.SendAsync(message)));
        }
    }
}
