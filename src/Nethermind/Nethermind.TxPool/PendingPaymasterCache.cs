// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.TxPool;

/// <summary>
/// Counts the pending frame transactions paying through each paymaster, so admission can cap how many
/// of them a single non-canonical paymaster may sponsor at once.
/// </summary>
/// <remarks>
/// EIP-8141 <c>MAX_PENDING_TXS_USING_NON_CANONICAL_PAYMASTER</c> (ethereum/EIPs#12007, "Non-canonical
/// paymaster"). The key is the <c>pay</c> frame target, which is derived from the frame layout alone, so
/// increment and decrement stay symmetric even if the paymaster's code or balance changes while the
/// transaction is pending. Counting every paymaster rather than only the currently non-canonical ones
/// keeps that symmetry; the cap itself is applied by <see cref="Filters.FrameTxPaymasterFilter"/>.
/// </remarks>
internal sealed class PendingPaymasterCache
{
    private readonly ConcurrentDictionary<AddressAsKey, int> _pending = new();

    /// <summary>Pending frame transactions currently paying through <paramref name="key"/>.</summary>
    public int GetPendingCount(AddressAsKey key) => _pending.TryGetValue(key, out int count) ? count : 0;

    public void Increment(AddressAsKey key) => _pending.AddOrUpdate(key, 1, static (_, count) => count + 1);

    public void Decrement(AddressAsKey key)
    {
        // Clamped at zero so a double release can never take the count negative and permanently
        // disable the cap for a paymaster.
        int updated = _pending.AddOrUpdate(key, 0, static (_, count) => count > 0 ? count - 1 : 0);
        if (updated == 0)
        {
            // Threadsafe: removes the key only while its value is still zero (mirrors DelegationCache).
            ((ICollection<KeyValuePair<AddressAsKey, int>>)_pending).Remove(new KeyValuePair<AddressAsKey, int>(key, 0));
        }
    }
}
