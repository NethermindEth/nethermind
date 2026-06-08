// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Stateless.Execution.IO;

namespace Nethermind.Stateless.Execution;

/// <remarks>
/// Stateless fixtures can pin a named fork independently of the base chain's transition schedule.
/// For activations at or after the supplied active fork, <see cref="GetSpec(ForkActivation)"/> returns
/// the pinned release spec; earlier activations continue to use the base provider.
/// Merge transition metadata (<see cref="MergeBlockNumber"/>, <see cref="TerminalTotalDifficulty"/>)
/// remains delegated to the base provider, so it can describe the underlying chain schedule rather than
/// the pinned stateless fork.
/// </remarks>
internal sealed class StatelessSpecProvider(
    IForkAwareSpecProvider baseProvider,
    ForkActivation activeForkActivation,
    IReleaseSpec activeForkSpec)
    : ISpecProvider
{
    public ForkActivation? MergeBlockNumber => baseProvider.MergeBlockNumber;

    public ulong TimestampFork => baseProvider.TimestampFork;

    public UInt256? TerminalTotalDifficulty => baseProvider.TerminalTotalDifficulty;

    public IReleaseSpec GenesisSpec => baseProvider.GenesisSpec;

    public long? DaoBlockNumber => baseProvider.DaoBlockNumber;

    public ulong? BeaconChainGenesisTimestamp => baseProvider.BeaconChainGenesisTimestamp;

    public ulong NetworkId => baseProvider.NetworkId;

    public ulong ChainId => baseProvider.ChainId;

    public string SealEngine => baseProvider.SealEngine;

    public ForkActivation[] TransitionActivations => baseProvider.TransitionActivations;

    public IReleaseSpec GetSpec(ForkActivation activation) =>
        activation >= activeForkActivation ? activeForkSpec : baseProvider.GetSpec(activation);

    public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null) =>
        baseProvider.UpdateMergeTransitionInfo(blockNumber, terminalTotalDifficulty);

    public static StatelessSpecProvider Create(IForkAwareSpecProvider baseProvider, ForkConfig forkConfig)
    {
        string? forkName = ForkIndexHelper.GetForkNameByIndex(forkConfig.Fork);

        if (forkName is null || !baseProvider.TryGetForkSpec(forkName, out IReleaseSpec? spec))
            throw new ArgumentException($"Unknown fork: {forkConfig.Fork}", nameof(forkConfig));

        spec = forkConfig.BlobSchedule is [{ } blobSchedule]
           ? new StatelessReleaseSpec(spec!, blobSchedule)
           : spec;

        return new(baseProvider, forkConfig.Activation.ToForkActivation(), spec!);
    }

    private sealed class StatelessReleaseSpec(IReleaseSpec spec, BlobSchedule blobSchedule) : ReleaseSpecDecorator(spec)
    {
        public override ulong TargetBlobCount => blobSchedule.Target;
        public override ulong MaxBlobCount => blobSchedule.Max;
        public override UInt256 BlobBaseFeeUpdateFraction => new(blobSchedule.BaseFeeUpdateFraction);
    }
}
