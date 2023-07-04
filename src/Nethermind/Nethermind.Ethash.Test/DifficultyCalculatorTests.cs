// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Ethash;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;
using NSubstitute;
using Nethermind.Core;

namespace Nethermind.Ethash.Test
{
    public class DifficultyCalculatorTests
    {
        [Test]
        public void Calculate_should_returns_expected_results()
        {
            IReleaseSpec releaseSpec = Substitute.For<IReleaseSpec>();
            releaseSpec.DifficultyBombDelay.Returns(0);
            releaseSpec.DifficultyBoundDivisor.Returns(2048);
            releaseSpec.IsEip2Enabled.Returns(true);
            releaseSpec.IsEip100Enabled.Returns(true);
            releaseSpec.IsTimeAdjustmentPostOlympic.Returns(true);
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            specProvider.GetSpec(Arg.Any<long>(), Arg.Any<ulong>()).Returns(releaseSpec);
            specProvider.GetSpec(Arg.Any<BlockHeader>()).Returns(releaseSpec);
            specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);
            EthashDifficultyCalculator difficultyCalculator = new(specProvider);
            UInt256 result = difficultyCalculator.Calculate(0x55f78f7, 1613570258, 0x602d20d2, 200000, false);
            Assert.That(result, Is.EqualTo((UInt256)90186983));
        }


        [Test]
        public void CalculateOlympic_should_returns_expected_results()
        {
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            specProvider.GetSpec(Arg.Any<long>(), Arg.Any<ulong>()).Returns(Olympic.Instance);
            specProvider.GetSpec(Arg.Any<BlockHeader>()).Returns(Olympic.Instance);
            specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(Olympic.Instance);
            EthashDifficultyCalculator difficultyCalculator = new(specProvider);
            UInt256 result = difficultyCalculator.Calculate(0x55f78f7, 1613570258, 0x602d20d2, 200000, false);
            Assert.That(result, Is.EqualTo((UInt256)90186983));
        }

        [Test]
        public void CalculateBerlin_should_returns_expected_results()
        {
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            specProvider.GetSpec(Arg.Any<long>(), Arg.Any<ulong>()).Returns(Berlin.Instance);
            specProvider.GetSpec(Arg.Any<BlockHeader>()).Returns(Berlin.Instance);
            specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(Berlin.Instance);
            EthashDifficultyCalculator difficultyCalculator = new(specProvider);
            UInt256 result = difficultyCalculator.Calculate(0x55f78f7, 1613570258, 0x602d20d2, 200000, false);
            Assert.That(result, Is.EqualTo((UInt256)90186982));
        }

        // previous difficulty bomb +  InitialDifficultyBombBlock + offset
        [TestCase(9000000 + EthashDifficultyCalculator.InitialDifficultyBombBlock + 1)]
        [TestCase(9000000 + EthashDifficultyCalculator.InitialDifficultyBombBlock + 3)]
        [TestCase(9000000 + EthashDifficultyCalculator.InitialDifficultyBombBlock + 730000)]
        public void London_calculation_should_not_be_equal_to_Berlin(long blocksAbove)
        {
            Calculation_should_not_be_equal_on_different_difficulty_hard_forks(blocksAbove,
                Berlin.Instance, London.Instance);
        }

        // previous difficulty bomb +  InitialDifficultyBombBlock + offset
        [TestCase(9700000 + EthashDifficultyCalculator.InitialDifficultyBombBlock + 1)]
        [TestCase(9700000 + EthashDifficultyCalculator.InitialDifficultyBombBlock + 3)]
        [TestCase(9700000 + EthashDifficultyCalculator.InitialDifficultyBombBlock + 730000)]
        public void ArrowGlacier_calculation_should_not_be_equal_to_London0(long blocksAbove)
        {
            Calculation_should_not_be_equal_on_different_difficulty_hard_forks(blocksAbove,
                London.Instance, ArrowGlacier.Instance);
        }

        // previous difficulty bomb +  InitialDifficultyBombBlock + offset
        [TestCase(10700000 + EthashDifficultyCalculator.InitialDifficultyBombBlock + 1)]
        [TestCase(10700000 + EthashDifficultyCalculator.InitialDifficultyBombBlock + 3)]
        [TestCase(10700000 + EthashDifficultyCalculator.InitialDifficultyBombBlock + 730000)]
        public void GrayGlacier_calculation_should_not_be_equal_to_ArrowGlacier(long blocksAbove)
        {
            Calculation_should_not_be_equal_on_different_difficulty_hard_forks(blocksAbove,
                ArrowGlacier.Instance, GrayGlacier.Instance);
        }

        private void Calculation_should_not_be_equal_on_different_difficulty_hard_forks(
            long blocksAbove, IReleaseSpec firstHardfork, IReleaseSpec secondHardfork)
        {
            UInt256 parentDifficulty = 0x55f78f7;
            ulong parentTimestamp = 1613570258;
            ulong currentTimestamp = 0x602d20d2;
            ISpecProvider firstHardForkSpecProvider = Substitute.For<ISpecProvider>();
            firstHardForkSpecProvider.GetSpec(Arg.Any<long>(), Arg.Any<ulong>()).Returns(firstHardfork);
            firstHardForkSpecProvider.GetSpec(Arg.Any<BlockHeader>()).Returns(firstHardfork);
            firstHardForkSpecProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(firstHardfork);
            EthashDifficultyCalculator firstHardforkDifficultyCalculator = new(firstHardForkSpecProvider);
            UInt256 firstHardforkResult = firstHardforkDifficultyCalculator.Calculate(parentDifficulty, parentTimestamp, currentTimestamp, blocksAbove, false);

            ISpecProvider secondHardforkSpecProvider = Substitute.For<ISpecProvider>();
            secondHardforkSpecProvider.GetSpec(Arg.Any<long>(), Arg.Any<ulong>()).Returns(secondHardfork);
            secondHardforkSpecProvider.GetSpec(Arg.Any<BlockHeader>()).Returns(secondHardfork);
            secondHardforkSpecProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(secondHardfork);
            EthashDifficultyCalculator secondHardforkDifficultyCalculator = new(secondHardforkSpecProvider);
            UInt256 secondHardforkResult = secondHardforkDifficultyCalculator.Calculate(parentDifficulty, parentTimestamp, currentTimestamp, blocksAbove, false);

            Assert.That(secondHardforkResult, Is.Not.EqualTo(firstHardforkResult));
        }
    }
}
