// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using static Nethermind.Core.Caching.SeqlockHeader;

namespace Nethermind.Core.Caching;

public sealed partial class AssociativeCache<TKey, TValue>
{
    /// <remarks>
    /// Such an entry is being written with a key of this tag, usually a new value for the key looked up. Reporting a
    /// miss for a present key would make callers reload and re-cache it as another instance.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static partial void SettleRead(ref Entry entry, long expectedTag, ref bool settled, ref TKey key, ref TValue? value)
    {
        if (!settled) settled = TryReadSettled(ref entry, expectedTag, out key, out value);
    }

    /// <summary>Spins briefly for an entry found locked or changed mid-read to settle, then reads its key and value.</summary>
    /// <returns>
    /// <see langword="false"/> when the entry no longer carries <paramref name="expectedTag"/>, or has not settled
    /// before the spin would start yielding.
    /// </returns>
    /// <remarks>
    /// A write holds the entry for a few stores, so spinning covers it; a writer preempted mid-write costs the reader
    /// a miss rather than a wait for that thread to be scheduled again. Kept out of line so the uncontended read path
    /// stays as small as before.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryReadSettled(ref Entry entry, long expectedTag, out TKey key, out TValue? value)
    {
        SpinWait spin = default;
        do
        {
            spin.SpinOnce();
            long h1 = Volatile.Read(ref entry.Header);
            if ((h1 & TagMask) != expectedTag) break;

            if ((h1 & LockMarker) == 0)
            {
                // Prevent ARM64 from reordering Key/Value loads before the seqlock header read.
                if (!Sse.IsSupported) Interlocked.MemoryBarrier();
                key = entry.Key;
                value = entry.Value;
                // Prevent ARM64 from reordering the trailing seq re-read before Key/Value loads.
                if (!Sse.IsSupported) Interlocked.MemoryBarrier();

                if (Volatile.Read(ref entry.Header) == h1) return true;
            }
        }
        while (!spin.NextSpinWillYield);

        key = default;
        value = null;
        return false;
    }
}
