/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Core;
using Nethermind.WebSockets;

namespace Nethermind.DataMarketplace.WebSockets
{
    public class NdmWebSocketsModule : IWebSocketsModule
    {
        private readonly ConcurrentDictionary<string, IWebSocketsClient> _clients =
            new ConcurrentDictionary<string, IWebSocketsClient>();

        private readonly INdmConsumerChannelManager _consumerChannelManager;
        private readonly INdmDataPublisher _dataPublisher;
        private readonly IJsonSerializer _jsonSerializer;
        private NdmWebSocketsConsumerChannel _channel;

        public string Name { get; } = "ndm";

        public NdmWebSocketsModule(INdmConsumerChannelManager consumerChannelManager, INdmDataPublisher dataPublisher,
            IJsonSerializer jsonSerializer)
        {
            _consumerChannelManager = consumerChannelManager;
            _dataPublisher = dataPublisher;
            _jsonSerializer = jsonSerializer;
        }

        public bool TryInit(HttpRequest request)
        {
            return true;
        }

        public IWebSocketsClient CreateClient(WebSocket webSocket, string client)
        {
            var socketsClient = new NdmWebSocketsClient(new WebSocketsClient(webSocket, client, _jsonSerializer),
                _dataPublisher);
            _channel = new NdmWebSocketsConsumerChannel(socketsClient);
            _consumerChannelManager.Add(_channel);
            _clients.TryAdd(socketsClient.Id, socketsClient);

            return socketsClient;
        }

        public Task SendRawAsync(string data)
            => Task.WhenAll(_clients.Values.Select(c => c.SendRawAsync(data)));

        public Task SendAsync(WebSocketsMessage message)
            => Task.WhenAll(_clients.Values.Select(c => c.SendAsync(message)));

        public void Cleanup(string clientId) => _clients.TryRemove(clientId, out _);
        
        public void RemoveClient(string id) => _clients.TryRemove(id, out _);
    }
}