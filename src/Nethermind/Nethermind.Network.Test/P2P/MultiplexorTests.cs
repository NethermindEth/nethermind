using DotNetty.Transport.Channels;
using Nethermind.Core.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    [TestFixture]
    public class MultiplexorTests
    {
        [Test]
        public void Does_send_on_active_channel()
        {
            Packet packet = new Packet("pro", 1, new byte[] {1, 2, 3});
            IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
            IChannel channel = Substitute.For<IChannel>();
            channel.Active.Returns(true);
            context.Channel.Returns(channel);
            
            PacketSender packetSender = new PacketSender(NullLogManager.Instance);
            packetSender.HandlerAdded(context);
            packetSender.Enqueue(packet);

            context.Received(1).WriteAndFlushAsync(packet);
        }
        
        [Test]
        public void Does_not_try_to_send_on_inactive_channel()
        {
            Packet packet = new Packet("pro", 1, new byte[] {1, 2, 3});
            IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
            IChannel channel = Substitute.For<IChannel>();
            channel.Active.Returns(false);
            context.Channel.Returns(channel);
            
            PacketSender packetSender = new PacketSender(NullLogManager.Instance);
            packetSender.HandlerAdded(context);
            packetSender.Enqueue(packet);

            context.Received(0).WriteAndFlushAsync(packet);
        }
    }
}