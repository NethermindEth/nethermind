// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.State.Flat.History.Proofs;
using Nethermind.State.Flat.History.Changesets;
using Nethermind.State.Flat.History.Walk;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

[TestFixture]
public class HistoryRowScannerTests
{
    [Test]
    public void ScratchImport_WhenWalSyncFails_RequiresDurableRecoveryBeforeReportingCompletion()
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> source = new();
        using FailingWalScratchDb scratch = new();
        HistoryRowFormat format = HistoryColumnsWriter.CreateSharedFormat(source, new FlatDbConfig()).RowFormat;
        BulkFillScratchState state = new(scratch, Keccak.EmptyTreeHash, 0);
        scratch.IsWalFailureEnabled = true;

        Assert.Throws<IOException>(() => state.ImportPage(Store(source, FlatHistoryColumns.AccountHistory), format,
            FlatHistoryColumns.AccountHistory, 1, CancellationToken.None));
        Assert.Throws<IOException>(() => new BulkFillScratchState(scratch, Keccak.EmptyTreeHash, 0),
            "a visible completed checkpoint must not bypass the failed durability barrier on reopen");
        scratch.IsWalFailureEnabled = false;
        int syncs = scratch.SuccessfulSyncs;
        BulkFillScratchState recovered = new(scratch, Keccak.EmptyTreeHash, 0);
        HistoricalStateScan.Page page = recovered.ImportPage(Store(source, FlatHistoryColumns.AccountHistory), format,
            FlatHistoryColumns.AccountHistory, 1, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.Complete, Is.True);
            Assert.That(scratch.SuccessfulSyncs, Is.GreaterThan(syncs), "successful recovery must establish durability before accepting completion");
        }
    }

    [Test]
    public void ScratchImport_WhenReopened_ResumesCommittedPages(
        [Values(FlatHistoryColumns.AccountHistory, FlatHistoryColumns.StorageHistory, FlatHistoryColumns.StorageClears)] FlatHistoryColumns column)
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> source = new();
        using SnapshotableMemColumnsDb<BulkFillScratchState.Columns> scratch = new();
        HistoryRowFormat format = HistoryColumnsWriter.CreateSharedFormat(source, new FlatDbConfig()).RowFormat;
        int keyLength = column == FlatHistoryColumns.StorageHistory ? BaseFlatPersistence.StorageKeyLength : Hash256.Size;
        byte[] key = ScanKey(keyLength, 1);
        RecordScanRow(source.GetColumnDb(column), column, key, 5, column == FlatHistoryColumns.StorageClears ? [] : [5]);
        RecordScanRow(source.GetColumnDb(column), column, key, 10, column == FlatHistoryColumns.StorageClears ? [] : [10]);
        BulkFillScratchState state = new(scratch, Keccak.EmptyTreeHash, 10);
        HistoricalStateScan.Page first = state.ImportPage(Store(source, column), format, column, 1, CancellationToken.None);
        Assert.That(first.Complete, Is.False, "the first page must leave a resumable checkpoint");

        state = new BulkFillScratchState(scratch, Keccak.EmptyTreeHash, 10);
        HistoricalStateScan.Page last = state.ImportPage(Store(source, column), format, column, 2, CancellationToken.None);
        BulkFillScratchState.Columns target = column switch
        {
            FlatHistoryColumns.AccountHistory => BulkFillScratchState.Columns.Accounts,
            FlatHistoryColumns.StorageHistory => BulkFillScratchState.Columns.Storage,
            _ => BulkFillScratchState.Columns.Clears,
        };
        byte[] expected = column == FlatHistoryColumns.AccountHistory ? [10] : new byte[sizeof(ulong) + (column == FlatHistoryColumns.StorageHistory ? 1 : 0)];
        if (column != FlatHistoryColumns.AccountHistory) BinaryPrimitives.WriteUInt64BigEndian(expected, 10);
        if (column == FlatHistoryColumns.StorageHistory) expected[^1] = 10;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(last.Complete, Is.True);
            Assert.That(last.Scanned, Is.EqualTo(1), "restart must not reread the committed row");
            Assert.That(scratch.GetColumnDb(target)[key], Is.EqualTo(expected), "resuming within a key must retain the selected version");
            Assert.That(state.ImportPage(Store(source, column), format, column, 1, CancellationToken.None).Scanned, Is.Zero, "completed imports must not restart");
        }
    }

    [Test]
    public void ScratchImport_WhenScanFails_DiscardsRowsAndCheckpoint()
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> source = new();
        using SnapshotableMemColumnsDb<BulkFillScratchState.Columns> scratch = new();
        HistoryRowFormat format = HistoryColumnsWriter.CreateSharedFormat(source, new FlatDbConfig()).RowFormat;
        IDb history = source.GetColumnDb(FlatHistoryColumns.AccountHistory);
        byte[] key = ScanKey(Hash256.Size, 1);
        RecordScanRow(history, FlatHistoryColumns.AccountHistory, key, 0, [1]);
        byte[] malformed = ScanKey(Hash256.Size, 2);
        history.PutSpan(malformed, [2]);
        BulkFillScratchState state = new(scratch, Keccak.EmptyTreeHash, 0);

        Assert.Throws<InvalidDataException>(() => state.ImportPage((ISortedKeyValueStore)history, format, FlatHistoryColumns.AccountHistory, 2, CancellationToken.None));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(scratch.GetColumnDb(BulkFillScratchState.Columns.Accounts)[key], Is.Null, "a callback before the error must not leak a partial page");
            Assert.That(scratch.GetColumnDb(BulkFillScratchState.Columns.Metadata)[new byte[] { (byte)BulkFillScratchState.Columns.Accounts }], Is.Null);
        }
        Assert.Throws<InvalidOperationException>(() => state.ImportPage((ISortedKeyValueStore)history, format, FlatHistoryColumns.AccountHistory, 1, CancellationToken.None));
        BulkFillScratchState reopened = new(scratch, Keccak.EmptyTreeHash, 0);
        Assert.That(reopened.ImportPage((ISortedKeyValueStore)history, format, FlatHistoryColumns.AccountHistory, 1, CancellationToken.None).Scanned, Is.EqualTo(1));
    }

    [Test]
    public void ScratchImport_WhenIdentityChanges_RefusesResume([Values] bool changeAnchor)
    {
        using SnapshotableMemColumnsDb<BulkFillScratchState.Columns> scratch = new();
        _ = new BulkFillScratchState(scratch, Keccak.EmptyTreeHash, 5);

        Assert.Throws<InvalidDataException>(() => new BulkFillScratchState(scratch,
            changeAnchor ? Keccak.EmptyTreeHash : Keccak.Zero, changeAnchor ? 6UL : 5UL));
    }

    [Test]
    public void ReadPage_WhenNoVersionExistsAtAnchor_EmitsNothing(
        [Values(FlatHistoryColumns.AccountHistory, FlatHistoryColumns.StorageHistory, FlatHistoryColumns.StorageClears)] FlatHistoryColumns column,
        [Values] bool empty)
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        HistoryRowFormat format = HistoryColumnsWriter.CreateSharedFormat(columns, new FlatDbConfig()).RowFormat;
        IDb source = columns.GetColumnDb(column);
        int keyLength = column == FlatHistoryColumns.StorageHistory ? BaseFlatPersistence.StorageKeyLength : Hash256.Size;
        if (!empty) RecordScanRow(source, column, ScanKey(keyLength, 0xFF), 1, []);
        HistoricalStateScan scanner = new((ISortedKeyValueStore)source, format, column, 0);
        int callbacks = 0;

        HistoricalStateScan.Page page = scanner.ReadPage(null, 2, (_, _, _) => callbacks++, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(callbacks, Is.Zero);
            Assert.That(page.Scanned, Is.EqualTo(empty ? 0 : 1));
            Assert.That(page.Complete, Is.True);
        }
    }

    [Test]
    public void ReadPage_WhenResumed_SelectsTheSameVersionsAsPointReads(
        [Values(FlatHistoryColumns.AccountHistory, FlatHistoryColumns.StorageHistory, FlatHistoryColumns.StorageClears)] FlatHistoryColumns column,
        [Values(1, 2, 5, 32)] int pageSize,
        [Values(0UL, 7UL, 15UL, 30UL)] ulong anchor)
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        HistoryRowFormat format = HistoryColumnsWriter.CreateSharedFormat(columns, new FlatDbConfig()).RowFormat;
        IDb source = columns.GetColumnDb(column);
        int keyLength = column == FlatHistoryColumns.StorageHistory ? BaseFlatPersistence.StorageKeyLength : Hash256.Size;
        byte[][] keys = [ScanKey(keyLength, 0), ScanKey(keyLength, 0x22), ScanKey(keyLength, 0xFF)];
        foreach (byte[] key in keys)
        {
            foreach (ulong block in new ulong[] { 0, 5, 10, 20 })
            {
                RecordScanRow(source, column, key, block, column == FlatHistoryColumns.StorageClears || block == 10 ? [] : [(byte)(block + 1)]);
            }
        }

        Dictionary<string, (ulong Block, byte[] Value)> selected = [];
        HistoricalStateScan.Cursor? cursor = null;
        int scanned = 0;
        int callbacks = 0;
        while (true)
        {
            HistoricalStateScan scanner = new((ISortedKeyValueStore)source, format, column, anchor);
            HistoricalStateScan.Page page = scanner.ReadPage(cursor, pageSize, (key, block, value) =>
            {
                callbacks++;
                selected[Convert.ToHexString(key)] = (block, value.ToArray());
            }, CancellationToken.None);
            Assert.That(page.Scanned, Is.LessThanOrEqualTo(pageSize), "a hot key must not overrun the raw-row page budget");
            scanned += page.Scanned;
            cursor = page.Position;
            if (page.Complete) break;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scanned, Is.EqualTo(12), "each source version is scanned exactly once across restarts");
            Assert.That(selected.Count, Is.EqualTo(keys.Length), "including tombstones and the all-FF identity");
            if (column != FlatHistoryColumns.StorageClears)
                Assert.That(callbacks, Is.EqualTo(keys.Length), "older versions must not overwrite the selected version after a page boundary");
        }

        foreach (byte[] key in keys)
        {
            (ulong block, byte[] value) = selected[Convert.ToHexString(key)];
            ulong expectedBlock = anchor >= 20 ? 20UL : anchor >= 10 ? 10UL : anchor >= 5 ? 5UL : 0UL;
            Assert.That(block, Is.EqualTo(expectedBlock), "a scan must select the last change at or below the anchor");
            if (column == FlatHistoryColumns.StorageClears) continue;

            HistoryStore pointReader = new(source, LimboLogs.Instance.GetClassLogger<HistoryStore>());
            byte[] expected = new byte[256];
            int length = pointReader.TryGetAt(anchor, key, expected, out ulong writtenAt);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(block, Is.EqualTo(writtenAt));
                Assert.That(value, Is.EqualTo(expected.AsSpan(0, length).ToArray()), "streaming selection must match the existing point lookup byte-for-byte");
            }
        }
    }

    [Test]
    public void ReadPage_WhenCancelledOrCallbackThrows_LeavesTheInputCursorReusable([Values] bool cancel)
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        HistoryRowFormat format = HistoryColumnsWriter.CreateSharedFormat(columns, new FlatDbConfig()).RowFormat;
        IDb source = columns.GetColumnDb(FlatHistoryColumns.AccountHistory);
        RecordScanRow(source, FlatHistoryColumns.AccountHistory, ScanKey(Hash256.Size, 1), 0, [1]);
        RecordScanRow(source, FlatHistoryColumns.AccountHistory, ScanKey(Hash256.Size, 2), 0, [2]);
        HistoricalStateScan scanner = new((ISortedKeyValueStore)source, format, FlatHistoryColumns.AccountHistory, 0);
        HistoricalStateScan.Cursor cursor = scanner.ReadPage(null, 1, (_, _, _) => { }, CancellationToken.None).Position!;
        byte[] original = cursor.Key.ToArray();
        using CancellationTokenSource cancellation = new();
        HistoricalStateScan.RowHandler fail = (_, _, _) =>
        {
            if (cancel) cancellation.Cancel();
            else throw new IOException("staging failed");
        };

        if (cancel)
            Assert.Throws<OperationCanceledException>(() => scanner.ReadPage(cursor, 1, fail, cancellation.Token));
        else
            Assert.Throws<IOException>(() => scanner.ReadPage(cursor, 1, fail, cancellation.Token));

        Assert.That(cursor.Key.ToArray(), Is.EqualTo(original), "failure must not mutate the caller's durable checkpoint");
        int replayed = 0;
        HistoricalStateScan.Page retry = scanner.ReadPage(cursor, 2, (_, _, value) =>
        {
            Assert.That(value.ToArray(), Is.EqualTo(new byte[] { 2 }), "retry must not skip the staged but uncommitted row");
            replayed++;
        }, CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(replayed, Is.EqualTo(1));
            Assert.That(retry.Complete, Is.True);
        }
    }

    [Test]
    public void ReadPage_WhenCursorHasDifferentIdentity_RefusesIt([Values] bool changeAnchor)
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        HistoryRowFormat format = HistoryColumnsWriter.CreateSharedFormat(columns, new FlatDbConfig()).RowFormat;
        HistoricalStateScan scanner = new(Store(columns, FlatHistoryColumns.AccountHistory), format, FlatHistoryColumns.AccountHistory, 10);
        HistoricalStateScan.Cursor cursor = new(changeAnchor ? FlatHistoryColumns.AccountHistory : FlatHistoryColumns.StorageClears,
            changeAnchor ? 11UL : 10UL, new byte[Hash256.Size + sizeof(ulong)]);

        Assert.Throws<ArgumentException>(() => scanner.ReadPage(cursor, 1, (_, _, _) => { }, CancellationToken.None));
    }

    [Test]
    public void ReadPage_WhenRowHasInvalidShape_RefusesIt([Values] bool invalidKey)
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        HistoryRowFormat format = HistoryColumnsWriter.CreateSharedFormat(columns, new FlatDbConfig()).RowFormat;
        IDb source = columns.GetColumnDb(FlatHistoryColumns.AccountHistory);
        source.PutSpan(new byte[Hash256.Size + sizeof(ulong) - (invalidKey ? 1 : 0)], invalidKey ? [1] : new byte[257]);
        HistoricalStateScan scanner = new((ISortedKeyValueStore)source, format, FlatHistoryColumns.AccountHistory, ulong.MaxValue);

        Assert.Throws<InvalidDataException>(() => scanner.ReadPage(null, 1, (_, _, _) => { }, CancellationToken.None));
    }

    [Test]
    public void HistoricalStateScan_WhenHistoryIsWindowed_RefusesTheDifferentRowSemantics()
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        HistoryRowFormat format = HistoryColumnsWriter.CreateSharedFormat(columns, new FlatDbConfig { HistoryRetention = HistoryRetentionMode.Rolling }).RowFormat;

        Assert.Throws<NotSupportedException>(() => new HistoricalStateScan(Store(columns, FlatHistoryColumns.AccountHistory), format, FlatHistoryColumns.AccountHistory, 10));
    }

    [Test]
    public void Contracts_sharing_a_storage_prefix_and_a_slot_past_the_streamed_key_limit_stream_at_full_depth_instead_of_splitting()
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        const int colliding = HistoryRowScanner.MaxStreamedKeys + 4;
        ValueHash256 first = Identity(0x01);
        ValueHash256 slot = Keccak.Compute("slot").ValueHash256;
        for (byte identity = 1; identity <= colliding; identity++) RecordStorage(columns, Identity(identity), slot, block: 1, [identity]);
        (HistoryAvailability _, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(columns, new FlatDbConfig { HistoryEnabled = true });
        HistoryRowScanner scanner = new(Store(columns, FlatHistoryColumns.AccountHistory), Store(columns, FlatHistoryColumns.StorageHistory), Store(columns, FlatHistoryColumns.StorageClears), rowFormat);
        using StoragePartitionRows rows = new();
        TreePath fullDepth = new(slot, CommitmentDepthPolicy.MaxTrieDepth);
        byte[] prefix = first.Bytes[..HistoryRowScanner.StoragePrefixLength].ToArray();

        ScanOutcome outcome = scanner.ScanStorage(prefix, fullDepth, from: 0, to: 1, maxRows: 1, rows, [], CancellationToken.None);
        int streamed = 0;
        while (outcome == ScanOutcome.SinglePathOverflow)
        {
            streamed++;
            outcome = scanner.ScanStorage(prefix, fullDepth, from: 0, to: 1, maxRows: 1, rows, [], CancellationToken.None);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome, Is.EqualTo(ScanOutcome.Fits),
                "a 64-nibble slot prefix has no children to split into, so a partition that still does not fit streams its keys one identity at a time instead of asking for a deeper split");
            Assert.That(streamed, Is.EqualTo(colliding - 1), "at full depth the streamed-key cap does not apply: every colliding identity but the one that fits is streamed, because the alternative is a split that cannot exist");
            Assert.That(rows.Count, Is.EqualTo(1));
        }
    }

    private static ValueHash256 Identity(byte tail)
    {
        byte[] bytes = new byte[Hash256.Size];
        bytes[0] = 0x11;
        bytes[1] = 0x22;
        bytes[2] = 0x33;
        bytes[3] = 0x44;
        bytes[4] = tail;
        return new ValueHash256(bytes);
    }

    private static byte[] ScanKey(int length, byte value)
    {
        byte[] key = new byte[length];
        key.AsSpan().Fill(value);
        return key;
    }

    private static void RecordScanRow(IDb source, FlatHistoryColumns column, ReadOnlySpan<byte> key, ulong block, ReadOnlySpan<byte> value)
    {
        byte[] rowKey = new byte[key.Length + sizeof(ulong)];
        key.CopyTo(rowKey);
        BinaryPrimitives.WriteUInt64BigEndian(rowKey.AsSpan(key.Length), column == FlatHistoryColumns.StorageClears ? block : ~block);
        source.Set(rowKey, value.ToArray());
    }

    private static ISortedKeyValueStore Store(IColumnsDb<FlatHistoryColumns> columns, FlatHistoryColumns column) => (ISortedKeyValueStore)columns.GetColumnDb(column);

    private static void RecordStorage(IColumnsDb<FlatHistoryColumns> columns, in ValueHash256 identity, in ValueHash256 slot, ulong block, ReadOnlySpan<byte> rawValue)
    {
        HistoryStore store = new(columns.GetColumnDb(FlatHistoryColumns.StorageHistory), LimboLogs.Instance.GetClassLogger<HistoryStore>());
        ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(stackalloc byte[BaseFlatPersistence.StorageKeyLength], identity, slot);
        Span<byte> value = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];
        int written = BaseFlatPersistence.EncodeSlotValue(BaseFlatPersistence.DecodeSlotValue(rawValue), rlpWrapSlots: true, value);
        using IColumnsWriteBatch<FlatHistoryColumns> batch = columns.StartWriteBatch();
        store.RecordChange(block, flatKey, value[..written], batch.GetColumnBatch(FlatHistoryColumns.StorageHistory));
    }

    private sealed class FailingWalScratchDb : SnapshotableMemColumnsDb<BulkFillScratchState.Columns>, IColumnsDb<BulkFillScratchState.Columns>
    {
        public bool IsWalFailureEnabled { get; set; }
        public int SuccessfulSyncs { get; private set; }

        public void SyncWal()
        {
            if (IsWalFailureEnabled) throw new IOException("WAL sync failed");
            SuccessfulSyncs++;
        }
    }
}
