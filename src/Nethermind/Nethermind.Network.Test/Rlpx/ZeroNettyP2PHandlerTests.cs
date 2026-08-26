// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test.Builders;
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
    [TestCaseSource(nameof(ExceptionDisconnectCases))]
    public void When_exception_is_thrown_send_disconnect_message(Exception exception, DisconnectReason expectedReason)
    {
        ISession session = Substitute.For<ISession>();
        IChannelHandlerContext channelHandlerContext = Substitute.For<IChannelHandlerContext>();
        ZeroNettyP2PHandler handler = new(session, LimboLogs.Instance);

        handler.ExceptionCaught(channelHandlerContext, exception);

        session.Received().InitiateDisconnect(expectedReason, Arg.Any<string>());
    }

    private static IEnumerable<TestCaseData> ExceptionDisconnectCases()
    {
        yield return new TestCaseData(new Exception(), DisconnectReason.Exception).SetName("Generic_exception_uses_generic_reason");
        yield return new TestCaseData(new CorruptedFrameException("malformed frame"), DisconnectReason.Exception).SetName("Corrupted_frame_uses_generic_reason");
    }

    [TestCase(true)]
    [TestCase(false)]
    public void When_corrupted_frame_is_received_from_privileged_peer_then_keep_session(bool isStatic)
    {
        Node node = new(TestItem.PublicKeyA, "127.0.0.1", 30303)
        {
            IsStatic = isStatic,
            IsTrusted = !isStatic
        };
        ISession session = Substitute.For<ISession>();
        session.Node.Returns(node);
        IChannelHandlerContext channelHandlerContext = Substitute.For<IChannelHandlerContext>();
        ZeroNettyP2PHandler handler = new(session, LimboLogs.Instance);
        CorruptedFrameException exception = new("malformed frame");

        handler.ExceptionCaught(channelHandlerContext, exception);

        session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        channelHandlerContext.Received().FireExceptionCaught(exception);
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
        IByteBufferAllocator allocator = Substitute.For<IByteBufferAllocator>();
        IChannelHandlerContext channelHandlerContext = Substitute.For<IChannelHandlerContext>();
        channelHandlerContext.Allocator.Returns(allocator);

        ISession session = Substitute.For<ISession>();
        ZeroNettyP2PHandler handler = new(session, LimboLogs.Instance);
        handler.EnableSnappy();

        IByteBuffer buff = Unpooled.Buffer(2);
        buff.WriteBytes(msg);
        ZeroPacket packet = new(buff);

        Assert.That(() => handler.ChannelRead(channelHandlerContext, packet), Throws.InstanceOf<CorruptedFrameException>());

        session.DidNotReceive().ReceiveMessage(Arg.Any<ZeroPacket>());
        allocator.DidNotReceive().Buffer(Arg.Any<int>());
        Assert.That(packet.ReferenceCount, Is.Zero, "the inbound packet must be released even when decoding throws");
    }

    private static IEnumerable<TestCaseData> MalformedSnappyPayloads()
    {
        yield return new TestCaseData(new byte[] { 0x80 }).SetName("Invalid_length_varint");
        yield return new TestCaseData(new byte[] { 0x01 }).SetName("Missing_literal_data");
        yield return new TestCaseData(new byte[] { 0x01, 0x04, 0x41, 0x42 }).SetName("Literal_exceeds_declared_length");
        yield return new TestCaseData(new byte[] { 0x01, 0x00, 0x41, 0x01 }).SetName("Incomplete_tag_after_declared_length");
        yield return new TestCaseData(new byte[] { 0x00, 0x00 }).SetName("Suffix_after_zero_length_block");
        yield return new TestCaseData(new byte[] { 0x80, 0x80, 0x80, 0x08 }).SetName("Declared_length_cannot_be_represented_by_payload");
        // A frame sized exactly to its packet type prefix leaves no content bytes at all.
        yield return new TestCaseData(Array.Empty<byte>()).SetName("Empty_payload");
    }

    [Test]
    [TestCaseSource(nameof(SnappyPayloadsExceedingMaxLength))]
    public void When_message_exceeds_max_size_then_disconnect_with_breach_of_protocol(byte[] data)
    {
        IByteBufferAllocator allocator = Substitute.For<IByteBufferAllocator>();
        ISession session = Substitute.For<ISession>();
        IChannelHandlerContext channelHandlerContext = Substitute.For<IChannelHandlerContext>();
        channelHandlerContext.Allocator.Returns(allocator);

        ZeroNettyP2PHandler handler = new(session, LimboLogs.Instance);
        handler.EnableSnappy();

        IByteBuffer content = Unpooled.WrappedBuffer(data);
        ZeroPacket packet = new(content);

        handler.ChannelRead(channelHandlerContext, packet);

        session.Received().InitiateDisconnect(DisconnectReason.BreachOfProtocol, "Max message size exceeded");
        session.DidNotReceive().ReceiveMessage(Arg.Any<ZeroPacket>());
        allocator.DidNotReceive().Buffer(Arg.Any<int>());
        Assert.That(packet.ReferenceCount, Is.Zero, "the inbound packet must be released after disconnecting");
    }

    private static IEnumerable<TestCaseData> SnappyPayloadsExceedingMaxLength()
    {
        yield return new TestCaseData(Snappy.CompressToArray(Enumerable.Repeat<byte>(0, SnappyParameters.MaxSnappyLength + 1).ToArray()))
            .SetName("Declared_length_exceeds_limit");
        yield return new TestCaseData(new byte[] { 0xff, 0xff, 0xff, 0xff, 0x0f })
            .SetName("Declared_length_overflows_int");
    }

    private class TestInternalNethermindException : Exception, IInternalNethermindException
    {

    }
}
