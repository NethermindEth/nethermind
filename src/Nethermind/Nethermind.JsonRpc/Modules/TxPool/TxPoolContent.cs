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

        public Dictionary<string, Dictionary<string, TransactionForRpc>> Pending { get; set; }
        public Dictionary<string, Dictionary<string, TransactionForRpc>> Queued { get; set; }

        private static Dictionary<string, Dictionary<string, TransactionForRpc>> MapByAddress(
            Dictionary<AddressAsKey, IDictionary<string, Transaction>> source,
            in TransactionForRpcContext extraData)
        {
            Dictionary<string, Dictionary<string, TransactionForRpc>> result = new(source.Count);
            foreach (KeyValuePair<AddressAsKey, IDictionary<string, Transaction>> byAddress in source)
            {
                string key = ((Address)byAddress.Key).ToString(withZeroX: true, withEip55Checksum: true);
                Dictionary<string, TransactionForRpc> txsByNonce = new(byAddress.Value.Count);
                foreach (KeyValuePair<string, Transaction> kv in byAddress.Value)
                    txsByNonce[kv.Key] = TransactionForRpc.FromTransaction(kv.Value, extraData);
                result[key] = txsByNonce;
            }
            return result;
        }
    }
}
