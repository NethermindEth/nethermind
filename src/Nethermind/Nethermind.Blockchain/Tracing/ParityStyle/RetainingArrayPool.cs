// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Numerics;
using Nethermind.Core.Collections;

namespace Nethermind.Blockchain.Tracing.ParityStyle;

/// <summary>An array pool that keeps every array returned to it for the next rentals of its owner.</summary>
/// <remarks>
/// The shared pool keeps one array of each size per thread and a bounded number per core. A tracer that rents one buffer per
/// action and returns the whole trace's buffers together overflows it, so its next trace misses it and allocates,
/// after taking a lock per partition it probes. This pool keeps all of them until <see cref="Clear"/> hands them to
/// the shared pool, so it must only be given arrays it rented. It takes no lock: its owner must not use it from two
/// threads at once.
/// </remarks>
internal sealed class RetainingArrayPool<T> : ArrayPool<T> where T : unmanaged
{
    private const int BucketCount = 32;
    // The shared pool hands out no shorter array, so a smaller request looks for one of these.
    private const int MinimumArrayLength = 16;
    // An array from the shared pool is a power of two, so the fitting bucket and the next one give at most four times
    // the request, or 32 elements; a larger array stays for a request of its own size.
    private const int BucketsSearched = 2;

    private readonly ArrayPoolList<T[]>?[] _buckets = new ArrayPoolList<T[]>?[BucketCount];

    public override T[] Rent(int minimumLength)
    {
        if (minimumLength <= 0) return Shared.Rent(minimumLength);

        // Bucket i holds arrays of at least 2^i elements, so the first non-empty bucket from here fits.
        int first = BitOperations.Log2((uint)Math.Max(minimumLength, MinimumArrayLength) - 1) + 1;
        int end = Math.Min(first + BucketsSearched, BucketCount);
        for (int i = first; i < end; i++)
        {
            ArrayPoolList<T[]>? bucket = _buckets[i];
            if (bucket is { Count: > 0 })
            {
                int last = bucket.Count - 1;
                T[] array = bucket[last];
                bucket.Truncate(last);
                return array;
            }
        }

        return Shared.Rent(minimumLength);
    }

    public override void Return(T[] array, bool clearArray = false)
    {
        if (array.Length == 0) return;
        if (clearArray) Array.Clear(array);

        (_buckets[BitOperations.Log2((uint)array.Length)] ??= new ArrayPoolList<T[]>(4)).Add(array);
    }

    /// <summary>Hands every retained array to the shared pool.</summary>
    public void Clear()
    {
        for (int i = 0; i < _buckets.Length; i++)
        {
            ArrayPoolList<T[]>? bucket = _buckets[i];
            if (bucket is null) continue;

            foreach (T[] array in bucket.AsSpan())
            {
                Shared.Return(array);
            }

            bucket.Dispose();
            _buckets[i] = null;
        }
    }
}
