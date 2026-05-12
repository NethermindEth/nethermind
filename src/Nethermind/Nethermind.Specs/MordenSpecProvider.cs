// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs;

public class MordenSpecProvider : ISpecProvider
{
    public const long HomesteadBlockNumber = 494_000;
    public const long SpuriousDragonBlockNumber = 1_885_000;

    private static readonly ForkSpec[] ForkSchedule =
    [
        new(0L, Frontier.Instance),
        new(HomesteadBlockNumber, Homestead.Instance),
        new(SpuriousDragonBlockNumber, SpuriousDragon.Instance),
    ];

    public static readonly FrozenDictionary<string, IReleaseSpec> Forks =
        ForkSchedule.ToFrozenDictionary(static x => x.Spec.Name, static x => x.Spec, StringComparer.OrdinalIgnoreCase);

    public IReleaseSpec GetSpec(ForkActivation forkActivation)
    {
        for (int i = ForkSchedule.Length - 1; i >= 0; i--)
        {
            if (ForkSchedule[i].Block is long forkBlock && forkActivation.BlockNumber >= forkBlock)
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
    public ulong TimestampFork => ISpecProvider.TimestampForkNever;
    public UInt256? TerminalTotalDifficulty { get; private set; }
    public IReleaseSpec GenesisSpec => Frontier.Instance;
    public long? DaoBlockNumber => null;
    public ulong? BeaconChainGenesisTimestamp => null;
    public ulong NetworkId => BlockchainIds.Morden;
    public ulong ChainId => NetworkId;
    public ForkActivation[] TransitionActivations { get; } =
    [
        (ForkActivation)HomesteadBlockNumber,
        (ForkActivation)SpuriousDragonBlockNumber,
    ];
}
