// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Specs;

public abstract class ForkScheduleSpecProvider : ISpecProvider
{
    private readonly Lazy<ForkSpec[]> _schedule;
    private readonly Lazy<FrozenDictionary<string, IReleaseSpec>> _forks;
    private readonly Lazy<string[]> _availableForks;

    protected ForkSpec[] ForkSchedule => _schedule.Value;
    public FrozenDictionary<string, IReleaseSpec> Forks => _forks.Value;
    public IEnumerable<string> AvailableForks => _availableForks.Value;
    public bool TryGetForkSpec(string forkName, out IReleaseSpec? spec) => Forks.TryGetValue(forkName, out spec);

    protected ForkScheduleSpecProvider(ForkSpec[] schedule, UInt256? terminalTotalDifficulty = null, ForkActivation? mergeBlockNumber = null)
        : this(new Lazy<ForkSpec[]>(schedule), terminalTotalDifficulty, mergeBlockNumber) { }

    protected ForkScheduleSpecProvider(Func<ForkSpec[]> scheduleFactory, UInt256? terminalTotalDifficulty = null, ForkActivation? mergeBlockNumber = null)
        : this(new Lazy<ForkSpec[]>(scheduleFactory), terminalTotalDifficulty, mergeBlockNumber) { }

    private ForkScheduleSpecProvider(Lazy<ForkSpec[]> schedule, UInt256? terminalTotalDifficulty, ForkActivation? mergeBlockNumber)
    {
        _schedule = schedule;
        _forks = new(() => ForkSchedule.ToFrozenDictionary(static x => x.Spec.Name, static x => x.Spec, StringComparer.OrdinalIgnoreCase));
        _availableForks = new(() => [.. Forks.Keys.Order()]);
        TerminalTotalDifficulty = terminalTotalDifficulty;
        MergeBlockNumber = mergeBlockNumber;
    }

    public IReleaseSpec GetSpec(ForkActivation forkActivation)
    {
        if (forkActivation.Timestamp is ulong ts)
        {
            for (int i = ForkSchedule.Length - 1; i >= 0; i--)
            {
                if (ForkSchedule[i].Timestamp is ulong forkTs && ts >= forkTs)
                    return ForkSchedule[i].Spec;
            }
        }

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
    public UInt256? TerminalTotalDifficulty { get; private set; }
    public IReleaseSpec GenesisSpec => ForkSchedule[0].Spec;
    public virtual long? DaoBlockNumber => null;
    public virtual ulong ChainId => NetworkId;
    public ForkActivation[] TransitionActivations { get; protected set; } = [];

    public abstract ulong TimestampFork { get; }
    public abstract ulong NetworkId { get; }
    public abstract ulong? BeaconChainGenesisTimestamp { get; }
}
