// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Core;

/// <summary>
/// Rocksdb's skiplist memtable have a fast path when the keys are inserted in ascending order. Otherwise, its seems
/// to be limited to 1Mil writes per second.
/// TODO: FlatDB layout need different key type to match the DB ordering.
/// </summary>
public class KeccakSortedBatch: IKeccakBatch
{
    private const int InitialBatchSize = 300;
    private static readonly int MaxCached = Environment.ProcessorCount;

    private readonly IBatch _baseBatch;
    private WriteFlags _writeFlags = WriteFlags.None;
    private bool _isDisposed;

    private int _counter = 0;
    private readonly ArrayPoolList<(ValueKeccak, int, byte[]?)> _batchData = new(InitialBatchSize);

    public KeccakSortedBatch(IBatch dbOnTheRocks)
    {
        _baseBatch = dbOnTheRocks;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }
        _isDisposed = true;

        if (_batchData.Count == 0)
        {
            // No data to write, skip empty batches
            _batchData.Dispose();
            _baseBatch.Dispose();
            return;
        }

        _batchData.AsSpan().Sort((item1, item2) =>
        {
            int keyCompare = item1.Item1.CompareTo(item2.Item1);
            if (keyCompare == 0)
            {
                // In case the key is the same, we sort in ascending counter
                return item1.Item2.CompareTo(item2.Item2);
            }

            return keyCompare;
        });

        // Sort the batch by key
        foreach ((ValueKeccak key, int _, byte[]? value) in _batchData)
        {
            _baseBatch.Set(key.BytesAsSpan, value, _writeFlags);
        }
        _batchData.Dispose();
        _baseBatch.Dispose();
    }

    public byte[]? Get(ValueKeccak key, ReadFlags flags = ReadFlags.None)
    {
        // Not checking _isDisposing here as for some reason, sometimes is is read after dispose
        return _baseBatch.Get(key.BytesAsSpan, flags);
    }

    public void Delete(ValueKeccak key)
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException($"Attempted to write a disposed batch");
        }

        _batchData.Add((key, Interlocked.Increment(ref _counter), null));
    }

    public void Set(ValueKeccak key, byte[]? value, WriteFlags flags = WriteFlags.None)
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException($"Attempted to write a disposed batch");
        }

        _batchData.Add((key, Interlocked.Increment(ref _counter), value));
        _writeFlags = flags;
    }
}

public static class BatchExtensions {
    public static KeccakSortedBatch ToKeccakBatch(this IBatch batch)
    {
        return new KeccakSortedBatch(batch);
    }
}
