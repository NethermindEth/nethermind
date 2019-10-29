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

using NUnit.Framework;

namespace Nethermind.Ssz.Test
{
    [TestFixture]
    public class MerkleTests
    {
        [TestCase(uint.MinValue, 1U)]
        [TestCase(1U, 1U)]
        [TestCase(2U, 2U)]
        [TestCase(3U, 4U)]
        [TestCase(uint.MaxValue / 2, 2147483648U)]
        [TestCase(uint.MaxValue / 2 + 1, 2147483648U)]
        public void Can_get_the_next_power_of_two_32(uint value, uint expectedResult)
        {
            Assert.AreEqual(expectedResult, Merkle.NextPowerOfTwo(value));
        }

        [TestCase(ulong.MinValue, 1UL)]
        [TestCase(1UL, 1UL)]
        [TestCase(2UL, 2UL)]
        [TestCase(3UL, 4UL)]
        [TestCase(ulong.MaxValue / 2, 9223372036854775808UL)]
        [TestCase(ulong.MaxValue / 2 + 1, 9223372036854775808UL)]
        public void Can_get_the_next_power_of_two_64(ulong value, ulong expectedResult)
        {
            Assert.AreEqual(expectedResult, Merkle.NextPowerOfTwo(value));
        }
    }
}