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
using System.Net.WebSockets;

namespace Nethermind.WebSockets
{
    public class WebSocketsManager : IWebSocketsManager
    {
        private readonly ConcurrentDictionary<string, IWebSocketsClient> _clients =
            new ConcurrentDictionary<string, IWebSocketsClient>();
        
        private readonly ConcurrentDictionary<string, IWebSocketsModule> _modules =
            new ConcurrentDictionary<string, IWebSocketsModule>();

        public void AddModule(IWebSocketsModule module)
        {
            _modules.TryAdd(module.Name, module);
        }

        public IWebSocketsModule GetModule(string name)
            => _modules.TryGetValue(name, out var module) ? module : null;

        public IWebSocketsClient AddClient(WebSocket webSocket)
        {
            var client = new WebSocketsClient(webSocket);
            _clients.TryAdd(client.Id, client);
            foreach (var (_, module) in _modules)
            {
                module.AddClient(client);
            }

            return client;
        }

        public void RemoveClient(string id)
        {
            _clients.TryRemove(id, out _);
        }
    }
}