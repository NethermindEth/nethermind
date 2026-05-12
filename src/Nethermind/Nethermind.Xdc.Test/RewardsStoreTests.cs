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
        RewardsStore store = new(db);
        Address account = Address.FromNumber(1);

        const ulong oldEpoch = 100;
        const ulong keptEpoch = 6_000;
        const ulong latestEpoch = 25_000; // cutoff = 5_000 when retention is 20_000

        store.SaveEpochRewards(oldEpoch, [new BlockReward(account, (UInt256)10)]);
        store.SaveEpochRewards(keptEpoch, [new BlockReward(account, (UInt256)20)]);
        store.SaveEpochRewards(latestEpoch, [new BlockReward(account, (UInt256)30)]);

        store.HasEpochRewards(oldEpoch).Should().BeFalse();
        store.TryGetAccountReward(account, oldEpoch, out _).Should().BeFalse();

        store.HasEpochRewards(keptEpoch).Should().BeTrue();
        store.TryGetAccountReward(account, keptEpoch, out UInt256 keptReward).Should().BeTrue();
        keptReward.Should().Be((UInt256)20);

        store.TryGetRetainedRange(out ulong oldest, out ulong newest).Should().BeTrue();
        oldest.Should().Be(keptEpoch);
        newest.Should().Be(latestEpoch);
    }
}
