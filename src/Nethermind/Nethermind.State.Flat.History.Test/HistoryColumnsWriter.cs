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

namespace Nethermind.State.Flat.History.Test;

/// <summary>
/// Writes history rows directly into the history columns using the same flat encoders the production writer uses,
/// so reader/manager tests can stage a history window without driving the full capture path.
/// </summary>
internal static class HistoryColumnsWriter
{
    public static void RecordAccount(IColumnsDb<FlatHistoryColumns> columns, Address address, ulong block, Account? account)
    {
        HistoryStore store = new(
            columns.GetColumnDb(FlatHistoryColumns.AccountHistory),
            columns.GetColumnDb(FlatHistoryColumns.AccountChangeSets));

        ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeAccountKeyHashed(
            stackalloc byte[BaseFlatPersistence.AccountKeyLength], address.ToAccountPath);

        using IColumnsWriteBatch<FlatHistoryColumns> batch = columns.StartWriteBatch();
        IWriteBatch history = batch.GetColumnBatch(FlatHistoryColumns.AccountHistory);
        IWriteBatch changeMarkers = batch.GetColumnBatch(FlatHistoryColumns.AccountChangeSets);

        if (account is null)
        {
            store.RecordChange(block, flatKey, ReadOnlySpan<byte>.Empty, history, changeMarkers);
            return;
        }

        using ArrayPoolSpan<byte> rlp = AccountDecoder.Slim.EncodeToArrayPoolSpan(account);
        store.RecordChange(block, flatKey, rlp, history, changeMarkers);
    }

    public static void RecordStorage(IColumnsDb<FlatHistoryColumns> columns, Address address, in UInt256 slot, ulong block, ReadOnlySpan<byte> rawValue)
    {
        HistoryStore store = new(
            columns.GetColumnDb(FlatHistoryColumns.StorageHistory),
            columns.GetColumnDb(FlatHistoryColumns.StorageChangeSets));

        ValueHash256 slotHash = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(slot, ref slotHash);
        ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(
            stackalloc byte[BaseFlatPersistence.StorageKeyLength], address.ToAccountPath, slotHash);

        Span<byte> value = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];
        int written = rawValue.IsEmpty
            ? 0
            : BaseFlatPersistence.EncodeSlotValue(SlotValue.FromSpanWithoutLeadingZero(rawValue), rlpWrapSlots: true, value);

        using IColumnsWriteBatch<FlatHistoryColumns> batch = columns.StartWriteBatch();
        store.RecordChange(
            block, flatKey, value[..written],
            batch.GetColumnBatch(FlatHistoryColumns.StorageHistory),
            batch.GetColumnBatch(FlatHistoryColumns.StorageChangeSets));
    }

    /// <summary>Records the per-block availability marker (<c>block -> captured state root</c>).</summary>
    public static void MarkBlock(IColumnsDb<FlatHistoryColumns> columns, ulong block, in ValueHash256 stateRoot)
    {
        using IColumnsWriteBatch<FlatHistoryColumns> batch = columns.StartWriteBatch();
        HistoryAvailability.MarkBlock(batch.GetColumnBatch(FlatHistoryColumns.AvailableBlocks), block, stateRoot);
    }

    /// <summary>Publishes the contiguous watermark (and stamps the format version), gating reads at or below it.</summary>
    public static void SetWatermark(IColumnsDb<FlatHistoryColumns> columns, ulong watermark) =>
        new HistoryAvailability(columns.GetColumnDb(FlatHistoryColumns.AvailableBlocks)).PublishWatermark(watermark);
}
