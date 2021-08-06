//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using Nethermind.Consensus.Ethash;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using NUnit.Framework;
using NSubstitute;

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
            specProvider.GetSpec(Arg.Any<long>()).Returns(releaseSpec);
            EthashDifficultyCalculator difficultyCalculator = new(specProvider);
            UInt256 result = difficultyCalculator.Calculate(0x55f78f7, 1613570258, 0x602d20d2, 200000, false);
            Assert.AreEqual((UInt256)90186983, result);
        }
        
        
        [Test]
        public void CalculateOlympic_should_returns_expected_results()
        {
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            specProvider.GetSpec(Arg.Any<long>()).Returns(Olympic.Instance);
            EthashDifficultyCalculator difficultyCalculator = new(specProvider);
            UInt256 result = difficultyCalculator.Calculate(0x55f78f7, 1613570258, 0x602d20d2, 200000, false);
            Assert.AreEqual((UInt256)90186983, result);
        }
        
        [Test]
        public void CalculateBerlin_should_returns_expected_results()
        {
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            specProvider.GetSpec(Arg.Any<long>()).Returns(Berlin.Instance);
            EthashDifficultyCalculator difficultyCalculator = new(specProvider);
            UInt256 result = difficultyCalculator.Calculate(0x55f78f7, 1613570258, 0x602d20d2, 200000, false);
            Assert.AreEqual((UInt256)90186982, result);
        }
        
        [TestCase(3)]
        [TestCase(730000)]
        public void London_calculation_should_not_be_equal_to_berlin_above_block_9200000(long blocksAbove)
        {
            UInt256 parentDifficulty = 0x55f78f7;
            UInt256 parentTimestamp = 1613570258;
            UInt256 currentTimestamp = 0x602d20d2;
            ISpecProvider berlinSpecProvider = Substitute.For<ISpecProvider>();
            berlinSpecProvider.GetSpec(Arg.Any<long>()).Returns(Berlin.Instance);
            EthashDifficultyCalculator berlinDifficultyCalculator = new(berlinSpecProvider);
            UInt256 berlinResult = berlinDifficultyCalculator.Calculate(parentDifficulty, parentTimestamp, currentTimestamp, 9200000L + blocksAbove, false);
            
            ISpecProvider londonSpecProvider = Substitute.For<ISpecProvider>();
            londonSpecProvider.GetSpec(Arg.Any<long>()).Returns(London.Instance);
            EthashDifficultyCalculator londonDifficultyCalculator = new(londonSpecProvider);
            UInt256 londonResult = londonDifficultyCalculator.Calculate(parentDifficulty, parentTimestamp, currentTimestamp, 9200000L + blocksAbove, false);
            
            Assert.AreNotEqual(berlinResult, londonResult);
        }
    }
}
