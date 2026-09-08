// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.TxPool;

/// <summary>Sums the pending maximum cost reserved per frame-transaction payer, bounding each payer's mempool exposure to its balance (EIP-8141).</summary>
/// <remarks>Per-payer totals and the individual reservations behind them move under one lock, so a replacement
/// discount cannot be granted against a reservation another thread has already released.</remarks>
internal sealed class PayerExposureCache
{
    private readonly Lock _lock = new();
    private readonly Dictionary<AddressAsKey, UInt256> _reserved = [];
    private readonly Dictionary<Hash256AsKey, Reservation> _live = [];

    private readonly record struct Reservation(AddressAsKey Payer, UInt256 Cost);

    public UInt256 GetReserved(AddressAsKey key)
    {
        lock (_lock)
        {
            return _reserved.TryGetValue(key, out UInt256 reserved) ? reserved : UInt256.Zero;
        }
    }

#if DEBUG
    /// <summary>Every reservation currently held, for the owning pool's bookkeeping check.</summary>
    public IEnumerable<KeyValuePair<AddressAsKey, UInt256>> Reservations
    {
        get
        {
            lock (_lock)
            {
                return new List<KeyValuePair<AddressAsKey, UInt256>>(_reserved);
            }
        }
    }
#endif

    /// <summary>Atomically reserves <paramref name="cost"/> for <paramref name="hash"/> if the summed reservation stays within <paramref name="balance"/>.</summary>
    /// <param name="reserved">On rejection, the total observed at the decision point, for diagnostics; zero otherwise.</param>
    /// <param name="replacedHash">A pending transaction this one would displace, whose reservation is excluded
    /// from the bound but still held; its release runs on removal.</param>
    public bool TryReserve(AddressAsKey key, Hash256 hash, in UInt256 cost, in UInt256 balance, out UInt256 reserved, Hash256? replacedHash = null)
    {
        reserved = UInt256.Zero;

        // A zero reservation would leave an entry Subtract never reclaims.
        if (cost.IsZero) return true;

        lock (_lock)
        {
            bool held = _reserved.TryGetValue(key, out UInt256 existing);

            // The discount holds only while the displaced reservation is still live: once released its room
            // is free for anyone, and counting it a second time would let the payer exceed its balance.
            UInt256 discount = replacedHash is not null
                               && _live.TryGetValue(replacedHash, out Reservation replaced)
                               && replaced.Payer.Equals(key)
                ? replaced.Cost
                : UInt256.Zero;

            UInt256 bound = existing > discount ? existing - discount : UInt256.Zero;
            if (UInt256.AddOverflow(existing, cost, out UInt256 updated) || bound + cost > balance)
            {
                reserved = existing;
                return false;
            }

            _reserved[key] = updated;
            if (!held)
            {
                // Tracked on add/remove only: an idle pool must read zero, so a floor above it is a leak.
                Interlocked.Increment(ref Metrics.FrameTxPayersWithReservedExposure);
            }

            // A hash resubmitted concurrently reserves twice; summing keeps the single release symmetric.
            _live[hash] = _live.TryGetValue(hash, out Reservation duplicate) && duplicate.Payer.Equals(key)
                ? duplicate with { Cost = duplicate.Cost + cost }
                : new Reservation(key, cost);
            return true;
        }
    }

    /// <summary>Re-takes a reservation a restored pending transaction already holds, without re-gating it against its payer's balance.</summary>
    /// <remarks>The bound was enforced when that transaction was first admitted, and rejecting it here would leave a
    /// pending record whose removal releases a reservation this ledger never took. An unbounded balance is what
    /// makes it a restore rather than a fresh admission; everything else — the zero case, the overflow guard,
    /// the payer gauge — is the reservation path's. The overflow guard cannot fire here: every restored cost was
    /// admitted against a running total this ledger already held, so re-taking them reproduces a sum that fitted once.</remarks>
    public void Restore(AddressAsKey key, Hash256 hash, in UInt256 cost) => TryReserve(key, hash, cost, UInt256.MaxValue, out _);

    /// <summary>Releases whatever <paramref name="hash"/> reserved, if anything; a repeated release is a no-op.</summary>
    public void Subtract(Hash256 hash)
    {
        lock (_lock)
        {
            if (!_live.Remove(hash, out Reservation live) || !_reserved.TryGetValue(live.Payer, out UInt256 existing))
            {
                return;
            }

            UInt256 updated = existing > live.Cost ? existing - live.Cost : UInt256.Zero;
            if (updated.IsZero)
            {
                _reserved.Remove(live.Payer);
                Interlocked.Decrement(ref Metrics.FrameTxPayersWithReservedExposure);
            }
            else
            {
                _reserved[live.Payer] = updated;
            }
        }
    }

    /// <summary>Releases the reservations held when the owning pool is torn down.</summary>
    /// <remarks>Nothing stops a submission already in flight from reserving after this, so it bounds the leak rather than closing it.</remarks>
    public void Clear()
    {
        lock (_lock)
        {
            Interlocked.Add(ref Metrics.FrameTxPayersWithReservedExposure, -_reserved.Count);
            _reserved.Clear();
            _live.Clear();
        }
    }
}
