// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Db;

namespace Nethermind.Blockchain.Receipts;

/// <summary>
/// Buffers receipt-blob writes so the RocksDB write does not block the block-processing path, flushing
/// them to the underlying receipts column on a background worker.
/// </summary>
/// <remarks>
/// Scoped to the receipts <c>Blocks</c> column only — it does not wrap or alter the shared <see cref="IDb"/>,
/// so no other db consumer is affected. Recently-inserted receipts are served from the receipt cache in
/// <see cref="PersistentReceiptStorage"/>; a read that reaches the column before the background flush calls
/// <see cref="EnsureFlushed"/>, which writes the key synchronously so the column still owns the returned
/// (native) memory. Reads are therefore always served from the underlying column, never from this buffer.
/// Durability is relaxed like the already-deferred state trie: entries not yet flushed are lost on an unclean
/// crash and the block is re-requested from the consensus layer. Drained on <see cref="Dispose"/>.
/// </remarks>
internal sealed class PendingReceiptsWriter : IDisposable
{
    private readonly IDb _column;
    // Batch a short window of blocks per flush so the worker wakes about once per window rather than once
    // per block; per-block wake-ups otherwise preempt (and add jitter to) the parallel block-processing path.
    private const int FlushBatchIntervalMs = 200;
    private const int FlushBatchMaxEntries = 128;

    private readonly ConcurrentDictionary<byte[], Entry> _pending = new((IEqualityComparer<byte[]>)Bytes.EqualityComparer);
    private readonly ConcurrentDictionary<byte[], Entry>.AlternateLookup<ReadOnlySpan<byte>> _pendingBySpan;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _lock = new();
    private readonly Thread _worker;
    private volatile bool _disposed;

    public PendingReceiptsWriter(IDb column)
    {
        _column = column;
        _pendingBySpan = _pending.GetAlternateLookup<ReadOnlySpan<byte>>();
        // Below-normal so a flush yields to block-processing threads rather than preempting them.
        _worker = new Thread(FlushLoop) { IsBackground = true, Name = "ReceiptsFlush", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
    }

    private readonly record struct Entry(byte[] Value, WriteFlags Flags);

    internal int PendingCount => _pending.Count;

    /// <summary>Buffers a receipt-blob write to be flushed to the column asynchronously.</summary>
    public void Write(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags)
    {
        if (_disposed)
        {
            _column.PutSpan(key, value, flags);
            return;
        }

        _pendingBySpan[key] = new Entry(value.ToArray(), flags);
        _signal.Release();
    }

    /// <summary>Writes the key to the column now if it is still buffered, so a following read is served from durable storage.</summary>
    public void EnsureFlushed(ReadOnlySpan<byte> key)
    {
        if (_pending.IsEmpty) return;
        lock (_lock) FlushKeyLocked(key);
    }

    /// <summary>Drops a buffered key (e.g. on reorg) so a stale blob is not later flushed over the removal.</summary>
    public void Drop(ReadOnlySpan<byte> key)
    {
        if (_pending.IsEmpty) return;
        lock (_lock) _pendingBySpan.TryRemove(key, out _, out _);
    }

    private void FlushKeyLocked(ReadOnlySpan<byte> key)
    {
        // Write to the column before dropping the buffer entry so the key is always readable from one of them.
        if (_pendingBySpan.TryGetValue(key, out byte[]? actualKey, out Entry e))
        {
            _column.PutSpan(actualKey, e.Value, e.Flags);
            _pending.TryRemove(new KeyValuePair<byte[], Entry>(actualKey, e));
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
                // Let a batch of blocks accumulate before draining (unless the buffer is already large), so the
                // worker wakes ~once per window instead of once per block. Cancellation ends the window promptly.
                if (_pending.Count < FlushBatchMaxEntries && token.WaitHandle.WaitOne(FlushBatchIntervalMs)) return;
                while (_signal.CurrentCount > 0) _signal.Wait(0); // coalesce pending signals into one drain
                DrainAll();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // A transient column-write failure must not kill the worker (that would grow the buffer unbounded);
                // the buffered entries stay and are retried after a short back-off.
                if (token.WaitHandle.WaitOne(1000)) return;
                if (!_pending.IsEmpty) _signal.Release(); // re-arm: the failed drain already consumed its signal
            }
        }
    }

    private void DrainAll()
    {
        if (_pending.IsEmpty) return;
        lock (_lock)
        {
            foreach (KeyValuePair<byte[], Entry> kv in _pending)
            {
                _column.PutSpan(kv.Key, kv.Value.Value, kv.Value.Flags);
                _pending.TryRemove(kv); // compare-remove keeps a concurrent overwrite for the next drain
            }
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
    }
}
