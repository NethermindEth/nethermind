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
// 

using FluentAssertions;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Mining.Test
{
    [TestFixture]
    public class FollowOtherMinersTests
    {
        [TestCase(1000000, 1000000)]
        [TestCase(1999999, 1999999)]
        [TestCase(2000000, 2000000)]
        [TestCase(2000001, 2000001)]
        [TestCase(3000000, 3000000)]
        public void Test(long current, long expected)
        {
            BlockHeader header = Build.A.BlockHeader.WithGasLimit(current).TestObject;
            FollowOtherMiners followOtherMiners = new FollowOtherMiners(MainnetSpecProvider.Instance);
            followOtherMiners.GetGasLimit(header).Should().Be(expected);
        }
        
        [TestCase(1000000, 2000000)]
        [TestCase(2000000, 4000000)]
        [TestCase(2000001, 4000002)]
        [TestCase(3000000, 6000000)]
        public void FollowOtherMines_on_1559_fork_block(long current, long expected)
        {
            int forkNumber = 5;
            OverridableReleaseSpec spec = new OverridableReleaseSpec(London.Instance)
            {
                Eip1559TransitionBlock = forkNumber
            };
            TestSpecProvider specProvider = new TestSpecProvider(spec);
            BlockHeader header = Build.A.BlockHeader.WithGasLimit(current).WithNumber(forkNumber - 1).TestObject;
            FollowOtherMiners followOtherMiners = new FollowOtherMiners(specProvider);
            followOtherMiners.GetGasLimit(header).Should().Be(expected);
        }
    }
}
