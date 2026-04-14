// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
        _persistence = new RocksDbPersistence(_columnsDb);
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

}
