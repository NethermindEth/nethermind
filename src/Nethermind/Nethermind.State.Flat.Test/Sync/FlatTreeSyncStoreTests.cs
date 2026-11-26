// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.State.Flat.Sync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Sync;

[TestFixture]
public class FlatTreeSyncStoreTests
{
    private SnapshotableMemColumnsDb<FlatDbColumns> _columnsDb = null!;
    private IPersistence _persistence = null!;

    [SetUp]
    public void SetUp()
    {
        _columnsDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
        // WriteStorageDirectToDb seeds the Storage column with raw (un-wrapped) bytes after the persistence
        // is built, so slot-presence detection can't kick in — pin the raw encoding up front.
        BasePersistence.SetSlotEncoding(_columnsDb.GetColumnDb(FlatDbColumns.Metadata), BasePersistence.SlotEncodingRaw);
        _persistence = new RocksDbPersistence(_columnsDb, LimboLogs.Instance, NullTrieNodeLog.Instance);
    }

    [TearDown]
    public void TearDown() => _columnsDb.Dispose();

    private static FlatTestContainer CreateContainer(IPersistence persistence, IHistoryPivotSeeder? seeder = null) =>
        new(loadPersistedTier: false, configure: builder =>
        {
            builder
                .AddSingleton<IPersistence>(persistence)
                .AddSingleton<IPersistenceManager>(Substitute.For<IPersistenceManager>());
            if (seeder is not null) builder.AddSingleton<IHistoryPivotSeeder>(seeder);
        });

    private void WriteStorageDirectToDb(Address address, UInt256 slot, byte[] value)
    {
        Span<byte> slotBytes = stackalloc byte[32];
        slot.ToBigEndian(slotBytes);
        WriteStorageDirectToDb(ValueKeccak.Compute(address.Bytes), ValueKeccak.Compute(slotBytes), value);
    }

    private void WriteStorageDirectToDb(in ValueHash256 addrHash, in ValueHash256 slotHash, byte[] value)
    {
        IDb storageDb = _columnsDb.GetColumnDb(FlatDbColumns.Storage);
        byte[] storageKey = new byte[52];
        addrHash.Bytes[..4].CopyTo(storageKey.AsSpan()[..4]);
        slotHash.Bytes.CopyTo(storageKey.AsSpan()[4..36]);
        addrHash.Bytes[4..20].CopyTo(storageKey.AsSpan()[36..52]);

        storageDb.PutSpan(storageKey, ((ReadOnlySpan<byte>)value).WithoutLeadingZeros());
    }

    private bool HasStorageEntries(Address address)
    {
        IDb storageDb = _columnsDb.GetColumnDb(FlatDbColumns.Storage);
        ValueHash256 addrHash = ValueKeccak.Compute(address.Bytes);
        byte[] prefix = addrHash.Bytes[..4].ToArray();

        foreach (byte[] key in storageDb.GetAllKeys())
        {
            if (key.AsSpan()[..4].SequenceEqual(prefix))
                return true;
        }
        return false;
    }

    [Test]
    public void EnsureStorageEmpty_deletes_all_storage_entries()
    {
        Address address = TestItem.AddressA;
        WriteStorageDirectToDb(address, 0, [0x01, 0x15, 0xe8]);
        WriteStorageDirectToDb(address, 1, [0xab, 0xcd]);
        WriteStorageDirectToDb(address, 42, [0xff]);

        Assert.That(HasStorageEntries(address), Is.True, "Storage entries should exist before cleanup");

        using FlatTestContainer container = CreateContainer(_persistence);
        FlatTreeSyncStore store = container.Resolve<FlatTreeSyncStore>();
        store.EnsureStorageEmpty(Keccak.Compute(address.Bytes));

        Assert.That(HasStorageEntries(address), Is.False, "Storage entries should be deleted after EnsureStorageEmpty");
    }

    /// <summary>
    /// Healing finds a storage trie node stored at a path with the hash it expects saved with its subtree, so the nodes
    /// have to go with the slots, whether or not the root is among them, in both columns, and for that account only.
    /// </summary>
    [Test]
    public void EnsureStorageEmpty_deletes_the_storage_trie_nodes_of_that_account_only([Values] bool withRootNode)
    {
        // The second account shares the first four bytes of the cleared one, which storage keys start with.
        Hash256 cleared = new("0x1234567811111111111111111111111111111111111111111111111111111111");
        Hash256 samePrefix = new("0x1234567822222222222222222222222222222222222222222222222222222222");
        Hash256 other = TestItem.KeccakB;
        Hash256[] accounts = [cleared, samePrefix, other];
        ValueHash256 slot = TestItem.KeccakC;
        // The deepest of these is past the paths the storage node column holds.
        TreePath[] nodePaths = [TreePath.Empty, TreePath.FromHexString("1"), TreePath.FromHexString("f"), TreePath.FromHexString("fffffffffffffff"), TreePath.FromHexString("1111111111111111")];
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL))
        {
            foreach (Hash256 account in accounts)
            {
                foreach (TreePath nodePath in nodePaths)
                {
                    if (withRootNode || account != cleared || nodePath.Length > 0) batch.SetStorageTrieNode(account, nodePath, StorageNodeRlp);
                }
            }
        }

        foreach (Hash256 account in accounts) WriteStorageDirectToDb(account, slot, [0x01, 0x02]);

        using FlatTestContainer container = CreateContainer(_persistence);
        container.Resolve<FlatTreeSyncStore>().EnsureStorageEmpty(cleared);

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            foreach (Hash256 account in accounts)
            {
                bool kept = account != cleared;
                UInt256 value = default;
                Assert.That(reader.TryGetStorageRaw(account, slot, ref value), Is.EqualTo(kept), $"slot of {account}");
                foreach (TreePath nodePath in nodePaths)
                {
                    Assert.That(reader.LoadStorageRlp(account, nodePath, ReadFlags.None), kept ? Is.EqualTo(StorageNodeRlp) : Is.Null, $"storage node {nodePath} of {account}");
                }
            }
        }
    }

    [Test]
    public void SaveNode_deletes_the_storage_of_accounts_the_new_state_node_no_longer_covers()
    {
        Address removed = TestItem.AddressA;
        Address kept = TestItem.AddressB;
        using FlatTestContainer container = CreateContainer(_persistence);
        FlatTreeSyncStore store = container.Resolve<FlatTreeSyncStore>();
        SaveRootAccountLeaf(store, removed);
        WriteStorageDirectToDb(removed, 1, [0x01]);
        WriteStorageDirectToDb(kept, 1, [0x02]);

        SaveRootAccountLeaf(store, kept);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(HasStorageEntries(removed), Is.False);
            Assert.That(HasStorageEntries(kept), Is.True);
        }
    }

    [Test]
    public void SaveNode_keeps_the_storage_of_the_account_the_new_state_leaf_holds()
    {
        Address held = TestItem.AddressA;
        Address removed = TestItem.AddressB;
        using FlatTestContainer container = CreateContainer(_persistence);
        FlatTreeSyncStore store = container.Resolve<FlatTreeSyncStore>();
        SaveRootAccountLeaf(store, removed);
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL))
        {
            batch.SetAccountRaw(Keccak.Compute(held.Bytes), new AccountStruct(1, 1));
            foreach (Address address in (Address[])[held, removed])
            {
                foreach (TreePath nodePath in StorageNodePaths) batch.SetStorageTrieNode(Keccak.Compute(address.Bytes), nodePath, StorageNodeRlp);
            }
        }
        WriteStorageDirectToDb(held, 1, [0x01]);
        WriteStorageDirectToDb(removed, 1, [0x02]);

        SaveRootAccountLeaf(store, held);

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(HasStorageEntries(held), Is.True);
            Assert.That(HasStorageEntries(removed), Is.False);
            foreach (TreePath nodePath in StorageNodePaths)
            {
                Assert.That(reader.LoadStorageRlp(Keccak.Compute(held.Bytes), nodePath, ReadFlags.None), Is.EqualTo(StorageNodeRlp), $"storage node {nodePath} of the held account");
                Assert.That(reader.LoadStorageRlp(Keccak.Compute(removed.Bytes), nodePath, ReadFlags.None), Is.Null, $"storage node {nodePath} of the removed account");
            }
        }
    }

    [Test]
    public void SaveNode_deletes_storage_that_has_no_flat_account_entry()
    {
        Address orphaned = TestItem.AddressC;
        Address kept = TestItem.AddressB;
        using FlatTestContainer container = CreateContainer(_persistence);
        FlatTreeSyncStore store = container.Resolve<FlatTreeSyncStore>();
        SaveRootAccountLeaf(store, TestItem.AddressA);
        WriteStorageDirectToDb(orphaned, 1, [0x01]);
        WriteStorageDirectToDb(kept, 1, [0x02]);

        SaveRootAccountLeaf(store, kept);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(HasStorageEntries(orphaned), Is.False);
            Assert.That(HasStorageEntries(kept), Is.True);
        }
    }

    [Test]
    public void SaveNode_self_destructs_each_replaced_range_once_keeping_the_leaf_account()
    {
        Address kept = TestItem.AddressB;
        using (FlatTestContainer container = CreateContainer(_persistence))
        {
            SaveRootAccountLeaf(container.Resolve<FlatTreeSyncStore>(), TestItem.AddressA);
        }

        FakeWriteBatch batch = new();
        using FlatTestContainer recording = CreateContainer(new BatchRecordingPersistence(_persistence, batch));
        SaveRootAccountLeaf(recording.Resolve<FlatTreeSyncStore>(), kept);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch.SelfDestructRangeCalls, Is.Not.Empty);
            Assert.That(batch.SelfDestructRangeCalls, Has.Count.EqualTo(batch.DeleteAccountRangeCalls.Count));
            Assert.That(batch.SelfDestructRangeCalls.Select(static call => call.KeptAccount), Is.All.EqualTo(Keccak.Compute(kept.Bytes).ValueHash256));
        }
    }

    public enum SavedBeforeTheLeaf
    {
        Nothing,
        StorageOfTheLeafAccount,
        LeafOfTheLeafAccountAbove,
        StorageOfAnotherAccount,
        StateNodeBelowTheLeaf,
        LeafOfAnotherAccountAbove,
        LeafOfTheLeafAccountBelow,
    }

    [TestCase(SavedBeforeTheLeaf.Nothing, false)]
    [TestCase(SavedBeforeTheLeaf.StorageOfTheLeafAccount, false)]
    [TestCase(SavedBeforeTheLeaf.LeafOfTheLeafAccountAbove, false)]
    [TestCase(SavedBeforeTheLeaf.StorageOfAnotherAccount, true)]
    [TestCase(SavedBeforeTheLeaf.StateNodeBelowTheLeaf, true)]
    [TestCase(SavedBeforeTheLeaf.LeafOfAnotherAccountAbove, true)]
    [TestCase(SavedBeforeTheLeaf.LeafOfTheLeafAccountBelow, true)]
    public void SaveNode_compares_a_leaf_with_the_one_RemoveMismatchedNode_took_out_until_a_state_node_or_another_account_is_saved_under_it(SavedBeforeTheLeaf saved, bool selfDestructsTheRange)
    {
        // Both accounts are under the leaf's path "a".
        Hash256 held = new("0xa000000000000000000000000000000000000000000000000000000000000001");
        Hash256 other = new("0xa000000000000000000000000000000000000000000000000000000000000002");
        TreePath leafPath = TreePath.FromHexString("a");
        using (FlatTestContainer container = CreateContainer(_persistence))
        {
            SaveAccountLeaf(container.Resolve<FlatTreeSyncStore>(), held, depth: 1);
        }

        FakeWriteBatch batch = new();
        using FlatTestContainer recording = CreateContainer(new BatchRecordingPersistence(_persistence, batch));
        FlatTreeSyncStore store = recording.Resolve<FlatTreeSyncStore>();
        (_, byte[] newLeaf) = Leaf(held, depth: 1, Rlp.Encode(new Account(1, 2)));
        store.RemoveMismatchedNode(null, leafPath, ValueKeccak.Compute(newLeaf));
        // The recording batch keeps the removal from the db.
        using (IPersistence.IWriteBatch dbBatch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL))
        {
            dbBatch.SetStateTrieNode(leafPath, default);
        }

        switch (saved)
        {
            case SavedBeforeTheLeaf.StorageOfTheLeafAccount: SaveSlotLeaf(store, held); break;
            case SavedBeforeTheLeaf.LeafOfTheLeafAccountAbove: SaveAccountLeaf(store, held, depth: 0); break;
            case SavedBeforeTheLeaf.StorageOfAnotherAccount: SaveSlotLeaf(store, other); break;
            case SavedBeforeTheLeaf.StateNodeBelowTheLeaf: SaveAccountLeaf(store, other, depth: 2); break;
            case SavedBeforeTheLeaf.LeafOfAnotherAccountAbove: SaveAccountLeaf(store, other, depth: 0); break;
            case SavedBeforeTheLeaf.LeafOfTheLeafAccountBelow: SaveAccountLeaf(store, held, depth: 2); break;
        }
        batch.SelfDestructRangeCalls.Clear();

        store.SaveNode(null, leafPath, ValueKeccak.Compute(newLeaf), newLeaf);

        Assert.That(batch.SelfDestructRangeCalls, selfDestructsTheRange ? Is.Not.Empty : Is.Empty);
    }

    private static readonly byte[] StorageNodeRlp = [0xc2, 0x80, 0x80];
    private static readonly TreePath[] StorageNodePaths = [TreePath.FromHexString("1"), TreePath.FromHexString("1111111111111111")];

    private static void SaveRootAccountLeaf(FlatTreeSyncStore store, Address address) =>
        SaveAccountLeaf(store, Keccak.Compute(address.Bytes), depth: 0);

    private static void SaveAccountLeaf(FlatTreeSyncStore store, Hash256 account, int depth)
    {
        (TreePath path, byte[] rlp) = Leaf(account, depth, Rlp.Encode(new Account(1, 1)));
        store.SaveNode(null, path, ValueKeccak.Compute(rlp), rlp);
    }

    private static void SaveSlotLeaf(FlatTreeSyncStore store, Hash256 account)
    {
        (_, byte[] rlp) = Leaf(TestItem.KeccakA, depth: 0, Rlp.Encode(new byte[] { 0x01 }));
        store.SaveNode(account, TreePath.Empty, ValueKeccak.Compute(rlp), rlp);
    }

    /// <summary>The leaf of the key at the depth, and its path.</summary>
    private static (TreePath Path, byte[] Rlp) Leaf(Hash256 key, int depth, byte[] value)
    {
        byte[] nibbles = Nibbles.BytesToNibbleBytes(key.Bytes);
        TreePath path = TreePath.Empty.Append(nibbles.AsSpan(0, depth));
        TrieNode leaf = TrieNodeFactory.CreateLeaf(nibbles.AsSpan(depth), value);
        TreePath resolvePath = path;
        leaf.ResolveKey(NullTrieNodeResolver.Instance, ref resolvePath);
        return (path, leaf.FullRlp.ToArray()!);
    }

    [Test]
    public void FinalizeSync_flushes_data_before_advancing_the_state_pointer()
    {
        // #11457: the state pointer must advance only after the (DisableWAL) data is flushed.
        List<string> log = [];
        OrderRecordingPersistence spy = new(_persistence, log);
        using FlatTestContainer container = CreateContainer(spy);
        FlatTreeSyncStore store = container.Resolve<FlatTreeSyncStore>();
        BlockHeader pivot = Build.A.BlockHeader.WithNumber(123).WithStateRoot(TestItem.KeccakA).TestObject;

        store.FinalizeSync(pivot);

        int firstFlush = log.IndexOf("flush");
        int firstAdvance = log.IndexOf("advance-pointer");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstFlush, Is.GreaterThanOrEqualTo(0), "data must be flushed during finalize");
            Assert.That(firstAdvance, Is.GreaterThan(firstFlush), "state pointer must advance only after the data flush");
        }

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        Assert.That(reader.CurrentState.BlockNumber, Is.EqualTo(123), "state pointer should end at the pivot block");
    }

    [Test]
    public void FinalizeSync_seeds_the_history_pivot_with_the_pivot_block_and_root_before_advancing_the_state_pointer()
    {
        IHistoryPivotSeeder seeder = Substitute.For<IHistoryPivotSeeder>();
        List<string> log = [];
        seeder.When(s => s.SeedPivot(Arg.Any<ulong>(), Arg.Any<ValueHash256>()))
            .Do(_ => log.Add("seed-pivot"));
        OrderRecordingPersistence spy = new(_persistence, log);
        using FlatTestContainer container = CreateContainer(spy, seeder);
        FlatTreeSyncStore store = container.Resolve<FlatTreeSyncStore>();
        BlockHeader pivot = Build.A.BlockHeader.WithNumber(123).WithStateRoot(TestItem.KeccakA).TestObject;

        store.FinalizeSync(pivot);

        using (Assert.EnterMultipleScope())
        {
            seeder.Received(1).SeedPivot(123, TestItem.KeccakA);

            int seedCallIndex = log.IndexOf("seed-pivot");
            int firstAdvance = log.IndexOf("advance-pointer");
            Assert.That(seedCallIndex, Is.GreaterThanOrEqualTo(0), "the seeder must actually be called");
            Assert.That(firstAdvance, Is.GreaterThan(seedCallIndex),
                "the pivot must be seeded while the reader still sees exactly the pivot's state, before any block is processed on top of it");
        }
    }

    [Test]
    public void FinalizeSync_withNoSeeder_neverTouchesTheHistoryPivotSeeder()
    {
        // No IHistoryPivotSeeder passed - the existing tests above already exercise this implicitly (omitting the
        // parameter), but this makes the "no-op when history capture/windowing is not configured" contract explicit.
        using FlatTestContainer container = CreateContainer(_persistence);
        FlatTreeSyncStore store = container.Resolve<FlatTreeSyncStore>();
        BlockHeader pivot = Build.A.BlockHeader.WithNumber(123).WithStateRoot(TestItem.KeccakA).TestObject;

        Assert.DoesNotThrow(() => store.FinalizeSync(pivot));
    }

    private sealed class BatchRecordingPersistence(IPersistence inner, FakeWriteBatch batch) : IPersistence
    {
        public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None) => inner.CreateReader(flags);

        public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags = WriteFlags.None) => batch;

        public void Flush() => inner.Flush();

        public void Clear() => inner.Clear();
    }

    private sealed class OrderRecordingPersistence(IPersistence inner, List<string> log) : IPersistence
    {
        public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None) => inner.CreateReader(flags);

        public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags = WriteFlags.None)
        {
            if (to != StateId.Sync) log.Add("advance-pointer");
            return inner.CreateWriteBatch(from, to, flags);
        }

        public void Flush()
        {
            log.Add("flush");
            inner.Flush();
        }

        public void Clear() => inner.Clear();
    }
}
