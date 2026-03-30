// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Facade.Eth.RpcTransaction;

public class AccessListTransactionForRpc : LegacyTransactionForRpc, IFromTransaction<AccessListTransactionForRpc>
{
    public new static TxType TxType => TxType.AccessList;

    public override TxType? Type => TxType;

    [JsonDiscriminator]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public AccessListForRpc? AccessList { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public virtual UInt256? YParity { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public sealed override ulong? ChainId { get; set; }

    [JsonConstructor]
    public AccessListTransactionForRpc() { }

    public AccessListTransactionForRpc(Transaction transaction, in TransactionForRpcContext extraData)
        : base(transaction, extraData)
    {
        AccessList = AccessListForRpc.FromAccessList(transaction.AccessList);
        YParity = transaction.Signature?.RecoveryId ?? 0;
        ChainId = transaction.ChainId ?? extraData.ChainId ?? BlockchainIds.Mainnet;
        V = YParity ?? 0;
    }

    public override Result<Transaction> ToTransaction(bool validateUserInput = false)
    {
        Result<Transaction> baseResult = base.ToTransaction(validateUserInput);
        if (baseResult.IsError) return baseResult;

        Transaction tx = baseResult.Data;
        tx.AccessList = AccessList?.ToAccessList() ?? Core.Eip2930.AccessList.Empty;

        return tx;
    }

    public new static AccessListTransactionForRpc FromTransaction(Transaction tx, in TransactionForRpcContext extraData)
        => new(tx, extraData);
}
