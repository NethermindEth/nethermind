// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Synchronization.Peers;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Test.Helpers;
using Nethermind.Xdc.Types;
using NSubstitute;
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
        using XdcTestBlockchain blockchain = await XdcTestBlockchain.Create();
        VotesManager votesManager = CreateVotesManager(blockchain);
        IXdcConsensusContext ctx = blockchain.XdcContext;

        // Add a new block with NO QC simulation
        Block freshBlock = await blockchain.AddBlockWithoutCommitQc();
        XdcBlockHeader header = (freshBlock.Header as XdcBlockHeader)!;

        Hash256 currentBlockHash = freshBlock?.Hash!;
        IXdcReleaseSpec releaseSpec = blockchain.SpecProvider.GetXdcSpec(header, ctx.CurrentRound);
        EpochSwitchInfo switchInfo = blockchain.EpochSwitchManager.GetEpochSwitchInfo(header)!;
        PrivateKey[] keys = blockchain.TakeRandomMasterNodes(releaseSpec, switchInfo);
        BlockRoundInfo blockInfo = new(currentBlockHash, ctx.CurrentRound, freshBlock!.Number);
        ulong gap = (ulong)Math.Max(0, switchInfo.EpochSwitchBlockInfo.BlockNumber - switchInfo.EpochSwitchBlockInfo.BlockNumber % releaseSpec.EpochLength - releaseSpec.Gap);

        // Initial values to check
        QuorumCertificate? initialLockQc = ctx.LockQC;
        ulong initialRound = ctx.CurrentRound;
        QuorumCertificate initialHighestQc = ctx.HighestQC;
        Assert.That(header.ExtraConsensusData!.BlockRound, Is.EqualTo(ctx.CurrentRound));

        // Amount of votes processed in this loop will be 1 less than the required to reach the threshold
        for (int i = 0; i < keys.Length - 1; i++)
        {
            Vote newVote = XdcTestHelper.BuildSignedVote(blockInfo, gap, keys[i]);
            await votesManager.HandleVote(newVote);
        }
        // Check same values as before: qc has not yet been processed, so there should be no changes
        ctx.LockQC.Should().BeEquivalentTo(initialLockQc);
        Assert.That(ctx.CurrentRound, Is.EqualTo(initialRound));
        ctx.HighestQC.Should().BeEquivalentTo(initialHighestQc);

        // Create another vote which is signed by someone not from the master node list
        PrivateKey randomKey = blockchain.RandomKeys.First();
        Vote randomVote = XdcTestHelper.BuildSignedVote(blockInfo, gap, randomKey);
        await votesManager.HandleVote(randomVote);

        // Again same check: vote is not valid so threshold is not yet reached
        ctx.LockQC.Should().BeEquivalentTo(initialLockQc);
        Assert.That(ctx.CurrentRound, Is.EqualTo(initialRound));
        ctx.HighestQC.Should().BeEquivalentTo(initialHighestQc);

        // Create a vote message that should trigger qc processing and increment the round
        Vote lastVote = XdcTestHelper.BuildSignedVote(blockInfo, gap, keys.Last());
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
            Substitute.For<ISyncPeerPool>(),
            blockchain.BlockTree,
            blockchain.EpochSwitchManager,
            blockchain.SnapshotManager,
            blockchain.QuorumCertificateManager,
            blockchain.SpecProvider,
            blockchain.Signer,
            Substitute.For<IForensicsProcessor>());
    }
}
