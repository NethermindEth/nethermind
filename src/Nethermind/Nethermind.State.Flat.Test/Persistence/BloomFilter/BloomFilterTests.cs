// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Bloom = Nethermind.State.Flat.Persistence.BloomFilter.BloomFilter;

namespace Nethermind.State.Flat.Test.Persistence.BloomFilter;

[TestFixture]
public class BloomFilterTests
{
    private static Bloom NewBloom(long capacity = 100, double bitsPerKey = 10, long initialCount = 0) =>
        new(capacity, bitsPerKey, initialCount);

    [TestCase(new ulong[] { 12345 })]
    [TestCase(new ulong[] { 1, 2, 3, 100, 1000, 99999 })]
    public void Add_AddedItems_AreFound(ulong[] hashes)
    {
        using Bloom bloom = NewBloom();

        foreach (ulong hash in hashes) bloom.Add(hash);

        foreach (ulong hash in hashes)
        {
            Assert.That(bloom.MightContain(hash), Is.True, $"hash {hash} should be found");
        }
    }

    [Test]
    public void Add_Concurrent_ShouldBeThreadSafe()
    {
        using Bloom bloom = NewBloom(capacity: 1000);
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
        using Bloom bloom = NewBloom(capacity: 10000);
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

        Assert.Pass("Concurrent operations completed without exceptions");
    }

    [Test]
    public void Dispose_MultipleTimes_ShouldNotThrow()
    {
        Bloom bloom = NewBloom();

        bloom.Dispose();
        Assert.DoesNotThrow(() => bloom.Dispose());
    }

    [Test]
    public void MightContain_BeforeAnyAdds_ShouldReturnFalse()
    {
        using Bloom bloom = NewBloom();
        Assert.That(bloom.MightContain(99999), Is.False, "empty bloom filter should return false for items not added");
    }

    [Test]
    public void Add_LargeNumberOfItems_ShouldWork()
    {
        const int totalItems = 500;
        using Bloom bloom = NewBloom(capacity: totalItems);

        for (ulong i = 0; i < totalItems; i++) bloom.Add(i);

        Assert.That(bloom.Count, Is.EqualTo(totalItems));
        for (ulong i = 0; i < 50; i++)
        {
            Assert.That(bloom.MightContain(i), Is.True, $"hash {i} should be found");
        }
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(long.MinValue)]
    public void Constructor_RejectsNonPositiveCapacity(long capacity) =>
        Assert.That(() => NewBloom(capacity: capacity), Throws.TypeOf<ArgumentOutOfRangeException>());

    [TestCase(0.0)]
    [TestCase(-1.0)]
    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    [TestCase(double.NegativeInfinity)]
    public void Constructor_RejectsInvalidBitsPerKey(double bitsPerKey) =>
        Assert.That(() => NewBloom(bitsPerKey: bitsPerKey), Throws.TypeOf<ArgumentOutOfRangeException>());

    [Test]
    public void Constructor_AcceptsInitialCount()
    {
        using Bloom bloom = NewBloom(initialCount: 42);
        Assert.That(bloom.Count, Is.EqualTo(42));
    }

    [TestCase(50, 10000)]
    [TestCase(500, 100)]
    public void Clear_RemovesAddedItems_AndFilterIsReusable(int itemCount, long capacity)
    {
        using Bloom bloom = NewBloom(capacity: capacity, bitsPerKey: 14);

        for (ulong i = 0; i < (ulong)itemCount; i++) bloom.Add(i);
        bloom.Clear();

        Assert.That(bloom.Count, Is.EqualTo(0));
        for (ulong i = 0; i < (ulong)itemCount; i++)
        {
            Assert.That(bloom.MightContain(i), Is.False, $"hash {i} should be gone after Clear");
        }

        bloom.Add(999_999);
        Assert.That(bloom.MightContain(999_999), Is.True);
    }

    [Test]
    public void Clear_RepeatedCycles_DoNotLeakBits()
    {
        using Bloom bloom = NewBloom(capacity: 10000, bitsPerKey: 14);
        for (int cycle = 0; cycle < 3; cycle++)
        {
            ulong offset = (ulong)cycle * 1000;
            for (ulong i = 0; i < 50; i++) bloom.Add(offset + i);
            bloom.Clear();
            for (ulong i = 0; i < 50; i++)
            {
                Assert.That(bloom.MightContain(offset + i), Is.False, $"cycle {cycle}: hash {offset + i} survived Clear");
            }
        }
    }

    [Test]
    public void Clear_OnEmptyFilter_DoesNotThrow()
    {
        using Bloom bloom = NewBloom();
        Assert.DoesNotThrow(bloom.Clear);
        Assert.That(bloom.Count, Is.EqualTo(0));
    }
}
