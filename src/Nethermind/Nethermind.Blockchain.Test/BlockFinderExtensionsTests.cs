// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
        Assert.That(parent.TotalDifficulty, Is.Null); // just to avoid the testing rig change without this test being updated

        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.FindHeader(child.ParentHash!, BlockTreeLookupOptions.TotalDifficultyNotNeeded, blockNumber: child.Number - 1).Returns(parent);
        blockFinder.FindHeader(child.ParentHash!, BlockTreeLookupOptions.None, blockNumber: child.Number - 1).Returns(parentWithTotalDiff);

        Assert.That(blockFinder.FindParentHeader(child, BlockTreeLookupOptions.TotalDifficultyNotNeeded), Is.EqualTo(parent));
        Assert.That(blockFinder.FindParentHeader(child, BlockTreeLookupOptions.None)!.TotalDifficulty, Is.EqualTo((UInt256?)UInt256.One));
    }

    [TestCase(BlockParameterType.Latest, "latest")]
    [TestCase(BlockParameterType.Earliest, "earliest")]
    [TestCase(BlockParameterType.Pending, "pending")]
    [TestCase(BlockParameterType.Finalized, "finalized")]
    [TestCase(BlockParameterType.Safe, "safe")]
    [MaxTime(Timeout.MaxTestTime)]
    public void BlockParameter_ToString_ReturnsLowercaseTypeName(BlockParameterType type, string expected) =>
        Assert.That(new BlockParameter(type).ToString(), Is.EqualTo(expected));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void BlockParameter_ToString_ReturnsBlockNumber() =>
        Assert.That(new BlockParameter(12345ul).ToString(), Is.EqualTo("12345"));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void BlockParameter_ToString_ReturnsBlockHash()
    {
        Hash256 hash = TestItem.KeccakA;
        Assert.That(new BlockParameter(hash).ToString(), Is.EqualTo(hash.ToString()));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void SearchForBlock_WhenBlockIsPruned_IncludesBlockNumberInError()
    {
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        BlockHeader head = Build.A.BlockHeader.WithNumber(1000).TestObject;
        Block headBlock = Build.A.Block.WithHeader(head).TestObject;
        blockFinder.Head.Returns(headBlock);
        blockFinder.GetLowestBlock().Returns(100ul);

        // Mock the underlying method that will be called
        blockFinder.FindBlock(50ul, BlockTreeLookupOptions.None).Returns((Block?)null);

        BlockParameter blockParameter = new(50ul);
        SearchResult<Block> result = blockFinder.SearchForBlock(blockParameter);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.PrunedHistoryUnavailable));
        Assert.That(result.Error, Does.Contain("50"));
        Assert.That(result.Error, Does.Contain("Pruned history unavailable"));
    }
}
