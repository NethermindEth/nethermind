// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Facade.Eth.RpcTransaction;

public sealed class RpcAccessListTransaction
{
    // HACK: To ensure that serialized Txs always have a `ChainId` we keep the last loaded `ChainSpec`.
    // See: https://github.com/NethermindEth/nethermind/pull/6061#discussion_r1321634914
    public static UInt256? DefaultChainId { get; set; }

    public TxType Type { get; set; }

    public UInt256 Nonce { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Address? To { get; set; }

    public long Gas { get; set; }

    public UInt256 Value { get; set; }

    public byte[] Input { get; set; }

    public UInt256 GasPrice { get; set; }

    public IEnumerable<AccessListItemForRpc> AccessList { get; set; }

    public UInt256 ChainId { get; set; }

    public UInt256 YParity { get; set; }

    /// <summary>
    /// For backwards compatibility, <c>v</c> is optionally provided as an alternative to <c>yParity</c>.
    /// This field is <b>DEPRECATED</b> and all use of it should migrate to <c>yParity</c>.
    /// </summary>
    public UInt256? V { get; set; }

    public UInt256 R { get; set; }

    public UInt256 S { get; set; }

    private RpcAccessListTransaction() { }

    public static RpcAccessListTransaction? FromTransaction(Transaction? transaction)
    {
        if (transaction is null)
        {
            return null;
        }

        if (transaction.Type != TxType.AccessList)
        {
            throw new ArgumentException("Transaction type must be AccessList");
        }

        return new RpcAccessListTransaction
        {
            Type = transaction.Type,
            Nonce = transaction.Nonce,
            To = transaction.To,
            Gas = transaction.GasLimit,
            Value = transaction.Value,
            Input = transaction.Data.AsArray() ?? [],
            GasPrice = transaction.GasPrice,
            AccessList = transaction.AccessList is null
                ? Array.Empty<AccessListItemForRpc>()
                : AccessListItemForRpc.FromAccessList(transaction.AccessList),
            ChainId = transaction.ChainId
                      ?? DefaultChainId
                      ?? BlockchainIds.Mainnet,

            YParity = transaction.Signature?.RecoveryId ?? 0,
            V = transaction.Signature?.RecoveryId ?? 0,
            R = new UInt256(transaction.Signature?.R ?? [], true),
            S = new UInt256(transaction.Signature?.S ?? [], true),
        };
    }
}
