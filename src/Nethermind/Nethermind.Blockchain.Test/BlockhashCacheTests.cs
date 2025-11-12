// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[TestFixture]
public class BlockhashCacheTests
{
    private IHeaderStore _headerStore = null!;
    private BlockhashCache _cache = null!;

    [SetUp]
    public void Setup()
    {
        _headerStore = new HeaderStore(new MemDb(), new MemDb());
        _cache = new BlockhashCache(_headerStore);
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
    public void Prefetch_loads_ancestors_in_background()
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
        _cache.Prefetch(headers[99], 50);
        System.Threading.Thread.Sleep(100); // Give background task time

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
}
