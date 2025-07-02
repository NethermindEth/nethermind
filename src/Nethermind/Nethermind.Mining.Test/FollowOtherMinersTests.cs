// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Consensus;
using Nethermind.Core;
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
            FollowOtherMiners followOtherMiners = new(MainnetSpecProvider.Instance);
            followOtherMiners.GetGasLimit(header).Should().Be(expected);
        }

        [TestCase(1000000, 2000000)]
        [TestCase(2000000, 4000000)]
        [TestCase(2000001, 4000002)]
        [TestCase(3000000, 6000000)]
        public void FollowOtherMines_on_1559_fork_block(long current, long expected)
        {
            int forkNumber = 5;
            OverridableReleaseSpec spec = new(London.Instance)
            {
                Eip1559TransitionBlock = forkNumber
            };
            TestSpecProvider specProvider = new(spec);
            BlockHeader header = Build.A.BlockHeader.WithGasLimit(current).WithNumber(forkNumber - 1).TestObject;
            FollowOtherMiners followOtherMiners = new(specProvider);
            followOtherMiners.GetGasLimit(header).Should().Be(expected);
        }
    }
}
