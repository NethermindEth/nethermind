// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.AuRa.Rewards;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Reward
{
    public class StaticRewardCalculatorTests
    {
        private readonly Block _block = new(Build.A.BlockHeader.TestObject, new BlockBody());

        [TestCase(4ul, 200ul)]
        [TestCase(5ul, 150ul)]
        [TestCase(9ul, 150ul)]
        [TestCase(10ul, 100ul)]
        [TestCase(999999999ul, 50ul)]
        public void calculates_rewards_correctly_for_thresholds(ulong blockNumber, ulong expectedReward)
        {
            Dictionary<ulong, UInt256> blockReward = new() { { 0, 200 }, { 5, 150 }, { 10, 100 }, { 11, 50 } };
            _block.Header.Number = blockNumber;
            StaticRewardCalculator calculator = new(blockReward);
            BlockReward[] result = calculator.CalculateRewards(_block);
            Assert.That(result, Is.EquivalentTo(new[] { new BlockReward(_block.Beneficiary, expectedReward) }).UsingPropertiesComparer());
        }

        [TestCase(0ul, 200ul)]
        [TestCase(999999999ul, 200ul)]
        public void calculates_rewards_correctly_for_single_value(ulong blockNumber, ulong expectedReward)
        {
            Dictionary<ulong, UInt256> blockReward = new() { { 0, 200 } };
            _block.Header.Number = blockNumber;
            StaticRewardCalculator calculator = new(blockReward);
            BlockReward[] result = calculator.CalculateRewards(_block);
            Assert.That(result, Is.EquivalentTo(new[] { new BlockReward(_block.Beneficiary, expectedReward) }).UsingPropertiesComparer());
        }

        [TestCase(0ul, 0ul)]
        [TestCase(999999999ul, 0ul)]
        public void calculates_rewards_correctly_for_null_argument(ulong blockNumber, ulong expectedReward)
        {
            _block.Header.Number = blockNumber;
            StaticRewardCalculator calculator = new(null);
            BlockReward[] result = calculator.CalculateRewards(_block);
            Assert.That(result, Is.EquivalentTo(new[] { new BlockReward(_block.Beneficiary, expectedReward) }).UsingPropertiesComparer());
        }

        [TestCase(9ul, 0ul)]
        [TestCase(10ul, 200ul)]
        public void calculates_rewards_correctly_for_not_supported_value(ulong blockNumber, ulong expectedReward)
        {
            Dictionary<ulong, UInt256> blockReward = new() { { 10, 200 } };
            _block.Header.Number = blockNumber;
            StaticRewardCalculator calculator = new(blockReward);
            BlockReward[] result = calculator.CalculateRewards(_block);
            Assert.That(result, Is.EquivalentTo(new[] { new BlockReward(_block.Beneficiary, expectedReward) }).UsingPropertiesComparer());
        }

        [TestCase(1ul, 0ul)]
        public void calculates_rewards_correctly_for_empty_dictionary(ulong blockNumber, ulong expectedReward)
        {
            Dictionary<ulong, UInt256> blockReward = [];
            _block.Header.Number = blockNumber;
            StaticRewardCalculator calculator = new(blockReward);
            BlockReward[] result = calculator.CalculateRewards(_block);
            Assert.That(result, Is.EquivalentTo(new[] { new BlockReward(_block.Beneficiary, expectedReward) }).UsingPropertiesComparer());
        }
    }
}
