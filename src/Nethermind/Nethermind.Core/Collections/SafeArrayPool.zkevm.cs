// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.Collections;

#pragma warning disable NETH003 // Build variant: only one of SafeArrayPool.std.cs / SafeArrayPool.zkevm.cs is compiled per build
/// <summary>
/// Shared array pool. Standard execution delegates to <see cref="ArrayPool{T}.Shared"/>;
/// the zkVM build provides a single-threaded power-of-two bucket pool instead.
/// </summary>
/// <remarks>
/// zkVM is single-threaded with no GC. Allocations are forever, so we keep pow2 buckets
/// with a small per-bucket cap so growing buffers turn into pool hits instead of fresh
/// allocations that pile up until execution ends. Replaces <see cref="ArrayPool{T}.Shared"/>
/// which uses thread-static state we cannot rely on under zkVM.
/// </remarks>
public static class SafeArrayPool<T>
{
    public static readonly ArrayPool<T> Shared = new SingleThreadedPow2Pool();

    private sealed class SingleThreadedPow2Pool : ArrayPool<T>
    {
        // Buckets cover 16 (2^4) up to 1 GiB elements (2^30); larger requests pass through unpooled.
        private const int MinBucketLog = 4;
        private const int MaxBucketLog = 30;
        private const int BucketCount = MaxBucketLog - MinBucketLog + 1;
        private const int MaxItemsPerBucket = 8;

        private readonly Stack<T[]>[] _buckets;

        public SingleThreadedPow2Pool()
        {
            _buckets = new Stack<T[]>[BucketCount];
            for (int i = 0; i < BucketCount; i++)
            {
                _buckets[i] = new Stack<T[]>(MaxItemsPerBucket);
            }
        }

        public override T[] Rent(int minimumLength)
        {
            if (minimumLength == 0) return Array.Empty<T>();
            ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);

            int bucketIndex = SelectBucket(minimumLength);
            if (bucketIndex < 0)
            {
                // Beyond the largest bucket — allocate exact, won't be pooled on return.
                return new T[minimumLength];
            }

            Stack<T[]> bucket = _buckets[bucketIndex];
            return bucket.Count > 0
                ? bucket.Pop()
                : new T[1 << (bucketIndex + MinBucketLog)];
        }

        public override void Return(T[] array, bool clearArray = false)
        {
            ArgumentNullException.ThrowIfNull(array);
            if (array.Length == 0) return;

            int bucketIndex = ExactBucket(array.Length);
            if (bucketIndex < 0) return; // Not a recognized bucket size — drop.

            Stack<T[]> bucket = _buckets[bucketIndex];
            if (bucket.Count >= MaxItemsPerBucket) return; // Bucket full — drop.

            // Always clear when the element type contains references so the pool does not
            // pin caller-visible data inside its retained buffers.
            if (clearArray || RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                Array.Clear(array);
            }

            bucket.Push(array);
        }

        private static int SelectBucket(int minimumLength)
        {
            if (minimumLength <= 1 << MinBucketLog) return 0;
            int log = BitOperations.Log2((uint)(minimumLength - 1)) + 1;
            return log <= MaxBucketLog ? log - MinBucketLog : -1;
        }

        private static int ExactBucket(int length)
        {
            if (!BitOperations.IsPow2(length)) return -1;
            int log = BitOperations.Log2((uint)length);
            if (log < MinBucketLog || log > MaxBucketLog) return -1;
            return log - MinBucketLog;
        }
    }
}
