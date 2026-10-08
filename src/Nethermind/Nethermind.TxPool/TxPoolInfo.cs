// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.TxPool
{
    public class TxPoolInfo(Dictionary<AddressAsKey, IDictionary<TxPoolTxKey, Transaction>> pending,
        Dictionary<AddressAsKey, IDictionary<TxPoolTxKey, Transaction>> queued)
    {
        public Dictionary<AddressAsKey, IDictionary<TxPoolTxKey, Transaction>> Pending { get; } = pending;
        public Dictionary<AddressAsKey, IDictionary<TxPoolTxKey, Transaction>> Queued { get; } = queued;
    }
}
