// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Config;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Enr;
using Nethermind.Network.P2P;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test;

public class PeerPoolTests
{
    [Test]
    public async Task PeerPool_ShouldThrottleSource_WhenFull()
    {
        ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();

        TestNodeSource nodeSource = new();
        PeerPool pool = new(
            nodeSource,
            Substitute.For<INodeStatsManager>(),
            new NetworkStorage(new TestMemDb(), LimboLogs.Instance),
            new NetworkConfig()
            {
                MaxActivePeers = 5,
                MaxCandidatePeerCount = 10
            },
            LimboLogs.Instance,
            trustedNodesManager);

        Random rand = new(0);
        PrivateKeyGenerator keyGen = new(new TestRandom((m) => rand.Next(m), (s) =>
        {
            byte[] buffer = new byte[s];
            rand.NextBytes(buffer);
            return buffer;
        }));

        for (int i = 0; i < 5; i++)
        {
            PublicKey key = keyGen.Generate().PublicKey;
            Node node = new(key, "1.2.3.4", 1234);
            Peer peer = pool.GetOrAdd(node);
            pool.ActivePeers[key] = peer;
        }

        pool.Start();

        for (int i = 0; i < 10; i++)
        {
            PublicKey key = keyGen.Generate().PublicKey;
            Node node = new(key, "1.2.3.4", 1234);
            nodeSource.AddNode(node);
        }

        Assert.That(() => nodeSource.BufferedNodeCount, Is.EqualTo(10).After(100, 10));

        await pool.StopAsync();
    }

    [Test]
    public async Task PeerPool_RunPeerCommit_ShouldContinueAfterNoPendingChange()
    {
        ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();
        TestNodeSource nodeSource = new();
        INodeStatsManager stats = Substitute.For<INodeStatsManager>();
        TestNetworkStorage storage = new();
        NetworkConfig networkConfig = new()
        {
            PeersPersistenceInterval = 50,
            MaxActivePeers = 0,
            MaxCandidatePeerCount = 0
        };

        PeerPool pool = new(nodeSource, stats, storage, networkConfig, LimboLogs.Instance, trustedNodesManager);

        storage.Pending = false;
        pool.Start();

        try
        {
            // allow a couple of ticks with no pending changes
            await Task.Delay(200);
            Assert.That(storage.CommitCount, Is.EqualTo(0));

            // now flip to pending and expect a commit soon
            storage.Pending = true;
            Assert.That(() => storage.CommitCount, Is.AtLeast(1).After(2000, 10));

            // StartBatch should be called once in ctor and once after commit
            Assert.That(() => storage.StartBatchCount, Is.AtLeast(2).After(2000, 10));
        }
        finally
        {
            await pool.StopAsync();
        }
    }

    [Test]
    public async Task PeerPool_ShouldIgnoreNodeRemoved_AfterStop()
    {
        ConfigProvider configProvider = new();
        ChainSpec spec = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboLogs.Instance)
            .LoadEmbeddedOrFromFile("chainspec/foundation.json");
        spec.Bootnodes = [];

        TestNodeSource nodeSource = new();
        await using IContainer container = new ContainerBuilder()
            .AddModule(new PseudoNethermindModule(spec, configProvider, new TestLogManager()))
            .AddModule(new TestEnvironmentModule(TestItem.PrivateKeyA, nameof(PeerPoolTests)))
            .AddSingleton(nodeSource)
            .Bind<INodeSource, TestNodeSource>()
            .Build();

        IPeerPool pool = container.Resolve<IPeerPool>();
        Node node = new(TestItem.PublicKeyA, "1.2.3.4", 1234);

        pool.Start();
        pool.GetOrAdd(node);
        await pool.StopAsync();

        nodeSource.RemoveNode(node);

        Assert.That(pool.TryGet(node.Id, out _), Is.True);
    }

    [Test]
    public async Task PeerPool_ShouldThrottleSource_WhenCandidatePoolIsFull()
    {
        ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();
        TestNodeSource nodeSource = new();
        PeerPool pool = CreatePeerPool(nodeSource, trustedNodesManager, maxActivePeers: 10, maxCandidatePeerCount: 1);

        pool.GetOrAdd(new Node(TestItem.PublicKeyA, "1.2.3.4", 1234));
        pool.Start();
        nodeSource.AddNode(new Node(TestItem.PublicKeyB, "1.2.3.5", 1234));

        try
        {
            Assert.That(() => nodeSource.BufferedNodeCount, Is.EqualTo(1).After(100, 10));
        }
        finally
        {
            await pool.StopAsync();
        }
    }

    [Test]
    public async Task PeerPool_ShouldThrottleSource_WhenActivePeerPoolIsFull()
    {
        ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();
        TestNodeSource nodeSource = new();
        PeerPool pool = CreatePeerPool(nodeSource, trustedNodesManager, maxActivePeers: 1, maxCandidatePeerCount: 10);

        Peer activePeer = pool.GetOrAdd(new Node(TestItem.PublicKeyA, "1.2.3.4", 1234));
        pool.ActivePeers[TestItem.PublicKeyA] = activePeer;
        pool.Start();
        nodeSource.AddNode(new Node(TestItem.PublicKeyB, "1.2.3.5", 1234));

        try
        {
            Assert.That(() => nodeSource.BufferedNodeCount, Is.EqualTo(1).After(100, 10));
        }
        finally
        {
            await pool.StopAsync();
        }
    }

    [Test]
    public async Task PeerPool_ShouldNotThrottleStaticNode_WhenFull()
    {
        ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();
        TestNodeSource nodeSource = new();
        PeerPool pool = CreatePeerPool(nodeSource, trustedNodesManager, maxActivePeers: 1, maxCandidatePeerCount: 1);

        Peer activePeer = pool.GetOrAdd(new Node(TestItem.PublicKeyA, "1.2.3.4", 1234));
        pool.ActivePeers[TestItem.PublicKeyA] = activePeer;
        pool.Start();

        Node staticNode = new(TestItem.PublicKeyB, "1.2.3.5", 1234) { IsStatic = true };
        nodeSource.AddNode(staticNode);

        try
        {
            // Static node bypasses throttling: consumed and registered even though the pool is full.
            Assert.That(() => nodeSource.BufferedNodeCount, Is.EqualTo(0).After(100, 10));
            Assert.That(pool.TryGet(staticNode.Id, out _), Is.True);
        }
        finally
        {
            await pool.StopAsync();
        }
    }

    [Test]
    public async Task PeerPool_ShouldUpdateDynamicNodeEndpointInPlace()
    {
        TestNodeSource nodeSource = new();
        PeerPool pool = CreatePeerPool(
            nodeSource,
            Substitute.For<ITrustedNodesManager>(),
            maxActivePeers: 10,
            maxCandidatePeerCount: 1);
        Node initialNode = new(TestItem.PublicKeyA, "1.2.3.4", 30303);
        Node upgradedNode = new(TestItem.PublicKeyA, "1.2.3.5", 30304, 30305);
        pool.Start();

        try
        {
            nodeSource.AddNode(initialNode);
            Assert.That(() => pool.TryGet(initialNode.Id, out _), Is.True.After(2000, 10));
            Assert.That(pool.TryGet(initialNode.Id, out Peer? peer), Is.True);

            nodeSource.AddNode(upgradedNode);
            Assert.That(() => peer.Node.Port, Is.EqualTo(30304).After(2000, 10));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(peer.Node.Host, Is.EqualTo("1.2.3.5"));
                Assert.That(peer.Node.DiscoveryPort, Is.EqualTo(30305));
                Assert.That(peer.Node, Is.SameAs(initialNode));
            }
        }
        finally
        {
            await pool.StopAsync();
        }
    }

    [Test]
    public async Task PeerPool_ShouldNotDowngradeEndpointFromOlderEnr()
    {
        TestNodeSource nodeSource = new();
        PeerPool pool = CreatePeerPool(
            nodeSource,
            Substitute.For<ITrustedNodesManager>(),
            maxActivePeers: 10,
            maxCandidatePeerCount: 1);
        Node currentNode = new(TestItem.PublicKeyA, "1.2.3.4", 30304, 30305)
        {
            Enr = new NodeRecord { EnrSequence = 2 }
        };
        Node olderNode = new(TestItem.PublicKeyA, "1.2.3.5", 30306, 30307)
        {
            Enr = new NodeRecord { EnrSequence = 1 }
        };
        pool.Start();

        try
        {
            nodeSource.AddNode(currentNode);
            Assert.That(() => pool.TryGet(currentNode.Id, out _), Is.True.After(2000, 10));
            Assert.That(pool.TryGet(currentNode.Id, out Peer? peer), Is.True);

            nodeSource.AddNode(olderNode);
            Assert.That(() => nodeSource.BufferedNodeCount, Is.Zero.After(2000, 10));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(peer.Node.Address, Is.EqualTo(currentNode.Address));
                Assert.That(peer.Node.DiscoveryAddress, Is.EqualTo(currentNode.DiscoveryAddress));
                Assert.That(peer.Node.Enr.EnrSequence, Is.EqualTo(2));
                Assert.That(peer.Node, Is.SameAs(currentNode));
            }
        }
        finally
        {
            await pool.StopAsync();
        }
    }

    [Test]
    public async Task PeerPool_ShouldNotReplaceConfiguredEndpointWithDynamicCandidate()
    {
        TestNodeSource nodeSource = new();
        PeerPool pool = CreatePeerPool(
            nodeSource,
            Substitute.For<ITrustedNodesManager>(),
            maxActivePeers: 10,
            maxCandidatePeerCount: 10);
        Node staticNode = new(TestItem.PublicKeyA, "1.2.3.4", 30303) { IsStatic = true };
        Node dynamicCandidate = new(TestItem.PublicKeyA, "1.2.3.5", 30304);
        pool.Start();

        try
        {
            nodeSource.AddNode(staticNode);
            Assert.That(() => pool.TryGet(staticNode.Id, out _), Is.True.After(2000, 10));
            Assert.That(pool.TryGet(staticNode.Id, out Peer? peer), Is.True);

            nodeSource.AddNode(dynamicCandidate);
            Assert.That(() => nodeSource.BufferedNodeCount, Is.Zero.After(2000, 10));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(peer.Node.Address, Is.EqualTo(staticNode.Address));
                Assert.That(peer.Node.IsStatic, Is.True);
            }
        }
        finally
        {
            await pool.StopAsync();
        }
    }

    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    [TestCase(false, false, true)]
    public async Task PeerPool_ShouldPromoteConfiguredEndpointAfterDynamicCandidate(
        bool isStatic,
        bool isTrusted,
        bool isBootnode)
    {
        TestNodeSource nodeSource = new();
        PeerPool pool = CreatePeerPool(
            nodeSource,
            Substitute.For<ITrustedNodesManager>(),
            maxActivePeers: 10,
            maxCandidatePeerCount: 1);
        Node initialNode = new(TestItem.PublicKeyA, "1.2.3.4", 30303)
        {
            Enr = new NodeRecord { EnrSequence = 2 }
        };
        Node configuredNode = new(TestItem.PublicKeyA, "1.2.3.5", 30304)
        {
            IsStatic = isStatic,
            IsTrusted = isTrusted,
            IsBootnode = isBootnode,
            Enr = new NodeRecord { EnrSequence = 1 }
        };
        Node laterCandidate = new(TestItem.PublicKeyA, "1.2.3.6", 30305);
        pool.Start();

        try
        {
            nodeSource.AddNode(initialNode);
            Assert.That(() => pool.TryGet(initialNode.Id, out _), Is.True.After(2000, 10));
            Assert.That(pool.TryGet(initialNode.Id, out Peer? peer), Is.True);

            nodeSource.AddNode(configuredNode);
            Assert.That(() => peer.Node.Address, Is.EqualTo(configuredNode.Address).After(2000, 10));
            nodeSource.AddNode(laterCandidate);
            Assert.That(() => nodeSource.BufferedNodeCount, Is.Zero.After(2000, 10));
            peer.Node.UpdateEndpoint(laterCandidate);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(peer.Node.Address, Is.EqualTo(configuredNode.Address));
                Assert.That(peer.Node.IsStatic, Is.EqualTo(isStatic));
                Assert.That(peer.Node.IsTrusted, Is.EqualTo(isTrusted));
                Assert.That(peer.Node.IsBootnode, Is.EqualTo(isBootnode));
                Assert.That(peer.Node.Enr.EnrSequence, Is.EqualTo(1));
            }
        }
        finally
        {
            await pool.StopAsync();
        }
    }

    [TestCase(true, true)]   // active session → peer kept
    [TestCase(false, false)] // no session → peer evicted
    public void PeerPool_DiscoveryEviction(bool hasSession, bool expectPresent)
    {
        ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();
        TestNodeSource nodeSource = new();
        PeerPool pool = CreatePeerPool(nodeSource, trustedNodesManager, maxActivePeers: 10, maxCandidatePeerCount: 10);

        Node node = new(TestItem.PublicKeyA, "1.2.3.4", 1234);
        Peer peer = pool.GetOrAdd(node);
        if (hasSession) peer.InSession = Substitute.For<ISession>();

        nodeSource.RemoveNode(node);

        Assert.That(pool.TryGet(node.Id, out _), Is.EqualTo(expectPresent));
    }

    [Test]
    public void PeerPool_Replace_DoesNotInheritStaticFlag()
    {
        ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();
        TestNodeSource nodeSource = new();
        PeerPool pool = CreatePeerPool(nodeSource, trustedNodesManager, maxActivePeers: 10, maxCandidatePeerCount: 10);

        Node oldNode = new(TestItem.PublicKeyA, "1.2.3.4", 1234) { IsStatic = true };
        Peer oldPeer = pool.GetOrAdd(oldNode);
        ISession session = Substitute.For<ISession>();
        session.Direction.Returns(ConnectionDirection.Out);
        session.ObsoleteRemoteNodeId.Returns(TestItem.PublicKeyA);
        session.Node.Returns(new Node(TestItem.PublicKeyB, "1.2.3.5", 1234));
        oldPeer.OutSession = session;

        Peer replacedPeer = pool.Replace(session);

        Assert.That(replacedPeer.Node.IsStatic, Is.False);
    }

    [Test]
    public void GetOrAdd_NetworkNode_sets_trusted_flag_from_manager()
    {
        ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();
        trustedNodesManager.IsTrusted(Arg.Any<Enode>()).Returns(true);
        TestNodeSource nodeSource = new();
        PeerPool pool = CreatePeerPool(nodeSource, trustedNodesManager, maxActivePeers: 10, maxCandidatePeerCount: 10);

        string enode = new Enode(TestItem.PublicKeyA, IPAddress.Parse("1.2.3.4"), 30303).ToString();
        Peer peer = pool.GetOrAdd(new NetworkNode(enode));

        Assert.That(peer.Node.IsTrusted, Is.True, "GetOrAdd(NetworkNode) marks trusted via the manager");
    }

    private static PeerPool CreatePeerPool(TestNodeSource nodeSource, ITrustedNodesManager trustedNodesManager, int maxActivePeers, int maxCandidatePeerCount) => new(
            nodeSource,
            Substitute.For<INodeStatsManager>(),
            new NetworkStorage(new TestMemDb(), LimboLogs.Instance),
            new NetworkConfig
            {
                MaxActivePeers = maxActivePeers,
                MaxCandidatePeerCount = maxCandidatePeerCount
            },
            LimboLogs.Instance,
            trustedNodesManager);

    private sealed class TestNetworkStorage : INetworkStorage
    {
        public volatile bool Pending;
        public int CommitCount { get; private set; }
        public int StartBatchCount { get; private set; }

        public NetworkNode[] GetPersistedNodes() => Array.Empty<NetworkNode>();
        public int PersistedNodesCount => 0;
        public void UpdateNode(NetworkNode node) => Pending = true;
        public void UpdateNodes(IEnumerable<NetworkNode> nodes) => Pending = true;
        public void RemoveNode(PublicKey nodeId) => Pending = true;
        public void StartBatch() { Interlocked.Increment(ref _startBatchCountBacking); StartBatchCount = _startBatchCountBacking; }
        public void Commit() { Interlocked.Increment(ref _commitCountBacking); CommitCount = _commitCountBacking; }
        public bool AnyPendingChange() => Pending;

        private int _commitCountBacking;
        private int _startBatchCountBacking;
    }
}
