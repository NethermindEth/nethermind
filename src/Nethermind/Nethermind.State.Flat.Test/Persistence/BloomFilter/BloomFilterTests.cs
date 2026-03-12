// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.State.Flat.Persistence.BloomFilter;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Persistence.BloomFilter;

[TestFixture]
public class BloomFilterTests
{
    #region Basic Add/Query Tests

    [Test]
    public void Add_SingleItem_ShouldBeFound()
    {
        // Arrange
        using Nethermind.State.Flat.Persistence.BloomFilter.BloomFilter bloom = new(capacity: 100, bitsPerKey: 10);
        ulong hash = 12345;

        // Act
        bloom.Add(hash);

        // Assert
        bloom.MightContain(hash).Should().BeTrue();
    }

    [Test]
    public void Add_MultipleItems_ShouldAllBeFound()
    {
        // Arrange
        using Nethermind.State.Flat.Persistence.BloomFilter.BloomFilter bloom = new(capacity: 100, bitsPerKey: 10);
        ulong[] hashes = { 1, 2, 3, 100, 1000, 99999 };

        // Act
        foreach (ulong hash in hashes)
        {
            bloom.Add(hash);
        }

        // Assert
        foreach (ulong hash in hashes)
        {
            bloom.MightContain(hash).Should().BeTrue($"hash {hash} should be found");
        }
    }

    #endregion

    #region Concurrency Tests

    [Test]
    public void Add_Concurrent_ShouldBeThreadSafe()
    {
        // Arrange
        using Nethermind.State.Flat.Persistence.BloomFilter.BloomFilter bloom = new(capacity: 1000, bitsPerKey: 10);
        int threadsCount = 10;
        int itemsPerThread = 50;
        using Barrier barrier = new(threadsCount);
        System.Collections.Concurrent.ConcurrentBag<ulong> addedHashes = new();

        // Act - Multiple threads adding concurrently
        Task[] tasks = Enumerable.Range(0, threadsCount).Select(threadId => Task.Run(() =>
        {
            barrier.SignalAndWait(); // Sync start
            for (int i = 0; i < itemsPerThread; i++)
            {
                ulong hash = (ulong)(threadId * itemsPerThread + i);
                bloom.Add(hash);
                addedHashes.Add(hash);
            }
        })).ToArray();

        Task.WaitAll(tasks);

        // Assert - All items should be found
        foreach (ulong hash in addedHashes)
        {
            bloom.MightContain(hash).Should().BeTrue($"hash {hash} should be found");
        }
    }

    [Test]
    public void Add_ConcurrentWithMightContain_ShouldWork()
    {
        // Arrange
        using Nethermind.State.Flat.Persistence.BloomFilter.BloomFilter bloom = new(capacity: 10000, bitsPerKey: 10);
        int duration = 1000; // ms
        CancellationTokenSource cts = new(duration);

        // Act - Some threads adding, others querying
        Task[] writerTasks = Enumerable.Range(0, 3).Select(threadId => Task.Run(() =>
        {
            ulong hash = (ulong)(threadId * 1000000);
            while (!cts.Token.IsCancellationRequested)
            {
                bloom.Add(hash++);
            }
        })).ToArray();

        Task[] readerTasks = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            ulong hash = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                bloom.MightContain(hash++);
                Thread.Yield();
            }
        })).ToArray();

        Task.WaitAll(writerTasks.Concat(readerTasks).ToArray());

        // Assert - No exceptions thrown
        Assert.Pass("Concurrent operations completed without exceptions");
    }

    #endregion

    #region Edge Cases

    [Test]
    public void Dispose_MultipleTimes_ShouldNotThrow()
    {
        // Arrange
        Nethermind.State.Flat.Persistence.BloomFilter.BloomFilter bloom = new(capacity: 100, bitsPerKey: 10);

        // Act & Assert
        bloom.Dispose();
        Assert.DoesNotThrow(() => bloom.Dispose());
    }

    [Test]
    public void MightContain_BeforeAnyAdds_ShouldReturnFalse()
    {
        // Arrange
        using Nethermind.State.Flat.Persistence.BloomFilter.BloomFilter bloom = new(capacity: 100, bitsPerKey: 10);

        // Act & Assert
        // Empty bloom filter should generally return false (though false positives are theoretically possible)
        bool result = bloom.MightContain(99999);
        result.Should().BeFalse("empty bloom filter should return false for items not added");
    }

    [Test]
    public void Add_LargeNumberOfItems_ShouldWork()
    {
        // Arrange
        int totalItems = 500;
        using Nethermind.State.Flat.Persistence.BloomFilter.BloomFilter bloom = new(capacity: totalItems, bitsPerKey: 10);

        // Act
        for (ulong i = 0; i < (ulong)totalItems; i++)
        {
            bloom.Add(i);
        }

        // Assert - Verify count
        bloom.Count.Should().Be(totalItems);

        // Verify sample of items can be found
        for (ulong i = 0; i < 50; i++)
        {
            bloom.MightContain(i).Should().BeTrue($"hash {i} should be found");
        }
    }

    #endregion
}
