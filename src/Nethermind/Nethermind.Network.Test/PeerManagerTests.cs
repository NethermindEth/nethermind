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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using FluentAssertions;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
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
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class PeerManagerTests
    {
        [Test]
        public async Task Can_start_and_stop()
        {
            await using Context ctx = new Context();
            ctx.PeerManager.Start();
            await ctx.PeerManager.StopAsync();
        }

        private const string enodesString = enode1String + "," + enode2String;

        private const string enode1String =
            "enode://22222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222@51.141.78.53:30303";

        private const string enode2String =
            "enode://1111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111b@52.141.78.53:30303";

        private const string enode3String =
            "enode://3333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333b@some.url";

        private const string enode4String =
            "enode://3333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333b@some.url:434";

        [Test, Retry(10)]
        public async Task Will_connect_to_a_candidate_node()
        {
            await using Context ctx = new Context();
            ctx.SetupPersistedPeers(1);
            ctx.PeerManager.Init();
            ctx.PeerManager.Start();
            await Task.Delay(_travisDelay);
            Assert.AreEqual(1, ctx.RlpxPeer.ConnectAsyncCallsCount);
        }

        [Test]
        public async Task Will_only_connect_up_to_max_peers()
        {
            await using Context ctx = new Context();
            ctx.SetupPersistedPeers(50);
            ctx.PeerManager.Init();
            ctx.PeerManager.Start();
            await Task.Delay(_travisDelayLong * 10);
            Assert.AreEqual(25, ctx.RlpxPeer.ConnectAsyncCallsCount);
        }

        [Test]
        public async Task Will_discard_a_duplicate_incoming_session()
        {
            await using Context ctx = new Context();
            ctx.PeerManager.Init();
            Session session1 = new Session(30303, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance,
                LimboLogs.Instance);
            Session session2 = new Session(30303, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance,
                LimboLogs.Instance);
            session1.RemoteHost = "1.2.3.4";
            session1.RemotePort = 12345;
            session1.RemoteNodeId = TestItem.PublicKeyA;
            session2.RemoteHost = "1.2.3.4";
            session2.RemotePort = 12345;
            session2.RemoteNodeId = TestItem.PublicKeyA;

            ctx.RlpxPeer.CreateIncoming(session1, session2);
            ctx.PeerManager.ActivePeers.Count.Should().Be(1);
        }

        [Test]
        public async Task Will_return_exception_in_port()
        {
            Assert.Throws<ArgumentException>(delegate
            {
                Enode enode = new Enode(enode3String);
            });
        }

        [Test]
        public async Task Will_return_exception_in_dns()
        {
            Assert.Throws<ArgumentException>(delegate
            {
                Enode enode = new Enode(enode4String);
            });
        }

        [Test]
        public async Task Will_accept_static_connection()
        {
            await using Context ctx = new Context();
            ctx.NetworkConfig.ActivePeersMaxCount = 1;
            ctx.StaticNodesManager.IsStatic(enode2String).Returns(true);

            ctx.PeerManager.Init();
            var enode1 = new Enode(enode1String);
            Node node1 = new Node(enode1.PublicKey, new IPEndPoint(enode1.HostIp, enode1.Port));
            Session session1 = new Session(30303, node1, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance,
                LimboLogs.Instance);

            var enode2 = new Enode(enode2String);
            Node node2 = new Node(enode2.PublicKey, new IPEndPoint(enode2.HostIp, enode2.Port));
            Session session2 = new Session(30303, node2, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance,
                LimboLogs.Instance);

            ctx.RlpxPeer.CreateIncoming(session1, session2);
            ctx.PeerManager.ActivePeers.Count.Should().Be(2);
        }

        [TestCase(true, ConnectionDirection.In)]
        [TestCase(false, ConnectionDirection.In)]
        // [TestCase(true, ConnectionDirection.Out)] // cannot create an active peer waiting for the test
        [TestCase(false, ConnectionDirection.Out)]
        public async Task Will_agree_on_which_session_to_disconnect_when_connecting_at_once(bool shouldLose,
            ConnectionDirection firstDirection)
        {
            await using Context ctx = new Context();
            ctx.PeerManager.Init();
            Session session1 = new Session(30303, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance,
                LimboLogs.Instance);
            session1.RemoteHost = "1.2.3.4";
            session1.RemotePort = 12345;
            session1.RemoteNodeId = shouldLose ? TestItem.PublicKeyA : TestItem.PublicKeyC;

            if (firstDirection == ConnectionDirection.In)
            {
                ctx.RlpxPeer.CreateIncoming(session1);
                await ctx.RlpxPeer.ConnectAsync(session1.Node);
                if (session1.State < SessionState.HandshakeComplete) session1.Handshake(session1.Node.Id);
                (ctx.PeerManager.ActivePeers.First().OutSession?.IsClosing ?? true).Should().Be(shouldLose);
                (ctx.PeerManager.ActivePeers.First().InSession?.IsClosing ?? true).Should().Be(!shouldLose);
            }
            else
            {
                ctx.RlpxPeer.SessionCreated += HandshakeOnCreate;
                await ctx.RlpxPeer.ConnectAsync(session1.Node);
                ctx.RlpxPeer.SessionCreated -= HandshakeOnCreate;
                ctx.RlpxPeer.CreateIncoming(session1);
                (ctx.PeerManager.ActivePeers.First().OutSession?.IsClosing ?? true).Should().Be(!shouldLose);
                (ctx.PeerManager.ActivePeers.First().InSession?.IsClosing ?? true).Should().Be(shouldLose);
            }

            ctx.PeerManager.ActivePeers.Count.Should().Be(1);
        }

        private void HandshakeOnCreate(object sender, SessionEventArgs e)
        {
            e.Session.Handshake(e.Session.RemoteNodeId);
        }

        [Test, Retry(5)]
        public async Task Will_fill_up_on_disconnects()
        {
            await using Context ctx = new Context();
            ctx.SetupPersistedPeers(50);
            ctx.PeerManager.Init();
            ctx.PeerManager.Start();
            await Task.Delay(_travisDelayLong);
            Assert.AreEqual(25, ctx.RlpxPeer.ConnectAsyncCallsCount);
            ctx.DisconnectAllSessions();

            await Task.Delay(_travisDelayLong);
            Assert.AreEqual(50, ctx.RlpxPeer.ConnectAsyncCallsCount);
        }

        [Test, Retry(5)]
        public async Task Ok_if_fails_to_connect()
        {
            await using Context ctx = new Context();
            ctx.SetupPersistedPeers(50);
            ctx.RlpxPeer.MakeItFail();
            ctx.PeerManager.Init();
            ctx.PeerManager.Start();

            await Task.Delay(_travisDelay);
            Assert.AreEqual(0, ctx.PeerManager.ActivePeers.Count);
        }

        [Test, Retry(3)]
        public async Task Will_fill_up_over_and_over_again_on_disconnects()
        {
            await using Context ctx = new Context();
            ctx.SetupPersistedPeers(50);
            ctx.PeerManager.Init();
            ctx.PeerManager.Start();

            int currentCount = 0;
            for (int i = 0; i < 10; i++)
            {
                currentCount += 25;
                await Task.Delay(_travisDelayLong);
                Assert.AreEqual(currentCount, ctx.RlpxPeer.ConnectAsyncCallsCount);
                ctx.DisconnectAllSessions();
            }
        }

        [Test]
        public async Task Will_fill_up_over_and_over_again_on_newly_discovered()
        {
            await using Context ctx = new Context();
            ctx.SetupPersistedPeers(0);
            ctx.PeerManager.Init();
            ctx.PeerManager.Start();

            for (int i = 0; i < 10; i++)
            {
                ctx.DiscoverNew(25);
                await Task.Delay(_travisDelay);
                Assert.AreEqual(25, ctx.PeerManager.ActivePeers.Count);
            }
        }

        private int _travisDelay = 100;

        private int _travisDelayLong = 1000;

        [Test]
        [Ignore("Behaviour changed that allows peers to go over max if awaiting response")]
        public async Task Will_fill_up_with_incoming_over_and_over_again_on_disconnects()
        {
            await using Context ctx = new Context();
            ctx.SetupPersistedPeers(0);
            ctx.PeerManager.Init();
            ctx.PeerManager.Start();

            for (int i = 0; i < 10; i++)
            {
                ctx.CreateNewIncomingSessions(25);
                await Task.Delay(_travisDelay);
                Assert.AreEqual(25, ctx.PeerManager.ActivePeers.Count);
            }
        }

        [Test, Retry(3)]
        public async Task Will_fill_up_over_and_over_again_on_disconnects_and_when_ids_keep_changing()
        {
            await using Context ctx = new Context();
            ctx.SetupPersistedPeers(50);
            ctx.PeerManager.Init();
            ctx.PeerManager.Start();

            int currentCount = 0;
            for (int i = 0; i < 10; i++)
            {
                currentCount += 25;
                await Task.Delay(_travisDelay);
                Assert.AreEqual(currentCount, ctx.RlpxPeer.ConnectAsyncCallsCount);
                ctx.HandshakeAllSessions();
                await Task.Delay(_travisDelay);
                ctx.DisconnectAllSessions();
            }

            await ctx.PeerManager.StopAsync();
            ctx.DisconnectAllSessions();

            Assert.True(ctx.PeerManager.CandidatePeers.All(p => p.OutSession == null));
        }

        [Test, Retry(3)]
        public async Task
            Will_fill_up_over_and_over_again_on_disconnects_and_when_ids_keep_changing_with_max_candidates_40()
        {
            await using Context ctx = new Context();
            ctx.NetworkConfig.MaxCandidatePeerCount = 40;
            ctx.NetworkConfig.CandidatePeerCountCleanupThreshold = 30;
            ctx.NetworkConfig.PersistedPeerCountCleanupThreshold = 40;
            ctx.SetupPersistedPeers(50);
            ctx.PeerManager.Init();
            ctx.PeerManager.Start();

            int currentCount = 0;
            for (int i = 0; i < 10; i++)
            {
                currentCount += 25;
                await Task.Delay(_travisDelayLong);
                Assert.AreEqual(currentCount, ctx.RlpxPeer.ConnectAsyncCallsCount);
                ctx.HandshakeAllSessions();
                await Task.Delay(_travisDelay);
                ctx.DisconnectAllSessions();
            }
        }

        [Test, Retry(3)]
        public async Task
            Will_fill_up_over_and_over_again_on_disconnects_and_when_ids_keep_changing_with_max_candidates_40_with_random_incoming_connections()
        {
            await using Context ctx = new Context();
            ctx.NetworkConfig.MaxCandidatePeerCount = 40;
            ctx.NetworkConfig.CandidatePeerCountCleanupThreshold = 30;
            ctx.NetworkConfig.PersistedPeerCountCleanupThreshold = 40;
            ctx.SetupPersistedPeers(50);
            ctx.PeerManager.Init();
            ctx.PeerManager.Start();

            int currentCount = 0;
            for (int i = 0; i < 10; i++)
            {
                currentCount += 25;
                await Task.Delay(_travisDelayLong);
                Assert.AreEqual(currentCount, ctx.RlpxPeer.ConnectAsyncCallsCount);
                ctx.HandshakeAllSessions();
                await Task.Delay(_travisDelay);
                ctx.CreateIncomingSessions();
                await Task.Delay(_travisDelay);
                ctx.DisconnectAllSessions();
            }
        }

        [Test]
        public async Task Will_load_static_nodes_and_connect_to_them()
        {
            await using Context ctx = new Context();
            const int nodesCount = 5;
            var staticNodes = ctx.CreateNodes(nodesCount);
            ctx.StaticNodesManager.Nodes.Returns(staticNodes);
            ctx.PeerManager.Init();
            ctx.PeerManager.Start();
            foreach (var node in staticNodes)
            {
                ctx.DiscoveryApp.NodeDiscovered += Raise.EventWith(new NodeEventArgs(new Node(node.Host, node.Port)));
            }

            await Task.Delay(_travisDelay);
            ctx.PeerManager.ActivePeers.Count(p => p.Node.IsStatic).Should().Be(nodesCount);
        }

        [Test, Retry(5)]
        public async Task Will_disconnect_on_remove_static_node()
        {
            await using Context ctx = new Context();
            const int nodesCount = 5;
            var disconnections = 0;
            var staticNodes = ctx.CreateNodes(nodesCount);
            ctx.StaticNodesManager.Nodes.Returns(staticNodes);
            ctx.PeerManager.Init();
            ctx.PeerManager.Start();
            await Task.Delay(_travisDelay);

            void DisconnectHandler(object o, DisconnectEventArgs e) => disconnections++;
            ctx.Sessions.ForEach(s => s.Disconnected += DisconnectHandler);

            ctx.StaticNodesManager.NodeRemoved += Raise.EventWith(new NetworkNodeEventArgs(staticNodes.First()));

            ctx.PeerManager.ActivePeers.Count(p => p.Node.IsStatic).Should().Be(nodesCount - 1);
            disconnections.Should().Be(1);
        }

        [Test]
        public async Task Will_connect_and_disconnect_on_peer_management()
        {
            await using Context ctx = new Context();
            var disconnections = 0;
            ctx.PeerManager.Init();
            ctx.PeerManager.Start();
            var node = new NetworkNode(ctx.GenerateEnode());
            ctx.PeerManager.AddPeer(node);
            await Task.Delay(_travisDelayLong);

            void DisconnectHandler(object o, DisconnectEventArgs e) => disconnections++;
            ctx.PeerManager.ActivePeers.Select(p => p.Node.Id).Should().BeEquivalentTo(node.NodeId);

            ctx.Sessions.ForEach(s => s.Disconnected += DisconnectHandler);

            ctx.PeerManager.RemovePeer(node).Should().BeTrue();
            ctx.PeerManager.ActivePeers.Should().BeEmpty();
            disconnections.Should().Be(1);
        }

        [Test]
        public async Task Will_only_add_same_peer_once()
        {
            await using Context ctx = new Context();
            ctx.PeerManager.Init();
            ctx.PeerManager.Start();
            var node = new NetworkNode(ctx.GenerateEnode());
            ctx.PeerManager.AddPeer(node);
            ctx.PeerManager.AddPeer(node);
            ctx.PeerManager.AddPeer(node);
            await Task.Delay(_travisDelayLong);
            ctx.PeerManager.ActivePeers.Should().HaveCount(1);
        }

        [Test]
        public async Task RemovePeer_should_fail_if_peer_not_added()
        {
            await using Context ctx = new Context();
            ctx.PeerManager.Init();
            ctx.PeerManager.Start();
            var node = new NetworkNode(ctx.GenerateEnode());
            await Task.Delay(_travisDelay);
            ctx.PeerManager.RemovePeer(node).Should().BeFalse();
        }

        private class Context : IAsyncDisposable
        {
            public RlpxMock RlpxPeer { get; }
            public IDiscoveryApp DiscoveryApp { get; }
            public INodeStatsManager Stats { get; }
            public INetworkStorage Storage { get; }
            public PeerLoader PeerLoader { get; }
            public PeerManager PeerManager { get; }
            public INetworkConfig NetworkConfig { get; }
            public IStaticNodesManager StaticNodesManager { get; }
            public List<Session> Sessions { get; } = new List<Session>();

            public Context()
            {
                RlpxPeer = new RlpxMock(Sessions);
                DiscoveryApp = Substitute.For<IDiscoveryApp>();
                ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
                Stats = new NodeStatsManager(timerFactory, LimboLogs.Instance);
                Storage = new InMemoryStorage();
                PeerLoader = new PeerLoader(new NetworkConfig(), new DiscoveryConfig(), Stats, Storage,
                    LimboLogs.Instance);
                NetworkConfig = new NetworkConfig();
                NetworkConfig.ActivePeersMaxCount = 25;
                NetworkConfig.PeersPersistenceInterval = 50;
                StaticNodesManager = Substitute.For<IStaticNodesManager>();
                PeerManager = new PeerManager(RlpxPeer, DiscoveryApp, Stats, Storage, PeerLoader, NetworkConfig,
                    LimboLogs.Instance, StaticNodesManager);
            }

            public void SetupPersistedPeers(int count)
            {
                Storage.UpdateNodes(CreateNodes(count));
            }

            public void CreateIncomingSessions()
            {
                Session[] clone;

                lock (Sessions)
                {
                    clone = Sessions.ToArray();
                }

                RlpxPeer.CreateIncoming(clone);
            }

            public void HandshakeAllSessions()
            {
                Session[] clone;
                lock (Sessions)
                {
                    clone = Sessions.ToArray();
                }

                foreach (Session session in clone)
                {
                    session.Handshake(new PrivateKeyGenerator().Generate().PublicKey);
                }
            }

            public void CreateNewIncomingSessions(int count)
            {
                for (int i = 0; i < count; i++)
                {
                    RlpxPeer.CreateRandomIncoming();
                }
            }

            public List<NetworkNode> CreateNodes(int count)
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

            public void DiscoverNew(int count)
            {
                for (int i = 0; i < count; i++)
                {
                    DiscoveryApp.NodeDiscovered +=
                        Raise.EventWith(new NodeEventArgs(new Node(new PrivateKeyGenerator().Generate().PublicKey,
                            "1.2.3.4", 1234)));
                }
            }

            public void DisconnectAllSessions()
            {
                Session[] clone;
                lock (Sessions)
                {
                    clone = Sessions.ToArray();
                    Sessions.Clear();
                }

                foreach (Session session in clone)
                {
                    session.MarkDisconnected(DisconnectReason.TooManyPeers, DisconnectType.Remote, "test");
                }
            }

            public string GenerateEnode(PrivateKeyGenerator generator = null)
            {
                generator ??= new PrivateKeyGenerator();
                string enode = $"enode://{generator.Generate().PublicKey.ToString(false)}@52.141.78.53:30303";
                return enode;
            }

            public async ValueTask DisposeAsync()
            {
                await PeerManager.StopAsync();
            }
        }

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

                var session = new Session(30313, node, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance,
                    LimboLogs.Instance);
                lock (_sessions)
                {
                    _sessions.Add(session);
                }

                SessionCreated?.Invoke(this, new SessionEventArgs(session));
                return Task.CompletedTask;
            }

            public void CreateRandomIncoming()
            {
                var session = new Session(30313, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance,
                    LimboLogs.Instance);
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
                    var sessionIn = new Session(30313, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance,
                        LimboLogs.Instance);
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
            private ConcurrentDictionary<PublicKey, NetworkNode> _nodes =
                new ConcurrentDictionary<PublicKey, NetworkNode>();

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

            public void RemoveNode(PublicKey nodeId)
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

            public int PersistedNodesCount => _nodes.Count;

            public bool AnyPendingChange()
            {
                return _pendingChanges;
            }
        }
    }
}
