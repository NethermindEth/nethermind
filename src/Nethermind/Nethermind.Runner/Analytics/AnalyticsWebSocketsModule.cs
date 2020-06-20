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
// 

using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.PubSub;
using Nethermind.Serialization.Json;
using Nethermind.WebSockets;

namespace Nethermind.Runner.Analytics
{
    public class AnalyticsWebSocketsModule : IWebSocketsModule, IProducer
    {
        private readonly ConcurrentDictionary<string, IWebSocketsClient> _clients =
            new ConcurrentDictionary<string, IWebSocketsClient>();
        
        private readonly IJsonSerializer _jsonSerializer;

        public string Name { get; } = "analytics";

        public AnalyticsWebSocketsModule(IJsonSerializer jsonSerializer)
        {
            _jsonSerializer = jsonSerializer;
        }

        public IWebSocketsClient CreateClient(WebSocket webSocket, string client)
        {
            var socketsClient = new WebSocketsClient(webSocket, client, _jsonSerializer);
            _clients.TryAdd(socketsClient.Id, socketsClient);

            return socketsClient;
        }

        public bool TryInit(HttpRequest request)
        {
            return true;
        }

        public async Task SendRawAsync(string data)
        {
            await Task.WhenAll(_clients.Values.Select(v => v.SendRawAsync(data)));
        } 
        
        public async Task SendAsync(WebSocketsMessage message)
        {
            await Task.WhenAll(_clients.Values.Select(v => v.SendAsync(message)));
        } 
        
        public void RemoveClient(string id) => _clients.TryRemove(id, out _);
        
        public async Task PublishAsync<T>(T data) where T : class
        {
            await SendAsync(new WebSocketsMessage("analytics", null, data));
        }
    }
}