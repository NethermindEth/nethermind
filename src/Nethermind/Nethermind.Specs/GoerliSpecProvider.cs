// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs;

public class GoerliSpecProvider : ISpecProvider
{
    public const long IstanbulBlockNumber = 1_561_651;
    public const long BerlinBlockNumber = 4_460_644;
    public const long LondonBlockNumber = 5_062_605;
    public const ulong BeaconChainGenesisTimestamp = 0x6059f460;
    public const ulong ShanghaiTimestamp = 0x6410f460;

    private GoerliSpecProvider() { }

    public IReleaseSpec GetSpec(ForkActivation forkActivation)
    {
        return forkActivation.BlockNumber switch
        {
            < IstanbulBlockNumber => GenesisSpec,
            < BerlinBlockNumber => Istanbul.Instance,
            < LondonBlockNumber => Berlin.Instance,
            _ => forkActivation.Timestamp switch
            {
                null or < ShanghaiTimestamp => London.Instance,
                _ => Shanghai.Instance
            }
        };
    }

    public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
    {
        if (blockNumber is not null)
            MergeBlockNumber = (ForkActivation)blockNumber;
        if (terminalTotalDifficulty is not null)
            TerminalTotalDifficulty = terminalTotalDifficulty;
    }

    public ulong NetworkId => BlockchainIds.Goerli;
    public ulong ChainId => NetworkId;
    public long? DaoBlockNumber => null;
    public ForkActivation? MergeBlockNumber { get; private set; } = null;
    public ulong TimestampFork => ShanghaiTimestamp;
    public UInt256? TerminalTotalDifficulty { get; private set; } = 10790000;
    public IReleaseSpec GenesisSpec { get; } = ConstantinopleFix.Instance;
    public ForkActivation[] TransitionActivations { get; } =
    {
        (ForkActivation)IstanbulBlockNumber,
        (ForkActivation)BerlinBlockNumber,
        (ForkActivation)LondonBlockNumber,
        (LondonBlockNumber, ShanghaiTimestamp)
    };

    public static readonly GoerliSpecProvider Instance = new();
}
