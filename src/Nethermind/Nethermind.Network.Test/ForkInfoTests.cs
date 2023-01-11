// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Test.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.Network.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class ForkInfoTests
    {
        [TestCase(0, 0ul, "0xfc64ec04", 1_150_000ul, "Unsynced")]
        [TestCase(1_149_999, 0ul, "0xfc64ec04", 1_150_000ul, "Last Frontier block")]
        [TestCase(1_150_000, 0ul, "0x97c2c34c", 1_920_000ul, "First Homestead block")]
        [TestCase(1_919_999, 0ul, "0x97c2c34c", 1_920_000ul, "Last Homestead block")]
        [TestCase(1_920_000, 0ul, "0x91d1f948", 2_463_000ul, "First DAO block")]
        [TestCase(2_462_999, 0ul, "0x91d1f948", 2_463_000ul, "Last DAO block")]
        [TestCase(2_463_000, 0ul, "0x7a64da13", 2_675_000ul, "First Tangerine block")]
        [TestCase(2_674_999, 0ul, "0x7a64da13", 2_675_000ul, "Last Tangerine block")]
        [TestCase(2_675_000, 0ul, "0x3edd5b10", 4_370_000ul, "First Spurious block")]
        [TestCase(4_369_999, 0ul, "0x3edd5b10", 4_370_000ul, "Last Spurious block")]
        [TestCase(4_370_000, 0ul, "0xa00bc324", 7_280_000ul, "First Byzantium block")]
        [TestCase(7_279_999, 0ul, "0xa00bc324", 7_280_000ul, "Last Byzantium block")]
        [TestCase(7_280_000, 0ul, "0x668db0af", 9_069_000ul, "First Constantinople block")]
        [TestCase(9_068_999, 0ul, "0x668db0af", 9_069_000ul, "Last Constantinople block")]
        [TestCase(9_069_000, 0ul, "0x879d6e30", 9_200_000ul, "First Istanbul block")]
        [TestCase(9_199_999, 0ul, "0x879d6e30", 9_200_000ul, "Last Istanbul block")]
        [TestCase(9_200_000, 0ul, "0xe029e991", 12_244_000ul, "Last Muir Glacier")]
        [TestCase(12_244_000, 0ul, "0x0eb440f6", 12_965_000ul, "First Berlin")]
        [TestCase(12_964_999, 0ul, "0x0eb440f6", 12_965_000ul, "Last Berlin")]
        [TestCase(12_965_000, 0ul, "0xb715077d", 13_773_000ul, "First London")]
        [TestCase(13_772_999, 0ul, "0xb715077d", 13_773_000ul, "Last London")]
        [TestCase(13_773_000, 0ul, "0x20c327fc", 15_050_000ul, "First Arrow Glacier")]
        [TestCase(15_049_999, 0ul, "0x20c327fc", 15_050_000ul, "Last Arrow Glacier")]
        [TestCase(15_050_000, 0ul, "0xf0afd0e3", 0ul, "First Gray Glacier")]
        [TestCase(20_000_000, 0ul, "0xf0afd0e3", 0ul, "Future Gray Glacier")]
        public void Fork_id_and_hash_as_expected(long head, ulong headTimestamp, string forkHashHex, ulong next, string description)
        {
            Test(head, headTimestamp, KnownHashes.MainnetGenesis, forkHashHex, next, description, MainnetSpecProvider.Instance, "foundation.json");
        }

        [TestCase(0, 0ul, "0xfc64ec04", 1_150_000ul, "Unsynced")]
        [TestCase(1_149_999, 0ul, "0xfc64ec04", 1_150_000ul, "Last Frontier block")]
        [TestCase(1_150_000, 0ul, "0x97c2c34c", 1_920_000ul, "First Homestead block")]
        [TestCase(1_919_999, 0ul, "0x97c2c34c", 1_920_000ul, "Last Homestead block")]
        [TestCase(1_920_000, 0ul, "0x91d1f948", 2_463_000ul, "First DAO block")]
        [TestCase(2_462_999, 0ul, "0x91d1f948", 2_463_000ul, "Last DAO block")]
        [TestCase(2_463_000, 0ul, "0x7a64da13", 2_675_000ul, "First Tangerine block")]
        [TestCase(2_674_999, 0ul, "0x7a64da13", 2_675_000ul, "Last Tangerine block")]
        [TestCase(2_675_000, 0ul, "0x3edd5b10", 4_370_000ul, "First Spurious block")]
        [TestCase(4_369_999, 0ul, "0x3edd5b10", 4_370_000ul, "Last Spurious block")]
        [TestCase(4_370_000, 0ul, "0xa00bc324", 7_280_000ul, "First Byzantium block")]
        [TestCase(7_279_999, 0ul, "0xa00bc324", 7_280_000ul, "Last Byzantium block")]
        [TestCase(7_280_000, 0ul, "0x668db0af", 9_069_000ul, "First Constantinople block")]
        [TestCase(9_068_999, 0ul, "0x668db0af", 9_069_000ul, "Last Constantinople block")]
        [TestCase(9_069_000, 0ul, "0x879d6e30", 9_200_000ul, "First Istanbul block")]
        [TestCase(9_199_999, 0ul, "0x879d6e30", 9_200_000ul, "Last Istanbul block")]
        [TestCase(9_200_000, 0ul, "0xe029e991", 12_244_000ul, "Last Muir Glacier")]
        [TestCase(12_244_000, 0ul, "0x0eb440f6", 12_965_000ul, "First Berlin")]
        [TestCase(12_964_999, 0ul, "0x0eb440f6", 12_965_000ul, "Last Berlin")]
        [TestCase(12_965_000, 0ul, "0xb715077d", 13_773_000ul, "First London")]
        [TestCase(13_772_999, 0ul, "0xb715077d", 13_773_000ul, "Last London")]
        [TestCase(13_773_000, 0ul, "0x20c327fc", 15_050_000ul, "First Arrow Glacier")]
        [TestCase(15_049_999, 0ul, "0x20c327fc", 15_050_000ul, "Last Arrow Glacier")]
        [TestCase(15_050_000, 0ul, "0xf0afd0e3", 18_000_000ul, "First Gray Glacier")]
        [TestCase(17_999_999, 0ul, "0xf0afd0e3", 18_000_000ul, "Last Gray Glacier")]
        [TestCase(18_000_000, 0ul, "0x4fb8a872", 1_668_000_000ul, "First Merge Start")]
        [TestCase(20_000_000, 0ul, "0x4fb8a872", 1_668_000_000ul, "Last Merge Start")]
        [TestCase(20_000_000, 1_668_000_000ul, "0xc1fdf181", 0ul, "First Shanghai")]
        [TestCase(21_000_000, 1_768_000_000ul, "0xc1fdf181", 0ul, "Future Shanghai")]
        public void Fork_id_and_hash_as_expected_with_timestamps(long head, ulong headTimestamp, string forkHashHex, ulong next, string description)
        {
            Test(head, headTimestamp, KnownHashes.MainnetGenesis, forkHashHex, next, description, "TimestampForkIdTest.json", "../../../");
        }

        [TestCase(15_050_000, 0ul, "0xf0afd0e3", 21_000_000ul, "First Gray Glacier")]
        [TestCase(21_000_000, 0ul, "0x3f5fd195", 0ul, "First Merge Fork Id test")]
        [TestCase(21_811_000, 0ul, "0x3f5fd195", 0ul, "Future Merge Fork Id test")]
        public void Fork_id_and_hash_as_expected_with_merge_fork_id(long head, ulong headTimestamp, string forkHashHex, ulong next, string description)
        {
            ChainSpecLoader loader = new ChainSpecLoader(new EthereumJsonSerializer());
            ChainSpec spec = loader.Load(File.ReadAllText(Path.Combine("../../../../Chains", "foundation.json")));
            spec.Parameters.MergeForkIdTransition = 21_000_000L;
            spec.MergeForkIdBlockNumber = 21_000_000L;
            ChainSpecBasedSpecProvider provider = new ChainSpecBasedSpecProvider(spec);
            Test(head, headTimestamp, KnownHashes.MainnetGenesis, forkHashHex, next, description, provider);
        }

        [TestCase(0, 0ul, "0xa3f5ab08", 1_561_651ul, "Unsynced")]
        [TestCase(1_561_650L, 0ul, "0xa3f5ab08", 1_561_651ul, "Last Constantinople block")]
        [TestCase(1_561_651L, 0ul, "0xc25efa5c", 4_460_644ul, "First Istanbul block")]
        [TestCase(4_460_644L, 0ul, "0x757a1c47", 5_062_605ul, "First Berlin block")]
        [TestCase(4_600_000L, 0ul, "0x757a1c47", 5_062_605ul, "Future Berlin block")]
        [TestCase(5_062_605L, 0ul, "0xB8C6299D", 0ul, "First London block")]
        [TestCase(6_000_000, 0ul, "0xB8C6299D", 0ul, "Future London block")]
        public void Fork_id_and_hash_as_expected_on_goerli(long head, ulong headTimestamp, string forkHashHex, ulong next, string description)
        {
            Test(head, headTimestamp, KnownHashes.GoerliGenesis, forkHashHex, next, description, GoerliSpecProvider.Instance, "goerli.json");
        }

        [TestCase(0, 0ul, "0x3b8e0691", 1ul, "Unsynced, last Frontier block")]
        [TestCase(1, 0ul, "0x60949295", 2ul, "First and last Homestead block")]
        [TestCase(2, 0ul, "0x8bde40dd", 3ul, "First and last Tangerine block")]
        [TestCase(3, 0ul, "0xcb3a64bb", 1035301ul, "First Spurious block")]
        [TestCase(1_035_300L, 0ul, "0xcb3a64bb", 1_035_301ul, "Last Spurious block")]
        [TestCase(1_035_301L, 0ul, "0x8d748b57", 3_660_663ul, "First Byzantium block")]
        [TestCase(3_660_662L, 0ul, "0x8d748b57", 3_660_663ul, "Last Byzantium block")]
        [TestCase(3_660_663L, 0ul, "0xe49cab14", 4_321_234ul, "First Constantinople block")]
        [TestCase(4_321_233L, 0ul, "0xe49cab14", 4_321_234ul, "Last Constantinople block")]
        [TestCase(4_321_234L, 0ul, "0xafec6b27", 5_435_345ul, "First Petersburg block")]
        [TestCase(5_435_344L, 0ul, "0xafec6b27", 5_435_345ul, "Last Petersburg block")]
        [TestCase(5_435_345L, 0ul, "0xcbdb8838", 8_290_928ul, "First Istanbul block")]
        [TestCase(8_290_928L, 0ul, "0x6910c8bd", 8_897_988ul, "First Berlin block")]
        [TestCase(8_700_000L, 0ul, "0x6910c8bd", 8_897_988ul, "Future Berlin block")]
        [TestCase(8_897_988L, 0ul, "0x8E29F2F3", 0ul, "First London block")]
        [TestCase(9_000_000L, 0ul, "0x8E29F2F3", 0ul, "Future London block")]
        public void Fork_id_and_hash_as_expected_on_rinkeby(long head, ulong headTimestamp, string forkHashHex, ulong next, string description)
        {
            Test(head, headTimestamp, KnownHashes.RinkebyGenesis, forkHashHex, next, description, RinkebySpecProvider.Instance, "rinkeby.json");
        }

        [TestCase(0, 0ul, "0x30c7ddbc", 10ul, " Unsynced, last Frontier, Homestead and first Tangerine block")]
        [TestCase(9, 0ul, "0x30c7ddbc", 10ul, "Last Tangerine block")]
        [TestCase(10, 0ul, "0x63760190", 1_700_000ul, "First Spurious block")]
        [TestCase(1_699_999L, 0ul, "0x63760190", 1_700_000ul, "Last Spurious block")]
        [TestCase(1_700_000L, 0ul, "0x3ea159c7", 4_230_000ul, "First Byzantium block")]
        [TestCase(4_229_999L, 0ul, "0x3ea159c7", 4_230_000ul, "Last Byzantium block")]
        [TestCase(4_230_000L, 0ul, "0x97b544f3", 4_939_394ul, "First Constantinople block")]
        [TestCase(4_939_393L, 0ul, "0x97b544f3", 4_939_394ul, "Last Constantinople block")]
        [TestCase(4_939_394L, 0ul, "0xd6e2149b", 6_485_846ul, "First Petersburg block")]
        [TestCase(6_485_845L, 0ul, "0xd6e2149b", 6_485_846ul, "Last Petersburg block")]
        [TestCase(6_485_846L, 0ul, "0x4bc66396", 7_117_117ul, "First Istanbul block")]
        [TestCase(7_117_117L, 0ul, "0x6727ef90", 9_812_189ul, "First Muir Glacier block")]
        [TestCase(9_812_189L, 0ul, "0xa157d377", 10_499_401ul, "First Berlin block")]
        [TestCase(9_900_000L, 0ul, "0xa157d377", 10_499_401ul, "Future Berlin block")]
        [TestCase(10_499_401L, 0ul, "0x7119B6B3", 0ul, "First London block")]
        [TestCase(12_000_000, 0ul, "0x7119B6B3", 0ul, "Future London block")]
        public void Fork_id_and_hash_as_expected_on_ropsten(long head, ulong headTimestamp, string forkHashHex, ulong next, string description)
        {
            Test(head, headTimestamp, KnownHashes.RopstenGenesis, forkHashHex, next, description, RopstenSpecProvider.Instance, "ropsten.json");
        }

        [TestCase(0, 0ul, "0xFE3366E7", 1735371ul, "Sepolia genesis")]
        [TestCase(1735370, 0ul, "0xFE3366E7", 1735371ul, "Sepolia Last block before MergeForkIdTranstion")]
        [TestCase(1735371, 0ul, "0xb96cbd13", 0ul, "First block - Sepolia MergeForkIdTransition")]
        public void Fork_id_and_hash_as_expected_on_sepolia(long head, ulong headTimestamp, string forkHashHex, ulong next, string description)
        {
            Test(head, headTimestamp, KnownHashes.SepoliaGenesis, forkHashHex, next, description, SepoliaSpecProvider.Instance, "sepolia.json");
        }

        [TestCase(0, 0ul, "0xc42480d3", 0ul, "shandong genesis")]
        public void Fork_id_and_hash_as_expected_on_shandong(long head, ulong headTimestamp, string forkHashHex, ulong next, string description)
        {
            ChainSpecLoader loader = new(new EthereumJsonSerializer());
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../Chains/shandong.json");
            ChainSpec chainSpec = loader.Load(File.ReadAllText(path));
            ChainSpecBasedSpecProvider chainSpecBasedSpecProvider = new ChainSpecBasedSpecProvider(chainSpec);

            Test(head, headTimestamp, new Keccak("0xbea94d3492ed9c41556a1c45c27da4947938880fb4c15f31fb742e5a1c10a2fb"), forkHashHex, next, description, chainSpecBasedSpecProvider, "shandong.json");
        }

        [TestCase(0, 0ul, "0xf64909b1", 1604400ul, "Unsynced, last Frontier, Homestead, Tangerine, Spurious, Byzantium")]
        [TestCase(1604399, 0ul, "0xf64909b1", 1604400ul, "Last Byzantium block")]
        [TestCase(1604400, 0ul, "0xfde2d083", 2508800ul, "First Constantinople block")]
        [TestCase(2508799, 0ul, "0xfde2d083", 2508800ul, "Last Constantinople block")]
        [TestCase(2508800, 0ul, "0xfc1d8f2f", 7298030ul, "First Petersburg block")]
        [TestCase(7298029, 0ul, "0xfc1d8f2f", 7298030ul, "Last Petersburg block")]
        [TestCase(7298030, 0ul, "0x54d05e6c", 9186425ul, "First Istanbul block")]
        [TestCase(9186424, 0ul, "0x54d05e6c", 9186425ul, "Last Istanbul block")]
        [TestCase(9186425, 0ul, "0xb6e6cd81", 16101500ul, "First POSDAO Activation block")]
        [TestCase(16101499, 0ul, "0xb6e6cd81", 16101500ul, "Last POSDAO Activation block")]
        [TestCase(16101500, 0ul, "0x069a83d9", 19040000ul, "First Berlin block")]
        [TestCase(19039999, 0ul, "0x069a83d9", 19040000ul, "Last Berlin block")]
        [TestCase(19040000, 0ul, "0x018479d3", 0ul, "First London block")]
        [TestCase(21735000, 0ul, "0x018479d3", 0ul, "First GIP-31 block")]
        public void Fork_id_and_hash_as_expected_on_gnosis(long head, ulong headTimestamp, string forkHashHex, ulong next, string description)
        {
            ChainSpecLoader loader = new ChainSpecLoader(new EthereumJsonSerializer());
            ChainSpec spec = loader.Load(File.ReadAllText(Path.Combine("../../../../Chains", "gnosis.json")));
            ChainSpecBasedSpecProvider provider = new ChainSpecBasedSpecProvider(spec);
            Test(head, headTimestamp, KnownHashes.GnosisGenesis, forkHashHex, next, description, provider);
        }

        [TestCase(0, 0ul, "0x50d39d7b", 0ul, "Chiado genesis")]
        public void Fork_id_and_hash_as_expected_on_chiado(long head, ulong headTimestamp, string forkHashHex, ulong next, string description)
        {
            ChainSpecLoader loader = new ChainSpecLoader(new EthereumJsonSerializer());
            ChainSpec spec = loader.Load(File.ReadAllText(Path.Combine("../../../../Chains", "chiado.json")));
            ChainSpecBasedSpecProvider provider = new ChainSpecBasedSpecProvider(spec);
            Test(head, headTimestamp, KnownHashes.ChiadoGenesis, forkHashHex, next, description, provider);
        }


        [TestCase(2ul, 3ul, 2ul, 3ul)]
        [TestCase(2ul, null, 2ul, 2ul)]
        [TestCase(null, 3ul, 3ul, 3ul)]
        [TestCase(null, null, 1ul, 1ul)]
        public void Chain_id_and_network_id_have_proper_default_values(ulong? specNetworkId, ulong? specChainId, ulong expectedNetworkId, ulong expectedChainId)
        {
            ChainSpecLoader loader = new(new EthereumJsonSerializer());

            ChainSpec spec = loader.Load($"{{\"params\":{{\"networkID\":{specNetworkId?.ToString() ?? "null"},\"chainId\":{specChainId?.ToString() ?? "null"}}},\"engine\":{{\"NethDev\":{{}}}}}}");
            ChainSpecBasedSpecProvider provider = new(spec);

            spec.ChainId.Should().Be(expectedChainId);
            spec.NetworkId.Should().Be(expectedNetworkId);
            provider.ChainId.Should().Be(expectedChainId);
            provider.NetworkId.Should().Be(expectedNetworkId);
        }

        private static void Test(long head, ulong headTimestamp, Keccak genesisHash, string forkHashHex, ulong next, string description, ISpecProvider specProvider, string chainSpec, string path = "../../../../Chains")
        {
            Test(head, headTimestamp, genesisHash, forkHashHex, next, description, specProvider);
            Test(head, headTimestamp, genesisHash, forkHashHex, next, description, chainSpec, path);
        }

        private static void Test(long head, ulong headTimestamp, Keccak genesisHash, string forkHashHex, ulong next, string description, string chainSpec, string path = "../../../../Chains")
        {
            ChainSpecLoader loader = new ChainSpecLoader(new EthereumJsonSerializer());
            ChainSpec spec = loader.Load(File.ReadAllText(Path.Combine(path, chainSpec)));
            ChainSpecBasedSpecProvider provider = new ChainSpecBasedSpecProvider(spec);
            Test(head, headTimestamp, genesisHash, forkHashHex, next, description, provider);
        }

        private static void Test(long head, ulong headTimestamp, Keccak genesisHash, string forkHashHex, ulong next, string description, ISpecProvider specProvider)
        {
            byte[] expectedForkHash = Bytes.FromHexString(forkHashHex);

            ForkId forkId = new ForkInfo(specProvider, genesisHash).GetForkId(head, headTimestamp);
            byte[] forkHash = forkId.ForkHash;
            forkHash.Should().BeEquivalentTo(expectedForkHash, description);

            forkId.Next.Should().Be(next);
            forkId.ForkHash.Should().BeEquivalentTo(expectedForkHash);
        }
    }
}
