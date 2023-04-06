// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs;

public class ChiadoSpecProvider : ISpecProvider
{
    public static readonly ChiadoSpecProvider Instance = new();
    private ChiadoSpecProvider() { }

    private ForkActivation? _theMergeBlock = null;
    private UInt256? _terminalTotalDifficulty = UInt256.Parse("231707791542740786049188744689299064356246512");

    public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
    {
        if (blockNumber is not null)
            _theMergeBlock = (ForkActivation)blockNumber;
        if (terminalTotalDifficulty is not null)
            _terminalTotalDifficulty = terminalTotalDifficulty;
    }

    public ForkActivation? MergeBlockNumber => _theMergeBlock;
    public ulong TimestampFork => ShanghaiTimestamp;
    public UInt256? TerminalTotalDifficulty => _terminalTotalDifficulty;
    public IReleaseSpec GenesisSpec => London.Instance;
    public long? DaoBlockNumber => null;
    public ulong NetworkId => BlockchainIds.Chiado;
    public ulong ChainId => BlockchainIds.Chiado;
    public ForkActivation[] TransitionActivations { get; }
    public IReleaseSpec GetSpec(ForkActivation forkActivation)
    {
        return forkActivation.BlockNumber switch
        {
            _ => forkActivation.Timestamp switch
            {
                null or < ShanghaiTimestamp => GenesisSpec,
                _ => Shanghai.Instance
            }
        };
    }

    public const ulong ShanghaiTimestamp = 1678832736;
}
