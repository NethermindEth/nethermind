// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.TxPool;

/// <summary>
/// Counts the pending frame transactions paying through each <c>pay</c> frame target.
/// </summary>
/// <remarks>Counts every paymaster, not only the currently non-canonical ones, so increment and decrement
/// stay symmetric even if the target's code changes while the transaction is pending.</remarks>
internal sealed class PendingPaymasterCache
{
    private readonly ConcurrentDictionary<AddressAsKey, int> _pending = new();

    /// <summary>Pending frame transactions currently paying through <paramref name="key"/>.</summary>
    public int GetPendingCount(AddressAsKey key) => _pending.TryGetValue(key, out int count) ? count : 0;

    public void Increment(AddressAsKey key) => _pending.AddOrUpdate(key, 1, static (_, count) => count + 1);

    public void Decrement(AddressAsKey key)
    {
        // Clamped at zero: a double release must not permanently disable the cap for a paymaster.
        int updated = _pending.AddOrUpdate(key, 0, static (_, count) => count > 0 ? count - 1 : 0);
        if (updated == 0)
        {
            // Threadsafe: removes the key only while its value is still zero (mirrors DelegationCache).
            ((ICollection<KeyValuePair<AddressAsKey, int>>)_pending).Remove(new KeyValuePair<AddressAsKey, int>(key, 0));
        }
    }
}
