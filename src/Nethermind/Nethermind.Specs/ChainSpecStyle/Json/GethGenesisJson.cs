// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nethermind.Specs.ChainSpecStyle.Json;

/// <summary>
/// Represents the Geth-style genesis.json file format as defined in EIP-7949.
/// </summary>
public class GethGenesisJson
{
    public GethGenesisConfigJson Config { get; set; }

    public Dictionary<Address, GethGenesisAllocJson> Alloc { get; set; }

    public ulong Nonce { get; set; }

    public ulong? Timestamp { get; set; }

    public byte[]? ExtraData { get; set; }

    public ulong? GasLimit { get; set; }

    public UInt256 Difficulty { get; set; }

    [JsonPropertyName("mixhash")]
    public Hash256? MixHash { get; set; }

    public Address? Coinbase { get; set; }

    public ulong? BaseFeePerGas { get; set; }

    public ulong? ExcessBlobGas { get; set; }

    public ulong? BlobGasUsed { get; set; }

    public Hash256? ParentBeaconBlockRoot { get; set; }
}
