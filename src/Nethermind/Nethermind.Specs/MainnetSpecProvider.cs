// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs;

public class MainnetSpecProvider : ISpecProvider
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
    public const ulong OsakaBlockTimestamp = ulong.MaxValue - 1;

    public IReleaseSpec GetSpec(ForkActivation forkActivation) =>
        forkActivation switch
        {
            { BlockNumber: < HomesteadBlockNumber } => Frontier.Instance,
            { BlockNumber: < DaoBlockNumberConst } => Homestead.Instance,
            { BlockNumber: < TangerineWhistleBlockNumber } => Dao.Instance,
            { BlockNumber: < SpuriousDragonBlockNumber } => TangerineWhistle.Instance,
            { BlockNumber: < ByzantiumBlockNumber } => SpuriousDragon.Instance,
            { BlockNumber: < ConstantinopleFixBlockNumber } => Byzantium.Instance,
            { BlockNumber: < IstanbulBlockNumber } => ConstantinopleFix.Instance,
            { BlockNumber: < MuirGlacierBlockNumber } => Istanbul.Instance,
            { BlockNumber: < BerlinBlockNumber } => MuirGlacier.Instance,
            { BlockNumber: < LondonBlockNumber } => Berlin.Instance,
            { BlockNumber: < ArrowGlacierBlockNumber } => London.Instance,
            { BlockNumber: < GrayGlacierBlockNumber } => ArrowGlacier.Instance,
            { BlockNumber: < ParisBlockNumber } => GrayGlacier.Instance,
            { Timestamp: null } or { Timestamp: < ShanghaiBlockTimestamp } => Paris.Instance,
            { Timestamp: < CancunBlockTimestamp } => Shanghai.Instance,
            { Timestamp: < PragueBlockTimestamp } => Cancun.Instance,
            { Timestamp: < OsakaBlockTimestamp } => Prague.Instance,
            _ => Osaka.Instance
        };

    public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
    {
        if (blockNumber is not null)
            MergeBlockNumber = (ForkActivation)blockNumber;

        if (terminalTotalDifficulty is not null)
            TerminalTotalDifficulty = terminalTotalDifficulty;
    }

    public ulong NetworkId { get; } = Core.BlockchainIds.Mainnet;
    public ulong ChainId => NetworkId;
    public long? DaoBlockNumber => DaoBlockNumberConst;
    public ulong? BeaconChainGenesisTimestamp => BeaconChainGenesisTimestampConst;
    public ForkActivation? MergeBlockNumber { get; private set; } = null;
    public ulong TimestampFork { get; } = ShanghaiBlockTimestamp;
    public UInt256? TerminalTotalDifficulty { get; private set; } = UInt256.Parse("58750000000000000000000");
    public IReleaseSpec GenesisSpec => Frontier.Instance;
    public static ForkActivation ShanghaiActivation { get; } = (ParisBlockNumber + 1, ShanghaiBlockTimestamp);
    public static ForkActivation CancunActivation { get; } = (ParisBlockNumber + 2, CancunBlockTimestamp);
    public static ForkActivation PragueActivation { get; } = (ParisBlockNumber + 3, PragueBlockTimestamp);
    public static ForkActivation OsakaActivation { get; } = (ParisBlockNumber + 4, OsakaBlockTimestamp);
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
    };

    public static MainnetSpecProvider Instance { get; } = new();
}
