// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.TxPool
{
    public class TxPoolInfo
    {
        public IDictionary<Address, IDictionary<ulong, Transaction>> Pending { get; }
        public IDictionary<Address, IDictionary<ulong, Transaction>> Queued { get; }

        public TxPoolInfo(IDictionary<Address, IDictionary<ulong, Transaction>> pending,
            IDictionary<Address, IDictionary<ulong, Transaction>> queued)
        {
            Pending = pending;
            Queued = queued;
        }
    }
}
