// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Nethermind.Core.Utils;

/// <summary>
/// Batches writes into a set of concurrent batches. For cases where throughput matter, but not atomicity.
/// </summary>
public class ConcurrentWriteBatcher : IWriteBatch
{
    private const long PersistEveryNWrite = 10000;

    private long _counter = 0;
    private readonly ConcurrentQueue<IWriteBatch> _batches = new();
    private readonly IKeyValueStoreWithBatching _underlyingDb;
    private bool _disposing = false;

    public ConcurrentWriteBatcher(IKeyValueStoreWithBatching underlyingDb)
    {
        _underlyingDb = underlyingDb;
    }

    public void Dispose()
    {
        _disposing = true;
        foreach (IWriteBatch batch in _batches)
        {
            batch.Dispose();
        }
    }

    public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
    {
        IWriteBatch currentBatch = RentWriteBatch();
        currentBatch.PutSpan(key, value, flags);
        ReturnWriteBatch(currentBatch);
    }

    public void DeleteByRange(Span<byte> startKey, Span<byte> endKey)
    {
        _underlyingDb.DeleteByRange(startKey, endKey);
    }

    public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
    {
        IWriteBatch currentBatch = RentWriteBatch();
        currentBatch.Set(key, value, flags);
        ReturnWriteBatch(currentBatch);
    }

    private void ReturnWriteBatch(IWriteBatch currentBatch)
    {
        long val = Interlocked.Increment(ref _counter);
        if (val % PersistEveryNWrite == 0)
        {
            currentBatch.Dispose();
        }
        else
        {
            _batches.Enqueue(currentBatch);
        }
    }

    private IWriteBatch RentWriteBatch()
    {
        if (_disposing) throw new InvalidOperationException("Trying to set while disposing");
        if (!_batches.TryDequeue(out IWriteBatch? currentBatch))
        {
            currentBatch = _underlyingDb.StartWriteBatch();
        }

        return currentBatch;
    }
}
