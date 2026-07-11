// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.GnosisForks;
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

    public ulong? DaoBlockNumber => baseProvider.DaoBlockNumber;

    public ulong? BeaconChainGenesisTimestamp => baseProvider.BeaconChainGenesisTimestamp;

    public ulong NetworkId => chainId;

    public ulong ChainId => chainId;

    public string SealEngine => baseProvider.SealEngine;

    public ForkActivation[] TransitionActivations => baseProvider.TransitionActivations;

    public IReleaseSpec GetSpec(ForkActivation activation) =>
        activation >= activeForkActivation ? activeForkSpec : baseProvider.GetSpec(activation);

    public void UpdateMergeTransitionInfo(ulong? blockNumber, UInt256? terminalTotalDifficulty = null) =>
        baseProvider.UpdateMergeTransitionInfo(blockNumber, terminalTotalDifficulty);

    public static StatelessSpecProvider Create(
        IForkAwareSpecProvider baseProvider,
        ulong chainId,
        ForkConfig forkConfig,
        ProtocolFork protocolFork)
    {
        string forkName = protocolFork switch
        {
            ProtocolFork.Amsterdam => Amsterdam.Instance.Name,
            _ => throw new ArgumentOutOfRangeException(nameof(protocolFork), protocolFork, "Unknown protocol fork")
        };

        IReleaseSpec spec;
        if (!baseProvider.TryGetForkSpec(forkName, out IReleaseSpec? configuredSpec) || configuredSpec is null)
        {
            spec = (chainId, protocolFork) switch
            {
                (BlockchainIds.Gnosis or BlockchainIds.Chiado, ProtocolFork.Amsterdam) => AmsterdamGnosis.Instance,
                (_, ProtocolFork.Amsterdam) => Amsterdam.Instance,
                _ => throw new ArgumentException($"Unknown fork: {protocolFork}", nameof(protocolFork))
            };
        }
        else
        {
            spec = configuredSpec;
        }

        return new(baseProvider, chainId, forkConfig.Activation.ToForkActivation(), spec);
    }
}
