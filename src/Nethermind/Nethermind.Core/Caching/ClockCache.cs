// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;
using Nethermind.Core.Threading;

using CollectionExtensions = Nethermind.Core.Collections.CollectionExtensions;

namespace Nethermind.Core.Caching;

public sealed class ClockCache<TKey, TValue>(int maxCapacity, int? lockPartition = null) : ClockCacheBase<TKey>(maxCapacity)
    where TKey : struct, IEquatable<TKey>
{
    private readonly ConcurrentDictionary<TKey, LruCacheItem> _cacheMap = new(lockPartition ?? CollectionExtensions.LockPartitions, maxCapacity);
    private readonly McsLock _lock = new();

    public TValue Get(TKey key)
    {
        if (MaxCapacity == 0) return default!;

        if (_cacheMap.TryGetValue(key, out LruCacheItem ov))
        {
            MarkAccessed(ov.Offset);
            return ov.Value;
        }
        return default!;
    }

    public bool TryGet(TKey key, out TValue value)
    {
        value = default!;
        if (MaxCapacity == 0) return false;

        if (_cacheMap.TryGetValue(key, out LruCacheItem ov))
        {
            MarkAccessed(ov.Offset);
            value = ov.Value;
            return true;
        }

        return false;
    }

    public bool Set(TKey key, TValue val)
    {
        if (MaxCapacity == 0) return true;

        if (val is null)
        {
            return Delete(key);
        }

        if (_cacheMap.TryGetValue(key, out LruCacheItem ov))
        {
            // Fast path: atomic update using TryUpdate
            if (_cacheMap.TryUpdate(key, new(val, ov.Offset), comparisonValue: ov))
            {
                MarkAccessed(ov.Offset);
                return false;
            }
        }

        // Fallback to slow path with lock
        return SetSlow(key, val);
    }

    private bool SetSlow(TKey key, TValue val)
    {
        using var lockRelease = _lock.Acquire();

        // Recheck under lock
        if (_cacheMap.TryGetValue(key, out LruCacheItem ov))
        {
            _cacheMap[key] = new(val, ov.Offset);
            MarkAccessed(ov.Offset);
            return false;
        }

        int offset = _count;
        Debug.Assert(_cacheMap.Count == _count);
        if (FreeOffsets.Count > 0)
        {
            offset = FreeOffsets.Dequeue();
        }
        else if (offset >= MaxCapacity)
        {
            offset = Replace(key);
        }

        _cacheMap[key] = new(val, offset);
        KeyToOffset[offset] = key;
        _count++;
        Debug.Assert(_cacheMap.Count == _count);

        return true;
    }

    private int Replace(TKey key)
    {
        int position = Clock;
        int max = _count;
        while (true)
        {
            if (position >= max)
            {
                position = 0;
            }

            bool accessed = ClearAccessed(position);
            if (!accessed)
            {
                if (!_cacheMap.TryRemove(KeyToOffset[position], out _))
                {
                    ThrowInvalidOperationException();
                }
                _count--;
                break;
            }

            position++;
        }

        Clock = position + 1;
        return position;

        [DoesNotReturn]
        void ThrowInvalidOperationException()
        {
            throw new InvalidOperationException($"{nameof(ClockCache<TKey, TValue>)} removing item {KeyToOffset[position]} at position {position} that doesn't exist");
        }
    }

    public bool Delete(TKey key)
    {
        if (MaxCapacity == 0) return false;

        using var lockRelease = _lock.Acquire();

        if (_cacheMap.Remove(key, out LruCacheItem ov))
        {
            _count--;
            KeyToOffset[ov.Offset] = default;
            ClearAccessed(ov.Offset);
            FreeOffsets.Enqueue(ov.Offset);
            return true;
        }

        return false;
    }

    public new void Clear()
    {
        if (MaxCapacity == 0) return;

        using var lockRelease = _lock.Acquire();

        base.Clear();
        _cacheMap.NoResizeClear();
    }

    public bool Contains(TKey key)
    {
        if (MaxCapacity == 0) return false;
        return _cacheMap.ContainsKey(key);
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly struct LruCacheItem(TValue v, int offset) : IEquatable<LruCacheItem>
    {
        public readonly TValue Value = v;
        public readonly int Offset = offset;

        public bool Equals(LruCacheItem other)
            => other.Offset == Offset && EqualityComparer<TValue>.Default.Equals(other.Value, Value);
    }
}
