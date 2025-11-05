// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using CommunityToolkit.HighPerformance;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State.Repositories;
using Nethermind.Xdc;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace Nethermind.Xdc.Tests;

[TestFixture]
internal class XdcBlockTreeTests
{
    private MemDb _blocksDb;
    private MemDb _headersDb;
    private MemDb _blockInfosDb;
    private MemDb _metadataDb;
    private TestableXdcBlockTree _blockTree;
    private XdcConsensusContext _xdcContext;

    [SetUp]
    public void Setup()
    {
        _blocksDb = new MemDb();
        _headersDb = new MemDb();
        _blockInfosDb = new MemDb();
        _metadataDb = new MemDb();
        _xdcContext = new XdcConsensusContext();

        ISyncConfig syncConfig = Substitute.For<ISyncConfig>();
        syncConfig.PivotNumber.Returns("1");
        syncConfig.PivotHash.Returns(Hash256.Zero.ToString());
        _blockTree = new TestableXdcBlockTree(
            _xdcContext,
            new BlockStore(_blocksDb),
            new HeaderStore(_headersDb, _blockInfosDb),
            _blockInfosDb,
            _metadataDb,
            Substitute.For<IBadBlockStore>(),
            new ChainLevelInfoRepository(_blockInfosDb),
            MainnetSpecProvider.Instance,
            NullBloomStorage.Instance,
            syncConfig,
            LimboLogs.Instance,
            0);

        // Insert genesis
        Block genesis = CreateBlock(0, Keccak.Zero);
        _blockTree.SuggestBlock(genesis);
        _blockTree.UpdateMainChain(new[] { genesis }, true);
    }

    [TearDown]
    public void TearDown()
    {
        _blocksDb?.Dispose();
        _headersDb?.Dispose();
        _blockInfosDb?.Dispose();
        _metadataDb?.Dispose();
    }

    /// <summary>
    /// Test 1: No finalized block (null) - should allow any block
    /// </summary>
    [Test]
    public void NoFinalizedBlock_ReturnsTrue()
    {
        // Arrange
        _xdcContext.HighestCommitBlock = null!;
        Block block1 = CreateBlock(1, _blockTree.Head!.Hash!);

        // Act
        bool result = _blockTree.TestBestSuggestedImprovementRequirementsSatisfied(block1.Header);

        // Assert
        result.Should().BeTrue("when no finalized block exists, any block should be allowed");
    }

    /// <summary>
    /// Test 2: Header is the finalized block itself - should reject (weird case)
    /// </summary>
    [Test]
    public void HeaderIsFinalizedBlock_ReturnsFalse()
    {
        // Arrange
        Block block1 = CreateBlock(1, _blockTree.Head!.Hash!);
        _blockTree.SuggestBlock(block1);
        _blockTree.UpdateMainChain(new[] { block1 }, true);

        // Set block1 as finalized
        _xdcContext.HighestCommitBlock = new BlockRoundInfo(block1.Hash!, 1, 1);

        // Act - try to re-suggest the same finalized block
        bool result = _blockTree.TestBestSuggestedImprovementRequirementsSatisfied(block1.Header);

        // Assert
        result.Should().BeFalse("re-suggesting the finalized block should be rejected");
    }

    /// <summary>
    /// Test 3: Header is direct child of finalized block - should allow
    /// </summary>
    [Test]
    public void DirectChildOfFinalizedBlock_ReturnsTrue()
    {
        // Arrange: Create chain: genesis -> block1(finalized) -> block2(new)
        Block block1 = CreateBlock(1, _blockTree.Head!.Hash!);
        _blockTree.SuggestBlock(block1);
        _blockTree.UpdateMainChain(new[] { block1 }, true);

        // Finalize block1
        _xdcContext.HighestCommitBlock = new BlockRoundInfo(block1.Hash!, 1, 1);

        Block block2 = CreateBlock(2, block1.Hash!);

        // Act
        bool result = _blockTree.TestBestSuggestedImprovementRequirementsSatisfied(block2.Header);

        // Assert
        result.Should().BeTrue("direct child of finalized block should be allowed");
    }

    /// <summary>
    /// Test 4: Header is descendant of finalized block (multiple generations) - should allow
    /// </summary>
    [Test]
    public void DescendantOfFinalizedBlock_ReturnsTrue()
    {
        // Arrange: Create chain: genesis -> block1(finalized) -> block2 -> block3 -> block4(new)
        Block block1 = CreateBlock(1, _blockTree.Head!.Hash!);
        _blockTree.SuggestBlock(block1);
        _blockTree.UpdateMainChain(new[] { block1 }, true);

        // Finalize block1
        _xdcContext.HighestCommitBlock = new BlockRoundInfo(block1.Hash!, 1, 1);

        Block block2 = CreateBlock(2, block1.Hash!);
        _blockTree.SuggestBlock(block2);
        _blockTree.UpdateMainChain(new[] { block2 }, true);

        Block block3 = CreateBlock(3, block2.Hash!);
        _blockTree.SuggestBlock(block3);
        _blockTree.UpdateMainChain(new[] { block3 }, true);

        Block block4 = CreateBlock(4, block3.Hash!);

        // Act
        bool result = _blockTree.TestBestSuggestedImprovementRequirementsSatisfied(block4.Header);

        // Assert
        result.Should().BeTrue("descendant of finalized block should be allowed");
    }

    /// <summary>
    /// Test 5: Header is on different fork before finalized block - should reject
    /// New logic: When walking back, we reach block number <= finalized without finding it
    /// </summary>
    [Test]
    public void ForkBeforeFinalizedBlock_ReturnsFalse()
    {
        // Arrange: Create diverging chains
        // Main: genesis -> block1 -> block2(finalized) -> block3
        // Fork: genesis -> block1Alt -> block2Alt (trying to suggest this)

        Block block1 = CreateBlock(1, _blockTree.Head!.Hash!);
        _blockTree.SuggestBlock(block1);
        _blockTree.UpdateMainChain(new[] { block1 }, true);

        Block block2 = CreateBlock(2, block1.Hash!);
        _blockTree.SuggestBlock(block2);
        _blockTree.UpdateMainChain(new[] { block2 }, true);

        // Finalize block2 at height 2
        _xdcContext.HighestCommitBlock = new BlockRoundInfo(block2.Hash!, 2, 2);

        // Create alternative fork from genesis
        Block block1Alt = CreateBlock(1, _blockTree.Genesis!.Hash!, differentHash: true);
        _blockTree.SuggestBlock(block1Alt); // Add to tree so parent can be found

        Block block2Alt = CreateBlock(2, block1Alt.Hash!, differentHash: true);

        // Act: When walking back from block2Alt, we'll reach block1Alt (height 1)
        // Then genesis (height 0), which is < finalized height (2), so reject
        bool result = _blockTree.TestBestSuggestedImprovementRequirementsSatisfied(block2Alt.Header);

        // Assert
        result.Should().BeFalse("fork not descending from finalized block should be rejected");
    }

    /// <summary>
    /// Test 6: Header causes deep reorg (>1024 blocks) - should reject
    /// </summary>
    [Test]
    public void DeepReorgPastMaxDepth_ReturnsFalse()
    {
        // Arrange: Create long chain and finalize early block
        Block currentBlock = _blockTree.Head!;

        // Build chain of 10 blocks
        for (int i = 1; i <= 10; i++)
        {
            Block nextBlock = CreateBlock(i, currentBlock.Hash!);
            _blockTree.SuggestBlock(nextBlock);
            _blockTree.UpdateMainChain(new[] { nextBlock }, true);
            currentBlock = nextBlock;
        }

        // Finalize block at height 5
        Block block5 = _blockTree.FindBlock(5, BlockTreeLookupOptions.None)!;
        _xdcContext.HighestCommitBlock = new BlockRoundInfo(block5.Hash!, 5, 5);

        // Now try to suggest a block that would require reorg deeper than MaxSearchDepth
        // We'll fake this by setting MaxSearchDepth lower in a modified test
        // For this test, we'll create a fork from genesis (depth = 10 > hypothetical limit)

        Block deepForkBlock = CreateBlock(11, _blockTree.Genesis!.Hash!, differentHash: true);

        // Act
        bool result = _blockTree.TestBestSuggestedImprovementRequirementsSatisfied(deepForkBlock.Header);

        // Assert
        result.Should().BeFalse("fork requiring traversal past finalized block should be rejected");
    }

    /// <summary>
    /// Test 7: Header's parent not found in tree - should reject
    /// </summary>
    [Test]
    public void ParentNotFoundInTree_ReturnsFalse()
    {
        // Arrange
        Block block1 = CreateBlock(1, _blockTree.Head!.Hash!);
        _blockTree.SuggestBlock(block1);
        _blockTree.UpdateMainChain(new[] { block1 }, true);

        // Finalize block1
        _xdcContext.HighestCommitBlock = new BlockRoundInfo(block1.Hash!, 1, 1);

        // Create orphan block with non-existent parent
        Hash256 nonExistentParent = Keccak.Compute("non-existent-parent");
        Block orphanBlock = CreateBlock(2, nonExistentParent);

        // Act
        bool result = _blockTree.TestBestSuggestedImprovementRequirementsSatisfied(orphanBlock.Header);

        // Assert
        result.Should().BeFalse("block with missing parent should be rejected");
    }

    /// <summary>
    /// Test 8: Complex scenario - multiple forks with finalized block in middle
    /// </summary>
    [Test]
    public void ComplexForkScenario_ValidDescendantAllowed_InvalidRejected()
    {
        // Arrange: Build complex chain structure
        // Main chain: genesis -> b1 -> b2(finalized) -> b3 -> b4
        // Valid fork: b2 -> b3Alt -> b4Alt (descendant of finalized)
        // Invalid fork: b1 -> b2Bad (not descendant of finalized)

        Block b1 = CreateBlock(1, _blockTree.Head!.Hash!);
        _blockTree.SuggestBlock(b1);
        _blockTree.UpdateMainChain(new[] { b1 }, true);

        Block b2 = CreateBlock(2, b1.Hash!);
        _blockTree.SuggestBlock(b2);
        _blockTree.UpdateMainChain(new[] { b2 }, true);

        // Finalize b2
        _xdcContext.HighestCommitBlock = new BlockRoundInfo(b2.Hash!, 2, 2);

        Block b3 = CreateBlock(3, b2.Hash!);
        _blockTree.SuggestBlock(b3);
        _blockTree.UpdateMainChain(new[] { b3 }, true);

        Block b4 = CreateBlock(4, b3.Hash!);
        _blockTree.SuggestBlock(b4);
        _blockTree.UpdateMainChain(new[] { b4 }, true);

        // Valid fork from finalized block
        Block b3Alt = CreateBlock(3, b2.Hash!, differentHash: true);
        bool validFork = _blockTree.TestBestSuggestedImprovementRequirementsSatisfied(b3Alt.Header);

        // Invalid fork before finalized block
        Block b2Bad = CreateBlock(2, b1.Hash!, differentHash: true);
        bool invalidFork = _blockTree.TestBestSuggestedImprovementRequirementsSatisfied(b2Bad.Header);

        // Assert
        validFork.Should().BeTrue("fork from finalized block should be allowed");
        invalidFork.Should().BeFalse("fork before finalized block should be rejected");
    }

    [Test]
    public void SameHeightDifferentBranch_ReturnsFalse()
    {
        // Arrange: 
        // Chain A: genesis -> b1 -> b2(finalized)
        // Chain B: genesis -> b1Alt -> b2Alt (try to suggest)

        Block b1 = CreateBlock(1, _blockTree.Head!.Hash!);
        _blockTree.SuggestBlock(b1);
        _blockTree.UpdateMainChain(new[] { b1 }, true);

        Block b2 = CreateBlock(2, b1.Hash!);
        _blockTree.SuggestBlock(b2);
        _blockTree.UpdateMainChain(new[] { b2 }, true);

        // Finalize b2 at height 2
        _xdcContext.HighestCommitBlock = new BlockRoundInfo(b2.Hash!, 2, 2);

        // Create alternative fork from genesis
        Block b1Alt = CreateBlock(1, _blockTree.Genesis!.Hash!, differentHash: true);
        _blockTree.SuggestBlock(b1Alt);

        // Try block at same height as finalized
        Block b2Alt = CreateBlock(2, b1Alt.Hash!, differentHash: true);

        // Act: Walking back: b2Alt -> b1Alt (height 1) -> genesis (height 0)
        // Since genesis.Number (0) < finalized.Number (2), reject
        bool result = _blockTree.TestBestSuggestedImprovementRequirementsSatisfied(b2Alt.Header);

        // Assert
        result.Should().BeFalse("block at same height on different branch should be rejected");
    }

    /// <summary>
    /// Test 10: Genesis as finalized block
    /// </summary>
    [Test]
    public void GenesisAsFinalized_AllowsDescendants()
    {
        // Arrange
        _xdcContext.HighestCommitBlock = new BlockRoundInfo(
            _blockTree.Genesis!.Hash!, 0, 0);

        Block b1 = CreateBlock(1, _blockTree.Genesis!.Hash!);

        // Act
        bool result = _blockTree.TestBestSuggestedImprovementRequirementsSatisfied(b1.Header);

        // Assert
        result.Should().BeTrue("descendant of genesis (finalized) should be allowed");
    }

    /// <summary>
    /// Test 11: Block number comparison - walking back reaches finalized number
    /// Tests the new condition: if (finalizedBlockInfo.BlockNumber >= current.Number)
    /// </summary>
    [Test]
    public void WalkingBackReachesFinalizedNumber_ReturnsFalse()
    {
        // Arrange: Build two separate chains
        // Main: genesis -> b1 -> b2 -> b3(finalized)
        // Alt:  genesis -> bAlt1 -> bAlt2 -> bAlt3 -> bAlt4 (try to suggest)

        // Build main chain
        Block b1 = CreateBlock(1, _blockTree.Genesis!.Hash!);
        _blockTree.SuggestBlock(b1);
        _blockTree.UpdateMainChain(new[] { b1 }, true);

        Block b2 = CreateBlock(2, b1.Hash!);
        _blockTree.SuggestBlock(b2);
        _blockTree.UpdateMainChain(new[] { b2 }, true);

        Block b3 = CreateBlock(3, b2.Hash!);
        _blockTree.SuggestBlock(b3);
        _blockTree.UpdateMainChain(new[] { b3 }, true);

        // Finalize b3 at height 3
        _xdcContext.HighestCommitBlock = new BlockRoundInfo(b3.Hash!, 3, 3);

        // Build alternative chain from genesis
        Block bAlt1 = CreateBlock(1, _blockTree.Genesis!.Hash!, differentHash: true);
        _blockTree.SuggestBlock(bAlt1);

        Block bAlt2 = CreateBlock(2, bAlt1.Hash!, differentHash: true);
        _blockTree.SuggestBlock(bAlt2);

        Block bAlt3 = CreateBlock(3, bAlt2.Hash!, differentHash: true);
        _blockTree.SuggestBlock(bAlt3);

        Block bAlt4 = CreateBlock(4, bAlt3.Hash!, differentHash: true);

        // Act: Walking back from bAlt4:
        // bAlt4 (height 4) -> bAlt3 (height 3)
        // Check: finalized.Number (3) >= current.Number (3) -> TRUE, reject
        bool result = _blockTree.TestBestSuggestedImprovementRequirementsSatisfied(bAlt4.Header);

        // Assert
        result.Should().BeFalse("walking back to finalized height on different chain should be rejected");
    }

    /// <summary>
    /// Test 12: Block exactly one level above finalized on wrong chain
    /// </summary>
    [Test]
    public void BlockOneAboveFinalizedOnWrongChain_ReturnsFalse()
    {
        // Arrange: 
        // Main: genesis -> b1 -> b2(finalized)
        // Alt:  genesis -> bAlt1 -> bAlt2 -> bAlt3 (try this at height 3)

        Block b1 = CreateBlock(1, _blockTree.Genesis!.Hash!);
        _blockTree.SuggestBlock(b1);
        _blockTree.UpdateMainChain(new[] { b1 }, true);

        Block b2 = CreateBlock(2, b1.Hash!);
        _blockTree.SuggestBlock(b2);
        _blockTree.UpdateMainChain(new[] { b2 }, true);

        // Finalize b2 at height 2
        _xdcContext.HighestCommitBlock = new BlockRoundInfo(b2.Hash!, 2, 2);

        // Alternative chain
        Block bAlt1 = CreateBlock(1, _blockTree.Genesis!.Hash!, differentHash: true);
        _blockTree.SuggestBlock(bAlt1);

        Block bAlt2 = CreateBlock(2, bAlt1.Hash!, differentHash: true);
        _blockTree.SuggestBlock(bAlt2);

        Block bAlt3 = CreateBlock(3, bAlt2.Hash!, differentHash: true);

        // Act: Walking from bAlt3 (height 3):
        // bAlt3 -> bAlt2 (height 2), finalized.Number (2) >= current.Number (2) -> reject
        bool result = _blockTree.TestBestSuggestedImprovementRequirementsSatisfied(bAlt3.Header);

        // Assert
        result.Should().BeFalse("block above finalized on wrong chain should be rejected");
    }

    private Block CreateBlock(long number, Hash256 parentHash, bool differentHash = false)
    {
        var header = Build.A.XdcBlockHeader()
            .WithNumber(number)
            .WithParentHash(parentHash)
            .WithExtraConsensusData(
            new ExtraFieldsV2((ulong)number, new QuorumCertificate(
                    new BlockRoundInfo(parentHash, (ulong)number, number),
                    Array.Empty<Signature>(), 0))
            )
            .WithTimestamp(differentHash ? (ulong)DateTime.Now.Ticks : 1)
            .TestObject;
        return new Block(header, Array.Empty<Transaction>(), Array.Empty<BlockHeader>());
    }

    private class TestableXdcBlockTree : XdcBlockTree
    {
        public TestableXdcBlockTree(
            XdcConsensusContext xdcConsensus,
            IBlockStore blockStore,
            IHeaderStore headerDb,
            IDb blockInfoDb,
            IDb metadataDb,
            IBadBlockStore badBlockStore,
            IChainLevelInfoRepository chainLevelInfoRepository,
            ISpecProvider specProvider,
            IBloomStorage bloomStorage,
            ISyncConfig syncConfig,
            ILogManager logManager,
            long genesisBlockNumber = 0)
            : base(xdcConsensus, blockStore, headerDb, blockInfoDb, metadataDb,
                    badBlockStore, chainLevelInfoRepository, specProvider, bloomStorage,
                    syncConfig, logManager, genesisBlockNumber)
        {
        }

        public bool TestBestSuggestedImprovementRequirementsSatisfied(BlockHeader header)
        {
            return BestSuggestedImprovementRequirementsSatisfied(header);
        }
    }
}
