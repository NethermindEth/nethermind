// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Common.Concurrency;
using DotNetty.Transport.Channels;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
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

        private readonly Packet _ackPacket = new(NetTestVectors.AckEip8);
        private readonly Packet _authPacket = new(NetTestVectors.AuthEip8);
        private IChannel _channel;
        private IChannelPipeline _pipeline;
        private IChannelHandlerContext _channelHandlerContext;
        private IHandshakeService _handshakeService;
        private ISession _session;
        private IMessageSerializationService _serializationService;
        private ILogManager _logger;
        private IEventExecutorGroup _group;

        private NettyHandshakeHandler CreateHandler(HandshakeRole handshakeRole = HandshakeRole.Recipient)
        {
            return new NettyHandshakeHandler(_serializationService, _handshakeService, _session, handshakeRole, _logger, _group, TimeSpan.Zero);
        }

        [Test]
        public async Task Ignores_non_byte_buffer_input()
        {
            NettyHandshakeHandler handler = CreateHandler();
            handler.ChannelRead(_channelHandlerContext, new object());

            _handshakeService.Received(0).Ack(Arg.Any<EncryptionHandshake>(), Arg.Any<Packet>());
            await _channelHandlerContext.Received(0).WriteAndFlushAsync(Arg.Any<object>());
        }

        [Test]
        public void Initiator_adds_frame_encryption_codecs_to_pipeline_on_receiving_ack()
        {
            NettyHandshakeHandler handler = CreateHandler(HandshakeRole.Initiator);
            handler.ChannelRead(_channelHandlerContext, Unpooled.Buffer(0, 0));

            _pipeline.Received(1).AddLast(Arg.Any<ZeroFrameEncoder>());
            _pipeline.Received(1).AddLast(Arg.Any<ZeroFrameDecoder>());
        }

        [Test]
        public void Initiator_adds_framing_codecs_to_pipeline_on_receiving_ack()
        {
            NettyHandshakeHandler handler = CreateHandler(HandshakeRole.Initiator);
            handler.ChannelRead(_channelHandlerContext, Unpooled.Buffer(0, 0));

            _pipeline.Received(1).AddLast(Arg.Any<ZeroPacketSplitter>());
            _pipeline.Received(1).AddLast(Arg.Any<ZeroFrameMerger>());
        }

        [Test]
        public void Initiator_adds_p2p_handlers_to_pipeline_on_receiving_ack()
        {
            NettyHandshakeHandler handler = CreateHandler(HandshakeRole.Initiator);
            handler.ChannelRead(_channelHandlerContext, Unpooled.Buffer(0, 0));

            _pipeline.Received(1).AddLast(_group, Arg.Any<ZeroNettyP2PHandler>());
            _pipeline.Received(1).AddLast(Arg.Any<PacketSender>());
        }

        [Test]
        public void Initiator_removes_itself_from_pipeline_on_receiving_ack()
        {
            NettyHandshakeHandler handler = CreateHandler(HandshakeRole.Initiator);
            handler.ChannelRead(_channelHandlerContext, Unpooled.Buffer(0, 0));

            _pipeline.Received(1).Remove(handler);
        }

        [Test]
        public void Initiator_removes_length_field_based_decoder_from_pipeline_on_receiving_ack()
        {
            NettyHandshakeHandler handler = CreateHandler(HandshakeRole.Initiator);
            handler.ChannelRead(_channelHandlerContext, Unpooled.Buffer(0, 0));

            _pipeline.Received(1).Remove<OneTimeLengthFieldBasedFrameDecoder>();
        }

        [Test]
        public void Initiator_sends_auth_on_channel_activation()
        {
            NettyHandshakeHandler handler = CreateHandler(HandshakeRole.Initiator);
            bool received = false;
            _channelHandlerContext.Allocator.Returns(UnpooledByteBufferAllocator.Default);
            _channelHandlerContext.When(x => x.WriteAndFlushAsync(Arg.Is<IByteBuffer>(b => Bytes.AreEqual(b.Array.Slice(b.ArrayOffset, NetTestVectors.AuthEip8.Length), NetTestVectors.AuthEip8))))
                .Do(c => received = true);
            handler.ChannelActive(_channelHandlerContext);

            received.Should().BeTrue();
            _handshakeService.Received(1).Auth(_session.RemoteNodeId, Arg.Any<EncryptionHandshake>());

        }

        [Test]
        public void Recipient_adds_frame_encryption_codecs_to_pipeline_on_sending_ack()
        {
            NettyHandshakeHandler handler = CreateHandler();
            handler.ChannelRead(_channelHandlerContext, Unpooled.Buffer(0, 0));

            _pipeline.Received(1).AddLast(Arg.Any<ZeroFrameEncoder>());
            _pipeline.Received(1).AddLast(Arg.Any<ZeroFrameDecoder>());
        }

        [Test]
        public void Recipient_adds_framing_codecs_to_pipeline_on_sending_ack()
        {
            NettyHandshakeHandler handler = CreateHandler();
            handler.ChannelRead(_channelHandlerContext, Unpooled.Buffer(0, 0));

            _pipeline.Received(1).AddLast(Arg.Any<ZeroPacketSplitter>());
            _pipeline.Received(1).AddLast(Arg.Any<ZeroFrameMerger>());
        }

        [Test]
        public void Recipient_adds_p2p_handlers_to_pipeline_on_sending_ack()
        {
            NettyHandshakeHandler handler = CreateHandler();
            handler.ChannelRead(_channelHandlerContext, Unpooled.Buffer(0, 0));

            _pipeline.Received(1).AddLast(_group, Arg.Any<ZeroNettyP2PHandler>());
            _pipeline.Received(1).AddLast(Arg.Any<PacketSender>());
        }

        [Test]
        public async Task Recipient_does_not_send_anything_on_channel_activation()
        {
            NettyHandshakeHandler handler = CreateHandler();
            handler.ChannelActive(_channelHandlerContext);

            _handshakeService.Received(0).Auth(Arg.Any<PublicKey>(), Arg.Any<EncryptionHandshake>());
            await _channelHandlerContext.Received(0).WriteAndFlushAsync(Arg.Any<object>());
        }

        [Test]
        public void Recipient_removes_itself_from_pipeline_on_sending_ack()
        {
            NettyHandshakeHandler handler = CreateHandler();
            handler.ChannelRead(_channelHandlerContext, Unpooled.Buffer(0, 0));

            _pipeline.Received(1).Remove(handler);
        }

        [Test]
        public void Recipient_removes_length_field_based_decoder_from_pipeline_on_sending_ack()
        {
            NettyHandshakeHandler handler = CreateHandler();
            handler.ChannelRead(_channelHandlerContext, Unpooled.Buffer(0, 0));

            _pipeline.Received(1).Remove<OneTimeLengthFieldBasedFrameDecoder>();
        }

        [Test]
        public void Recipient_sends_ack_on_receiving_auth()
        {
            bool received = false;
            NettyHandshakeHandler handler = CreateHandler();
            _channelHandlerContext.Allocator.Returns(UnpooledByteBufferAllocator.Default);
            _channelHandlerContext.When(x => x.WriteAndFlushAsync(Arg.Is<IByteBuffer>(b => Bytes.AreEqual(b.Array.Slice(b.ArrayOffset, NetTestVectors.AckEip8.Length), NetTestVectors.AckEip8))))
                .Do(_ => received = true);
            handler.ChannelRead(_channelHandlerContext, Unpooled.Buffer(0, 0));

            received.Should().BeTrue();
            _handshakeService.Received(1).Ack(Arg.Any<EncryptionHandshake>(), Arg.Any<Packet>());
        }
    }
}
