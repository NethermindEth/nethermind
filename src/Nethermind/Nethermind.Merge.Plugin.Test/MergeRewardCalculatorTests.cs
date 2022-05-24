//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
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
            MergeRewardCalculator calculator = new(new RewardCalculator(RopstenSpecProvider.Instance), poSSwitcher);
            BlockReward[] rewards = calculator.CalculateRewards(block);
            
            Assert.AreEqual(3, rewards.Length);
            Assert.AreEqual(5312500000000000000, (long)rewards[0].Value, "miner");
            Assert.AreEqual(3750000000000000000, (long)rewards[1].Value, "uncle1");
            Assert.AreEqual(3750000000000000000, (long)rewards[2].Value, "uncle2");
            
            rewards = calculator.CalculateRewards(block2);
            
            Assert.AreEqual(0, rewards.Length);
        }
        
        [Test]
        public void One_uncle()
        {
            Block uncle = Build.A.Block.WithNumber(1).TestObject;
            Block block = Build.A.Block.WithNumber(3).WithUncles(uncle).WithTotalDifficulty(1L).WithDifficulty(23).TestObject;
            Block block2 = Build.A.Block.WithNumber(4).WithUncles(uncle).WithTotalDifficulty(3L).WithDifficulty(0).TestObject;

            PoSSwitcher poSSwitcher = CreatePosSwitcher();
            poSSwitcher.TryUpdateTerminalBlock(block.Header);

            MergeRewardCalculator calculator = new(new RewardCalculator(RopstenSpecProvider.Instance), poSSwitcher);
            BlockReward[] rewards = calculator.CalculateRewards(block);
            
            Assert.AreEqual(2, rewards.Length);
            Assert.AreEqual(5156250000000000000, (long)rewards[0].Value, "miner");
            Assert.AreEqual(3750000000000000000, (long)rewards[1].Value, "uncle1");
            
            rewards = calculator.CalculateRewards(block2);
            Assert.AreEqual(0, rewards.Length);
        }
        
        [Test]
        public void No_uncles()
        {
            Block block = Build.A.Block.WithNumber(2).WithTotalDifficulty(1L).WithDifficulty(300).TestObject;
            Block block2 = Build.A.Block.WithNumber(3).WithTotalDifficulty(3L).WithDifficulty(0).TestObject;
            
            PoSSwitcher poSSwitcher = CreatePosSwitcher();
            poSSwitcher.TryUpdateTerminalBlock(block.Header);

            MergeRewardCalculator calculator = new(new RewardCalculator(RopstenSpecProvider.Instance), poSSwitcher);
            BlockReward[] rewards = calculator.CalculateRewards(block);
            
            Assert.AreEqual(1, rewards.Length);
            Assert.AreEqual(5000000000000000000, (long)rewards[0].Value, "miner");

            rewards = calculator.CalculateRewards(block2);
            Assert.AreEqual(0, rewards.Length);
        }
        
        [Test]
        public void Byzantium_reward_two_uncles()
        {
            long blockNumber = RopstenSpecProvider.ByzantiumBlockNumber;
            Block uncle = Build.A.Block.WithNumber(blockNumber - 2).TestObject;
            Block uncle2 = Build.A.Block.WithNumber(blockNumber - 2).TestObject;
            Block block = Build.A.Block.WithNumber(blockNumber).WithUncles(uncle, uncle2).WithTotalDifficulty(1L).WithDifficulty(300).TestObject;
            Block block2 = Build.A.Block.WithNumber(blockNumber + 1).WithUncles(uncle, uncle2).WithTotalDifficulty(3L).WithDifficulty(0).TestObject;
            
            PoSSwitcher poSSwitcher = CreatePosSwitcher();
            poSSwitcher.TryUpdateTerminalBlock(block.Header);
            
            MergeRewardCalculator calculator = new(new RewardCalculator(RopstenSpecProvider.Instance), poSSwitcher);
            BlockReward[] rewards = calculator.CalculateRewards(block);
            
            Assert.AreEqual(3, rewards.Length);
            Assert.AreEqual(3187500000000000000, (long)rewards[0].Value, "miner");
            Assert.AreEqual(2250000000000000000, (long)rewards[1].Value, "uncle1");
            Assert.AreEqual(2250000000000000000, (long)rewards[2].Value, "uncle2");
            
            rewards = calculator.CalculateRewards(block2);
            
            Assert.AreEqual(0, rewards.Length);
        }
        
        [Test]
        public void Constantinople_reward_two_uncles()
        {
            long blockNumber = RopstenSpecProvider.ConstantinopleBlockNumber;
            Block uncle = Build.A.Block.WithNumber(blockNumber - 2).TestObject;
            Block uncle2 = Build.A.Block.WithNumber(blockNumber - 2).TestObject;
            Block block = Build.A.Block.WithNumber(blockNumber).WithUncles(uncle, uncle2).WithTotalDifficulty(1L).WithDifficulty(300).TestObject;
            Block block2 = Build.A.Block.WithNumber(blockNumber + 1).WithUncles(uncle, uncle2).WithTotalDifficulty(3L).WithDifficulty(0).TestObject;

            
            PoSSwitcher poSSwitcher = CreatePosSwitcher();
            poSSwitcher.TryUpdateTerminalBlock(block.Header);
            MergeRewardCalculator calculator = new(new RewardCalculator(RopstenSpecProvider.Instance), poSSwitcher);
            BlockReward[] rewards = calculator.CalculateRewards(block);
            
            Assert.AreEqual(3, rewards.Length);
            Assert.AreEqual(2125000000000000000, (long)rewards[0].Value, "miner");
            Assert.AreEqual(1500000000000000000, (long)rewards[1].Value, "uncle1");
            Assert.AreEqual(1500000000000000000, (long)rewards[2].Value, "uncle2");
            
            rewards = calculator.CalculateRewards(block2);
            Assert.AreEqual(0, rewards.Length);
        }

        [Test]
        public void No_block_rewards_calculator()
        {
            Block block = Build.A.Block.WithTotalDifficulty(1L).WithDifficulty(0).TestObject;
            Block block2 = Build.A.Block.WithTotalDifficulty(3L).WithDifficulty(0).TestObject;

            PoSSwitcher poSSwitcher = CreatePosSwitcher();
            poSSwitcher.TryUpdateTerminalBlock(block.Header);
            MergeRewardCalculator calculator = new(NoBlockRewards.Instance, poSSwitcher);
            
            BlockReward[] rewards = calculator.CalculateRewards(block);
            Assert.AreEqual(0,rewards.Length);
            
            rewards = calculator.CalculateRewards(block2);
            Assert.AreEqual(0,rewards.Length);
        }
        
        
        private static PoSSwitcher CreatePosSwitcher()
        {
            IDb db= new MemDb();
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            TestSpecProvider specProvider = new (London.Instance);
            specProvider.TerminalTotalDifficulty = 2;
            MergeConfig? mergeConfig = new() {Enabled = true };
            IBlockCacheService blockCacheService = new BlockCacheService();
            return new PoSSwitcher(mergeConfig, new SyncConfig(), db, blockTree, specProvider, blockCacheService, LimboLogs.Instance);
        }
        
    }
}
