// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
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

        store.HasEpochRewards(120).Should().BeTrue();
        store.TryGetAccountReward(account, 120, out UInt256 savedReward).Should().BeTrue();
        savedReward.Should().Be((UInt256)30);
    }

    [Test]
    public void TryGetAccountReward_WhenNoRewardForAccount_ShouldReturnFalse()
    {
        IDb db = new MemDb();
        RewardsStore store = new(db);
        Address savedAccount = Address.FromNumber(1);
        Address missingAccount = Address.FromNumber(2);

        store.SaveEpochRewards(120, [new BlockReward(savedAccount, (UInt256)10)]);

        store.TryGetAccountReward(missingAccount, 120, out _).Should().BeFalse();
    }

    [Test]
    public void TryGetRetainedRange_WhenEpochRewardsWereSaved_ShouldReturnSavedBounds()
    {
        IDb db = new MemDb();
        RewardsStore store = new(db);
        Address account = Address.FromNumber(1);

        store.SaveEpochRewards(120, [new BlockReward(account, (UInt256)10)]);
        store.SaveEpochRewards(180, [new BlockReward(account, (UInt256)20)]);

        store.TryGetRetainedRange(out ulong oldest, out ulong newest).Should().BeTrue();
        oldest.Should().Be(120);
        newest.Should().Be(180);
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

        store.HasEpochRewards(oldEpoch).Should().BeFalse();
        store.TryGetAccountReward(account, oldEpoch, out _).Should().BeFalse();

        ulong newOldestKeptEpoch = 2;
        store.HasEpochRewards(newOldestKeptEpoch).Should().BeTrue();
        store.TryGetAccountReward(account, newOldestKeptEpoch, out UInt256 keptReward).Should().BeTrue();
        keptReward.Should().Be((UInt256)newOldestKeptEpoch);

        store.TryGetRetainedRange(out ulong oldest, out ulong newest).Should().BeTrue();
        oldest.Should().Be(newOldestKeptEpoch);
        newest.Should().Be(latestEpoch);
    }
}
