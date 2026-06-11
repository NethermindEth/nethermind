// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Test.Helpers;
using Nethermind.Xdc.Types;
using NUnit.Framework;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test.ModuleTests;

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
        ulong epochSwitchNumber = switchInfo.EpochSwitchBlockInfo.BlockNumber;
        ulong offset = epochSwitchNumber % spec.EpochLength + spec.Gap;
        ulong gapNumber = epochSwitchNumber.SaturatingSub(offset);
        //We skip 1 vote so we are 1 under the vote threshold, proving that if the round advances the module cast a vote itself
        foreach (PrivateKey? key in masternodes.Skip(1))
        {
            Vote vote = XdcTestHelper.BuildSignedVote(votingBlock, gapNumber, key);
            await blockChain.VotesManager.HandleVote(vote);
        }

        TaskCompletionSource newRoundWaitHandle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        blockChain.XdcContext.NewRoundSetEvent += (s, a) => { newRoundWaitHandle.SetResult(); };

        //Set current signer as the one that didn't vote
        blockChain.Signer.SetSigner(masternodes.First());

        // Align timestamper with block timestamps so TryPropose does not block on the mine-period gate
        blockChain.Timestamper.Set(DateTimeOffset.FromUnixTimeSeconds((long)(head.Timestamp + spec.MinePeriod)).UtcDateTime);

        //Starting here will trigger the final vote to be cast and round should advance
        blockChain.StartHotStuffModule();

        Task waitTask = await Task.WhenAny(newRoundWaitHandle.Task, Task.Delay(5_000));
        if (waitTask != newRoundWaitHandle.Task)
        {
            Assert.Fail("Timed out waiting for the round to start. The vote threshold was not reached?");
        }

        BlockHeader? parentOfHead = blockChain.BlockTree.FindHeader(head.ParentHash!);
        BlockHeader? grandParentOfHead = blockChain.BlockTree.FindHeader(parentOfHead!.ParentHash!);

        Assert.That(grandParentOfHead!.Hash!, Is.EqualTo(blockChain.XdcContext.HighestCommitBlock!.Hash));
    }

    [Test]
    public async Task TestShouldNotCommitIfRoundsNotContinousFor3Rounds()
    {
        using XdcTestBlockchain blockChain = await XdcTestBlockchain.Create(2, true);

        await blockChain.AddBlock();

        blockChain.StartHotStuffModule();

        await blockChain.TriggerBlockProposal();

        await blockChain.SimulateVoting();

        BlockRoundInfo beforeTimeoutFinalized = blockChain.XdcContext.HighestCommitBlock!;
        QuorumCertificate beforeTimeoutQC = blockChain.XdcContext.HighestQC;

        //Simulate timeout
        blockChain.XdcContext.SetNewRound();

        await blockChain.TriggerBlockProposal();

        await blockChain.SimulateVoting();

        Assert.That(blockChain.XdcContext.HighestCommitBlock, Is.EqualTo(beforeTimeoutFinalized));
        Assert.That(blockChain.XdcContext.HighestQC, Is.Not.EqualTo(beforeTimeoutQC));
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
        ulong epochSwitchNumber = switchInfo.EpochSwitchBlockInfo.BlockNumber;
        ulong offset = epochSwitchNumber % spec.EpochLength + spec.Gap;
        ulong gapNumber = epochSwitchNumber.SaturatingSub(offset);
        //We skip 1 vote so we are 1 under the vote threshold
        foreach (PrivateKey? key in masternodes.Skip(1))
        {
            Vote vote = XdcTestHelper.BuildSignedVote(votingBlock, gapNumber, key);
            await blockChain.VotesManager.HandleVote(vote);
        }

        QuorumCertificate beforeFinalVote = blockChain.XdcContext.HighestQC!;
        //Our highest QC should be 1 number behind head
        // Our highest QC should be 1 number behind head
        Assert.That(beforeFinalVote.ProposedBlockInfo.BlockNumber, Is.EqualTo(head.Number - 1));

        TaskCompletionSource newRoundWaitHandle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        blockChain.XdcContext.NewRoundSetEvent += (s, a) => { newRoundWaitHandle.SetResult(); };

        //Set current signer as the one that didn't vote
        blockChain.Signer.SetSigner(masternodes.First());

        // Align timestamper with block timestamps so TryPropose does not block on the mine-period gate
        blockChain.Timestamper.Set(DateTimeOffset.FromUnixTimeSeconds((long)(head.Timestamp + spec.MinePeriod)).UtcDateTime);

        //Starting here will trigger the final vote to be cast
        blockChain.StartHotStuffModule();

        Task waitTask = await Task.WhenAny(newRoundWaitHandle.Task, Task.Delay(10_000));
        if (waitTask != newRoundWaitHandle.Task)
        {
            Assert.Fail("Timed out waiting for the round to start. The vote threshold was not reached?");
        }

        Assert.That(blockChain.XdcContext.HighestQC!.ProposedBlockInfo.Hash, Is.EqualTo(head.Hash!));
    }

    [TestCase(1)]
    [TestCase(10)]
    [TestCase(30)]
    public async Task CanBuildAFinalizedChain(int count)
    {
        using XdcTestBlockchain blockChain = await XdcTestBlockchain.Create(0, true);
        blockChain.ChangeReleaseSpec((s) =>
        {
            s.EpochLength = 90UL;
            s.Gap = 45UL;
        });

        await blockChain.AddBlocks(3);

        blockChain.StartHotStuffModule();

        BlockHeader startBlock = blockChain.BlockTree.Head!.Header;

        for (ulong i = 1; i <= (ulong)count; i++)
        {
            await blockChain.TriggerAndSimulateBlockProposalAndVoting();
            Assert.That(blockChain.BlockTree.Head.Number, Is.EqualTo(startBlock.Number + i));
            Assert.That(blockChain.XdcContext.HighestQC!.ProposedBlockInfo.BlockNumber, Is.EqualTo(startBlock.Number + i));
            Assert.That(blockChain.XdcContext.HighestCommitBlock!.BlockNumber, Is.EqualTo(blockChain.XdcContext.HighestQC!.ProposedBlockInfo.BlockNumber - 2UL));
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
        ulong epochSwitchNumber = switchInfo.EpochSwitchBlockInfo.BlockNumber;
        ulong offset = epochSwitchNumber % spec.EpochLength + spec.Gap;
        ulong gapNumber = epochSwitchNumber.SaturatingSub(offset);
        //We skip 1 vote so we are 1 under the vote threshold
        foreach (PrivateKey? key in masternodes.Skip(1))
        {
            Vote vote = XdcTestHelper.BuildSignedVote(votingBlock, gapNumber, key);
            await blockChain.VotesManager.HandleVote(vote);
        }

        //Setting the signer to a non master node
        blockChain.Signer.SetSigner(TestItem.PrivateKeyA);

        ulong roundCountBeforeStart = blockChain.XdcContext.CurrentRound;

        //Should not cause any new vote to be cast
        blockChain.StartHotStuffModule();

        await Task.Delay(100);

        Assert.That(blockChain.XdcContext.CurrentRound, Is.EqualTo(roundCountBeforeStart));
    }
}
