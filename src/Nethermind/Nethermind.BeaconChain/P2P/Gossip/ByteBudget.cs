// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BeaconChain.P2P.Gossip;

/// <summary>A budget of bytes and of charges whose charges are given back by their holder; safe to call from several network threads.</summary>
internal sealed class ByteBudget(long maxBytes, int maxCharges)
{
    private readonly Lock _lock = new();
    private long _bytes;
    private int _charges;

    /// <summary>The bytes charged and not given back.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Charges <paramref name="bytes"/>.</summary>
    /// <returns>The charge, or <c>null</c> when the budget cannot hold <paramref name="bytes"/> or holds <c>maxCharges</c> charges already.</returns>
    public Charge? TryCharge(long bytes)
    {
        lock (_lock)
        {
            if (bytes > maxBytes - Bytes || Volatile.Read(ref _charges) >= maxCharges)
            {
                return null;
            }

            Interlocked.Add(ref _bytes, bytes);
            Interlocked.Increment(ref _charges);
            return new Charge(this, bytes);
        }
    }

    /// <summary>Bytes held against the budget until <see cref="Release"/>.</summary>
    internal sealed class Charge(ByteBudget owner, long bytes)
    {
        private int _released;

        /// <summary>Gives the bytes back; later calls do nothing.</summary>
        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Interlocked.Add(ref owner._bytes, -bytes);
                Interlocked.Decrement(ref owner._charges);
            }
        }
    }
}
