// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Int256;

namespace Nethermind.Facade.Eth.RpcTransaction;

public class LegacyTransactionForRpc : TransactionForRpc, ITxTyped, IFromTransaction<LegacyTransactionForRpc>
{
    public static TxType TxType => TxType.Legacy;

    public override TxType? Type => TxType;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public UInt256? Nonce { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Address? To { get; set; }

    // NOTE: This field exists only during deserialization according to the Ethereum JSON-RPC spec.
    // No transaction types include a `From` field when serializing.
    // For backwards compatibility with previous Nethermind versions we also serialize it.
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Address? From { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public UInt256? Value { get; set; }

    // Required for compatibility with some CLs like Prysm
    // Accept during deserialization, ignore during serialization
    // See: https://github.com/NethermindEth/nethermind/pull/6067
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? Data { set { Input = value; } private get { return null; } }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public byte[]? Input { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public virtual UInt256? GasPrice { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public virtual ulong? ChainId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public virtual UInt256? V { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public virtual UInt256? R { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public virtual UInt256? S { get; set; }

    [JsonConstructor]
    public LegacyTransactionForRpc() { }

    public LegacyTransactionForRpc(Transaction transaction, int? txIndex = null, Hash256? blockHash = null, long? blockNumber = null)
        : base(transaction, txIndex, blockHash, blockNumber)
    {
        Nonce = transaction.Nonce;
        To = transaction.To;
        From = transaction.SenderAddress;
        Gas = transaction.GasLimit;
        Value = transaction.Value;
        Input = transaction.Data.AsArray() ?? [];
        GasPrice = transaction.GasPrice;
        ChainId = transaction.ChainId;

        Signature? signature = transaction.Signature;
        if (signature is null)
        {
            R = UInt256.Zero;
            S = UInt256.Zero;
            V = 0;
        }
        else
        {
            R = new UInt256(signature.R.Span, true);
            S = new UInt256(signature.S.Span, true);
            V = signature.V;
        }
    }

    public override Transaction ToTransaction()
    {
        var tx = base.ToTransaction();

        tx.Nonce = Nonce ?? 0; // TODO: Should we pick the last nonce?
        tx.To = To;
        tx.GasLimit = Gas ?? 90_000;
        tx.Value = Value ?? 0;
        tx.Data = Input;
        tx.GasPrice = GasPrice ?? 0;
        tx.ChainId = ChainId;
        tx.SenderAddress = From ?? Address.SystemUser;
        if ((R != 0 || S != 0) && (R is not null || S is not null))
        {
            ulong v;
            if (V is null)
            {
                v = 0;
            }
            else if (V.Value > 1)
            {
                v = V.Value.ToUInt64(null); // non protected
            }
            else
            {
                v = EthereumEcdsaExtensions.CalculateV(ChainId ?? 0, V.Value == 1); // protected
            }

            tx.Signature = new(R ?? UInt256.Zero, S ?? UInt256.Zero, v);
        }

        return tx;
    }

    public override void EnsureDefaults(long? gasCap)
    {
        if (gasCap is null || gasCap == 0)
            gasCap = long.MaxValue;

        Gas = Gas is null || Gas == 0
            ? gasCap
            : Math.Min(gasCap.Value, Gas.Value);

        From ??= Address.SystemUser;
    }

    public override bool ShouldSetBaseFee() => GasPrice.IsPositive();

    public static LegacyTransactionForRpc FromTransaction(Transaction tx, TransactionConverterExtraData extraData) =>
        new(tx, txIndex: extraData.TxIndex, blockHash: extraData.BlockHash, blockNumber: extraData.BlockNumber);
}
