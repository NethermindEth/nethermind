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

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Serialization.Json;
using Nethermind.WebSockets;

namespace Nethermind.JsonRpc.WebSockets
{
    public class JsonRpcWebSocketsModule : IWebSocketsModule
    {
        private readonly ConcurrentDictionary<string, IWebSocketsClient> _clients =
            new ConcurrentDictionary<string, IWebSocketsClient>();

        private readonly JsonRpcProcessor _jsonRpcProcessor;
        private readonly JsonRpcService _jsonRpcService;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IJsonRpcLocalStats _jsonRpcLocalStats;

        public string Name { get; } = "json-rpc";

        public JsonRpcWebSocketsModule(
            JsonRpcProcessor jsonRpcProcessor,
            JsonRpcService jsonRpcService,
            IJsonSerializer jsonSerializer,
            IJsonRpcLocalStats jsonRpcLocalStats)
        {
            _jsonRpcProcessor = jsonRpcProcessor;
            _jsonRpcService = jsonRpcService;
            _jsonSerializer = jsonSerializer;
            _jsonRpcLocalStats = jsonRpcLocalStats;
        }

        public IWebSocketsClient CreateClient(WebSocket webSocket, string client)
        {
            var socketsClient = new JsonRpcWebSocketsClient(
                new WebSocketsClient(webSocket, client, _jsonSerializer),
                _jsonRpcProcessor,
                _jsonRpcService,
                _jsonSerializer,
                _jsonRpcLocalStats);
            _clients.TryAdd(socketsClient.Id, socketsClient);

            return socketsClient;
        }

        public bool TryInit(HttpRequest request)
        {
            return true;
        }

        public Task SendRawAsync(string data) => Task.CompletedTask;

        public Task SendAsync(WebSocketsMessage message) => Task.CompletedTask;

        public void RemoveClient(string id) => _clients.TryRemove(id, out _);
    }
}
