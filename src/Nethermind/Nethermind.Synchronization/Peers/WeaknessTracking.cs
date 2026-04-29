// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.Synchronization.Peers
{
    /// <summary>
    /// Bit math for the per-peer weakness counter — a packed <see cref="uint"/> with one 4-bit nibble
    /// per tracked context (the six single-bit contexts plus the composite <see cref="AllocationContexts.Blocks"/>).
    /// Each nibble counts consecutive weak responses for that context, saturating at
    /// <see cref="WeaknessNibbleMask"/>; once it reaches <c>SleepThreshold</c> the peer is put to sleep
    /// for that context. <see cref="PeerInfo"/> holds the storage; the math lives here.
    /// </summary>
    internal static class WeaknessTracking
    {
        /// <summary>Number of contexts that have a weakness counter (single-bit + Blocks composite).</summary>
        public const int TrackedContextCount = 7;

        /// <summary>Bits per nibble in the packed weakness word.</summary>
        public const int WeaknessBits = 4;

        /// <summary>Mask covering one weakness nibble.</summary>
        public const uint WeaknessNibbleMask = 0xFu;

        /// <summary>
        /// Tracked contexts indexed by their nibble position 0..<see cref="TrackedContextCount"/>-1.
        /// First six entries match <see cref="AllocationAllowances.OrderedSingleContexts"/>; the trailing
        /// entry is <see cref="AllocationContexts.Blocks"/>, which has no slot but participates in weakness.
        /// </summary>
        public static readonly AllocationContexts[] OrderedTrackedContexts =
        [
            AllocationContexts.Headers,
            AllocationContexts.Bodies,
            AllocationContexts.Receipts,
            AllocationContexts.State,
            AllocationContexts.Snap,
            AllocationContexts.ForwardHeader,
            AllocationContexts.Blocks,
        ];

        /// <summary>Builds a 1-per-participating-nibble delta for the packed weakness word.</summary>
        public static uint BuildDelta(AllocationContexts contexts)
        {
            uint delta = 0;
            for (int i = 0; i < TrackedContextCount; i++)
            {
                AllocationContexts ctx = OrderedTrackedContexts[i];
                if ((contexts & ctx) == ctx) delta |= 1u << (i * WeaknessBits);
            }
            return delta;
        }

        /// <summary>Adds <paramref name="delta"/> to <paramref name="value"/> nibble-wise, clamping each nibble at <see cref="WeaknessNibbleMask"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint SaturatingAdd(uint value, uint delta)
        {
            uint result = value;
            for (uint remaining = delta; remaining != 0;)
            {
                int shift = BitOperations.TrailingZeroCount(remaining) & ~3;
                uint cur = (result >> shift) & WeaknessNibbleMask;
                uint inc = (delta >> shift) & WeaknessNibbleMask;
                uint sum = Math.Min(cur + inc, WeaknessNibbleMask);
                result = (result & ~(WeaknessNibbleMask << shift)) | (sum << shift);
                remaining &= ~(WeaknessNibbleMask << shift);
            }
            return result;
        }

        /// <summary>Union of contexts in <paramref name="requested"/> whose nibble in <paramref name="weaknesses"/> reached <paramref name="threshold"/>.</summary>
        public static AllocationContexts AtOrAboveThreshold(uint weaknesses, AllocationContexts requested, int threshold)
        {
            AllocationContexts sleeps = AllocationContexts.None;
            for (int i = 0; i < TrackedContextCount; i++)
            {
                AllocationContexts ctx = OrderedTrackedContexts[i];
                if ((requested & ctx) != ctx) continue;
                if (((weaknesses >> (i * WeaknessBits)) & WeaknessNibbleMask) >= threshold)
                    sleeps |= ctx;
            }
            return sleeps;
        }

        /// <summary>Mask covering every nibble whose context flag is set in <paramref name="requested"/> — for clearing those nibbles atomically.</summary>
        public static uint ClearMaskFor(AllocationContexts requested)
        {
            uint clearMask = 0;
            for (int i = 0; i < TrackedContextCount; i++)
            {
                AllocationContexts ctx = OrderedTrackedContexts[i];
                if ((requested & ctx) == ctx)
                    clearMask |= WeaknessNibbleMask << (i * WeaknessBits);
            }
            return clearMask;
        }
    }
}
