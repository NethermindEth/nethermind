// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Facade.Eth.RpcTransaction;

public class SetCodeTransactionForRpc : EIP1559TransactionForRpc, IFromTransaction<SetCodeTransactionForRpc>
{
    public new static TxType TxType => TxType.SetCode;

    public override TxType? Type => TxType;

    public AuthorizationListForRpc AuthorizationList { get; set; }

    #region Deprecated fields from `EIP1559TransactionForRpc`
    [JsonIgnore(Condition = JsonIgnoreCondition.Always)]
    public override UInt256? GasPrice { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Always)]
    public override UInt256? V { get; set; }
    #endregion

    [JsonConstructor]
    public SetCodeTransactionForRpc() { }

    public SetCodeTransactionForRpc(Transaction transaction, int? txIndex = null, Hash256? blockHash = null, long? blockNumber = null, UInt256? baseFee = null, ulong? chainId = null)
        : base(transaction, txIndex, blockHash, blockNumber, baseFee, chainId)
    {
        AuthorizationList = AuthorizationListForRpc.FromAuthorizationList(transaction.AuthorizationList);
    }

    public override Transaction ToTransaction()
    {
        var tx = base.ToTransaction();

        // TODO: `AuthorizationList` cannot be empty
        tx.AuthorizationList = AuthorizationList?.ToAuthorizationList() ?? [];

        return tx;
    }

    public new static SetCodeTransactionForRpc FromTransaction(Transaction tx, TransactionConverterExtraData extraData)
        => new(tx, txIndex: extraData.TxIndex, blockHash: extraData.BlockHash, blockNumber: extraData.BlockNumber, baseFee: extraData.BaseFee, chainId: extraData.ChainId);
}
