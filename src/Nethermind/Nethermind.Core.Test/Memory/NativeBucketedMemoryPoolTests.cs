// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Memory;
using NUnit.Framework;

namespace Nethermind.Core.Test.Memory;

[Parallelizable(ParallelScope.All)]
public unsafe class NativeBucketedMemoryPoolTests
{
    private const int MaxBucket = 4096;

    private static NativeBucketedMemoryPool CreatePool() => new(NativeBucketedMemoryPool.GenerateBucketSizes(16, 4, MaxBucket), 64 * 1024);

    [TestCase(64, 4, 2048, new[] { 64, 128, 192, 256, 320, 384, 448, 512, 640, 768, 896, 1024, 1280, 1536, 1792, 2048 })]
    [TestCase(16, 4, 128, new[] { 16, 32, 48, 64, 80, 96, 112, 128 })]
    [TestCase(16, 2, 200, new[] { 16, 32, 48, 64, 96, 128, 192 })]
    public void GenerateBucketSizes_spaces_buckets_like_jemalloc(int quantum, int bucketsPerDoubling, int maxSize, int[] expected) =>
        Assert.That(NativeBucketedMemoryPool.GenerateBucketSizes(quantum, bucketsPerDoubling, maxSize), Is.EqualTo(expected));

    [TestCase(0, 16)]
    [TestCase(100, 112)]
    [TestCase(1200, 1280)]
    [TestCase(MaxBucket, MaxBucket)]
    [TestCase(MaxBucket + 1, MaxBucket + 1)]
    public void Rent_rounds_up_to_the_bucket_and_reuses_released_blocks(int length, int expectedCapacity)
    {
        using NativeBucketedMemoryPool pool = CreatePool();
        RefCountingMemory first = pool.Rent(length);
        nint firstBlock = Address(first);
        ((IDisposable)first).Dispose();

        using RefCountingMemory second = pool.Rent(length);
        using RefCountingMemory third = pool.Rent(length);
        second.GetSpan().Fill(0xA1);
        third.GetSpan().Fill(0xB2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.GetSpan().Length, Is.EqualTo(length));
            Assert.That(second.Capacity, Is.EqualTo(expectedCapacity));
            if (length <= MaxBucket) Assert.That(Address(second), Is.EqualTo(firstBlock), "the released block is reused");
            Assert.That(second.GetSpan().IndexOfAnyExcept((byte)0xA1), Is.EqualTo(-1), "blocks are distinct");
        }
    }

    [Test]
    public void Release_after_dispose_frees_the_block_and_rent_throws()
    {
        NativeBucketedMemoryPool pool = CreatePool();
        RefCountingMemory memory = pool.Rent(100);
        pool.Dispose();
        ((IDisposable)memory).Dispose();

        Assert.Throws<ObjectDisposedException>(() => pool.Rent(100));
    }

    [Test]
    public void Constructor_rejects_sizes_that_do_not_ascend()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new NativeBucketedMemoryPool([32, 16], 1024), "not ascending");
            Assert.Throws<ArgumentOutOfRangeException>(() => new NativeBucketedMemoryPool([0, 16], 1024), "not positive");
            Assert.Throws<ArgumentOutOfRangeException>(() => new NativeBucketedMemoryPool([], 1024), "no buckets");
        }
    }

    private static nint Address(RefCountingMemory memory) => (nint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(memory.GetSpan()));
}
