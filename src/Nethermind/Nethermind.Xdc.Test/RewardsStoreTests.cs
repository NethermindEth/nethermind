// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
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
    public void SaveEpochRewards_WhenSameAccountHasMultipleBlockRewards_ShouldAggregateAndReadRewardByAccount()
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
    public void SaveEpochRewards_ShouldRoundTripNestedRewardPayload()
    {
        IDb db = new MemDb();
        using RewardsStore store = CreateStore(db);
        Hash256 epochBlockHash = TestItem.KeccakA;
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> payload = new()
        {
            [XdcConstants.RpcRewardSectionMasternode] = new()
            {
                [Address.FromNumber(1).ToString()] = new() { [Address.FromNumber(2).ToString()] = "1000" },
            },
        };

        store.SaveEpochRewards(epochBlockHash, payload);

        Assert.That(store.TryGetEpochRewards(epochBlockHash, out Dictionary<string, Dictionary<string, Dictionary<string, string>>>? loaded), Is.True);
        Assert.That(loaded![XdcConstants.RpcRewardSectionMasternode][Address.FromNumber(1).ToString()][Address.FromNumber(2).ToString()], Is.EqualTo("1000"));
    }

    [Test]
    public void SaveEpochRewards_WhenAccountAppearsAcrossSections_ShouldAggregateAccountReward()
    {
        IDb db = new MemDb();
        using RewardsStore store = CreateStore(db);
        Hash256 epochBlockHash = TestItem.KeccakA;
        Address account = Address.FromNumber(1);
        Address signer = Address.FromNumber(10);

        Dictionary<string, Dictionary<string, Dictionary<string, string>>> payload = new()
        {
            [XdcConstants.RpcRewardSectionMasternode] = new()
            {
                [signer.ToString()] = new() { [account.ToString()] = "100" },
            },
            [XdcConstants.RpcRewardSectionProtector] = new()
            {
                [signer.ToString()] = new() { [account.ToString()] = "50" },
            },
        };

        store.SaveEpochRewards(epochBlockHash, payload);

        Assert.That(store.TryGetAccountReward(account, epochBlockHash, out UInt256 savedReward), Is.True);
        Assert.That(savedReward, Is.EqualTo((UInt256)150));
    }

    [Test]
    public void SaveEpochRewards_WhenFoundationWalletAppearsUnderMultipleSigners_ShouldAggregateFoundationReward()
    {
        IDb db = new MemDb();
        using RewardsStore store = CreateStore(db);
        Hash256 epochBlockHash = TestItem.KeccakA;
        Address foundation = Address.FromNumber(99);
        Address owner = Address.FromNumber(1);
        Address signer1 = Address.FromNumber(10);
        Address signer2 = Address.FromNumber(11);

        Dictionary<string, Dictionary<string, Dictionary<string, string>>> payload = new()
        {
            [XdcConstants.RpcRewardSectionMasternode] = new()
            {
                [signer1.ToString()] = new()
                {
                    [owner.ToString()] = "90",
                    [foundation.ToString()] = "10",
                },
                [signer2.ToString()] = new()
                {
                    [owner.ToString()] = "180",
                    [foundation.ToString()] = "20",
                },
            },
        };

        store.SaveEpochRewards(epochBlockHash, payload);

        Assert.That(store.TryGetAccountReward(foundation, epochBlockHash, out UInt256 foundationReward), Is.True);
        Assert.That(foundationReward, Is.EqualTo((UInt256)30));
    }

    [Test]
    public void SaveEpochRewards_WhenEmptyEpoch_ShouldRoundTripAndReturnFalseForAccountReward()
    {
        IDb db = new MemDb();
        using RewardsStore store = CreateStore(db);
        Hash256 epochBlockHash = TestItem.KeccakA;
        Address account = Address.FromNumber(1);

        store.SaveEpochRewards(epochBlockHash, new Dictionary<string, Dictionary<string, Dictionary<string, string>>>());

        Assert.That(store.HasEpochRewards(epochBlockHash), Is.True);
        Assert.That(store.TryGetEpochRewards(epochBlockHash, out Dictionary<string, Dictionary<string, Dictionary<string, string>>>? loaded), Is.True);
        Assert.That(loaded, Is.Empty);
        Assert.That(store.TryGetAccountReward(account, epochBlockHash, out _), Is.False);
    }

    [Test]
    public void OnBlockAddedToMain_WhenEpochBlockIsProcessed_ShouldPersistRewards()
    {
        IDb db = new MemDb();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IXdcReleaseSpec xdcSpec = Substitute.For<IXdcReleaseSpec>();
        xdcSpec.SwitchBlock.Returns(0UL);
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcSpec);

        const ulong epochBlockNumber = 900;
        Address account = Address.FromNumber(1);
        BlockReward[] rewards = [new BlockReward(account, (UInt256)42)];
        Block block = new(BuildCheckpointHeader(epochBlockNumber));
        ((XdcBlockHeader)block.Header).ProcessedRewards = rewards;

        blockTree.WasProcessed(epochBlockNumber, block.Hash!).Returns(true);
        blockTree.Head.Returns(block);
        blockTree.FindBestSuggestedHeader().Returns(block.Header);
        epochSwitchManager.IsEpochSwitchAtBlock(Arg.Any<XdcBlockHeader>()).Returns(true);

        using RewardsStore store = CreateStore(
            db,
            blockTree: blockTree,
            epochSwitchManager: epochSwitchManager,
            specProvider: specProvider);
        store.Start();

        blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.HasEpochRewards(block.Hash!), Is.True);
            Assert.That(store.TryGetAccountReward(account, block.Hash!, out UInt256 savedReward), Is.True);
            Assert.That(savedReward, Is.EqualTo((UInt256)42));
        }
    }

    [Test]
    public void OnBlockAddedToMain_WhenBlockWasNotProcessed_ShouldNotPersistRewards()
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

        Assert.That(store.HasEpochRewards(block.Hash!), Is.False);
    }

    [Test]
    public void OnBlockAddedToMain_WhenSyncing_ShouldPersistProcessedRewards()
    {
        IDb db = new MemDb();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IXdcReleaseSpec xdcSpec = Substitute.For<IXdcReleaseSpec>();
        xdcSpec.SwitchBlock.Returns(0UL);
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcSpec);
        Block block = new(BuildCheckpointHeader(900));
        Address account = Address.FromNumber(1);
        ((XdcBlockHeader)block.Header).ProcessedRewards = [new BlockReward(account, (UInt256)42)];

        blockTree.WasProcessed(block.Number, block.Hash!).Returns(true);
        blockTree.Head.Returns((Block?)null);
        blockTree.FindBestSuggestedHeader().Returns((BlockHeader?)null);
        epochSwitchManager.IsEpochSwitchAtBlock(Arg.Any<XdcBlockHeader>()).Returns(true);

        using RewardsStore store = CreateStore(
            db,
            blockTree: blockTree,
            epochSwitchManager: epochSwitchManager,
            specProvider: specProvider);
        store.Start();

        blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.HasEpochRewards(block.Hash!), Is.True);
            Assert.That(store.TryGetAccountReward(account, block.Hash!, out UInt256 savedReward), Is.True);
            Assert.That(savedReward, Is.EqualTo((UInt256)42));
        }
    }

    [Test]
    public void SaveEpochRewards_WhenSameHashIsSavedAgain_ShouldOverwriteRewardPayload()
    {
        IDb db = new MemDb();
        using RewardsStore store = CreateStore(db);
        Hash256 epochBlockHash = TestItem.KeccakA;

        Dictionary<string, Dictionary<string, Dictionary<string, string>>> initialPayload = new()
        {
            [XdcConstants.RpcRewardSectionMasternode] = new()
            {
                [Address.FromNumber(1).ToString()] = new() { [Address.FromNumber(2).ToString()] = "100" },
            },
        };
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> updatedPayload = new()
        {
            [XdcConstants.RpcRewardSectionMasternode] = new()
            {
                [Address.FromNumber(1).ToString()] = new() { [Address.FromNumber(2).ToString()] = "200" },
            },
        };

        store.SaveEpochRewards(epochBlockHash, initialPayload);
        store.SaveEpochRewards(epochBlockHash, updatedPayload);

        Assert.That(store.TryGetEpochRewards(epochBlockHash, out Dictionary<string, Dictionary<string, Dictionary<string, string>>>? loaded), Is.True);
        Assert.That(loaded![XdcConstants.RpcRewardSectionMasternode][Address.FromNumber(1).ToString()][Address.FromNumber(2).ToString()], Is.EqualTo("200"));
    }

    private static RewardsStore CreateStore(
        IDb db,
        IBlockTree? blockTree = null,
        IEpochSwitchManager? epochSwitchManager = null,
        ISpecProvider? specProvider = null,
        ILogManager? logManager = null) =>
        new(
            db,
            blockTree ?? Substitute.For<IBlockTree>(),
            epochSwitchManager ?? Substitute.For<IEpochSwitchManager>(),
            specProvider ?? Substitute.For<ISpecProvider>(),
            logManager ?? LimboLogs.Instance);

    private static XdcBlockHeader BuildCheckpointHeader(ulong number) =>
        Build.A.XdcBlockHeader()
            .WithNumber(number)
            .WithExtraConsensusData(new ExtraFieldsV2(number, Build.A.QuorumCertificate().TestObject))
            .TestObject;
}
