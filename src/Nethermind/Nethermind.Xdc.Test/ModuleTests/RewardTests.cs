// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Test.Helpers;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.ModuleTests;

public class RewardTests
{
    [Test]
    public async Task TestHookRewardV2()
    {
        var chain = await XdcTestBlockchain.Create();
        var masternodeVotingContract = Substitute.For<IMasternodeVotingContract>();
        var rc = new XdcRewardCalculator(
            chain.EpochSwitchManager,
            chain.SpecProvider,
            chain.BlockTree,
            masternodeVotingContract
        );
        var head = (XdcBlockHeader)chain.BlockTree.Head!.Header;
        IXdcReleaseSpec spec = chain.SpecProvider.GetXdcSpec(head, chain.XdcContext.CurrentRound);
        var epochLength = spec.EpochLength;

        // Add blocks up to epochLength (E) + 15 and create a signing tx that will be inserted in the next block
        await chain.AddBlocks(epochLength + 15 - 3);
        var header915 = chain.BlockTree.Head!.Header as XdcBlockHeader;
        Assert.That(header915, Is.Not.Null);
        PrivateKey signer915 = GetSignerFromMasternodes(chain, header915, spec);
        Address owner = signer915.Address;
        masternodeVotingContract.GetCandidateOwner(Arg.Any<BlockHeader>(), signer915.Address).Returns(owner);
        await chain.AddBlock(BuildSigningTx(
            spec,
            header915.Number,
            header915.Hash ?? header915.CalculateHash().ToHash256(),
            signer915));

        // Add blocks up until 3E and evaluate rewards at this checkpoint
        // Save block 3E - 15 for second part of the test
        await chain.AddBlocks(2 * epochLength - 31);
        var header2685 = chain.BlockTree.Head!.Header as XdcBlockHeader;
        Assert.That(header2685, Is.Not.Null);
        PrivateKey signer2685 = GetSignerFromMasternodes(chain, header2685, spec);
        Address owner2 = signer2685.Address;
        masternodeVotingContract.GetCandidateOwner(Arg.Any<BlockHeader>(), signer2685.Address).Returns(owner2);
        // Continue adding blocks up until 3E
        await chain.AddBlocks(15);
        Block block2700 = chain.BlockTree.Head!;
        var header2700 = block2700.Header as XdcBlockHeader;
        Assert.That(header2700, Is.Not.Null);

        BlockReward[] rewardsAt2700 = rc.CalculateRewards(block2700);

        // Expect exactly 2 entries: one for the masternode owner and one for foundation
        Address foundation = spec.FoundationWallet;
        foundation.Should().NotBe(Address.Zero);

        Assert.That(rewardsAt2700, Has.Length.EqualTo(2));
        rewardsAt2700.Length.Should().Be(2);

        UInt256 total = rewardsAt2700.Aggregate(UInt256.Zero, (acc, r) => acc + r.Value);
        UInt256 ownerReward = rewardsAt2700.Single(r => r.Address == owner).Value;
        UInt256 foundationReward = rewardsAt2700.Single(r => r.Address == foundation).Value;

        // Check 90/10 split
        Assert.That(foundationReward, Is.EqualTo(total / 10));
        Assert.That(ownerReward, Is.EqualTo(total * 90 / 100));

        // === Second part of the test: signing hash in a different epoch still counts ===

        Transaction signingTx2 = BuildSigningTx(
            spec,
            header2685.Number,
            header2685.Hash!,
            signer2685);

        // Place signingTx2 in block 3E + 16 (different epoch than the signed block)
        await chain.AddBlocks(15);
        await chain.AddBlock(signingTx2);

        // Add blocks up until 4E and check rewards
        await chain.AddBlocks(epochLength - 16);
        Block block3600 = chain.BlockTree.Head!;
        var header3600 = block3600.Header as XdcBlockHeader;
        Assert.That(header3600, Is.Not.Null);
        BlockReward[] rewardsAt3600 = rc.CalculateRewards(block3600);

        // Same expectations: exactly two outputs with 90/10 split
        // Since this only counts signing txs from 2E to 3E, only signingTx2 should get counted
        Assert.That(rewardsAt3600, Has.Length.EqualTo(2));
        UInt256 total2 = rewardsAt3600.Aggregate(UInt256.Zero, (acc, r) => acc + r.Value);
        UInt256 ownerReward2 = rewardsAt3600.Single(r => r.Address == owner2).Value;
        UInt256 foundationReward2 = rewardsAt3600.Single(r => r.Address == foundation).Value;

        Assert.That(foundationReward2, Is.EqualTo(total2 / 10));
        Assert.That(ownerReward2, Is.EqualTo(total2 * 90 / 100));

        // === Third part of the test: if no signing tx, reward should be empty ===

        // Add blocks up to 5E and check rewards
        await chain.AddBlocks(epochLength);
        Block block4500 = chain.BlockTree.Head!;
        BlockReward[] rewardsAt4500 = rc.CalculateRewards(block4500);
        // If no valid signing txs were counted for that epoch, expect no rewards.
        rewardsAt4500.Should().BeEmpty();
    }

    [Test]
    public async Task TestHookRewardV2SplitReward()
    {
        var chain = await XdcTestBlockchain.Create();
        var masternodeVotingContract = Substitute.For<IMasternodeVotingContract>();
        var rc = new XdcRewardCalculator(
            chain.EpochSwitchManager,
            chain.SpecProvider,
            chain.BlockTree,
            masternodeVotingContract
        );

        var head = (XdcBlockHeader)chain.BlockTree.Head!.Header;
        IXdcReleaseSpec spec = chain.SpecProvider.GetXdcSpec(head, chain.XdcContext.CurrentRound);
        var epochLength = spec.EpochLength;

        // === Layout (mirrors Go test intent) ===
        // - Insert 1 signing tx for header (E + 15) signed by signerA into block (E + 16)
        // - Insert 2 signing txs (one for header (E + 15), one for header (2E - 15)) signed by signerB into block (2E - 1)
        // - Verify: rewards at (3E) split 1:2 between A:B with 90/10 owner/foundation

        // Move to block (E + 15)
        await chain.AddBlocks(epochLength + 15 - 3);
        var header915 = (XdcBlockHeader)chain.BlockTree.Head!.Header;

        EpochSwitchInfo? epochInfo = chain.EpochSwitchManager.GetEpochSwitchInfo(header915);
        Assert.That(epochInfo, Is.Not.Null);
        PrivateKey[] masternodes = chain.TakeRandomMasterNodes(spec, epochInfo);
        PrivateKey signerA = masternodes.First();
        PrivateKey signerB = masternodes.Last();
        Address ownerA = signerA.Address;
        Address ownerB = signerB.Address;
        masternodeVotingContract.GetCandidateOwner(Arg.Any<BlockHeader>(), signerA.Address).Returns(ownerA);
        masternodeVotingContract.GetCandidateOwner(Arg.Any<BlockHeader>(), signerB.Address).Returns(ownerB);

        // Insert 1 signing tx for header (E + 15) in block (E + 16)
        Transaction txA = BuildSigningTx(
            spec,
            header915.Number,
            header915.Hash ?? header915.CalculateHash().ToHash256(),
            signerA);
        await chain.AddBlock(txA); // advances to (E + 16)

        // Move to block (2E - 15) to capture that header as well
        var currentNumber = chain.BlockTree.Head!.Number;
        await chain.AddBlocks(epochLength - 31);
        var header1785 = (XdcBlockHeader)chain.BlockTree.Head!.Header;

        // Prepare two signing txs signed by signerB:
        // - for header (E + 15)
        // - for header (2E - 15)
        Transaction txB1 = BuildSigningTx(
            spec,
            header915.Number,
            header915.Hash!,
            signerB);

        Transaction txB2 = BuildSigningTx(
            spec,
            header1785.Number,
            header1785.Hash!,
            signerB,
            1);

        // Advance to (2E - 2), then add a block with both signerB txs to be at (2E - 1)
        await chain.AddBlocks(13);
        await chain.AddBlock(txB1, txB2); // now at (2E - 1)

        // Rewards at (3E) should exist with split 1:2 across A:B and 90/10 owner/foundation
        await chain.AddBlocks(epochLength + 1); // from (2E - 1) to (3E)
        Block block2700 = chain.BlockTree.Head!;
        BlockReward[] rewards = rc.CalculateRewards(block2700);

        Address foundation = spec.FoundationWallet;

        // Expect exactly 3 entries: ownerA, ownerB, foundation.
        Assert.That(rewards.Length, Is.EqualTo(3));

        // Calculate exact rewards for each address
        UInt256 totalRewards = (UInt256)spec.Reward * Unit.Ether;
        UInt256 signerAReward = totalRewards / 3;
        UInt256 ownerAReward = signerAReward * 90 / 100;
        UInt256 foundationReward = signerAReward / 10;
        UInt256 signerBReward = totalRewards * 2 / 3;
        UInt256 ownerBReward = signerBReward * 90 / 100;
        foundationReward += signerBReward / 10;

        foreach (BlockReward reward in rewards)
        {
            if (reward.Address == ownerA) Assert.That(reward.Value, Is.EqualTo(ownerAReward));
            if (reward.Address == ownerB) Assert.That(reward.Value, Is.EqualTo(ownerBReward));
            if (reward.Address == foundation) Assert.That(reward.Value, Is.EqualTo(foundationReward));
        }
    }

    private static Transaction BuildSigningTx(IXdcReleaseSpec spec, long blockNumber, Hash256 blockHash, PrivateKey signer, long nonce = 0)
    {
        return Build.A.Transaction
            .WithChainId(0)
            .WithNonce((UInt256)nonce)
            .WithGasLimit(200000)
            .WithXdcSigningData(blockNumber, blockHash)
            .ToBlockSignerContract(spec)
            .SignedAndResolved(signer)
            .TestObject;
    }

    private static PrivateKey GetSignerFromMasternodes(XdcTestBlockchain chain, XdcBlockHeader header, IXdcReleaseSpec spec)
    {
        EpochSwitchInfo? epochInfo = chain.EpochSwitchManager.GetEpochSwitchInfo(header);
        Assert.That(epochInfo, Is.Not.Null);
        return chain.TakeRandomMasterNodes(spec, epochInfo).First();
    }
}
