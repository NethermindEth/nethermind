// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs;

public class SepoliaSpecProvider : ISpecProvider
{
    public const ulong BeaconChainGenesisTimestamp = 0x62b07d60;
    public const ulong ShanghaiBlockTimestamp = 0x63fd7d60;

    private SepoliaSpecProvider() { }

    public IReleaseSpec GetSpec(ForkActivation forkActivation) =>
        forkActivation switch
        {
            { Timestamp: null } or { Timestamp: < ShanghaiBlockTimestamp } => London.Instance,
            _ => Shanghai.Instance
        };

    public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
    {
        if (blockNumber is not null)
            MergeBlockNumber = (ForkActivation)blockNumber;
        if (terminalTotalDifficulty is not null)
            TerminalTotalDifficulty = terminalTotalDifficulty;
    }

    public ulong NetworkId => Core.BlockchainIds.Rinkeby;
    public ulong ChainId => NetworkId;
    public long? DaoBlockNumber => null;
    public ForkActivation? MergeBlockNumber { get; private set; } = null;
    public ulong TimestampFork => ISpecProvider.TimestampForkNever;
    public UInt256? TerminalTotalDifficulty { get; private set; } = 17000000000000000;
    public IReleaseSpec GenesisSpec => London.Instance;
    public ForkActivation[] TransitionActivations { get; } =
    {
        (ForkActivation)1735371,
        (1735371, 1677557088)
    };

    public static SepoliaSpecProvider Instance { get; } = new();
}
