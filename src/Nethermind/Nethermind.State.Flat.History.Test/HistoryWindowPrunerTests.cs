// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.History.Changesets;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class HistoryWindowPrunerTests
{
    private static readonly Address Address = TestItem.AddressA;
    private static readonly UInt256 Slot = 1;
    private static readonly Address SlicedAddress = TestItem.AddressB;
    private static readonly ulong[] LongTailDeadBlocks = [10, 20, 30];
    private static readonly ulong[] SlicedRowsBelowGeneralFloor = [40, 50];
    private const ulong LongTailWatermark = 100;
    private const ulong LongTailGeneralFloor = 60;

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
    public void RunOnePass_WithAnExhaustedBudget_YieldsMidColumnAndResumesFromCursorOnTheNextPass()
    {
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, 0, new Account(0, 0));
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, 5, new Account(1, 100));
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, 10, new Account(2, 200));
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 20);

        HistoryWindowPruner exhausted = CreatePruner(retentionBlocks: 8);
        exhausted.RunOnePass(CancellationToken.None, new CountdownBudget(rowsBeforeExhaustion: 1));
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

    [Test]
    public void RunOnePass_WithASweepStillPending_StillAdvancesTheFloorToTheCurrentWatermark()
    {
        for (ulong block = 0; block <= 60; block += 5)
        {
            HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, block, new Account(block, block * 10));
        }

        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 20);
        HistoryWindowPruner first = CreatePruner(retentionBlocks: 8);
        first.RunOnePass(CancellationToken.None, new CountdownBudget(rowsBeforeExhaustion: 1));
        first.Dispose();

        Assert.That(_reader.IsPrunedBelowFloor(11), Is.True, "precondition: the first pass published floor 12 and yielded mid-column");

        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 60);
        HistoryWindowPruner second = CreatePruner(retentionBlocks: 8);
        second.RunOnePass(CancellationToken.None, new CountdownBudget(rowsBeforeExhaustion: 1));
        second.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_reader.IsPrunedBelowFloor(51), Is.True,
                "a sweep that has not finished must not pin the window: the floor follows the watermark, or a node that starts deep never reaches the retention it was configured with");
            Assert.That(_reader.IsPrunedBelowFloor(52), Is.False, "and it must land exactly at watermark minus retention, not past it");
        }
    }

    [Test]
    public void RunOnePass_AfterADrainTimeout_StillOwesAndPerformsTheDeletesForTheAlreadyPublishedFloor()
    {
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, 5, new Account(1, 100));
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 20);

        HistoryScopeGate gate = new();
        long stuckScope = gate.EnterScope();

        HistoryWindowPruner pruner = CreatePruner(retentionBlocks: 8, passBudgetSeconds: 1, scopeGate: gate);

        bool completedWhileStuck = pruner.RunOnePass(CancellationToken.None);
        gate.ExitScope(stuckScope);
        bool completedAfterRelease = pruner.RunOnePass(CancellationToken.None);
        pruner.Dispose();

        HistoryStoreV3 accountHistoryV3 = new(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountHistory));
        Span<byte> buffer = stackalloc byte[256];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(completedWhileStuck, Is.False, "precondition: the open scope must make the drain time out");
            Assert.That(completedAfterRelease, Is.True);
            Assert.That(accountHistoryV3.TryGetValueBeforeNextChange(4, AccountKey(), buffer, out _), Is.EqualTo(-1),
                "the floor was already published by the timed-out pass, so the next pass must resume its deletes rather than see no floor advance and skip them");
        }
    }

    [Test]
    public void RunOnePass_WithTheDrainStillBlocked_RefusesToDeleteRowsThatOpenScopesCanStillRead()
    {
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, 5, new Account(1, 100));
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 20);

        HistoryScopeGate gate = new();
        long stuckScope = gate.EnterScope();

        HistoryWindowPruner pruner = CreatePruner(retentionBlocks: 8, passBudgetSeconds: 1, scopeGate: gate);

        bool firstPass = pruner.RunOnePass(CancellationToken.None);
        bool secondPass = pruner.RunOnePass(CancellationToken.None);

        HistoryStoreV3 accountHistoryV3 = new(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountHistory));
        Span<byte> buffer = stackalloc byte[256];
        bool rowSurvived = accountHistoryV3.TryGetValueBeforeNextChange(4, AccountKey(), buffer, out _) > 0;

        gate.ExitScope(stuckScope);
        pruner.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstPass, Is.False, "precondition: the open scope must make the first drain time out");
            Assert.That(secondPass, Is.False,
                "the owed-deletes path must re-wait for the scopes the timed-out drain never collected, not walk straight into deleting");
            Assert.That(rowSurvived, Is.True,
                "a scope opened under the old floor is still resolving this row; deleting it would make that read answer from live state instead of failing closed");
        }
    }

    [Test]
    public void RunOnePass_AColumnSweptEarlierInTheCycle_IsNotRescannedWhileItsSiblingsFinish()
    {
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, TestItem.Addresses[0], 1, new Account(0, 0));
        for (int i = 1; i <= 6; i++)
        {
            HistoryColumnsWriter.RecordAccountV3(_historyColumns, TestItem.Addresses[i], 15, new Account((ulong)i, (ulong)i));
        }

        HistoryColumnsWriter.RecordStorageV3(_historyColumns, Address, 1, block: 1, [0x0a]);
        for (int i = 1; i <= 15; i++)
        {
            HistoryColumnsWriter.RecordStorageV3(_historyColumns, TestItem.Addresses[i], (UInt256)i, block: 15, [0x0b]);
        }

        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 20);

        using HistoryWindowPruner pruner = CreatePruner(retentionBlocks: 8);

        bool completed = false;
        for (int pass = 0; pass < 8 && !completed; pass++)
        {
            completed = pruner.RunOnePass(CancellationToken.None, new CountdownBudget(3));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(completed, Is.True,
                "23 rows under a shared budget of 3 per pass converge in 8 passes only if a column finished earlier in the cycle stays finished: rescanning its live rows from scratch would spend every later pass budget before storage could progress");
            Assert.That(_reader.IsPrunedBelowFloor(11), Is.True);
        }
    }

    [Test]
    public void RunOnePass_AFloorAdvanceMidCycle_HoldsEveryColumnToThePinnedFloorAndQueuesTheNextCycle()
    {
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, TestItem.Addresses[0], 1, new Account(0, 0));
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, 15, new Account(1, 1));
        HistoryColumnsWriter.RecordStorageV3(_historyColumns, Address, 1, block: 15, [0x0a]);
        for (int i = 1; i <= 5; i++)
        {
            HistoryColumnsWriter.RecordStorageV3(_historyColumns, TestItem.Addresses[i], (UInt256)i, block: 25, [0x0b]);
        }

        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 20);

        using HistoryWindowPruner pruner = CreatePruner(retentionBlocks: 8);
        bool firstPass = pruner.RunOnePass(CancellationToken.None, new CountdownBudget(3));

        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 30);
        bool completingPass = pruner.RunOnePass(CancellationToken.None, new CountdownBudget(100));

        int accountRowsAfterPinnedCycle = CountRows(FlatHistoryColumns.AccountHistory);
        int storageRowsAfterPinnedCycle = CountRows(FlatHistoryColumns.StorageHistory);

        bool queuedCycle = pruner.RunOnePass(CancellationToken.None, new CountdownBudget(100));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstPass, Is.False, "the first pass yields with the storage column mid-scan");
            Assert.That(completingPass, Is.False,
                "a cycle completing while the live floor is already ahead of its pinned one reports yielded, so the loop starts the next cycle without waiting for another watermark event");
            Assert.That(accountRowsAfterPinnedCycle, Is.EqualTo(1),
                "the finished account column holds its block-15 row through the pinned cycle");
            Assert.That(storageRowsAfterPinnedCycle, Is.EqualTo(6),
                "the unfinished storage column sweeps to the pinned floor, not the live one");
            Assert.That(queuedCycle, Is.True, "the queued cycle pins the advanced floor and finishes against it");
            Assert.That(CountRows(FlatHistoryColumns.AccountHistory), Is.EqualTo(0));
            Assert.That(CountRows(FlatHistoryColumns.StorageHistory), Is.EqualTo(5));
        }
    }

    private int CountRows(FlatHistoryColumns column)
    {
        int count = 0;
        foreach (KeyValuePair<byte[], byte[]> _ in _historyColumns.GetColumnDb(column).GetAll()) count++;
        return count;
    }

    private int CountBlockMarkers()
    {
        int count = 0;
        foreach (KeyValuePair<byte[], byte[]> row in _historyColumns.GetColumnDb(FlatHistoryColumns.AvailableBlocks).GetAll())
        {
            if (row.Key.Length == sizeof(ulong)) count++;
        }

        return count;
    }

    [Test]
    public void Start_WithNoWatermarkEventSinceStartup_StillRunsAFirstPassAndPublishesTheFloor()
    {
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, 0, new Account(0, 0));
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 20);

        HistoryWindowPruner pruner = CreatePruner(retentionBlocks: 8);
        using ManualResetEventSlim firstPass = new();
        pruner.PassCompleted += firstPass.Set;
        pruner.Start();

        Assert.That(firstPass.Wait(TimeSpan.FromSeconds(10)), Is.True,
            "a restarted node must not wait for the first persistence flush before pruning");
        Assert.That(_reader.IsPrunedBelowFloor(11), Is.True);

        pruner.Dispose();
    }

    [Test]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        HistoryWindowPruner pruner = CreatePruner(retentionBlocks: 8);
        pruner.Dispose();

        Assert.That(pruner.Dispose, Throws.Nothing);
    }

    [Test]
    public void RunOnePass_WithTheBudgetExhaustedInTheAccountSweep_LeavesTheLaterColumnsToLaterPasses()
    {
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, 0, new Account(0, 0));
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, 5, new Account(1, 100));
        HistoryColumnsWriter.RecordStorageV3(_historyColumns, Address, Slot, 5, [0xAA]);
        HistoryColumnsWriter.MarkBlockV3(_historyColumns, 11, ValueKeccak.Compute("below"u8));
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 20);

        using HistoryWindowPruner pruner = CreatePruner(retentionBlocks: 8); // floor = 12
        bool firstPass = pruner.RunOnePass(CancellationToken.None, new CountdownBudget(rowsBeforeExhaustion: 1));

        int accountRowsAfterFirstPass = CountRows(FlatHistoryColumns.AccountHistory);
        int storageRowsAfterFirstPass = CountRows(FlatHistoryColumns.StorageHistory);
        int markersAfterFirstPass = CountBlockMarkers();

        int passes = 1;
        bool completed = firstPass;
        while (!completed && passes < 10)
        {
            completed = pruner.RunOnePass(CancellationToken.None, new CountdownBudget(rowsBeforeExhaustion: 1));
            passes++;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstPass, Is.False, "the budget runs out on the second account row");
            Assert.That(accountRowsAfterFirstPass, Is.EqualTo(1), "the one row the budget allowed is deleted, block 5 waits for the next pass");
            Assert.That(storageRowsAfterFirstPass, Is.EqualTo(1), "the pass budget is shared, so storage must not start once accounts exhausted it");
            Assert.That(markersAfterFirstPass, Is.EqualTo(1), "nor must the block markers");
            Assert.That(completed, Is.True, "later passes resume every column from its cursor and finish the cycle");
            Assert.That(passes, Is.EqualTo(4), "one budgeted row per pass across the columns: two accounts, one storage row, one marker");
            Assert.That(CountRows(FlatHistoryColumns.AccountHistory), Is.EqualTo(0));
            Assert.That(CountRows(FlatHistoryColumns.StorageHistory), Is.EqualTo(0));
            Assert.That(CountBlockMarkers(), Is.EqualTo(0));
        }
    }

    [Test]
    public void PruneClearsColumn_KeepsOnlyTheNewestBelowFloorClearPerAccount_AndItAloneStillAnswersAQuery()
    {
        StorageClearStore clears = new(_historyColumns.GetColumnDb(FlatHistoryColumns.StorageClears));
        Span<byte> accountKeyBuffer = stackalloc byte[HistoryKeyLayout.AccountKeyLength];
        byte[] flatAccountKey = Address.ToAccountPath.Bytes.ToArray();

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

        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, new FlatDbConfig { HistoryEnabled = true, HistoryRetention = HistoryRetentionMode.Rolling, HistoryRetentionBlocks = 8 });
        HistoryReader reader = new(_db, _historyColumns, availability, rowFormat, LimboLogs.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.IsAvailable(new StateId(12, rootAtFloor)), Is.True,
                "the marker at exactly the floor must survive so a read at the floor can validate its state root");
            Assert.That(reader.IsAvailable(new StateId(11, rootBelowFloor)), Is.False,
                "block 11 is below the floor and pruned regardless of its marker (reads there are refused before the marker is even consulted)");
        }
    }

    [Test]
    public void RunOnePass_KeysWithLongLiveTails_DeletesExactlyTheDeadRowsAndSeeksPastEachLiveTail()
    {
        // 1. A plain and a sliced address each hold dead rows below their floor and 40 live rows above it.
        // 2. One full pass with an unlimited budget that counts the rows it visits; only the account column has rows.
        // 3. Exactly the dead rows go, and each key is read only up to the live-row threshold.
        SeedLongLiveTails();
        using HistoryWindowPruner pruner = CreateLongLiveTailPruner();
        CountingBudget budget = new();

        bool completed = pruner.RunOnePass(CancellationToken.None, budget);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(completed, Is.True, "an unlimited budget finishes the cycle in one pass");
            AssertOnlyLiveTailRowsSurvive();
            Assert.That(budget.Checks, Is.EqualTo(2 * (LongTailDeadBlocks.Length + HistoryWindowPruner.LiveRowsBeforeSkip)),
                "the account sweep must read each key's dead rows plus the live-row threshold and seek past the rest of the live tail, not read every live row");
        }
    }

    [Test]
    public void RunOnePass_YieldingInsideALiveTail_ResumesAndDeletesExactlyTheDeadRows()
    {
        // 1. Same rows as above; the first pass yields after 10 account rows, inside the first key's live tail.
        // 2. The next pass resumes from the cursor written there and finishes the sweep.
        // 3. Exactly the dead rows of both keys are gone and every live row is intact.
        SeedLongLiveTails();
        using HistoryWindowPruner pruner = CreateLongLiveTailPruner();

        bool yieldedPass = pruner.RunOnePass(CancellationToken.None, new CountdownBudget(rowsBeforeExhaustion: 10));
        bool resumedPass = pruner.RunOnePass(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(yieldedPass, Is.False, "precondition: the first pass yields with the account column mid-scan");
            Assert.That(resumedPass, Is.True, "the resumed pass finishes the cycle");
            AssertOnlyLiveTailRowsSurvive();
        }
    }

    [Test]
    public void RunOnePass_ViewThatCannotSeek_ReadsEveryLiveRowAndDeletesExactlyTheDeadRows()
    {
        SeedLongLiveTails();
        using HistoryWindowPruner pruner = CreateLongLiveTailPruner(new NonSeekingHistoryColumns(_historyColumns));
        CountingBudget budget = new();

        bool completed = pruner.RunOnePass(CancellationToken.None, budget);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(completed, Is.True, "an unlimited budget finishes the cycle in one pass");
            AssertOnlyLiveTailRowsSurvive();
            Assert.That(budget.Checks, Is.EqualTo(2 * (LongTailDeadBlocks.Length + (int)(LongTailWatermark - LongTailGeneralFloor)) + SlicedRowsBelowGeneralFloor.Length),
                "a view without ISeekableSortedView falls back to reading every row of the account sweep");
        }
    }

    private void SeedLongLiveTails()
    {
        foreach (Address address in new[] { Address, SlicedAddress })
        {
            foreach (ulong block in LongTailDeadBlocks)
            {
                HistoryColumnsWriter.RecordAccountV3(_historyColumns, address, block, new Account(block, block));
            }

            for (ulong block = LongTailGeneralFloor + 1; block <= LongTailWatermark; block++)
            {
                HistoryColumnsWriter.RecordAccountV3(_historyColumns, address, block, new Account(block, block));
            }
        }

        foreach (ulong block in SlicedRowsBelowGeneralFloor)
        {
            HistoryColumnsWriter.RecordAccountV3(_historyColumns, SlicedAddress, block, new Account(block, block));
        }

        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, LongTailWatermark);
    }

    // General floor 100 - 40 = 60; the slice keeps 70 blocks, so its own floor is 30.
    private HistoryWindowPruner CreateLongLiveTailPruner(IColumnsDb<FlatHistoryColumns>? prunerHistory = null)
    {
        HistoryWindowPruner pruner = CreatePruner(retentionBlocks: LongTailWatermark - LongTailGeneralFloor,
            configure: config => config.HistorySliceAddresses = $"{SlicedAddress}:70", prunerHistory: prunerHistory);
        pruner.ReconcileSliceScopes();
        return pruner;
    }

    private void AssertOnlyLiveTailRowsSurvive()
    {
        List<ulong> expectedPlain = [];
        for (ulong block = LongTailGeneralFloor + 1; block <= LongTailWatermark; block++) expectedPlain.Add(block);
        List<ulong> expectedSliced = [.. SlicedRowsBelowGeneralFloor, .. expectedPlain];

        Assert.That(SurvivingAccountBlocks(Address), Is.EqualTo(expectedPlain),
            "the plain address keeps exactly its rows above the general floor");
        Assert.That(SurvivingAccountBlocks(SlicedAddress), Is.EqualTo(expectedSliced),
            "the sliced address keeps its rows above its own deeper floor, including those below the general floor");
    }

    private List<ulong> SurvivingAccountBlocks(Address address)
    {
        byte[] flatKey = address.ToAccountPath.Bytes.ToArray();
        List<ulong> blocks = [];
        foreach (KeyValuePair<byte[], byte[]> row in _historyColumns.GetColumnDb(FlatHistoryColumns.AccountHistory).GetAll())
        {
            if (row.Key.Length == flatKey.Length + sizeof(ulong) && row.Key.AsSpan(0, flatKey.Length).SequenceEqual(flatKey))
            {
                blocks.Add(BinaryPrimitives.ReadUInt64BigEndian(row.Key.AsSpan(flatKey.Length)));
            }
        }

        blocks.Sort();
        return blocks;
    }

    private void RecordClear(StorageClearStore clears, byte[] accountKey, ulong block)
    {
        using IColumnsWriteBatch<FlatHistoryColumns> batch = _historyColumns.StartWriteBatch();
        clears.RecordClear(block, accountKey, batch.GetColumnBatch(FlatHistoryColumns.StorageClears));
    }

    private HistoryWindowPruner CreatePruner(ulong retentionBlocks, int passBudgetSeconds = 30, Action<FlatDbConfig>? configure = null, HistoryScopeGate? scopeGate = null,
        IColumnsDb<FlatHistoryColumns>? prunerHistory = null)
    {
        FlatDbConfig config = new()
        {
            HistoryEnabled = true,
            HistoryRetention = retentionBlocks > 0 ? HistoryRetentionMode.Rolling : HistoryRetentionMode.None,
            HistoryRetentionBlocks = retentionBlocks,
            HistoryPruneIntervalBlocks = 1,
            HistoryPrunePassBudgetSeconds = passBudgetSeconds
        };
        configure?.Invoke(config);
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, config);
        _writer = new HistoryWriter(_db, _historyColumns, config, availability, rowFormat, LimboLogs.Instance, commitments: null);
        _reader = new HistoryReader(_db, _historyColumns, availability, rowFormat, LimboLogs.Instance);
        return new HistoryWindowPruner(
            _writer, prunerHistory ?? _historyColumns, config,
            scopeGate ?? new HistoryScopeGate(),
            availability, rowFormat,
            LimboLogs.Instance,
            new TransactionChangesetIndex(_historyColumns, config));
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

    private sealed class CountingBudget : IPruneBudget
    {
        public int Checks { get; private set; }

        public bool Exhausted
        {
            get
            {
                Checks++;
                return false;
            }
        }
    }

    private sealed class NonSeekingHistoryColumns(IColumnsDb<FlatHistoryColumns> inner) : IColumnsDb<FlatHistoryColumns>
    {
        public IDb GetColumnDb(FlatHistoryColumns key) => new NonSeekingDb(inner.GetColumnDb(key));
        public IColumnsWriteBatch<FlatHistoryColumns> StartWriteBatch() => inner.StartWriteBatch();
        public IEnumerable<FlatHistoryColumns> ColumnKeys => inner.ColumnKeys;
        public IColumnDbSnapshot<FlatHistoryColumns> CreateSnapshot() => inner.CreateSnapshot();
        public void Flush(bool onlyWal = false) => inner.Flush(onlyWal);
        public void SyncWal() => inner.SyncWal();

        public void Dispose() { }
    }

    private sealed class NonSeekingDb(IDb inner) : IDb, ISortedKeyValueStore
    {
        private ISortedKeyValueStore Sorted => (ISortedKeyValueStore)inner;

        public byte[]? FirstKey => Sorted.FirstKey;
        public byte[]? LastKey => Sorted.LastKey;

        public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive, ReadFlags flags = ReadFlags.None) =>
            new ForwardOnlyView(Sorted.GetViewBetween(firstKeyInclusive, lastKeyExclusive, flags));

        public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => inner.Get(key, flags);
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

    /// <summary>A view as implemented before <see cref="ISeekableSortedView"/> existed.</summary>
    private sealed class ForwardOnlyView(ISortedView inner) : ISortedView
    {
        public bool StartBefore(ReadOnlySpan<byte> value) => inner.StartBefore(value);
        public bool MoveNext() => inner.MoveNext();
        public ReadOnlySpan<byte> CurrentKey => inner.CurrentKey;
        public ReadOnlySpan<byte> CurrentValue => inner.CurrentValue;
        public void Dispose() => inner.Dispose();
    }

    private static byte[] AccountKey()
    {
        Span<byte> buffer = stackalloc byte[HistoryKeyLayout.AccountKeyLength];
        return Address.ToAccountPath.Bytes.ToArray();
    }
}
