// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Data;

public class TransactionForRpc
{
    public TransactionForRpc(Transaction transaction) : this(null, null, null, transaction) { }

    public TransactionForRpc(Keccak? blockHash, long? blockNumber, int? txIndex, Transaction transaction, UInt256? baseFee = null)
    {
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
        Input = Data = transaction.Data;
        if (transaction.IsEip1559)
        {
            GasPrice = baseFee is not null
                ? transaction.CalculateEffectiveGasPrice(true, baseFee.Value)
                : transaction.MaxFeePerGas;
            MaxFeePerGas = transaction.MaxFeePerGas;
            MaxPriorityFeePerGas = transaction.MaxPriorityFeePerGas;
        }
        ChainId = transaction.ChainId;
        Type = transaction.Type;
        AccessList = transaction.AccessList is null ? null : AccessListItemForRpc.FromAccessList(transaction.AccessList);

        Signature? signature = transaction.Signature;
        if (signature is not null)
        {

            YParity = (transaction.IsEip1559 || transaction.IsEip2930) ? signature.RecoveryId : null;
            R = new UInt256(signature.R, true);
            S = new UInt256(signature.S, true);
            V = transaction.Type == TxType.Legacy ? (UInt256?)signature.V : (UInt256?)signature.RecoveryId;
        }
    }

    // ReSharper disable once UnusedMember.Global
    public TransactionForRpc()
    {
    }

    public Keccak? Hash { get; set; }
    public UInt256? Nonce { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Include)]
    public Keccak? BlockHash { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Include)]
    public long? BlockNumber { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Include)]
    public long? TransactionIndex { get; set; }

    public Address? From { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Include)]
    public Address? To { get; set; }

    public UInt256? Value { get; set; }
    public UInt256? GasPrice { get; set; }

    public UInt256? MaxPriorityFeePerGas { get; set; }

    public UInt256? MaxFeePerGas { get; set; }
    public long? Gas { get; set; }
    public byte[]? Data { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Include)]
    public byte[]? Input { get; set; }

    public UInt256? ChainId { get; set; }

    public TxType Type { get; set; }

    public AccessListItemForRpc[]? AccessList { get; set; }

    public UInt256? MaxFeePerDataGas { get; set; } // eip4844

    public UInt256? V { get; set; }

    public UInt256? S { get; set; }

    public UInt256? R { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
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
            Data = Data ?? Input,
            Type = Type,
            AccessList = TryGetAccessList(),
            ChainId = chainId,
            DecodedMaxFeePerGas = MaxFeePerGas ?? 0,
            Hash = Hash
        };

        if (tx.IsEip1559)
        {
            tx.GasPrice = MaxPriorityFeePerGas ?? 0;
        }

        if (tx.IsEip4844)
        {
            tx.MaxFeePerDataGas = MaxFeePerDataGas;
        }

        return tx;
    }

    public Transaction ToTransaction(ulong? chainId = null) => ToTransaction<Transaction>();

    public T ToTransaction<T>(ulong? chainId = null) where T : Transaction, new()
    {
        T tx = new()
        {
            GasLimit = Gas ?? 0,
            GasPrice = GasPrice ?? 0,
            Nonce = (ulong)(Nonce ?? 0), // here pick the last nonce?
            To = To,
            SenderAddress = From,
            Value = Value ?? 0,
            Data = Data ?? Input,
            Type = Type,
            AccessList = TryGetAccessList(),
            ChainId = chainId,
            MaxFeePerDataGas = MaxFeePerDataGas,
        };

        if (tx.IsEip1559)
        {
            tx.GasPrice = MaxPriorityFeePerGas ?? 0;
            tx.DecodedMaxFeePerGas = MaxFeePerGas ?? 0;
        }

        if (tx.IsEip4844)
        {
            tx.MaxFeePerDataGas = MaxFeePerDataGas;
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
}
