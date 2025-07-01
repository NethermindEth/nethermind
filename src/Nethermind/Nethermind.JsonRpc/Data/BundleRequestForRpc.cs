// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Data;

public class BundleRequestForRpc
{
    /// <summary>
    /// Array of signed transaction hex strings
    /// </summary>
    [JsonPropertyName("txs")]
    public string[] Transactions { get; set; } = [];

    /// <summary>
    /// Target block number (hex string)
    /// </summary>
    [JsonPropertyName("blockNumber")]
    public string BlockNumber { get; set; } = string.Empty;
}