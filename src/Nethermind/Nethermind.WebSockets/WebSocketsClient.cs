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
using Nethermind.Core;

namespace Nethermind.WebSockets
{
    public class WebSocketsClient : IWebSocketsClient
    {
        private readonly WebSocket _webSocket;
        private readonly IJsonSerializer _jsonSerializer;

        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Client { get; }

        public WebSocketsClient(WebSocket webSocket, string client, IJsonSerializer jsonSerializer)
        {
            _webSocket = webSocket;
            Client = client;
            _jsonSerializer = jsonSerializer;
        }

        public Task ReceiveAsync(byte[] data) => Task.CompletedTask;

        public Task SendAsync(WebSocketsMessage message)
        {
            if (message is null)
            {
                return Task.CompletedTask;
            }

            if (message.Client == Client || string.IsNullOrWhiteSpace(Client) ||
                string.IsNullOrWhiteSpace(message.Client))
            {
                return SendRawAsync(_jsonSerializer.Serialize(new
                {
                    type = message.Type,
                    client = Client,
                    data = message.Data
                }));
            }

            return Task.CompletedTask;
        }

        public Task SendRawAsync(string data)
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