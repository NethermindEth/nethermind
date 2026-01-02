// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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

    public AccessListTransactionForRpc(Transaction transaction, int? txIndex = null, Hash256? blockHash = null, long? blockNumber = null, ulong? chainId = null)
        : base(transaction, txIndex, blockHash, blockNumber)
    {
        AccessList = AccessListForRpc.FromAccessList(transaction.AccessList);
        YParity = transaction.Signature?.RecoveryId ?? 0;
        ChainId = transaction.ChainId ?? chainId ?? BlockchainIds.Mainnet;
        V = YParity ?? 0;
    }

    public override Transaction ToTransaction()
    {
        var tx = base.ToTransaction();

        tx.AccessList = AccessList?.ToAccessList() ?? Core.Eip2930.AccessList.Empty;

        return tx;
    }

    public new static AccessListTransactionForRpc FromTransaction(Transaction tx, TransactionConverterExtraData extraData)
        => new(tx, txIndex: extraData.TxIndex, blockHash: extraData.BlockHash, blockNumber: extraData.BlockNumber, chainId: extraData.ChainId);
}
