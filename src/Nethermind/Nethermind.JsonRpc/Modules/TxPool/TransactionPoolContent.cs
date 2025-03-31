// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.TxPool
{
    public class TxPoolContent
    {
        public TxPoolContent(TxPoolInfo info, ulong chainId)
        {
            Pending = info.Pending.ToDictionary(k => k.Key, k => k.Value.ToDictionary(v => v.Key, v => TransactionForRpc.FromTransaction(v.Value, chainId: chainId)));
            Queued = info.Queued.ToDictionary(k => k.Key, k => k.Value.ToDictionary(v => v.Key, v => TransactionForRpc.FromTransaction(v.Value, chainId: chainId)));
        }

        public Dictionary<AddressAsKey, Dictionary<ulong, TransactionForRpc>> Pending { get; set; }
        public Dictionary<AddressAsKey, Dictionary<ulong, TransactionForRpc>> Queued { get; set; }
    }
}
