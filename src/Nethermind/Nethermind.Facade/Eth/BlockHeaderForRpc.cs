// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Facade.Eth;

public class BlockHeaderForRpc
{
    public BlockHeaderForRpc() { }

    [SkipLocalsInit]
    public BlockHeaderForRpc(BlockHeader header, ISpecProvider? specProvider = null)
    {
        Number = header.Number;
        Hash = header.Hash;
        ParentHash = header.ParentHash;
        MixHash = header.MixHash;
        Nonce = header.Nonce;
        Sha3Uncles = header.UnclesHash;
        LogsBloom = header.Bloom;
        StateRoot = header.StateRoot;
        Miner = header.Beneficiary;
        Difficulty = header.Difficulty;
        ExtraData = header.ExtraData;
        GasLimit = header.GasLimit;
        GasUsed = header.GasUsed;
        Timestamp = header.Timestamp;
        TransactionsRoot = header.TxRoot;
        ReceiptsRoot = header.ReceiptsRoot;
        WithdrawalsRoot = header.WithdrawalsRoot;
        RequestsHash = header.RequestsHash;
        BlockAccessListHash = header.BlockAccessListHash;

        // Fork-conditional fields are spec-gated to match BlockForRpc's emission rules; without this,
        // pre-London headers with default-zero BaseFeePerGas would leak the field as "0x0".
        if (specProvider is not null)
        {
            IReleaseSpec spec = specProvider.GetSpec(header);
            if (spec.IsEip1559Enabled) BaseFeePerGas = header.BaseFeePerGas;
            if (spec.IsEip4844Enabled)
            {
                BlobGasUsed = header.BlobGasUsed;
                ExcessBlobGas = header.ExcessBlobGas;
            }
            if (spec.IsEip4788Enabled) ParentBeaconBlockRoot = header.ParentBeaconBlockRoot;
            if (spec.IsEip7843Enabled) SlotNumber = header.SlotNumber;
        }
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Address? Author { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ulong? Number { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Hash256? Hash { get; set; }

    public Hash256? ParentHash { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [JsonConverter(typeof(BlockNonceConverter))]
    public ulong? Nonce { get; set; }

    public Hash256? MixHash { get; set; }
    public Hash256? Sha3Uncles { get; set; }

    public byte[]? Signature { get; set; }

    [JsonConverter(typeof(NullableULongConverter))]
    public ulong? Step { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Bloom? LogsBloom { get; set; }

    public Hash256? StateRoot { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Address? Miner { get; set; }

    public UInt256 Difficulty { get; set; }
    public byte[] ExtraData { get; set; } = [];
    public ulong GasLimit { get; set; }
    public ulong GasUsed { get; set; }
    public UInt256 Timestamp { get; set; }
    public Hash256? TransactionsRoot { get; set; }
    public Hash256? ReceiptsRoot { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UInt256? BaseFeePerGas { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Hash256? WithdrawalsRoot { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? BlobGasUsed { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? ExcessBlobGas { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Hash256? ParentBeaconBlockRoot { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Hash256? RequestsHash { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? SlotNumber { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Hash256? BlockAccessListHash { get; set; }
}
