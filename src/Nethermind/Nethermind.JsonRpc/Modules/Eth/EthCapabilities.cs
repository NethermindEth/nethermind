// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Modules.Eth;

/// <summary>
/// Response for eth_capabilities — describes the head block and the historical data availability of each resource type.
/// Follows ethereum/execution-apis#755.
/// </summary>
/// <param name="Head">The current chain head.</param>
/// <param name="State">Historical account and storage state availability.</param>
/// <param name="Tx">Historical transaction lookup availability.</param>
/// <param name="Logs">Historical log search / filter availability.</param>
/// <param name="Receipts">Receipt lookup availability.</param>
/// <param name="Blocks">Historical block and header availability.</param>
/// <param name="Stateproofs">Proof / trie-node availability (eth_getProof depth window).</param>
public record class EthCapabilities(
    ChainHead Head,
    ResourceAvailability State,
    ResourceAvailability Tx,
    ResourceAvailability Logs,
    ResourceAvailability Receipts,
    ResourceAvailability Blocks,
    ResourceAvailability Stateproofs);

/// <summary>Head block number and hash.</summary>
/// <param name="Number">Block number.</param>
/// <param name="Hash">Block hash.</param>
public readonly record struct ChainHead(long Number, Hash256 Hash);

/// <summary>Availability descriptor for one historical data resource.</summary>
/// <param name="Disabled"><c>true</c> when the resource is completely unavailable on this node.</param>
/// <param name="OldestBlock">Earliest block for which this resource is available. Omitted when <paramref name="Disabled"/> is <c>true</c>.</param>
/// <param name="DeleteStrategy">Retention / deletion strategy. Present only when data is pruned with a rolling window. Omitted for archive nodes or disabled resources.</param>
public readonly record struct ResourceAvailability(
    bool Disabled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? OldestBlock = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DeleteStrategy? DeleteStrategy = null);

/// <summary>Rolling-window deletion strategy for a resource.</summary>
/// <param name="Type">Strategy type — always <c>"window"</c>.</param>
/// <param name="RetentionBlocks">Number of recent blocks retained.</param>
public readonly record struct DeleteStrategy(string Type, long RetentionBlocks);
