// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Consensus;
using Nethermind.Xdc.Test.Helpers;
using Nethermind.Xdc.Types;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;
internal class XdcReorgModuleTests
{
    [Test]
    public async Task TestNormalReorgWhenNotInvolveCommittedBlock()
    {
        var blockChain = await XdcTestBlockchain.Create();
        var startRound = blockChain.XdcContext.CurrentRound;
        await blockChain.AddBlocks(3);
        // Simulate timeout to make block rounds non-consecutive preventing finalization
        blockChain.XdcContext.SetNewRound(blockChain.XdcContext.CurrentRound + 1);
        await blockChain.AddBlocks(2);
        var finalizedBlockInfo = blockChain.XdcContext.HighestCommitBlock;
        finalizedBlockInfo.Round.Should().Be(blockChain.XdcContext.CurrentRound - 2 - 1 - 2); // Finalization is 2 round behind current plus 1 round timeout plus 2 rounds of blocks added

        var finalizedBlock = blockChain.BlockTree.FindHeader(finalizedBlockInfo.Hash);

        var forkBlock = await blockChain.BlockProducer.BuildBlock(finalizedBlock);

        blockChain.BlockTree.SuggestBlock(forkBlock!).Should().Be(Blockchain.AddBlockResult.Added);
    }

    [Test]
    public async Task TestReorg()
    {
        var blockChain = await XdcTestBlockchain.Create(3);
        var startRound = blockChain.XdcContext.CurrentRound;
        await blockChain.AddBlocks(10);

        var finalizedBlockInfo = blockChain.XdcContext.HighestCommitBlock;
        finalizedBlockInfo.Round.Should().Be(blockChain.XdcContext.CurrentRound - 2); // Finalization is 2 rounds behind

        XdcBlockHeader? finalizedBlock = (XdcBlockHeader)blockChain.BlockTree.FindHeader(finalizedBlockInfo.Hash)!;

        var newHeadWaitHandle = new TaskCompletionSource();
        blockChain.BlockTree.NewHeadBlock += (_, args) =>
        {
            newHeadWaitHandle.SetResult();
        };        

        XdcBlockHeader forkparent = finalizedBlock;
        for (int i = 0; i < 3; i++)
        {
            //Build a fork on finalized block, which should result in fork becoming new head
            forkparent = (XdcBlockHeader)(await blockChain.AddBlockFromParent(forkparent)).Header;
        }
        
        if (blockChain.BlockTree.Head!.Hash != forkparent.Hash)
        {
            //Wait for new head 
            await Task.WhenAny(newHeadWaitHandle.Task, Task.Delay(10_000));
        }

        blockChain.BlockTree.Head!.Hash.Should().Be(forkparent.Hash!);
        blockChain.XdcContext.HighestCommitBlock.Hash.Should().Be(blockChain.BlockTree.FindHeader(forkparent.ParentHash!)!.ParentHash!);
    }

    [TestCase(5)]
    [TestCase(900)]
    [TestCase(901)]
    public async Task TestShouldNotReorgCommittedBlock(int number)
    {
        var blockChain = await XdcTestBlockchain.Create();
        var startRound = blockChain.XdcContext.CurrentRound;
        await blockChain.AddBlocks(number);
        var finalizedBlockInfo = blockChain.XdcContext.HighestCommitBlock;
        finalizedBlockInfo.Round.Should().Be(blockChain.XdcContext.CurrentRound - 2); // Finalization is 2 rounds behind

        var finalizedBlock = blockChain.BlockTree.FindHeader(finalizedBlockInfo.Hash)!;
        var parentOfFinalizedBlock = blockChain.BlockTree.FindHeader(finalizedBlock.ParentHash!);

        var forkBlock = await blockChain.BlockProducer.BuildBlock(parentOfFinalizedBlock);

        blockChain.BlockTree.SuggestBlock(forkBlock!).Should().Be(Blockchain.AddBlockResult.InvalidBlock);
    }

}
