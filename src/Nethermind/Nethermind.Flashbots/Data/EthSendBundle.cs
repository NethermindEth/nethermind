// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.Flashbots.Data;

/// <summary>
/// Wire-format request for <c>eth_sendBundle</c> as defined by the Flashbots spec.
/// </summary>
/// <remarks>
/// Field naming and types follow
/// <a href="https://docs.flashbots.net/flashbots-auction/advanced/rpc-endpoint#eth_sendbundle">the Flashbots RPC reference</a>.
/// </remarks>
public class EthSendBundle
{
    [JsonRequired]
    [JsonPropertyName("txs")]
    public string[] Txs { get; set; } = [];

    [JsonRequired]
    [JsonPropertyName("blockNumber")]
    public string BlockNumber { get; set; } = string.Empty;

    [JsonPropertyName("minTimestamp")]
    public ulong? MinTimestamp { get; set; }

    [JsonPropertyName("maxTimestamp")]
    public ulong? MaxTimestamp { get; set; }

    [JsonPropertyName("revertingTxHashes")]
    public string[]? RevertingTxHashes { get; set; }

    [JsonPropertyName("replacementUuid")]
    public string? ReplacementUuid { get; set; }

    [JsonPropertyName("builders")]
    public string[]? Builders { get; set; }
}
