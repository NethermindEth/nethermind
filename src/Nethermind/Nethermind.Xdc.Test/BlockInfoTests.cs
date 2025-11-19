// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Xdc.Test.Helpers;
using Nethermind.Xdc.Types;
using NUnit.Framework;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;
[Parallelizable(ParallelScope.All)]
internal class BlockInfoTests
{
    [Test]
    public async Task VerifyGenesisV2Block()
    {
        XdcTestBlockchain xdcTestBlockchain = await XdcTestBlockchain.Create();
        XdcBlockHeader genesisBlock = (XdcBlockHeader)xdcTestBlockchain.BlockTree.FindHeader(xdcTestBlockchain.BlockTree.Genesis!.Number + 1)!;

        BlockRoundInfo blockInfo = new BlockRoundInfo(genesisBlock.Hash!, 1, genesisBlock.Number);

        var result = XdcExtensions.ValidateBlockInfo(blockInfo, genesisBlock);

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task RoundMismatch_Fails()
    {
        XdcTestBlockchain xdcTestBlockchain = await XdcTestBlockchain.Create();
        XdcBlockHeader headBlock = (XdcBlockHeader)xdcTestBlockchain.BlockTree.Head!.Header!;

        BlockRoundInfo blockInfo = new BlockRoundInfo(headBlock.Hash!, headBlock.ExtraConsensusData!.BlockRound - 1, headBlock.Number);

        var result = XdcExtensions.ValidateBlockInfo(blockInfo, headBlock);

        Assert.That(result, Is.False);
    }


    [Test]
    public async Task HashMismatch_Fails()
    {
        XdcTestBlockchain xdcTestBlockchain = await XdcTestBlockchain.Create();
        XdcBlockHeader headBlock = (XdcBlockHeader)xdcTestBlockchain.BlockTree.Head!.Header!;
        XdcBlockHeader parentBlock = (XdcBlockHeader)xdcTestBlockchain.BlockTree.FindHeader(headBlock.ParentHash!)!;

        BlockRoundInfo blockInfo = new BlockRoundInfo(parentBlock.Hash!, headBlock.ExtraConsensusData!.BlockRound, headBlock.Number);

        var result = XdcExtensions.ValidateBlockInfo(blockInfo, headBlock);

        Assert.That(result, Is.False);
    }


    [Test]
    public async Task NumberMismatch_Fails()
    {
        XdcTestBlockchain xdcTestBlockchain = await XdcTestBlockchain.Create();
        XdcBlockHeader headBlock = (XdcBlockHeader)xdcTestBlockchain.BlockTree.Head!.Header!;
        XdcBlockHeader parentBlock = (XdcBlockHeader)xdcTestBlockchain.BlockTree.FindHeader(headBlock.ParentHash!)!;

        BlockRoundInfo blockInfo = new BlockRoundInfo(headBlock.Hash!, headBlock.ExtraConsensusData!.BlockRound, parentBlock.Number);

        var result = XdcExtensions.ValidateBlockInfo(blockInfo, headBlock);

        Assert.That(result, Is.False);
    }


    [Test]
    public async Task NoMismatch_Pass()
    {
        XdcTestBlockchain xdcTestBlockchain = await XdcTestBlockchain.Create();
        XdcBlockHeader headBlock = (XdcBlockHeader)xdcTestBlockchain.BlockTree.Head!.Header!;

        BlockRoundInfo blockInfo = new BlockRoundInfo(headBlock.Hash!, headBlock.ExtraConsensusData!.BlockRound, headBlock.Number);

        var result = XdcExtensions.ValidateBlockInfo(blockInfo, headBlock);

        Assert.That(result, Is.True);
    }
}

