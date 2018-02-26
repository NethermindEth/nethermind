using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Handlers.Logging;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Discovery.Lifecycle;
using Nevermind.Discovery.Messages;
using Nevermind.Discovery.RoutingTable;
using Nevermind.Discovery.Serializers;
using Nevermind.Json;
using Nevermind.KeyStore;
using Nevermind.Network;
using NSubstitute;
using NUnit.Framework;
using Node = Nevermind.Discovery.RoutingTable.Node;

namespace Nevermind.Discovery.Test
{
    [TestFixture]
    public class NettyDiscoveryHandlerTests
    {
        private readonly PrivateKey _privateKey = new PrivateKey("49a7b37aa6f6645917e7b807e9d1c00d4fa71f18343b0d4122a4d2df64dd6fee");
        private readonly PrivateKey _privateKey2 = new PrivateKey("3a1076bf45ab87712ad64ccb3b10217737f7faacbf2872e88fdd9a537d8fe266");
        private List<IChannel> _channels;
        private List<NettyDiscoveryHandler> _discoveryHandlers;
        private List<IDiscoveryManager> _discoveryManagers;
        private readonly IPEndPoint _address = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 10001);
        private readonly IPEndPoint _address2 = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 10002);
        private int _channelActivatedCounter;

        [SetUp]
        public async Task Initialize()
        {
            _channels = new List<IChannel>();
            _discoveryHandlers = new List<NettyDiscoveryHandler>();
            _discoveryManagers = new List<IDiscoveryManager>();
            _channelActivatedCounter = 0;
            var discoveryManager = Substitute.For<IDiscoveryManager>();
            var messageSerializationService = CreateSerializationService(_privateKey);

            var discoveryManager2 = Substitute.For<IDiscoveryManager>();
            var messageSerializationService2 = CreateSerializationService(_privateKey2);

            await StartUdpChannel("127.0.0.1", 10001, discoveryManager, messageSerializationService);
            await StartUdpChannel("127.0.0.1", 10002, discoveryManager2, messageSerializationService2);

            _discoveryManagers.Add(discoveryManager);
            _discoveryManagers.Add(discoveryManager2);

            Thread.Sleep(50);

            Assert.AreEqual(2, _channelActivatedCounter);
        }

        [TearDown]
        public void CleanUp()
        {
            _channels.ToList().ForEach(x => { x.CloseAsync(); });
            Thread.Sleep(50);
        }

        [Test]
        public void PingSentReceivedTest()
        {
            var msg = new PingMessage
            {
                FarAddress = _address2,
                SourceAddress = _address,
                DestinationAddress = _address2,
                Version = 4,
                ExpirationTime = 100,
                FarPublicKey = _privateKey2.PublicKey
            };
            _discoveryHandlers[0].SendMessage(msg);
            Thread.Sleep(200);
            _discoveryManagers[1].Received(1).OnIncomingMessage(Arg.Is<DiscoveryMessage>(x => x.MessageType == MessageType.Ping));

            var msg2 = new PingMessage
            {
                FarAddress = _address,
                SourceAddress = _address2,
                DestinationAddress = _address,
                Version = 4,
                ExpirationTime = 100,
                FarPublicKey = _privateKey.PublicKey
            };
            _discoveryHandlers[1].SendMessage(msg2);
            Thread.Sleep(200);
            _discoveryManagers[0].Received(1).OnIncomingMessage(Arg.Is<DiscoveryMessage>(x => x.MessageType == MessageType.Ping));  
        }

        [Test]
        public void PongSentReceivedTest()
        {
            var msg = new PongMessage
            {
                FarAddress = _address2,
                PingMdc = new byte[] {1,2,3},
                ExpirationTime = 100,
                FarPublicKey = _privateKey2.PublicKey
            };
            _discoveryHandlers[0].SendMessage(msg);
            Thread.Sleep(200);
            _discoveryManagers[1].Received(1).OnIncomingMessage(Arg.Is<DiscoveryMessage>(x => x.MessageType == MessageType.Pong));

            var msg2 = new PongMessage
            {
                FarAddress = _address,
                PingMdc = new byte[] { 1, 2, 3 },
                ExpirationTime = 100,
                FarPublicKey = _privateKey.PublicKey
            };
            _discoveryHandlers[1].SendMessage(msg2);
            Thread.Sleep(200);
            _discoveryManagers[0].Received(1).OnIncomingMessage(Arg.Is<DiscoveryMessage>(x => x.MessageType == MessageType.Pong));
        }

        [Test]
        public void FindNodeSentReceivedTest()
        {
            var msg = new FindNodeMessage
            {
                FarAddress = _address2,
                SearchedNodeId = new byte[] { 1, 2, 3 },
                ExpirationTime = 100,
                FarPublicKey = _privateKey2.PublicKey
            };
            _discoveryHandlers[0].SendMessage(msg);
            Thread.Sleep(200);
            _discoveryManagers[1].Received(1).OnIncomingMessage(Arg.Is<DiscoveryMessage>(x => x.MessageType == MessageType.FindNode));

            var msg2 = new FindNodeMessage
            {
                FarAddress = _address,
                SearchedNodeId = new byte[] { 1, 2, 3 },
                ExpirationTime = 100,
                FarPublicKey = _privateKey.PublicKey
            };
            _discoveryHandlers[1].SendMessage(msg2);
            Thread.Sleep(200);
            _discoveryManagers[0].Received(1).OnIncomingMessage(Arg.Is<DiscoveryMessage>(x => x.MessageType == MessageType.FindNode));
        }

        [Test]
        public void NeighborsSentReceivedTest()
        {
            var msg = new NeighborsMessage
            {
                FarAddress = _address2,
                Nodes = new List<Node>().ToArray(),
                ExpirationTime = 100,
                FarPublicKey = _privateKey2.PublicKey
            };
            _discoveryHandlers[0].SendMessage(msg);
            Thread.Sleep(200);
            _discoveryManagers[1].Received(1).OnIncomingMessage(Arg.Is<DiscoveryMessage>(x => x.MessageType == MessageType.Neighbors));

            var msg2 = new NeighborsMessage
            {
                FarAddress = _address,
                Nodes = new List<Node>().ToArray(),
                ExpirationTime = 100,
                FarPublicKey = _privateKey.PublicKey
            };
            _discoveryHandlers[1].SendMessage(msg2);
            Thread.Sleep(200);
            _discoveryManagers[0].Received(1).OnIncomingMessage(Arg.Is<DiscoveryMessage>(x => x.MessageType == MessageType.Neighbors));
        }

        private async Task StartUdpChannel(string address, int port, IDiscoveryManager discoveryManager, IMessageSerializationService service)
        {
            var group = new MultithreadEventLoopGroup(1);

            var bootstrap = new Bootstrap();
            bootstrap
                .Group(group)
                .Channel<SocketDatagramChannel>()
                .Handler(new ActionChannelInitializer<IDatagramChannel>(x => InitializeChannel(x, discoveryManager, service)));

            _channels.Add(await bootstrap.BindAsync(IPAddress.Parse(address), port));
        }

        private void InitializeChannel(IDatagramChannel channel, IDiscoveryManager discoveryManager, IMessageSerializationService service)
        {
            var handler = new NettyDiscoveryHandler(new ConsoleLogger(), discoveryManager, channel, service);
            handler.OnChannelActivated += (x, y) => { _channelActivatedCounter++; };
            _discoveryHandlers.Add(handler);
            discoveryManager.MessageSender = handler;
            channel.Pipeline
                .AddLast(new LoggingHandler(LogLevel.TRACE))
                .AddLast(handler);
        }

        private IMessageSerializationService CreateSerializationService(PrivateKey privateKey)
        {
            var config = new DiscoveryConfigurationProvider();
            var signer = new Signer();

            var pingSerializer = new PingMessageSerializer(signer, privateKey, new DiscoveryMessageFactory(config), new NodeIdResolver(signer), new NodeFactory());
            var pongSerializer = new PongMessageSerializer(signer, privateKey, new DiscoveryMessageFactory(config), new NodeIdResolver(signer), new NodeFactory());
            var findNodeSerializer = new FindNodeMessageSerializer(signer, privateKey, new DiscoveryMessageFactory(config), new NodeIdResolver(signer), new NodeFactory());
            var neighborsSerializer = new NeighborsMessageSerializer(signer, privateKey, new DiscoveryMessageFactory(config), new NodeIdResolver(signer), new NodeFactory());

            var messageSerializationService = new MessageSerializationService();
            messageSerializationService.Register(pingSerializer);
            messageSerializationService.Register(pongSerializer);
            messageSerializationService.Register(findNodeSerializer);
            messageSerializationService.Register(neighborsSerializer);
            return messageSerializationService;
        }
    }
}