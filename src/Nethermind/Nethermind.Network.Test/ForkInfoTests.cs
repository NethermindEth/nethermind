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

using System.IO;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.Network.Test
{
    [Parallelizable(ParallelScope.Self)]
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
            Test(head, KnownHashes.MainnetGenesis, forkHashHex, next, description, MainnetSpecProvider.Instance, "foundation.json");
        }

        [TestCase(0, "0xa3f5ab08", 1561651, "Unsynced")]
        [TestCase(1561650, "0xa3f5ab08", 1561651, "Last Constantinople block")]
        [TestCase(1561651, "0xc25efa5c", 0, "First Istanbul block")]
        public void Fork_id_and_hash_as_expected_on_goerli(long head, string forkHashHex, long next, string description)
        {
            Test(head, KnownHashes.GoerliGenesis, forkHashHex, next, description, GoerliSpecProvider.Instance, "goerli.json");
        }

        [TestCase(0, "0x3b8e0691", 1, "Unsynced, last Frontier block")]
        [TestCase(1, "0x60949295", 2, "First and last Homestead block")]
        [TestCase(2, "0x8bde40dd", 3, "First and last Tangerine block")]
        [TestCase(3, "0xcb3a64bb", 1035301, "First Spurious block")]
        [TestCase(1035300, "0xcb3a64bb", 1035301, "Last Spurious block")]
        [TestCase(1035301, "0x8d748b57", 3660663, "First Byzantium block")]
        [TestCase(3660662, "0x8d748b57", 3660663, "Last Byzantium block")]
        [TestCase(3660663, "0xe49cab14", 4321234, "First Constantinople block")]
        [TestCase(4321233, "0xe49cab14", 4321234, "Last Constantinople block")]
        [TestCase(4321234, "0xafec6b27", 5435345, "First Petersburg block")]
        [TestCase(5435344, "0xafec6b27", 5435345, "Last Petersburg block")]
        [TestCase(5435345, "0xcbdb8838", 0, "First Istanbul block")]
        [TestCase(6000000, "0xcbdb8838", 0, "")]
        public void Fork_id_and_hash_as_expected_on_rinkeby(long head, string forkHashHex, long next, string description)
        {
            Test(head, KnownHashes.RinkebyGenesis, forkHashHex, next, description, RinkebySpecProvider.Instance, "rinkeby.json");
        }

        [TestCase(0, "0x30c7ddbc", 10, " Unsynced, last Frontier, Homestead and first Tangerine block")]
        [TestCase(9, "0x30c7ddbc", 10, "Last Tangerine block")]
        [TestCase(10, "0x63760190", 1700000, "First Spurious block")]
        [TestCase(1699999, "0x63760190", 1700000, "Last Spurious block")]
        [TestCase(1700000, "0x3ea159c7", 4230000, "First Byzantium block")]
        [TestCase(4229999, "0x3ea159c7", 4230000, "Last Byzantium block")]
        [TestCase(4230000, "0x97b544f3", 4939394, "First Constantinople block")]
        [TestCase(4939393, "0x97b544f3", 4939394, "Last Constantinople block")]
        [TestCase(4939394, "0xd6e2149b", 6485846, "First Petersburg block")]
        [TestCase(6485845, "0xd6e2149b", 6485846, "Last Petersburg block")]
        [TestCase(6485846, "0x4bc66396", 7117117L, "First Istanbul block")]
        [TestCase(7117117, "0x6727ef90", 0, "First Muir Glacier block")]
        [TestCase(7500000, "0x6727ef90", 0, "Future Muir Glacier block")]
        public void Fork_id_and_hash_as_expected_on_ropsten(long head, string forkHashHex, long next, string description)
        {
            Test(head, KnownHashes.RopstenGenesis, forkHashHex, next, description, RopstenSpecProvider.Instance, "ropsten.json");
        }

        private static void Test(long head, Keccak genesisHash, string forkHashHex, long next, string description, ISpecProvider specProvider, string chainSpec)
        {
            Test(head, genesisHash, forkHashHex, next, description, specProvider);

            ChainSpecLoader loader = new ChainSpecLoader(new EthereumJsonSerializer());
            ChainSpec spec = loader.Load(File.ReadAllText("../../../../Chains/" + chainSpec));
            ChainSpecBasedSpecProvider provider = new ChainSpecBasedSpecProvider(spec);
            Test(head, genesisHash, forkHashHex, next, description, provider);
        }

        private static void Test(long head, Keccak genesisHash, string forkHashHex, long next, string description, ISpecProvider specProvider)
        {
            byte[] expectedForkHash = Bytes.FromHexString(forkHashHex);
            byte[] forkHash = ForkInfo.CalculateForkHash(specProvider, head, genesisHash);
            forkHash.Should().BeEquivalentTo(expectedForkHash, description);

            ForkId forkId = ForkInfo.CalculateForkId(specProvider, head, genesisHash);
            forkId.Next.Should().Be(next);
            forkId.ForkHash.Should().BeEquivalentTo(expectedForkHash);
        }
    }
}