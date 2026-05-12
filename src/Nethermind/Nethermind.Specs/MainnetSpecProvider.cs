// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs;

public class MainnetSpecProvider : IForkAwareSpecProvider
{
    public const long HomesteadBlockNumber = 1_150_000;
    public const long DaoBlockNumberConst = 1_920_000;
    public const long TangerineWhistleBlockNumber = 2_463_000;
    public const long SpuriousDragonBlockNumber = 2_675_000;
    public const long ByzantiumBlockNumber = 4_370_000;
    public const long ConstantinopleFixBlockNumber = 7_280_000;
    public const long IstanbulBlockNumber = 9_069_000;
    public const long MuirGlacierBlockNumber = 9_200_000;
    public const long BerlinBlockNumber = 12_244_000;
    public const long LondonBlockNumber = 12_965_000;
    public const long ArrowGlacierBlockNumber = 13_773_000;
    public const long GrayGlacierBlockNumber = 15_050_000;
    public const long ParisBlockNumber = 15_537_393;
    public const ulong GenesisBlockTimestamp = 0x55ba4215;
    public const ulong BeaconChainGenesisTimestampConst = 0x5fc63057;
    public const ulong ShanghaiBlockTimestamp = 0x64373057;
    public const ulong CancunBlockTimestamp = 0x65F1B057;
    public const ulong PragueBlockTimestamp = 0x681b3057;
    public const ulong OsakaBlockTimestamp = 0x6930b057;
    public const ulong BPO1BlockTimestamp = 0x69383057;
    public const ulong BPO2BlockTimestamp = 0x695db057;
    public const ulong BPO3BlockTimestamp = ulong.MaxValue - 3;
    public const ulong BPO4BlockTimestamp = ulong.MaxValue - 2;
    public const ulong BPO5BlockTimestamp = ulong.MaxValue - 1;
    public const ulong AmsterdamBlockTimestamp = ulong.MaxValue;

    private static readonly ForkSpec[] ForkSchedule =
    [
        new(0L, Frontier.Instance),
        new(HomesteadBlockNumber, Homestead.Instance),
        new(DaoBlockNumberConst, Dao.Instance),
        new(TangerineWhistleBlockNumber, TangerineWhistle.Instance),
        new(SpuriousDragonBlockNumber, SpuriousDragon.Instance),
        new(ByzantiumBlockNumber, Byzantium.Instance),
        new(ConstantinopleFixBlockNumber, ConstantinopleFix.Instance),
        new(IstanbulBlockNumber, Istanbul.Instance),
        new(MuirGlacierBlockNumber, MuirGlacier.Instance),
        new(BerlinBlockNumber, Berlin.Instance),
        new(LondonBlockNumber, London.Instance),
        new(ArrowGlacierBlockNumber, ArrowGlacier.Instance),
        new(GrayGlacierBlockNumber, GrayGlacier.Instance),
        new(ParisBlockNumber, Paris.Instance),
        new(ShanghaiBlockTimestamp, Shanghai.Instance),
        new(CancunBlockTimestamp, Cancun.Instance),
        new(PragueBlockTimestamp, Prague.Instance),
        new(OsakaBlockTimestamp, Osaka.Instance),
        new(BPO1BlockTimestamp, BPO1.Instance),
        new(BPO2BlockTimestamp, BPO2.Instance),
        new(BPO3BlockTimestamp, BPO3.Instance),
        new(BPO4BlockTimestamp, BPO4.Instance),
        new(BPO5BlockTimestamp, BPO5.Instance),
        new(AmsterdamBlockTimestamp, Amsterdam.Instance),
    ];

    public IReleaseSpec GetSpec(ForkActivation forkActivation)
    {
        if (forkActivation.Timestamp is ulong ts)
        {
            for (int i = ForkSchedule.Length - 1; i >= 0; i--)
            {
                if (ForkSchedule[i].Timestamp is ulong forkTs && ts >= forkTs)
                    return ForkSchedule[i].Spec;
            }
        }

        for (int i = ForkSchedule.Length - 1; i >= 0; i--)
        {
            if (ForkSchedule[i].Block is long forkBlock && forkActivation.BlockNumber >= forkBlock)
                return ForkSchedule[i].Spec;
        }

        return ForkSchedule[0].Spec;
    }

    public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
    {
        if (blockNumber is not null)
            MergeBlockNumber = (ForkActivation)blockNumber;

        if (terminalTotalDifficulty is not null)
            TerminalTotalDifficulty = terminalTotalDifficulty;
    }

    public ulong NetworkId => Core.BlockchainIds.Mainnet;
    public ulong ChainId => NetworkId;
    public long? DaoBlockNumber => DaoBlockNumberConst;
    public ulong? BeaconChainGenesisTimestamp => BeaconChainGenesisTimestampConst;
    public ForkActivation? MergeBlockNumber { get; private set; } = null;
    public ulong TimestampFork => ShanghaiBlockTimestamp;
    // 58750000000000000000000
    public UInt256? TerminalTotalDifficulty { get; private set; } = new UInt256(15566869308787654656ul, 3184ul);
    public IReleaseSpec GenesisSpec => Frontier.Instance;
    public static ForkActivation ShanghaiActivation { get; } = (ParisBlockNumber + 1, ShanghaiBlockTimestamp);
    public static ForkActivation CancunActivation { get; } = (ParisBlockNumber + 2, CancunBlockTimestamp);
    public static ForkActivation PragueActivation { get; } = (ParisBlockNumber + 3, PragueBlockTimestamp);
    public static ForkActivation OsakaActivation { get; } = (ParisBlockNumber + 4, OsakaBlockTimestamp);
    public static ForkActivation BPO1Activation { get; } = (ParisBlockNumber + 5, BPO1BlockTimestamp);
    public static ForkActivation BPO2Activation { get; } = (ParisBlockNumber + 6, BPO2BlockTimestamp);
    public static ForkActivation BPO3Activation { get; } = (ParisBlockNumber + 7, BPO3BlockTimestamp);
    public static ForkActivation BPO4Activation { get; } = (ParisBlockNumber + 8, BPO4BlockTimestamp);
    public static ForkActivation BPO5Activation { get; } = (ParisBlockNumber + 9, BPO5BlockTimestamp);
    public static ForkActivation AmsterdamActivation { get; } = (ParisBlockNumber + 10, AmsterdamBlockTimestamp);
    public static readonly FrozenDictionary<string, IReleaseSpec> Forks =
        ForkSchedule.ToFrozenDictionary(static x => x.Spec.Name, static x => x.Spec, StringComparer.OrdinalIgnoreCase);

    private static readonly string[] _availableForks = [.. Forks.Keys.Order()];

    public IEnumerable<string> AvailableForks => _availableForks;
    public bool TryGetForkSpec(string forkName, out IReleaseSpec? spec) => Forks.TryGetValue(forkName, out spec);

    public ForkActivation[] TransitionActivations { get; } =
    {
        (ForkActivation)HomesteadBlockNumber,
        (ForkActivation)DaoBlockNumberConst,
        (ForkActivation)TangerineWhistleBlockNumber,
        (ForkActivation)SpuriousDragonBlockNumber,
        (ForkActivation)ByzantiumBlockNumber,
        (ForkActivation)ConstantinopleFixBlockNumber,
        (ForkActivation)IstanbulBlockNumber,
        (ForkActivation)MuirGlacierBlockNumber,
        (ForkActivation)BerlinBlockNumber,
        (ForkActivation)LondonBlockNumber,
        (ForkActivation)ArrowGlacierBlockNumber,
        (ForkActivation)GrayGlacierBlockNumber,
        ShanghaiActivation,
        CancunActivation,
        PragueActivation,
        OsakaActivation,
        BPO1Activation,
        BPO2Activation,
        AmsterdamActivation
    };

    public static MainnetSpecProvider Instance { get; } = new();
}
