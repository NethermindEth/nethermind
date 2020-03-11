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

using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Network.Test
{
    [TestFixture]
    public class ForkInfoTests
    {
        [TestCase(0, "0xfc64ec04", 1150000, "Unsynced")]
        [TestCase(1149999, "0xfc64ec04", 1150000, "Last Frontier block")]
        [TestCase(1150000, "0x97c2c34c", 1920000, "First Homestead block")]
        [TestCase(1919999, "0x97c2c34c", 1920000, "Last Homestead block")]
        [TestCase(1920000, "0x91d1f948", 2463000, "First DAO block")]
        [TestCase(2462999, "0x91d1f948", 2463000, "Last DAO block")]
        [TestCase(2463000, "0x7a64da13", 2675000, "First Tangerine block")]
        [TestCase(2674999, "0x7a64da13", 2675000, "Last Tangerine block")]
        [TestCase(2675000, "0x3edd5b10", 4370000, "First Spurious block")]
        [TestCase(4369999, "0x3edd5b10", 4370000, "Last Spurious block")]
        [TestCase(4370000, "0xa00bc324", 7280000, "First Byzantium block")]
        [TestCase(7279999, "0xa00bc324", 7280000, "Last Byzantium block")]
        [TestCase(7280000, "0x668db0af", 9069000, "First Constantinople block")]
        [TestCase(9068999, "0x668db0af", 9069000, "Last Constantinople block")]
        [TestCase(9069000, "0x879d6e30", 9200000, "First Istanbul block")]
        [TestCase(9199999, "0x879d6e30", 9200000, "Last Istanbul block")]
        [TestCase(9200000, "0xe029e991", 0, "First Muir Glacier")]
        [TestCase(9500000, "0xe029e991", 0, "Muir Glacier block")]
        public void Fork_id_and_hash_as_expected(long head, string forkHashHex, long next, string description)
        {
            byte[] expectedForkHash = Bytes.FromHexString(forkHashHex);

            MainNetSpecProvider mainNetSpecProvider = MainNetSpecProvider.Instance;
            byte[] forkHash = ForkInfo.CalculateForkHash(mainNetSpecProvider, head);
            forkHash.Should().BeEquivalentTo(expectedForkHash, description);
            
            ForkId forkId = ForkInfo.CalculateForkId(mainNetSpecProvider, head);
            forkId.Next.Should().Be(next);
            forkId.ForkHash.Should().BeEquivalentTo(expectedForkHash);
        }
    }
}