// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Modules.Admin.Models
{
    public class EthProtocolDetails
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("earliestBlock")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? EarliestBlock { get; set; }

        [JsonPropertyName("latestBlock")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LatestBlock { get; set; }

        [JsonPropertyName("latestBlockHash")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LatestBlockHash { get; set; }
    }
}