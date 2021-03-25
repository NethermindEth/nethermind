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

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Blockchain.Rewards;
using Nethermind.Consensus.AuRa.Rewards;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Reward
{
    public class StaticRewardCalculatorTests
    {
        private readonly Block _block = new(Build.A.BlockHeader.TestObject, new BlockBody());

        [TestCase(4, 200ul)]
        [TestCase(5, 150ul)]
        [TestCase(9, 150ul)]
        [TestCase(10, 100ul)]
        [TestCase(999999999, 50ul)]
        public void calculates_rewards_correctly_for_thresholds(long blockNumber, ulong expectedReward)
        {
            var blockReward = new Dictionary<long, UInt256>() {{0, 200}, {5, 150}, {10, 100}, {11, 50}};
            _block.Header.Number = blockNumber;
            var calculator = new StaticRewardCalculator(blockReward);
            var result =  calculator.CalculateRewards(_block);
            result.Should().BeEquivalentTo(new BlockReward(_block.Beneficiary, expectedReward));
        }
        
        [TestCase(0, 200ul)]
        [TestCase(999999999, 200ul)]
        public void calculates_rewards_correctly_for_single_value(long blockNumber, ulong expectedReward)
        {
            var blockReward = new Dictionary<long, UInt256>() {{0, 200}};
            _block.Header.Number = blockNumber;
            var calculator = new StaticRewardCalculator(blockReward);
            var result =  calculator.CalculateRewards(_block);
            result.Should().BeEquivalentTo(new BlockReward(_block.Beneficiary, expectedReward));
        }
        
        [TestCase(0, 0ul)]
        [TestCase(999999999, 0ul)]
        public void calculates_rewards_correctly_for_null_argument(long blockNumber, ulong expectedReward)
        {
            _block.Header.Number = blockNumber;
            var calculator = new StaticRewardCalculator(null);
            var result =  calculator.CalculateRewards(_block);
            result.Should().BeEquivalentTo(new BlockReward(_block.Beneficiary, expectedReward));
        }
        
        [TestCase(9, 0ul)]
        [TestCase(10, 200ul)]
        public void calculates_rewards_correctly_for_not_supported_value(long blockNumber, ulong expectedReward)
        {
            var blockReward = new Dictionary<long, UInt256>() {{10, 200}};
            _block.Header.Number = blockNumber;
            var calculator = new StaticRewardCalculator(blockReward);
            var result =  calculator.CalculateRewards(_block);
            result.Should().BeEquivalentTo(new BlockReward(_block.Beneficiary, expectedReward));
        }
        
        [TestCase(1, 0ul)]
        public void calculates_rewards_correctly_for_empty_dictionary(long blockNumber, ulong expectedReward)
        {
            var blockReward = new Dictionary<long, UInt256>() {};
            _block.Header.Number = blockNumber;
            var calculator = new StaticRewardCalculator(blockReward);
            var result =  calculator.CalculateRewards(_block);
            result.Should().BeEquivalentTo(new BlockReward(_block.Beneficiary, expectedReward));
        }
    }
}
