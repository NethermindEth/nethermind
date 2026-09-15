// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.TxPool
{
    public class TxPoolInfo(Dictionary<AddressAsKey, IDictionary<string, Transaction>> pending,
        Dictionary<AddressAsKey, IDictionary<string, Transaction>> queued)
    {
        public Dictionary<AddressAsKey, IDictionary<string, Transaction>> Pending { get; } = pending;
        public Dictionary<AddressAsKey, IDictionary<string, Transaction>> Queued { get; } = queued;
    }
}
