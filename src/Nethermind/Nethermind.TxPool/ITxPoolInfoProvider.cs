// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Collections.Immutable;
using Nethermind.Core;

namespace Nethermind.TxPool;

public interface ITxPoolInfoProvider
{
    TxPoolInfo GetInfo();

    // Default impl falls back to GetInfo() for external implementers; the in-tree provider
    // overrides both with single-sender / count-only paths that avoid the full-pool walk.
    TxPoolSenderInfo GetSenderInfo(Address address)
    {
        TxPoolInfo info = GetInfo();
        info.Pending.TryGetValue(address, out IDictionary<ulong, Transaction>? pending);
        info.Queued.TryGetValue(address, out IDictionary<ulong, Transaction>? queued);
        if (pending is null && queued is null) return TxPoolSenderInfo.Empty;
        return new TxPoolSenderInfo(
            pending ?? ImmutableDictionary<ulong, Transaction>.Empty,
            queued ?? ImmutableDictionary<ulong, Transaction>.Empty);
    }

    TxPoolCounts GetCounts()
    {
        TxPoolInfo info = GetInfo();
        int pending = 0;
        int queued = 0;
        foreach (KeyValuePair<AddressAsKey, IDictionary<ulong, Transaction>> kv in info.Pending)
            pending += kv.Value.Count;
        foreach (KeyValuePair<AddressAsKey, IDictionary<ulong, Transaction>> kv in info.Queued)
            queued += kv.Value.Count;
        return new TxPoolCounts(pending, queued);
    }
}
