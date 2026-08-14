// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Specs.GnosisForks;

namespace Nethermind.Specs;

public class GnosisSpecProvider : ForkScheduleSpecProvider
{
    public const ulong ConstantinopleBlockNumber = 1_604_400;
    public const ulong ConstantinopleFixBlockNumber = 2_508_800;
    public const ulong IstanbulBlockNumber = 7_298_030;
    public const ulong PosdaoTransitionBlockNumber = 9_186_425; // does not alter EVM specs, fork-id boundary only
    public const ulong BerlinBlockNumber = 16_101_500;
    public const ulong LondonBlockNumber = 19_040_000;
    public const ulong BeaconChainGenesisTimestampConst = 0x61b10dbc;
    public const ulong ShanghaiTimestamp = 0x64c8edbc;
    public const ulong CancunTimestamp = 0x65ef4dbc;
    public const ulong PragueTimestamp = 0x68122dbc;
    public const ulong BalancerTimestamp = 0x69496dbc; // does not alter specs
    public const ulong OsakaTimestamp = 0x69de2dbc;
    public static readonly Address FeeCollector = new("0x6BBe78ee9e474842Dbd4AB4987b3CeFE88426A92");

    // Lazy because LondonGnosis.Apply → GnosisSpecProvider.FeeCollector → GnosisSpecProvider static
    // init → LondonGnosis.Instance (still building → null) is a circular init if Chiado or anything
    // else touches a Gnosis fork instance before GnosisSpecProvider is first directly accessed.
    private GnosisSpecProvider() : base(
        static () => new ForkSchedule
        {
            [GenesisBlockNumber] = Byzantium.Instance,
            [ConstantinopleBlockNumber] = Constantinople.Instance,
            [ConstantinopleFixBlockNumber] = ConstantinopleFix.Instance,
            [IstanbulBlockNumber] = Istanbul.Instance,
            [BerlinBlockNumber] = Berlin.Instance,
            [LondonBlockNumber] = LondonGnosis.Instance,
            [ShanghaiTimestamp] = ShanghaiGnosis.Instance,
            [CancunTimestamp] = CancunGnosis.Instance,
            [PragueTimestamp] = PragueGnosis.Instance,
            [OsakaTimestamp] = OsakaGnosis.Instance,
        },
        // 8626000000000000000000058750000000000000000000
        terminalTotalDifficulty: new UInt256(15847367919172845568ul, 12460455203863319017ul, 25349535ul)) =>
        TransitionActivations =
        [
            (ForkActivation)ConstantinopleBlockNumber,
            (ForkActivation)ConstantinopleFixBlockNumber,
            (ForkActivation)IstanbulBlockNumber,
            (ForkActivation)PosdaoTransitionBlockNumber,
            (ForkActivation)BerlinBlockNumber,
            (ForkActivation)LondonBlockNumber,
            (LondonBlockNumber, ShanghaiTimestamp),
            (LondonBlockNumber, CancunTimestamp),
            (LondonBlockNumber, PragueTimestamp),
            (LondonBlockNumber, BalancerTimestamp),
            (LondonBlockNumber, OsakaTimestamp),
        ];

    public override ulong TimestampFork => ShanghaiTimestamp;
    public override ulong NetworkId => BlockchainIds.Gnosis;
    public override ulong ChainId => BlockchainIds.Gnosis;
    public override ulong? BeaconChainGenesisTimestamp => BeaconChainGenesisTimestampConst;
    public override ulong? DaoBlockNumber => null;
    public string SealEngine => SealEngineType.AuRa;

    public static GnosisSpecProvider Instance { get; } = new();
}
