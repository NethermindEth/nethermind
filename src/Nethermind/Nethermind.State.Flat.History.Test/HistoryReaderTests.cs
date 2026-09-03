// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class HistoryReaderTests
{
    private static readonly Address Address = new("0x0000000000000000000000000000000000000abc");
    private static readonly UInt256 Slot = 7;

    private SnapshotableMemColumnsDb<FlatDbColumns> _db = null!;
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;
    private HistoryReader _reader = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        FlatDbConfig config = new();
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, config);
        _reader = new HistoryReader(_db, _historyColumns, availability, rowFormat, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        _historyColumns.Dispose();
    }

    [TestCase(3ul, -1)]
    [TestCase(5ul, 5)]
    [TestCase(19ul, 5)]
    [TestCase(20ul, 20)]
    [TestCase(29ul, 20)]
    [TestCase(30ul, -1)]
    [TestCase(35ul, -1)]
    public void Resolves_account_as_of_block(ulong block, long expectedNonce)
    {
        HistoryColumnsWriter.RecordAccount(_historyColumns, Address, 5, new Account(5, 500));
        HistoryColumnsWriter.RecordAccount(_historyColumns, Address, 20, new Account(20, 2000));
        HistoryColumnsWriter.RecordAccount(_historyColumns, Address, 30, account: null);

        bool found = _reader.TryGetAccount(block, Address, out AccountStruct account);

        if (expectedNonce < 0)
        {
            Assert.That(found, Is.False);
            return;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(account.Nonce, Is.EqualTo((ulong)expectedNonce));
            Assert.That(account.Balance, Is.EqualTo((UInt256)(expectedNonce * 100)));
        }
    }

    [TestCase(3ul, null)]
    [TestCase(5ul, "aa")]
    [TestCase(19ul, "aa")]
    [TestCase(20ul, "bbcc")]
    [TestCase(29ul, "bbcc")]
    [TestCase(30ul, null)]
    [TestCase(35ul, null)]
    public void Resolves_storage_as_of_block(ulong block, string? expectedHex)
    {
        HistoryColumnsWriter.RecordStorage(_historyColumns, Address, Slot, 5, [0xAA]);
        HistoryColumnsWriter.RecordStorage(_historyColumns, Address, Slot, 20, [0xBB, 0xCC]);
        HistoryColumnsWriter.RecordStorage(_historyColumns, Address, Slot, 30, ReadOnlySpan<byte>.Empty);

        bool found = _reader.TryGetStorage(block, Address, Slot, out SlotValue value);

        if (expectedHex is null)
        {
            Assert.That(found, Is.False);
            return;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(value.AsReadOnlySpan.WithoutLeadingZeros().ToArray(), Is.EqualTo(Convert.FromHexString(expectedHex)));
        }
    }

    [Test]
    public void TryGetAccount_AtBlockAboveGlobalFloor_ResolvesNormally()
    {
        HistoryColumnsWriter.RecordAccount(_historyColumns, Address, 5, new Account(5, 500));
        HistoryColumnsWriter.SetWatermark(_historyColumns, 30);
        HistoryColumnsWriter.SetGlobalFloor(_historyColumns, 5);

        bool found = _reader.TryGetAccount(20, Address, out AccountStruct account);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True, "a read at or above the floor must resolve exactly as it would with no window configured");
            Assert.That(account.Nonce, Is.EqualTo(5UL));
        }
    }

    [Test]
    public void IsAvailable_AtBlockBelowGlobalFloor_ReportsFalseWithoutThrowing()
    {
        HistoryColumnsWriter.MarkBlock(_historyColumns, 6, TestItem.KeccakA);
        HistoryColumnsWriter.SetWatermark(_historyColumns, 30);
        HistoryColumnsWriter.SetGlobalFloor(_historyColumns, 10);

        bool available = _reader.IsAvailable(new StateId(6, TestItem.KeccakA));

        Assert.That(available, Is.False, "the scope-entry gate reports unavailable rather than throwing, so the caller can fall through to its own unavailable-state handling");
    }

    [TestCase(false, 9ul)]
    [TestCase(true, 5ul)]
    public void A_row_landing_under_a_v3_read_is_observed_only_when_a_capture_was_published(bool publishCapture, ulong expectedNonce)
    {
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryRetentionBlocks = 100 };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, config);
        HistoryColumnsWriter.SetPersistedAccount(_db, Address, new Account(9, 900));

        HookedFlatColumns hooked = new(_db, FlatDbColumns.Account, () =>
        {
            HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, 20, new Account(5, 500));
            if (publishCapture) availability.MarkCapturePublished();
        });

        HistoryReader reader = new(hooked, _historyColumns, availability, rowFormat, LimboLogs.Instance);

        bool found = reader.TryGetAccount(10, Address, out AccountStruct account);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(account.Nonce, Is.EqualTo(expectedNonce));
        }
    }

    [TestCase(false, false)]
    [TestCase(true, true)]
    public void A_storage_row_landing_under_a_v3_read_is_observed_only_when_a_capture_was_published(bool publishCapture, bool expectedFound)
    {
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryRetentionBlocks = 100 };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, config);

        HookedFlatColumns hooked = new(_db, FlatDbColumns.Storage, () =>
        {
            HistoryColumnsWriter.RecordStorageV3(_historyColumns, Address, Slot, 20, [0x55]);
            if (publishCapture) availability.MarkCapturePublished();
        });

        HistoryReader reader = new(hooked, _historyColumns, availability, rowFormat, LimboLogs.Instance);

        bool found = reader.TryGetStorage(10, Address, Slot, out SlotValue value);

        Assert.That(found, Is.EqualTo(expectedFound));
        if (expectedFound)
        {
            Assert.That(value.AsReadOnlySpan.WithoutLeadingZeros().ToArray(), Is.EqualTo(new byte[] { 0x55 }));
        }
    }

    private sealed class HookedFlatColumns(IColumnsDb<FlatDbColumns> inner, FlatDbColumns hookedColumn, Action onFirstRead)
        : IColumnsDb<FlatDbColumns>
    {
        public IDb GetColumnDb(FlatDbColumns key) =>
            key == hookedColumn ? new HookedDb(inner.GetColumnDb(key), onFirstRead) : inner.GetColumnDb(key);

        public IColumnsWriteBatch<FlatDbColumns> StartWriteBatch() => inner.StartWriteBatch();
        public IEnumerable<FlatDbColumns> ColumnKeys => inner.ColumnKeys;
        public IColumnDbSnapshot<FlatDbColumns> CreateSnapshot() => inner.CreateSnapshot();
        public void Flush(bool onlyWal = false) => inner.Flush(onlyWal);
        public void SyncWal() => inner.SyncWal();

        public void Dispose() { }
    }

    private sealed class HookedDb(IDb inner, Action onFirstRead) : IDb, ISortedKeyValueStore
    {
        private int _fired;

        private ISortedKeyValueStore Sorted => (ISortedKeyValueStore)inner;

        public byte[]? FirstKey => Sorted.FirstKey;
        public byte[]? LastKey => Sorted.LastKey;

        public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive, ReadFlags flags = ReadFlags.None) =>
            Sorted.GetViewBetween(firstKeyInclusive, lastKeyExclusive, flags);

        public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            if (Interlocked.Exchange(ref _fired, 1) == 0) onFirstRead();
            return inner.Get(key, flags);
        }

        public void Set(scoped ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) => inner.Set(key, value, flags);
        public string Name => inner.Name;
        public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys] => inner[keys];
        public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false) => inner.GetAll(ordered);
        public IEnumerable<byte[]> GetAllKeys(bool ordered = false) => inner.GetAllKeys(ordered);
        public IEnumerable<byte[]> GetAllValues(bool ordered = false) => inner.GetAllValues(ordered);
        public IWriteBatch StartWriteBatch() => inner.StartWriteBatch();
        public void Flush(bool onlyWal = false) => inner.Flush(onlyWal);

        public void Dispose() { }
    }

    [Test]
    public void IsPrunedBelowFloor_DistinguishesPrunedFromNeverCaptured()
    {
        HistoryColumnsWriter.SetWatermark(_historyColumns, 30);
        HistoryColumnsWriter.SetGlobalFloor(_historyColumns, 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_reader.IsPrunedBelowFloor(6), Is.True, "covered by the watermark but below the floor must report pruned");
            Assert.That(_reader.IsPrunedBelowFloor(40), Is.False, "above the watermark is 'never captured', not 'pruned'");
        }
    }
}
