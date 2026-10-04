// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Text.Json;
using Nethermind.Blockchain;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

public class RewardsStoreTests
{
    [Test]
    public void Stored_rewards_keep_the_reflection_serializer_format()
    {
        // Rewards persist in the database, so the stored bytes must match what earlier versions wrote and read back the same.
        MemDb db = new();
        RewardsStore store = new(db, Substitute.For<IBlockTree>(), Substitute.For<IEpochSwitchManager>(), Substitute.For<ISpecProvider>(), LimboLogs.Instance);
        XdcEpochRewards rewards = new()
        {
            Signers = { ["0x01"] = new XdcRewardLog { Sign = 3, Reward = "100" } },
            Rewards = { ["0x01"] = new() { ["0x02"] = "50" } },
            SignersProtector = { ["0x03"] = new XdcRewardLog { Sign = 1, Reward = "7" } },
            RewardsProtector = { ["0x03"] = new() { ["0x04"] = "8" } },
            SignersObserver = { ["0x05"] = new XdcRewardLog { Sign = 2, Reward = "9" } },
            RewardsObserver = { ["0x05"] = new() { ["0x06"] = "10" } },
        };
        byte[] reflectionBytes = JsonSerializer.SerializeToUtf8Bytes(rewards);

        store.SaveEpochRewards(TestItem.KeccakA, rewards);

        Assert.That(db.Values.Single(), Is.EqualTo(reflectionBytes));
        Assert.That(store.TryGetEpochRewards(TestItem.KeccakA, out XdcEpochRewards? read), Is.True);
        Assert.That(JsonSerializer.SerializeToUtf8Bytes(read), Is.EqualTo(reflectionBytes));
    }
}
