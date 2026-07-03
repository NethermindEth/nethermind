// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Db;

/// <summary>
/// <see cref="IDb"/> decorator whose writes complete without waiting for the underlying db, while staying
/// immediately readable through this instance.
/// </summary>
/// <remarks>
/// Writes land in an in-memory buffer drained to the inner db by a background worker, taking the RocksDB write off
/// the caller's (e.g. <c>engine_newPayload</c>) critical path. Reads are buffer-first; span/native reads of a
/// still-buffered key flush that key synchronously first so returned native memory is always owned by the inner db.
/// Batch entries become visible when the batch is disposed. <see cref="WriteFlags"/> are preserved into the drain.
/// If the buffer exceeds <see cref="MaxBufferedBytes"/> writes fall back to synchronous write-through (backpressure).
/// Durability is relaxed: entries not yet drained are lost on an unclean crash and must be re-derivable (e.g.
/// re-requested from the CL). <see cref="Flush"/> and <see cref="Dispose"/> drain the buffer.
/// </remarks>
public sealed class WriteBehindDb : IDb, IReadOnlyNativeKeyValueStore, ITunableDb, ISortedKeyValueStore
{
    internal const long MaxBufferedBytes = 64 * 1024 * 1024;

    private readonly IDb _inner;
    private readonly bool _disposeInner;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<byte[], Entry> _buffer = new((IEqualityComparer<byte[]>)Bytes.EqualityComparer);
    private readonly ConcurrentDictionary<byte[], Entry>.AlternateLookup<ReadOnlySpan<byte>> _bufferLookup;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _drainLock = new();
    private readonly Thread _worker;
    private long _bufferedBytes;
    private int _disposed;

    public WriteBehindDb(IDb inner, ILogManager? logManager = null, bool disposeInner = true)
    {
        _inner = inner;
        _disposeInner = disposeInner;
        _logger = logManager?.GetClassLogger<WriteBehindDb>() ?? NullLogger.Instance;
        _bufferLookup = _buffer.GetAlternateLookup<ReadOnlySpan<byte>>();
        _worker = new Thread(FlushLoop) { IsBackground = true, Name = $"WriteBehind:{inner.Name}" };
        _worker.Start();
    }

    private readonly record struct Entry(byte[]? Value, WriteFlags Flags);

    public string Name => _inner.Name;

    /// <inheritdoc/>
    public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
    {
        // Past the cap (drain fell behind, e.g. bulk sync) or once disposed, degrade to the undecorated behavior.
        if (Volatile.Read(ref _disposed) == 1 || Interlocked.Read(ref _bufferedBytes) >= MaxBufferedBytes)
        {
            FlushKey(key);
            _inner.Set(key, value, flags);
            return;
        }

        _bufferLookup[key] = new Entry(value, flags);
        Interlocked.Add(ref _bufferedBytes, key.Length + (value?.Length ?? 0));
        _signal.Release();
    }

    public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
        => Set(key, value.IsNull() ? null : value.ToArray(), flags);

    public void Remove(ReadOnlySpan<byte> key) => Set(key, null);

    /// <inheritdoc/>
    public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        => _bufferLookup.TryGetValue(key, out Entry e) ? e.Value : _inner.Get(key, flags);

    public bool KeyExists(ReadOnlySpan<byte> key)
        => _bufferLookup.TryGetValue(key, out Entry e) ? e.Value is not null : _inner.KeyExists(key);

    public MemoryManager<byte>? GetOwnedMemory(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        => _bufferLookup.TryGetValue(key, out Entry e) ? ArrayMemoryManager.From(e.Value) : _inner.GetOwnedMemory(key, flags);

    /// <inheritdoc/>
    /// <remarks>A still-buffered key is flushed first so the returned memory is owned by the inner db.</remarks>
    public Span<byte> GetSpan(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
    {
        FlushKey(key);
        return _inner.GetSpan(key, flags);
    }

    public ReadOnlySpan<byte> GetNativeSlice(scoped ReadOnlySpan<byte> key, out IntPtr handle, ReadFlags flags = ReadFlags.None)
    {
        FlushKey(key);
        if (_inner is IReadOnlyNativeKeyValueStore native) return native.GetNativeSlice(key, out handle, flags);
        handle = IntPtr.Zero;
        return _inner.Get(key, flags);
    }

    public void DangerousReleaseMemory(in ReadOnlySpan<byte> span) => _inner.DangerousReleaseMemory(span);

    public void DangerousReleaseHandle(IntPtr handle)
    {
        if (_inner is IReadOnlyNativeKeyValueStore native) native.DangerousReleaseHandle(handle);
    }

    public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys]
    {
        get
        {
            KeyValuePair<byte[], byte[]?>[] result = new KeyValuePair<byte[], byte[]?>[keys.Length];
            for (int i = 0; i < keys.Length; i++) result[i] = new(keys[i], Get(keys[i]));
            return result;
        }
    }

    public IEnumerable<KeyValuePair<byte[], byte[]?>> GetAll(bool ordered = false) { DrainAll(); return _inner.GetAll(ordered); }
    public IEnumerable<byte[]> GetAllKeys(bool ordered = false) { DrainAll(); return _inner.GetAllKeys(ordered); }
    public IEnumerable<byte[]> GetAllValues(bool ordered = false) { DrainAll(); return _inner.GetAllValues(ordered); }

    /// <inheritdoc/>
    /// <remarks>Entries become readable (and are queued for the drain) when the batch is disposed.</remarks>
    public IWriteBatch StartWriteBatch() => new WriteBehindBatch(this);

    public void Flush(bool onlyWal = false) { DrainAll(); _inner.Flush(onlyWal); }

    public void Clear()
    {
        lock (_drainLock)
        {
            _buffer.Clear();
            Interlocked.Exchange(ref _bufferedBytes, 0);
            _inner.Clear();
        }
    }

    public void Compact() { DrainAll(); _inner.Compact(); }
    public void SetWriteBuffer(long sizeBytes) => _inner.SetWriteBuffer(sizeBytes);
    public IDbMeta.DbMetric GatherMetric() => _inner.GatherMetric();

    public void Tune(ITunableDb.TuneType type)
    {
        if (_inner is ITunableDb tunable) tunable.Tune(type);
    }

    public byte[]? FirstKey { get { DrainAll(); return Sorted.FirstKey; } }
    public byte[]? LastKey { get { DrainAll(); return Sorted.LastKey; } }

    public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive)
    {
        DrainAll();
        return Sorted.GetViewBetween(firstKeyInclusive, lastKeyExclusive);
    }

    private ISortedKeyValueStore Sorted => _inner as ISortedKeyValueStore
        ?? throw new NotSupportedException($"{_inner.Name} is not an {nameof(ISortedKeyValueStore)}");

    private void FlushKey(scoped ReadOnlySpan<byte> key)
    {
        // Inner is written before the buffer entry is dropped so the key is always readable from one of the two.
        if (_bufferLookup.TryGetValue(key, out byte[]? actualKey, out Entry e))
        {
            if (e.Value is null) _inner.Remove(key); else _inner.Set(key, e.Value, e.Flags);
            if (_buffer.TryRemove(new KeyValuePair<byte[], Entry>(actualKey, e)))
            {
                Interlocked.Add(ref _bufferedBytes, -(actualKey.Length + (e.Value?.Length ?? 0)));
            }
        }
    }

    private void FlushLoop()
    {
        CancellationToken token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                _signal.Wait(token);
                while (_signal.CurrentCount > 0) _signal.Wait(0); // coalesce pending signals into one drain
                DrainAll();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception e)
            {
                // The worker must survive transient inner-db failures or the buffer would grow unbounded.
                if (_logger.IsError) _logger.Error($"WriteBehind flush of {Name} failed, retrying", e);
                if (token.WaitHandle.WaitOne(1000)) return;
                _signal.Release(); // re-arm: the failed drain already consumed its signal
            }
        }
    }

    private void DrainAll()
    {
        if (_buffer.IsEmpty) return;
        lock (_drainLock)
        {
            if (_buffer.IsEmpty) return;
            // Batch-commit to inner first, remove from the buffer after, so reads never miss a key mid-flush.
            List<KeyValuePair<byte[], Entry>> drained = [];
            using (IWriteBatch batch = _inner.StartWriteBatch())
            {
                foreach (KeyValuePair<byte[], Entry> kv in _buffer)
                {
                    if (kv.Value.Value is null) batch.Remove(kv.Key); else batch.Set(kv.Key, kv.Value.Value, kv.Value.Flags);
                    drained.Add(kv);
                }
            }
            foreach (KeyValuePair<byte[], Entry> kv in drained)
            {
                if (_buffer.TryRemove(kv)) // compare-remove: keeps a concurrent overwrite for the next drain
                {
                    Interlocked.Add(ref _bufferedBytes, -(kv.Key.Length + (kv.Value.Value?.Length ?? 0)));
                }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _cts.Cancel();
        _worker.Join(TimeSpan.FromSeconds(30));
        DrainAll();
        _cts.Dispose();
        _signal.Dispose();
        if (_disposeInner) _inner.Dispose();
    }

    private sealed class WriteBehindBatch(WriteBehindDb db) : IWriteBatch
    {
        private readonly List<(byte[] Key, byte[]? Value, WriteFlags Flags)> _entries = [];

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
            => _entries.Add((key.ToArray(), value, flags));

        public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
            => _entries.Add((key.ToArray(), value.IsNull() ? null : value.ToArray(), flags));

        public void Clear() => _entries.Clear();

        // No merge users among the wrapped stores; buffering can't apply the native merge operator anyway.
        public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
            => throw new NotSupportedException($"{nameof(WriteBehindDb)} does not support merge");

        public void Dispose()
        {
            foreach ((byte[] key, byte[]? value, WriteFlags flags) in _entries)
            {
                db._buffer[key] = new Entry(value, flags);
                Interlocked.Add(ref db._bufferedBytes, key.Length + (value?.Length ?? 0));
            }
            _entries.Clear();
            if (Volatile.Read(ref db._disposed) == 0) db._signal.Release();
        }
    }
}
