// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
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
    // The largest array the shared pool hands out of its buckets; it allocates a longer one at the requested length.
    private const int MaxSharedArrayLength = 1 << 30;

    private readonly ArrayPoolList<T[]>?[] _buckets = new ArrayPoolList<T[]>?[BucketCount];

    /// <summary>The number of elements in the arrays the pool keeps.</summary>
    public long RetainedLength { get; private set; }

    /// <summary>The length of the array <see cref="Rent"/> takes from the shared pool when it keeps none that fits.</summary>
    public static int SharedRentLength(int minimumLength) =>
        minimumLength > MaxSharedArrayLength ? minimumLength : (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(minimumLength, MinimumArrayLength));

    public override T[] Rent(int minimumLength) =>
        TryRentRetained(minimumLength, out T[]? array) ? array : Shared.Rent(minimumLength);

    /// <summary>Takes a kept array of at least <paramref name="minimumLength"/> elements, without falling back to the shared pool.</summary>
    public bool TryRentRetained(int minimumLength, [NotNullWhen(true)] out T[]? array)
    {
        if (minimumLength > 0)
        {
            // Bucket i holds arrays of at least 2^i elements, so the first non-empty bucket from here fits.
            int first = BitOperations.Log2((uint)Math.Max(minimumLength, MinimumArrayLength) - 1) + 1;
            int end = Math.Min(first + BucketsSearched, BucketCount);
            for (int i = first; i < end; i++)
            {
                ArrayPoolList<T[]>? bucket = _buckets[i];
                if (bucket is { Count: > 0 })
                {
                    int last = bucket.Count - 1;
                    array = bucket[last];
                    bucket.Truncate(last);
                    RetainedLength -= array.Length;
                    return true;
                }
            }
        }

        array = null;
        return false;
    }

    public override void Return(T[] array, bool clearArray = false)
    {
        if (array.Length == 0) return;
        if (clearArray) Array.Clear(array);

        (_buckets[BitOperations.Log2((uint)array.Length)] ??= [with(4)]).Add(array);
        RetainedLength += array.Length;
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

        RetainedLength = 0;
    }
}
