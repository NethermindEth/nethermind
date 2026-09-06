// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.BalRecorder;

public class BalRecorderSpecProvider(ISpecProvider inner, BalRecorderSpecSwitch balSwitch) : ISpecProvider
{
    // Inner spec provider returns singletons per fork; cache our wrapper by reference so
    // repeated GetSpec / GenesisSpec calls don't allocate a new decorator each time.
    private readonly ConcurrentDictionary<IReleaseSpec, IReleaseSpec> _wrapped = new(ReferenceEqualityComparer.Instance);

    public void UpdateMergeTransitionInfo(ulong? blockNumber, UInt256? terminalTotalDifficulty = null) =>
        inner.UpdateMergeTransitionInfo(blockNumber, terminalTotalDifficulty);

    public ForkActivation? MergeBlockNumber => inner.MergeBlockNumber;
    public ulong TimestampFork => inner.TimestampFork;
    public UInt256? TerminalTotalDifficulty => inner.TerminalTotalDifficulty;
    public IReleaseSpec GenesisSpec => Wrap(inner.GenesisSpec);
    public bool GenesisStateUnavailable => inner.GenesisStateUnavailable;
    public ulong? DaoBlockNumber => inner.DaoBlockNumber;
    public ulong? BeaconChainGenesisTimestamp => inner.BeaconChainGenesisTimestamp;
    public ulong NetworkId => inner.NetworkId;
    public ulong ChainId => inner.ChainId;
    public string SealEngine => inner.SealEngine;
    public ForkActivation[] TransitionActivations => inner.TransitionActivations;

    public IReleaseSpec GetSpec(ForkActivation forkActivation) => Wrap(inner.GetSpec(forkActivation));

    private IReleaseSpec Wrap(IReleaseSpec spec) =>
        _wrapped.GetOrAdd(spec, s => new BalRecorderReleaseSpec(s, balSwitch));
}
