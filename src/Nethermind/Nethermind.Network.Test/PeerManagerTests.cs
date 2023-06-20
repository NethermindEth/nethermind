// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.P2P.EventArg;
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
            await using Context ctx = new();
            ctx.PeerManager.Start();
            await ctx.PeerManager.StopAsync();
        }

        private const string enode1String =
            "enode://22222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222@51.141.78.53:30303";

        private const string enode2String =
            "enode://1111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111b@52.141.78.53:30303";

        private const string enode3String =
            "enode://3333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333b@some.url";

        private const string enode4String =
            "enode://3333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333b@some.url:434";

        private const string enode5String =
            "enode://3333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333b@52.141.78.53";

        private const string enode6String =
            "enode://3333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333b@52.141.78.53:12345";

        private const string enode7String =
            "enode://3333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333b@52.141.78.53:12345?discport=6789";

        private const string enode8String =
            "enode://3333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333b@52.141.78.53:12345?somethingwrong=6789";

        private const string enode9String =
            "enode://3333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333b@52.141.78.53:12345?discport=6789?discport=67899";

        private const string enode10String =
            "enode://3333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333b@52.141.78.53:12345:discport=6789";

        [Test, Retry(10)]
        public async Task Will_connect_to_a_candidate_node()
        {
            await using Context ctx = new();
            ctx.SetupPersistedPeers(1);
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();
            await Task.Delay(_travisDelay);
            Assert.That(ctx.RlpxPeer.ConnectAsyncCallsCount, Is.EqualTo(1));
        }

        [Test]
        public async Task Will_only_connect_up_to_max_peers()
        {
            await using Context ctx = new(1);
            ctx.SetupPersistedPeers(50);
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();
            await Task.Delay(_travisDelayLong);

            int expectedConnectCount = 25;
            Assert.That(
                () => ctx.RlpxPeer.ConnectAsyncCallsCount,
                Is
                    .InRange(expectedConnectCount, expectedConnectCount + 1)
                    .After(_travisDelay * 10, 10));
        }

        [Test]
        public async Task Will_discard_a_duplicate_incoming_session()
        {
            await using Context ctx = new();
            ctx.PeerManager.Start();
            Session session1 = new(30303, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance,
                LimboLogs.Instance);
            Session session2 = new(30303, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance,
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
        public void Will_return_exception_in_port()
        {
            Assert.Throws<ArgumentException>(delegate
            {
                Enode unused = new(enode3String);
            });
        }

        [Test]
        public void Will_return_exception_in_dns()
        {
            Assert.Throws<ArgumentException>(delegate
            {
                Enode unused = new(enode4String);
            });
        }

        [Test]
        public void Will_return_exception_when_there_is_no_port()
        {
            Assert.Throws<ArgumentException>(delegate
            {
                Enode unused = new(enode5String);
            });
        }

        [Test]
        public void Will_parse_ports_correctly_when_there_are_two_different_ports()
        {
            Enode enode = new(enode6String);
            enode.Port.Should().Be(12345);
            enode.DiscoveryPort.Should().Be(12345);
        }

        [Test]
        public void Will_parse_port_correctly_when_there_is_only_one()
        {
            Enode enode = new(enode7String);
            enode.Port.Should().Be(12345);
            enode.DiscoveryPort.Should().Be(6789);
        }

        [Test]
        public void Will_return_exception_on_wrong_ports_part()
        {
            Assert.Throws<ArgumentException>(delegate
            {
                Enode unused = new(enode8String);
            });
        }

        [Test]
        public void Will_return_exception_on_duplicated_discovery_port_part()
        {
            Assert.Throws<ArgumentException>(delegate
            {
                Enode unused = new(enode9String);
            });
        }

        [Test]
        public void Will_return_exception_on_wrong_form_of_discovery_port_part()
        {
            Assert.Throws<ArgumentException>(delegate
            {
                Enode unused = new(enode10String);
            });
        }

        [Test]
        public async Task Will_accept_static_connection()
        {
            await using Context ctx = new();
            ctx.NetworkConfig.MaxActivePeers = 1;
            ctx.StaticNodesManager.IsStatic(enode2String).Returns(true);

            ctx.PeerPool.Start();
            ctx.PeerManager.Start();
            var enode1 = new Enode(enode1String);
            Node node1 = new(enode1.PublicKey, new IPEndPoint(enode1.HostIp, enode1.Port));
            Session session1 = new(30303, node1, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance,
                LimboLogs.Instance);

            var enode2 = new Enode(enode2String);
            Node node2 = new(enode2.PublicKey, new IPEndPoint(enode2.HostIp, enode2.Port));
            Session session2 = new(30303, node2, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance,
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
            await using Context ctx = new();

            ctx.PeerPool.Start();
            ctx.PeerManager.Start();
            Session session1 = new(30303, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance,
                LimboLogs.Instance);
            session1.RemoteHost = "1.2.3.4";
            session1.RemotePort = 12345;
            session1.RemoteNodeId =
                (firstDirection == ConnectionDirection.In)
                    ? (shouldLose ? TestItem.PublicKeyA : TestItem.PublicKeyC)
                    : (shouldLose ? TestItem.PublicKeyC : TestItem.PublicKeyA);


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
            await using Context ctx = new();
            ctx.SetupPersistedPeers(50);
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();
            await Task.Delay(_travisDelayLong);
            Assert.That(ctx.RlpxPeer.ConnectAsyncCallsCount, Is.EqualTo(25));
            ctx.DisconnectAllSessions();

            await Task.Delay(_travisDelayLong);
            Assert.That(ctx.RlpxPeer.ConnectAsyncCallsCount, Is.EqualTo(50));
        }

        [Test, Retry(5)]
        public async Task Ok_if_fails_to_connect()
        {
            await using Context ctx = new();
            ctx.SetupPersistedPeers(50);
            ctx.RlpxPeer.MakeItFail();
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();

            await Task.Delay(_travisDelay);
            Assert.That(ctx.PeerManager.ActivePeers.Count, Is.EqualTo(0));
        }

        [Test, Retry(3)]
        [NonParallelizable]
        public async Task Will_fill_up_over_and_over_again_on_disconnects()
        {
            await using Context ctx = new();
            ctx.SetupPersistedPeers(50);
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();

            TimeSpan prevConnectingDelay = StatsParameters.Instance.DelayDueToEvent[NodeStatsEventType.Connecting];
            StatsParameters.Instance.DelayDueToEvent[NodeStatsEventType.Connecting] = TimeSpan.Zero;

            try
            {
                int currentCount = 0;
                for (int i = 0; i < 10; i++)
                {
                    currentCount += 25;
                    await Task.Delay(_travisDelayLonger);
                    Assert.That(ctx.RlpxPeer.ConnectAsyncCallsCount, Is.EqualTo(currentCount));
                    ctx.DisconnectAllSessions();
                }
            }
            finally
            {
                StatsParameters.Instance.DelayDueToEvent[NodeStatsEventType.Connecting] = prevConnectingDelay;
            }
        }

        [Test]
        public async Task Will_fill_up_over_and_over_again_on_newly_discovered()
        {
            await using Context ctx = new();
            ctx.SetupPersistedPeers(0);
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();

            for (int i = 0; i < 10; i++)
            {
                ctx.DiscoverNew(25);
                await Task.Delay(_travisDelay);
                Assert.That(ctx.PeerManager.ActivePeers.Count, Is.EqualTo(25));
            }
        }

        [Test]
        public async Task IfPeerAdded_with_invalid_chain_then_do_not_connect()
        {
            await using Context ctx = new();
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();

            var networkNode = new NetworkNode(ctx.GenerateEnode());
            ctx.Stats.ReportFailedValidation(new Node(networkNode), CompatibilityValidationType.NetworkId);

            ctx.PeerPool.GetOrAdd(networkNode);

            await Task.Delay(_travisDelay);
            ctx.PeerPool.ActivePeers.Count.Should().Be(0);
        }

        private int _travisDelay = 500;

        private int _travisDelayLong = 1000;
        private int _travisDelayLonger = 3000;

        [Test]
        [Ignore("Behaviour changed that allows peers to go over max if awaiting response")]
        public async Task Will_fill_up_with_incoming_over_and_over_again_on_disconnects()
        {
            await using Context ctx = new();
            ctx.SetupPersistedPeers(0);
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();

            for (int i = 0; i < 10; i++)
            {
                ctx.CreateNewIncomingSessions(25);
                await Task.Delay(_travisDelay);
                Assert.That(ctx.PeerManager.ActivePeers.Count, Is.EqualTo(25));
            }
        }

        [Test]
        [Retry(3)]
        public async Task Will_fill_up_over_and_over_again_on_disconnects_and_when_ids_keep_changing()
        {
            await using Context ctx = new();
            ctx.SetupPersistedPeers(50);
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();

            int currentCount = 0;
            int maxCount = 0;
            for (int i = 0; i < 10; i++)
            {
                currentCount += 25;
                maxCount += 50;
                await Task.Delay(_travisDelay);
                ctx.RlpxPeer.ConnectAsyncCallsCount.Should().BeInRange(currentCount, maxCount);
                ctx.HandshakeAllSessions();
                await Task.Delay(_travisDelay);
                ctx.DisconnectAllSessions();
            }

            await ctx.PeerManager.StopAsync();
            ctx.DisconnectAllSessions();

            Assert.That(
                () => ctx.PeerManager.CandidatePeers.All(p => p.OutSession is null),
                Is.True.After(1000, 10));
        }

        [Test]
        [Explicit("CI issues - bad test design")]
        public async Task Will_fill_up_over_and_over_again_on_disconnects_and_when_ids_keep_changing_with_max_candidates_40()
        {
            await using Context ctx = new();
            ctx.NetworkConfig.MaxCandidatePeerCount = 40;
            ctx.NetworkConfig.CandidatePeerCountCleanupThreshold = 30;
            ctx.NetworkConfig.PersistedPeerCountCleanupThreshold = 40;
            ctx.SetupPersistedPeers(50);
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();

            int currentCount = 0;
            int count = 35;
            for (int i = 0; i < 10; i++)
            {
                currentCount += count;
                await Task.Delay(_travisDelayLong);
                ctx.RlpxPeer.ConnectAsyncCallsCount.Should().BeInRange(currentCount, currentCount + count);
                ctx.HandshakeAllSessions();
                await Task.Delay(_travisDelay);
                ctx.DisconnectAllSessions();
            }
        }

        [Test]
        [Explicit("CI issues - bad test design")]
        public async Task Will_fill_up_over_and_over_again_on_disconnects_and_when_ids_keep_changing_with_max_candidates_40_with_random_incoming_connections()
        {
            await using Context ctx = new();
            ctx.NetworkConfig.MaxCandidatePeerCount = 40;
            ctx.NetworkConfig.CandidatePeerCountCleanupThreshold = 30;
            ctx.NetworkConfig.PersistedPeerCountCleanupThreshold = 40;
            ctx.SetupPersistedPeers(50);
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();

            int count = 35;
            int currentCount = 0;
            for (int i = 0; i < 10; i++)
            {
                currentCount += count;
                await Task.Delay(_travisDelayLong);
                ctx.RlpxPeer.ConnectAsyncCallsCount.Should().BeInRange(currentCount, currentCount + count);
                ctx.HandshakeAllSessions();
                await Task.Delay(_travisDelay);
                ctx.CreateIncomingSessions();
                await Task.Delay(_travisDelay);
                ctx.DisconnectAllSessions();
            }
        }

        [Test]
        public async Task Will_not_cleanup_active_peers()
        {
            await using Context ctx = new();
            ctx.NetworkConfig.MaxCandidatePeerCount = 2;
            ctx.NetworkConfig.CandidatePeerCountCleanupThreshold = 1;
            ctx.NetworkConfig.PersistedPeerCountCleanupThreshold = 1;
            ctx.SetupPersistedPeers(4);
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();

            Assert.That(
                () => ctx.PeerManager.ActivePeers.Count,
                Is.EqualTo(4).After(5000, 100));
        }

        [Test]
        public async Task Will_load_static_nodes_and_connect_to_them()
        {
            await using Context ctx = new();
            const int nodesCount = 5;
            var staticNodes = ctx.CreateNodes(nodesCount);
            ctx.StaticNodesManager.LoadInitialList().Returns(staticNodes.Select(n => new Node(n, true)).ToList());
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();
            foreach (var node in staticNodes)
            {
                ctx.DiscoveryApp.NodeAdded += Raise.EventWith(new NodeEventArgs(new Node(TestItem.PublicKeyA, node.Host, node.Port)));
            }

            await Task.Delay(_travisDelay);
            ctx.PeerManager.ActivePeers.Count(p => p.Node.IsStatic).Should().Be(nodesCount);
        }

        [Test, Retry(5)]
        public async Task Will_disconnect_on_remove_static_node()
        {
            await using Context ctx = new();
            const int nodesCount = 5;
            var disconnections = 0;
            var staticNodes = ctx.CreateNodes(nodesCount);
            ctx.StaticNodesManager.LoadInitialList().Returns(staticNodes.Select(n => new Node(n, true)).ToList());
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();
            await Task.Delay(_travisDelay);

            void DisconnectHandler(object o, DisconnectEventArgs e) => disconnections++;
            ctx.Sessions.ForEach(s => s.Disconnected += DisconnectHandler);

            ctx.StaticNodesManager.NodeRemoved += Raise.EventWith(new NodeEventArgs(
                new Node(staticNodes.First())));

            ctx.PeerManager.ActivePeers.Count(p => p.Node.IsStatic).Should().Be(nodesCount - 1);
            disconnections.Should().Be(1);
        }

        [Test, Retry(3)]
        public async Task Will_connect_and_disconnect_on_peer_management()
        {
            await using Context ctx = new();
            var disconnections = 0;
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();
            var node = new NetworkNode(ctx.GenerateEnode());
            ctx.PeerPool.GetOrAdd(node);
            await Task.Delay(_travisDelayLong);

            void DisconnectHandler(object o, DisconnectEventArgs e) => disconnections++;
            ctx.PeerManager.ActivePeers.Select(p => p.Node.Id).Should().BeEquivalentTo(new[] { node.NodeId });

            ctx.Sessions.ForEach(s => s.Disconnected += DisconnectHandler);

            ctx.PeerPool.TryRemove(node.NodeId, out _).Should().BeTrue();
            ctx.PeerManager.ActivePeers.Should().BeEmpty();
            disconnections.Should().Be(1);
        }

        [Test]
        public async Task Will_only_add_same_peer_once()
        {
            await using Context ctx = new();
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();
            var node = new NetworkNode(ctx.GenerateEnode());
            ctx.PeerPool.GetOrAdd(node);
            ctx.PeerPool.GetOrAdd(node);
            ctx.PeerPool.GetOrAdd(node);
            await Task.Delay(_travisDelayLong);
            ctx.PeerManager.ActivePeers.Should().HaveCount(1);
        }

        [Test]
        public async Task RemovePeer_should_fail_if_peer_not_added()
        {
            await using Context ctx = new();
            ctx.PeerPool.Start();
            ctx.PeerManager.Start();
            var node = new NetworkNode(ctx.GenerateEnode());
            await Task.Delay(_travisDelay);
            ctx.PeerPool.TryRemove(node.NodeId, out _).Should().BeFalse();
        }

        private class Context : IAsyncDisposable
        {
            public RlpxMock RlpxPeer { get; }
            public IDiscoveryApp DiscoveryApp { get; }
            public INodeStatsManager Stats { get; }
            public INetworkStorage Storage { get; }
            public NodesLoader NodesLoader { get; }
            public PeerManager PeerManager { get; }
            public IPeerPool PeerPool { get; }
            public INetworkConfig NetworkConfig { get; }
            public IStaticNodesManager StaticNodesManager { get; }
            public List<Session> Sessions { get; } = new();

            public Context(int parallelism = 0)
            {
                RlpxPeer = new RlpxMock(Sessions);
                DiscoveryApp = Substitute.For<IDiscoveryApp>();
                DiscoveryApp.LoadInitialList().Returns(new List<Node>());
                ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
                Stats = new NodeStatsManager(timerFactory, LimboLogs.Instance);
                Storage = new InMemoryStorage();
                NodesLoader = new NodesLoader(new NetworkConfig(), Stats, Storage, RlpxPeer, LimboLogs.Instance);
                NetworkConfig = new NetworkConfig();
                NetworkConfig.MaxActivePeers = 25;
                NetworkConfig.PeersPersistenceInterval = 50;
                NetworkConfig.NumConcurrentOutgoingConnects = parallelism;
                StaticNodesManager = Substitute.For<IStaticNodesManager>();
                StaticNodesManager.LoadInitialList().Returns(new List<Node>());
                CompositeNodeSource nodeSources = new(NodesLoader, DiscoveryApp, StaticNodesManager);
                PeerPool = new PeerPool(nodeSources, Stats, Storage, NetworkConfig, LimboLogs.Instance);
                PeerManager = new PeerManager(RlpxPeer, PeerPool, Stats, NetworkConfig, LimboLogs.Instance);
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
                    NetworkNode node = new(enode);
                    nodes.Add(node);
                }

                return nodes;
            }

            public void DiscoverNew(int count)
            {
                for (int i = 0; i < count; i++)
                {
                    DiscoveryApp.NodeAdded +=
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

        private class RlpxMock : IRlpxHost
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
                Session session = new(30313, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
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
            public int LocalPort => 0;
            public event EventHandler<SessionEventArgs> SessionCreated;

            public void CreateIncoming(params Session[] sessions)
            {
                List<Session> incomingSessions = new();
                foreach (Session session in sessions)
                {
                    Session sessionIn = new(30313, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
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

            private bool _isFailing;

            public void MakeItFail()
            {
                _isFailing = true;
            }
        }

        private class InMemoryStorage : INetworkStorage
        {
            private readonly ConcurrentDictionary<PublicKey, NetworkNode> _nodes =
                new();

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
