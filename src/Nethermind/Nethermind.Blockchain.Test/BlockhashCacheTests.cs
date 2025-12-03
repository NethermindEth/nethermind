// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
[Parallelizable(ParallelScope.All)]
public class BlockhashCacheTests
{
    private const int FlatCacheItemLength = BlockhashCache.MaxDepth + 1;

    [Test]
    public void GetHash_with_depth_zero_returns_block_hash()
    {
        (BlockTree tree, BlockhashCache cache) = BuildTest(1);
        Hash256? result = cache.GetHash(tree.Genesis!, 0);
        result.Should().Be(tree.Genesis!.Hash!);
        cache.GetStats().Should().Be(new BlockhashCache.Stats(0, 0, 0));
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

        cache.GetStats().Should().Be(new BlockhashCache.Stats(10, 1, 0));
    }

    [Test]
    public void GetHash_returns_null_for_missing_ancestor()
    {
        (BlockTree tree, BlockhashCache cache) = BuildTest(5);
        BlockHeader? head = tree.FindHeader(4, BlockTreeLookupOptions.None);
        Hash256? result = cache.GetHash(head!, 10);
        result.Should().BeNull();
        cache.GetStats().Should().Be(new BlockhashCache.Stats(5, 1, 1));
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
        cache.GetStats().Should().Be(new BlockhashCache.Stats(6, 1, 0));
    }

    [Test]
    public void GetHash_handles_max_depth_of_256()
    {
        (BlockTree tree, BlockhashCache cache) = BuildTest(300);
        BlockHeader? head = tree.FindHeader(299, BlockTreeLookupOptions.None);
        Hash256? result = cache.GetHash(head!, 256);

        BlockHeader? expected = tree.FindHeader(43, BlockTreeLookupOptions.None);
        result.Should().Be(expected!.Hash!);
        cache.GetStats().Should().Be(new BlockhashCache.Stats(FlatCacheItemLength, 1, 1));
    }

    [Test]
    public void GetHash_does_not_go_beyond_depth_256()
    {
        (BlockTree tree, BlockhashCache cache) = BuildTest(300);

        BlockHeader? head = tree.FindHeader(299, BlockTreeLookupOptions.None);
        Hash256? result300 = cache.GetHash(head!, 300);
        Hash256? result256 = cache.GetHash(head!, 256);

        result300.Should().BeNull();
        result256.Should().NotBeNull();
        cache.GetStats().Should().Be(new BlockhashCache.Stats(FlatCacheItemLength, 1, 1));
    }

    [Test]
    public void Contains_returns_true_for_cached_blocks()
    {
        (BlockTree tree, BlockhashCache cache) = BuildTest(10);

        BlockHeader? head = tree.FindHeader(9, BlockTreeLookupOptions.None);
        cache.GetHash(head!, 5);

        cache.Contains(head!.Hash!).Should().BeTrue();
        cache.GetStats().Should().Be(new BlockhashCache.Stats(6, 1, 0));
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
        cache.GetStats().Should().Be(new BlockhashCache.Stats(51, 1, 0));
        int removed = cache.PruneBefore(60);

        removed.Should().BeGreaterThan(0);
        BlockHeader? old = tree.FindHeader(40, BlockTreeLookupOptions.None);
        BlockHeader? kept = tree.FindHeader(60, BlockTreeLookupOptions.None);
        cache.Contains(old!.Hash!).Should().BeFalse();
        cache.Contains(kept!.Hash!).Should().BeTrue();
        cache.GetStats().Should().Be(new BlockhashCache.Stats(40, 1, 0));
    }

    [Test]
    public void Clear_removes_all_cached_blocks()
    {
        (BlockTree tree, BlockhashCache cache) = BuildTest(10);

        BlockHeader? head = tree.FindHeader(9, BlockTreeLookupOptions.None);
        cache.GetHash(head!, 5);
        cache.Clear();

        cache.Contains(head!.Hash!).Should().BeFalse();
        cache.GetStats().Should().Be(new BlockhashCache.Stats(0, 0, 0));
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
        cache.GetStats().Should().Be(new BlockhashCache.Stats(100, 1, 1));
    }

    [Test]
    public async Task Concurrent_Prefetch_loads_correctly()
    {
        (BlockTree tree, BlockhashCache cache) = BuildTest(100);

        BlockHeader head = tree.FindHeader(99, BlockTreeLookupOptions.None)!;
        BlockHeader block90 = tree.FindHeader(90, BlockTreeLookupOptions.None)!;
        BlockHeader block1 = tree.FindHeader(1, BlockTreeLookupOptions.None)!;
        Task prefetch99 = cache.Prefetch(head);
        Task prefetch90 = cache.Prefetch(block90);
        await Task.WhenAll(prefetch99, prefetch90);

        cache.GetHash(head, 98).Should().Be(block1.Hash!);
        cache.GetHash(block90, 89).Should().Be(block1.Hash!);
        cache.GetStats().Should().Be(new BlockhashCache.Stats(100, 1, 2));
    }

    [Test]
    public void Sequential_queries_work_correctly()
    {
        (BlockTree tree, BlockhashCache cache) = BuildTest(512);

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
    public async Task Periodic_pruning_maintains_cache_size()
    {
        (BlockTree tree, BlockhashCache cache) = BuildTest(1000);

        for (int i = 100; i < 1000; i += 100)
        {
            BlockHeader block = tree.FindHeader(i, BlockTreeLookupOptions.None)!;
            await cache.Prefetch(block);

            if (i > 500)
            {
                int pruned = cache.PruneBefore(i - 400);
                pruned.Should().BeGreaterThan(0);
                cache.GetStats().Should().Be(new BlockhashCache.Stats(401, 1, 5));
            }
        }
    }

    [Test]
    public void Can_stitch_block_ranges()
    {
        (BlockTree tree, BlockhashCache cache) = BuildTest(1000);

        for (int i = 100; i <= 500; i += 100)
        {
            BlockHeader block = tree.FindHeader(i, BlockTreeLookupOptions.None)!;
            cache.GetHash(block, 50);
        }

        cache.GetStats().Should().Be(new BlockhashCache.Stats(255, 5, 0));

        for (int i = 100; i <= 500; i += 100)
        {
            BlockHeader block = tree.FindHeader(i, BlockTreeLookupOptions.None)!;
            cache.GetHash(block, BlockhashCache.MaxDepth);
        }

        cache.GetStats().Should().Be(new BlockhashCache.Stats(501, 1, 5));
    }

    [Test]
    public async Task Can_support_multiple_forks()
    {
        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTreeBuilder builder = Build.A.BlockTree(genesis).WithoutSettingHead
            .OfChainLengthWithSharedSplits(out Block head1, 1000)
            .OfChainLengthWithSharedSplits(out Block head2, 200, 1, 800)
            .OfChainLengthWithSharedSplits(out Block head3, 200, 2, 800);

        BlockhashCache cache = new(builder.HeaderStore, LimboLogs.Instance);

        await cache.Prefetch(head1.Header);
        await cache.Prefetch(head2.Header);
        await cache.Prefetch(head3.Header);
        cache.GetStats().Should().Be(new BlockhashCache.Stats(255 + 200 + 200, 3, 3));
        cache.PruneBefore(800);
        cache.GetStats().Should().Be(new BlockhashCache.Stats(200 + 199 + 199, 3, 3));
    }

    [Test]
    public async Task Can_prune_old_forks()
    {
        const int depth = BlockhashCache.MaxDepth * 5 + 1;
        (BlockTree tree, BlockhashCache cache) = BuildTest(depth);
        for (int i = BlockhashCache.MaxDepth; i < depth; i += BlockhashCache.MaxDepth)
        {
            cache.GetHash(tree.FindHeader(i, BlockTreeLookupOptions.RequireCanonical)!, BlockhashCache.MaxDepth);
        }

        cache.GetStats().Should().Be(new BlockhashCache.Stats(depth, 1, 5));
        await cache.Prefetch(tree.FindHeader(depth - 1, BlockTreeLookupOptions.RequireCanonical)!);
        await Task.Delay(100);
        cache.GetStats().Should().Be(new BlockhashCache.Stats(BlockhashCache.MaxDepth * 2 + 1, 1, 3));
    }

    [Test]
    public async Task Prefetch_prunes()
    {
        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTreeBuilder builder = Build.A.BlockTree(genesis).WithoutSettingHead
            .OfChainLengthWithSharedSplits(out Block head1, 1000)
            .OfChainLengthWithSharedSplits(out Block head2, 300, 1, 300)
            .OfChainLengthWithSharedSplits(out Block head3, 300, 2, 300);

        BlockhashCache cache = new(builder.HeaderStore, LimboLogs.Instance);

        await cache.Prefetch(head1.Header);
        await cache.Prefetch(head2.Header);
        await cache.Prefetch(head3.Header);
        cache.GetStats().Should().Be(new BlockhashCache.Stats(FlatCacheItemLength + FlatCacheItemLength + FlatCacheItemLength, 3, 3));
        cache.PruneBefore(800);
        cache.GetStats().Should().Be(new BlockhashCache.Stats(200, 1, 1));
    }

    [TestCase(300)]
    [TestCase(50)]
    public async Task Prefetch_reuses_parent_data(int chainDepth)
    {
        (BlockTree tree, BlockhashCache cache) = BuildTest(chainDepth);
        BlockHeader head = tree.FindHeader(chainDepth - 1, BlockTreeLookupOptions.None)!;
        BlockHeader prev = tree.FindHeader(chainDepth - 2, BlockTreeLookupOptions.None)!;

        Hash256[] prevHashes = (await cache.Prefetch(prev, CancellationToken.None))!;
        Hash256[] headHashes = (await cache.Prefetch(head, CancellationToken.None))!;
        cache.GetStats().Should().Be(new BlockhashCache.Stats(Math.Min(chainDepth - 1, FlatCacheItemLength), 1, 2));
        Assert.Multiple(() =>
            {
                int compareLength = headHashes.Length - 1;
                Assert.That(prevHashes.AsSpan(0, compareLength)
                    .SequenceEqual(headHashes.AsSpan(1, compareLength)));
                Assert.That(headHashes[0], Is.EqualTo(head.Hash));
            }
        );
    }

    [Test]
    public async Task DoesNot_cache_cancelled_searches()
    {
        SlowHeaderStore headerStore = new(new HeaderStore(new MemDb(), new MemDb()));
        (BlockTree tree, BlockhashCache cache) = BuildTest(260, headerStore);

        BlockHeader head = tree.FindHeader(FlatCacheItemLength, BlockTreeLookupOptions.None)!;
        CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(20));
        await cache.Prefetch(head, cts.Token);
        cache.GetStats().Should().Be(new BlockhashCache.Stats(0, 0, 0));
    }

    [Test]
    public async Task Prefetch_with_null_hash_does_not_cache()
    {
        (BlockTree tree, BlockhashCache cache) = BuildTest(10);

        BlockHeader parent = tree.FindHeader(9, BlockTreeLookupOptions.None)!;
        await cache.Prefetch(parent);
        int cacheCountBefore = cache.GetStats().FlatCache;

        BlockHeader production = Build.A.BlockHeader.WithParent(parent).WithNumber(10).TestObject;
        production.Hash = null;
        Hash256[]? hashes = await cache.Prefetch(production);

        hashes![0].Should().BeNull();
        hashes[1].Should().Be(parent.Hash!);
        cache.GetStats().FlatCache.Should().Be(cacheCountBefore);
    }

    private static (BlockTree, BlockhashCache) BuildTest(int chainLength, IHeaderStore? headerStore = null)
    {
        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTreeBuilder builder = Build.A.BlockTree(genesis);
        if (headerStore is not null)
        {
            builder.WithHeaderStore(headerStore);
        }

        builder.WithoutSettingHead.OfChainLength(chainLength);
        BlockTree tree = builder.TestObject;
        BlockhashCache cache = new(builder.HeaderStore, LimboLogs.Instance);
        return (tree, cache);
    }
}
