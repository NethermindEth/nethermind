// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.Logging;

namespace Nethermind.State.Flat.History.Test;

internal static class HistoryColumnsWriter
{
    public static void RecordAccount(IColumnsDb<FlatHistoryColumns> columns, Address address, ulong block, Account? account)
    {
        HistoryStore store = new(columns.GetColumnDb(FlatHistoryColumns.AccountHistory), LimboLogs.Instance.GetClassLogger<HistoryStore>());

        ReadOnlySpan<byte> flatKey = address.ToAccountPath.Bytes;

        using IColumnsWriteBatch<FlatHistoryColumns> batch = columns.StartWriteBatch();
        IWriteBatch history = batch.GetColumnBatch(FlatHistoryColumns.AccountHistory);

        if (account is null)
        {
            store.RecordChange(block, flatKey, ReadOnlySpan<byte>.Empty, history);
            return;
        }

        using ArrayPoolSpan<byte> rlp = AccountDecoder.Slim.EncodeToArrayPoolSpan(account);
        store.RecordChange(block, flatKey, rlp, history);
    }

    public static void RecordStorage(IColumnsDb<FlatHistoryColumns> columns, Address address, in UInt256 slot, ulong block, ReadOnlySpan<byte> rawValue)
    {
        HistoryStore store = new(columns.GetColumnDb(FlatHistoryColumns.StorageHistory), LimboLogs.Instance.GetClassLogger<HistoryStore>());

        ValueHash256 slotHash = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(slot, ref slotHash);
        ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(
            stackalloc byte[BaseFlatPersistence.StorageKeyLength], address.ToAccountPath, slotHash);

        Span<byte> value = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];
        int written = rawValue.IsEmpty
            ? 0
            : BaseFlatPersistence.EncodeSlotValue(SlotValue.FromSpanWithoutLeadingZero(rawValue), rlpWrapSlots: true, value);

        using IColumnsWriteBatch<FlatHistoryColumns> batch = columns.StartWriteBatch();
        store.RecordChange(block, flatKey, value[..written], batch.GetColumnBatch(FlatHistoryColumns.StorageHistory));
    }

    public static void RecordRawAccountRow(IColumnsDb<FlatHistoryColumns> columns, Address address, ulong block, ReadOnlySpan<byte> rawRow)
    {
        HistoryStore store = new(columns.GetColumnDb(FlatHistoryColumns.AccountHistory), LimboLogs.Instance.GetClassLogger<HistoryStore>());

        ReadOnlySpan<byte> flatKey = address.ToAccountPath.Bytes;

        using IColumnsWriteBatch<FlatHistoryColumns> batch = columns.StartWriteBatch();
        store.RecordChange(block, flatKey, rawRow, batch.GetColumnBatch(FlatHistoryColumns.AccountHistory));
    }

    public static void RecordRawStorageRow(IColumnsDb<FlatHistoryColumns> columns, Address address, in UInt256 slot, ulong block, ReadOnlySpan<byte> rawRow)
    {
        HistoryStore store = new(columns.GetColumnDb(FlatHistoryColumns.StorageHistory), LimboLogs.Instance.GetClassLogger<HistoryStore>());

        ValueHash256 slotHash = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(slot, ref slotHash);
        ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(
            stackalloc byte[BaseFlatPersistence.StorageKeyLength], address.ToAccountPath, slotHash);

        using IColumnsWriteBatch<FlatHistoryColumns> batch = columns.StartWriteBatch();
        store.RecordChange(block, flatKey, rawRow, batch.GetColumnBatch(FlatHistoryColumns.StorageHistory));
    }

    public static void MarkBlock(IColumnsDb<FlatHistoryColumns> columns, ulong block, in ValueHash256 stateRoot)
    {
        using IColumnsWriteBatch<FlatHistoryColumns> batch = columns.StartWriteBatch();
        HistoryAvailability.MarkBlock(batch.GetColumnBatch(FlatHistoryColumns.AvailableBlocks), block, stateRoot, HistoryAvailability.FormatVersion);
    }

    public static void MarkBlockV3(IColumnsDb<FlatHistoryColumns> columns, ulong block, in ValueHash256 stateRoot)
    {
        using IColumnsWriteBatch<FlatHistoryColumns> batch = columns.StartWriteBatch();
        HistoryAvailability.MarkBlock(batch.GetColumnBatch(FlatHistoryColumns.AvailableBlocks), block, stateRoot, HistoryAvailability.WindowedFormatVersion);
    }

    public static void SetWatermark(IColumnsDb<FlatHistoryColumns> columns, ulong watermark) =>
        new HistoryAvailability(columns.GetColumnDb(FlatHistoryColumns.AvailableBlocks)).PublishWatermark(watermark, HistoryAvailability.FormatVersion);

    public static void SetGlobalFloor(IColumnsDb<FlatHistoryColumns> columns, ulong floor) =>
        new HistoryAvailability(columns.GetColumnDb(FlatHistoryColumns.AvailableBlocks)).PublishGlobalFloor(floor);

    public static byte? GetStampedFormatVersion(IColumnsDb<FlatHistoryColumns> columns) =>
        new HistoryAvailability(columns.GetColumnDb(FlatHistoryColumns.AvailableBlocks)).StampedFormatVersion;

    public static (HistoryAvailability Availability, HistoryRowFormat RowFormat) CreateSharedFormat(IColumnsDb<FlatHistoryColumns> columns, IFlatDbConfig config)
    {
        HistoryAvailability availability = new(columns.GetColumnDb(FlatHistoryColumns.AvailableBlocks));
        HistoryRowFormat rowFormat = HistoryRowFormat.Resolve(availability, config);
        return (availability, rowFormat);
    }

    public static void RecordAccountV3(IColumnsDb<FlatHistoryColumns> columns, Address address, ulong block, Account? account)
    {
        HistoryStoreV3 store = new(columns.GetColumnDb(FlatHistoryColumns.AccountHistory));

        ReadOnlySpan<byte> flatKey = address.ToAccountPath.Bytes;

        using IColumnsWriteBatch<FlatHistoryColumns> batch = columns.StartWriteBatch();
        IWriteBatch history = batch.GetColumnBatch(FlatHistoryColumns.AccountHistory);

        if (account is null)
        {
            store.RecordPreValue(block, flatKey, ReadOnlySpan<byte>.Empty, history);
            return;
        }

        using ArrayPoolSpan<byte> rlp = AccountDecoder.Slim.EncodeToArrayPoolSpan(account);
        store.RecordPreValue(block, flatKey, rlp, history);
    }

    public static void RecordStorageV3(IColumnsDb<FlatHistoryColumns> columns, Address address, in UInt256 slot, ulong block, ReadOnlySpan<byte> rawValueBeforeChange)
    {
        HistoryStoreV3 store = new(columns.GetColumnDb(FlatHistoryColumns.StorageHistory));

        ValueHash256 slotHash = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(slot, ref slotHash);
        ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(
            stackalloc byte[BaseFlatPersistence.StorageKeyLength], address.ToAccountPath, slotHash);

        Span<byte> value = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];
        int written = rawValueBeforeChange.IsEmpty
            ? 0
            : BaseFlatPersistence.EncodeSlotValue(SlotValue.FromSpanWithoutLeadingZero(rawValueBeforeChange), rlpWrapSlots: true, value);

        using IColumnsWriteBatch<FlatHistoryColumns> batch = columns.StartWriteBatch();
        store.RecordPreValue(block, flatKey, value[..written], batch.GetColumnBatch(FlatHistoryColumns.StorageHistory));
    }

    public static void SetWatermarkV3(IColumnsDb<FlatHistoryColumns> columns, ulong watermark) =>
        new HistoryAvailability(columns.GetColumnDb(FlatHistoryColumns.AvailableBlocks)).PublishWatermark(watermark, HistoryAvailability.WindowedFormatVersion);

    public static void SetPersistedAccount(IColumnsDb<FlatDbColumns> db, Address address, Account? account)
    {
        ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeAccountKeyHashed(
            stackalloc byte[BaseFlatPersistence.AccountKeyLength], address.ToAccountPath);

        IDb accountColumn = db.GetColumnDb(FlatDbColumns.Account);
        if (account is null)
        {
            accountColumn.Remove(flatKey);
            return;
        }

        using ArrayPoolSpan<byte> rlp = AccountDecoder.Slim.EncodeToArrayPoolSpan(account);
        accountColumn.PutSpan(flatKey, rlp);
    }
}
