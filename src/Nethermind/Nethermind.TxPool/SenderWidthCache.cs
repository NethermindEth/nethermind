// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.TxPool;

/// <summary>
/// Per-sender ledger of MATCHA width: the budget an EIP-8250 keyed-nonce sender earns from the gas its
/// finalized frame transactions paid and spends on every pending admission beyond its free baseline.
/// </summary>
/// <remarks>
/// Spent width is never returned: inclusion, invalidation, removal, expiry, or reorg do not credit it back,
/// which is what bounds repeated mass invalidation (EIP-8141 MATCHA policy). A sender leaves the ledger when
/// its width drains to zero. Finalized senders that never spend would otherwise accumulate for the life of
/// the process, so the ledger holds at most <c>maxSenders</c> and evicts an arbitrary sender to admit a new
/// earner. Eviction only ever removes width, never grants it, so the bound fails safe: an evicted sender
/// falls back to its free baseline. Earned width is held at the caller's cap and otherwise saturates at
/// <see cref="UInt256.MaxValue"/> rather than wrapping.
/// </remarks>
internal sealed class SenderWidthCache(int maxSenders = SenderWidthCache.DefaultMaxSenders)
{
    public const int DefaultMaxSenders = 1 << 16;

    private readonly ConcurrentDictionary<AddressAsKey, UInt256> _width = new();

    public int Count => _width.Count;

    public UInt256 GetWidth(AddressAsKey sender) => _width.TryGetValue(sender, out UInt256 width) ? width : UInt256.Zero;

    /// <summary>
    /// Credits <paramref name="sender"/> with the width that <paramref name="finalizedGas"/> earns, holding the
    /// balance at <paramref name="widthCap"/>. A zero cap lifts the ceiling.
    /// </summary>
    public void Earn(AddressAsKey sender, in UInt256 finalizedGas, in UInt256 widthCap = default) => Credit(sender, WidthFor(finalizedGas), widthCap);

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

    /// <summary>Drops every balance when the owning pool is torn down.</summary>
    /// <remarks>Nothing stops a submission already in flight from spending after this, so it bounds the leak rather than closing it.</remarks>
    public void Clear()
    {
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

    private void Credit(AddressAsKey sender, in UInt256 amount, in UInt256 widthCap)
    {
        if (amount.IsZero) return;

        bool capped = !widthCap.IsZero;

        while (true)
        {
            if (_width.TryGetValue(sender, out UInt256 existing))
            {
                if (UInt256.AddOverflow(existing, amount, out UInt256 updated)) updated = UInt256.MaxValue;
                if (capped && updated > widthCap) updated = widthCap;
                if (updated == existing) return;
                if (_width.TryUpdate(sender, updated, existing)) return;
            }
            else
            {
                UInt256 seeded = capped && amount > widthCap ? widthCap : amount;
                if (_width.Count >= maxSenders) EvictOne();
                if (_width.TryAdd(sender, seeded))
                {
                    Interlocked.Increment(ref Metrics.FrameTxSendersWithWidth);
                    return;
                }
            }
        }
    }

    private void EvictOne()
    {
        foreach (KeyValuePair<AddressAsKey, UInt256> entry in _width)
        {
            if (_width.TryRemove(entry.Key, out _))
            {
                Interlocked.Decrement(ref Metrics.FrameTxSendersWithWidth);
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
}
