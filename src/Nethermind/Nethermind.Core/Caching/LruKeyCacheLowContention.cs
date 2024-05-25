// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;

namespace Nethermind.Core.Caching;

public sealed class LruKeyCacheLowContention<TKey> where TKey : notnull
{
    private static int CacheCount = (int)BitOperations.RoundUpToPowerOf2((uint)Environment.ProcessorCount * 2);
    private static int CacheMax = CacheCount - 1;
    private readonly LruKeyCache<TKey>[] _caches;

    public LruKeyCacheLowContention(int maxCapacity, string name)
        : this(maxCapacity, 0, name)
    {
    }

    public LruKeyCacheLowContention(int maxCapacity, int startCapacity, string name)
    {
        startCapacity = Math.Max(CacheCount, startCapacity);
        maxCapacity = Math.Max(CacheCount, maxCapacity);

        _caches = new LruKeyCache<TKey>[CacheCount];
        for (int i = 0; i < _caches.Length; i++)
        {
            // Cache per nibble to reduce contention as TxPool is very parallel
            _caches[i] = new LruKeyCache<TKey>(startCapacity / CacheCount, maxCapacity / CacheCount, $"{name} {i}");
        }
    }

    public bool Get(TKey key)
    {
        var cache = _caches[GetCacheIndex(key)];
        return cache.Get(key);
    }

    public bool Set(TKey key)
    {
        var cache = _caches[GetCacheIndex(key)];
        return cache.Set(key);
    }

    public void Clear()
    {
        foreach (var cache in _caches)
        {
            cache.Clear();
        }
    }

    private static int GetCacheIndex(TKey key) => key.GetHashCode() & CacheMax;
}
