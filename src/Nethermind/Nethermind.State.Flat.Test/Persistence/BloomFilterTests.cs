// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Nethermind.Core.Collections;

namespace Nethermind.State.Flat.Test.Persistence;

[TestFixture]
public class BloomFilterTests
{
    private static BloomFilter NewBloom(long capacity = 100, double bitsPerKey = 10, long initialCount = 0) =>
        new(capacity, bitsPerKey, initialCount);

    [TestCase(new ulong[] { 12345 })]
    [TestCase(new ulong[] { 1, 2, 3, 100, 1000, 99999 })]
    public void Add_AddedItems_AreFound(ulong[] hashes)
    {
        using BloomFilter bloom = NewBloom();

        foreach (ulong hash in hashes) bloom.Add(hash);

        foreach (ulong hash in hashes)
        {
            Assert.That(bloom.MightContain(hash), Is.True, $"hash {hash} should be found");
        }
    }

    [Test]
    public void Add_Concurrent_ShouldBeThreadSafe()
    {
        using BloomFilter bloom = NewBloom(capacity: 1000);
        int threadsCount = 10;
        int itemsPerThread = 50;
        using Barrier barrier = new(threadsCount);
        ConcurrentBag<ulong> addedHashes = [];

        Task[] tasks = Enumerable.Range(0, threadsCount).Select(threadId => Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (int i = 0; i < itemsPerThread; i++)
            {
                ulong hash = (ulong)(threadId * itemsPerThread + i);
                bloom.Add(hash);
                addedHashes.Add(hash);
            }
        })).ToArray();

        Task.WaitAll(tasks);

        foreach (ulong hash in addedHashes)
        {
            Assert.That(bloom.MightContain(hash), Is.True, $"hash {hash} should be found");
        }
    }

    [Test]
    public void Add_ConcurrentWithMightContain_ShouldWork()
    {
        // The capacity is far above the 30k added keys. A saturated filter's
        // false-positive rate masks a single lost write and defeats the check below.
        using BloomFilter bloom = NewBloom(capacity: 100_000);
        const int iterationsPerThread = 10000;

        Task[] writerTasks = Enumerable.Range(0, 3).Select(threadId => Task.Run(() =>
        {
            ulong hash = (ulong)(threadId * 1000000);
            for (int i = 0; i < iterationsPerThread; i++)
            {
                bloom.Add(hash++);
            }
        })).ToArray();

        Task[] readerTasks = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            ulong hash = 0;
            for (int i = 0; i < iterationsPerThread; i++)
            {
                bloom.MightContain(hash++);
            }
        })).ToArray();

        Task.WaitAll(writerTasks.Concat(readerTasks).ToArray());

        // A bloom filter never returns a false negative. Every added hash must be found
        // afterwards. A miss means a concurrent-access race lost a write.
        List<ulong> lost = [];
        for (int threadId = 0; threadId < 3; threadId++)
        {
            ulong hash = (ulong)(threadId * 1000000);
            for (int i = 0; i < iterationsPerThread; i++)
            {
                if (!bloom.MightContain(hash)) lost.Add(hash);
                hash++;
            }
        }

        Assert.That(lost, Is.Empty, "hashes lost during concurrent add");
    }

    [Test]
    public void AddUnsynchronized_SetsTheSameBitsAsAdd([Values(1.0, 10.0, 14.0, 30.0)] double bitsPerKey)
    {
        const int keys = 2_000;
        using BloomFilter atomic = NewBloom(capacity: keys, bitsPerKey: bitsPerKey);
        using BloomFilter plain = NewBloom(capacity: keys, bitsPerKey: bitsPerKey);

        for (ulong i = 0; i < keys; i++)
        {
            atomic.Add(i * 0x9E3779B97F4A7C15UL);
            plain.AddUnsynchronized(i * 0x9E3779B97F4A7C15UL);
        }

        Assert.That(plain.Count, Is.EqualTo(keys));
        // Same bits set means the same answer for every probe, members and non-members alike.
        for (ulong probe = 0; probe < 50_000; probe++)
        {
            ulong key = probe * 0xC2B2AE3D27D4EB4FUL;
            Assert.That(plain.MightContain(key), Is.EqualTo(atomic.MightContain(key)), $"probe {probe}");
        }
    }

    [Test]
    public void Operations_AfterDispose_Throw()
    {
        BloomFilter bloom = NewBloom();
        bloom.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => bloom.Add(1), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => bloom.AddUnsynchronized(1), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => bloom.MightContain(1), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => bloom.Clear(), Throws.TypeOf<ObjectDisposedException>());
        }
    }

    [Test]
    public void Dispose_MultipleTimes_ShouldNotThrow()
    {
        BloomFilter bloom = NewBloom();

        bloom.Dispose();
        Assert.DoesNotThrow(() => bloom.Dispose());
    }

    [Test]
    public void AlwaysTrue_MightContain_AnyKey_ReturnsTrue([Values(0UL, 1UL, 0xDEADBEEFCAFEBABEUL, ulong.MaxValue)] ulong key)
    {
        using Nethermind.Core.Collections.BloomFilter bloom =
            Nethermind.Core.Collections.BloomFilter.AlwaysTrue();

        Assert.That(bloom.MightContain(key), Is.True, "AlwaysTrue sentinel must match every probe");
    }

    [Test]
    public void MightContain_BeforeAnyAdds_ShouldReturnFalse()
    {
        using BloomFilter bloom = NewBloom();
        Assert.That(bloom.MightContain(99999), Is.False, "empty bloom filter should return false for items not added");
    }

    [Test]
    public void Add_LargeNumberOfItems_ShouldWork()
    {
        const int totalItems = 500;
        using BloomFilter bloom = NewBloom(capacity: totalItems);

        for (ulong i = 0; i < totalItems; i++) bloom.Add(i);

        Assert.That(bloom.Count, Is.EqualTo(totalItems));
        for (ulong i = 0; i < 50; i++)
        {
            Assert.That(bloom.MightContain(i), Is.True, $"hash {i} should be found");
        }
    }

    [Test]
    public void Constructor_RejectsNonPositiveCapacity([Values(0, -1, long.MinValue)] long capacity) =>
        Assert.That(() => NewBloom(capacity: capacity), Throws.TypeOf<ArgumentOutOfRangeException>());

    [Test]
    public void Constructor_RejectsInvalidBitsPerKey([Values(0.0, -1.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity)] double bitsPerKey) =>
        Assert.That(() => NewBloom(bitsPerKey: bitsPerKey), Throws.TypeOf<ArgumentOutOfRangeException>());

    [Test]
    public void Constructor_AcceptsInitialCount()
    {
        using BloomFilter bloom = NewBloom(initialCount: 42);
        Assert.That(bloom.Count, Is.EqualTo(42));
    }
}
