// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Rewards
{
    public class RewardCalculatorTests
    {
        [Test, Timeout(Timeout.MaxTestTime)]
        public void Two_uncles_from_the_same_coinbase()
        {
            Block uncle = Build.A.Block.WithNumber(1).TestObject;
            Block uncle2 = Build.A.Block.WithNumber(1).TestObject;
            Block block = Build.A.Block.WithNumber(3).WithUncles(uncle, uncle2).TestObject;

            RewardCalculator calculator = new(RopstenSpecProvider.Instance);
            BlockReward[] rewards = calculator.CalculateRewards(block);

            Assert.AreEqual(3, rewards.Length);
            Assert.AreEqual(5312500000000000000, (long)rewards[0].Value, "miner");
            Assert.AreEqual(3750000000000000000, (long)rewards[1].Value, "uncle1");
            Assert.AreEqual(3750000000000000000, (long)rewards[2].Value, "uncle2");
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void One_uncle()
        {
            Block uncle = Build.A.Block.WithNumber(1).TestObject;
            Block block = Build.A.Block.WithNumber(3).WithUncles(uncle).TestObject;

            RewardCalculator calculator = new(RopstenSpecProvider.Instance);
            BlockReward[] rewards = calculator.CalculateRewards(block);

            Assert.AreEqual(2, rewards.Length);
            Assert.AreEqual(5156250000000000000, (long)rewards[0].Value, "miner");
            Assert.AreEqual(3750000000000000000, (long)rewards[1].Value, "uncle1");
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void No_uncles()
        {
            Block block = Build.A.Block.WithNumber(3).TestObject;

            RewardCalculator calculator = new(RopstenSpecProvider.Instance);
            BlockReward[] rewards = calculator.CalculateRewards(block);

            Assert.AreEqual(1, rewards.Length);
            Assert.AreEqual(5000000000000000000, (long)rewards[0].Value, "miner");
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Byzantium_reward_two_uncles()
        {
            long blockNumber = RopstenSpecProvider.ByzantiumBlockNumber;
            Block uncle = Build.A.Block.WithNumber(blockNumber - 2).TestObject;
            Block uncle2 = Build.A.Block.WithNumber(blockNumber - 2).TestObject;
            Block block = Build.A.Block.WithNumber(blockNumber).WithUncles(uncle, uncle2).TestObject;

            RewardCalculator calculator = new(RopstenSpecProvider.Instance);
            BlockReward[] rewards = calculator.CalculateRewards(block);

            Assert.AreEqual(3, rewards.Length);
            Assert.AreEqual(3187500000000000000, (long)rewards[0].Value, "miner");
            Assert.AreEqual(2250000000000000000, (long)rewards[1].Value, "uncle1");
            Assert.AreEqual(2250000000000000000, (long)rewards[2].Value, "uncle2");
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Constantinople_reward_two_uncles()
        {
            long blockNumber = RopstenSpecProvider.ConstantinopleBlockNumber;
            Block uncle = Build.A.Block.WithNumber(blockNumber - 2).TestObject;
            Block uncle2 = Build.A.Block.WithNumber(blockNumber - 2).TestObject;
            Block block = Build.A.Block.WithNumber(blockNumber).WithUncles(uncle, uncle2).TestObject;

            RewardCalculator calculator = new(RopstenSpecProvider.Instance);
            BlockReward[] rewards = calculator.CalculateRewards(block);

            Assert.AreEqual(3, rewards.Length);
            Assert.AreEqual(2125000000000000000, (long)rewards[0].Value, "miner");
            Assert.AreEqual(1500000000000000000, (long)rewards[1].Value, "uncle1");
            Assert.AreEqual(1500000000000000000, (long)rewards[2].Value, "uncle2");
        }
    }
}
