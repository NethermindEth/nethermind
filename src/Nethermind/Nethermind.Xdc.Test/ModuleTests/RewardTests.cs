// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Db;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Test.Helpers;
using Nethermind.Xdc.Types;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test.ModuleTests;

[NonParallelizable]
public class RewardTests
{
    [TestCase(false, 3)]
    [TestCase(true, 0)]
    public void GetRewardMasternodes_GenesisHeader_UsesExpectedValidatorSource(bool isSubnet, int expectedCount)
    {
        Address[] extraDataMasternodes =
        [
            Address.FromNumber(1),
            Address.FromNumber(2),
            Address.FromNumber(3),
        ];
        XdcBlockHeaderBuilder builder = isSubnet
            ? Build.A.XdcSubnetBlockHeader()
            : Build.A.XdcBlockHeader();
        builder.WithNumber(0);
        builder.WithValidators(Array.Empty<Address>());
        builder.WithExtraData(XdcTestHelper.BuildV1ExtraData(extraDataMasternodes));
        XdcBlockHeader checkpointHeader = builder.TestObject;
        IXdcReleaseSpec spec = Substitute.For<IXdcReleaseSpec>();
        spec.SwitchBlock.Returns(0UL);
        XdcRewardCalculatorSource source = CreateRewardCalculatorSource(isSubnet);
        XdcRewardCalculator rewardCalculator = (XdcRewardCalculator)source.Get(
            CreateXdcTransactionProcessor(Substitute.For<ISpecProvider>(), Substitute.For<IMasternodeVotingContract>()));

        HashSet<Address> masternodes = rewardCalculator.GetRewardMasternodes(checkpointHeader, spec);

        Assert.That(rewardCalculator, isSubnet ? Is.TypeOf<XdcSubnetRewardCalculator>() : Is.TypeOf<XdcRewardCalculator>());
        Assert.That(masternodes, Has.Count.EqualTo(expectedCount));
        if (!isSubnet)
        {
            Assert.That(masternodes, Is.EquivalentTo(extraDataMasternodes));
        }
    }

    // Test ported from XDC reward_test :
    // https://github.com/XinFinOrg/XDPoSChain/blob/af4178b2c7f9d668d8ba1f3a0244606a20ce303d/consensus/tests/engine_v2_tests/reward_test.go#L18
    [Test]
    public async Task TestHookRewardV2()
    {
        using XdcTestBlockchain chain = await XdcTestBlockchain.Create();
        IMasternodeVotingContract masternodeVotingContract = Substitute.For<IMasternodeVotingContract>();
        SigningTxCache signingTxCache = new(chain.BlockTree, chain.SpecProvider);
        chain.ChangeReleaseSpec(spec =>
        {
            spec.EpochLength = 50;
            spec.MergeSignRange = 5;
            spec.MergeSignRange = Math.Min(spec.MergeSignRange, spec.EpochLength / 2);
            if (spec.MergeSignRange < 1) spec.MergeSignRange = 1;
        });

        masternodeVotingContract
            .GetCandidateOwner(Arg.Any<IWorldState>(), Arg.Any<Address>())
            .Returns(ci => ci.ArgAt<Address>(1));

        XdcRewardCalculator rewardCalculator = new(
            chain.EpochSwitchManager,
            chain.SpecProvider,
            chain.BlockTree,
            masternodeVotingContract,
            Substitute.For<IMintedRecordContract>(),
            signingTxCache,
            CreateXdcTransactionProcessor(chain.SpecProvider, masternodeVotingContract, chain.MainWorldState),
            Substitute.For<IRewardsStore>()
        );

        XdcBlockHeader head = (XdcBlockHeader)chain.BlockTree.Head!.Header;
        IXdcReleaseSpec spec = chain.SpecProvider.GetXdcSpec(head, chain.XdcContext.CurrentRound);

        ulong epochLength = spec.EpochLength;
        ulong mergeSignRange = spec.MergeSignRange;

        ulong initialHeadNumber = chain.BlockTree.Head!.Number;

        // --- Part 1: create signing tx for header (E + mergeSignRange) included in next block ---

        ulong targetSignedHeaderNumberEPlusMerge = epochLength + mergeSignRange;
        await chain.AddBlocks(targetSignedHeaderNumberEPlusMerge - initialHeadNumber);

        XdcBlockHeader? signedHeaderEPlusMerge = chain.BlockTree.Head!.Header as XdcBlockHeader;
        Assert.That(signedHeaderEPlusMerge, Is.Not.Null);

        // Note: SubmitTransactionSign signs with DI `ISigner`, not necessarily `chain.Signer`
        await chain.AddBlock(BuildSigningTx(
            spec,
            signedHeaderEPlusMerge.Number,
            signedHeaderEPlusMerge.Hash ?? signedHeaderEPlusMerge.CalculateHash().ToHash256(),
            chain.Signer.Key!,
            chain.ReadOnlyState.GetNonce(chain.Signer.Address)));

        // --- Move to header (3E - mergeSignRange) to sign it later in Part 2 ---

        ulong blockNumberAfterIncludingSignTx = chain.BlockTree.Head!.Number;
        ulong targetSignedHeader3EMinusMerge = 3 * epochLength - mergeSignRange;
        await chain.AddBlocks(targetSignedHeader3EMinusMerge - blockNumberAfterIncludingSignTx);

        XdcBlockHeader? signedHeader3EMinusMerge = chain.BlockTree.Head!.Header as XdcBlockHeader;
        Assert.That(signedHeader3EMinusMerge, Is.Not.Null);

        // --- Evaluate rewards at checkpoint (3E) ---

        ulong headBeforeCheckpoint = chain.BlockTree.Head!.Number;
        ulong checkpoint3E = 3 * epochLength;
        await chain.AddBlocks(checkpoint3E - headBeforeCheckpoint);

        Block block3E = chain.BlockTree.Head!;
        XdcBlockHeader? header3E = block3E.Header as XdcBlockHeader;
        Assert.That(header3E, Is.Not.Null);

        BlockReward[] rewardsAt3E = rewardCalculator.CalculateRewards(block3E);

        Address foundation = spec.FoundationWallet;
        Assert.That(foundation, Is.Not.Null);

        Assert.That(rewardsAt3E, Has.Length.EqualTo(2));

        UInt256 totalAt3E = rewardsAt3E.Aggregate(UInt256.Zero, (acc, r) => acc + r.Value);
        UInt256 foundationRewardAt3E = rewardsAt3E.Single(r => r.Address == foundation).Value;
        UInt256 ownerRewardAt3E = rewardsAt3E.Single(r => r.Address != foundation).Value;

        // Check 90/10 split on totals
        Assert.That(foundationRewardAt3E, Is.EqualTo(totalAt3E / 10));
        Assert.That(ownerRewardAt3E, Is.EqualTo(totalAt3E * 90 / 100));

        // === Part 2: signing hash in a different epoch still counts ===

        // Place signing tx for the previously captured (3E - mergeSignRange) header in block (3E + mergeSignRange + 1)
        ulong targetIncludingBlockForSecondSign = 3 * epochLength + mergeSignRange + 1;
        ulong current = chain.BlockTree.Head!.Number;
        await chain.AddBlocks(targetIncludingBlockForSecondSign - current - 1); // move so AddBlockMayHaveExtraTx produces the target

        // For 4E reward calculation, the masternodes come from the second epoch switch found
        // when walking backwards from 4E. The signed header (3E - mergeSignRange) is in the
        // range [2E+1, 3E), so its epoch switch info provides the relevant masternodes.
        // Use a masternode from that epoch to ensure the signature is counted.
        EpochSwitchInfo? epochSwitchInfoFor2E = chain.EpochSwitchManager.GetEpochSwitchInfo(signedHeader3EMinusMerge);
        Assert.That(epochSwitchInfoFor2E, Is.Not.Null);
        PrivateKey signerForPart2 = chain.MasterNodeCandidates.First(k => k.Address == epochSwitchInfoFor2E!.Masternodes[0]);

        // Set the chain's signer to our chosen masternode - required because
        // SignTransactionFilter rejects signing txs from non-current-signers
        chain.Signer.SetSigner(signerForPart2);

        await chain.AddBlock(BuildSigningTx(
            spec,
            signedHeader3EMinusMerge.Number,
            signedHeader3EMinusMerge.Hash ?? signedHeader3EMinusMerge.CalculateHash().ToHash256(),
            signerForPart2,
            chain.ReadOnlyState.GetNonce(signerForPart2.Address)));

        // --- Evaluate rewards at checkpoint (4E) ---
        ulong checkpoint4E = 4 * epochLength;
        current = chain.BlockTree.Head!.Number;
        await chain.AddBlocks(checkpoint4E - current);

        Block block4E = chain.BlockTree.Head!;
        XdcBlockHeader? header4E = block4E.Header as XdcBlockHeader;
        Assert.That(header4E, Is.Not.Null);

        BlockReward[] rewardsAt4E = rewardCalculator.CalculateRewards(block4E);
        Assert.That(rewardsAt4E, Has.Length.EqualTo(2));

        UInt256 totalAt4E = rewardsAt4E.Aggregate(UInt256.Zero, (acc, r) => acc + r.Value);
        UInt256 foundationRewardAt4E = rewardsAt4E.Single(r => r.Address == foundation).Value;
        UInt256 ownerRewardAt4E = rewardsAt4E.Single(r => r.Address != foundation).Value;

        Assert.That(foundationRewardAt4E, Is.EqualTo(totalAt4E / 10));
        Assert.That(ownerRewardAt4E, Is.EqualTo(totalAt4E * 90 / 100));

        // === Part 3: if no signing tx, reward should be empty ===

        ulong checkpoint5E = 5 * epochLength;
        current = chain.BlockTree.Head!.Number;
        await chain.AddBlocks(checkpoint5E - current);

        Block block5E = chain.BlockTree.Head!;
        BlockReward[] rewardsAt5E = rewardCalculator.CalculateRewards(block5E);
        Assert.That(rewardsAt5E, Is.Empty);
    }

    // Test ported from XDC reward_test :
    // https://github.com/XinFinOrg/XDPoSChain/blob/af4178b2c7f9d668d8ba1f3a0244606a20ce303d/consensus/tests/engine_v2_tests/reward_test.go#L99
    [Test]
    public async Task TestHookRewardV2SplitReward()
    {
        XdcTestBlockchain chain = await XdcTestBlockchain.Create();
        IMasternodeVotingContract masternodeVotingContract = Substitute.For<IMasternodeVotingContract>();
        SigningTxCache signingTxCache = new(chain.BlockTree, chain.SpecProvider);
        masternodeVotingContract
            .GetCandidateOwner(Arg.Any<IWorldState>(), Arg.Any<Address>())
            .Returns(ci => ci.ArgAt<Address>(1));

        XdcRewardCalculator rewardCalculator = new(
            chain.EpochSwitchManager,
            chain.SpecProvider,
            chain.BlockTree,
            masternodeVotingContract,
            Substitute.For<IMintedRecordContract>(),
            signingTxCache,
            CreateXdcTransactionProcessor(chain.SpecProvider, masternodeVotingContract, chain.MainWorldState),
            Substitute.For<IRewardsStore>()
        );

        XdcBlockHeader head = (XdcBlockHeader)chain.BlockTree.Head!.Header;
        IXdcReleaseSpec spec = chain.SpecProvider.GetXdcSpec(head, chain.XdcContext.CurrentRound);

        ulong epochLength = spec.EpochLength;
        ulong mergeSignRange = spec.MergeSignRange;

        // - Insert 1 signing tx for header (E + mergeSignRange) signed by signerA into block (E + mergeSignRange + 1)
        // - Insert 2 signing txs (one for header (E + mergeSignRange), one for header (2E - mergeSignRange)) signed by signerB into block (2E - 1)
        // - Verify: rewards at (3E) split 1:2 between A:B with 90/10 owner/foundation

        ulong initialHeadNumber = chain.BlockTree.Head!.Number;
        ulong targetHeaderEPlusMerge = epochLength + mergeSignRange;
        await chain.AddBlocks(targetHeaderEPlusMerge - initialHeadNumber);

        XdcBlockHeader headerEPlusMerge = (XdcBlockHeader)chain.BlockTree.Head!.Header;

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

        ulong targetHeader2EMinusMerge = 2 * epochLength - mergeSignRange;
        ulong currentHeadNumber = chain.BlockTree.Head!.Number;
        await chain.AddBlocks(targetHeader2EMinusMerge - currentHeadNumber);

        XdcBlockHeader header2EMinusMerge = (XdcBlockHeader)chain.BlockTree.Head!.Header;

        // Create a block (2E - 1) containing 2 signing txs from signerB.
        // Move to (2E - 2) so that AddBlock puts us at (2E - 1).
        ulong targetBlock2EMinus1 = 2 * epochLength - 1;
        ulong targetBlock2EMinus2 = targetBlock2EMinus1 - 1;

        currentHeadNumber = chain.BlockTree.Head!.Number;
        await chain.AddBlocks(targetBlock2EMinus2 - currentHeadNumber);

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

        ulong checkpoint3E = 3 * epochLength;
        currentHeadNumber = chain.BlockTree.Head!.Number;
        await chain.AddBlocks(checkpoint3E - currentHeadNumber);

        Block block3E = chain.BlockTree.Head!;
        BlockReward[] rewards = rewardCalculator.CalculateRewards(block3E);

        Address foundation = spec.FoundationWallet;
        Assert.That(rewards.Length, Is.EqualTo(3));

        UInt256 totalRewards = (UInt256)spec.Reward * Unit.Ether;

        UInt256 signerAReward = totalRewards / 3;
        UInt256 ownerAReward = signerAReward * 90 / 100;
        UInt256 foundationReward = signerAReward / 10;
        UInt256 signerBReward = totalRewards / 3 * 2;
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
        const ulong epochLength = 45;
        const ulong totalRewardInEther = 250;

        const ulong mergeSignRange = 15;
        const ulong firstSignedBlockNumber = mergeSignRange;
        const ulong secondSignedBlockNumber = 2 * mergeSignRange;
        const ulong firstIncludedTxBlockNumber = firstSignedBlockNumber + 1;
        const ulong secondIncludedTxBlockNumber = secondSignedBlockNumber + 1;

        PrivateKey[] masternodes = XdcTestHelper.GeneratePrivateKeys(2);
        PrivateKey signerA = masternodes.First();
        PrivateKey signerB = masternodes.Last();

        Address foundationWalletAddr = new("0x0000000000000000000000000000000000000068");
        Address blockSignerContract = new("0x0000000000000000000000000000000000000089");

        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        epochSwitchManager.IsEpochSwitchAtBlock(Arg.Any<XdcBlockHeader>())
            .Returns(ci => ((XdcBlockHeader)ci.Args()[0]!).Number % epochLength == 0);

        IXdcReleaseSpec xdcSpec = Substitute.For<IXdcReleaseSpec>();
        xdcSpec.EpochLength.Returns(epochLength);
        xdcSpec.FoundationWallet.Returns(foundationWalletAddr);
        xdcSpec.BlockSignerContract.Returns(blockSignerContract);
        xdcSpec.Reward.Returns(totalRewardInEther);
        xdcSpec.SwitchBlock.Returns(0UL);
        xdcSpec.MergeSignRange.Returns(mergeSignRange);
        xdcSpec.IsTipUpgradeRewardEnabled.Returns(false);

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcSpec);

        IBlockTree tree = Substitute.For<IBlockTree>();
        ulong chainSize = 2 * epochLength + 1;

        XdcBlockHeader[] blockHeaders = new XdcBlockHeader[chainSize];
        Block[] blocks = new Block[chainSize];
        Address[] masternodeAddresses = masternodes.Select(m => m.Address).ToArray();
        for (ulong i = 0; i <= epochLength * 2; i++)
        {
            XdcBlockHeaderBuilder builder = Build.A.XdcBlockHeader()
                .WithNumber(i)
                .WithValidators(masternodeAddresses);
            // Block 0 is the v1 genesis (SwitchBlock=0), so ExtraData must use v1 format
            if (i == 0)
                builder.WithExtraData(XdcTestHelper.BuildV1ExtraData(masternodeAddresses));
            else
                builder.WithExtraConsensusData(new ExtraFieldsV2((ulong)i, Build.A.QuorumCertificate().TestObject));
            blockHeaders[i] = builder.TestObject;
            blocks[i] = new Block(blockHeaders[i]);
        }

        // SignerA signs blocks `mergeSignRange` and `2*mergeSignRange`
        // SignerB signs block `mergeSignRange`
        List<Transaction> txsAtFirstIncludedBlock =
        [
            BuildSigningTx(xdcSpec, firstSignedBlockNumber, blockHeaders[firstSignedBlockNumber].Hash!, signerA, nonce: 1),
            BuildSigningTx(xdcSpec, firstSignedBlockNumber, blockHeaders[firstSignedBlockNumber].Hash!, signerB, nonce: 2),
        ];

        List<Transaction> txsAtSecondIncludedBlock =
        [
            BuildSigningTx(xdcSpec, secondSignedBlockNumber, blockHeaders[secondSignedBlockNumber].Hash!, signerA, nonce: 3),
        ];

        blocks[firstIncludedTxBlockNumber] = new Block(blockHeaders[firstIncludedTxBlockNumber], new BlockBody(txsAtFirstIncludedBlock.ToArray(), null, null));
        blocks[secondIncludedTxBlockNumber] = new Block(blockHeaders[secondIncludedTxBlockNumber], new BlockBody(txsAtSecondIncludedBlock.ToArray(), null, null));

        tree.FindHeader(Arg.Any<Hash256>(), Arg.Any<ulong?>())
            .Returns(ci => blockHeaders[(int)ci.ArgAt<ulong?>(1)!]);
        tree.FindBlock(Arg.Any<Hash256>(), Arg.Any<ulong?>())
            .Returns(ci => blocks[(int)ci.ArgAt<ulong?>(1)!]);

        IMasternodeVotingContract votingContract = Substitute.For<IMasternodeVotingContract>();
        votingContract.GetCandidateOwner(Arg.Any<IWorldState>(), Arg.Any<Address>())
            .Returns(ci => ci.ArgAt<Address>(1));

        SigningTxCache signingTxCache = new(tree, specProvider);
        XdcRewardCalculator rewardCalculator = new(epochSwitchManager, specProvider, tree, votingContract, Substitute.For<IMintedRecordContract>(), signingTxCache, CreateXdcTransactionProcessor(specProvider, votingContract), Substitute.For<IRewardsStore>());
        BlockReward[] rewards = rewardCalculator.CalculateRewards(blocks.Last());

        Assert.That(rewards, Has.Length.EqualTo(3));

        UInt256 aOwnerExpected = UInt256.Parse("149999999999999999999");
        UInt256 aFoundExpected = UInt256.Parse("16666666666666666666");
        UInt256 bOwnerExpected = UInt256.Parse("74999999999999999999");
        UInt256 bFoundExpected = UInt256.Parse("8333333333333333333");

        UInt256 aOwnerReward = rewards.Single(r => r.Address == signerA.Address).Value;
        UInt256 bOwnerReward = rewards.Single(r => r.Address == signerB.Address).Value;
        UInt256 foundationReward = rewards.Single(r => r.Address == foundationWalletAddr).Value;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(foundationReward, Is.EqualTo(aFoundExpected + bFoundExpected));
            Assert.That(aOwnerReward, Is.EqualTo(aOwnerExpected));
            Assert.That(bOwnerReward, Is.EqualTo(bOwnerExpected));
        }
    }

    [Test]
    public void TestHookRewardAfterUpgrade()
    {
        const ulong epochLength = 900;
        const ulong mergeSignRange = 15;
        const ulong checkpointNumber = 2700;

        PrivateKey[] keys = XdcTestHelper.GeneratePrivateKeys(9);
        PrivateKey[] masternodeKeys = [keys[0], keys[1], keys[2], keys[3], keys[4]];
        PrivateKey signer1 = masternodeKeys.First();
        PrivateKey signer2 = masternodeKeys.Last();
        PrivateKey protector1 = keys[5];
        PrivateKey protector2 = keys[6];
        PrivateKey observer1 = keys[7];
        PrivateKey observer2 = keys[8];

        Address[] masternodes = masternodeKeys.Select(key => key.Address).ToArray();

        Address foundationWalletAddr = Address.FromNumber(0x68);
        Address blockSignerContract = Address.FromNumber(0x89);

        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        epochSwitchManager.IsEpochSwitchAtBlock(Arg.Any<XdcBlockHeader>())
            .Returns(ci => ((XdcBlockHeader)ci.Args()[0]!).Number % epochLength == 0);

        IXdcReleaseSpec xdcSpec = Substitute.For<IXdcReleaseSpec>();
        xdcSpec.EpochLength.Returns(epochLength);
        xdcSpec.FoundationWallet.Returns(foundationWalletAddr);
        xdcSpec.BlockSignerContract.Returns(blockSignerContract);
        xdcSpec.SwitchBlock.Returns(0UL);
        xdcSpec.MergeSignRange.Returns(mergeSignRange);
        xdcSpec.IsTipUpgradeRewardEnabled.Returns(true);
        xdcSpec.MaxProtectorNodes.Returns(2);
        xdcSpec.MaxObserverNodes.Returns(2);
        xdcSpec.MasternodeReward.Returns((UInt256)500);
        xdcSpec.ProtectorReward.Returns((UInt256)400);
        xdcSpec.ObserverReward.Returns((UInt256)300);

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcSpec);

        IBlockTree tree = Substitute.For<IBlockTree>();
        ulong chainSize = checkpointNumber + 1;
        XdcBlockHeader[] blockHeaders = new XdcBlockHeader[chainSize];
        Block[] blocks = new Block[chainSize];

        for (ulong i = 0; i < chainSize; i++)
        {
            XdcBlockHeaderBuilder builder = Build.A.XdcBlockHeader()
                .WithNumber(i)
                .WithValidators(masternodes);
            if (i == 0)
            {
                builder.WithExtraData(XdcTestHelper.BuildV1ExtraData(masternodes));
            }
            else
            {
                builder.WithExtraConsensusData(new ExtraFieldsV2((ulong)i, Build.A.QuorumCertificate().TestObject));
            }

            if (i == epochLength)
            {
                builder.WithPenalties([observer2.Address]);
            }

            blockHeaders[i] = builder.TestObject;
            blocks[i] = new Block(blockHeaders[i]);
        }

        Transaction[] txsAt916 =
        [
            BuildSigningTx(xdcSpec, 915, blockHeaders[915].Hash!, signer1, nonce: 1),
        ];

        Transaction[] txsAt1799 =
        [
            BuildSigningTx(xdcSpec, 915, blockHeaders[915].Hash!, signer2, nonce: 2),
            BuildSigningTx(xdcSpec, 1785, blockHeaders[1785].Hash!, signer2, nonce: 3),
            BuildSigningTx(xdcSpec, 1785, blockHeaders[1785].Hash!, protector1, nonce: 4),
            BuildSigningTx(xdcSpec, 915, blockHeaders[915].Hash!, protector2, nonce: 5),
            BuildSigningTx(xdcSpec, 1785, blockHeaders[1785].Hash!, protector2, nonce: 6),
            BuildSigningTx(xdcSpec, 1785, blockHeaders[1785].Hash!, observer1, nonce: 7),
            BuildSigningTx(xdcSpec, 1785, blockHeaders[1785].Hash!, observer2, nonce: 8),
        ];

        blocks[916] = new Block(blockHeaders[916], new BlockBody(txsAt916, null, null));
        blocks[1799] = new Block(blockHeaders[1799], new BlockBody(txsAt1799, null, null));

        tree.FindHeader(Arg.Any<Hash256>(), Arg.Any<ulong?>())
            .Returns(ci => blockHeaders[(int)ci.ArgAt<ulong?>(1)!]);
        tree.FindBlock(Arg.Any<Hash256>(), Arg.Any<ulong?>())
            .Returns(ci => blocks[(int)ci.ArgAt<ulong?>(1)!]);

        IMasternodeVotingContract votingContract = Substitute.For<IMasternodeVotingContract>();
        votingContract.GetCandidateOwner(Arg.Any<IWorldState>(), Arg.Any<Address>())
            .Returns(ci => ci.ArgAt<Address>(1));
        Address[] rewardCandidates =
        [
            ..masternodes,
            protector1.Address,
            protector2.Address,
            observer1.Address,
            observer2.Address,
        ];
        votingContract.GetCandidates(Arg.Any<BlockHeader>())
            .Throws(new InvalidOperationException("Readonly candidates lookup should not be used for block-processing rewards."));
        votingContract.GetCandidates(Arg.Any<ITransactionProcessor>(), Arg.Any<BlockHeader>()).Returns(rewardCandidates);
        votingContract.GetCandidateStake(Arg.Any<BlockHeader>(), Arg.Any<Address>())
            .Throws(new InvalidOperationException("Readonly stake lookup should not be used for block-processing rewards."));
        votingContract.GetCandidateStake(Arg.Any<ITransactionProcessor>(), Arg.Any<BlockHeader>(), Arg.Any<Address>()).Returns(UInt256.One);

        IWorldState worldState = TestWorldStateFactory.CreateForTest(TestMemDbProvider.Init(), LimboLogs.Instance);
        using IDisposable _ = worldState.BeginScope(IWorldState.PreGenesis);
        IMintedRecordContract mintedRecordContract = new MintedRecordContract();
        ITransactionProcessor transactionProcessor = new XdcTransactionProcessor(
            Substitute.For<ITransactionProcessor.IBlobBaseFeeCalculator>(),
            specProvider,
            worldState,
            Substitute.For<IVirtualMachine>(),
            Substitute.For<ICodeInfoRepository>(),
            LimboLogs.Instance,
            votingContract);
        ISigningTxCache signingTxCache = new SigningTxCache(tree, specProvider);
        XdcRewardCalculator rewardCalculator = new(
            epochSwitchManager,
            specProvider,
            tree,
            votingContract,
            mintedRecordContract,
            signingTxCache,
            transactionProcessor,
            Substitute.For<IRewardsStore>());

        BlockReward[] rewards = rewardCalculator.CalculateRewards(blocks[(int)checkpointNumber]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rewards, Has.Length.EqualTo(6));
            Assert.That(rewards.Single(r => r.Address == signer1.Address).Value, Is.EqualTo((UInt256)450));
            Assert.That(rewards.Single(r => r.Address == signer2.Address).Value, Is.EqualTo((UInt256)450));
            Assert.That(rewards.Single(r => r.Address == protector1.Address).Value, Is.EqualTo((UInt256)360));
            Assert.That(rewards.Single(r => r.Address == protector2.Address).Value, Is.EqualTo((UInt256)360));
            Assert.That(rewards.Single(r => r.Address == observer1.Address).Value, Is.EqualTo((UInt256)270));
            Assert.That(rewards.Single(r => r.Address == foundationWalletAddr).Value, Is.EqualTo((UInt256)210));
            Assert.That(rewards.Any(r => r.Address == observer2.Address), Is.False);
        }

        // Validate minted-record accounting side effects produced by the reward upgrade path.
        UInt256 epochNumber = 3;
        UInt256 mintedRecordPostMintedBase = UInt256.Parse("0x0100000000000000000000000000000000000000000000000000000000000000");
        UInt256 mintedRecordPostBurnedBase = UInt256.Parse("0x0200000000000000000000000000000000000000000000000000000000000000");
        UInt256 mintedRecordPostRewardBlockBase = UInt256.Parse("0x0300000000000000000000000000000000000000000000000000000000000000");
        Address mintedRecordAddress = Address.FromNumber(0x9a);

        UInt256 totalMinted = ReadStorageUInt256(worldState, mintedRecordAddress, mintedRecordPostMintedBase + epochNumber);
        UInt256 rewardBlock = ReadStorageUInt256(worldState, mintedRecordAddress, mintedRecordPostRewardBlockBase + epochNumber);
        UInt256 onsetBlock = ReadStorageUInt256(worldState, mintedRecordAddress, (UInt256)2);
        UInt256 totalBurned = ReadStorageUInt256(worldState, mintedRecordAddress, mintedRecordPostBurnedBase + epochNumber);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(totalMinted, Is.EqualTo((UInt256)2100));
            Assert.That(rewardBlock, Is.EqualTo((UInt256)checkpointNumber));
            Assert.That(onsetBlock, Is.EqualTo((UInt256)checkpointNumber));
            Assert.That(totalBurned, Is.EqualTo(UInt256.Zero));
        }
    }

    [Test]
    public void RewardCalculator_CalculateRewardsForSignersAndHolders_MatchesExpectedValues()
    {
        IMasternodeVotingContract masternodeVotingContract = Substitute.For<IMasternodeVotingContract>();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        ISigningTxCache signingTxCache = new SigningTxCache(blockTree, specProvider);
        XdcRewardCalculator rewardCalculator = new(
            Substitute.For<IEpochSwitchManager>(),
            specProvider,
            blockTree,
            masternodeVotingContract,
            Substitute.For<IMintedRecordContract>(),
            signingTxCache,
            CreateXdcTransactionProcessor(specProvider, masternodeVotingContract),
            Substitute.For<IRewardsStore>()
            );

        UInt256 totalReward = UInt256.Parse("171000000000000000000");
        ulong totalSigner = 177, sign = 59;
        UInt256 expectedReward = UInt256.Parse("56999999999999999983");

        Assert.That(rewardCalculator.CalculateProportionalReward(sign, totalSigner, totalReward), Is.EqualTo(expectedReward));

        UInt256 expectedAmountOwner = UInt256.Parse(("51299999999999999984"));
        UInt256 expectedAmountFoundationWallet = UInt256.Parse(("5699999999999999998"));
        bool ok = Address.TryParse("0x68d1e2F85e4583BeCc610b47Dd1b857850a4025A", out Address? signer);
        Assert.That(ok, Is.True);
        Address foundationWalletAddr = Address.FromNumber(0x68);
        masternodeVotingContract.GetCandidateOwner(Arg.Any<IWorldState>(), signer!).Returns(signer!);
        (BlockReward holderReward, UInt256 foundationWalletReward) = rewardCalculator.DistributeRewards(signer!, expectedReward, foundationWalletAddr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(holderReward.Value, Is.EqualTo(expectedAmountOwner));
            Assert.That(foundationWalletReward, Is.EqualTo(expectedAmountFoundationWallet));
        }
    }

    [Test]
    public void DistributeRewards_OwnerIsFoundation_MatchesReferenceOverwriteBehavior()
    {
        IMasternodeVotingContract masternodeVotingContract = Substitute.For<IMasternodeVotingContract>();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        ISigningTxCache signingTxCache = new SigningTxCache(blockTree, specProvider);
        XdcRewardCalculator rewardCalculator = new(
            Substitute.For<IEpochSwitchManager>(),
            specProvider,
            blockTree,
            masternodeVotingContract,
            Substitute.For<IMintedRecordContract>(),
            signingTxCache,
            CreateXdcTransactionProcessor(specProvider, masternodeVotingContract),
            Substitute.For<IRewardsStore>());

        Address signer = new("0x80b329b66ddfe2180904d6ae737283a3f1860b83");
        Address foundationWalletAddr = new("0x5cb041be27deb4a506ad63d082c6043b4a5c6898");
        UInt256 reward = UInt256.Parse("666666666666666633");
        UInt256 expectedFoundationReward = UInt256.Parse("66666666666666663");
        masternodeVotingContract.GetCandidateOwner(Arg.Any<IWorldState>(), signer).Returns(foundationWalletAddr);

        (BlockReward holderReward, UInt256 foundationWalletReward) = rewardCalculator.DistributeRewards(signer, reward, foundationWalletAddr);

        Assert.That(holderReward.Address, Is.EqualTo(foundationWalletAddr));
        Assert.That(holderReward.Value, Is.EqualTo(expectedFoundationReward));
        Assert.That(foundationWalletReward, Is.EqualTo(UInt256.Zero));
    }

    private static Transaction BuildSigningTx(IXdcReleaseSpec spec, ulong blockNumber, Hash256 blockHash, PrivateKey signer, ulong nonce = 0)
    {
        // These are protocol constants (not "test magic numbers"):
        const int signingTxGasLimit = 200_000;
        const int chainId = 0;

        return Build.A.Transaction
            .WithChainId(chainId)
            .WithNonce(nonce)
            .WithGasLimit(signingTxGasLimit)
            .WithXdcSigningData(blockNumber, blockHash)
            .ToBlockSignerContract(spec)
            .SignedAndResolved(signer)
            .TestObject;
    }

    private static XdcRewardCalculatorSource CreateRewardCalculatorSource(bool isSubnet)
    {
        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IMasternodeVotingContract masternodeVotingContract = Substitute.For<IMasternodeVotingContract>();
        IMintedRecordContract mintedRecordContract = Substitute.For<IMintedRecordContract>();
        ISigningTxCache signingTxCache = Substitute.For<ISigningTxCache>();
        IRewardsStore rewardsStore = Substitute.For<IRewardsStore>();
        return isSubnet
            ? new XdcSubnetRewardCalculatorSource(epochSwitchManager, specProvider, blockTree, masternodeVotingContract, mintedRecordContract, signingTxCache, rewardsStore)
            : new XdcRewardCalculatorSource(epochSwitchManager, specProvider, blockTree, masternodeVotingContract, mintedRecordContract, signingTxCache, rewardsStore);
    }

    private static XdcTransactionProcessor CreateXdcTransactionProcessor(
        ISpecProvider specProvider,
        IMasternodeVotingContract masternodeVotingContract,
        IWorldState? worldState = null) => new(
            Substitute.For<ITransactionProcessor.IBlobBaseFeeCalculator>(),
            specProvider,
            worldState ?? Substitute.For<IWorldState>(),
            Substitute.For<IVirtualMachine>(),
            Substitute.For<ICodeInfoRepository>(),
            LimboLogs.Instance,
            masternodeVotingContract);

    private static UInt256 ReadStorageUInt256(IWorldState worldState, Address address, UInt256 slot)
    {
        ReadOnlySpan<byte> value = worldState.Get(new StorageCell(address, slot));
        return value.Length == 0 ? UInt256.Zero : new UInt256(value, isBigEndian: true);
    }
}
