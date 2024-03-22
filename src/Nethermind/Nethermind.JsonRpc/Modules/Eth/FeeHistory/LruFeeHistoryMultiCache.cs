// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Caching;


namespace Nethermind.JsonRpc.Modules.Eth.FeeHistory;

public sealed class LruFeeHistoryMultiCache<TKey, TValue>
{
    private readonly int _totalMaxCapacity;
    private LruCache<TKey, TValue>[] _caches;
    private readonly string _name;
    private readonly IBlockFinder _blockFinder;
    private long _headBlockNumber;


    public LruFeeHistoryMultiCache(int maxCapacity, int startCapacity, string name, IBlockFinder blockFinder, int? size = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCapacity, 2);

        _name = name;
        _caches = new LruCache<TKey, TValue>[size ?? 10];
        _totalMaxCapacity = maxCapacity * _caches.Length;
        _headBlockNumber = blockFinder.Head?.Number ?? 0; // no head is reduced to block 0
        _blockFinder = blockFinder;
        for (var index = 0; index < _caches.Length; index++)
        {
            _caches[index] = new LruCache<TKey, TValue>(maxCapacity, startCapacity, $"{name}_{index}");
        }
    }

    public LruFeeHistoryMultiCache(int maxCapacity, string name, IBlockFinder blockFinder, int? size = null)
        : this(maxCapacity, 0, name, blockFinder, size)
    {
    }

    private LruCache<TKey, TValue> GetBucket(long partitionKey)
    {
        // distribution of buckets
        // 20% to 0-70th percentile
        // 20% to newer blocks
        int firstSegmentBucketSize = (int)(0.2 * _caches.Length);
        // 60% to the remaining 30% before head i.e (70 -100th percentile)
        int secondSegmentBucketSize = _caches.Length - 2 * firstSegmentBucketSize;

        long firstSegment = (long)(_headBlockNumber * 0.7);

        if (partitionKey <= firstSegment)
        {
            return _caches[(int)(_headBlockNumber % firstSegmentBucketSize)];
        }
        return partitionKey > _headBlockNumber ? _caches[8 + (int)(_headBlockNumber % firstSegmentBucketSize)] : _caches[2 + (int)(_headBlockNumber % secondSegmentBucketSize)];
    }

    public void Clear()
    {

        // may be acquire all the locks from each bucket first.
        // maybe acquire creation lock (only applicable to this function) recreate all queues then swap in O(1)
        // update head then release lock
        // maybe just not clear, simply change the head and old value would get invalidated automatically due to LRU behaviour
        // since clear is used only for re-partitioning (maybe make private)
        foreach (var cache in _caches)
        {
            cache.Clear();
        }
    }

    public TValue Get(TKey key, long? blockNumber = null)
    {
        if (blockNumber is not null) return GetBucket(blockNumber.Value).Get(key);

        foreach (var cache in _caches)
        {
            if (cache.TryGet(key, out TValue value)) return value;
        }

#pragma warning disable 8603
        // fixed C# 9
        return default;
#pragma warning restore 8603
    }

    public bool TryGet(TKey key, out TValue value, long blockNumber)
    {
        return GetBucket(blockNumber).TryGet(key, out value);
    }

    public bool Set(TKey key, TValue val, long blockNumber)
    {
        var currentHeadBlockNumber = _blockFinder.Head!.Number;
        if (currentHeadBlockNumber < _headBlockNumber + 1024) return GetBucket(blockNumber).Set(key, val);
        Clear(); // might need to auto resize to larger number of buckets, although for now squishing would suffice
        _headBlockNumber = currentHeadBlockNumber;
        return GetBucket(blockNumber).Set(key, val);
    }

    public bool Delete(TKey key, long blockNumber)
    {
        return GetBucket(blockNumber).Delete(key);
    }

    public bool Contains(TKey key, long? blockNumber = null)
    {
        if (blockNumber < 0) return false;
        return blockNumber is not null ? GetBucket(blockNumber.Value).Contains(key) : _caches.Any(cache => cache.Contains(key));
    }

    // does not give real time results
    // because it acquires and releases locks in sequence
    public KeyValuePair<TKey, TValue>[] ToArray()
    {


        int i = 0;
        var array = new KeyValuePair<TKey, TValue>[Size];
        foreach (var cache in _caches)
        {
            var srcArray = cache.ToArray();
            var expectedMinLength = i + srcArray.Length;
            if (array.Length < expectedMinLength) Array.Resize(ref array, expectedMinLength); // might increase on releasing lock, would never decrease
            Array.Copy(srcArray, 0, array, i, srcArray.Length);
            i += srcArray.Length;
        }

        return array;
    }

    public int Size
    {
        get
        {
            return _caches.Sum(cache => cache.Size);
        }
    }

    // does not give real time results
    // because it acquires and releases locks in sequence
    [MethodImpl(MethodImplOptions.Synchronized)]
    public TValue[] GetValues()
    {
        int i = 0;
        var array = new TValue[Size];
        foreach (var cache in _caches)
        {
            var srcArray = cache.GetValues();
            var expectedMinLength = i + srcArray.Length;
            if (array.Length < expectedMinLength) Array.Resize(ref array, expectedMinLength); // might increase on each iteration, would never decrease
            Array.Copy(srcArray, 0, array, i, srcArray.Length);
            i += srcArray.Length;
        }

        return array;
    }

    public long MemorySize
    {
        get
        {
            return _caches.Sum(cache => cache.MemorySize);
        }
    }

    public int NumberOfCaches
    {
        get
        {
            return _caches.Length;
        }
    }
}
