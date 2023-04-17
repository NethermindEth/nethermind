// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core.PubSub;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;

namespace Nethermind.Analytics
{
    public class AnalyticsWebSocketsModule : IWebSocketsModule, IPublisher
    {
        private readonly ConcurrentDictionary<string, ISocketsClient> _clients = new();

        private readonly IJsonSerializer _jsonSerializer;
        private readonly ILogManager _logManager;

        public string Name { get; } = "analytics";

        public AnalyticsWebSocketsModule(IJsonSerializer jsonSerializer, ILogManager logManager)
        {
            _jsonSerializer = jsonSerializer;
            _logManager = logManager;
        }

        public ISocketsClient CreateClient(WebSocket webSocket, string clientName, HttpContext httpContext)
        {
            SocketClient socketsClient = new SocketClient(clientName, new WebSocketHandler(webSocket, _logManager), _jsonSerializer);
            _clients.TryAdd(socketsClient.Id, socketsClient);

            return socketsClient;
        }

        public void RemoveClient(string id) => _clients.TryRemove(id, out _);

        public async Task PublishAsync<T>(T data) where T : class
        {
            await SendAsync(new SocketsMessage("analytics", null, data));
        }

        public async Task SendAsync(SocketsMessage message)
        {
            await Task.WhenAll(_clients.Values.Select(v => v.SendAsync(message)));
        }

        public void Dispose()
        {
        }
    }
}
