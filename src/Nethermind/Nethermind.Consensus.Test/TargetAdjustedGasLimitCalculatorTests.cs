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
        private const long DefaultBlockNumber = 20_000_000;

        private static long Calc(
            IReleaseSpec spec,
            long parentGasLimit,
            long? configTarget = null,
            long? overrideTarget = null,
            long blockNumber = DefaultBlockNumber)
        {
            TestSpecProvider specProvider = new(spec);
            TargetAdjustedGasLimitCalculator calculator = new(
                specProvider,
                new BlocksConfig { TargetBlockGasLimit = configTarget });
            BlockHeader header = Build.A.BlockHeader.WithNumber(blockNumber - 1).WithGasLimit(parentGasLimit).TestObject;
            return calculator.GetGasLimit(header, overrideTarget);
        }

        [Test]
        public void Is_bump_on_1559_eip_block()
        {
            const int londonBlock = 5;
            const long parentGasLimit = 1000000000000000000;
            OverridableReleaseSpec spec = new(London.Instance) { Eip1559TransitionBlock = londonBlock };

            Calc(spec, parentGasLimit, blockNumber: londonBlock)
                .Should().Be(parentGasLimit * Eip1559Constants.DefaultElasticityMultiplier);
        }

        [TestCase(30_000_000, 100_000_000, 30029295)]
        public void Is_calculating_correct_gasLimit(long parentGasLimit, long configTarget, long expected)
            => Calc(Prague.Instance, parentGasLimit, configTarget: configTarget).Should().Be(expected);

        // Config target would decrease, override pushes up — confirms override drives the direction.
        [TestCase(30_000_000, 20_000_000, 50_000_000, 30029295)]
        // Config target would increase, override pulls down — confirms override drives the direction.
        [TestCase(30_000_000, 100_000_000, 20_000_000, 29970705)]
        public void Override_takes_precedence_over_config_target(long parentGasLimit, long configTarget, long overrideTarget, long expected)
            => Calc(Prague.Instance, parentGasLimit, configTarget, overrideTarget).Should().Be(expected);

        [Test]
        public void Null_override_falls_back_to_config_target()
        {
            const long parentGasLimit = 30_000_000;
            const long configTarget = 100_000_000;

            Calc(Prague.Instance, parentGasLimit, configTarget, overrideTarget: null)
                .Should().Be(Calc(Prague.Instance, parentGasLimit, configTarget));
        }

        [Test]
        public void DoesNot_go_below_minimum()
        {
            const int londonBlock = 5;

            Calc(London.Instance, parentGasLimit: 5000, configTarget: 1, blockNumber: londonBlock)
                .Should().Be(London.Instance.MinGasLimit);
        }
    }
}
