// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    public const ulong PragueTimestamp = 0x67C96E4C;
    public const ulong OsakaTimestamp = 0x9999FACE;

    public static readonly Address FeeCollector = new("0x1559000000000000000000000000000000000000");

    private ChiadoSpecProvider() { }

    IReleaseSpec ISpecProvider.GetSpecInternal(ForkActivation forkActivation) => forkActivation.BlockNumber switch
    {
        _ => forkActivation.Timestamp switch
        {
            null or < ShanghaiTimestamp => GenesisSpec,
            < CancunTimestamp => ShanghaiGnosis.Instance,
            < PragueTimestamp => CancunGnosis.Instance,
            < OsakaTimestamp => PragueGnosis.Instance,
            _ => OsakaGnosis.Instance
        }
    };

    public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
    {
        if (blockNumber is not null)
            MergeBlockNumber = (ForkActivation)blockNumber;

        if (terminalTotalDifficulty is not null)
            TerminalTotalDifficulty = terminalTotalDifficulty;
    }

    public ForkActivation? MergeBlockNumber { get; private set; }
    public ulong TimestampFork => ShanghaiTimestamp;
    public UInt256? TerminalTotalDifficulty { get; private set; } = UInt256.Parse("231707791542740786049188744689299064356246512");
    public IReleaseSpec GenesisSpec => London.Instance;
    public long? DaoBlockNumber => null;
    public ulong? BeaconChainGenesisTimestamp => BeaconChainGenesisTimestampConst;
    public ulong NetworkId => BlockchainIds.Chiado;
    public ulong ChainId => BlockchainIds.Chiado;
    public string SealEngine => SealEngineType.AuRa;
    public ForkActivation[] TransitionActivations { get; }

    public static ChiadoSpecProvider Instance { get; } = new();
}
