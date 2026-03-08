// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.Core.Collections;

/// <summary>
/// 16-shard Dictionary+Lock for concurrent writes with lock-free reads when disabled.
/// </summary>
public sealed class ShardedDictionary<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>
    where TKey : notnull
{
    public const int NumShards = 16;
    private const int ShardMask = NumShards - 1;

    private readonly Lock[] _locks;
    private readonly Dictionary<TKey, TValue>[] _dicts;
    private volatile bool _lockEnabled = true;

    public ShardedDictionary()
    {
        _locks = new Lock[NumShards];
        _dicts = new Dictionary<TKey, TValue>[NumShards];
        for (int i = 0; i < NumShards; i++)
        {
            _locks[i] = new Lock();
            _dicts[i] = new Dictionary<TKey, TValue>();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetShardIndex(TKey key) => key.GetHashCode() & ShardMask;

    public TValue this[TKey key]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            int idx = GetShardIndex(key);
            if (_lockEnabled)
                lock (_locks[idx]) return _dicts[idx][key];
            return _dicts[idx][key];
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            int idx = GetShardIndex(key);
            if (_lockEnabled)
                lock (_locks[idx]) _dicts[idx][key] = value;
            else
                _dicts[idx][key] = value;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        int idx = GetShardIndex(key);
        if (_lockEnabled)
            lock (_locks[idx]) return _dicts[idx].TryGetValue(key, out value);
        return _dicts[idx].TryGetValue(key, out value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue GetOrAdd(TKey key, TValue value)
    {
        int idx = GetShardIndex(key);
        if (_lockEnabled)
        {
            lock (_locks[idx])
            {
                ref TValue? slot = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_dicts[idx], key, out bool exists);
                if (!exists) slot = value;
                return slot!;
            }
        }

        {
            ref TValue? slot = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_dicts[idx], key, out bool exists);
            if (!exists) slot = value;
            return slot!;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
    {
        int idx = GetShardIndex(key);
        if (_lockEnabled)
        {
            lock (_locks[idx])
            {
                ref TValue? slot = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_dicts[idx], key, out bool exists);
                if (!exists) slot = factory(key);
                return slot!;
            }
        }

        {
            ref TValue? slot = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_dicts[idx], key, out bool exists);
            if (!exists) slot = factory(key);
            return slot!;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ContainsKey(TKey key)
    {
        int idx = GetShardIndex(key);
        if (_lockEnabled)
            lock (_locks[idx]) return _dicts[idx].ContainsKey(key);
        return _dicts[idx].ContainsKey(key);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAdd(TKey key, TValue value)
    {
        int idx = GetShardIndex(key);
        if (_lockEnabled)
            lock (_locks[idx]) return _dicts[idx].TryAdd(key, value);
        return _dicts[idx].TryAdd(key, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRemove(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        int idx = GetShardIndex(key);
        if (_lockEnabled)
            lock (_locks[idx]) return _dicts[idx].Remove(key, out value);
        return _dicts[idx].Remove(key, out value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Remove(TKey key, [MaybeNullWhen(false)] out TValue value) =>
        TryRemove(key, out value);

    public int Count
    {
        get
        {
            int count = 0;
            for (int i = 0; i < NumShards; i++)
            {
                if (_lockEnabled)
                    lock (_locks[i]) count += _dicts[i].Count;
                else
                    count += _dicts[i].Count;
            }
            return count;
        }
    }

    public bool IsEmpty
    {
        get
        {
            for (int i = 0; i < NumShards; i++)
            {
                if (_lockEnabled)
                {
                    lock (_locks[i]) { if (_dicts[i].Count > 0) return false; }
                }
                else
                {
                    if (_dicts[i].Count > 0) return false;
                }
            }
            return true;
        }
    }

    public IEnumerable<TKey> Keys
    {
        get
        {
            for (int i = 0; i < NumShards; i++)
            {
                if (_lockEnabled) _locks[i].Enter();
                try
                {
                    foreach (TKey key in _dicts[i].Keys) yield return key;
                }
                finally
                {
                    if (_lockEnabled) _locks[i].Exit();
                }
            }
        }
    }

    public IEnumerable<TValue> Values
    {
        get
        {
            for (int i = 0; i < NumShards; i++)
            {
                if (_lockEnabled) _locks[i].Enter();
                try
                {
                    foreach (TValue value in _dicts[i].Values) yield return value;
                }
                finally
                {
                    if (_lockEnabled) _locks[i].Exit();
                }
            }
        }
    }

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        for (int i = 0; i < NumShards; i++)
        {
            if (_lockEnabled) _locks[i].Enter();
            try
            {
                foreach (KeyValuePair<TKey, TValue> kv in _dicts[i]) yield return kv;
            }
            finally
            {
                if (_lockEnabled) _locks[i].Exit();
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void EnableLock() => _lockEnabled = true;
    public void DisableLock() => _lockEnabled = false;

    public void AddOrUpdateFromShard(int shardIndex, ShardedDictionary<TKey, TValue> source)
    {
        foreach (KeyValuePair<TKey, TValue> kv in source._dicts[shardIndex])
            _dicts[shardIndex][kv.Key] = kv.Value;
    }

    public void RemoveFromShard(int shardIndex, Func<TKey, bool> predicate)
    {
        Dictionary<TKey, TValue> dict = _dicts[shardIndex];
        List<TKey>? toRemove = null;
        foreach (TKey key in dict.Keys)
        {
            if (predicate(key))
                (toRemove ??= new List<TKey>()).Add(key);
        }

        if (toRemove is not null)
            foreach (TKey key in toRemove)
                dict.Remove(key);
    }

    internal void NoResizeClear()
    {
        for (int i = 0; i < NumShards; i++)
            lock (_locks[i]) _dicts[i].Clear();
    }
}
