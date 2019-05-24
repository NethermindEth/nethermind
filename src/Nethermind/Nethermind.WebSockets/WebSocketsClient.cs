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

using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.WebSockets
{
    public class WebSocketsClient : IWebSocketsClient
    {
        private readonly WebSocket _webSocket;

        public WebSocketsClient(WebSocket webSocket)
        {
            _webSocket = webSocket;
        }

        public string Id { get; } = Guid.NewGuid().ToString("N");

        public Task SendAsync(string data)
        {
            if (_webSocket.State != WebSocketState.Open)
            {
                return Task.CompletedTask;
            }

            var bytes = Encoding.UTF8.GetBytes(data);
            return _webSocket.SendAsync(new ArraySegment<byte>(bytes, 0, bytes.Length), WebSocketMessageType.Text,
                true, CancellationToken.None);
        }
    }
}