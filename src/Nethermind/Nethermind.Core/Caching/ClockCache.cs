// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

using Nethermind.Core.Threading;

namespace Nethermind.Core.Caching
{
    public sealed class ClockCache<TKey, TValue>
        where TKey : struct, IEquatable<TKey>
    {
        private const int BitShiftPerInt64 = 6;
        private readonly int _maxCapacity;
        private readonly ConcurrentDictionary<TKey, LruCacheItem> _cacheMap;
        private readonly McsLock _lock = new();
        private readonly string _name;
        private readonly TKey[] _keys;
        private readonly long[] _accessedBitmap;
        private readonly Queue<int> _freeOffsets = new();

        private int _clock = 0;

        public ClockCache(int maxCapacity, string name)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxCapacity, 1);

            _name = name;
            _maxCapacity = maxCapacity;
            _cacheMap = new ConcurrentDictionary<TKey, LruCacheItem>(); // do not initialize it at the full capacity
            _keys = new TKey[maxCapacity];
            _accessedBitmap = new long[GetInt64ArrayLengthFromBitLength(maxCapacity)];
        }

        public TValue Get(TKey key)
        {
            if (_cacheMap.TryGetValue(key, out LruCacheItem? ov))
            {
                MarkAccessed(ov.Offset);
                return ov.Value;
            }
            return default!;
        }

        public bool TryGet(TKey key, out TValue value)
        {
            using var lockRelease = _lock.Acquire();

            if (_cacheMap.TryGetValue(key, out LruCacheItem? ov))
            {
                MarkAccessed(ov.Offset);
                value = ov.Value;
                return true;
            }

            value = default!;
            return false;
        }

        public bool Set(TKey key, TValue val)
        {
            if (val is null)
            {
                return Delete(key);
            }

            if (_cacheMap.TryGetValue(key, out LruCacheItem? ov))
            {
                ov.Value = val;
                MarkAccessed(ov.Offset);
                return false;
            }

            return SetSlow(key, val);
        }

        private bool SetSlow(TKey key, TValue val)
        {
            using var lockRelease = _lock.Acquire();

            // Recheck under lock
            if (_cacheMap.TryGetValue(key, out LruCacheItem? ov))
            {
                ov.Value = val;
                MarkAccessed(ov.Offset);
                return false;
            }

            int offset = _cacheMap.Count;
            if (_freeOffsets.Count > 0)
            {
                offset = _freeOffsets.Dequeue();
            }
            else if (offset >= _maxCapacity)
            {
                offset = Replace(key);
            }

            _cacheMap[key] = new LruCacheItem(offset, val);
            _keys[offset] = key;

            return true;
        }

        private int Replace(TKey key)
        {
            int position = _clock;
            while (true)
            {
                if (position >= _maxCapacity)
                {
                    position = 0;
                }

                bool accessed = ClearAccessed(position);
                if (!accessed)
                {
                    if (!_cacheMap.TryRemove(_keys[position], out _))
                    {
                        ThrowInvalidOperationException();
                    }
                    break;
                }

                position++;
            }

            _clock = position + 1;
            return position;

            [DoesNotReturn]
            static void ThrowInvalidOperationException()
            {
                throw new InvalidOperationException($"{nameof(ClockKeyCache<TKey>)} removing item that doesn't exist");
            }
        }

        public bool Delete(TKey key)
        {
            using var lockRelease = _lock.Acquire();

            if (_cacheMap.Remove(key, out LruCacheItem? ov))
            {
                _keys[ov.Offset] = default;
                ClearAccessed(ov.Offset);
                _freeOffsets.Enqueue(ov.Offset);
                return true;
            }

            return false;
        }

        public void Clear()
        {
            using var lockRelease = _lock.Acquire();

            _clock = 0;
            _cacheMap.Clear();
            if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>())
            {
                _keys.AsSpan().Clear();
            }
            _accessedBitmap.AsSpan().Clear();
        }

        public bool Contains(TKey key)
        {
            return _cacheMap.ContainsKey(key);
        }

        public int Count => _cacheMap.Count;

        private bool ClearAccessed(int position)
        {
            uint offset = (uint)position >> BitShiftPerInt64;
            long flags = 1L << position;

            ref long accessedBitmapWord = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_accessedBitmap), offset);
            bool accessed = (accessedBitmapWord & flags) != 0;

            if (accessed)
            {
                // Clear the accessed bit
                flags = ~flags;
                long current = Volatile.Read(ref accessedBitmapWord);
                while (true)
                {
                    long previous = Interlocked.CompareExchange(ref accessedBitmapWord, current & flags, current);
                    if (previous == current)
                    {
                        break;
                    }
                    current = previous;
                }
            }

            return accessed;
        }

        private void MarkAccessed(int position)
        {
            uint offset = (uint)position >> BitShiftPerInt64;
            long flags = 1L << position;

            ref long accessedBitmapWord = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_accessedBitmap), offset);

            long current = Volatile.Read(ref accessedBitmapWord);
            while (true)
            {
                long previous = Interlocked.CompareExchange(ref accessedBitmapWord, current | flags, current);
                if (previous == current)
                {
                    break;
                }
                current = previous;
            }
        }

        /// <summary>
        /// Used for conversion between different representations of bit array.
        /// Returns (n + (64 - 1)) / 64, rearranged to avoid arithmetic overflow.
        /// For example, in the bit to int case, the straightforward calc would
        /// be (n + 63) / 64, but that would cause overflow. So instead it's
        /// rearranged to ((n - 1) / 64) + 1.
        /// Due to sign extension, we don't need to special case for n == 0, if we use
        /// bitwise operations (since ((n - 1) >> 6) + 1 = 0).
        /// This doesn't hold true for ((n - 1) / 64) + 1, which equals 1.
        ///
        /// Usage:
        /// GetInt32ArrayLengthFromBitLength(77): returns how many ints must be
        /// allocated to store 77 bits.
        /// </summary>
        /// <param name="n"></param>
        /// <returns>how many ints are required to store n bytes</returns>
        private static int GetInt64ArrayLengthFromBitLength(int n) =>
            (n - 1 + (1 << BitShiftPerInt64)) >>> BitShiftPerInt64;

        private class LruCacheItem(int offset, TValue v)
        {
            public readonly int Offset = offset;
            public TValue Value = v;
        }
    }
}
