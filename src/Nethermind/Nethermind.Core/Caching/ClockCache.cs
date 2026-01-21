// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
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
#if ZKVM
    // `lockPartition` is intentionally ignored in ZKVM implementation.
    // Consume it to avoid CS9113 when TreatWarningsAsErrors is enabled.
    private readonly int? _lockPartitionUnused = lockPartition;

    private readonly McsLock _lock = new();
    private readonly ClockKeyCacheNonConcurrent<TKey> _keyCache = new(maxCapacity);
    private readonly TValue?[] _values = maxCapacity > 0 ? new TValue?[maxCapacity] : [];

    public TValue Get(TKey key)
    {
        if (MaxCapacity == 0) return default!;

        using var _ = _lock.Acquire();

        if (_keyCache.Get(key))
        {
            // We don't get the offset back from ClockKeyCacheNonConcurrent, so we resolve it from KeyToOffset.
            // This is O(n) but acceptable for ZKVM builds and keeps us away from ConcurrentDictionary/EqualityComparer issues.
            int offset = FindOffset(key);
            if ((uint)offset < (uint)_values.Length)
            {
                MarkAccessed(offset);
                return _values[offset]!;
            }
        }

        return default!;
    }

    public bool TryGet(TKey key, out TValue value)
    {
        value = default!;
        if (MaxCapacity == 0) return false;

        using var _ = _lock.Acquire();

        if (_keyCache.Get(key))
        {
            int offset = FindOffset(key);
            if ((uint)offset < (uint)_values.Length)
            {
                MarkAccessed(offset);
                TValue? v = _values[offset];
                if (v is not null)
                {
                    value = v;
                    return true;
                }
            }
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

        using var _ = _lock.Acquire();

        if (_keyCache.Get(key))
        {
            int existingOffset = FindOffset(key);
            if ((uint)existingOffset < (uint)_values.Length)
            {
                _values[existingOffset] = val;
                MarkAccessed(existingOffset);
                return false;
            }

            // If we somehow cannot find the offset, fall through to slow insert path.
        }

        int offset = _count;
        if (FreeOffsets.Count > 0)
        {
            offset = FreeOffsets.Dequeue();
        }
        else if (offset >= MaxCapacity)
        {
            offset = ReplaceUnderLock(key);
        }

        _keyCache.Set(key);
        KeyToOffset[offset] = key;
        _values[offset] = val;

        _count++;
        return true;
    }

    public bool Delete(TKey key) => Delete(key, out _);

    public bool Delete(TKey key, [NotNullWhen(true)] out TValue? value)
    {
        value = default;
        if (MaxCapacity == 0) return false;

        using var _ = _lock.Acquire();

        // Determine offset before deleting from key cache.
        int offset = FindOffset(key);
        bool removed = _keyCache.Delete(key);

        if (!removed)
        {
            return false;
        }

        if ((uint)offset < (uint)_values.Length)
        {
            value = _values[offset];
            _values[offset] = default;

            _count--;
            KeyToOffset[offset] = default;
            ClearAccessed(offset);
            FreeOffsets.Enqueue(offset);

            return value is not null;
        }

        // Offset not found: still consider it removed from key cache.
        _count--;
        return false;
    }

    public new void Clear()
    {
        if (MaxCapacity == 0) return;

        using var _ = _lock.Acquire();

        base.Clear();
        _keyCache.Clear();
        Array.Clear(_values, 0, _values.Length);
    }

    public bool Contains(TKey key)
    {
        if (MaxCapacity == 0) return false;

        using var _ = _lock.Acquire();
        return _keyCache.Contains(key);
    }

    private int ReplaceUnderLock(TKey key)
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
                TKey evictedKey = KeyToOffset[position];
                _keyCache.Delete(evictedKey);

                _values[position] = default;
                KeyToOffset[position] = default;

                _count--;
                break;
            }

            position++;
        }

        Clock = position + 1;
        return position;
    }

    private int FindOffset(TKey key)
    {
        ReadOnlySpan<TKey> span = KeyToOffset;
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i].Equals(key))
            {
                return i;
            }
        }

        return -1;
    }

#else
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
            throw new InvalidOperationException(
                $"{nameof(ClockCache<TKey, TValue>)} removing item {KeyToOffset[position]} at position {position} that doesn't exist");
        }
    }

    public bool Delete(TKey key) => Delete(key, out _);

    public bool Delete(TKey key, [NotNullWhen(true)] out TValue? value)
    {
        if (MaxCapacity == 0)
        {
            value = default;
            return false;
        }

        using var lockRelease = _lock.Acquire();

        if (_cacheMap.Remove(key, out LruCacheItem ov))
        {
            _count--;
            KeyToOffset[ov.Offset] = default;
            ClearAccessed(ov.Offset);
            FreeOffsets.Enqueue(ov.Offset);
            value = ov.Value;
            return ov.Value != null;
        }

        value = default;
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
        {
            if (other.Offset != Offset)
            {
                return false;
            }

            if (typeof(TValue).IsValueType)
            {
                return EqualityComparer<TValue>.Default.Equals(other.Value, Value);
            }

            return ReferenceEquals(other.Value, Value);
        }
    }
#endif
}
