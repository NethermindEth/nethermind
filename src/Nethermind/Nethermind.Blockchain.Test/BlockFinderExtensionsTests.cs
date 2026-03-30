// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[Parallelizable(ParallelScope.All)]
public class BlockFinderExtensionsTests
{
    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_upgrade_maybe_parent()
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        BlockHeader parentWithTotalDiff = Build.A.BlockHeader.WithTotalDifficulty(1).TestObject;
        BlockHeader child = Build.A.BlockHeader.WithParent(parent).TestObject;
        parent.TotalDifficulty.Should().BeNull(); // just to avoid the testing rig change without this test being updated

        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.FindHeader(child.ParentHash!, BlockTreeLookupOptions.TotalDifficultyNotNeeded, blockNumber: child.Number - 1).Returns(parent);
        blockFinder.FindHeader(child.ParentHash!, BlockTreeLookupOptions.None, blockNumber: child.Number - 1).Returns(parentWithTotalDiff);

        blockFinder.FindParentHeader(child, BlockTreeLookupOptions.TotalDifficultyNotNeeded).Should().Be(parent);
        blockFinder.FindParentHeader(child, BlockTreeLookupOptions.None)!.TotalDifficulty.Should().Be((UInt256?)UInt256.One);
    }

    [TestCase(BlockParameterType.Latest, "latest")]
    [TestCase(BlockParameterType.Earliest, "earliest")]
    [TestCase(BlockParameterType.Pending, "pending")]
    [TestCase(BlockParameterType.Finalized, "finalized")]
    [TestCase(BlockParameterType.Safe, "safe")]
    [MaxTime(Timeout.MaxTestTime)]
    public void BlockParameter_ToString_ReturnsLowercaseTypeName(BlockParameterType type, string expected)
    {
        new BlockParameter(type).ToString().Should().Be(expected);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void BlockParameter_ToString_ReturnsBlockNumber()
    {
        new BlockParameter(12345L).ToString().Should().Be("12345");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void BlockParameter_ToString_ReturnsBlockHash()
    {
        var hash = TestItem.KeccakA;
        new BlockParameter(hash).ToString().Should().Be(hash.ToString());
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void SearchForBlock_WhenBlockIsPruned_IncludesBlockNumberInError()
    {
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        BlockHeader head = Build.A.BlockHeader.WithNumber(1000).TestObject;
        Block headBlock = Build.A.Block.WithHeader(head).TestObject;
        blockFinder.Head.Returns(headBlock);
        blockFinder.GetLowestBlock().Returns(100);

        // Mock the underlying method that will be called
        blockFinder.FindBlock(50, BlockTreeLookupOptions.None).Returns((Block?)null);

        BlockParameter blockParameter = new BlockParameter(50);
        SearchResult<Block> result = blockFinder.SearchForBlock(blockParameter);

        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.PrunedHistoryUnavailable);
        result.Error.Should().Contain("50");
        result.Error.Should().Contain("pruned history unavailable");
    }
}
