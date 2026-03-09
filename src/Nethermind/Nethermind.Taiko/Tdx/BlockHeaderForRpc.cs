// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Taiko.Tdx;

public class BlockHeaderForRpc
{
    public BlockHeaderForRpc() { }

    public BlockHeaderForRpc(BlockHeader header)
    {
        Hash = header.Hash;
        ParentHash = header.ParentHash;
        Sha3Uncles = header.UnclesHash;
        Miner = header.Beneficiary;
        StateRoot = header.StateRoot;
        TransactionsRoot = header.TxRoot;
        ReceiptsRoot = header.ReceiptsRoot;
        LogsBloom = header.Bloom;
        Difficulty = header.Difficulty;
        Number = header.Number;
        GasLimit = header.GasLimit;
        GasUsed = header.GasUsed;
        Timestamp = (UInt256)header.Timestamp;
        ExtraData = header.ExtraData;
        MixHash = header.MixHash;

        Nonce = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(Nonce, header.Nonce);

        BaseFeePerGas = header.BaseFeePerGas;
        WithdrawalsRoot = header.WithdrawalsRoot;
        BlobGasUsed = header.BlobGasUsed;
        ExcessBlobGas = header.ExcessBlobGas;
        ParentBeaconBlockRoot = header.ParentBeaconBlockRoot;
        RequestsHash = header.RequestsHash;
        SlotNumber = header.SlotNumber;
        BlockAccessListHash = header.BlockAccessListHash;
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Hash256? Hash { get; set; }

    public Hash256? ParentHash { get; set; }
    public Hash256? Sha3Uncles { get; set; }
    public Address? Miner { get; set; }
    public Hash256? StateRoot { get; set; }
    public Hash256? TransactionsRoot { get; set; }
    public Hash256? ReceiptsRoot { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Bloom? LogsBloom { get; set; }
    public UInt256 Difficulty { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long Number { get; set; }

    public long GasLimit { get; set; }
    public long GasUsed { get; set; }
    public UInt256 Timestamp { get; set; }
    public byte[] ExtraData { get; set; } = [];
    public Hash256? MixHash { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? Nonce { get; set; }
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
    public Hash256? BlockAccessListHash { get; set; }
}
