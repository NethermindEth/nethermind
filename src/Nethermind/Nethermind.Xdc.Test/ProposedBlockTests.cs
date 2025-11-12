// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Consensus;
using Nethermind.Crypto;
using Nethermind.Xdc.Test.Helpers;
using Nethermind.Xdc.Types;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;
internal class ProposedBlockTests
{
    [Test]
    public async Task TestShouldSendVoteMsgAndCommitGrandGrandParentBlockAsync()
    {
        var blockChain = await XdcTestBlockchain.Create(2, true);

        await blockChain.AddBlockWithoutCommitQc();

        //We wait here because HotStuff will cast a vote itself, and if we change the Signer before that, the test will fail
        await Task.Delay(200);

        var head = (XdcBlockHeader)blockChain.BlockTree.Head!.Header;
        var spec = blockChain.SpecProvider.GetXdcSpec(head, blockChain.XdcContext.CurrentRound);

        EpochSwitchInfo switchInfo = blockChain.EpochSwitchManager.GetEpochSwitchInfo(head)!;
        PrivateKey[] masternodes = blockChain.TakeRandomMasterNodes(spec, switchInfo);
        if (masternodes.Any((m) => m.Address == head.Beneficiary))
        {
            //If we randomly picked the block proposer we need to remove him with a another voting masternode
            var extraMaster = switchInfo.Masternodes.First((m) => m != head.Beneficiary && masternodes.Any(x => x.Address != m));
            var extraMasterkey = blockChain.MasterNodeCandidates.First(x => x.Address == extraMaster);
            masternodes = [.. masternodes.Where(x => x.Address != head.Beneficiary), extraMasterkey];
        }

        var newRoundWaitHandle = new TaskCompletionSource();
        blockChain.XdcContext.NewRoundSetEvent += (s, a) => { newRoundWaitHandle.SetResult(); };

        var votingBlock = new BlockRoundInfo(head.Hash!, blockChain.XdcContext.CurrentRound, head.Number);
        //We skip 1 vote so we are 1 under the vote threshold, proving that if the round advances the module cast a vote itself
        foreach (var key in masternodes.Skip(1))
        {
            //Need to set the signer before casting vote
            blockChain.Signer.SetSigner(key);
            await blockChain.VotesManager.CastVote(votingBlock);
        }

        var waitTask = await Task.WhenAny(newRoundWaitHandle.Task, Task.Delay(5_000));
        if (waitTask != newRoundWaitHandle.Task)
        {
            Assert.Fail("Timed out waiting for the round to start. The vote threshold was not reached?");
        }

        var parentOfHead = blockChain.BlockTree.FindHeader(head.ParentHash!);
        var grandParentOfHead = blockChain.BlockTree.FindHeader(parentOfHead!.ParentHash!);

        grandParentOfHead!.Hash!.Should().Be(blockChain.XdcContext.HighestCommitBlock.Hash);

    }

    [Test]
    public async Task TestShouldNotCommitIfRoundsNotContinousFor3Rounds()
    {
        var blockChain = await XdcTestBlockchain.Create(1, true);

        var newRoundWaitHandle = new TaskCompletionSource();
        blockChain.XdcContext.NewRoundSetEvent += (s, a) => { newRoundWaitHandle.SetResult(); };

        await blockChain.TriggerAndSimulateBlockProposalAndVoting();

        await newRoundWaitHandle.Task;
    }
}
