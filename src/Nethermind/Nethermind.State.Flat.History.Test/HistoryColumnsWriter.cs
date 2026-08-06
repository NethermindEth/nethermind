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

    /// <summary>Records the per-block availability marker stamped as the windowed (v3) format - the v3 counterpart
    /// to <see cref="MarkBlock"/>, for staging markers directly on a windowed writer/reader without a full
    /// capture walk.</summary>
    public static void MarkBlockV3(IColumnsDb<FlatHistoryColumns> columns, ulong block, in ValueHash256 stateRoot)
    {
        using IColumnsWriteBatch<FlatHistoryColumns> batch = columns.StartWriteBatch();
        HistoryAvailability.MarkBlock(batch.GetColumnBatch(FlatHistoryColumns.AvailableBlocks), block, stateRoot, HistoryAvailability.WindowedFormatVersion);
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

    /// <summary>Builds the shared <see cref="HistoryAvailability"/>/<see cref="HistoryRowFormat"/> pair a test's
    /// writer, reader and pruner must all share — mirroring the single DI-bound instance production wires them
    /// through, so a test cannot accidentally recreate the exact "writer/reader resolved from one config, pruner
    /// from another" mismatch that masked the pruner's format-decode bug.</summary>
    public static (HistoryAvailability Availability, HistoryRowFormat RowFormat) CreateSharedFormat(IColumnsDb<FlatHistoryColumns> columns, IFlatDbConfig config)
    {
        HistoryAvailability availability = new(columns.GetColumnDb(FlatHistoryColumns.AvailableBlocks));
        HistoryRowFormat rowFormat = HistoryRowFormat.Resolve(availability, config.HistoryRetentionBlocks > 0);
        return (availability, rowFormat);
    }

    /// <summary>Writes a v3 pre-value account row directly (the shape a windowed writer's capture produces), for
    /// staging pruner/reader test fixtures without driving a full capture walk.</summary>
    public static void RecordAccountV3(IColumnsDb<FlatHistoryColumns> columns, Address address, ulong block, Account? account)
    {
        HistoryStoreV3 store = new(columns.GetColumnDb(FlatHistoryColumns.AccountHistory));

        ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeAccountKeyHashed(
            stackalloc byte[BaseFlatPersistence.AccountKeyLength], address.ToAccountPath);

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

    /// <summary>Writes a v3 pre-value storage row directly — the ascending-suffix counterpart to
    /// <see cref="RecordStorage"/>, for staging pruner/reader test fixtures without driving a full capture walk.</summary>
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

    /// <summary>Publishes the contiguous watermark stamped as the windowed (v3) format, for staging v3 pruner/reader
    /// test fixtures — the v3 counterpart to <see cref="SetWatermark"/>.</summary>
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

internal sealed class TestCaptureStatus : IStateHistoryCaptureStatus
{
    public bool CaptureHealthy { get; set; } = true;

#pragma warning disable CS0067
    public event Action<ulong>? WatermarkAdvanced;

    public event Action? CaptureDisabled;
#pragma warning restore CS0067
}
