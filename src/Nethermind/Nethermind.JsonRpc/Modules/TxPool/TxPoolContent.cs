// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.TxPool
{
    public class TxPoolContent
    {
        public TxPoolContent(TxPoolInfo info, ulong chainId)
        {
            TransactionForRpcContext extraData = new(chainId);
            Pending = MapByAddress(info.Pending, extraData);
            Queued = MapByAddress(info.Queued, extraData);
        }

        public Dictionary<string, Dictionary<TxPoolTxKey, TransactionForRpc>> Pending { get; set; }
        public Dictionary<string, Dictionary<TxPoolTxKey, TransactionForRpc>> Queued { get; set; }

        private static Dictionary<string, Dictionary<TxPoolTxKey, TransactionForRpc>> MapByAddress(
            Dictionary<AddressAsKey, IDictionary<TxPoolTxKey, Transaction>> source,
            in TransactionForRpcContext extraData)
        {
            Dictionary<string, Dictionary<TxPoolTxKey, TransactionForRpc>> result = [with(source.Count)];
            foreach (KeyValuePair<AddressAsKey, IDictionary<TxPoolTxKey, Transaction>> byAddress in source)
            {
                string key = ((Address)byAddress.Key).ToString(withZeroX: true, withEip55Checksum: true);
                Dictionary<TxPoolTxKey, TransactionForRpc> txsByNonce = [with(byAddress.Value.Count)];
                foreach (KeyValuePair<TxPoolTxKey, Transaction> kv in byAddress.Value)
                    txsByNonce[kv.Key] = TransactionForRpc.FromTransaction(kv.Value, extraData);
                result[key] = txsByNonce;
            }
            return result;
        }
    }
}
