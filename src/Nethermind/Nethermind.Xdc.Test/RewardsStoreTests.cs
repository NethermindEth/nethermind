// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Rewards;
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

        BlockReward[] rewards =
        [
            new(account, (UInt256)10),
            new(account, (UInt256)20),
        ];

        store.SaveEpochRewards(120, rewards);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.HasEpochRewards(120), Is.True);
            Assert.That(store.TryGetAccountReward(account, 120, out UInt256 savedReward), Is.True);
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

        store.SaveEpochRewards(120, [new BlockReward(savedAccount, (UInt256)10)]);

        Assert.That(store.TryGetAccountReward(missingAccount, 120, out _), Is.False);
    }

    [Test]
    public void TryGetRetainedRange_WhenEpochRewardsWereSaved_ShouldReturnSavedBounds()
    {
        IDb db = new MemDb();
        RewardsStore store = new(db);
        Address account = Address.FromNumber(1);

        store.SaveEpochRewards(120, [new BlockReward(account, (UInt256)10)]);
        store.SaveEpochRewards(180, [new BlockReward(account, (UInt256)20)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.TryGetRetainedRange(out ulong oldest, out ulong newest), Is.True);
            Assert.That(oldest, Is.EqualTo(120));
            Assert.That(newest, Is.EqualTo(180));
        }
    }

    [Test]
    public void SaveEpochRewards_WhenEpochIsOutsideRetentionWindow_ShouldPruneOlderEntries()
    {
        IDb db = new MemDb();
        const int retention = 3;
        RewardsStore store = new(db, retention);
        Address account = Address.FromNumber(1);

        ulong oldEpoch = 1;
        ulong latestEpoch = (ulong)retention + 1;

        for (ulong epoch = oldEpoch; epoch <= latestEpoch; epoch++)
        {
            store.SaveEpochRewards(epoch, [new BlockReward(account, (UInt256)epoch)]);
        }

        using (Assert.EnterMultipleScope())
        {
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
}
