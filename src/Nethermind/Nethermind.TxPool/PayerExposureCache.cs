// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.TxPool;

/// <summary>
/// Tracks the summed pending maximum cost reserved per resolved frame-transaction payer, so that
/// admission can bound each payer's aggregate exposure to its balance.
/// </summary>
/// <remarks>
/// EIP-8141 per-payer reservation accounting (ethereum/EIPs#12007, "Reservation accounting applies
/// to every payer, not only canonical paymasters"). Mirrors <see cref="DelegationCache"/>: an
/// event-driven counter maintained from the pool's insert/remove events and read at admission.
/// </remarks>
internal sealed class PayerExposureCache
{
    private readonly ConcurrentDictionary<AddressAsKey, UInt256> _reserved = new();

    /// <summary>Summed pending maximum cost currently reserved for <paramref name="key"/>, or zero.</summary>
    public UInt256 GetReserved(AddressAsKey key) => _reserved.TryGetValue(key, out UInt256 reserved) ? reserved : UInt256.Zero;

    public void Add(AddressAsKey key, in UInt256 cost)
    {
        if (cost.IsZero) return;
        UInt256 delta = cost;
        _reserved.AddOrUpdate(key, delta, (_, existing) => existing + delta);
    }

    public void Subtract(AddressAsKey key, in UInt256 cost)
    {
        if (cost.IsZero) return;
        UInt256 delta = cost;
        UInt256 updated = _reserved.AddOrUpdate(key, UInt256.Zero, (_, existing) => existing > delta ? existing - delta : UInt256.Zero);
        if (updated.IsZero)
        {
            // Threadsafe: removes the key only while its value is still zero (mirrors DelegationCache).
            ((ICollection<KeyValuePair<AddressAsKey, UInt256>>)_reserved).Remove(
                new KeyValuePair<AddressAsKey, UInt256>(key, UInt256.Zero));
        }
    }
}
