// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.TxPool;

/// <summary>Sums the pending maximum cost reserved per frame-transaction payer, bounding each payer's mempool exposure to its balance (EIP-8141).</summary>
internal sealed class PayerExposureCache
{
    private readonly ConcurrentDictionary<AddressAsKey, UInt256> _reserved = new();

    public UInt256 GetReserved(AddressAsKey key) => _reserved.TryGetValue(key, out UInt256 reserved) ? reserved : UInt256.Zero;

#if DEBUG
    /// <summary>Every reservation currently held, for the owning pool's bookkeeping check.</summary>
    public IEnumerable<KeyValuePair<AddressAsKey, UInt256>> Reservations => _reserved;
#endif

    /// <summary>Atomically reserves <paramref name="cost"/> if the summed reservation stays within <paramref name="balance"/>.</summary>
    /// <param name="reserved">On rejection, the total observed at the decision point, for diagnostics; zero otherwise.</param>
    /// <param name="replaced">The reservation of a displaced pending transaction, excluded from the bound but still held; its release runs on removal.</param>
    public bool TryReserve(AddressAsKey key, in UInt256 cost, in UInt256 balance, out UInt256 reserved, in UInt256 replaced = default)
    {
        // A zero reservation would leave an entry Subtract never reclaims.
        if (cost.IsZero)
        {
            reserved = UInt256.Zero;
            return true;
        }

        while (true)
        {
            if (_reserved.TryGetValue(key, out UInt256 existing))
            {
                UInt256 bound = existing > replaced ? existing - replaced : UInt256.Zero;
                if (UInt256.AddOverflow(existing, cost, out UInt256 updated) || bound + cost > balance)
                {
                    reserved = existing;
                    return false;
                }
                if (_reserved.TryUpdate(key, updated, existing))
                {
                    reserved = UInt256.Zero;
                    return true;
                }
            }
            else
            {
                if (cost > balance)
                {
                    reserved = UInt256.Zero;
                    return false;
                }
                if (_reserved.TryAdd(key, cost))
                {
                    // Tracked on add/remove only: an idle pool must read zero, so a floor above it is a leak.
                    Interlocked.Increment(ref Metrics.FrameTxPayersWithReservedExposure);
                    reserved = UInt256.Zero;
                    return true;
                }
            }
        }
    }

    /// <summary>Releases a reserved <paramref name="cost"/>, clamping at zero so a double release cannot re-open the gate.</summary>
    public void Subtract(AddressAsKey key, in UInt256 cost)
    {
        if (cost.IsZero) return;

        while (_reserved.TryGetValue(key, out UInt256 existing))
        {
            UInt256 updated = existing > cost ? existing - cost : UInt256.Zero;
            if (!updated.IsZero)
            {
                if (_reserved.TryUpdate(key, updated, existing)) return;
                continue;
            }

            if (RemoveTracked(key, existing)) return;
        }
    }

    /// <summary>Releases the reservations held when the owning pool is torn down.</summary>
    /// <remarks>Nothing stops a submission already in flight from reserving after this, so it bounds the leak rather than closing it.</remarks>
    public void Clear()
    {
        // Unconditional, unlike Subtract: TryAdd is the only increment, so retiring an entry at whatever
        // value it now holds still retires exactly one increment.
        foreach (KeyValuePair<AddressAsKey, UInt256> entry in _reserved)
        {
            if (_reserved.TryRemove(entry.Key, out _))
            {
                Interlocked.Decrement(ref Metrics.FrameTxPayersWithReservedExposure);
            }
        }
    }

    /// <summary>Removes <paramref name="key"/> only while its reservation is still <paramref name="expected"/>, so a racing reserve is never dropped.</summary>
    private bool RemoveTracked(AddressAsKey key, in UInt256 expected)
    {
        if (!((ICollection<KeyValuePair<AddressAsKey, UInt256>>)_reserved).Remove(
                new KeyValuePair<AddressAsKey, UInt256>(key, expected)))
        {
            return false;
        }

        Interlocked.Decrement(ref Metrics.FrameTxPayersWithReservedExposure);
        return true;
    }
}
