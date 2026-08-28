// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.TxPool;

/// <summary>Response model for <c>txpool_contentFrom</c>: the pending and queued transactions of a single address.</summary>
/// <remarks>Keyed by decimal nonce, except EIP-8250 keyed transactions, which use the tx hash because several share one sequence.</remarks>
public class TxPoolContentFrom
{
    public TxPoolContentFrom(TxPoolSenderInfo info, ulong chainId)
    {
        TransactionForRpcContext extraData = new(chainId);
        Pending = MapTransactions(info.Pending, extraData);
        Queued = MapTransactions(info.Queued, extraData);
    }

    /// <summary>Transactions ready for inclusion in the next block.</summary>
    public Dictionary<string, TransactionForRpc> Pending { get; }

    /// <summary>Transactions with nonce gaps awaiting preceding transactions.</summary>
    public Dictionary<string, TransactionForRpc> Queued { get; }

    private static Dictionary<string, TransactionForRpc> MapTransactions(
        IDictionary<string, Transaction> source,
        in TransactionForRpcContext extraData)
    {
        Dictionary<string, TransactionForRpc> result = new(source.Count);
        foreach (KeyValuePair<string, Transaction> kv in source)
            result[kv.Key] = TransactionForRpc.FromTransaction(kv.Value, extraData);
        return result;
    }
}
