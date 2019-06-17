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

using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Channels;
using Nethermind.WebSockets;

namespace Nethermind.DataMarketplace.WebSockets
{
    public class WebSocketsNdmConsumerChannel : INdmConsumerChannel
    {
        private readonly IWebSocketsClient _client;
        private readonly Keccak _depositId;
        public NdmConsumerChannelType Type => NdmConsumerChannelType.WebSockets;

        public WebSocketsNdmConsumerChannel(IWebSocketsClient client, Keccak depositId)
        {
            _client = client;
            _depositId = depositId;
        }

        public Task PublishAsync(Keccak depositId, string data)
            => _depositId != depositId ? Task.CompletedTask : _client.SendAsync(data);
    }
}