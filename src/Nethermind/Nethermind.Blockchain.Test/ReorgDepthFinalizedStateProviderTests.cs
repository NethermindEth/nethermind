// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[TestFixture]
[Parallelizable(ParallelScope.Self)]
public class ReorgDepthFinalizedStateProviderTests
{
    private IBlockTree _blockTree = null!;
    private ReorgDepthFinalizedStateProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _blockTree = Substitute.For<IBlockTree>();
        _provider = new ReorgDepthFinalizedStateProvider(_blockTree);
    }

    [Test]
    public void FinalizedBlockNumber_ReturnsCorrectValue()
    {
        // Arrange
        long bestKnownNumber = 1000;
        _blockTree.BestKnownNumber.Returns(bestKnownNumber);

        // Act
        long result = _provider.FinalizedBlockNumber;

        // Assert
        result.Should().Be(bestKnownNumber - Reorganization.MaxDepth);
    }

    [Test]
    public void GetFinalizedStateRootAt_ReturnsNull_WhenBlockNumberExceedsFinalizedBlock()
    {
        // Arrange
        long bestKnownNumber = 100;
        long blockNumber = 100;
        _blockTree.BestKnownNumber.Returns(bestKnownNumber);

        // Act
        Hash256? result = _provider.GetFinalizedStateRootAt(blockNumber);

        // Assert
        result.Should().BeNull();
        _blockTree.DidNotReceive().FindHeader(Arg.Any<long>(), Arg.Any<BlockTreeLookupOptions>());
    }

    [Test]
    public void GetFinalizedStateRootAt_ReturnsStateRoot_WhenBlockNumberIsFinalized()
    {
        // Arrange
        long bestKnownNumber = 1000;
        long blockNumber = 900;
        Hash256 expectedStateRoot = TestItem.KeccakA;
        BlockHeader header = Build.A.BlockHeader.WithStateRoot(expectedStateRoot).TestObject;

        _blockTree.BestKnownNumber.Returns(bestKnownNumber);
        _blockTree.FindHeader(blockNumber, BlockTreeLookupOptions.RequireCanonical).Returns(header);

        // Act
        Hash256? result = _provider.GetFinalizedStateRootAt(blockNumber);

        // Assert
        result.Should().Be(expectedStateRoot);
        _blockTree.Received(1).FindHeader(blockNumber, BlockTreeLookupOptions.RequireCanonical);
    }

    [Test]
    public void GetFinalizedStateRootAt_AtBoundary_ReturnsStateRoot()
    {
        // Arrange
        long bestKnownNumber = 1000;
        long blockNumber = bestKnownNumber - Reorganization.MaxDepth; // Exactly at the boundary
        Hash256 expectedStateRoot = TestItem.KeccakD;
        BlockHeader header = Build.A.BlockHeader.WithStateRoot(expectedStateRoot).TestObject;

        _blockTree.BestKnownNumber.Returns(bestKnownNumber);
        _blockTree.FindHeader(blockNumber, BlockTreeLookupOptions.RequireCanonical).Returns(header);

        // Act
        Hash256? result = _provider.GetFinalizedStateRootAt(blockNumber);

        // Assert
        result.Should().Be(expectedStateRoot);
        _blockTree.Received(1).FindHeader(blockNumber, BlockTreeLookupOptions.RequireCanonical);
    }
}
