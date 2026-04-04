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

        [TestCase(20_000_000, 60_000_000, 20_019_530)]
        [TestCase(50_000_000, 60_000_000, 49_951_173)]
        [TestCase(30_000_000, 60_000_000, 30_000_000)]
        public void Is_target_halved_when_eip7782_enabled_steady_state(long currentGasLimit, long targetGasLimit, long expectedGasLimit)
        {
            OverridableReleaseSpec postForkSpec = new(Prague.Instance) { IsEip7782Enabled = true };
            TestSpecProvider specProvider = new(postForkSpec);

            TargetAdjustedGasLimitCalculator calculator = new(specProvider, new BlocksConfig { TargetBlockGasLimit = targetGasLimit });
            BlockHeader parent = Build.A.BlockHeader.WithNumber(10).WithGasLimit(currentGasLimit).TestObject;

            long gasLimit = calculator.GetGasLimit(parent);

            gasLimit.Should().Be(expectedGasLimit);
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
