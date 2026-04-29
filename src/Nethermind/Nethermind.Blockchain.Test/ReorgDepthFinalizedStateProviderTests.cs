// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
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
    public void FinalizedBlockNumber_FallsBackToBestKnownMinusMaxDepth_WhenFinalizedHashIsNull()
    {
        long bestKnownNumber = 1000;
        _blockTree.BestKnownNumber.Returns(bestKnownNumber);
        _blockTree.FinalizedHash.Returns((Hash256?)null);

        long result = _provider.FinalizedBlockNumber;

        result.Should().Be(bestKnownNumber - Reorganization.MaxDepth);
    }

    [Test]
    public void FinalizedBlockNumber_ReturnsHeaderNumber_WhenFinalizedHashAndHeaderAreKnown()
    {
        const long finalizedNumber = 950;
        Hash256 finalizedHash = TestItem.KeccakA;
        BlockHeader finalizedHeader = Build.A.BlockHeader.WithNumber(finalizedNumber).TestObject;

        _blockTree.FinalizedHash.Returns(finalizedHash);
        _blockTree.FindHeader(finalizedHash, BlockTreeLookupOptions.DoNotCreateLevelIfMissing).Returns(finalizedHeader);

        long result = _provider.FinalizedBlockNumber;

        result.Should().Be(finalizedNumber);
    }

    [Test]
    public void FinalizedBlockNumber_FallsBack_WhenFinalizedHeaderCannotBeFound()
    {
        const long bestKnownNumber = 1000;
        Hash256 finalizedHash = TestItem.KeccakA;

        _blockTree.BestKnownNumber.Returns(bestKnownNumber);
        _blockTree.FinalizedHash.Returns(finalizedHash);
        _blockTree.FindHeader(finalizedHash, BlockTreeLookupOptions.DoNotCreateLevelIfMissing).Returns((BlockHeader?)null);

        long result = _provider.FinalizedBlockNumber;

        result.Should().Be(bestKnownNumber - Reorganization.MaxDepth);
    }

    [Test]
    public void FinalizedBlockNumber_FallsBack_WhenFinalizedHashIsZero()
    {
        const long bestKnownNumber = 1000;

        _blockTree.BestKnownNumber.Returns(bestKnownNumber);
        _blockTree.FinalizedHash.Returns(Keccak.Zero);

        long result = _provider.FinalizedBlockNumber;

        result.Should().Be(bestKnownNumber - Reorganization.MaxDepth);
        _blockTree.DidNotReceive().FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>());
    }

    [Test]
    public void FinalizedBlockNumber_FallsBack_DoesNotGoNegative()
    {
        // Below Reorganization.MaxDepth (=64) the formula bestKnownNumber - 64 would be negative;
        // FinalizedBlockNumber must clamp to 0.
        _blockTree.BestKnownNumber.Returns(10);
        _blockTree.FinalizedHash.Returns((Hash256?)null);

        _provider.FinalizedBlockNumber.Should().Be(0);
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

    [TestCase(900, TestName = "Inside the finalized window")]
    [TestCase(1000 - 64, TestName = "Exactly at the boundary (bestKnown - MaxDepth)")]
    public void GetFinalizedStateRootAt_ReturnsStateRoot_WhenBlockIsAtOrBelowFinalizedBoundary(long blockNumber)
    {
        const long bestKnownNumber = 1000;
        Hash256 expectedStateRoot = TestItem.KeccakA;
        BlockHeader header = Build.A.BlockHeader.WithStateRoot(expectedStateRoot).TestObject;

        _blockTree.BestKnownNumber.Returns(bestKnownNumber);
        _blockTree.FindHeader(blockNumber, BlockTreeLookupOptions.RequireCanonical).Returns(header);

        Hash256? result = _provider.GetFinalizedStateRootAt(blockNumber);

        result.Should().Be(expectedStateRoot);
        _blockTree.Received(1).FindHeader(blockNumber, BlockTreeLookupOptions.RequireCanonical);
    }
}
