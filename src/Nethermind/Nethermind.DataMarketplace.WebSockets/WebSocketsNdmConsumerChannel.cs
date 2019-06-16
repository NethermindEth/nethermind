using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Channels;
using Nethermind.WebSockets;

namespace Nethermind.DataMarketplace.WebSockets
{
    public class WebSocketsNdmConsumerChannel : INdmConsumerChannel
    {
        private readonly IWebSocketsClient _client;
        private readonly Keccak _depositId;
        public NdmConsumerChannelType Type => NdmConsumerChannelType.WebSockets;

        public WebSocketsNdmConsumerChannel(IWebSocketsClient client, Keccak depositId)
        {
            _client = client;
            _depositId = depositId;
        }

        public Task PublishAsync(Keccak depositId, string data)
            => _depositId != depositId ? Task.CompletedTask : _client.SendAsync(data);
    }
}