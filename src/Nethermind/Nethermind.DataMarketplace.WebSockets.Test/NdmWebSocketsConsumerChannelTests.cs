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

using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Channels;
using Nethermind.WebSockets;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.WebSockets.Test
{
    [TestFixture]
    public class NdmWebSocketsConsumerChannelTests
    {
        [Test]
        public void Can_publish()
        {
            IWebSocketsClient webSocketsClient = Substitute.For<IWebSocketsClient>();
            NdmWebSocketsConsumerChannel channel = new NdmWebSocketsConsumerChannel(webSocketsClient);
            channel.PublishAsync(Keccak.Zero, "client", "data");
            webSocketsClient.Received().SendAsync(Arg.Is<WebSocketsMessage>(ws => ws.Client == "client" && ws.Type == "data_received"));
        }
        
        [Test]
        public void Channel_type_is_web_sockets()
        {
            IWebSocketsClient webSocketsClient = Substitute.For<IWebSocketsClient>();
            NdmWebSocketsConsumerChannel channel = new NdmWebSocketsConsumerChannel(webSocketsClient);
            channel.Type.Should().Be(NdmConsumerChannelType.WebSockets);
        }
    }
}