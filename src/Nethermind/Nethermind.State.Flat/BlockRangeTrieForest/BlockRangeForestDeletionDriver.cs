// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.BlockRangeTrieForest;

/// <summary>
/// Amortizes deletion of stale <see cref="IBlockRangeTrieForest"/> entries over ongoing
/// flat persistence batches.  After each batch that persists N nodes, call
/// <see cref="DeleteBatch"/> with <c>2 * N</c> to delete up to that many entries from
/// block-ranges that are now below the persisted-state cursor.
///
/// The deletion cursor (last deleted key) is persisted in the flat-DB metadata column so
/// that progress survives restarts.
/// </summary>
public class BlockRangeForestDeletionDriver(IBlockRangeTrieForest forest, IColumnsDb<FlatDbColumns> metadataDb)
{
    private IBlockRangeTrieForest.IDeletionCursor? _cursor;

    public void DeleteBatch(long belowBlockRange, int count)
    {
        if (count <= 0 || belowBlockRange <= 0) return;

        EnsureCursor(belowBlockRange);
        if (_cursor is null || _cursor.IsExhausted) return;

        int deleted = _cursor.DeleteBatch(count);
        if (deleted > 0)
            PersistCursor(_cursor.CurrentKey);

        if (_cursor.IsExhausted)
        {
            _cursor.Dispose();
            _cursor = null;
        }
    }

    private void EnsureCursor(long belowBlockRange)
    {
        if (_cursor is not null && !_cursor.IsExhausted) return;

        _cursor?.Dispose();
        byte[]? resumeKey = ReadCursor();
        _cursor = forest.CreateDeletionCursor(belowBlockRange, resumeKey ?? []);
    }

    private byte[]? ReadCursor() =>
        BasePersistence.ReadForestDeletionCursor(metadataDb.GetColumnDb(FlatDbColumns.Metadata));

    private void PersistCursor(byte[] key)
    {
        using IColumnsWriteBatch<FlatDbColumns> batch = metadataDb.StartWriteBatch();
        BasePersistence.SetForestDeletionCursor(batch.GetColumnBatch(FlatDbColumns.Metadata), key);
    }
}
