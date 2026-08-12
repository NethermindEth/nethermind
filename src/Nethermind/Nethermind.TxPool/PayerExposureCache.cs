// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.TxPool;

/// <summary>
/// Tracks the summed pending maximum cost reserved per resolved frame-transaction payer, so that
/// admission can bound each payer's aggregate exposure to its balance.
/// </summary>
/// <remarks>
/// EIP-8141: "Reservation accounting applies to every payer, not only canonical paymasters".
/// </remarks>
internal sealed class PayerExposureCache
{
    private readonly ConcurrentDictionary<AddressAsKey, UInt256> _reserved = new();

    /// <summary>Summed pending maximum cost currently reserved for <paramref name="key"/>, or zero.</summary>
    public UInt256 GetReserved(AddressAsKey key) => _reserved.TryGetValue(key, out UInt256 reserved) ? reserved : UInt256.Zero;

    /// <summary>
    /// Atomically reserves <paramref name="cost"/> for <paramref name="key"/> if and only if the
    /// resulting summed reservation stays within <paramref name="balance"/>.
    /// </summary>
    /// <param name="reserved">
    /// On failure, the reservation observed for <paramref name="key"/> at the point the decision was
    /// made (so a trace can report the total the check actually saw); zero on success.
    /// </param>
    /// <returns>
    /// <c>true</c> if the cost was reserved; <c>false</c> (reserving nothing) if the bound would be
    /// exceeded or the addition would overflow.
    /// </returns>
    /// <remarks>
    /// The compare-and-set loop makes the read of the current reservation and the reservation itself
    /// a single atomic step, closing the check-then-act gap between concurrent admissions.
    /// </remarks>
    public bool TryReserve(AddressAsKey key, in UInt256 cost, in UInt256 balance, out UInt256 reserved)
    {
        // Mirrors Subtract: a zero reservation would leave an entry the matching release never reclaims.
        if (cost.IsZero)
        {
            reserved = UInt256.Zero;
            return true;
        }

        while (true)
        {
            if (_reserved.TryGetValue(key, out UInt256 existing))
            {
                if (UInt256.AddOverflow(existing, cost, out UInt256 updated) || updated > balance)
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
                    // Tracked on the add/remove transitions: an idle pool must read zero, so a non-zero
                    // floor is a leaked reservation rather than the bound doing its job.
                    Interlocked.Increment(ref Metrics.FrameTxPayersWithReservedExposure);
                    reserved = UInt256.Zero;
                    return true;
                }
            }
        }
    }

    /// <summary>
    /// Releases a previously reserved <paramref name="cost"/> for <paramref name="key"/>, clamping at
    /// zero rather than going negative so a double release can never re-enable the gate for a payer.
    /// </summary>
    /// <remarks>
    /// Compare-and-set like <see cref="TryReserve"/>, so a release for an unreserved payer touches
    /// nothing: inserting a transient zero would decrement the gauge without a matching increment.
    /// </remarks>
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

            // Removes the entry only while its value is still the one this release priced against.
            if (((ICollection<KeyValuePair<AddressAsKey, UInt256>>)_reserved).Remove(
                    new KeyValuePair<AddressAsKey, UInt256>(key, existing)))
            {
                Interlocked.Decrement(ref Metrics.FrameTxPayersWithReservedExposure);
                return;
            }
        }
    }
}
