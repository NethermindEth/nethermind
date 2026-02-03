// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
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

[NonParallelizable]
public class RewardTests
{
    // Test ported from XDC reward_test :
    // https://github.com/XinFinOrg/XDPoSChain/blob/af4178b2c7f9d668d8ba1f3a0244606a20ce303d/consensus/tests/engine_v2_tests/reward_test.go#L18
    [Test]
    public async Task TestHookRewardV2()
    {
        var chain = await XdcTestBlockchain.Create();
        var masternodeVotingContract = Substitute.For<IMasternodeVotingContract>();

        chain.ChangeReleaseSpec(spec =>
        {
            spec.EpochLength = 50;
            spec.MergeSignRange = 5;
            spec.MergeSignRange = System.Math.Min(spec.MergeSignRange, spec.EpochLength / 2);
            if (spec.MergeSignRange < 1) spec.MergeSignRange = 1;
        });

        masternodeVotingContract
            .GetCandidateOwner(Arg.Any<BlockHeader>(), Arg.Any<Address>())
            .Returns(ci => ci.ArgAt<Address>(1));

        var rewardCalculator = new XdcRewardCalculator(
            chain.EpochSwitchManager,
            chain.SpecProvider,
            chain.BlockTree,
            masternodeVotingContract
        );

        var head = (XdcBlockHeader)chain.BlockTree.Head!.Header;
        IXdcReleaseSpec spec = chain.SpecProvider.GetXdcSpec(head, chain.XdcContext.CurrentRound);

        long epochLength = spec.EpochLength;
        long mergeSignRange = spec.MergeSignRange;

        long initialHeadNumber = chain.BlockTree.Head!.Number;

        // --- Part 1: create signing tx for header (E + mergeSignRange) included in next block ---

        long targetSignedHeaderNumberEPlusMerge = epochLength + mergeSignRange;
        await chain.AddBlocks((int)(targetSignedHeaderNumberEPlusMerge - initialHeadNumber));

        var signedHeaderEPlusMerge = chain.BlockTree.Head!.Header as XdcBlockHeader;
        Assert.That(signedHeaderEPlusMerge, Is.Not.Null);

        // Note: SubmitTransactionSign signs with DI `ISigner`, not necessarily `chain.Signer`
        await chain.AddBlock(BuildSigningTx(
            spec,
            signedHeaderEPlusMerge.Number,
            signedHeaderEPlusMerge.Hash ?? signedHeaderEPlusMerge.CalculateHash().ToHash256(),
            chain.Signer.Key!,
            (long)chain.ReadOnlyState.GetNonce(chain.Signer.Address)));

        // --- Move to header (3E - mergeSignRange) to sign it later in Part 2 ---

        long blockNumberAfterIncludingSignTx = chain.BlockTree.Head!.Number;
        long targetSignedHeader3EMinusMerge = 3 * epochLength - mergeSignRange;
        await chain.AddBlocks((int)(targetSignedHeader3EMinusMerge - blockNumberAfterIncludingSignTx));

        var signedHeader3EMinusMerge = chain.BlockTree.Head!.Header as XdcBlockHeader;
        Assert.That(signedHeader3EMinusMerge, Is.Not.Null);

        // --- Evaluate rewards at checkpoint (3E) ---

        long headBeforeCheckpoint = chain.BlockTree.Head!.Number;
        long checkpoint3E = 3 * epochLength;
        await chain.AddBlocks((int)(checkpoint3E - headBeforeCheckpoint));

        Block block3E = chain.BlockTree.Head!;
        var header3E = block3E.Header as XdcBlockHeader;
        Assert.That(header3E, Is.Not.Null);

        BlockReward[] rewardsAt3E = rewardCalculator.CalculateRewards(block3E);

        Address foundation = spec.FoundationWallet;
        foundation.Should().NotBeNull();

        Assert.That(rewardsAt3E, Has.Length.EqualTo(2));

        UInt256 totalAt3E = rewardsAt3E.Aggregate(UInt256.Zero, (acc, r) => acc + r.Value);
        UInt256 foundationRewardAt3E = rewardsAt3E.Single(r => r.Address == foundation).Value;
        UInt256 ownerRewardAt3E = rewardsAt3E.Single(r => r.Address != foundation).Value;

        // Check 90/10 split on totals
        Assert.That(foundationRewardAt3E, Is.EqualTo(totalAt3E / 10));
        Assert.That(ownerRewardAt3E, Is.EqualTo(totalAt3E * 90 / 100));

        // === Part 2: signing hash in a different epoch still counts ===

        // Place signing tx for the previously captured (3E - mergeSignRange) header in block (3E + mergeSignRange + 1)
        long targetIncludingBlockForSecondSign = 3 * epochLength + mergeSignRange + 1;
        long current = chain.BlockTree.Head!.Number;
        await chain.AddBlocks((int)(targetIncludingBlockForSecondSign - current - 1)); // move so AddBlockMayHaveExtraTx produces the target

        await chain.AddBlock(BuildSigningTx(
            spec,
            signedHeader3EMinusMerge.Number,
            signedHeader3EMinusMerge.Hash ?? signedHeader3EMinusMerge.CalculateHash().ToHash256(),
            chain.Signer.Key!,
            (long)chain.ReadOnlyState.GetNonce(chain.Signer.Address)));

        // --- Evaluate rewards at checkpoint (4E) ---
        long checkpoint4E = 4 * epochLength;
        current = chain.BlockTree.Head!.Number;
        await chain.AddBlocks((int)(checkpoint4E - current));

        Block block4E = chain.BlockTree.Head!;
        var header4E = block4E.Header as XdcBlockHeader;
        Assert.That(header4E, Is.Not.Null);

        BlockReward[] rewardsAt4E = rewardCalculator.CalculateRewards(block4E);
        Assert.That(rewardsAt4E, Has.Length.EqualTo(2));

        UInt256 totalAt4E = rewardsAt4E.Aggregate(UInt256.Zero, (acc, r) => acc + r.Value);
        UInt256 foundationRewardAt4E = rewardsAt4E.Single(r => r.Address == foundation).Value;
        UInt256 ownerRewardAt4E = rewardsAt4E.Single(r => r.Address != foundation).Value;

        Assert.That(foundationRewardAt4E, Is.EqualTo(totalAt4E / 10));
        Assert.That(ownerRewardAt4E, Is.EqualTo(totalAt4E * 90 / 100));

        // === Part 3: if no signing tx, reward should be empty ===

        long checkpoint5E = 5 * epochLength;
        current = chain.BlockTree.Head!.Number;
        await chain.AddBlocks((int)(checkpoint5E - current));

        Block block5E = chain.BlockTree.Head!;
        BlockReward[] rewardsAt5E = rewardCalculator.CalculateRewards(block5E);
        rewardsAt5E.Should().BeEmpty();
    }

    // Test ported from XDC reward_test :
    // https://github.com/XinFinOrg/XDPoSChain/blob/af4178b2c7f9d668d8ba1f3a0244606a20ce303d/consensus/tests/engine_v2_tests/reward_test.go#L99
    [Test]
    public async Task TestHookRewardV2SplitReward()
    {
        var chain = await XdcTestBlockchain.Create();
        var masternodeVotingContract = Substitute.For<IMasternodeVotingContract>();

        masternodeVotingContract
            .GetCandidateOwner(Arg.Any<BlockHeader>(), Arg.Any<Address>())
            .Returns(ci => ci.ArgAt<Address>(1));

        var rewardCalculator = new XdcRewardCalculator(
            chain.EpochSwitchManager,
            chain.SpecProvider,
            chain.BlockTree,
            masternodeVotingContract
        );

        var head = (XdcBlockHeader)chain.BlockTree.Head!.Header;
        IXdcReleaseSpec spec = chain.SpecProvider.GetXdcSpec(head, chain.XdcContext.CurrentRound);

        long epochLength = spec.EpochLength;
        long mergeSignRange = spec.MergeSignRange;

        // - Insert 1 signing tx for header (E + mergeSignRange) signed by signerA into block (E + mergeSignRange + 1)
        // - Insert 2 signing txs (one for header (E + mergeSignRange), one for header (2E - mergeSignRange)) signed by signerB into block (2E - 1)
        // - Verify: rewards at (3E) split 1:2 between A:B with 90/10 owner/foundation

        long initialHeadNumber = chain.BlockTree.Head!.Number;
        long targetHeaderEPlusMerge = epochLength + mergeSignRange;
        await chain.AddBlocks((int)(targetHeaderEPlusMerge - initialHeadNumber));

        var headerEPlusMerge = (XdcBlockHeader)chain.BlockTree.Head!.Header;

        EpochSwitchInfo? epochInfoAtEPlusMerge = chain.EpochSwitchManager.GetEpochSwitchInfo(headerEPlusMerge);
        Assert.That(epochInfoAtEPlusMerge, Is.Not.Null);
        _ = chain.TakeRandomMasterNodes(spec, epochInfoAtEPlusMerge);

        PrivateKey signerA = chain.Signer.Key!;
        Address ownerA = signerA.Address;

        // Insert 1 signing tx for header (E + mergeSignRange) in block (E + mergeSignRange + 1)
        Transaction txA = BuildSigningTx(
            spec,
            headerEPlusMerge.Number,
            headerEPlusMerge.Hash ?? headerEPlusMerge.CalculateHash().ToHash256(),
            signerA);

        await chain.AddBlock(txA); // advances by 1

        long targetHeader2EMinusMerge = 2 * epochLength - mergeSignRange;
        long currentHeadNumber = chain.BlockTree.Head!.Number;
        await chain.AddBlocks((int)(targetHeader2EMinusMerge - currentHeadNumber));

        var header2EMinusMerge = (XdcBlockHeader)chain.BlockTree.Head!.Header;

        // Create a block (2E - 1) containing 2 signing txs from signerB.
        // Move to (2E - 2) so that AddBlock puts us at (2E - 1).
        long targetBlock2EMinus1 = 2 * epochLength - 1;
        long targetBlock2EMinus2 = targetBlock2EMinus1 - 1;

        currentHeadNumber = chain.BlockTree.Head!.Number;
        await chain.AddBlocks((int)(targetBlock2EMinus2 - currentHeadNumber));

        PrivateKey signerB = chain.Signer.Key!;
        Address ownerB = signerB.Address;

        Assert.That(ownerA, Is.Not.EqualTo(ownerB));

        Transaction txBForEPlusMerge = BuildSigningTx(
            spec,
            headerEPlusMerge.Number,
            headerEPlusMerge.Hash!,
            signerB);

        Transaction txBFor2EMinusMerge = BuildSigningTx(
            spec,
            header2EMinusMerge.Number,
            header2EMinusMerge.Hash!,
            signerB,
            nonce: 1);

        await chain.AddBlock(txBForEPlusMerge, txBFor2EMinusMerge); // now at (2E - 1)

        long checkpoint3E = 3 * epochLength;
        currentHeadNumber = chain.BlockTree.Head!.Number;
        await chain.AddBlocks((int)(checkpoint3E - currentHeadNumber));

        Block block3E = chain.BlockTree.Head!;
        BlockReward[] rewards = rewardCalculator.CalculateRewards(block3E);

        Address foundation = spec.FoundationWallet;
        Assert.That(rewards.Length, Is.EqualTo(3));

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
        const long epochLength = 45;
        const long totalRewardInEther = 250;

        const long mergeSignRange = 15;
        const long firstSignedBlockNumber = mergeSignRange;
        const long secondSignedBlockNumber = 2 * mergeSignRange;
        const long firstIncludedTxBlockNumber = firstSignedBlockNumber + 1;
        const long secondIncludedTxBlockNumber = secondSignedBlockNumber + 1;

        PrivateKey[] masternodes = XdcTestHelper.GeneratePrivateKeys(2);
        PrivateKey signerA = masternodes.First();
        PrivateKey signerB = masternodes.Last();

        var foundationWalletAddr = new Address("0x0000000000000000000000000000000000000068");
        var blockSignerContract = new Address("0x0000000000000000000000000000000000000089");

        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        epochSwitchManager.IsEpochSwitchAtBlock(Arg.Any<XdcBlockHeader>())
            .Returns(ci => ((XdcBlockHeader)ci.Args()[0]!).Number % epochLength == 0);

        IXdcReleaseSpec xdcSpec = Substitute.For<IXdcReleaseSpec>();
        xdcSpec.EpochLength.Returns((int)epochLength);
        xdcSpec.FoundationWallet.Returns(foundationWalletAddr);
        xdcSpec.BlockSignerContract.Returns(blockSignerContract);
        xdcSpec.Reward.Returns(totalRewardInEther);
        xdcSpec.SwitchBlock.Returns(0);
        xdcSpec.MergeSignRange = mergeSignRange;

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcSpec);

        IBlockTree tree = Substitute.For<IBlockTree>();
        long chainSize = 2 * epochLength + 1;

        var blockHeaders = new XdcBlockHeader[chainSize];
        var blocks = new Block[chainSize];
        for (int i = 0; i <= epochLength * 2; i++)
        {
            blockHeaders[i] = Build.A.XdcBlockHeader()
                .WithNumber(i)
                .WithValidators(masternodes.Select(m => m.Address).ToArray())
                .WithExtraConsensusData(new ExtraFieldsV2((ulong)i, Build.A.QuorumCertificate().TestObject))
                .TestObject;
            blocks[i] = new Block(blockHeaders[i]);
        }

        // SignerA signs blocks `mergeSignRange` and `2*mergeSignRange`
        // SignerB signs block `mergeSignRange`
        var txsAtFirstIncludedBlock = new List<Transaction>
        {
            BuildSigningTx(xdcSpec, firstSignedBlockNumber, blockHeaders[firstSignedBlockNumber].Hash!, signerA, nonce: 1),
            BuildSigningTx(xdcSpec, firstSignedBlockNumber, blockHeaders[firstSignedBlockNumber].Hash!, signerB, nonce: 2),
        };

        var txsAtSecondIncludedBlock = new List<Transaction>
        {
            BuildSigningTx(xdcSpec, secondSignedBlockNumber, blockHeaders[secondSignedBlockNumber].Hash!, signerA, nonce: 3),
        };

        blocks[firstIncludedTxBlockNumber] = new Block(blockHeaders[firstIncludedTxBlockNumber], new BlockBody(txsAtFirstIncludedBlock.ToArray(), null, null));
        blocks[secondIncludedTxBlockNumber] = new Block(blockHeaders[secondIncludedTxBlockNumber], new BlockBody(txsAtSecondIncludedBlock.ToArray(), null, null));

        tree.FindHeader(Arg.Any<Hash256>(), Arg.Any<long>())
            .Returns(ci => blockHeaders[(long)ci.Args()[1]]);

        tree.FindBlock(Arg.Any<long>())
            .Returns(ci => blocks[(long)ci.Args()[0]]);

        IMasternodeVotingContract votingContract = Substitute.For<IMasternodeVotingContract>();
        votingContract.GetCandidateOwner(Arg.Any<BlockHeader>(), Arg.Any<Address>())
            .Returns(ci => ci.ArgAt<Address>(1));

        var rewardCalculator = new XdcRewardCalculator(epochSwitchManager, specProvider, tree, votingContract);
        BlockReward[] rewards = rewardCalculator.CalculateRewards(blocks.Last());

        Assert.That(rewards, Has.Length.EqualTo(3));

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
        // These are protocol constants (not "test magic numbers"):
        const int signingTxGasLimit = 200_000;
        const int chainId = 0;

        return Build.A.Transaction
            .WithChainId(chainId)
            .WithNonce((UInt256)nonce)
            .WithGasLimit(signingTxGasLimit)
            .WithXdcSigningData(blockNumber, blockHash)
            .ToBlockSignerContract(spec)
            .SignedAndResolved(signer)
            .TestObject;
    }
}
