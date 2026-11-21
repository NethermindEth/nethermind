// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.State.Flat.Sync;
using Nethermind.Synchronization.FastSync;
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

    private static FlatTestContainer CreateContainer(IPersistence persistence, IHistoryPivotSeeder? seeder = null, IReleaseSpec? spec = null) =>
        new(loadPersistedTier: false, configure: builder =>
        {
            IStateSyncPivot pivot = Substitute.For<IStateSyncPivot>();
            pivot.GetPivotHeader().Returns(Build.A.BlockHeader.TestObject);
            builder
                .AddSingleton<IPersistence>(persistence)
                .AddSingleton<IPersistenceManager>(Substitute.For<IPersistenceManager>())
                .AddSingleton<IStateSyncPivot>(pivot)
                .AddSingleton<ISpecProvider>(new TestSpecProvider(spec ?? Shanghai.Instance));
            if (seeder is not null) builder.AddSingleton<IHistoryPivotSeeder>(seeder);
        });

    private void WriteStorageDirectToDb(Address address, UInt256 slot, byte[] value)
    {
        IDb storageDb = _columnsDb.GetColumnDb(FlatDbColumns.Storage);
        ValueHash256 addrHash = ValueKeccak.Compute(address.Bytes);
        Span<byte> slotBytes = stackalloc byte[32];
        slot.ToBigEndian(slotBytes);
        ValueHash256 slotHash = ValueKeccak.Compute(slotBytes);

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
    public void SaveNode_self_destructs_each_replaced_range_once_keeping_the_leaf_account_unless_EIP_6780_is_enabled([Values] bool eip6780Enabled)
    {
        Address kept = TestItem.AddressB;
        using (FlatTestContainer container = CreateContainer(_persistence))
        {
            SaveRootAccountLeaf(container.Resolve<FlatTreeSyncStore>(), TestItem.AddressA);
        }

        FakeWriteBatch batch = new();
        using FlatTestContainer recording = CreateContainer(new BatchRecordingPersistence(_persistence, batch), spec: eip6780Enabled ? Cancun.Instance : Shanghai.Instance);
        SaveRootAccountLeaf(recording.Resolve<FlatTreeSyncStore>(), kept);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch.DeleteAccountRangeCalls, Is.Not.Empty);
            Assert.That(batch.SelfDestructRangeCalls, Has.Count.EqualTo(eip6780Enabled ? 0 : batch.DeleteAccountRangeCalls.Count));
            Assert.That(batch.SelfDestructRangeCalls.Select(static call => call.KeptAccount), Is.All.EqualTo(Keccak.Compute(kept.Bytes).ValueHash256));
        }
    }

    private static readonly byte[] StorageNodeRlp = [0xc2, 0x80, 0x80];
    private static readonly TreePath[] StorageNodePaths = [TreePath.FromHexString("1"), TreePath.FromHexString("1111111111111111")];

    private static void SaveRootAccountLeaf(FlatTreeSyncStore store, Address address)
    {
        TrieNode leaf = TrieNodeFactory.CreateLeaf(Nibbles.BytesToNibbleBytes(Keccak.Compute(address.Bytes).Bytes), Rlp.Encode(new Account(1, 1)));
        TreePath path = TreePath.Empty;
        leaf.ResolveKey(NullTrieNodeResolver.Instance, ref path);
        byte[] rlp = leaf.FullRlp.ToArray()!;
        store.SaveNode(null, TreePath.Empty, ValueKeccak.Compute(rlp), rlp);
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
