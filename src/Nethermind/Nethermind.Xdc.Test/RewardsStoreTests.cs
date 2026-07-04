// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[TestFixture]
public class RewardsStoreTests
{
    private static Hash256 EpochHash(ulong epochBlockNumber) =>
        Keccak.Compute(BitConverter.GetBytes(epochBlockNumber).PadLeft(32));

    private static IBlockTree CreateBlockTree(params (ulong number, Hash256 hash)[] epochs)
    {
        Dictionary<Hash256, BlockHeader> headers = [];
        foreach ((ulong number, Hash256 hash) in epochs)
        {
            headers[hash] = Build.A.BlockHeader.WithNumber(number).WithHash(hash).TestObject;
        }

        IBlockTree tree = Substitute.For<IBlockTree>();
        tree.FindHeader(Arg.Any<Hash256>(), Arg.Any<ulong?>())
            .Returns(call =>
            {
                Hash256 hash = call.Arg<Hash256>();
                return headers.TryGetValue(hash, out BlockHeader? header) ? header : null;
            });

        return tree;
    }

    [Test]
    public void SaveEpochRewards_WhenSameAccountHasMultipleRewards_ShouldAggregateAndReadRewardByAccount()
    {
        IDb db = new MemDb();
        const ulong epoch = 120;
        Hash256 epochHash = EpochHash(epoch);
        RewardsStore store = new(db, CreateBlockTree((epoch, epochHash)));
        Address account = Address.FromNumber(1);
        Address signer1 = Address.FromNumber(10);
        Address signer2 = Address.FromNumber(11);

        Dictionary<string, Dictionary<string, Dictionary<string, string>>> payload = new()
        {
            [XdcConstants.RpcRewardSectionMasternode] = new()
            {
                [signer1.ToString()] = new() { [account.ToString()] = "10" },
                [signer2.ToString()] = new() { [account.ToString()] = "20" },
            },
        };

        store.SaveEpochRewards(epochHash, payload);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.HasEpochRewards(epochHash), Is.True);
            Assert.That(store.TryGetAccountReward(account, epochHash, out UInt256 savedReward), Is.True);
            Assert.That(savedReward, Is.EqualTo((UInt256)30));
        }
    }

    [Test]
    public void TryGetAccountReward_WhenNoRewardForAccount_ShouldReturnFalse()
    {
        IDb db = new MemDb();
        const ulong epoch = 120;
        Hash256 epochHash = EpochHash(epoch);
        RewardsStore store = new(db, CreateBlockTree((epoch, epochHash)));
        Address savedAccount = Address.FromNumber(1);
        Address missingAccount = Address.FromNumber(2);
        Address signer = Address.FromNumber(10);

        Dictionary<string, Dictionary<string, Dictionary<string, string>>> payload = new()
        {
            [XdcConstants.RpcRewardSectionMasternode] = new()
            {
                [signer.ToString()] = new() { [savedAccount.ToString()] = "10" },
            },
        };

        store.SaveEpochRewards(epochHash, payload);

        Assert.That(store.TryGetAccountReward(missingAccount, epochHash, out _), Is.False);
    }

    [Test]
    public void SaveEpochRewards_ShouldRoundTripNestedRewardPayload()
    {
        IDb db = new MemDb();
        const ulong epoch = 120;
        Hash256 epochHash = EpochHash(epoch);
        RewardsStore store = new(db, CreateBlockTree((epoch, epochHash)));
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> payload = new()
        {
            [XdcConstants.RpcRewardSectionMasternode] = new()
            {
                [Address.FromNumber(1).ToString()] = new() { [Address.FromNumber(2).ToString()] = "1000" },
            },
        };

        store.SaveEpochRewards(epochHash, payload);

        Assert.That(store.TryGetEpochRewards(epochHash, out Dictionary<string, Dictionary<string, Dictionary<string, string>>>? loaded), Is.True);
        Assert.That(loaded![XdcConstants.RpcRewardSectionMasternode][Address.FromNumber(1).ToString()][Address.FromNumber(2).ToString()], Is.EqualTo("1000"));
    }

    [Test]
    public void SaveEpochRewards_WhenAccountAppearsAcrossSections_ShouldAggregateAccountReward()
    {
        IDb db = new MemDb();
        const ulong epoch = 120;
        Hash256 epochHash = EpochHash(epoch);
        RewardsStore store = new(db, CreateBlockTree((epoch, epochHash)));
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

        store.SaveEpochRewards(epochHash, payload);

        Assert.That(store.TryGetAccountReward(account, epochHash, out UInt256 savedReward), Is.True);
        Assert.That(savedReward, Is.EqualTo((UInt256)150));
    }

    [Test]
    public void SaveEpochRewards_WhenFoundationWalletAppearsUnderMultipleSigners_ShouldAggregateFoundationReward()
    {
        IDb db = new MemDb();
        const ulong epoch = 120;
        Hash256 epochHash = EpochHash(epoch);
        RewardsStore store = new(db, CreateBlockTree((epoch, epochHash)));
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

        store.SaveEpochRewards(epochHash, payload);

        Assert.That(store.TryGetAccountReward(foundation, epochHash, out UInt256 foundationReward), Is.True);
        Assert.That(foundationReward, Is.EqualTo((UInt256)30));
    }

    [Test]
    public void SaveEpochRewards_WhenEmptyEpoch_ShouldRoundTripAndReturnFalseForAccountReward()
    {
        IDb db = new MemDb();
        const ulong epoch = 120;
        Hash256 epochHash = EpochHash(epoch);
        RewardsStore store = new(db, CreateBlockTree((epoch, epochHash)));
        Address account = Address.FromNumber(1);

        store.SaveEpochRewards(epochHash, []);

        Assert.That(store.HasEpochRewards(epochHash), Is.True);
        Assert.That(store.TryGetEpochRewards(epochHash, out Dictionary<string, Dictionary<string, Dictionary<string, string>>>? loaded), Is.True);
        Assert.That(loaded, Is.Empty);
        Assert.That(store.TryGetAccountReward(account, epochHash, out _), Is.False);
    }

    [Test]
    public void SaveEpochRewards_WhenEpochIsOutsideRetentionWindow_ShouldPruneOlderEntries()
    {
        IDb db = new MemDb();
        const int retention = 3;
        Address account = Address.FromNumber(1);
        Address signer = Address.FromNumber(10);

        ulong oldEpoch = 1;
        ulong latestEpoch = (ulong)retention + 1;

        List<(ulong number, Hash256 hash)> epochs = [];
        for (ulong epoch = oldEpoch; epoch <= latestEpoch; epoch++)
        {
            epochs.Add((epoch, EpochHash(epoch)));
        }

        IBlockTree blockTree = CreateBlockTree([.. epochs]);
        RewardsStore store = new(db, blockTree, retention);

        for (ulong epoch = oldEpoch; epoch <= latestEpoch; epoch++)
        {
            Dictionary<string, Dictionary<string, Dictionary<string, string>>> payload = new()
            {
                [XdcConstants.RpcRewardSectionMasternode] = new()
                {
                    [signer.ToString()] = new() { [account.ToString()] = epoch.ToString() },
                },
            };
            store.SaveEpochRewards(EpochHash(epoch), payload);
        }

        Hash256 oldEpochHash = EpochHash(oldEpoch);
        ulong newOldestKeptEpoch = 2;
        Hash256 newOldestKeptEpochHash = EpochHash(newOldestKeptEpoch);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.HasEpochRewards(oldEpochHash), Is.False);
            Assert.That(store.TryGetAccountReward(account, oldEpochHash, out _), Is.False);
            Assert.That(store.HasEpochRewards(newOldestKeptEpochHash), Is.True);
            Assert.That(store.TryGetAccountReward(account, newOldestKeptEpochHash, out UInt256 keptReward), Is.True);
            Assert.That(keptReward, Is.EqualTo((UInt256)newOldestKeptEpoch));
        }
    }

    [Test]
    public void SaveEpochRewards_WhenSameHashIsSavedAgain_ShouldOverwriteRewardPayload()
    {
        IDb db = new MemDb();
        const ulong epoch = 120;
        Hash256 epochHash = EpochHash(epoch);
        RewardsStore store = new(db, CreateBlockTree((epoch, epochHash)));

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

        store.SaveEpochRewards(epochHash, initialPayload);
        store.SaveEpochRewards(epochHash, updatedPayload);

        Assert.That(store.TryGetEpochRewards(epochHash, out Dictionary<string, Dictionary<string, Dictionary<string, string>>>? loaded), Is.True);
        Assert.That(loaded![XdcConstants.RpcRewardSectionMasternode][Address.FromNumber(1).ToString()][Address.FromNumber(2).ToString()], Is.EqualTo("200"));
    }
}
