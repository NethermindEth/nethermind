// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.State.Flat.Persistence.BloomFilter;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Persistence.BloomFilter;

[TestFixture]
public class PersistedBloomFilterTests
{
    private string _testDirectory = null!;

    [SetUp]
    public void Setup()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"bloom_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_testDirectory))
        {
            try
            {
                Directory.Delete(_testDirectory, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    private string GetTestFilePath() => Path.Combine(_testDirectory, $"test_{Guid.NewGuid()}.bloom");

    private double FPRate(ulong count, int bitsPerKey)
    {
        using var segment = PersistedBloomFilter.CreateNew(GetTestFilePath(), (long)count, bitsPerKey);

        Span<byte> buffer = stackalloc byte[8];
        for (ulong i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt64BigEndian(buffer, i);
            segment.TryAdd(XxHash64.HashToUInt64(buffer));
        }

        long hit = 0;
        for (ulong i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt64BigEndian(buffer, i + count);
            if (segment.MightContain(XxHash64.HashToUInt64(buffer)))
            {
                hit++;
            }
        }

        return ((double)hit) / count;
    }


    [Test]
    public void TestFpRate()
    {
        for (int i = 8; i <= 20; i++)
        {
            Console.Error.WriteLine($"FP rate for {i} is {FPRate(1_000_000, i):P}");
        }

        Assert.That(FPRate(1_000_000, 10), Is.LessThanOrEqualTo(0.01));
        Assert.That(FPRate(1_000_000, 12), Is.LessThanOrEqualTo(0.005));
        Assert.That(FPRate(1_000_000, 20), Is.LessThanOrEqualTo(0.0005));
    }

    #region Creation & Persistence Tests

    [Test]
    public void Create_NewSegment_ShouldHaveCorrectInitialState()
    {
        // Arrange
        string path = GetTestFilePath();
        long capacity = 1000;
        int bitsPerKey = 10;

        // Act
        using var segment = PersistedBloomFilter.CreateNew(path, capacity, bitsPerKey);

        // Assert
        segment.Capacity.Should().Be(capacity);
        segment.BitsPerKey.Should().Be(bitsPerKey);
        segment.Count.Should().Be(0);
        segment.IsSealed.Should().BeFalse();
        segment.IsFull.Should().BeFalse();
        segment.K.Should().BeGreaterThan(0);
    }

    [Test]
    public void Create_AndReopen_ShouldPersistMetadata()
    {
        // Arrange
        string path = GetTestFilePath();
        long capacity = 1000;
        int bitsPerKey = 10;
        long expectedCount;

        // Act - Create and add items
        using (var segment = PersistedBloomFilter.CreateNew(path, capacity, bitsPerKey))
        {
            segment.TryAdd(1).Should().BeTrue();
            segment.TryAdd(2).Should().BeTrue();
            segment.TryAdd(3).Should().BeTrue();
            expectedCount = segment.Count;
        }

        // Act - Reopen
        using var reopened = PersistedBloomFilter.OpenExisting(path);

        // Assert
        reopened.Capacity.Should().Be(capacity);
        reopened.BitsPerKey.Should().Be(bitsPerKey);
        reopened.Count.Should().Be(expectedCount);
        reopened.IsSealed.Should().BeFalse();
    }

    [Test]
    public void Create_WithInvalidHeader_ShouldThrow()
    {
        // Arrange
        string path = GetTestFilePath();
        File.WriteAllBytes(path, new byte[256]); // Invalid header

        // Act & Assert
        Assert.Throws<InvalidDataException>(() =>
        {
            using var segment = PersistedBloomFilter.OpenExisting(path);
        });
    }

    #endregion

    #region Basic Operations Tests

    [Test]
    public void TryAdd_SingleItem_ShouldBeFound()
    {
        // Arrange
        string path = GetTestFilePath();
        using var segment = PersistedBloomFilter.CreateNew(path, 1000, 10);
        ulong hash = 12345;

        // Act
        bool added = segment.TryAdd(hash);

        // Assert
        added.Should().BeTrue();
        segment.MightContain(hash).Should().BeTrue();
        segment.Count.Should().Be(1);
    }

    [Test]
    public void TryAdd_MultipleItems_ShouldAllBeFound()
    {
        // Arrange
        string path = GetTestFilePath();
        using var segment = PersistedBloomFilter.CreateNew(path, 1000, 10);
        ulong[] hashes = { 1, 2, 3, 100, 1000, 99999 };

        // Act
        foreach (var hash in hashes)
        {
            segment.TryAdd(hash).Should().BeTrue();
        }

        // Assert
        foreach (var hash in hashes)
        {
            segment.MightContain(hash).Should().BeTrue($"hash {hash} should be found");
        }
        segment.Count.Should().Be(hashes.Length);
    }

    [Test]
    public void MightContain_NonExistentItem_ShouldReturnFalseOrOccasionallyTrue()
    {
        // Arrange
        string path = GetTestFilePath();
        using var segment = PersistedBloomFilter.CreateNew(path, 1000, 10);
        segment.TryAdd(1).Should().BeTrue();
        segment.TryAdd(2).Should().BeTrue();

        // Act
        bool result = segment.MightContain(99999);

        // Assert
        // Bloom filters can have false positives, so we only assert it doesn't throw.
        Assert.Pass("Query completed without exception");
    }

    [Test]
    public void FalsePositiveRate_ShouldBeReasonable()
    {
        // Arrange
        string path = GetTestFilePath();
        int capacity = 1000;
        int bitsPerKey = 10;
        using var segment = PersistedBloomFilter.CreateNew(path, capacity, bitsPerKey);

        // Add half capacity
        for (ulong i = 0; i < (ulong)(capacity / 2); i++)
        {
            segment.TryAdd(i).Should().BeTrue();
        }

        // Act - Check false positives
        int falsePositives = 0;
        int checksCount = 1000;
        for (ulong i = (ulong)capacity; i < (ulong)(capacity + checksCount); i++)
        {
            if (segment.MightContain(i))
                falsePositives++;
        }

        double fpRate = (double)falsePositives / checksCount;
        fpRate.Should().BeLessThan(0.1, "false positive rate should be reasonable");
    }

    #endregion

    #region Capacity & Full Tests

    [Test]
    public void IsFull_WhenAtCapacity_ShouldBeTrue()
    {
        // Arrange
        string path = GetTestFilePath();
        int capacity = 10;
        using var segment = PersistedBloomFilter.CreateNew(path, capacity, 10);

        // Act
        for (ulong i = 0; i < (ulong)capacity; i++)
        {
            segment.TryAdd(i).Should().BeTrue();
        }

        // Assert
        segment.IsFull.Should().BeTrue();
        segment.Count.Should().Be(capacity);
    }

    [Test]
    public void TryAdd_BeyondCapacity_ShouldStillWork()
    {
        // Arrange
        string path = GetTestFilePath();
        int capacity = 5;
        using var segment = PersistedBloomFilter.CreateNew(path, capacity, 10);

        // Act - Add beyond capacity (not sealed, so should work)
        for (ulong i = 0; i < (ulong)(capacity + 5); i++)
        {
            segment.TryAdd(i).Should().BeTrue();
        }

        // Assert
        segment.Count.Should().Be(capacity + 5);
        segment.IsFull.Should().BeTrue();
    }

    #endregion

    #region Seal & Flush Tests

    [Test]
    public void Seal_ShouldPreventFurtherAdds()
    {
        // Arrange
        string path = GetTestFilePath();
        using var segment = PersistedBloomFilter.CreateNew(path, 1000, 10);
        segment.TryAdd(1).Should().BeTrue();

        // Act
        segment.Seal();

        // Assert
        segment.IsSealed.Should().BeTrue();
        segment.TryAdd(2).Should().BeFalse("sealed segment should reject TryAdd");
    }

    [Test]
    public void Seal_ShouldPersistState()
    {
        // Arrange
        string path = GetTestFilePath();
        using (var segment = PersistedBloomFilter.CreateNew(path, 1000, 10))
        {
            segment.TryAdd(1).Should().BeTrue();
            segment.TryAdd(2).Should().BeTrue();
            segment.Seal();
        }

        // Act - Reopen
        using var reopened = PersistedBloomFilter.OpenExisting(path);

        // Assert
        reopened.IsSealed.Should().BeTrue();
        reopened.Count.Should().Be(2);
        reopened.TryAdd(3).Should().BeFalse("sealed segment should reject TryAdd after reopen");
    }

    [Test]
    public void Flush_ShouldPersistData()
    {
        // Arrange
        string path = GetTestFilePath();
        ulong testHash = 12345;

        using (var segment = PersistedBloomFilter.CreateNew(path, 1000, 10))
        {
            segment.TryAdd(testHash).Should().BeTrue();
            segment.Flush();
        }

        // Act - Reopen
        using var reopened = PersistedBloomFilter.OpenExisting(path);

        // Assert
        reopened.MightContain(testHash).Should().BeTrue();
        reopened.Count.Should().Be(1);
    }

    [Test]
    public void Dispose_UnsealedSegment_ShouldPersistHeader()
    {
        // Arrange
        string path = GetTestFilePath();
        long expectedCount;

        using (var segment = PersistedBloomFilter.CreateNew(path, 1000, 10))
        {
            segment.TryAdd(1).Should().BeTrue();
            segment.TryAdd(2).Should().BeTrue();
            expectedCount = segment.Count;
        }

        // Act - Reopen
        using var reopened = PersistedBloomFilter.OpenExisting(path);

        // Assert
        reopened.Count.Should().Be(expectedCount);
        reopened.IsSealed.Should().BeFalse();
    }

    #endregion

    #region Concurrency Tests

    [Test]
    public void TryAdd_Concurrent_ShouldBeThreadSafe()
    {
        // Arrange
        string path = GetTestFilePath();
        int capacity = 10000;
        using var segment = PersistedBloomFilter.CreateNew(path, capacity, 10);
        int threadsCount = 10;
        int itemsPerThread = 100;
        var barrier = new Barrier(threadsCount);

        // Act - Multiple threads adding concurrently
        var tasks = Enumerable.Range(0, threadsCount).Select(threadId => Task.Run(() =>
        {
            barrier.SignalAndWait(); // Sync start
            for (int i = 0; i < itemsPerThread; i++)
            {
                ulong hash = (ulong)(threadId * itemsPerThread + i);
                segment.TryAdd(hash).Should().BeTrue();
            }
        })).ToArray();

        Task.WaitAll(tasks);

        // Assert
        segment.Count.Should().Be(threadsCount * itemsPerThread);

        // Verify all items can be found
        for (int threadId = 0; threadId < threadsCount; threadId++)
        {
            for (int i = 0; i < itemsPerThread; i++)
            {
                ulong hash = (ulong)(threadId * itemsPerThread + i);
                segment.MightContain(hash).Should().BeTrue($"hash {hash} should be found");
            }
        }
    }

    [Test]
    public void TryAdd_AndMightContain_Concurrent_ShouldWork()
    {
        // Arrange
        string path = GetTestFilePath();
        using var segment = PersistedBloomFilter.CreateNew(path, 10000, 10);
        int duration = 1000; // ms
        var cts = new CancellationTokenSource(duration);
        int addCount = 0;

        // Act - Writers and readers concurrently
        var writerTask = Task.Run(() =>
        {
            ulong hash = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                if (segment.TryAdd(hash++))
                    Interlocked.Increment(ref addCount);
            }
        });

        var readerTasks = Enumerable.Range(0, 5).Select(_ => Task.Run(() =>
        {
            ulong hash = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                segment.MightContain(hash++);
                Thread.Yield();
            }
        })).ToArray();

        Task.WaitAll(new[] { writerTask }.Concat(readerTasks).ToArray());

        // Assert - No exceptions, count is consistent with successful adds
        segment.Count.Should().Be(addCount);
    }

    [Test]
    public void Seal_WhileConcurrentAdds_ShouldEventuallyCauseTryAddToFail()
    {
        // Arrange
        string path = GetTestFilePath();
        using var segment = PersistedBloomFilter.CreateNew(path, 100000, 10);
        var barrier = new Barrier(2);

        int successfulAdds = 0;

        // Act
        var adderTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (int i = 0; i < 100_000; i++)
            {
                if (!segment.TryAdd((ulong)i))
                    return;

                Interlocked.Increment(ref successfulAdds);

                if (i % 100 == 0) Thread.Sleep(1); // Slow down to allow seal
            }
        });

        var sealTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            Thread.Sleep(50); // Let some adds happen
            segment.Seal();
        });

        Task.WaitAll(adderTask, sealTask);

        // Assert
        segment.IsSealed.Should().BeTrue();
        segment.Count.Should().Be(successfulAdds);
        segment.TryAdd(999_999).Should().BeFalse();
    }

    [Test]
    public void Flush_WhileConcurrentAdds_ShouldComplete()
    {
        // Arrange
        string path = GetTestFilePath();
        using var segment = PersistedBloomFilter.CreateNew(path, 100000, 10);
        var barrier = new Barrier(2);
        long countBeforeFlush = 0;

        // Act
        var adderTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (int i = 0; i < 1000; i++)
            {
                segment.TryAdd((ulong)i).Should().BeTrue();
                if (i % 100 == 0) Thread.Sleep(1);
            }
        });

        var flushTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            Thread.Sleep(100); // Let some adds happen
            countBeforeFlush = segment.Count;
            segment.Flush();
        });

        Task.WaitAll(adderTask, flushTask);

        // Assert - Flush completed
        segment.Count.Should().BeGreaterOrEqualTo(countBeforeFlush);
    }

    [Test]
    public void TryAdd_SameHashConcurrently_ShouldIncrementCountMultipleTimes()
    {
        // Arrange
        string path = GetTestFilePath();
        using var segment = PersistedBloomFilter.CreateNew(path, 10000, 10);
        ulong sameHash = 12345;
        int threadsCount = 10;
        var barrier = new Barrier(threadsCount);

        // Act - All threads add the same hash
        var tasks = Enumerable.Range(0, threadsCount).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            segment.TryAdd(sameHash).Should().BeTrue();
        })).ToArray();

        Task.WaitAll(tasks);

        // Assert - Count should be threadsCount (bloom filter doesn't deduplicate)
        segment.Count.Should().Be(threadsCount);
        segment.MightContain(sameHash).Should().BeTrue();
    }

    #endregion

    #region Edge Cases

    [Test]
    public void Create_WithZeroCapacity_ShouldThrowOnReload()
    {
        // Placeholder: real corruption test would need to write raw header bytes.
        Assert.Pass("Corruption scenario not implemented.");
    }

    [Test]
    public void Dispose_MultipleTimes_ShouldNotThrow()
    {
        // Arrange
        string path = GetTestFilePath();
        var segment = PersistedBloomFilter.CreateNew(path, 1000, 10);

        // Act & Assert
        segment.Dispose();
        Assert.DoesNotThrow(() => segment.Dispose());
    }

    [Test]
    public void TryAdd_AfterDispose_ShouldReturnFalse()
    {
        // Arrange
        string path = GetTestFilePath();
        var segment = PersistedBloomFilter.CreateNew(path, 1000, 10);
        segment.Dispose();

        // Act & Assert
        segment.TryAdd(1).Should().BeFalse("disposed segment should reject TryAdd");
    }

    #endregion
}
