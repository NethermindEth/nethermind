//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Infrastructure;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;

namespace Nethermind.DataMarketplace.WebSockets
{
    public class NdmWebSocketsModule : IWebSocketsModule
    {
        private readonly ConcurrentDictionary<string, ISocketsClient> _clients =
            new ConcurrentDictionary<string, ISocketsClient>();

        private readonly INdmConsumerChannelManager _consumerChannelManager;
        private readonly INdmDataPublisher _dataPublisher;
        private readonly IJsonSerializer _jsonSerializer;
        private NdmWebSocketsConsumerChannel? _channel;
        private readonly ILogManager _logManager;

        public string Name { get; } = "ndm";

        public NdmWebSocketsModule(INdmApi api)
        {
            _consumerChannelManager = api.NdmConsumerChannelManager;
            _dataPublisher = api.NdmDataPublisher;
            _jsonSerializer = api.EthereumJsonSerializer;
            _logManager = api.LogManager;
        }

        public bool TryInit(HttpRequest request)
        {
            return true;
        }

        public ISocketsClient CreateClient(WebSocket webSocket, string clientName)
        {
            NdmWebSocketsClient socketsClient = new NdmWebSocketsClient(clientName, new WebSocketHandler(webSocket, _logManager), _dataPublisher, _jsonSerializer);
            _channel = new NdmWebSocketsConsumerChannel(socketsClient);
            _consumerChannelManager.Add(_channel);
            _clients.TryAdd(socketsClient.Id, socketsClient);

            return socketsClient;
        }

        public void RemoveClient(string id) => _clients.TryRemove(id, out _);

        public Task SendAsync(SocketsMessage message)
            => Task.WhenAll(_clients.Values.Select(c => c.SendAsync(message)));
    }
}
