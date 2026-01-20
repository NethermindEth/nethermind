// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
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
    // Test ported from XDC reward_test :
    // https://github.com/XinFinOrg/XDPoSChain/blob/af4178b2c7f9d668d8ba1f3a0244606a20ce303d/consensus/tests/engine_v2_tests/reward_test.go#L18
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
        foundation.Should().NotBeNull();

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

    // Test ported from XDC reward_test :
    // https://github.com/XinFinOrg/XDPoSChain/blob/af4178b2c7f9d668d8ba1f3a0244606a20ce303d/consensus/tests/engine_v2_tests/reward_test.go#L99
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

        // - Insert 1 signing tx for header (E + 15) signed by signerA into block (E + 16)
        // - Insert 2 signing txs (one for header (E + 15), one for header (2E - 15)) signed by signerB into block (2E - 1)
        // - Verify: rewards at (3E) split 1:2 between A:B with 90/10 owner/foundation

        // Move to block (E + 15)
        await chain.AddBlocks(epochLength + 15 - 3);
        var header915 = (XdcBlockHeader)chain.BlockTree.Head!.Header;

        EpochSwitchInfo? epochInfo = chain.EpochSwitchManager.GetEpochSwitchInfo(header915);
        Assert.That(epochInfo, Is.Not.Null);
        PrivateKey[] masternodes = chain.TakeRandomMasterNodes(spec, epochInfo);
        PrivateKey signerA = GetSignerFromMasternodes(chain, header915, spec);
        Address ownerA = signerA.Address;
        masternodeVotingContract.GetCandidateOwner(Arg.Any<BlockHeader>(), signerA.Address).Returns(ownerA);

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


        await chain.AddBlocks(13);

        PrivateKey signerB = GetSignerFromMasternodes(chain, header1785, spec);
        Address ownerB = signerB.Address;

        var signer = (Signer)chain.Signer;
        signer.SetSigner(signerB);

        masternodeVotingContract.GetCandidateOwner(Arg.Any<BlockHeader>(), signerB.Address).Returns(ownerB);
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

    // Test to check calculated rewards against precalculated values from :
    // https://github.com/XinFinOrg/XDPoSChain/blob/af4178b2c7f9d668d8ba1f3a0244606a20ce303d/consensus/tests/engine_v2_tests/reward_test.go#L147
    [Test]
    public void RewardCalculator_SplitReward_MatchesRounding()
    {
        const long epoch = 45, reward = 250;
        PrivateKey[] masternodes = XdcTestHelper.GeneratePrivateKeys(2);
        PrivateKey signerA = masternodes.First();
        PrivateKey signerB = masternodes.Last();
        var foundationWalletAddr = new Address("0x0000000000000000000000000000000000000068");
        var blockSignerContract = new Address("0x0000000000000000000000000000000000000089");

        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        epochSwitchManager.IsEpochSwitchAtBlock(Arg.Any<XdcBlockHeader>())
            .Returns(ci => ((XdcBlockHeader)ci.Args()[0]!).Number % epoch == 0);

        IXdcReleaseSpec xdcSpec = Substitute.For<IXdcReleaseSpec>();
        xdcSpec.EpochLength.Returns((int)epoch);
        xdcSpec.FoundationWallet.Returns(foundationWalletAddr);
        xdcSpec.BlockSignerContract.Returns(blockSignerContract);
        xdcSpec.Reward.Returns(reward);
        xdcSpec.SwitchBlock.Returns(0);
        xdcSpec.MergeSignRange = 15;
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcSpec);

        IBlockTree tree = Substitute.For<IBlockTree>();
        long size = 2 * epoch + 1;
        var blockHeaders = new XdcBlockHeader[size];
        var blocks = new Block[size];
        for (int i = 0; i <= epoch * 2; i++)
        {
            blockHeaders[i] = Build.A.XdcBlockHeader()
                .WithNumber(i)
                .WithValidators(masternodes.Select(m => m.Address).ToArray())
                .WithExtraConsensusData(new ExtraFieldsV2((ulong)i, Build.A.QuorumCertificate().TestObject))
                .TestObject;
            blocks[i] = new Block(blockHeaders[i]);
        }

        // SignerA signs blocks 15 and 30
        // SignerB signs blocks 15
        var txsBlock16 = new List<Transaction>();
        txsBlock16.Add(BuildSigningTx(xdcSpec, 15, blockHeaders[15].Hash!, signerA, 1));
        txsBlock16.Add(BuildSigningTx(xdcSpec, 15, blockHeaders[15].Hash!, signerB, 2));
        var txsBlock31 = new List<Transaction>();
        txsBlock31.Add(BuildSigningTx(xdcSpec, 30, blockHeaders[30].Hash!, signerA, 3));
        blocks[16] = new Block(blockHeaders[16], new BlockBody(txsBlock16.ToArray(), null, null));
        blocks[31] = new Block(blockHeaders[31], new BlockBody(txsBlock31.ToArray(), null, null));
        tree.FindHeader(Arg.Any<Hash256>(), Arg.Any<long>())
            .Returns(ci => blockHeaders[(long)ci.Args()[1]]);
        tree.FindBlock(Arg.Any<long>())
            .Returns(ci => blocks[(long)ci.Args()[0]]);

        IMasternodeVotingContract votingContract = Substitute.For<IMasternodeVotingContract>();
        votingContract.GetCandidateOwner(Arg.Any<BlockHeader>(), Arg.Any<Address>())
            .Returns(ci => ci.Arg<Address>());

        var rewardCalculator = new XdcRewardCalculator(epochSwitchManager, specProvider, tree, votingContract);
        BlockReward[] rewards = rewardCalculator.CalculateRewards(blocks.Last());

        // Expect ownerA, ownerB, and foundation
        Assert.That(rewards, Has.Length.EqualTo(3));

        // Expected values from XDC repo:
        // A gets 2/3 of total, then 90% owner, 10% foundation (flooring at each integer division step)
        // B gets 1/3 of total, then 90% owner, 10% foundation
        var aOwnerExpected = UInt256.Parse("149999999999999999999");
        var aFoundExpected = UInt256.Parse("16666666666666666666");
        var bOwnerExpected = UInt256.Parse("74999999999999999999");
        var bFoundExpected = UInt256.Parse("8333333333333333333");

        UInt256 aOwnerReward = rewards.Single(r => r.Address == signerA.Address).Value;
        UInt256 bOwnerReward = rewards.Single(r => r.Address == signerB.Address).Value;
        UInt256 foundationReward = rewards.Single(r => r.Address == foundationWalletAddr).Value;

        Assert.That(foundationReward, Is.EqualTo(aFoundExpected + bFoundExpected));
        Assert.That(aOwnerReward, Is.EqualTo(aOwnerExpected));
        Assert.That(bOwnerReward, Is.EqualTo(bOwnerExpected));

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
        return chain.Signer.Key!;
    }
}
