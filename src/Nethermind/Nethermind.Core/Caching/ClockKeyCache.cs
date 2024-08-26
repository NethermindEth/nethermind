// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

using Nethermind.Core.Collections;
using Nethermind.Core.Threading;

using CollectionExtensions = Nethermind.Core.Collections.CollectionExtensions;

namespace Nethermind.Core.Caching;

public sealed class ClockKeyCache<TKey>(int maxCapacity) : ClockCacheBase<TKey>(maxCapacity)
    where TKey : struct, IEquatable<TKey>
{
    private readonly ConcurrentDictionary<TKey, int> _cacheMap = new(CollectionExtensions.LockPartitions, maxCapacity);
    private readonly McsLock _lock = new();

    public bool Get(TKey key)
    {
        if (MaxCapacity == 0) return false;
        if (_cacheMap.TryGetValue(key, out int offset))
        {
            MarkAccessed(offset);
            return true;
        }
        return false;
    }

    public bool Set(TKey key)
    {
        if (MaxCapacity == 0) return true;
        if (_cacheMap.TryGetValue(key, out int offset))
        {
            MarkAccessed(offset);
            return false;
        }

        return SetSlow(key);
    }

    private bool SetSlow(TKey key)
    {
        if (MaxCapacity == 0) return false;
        using var lockRelease = _lock.Acquire();

        // Recheck under lock
        if (_cacheMap.TryGetValue(key, out int offset))
        {
            MarkAccessed(offset);
            return false;
        }

        offset = _count;
        Debug.Assert(_cacheMap.Count == _count);
        if (FreeOffsets.Count > 0)
        {
            offset = FreeOffsets.Dequeue();
        }
        else if (offset >= MaxCapacity)
        {
            offset = Replace(key);
        }

        _cacheMap[key] = offset;
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
        static void ThrowInvalidOperationException()
        {
            throw new InvalidOperationException($"{nameof(ClockKeyCache<TKey>)} removing item that doesn't exist");
        }
    }

    public bool Delete(TKey key)
    {
        if (MaxCapacity == 0) return false;
        using var lockRelease = _lock.Acquire();

        if (_cacheMap.Remove(key, out int offset))
        {
            _count--;
            ClearAccessed(offset);
            FreeOffsets.Enqueue(offset);
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
}
