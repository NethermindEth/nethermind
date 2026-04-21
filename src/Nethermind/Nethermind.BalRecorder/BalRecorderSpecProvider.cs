// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.BalRecorder;

public class BalRecorderSpecProvider(ISpecProvider inner, BalRecorderSpecSwitch balSwitch) : ISpecProvider
{
    public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null) =>
        inner.UpdateMergeTransitionInfo(blockNumber, terminalTotalDifficulty);

    public ForkActivation? MergeBlockNumber => inner.MergeBlockNumber;
    public ulong TimestampFork => inner.TimestampFork;
    public UInt256? TerminalTotalDifficulty => inner.TerminalTotalDifficulty;
    public IReleaseSpec GenesisSpec => new BalRecorderReleaseSpec(inner.GenesisSpec, balSwitch);
    public bool GenesisStateUnavailable => inner.GenesisStateUnavailable;
    public long? DaoBlockNumber => inner.DaoBlockNumber;
    public ulong? BeaconChainGenesisTimestamp => inner.BeaconChainGenesisTimestamp;
    public ulong NetworkId => inner.NetworkId;
    public ulong ChainId => inner.ChainId;
    public string SealEngine => inner.SealEngine;
    public ForkActivation[] TransitionActivations => inner.TransitionActivations;

    public IReleaseSpec GetSpec(ForkActivation forkActivation) =>
        new BalRecorderReleaseSpec(inner.GetSpec(forkActivation), balSwitch);
}
