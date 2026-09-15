// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.TxPool;

/// <summary>
/// Per-sender ledger of MATCHA width: the budget an EIP-8250 keyed-nonce sender earns from the gas its
/// included frame transactions paid and spends on every pending admission beyond its free baseline.
/// </summary>
/// <remarks>
/// A sender is present only while its width is positive, so an idle pool reads empty and the sender gauge
/// is a leak detector, as in <see cref="PayerExposureCache"/>. Width saturates at
/// <see cref="UInt256.MaxValue"/> rather than wrapping. Each charge is remembered by transaction hash until
/// the pool refunds it, so a transaction that leaves the pool returns exactly what its admission took, once.
/// </remarks>
internal sealed class SenderWidthCache
{
    private readonly ConcurrentDictionary<AddressAsKey, UInt256> _width = new();
    private readonly ConcurrentDictionary<ValueHash256, Charge> _charges = new();

    public UInt256 GetWidth(AddressAsKey sender) => _width.TryGetValue(sender, out UInt256 width) ? width : UInt256.Zero;

    /// <summary>Credits <paramref name="sender"/> with the width that <paramref name="finalizedGas"/> earns.</summary>
    public void Earn(AddressAsKey sender, in UInt256 finalizedGas) => Credit(sender, WidthFor(finalizedGas));

    /// <summary>
    /// Atomically deducts <paramref name="cost"/> from <paramref name="sender"/>'s width, or leaves it
    /// untouched and returns <c>false</c> when the width does not cover the cost.
    /// </summary>
    public bool TrySpend(AddressAsKey sender, in UInt256 cost)
    {
        if (cost.IsZero) return true;

        while (_width.TryGetValue(sender, out UInt256 existing))
        {
            if (existing < cost) return false;

            UInt256 updated = existing - cost;
            if (!updated.IsZero)
            {
                if (_width.TryUpdate(sender, updated, existing)) return true;
                continue;
            }

            if (RemoveTracked(sender, existing)) return true;
        }

        return false;
    }

    /// <summary>Returns <paramref name="cost"/> to <paramref name="sender"/>'s width.</summary>
    public void Refund(AddressAsKey sender, in UInt256 cost) => Credit(sender, cost);

    /// <summary>
    /// Remembers that admitting <paramref name="hash"/> spent <paramref name="cost"/> of
    /// <paramref name="sender"/>'s width, so its departure from the pool can refund it.
    /// </summary>
    public void RecordCharge(in ValueHash256 hash, AddressAsKey sender, in UInt256 cost) =>
        _charges[hash] = new Charge(sender, cost);

    /// <summary>Refunds the width admitting <paramref name="hash"/> spent, if any; a second call for the same hash refunds nothing.</summary>
    public void RefundCharge(in ValueHash256 hash)
    {
        if (_charges.TryRemove(hash, out Charge charge)) Refund(charge.Sender, charge.Cost);
    }

    /// <summary>Drops every balance and charge when the owning pool is torn down.</summary>
    /// <remarks>Nothing stops a submission already in flight from spending after this, so it bounds the leak rather than closing it.</remarks>
    public void Clear()
    {
        _charges.Clear();
        // Unconditional, unlike TrySpend: TryAdd is the only increment, so retiring an entry at whatever
        // value it now holds still retires exactly one increment.
        foreach (KeyValuePair<AddressAsKey, UInt256> entry in _width)
        {
            if (_width.TryRemove(entry.Key, out _))
            {
                Interlocked.Decrement(ref Metrics.FrameTxSendersWithWidth);
            }
        }
    }

    /// <summary>The width a quantity of finalized gas earns: the single place the exchange rate lives.</summary>
    private static UInt256 WidthFor(in UInt256 finalizedGas) => finalizedGas;

    private void Credit(AddressAsKey sender, in UInt256 amount)
    {
        // A zero balance would leave an entry TrySpend never reclaims.
        if (amount.IsZero) return;

        while (true)
        {
            if (_width.TryGetValue(sender, out UInt256 existing))
            {
                if (UInt256.AddOverflow(existing, amount, out UInt256 updated)) updated = UInt256.MaxValue;
                if (_width.TryUpdate(sender, updated, existing)) return;
            }
            else if (_width.TryAdd(sender, amount))
            {
                // Tracked on add/remove only: an idle pool must read zero, so a floor above it is a leak.
                Interlocked.Increment(ref Metrics.FrameTxSendersWithWidth);
                return;
            }
        }
    }

    /// <summary>Removes <paramref name="sender"/> only while its width is still <paramref name="expected"/>, so a racing credit is never dropped.</summary>
    private bool RemoveTracked(AddressAsKey sender, in UInt256 expected)
    {
        if (!((ICollection<KeyValuePair<AddressAsKey, UInt256>>)_width).Remove(
                new KeyValuePair<AddressAsKey, UInt256>(sender, expected)))
        {
            return false;
        }

        Interlocked.Decrement(ref Metrics.FrameTxSendersWithWidth);
        return true;
    }

    private readonly record struct Charge(AddressAsKey Sender, UInt256 Cost);
}
