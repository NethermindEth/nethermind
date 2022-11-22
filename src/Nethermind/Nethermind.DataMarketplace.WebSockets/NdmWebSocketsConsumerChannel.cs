// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Channels;
using Nethermind.Sockets;

namespace Nethermind.DataMarketplace.WebSockets
{
    public class NdmWebSocketsConsumerChannel : INdmConsumerChannel
    {
        private readonly ISocketsClient _client;
        public NdmConsumerChannelType Type => NdmConsumerChannelType.WebSockets;

        public NdmWebSocketsConsumerChannel(ISocketsClient client)
        {
            _client = client;
        }

        public Task PublishAsync(Keccak depositId, string client, string data)
            => _client.SendAsync(new SocketsMessage("data_received", client, new
            {
                depositId,
                data
            }));
    }
}
