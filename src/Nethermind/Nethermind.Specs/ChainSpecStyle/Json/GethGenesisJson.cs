// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;

namespace Nethermind.Specs.ChainSpecStyle.Json;

/// <summary>
/// Represents the Geth-style genesis.json file format as defined in EIP-7949.
/// </summary>
public class GethGenesisJson
{
    /// <summary>Optional chain name used in logging.</summary>
    /// <remarks>Nethermind extension to EIP-7949.</remarks>
    public string? Name { get; set; }

    /// <summary>Optional bootnodes as enode URLs or ENR strings; <c>null</c> loads no bootnodes.</summary>
    /// <remarks>Nethermind extension to EIP-7949. Invalid records are logged and skipped.</remarks>
    public string[]? Bootnodes { get; set; }

    public GethGenesisConfigJson? Config { get; set; }

    public Dictionary<Address, GethGenesisAllocJson>? Alloc { get; set; }

    [JsonConverter(typeof(ULongConverter))]
    public ulong Nonce { get; set; }

    public ulong? Timestamp { get; set; }

    public byte[]? ExtraData { get; set; }

    public ulong? GasLimit { get; set; }

    public UInt256 Difficulty { get; set; }

    [JsonPropertyName("mixhash")]
    public Hash256? MixHash { get; set; }

    public Address? Coinbase { get; set; }

    public UInt256? BaseFeePerGas { get; set; }

    public ulong? ExcessBlobGas { get; set; }

    public ulong? BlobGasUsed { get; set; }

    public Hash256? ParentBeaconBlockRoot { get; set; }

    public ulong? SlotNumber { get; set; }
}
