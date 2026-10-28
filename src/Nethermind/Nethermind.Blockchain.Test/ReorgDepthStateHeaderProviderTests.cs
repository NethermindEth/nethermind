// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class ReorgDepthStateHeaderProviderTests
{
    private IBlockTree _blockTree = null!;
    private ReorgDepthStateHeaderProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _blockTree = Substitute.For<IBlockTree>();
        _provider = new ReorgDepthStateHeaderProvider(_blockTree);
    }

    [Test]
    public void FinalizedBlockNumber_ReturnsCorrectValue()
    {
        // Arrange
        ulong bestKnownNumber = 1000;
        _blockTree.BestKnownNumber.Returns(bestKnownNumber);

        // Act
        ulong result = _provider.FinalizedBlockNumber;

        // Assert
        Assert.That(result, Is.EqualTo(bestKnownNumber - Reorganization.MaxDepth));
    }

    [Test]
    public void GetFinalizedHeader_ReturnsNull_WhenBlockNumberExceedsFinalizedBlock()
    {
        // Arrange
        ulong bestKnownNumber = 100;
        ulong blockNumber = 100;
        _blockTree.BestKnownNumber.Returns(bestKnownNumber);

        // Act
        BlockHeader? result = _provider.GetFinalizedHeader(blockNumber);

        // Assert
        Assert.That(result, Is.Null);
        _blockTree.DidNotReceive().FindHeader(Arg.Any<ulong>(), Arg.Any<BlockTreeLookupOptions>());
    }

    [Test]
    public void GetFinalizedHeader_ReturnsHeader_WhenBlockNumberIsFinalized()
    {
        // Arrange
        ulong bestKnownNumber = 1000;
        ulong blockNumber = 900;
        Hash256 expectedStateRoot = TestItem.KeccakA;
        BlockHeader header = Build.A.BlockHeader.WithStateRoot(expectedStateRoot).TestObject;

        _blockTree.BestKnownNumber.Returns(bestKnownNumber);
        _blockTree.FindHeader(blockNumber, BlockTreeLookupOptions.RequireCanonical).Returns(header);

        // Act
        BlockHeader? result = _provider.GetFinalizedHeader(blockNumber);

        // Assert
        Assert.That(result, Is.SameAs(header));
        _blockTree.Received(1).FindHeader(blockNumber, BlockTreeLookupOptions.RequireCanonical);
    }

    [Test]
    public void GetFinalizedHeader_AtBoundary_ReturnsHeader()
    {
        // Arrange
        ulong bestKnownNumber = 1000;
        ulong blockNumber = 1000 - Reorganization.MaxDepth;
        BlockHeader header = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;

        _blockTree.BestKnownNumber.Returns(bestKnownNumber);
        _blockTree.FindHeader(blockNumber, BlockTreeLookupOptions.RequireCanonical).Returns(header);

        // Act
        BlockHeader? result = _provider.GetFinalizedHeader(blockNumber);

        // Assert
        Assert.That(result, Is.SameAs(header));
        _blockTree.Received(1).FindHeader(blockNumber, BlockTreeLookupOptions.RequireCanonical);
    }

    [Test]
    public void FindParentHeader_ReturnsNull_WhenParentHashIsNull()
    {
        BlockHeader target = Build.A.BlockHeader.TestObject;

        Assert.That(_provider.FindParentHeader(target), Is.Null);
        _blockTree.DidNotReceive().FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>());
    }

    [Test]
    public void FindParentHeader_ResolvesByParentHashAndHeight_WithoutTotalDifficultyOrLevelCreation()
    {
        BlockHeader parent = Build.A.BlockHeader.WithNumber(9).TestObject;
        BlockHeader target = Build.A.BlockHeader.WithNumber(10).WithParentHash(parent.Hash!).TestObject;
        const BlockTreeLookupOptions readOnlyLookup = BlockTreeLookupOptions.TotalDifficultyNotNeeded | BlockTreeLookupOptions.DoNotCreateLevelIfMissing;

        _blockTree.FindHeader(target.ParentHash!, readOnlyLookup, target.Number - 1).Returns(parent);

        BlockHeader? result = _provider.FindParentHeader(target);

        Assert.That(result, Is.SameAs(parent));
        _blockTree.Received(1).FindHeader(target.ParentHash!, readOnlyLookup, target.Number - 1);
    }
}
