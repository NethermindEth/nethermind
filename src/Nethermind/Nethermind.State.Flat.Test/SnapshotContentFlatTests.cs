// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class SnapshotContentFlatTests
{
    private static readonly Hash256 AddressA = TestItem.AddressA.ToAccountPath.ToCommitment();

    [Test]
    public void Default_config_keeps_storage_nodes_object_backed()
    {
        SnapshotContent content = new(flatNodeStorage: false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(content.StorageNodesAreFlat, Is.False);
            Assert.That(content.FlatStorageNodes, Is.Null);
            Assert.That(content.StorageNodes, Is.Not.Null);
        }
    }

    [Test]
    public void Flat_config_routes_storage_nodes_through_the_slab_tier()
    {
        SnapshotContent content = new(flatNodeStorage: true);
        Assert.That(content.StorageNodesAreFlat, Is.True);
        Assert.That(content.FlatStorageNodes, Is.Not.Null);

        byte[] rlp = [0xC2, 0x01, 0x02];
        content.FlatStorageNodes![(AddressA, TreePath.FromHexString("12"))] = new TrieNode(NodeType.Leaf, rlp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(content.StorageNodesCount, Is.EqualTo(1));
            Assert.That(content.StorageNodes.Count, Is.Zero, "the object-backed tier stays empty when flat");
            Assert.That(content.TryGetStorageNode(new((AddressA, TreePath.FromHexString("12"))), out TrieNode? node), Is.True);
            Assert.That(node!.FullRlp.ToArray(), Is.EqualTo(rlp));
        }

        List<(Hash256, TreePath)> enumerated = [];
        foreach (KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode> kvp in content.EnumerateStorageNodes())
            enumerated.Add(kvp.Key.Key);

        Assert.That(enumerated, Is.EqualTo(new[] { (AddressA, TreePath.FromHexString("12")) }));
        Assert.That(content.StorageNodesForMerge, Has.Count.EqualTo(1));
    }

    [Test]
    public void Flat_pool_hands_out_flat_snapshots()
    {
        ResourcePool pool = new(new FlatDbConfig { FlatNodeStorage = true });
        using Snapshot snapshot = pool.CreateSnapshot(StateId.PreGenesis, StateId.PreGenesis, ResourcePool.Usage.MainBlockProcessing);

        Assert.That(snapshot.StorageNodesAreFlat, Is.True);
        snapshot.Content.FlatStorageNodes![(AddressA, TreePath.Empty)] = new TrieNode(NodeType.Unknown, TestItem.KeccakA);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.StorageNodesCount, Is.EqualTo(1));
            Assert.That(snapshot.TryGetStorageNode(new((AddressA, TreePath.Empty)), out TrieNode? node), Is.True);
            Assert.That(node!.Keccak, Is.EqualTo(TestItem.KeccakA));
        }
    }

    [Test]
    public void Flat_reset_releases_the_arena_and_allows_reuse()
    {
        SnapshotContent content = new(flatNodeStorage: true);
        content.FlatStorageNodes![(AddressA, TreePath.Empty)] = new TrieNode(NodeType.Leaf, new byte[] { 0xC1, 0x01 });
        Assert.That(content.FlatStorageNodes.ArenaBytesReserved, Is.GreaterThan(0));

        content.Reset();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(content.StorageNodesCount, Is.Zero);
            Assert.That(content.FlatStorageNodes.ArenaBytesReserved, Is.Zero);
        }
    }
}
