// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Facade.Eth.RpcTransaction;

public class RpcLegacyTransaction : RpcNethermindTransaction
{
    public TxType Type { get; set; }

    public UInt256 Nonce { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Address? To { get; set; }

    public long Gas { get; set; }

    public UInt256 Value { get; set; }

    public byte[] Input { get; set; }

    public virtual UInt256 GasPrice { get; set; }

    public UInt256? ChainId { get; set; }

    public virtual UInt256 V { get; set; }

    public UInt256 R { get; set; }
    public UInt256 S { get; set; }

    [JsonConstructor]
    public RpcLegacyTransaction() { }

    public RpcLegacyTransaction(Transaction transaction, int? txIndex = null, Hash256? blockHash = null, long? blockNumber = null)
        : base(transaction, txIndex, blockHash, blockNumber)
    {
        Type = transaction.Type;
        Nonce = transaction.Nonce;
        To = transaction.To;
        Gas = transaction.GasLimit;
        Value = transaction.Value;
        Input = transaction.Data.AsArray() ?? [];
        GasPrice = transaction.GasPrice;
        ChainId = transaction.ChainId;

        R = new UInt256(transaction.Signature?.R ?? [], true);
        S = new UInt256(transaction.Signature?.S ?? [], true);
        V = transaction.Signature?.V ?? 0;
    }

    public override Transaction ToTransaction()
    {
        return new Transaction()
        {
            Type = Type,
            Nonce = Nonce,
            To = To,
            GasLimit = Gas,
            Value = Value,
            Data = Input,
            GasPrice = GasPrice,

            // TODO: Dangerous cast
            ChainId = (ulong)ChainId,
            // TODO: Get `From`
            // SenderAddress = From,
        };
    }

    public static readonly ITransactionConverter<RpcLegacyTransaction> Converter = new ConverterImpl();

    private class ConverterImpl : ITransactionConverter<RpcLegacyTransaction>
    {
        public RpcLegacyTransaction FromTransaction(Transaction tx, TransactionConverterExtraData extraData)
            => new(tx, txIndex: extraData.TxIndex, blockHash: extraData.BlockHash, blockNumber: extraData.BlockNumber);
    }
}
