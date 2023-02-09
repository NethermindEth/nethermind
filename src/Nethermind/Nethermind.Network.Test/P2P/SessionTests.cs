// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class SessionTests
    {
        private IChannel _channel;
        private IChannelHandlerContext _channelHandlerContext;
        private IPacketSender _packetSender;
        private IChannelPipeline _pipeline;

        [SetUp]
        public void SetUp()
        {
            _channel = Substitute.For<IChannel>();
            _channelHandlerContext = Substitute.For<IChannelHandlerContext>();
            _pipeline = Substitute.For<IChannelPipeline>();
            _channelHandlerContext.Channel.Returns(_channel);
            _channel.Pipeline.Returns(_pipeline);
            _pipeline.Get<ZeroPacketSplitter>().Returns(new ZeroPacketSplitter(LimboLogs.Instance));
            _packetSender = Substitute.For<IPacketSender>();
        }

        [Test]
        public void Constructor_sets_the_values()
        {
            Session session = new(30312, new Node(TestItem.PublicKeyB, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            Assert.AreEqual(TestItem.PublicKeyB, session.RemoteNodeId);
            Assert.AreEqual(30312, session.LocalPort);
            Assert.AreEqual(ConnectionDirection.Out, session.Direction);
            Assert.AreNotEqual(default(Guid), session.SessionId);
        }

        [Test]
        public void Can_set_remaining_properties()
        {
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            session.PingSender = Substitute.For<IPingSender>();
            Assert.NotNull(session.PingSender);
            session.ObsoleteRemoteNodeId = TestItem.PublicKeyC;
            Assert.NotNull(session.ObsoleteRemoteNodeId);
        }

        [Test]
        public void Node_can_be_retrieved_only_after_remote_known()
        {
            Session session = new(30312, _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            Assert.Throws<InvalidOperationException>(() =>
            {
                var node = session.Node;
            });

            session.Handshake(TestItem.PublicKeyA);
            session.RemoteHost = "127.0.0.1";
            session.RemotePort = 30000;
            Assert.NotNull(session.Node);
        }

        [Test]
        public void Throws_when_init_called_before_the_handshake()
        {
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            Assert.Throws<InvalidOperationException>(() => session.Init(5, _channelHandlerContext, _packetSender));
        }

        [Test]
        public void Raises_event_on_init()
        {
            bool wasCalled = false;
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            session.Initialized += (s, e) => wasCalled = true;

            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            Assert.AreEqual(5, session.P2PVersion);
            Assert.True(wasCalled);
        }

        [Test]
        public void Sets_p2p_version_on_init()
        {
            Session session = new(30312, _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            session.Handshake(TestItem.PublicKeyB);
            session.Init(4, _channelHandlerContext, _packetSender);
            Assert.AreEqual(4, session.P2PVersion);
            Assert.AreEqual(TestItem.PublicKeyB, session.RemoteNodeId);
        }

        [Test]
        public void Raises_event_on_handshake()
        {
            bool wasCalled = false;
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            session.HandshakeComplete += (s, e) => wasCalled = true;

            session.Handshake(TestItem.PublicKeyA);
            Assert.True(wasCalled);
        }

        [Test]
        public void Cannot_handshake_twice()
        {
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            session.Handshake(TestItem.PublicKeyA);
            Assert.Throws<InvalidOperationException>(() => session.Handshake(TestItem.PublicKeyA));
        }

        [Test]
        public void Cannot_handshake_after_init()
        {
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            Assert.Throws<InvalidOperationException>(() => session.Handshake(TestItem.PublicKeyA));
        }

        [Test]
        public void Cannot_enable_snappy_before_init()
        {
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            Assert.Throws<InvalidOperationException>(() => session.EnableSnappy());
            session.Handshake(TestItem.PublicKeyA);
            Assert.Throws<InvalidOperationException>(() => session.EnableSnappy());
            session.Init(5, _channelHandlerContext, _packetSender);
        }

        [Test]
        public void Can_enable_snappy()
        {
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            ZeroNettyP2PHandler handler = new(session, LimboLogs.Instance);
            _pipeline.Get<ZeroNettyP2PHandler>().Returns(handler);
            Assert.False(handler.SnappyEnabled);
            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            session.EnableSnappy();
            Assert.True(handler.SnappyEnabled);
            _pipeline.Received().Get<ZeroPacketSplitter>();
            _pipeline.Received().AddBefore(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ZeroSnappyEncoder>());
        }

        [Test]
        public void Enabling_snappy_on_disconnected_will_not_cause_trouble()
        {
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            session.MarkDisconnected(DisconnectReason.Other, DisconnectType.Remote, "test");
            session.EnableSnappy();
        }

        [Test]
        public async Task Adding_protocols_when_disconnecting_will_not_cause_trouble()
        {
            bool shouldStop = false;
            int i = 0;
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            Action addProtocol = () =>
            {
                IProtocolHandler required = Substitute.For<IProtocolHandler>();
                required.ProtocolCode.Returns("p2p");
                required.MessageIdSpaceSize.Returns(16);
                session.AddProtocolHandler(required);
                while (!shouldStop)
                {
                    IProtocolHandler protocolHandler = Substitute.For<IProtocolHandler>();
                    protocolHandler.ProtocolCode.Returns("aa" + i++);
                    protocolHandler.MessageIdSpaceSize.Returns(10);
                    session.AddProtocolHandler(protocolHandler);
                }
            };

            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            Task task = new(addProtocol);
            task.Start();

            await Task.Delay(20);
            session.InitiateDisconnect(InitiateDisconnectReason.Other, "test");
            await Task.Delay(10);
            shouldStop = true;
        }

        [Test]
        public void Cannot_init_before_the_handshake()
        {
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            Assert.Throws<InvalidOperationException>(() => session.Init(5, _channelHandlerContext, _packetSender));
        }

        [Test]
        public void Is_closing_is_false_when_not_closing()
        {
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            Assert.False(session.IsClosing);
            session.Handshake(TestItem.PublicKeyA);
            Assert.False(session.IsClosing);
            session.Init(5, _channelHandlerContext, _packetSender);
            Assert.False(session.IsClosing);
        }

        [Test]
        public void Best_state_reached_is_correct()
        {
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            Assert.AreEqual(SessionState.New, session.BestStateReached);
            session.Handshake(TestItem.PublicKeyA);
            Assert.AreEqual(SessionState.HandshakeComplete, session.BestStateReached);
            session.Init(5, _channelHandlerContext, _packetSender);
            Assert.AreEqual(SessionState.Initialized, session.BestStateReached);
        }

        [Test]
        public void Cannot_dispose_unless_disconnected()
        {
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            session.Handshake(TestItem.PublicKeyA);
            Assert.Throws<InvalidOperationException>(() => session.Dispose());
            session.Init(5, _channelHandlerContext, _packetSender);
            Assert.Throws<InvalidOperationException>(() => session.Dispose());

            IProtocolHandler p2p = BuildHandler("p2p", 10);
            IProtocolHandler aaa = BuildHandler("aaa", 10);
            IProtocolHandler bbb = BuildHandler("bbb", 5);
            IProtocolHandler ccc = BuildHandler("ccc", 1);
            session.AddProtocolHandler(p2p);
            session.AddProtocolHandler(aaa);
            session.AddProtocolHandler(bbb);
            session.AddProtocolHandler(ccc);

            session.InitiateDisconnect(InitiateDisconnectReason.Other, "test");
            session.Dispose();

            aaa.Received().DisconnectProtocol(DisconnectReason.Other, "test");
            bbb.Received().DisconnectProtocol(DisconnectReason.Other, "test");
            ccc.Received().DisconnectProtocol(DisconnectReason.Other, "test");

            aaa.Received().Dispose();
            bbb.Received().Dispose();
            ccc.Received().Dispose();
        }

        [Test]
        public void Raises_event_on_disconnecting()
        {
            bool wasCalled = false;
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            session.Disconnecting += (s, e) => wasCalled = true;

            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            session.InitiateDisconnect(InitiateDisconnectReason.Other);
            Assert.True(wasCalled);
        }

        [Test]
        public void Raises_event_on_disconnected()
        {
            bool wasCalled = false;
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            session.Disconnected += (s, e) => wasCalled = true;

            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            session.MarkDisconnected(DisconnectReason.Other, DisconnectType.Local, "test");
            Assert.True(wasCalled);
        }

        [Test]
        public void Disconnects_after_initiating_disconnect()
        {
            bool wasCalled = false;
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            session.Disconnecting += (s, e) => wasCalled = true;

            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            session.InitiateDisconnect(InitiateDisconnectReason.Other);
            Assert.True(wasCalled);
            Assert.True(session.IsClosing);
        }

        [Test]
        public void Do_not_disconnects_after_initiating_disconnect_on_static_node()
        {
            bool wasCalled = false;
            Node node = new Node(TestItem.PublicKeyA, "127.0.0.1", 8545);
            node.IsStatic = true;
            Session session = new(30312, node, _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            session.Disconnecting += (s, e) => wasCalled = true;

            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            session.InitiateDisconnect(InitiateDisconnectReason.TooManyPeers);
            Assert.False(wasCalled);
            Assert.False(session.IsClosing);
        }

        [Test]
        public void Error_on_channel_when_disconnecting_channels_does_not_prevent_the_event()
        {
            bool wasCalled = false;
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            _channel.DisconnectAsync().Returns(Task.FromException<Exception>(new Exception()));
            session.Disconnected += (s, e) => wasCalled = true;
            session.MarkDisconnected(DisconnectReason.Other, DisconnectType.Local, "test");
            Assert.True(wasCalled);
        }

        [Test]
        public void Error_on_context_when_disconnecting_channels_does_not_prevent_the_event()
        {
            bool wasCalled = false;
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            _channelHandlerContext.DisconnectAsync().Returns(Task.FromException<Exception>(new Exception()));
            session.Disconnected += (s, e) => wasCalled = true;
            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            session.MarkDisconnected(DisconnectReason.Other, DisconnectType.Local, "test");
            Assert.True(wasCalled);
        }

        [Test]
        public void Can_disconnect_many_times()
        {
            int wasCalledTimes = 0;
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            session.Disconnecting += (s, e) => wasCalledTimes++;

            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            session.InitiateDisconnect(InitiateDisconnectReason.Other);
            session.InitiateDisconnect(InitiateDisconnectReason.Other);
            session.MarkDisconnected(DisconnectReason.Other, DisconnectType.Local, "test");
            session.MarkDisconnected(DisconnectReason.Other, DisconnectType.Remote, "test");
            Assert.AreEqual(1, wasCalledTimes);
        }

        [Test]
        public void Can_disconnect_before_init()
        {
            int wasCalledTimes = 0;
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            session.Disconnecting += (s, e) => wasCalledTimes++;
            session.Handshake(TestItem.PublicKeyA);
            session.MarkDisconnected(DisconnectReason.Other, DisconnectType.Remote, "test");
            session.Init(5, _channelHandlerContext, _packetSender);
            Assert.AreEqual(1, wasCalledTimes);
        }

        [Test]
        public void On_incoming_sessions_can_fill_remote_id_on_handshake()
        {
            Session session = new(30312, _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            session.Handshake(TestItem.PublicKeyB);
            Assert.AreEqual(TestItem.PublicKeyB, session.RemoteNodeId);
        }

        [Test]
        public void Checks_init_arguments()
        {
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            session.Handshake(TestItem.PublicKeyA);
            Assert.Throws<ArgumentNullException>(() => session.Init(5, null, _packetSender), "context");
            Assert.Throws<ArgumentNullException>(() => session.Init(5, _channelHandlerContext, null), "packageSender");
        }

        [Test]
        public void Can_add_and_disconnect_many_handlers()
        {
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            IProtocolHandler p2p = BuildHandler("p2p", 10);
            IProtocolHandler aaa = BuildHandler("aaa", 10);
            IProtocolHandler bbb = BuildHandler("bbb", 5);
            IProtocolHandler ccc = BuildHandler("ccc", 1);
            session.AddProtocolHandler(p2p);
            session.AddProtocolHandler(aaa);
            session.AddProtocolHandler(bbb);
            session.AddProtocolHandler(ccc);
            session.InitiateDisconnect(InitiateDisconnectReason.Other, "test");
            aaa.Received().DisconnectProtocol(DisconnectReason.Other, "test");
            bbb.Received().DisconnectProtocol(DisconnectReason.Other, "test");
            ccc.Received().DisconnectProtocol(DisconnectReason.Other, "test");
        }

        [Test]
        public void Cannot_add_handlers_before_p2p()
        {
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            IProtocolHandler aaa = BuildHandler("aaa", 10);
            Assert.Throws<InvalidOperationException>(() => session.AddProtocolHandler(aaa));
        }

        [Test]
        public void Cannot_add_handler_twice()
        {
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            IProtocolHandler p2p = BuildHandler("p2p", 10);
            IProtocolHandler p2pAgain = BuildHandler("p2p", 10);
            session.AddProtocolHandler(p2p);
            Assert.Throws<InvalidOperationException>(() => session.AddProtocolHandler(p2pAgain));
        }

        private IProtocolHandler BuildHandler(string code, int spaceSize)
        {
            IProtocolHandler handler = Substitute.For<IProtocolHandler>();
            handler.ProtocolCode.Returns(code);
            handler.MessageIdSpaceSize.Returns(spaceSize);
            return handler;
        }

        [Test]
        [NonParallelizable]
        public void Can_deliver_messages()
        {
            Metrics.P2PBytesSent = 0;

            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            IProtocolHandler p2p = BuildHandler("p2p", 10);
            IProtocolHandler aaa = BuildHandler("aaa", 10);
            IProtocolHandler bbb = BuildHandler("bbb", 5);
            IProtocolHandler ccc = BuildHandler("ccc", 1);
            session.AddProtocolHandler(p2p);
            session.AddProtocolHandler(aaa);
            session.AddProtocolHandler(bbb);
            session.AddProtocolHandler(ccc);

            _packetSender.Enqueue(PingMessage.Instance).Returns(10);
            session.DeliverMessage(PingMessage.Instance);
            _packetSender.Received().Enqueue(PingMessage.Instance);

            Metrics.P2PBytesSent.Should().Be(10);
        }

        [Test]
        public void Cannot_deliver_before_initialized()
        {
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            Assert.Throws<InvalidOperationException>(() => session.DeliverMessage(PingMessage.Instance));
            session.Handshake(TestItem.PublicKeyA);
            Assert.Throws<InvalidOperationException>(() => session.DeliverMessage(PingMessage.Instance));
            session.Init(5, _channelHandlerContext, _packetSender);
            IProtocolHandler p2p = BuildHandler("p2p", 10);
            session.AddProtocolHandler(p2p);
        }

        [Test]
        public void Cannot_receive_before_initialized()
        {
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            Assert.Throws<InvalidOperationException>(() => session.ReceiveMessage(new Packet("p2p", 1, Array.Empty<byte>())));
            session.Handshake(TestItem.PublicKeyA);
            Assert.Throws<InvalidOperationException>(() => session.ReceiveMessage(new Packet("p2p", 1, Array.Empty<byte>())));
            session.Init(5, _channelHandlerContext, _packetSender);
            IProtocolHandler p2p = BuildHandler("p2p", 10);
            session.AddProtocolHandler(p2p);
        }

        [Test]
        public void Stops_delivering_messages_after_disconnect()
        {
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            IProtocolHandler p2p = BuildHandler("p2p", 10);
            session.AddProtocolHandler(p2p);

            session.InitiateDisconnect(InitiateDisconnectReason.Other);

            session.DeliverMessage(PingMessage.Instance);
            _packetSender.DidNotReceive().Enqueue(Arg.Any<PingMessage>());
        }

        [Test]
        public void Stops_receiving_messages_after_disconnect()
        {
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            IProtocolHandler p2p = BuildHandler("p2p", 10);
            session.AddProtocolHandler(p2p);

            session.InitiateDisconnect(InitiateDisconnectReason.Other);

            session.ReceiveMessage(new Packet("p2p", 3, Array.Empty<byte>()));
            p2p.DidNotReceive().HandleMessage(Arg.Is<Packet>(p => p.Protocol == "p2p" && p.PacketType == 3));
        }

        [Test, Retry(3)]
        [Parallelizable(ParallelScope.None)] // It touches global metrics
        public void Can_receive_messages()
        {
            Metrics.P2PBytesReceived = 0;

            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            IProtocolHandler p2p = BuildHandler("p2p", 10);
            IProtocolHandler aaa = BuildHandler("aaa", 10);
            IProtocolHandler bbb = BuildHandler("bbb", 5);
            IProtocolHandler ccc = BuildHandler("ccc", 1);
            session.AddProtocolHandler(p2p);
            session.AddProtocolHandler(aaa);
            session.AddProtocolHandler(bbb);
            session.AddProtocolHandler(ccc);

            byte[] data = new byte[10];
            session.ReceiveMessage(new Packet("---", 3, data));
            p2p.Received().HandleMessage(Arg.Is<Packet>(p => p.Protocol == "p2p" && p.PacketType == 3));

            session.ReceiveMessage(new Packet("---", 11, data));
            aaa.Received().HandleMessage(Arg.Is<Packet>(p => p.Protocol == "aaa" && p.PacketType == 1));

            session.ReceiveMessage(new Packet("---", 21, data));
            bbb.Received().HandleMessage(Arg.Is<Packet>(p => p.Protocol == "bbb" && p.PacketType == 1));

            session.ReceiveMessage(new Packet("---", 25, data));
            ccc.Received().HandleMessage(Arg.Is<Packet>(p => p.Protocol == "ccc" && p.PacketType == 0));

            session.ReceiveMessage(new Packet("---", 100, data));

            Metrics.P2PBytesReceived.Should().Be(data.Length * 5);
        }

        [Test]
        public void Updates_local_and_remote_metrics_on_disconnects()
        {
            Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, new MetricsDisconnectsAnalyzer(), LimboLogs.Instance);
            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            IProtocolHandler p2p = BuildHandler("p2p", 10);
            session.AddProtocolHandler(p2p);

            long beforeLocal = Network.Metrics.LocalOtherDisconnects;
            long beforeRemote = Network.Metrics.OtherDisconnects;
            session.MarkDisconnected(DisconnectReason.Other, DisconnectType.Local, string.Empty);
            long afterLocal = Network.Metrics.LocalOtherDisconnects;
            long afterRemote = Network.Metrics.OtherDisconnects;
            Assert.AreEqual(beforeLocal + 1, afterLocal);
            Assert.AreEqual(beforeRemote, afterRemote);

            session = new Session(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _channel, new MetricsDisconnectsAnalyzer(), LimboLogs.Instance);
            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            p2p = BuildHandler("p2p", 10);
            session.AddProtocolHandler(p2p);

            beforeLocal = Network.Metrics.LocalOtherDisconnects;
            beforeRemote = Network.Metrics.OtherDisconnects;
            session.MarkDisconnected(DisconnectReason.Other, DisconnectType.Remote, string.Empty);
            afterLocal = Network.Metrics.LocalOtherDisconnects;
            afterRemote = Network.Metrics.OtherDisconnects;
            Assert.AreEqual(beforeLocal, afterLocal);
            Assert.AreEqual(beforeRemote + 1, afterRemote);
        }
    }
}
