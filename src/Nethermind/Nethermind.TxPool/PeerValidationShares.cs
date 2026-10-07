// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nethermind.TxPool;

/// <summary>
/// Fair shares of the per-head frame validation time for the peers whose frame transactions fail it.
/// </summary>
/// <remarks>
/// Every peer that submits a frame transaction during a head is active for that head, and each active peer may
/// spend at most the head budget divided by the active count on validation that ends in a rejection. Time is
/// measured, not declared, so one setting fits a fast and a slow node alike. A peer is checked before
/// validation and charged after it, so it can overshoot its share by one validation. Shares reset on every
/// head; a peer that joins later in the head shrinks the shares of those already active.
/// </remarks>
internal sealed class PeerValidationShares(long headBudgetTicks)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<object, long> _spentTicks = new(ReferenceEqualityComparer.Instance);
    private long _headGeneration = long.MinValue;

    /// <summary>Registers <paramref name="peer"/> as active for the head and reports whether it has share left.</summary>
    /// <returns><see langword="false"/> when the transaction must be dropped without validation.</returns>
    public bool HasShare(object peer, long headGeneration)
    {
        lock (_lock)
        {
            if (!TryRoll(headGeneration)) return true;
            long spent = CollectionsMarshal.GetValueRefOrAddDefault(_spentTicks, peer, out _);
            return spent < headBudgetTicks / _spentTicks.Count;
        }
    }

    /// <summary>Charges <paramref name="peer"/> the validation time of a transaction the pool then rejected.</summary>
    public void Charge(object peer, long headGeneration, long ticks)
    {
        lock (_lock)
        {
            // A charge for a head that has already passed belongs to no share.
            if (!TryRoll(headGeneration)) return;
            CollectionsMarshal.GetValueRefOrAddDefault(_spentTicks, peer, out _) += ticks;
        }
    }

    /// <summary>The validation time <paramref name="peer"/> has been charged on the current head.</summary>
    internal long SpentTicks(object peer)
    {
        lock (_lock)
        {
            return _spentTicks.GetValueOrDefault(peer);
        }
    }

    // False for a stale head, which neither resets the shares nor counts against them.
    private bool TryRoll(long headGeneration)
    {
        if (headGeneration < _headGeneration) return false;
        if (headGeneration > _headGeneration)
        {
            _spentTicks.Clear();
            _headGeneration = headGeneration;
        }

        return true;
    }
}
