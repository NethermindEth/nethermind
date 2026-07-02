// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class StorageClearBackfillTests
{
    private static readonly Address AddrA = TestItem.AddressA;
    private static readonly UInt256 Slot1 = 1;

    private SnapshotableMemColumnsDb<FlatDbColumns> _db = null!;
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;
    private HistoryReader _reader = null!;
    private StorageClearBackfill _backfill = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _reader = new HistoryReader(_db, _historyColumns, LimboLogs.Instance);
        _backfill = new StorageClearBackfill(_historyColumns, static () => true, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _backfill.Dispose();
        _db.Dispose();
        _historyColumns.Dispose();
    }

    [Test]
    public void RunScan_WithPreFixHistoryContainingDeletionTombstone_SynthesizesClearAndFixesReads()
    {
        // Pre-fix history: slot written @1, account destructed @2 (deletion tombstone captured, clear marker NOT).
        RecordAccountChange(1, AddrA, EncodedAccount(new Account(1, 100)));
        RecordStorageChange(1, AddrA, Slot1, [0x0a]);
        RecordAccountChange(2, AddrA, ReadOnlySpan<byte>.Empty);

        Assert.That(TryGetStorage(3, out SlotValue stale), Is.True, "precondition: pre-fix history leaks the stale slot");
        Assert.That(stale.AsReadOnlySpan.WithoutLeadingZeros().ToArray(), Is.EqualTo(new byte[] { 0x0a }),
            "precondition: the leak resolves to the pre-destruct value");

        _backfill.RunScan(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(TryGetStorage(1, out SlotValue live), Is.True, "the pre-destruct read must survive the repair");
            Assert.That(live.AsReadOnlySpan.WithoutLeadingZeros().ToArray(), Is.EqualTo(new byte[] { 0x0a }),
                "the pre-destruct value must be unchanged");
            Assert.That(TryGetStorage(2, out _), Is.False, "the destruct block itself must read empty");
            Assert.That(TryGetStorage(3, out _), Is.False, "post-destruct reads must be empty after the repair");
            Assert.That(_backfill.IsCompleted, Is.True, "a full scan must mark completion");
        }
    }

    [Test]
    public void RunScan_WithEmptyHistory_MarksCompletedWithoutScanning()
    {
        _backfill.RunScan(CancellationToken.None);

        Assert.That(_backfill.IsCompleted, Is.True, "an empty history has nothing to repair and must complete immediately");
    }

    [Test]
    public void RunScan_WhenAlreadyCompleted_IsIdempotent()
    {
        RecordAccountChange(1, AddrA, EncodedAccount(new Account(1, 100)));
        RecordStorageChange(1, AddrA, Slot1, [0x0a]);
        RecordAccountChange(2, AddrA, ReadOnlySpan<byte>.Empty);

        _backfill.RunScan(CancellationToken.None);
        Assert.That(_backfill.IsCompleted, Is.True, "precondition: first scan completes");

        _backfill.RunScan(CancellationToken.None);

        Assert.That(TryGetStorage(3, out _), Is.False, "reads stay correct after a redundant re-run");
    }

    [Test]
    public void RunScan_WhenCancelled_DoesNotMarkCompletedAndResumes()
    {
        RecordAccountChange(1, AddrA, ReadOnlySpan<byte>.Empty);

        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        _backfill.RunScan(cancelled.Token);

        Assert.That(_backfill.IsCompleted, Is.False, "an interrupted scan must not mark completion");

        _backfill.RunScan(CancellationToken.None);

        Assert.That(_backfill.IsCompleted, Is.True, "the next run must finish the repair");
    }

    private bool TryGetStorage(ulong block, out SlotValue value) => _reader.TryGetStorage(block, AddrA, Slot1, out value);

    private void RecordAccountChange(ulong block, Address address, ReadOnlySpan<byte> value)
    {
        HistoryStore accountHistory = new(
            _historyColumns.GetColumnDb(FlatHistoryColumns.AccountHistory),
            _historyColumns.GetColumnDb(FlatHistoryColumns.AccountChangeSets));

        Span<byte> key = stackalloc byte[BaseFlatPersistence.AccountKeyLength];
        using IColumnsWriteBatch<FlatHistoryColumns> batch = _historyColumns.StartWriteBatch();
        accountHistory.RecordChange(
            block, BaseFlatPersistence.EncodeAccountKeyHashed(key, address.ToAccountPath), value,
            batch.GetColumnBatch(FlatHistoryColumns.AccountHistory),
            batch.GetColumnBatch(FlatHistoryColumns.AccountChangeSets));
    }

    private void RecordStorageChange(ulong block, Address address, UInt256 slot, byte[] rawValue)
    {
        HistoryStore storageHistory = new(
            _historyColumns.GetColumnDb(FlatHistoryColumns.StorageHistory),
            _historyColumns.GetColumnDb(FlatHistoryColumns.StorageChangeSets));

        ValueHash256 slotHash = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(slot, ref slotHash);
        Span<byte> key = stackalloc byte[BaseFlatPersistence.StorageKeyLength];
        Span<byte> value = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];
        int written = BaseFlatPersistence.EncodeSlotValue(SlotValue.FromSpanWithoutLeadingZero(rawValue), rlpWrapSlots: true, value);

        using IColumnsWriteBatch<FlatHistoryColumns> batch = _historyColumns.StartWriteBatch();
        storageHistory.RecordChange(
            block, BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(key, address.ToAccountPath, slotHash), value[..written],
            batch.GetColumnBatch(FlatHistoryColumns.StorageHistory),
            batch.GetColumnBatch(FlatHistoryColumns.StorageChangeSets));
    }

    private static byte[] EncodedAccount(Account account)
    {
        using ArrayPoolSpan<byte> rlp = AccountDecoder.Slim.EncodeToArrayPoolSpan(account);
        return ((ReadOnlySpan<byte>)rlp).ToArray();
    }
}
