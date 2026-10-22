// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.Trie;
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
    public void EnumerationAndBloomIncludeOnlyReachableWritesSinceRetain([Values] bool storage)
    {
        using INodeRecordBuffer buffer = FlatTestHelpers.NewTestBuffer();
        using INodeRecordBufferPin originalRetain = buffer.PinHead();
        Hash256? address = storage ? TestItem.KeccakA : null;
        Hash256 firstKey = new("1000000000000000000000000000000000000000000000000000000000000000");
        Hash256 lastKey = new("f000000000000000000000000000000000000000000000000000000000000000");
        (ValueHash256 Hash, ulong Pointer) original = Write((ValueKeccak.EmptyTreeHash, 0), firstKey, 1, lastKey);
        INodeRecordBufferPin updateRetain = buffer.PinHead();
        (ValueHash256 Hash, ulong Pointer) unrelated = Write((ValueKeccak.EmptyTreeHash, 0), firstKey, 3);
        (ValueHash256 Hash, ulong Pointer) updated = Write(original, lastKey, 2);
        using Snapshot snapshot = new(new(1, original.Hash), new(2, updated.Hash), new SnapshotContent(), _pool, ResourcePool.Usage.MainBlockProcessing, updateRetain);
        if (storage) snapshot.Content.StorageRoots[address!] = updated.Pointer;
        else
        {
            snapshot.Content.StateRootPointer = updated.Pointer;
        }
        ulong[] pointers = (storage ? snapshot.StorageNodes.Select(node => node.Pointer) : snapshot.StateNodes.Select(node => node.Pointer)).ToArray();
        bool found = storage
            ? snapshot.TryGetStorageNode((address!, TreePath.Empty.Append(1)), out TrieNode? inherited)
            : snapshot.TryGetStateNode(TreePath.Empty.Append(1), out inherited);
        ulong inheritedPointer = buffer.EnumerateNodes(updated.Pointer).First(node => node.Path == TreePath.Empty.Append(1)).Pointer;
        BloomFilter bloom = ((NodeRecordBuffer)buffer).FindBloom(updated.Pointer)!;
        snapshot.PopulateBloom();
        snapshot.PopulateBloom();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bloom.Count, Is.EqualTo(2), "only the reachable writes, once");
            Assert.That(updated.Pointer >> 32, Is.EqualTo(original.Pointer >> 32));
            Assert.That(pointers, Has.Length.EqualTo(2));
            Assert.That(pointers, Does.Not.Contain(unrelated.Pointer));
            Assert.That(pointers, Does.Not.Contain(inheritedPointer));
            Assert.That(found, Is.True);
            Assert.That(inherited!.FullRlp.ToArray(), Is.Not.Empty);
        }

        (ValueHash256 Hash, ulong Pointer) Write((ValueHash256 Hash, ulong Pointer) root, Hash256 key, byte value, Hash256? other = null)
        {
            using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(2);
            entries.Add(new(key.ValueHash256, Enumerable.Repeat(value, 32).ToArray()));
            if (other is not null) entries.Add(new(other.ValueHash256, new byte[32]));
            return FlatTestHelpers.BulkSet(_pool, buffer, address, root, entries, canParallelize: false);
        }
    }

    [Test]
    public void UnchangedStateRootOmitsEvenRecordsWrittenAfterRetain([Values] bool duplicateRoot)
    {
        using INodeRecordBuffer buffer = FlatTestHelpers.NewTestBuffer();
        byte[] rlp = FlatTestHelpers.LeafRlp(1);
        ValueHash256 hash = ValueKeccak.Compute(rlp);
        ulong original = buffer.AppendRecordFromRlp(null, TreePath.Empty, hash, rlp, []);
        using Snapshot snapshot = _pool.CreateSnapshot(new(1, hash), new(2, hash), ResourcePool.Usage.MainBlockProcessing, buffer);
        ulong root = duplicateRoot ? buffer.AppendRecordFromRlp(null, TreePath.Empty, hash, rlp, []) : original;
        snapshot.Content.StateRootPointer = root;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.StateNodes, Is.Empty);
            Assert.That(snapshot.TryGetStateNode(TreePath.Empty, out TrieNode? found), Is.True);
            Assert.That(found!.FullRlp.ToArray(), Is.EqualTo(rlp));
        }
    }

    [Test]
    public void FixtureConnectsPathsWithUnnormalizedTrailingBytes([Values] bool storage)
    {
        using INodeRecordBuffer buffer = FlatTestHelpers.NewTestBuffer();
        TreePath path = new(TestItem.KeccakA, 3);
        byte[] rlp = FlatTestHelpers.LeafRlp(1);
        using Snapshot snapshot = FlatTestHelpers.MakeSnapshot(_pool, buffer, content =>
        {
            if (storage) content.SetStorageNodeRlp(buffer, TestItem.KeccakB, path, rlp);
            else content.SetStateNodeRlp(buffer, path, rlp);
        });

        bool found = storage
            ? snapshot.TryGetStorageNode((TestItem.KeccakB, path), out TrieNode? node)
            : snapshot.TryGetStateNode(path, out node);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(node!.FullRlp.ToArray(), Is.EqualTo(rlp));
        }
    }

    [Test]
    public void SuppliedRootRlpIsPreservedWhenItsChildrenArriveLater()
    {
        using INodeRecordBuffer buffer = FlatTestHelpers.NewTestBuffer();
        using Snapshot snapshot = _pool.CreateSnapshot(StateId.PreGenesis, new(1, Keccak.Zero), ResourcePool.Usage.MainBlockProcessing, buffer);
        byte[] leafRlp = FlatTestHelpers.LeafRlp(new byte[40]);
        byte[] rootRlp = FlatTestHelpers.ExtensionRlp([1, 2], leafRlp);
        snapshot.Content.SetStateNodeRlp(buffer, TreePath.Empty, rootRlp);
        snapshot.Content.SetStateNodeRlp(buffer, TreePath.FromHexString("12"), leafRlp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.GetRlp(snapshot.Content.StateRootPointer).ToArray(), Is.EqualTo(rootRlp));
            Assert.That(snapshot.TryGetStateNode(TreePath.FromHexString("12"), out TrieNode? node), Is.True);
            Assert.That(node!.FullRlp.ToArray(), Is.EqualTo(leafRlp));
            Assert.That(snapshot.StateNodes.Count(), Is.EqualTo(2));
        }
    }

    [Test]
    public void ReplacingRootOmitsDisconnectedEarlierNodes([Values] bool storage)
    {
        using INodeRecordBuffer buffer = FlatTestHelpers.NewTestBuffer();
        using Snapshot snapshot = _pool.CreateSnapshot(StateId.PreGenesis, new(1, Keccak.Zero), ResourcePool.Usage.MainBlockProcessing, buffer);
        TreePath oldPath = TreePath.FromHexString("12");
        if (storage) snapshot.Content.SetStorageNodeRlp(buffer, TestItem.KeccakA, oldPath, FlatTestHelpers.LeafRlpA);
        else snapshot.Content.SetStateNodeRlp(buffer, oldPath, FlatTestHelpers.LeafRlpA);
        byte[] replacement = FlatTestHelpers.LeafRlp(2);
        ulong root = buffer.AppendRecordFromRlp(storage ? TestItem.KeccakA : null, TreePath.Empty, ValueKeccak.Compute(replacement), replacement, []);
        if (storage) snapshot.Content.StorageRoots[TestItem.KeccakA] = root;
        else snapshot.Content.StateRootPointer = root;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(storage ? snapshot.TryGetStorageNode((TestItem.KeccakA, oldPath), out _) : snapshot.TryGetStateNode(oldPath, out _), Is.False);
            Assert.That(storage ? snapshot.StorageNodes.Count() : snapshot.StateNodes.Count(), Is.EqualTo(1));
            Assert.That(storage ? snapshot.StorageNodes.Single().Pointer : snapshot.StateNodes.Single().Pointer, Is.EqualTo(root));
        }
    }

    [Test]
    public void ExplicitEmptyRootEnumeratesNoNodes([Values] bool storage)
    {
        using INodeRecordBuffer buffer = FlatTestHelpers.NewTestBuffer();
        using Snapshot snapshot = _pool.CreateSnapshot(StateId.PreGenesis, new(1, Keccak.EmptyTreeHash), ResourcePool.Usage.MainBlockProcessing, buffer);
        if (storage)
        {
            snapshot.Content.SetStorageNodeRlp(buffer, TestItem.KeccakA, TreePath.Empty, FlatTestHelpers.LeafRlpA);
            snapshot.Content.StorageRoots[TestItem.KeccakA] = 0;
        }
        else
        {
            snapshot.Content.SetStateNodeRlp(buffer, TreePath.Empty, FlatTestHelpers.LeafRlpA);
            snapshot.Content.StateRootPointer = 0;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(storage ? snapshot.StorageNodes.Count() : snapshot.StateNodes.Count(), Is.Zero);
            Assert.That(storage ? snapshot.TryGetStorageNode((TestItem.KeccakA, TreePath.Empty), out _) : snapshot.TryGetStateNode(TreePath.Empty, out _), Is.False);
        }
    }

    [Test]
    public void RootMetadataEstimatesDoNotScaleWithReachableNodes([Values] bool storage)
    {
        using INodeRecordBuffer buffer = FlatTestHelpers.NewTestBuffer();
        using Snapshot single = FlatTestHelpers.MakeSnapshot(_pool, buffer, content =>
        {
            if (storage) content.SetStorageNodeRlp(buffer, TestItem.KeccakA, TreePath.Empty, FlatTestHelpers.LeafRlpA);
            else content.SetStateNodeRlp(buffer, TreePath.Empty, FlatTestHelpers.LeafRlpA);
        });
        using Snapshot connected = FlatTestHelpers.MakeSnapshot(_pool, buffer, content =>
        {
            if (storage) content.SetStorageNodeRlp(buffer, TestItem.KeccakA, TreePath.FromHexString("12"), FlatTestHelpers.LeafRlpA);
            else content.SetStateNodeRlp(buffer, TreePath.FromHexString("12"), FlatTestHelpers.LeafRlpA);
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(storage ? single.StorageNodes.Count() : single.StateNodes.Count(), Is.EqualTo(1));
            Assert.That(storage ? connected.StorageNodes.Count() : connected.StateNodes.Count(), Is.EqualTo(2));
            Assert.That(connected.EstimateMemory(), Is.EqualTo(single.EstimateMemory()));
            Assert.That(connected.EstimateCompactedMemory(), Is.EqualTo(single.EstimateCompactedMemory()));
        }
    }

    [Test]
    public void NodesEnumerateInAddressHashThenPathOrder([Values] bool storage)
    {
        using INodeRecordBuffer buffer = FlatTestHelpers.NewTestBuffer();
        Hash256[] addresses = [TestItem.KeccakH, TestItem.KeccakA, TestItem.KeccakF, TestItem.KeccakC, TestItem.KeccakE, TestItem.KeccakB, TestItem.KeccakG, TestItem.KeccakD];
        string[] paths = ["f", "0", "37", "3a", "3c01234567", "3b0123456789abcdef01", "3b0123456789abcdef02"];
        using Snapshot snapshot = FlatTestHelpers.MakeSnapshot(_pool, buffer, content =>
        {
            foreach (string path in paths)
            {
                if (!storage) content.SetStateNodeRlp(buffer, TreePath.FromHexString(path), FlatTestHelpers.LeafRlpA);
                else foreach (Hash256 address in addresses) content.SetStorageNodeRlp(buffer, address, TreePath.FromHexString(path), FlatTestHelpers.LeafRlpA);
            }
        });

        string[] keys = storage
            ? snapshot.StorageNodes.Select(node => SortKey(node.Address, node.Path)).ToArray()
            : snapshot.StateNodes.Select(node => SortKey(null, node.Path)).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(keys, Has.Length.GreaterThan(paths.Length * (storage ? addresses.Length : 1)), "synthesized ancestors are enumerated too");
            Assert.That(keys, Is.Ordered.Using((IComparer<string>)StringComparer.Ordinal));
        }

        static string SortKey(Hash256? address, in TreePath path) => $"{address}:{path.Path}:{path.Length:D2}";
    }

    [Test]
    public void StateRootPublicationRecordsResolvedAndEmptyRoots([Values] bool empty)
    {
        using INodeRecordBuffer buffer = FlatTestHelpers.NewTestBuffer();
        using SnapshotBundle bundle = new(FlatTestHelpers.MakeBundle(_pool), buffer, _pool, ResourcePool.Usage.MainBlockProcessing);
        INodeRecordBufferPin retain = buffer.PinHead();
        byte[] rlp = FlatTestHelpers.LeafRlp(1);
        ValueHash256 hash = ValueKeccak.Compute(rlp);
        (ValueHash256 Hash, ulong Pointer) root = empty ? (ValueKeccak.EmptyTreeHash, 0) : (hash, buffer.AppendRecordFromRlp(null, TreePath.Empty, hash, rlp, []));
        bundle.PublishBufferedStateRoot(root);
        (Snapshot? published, TransientResource? retired) = bundle.CollectAndApplySnapshot(
            StateId.PreGenesis, new(1, root.Hash), retain);
        using Snapshot snapshot = published!;
        retired!.ReleaseLease();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.StateRootPointer, Is.EqualTo(root.Pointer));
            Assert.That(snapshot.StateNodes.Count(), Is.EqualTo(empty ? 0 : 1));
        }
    }

    [Test]
    public void CollectWithoutReturnRewiresWritesToFreshContent()
    {
        using SnapshotBundle bundle = new(
            FlatTestHelpers.MakeBundle(_pool),
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
