// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Db;

/// <summary>
/// Write-behind <see cref="IDb"/> decorator: buffers writes in memory (readable immediately) and flushes them to the
/// inner db on a background worker, taking the RocksDB write off the calling (e.g. <c>engine_newPayload</c>) path.
/// Ported from ethrex's "defer block data persistence" (#6905); intended for write-once stores (blocks, headers,
/// receipts) where a given key is written exactly once, which keeps the buffer→drain path race-free.
/// </summary>
/// <remarks>
/// Reads are buffer-first for <see cref="Get"/>/<see cref="GetOwnedMemory"/>/<see cref="KeyExists"/> (managed values).
/// Span/native reads of a still-buffered key synchronously flush just that key to the inner db first, so native-memory
/// ownership stays with the inner db and <see cref="DangerousReleaseMemory"/> can delegate unconditionally.
/// Durability is best-effort for a benchmark: buffered-but-unflushed entries are lost on an unclean crash (the block
/// would be re-requested from the CL); <see cref="Flush"/>/<see cref="Dispose"/> drain the buffer.
/// </remarks>
public sealed class WriteBehindDb : IDb, IReadOnlyNativeKeyValueStore
{
    private readonly IDb _inner;
    private readonly ConcurrentDictionary<byte[], byte[]?> _buffer = new(Bytes.EqualityComparer);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _worker;
    private volatile bool _disposed;

    public WriteBehindDb(IDb inner)
    {
        _inner = inner;
        _worker = new Thread(FlushLoop) { IsBackground = true, Name = $"WriteBehind:{inner.Name}" };
        _worker.Start();
    }

    public string Name => _inner.Name;

    // ---- writes: buffer + signal, return immediately ----
    public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
    {
        _buffer[key.ToArray()] = value;
        _signal.Release();
    }

    public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
    {
        _buffer[key.ToArray()] = value.IsNull() ? null : value.ToArray();
        _signal.Release();
    }

    public void Remove(ReadOnlySpan<byte> key)
    {
        _buffer[key.ToArray()] = null;
        _signal.Release();
    }

    // ---- reads: buffer-first (managed) ----
    public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        => _buffer.TryGetValue(GetKey(key), out byte[]? v) ? v : _inner.Get(key, flags);

    public bool KeyExists(ReadOnlySpan<byte> key)
        => _buffer.TryGetValue(GetKey(key), out byte[]? v) ? v is not null : _inner.KeyExists(key);

    public MemoryManager<byte>? GetOwnedMemory(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        => _buffer.TryGetValue(GetKey(key), out byte[]? v)
            ? (v is null ? null : new ManagedMemoryManager(v))
            : _inner.GetOwnedMemory(key, flags);

    // span / native reads: flush this key first so the inner db owns the returned native memory
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

    // ---- enumeration / batch / meta: drain first, then delegate ----
    public IEnumerable<KeyValuePair<byte[], byte[]?>> GetAll(bool ordered = false) { DrainAll(); return _inner.GetAll(ordered); }
    public IEnumerable<byte[]> GetAllKeys(bool ordered = false) { DrainAll(); return _inner.GetAllKeys(ordered); }
    public IEnumerable<byte[]> GetAllValues(bool ordered = false) { DrainAll(); return _inner.GetAllValues(ordered); }

    public IWriteBatch StartWriteBatch() { DrainAll(); return _inner.StartWriteBatch(); }

    public void Flush(bool onlyWal = false) { DrainAll(); _inner.Flush(onlyWal); }
    public void Clear() { _buffer.Clear(); _inner.Clear(); }
    public void Compact() { DrainAll(); _inner.Compact(); }
    public void SetWriteBuffer(long sizeBytes) => _inner.SetWriteBuffer(sizeBytes);
    public IDbMeta.DbMetric GatherMetric() => _inner.GatherMetric();

    // ---- flush machinery ----
    private static byte[] GetKey(scoped ReadOnlySpan<byte> key) => key.ToArray();

    private void FlushKey(scoped ReadOnlySpan<byte> key)
    {
        byte[] k = GetKey(key);
        if (_buffer.TryGetValue(k, out byte[]? v))
        {
            // Write to inner FIRST, then drop from the buffer, so the key is always readable from one or the
            // other — never a window where it is in neither (which a concurrent newPayload read could hit).
            if (v is null) _inner.Remove(key); else _inner.Set(key, v);
            _buffer.TryRemove(new KeyValuePair<byte[], byte[]?>(k, v)); // remove only if still the same value
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
                // coalesce: one drain pass handles all pending signals
                while (_signal.CurrentCount > 0) _signal.Wait(0);
                DrainAll();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void DrainAll()
    {
        if (_buffer.IsEmpty) return;
        // Write-before-remove: stage all pending entries into the batch, commit them to the inner db, and only
        // THEN remove them from the buffer. Until removal the buffer still serves reads, so a key is never
        // simultaneously absent from both the buffer and the inner db (no read-miss window vs. the next block).
        List<KeyValuePair<byte[], byte[]?>> drained = [];
        using (IWriteBatch batch = _inner.StartWriteBatch())
        {
            foreach (KeyValuePair<byte[], byte[]?> kv in _buffer)
            {
                if (kv.Value is null) batch.Remove(kv.Key); else batch.Set(kv.Key, kv.Value);
                drained.Add(kv);
            }
        } // batch is committed to the inner db here
        foreach (KeyValuePair<byte[], byte[]?> kv in drained)
        {
            _buffer.TryRemove(kv); // remove only if unchanged; inner now holds the value
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

    private sealed class ManagedMemoryManager(byte[] array) : MemoryManager<byte>
    {
        public override Span<byte> GetSpan() => array;
        public override Memory<byte> Memory => array;
        public override MemoryHandle Pin(int elementIndex = 0)
        {
            System.Runtime.InteropServices.GCHandle handle = System.Runtime.InteropServices.GCHandle.Alloc(array, System.Runtime.InteropServices.GCHandleType.Pinned);
            unsafe { return new MemoryHandle((byte*)handle.AddrOfPinnedObject() + elementIndex, handle); }
        }
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
}
