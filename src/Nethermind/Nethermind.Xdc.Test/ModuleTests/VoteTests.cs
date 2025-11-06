// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Test.Helpers;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.ModuleTests;

public class VoteTests
{
    [Test]
    public async Task HandleVote_FirstV2Round_SuccessfullyGenerateAndProcessQc()
    {
        var blockchain = await XdcTestBlockchain.Create();
        var votesManager = CreateVotesManager(blockchain);
        IXdcConsensusContext ctx = blockchain.XdcContext;

        var currentBlock = blockchain.BlockTree.Head;
        var currentBlockHash = currentBlock?.Hash;
        var currentHeader = currentBlock?.Header as XdcBlockHeader;
        IXdcReleaseSpec releaseSpec = blockchain.SpecProvider.GetXdcSpec(currentHeader!, ctx.CurrentRound);
        var switchInfo = blockchain.EpochSwitchManager.GetEpochSwitchInfo(currentHeader);
        PrivateKey[] keys = blockchain.TakeRandomMasterNodes(releaseSpec, switchInfo!);
        Console.WriteLine(keys.Length);
        Console.WriteLine(switchInfo!.Masternodes.Length);
        var blockInfo = new BlockRoundInfo(currentBlockHash!, ctx.CurrentRound, currentBlock!.Number);

        // Initial values to check
        var initialLockQc = ctx.LockQC;
        var initialRound = ctx.CurrentRound;
        var initialHighestQc = ctx.HighestQC;

        // Amount of votes processed in this loop will be 1 less than the required to reach the threshold
        for (int i = 0; i < keys.Length - 2; i++)
        {
            var newVote = XdcTestHelper.BuildSignedVote(blockInfo, 450, keys[i]);
            await votesManager.HandleVote(newVote);
        }
        // Check same values as before: qc has not yet been processed, so there should be no changes
        ctx.LockQC.Should().BeEquivalentTo(initialLockQc);
        Assert.That(ctx.CurrentRound, Is.EqualTo(initialRound));
        ctx.HighestQC.Should().BeEquivalentTo(initialHighestQc);

        // Create a vote message that should trigger qc processing and increment the round
        var lastVote =  XdcTestHelper.BuildSignedVote(blockInfo, 450, keys.Last());
        await votesManager.HandleVote(lastVote);
        // Check new round has been set
        Assert.That(ctx.CurrentRound, Is.EqualTo(initialRound + 1));
        // The lockQC shall be the parent's QC round number
        Assert.That(ctx.LockQC, Is.Not.Null);
        Assert.That(ctx.LockQC.ProposedBlockInfo.Round, Is.EqualTo(initialRound - 1));
        // The highestQC proposedBlockInfo shall be the same as the one from its votes
        ctx.HighestQC.ProposedBlockInfo.Should().BeEquivalentTo(blockInfo);
    }

    // [Test]
    // public async Task HandleVote_SuccessfullyGenerateAndProcessQc()
    // {
    //     var testConfig = await XdcTestBlockchain.Create(905);
    //     var votesManager = testConfig.CreateVotesManager();
    //     PrivateKey[] keys = testConfig.GetCurrentKeys();
    //     IXdcConsensusContext ctx = testConfig.XdcContext;
    //     IBlockTree tree = testConfig.BlockTree;
    //
    //     var currentBlockHash = tree.Head?.Hash;
    //     var blockInfo = new BlockRoundInfo(currentBlockHash!, 5UL, 905);
    //     ctx.SetNewRound(5);
    //
    //     // Check initialized with nil and 0 round
    //     Assert.That(ctx.LockQC, Is.Null);
    //     Assert.That(ctx.CurrentRound, Is.EqualTo(5));
    //     Assert.That(ctx.HighestQC, Is.Not.Null);
    //     Assert.That(ctx.HighestQC.ProposedBlockInfo.Round, Is.EqualTo(0));
    //
    //     var threshold = Math.Ceiling(keys.Length * 0.67);
    //     int i = 0;
    //     for (int count = 1; count < threshold; i++, count++)
    //     {
    //         var newVote = XdcTestHelper.BuildSignedVote(blockInfo, 450, keys[i]);
    //         await votesManager.HandleVote(newVote);
    //     }
    //     // Same check as before, since qc has not been processed so no new round has been set
    //     Assert.That(ctx.LockQC, Is.Null);
    //     Assert.That(ctx.CurrentRound, Is.EqualTo(5));
    //     Assert.That(ctx.HighestQC.ProposedBlockInfo.Round, Is.EqualTo(0));
    //
    //     // Create another vote which is signed by someone not from the master node list
    //     var randomKeys = XdcTestHelper.MakeKeys(1);
    //     var randomKey = randomKeys[0];
    //     var randomVote = XdcTestHelper.BuildSignedVote(blockInfo, 450, randomKey);
    //     await votesManager.HandleVote(randomVote);
    //
    //     // Still same check as this vote does not pass validation so qc has not been processed
    //     Assert.That(ctx.LockQC, Is.Null);
    //     Assert.That(ctx.CurrentRound, Is.EqualTo(5));
    //     Assert.That(ctx.HighestQC.ProposedBlockInfo.Round, Is.EqualTo(0));
    //
    //     // Create a vote message that should trigger vote pool hook and increment the round
    //     var lastVote =  XdcTestHelper.BuildSignedVote(blockInfo, 450, keys[i]);
    //     await votesManager.HandleVote(lastVote);
    //     // The lockQC shall be the parent's QC round number
    //     Assert.That(ctx.LockQC, Is.Not.Null);
    //     Assert.That(ctx.LockQC.ProposedBlockInfo.Round, Is.EqualTo(4));
    //     // Check round has now changed from 5 to 6
    //     Assert.That(ctx.CurrentRound, Is.EqualTo(6));
    //     // The highestQC proposedBlockInfo shall be the same as the one from its votes
    //     ctx.HighestQC.ProposedBlockInfo.Should().BeEquivalentTo(blockInfo);
    //     //// Should trigger processQC and commit the grandparent of the highestQC block
    //     Assert.That(ctx.HighestCommitBlock.Round, Is.EqualTo(3));
    //     Assert.That(ctx.HighestCommitBlock.BlockNumber, Is.EqualTo(903));
    // }
    //
    // [Test]
    // public async Task HandleVote_ThenProcessTimeouts_CheckCorrectUpdateOfContext()
    // {
    //     var (tree, signer, ctx) = XdcTestHelper.PrepareXdcTestBlockChain(905);
    //     IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
    //     ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
    //     IDb db = Substitute.For<IDb>();
    //     ISpecProvider specProvider = Substitute.For<ISpecProvider>();
    //     IForensicsProcessor forensicsProcessor = Substitute.For<IForensicsProcessor>();
    //     IQuorumCertificateManager quorumCertificateManager =
    //         new QuorumCertificateManager(ctx, tree, specProvider, epochSwitchManager);
    //     var votesManager = new VotesManager(ctx, tree, epochSwitchManager, snapshotManager, quorumCertificateManager,
    //         specProvider, signer, forensicsProcessor);
    //
    //     //TODO: This keys should correspond to the master node addresses
    //     var keys = XdcTestHelper.MakeKeys(3);
    //
    //     var currentBlockHash = tree.Head?.Hash;
    //     var blockInfo = new BlockRoundInfo(currentBlockHash!, 5UL, 905);
    //     var vote = XdcTestHelper.BuildSignedVote(blockInfo, 450, signer.Key!);
    //     ctx.SetNewRound(tree, 5);
    //
    //     await votesManager.HandleVote(vote);
    //
    //     for (int i = 0; i < 2; i++)
    //     {
    //         var newVote = XdcTestHelper.BuildSignedVote(blockInfo, 450, keys[i]);
    //         await votesManager.HandleVote(newVote);
    //     }
    //     // Same check as before, since qc has not been processed so no new round has been set
    //     Assert.That(ctx.LockQC, Is.Null);
    //     Assert.That(ctx.CurrentRound, Is.EqualTo(5));
    //     Assert.That(ctx.HighestQC.ProposedBlockInfo.Round, Is.EqualTo(0));
    //
    //     // Create a vote message that should trigger vote pool hook and increment the round
    //     var lastVote =  XdcTestHelper.BuildSignedVote(blockInfo, 450, keys.Last());
    //     await votesManager.HandleVote(lastVote);
    //     // The lockQC shall be the parent's QC round number
    //     Assert.That(ctx.LockQC.ProposedBlockInfo.Round, Is.EqualTo(4));
    //     // Check round has now changed from 5 to 6
    //     Assert.That(ctx.CurrentRound, Is.EqualTo(6));
    //     // The highestQC proposedBlockInfo shall be the same as the one from its votes
    //     ctx.HighestQC.ProposedBlockInfo.Should().BeEquivalentTo(blockInfo);
    //     //// Should trigger processQC and commit the grandparent of the highestQC block
    //     Assert.That(ctx.HighestCommitBlock.Round, Is.EqualTo(3));
    //     Assert.That(ctx.HighestCommitBlock.BlockNumber, Is.EqualTo(903));
    // }

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
