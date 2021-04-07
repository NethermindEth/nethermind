using System;
using System.Threading.Tasks;
using Nethermind.Pipeline;
using Nethermind.WebSockets;

namespace MyPlugin
{
    public class WebSocketsPublisher<T> : IPipelinePublisher<T>
    {
        private readonly IWebSocketsClient _client;

        public WebSocketsPublisher(IWebSocketsClient client)
        {
            _client = client;
        }

        public Task SendAsync(WebSocketsMessage message)
        {
            return _client.SendAsync(message);
        }

        public Task SendRawAsync(string data)
        {
            return _client.SendRawAsync(data);
        }

        public void Publish()
        {

        }
    }
}