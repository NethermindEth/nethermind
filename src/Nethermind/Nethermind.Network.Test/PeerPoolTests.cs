// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Config;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
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
        var trustedNodesManager = Substitute.For<ITrustedNodesManager>();

        TestNodeSource nodeSource = new TestNodeSource();
        PeerPool pool = new PeerPool(
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

        Random rand = new Random(0);
        PrivateKeyGenerator keyGen = new PrivateKeyGenerator(new TestRandom((m) => rand.Next(m), (s) =>
        {
            byte[] buffer = new byte[s];
            rand.NextBytes(buffer);
            return buffer;
        }));

        for (int i = 0; i < 5; i++)
        {
            PublicKey key = keyGen.Generate().PublicKey;
            Node node = new Node(key, "1.2.3.4", 1234);
            Peer peer = pool.GetOrAdd(node);
            pool.ActivePeers[key] = peer;
        }

        pool.Start();

        for (int i = 0; i < 10; i++)
        {
            PublicKey key = keyGen.Generate().PublicKey;
            Node node = new Node(key, "1.2.3.4", 1234);
            nodeSource.AddNode(node);
        }

        Assert.That(() => nodeSource.BufferedNodeCount, Is.EqualTo(5).After(100, 10));

        await pool.StopAsync();
    }

    [Test]
    public async Task PeerPool_RunPeerCommit_ShouldContinueAfterNoPendingChange()
    {
        var trustedNodesManager = Substitute.For<ITrustedNodesManager>();
        var nodeSource = new TestNodeSource();
        var stats = Substitute.For<INodeStatsManager>();
        var storage = new TestNetworkStorage();
        var networkConfig = new NetworkConfig
        {
            PeersPersistenceInterval = 50,
            MaxActivePeers = 0,
            MaxCandidatePeerCount = 0
        };

        var pool = new PeerPool(nodeSource, stats, storage, networkConfig, LimboLogs.Instance, trustedNodesManager);

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

    private sealed class TestNetworkStorage : INetworkStorage
    {
        public volatile bool Pending;
        public int CommitCount { get; private set; }
        public int StartBatchCount { get; private set; }

        public NetworkNode[] GetPersistedNodes() => Array.Empty<NetworkNode>();
        public int PersistedNodesCount => 0;
        public void UpdateNode(NetworkNode node) { Pending = true; }
        public void UpdateNodes(IEnumerable<NetworkNode> nodes) { Pending = true; }
        public void RemoveNode(PublicKey nodeId) { Pending = true; }
        public void StartBatch() { Interlocked.Increment(ref _startBatchCountBacking); StartBatchCount = _startBatchCountBacking; }
        public void Commit() { Interlocked.Increment(ref _commitCountBacking); CommitCount = _commitCountBacking; }
        public bool AnyPendingChange() => Pending;

        private int _commitCountBacking;
        private int _startBatchCountBacking;
    }
}
