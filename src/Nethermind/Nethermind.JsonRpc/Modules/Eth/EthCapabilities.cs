// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Modules.Eth;

/// <summary>
/// Response for eth_capabilities — describes the head block and the historical data availability of each resource type.
/// Follows ethereum/execution-apis#755.
/// </summary>
/// <remarks>
/// Tx and Logs alias Receipts because both lookups flow through receipt storage, so their coverage
/// equals receipt coverage. <c>JsonPropertyOrder</c> mirrors the spec's example key order for doc
/// parity — JSON object key order is not part of the wire contract.
/// </remarks>
/// <param name="Head">The current chain head.</param>
/// <param name="State">Historical account and storage state availability.</param>
/// <param name="Receipts">Receipt lookup availability (also exposed as <see cref="Tx"/> and <see cref="Logs"/>).</param>
/// <param name="Blocks">Historical block and header availability.</param>
/// <param name="Stateproofs">Proof / trie-node availability (eth_getProof depth window).</param>
public record EthCapabilities(
    [property: JsonPropertyOrder(0)] ChainHead Head,
    [property: JsonPropertyOrder(1)] ResourceAvailability State,
    [property: JsonPropertyOrder(4)] ResourceAvailability Receipts,
    [property: JsonPropertyOrder(5)] ResourceAvailability Blocks,
    [property: JsonPropertyOrder(6)] ResourceAvailability Stateproofs)
{
    /// <summary>Historical transaction lookup availability — same as <see cref="Receipts"/>.</summary>
    [JsonPropertyOrder(2)]
    public ResourceAvailability Tx => Receipts;

    /// <summary>Historical log search / filter availability — same as <see cref="Receipts"/>.</summary>
    [JsonPropertyOrder(3)]
    public ResourceAvailability Logs => Receipts;
}

/// <summary>Head block number and hash.</summary>
/// <param name="Number">Block number.</param>
/// <param name="Hash">Block hash.</param>
public readonly record struct ChainHead(ulong Number, Hash256 Hash);

/// <summary>Availability descriptor for one historical data resource.</summary>
/// <param name="Disabled"><c>true</c> when the resource is completely unavailable on this node.</param>
/// <param name="OldestBlock">Earliest block for which this resource is available. Omitted when <paramref name="Disabled"/> is <c>true</c>.</param>
/// <param name="DeleteStrategy">Retention / deletion strategy. Present only when data is pruned with a rolling window. Omitted for archive nodes or disabled resources.</param>
public readonly record struct ResourceAvailability(
    bool Disabled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ulong? OldestBlock = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DeleteStrategy? DeleteStrategy = null);

/// <summary>Rolling-window deletion strategy for a resource.</summary>
/// <param name="Type">Strategy type — currently always <c>"window"</c>; the spec uses <c>oneOf</c> to leave room for future strategies.</param>
/// <param name="RetentionBlocks">Number of recent blocks retained, serialized as a hex QUANTITY string per the spec's <c>uint</c> type.</param>
public readonly record struct DeleteStrategy(
    string Type,
    ulong RetentionBlocks);
