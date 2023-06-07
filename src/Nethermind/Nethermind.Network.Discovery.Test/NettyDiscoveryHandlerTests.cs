// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using DotNetty.Handlers.Logging;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Test.Builders;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test
{
    [Parallelizable(ParallelScope.Self)]
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
            _channels.ForEach(x => { x.CloseAsync(); });
            await Task.Delay(50);
        }

        [Test]
        [Retry(5)]
        public async Task PingSentReceivedTest()
        {
            ResetMetrics();

            PingMsg msg = new(_privateKey2.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, _address, _address2, new byte[32])
            {
                FarAddress = _address2
            };

            _discoveryHandlers[0].SendMsg(msg);
            await SleepWhileWaiting();
            _discoveryManagersMocks[1].Received(1).OnIncomingMsg(Arg.Is<DiscoveryMsg>(x => x.MsgType == MsgType.Ping));

            PingMsg msg2 = new(_privateKey.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, _address2, _address, new byte[32])
            {
                FarAddress = _address
            };

            _discoveryHandlers[1].SendMsg(msg2);
            await SleepWhileWaiting();
            _discoveryManagersMocks[0].Received(1).OnIncomingMsg(Arg.Is<DiscoveryMsg>(x => x.MsgType == MsgType.Ping));

            AssertMetrics(258);
        }

        [Test]
        [Retry(5)]
        public async Task PongSentReceivedTest()
        {
            ResetMetrics();

            PongMsg msg = new(_privateKey2.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, new byte[] { 1, 2, 3 })
            {
                FarAddress = _address2
            };

            _discoveryHandlers[0].SendMsg(msg);
            await SleepWhileWaiting();
            _discoveryManagersMocks[1].Received(1).OnIncomingMsg(Arg.Is<DiscoveryMsg>(x => x.MsgType == MsgType.Pong));

            PongMsg msg2 = new(_privateKey.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, new byte[] { 1, 2, 3 })
            {
                FarAddress = _address
            };
            _discoveryHandlers[1].SendMsg(msg2);
            await SleepWhileWaiting();
            _discoveryManagersMocks[0].Received(1).OnIncomingMsg(Arg.Is<DiscoveryMsg>(x => x.MsgType == MsgType.Pong));

            AssertMetrics(240);
        }

        [Test]
        [Retry(5)]
        public async Task FindNodeSentReceivedTest()
        {
            ResetMetrics();

            FindNodeMsg msg = new(_privateKey2.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, new byte[] { 1, 2, 3 })
            {
                FarAddress = _address2
            };

            _discoveryHandlers[0].SendMsg(msg);
            await SleepWhileWaiting();
            _discoveryManagersMocks[1].Received(1).OnIncomingMsg(Arg.Is<DiscoveryMsg>(x => x.MsgType == MsgType.FindNode));

            FindNodeMsg msg2 = new(_privateKey2.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, new byte[] { 1, 2, 3 })
            {
                FarAddress = _address
            };

            _discoveryHandlers[1].SendMsg(msg2);
            await SleepWhileWaiting();
            _discoveryManagersMocks[0].Received(1).OnIncomingMsg(Arg.Is<DiscoveryMsg>(x => x.MsgType == MsgType.FindNode));

            AssertMetrics(216);
        }

        [Test]
        [Retry(5)]
        public async Task NeighborsSentReceivedTest()
        {
            ResetMetrics();

            NeighborsMsg msg = new(_privateKey2.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, new List<Node>().ToArray())
            {
                FarAddress = _address2
            };

            _discoveryHandlers[0].SendMsg(msg);
            await SleepWhileWaiting();
            _discoveryManagersMocks[1].Received(1).OnIncomingMsg(Arg.Is<DiscoveryMsg>(x => x.MsgType == MsgType.Neighbors));

            NeighborsMsg msg2 = new(_privateKey.PublicKey, Timestamper.Default.UnixTime.SecondsLong + 1200, new List<Node>().ToArray())
            {
                FarAddress = _address,
            };

            _discoveryHandlers[1].SendMsg(msg2);
            await SleepWhileWaiting();
            _discoveryManagersMocks[0].Received(1).OnIncomingMsg(Arg.Is<DiscoveryMsg>(x => x.MsgType == MsgType.Neighbors));

            AssertMetrics(210);
        }

        private static void ResetMetrics()
        {
            Metrics.DiscoveryBytesSent = Metrics.DiscoveryBytesReceived = 0;
        }

        private static void AssertMetrics(int value)
        {
            Metrics.DiscoveryBytesSent.Should().Be(value);
            Metrics.DiscoveryBytesReceived.Should().Be(value);
        }

        private async Task StartUdpChannel(string address, int port, IDiscoveryManager discoveryManager, IMessageSerializationService service)
        {
            MultithreadEventLoopGroup group = new(1);

            Bootstrap bootstrap = new();
            bootstrap
                .Group(group)
                .ChannelFactory(() => new SocketDatagramChannel(AddressFamily.InterNetwork))
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

        private static async Task SleepWhileWaiting()
        {
            await Task.Delay((TestContext.CurrentContext.CurrentRepeatCount + 1) * 300);
        }
    }
}
