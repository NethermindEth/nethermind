// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.TxPool;

public class TxPoolContentFrom
{
    public TxPoolContentFrom(TxPoolInfo info, Address address, ulong chainId)
    {
        TransactionForRpcContext extraData = new(chainId);
        AddressAsKey key = address;
        Pending = MapTransactions(info.Pending, key, extraData);
        Queued = MapTransactions(info.Queued, key, extraData);
    }

    public Dictionary<ulong, TransactionForRpc> Pending { get; }
    public Dictionary<ulong, TransactionForRpc> Queued { get; }

    private static Dictionary<ulong, TransactionForRpc> MapTransactions(
        Dictionary<AddressAsKey, IDictionary<ulong, Transaction>> source,
        AddressAsKey key,
        in TransactionForRpcContext extraData)
    {
        if (!source.TryGetValue(key, out IDictionary<ulong, Transaction>? txsByNonce))
            return new Dictionary<ulong, TransactionForRpc>(0);

        Dictionary<ulong, TransactionForRpc> result = new(txsByNonce.Count);
        foreach (KeyValuePair<ulong, Transaction> kv in txsByNonce)
            result[kv.Key] = TransactionForRpc.FromTransaction(kv.Value, extraData);
        return result;
    }
}
