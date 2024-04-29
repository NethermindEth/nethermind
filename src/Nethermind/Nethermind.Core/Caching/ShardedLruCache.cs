// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Caching;

using System;
using System.Collections.Generic;

// ChatGPT generated wrapper around LruCache. Some method removed for simplicity.
public sealed class ShardedLruCache<TKey, TValue> : ICache<TKey, TValue> where TKey : notnull
{
    private readonly int _numberOfShards;
    private readonly LruCache<TKey, TValue>[] _shards;

    public ShardedLruCache(int maxCapacity, string name): this(maxCapacity, Environment.ProcessorCount, name)
    {
    }

    public ShardedLruCache(int maxCapacity, int numberOfShards, string name)
    {
        if (numberOfShards <= 0)
            throw new ArgumentOutOfRangeException(nameof(numberOfShards), "Number of shards must be greater than 0");

        _numberOfShards = numberOfShards;
        _shards = new LruCache<TKey, TValue>[numberOfShards];

        int capacityPerShard = Math.Max(maxCapacity / numberOfShards, 1);
        for (int i = 0; i < numberOfShards; i++)
        {
            _shards[i] = new LruCache<TKey, TValue>(capacityPerShard, $"{name}_Shard{i}");
        }
    }

    private LruCache<TKey, TValue> GetShard(TKey key)
    {
        int shardIndex = Math.Abs(key.GetHashCode()) % _numberOfShards;
        return _shards[shardIndex];
    }

    public void Clear()
    {
        foreach (var shard in _shards)
        {
            shard.Clear();
        }
    }

    public TValue Get(TKey key)
    {
        return GetShard(key).Get(key);
    }

    public bool TryGet(TKey key, out TValue value)
    {
        return GetShard(key).TryGet(key, out value);
    }

    public bool Set(TKey key, TValue val)
    {
        return GetShard(key).Set(key, val);
    }

    public bool Delete(TKey key)
    {
        return GetShard(key).Delete(key);
    }

    public bool Contains(TKey key)
    {
        return GetShard(key).Contains(key);
    }
}
