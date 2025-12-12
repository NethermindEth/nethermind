// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.TxPool;

internal sealed class DelegationCache
{
    private readonly ConcurrentDictionary<AddressAsKey, int> _pendingDelegations = new();

    public bool HasPending(AddressAsKey key)
    {
        return _pendingDelegations.ContainsKey(key);
    }

    public void DecrementDelegationCount(AddressAsKey key)
    {
        InternalIncrement(key, false);
    }
    public void IncrementDelegationCount(AddressAsKey key)
    {
        InternalIncrement(key, true);
    }

    private void InternalIncrement(AddressAsKey key, bool increment)
    {
        int value = increment ? 1 : -1;
        var lastCount = _pendingDelegations.AddOrUpdate(key,
            (k) =>
            {
                if (increment)
                    return 1;
                return 0;
            },
            (k, c) => c + value);

        if (lastCount == 0)
        {
            //Remove() is threadsafe and only removes if the count is the same as the updated one
            ((ICollection<KeyValuePair<AddressAsKey, int>>)_pendingDelegations).Remove(
                new KeyValuePair<AddressAsKey, int>(key, lastCount));
        }
    }
}
