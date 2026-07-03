// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Db;

/// <summary>
/// Write-behind <see cref="IDb"/> decorator: writes land in an in-memory buffer (readable immediately) and are
/// flushed to the inner db by a background worker, taking the RocksDB write off the caller's (e.g.
/// <c>engine_newPayload</c>) critical path.
/// </summary>
/// <remarks>
/// Reads are buffer-first; span/native reads of a still-buffered key flush that key synchronously first, so returned
/// native memory is always owned by the inner db. Batch entries become visible when the batch is disposed. Durability
/// is relaxed: entries not yet drained are lost on an unclean crash and must be re-derivable (e.g. re-requested from
/// the CL). <see cref="Flush"/> and <see cref="Dispose"/> drain the buffer.
/// </remarks>
public sealed class WriteBehindDb : IDb, IReadOnlyNativeKeyValueStore
{
    private readonly IDb _inner;
    private readonly ConcurrentDictionary<byte[], byte[]?> _buffer = new((IEqualityComparer<byte[]>)Bytes.EqualityComparer);
    private readonly ConcurrentDictionary<byte[], byte[]?>.AlternateLookup<ReadOnlySpan<byte>> _bufferLookup;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _worker;
    private volatile bool _disposed;

    public WriteBehindDb(IDb inner)
    {
        _inner = inner;
        _bufferLookup = _buffer.GetAlternateLookup<ReadOnlySpan<byte>>();
        _worker = new Thread(FlushLoop) { IsBackground = true, Name = $"WriteBehind:{inner.Name}" };
        _worker.Start();
    }

    public string Name => _inner.Name;

    public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
    {
        _bufferLookup[key] = value;
        _signal.Release();
    }

    public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
        => Set(key, value.IsNull() ? null : value.ToArray(), flags);

    public void Remove(ReadOnlySpan<byte> key) => Set(key, null);

    public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        => _bufferLookup.TryGetValue(key, out byte[]? v) ? v : _inner.Get(key, flags);

    public bool KeyExists(ReadOnlySpan<byte> key)
        => _bufferLookup.TryGetValue(key, out byte[]? v) ? v is not null : _inner.KeyExists(key);

    public MemoryManager<byte>? GetOwnedMemory(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        => _bufferLookup.TryGetValue(key, out byte[]? v)
            ? (v is null ? null : new ManagedMemoryManager(v))
            : _inner.GetOwnedMemory(key, flags);

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

    public IWriteBatch StartWriteBatch() => new WriteBehindBatch(this);

    public void Flush(bool onlyWal = false) { DrainAll(); _inner.Flush(onlyWal); }
    public void Clear() { _buffer.Clear(); _inner.Clear(); }
    public void Compact() { DrainAll(); _inner.Compact(); }
    public void SetWriteBuffer(long sizeBytes) => _inner.SetWriteBuffer(sizeBytes);
    public IDbMeta.DbMetric GatherMetric() => _inner.GatherMetric();

    private void FlushKey(scoped ReadOnlySpan<byte> key)
    {
        // Inner is written before the buffer entry is dropped so the key is always readable from one of the two.
        if (_bufferLookup.TryGetValue(key, out byte[]? actualKey, out byte[]? v))
        {
            if (v is null) _inner.Remove(key); else _inner.Set(key, v);
            _buffer.TryRemove(new KeyValuePair<byte[], byte[]?>(actualKey, v));
        }
    }

    private void FlushLoop()
    {
        CancellationToken token = _cts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                _signal.Wait(token);
                while (_signal.CurrentCount > 0) _signal.Wait(0); // coalesce pending signals into one drain
                DrainAll();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void DrainAll()
    {
        if (_buffer.IsEmpty) return;
        // Batch-commit to inner first, remove from the buffer after, so reads never miss a key mid-flush.
        List<KeyValuePair<byte[], byte[]?>> drained = [];
        using (IWriteBatch batch = _inner.StartWriteBatch())
        {
            foreach (KeyValuePair<byte[], byte[]?> kv in _buffer)
            {
                if (kv.Value is null) batch.Remove(kv.Key); else batch.Set(kv.Key, kv.Value);
                drained.Add(kv);
            }
        }
        foreach (KeyValuePair<byte[], byte[]?> kv in drained)
        {
            _buffer.TryRemove(kv); // compare-remove: keeps a concurrent overwrite for the next drain
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _worker.Join(TimeSpan.FromSeconds(30));
        DrainAll();
        _cts.Dispose();
        _signal.Dispose();
        _inner.Dispose();
    }

    private sealed class WriteBehindBatch(WriteBehindDb db) : IWriteBatch
    {
        private readonly List<KeyValuePair<byte[], byte[]?>> _entries = [];

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
            => _entries.Add(new(key.ToArray(), value));

        public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
            => _entries.Add(new(key.ToArray(), value.IsNull() ? null : value.ToArray()));

        public void Clear() => _entries.Clear();

        public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
        {
            // Merge needs the native operator so it cannot be buffered; flush the key to keep write ordering.
            db.FlushKey(key);
            using IWriteBatch inner = db._inner.StartWriteBatch();
            inner.Merge(key, value, flags);
        }

        public void Dispose()
        {
            foreach (KeyValuePair<byte[], byte[]?> kv in _entries) db._buffer[kv.Key] = kv.Value;
            _entries.Clear();
            db._signal.Release();
        }
    }

    private sealed class ManagedMemoryManager(byte[] array) : MemoryManager<byte>
    {
        public override Span<byte> GetSpan() => array;
        public override Memory<byte> Memory => array;
        public override MemoryHandle Pin(int elementIndex = 0)
        {
            GCHandle handle = GCHandle.Alloc(array, GCHandleType.Pinned);
            unsafe { return new MemoryHandle((byte*)handle.AddrOfPinnedObject() + elementIndex, handle); }
        }
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
}
