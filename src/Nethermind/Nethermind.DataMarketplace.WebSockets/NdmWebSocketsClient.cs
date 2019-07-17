using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.WebSockets;

namespace Nethermind.DataMarketplace.WebSockets
{
    public class NdmWebSocketsClient : IWebSocketsClient
    {
        private readonly IWebSocketsClient _client;
        private readonly INdmDataPublisher _dataPublisher;
        public string Id => _client.Id;

        public NdmWebSocketsClient(IWebSocketsClient client, INdmDataPublisher dataPublisher)
        {
            _client = client;
            _dataPublisher = dataPublisher;
        }

        public Task ReceiveAsync(byte[] data)
        {
            if (data is null || data.Length == 0)
            {
                return Task.CompletedTask;
            }

            var (dataHeaderId, headerData) = GetDataInfo(data);
            if (dataHeaderId is null || string.IsNullOrWhiteSpace(headerData))
            {
                return Task.CompletedTask;
            }

            var dataResult = new Dictionary<string, string>
            {
                [string.Empty] = headerData
            };
            _dataPublisher.Publish(new DataHeaderData(dataHeaderId, dataResult));

            return Task.CompletedTask;
        }

        private static (Keccak dataHeaderId, string data) GetDataInfo(byte[] bytes)
        {
            var request = Encoding.UTF8.GetString(bytes);
            var parts = request.Split('|');

            if (!parts.Any() || parts.Length != 3)
            {
                return (null, null);
            }

            var dataHeaderId = parts[0];
            var extension = parts[1];
            var data = parts[2];

            return string.IsNullOrWhiteSpace(dataHeaderId) ? (null, null) : (new Keccak(dataHeaderId), data);
        }

        public Task SendRawAsync(string data) => _client.SendRawAsync(data);
        public Task SendAsync(WebSocketsMessage message) => _client.SendAsync(message);
    }
}