// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Specs.GnosisForks;

namespace Nethermind.Specs;

public class ChiadoSpecProvider : ForkScheduleSpecProvider
{
    public const ulong BeaconChainGenesisTimestampConst = 0x6343ee4c;
    public const ulong ShanghaiTimestamp = 0x646e0e4c;
    public const ulong CancunTimestamp = 0x65ba8e4c;
    public const ulong PragueTimestamp = 0x67c96e4c;
    public const ulong OsakaTimestamp = 0x69b7ce4c;

    public static readonly Address FeeCollector = new("0x1559000000000000000000000000000000000000");

    private ChiadoSpecProvider() : this(new ForkSchedule
    {
        [GenesisBlockNumber] = London.Instance,
        [ShanghaiTimestamp] = ShanghaiGnosis.Instance,
        [CancunTimestamp] = CancunGnosis.Instance,
        [PragueTimestamp] = PragueGnosis.Instance,
        [OsakaTimestamp] = OsakaGnosis.Instance,
    })
    { }

    private ChiadoSpecProvider(ForkSchedule schedule) : base(schedule,
        // 231707791542740786049188744689299064356246512
        terminalTotalDifficulty: new UInt256(18446744073375486960ul, 18446744073709551615ul, 680927ul)) =>
        TransitionActivations = schedule.ToTransitionActivations(
            postMergeBlock: GenesisBlockNumber,
            incrementBlockPerTimestampFork: false);

    public override ulong TimestampFork => ShanghaiTimestamp;
    public override ulong NetworkId => BlockchainIds.Chiado;
    public override ulong ChainId => BlockchainIds.Chiado;
    public override ulong? BeaconChainGenesisTimestamp => BeaconChainGenesisTimestampConst;
    public string SealEngine => SealEngineType.AuRa;

    public static ChiadoSpecProvider Instance { get; } = new();
}
