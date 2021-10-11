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

using System.Net;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Common.Concurrency;
using DotNetty.Transport.Channels;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx.Handshake
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class NettyHandshakeHandlerTests
    {
        [SetUp]
        public void Setup()
        {
            _session = Substitute.For<ISession>();

            _pipeline = Substitute.For<IChannelPipeline>();

            _group = new SingleThreadEventLoop();
            
            _serializationService = new MessageSerializationService();
            
            _channel = Substitute.For<IChannel>();
            _channel.Pipeline.Returns(_pipeline);
            _channel.RemoteAddress.Returns(new IPEndPoint(IPAddress.Loopback, 8003));

            _channelHandlerContext = Substitute.For<IChannelHandlerContext>();
            _channelHandlerContext.Channel.Returns(_channel);

            _handshakeService = Substitute.For<IHandshakeService>();
            _handshakeService.Auth(Arg.Any<PublicKey>(), Arg.Any<EncryptionHandshake>()).Returns(_authPacket);
            _handshakeService.Ack(Arg.Any<EncryptionHandshake>(), Arg.Any<Packet>()).Returns(_ackPacket).AndDoes(ci => ci.Arg<EncryptionHandshake>().Secrets = NetTestVectors.BuildSecretsWithSameIngressAndEgress());
            _handshakeService.When(s => s.Agree(Arg.Any<EncryptionHandshake>(), Arg.Any<Packet>())).Do(ci => ci.Arg<EncryptionHandshake>().Secrets = NetTestVectors.BuildSecretsWithSameIngressAndEgress());

            _logger = LimboLogs.Instance;
            _session.RemoteNodeId.Returns(NetTestVectors.StaticKeyB.PublicKey);
        }

        private readonly Packet _ackPacket = new Packet(NetTestVectors.AckEip8);
        private readonly Packet _authPacket = new Packet(NetTestVectors.AuthEip8);
        private IChannel _channel;
        private IChannelPipeline _pipeline;
        private IChannelHandlerContext _channelHandlerContext;
        private IHandshakeService _handshakeService;
        private ISession _session;
        private IMessageSerializationService _serializationService;
        private ILogManager _logger;
        private IEventExecutorGroup _group;
        
        [Test]
        public async Task Ignores_non_byte_buffer_input()
        {
            NettyHandshakeHandler handler = new NettyHandshakeHandler(_serializationService, _handshakeService, _session, HandshakeRole.Recipient, _logger, _group);
            handler.ChannelRead(_channelHandlerContext, new object());

            _handshakeService.Received(0).Ack(Arg.Any<EncryptionHandshake>(), Arg.Any<Packet>());
            await _channelHandlerContext.Received(0).WriteAndFlushAsync(Arg.Any<object>());
        }

        [Test]
        public void Initiator_adds_frame_encryption_codecs_to_pipeline_on_receiving_ack()
        {
            NettyHandshakeHandler handler = new NettyHandshakeHandler(_serializationService, _handshakeService, _session, HandshakeRole.Initiator, _logger, _group);
            handler.ChannelRead(_channelHandlerContext, Unpooled.Buffer(0, 0));

            _pipeline.Received(1).AddLast(Arg.Any<ZeroFrameEncoder>());
            _pipeline.Received(1).AddLast(Arg.Any<ZeroFrameDecoder>());
        }

        [Test]
        public void Initiator_adds_framing_codecs_to_pipeline_on_receiving_ack()
        {
            NettyHandshakeHandler handler = new NettyHandshakeHandler(_serializationService, _handshakeService, _session, HandshakeRole.Initiator, _logger, _group);
            handler.ChannelRead(_channelHandlerContext, Unpooled.Buffer(0, 0));

            _pipeline.Received(1).AddLast(Arg.Any<ZeroPacketSplitter>());
            _pipeline.Received(1).AddLast(Arg.Any<ZeroFrameMerger>());
        }

        [Test]
        public void Initiator_adds_p2p_handlers_to_pipeline_on_receiving_ack()
        {
            NettyHandshakeHandler handler = new NettyHandshakeHandler(_serializationService, _handshakeService, _session, HandshakeRole.Initiator, _logger, _group);
            handler.ChannelRead(_channelHandlerContext, Unpooled.Buffer(0, 0));

            _pipeline.Received(1).AddLast(_group, Arg.Any<ZeroNettyP2PHandler>());
            _pipeline.Received(1).AddLast(Arg.Any<PacketSender>());
        }

        [Test]
        public void Initiator_removes_itself_from_pipeline_on_receiving_ack()
        {
            NettyHandshakeHandler handler = new NettyHandshakeHandler(_serializationService, _handshakeService, _session, HandshakeRole.Initiator, _logger, _group);
            handler.ChannelRead(_channelHandlerContext, Unpooled.Buffer(0, 0));

            _pipeline.Received(1).Remove(handler);
        }

        [Test]
        public void Initiator_removes_length_field_based_decoder_from_pipeline_on_receiving_ack()
        {
            NettyHandshakeHandler handler = new NettyHandshakeHandler(_serializationService, _handshakeService, _session, HandshakeRole.Initiator, _logger, _group);
            handler.ChannelRead(_channelHandlerContext, Unpooled.Buffer(0, 0));

            _pipeline.Received(1).Remove<LengthFieldBasedFrameDecoder>();
        }

        [Test]
        public async Task Initiator_sends_auth_on_channel_activation()
        {
            NettyHandshakeHandler handler = new NettyHandshakeHandler(_serializationService, _handshakeService, _session, HandshakeRole.Initiator, _logger, _group);
            handler.ChannelActive(_channelHandlerContext);

            _handshakeService.Received(1).Auth(_session.RemoteNodeId, Arg.Any<EncryptionHandshake>());
            await _channelHandlerContext.Received(1).WriteAndFlushAsync(Arg.Is<IByteBuffer>(b => Bytes.AreEqual(b.Array.Slice(b.ArrayOffset, NetTestVectors.AuthEip8.Length), NetTestVectors.AuthEip8)));
        }

        [Test]
        public void Recipient_adds_frame_encryption_codecs_to_pipeline_on_sending_ack()
        {
            NettyHandshakeHandler handler = new NettyHandshakeHandler(_serializationService, _handshakeService, _session, HandshakeRole.Recipient, _logger, _group);
            handler.ChannelRead(_channelHandlerContext, Unpooled.Buffer(0, 0));

            _pipeline.Received(1).AddLast(Arg.Any<ZeroFrameEncoder>());
            _pipeline.Received(1).AddLast(Arg.Any<ZeroFrameDecoder>());
        }

        [Test]
        public void Recipient_adds_framing_codecs_to_pipeline_on_sending_ack()
        {
            NettyHandshakeHandler handler = new NettyHandshakeHandler(_serializationService, _handshakeService, _session, HandshakeRole.Recipient, _logger, _group);
            handler.ChannelRead(_channelHandlerContext, Unpooled.Buffer(0, 0));

            _pipeline.Received(1).AddLast(Arg.Any<ZeroPacketSplitter>());
            _pipeline.Received(1).AddLast(Arg.Any<ZeroFrameMerger>());
        }

        [Test]
        public void Recipient_adds_p2p_handlers_to_pipeline_on_sending_ack()
        {
            NettyHandshakeHandler handler = new NettyHandshakeHandler(_serializationService, _handshakeService, _session, HandshakeRole.Recipient, _logger, _group);
            handler.ChannelRead(_channelHandlerContext, Unpooled.Buffer(0, 0));

            _pipeline.Received(1).AddLast(_group, Arg.Any<ZeroNettyP2PHandler>());
            _pipeline.Received(1).AddLast(Arg.Any<PacketSender>());
        }

        [Test]
        public async Task Recipient_does_not_send_anything_on_channel_activation()
        {
            NettyHandshakeHandler handler = new NettyHandshakeHandler(_serializationService, _handshakeService, _session, HandshakeRole.Recipient, _logger, _group);
            handler.ChannelActive(_channelHandlerContext);

            _handshakeService.Received(0).Auth(Arg.Any<PublicKey>(), Arg.Any<EncryptionHandshake>());
            await _channelHandlerContext.Received(0).WriteAndFlushAsync(Arg.Any<object>());
        }

        [Test]
        public void Recipient_removes_itself_from_pipeline_on_sending_ack()
        {
            NettyHandshakeHandler handler = new NettyHandshakeHandler(_serializationService, _handshakeService, _session, HandshakeRole.Recipient, _logger, _group);
            handler.ChannelRead(_channelHandlerContext, Unpooled.Buffer(0, 0));

            _pipeline.Received(1).Remove(handler);
        }

        [Test]
        public void Recipient_removes_length_field_based_decoder_from_pipeline_on_sending_ack()
        {
            NettyHandshakeHandler handler = new NettyHandshakeHandler(_serializationService, _handshakeService, _session, HandshakeRole.Recipient, _logger, _group);
            handler.ChannelRead(_channelHandlerContext, Unpooled.Buffer(0, 0));

            _pipeline.Received(1).Remove<LengthFieldBasedFrameDecoder>();
        }

        [Test]
        public async Task Recipient_sends_ack_on_receiving_auth()
        {
            NettyHandshakeHandler handler = new NettyHandshakeHandler(_serializationService, _handshakeService, _session, HandshakeRole.Recipient, _logger, _group);
            handler.ChannelRead(_channelHandlerContext, Unpooled.Buffer(0, 0));

            _handshakeService.Received(1).Ack(Arg.Any<EncryptionHandshake>(), Arg.Any<Packet>());
            await _channelHandlerContext.Received(1).WriteAndFlushAsync(Arg.Is<IByteBuffer>(b => Bytes.AreEqual(b.Array.Slice(b.ArrayOffset, NetTestVectors.AckEip8.Length), NetTestVectors.AckEip8)));
        }
    }
}
