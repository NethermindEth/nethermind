// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;
using Nethermind.State.Flat.History.Proofs;

namespace Nethermind.State.Flat.History.Walk;

internal sealed class SeriesWriter(IColumnsDb<FlatHistoryColumns> history) : IDisposable
{
    private const int MaxRowsPerBatch = 65_536;

    private readonly IDb _scratchColumn = history.GetColumnDb(FlatHistoryColumns.AccountCommitments);
    private IWriteBatch? _batch;
    private int _rowsInBatch;

    public void Write(in SeriesKey key, ulong block, byte[] row)
    {
        if (!key.Scratch) throw new InvalidOperationException("Only scratch series are written by the walk; exact commitment rows come from the emitter.");

        Span<byte> rowKey = stackalloc byte[SeriesKey.MaxKeyLength];
        int prefixLength = key.WritePrefix(rowKey);
        int keyLength = CommitmentKeyLayout.WriteSeekKey(rowKey, rowKey[..prefixLength], block);
        _batch ??= _scratchColumn.StartWriteBatch();
        _batch.PutSpan(rowKey[..keyLength], row);
        if (++_rowsInBatch >= MaxRowsPerBatch) Flush();
    }

    public void Delete(in SeriesKey key)
    {
        if (!key.Scratch) throw new InvalidOperationException("Only scratch series are deleted by the walk; exact commitment rows stay.");

        Flush();
        Span<byte> prefix = stackalloc byte[SeriesKey.MaxKeyLength];
        int prefixLength = key.WritePrefix(prefix);
        Span<byte> upper = stackalloc byte[SeriesKey.MaxKeyLength];
        int upperLength = CommitmentKeyLayout.WriteUpperBound(upper, prefix[..prefixLength]);
        RemoveRange(prefix[..prefixLength], upper[..upperLength]);
    }

    public void DeleteAllScratch()
    {
        Flush();
        RemoveRange([SeriesKey.ScratchMarker], [SeriesKey.ScratchMarker + 1]);
    }

    public void DeleteAccountScratchUnder(byte firstPathByte)
    {
        Flush();
        for (int depth = 2; depth <= CommitmentDepthPolicy.MaxTrieDepth; depth++)
        {
            byte[] lower = [SeriesKey.ScratchMarker, 0x00, (byte)depth, firstPathByte];
            byte[] upper = firstPathByte == byte.MaxValue ? [SeriesKey.ScratchMarker, 0x00, (byte)(depth + 1)] : [SeriesKey.ScratchMarker, 0x00, (byte)depth, (byte)(firstPathByte + 1)];
            RemoveRange(lower, upper);
        }
    }

    public void DeleteStorageScratchUnder(byte firstIdentityByte)
    {
        Flush();
        byte[] lower = [SeriesKey.ScratchMarker, 0x01, firstIdentityByte];
        byte[] upper = firstIdentityByte == byte.MaxValue ? [SeriesKey.ScratchMarker, 0x02] : [SeriesKey.ScratchMarker, 0x01, (byte)(firstIdentityByte + 1)];
        RemoveRange(lower, upper);
    }

    public void Flush()
    {
        _batch?.Dispose();
        _batch = null;
        _rowsInBatch = 0;
    }

    public void Dispose() => Flush();

    private void RemoveRange(ReadOnlySpan<byte> lowerInclusive, ReadOnlySpan<byte> upperExclusive)
    {
        if (_scratchColumn is IRangeRemovableKeyValueStore ranged)
        {
            ranged.RemoveRange(lowerInclusive, upperExclusive);
            return;
        }

        List<byte[]> keys = [];
        using (ISortedView view = ((ISortedKeyValueStore)_scratchColumn).GetViewBetween(lowerInclusive, upperExclusive))
        {
            while (view.MoveNext()) keys.Add(view.CurrentKey.ToArray());
        }

        using IWriteBatch batch = _scratchColumn.StartWriteBatch();
        foreach (byte[] key in keys) batch.Remove(key);
    }
}
