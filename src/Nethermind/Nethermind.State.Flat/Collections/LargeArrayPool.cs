// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;

namespace Nethermind.State.Flat.Collections;

/// <summary>
/// A pool for large arrays, shared by all threads, that keeps a bounded number of arrays per power-of-two length.
/// </summary>
/// <remarks>
/// Unlike <see cref="System.Buffers.ArrayPool{T}.Shared"/> it keeps no per-thread copies. After every gen2 GC it
/// releases the arrays of each length that was neither rented nor returned since the previous one, and all of them when
/// memory load is high.
/// </remarks>
internal sealed class LargeArrayPool<T>
{
    private readonly int _minLengthLog2;
    private readonly Bucket[] _buckets;

    public LargeArrayPool(int minLength, int maxLength, int maxArraysPerLength, bool trimOnGen2 = true)
    {
        _minLengthLog2 = BitOperations.Log2(BitOperations.RoundUpToPowerOf2((uint)minLength));
        int maxLengthLog2 = BitOperations.Log2(BitOperations.RoundUpToPowerOf2((uint)maxLength));
        _buckets = new Bucket[maxLengthLog2 - _minLengthLog2 + 1];
        for (int i = 0; i < _buckets.Length; i++) _buckets[i] = new Bucket(1 << (_minLengthLog2 + i), maxArraysPerLength);
        if (trimOnGen2) _ = new Gen2Trimmer(this);
    }

    /// <summary>An array of at least <paramref name="minimumLength"/>, pooled when its length class is.</summary>
    public T[] Rent(int minimumLength)
    {
        int index = BucketIndex(minimumLength);
        if (index >= _buckets.Length) return new T[minimumLength];

        Bucket bucket = _buckets[index];
        return bucket.TryPop() ?? new T[bucket.Length];
    }

    /// <summary>Keeps the array when its length is one of the pooled lengths and that length has room.</summary>
    public void Return(T[] array)
    {
        int index = BucketIndex(array.Length);
        if (index < _buckets.Length && _buckets[index].Length == array.Length) _buckets[index].TryPush(array);
    }

    /// <summary>Releases the arrays of every length not used since the previous trim, or all of them.</summary>
    public void Trim(bool all)
    {
        foreach (Bucket bucket in _buckets) bucket.Trim(all);
    }

    private int BucketIndex(int length) =>
        length <= 1 << _minLengthLog2 ? 0 : BitOperations.Log2(BitOperations.RoundUpToPowerOf2((uint)length)) - _minLengthLog2;

    private sealed class Bucket(int length, int capacity)
    {
        private readonly T[]?[] _arrays = new T[capacity][];
        private readonly Lock _lock = new();
        private int _count;
        private bool _usedSinceTrim;

        public int Length => length;

        public T[]? TryPop()
        {
            lock (_lock)
            {
                _usedSinceTrim = true;
                if (_count == 0) return null;

                T[] array = _arrays[--_count]!;
                _arrays[_count] = null;
                return array;
            }
        }

        public void TryPush(T[] array)
        {
            lock (_lock)
            {
                _usedSinceTrim = true;
                if (_count < _arrays.Length) _arrays[_count++] = array;
            }
        }

        public void Trim(bool all)
        {
            lock (_lock)
            {
                if (all || !_usedSinceTrim)
                {
                    Array.Clear(_arrays, 0, _count);
                    _count = 0;
                }

                _usedSinceTrim = false;
            }
        }
    }

    /// <remarks>
    /// Unreachable from the start, so the GC finalizes it; re-registering keeps it alive in gen2, where it is only
    /// finalized again, and so trims the pool, when a gen2 collection runs.
    /// </remarks>
    private sealed class Gen2Trimmer(LargeArrayPool<T> pool)
    {
        ~Gen2Trimmer()
        {
            GCMemoryInfo info = GC.GetGCMemoryInfo();
            pool.Trim(all: info.MemoryLoadBytes >= info.HighMemoryLoadThresholdBytes / 10 * 9);
            GC.ReRegisterForFinalize(this);
        }
    }
}
