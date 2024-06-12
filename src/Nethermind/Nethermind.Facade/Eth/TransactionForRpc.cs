// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;

namespace Nethermind.Facade.Eth;

public class TransactionForRpc
{
    // HACK: To ensure that serialized Txs always have a `ChainId` we keep the last loaded `ChainSpec`.
    // See: https://github.com/NethermindEth/nethermind/pull/6061#discussion_r1321634914
    public static UInt256? DefaultChainId { get; set; }

    public TransactionForRpc(Transaction transaction) : this(null, null, null, transaction) { }

    public TransactionForRpc(Hash256? blockHash, long? blockNumber, int? txIndex, Transaction transaction, UInt256? baseFee = null)
    {
        if (transaction.Type == TxType.DepositTx)
        {
            SourceHash = transaction.SourceHash;
            Mint = transaction.Mint;
            IsSystemTx = transaction.IsOPSystemTransaction;
        }
        Hash = transaction.Hash;
        Nonce = transaction.Nonce;
        BlockHash = blockHash;
        BlockNumber = blockNumber;
        TransactionIndex = txIndex;
        From = transaction.SenderAddress;
        To = transaction.To;
        Value = transaction.Value;
        GasPrice = transaction.GasPrice;
        Gas = transaction.GasLimit;
        Input = Data = transaction.Data.AsArray();
        if (transaction.Supports1559)
        {
            GasPrice = baseFee is not null
                ? transaction.CalculateEffectiveGasPrice(true, baseFee.Value)
                : transaction.MaxFeePerGas;
            MaxFeePerGas = transaction.MaxFeePerGas;
            MaxPriorityFeePerGas = transaction.MaxPriorityFeePerGas;
        }
        if (transaction.Type > TxType.Legacy)
        {
            ChainId = transaction.ChainId
                      ?? DefaultChainId
                      ?? BlockchainIds.Mainnet;
        }
        else
        {
            ChainId = transaction.ChainId;
        }
        Type = transaction.Type;
        if (transaction.SupportsAccessList)
        {
            AccessList = transaction.AccessList is null ? Array.Empty<AccessListItemForRpc>() : AccessListItemForRpc.FromAccessList(transaction.AccessList);
        }
        else
        {
            AccessList = null;
        }
        MaxFeePerBlobGas = transaction.MaxFeePerBlobGas;
        BlobVersionedHashes = transaction.BlobVersionedHashes;

        Signature? signature = transaction.Signature;
        if (signature is not null)
        {

            YParity = transaction.SupportsAccessList ? signature.RecoveryId : null;
            R = new UInt256(signature.R, true);
            S = new UInt256(signature.S, true);
            // V must be null for non-legacy transactions. Temporarily set to recovery id for Geth compatibility.
            // See https://github.com/ethereum/go-ethereum/issues/27727
            V = transaction.Type == TxType.Legacy ? signature.V : signature.RecoveryId;
        }
    }

    // ReSharper disable once UnusedMember.Global
    [JsonConstructor]
    public TransactionForRpc() { }

    public Hash256? SourceHash { get; set; }
    public UInt256? Mint { get; set; }
    public bool? IsSystemTx { get; set; } // this is the IsOpSystemTransaction flag

    public Hash256? Hash { get; set; }
    public UInt256? Nonce { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Hash256? BlockHash { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? BlockNumber { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? TransactionIndex { get; set; }

    public Address? From { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Address? To { get; set; }

    public UInt256? Value { get; set; }
    public UInt256? GasPrice { get; set; }

    public UInt256? MaxPriorityFeePerGas { get; set; }

    public UInt256? MaxFeePerGas { get; set; }
    public long? Gas { get; set; }

    // Required for compatibility with some CLs like Prysm
    // Accept during deserialization, ignore during serialization
    // See: https://github.com/NethermindEth/nethermind/pull/6067
    [JsonPropertyName(nameof(Data))]
    [JsonConverter(typeof(DataConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? Data { set { Input = value; } private get { return null; } }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public byte[]? Input { get; set; }

    public UInt256? ChainId { get; set; }

    public TxType Type { get; set; }

    public IEnumerable<AccessListItemForRpc>? AccessList { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UInt256? MaxFeePerBlobGas { get; set; } // eip4844

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[][]? BlobVersionedHashes { get; set; } // eip4844

    public UInt256? V { get; set; }

    public UInt256? S { get; set; }

    public UInt256? R { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UInt256? YParity { get; set; }

    public Transaction ToTransactionWithDefaults(ulong? chainId = null) => ToTransactionWithDefaults<Transaction>(chainId);

    public T ToTransactionWithDefaults<T>(ulong? chainId = null) where T : Transaction, new()
    {
        T tx = new()
        {
            GasLimit = Gas ?? 90000,
            GasPrice = GasPrice ?? 20.GWei(),
            Nonce = (ulong)(Nonce ?? 0), // here pick the last nonce?
            To = To,
            SenderAddress = From,
            Value = Value ?? 0,
            Data = Input,
            Type = Type,
            AccessList = TryGetAccessList(),
            ChainId = chainId,
            DecodedMaxFeePerGas = MaxFeePerGas ?? 0,
            Hash = Hash
        };

        if (tx.Supports1559)
        {
            tx.GasPrice = MaxPriorityFeePerGas ?? 0;
        }

        if (tx.SupportsBlobs)
        {
            tx.MaxFeePerBlobGas = MaxFeePerBlobGas;
            tx.BlobVersionedHashes = BlobVersionedHashes;
        }

        return tx;
    }

    public Transaction ToTransaction(ulong? chainId = null) => ToTransaction<Transaction>();

    public T ToTransaction<T>(ulong? chainId = null) where T : Transaction, new()
    {
        byte[]? data = Input;

        T tx = new()
        {
            GasLimit = Gas ?? 0,
            GasPrice = GasPrice ?? 0,
            Nonce = (ulong)(Nonce ?? 0), // here pick the last nonce?
            To = To,
            SenderAddress = From,
            Value = Value ?? 0,
            Data = (Memory<byte>?)data,
            Type = Type,
            AccessList = TryGetAccessList(),
            ChainId = chainId,
        };

        if (data is null)
        {
            tx.Data = null; // Yes this is needed... really. Try a debugger.
        }

        if (tx.Supports1559)
        {
            tx.GasPrice = MaxPriorityFeePerGas ?? 0;
            tx.DecodedMaxFeePerGas = MaxFeePerGas ?? 0;
        }

        if (tx.SupportsBlobs)
        {
            tx.MaxFeePerBlobGas = MaxFeePerBlobGas;
            tx.BlobVersionedHashes = BlobVersionedHashes;
        }

        return tx;
    }

    private AccessList? TryGetAccessList() =>
        !Type.IsTxTypeWithAccessList() || AccessList is null
            ? null
            : AccessListItemForRpc.ToAccessList(AccessList);

    public void EnsureDefaults(long? gasCap)
    {
        if (gasCap is null || gasCap == 0)
            gasCap = long.MaxValue;

        Gas = Gas is null || Gas == 0
            ? gasCap
            : Math.Min(gasCap.Value, Gas.Value);

        From ??= Address.SystemUser;
    }

    private class DataConverter : JsonConverter<byte[]?>
    {
        public override bool HandleNull { get; } = false;

        public override byte[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return JsonSerializer.Deserialize<byte[]?>(ref reader, options);
        }

        public override void Write(Utf8JsonWriter writer, byte[]? value, JsonSerializerOptions options)
        {
            throw new NotSupportedException();
        }
    }
}
