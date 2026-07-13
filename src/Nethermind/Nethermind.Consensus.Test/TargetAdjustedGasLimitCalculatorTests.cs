// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Consensus.Test
{
    public class TargetAdjustedGasLimitCalculatorTests
    {
        [TestCase(30_000_000UL, 20_000_000UL, 50_000_000UL, 30_029_295UL)]
        [TestCase(30_000_000UL, 100_000_000UL, 20_000_000UL, 29_970_705UL)]
        public void Explicit_target_overrides_config_and_remains_parent_bounded(
            ulong parentGasLimit,
            ulong configTarget,
            ulong explicitTarget,
            ulong expectedGasLimit)
        {
            TestSpecProvider specProvider = new(Prague.Instance);
            TargetAdjustedGasLimitCalculator calculator = new(
                specProvider,
                new BlocksConfig { TargetBlockGasLimit = configTarget });
            BlockHeader parent = Build.A.BlockHeader
                .WithNumber(19_999_999)
                .WithGasLimit(parentGasLimit)
                .TestObject;

            Assert.That(calculator.GetGasLimit(parent, explicitTarget), Is.EqualTo(expectedGasLimit));
        }

        [Test]
        public void Is_bump_on_1559_eip_block()
        {
            ulong londonBlock = 5;
            ulong gasLimit = 1000000000000000000;
            OverridableReleaseSpec spec = new(London.Instance)
            {
                Eip1559TransitionBlock = londonBlock
            };
            TestSpecProvider specProvider = new(spec);
            TargetAdjustedGasLimitCalculator targetedAdjustedGasLimitCalculator = new(specProvider, new BlocksConfig());
            BlockHeader header = Build.A.BlockHeader.WithNumber(londonBlock - 1).WithGasLimit(gasLimit).TestObject;
            ulong actualValue = targetedAdjustedGasLimitCalculator.GetGasLimit(header);
            Assert.That(actualValue, Is.EqualTo(gasLimit * Eip1559Constants.DefaultElasticityMultiplier));
        }

        [TestCase(30_000_000ul, 100_000_000UL, 30029295UL)]
        public void Is_calculating_correct_gasLimit(ulong currentGasLimit, ulong targetGasLimit, ulong expectedGasLimit)
        {
            ulong blockNumber = 20_000_000;
            ulong gasLimit = currentGasLimit;
            TestSpecProvider specProvider = new(Prague.Instance);
            TargetAdjustedGasLimitCalculator targetedAdjustedGasLimitCalculator = new(specProvider,
                new BlocksConfig()
                {
                    TargetBlockGasLimit = targetGasLimit
                });
            BlockHeader header = Build.A.BlockHeader.WithNumber(blockNumber - 1UL).WithGasLimit(gasLimit).TestObject;
            ulong actualValue = targetedAdjustedGasLimitCalculator.GetGasLimit(header);
            Assert.That(actualValue, Is.EqualTo(expectedGasLimit));
        }

        [TestCase(0UL)]
        [TestCase(1UL)]
        public void DoesNot_go_below_minimum(ulong targetGasLimit)
        {
            ulong londonBlock = 5;
            ulong gasLimit = 5000;
            TestSpecProvider specProvider = new(London.Instance);
            TargetAdjustedGasLimitCalculator targetedAdjustedGasLimitCalculator = new(specProvider, new BlocksConfig() { TargetBlockGasLimit = targetGasLimit });
            BlockHeader header = Build.A.BlockHeader.WithNumber(londonBlock - 1UL).WithGasLimit(gasLimit).TestObject;
            ulong actualValue = targetedAdjustedGasLimitCalculator.GetGasLimit(header);
            Assert.That(actualValue, Is.EqualTo(specProvider.GetSpec(new ForkActivation(5)).MinGasLimit));
        }

        [Test]
        public void Target_aware_interface_overload_preserves_legacy_implementations()
        {
            IGasLimitCalculator calculator = new LegacyGasLimitCalculator();
            BlockHeader parent = Build.A.BlockHeader.TestObject;

            Assert.That(calculator.GetGasLimit(parent, 1), Is.EqualTo(42));
        }

        private sealed class LegacyGasLimitCalculator : IGasLimitCalculator
        {
            public ulong GetGasLimit(BlockHeader parentHeader) => 42;
        }
    }
}
