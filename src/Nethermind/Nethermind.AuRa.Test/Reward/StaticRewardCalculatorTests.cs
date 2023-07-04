// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Consensus.AuRa.Rewards;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Test;
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
            Dictionary<long, UInt256> blockReward = new() { { 0, 200 }, { 5, 150 }, { 10, 100 }, { 11, 50 } };
            _block.Header.Number = blockNumber;
            StaticRewardCalculator calculator = new(blockReward);
            BlockReward[] result = calculator.CalculateRewards(_block);
            result.Should().BeEquivalentTo(new BlockReward(_block.Beneficiary, expectedReward));
        }

        [TestCase(0, 200ul)]
        [TestCase(999999999, 200ul)]
        public void calculates_rewards_correctly_for_single_value(long blockNumber, ulong expectedReward)
        {
            Dictionary<long, UInt256> blockReward = new() { { 0, 200 } };
            _block.Header.Number = blockNumber;
            StaticRewardCalculator calculator = new(blockReward);
            BlockReward[] result = calculator.CalculateRewards(_block);
            result.Should().BeEquivalentTo(new BlockReward(_block.Beneficiary, expectedReward));
        }

        [TestCase(0, 0ul)]
        [TestCase(999999999, 0ul)]
        public void calculates_rewards_correctly_for_null_argument(long blockNumber, ulong expectedReward)
        {
            _block.Header.Number = blockNumber;
            StaticRewardCalculator calculator = new(null);
            BlockReward[] result = calculator.CalculateRewards(_block);
            result.Should().BeEquivalentTo(new BlockReward(_block.Beneficiary, expectedReward));
        }

        [TestCase(9, 0ul)]
        [TestCase(10, 200ul)]
        public void calculates_rewards_correctly_for_not_supported_value(long blockNumber, ulong expectedReward)
        {
            Dictionary<long, UInt256> blockReward = new() { { 10, 200 } };
            _block.Header.Number = blockNumber;
            StaticRewardCalculator calculator = new(blockReward);
            BlockReward[] result = calculator.CalculateRewards(_block);
            result.Should().BeEquivalentTo(new BlockReward(_block.Beneficiary, expectedReward));
        }

        [TestCase(1, 0ul)]
        public void calculates_rewards_correctly_for_empty_dictionary(long blockNumber, ulong expectedReward)
        {
            Dictionary<long, UInt256> blockReward = new() { };
            _block.Header.Number = blockNumber;
            StaticRewardCalculator calculator = new(blockReward);
            BlockReward[] result = calculator.CalculateRewards(_block);
            result.Should().BeEquivalentTo(new BlockReward(_block.Beneficiary, expectedReward));
        }
    }
}
