// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Db;

namespace Nethermind.State.Flat.Persistence;

internal class WriteBufferAdjuster(
    IColumnsDb<FlatDbColumns> db,
    long writeBufferFloor = WriteBufferAdjuster.DefaultWriteBufferFloor,
    long writeBufferCap = WriteBufferAdjuster.DefaultWriteBufferCap)
{
    internal const int ColumnCount = 7;
    internal const long DefaultWriteBufferFloor = 16 * MemorySizes.MiB;
    internal const long DefaultWriteBufferCap = 64 * MemorySizes.MiB;

    // Upper bound the per-batch adjuster grows a column's memtable to. The heavy columns use the whole cap;
    // Account and the metadata/fallback columns scale down from it, preserving the historical 64/32/16 MB ratio
    // at the default cap. Raising the cap lets a heavy-block CompactSize persist coalesce in the memtable instead
    // of forcing a flush and L0 pileup.
    private long MaxWriteBufferSize(FlatDbColumns column) => column switch
    {
        FlatDbColumns.Account => _writeBufferCap / 2,
        FlatDbColumns.Storage => _writeBufferCap,
        FlatDbColumns.StateNodes => _writeBufferCap,
        FlatDbColumns.StateTopNodes => _writeBufferCap,
        FlatDbColumns.StorageNodes => _writeBufferCap,
        _ => _writeBufferCap / 4,                             // Metadata, FallbackNodes
    };

    // Lower bound applied per column. Frequent small persistence batches (small CompactSize) would otherwise
    // shrink the memtable to the floor and churn L0; raising the floor lets them coalesce in the memtable instead.
    private readonly long _writeBufferFloor = writeBufferFloor <= 0 ? DefaultWriteBufferFloor : writeBufferFloor;

    private readonly long _writeBufferCap = writeBufferCap <= 0 ? DefaultWriteBufferCap : writeBufferCap;

    private bool _syncBufferSet;

    private WriteBufferSizeBuffer _lastWriteBufferSize;
    private ActiveCounterBuffer _activeCounters;
    private int _activeCounterCount;

    [InlineArray(ColumnCount)]
    private struct WriteBufferSizeBuffer
    {
        private long _element0;
    }

    [InlineArray(ColumnCount)]
    private struct ActiveCounterBuffer
    {
        private CountingWriteBatch? _element0;
    }

    public IWriteBatch Wrap(IColumnsWriteBatch<FlatDbColumns> batch, FlatDbColumns column, WriteFlags flags)
    {
        if (flags.HasFlag(WriteFlags.DisableWAL))
        {
            if (!_syncBufferSet)
            {
                SetWriteBuffer(FlatDbColumns.Account);
                SetWriteBuffer(FlatDbColumns.Storage);
                SetWriteBuffer(FlatDbColumns.StateNodes);
                SetWriteBuffer(FlatDbColumns.StorageNodes);
                _syncBufferSet = true;

                void SetWriteBuffer(FlatDbColumns col) =>
                    db.GetColumnDb(col).SetWriteBuffer(MaxWriteBufferSize(col));
            }

            return batch.GetColumnBatch(column);
        }

        _syncBufferSet = false;

        int idx = (int)column;
        CountingWriteBatch? counter = _activeCounters[idx];
        if (counter is null)
        {
            counter = new(batch.GetColumnBatch(column));
            _activeCounters[idx] = counter;
            _activeCounterCount++;
        }
        return counter;
    }

    public void OnBatchDisposed()
    {
        if (_activeCounterCount == 0) return;

        for (int i = 0; i < ColumnCount; i++)
        {
            CountingWriteBatch? counter = _activeCounters[i];
            if (counter is not null)
            {
                AdjustWriteBuffer((FlatDbColumns)i, counter.BytesWritten);
                _activeCounters[i] = null;
            }
        }
        _activeCounterCount = 0;
    }

    private void AdjustWriteBuffer(FlatDbColumns column, long bytesWritten)
    {
        if (_syncBufferSet) return;
        if (bytesWritten == 0) return;
        int idx = (int)column;
        long target = Math.Clamp((long)(bytesWritten * 1.5), _writeBufferFloor, Math.Max(_writeBufferFloor, MaxWriteBufferSize(column)));
        long lastSize = _lastWriteBufferSize[idx];
        if (lastSize != 0 && Math.Abs(target - lastSize) <= (long)(lastSize * 0.2))
            return;
        _lastWriteBufferSize[idx] = target;
        db.GetColumnDb(column).SetWriteBuffer(target);
    }

    internal class CountingWriteBatch(IWriteBatch inner) : IWriteBatch
    {
        public long BytesWritten { get; private set; }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            BytesWritten += key.Length + (value?.Length ?? 0);
            inner.Set(key, value, flags);
        }

        public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
        {
            BytesWritten += key.Length + value.Length;
            inner.PutSpan(key, value, flags);
        }

        public void Remove(ReadOnlySpan<byte> key)
        {
            BytesWritten += key.Length;
            inner.Remove(key);
        }

        public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
        {
            BytesWritten += key.Length + value.Length;
            inner.Merge(key, value, flags);
        }

        public void Clear() => inner.Clear();
        public void Dispose() => inner.Dispose();
    }
}
