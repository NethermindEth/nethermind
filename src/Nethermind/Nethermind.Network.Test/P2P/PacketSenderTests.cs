// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using DotNetty.Transport.Channels;
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
        private (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage message) SetupChannel(bool isActive)
        {
            IByteBuffer serialized = UnpooledByteBufferAllocator.Default.Buffer(2);
            IMessageSerializationService serializer = Substitute.For<IMessageSerializationService>();

            TestMessage testMessage = new();
            serializer.ZeroSerialize(testMessage).Returns(serialized);
            serialized.SafeRelease();

            IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
            IChannel channel = Substitute.For<IChannel>();
            channel.IsWritable.Returns(true);
            channel.Active.Returns(isActive);
            context.Channel.Returns(channel);

            return (context, serializer, testMessage);
        }

        [TestCase(true, 1, Description = "Does send on active channel")]
        [TestCase(false, 0, Description = "Does not try to send on inactive channel")]
        public void Send_respects_channel_active_state(bool isActive, int expectedSendCount)
        {
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage testMessage) = SetupChannel(isActive);

            PacketSender packetSender = new(serializer, LimboLogs.Instance, TimeSpan.Zero);
            packetSender.HandlerAdded(context);
            packetSender.Enqueue(testMessage);

            context.Received(expectedSendCount).WriteAndFlushAsync(Arg.Any<IByteBuffer>());
        }

        [Test]
        public async Task Send_after_delay_if_specified()
        {
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage testMessage) = SetupChannel(isActive: true);

            TimeSpan delay = TimeSpan.FromMilliseconds(100);

            PacketSender packetSender = new(serializer, LimboLogs.Instance, delay);
            packetSender.HandlerAdded(context);
            packetSender.Enqueue(testMessage);

            await context.Received(0).WriteAndFlushAsync(Arg.Any<IByteBuffer>());

            await Task.Delay(delay * 3);

            await context.Received(1).WriteAndFlushAsync(Arg.Any<IByteBuffer>());
        }

        [Test]
        public void Returns_buffer_when_delay_cannot_complete([Values(-2, 60000)] int delayMilliseconds)
        {
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage testMessage) = SetupChannel(isActive: true);
            IByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(131072);
            buffer.WriteZero(131072);
            serializer.ZeroSerialize(testMessage, Arg.Any<IByteBufferAllocator>()).Returns(buffer);
            PacketSender packetSender = new(serializer, LimboLogs.Instance, TimeSpan.FromMilliseconds(delayMilliseconds));
            packetSender.HandlerAdded(context);
            try
            {
                packetSender.Enqueue(testMessage);
                packetSender.HandlerRemoved(context);

                Assert.That(() => buffer.ReferenceCount, Is.Zero.After(1000, 10));
                context.DidNotReceive().WriteAndFlushAsync(Arg.Any<IByteBuffer>());
            }
            finally
            {
                if (buffer.ReferenceCount > 0) buffer.SafeRelease();
            }
        }

        [Test]
        public void Does_not_release_buffer_after_pipeline_handoff([Values] bool writeFails)
        {
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage testMessage) = SetupChannel(isActive: true);
            IByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(2);
            serializer.ZeroSerialize(testMessage, Arg.Any<IByteBufferAllocator>()).Returns(buffer);
            context.WriteAndFlushAsync(buffer).Returns(writeFails ? Task.FromException(new InvalidOperationException()) : Task.CompletedTask);
            PacketSender packetSender = new(serializer, LimboLogs.Instance, TimeSpan.Zero);
            packetSender.HandlerAdded(context);
            try
            {
                packetSender.Enqueue(testMessage);
                packetSender.HandlerRemoved(context);

                context.Received(1).WriteAndFlushAsync(buffer);
                Assert.That(buffer.ReferenceCount, Is.EqualTo(1));
            }
            finally
            {
                if (buffer.ReferenceCount > 0) buffer.SafeRelease();
            }
        }
        private class TestMessage : P2PMessage
        {
            public override int PacketType { get; } = 0;
            public override string Protocol { get; } = "";
        }
    }
}
