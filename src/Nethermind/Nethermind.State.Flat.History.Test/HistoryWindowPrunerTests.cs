// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

public class HistoryWindowPrunerTests
{
    private static readonly Address Address = TestItem.AddressA;
    private static readonly UInt256 Slot = 1;

    private SnapshotableMemColumnsDb<FlatDbColumns> _db = null!;
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;
    private HistoryStore _accountHistory = null!;
    private HistoryReader _reader = null!;
    private HistoryWriter _writer = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _writer = new HistoryWriter(_db, _historyColumns, new FlatDbConfig { HistoryEnabled = true }, LimboLogs.Instance);
        _reader = new HistoryReader(_db, _historyColumns, LimboLogs.Instance);
        _accountHistory = new HistoryStore(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountHistory), LimboLogs.Instance.GetClassLogger<HistoryStore>());
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        _historyColumns.Dispose();
    }

    [Test]
    public void RunOnePass_KeepsNewestVersionAtOrBelowFloor_DeletesStrictlyOlderVersions()
    {
        HistoryColumnsWriter.RecordAccount(_historyColumns, Address, 0, new Account(0, 0));
        HistoryColumnsWriter.RecordAccount(_historyColumns, Address, 5, new Account(5, 500));
        HistoryColumnsWriter.RecordAccount(_historyColumns, Address, 10, new Account(10, 1000));
        HistoryColumnsWriter.RecordAccount(_historyColumns, Address, 15, new Account(15, 1500));
        HistoryColumnsWriter.RecordAccount(_historyColumns, Address, 20, new Account(20, 2000));
        HistoryColumnsWriter.SetWatermark(_historyColumns, 20);

        HistoryWindowPruner pruner = CreatePruner(retentionBlocks: 8);
        Assert.That(_writer.LastCapturedBlock, Is.EqualTo(20UL), "precondition: watermark set to 20");

        pruner.RunOnePass(CancellationToken.None);

        Span<byte> buffer = stackalloc byte[256];
        ReadOnlySpan<byte> flatKey = AccountKey();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_accountHistory.TryGetAt(12, flatKey, buffer), Is.GreaterThan(0),
                "the floor (20 - 8 = 12) must still resolve from the retained newest-at-or-below-floor row (block 10)");
            Assert.That(_accountHistory.TryGetAt(5, flatKey, buffer), Is.EqualTo(-1),
                "rows strictly older than the retained floor answer (blocks 0 and 5) must be deleted");
            Assert.That(_accountHistory.TryGetAt(0, flatKey, buffer), Is.EqualTo(-1),
                "rows strictly older than the retained floor answer (blocks 0 and 5) must be deleted");
            Assert.That(_accountHistory.TryGetAt(20, flatKey, buffer), Is.GreaterThan(0),
                "rows above the floor are always retained");
        }

        pruner.Dispose();
    }

    [Test]
    public void RunOnePass_PublishesGlobalFloor_MatchingWatermarkMinusRetention()
    {
        HistoryColumnsWriter.RecordAccount(_historyColumns, Address, 0, new Account(0, 0));
        HistoryColumnsWriter.SetWatermark(_historyColumns, 20);

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
    public void RunOnePass_WhenBackfillInterlockIsActive_IsANoOp()
    {
        HistoryColumnsWriter.RecordAccount(_historyColumns, Address, 0, new Account(0, 0));
        HistoryColumnsWriter.RecordAccount(_historyColumns, Address, 5, new Account(5, 500));
        HistoryColumnsWriter.SetWatermark(_historyColumns, 20);

        HistoryWindowPruner pruner = CreatePruner(retentionBlocks: 8, interlock: new AlwaysActiveBackfillInterlock());
        pruner.RunOnePass(CancellationToken.None);

        Span<byte> buffer = stackalloc byte[256];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_reader.IsPrunedBelowFloor(0), Is.False, "a pass must not publish a floor while backfill is active");
            Assert.That(_accountHistory.TryGetAt(5, AccountKey(), buffer), Is.GreaterThan(0), "a pass must not delete any row while backfill is active");
        }

        pruner.Dispose();
    }

    [Test]
    public void RunOnePass_WithAnExhaustedBudget_YieldsMidColumnAndResumesFromCursorOnTheNextPass()
    {
        HistoryColumnsWriter.RecordAccount(_historyColumns, Address, 0, new Account(0, 0));
        HistoryColumnsWriter.RecordAccount(_historyColumns, Address, 5, new Account(5, 500));
        HistoryColumnsWriter.RecordAccount(_historyColumns, Address, 10, new Account(10, 1000));
        HistoryColumnsWriter.SetWatermark(_historyColumns, 20);

        // Floor 12: the newest-at-or-below-floor row (block 10) is examined and kept without exhausting the
        // budget; the very next row (block 5) hits it and is where the pass yields — proving the cursor lands
        // mid-column, not merely at the first key, and that resuming continues past it rather than restarting.
        HistoryWindowPruner exhausted = CreatePruner(retentionBlocks: 8);
        exhausted.RunOnePass(CancellationToken.None, () => new CountdownBudget(rowsBeforeExhaustion: 1));
        exhausted.Dispose();

        Span<byte> buffer = stackalloc byte[256];
        Assert.That(_accountHistory.TryGetAt(0, AccountKey(), buffer), Is.GreaterThan(0),
            "precondition: the oldest row must still be present after the pass yielded before reaching it");

        HistoryWindowPruner ample = CreatePruner(retentionBlocks: 8);
        ample.RunOnePass(CancellationToken.None);
        ample.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_accountHistory.TryGetAt(0, AccountKey(), buffer), Is.EqualTo(-1),
                "resuming from the persisted mid-column cursor must reach and delete the oldest row the first pass never got to");
            Assert.That(_reader.TryGetAccount(12, Address, out AccountStruct atFloor), Is.True,
                "the floor must still resolve correctly after a resumed pass");
            Assert.That(atFloor.Nonce, Is.EqualTo(10UL));
        }
    }

    // Regression for round-robin independence: account has two versions (needs two budget checks to finish its
    // one key), storage has one (needs only one). Giving every column the same "one check" shaped budget must let
    // storage complete while account yields — proving storage's progress does not wait behind account finishing,
    // which the original completedAccount && PruneVersionedColumn(storage...) chain would have serialized.
    [Test]
    public void RunOnePass_StorageColumnMakesProgressEvenWhenAccountColumnDoesNotComplete()
    {
        HistoryColumnsWriter.RecordAccount(_historyColumns, Address, 0, new Account(0, 0));
        HistoryColumnsWriter.RecordAccount(_historyColumns, Address, 5, new Account(5, 500));
        HistoryColumnsWriter.RecordStorage(_historyColumns, Address, Slot, 5, [0xAA]);
        HistoryColumnsWriter.SetWatermark(_historyColumns, 20);

        HistoryWindowPruner pruner = CreatePruner(retentionBlocks: 8);
        pruner.RunOnePass(CancellationToken.None, () => new CountdownBudget(rowsBeforeExhaustion: 1));
        pruner.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Span<byte> buffer = stackalloc byte[256];
            Assert.That(_accountHistory.TryGetAt(0, AccountKey(), buffer), Is.GreaterThan(0),
                "account needs two checks for its one key and only got one — it must not have completed");
            Assert.That(_reader.TryGetStorage(12, Address, Slot, out SlotValue value), Is.True,
                "storage needs only one check for its single row — it must complete in the same pass regardless of account's progress");
            Assert.That(value.AsReadOnlySpan.WithoutLeadingZeros().ToArray(), Is.EqualTo(new byte[] { 0xAA }));
        }
    }

    private HistoryWindowPruner CreatePruner(ulong retentionBlocks, int passBudgetSeconds = 30, IBackfillInterlock? interlock = null) =>
        new(_writer, _historyColumns,
            new FlatDbConfig { HistoryEnabled = true, HistoryRetentionBlocks = retentionBlocks, HistoryPruneIntervalBlocks = 1, HistoryPrunePassBudgetSeconds = passBudgetSeconds },
            interlock ?? NullBackfillInterlock.Instance,
            new HistoryScopeGate(),
            LimboLogs.Instance);

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
        Span<byte> buffer = stackalloc byte[BaseFlatPersistence.AccountKeyLength];
        return BaseFlatPersistence.EncodeAccountKeyHashed(buffer, Address.ToAccountPath).ToArray();
    }

    private sealed class AlwaysActiveBackfillInterlock : IBackfillInterlock
    {
        public bool IsBackfillActive => true;
    }
}
