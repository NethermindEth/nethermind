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

/// <summary>
/// Writes history rows directly into the history columns using the same flat encoders the production writer uses,
/// so reader/manager tests can stage a history window without driving the full capture path.
/// </summary>
internal static class HistoryColumnsWriter
{
    public static void RecordAccount(IColumnsDb<FlatHistoryColumns> columns, Address address, ulong block, Account? account)
    {
        HistoryStore store = new(columns.GetColumnDb(FlatHistoryColumns.AccountHistory), LimboLogs.Instance.GetClassLogger<HistoryStore>());

        ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeAccountKeyHashed(
            stackalloc byte[BaseFlatPersistence.AccountKeyLength], address.ToAccountPath);

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

    /// <summary>Writes a raw account-history row, bypassing the account encoder — for staging corrupt rows.</summary>
    public static void RecordRawAccountRow(IColumnsDb<FlatHistoryColumns> columns, Address address, ulong block, ReadOnlySpan<byte> rawRow)
    {
        HistoryStore store = new(columns.GetColumnDb(FlatHistoryColumns.AccountHistory), LimboLogs.Instance.GetClassLogger<HistoryStore>());

        ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeAccountKeyHashed(
            stackalloc byte[BaseFlatPersistence.AccountKeyLength], address.ToAccountPath);

        using IColumnsWriteBatch<FlatHistoryColumns> batch = columns.StartWriteBatch();
        store.RecordChange(block, flatKey, rawRow, batch.GetColumnBatch(FlatHistoryColumns.AccountHistory));
    }

    /// <summary>Writes a raw storage-history row, bypassing the slot encoder — for staging corrupt rows.</summary>
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

    /// <summary>Records the per-block availability marker (<c>block -> captured state root</c>).</summary>
    public static void MarkBlock(IColumnsDb<FlatHistoryColumns> columns, ulong block, in ValueHash256 stateRoot)
    {
        using IColumnsWriteBatch<FlatHistoryColumns> batch = columns.StartWriteBatch();
        HistoryAvailability.MarkBlock(batch.GetColumnBatch(FlatHistoryColumns.AvailableBlocks), block, stateRoot, HistoryAvailability.FormatVersion);
    }

    /// <summary>Publishes the contiguous watermark (and stamps the format version), gating reads at or below it.</summary>
    public static void SetWatermark(IColumnsDb<FlatHistoryColumns> columns, ulong watermark) =>
        new HistoryAvailability(columns.GetColumnDb(FlatHistoryColumns.AvailableBlocks)).PublishWatermark(watermark, HistoryAvailability.FormatVersion);

    /// <summary>Publishes the retention floor (and stamps the windowed format version), gating reads below it.</summary>
    public static void SetGlobalFloor(IColumnsDb<FlatHistoryColumns> columns, ulong floor) =>
        new HistoryAvailability(columns.GetColumnDb(FlatHistoryColumns.AvailableBlocks)).PublishGlobalFloor(floor);

    /// <summary>Reads the raw stamped format byte directly, for regression tests on format-version stamping.</summary>
    public static byte? GetStampedFormatVersion(IColumnsDb<FlatHistoryColumns> columns) =>
        new HistoryAvailability(columns.GetColumnDb(FlatHistoryColumns.AvailableBlocks)).StampedFormatVersion;
}
