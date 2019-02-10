/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.Rlpx;
using Nethermind.Stats.Model;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    [TestFixture]
    public class P2PSessionTests
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
            _pipeline.Get<NettyPacketSplitter>().Returns(new NettyPacketSplitter());
            _packetSender = Substitute.For<IPacketSender>();
        }

        [Test]
        public void Constructor_sets_the_values()
        {
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
            Assert.AreEqual(TestItem.PublicKeyB, session.RemoteNodeId);
            Assert.AreEqual(30312, session.LocalPort);
            Assert.AreEqual(ConnectionDirection.Out, session.Direction);
            Assert.AreNotEqual(default(Guid), session.SessionId);
        }
        
        [Test]
        public void Can_set_remaining_properties()
        {
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
            session.Node = new Node("127.0.0.1", 8545);
            session.PingSender = Substitute.For<IPingSender>();
            Assert.NotNull(session.PingSender);
            session.ObsoleteRemoteNodeId = TestItem.PublicKeyC;
            Assert.NotNull(session.ObsoleteRemoteNodeId);
        }

        [Test]
        public void Node_can_be_retrieved_only_after_remote_known()
        {
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
            Assert.Throws<InvalidOperationException>(() =>
            {
                var node = session.Node;
            });
            session.RemoteHost = "127.0.0.1";
            session.RemotePort = 30000;
            Assert.NotNull(session.Node);
        }

        [Test]
        public void Throws_when_init_called_before_the_handshake()
        {
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
            Assert.Throws<InvalidOperationException>(() => session.Init(5, _channelHandlerContext, _packetSender));
        }

        [Test]
        public void Raises_event_on_init()
        {
            bool wasCalled = false;
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
            session.Initialized += (s, e) => wasCalled = true;

            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            Assert.AreEqual(5, session.P2PVersion);
            Assert.True(wasCalled);
        }
        
        [Test]
        public void Sets_p2p_version_on_init()
        {
            P2PSession session = new P2PSession(null, 30312, ConnectionDirection.In, LimboLogs.Instance, _channel);
            session.Handshake(TestItem.PublicKeyB);
            session.Init(4, _channelHandlerContext, _packetSender);
            Assert.AreEqual(4, session.P2PVersion);
            Assert.AreEqual(TestItem.PublicKeyB, session.RemoteNodeId);
        }

        [Test]
        public void Raises_event_on_handshake()
        {
            bool wasCalled = false;
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
            session.HandshakeComplete += (s, e) => wasCalled = true;

            session.Handshake(TestItem.PublicKeyA);
            Assert.True(wasCalled);
        }

        [Test]
        public void Cannot_handshake_twice()
        {
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
            session.Handshake(TestItem.PublicKeyA);
            Assert.Throws<InvalidOperationException>(() => session.Handshake(TestItem.PublicKeyA));
        }

        [Test]
        public void Cannot_handshake_after_init()
        {
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            Assert.Throws<InvalidOperationException>(() => session.Handshake(TestItem.PublicKeyA));
        }

        [Test]
        public void Cannot_enable_snappy_before_init()
        {
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
            Assert.Throws<InvalidOperationException>(() => session.EnableSnappy());
            session.Handshake(TestItem.PublicKeyA);
            Assert.Throws<InvalidOperationException>(() => session.EnableSnappy());
            session.Init(5, _channelHandlerContext, _packetSender);
        }

        [Test]
        public void Can_enable_snappy()
        {
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            session.EnableSnappy();
            _pipeline.Received().Get<NettyPacketSplitter>();
            _pipeline.Received().AddBefore(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SnappyDecoder>());
            _pipeline.Received().AddBefore(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SnappyEncoder>());
        }

        [Test]
        public void Enabling_snappy_on_disconnected_will_not_cause_trouble()
        {
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            session.Disconnect(DisconnectReason.Other, DisconnectType.Remote);
            session.EnableSnappy();
        }

        [Test]
        public void Cannot_init_before_the_handshake()
        {
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
            Assert.Throws<InvalidOperationException>(() => session.Init(5, _channelHandlerContext, _packetSender));
        }

        [Test]
        public void Is_closing_is_false_when_not_closing()
        {
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
            Assert.False(session.IsClosing);
            session.Handshake(TestItem.PublicKeyA);
            Assert.False(session.IsClosing);
            session.Init(5, _channelHandlerContext, _packetSender);
            Assert.False(session.IsClosing);
        }

        [Test]
        public void Cannot_dispose_unless_disconnected()
        {
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
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

            session.InitiateDisconnect(DisconnectReason.ClientQuitting);
            session.Dispose();

            aaa.Received().Disconnect(DisconnectReason.ClientQuitting);
            bbb.Received().Disconnect(DisconnectReason.ClientQuitting);
            ccc.Received().Disconnect(DisconnectReason.ClientQuitting);

            aaa.Received().Dispose();
            bbb.Received().Dispose();
            ccc.Received().Dispose();
        }

        [Test]
        public void Raises_event_on_disconnecting()
        {
            bool wasCalled = false;
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
            session.Disconnecting += (s, e) => wasCalled = true;

            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            session.InitiateDisconnect(DisconnectReason.Other);
            Assert.True(wasCalled);
        }

        [Test]
        public void Raises_event_on_disconnected()
        {
            bool wasCalled = false;
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
            session.Disconnected += (s, e) => wasCalled = true;

            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            session.Disconnect(DisconnectReason.Other, DisconnectType.Local);
            Assert.True(wasCalled);
        }

        [Test]
        public void Disconnects_after_initiating_disconnect()
        {
            bool wasCalled = false;
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
            session.Disconnecting += (s, e) => wasCalled = true;

            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            session.InitiateDisconnect(DisconnectReason.Other);
            Assert.True(wasCalled);
        }
        
        [Test]
        public void Error_on_channel_when_disconnecting_channels_does_not_prevent_the_event()
        {
            bool wasCalled = false;
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
            _channel.DisconnectAsync().Returns(Task.FromException<Exception>(new Exception()));
            session.Disconnected += (s, e) => wasCalled = true;
            session.Disconnect(DisconnectReason.Other, DisconnectType.Local);
            Assert.True(wasCalled);
        }
        
        [Test]
        public void Error_on_context_when_disconnecting_channels_does_not_prevent_the_event()
        {
            bool wasCalled = false;
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
            _channelHandlerContext.DisconnectAsync().Returns(Task.FromException<Exception>(new Exception()));
            session.Disconnected += (s, e) => wasCalled = true;
            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            session.Disconnect(DisconnectReason.Other, DisconnectType.Local);
            Assert.True(wasCalled);
        }

        [Test]
        public void Can_disconnect_many_times()
        {
            int wasCalledTimes = 0;
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
            session.Disconnecting += (s, e) => wasCalledTimes++;

            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            session.InitiateDisconnect(DisconnectReason.Other);
            session.InitiateDisconnect(DisconnectReason.Other);
            session.Disconnect(DisconnectReason.Other, DisconnectType.Local);
            session.Disconnect(DisconnectReason.Other, DisconnectType.Remote);
            Assert.AreEqual(1, wasCalledTimes);
        }

        [Test]
        public void Can_disconnect_before_init()
        {
            int wasCalledTimes = 0;
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
            session.Disconnecting += (s, e) => wasCalledTimes++;
            session.Handshake(TestItem.PublicKeyA);
            session.Disconnect(DisconnectReason.Other, DisconnectType.Remote);
            session.Init(5, _channelHandlerContext, _packetSender);
            Assert.AreEqual(1, wasCalledTimes);
        }
        
        [Test]
        public void On_incoming_sessions_can_fill_remote_id_on_handshake()
        {
            P2PSession session = new P2PSession(null, 30312, ConnectionDirection.In, LimboLogs.Instance, _channel);
            session.Handshake(TestItem.PublicKeyB);
            Assert.AreEqual(TestItem.PublicKeyB, session.RemoteNodeId);
        }

        [Test]
        public void Checks_init_arguments()
        {
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
            session.Handshake(TestItem.PublicKeyA);
            Assert.Throws<ArgumentNullException>(() => session.Init(5, null, _packetSender), "context");
            Assert.Throws<ArgumentNullException>(() => session.Init(5, _channelHandlerContext, null), "packageSender");
        }

        [Test]
        public void Can_add_and_disconnect_many_handlers()
        {
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
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
            session.InitiateDisconnect(DisconnectReason.ClientQuitting);
            aaa.Received().Disconnect(DisconnectReason.ClientQuitting);
            bbb.Received().Disconnect(DisconnectReason.ClientQuitting);
            ccc.Received().Disconnect(DisconnectReason.ClientQuitting);
        }

        [Test]
        public void Cannot_add_handlers_before_p2p()
        {
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            IProtocolHandler aaa = BuildHandler("aaa", 10);
            Assert.Throws<InvalidOperationException>(() => session.AddProtocolHandler(aaa));
        }

        [Test]
        public void Cannot_add_handler_twice()
        {
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
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
        public void Can_deliver_messages()
        {
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
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

            session.DeliverMessage(new Packet("p2p", 3, Bytes.Empty));
            _packetSender.Received().Enqueue(Arg.Is<Packet>(p => p.Protocol == "p2p" && p.PacketType == 3));

            session.DeliverMessage(new Packet("aaa", 1, Bytes.Empty));
            _packetSender.Received().Enqueue(Arg.Is<Packet>(p => p.Protocol == "aaa" && p.PacketType == 11));

            session.DeliverMessage(new Packet("bbb", 1, Bytes.Empty));
            _packetSender.Received().Enqueue(Arg.Is<Packet>(p => p.Protocol == "bbb" && p.PacketType == 21));

            session.DeliverMessage(new Packet("ccc", 0, Bytes.Empty));
            _packetSender.Received().Enqueue(Arg.Is<Packet>(p => p.Protocol == "ccc" && p.PacketType == 25));
        }

        [Test]
        public void Cannot_deliver_invalid_messages()
        {
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
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

            Assert.Throws<InvalidOperationException>(() => session.DeliverMessage(new Packet("p2p", 11, Bytes.Empty)), "p2p.11");
            Assert.Throws<InvalidOperationException>(() => session.DeliverMessage(new Packet("ccc", 100, Bytes.Empty)), "ccc.100");
            Assert.Throws<InvalidOperationException>(() => session.DeliverMessage(new Packet("ddd", 0, Bytes.Empty)), "ddd.0");
        }
        
        [Test]
        public void Cannot_deliver_before_initialized()
        {
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
            Assert.Throws<InvalidOperationException>(() => session.DeliverMessage(new Packet("p2p", 1, Bytes.Empty)));
            session.Handshake(TestItem.PublicKeyA);
            Assert.Throws<InvalidOperationException>(() => session.DeliverMessage(new Packet("p2p", 1, Bytes.Empty)));
            session.Init(5, _channelHandlerContext, _packetSender);
            IProtocolHandler p2p = BuildHandler("p2p", 10);
            session.AddProtocolHandler(p2p);
        }
        
        [Test]
        public void Cannot_receive_before_initialized()
        {
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
            Assert.Throws<InvalidOperationException>(() => session.ReceiveMessage(new Packet("p2p", 1, Bytes.Empty)));
            session.Handshake(TestItem.PublicKeyA);
            Assert.Throws<InvalidOperationException>(() => session.ReceiveMessage(new Packet("p2p", 1, Bytes.Empty)));
            session.Init(5, _channelHandlerContext, _packetSender);
            IProtocolHandler p2p = BuildHandler("p2p", 10);
            session.AddProtocolHandler(p2p);
        }

        [Test]
        public void Stops_delivering_messages_after_disconnect()
        {
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            IProtocolHandler p2p = BuildHandler("p2p", 10);
            session.AddProtocolHandler(p2p);

            session.InitiateDisconnect(DisconnectReason.Other);

            session.DeliverMessage(new Packet("p2p", 3, Bytes.Empty));
            _packetSender.DidNotReceive().Enqueue(Arg.Is<Packet>(p => p.Protocol == "p2p" && p.PacketType == 3));
        }
        
        [Test]
        public void Stops_receiving_messages_after_disconnect()
        {
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _channelHandlerContext, _packetSender);
            IProtocolHandler p2p = BuildHandler("p2p", 10);
            session.AddProtocolHandler(p2p);

            session.InitiateDisconnect(DisconnectReason.Other);

            session.ReceiveMessage(new Packet("p2p", 3, Bytes.Empty));
            p2p.DidNotReceive().HandleMessage(Arg.Is<Packet>(p => p.Protocol == "p2p" && p.PacketType == 3));
        }

        [Test]
        public void Can_receive_messages()
        {
            P2PSession session = new P2PSession(TestItem.PublicKeyB, 30312, ConnectionDirection.Out, LimboLogs.Instance, _channel);
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

            session.ReceiveMessage(new Packet("---", 3, Bytes.Empty));
            p2p.Received().HandleMessage(Arg.Is<Packet>(p => p.Protocol == "p2p" && p.PacketType == 3));

            session.ReceiveMessage(new Packet("---", 11, Bytes.Empty));
            aaa.Received().HandleMessage(Arg.Is<Packet>(p => p.Protocol == "aaa" && p.PacketType == 1));

            session.ReceiveMessage(new Packet("---", 21, Bytes.Empty));
            bbb.Received().HandleMessage(Arg.Is<Packet>(p => p.Protocol == "bbb" && p.PacketType == 1));

            session.ReceiveMessage(new Packet("---", 25, Bytes.Empty));
            ccc.Received().HandleMessage(Arg.Is<Packet>(p => p.Protocol == "ccc" && p.PacketType == 0));

            session.ReceiveMessage(new Packet("---", 100, Bytes.Empty));
        }
    }
}