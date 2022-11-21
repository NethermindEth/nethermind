// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

        public ISocketsClient CreateClient(WebSocket webSocket, string clientName, int port)
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
