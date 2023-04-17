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
        [Test]
        public void Does_send_on_active_channel()
        {
            IByteBuffer serialized = UnpooledByteBufferAllocator.Default.Buffer(2);
            var serializer = Substitute.For<IMessageSerializationService>();
            serializer.ZeroSerialize(PingMessage.Instance).Returns(serialized);
            serialized.SafeRelease();
            IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
            IChannel channel = Substitute.For<IChannel>();
            channel.Active.Returns(true);
            context.Channel.Returns(channel);

            PacketSender packetSender = new(serializer, LimboLogs.Instance, TimeSpan.Zero);
            packetSender.HandlerAdded(context);
            packetSender.Enqueue(PingMessage.Instance);

            context.Received(1).WriteAndFlushAsync(Arg.Any<IByteBuffer>());
        }

        [Test]
        public void Does_not_try_to_send_on_inactive_channel()
        {
            IByteBuffer serialized = UnpooledByteBufferAllocator.Default.Buffer(2);
            var serializer = Substitute.For<IMessageSerializationService>();
            serializer.ZeroSerialize(PingMessage.Instance).Returns(serialized);
            serialized.SafeRelease();
            IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
            IChannel channel = Substitute.For<IChannel>();
            channel.Active.Returns(false);
            context.Channel.Returns(channel);

            PacketSender packetSender = new(serializer, LimboLogs.Instance, TimeSpan.Zero);
            packetSender.HandlerAdded(context);
            packetSender.Enqueue(PingMessage.Instance);

            context.Received(0).WriteAndFlushAsync(Arg.Any<IByteBuffer>());
        }

        [Test]
        public async Task Send_after_delay_if_specified()
        {
            IByteBuffer serialized = UnpooledByteBufferAllocator.Default.Buffer(2);
            var serializer = Substitute.For<IMessageSerializationService>();
            serializer.ZeroSerialize(PingMessage.Instance).Returns(serialized);
            serialized.SafeRelease();
            IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
            IChannel channel = Substitute.For<IChannel>();
            channel.Active.Returns(true);
            context.Channel.Returns(channel);

            TimeSpan delay = TimeSpan.FromMilliseconds(100);

            PacketSender packetSender = new(serializer, LimboLogs.Instance, delay);
            packetSender.HandlerAdded(context);
            packetSender.Enqueue(PingMessage.Instance);

            await context.Received(0).WriteAndFlushAsync(Arg.Any<IByteBuffer>());

            await Task.Delay(delay * 2);

            await context.Received(1).WriteAndFlushAsync(Arg.Any<IByteBuffer>());
        }
    }
}
