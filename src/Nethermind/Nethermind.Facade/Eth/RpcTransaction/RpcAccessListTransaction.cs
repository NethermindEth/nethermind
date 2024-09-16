// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Facade.Eth.RpcTransaction;

public class RpcAccessListTransaction : RpcLegacyTransaction
{
    // HACK: To ensure that serialized Txs always have a `ChainId` we keep the last loaded `ChainSpec`.
    // See: https://github.com/NethermindEth/nethermind/pull/6061#discussion_r1321634914
    public static UInt256? DefaultChainId { get; set; }

    public RpcAccessList AccessList { get; set; }

    public new UInt256 ChainId { get; set; }

    public UInt256 YParity { get; set; }

    /// <summary>
    /// For backwards compatibility, <c>v</c> is optionally provided as an alternative to <c>yParity</c>.
    /// This field is <b>DEPRECATED</b> and all use of it should migrate to <c>yParity</c>.
    /// </summary>
    public override UInt256 V { get; set; }

    public RpcAccessListTransaction(Transaction transaction, int? txIndex = null, Hash256? blockHash = null, long? blockNumber = null)
        : base(transaction, txIndex, blockHash, blockNumber)
    {
        AccessList = RpcAccessList.FromAccessList(transaction.AccessList);
        ChainId = transaction.ChainId
                  ?? DefaultChainId
                  ?? BlockchainIds.Mainnet;
        YParity = transaction.Signature?.RecoveryId ?? 0;
        V = YParity;
    }

    public new class Converter : IToTransaction<RpcGenericTransaction>, IFromTransaction<RpcAccessListTransaction>
    {
        private readonly RpcLegacyTransaction.Converter _baseConverter = new();

        public RpcAccessListTransaction FromTransaction(Transaction tx, TransactionConverterExtraData extraData)
            => new(tx, txIndex: extraData.TxIndex, blockHash: extraData.BlockHash, blockNumber: extraData.BlockNumber);

        public Transaction ToTransaction(RpcGenericTransaction rpcTx)
        {
            var tx = _baseConverter.ToTransaction(rpcTx);
            tx.AccessList = rpcTx.AccessList?.ToAccessList() ?? Core.Eip2930.AccessList.Empty;
            return tx;
        }

        public Transaction ToTransactionWithDefaults(RpcGenericTransaction rpcTx, ulong chainId)
        {
            var tx = _baseConverter.ToTransactionWithDefaults(rpcTx, chainId);
            tx.AccessList = rpcTx.AccessList?.ToAccessList() ?? Core.Eip2930.AccessList.Empty;
            return tx;
        }
    }
}