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
                new BlocksConfig { TargetBlockGasLimit = (ulong?)configTarget });
            BlockHeader header = Build.A.BlockHeader.WithNumber((ulong)(blockNumber - 1)).WithGasLimit((ulong)parentGasLimit).TestObject;
            return (long)calculator.GetGasLimit(header, (ulong?)overrideTarget);
        }

        [Test]
        public void Is_bump_on_1559_eip_block()
        {
            const int londonBlock = 5;
            const long parentGasLimit = 1000000000000000000;
            OverridableReleaseSpec spec = new(London.Instance) { Eip1559TransitionBlock = londonBlock };

            Assert.That(
                Calc(spec, parentGasLimit, blockNumber: londonBlock),
                Is.EqualTo(parentGasLimit * Eip1559Constants.DefaultElasticityMultiplier));
        }

        [TestCase(30_000_000, 100_000_000, 30029295)]
        public void Is_calculating_correct_gasLimit(long parentGasLimit, long configTarget, long expected)
            => Assert.That(Calc(Prague.Instance, parentGasLimit, configTarget: configTarget), Is.EqualTo(expected));

        [TestCase(30_000_000, 20_000_000, 50_000_000, 30029295)]
        [TestCase(30_000_000, 100_000_000, 20_000_000, 29970705)]
        public void Override_takes_precedence_over_config_target(long parentGasLimit, long configTarget, long overrideTarget, long expected)
            => Assert.That(Calc(Prague.Instance, parentGasLimit, configTarget, overrideTarget), Is.EqualTo(expected));

        [Test]
        public void Null_override_falls_back_to_config_target()
        {
            const long parentGasLimit = 30_000_000;
            const long configTarget = 100_000_000;

            Assert.That(
                Calc(Prague.Instance, parentGasLimit, configTarget, overrideTarget: null),
                Is.EqualTo(Calc(Prague.Instance, parentGasLimit, configTarget)));
        }

        [TestCase(0UL)]
        [TestCase(1UL)]
        public void DoesNot_go_below_minimum(ulong targetGasLimit)
        {
            const int londonBlock = 5;

            Assert.That(
                Calc(London.Instance, parentGasLimit: 5000, configTarget: (long)targetGasLimit, blockNumber: londonBlock),
                Is.EqualTo(London.Instance.MinGasLimit));
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
