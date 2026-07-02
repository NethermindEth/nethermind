// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[TestFixture]
public class RewardsStoreTests
{
    private static Hash256 EpochHash(ulong epochBlockNumber) =>
        Keccak.Compute(BitConverter.GetBytes(epochBlockNumber).PadLeft(32));

    [Test]
    public void SaveEpochRewards_WhenSameAccountHasMultipleRewards_ShouldAggregateAndReadRewardByAccount()
    {
        IDb db = new MemDb();
        RewardsStore store = new(db);
        Address account = Address.FromNumber(1);
        Address signer1 = Address.FromNumber(10);
        Address signer2 = Address.FromNumber(11);
        const ulong epoch = 120;
        Hash256 epochHash = EpochHash(epoch);

        Dictionary<string, Dictionary<string, Dictionary<string, string>>> payload = new()
        {
            [XdcConstants.RpcRewardSectionMasternode] = new()
            {
                [signer1.ToString()] = new() { [account.ToString()] = "10" },
                [signer2.ToString()] = new() { [account.ToString()] = "20" },
            },
        };

        store.SaveEpochRewards(epochHash, epoch, payload);

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
        RewardsStore store = new(db);
        Address savedAccount = Address.FromNumber(1);
        Address missingAccount = Address.FromNumber(2);
        Address signer = Address.FromNumber(10);
        const ulong epoch = 120;
        Hash256 epochHash = EpochHash(epoch);

        Dictionary<string, Dictionary<string, Dictionary<string, string>>> payload = new()
        {
            [XdcConstants.RpcRewardSectionMasternode] = new()
            {
                [signer.ToString()] = new() { [savedAccount.ToString()] = "10" },
            },
        };

        store.SaveEpochRewards(epochHash, epoch, payload);

        Assert.That(store.TryGetAccountReward(missingAccount, epochHash, out _), Is.False);
    }

    [Test]
    public void TryGetRetainedRange_WhenEpochRewardsWereSaved_ShouldReturnSavedBounds()
    {
        IDb db = new MemDb();
        RewardsStore store = new(db);
        Address account = Address.FromNumber(1);
        Address signer = Address.FromNumber(10);
        const ulong epoch10 = 120;
        const ulong epoch20 = 180;

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

        store.SaveEpochRewards(EpochHash(epoch10), epoch10, payload10);
        store.SaveEpochRewards(EpochHash(epoch20), epoch20, payload20);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.TryGetRetainedRange(out ulong oldest, out ulong newest), Is.True);
            Assert.That(oldest, Is.EqualTo(epoch10));
            Assert.That(newest, Is.EqualTo(epoch20));
        }
    }

    [Test]
    public void SaveEpochRewards_ShouldRoundTripNestedRewardPayload()
    {
        IDb db = new MemDb();
        RewardsStore store = new(db);
        const ulong epoch = 120;
        Hash256 epochHash = EpochHash(epoch);
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> payload = new()
        {
            [XdcConstants.RpcRewardSectionMasternode] = new()
            {
                [Address.FromNumber(1).ToString()] = new() { [Address.FromNumber(2).ToString()] = "1000" },
            },
        };

        store.SaveEpochRewards(epochHash, epoch, payload);

        Assert.That(store.TryGetEpochRewards(epochHash, out Dictionary<string, Dictionary<string, Dictionary<string, string>>>? loaded), Is.True);
        Assert.That(loaded![XdcConstants.RpcRewardSectionMasternode][Address.FromNumber(1).ToString()][Address.FromNumber(2).ToString()], Is.EqualTo("1000"));
    }

    [Test]
    public void SaveEpochRewards_WhenAccountAppearsAcrossSections_ShouldAggregateAccountReward()
    {
        IDb db = new MemDb();
        RewardsStore store = new(db);
        Address account = Address.FromNumber(1);
        Address signer = Address.FromNumber(10);
        const ulong epoch = 120;
        Hash256 epochHash = EpochHash(epoch);

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

        store.SaveEpochRewards(epochHash, epoch, payload);

        Assert.That(store.TryGetAccountReward(account, epochHash, out UInt256 savedReward), Is.True);
        Assert.That(savedReward, Is.EqualTo((UInt256)150));
    }

    [Test]
    public void TryGetAccountReward_WhenSignerMetadataContainsAccountKey_ShouldIgnoreSignerMetadata()
    {
        IDb db = new MemDb();
        RewardsStore store = new(db);
        Address account = Address.FromNumber(1);
        Address signer = Address.FromNumber(10);
        const ulong epoch = 120;
        Hash256 epochHash = EpochHash(epoch);

        Dictionary<string, Dictionary<string, Dictionary<string, string>>> payload = new()
        {
            [XdcConstants.RpcRewardSectionMasternode] = new()
            {
                [signer.ToString()] = new() { [account.ToString()] = "100" },
            },
            [XdcConstants.RpcSignerSectionMasternode] = new()
            {
                [account.ToString()] = new()
                {
                    [XdcConstants.RpcSignerReward] = "1000",
                    [XdcConstants.RpcSignerCount] = "10",
                },
            },
        };

        store.SaveEpochRewards(epochHash, epoch, payload);

        Assert.That(store.TryGetAccountReward(account, epochHash, out UInt256 savedReward), Is.True);
        Assert.That(savedReward, Is.EqualTo((UInt256)100));
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
        const ulong epoch = 120;
        Hash256 epochHash = EpochHash(epoch);

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

        store.SaveEpochRewards(epochHash, epoch, payload);

        Assert.That(store.TryGetAccountReward(foundation, epochHash, out UInt256 foundationReward), Is.True);
        Assert.That(foundationReward, Is.EqualTo((UInt256)30));
    }

    [Test]
    public void SaveEpochRewards_WhenEmptyEpoch_ShouldRoundTripAndReturnFalseForAccountReward()
    {
        IDb db = new MemDb();
        RewardsStore store = new(db);
        Address account = Address.FromNumber(1);
        const ulong epoch = 120;
        Hash256 epochHash = EpochHash(epoch);

        store.SaveEpochRewards(epochHash, epoch, []);

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
            store.SaveEpochRewards(EpochHash(epoch), epoch, payload);
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
            Assert.That(store.TryGetRetainedRange(out ulong oldest, out ulong newest), Is.True);
            Assert.That(oldest, Is.EqualTo(newOldestKeptEpoch));
            Assert.That(newest, Is.EqualTo(latestEpoch));
        }
    }

    [Test]
    public void SaveEpochRewards_WhenSameNumberHasNewHash_ShouldReplaceOldRewardEntry()
    {
        IDb db = new MemDb();
        RewardsStore store = new(db);
        const ulong epoch = 120;
        Hash256 oldHash = EpochHash(epoch);
        Hash256 newHash = Keccak.Compute("reorged-epoch-block");

        Dictionary<string, Dictionary<string, Dictionary<string, string>>> oldPayload = new()
        {
            [XdcConstants.RpcRewardSectionMasternode] = new()
            {
                [Address.FromNumber(1).ToString()] = new() { [Address.FromNumber(2).ToString()] = "100" },
            },
        };
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> newPayload = new()
        {
            [XdcConstants.RpcRewardSectionMasternode] = new()
            {
                [Address.FromNumber(1).ToString()] = new() { [Address.FromNumber(2).ToString()] = "200" },
            },
        };

        store.SaveEpochRewards(oldHash, epoch, oldPayload);
        store.SaveEpochRewards(newHash, epoch, newPayload);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.HasEpochRewards(oldHash), Is.False);
            Assert.That(store.TryGetEpochRewards(oldHash, out _), Is.False);
            Assert.That(store.TryGetEpochRewards(newHash, out Dictionary<string, Dictionary<string, Dictionary<string, string>>>? loaded), Is.True);
            Assert.That(loaded![XdcConstants.RpcRewardSectionMasternode][Address.FromNumber(1).ToString()][Address.FromNumber(2).ToString()], Is.EqualTo("200"));
        }
    }
}
