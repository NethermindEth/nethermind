// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
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

// HistoryRetentionBlocks > 0 forces the v3 (pre-value, ascending-suffix) row format - a pruner can never actually
// run against v2 rows in production, since HistoryRetentionBlocks == 0 short-circuits RunOnePassUnderGate before
// any scan. Every test that exercises an actual prune pass here therefore stages v3 rows and constructs the
// writer/reader/pruner from the SAME config CreatePruner uses, mirroring the single DI-bound HistoryAvailability/
// HistoryRowFormat pair production shares between them - the exact combination whose absence (writer/reader
// resolved from one config, pruner assuming v2 regardless) masked the pruner's format-decode bug.
public class HistoryWindowPrunerTests
{
    private static readonly Address Address = TestItem.AddressA;
    private static readonly UInt256 Slot = 1;

    private SnapshotableMemColumnsDb<FlatDbColumns> _db = null!;
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;
    private HistoryReader _reader = null!;
    private HistoryWriter _writer = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        _historyColumns.Dispose();
    }

    [Test]
    public void RunOnePass_DeletesEveryRowAtOrBelowFloor_ForV3PreValueRows()
    {
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, 5, new Account(0, 0));
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, 10, new Account(1, 100));
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, 15, new Account(2, 200));
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, 20, new Account(3, 300));
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 20);

        HistoryWindowPruner pruner = CreatePruner(retentionBlocks: 8);
        Assert.That(_writer.LastCapturedBlock, Is.EqualTo(20UL), "precondition: watermark set to 20");

        pruner.RunOnePass(CancellationToken.None);

        HistoryStoreV3 accountHistoryV3 = new(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountHistory));
        Span<byte> buffer = stackalloc byte[256];
        ReadOnlySpan<byte> flatKey = AccountKey();

        // Floor = 20 - 8 = 12. A v3 pre-value row at or below the floor can never answer a valid (>= floor) query
        // (see HistoryRowFormat.RetainsNewestRowAtOrBelowFloor's remarks) - unlike v2 there is no single row to
        // keep, so both blocks 5 and 10 must be gone entirely, leaving 15 as the answer for every query below it.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accountHistoryV3.TryGetValueBeforeNextChange(4, flatKey, buffer, out ulong foundAt1), Is.GreaterThan(0));
            Assert.That(foundAt1, Is.EqualTo(15UL), "the row at block 5 must be deleted; 15 answers this query instead");

            Assert.That(accountHistoryV3.TryGetValueBeforeNextChange(9, flatKey, buffer, out ulong foundAt2), Is.GreaterThan(0));
            Assert.That(foundAt2, Is.EqualTo(15UL), "the row at block 10 must be deleted for the same reason");

            Assert.That(accountHistoryV3.TryGetValueBeforeNextChange(12, flatKey, buffer, out ulong foundAt3), Is.GreaterThan(0));
            Assert.That(foundAt3, Is.EqualTo(15UL), "the row at block 15 (above the floor) must survive and answer a query at the floor");

            Assert.That(accountHistoryV3.TryGetValueBeforeNextChange(15, flatKey, buffer, out ulong foundAt4), Is.GreaterThan(0));
            Assert.That(foundAt4, Is.EqualTo(20UL), "the row at block 20 (also above the floor) must survive too");
        }

        pruner.Dispose();
    }

    [Test]
    public void RunOnePass_PublishesGlobalFloor_MatchingWatermarkMinusRetention()
    {
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, 0, new Account(0, 0));
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 20);

        HistoryWindowPruner pruner = CreatePruner(retentionBlocks: 8);
        pruner.RunOnePass(CancellationToken.None);

        Assert.That(_reader.IsPrunedBelowFloor(11), Is.True, "the published floor (12) must reject block 11");
        Assert.That(_reader.IsPrunedBelowFloor(12), Is.False, "the published floor (12) must admit block 12 itself");

        pruner.Dispose();
    }

    // retention == 0 means unwindowed (v2) - the only production-reachable configuration where a pruner is
    // constructed but never actually scans (RunOnePassUnderGate returns before touching row format at all), so
    // this is the one test in this file that legitimately stays on v2-shaped staging.
    [Test]
    public void RunOnePass_WithZeroRetentionBlocks_NeverPublishesAFloor()
    {
        HistoryColumnsWriter.RecordAccount(_historyColumns, Address, 0, new Account(0, 0));
        HistoryColumnsWriter.SetWatermark(_historyColumns, 20);

        HistoryWindowPruner pruner = CreatePruner(retentionBlocks: 0);
        pruner.RunOnePass(CancellationToken.None);

        Assert.That(_reader.IsPrunedBelowFloor(0), Is.False, "HistoryRetentionBlocks = 0 must leave today's unbounded-retention behavior untouched");

        pruner.Dispose();
    }

    [Test]
    public void RunOnePass_WhenBackfillInterlockIsActive_IsANoOp()
    {
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, 0, new Account(0, 0));
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, 5, new Account(1, 100));
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 20);

        HistoryWindowPruner pruner = CreatePruner(retentionBlocks: 8, interlock: new AlwaysActiveBackfillInterlock());
        pruner.RunOnePass(CancellationToken.None);

        HistoryStoreV3 accountHistoryV3 = new(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountHistory));
        Span<byte> buffer = stackalloc byte[256];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_reader.IsPrunedBelowFloor(0), Is.False, "a pass must not publish a floor while backfill is active");
            Assert.That(accountHistoryV3.TryGetValueBeforeNextChange(4, AccountKey(), buffer, out ulong foundAt), Is.GreaterThan(0));
            Assert.That(foundAt, Is.EqualTo(5UL), "a pass must not delete any row while backfill is active");
        }

        pruner.Dispose();
    }

    [Test]
    public void RunOnePass_WhileABeginBackfillScopeIsOpen_IsANoOp()
    {
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, 0, new Account(0, 0));
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, 5, new Account(1, 100));
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 20);

        HistoryWindowPruner pruner = CreatePruner(retentionBlocks: 8);
        IDisposable backfillScope = pruner.BeginBackfill();

        // Deterministic, single-threaded: this is the real gate (BeginBackfill/EndBackfill), not the external
        // IBackfillInterlock the sibling test above exercises — proving the pruner's own admission check, not
        // just the advisory flag, blocks a pass for as long as the scope is open.
        pruner.RunOnePass(CancellationToken.None);

        HistoryStoreV3 accountHistoryV3 = new(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountHistory));
        Span<byte> buffer = stackalloc byte[256];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_reader.IsPrunedBelowFloor(0), Is.False, "a pass must not publish a floor while a BeginBackfill scope is open");
            Assert.That(accountHistoryV3.TryGetValueBeforeNextChange(4, AccountKey(), buffer, out ulong foundAt), Is.GreaterThan(0));
            Assert.That(foundAt, Is.EqualTo(5UL), "a pass must not delete any row while a BeginBackfill scope is open");
        }

        backfillScope.Dispose();
        pruner.RunOnePass(CancellationToken.None);

        Assert.That(_reader.IsPrunedBelowFloor(0), Is.True, "once the scope is disposed, a subsequent pass must proceed normally");

        pruner.Dispose();
    }

    [Test]
    public void BeginBackfillAsync_WhileAPrunePassHoldsTheGate_CompletesOnlyAfterThePassReleases()
    {
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, 0, new Account(0, 0));
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 20);

        HistoryWindowPruner pruner = CreatePruner(retentionBlocks: 8);

        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);

        // Blocks the pass mid-column on its very first budget check (there is exactly one row to scan, so the
        // check is guaranteed to happen) - the pass is genuinely still holding the gate for as long as this
        // background call has not returned, not merely simulated by a flag.
        Task passTask = Task.Run(() => pruner.RunOnePass(CancellationToken.None, () => new GateProbeBudget(entered, release)));
        entered.Wait();

        Task<IDisposable> backfillTask = pruner.BeginBackfillAsync(CancellationToken.None);

        // Deterministic, not a race: BeginBackfillAsync runs synchronously up to its first await, and by the time
        // entered.Wait() above returned, TryEnterPrune already set the gate's pruning-active flag (well before the
        // per-row budget check that signals entered) - so this call's own TryEnterBackfill is guaranteed to have
        // failed and suspended on the pass's not-yet-signaled exit task, with no dependency on timing or sleeps.
        Assert.That(backfillTask.IsCompleted, Is.False, "the async wait must not complete while the prune pass still holds the gate");

        release.Set();
        passTask.Wait();

        Assert.That(backfillTask.GetAwaiter().GetResult(), Is.Not.Null, "the async wait must complete once the prune pass releases the gate");
        Assert.That(backfillTask.IsCompletedSuccessfully, Is.True);

        backfillTask.Result.Dispose();
        pruner.Dispose();
    }

    [Test]
    public void BeginBackfill_DisposedTwice_ReleasesTheGateOnlyOnce()
    {
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, 0, new Account(0, 0));
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 20);

        HistoryWindowPruner pruner = CreatePruner(retentionBlocks: 8);
        IDisposable firstScope = pruner.BeginBackfill();
        IDisposable secondScope = pruner.BeginBackfill();

        firstScope.Dispose();
        firstScope.Dispose(); // double-dispose must not release the gate an extra time

        pruner.RunOnePass(CancellationToken.None);
        Assert.That(_reader.IsPrunedBelowFloor(0), Is.False, "the second, still-open scope must still block a pass after the first scope's double-dispose");

        secondScope.Dispose();
        pruner.RunOnePass(CancellationToken.None);
        Assert.That(_reader.IsPrunedBelowFloor(0), Is.True, "once every scope is disposed, a pass must proceed normally");

        pruner.Dispose();
    }

    [Test]
    public void RunOnePass_WithAnExhaustedBudget_YieldsMidColumnAndResumesFromCursorOnTheNextPass()
    {
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, 0, new Account(0, 0));
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, 5, new Account(1, 100));
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, 10, new Account(2, 200));
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 20);

        // Floor 12: v3 has no free "keep" row like v2 - ascending iteration visits block 0 first, which costs the
        // pass's only allowed check and is deleted outright; the next row (block 5) is where the pass then yields.
        HistoryWindowPruner exhausted = CreatePruner(retentionBlocks: 8);
        exhausted.RunOnePass(CancellationToken.None, () => new CountdownBudget(rowsBeforeExhaustion: 1));
        exhausted.Dispose();

        HistoryStoreV3 accountHistoryV3 = new(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountHistory));
        Span<byte> buffer = stackalloc byte[256];
        ReadOnlySpan<byte> flatKey = AccountKey();

        Assert.That(accountHistoryV3.TryGetValueBeforeNextChange(4, flatKey, buffer, out ulong foundAt), Is.GreaterThan(0));
        Assert.That(foundAt, Is.EqualTo(5UL),
            "precondition: the pass yielded before reaching block 5, which must still be present and answer this query");

        HistoryWindowPruner ample = CreatePruner(retentionBlocks: 8);
        ample.RunOnePass(CancellationToken.None);
        ample.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accountHistoryV3.TryGetValueBeforeNextChange(0, flatKey, buffer, out _), Is.EqualTo(-1),
                "resuming from the persisted mid-column cursor must reach and delete every remaining at-or-below-floor row (5 and 10) - v3 keeps none of them");
            Assert.That(_reader.IsPrunedBelowFloor(11), Is.True, "the floor must still be correctly published after a resumed pass");
            Assert.That(_reader.IsPrunedBelowFloor(12), Is.False);
        }
    }

    // Regression for round-robin independence: account has two versions (needs two budget checks to finish its
    // one key), storage has one (needs only one). Giving every column the same "one check" shaped budget must let
    // storage complete while account yields — proving storage's progress does not wait behind account finishing,
    // which the original completedAccount && PruneVersionedColumn(storage...) chain would have serialized.
    [Test]
    public void RunOnePass_StorageColumnMakesProgressEvenWhenAccountColumnDoesNotComplete()
    {
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, 0, new Account(0, 0));
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, 5, new Account(1, 100));
        HistoryColumnsWriter.RecordStorageV3(_historyColumns, Address, Slot, 5, [0xAA]);
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 20);

        HistoryWindowPruner pruner = CreatePruner(retentionBlocks: 8);
        pruner.RunOnePass(CancellationToken.None, () => new CountdownBudget(rowsBeforeExhaustion: 1));
        pruner.Dispose();

        HistoryStoreV3 accountHistoryV3 = new(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountHistory));
        HistoryStoreV3 storageHistoryV3 = new(_historyColumns.GetColumnDb(FlatHistoryColumns.StorageHistory));
        using (Assert.EnterMultipleScope())
        {
            Span<byte> buffer = stackalloc byte[256];
            Assert.That(accountHistoryV3.TryGetValueBeforeNextChange(4, AccountKey(), buffer, out ulong foundAt), Is.GreaterThan(0));
            Assert.That(foundAt, Is.EqualTo(5UL), "account needs two checks for its one key and only got one — block 5 must not have been reached yet");

            Assert.That(storageHistoryV3.TryGetValueBeforeNextChange(4, StorageKey(), buffer, out _), Is.EqualTo(-1),
                "storage needs only one check for its single row — it must complete in the same pass regardless of account's progress");
        }
    }

    // Regression for PruneClearsColumn's per-account retention rule: two clears below the floor for the same
    // account. Only the newest below-floor one is kept; that alone must still suffice for any query whose needed
    // range includes it, since the newest is always >= any older one it superseded.
    [Test]
    public void PruneClearsColumn_KeepsOnlyTheNewestBelowFloorClearPerAccount_AndItAloneStillAnswersAQuery()
    {
        StorageClearStore clears = new(_historyColumns.GetColumnDb(FlatHistoryColumns.StorageClears));
        Span<byte> accountKeyBuffer = stackalloc byte[HistoryKeyLayout.AccountKeyLength];
        byte[] flatAccountKey = HistoryKeyLayout.EncodeAccountKey(accountKeyBuffer, Address.ToAccountPath).ToArray();

        RecordClear(clears, flatAccountKey, block: 3);
        RecordClear(clears, flatAccountKey, block: 7);
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, 0, new Account(0, 0));
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 20);

        HistoryWindowPruner pruner = CreatePruner(retentionBlocks: 8); // floor = 12
        pruner.RunOnePass(CancellationToken.None);
        pruner.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clears.HasClearInRange(flatAccountKey, afterBlockExclusive: 2, atOrBeforeBlock: 3), Is.False,
                "block 3's clear (superseded by the newer one below the floor) must have been pruned");
            Assert.That(clears.HasClearInRange(flatAccountKey, afterBlockExclusive: 1, atOrBeforeBlock: 12), Is.True,
                "the retained newest-below-floor clear (block 7) alone must still answer a query whose range includes it");
        }
    }

    // Regression for PruneBlockMarkers: the marker at exactly the floor block must survive (only markers strictly
    // below it are dead), since HistoryReader.IsAvailable/Matches needs it to validate an EIP-1898 state root at
    // the floor itself.
    [Test]
    public void PruneBlockMarkers_RetainsTheMarkerAtExactlyTheFloor_ForEip1898RootMatchingAtTheFloor()
    {
        ValueHash256 rootAtFloor = ValueKeccak.Compute("floor"u8);
        ValueHash256 rootBelowFloor = ValueKeccak.Compute("below"u8);
        HistoryColumnsWriter.MarkBlockV3(_historyColumns, 11, rootBelowFloor);
        HistoryColumnsWriter.MarkBlockV3(_historyColumns, 12, rootAtFloor);
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, 0, new Account(0, 0));
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 20);

        HistoryWindowPruner pruner = CreatePruner(retentionBlocks: 8); // floor = 12
        pruner.RunOnePass(CancellationToken.None);
        pruner.Dispose();

        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, new FlatDbConfig { HistoryEnabled = true, HistoryRetentionBlocks = 8 });
        HistoryReader reader = new(_db, _historyColumns, new FlatDbConfig { HistoryEnabled = true, HistoryRetentionBlocks = 8 }, availability, rowFormat, LimboLogs.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.IsAvailable(new StateId(12, rootAtFloor)), Is.True,
                "the marker at exactly the floor must survive so a read at the floor can validate its state root");
            Assert.That(reader.IsAvailable(new StateId(11, rootBelowFloor)), Is.False,
                "block 11 is below the floor and pruned regardless of its marker (reads there are refused before the marker is even consulted)");
        }
    }

    private void RecordClear(StorageClearStore clears, byte[] accountKey, ulong block)
    {
        using IColumnsWriteBatch<FlatHistoryColumns> batch = _historyColumns.StartWriteBatch();
        clears.RecordClear(block, accountKey, batch.GetColumnBatch(FlatHistoryColumns.StorageClears));
    }

    [Test]
    public void PruneChangesetSidecarColumn_RetentionIndependentOfReadPathWindow_DeletesBelowItsOwnFloor()
    {
        ChangesetSidecarStore sidecarStore = new(_historyColumns.GetColumnDb(FlatHistoryColumns.ChangesetSidecar));
        WriteSidecarChunk(sidecarStore, 0);
        WriteSidecarChunk(sidecarStore, 5);
        WriteSidecarChunk(sidecarStore, 10);
        WriteSidecarChunk(sidecarStore, 15);
        WriteSidecarChunk(sidecarStore, 20);
        HistoryColumnsWriter.SetWatermark(_historyColumns, 20);

        // HistoryRetentionBlocks stays 0 (unbounded read-path window) - the sidecar's own retention knob is what
        // drives this pruning, proving the two are genuinely independent per FlatDbColumns.ChangesetSidecar's doc.
        HistoryWindowPruner pruner = CreatePruner(retentionBlocks: 0, configure: c =>
        {
            c.HistoryChangesetSidecarEnabled = true;
            c.HistoryChangesetSidecarRetentionBlocks = 8;
        });
        pruner.RunOnePass(CancellationToken.None);
        pruner.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sidecarStore.TryGetChunk(0, 0), Is.Null, "floor = 20 - 8 = 12; block 0 is below it");
            Assert.That(sidecarStore.TryGetChunk(5, 0), Is.Null, "block 5 is below the floor too");
            Assert.That(sidecarStore.TryGetChunk(10, 0), Is.Null, "block 10 is below the floor too");
            Assert.That(sidecarStore.TryGetChunk(15, 0), Is.Not.Null, "block 15 is at/above the floor");
            Assert.That(sidecarStore.TryGetChunk(20, 0), Is.Not.Null, "block 20 is at/above the floor");
        }
    }

    // The in-memory test double's GatherMetric().Size reports a row count, not real bytes - the pruner's own
    // logic does not care what unit the metric is in, only that it compares against the configured cap, so this
    // still exercises the real control flow (detect over-cap, purge oldest-first, re-check, record the metric).
    [Test]
    public void PruneChangesetSidecarColumn_OverItsByteCap_PurgesOldestRangesAheadOfRetention_AndRecordsTheMetric()
    {
        ChangesetSidecarStore sidecarStore = new(_historyColumns.GetColumnDb(FlatHistoryColumns.ChangesetSidecar));
        for (ulong block = 0; block < 1500; block++)
        {
            WriteSidecarChunk(sidecarStore, block);
        }
        HistoryColumnsWriter.SetWatermark(_historyColumns, 1500);

        long before = Metrics.FlatHistorySidecarOverCapPurgedRows;

        HistoryWindowPruner pruner = CreatePruner(retentionBlocks: 0, configure: c =>
        {
            c.HistoryChangesetSidecarEnabled = true;
            c.HistoryChangesetSidecarMaxBytes = 1000;
        });
        pruner.RunOnePass(CancellationToken.None);
        pruner.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sidecarStore.TryGetChunk(0, 0), Is.Null, "the oldest rows must be purged first to get back under the cap");
            Assert.That(sidecarStore.TryGetChunk(1499, 0), Is.Not.Null, "the newest rows must survive - only enough of the oldest are dropped to get under budget");
            Assert.That(Metrics.FlatHistorySidecarOverCapPurgedRows, Is.GreaterThan(before), "the forced-early-purge metric must record this degraded state");
        }
    }

    private void WriteSidecarChunk(ChangesetSidecarStore store, ulong block)
    {
        using IColumnsWriteBatch<FlatHistoryColumns> batch = _historyColumns.StartWriteBatch();
        store.RecordChunk(block, 0, new byte[] { 0x01 }, batch.GetColumnBatch(FlatHistoryColumns.ChangesetSidecar));
    }

    private HistoryWindowPruner CreatePruner(ulong retentionBlocks, int passBudgetSeconds = 30, IBackfillInterlock? interlock = null, Action<FlatDbConfig>? configure = null)
    {
        FlatDbConfig config = new()
        {
            HistoryEnabled = true,
            HistoryRetentionBlocks = retentionBlocks,
            HistoryPruneIntervalBlocks = 1,
            HistoryPrunePassBudgetSeconds = passBudgetSeconds
        };
        configure?.Invoke(config);
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, config);
        _writer = new HistoryWriter(_db, _historyColumns, config, availability, rowFormat, LimboLogs.Instance);
        _reader = new HistoryReader(_db, _historyColumns, config, availability, rowFormat, LimboLogs.Instance);
        return new HistoryWindowPruner(
            _writer, _historyColumns, config,
            interlock ?? NullBackfillInterlock.Instance,
            new HistoryScopeGate(),
            availability, rowFormat,
            LimboLogs.Instance);
    }

    private sealed class GateProbeBudget(ManualResetEventSlim entered, ManualResetEventSlim release) : IPruneBudget
    {
        private bool _signaled;

        public bool Exhausted
        {
            get
            {
                if (!_signaled)
                {
                    _signaled = true;
                    entered.Set();
                    release.Wait();
                }

                return false;
            }
        }
    }

    private sealed class CountdownBudget(int rowsBeforeExhaustion) : IPruneBudget
    {
        private int _remaining = rowsBeforeExhaustion;

        public bool Exhausted
        {
            get
            {
                if (_remaining <= 0) return true;
                _remaining--;
                return false;
            }
        }
    }

    private static byte[] AccountKey()
    {
        Span<byte> buffer = stackalloc byte[HistoryKeyLayout.AccountKeyLength];
        return HistoryKeyLayout.EncodeAccountKey(buffer, Address.ToAccountPath).ToArray();
    }

    private static byte[] StorageKey()
    {
        ValueHash256 slotHash = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(Slot, ref slotHash);
        Span<byte> buffer = stackalloc byte[BaseFlatPersistence.StorageKeyLength];
        return BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(buffer, Address.ToAccountPath, slotHash).ToArray();
    }

    private sealed class AlwaysActiveBackfillInterlock : IBackfillInterlock
    {
        public bool IsBackfillActive => true;
    }
}
