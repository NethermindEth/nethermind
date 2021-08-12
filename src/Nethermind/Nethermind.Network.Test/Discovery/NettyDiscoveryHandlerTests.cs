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

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Handlers.Logging;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Network.Test.Builders;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.Discovery
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class NettyDiscoveryHandlerTests
    {
        private readonly PrivateKey _privateKey = new PrivateKey("49a7b37aa6f6645917e7b807e9d1c00d4fa71f18343b0d4122a4d2df64dd6fee");
        private readonly PrivateKey _privateKey2 = new PrivateKey("3a1076bf45ab87712ad64ccb3b10217737f7faacbf2872e88fdd9a537d8fe266");
        private List<IChannel> _channels;
        private List<NettyDiscoveryHandler> _discoveryHandlers;
        private List<IDiscoveryManager> _discoveryManagersMocks;
        private readonly IPEndPoint _address = new IPEndPoint(IPAddress.Loopback, 10001);
        private readonly IPEndPoint _address2 = new IPEndPoint(IPAddress.Loopback, 10002);
        private int _channelActivatedCounter;

        [SetUp]
        public async Task Initialize()
        {
            _channels = new List<IChannel>();
            _discoveryHandlers = new List<NettyDiscoveryHandler>();
            _discoveryManagersMocks = new List<IDiscoveryManager>();
            _channelActivatedCounter = 0;
            var discoveryManagerMock = Substitute.For<IDiscoveryManager>();
            var messageSerializationService = Build.A.SerializationService().WithDiscovery(_privateKey).TestObject;

            var discoveryManagerMock2 = Substitute.For<IDiscoveryManager>();
            var messageSerializationService2 = Build.A.SerializationService().WithDiscovery(_privateKey).TestObject;

            await StartUdpChannel("127.0.0.1", 10001, discoveryManagerMock, messageSerializationService);
            await StartUdpChannel("127.0.0.1", 10002, discoveryManagerMock2, messageSerializationService2);

            _discoveryManagersMocks.Add(discoveryManagerMock);
            _discoveryManagersMocks.Add(discoveryManagerMock2);

            Assert.AreEqual(2, _channelActivatedCounter);
        }

        [TearDown]
        public async Task CleanUp()
        {
            _channels.ToList().ForEach(x => { x.CloseAsync(); });
            await Task.Delay(50);
        }

        [Test]
        [Retry(5)]
        public async Task PingSentReceivedTest()
        {
            ResetMetrics();
            
            var msg = new PingMessage
            {
                FarAddress = _address2,
                SourceAddress = _address,
                DestinationAddress = _address2,
                ExpirationTime = Timestamper.Default.UnixTime.SecondsLong + 1200,
                FarPublicKey = _privateKey2.PublicKey
            };
            _discoveryHandlers[0].SendMessage(msg);
            await SleepWhileWaiting();
            _discoveryManagersMocks[1].Received(1).OnIncomingMessage(Arg.Is<DiscoveryMessage>(x => x.MessageType == MessageType.Ping));

            var msg2 = new PingMessage
            {
                FarAddress = _address,
                SourceAddress = _address2,
                DestinationAddress = _address,
                ExpirationTime = Timestamper.Default.UnixTime.SecondsLong + 1200,
                FarPublicKey = _privateKey.PublicKey
            };
            _discoveryHandlers[1].SendMessage(msg2);
            await SleepWhileWaiting();
            _discoveryManagersMocks[0].Received(1).OnIncomingMessage(Arg.Is<DiscoveryMessage>(x => x.MessageType == MessageType.Ping));
            
            AssertMetrics(258);
        }

        [Test]
        [Retry(5)]
        public async Task PongSentReceivedTest()
        {
            ResetMetrics();
            
            var msg = new PongMessage
            {
                FarAddress = _address2,
                PingMdc = new byte[] {1,2,3},
                ExpirationTime = Timestamper.Default.UnixTime.SecondsLong + 1200,
                FarPublicKey = _privateKey2.PublicKey
            };
            _discoveryHandlers[0].SendMessage(msg);
            await SleepWhileWaiting();
            _discoveryManagersMocks[1].Received(1).OnIncomingMessage(Arg.Is<DiscoveryMessage>(x => x.MessageType == MessageType.Pong));

            var msg2 = new PongMessage
            {
                FarAddress = _address,
                PingMdc = new byte[] { 1, 2, 3 },
                ExpirationTime = Timestamper.Default.UnixTime.SecondsLong + 1200,
                FarPublicKey = _privateKey.PublicKey
            };
            _discoveryHandlers[1].SendMessage(msg2);
            await SleepWhileWaiting();
            _discoveryManagersMocks[0].Received(1).OnIncomingMessage(Arg.Is<DiscoveryMessage>(x => x.MessageType == MessageType.Pong));
            
            AssertMetrics(240);
        }
        
        [Test]
        [Retry(5)]
        public async Task FindNodeSentReceivedTest()
        {
            ResetMetrics();
            
            var msg = new FindNodeMessage
            {
                FarAddress = _address2,
                SearchedNodeId = new byte[] { 1, 2, 3 },
                ExpirationTime = Timestamper.Default.UnixTime.SecondsLong + 1200,
                FarPublicKey = _privateKey2.PublicKey
            };
            _discoveryHandlers[0].SendMessage(msg);
            await SleepWhileWaiting();
            _discoveryManagersMocks[1].Received(1).OnIncomingMessage(Arg.Is<DiscoveryMessage>(x => x.MessageType == MessageType.FindNode));

            var msg2 = new FindNodeMessage
            {
                FarAddress = _address,
                SearchedNodeId = new byte[] { 1, 2, 3 },
                ExpirationTime = Timestamper.Default.UnixTime.SecondsLong + 1200,
                FarPublicKey = _privateKey.PublicKey
            };
            _discoveryHandlers[1].SendMessage(msg2);
            await SleepWhileWaiting();
            _discoveryManagersMocks[0].Received(1).OnIncomingMessage(Arg.Is<DiscoveryMessage>(x => x.MessageType == MessageType.FindNode));
            
            AssertMetrics(216);
        }

        [Test]
        [Retry(5)]
        public async Task NeighborsSentReceivedTest()
        {
            ResetMetrics();
            
            var msg = new NeighborsMessage
            {
                FarAddress = _address2,
                Nodes = new List<Node>().ToArray(),
                ExpirationTime = Timestamper.Default.UnixTime.SecondsLong + 1200,
                FarPublicKey = _privateKey2.PublicKey
            };
            _discoveryHandlers[0].SendMessage(msg);
            await SleepWhileWaiting();
            _discoveryManagersMocks[1].Received(1).OnIncomingMessage(Arg.Is<DiscoveryMessage>(x => x.MessageType == MessageType.Neighbors));

            var msg2 = new NeighborsMessage
            {
                FarAddress = _address,
                Nodes = new List<Node>().ToArray(),
                ExpirationTime = Timestamper.Default.UnixTime.SecondsLong + 1200,
                FarPublicKey = _privateKey.PublicKey
            };
            _discoveryHandlers[1].SendMessage(msg2);
            await SleepWhileWaiting();
            _discoveryManagersMocks[0].Received(1).OnIncomingMessage(Arg.Is<DiscoveryMessage>(x => x.MessageType == MessageType.Neighbors));
            
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
            var group = new MultithreadEventLoopGroup(1);

            var bootstrap = new Bootstrap();
            bootstrap
                .Group(group)
                .ChannelFactory(() => new SocketDatagramChannel(AddressFamily.InterNetwork))
                .Handler(new ActionChannelInitializer<IDatagramChannel>(x => InitializeChannel(x, discoveryManager, service)));

            _channels.Add(await bootstrap.BindAsync(IPAddress.Parse(address), port));
        }

        private void InitializeChannel(IDatagramChannel channel, IDiscoveryManager discoveryManager, IMessageSerializationService service)
        {
            var handler = new NettyDiscoveryHandler(discoveryManager, channel, service, new Timestamper(), LimboLogs.Instance);
            handler.OnChannelActivated += (x, y) =>
            {
                _channelActivatedCounter++;
            };
            _discoveryHandlers.Add(handler);
            discoveryManager.MessageSender = handler;
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
