// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.TxPool
{
    public class TxPoolInfo
    {
        public Dictionary<AddressAsKey, IDictionary<ulong, Transaction>> Pending { get; }
        public Dictionary<AddressAsKey, IDictionary<ulong, Transaction>> Queued { get; }

        public TxPoolInfo(Dictionary<AddressAsKey, IDictionary<ulong, Transaction>> pending,
            Dictionary<AddressAsKey, IDictionary<ulong, Transaction>> queued)
        {
            Pending = pending;
            Queued = queued;
        }
    }
}
