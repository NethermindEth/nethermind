// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[TestFixture]
public class RewardsStoreTests
{
    [Test]
    public void SaveEpochRewards_WhenSameAccountHasMultipleRewards_ShouldAggregateAndReadRewardByAccount()
    {
        IDb db = new MemDb();
        using RewardsStore store = CreateStore(db);
        Address account = Address.FromNumber(1);
        Hash256 epochBlockHash = TestItem.KeccakA;

        BlockReward[] rewards =
        [
            new(account, (UInt256)10),
            new(account, (UInt256)20),
        ];

        store.SaveEpochRewards(epochBlockHash, rewards);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.HasEpochRewards(epochBlockHash), Is.True);
            Assert.That(store.TryGetAccountReward(account, epochBlockHash, out UInt256 savedReward), Is.True);
            Assert.That(savedReward, Is.EqualTo((UInt256)30));
        }
    }

    [Test]
    public void TryGetAccountReward_WhenNoRewardForAccount_ShouldReturnFalse()
    {
        IDb db = new MemDb();
        using RewardsStore store = CreateStore(db);
        Address savedAccount = Address.FromNumber(1);
        Address missingAccount = Address.FromNumber(2);
        Hash256 epochBlockHash = TestItem.KeccakA;

        store.SaveEpochRewards(epochBlockHash, [new BlockReward(savedAccount, (UInt256)10)]);

        Assert.That(store.TryGetAccountReward(missingAccount, epochBlockHash, out _), Is.False);
    }

    [Test]
    public async Task OnBlockAddedToMain_WhenEpochBlockIsProcessed_ShouldPersistRewards()
    {
        IDb db = new MemDb();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IXdcReleaseSpec xdcSpec = Substitute.For<IXdcReleaseSpec>();
        xdcSpec.SwitchBlock.Returns(0UL);
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcSpec);
        IRewardCalculatorSource rewardCalculatorSource = Substitute.For<IRewardCalculatorSource>();
        IReadOnlyTxProcessingEnvFactory envFactory = Substitute.For<IReadOnlyTxProcessingEnvFactory>();
        IReadOnlyTxProcessorSource processorSource = Substitute.For<IReadOnlyTxProcessorSource>();
        IReadOnlyTxProcessingScope processingScope = Substitute.For<IReadOnlyTxProcessingScope>();
        ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
        IRewardCalculator rewardCalculator = Substitute.For<IRewardCalculator>();

        const ulong epochBlockNumber = 900;
        Address account = Address.FromNumber(1);
        BlockReward[] rewards = [new BlockReward(account, (UInt256)42)];
        Block block = new(BuildCheckpointHeader(epochBlockNumber));

        blockTree.WasProcessed(epochBlockNumber, block.Hash!).Returns(true);
        blockTree.Head.Returns(block);
        blockTree.FindBestSuggestedHeader().Returns(block.Header);
        epochSwitchManager.IsEpochSwitchAtBlock(Arg.Any<XdcBlockHeader>()).Returns(true);
        envFactory.Create().Returns(processorSource);
        processorSource.Build(block.Header).Returns(processingScope);
        processingScope.TransactionProcessor.Returns(transactionProcessor);
        rewardCalculatorSource.Get(transactionProcessor).Returns(rewardCalculator);
        rewardCalculator.CalculateRewards(block).Returns(rewards);

        using RewardsStore store = CreateStore(
            db,
            blockTree: blockTree,
            epochSwitchManager: epochSwitchManager,
            specProvider: specProvider,
            rewardCalculatorSource: rewardCalculatorSource,
            readOnlyTxProcessingEnvFactory: envFactory);
        store.Start();

        blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block));

        await Task.Delay(100);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.HasEpochRewards(block.Hash!), Is.True);
            Assert.That(store.TryGetAccountReward(account, block.Hash!, out UInt256 savedReward), Is.True);
            Assert.That(savedReward, Is.EqualTo((UInt256)42));
        }
    }

    [Test]
    public async Task OnBlockAddedToMain_WhenBlockWasNotProcessed_ShouldNotPersistRewards()
    {
        IDb db = new MemDb();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        Block block = new(BuildCheckpointHeader(900));

        blockTree.WasProcessed(block.Number, block.Hash!).Returns(false);
        epochSwitchManager.IsEpochSwitchAtBlock(Arg.Any<XdcBlockHeader>()).Returns(true);

        using RewardsStore store = CreateStore(db, blockTree: blockTree, epochSwitchManager: epochSwitchManager);
        store.Start();

        blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block));

        await Task.Delay(100);

        Assert.That(store.HasEpochRewards(block.Hash!), Is.False);
    }

    [Test]
    public async Task OnBlockAddedToMain_WhenSyncing_ShouldNotPersistRewards()
    {
        IDb db = new MemDb();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        Block block = new(BuildCheckpointHeader(900));

        blockTree.WasProcessed(block.Number, block.Hash!).Returns(true);
        blockTree.Head.Returns((Block?)null);
        blockTree.FindBestSuggestedHeader().Returns((BlockHeader?)null);
        epochSwitchManager.IsEpochSwitchAtBlock(Arg.Any<XdcBlockHeader>()).Returns(true);

        using RewardsStore store = CreateStore(db, blockTree: blockTree, epochSwitchManager: epochSwitchManager);
        store.Start();

        blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block));

        await Task.Delay(100);

        Assert.That(store.HasEpochRewards(block.Hash!), Is.False);
    }

    private static RewardsStore CreateStore(
        IDb db,
        IBlockTree? blockTree = null,
        IEpochSwitchManager? epochSwitchManager = null,
        ISpecProvider? specProvider = null,
        IRewardCalculatorSource? rewardCalculatorSource = null,
        IReadOnlyTxProcessingEnvFactory? readOnlyTxProcessingEnvFactory = null,
        ILogManager? logManager = null) =>
        new(
            db,
            blockTree ?? Substitute.For<IBlockTree>(),
            epochSwitchManager ?? Substitute.For<IEpochSwitchManager>(),
            specProvider ?? Substitute.For<ISpecProvider>(),
            rewardCalculatorSource ?? Substitute.For<IRewardCalculatorSource>(),
            readOnlyTxProcessingEnvFactory ?? Substitute.For<IReadOnlyTxProcessingEnvFactory>(),
            logManager ?? LimboLogs.Instance);

    private static XdcBlockHeader BuildCheckpointHeader(ulong number) =>
        Build.A.XdcBlockHeader()
            .WithNumber(number)
            .WithExtraConsensusData(new ExtraFieldsV2(number, Build.A.QuorumCertificate().TestObject))
            .TestObject;
}
