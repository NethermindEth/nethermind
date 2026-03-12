// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Db;

namespace Nethermind.State.Flat.Persistence;

internal class WriteBufferAdjuster(IColumnsDb<FlatDbColumns> db)
{
    internal const int ColumnCount = 7;
    private const long MinWriteBufferSize = 16L * 1024 * 1024;   // 16 MB floor
    private const long MaxWriteBufferSize = 256L * 1024 * 1024;  // 256 MB cap

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
                SetWriteBuffer(db, FlatDbColumns.Account, 32L * 1024 * 1024);
                SetWriteBuffer(db, FlatDbColumns.Storage, 64L * 1024 * 1024);
                SetWriteBuffer(db, FlatDbColumns.StateNodes, 64L * 1024 * 1024);
                SetWriteBuffer(db, FlatDbColumns.StorageNodes, 64L * 1024 * 1024);
                _syncBufferSet = true;

                static void SetWriteBuffer(IColumnsDb<FlatDbColumns> columnsDb, FlatDbColumns column, long size) =>
                    columnsDb.GetColumnDb(column).SetWriteBuffer(size);
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
        long target = Math.Clamp((long)(bytesWritten * 1.5), MinWriteBufferSize, MaxWriteBufferSize);
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
