// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Mining.Test
{
    [TestFixture]
    public class GasLimitCalculatorTests
    {
        [TestCase(1000000, 2000000, 1000975)]
        [TestCase(1999999, 2000000, 2000000)]
        [TestCase(2000000, 2000000, 2000000)]
        [TestCase(2000001, 2000000, 2000000)]
        [TestCase(3000000, 2000000, 2997072)]
        public void Test(long current, long target, long expected)
        {
            BlocksConfig blocksConfig = new();
            blocksConfig.TargetBlockGasLimit = target;

            TargetAdjustedGasLimitCalculator targetAdjustedGasLimitCalculator = new(
                MainnetSpecProvider.Instance, blocksConfig);

            BlockHeader header = Build.A.BlockHeader.WithGasLimit(current).TestObject;
            targetAdjustedGasLimitCalculator.GetGasLimit(header).Should().Be(expected);
        }
    }
}
