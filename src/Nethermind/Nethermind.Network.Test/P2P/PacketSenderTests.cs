//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using Nethermind.Logging;
using Nethermind.Network.P2P;
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
