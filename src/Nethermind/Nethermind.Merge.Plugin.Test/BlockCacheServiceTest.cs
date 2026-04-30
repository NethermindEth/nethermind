// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Merge.Plugin.Handlers;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

[TestFixture]
public class BlockCacheServiceTest
{
    [Test]
    public void prunes_highest_unprotected_block()
    {
        BlockCacheService blockCacheService = new(2);
        Block block1 = Build.A.Block.WithNumber(1).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).TestObject;
        Block block3 = Build.A.Block.WithNumber(3).TestObject;
        Hash256 block1Hash = block1.GetOrCalculateHash();
        Hash256 block2Hash = block2.GetOrCalculateHash();
        Hash256 block3Hash = block3.GetOrCalculateHash();

        blockCacheService.TryAddBlock(block1).Should().BeTrue();
        blockCacheService.TryAddBlock(block2).Should().BeTrue();
        blockCacheService.TryAddBlock(block3).Should().BeTrue();

        blockCacheService.BlockCache.Should().HaveCount(2);
        blockCacheService.BlockCache.ContainsKey(block1Hash).Should().BeTrue();
        blockCacheService.BlockCache.ContainsKey(block2Hash).Should().BeTrue();
        blockCacheService.BlockCache.ContainsKey(block3Hash).Should().BeFalse();
    }

    [Test]
    public void preserves_head_and_finalized_blocks_when_pruning()
    {
        BlockCacheService blockCacheService = new(2);
        Block finalizedBlock = Build.A.Block.WithNumber(1).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).TestObject;
        Block headBlock = Build.A.Block.WithNumber(3).TestObject;
        Hash256 finalizedHash = finalizedBlock.GetOrCalculateHash();
        Hash256 block2Hash = block2.GetOrCalculateHash();
        Hash256 headHash = headBlock.GetOrCalculateHash();
        blockCacheService.FinalizedHash = finalizedHash;
        blockCacheService.HeadBlockHash = headHash;

        blockCacheService.TryAddBlock(finalizedBlock).Should().BeTrue();
        blockCacheService.TryAddBlock(block2).Should().BeTrue();
        blockCacheService.TryAddBlock(headBlock).Should().BeTrue();

        blockCacheService.BlockCache.Should().HaveCount(2);
        blockCacheService.BlockCache.ContainsKey(finalizedHash).Should().BeTrue();
        blockCacheService.BlockCache.ContainsKey(block2Hash).Should().BeFalse();
        blockCacheService.BlockCache.ContainsKey(headHash).Should().BeTrue();
    }

    [Test]
    public void preserves_protected_blocks_when_all_candidates_are_protected()
    {
        BlockCacheService blockCacheService = new(1);
        Block finalizedBlock = Build.A.Block.WithNumber(1).TestObject;
        Block headBlock = Build.A.Block.WithNumber(2).TestObject;
        Hash256 finalizedHash = finalizedBlock.GetOrCalculateHash();
        Hash256 headHash = headBlock.GetOrCalculateHash();
        blockCacheService.FinalizedHash = finalizedHash;
        blockCacheService.HeadBlockHash = headHash;

        blockCacheService.TryAddBlock(finalizedBlock).Should().BeTrue();
        blockCacheService.TryAddBlock(headBlock).Should().BeTrue();

        blockCacheService.BlockCache.Should().HaveCount(2);
        blockCacheService.BlockCache.ContainsKey(finalizedHash).Should().BeTrue();
        blockCacheService.BlockCache.ContainsKey(headHash).Should().BeTrue();
    }
}
