// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.Synchronization.Peers
{
    /// <summary>
    /// Bit math for the per-peer weakness counter — a packed <see cref="uint"/> with one 4-bit nibble
    /// per tracked context (single-bit contexts plus the composite <see cref="AllocationContexts.Blocks"/>).
    /// Each nibble counts consecutive weak responses for that context, saturating at
    /// <see cref="WeaknessNibbleMask"/>; once it reaches <c>SleepThreshold</c> the peer is put to sleep
    /// for that context. <see cref="PeerInfo"/> holds the storage; the math lives here.
    /// </summary>
    internal static class WeaknessTracking
    {
        /// <summary>Number of contexts that have a weakness counter (single-bit + Blocks composite).</summary>
        public const int TrackedContextCount = 8;

        /// <summary>Bits per nibble in the packed weakness word.</summary>
        public const int WeaknessBits = 4;

        /// <summary>Mask covering one weakness nibble.</summary>
        public const uint WeaknessNibbleMask = 0xFu;

        /// <summary>Nibble/slot index of a single tracked <paramref name="context"/> — the fixed inverse of <see cref="ContextAt"/>.</summary>
        /// <remarks>
        /// Each single-bit context sits at the nibble matching its bit index (<see cref="AllocationContexts.Headers"/>=0 …
        /// <see cref="AllocationContexts.BlockAccessLists"/>=6); the composite <see cref="AllocationContexts.Blocks"/>
        /// takes the last slot. No allocation slot exists for <see cref="AllocationContexts.BlockAccessLists"/>/<see cref="AllocationContexts.Blocks"/>, but both participate in weakness.
        /// </remarks>
        public static int IndexOf(AllocationContexts context) =>
            context == AllocationContexts.Blocks
                ? TrackedContextCount - 1
                : BitOperations.TrailingZeroCount((uint)context);

        /// <summary>Tracked context stored at nibble/slot <paramref name="index"/> — the fixed inverse of <see cref="IndexOf"/>.</summary>
        public static AllocationContexts ContextAt(int index) =>
            index == TrackedContextCount - 1
                ? AllocationContexts.Blocks
                : (AllocationContexts)(1u << index);

        /// <summary>Builds a 1-per-participating-nibble delta for the packed weakness word.</summary>
        public static uint BuildDelta(AllocationContexts contexts)
        {
            uint delta = 0;
            for (int i = 0; i < TrackedContextCount; i++)
            {
                AllocationContexts ctx = ContextAt(i);
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
                AllocationContexts ctx = ContextAt(i);
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
                AllocationContexts ctx = ContextAt(i);
                if ((requested & ctx) == ctx)
                    clearMask |= WeaknessNibbleMask << (i * WeaknessBits);
            }
            return clearMask;
        }
    }
}
