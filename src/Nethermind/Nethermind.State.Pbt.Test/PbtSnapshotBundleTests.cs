// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core.Memory;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Persistence;
using NUnit.Framework;
using static Nethermind.State.Pbt.Test.PbtStoreTestExtensions;

namespace Nethermind.State.Pbt.Test;

public class PbtSnapshotBundleTests
{
    [Test]
    public void First_write_to_a_run_seeds_the_whole_run_from_the_newest_layer_holding_it([Values] bool heldByLayer)
    {
        // Slots 3, 5 and 6 share one run.
        PbtVariableTreeKey persistedKey = PbtStateKey.Slot(TestItem.AddressA, 5);
        PbtVariableTreeKey layerKey = PbtStateKey.Slot(TestItem.AddressA, 6);
        EvmWord persisted = EvmWordSlot.FromStripped(Value(1));
        EvmWord layer = EvmWordSlot.FromStripped(Value(2));
        EvmWord local = EvmWordSlot.FromStripped(Value(3));
        PbtResourcePool pool = new(new PbtConfig());
        PbtSnapshotContent sharedContent = new();
        if (heldByLayer) sharedContent.SetSlot(layerKey, layer);
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), new PbtReadOnlySnapshotBundle(PbtSnapshotBundleTestExtensions.Chain(pool, sharedContent), new Reader(persistedKey, new ValueHash256(Value(1)))), pool, PbtResourcePool.Usage.MainBlockProcessing, NoopPbtTrieNodeCache.Instance);
        bundle.SetSlot(TestItem.AddressA, 3, local);
        EvmWord[] afterWrite = [bundle.GetSlot(TestItem.AddressA, 3), bundle.GetSlot(TestItem.AddressA, 5), bundle.GetSlot(TestItem.AddressA, 6)];
        bundle.SetSlot(TestItem.AddressA, heldByLayer ? 6u : 5u, default);
        EvmWord[] afterClear = [bundle.GetSlot(TestItem.AddressA, 3), bundle.GetSlot(TestItem.AddressA, 5), bundle.GetSlot(TestItem.AddressA, 6)];
        bundle.SelfDestruct(PbtStateKey.AddressKeyHash(TestItem.AddressA));
        bundle.SetSlot(TestItem.AddressA, 5, local);
        using (Assert.EnterMultipleScope())
        {
            // A layer holding the run answers for all of it, so the persisted slot is masked when a layer holds the run.
            Assert.That(afterWrite, Is.EqualTo(new[] { local, heldByLayer ? default : persisted, heldByLayer ? layer : default }));
            Assert.That(afterClear, Is.EqualTo(new[] { local, default, default }));
            Assert.That(new[] { bundle.GetSlot(TestItem.AddressA, 3), bundle.GetSlot(TestItem.AddressA, 5), bundle.GetSlot(TestItem.AddressA, 6) }, Is.EqualTo(new[] { default, local, default }));
        }
    }

    [Test]
    public void SnapshotStore_ConcurrentWritersApplyOnDisposeAndReleasePreviousPayloads([Values] bool tombstones)
    {
        TrackingMemoryProvider memoryProvider = new();
        using PbtSnapshotBundle bundle = CreateBundle(new Reader(default, null));
        PbtNodePath groupPath = new([], 0);
        ValueHash256 groupHash = new(Value(1));
        byte[] encoding = PbtNodeGroupEncoder.Encode(groupPath, [new PbtNodeRecord(groupPath.ToPath<PbtStorageNodePath>(), BranchEncoding(1))], default);

        using (PbtSnapshotStore store = new(bundle))
        {
            System.Threading.Tasks.Parallel.For(0, 1000, iteration =>
            {
                using IPbtConcurrentWriter writer = store.CreateWriter();
                using RefCountingMemory payload = Memory(encoding, memoryProvider);
                PbtTraversalPath path = new(Span<byte>.Empty);
                writer.SetNodeGroup(path, groupHash, payload);
                writer.SetNodeGroup(path, groupHash, payload);
                if (tombstones) writer.SetNodeGroup(path, groupHash, null);
            });

            using RefCountingMemory? pending = bundle.GetNodeGroup(groupPath.ToPath<PbtStorageNodePath>(), groupHash);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pending, Is.Null, "handed-off groups stay invisible until the store is disposed");
                Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.EqualTo(1000));
            }
        }

        PbtSnapshot snapshot = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), default, out PbtTransientResource retired);
        retired.ReleaseLease();
        bool found = snapshot.Content.TryGetNodeGroup(groupPath, out RefCountingMemory? current);
        using (current)
        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(current?.GetSpan().ToArray(), Is.EqualTo(tombstones ? null : encoding));
            Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.EqualTo(tombstones ? 0 : 1));
        }

        snapshot.Dispose();
    }

    [Test]
    public void SnapshotContent_DisjointGroupReplacementAndResetPreserveReadLeases([Values(1, 16)] int groupCount, [Values] bool tombstone, [Values] bool replaceViaStoragePath)
    {
        using PbtSnapshotContent content = new();
        TrackingMemoryProvider memoryProvider = new();
        for (int round = 0; round < 2; round++)
        {
            RefCountingMemory?[] retained = new RefCountingMemory?[groupCount];
            byte[][] originalEncodings = new byte[groupCount][];
            long[] expectedNodeBytes = new long[groupCount];
            try
            {
                for (int index = 0; index < groupCount; index++)
                {
                    PbtNodePath groupPath = new([(byte)(index << 4)], 4);
                    PbtStorageNodePath storagePath = groupPath.ToPath<PbtStorageNodePath>();
                    Assert.That(content.TryGetNodeGroup(groupPath, out RefCountingMemory? missing), Is.False);
                    using (missing) Assert.That(missing, Is.Null);

                    byte[] original = PbtNodeGroupEncoder.Encode(groupPath, [new PbtNodeRecord(PbtTestPaths.PathOf(groupPath, 0).ToPath<PbtStorageNodePath>(), BranchEncoding((byte)(round + 1)))], default);
                    originalEncodings[index] = original;
                    using (RefCountingMemory payload = Memory(original, memoryProvider))
                    {
                        content.SetNodeGroup(groupPath, payload);
                        content.SetNodeGroup(groupPath, payload);
                    }
                    Assert.That(content.TryGetNodeGroup(storagePath, out retained[index]), Is.True);
                    Assert.That(retained[index], Is.Not.Null);

                    byte[] replacement = PbtNodeGroupEncoder.Encode(groupPath, [new PbtNodeRecord(PbtTestPaths.PathOf(groupPath, 0).ToPath<PbtStorageNodePath>(), BranchEncoding((byte)(round + 3)))], default);
                    using (RefCountingMemory? payload = tombstone ? null : Memory(replacement, memoryProvider))
                    {
                        if (replaceViaStoragePath) content.SetNodeGroup(storagePath, payload);
                        else content.SetNodeGroup(groupPath, payload);
                    }
                    bool found = content.TryGetNodeGroup(groupPath, out RefCountingMemory? current);
                    using (current)
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(found, Is.True);
                        Assert.That(current?.GetSpan().ToArray(), Is.EqualTo(tombstone ? null : replacement));
                        Assert.That(retained[index]!.GetSpan().ToArray(), Is.EqualTo(original));
                    }
                    expectedNodeBytes[index] = storagePath.ToPathArray().Length + (tombstone ? 0 : replacement.Length);
                }

                long nodeBytes = 0;
                foreach (long size in expectedNodeBytes) nodeBytes += size;
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(content.NodeGroupCount(), Is.EqualTo(groupCount));
                    Assert.That(content.GetPayloadSize(), Is.EqualTo(new PbtSnapshotPayloadSize(0, nodeBytes)));
                    Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.EqualTo(groupCount * (tombstone ? 1 : 2)));
                }

                content.Reset();
                content.Reset();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(content.NodeGroupCount(), Is.Zero);
                    Assert.That(content.GetPayloadSize(), Is.EqualTo(default(PbtSnapshotPayloadSize)));
                    Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.EqualTo(groupCount));
                    for (int index = 0; index < groupCount; index++)
                        Assert.That(retained[index]!.GetSpan().ToArray(), Is.EqualTo(originalEncodings[index]));
                }
            }
            finally
            {
                foreach (RefCountingMemory? payload in retained) ((IDisposable?)payload)?.Dispose();
            }
            Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
        }
    }

    [Test]
    public void Dispose_ReturnsTransientOnceEvenWhenOtherCleanupThrows()
    {
        using TrackingTransientPool pool = new() { ThrowOnBuilderReturn = true };
        PbtSnapshotBundle bundle = PbtSnapshotBundleTestExtensions.CreateBundle(pool, new Reader(default, null));
        Assert.Throws<IOException>(bundle.Dispose);
        Assert.DoesNotThrow(bundle.Dispose);
        Assert.That(pool.ReturnCount, Is.EqualTo(1));
    }

    private static PbtSnapshotBundle CreateBundle(Reader reader) =>
        PbtSnapshotBundleTestExtensions.CreateBundle(new PbtResourcePool(new PbtConfig()), reader);

    private sealed class TrackingTransientPool : IPbtResourcePool, IDisposable
    {
        private readonly Stack<PbtTransientResource> _available = new();
        private readonly List<PbtTransientResource> _resources = [];
        public PbtTransientResource? LastRented { get; private set; }
        public int ReturnCount { get; private set; }
        public bool ThrowOnBuilderReturn { get; init; }

        public PbtTransientResource GetCachedResource(PbtResourcePool.Usage usage)
        {
            if (!_available.TryPop(out PbtTransientResource? resource))
            {
                resource = new PbtTransientResource();
                _resources.Add(resource);
            }
            resource.OnRented(this, usage);
            return LastRented = resource;
        }

        public void ReturnCachedResource(PbtResourcePool.Usage usage, PbtTransientResource resource)
        {
            resource.Reset();
            _available.Push(resource);
            ReturnCount++;
        }

        public PbtSnapshotContent GetSnapshotContent(PbtResourcePool.Usage usage) => new();
        public void ReturnSnapshotContent(PbtResourcePool.Usage usage, PbtSnapshotContent content) => content.Dispose();
        public PbtWriteBatchBuilder<PbtPath> GetWriteBatch(PbtResourcePool.Usage usage) => new();
        public void ReturnWriteBatch(PbtResourcePool.Usage usage, PbtWriteBatchBuilder<PbtPath> builder)
        {
            builder.Dispose();
            if (ThrowOnBuilderReturn) throw new IOException("Builder return failed");
        }

        public PbtWriteBatchBuilder<PbtStoragePath> GetStorageWriteBatch(PbtResourcePool.Usage usage) => new();
        public void ReturnStorageWriteBatch(PbtResourcePool.Usage usage, PbtWriteBatchBuilder<PbtStoragePath> builder)
        {
            builder.Dispose();
            if (ThrowOnBuilderReturn) throw new IOException("Builder return failed");
        }

        public void Dispose()
        {
            foreach (PbtTransientResource resource in _resources) resource.Dispose();
        }
    }

    [Test]
    public void Storage_mutations_use_small_header_and_wide_storage_partitions([Values(0u, 63u, 64u, 256u, uint.MaxValue)] uint slotValue)
    {
        UInt256 slot = slotValue == uint.MaxValue ? UInt256.MaxValue : new UInt256(slotValue);
        PbtVariableTreeKey key = PbtStateKey.Slot(TestItem.AddressA, slot);
        using PbtSnapshotBundle bundle = CreateBundle(new Reader(key, null));
        EvmWord value = EvmWordSlot.FromStripped(Value(9));
        foreach (bool delete in new[] { false, true })
        {
            bundle.SetSlot(TestItem.AddressA, slot, delete ? default : value);
            using PbtPartitionBatches changes = bundle.PrepareLeafChanges();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(bundle.GetSlot(TestItem.AddressA, slot), Is.EqualTo(delete ? default : value));
                Assert.That(changes.Account?.ConsumeOperations().Length ?? 0, Is.EqualTo(slot < 64 ? 1 : 0));
                Assert.That(changes.Storage?.ConsumeOperations().Length ?? 0, Is.EqualTo(slot < 64 ? 0 : 1));
                Assert.That(changes.Code?.ConsumeOperations().Length ?? 0, Is.Zero);
            }
            bundle.CompleteLeafChanges();
        }
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void Trie_updates_leave_independently_staged_flat_entries_unchanged(bool leafExists, bool delete)
    {
        PbtVariableTreeKey key = PbtStateKey.Slot(TestItem.AddressA, 1);
        ValueHash256 flatValue = new(Value(9));
        using PbtSnapshotBundle bundle = CreateBundle(new Reader(key, null));
        ValueHash256 root;
        using (PbtSnapshotStore store = new(bundle)) root = store.Fold(default, leafExists ? [(key.Bytes.ToArray(), Value(1))] : []);
        bundle.SetSlot(TestItem.AddressA, 1, EvmWordSlot.FromStripped(flatValue.Bytes));

        ValueHash256 updatedRoot;
        using (PbtSnapshotStore store = new(bundle)) updatedRoot = store.Fold(root, [(key.Bytes.ToArray(), delete ? null : Value(2))]);

        using PbtSnapshotStore reader = new(bundle);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.GetSlot(TestItem.AddressA, 1), Is.EqualTo(EvmWordSlot.FromStripped(flatValue.Bytes)));
            Assert.That(updatedRoot, Is.EqualTo(delete ? default : PbtTreeHarness.HashLeaf(key.Bytes, Value(2))));
            Assert.That(reader.GetNode(new PbtNodePath([], 0), updatedRoot), Is.EqualTo(delete ? null : PbtTreeHarness.EncodeLeaf(key)));
        }
    }

    [NonParallelizable]
    [Test]
    public void Trie_cache_reuses_only_matching_subtree_hashes([Values(0UL, 1UL, 1048576UL)] ulong budget)
    {
        bool admitted = budget == 1048576;
        long initialHits = Metrics.PbtTrieCacheHits["account"];
        long initialMisses = Metrics.PbtTrieCacheMisses["account"];
        TrackingMemoryProvider memory = new();
        PbtNodePath path = new([], 0);
        byte[] encoding = PbtNodeGroupEncoder.Encode(path, [new PbtNodeRecord(path.ToPath<PbtStorageNodePath>(), BranchEncoding(1))], default);
        using PbtTrieNodeCache cache = new(new PbtConfig { AccountTrieNodeCacheSizeBudget = budget });
        Reader reader = new(default, null) { GroupPayload = encoding, MemoryProvider = memory, CurrentRoot = new ValueHash256(Value(1)) };
        PbtResourcePool pool = new(new PbtConfig());
        ReadStageAndCommit(reader, pool, cache, path, reader.CurrentRoot, encoding);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.GroupReadCount, Is.EqualTo(1));
            Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.EqualTo(admitted ? 1 : 0), "an admitted source allocation stays leased by the cache");
            Assert.That(cache.MemorySize, Is.LessThanOrEqualTo(budget));
        }
        PbtReadOnlySnapshotBundle readOnly = new(new(0), reader);
        using PbtSnapshotBundle bundle = new(PbtSnapshotBundleTestExtensions.Chain(pool, new PbtSnapshotContent()), readOnly, pool, PbtResourcePool.Usage.MainBlockProcessing, cache);
        Assert.That(bundle.TreeRoot, Is.Not.EqualTo(readOnly.TreeRoot));
        int readsBeforeDirectRead = reader.GroupReadCount;
        for (int read = 0; read < 2; read++)
        {
            using RefCountingMemory? payload = readOnly.GetNodeGroup(path.ToPath<PbtStorageNodePath>());
            Assert.That(payload!.GetSpan().ToArray(), Is.EqualTo(encoding));
        }
        Assert.That(reader.GroupReadCount, Is.EqualTo(readsBeforeDirectRead + 2), "direct read-only reads bypass the populated trie cache");
        int readsBeforeCachedRead = reader.GroupReadCount;
        using RefCountingMemory? cachedRead = bundle.GetNodeGroup(path.ToPath<PbtStorageNodePath>(), reader.CurrentRoot);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cachedRead!.Memory.ToArray(), Is.EqualTo(encoding));
            Assert.That(reader.GroupReadCount, Is.EqualTo(readsBeforeCachedRead + (admitted ? 0 : 1)), "the cache lookup must use the supplied subtree hash, not the local view root");
            Assert.That(Metrics.PbtTrieCacheHits["account"] - initialHits, Is.EqualTo(admitted ? 1 : 0));
            Assert.That(Metrics.PbtTrieCacheMisses["account"] - initialMisses, Is.EqualTo(admitted ? 1 : 2));
        }
    }

    [Test]
    public void Trie_cache_reuses_unchanged_descendants_across_roots()
    {
        PbtNodePath path = new(Bytes.FromHexString("00"), 4);
        byte[] originalNode = BranchEncoding(1);
        byte[] changedNode = BranchEncoding(2);
        ValueHash256 originalHash = PbtTreeHarness.HashBranch(originalNode);
        ValueHash256 changedHash = PbtTreeHarness.HashBranch(changedNode);
        PbtStorageNodePath childPath = PbtTestPaths.PathOf(path, 0).ToPath<PbtStorageNodePath>();
        byte[] original = PbtNodeGroupEncoder.Encode(path, [new PbtNodeRecord(childPath, originalNode)], default);
        byte[] changed = PbtNodeGroupEncoder.Encode(path, [new PbtNodeRecord(childPath, changedNode)], default);
        using PbtTrieNodeCache cache = new(new PbtConfig());
        PbtResourcePool pool = new(new PbtConfig());
        Reader reader = new(default, null) { GroupKey = path, GroupPayload = original, CurrentRoot = TestItem.KeccakA.ValueHash256 };
        ReadStageAndCommit(reader, pool, cache, path, originalHash, original);
        Assert.That(reader.GroupReadCount, Is.EqualTo(1));

        Reader forkReader = new(default, null) { GroupKey = path, GroupPayload = original, CurrentRoot = TestItem.KeccakB.ValueHash256 };
        using PbtSnapshotBundle fork = PbtSnapshotBundleTestExtensions.CreateBundle(pool, forkReader, cache);
        using (RefCountingMemory? payload = fork.GetNodeGroup(path.ToPath<PbtStorageNodePath>(), originalHash))
            Assert.That(payload!.Memory.ToArray(), Is.EqualTo(original));
        Assert.That(forkReader.GroupReadCount, Is.Zero, "an unrelated tree-root change must not invalidate this subtree");

        Reader changedReader = new(default, null) { GroupKey = path, GroupPayload = changed, CurrentRoot = TestItem.KeccakB.ValueHash256 };
        ReadStageAndCommit(changedReader, pool, cache, path, changedHash, changed);
        Assert.That(changedReader.GroupReadCount, Is.EqualTo(1), "different subtree hashes must miss even with the same whole-tree root");
        using PbtSnapshotBundle oldView = PbtSnapshotBundleTestExtensions.CreateBundle(pool, reader, cache);
        using (RefCountingMemory? payload = oldView.GetNodeGroup(path.ToPath<PbtStorageNodePath>(), originalHash))
            Assert.That(payload!.Memory.ToArray(), Is.EqualTo(original));
        Assert.That(reader.GroupReadCount, Is.EqualTo(2), "replacement must not make the old view return the new subtree");
    }

    /// <summary>Reads one group through a throwaway bundle over <paramref name="reader"/> and stages it as a fold would, then commits the block so the group reaches the shared cache.</summary>
    private static void ReadStageAndCommit(Reader reader, PbtResourcePool pool, PbtTrieNodeCache cache, PbtNodePath path, in ValueHash256 groupHash, byte[] expected)
    {
        using PbtSnapshotBundle bundle = PbtSnapshotBundleTestExtensions.CreateBundle(pool, reader, cache);
        using (RefCountingMemory? payload = bundle.GetNodeGroup(path.ToPath<PbtStorageNodePath>(), groupHash))
        {
            Assert.That(payload!.Memory.ToArray(), Is.EqualTo(expected));
            bundle.SetNodeGroup(path.ToPath<PbtStorageNodePath>(), groupHash, payload);
        }
        bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), groupHash, out PbtTransientResource retired).Dispose();
        cache.Add(retired);
        retired.ReleaseLease();
    }

    [NonParallelizable]
    [Test]
    public void Transient_stages_folded_groups_and_bulk_add_folds_them_into_the_shared_cache([ValueSource(nameof(CachePartitions))] string partition)
    {
        long initialEntries = Metrics.PbtTrieCacheEntries[partition];
        using PbtTrieNodeCache cache = new(CacheConfig(partition, 1048576));
        using TrackingTransientPool pool = new();
        TrackingMemoryProvider foldMemory = new();
        PbtNodePath foldedPath = CachePath(partition);
        byte[] first = PbtNodeGroupEncoder.Encode(foldedPath, [new PbtNodeRecord(PbtTestPaths.PathOf(foldedPath, 0).ToPath<PbtStorageNodePath>(), BranchEncoding(1))], default);
        byte[] second = PbtNodeGroupEncoder.Encode(foldedPath, [new PbtNodeRecord(PbtTestPaths.PathOf(foldedPath, 0).ToPath<PbtStorageNodePath>(), BranchEncoding(2))], default);
        ValueHash256 firstHash = new(Value(1));
        ValueHash256 secondHash = new(Value(2));
        Reader reader = new(default, null);
        PbtSnapshotBundle bundle = PbtSnapshotBundleTestExtensions.CreateBundle(pool, reader, cache);
        PbtTransientResource transient = pool.LastRented!;

        using (RefCountingMemory payload = Memory(first, foldMemory)) bundle.SetNodeGroup(foldedPath.ToPath<PbtStorageNodePath>(), firstHash, payload);
        using (RefCountingMemory payload = Memory(second, foldMemory)) bundle.SetNodeGroup(foldedPath.ToPath<PbtStorageNodePath>(), secondHash, payload);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(transient.NodeGroups.Count, Is.EqualTo(1), "a later fold of the same path supersedes the earlier one");
            Assert.That(TrackingMemoryProvider.CountUnreleased(foldMemory.Rented), Is.EqualTo(1), "the staged fold is leased as-is, never copied");
        }

        PbtSnapshot snapshot = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), default, out PbtTransientResource retired);
        Assert.That(retired, Is.SameAs(transient));
        cache.Add(retired);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.EntryCount, Is.EqualTo(1));
            Assert.That(Metrics.PbtTrieCacheEntries[partition] - initialEntries, Is.EqualTo(1));
            Assert.That(cache.TryGet(firstHash, foldedPath, out _), Is.False);
            Assert.That(cache.TryGet(secondHash, foldedPath, out RefCountingMemory? folded), Is.True);
            using (folded) Assert.That(folded!.Memory.ToArray(), Is.EqualTo(second));
        }
        retired.ReleaseLease();
        snapshot.Dispose();
        bundle.Dispose();
        cache.Clear();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.ReturnCount, Is.EqualTo(2));
            Assert.That(transient.NodeGroups.Count, Is.Zero);
            Assert.That(TrackingMemoryProvider.CountUnreleased(foldMemory.Rented), Is.Zero);
        }
    }

    // At a 1 MiB budget each shard holds a single set, so paths sharing the top hash byte share a set.
    [Test]
    public void Trie_cache_keeps_distinct_paths_in_the_same_set()
    {
        Dictionary<int, PbtNodePath> shards = [];
        PbtNodePath first = default;
        PbtNodePath second = default;
        bool found = false;
        for (int suffix = 0; suffix <= 256; suffix++)
        {
            PbtNodePath candidate = new(new byte[] { Eip8297KeyDerivation.AccountZone, 0, (byte)(suffix >> 8), (byte)suffix }, 32);
            int shard = (int)((uint)candidate.GetHashCode() >> 24);
            if (shards.TryGetValue(shard, out first))
            {
                second = candidate;
                found = true;
                break;
            }
            shards.Add(shard, candidate);
        }
        Assert.That(found, Is.True);
        using PbtTrieNodeCache cache = new(new PbtConfig { AccountTrieNodeCacheSizeBudget = 1048576 });
        using RefCountingMemory original = Memory(Bytes.FromHexString("010203"));
        using RefCountingMemory other = Memory(Bytes.FromHexString("040506"));
        ValueHash256 hash = TestItem.KeccakA.ValueHash256;
        cache.Add(hash, first, original);
        Assert.That(cache.TryGet(hash, second, out _), Is.False);
        cache.Add(hash, second, other);
        Assert.That(cache.TryGet(hash, first, out RefCountingMemory? firstPayload), Is.True);
        using (firstPayload) Assert.That(firstPayload!.Memory.ToArray(), Is.EqualTo(original.Memory.ToArray()));
        Assert.That(cache.TryGet(hash, second, out RefCountingMemory? secondPayload), Is.True);
        using (secondPayload) Assert.That(secondPayload!.Memory.ToArray(), Is.EqualTo(other.Memory.ToArray()));
    }

    [Test]
    public void Trie_cache_shares_canonical_paths_across_representations(
        [Values(0, 4, 8, 12, 272)] int depth,
        [Values(Eip8297KeyDerivation.AccountZone, Eip8297KeyDerivation.CodeZone, Eip8297KeyDerivation.StorageZone)] byte zone,
        [Values] bool storageFirst)
    {
        byte[] bytes = new byte[(depth + 7) / 8];
        if (bytes.Length > 0) bytes[0] = depth == 4 ? (byte)(zone & 0xF0) : zone;
        PbtNodePath narrow = new(bytes, depth);
        PbtStorageNodePath wide = new(bytes, depth);
        if (storageFirst) AssertCanonicalCachePaths(wide, narrow);
        else AssertCanonicalCachePaths(narrow, wide);
    }

    private static void AssertCanonicalCachePaths<TInserted, TRequested>(TInserted inserted, TRequested requested)
        where TInserted : struct, IPbtNodePath<TInserted>
        where TRequested : struct, IPbtNodePath<TRequested>
    {
        using PbtTrieNodeCache cache = new(new PbtConfig());
        using RefCountingMemory source = Memory(Bytes.FromHexString("010203"));
        cache.Add(default, inserted, source);
        long retainedSize = cache.MemorySize;
        Assert.That(cache.TryGet(default, requested, out RefCountingMemory? first), Is.True);
        using (first)
        {
            cache.Add(default, requested, source);
            Assert.That(cache.TryGet(default, inserted, out RefCountingMemory? second), Is.True);
            using (second)
            using (Assert.EnterMultipleScope())
            {
                Assert.That(PbtNodePathOperations.Equal(inserted, requested), Is.True);
                Assert.That(inserted.GetHashCode(), Is.EqualTo(requested.GetHashCode()));
                Assert.That(second, Is.SameAs(first), "equivalent fill must retain the existing cache entry");
                Assert.That(second!.GetSpan().ToArray(), Is.EqualTo(Bytes.FromHexString("010203")));
                Assert.That(cache.MemorySize, Is.EqualTo(retainedSize));
                Assert.That(cache.TryGet(new ValueHash256(Value(1)), requested, out _), Is.False);
            }
        }
    }

    private static readonly string[] CachePartitions = ["account", "code", "storage"];

    private static PbtConfig CacheConfig(string partition, ulong budget) => new()
    {
        AccountTrieNodeCacheSizeBudget = partition == "account" ? budget : 1048576,
        CodeTrieNodeCacheSizeBudget = partition == "code" ? budget : 1048576,
        StorageTrieNodeCacheSizeBudget = partition == "storage" ? budget : 1048576,
    };

    private static PbtNodePath CachePath(string partition) => new(Bytes.FromHexString(partition switch
    {
        "account" => "00",
        "code" => "01",
        _ => "ff",
    }), 8);

    [NonParallelizable]
    [TestCase("", 0, "account")]
    [TestCase("00", 4, "account")]
    [TestCase("f0", 4, "storage")]
    [TestCase("00", 8, "account")]
    [TestCase("01", 8, "code")]
    [TestCase("ff", 8, "storage")]
    [TestCase("00a0", 12, "account")]
    [TestCase("01a0", 12, "code")]
    [TestCase("ffa0", 12, "storage")]
    [TestCase("ff", 528, "storage")]
    public void Trie_cache_routes_memory_and_lookup_metrics_by_partition(string hex, int depth, string partition)
    {
        byte[] bytes = new byte[(depth + 7) / 8];
        Bytes.FromHexString(hex).CopyTo(bytes, 0);
        PbtStorageNodePath path = new(bytes, depth);
        long[] initialMemory = new long[3];
        long[] initialEntries = new long[3];
        long[] initialHits = new long[3];
        long[] initialMisses = new long[3];
        for (int index = 0; index < CachePartitions.Length; index++)
        {
            string label = CachePartitions[index];
            initialMemory[index] = Metrics.PbtTrieCacheMemory[label];
            initialEntries[index] = Metrics.PbtTrieCacheEntries[label];
            initialHits[index] = Metrics.PbtTrieCacheHits[label];
            initialMisses[index] = Metrics.PbtTrieCacheMisses[label];
        }
        using PbtTrieNodeCache cache = new(CacheConfig(partition, 1048576));
        using RefCountingMemory source = Memory(Bytes.FromHexString("010203"));
        Assert.That(cache.TryGet(default, path, out _), Is.False);
        cache.Add(default, path, source);
        Assert.That(cache.TryGet(default, path, out RefCountingMemory? retained), Is.True);
        using (retained)
        {
            Assert.That(cache.TryGet(new ValueHash256(Value(1)), path, out _), Is.False);
            long retainedSize = cache.MemorySize;
            Assert.That(retainedSize, Is.GreaterThan(0));
            AssertMetrics(retainedSize, 1, 1, 2);
            cache.Clear();
            AssertMetrics(0, 0, 1, 2);
            cache.Add(default, path, source);
            AssertMetrics(retainedSize, 1, 1, 2);
            cache.Dispose();
            cache.Add(default, path, source);
            Assert.That(cache.TryGet(default, path, out _), Is.False);
            AssertMetrics(0, 0, 1, 3);
            Assert.That(retained!.GetSpan().ToArray(), Is.EqualTo(source.GetSpan().ToArray()));
        }

        void AssertMetrics(long memory, long entries, long hits, long misses)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cache.MemorySize, Is.EqualTo(memory));
                Assert.That(cache.EntryCount, Is.EqualTo(entries));
                for (int index = 0; index < CachePartitions.Length; index++)
                {
                    string label = CachePartitions[index];
                    Assert.That(Metrics.PbtTrieCacheMemory[label] - initialMemory[index], Is.EqualTo(label == partition ? memory : 0), label);
                    // The entry gauge is set to the partition total, so an untouched partition keeps whatever an earlier cache left.
                    Assert.That(Metrics.PbtTrieCacheEntries[label], Is.EqualTo(label == partition ? entries : initialEntries[index]), label);
                    Assert.That(Metrics.PbtTrieCacheHits[label] - initialHits[index], Is.EqualTo(label == partition ? hits : 0), label);
                    Assert.That(Metrics.PbtTrieCacheMisses[label] - initialMisses[index], Is.EqualTo(label == partition ? misses : 0), label);
                }
            }
        }
    }

    [Test, NonParallelizable]
    public void Trie_cache_partition_budget_does_not_disable_other_partitions(
        [ValueSource(nameof(CachePartitions))] string partition, [Values(0UL, 1UL, 1048576UL)] ulong budget)
    {
        using PbtTrieNodeCache cache = new(CacheConfig(partition, budget));
        using RefCountingMemory source = Memory(Bytes.FromHexString("010203"));
        foreach (string label in CachePartitions)
        {
            long initialMisses = Metrics.PbtTrieCacheMisses[label];
            PbtNodePath path = CachePath(label);
            cache.Add(default, path, source);
            bool expectedHit = label != partition || budget == 1048576;
            Assert.That(cache.TryGet(default, path, out RefCountingMemory? payload), Is.EqualTo(expectedHit), label);
            using (payload)
                Assert.That(Metrics.PbtTrieCacheMisses[label] - initialMisses, Is.EqualTo(expectedHit ? 0 : 1), label);
        }
    }

    private static PbtNodePath CachePath(string partition, int index) =>
        new([CachePath(partition).GetByte(0), (byte)(index >> 8), (byte)index], 24);

    [Test]
    public void Trie_cache_over_budget_insert_evicts_one_entry_not_the_shard([ValueSource(nameof(CachePartitions))] string partition)
    {
        using PbtTrieNodeCache cache = new(CacheConfig(partition, 1048576));
        using RefCountingMemory source = Memory(new byte[1600]);
        for (int count = 1; count <= 2048; count++)
        {
            cache.Add(default, CachePath(partition, count), source);
            int hits = 0;
            for (int index = 1; index <= count; index++)
            {
                if (!cache.TryGet(default, CachePath(partition, index), out RefCountingMemory? payload)) continue;
                hits++;
                ((IDisposable)payload).Dispose();
            }
            if (hits == count) continue;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(hits, Is.EqualTo(count - 1), "the first shard overflow evicts exactly one entry");
                Assert.That(cache.MemorySize, Is.LessThanOrEqualTo(1048576));
            }
            return;
        }
        Assert.Fail("no shard overflowed");
    }

    [Test, NonParallelizable]
    public void Trie_cache_partition_eviction_preserves_other_partitions([ValueSource(nameof(CachePartitions))] string partition)
    {
        long[] initialMemory = new long[CachePartitions.Length];
        for (int index = 0; index < CachePartitions.Length; index++)
            initialMemory[index] = Metrics.PbtTrieCacheMemory[CachePartitions[index]];
        using PbtTrieNodeCache cache = new(CacheConfig(partition, 1048576));
        RefCountingMemory source = Memory(Bytes.FromHexString("010203"));
        foreach (string label in CachePartitions) cache.Add(default, CachePath(label), source);
        // Slot tables differ per partition: storage slots carry the wider path.
        long[] entryMemory = new long[CachePartitions.Length];
        for (int index = 0; index < CachePartitions.Length; index++)
            entryMemory[index] = Metrics.PbtTrieCacheMemory[CachePartitions[index]] - initialMemory[index];
        PbtNodePath path = CachePath(partition);
        ValueHash256 replacementRoot = new(Value(1));
        cache.Add(replacementRoot, path, source);
        Assert.That(cache.TryGet(default, path, out _), Is.False);
        Assert.That(cache.TryGet(replacementRoot, path, out RefCountingMemory? retained), Is.True);
        using (retained)
        {
            using RefCountingMemory larger = Memory(new byte[1600]);
            cache.Add(new ValueHash256(Value(2)), path, larger);
            Assert.That(cache.TryGet(replacementRoot, path, out _), Is.False);
            foreach (string label in CachePartitions)
            {
                if (label == partition) continue;
                Assert.That(cache.TryGet(default, CachePath(label), out RefCountingMemory? payload), Is.True, label);
                ((IDisposable)payload!).Dispose();
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(retained!.GetSpan().ToArray(), Is.EqualTo(source.GetSpan().ToArray()));
                long totalMemory = 0;
                for (int index = 0; index < CachePartitions.Length; index++)
                {
                    string label = CachePartitions[index];
                    long memory = Metrics.PbtTrieCacheMemory[label] - initialMemory[index];
                    Assert.That(memory, Is.EqualTo(entryMemory[index] + (label == partition ? larger.Capacity - source.Capacity : 0)), label);
                    totalMemory += memory;
                }
                Assert.That(cache.MemorySize, Is.EqualTo(totalMemory));
                Assert.That(cache.EntryCount, Is.EqualTo(CachePartitions.Length));
                foreach (string label in CachePartitions) Assert.That(Metrics.PbtTrieCacheEntries[label], Is.EqualTo(1), label);
            }
            cache.Dispose();
            cache.Add(default, path, source);
            Assert.That(cache.EntryCount, Is.Zero);
            for (int index = 0; index < CachePartitions.Length; index++)
            {
                Assert.That(Metrics.PbtTrieCacheMemory[CachePartitions[index]], Is.EqualTo(initialMemory[index]));
                Assert.That(Metrics.PbtTrieCacheEntries[CachePartitions[index]], Is.Zero);
            }
        }
        ((IDisposable)source).Dispose();
        Assert.That(source.TryAcquireLease(), Is.False, "the cache released every lease it took");
    }

    [Test]
    public void Trie_cache_leases_every_payload_without_copying([Values] bool rocksDbBacked)
    {
        using PbtTrieNodeCache cache = new(CacheConfig("account", 1048576));
        PbtNodePath path = CachePath("account");
        RefCountingMemory source = rocksDbBacked
            ? RefCountingMemory.OwningRocksDb(ArrayMemoryManager.From(new byte[1600])!)
            : Memory(new byte[1600]);
        cache.Add(default, path, source);
        Assert.That(cache.TryGet(default, path, out RefCountingMemory? hit), Is.True);
        using (hit) Assert.That(hit, Is.SameAs(source));
        ((IDisposable)source).Dispose();
        Assert.That(source.TryAcquireLease(), Is.True, "the cache's lease keeps the payload alive after its producer releases it");
        ((IDisposable)source).Dispose();
        cache.Clear();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.TryAcquireLease(), Is.False);
            Assert.That(cache.MemorySize, Is.Zero);
        }
    }

    [Test]
    public void Trie_cache_concurrent_hits_and_eviction_keep_payloads_alive()
    {
        using PbtTrieNodeCache cache = new(CacheConfig("account", 1048576));
        RefCountingMemory[] sources = [Memory(Bytes.FromHexString("010203")), Memory(new byte[1600]), Memory(new byte[8])];
        (PbtNodePath Path, int Variant) Entry(int iteration) =>
            (CachePath(CachePartitions[iteration % CachePartitions.Length]), (iteration / CachePartitions.Length) % sources.Length);
        // Writes to the cache come from one thread at a time; lookups run concurrently with them.
        System.Threading.Tasks.Parallel.Invoke(
            () =>
            {
                for (int iteration = 0; iteration < 10000; iteration++)
                {
                    (PbtNodePath path, int variant) = Entry(iteration);
                    cache.Add(new ValueHash256(Value((byte)variant)), path, sources[variant]);
                    if ((iteration & 63) == 0) cache.Clear();
                }
            },
            () => System.Threading.Tasks.Parallel.For(0, 10000, iteration =>
            {
                (PbtNodePath path, int variant) = Entry(iteration);
                if (cache.TryGet(new ValueHash256(Value((byte)variant)), path, out RefCountingMemory? payload))
                    using (payload) Assert.That(payload.GetSpan().ToArray(), Is.EqualTo(sources[variant].GetSpan().ToArray()), "a lock-free hit must return the payload admitted under its own subtree hash");
            }));
        foreach (RefCountingMemory source in sources) ((IDisposable)source).Dispose();
        cache.Clear();
        Assert.That(cache.MemorySize, Is.Zero);
    }

    [Test]
    public void Child_cache_concurrent_replacements_release_every_superseded_payload()
    {
        TrackingMemoryProvider memoryProvider = new();
        PbtTrieNodeCache.ChildCache child = new(16);
        PbtNodePath path = CachePath("account");
        byte[][] encodings = [Bytes.FromHexString("01"), Bytes.FromHexString("0202")];
        System.Threading.Tasks.Parallel.For(0, 10000, iteration =>
        {
            int variant = iteration & 1;
            ValueHash256 groupHash = new(Value((byte)variant));
            using RefCountingMemory payload = Memory(encodings[variant], memoryProvider);
            child.Set(groupHash, path, payload);
        });
        Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.EqualTo(1), "only the surviving entry keeps its payload");
        child.Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
    }

    [TestCase(0, false)]
    [TestCase(1, false)]
    [TestCase(2, false)]
    [TestCase(3, false)]
    [TestCase(1, true)]
    [TestCase(2, true)]
    [TestCase(3, true)]
    public void Node_group_newest_full_replacement_or_tombstone_stops_fallback(int newestTier, bool tombstone)
    {
        PbtNodePath groupKey = new([], 0);
        PbtStorageNodePath wideGroupKey = new([], 0);
        byte[] persisted = PbtNodeGroupEncoder.Encode(groupKey, [new PbtNodeRecord(groupKey.ToPath<PbtStorageNodePath>(), BranchEncoding(1)),
            new PbtNodeRecord(PbtTestPaths.PathOf(groupKey, 14).ToPath<PbtStorageNodePath>(), BranchEncoding(2))], default);
        byte[] shared = PbtNodeGroupEncoder.Encode(groupKey, [new PbtNodeRecord(groupKey.ToPath<PbtStorageNodePath>(), BranchEncoding(3))], default);
        byte[] local = PbtNodeGroupEncoder.Encode(groupKey, [new PbtNodeRecord(groupKey.ToPath<PbtStorageNodePath>(), BranchEncoding(4))], default);
        byte[] write = PbtNodeGroupEncoder.Encode(groupKey, [new PbtNodeRecord(groupKey.ToPath<PbtStorageNodePath>(), BranchEncoding(5))], default);
        Reader reader = new(new PbtVariableTreeKey([0]), null) { GroupPayload = persisted };
        PbtResourcePool pool = new(new PbtConfig());
        PbtSnapshotPooledList sharedSnapshots = newestTier >= 1
            ? PbtSnapshotBundleTestExtensions.Chain(pool, Content(groupKey, persisted), Content(wideGroupKey, newestTier == 1 && tombstone ? null : shared))
            : new(0);
        PbtSnapshotPooledList localSnapshots = newestTier >= 2
            ? PbtSnapshotBundleTestExtensions.Chain(pool, Content(groupKey, shared), Content(wideGroupKey, newestTier == 2 && tombstone ? null : local))
            : new(0);
        using PbtTrieNodeCache cache = new(new PbtConfig());
        if (newestTier >= 2)
        {
            using RefCountingMemory cached = Memory(shared);
            cache.Add(TestItem.KeccakA.ValueHash256, groupKey, cached);
        }
        using PbtSnapshotBundle bundle = new(localSnapshots, new PbtReadOnlySnapshotBundle(sharedSnapshots, reader), pool, PbtResourcePool.Usage.MainBlockProcessing, cache);
        if (newestTier == 3)
        {
            using RefCountingMemory? payload = tombstone ? null : Memory(write);
            bundle.SetNodeGroup(wideGroupKey, TestItem.KeccakA.ValueHash256, payload);
        }

        using RefCountingMemory? actual = bundle.GetNodeGroup(wideGroupKey, TestItem.KeccakA.ValueHash256);
        byte[]? expected = tombstone ? null : newestTier switch { 0 => persisted, 1 => shared, 2 => local, _ => write };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual?.Memory.ToArray(), Is.EqualTo(expected));
            Assert.That(reader.GroupReadCount, Is.EqualTo(newestTier == 0 ? 1 : 0));
        }
    }

    [Test]
    public void Failed_group_read_during_fold_leaves_the_write_buffer_unchanged([Values] bool malformed)
    {
        TrackingMemoryProvider memoryProvider = new();
        PbtVariableTreeKey originalLeafKey = PbtStateKey.Slot(TestItem.AddressA, 1);
        ValueHash256 originalLeafValue = new(Value(2));
        PbtNodePath originalNodePath = new([0x80], 4);
        byte[] originalNode = PbtNodeGroupEncoder.Encode(originalNodePath, [new PbtNodeRecord(PbtTestPaths.PathOf(originalNodePath, 0).ToPath<PbtStorageNodePath>(), BranchEncoding(1))], default);
        Reader reader = new(new PbtVariableTreeKey([0]), null)
        {
            GroupPayload = malformed ? Bytes.FromHexString("01") : null,
            GroupReadException = malformed ? null : new InvalidDataException("Configured group read failure."),
            MemoryProvider = memoryProvider,
        };
        using PbtSnapshotBundle bundle = CreateBundle(reader);
        bundle.SetSlot(TestItem.AddressA, 1, EvmWordSlot.FromStripped(originalLeafValue.Bytes));
        using RefCountingMemory originalPayload = Memory(originalNode);
        bundle.SetNodeGroup(originalNodePath.ToPath<PbtStorageNodePath>(), TestItem.KeccakA.ValueHash256, originalPayload);

        CountingStore store = new(bundle);
        Action fold = () => store.Fold(new ValueHash256(Value(5)), [(PbtStoreTestExtensions.ZoneKey("0003"), Value(4))]);
        if (malformed) Assert.Catch(fold);
        else Assert.Throws<InvalidDataException>(fold);

        bundle.CompleteLeafChanges();
        using PbtSnapshot snapshot = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), default);
        bool foundGroup = snapshot.Content.TryGetNodeGroup(originalNodePath, out RefCountingMemory? group);
        using RefCountingMemory? groupLease = group;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.GroupReadCount, Is.EqualTo(1));
            Assert.That(store.ApplyCount, Is.Zero);
            Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
            Assert.That(snapshot.Content.HeaderStorages, Has.Count.EqualTo(1));
            Assert.That(snapshot.Content.GetSlot(originalLeafKey), Is.EqualTo(EvmWordSlot.FromStripped(originalLeafValue.Bytes)));
            Assert.That(snapshot.Content.NodeGroupCount(), Is.EqualTo(1));
            Assert.That(foundGroup, Is.True);
            Assert.That(group!.GetSpan().ToArray(), Is.EqualTo(originalNode));
        }
    }

    [Test]
    public void Code_bearing_account_matches_pinned_eip_root()
    {
        using PbtSnapshotBundle bundle = CreateBundle(new Reader(default, null));
        Address address = new("0x0000000000000000000000000000000000000001");
        byte[] bytes = Bytes.FromHexString("6001");
        Account account = Build.An.Account.WithNonce(1).WithBalance(2).WithCode(bytes).TestObject;
        SetAccountWithCode(bundle, address, account, new CodeInfo(bytes), codeFirst: false);
        // EIP-8297 at d2a64c2d: literal Python mapping and merkelization, using BLAKE3.
        ValueHash256 expected = new("0x1d6376e73eb20356030d5335b0297c6d29b1222e9522450c33406126f6f9f5ee");
        Assert.That(bundle.Fold(default), Is.EqualTo(expected));
    }

    [Test]
    public void Whole_account_and_code_survive_fold_and_seal_in_either_write_order([Values] bool codeFirst, [Values(0ul, 9ul)] ulong nonce)
    {
        using PbtSnapshotBundle bundle = CreateBundle(new Reader(default, null));
        byte[] bytes = new byte[160];
        Bytes.FromHexString("6001600055").CopyTo(bytes, 0);
        CodeInfo code = new(bytes);
        Account account = Build.An.Account.WithNonce(nonce).WithBalance(nonce).WithStorageRoot(TestItem.KeccakB).WithCode(bytes).TestObject;
        // PBT keeps no storage root, so the account reads back over the empty tree.
        Account stored = account.WithChangedStorageRoot(Keccak.EmptyTreeHash);
        SetAccountWithCode(bundle, TestItem.AddressA, account, code, codeFirst);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.GetAccount(TestItem.AddressA), Is.EqualTo(stored));
            Assert.That(bundle.GetCode(account.CodeHash.ValueHash256), Is.SameAs(code));
        }
        ValueHash256 root = bundle.Fold(default);
        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, nonce, nonce, bytes);
        Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)));
        Assert.That(bundle.Fold(root), Is.EqualTo(root));
        using PbtSnapshot snapshot = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), root);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.GetAccount(TestItem.AddressA), Is.EqualTo(stored));
            Assert.That(bundle.GetCode(account.CodeHash.ValueHash256), Is.SameAs(code));
            Assert.That(snapshot.Content.Accounts[PbtStateKey.AddressKeyHash(TestItem.AddressA)]?.ToAccount(), Is.EqualTo(stored));
            Assert.That(snapshot.Content.Codes[account.CodeHash.ValueHash256], Is.SameAs(code));
        }
        Account rebalanced = account.WithChangedBalance(nonce + 1);
        bundle.SetAccount(TestItem.AddressA, rebalanced);
        int pendingAfterBalanceChange = bundle.PendingMutationCount;
        root = bundle.Fold(root);
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, nonce, nonce + 1, bytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pendingAfterBalanceChange, Is.EqualTo(3), "a balance change stages header leaves only, never code chunks");
            Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)));
        }
    }

    [Test]
    public void Concurrent_slot_account_and_code_writes_are_all_kept()
    {
        const int WritesPerSlot = 2000;
        const int AccountCount = 64;
        using PbtSnapshotBundle bundle = CreateBundle(new Reader(default, null));
        static byte[] CodeOf(int index) => [0x60, (byte)index];
        static Account AccountOf(int index) => Build.An.Account.WithNonce((ulong)index + 1).WithCode(CodeOf(index)).TestObject;

        System.Threading.Tasks.Parallel.Invoke(
            // Every slot of one run has its own writer, so a replacement built from a stale run would undo another slot's write.
            () => System.Threading.Tasks.Parallel.For(0, SlotRun.Width, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = SlotRun.Width }, slot =>
            {
                for (uint write = 1; write <= WritesPerSlot; write++)
                {
                    UInt256 value = write;
                    bundle.SetSlot(TestItem.AddressA, (UInt256)(uint)slot, EvmWordSlot.FromUInt256(in value));
                }
            }),
            () => System.Threading.Tasks.Parallel.For(0, AccountCount, index => bundle.SetAccount(TestItem.Addresses[index], AccountOf(index))),
            () => System.Threading.Tasks.Parallel.For(0, AccountCount, index =>
                bundle.SetCode(AccountOf(index).CodeHash.ValueHash256, new CodeInfo(CodeOf(index)))));

        Dictionary<string, byte[]> model = [];
        for (uint slot = 0; slot < SlotRun.Width; slot++) PbtReferenceModel.SetSlot(model, TestItem.AddressA, slot, WritesPerSlot);
        for (int index = 0; index < AccountCount; index++) PbtReferenceModel.SetAccount(model, TestItem.Addresses[index], (ulong)index + 1, 0, CodeOf(index));
        UInt256 lastWrite = WritesPerSlot;
        using (Assert.EnterMultipleScope())
        {
            for (uint slot = 0; slot < SlotRun.Width; slot++)
                Assert.That(bundle.GetSlot(TestItem.AddressA, slot), Is.EqualTo(EvmWordSlot.FromUInt256(in lastWrite)), $"slot {slot}");
            Assert.That(bundle.Fold(default), Is.EqualTo(PbtReferenceModel.Root(model)));
        }
    }

    [Test]
    public void Shared_code_is_reapplied_for_each_holder_across_folds([Values] bool reverseOrder, [Values(1, 129, 258)] int chunkCount)
    {
        using PbtSnapshotBundle bundle = CreateBundle(new Reader(default, null));
        byte[] bytes = new byte[chunkCount * 31];
        bytes.AsSpan().Fill(0x5b);
        CodeInfo code = new(bytes);
        Account account = Build.An.Account.WithCode(bytes).TestObject;
        SetAccountWithCode(bundle, TestItem.AddressA, account, code, codeFirst: !reverseOrder);
        ValueHash256 root = bundle.Fold(default);
        using PbtSnapshot original = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), root);
        bundle.SetAccount(TestItem.AddressB, account);
        root = bundle.Fold(root);
        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, account.Nonce, account.Balance, bytes);
        PbtReferenceModel.SetAccount(model, TestItem.AddressB, account.Nonce, account.Balance, bytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)));
            Assert.That(original.Content.Codes[account.CodeHash.ValueHash256], Is.SameAs(code));
            Assert.That(bundle.GetCode(account.CodeHash.ValueHash256), Is.SameAs(code));
        }
    }

    private static readonly byte[] RealCode = Bytes.FromHexString("6001");
    private static readonly byte[] OtherRealCode = Bytes.FromHexString("6002");
    private static readonly byte[] Delegation = Bytes.FromHexString("ef01000000000000000000000000000000000000000001");

    public static IEnumerable<TestCaseData> CodeHashTransitionCases()
    {
        yield return new TestCaseData(RealCode, OtherRealCode, typeof(InvalidOperationException)).SetName("Real_to_other_real_throws");
        yield return new TestCaseData(RealCode, null, typeof(InvalidOperationException)).SetName("Real_to_deleted_throws");
        yield return new TestCaseData(RealCode, Delegation, typeof(InvalidOperationException)).SetName("Real_to_delegation_throws");
        yield return new TestCaseData(RealCode, RealCode, null).SetName("Real_to_same_real_is_allowed");
        yield return new TestCaseData(Array.Empty<byte>(), RealCode, null).SetName("Empty_to_real_is_allowed");
    }

    [TestCaseSource(nameof(CodeHashTransitionCases))]
    public void Code_hash_transitions_without_a_reference_count(byte[] previousCode, byte[]? nextCode, Type? expectedException)
    {
        using PbtSnapshotBundle bundle = CreateBundle(new Reader(default, null));
        Account previous = Build.An.Account.WithNonce(1).WithCode(previousCode).TestObject;
        Account? next = nextCode is null ? null : Build.An.Account.WithNonce(2).WithCode(nextCode).TestObject;
        Set(previous, previousCode);
        ValueHash256 root = bundle.Fold(default);
        if (expectedException is not null)
        {
            Assert.Throws(expectedException, () => Set(next, nextCode));
            return;
        }
        Set(next, nextCode);
        Dictionary<string, byte[]> model = [];
        if (next is not null) PbtReferenceModel.SetAccount(model, TestItem.AddressA, next.Nonce, next.Balance, nextCode);
        Assert.That(bundle.Fold(root), Is.EqualTo(PbtReferenceModel.Root(model)));

        void Set(Account? account, byte[]? code) =>
            SetAccountWithCode(bundle, TestItem.AddressA, account, code is null ? null : new CodeInfo(code), codeFirst: true);
    }

    [Test]
    public void Replacing_unknown_previous_code_throws()
    {
        using PbtSnapshotBundle bundle = CreateBundle(new Reader(default, null));
        bundle.SetAccount(TestItem.AddressA, Build.An.Account.WithCode(RealCode).TestObject);
        Assert.Throws<InvalidDataException>(() => bundle.SetAccount(TestItem.AddressA, Build.An.Account.WithCode(OtherRealCode).TestObject));
    }

    [Test]
    public void Delegation_header_matches_eip_preimages()
    {
        Account account = Build.An.Account.WithCode(Delegation).TestObject;
        ValueHash256 addressHash = PbtStateKey.AddressKeyHash(TestItem.AddressA);
        Dictionary<PbtPath, ValueHash256> expected = new()
        {
            [new PbtPath([0, .. addressHash.Bytes, 0])] = new(Bytes.FromHexString("0000000000000017000000000000000000000000000000000000000000000000")),
            [new PbtPath([0, .. addressHash.Bytes, 2])] = new([.. Delegation, .. new byte[9]]),
        };
        Assert.That(PbtFlatState.AccountLeaves(addressHash, account, new CodeInfo(Delegation)), Is.EquivalentTo(expected));
    }

    [Test]
    public void Delegation_transitions_match_reference_leaves([Values] bool codeFirst, [Values] bool foldEachChange)
    {
        using PbtSnapshotBundle bundle = CreateBundle(new Reader(default, null));
        byte[] secondDelegation = Bytes.FromHexString("ef01000000000000000000000000000000000000000002");
        ValueHash256 root = default;
        Dictionary<Address, byte[]> accounts = [];
        Set(TestItem.AddressA, Delegation);
        Set(TestItem.AddressB, Delegation);
        Set(TestItem.AddressA, secondDelegation);
        Set(TestItem.AddressA, []);
        Set(TestItem.AddressA, Delegation);
        Set(TestItem.AddressA, null);
        Set(TestItem.AddressB, null);
        Assert.That(bundle.GetAccount(TestItem.AddressB), Is.Null);
        Assert.That(bundle.Fold(root), Is.EqualTo(default(ValueHash256)));

        void Set(Address address, byte[]? bytes)
        {
            Account? account = bytes is null ? null : Build.An.Account.WithNonce(1).WithCode(bytes).TestObject;
            SetAccountWithCode(bundle, address, account, bytes is null ? null : new CodeInfo(bytes), codeFirst);
            if (bytes is null) accounts.Remove(address);
            else accounts[address] = bytes;
            Dictionary<string, byte[]> model = [];
            foreach ((Address owner, byte[] ownerCode) in accounts) PbtReferenceModel.SetAccount(model, owner, 1, 0, ownerCode);
            if (foldEachChange)
            {
                root = bundle.Fold(root);
                Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)), "incremental root");
            }
        }
    }

    [Test]
    public void Code_resolution_preserves_other_pending_accounts_and_survives_scratch_reuse([Values(0, 1, 40)] int accountCount)
    {
        using TrackingTransientPool pool = new();
        using PbtSnapshotBundle bundle = PbtSnapshotBundleTestExtensions.CreateBundle(pool, new Reader(default, null));
        byte[] bytes = Bytes.FromHexString("6001600055");
        byte[] otherBytes = Bytes.FromHexString("6002600055");
        Account account = Build.An.Account.WithCode(bytes).TestObject;
        Account otherAccount = Build.An.Account.WithCode(otherBytes).TestObject;
        bundle.SetAccount(TestItem.AddressA, otherAccount);
        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, 0, 0, otherBytes);
        for (int index = 0; index < accountCount; index++)
        {
            byte[] addressBytes = new byte[Address.Size];
            addressBytes[^1] = (byte)index;
            Address address = new(addressBytes);
            bundle.SetAccount(address, account);
            PbtReferenceModel.SetAccount(model, address, 0, 0, bytes);
        }

        bundle.SetCode(account.CodeHash.ValueHash256, new CodeInfo(bytes));
        Assert.Throws<InvalidDataException>(() => bundle.Fold(default));
        bundle.SetCode(otherAccount.CodeHash.ValueHash256, new CodeInfo(otherBytes));
        ValueHash256 root = bundle.Fold(default);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)));
            Assert.That(bundle.PendingMutationCount, Is.Zero);
        }
    }

    [Test]
    public void Persisted_code_is_read_once_per_bundle_and_never_snapshotted()
    {
        Reader reader = new(default, null);
        using PbtSnapshotBundle bundle = CreateBundle(reader);
        byte[] bytes = Bytes.FromHexString("6001600055");
        Account account = Build.An.Account.WithBalance(1).WithCode(bytes).TestObject;
        ValueHash256 codeHash = account.CodeHash.ValueHash256;
        CodeInfo persisted = new(bytes);
        reader.Codes[codeHash] = persisted;
        bundle.SetAccount(TestItem.AddressA, account);
        ValueHash256 root = bundle.Fold(default);
        using PbtSnapshot first = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), root);
        Account updated = account.WithChangedBalance(2);
        bundle.SetAccount(TestItem.AddressA, updated);
        int pendingAfterBalanceChange = bundle.PendingMutationCount;
        root = bundle.Fold(root);
        using PbtSnapshot second = bundle.CollectSnapshot(new StateId(1, default), new StateId(2, default), root);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pendingAfterBalanceChange, Is.EqualTo(3), "an account write stages header leaves only, never code chunks");
            Assert.That(reader.CodeReadCount, Is.EqualTo(1), "persisted code is memoized per bundle");
            Assert.That(bundle.GetCode(codeHash), Is.SameAs(persisted));
            Assert.That(first.Content.Codes, Is.Empty, "memoized code must not be snapshotted");
            Assert.That(second.Content.Codes, Is.Empty, "memoized code must not be snapshotted");
        }
    }

    [Test]
    public void Hinted_account_serves_the_write_path_without_a_persistence_read_and_never_shadows_a_write()
    {
        Account persisted = Build.An.Account.WithBalance(1).TestObject;
        Reader reader = new(default, null) { Accounts = [new(PbtStateKey.AddressKeyHash(TestItem.AddressA), persisted)] };
        using PbtSnapshotBundle bundle = CreateBundle(reader);
        bundle.HintAccount(TestItem.AddressA, persisted);
        Account written = persisted.WithChangedBalance(2);
        bundle.SetAccount(TestItem.AddressA, written);
        bundle.HintAccount(TestItem.AddressA, persisted);
        bundle.HintAccount(TestItem.AddressB, null);
        Account? hintedA = bundle.GetAccount(TestItem.AddressA);
        Account? hintedB = bundle.GetAccount(TestItem.AddressB);
        int readsBeforeCollect = reader.AccountReadCount;
        using PbtSnapshot snapshot = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), bundle.Fold(default));
        bundle.GetAccount(TestItem.AddressB);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(readsBeforeCollect, Is.Zero, "hinted accounts are not read from persistence");
            Assert.That(hintedA, Is.EqualTo(written), "a later hint never shadows a write");
            Assert.That(hintedB, Is.Null, "a null hint is served as an absent account");
            Assert.That(reader.AccountReadCount, Is.EqualTo(1), "hints are dropped with the write buffer");
        }
    }

    [Test]
    public void Failed_partition_fold_keeps_mutations_pending_without_completing_snapshot([Values(0x00, 0x01, 0xFF)] int failedZone)
    {
        using PbtSnapshotBundle bundle = CreateBundle(new Reader(default, null));
        byte[] bytes = new byte[(256 + 2) * 31];
        bytes.AsSpan().Fill(0x5b);
        Account account = Build.An.Account.WithBalance(3).WithCode(bytes).TestObject;
        SetAccountWithCode(bundle, TestItem.AddressA, account, new CodeInfo(bytes), codeFirst: true);
        bundle.SetSlot(TestItem.AddressA, 1000, EvmWordSlot.FromStripped(Bytes.FromHexString("01")));
        // Slots of other accounts store a group below the storage zone boundary, so the failing zone publishes one.
        bundle.SetSlot(TestItem.AddressA, 2000, EvmWordSlot.FromStripped(Bytes.FromHexString("01")));
        bundle.SetSlot(TestItem.AddressC, 1000, EvmWordSlot.FromStripped(Bytes.FromHexString("01")));
        bundle.SetSlot(TestItem.AddressD, 1000, EvmWordSlot.FromStripped(Bytes.FromHexString("01")));
        ValueHash256 root = bundle.Fold(default);
        using PbtSnapshot original = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), root);

        Account replacement = account.WithChangedBalance(4);
        bundle.SetAccount(TestItem.AddressA, replacement);
        bundle.SetSlot(TestItem.AddressA, 1000, EvmWordSlot.FromStripped(Bytes.FromHexString("02")));
        // A balance change stages no code chunk, so a new contract keeps the code partition in the batch.
        byte[] otherBytes = Bytes.FromHexString("6002600055");
        Account other = Build.An.Account.WithCode(otherBytes).TestObject;
        SetAccountWithCode(bundle, TestItem.AddressB, other, new CodeInfo(otherBytes), codeFirst: true);
        CountingStore store = new(bundle) { FailedZone = failedZone };
        Assert.Throws<InvalidDataException>(() => TrieUpdater.UpdateRoot(store, root, bundle.PrepareLeafChanges(), PbtTreeHarness.FoldQuota(), PbtTreeHarness.DefaultFanOut, null));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.TreeRoot, Is.EqualTo(root));
            Assert.That(bundle.GetAccount(TestItem.AddressA), Is.EqualTo(replacement));
            Assert.Throws<InvalidOperationException>(() => bundle.CollectSnapshot(new StateId(1, default), new StateId(2, default), root));
            Assert.That(original.TreeRoot, Is.EqualTo(root));
            Assert.That(original.Content.Accounts[PbtStateKey.AddressKeyHash(TestItem.AddressA)]?.ToAccount(), Is.EqualTo(account));
        }
    }

    private static void SetAccountWithCode(PbtSnapshotBundle bundle, Address address, Account? account, CodeInfo? code, bool codeFirst)
    {
        bool hasCode = code is not null && code.Code.Length > 0;
        if (codeFirst && hasCode) bundle.SetCode(account!.CodeHash.ValueHash256, code!);
        bundle.SetAccount(address, account);
        if (!codeFirst && hasCode) bundle.SetCode(account!.CodeHash.ValueHash256, code!);
    }

    private static PbtSnapshotContent Content<TPath>(TPath groupKey, byte[]? encoding)
        where TPath : struct, IPbtNodePath<TPath>
    {
        PbtSnapshotContent content = new();
        using RefCountingMemory? payload = encoding is null ? null : Memory(encoding);
        content.SetNodeGroup(groupKey, payload);
        return content;
    }

    private static RefCountingMemory Memory(byte[] encoding, IRefCountingMemoryProvider? memoryProvider = null)
    {
        RefCountingMemory payload = (memoryProvider ?? PooledRefCountingMemoryProvider.Instance).Rent(encoding.Length);
        encoding.CopyTo(payload.GetSpan());
        return payload;
    }

    private static byte[] BranchEncoding(byte marker)
    {
        ValueHash256 left = new(Value(marker));
        ValueHash256 right = new(Value((byte)(marker + 32)));
        return PbtTreeHarness.EncodeBranch([], 0, left, right);
    }

    private sealed class Reader(PbtVariableTreeKey key, ValueHash256? value) : IPbtPersistence.IReader
    {
        public IEnumerable<KeyValuePair<ValueHash256, Account>> Accounts { get; init; } = [];
        public PbtNodePath GroupKey { get; set; } = new([], 0);
        public byte[]? GroupPayload { get; set; }
        public IRefCountingMemoryProvider MemoryProvider { get; set; } = PooledRefCountingMemoryProvider.Instance;
        public Exception? GroupReadException { get; set; }
        public int GroupReadCount { get; private set; }
        public Dictionary<ValueHash256, CodeInfo> Codes { get; } = [];
        public int CodeReadCount { get; private set; }
        public int AccountReadCount { get; private set; }
        public StateId CurrentState => StateId.PreGenesis;
        public ValueHash256 CurrentRoot { get; set; }
        public PbtAccount? GetAccount(in ValueHash256 addressHash)
        {
            AccountReadCount++;
            foreach ((ValueHash256 hash, Account account) in Accounts)
                if (hash == addressHash) return ToPbtAccount(account);
            return null;
        }
        private PbtAccount ToPbtAccount(Account account) => PbtAccount.From(account, account.HasCode ? Codes[account.CodeHash.ValueHash256] : null);
        public PackedSlotRun GetSlotRun<TKey>(in TKey runKey) where TKey : struct, IPbtKey<TKey>
        {
            PackedSlotRun run = SlotRun.Empty;
            if (value is { } word && SlotRun.RunKey(key).Bytes.SequenceEqual(runKey.Bytes))
            {
                PackedSlotRun previous = run;
                run = run.With(SlotRun.IndexOf(key), EvmWordSlot.FromStripped(word.Bytes));
                SlotRun.Return(previous);
            }
            return run;
        }
        public CodeInfo? GetCode(in ValueHash256 codeHash)
        {
            CodeReadCount++;
            return Codes.GetValueOrDefault(codeHash);
        }
        public bool TryGetCodeLeaf(in PbtPath key, out ValueHash256 value)
        {
            value = default;
            return false;
        }
        public IEnumerator<KeyValuePair<ValueHash256, PbtAccount>> EnumerateAccounts() => throw new NotSupportedException();
        public RefCountingMemory? GetNodeGroup<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath>
        {
            GroupReadCount++;
            if (GroupReadException is not null) throw GroupReadException;
            if (!PbtNodePathOperations.Equal(groupKey, GroupKey) || GroupPayload is null) return null;
            RefCountingMemory memory = MemoryProvider.Rent(GroupPayload.Length);
            GroupPayload.CopyTo(memory.GetSpan());
            return memory;
        }
        public void Dispose() { }
    }

    private sealed class CountingStore(PbtSnapshotBundle bundle) : IPbtStore, IPbtNodeGroupSink
    {
        public IPbtConcurrentWriter CreateWriter() => new PbtPassThroughWriter(this);

        public int ApplyCount { get; private set; }
        public int? FailedZone { get; init; }
        public RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash) => bundle.GetNodeGroup(groupKey.ToPath<PbtStorageNodePath>(), groupHash);
        public void SetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash, RefCountingMemory? payload)
        {
            if (groupKey.BitDepth == 8 && groupKey.ToPath<PbtStorageNodePath>().GetByte(0) == FailedZone)
                throw new InvalidDataException("Configured partition write failure.");
            ApplyCount++;
            bundle.SetNodeGroup(groupKey.ToPath<PbtStorageNodePath>(), groupHash, payload);
        }
    }
}
