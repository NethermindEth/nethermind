using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.KeyStore;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Stats;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test
{
    [TestFixture]
    public class PeerManagerTests
    {
        private IPeerManager _peerManager;
        private INodeFactory _nodeFactory;
        private IDiscoveryConfigurationProvider _configurationProvider;
        private IDiscoveryManager _discoveryManager;
        private TestRlpxPeer _localPeer;
        private ISynchronizationManager _synchronizationManager;
        private ILogger _logger;

        [SetUp]
        public void Initialize()
        {
            _logger = new SimpleConsoleLogger();
            _configurationProvider = new DiscoveryConfigurationProvider(new NetworkHelper(_logger));
            _configurationProvider.DbBasePath = Path.Combine(Path.GetTempPath(), "PeerManagerTests");
            if (!Directory.Exists(_configurationProvider.DbBasePath))
            {
                Directory.CreateDirectory(_configurationProvider.DbBasePath);
            }
            _nodeFactory = new NodeFactory();
            _localPeer = new TestRlpxPeer();
            var keyProvider = new PrivateKeyProvider(new CryptoRandom());
            var key = keyProvider.PrivateKey.PublicKey;
            _synchronizationManager = Substitute.For<ISynchronizationManager>();

            var nodeTable = new NodeTable(_configurationProvider, _nodeFactory, Substitute.For<IKeyStore>(), _logger, new NodeDistanceCalculator(_configurationProvider));
            nodeTable.Initialize(key);

            _discoveryManager = new DiscoveryManager(_logger, _configurationProvider, new NodeLifecycleManagerFactory(_nodeFactory, nodeTable, _logger, _configurationProvider, new DiscoveryMessageFactory(_configurationProvider), Substitute.For<IEvictionManager>(), new NodeStatsProvider(_configurationProvider)), _nodeFactory, nodeTable, new DiscoveryStorage(_configurationProvider, _nodeFactory, _logger, new PerfService(_logger)));
            _discoveryManager.MessageSender = Substitute.For<IMessageSender>();

            _peerManager = new PeerManager(_localPeer, _discoveryManager, _logger, _configurationProvider, _synchronizationManager, new NodeStatsProvider(_configurationProvider), new PeerStorage(_configurationProvider, _nodeFactory, _logger, new PerfService(_logger)), new PerfService(_logger), _nodeFactory);
        }

        [Test]
        public void OutPeerBecomesActiveAndDisconnectTest()
        {
            var p2pSession = InitializeNode();

            //trigger p2p initialization
            var p2pProtocol = new P2PProtocolHandler(p2pSession, new MessageSerializationService(), p2pSession.RemoteNodeId, p2pSession.RemotePort ?? 0, _logger);
            var p2pArgs = new P2PProtocolInitializedEventArgs(p2pProtocol)
            {
                P2PVersion = 4,
                Capabilities = new[] {new Capability(Protocol.Eth, 62)}.ToList(),
            };
            p2pSession.TriggerProtocolInitialized(p2pArgs);
            Assert.IsTrue(_peerManager.ActivePeers.First().NodeStats.DidEventHappen(NodeStatsEvent.P2PInitialized));

            //trigger eth62 initialization
            var eth62 = new Eth62ProtocolHandler(p2pSession, new MessageSerializationService(), _synchronizationManager, _logger);
            var args = new Eth62ProtocolInitializedEventArgs(eth62)
            {
                ChainId = _synchronizationManager.ChainId
            };
            p2pSession.TriggerProtocolInitialized(args);
            Assert.IsTrue(_peerManager.ActivePeers.First().NodeStats.DidEventHappen(NodeStatsEvent.Eth62Initialized));
            Assert.NotNull(_peerManager.ActivePeers.First().SynchronizationPeer);

            //verify active peer was added to synch manager
            _synchronizationManager.Received(1).AddPeer(Arg.Any<ISynchronizationPeer>());

            //trigger disconnect
            p2pSession.TriggerPeerDisconnected();
            Assert.AreEqual(1, _peerManager.CandidatePeers.Count);
            Assert.AreEqual(0, _peerManager.ActivePeers.Count);

            //verify active peer was removed from synch manager
            _synchronizationManager.Received(1).RemovePeer(Arg.Any<ISynchronizationPeer>());
        }

        [Test]
        public void InPeerBecomesActiveAndDisconnectTest()
        {
            var node = _nodeFactory.CreateNode("192.1.1.1", 3333);

            //trigger connection initialized
            var p2pSession = new TestP2PSession();
            p2pSession.RemoteNodeId = node.Id;
            p2pSession.RemoteHost = node.Host;
            p2pSession.RemotePort = node.Port;
            p2pSession.ClientConnectionType = ClientConnectionType.In;
            _localPeer.TriggerConnectionInitialized(p2pSession, ClientConnectionType.In);

            //trigger p2p initialization
            var p2pProtocol = new P2PProtocolHandler(p2pSession, new MessageSerializationService(), p2pSession.RemoteNodeId, p2pSession.RemotePort ?? 0, _logger);
            var p2pArgs = new P2PProtocolInitializedEventArgs(p2pProtocol)
            {
                P2PVersion = 4,
                Capabilities = new[] { new Capability(Protocol.Eth, 62) }.ToList()
            };
            p2pSession.TriggerProtocolInitialized(p2pArgs);
            Assert.IsTrue(_peerManager.ActivePeers.First().NodeStats.DidEventHappen(NodeStatsEvent.P2PInitialized));

            //trigger eth62 initialization
            var eth62 = new Eth62ProtocolHandler(p2pSession, new MessageSerializationService(), _synchronizationManager, _logger);
            var args = new Eth62ProtocolInitializedEventArgs(eth62)
            {
                ChainId = _synchronizationManager.ChainId
            };
            p2pSession.TriggerProtocolInitialized(args);

            Assert.AreEqual(1, _peerManager.CandidatePeers.Count);
            Assert.AreEqual(1, _peerManager.ActivePeers.Count);
            Assert.IsTrue(_peerManager.ActivePeers.First().NodeStats.DidEventHappen(NodeStatsEvent.Eth62Initialized));
            Assert.NotNull(_peerManager.ActivePeers.First().SynchronizationPeer);

            //verify active peer was added to synch manager
            _synchronizationManager.Received(1).AddPeer(Arg.Any<ISynchronizationPeer>());

            //trigger disconnect
            p2pSession.TriggerPeerDisconnected();
            Assert.AreEqual(1, _peerManager.CandidatePeers.Count);
            Assert.AreEqual(0, _peerManager.ActivePeers.Count);

            //verify active peer was removed from synch manager
            _synchronizationManager.Received(1).RemovePeer(Arg.Any<ISynchronizationPeer>());
        }

        [Test]
        public void DisconnectOnWrongP2PVersionTest()
        {
            var p2pSession = InitializeNode();

            //trigger p2p initialization
            var p2pProtocol = new P2PProtocolHandler(p2pSession, new MessageSerializationService(), p2pSession.RemoteNodeId, p2pSession.RemotePort??0, _logger);
            var p2pArgs = new P2PProtocolInitializedEventArgs(p2pProtocol)
            {
                P2PVersion = 1,
                Capabilities = new[] { new Capability(Protocol.Eth, 62) }.ToList()
            };
            p2pSession.TriggerProtocolInitialized(p2pArgs);
            Assert.IsTrue(p2pSession.Disconected);
            Assert.AreEqual(DisconnectReason.IncompatibleP2PVersion, p2pSession.DisconnectReason);
        }

        [Test]
        public void DisconnectOnNoEth62SupportTest()
        {
            var p2pSession = InitializeNode();

            //trigger p2p initialization
            var p2pProtocol = new P2PProtocolHandler(p2pSession, new MessageSerializationService(), p2pSession.RemoteNodeId, p2pSession.RemotePort ?? 0, _logger);
            var p2pArgs = new P2PProtocolInitializedEventArgs(p2pProtocol)
            {
                P2PVersion = 5,
                Capabilities = new[] { new Capability(Protocol.Eth, 60) }.ToList()
            };
            p2pSession.TriggerProtocolInitialized(p2pArgs);
            Assert.IsTrue(p2pSession.Disconected);
            Assert.AreEqual(DisconnectReason.Other, p2pSession.DisconnectReason);
        }

        [Test]
        public void DisconnectOnWrongChainIdTest()
        {
            var p2pSession = InitializeNode();

            //trigger eth62 initialization
            var eth62 = new Eth62ProtocolHandler(p2pSession, new MessageSerializationService(), _synchronizationManager, _logger);
            var args = new Eth62ProtocolInitializedEventArgs(eth62)
            {
                ChainId = 100
            };
            p2pSession.TriggerProtocolInitialized(args);
            Assert.IsTrue(p2pSession.Disconected);
            Assert.AreEqual(DisconnectReason.Other, p2pSession.DisconnectReason);
        }

        [Test]
        public void DisconnectOnAlreadyConnectedTest()
        {
            var p2pSession = InitializeNode(ClientConnectionType.In);

            //trigger p2p initialization
            var p2pProtocol = new P2PProtocolHandler(p2pSession, new MessageSerializationService(), p2pSession.RemoteNodeId, p2pSession.RemotePort ?? 0, _logger);
            var p2pArgs = new P2PProtocolInitializedEventArgs(p2pProtocol)
            {
                P2PVersion = 1,
                Capabilities = new[] { new Capability(Protocol.Eth, 62) }.ToList()
            };
            p2pSession.TriggerProtocolInitialized(p2pArgs);
            Assert.IsTrue(p2pSession.Disconected);
            Assert.AreEqual(DisconnectReason.AlreadyConnected, p2pSession.DisconnectReason);
        }

        [Test]
        public void DisconnectOnTooManyPeersTest()
        {
            var node = _nodeFactory.CreateNode("192.1.1.1", 3333);
            ((DiscoveryConfigurationProvider)_configurationProvider).ActivePeersMaxCount = 0;

            //trigger connection initialized
            var p2pSession = new TestP2PSession();
            p2pSession.RemoteNodeId = node.Id;
            p2pSession.ClientConnectionType = ClientConnectionType.In;
            _localPeer.TriggerConnectionInitialized(p2pSession, ClientConnectionType.In);

            //trigger p2p initialization
            var p2pProtocol = new P2PProtocolHandler(p2pSession, new MessageSerializationService(), p2pSession.RemoteNodeId, p2pSession.RemotePort ?? 0, _logger);
            var p2pArgs = new P2PProtocolInitializedEventArgs(p2pProtocol)
            {
                P2PVersion = 1,
                Capabilities = new[] { new Capability(Protocol.Eth, 62) }.ToList()
            };
            p2pSession.TriggerProtocolInitialized(p2pArgs);
            Assert.IsTrue(p2pSession.Disconected);
            Assert.AreEqual(DisconnectReason.TooManyPeers, p2pSession.DisconnectReason);
        }

        private TestP2PSession InitializeNode(ClientConnectionType clientConnectionType = ClientConnectionType.Out)
        {
            var node = _nodeFactory.CreateNode("192.1.1.1", 3333);
            _discoveryManager.GetNodeLifecycleManager(node);

            //verify new peer is added
            Assert.AreEqual(1, _peerManager.CandidatePeers.Count);
            Assert.AreEqual(node.Id, _peerManager.CandidatePeers.First().Node.Id);

            //trigger connection start
            var task = _peerManager.RunPeerUpdate();
            task.Wait();
            Assert.AreEqual(1, _localPeer.ConnectionAsyncCallsCounter);
            Assert.AreEqual(1, _peerManager.CandidatePeers.Count);
            Assert.AreEqual(1, _peerManager.ActivePeers.Count);

            //trigger connection initialized
            var p2pSession = new TestP2PSession();
            p2pSession.RemoteNodeId = node.Id;
            p2pSession.ClientConnectionType = clientConnectionType;
            _localPeer.TriggerConnectionInitialized(p2pSession);

            var peer = _peerManager.ActivePeers.First();
            if (clientConnectionType == ClientConnectionType.Out)
            {
                Assert.IsNotNull(peer.Session);
            }

            return p2pSession;
        }
    }

    public class TestP2PSession : IP2PSession
    {
        public bool Disconected { get; set; }
        public DisconnectReason DisconnectReason { get; set; }

        public PublicKey RemoteNodeId { get; set; }
        public string RemoteHost { get; set; }
        public int? RemotePort { get; set; }
        public ClientConnectionType ClientConnectionType { get; set; }

        public string SessionId { get; }

        public TestP2PSession()
        {
            SessionId = Guid.NewGuid().ToString();
        }

        public void ReceiveMessage(Packet packet)
        {

        }

        public void TriggerProtocolInitialized(ProtocolInitializedEventArgs eventArgs)
        {
            ProtocolInitialized?.Invoke(this, eventArgs);
        }

        public void TriggerPeerDisconnected()
        {
            PeerDisconnected?.Invoke(this, new DisconnectEventArgs(DisconnectReason.TooManyPeers, DisconnectType.Local, SessionId));
        }

        public void DeliverMessage(Packet packet, bool priority = false)
        {
        }

        public void Init(byte p2PVersion, IChannelHandlerContext context, IPacketSender packetSender)
        {
        }

        public Task InitiateDisconnectAsync(DisconnectReason disconnectReason)
        {
            Disconected = true;
            DisconnectReason = disconnectReason;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(DisconnectReason disconnectReason, DisconnectType disconnectType, TimeSpan? delay = null)
        {
            return Task.CompletedTask;
        }

        public event EventHandler<DisconnectEventArgs> PeerDisconnected;
        public event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized;
    }

    public class TestRlpxPeer : IRlpxPeer
    {
        public int ConnectionAsyncCallsCounter = 0;

        public Task Shutdown()
        {
            return Task.CompletedTask;
        }

        public void TriggerConnectionInitialized(IP2PSession session, ClientConnectionType clientConnectionType = ClientConnectionType.Out)
        {
            ConnectionInitialized?.Invoke(this, new ConnectionInitializedEventArgs(session, clientConnectionType));
        }

        public Task Init()
        {
            return Task.CompletedTask;
        }

        public Task ConnectAsync(PublicKey remoteNodeId, string remoteHost, int remotePort)
        {
            ConnectionAsyncCallsCounter++;
            return Task.CompletedTask;
        }

        public event EventHandler<ConnectionInitializedEventArgs> ConnectionInitialized;
    }
}