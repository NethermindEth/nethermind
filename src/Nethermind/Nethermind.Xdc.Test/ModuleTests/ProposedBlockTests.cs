// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Test.Helpers;
using Nethermind.Xdc.Types;
using NUnit.Framework;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;

internal class ProposedBlockTests
{
    [Test]
    public async Task TestShouldSendVoteMsgAndCommitGreatGrandparentBlockAsync()
    {
        using XdcTestBlockchain blockChain = await XdcTestBlockchain.Create(2, true);

        await blockChain.AddBlockWithoutCommitQc();

        XdcBlockHeader head = (XdcBlockHeader)blockChain.BlockTree.Head!.Header;
        IXdcReleaseSpec spec = blockChain.SpecProvider.GetXdcSpec(head, blockChain.XdcContext.CurrentRound);

        EpochSwitchInfo switchInfo = blockChain.EpochSwitchManager.GetEpochSwitchInfo(head)!;
        PrivateKey[] masternodes = blockChain.TakeRandomMasterNodes(spec, switchInfo);

        BlockRoundInfo votingBlock = new(head.Hash!, blockChain.XdcContext.CurrentRound, head.Number);
        long gapNumber = switchInfo.EpochSwitchBlockInfo.BlockNumber == 0 ? 0 : Math.Max(0, switchInfo.EpochSwitchBlockInfo.BlockNumber - switchInfo.EpochSwitchBlockInfo.BlockNumber % spec.EpochLength - spec.Gap);
        //We skip 1 vote so we are 1 under the vote threshold, proving that if the round advances the module cast a vote itself
        foreach (PrivateKey? key in masternodes.Skip(1))
        {
            Vote vote = XdcTestHelper.BuildSignedVote(votingBlock, (ulong)gapNumber, key);
            await blockChain.VotesManager.HandleVote(vote);
        }

        TaskCompletionSource newRoundWaitHandle = new();
        blockChain.XdcContext.NewRoundSetEvent += (s, a) => { newRoundWaitHandle.SetResult(); };

        //Set current signer as the one that didn't vote
        blockChain.Signer.SetSigner(masternodes.First());

        //Starting here will trigger the final vote to be cast and round should advance
        blockChain.StartHotStuffModule();

        Task waitTask = await Task.WhenAny(newRoundWaitHandle.Task, Task.Delay(5_000));
        if (waitTask != newRoundWaitHandle.Task)
        {
            Assert.Fail("Timed out waiting for the round to start. The vote threshold was not reached?");
        }

        BlockHeader? parentOfHead = blockChain.BlockTree.FindHeader(head.ParentHash!);
        BlockHeader? grandParentOfHead = blockChain.BlockTree.FindHeader(parentOfHead!.ParentHash!);

        grandParentOfHead!.Hash!.Should().Be(blockChain.XdcContext.HighestCommitBlock.Hash);
    }

    [Test]
    public async Task TestShouldNotCommitIfRoundsNotContinousFor3Rounds()
    {
        using XdcTestBlockchain blockChain = await XdcTestBlockchain.Create(2, true);

        await blockChain.AddBlock();

        blockChain.StartHotStuffModule();

        await blockChain.TriggerBlockProposal();

        await blockChain.SimulateVoting();

        BlockRoundInfo beforeTimeoutFinalized = blockChain.XdcContext.HighestCommitBlock;
        QuorumCertificate beforeTimeoutQC = blockChain.XdcContext.HighestQC;

        //Simulate timeout
        blockChain.XdcContext.SetNewRound();

        await blockChain.TriggerBlockProposal();

        await blockChain.SimulateVoting();

        blockChain.XdcContext.HighestCommitBlock.Should().Be(beforeTimeoutFinalized);
        blockChain.XdcContext.HighestQC.Should().NotBe(beforeTimeoutQC);
    }

    [Test]
    public async Task TestProposedBlockMessageHandlerSuccessfullyGenerateVote()
    {
        using XdcTestBlockchain blockChain = await XdcTestBlockchain.Create(2, true);

        await blockChain.AddBlockWithoutCommitQc();

        XdcBlockHeader head = (XdcBlockHeader)blockChain.BlockTree.Head!.Header;
        IXdcReleaseSpec spec = blockChain.SpecProvider.GetXdcSpec(head, blockChain.XdcContext.CurrentRound);

        EpochSwitchInfo switchInfo = blockChain.EpochSwitchManager.GetEpochSwitchInfo(head)!;

        PrivateKey[] masternodes = blockChain.TakeRandomMasterNodes(spec, switchInfo);

        BlockRoundInfo votingBlock = new(head.Hash!, blockChain.XdcContext.CurrentRound, head.Number);
        long gapNumber = switchInfo.EpochSwitchBlockInfo.BlockNumber == 0 ? 0 : Math.Max(0, switchInfo.EpochSwitchBlockInfo.BlockNumber - switchInfo.EpochSwitchBlockInfo.BlockNumber % spec.EpochLength - spec.Gap);
        //We skip 1 vote so we are 1 under the vote threshold
        foreach (PrivateKey? key in masternodes.Skip(1))
        {
            Vote vote = XdcTestHelper.BuildSignedVote(votingBlock, (ulong)gapNumber, key);
            await blockChain.VotesManager.HandleVote(vote);
        }

        QuorumCertificate beforeFinalVote = blockChain.XdcContext.HighestQC!;
        //Our highest QC should be 1 number behind head
        beforeFinalVote.ProposedBlockInfo.BlockNumber.Should().Be(head.Number - 1);

        TaskCompletionSource newRoundWaitHandle = new();
        blockChain.XdcContext.NewRoundSetEvent += (s, a) => { newRoundWaitHandle.SetResult(); };

        //Set current signer as the one that didn't vote
        blockChain.Signer.SetSigner(masternodes.First());

        //Starting here will trigger the final vote to be cast
        blockChain.StartHotStuffModule();

        Task waitTask = await Task.WhenAny(newRoundWaitHandle.Task, Task.Delay(10_000));
        if (waitTask != newRoundWaitHandle.Task)
        {
            Assert.Fail("Timed out waiting for the round to start. The vote threshold was not reached?");
        }

        blockChain.XdcContext.HighestQC!.ProposedBlockInfo.Hash.Should().Be(head.Hash!);
    }

    [TestCase(1)]
    [TestCase(10)]
    [TestCase(30)]
    public async Task CanBuildAFinalizedChain(int count)
    {
        using XdcTestBlockchain blockChain = await XdcTestBlockchain.Create(0, true);
        blockChain.ChangeReleaseSpec((s) =>
        {
            s.EpochLength = 90;
            s.Gap = 45;
        });

        await blockChain.AddBlocks(3);

        blockChain.StartHotStuffModule();

        BlockHeader startBlock = blockChain.BlockTree.Head!.Header;

        for (int i = 1; i <= count; i++)
        {
            await blockChain.TriggerAndSimulateBlockProposalAndVoting();
            blockChain.BlockTree.Head.Number.Should().Be(startBlock.Number + i);
            blockChain.XdcContext.HighestQC!.ProposedBlockInfo.BlockNumber.Should().Be(startBlock.Number + i);
            blockChain.XdcContext.HighestCommitBlock.BlockNumber.Should().Be(blockChain.XdcContext.HighestQC!.ProposedBlockInfo.BlockNumber - 2);
        }
    }

    [Test]
    public async Task TestProposedBlockMessageHandlerNotGenerateVoteIfSignerNotInMNlist()
    {
        using XdcTestBlockchain blockChain = await XdcTestBlockchain.Create(2, true);

        await blockChain.AddBlockWithoutCommitQc();

        XdcBlockHeader head = (XdcBlockHeader)blockChain.BlockTree.Head!.Header;
        IXdcReleaseSpec spec = blockChain.SpecProvider.GetXdcSpec(head, blockChain.XdcContext.CurrentRound);

        EpochSwitchInfo switchInfo = blockChain.EpochSwitchManager.GetEpochSwitchInfo(head)!;
        PrivateKey[] masternodes = blockChain.TakeRandomMasterNodes(spec, switchInfo);
        if (masternodes.Any((m) => m.Address == head.Beneficiary))
        {
            //If we randomly picked the block proposer we need to remove him with a another voting masternode
            Address extraMaster = switchInfo.Masternodes.First((m) => m != head.Beneficiary && masternodes.Any(x => x.Address != m));
            PrivateKey extraMasterKey = blockChain.MasterNodeCandidates.First(x => x.Address == extraMaster);
            masternodes = [.. masternodes.Where(x => x.Address != head.Beneficiary), extraMasterKey];
        }

        BlockRoundInfo votingBlock = new(head.Hash!, blockChain.XdcContext.CurrentRound, head.Number);
        long gapNumber = switchInfo.EpochSwitchBlockInfo.BlockNumber == 0 ? 0 : Math.Max(0, switchInfo.EpochSwitchBlockInfo.BlockNumber - switchInfo.EpochSwitchBlockInfo.BlockNumber % spec.EpochLength - spec.Gap);
        //We skip 1 vote so we are 1 under the vote threshold
        foreach (PrivateKey? key in masternodes.Skip(1))
        {
            Vote vote = XdcTestHelper.BuildSignedVote(votingBlock, (ulong)gapNumber, key);
            await blockChain.VotesManager.HandleVote(vote);
        }

        //Setting the signer to a non master node
        blockChain.Signer.SetSigner(TestItem.PrivateKeyA);

        ulong roundCountBeforeStart = blockChain.XdcContext.CurrentRound;

        //Should not cause any new vote to be cast
        blockChain.StartHotStuffModule();

        await Task.Delay(100);

        blockChain.XdcContext.CurrentRound.Should().Be(roundCountBeforeStart);
    }
}
