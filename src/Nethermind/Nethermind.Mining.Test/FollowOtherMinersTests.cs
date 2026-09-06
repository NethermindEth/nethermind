// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        [TestCase(1000000UL, 1000000UL)]
        [TestCase(1999999UL, 1999999UL)]
        [TestCase(2000000UL, 2000000UL)]
        [TestCase(2000001UL, 2000001UL)]
        [TestCase(3000000UL, 3000000UL)]
        public void Test(ulong current, ulong expected)
        {
            BlockHeader header = Build.A.BlockHeader.WithGasLimit(current).TestObject;
            FollowOtherMiners followOtherMiners = new(MainnetSpecProvider.Instance);
            Assert.That(followOtherMiners.GetGasLimit(header), Is.EqualTo(expected));
        }

        [TestCase(1000000UL, 2000000UL)]
        [TestCase(2000000UL, 4000000UL)]
        [TestCase(2000001UL, 4000002UL)]
        [TestCase(3000000UL, 6000000UL)]
        public void FollowOtherMines_on_1559_fork_block(ulong current, ulong expected)
        {
            ulong forkNumber = 5;
            OverridableReleaseSpec spec = new(London.Instance)
            {
                Eip1559TransitionBlock = forkNumber
            };
            TestSpecProvider specProvider = new(spec);
            BlockHeader header = Build.A.BlockHeader.WithGasLimit(current).WithNumber(forkNumber - 1).TestObject;
            FollowOtherMiners followOtherMiners = new(specProvider);
            Assert.That(followOtherMiners.GetGasLimit(header), Is.EqualTo(expected));
        }
    }
}
