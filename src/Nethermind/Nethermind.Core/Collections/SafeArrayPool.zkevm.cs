// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.Collections;

/// <summary>
/// Shared array pool. Standard execution delegates to <see cref="ArrayPool{T}.Shared"/>;
/// the zkVM build provides a single-threaded power-of-two bucket pool instead.
/// </summary>
/// <remarks>
/// zkVM is single-threaded with no GC. Allocations are forever, so every returned buffer
/// is retained for reuse (no per-bucket cap) and bucket containers are lazily created.
/// Buckets span 1 element up to 2^28 elements (256 Mi); larger requests pass through
/// unpooled. Replaces <see cref="ArrayPool{T}.Shared"/>, which relies on thread-static state.
/// </remarks>
public static class SafeArrayPool<T>
{
    private static readonly SingleThreadedPow2Pool Pool = new();

    /// <summary>Gets the shared zkVM array pool.</summary>
    /// <remarks>The concrete return type allows the zkVM AOT compiler to devirtualize pool operations.</remarks>
    public static SingleThreadedPow2Pool Shared => Pool;

    internal static T[] Rent(int minimumLength, out bool isFresh) => Pool.Rent(minimumLength, out isFresh);

    /// <summary>A single-threaded, power-of-two array pool for the zkVM guest.</summary>
    public sealed class SingleThreadedPow2Pool : ArrayPool<T>
    {
        private const int MinBucketLog = 0;   // 1 element
        private const int MaxBucketLog = 28;  // 256 Mi elements
        private const int BucketCount = MaxBucketLog - MinBucketLog + 1;

        private readonly Stack<T[]>?[] _buckets = new Stack<T[]>?[BucketCount];

        public override T[] Rent(int minimumLength) => Rent(minimumLength, out _);

        /// <summary>Rents an array and reports whether its elements are known to be default-initialized.</summary>
        /// <param name="minimumLength">The minimum length of the requested array.</param>
        /// <param name="isFresh">
        /// <see langword="true"/> when the array cannot contain data from a previous renter;
        /// otherwise, <see langword="false"/>.
        /// </param>
        /// <returns>An array whose length is at least <paramref name="minimumLength"/>.</returns>
        /// <remarks>
        /// A non-fresh array has arbitrary contents that callers must clear before exposing unwritten elements.
        /// A zero-length request returns <see cref="Array.Empty{T}"/> with <paramref name="isFresh"/> set to
        /// <see langword="true"/> because the singleton exposes no elements.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="minimumLength"/> is negative.
        /// </exception>
        public T[] Rent(int minimumLength, out bool isFresh)
        {
            if (minimumLength == 0)
            {
                isFresh = true;
                return Array.Empty<T>();
            }

            ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);

            int bucketIndex = BucketFor(minimumLength);
            if (bucketIndex < 0)
            {
                isFresh = true;
                return new T[minimumLength];
            }

            Stack<T[]>? bucket = _buckets[bucketIndex];
            if (bucket is { Count: > 0 })
            {
                isFresh = false;
                return bucket.Pop();
            }

            isFresh = true;
            return new T[1 << (bucketIndex + MinBucketLog)];
        }

        public override void Return(T[] array, bool clearArray = false)
        {
            ArgumentNullException.ThrowIfNull(array);
            if (!BitOperations.IsPow2(array.Length)) return;

            int bucketIndex = BitOperations.Log2((uint)array.Length) - MinBucketLog;
            if (bucketIndex >= BucketCount) return;

            if (clearArray || RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                Array.Clear(array);
            }

            (_buckets[bucketIndex] ??= new Stack<T[]>()).Push(array);
        }

        private static int BucketFor(int minimumLength)
        {
            if (minimumLength <= 1 << MinBucketLog) return 0;
            int log = BitOperations.Log2((uint)(minimumLength - 1)) + 1;
            return log <= MaxBucketLog ? log - MinBucketLog : -1;
        }
    }
}
