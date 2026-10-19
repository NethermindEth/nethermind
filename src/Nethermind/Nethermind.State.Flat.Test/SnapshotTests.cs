// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;
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
        // The arena writer throws if the persisted write overruns the prepared extent,
        // so persist-time sizing must reflect content written after the counts were sealed.
        using Snapshot snapshot = FlatTestHelpers.MakeSnapshot(_pool, content =>
            content.Accounts[new(TestItem.AddressA)] = new(1, 100));

        long sealedEstimate = snapshot.EstimateMemory(); // seals the ledger value
        using PersistedSnapshotBuilder.PreparedSnapshot before = new(snapshot);
        long sizeBeforeMutation = before.EstimatedSize;
        snapshot.Content.Accounts[new(TestItem.AddressB)] = new(2, 200);

        using PersistedSnapshotBuilder.PreparedSnapshot after = new(snapshot);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.EstimateMemory(), Is.EqualTo(sealedEstimate), "ledger value must stay sealed");
            Assert.That(after.EstimatedSize, Is.GreaterThan(sizeBeforeMutation),
                "arena sizing must see the post-seal write");
        }
    }

    [Test]
    public void EnumerationIncludesOnlyReachableWritesSinceRetain([Values] bool storage)
    {
        using INodeRecordBuffer buffer = FlatTestHelpers.NewTestBuffer();
        using INodeRecordBufferPin originalRetain = buffer.Pin();
        Hash256? address = storage ? TestItem.KeccakA : null;
        using WarmedNodeLog warmed = new(1);
        DictionaryNodeSource source = new(new Dictionary<TreePath, byte[]>(), warmed, address);
        Hash256 firstKey = new("1000000000000000000000000000000000000000000000000000000000000000");
        Hash256 lastKey = new("f000000000000000000000000000000000000000000000000000000000000000");
        TrieRoot original = Write(TrieRoot.Empty, firstKey, 1, lastKey);
        INodeRecordBufferPin updateRetain = buffer.Pin();
        TrieRoot unrelated = Write(TrieRoot.Empty, firstKey, 3);
        TrieRoot updated = Write(original, lastKey, 2);
        using Snapshot snapshot = new(new(1, original.Hash), new(2, updated.Hash), new SnapshotContent(), _pool, ResourcePool.Usage.MainBlockProcessing) { BufferPin = updateRetain };
        if (storage) snapshot.Content.StorageRoots[address!] = updated.Pointer;
        else
        {
            snapshot.Content.StateRootPointer = updated.Pointer;
            snapshot.Content.HasStateRoot = true;
        }
        ulong[] pointers = (storage ? snapshot.StorageNodes.Select(kv => kv.Value) : snapshot.StateNodes.Select(kv => kv.Value)).ToArray();
        if (storage) AssertIteratorEntries(snapshot.GetStorageNodeIterator(), snapshot.StorageNodes.ToArray(), snapshot);
        else AssertIteratorEntries(snapshot.GetStateNodeIterator(), snapshot.StateNodes.ToArray(), snapshot);
        bool found = storage
            ? snapshot.TryGetStorageNode((address!, TreePath.Empty.Append(1)), out ulong inherited)
            : snapshot.TryGetStateNode(TreePath.Empty.Append(1), out inherited);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated.Pointer >> 32, Is.EqualTo(original.Pointer >> 32));
            Assert.That(pointers, Has.Length.EqualTo(2));
            Assert.That(pointers, Does.Not.Contain(unrelated.Pointer));
            Assert.That(pointers, Does.Not.Contain(inherited));
            Assert.That(found, Is.True);
            Assert.That(snapshot.GetRlp(inherited).ToArray(), Is.Not.Empty);
        }

        TrieRoot Write(TrieRoot root, Hash256 key, byte value, Hash256? other = null)
        {
            using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(2);
            entries.Add(new(key.ValueHash256, Enumerable.Repeat(value, 32).ToArray()));
            if (other is not null) entries.Add(new(other.ValueHash256, new byte[32]));
            return BufferBulkSet.BulkSet(buffer, source, address, root, entries, canParallelize: false);
        }
    }

    [Test]
    public void UnchangedStateRootOmitsEvenRecordsWrittenAfterRetain([Values] bool duplicateRoot)
    {
        using INodeRecordBuffer buffer = FlatTestHelpers.NewTestBuffer();
        byte[] rlp = FlatTestHelpers.LeafRlp(1);
        ValueHash256 hash = ValueKeccak.Compute(rlp);
        ulong original = buffer.AppendRecordFromRlp(null, TreePath.Empty, hash, rlp);
        using Snapshot snapshot = _pool.CreateSnapshot(new(1, hash), new(2, hash), ResourcePool.Usage.MainBlockProcessing, buffer);
        ulong root = duplicateRoot ? buffer.AppendRecordFromRlp(null, TreePath.Empty, hash, rlp) : original;
        snapshot.Content.StateRootPointer = root;
        snapshot.Content.HasStateRoot = true;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.StateNodes, Is.Empty);
            Assert.That(snapshot.TryGetStateNode(TreePath.Empty, out ulong found), Is.True);
            Assert.That(found, Is.EqualTo(root));
            Assert.That(snapshot.GetRlp(found).ToArray(), Is.EqualTo(rlp));
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
        ChildRef child = ChildRef.ForHash(ValueKeccak.Compute(leafRlp), 0);
        byte[] rootRlp = new byte[NodeRlp.ExtensionLength([1, 2], child)];
        NodeRlp.WriteExtension([1, 2], child, rootRlp);
        snapshot.Content.SetStateNodeRlp(buffer, TreePath.Empty, rootRlp);
        snapshot.Content.SetStateNodeRlp(buffer, TreePath.FromHexString("12"), leafRlp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.GetRlp(snapshot.Content.StateRootPointer).ToArray(), Is.EqualTo(rootRlp));
            Assert.That(snapshot.TryGetStateNode(TreePath.FromHexString("12"), out ulong pointer), Is.True);
            Assert.That(snapshot.GetRlp(pointer).ToArray(), Is.EqualTo(leafRlp));
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
        ulong root = buffer.AppendRecordFromRlp(storage ? TestItem.KeccakA : null, TreePath.Empty, ValueKeccak.Compute(replacement), replacement);
        if (storage) snapshot.Content.StorageRoots[TestItem.KeccakA] = root;
        else snapshot.Content.StateRootPointer = root;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(storage ? snapshot.TryGetStorageNode((TestItem.KeccakA, oldPath), out ulong _) : snapshot.TryGetStateNode(oldPath, out ulong _), Is.False);
            Assert.That(storage ? snapshot.StorageNodes.Count() : snapshot.StateNodes.Count(), Is.EqualTo(1));
            Assert.That(storage ? snapshot.StorageNodes.Single().Value : snapshot.StateNodes.Single().Value, Is.EqualTo(root));
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
            Assert.That(storage ? snapshot.TryGetStorageNode((TestItem.KeccakA, TreePath.Empty), out ulong _) : snapshot.TryGetStateNode(TreePath.Empty, out ulong _), Is.False);
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
    public void NodeIteratorStopsOnCompletionOrEarlyDispose([Values] bool storage, [Values] bool finish)
    {
        using ForwardingNodeRecordBuffer buffer = new(FlatTestHelpers.NewTestBuffer());
        using Snapshot snapshot = FlatTestHelpers.MakeSnapshot(_pool, buffer, content =>
        {
            if (storage) content.SetStorageNodeRlp(buffer, TestItem.KeccakA, TreePath.Empty, FlatTestHelpers.LeafRlpA);
            else content.SetStateNodeRlp(buffer, TreePath.Empty, FlatTestHelpers.LeafRlpA);
        });
        if (storage) AssertReads(snapshot.GetStorageNodeIterator());
        else AssertReads(snapshot.GetStateNodeIterator());

        void AssertReads<TKey>(Snapshot.NodeIterator<TKey> nodes) where TKey : IEquatable<TKey>
        {
            Assert.That(nodes.MoveNext(), Is.True);
            Assert.That(nodes.ReadRlp().ToArray(), Is.EqualTo(Bytes.FromHexString(FlatTestHelpers.LeafRlpA)));
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) _ = nodes.ReadRlp().Length;
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero, "reading RLP must not materialize an array");
            if (finish) Assert.That(nodes.MoveNext(), Is.False);
            else nodes.Dispose();
            nodes.Dispose();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(nodes.MoveNext(), Is.False);
                Assert.That(() => nodes.ReadRlp().Length, Throws.InvalidOperationException);
                Assert.That(() => nodes.Key, Throws.InvalidOperationException);
            }
        }
    }

    [Test]
    public void NodeIteratorDisposesItsSourceWhenMoveNextThrows()
    {
        IEnumerator<KeyValuePair<HashedKey<TreePath>, ulong>> source = Substitute.For<IEnumerator<KeyValuePair<HashedKey<TreePath>, ulong>>>();
        source.MoveNext().Returns(_ => throw new InvalidOperationException("Injected iterator failure."));
        using Snapshot.NodeIterator<TreePath> nodes = new(null, source);

        Assert.That(() => nodes.MoveNext(), Throws.InvalidOperationException);
        nodes.Dispose();
        source.Received(1).Dispose();
        Assert.That(nodes.MoveNext(), Is.False);
    }

    private static void AssertIteratorEntries<TKey>(Snapshot.NodeIterator<TKey> nodes,
        KeyValuePair<HashedKey<TKey>, ulong>[] expected, Snapshot snapshot) where TKey : IEquatable<TKey>
    {
        using (nodes)
        {
            Assert.That(() => nodes.ReadRlp().Length, Throws.InvalidOperationException);
            Assert.That(() => nodes.Key, Throws.InvalidOperationException);
            foreach (KeyValuePair<HashedKey<TKey>, ulong> entry in expected)
            {
                Assert.That(nodes.MoveNext(), Is.True);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(nodes.Key, Is.EqualTo(entry.Key));
                    Assert.That(nodes.ReadRlp().ToArray(), Is.EqualTo(snapshot.GetRlp(entry.Value).ToArray()));
                }
            }
            Assert.That(nodes.MoveNext(), Is.False);
            Assert.That(() => nodes.ReadRlp().Length, Throws.InvalidOperationException);
        }
    }

    public enum PublishedRootKind { Resolved, Empty, Unresolved }

    [Test]
    public void StateRootPublicationDistinguishesUnresolvedAndEmptyRoots([Values] PublishedRootKind kind)
    {
        using INodeRecordBuffer buffer = FlatTestHelpers.NewTestBuffer();
        using SnapshotBundle bundle = new(FlatTestHelpers.MakeBundle(_pool), buffer, _pool, ResourcePool.Usage.MainBlockProcessing);
        INodeRecordBufferPin retain = buffer.Pin();
        byte[] rlp = FlatTestHelpers.LeafRlp(1);
        ValueHash256 hash = ValueKeccak.Compute(rlp);
        TrieRoot root = kind switch
        {
            PublishedRootKind.Resolved => new(hash, buffer.AppendRecordFromRlp(null, TreePath.Empty, hash, rlp)),
            PublishedRootKind.Empty => TrieRoot.Empty,
            _ => new(hash, 0)
        };
        bundle.PublishBufferedStateRoot(root);
        (Snapshot? published, TransientResource? retired) = bundle.CollectAndApplySnapshot(
            StateId.PreGenesis, new(1, root.Hash), retain);
        using Snapshot snapshot = published!;
        retired!.ReleaseLease();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.HasStateRoot, Is.EqualTo(kind != PublishedRootKind.Unresolved));
            Assert.That(snapshot.StateRootPointer, Is.EqualTo(root.Pointer));
            Assert.That(snapshot.StateNodes.Count(), Is.EqualTo(kind == PublishedRootKind.Resolved ? 1 : 0));
            AssertIteratorEntries(snapshot.GetStateNodeIterator(), snapshot.StateNodes.ToArray(), snapshot);
        }
    }

    public enum ReaderTier { ReadOnly, Local, Current }

    [Test]
    public void RequestedHashSkipsUnrelatedNewerNodes([Values] bool storage, [Values] ReaderTier tier, [Values] bool newerHasPath)
    {
        using INodeRecordBuffer buffer = FlatTestHelpers.NewTestBuffer();
        Hash256 address = TestItem.KeccakA;
        TreePath targetPath = TreePath.FromHexString("1");
        TreePath newerPath = newerHasPath ? targetPath : TreePath.FromHexString("2");
        byte[] expectedRlp = FlatTestHelpers.LeafRlp(1);
        Hash256 expectedHash = new(ValueKeccak.Compute(expectedRlp));
        ReadOnlySnapshotBundle readOnly = new(
            FlatTestHelpers.SnapshotList(NodeSnapshot(expectedRlp, targetPath), NodeSnapshot(FlatTestHelpers.LeafRlp(2), newerPath)),
            Substitute.For<IPersistence.IPersistenceReader>(), false, PersistedSnapshotStack.Empty(), bufferPin: null);
        using SnapshotBundle bundle = new(readOnly, buffer, _pool, ResourcePool.Usage.MainBlockProcessing,
            tier == ReaderTier.ReadOnly ? null : FlatTestHelpers.SnapshotList(NodeSnapshot(FlatTestHelpers.LeafRlp(3), newerPath)));
        if (tier == ReaderTier.Current)
        {
            using Snapshot currentSnapshot = NodeSnapshot(FlatTestHelpers.LeafRlp(4), newerPath);
            ulong pointer = storage ? currentSnapshot.Content.StorageRoots[address] : currentSnapshot.StateRootPointer;
            TrieRoot current = new(buffer.GetRecord(pointer).Hash, pointer);
            if (storage) bundle.PublishBufferedStorageRoot(address, current);
            else bundle.PublishBufferedStateRoot(current);
        }

        if (tier == ReaderTier.ReadOnly)
        {
            bool found = storage
                ? readOnly.TryFindStorageNodes(address, targetPath, expectedHash, out TrieNode? matching)
                : readOnly.TryFindStateNodes(targetPath, expectedHash, out matching);
            bool wrongFound = storage
                ? readOnly.TryFindStorageNodes(address, targetPath, Keccak.Zero, out _)
                : readOnly.TryFindStateNodes(targetPath, Keccak.Zero, out _);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(found, Is.True);
                Assert.That(matching!.FullRlp.ToArray(), Is.EqualTo(expectedRlp));
                Assert.That(wrongFound, Is.False);
            }
        }
        else
        {
            TrieNode matching = Find(expectedHash);
            TrieNode missing = Find(Keccak.Zero);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(matching.FullRlp.ToArray(), Is.EqualTo(expectedRlp));
                Assert.That(missing.NodeType, Is.EqualTo(NodeType.Unknown));
                Assert.That(missing.Keccak, Is.EqualTo(Keccak.Zero));
            }
        }

        Snapshot NodeSnapshot(byte[] rlp, TreePath path) => FlatTestHelpers.MakeSnapshot(_pool, buffer, content =>
        {
            if (storage) content.SetStorageNodeRlp(buffer, address, path, rlp);
            else content.SetStateNodeRlp(buffer, path, rlp);
        });

        TrieNode Find(Hash256 hash) => storage
            ? bundle.FindStorageNodeOrUnknown(address, targetPath, hash)
            : bundle.FindStateNodeOrUnknown(targetPath, hash);
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
