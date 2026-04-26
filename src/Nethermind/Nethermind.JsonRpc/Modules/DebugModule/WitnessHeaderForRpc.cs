// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Modules.DebugModule;

public class WitnessHeaderForRpc
{
    public WitnessHeaderForRpc(BlockHeader header)
    {
        ParentHash = header.ParentHash;
        Sha3Uncles = header.UnclesHash;
        StateRoot = header.StateRoot;
        TransactionsRoot = header.TxRoot;
        ReceiptsRoot = header.ReceiptsRoot;
        LogsBloom = header.Bloom;
        Difficulty = header.Difficulty;
        Number = header.Number;
        GasLimit = header.GasLimit;
        GasUsed = header.GasUsed;
        Timestamp = header.Timestamp;
        ExtraData = header.ExtraData;
        Hash = header.Hash;

        (Miner, MixHash, Nonce, Step, Signature) = GetConsensusFields(header);

        if (header.BaseFeePerGas != UInt256.Zero)
            BaseFeePerGas = header.BaseFeePerGas;

        WithdrawalsRoot = header.WithdrawalsRoot;
        BlobGasUsed = header.BlobGasUsed;
        ExcessBlobGas = header.ExcessBlobGas;
        ParentBeaconBlockRoot = header.ParentBeaconBlockRoot;
        RequestsHash = header.RequestsHash;
    }

    private static (Address? Miner, Hash256? MixHash, byte[]? Nonce, long? Step, byte[]? Signature) GetConsensusFields(BlockHeader header)
    {
        if (header.AuRaSignature is null)
        {
            byte[] nonce = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(nonce, header.Nonce);
            return (header.Beneficiary, header.MixHash, nonce, null, null);
        }

        return (header.Beneficiary, null, null, header.AuRaStep, header.AuRaSignature);
    }

    public Hash256? ParentHash { get; }
    public Hash256? Sha3Uncles { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Address? Miner { get; }

    public Hash256? StateRoot { get; }
    public Hash256? TransactionsRoot { get; }
    public Hash256? ReceiptsRoot { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Bloom? LogsBloom { get; }

    public UInt256 Difficulty { get; }
    public long Number { get; }
    public long GasLimit { get; }
    public long GasUsed { get; }
    public UInt256 Timestamp { get; }
    public byte[]? ExtraData { get; }
    public Hash256? MixHash { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public byte[]? Nonce { get; }

    [JsonConverter(typeof(NullableRawLongConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Step { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? Signature { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UInt256? BaseFeePerGas { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Hash256? WithdrawalsRoot { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? BlobGasUsed { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? ExcessBlobGas { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Hash256? ParentBeaconBlockRoot { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Hash256? RequestsHash { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Hash256? Hash { get; }
}
