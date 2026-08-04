// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[TestFixture, Parallelizable(ParallelScope.All)]
internal class XdcBlockTreeTests
{
    [Test]
    public void Suggest_BlockBelowFinalizedHeight_WhenKnown_ReturnsAlreadyKnown()
    {
        (XdcBlockTree blockTree, IXdcConsensusContext consensus) = BuildBlockTree();

        Block genesis = XdcBlock(0UL, Keccak.Zero);
        Block block1 = XdcBlock(1UL, genesis.Hash!);
        Block block2 = XdcBlock(2UL, block1.Hash!);

        blockTree.SuggestBlock(genesis);
        blockTree.SuggestBlock(block1);
        blockTree.SuggestBlock(block2);
        blockTree.UpdateHeadBlock(block2.Hash!);

        consensus.HighestCommitBlock.Returns(new BlockRoundInfo(block2.Hash!, 1, block2.Number));

        Assert.That(blockTree.SuggestBlock(block1), Is.EqualTo(AddBlockResult.AlreadyKnown));
    }

    [Test]
    public void Suggest_BlockBelowFinalizedHeight_WhenUnknown_ReturnsInvalidBlock()
    {
        (XdcBlockTree blockTree, IXdcConsensusContext consensus) = BuildBlockTree();

        Block genesis = XdcBlock(0UL, Keccak.Zero);
        Block block1 = XdcBlock(1UL, genesis.Hash!);
        Block block2 = XdcBlock(2UL, block1.Hash!);

        blockTree.SuggestBlock(genesis);
        blockTree.SuggestBlock(block1);
        blockTree.SuggestBlock(block2);
        blockTree.UpdateHeadBlock(block2.Hash!);

        consensus.HighestCommitBlock.Returns(new BlockRoundInfo(block2.Hash!, 1, block2.Number));

        Block unknown = XdcBlock(1UL, genesis.Hash!, nonce: 1UL);
        Assert.That(blockTree.SuggestBlock(unknown), Is.EqualTo(AddBlockResult.InvalidBlock));
    }

    [Test]
    public void Suggest_BlockAboveFinalizedHeight_ConnectedToFinalizedChain_ReturnsAdded()
    {
        (XdcBlockTree blockTree, IXdcConsensusContext consensus) = BuildBlockTree();

        Block genesis = XdcBlock(0UL, Keccak.Zero);
        Block block1 = XdcBlock(1UL, genesis.Hash!);
        Block block2 = XdcBlock(2UL, block1.Hash!);
        Block block3 = XdcBlock(3UL, block2.Hash!);

        blockTree.SuggestBlock(genesis);
        blockTree.SuggestBlock(block1);
        blockTree.SuggestBlock(block2);
        blockTree.SuggestBlock(block3);
        blockTree.UpdateHeadBlock(block3.Hash!);

        consensus.HighestCommitBlock.Returns(new BlockRoundInfo(block2.Hash!, 1, block2.Number));

        Block block3Alt = XdcBlock(3UL, block2.Hash!, nonce: 1UL);
        Assert.That(blockTree.SuggestBlock(block3Alt), Is.EqualTo(AddBlockResult.Added));
    }

    [Test]
    public void Suggest_BlockAboveFinalizedHeight_ForkBeforeFinalized_ReturnsInvalidBlock()
    {
        (XdcBlockTree blockTree, IXdcConsensusContext consensus) = BuildBlockTree();

        Block genesis = XdcBlock(0UL, Keccak.Zero);
        Block block1 = XdcBlock(1UL, genesis.Hash!);
        Block block2 = XdcBlock(2UL, block1.Hash!);
        Block block3 = XdcBlock(3UL, block2.Hash!);

        // block2Fork forks at height 2 — same height as the finalized block
        Block block2Fork = XdcBlock(2UL, block1.Hash!, nonce: 1UL);
        Block block3Fork = XdcBlock(3UL, block2Fork.Hash!);

        blockTree.SuggestBlock(genesis);
        blockTree.SuggestBlock(block1);
        blockTree.SuggestBlock(block2);
        blockTree.SuggestBlock(block2Fork);
        blockTree.SuggestBlock(block3);
        blockTree.UpdateHeadBlock(block3.Hash!);

        consensus.HighestCommitBlock.Returns(new BlockRoundInfo(block2.Hash!, 1, block2.Number));

        Assert.That(blockTree.SuggestBlock(block3Fork), Is.EqualTo(AddBlockResult.InvalidBlock));
    }

    [Test]
    public void Suggest_BlockAboveFinalizedHeight_UnknownParent_ReturnsUnknownParent()
    {
        (XdcBlockTree blockTree, IXdcConsensusContext consensus) = BuildBlockTree();

        Block genesis = XdcBlock(0UL, Keccak.Zero);
        Block block1 = XdcBlock(1UL, genesis.Hash!);
        blockTree.SuggestBlock(genesis);
        blockTree.SuggestBlock(block1);
        blockTree.UpdateHeadBlock(block1.Hash!);

        consensus.HighestCommitBlock.Returns(new BlockRoundInfo(block1.Hash!, 1, block1.Number));

        Block disconnected = XdcBlock(2UL, Keccak.Compute("other"));
        Assert.That(blockTree.SuggestBlock(disconnected), Is.EqualTo(AddBlockResult.UnknownParent));
    }

    [TestCase(5UL, false, true)] // higher round from a remote (non-self-mined) block overrides an equal-TD self-mined head
    [TestCase(1UL, false, false)] // same round from a remote block does not override - self-mined tie-break still applies
    [TestCase(1UL, true, true)] // same round, also self-mined, overrides (unchanged proposal-race behavior)
    [TestCase(0UL, true, false)] // lower round does not override, even if self-mined
    public void IsSameTdButPreferred_BreaksEqualTdTieByRoundThenSelfMined(ulong candidateRound, bool candidateSelfMined, bool expectedOverride)
    {
        XdcBlockHeader head = XdcHeaderWithRound(1UL, Keccak.Zero, round: 1, selfMined: true);
        head.TotalDifficulty = 2;
        XdcBlockHeader candidate = XdcHeaderWithRound(1UL, Keccak.Zero, round: candidateRound, selfMined: candidateSelfMined, nonce: 1UL);
        candidate.TotalDifficulty = head.TotalDifficulty;

        Assert.That(XdcBlockTree.IsSameTdButPreferred(candidate, head), Is.EqualTo(expectedOverride));
    }

    [Test]
    public void IsSameTdButPreferred_DifferentTotalDifficulty_NeverOverrides()
    {
        XdcBlockHeader head = XdcHeaderWithRound(1UL, Keccak.Zero, round: 1, selfMined: false);
        XdcBlockHeader candidate = XdcHeaderWithRound(1UL, Keccak.Zero, round: 5, selfMined: true, nonce: 1UL);
        head.TotalDifficulty = 2;
        candidate.TotalDifficulty = 3;

        Assert.That(XdcBlockTree.IsSameTdButPreferred(candidate, head), Is.False);
    }

    [Test]
    public void IsSameTdButPreferred_MissingConsensusData_NeverOverrides()
    {
        XdcBlockHeader head = XdcHeaderWithRound(1UL, Keccak.Zero, round: 1, selfMined: false);
        head.TotalDifficulty = 2;
        XdcBlockHeader candidate = Build.A.XdcBlockHeader().WithNumber(1UL).TestObject;
        candidate.TotalDifficulty = head.TotalDifficulty;

        Assert.That(XdcBlockTree.IsSameTdButPreferred(candidate, head), Is.False);
    }

    private static XdcBlockHeader XdcHeaderWithRound(ulong number, Hash256 parentHash, ulong round, bool selfMined, ulong nonce = 0)
    {
        XdcBlockHeader header = new(
            parentHash,
            Keccak.OfAnEmptySequenceRlp,
            Address.Zero,
            UInt256.One,
            number,
            XdcConstants.DefaultTargetGasLimit,
            1_700_000_000,
            [],
            isSelfMined: selfMined)
        {
            Nonce = nonce
        };
        header.ExtraConsensusData = new ExtraFieldsV2(round, null!);
        header.Hash = header.CalculateHash().ToHash256();
        return header;
    }

    private static Block XdcBlock(ulong number, Hash256 parentHash, ulong nonce = 0)
    {
        XdcBlockHeader header = Build.A.XdcBlockHeader()
            .WithNumber(number)
            .WithParentHash(parentHash)
            .TestObject;
        header.Nonce = nonce;
        header.Hash = header.CalculateHash().ToHash256();
        return Build.A.Block.WithHeader(header).TestObject;
    }

    private static (XdcBlockTree, IXdcConsensusContext) BuildBlockTree()
    {
        IXdcConsensusContext consensus = Substitute.For<IXdcConsensusContext>();
        consensus.HighestCommitBlock.Returns((BlockRoundInfo)null!);

        XdcHeaderStore xdcHeaderStore = new(new TestMemDb(), new TestMemDb(), new XdcHeaderDecoder());
        BlockTreeBuilder builder = Build.A.BlockTree(MainnetSpecProvider.Instance)
            .WithHeaderStore(xdcHeaderStore)
            .WithoutSettingHead;

        XdcBlockTree blockTree = new(
            consensus,
            builder.BlockStore,
            xdcHeaderStore,
            builder.BlockInfoDb,
            builder.MetadataDb,
            builder.BadBlockStore,
            builder.BlockAccessListStore,
            builder.ChainLevelInfoRepository,
            MainnetSpecProvider.Instance,
            builder.SyncConfig,
            builder.StateBoundary,
            LimboLogs.Instance);

        return (blockTree, consensus);
    }
}
