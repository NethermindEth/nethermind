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
        finalizedBlockInfo.Should().NotBeNull("Finalized yet");
        finalizedBlockInfo.Round.Should().Be(blockChain.XdcContext.CurrentRound - 2 - 1 - 2); // Finalization is 2 round behind current plus 1 round timeout plus 2 rounds of blocks added

        var finalizedParent = blockChain.BlockTree.FindHeader(finalizedBlockInfo.Hash);

        var forkBlock = await blockChain.BlockProducer.BuildBlock(finalizedParent);

        blockChain.BlockTree.SuggestBlock(forkBlock!).Should().Be(Blockchain.AddBlockResult.Added);
    }

    [Test]
    public async Task TestShouldNotReorgCommittedBlock()
    {
        var blockChain = await XdcTestBlockchain.Create();
        var startRound = blockChain.XdcContext.CurrentRound;
        await blockChain.AddBlocks(3);
        // Simulate timeout to make block rounds non-consecutive preventing finalization
        blockChain.XdcContext.SetNewRound(blockChain.XdcContext.CurrentRound + 1);
        await blockChain.AddBlocks(2);
        var finalizedBlockInfo = blockChain.XdcContext.HighestCommitBlock;
        finalizedBlockInfo.Should().NotBeNull("Finalized yet");
        finalizedBlockInfo.Round.Should().Be(blockChain.XdcContext.CurrentRound - 2 - 1 - 2); // Finalization is 2 round behind current plus 1 round timeout plus 2 rounds of blocks added

        var finalizedParent = blockChain.BlockTree.FindHeader(finalizedBlockInfo.Hash);

        var forkBlock = await blockChain.BlockProducer.BuildBlock(finalizedParent);

        blockChain.BlockTree.SuggestBlock(forkBlock!).Should().Be(Blockchain.AddBlockResult.InvalidBlock);
    }

}
