// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

    public long? Gas { get; set; }

    public UInt256 Value { get; set; }

    public byte[] Input { get; set; }

    public virtual UInt256 GasPrice { get; set; }

    public ulong? ChainId { get; set; }

    public virtual UInt256 V { get; set; }

    public UInt256 R { get; set; }
    public UInt256 S { get; set; }

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

    public class Converter : IToTransaction<RpcGenericTransaction>, IFromTransaction<RpcLegacyTransaction>
    {
        public RpcLegacyTransaction FromTransaction(Transaction tx, TransactionConverterExtraData extraData)
            => new(tx, txIndex: extraData.TxIndex, blockHash: extraData.BlockHash, blockNumber: extraData.BlockNumber);

        public Transaction ToTransaction(RpcGenericTransaction rpcTx)
        {
            Transaction tx = new()
            {
                Type = (TxType)rpcTx.Type,
                Nonce = rpcTx.Nonce ?? 0, // TODO: here pick the last nonce?
                To = rpcTx.To,
                GasLimit = rpcTx.Gas ?? 0,
                Value = rpcTx.Value ?? 0,
                Data = rpcTx.Input,
                GasPrice = rpcTx.GasPrice ?? 0,
                SenderAddress = rpcTx.From,

                // TODO: Unsafe cast
                ChainId = (ulong?)rpcTx.ChainId,
            };

            return tx;
        }

        public Transaction ToTransactionWithDefaults(RpcGenericTransaction t, ulong chainId)
        {
            throw new NotImplementedException();
        }
    }
}
