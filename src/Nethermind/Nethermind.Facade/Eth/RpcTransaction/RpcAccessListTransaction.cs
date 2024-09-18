// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Facade.Eth.RpcTransaction;

public class RpcAccessListTransaction : RpcLegacyTransaction
{
    // HACK: To ensure that serialized Txs always have a `ChainId` we keep the last loaded `ChainSpec`.
    // See: https://github.com/NethermindEth/nethermind/pull/6061#discussion_r1321634914
    public static UInt256? DefaultChainId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public RpcAccessList? AccessList { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public UInt256? YParity { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public override ulong? ChainId { get; set; }

    public RpcAccessListTransaction(Transaction transaction, int? txIndex = null, Hash256? blockHash = null, long? blockNumber = null)
        : base(transaction, txIndex, blockHash, blockNumber)
    {
        AccessList = RpcAccessList.FromAccessList(transaction.AccessList);
        YParity = transaction.Signature?.RecoveryId ?? 0;
        ChainId = (ulong?)(transaction.ChainId ?? DefaultChainId ?? BlockchainIds.Mainnet);
        V = YParity ?? 0;
    }

    public new class Converter : IFromTransaction<RpcAccessListTransaction>
    {
        public RpcAccessListTransaction FromTransaction(Transaction tx, TransactionConverterExtraData extraData)
            => new(tx, txIndex: extraData.TxIndex, blockHash: extraData.BlockHash, blockNumber: extraData.BlockNumber);
    }
}
