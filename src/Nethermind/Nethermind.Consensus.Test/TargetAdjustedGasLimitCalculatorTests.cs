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


using Nethermind.Core;
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
            TargetAdjustedGasLimitCalculator targetedAdjustedGasLimitCalculator = new(specProvider, new MiningConfig());
            BlockHeader header = Build.A.BlockHeader.WithNumber(londonBlock - 1).WithGasLimit(gasLimit).TestObject;
            long actualValue = targetedAdjustedGasLimitCalculator.GetGasLimit(header);
            Assert.AreEqual(gasLimit * Eip1559Constants.ElasticityMultiplier, actualValue);
        }
    }
}
