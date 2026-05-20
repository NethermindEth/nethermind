// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Rewards;

[Parallelizable(ParallelScope.All)]
public class RewardCalculatorTests
{
    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(3L, 1L, 2, 5312500000000000000L, 3750000000000000000L, TestName = "Two_uncles_from_the_same_coinbase")]
    [TestCase(3L, 1L, 1, 5156250000000000000L, 3750000000000000000L, TestName = "One_uncle")]
    [TestCase(3L, 0L, 0, 5000000000000000000L, 0L, TestName = "No_uncles")]
    public void Frontier_era_rewards(long blockNumber, long uncleNumber, int uncleCount,
        long expectedMinerReward, long expectedUncleReward)
    {
        Block[] uncles = Enumerable.Range(0, uncleCount)
            .Select(_ => Build.A.Block.WithNumber(uncleNumber).TestObject).ToArray();
        Block block = Build.A.Block.WithNumber(blockNumber).WithUncles(uncles).TestObject;

        RewardCalculator calculator = new(MainnetSpecProvider.Instance);
        BlockReward[] rewards = calculator.CalculateRewards(block);

        Assert.That(rewards.Length, Is.EqualTo(1 + uncleCount));
        Assert.That((long)rewards[0].Value, Is.EqualTo(expectedMinerReward), "miner");
        for (int i = 1; i < rewards.Length; i++)
            Assert.That((long)rewards[i].Value, Is.EqualTo(expectedUncleReward), $"uncle{i}");
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase("Byzantium", 3187500000000000000L, 2250000000000000000L, TestName = "Byzantium_reward_two_uncles")]
    [TestCase("ConstantinopleFix", 2125000000000000000L, 1500000000000000000L, TestName = "Constantinople_reward_two_uncles")]
    public void Post_frontier_two_uncle_rewards(string fork, long expectedMinerReward, long expectedUncleReward)
    {
        long blockNumber = fork == "Byzantium"
            ? MainnetSpecProvider.ByzantiumBlockNumber
            : MainnetSpecProvider.ConstantinopleFixBlockNumber;
        Block uncle = Build.A.Block.WithNumber(blockNumber - 2).TestObject;
        Block uncle2 = Build.A.Block.WithNumber(blockNumber - 2).TestObject;
        Block block = Build.A.Block.WithNumber(blockNumber).WithUncles(uncle, uncle2).TestObject;

        RewardCalculator calculator = new(MainnetSpecProvider.Instance);
        BlockReward[] rewards = calculator.CalculateRewards(block);

        Assert.That(rewards.Length, Is.EqualTo(3));
        Assert.That((long)rewards[0].Value, Is.EqualTo(expectedMinerReward), "miner");
        Assert.That((long)rewards[1].Value, Is.EqualTo(expectedUncleReward), "uncle1");
        Assert.That((long)rewards[2].Value, Is.EqualTo(expectedUncleReward), "uncle2");
    }
}
