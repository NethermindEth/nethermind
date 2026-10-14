// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
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
    public void Storage_value_layout_matches_memory_estimate_assumptions()
    {
        // SnapshotContentCounts budgets both the UInt256 key and nullable value at these sizes.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Unsafe.SizeOf<UInt256>(), Is.EqualTo(32));
            Assert.That(Unsafe.SizeOf<UInt256?>(), Is.EqualTo(40));
        }
    }

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
            content.Storages[new((TestItem.AddressA, 1))] = new UInt256(TestItem.KeccakA.Bytes, isBigEndian: true);
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
    public void CompactedEstimateDoesNotOverflowForLargeCounts() =>
        Assert.That(new SnapshotContentCounts(0, 16_000_000, 0, 0, 0).EstimateCompactedMemory(), Is.EqualTo(2_176_000_000L));

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
        const ulong original = 1;
        const ulong replacement = 2;
        const ulong otherAddress = 3;

        snapshot.Content.StorageNodes[(addressA, pathA)] = original;
        snapshot.Content.StorageNodes[(addressA, pathA)] = replacement;
        snapshot.Content.StorageNodes[(addressB, pathB)] = otherAddress;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.Content.StorageNodes.Count, Is.EqualTo(2));
            Assert.That(snapshot.Content.StorageNodes.TryGetValue(new((addressA, pathA)), out ulong foundA), Is.True);
            Assert.That(foundA, Is.EqualTo(replacement));
            Assert.That(snapshot.Content.StorageNodes.TryGetValue(new((addressB, pathB)), out ulong foundB), Is.True);
            Assert.That(foundB, Is.EqualTo(otherAddress));
            Assert.That(snapshot.StorageNodesCount, Is.EqualTo(2));
        }
    }

    [TestCase(256)]
    [TestCase(8_192)]
    public void AddressNodesResetForPoolingKeepsSmallCapacityAndDropsLargeCapacity(int requestedCapacity)
    {
        AddressStorageNodeDictionary.AddressNodes nodes = new();
        nodes.EnsureAdditionalCapacity(requestedCapacity);
        nodes.Set(TreePath.Empty, 1);
        int capacityBeforeReset = nodes.Nodes.Capacity;

        nodes.ResetForPooling();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodes.Nodes, Is.Empty);

            if (requestedCapacity > 4_096)
            {
                Assert.That(nodes.Nodes.Capacity, Is.Zero);
            }
            else
            {
                Assert.That(nodes.Nodes.Capacity, Is.EqualTo(capacityBeforeReset));
            }
        }
    }

    [Test]
    public void AddressOwnedStorageNodesReuseRetainedLargeDictionaryForLargeBatches()
    {
        const int LargeBatch = 12_345;
        AddressStorageNodeDictionary storageNodes = new();
        Hash256 addressA = TestItem.AddressA.ToAccountPath.ToCommitment();
        Hash256 addressB = TestItem.AddressB.ToAccountPath.ToCommitment();
        AddressStorageNodeDictionary.AddressNodes first = storageNodes.GetOrAddAddress(addressA);
        first.EnsureAdditionalCapacity(LargeBatch);
        first.Set(TreePath.Empty, 1);

        storageNodes.NoLockClear();
        AddressStorageNodeDictionary.AddressNodes second = storageNodes.GetOrAddAddress(addressB);
        Dictionary<HashedKey<TreePath>, ulong> beforeBatch = second.Nodes;
        second.EnsureAdditionalCapacity(LargeBatch);

        using (Assert.EnterMultipleScope())
        {
            // Growing in place keeps the dictionary instance, so a new instance means one was rented from the retained pool.
            Assert.That(second.Nodes, Is.Not.SameAs(beforeBatch));
            Assert.That(second.Nodes, Is.Empty);
            Assert.That(second.Nodes.Capacity, Is.GreaterThanOrEqualTo(LargeBatch));
        }
    }

    [Test]
    public void AddressOwnedStorageNodesSupportOverlappingAndReusedEnumerators()
    {
        AddressStorageNodeDictionary storageNodes = new();
        Hash256 addressA = TestItem.AddressA.ToAccountPath.ToCommitment();
        Hash256 addressB = TestItem.AddressB.ToAccountPath.ToCommitment();
        storageNodes[(addressA, TreePath.FromHexString("12"))] = 1;
        storageNodes[(addressA, TreePath.FromHexString("34"))] = 2;
        storageNodes[(addressB, TreePath.FromHexString("56"))] = 3;

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
    public void CollectWithoutReturnRewiresWritesToFreshContent()
    {
        using SnapshotBundle bundle = new(
            FlatTestHelpers.MakeBundle(_pool),
            Substitute.For<ITrieNodeCache>(),
            null,
            _pool,
            ResourcePool.Usage.MainBlockProcessing);
        Account accountA = new(1, 100);
        Account accountB = new(2, 200);

        bundle.SetAccount(TestItem.AddressA, accountA);
        bundle.CollectAndApplySnapshot(StateId.PreGenesis, new StateId(1, TestItem.KeccakA), null, returnSnapshot: false);

        bundle.SetAccount(TestItem.AddressB, accountB);
        (Snapshot? second, TransientResource? transientResource) =
            bundle.CollectAndApplySnapshot(new StateId(1, TestItem.KeccakA), new StateId(2, TestItem.KeccakB), null);
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
