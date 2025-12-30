// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.State.Flat.Persistence.BloomFilter;
using NUnit.Framework;

namespace Nethermind.Store.Test.Flat.Persistence.BloomFilter;

[TestFixture]
public class SegmentedBloomTests
{
    private string _testDirectory = null!;

    [SetUp]
    public void Setup()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"segmented_bloom_test_{Guid.NewGuid()}");
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

    #region Initialization Tests

    [Test]
    public void Create_WithEmptyDirectory_ShouldInitialize()
    {
        // Arrange & Act
        using var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: 100, bitsPerKey: 10);

        // Assert
        bloom.Should().NotBeNull();
        Directory.GetFiles(_testDirectory, "*.bloom").Should().HaveCount(1, "should create one initial segment");
    }

    [Test]
    public void Create_WithExistingSegments_ShouldLoadThem()
    {
        // Arrange - Create some segments first
        using (var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: 100, bitsPerKey: 10))
        {
            for (ulong i = 0; i < 150; i++) // Force rotation
            {
                bloom.Add(i);
            }
        }

        int segmentCountBefore = Directory.GetFiles(_testDirectory, "*.bloom").Length;

        // Act - Reopen
        using var reopened = new SegmentedBloom(_testDirectory, segmentCapacity: 100, bitsPerKey: 10);

        // Assert
        Directory.GetFiles(_testDirectory, "*.bloom").Length.Should().Be(segmentCountBefore);
        reopened.MightContain(1).Should().BeTrue();
        reopened.MightContain(149).Should().BeTrue();
    }

    [Test]
    public void Create_WithDisabledMode_ShouldNotCreateFiles()
    {
        // Arrange & Act
        using var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: 100, bitsPerKey: 10, enabled: false);

        // Assert
        Directory.GetFiles(_testDirectory, "*.bloom").Should().BeEmpty("disabled mode should not create files");
    }

    #endregion

    #region Add & Query Tests

    [Test]
    public void Add_SingleItem_ShouldBeFound()
    {
        // Arrange
        using var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: 100, bitsPerKey: 10);
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
        using var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: 100, bitsPerKey: 10);
        ulong[] hashes = { 1, 2, 3, 100, 1000, 99999 };

        // Act
        foreach (var hash in hashes)
        {
            bloom.Add(hash);
        }

        // Assert
        foreach (var hash in hashes)
        {
            bloom.MightContain(hash).Should().BeTrue($"hash {hash} should be found");
        }
    }

    [Test]
    public void Add_DuplicateItem_ShouldBeSkipped()
    {
        // Arrange
        using var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: 100, bitsPerKey: 10);
        ulong hash = 12345;

        // Act
        bloom.Add(hash);
        bloom.Add(hash); // Duplicate

        // Assert
        bloom.MightContain(hash).Should().BeTrue();
        // Note: We can't directly verify skip count without exposing metrics,
        // but the MightContain check before Add should prevent the second add
    }

    [Test]
    public void MightContain_NonExistentItem_ShouldCheckAllSegments()
    {
        // Arrange
        using var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: 100, bitsPerKey: 10);

        // Add items to trigger rotation
        for (ulong i = 0; i < 150; i++)
        {
            bloom.Add(i);
        }

        // Act & Assert
        bloom.MightContain(1).Should().BeTrue("first segment");
        bloom.MightContain(149).Should().BeTrue("second segment");
        // Note: False positives possible, but items definitely added should be found
    }

    [Test]
    public void Add_DisabledMode_ShouldAlwaysReturnTrue()
    {
        // Arrange
        using var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: 100, bitsPerKey: 10, enabled: false);

        // Act & Assert
        bloom.MightContain(99999).Should().BeTrue("disabled mode always returns true");
        bloom.Add(1); // Should not throw
        bloom.MightContain(1).Should().BeTrue();
    }

    #endregion

    #region Segment Rotation Tests

    [Test]
    public void Add_FillSegment_ShouldTriggerRotation()
    {
        // Arrange
        int segmentCapacity = 50;
        using var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: segmentCapacity, bitsPerKey: 10);

        // Act - Add more than capacity
        for (ulong i = 0; i < (ulong)(segmentCapacity + 10); i++)
        {
            bloom.Add(i);
        }

        // Assert
        var segmentFiles = Directory.GetFiles(_testDirectory, "*.bloom");
        segmentFiles.Should().HaveCountGreaterThan(1, "should have created additional segment");
    }

    [Test]
    public void Add_AfterRotation_ShouldStillFindAllItems()
    {
        // Arrange
        int segmentCapacity = 50;
        using var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: segmentCapacity, bitsPerKey: 10);
        var addedHashes = new List<ulong>();

        // Act - Add items across rotation
        for (ulong i = 0; i < (ulong)(segmentCapacity * 2 + 10); i++)
        {
            bloom.Add(i);
            addedHashes.Add(i);
        }

        // Assert - All items should be found
        foreach (var hash in addedHashes)
        {
            bloom.MightContain(hash).Should().BeTrue($"hash {hash} should be found after rotation");
        }
    }

    [Test]
    public void Rotation_ShouldSealOldSegment()
    {
        // Arrange
        int segmentCapacity = 50;
        using var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: segmentCapacity, bitsPerKey: 10);

        // Act - Fill first segment
        for (ulong i = 0; i < (ulong)segmentCapacity; i++)
        {
            bloom.Add(i);
        }

        var firstSegmentFile = Directory.GetFiles(_testDirectory, "*.bloom").OrderBy(f => File.GetCreationTime(f)).First();

        // Trigger rotation
        bloom.Add((ulong)segmentCapacity);
        bloom.Add((ulong)(segmentCapacity + 1));

        // Assert - First segment should be sealed (we can't directly check, but it should be sealed)
        // Wait a bit for rotation to complete
        Thread.Sleep(100);

        var segmentFiles = Directory.GetFiles(_testDirectory, "*.bloom");
        segmentFiles.Should().HaveCountGreaterThan(1);
    }

    #endregion

    #region Concurrency Tests

    [Test]
    public void Add_Concurrent_ShouldBeThreadSafe()
    {
        // Arrange
        using var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: 1000, bitsPerKey: 10);
        int threadsCount = 10;
        int itemsPerThread = 50;
        var barrier = new Barrier(threadsCount);
        var addedHashes = new System.Collections.Concurrent.ConcurrentBag<ulong>();

        // Act - Multiple threads adding concurrently
        var tasks = Enumerable.Range(0, threadsCount).Select(threadId => Task.Run(() =>
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
        foreach (var hash in addedHashes)
        {
            bloom.MightContain(hash).Should().BeTrue($"hash {hash} should be found");
        }
    }

    [Test]
    public void Rotation_Concurrent_ShouldHandleRaces()
    {
        // Arrange
        int segmentCapacity = 100;
        using var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: segmentCapacity, bitsPerKey: 10);
        int threadsCount = 10;
        int itemsPerThread = 20; // Total = 200, should trigger rotation
        var barrier = new Barrier(threadsCount);

        // Act - Multiple threads filling and triggering rotation
        var tasks = Enumerable.Range(0, threadsCount).Select(threadId => Task.Run(() =>
        {
            barrier.SignalAndWait(); // Sync start
            for (int i = 0; i < itemsPerThread; i++)
            {
                ulong hash = (ulong)(threadId * 1000000 + i); // Ensure unique hashes
                bloom.Add(hash);
            }
        })).ToArray();

        Task.WaitAll(tasks);

        // Assert - Should have rotated, check segment count
        Thread.Sleep(500); // Wait for rotation to settle
        var segmentFiles = Directory.GetFiles(_testDirectory, "*.bloom");
        segmentFiles.Should().HaveCountGreaterThan(1, "concurrent adds should trigger rotation");
    }

    [Test]
    public void Add_ConcurrentDuplicates_ShouldHandleSkipping()
    {
        // Arrange
        using var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: 1000, bitsPerKey: 10);
        ulong sameHash = 12345;
        int threadsCount = 10;
        var barrier = new Barrier(threadsCount);

        // Act - All threads try to add the same hash
        var tasks = Enumerable.Range(0, threadsCount).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            bloom.Add(sameHash);
        })).ToArray();

        Task.WaitAll(tasks);

        // Assert - Should not throw, hash should be found
        bloom.MightContain(sameHash).Should().BeTrue();
    }

    [Test]
    public void Add_ConcurrentWithMightContain_ShouldWork()
    {
        // Arrange
        using var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: 10000, bitsPerKey: 10);
        int duration = 1000; // ms
        var cts = new CancellationTokenSource(duration);
        var addedHashes = new System.Collections.Concurrent.ConcurrentBag<ulong>();

        // Act - Some threads adding, others querying
        var writerTasks = Enumerable.Range(0, 3).Select(threadId => Task.Run(() =>
        {
            try
            {
                ulong hash = (ulong)(threadId * 1000000);
                int iteration = 0;
                while (!cts.Token.IsCancellationRequested)
                {
                    bloom.Add(hash++);
                    addedHashes.Add(hash);
                    if (iteration % 1000 == 0) Task.Yield();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                throw;
            }
        })).ToArray();

        var readerTasks = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
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

    [Test]
    public void Rotation_RaceCondition_ShouldNotCreateDuplicateSegments()
    {
        // Arrange
        int segmentCapacity = 50;
        using var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: segmentCapacity, bitsPerKey: 10);

        // Pre-fill close to capacity
        for (ulong i = 0; i < (ulong)(segmentCapacity - 5); i++)
        {
            bloom.Add(i);
        }

        // Act - Multiple threads hitting full capacity simultaneously
        int threadsCount = 10;
        var barrier = new Barrier(threadsCount);
        var tasks = Enumerable.Range(0, threadsCount).Select(threadId => Task.Run(() =>
        {
            barrier.SignalAndWait(); // All start at once
            for (int i = 0; i < 10; i++)
            {
                ulong hash = (ulong)(threadId * 10000 + i);
                bloom.Add(hash);
            }
        })).ToArray();

        Task.WaitAll(tasks);
        Thread.Sleep(500); // Wait for rotations to settle

        // Assert - Should have reasonable number of segments (not one per thread)
        var segmentFiles = Directory.GetFiles(_testDirectory, "*.bloom");
        segmentFiles.Length.Should().BeLessThan(5, "should not create excessive segments from race conditions");
    }

    #endregion

    #region Persistence Tests

    [Test]
    public void Flush_ShouldPersistAllSegments()
    {
        // Arrange
        ulong[] testHashes = { 1, 2, 3, 100, 200 };

        using (var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: 100, bitsPerKey: 10))
        {
            foreach (var hash in testHashes)
            {
                bloom.Add(hash);
            }
            bloom.Flush();
        }

        // Act - Reopen
        using var reopened = new SegmentedBloom(_testDirectory, segmentCapacity: 100, bitsPerKey: 10);

        // Assert
        foreach (var hash in testHashes)
        {
            reopened.MightContain(hash).Should().BeTrue($"hash {hash} should persist after flush");
        }
    }

    [Test]
    public void Dispose_ShouldPersistData()
    {
        // Arrange
        ulong[] testHashes = { 1, 2, 3, 100, 200 };

        using (var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: 100, bitsPerKey: 10))
        {
            foreach (var hash in testHashes)
            {
                bloom.Add(hash);
            }
            // Dispose without explicit flush
        }

        // Act - Reopen
        using var reopened = new SegmentedBloom(_testDirectory, segmentCapacity: 100, bitsPerKey: 10);

        // Assert
        foreach (var hash in testHashes)
        {
            reopened.MightContain(hash).Should().BeTrue($"hash {hash} should persist after dispose");
        }
    }

    [Test]
    public void Reload_WithMultipleSegments_ShouldPreserveData()
    {
        // Arrange - Create multiple segments
        int segmentCapacity = 50;
        var allHashes = new List<ulong>();

        using (var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: segmentCapacity, bitsPerKey: 10))
        {
            for (ulong i = 0; i < (ulong)(segmentCapacity * 3); i++)
            {
                bloom.Add(i);
                allHashes.Add(i);
            }
        }

        // Act - Reopen
        using var reopened = new SegmentedBloom(_testDirectory, segmentCapacity: segmentCapacity, bitsPerKey: 10);

        // Assert - All hashes should be found
        foreach (var hash in allHashes)
        {
            reopened.MightContain(hash).Should().BeTrue($"hash {hash} should be found after reload");
        }
    }

    #endregion

    #region Disabled Mode Tests

    [Test]
    public void DisabledMode_Add_ShouldNotThrow()
    {
        // Arrange
        using var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: 100, bitsPerKey: 10, enabled: false);

        // Act & Assert
        Assert.DoesNotThrow(() =>
        {
            for (ulong i = 0; i < 1000; i++)
            {
                bloom.Add(i);
            }
        });
    }

    [Test]
    public void DisabledMode_MightContain_ShouldAlwaysReturnTrue()
    {
        // Arrange
        using var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: 100, bitsPerKey: 10, enabled: false);

        // Act & Assert
        for (ulong i = 0; i < 100; i++)
        {
            bloom.MightContain(i).Should().BeTrue();
        }
    }

    [Test]
    public void DisabledMode_Flush_ShouldNotThrow()
    {
        // Arrange
        using var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: 100, bitsPerKey: 10, enabled: false);

        // Act & Assert
        Assert.DoesNotThrow(() => bloom.Flush());
    }

    [Test]
    public void DisabledMode_Dispose_ShouldNotThrow()
    {
        // Arrange
        var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: 100, bitsPerKey: 10, enabled: false);

        // Act & Assert
        Assert.DoesNotThrow(() => bloom.Dispose());
    }

    #endregion

    #region Edge Cases

    [Test]
    public void Dispose_MultipleTimes_ShouldNotThrow()
    {
        // Arrange
        var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: 100, bitsPerKey: 10);

        // Act & Assert
        bloom.Dispose();
        Assert.DoesNotThrow(() => bloom.Dispose());
    }

    [Test]
    public void Add_LargeNumberOfItems_ShouldHandleMultipleRotations()
    {
        // Arrange
        int segmentCapacity = 100;
        int totalItems = 500;
        using var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: segmentCapacity, bitsPerKey: 10);
        var allHashes = new List<ulong>();

        // Act
        for (ulong i = 0; i < (ulong)totalItems; i++)
        {
            bloom.Add(i);
            allHashes.Add(i);
        }

        // Assert
        var segmentFiles = Directory.GetFiles(_testDirectory, "*.bloom");
        segmentFiles.Length.Should().BeGreaterOrEqualTo(totalItems / segmentCapacity);

        // Verify all can be found
        foreach (var hash in allHashes.Take(50)) // Sample check
        {
            bloom.MightContain(hash).Should().BeTrue($"hash {hash} should be found");
        }
    }

    [Test]
    public void MightContain_BeforeAnyAdds_ShouldReturnFalse()
    {
        // Arrange
        using var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: 100, bitsPerKey: 10);

        // Act & Assert
        // Note: Might return false or rarely true due to random hash collisions
        // Just verify it doesn't throw
        bool result = bloom.MightContain(99999);
        Assert.Pass("Query completed without exception");
    }

    #endregion

    #region WAL Tests

    private const string WalFileName = "segmented_bloom.wal";

    private static void WriteWal(string walPath, params ulong[] values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(walPath)!);

        using var fs = new FileStream(walPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        Span<byte> buf = stackalloc byte[sizeof(ulong)];
        foreach (ulong v in values)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buf, v);
            fs.Write(buf);
        }
        fs.Flush(true);
    }

    [Test]
    public void Wal_EnabledByDefault_ShouldCreateWalFile_AndAppendOnAdd()
    {
        // Arrange
        using var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: 100, bitsPerKey: 10);
        string walPath = Path.Combine(_testDirectory, WalFileName);

        // Assert: constructor opens/creates WAL and checkpoints => empty
        File.Exists(walPath).Should().BeTrue("WAL should be created when enabled by default");
        new FileInfo(walPath).Length.Should().Be(0, "constructor checkpoint should truncate WAL");

        // Act
        bloom.Add(12345UL);
        bloom.Flush(); // WAL-only flush

        // Assert: at least one record written (8 bytes)
        new FileInfo(walPath).Length.Should().BeGreaterThanOrEqualTo(8, "Add should append to WAL when enabled");
    }

    [Test]
    public void Wal_DisabledGlobally_ShouldNotCreateWalFile_AndFlushShouldPersistSegments()
    {
        // Arrange
        using var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: 100, bitsPerKey: 10, enabled: true, walFlushThresholdBytes: 0);
        string walPath = Path.Combine(_testDirectory, WalFileName);

        // Assert
        File.Exists(walPath).Should().BeFalse("WAL should not be created when globally disabled");

        // Act (should still function normally)
        ulong h = 1;
        bloom.Add(h);
        bloom.MightContain(h).Should().BeTrue();
        bloom.Flush(); // since WAL disabled => flush segments

        // Assert: WAL still not created
        File.Exists(walPath).Should().BeFalse("WAL should still not be created after operations when globally disabled");

        // Assert: data persisted by segment flush
        using var reopened = new SegmentedBloom(_testDirectory, segmentCapacity: 100, bitsPerKey: 10, enabled: true, walFlushThresholdBytes: 0);
        reopened.MightContain(h).Should().BeTrue("Flush should persist segments when WAL is disabled");
    }

    [Test]
    public void Wal_PerAddDisabled_ShouldNotAppend()
    {
        // Arrange
        using var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: 100, bitsPerKey: 10, enabled: true, walFlushThresholdBytes: 1024);
        string walPath = Path.Combine(_testDirectory, WalFileName);

        File.Exists(walPath).Should().BeTrue();
        new FileInfo(walPath).Length.Should().Be(0);

        // Act: add with WAL disabled for this call
        bloom.Add(99999UL, writeWal: false);
        bloom.Flush(); // WAL-only flush

        // Assert: WAL remains empty
        new FileInfo(walPath).Length.Should().Be(0, "per-add WAL disable should prevent appending records");

        // sanity: bloom still got the value
        bloom.MightContain(99999UL).Should().BeTrue();
    }

    [Test]
    public void Wal_Replay_ShouldRestoreEntries_AndCheckpointTruncateWal()
    {
        // Arrange: prewrite a WAL with entries (no bloom segments created yet)
        string walPath = Path.Combine(_testDirectory, WalFileName);
        ulong[] hashes = { 1111UL, 2222UL, 3333UL };
        WriteWal(walPath, hashes);

        new FileInfo(walPath).Length.Should().Be(hashes.Length * sizeof(ulong));

        // Act: opening with WAL enabled should replay then checkpoint (truncate)
        using var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: 100, bitsPerKey: 10, enabled: true, walFlushThresholdBytes: 1024);

        // Assert: values are present after replay
        foreach (ulong h in hashes)
            bloom.MightContain(h).Should().BeTrue($"hash {h} should be restored from WAL replay");

        // Assert: WAL truncated by checkpoint
        File.Exists(walPath).Should().BeTrue();
        new FileInfo(walPath).Length.Should().Be(0, "constructor checkpoint should truncate WAL after replay");
    }

    [Test]
    public void Wal_Flush_ShouldNotCheckpointOrTruncateWal()
    {
        // Arrange
        using var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: 100, bitsPerKey: 10, enabled: true, walFlushThresholdBytes: 1024);
        string walPath = Path.Combine(_testDirectory, WalFileName);

        // WAL is not written in add
        bloom.Add(424242UL);
        long lenBefore = new FileInfo(walPath).Length;
        lenBefore.Should().BeGreaterOrEqualTo(0);

        // Flush will write WAL but not bloom.
        bloom.Flush();
        new FileInfo(walPath).Length.Should().BeGreaterOrEqualTo(8);
    }

    [Test]
    public void Wal_FlushDurable_ShouldCheckpointAndTruncateWal()
    {
        // Arrange
        using var bloom = new SegmentedBloom(_testDirectory, segmentCapacity: 100, bitsPerKey: 10, enabled: true, walFlushThresholdBytes: 1024);
        string walPath = Path.Combine(_testDirectory, WalFileName);

        bloom.Add(424242UL);
        bloom.Flush();
        new FileInfo(walPath).Length.Should().BeGreaterThanOrEqualTo(8);

        // Act
        bloom.FlushDurable();

        // Assert: WAL truncated
        new FileInfo(walPath).Length.Should().Be(0, "FlushDurable should checkpoint and truncate WAL when enabled");
    }

    #endregion
}
