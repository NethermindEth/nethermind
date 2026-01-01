// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Crypto;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Test.Helpers;
using Nethermind.Xdc.Types;
using NUnit.Framework;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;

public class VoteTests
{
    [Test]
    public async Task HandleVote_SuccessfullyGenerateAndProcessQc()
    {
        var blockchain = await XdcTestBlockchain.Create();
        var votesManager = CreateVotesManager(blockchain);
        IXdcConsensusContext ctx = blockchain.XdcContext;

        // Add a new block with NO QC simulation
        var freshBlock = await blockchain.AddBlockWithoutCommitQc();
        var header = (freshBlock.Header as XdcBlockHeader)!;

        var currentBlockHash = freshBlock?.Hash!;
        IXdcReleaseSpec releaseSpec = blockchain.SpecProvider.GetXdcSpec(header, ctx.CurrentRound);
        var switchInfo = blockchain.EpochSwitchManager.GetEpochSwitchInfo(header)!;
        PrivateKey[] keys = blockchain.TakeRandomMasterNodes(releaseSpec, switchInfo);
        var blockInfo = new BlockRoundInfo(currentBlockHash, ctx.CurrentRound, freshBlock!.Number);
        ulong gap = 0ul;
        if (switchInfo.EpochSwitchBlockInfo.BlockNumber != 0)
        {
            ulong epochLength = checked((ulong)releaseSpec.EpochLength);
            ulong gapFromSpec = checked((ulong)releaseSpec.Gap);
            ulong epochStart = switchInfo.EpochSwitchBlockInfo.BlockNumber - (switchInfo.EpochSwitchBlockInfo.BlockNumber % epochLength);
            gap = epochStart > gapFromSpec ? epochStart - gapFromSpec : 0ul;
        }

        // Initial values to check
        var initialLockQc = ctx.LockQC;
        var initialRound = ctx.CurrentRound;
        var initialHighestQc = ctx.HighestQC;
        Assert.That(header.ExtraConsensusData!.BlockRound, Is.EqualTo(ctx.CurrentRound));

        // Amount of votes processed in this loop will be 1 less than the required to reach the threshold
        for (int i = 0; i < keys.Length - 1; i++)
        {
            var newVote = XdcTestHelper.BuildSignedVote(blockInfo, gap, keys[i]);
            await votesManager.HandleVote(newVote);
        }
        // Check same values as before: qc has not yet been processed, so there should be no changes
        ctx.LockQC.Should().BeEquivalentTo(initialLockQc);
        Assert.That(ctx.CurrentRound, Is.EqualTo(initialRound));
        ctx.HighestQC.Should().BeEquivalentTo(initialHighestQc);

        // Create another vote which is signed by someone not from the master node list
        var randomKey = blockchain.RandomKeys.First();
        var randomVote = XdcTestHelper.BuildSignedVote(blockInfo, gap, randomKey);
        await votesManager.HandleVote(randomVote);

        // Again same check: vote is not valid so threshold is not yet reached
        ctx.LockQC.Should().BeEquivalentTo(initialLockQc);
        Assert.That(ctx.CurrentRound, Is.EqualTo(initialRound));
        ctx.HighestQC.Should().BeEquivalentTo(initialHighestQc);

        // Create a vote message that should trigger qc processing and increment the round
        var lastVote = XdcTestHelper.BuildSignedVote(blockInfo, gap, keys.Last());
        await votesManager.HandleVote(lastVote);
        // Check new round has been set
        Assert.That(ctx.CurrentRound, Is.EqualTo(initialRound + 1));
        // The lockQC shall be the parent's QC round number
        Assert.That(ctx.LockQC, Is.Not.Null);
        Assert.That(ctx.LockQC.ProposedBlockInfo.Round, Is.EqualTo(initialRound - 1));
        // The highestQC proposedBlockInfo shall be the same as the one from its votes
        ctx.HighestQC.ProposedBlockInfo.Should().BeEquivalentTo(blockInfo);
    }

    private VotesManager CreateVotesManager(XdcTestBlockchain blockchain)
    {
        return new VotesManager(
            blockchain.XdcContext,
            blockchain.BlockTree,
            blockchain.EpochSwitchManager,
            blockchain.SnapshotManager,
            blockchain.QuorumCertificateManager,
            blockchain.SpecProvider,
            blockchain.Signer,
            NSubstitute.Substitute.For<IForensicsProcessor>());
    }
}
