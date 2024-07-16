// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Data;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.TxPool
{
    public class TxPoolContent
    {
        public TxPoolContent(TxPoolInfo info)
        {
            Pending = info.Pending.ToDictionary(k => k.Key, k => k.Value.ToDictionary(v => v.Key, v => new TransactionForRpc(null, null, null, v.Value)));
            Queued = info.Queued.ToDictionary(k => k.Key, k => k.Value.ToDictionary(v => v.Key, v => new TransactionForRpc(null, null, null, v.Value)));
        }

        public Dictionary<AddressAsKey, Dictionary<ulong, TransactionForRpc>> Pending { get; set; }
        public Dictionary<AddressAsKey, Dictionary<ulong, TransactionForRpc>> Queued { get; set; }
    }
}
