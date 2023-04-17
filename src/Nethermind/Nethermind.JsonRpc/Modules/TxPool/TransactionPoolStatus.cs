// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.TxPool
{
    public class TxPoolStatus
    {
        public TxPoolStatus(TxPoolInfo info)
        {
            Pending = info.Pending.Sum(t => t.Value.Count);
            Queued = info.Queued.Sum(t => t.Value.Count);
        }

        public int Pending { get; set; }
        public int Queued { get; set; }
    }
}
