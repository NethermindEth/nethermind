// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
namespace Nethermind.Core.Caching;

public sealed class ClockKeyCacheNonConcurrent<TKey>(int maxCapacity) : ClockCacheBase<TKey>(maxCapacity)
    where TKey : struct, IEquatable<TKey>
{
    private readonly Dictionary<TKey, int> _cacheMap = new();

    public bool Get(TKey key)
    {
        if (MaxCapacity == 0) return false;
        if (_cacheMap.TryGetValue(key, out int offset))
        {
            MarkAccessedNonConcurrent(offset);
            return true;
        }
        return false;
    }

    public bool Set(TKey key)
    {
        if (MaxCapacity == 0) return true;
        if (_cacheMap.TryGetValue(key, out int offset))
        {
            MarkAccessedNonConcurrent(offset);
            return false;
        }

        return SetSlow(key);
    }

    private bool SetSlow(TKey key)
    {
        int offset = _count;
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

            bool accessed = ClearAccessedNonConcurrent(position);
            if (!accessed)
            {
                if (!_cacheMap.Remove(KeyToOffset[position]))
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
            throw new InvalidOperationException($"{nameof(ClockKeyCacheNonConcurrent<TKey>)} removing item that doesn't exist");
        }
    }

    public bool Delete(TKey key)
    {
        if (_cacheMap.Remove(key, out int offset))
        {
            _count--;
            ref var node = ref KeyToOffset[offset];
            ClearAccessedNonConcurrent(offset);
            FreeOffsets.Enqueue(offset);
            return true;
        }

        return false;
    }

    public new void Clear()
    {
        base.Clear();
        _cacheMap.Clear();
    }

    public bool Contains(TKey key)
    {
        return _cacheMap.ContainsKey(key);
    }
}
