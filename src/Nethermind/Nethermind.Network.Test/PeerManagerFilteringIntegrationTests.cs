// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test;

[Parallelizable(ParallelScope.All)]
[TestFixture]
public class PeerManagerFilteringIntegrationTests
{
    [Test]
    public async Task PeerManager_CallsShouldContactBeforeConnectAsync()
    {
        CallOrderTrackingMock trackingMock = new();
        ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
        INodeStatsManager stats = new NodeStatsManager(timerFactory, LimboLogs.Instance);
        INetworkStorage storage = new InMemoryStorage();
        INetworkConfig networkConfig = new NetworkConfig
        {
            MaxActivePeers = 25,
            NumConcurrentOutgoingConnects = 1,
            MaxOutgoingConnectPerSec = 1000000
        };

        NodesLoader nodesLoader = new(networkConfig, stats, storage, new Enode(TestItem.PublicKeyA, IPAddress.Loopback, 30303), LimboLogs.Instance);
        IStaticNodesManager staticNodesManager = Substitute.For<IStaticNodesManager>();
        staticNodesManager.DiscoverNodes(Arg.Any<CancellationToken>()).Returns(AsyncEnumerable.Empty<Node>());
        TestNodeSource testNodeSource = new();
        CompositeNodeSource nodeSources = new(nodesLoader, Substitute.For<IDiscoveryApp>(), staticNodesManager, testNodeSource);
        ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();
        IPeerPool peerPool = new PeerPool(nodeSources, stats, storage, networkConfig, LimboLogs.Instance, trustedNodesManager);
        PeerManager peerManager = new(trackingMock, peerPool, stats, networkConfig, new Enode(TestItem.PublicKeyA, IPAddress.Loopback, 30303), LimboLogs.Instance);

        try
        {
            peerPool.Start();
            peerManager.Start();

            testNodeSource.AddNode(new Node(new PrivateKeyGenerator().Generate().PublicKey, "203.0.113.1", 30303));

            await trackingMock.FirstConnect.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.That(trackingMock.CallsToConnectAsync, Is.Not.Empty);
            Assert.That(trackingMock.CallsToShouldContact, Is.Not.Empty, "ShouldContact should have been called before ConnectAsync");
        }
        finally
        {
            await peerManager.StopAsync();
            await peerPool.StopAsync();
        }
    }

    [TestCase(true, false, Description = "Static peer bypasses subnet filter")]
    [TestCase(false, true, Description = "Bootnode peer bypasses subnet filter")]
    public async Task PrivilegedPeer_BypassesSubnetFilter(bool isStatic, bool isBootnode)
    {
        await using Context ctx = new();
        Node node = new(new PrivateKeyGenerator().Generate().PublicKey, "203.0.113.1", 30303)
        {
            IsStatic = isStatic,
            IsBootnode = isBootnode
        };

        ctx.TestNodeSource.AddNode(node);

        ctx.PeerPool.Start();
        ctx.PeerManager.Start();

        await ctx.RlpxMock.FirstConnect.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.That(ctx.RlpxMock.ConnectedNodeIds, Is.Not.Empty, "Privileged peer should bypass the subnet filter and trigger ConnectAsync");
    }

    [Test]
    public async Task RegularPeer_BlockedByIpFilter()
    {
        await using Context ctx = new();
        Node regularNode = new(new PrivateKeyGenerator().Generate().PublicKey, "203.0.113.3", 30303);
        Node staticBeacon = new(new PrivateKeyGenerator().Generate().PublicKey, "203.0.113.99", 30303) { IsStatic = true };

        ctx.StaticNodesManager
            .DiscoverNodes(Arg.Any<CancellationToken>())
            .Returns(new[] { staticBeacon }.ToAsyncEnumerable());
        ctx.TestNodeSource.AddNode(regularNode);

        ctx.PeerPool.Start();
        ctx.PeerManager.Start();

        await ctx.RlpxMock.FirstConnect.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.That(ctx.RlpxMock.ConnectedNodeIds, Does.Not.Contain(regularNode.Id), "Regular peer should be blocked by the IP filter");
    }

    [Test]
    public async Task StaticAndRegularPeers_OnlyStaticBypasses()
    {
        await using Context ctx = new();
        Node staticNode = new(new PrivateKeyGenerator().Generate().PublicKey, "203.0.113.10", 30303) { IsStatic = true };
        Node regularNode = new(new PrivateKeyGenerator().Generate().PublicKey, "203.0.113.11", 30303);

        ctx.StaticNodesManager
            .DiscoverNodes(Arg.Any<CancellationToken>())
            .Returns(new[] { staticNode }.ToAsyncEnumerable());
        ctx.TestNodeSource.AddNode(regularNode);

        ctx.PeerPool.Start();
        ctx.PeerManager.Start();

        Node connected = await ctx.RlpxMock.FirstConnect.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.That(connected.Id, Is.EqualTo(staticNode.Id), "First peer connected should be the static one");
        Assert.That(ctx.RlpxMock.ConnectedNodeIds, Does.Not.Contain(regularNode.Id), "Regular peer should be blocked by the IP filter");
    }

    private class Context : IAsyncDisposable
    {
        public FilterRejectingRlpxMock RlpxMock { get; }
        public PeerManager PeerManager { get; }
        public IPeerPool PeerPool { get; }
        public IStaticNodesManager StaticNodesManager { get; }
        public TestNodeSource TestNodeSource { get; }

        public Context()
        {
            RlpxMock = new FilterRejectingRlpxMock();
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            INodeStatsManager stats = new NodeStatsManager(timerFactory, LimboLogs.Instance);
            INetworkStorage storage = new InMemoryStorage();
            INetworkConfig networkConfig = new NetworkConfig
            {
                MaxActivePeers = 25,
                NumConcurrentOutgoingConnects = 1,
                MaxOutgoingConnectPerSec = 1000000
            };

            NodesLoader nodesLoader = new(networkConfig, stats, storage, new Enode(TestItem.PublicKeyA, IPAddress.Loopback, 30303), LimboLogs.Instance);
            StaticNodesManager = Substitute.For<IStaticNodesManager>();
            StaticNodesManager.DiscoverNodes(Arg.Any<CancellationToken>()).Returns(AsyncEnumerable.Empty<Node>());
            TestNodeSource = new TestNodeSource();
            CompositeNodeSource nodeSources = new(nodesLoader, Substitute.For<IDiscoveryApp>(), StaticNodesManager, TestNodeSource);
            ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();
            PeerPool = new PeerPool(nodeSources, stats, storage, networkConfig, LimboLogs.Instance, trustedNodesManager);
            PeerManager = new PeerManager(RlpxMock, PeerPool, stats, networkConfig, new Enode(TestItem.PublicKeyA, IPAddress.Loopback, 30303), LimboLogs.Instance);
        }

        public async ValueTask DisposeAsync()
        {
            await PeerManager.StopAsync();
            await PeerPool.StopAsync();
        }
    }

    /// <summary>
    /// Mock that rejects subnet-matched IPs but allows exact-matched IPs.
    /// Static and bootnode peers pass because PeerManager uses exactOnly=true for them.
    /// Regular peers are blocked because PeerManager uses exactOnly=false.
    /// </summary>
    private class FilterRejectingRlpxMock : IRlpxHost
    {
        public ConcurrentBag<PublicKey> ConnectedNodeIds { get; } = [];
        public TaskCompletionSource<Node> FirstConnect { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ShouldContact(IPAddress ip, bool exactOnly = false) => exactOnly;

        public Task<bool> ConnectAsync(Node node)
        {
            ConnectedNodeIds.Add(node.Id);
            FirstConnect.TrySetResult(node);

            Session session = new(30303, node, Substitute.For<IChannel>(), NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            SessionCreated?.Invoke(this, new SessionEventArgs(session));
            return Task.FromResult(true);
        }

        public Task Init() => Task.CompletedTask;
        public Task Shutdown() => Task.CompletedTask;
        public int LocalPort => 30303;
        public event EventHandler<SessionEventArgs>? SessionCreated;
        public event SessionDisconnectedEventHandler? SessionDisconnected { add { } remove { } }
        public ISessionMonitor SessionMonitor => Substitute.For<ISessionMonitor>();
    }

    private class CallOrderTrackingMock : IRlpxHost
    {
        public ConcurrentBag<IPAddress> CallsToShouldContact { get; } = [];
        public ConcurrentBag<Node> CallsToConnectAsync { get; } = [];
        public TaskCompletionSource<Node> FirstConnect { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ShouldContact(IPAddress ip, bool exactOnly = false)
        {
            CallsToShouldContact.Add(ip);
            return true;
        }

        public Task<bool> ConnectAsync(Node node)
        {
            CallsToConnectAsync.Add(node);
            FirstConnect.TrySetResult(node);
            return Task.FromResult(true);
        }

        public Task Init() => Task.CompletedTask;
        public Task Shutdown() => Task.CompletedTask;
        public int LocalPort => 30303;
        public event EventHandler<SessionEventArgs>? SessionCreated { add { } remove { } }
        public event SessionDisconnectedEventHandler? SessionDisconnected { add { } remove { } }
        public ISessionMonitor SessionMonitor => Substitute.For<ISessionMonitor>();
    }

    private class InMemoryStorage : INetworkStorage
    {
        private readonly ConcurrentDictionary<PublicKey, NetworkNode> _nodes = new();
        private bool _pendingChanges;

        public NetworkNode[] GetPersistedNodes() => _nodes.Select(static kvp => kvp.Value).ToArray();
        public int PersistedNodesCount => _nodes.Count;

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

        public void RemoveNode(PublicKey nodeId) => _pendingChanges = true;
        public void StartBatch() { }
        public void Commit() { }
        public bool AnyPendingChange() => _pendingChanges;
    }
}
