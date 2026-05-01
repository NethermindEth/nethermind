// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Modules.Eth;

/// <summary>
/// Response for eth_capabilities — describes the head block and the historical data availability of each resource type.
/// Follows ethereum/execution-apis#755.
/// </summary>
public record class EthCapabilitiesResult
{
    /// <summary>The current chain head.</summary>
    public required CapabilityHead Head { get; init; }

    /// <summary>Historical account and storage state availability.</summary>
    public required CapabilityResource State { get; init; }

    /// <summary>Historical transaction lookup availability.</summary>
    public required CapabilityResource Tx { get; init; }

    /// <summary>Historical log search / filter availability.</summary>
    public required CapabilityResource Logs { get; init; }

    /// <summary>Receipt lookup availability.</summary>
    public required CapabilityResource Receipts { get; init; }

    /// <summary>Historical block and header availability.</summary>
    public required CapabilityResource Blocks { get; init; }

    /// <summary>Proof / trie-node availability (eth_getProof depth window).</summary>
    public required CapabilityResource Stateproofs { get; init; }
}

/// <summary>Head block number and hash.</summary>
public readonly record struct CapabilityHead
{
    /// <summary>Block number.</summary>
    public required long Number { get; init; }

    /// <summary>Block hash.</summary>
    public required Hash256 Hash { get; init; }
}

/// <summary>Availability descriptor for one historical data resource.</summary>
public readonly record struct CapabilityResource
{
    /// <summary><c>true</c> when the resource is completely unavailable on this node.</summary>
    public required bool Disabled { get; init; }

    /// <summary>
    /// Earliest block for which this resource is available.
    /// Omitted when <see cref="Disabled"/> is <c>true</c>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? OldestBlock { get; init; }

    /// <summary>
    /// Retention / deletion strategy. Present only when data is pruned with a rolling window.
    /// Omitted for archive nodes or disabled resources.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CapabilityDeleteStrategy? DeleteStrategy { get; init; }
}

/// <summary>Rolling-window deletion strategy for a resource.</summary>
public readonly record struct CapabilityDeleteStrategy
{
    /// <summary>Strategy type — always <c>"window"</c>.</summary>
    public required string Type { get; init; }

    /// <summary>Number of recent blocks retained.</summary>
    public required long RetentionBlocks { get; init; }
}
