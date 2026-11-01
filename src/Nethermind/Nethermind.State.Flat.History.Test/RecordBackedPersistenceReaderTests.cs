// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.History.Prestate;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

/// <summary>
/// The history holds, at block 2: the first account with nonce 1 and its slot 1 = 5, the second account with nonce 2.
/// Each test stores a record for that state whose values differ from history where it matters, so an answer shows
/// which source served it. Real records never differ; the verification test covers the case where one does.
/// </summary>
public class RecordBackedPersistenceReaderTests
{
    private const ulong Block = 2;
    private static readonly Address First = TestItem.AddressA;
    private static readonly Address Second = TestItem.AddressB;
    private static readonly StateId State = new(Block, Root(Block));

    private SnapshotableMemColumnsDb<FlatDbColumns> _db = null!;
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _columns = null!;
    private HistoryReader _historyReader = null!;
    private HistoryScopeGate _gate = null!;
    private MemDb _records = null!;
    private PrestateRecordStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _columns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        for (ulong block = 0; block <= 4; block++) HistoryColumnsWriter.MarkBlockV3(_columns, block, Root(block));
        HistoryColumnsWriter.SetWatermarkV3(_columns, 4);
        HistoryColumnsWriter.RecordAccountV3(_columns, First, 3, new Account(1, 10));
        HistoryColumnsWriter.RecordAccountV3(_columns, Second, 3, new Account(2, 20));
        HistoryColumnsWriter.RecordStorageV3(_columns, First, 1, 3, [0x05]);
        (HistoryAvailability availability, HistoryRowFormat format) = HistoryColumnsWriter.CreateSharedFormat(_columns,
            new FlatDbConfig { HistoryEnabled = true, HistoryRetention = HistoryRetentionMode.Rolling, HistoryRetentionBlocks = 1000 });
        Assert.That(format.IsV3, Is.True, "precondition: the windowed format records poisoned clears");
        _historyReader = new HistoryReader(_db, _columns, availability, format, LimboLogs.Instance);
        _gate = new HistoryScopeGate();
        _records = new MemDb();
        _store = new PrestateRecordStore(_records);
    }

    [TearDown]
    public void TearDown()
    {
        _records.Dispose();
        _db.Dispose();
        _columns.Dispose();
    }

    [Test]
    public void GetAccount_WhenTheRecordHoldsIt_AnswersFromTheRecord()
    {
        WriteRecord(capture => capture.OnAccountRead(First, new Account(9, 90)));
        using RecordBackedPersistenceReader reader = Reader(new FlatDbConfig { HistoryPrestateRecordsLazyOpenReads = 0 });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.GetAccount(First)!.Nonce, Is.EqualTo(9UL), "a key in the record is served from it");
            Assert.That(reader.GetAccount(Second)!.Nonce, Is.EqualTo(2UL), "a key not in the record is read from history");
        }
    }

    [Test]
    public void TryGetSlot_WhenTheRecordHoldsAZero_ReturnsUnsetWithoutHistory()
    {
        WriteRecord(capture => capture.OnSlotRead(First, 1, 0));
        using RecordBackedPersistenceReader reader = Reader(new FlatDbConfig { HistoryPrestateRecordsLazyOpenReads = 0 });
        UInt256 value = 77;

        bool found = reader.TryGetSlot(First, 1, ref value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.False, "a held zero does not fall through to history, which holds 5");
            Assert.That(value, Is.EqualTo((UInt256)77), "an unset slot leaves the output untouched, as the history reader does");
        }
    }

    [Test]
    public void GetAccount_BeforeTheLazyOpenThreshold_ReadsHistoryThenTheRecord()
    {
        WriteRecord(capture => capture.OnAccountRead(First, new Account(9, 90)));
        using RecordBackedPersistenceReader reader = Reader(new FlatDbConfig { HistoryPrestateRecordsLazyOpenReads = 2 });

        ulong first = reader.GetAccount(First)!.Nonce;
        ulong second = reader.GetAccount(First)!.Nonce;
        ulong third = reader.GetAccount(First)!.Nonce;

        Assert.That((first, second, third), Is.EqualTo((1UL, 1UL, 9UL)), "a record that is not cached opens only after two history reads");
    }

    [Test]
    public void GetAccount_WhenTheRecordIsCached_UsesItAtOnce()
    {
        WriteRecord(capture => capture.OnAccountRead(First, new Account(9, 90)));
        PrestateRecordCache cache = new(_store, new FlatDbConfig { HistoryPrestateRecordsLazyOpenReads = 100 }, LimboLogs.Instance);
        Assert.That(cache.TryLease(State, out PrestateRecord? warm), Is.True, "precondition: the record is cached");
        warm!.Dispose();

        using RecordBackedPersistenceReader reader = new(History(), cache, State);

        Assert.That(reader.GetAccount(First)!.Nonce, Is.EqualTo(9UL), "a cached record costs nothing to open, so the threshold does not apply");
    }

    [TestCase(true, TestName = "TryGetSlot_WithAPoisonedClearAbove_GoesToHistoryAndKeepsItsError")]
    [TestCase(false, TestName = "TryGetSlot_WithoutAPoisonedClear_AnswersFromTheRecord")]
    public void TryGetSlot_PoisonedClear_DecidesTheSource(bool poisoned)
    {
        Address destructed = TestItem.AddressC;
        if (poisoned)
        {
            IDb clears = _columns.GetColumnDb(FlatHistoryColumns.StorageClears);
            using IWriteBatch batch = clears.StartWriteBatch();
            new StorageClearStore(clears).RecordPoisonedClear(3, destructed.ToAccountPath.Bytes, batch);
        }

        WriteRecord(capture => capture.OnSlotRead(destructed, 1, 6));
        using RecordBackedPersistenceReader reader = Reader(new FlatDbConfig { HistoryPrestateRecordsLazyOpenReads = 0 });
        UInt256 value = default;

        if (poisoned)
        {
            Assert.That(() => reader.TryGetSlot(destructed, 1, ref value), Throws.InstanceOf<MissingTrieNodeException>(),
                "the history path fails closed for an over-cap self-destruct, and the record must not hide that");
            return;
        }

        Assert.That(reader.TryGetSlot(destructed, 1, ref value) && value == 6, Is.True);
    }

    [Test]
    public void GetAccount_WhenVerificationFindsAMismatch_ReturnsHistoryAndStopsRecordReads()
    {
        WriteRecord(capture => capture.OnAccountRead(First, new Account(9, 90)));
        PrestateRecordCache cache = new(_store, new FlatDbConfig { HistoryPrestateRecordsLazyOpenReads = 0, HistoryPrestateRecordsVerifyPercent = 100 }, LimboLogs.Instance);
        using RecordBackedPersistenceReader reader = new(History(), cache, State);

        ulong nonce = reader.GetAccount(First)!.Nonce;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nonce, Is.EqualTo(1UL), "a verified read answers with the history value");
            Assert.That(cache.IsEnabled, Is.False, "one mismatch stops record reads until restart");
        }
    }

    [Test]
    public void HistoricalBundle_WithReaderFlags_ReadsHistoryOnly([Values(ReaderFlags.None, ReaderFlags.Sync, ReaderFlags.FullScan)] ReaderFlags flags)
    {
        WriteRecord(capture => capture.OnAccountRead(First, new Account(9, 90)));
        HistoricalFlatDbManager manager = Manager(new PrestateRecordCache(_store, new FlatDbConfig { HistoryPrestateRecordsLazyOpenReads = 0 }, LimboLogs.Instance));

        using ReadOnlySnapshotBundle bundle = manager.GatherReadOnlySnapshotBundle(State, flags);

        Assert.That(bundle.GetAccount(First)!.Nonce, Is.EqualTo(flags == ReaderFlags.None ? 9UL : 1UL),
            "scans and verification read history alone; only plain reads consult the record");
    }

    [Test]
    public void HistoricalBundle_WhenTheRecordStoreFails_ReadsHistoryAndLeavesNoScopeOpen()
    {
        using FailingRecordsDb failing = new();
        PrestateRecordCache cache = new(new PrestateRecordStore(failing), new FlatDbConfig { HistoryPrestateRecordsLazyOpenReads = 0 }, LimboLogs.Instance);
        failing.Fail = true;
        HistoricalFlatDbManager manager = Manager(cache);

        using (ReadOnlySnapshotBundle bundle = manager.GatherReadOnlySnapshotBundle(State))
        {
            Assert.That(bundle.GetAccount(First)!.Nonce, Is.EqualTo(1UL), "a failing store is no record");
        }

        Assert.That(_gate.TryDrainForFloorAdvance(TimeSpan.Zero, CancellationToken.None), Is.True, "a leaked scope would stop history pruning");
    }

    private void WriteRecord(Action<PrestateCapture> record) => _store.Write(State, PrestateRecordTests.Encode(record, State));

    private HistoryBackedPersistenceReader History() => new(_historyReader, State, _gate);

    private RecordBackedPersistenceReader Reader(FlatDbConfig config) =>
        new(History(), new PrestateRecordCache(_store, config, LimboLogs.Instance), State);

    private HistoricalFlatDbManager Manager(PrestateRecordCache cache)
    {
        IPersistenceManager persistence = Substitute.For<IPersistenceManager>();
        persistence.GetCurrentPersistedStateId().Returns(new StateId(100, TestItem.KeccakA));
        return new HistoricalFlatDbManager(Substitute.For<IFlatDbManager>(), persistence, _historyReader, new ResourcePool(new FlatDbConfig { CompactSize = 16 }),
            enableDetailedMetrics: false, _gate, LimboLogs.Instance, cache);
    }

    private static ValueHash256 Root(ulong block) => Keccak.Compute(BitConverter.GetBytes(block)).ValueHash256;

    private sealed class FailingRecordsDb : MemDb
    {
        public bool Fail { get; set; }

        public override byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) =>
            Fail ? throw new InvalidOperationException("disk failure") : base.Get(key, flags);
    }
}
