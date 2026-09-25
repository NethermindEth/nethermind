// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Eez;

/// <summary>Serves a release spec as an <see cref="EezReleaseSpec"/> where it names no deposit contract, and unchanged otherwise.</summary>
public sealed class EezSpecProvider(ISpecProvider inner) : IForkAwareSpecProvider
{
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
    public IEnumerable<string> AvailableForks => inner is IForkAwareSpecProvider forkAware ? forkAware.AvailableForks : [];

    public IReleaseSpec GetSpec(ForkActivation forkActivation) => Wrap(inner.GetSpec(forkActivation));

    public bool TryGetForkSpec(string forkName, out IReleaseSpec? spec)
    {
        if (inner is IForkAwareSpecProvider forkAware && forkAware.TryGetForkSpec(forkName, out IReleaseSpec? innerSpec) && innerSpec is not null)
        {
            spec = Wrap(innerSpec);
            return true;
        }

        spec = null;
        return false;
    }

    private IReleaseSpec Wrap(IReleaseSpec spec) =>
        _wrapped.GetOrAdd(spec, static s => EezReleaseSpec.NeedsDepositContractDefault(s) ? new EezReleaseSpec(s) : s);
}
