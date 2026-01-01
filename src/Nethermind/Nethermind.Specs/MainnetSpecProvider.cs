// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs;

public class MainnetSpecProvider : ISpecProvider
{
    public const ulong HomesteadBlockNumber = 1_150_000;
    public const ulong DaoBlockNumberConst = 1_920_000;
    public const ulong TangerineWhistleBlockNumber = 2_463_000;
    public const ulong SpuriousDragonBlockNumber = 2_675_000;
    public const ulong ByzantiumBlockNumber = 4_370_000;
    public const ulong ConstantinopleFixBlockNumber = 7_280_000;
    public const ulong IstanbulBlockNumber = 9_069_000;
    public const ulong MuirGlacierBlockNumber = 9_200_000;
    public const ulong BerlinBlockNumber = 12_244_000;
    public const ulong LondonBlockNumber = 12_965_000;
    public const ulong ArrowGlacierBlockNumber = 13_773_000;
    public const ulong GrayGlacierBlockNumber = 15_050_000;
    public const ulong ParisBlockNumber = 15_537_393;
    public const ulong GenesisBlockTimestamp = 0x55ba4215;
    public const ulong BeaconChainGenesisTimestampConst = 0x5fc63057;
    public const ulong ShanghaiBlockTimestamp = 0x64373057;
    public const ulong CancunBlockTimestamp = 0x65F1B057;
    public const ulong PragueBlockTimestamp = 0x681b3057;
    public const ulong OsakaBlockTimestamp = 0x6930b057;
    public const ulong BPO1BlockTimestamp = 0x69383057;
    public const ulong BPO2BlockTimestamp = 0x695db057;

    IReleaseSpec ISpecProvider.GetSpecInternal(ForkActivation forkActivation) =>
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
            { Timestamp: < BPO1BlockTimestamp } => Osaka.Instance,
            { Timestamp: < BPO2BlockTimestamp } => BPO1.Instance,
            _ => BPO2.Instance
        };

    public void UpdateMergeTransitionInfo(ulong? blockNumber, UInt256? terminalTotalDifficulty = null)
    {
        if (blockNumber is not null)
            MergeBlockNumber = (ForkActivation)blockNumber.Value;

        if (terminalTotalDifficulty is not null)
            TerminalTotalDifficulty = terminalTotalDifficulty;
    }

    public ulong NetworkId => Core.BlockchainIds.Mainnet;
    public ulong ChainId => NetworkId;
    public ulong? DaoBlockNumber => DaoBlockNumberConst;
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
    };

    public static MainnetSpecProvider Instance { get; } = new();
}
