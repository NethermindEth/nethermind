// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Xdc;

public class EpochNumInfo
{
    [JsonPropertyName("hash")]
    public Hash256? EpochBlockHash { get; set; }

    [JsonPropertyName("round")]
    public UInt256? EpochRound { get; set; }

    [JsonPropertyName("firstBlock")]
    public UInt256? EpochFirstBlockNumber { get; set; }

    [JsonPropertyName("lastBlock")]
    public UInt256? EpochLastBlockNumber { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EpochConsensusVersion { get; set; }
}
