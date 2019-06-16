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
using Nethermind.Core.Crypto;

namespace Nethermind.DataMarketplace.Channels
{
    public class WebSocketsNdmConsumerChannel : INdmConsumerChannel
    {
        private readonly WebSocket _webSocket;
        private readonly Keccak _depositId;
        public NdmConsumerChannelType Type => NdmConsumerChannelType.WebSockets;

        public WebSocketsNdmConsumerChannel(WebSocket webSocket, Keccak depositId)
        {
            _webSocket = webSocket;
            _depositId = depositId;
        }

        public async Task PublishAsync(Keccak depositId, string data)
        {
            if (_depositId != depositId)
            {
                return;
            }
            
            if (_webSocket.State != WebSocketState.Open)
            {
                return;
            }
            
            var bytes = Encoding.UTF8.GetBytes(data);
            await _webSocket.SendAsync(new ArraySegment<byte>(bytes, 0, bytes.Length), WebSocketMessageType.Text,
                true, CancellationToken.None);
        }
    }
}