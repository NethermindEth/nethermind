// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

[TestFixture]
public class OrphanStorageRowSweepTests
{
    private static readonly Address Living = TestItem.AddressA;
    private static readonly Address DestroyedAtBirth = TestItem.AddressB;
    private static readonly Address RevivedEmpty = TestItem.AddressC;
    private static readonly Address LateBloomer = TestItem.AddressD;
    private static readonly Address NeverAnAccount = TestItem.AddressE;
    private static readonly Address Flickering = TestItem.AddressF;

    private SnapshotableMemColumnsDb<FlatHistoryColumns> _history = null!;
    private SnapshotableMemColumnsDb<FlatDbColumns> _flat = null!;
    private HistoryRowFormat _rowFormat = null!;
    private HistoryAvailability _availability = null!;

    [SetUp]
    public void SetUp()
    {
        _history = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _flat = new SnapshotableMemColumnsDb<FlatDbColumns>();
        (_availability, _rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_history, new FlatDbConfig { HistoryEnabled = true });

        HistoryColumnsWriter.RecordAccount(_history, Living, block: 5, new Account(1, 1, TestItem.KeccakA, Keccak.OfAnEmptyString));
        HistoryColumnsWriter.RecordStorage(_history, Living, 1, block: 5, [0x0A]);
        HistoryColumnsWriter.RecordStorage(_history, Living, 1, block: 8, [0x0B]);

        HistoryColumnsWriter.RecordAccount(_history, DestroyedAtBirth, block: 7, null);
        HistoryColumnsWriter.RecordStorage(_history, DestroyedAtBirth, 1, block: 7, [0x0C]);

        HistoryColumnsWriter.RecordAccount(_history, RevivedEmpty, block: 7, new Account(0, 0));
        HistoryColumnsWriter.RecordStorage(_history, RevivedEmpty, 1, block: 7, [0x0D]);
        HistoryColumnsWriter.RecordStorage(_history, RevivedEmpty, 2, block: 7, [0x0E]);

        HistoryColumnsWriter.RecordAccount(_history, LateBloomer, block: 3, new Account(0, 0));
        HistoryColumnsWriter.RecordAccount(_history, LateBloomer, block: 9, new Account(1, 1, TestItem.KeccakB, Keccak.OfAnEmptyString));
        HistoryColumnsWriter.RecordStorage(_history, LateBloomer, 1, block: 9, [0x0F]);

        HistoryColumnsWriter.RecordStorage(_history, NeverAnAccount, 1, block: 4, [0x10]);
        HistoryColumnsWriter.RecordStorage(_history, NeverAnAccount, 1, block: 6, []);

        for (ulong block = 10; block <= 14; block++) HistoryColumnsWriter.RecordAccount(_history, Flickering, block, new Account(block, 1, TestItem.KeccakC, Keccak.OfAnEmptyString));
        HistoryColumnsWriter.RecordAccount(_history, Flickering, block: 15, new Account(15, 1));
        for (ulong block = 16; block <= 18; block++) HistoryColumnsWriter.RecordAccount(_history, Flickering, block, new Account(block, 1, TestItem.KeccakC, Keccak.OfAnEmptyString));
        HistoryColumnsWriter.RecordStorage(_history, Flickering, 1, block: 12, [0x11]);
        HistoryColumnsWriter.RecordStorage(_history, Flickering, 1, block: 15, [0x12]);
        HistoryColumnsWriter.RecordStorage(_history, Flickering, 1, block: 17, [0x13]);
        using IColumnsWriteBatch<FlatHistoryColumns> batch = _history.StartWriteBatch();
        new StorageClearStore(_history.GetColumnDb(FlatHistoryColumns.StorageClears)).RecordClear(15, Flickering.ToAccountPath.Bytes, batch.GetColumnBatch(FlatHistoryColumns.StorageClears));
    }

    [TearDown]
    public void TearDown()
    {
        _history.Dispose();
        _flat.Dispose();
    }

    [Test]
    public void Rows_whose_account_had_no_storage_at_their_block_are_deleted_and_the_rest_stay()
    {
        using OrphanStorageRowSweep sweep = new(_history, _flat, _availability, _rowFormat, new SweepPacer(), LimboLogs.Instance);

        OrphanStorageRowReport? announced = null;
        sweep.Completed += completed => announced = completed;
        OrphanStorageRowReport report = sweep.RunToCompletion(repair: true, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(report, Is.EqualTo(new OrphanStorageRowReport(RowsScanned: 11, OrphanRows: 5, OrphanAccounts: 4)));
            Assert.That(announced, Is.EqualTo(report), "whoever derives data from the rows learns that they changed");
            Assert.That(sweep.AlreadyHandled, Is.True);
            Assert.That(Slot(Living, 1, 5), Is.EqualTo(0x0A), "a contract whose account row carries a storage root keeps its rows");
            Assert.That(Slot(Living, 1, 8), Is.EqualTo(0x0B));
            Assert.That(Slot(DestroyedAtBirth, 1, 7), Is.Zero, "a row at a block where the account row is a deletion cannot be part of any state; this is a contract created and destroyed in one block");
            Assert.That(Slot(RevivedEmpty, 1, 7), Is.Zero, "a row at a block where the account row says empty storage root contradicts that root; this is the same contract revived by a transfer in that block");
            Assert.That(Slot(RevivedEmpty, 2, 7), Is.Zero);
            Assert.That(Slot(LateBloomer, 1, 9), Is.EqualTo(0x0F), "an account that had no storage earlier and gained it later keeps the rows written once it had a storage root");
            Assert.That(Slot(NeverAnAccount, 1, 4), Is.Zero, "a row with no account row at or below its block at all is orphaned: in a genesis-anchored history every account that exists has a row");
            Assert.That(RowCount(NeverAnAccount, 1), Is.EqualTo(1), "a deletion row is never touched, whatever account stands over it");
            Assert.That(Slot(Flickering, 1, 12), Is.EqualTo(0x11), "runs of account rows with the same verdict collapse without moving the boundary: storage held before the empty-root block stays");
            Assert.That(Slot(Flickering, 1, 15), Is.Zero, "the row at the one block the account had no storage goes, and the clear the destruction recorded keeps the older value from showing through");
            Assert.That(Slot(Flickering, 1, 17), Is.EqualTo(0x13), "and storage held after it stays");
        }
    }

    [Test]
    public void A_check_counts_without_deleting_or_stamping()
    {
        using OrphanStorageRowSweep sweep = new(_history, _flat, _availability, _rowFormat, new SweepPacer(), LimboLogs.Instance);

        OrphanStorageRowReport report = sweep.RunToCompletion(repair: false, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(report, Is.EqualTo(new OrphanStorageRowReport(RowsScanned: 11, OrphanRows: 5, OrphanAccounts: 4)));
            Assert.That(Slot(DestroyedAtBirth, 1, 7), Is.EqualTo(0x0C));
            Assert.That(sweep.AlreadyHandled, Is.False);
        }
    }

    [Test]
    public void A_pass_that_runs_out_of_budget_yields_inside_a_bucket_and_resumes_from_the_row_it_stopped_at()
    {
        using OrphanStorageRowSweep sweep = new(_history, _flat, _availability, _rowFormat, new SweepPacer(), LimboLogs.Instance);

        bool completed = sweep.RunOnePass(repair: true, maxUnits: 1, TimeSpan.MaxValue, CancellationToken.None);
        bool handledAfterOnePass = sweep.AlreadyHandled;
        OrphanStorageRowReport report = sweep.RunToCompletion(repair: true, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(completed, Is.False);
            Assert.That(handledAfterOnePass, Is.False, "nothing is stamped until every row has been seen");
            Assert.That(report, Is.EqualTo(new OrphanStorageRowReport(RowsScanned: 11, OrphanRows: 5, OrphanAccounts: 4)), "the later passes continue from the cursor, so every row is counted exactly once");
            Assert.That(Slot(DestroyedAtBirth, 1, 7), Is.Zero);
            Assert.That(Slot(RevivedEmpty, 2, 7), Is.Zero);
            Assert.That(sweep.AlreadyHandled, Is.True);
        }
    }

    [Test]
    public void A_sweep_finished_by_a_later_instance_announces_the_whole_sweep()
    {
        using (OrphanStorageRowSweep first = new(_history, _flat, _availability, _rowFormat, new SweepPacer(), LimboLogs.Instance))
        {
            first.RunOnePass(repair: true, maxUnits: 1, TimeSpan.MaxValue, CancellationToken.None);
        }

        using OrphanStorageRowSweep resumed = new(_history, _flat, _availability, _rowFormat, new SweepPacer(), LimboLogs.Instance);
        OrphanStorageRowReport? announced = null;
        resumed.Completed += completed => announced = completed;

        resumed.RunToCompletion(repair: true, CancellationToken.None);

        Assert.That(announced, Is.EqualTo(new OrphanStorageRowReport(RowsScanned: 11, OrphanRows: 5, OrphanAccounts: 4)),
            "the tally travels with the cursor, so a consumer deciding on the announced counts sees what the whole sweep did, not what the last process saw");
    }

    [Test]
    public void A_progress_record_left_at_the_start_of_the_key_space_is_resumed_with_its_tally_not_restarted()
    {
        Span<byte> record = stackalloc byte[1 + 3 * sizeof(long)];
        record[0] = 0;
        BinaryPrimitives.WriteInt64BigEndian(record[1..], 3);
        BinaryPrimitives.WriteInt64BigEndian(record[(1 + sizeof(long))..], 2);
        BinaryPrimitives.WriteInt64BigEndian(record[(1 + 2 * sizeof(long))..], 1);
        _flat.GetColumnDb(FlatDbColumns.Metadata).PutSpan(Keccak.Compute("OrphanStorageRowsSweepProgress").Bytes, record);
        using OrphanStorageRowSweep resumed = new(_history, _flat, _availability, _rowFormat, new SweepPacer(), LimboLogs.Instance);
        OrphanStorageRowReport? announced = null;
        resumed.Completed += completed => announced = completed;

        resumed.RunToCompletion(repair: true, CancellationToken.None);

        Assert.That(announced, Is.EqualTo(new OrphanStorageRowReport(RowsScanned: 14, OrphanRows: 7, OrphanAccounts: 5)),
            "a first pass writes its tally under cursor zero before it deletes, so a record at cursor zero is a resume whose deleted rows can no longer be recounted; only a missing record is a fresh sweep");
    }

    [Test]
    public void A_windowed_history_is_unsupported_and_recorded_as_handled()
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> windowed = new();
        (HistoryAvailability _, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(windowed, new FlatDbConfig { HistoryEnabled = true, HistoryRetention = HistoryRetentionMode.Rolling, HistoryRetentionBlocks = 128 });
        using OrphanStorageRowSweep sweep = new(windowed, _flat, _availability, rowFormat, new SweepPacer(), LimboLogs.Instance);

        bool handledBefore = sweep.AlreadyHandled;
        sweep.MarkUnsupported();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sweep.Supported, Is.False, "windowed rows are pre-values, a different contract from the post-value rows this sweep judges");
            Assert.That(handledBefore, Is.False);
            Assert.That(sweep.AlreadyHandled, Is.True, "recorded once so the decision is not re-logged on every start");
            Assert.That(() => sweep.RunOnePass(repair: true, maxUnits: long.MaxValue, TimeSpan.MaxValue, CancellationToken.None), Throws.InvalidOperationException);
        }
    }

    [Test]
    public void A_subscriber_that_arrives_after_completion_receives_the_latched_report()
    {
        using OrphanStorageRowSweep sweep = new(_history, _flat, _availability, _rowFormat, new SweepPacer(), LimboLogs.Instance);
        OrphanStorageRowReport completed = sweep.RunToCompletion(repair: true, CancellationToken.None);

        OrphanStorageRowReport? late = null;
        sweep.Completed += report => late = report;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(late, Is.EqualTo(completed), "init steps run concurrently and a small history completes in milliseconds, so a consumer subscribing after the fact must still get the report");
            Assert.That(sweep.TryGetCompletedReport(out OrphanStorageRowReport polled), Is.True);
            Assert.That(polled, Is.EqualTo(completed));
        }
    }

    [Test]
    public void A_fresh_flat_state_whose_history_still_holds_rows_is_not_stamped_without_a_scan()
    {
        _availability.PublishWatermark(20, _rowFormat.FormatVersion);
        using OrphanStorageRowSweep sweep = new(_history, _flat, _availability, _rowFormat, new SweepPacer(), LimboLogs.Instance);

        Assert.That(sweep.TryStampFresh(), Is.False, "freshness is judged from the database the rows live in: a flat state without a persisted block says nothing about a history that already has rows to judge");
    }

    [Test]
    public void A_database_with_neither_a_persisted_state_nor_a_watermark_is_stamped_without_a_scan()
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> empty = new();
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(empty, new FlatDbConfig { HistoryEnabled = true });
        using OrphanStorageRowSweep sweep = new(empty, _flat, availability, rowFormat, new SweepPacer(), LimboLogs.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sweep.TryStampFresh(), Is.True);
            Assert.That(sweep.AlreadyHandled, Is.True);
        }
    }

    [Test]
    public void A_history_with_a_published_global_floor_is_unsupported()
    {
        _availability.PublishWatermark(20, _rowFormat.FormatVersion);
        _availability.PublishGlobalFloor(5);
        using OrphanStorageRowSweep sweep = new(_history, _flat, _availability, _rowFormat, new SweepPacer(), LimboLogs.Instance);

        Assert.That(sweep.Supported, Is.False, "below a floor the absence of an account row means pruned, not absent; the deletion is irreversible, so the premise is checked rather than assumed");
    }

    [Test]
    public void A_started_sweep_waits_for_the_drain_then_sweeps_in_the_background_and_announces()
    {
        IPersistence flatPersistence = new RocksDbPersistence(_flat, LimboLogs.Instance);
        using (flatPersistence.CreateWriteBatch(StateId.PreGenesis, new StateId(1, Keccak.EmptyTreeHash)))
        {
        }

        using OrphanStorageRowSweep sweep = new(_history, _flat, _availability, _rowFormat, new SweepPacer(), LimboLogs.Instance);
        OrphanStorageRowReport? announced = null;
        sweep.Completed += report => announced = report;

        sweep.Start(repair: true, drainedAtBlock: 1);

        Assert.That(() => sweep.AlreadyHandled, Is.True.After(10_000, 50), "the background thread drains, sweeps, announces and stamps on its own");
        Assert.That(announced, Is.EqualTo(new OrphanStorageRowReport(RowsScanned: 11, OrphanRows: 5, OrphanAccounts: 4)));
    }

    private int RowCount(Address address, UInt256 slot)
    {
        ValueHash256 slotHash = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(slot, ref slotHash);
        ReadOnlySpan<byte> flatKey = Nethermind.State.Flat.Persistence.BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(stackalloc byte[Nethermind.State.Flat.Persistence.BaseFlatPersistence.StorageKeyLength], address.ToAccountPath, slotHash);
        Span<byte> upper = stackalloc byte[flatKey.Length + 9];
        flatKey.CopyTo(upper);
        upper[flatKey.Length..].Fill(0xFF);
        upper[^1] = 0x00;
        int count = 0;
        using ISortedView view = ((ISortedKeyValueStore)_history.GetColumnDb(FlatHistoryColumns.StorageHistory)).GetViewBetween(flatKey, upper);
        while (view.MoveNext()) count++;
        return count;
    }

    private int Slot(Address address, UInt256 slot, ulong block)
    {
        HistoryReader reader = new(_flat, _history, _availability, _rowFormat, LimboLogs.Instance);
        if (!reader.TryGetStorage(block, address, slot, out SlotValue value)) return 0;

        ReadOnlySpan<byte> bytes = value.AsReadOnlySpan;
        return bytes.IsZero() ? 0 : bytes[^1];
    }
}
