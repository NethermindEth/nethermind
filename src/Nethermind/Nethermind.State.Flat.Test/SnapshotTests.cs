// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public class SnapshotTests
{
    private ResourcePool _pool = null!;

    [SetUp]
    public void SetUp() => _pool = new ResourcePool(new FlatDbConfig());

    [Test]
    public void AddressOwnedStorageNodesPreserveFlattenedDictionarySemantics()
    {
        using Snapshot snapshot = _pool.CreateSnapshot(StateId.PreGenesis, StateId.PreGenesis, ResourcePool.Usage.MainBlockProcessing);
        Hash256 addressA = TestItem.AddressA.ToAccountPath.ToCommitment();
        Hash256 addressB = TestItem.AddressB.ToAccountPath.ToCommitment();
        TreePath pathA = TreePath.FromHexString("12");
        TreePath pathB = TreePath.FromHexString("34");
        TrieNode original = new(NodeType.Leaf, TestItem.KeccakA);
        TrieNode replacement = new(NodeType.Branch, TestItem.KeccakB);
        TrieNode otherAddress = new(NodeType.Extension, TestItem.KeccakC);

        snapshot.Content.StorageNodes[(addressA, pathA)] = original;
        snapshot.Content.StorageNodes[(addressA, pathA)] = replacement;
        snapshot.Content.StorageNodes[(addressB, pathB)] = otherAddress;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.Content.StorageNodes.Count, Is.EqualTo(2));
            Assert.That(snapshot.Content.StorageNodes.TryGetValue(new((addressA, pathA)), out TrieNode? foundA), Is.True);
            Assert.That(foundA, Is.SameAs(replacement));
            Assert.That(snapshot.Content.StorageNodes.TryGetValue(new((addressB, pathB)), out TrieNode? foundB), Is.True);
            Assert.That(foundB, Is.SameAs(otherAddress));
            Assert.That(snapshot.StorageNodesCount, Is.EqualTo(2));
        }
    }

    [Test]
    public void CollectWithoutReturnRewiresWritesToFreshContent()
    {
        using SnapshotBundle bundle = new(
            FlatTestHelpers.MakeBundle(_pool),
            Substitute.For<ITrieNodeCache>(),
            _pool,
            ResourcePool.Usage.MainBlockProcessing);
        Account accountA = new(1, 100);
        Account accountB = new(2, 200);

        bundle.SetAccount(TestItem.AddressA, accountA);
        bundle.CollectAndApplySnapshot(StateId.PreGenesis, new StateId(1, TestItem.KeccakA), returnSnapshot: false);

        bundle.SetAccount(TestItem.AddressB, accountB);
        (Snapshot? second, TransientResource? transientResource) =
            bundle.CollectAndApplySnapshot(new StateId(1, TestItem.KeccakA), new StateId(2, TestItem.KeccakB));
        using Snapshot? snapshotLease = second;
        transientResource?.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second!.TryGetAccount(new(TestItem.AddressB), out Account? published), Is.True);
            Assert.That(published, Is.SameAs(accountB));
            Assert.That(second.TryGetAccount(new(TestItem.AddressA), out _), Is.False);
            Assert.That(bundle.GetAccount(TestItem.AddressA), Is.SameAs(accountA));
        }
    }
}
