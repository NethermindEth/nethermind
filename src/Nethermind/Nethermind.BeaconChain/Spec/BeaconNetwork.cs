// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.BeaconChain.Spec;

/// <summary>The consensus-layer parameters of a supported network that its execution chainspec does not carry.</summary>
/// <remarks>
/// Fork timing (genesis time, the Capella-onward fork epochs and the blob schedule) is not held here:
/// <see cref="BeaconChainSpec.ForExecution"/> reads it from the execution chainspec, so a fork date is set in one place.
/// </remarks>
internal sealed partial record BeaconNetwork
{
    public required ulong ChainId { get; init; }
    public required string CheckpointSyncUrl { get; init; }
    public required string[] Bootnodes { get; init; }
    public required ulong SecondsPerSlot { get; init; }
    public required ulong SlotsPerEpoch { get; init; }
    public required Hash256 GenesisValidatorsRoot { get; init; }
    /// <summary>The phase0 through Fulu fork versions, in fork order.</summary>
    public required byte[][] ForkVersions { get; init; }
    public required ulong AltairForkEpoch { get; init; }
    public required ulong BellatrixForkEpoch { get; init; }
    /// <summary>The Gloas <c>fork_version</c>; a placeholder unless <see cref="GloasForkVersionConfirmed"/>.</summary>
    public required byte[] GloasForkVersion { get; init; }
    /// <summary>Whether <see cref="GloasForkVersion"/> is the network's published version, so it may activate without an override.</summary>
    public bool GloasForkVersionConfirmed { get; init; }

    public static BeaconNetwork Mainnet { get; } = new()
    {
        ChainId = BlockchainIds.Mainnet,
        CheckpointSyncUrl = "https://mainnet.checkpoint.sigp.io",
        Bootnodes = MainnetBootnodes.Enrs,
        SecondsPerSlot = 12,
        SlotsPerEpoch = 32,
        GenesisValidatorsRoot = new Hash256(Bytes.FromHexString("0x4b363db94e286120d76eb905340fdd4e54bfe9f06bf33ff6cf5ad27f511bfe95")),
        ForkVersions = Versions("0x00000000", "0x01000000", "0x02000000", "0x03000000", "0x04000000", "0x05000000", "0x06000000"),
        AltairForkEpoch = 74240,
        BellatrixForkEpoch = 144896,
        GloasForkVersion = Bytes.FromHexString("0x07000000"), // specs/gloas/fork.md GLOAS_FORK_VERSION; not yet published for mainnet
    };

    /// <remarks><c>GLOAS_FORK_VERSION</c> 0x80000910 and <c>GLOAS_FORK_EPOCH</c> 143616 are from eth-clients/hoodi PR #35.</remarks>
    public static BeaconNetwork Hoodi { get; } = new()
    {
        ChainId = BlockchainIds.Hoodi,
        CheckpointSyncUrl = "https://checkpoint-sync.hoodi.ethpandaops.io",
        Bootnodes = HoodiBootnodes.Enrs,
        SecondsPerSlot = 12,
        SlotsPerEpoch = 32,
        GenesisValidatorsRoot = new Hash256(Bytes.FromHexString("0x212f13fc4df078b6cb7db228f1c8307566dcecf900867401a92023d7ba99cb5f")),
        ForkVersions = Versions("0x10000910", "0x20000910", "0x30000910", "0x40000910", "0x50000910", "0x60000910", "0x70000910"),
        AltairForkEpoch = 0,
        BellatrixForkEpoch = 0,
        GloasForkVersion = Bytes.FromHexString("0x80000910"),
        GloasForkVersionConfirmed = true,
    };

    /// <summary>Values from the <c>eth-clients/sepolia</c> <c>metadata/config.yaml</c> as of 2026-09-22.</summary>
    /// <remarks>
    /// <c>GLOAS_FORK_VERSION</c> 0x90000076 was merged in eth-clients/sepolia PR #126. The bootnode ENRs
    /// (<see cref="SepoliaBootnodes"/>) are from <c>metadata/bootstrap_nodes.yaml</c>.
    /// </remarks>
    public static BeaconNetwork Sepolia { get; } = new()
    {
        ChainId = BlockchainIds.Sepolia,
        CheckpointSyncUrl = "https://checkpoint-sync.sepolia.ethpandaops.io",
        Bootnodes = SepoliaBootnodes.Enrs,
        SecondsPerSlot = 12,
        SlotsPerEpoch = 32,
        GenesisValidatorsRoot = new Hash256(Bytes.FromHexString("0xd8ea171f3c94aea21ebc42a1ed61052acf3f9209c00e4efbaaddac09ed9b8078")),
        ForkVersions = Versions("0x90000069", "0x90000070", "0x90000071", "0x90000072", "0x90000073", "0x90000074", "0x90000075"),
        AltairForkEpoch = 50,
        BellatrixForkEpoch = 100,
        GloasForkVersion = Bytes.FromHexString("0x90000076"),
        GloasForkVersionConfirmed = true,
    };

    /// <exception cref="UnsupportedBeaconNetworkException">
    /// <paramref name="chainId"/> is not a network the embedded beacon chain driver supports. Never falls back
    /// to <see cref="Mainnet"/>: following the wrong chain silently is worse than refusing to start.
    /// </exception>
    public static BeaconNetwork ForChainId(ulong chainId) => chainId switch
    {
        BlockchainIds.Mainnet => Mainnet,
        BlockchainIds.Hoodi => Hoodi,
        BlockchainIds.Sepolia => Sepolia,
        BlockchainIds.Gnosis => Gnosis,
        BlockchainIds.Chiado => Chiado,
        _ => throw new UnsupportedBeaconNetworkException(chainId),
    };

    private static byte[][] Versions(params string[] versions) => [.. versions.Select(static version => Bytes.FromHexString(version))];
}
