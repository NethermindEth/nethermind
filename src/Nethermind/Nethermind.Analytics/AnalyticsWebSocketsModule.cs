//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

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

        public ISocketsClient CreateClient(WebSocket webSocket, string clientName)
        {
            var socketsClient = new SocketClient(clientName, new WebSocketHandler(webSocket, _logManager), _jsonSerializer);
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
