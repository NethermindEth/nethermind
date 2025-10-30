// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.TxPool
{
    public class TxPoolInfo
    {
        public Dictionary<Box<Address>, IDictionary<ulong, Transaction>> Pending { get; }
        public Dictionary<Box<Address>, IDictionary<ulong, Transaction>> Queued { get; }

        public TxPoolInfo(Dictionary<Box<Address>, IDictionary<ulong, Transaction>> pending,
            Dictionary<Box<Address>, IDictionary<ulong, Transaction>> queued)
        {
            Pending = pending;
            Queued = queued;
        }
    }
}
