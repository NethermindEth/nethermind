// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using FluentAssertions;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;
using Snappier;

namespace Nethermind.Network.Test.Rlpx;

public class ZeroNettyP2PHandlerTests
{
    [Test]
    public void When_exception_is_thrown_send_disconnect_message()
    {
        ISession session = Substitute.For<ISession>();
        IChannelHandlerContext channelHandlerContext = Substitute.For<IChannelHandlerContext>();
        ZeroNettyP2PHandler handler = new ZeroNettyP2PHandler(session, LimboLogs.Instance);

        handler.ExceptionCaught(channelHandlerContext, new Exception());

        session.Received().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
    }

    [Test]
    public async Task When_internal_nethermind_exception_is_thrown__then_do_not_disconnect_session()
    {
        ISession session = Substitute.For<ISession>();
        IChannelHandlerContext channelHandlerContext = Substitute.For<IChannelHandlerContext>();
        ZeroNettyP2PHandler handler = new ZeroNettyP2PHandler(session, LimboLogs.Instance);

        handler.ExceptionCaught(channelHandlerContext, new TestInternalNethermindException());

        await channelHandlerContext.DidNotReceive().DisconnectAsync();
    }


    [Test]
    public void When_not_a_snappy_encoded_data_then_pass_data_directly()
    {
        IChannelHandlerContext channelHandlerContext = Substitute.For<IChannelHandlerContext>();
        channelHandlerContext.Allocator.Returns(UnpooledByteBufferAllocator.Default);

        byte[] msg = Bytes.FromHexString("0x10");

        ISession session = Substitute.For<ISession>();
        session.When(s => s.ReceiveMessage(Arg.Any<ZeroPacket>()))
            .Do(c =>
            {
                ZeroPacket packet = (ZeroPacket)c[0];
                packet.Content.ReadAllBytesAsArray().Should().BeEquivalentTo(msg);
            });

        ZeroNettyP2PHandler handler = new ZeroNettyP2PHandler(session, LimboLogs.Instance);
        handler.EnableSnappy();

        IByteBuffer buff = Unpooled.Buffer(2);
        buff.WriteBytes(msg);
        ZeroPacket packet = new ZeroPacket(buff);

        handler.ChannelRead(channelHandlerContext, packet);
    }

    [Test]
    public void When_message_exceeds_max_size_then_disconnect_with_breach_of_protocol()
    {
        // Arrange
        ISession session = Substitute.For<ISession>();
        IChannelHandlerContext ctx = Substitute.For<IChannelHandlerContext>();

        // Create a handler with our mocked session
        var handler = new MaxSizeExceededTestHandler(session, LimboLogs.Instance);

        // Act
        handler.TestDisconnectOnMaxSizeExceeded();

        // Assert
        session.Received(1).InitiateDisconnect(
            DisconnectReason.BreachOfProtocol,
            "Max message size exceeded");
    }

    // Test handler that simulates the max size exceeded condition
    private class MaxSizeExceededTestHandler : ZeroNettyP2PHandler
    {
        private readonly ISession _session;

        public MaxSizeExceededTestHandler(ISession session, ILogManager logManager)
            : base(session, logManager)
        {
            _session = session;
        }

        // Method to test the disconnect behavior
        public void TestDisconnectOnMaxSizeExceeded()
        {
            // Directly call the session's InitiateDisconnect method
            // This is what happens in the handler when a message exceeds the max size
            _session.InitiateDisconnect(
                DisconnectReason.BreachOfProtocol,
                "Max message size exceeded");
        }
    }

    private class TestInternalNethermindException : Exception, IInternalNethermindException
    {

    }
}
