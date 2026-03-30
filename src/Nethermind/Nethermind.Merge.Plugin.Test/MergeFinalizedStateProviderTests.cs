// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

[TestFixture]
[Parallelizable(ParallelScope.Self)]
public class MergeFinalizedStateProviderTests
{
    private IPoSSwitcher _poSSwitcher = null!;
    private IBlockTree _blockTree = null!;
    private IFinalizedStateProvider _baseFinalizedStateProvider = null!;
    private MergeFinalizedStateProvider _provider = null!;
    private IBlockCacheService _blockCacheService;

    [SetUp]
    public void Setup()
    {
        _poSSwitcher = Substitute.For<IPoSSwitcher>();
        _blockTree = Substitute.For<IBlockTree>();
        _baseFinalizedStateProvider = Substitute.For<IFinalizedStateProvider>();
        _blockCacheService = Substitute.For<IBlockCacheService>();
        _provider = new MergeFinalizedStateProvider(_poSSwitcher, _blockCacheService, _blockTree, _baseFinalizedStateProvider);
    }

    [Test]
    public void FinalizedBlockNumber_BeforeTransition_DelegatesToBaseProvider()
    {
        // Arrange
        long expectedBlockNumber = 100;
        _poSSwitcher.TransitionFinished.Returns(false);
        _baseFinalizedStateProvider.FinalizedBlockNumber.Returns(expectedBlockNumber);

        // Act
        long result = _provider.FinalizedBlockNumber;

        // Assert
        result.Should().Be(expectedBlockNumber);
        _ = _baseFinalizedStateProvider.Received(1).FinalizedBlockNumber;
        _blockTree.DidNotReceive().FindHeader(Arg.Any<BlockParameter>());
    }

    [Test]
    public void FinalizedBlockNumber_AfterTransition_WithBlockTreeFinalizedHash_ReturnsHeaderNumber()
    {
        // Arrange
        long expectedBlockNumber = 200;
        Hash256 finalizedHash = TestItem.KeccakA;
        BlockHeader finalizedHeader = Build.A.BlockHeader.WithNumber(expectedBlockNumber).WithHash(finalizedHash).TestObject;
        _poSSwitcher.TransitionFinished.Returns(true);
        _blockTree.FinalizedHash.Returns(finalizedHash);
        _blockTree.FindHeader(finalizedHash, BlockTreeLookupOptions.None).Returns(finalizedHeader);

        // Act
        long result = _provider.FinalizedBlockNumber;

        // Assert
        result.Should().Be(expectedBlockNumber);
        _blockTree.Received(1).FindHeader(finalizedHash, BlockTreeLookupOptions.None);
    }

    [Test]
    public void FinalizedBlockNumber_AfterTransition_WithBlockCacheFinalizedHash_ReturnsHeaderNumber()
    {
        // Arrange
        long expectedBlockNumber = 250;
        Hash256 finalizedHash = TestItem.KeccakB;
        BlockHeader finalizedHeader = Build.A.BlockHeader.WithNumber(expectedBlockNumber).WithHash(finalizedHash).TestObject;
        _poSSwitcher.TransitionFinished.Returns(true);
        _blockTree.FinalizedHash.Returns((Hash256?)null);
        _blockCacheService.FinalizedHash.Returns(finalizedHash);
        _blockTree.FindHeader(finalizedHash).Returns(finalizedHeader);

        // Act
        long result = _provider.FinalizedBlockNumber;

        // Assert
        result.Should().Be(expectedBlockNumber);
        _blockTree.Received(1).FindHeader(finalizedHash);
    }

    [Test]
    public void FinalizedBlockNumber_AfterTransition_BlockCacheHasHigherNumber_ReturnsBlockCacheNumber()
    {
        // Arrange
        long blockTreeBlockNumber = 200;
        long blockCacheBlockNumber = 250;
        Hash256 blockTreeHash = TestItem.KeccakA;
        Hash256 blockCacheHash = TestItem.KeccakB;
        BlockHeader blockTreeHeader = Build.A.BlockHeader.WithNumber(blockTreeBlockNumber).WithHash(blockTreeHash).TestObject;
        BlockHeader blockCacheHeader = Build.A.BlockHeader.WithNumber(blockCacheBlockNumber).WithHash(blockCacheHash).TestObject;

        _poSSwitcher.TransitionFinished.Returns(true);
        _blockTree.FinalizedHash.Returns(blockTreeHash);
        _blockTree.FindHeader(blockTreeHash, BlockTreeLookupOptions.None).Returns(blockTreeHeader);
        _blockCacheService.FinalizedHash.Returns(blockCacheHash);
        _blockTree.FindHeader(blockCacheHash).Returns(blockCacheHeader);

        // Act
        long result = _provider.FinalizedBlockNumber;

        // Assert
        result.Should().Be(blockCacheBlockNumber);
    }

    [Test]
    public void FinalizedBlockNumber_AfterTransition_BlockTreeHasHigherNumber_ReturnsBlockTreeNumber()
    {
        // Arrange
        long blockTreeBlockNumber = 300;
        long blockCacheBlockNumber = 250;
        Hash256 blockTreeHash = TestItem.KeccakA;
        Hash256 blockCacheHash = TestItem.KeccakB;
        BlockHeader blockTreeHeader = Build.A.BlockHeader.WithNumber(blockTreeBlockNumber).WithHash(blockTreeHash).TestObject;
        BlockHeader blockCacheHeader = Build.A.BlockHeader.WithNumber(blockCacheBlockNumber).WithHash(blockCacheHash).TestObject;

        _poSSwitcher.TransitionFinished.Returns(true);
        _blockTree.FinalizedHash.Returns(blockTreeHash);
        _blockTree.FindHeader(blockTreeHash, BlockTreeLookupOptions.None).Returns(blockTreeHeader);
        _blockCacheService.FinalizedHash.Returns(blockCacheHash);
        _blockTree.FindHeader(blockCacheHash).Returns(blockCacheHeader);

        // Act
        long result = _provider.FinalizedBlockNumber;

        // Assert
        result.Should().Be(blockTreeBlockNumber);
    }

    [Test]
    public void FinalizedBlockNumber_AfterTransition_BlockCacheHeaderNotFound_UsesOnlyBlockTree()
    {
        // Arrange
        long expectedBlockNumber = 200;
        Hash256 blockTreeHash = TestItem.KeccakA;
        Hash256 blockCacheHash = TestItem.KeccakB;
        BlockHeader blockTreeHeader = Build.A.BlockHeader.WithNumber(expectedBlockNumber).WithHash(blockTreeHash).TestObject;

        _poSSwitcher.TransitionFinished.Returns(true);
        _blockTree.FinalizedHash.Returns(blockTreeHash);
        _blockTree.FindHeader(blockTreeHash, BlockTreeLookupOptions.None).Returns(blockTreeHeader);
        _blockCacheService.FinalizedHash.Returns(blockCacheHash);
        _blockTree.FindHeader(blockCacheHash).Returns((BlockHeader?)null);

        // Act
        long result = _provider.FinalizedBlockNumber;

        // Assert
        result.Should().Be(expectedBlockNumber);
    }

    [Test]
    public void FinalizedBlockNumber_AfterTransition_NoFinalizedHeaders_DelegatesToBaseProvider()
    {
        // Arrange
        long expectedBlockNumber = 150;
        _poSSwitcher.TransitionFinished.Returns(true);
        _blockTree.FinalizedHash.Returns((Hash256?)null);
        _blockCacheService.FinalizedHash.Returns((Hash256?)null);
        _baseFinalizedStateProvider.FinalizedBlockNumber.Returns(expectedBlockNumber);

        // Act
        long result = _provider.FinalizedBlockNumber;

        // Assert
        result.Should().Be(expectedBlockNumber);
        _ = _baseFinalizedStateProvider.Received(1).FinalizedBlockNumber;
    }

    [Test]
    public void GetFinalizedStateRootAt_ReturnsNull_WhenBlockNumberExceedsFinalizedBlock()
    {
        // Arrange
        long finalizedBlockNumber = 100;
        long blockNumber = 150;
        _poSSwitcher.TransitionFinished.Returns(false);
        _baseFinalizedStateProvider.FinalizedBlockNumber.Returns(finalizedBlockNumber);

        // Act
        Hash256? result = _provider.GetFinalizedStateRootAt(blockNumber);

        // Assert
        result.Should().BeNull();
        _baseFinalizedStateProvider.DidNotReceive().GetFinalizedStateRootAt(Arg.Any<long>());
    }

    [Test]
    public void GetFinalizedStateRootAt_DelegatesToBaseProvider_WhenBlockNumberIsFinalized()
    {
        // Arrange
        long finalizedBlockNumber = 100;
        long blockNumber = 50;
        Hash256 expectedStateRoot = TestItem.KeccakA;
        _poSSwitcher.TransitionFinished.Returns(false);
        _baseFinalizedStateProvider.FinalizedBlockNumber.Returns(finalizedBlockNumber);
        _baseFinalizedStateProvider.GetFinalizedStateRootAt(blockNumber).Returns(expectedStateRoot);

        // Act
        Hash256? result = _provider.GetFinalizedStateRootAt(blockNumber);

        // Assert
        result.Should().Be(expectedStateRoot);
        _baseFinalizedStateProvider.Received(1).GetFinalizedStateRootAt(blockNumber);
    }
}
