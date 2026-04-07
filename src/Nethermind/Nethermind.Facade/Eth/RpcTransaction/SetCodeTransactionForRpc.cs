// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;

namespace Nethermind.Facade.Eth.RpcTransaction;

public class SetCodeTransactionForRpc : EIP1559TransactionForRpc, IFromTransaction<SetCodeTransactionForRpc>
{
    public new static TxType TxType => TxType.SetCode;

    public override TxType? Type => TxType;

    [JsonDiscriminator]
    public AuthorizationListForRpc AuthorizationList { get; set; }

    [JsonConstructor]
    public SetCodeTransactionForRpc() { }

    public SetCodeTransactionForRpc(Transaction transaction, in TransactionForRpcContext extraData)
        : base(transaction, extraData)
    {
        AuthorizationList = AuthorizationListForRpc.FromAuthorizationList(transaction.AuthorizationList);
    }

    public override Result<Transaction> ToTransaction(bool validateUserInput = false)
    {
        Result<Transaction> baseResult = base.ToTransaction(validateUserInput);
        if (baseResult.IsError) return baseResult;

        Transaction tx = baseResult.Data;
        tx.AuthorizationList = AuthorizationList?.ToAuthorizationList() ?? [];

        return tx;
    }

    public new static SetCodeTransactionForRpc FromTransaction(Transaction tx, in TransactionForRpcContext extraData)
        => new(tx, extraData);
}
