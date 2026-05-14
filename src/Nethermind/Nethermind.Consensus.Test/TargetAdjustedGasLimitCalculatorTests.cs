// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using FluentAssertions;
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
        [Test]
        public void Is_bump_on_1559_eip_block()
        {
            int londonBlock = 5;
            long gasLimit = 1000000000000000000;
            OverridableReleaseSpec spec = new(London.Instance)
            {
                Eip1559TransitionBlock = londonBlock
            };
            TestSpecProvider specProvider = new(spec);
            TargetAdjustedGasLimitCalculator targetedAdjustedGasLimitCalculator = new(specProvider, new BlocksConfig());
            BlockHeader header = Build.A.BlockHeader.WithNumber(londonBlock - 1).WithGasLimit(gasLimit).TestObject;
            long actualValue = targetedAdjustedGasLimitCalculator.GetGasLimit(header);
            Assert.That(actualValue, Is.EqualTo(gasLimit * Eip1559Constants.DefaultElasticityMultiplier));
        }

        [TestCase(30_000_000, 100_000_000, 30029295)]
        public void Is_calculating_correct_gasLimit(long currentGasLimit, long targetGasLimit, long expectedGasLimit)
        {
            int blockNumber = 20_000_000;
            long gasLimit = currentGasLimit;
            TestSpecProvider specProvider = new(Prague.Instance);
            TargetAdjustedGasLimitCalculator targetedAdjustedGasLimitCalculator = new(specProvider,
                new BlocksConfig()
                {
                    TargetBlockGasLimit = targetGasLimit
                });
            BlockHeader header = Build.A.BlockHeader.WithNumber(blockNumber - 1).WithGasLimit(gasLimit).TestObject;
            long actualValue = targetedAdjustedGasLimitCalculator.GetGasLimit(header);
            actualValue.Should().Be(expectedGasLimit);
        }

        // Config target would decrease, override pushes up — confirms override drives the direction.
        [TestCase(30_000_000, 20_000_000, 50_000_000, 30029295)]
        // Config target would increase, override pulls down — confirms override drives the direction.
        [TestCase(30_000_000, 100_000_000, 20_000_000, 29970705)]
        public void Override_takes_precedence_over_config_target(long currentGasLimit, long configTargetGasLimit, long overrideTargetGasLimit, long expectedGasLimit)
        {
            int blockNumber = 20_000_000;
            TestSpecProvider specProvider = new(Prague.Instance);
            TargetAdjustedGasLimitCalculator calculator = new(specProvider,
                new BlocksConfig()
                {
                    TargetBlockGasLimit = configTargetGasLimit
                });
            BlockHeader header = Build.A.BlockHeader.WithNumber(blockNumber - 1).WithGasLimit(currentGasLimit).TestObject;

            long actualValue = calculator.GetGasLimit(header, overrideTargetGasLimit);

            actualValue.Should().Be(expectedGasLimit);
        }

        [Test]
        public void Null_override_falls_back_to_config_target()
        {
            const long currentGasLimit = 30_000_000;
            const long configTargetGasLimit = 100_000_000;
            TestSpecProvider specProvider = new(Prague.Instance);
            TargetAdjustedGasLimitCalculator calculator = new(specProvider,
                new BlocksConfig()
                {
                    TargetBlockGasLimit = configTargetGasLimit
                });
            BlockHeader header = Build.A.BlockHeader.WithNumber(20_000_000 - 1).WithGasLimit(currentGasLimit).TestObject;

            long withoutOverride = calculator.GetGasLimit(header);
            long withNullOverride = calculator.GetGasLimit(header, targetGasLimitOverride: null);

            withNullOverride.Should().Be(withoutOverride);
        }

        [Test]
        public void DoesNot_go_below_minimum()
        {
            int londonBlock = 5;
            long gasLimit = 5000;
            TestSpecProvider specProvider = new(London.Instance);
            TargetAdjustedGasLimitCalculator targetedAdjustedGasLimitCalculator = new(specProvider, new BlocksConfig() { TargetBlockGasLimit = 1 });
            BlockHeader header = Build.A.BlockHeader.WithNumber(londonBlock - 1).WithGasLimit(gasLimit).TestObject;
            long actualValue = targetedAdjustedGasLimitCalculator.GetGasLimit(header);
            Assert.That(actualValue, Is.EqualTo(specProvider.GetSpec(new ForkActivation(5)).MinGasLimit));
        }
    }
}
