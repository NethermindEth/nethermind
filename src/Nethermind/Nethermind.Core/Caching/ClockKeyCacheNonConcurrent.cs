// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nethermind.Core.Caching
{
    public sealed class ClockKeyCacheNonConcurrent<TKey>
        where TKey : struct, IEquatable<TKey>
    {
        private const int BitShiftPerInt64 = 6;
        private readonly int _maxCapacity;
        private readonly Dictionary<TKey, int> _cacheMap;
        private readonly string _name;
        private readonly TKey[] _items;
        private readonly long[] _accessedBitmap;
        private readonly Queue<int> _freeOffsets = new();

        private int _clock = 0;

        public ClockKeyCacheNonConcurrent(int maxCapacity, string name)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxCapacity, 1);

            _name = name;
            _maxCapacity = maxCapacity;
            _cacheMap = new Dictionary<TKey, int>(maxCapacity / 2); // do not initialize it at the full capacity
            _items = new TKey[maxCapacity];
            _accessedBitmap = new long[GetInt64ArrayLengthFromBitLength(maxCapacity)];
        }

        public bool Get(TKey key)
        {
            if (_cacheMap.TryGetValue(key, out int offset))
            {
                MarkAccessed(offset);
                return true;
            }
            return false;
        }

        public bool Set(TKey key)
        {
            if (_cacheMap.TryGetValue(key, out int offset))
            {
                MarkAccessed(offset);
                return false;
            }

            return SetSlow(key);
        }

        private bool SetSlow(TKey key)
        {
            int offset = _cacheMap.Count;
            if (_freeOffsets.Count > 0)
            {
                offset = _freeOffsets.Dequeue();
            }
            else if (offset >= _maxCapacity)
            {
                offset = Replace(key);
            }

            _cacheMap[key] = offset;
            _items[offset] = key;

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
                    if (!_cacheMap.Remove(_items[position]))
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
            if (_cacheMap.Remove(key, out int offset))
            {
                ref var node = ref _items[offset];
                ClearAccessed(offset);
                _freeOffsets.Enqueue(offset);
                return true;
            }

            return false;
        }

        public void Clear()
        {
            _clock = 0;
            _cacheMap.Clear();
            if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>())
            {
                _items.AsSpan().Clear();
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
            accessedBitmapWord &= ~flags;

            return accessed;
        }

        private void MarkAccessed(int position)
        {
            uint offset = (uint)position >> BitShiftPerInt64;
            long flags = 1L << position;

            ref long accessedBitmapWord = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_accessedBitmap), offset);
            accessedBitmapWord |= flags;
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
    }
}
