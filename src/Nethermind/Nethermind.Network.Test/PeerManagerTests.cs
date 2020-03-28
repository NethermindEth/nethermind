//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using FluentAssertions;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx;
using Nethermind.Network.StaticNodes;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test
{
    [TestFixture]
    // [Explicit("Repeatedly fails on Travis")]
    public class PeerManagerTests
    {
        private RlpxMock _rlpxPeer;
        private IDiscoveryApp _discoveryApp;
        private INodeStatsManager _stats;
        private INetworkStorage _storage;
        private PeerLoader _peerLoader;
        private PeerManager _peerManager;
        private INetworkConfig _networkConfig;
        private IStaticNodesManager _staticNodesManager;

        private class RlpxMock : IRlpxPeer
        {
            private readonly List<Session> _sessions;

            public RlpxMock(List<Session> sessions)
            {
                _sessions = sessions;
            }

            public Task Init()
            {
                return Task.CompletedTask;
            }

            public Task ConnectAsync(Node node)
            {
                if (_isFailing)
                {
                    throw new InvalidOperationException("making it fail");
                }

                lock (this)
                {
                    ConnectAsyncCallsCount++;
                }

                var session = new Session(30313, LimboLogs.Instance, Substitute.For<IChannel>(), node);
                lock (_sessions)
                {
                    _sessions.Add(session);
                }

                SessionCreated?.Invoke(this, new SessionEventArgs(session));
                return Task.CompletedTask;
            }

            public void CreateRandomIncoming()
            {
                var session = new Session(30313, LimboLogs.Instance, Substitute.For<IChannel>());
                lock (_sessions)
                {
                    _sessions.Add(session);
                }

                session.RemoteHost = "1.2.3.4";
                session.RemotePort = 12345;
                SessionCreated?.Invoke(this, new SessionEventArgs(session));
                session.Handshake(new PrivateKeyGenerator().Generate().PublicKey);
            }

            public int ConnectAsyncCallsCount { get; set; }

            public Task Shutdown()
            {
                return Task.CompletedTask;
            }

            public PublicKey LocalNodeId { get; } = TestItem.PublicKeyA;
            public int LocalPort { get; }
            public event EventHandler<SessionEventArgs> SessionCreated;

            public void CreateIncoming(params Session[] sessions)
            {
                List<Session> incomingSessions = new List<Session>();
                foreach (Session session in sessions)
                {
                    var sessionIn = new Session(30313, LimboLogs.Instance, Substitute.For<IChannel>());
                    sessionIn.RemoteHost = session.RemoteHost;
                    sessionIn.RemotePort = session.RemotePort;
                    SessionCreated?.Invoke(this, new SessionEventArgs(sessionIn));
                    sessionIn.Handshake(session.RemoteNodeId);
                    incomingSessions.Add(sessionIn);
                }

                lock (_sessions)
                {
                    _sessions.AddRange(incomingSessions);
                }
            }

            private bool _isFailing = false;

            public void MakeItFail()
            {
                _isFailing = true;
            }
        }

        private class InMemoryStorage : INetworkStorage
        {
            private ConcurrentDictionary<PublicKey, NetworkNode> _nodes = new ConcurrentDictionary<PublicKey, NetworkNode>();

            public NetworkNode[] GetPersistedNodes()
            {
                return _nodes.Values.ToArray();
            }

            public void UpdateNode(NetworkNode node)
            {
                _nodes[node.NodeId] = node;
                _pendingChanges = true;
            }

            public void UpdateNodes(IEnumerable<NetworkNode> nodes)
            {
                foreach (NetworkNode node in nodes)
                {
                    UpdateNode(node);
                }
            }

            public void RemoveNodes(NetworkNode[] nodes)
            {
                _pendingChanges = true;
            }

            public void StartBatch()
            {
            }

            public void Commit()
            {
            }

            private bool _pendingChanges;

            public bool AnyPendingChange()
            {
                return _pendingChanges;
            }
        }

        [SetUp]
        public void SetUp()
        {
            _rlpxPeer = new RlpxMock(_sessions);
            _discoveryApp = Substitute.For<IDiscoveryApp>();
            _stats = new NodeStatsManager(new StatsConfig(), LimboLogs.Instance);
            _storage = new InMemoryStorage();
            _peerLoader = new PeerLoader(new NetworkConfig(), new DiscoveryConfig(), _stats, _storage, LimboLogs.Instance);
            _networkConfig = new NetworkConfig();
            _networkConfig.ActivePeersMaxCount = 25;
            _networkConfig.PeersPersistenceInterval = 50;
            _staticNodesManager = Substitute.For<IStaticNodesManager>();
            _peerManager = new PeerManager(_rlpxPeer, _discoveryApp, _stats, _storage, _peerLoader, _networkConfig,
                LimboLogs.Instance, _staticNodesManager);
        }

        [TearDown]
        public async Task TearDown()
        {
            await _peerManager.StopAsync();
        }

        [Test]
        public async Task Can_start_and_stop()
        {
            _peerManager.Start();
            await _peerManager.StopAsync();
        }

        private const string enodesString = enode1String + "," + enode2String;
        private const string enode1String = "enode://22222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222@51.141.78.53:30303";
        private const string enode2String = "enode://1111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111b@52.141.78.53:30303";

        private void SetupPersistedPeers(int count)
        {
            _storage.UpdateNodes(CreateNodes(count));
        }

        [Test]
        public void Will_connect_to_a_candidate_node()
        {
            SetupPersistedPeers(1);
            _peerManager.Init();
            _peerManager.Start();
            Thread.Sleep(_travisDelay);
            Assert.AreEqual(1, _rlpxPeer.ConnectAsyncCallsCount);
        }

        [Test]
        public void Will_only_connect_up_to_max_peers()
        {
            SetupPersistedPeers(50);
            _peerManager.Init();
            _peerManager.Start();
            Thread.Sleep(_travisDelay);
            Assert.AreEqual(25, _rlpxPeer.ConnectAsyncCallsCount);
        }
        
        [Test]
        public void Will_discard_a_duplicate_incoming_session()
        {
            _peerManager.Init();
            Session session1 = new Session(30303, LimboLogs.Instance, Substitute.For<IChannel>());
            Session session2 = new Session(30303, LimboLogs.Instance, Substitute.For<IChannel>());
            session1.RemoteHost = "1.2.3.4";
            session1.RemotePort = 12345;
            session1.RemoteNodeId = TestItem.PublicKeyA;
            session2.RemoteHost = "1.2.3.4";
            session2.RemotePort = 12345;
            session2.RemoteNodeId = TestItem.PublicKeyA;
            
            _rlpxPeer.CreateIncoming(session1, session2);
            _peerManager.ActivePeers.Count.Should().Be(1);
        }
        
        [TestCase(true, ConnectionDirection.In)]
        [TestCase(false, ConnectionDirection.In)]
        // [TestCase(true, ConnectionDirection.Out)] // cannot create an active peer waiting for the test
        [TestCase(false, ConnectionDirection.Out)]
        public void Will_agree_on_which_session_to_disconnect_when_connecting_at_once(bool shouldLose, ConnectionDirection firstDirection)
        {
            _peerManager.Init();
            Session session1 = new Session(30303, LimboLogs.Instance, Substitute.For<IChannel>());
            session1.RemoteHost = "1.2.3.4";
            session1.RemotePort = 12345;
            session1.RemoteNodeId = shouldLose ? TestItem.PublicKeyA : TestItem.PublicKeyC;

            if (firstDirection == ConnectionDirection.In)
            {
                _rlpxPeer.CreateIncoming(session1);
                _rlpxPeer.ConnectAsync(session1.Node);
                if(session1.State < SessionState.HandshakeComplete) session1.Handshake(session1.Node.Id);
                (_peerManager.ActivePeers.First().OutSession?.IsClosing ?? true).Should().Be(shouldLose);
                (_peerManager.ActivePeers.First().InSession?.IsClosing ?? true).Should().Be(!shouldLose);
            }
            else
            {
                _rlpxPeer.SessionCreated += HandshakeOnCreate;
                _rlpxPeer.ConnectAsync(session1.Node);
                _rlpxPeer.SessionCreated -= HandshakeOnCreate;
                _rlpxPeer.CreateIncoming(session1);
                (_peerManager.ActivePeers.First().OutSession?.IsClosing ?? true).Should().Be(!shouldLose);
                (_peerManager.ActivePeers.First().InSession?.IsClosing ?? true).Should().Be(shouldLose);
            }

            _peerManager.ActivePeers.Count.Should().Be(1);
        }

        private void HandshakeOnCreate(object sender, SessionEventArgs e)
        {
            e.Session.Handshake(e.Session.RemoteNodeId);
        }

        private List<Session> _sessions = new List<Session>();

        [Test, Retry(5)]
        public void Will_fill_up_on_disconnects()
        {
            SetupPersistedPeers(50);
            _peerManager.Init();
            _peerManager.Start();
            Thread.Sleep(_travisDelayLong);
            Assert.AreEqual(25, _rlpxPeer.ConnectAsyncCallsCount);
            DisconnectAllSessions();

            Thread.Sleep(_travisDelayLong);
            Assert.AreEqual(50, _rlpxPeer.ConnectAsyncCallsCount);
        }

        [Test, Retry(5)]
        public void Ok_if_fails_to_connect()
        {
            SetupPersistedPeers(50);
            _rlpxPeer.MakeItFail();
            _peerManager.Init();
            _peerManager.Start();

            Thread.Sleep(_travisDelay);
            Assert.AreEqual(0, _peerManager.ActivePeers.Count);
        }

        [Test, Retry(3)]
        public void Will_fill_up_over_and_over_again_on_disconnects()
        {
            SetupPersistedPeers(50);
            _peerManager.Init();
            _peerManager.Start();

            int currentCount = 0;
            for (int i = 0; i < 10; i++)
            {
                currentCount += 25;
                Thread.Sleep(_travisDelayLong);
                Assert.AreEqual(currentCount, _rlpxPeer.ConnectAsyncCallsCount);
                DisconnectAllSessions();
            }
        }

        [Test]
        public void Will_fill_up_over_and_over_again_on_newly_discovered()
        {
            SetupPersistedPeers(0);
            _peerManager.Init();
            _peerManager.Start();

            for (int i = 0; i < 10; i++)
            {
                DiscoverNew(25);
                Thread.Sleep(_travisDelay);
                Assert.AreEqual(25, _peerManager.ActivePeers.Count);
            }
        }

        private int _travisDelay = 100;

        private int _travisDelayLong = 1000;

        [Test]
        [Ignore("Behaviour changed that allows peers to go over max if awaiting response")]
        public void Will_fill_up_with_incoming_over_and_over_again_on_disconnects()
        {
            SetupPersistedPeers(0);
            _peerManager.Init();
            _peerManager.Start();

            for (int i = 0; i < 10; i++)
            {
                CreateNewIncomingSessions(25);
                Thread.Sleep(_travisDelay);
                Assert.AreEqual(25, _peerManager.ActivePeers.Count);
            }
        }

        [Test, Retry(3)]
        public async Task Will_fill_up_over_and_over_again_on_disconnects_and_when_ids_keep_changing()
        {
            SetupPersistedPeers(50);
            _peerManager.Init();
            _peerManager.Start();

            int currentCount = 0;
            for (int i = 0; i < 10; i++)
            {
                currentCount += 25;
                Thread.Sleep(_travisDelay);
                Assert.AreEqual(currentCount, _rlpxPeer.ConnectAsyncCallsCount);
                HandshakeAllSessions();
                Thread.Sleep(_travisDelay);
                DisconnectAllSessions();
            }

            await _peerManager.StopAsync();
            DisconnectAllSessions();

            Assert.True(_peerManager.CandidatePeers.All(p => p.OutSession == null));
        }

        [Test, Retry(3)]
        public void Will_fill_up_over_and_over_again_on_disconnects_and_when_ids_keep_changing_with_max_candidates_40()
        {
            _networkConfig.MaxCandidatePeerCount = 40;
            _networkConfig.CandidatePeerCountCleanupThreshold = 30;
            _networkConfig.PersistedPeerCountCleanupThreshold = 40;
            SetupPersistedPeers(50);
            _peerManager.Init();
            _peerManager.Start();

            int currentCount = 0;
            for (int i = 0; i < 10; i++)
            {
                currentCount += 25;
                Thread.Sleep(_travisDelayLong);
                Assert.AreEqual(currentCount, _rlpxPeer.ConnectAsyncCallsCount);
                HandshakeAllSessions();
                Thread.Sleep(_travisDelay);
                DisconnectAllSessions();
            }
        }

        [Test, Retry(3)]
        public void Will_fill_up_over_and_over_again_on_disconnects_and_when_ids_keep_changing_with_max_candidates_40_with_random_incoming_connections()
        {
            _networkConfig.MaxCandidatePeerCount = 40;
            _networkConfig.CandidatePeerCountCleanupThreshold = 30;
            _networkConfig.PersistedPeerCountCleanupThreshold = 40;
            SetupPersistedPeers(50);
            _peerManager.Init();
            _peerManager.Start();

            int currentCount = 0;
            for (int i = 0; i < 10; i++)
            {
                currentCount += 25;
                Thread.Sleep(_travisDelayLong);
                Assert.AreEqual(currentCount, _rlpxPeer.ConnectAsyncCallsCount);
                HandshakeAllSessions();
                Thread.Sleep(_travisDelay);
                CreateIncomingSessions();
                Thread.Sleep(_travisDelay);
                DisconnectAllSessions();
            }
        }

        private List<NetworkNode> CreateNodes(int count)
        {
            var nodes = new List<NetworkNode>();
            for (int i = 0; i < count; i++)
            {
                var generator = new PrivateKeyGenerator();
                var enode = GenerateEnode(generator);
                NetworkNode node = new NetworkNode(enode);
                nodes.Add(node);
            }

            return nodes;
        }

        private static string GenerateEnode(PrivateKeyGenerator generator = null)
        {
            generator ??= new PrivateKeyGenerator();
            string enode = $"enode://{generator.Generate().PublicKey.ToString(false)}@52.141.78.53:30303";
            return enode;
        }

        private void CreateIncomingSessions()
        {
            Session[] clone;

            lock (_sessions)
            {
                clone = _sessions.ToArray();
            }

            _rlpxPeer.CreateIncoming(clone);
        }

        private void CreateNewIncomingSessions(int count)
        {
            for (int i = 0; i < count; i++)
            {
                _rlpxPeer.CreateRandomIncoming();
            }
        }

        private void DiscoverNew(int count)
        {
            for (int i = 0; i < count; i++)
            {
                _discoveryApp.NodeDiscovered += Raise.EventWith(new NodeEventArgs(new Node(new PrivateKeyGenerator().Generate().PublicKey, "1.2.3.4", 1234)));
            }
        }

        private void DisconnectAllSessions()
        {
            Session[] clone;
            lock (_sessions)
            {
                clone = _sessions.ToArray();
                _sessions.Clear();
            }

            foreach (Session session in clone)
            {
                session.MarkDisconnected(DisconnectReason.TooManyPeers, DisconnectType.Remote, "test");
            }
        }

        private void HandshakeAllSessions()
        {
            Session[] clone;
            lock (_sessions)
            {
                clone = _sessions.ToArray();
            }

            foreach (Session session in clone)
            {
                session.Handshake(new PrivateKeyGenerator().Generate().PublicKey);
            }
        }

        [Test]
        public void Will_load_static_nodes_and_connect_to_them()
        {
            const int nodesCount = 5;
            var staticNodes = CreateNodes(nodesCount);
            _staticNodesManager.Nodes.Returns(staticNodes);
            _peerManager.Init();
            _peerManager.Start();
            foreach (var node in staticNodes)
            {
                _discoveryApp.NodeDiscovered += Raise.EventWith(new NodeEventArgs(new Node(node.Host, node.Port)));
            }

            Thread.Sleep(_travisDelay);
            _peerManager.ActivePeers.Count(p => p.Node.IsStatic).Should().Be(nodesCount);
        }

        [Test, Retry(3)]
        public void Will_disconnect_on_remove_static_node()
        {
            const int nodesCount = 5;
            var disconnections = 0;
            var staticNodes = CreateNodes(nodesCount);
            _staticNodesManager.Nodes.Returns(staticNodes);
            _peerManager.Init();
            _peerManager.Start();
            Thread.Sleep(_travisDelay);

            void DisconnectHandler(object o, DisconnectEventArgs e) => disconnections++;
            _sessions.ForEach(s => s.Disconnected += DisconnectHandler);

            _staticNodesManager.NodeRemoved += Raise.EventWith(new NetworkNodeEventArgs(staticNodes.First()));

            _peerManager.ActivePeers.Count(p => p.Node.IsStatic).Should().Be(nodesCount - 1);
            disconnections.Should().Be(1);
        }

        [Test]
        public void Will_connect_and_disconnect_on_peer_management()
        {
            var disconnections = 0;
            _peerManager.Init();
            _peerManager.Start();
            var node = new NetworkNode(GenerateEnode());
            _peerManager.AddPeer(node);
            Thread.Sleep(_travisDelayLong);

            void DisconnectHandler(object o, DisconnectEventArgs e) => disconnections++;
            _peerManager.ActivePeers.Select(p => p.Node.Id).Should().BeEquivalentTo(node.NodeId);

            _sessions.ForEach(s => s.Disconnected += DisconnectHandler);

            _peerManager.RemovePeer(node).Should().BeTrue();
            _peerManager.ActivePeers.Should().BeEmpty();
            disconnections.Should().Be(1);
        }

        [Test]
        public void Will_only_add_same_peer_once()
        {
            _peerManager.Init();
            _peerManager.Start();
            var node = new NetworkNode(GenerateEnode());
            _peerManager.AddPeer(node);
            _peerManager.AddPeer(node);
            _peerManager.AddPeer(node);
            Thread.Sleep(_travisDelayLong);
            _peerManager.ActivePeers.Should().HaveCount(1);
        }

        [Test]
        public void RemovePeer_should_fail_if_peer_not_added()
        {
            _peerManager.Init();
            _peerManager.Start();
            var node = new NetworkNode(GenerateEnode());
            Thread.Sleep(_travisDelay);
            _peerManager.RemovePeer(node).Should().BeFalse();
        }
    }
}