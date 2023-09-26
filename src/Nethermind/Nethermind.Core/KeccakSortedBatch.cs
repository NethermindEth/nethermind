// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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

    private static ConcurrentQueue<Dictionary<ValueKeccak, byte[]?>> s_cache = new();

    private readonly IBatch _baseBatch;
    private WriteFlags _writeFlags = WriteFlags.None;

    private Dictionary<ValueKeccak, byte[]?> _batchData = CreateOrGetFromCache();

    public KeccakSortedBatch(IBatch dbOnTheRocks)
    {
        _baseBatch = dbOnTheRocks;
    }

    public void Dispose()
    {
        Dictionary<ValueKeccak, byte[]?> batchData = _batchData;
        // Clear the _batchData reference so is null if Get is called.
        _batchData = null!;
        if (batchData.Count == 0)
        {
            // No data to write, skip empty batches
            ReturnToCache(batchData);
            _baseBatch.Dispose();
            return;
        }

        // Sort the batch by key
        foreach (var kv in batchData.OrderBy(i => i.Key))
        {
            _baseBatch.Set(kv.Key.BytesAsSpan, kv.Value, _writeFlags);
        }
        ReturnToCache(batchData);

        _baseBatch.Dispose();
    }

    public byte[]? Get(ValueKeccak key, ReadFlags flags = ReadFlags.None)
    {
        // Not checking _isDisposing here as for some reason, sometimes is is read after dispose
        return _batchData?.TryGetValue(key, out byte[]? value) ?? false ? value : _baseBatch.Get(key.BytesAsSpan);
    }

    public void Set(ValueKeccak key, byte[]? value, WriteFlags flags = WriteFlags.None)
    {
        _batchData[key] = value;

        _writeFlags = flags;
    }

    private static Dictionary<ValueKeccak, byte[]?> CreateOrGetFromCache()
    {
        if (s_cache.TryDequeue(out Dictionary<ValueKeccak, byte[]?>? batchData))
        {
            return batchData;
        }

        return new Dictionary<ValueKeccak, byte[]?>(InitialBatchSize);
    }

    private static void ReturnToCache(Dictionary<ValueKeccak, byte[]?> batchData)
    {
        if (s_cache.Count >= MaxCached) return;

        batchData.Clear();
        batchData.TrimExcess(capacity: InitialBatchSize);
        s_cache.Enqueue(batchData);
    }
}

public static class BatchExtensions {
    public static KeccakSortedBatch ToKeccakBatch(this IBatch batch)
    {
        return new KeccakSortedBatch(batch);
    }
}
