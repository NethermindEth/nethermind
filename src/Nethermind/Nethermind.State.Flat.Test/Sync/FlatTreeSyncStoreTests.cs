// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Sync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Sync;

[TestFixture(FlatLayout.Flat)]
[TestFixture(FlatLayout.PaprikaFlat)]
public class FlatTreeSyncStoreTests(FlatLayout layout)
{
    private SnapshotableMemColumnsDb<FlatDbColumns> _columnsDb = null!;
    private LayoutPersistence _layoutPersistence = null!;
    private IPersistence _persistence = null!;

    [SetUp]
    public void SetUp()
    {
        _columnsDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _layoutPersistence = new LayoutPersistence(_columnsDb, layout, trieNodeLog: false);
        _persistence = _layoutPersistence.Persistence;
    }

    [TearDown]
    public void TearDown()
    {
        _layoutPersistence.Dispose();
        _columnsDb.Dispose();
    }

    private void WriteStorage(Address address, UInt256 slot, byte[] value)
    {
        using IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        batch.SetStorage(address, slot, new UInt256(value, isBigEndian: true));
    }

    private bool HasStorageEntries(Address address)
    {
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        using IPersistence.IFlatIterator slots = reader.CreateStorageIterator(ValueKeccak.Compute(address.Bytes), ValueKeccak.Zero, ValueKeccak.MaxValue);
        return slots.MoveNext();
    }

    [Test]
    public void EnsureStorageEmpty_deletes_all_storage_entries()
    {
        Address address = TestItem.AddressA;
        WriteStorage(address, 0, [0x01, 0x15, 0xe8]);
        WriteStorage(address, 1, [0xab, 0xcd]);
        WriteStorage(address, 42, [0xff]);

        Assert.That(HasStorageEntries(address), Is.True, "Storage entries should exist before cleanup");

        FlatTreeSyncStore store = new(_persistence, Substitute.For<IPersistenceManager>(), LimboLogs.Instance);
        store.EnsureStorageEmpty(Keccak.Compute(address.Bytes));

        Assert.That(HasStorageEntries(address), Is.False, "Storage entries should be deleted after EnsureStorageEmpty");
    }

    [Test]
    public void FinalizeSync_flushes_data_before_advancing_the_state_pointer()
    {
        // #11457: the state pointer must advance only after the (DisableWAL) data is flushed.
        List<string> log = [];
        OrderRecordingPersistence spy = new(_persistence, log);
        FlatTreeSyncStore store = new(spy, Substitute.For<IPersistenceManager>(), LimboLogs.Instance);
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
        FlatTreeSyncStore store = new(spy, Substitute.For<IPersistenceManager>(), LimboLogs.Instance, seeder);
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
        FlatTreeSyncStore store = new(_persistence, Substitute.For<IPersistenceManager>(), LimboLogs.Instance);
        BlockHeader pivot = Build.A.BlockHeader.WithNumber(123).WithStateRoot(TestItem.KeccakA).TestObject;

        Assert.DoesNotThrow(() => store.FinalizeSync(pivot));
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
