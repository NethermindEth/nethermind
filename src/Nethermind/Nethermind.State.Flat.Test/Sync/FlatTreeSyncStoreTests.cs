// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Sync;
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
        _persistence = new RocksDbPersistence(_columnsDb, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown() => _columnsDb.Dispose();

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

        storageDb.Set(storageKey, ((ReadOnlySpan<byte>)value).WithoutLeadingZeros().ToArray());
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
        Assert.That(firstFlush, Is.GreaterThanOrEqualTo(0), "data must be flushed during finalize");
        Assert.That(firstAdvance, Is.GreaterThan(firstFlush), "state pointer must advance only after the data flush");

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        Assert.That(reader.CurrentState.BlockNumber, Is.EqualTo(123), "state pointer should end at the pivot block");
    }

    private sealed class OrderRecordingPersistence(IPersistence inner, List<string> log) : IPersistence
    {
        public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None) => inner.CreateReader(flags);

        public StateId GetCurrentState() => inner.GetCurrentState();

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
