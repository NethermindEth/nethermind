//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Collections;
using System.Collections.Generic;
using Nethermind.Consensus.AuRa;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test
{
    public class AuraDifficultyCalculatorTests
    {
        private static IEnumerable DifficultyTestCases
        {
            get
            {
                yield return new TestCaseData(1, 0, 0).Returns(new UInt256(UInt128.MaxValue - 1));
                yield return new TestCaseData(10, 0, 0).Returns(new UInt256(UInt128.MaxValue - 10));
                yield return new TestCaseData(10, 9, 0).Returns(new UInt256(UInt128.MaxValue - 1));
                yield return new TestCaseData(100, 10, 0).Returns(new UInt256(UInt128.MaxValue - 90));
                
                yield return new TestCaseData(1, 0, 1).Returns(new UInt256(UInt128.MaxValue));
                yield return new TestCaseData(10, 0, 5).Returns(new UInt256(UInt128.MaxValue - 5));
                yield return new TestCaseData(10, 9, 3).Returns(new UInt256(UInt128.MaxValue) + 2);
                yield return new TestCaseData(100, 10, 10).Returns(new UInt256(UInt128.MaxValue - 80));
            }
        }
        
        [TestCaseSource(nameof(DifficultyTestCases))]
        public UInt256 calculates_difficulty(long step, long parentStep, long emptyStepCount) => 
            AuraDifficultyCalculator.CalculateDifficulty(parentStep, step, emptyStepCount);
    }
}