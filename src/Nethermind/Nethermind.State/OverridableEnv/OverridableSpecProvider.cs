// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.State.OverridableEnv;

/// <summary>
/// Wraps an <see cref="ISpecProvider"/> and allows a temporary per-call spec override.
/// Safe to use when one instance is only accessed from one request at a time (e.g., a bounded module pool).
/// </summary>
public class OverridableSpecProvider(ISpecProvider inner) : ISpecProvider
{
    // Volatile so cross-thread reads observe SetOverride/ResetOverride writes without
    // relying solely on the architectural "one request at a time" invariant.
    private volatile IReleaseSpec? _override;

    public ISpecProvider SpecProvider => inner;

    public IReleaseSpec GetSpec(ForkActivation forkActivation) => _override ?? inner.GetSpec(forkActivation);

    internal void SetOverride(IReleaseSpec spec) => _override = spec;
    internal void ResetOverride() => _override = null;

    public void UpdateMergeTransitionInfo(ulong? blockNumber, UInt256? terminalTotalDifficulty = null)
        => inner.UpdateMergeTransitionInfo(blockNumber, terminalTotalDifficulty);

    public ForkActivation? MergeBlockNumber => inner.MergeBlockNumber;
    public ulong TimestampFork => inner.TimestampFork;
    public UInt256? TerminalTotalDifficulty => inner.TerminalTotalDifficulty;
    public IReleaseSpec GenesisSpec => inner.GenesisSpec;
    public ulong? DaoBlockNumber => inner.DaoBlockNumber;
    public ulong? BeaconChainGenesisTimestamp => inner.BeaconChainGenesisTimestamp;
    public ulong NetworkId => inner.NetworkId;
    public ulong ChainId => inner.ChainId;
    public string SealEngine => inner.SealEngine;
    public ForkActivation[] TransitionActivations => inner.TransitionActivations;
}
