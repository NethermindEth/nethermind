// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Specs.GnosisForks;

namespace Nethermind.Specs;

public class GnosisSpecProvider : ForkScheduleSpecProvider, IForkAwareSpecProvider
{
    public const long ConstantinopleBlockNumber = 1_604_400;
    public const long ConstantinopleFixBlockNumber = 2_508_800;
    public const long IstanbulBlockNumber = 7_298_030;
    public const long PosdaoTransitionBlockNumber = 9_186_425; // does not alter EVM specs, fork-id boundary only
    public const long BerlinBlockNumber = 16_101_500;
    public const long LondonBlockNumber = 19_040_000;
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
        static () =>
        [
            new(0L, Byzantium.Instance),
            new(ConstantinopleBlockNumber, Constantinople.Instance),
            new(ConstantinopleFixBlockNumber, ConstantinopleFix.Instance),
            new(IstanbulBlockNumber, Istanbul.Instance),
            new(BerlinBlockNumber, Berlin.Instance),
            new(LondonBlockNumber, LondonGnosis.Instance),
            new(ShanghaiTimestamp, ShanghaiGnosis.Instance),
            new(CancunTimestamp, CancunGnosis.Instance),
            new(PragueTimestamp, PragueGnosis.Instance),
            new(OsakaTimestamp, OsakaGnosis.Instance),
        ],
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
    // 8626000000000000000000058750000000000000000000
    public override long? DaoBlockNumber => null;
    public string SealEngine => SealEngineType.AuRa;

    public static GnosisSpecProvider Instance { get; } = new();
}
