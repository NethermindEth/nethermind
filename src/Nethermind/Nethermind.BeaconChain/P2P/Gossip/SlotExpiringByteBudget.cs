// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BeaconChain.P2P.Gossip;

/// <summary>A byte budget whose charges are given back by their holder or expire a number of slots after the slot they were taken in; safe to call from several network threads.</summary>
internal sealed class SlotExpiringByteBudget(long maxBytes, ulong lifetimeSlots)
{
    private readonly Lock _lock = new();
    private readonly Queue<Charge> _charges = new();
    private long _bytes;

    /// <summary>The bytes charged and neither given back nor expired.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Charges <paramref name="bytes"/> at <paramref name="currentSlot"/>.</summary>
    /// <returns>The action that gives the charge back, which runs at most once, or <c>null</c> when the budget cannot hold <paramref name="bytes"/>.</returns>
    public Action? TryCharge(long bytes, ulong currentSlot)
    {
        lock (_lock)
        {
            // Charges are taken in slot order, so the oldest is the first to expire.
            while (_charges.TryPeek(out Charge? oldest) && (oldest.IsReleased || currentSlot > oldest.Slot + lifetimeSlots))
            {
                _charges.Dequeue().Release();
            }

            if (bytes > maxBytes - Bytes)
            {
                return null;
            }

            Charge charge = new(this, bytes, currentSlot);
            Interlocked.Add(ref _bytes, bytes);
            _charges.Enqueue(charge);
            return charge.Release;
        }
    }

    private sealed class Charge(SlotExpiringByteBudget owner, long bytes, ulong slot)
    {
        private int _released;

        public ulong Slot => slot;
        public bool IsReleased => Volatile.Read(ref _released) != 0;

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Interlocked.Add(ref owner._bytes, -bytes);
            }
        }
    }
}
