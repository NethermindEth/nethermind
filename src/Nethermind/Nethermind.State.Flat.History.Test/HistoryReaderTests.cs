// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
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

        bool found = _reader.TryGetStorage(block, Address, Slot, out UInt256 value);

        if (expectedHex is null)
        {
            Assert.That(found, Is.False);
            return;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(value.ToMinimalBigEndian(), Is.EqualTo(Convert.FromHexString(expectedHex)));
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

    [TestCase(false, 9ul, false)]
    [TestCase(false, 9ul, true)]
    [TestCase(true, 5ul, false)]
    [TestCase(true, 5ul, true)]
    public void A_row_landing_under_a_v3_read_is_observed_only_when_a_capture_was_published(bool publishCapture, ulong expectedNonce, bool scoped)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        using ColumnsDb<FlatHistoryColumns>? rocks = scoped ? CreateRocksHistory(directory.Path) : null;
        IColumnsDb<FlatHistoryColumns> history = rocks is null ? _historyColumns : rocks;
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryRetention = HistoryRetentionMode.Rolling, HistoryRetentionBlocks = 100 };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(history, config);
        HistoryColumnsWriter.SetPersistedAccount(_db, Address, new Account(9, 900));

        HookedColumns<FlatDbColumns> hooked = new(_db, FlatDbColumns.Account, () =>
        {
            HistoryColumnsWriter.RecordAccountV3(history, Address, 20, new Account(5, 500));
            if (publishCapture) availability.MarkCapturePublished();
        });

        HistoryReader reader = new(hooked, history, availability, rowFormat, LimboLogs.Instance);

        using HistoryReader.ReadScope? scope = scoped ? new() : null;
        bool found = reader.TryGetAccount(10, Address, out AccountStruct account, scope);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(account.Nonce, Is.EqualTo(expectedNonce));
        }
    }

    [TestCase(false, false, false)]
    [TestCase(false, false, true)]
    [TestCase(true, true, false)]
    [TestCase(true, true, true)]
    public void A_storage_row_landing_under_a_v3_read_is_observed_only_when_a_capture_was_published(bool publishCapture, bool expectedFound, bool scoped)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        using ColumnsDb<FlatHistoryColumns>? rocks = scoped ? CreateRocksHistory(directory.Path) : null;
        IColumnsDb<FlatHistoryColumns> history = rocks is null ? _historyColumns : rocks;
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryRetention = HistoryRetentionMode.Rolling, HistoryRetentionBlocks = 100 };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(history, config);

        HookedColumns<FlatDbColumns> hooked = new(_db, FlatDbColumns.Storage, () =>
        {
            HistoryColumnsWriter.RecordStorageV3(history, Address, Slot, 20, [0x55]);
            if (publishCapture) availability.MarkCapturePublished();
        });

        HistoryReader reader = new(hooked, history, availability, rowFormat, LimboLogs.Instance);

        using HistoryReader.ReadScope? scope = scoped ? new() : null;
        bool found = reader.TryGetStorage(10, Address, Slot, out UInt256 value, clearsCache: null, scope);

        Assert.That(found, Is.EqualTo(expectedFound));
        if (expectedFound)
        {
            Assert.That(value.ToMinimalBigEndian(), Is.EqualTo(new byte[] { 0x55 }));
        }
    }

    [Test]
    public void ReadScopeState_WithAtMostOneRead_DoesNotAllocate([Values] bool enabled, [Values(0, 1)] int reads)
    {
        ReadScopeWithoutRows(enabled, reads);
        long before = GC.GetAllocatedBytesForCurrentThread();
        ReadScopeWithoutRows(enabled, reads);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(allocated, Is.Zero);
    }

    [Test]
    public void ReadScopeState_AfterTheFirstRead_ReusesTheScope([Values] bool enabled)
    {
        HistoryReader.ReadScopeState state = new(enabled);
        try
        {
            Assert.That(state.GetScope(), Is.Null);
            HistoryReader.ReadScope? scope = state.GetScope();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(scope is not null, Is.EqualTo(enabled));
                Assert.That(state.GetScope(), Is.SameAs(scope));
            }
        }
        finally
        {
            state.Dispose();
        }
    }

    [Test]
    public void ReadScopeState_AfterDisposal_RejectsReadsAndDisposesViews([Values(0, 1, 2)] int reads)
    {
        HistoryReader.ReadScopeState state = new(true);
        HistoryReader.ReadScope? scope = null;
        for (int i = 0; i < reads; i++) scope = state.GetScope();

        state.Dispose();
        state.Dispose();

        Assert.That(() => state.GetScope(), Throws.TypeOf<ObjectDisposedException>());
        if (scope is not null)
        {
            HistoryStoreV3 store = new(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountHistory));
            Assert.That(() => store.TryGetValueBeforeNextChange(0, new byte[32], new byte[256], out _, scope.Accounts),
                Throws.TypeOf<ObjectDisposedException>());
        }
    }

    [Test]
    public void PersistenceReader_AfterTheFirstRead_ReusesAndDisposesNativeViews([Values] bool storage)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        using ColumnsDb<FlatHistoryColumns> history = CreateRocksHistory(directory.Path);
        HistoryColumnsWriter.RecordAccountV3(history, Address, 20, new Account(5, 500));
        HistoryColumnsWriter.RecordStorageV3(history, Address, Slot, 20, [0x55]);
        HistoryColumnsWriter.MarkBlockV3(history, 10, Keccak.EmptyTreeHash);
        HistoryColumnsWriter.SetWatermarkV3(history, 20);
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryRetention = HistoryRetentionMode.Rolling, HistoryRetentionBlocks = 100 };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(history, config);
        List<TrackedView> views = [];
        HookedColumns<FlatHistoryColumns> observed = new(history,
            storage ? FlatHistoryColumns.StorageHistory : FlatHistoryColumns.AccountHistory, static () => { }, view =>
            {
                TrackedView tracked = new(view);
                views.Add(tracked);
                return tracked;
            });
        HistoryReader historyReader = new(_db, observed, availability, rowFormat, LimboLogs.Instance);
        HistoryScopeGate gate = new();
        using HistoryBackedPersistenceReader reader = new(historyReader, new StateId(10, Keccak.EmptyTreeHash), gate);

        for (int i = 0; i < 4; i++)
        {
            if (storage)
            {
                UInt256 value = default;
                Assert.That(reader.TryGetSlot(Address, Slot, ref value), Is.True);
                Assert.That(value, Is.EqualTo((UInt256)0x55));
            }
            else
            {
                Assert.That(reader.GetAccount(Address)?.Nonce, Is.EqualTo(5UL));
            }
            Assert.That(views.Count, Is.EqualTo(i == 0 ? 1 : 2));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(views[0].Disposed, Is.True);
            Assert.That(views[1].Disposed, Is.False);
        }
        reader.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(views[1].Disposed, Is.True);
            Assert.That(gate.TryDrainForFloorAdvance(TimeSpan.Zero, CancellationToken.None), Is.True);
        }
    }

    private static void ReadScopeWithoutRows(bool enabled, int reads)
    {
        HistoryReader.ReadScopeState state = new(enabled);
        try
        {
            for (int i = 0; i < reads; i++) _ = state.GetScope();
        }
        finally
        {
            state.Dispose();
        }
    }

    private static ColumnsDb<FlatHistoryColumns> CreateRocksHistory(string path)
    {
        DbConfig config = new();
        RocksDbConfigFactory factory = new(config, new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance, validateConfig: false);
        return new ColumnsDb<FlatHistoryColumns>(path, new DbSettings("FlatHistory", "history"), config, factory,
            LimboLogs.Instance, Enum.GetValues<FlatHistoryColumns>());
    }

    private sealed class HookedColumns<TKey>(IColumnsDb<TKey> inner, TKey hookedColumn, Action onFirstRead, Func<ISortedView, ISortedView>? onView = null)
        : IColumnsDb<TKey> where TKey : struct, Enum
    {
        public IDb GetColumnDb(TKey key) =>
            EqualityComparer<TKey>.Default.Equals(key, hookedColumn) ? new HookedDb(inner.GetColumnDb(key), onFirstRead, onView) : inner.GetColumnDb(key);

        public IColumnsWriteBatch<TKey> StartWriteBatch() => inner.StartWriteBatch();
        public IEnumerable<TKey> ColumnKeys => inner.ColumnKeys;
        public IColumnDbSnapshot<TKey> CreateSnapshot() => inner.CreateSnapshot();
        public void Flush(bool onlyWal = false) => inner.Flush(onlyWal);
        public void SyncWal() => inner.SyncWal();

        public void Dispose() { }
    }

    private sealed class HookedDb(IDb inner, Action onFirstRead, Func<ISortedView, ISortedView>? onView = null) : IDb, ISortedKeyValueStore
    {
        private int _fired;

        private ISortedKeyValueStore Sorted => (ISortedKeyValueStore)inner;

        public byte[]? FirstKey => Sorted.FirstKey;
        public byte[]? LastKey => Sorted.LastKey;

        public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive, ReadFlags flags = ReadFlags.None)
        {
            ISortedView view = Sorted.GetViewBetween(firstKeyInclusive, lastKeyExclusive, flags);
            return onView?.Invoke(view) ?? view;
        }

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

    private sealed class TrackedView(ISortedView inner) : IRebindableSortedView
    {
        internal bool Disposed { get; private set; }
        public ReadOnlySpan<byte> CurrentKey => inner.CurrentKey;
        public ReadOnlySpan<byte> CurrentValue => inner.CurrentValue;
        public bool StartBefore(ReadOnlySpan<byte> value) => inner.StartBefore(value);
        public bool MoveNext() => inner.MoveNext();
        public bool SeekTo(ReadOnlySpan<byte> key) => ((ISeekableSortedView)inner).SeekTo(key);
        public bool TryResetBounds(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive) =>
            inner is IRebindableSortedView reusable && reusable.TryResetBounds(firstKeyInclusive, lastKeyExclusive);

        public void Dispose()
        {
            inner.Dispose();
            Disposed = true;
        }
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
