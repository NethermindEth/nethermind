// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
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
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Pbt.ScopeProvider;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtSnapshotBundleTests
{
    [TestCase(false, false, false)]
    [TestCase(false, true, false)]
    [TestCase(true, true, false)]
    [TestCase(false, true, true)]
    [TestCase(true, true, true)]
    public void Enumeration_streams_base_and_disposes_iterator_on_early_exit(bool writable, bool storage, bool filtered)
    {
        PbtStorageTreeKey key = PbtStateKey.Storage(TestItem.AddressA, 1);
        Reader reader = new(key, new ValueHash256(Value(1)))
        {
            Accounts = [new(PbtKeyDerivation.AddressKeyHash(TestItem.AddressA), new Account(1, 1))],
            ThrowAfterFirst = true
        };
        PbtResourcePool pool = new(new PbtConfig());
        PbtReadOnlySnapshotBundle readOnly = new(new(0), reader);
        using PbtSnapshotBundle bundle = new(new(0), readOnly, pool, PbtResourcePool.Usage.MainBlockProcessing);
        if (storage)
        {
            ValueHash256? addressFilter = filtered ? PbtKeyDerivation.AddressKeyHash(TestItem.AddressA) : (ValueHash256?)null;
            using IEnumerator<KeyValuePair<PbtStorageTreeKey, EvmWord>> iterator = (writable ? bundle.EnumerateStorage(addressFilter) : readOnly.EnumerateStorage(addressFilter)).GetEnumerator();
            Assert.That(iterator.MoveNext(), Is.True);
        }
        else
        {
            using IEnumerator<KeyValuePair<ValueHash256, Account>> iterator = readOnly.EnumerateAccounts().GetEnumerator();
            Assert.That(iterator.MoveNext(), Is.True);
        }
        Assert.That(reader.IteratorDisposals, Is.EqualTo(1));
    }

    [Test]
    public void First_write_to_a_run_seeds_the_whole_run_from_the_newest_layer_holding_it([Values] bool heldByLayer)
    {
        ValueHash256 addressHash = PbtKeyDerivation.AddressKeyHash(TestItem.AddressA);
        // Slots 3, 5 and 6 share one run.
        PbtStorageTreeKey persistedKey = PbtStateKey.Storage(TestItem.AddressA, 5);
        PbtStorageTreeKey layerKey = PbtStateKey.Storage(TestItem.AddressA, 6);
        EvmWord persisted = EvmWordSlot.FromStripped(Value(1));
        EvmWord layer = EvmWordSlot.FromStripped(Value(2));
        EvmWord local = EvmWordSlot.FromStripped(Value(3));
        PbtResourcePool pool = new(new PbtConfig());
        PbtSnapshotContent sharedContent = new();
        if (heldByLayer) sharedContent.SetSlot(layerKey, layer);
        PbtSnapshotPooledList sharedSnapshots = new(1)
        {
            new PbtSnapshot(StateId.PreGenesis, new StateId(1, default), default, sharedContent, pool, PbtResourcePool.Usage.MainBlockProcessing)
        };
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), new PbtReadOnlySnapshotBundle(sharedSnapshots, new Reader(persistedKey, new ValueHash256(Value(1)))), pool, PbtResourcePool.Usage.MainBlockProcessing);
        bundle.SetSlot(TestItem.AddressA, 3, local);
        EvmWord[] afterWrite = [bundle.GetSlot(TestItem.AddressA, 3), bundle.GetSlot(TestItem.AddressA, 5), bundle.GetSlot(TestItem.AddressA, 6)];
        bundle.SetSlot(TestItem.AddressA, heldByLayer ? 6u : 5u, default);
        KeyValuePair<PbtStorageTreeKey, EvmWord>[] afterClear = bundle.EnumerateStorage(addressHash).ToArray();
        bundle.SelfDestruct(TestItem.AddressA);
        bundle.SetSlot(TestItem.AddressA, 5, local);
        using (Assert.EnterMultipleScope())
        {
            // A layer holding the run answers for all of it, so the persisted slot is masked when a layer holds the run.
            Assert.That(afterWrite, Is.EqualTo(new[] { local, heldByLayer ? default : persisted, heldByLayer ? layer : default }));
            Assert.That(afterClear.Select(slot => slot.Key), Is.EqualTo(new[] { PbtStateKey.Storage(TestItem.AddressA, 3) }));
            Assert.That(new[] { bundle.GetSlot(TestItem.AddressA, 3), bundle.GetSlot(TestItem.AddressA, 5), bundle.GetSlot(TestItem.AddressA, 6) }, Is.EqualTo(new[] { default, local, default }));
        }
    }

    [Test]
    public void Enumeration_merges_local_deletes_clears_and_same_layer_rewrites([Values] bool filtered)
    {
        ValueHash256 addressHash = PbtKeyDerivation.AddressKeyHash(TestItem.AddressA);
        PbtStorageTreeKey deleted = PbtStateKey.Storage(TestItem.AddressA, 1);
        PbtStorageTreeKey rewritten = PbtStateKey.Storage(TestItem.AddressA, 1000);
        PbtStorageTreeKey cleared = PbtStateKey.Storage(TestItem.AddressA, 2000);
        PbtStorageTreeKey other = PbtStateKey.Storage(TestItem.AddressB, 1);
        EvmWord original = EvmWordSlot.FromStripped(Value(1));
        EvmWord replacement = EvmWordSlot.FromStripped(Value(2));
        SortedDictionary<PbtStorageTreeKey, EvmWord> persisted = new(PbtStorageKeyLayout.Comparer) { [deleted] = original, [rewritten] = original, [cleared] = original, [other] = original };
        Reader reader = new(default, null) { Storage = persisted };
        PbtResourcePool pool = new(new PbtConfig());
        PbtSnapshotContent content = new();
        content.ClearStorage(addressHash);
        content.SetSlot(rewritten, original);
        content.SetSlot(deleted, original);
        PbtSnapshotPooledList snapshots = new(1) { new PbtSnapshot(StateId.PreGenesis, new StateId(1, default), default, content, pool, PbtResourcePool.Usage.MainBlockProcessing) };
        using PbtSnapshotBundle bundle = new(snapshots, new PbtReadOnlySnapshotBundle(new(0), reader), pool, PbtResourcePool.Usage.MainBlockProcessing);
        bundle.SetSlot(TestItem.AddressA, 1, default);
        bundle.SetSlot(TestItem.AddressA, 1000, replacement);
        SortedDictionary<PbtStorageTreeKey, EvmWord> expected = new(PbtStorageKeyLayout.Comparer) { [rewritten] = replacement };
        if (!filtered) expected[other] = original;
        Assert.That(bundle.EnumerateStorage(filtered ? addressHash : (ValueHash256?)null), Is.EqualTo(expected));
    }

    [Test]
    public void SnapshotContent_ConcurrentSameGroupReplacementsReleasePreviousPayloads([Values] bool tombstones)
    {
        using PbtSnapshotContent content = new();
        TrackingMemoryProvider memoryProvider = new();
        PbtNodePath groupPath = new([], 0);
        byte[] encoding = EncodeGroup(groupPath, [new PbtNodeRecord(groupPath.ToPath<PbtStorageNodePath>(), BranchEncoding(1))]);

        System.Threading.Tasks.Parallel.For(0, 1000, iteration =>
        {
            using RefCountingMemory payload = Memory(encoding, memoryProvider);
            content.SetNodeGroup(groupPath, payload);
            content.SetNodeGroup(groupPath, payload);
            if (tombstones) content.SetNodeGroup(groupPath, null);
        });

        bool found = content.TryGetNodeGroup(groupPath, out RefCountingMemory? current);
        using (current)
        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(current?.GetSpan().ToArray(), Is.EqualTo(tombstones ? null : encoding));
            Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.EqualTo(tombstones ? 0 : 1));
        }

        content.Reset();
        Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
    }

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
                    expectedNodeBytes[index] = storagePath.ToPathArray().Length + (tombstone ? 0 : replacement.Length);
                });

                long nodeBytes = 0;
                foreach (long size in expectedNodeBytes) nodeBytes += size;
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(content.NodeGroups, Has.Count.EqualTo(groupCount));
                    Assert.That(content.GetPayloadSize(), Is.EqualTo(new PbtSnapshotPayloadSize(0, nodeBytes)));
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

    [TestCase(false)]
    [TestCase(true)]
    public void RetiredPrewarmResource_IsNotRecycledUntilItsLastReaderReleases(bool dispose)
    {
        using TrackingTransientPool pool = new();
        using PbtSnapshotBundle bundle = CreatePrewarmBundle(pool);
        PbtTransientResource retired = pool.LastRented!;
        Assert.That(retired.ShouldPrewarm(TestItem.AddressA), Is.True);
        Assert.That(retired.TryAcquireLease(), Is.True);
        try
        {
            if (dispose) bundle.Dispose();
            else bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), default).Dispose();
            Assert.That(pool.ReturnCount, Is.Zero);
            Assert.That(retired.ShouldPrewarm(TestItem.AddressA), Is.False);
            using PbtSnapshotBundle concurrentBundle = CreatePrewarmBundle(pool);
            Assert.That(pool.LastRented, Is.Not.SameAs(retired));
            Assert.That(retired.ShouldPrewarm(TestItem.AddressB), Is.True);
        }
        finally
        {
            retired.ReleaseLease();
        }
        Assert.That(pool.ReturnCount, Is.EqualTo(2));
        using PbtSnapshotBundle nextBundle = CreatePrewarmBundle(pool);
        Assert.That(pool.LastRented, Is.SameAs(retired));
    }

    [Test]
    public void Dispose_ReturnsTransientOnceEvenWhenOtherCleanupThrows()
    {
        using TrackingTransientPool pool = new() { ThrowOnBuilderReturn = true };
        PbtSnapshotBundle bundle = CreatePrewarmBundle(pool);
        Assert.Throws<IOException>(bundle.Dispose);
        Assert.DoesNotThrow(bundle.Dispose);
        Assert.That(pool.ReturnCount, Is.EqualTo(1));
    }

    private static PbtSnapshotBundle CreatePrewarmBundle(IPbtResourcePool pool) => new(
        new PbtSnapshotPooledList(0),
        new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(new PbtStorageTreeKey([0]), null)),
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
        public PbtWriteBatchBuilder<PbtPath> GetWriteBatch(PbtResourcePool.Usage usage) => new(2);
        public void ReturnWriteBatch(PbtResourcePool.Usage usage, PbtWriteBatchBuilder<PbtPath> builder)
        {
            builder.Dispose();
            if (ThrowOnBuilderReturn) throw new IOException("Builder return failed");
        }

        public PbtWriteBatchBuilder<PbtStoragePath> GetStorageWriteBatch(PbtResourcePool.Usage usage) => new(2);
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
    public void LocalCanonicalWrites_OverrideSharedAndPersistedLeaves()
    {
        PbtStorageTreeKey key = PbtStateKey.Storage(TestItem.AddressA, 1);
        ValueHash256 persisted = new(Value(1));
        ValueHash256 shared = new(Value(2));
        ValueHash256 local = new(Value(3));
        PbtResourcePool pool = new(new PbtConfig());
        PbtSnapshotContent sharedContent = new();
        sharedContent.SetSlot(key, EvmWordSlot.FromStripped(shared.Bytes));
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
        PbtStorageTreeKey matching = PbtStateKey.Storage(TestItem.AddressA, 1000);
        PbtStorageTreeKey other = PbtStateKey.Storage(TestItem.AddressB, 1000);
        PbtStorageTreeKey prefix = PbtStateKey.StoragePrefix(TestItem.AddressA);
        PbtResourcePool pool = new(new PbtConfig());
        PbtSnapshotContent sharedContent = new();
        sharedContent.SetSlot(matching, EvmWordSlot.FromStripped(Value(1)));
        sharedContent.SetSlot(other, EvmWordSlot.FromStripped(Value(2)));
        PbtSnapshotPooledList sharedSnapshots = new(1)
        {
            new PbtSnapshot(StateId.PreGenesis, new StateId(1, default), default, sharedContent, pool, PbtResourcePool.Usage.MainBlockProcessing)
        };
        using PbtReadOnlySnapshotBundle readOnly = new(sharedSnapshots, new Reader(matching, null));
        List<PbtStorageTreeKey> expected = filtered ? [matching] : [matching, other];
        expected.Sort();
        List<PbtStorageTreeKey> sharedKeys = [];
        foreach (KeyValuePair<PbtStorageTreeKey, ValueHash256> leaf in filtered ? readOnly.EnumerateLeaves(prefix) : readOnly.EnumerateLeaves())
            sharedKeys.Add(leaf.Key);
        Assert.That(sharedKeys, Is.EqualTo(expected));
    }

    [TestCase(0u)]
    [TestCase(63u)]
    [TestCase(64u)]
    [TestCase(256u)]
    [TestCase(uint.MaxValue)]
    public void Storage_mutations_use_small_header_and_wide_storage_partitions(uint slotValue)
    {
        UInt256 slot = slotValue == uint.MaxValue ? UInt256.MaxValue : new UInt256(slotValue);
        PbtStorageTreeKey key = PbtStateKey.Storage(TestItem.AddressA, slot);
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
            }
            bundle.CompleteLeafChanges();
        }
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void Trie_updates_leave_independently_staged_flat_entries_unchanged(bool leafExists, bool delete)
    {
        PbtStorageTreeKey key = PbtStateKey.Storage(TestItem.AddressA, 1);
        ValueHash256 flatValue = new(Value(9));
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0),
            new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(key, null)), pool, PbtResourcePool.Usage.MainBlockProcessing);
        PbtSnapshotStore store = new(bundle);
        using PbtWriteBatchBuilder<PbtStorageTreeKey> initial = new(0);
        if (leafExists) initial.Set(key, new ValueHash256(Value(1)));
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, initial.Build());
        bundle.SetSlot(TestItem.AddressA, 1, EvmWordSlot.FromStripped(flatValue.Bytes));

        using PbtWriteBatchBuilder<PbtStorageTreeKey> changes = new(0);
        if (delete) changes.Delete(key);
        else changes.Set(key, new ValueHash256(Value(2)));
        ValueHash256 updatedRoot = TrieUpdater.UpdateRoot(store, root, changes.Build());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.GetSlot(TestItem.AddressA, 1), Is.EqualTo(EvmWordSlot.FromStripped(flatValue.Bytes)));
            Assert.That(updatedRoot, Is.EqualTo(delete ? default : PbtNodeCodec.HashLeaf(key.Bytes, Value(2))));
            Assert.That(store.GetNode(new PbtNodePath([], 0), updatedRoot), Is.EqualTo(delete ? null : PbtNodeCodec.EncodeLeaf(key)));
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
            new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(new PbtStorageTreeKey([0]), null)), pool, PbtResourcePool.Usage.MainBlockProcessing);
        // Only a delegated account may be deleted once its code is known: chunk leaves are shared without a reference count.
        byte[] code = deleteAccount ? Delegation : RealCode;
        Account account = Build.An.Account.WithCode(code).TestObject;
        bundle.SetAccount(TestItem.AddressA, account);
        bundle.SetAccount(TestItem.AddressA, account);
        bundle.SetCode(account.CodeHash.ValueHash256, new CodeInfo(code));
        EvmWord value = EvmWordSlot.FromStripped(Bytes.FromHexString("ab"));
        bundle.SetSlot(TestItem.AddressA, slot, value);
        ValueHash256 root = Fold(bundle, default);
        using (Assert.EnterMultipleScope())
        {
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
        }
    }

    [NonParallelizable]
    [TestCase(0UL, 2)]
    [TestCase(1UL, 2)]
    [TestCase(1048576UL, 1)]
    public void Trie_cache_reuses_only_matching_subtree_hashes(ulong budget, int expectedReads)
    {
        long initialHits = Metrics.PbtTrieCacheHits["account"];
        long initialMisses = Metrics.PbtTrieCacheMisses["account"];
        TrackingMemoryProvider memory = new();
        PbtNodePath path = new([], 0);
        byte[] encoding = EncodeGroup(path, [new PbtNodeRecord(path.ToPath<PbtStorageNodePath>(), BranchEncoding(1))]);
        using PbtTrieNodeCache cache = new(new PbtConfig { AccountTrieNodeCacheSizeBudget = budget });
        Reader reader = new(default, null) { GroupPayload = encoding, MemoryProvider = memory, CurrentRoot = new ValueHash256(Value(1)) };
        PbtResourcePool pool = new(new PbtConfig());
        PbtReadOnlySnapshotBundle readOnly = new(new(0), reader);
        using PbtSnapshotBundle bundle = new(Snapshots(pool, new PbtSnapshotContent()), readOnly, pool, PbtResourcePool.Usage.MainBlockProcessing, cache);
        Assert.That(bundle.TreeRoot, Is.Not.EqualTo(readOnly.TreeRoot));
        for (int read = 0; read < 2; read++)
        {
            using RefCountingMemory? payload = bundle.GetNodeGroup(path, reader.CurrentRoot);
            Assert.That(payload!.GetSpan().ToArray(), Is.EqualTo(encoding));
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.GroupReadCount, Is.EqualTo(expectedReads));
            Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero, "cache must not retain oversized source allocations");
            Assert.That(cache.MemorySize, Is.LessThanOrEqualTo(budget));
        }
        int readsBeforeDirectRead = reader.GroupReadCount;
        for (int read = 0; read < 2; read++)
        {
            using RefCountingMemory? payload = readOnly.GetNodeGroup(path);
            Assert.That(payload!.GetSpan().ToArray(), Is.EqualTo(encoding));
        }
        Assert.That(reader.GroupReadCount, Is.EqualTo(readsBeforeDirectRead + 2), "direct read-only reads bypass the populated trie cache");
        Reader forkReader = new(default, null) { GroupPayload = encoding, CurrentRoot = new ValueHash256(Value(2)) };
        using PbtSnapshotBundle fork = new(new(0), new PbtReadOnlySnapshotBundle(new(0), forkReader), pool, PbtResourcePool.Usage.MainBlockProcessing, cache);
        using RefCountingMemory? forkPayload = fork.GetNodeGroup(path, forkReader.CurrentRoot);
        Assert.That(forkReader.GroupReadCount, Is.EqualTo(1));
        Assert.That(cache.TryGet(default, new PbtNodePath([0], 4), out _), Is.False);
        Assert.That(cache.TryGet(default, new PbtStorageNodePath([], 0), out _), Is.False);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.PbtTrieCacheHits["account"] - initialHits, Is.EqualTo(2 - expectedReads));
            Assert.That(Metrics.PbtTrieCacheMisses["account"] - initialMisses, Is.EqualTo(expectedReads + 3));
        }
        using (RefCountingMemory? payload = bundle.GetNodeGroup(path, reader.CurrentRoot))
            Assert.That(payload!.Memory.ToArray(), Is.EqualTo(encoding));
        int readsBeforeWarming = reader.GroupReadCount;
        using PbtTrieWarmupSession session = bundle.CreateTrieWarmupSession(new NoopTrieWarmer(), 1);
        using RefCountingMemory? warmed = ((IPbtStore)session).GetNodeGroup(path, reader.CurrentRoot);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(warmed!.Memory.ToArray(), Is.EqualTo(encoding));
            Assert.That(reader.GroupReadCount, Is.EqualTo(readsBeforeWarming + expectedReads - 1), "warming must use the supplied subtree hash, not its local view root");
        }
    }

    [Test]
    public void Trie_cache_reuses_unchanged_descendants_across_roots([Values] bool warmFirst)
    {
        PbtNodePath path = new(Bytes.FromHexString("00"), 4);
        byte[] originalNode = BranchEncoding(1);
        byte[] changedNode = BranchEncoding(2);
        ValueHash256 originalHash = PbtNodeCodec.Hash(new PbtNodeReader(originalNode));
        ValueHash256 changedHash = PbtNodeCodec.Hash(new PbtNodeReader(changedNode));
        PbtStorageNodePath childPath = PbtFourLevelGroupGeometry.PathOf(path, 0).ToPath<PbtStorageNodePath>();
        byte[] original = EncodeGroup(path, [new PbtNodeRecord(childPath, originalNode)]);
        byte[] changed = EncodeGroup(path, [new PbtNodeRecord(childPath, changedNode)]);
        using PbtTrieNodeCache cache = new(new PbtConfig());
        PbtResourcePool pool = new(new PbtConfig());
        Reader reader = new(default, null) { GroupKey = path, GroupPayload = original, CurrentRoot = TestItem.KeccakA.ValueHash256 };
        using PbtSnapshotBundle bundle = new(new(0), new PbtReadOnlySnapshotBundle(new(0), reader), pool, PbtResourcePool.Usage.MainBlockProcessing, cache);
        using PbtTrieWarmupSession session = bundle.CreateTrieWarmupSession(new NoopTrieWarmer(), 1);
        using (RefCountingMemory? payload = warmFirst
            ? ((IPbtStore)session).GetNodeGroup(path, originalHash)
            : bundle.GetNodeGroup(path, originalHash))
            Assert.That(payload!.Memory.ToArray(), Is.EqualTo(original));
        Assert.That(reader.GroupReadCount, Is.EqualTo(1));

        Reader forkReader = new(default, null) { GroupKey = path, GroupPayload = original, CurrentRoot = TestItem.KeccakB.ValueHash256 };
        using PbtSnapshotBundle fork = new(new(0), new PbtReadOnlySnapshotBundle(new(0), forkReader), pool, PbtResourcePool.Usage.MainBlockProcessing, cache);
        using PbtTrieWarmupSession forkSession = fork.CreateTrieWarmupSession(new NoopTrieWarmer(), 2);
        using (RefCountingMemory? payload = warmFirst
            ? fork.GetNodeGroup(path, originalHash)
            : ((IPbtStore)forkSession).GetNodeGroup(path, originalHash))
            Assert.That(payload!.Memory.ToArray(), Is.EqualTo(original));
        Assert.That(forkReader.GroupReadCount, Is.Zero, "an unrelated tree-root change must not invalidate this subtree");

        Reader changedReader = new(default, null) { GroupKey = path, GroupPayload = changed, CurrentRoot = TestItem.KeccakB.ValueHash256 };
        using PbtSnapshotBundle changedBundle = new(new(0), new PbtReadOnlySnapshotBundle(new(0), changedReader), pool, PbtResourcePool.Usage.MainBlockProcessing, cache);
        using (RefCountingMemory? payload = changedBundle.GetNodeGroup(path, changedHash))
            Assert.That(payload!.Memory.ToArray(), Is.EqualTo(changed));
        Assert.That(changedReader.GroupReadCount, Is.EqualTo(1), "different subtree hashes must miss even with the same whole-tree root");
        using (RefCountingMemory? payload = bundle.GetNodeGroup(path, originalHash))
            Assert.That(payload!.Memory.ToArray(), Is.EqualTo(original));
        Assert.That(reader.GroupReadCount, Is.EqualTo(2), "replacement must not make the old view return the new subtree");
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
        using RefCountingMemory source = Memory(Bytes.FromHexString("010203"));
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
                    Assert.That(memory, Is.EqualTo(entryMemory[index] + (label == partition ? 1600 - 3 : 0)), label);
                    totalMemory += memory;
                }
                Assert.That(cache.MemorySize, Is.EqualTo(totalMemory));
                Assert.That(cache.EntryCount, Is.EqualTo(CachePartitions.Length));
                foreach (string label in CachePartitions) Assert.That(Metrics.PbtTrieCacheEntries[label], Is.EqualTo(1), label);
            }
            cache.Dispose();
            Assert.That(cache.EntryCount, Is.Zero);
            for (int index = 0; index < CachePartitions.Length; index++)
            {
                Assert.That(Metrics.PbtTrieCacheMemory[CachePartitions[index]], Is.EqualTo(initialMemory[index]));
                Assert.That(Metrics.PbtTrieCacheEntries[CachePartitions[index]], Is.Zero);
            }
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
            cache.Add(new ValueHash256(Value(2)), path, larger);
            Assert.That(cache.TryGet(new ValueHash256(Value(1)), path, out _), Is.False, "a newer subtree hash replaces the same path");
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

    [TestCase(16, false, true)]
    [TestCase(2048, false, true)]
    [TestCase(1600, false, false)]
    [TestCase(2048, true, false)]
    public void Trie_cache_shares_right_sized_managed_payloads_and_copies_the_rest(int length, bool rocksDbBacked, bool expectedShared)
    {
        using PbtTrieNodeCache cache = new(CacheConfig("account", 1048576));
        PbtNodePath path = CachePath("account");
        RefCountingMemory source = rocksDbBacked
            ? RefCountingMemory.OwningRocksDb(ArrayMemoryManager.From(new byte[length])!)
            : Memory(new byte[length]);
        cache.Add(default, path, source);
        Assert.That(cache.TryGet(default, path, out RefCountingMemory? hit), Is.True);
        using (hit) Assert.That(ReferenceEquals(hit, source), Is.EqualTo(expectedShared));
        ((IDisposable)source).Dispose();
        Assert.That(source.TryAcquireLease(), Is.EqualTo(expectedShared), "only a shared payload stays leased by the cache after its producer releases it");
        if (expectedShared) ((IDisposable)source).Dispose();
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
        System.Threading.Tasks.Parallel.For(0, 10000, iteration =>
        {
            PbtNodePath path = CachePath(CachePartitions[iteration % CachePartitions.Length]);
            int variant = (iteration / CachePartitions.Length) % sources.Length;
            ValueHash256 groupHash = new(Value((byte)variant));
            cache.Add(groupHash, path, sources[variant]);
            if (cache.TryGet(groupHash, path, out RefCountingMemory? payload))
                using (payload) Assert.That(payload.GetSpan().ToArray(), Is.EqualTo(sources[variant].GetSpan().ToArray()), "a lock-free hit must return the payload admitted under its own subtree hash");
            if ((iteration & 63) == 0) cache.Clear();
        });
        foreach (RefCountingMemory source in sources) ((IDisposable)source).Dispose();
        cache.Clear();
        Assert.That(cache.MemorySize, Is.Zero);
    }

    [Test]
    public void Node_group_read_rejects_non_boundary_key_before_empty_persistence_lookup()
    {
        Reader reader = new(new PbtStorageTreeKey([0]), null);
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
        Reader reader = new(new PbtStorageTreeKey([0]), null) { GroupPayload = persisted };
        PbtResourcePool pool = new(new PbtConfig());
        PbtSnapshotPooledList sharedSnapshots = newestTier >= 1
            ? Snapshots(pool, Content(groupKey, persisted), Content(wideGroupKey, newestTier == 1 && tombstone ? null : shared))
            : new(0);
        PbtSnapshotPooledList localSnapshots = newestTier >= 2
            ? Snapshots(pool, Content(groupKey, shared), Content(wideGroupKey, newestTier == 2 && tombstone ? null : local))
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
        using PbtTrieWarmupSession session = bundle.CreateTrieWarmupSession(new NoopTrieWarmer(), 1);
        using RefCountingMemory? warmed = ((IPbtStore)session).GetNodeGroup(wideGroupKey, TestItem.KeccakA.ValueHash256);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(warmed?.Memory.ToArray(), Is.EqualTo(newestTier == 3 ? local : expected), "warming uses frozen layers, not live writes or stale cached base groups");
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
        Reader reader = new(new PbtStorageTreeKey([0]), null)
        {
            GroupPayload = malformed,
            MemoryProvider = memoryProvider,
        };
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), reader), pool, PbtResourcePool.Usage.MainBlockProcessing);
        PbtStorageTreeKey originalLeafKey = PbtStateKey.Storage(TestItem.AddressA, 1);
        PbtNodePath originalGroupKey = new([0x80], 4);
        byte[] originalGroup = EncodeGroup(originalGroupKey, [new PbtNodeRecord(PbtFourLevelGroupGeometry.PathOf(originalGroupKey, 0).ToPath<PbtStorageNodePath>(), BranchEncoding(1))]);
        using RefCountingMemory originalPayload = Memory(originalGroup);
        bundle.SetSlot(TestItem.AddressA, 1, EvmWordSlot.FromStripped(Value(2)));
        bundle.SetNodeGroup(originalGroupKey, TestItem.KeccakA.ValueHash256, originalPayload);
        using PbtWriteBatchBuilder<PbtStorageTreeKey> changes = new(0);
        changes.Set(new PbtStorageTreeKey([3]), new ValueHash256(Value(4)));

        Assert.Throws<InvalidDataException>(() => TrieUpdater.UpdateRoot(new PbtSnapshotStore(bundle), new ValueHash256(Value(5)), changes.Build()));
        AssertSnapshotUnchanged(bundle, originalLeafKey, originalGroupKey, originalGroup);
        Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
    }

    [Test]
    public void Malformed_group_replacement_is_rejected_without_mutating_snapshot()
    {
        PbtResourcePool pool = new(new PbtConfig());
        Reader reader = new(new PbtStorageTreeKey([0]), null);
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), reader), pool, PbtResourcePool.Usage.MainBlockProcessing);
        PbtStorageTreeKey leafKey = PbtStateKey.Storage(TestItem.AddressA, 1);
        PbtNodePath groupKey = new([], 0);
        byte[] original = EncodeGroup(groupKey, [new PbtNodeRecord(groupKey.ToPath<PbtStorageNodePath>(), BranchEncoding(1))]);
        using RefCountingMemory originalPayload = Memory(original);
        using RefCountingMemory malformed = Memory(Bytes.FromHexString("7f"));
        bundle.SetSlot(TestItem.AddressA, 1, EvmWordSlot.FromStripped(Value(2)));
        bundle.SetNodeGroup(groupKey, TestItem.KeccakA.ValueHash256, originalPayload);

        Assert.Throws<InvalidDataException>(() => bundle.SetNodeGroup(groupKey, TestItem.KeccakB.ValueHash256, malformed));
        AssertSnapshotUnchanged(bundle, leafKey, groupKey, original);
        Assert.That(reader.GroupReadCount, Is.Zero);
    }

    [Test]
    public void Updater_group_read_failure_preserves_prior_deltas_and_does_not_apply()
    {
        PbtResourcePool pool = new(new PbtConfig());
        PbtStorageTreeKey originalLeafKey = PbtStateKey.Storage(TestItem.AddressA, 1);
        ValueHash256 originalLeafValue = new(Value(2));
        PbtNodePath originalNodePath = new([0x80], 4);
        byte[] originalNode = EncodeGroup(originalNodePath, [new PbtNodeRecord(PbtFourLevelGroupGeometry.PathOf(originalNodePath, 0).ToPath<PbtStorageNodePath>(), BranchEncoding(1))]);
        Reader reader = new(new PbtStorageTreeKey([0]), null) { GroupReadException = new InvalidDataException("Configured group read failure.") };
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), reader), pool, PbtResourcePool.Usage.MainBlockProcessing);
        bundle.SetSlot(TestItem.AddressA, 1, EvmWordSlot.FromStripped(originalLeafValue.Bytes));
        using RefCountingMemory originalPayload = Memory(originalNode);
        bundle.SetNodeGroup(originalNodePath, TestItem.KeccakA.ValueHash256, originalPayload);
        using PbtWriteBatchBuilder<PbtStorageTreeKey> changes = new(0);
        changes.Set(new PbtStorageTreeKey([3]), new ValueHash256(Value(4)));

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
            Assert.That(snapshot.Content.GetSlot(originalLeafKey), Is.EqualTo(EvmWordSlot.FromStripped(originalLeafValue.Bytes)));
            Assert.That(snapshot.Content.NodeGroups, Has.Count.EqualTo(1));
            Assert.That(foundGroup, Is.True);
            Assert.That(group!.GetSpan().ToArray(), Is.EqualTo(originalNode));
        }
    }

    [Test]
    public void CollectedSnapshot_ContainsCanonicalWritesAndRoot()
    {
        PbtStorageTreeKey key = PbtStateKey.Storage(TestItem.AddressA, 1);
        ValueHash256 value = new(Value(2));
        ValueHash256 root = new(Value(3));
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(new PbtStorageTreeKey([0]), null)), pool, PbtResourcePool.Usage.MainBlockProcessing);
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
            Assert.That(snapshot.Content.GetSlot(key), Is.EqualTo(EvmWordSlot.FromStripped(value.Bytes)));
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
            Assert.That(newer.Content.GetSlot(PbtStateKey.Storage(TestItem.AddressA, slot)), Is.EqualTo(expected));
            Assert.That(older.Content.GetSlot(PbtStateKey.Storage(TestItem.AddressA, slot)), Is.EqualTo(original));
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
        }
    }

    [Test]
    public void Shared_code_is_reapplied_for_each_holder_across_folds([Values] bool reverseOrder, [Values(1, 129, 258)] int chunkCount)
    {
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0),
            new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(default, null)), pool, PbtResourcePool.Usage.MainBlockProcessing);
        byte[] bytes = new byte[chunkCount * 31];
        bytes.AsSpan().Fill(0x5b);
        CodeInfo code = new(bytes);
        Account account = Build.An.Account.WithCode(bytes).TestObject;
        if (!reverseOrder) bundle.SetCode(account.CodeHash.ValueHash256, code);
        bundle.SetAccount(TestItem.AddressA, account);
        if (reverseOrder) bundle.SetCode(account.CodeHash.ValueHash256, code);
        ValueHash256 root = Fold(bundle, default);
        using PbtSnapshot original = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), root);
        bundle.SetAccount(TestItem.AddressB, account);
        root = Fold(bundle, root);
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
    private static readonly byte[] OtherDelegation = Bytes.FromHexString("ef01000000000000000000000000000000000000000002");

    public static IEnumerable<TestCaseData> CodeHashTransitionCases()
    {
        yield return new TestCaseData(RealCode, OtherRealCode, typeof(InvalidOperationException)).SetName("Real_to_other_real_throws");
        yield return new TestCaseData(RealCode, null, typeof(InvalidOperationException)).SetName("Real_to_deleted_throws");
        yield return new TestCaseData(RealCode, Delegation, typeof(InvalidOperationException)).SetName("Real_to_delegation_throws");
        yield return new TestCaseData(RealCode, RealCode, null).SetName("Real_to_same_real_is_allowed");
        yield return new TestCaseData(Array.Empty<byte>(), RealCode, null).SetName("Empty_to_real_is_allowed");
        yield return new TestCaseData(Delegation, OtherDelegation, null).SetName("Delegation_to_other_delegation_is_allowed");
        yield return new TestCaseData(Delegation, Array.Empty<byte>(), null).SetName("Delegation_to_empty_is_allowed");
        yield return new TestCaseData(Delegation, null, null).SetName("Delegation_to_deleted_is_allowed");
    }

    [TestCaseSource(nameof(CodeHashTransitionCases))]
    public void Code_hash_transitions_without_a_reference_count(byte[] previousCode, byte[]? nextCode, Type? expectedException)
    {
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0),
            new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(default, null)), pool, PbtResourcePool.Usage.MainBlockProcessing);
        Account previous = Build.An.Account.WithNonce(1).WithCode(previousCode).TestObject;
        Account? next = nextCode is null ? null : Build.An.Account.WithNonce(2).WithCode(nextCode).TestObject;
        Set(previous, previousCode);
        ValueHash256 root = Fold(bundle, default);
        if (expectedException is not null)
        {
            Assert.Throws(expectedException, () => Set(next, nextCode));
            return;
        }
        Set(next, nextCode);
        Dictionary<string, byte[]> model = [];
        if (next is not null) PbtReferenceModel.SetAccount(model, TestItem.AddressA, next.Nonce, next.Balance, nextCode);
        Assert.That(Fold(bundle, root), Is.EqualTo(PbtReferenceModel.Root(model)));

        void Set(Account? account, byte[]? code)
        {
            if (code is { Length: > 0 }) bundle.SetCode(account!.CodeHash.ValueHash256, new CodeInfo(code));
            bundle.SetAccount(TestItem.AddressA, account);
        }
    }

    [Test]
    public void Replacing_unknown_previous_code_throws()
    {
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0),
            new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(default, null)), pool, PbtResourcePool.Usage.MainBlockProcessing);
        bundle.SetAccount(TestItem.AddressA, Build.An.Account.WithCode(RealCode).TestObject);
        Assert.Throws<InvalidDataException>(() => bundle.SetAccount(TestItem.AddressA, Build.An.Account.WithCode(OtherRealCode).TestObject));
    }

    [Test]
    public void Delegation_header_matches_eip_preimages()
    {
        byte[] code = Bytes.FromHexString("ef01000000000000000000000000000000000000000001");
        Account account = Build.An.Account.WithCode(code).TestObject;
        ValueHash256 addressHash = PbtKeyDerivation.AddressKeyHash(TestItem.AddressA);
        Dictionary<PbtPath, ValueHash256> expected = new()
        {
            [new PbtPath([0, .. addressHash.Bytes, 0])] = new(Bytes.FromHexString("0000000000000017000000000000000000000000000000000000000000000000")),
            [new PbtPath([0, .. addressHash.Bytes, 2])] = new([.. code, .. new byte[9]]),
        };
        Assert.That(PbtFlatState.AccountLeaves(addressHash, account, new CodeInfo(code)), Is.EquivalentTo(expected));
    }

    [Test]
    public void Delegation_transitions_match_reference_leaves([Values] bool codeFirst, [Values] bool foldEachChange)
    {
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0),
            new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(default, null)), pool, PbtResourcePool.Usage.MainBlockProcessing);
        byte[] firstDelegation = Bytes.FromHexString("ef01000000000000000000000000000000000000000001");
        byte[] secondDelegation = Bytes.FromHexString("ef01000000000000000000000000000000000000000002");
        ValueHash256 root = default;
        Dictionary<Address, byte[]> accounts = [];
        Set(TestItem.AddressA, firstDelegation);
        Set(TestItem.AddressB, firstDelegation);
        Set(TestItem.AddressA, secondDelegation);
        Set(TestItem.AddressA, []);
        Set(TestItem.AddressA, firstDelegation);
        Set(TestItem.AddressA, null);
        Set(TestItem.AddressB, null);
        Assert.That(Fold(bundle, root), Is.EqualTo(default(ValueHash256)));

        void Set(Address address, byte[]? bytes)
        {
            Account? account = bytes is null ? null : Build.An.Account.WithNonce(1).WithCode(bytes).TestObject;
            if (codeFirst && bytes is { Length: > 0 }) bundle.SetCode(account!.CodeHash.ValueHash256, new CodeInfo(bytes));
            bundle.SetAccount(address, account);
            if (!codeFirst && bytes is { Length: > 0 }) bundle.SetCode(account!.CodeHash.ValueHash256, new CodeInfo(bytes));
            if (bytes is null) accounts.Remove(address);
            else accounts[address] = bytes;
            Dictionary<string, byte[]> model = [];
            foreach ((Address owner, byte[] ownerCode) in accounts) PbtReferenceModel.SetAccount(model, owner, 1, 0, ownerCode);
            if (foldEachChange)
            {
                root = Fold(bundle, root);
                Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)), "incremental root");
            }
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
            Assert.That(bundle.PendingMutationCount, Is.Zero);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Late_code_lookup_resolves_pending_mutations(bool failFirstFold)
    {
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0),
            new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(default, null)), pool, PbtResourcePool.Usage.MainBlockProcessing);
        byte[] bytes = Bytes.FromHexString("6001600055");
        Account account = Build.An.Account.WithCode(bytes).TestObject;
        bundle.SetAccount(TestItem.AddressA, account);
        if (failFirstFold) Assert.Throws<InvalidDataException>(() => Fold(bundle, default));
        bundle.ReadCode = hash => hash == account.CodeHash.ValueHash256 ? bytes : null;
        if (!failFirstFold) Assert.That(bundle.GetCode(account.CodeHash.ValueHash256), Is.Not.Null);
        ValueHash256 root = Fold(bundle, default);
        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, account.Nonce, account.Balance, bytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)));
            Assert.That(bundle.PendingMutationCount, Is.Zero);
        }
    }

    [Test]
    public void Persisted_code_is_read_once_per_bundle_and_never_snapshotted()
    {
        PbtResourcePool pool = new(new PbtConfig());
        Reader reader = new(default, null);
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0),
            new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), reader), pool, PbtResourcePool.Usage.MainBlockProcessing);
        byte[] bytes = Bytes.FromHexString("6001600055");
        Account account = Build.An.Account.WithBalance(1).WithCode(bytes).TestObject;
        ValueHash256 codeHash = account.CodeHash.ValueHash256;
        CodeInfo persisted = new(bytes);
        reader.Codes[codeHash] = persisted;
        bundle.SetAccount(TestItem.AddressA, account);
        ValueHash256 root = Fold(bundle, default);
        using PbtSnapshot first = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), root);
        Account updated = account.WithChangedBalance(2);
        bundle.SetAccount(TestItem.AddressA, updated);
        root = Fold(bundle, root);
        using PbtSnapshot second = bundle.CollectSnapshot(new StateId(1, default), new StateId(2, default), root);
        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, updated.Nonce, updated.Balance, bytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)));
            Assert.That(reader.CodeReadCount, Is.EqualTo(1), "persisted code is memoized per bundle");
            Assert.That(bundle.GetCode(codeHash), Is.SameAs(persisted));
            Assert.That(first.Content.Codes, Is.Empty, "memoized code must not be snapshotted");
            Assert.That(second.Content.Codes, Is.Empty, "memoized code must not be snapshotted");
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
        // A balance change stages no code chunk, so a new contract keeps the code partition in the batch.
        byte[] otherBytes = Bytes.FromHexString("6002600055");
        Account other = Build.An.Account.WithCode(otherBytes).TestObject;
        bundle.SetCode(other.CodeHash.ValueHash256, new CodeInfo(otherBytes));
        bundle.SetAccount(TestItem.AddressB, other);
        CountingStore store = new(bundle) { FailedZone = failedZone };
        Assert.Throws<AggregateException>(() => TrieUpdater.UpdateRoot(store, root, bundle.PrepareLeafChanges(), PbtTreeHarness.FoldQuota(), FoldFanOut.Default, true, null));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.TreeRoot, Is.EqualTo(root));
            Assert.That(bundle.GetAccount(TestItem.AddressA), Is.SameAs(replacement));
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
            ValueHash256 updated = TrieUpdater.UpdateRoot(new PbtSnapshotStore(bundle), root, changes, PbtTreeHarness.FoldQuota(), FoldFanOut.Default, true, null);
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
        PbtNodeGroupCodec.Encode(ref writer, groupKey, records, default);
        return writer.WrittenSpan.ToArray();
    }

    private static void AssertSnapshotUnchanged(PbtSnapshotBundle bundle, in PbtStorageTreeKey leafKey, PbtNodePath nodePath, byte[] node)
    {
        bundle.CompleteLeafChanges();
        using PbtSnapshot snapshot = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), default);
        bool foundGroup = snapshot.Content.TryGetNodeGroup(nodePath, out RefCountingMemory? actual);
        using RefCountingMemory? actualLease = actual;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.Content.Storages, Has.Count.EqualTo(1));
            Assert.That(snapshot.Content.TryGetSlot(leafKey, out _), Is.True);
            Assert.That(snapshot.Content.NodeGroups, Has.Count.EqualTo(1));
            Assert.That(foundGroup, Is.True);
            Assert.That(actual!.GetSpan().ToArray(), Is.EqualTo(node));
        }
    }

    private sealed class Reader(PbtStorageTreeKey key, ValueHash256? value) : IPbtPersistence.IReader
    {
        public IEnumerable<KeyValuePair<ValueHash256, Account>> Accounts { get; init; } = [];
        public IEnumerable<KeyValuePair<PbtStorageTreeKey, EvmWord>> Storage { get; init; } = [];
        public bool ThrowAfterFirst { get; init; }
        public int IteratorDisposals { get; private set; }
        private IEnumerator<T> Track<T>(IEnumerable<T> values)
        {
            try
            {
                foreach (T item in values)
                {
                    yield return item;
                    if (ThrowAfterFirst) throw new InvalidOperationException("Enumeration must not read ahead.");
                }
            }
            finally
            {
                IteratorDisposals++;
            }
        }
        public PbtNodePath GroupKey { get; set; } = new([], 0);
        public byte[]? GroupPayload { get; set; }
        public IRefCountingMemoryProvider MemoryProvider { get; set; } = PooledRefCountingMemoryProvider.Instance;
        public Exception? GroupReadException { get; set; }
        public int GroupReadCount { get; private set; }
        public Dictionary<ValueHash256, CodeInfo> Codes { get; } = [];
        public int CodeReadCount { get; private set; }
        public StateId CurrentState => StateId.PreGenesis;
        public ValueHash256 CurrentRoot { get; set; }
        public Account? GetAccount(in ValueHash256 addressHash) => null;
        public ISlotRun GetSlotRun(in PbtStorageTreeKey runKey)
        {
            PbtStorageTreeKey wanted = runKey;
            ISlotRun run = SlotRun.Empty;
            foreach ((PbtStorageTreeKey slotKey, EvmWord word) in EnumerateStorageCore(null))
            {
                if (SlotRun.RunKey(slotKey) != wanted) continue;
                ISlotRun previous = run;
                run = run.With(SlotRun.IndexOf(slotKey), word);
                SlotRun.Return(previous);
            }
            return run;
        }
        public CodeInfo? GetCode(in ValueHash256 codeHash)
        {
            CodeReadCount++;
            return Codes.GetValueOrDefault(codeHash);
        }
        public IPbtIterator<KeyValuePair<ValueHash256, Account>> EnumerateAccounts() => new PbtIterator<KeyValuePair<ValueHash256, Account>>(Track(Accounts));
        public IPbtIterator<KeyValuePair<PbtStorageTreeKey, EvmWord>> EnumerateStorage(ValueHash256? addressHash = null) =>
            new PbtIterator<KeyValuePair<PbtStorageTreeKey, EvmWord>>(Track(EnumerateStorageCore(addressHash)));

        private IEnumerable<KeyValuePair<PbtStorageTreeKey, EvmWord>> EnumerateStorageCore(ValueHash256? addressHash)
        {
            foreach (KeyValuePair<PbtStorageTreeKey, EvmWord> slot in Storage)
                if (addressHash is null || PbtFlatState.StorageAddress(slot.Key) == addressHash.Value) yield return slot;
            if (value is { } word && (addressHash is null || PbtFlatState.StorageAddress(key) == addressHash.Value))
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
        public IPbtIterator<PbtStorageNodePath> EnumerateNodeGroupKeys() => new PbtIterator<PbtStorageNodePath>(((IEnumerable<PbtStorageNodePath>)[]).GetEnumerator());
        public void Dispose() { }
    }

    private sealed class CountingStore(PbtSnapshotBundle bundle) : IPbtStore
    {
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
