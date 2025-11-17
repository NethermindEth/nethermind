// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class BlockhashCacheTests
{
    [Test]
    public void GetHash_with_depth_zero_returns_block_hash()
    {
        (BlockTree tree, BlockhashCache cache) = BuildTest(1);
        Hash256? result = cache.GetHash(tree.Genesis!, 0);
        result.Should().Be(tree.Genesis!.Hash!);
    }

    [Test]
    public void GetHash_returns_correct_ancestor()
    {
        (BlockTree tree, BlockhashCache cache) = BuildTest(10);
        BlockHeader? head = tree.FindHeader(9, BlockTreeLookupOptions.None);

        // depth=1 should return block 8
        Hash256? result1 = cache.GetHash(head!, 1);
        BlockHeader? block8 = tree.FindHeader(8, BlockTreeLookupOptions.None);
        result1.Should().Be(block8!.Hash!);

        // depth=5 should return block 4
        Hash256? result5 = cache.GetHash(head!, 5);
        BlockHeader? block4 = tree.FindHeader(4, BlockTreeLookupOptions.None);
        result5.Should().Be(block4!.Hash!);

        // depth=9 should return block 0 (genesis)
        Hash256? result9 = cache.GetHash(head!, 9);
        result9.Should().Be(tree.Genesis!.Hash!);
    }

    [Test]
    public void GetHash_returns_null_for_missing_ancestor()
    {
        (BlockTree tree, BlockhashCache cache) = BuildTest(5);
        BlockHeader? head = tree.FindHeader(4, BlockTreeLookupOptions.None);
        Hash256? result = cache.GetHash(head!, 10);
        result.Should().BeNull();
    }

    [Test]
    public void GetHash_caches_loaded_blocks()
    {
        (BlockTree tree, BlockhashCache cache) = BuildTest(10);
        BlockHeader? head = tree.FindHeader(9, BlockTreeLookupOptions.None);
        cache.GetHash(head!, 5);

        cache.Contains(head!.Hash!).Should().BeTrue();
        BlockHeader? ancestor = tree.FindHeader(4, BlockTreeLookupOptions.None);
        cache.Contains(ancestor!.Hash!).Should().BeTrue();
    }

    [Test]
    public void GetHash_handles_max_depth_of_256()
    {
        (BlockTree tree, BlockhashCache cache) = BuildTest(300);
        BlockHeader? head = tree.FindHeader(299, BlockTreeLookupOptions.None);
        Hash256? result = cache.GetHash(head!, 256);

        BlockHeader? expected = tree.FindHeader(43, BlockTreeLookupOptions.None);
        result.Should().Be(expected!.Hash!);
    }

    [Test]
    public void GetHash_clamps_depth_beyond_256()
    {
        (BlockTree tree, BlockhashCache cache) = BuildTest(300);

        BlockHeader? head = tree.FindHeader(299, BlockTreeLookupOptions.None);
        Hash256? result300 = cache.GetHash(head!, 300);
        Hash256? result256 = cache.GetHash(head!, 256);

        result300.Should().Be(result256!);
        result256.Should().NotBeNull();
    }

    [Test]
    public void Contains_returns_true_for_cached_blocks()
    {
        (BlockTree tree, BlockhashCache cache) = BuildTest(10);

        BlockHeader? head = tree.FindHeader(9, BlockTreeLookupOptions.None);
        cache.GetHash(head!, 5);

        cache.Contains(head!.Hash!).Should().BeTrue();
    }

    [Test]
    public void Contains_returns_false_for_uncached_blocks()
    {
        (BlockTree _, BlockhashCache cache) = BuildTest(1);
        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;
        cache.Contains(header.Hash!).Should().BeFalse();
    }

    [Test]
    public void PruneBefore_removes_old_blocks()
    {
        (BlockTree tree, BlockhashCache cache) = BuildTest(100);

        BlockHeader? head = tree.FindHeader(99, BlockTreeLookupOptions.None);
        cache.GetHash(head!, 50);
        int removed = cache.PruneBefore(60);

        removed.Should().BeGreaterThan(0);
        BlockHeader? old = tree.FindHeader(40, BlockTreeLookupOptions.None);
        BlockHeader? kept = tree.FindHeader(60, BlockTreeLookupOptions.None);
        cache.Contains(old!.Hash!).Should().BeFalse();
        cache.Contains(kept!.Hash!).Should().BeTrue();
    }

    [Test]
    public void Clear_removes_all_cached_blocks()
    {
        (BlockTree tree, BlockhashCache cache) = BuildTest(10);

        BlockHeader? head = tree.FindHeader(9, BlockTreeLookupOptions.None);
        cache.GetHash(head!, 5);
        cache.Clear();

        cache.Contains(head!.Hash!).Should().BeFalse();
    }

    [Test]
    public async Task Prefetch_loads_ancestors_in_background()
    {
        (BlockTree tree, BlockhashCache cache) = BuildTest(100);

        BlockHeader? head = tree.FindHeader(99, BlockTreeLookupOptions.None);
        BlockHeader? block1 = tree.FindHeader(1, BlockTreeLookupOptions.None);
        await cache.Prefetch(head!);

        cache.Contains(head!.Hash!).Should().BeTrue();
        cache.Contains(block1!.Hash!).Should().BeTrue();
    }

    [Test]
    public void Sequential_queries_work_correctly()
    {
        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTreeBuilder builder = Build.A.BlockTree(genesis).WithoutSettingHead.OfChainLength(512);
        BlockTree tree = builder.TestObject;
        BlockhashCache cache = new(builder.HeaderStore, LimboLogs.Instance);

        for (int blockNum = 256; blockNum < 512; blockNum += 50)
        {
            BlockHeader? block = tree.FindHeader(blockNum, BlockTreeLookupOptions.None);

            for (int depth = 1; depth <= 100; depth += 25)
            {
                Hash256? result = cache.GetHash(block!, depth);
                BlockHeader? expected = tree.FindHeader(blockNum - depth, BlockTreeLookupOptions.None);
                result.Should().Be(expected!.Hash!, $"block {blockNum} depth {depth}");
            }
        }
    }

    [Test]
    public void Periodic_pruning_maintains_cache_size()
    {
        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTreeBuilder builder = Build.A.BlockTree(genesis).WithoutSettingHead.OfChainLength(1000);
        BlockTree tree = builder.TestObject;
        BlockhashCache cache = new(builder.HeaderStore, LimboLogs.Instance);

        for (int i = 100; i < 1000; i += 100)
        {
            BlockHeader? block = tree.FindHeader(i, BlockTreeLookupOptions.None);
            cache.GetHash(block!, 50);

            if (i > 500)
            {
                int pruned = cache.PruneBefore(i - 400);
                pruned.Should().BeGreaterThan(0);
            }
        }
    }

    private static (BlockTree, BlockhashCache) BuildTest(int chainLength)
    {
        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTreeBuilder builder = Build.A.BlockTree(genesis).WithoutSettingHead.OfChainLength(chainLength);
        BlockTree tree = builder.TestObject;
        BlockhashCache cache = new(builder.HeaderStore, LimboLogs.Instance);
        return (tree, cache);
    }
}
