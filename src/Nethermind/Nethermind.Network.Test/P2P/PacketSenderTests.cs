// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Embedded;
using Nethermind.Core.Buffers;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class PacketSenderTests
    {
        private (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage message, PooledBuffer buffer) SetupChannel(bool isActive)
        {
            PooledBuffer serialized = PooledBuffer.Rent(2);
            IMessageSerializationService serializer = Substitute.For<IMessageSerializationService>();

            TestMessage testMessage = new();
            serializer.ZeroSerialize(testMessage).Returns(serialized);

            IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
            IChannel channel = Substitute.For<IChannel>();
            channel.IsWritable.Returns(true);
            channel.Active.Returns(isActive);
            context.Channel.Returns(channel);

            return (context, serializer, testMessage, serialized);
        }

        [TestCase(true, 1, Description = "Does send on active channel")]
        [TestCase(false, 0, Description = "Does not try to send on inactive channel")]
        public void Send_respects_channel_active_state(bool isActive, int expectedSendCount)
        {
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage testMessage, PooledBuffer buffer) = SetupChannel(isActive);

            PacketSender packetSender = new(serializer, LimboLogs.Instance, TimeSpan.Zero);
            packetSender.HandlerAdded(context);
            int length = packetSender.Enqueue(testMessage);

            Assert.That(length, Is.EqualTo(isActive ? 2 : 0));
            context.Received(expectedSendCount).WriteAndFlushAsync(Arg.Any<IByteBuffer>());
            buffer.Dispose();
        }

        [Test]
        public async Task Send_after_delay_if_specified()
        {
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage testMessage, PooledBuffer buffer) = SetupChannel(isActive: true);

            TimeSpan delay = TimeSpan.FromMilliseconds(100);

            PacketSender packetSender = new(serializer, LimboLogs.Instance, delay);
            packetSender.HandlerAdded(context);
            packetSender.Enqueue(testMessage);

            await context.Received(0).WriteAndFlushAsync(Arg.Any<IByteBuffer>());

            await Task.Delay(delay * 3);

            await context.Received(1).WriteAndFlushAsync(Arg.Any<IByteBuffer>());
            buffer.Dispose();
        }

        [Test]
        public void Returns_buffer_when_delay_cannot_complete([Values(-2, 60000)] int delayMilliseconds)
        {
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage testMessage, PooledBuffer buffer) = SetupChannel(isActive: true);
            PacketSender packetSender = new(serializer, LimboLogs.Instance, TimeSpan.FromMilliseconds(delayMilliseconds));
            packetSender.HandlerAdded(context);
            try
            {
                packetSender.Enqueue(testMessage);
                packetSender.HandlerRemoved(context);

                Assert.That(() => IsBufferDisposed(buffer), Is.True.After(1000, 10));
                context.DidNotReceive().WriteAndFlushAsync(Arg.Any<IByteBuffer>());
            }
            finally
            {
                buffer.Dispose();
            }
        }

        [Test]
        public void Returns_pooled_buffer_after_pipeline_handoff([Values] bool writeFails)
        {
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage testMessage, PooledBuffer buffer) = SetupChannel(isActive: true);
            IByteBuffer? written = null;
            context.WriteAndFlushAsync(Arg.Do<IByteBuffer>(b => written = b))
                .Returns(writeFails ? Task.FromException(new InvalidOperationException()) : Task.CompletedTask);
            PacketSender packetSender = new(serializer, LimboLogs.Instance, TimeSpan.Zero);
            packetSender.HandlerAdded(context);
            try
            {
                packetSender.Enqueue(testMessage);
                packetSender.HandlerRemoved(context);

                context.Received(1).WriteAndFlushAsync(Arg.Any<IByteBuffer>());
                Assert.That(written?.ReadableBytes, Is.EqualTo(2));
                AssertBufferDisposed(buffer);
            }
            finally
            {
                buffer.Dispose();
            }
        }

        // A fully released rental rejects new leases.
        private static void AssertBufferDisposed(PooledBuffer buffer) =>
            Assert.Throws<ObjectDisposedException>(() => _ = buffer[..]);

        [Test]
        public void Sends_rented_bytes_through_real_pipeline_and_returns_rental()
        {
            using PooledBuffer buffer = PooledBuffer.Rent(3);
            buffer.Span[0] = 0x80;
            buffer.Span[1] = 0x12;
            buffer.Span[2] = 0x34;
            IMessageSerializationService service = Substitute.For<IMessageSerializationService>();
            service.ZeroSerialize(Arg.Any<TestMessage>()).Returns(buffer);
            PacketSender packetSender = new(service, LimboLogs.Instance, TimeSpan.Zero);
            EmbeddedChannel channel = new(packetSender);
            try
            {
                int length = packetSender.Enqueue(new TestMessage());

                IByteBuffer outbound = channel.ReadOutbound<IByteBuffer>();
                try
                {
                    Assert.That(length, Is.EqualTo(3));
                    byte[] bytes = new byte[outbound.ReadableBytes];
                    outbound.ReadBytes(bytes);
                    Assert.That(bytes, Is.EqualTo(new byte[] { 0x80, 0x12, 0x34 }));
                }
                finally
                {
                    outbound.SafeRelease();
                }

                AssertBufferDisposed(buffer);
            }
            finally
            {
                channel.FinishAndReleaseAll();
            }
        }

        private static bool IsBufferDisposed(PooledBuffer buffer)
        {
            try
            {
                _ = buffer[..];
                return false;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }

        private class TestMessage : P2PMessage
        {
            public override int PacketType { get; } = 0;
            public override string Protocol { get; } = "";
        }
    }
}
