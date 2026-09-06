// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        [TestCase(1000000UL, 2000000UL, 1000975UL)]
        [TestCase(1999999UL, 2000000UL, 2000000UL)]
        [TestCase(2000000UL, 2000000UL, 2000000UL)]
        [TestCase(2000001UL, 2000000UL, 2000000UL)]
        [TestCase(3000000UL, 2000000UL, 2997072UL)]
        public void Test(ulong current, ulong target, ulong expected)
        {
            BlocksConfig blocksConfig = new();
            blocksConfig.TargetBlockGasLimit = target;

            TargetAdjustedGasLimitCalculator targetAdjustedGasLimitCalculator = new(
                MainnetSpecProvider.Instance, blocksConfig);

            BlockHeader header = Build.A.BlockHeader.WithGasLimit(current).TestObject;
            Assert.That(targetAdjustedGasLimitCalculator.GetGasLimit(header), Is.EqualTo(expected));
        }
    }
}
