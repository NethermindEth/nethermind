// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using Nethermind.Core.Exceptions;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
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
        ZeroNettyP2PHandler handler = new(session, LimboLogs.Instance);

        handler.ExceptionCaught(channelHandlerContext, new Exception());

        session.Received().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
    }

    [Test]
    public async Task When_internal_nethermind_exception_is_thrown__then_do_not_disconnect_session()
    {
        ISession session = Substitute.For<ISession>();
        IChannelHandlerContext channelHandlerContext = Substitute.For<IChannelHandlerContext>();
        ZeroNettyP2PHandler handler = new(session, LimboLogs.Instance);

        handler.ExceptionCaught(channelHandlerContext, new TestInternalNethermindException());

        await channelHandlerContext.DidNotReceive().DisconnectAsync();
    }

    [Test]
    [TestCaseSource(nameof(MalformedSnappyPayloads))]
    public void When_malformed_snappy_data_then_throw_corrupted_frame(byte[] msg)
    {
        IChannelHandlerContext channelHandlerContext = Substitute.For<IChannelHandlerContext>();
        channelHandlerContext.Allocator.Returns(UnpooledByteBufferAllocator.Default);

        ISession session = Substitute.For<ISession>();
        ZeroNettyP2PHandler handler = new(session, LimboLogs.Instance);
        handler.EnableSnappy();

        IByteBuffer buff = Unpooled.Buffer(2);
        buff.WriteBytes(msg);
        ZeroPacket packet = new(buff);

        Assert.That(() => handler.ChannelRead(channelHandlerContext, packet), Throws.InstanceOf<CorruptedFrameException>());

        session.DidNotReceive().ReceiveMessage(Arg.Any<ZeroPacket>());
    }

    private static IEnumerable<TestCaseData> MalformedSnappyPayloads()
    {
        yield return new TestCaseData(new byte[] { 0x80 }).SetName("Invalid_length_varint");
        yield return new TestCaseData(new byte[] { 0x01 }).SetName("Missing_literal_data");
        // A frame sized exactly to its packet type prefix leaves no content bytes at all.
        yield return new TestCaseData(Array.Empty<byte>()).SetName("Empty_payload");
    }

    [Test]
    public void When_message_exceeds_max_size_then_disconnect_with_breach_of_protocol()
    {
        // Arrange
        ISession session = Substitute.For<ISession>();
        IChannelHandlerContext channelHandlerContext = Substitute.For<IChannelHandlerContext>();
        channelHandlerContext.Allocator.Returns(UnpooledByteBufferAllocator.Default);

        ZeroNettyP2PHandler handler = new(session, LimboLogs.Instance);
        handler.EnableSnappy();

        // Create compressed data that will exceed MaxSnappyLength when decompressed
        byte[] data = Snappy.CompressToArray(Enumerable.Repeat<byte>(0, SnappyParameters.MaxSnappyLength + 1).ToArray());

        // Create a packet with our compressed data
        IByteBuffer content = Unpooled.Buffer(data.Length);
        content.WriteBytes(data);
        ZeroPacket packet = new(content);

        // Act
        handler.ChannelRead(channelHandlerContext, packet); // releases buffer

        // Assert
        session.Received().InitiateDisconnect(DisconnectReason.BreachOfProtocol, "Max message size exceeded");
    }

    private class TestInternalNethermindException : Exception, IInternalNethermindException
    {

    }
}
