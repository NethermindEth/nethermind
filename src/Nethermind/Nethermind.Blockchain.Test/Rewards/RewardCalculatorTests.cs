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

using Nethermind.Blockchain.Rewards;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Rewards
{
    public class RewardCalculatorTests
    {
        [Test]
        public void Two_uncles_from_the_same_coinbase()
        {
            Block ommer = Build.A.Block.WithNumber(1).TestObject;
            Block ommer2 = Build.A.Block.WithNumber(1).TestObject;
            Block block = Build.A.Block.WithNumber(3).WithOmmers(ommer, ommer2).TestObject;
            
            RewardCalculator calculator = new RewardCalculator(RopstenSpecProvider.Instance);
            BlockReward[] rewards = calculator.CalculateRewards(block);
            
            Assert.AreEqual(3, rewards.Length);
            Assert.AreEqual(5312500000000000000, (long)rewards[0].Value, "miner");
            Assert.AreEqual(3750000000000000000, (long)rewards[1].Value, "uncle1");
            Assert.AreEqual(3750000000000000000, (long)rewards[2].Value, "uncle2");
        }
        
        [Test]
        public void One_uncle()
        {
            Block ommer = Build.A.Block.WithNumber(1).TestObject;
            Block block = Build.A.Block.WithNumber(3).WithOmmers(ommer).TestObject;
            
            RewardCalculator calculator = new RewardCalculator(RopstenSpecProvider.Instance);
            BlockReward[] rewards = calculator.CalculateRewards(block);
            
            Assert.AreEqual(2, rewards.Length);
            Assert.AreEqual(5156250000000000000, (long)rewards[0].Value, "miner");
            Assert.AreEqual(3750000000000000000, (long)rewards[1].Value, "uncle1");
        }
        
        [Test]
        public void No_uncles()
        {
            Block block = Build.A.Block.WithNumber(3).TestObject;
            
            RewardCalculator calculator = new RewardCalculator(RopstenSpecProvider.Instance);
            BlockReward[] rewards = calculator.CalculateRewards(block);
            
            Assert.AreEqual(1, rewards.Length);
            Assert.AreEqual(5000000000000000000, (long)rewards[0].Value, "miner");
        }
        
        [Test]
        public void Byzantium_reward_two_uncles()
        {
            long blockNumber = RopstenSpecProvider.ByzantiumBlockNumber;
            Block ommer = Build.A.Block.WithNumber(blockNumber - 2).TestObject;
            Block ommer2 = Build.A.Block.WithNumber(blockNumber - 2).TestObject;
            Block block = Build.A.Block.WithNumber(blockNumber).WithOmmers(ommer, ommer2).TestObject;
            
            RewardCalculator calculator = new RewardCalculator(RopstenSpecProvider.Instance);
            BlockReward[] rewards = calculator.CalculateRewards(block);
            
            Assert.AreEqual(3, rewards.Length);
            Assert.AreEqual(3187500000000000000, (long)rewards[0].Value, "miner");
            Assert.AreEqual(2250000000000000000, (long)rewards[1].Value, "uncle1");
            Assert.AreEqual(2250000000000000000, (long)rewards[2].Value, "uncle2");
        }
        
        [Test]
        public void Constantinople_reward_two_uncles()
        {
            long blockNumber = RopstenSpecProvider.ConstantinopleBlockNumber;
            Block ommer = Build.A.Block.WithNumber(blockNumber - 2).TestObject;
            Block ommer2 = Build.A.Block.WithNumber(blockNumber - 2).TestObject;
            Block block = Build.A.Block.WithNumber(blockNumber).WithOmmers(ommer, ommer2).TestObject;
            
            RewardCalculator calculator = new RewardCalculator(RopstenSpecProvider.Instance);
            BlockReward[] rewards = calculator.CalculateRewards(block);
            
            Assert.AreEqual(3, rewards.Length);
            Assert.AreEqual(2125000000000000000, (long)rewards[0].Value, "miner");
            Assert.AreEqual(1500000000000000000, (long)rewards[1].Value, "uncle1");
            Assert.AreEqual(1500000000000000000, (long)rewards[2].Value, "uncle2");
        }
    }
}
