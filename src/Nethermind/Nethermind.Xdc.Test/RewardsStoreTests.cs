// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[TestFixture]
public class RewardsStoreTests
{
    [Test]
    public void SaveEpochRewards_WhenSameAccountHasMultipleRewards_ShouldAggregateAndReadRewardByAccount()
    {
        IDb db = new MemDb();
        RewardsStore store = new(db);
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

        store.SaveEpochRewards(120, payload);

        Assert.That(store.HasEpochRewards(120), Is.True);
        Assert.That(store.TryGetAccountReward(account, 120, out UInt256 savedReward), Is.True);
        Assert.That(savedReward, Is.EqualTo((UInt256)30));
    }

    [Test]
    public void TryGetAccountReward_WhenNoRewardForAccount_ShouldReturnFalse()
    {
        IDb db = new MemDb();
        RewardsStore store = new(db);
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

        store.SaveEpochRewards(120, payload);

        Assert.That(store.TryGetAccountReward(missingAccount, 120, out _), Is.False);
    }

    [Test]
    public void TryGetRetainedRange_WhenEpochRewardsWereSaved_ShouldReturnSavedBounds()
    {
        IDb db = new MemDb();
        RewardsStore store = new(db);
        Address account = Address.FromNumber(1);
        Address signer = Address.FromNumber(10);

        Dictionary<string, Dictionary<string, Dictionary<string, string>>> payload10 = new()
        {
            [XdcConstants.RpcRewardSectionMasternode] = new()
            {
                [signer.ToString()] = new() { [account.ToString()] = "10" },
            },
        };
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> payload20 = new()
        {
            [XdcConstants.RpcRewardSectionMasternode] = new()
            {
                [signer.ToString()] = new() { [account.ToString()] = "20" },
            },
        };

        store.SaveEpochRewards(120, payload10);
        store.SaveEpochRewards(180, payload20);

        Assert.That(store.TryGetRetainedRange(out ulong oldest, out ulong newest), Is.True);
        Assert.That(oldest, Is.EqualTo(120));
        Assert.That(newest, Is.EqualTo(180));
    }

    [Test]
    public void SaveEpochRewards_ShouldRoundTripNestedRewardPayload()
    {
        IDb db = new MemDb();
        RewardsStore store = new(db);
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> payload = new()
        {
            [XdcConstants.RpcRewardSectionMasternode] = new()
            {
                [Address.FromNumber(1).ToString()] = new() { [Address.FromNumber(2).ToString()] = "1000" },
            },
        };

        store.SaveEpochRewards(120, payload);

        Assert.That(store.TryGetEpochRewards(120, out Dictionary<string, Dictionary<string, Dictionary<string, string>>>? loaded), Is.True);
        Assert.That(loaded![XdcConstants.RpcRewardSectionMasternode][Address.FromNumber(1).ToString()][Address.FromNumber(2).ToString()], Is.EqualTo("1000"));
    }

    [Test]
    public void SaveEpochRewards_WhenAccountAppearsAcrossSections_ShouldAggregateAccountReward()
    {
        IDb db = new MemDb();
        RewardsStore store = new(db);
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

        store.SaveEpochRewards(120, payload);

        Assert.That(store.TryGetAccountReward(account, 120, out UInt256 savedReward), Is.True);
        Assert.That(savedReward, Is.EqualTo((UInt256)150));
    }

    [Test]
    public void SaveEpochRewards_WhenFoundationWalletAppearsUnderMultipleSigners_ShouldAggregateFoundationReward()
    {
        IDb db = new MemDb();
        RewardsStore store = new(db);
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

        store.SaveEpochRewards(120, payload);

        Assert.That(store.TryGetAccountReward(foundation, 120, out UInt256 foundationReward), Is.True);
        Assert.That(foundationReward, Is.EqualTo((UInt256)30));
    }

    [Test]
    public void SaveEpochRewards_WhenEmptyEpoch_ShouldRoundTripAndReturnFalseForAccountReward()
    {
        IDb db = new MemDb();
        RewardsStore store = new(db);
        Address account = Address.FromNumber(1);

        store.SaveEpochRewards(120, []);

        Assert.That(store.HasEpochRewards(120), Is.True);
        Assert.That(store.TryGetEpochRewards(120, out Dictionary<string, Dictionary<string, Dictionary<string, string>>>? loaded), Is.True);
        Assert.That(loaded, Is.Empty);
        Assert.That(store.TryGetAccountReward(account, 120, out _), Is.False);
    }

    [Test]
    public void SaveEpochRewards_WhenEpochIsOutsideRetentionWindow_ShouldPruneOlderEntries()
    {
        IDb db = new MemDb();
        const int retention = 3;
        RewardsStore store = new(db, retention);
        Address account = Address.FromNumber(1);
        Address signer = Address.FromNumber(10);

        ulong oldEpoch = 1;
        ulong latestEpoch = (ulong)retention + 1;

        for (ulong epoch = oldEpoch; epoch <= latestEpoch; epoch++)
        {
            Dictionary<string, Dictionary<string, Dictionary<string, string>>> payload = new()
            {
                [XdcConstants.RpcRewardSectionMasternode] = new()
                {
                    [signer.ToString()] = new() { [account.ToString()] = epoch.ToString() },
                },
            };
            store.SaveEpochRewards(epoch, payload);
        }

        Assert.That(store.HasEpochRewards(oldEpoch), Is.False);
        Assert.That(store.TryGetAccountReward(account, oldEpoch, out _), Is.False);
        ulong newOldestKeptEpoch = 2;
        Assert.That(store.HasEpochRewards(newOldestKeptEpoch), Is.True);
        Assert.That(store.TryGetAccountReward(account, newOldestKeptEpoch, out UInt256 keptReward), Is.True);
        Assert.That(keptReward, Is.EqualTo((UInt256)newOldestKeptEpoch));
        Assert.That(store.TryGetRetainedRange(out ulong oldest, out ulong newest), Is.True);
        Assert.That(oldest, Is.EqualTo(newOldestKeptEpoch));
        Assert.That(newest, Is.EqualTo(latestEpoch));
    }
}
