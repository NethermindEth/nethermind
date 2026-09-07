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
        new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(new PbtFullKey([0]), null)),
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
        public PbtWriteBatchBuilder GetWriteBatchBuilder(PbtResourcePool.Usage usage) => new();
        public void ReturnWriteBatchBuilder(PbtResourcePool.Usage usage, PbtWriteBatchBuilder builder)
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
        PbtFullKey key = PbtStateKey.Storage(TestItem.AddressA, 1);
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
        PbtFullKey matching = PbtStateKey.Storage(TestItem.AddressA, 1000);
        PbtFullKey other = PbtStateKey.Storage(TestItem.AddressB, 1000);
        PbtFullKey prefix = PbtStateKey.StoragePrefix(TestItem.AddressA);
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
        List<PbtFullKey> expected = filtered ? [matching] : [matching, other];
        expected.Sort();
        List<PbtFullKey> sharedKeys = [];
        foreach (KeyValuePair<PbtFullKey, ValueHash256> leaf in filtered ? readOnly.EnumerateLeaves(prefix) : readOnly.EnumerateLeaves())
            sharedKeys.Add(leaf.Key);
        bundle.SetSlot(TestItem.AddressA, 1000, EvmWordSlot.FromStripped(Value(3)));
        List<PbtFullKey> visibleKeys = [];
        foreach (KeyValuePair<PbtFullKey, ValueHash256> leaf in filtered ? bundle.EnumerateLeaves(prefix) : bundle.EnumerateLeaves())
            visibleKeys.Add(leaf.Key);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sharedKeys, Is.EqualTo(expected));
            Assert.That(visibleKeys, Is.EqualTo(expected));
        }
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void Trie_updates_leave_independently_staged_flat_entries_unchanged(bool leafExists, bool delete)
    {
        PbtFullKey key = PbtStateKey.Storage(TestItem.AddressA, 1);
        ValueHash256 flatValue = new(Value(9));
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0),
            new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(key, null)), pool, PbtResourcePool.Usage.MainBlockProcessing);
        PbtSnapshotStore store = new(bundle);
        PbtWriteBatch initial = new();
        if (leafExists) initial.Set(key, new ValueHash256(Value(1)));
        ValueHash256 root = TrieUpdater.UpdateRoot(store, default, initial);
        bundle.SetSlot(TestItem.AddressA, 1, EvmWordSlot.FromStripped(flatValue.Bytes));

        PbtWriteBatch changes = new();
        if (delete) changes.Delete(key);
        else changes.Set(key, new ValueHash256(Value(2)));
        ValueHash256 updatedRoot = TrieUpdater.UpdateRoot(store, root, changes);

        byte[] expectedLeaf = PbtNodeCodec.EncodeLeaf(key, Value(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.GetSlot(TestItem.AddressA, 1), Is.EqualTo(EvmWordSlot.FromStripped(flatValue.Bytes)));
            Assert.That(bundle.PendingLeafMutations(), Is.EquivalentTo(new[] { new KeyValuePair<PbtFullKey, ValueHash256?>(key, flatValue) }));
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
            new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(new PbtFullKey([0]), null)), pool, PbtResourcePool.Usage.MainBlockProcessing);
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

    [Test]
    public void Node_group_read_rejects_non_boundary_key_before_empty_persistence_lookup()
    {
        Reader reader = new(new PbtFullKey([0]), null);
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
        byte[] persisted = EncodeGroup(groupKey, [new PbtNodeRecord(groupKey, BranchEncoding(1)),
            new PbtNodeRecord(PbtFourLevelGroupGeometry.PathOf(groupKey, 14), BranchEncoding(2))]);
        byte[] shared = EncodeGroup(groupKey, [new PbtNodeRecord(groupKey, BranchEncoding(3))]);
        byte[] local = EncodeGroup(groupKey, [new PbtNodeRecord(groupKey, BranchEncoding(4))]);
        byte[] write = EncodeGroup(groupKey, [new PbtNodeRecord(groupKey, BranchEncoding(5))]);
        Reader reader = new(new PbtFullKey([0]), null) { GroupPayload = persisted };
        PbtResourcePool pool = new(new PbtConfig());
        PbtSnapshotPooledList sharedSnapshots = newestTier >= 1
            ? Snapshots(pool, Content(groupKey, persisted), Content(groupKey, newestTier == 1 && tombstone ? null : shared))
            : new(0);
        PbtSnapshotPooledList localSnapshots = newestTier >= 2
            ? Snapshots(pool, Content(groupKey, shared), Content(groupKey, newestTier == 2 && tombstone ? null : local))
            : new(0);
        using PbtSnapshotBundle bundle = new(localSnapshots, new PbtReadOnlySnapshotBundle(sharedSnapshots, reader), pool, PbtResourcePool.Usage.MainBlockProcessing);
        if (newestTier == 3)
        {
            using RefCountingMemory? payload = tombstone ? null : Memory(write);
            bundle.SetNodeGroup(groupKey, payload);
        }

        using RefCountingMemory? actual = bundle.GetNodeGroup(groupKey);
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
        byte[] malformed = invalidFooter ? new byte[PbtNodeGroupCodec.TrailerLength] : Bytes.FromHexString("01");
        if (invalidFooter) malformed[^1] = 0x80;
        Reader reader = new(new PbtFullKey([0]), null)
        {
            GroupPayload = malformed,
            MemoryProvider = memoryProvider,
        };
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), reader), pool, PbtResourcePool.Usage.MainBlockProcessing);
        PbtFullKey originalLeafKey = PbtStateKey.Storage(TestItem.AddressA, 1);
        PbtNodePath originalGroupKey = new([0x80], 4);
        byte[] originalGroup = EncodeGroup(originalGroupKey, [new PbtNodeRecord(PbtFourLevelGroupGeometry.PathOf(originalGroupKey, 0), BranchEncoding(1))]);
        using RefCountingMemory originalPayload = Memory(originalGroup);
        bundle.SetSlot(TestItem.AddressA, 1, EvmWordSlot.FromStripped(Value(2)));
        bundle.SetNodeGroup(originalGroupKey, originalPayload);
        PbtWriteBatch changes = new();
        changes.Set(new PbtFullKey([3]), new ValueHash256(Value(4)));

        Assert.Throws<InvalidDataException>(() => TrieUpdater.UpdateRoot(new PbtSnapshotStore(bundle), new ValueHash256(Value(5)), changes));
        AssertSnapshotUnchanged(bundle, originalLeafKey, originalGroupKey, originalGroup);
        Assert.That(TrackingMemoryProvider.CountUnreleased(memoryProvider.Rented), Is.Zero);
    }

    [Test]
    public void Malformed_group_replacement_is_rejected_without_mutating_snapshot()
    {
        PbtResourcePool pool = new(new PbtConfig());
        Reader reader = new(new PbtFullKey([0]), null);
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), reader), pool, PbtResourcePool.Usage.MainBlockProcessing);
        PbtFullKey leafKey = PbtStateKey.Storage(TestItem.AddressA, 1);
        PbtNodePath groupKey = new([], 0);
        byte[] original = EncodeGroup(groupKey, [new PbtNodeRecord(groupKey, BranchEncoding(1))]);
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
        PbtFullKey originalLeafKey = PbtStateKey.Storage(TestItem.AddressA, 1);
        ValueHash256 originalLeafValue = new(Value(2));
        PbtNodePath originalNodePath = new([0x80], 4);
        byte[] originalNode = EncodeGroup(originalNodePath, [new PbtNodeRecord(PbtFourLevelGroupGeometry.PathOf(originalNodePath, 0), BranchEncoding(1))]);
        Reader reader = new(new PbtFullKey([0]), null) { GroupReadException = new InvalidDataException("Configured group read failure.") };
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), reader), pool, PbtResourcePool.Usage.MainBlockProcessing);
        bundle.SetSlot(TestItem.AddressA, 1, EvmWordSlot.FromStripped(originalLeafValue.Bytes));
        using RefCountingMemory originalPayload = Memory(originalNode);
        bundle.SetNodeGroup(originalNodePath, originalPayload);
        PbtWriteBatch changes = new();
        changes.Set(new PbtFullKey([3]), new ValueHash256(Value(4)));

        CountingStore store = new(bundle);
        Assert.Throws<InvalidDataException>(() => TrieUpdater.UpdateRoot(store, new ValueHash256(Value(5)), changes));

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
        PbtFullKey key = PbtStateKey.Storage(TestItem.AddressA, 1);
        ValueHash256 value = new(Value(2));
        ValueHash256 root = new(Value(3));
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0), new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(new PbtFullKey([0]), null)), pool, PbtResourcePool.Usage.MainBlockProcessing);
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
            Assert.That(bundle.GetCodeReference(account.CodeHash.ValueHash256), Is.EqualTo(1));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Shared_overflow_code_survives_account_replacement_and_last_reference_removal(bool reverseOrder)
    {
        PbtResourcePool pool = new(new PbtConfig());
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0),
            new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), new Reader(default, null)), pool, PbtResourcePool.Usage.MainBlockProcessing);
        byte[] bytes = new byte[(PbtKeyDerivation.HeaderCodeChunks + 2) * 31];
        bytes.AsSpan().Fill(0x5b);
        CodeInfo code = new(bytes);
        Account account = Build.An.Account.WithCode(bytes).TestObject;
        bundle.SetCode(account.CodeHash.ValueHash256, code);
        bundle.SetAccount(TestItem.AddressA, account);
        bundle.SetAccount(TestItem.AddressB, account);
        ValueHash256 root = Fold(bundle, default);
        using PbtSnapshot original = bundle.CollectSnapshot(StateId.PreGenesis, new StateId(1, default), root);
        Account replacement = Build.An.Account.WithNonce(3).TestObject;
        if (reverseOrder) bundle.SetAccount(TestItem.AddressB, account);
        bundle.SetAccount(TestItem.AddressA, replacement);
        if (!reverseOrder) bundle.SetAccount(TestItem.AddressB, account);
        root = Fold(bundle, root);
        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, replacement.Nonce, replacement.Balance);
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
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, replacement.Nonce, replacement.Balance);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)));
            Assert.That(bundle.GetCodeReference(account.CodeHash.ValueHash256), Is.Zero);
            Assert.That(bundle.GetCode(account.CodeHash.ValueHash256), Is.SameAs(code));
        }
    }

    private static ValueHash256 Fold(PbtSnapshotBundle bundle, ValueHash256 root)
    {
        ValueHash256 updated = TrieUpdater.UpdateRoot(new PbtSnapshotStore(bundle), root, bundle.PrepareLeafChanges());
        bundle.CompleteLeafChanges();
        return updated;
    }

    private static PbtSnapshotPooledList Snapshots(PbtResourcePool pool, params PbtSnapshotContent[] contents)
    {
        PbtSnapshotPooledList snapshots = new(contents.Length);
        for (int index = 0; index < contents.Length; index++)
            snapshots.Add(new PbtSnapshot(StateId.PreGenesis, new StateId((ulong)index, default), default, contents[index], pool, PbtResourcePool.Usage.MainBlockProcessing));
        return snapshots;
    }

    private static PbtSnapshotContent Content(PbtNodePath groupKey, byte[]? encoding)
    {
        PbtSnapshotContent content = new();
        using RefCountingMemory? payload = encoding is null ? null : Memory(encoding);
        content.SetNodeGroup(groupKey, payload);
        return content;
    }

    private static RefCountingMemory Memory(byte[] encoding)
    {
        RefCountingMemory payload = PooledRefCountingMemoryProvider.Instance.Rent(encoding.Length);
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
        int length = PbtNodeGroupCodec.TrailerLength;
        foreach (PbtNodeRecord record in records) length += record.Encoding.Length;
        byte[] payload = new byte[length];
        BufferWriter writer = new(payload);
        PbtNodeGroupCodec.Encode(ref writer, groupKey, records);
        return writer.WrittenSpan.ToArray();
    }

    private static void AssertSnapshotUnchanged(PbtSnapshotBundle bundle, PbtFullKey leafKey, PbtNodePath nodePath, byte[] node)
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

    private sealed class Reader(PbtFullKey key, ValueHash256? value) : IPbtPersistence.IReader
    {
        public PbtNodePath GroupKey { get; set; } = new([], 0);
        public byte[]? GroupPayload { get; set; }
        public IRefCountingMemoryProvider MemoryProvider { get; set; } = PooledRefCountingMemoryProvider.Instance;
        public Exception? GroupReadException { get; set; }
        public int GroupReadCount { get; private set; }
        public StateId CurrentState => StateId.PreGenesis;
        public ValueHash256 CurrentRoot => default;
        public Account? GetAccount(in ValueHash256 addressHash) => null;
        public EvmWord GetSlot(PbtFullKey requested) => requested == key && value is { } word ? EvmWordSlot.FromStripped(word.Bytes) : default;
        public CodeInfo? GetCode(in ValueHash256 codeHash) => null;
        public IEnumerable<KeyValuePair<ValueHash256, Account>> EnumerateAccounts() => [];
        public IEnumerable<KeyValuePair<PbtFullKey, EvmWord>> EnumerateStorage(PbtFullKey? prefix = null)
        {
            if (value is { } word && (prefix is null || prefix.Value.IsPrefixOf(key)))
                yield return new(key, EvmWordSlot.FromStripped(word.Bytes));
        }
        public RefCountingMemory? GetNodeGroup(PbtNodePath groupKey)
        {
            GroupReadCount++;
            if (GroupReadException is not null) throw GroupReadException;
            if (!groupKey.Equals(GroupKey) || GroupPayload is null) return null;
            RefCountingMemory memory = MemoryProvider.Rent(GroupPayload.Length);
            GroupPayload.CopyTo(memory.GetSpan());
            return memory;
        }
        public IEnumerable<PbtNodePath> EnumerateNodeGroupKeys() => [];
        public ulong GetCodeReference(in ValueHash256 codeHash) => 0;
        public void Dispose() { }
    }

    private sealed class CountingStore(PbtSnapshotBundle bundle) : IPbtStore
    {
        public int ApplyCount { get; private set; }
        public RefCountingMemory? GetNodeGroup(PbtNodePath groupKey) => bundle.GetNodeGroup(groupKey);
        public void SetNodeGroup(PbtNodePath groupKey, RefCountingMemory? payload)
        {
            ApplyCount++;
            bundle.SetNodeGroup(groupKey, payload);
        }
    }
}
