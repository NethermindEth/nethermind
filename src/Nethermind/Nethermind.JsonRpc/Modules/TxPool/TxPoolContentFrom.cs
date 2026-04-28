// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.TxPool;

/// <summary>Response model for <c>txpool_contentFrom</c>: pending and queued transactions from a single address, keyed by nonce.</summary>
public class TxPoolContentFrom
{
    public TxPoolContentFrom(TxPoolSenderInfo info, ulong chainId)
    {
        TransactionForRpcContext extraData = new(chainId);
        Pending = MapTransactions(info.Pending, extraData);
        Queued = MapTransactions(info.Queued, extraData);
    }

    /// <summary>Transactions ready for inclusion in the next block, keyed by nonce.</summary>
    public Dictionary<ulong, TransactionForRpc> Pending { get; }

    /// <summary>Transactions with nonce gaps awaiting preceding transactions, keyed by nonce.</summary>
    public Dictionary<ulong, TransactionForRpc> Queued { get; }

    private static Dictionary<ulong, TransactionForRpc> MapTransactions(
        IDictionary<ulong, Transaction> source,
        in TransactionForRpcContext extraData)
    {
        Dictionary<ulong, TransactionForRpc> result = new(source.Count);
        foreach (KeyValuePair<ulong, Transaction> kv in source)
            result[kv.Key] = TransactionForRpc.FromTransaction(kv.Value, extraData);
        return result;
    }
}
