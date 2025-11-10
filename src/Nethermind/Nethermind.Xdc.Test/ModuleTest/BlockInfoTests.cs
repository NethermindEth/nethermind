// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus.Validators;
using Nethermind.Xdc.Test.Helpers;
using Nethermind.Xdc.Types;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.ModuleTest;
internal class BlockInfoTests
{
    private XdcTestBlockchain xdcTestBlockchain;

    [SetUp]
    public async Task Setup()
    {
        xdcTestBlockchain = await XdcTestBlockchain.Create();
    }

    [Test]
    public void VerifyGenesisV2Block()
    {
        XdcBlockHeader genesisBlock = (XdcBlockHeader)xdcTestBlockchain.BlockTree.FindHeader(xdcTestBlockchain.BlockTree.Genesis!.Number + 1)!;

        BlockRoundInfo blockInfo = new BlockRoundInfo(genesisBlock.Hash!, 1, genesisBlock.Number);

        var result = XdcExtensions.ValidateBlockInfo(blockInfo, genesisBlock);

        Assert.That(result, Is.True);
    }

    [Test]
    public void RoundMismatch_Fails()
    {
        XdcBlockHeader headBlock = (XdcBlockHeader)xdcTestBlockchain.BlockTree.Head!.Header!;

        BlockRoundInfo blockInfo = new BlockRoundInfo(headBlock.Hash!, headBlock.ExtraConsensusData!.BlockRound - 1, headBlock.Number);

        var result = XdcExtensions.ValidateBlockInfo(blockInfo, headBlock);

        Assert.That(result, Is.False);
    }


    [Test]
    public void HashMismatch_Fails()
    {
        XdcBlockHeader headBlock = (XdcBlockHeader)xdcTestBlockchain.BlockTree.Head!.Header!;
        XdcBlockHeader parentBlock = (XdcBlockHeader)xdcTestBlockchain.BlockTree.FindHeader(headBlock.ParentHash!)!;

        BlockRoundInfo blockInfo = new BlockRoundInfo(parentBlock.Hash!, headBlock.ExtraConsensusData!.BlockRound, headBlock.Number);

        var result = XdcExtensions.ValidateBlockInfo(blockInfo, headBlock);

        Assert.That(result, Is.False);
    }


    [Test]
    public void NumberMismatch_Fails()
    {
        XdcBlockHeader headBlock = (XdcBlockHeader)xdcTestBlockchain.BlockTree.Head!.Header!;
        XdcBlockHeader parentBlock = (XdcBlockHeader)xdcTestBlockchain.BlockTree.FindHeader(headBlock.ParentHash!)!;

        BlockRoundInfo blockInfo = new BlockRoundInfo(headBlock.Hash!, headBlock.ExtraConsensusData!.BlockRound, parentBlock.Number);

        var result = XdcExtensions.ValidateBlockInfo(blockInfo, headBlock);

        Assert.That(result, Is.False);
    }


    [Test]
    public void NoMismatch_Pass()
    {
        XdcBlockHeader headBlock = (XdcBlockHeader)xdcTestBlockchain.BlockTree.Head!.Header!;

        BlockRoundInfo blockInfo = new BlockRoundInfo(headBlock.Hash!, headBlock.ExtraConsensusData!.BlockRound, headBlock.Number);

        var result = XdcExtensions.ValidateBlockInfo(blockInfo, headBlock);

        Assert.That(result, Is.True);
    }

    /*
    func TestShouldVerifyBlockInfo(t *testing.T) {
	    // Insert another Block, but it won't trigger commit
	    blockNum := 902
	    blockCoinBase := fmt.Sprintf("0x111000000000000000000000000000000%03d", blockNum)
	    block902 := CreateBlock(blockchain, params.TestXDPoSMockChainConfig, currentBlock, blockNum, 2, blockCoinBase, signer, signFn, nil, nil, "")
	    err = blockchain.InsertBlock(block902)
	    assert.Nil(t, err)
	}
    */

}

