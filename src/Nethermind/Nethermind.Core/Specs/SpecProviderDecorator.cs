// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Core.Specs;

public class SpecProviderDecorator(ISpecProvider baseSpecProvider) : ISpecProvider
{
    public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null) => baseSpecProvider.UpdateMergeTransitionInfo(blockNumber, terminalTotalDifficulty);

    public ForkActivation? MergeBlockNumber => baseSpecProvider.MergeBlockNumber;

    public ulong TimestampFork => baseSpecProvider.TimestampFork;

    public UInt256? TerminalTotalDifficulty => baseSpecProvider.TerminalTotalDifficulty;

    public IReleaseSpec GenesisSpec => baseSpecProvider.GenesisSpec;

    public long? DaoBlockNumber => baseSpecProvider.DaoBlockNumber;

    public ulong? BeaconChainGenesisTimestamp => baseSpecProvider.BeaconChainGenesisTimestamp;

    public ulong NetworkId => baseSpecProvider.NetworkId;

    public ulong ChainId => baseSpecProvider.ChainId;

    public ForkActivation[] TransitionActivations => baseSpecProvider.TransitionActivations;

    public virtual IReleaseSpec GetSpecInternal(ForkActivation forkActivation) => baseSpecProvider.GetSpecInternal(forkActivation);
}
