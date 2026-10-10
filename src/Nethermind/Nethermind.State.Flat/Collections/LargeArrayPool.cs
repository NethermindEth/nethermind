// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;

namespace Nethermind.State.Flat.Collections;

/// <summary>
/// A pool for large arrays, shared by all threads, that keeps a bounded number of arrays per power-of-two length.
/// </summary>
/// <remarks>
/// Unlike <see cref="System.Buffers.ArrayPool{T}.Shared"/> it keeps no per-thread copies. Like it, after a gen2 GC it
/// releases the arrays of each length that went unused for <see cref="IdleTrimMilliseconds"/>, and all of them when
/// memory load is high.
/// </remarks>
internal sealed class LargeArrayPool<T>
{
    internal const long IdleTrimMilliseconds = 60_000;

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

    /// <summary>Releases the arrays of every length idle for <see cref="IdleTrimMilliseconds"/>, or all of them.</summary>
    public void Trim(bool all, long nowMilliseconds)
    {
        foreach (Bucket bucket in _buckets) bucket.Trim(all, nowMilliseconds);
    }

    private int BucketIndex(int length) =>
        length <= 1 << _minLengthLog2 ? 0 : BitOperations.Log2(BitOperations.RoundUpToPowerOf2((uint)length)) - _minLengthLog2;

    private sealed class Bucket(int length, int capacity)
    {
        private readonly T[]?[] _arrays = new T[capacity][];
        private readonly Lock _lock = new();
        private int _count;
        private long _lastUsedMilliseconds = Environment.TickCount64;

        public int Length => length;

        public T[]? TryPop()
        {
            lock (_lock)
            {
                _lastUsedMilliseconds = Environment.TickCount64;
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
                _lastUsedMilliseconds = Environment.TickCount64;
                if (_count < _arrays.Length) _arrays[_count++] = array;
            }
        }

        public void Trim(bool all, long nowMilliseconds)
        {
            lock (_lock)
            {
                if (all || nowMilliseconds - _lastUsedMilliseconds >= IdleTrimMilliseconds)
                {
                    Array.Clear(_arrays, 0, _count);
                    _count = 0;
                }
            }
        }
    }

    /// <remarks>
    /// Unreachable from the start, so the GC finalizes it; re-registering keeps it alive in gen2, where it is only
    /// finalized again, and so trims the pool, when a gen2 collection runs. It holds the pool weakly and stops once the
    /// pool is gone.
    /// </remarks>
    private sealed class Gen2Trimmer(LargeArrayPool<T> pool)
    {
        private readonly WeakReference<LargeArrayPool<T>> _pool = new(pool);

        ~Gen2Trimmer()
        {
            if (!_pool.TryGetTarget(out LargeArrayPool<T>? target)) return;

            GCMemoryInfo info = GC.GetGCMemoryInfo();
            target.Trim(all: info.MemoryLoadBytes >= info.HighMemoryLoadThresholdBytes / 10 * 9, Environment.TickCount64);
            GC.ReRegisterForFinalize(this);
        }
    }
}
