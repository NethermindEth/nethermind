// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test
{
    public class MergeRewardCalculatorTests
    {
        [Test]
        public void Two_uncles_from_the_same_coinbase()
        {
            Block uncle = Build.A.Block.WithNumber(1).TestObject;
            Block uncle2 = Build.A.Block.WithNumber(1).TestObject;
            Block block = Build.A.Block.WithNumber(3).WithUncles(uncle, uncle2).WithTotalDifficulty(1L).WithDifficulty(30).TestObject;
            Block block2 = Build.A.Block.WithNumber(4).WithUncles(uncle, uncle2).WithTotalDifficulty(2L).WithDifficulty(0).TestObject;

            PoSSwitcher poSSwitcher = CreatePosSwitcher();
            poSSwitcher.TryUpdateTerminalBlock(block.Header);
            MergeRewardCalculator calculator = new(new RewardCalculator(MainnetSpecProvider.Instance), poSSwitcher);
            BlockReward[] rewards = calculator.CalculateRewards(block);

            Assert.That(rewards.Length, Is.EqualTo(3));
            Assert.That((long)rewards[0].Value, Is.EqualTo(5312500000000000000), "miner");
            Assert.That((long)rewards[1].Value, Is.EqualTo(3750000000000000000), "uncle1");
            Assert.That((long)rewards[2].Value, Is.EqualTo(3750000000000000000), "uncle2");

            rewards = calculator.CalculateRewards(block2);

            Assert.That(rewards.Length, Is.EqualTo(0));
        }

        [Test]
        public void One_uncle()
        {
            Block uncle = Build.A.Block.WithNumber(1).TestObject;
            Block block = Build.A.Block.WithNumber(3).WithUncles(uncle).WithTotalDifficulty(1L).WithDifficulty(23).TestObject;
            Block block2 = Build.A.Block.WithNumber(4).WithUncles(uncle).WithTotalDifficulty(3L).WithDifficulty(0).TestObject;

            PoSSwitcher poSSwitcher = CreatePosSwitcher();
            poSSwitcher.TryUpdateTerminalBlock(block.Header);

            MergeRewardCalculator calculator = new(new RewardCalculator(MainnetSpecProvider.Instance), poSSwitcher);
            BlockReward[] rewards = calculator.CalculateRewards(block);

            Assert.That(rewards.Length, Is.EqualTo(2));
            Assert.That((long)rewards[0].Value, Is.EqualTo(5156250000000000000), "miner");
            Assert.That((long)rewards[1].Value, Is.EqualTo(3750000000000000000), "uncle1");

            rewards = calculator.CalculateRewards(block2);
            Assert.That(rewards.Length, Is.EqualTo(0));
        }

        [Test]
        public void No_uncles()
        {
            Block block = Build.A.Block.WithNumber(2).WithTotalDifficulty(1L).WithDifficulty(300).TestObject;
            Block block2 = Build.A.Block.WithNumber(3).WithTotalDifficulty(3L).WithDifficulty(0).TestObject;

            PoSSwitcher poSSwitcher = CreatePosSwitcher();
            poSSwitcher.TryUpdateTerminalBlock(block.Header);

            MergeRewardCalculator calculator = new(new RewardCalculator(MainnetSpecProvider.Instance), poSSwitcher);
            BlockReward[] rewards = calculator.CalculateRewards(block);

            Assert.That(rewards.Length, Is.EqualTo(1));
            Assert.That((long)rewards[0].Value, Is.EqualTo(5000000000000000000), "miner");

            rewards = calculator.CalculateRewards(block2);
            Assert.That(rewards.Length, Is.EqualTo(0));
        }

        [Test]
        public void Byzantium_reward_two_uncles()
        {
            long blockNumber = MainnetSpecProvider.ByzantiumBlockNumber;
            Block uncle = Build.A.Block.WithNumber(blockNumber - 2).TestObject;
            Block uncle2 = Build.A.Block.WithNumber(blockNumber - 2).TestObject;
            Block block = Build.A.Block.WithNumber(blockNumber).WithUncles(uncle, uncle2).WithTotalDifficulty(1L).WithDifficulty(300).TestObject;
            Block block2 = Build.A.Block.WithNumber(blockNumber + 1).WithUncles(uncle, uncle2).WithTotalDifficulty(3L).WithDifficulty(0).TestObject;

            PoSSwitcher poSSwitcher = CreatePosSwitcher();
            poSSwitcher.TryUpdateTerminalBlock(block.Header);

            MergeRewardCalculator calculator = new(new RewardCalculator(MainnetSpecProvider.Instance), poSSwitcher);
            BlockReward[] rewards = calculator.CalculateRewards(block);

            Assert.That(rewards.Length, Is.EqualTo(3));
            Assert.That((long)rewards[0].Value, Is.EqualTo(3187500000000000000), "miner");
            Assert.That((long)rewards[1].Value, Is.EqualTo(2250000000000000000), "uncle1");
            Assert.That((long)rewards[2].Value, Is.EqualTo(2250000000000000000), "uncle2");

            rewards = calculator.CalculateRewards(block2);

            Assert.That(rewards.Length, Is.EqualTo(0));
        }

        [Test]
        public void Constantinople_reward_two_uncles()
        {
            long blockNumber = MainnetSpecProvider.ConstantinopleFixBlockNumber;
            Block uncle = Build.A.Block.WithNumber(blockNumber - 2).TestObject;
            Block uncle2 = Build.A.Block.WithNumber(blockNumber - 2).TestObject;
            Block block = Build.A.Block.WithNumber(blockNumber).WithUncles(uncle, uncle2).WithTotalDifficulty(1L).WithDifficulty(300).TestObject;
            Block block2 = Build.A.Block.WithNumber(blockNumber + 1).WithUncles(uncle, uncle2).WithTotalDifficulty(3L).WithDifficulty(0).TestObject;


            PoSSwitcher poSSwitcher = CreatePosSwitcher();
            poSSwitcher.TryUpdateTerminalBlock(block.Header);
            MergeRewardCalculator calculator = new(new RewardCalculator(MainnetSpecProvider.Instance), poSSwitcher);
            BlockReward[] rewards = calculator.CalculateRewards(block);

            Assert.That(rewards.Length, Is.EqualTo(3));
            Assert.That((long)rewards[0].Value, Is.EqualTo(2125000000000000000), "miner");
            Assert.That((long)rewards[1].Value, Is.EqualTo(1500000000000000000), "uncle1");
            Assert.That((long)rewards[2].Value, Is.EqualTo(1500000000000000000), "uncle2");

            rewards = calculator.CalculateRewards(block2);
            Assert.That(rewards.Length, Is.EqualTo(0));
        }

        [Test]
        public void No_block_rewards_calculator()
        {
            Block block = Build.A.Block.WithNumber(1).WithTotalDifficulty(1L).WithDifficulty(0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithTotalDifficulty(3L).WithDifficulty(0).TestObject;

            PoSSwitcher poSSwitcher = CreatePosSwitcher();
            poSSwitcher.TryUpdateTerminalBlock(block.Header);
            MergeRewardCalculator calculator = new(NoBlockRewards.Instance, poSSwitcher);

            BlockReward[] rewards = calculator.CalculateRewards(block);
            Assert.That(rewards.Length, Is.EqualTo(0));

            rewards = calculator.CalculateRewards(block2);
            Assert.That(rewards.Length, Is.EqualTo(0));
        }


        private static PoSSwitcher CreatePosSwitcher()
        {
            IDb db = new MemDb();
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.TerminalTotalDifficulty = 2;
            MergeConfig? mergeConfig = new() { };
            IBlockCacheService blockCacheService = new BlockCacheService();
            return new PoSSwitcher(mergeConfig, new SyncConfig(), db, blockTree, specProvider, LimboLogs.Instance);
        }

    }
}
