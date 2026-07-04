// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Db;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Test.Helpers;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[TestFixture]
public class ReadOnlyRewardSideEffectsTests
{
    private static readonly IRewardMasternodeSelector MainRewardMasternodeSelector = new MainXdcRewardMasternodeSelector();

    [Test]
    public void CalculateRewards_TipUpgradeEpoch_MatchesWritableCalculatorWithoutSideEffects()
    {
        const ulong epochLength = 900;
        const ulong mergeSignRange = 15;
        const ulong checkpointNumber = 2700;

        (Block checkpointBlock,
            IEpochSwitchManager epochSwitchManager,
            ISpecProvider specProvider,
            IBlockTree tree,
            IMasternodeVotingContract votingContract,
            ISigningTxCache signingTxCache,
            ITransactionProcessor transactionProcessor) = BuildTipUpgradeFixture(epochLength, mergeSignRange, checkpointNumber);

        IRewardsStore writableRewardsStore = Substitute.For<IRewardsStore>();
        IMintedRecordContract writableMintedRecordContract = Substitute.For<IMintedRecordContract>();
        XdcRewardCalculator writableCalculator = new(
            epochSwitchManager,
            specProvider,
            tree,
            votingContract,
            writableMintedRecordContract,
            signingTxCache,
            transactionProcessor,
            writableRewardsStore,
            MainRewardMasternodeSelector);

        IDb db = new MemDb();
        RewardsStore innerStore = new(db);
        ReadOnlyRewardsStore readOnlyStore = new(innerStore);
        ReadOnlyMintedRecordContract readOnlyMintedRecordContract = new();
        XdcRewardCalculator readOnlyCalculator = new(
            epochSwitchManager,
            specProvider,
            tree,
            votingContract,
            readOnlyMintedRecordContract,
            signingTxCache,
            transactionProcessor,
            readOnlyStore,
            MainRewardMasternodeSelector);

        BlockReward[] writableRewards = writableCalculator.CalculateRewards(checkpointBlock);
        BlockReward[] readOnlyRewards = readOnlyCalculator.CalculateRewards(checkpointBlock);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(readOnlyRewards, Is.EquivalentTo(writableRewards).Using<BlockReward>((left, right) =>
                left.Address == right.Address && left.Value == right.Value));
            writableMintedRecordContract.Received(1).UpdateAccounting(
                transactionProcessor,
                (XdcBlockHeader)checkpointBlock.Header,
                Arg.Any<IXdcReleaseSpec>(),
                Arg.Any<UInt256>(),
                Arg.Any<UInt256>());
            writableRewardsStore.Received(1).SaveEpochRewards(checkpointNumber, writableRewards);
            Assert.That(innerStore.HasEpochRewards(checkpointNumber), Is.False);
        }
    }

    private static Transaction BuildSigningTx(IXdcReleaseSpec spec, ulong blockNumber, Hash256 blockHash, PrivateKey signer, ulong nonce = 0)
    {
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

    private static (
        Block CheckpointBlock,
        IEpochSwitchManager EpochSwitchManager,
        ISpecProvider SpecProvider,
        IBlockTree Tree,
        IMasternodeVotingContract VotingContract,
        ISigningTxCache SigningTxCache,
        ITransactionProcessor TransactionProcessor) BuildTipUpgradeFixture(
        ulong epochLength,
        ulong mergeSignRange,
        ulong checkpointNumber)
    {
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
                builder.WithExtraConsensusData(new ExtraFieldsV2(i, Build.A.QuorumCertificate().TestObject));
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
        votingContract.GetCandidates(Arg.Any<BlockHeader>()).Returns(rewardCandidates);
        votingContract.GetCandidateStake(Arg.Any<BlockHeader>(), Arg.Any<Address>()).Returns(UInt256.One);

        IWorldState worldState = TestWorldStateFactory.CreateForTest(TestMemDbProvider.Init(), LimboLogs.Instance);
        worldState.BeginScope(IWorldState.PreGenesis);
        ITransactionProcessor transactionProcessor = new XdcTransactionProcessor(
            Substitute.For<ITransactionProcessor.IBlobBaseFeeCalculator>(),
            specProvider,
            worldState,
            Substitute.For<IVirtualMachine>(),
            Substitute.For<ICodeInfoRepository>(),
            LimboLogs.Instance,
            votingContract);
        ISigningTxCache signingTxCache = new SigningTxCache(tree, specProvider);

        return (
            blocks[(int)checkpointNumber],
            epochSwitchManager,
            specProvider,
            tree,
            votingContract,
            signingTxCache,
            transactionProcessor);
    }
}
