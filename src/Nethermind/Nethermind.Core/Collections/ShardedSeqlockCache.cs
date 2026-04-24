// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Core.Collections;

/// <summary>
/// A sharded wrapper over multiple <see cref="SeqlockCache{TKey,TValue}"/> instances
/// to increase total capacity beyond the 32K-entry limit of a single cache.
/// Each shard is an independent SeqlockCache; the key's hash selects the shard.
/// </summary>
public sealed class ShardedSeqlockCache<TKey, TValue>
    where TKey : struct, IHash64bit<TKey>
    where TValue : class?
{
    private readonly SeqlockCache<TKey, TValue>[] _shards;
    private readonly int _shardMask;

    public ShardedSeqlockCache(int shardCount = 8)
    {
        _shards = new SeqlockCache<TKey, TValue>[shardCount];
        _shardMask = shardCount - 1;
        for (int i = 0; i < shardCount; i++)
        {
            _shards[i] = new SeqlockCache<TKey, TValue>();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private SeqlockCache<TKey, TValue> GetShard(in TKey key)
    {
        int hash = key.GetHashCode();
        int shard = (int)((uint)hash >> 16) & _shardMask;
        return _shards[shard];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(in TKey key, out TValue? value) => GetShard(in key).TryGetValue(in key, out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(in TKey key, TValue? value) => GetShard(in key).Set(in key, value);

    public void Clear()
    {
        for (int i = 0; i < _shards.Length; i++)
        {
            _shards[i].Clear();
        }
    }
}
