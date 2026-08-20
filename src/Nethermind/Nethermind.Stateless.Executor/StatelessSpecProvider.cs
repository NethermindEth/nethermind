// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.Specs.GnosisForks;
using Nethermind.Stateless.Execution.IO;

namespace Nethermind.Stateless.Execution;

/// <remarks>
/// Stateless inputs can pin a named fork independently of the base chain's transition schedule.
/// For activations at or after the payload's own activation, <see cref="GetSpec(ForkActivation)"/> returns
/// the payload's release spec; earlier activations continue to use the base provider.
/// Chain id is supplied externally, so any compatible base schedule (e.g. Mainnet rules) can serve
/// as a devnet's fork catalog without misreporting the chain id to EIP-155 validation.
/// Merge transition metadata (<see cref="MergeBlockNumber"/>, <see cref="TerminalTotalDifficulty"/>)
/// stays delegated to the base provider, describing the underlying chain rather than the pinned fork.
/// </remarks>
internal sealed class StatelessSpecProvider(
    ISpecProvider baseProvider,
    ulong chainId,
    ForkActivation payloadActivation,
    IReleaseSpec payloadSpec)
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
        activation >= payloadActivation ? payloadSpec : baseProvider.GetSpec(activation);

    public void UpdateMergeTransitionInfo(ulong? blockNumber, UInt256? terminalTotalDifficulty = null) =>
        baseProvider.UpdateMergeTransitionInfo(blockNumber, terminalTotalDifficulty);

    /// <summary>Creates the spec provider governing the rules of a decoded stateless payload.</summary>
    /// <param name="chainId">The chain id the payload was produced on.</param>
    /// <param name="protocolFork">
    /// The fork pinned by the input schema, or <see cref="ProtocolFork.Current"/> to follow the chain's schedule.
    /// </param>
    /// <param name="payloadActivation">The activation of the payload's own block.</param>
    public static StatelessSpecProvider Create(ulong chainId, ProtocolFork protocolFork, ForkActivation payloadActivation)
    {
        ChainSpecBasedSpecProvider.KnownProvidersByChainId.TryGetValue(chainId, out IForkAwareSpecProvider? baseProvider);

        // Unknown chains (e.g. devnets) fall back to Mainnet: a fork catalog for a pinned fork,
        // but the activation schedule itself for ProtocolFork.Current.
        baseProvider ??= MainnetSpecProvider.Instance;

        return new(baseProvider, chainId, payloadActivation, GetPayloadSpec(baseProvider, chainId, protocolFork, payloadActivation));
    }

    private static IReleaseSpec GetPayloadSpec(
        IForkAwareSpecProvider baseProvider, ulong chainId, ProtocolFork protocolFork, ForkActivation payloadActivation)
    {
        if (protocolFork == ProtocolFork.Current)
            return baseProvider.GetSpec(payloadActivation);

        if (baseProvider.TryGetForkSpec(protocolFork.GetName(), out IReleaseSpec? configuredSpec) && configuredSpec is not null)
            return configuredSpec;

        return (chainId, protocolFork) switch
        {
            (BlockchainIds.Gnosis or BlockchainIds.Chiado, ProtocolFork.Amsterdam) => AmsterdamGnosis.Instance,
            (_, ProtocolFork.Amsterdam) => Amsterdam.Instance,
            _ => throw new ArgumentException($"Unknown fork: {protocolFork}", nameof(protocolFork))
        };
    }
}
