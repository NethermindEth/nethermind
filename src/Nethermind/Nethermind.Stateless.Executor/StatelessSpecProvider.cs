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
/// Chain id is supplied externally, so any compatible base schedule (e.g. Mainnet rules) can serve
/// as a devnet's fork catalog without misreporting the chain id to EIP-155 validation.
/// Merge transition metadata (<see cref="MergeBlockNumber"/>, <see cref="TerminalTotalDifficulty"/>)
/// stays delegated to the base provider, describing the underlying chain rather than the pinned fork.
/// </remarks>
internal sealed class StatelessSpecProvider(
    ISpecProvider baseProvider,
    ulong chainId,
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

    public ulong NetworkId => chainId;

    public ulong ChainId => chainId;

    public string SealEngine => baseProvider.SealEngine;

    public ForkActivation[] TransitionActivations => baseProvider.TransitionActivations;

    public IReleaseSpec GetSpec(ForkActivation activation) =>
        activation >= activeForkActivation ? activeForkSpec : baseProvider.GetSpec(activation);

    public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null) =>
        baseProvider.UpdateMergeTransitionInfo(blockNumber, terminalTotalDifficulty);

    public static StatelessSpecProvider Create(IForkAwareSpecProvider baseProvider, ulong chainId, ForkConfig forkConfig)
    {
        string? forkName = ForkIndexHelper.GetForkNameByIndex(forkConfig.Fork);

        if (forkName is null || !baseProvider.TryGetForkSpec(forkName, out IReleaseSpec? spec))
            throw new ArgumentException($"Unknown fork: {forkConfig.Fork}", nameof(forkConfig));

        spec = forkConfig.BlobSchedule is [{ } blobSchedule]
           ? new StatelessReleaseSpec(spec!, blobSchedule)
           : spec;

        return new(baseProvider, chainId, forkConfig.Activation.ToForkActivation(), spec!);
    }

    private sealed class StatelessReleaseSpec(IReleaseSpec spec, BlobSchedule blobSchedule) : ReleaseSpecDecorator(spec)
    {
        public override ulong TargetBlobCount => blobSchedule.Target;
        public override ulong MaxBlobCount => blobSchedule.Max;
        public override UInt256 BlobBaseFeeUpdateFraction => new(blobSchedule.BaseFeeUpdateFraction);
    }
}
