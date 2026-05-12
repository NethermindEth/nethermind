// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Specs.GnosisForks;

namespace Nethermind.Specs;

public class ChiadoSpecProvider : ISpecProvider
{
    public const ulong BeaconChainGenesisTimestampConst = 0x6343ee4c;
    public const ulong ShanghaiTimestamp = 0x646e0e4c;
    public const ulong CancunTimestamp = 0x65ba8e4c;
    public const ulong PragueTimestamp = 0x67c96e4c;
    public const ulong OsakaTimestamp = 0x69b7ce4c;

    public static readonly Address FeeCollector = new("0x1559000000000000000000000000000000000000");

    private ChiadoSpecProvider() { }

    private static readonly ForkSpec[] ForkSchedule =
    [
        new(0ul, London.Instance),
        new(ShanghaiTimestamp, ShanghaiGnosis.Instance),
        new(CancunTimestamp, CancunGnosis.Instance),
        new(PragueTimestamp, PragueGnosis.Instance),
        new(OsakaTimestamp, OsakaGnosis.Instance),
    ];

    public static readonly FrozenDictionary<string, IReleaseSpec> Forks = ForkSchedule.ToFrozenDictionary(static x => x.Spec.Name, static x => x.Spec, StringComparer.OrdinalIgnoreCase);

    public IReleaseSpec GetSpec(ForkActivation forkActivation)
    {
        ulong timestamp = forkActivation.Timestamp ?? 0;

        for (int i = ForkSchedule.Length - 1; i >= 0; i--)
        {
            if (timestamp >= ForkSchedule[i].Timestamp)
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

    public ForkActivation? MergeBlockNumber { get; private set; }
    public ulong TimestampFork => ShanghaiTimestamp;

    // 231707791542740786049188744689299064356246512
    public UInt256? TerminalTotalDifficulty { get; private set; } = new UInt256(18446744073375486960ul, 18446744073709551615ul, 680927ul);
    public IReleaseSpec GenesisSpec => London.Instance;
    public long? DaoBlockNumber => null;
    public ulong? BeaconChainGenesisTimestamp => BeaconChainGenesisTimestampConst;
    public ulong NetworkId => BlockchainIds.Chiado;
    public ulong ChainId => BlockchainIds.Chiado;
    public string SealEngine => SealEngineType.AuRa;
    public ForkActivation[] TransitionActivations { get; } =
    [
        (0, ShanghaiTimestamp),
        (0, CancunTimestamp),
        (0, PragueTimestamp),
        (0, OsakaTimestamp),
    ];

    public static ChiadoSpecProvider Instance { get; } = new();
}
