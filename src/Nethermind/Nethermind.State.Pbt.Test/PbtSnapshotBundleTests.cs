// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.State.Pbt.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtSnapshotBundleTests
{
    [Test]
    public void SnapshotContent_DisjointGroupReplacementAndResetPreserveReadLeases([Values(1, 16)] int groupCount, [Values] bool tombstone)
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
                System.Threading.Tasks.Parallel.For(0, groupCount, index =>
                {
                    PbtNodePath groupPath = new([(byte)(index << 4)], 4);
                    PbtStorageNodePath storagePath = groupPath.ToPath<PbtStorageNodePath>();
                    Assert.That(content.TryGetNodeGroup(groupPath, out RefCountingMemory? missing), Is.False);
                    using (missing) Assert.That(missing, Is.Null);

                    byte[] original = EncodeGroup(groupPath, [new PbtNodeRecord(PbtFourLevelGroupGeometry.PathOf(groupPath, 0).ToPath<PbtStorageNodePath>(), BranchEncoding((byte)(round + 1)))]);
                    originalEncodings[index] = original;
                    using (RefCountingMemory payload = Memory(original, memoryProvider)) content.SetNodeGroup(groupPath, payload);
                    Assert.That(content.TryGetNodeGroup(groupPath, out retained[index]), Is.True);
                    Assert.That(retained[index], Is.Not.Null);

                    byte[] replacement = EncodeGroup(groupPath, [new PbtNodeRecord(PbtFourLevelGroupGeometry.PathOf(groupPath, 0).ToPath<PbtStorageNodePath>(), BranchEncoding((byte)(round + 3)))]);
                    using (RefCountingMemory? payload = tombstone ? null : Memory(replacement, memoryProvider))
                        content.SetNodeGroup(groupPath, payload);
                    bool found = content.TryGetNodeGroup(groupPath, out RefCountingMemory? current);
                    using (current)
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(found, Is.True);
                        Assert.That(current?.GetSpan().ToArray(), Is.EqualTo(tombstone ? null : replacement));
                        Assert.That(retained[index]!.GetSpan().ToArray(), Is.EqualTo(original));
                    }
                    expectedNodeBytes[index] = storagePath.EncodedLength + (tombstone ? 0 : replacement.Length);
                });

                long nodeBytes = 0;
                foreach (long size in expectedNodeBytes) nodeBytes += size;
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(content.NodeGroups, Has.Count.EqualTo(groupCount));
                    Assert.That(content.GetPayloadSize(), Is.EqualTo(new PbtSnapshotPayloadSize(0, nodeBytes, 0)));
                    Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.EqualTo(groupCount * (tombstone ? 1 : 2)));
                }

                content.Reset();
                content.Reset();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(content.NodeGroups, Is.Empty);
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

    [TestCase(null)]
    [TestCase(0)]
    [TestCase(1)]
    public void PrewarmHints_AgreeAcrossOverloadsAndResetBetweenSnapshots(int? slotIndex)
    {
        using TrackingTransientPool pool = new();
        using PbtSnapshotBundle bundle = CreatePrewarmBundle(pool);
        UInt256? slot = slotIndex is null ? null : (UInt256)(uint)slotIndex.Value;
        ValueAddress address = new(TestItem.AddressA.Bytes);
        Assert.That(bundle.ShouldQueuePrewarm(TestItem.AddressA, slot), Is.True);
        Assert.That(bundle.ShouldQueuePrewarm(address, slot), Is.False);
        using PbtSnapshot snapshot = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), default);
        Assert.That(bundle.ShouldQueuePrewarm(address, slot), Is.True);
        Assert.That(bundle.ShouldQueuePrewarm(TestItem.AddressA, slot), Is.False);
        bundle.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.ShouldQueuePrewarm(address, slot), Is.False);
            Assert.That(bundle.ShouldQueuePrewarm(TestItem.AddressA, slot), Is.False);
            Assert.That(pool.ReturnCount, Is.EqualTo(2));
            Assert.That(snapshot.Content.Accounts, Is.Empty);
            Assert.That(snapshot.Content.Storages, Is.Empty);
            Assert.That(snapshot.Content.Codes, Is.Empty);
            Assert.That(snapshot.Content.NodeGroups, Is.Empty);
        }
        using PbtSnapshotBundle nextBundle = CreatePrewarmBundle(pool);
        Assert.That(nextBundle.ShouldQueuePrewarm(address, slot), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RetiredPrewarmResource_IsNotRecycledUntilItsLastReaderReleases(bool dispose)
    {
        using TrackingTransientPool pool = new();
        using PbtSnapshotBundle bundle = CreatePrewarmBundle(pool);
        PbtTransientResource retired = pool.LastRented!;
        Assert.That(bundle.ShouldQueuePrewarm(TestItem.AddressA), Is.True);
        Assert.That(retired.TryAcquireLease(), Is.True);
        try
        {
            if (dispose) bundle.Dispose();
            else
            {
                using PbtSnapshot snapshot = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), default);
                Assert.That(bundle.ShouldQueuePrewarm(TestItem.AddressA), Is.True);
            }
            Assert.That(pool.ReturnCount, Is.Zero);
            Assert.That(retired.ShouldPrewarm(TestItem.AddressA), Is.False);
            using PbtSnapshotBundle concurrentBundle = CreatePrewarmBundle(pool);
            Assert.That(pool.LastRented, Is.Not.SameAs(retired));
            Assert.That(concurrentBundle.ShouldQueuePrewarm(TestItem.AddressA), Is.True);
            Assert.That(retired.ShouldPrewarm(TestItem.AddressB), Is.True);
            Assert.That(concurrentBundle.ShouldQueuePrewarm(TestItem.AddressB), Is.True);
        }
        finally
        {
            retired.ReleaseLease();
        }
        Assert.That(pool.ReturnCount, Is.EqualTo(2));
        using PbtSnapshotBundle nextBundle = CreatePrewarmBundle(pool);
        Assert.That(pool.LastRented, Is.SameAs(retired));
        Assert.That(nextBundle.ShouldQueuePrewarm(TestItem.AddressA), Is.True);
    }

    [Test]
    public void Dispose_ReturnsTransientOnceEvenWhenOtherCleanupThrows()
    {
        using TrackingTransientPool pool = new() { ThrowOnBuilderReturn = true };
        PbtSnapshotBundle bundle = CreatePrewarmBundle(pool);
        Assert.Throws<IOException>(bundle.Dispose);
        Assert.DoesNotThrow(bundle.Dispose);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.ReturnCount, Is.EqualTo(1));
            Assert.That(bundle.ShouldQueuePrewarm(TestItem.AddressA), Is.False);
        }
    }

    private static PbtSnapshotBundle CreatePrewarmBundle(IPbtResourcePool pool) => new(
        new PbtSnapshotPooledList(0),
        new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(new PbtStorageFullKey([0]), null)),
        pool, PbtResourcePool.Usage.MainBlockProcessing);

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
        public PbtWriteBatchBuilder<PbtFullKey> GetWriteBatch(PbtResourcePool.Usage usage) => new(2);
        public void ReturnWriteBatch(PbtResourcePool.Usage usage, PbtWriteBatchBuilder<PbtFullKey> builder)
        {
            builder.Dispose();
            if (ThrowOnBuilderReturn) throw new IOException("Builder return failed");
        }

        public PbtWriteBatchBuilder<PbtStorageFullKey> GetStorageWriteBatch(PbtResourcePool.Usage usage) => new(2);
        public void ReturnStorageWriteBatch(PbtResourcePool.Usage usage, PbtWriteBatchBuilder<PbtStorageFullKey> builder)
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
    public void LocalCanonicalWrites_OverrideSharedAndPersistedLeaves()
    {
        PbtStorageFullKey key = PbtStateKey.Storage(TestItem.AddressA, 1);
        ValueHash256 persisted = new(Value(1));
        ValueHash256 shared = new(Value(2));
        ValueHash256 local = new(Value(3));
        PbtResourcePool pool = new(new PbtConfig());
        PbtSnapshotContent sharedContent = new();
        sharedContent.Storages[key] = EvmWordSlot.FromStripped(shared.Bytes);
        PbtSnapshotPooledList sharedSnapshots = new(1)
        {
            new PbtSnapshot(StateId.PreGenesis, new StateId(1, default), default, sharedContent, pool, PbtResourcePool.Usage.MainBlockProcessing)
        };
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), new PbtReadOnlySnapshotBundle(sharedSnapshots, new Reader(key, persisted)), pool, PbtResourcePool.Usage.MainBlockProcessing);
        Assert.That(bundle.GetSlot(TestItem.AddressA, 1), Is.EqualTo(EvmWordSlot.FromStripped(shared.Bytes)));
        bundle.SetSlot(TestItem.AddressA, 1, EvmWordSlot.FromStripped(local.Bytes));
        Assert.That(bundle.GetSlot(TestItem.AddressA, 1), Is.EqualTo(EvmWordSlot.FromStripped(local.Bytes)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Leaf_enumeration_preserves_optional_value_key_prefix(bool filtered)
    {
        PbtStorageFullKey matching = PbtStateKey.Storage(TestItem.AddressA, 1000);
        PbtStorageFullKey other = PbtStateKey.Storage(TestItem.AddressB, 1000);
        PbtStorageFullKey prefix = PbtStateKey.StoragePrefix(TestItem.AddressA);
        PbtResourcePool pool = new(new PbtConfig());
        PbtSnapshotContent sharedContent = new();
        sharedContent.Storages[matching] = EvmWordSlot.FromStripped(Value(1));
        sharedContent.Storages[other] = EvmWordSlot.FromStripped(Value(2));
        PbtSnapshotPooledList sharedSnapshots = new(1)
        {
            new PbtSnapshot(StateId.PreGenesis, new StateId(1, default), default, sharedContent, pool, PbtResourcePool.Usage.MainBlockProcessing)
        };
        PbtReadOnlySnapshotBundle readOnly = new(sharedSnapshots, new Reader(matching, null));
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), readOnly, pool, PbtResourcePool.Usage.MainBlockProcessing);
        List<PbtStorageFullKey> expected = filtered ? [matching] : [matching, other];
        expected.Sort();
        List<PbtStorageFullKey> sharedKeys = [];
        foreach (KeyValuePair<PbtStorageFullKey, ValueHash256> leaf in filtered ? readOnly.EnumerateLeaves(prefix) : readOnly.EnumerateLeaves())
            sharedKeys.Add(leaf.Key);
        bundle.SetSlot(TestItem.AddressA, 1000, EvmWordSlot.FromStripped(Value(3)));
        List<PbtStorageFullKey> visibleKeys = [];
        foreach (KeyValuePair<PbtStorageFullKey, ValueHash256> leaf in filtered ? bundle.EnumerateLeaves(prefix) : bundle.EnumerateLeaves())
            visibleKeys.Add(leaf.Key);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sharedKeys, Is.EqualTo(expected));
            Assert.That(visibleKeys, Is.EqualTo(expected));
        }
    }

    [TestCase(0u)]
    [TestCase(63u)]
    [TestCase(64u)]
    [TestCase(256u)]
    [TestCase(uint.MaxValue)]
    public void Storage_mutations_use_small_header_and_wide_storage_partitions(uint slotValue)
    {
        UInt256 slot = slotValue == uint.MaxValue ? UInt256.MaxValue : new UInt256(slotValue);
        PbtStorageFullKey key = PbtStateKey.Storage(TestItem.AddressA, slot);
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0),
            new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(key, null)), pool, PbtResourcePool.Usage.MainBlockProcessing);
        EvmWord value = EvmWordSlot.FromStripped(Value(9));
        foreach (bool delete in new[] { false, true })
        {
            bundle.SetSlot(TestItem.AddressA, slot, delete ? default : value);
            using PbtPartitionBatches changes = bundle.PrepareLeafChanges();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(bundle.GetSlot(TestItem.AddressA, slot), Is.EqualTo(delete ? default : value));
                Assert.That(changes.Account?.Count ?? 0, Is.EqualTo(slot < 64 ? 1 : 0));
                Assert.That(changes.Storage?.Count ?? 0, Is.EqualTo(slot < 64 ? 0 : 1));
                Assert.That(changes.Code, Is.Null);
                Assert.That(bundle.EnumeratePendingLeafMutationsForTest(), Is.EquivalentTo(new[]
                {
                    new KeyValuePair<PbtStorageFullKey, ValueHash256?>(key, delete ? (ValueHash256?)null : new ValueHash256(Value(9)))
                }));
            }
            bundle.CompleteLeafChanges();
        }
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void Trie_updates_leave_independently_staged_flat_entries_unchanged(bool leafExists, bool delete)
    {
        PbtStorageFullKey key = PbtStateKey.Storage(TestItem.AddressA, 1);
        ValueHash256 flatValue = new(Value(9));
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0),
            new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(key, null)), pool, PbtResourcePool.Usage.MainBlockProcessing);
        PbtSnapshotStore store = new(bundle);
        using PbtWriteBatchBuilder<PbtStorageFullKey> initial = new(0);
        if (leafExists) initial.Set(key, new ValueHash256(Value(1)));
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, initial.Build());
        bundle.SetSlot(TestItem.AddressA, 1, EvmWordSlot.FromStripped(flatValue.Bytes));

        using PbtWriteBatchBuilder<PbtStorageFullKey> changes = new(0);
        if (delete) changes.Delete(key);
        else changes.Set(key, new ValueHash256(Value(2)));
        ValueHash256 updatedRoot = TrieUpdater.UpdateRoot(store, root, changes.Build());

        byte[] expectedLeaf = PbtNodeCodec.EncodeLeaf(key, Value(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.GetSlot(TestItem.AddressA, 1), Is.EqualTo(EvmWordSlot.FromStripped(flatValue.Bytes)));
            Assert.That(bundle.EnumeratePendingLeafMutationsForTest(), Is.EquivalentTo(new[] { new KeyValuePair<PbtStorageFullKey, ValueHash256?>(key, flatValue) }));
            Assert.That(updatedRoot, Is.EqualTo(delete ? default : PbtNodeCodec.Hash(new PbtNodeReader(expectedLeaf))));
            Assert.That(store.GetNode(new PbtNodePath([], 0)), Is.EqualTo(delete ? null : expectedLeaf));
        }
    }

    [TestCase(7u, false)]
    [TestCase(1000u, false)]
    [TestCase(7u, true)]
    [TestCase(1000u, true)]
    public void Account_and_slot_setters_stage_canonical_writes_and_deletions(uint slot, bool deleteAccount)
    {
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0),
            new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(new PbtStorageFullKey([0]), null)), pool, PbtResourcePool.Usage.MainBlockProcessing);
        byte[] code = Bytes.FromHexString("6001");
        Account account = Build.An.Account.WithCode(code).TestObject;
        bundle.SetAccount(TestItem.AddressA, account);
        bundle.SetAccount(TestItem.AddressA, account);
        bundle.SetCode(account.CodeHash.ValueHash256, new CodeInfo(code));
        EvmWord value = EvmWordSlot.FromStripped(Bytes.FromHexString("ab"));
        bundle.SetSlot(TestItem.AddressA, slot, value);
        ValueHash256 root = Fold(bundle, default);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.GetCodeReference(account.CodeHash.ValueHash256), Is.EqualTo(1));
            Assert.That(bundle.GetSlot(TestItem.AddressA, slot), Is.EqualTo(value));
            Assert.That(bundle.GetCode(account.CodeHash.ValueHash256)!.Code.ToArray(), Is.EqualTo(code));
        }
        if (deleteAccount) bundle.SetAccount(TestItem.AddressA, null);
        else bundle.SetSlot(TestItem.AddressA, slot, default);
        root = Fold(bundle, root);
        using PbtSnapshot snapshot = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), root);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.GetAccount(TestItem.AddressA), Is.EqualTo(deleteAccount ? null : account));
            Assert.That(bundle.GetSlot(TestItem.AddressA, slot), Is.EqualTo(default(EvmWord)));
            Assert.That(bundle.EnumerateLeaves(PbtStateKey.Storage(TestItem.AddressA, slot)), Is.Empty);
            Assert.That(bundle.GetCodeReference(account.CodeHash.ValueHash256), Is.EqualTo(deleteAccount ? 0 : 1));
        }
    }

    [NonParallelizable]
    [TestCase(0UL, 2)]
    [TestCase(1UL, 2)]
    [TestCase(1048576UL, 1)]
    public void Trie_cache_reuses_only_matching_immutable_views(ulong budget, int expectedReads)
    {
        long initialHits = Metrics.PbtTrieCacheHits["account"];
        long initialMisses = Metrics.PbtTrieCacheMisses["account"];
        TrackingMemoryProvider memory = new();
        PbtNodePath path = new([], 0);
        byte[] encoding = EncodeGroup(path, [new PbtNodeRecord(path.ToPath<PbtStorageNodePath>(), BranchEncoding(1))]);
        using PbtTrieNodeCache cache = new(new PbtConfig { AccountTrieNodeCacheSizeBudget = budget });
        Reader reader = new(default, null) { GroupPayload = encoding, MemoryProvider = memory };
        using PbtReadOnlySnapshotBundle bundle = new(new(0), reader, trieNodeCache: cache);
        for (int read = 0; read < 2; read++)
        {
            using RefCountingMemory? payload = bundle.GetNodeGroup(path);
            Assert.That(payload!.GetSpan().ToArray(), Is.EqualTo(encoding));
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.GroupReadCount, Is.EqualTo(expectedReads));
            Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero, "cache must not retain oversized source allocations");
            Assert.That(cache.MemorySize, Is.LessThanOrEqualTo(budget));
        }
        Reader forkReader = new(default, null) { GroupPayload = encoding, CurrentRoot = new ValueHash256(Value(2)) };
        using PbtReadOnlySnapshotBundle fork = new(new(0), forkReader, trieNodeCache: cache);
        using RefCountingMemory? forkPayload = fork.GetNodeGroup(path);
        Assert.That(forkReader.GroupReadCount, Is.EqualTo(1));
        Assert.That(cache.TryGet(default, new PbtNodePath([0], 4), out _), Is.False);
        Assert.That(cache.TryGet(default, new PbtStorageNodePath([], 0), out _), Is.False);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.PbtTrieCacheHits["account"] - initialHits, Is.EqualTo(2 - expectedReads));
            Assert.That(Metrics.PbtTrieCacheMisses["account"] - initialMisses, Is.EqualTo(expectedReads + 3));
        }
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
                Assert.That(inserted.Equals(requested), Is.True);
                Assert.That(requested.Equals(inserted), Is.True);
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
        long[] initialHits = new long[3];
        long[] initialMisses = new long[3];
        for (int index = 0; index < CachePartitions.Length; index++)
        {
            string label = CachePartitions[index];
            initialMemory[index] = Metrics.PbtTrieCacheMemory[label];
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
            AssertMetrics(retainedSize, 1, 2);
            cache.Clear();
            AssertMetrics(0, 1, 2);
            cache.Add(default, path, source);
            AssertMetrics(retainedSize, 1, 2);
            cache.Dispose();
            cache.Add(default, path, source);
            Assert.That(cache.TryGet(default, path, out _), Is.False);
            AssertMetrics(0, 1, 3);
            Assert.That(retained!.GetSpan().ToArray(), Is.EqualTo(source.GetSpan().ToArray()));
        }

        void AssertMetrics(long memory, long hits, long misses)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cache.MemorySize, Is.EqualTo(memory));
                for (int index = 0; index < CachePartitions.Length; index++)
                {
                    string label = CachePartitions[index];
                    Assert.That(Metrics.PbtTrieCacheMemory[label] - initialMemory[index], Is.EqualTo(label == partition ? memory : 0), label);
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

    [Test]
    public void Trie_cache_uses_all_shards_below_each_zone([ValueSource(nameof(CachePartitions))] string partition)
    {
        using PbtTrieNodeCache cache = new(CacheConfig(partition, 1048576));
        using RefCountingMemory source = Memory(new byte[1600]);
        byte[] bytes = new byte[2];
        bytes[0] = CachePath(partition).GetByte(0);
        for (int shard = 0; shard < 256; shard++)
        {
            bytes[1] = (byte)shard;
            cache.Add(default, new PbtNodePath(bytes, 16), source);
        }
        for (int shard = 0; shard < 256; shard++)
        {
            bytes[1] = (byte)shard;
            Assert.That(cache.TryGet(default, new PbtNodePath(bytes, 16), out RefCountingMemory? payload), Is.True, $"shard {shard}");
            ((IDisposable)payload!).Dispose();
        }
        Assert.That(cache.MemorySize, Is.LessThanOrEqualTo(1048576));
    }

    [Test, NonParallelizable]
    public void Trie_cache_partition_eviction_preserves_other_partitions([ValueSource(nameof(CachePartitions))] string partition)
    {
        long[] initialMemory = new long[CachePartitions.Length];
        for (int index = 0; index < CachePartitions.Length; index++)
            initialMemory[index] = Metrics.PbtTrieCacheMemory[CachePartitions[index]];
        using PbtTrieNodeCache cache = new(CacheConfig(partition, 1048576));
        using RefCountingMemory source = Memory(Bytes.FromHexString("010203"));
        foreach (string label in CachePartitions) cache.Add(default, CachePath(label), source);
        long entrySize = cache.MemorySize / CachePartitions.Length;
        PbtNodePath path = CachePath(partition);
        ValueHash256 replacementRoot = new(Value(1));
        cache.Add(replacementRoot, path, source);
        Assert.That(cache.TryGet(default, path, out _), Is.False);
        Assert.That(cache.TryGet(replacementRoot, path, out RefCountingMemory? retained), Is.True);
        using (retained)
        {
            using RefCountingMemory larger = Memory(new byte[1600]);
            cache.Add(default, path.AppendNib(0), larger);
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
                    Assert.That(memory, Is.EqualTo(entrySize + (label == partition ? 1600 - 3 : 0)), label);
                    totalMemory += memory;
                }
                Assert.That(cache.MemorySize, Is.EqualTo(totalMemory));
            }
            cache.Dispose();
            for (int index = 0; index < CachePartitions.Length; index++)
                Assert.That(Metrics.PbtTrieCacheMemory[CachePartitions[index]], Is.EqualTo(initialMemory[index]));
        }
    }

    [Test]
    public void Trie_cache_replacement_eviction_and_disposal_preserve_caller_leases()
    {
        using PbtTrieNodeCache cache = new(new PbtConfig { AccountTrieNodeCacheSizeBudget = 1048576 });
        PbtNodePath path = new([], 0);
        using RefCountingMemory source = Memory(Bytes.FromHexString("010203"));
        cache.Add(default, path, source);
        Assert.That(cache.TryGet(default, path, out RefCountingMemory? retained), Is.True);
        using (retained)
        {
            cache.Add(new ValueHash256(Value(1)), path, source);
            Assert.That(cache.TryGet(default, path, out _), Is.False);
            Assert.That(cache.TryGet(new ValueHash256(Value(1)), path, out RefCountingMemory? replacement), Is.True);
            using RefCountingMemory? replacementLease = replacement;
            using RefCountingMemory larger = Memory(new byte[1600]);
            cache.Add(new ValueHash256(Value(2)), new PbtNodePath([0], 4), larger);
            Assert.That(cache.TryGet(new ValueHash256(Value(1)), path, out _), Is.False, "a full shard evicts old entries");
            cache.Clear();
            cache.Dispose();
            cache.Add(default, path, source);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cache.MemorySize, Is.Zero);
                Assert.That(retained!.GetSpan().ToArray(), Is.EqualTo(Bytes.FromHexString("010203")));
                Assert.That(cache.TryGet(default, path, out _), Is.False);
            }
        }
        Assert.Throws<InvalidOperationException>(() => retained!.AcquireLease());
    }

    [Test]
    public void Trie_cache_concurrent_hits_and_eviction_keep_payloads_alive()
    {
        using PbtTrieNodeCache cache = new(CacheConfig("account", 1048576));
        using RefCountingMemory source = Memory(Bytes.FromHexString("010203"));
        System.Threading.Tasks.Parallel.For(0, 1000, iteration =>
        {
            PbtNodePath path = CachePath(CachePartitions[iteration % CachePartitions.Length]);
            cache.Add(default, path, source);
            if (cache.TryGet(default, path, out RefCountingMemory? payload))
                using (payload) Assert.That(payload.GetSpan().ToArray(), Is.EqualTo(Bytes.FromHexString("010203")));
            if ((iteration & 3) == 0) cache.Clear();
        });
        cache.Clear();
        Assert.That(cache.MemorySize, Is.Zero);
    }

    [Test]
    public void Node_group_read_rejects_non_boundary_key_before_empty_persistence_lookup()
    {
        Reader reader = new(new PbtStorageFullKey([0]), null);
        using PbtReadOnlySnapshotBundle bundle = new(new PbtSnapshotPooledList(0), reader);

        Assert.Throws<ArgumentException>(() => bundle.GetNodeGroup(new PbtNodePath([0], 1)));
        Assert.That(reader.GroupReadCount, Is.Zero);
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
        byte[] persisted = EncodeGroup(groupKey, [new PbtNodeRecord(groupKey.ToPath<PbtStorageNodePath>(), BranchEncoding(1)),
            new PbtNodeRecord(PbtFourLevelGroupGeometry.PathOf(groupKey, 14).ToPath<PbtStorageNodePath>(), BranchEncoding(2))]);
        byte[] shared = EncodeGroup(groupKey, [new PbtNodeRecord(groupKey.ToPath<PbtStorageNodePath>(), BranchEncoding(3))]);
        byte[] local = EncodeGroup(groupKey, [new PbtNodeRecord(groupKey.ToPath<PbtStorageNodePath>(), BranchEncoding(4))]);
        byte[] write = EncodeGroup(groupKey, [new PbtNodeRecord(groupKey.ToPath<PbtStorageNodePath>(), BranchEncoding(5))]);
        Reader reader = new(new PbtStorageFullKey([0]), null) { GroupPayload = persisted };
        PbtResourcePool pool = new(new PbtConfig());
        PbtSnapshotPooledList sharedSnapshots = newestTier >= 1
            ? Snapshots(pool, Content(groupKey, persisted), Content(wideGroupKey, newestTier == 1 && tombstone ? null : shared))
            : new(0);
        PbtSnapshotPooledList localSnapshots = newestTier >= 2
            ? Snapshots(pool, Content(groupKey, shared), Content(wideGroupKey, newestTier == 2 && tombstone ? null : local))
            : new(0);
        using PbtTrieNodeCache cache = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(localSnapshots, new PbtReadOnlySnapshotBundle(sharedSnapshots, reader, trieNodeCache: cache), pool, PbtResourcePool.Usage.MainBlockProcessing);
        if (newestTier == 3)
        {
            using RefCountingMemory? payload = tombstone ? null : Memory(write);
            bundle.SetNodeGroup(wideGroupKey, payload);
        }

        using RefCountingMemory? actual = bundle.GetNodeGroup(wideGroupKey);
        byte[]? expected = tombstone ? null : newestTier switch { 0 => persisted, 1 => shared, 2 => local, _ => write };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual?.Memory.ToArray(), Is.EqualTo(expected));
            Assert.That(reader.GroupReadCount, Is.EqualTo(newestTier == 0 ? 1 : 0));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Malformed_persisted_group_is_rejected_by_updater_and_lease_is_released_without_mutating_snapshot(bool invalidFooter)
    {
        TrackingMemoryProvider memoryProvider = new();
        byte[] malformed = invalidFooter ? new byte[PbtNodeGroupCodec.MaxTrailerLength] : Bytes.FromHexString("01");
        if (invalidFooter) malformed[^1] = 0x80;
        Reader reader = new(new PbtStorageFullKey([0]), null)
        {
            GroupPayload = malformed,
            MemoryProvider = memoryProvider,
        };
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), reader), pool, PbtResourcePool.Usage.MainBlockProcessing);
        PbtStorageFullKey originalLeafKey = PbtStateKey.Storage(TestItem.AddressA, 1);
        PbtNodePath originalGroupKey = new([0x80], 4);
        byte[] originalGroup = EncodeGroup(originalGroupKey, [new PbtNodeRecord(PbtFourLevelGroupGeometry.PathOf(originalGroupKey, 0).ToPath<PbtStorageNodePath>(), BranchEncoding(1))]);
        using RefCountingMemory originalPayload = Memory(originalGroup);
        bundle.SetSlot(TestItem.AddressA, 1, EvmWordSlot.FromStripped(Value(2)));
        bundle.SetNodeGroup(originalGroupKey, originalPayload);
        using PbtWriteBatchBuilder<PbtStorageFullKey> changes = new(0);
        changes.Set(new PbtStorageFullKey([3]), new ValueHash256(Value(4)));

        Assert.Throws<InvalidDataException>(() => TrieUpdater.UpdateRoot(new PbtSnapshotStore(bundle), new ValueHash256(Value(5)), changes.Build()));
        AssertSnapshotUnchanged(bundle, originalLeafKey, originalGroupKey, originalGroup);
        Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
    }

    [Test]
    public void Malformed_group_replacement_is_rejected_without_mutating_snapshot()
    {
        PbtResourcePool pool = new(new PbtConfig());
        Reader reader = new(new PbtStorageFullKey([0]), null);
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), reader), pool, PbtResourcePool.Usage.MainBlockProcessing);
        PbtStorageFullKey leafKey = PbtStateKey.Storage(TestItem.AddressA, 1);
        PbtNodePath groupKey = new([], 0);
        byte[] original = EncodeGroup(groupKey, [new PbtNodeRecord(groupKey.ToPath<PbtStorageNodePath>(), BranchEncoding(1))]);
        using RefCountingMemory originalPayload = Memory(original);
        using RefCountingMemory malformed = Memory(Bytes.FromHexString("7f"));
        bundle.SetSlot(TestItem.AddressA, 1, EvmWordSlot.FromStripped(Value(2)));
        bundle.SetNodeGroup(groupKey, originalPayload);

        Assert.Throws<InvalidDataException>(() => bundle.SetNodeGroup(groupKey, malformed));
        AssertSnapshotUnchanged(bundle, leafKey, groupKey, original);
        Assert.That(reader.GroupReadCount, Is.Zero);
    }

    [Test]
    public void Updater_group_read_failure_preserves_prior_deltas_and_does_not_apply()
    {
        PbtResourcePool pool = new(new PbtConfig());
        PbtStorageFullKey originalLeafKey = PbtStateKey.Storage(TestItem.AddressA, 1);
        ValueHash256 originalLeafValue = new(Value(2));
        PbtNodePath originalNodePath = new([0x80], 4);
        byte[] originalNode = EncodeGroup(originalNodePath, [new PbtNodeRecord(PbtFourLevelGroupGeometry.PathOf(originalNodePath, 0).ToPath<PbtStorageNodePath>(), BranchEncoding(1))]);
        Reader reader = new(new PbtStorageFullKey([0]), null) { GroupReadException = new InvalidDataException("Configured group read failure.") };
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), reader), pool, PbtResourcePool.Usage.MainBlockProcessing);
        bundle.SetSlot(TestItem.AddressA, 1, EvmWordSlot.FromStripped(originalLeafValue.Bytes));
        using RefCountingMemory originalPayload = Memory(originalNode);
        bundle.SetNodeGroup(originalNodePath, originalPayload);
        using PbtWriteBatchBuilder<PbtStorageFullKey> changes = new(0);
        changes.Set(new PbtStorageFullKey([3]), new ValueHash256(Value(4)));

        CountingStore store = new(bundle);
        Assert.Throws<InvalidDataException>(() => TrieUpdater.UpdateRoot(store, new ValueHash256(Value(5)), changes.Build()));

        bundle.CompleteLeafChanges();
        using PbtSnapshot snapshot = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), default);
        bool foundGroup = snapshot.Content.TryGetNodeGroup(originalNodePath, out RefCountingMemory? group);
        using RefCountingMemory? groupLease = group;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.GroupReadCount, Is.EqualTo(1));
            Assert.That(store.ApplyCount, Is.Zero);
            Assert.That(snapshot.Content.Storages, Has.Count.EqualTo(1));
            Assert.That(snapshot.Content.Storages.TryGetValue(originalLeafKey, out EvmWord leaf) && leaf.Equals(EvmWordSlot.FromStripped(originalLeafValue.Bytes)), Is.True);
            Assert.That(snapshot.Content.NodeGroups, Has.Count.EqualTo(1));
            Assert.That(foundGroup, Is.True);
            Assert.That(group!.GetSpan().ToArray(), Is.EqualTo(originalNode));
        }
    }

    [Test]
    public void CollectedSnapshot_ContainsCanonicalWritesAndRoot()
    {
        PbtStorageFullKey key = PbtStateKey.Storage(TestItem.AddressA, 1);
        ValueHash256 value = new(Value(2));
        ValueHash256 root = new(Value(3));
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(new PbtStorageFullKey([0]), null)), pool, PbtResourcePool.Usage.MainBlockProcessing);
        bundle.SetSlot(TestItem.AddressA, 1, EvmWordSlot.FromStripped(value.Bytes));
        Assert.Throws<InvalidOperationException>(() => bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), root));
        root = Fold(bundle, default);
        CodeInfo code = new(Bytes.FromHexString("6001"));
        bundle.SetCode(value, code);
        using PbtSnapshot snapshot = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), root);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.Content.Codes[value], Is.SameAs(code));
            Assert.That(snapshot.TreeRoot, Is.EqualTo(root));
            Assert.That(snapshot.Content.Storages.TryGetValue(key, out EvmWord actual) && actual.Equals(EvmWordSlot.FromStripped(value.Bytes)), Is.True);
        }
    }

    [TestCase(7u, false)]
    [TestCase(7u, true)]
    [TestCase(1000u, false)]
    [TestCase(1000u, true)]
    public void Storage_clear_masks_older_snapshots_but_preserves_subsequent_writes(uint slot, bool clearLast)
    {
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0),
            new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(default, null)), pool, PbtResourcePool.Usage.MainBlockProcessing);
        EvmWord original = EvmWordSlot.FromStripped(Bytes.FromHexString("01"));
        EvmWord replacement = EvmWordSlot.FromStripped(Bytes.FromHexString("02"));
        bundle.SetSlot(TestItem.AddressA, slot, original);
        bundle.SetSlot(TestItem.AddressB, slot, original);
        ValueHash256 root = Fold(bundle, default);
        using PbtSnapshot older = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), root);

        if (!clearLast) bundle.SelfDestruct(TestItem.AddressA);
        bundle.SetSlot(TestItem.AddressA, slot, replacement);
        if (clearLast) bundle.SelfDestruct(TestItem.AddressA);
        EvmWord expected = clearLast ? default : replacement;
        Assert.That(bundle.GetSlot(TestItem.AddressA, slot), Is.EqualTo(expected));
        root = Fold(bundle, root);
        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, slot, 1);
        if (!clearLast) PbtReferenceModel.SetSlot(model, TestItem.AddressA, slot, 2);
        Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)));
        Assert.That(Fold(bundle, root), Is.EqualTo(root), "repeated folds must not replay cleared writes");
        using PbtSnapshot newer = bundle.CollectSnapshot(new StateId(1, default), new StateId(2, default), root);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.GetSlot(TestItem.AddressA, slot), Is.EqualTo(expected));
            Assert.That(bundle.GetSlot(TestItem.AddressB, slot), Is.EqualTo(original));
            Assert.That(newer.Content.SelfDestructedStorageAddresses.ContainsKey(PbtKeyDerivation.AddressKeyHash(TestItem.AddressA)), Is.True);
            Assert.That(newer.Content.Storages.ContainsKey(PbtStateKey.Storage(TestItem.AddressA, slot)), Is.EqualTo(!clearLast));
            Assert.That(older.Content.Storages[PbtStateKey.Storage(TestItem.AddressA, slot)], Is.EqualTo(original));
        }
    }

    [Test]
    public void Code_bearing_account_matches_pinned_eip_root()
    {
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0),
            new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(default, null)), pool, PbtResourcePool.Usage.MainBlockProcessing);
        Address address = new("0x0000000000000000000000000000000000000001");
        byte[] bytes = Bytes.FromHexString("6001");
        Account account = Build.An.Account.WithNonce(1).WithBalance(2).WithCode(bytes).TestObject;
        bundle.SetAccount(address, account);
        bundle.SetCode(account.CodeHash.ValueHash256, new CodeInfo(bytes));
        // EIP-8297 at d2a64c2d: literal Python mapping and merkelization, using BLAKE3.
        ValueHash256 expected = new("0x1d6376e73eb20356030d5335b0297c6d29b1222e9522450c33406126f6f9f5ee");
        Assert.That(Fold(bundle, default), Is.EqualTo(expected));
    }

    [TestCase(false, 0ul)]
    [TestCase(true, 0ul)]
    [TestCase(false, 9ul)]
    [TestCase(true, 9ul)]
    public void Whole_account_and_code_survive_fold_and_seal_in_either_write_order(bool codeFirst, ulong nonce)
    {
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0),
            new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(default, null)), pool, PbtResourcePool.Usage.MainBlockProcessing);
        byte[] bytes = new byte[160];
        Bytes.FromHexString("6001600055").CopyTo(bytes, 0);
        CodeInfo code = new(bytes);
        Account account = Build.An.Account.WithNonce(nonce).WithBalance(nonce).WithStorageRoot(TestItem.KeccakB).WithCode(bytes).TestObject;
        if (codeFirst) bundle.SetCode(account.CodeHash.ValueHash256, code);
        bundle.SetAccount(TestItem.AddressA, account);
        if (!codeFirst) bundle.SetCode(account.CodeHash.ValueHash256, code);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.GetAccount(TestItem.AddressA), Is.SameAs(account));
            Assert.That(bundle.GetCode(account.CodeHash.ValueHash256), Is.SameAs(code));
            Dictionary<PbtStorageFullKey, ValueHash256> staged = [];
            foreach ((PbtStorageFullKey key, ValueHash256? value) in bundle.EnumeratePendingLeafMutationsForTest())
                if (value is not null) staged[key] = value.Value;
            Assert.That(staged, Is.EquivalentTo(bundle.EnumerateLeaves()), "setters must translate before root preparation");
        }
        ValueHash256 root = Fold(bundle, default);
        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, nonce, nonce, bytes);
        Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)));
        Assert.That(Fold(bundle, root), Is.EqualTo(root));
        using PbtSnapshot snapshot = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), root);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.GetAccount(TestItem.AddressA), Is.SameAs(account));
            Assert.That(bundle.GetCode(account.CodeHash.ValueHash256), Is.SameAs(code));
            Assert.That(snapshot.Content.Accounts[PbtKeyDerivation.AddressKeyHash(TestItem.AddressA)], Is.SameAs(account));
            Assert.That(snapshot.Content.Codes[account.CodeHash.ValueHash256], Is.SameAs(code));
            Assert.That(bundle.GetCodeReference(account.CodeHash.ValueHash256), Is.EqualTo(1));
        }
    }

    [Test]
    public void Shared_code_survives_account_replacement_and_last_reference_removal(
        [Values] bool reverseOrder, [Values(0ul, 3ul)] ulong replacementNonce,
        [Values("", "00", "6001")] string replacementCode, [Values(1, 129, 258)] int chunkCount)
    {
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0),
            new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(default, null)), pool, PbtResourcePool.Usage.MainBlockProcessing);
        byte[] bytes = new byte[chunkCount * 31];
        bytes.AsSpan().Fill(0x5b);
        CodeInfo code = new(bytes);
        Account account = Build.An.Account.WithCode(bytes).TestObject;
        bundle.SetCode(account.CodeHash.ValueHash256, code);
        bundle.SetAccount(TestItem.AddressA, account);
        bundle.SetAccount(TestItem.AddressB, account);
        int codeLeaves = 0;
        foreach ((PbtStorageFullKey key, ValueHash256 _) in bundle.EnumerateLeaves())
        {
            if (key.Bytes[0] == 0x01) codeLeaves++;
            else Assert.That(key.Bytes[^1], Is.LessThan(128), "code must not occupy account header leaves");
        }
        Assert.That(codeLeaves, Is.EqualTo(chunkCount), "identical bytecode must share every chunk");
        ValueHash256 root = Fold(bundle, default);
        using PbtSnapshot original = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), root);
        byte[] replacementBytes = Bytes.FromHexString(replacementCode);
        Account replacement = Build.An.Account.WithNonce(replacementNonce).WithCode(replacementBytes).TestObject;
        if (replacement.HasCode) bundle.SetCode(replacement.CodeHash.ValueHash256, new CodeInfo(replacementBytes));
        if (reverseOrder) bundle.SetAccount(TestItem.AddressB, account);
        bundle.SetAccount(TestItem.AddressA, replacement);
        if (!reverseOrder) bundle.SetAccount(TestItem.AddressB, account);
        root = Fold(bundle, root);
        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, replacement.Nonce, replacement.Balance, replacementBytes);
        PbtReferenceModel.SetAccount(model, TestItem.AddressB, account.Nonce, account.Balance, bytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)));
            Assert.That(bundle.GetCodeReference(account.CodeHash.ValueHash256), Is.EqualTo(1));
            Assert.That(original.Content.Accounts[PbtKeyDerivation.AddressKeyHash(TestItem.AddressA)], Is.SameAs(account));
            Assert.That(original.Content.Codes[account.CodeHash.ValueHash256], Is.SameAs(code));
        }
        bundle.SetAccount(TestItem.AddressB, null);
        root = Fold(bundle, root);
        model.Clear();
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, replacement.Nonce, replacement.Balance, replacementBytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)));
            Assert.That(bundle.GetCodeReference(account.CodeHash.ValueHash256), Is.Zero);
            Assert.That(bundle.GetCode(account.CodeHash.ValueHash256), Is.SameAs(code));
        }
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(40)]
    public void Code_resolution_preserves_other_pending_accounts_and_survives_scratch_reuse(int accountCount)
    {
        using TrackingTransientPool pool = new();
        using PbtSnapshotBundle bundle = CreatePrewarmBundle(pool);
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
        Assert.Throws<InvalidDataException>(() => Fold(bundle, default));
        bundle.SetCode(otherAccount.CodeHash.ValueHash256, new CodeInfo(otherBytes));
        ValueHash256 root = Fold(bundle, default);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)));
            Assert.That(bundle.GetCodeReference(account.CodeHash.ValueHash256), Is.EqualTo(accountCount));
            Assert.That(bundle.GetCodeReference(otherAccount.CodeHash.ValueHash256), Is.EqualTo(1));
            Assert.That(bundle.PendingMutationCount, Is.Zero);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Pending_code_replacement_and_final_reference_readdition_stage_latest_values(bool resolveAbandonedCode)
    {
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0),
            new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(default, null)), pool, PbtResourcePool.Usage.MainBlockProcessing);
        byte[] abandonedBytes = Bytes.FromHexString("6001");
        Account abandoned = Build.An.Account.WithCode(abandonedBytes).TestObject;
        byte[] bytes = new byte[(PbtKeyDerivation.StemSubtreeWidth + 2) * 31];
        bytes.AsSpan().Fill(0x5b);
        Account account = Build.An.Account.WithBalance(3).WithCode(bytes).TestObject;
        bundle.SetAccount(TestItem.AddressA, abandoned);
        bundle.SetAccount(TestItem.AddressA, account);
        if (resolveAbandonedCode) bundle.SetCode(abandoned.CodeHash.ValueHash256, new CodeInfo(abandonedBytes));
        bundle.SetCode(account.CodeHash.ValueHash256, new CodeInfo(bytes));
        bundle.SetAccount(TestItem.AddressA, null);
        bundle.SetAccount(TestItem.AddressB, account);
        Dictionary<PbtStorageFullKey, ValueHash256> staged = [];
        foreach ((PbtStorageFullKey key, ValueHash256? value) in bundle.EnumeratePendingLeafMutationsForTest())
            if (value is not null) staged[key] = value.Value;
        Assert.That(staged, Is.EquivalentTo(bundle.EnumerateLeaves()));
        ValueHash256 root = Fold(bundle, default);
        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, TestItem.AddressB, account.Nonce, account.Balance, bytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)));
            Assert.That(bundle.GetCodeReference(abandoned.CodeHash.ValueHash256), Is.Zero);
            Assert.That(bundle.GetCodeReference(account.CodeHash.ValueHash256), Is.EqualTo(1));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Late_code_lookup_resolves_pending_mutations_without_reapplying_references(bool failFirstFold)
    {
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0),
            new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(default, null)), pool, PbtResourcePool.Usage.MainBlockProcessing);
        byte[] bytes = Bytes.FromHexString("6001600055");
        Account account = Build.An.Account.WithCode(bytes).TestObject;
        bundle.SetAccount(TestItem.AddressA, account);
        if (failFirstFold)
        {
            Assert.Throws<InvalidDataException>(() => Fold(bundle, default));
            Assert.That(bundle.GetCodeReference(account.CodeHash.ValueHash256), Is.EqualTo(1));
        }
        bundle.ReadCode = hash => hash == account.CodeHash.ValueHash256 ? bytes : null;
        if (!failFirstFold)
        {
            KeyValuePair<PbtStorageFullKey, ValueHash256?>[] pending = [.. bundle.EnumeratePendingLeafMutationsForTest()];
            Assert.That(bundle.GetCode(account.CodeHash.ValueHash256), Is.Not.Null);
            Assert.That(bundle.EnumeratePendingLeafMutationsForTest(), Is.EquivalentTo(pending), "a read-through fill must not translate mutations");
        }
        ValueHash256 root = Fold(bundle, default);
        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, account.Nonce, account.Balance, bytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)));
            Assert.That(bundle.GetCodeReference(account.CodeHash.ValueHash256), Is.EqualTo(1));
            Assert.That(bundle.PendingMutationCount, Is.Zero);
        }
    }

    [TestCase(0x00)]
    [TestCase(0x01)]
    [TestCase(0xFF)]
    public void Failed_partition_fold_keeps_mutations_pending_without_completing_snapshot(int failedZone)
    {
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0),
            new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(default, null)), pool, PbtResourcePool.Usage.MainBlockProcessing);
        byte[] bytes = new byte[(PbtKeyDerivation.StemSubtreeWidth + 2) * 31];
        bytes.AsSpan().Fill(0x5b);
        Account account = Build.An.Account.WithBalance(3).WithCode(bytes).TestObject;
        bundle.SetCode(account.CodeHash.ValueHash256, new CodeInfo(bytes));
        bundle.SetAccount(TestItem.AddressA, account);
        bundle.SetSlot(TestItem.AddressA, 1000, EvmWordSlot.FromStripped(Bytes.FromHexString("01")));
        ValueHash256 root = Fold(bundle, default);
        using PbtSnapshot original = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), root);

        Account replacement = account.WithChangedBalance(4);
        bundle.SetAccount(TestItem.AddressA, replacement);
        bundle.SetSlot(TestItem.AddressA, 1000, EvmWordSlot.FromStripped(Bytes.FromHexString("02")));
        KeyValuePair<PbtStorageFullKey, ValueHash256?>[] pending = [.. bundle.EnumeratePendingLeafMutationsForTest()];
        CountingStore store = new(bundle) { FailedZone = failedZone };
        Assert.Throws<AggregateException>(() => TrieUpdater.UpdateRoot(store, root, bundle.PrepareLeafChanges()));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.TreeRoot, Is.EqualTo(root));
            Assert.That(bundle.EnumeratePendingLeafMutationsForTest(), Is.EquivalentTo(pending));
            Assert.That(bundle.GetAccount(TestItem.AddressA), Is.SameAs(replacement));
            Assert.That(bundle.GetCodeReference(account.CodeHash.ValueHash256), Is.EqualTo(1));
            Assert.Throws<InvalidOperationException>(() => bundle.CollectSnapshot(new StateId(1, default), new StateId(2, default), root));
            Assert.That(original.TreeRoot, Is.EqualTo(root));
            Assert.That(original.Content.Accounts[PbtKeyDerivation.AddressKeyHash(TestItem.AddressA)], Is.SameAs(account));
        }
    }

    private static ValueHash256 Fold(PbtSnapshotBundle bundle, ValueHash256 root)
    {
        PbtPartitionBatches changes = bundle.PrepareLeafChanges();
        try
        {
            ValueHash256 updated = TrieUpdater.UpdateRoot(new PbtSnapshotStore(bundle), root, changes);
            bundle.CompleteLeafChanges();
            return updated;
        }
        finally
        {
            changes.Dispose();
        }
    }

    private static PbtSnapshotPooledList Snapshots(PbtResourcePool pool, params PbtSnapshotContent[] contents)
    {
        PbtSnapshotPooledList snapshots = new(contents.Length);
        for (int index = 0; index < contents.Length; index++)
            snapshots.Add(new PbtSnapshot(StateId.PreGenesis, new StateId((ulong)index, default), default, contents[index], pool, PbtResourcePool.Usage.MainBlockProcessing));
        return snapshots;
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

    private static byte[] Value(byte marker)
    {
        byte[] value = new byte[32];
        value[^1] = marker;
        return value;
    }

    private static byte[] BranchEncoding(byte marker)
    {
        ValueHash256 left = new(Value(marker));
        ValueHash256 right = new(Value((byte)(marker + 32)));
        return PbtNodeCodec.EncodeBranch([], 0, left, right);
    }

    private static byte[] EncodeGroup(PbtNodePath groupKey, IReadOnlyList<PbtNodeRecord> records)
    {
        int length = PbtNodeGroupCodec.HeaderLength + PbtNodeGroupCodec.MaxTrailerLength;
        foreach (PbtNodeRecord record in records) length += record.Encoding.Length;
        byte[] payload = new byte[length];
        BufferWriter writer = new(payload);
        PbtNodeGroupCodec.Encode(ref writer, groupKey, records);
        return writer.WrittenSpan.ToArray();
    }

    private static void AssertSnapshotUnchanged(PbtSnapshotBundle bundle, PbtStorageFullKey leafKey, PbtNodePath nodePath, byte[] node)
    {
        bundle.CompleteLeafChanges();
        using PbtSnapshot snapshot = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), default);
        bool foundGroup = snapshot.Content.TryGetNodeGroup(nodePath, out RefCountingMemory? actual);
        using RefCountingMemory? actualLease = actual;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.Content.Storages, Has.Count.EqualTo(1));
            Assert.That(snapshot.Content.Storages.ContainsKey(leafKey), Is.True);
            Assert.That(snapshot.Content.NodeGroups, Has.Count.EqualTo(1));
            Assert.That(foundGroup, Is.True);
            Assert.That(actual!.GetSpan().ToArray(), Is.EqualTo(node));
        }
    }

    private sealed class Reader(PbtStorageFullKey key, ValueHash256? value) : IPbtPersistence.IReader
    {
        public PbtNodePath GroupKey { get; set; } = new([], 0);
        public byte[]? GroupPayload { get; set; }
        public IRefCountingMemoryProvider MemoryProvider { get; set; } = PooledRefCountingMemoryProvider.Instance;
        public Exception? GroupReadException { get; set; }
        public int GroupReadCount { get; private set; }
        public StateId CurrentState => StateId.PreGenesis;
        public ValueHash256 CurrentRoot { get; set; }
        public Account? GetAccount(in ValueHash256 addressHash) => null;
        public EvmWord GetSlot(PbtStorageFullKey requested) => requested == key && value is { } word ? EvmWordSlot.FromStripped(word.Bytes) : default;
        public CodeInfo? GetCode(in ValueHash256 codeHash) => null;
        public IEnumerable<KeyValuePair<ValueHash256, Account>> EnumerateAccounts() => [];
        public IEnumerable<KeyValuePair<PbtStorageFullKey, EvmWord>> EnumerateStorage(PbtStorageFullKey? prefix = null)
        {
            if (value is { } word && (prefix is null || prefix.Value.IsPrefixOf(key)))
                yield return new(key, EvmWordSlot.FromStripped(word.Bytes));
        }
        public RefCountingMemory? GetNodeGroup<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath>
        {
            GroupReadCount++;
            if (GroupReadException is not null) throw GroupReadException;
            if (!groupKey.Equals(GroupKey) || GroupPayload is null) return null;
            RefCountingMemory memory = MemoryProvider.Rent(GroupPayload.Length);
            GroupPayload.CopyTo(memory.GetSpan());
            return memory;
        }
        public IEnumerable<PbtStorageNodePath> EnumerateNodeGroupKeys() => [];
        public ulong GetCodeReference(in ValueHash256 codeHash) => 0;
        public void Dispose() { }
    }

    private sealed class CountingStore(PbtSnapshotBundle bundle) : IPbtStore
    {
        public int ApplyCount { get; private set; }
        public int? FailedZone { get; init; }
        public RefCountingMemory? GetNodeGroup<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath> => bundle.GetNodeGroup(groupKey);
        public void SetNodeGroup<TPath>(TPath groupKey, RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath>
        {
            if (groupKey.BitDepth == 8 && groupKey.GetByte(0) == FailedZone)
                throw new InvalidDataException("Configured partition write failure.");
            ApplyCount++;
            bundle.SetNodeGroup(groupKey, payload);
        }
    }
}
