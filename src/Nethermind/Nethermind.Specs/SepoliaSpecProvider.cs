// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs;

public class SepoliaSpecProvider : ISpecProvider
{
    public const ulong BeaconChainGenesisTimestamp = 0x62b07d60;
    public const ulong ShanghaiTimestamp = 0x63fd7d60;
    public const ulong CancunTimestamp = 0x65B97D60;

    private SepoliaSpecProvider() { }

    public IReleaseSpec GetSpec(ForkActivation forkActivation) =>
        forkActivation switch
        {
            { Timestamp: null } or { Timestamp: < ShanghaiTimestamp } => London.Instance,
            { Timestamp: < CancunTimestamp } => Shanghai.Instance,
            _ => Cancun.Instance
        };

    public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
    {
        if (blockNumber is not null)
            MergeBlockNumber = (ForkActivation)blockNumber;
        if (terminalTotalDifficulty is not null)
            TerminalTotalDifficulty = terminalTotalDifficulty;
    }

    public ulong NetworkId => Core.BlockchainIds.Sepolia;
    public ulong ChainId => NetworkId;
    public long? DaoBlockNumber => null;
    public ForkActivation? MergeBlockNumber { get; private set; } = null;
    public ulong TimestampFork => ISpecProvider.TimestampForkNever;
    public UInt256? TerminalTotalDifficulty { get; private set; } = 17000000000000000;
    public IReleaseSpec GenesisSpec => London.Instance;
    public ForkActivation[] TransitionActivations { get; } =
    {
        (ForkActivation)1735371,
        (1735371, ShanghaiTimestamp),
        (1735371, CancunTimestamp),
    };

    public static SepoliaSpecProvider Instance { get; } = new();
}
