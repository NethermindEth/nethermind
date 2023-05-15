// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test;

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
    [TestCase(15_050_000, 0ul, "0xf0afd0e3", 1681338455ul, "First Gray Glacier")]
    [TestCase(15_051_000, 0ul, "0xf0afd0e3", 1681338455ul, "Future Gray Glacier")]
    [TestCase(15_051_000, 1681338455ul, "0xdce96c2d", 0ul, "First Shanghai timestamp")]
    [TestCase(15_051_000, 9981338455ul, "0xdce96c2d", 0ul, "Future Shanghai timestamp")]
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
    [TestCase(15_050_000, 0ul, "0xf0afd0e3", 1_668_000_000ul, "First Gray Glacier")]
    [TestCase(17_999_999, 0ul, "0xf0afd0e3", 1_668_000_000ul, "Last Gray Glacier")]
    [TestCase(20_000_000, 1_668_000_000ul, "0x71147644", 0ul, "First Shanghai")]
    [TestCase(21_000_000, 1_768_000_000ul, "0x71147644", 0ul, "Future Shanghai")]
    public void Fork_id_and_hash_as_expected_with_timestamps(long head, ulong headTimestamp, string forkHashHex, ulong next, string description)
    {
        Test(head, headTimestamp, KnownHashes.MainnetGenesis, forkHashHex, next, description, "TimestampForkIdTest.json", "../../../");
    }

    [TestCase(15_050_000, 0ul, "0xf0afd0e3", 21_000_000ul, "First Gray Glacier")]
    [TestCase(21_000_000, 0ul, "0x3f5fd195", 1681338455UL, "First Merge Fork Id test")]
    [TestCase(21_811_000, 0ul, "0x3f5fd195", 1681338455UL, "Future Merge Fork Id test")]
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
    [TestCase(5_062_605L, 0ul, "0xB8C6299D", 1678832736ul, "First London block")]
    [TestCase(6_000_000, 0ul, "0xB8C6299D", 1678832736ul, "Future London block")]
    [TestCase(6_000_001, 1678832736ul, "0xf9843abf", 0ul, "First Shanghai timestamp")]
    [TestCase(6_000_001, 2678832736ul, "0xf9843abf", 0ul, "Future Shanghai timestamp")]
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
    [TestCase(1735371, 0ul, "0xb96cbd13", 1677557088UL, "First block - Sepolia MergeForkIdTransition")]
    [TestCase(1735372, 1677557088ul, "0xf7f9bc08", 0ul, "Shanghai")]
    [TestCase(1735372, 2677557088ul, "0xf7f9bc08", 0ul, "Future Shanghai")]
    public void Fork_id_and_hash_as_expected_on_sepolia(long head, ulong headTimestamp, string forkHashHex, ulong next, string description)
    {
        Test(head, headTimestamp, KnownHashes.SepoliaGenesis, forkHashHex, next, description, SepoliaSpecProvider.Instance, "sepolia.json");
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

    [TestCase(0L, 0UL, "0x50d39d7b", ChiadoSpecProvider.ShanghaiTimestamp, "Chiado genesis")]
    public void Fork_id_and_hash_as_expected_on_chiado(long head, ulong headTimestamp, string forkHashHex, ulong next, string description)
    {
        ChainSpecLoader loader = new ChainSpecLoader(new EthereumJsonSerializer());
        ChainSpec spec = loader.Load(File.ReadAllText(Path.Combine("../../../../Chains", "chiado.json")));
        ChainSpecBasedSpecProvider provider = new ChainSpecBasedSpecProvider(spec);
        Test(head, headTimestamp, KnownHashes.ChiadoGenesis, forkHashHex, next, description, provider);
    }

    // Local is mainnet Gray Glacier, remote announces the same. No future fork is announced.
    [TestCase(MainnetSpecProvider.GrayGlacierBlockNumber, 0ul, "0xf0afd0e3", 0ul, ValidationResult.Valid)]

    // Local is mainnet Petersburg, remote announces the same. No future fork is announced.
    [TestCase(7987396, 0ul, "0x668db0af", 0ul, ValidationResult.Valid)]

    // Local is mainnet Petersburg, remote announces the same. Remote also announces a next fork
    // at block 0xffffffff, but that is uncertain.
    [TestCase(7987396, 0ul, "0x668db0af", ulong.MaxValue, ValidationResult.Valid)]

    // Local is mainnet currently in Byzantium only (so it's aware of Petersburg), remote announces
    // also Byzantium, but it's not yet aware of Petersburg (e.g. non updated node before the fork).
    // In this case we don't know if Petersburg passed yet or not.
    [TestCase(7279999, 0ul, "0xa00bc324", 0ul, ValidationResult.Valid)]

    // Local is mainnet currently in Byzantium only (so it's aware of Petersburg), remote announces
    // also Byzantium, and it's also aware of Petersburg (e.g. updated node before the fork). We
    // don't know if Petersburg passed yet (will pass) or not.
    [TestCase(7279999, 0ul, "0xa00bc324", 7280000ul, ValidationResult.Valid)]

    // Local is mainnet currently in Byzantium only (so it's aware of Petersburg), remote announces
    // also Byzantium, and it's also aware of some random fork (e.g. misconfigured Petersburg). As
    // neither forks passed at neither nodes, they may mismatch, but we still connect for now.
    [TestCase(7279999, 0ul, "0xa00bc324", ulong.MaxValue, ValidationResult.Valid)]

    // Local is mainnet exactly on Petersburg, remote announces Byzantium + knowledge about Petersburg. Remote
    // is simply out of sync, accept.
    [TestCase(7280000, 0ul, "0xa00bc324", 7280000ul, ValidationResult.Valid)]

    // Local is mainnet Petersburg, remote announces Byzantium + knowledge about Petersburg. Remote
    // is simply out of sync, accept.
    [TestCase(7987396, 0ul, "0xa00bc324", 7280000ul, ValidationResult.Valid)]

    // Local is mainnet Petersburg, remote announces Spurious + knowledge about Byzantium. Remote
    // is definitely out of sync. It may or may not need the Petersburg update, we don't know yet.
    [TestCase(7987396, 0ul, "0x3edd5b10", 4370000ul, ValidationResult.Valid)]

    // Local is mainnet Byzantium, remote announces Petersburg. Local is out of sync, accept.
    [TestCase(7279999, 0ul, "0x668db0af", 0ul, ValidationResult.Valid)]

    // Local is mainnet Spurious, remote announces Byzantium, but is not aware of Petersburg. Local
    // out of sync. Local also knows about a future fork, but that is uncertain yet.
    [TestCase(4369999, 0ul, "0xa00bc324", 0ul, ValidationResult.Valid)]

    // Local is mainnet Petersburg. remote announces Byzantium but is not aware of further forks.
    // Remote needs software update.
    [TestCase(7987396, 0ul, "0xa00bc324", 0ul, ValidationResult.RemoteStale)]

    // Local is mainnet Petersburg, and isn't aware of more forks. Remote announces Petersburg +
    // 0xffffffff. Local needs software update, reject.
    [TestCase(7987396, 0ul, "0x5cddc0e1", 0ul, ValidationResult.IncompatibleOrStale)]

    // Local is mainnet Byzantium, and is aware of Petersburg. Remote announces Petersburg +
    // 0xffffffff. Local needs software update, reject.
    [TestCase(7279999, 0ul, "0x5cddc0e1", 0ul, ValidationResult.IncompatibleOrStale)]

    // Local is mainnet Petersburg, remote is Rinkeby Petersburg.
    [TestCase(7987396, 0ul, "0xafec6b27", 0ul, ValidationResult.IncompatibleOrStale)]

    // Local is mainnet Gray Glacier, far in the future. Remote announces Gopherium (non existing fork)
    // at some future block 88888888, for itself, but past block for local. Local is incompatible.
    //
    // This case detects non-upgraded nodes with majority hash power (typical Ropsten mess).
    [TestCase(88888888, 0ul, "0xf0afd0e3", 88888888ul, ValidationResult.IncompatibleOrStale)]

    // Local is mainnet Byzantium. Remote is also in Byzantium, but announces Gopherium (non existing
    // fork) at block 7279999, before Petersburg. Local is incompatible.
    [TestCase(7279999, 0ul, "0xa00bc324", 7279999ul, ValidationResult.IncompatibleOrStale)]

    //------------------------------------
    // Block to timestamp transition tests
    //------------------------------------

    // Local is mainnet currently in Gray Glacier only (so it's aware of Shanghai), remote announces
    // also Gray Glacier, but it's not yet aware of Shanghai (e.g. non updated node before the fork).
    // In this case we don't know if Shanghai passed yet or not.
    [TestCase(15050000, 0ul, "0xf0afd0e3", 0ul, ValidationResult.Valid, true)]

    // Local is mainnet currently in Gray Glacier only (so it's aware of Shanghai), remote announces
    // also Gray Glacier, and it's also aware of Shanghai (e.g. updated node before the fork). We
    // don't know if Shanghai passed yet (will pass) or not.
    [TestCase(15050000, 0ul, "0xf0afd0e3", 1_668_000_000ul, ValidationResult.Valid, true)]

    // Local is mainnet currently in Gray Glacier only (so it's aware of Shanghai), remote announces
    // also Gray Glacier, and it's also aware of some random fork (e.g. misconfigured Shanghai). As
    // neither forks passed at neither nodes, they may mismatch, but we still connect for now.
    [TestCase(15050000, 0ul, "0xf0afd0e3", ulong.MaxValue, ValidationResult.Valid, true)]

    // Local is mainnet exactly on Shanghai, remote announces Gray Glacier + knowledge about Shanghai. Remote
    // is simply out of sync, accept.
    [TestCase(20000000, 1_668_000_000ul, "0xf0afd0e3", 1_668_000_000ul, ValidationResult.Valid, true)]

    // Local is mainnet Shanghai, remote announces Gray Glacier + knowledge about Shanghai. Remote
    // is simply out of sync, accept.
    [TestCase(20123456, 1_668_111_111ul, "0xf0afd0e3", 1_668_000_000ul, ValidationResult.Valid, true)]

    // Local is mainnet Shanghai, remote announces Arrow Glacier + knowledge about Gray Glacier. Remote
    // is definitely out of sync. It may or may not need the Shanghai update, we don't know yet.
    [TestCase(20000000, 1_668_000_000ul, "0x20c327fc", 15_050_000ul, ValidationResult.Valid, true)]

    // Local is mainnet Gray Glacier, remote announces Shanghai. Local is out of sync, accept.
    [TestCase(15_050_000, 0ul, "0x71147644", 0ul, ValidationResult.Valid, true)]

    // Local is mainnet Arrow Glacier, remote announces Gray Glacier, but is not aware of Shanghai. Local
    // out of sync. Local also knows about a future fork, but that is uncertain yet.
    [TestCase(13_773_000, 0ul, "0xf0afd0e3", 0ul, ValidationResult.Valid, true)]

    // Local is mainnet Shanghai. remote announces Gray Glacier but is not aware of further forks.
    // Remote needs software update.
    [TestCase(20000000, 1_668_000_000ul, "0xf0afd0e3", 0ul, ValidationResult.RemoteStale, true)]

    // Local is mainnet Gray Glacier, and isn't aware of more forks. Remote announces Gray Glacier +
    // 0xffffffff. Local needs software update, reject.
    [TestCase(15_050_000, 0ul, "0x87654321", ulong.MaxValue, ValidationResult.IncompatibleOrStale)]

    // Local is mainnet Gray Glacier, and is aware of Shanghai. Remote announces Shanghai +
    // 0xffffffff. Local needs software update, reject.
    [TestCase(15_050_000, 0ul, "0x98765432", ulong.MaxValue, ValidationResult.IncompatibleOrStale, true)]

    // Local is mainnet Gray Glacier, far in the future. Remote announces Gopherium (non existing fork)
    // at some future timestamp 8888888888, for itself, but past block for local. Local is incompatible.
    //
    // This case detects non-upgraded nodes with majority hash power (typical Ropsten mess).
    [TestCase(888888888, 1660000000ul, "0xf0afd0e3", 1660000000ul, ValidationResult.IncompatibleOrStale)]

    // Local is mainnet Gray Glacier. Remote is also in Gray Glacier, but announces Gopherium (non existing
    // fork) at block 7279999, before Shanghai. Local is incompatible.
    [TestCase(19999999, 1667999999ul, "0xf0afd0e3", 1667999999ul, ValidationResult.IncompatibleOrStale, true)]

    //----------------------
    // Timestamp based tests
    //----------------------

    // Local is mainnet Shanghai, remote announces the same. No future fork is announced.
    [TestCase(20000000, 1_668_000_000ul, "0x71147644", 0ul, ValidationResult.Valid, true)]

    // Local is mainnet Shanghai, remote announces the same. Remote also announces a next fork
    // at time 0xffffffff, but that is uncertain.
    [TestCase(20000000, 1_668_000_000ul, "0x71147644", ulong.MaxValue, ValidationResult.Valid, true)]

    // Local is mainnet Shanghai, and isn't aware of more forks. Remote announces Shanghai +
    // 0xffffffff. Local needs software update, reject.
    [TestCase(20000000, 1_668_000_000ul, "0x846271649", 0ul, ValidationResult.IncompatibleOrStale, true)]

    // Local is mainnet Shanghai, remote is random Shanghai.
    [TestCase(20000000, 1_668_000_000ul, "0x12345678", 0ul, ValidationResult.IncompatibleOrStale, true)]

    // Local is mainnet Shanghai, far in the future. Remote announces Gopherium (non existing fork)
    // at some future timestamp 8888888888, for itself, but past block for local. Local is incompatible.
    //
    // This case detects non-upgraded nodes with majority hash power (typical Ropsten mess).
    [TestCase(88888888, 8888888888ul, "0x71147644", 8888888888ul, ValidationResult.IncompatibleOrStale, true)]

    public void Test_fork_id_validation_mainnet(long headNumber, ulong headTimestamp, string hash, ulong next, ValidationResult result, bool UseTimestampSpec = false)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        Block head = Build.A.Block.WithNumber(headNumber).WithTimestamp(headTimestamp).TestObject;
        blockTree.Head.Returns(head);

        ISpecProvider specProvider = MainnetSpecProvider.Instance;
        if (UseTimestampSpec)
        {
            ChainSpecLoader loader = new ChainSpecLoader(new EthereumJsonSerializer());
            ChainSpec spec = loader.Load(File.ReadAllText(Path.Combine("../../../", "TimestampForkIdTest.json")));
            specProvider = new ChainSpecBasedSpecProvider(spec);
        }

        ForkInfo forkInfo = new(specProvider, KnownHashes.MainnetGenesis);

        forkInfo.ValidateForkId(new ForkId(Bytes.ReadEthUInt32(Bytes.FromHexString(hash)), next), head.Header).Should().Be(result);
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
        uint expectedForkHash = Bytes.ReadEthUInt32(Bytes.FromHexString(forkHashHex));

        ForkId forkId = new ForkInfo(specProvider, genesisHash).GetForkId(head, headTimestamp);
        uint forkHash = forkId.ForkHash;
        forkHash.Should().Be(expectedForkHash);

        forkId.Next.Should().Be(next);
        forkId.ForkHash.Should().Be(expectedForkHash);
    }
}
