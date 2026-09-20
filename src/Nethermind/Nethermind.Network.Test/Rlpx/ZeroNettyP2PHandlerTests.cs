// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Handlers.Timeout;
using DotNetty.Transport.Channels;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
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

    [TestCaseSource(nameof(ExpectedCommunicationExceptions))]
    public void Expected_communication_exception_is_logged_at_trace(Exception exception)
    {
        TestLogger logger = new() { IsDebug = false };
        ISession session = Substitute.For<ISession>();
        ZeroNettyP2PHandler handler = new(session, new OneLoggerLogManager(new ILogger(logger)));

        handler.ExceptionCaught(Substitute.For<IChannelHandlerContext>(), exception);

        Assert.That(logger.LogList, Has.Some.Contains(exception.GetType().Name));
    }

    private static IEnumerable<TestCaseData> ExpectedCommunicationExceptions()
    {
        yield return new TestCaseData(new RlpException("malformed message")).SetName("RLP_exception_is_logged_at_trace");
        yield return new TestCaseData(ReadTimeoutException.Instance).SetName("Read_timeout_is_logged_at_trace");
    }

    [Test]
    public void When_corrupted_frame_is_received_from_privileged_peer_then_keep_session([Values] bool isStatic)
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

    [Test]
    public void Snappy_releases_buffers_when_consumer_finishes([Values] bool retain, [Values] bool throws)
    {
        using PooledBufferLeakDetector detector = new();
        IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
        context.Allocator.Returns(detector.Allocator);
        ISession session = Substitute.For<ISession>();
        ZeroNettyP2PHandler handler = new(session, LimboLogs.Instance);
        handler.EnableSnappy();
        byte[] payload = [1, 2, 3, 4, 5];
        ZeroPacket received = null;
        session.When(s => s.ReceiveMessage(Arg.Any<ZeroPacket>())).Do(call =>
        {
            received = call.Arg<ZeroPacket>();
            if (retain) received.Retain();
            AssertPacket(received, payload, 7);
            if (throws) throw new InvalidOperationException("Consumer failed");
        });
        ZeroPacket input = CreateCompressedPacket(detector.Allocator, payload, 7);
        try
        {
            if (throws)
                Assert.That(() => handler.ChannelRead(context, input), Throws.TypeOf<InvalidOperationException>());
            else
                handler.ChannelRead(context, input);

            handler.HandlerRemoved(context);
            Assert.That(input.ReferenceCount, Is.Zero);
            Assert.That(received, Is.Not.Null);
            Assert.That(received.ReferenceCount, Is.EqualTo(retain ? 1 : 0));
            if (retain) AssertPacket(received, payload, 7);
        }
        finally
        {
            handler.HandlerRemoved(context);
            if (retain) received?.Release();
        }
    }

    [Test]
    public void Snappy_decodes_into_offset_output_and_recovers_after_malformed_message(
        [Values(0, 1, 17, 65536)] int length, [Values] bool repeated)
    {
        using PooledBufferLeakDetector detector = new();
        using DisposableByteBuffer backing = detector.Allocator.Buffer(length + 11).AsDisposable();
        IByteBuffer output = backing.Slice(7, length + 4).Clear();
        output.WriteInt(0x12345678).SkipBytes(4);
        IByteBufferAllocator allocator = Substitute.For<IByteBufferAllocator>();
        allocator.Buffer(length).Returns(_ => (IByteBuffer)output.Retain());
        IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
        context.Allocator.Returns(allocator);
        ISession session = Substitute.For<ISession>();
        ZeroNettyP2PHandler handler = new(session, LimboLogs.Instance);
        handler.EnableSnappy();
        byte[] payload = new byte[length];
        if (repeated) payload.AsSpan().Fill(42);
        else new Random(42).NextBytes(payload);
        session.When(s => s.ReceiveMessage(Arg.Any<ZeroPacket>()))
            .Do(call => AssertPacket(call.Arg<ZeroPacket>(), payload, 7));

        for (int i = 0; i < 2; i++)
        {
            output.SetWriterIndex(4);
            handler.ChannelRead(context, CreateCompressedPacket(detector.Allocator, payload, 7));
            Assert.That(output.GetInt(0), Is.EqualTo(0x12345678), "output prefix must remain intact");
            ZeroPacket malformed = new(Unpooled.WrappedBuffer(new byte[] { 1 }));
            Assert.That(() => handler.ChannelRead(context, malformed), Throws.InstanceOf<CorruptedFrameException>());
            Assert.That(malformed.ReferenceCount, Is.Zero);
        }
        session.Received(2).ReceiveMessage(Arg.Any<ZeroPacket>());
    }

    [Test]
    public void Snappy_releases_output_when_writer_fails_and_accepts_next_message()
    {
        using PooledBufferLeakDetector detector = new();
        IByteBuffer failedOutput = detector.Allocator.Buffer(1, 1);
        IByteBufferAllocator allocator = Substitute.For<IByteBufferAllocator>();
        int allocations = 0;
        allocator.Buffer(5).Returns(_ => allocations++ == 0 ? failedOutput : detector.Allocator.Buffer(5));
        IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
        context.Allocator.Returns(allocator);
        ISession session = Substitute.For<ISession>();
        ZeroNettyP2PHandler handler = new(session, LimboLogs.Instance);
        handler.EnableSnappy();
        byte[] payload = [1, 2, 3, 4, 5];
        ZeroPacket input = CreateCompressedPacket(detector.Allocator, payload, 7);

        Assert.That(() => handler.ChannelRead(context, input), Throws.InstanceOf<IndexOutOfRangeException>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(input.ReferenceCount, Is.Zero);
            Assert.That(failedOutput.ReferenceCount, Is.Zero);
        }
        session.DidNotReceive().ReceiveMessage(Arg.Any<ZeroPacket>());
        session.When(s => s.ReceiveMessage(Arg.Any<ZeroPacket>()))
            .Do(call => AssertPacket(call.Arg<ZeroPacket>(), payload, 7));
        handler.ChannelRead(context, CreateCompressedPacket(detector.Allocator, payload, 7));
        handler.HandlerRemoved(context);
        session.Received(1).ReceiveMessage(Arg.Any<ZeroPacket>());
    }

    [Test]
    public void Retained_snappy_message_survives_later_messages([Values] bool slice)
    {
        using PooledBufferLeakDetector detector = new();
        IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
        context.Allocator.Returns(detector.Allocator);
        ISession session = Substitute.For<ISession>();
        ZeroNettyP2PHandler handler = new(session, LimboLogs.Instance);
        handler.EnableSnappy();
        ZeroPacket retained = null;
        session.When(s => s.ReceiveMessage(Arg.Any<ZeroPacket>())).Do(call =>
        {
            ZeroPacket packet = call.Arg<ZeroPacket>();
            if (retained is null)
            {
                if (slice)
                    retained = new ZeroPacket(packet.Content.RetainedSlice()) { PacketType = packet.PacketType };
                else
                {
                    packet.Retain();
                    retained = packet;
                }
            }
            else AssertPacket(packet, [9, 8, 7], 8);
        });
        try
        {
            handler.ChannelRead(context, CreateCompressedPacket(detector.Allocator, [1, 2, 3], 7));
            handler.ChannelRead(context, CreateCompressedPacket(detector.Allocator, [9, 8, 7], 8));
            session.Received(2).ReceiveMessage(Arg.Any<ZeroPacket>());
            AssertPacket(retained, [1, 2, 3], 7);
        }
        finally
        {
            handler.HandlerRemoved(context);
            retained?.Release();
        }
    }

    [Test]
    public void Snappy_reuses_bounded_output_and_resets_readable_range([Values(1, 65536, 65537)] int length)
    {
        using PooledBufferLeakDetector detector = new();
        IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
        context.Allocator.Returns(detector.Allocator);
        ISession session = Substitute.For<ISession>();
        ZeroNettyP2PHandler handler = new(session, LimboLogs.Instance);
        handler.EnableSnappy();
        IByteBuffer first = null;
        ZeroPacket firstPacket = null;
        byte[] payload = new byte[length];
        new Random(42).NextBytes(payload);
        session.When(s => s.ReceiveMessage(Arg.Any<ZeroPacket>())).Do(call =>
        {
            ZeroPacket packet = call.Arg<ZeroPacket>();
            if (first is null)
            {
                first = packet.Content;
                firstPacket = packet;
                AssertPacket(packet, payload, 7);
                packet.Protocol = "eth";
                packet.Content.SkipBytes(length);
                packet.Content.MarkReaderIndex().MarkWriterIndex();
            }
            else
            {
                AssertPacket(packet, [9, 8, 7], 8);
                Assert.That(packet.Protocol, Is.Null, "protocol resolution from the previous message must not survive reuse");
                Assert.That(ReferenceEquals(firstPacket, packet), Is.EqualTo(length == 65536));
                Assert.That(ReferenceEquals(first, packet.Content), Is.EqualTo(length == 65536));
                packet.Content.SkipBytes(1).ResetReaderIndex();
                AssertPacket(packet, [9, 8, 7], 8);
                packet.Content.ResetWriterIndex();
                Assert.That(packet.Content.WriterIndex, Is.Zero);
            }
        });
        try
        {
            handler.ChannelRead(context, CreateCompressedPacket(detector.Allocator, payload, 7));
            if (length > 65536) Assert.That(first.ReferenceCount, Is.Zero);
            ZeroPacket malformed = new(Unpooled.WrappedBuffer(new byte[] { 1 }));
            Assert.That(() => handler.ChannelRead(context, malformed), Throws.InstanceOf<CorruptedFrameException>());
            Assert.That(malformed.ReferenceCount, Is.Zero);
            handler.ChannelRead(context, CreateCompressedPacket(detector.Allocator, [9, 8, 7], 8));
        }
        finally
        {
            handler.HandlerRemoved(context);
        }
    }

    [Test]
    public void Snappy_reentrant_delivery_does_not_overwrite_outer_message()
    {
        using PooledBufferLeakDetector detector = new();
        IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
        context.Allocator.Returns(detector.Allocator);
        ISession session = Substitute.For<ISession>();
        ZeroNettyP2PHandler handler = new(session, LimboLogs.Instance);
        handler.EnableSnappy();
        session.When(s => s.ReceiveMessage(Arg.Any<ZeroPacket>())).Do(call =>
        {
            ZeroPacket packet = call.Arg<ZeroPacket>();
            if (packet.PacketType == 7)
            {
                handler.ChannelRead(context, CreateCompressedPacket(detector.Allocator, [9, 8, 7], 8));
                AssertPacket(packet, [1, 2, 3], 7);
            }
            else AssertPacket(packet, [9, 8, 7], 8);
        });
        try
        {
            handler.ChannelRead(context, CreateCompressedPacket(detector.Allocator, [1, 2, 3], 7));
            session.Received(2).ReceiveMessage(Arg.Any<ZeroPacket>());
        }
        finally
        {
            handler.HandlerRemoved(context);
        }
    }

    [Test]
    public void Snappy_cleanup_releases_output_even_during_callback([Values] bool inactive, [Values] bool duringCallback)
    {
        using PooledBufferLeakDetector detector = new();
        IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
        context.Allocator.Returns(detector.Allocator);
        ISession session = Substitute.For<ISession>();
        ZeroNettyP2PHandler handler = new(session, LimboLogs.Instance);
        handler.EnableSnappy();
        IByteBuffer output = null;
        void Cleanup()
        {
            if (inactive) handler.ChannelInactive(context);
            else handler.HandlerRemoved(context);
        }
        session.When(s => s.ReceiveMessage(Arg.Any<ZeroPacket>())).Do(call =>
        {
            ZeroPacket packet = call.Arg<ZeroPacket>();
            output = packet.Content;
            if (duringCallback) Cleanup();
            AssertPacket(packet, [1, 2, 3], 7);
        });
        try
        {
            handler.ChannelRead(context, CreateCompressedPacket(detector.Allocator, [1, 2, 3], 7));
            Cleanup();
            Assert.That(output.ReferenceCount, Is.Zero);
        }
        finally
        {
            handler.HandlerRemoved(context);
        }
    }

    private static ZeroPacket CreateCompressedPacket(IByteBufferAllocator allocator, byte[] payload, byte packetType)
    {
        IByteBuffer buffer = allocator.Buffer();
        buffer.WriteInt(0x12345678);
        buffer.WriteBytes(Snappy.CompressToArray(payload));
        buffer.SkipBytes(4);
        return new ZeroPacket(buffer) { PacketType = packetType };
    }

    private static void AssertPacket(ZeroPacket packet, byte[] payload, byte packetType)
    {
        byte[] actual = new byte[packet.Content.ReadableBytes];
        packet.Content.GetBytes(packet.Content.ReaderIndex, actual);
        Assert.That(actual, Is.EqualTo(payload));
        Assert.That(packet.PacketType, Is.EqualTo(packetType));
    }

    private class TestInternalNethermindException : Exception, IInternalNethermindException
    {

    }
}
