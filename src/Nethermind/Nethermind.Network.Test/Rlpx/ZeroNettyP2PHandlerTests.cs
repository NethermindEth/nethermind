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
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
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
        foreach ((string hex, string name) in new (string, string)[]
        {
            ("0b00a51d01", "Copy1_overruns_declared_length_by_one"),
            ("4000a5fe0100", "Copy2_overruns_declared_length_by_one"),
            ("4000a5ff01000000", "Copy4_overruns_declared_length_by_one"),
            ("0c00a51d", "Copy1_missing_offset"),
            ("4100a5fe", "Copy2_missing_offset"),
            ("4100a5fe01", "Copy2_truncated_offset"),
            ("4100a5ff", "Copy4_missing_offset"),
            ("4100a5ff01", "Copy4_one_offset_byte"),
            ("4100a5ff0100", "Copy4_two_offset_bytes"),
            ("4100a5ff010000", "Copy4_three_offset_bytes"),
            ("0c00a51d00", "Copy1_zero_offset"),
            ("4100a5fe0200", "Copy2_offset_exceeds_history"),
            ("4100a5ffffffffff", "Copy4_offset_exceeds_history"),
            ("01f0", "Literal_missing_one_byte_length"),
            ("01f4ff", "Literal_truncated_two_byte_length"),
            ("01f8ffff", "Literal_truncated_three_byte_length"),
            ("01fcffffff", "Literal_truncated_four_byte_length"),
            ("01fcffffff7f", "Literal_length_overflows_signed_int"),
            ("01fcffffffff", "Literal_length_plus_one_overflows_uint")
        })
        {
            yield return new TestCaseData(Convert.FromHexString(hex)).SetName(name);
        }
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
        using DisposableByteBuffer backing = detector.Allocator.Buffer(length + 43).AsDisposable();
        backing.Array.AsSpan(backing.ArrayOffset, backing.Capacity).Fill(0xa5);
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
            using (Assert.EnterMultipleScope())
            {
                Assert.That(output.GetInt(0), Is.EqualTo(0x12345678), "output prefix must remain intact");
                Assert.That(backing.Array.AsSpan(backing.ArrayOffset, 7).ToArray(), Is.All.EqualTo(0xa5));
                Assert.That(backing.Array.AsSpan(backing.ArrayOffset + length + 11, 32).ToArray(), Is.All.EqualTo(0xa5));
            }
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

    public enum RetainedConsumer { Packet, Slice, MemoryOwner }

    [Test]
    public void Retained_snappy_message_survives_later_messages([Values] RetainedConsumer consumer)
    {
        using PooledBufferLeakDetector detector = new();
        IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
        context.Allocator.Returns(detector.Allocator);
        ISession session = Substitute.For<ISession>();
        ZeroNettyP2PHandler handler = new(session, LimboLogs.Instance);
        handler.EnableSnappy();
        ZeroPacket retained = null;
        NettyBufferMemoryOwner owner = null;
        IByteBuffer retainedBuffer = null;
        session.When(s => s.ReceiveMessage(Arg.Any<ZeroPacket>())).Do(call =>
        {
            ZeroPacket packet = call.Arg<ZeroPacket>();
            if (retainedBuffer is null)
            {
                retainedBuffer = packet.Content;
                if (consumer == RetainedConsumer.MemoryOwner)
                    owner = new NettyBufferMemoryOwner(packet.Content);
                else if (consumer == RetainedConsumer.Slice)
                    retained = new ZeroPacket(packet.Content.RetainedSlice()) { PacketType = packet.PacketType };
                else
                {
                    packet.Retain();
                    retained = packet;
                }
            }
            else
            {
                Assert.That(packet.Content, Is.Not.SameAs(retainedBuffer));
                AssertPacket(packet, [9, 8, 7], 8);
            }
        });
        try
        {
            handler.ChannelRead(context, CreateCompressedPacket(detector.Allocator, [1, 2, 3], 7));
            handler.ChannelRead(context, CreateCompressedPacket(detector.Allocator, [9, 8, 7], 8));
            session.Received(2).ReceiveMessage(Arg.Any<ZeroPacket>());
            if (owner is not null)
            {
                Assert.That(owner.Memory.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
                owner.Dispose();
                Assert.That(retainedBuffer.ReferenceCount, Is.Zero);
            }
            else AssertPacket(retained, [1, 2, 3], 7);
        }
        finally
        {
            handler.HandlerRemoved(context);
            retained?.Release();
            owner?.Dispose();
        }
    }

    [Test]
    public void Decoded_transaction_survives_snappy_output_reuse()
    {
        using PooledBufferLeakDetector detector = new();
        IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
        context.Allocator.Returns(detector.Allocator);
        ISession session = Substitute.For<ISession>();
        ZeroNettyP2PHandler handler = new(session, LimboLogs.Instance);
        handler.EnableSnappy();
        Transaction source = new()
        {
            Data = new byte[] { 1, 2, 3, 4 },
            GasLimit = 21000,
            Signature = new Signature(1, 2, 27)
        };
        Hash256 expectedHash = Keccak.Compute(TxDecoder.Instance.Encode(source).Bytes);
        TransactionsMessageSerializer serializer = new();
        using TransactionsMessage message = new(new ArrayPoolList<Transaction>(1) { source });
        using DisposableByteBuffer encoded = detector.Allocator.Buffer(256).AsDisposable();
        serializer.Serialize(encoded, message);
        byte[] payload = encoded.ReadAllBytesAsArray();
        Transaction decoded = null;
        IByteBuffer firstBuffer = null;
        session.When(s => s.ReceiveMessage(Arg.Any<ZeroPacket>())).Do(call =>
        {
            ZeroPacket packet = call.Arg<ZeroPacket>();
            if (decoded is null)
            {
                firstBuffer = packet.Content;
                using TransactionsMessage received = serializer.Deserialize(packet.Content);
                decoded = received.Transactions[0];
            }
            else
            {
                Assert.That(packet.Content, Is.SameAs(firstBuffer), "Overwrite the actual decoder input storage.");
                AssertPacket(packet, payload, 8);
            }
        });
        try
        {
            handler.ChannelRead(context, CreateCompressedPacket(detector.Allocator, payload, 7));
            Array.Fill(payload, (byte)0xa5);
            handler.ChannelRead(context, CreateCompressedPacket(detector.Allocator, payload, 8));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(decoded.Data.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
                // Read Hash only after reuse so its delayed preimage must survive the overwrite.
                Assert.That(decoded.Hash, Is.EqualTo(expectedHash));
            }
        }
        finally
        {
            handler.HandlerRemoved(context);
            if (decoded is not null) TxDecoder.TxObjectPool.Return(decoded);
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

    [Test]
    public void Truncated_snappy_blocks_do_not_publish_or_poison_reused_output(
        [Values(17, 128, 1024, 65537)] int length)
    {
        using PooledBufferLeakDetector detector = new();
        IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
        context.Allocator.Returns(detector.Allocator);
        ISession session = Substitute.For<ISession>();
        ZeroNettyP2PHandler handler = new(session, LimboLogs.Instance);
        handler.EnableSnappy();
        byte[] payload = new byte[length];
        Random random = new(13592);
        random.NextBytes(payload);
        byte[] compressed = Snappy.CompressToArray(payload);
        int delivered = 0;
        session.When(s => s.ReceiveMessage(Arg.Any<ZeroPacket>())).Do(call =>
        {
            AssertPacket(call.Arg<ZeroPacket>(), payload, 7);
            delivered++;
        });
        try
        {
            for (int i = 0; i < 32; i++)
            {
                int truncatedLength = i == 0 ? 0 : i == 1 ? compressed.Length - 1 : random.Next(compressed.Length);
                // Bytes beyond WriterIndex remain present, but must never complete the truncated block.
                IByteBuffer buffer = detector.Allocator.Buffer(compressed.Length + 7).WriteZero(7).WriteBytes(compressed);
                buffer.SetIndex(7, 7 + truncatedLength);
                ZeroPacket malformed = new(buffer);
                Assert.That(() => handler.ChannelRead(context, malformed), Throws.InstanceOf<CorruptedFrameException>(), $"cut {truncatedLength}");
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(malformed.ReferenceCount, Is.Zero);
                    Assert.That(delivered, Is.EqualTo(i));
                }
                handler.ChannelRead(context, CreateCompressedPacket(detector.Allocator, payload, 7));
                Assert.That(delivered, Is.EqualTo(i + 1));
            }
        }
        finally
        {
            handler.HandlerRemoved(context);
        }
    }

    [Test]
    public void Snappy_accepts_declared_length_at_limit([Values(-1, 0)] int delta)
    {
        using PooledBufferLeakDetector detector = new();
        IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
        context.Allocator.Returns(detector.Allocator);
        ISession session = Substitute.For<ISession>();
        ZeroNettyP2PHandler handler = new(session, LimboLogs.Instance);
        handler.EnableSnappy();
        byte[] payload = new byte[SnappyParameters.MaxSnappyLength + delta];
        payload.AsSpan().Fill(0xa5);
        session.When(s => s.ReceiveMessage(Arg.Any<ZeroPacket>()))
            .Do(call => AssertPacket(call.Arg<ZeroPacket>(), payload, 7));
        try
        {
            handler.ChannelRead(context, CreateCompressedPacket(detector.Allocator, payload, 7));
            session.Received(1).ReceiveMessage(Arg.Any<ZeroPacket>());
            session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
        finally
        {
            handler.HandlerRemoved(context);
        }
    }

    [TestCase("0c00a51d01", 12)]
    [TestCase("4100a5fe0100", 65)]
    [TestCase("4100a5ff01000000", 65)]
    public void Snappy_maximum_length_copy_tags_support_overlapping_offset_one(string hex, int length)
        => AssertRawSnappyBlock(Convert.FromHexString(hex), length, valid: true);

    [Test]
    public void Snappy_overlapping_copies_end_at_output_limit_or_overrun_by_one([Values] bool overrun)
    {
        const int length = SnappyParameters.MaxSnappyLength;
        int fullCopies = (length - 1) / 64;
        byte[] encoded = new byte[6 + (fullCopies + 1) * 3];
        // Raw Snappy: 16 MiB varint, one literal, then offset-one COPY_2 commands.
        new byte[] { 0x80, 0x80, 0x80, 0x08, 0x00, 0xa5 }.CopyTo(encoded, 0);
        for (int offset = 6; offset < encoded.Length; offset += 3)
        {
            encoded[offset] = 0xfe;
            encoded[offset + 1] = 1;
        }
        if (!overrun) encoded[^3] = 0xfa; // Final copy is 63 bytes, completing exactly 16 MiB.
        AssertRawSnappyBlock(encoded, length, valid: !overrun);
    }

    private static void AssertRawSnappyBlock(byte[] encoded, int length, bool valid)
    {
        using PooledBufferLeakDetector detector = new();
        IByteBufferAllocator allocator = Substitute.For<IByteBufferAllocator>();
        allocator.Buffer(Arg.Any<int>()).Returns(call => detector.Allocator.Buffer(call.Arg<int>()));
        IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
        context.Allocator.Returns(allocator);
        ISession session = Substitute.For<ISession>();
        ZeroNettyP2PHandler handler = new(session, LimboLogs.Instance);
        handler.EnableSnappy();
        int delivered = 0;
        session.When(s => s.ReceiveMessage(Arg.Any<ZeroPacket>())).Do(call =>
        {
            IByteBuffer content = call.Arg<ZeroPacket>().Content;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(content.ReadableBytes, Is.EqualTo(length));
                Assert.That(content.Array.AsSpan(content.ArrayOffset + content.ReaderIndex, content.ReadableBytes).IndexOfAnyExcept((byte)0xa5), Is.EqualTo(-1));
            }
            delivered++;
        });
        ZeroPacket packet = new(detector.Allocator.Buffer(encoded.Length + 7).WriteZero(7).WriteBytes(encoded).SkipBytes(7));
        try
        {
            if (valid)
            {
                handler.ChannelRead(context, packet);
            }
            else
            {
                Assert.That(() => handler.ChannelRead(context, packet), Throws.InstanceOf<CorruptedFrameException>());
                allocator.DidNotReceive().Buffer(Arg.Any<int>());
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(delivered, Is.EqualTo(valid ? 1 : 0));
                Assert.That(packet.ReferenceCount, Is.Zero);
            }
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
