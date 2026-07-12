// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.State.Flat.PersistedSnapshots;
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
    public void CountsSealOnFirstObservationNotBefore()
    {
        // ResourcePool.CreateSnapshot hands out an EMPTY snapshot that callers populate through
        // Content afterwards, so counts must not be frozen at construction.
        using Snapshot snapshot = _pool.CreateSnapshot(StateId.PreGenesis, StateId.PreGenesis, ResourcePool.Usage.MainBlockProcessing);
        snapshot.Content.Accounts[new(TestItem.AddressA)] = new(1, 100);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.AccountsCount, Is.EqualTo(1));
            Assert.That(snapshot.EstimateMemory(), Is.GreaterThan(0));
        }
    }

    [Test]
    public void CountsAndEstimatesAreSealedByFirstObservation()
    {
        // The repository adds EstimateMemory() to its memory ledger when a snapshot is published and
        // subtracts a later call on removal: both sides must see the same value even if a straggling
        // writer (e.g. a trie-warmer job) mutates the content in between, and serving sealed counts
        // avoids the all-stripe-locks ConcurrentDictionary.Count on live maps.
        using Snapshot snapshot = FlatTestHelpers.MakeSnapshot(_pool, content =>
        {
            content.Accounts[new(TestItem.AddressA)] = new(1, 100);
            content.Storages[new((TestItem.AddressA, 1))] = new SlotValue(TestItem.KeccakA.Bytes);
        });

        long estimate = snapshot.EstimateMemory();
        long compactedEstimate = snapshot.EstimateCompactedMemory();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.AccountsCount, Is.EqualTo(1));
            Assert.That(snapshot.StoragesCount, Is.EqualTo(1));
        }

        // A write landing after the first observation must not move the sealed values.
        snapshot.Content.Accounts[new(TestItem.AddressB)] = new(2, 200);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.AccountsCount, Is.EqualTo(1));
            Assert.That(snapshot.EstimateMemory(), Is.EqualTo(estimate));
            Assert.That(snapshot.EstimateCompactedMemory(), Is.EqualTo(compactedEstimate));
        }
    }

    [Test]
    public void SealedEstimatesMatchLiveContentEstimates()
    {
        using Snapshot snapshot = FlatTestHelpers.MakeSnapshot(_pool, content =>
        {
            content.Accounts[new(TestItem.AddressA)] = new(1, 100);
            content.SelfDestructedStorageAddresses[new(TestItem.AddressB)] = true;
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.EstimateMemory(), Is.EqualTo(snapshot.Content.EstimateMemory()));
            Assert.That(snapshot.EstimateCompactedMemory(), Is.EqualTo(snapshot.Content.EstimateCompactedMemory()));
        }
    }

    [Test]
    public void ArenaSizingUsesLiveCountsNotSealedCounts()
    {
        // The arena writer throws if the persisted write overruns the extent sized from EstimateSize,
        // so persist-time sizing must reflect content written after the counts were sealed.
        using Snapshot snapshot = FlatTestHelpers.MakeSnapshot(_pool, content =>
            content.Accounts[new(TestItem.AddressA)] = new(1, 100));

        long sealedEstimate = snapshot.EstimateMemory(); // seals the ledger value
        long sizeBeforeMutation = PersistedSnapshotBuilder.EstimateSize(snapshot);
        snapshot.Content.Accounts[new(TestItem.AddressB)] = new(2, 200);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.EstimateMemory(), Is.EqualTo(sealedEstimate), "ledger value must stay sealed");
            Assert.That(PersistedSnapshotBuilder.EstimateSize(snapshot), Is.GreaterThan(sizeBeforeMutation),
                "arena sizing must see the post-seal write");
        }
    }

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
    public void AddressOwnedStorageNodesRetainEligibleInnerCapacityAfterQuiescentClear()
    {
        AddressStorageNodeDictionary storageNodes = new();
        Hash256 addressA = TestItem.AddressA.ToAccountPath.ToCommitment();
        Hash256 addressB = TestItem.AddressB.ToAccountPath.ToCommitment();
        AddressStorageNodeDictionary.AddressNodes original = storageNodes.GetOrAddAddress(addressA);
        original.EnsureAdditionalCapacity(256);
        original.Set(TreePath.Empty, new TrieNode(NodeType.Unknown, TestItem.KeccakA));
        int capacity = original.Nodes.Capacity;

        storageNodes.NoLockClear();
        AddressStorageNodeDictionary.AddressNodes reused = storageNodes.GetOrAddAddress(addressB);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(original.Nodes, Is.Empty);
            Assert.That(original.Nodes.Capacity, Is.EqualTo(capacity));
            Assert.That(reused.Nodes, Is.Empty);
        }
    }

    [Test]
    public void AddressOwnedStorageNodesSupportOverlappingAndReusedEnumerators()
    {
        AddressStorageNodeDictionary storageNodes = new();
        Hash256 addressA = TestItem.AddressA.ToAccountPath.ToCommitment();
        Hash256 addressB = TestItem.AddressB.ToAccountPath.ToCommitment();
        storageNodes[(addressA, TreePath.FromHexString("12"))] = new TrieNode(NodeType.Leaf, TestItem.KeccakA);
        storageNodes[(addressA, TreePath.FromHexString("34"))] = new TrieNode(NodeType.Branch, TestItem.KeccakB);
        storageNodes[(addressB, TreePath.FromHexString("56"))] = new TrieNode(NodeType.Extension, TestItem.KeccakC);

        AddressStorageNodeDictionary.Enumerator first = storageNodes.GetEnumerator();
        AddressStorageNodeDictionary.Enumerator second = storageNodes.GetEnumerator();
        int firstCount = Count(ref first);
        int secondCount = Count(ref second);
        first.Dispose();
        second.Dispose();

        AddressStorageNodeDictionary.Enumerator reused = storageNodes.GetEnumerator();
        int reusedCount = Count(ref reused);
        reused.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstCount, Is.EqualTo(3));
            Assert.That(secondCount, Is.EqualTo(3));
            Assert.That(reusedCount, Is.EqualTo(3));
        }

        static int Count(ref AddressStorageNodeDictionary.Enumerator enumerator)
        {
            int count = 0;
            while (enumerator.MoveNext()) count++;
            return count;
        }
    }

    [Test]
    public void PublicTrieNodeSettersPreserveWriteBehavior()
    {
        using SnapshotBundle bundle = new(
            FlatTestHelpers.MakeBundle(_pool),
            Substitute.For<ITrieNodeCache>(),
            _pool,
            ResourcePool.Usage.MainBlockProcessing);
        TreePath statePath = TreePath.FromHexString("12");
        TreePath storagePath = TreePath.FromHexString("34");
        Hash256 address = TestItem.AddressA.ToAccountPath.ToCommitment();
        TrieNode stateNode = new(NodeType.Unknown, TestItem.KeccakA);
        TrieNode storageNode = new(NodeType.Unknown, TestItem.KeccakB);

        bundle.SetStateNode(statePath, stateNode);
        bundle.SetStorageNode(address, storagePath, storageNode);

        MethodInfo? stateSetter = typeof(SnapshotBundle).GetMethod(
            nameof(SnapshotBundle.SetStateNode),
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            [typeof(TreePath).MakeByRefType(), typeof(TrieNode)],
            modifiers: null);
        MethodInfo? storageSetter = typeof(SnapshotBundle).GetMethod(
            nameof(SnapshotBundle.SetStorageNode),
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            [typeof(Hash256), typeof(TreePath).MakeByRefType(), typeof(TrieNode)],
            modifiers: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stateSetter, Is.Not.Null);
            Assert.That(storageSetter, Is.Not.Null);
            Assert.That(bundle.FindStateNodeOrUnknown(statePath, TestItem.KeccakA), Is.SameAs(stateNode));
            Assert.That(bundle.FindStorageNodeOrUnknown(address, storagePath, TestItem.KeccakB), Is.SameAs(storageNode));
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
