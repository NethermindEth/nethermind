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
        IReleaseSpec result = ForkSchedule[0].Spec;

        for (int i = 0; i < ForkSchedule.Length; i++)
        {
            ForkSpec fork = ForkSchedule[i];
            if (fork.Block is long block)
            {
                if (forkActivation.BlockNumber >= block)
                    result = fork.Spec;
                else
                    break;
            }
            else if (fork.Timestamp is ulong forkTs && forkActivation.Timestamp is ulong ts && ts >= forkTs)
            {
                result = fork.Spec;
            }
        }

        return result;
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
