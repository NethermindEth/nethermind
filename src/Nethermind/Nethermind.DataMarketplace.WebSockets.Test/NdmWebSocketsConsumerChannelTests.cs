// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Channels;
using Nethermind.Sockets;
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
            ISocketsClient webSocketsClient = Substitute.For<ISocketsClient>();
            NdmWebSocketsConsumerChannel channel = new NdmWebSocketsConsumerChannel(webSocketsClient);
            channel.PublishAsync(Keccak.Zero, "client", "data");
            webSocketsClient.Received().SendAsync(Arg.Is<SocketsMessage>(ws => ws.Client == "client" && ws.Type == "data_received"));
        }

        [Test]
        public void Channel_type_is_web_sockets()
        {
            ISocketsClient webSocketsClient = Substitute.For<ISocketsClient>();
            NdmWebSocketsConsumerChannel channel = new NdmWebSocketsConsumerChannel(webSocketsClient);
            channel.Type.Should().Be(NdmConsumerChannelType.WebSockets);
        }
    }
}
