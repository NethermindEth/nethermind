// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Common.Concurrency;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Embedded;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Stats.Model;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx.Handshake
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class NettyHandshakeHandlerTests
    {
        [Test, CancelAfter(10000)]
        public async Task Closed_session_receipts_are_not_retained_by_the_live_executor(CancellationToken cancellationToken)
        {
            try
            {
                (WeakReference tracker, Task drained) = CreateAndCloseReceiptSession();
                await drained.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                for (int i = 0; i < 3 && tracker.IsAlive; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(_group.TerminationCompletion.IsCompleted, Is.False);
                    Assert.That(tracker.IsAlive, Is.False);
                }
            }
            finally
            {
                await _group.ShutdownGracefullyAsync(TimeSpan.Zero, TimeSpan.FromSeconds(1));
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private (WeakReference Tracker, Task Drained) CreateAndCloseReceiptSession()
        {
            EmbeddedChannel channel = new();
            Session session = new(30303, new Node(NetTestVectors.StaticKeyB.PublicKey, "127.0.0.1", 30303),
                channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            IHandshakeService handshakeService = Substitute.For<IHandshakeService>();
            handshakeService.When(x => x.Agree(Arg.Any<EncryptionHandshake>(), Arg.Any<Packet>())).Do(call =>
            {
                EncryptionHandshake handshake = call.Arg<EncryptionHandshake>();
                handshake.RemoteNodeId = NetTestVectors.StaticKeyB.PublicKey;
                handshake.Secrets = NetTestVectors.BuildSecretsWithSameIngressAndEgress();
            });
            NettyHandshakeHandler handler = new(_serializationService, handshakeService, session,
                HandshakeRole.Initiator, _logger, _group, TimeSpan.Zero);
            channel.Pipeline.AddLast(new OneTimeLengthFieldBasedFrameDecoder());
            channel.Pipeline.AddLast(handler);
            WeakReference tracker = new(session.InboundReceipts);
            try
            {
                handler.ChannelRead(channel.Pipeline.Context(handler), Unpooled.Buffer(0, 0));
                Assert.That(session.State, Is.EqualTo(SessionState.Initialized));
            }
            finally
            {
                channel.FinishAndReleaseAll();
                session.MarkDisconnected(DisconnectReason.ClientQuitting, DisconnectType.Remote, "test completed");
                session.Dispose();
            }
            System.Threading.Tasks.TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _group.Execute(() => drained.SetResult());
            return (tracker, drained.Task);
        }

        [SetUp]
        public void Setup()
        {
            _session = Substitute.For<ISession>();
            _session.InboundReceipts.Returns(PacketReceiptTracker.Closed);

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
            _handshakeService.Ack(Arg.Any<EncryptionHandshake>(), Arg.Any<Packet>()).Returns(_ackPacket).AndDoes(static ci => ci.Arg<EncryptionHandshake>().Secrets = NetTestVectors.BuildSecretsWithSameIngressAndEgress());
            _handshakeService.When(static s => s.Agree(Arg.Any<EncryptionHandshake>(), Arg.Any<Packet>())).Do(static ci => ci.Arg<EncryptionHandshake>().Secrets = NetTestVectors.BuildSecretsWithSameIngressAndEgress());

            _logger = LimboLogs.Instance;
            _session.RemoteNodeId.Returns(NetTestVectors.StaticKeyB.PublicKey);
        }

        [TearDown]
        public void TearDown() => _session?.Dispose();

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
            if (handshakeRole == HandshakeRole.Recipient)
            {
                _session.Node.Throws(new InvalidOperationException("property throw on incoming connection before handshake"));
                _session.RemoteNodeId.Returns((PublicKey)null); // Incoming connection have null remote node id until handshake finished
            }
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

            _pipeline.DidNotReceive().AddLast(Arg.Any<ZeroFrameEncoder>());
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

            Assert.That(received, Is.True);
            _handshakeService.Received(1).Auth(_session.RemoteNodeId, Arg.Any<EncryptionHandshake>());

        }

        [Test]
        public void Recipient_adds_frame_encryption_codecs_to_pipeline_on_sending_ack()
        {
            NettyHandshakeHandler handler = CreateHandler();
            handler.ChannelRead(_channelHandlerContext, Unpooled.Buffer(0, 0));

            _pipeline.DidNotReceive().AddLast(Arg.Any<ZeroFrameEncoder>());
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

            Assert.That(received, Is.True);
            _handshakeService.Received(1).Ack(Arg.Any<EncryptionHandshake>(), Arg.Any<Packet>());
        }

        [Test]
        public async Task Handler_disconnect_on_exception()
        {
            NettyHandshakeHandler handler = CreateHandler();

            IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
            handler.ExceptionCaught(context, new Exception("any exception"));

            await context.Received().DisconnectAsync();
        }
    }
}
