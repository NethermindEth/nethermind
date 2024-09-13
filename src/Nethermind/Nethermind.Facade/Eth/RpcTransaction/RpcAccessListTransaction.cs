// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
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
    public new UInt256? V { get; set; }

    [JsonConstructor]
    public RpcAccessListTransaction() { }

    public RpcAccessListTransaction(Transaction transaction) : base(transaction)
    {
        AccessList = RpcAccessList.FromAccessList(transaction.AccessList);
        ChainId = transaction.ChainId
                  ?? DefaultChainId
                  ?? BlockchainIds.Mainnet;
        YParity = transaction.Signature?.RecoveryId ?? 0;
        V = transaction.Signature?.RecoveryId;
    }

    public new static readonly ITransactionConverter<RpcAccessListTransaction> Converter = new ConverterImpl();

    private class ConverterImpl : ITransactionConverter<RpcAccessListTransaction>
    {
        public RpcAccessListTransaction FromTransaction(Transaction transaction) => new(transaction);
    }
}
