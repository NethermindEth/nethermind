// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Timers;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Test;

[Parallelizable(ParallelScope.Self)]
public class NetworkStorageTests
{
    [SetUp]
    public void SetUp()
    {
        ILogManager logManager = LimboLogs.Instance;
        _ = new ConfigProvider();
        _tempDir = TempPath.GetTempDirectory();

        SimpleFilePublicKeyDb db = new("Test", _tempDir.Path, logManager);
        _storage = new NetworkStorage(db, logManager);
    }

    [TearDown]
    public void TearDown() => _tempDir.Dispose();

    private TempPath _tempDir;
    private INetworkStorage _storage;

    [Test]
    public void Can_store_discovery_nodes()
    {
        NetworkNode[] persistedNodes = _storage.GetPersistedNodes();
        Assert.That(persistedNodes.Length, Is.EqualTo(0));

        Node[] nodes =
        [
            new Node(TestItem.PublicKeyA, "192.1.1.1", 3441),
            new Node(TestItem.PublicKeyB, "192.1.1.2", 3442),
            new Node(TestItem.PublicKeyC, "192.1.1.3", 3443),
            new Node(TestItem.PublicKeyD, "192.1.1.4", 3444),
            new Node(TestItem.PublicKeyE, "192.1.1.5", 3445)
        ];

        INodeStatsManager nodeStatsManager = new NodeStatsManager(new TimerFactory(), LimboLogs.Instance);

        DateTime utcNow = DateTime.UtcNow;
        NetworkNode[] networkNodes = nodes
            .Select(x => new NetworkNode(x.Id, x.Host, x.Port, nodeStatsManager.GetOrAdd(x).NewPersistedNodeReputation(utcNow)))
            .ToArray();


        _storage.StartBatch();
        _storage.UpdateNodes(networkNodes);
        _storage.Commit();

        persistedNodes = _storage.GetPersistedNodes();
        foreach (Node manager in nodes)
        {
            NetworkNode persistedNode = persistedNodes.FirstOrDefault(x => x.NodeId.Equals(manager.Id));
            Assert.That(persistedNode, Is.Not.Null);
            Assert.That(persistedNode.Port, Is.EqualTo(manager.Port));
            Assert.That(persistedNode.Host, Is.EqualTo(manager.Host));
            Assert.That(persistedNode.Reputation, Is.EqualTo(nodeStatsManager.GetOrAdd(manager).CurrentNodeReputation()));
        }

        _storage.StartBatch();
        _storage.RemoveNode(networkNodes.First().NodeId);
        _storage.Commit();

        persistedNodes = _storage.GetPersistedNodes();
        foreach (Node manager in nodes.Take(1))
        {
            NetworkNode persistedNode = persistedNodes.FirstOrDefault(x => x.NodeId.Equals(manager.Id));
            Assert.That(persistedNode, Is.Null);
        }

        utcNow = DateTime.UtcNow;
        foreach (Node manager in nodes.Skip(1))
        {
            NetworkNode persistedNode = persistedNodes.FirstOrDefault(x => x.NodeId.Equals(manager.Id));
            Assert.That(persistedNode, Is.Not.Null);
            Assert.That(persistedNode.Port, Is.EqualTo(manager.Port));
            Assert.That(persistedNode.Host, Is.EqualTo(manager.Host));
            Assert.That(persistedNode.Reputation, Is.EqualTo(nodeStatsManager.GetOrAdd(manager).CurrentNodeReputation(utcNow)));
        }
    }

    [Test]
    public void Can_store_peers()
    {
        NetworkNode[] persistedPeers = _storage.GetPersistedNodes();
        Assert.That(persistedPeers.Length, Is.EqualTo(0));

        Node[] nodes =
        [
            new Node(TestItem.PublicKeyA, "192.1.1.1", 3441),
            new Node(TestItem.PublicKeyB, "192.1.1.2", 3442),
            new Node(TestItem.PublicKeyC, "192.1.1.3", 3443),
            new Node(TestItem.PublicKeyD, "192.1.1.4", 3444),
            new Node(TestItem.PublicKeyE, "192.1.1.5", 3445)
        ];

        NetworkNode[] peers = nodes.Select(x => new NetworkNode(x.Id, x.Host, x.Port, 0L)).ToArray();

        _storage.StartBatch();
        _storage.UpdateNodes(peers);
        _storage.Commit();

        persistedPeers = _storage.GetPersistedNodes();
        foreach (NetworkNode peer in peers)
        {
            NetworkNode persistedNode = persistedPeers.FirstOrDefault(x => x.NodeId.Equals(peer.NodeId));
            Assert.That(persistedNode, Is.Not.Null);
            Assert.That(persistedNode.Port, Is.EqualTo(peer.Port));
            Assert.That(persistedNode.Host, Is.EqualTo(peer.Host));
            Assert.That(persistedNode.Reputation, Is.EqualTo(peer.Reputation));
        }

        _storage.StartBatch();
        _storage.RemoveNode(peers.First().NodeId);
        _storage.Commit();

        persistedPeers = _storage.GetPersistedNodes();
        foreach (NetworkNode peer in peers.Take(1))
        {
            NetworkNode persistedNode = persistedPeers.FirstOrDefault(x => x.NodeId.Equals(peer.NodeId));
            Assert.That(persistedNode, Is.Null);
        }

        foreach (NetworkNode peer in peers.Skip(1))
        {
            NetworkNode persistedNode = persistedPeers.FirstOrDefault(x => x.NodeId.Equals(peer.NodeId));
            Assert.That(persistedNode, Is.Not.Null);
            Assert.That(persistedNode.Port, Is.EqualTo(peer.Port));
            Assert.That(persistedNode.Host, Is.EqualTo(peer.Host));
            Assert.That(persistedNode.Reputation, Is.EqualTo(peer.Reputation));
        }
    }

    [Test]
    public void Start_batch_discards_pending_nodes_from_stale_batch()
    {
        NetworkStorage storage = new(new SnapshotableMemDb(), LimboLogs.Instance);
        NetworkNode node = new(TestItem.PublicKeyA, "192.1.1.1", 3441, 0L);

        storage.StartBatch();
        storage.UpdateNode(node);
        storage.StartBatch();

        Assert.That(storage.GetPersistedNodes(), Is.Empty);

        storage.UpdateNode(node);
        storage.Commit();

        Assert.That(storage.GetPersistedNodes(), Has.Length.EqualTo(1));
    }

    [Test]
    public void Failed_commit_reloads_persisted_nodes_before_new_updates()
    {
        FailingBatchDb db = new();
        NetworkStorage storage = new(db, LimboLogs.Instance);
        NetworkNode persistedNode = new(TestItem.PublicKeyA, "192.1.1.1", 3441, 1L);
        NetworkNode discardedNode = new(TestItem.PublicKeyB, "192.1.1.2", 3442, 2L);
        NetworkNode pendingNode = new(TestItem.PublicKeyC, "192.1.1.3", 3443, 3L);
        storage.UpdateNode(persistedNode);

        db.ThrowOnNextBatchDispose = true;
        storage.StartBatch();
        storage.UpdateNode(discardedNode);
        Assert.Throws<InvalidOperationException>(storage.Commit);

        storage.StartBatch();
        storage.UpdateNode(pendingNode);

        NetworkNode[] nodes = storage.GetPersistedNodes();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodes, Has.Some.Matches<NetworkNode>(node => node.NodeId.Equals(persistedNode.NodeId)));
            Assert.That(nodes, Has.Some.Matches<NetworkNode>(node => node.NodeId.Equals(pendingNode.NodeId)));
            Assert.That(nodes, Has.None.Matches<NetworkNode>(node => node.NodeId.Equals(discardedNode.NodeId)));
        }
    }

    private sealed class FailingBatchDb : MemDb
    {
        public bool ThrowOnNextBatchDispose { get; set; }

        public override IWriteBatch StartWriteBatch()
        {
            if (!ThrowOnNextBatchDispose)
            {
                return base.StartWriteBatch();
            }

            ThrowOnNextBatchDispose = false;
            return new FailingWriteBatch();
        }
    }

    private sealed class FailingWriteBatch : IWriteBatch
    {
        public void Clear() { }

        public void Dispose() => throw new InvalidOperationException("Failed batch dispose.");

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) { }

        public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) { }
    }
}
