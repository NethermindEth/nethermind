// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs;

public class MainnetSpecProvider : ForkScheduleSpecProvider
{
    public const ulong HomesteadBlockNumber = 1_150_000;
    public const ulong DaoForkBlockNumber = 1_920_000;
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
    public const ulong AmsterdamBlockTimestamp = ulong.MaxValue;

    public static ForkActivation ShanghaiActivation { get; } = (ParisBlockNumber + 1, ShanghaiBlockTimestamp);
    public static ForkActivation CancunActivation { get; } = (ParisBlockNumber + 2, CancunBlockTimestamp);
    public static ForkActivation PragueActivation { get; } = (ParisBlockNumber + 3, PragueBlockTimestamp);
    public static ForkActivation OsakaActivation { get; } = (ParisBlockNumber + 4, OsakaBlockTimestamp);
    public static ForkActivation BPO1Activation { get; } = (ParisBlockNumber + 5, BPO1BlockTimestamp);
    public static ForkActivation BPO2Activation { get; } = (ParisBlockNumber + 6, BPO2BlockTimestamp);
    public static ForkActivation AmsterdamActivation { get; } = (ParisBlockNumber + 7, AmsterdamBlockTimestamp);

    public MainnetSpecProvider() : this(new ForkSchedule
    {
        [GenesisBlockNumber] = Frontier.Instance,
        [HomesteadBlockNumber] = Homestead.Instance,
        [DaoForkBlockNumber] = Dao.Instance,
        [TangerineWhistleBlockNumber] = TangerineWhistle.Instance,
        [SpuriousDragonBlockNumber] = SpuriousDragon.Instance,
        [ByzantiumBlockNumber] = Byzantium.Instance,
        [ConstantinopleFixBlockNumber] = ConstantinopleFix.Instance,
        [IstanbulBlockNumber] = Istanbul.Instance,
        [MuirGlacierBlockNumber] = MuirGlacier.Instance,
        [BerlinBlockNumber] = Berlin.Instance,
        [LondonBlockNumber] = London.Instance,
        [ArrowGlacierBlockNumber] = ArrowGlacier.Instance,
        [GrayGlacierBlockNumber] = GrayGlacier.Instance,
        [ParisBlockNumber] = Paris.Instance,
        [ShanghaiBlockTimestamp] = Shanghai.Instance,
        [CancunBlockTimestamp] = Cancun.Instance,
        [PragueBlockTimestamp] = Prague.Instance,
        [OsakaBlockTimestamp] = Osaka.Instance,
        [BPO1BlockTimestamp] = BPO1.Instance,
        [BPO2BlockTimestamp] = BPO2.Instance,
        [AmsterdamBlockTimestamp] = Amsterdam.Instance,
    })
    { }

    private MainnetSpecProvider(ForkSchedule schedule) : base(schedule,
        terminalTotalDifficulty: new UInt256(15566869308787654656ul, 3184ul)) =>
        TransitionActivations = schedule.ToTransitionActivations(
            postMergeBlock: ParisBlockNumber + 1,
            excludeBlocks: [ParisBlockNumber]);

    public override ulong NetworkId => Core.BlockchainIds.Mainnet;
    public override ulong? DaoBlockNumber => DaoForkBlockNumber;
    public override ulong? BeaconChainGenesisTimestamp => BeaconChainGenesisTimestampConst;
    public override ulong TimestampFork => ShanghaiBlockTimestamp;

    public static MainnetSpecProvider Instance { get; } = new();
}
