// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.CoreOfCore;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.TxPool
{
    public class TxPoolStatus
    {
        public TxPoolStatus(TxPoolInfo info)
        {
            Pending = (int)info.Pending.Sum(static t => t.Value.Count);
            Queued = (int)info.Queued.Sum(static t => t.Value.Count);
        }

        public int Pending { get; set; }
        public int Queued { get; set; }
    }
}
