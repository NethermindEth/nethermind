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
    public static readonly ArrayPool<T> Shared = new SingleThreadedPow2Pool();

    private sealed class SingleThreadedPow2Pool : ArrayPool<T>
    {
        private const int MinBucketLog = 0;   // 1 element
        private const int MaxBucketLog = 28;  // 256 Mi elements
        private const int BucketCount = MaxBucketLog - MinBucketLog + 1;

        private readonly Stack<T[]>?[] _buckets = new Stack<T[]>?[BucketCount];

        public override T[] Rent(int minimumLength)
        {
            if (minimumLength == 0) return Array.Empty<T>();
            ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);

            int bucketIndex = BucketFor(minimumLength);
            if (bucketIndex < 0) return new T[minimumLength];

            Stack<T[]>? bucket = _buckets[bucketIndex];
            return bucket is { Count: > 0 }
                ? bucket.Pop()
                : new T[1 << (bucketIndex + MinBucketLog)];
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
