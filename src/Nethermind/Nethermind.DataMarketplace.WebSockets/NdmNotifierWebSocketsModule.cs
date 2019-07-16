using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.WebSockets;

namespace Nethermind.DataMarketplace.WebSockets
{
    public class NdmNotifierWebSocketsModule : IWebSocketsModule, INdmNotifier
    {
        private IWebSocketsClient _client;
        private readonly IJsonSerializer _jsonSerializer;
        public string Name { get; } = "notifier";

        public NdmNotifierWebSocketsModule(IJsonSerializer jsonSerializer)
        {
            _jsonSerializer = jsonSerializer;
        }

        public bool TryInit(HttpRequest request)
        {
            return true;
        }

        public void AddClient(IWebSocketsClient client)
        {
            _client = client;
        }

        public Task ExecuteAsync(IWebSocketsClient client, byte[] data)
            => Task.CompletedTask;

        public void Cleanup(IWebSocketsClient client)
        {
        }

        public Task NotifyAsync(Notification notification)
            => _client is null ? Task.CompletedTask :
                notification is null ? Task.CompletedTask : _client.SendAsync(_jsonSerializer.Serialize(new
                {
                    type = notification.Type,
                    data = notification.Data
                }));
    }
}