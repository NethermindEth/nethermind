// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs;

public class ChiadoSpecProvider : ISpecProvider
{
    public const ulong ShanghaiTimestamp = 0x646dff10UL;

    private ChiadoSpecProvider() { }

    public IReleaseSpec GetSpec(ForkActivation forkActivation) => forkActivation.BlockNumber switch
    {
        _ => forkActivation.Timestamp switch
        {
            null or < ShanghaiTimestamp => GenesisSpec,
            _ => Shanghai.Instance
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
    public ulong NetworkId => BlockchainIds.Chiado;
    public ulong ChainId => BlockchainIds.Chiado;
    public ForkActivation[] TransitionActivations { get; }

    public static ChiadoSpecProvider Instance { get; } = new();
}
