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

using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;

namespace Nethermind.JsonRpc.WebSockets
{
    public class JsonRpcWebSocketsModule : IWebSocketsModule
    {
        private readonly ConcurrentDictionary<string, ISocketsClient> _clients = new();

        private readonly JsonRpcProcessor _jsonRpcProcessor;
        private readonly IJsonRpcService _jsonRpcService;
        private readonly IJsonRpcLocalStats _jsonRpcLocalStats;
        private readonly ILogManager _logManager;
        private readonly IJsonSerializer _jsonSerializer;

        public string Name { get; } = "json-rpc";

        public JsonRpcWebSocketsModule(
            JsonRpcProcessor jsonRpcProcessor,
            IJsonRpcService jsonRpcService,
            IJsonRpcLocalStats jsonRpcLocalStats,
            ILogManager logManager,
            IJsonSerializer jsonSerializer)
        {
            _jsonRpcProcessor = jsonRpcProcessor;
            _jsonRpcService = jsonRpcService;
            _jsonRpcLocalStats = jsonRpcLocalStats;
            _logManager = logManager;
            _jsonSerializer = jsonSerializer;
        }

        public ISocketsClient CreateClient(WebSocket webSocket, string clientName)
        {
            var socketsClient = new JsonRpcSocketsClient(
                clientName, 
                new WebSocketHandler(webSocket, _logManager), 
                RpcEndpoint.WebSocket, 
                _jsonRpcProcessor, 
                _jsonRpcService,  
                _jsonRpcLocalStats, 
                _jsonSerializer);

            _clients.TryAdd(socketsClient.Id, socketsClient);

            return socketsClient;
        }

        public void RemoveClient(string id)
        {
            if (_clients.TryRemove(id, out var client) 
                && client is IDisposable disposableClient)
            {
                disposableClient.Dispose();
            }
        }

        public Task SendAsync(SocketsMessage message) => Task.CompletedTask;
    }
}
