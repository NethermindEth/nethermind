// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Reflection;
using System.Text;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
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
    [TestCase(15_050_000, 0ul, "0xf0afd0e3", 1_681_338_455ul, "First Gray Glacier")]
    [TestCase(15_051_000, 0ul, "0xf0afd0e3", 1_681_338_455ul, "Future Gray Glacier")]
    [TestCase(15_051_000, 1_681_338_455ul, "0xdce96c2d", 1_710_338_135ul, "First Shanghai timestamp")]
    [TestCase(15_051_000, 1_710_338_134ul, "0xdce96c2d", 1_710_338_135ul, "Future Shanghai timestamp")]
    [TestCase(15_051_000, 1_710_338_135ul, "0x9f3d2254", 1_746_612_311ul, "First Cancun timestamp")]
    [TestCase(15_051_000, 1_746_612_310ul, "0x9f3d2254", 1_746_612_311ul, "Future Cancun timestamp")]
    [TestCase(15_051_000, 1_746_612_311ul, "0xc376cf8b", 0ul, "First Prague timestamp")]
    [TestCase(15_051_000, 1_846_612_311ul, "0xc376cf8b", 0ul, "Future Prague timestamp")]
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
        Test(head, headTimestamp, KnownHashes.MainnetGenesis, forkHashHex, next, description, "TimestampForkIdTest.json",
            $"../../../../{Assembly.GetExecutingAssembly().GetName().Name}");
    }

    [TestCase(15_050_000, 0ul, "0xf0afd0e3", 21_000_000ul, "First Gray Glacier")]
    [TestCase(21_000_000, 0ul, "0x3f5fd195", 1681338455UL, "First Merge Fork Id test")]
    [TestCase(21_811_000, 0ul, "0x3f5fd195", 1681338455UL, "Future Merge Fork Id test")]
    public void Fork_id_and_hash_as_expected_with_merge_fork_id(long head, ulong headTimestamp, string forkHashHex, ulong next, string description)
    {
        var loader = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboTraceLogger.Instance);
        ChainSpec spec = loader.LoadEmbeddedOrFromFile("../../../../Chains/foundation.json");
        spec.Parameters.MergeForkIdTransition = 21_000_000L;
        spec.MergeForkIdBlockNumber = 21_000_000L;
        ChainSpecBasedSpecProvider provider = new ChainSpecBasedSpecProvider(spec);
        Test(head, headTimestamp, KnownHashes.MainnetGenesis, forkHashHex, next, description, provider);
    }

    [TestCase(0, 0ul, "0xc61a6098", 1_696_000_704ul, "Unsynced")]
    [TestCase(1, 1_696_000_703ul, "0xc61a6098", 1_696_000_704ul, "Last genesis spec block")]
    [TestCase(2, 1_696_000_704ul, "0xfd4f016b", 1_707_305_664ul, "First Shanghai block")]
    [TestCase(3, 1_707_305_663ul, "0xfd4f016b", 1_707_305_664ul, "Future Shanghai timestamp")]
    [TestCase(4, 1_707_305_664ul, "0x9b192ad0", 1_740_434_112ul, "First Cancun timestamp")]
    [TestCase(5, 1_717_305_664ul, "0x9b192ad0", 1_740_434_112ul, "Future Cancun timestamp")]
    [TestCase(5, 1_740_434_112ul, "0xdfbd9bed", 0ul, "First Prague timestamp")]
    [TestCase(5, 1_760_434_112ul, "0xdfbd9bed", 0ul, "Future Prague timestamp")]
    public void Fork_id_and_hash_as_expected_on_holesky(long head, ulong headTimestamp, string forkHashHex, ulong next, string description)
    {
        Test(head, headTimestamp, KnownHashes.HoleskyGenesis, forkHashHex, next, description, HoleskySpecProvider.Instance, "holesky.json");
    }

    [TestCase(0, 0ul, "0xFE3366E7", 1735371ul, "Sepolia genesis")]
    [TestCase(1735370, 0ul, "0xFE3366E7", 1_735_371ul, "Sepolia Last block before MergeForkIdTranstion")]
    [TestCase(1735371, 0ul, "0xb96cbd13", 1_677_557_088ul, "First block - Sepolia MergeForkIdTransition")]
    [TestCase(1735372, 1_677_557_088ul, "0xf7f9bc08", 1_706_655_072ul, "Shanghai")]
    [TestCase(1735372, 1_706_655_071ul, "0xf7f9bc08", 1_706_655_072ul, "Future Shanghai")]
    [TestCase(1735373, 1_706_655_072ul, "0x88cf81d9", 1_741_159_776ul, "First Cancun timestamp")]
    [TestCase(1735374, 1_716_655_072ul, "0x88cf81d9", 1_741_159_776ul, "Future Cancun timestamp")]
    [TestCase(1735373, 1_741_159_776ul, "0xed88b5fd", 0ul, "First Prague timestamp")]
    [TestCase(1735374, 1_761_159_776ul, "0xed88b5fd", 0ul, "Future Prague timestamp")]
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
    [TestCase(19040000, 0ul, "0x018479d3", GnosisSpecProvider.ShanghaiTimestamp, "First London block")]
    [TestCase(21735000, 0ul, "0x018479d3", GnosisSpecProvider.ShanghaiTimestamp, "First GIP-31 block")]
    [TestCase(31735000, GnosisSpecProvider.ShanghaiTimestamp, "0x2efe91ba", GnosisSpecProvider.CancunTimestamp, "First Shanghai timestamp")]
    [TestCase(41735000, GnosisSpecProvider.CancunTimestamp, "0x1384dfc1", GnosisSpecProvider.PragueTimestamp, "First Cancun timestamp")]
    [TestCase(91735000, GnosisSpecProvider.CancunTimestamp, "0x1384dfc1", GnosisSpecProvider.PragueTimestamp, "Future Cancun timestamp")]
    [TestCase(101735000, GnosisSpecProvider.PragueTimestamp, "0x2f095d4a", 0ul, "First Prague timestamp")]
    [TestCase(101735000, GnosisSpecProvider.PragueTimestamp, "0x2f095d4a", 0ul, "Future Prague timestamp")]
    public void Fork_id_and_hash_as_expected_on_gnosis(long head, ulong headTimestamp, string forkHashHex, ulong next, string description)
    {
        var loader = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboTraceLogger.Instance);
        ChainSpec spec = loader.LoadEmbeddedOrFromFile("../../../../Chains/gnosis.json");
        ChainSpecBasedSpecProvider provider = new ChainSpecBasedSpecProvider(spec);
        Test(head, headTimestamp, KnownHashes.GnosisGenesis, forkHashHex, next, description, provider);
    }

    [TestCase(0L, 0UL, "0x50d39d7b", ChiadoSpecProvider.ShanghaiTimestamp, "Chiado genesis")]
    [TestCase(3945317, ChiadoSpecProvider.ShanghaiTimestamp, "0xa15a4252", ChiadoSpecProvider.CancunTimestamp, "First Shanghai timestamp")]
    [TestCase(4_000_000, ChiadoSpecProvider.CancunTimestamp, "0x5fbc16bc", 1741254220ul, "First Cancun timestamp")]
    [TestCase(5_000_000, 1741254219u, "0x5fbc16bc", 1741254220ul, "Future Cancun timestamp")]
    [TestCase(5_000_000, 1741254220u, "0x8BA51786", 0ul, "First Prague timestamp")]
    [TestCase(5_000_000, 1741254420u, "0x8BA51786", 0ul, "Future Prague timestamp")]
    public void Fork_id_and_hash_as_expected_on_chiado(long head, ulong headTimestamp, string forkHashHex, ulong next, string description)
    {
        var loader = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboTraceLogger.Instance);
        ChainSpec spec = loader.LoadEmbeddedOrFromFile("../../../../Chains/chiado.json");
        ChainSpecBasedSpecProvider provider = new ChainSpecBasedSpecProvider(spec);
        Test(head, headTimestamp, KnownHashes.ChiadoGenesis, forkHashHex, next, description, provider);
    }

    [TestCase(0L, HoodiSpecProvider.CancunTimestamp, "0xBEF71D30", 1742999832UL, "First Cancun timestamp")]
    [TestCase(5_000_000, HoodiSpecProvider.PragueTimestamp - 1, "0xBEF71D30", 1742999832UL, "Future Cancun timestamp")]
    [TestCase(5_000_000, HoodiSpecProvider.PragueTimestamp, "0x929E24E", 0ul, "First Prague timestamp")]
    [TestCase(5_000_000, HoodiSpecProvider.PragueTimestamp + 100000, "0x929E24E", 0ul, "Future Prague timestamp")]
    public void Fork_id_and_hash_as_expected_on_hoodi(long head, ulong headTimestamp, string forkHashHex, ulong next, string description)
    {
        var loader = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboTraceLogger.Instance);
        ChainSpec spec = loader.LoadEmbeddedOrFromFile("../../../../Chains/hoodi.json");
        ChainSpecBasedSpecProvider provider = new ChainSpecBasedSpecProvider(spec);
        Test(head, headTimestamp, KnownHashes.HoodiGenesis, forkHashHex, next, description, provider);
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

    public void Test_fork_id_validation_mainnet(long headNumber, ulong headTimestamp, string hash, ulong next, ValidationResult result, bool UseTimestampSpec = false)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        Block head = Build.A.Block.WithNumber(headNumber).WithTimestamp(headTimestamp).TestObject;
        blockTree.Head.Returns(head);

        ISpecProvider specProvider = MainnetSpecProvider.Instance;
        if (UseTimestampSpec)
        {
            var loader = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboTraceLogger.Instance);
            ChainSpec spec = loader.LoadEmbeddedOrFromFile($"../../../../{Assembly.GetExecutingAssembly().GetName().Name}/TimestampForkIdTest.json");
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

        string chainspec = $"{{\"params\":{{\"networkID\":{specNetworkId?.ToString() ?? "null"},\"chainId\":{specChainId?.ToString() ?? "null"}}},\"engine\":{{\"NethDev\":{{}}}}}}";
        using MemoryStream memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(chainspec));
        ChainSpec spec = loader.Load(memoryStream);
        ChainSpecBasedSpecProvider provider = new(spec);

        spec.ChainId.Should().Be(expectedChainId);
        spec.NetworkId.Should().Be(expectedNetworkId);
        provider.ChainId.Should().Be(expectedChainId);
        provider.NetworkId.Should().Be(expectedNetworkId);
    }

    private static void Test(long head, ulong headTimestamp, Hash256 genesisHash, string forkHashHex, ulong next, string description, ISpecProvider specProvider, string chainSpec, string path = "../../../../Chains")
    {
        Test(head, headTimestamp, genesisHash, forkHashHex, next, description, specProvider);
        Test(head, headTimestamp, genesisHash, forkHashHex, next, description, chainSpec, path);
    }

    private static void Test(long head, ulong headTimestamp, Hash256 genesisHash, string forkHashHex, ulong next, string description, string chainSpec, string path = "../../../../Chains")
    {
        var loader = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboTraceLogger.Instance);
        ChainSpec spec = loader.LoadEmbeddedOrFromFile(Path.Combine(path, chainSpec));
        ChainSpecBasedSpecProvider provider = new ChainSpecBasedSpecProvider(spec);
        Test(head, headTimestamp, genesisHash, forkHashHex, next, description, provider);
    }

    private static void Test(long head, ulong headTimestamp, Hash256 genesisHash, string forkHashHex, ulong next, string description, ISpecProvider specProvider)
    {
        uint expectedForkHash = Bytes.ReadEthUInt32(Bytes.FromHexString(forkHashHex));

        ForkId forkId = new ForkInfo(specProvider, genesisHash).GetForkId(head, headTimestamp);
        uint forkHash = forkId.ForkHash;
        forkHash.Should().Be(expectedForkHash);

        forkId.Next.Should().Be(next);
        forkId.ForkHash.Should().Be(expectedForkHash);
    }
}
