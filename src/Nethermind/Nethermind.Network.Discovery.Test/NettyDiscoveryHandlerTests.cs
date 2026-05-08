// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using DotNetty.Handlers.Logging;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test
{
    [Parallelizable(ParallelScope.None)] // Some test check for global metric
    [TestFixture]
    public class NettyDiscoveryHandlerTests
    {
        private readonly PrivateKey _privateKey = new("49a7b37aa6f6645917e7b807e9d1c00d4fa71f18343b0d4122a4d2df64dd6fee");
        private readonly PrivateKey _privateKey2 = new("3a1076bf45ab87712ad64ccb3b10217737f7faacbf2872e88fdd9a537d8fe266");
        private List<IChannel> _channels = new();
        private List<NettyDiscoveryHandler> _discoveryHandlers = new();
        private List<IDiscoveryManager> _discoveryManagersMocks = new();
        private readonly IPEndPoint _address = new(IPAddress.Loopback, 10001);
        private readonly IPEndPoint _address2 = new(IPAddress.Loopback, 10002);
        private int _channelActivatedCounter;
        private IChannelFactory _channelFactory = new LocalChannelFactory(nameof(NettyDiscoveryBaseHandler), new NetworkConfig());

        [SetUp]
        public async Task Initialize()
        {
            _channels = new List<IChannel>();
            _discoveryHandlers = new List<NettyDiscoveryHandler>();
            _discoveryManagersMocks = new List<IDiscoveryManager>();
            _channelActivatedCounter = 0;
            IDiscoveryManager? discoveryManagerMock = Substitute.For<IDiscoveryManager>();
            IMessageSerializationService? messageSerializationService = Build.A.SerializationService().WithDiscovery(_privateKey).TestObject;

            IDiscoveryManager? discoveryManagerMock2 = Substitute.For<IDiscoveryManager>();
            IMessageSerializationService? messageSerializationService2 = Build.A.SerializationService().WithDiscovery(_privateKey).TestObject;

            await StartUdpChannel("127.0.0.1", 10001, discoveryManagerMock, messageSerializationService);
            await StartUdpChannel("127.0.0.1", 10002, discoveryManagerMock2, messageSerializationService2);

            _discoveryManagersMocks.Add(discoveryManagerMock);
            _discoveryManagersMocks.Add(discoveryManagerMock2);

            Assert.That(() => _channelActivatedCounter, Is.EqualTo(2).After(1000, 100));
        }

        [TearDown]
        public async Task CleanUp()
        {
            _channels.ForEach(static x => { x.CloseAsync(); });
            await Task.Delay(50);
        }

        [Test]
        public async Task PingSentReceivedTest()
        {
            PingMsg msg = new(_privateKey2.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, _address, _address2, new byte[32])
            {
                FarAddress = _address2
            };

            await _discoveryHandlers[0].SendMsg(msg);
            await SleepWhileWaiting();
            _discoveryManagersMocks[1].Received(1).OnIncomingMsg(Arg.Is<DiscoveryMsg>(static x => x.MsgType == MsgType.Ping));

            PingMsg msg2 = new(_privateKey.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, _address2, _address, new byte[32])
            {
                FarAddress = _address
            };

            await _discoveryHandlers[1].SendMsg(msg2);
            await SleepWhileWaiting();
            _discoveryManagersMocks[0].Received(1).OnIncomingMsg(Arg.Is<DiscoveryMsg>(static x => x.MsgType == MsgType.Ping));
        }

        [Test]
        public async Task PongSentReceivedTest()
        {
            PongMsg msg = new(_privateKey2.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, new byte[] { 1, 2, 3 })
            {
                FarAddress = _address2
            };

            await _discoveryHandlers[0].SendMsg(msg);
            await SleepWhileWaiting();
            _discoveryManagersMocks[1].Received(1).OnIncomingMsg(Arg.Is<DiscoveryMsg>(static x => x.MsgType == MsgType.Pong));

            PongMsg msg2 = new(_privateKey.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, new byte[] { 1, 2, 3 })
            {
                FarAddress = _address
            };
            await _discoveryHandlers[1].SendMsg(msg2);
            await SleepWhileWaiting();
            _discoveryManagersMocks[0].Received(1).OnIncomingMsg(Arg.Is<DiscoveryMsg>(static x => x.MsgType == MsgType.Pong));
        }

        [Test]
        public async Task FindNodeSentReceivedTest()
        {
            FindNodeMsg msg = new(_privateKey2.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, new byte[] { 1, 2, 3 })
            {
                FarAddress = _address2
            };

            await _discoveryHandlers[0].SendMsg(msg);
            await SleepWhileWaiting();
            _discoveryManagersMocks[1].Received(1).OnIncomingMsg(Arg.Is<DiscoveryMsg>(static x => x.MsgType == MsgType.FindNode));

            FindNodeMsg msg2 = new(_privateKey2.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, new byte[] { 1, 2, 3 })
            {
                FarAddress = _address
            };

            await _discoveryHandlers[1].SendMsg(msg2);
            await SleepWhileWaiting();
            _discoveryManagersMocks[0].Received(1).OnIncomingMsg(Arg.Is<DiscoveryMsg>(static x => x.MsgType == MsgType.FindNode));
        }

        [Test]
        public async Task NeighborsSentReceivedTest()
        {
            NeighborsMsg msg = new(_privateKey2.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, Array.Empty<Node>())
            {
                FarAddress = _address2
            };

            await _discoveryHandlers[0].SendMsg(msg);
            await SleepWhileWaiting();
            _discoveryManagersMocks[1].Received(1).OnIncomingMsg(Arg.Is<DiscoveryMsg>(static x => x.MsgType == MsgType.Neighbors));

            NeighborsMsg msg2 = new(_privateKey.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, Array.Empty<Node>())
            {
                FarAddress = _address,
            };

            await _discoveryHandlers[1].SendMsg(msg2);
            await SleepWhileWaiting();
            _discoveryManagersMocks[0].Received(1).OnIncomingMsg(Arg.Is<DiscoveryMsg>(static x => x.MsgType == MsgType.Neighbors));
        }

        private (IDiscoveryManager DiscoveryManager, NettyDiscoveryHandler Handler, IChannelHandlerContext Ctx, IMessageSerializationService Service) CreateHandler(NodeFilter? nodeFilter = null)
        {
            IDiscoveryManager discoveryManager = Substitute.For<IDiscoveryManager>();
            IMessageSerializationService service = Build.A.SerializationService().WithDiscovery(_privateKey2).TestObject;
            IChannel channel = Substitute.For<IChannel>();
            NettyDiscoveryHandler handler = nodeFilter is not null
                ? new(discoveryManager, channel, service, Timestamper.Default, LimboLogs.Instance, nodeFilter)
                : new(discoveryManager, channel, service, Timestamper.Default, LimboLogs.Instance);
            IChannelHandlerContext ctx = Substitute.For<IChannelHandlerContext>();
            return (discoveryManager, handler, ctx, service);
        }

        [Test]
        public void UndersizedPacketIsNotForwardedToDiscoveryManager()
        {
            (IDiscoveryManager? discoveryManagerMock, NettyDiscoveryHandler? handler, IChannelHandlerContext? ctx, IMessageSerializationService _) = CreateHandler();

            // 50 bytes is well under the 98-byte minimum for a valid discovery v4 message
            byte[] data = new byte[50];
            IPEndPoint from = IPEndPoint.Parse("127.0.0.1:10000");
            IPEndPoint to = IPEndPoint.Parse("127.0.0.1:10003");
            handler.ChannelRead(ctx, new DatagramPacket(Unpooled.WrappedBuffer(data), from, to));

            discoveryManagerMock.DidNotReceive().OnIncomingMsg(Arg.Any<DiscoveryMsg>());
        }

        [Test]
        public void ForwardsUnrecognizedMessageToNextHandler()
        {
            byte[] data = [1, 2, 3];
            IPEndPoint from = IPEndPoint.Parse("127.0.0.1:10000");
            IPEndPoint to = IPEndPoint.Parse("127.0.0.1:10003");
            DatagramPacket packet = new(Unpooled.WrappedBuffer(data), from, to);

            IChannelHandlerContext ctx = Substitute.For<IChannelHandlerContext>();
            _discoveryHandlers[0].ChannelRead(ctx, packet);

            ctx.FireChannelRead(Arg.Is<DatagramPacket>(
                p => p.Content.ReadAllBytesAsArray().SequenceEqual(data)
            ));
        }

        [Test]
        public async Task FarFutureMessagesAreRejected()
        {
            PingMsg msg = new(_privateKey2.PublicKey, Timestamper.Default.UnixTime.SecondsLong + (long)TimeSpan.FromHours(2).TotalSeconds, _address, _address2, new byte[32])
            {
                FarAddress = _address2
            };

            await _discoveryHandlers[0].SendMsg(msg);
            await SleepWhileWaiting();

            _discoveryManagersMocks[1].DidNotReceive().OnIncomingMsg(Arg.Any<DiscoveryMsg>());
        }

        [Test]
        public async Task RateLimitedMessagesAreIgnored()
        {
            (IDiscoveryManager? discoveryManagerMock, NettyDiscoveryHandler? handler, IChannelHandlerContext? ctx, IMessageSerializationService? service) = CreateHandler(NodeFilter.CreateExact(16, TimeSpan.FromMinutes(1)));
            SemaphoreSlim called = new(0);
            discoveryManagerMock.When(x => x.OnIncomingMsg(Arg.Any<DiscoveryMsg>())).Do(_ => called.Release());

            byte[] data = SerializePing(service);

            handler.ChannelRead(ctx, new DatagramPacket(Unpooled.WrappedBuffer((byte[])data.Clone()), _address2, _address));
            handler.ChannelRead(ctx, new DatagramPacket(Unpooled.WrappedBuffer((byte[])data.Clone()), _address2, _address));

            // Wait for the one allowed message to be dispatched via Task.Factory.StartNew
            Assert.That(await called.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
            // Brief wait to ensure no second call sneaks through
            await Task.Delay(50);

            discoveryManagerMock.Received(1).OnIncomingMsg(Arg.Any<DiscoveryMsg>());
        }

        [Test]
        public async Task DefaultInboundRateLimiter_Allows_ShortBurstFromSameIp()
        {
            (IDiscoveryManager? discoveryManagerMock, NettyDiscoveryHandler? handler, IChannelHandlerContext? ctx, IMessageSerializationService? service) = CreateHandler();

            byte[] data = SerializePing(service);

            handler.ChannelRead(ctx, new DatagramPacket(Unpooled.WrappedBuffer((byte[])data.Clone()), _address2, _address));
            handler.ChannelRead(ctx, new DatagramPacket(Unpooled.WrappedBuffer((byte[])data.Clone()), _address2, _address));

            await SleepWhileWaiting();

            discoveryManagerMock.Received(2).OnIncomingMsg(Arg.Any<DiscoveryMsg>());
        }

        [Test]
        public async Task DefaultInboundRateLimiter_Drops_Message_AboveBurstLimit()
        {
            (IDiscoveryManager? discoveryManagerMock, NettyDiscoveryHandler? handler, IChannelHandlerContext? ctx, IMessageSerializationService? service) = CreateHandler();

            byte[] data = SerializePing(service);

            for (int i = 0; i < 5; i++)
            {
                handler.ChannelRead(ctx, new DatagramPacket(Unpooled.WrappedBuffer((byte[])data.Clone()), _address2, _address));
            }

            await SleepWhileWaiting();

            discoveryManagerMock.Received(4).OnIncomingMsg(Arg.Any<DiscoveryMsg>());
        }

        private byte[] SerializePing(IMessageSerializationService service)
        {
            PingMsg msg = new(_privateKey2.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, _address2, _address, new byte[32])
            {
                FarAddress = _address
            };

            IByteBuffer serialized = service.ZeroSerialize(msg);
            byte[] data = serialized.ReadAllBytesAsArray();
            serialized.SafeRelease();
            return data;
        }

        private async Task StartUdpChannel(string address, int port, IDiscoveryManager discoveryManager, IMessageSerializationService service)
        {
            MultithreadEventLoopGroup group = new(1);

            Bootstrap bootstrap = new();
            bootstrap
                .Group(group)
                .ChannelFactory(() => _channelFactory.CreateDatagramChannel())
                .Handler(new ActionChannelInitializer<IDatagramChannel>(x => InitializeChannel(x, discoveryManager, service)));

            _channels.Add(await bootstrap.BindAsync(IPAddress.Parse(address), port));
        }

        private void InitializeChannel(IDatagramChannel channel, IDiscoveryManager discoveryManager, IMessageSerializationService service)
        {
            NettyDiscoveryHandler handler = new(discoveryManager, channel, service, new Timestamper(), LimboLogs.Instance);
            handler.OnChannelActivated += (_, _) =>
            {
                _channelActivatedCounter++;
            };
            _discoveryHandlers.Add(handler);
            discoveryManager.MsgSender = handler;
            channel.Pipeline
                .AddLast(new LoggingHandler(DotNetty.Handlers.Logging.LogLevel.TRACE))
                .AddLast(handler);
        }

        private static async Task SleepWhileWaiting() => await Task.Delay((TestContext.CurrentContext.CurrentRepeatCount + 1) * 300);
    }
}
