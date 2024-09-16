// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Facade.Eth.RpcTransaction;

/// <summary>
/// Transaction object generic to all types.
/// Used only for deserialization purposes.
/// Source: https://github.com/ethereum/execution-apis/blob/1d4f70e84191bb574286fd7cea6c48795bf73e78/src/schemas/transaction.yaml#L358
/// </summary>
public class RpcGenericTransaction
{
    public TxType? Type { get; set; }

    public UInt256? Nonce { get; set; }

    public Address? To { get; set; }

    public Address? From { get; set; }

    public long? Gas { get; set; }

    public UInt256? Value { get; set; }

    // Required for compatibility with some CLs like Prysm
    // See: https://github.com/NethermindEth/nethermind/pull/6067
    [JsonPropertyName(nameof(Data))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? Data { set { Input = value; } private get { return null; } }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public byte[]? Input { get; set; }

    public UInt256? GasPrice { get; set; }

    public UInt256? MaxPriorityFeePerGas { get; set; }

    public UInt256? MaxFeePerGas { get; set; }

    public UInt256? MaxFeePerBlobGas { get; set; }

    public RpcAccessList? AccessList { get; set; }

    public byte[][]? BlobVersionedHashes { get; set; }

    // TODO: Add missing field
    // public byte[]? Blobs { get; set; }

    public UInt256? ChainId { get; set; }

    // TODO: `Gas` is set as default to to `long.MaxValue` here but to `90_000` in `ToTransactionWithDefaults`
    public void EnsureDefaults(long? gasCap)
    {
        if (gasCap is null || gasCap == 0)
            gasCap = long.MaxValue;

        Gas = Gas is null || Gas == 0
            ? gasCap
            : Math.Min(gasCap.Value, Gas.Value);

        From ??= Address.SystemUser;
    }

    public class Converter : IToTransaction<RpcGenericTransaction>
    {
        private readonly IToTransaction<RpcGenericTransaction>?[] _converters = new IToTransaction<RpcGenericTransaction>?[Transaction.MaxTxType + 1];

        public Converter RegisterConverter(TxType txType, IToTransaction<RpcGenericTransaction> converter)
        {
            _converters[(byte)txType] = converter;
            return this;
        }

        public Transaction ToTransaction(RpcGenericTransaction tx)
            => _converters[(byte)tx.Type!]!.ToTransaction(tx);

        public Transaction ToTransactionWithDefaults(RpcGenericTransaction tx, ulong chainId)
            => _converters[(byte)tx.Type!]!.ToTransactionWithDefaults(tx, chainId);
    }
}

public static class RpcGenericTransactionExtensions
{
    public static readonly RpcGenericTransaction.Converter GlobalConverter = new RpcGenericTransaction.Converter()
        .RegisterConverter(TxType.Legacy, new RpcLegacyTransaction.Converter())
        .RegisterConverter(TxType.AccessList, new RpcAccessListTransaction.Converter())
        .RegisterConverter(TxType.EIP1559, new RpcEIP1559Transaction.Converter())
        .RegisterConverter(TxType.Blob, new RpcBlobTransaction.Converter());

    /// <remarks>
    /// Intended to be used until proper DI is implemented.
    /// </remarks>
    public static Transaction ToTransaction(this RpcGenericTransaction rpcTransaction)
    {
        return GlobalConverter.ToTransaction(rpcTransaction);
    }
}
