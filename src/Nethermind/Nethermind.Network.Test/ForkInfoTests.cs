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
        [TestCase(0, "0xfc64ec04", 1_150_000, "Unsynced")]
        [TestCase(1_149_999, "0xfc64ec04", 1_150_000, "Last Frontier block")]
        [TestCase(1_150_000, "0x97c2c34c", 1_920_000, "First Homestead block")]
        [TestCase(1_919_999, "0x97c2c34c", 1_920_000, "Last Homestead block")]
        [TestCase(1_920_000, "0x91d1f948", 2_463_000, "First DAO block")]
        [TestCase(2_462_999, "0x91d1f948", 2_463_000, "Last DAO block")]
        [TestCase(2_463_000, "0x7a64da13", 2_675_000, "First Tangerine block")]
        [TestCase(2_674_999, "0x7a64da13", 2_675_000, "Last Tangerine block")]
        [TestCase(2_675_000, "0x3edd5b10", 4_370_000, "First Spurious block")]
        [TestCase(4_369_999, "0x3edd5b10", 4_370_000, "Last Spurious block")]
        [TestCase(4_370_000, "0xa00bc324", 7_280_000, "First Byzantium block")]
        [TestCase(7_279_999, "0xa00bc324", 7_280_000, "Last Byzantium block")]
        [TestCase(7_280_000, "0x668db0af", 9_069_000, "First Constantinople block")]
        [TestCase(9_068_999, "0x668db0af", 9_069_000, "Last Constantinople block")]
        [TestCase(9_069_000, "0x879d6e30", 9_200_000, "First Istanbul block")]
        [TestCase(9_199_999, "0x879d6e30", 9_200_000, "Last Istanbul block")]
        [TestCase(9_200_000, "0xe029e991", 12_244_000, "Last Muir Glacier")]
        [TestCase(12_244_000, "0x0eb440f6", 12_965_000, "First Berlin")]
        [TestCase(12_964_999, "0x0eb440f6", 12_965_000, "Last Berlin")]
        [TestCase(12_965_000, "0xb715077d", 0L, "First London")]
        [TestCase(12_985_100, "0xb715077d", 0L, "Future London")]
        public void Fork_id_and_hash_as_expected(long head, string forkHashHex, long next, string description)
        {
            Test(head, KnownHashes.MainnetGenesis, forkHashHex, next, description, MainnetSpecProvider.Instance, "foundation.json");
        }

        [TestCase(0, "0xa3f5ab08", 1_561_651L, "Unsynced")]
        [TestCase(1_561_650L, "0xa3f5ab08", 1_561_651L, "Last Constantinople block")]
        [TestCase(1_561_651L, "0xc25efa5c", 4_460_644L, "First Istanbul block")]
        [TestCase(4_460_644L, "0x757a1c47", 5_062_605, "First Berlin block")]
        [TestCase(4_600_000L, "0x757a1c47", 5_062_605, "Future Berlin block")]
        [TestCase(5_062_605L, "0xB8C6299D", 0L, "First London block")]
        [TestCase(6_000_000, "0xB8C6299D", 0L, "Future London block")]
        public void Fork_id_and_hash_as_expected_on_goerli(long head, string forkHashHex, long next, string description)
        {
            Test(head, KnownHashes.GoerliGenesis, forkHashHex, next, description, GoerliSpecProvider.Instance, "goerli.json");
        }

        [TestCase(0, "0x3b8e0691", 1, "Unsynced, last Frontier block")]
        [TestCase(1, "0x60949295", 2, "First and last Homestead block")]
        [TestCase(2, "0x8bde40dd", 3, "First and last Tangerine block")]
        [TestCase(3, "0xcb3a64bb", 1035301, "First Spurious block")]
        [TestCase(1_035_300L, "0xcb3a64bb", 1_035_301L, "Last Spurious block")]
        [TestCase(1_035_301L, "0x8d748b57", 3_660_663L, "First Byzantium block")]
        [TestCase(3_660_662L, "0x8d748b57", 3_660_663L, "Last Byzantium block")]
        [TestCase(3_660_663L, "0xe49cab14", 4_321_234L, "First Constantinople block")]
        [TestCase(4_321_233L, "0xe49cab14", 4_321_234L, "Last Constantinople block")]
        [TestCase(4_321_234L, "0xafec6b27", 5_435_345L, "First Petersburg block")]
        [TestCase(5_435_344L, "0xafec6b27", 5_435_345L, "Last Petersburg block")]
        [TestCase(5_435_345L, "0xcbdb8838", 8_290_928L, "First Istanbul block")]
        [TestCase(8_290_928L, "0x6910c8bd", 8_897_988L, "First Berlin block")]
        [TestCase(8_700_000L, "0x6910c8bd", 8_897_988L, "Future Berlin block")]
        [TestCase(8_897_988L, "0x8E29F2F3", 0L, "First London block")]
        [TestCase(9_000_000L, "0x8E29F2F3", 0L, "Future London block")]
        public void Fork_id_and_hash_as_expected_on_rinkeby(long head, string forkHashHex, long next, string description)
        {
            Test(head, KnownHashes.RinkebyGenesis, forkHashHex, next, description, RinkebySpecProvider.Instance, "rinkeby.json");
        }

        [TestCase(0, "0x30c7ddbc", 10, " Unsynced, last Frontier, Homestead and first Tangerine block")]
        [TestCase(9, "0x30c7ddbc", 10, "Last Tangerine block")]
        [TestCase(10, "0x63760190", 1_700_000L, "First Spurious block")]
        [TestCase(1_699_999L, "0x63760190", 1_700_000L, "Last Spurious block")]
        [TestCase(1_700_000L, "0x3ea159c7", 4_230_000L, "First Byzantium block")]
        [TestCase(4_229_999L, "0x3ea159c7", 4_230_000L, "Last Byzantium block")]
        [TestCase(4_230_000L, "0x97b544f3", 4_939_394L, "First Constantinople block")]
        [TestCase(4_939_393L, "0x97b544f3", 4_939_394L, "Last Constantinople block")]
        [TestCase(4_939_394L, "0xd6e2149b", 6_485_846L, "First Petersburg block")]
        [TestCase(6_485_845L, "0xd6e2149b", 6_485_846L, "Last Petersburg block")]
        [TestCase(6_485_846L, "0x4bc66396", 7_117_117L, "First Istanbul block")]
        [TestCase(7_117_117L, "0x6727ef90", 9_812_189L, "First Muir Glacier block")]
        [TestCase(9_812_189L, "0xa157d377", 10_499_401L, "First Berlin block")]
        [TestCase(9_900_000L, "0xa157d377", 10_499_401L, "Future Berlin block")]
        [TestCase(10_499_401L, "0x7119B6B3", 0L, "First London block")]
        [TestCase(12_000_000, "0x7119B6B3", 0L, "Future London block")]
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
