// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

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

    [Test]
    public void RunOnePass_WithASweepStillPending_StillAdvancesTheFloorToTheCurrentWatermark()
    {
        for (ulong block = 0; block <= 60; block += 5)
        {
            HistoryColumnsWriter.RecordAccountV3(_historyColumns, Address, block, new Account(block, block * 10));
        }

        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 20);
        HistoryWindowPruner first = CreatePruner(retentionBlocks: 8);
        first.RunOnePass(CancellationToken.None, () => new CountdownBudget(rowsBeforeExhaustion: 1));
        first.Dispose();

        Assert.That(_reader.IsPrunedBelowFloor(11), Is.True, "precondition: the first pass published floor 12 and yielded mid-column");

        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 60);
        HistoryWindowPruner second = CreatePruner(retentionBlocks: 8);
        second.RunOnePass(CancellationToken.None, () => new CountdownBudget(rowsBeforeExhaustion: 1));
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
        for (int pass = 0; pass < 7 && !completed; pass++)
        {
            completed = pruner.RunOnePass(CancellationToken.None, () => new CountdownBudget(3));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(completed, Is.True,
                "a sweep spread over budgeted passes must converge in about max(per-column chunks) passes: a column finished earlier in the cycle stays finished instead of rescanning its live rows from scratch on every pass until the columns happen to align");
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
        bool firstPass = pruner.RunOnePass(CancellationToken.None, () => new CountdownBudget(3));

        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 30);
        bool completingPass = pruner.RunOnePass(CancellationToken.None, () => new CountdownBudget(100));

        int accountRowsAfterPinnedCycle = CountRows(FlatHistoryColumns.AccountHistory);
        int storageRowsAfterPinnedCycle = CountRows(FlatHistoryColumns.StorageHistory);

        bool queuedCycle = pruner.RunOnePass(CancellationToken.None, () => new CountdownBudget(100));

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

    private void RecordClear(StorageClearStore clears, byte[] accountKey, ulong block)
    {
        using IColumnsWriteBatch<FlatHistoryColumns> batch = _historyColumns.StartWriteBatch();
        clears.RecordClear(block, accountKey, batch.GetColumnBatch(FlatHistoryColumns.StorageClears));
    }

    private HistoryWindowPruner CreatePruner(ulong retentionBlocks, int passBudgetSeconds = 30, Action<FlatDbConfig>? configure = null, HistoryScopeGate? scopeGate = null)
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
        _writer = new HistoryWriter(_db, _historyColumns, config, availability, rowFormat, LimboLogs.Instance);
        _reader = new HistoryReader(_db, _historyColumns, availability, rowFormat, LimboLogs.Instance);
        return new HistoryWindowPruner(
            _writer, _historyColumns, config,
            scopeGate ?? new HistoryScopeGate(),
            availability, rowFormat,
            LimboLogs.Instance);
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
        return Address.ToAccountPath.Bytes.ToArray();
    }

    private static byte[] StorageKey()
    {
        ValueHash256 slotHash = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(Slot, ref slotHash);
        Span<byte> buffer = stackalloc byte[BaseFlatPersistence.StorageKeyLength];
        return BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(buffer, Address.ToAccountPath, slotHash).ToArray();
    }
}
