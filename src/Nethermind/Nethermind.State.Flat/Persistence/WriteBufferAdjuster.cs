// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;

namespace Nethermind.State.Flat.Persistence;

internal class WriteBufferAdjuster(IColumnsDb<FlatDbColumns> db)
{
    private const long MinWriteBufferSize = 16L * 1024 * 1024;   // 16 MB floor
    private const long MaxWriteBufferSize = 256L * 1024 * 1024;  // 256 MB cap

    private readonly Dictionary<FlatDbColumns, long> _lastWriteBufferSize = new();
    private readonly Dictionary<FlatDbColumns, CountingWriteBatch> _activeCounters = new();

    public IWriteOnlyKeyValueStore Wrap(IColumnsWriteBatch<FlatDbColumns> batch, FlatDbColumns column, WriteFlags flags)
    {
        if (flags.HasFlag(WriteFlags.DisableWAL))
            return batch.GetColumnBatch(column);

        if (!_activeCounters.TryGetValue(column, out CountingWriteBatch? counter))
        {
            counter = new(batch.GetColumnBatch(column));
            _activeCounters[column] = counter;
        }
        return counter;
    }

    public void OnBatchDisposed()
    {
        if (_activeCounters.Count == 0) return;

        foreach (KeyValuePair<FlatDbColumns, CountingWriteBatch> entry in _activeCounters)
        {
            AdjustWriteBuffer(entry.Key, entry.Value.BytesWritten);
        }
        _activeCounters.Clear();
    }

    private void AdjustWriteBuffer(FlatDbColumns column, long bytesWritten)
    {
        if (bytesWritten == 0) return;
        long target = Math.Clamp((long)(bytesWritten * 1.5), MinWriteBufferSize, MaxWriteBufferSize);
        if (_lastWriteBufferSize.TryGetValue(column, out long lastSize)
            && Math.Abs(target - lastSize) <= (long)(lastSize * 0.2))
            return;
        _lastWriteBufferSize[column] = target;
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
