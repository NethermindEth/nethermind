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
/// the process, so the ledger holds at most <c>maxSenders</c>. To admit a new earner into a full ledger it
/// samples a bounded window that rotates through the whole ledger, and replaces the window's smallest balance
/// only when the newcomer's balance exceeds it, so flooding the ledger with cheap senders never displaces a
/// larger balance. Eviction only ever removes width, never grants it, so the bound fails safe: an evicted or
/// refused sender falls back to its free baseline. Credits come from the single finalization thread, which is
/// the only user of the rotating window. New earnings stop at the caller's cap and otherwise saturate at
/// <see cref="UInt256.MaxValue"/> rather than wrapping; a cap lowered later never shrinks a balance already earned.
/// A second instance holds the width of EIP-8141 paymasters under the same rules, reported on its own gauge.
/// Width an admission holds through <see cref="TryReserve"/> keeps its holder in the ledger, even at a zero balance,
/// and out of eviction until <see cref="Release"/>, so a refund always finds the entry it was taken from and never
/// adds one.
/// </remarks>
internal sealed class SenderWidthCache(int maxSenders = SenderWidthCache.DefaultMaxSenders, bool holdsPaymasters = false)
{
    public const int DefaultMaxSenders = 1 << 16;
    private const int EvictionSample = 32;

    private readonly ConcurrentDictionary<AddressAsKey, Holding> _width = new();
    private int _count;
    private IEnumerator<KeyValuePair<AddressAsKey, Holding>>? _evictionWindow;

    public int Count => Volatile.Read(ref _count);

    private ref long HoldersWithWidth => ref holdsPaymasters ? ref Metrics.FrameTxPaymastersWithWidth : ref Metrics.FrameTxSendersWithWidth;

    public UInt256 GetWidth(AddressAsKey sender) => _width.TryGetValue(sender, out Holding holding) ? holding.Width : UInt256.Zero;

    /// <summary>
    /// Credits <paramref name="sender"/> with the width that <paramref name="finalizedGas"/> earns, up to
    /// <paramref name="widthCap"/>. A balance already at or above the cap is left as is. A zero cap lifts the ceiling.
    /// </summary>
    public void Earn(AddressAsKey sender, in UInt256 finalizedGas, in UInt256 widthCap = default) => Credit(sender, WidthFor(finalizedGas), widthCap);

    /// <summary>
    /// Atomically deducts <paramref name="cost"/> from <paramref name="sender"/>'s width, or leaves it
    /// untouched and returns <c>false</c> when the width does not cover the cost.
    /// </summary>
    public bool TrySpend(AddressAsKey sender, in UInt256 cost)
    {
        if (cost.IsZero) return true;

        while (_width.TryGetValue(sender, out Holding existing))
        {
            if (existing.Width < cost) return false;
            if (Replace(sender, existing, existing with { Width = existing.Width - cost })) return true;
        }

        return false;
    }

    /// <summary>
    /// Atomically takes <paramref name="cost"/> from <paramref name="holder"/>'s width for an admission still in
    /// flight, or leaves it untouched and returns <c>false</c> when the width does not cover the cost.
    /// </summary>
    /// <remarks>Every successful call with a non-zero cost must be paired with one <see cref="Release"/>.</remarks>
    public bool TryReserve(AddressAsKey holder, in UInt256 cost)
    {
        if (cost.IsZero) return true;

        while (_width.TryGetValue(holder, out Holding existing))
        {
            if (existing.Width < cost) return false;
            if (_width.TryUpdate(holder, new Holding(existing.Width - cost, existing.Reservations + 1), existing)) return true;
        }

        return false;
    }

    /// <summary>
    /// Ends one reservation taken by <see cref="TryReserve"/>, giving <paramref name="refund"/> of it back to
    /// <paramref name="holder"/>: nothing when the admission went through, the whole cost when it did not.
    /// </summary>
    /// <remarks>The refund ignores the cap, since the amount was already held.</remarks>
    public void Release(AddressAsKey holder, in UInt256 refund)
    {
        while (_width.TryGetValue(holder, out Holding existing) && existing.Reservations > 0)
        {
            if (UInt256.AddOverflow(existing.Width, refund, out UInt256 width)) width = UInt256.MaxValue;
            if (Replace(holder, existing, new Holding(width, existing.Reservations - 1))) return;
        }
    }

    /// <summary>Drops every balance when the owning pool is torn down.</summary>
    /// <remarks>Nothing stops a submission already in flight from spending after this, so it bounds the leak rather than closing it.</remarks>
    public void Clear()
    {
        foreach (KeyValuePair<AddressAsKey, Holding> entry in _width)
        {
            if (_width.TryRemove(entry.Key, out _))
            {
                Untrack();
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
            if (_width.TryGetValue(sender, out Holding existing))
            {
                if (capped && existing.Width >= widthCap) return;
                if (UInt256.AddOverflow(existing.Width, amount, out UInt256 updated)) updated = UInt256.MaxValue;
                if (capped && updated > widthCap) updated = widthCap;
                if (_width.TryUpdate(sender, existing with { Width = updated }, existing)) return;
            }
            else
            {
                UInt256 seeded = capped && amount > widthCap ? widthCap : amount;
                if (Count >= maxSenders && !TryEvictFor(seeded)) return;
                if (_width.TryAdd(sender, new Holding(seeded, 0)))
                {
                    Interlocked.Increment(ref _count);
                    Interlocked.Increment(ref HoldersWithWidth);
                    return;
                }
            }
        }
    }

    private bool TryEvictFor(in UInt256 newcomer)
    {
        KeyValuePair<AddressAsKey, Holding> victim = default;
        bool found = false;
        bool restarted = false;
        int sampled = 0;
        while (sampled < EvictionSample)
        {
            _evictionWindow ??= _width.GetEnumerator();
            if (!_evictionWindow.MoveNext())
            {
                _evictionWindow.Dispose();
                _evictionWindow = null;
                if (restarted) break;
                restarted = true;
                continue;
            }

            KeyValuePair<AddressAsKey, Holding> entry = _evictionWindow.Current;
            if (entry.Value.Reservations == 0 && (!found || entry.Value.Width < victim.Value.Width))
            {
                victim = entry;
                found = true;
            }

            sampled++;
        }

        if (sampled == 0) return true;
        if (!found || newcomer <= victim.Value.Width) return false;
        return RemoveTracked(victim.Key, victim.Value) || Count < maxSenders;
    }

    private void Untrack()
    {
        Interlocked.Decrement(ref _count);
        Interlocked.Decrement(ref HoldersWithWidth);
    }

    /// <summary>Swaps <paramref name="expected"/> for <paramref name="updated"/>, dropping the entry once it holds neither width nor a reservation.</summary>
    private bool Replace(AddressAsKey sender, in Holding expected, in Holding updated) =>
        updated.Width.IsZero && updated.Reservations == 0
            ? RemoveTracked(sender, expected)
            : _width.TryUpdate(sender, updated, expected);

    /// <summary>Removes <paramref name="sender"/> only while its holding is still <paramref name="expected"/>, so a racing credit or reservation is never dropped.</summary>
    private bool RemoveTracked(AddressAsKey sender, in Holding expected)
    {
        if (!((ICollection<KeyValuePair<AddressAsKey, Holding>>)_width).Remove(
                new KeyValuePair<AddressAsKey, Holding>(sender, expected)))
        {
            return false;
        }

        Untrack();
        return true;
    }

    private readonly record struct Holding(UInt256 Width, int Reservations);
}
