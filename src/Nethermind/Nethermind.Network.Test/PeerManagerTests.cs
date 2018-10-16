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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Core.Test.Builders;
using Nethermind.KeyStore;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Network.Test.Builders;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test
{
    [TestFixture]
    public class PeerManagerTests
    {
        private TestRlpxPeer _localPeer;
        private PeerManager _peerManager;
        private INodeFactory _nodeFactory;
        private IConfigProvider _configurationProvider;
        private IDiscoveryManager _discoveryManager;
        private ISynchronizationManager _synchronizationManager;
        private ILogManager _logManager;
        
        [SetUp]
        public void Initialize()
        {
            _logManager = new OneLoggerLogManager(new SimpleConsoleLogger());
            _configurationProvider = new JsonConfigProvider();
            var config = ((NetworkConfig)_configurationProvider.GetConfig<INetworkConfig>());
            config.DbBasePath = Path.Combine(Path.GetTempPath(), "PeerManagerTests");
            config.IsActivePeerTimerEnabled = false;
            config.IsDiscoveryNodesPersistenceOn = false;
            config.IsPeersPersistenceOn = false;

            if (!Directory.Exists(_configurationProvider.GetConfig<INetworkConfig>().DbBasePath))
            {
                Directory.CreateDirectory(_configurationProvider.GetConfig<INetworkConfig>().DbBasePath);
            }
            
            var serializationService = Build.A.SerializationService().WithEncryptionHandshake().WithP2P().WithEth().TestObject;

            var syncManager = Substitute.For<ISynchronizationManager>();
            Block genesisBlock = Build.A.Block.Genesis.TestObject;
            syncManager.Head.Returns(genesisBlock.Header);
            syncManager.Genesis.Returns(genesisBlock.Header);
            
            _nodeFactory = new NodeFactory();
            _localPeer = new TestRlpxPeer();
            var keyProvider = new PrivateKeyGenerator(new CryptoRandom());
            var key = keyProvider.Generate().PublicKey;
            _synchronizationManager = Substitute.For<ISynchronizationManager>();

            var nodeTable = new NodeTable(_nodeFactory, Substitute.For<IKeyStore>(), new NodeDistanceCalculator(_configurationProvider), _configurationProvider, _logManager);
            nodeTable.Initialize(new NodeId(key));

            INetworkConfig networkConfig = _configurationProvider.GetConfig<INetworkConfig>();
            IStatsConfig statsConfig = _configurationProvider.GetConfig<IStatsConfig>();
            _discoveryManager = new DiscoveryManager(new NodeLifecycleManagerFactory(_nodeFactory, nodeTable, new DiscoveryMessageFactory(_configurationProvider), Substitute.For<IEvictionManager>(), new NodeStatsProvider(_configurationProvider.GetConfig<IStatsConfig>(), _nodeFactory, _logManager), _configurationProvider, _logManager), _nodeFactory, nodeTable, new NetworkStorage("test", networkConfig, _logManager, new PerfService(_logManager)), _configurationProvider, _logManager);
            _discoveryManager.MessageSender = Substitute.For<IMessageSender>();
            var app = new DiscoveryApp(new NodesLocator(nodeTable, _discoveryManager, _configurationProvider, _logManager), _discoveryManager, _nodeFactory, nodeTable, Substitute.For<IMessageSerializationService>(), new CryptoRandom(), Substitute.For<INetworkStorage>(), _configurationProvider, _logManager, new PerfService(_logManager));
            app.Initialize(key);
            
            var networkStorage = new NetworkStorage("test", networkConfig, _logManager, new PerfService(_logManager));
            _peerManager = new PeerManager(_localPeer, app, _synchronizationManager, new NodeStatsProvider(statsConfig, _nodeFactory, _logManager), networkStorage, _nodeFactory, _configurationProvider, new PerfService(_logManager), _logManager);
            _peerManager.Init(true);
        }

        [Test]
        public void OurPeerBecomesActiveAndDisconnectTest()
        {
            var p2pSession = InitializeNode();

            //trigger p2p initialization
            var p2pProtocol = new P2PProtocolHandler(p2pSession, new MessageSerializationService(), p2pSession.RemoteNodeId, p2pSession.RemotePort ?? 0, _logManager, new PerfService(_logManager));
            var p2pArgs = new P2PProtocolInitializedEventArgs(p2pProtocol)
            {
                P2PVersion = 4,
                Capabilities = new[] {new Capability(Protocol.Eth, 62)}.ToList(),
            };
            p2pSession.TriggerProtocolInitialized(p2pArgs);
            AssertTrue(() => _peerManager.ActivePeers.First().NodeStats.DidEventHappen(NodeStatsEventType.P2PInitialized), 5000);
            //Assert.IsTrue();

            //trigger eth62 initialization
            var eth62 = new Eth62ProtocolHandler(p2pSession, new MessageSerializationService(), _synchronizationManager, _logManager, new PerfService(_logManager));
            var args = new Eth62ProtocolInitializedEventArgs(eth62)
            {
                ChainId = _synchronizationManager.ChainId
            };
            p2pSession.TriggerProtocolInitialized(args);
            AssertTrue(() => _peerManager.ActivePeers.First().NodeStats.DidEventHappen(NodeStatsEventType.Eth62Initialized), 5000);
            //Assert.IsTrue(_peerManager.ActivePeers.First().NodeStats.DidEventHappen(NodeStatsEventType.Eth62Initialized));
            AssertTrue(() => _peerManager.ActivePeers.First().SynchronizationPeer != null, 5000);
            //Assert.NotNull(_peerManager.ActivePeers.First().SynchronizationPeer);

            //verify active peer was added to synch manager
            _synchronizationManager.Received(1).AddPeer(Arg.Any<ISynchronizationPeer>());

            //trigger disconnect
            p2pSession.TriggerPeerDisconnected();

            //verify active peer was removed from synch manager
            AssertTrue(() => _peerManager.ActivePeers.Count == 0, 5000);
            Assert.AreEqual(1, _peerManager.CandidatePeers.Count);
            //Assert.AreEqual(0, _peerManager.ActivePeers.Count);
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
            p2pSession.ConnectionDirection = ConnectionDirection.In;
            _localPeer.TriggerSessionCreated(p2pSession);
            p2pSession.TriggerHandshakeComplete();

            //trigger p2p initialization
            var p2pProtocol = new P2PProtocolHandler(p2pSession, new MessageSerializationService(), p2pSession.RemoteNodeId, p2pSession.RemotePort ?? 0, _logManager, new PerfService(_logManager));
            var p2pArgs = new P2PProtocolInitializedEventArgs(p2pProtocol)
            {
                P2PVersion = 4,
                Capabilities = new[] { new Capability(Protocol.Eth, 62) }.ToList()
            };
            p2pSession.TriggerProtocolInitialized(p2pArgs);
            AssertTrue(() => _peerManager.ActivePeers.First().NodeStats.DidEventHappen(NodeStatsEventType.P2PInitialized), 5000);
            //Assert.IsTrue(_peerManager.ActivePeers.First().NodeStats.DidEventHappen(NodeStatsEventType.P2PInitialized));

            //trigger eth62 initialization
            var eth62 = new Eth62ProtocolHandler(p2pSession, new MessageSerializationService(), _synchronizationManager, _logManager, new PerfService(_logManager));
            var args = new Eth62ProtocolInitializedEventArgs(eth62)
            {
                ChainId = _synchronizationManager.ChainId
            };
            p2pSession.TriggerProtocolInitialized(args);
            AssertTrue(() => _peerManager.ActivePeers.First().NodeStats.DidEventHappen(NodeStatsEventType.Eth62Initialized), 5000);

            Assert.AreEqual(1, _peerManager.CandidatePeers.Count);
            Assert.AreEqual(1, _peerManager.ActivePeers.Count);
            //Assert.IsTrue(_peerManager.ActivePeers.First().NodeStats.DidEventHappen(NodeStatsEventType.Eth62Initialized));
            AssertTrue(() => _peerManager.ActivePeers.First().SynchronizationPeer != null, 5000);
            //Assert.NotNull(_peerManager.ActivePeers.First().SynchronizationPeer);

            //verify active peer was added to synch manager
            _synchronizationManager.Received(1).AddPeer(Arg.Any<ISynchronizationPeer>());

            //trigger disconnect
            p2pSession.TriggerPeerDisconnected();
            AssertTrue(() => _peerManager.ActivePeers.Count == 0, 5000);
            Assert.AreEqual(1, _peerManager.CandidatePeers.Count);
            //Assert.AreEqual(0, _peerManager.ActivePeers.Count);

            //verify active peer was removed from synch manager
            _synchronizationManager.Received(1).RemovePeer(Arg.Any<ISynchronizationPeer>());
        }

        [Test]
        public void DisconnectOnWrongP2PVersionTest()
        {
            var p2pSession = InitializeNode();

            //trigger p2p initialization
            var p2pProtocol = new P2PProtocolHandler(p2pSession, new MessageSerializationService(), p2pSession.RemoteNodeId, p2pSession.RemotePort??0, _logManager, new PerfService(_logManager));
            var p2pArgs = new P2PProtocolInitializedEventArgs(p2pProtocol)
            {
                P2PVersion = 1,
                Capabilities = new[] { new Capability(Protocol.Eth, 62) }.ToList()
            };
            p2pSession.TriggerProtocolInitialized(p2pArgs);
            AssertTrue(() => p2pSession.Disconected, 5000);
            //Assert.IsTrue(p2pSession.Disconected);
            Assert.AreEqual(DisconnectReason.IncompatibleP2PVersion, p2pSession.DisconnectReason);
        }

        [Test]
        public void DisconnectOnNoEth62SupportTest()
        {
            var p2pSession = InitializeNode();

            //trigger p2p initialization
            var p2pProtocol = new P2PProtocolHandler(p2pSession, new MessageSerializationService(), p2pSession.RemoteNodeId, p2pSession.RemotePort ?? 0, _logManager, new PerfService(_logManager));
            var p2pArgs = new P2PProtocolInitializedEventArgs(p2pProtocol)
            {
                P2PVersion = 5,
                Capabilities = new[] { new Capability(Protocol.Eth, 60) }.ToList()
            };
            p2pSession.TriggerProtocolInitialized(p2pArgs);
            AssertTrue(() => p2pSession.Disconected, 5000);
            //Assert.IsTrue(p2pSession.Disconected);
            Assert.AreEqual(DisconnectReason.Other, p2pSession.DisconnectReason);
        }

        [Test]
        public void DisconnectOnWrongChainIdTest()
        {
            var p2pSession = InitializeNode();

            //trigger eth62 initialization
            var eth62 = new Eth62ProtocolHandler(p2pSession, new MessageSerializationService(), _synchronizationManager, _logManager, new PerfService(_logManager));
            var args = new Eth62ProtocolInitializedEventArgs(eth62)
            {
                ChainId = 100
            };
            p2pSession.TriggerProtocolInitialized(args);
            AssertTrue(() => p2pSession.Disconected, 5000);
            //Assert.IsTrue(p2pSession.Disconected);
            Assert.AreEqual(DisconnectReason.Other, p2pSession.DisconnectReason);
        }

        [Test]
        public void DisconnectOnAlreadyConnectedTest()
        {
            var p2pSession = InitializeNode(ConnectionDirection.In);

            AssertTrue(() => p2pSession.Disconected, 5000);
            //Assert.IsTrue(p2pSession.Disconected);
            Assert.AreEqual(DisconnectReason.AlreadyConnected, p2pSession.DisconnectReason);
        }

        [Test]
        public void DisconnectOnTooManyPeersTest()
        {
            var node = _nodeFactory.CreateNode("192.1.1.1", 3333);
            ((NetworkConfig)_configurationProvider.GetConfig<INetworkConfig>()).ActivePeersMaxCount = 0;

            //trigger connection initialized
            var p2pSession = new TestP2PSession();
            p2pSession.RemoteNodeId = node.Id;
            p2pSession.ConnectionDirection = ConnectionDirection.In;
            _localPeer.TriggerSessionCreated(p2pSession);
            p2pSession.TriggerHandshakeComplete();

            AssertTrue(() => p2pSession.Disconected, 5000);
            //Assert.IsTrue(p2pSession.Disconected);
            Assert.AreEqual(DisconnectReason.TooManyPeers, p2pSession.DisconnectReason);
        }

        private TestP2PSession InitializeNode(ConnectionDirection connectionDirection = ConnectionDirection.Out)
        {
            var node = _nodeFactory.CreateNode("192.1.1.1", 3333);

            _discoveryManager.GetNodeLifecycleManager(node);
            var task = _peerManager.Start();
            task.Wait();

            //verify new peer is added
            Assert.AreEqual(1, _peerManager.CandidatePeers.Count);
            Assert.AreEqual(node.Id, _peerManager.CandidatePeers.First().Node.Id);

            //trigger connection start
            task = _peerManager.RunPeerUpdate();
            task.Wait();
            Assert.AreEqual(1, _localPeer.ConnectionAsyncCallsCounter);
            Assert.AreEqual(1, _peerManager.CandidatePeers.Count);
            Assert.AreEqual(1, _peerManager.ActivePeers.Count);

            //trigger connection initialized
            var p2pSession = new TestP2PSession();
            p2pSession.RemoteNodeId = node.Id;
            p2pSession.ConnectionDirection = connectionDirection;
            _localPeer.TriggerSessionCreated(p2pSession);

            p2pSession.TriggerHandshakeComplete();

            var peer = _peerManager.ActivePeers.First();
            if (connectionDirection == ConnectionDirection.Out)
            {
                Assert.IsNotNull(peer.Session);
            }

            return p2pSession;
        }

        private void AssertTrue(Func<bool> check, int timeout)
        {
            var checkThreshold = 200;
            if (timeout < checkThreshold)
            {
                Assert.IsTrue(check.Invoke());
                return;
            }

            var waitTime = checkThreshold;
            while (waitTime < timeout)
            {
                if (check.Invoke())
                {
                    return;
                }

                waitTime = waitTime + checkThreshold;
                var task = Task.Delay(checkThreshold);
                task.Wait();
            }
            Assert.IsTrue(check.Invoke());
        }
    }

    public class TestP2PSession : IP2PSession
    {
        public bool Disconected { get; set; }
        public DisconnectReason DisconnectReason { get; set; }

        public NodeId RemoteNodeId { get; set; }
        public string RemoteHost { get; set; }
        public int? RemotePort { get; set; }
        public ConnectionDirection ConnectionDirection { get; set; }

        public string SessionId { get; }
        public INodeStats NodeStats { get; set; }

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
            //var task = Task.Delay(1000);
            //task.Wait();
        }

        public void TriggerPeerDisconnected()
        {
            PeerDisconnected?.Invoke(this, new DisconnectEventArgs(DisconnectReason.TooManyPeers, DisconnectType.Local));
            //var task = Task.Delay(1000);
            //task.Wait();
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

        public Task DisconnectAsync(DisconnectReason disconnectReason, DisconnectType disconnectType)
        {
            return Task.CompletedTask;
        }

        public void Handshake()
        {
            HandshakeComplete?.Invoke(this, EventArgs.Empty);
            var task = Task.Delay(1000);
            task.Wait();
        }

        public event EventHandler<DisconnectEventArgs> PeerDisconnected;
        public event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized;
        public event EventHandler<EventArgs> HandshakeComplete;
        
        public void TriggerPeerDisconnected(DisconnectReason reason, DisconnectType disconnectType)
        {
            PeerDisconnected?.Invoke(this, new DisconnectEventArgs(reason, disconnectType));
            var task = Task.Delay(1000);
            task.Wait();
        }

        public void TriggerHandshakeComplete()
        {
            HandshakeComplete?.Invoke(this, EventArgs.Empty);
            var task = Task.Delay(1000);
            task.Wait();
        }
    }

    public class TestRlpxPeer : IRlpxPeer
    {
        public int ConnectionAsyncCallsCounter = 0;

        public Task Shutdown()
        {
            return Task.CompletedTask;
        }

        public Task Init()
        {
            return Task.CompletedTask;
        }

        public Task ConnectAsync(NodeId remoteNodeId, string remoteHost, int remotePort, INodeStats nodeStats)
        {
            ConnectionAsyncCallsCounter++;
            return Task.CompletedTask;
        }

        public void TriggerSessionCreated(IP2PSession session)
        {
            SessionCreated?.Invoke(this, new SessionEventArgs(session));
        }
        
        public event EventHandler<SessionEventArgs> SessionCreated;
    }
}