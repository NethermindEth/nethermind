// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nethermind.Core.Memory;

/// <summary>
/// An <see cref="IRefCountingMemoryProvider"/> over native memory: a request is rounded up to the
/// smallest bucket holding it, and the last release returns the block to that bucket for reuse.
/// </summary>
/// <remarks>
/// Each bucket keeps at most <c>maxRetainedBytesPerBucket</c> of released blocks and frees the rest.
/// A request above the largest bucket gets an exact-size block that is freed on its last release.
/// Rents and releases are safe from any thread. <see cref="Dispose"/> frees the retained blocks and
/// stops new rents; memory still leased is freed as it is released.
/// Uses <see cref="NativeMemory.Alloc(nuint)"/> only, never an aligned allocation, so it also runs on
/// the zkVM guest.
/// </remarks>
public sealed unsafe class NativeBucketedMemoryPool : IRefCountingMemoryProvider, IDisposable
{
    private readonly int[] _bucketSizes;
    private readonly Bucket[] _buckets;
    private volatile bool _disposed;

    /// <param name="bucketSizes">Strictly ascending positive block sizes.</param>
    /// <param name="maxRetainedBytesPerBucket">Upper bound on released bytes a bucket keeps for reuse; it always keeps at least one block.</param>
    public NativeBucketedMemoryPool(int[] bucketSizes, int maxRetainedBytesPerBucket)
    {
        ArgumentNullException.ThrowIfNull(bucketSizes);
        ArgumentOutOfRangeException.ThrowIfZero(bucketSizes.Length);
        int previous = 0;
        foreach (int bucketSize in bucketSizes)
        {
            if (bucketSize <= previous) throw new ArgumentOutOfRangeException(nameof(bucketSizes), bucketSize, "sizes must ascend");
            previous = bucketSize;
        }

        _bucketSizes = (int[])bucketSizes.Clone();
        _buckets = new Bucket[bucketSizes.Length];
        for (int i = 0; i < bucketSizes.Length; i++) _buckets[i] = new Bucket(Math.Max(1, maxRetainedBytesPerBucket / bucketSizes[i]));
    }

    public RefCountingMemory Rent(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ObjectDisposedException.ThrowIf(_disposed, this);
        int bucketIndex = BucketIndex(length);
        bool isBucketed = bucketIndex < _buckets.Length;
        int capacity = isBucketed ? _bucketSizes[bucketIndex] : length;
        byte* block = isBucketed ? _buckets[bucketIndex].TryPop() : null;
        if (block is null) block = (byte*)NativeMemory.Alloc((nuint)capacity);
        return RefCountingMemory.OwningNative(this, block, capacity, length);
    }

    /// <summary>Takes back a block of <paramref name="capacity"/> bytes rented from this pool; safe after <see cref="Dispose"/>.</summary>
    internal void Return(byte* block, int capacity)
    {
        int bucketIndex = BucketIndex(capacity);
        if (bucketIndex == _buckets.Length || !_buckets[bucketIndex].TryPush(block)) NativeMemory.Free(block);
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (Bucket bucket in _buckets) bucket.Close();
    }

    /// <summary>
    /// Generates jemalloc-style bucket sizes: <paramref name="quantum"/> apart while a doubling is too
    /// small to hold <paramref name="bucketsPerDoubling"/> of them, then that many evenly spaced sizes
    /// per doubling, up to <paramref name="maxSize"/> inclusive.
    /// </summary>
    /// <remarks>
    /// With quantum 64 and four buckets per doubling the spacing above 1024 is 256, so any request
    /// wastes under a quarter of its bucket and typically far less (1200 bytes lands in 1280).
    /// </remarks>
    public static int[] GenerateBucketSizes(int quantum, int bucketsPerDoubling, int maxSize)
    {
        if (!BitOperations.IsPow2(quantum)) throw new ArgumentOutOfRangeException(nameof(quantum), quantum, "must be a power of two");
        if (!BitOperations.IsPow2(bucketsPerDoubling)) throw new ArgumentOutOfRangeException(nameof(bucketsPerDoubling), bucketsPerDoubling, "must be a power of two");
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSize, quantum);

        List<int> sizes = [];
        for (long groupBase = quantum; groupBase <= maxSize; groupBase *= 2)
        {
            long spacing = Math.Max(quantum, groupBase / bucketsPerDoubling);
            for (long size = groupBase; size < groupBase * 2 && size <= maxSize; size += spacing)
                sizes.Add((int)size);
        }

        return sizes.ToArray();
    }

    /// <returns>The smallest bucket holding <paramref name="size"/> bytes, or the bucket count when none does.</returns>
    private int BucketIndex(int size)
    {
        int index = Array.BinarySearch(_bucketSizes, size);
        return index >= 0 ? index : ~index;
    }

    /// <summary>A bounded LIFO of released blocks of one size.</summary>
    private sealed class Bucket(int capacity)
    {
        private readonly Lock _lock = new();
        private readonly nint[] _blocks = new nint[capacity];
        private int _count;
        private bool _closed;

        public byte* TryPop()
        {
            lock (_lock) return _count == 0 ? null : (byte*)_blocks[--_count];
        }

        public bool TryPush(byte* block)
        {
            lock (_lock)
            {
                if (_closed || _count == _blocks.Length) return false;
                _blocks[_count++] = (nint)block;
                return true;
            }
        }

        /// <summary>Frees the retained blocks and refuses any further ones.</summary>
        public void Close()
        {
            lock (_lock)
            {
                _closed = true;
                while (_count > 0) NativeMemory.Free((byte*)_blocks[--_count]);
            }
        }
    }
}
