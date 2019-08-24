using System;
using System.Net.NetworkInformation;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    [TestFixture]
    public class PacketSenderTests
    {
        [Test]
        public void Does_send_on_active_channel()
        {
            byte[] serialized = new byte[2];
            var serializer = Substitute.For<IMessageSerializationService>();
            serializer.Serialize(PingMessage.Instance).Returns(serialized);
            IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
            IChannel channel = Substitute.For<IChannel>();
            channel.Active.Returns(true);
            context.Channel.Returns(channel);
            
            PacketSender packetSender = new PacketSender(serializer, LimboLogs.Instance);
            packetSender.HandlerAdded(context);
            packetSender.Enqueue(PingMessage.Instance);

            context.Received(1).WriteAndFlushAsync(Arg.Any<IByteBuffer>());
        }
        
        [Test]
        public void Does_not_try_to_send_on_inactive_channel()
        {
            byte[] serialized = new byte[2];
            var serializer = Substitute.For<IMessageSerializationService>();
            serializer.Serialize(PingMessage.Instance).Returns(serialized);
            IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
            IChannel channel = Substitute.For<IChannel>();
            channel.Active.Returns(false);
            context.Channel.Returns(channel);
            
            PacketSender packetSender = new PacketSender(serializer ,LimboLogs.Instance);
            packetSender.HandlerAdded(context);
            packetSender.Enqueue(PingMessage.Instance);

            context.Received(0).WriteAndFlushAsync(Arg.Any<IByteBuffer>());
        }
    }
}