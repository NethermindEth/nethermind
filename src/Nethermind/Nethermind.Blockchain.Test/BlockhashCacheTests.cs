// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[TestFixture]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
[Parallelizable(ParallelScope.All)]
public class BlockhashCacheTests
{
    private IHeaderStore _headerStore = null!;
    private BlockhashCache _cache = null!;

    [SetUp]
    public void Setup()
    {
        _headerStore = new HeaderStore(new MemDb(), new MemDb());
        _cache = new BlockhashCache(_headerStore, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _cache.Dispose();
    }

    [Test]
    public void Can_add_single_block()
    {
        // Arrange
        BlockHeader header = Build.A.BlockHeader.WithNumber(0).TestObject;

        // Act
        _cache.Set(header);

        // Assert
        _cache.Contains(header.Hash!).Should().BeTrue();
    }

    [Test]
    public void Can_get_block_at_depth_zero()
    {
        // Arrange
        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        _cache.Set(header);

        // Act
        Hash256? result = _cache.GetHash(header, 0);

        // Assert
        result.Should().Be(header.Hash!);
    }

    [Test]
    public void Can_get_ancestor_from_sequential_blocks()
    {
        // Arrange - build chain of 10 blocks
        BlockHeader[] headers = new BlockHeader[10];
        headers[0] = Build.A.BlockHeader.WithNumber(0).TestObject;
        _cache.Set(headers[0]);

        for (int i = 1; i < 10; i++)
        {
            headers[i] = Build.A.BlockHeader
                .WithNumber(i)
                .WithParent(headers[i - 1])
                .TestObject;
            _cache.Set(headers[i]);
        }

        // Act - query block 9 for ancestor 5 blocks back
        Hash256? result = _cache.GetHash(headers[9], 5);

        // Assert
        result.Should().Be(headers[4].Hash!);
    }

    [Test]
    public void Sequential_blocks_share_snapshot()
    {
        // Arrange
        BlockHeader[] headers = new BlockHeader[100];
        headers[0] = Build.A.BlockHeader.WithNumber(0).TestObject;
        _cache.Set(headers[0]);

        for (int i = 1; i < 100; i++)
        {
            headers[i] = Build.A.BlockHeader
                .WithNumber(i)
                .WithParent(headers[i - 1])
                .TestObject;
            _cache.Set(headers[i]);
        }

        // Act
        BlockhashCache.CacheStats stats = _cache.GetStats();

        // Assert - should have very few snapshots (segments)
        stats.UniqueSnapshots.Should().BeLessThan(10); // 100 blocks / 256 per segment = 1 segment
        stats.AverageBlocksPerSnapshot.Should().BeGreaterThan(10); // Many blocks per snapshot
    }

    [Test]
    public void Can_handle_fork()
    {
        // Arrange - build main chain
        BlockHeader[] mainChain = new BlockHeader[10];
        mainChain[0] = Build.A.BlockHeader.WithNumber(0).TestObject;
        _cache.Set(mainChain[0]);

        for (int i = 1; i < 10; i++)
        {
            mainChain[i] = Build.A.BlockHeader
                .WithNumber(i)
                .WithParent(mainChain[i - 1])
                .TestObject;
            _cache.Set(mainChain[i]);
        }

        // Act - create fork at block 5
        BlockHeader fork6 = Build.A.BlockHeader
            .WithNumber(6)
            .WithParent(mainChain[5])
            .WithExtraData([1]) // need to change hash
            .TestObject;
        _cache.Set(fork6);

        BlockHeader fork7 = Build.A.BlockHeader
            .WithNumber(7)
            .WithParent(fork6)
            .TestObject;
        _cache.Set(fork7);

        // Assert - both chains can query their ancestors
        Hash256? mainAncestor = _cache.GetHash(mainChain[9], 4); // Should get block 5
        Hash256? forkAncestor = _cache.GetHash(fork7, 2); // Should get block 5

        mainAncestor.Should().Be(mainChain[5].Hash!);
        forkAncestor.Should().Be(mainChain[5].Hash!);

        // Both chains should have different block 7
        _cache.GetHash(mainChain[9], 2).Should().Be(mainChain[7].Hash!);
        _cache.GetHash(fork7, 0).Should().Be(fork7.Hash!);
        mainChain[7].Hash.Should().NotBe(fork7.Hash!);
    }

    [Test]
    public void Fork_creates_new_snapshot()
    {
        // Arrange
        BlockHeader[] mainChain = new BlockHeader[10];
        mainChain[0] = Build.A.BlockHeader.WithNumber(0).TestObject;
        _cache.Set(mainChain[0]);

        for (int i = 1; i < 10; i++)
        {
            mainChain[i] = Build.A.BlockHeader
                .WithNumber(i)
                .WithParent(mainChain[i - 1])
                .TestObject;
            _cache.Set(mainChain[i]);
        }

        BlockhashCache.CacheStats statsBefore = _cache.GetStats();

        // Act - create fork
        BlockHeader fork = Build.A.BlockHeader
            .WithNumber(6)
            .WithParent(mainChain[5])
            .WithExtraData([1]) // need to change hash
            .TestObject;
        _cache.Set(fork);

        BlockhashCache.CacheStats statsAfter = _cache.GetStats();

        // Assert - should have more snapshots after fork
        statsAfter.UniqueSnapshots.Should().BeGreaterThan(statsBefore.UniqueSnapshots);
    }

    [Test]
    public void Can_handle_segments_beyond_256()
    {
        // Arrange - build chain longer than one segment
        BlockHeader[] headers = new BlockHeader[300];
        headers[0] = Build.A.BlockHeader.WithNumber(0).TestObject;
        _cache.Set(headers[0]);

        for (int i = 1; i < 300; i++)
        {
            headers[i] = Build.A.BlockHeader
                .WithNumber(i)
                .WithParent(headers[i - 1])
                .TestObject;
            _cache.Set(headers[i]);
        }

        // Act - query across segment boundary
        Hash256? result = _cache.GetHash(headers[299], 250);

        // Assert
        result.Should().Be(headers[49].Hash!);
    }

    [Test]
    public void Can_load_from_store_on_cache_miss()
    {
        // Arrange - add blocks to store but not cache
        BlockHeader[] headers = new BlockHeader[10];
        headers[0] = Build.A.BlockHeader.WithNumber(0).TestObject;
        _headerStore.Insert(headers[0]);

        for (int i = 1; i < 10; i++)
        {
            headers[i] = Build.A.BlockHeader
                .WithNumber(i)
                .WithParent(headers[i - 1])
                .TestObject;
            _headerStore.Insert(headers[i]);
        }

        // Act - query cache (should load from store)
        Hash256? result = _cache.GetHash(headers[9], 5);

        // Assert
        result.Should().Be(headers[4].Hash!);
        _cache.Contains(headers[9].Hash!).Should().BeTrue(); // Should now be cached
    }

    [Test]
    public void Returns_null_for_depth_beyond_chain_length()
    {
        // Arrange
        BlockHeader header = Build.A.BlockHeader.WithNumber(5).TestObject;
        _cache.Set(header);

        // Act
        Hash256? result = _cache.GetHash(header, 10);

        // Assert
        result.Should().BeNull();
    }

    [Test]
    public void Can_prune_old_blocks()
    {
        // Arrange
        BlockHeader[] headers = new BlockHeader[100];
        headers[0] = Build.A.BlockHeader.WithNumber(0).TestObject;
        _cache.Set(headers[0]);

        for (int i = 1; i < 100; i++)
        {
            headers[i] = Build.A.BlockHeader
                .WithNumber(i)
                .WithParent(headers[i - 1])
                .TestObject;
            _cache.Set(headers[i]);
        }

        // Act
        int pruned = _cache.PruneBefore(50);

        // Assert
        pruned.Should().Be(50);
        _cache.Contains(headers[49].Hash!).Should().BeFalse();
        _cache.Contains(headers[50].Hash!).Should().BeTrue();
    }

    [Test]
    public void Stats_track_segment_depth()
    {
        // Arrange - build long chain
        BlockHeader[] headers = new BlockHeader[600];
        headers[0] = Build.A.BlockHeader.WithNumber(0).TestObject;
        _cache.Set(headers[0]);

        for (int i = 1; i < 600; i++)
        {
            headers[i] = Build.A.BlockHeader
                .WithNumber(i)
                .WithParent(headers[i - 1])
                .TestObject;
            _cache.Set(headers[i]);
        }

        // Act
        BlockhashCache.CacheStats stats = _cache.GetStats();

        // Assert
        stats.MaxSegmentDepth.Should().BeGreaterOrEqualTo(3); // 600 blocks / 256 per segment = ~3 segments
    }

    [Test]
    public void Can_query_multiple_unfinalized_blocks()
    {
        // Arrange - simulate multiple unfinalized blocks that all need 256 ancestors
        BlockHeader[] chain = new BlockHeader[1000];
        chain[0] = Build.A.BlockHeader.WithNumber(0).TestObject;
        _cache.Set(chain[0]);

        for (int i = 1; i < 1000; i++)
        {
            chain[i] = Build.A.BlockHeader
                .WithNumber(i)
                .WithParent(chain[i - 1])
                .TestObject;
            _cache.Set(chain[i]);
        }

        // Act - query from multiple "unfinalized" blocks
        Hash256? result900 = _cache.GetHash(chain[900], 200); // Block 700
        Hash256? result950 = _cache.GetHash(chain[950], 200); // Block 750
        Hash256? result999 = _cache.GetHash(chain[999], 200); // Block 799

        // Assert
        result900.Should().Be(chain[700].Hash!);
        result950.Should().Be(chain[750].Hash!);
        result999.Should().Be(chain[799].Hash!);
    }

    [Test]
    public async Task Prefetch_loads_ancestors_in_background()
    {
        // Arrange - add blocks to store
        BlockHeader[] headers = new BlockHeader[100];
        headers[0] = Build.A.BlockHeader.WithNumber(0).TestObject;
        _headerStore.Insert(headers[0]);

        for (int i = 1; i < 100; i++)
        {
            headers[i] = Build.A.BlockHeader
                .WithNumber(i)
                .WithParent(headers[i - 1])
                .TestObject;
            _headerStore.Insert(headers[i]);
        }

        // Act - prefetch
        await _cache.Prefetch(headers[99]);

        // Assert - ancestors should now be cached
        _cache.Contains(headers[99].Hash!).Should().BeTrue();
        _cache.Contains(headers[50].Hash!).Should().BeTrue();
    }

    [Test]
    public void Remove_decrements_snapshot_refcount()
    {
        // Arrange
        BlockHeader[] headers = new BlockHeader[10];
        headers[0] = Build.A.BlockHeader.WithNumber(0).TestObject;
        _cache.Set(headers[0]);

        for (int i = 1; i < 10; i++)
        {
            headers[i] = Build.A.BlockHeader
                .WithNumber(i)
                .WithParent(headers[i - 1])
                .TestObject;
            _cache.Set(headers[i]);
        }

        BlockhashCache.CacheStats statsBefore = _cache.GetStats();

        // Act - remove blocks
        for (int i = 0; i < 5; i++)
        {
            _cache.Remove(headers[i].Hash!);
        }

        BlockhashCache.CacheStats statsAfter = _cache.GetStats();

        // Assert
        statsAfter.TotalBlocks.Should().Be(statsBefore.TotalBlocks - 5);
    }

    [Test]
    public void Clear_removes_all_blocks_and_snapshots()
    {
        // Arrange
        BlockHeader[] headers = new BlockHeader[100];
        headers[0] = Build.A.BlockHeader.WithNumber(0).TestObject;
        _cache.Set(headers[0]);

        for (int i = 1; i < 100; i++)
        {
            headers[i] = Build.A.BlockHeader
                .WithNumber(i)
                .WithParent(headers[i - 1])
                .TestObject;
            _cache.Set(headers[i]);
        }

        // Act
        _cache.Clear();
        BlockhashCache.CacheStats stats = _cache.GetStats();

        // Assert
        stats.TotalBlocks.Should().Be(0);
        stats.UniqueSnapshots.Should().Be(0);
    }

    [Test]
    public void Stress_test_with_thousands_of_blocks_and_periodic_pruning()
    {
        // Arrange
        const int totalBlocks = 5000;
        const int pruneInterval = 128;
        const int pruneKeepWindow = 512; // Keep last 512 blocks
        BlockHeader[] chain = new BlockHeader[totalBlocks];

        // Act & Assert - Build chain with periodic pruning
        chain[0] = Build.A.BlockHeader.WithNumber(0).TestObject;
        _cache.Set(chain[0]);
        _headerStore.Insert(chain[0]);

        int totalPruned = 0;
        BlockhashCache.CacheStats lastStats = _cache.GetStats();

        for (int i = 1; i < totalBlocks; i++)
        {
            // Add new block
            chain[i] = Build.A.BlockHeader
                .WithNumber(i)
                .WithParent(chain[i - 1])
                .TestObject;
            _cache.Set(chain[i]);
            _headerStore.Insert(chain[i]);

            // Verify recent ancestor lookups work correctly
            if (i >= 10)
            {
                Hash256? ancestor5 = _cache.GetHash(chain[i], 5);
                ancestor5.Should().Be(chain[i - 5].Hash!,
                    $"block {i} should find ancestor at depth 5");
            }

            if (i >= 100)
            {
                Hash256? ancestor100 = _cache.GetHash(chain[i], 100);
                ancestor100.Should().Be(chain[i - 100].Hash!,
                    $"block {i} should find ancestor at depth 100");
            }

            // Perform pruning every 128 blocks
            if (i > 0 && i % pruneInterval == 0 && i > pruneKeepWindow)
            {
                long pruneBeforeBlock = i - pruneKeepWindow;
                int prunedCount = _cache.PruneBefore(pruneBeforeBlock);
                totalPruned += prunedCount;

                // Verify pruned blocks are gone
                if (pruneBeforeBlock > 0)
                {
                    _cache.Contains(chain[pruneBeforeBlock - 1].Hash!).Should().BeFalse(
                        $"block {pruneBeforeBlock - 1} should be pruned at iteration {i}");
                }

                // Verify kept blocks are still present
                _cache.Contains(chain[i].Hash!).Should().BeTrue(
                    $"block {i} should be kept at iteration {i}");
                _cache.Contains(chain[i - pruneKeepWindow / 2].Hash!).Should().BeTrue(
                    $"block {i - pruneKeepWindow / 2} should be kept at iteration {i}");

                BlockhashCache.CacheStats stats = _cache.GetStats();

                // Cache should not grow unbounded
                stats.TotalBlocks.Should().BeLessThan(pruneKeepWindow + pruneInterval,
                    $"cache size should be bounded at iteration {i}");

                lastStats = stats;
            }

            // Test queries at various depths periodically
            if (i > 0 && i % 250 == 0)
            {
                // Query at multiple depths
                for (int depth = 1; depth <= Math.Min(i, 256); depth += 32)
                {
                    Hash256? result = _cache.GetHash(chain[i], depth);
                    if (i - depth >= 0 && _cache.Contains(chain[i - depth].Hash!))
                    {
                        result.Should().Be(chain[i - depth].Hash!,
                            $"block {i} should find ancestor at depth {depth}");
                    }
                }
            }
        }

        // Final validations
        BlockhashCache.CacheStats finalStats = _cache.GetStats();

        // Verify final cache size is bounded
        finalStats.TotalBlocks.Should().BeLessThan(pruneKeepWindow + pruneInterval,
            "final cache size should be bounded");

        // Verify we pruned a significant number of blocks
        totalPruned.Should().BeGreaterThan(totalBlocks - pruneKeepWindow - pruneInterval,
            "should have pruned most old blocks");

        // Verify recent blocks are still accessible
        for (int i = totalBlocks - 256; i < totalBlocks; i++)
        {
            _cache.Contains(chain[i].Hash!).Should().BeTrue(
                $"recent block {i} should still be in cache");

            if (i < totalBlocks - 1)
            {
                Hash256? hash = _cache.GetHash(chain[totalBlocks - 1], totalBlocks - 1 - i);
                hash.Should().Be(chain[i].Hash!,
                    $"should be able to query ancestor {i} from block {totalBlocks - 1}");
            }
        }

        // Verify statistics are reasonable
        finalStats.UniqueSnapshots.Should().BeLessThan(10,
            "should have few snapshots due to pruning");
        finalStats.MaxSegmentDepth.Should().BeGreaterThan(0);
    }

    [Test]
    public void Stress_test_with_multiple_forks_and_pruning()
    {
        // Arrange
        const int mainChainLength = 3000;
        const int pruneInterval = 128;
        const int pruneKeepWindow = 384;
        const int forksCount = 10;
        const int forkLength = 50;

        BlockHeader[] mainChain = new BlockHeader[mainChainLength];
        List<BlockHeader[]> forks = new();

        // Act - Build main chain
        mainChain[0] = Build.A.BlockHeader.WithNumber(0).TestObject;
        _cache.Set(mainChain[0]);
        _headerStore.Insert(mainChain[0]);

        for (int i = 1; i < mainChainLength; i++)
        {
            mainChain[i] = Build.A.BlockHeader
                .WithNumber(i)
                .WithParent(mainChain[i - 1])
                .TestObject;
            _cache.Set(mainChain[i]);
            _headerStore.Insert(mainChain[i]);

            // Create forks at specific intervals
            if (i % (mainChainLength / forksCount) == 0 && i > 100 && forks.Count < forksCount)
            {
                BlockHeader[] fork = new BlockHeader[forkLength];
                fork[0] = Build.A.BlockHeader
                    .WithNumber(i)
                    .WithParent(mainChain[i - 1])
                    .WithExtraData(new byte[] { (byte)forks.Count, 0xFF }) // Unique hash
                    .TestObject;
                _cache.Set(fork[0]);

                for (int j = 1; j < forkLength; j++)
                {
                    fork[j] = Build.A.BlockHeader
                        .WithNumber(i + j)
                        .WithParent(fork[j - 1])
                        .TestObject;
                    _cache.Set(fork[j]);
                }

                forks.Add(fork);
            }

            // Periodic pruning
            if (i > 0 && i % pruneInterval == 0 && i > pruneKeepWindow)
            {
                long pruneBeforeBlock = i - pruneKeepWindow;
                _cache.PruneBefore(pruneBeforeBlock);
            }

            // Verify ancestor queries work on main chain
            if (i >= 50)
            {
                Hash256? ancestor = _cache.GetHash(mainChain[i], 50);
                if (_cache.Contains(mainChain[i - 50].Hash!))
                {
                    ancestor.Should().Be(mainChain[i - 50].Hash!,
                        $"main chain block {i} should find ancestor at depth 50");
                }
            }
        }

        // Verify all forks can still query their ancestors (if not pruned)
        foreach (BlockHeader[] fork in forks)
        {
            if (_cache.Contains(fork[^1].Hash!))
            {
                for (int depth = 1; depth < Math.Min(forkLength, 50); depth++)
                {
                    if (forkLength - depth - 1 >= 0 && _cache.Contains(fork[forkLength - depth - 1].Hash!))
                    {
                        Hash256? ancestor = _cache.GetHash(fork[^1], depth);
                        ancestor.Should().Be(fork[forkLength - depth - 1].Hash!,
                            "fork should maintain correct ancestor relationships");
                    }
                }
            }
        }

        // Final validations
        BlockhashCache.CacheStats finalStats = _cache.GetStats();
        finalStats.TotalBlocks.Should().BeLessThan(pruneKeepWindow + pruneInterval + (forksCount * forkLength),
            "cache should be bounded even with forks");

        // Note: Most forks get pruned during execution, so we expect fewer snapshots at the end
        // The test validates that forks work correctly while they exist, not that they all survive pruning
        finalStats.UniqueSnapshots.Should().BeGreaterThan(0,
            "should have at least some snapshots remaining after pruning");
    }

    [Test]
    public async Task Stress_test_with_concurrent_operations()
    {
        // Arrange
        const int blocksPerTask = 500;
        const int taskCount = 4;
        const int pruneInterval = 128;
        BlockHeader[] sharedChain = new BlockHeader[blocksPerTask * taskCount];

        // Build initial chain in header store
        sharedChain[0] = Build.A.BlockHeader.WithNumber(0).TestObject;
        _headerStore.Insert(sharedChain[0]);

        for (int i = 1; i < sharedChain.Length; i++)
        {
            sharedChain[i] = Build.A.BlockHeader
                .WithNumber(i)
                .WithParent(sharedChain[i - 1])
                .TestObject;
            _headerStore.Insert(sharedChain[i]);
        }

        // Act - Concurrent operations
        List<Task> tasks = new();

        // Task 1-3: Concurrent Set operations on different ranges
        for (int taskId = 0; taskId < 3; taskId++)
        {
            int startIdx = taskId * blocksPerTask;
            int endIdx = startIdx + blocksPerTask;
            tasks.Add(Task.Run(() =>
            {
                for (int i = startIdx; i < endIdx && i < sharedChain.Length; i++)
                {
                    _cache.Set(sharedChain[i]);
                    if (i % 10 == 0)
                    {
                        Thread.Sleep(1); // Small delay to increase contention
                    }
                }
            }));
        }

        // Task 4: Concurrent GetHash queries
        tasks.Add(Task.Run(async () =>
        {
            await Task.Delay(50); // Let some blocks be added first
            for (int i = 0; i < 100; i++)
            {
                int blockIdx = Random.Shared.Next(100, Math.Min(1000, sharedChain.Length));
                int depth = Random.Shared.Next(1, Math.Min(blockIdx, 100));
                if (_cache.Contains(sharedChain[blockIdx].Hash!))
                {
                    _cache.GetHash(sharedChain[blockIdx], depth);
                }
                await Task.Delay(5);
            }
        }));

        // Task 5: Periodic pruning
        tasks.Add(Task.Run(async () =>
        {
            await Task.Delay(100); // Let cache populate first
            for (int i = 0; i < 5; i++)
            {
                long pruneBlock = (i + 1) * pruneInterval;
                if (pruneBlock < sharedChain.Length)
                {
                    _cache.PruneBefore(pruneBlock);
                }
                await Task.Delay(50);
            }
        }));

        // Task 6: Concurrent Prefetch operations
        tasks.Add(Task.Run(async () =>
        {
            await Task.Delay(75);
            for (int i = 0; i < 20; i++)
            {
                int blockIdx = Random.Shared.Next(256, Math.Min(1500, sharedChain.Length));
                if (_cache.Contains(sharedChain[blockIdx].Hash!))
                {
                    await _cache.Prefetch(sharedChain[blockIdx]);
                }
                await Task.Delay(10);
            }
        }));

        // Wait for all tasks
        await Task.WhenAll(tasks);

        // Assert - Verify cache integrity
        BlockhashCache.CacheStats stats = _cache.GetStats();
        stats.TotalBlocks.Should().BeGreaterThan(0, "cache should contain blocks after concurrent operations");

        // Verify some ancestor queries work correctly
        for (int i = sharedChain.Length - 100; i < sharedChain.Length; i++)
        {
            if (_cache.Contains(sharedChain[i].Hash!))
            {
                for (int depth = 1; depth < Math.Min(50, i); depth += 10)
                {
                    if (_cache.Contains(sharedChain[i - depth].Hash!))
                    {
                        Hash256? result = _cache.GetHash(sharedChain[i], depth);
                        result.Should().Be(sharedChain[i - depth].Hash!,
                            $"block {i} should correctly find ancestor at depth {depth} after concurrent operations");
                    }
                }
            }
        }
    }

    [Test]
    public void Stress_test_deep_ancestor_queries_across_segments()
    {
        // Arrange
        const int chainLength = 4000;
        const int pruneInterval = 128;
        const int maxQueryDepth = 1024; // Query across multiple 256-block segments

        BlockHeader[] chain = new BlockHeader[chainLength];
        chain[0] = Build.A.BlockHeader.WithNumber(0).TestObject;
        _cache.Set(chain[0]);
        _headerStore.Insert(chain[0]);

        // Build chain
        for (int i = 1; i < chainLength; i++)
        {
            chain[i] = Build.A.BlockHeader
                .WithNumber(i)
                .WithParent(chain[i - 1])
                .TestObject;
            _cache.Set(chain[i]);
            _headerStore.Insert(chain[i]);

            // Periodic pruning but keep a large window
            if (i > 0 && i % pruneInterval == 0 && i > 1500)
            {
                _cache.PruneBefore(i - 1200);
            }
        }

        // Act & Assert - Test deep ancestor queries
        int[] testDepths = { 1, 50, 100, 150, 200, 256, 300, 512, 768, 1000 };

        foreach (int depth in testDepths)
        {
            int queryBlockIdx = chainLength - 1;
            int ancestorIdx = queryBlockIdx - depth;

            if (ancestorIdx >= 0)
            {
                Hash256? result = _cache.GetHash(chain[queryBlockIdx], depth);

                if (_cache.Contains(chain[ancestorIdx].Hash!))
                {
                    result.Should().Be(chain[ancestorIdx].Hash!,
                        $"should find ancestor at depth {depth} (block {ancestorIdx}) from block {queryBlockIdx}");
                }
            }
        }

        // Test queries from multiple recent blocks at various depths
        for (int blockIdx = chainLength - 300; blockIdx < chainLength; blockIdx += 50)
        {
            for (int depth = 50; depth <= Math.Min(blockIdx, maxQueryDepth); depth += 100)
            {
                int ancestorIdx = blockIdx - depth;
                if (ancestorIdx >= 0 && _cache.Contains(chain[ancestorIdx].Hash!))
                {
                    Hash256? result = _cache.GetHash(chain[blockIdx], depth);
                    result.Should().Be(chain[ancestorIdx].Hash!,
                        $"block {blockIdx} should find ancestor {ancestorIdx} at depth {depth}");
                }
            }
        }

        // Verify segment chaining works correctly
        BlockhashCache.CacheStats stats = _cache.GetStats();
        stats.MaxSegmentDepth.Should().BeGreaterThan(3,
            "should have multiple chained segments for deep queries");

        // Test boundary cases around segment boundaries (multiples of 256)
        int[] segmentBoundaries = { 256, 512, 768, 1024, 1280 };
        foreach (int boundary in segmentBoundaries)
        {
            if (chainLength > boundary + 100)
            {
                int testBlock = boundary + 50;
                if (_cache.Contains(chain[testBlock].Hash!))
                {
                    // Query across segment boundary
                    Hash256? beforeBoundary = _cache.GetHash(chain[testBlock], 100);
                    if (_cache.Contains(chain[testBlock - 100].Hash!))
                    {
                        beforeBoundary.Should().Be(chain[testBlock - 100].Hash!,
                            $"should correctly query across segment boundary at {boundary}");
                    }
                }
            }
        }
    }
}
