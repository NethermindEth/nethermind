// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[TestFixture]
public class ReadOnlyRewardsStoreTests
{
    [Test]
    public void SaveEpochRewards_ShouldNotMutateInnerStore()
    {
        IDb db = new MemDb();
        RewardsStore inner = new(db);
        ReadOnlyRewardsStore readOnly = new(inner);
        Address account = Address.FromNumber(1);

        readOnly.SaveEpochRewards(120, [new BlockReward(account, (UInt256)10)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inner.HasEpochRewards(120), Is.False);
            Assert.That(readOnly.HasEpochRewards(120), Is.False);
        }
    }

    [Test]
    public void Reads_ShouldDelegateToInnerStore()
    {
        IDb db = new MemDb();
        RewardsStore inner = new(db);
        ReadOnlyRewardsStore readOnly = new(inner);
        Address account = Address.FromNumber(1);

        inner.SaveEpochRewards(120, [new BlockReward(account, (UInt256)10)]);
        inner.SaveEpochRewards(180, [new BlockReward(account, (UInt256)20)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(readOnly.HasEpochRewards(120), Is.True);
            Assert.That(readOnly.TryGetAccountReward(account, 120, out UInt256 rewardAt120), Is.True);
            Assert.That(rewardAt120, Is.EqualTo((UInt256)10));
            Assert.That(readOnly.TryGetRetainedRange(out ulong oldest, out ulong newest), Is.True);
            Assert.That(oldest, Is.EqualTo(120));
            Assert.That(newest, Is.EqualTo(180));
        }
    }
}
