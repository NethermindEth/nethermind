// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Image;

/// <summary>Bounds for sweeping a sorted PBT column in chunks, one short-lived view per chunk.</summary>
internal static class PbtColumnSweep
{
    /// <summary>An exclusive upper bound past every PBT column key, including the storage keys longer than 32 bytes.</summary>
    public static byte[] PastEveryKey()
    {
        byte[] key = new byte[PbtStorageTreeKey.MaxLength + 1];
        key.AsSpan().Fill(0xFF);
        return key;
    }

    /// <summary>Returns the inclusive lower bound immediately after <paramref name="key"/>.</summary>
    public static byte[] AfterKey(ReadOnlySpan<byte> key)
    {
        byte[] next = new byte[key.Length + 1];
        key.CopyTo(next);
        return next;
    }

    /// <summary>Deletes every key of <paramref name="column"/> that <paramref name="keep"/> rejects.</summary>
    /// <remarks>Each chunk of <paramref name="chunkSize"/> keys closes its view before its write batch commits,
    /// so the sweep never pins a RocksDB version throughout.</remarks>
    /// <returns>The keys deleted.</returns>
    public static long DeleteKeys(IDb column, int chunkSize, Func<ReadOnlySpan<byte>, bool> keep, CancellationToken cancellationToken)
    {
        ISortedKeyValueStore store = (ISortedKeyValueStore)column;
        byte[] pastEnd = PastEveryKey();
        long deleted = 0;
        byte[]? cursor = [];
        while (cursor is not null)
        {
            using IWriteBatch batch = column.StartWriteBatch();
            using ISortedView view = store.GetViewBetween(cursor, pastEnd);
            int read = 0;
            while (read < chunkSize && view.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                read++;
                if (keep(view.CurrentKey)) continue;
                batch.Remove(view.CurrentKey);
                deleted++;
            }

            // The count limit leaves the view on the last key read.
            cursor = read == chunkSize ? AfterKey(view.CurrentKey) : null;
        }
        return deleted;
    }
}
