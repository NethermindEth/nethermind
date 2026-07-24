// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class StorageClearStoreTests
{
    private static readonly byte[] AccountA = [1, 2, 3, 4];
    private static readonly byte[] AccountB = [9, 9, 9, 9];

    private SnapshotableMemColumnsDb<FlatHistoryColumns> _columnsDb = null!;
    private StorageClearStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _columnsDb = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _store = new StorageClearStore(_columnsDb.GetColumnDb(FlatHistoryColumns.StorageClears));
    }

    [TearDown]
    public void TearDown() => _columnsDb.Dispose();

    // AccountA cleared at block 10.
    [TestCase(5ul, 9ul, false, TestName = "RangeEndsBeforeClear")]
    [TestCase(5ul, 10ul, true, TestName = "ClearAtInclusiveUpperBound")]
    [TestCase(5ul, 15ul, true, TestName = "ClearInsideRange")]
    [TestCase(10ul, 15ul, false, TestName = "ClearAtExclusiveLowerBound_SameBlockWriteSurvives")]
    [TestCase(11ul, 15ul, false, TestName = "RangeStartsAfterClear")]
    [TestCase(15ul, 15ul, false, TestName = "EmptyRange")]
    public void HasClearInRange_WithSingleClear_HonorsRangeBounds(ulong afterExclusive, ulong atOrBefore, bool expected)
    {
        RecordClear(10, AccountA);

        Assert.That(_store.HasClearInRange(AccountA, afterExclusive, atOrBefore), Is.EqualTo(expected),
            $"clear at block 10 should {(expected ? "" : "not ")}fall in ({afterExclusive}, {atOrBefore}]");
    }

    [Test]
    public void HasClearInRange_WithClearOnOtherAccount_DoesNotBleedAcrossAccounts()
    {
        RecordClear(10, AccountB);

        Assert.That(_store.HasClearInRange(AccountA, 5, 15), Is.False,
            "a clear recorded for another account must not affect this account");
    }

    [Test]
    public void HasClearInRange_WithMultipleClears_FindsTheOneInsideTheRange()
    {
        RecordClear(10, AccountA);
        RecordClear(30, AccountA);

        Assert.That(_store.HasClearInRange(AccountA, 15, 25), Is.False,
            "neither clear (10, 30) falls in (15, 25]");
        Assert.That(_store.HasClearInRange(AccountA, 25, 35), Is.True,
            "the clear at 30 falls in (25, 35]");
    }

    private void RecordClear(ulong block, byte[] accountKey)
    {
        using IColumnsWriteBatch<FlatHistoryColumns> batch = _columnsDb.StartWriteBatch();
        _store.RecordClear(block, accountKey, batch.GetColumnBatch(FlatHistoryColumns.StorageClears));
    }
}
