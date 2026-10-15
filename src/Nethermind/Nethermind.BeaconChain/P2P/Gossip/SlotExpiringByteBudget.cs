// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BeaconChain.P2P.Gossip;

/// <summary>A byte budget whose charges are given back by their holder; safe to call from several network threads.</summary>
/// <remarks>A charge expires a number of slots after the slot it was taken in only once the verdict it is bound to has ended without giving it back.</remarks>
internal sealed class SlotExpiringByteBudget(long maxBytes, ulong lifetimeSlots)
{
    private readonly Lock _lock = new();
    private Queue<Charge> _charges = new();
    private long _bytes;
    private int _held;

    /// <summary>The bytes charged and neither given back nor expired.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Charges <paramref name="bytes"/> at <paramref name="currentSlot"/>.</summary>
    /// <returns>The charge, or <c>null</c> when the budget cannot hold <paramref name="bytes"/>.</returns>
    public Charge? TryCharge(long bytes, ulong currentSlot)
    {
        lock (_lock)
        {
            // Charges are taken in slot order, so the oldest is the first to expire.
            while (_charges.TryPeek(out Charge? oldest) && (oldest.IsReleased || oldest.VerdictEnded && currentSlot > oldest.Slot + lifetimeSlots))
            {
                _charges.Dequeue().Release();
            }

            // A charge whose verdict is still pending stays at the head, so given-back charges behind it are dropped here instead.
            if (_charges.Count > 2 * Volatile.Read(ref _held) + 64)
            {
                _charges = new Queue<Charge>(_charges.Where(static charge => !charge.IsReleased));
            }

            if (bytes > maxBytes - Bytes)
            {
                return null;
            }

            Charge charge = new(this, bytes, currentSlot);
            Interlocked.Add(ref _bytes, bytes);
            Interlocked.Increment(ref _held);
            _charges.Enqueue(charge);
            return charge;
        }
    }

    /// <summary>Bytes held against the budget until <see cref="Release"/>.</summary>
    internal sealed class Charge(SlotExpiringByteBudget owner, long bytes, ulong slot)
    {
        private int _released;
        private GossipVerdict? _verdict;

        public ulong Slot => slot;
        public bool IsReleased => Volatile.Read(ref _released) != 0;
        public bool VerdictEnded => Volatile.Read(ref _verdict)?.IsCompleted == true;

        /// <summary>Ties the charge to <paramref name="verdict"/>, so it can expire only after the verdict ends.</summary>
        public void Bind(GossipVerdict verdict) => Volatile.Write(ref _verdict, verdict);

        /// <summary>Gives the bytes back; later calls do nothing.</summary>
        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Interlocked.Add(ref owner._bytes, -bytes);
                Interlocked.Decrement(ref owner._held);
            }
        }
    }
}
