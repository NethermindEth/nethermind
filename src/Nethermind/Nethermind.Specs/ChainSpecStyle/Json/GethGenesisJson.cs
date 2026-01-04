// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nethermind.Specs.ChainSpecStyle.Json;

/// <summary>
/// Represents the Geth-style genesis.json file format as defined in EIP-7949.
/// </summary>
public class GethGenesisJson
{
    public GethGenesisConfigJson Config { get; set; }

    public Dictionary<string, GethGenesisAllocJson> Alloc { get; set; }

    public string Nonce { get; set; }

    public string Timestamp { get; set; }

    public string ExtraData { get; set; }

    public string GasLimit { get; set; }

    public string Difficulty { get; set; }

    [JsonPropertyName("mixhash")]
    public string MixHash { get; set; }

    public string Coinbase { get; set; }

    public string BaseFeePerGas { get; set; }

    public string ExcessBlobGas { get; set; }

    public string BlobGasUsed { get; set; }

    public string ParentBeaconBlockRoot { get; set; }
}
