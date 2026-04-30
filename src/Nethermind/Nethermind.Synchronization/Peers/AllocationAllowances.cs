// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.Synchronization.Peers
{
    /// <summary>
    /// Per-peer concurrent-request allowance, one byte per single-bit <see cref="AllocationContexts"/> flag.
    /// Configured once when the peer is created and read-only afterwards, so it stays a simple struct of named
    /// fields. <see cref="PeerInfo"/> packs the live counters into a single <see cref="ulong"/> internally to
    /// enable atomic compound CAS; the static helpers on this type encapsulate all the byte-slot bit math.
    /// </summary>
    public struct AllocationAllowances(byte headers, byte bodies, byte receipts, byte state, byte snap, byte forwardHeader) : IEquatable<AllocationAllowances>
    {
        public byte Headers = headers;
        public byte Bodies = bodies;
        public byte Receipts = receipts;
        public byte State = state;
        public byte Snap = snap;
        public byte ForwardHeader = forwardHeader;

        /// <summary>
        /// Minimal allowance — one slot per context. Useful for tests that exercise the binary
        /// alloc/full mechanic.
        /// </summary>
        public static AllocationAllowances Single { get; } = new(1, 1, 1, 1, 1, 1);

        /// <summary>
        /// Production default: Headers pinned to 1 (they reliably hang under higher allowances),
        /// every other context at <c>ISyncConfig.AllocationSlots</c>'s default of 2. Mirrors what
        /// <c>SyncPeerPool</c> builds for the typical config; used as the fallback for the
        /// parameterless <c>PeerInfo</c> ctor.
        /// </summary>
        public static AllocationAllowances Default { get; } = new(headers: 1, bodies: 2, receipts: 2, state: 2, snap: 2, forwardHeader: 2);

        /// <summary>Number of single-bit allocation contexts (one byte slot each).</summary>
        public const int SingleContextCount = 6;

        /// <summary>Bits per byte-slot in the packed slot word.</summary>
        public const int SlotBits = 8;

        /// <summary>Mask covering one byte-slot.</summary>
        public const ulong SlotByteMask = 0xFFul;

        /// <summary>
        /// Single-bit contexts indexed by their byte-slot position 0..<see cref="SingleContextCount"/>-1.
        /// Every helper here drives byte positions from this array — single source of truth.
        /// </summary>
        public static readonly AllocationContexts[] OrderedSingleContexts =
        [
            AllocationContexts.Headers,
            AllocationContexts.Bodies,
            AllocationContexts.Receipts,
            AllocationContexts.State,
            AllocationContexts.Snap,
            AllocationContexts.ForwardHeader,
        ];

        public byte this[AllocationContexts context]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            readonly get => context switch
            {
                AllocationContexts.Headers => Headers,
                AllocationContexts.Bodies => Bodies,
                AllocationContexts.Receipts => Receipts,
                AllocationContexts.State => State,
                AllocationContexts.Snap => Snap,
                AllocationContexts.ForwardHeader => ForwardHeader,
                _ => ThrowNotSingle(context),
            };
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                switch (context)
                {
                    case AllocationContexts.Headers: Headers = value; break;
                    case AllocationContexts.Bodies: Bodies = value; break;
                    case AllocationContexts.Receipts: Receipts = value; break;
                    case AllocationContexts.State: State = value; break;
                    case AllocationContexts.Snap: Snap = value; break;
                    case AllocationContexts.ForwardHeader: ForwardHeader = value; break;
                    default: ThrowNotSingle(context); break;
                }
            }
        }

        /// <summary>Encodes this allowance as a packed slot word (one byte per single-bit context).</summary>
        public readonly ulong Pack()
        {
            ulong packed = 0;
            for (int i = 0; i < SingleContextCount; i++)
                packed |= ((ulong)this[OrderedSingleContexts[i]]) << (i * SlotBits);
            return packed;
        }

        /// <summary>Decodes a packed slot word into named per-context bytes.</summary>
        public static AllocationAllowances Unpack(ulong packed)
        {
            AllocationAllowances result = default;
            for (int i = 0; i < SingleContextCount; i++)
                result[OrderedSingleContexts[i]] = (byte)((packed >> (i * SlotBits)) & SlotByteMask);
            return result;
        }

        /// <summary>
        /// Builds a 1-per-participating-slot delta for the packed slot word — used by
        /// <see cref="PeerInfo"/> to atomically decrement (allocate) or increment (free) only the
        /// byte slots whose context flag is set in <paramref name="contexts"/>.
        /// </summary>
        public static ulong BuildSlotDelta(AllocationContexts contexts)
        {
            ulong delta = 0;
            for (int i = 0; i < SingleContextCount; i++)
            {
                AllocationContexts ctx = OrderedSingleContexts[i];
                if ((contexts & ctx) == ctx) delta |= 1ul << (i * SlotBits);
            }
            return delta;
        }

        /// <summary>True if any byte-slot of <paramref name="value"/> is zero among the byte boundaries set in <paramref name="participating"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool HasZeroSlot(ulong value, ulong participating)
        {
            for (ulong remaining = participating; remaining != 0;)
            {
                int shift = BitOperations.TrailingZeroCount(remaining) & ~7;
                if (((value >> shift) & SlotByteMask) == 0) return true;
                remaining &= ~(SlotByteMask << shift);
            }
            return false;
        }

        /// <summary>For each participating slot, returns its low bit if value's slot is already at <paramref name="cap"/>'s value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong CarryOnSaturation(ulong value, ulong delta, ulong cap)
        {
            ulong saturated = 0;
            for (ulong remaining = delta; remaining != 0;)
            {
                int bit = BitOperations.TrailingZeroCount(remaining);
                int shift = bit & ~7;
                if (((value >> shift) & SlotByteMask) >= ((cap >> shift) & SlotByteMask))
                    saturated |= 1ul << bit;
                remaining &= ~(SlotByteMask << shift);
            }
            return saturated;
        }

        /// <summary>True if any single-bit context in <paramref name="contexts"/> has at least one slot taken (<c>available[i] &lt; cap[i]</c>).</summary>
        public static bool AnyAllocated(ulong available, ulong cap, AllocationContexts contexts)
        {
            for (int i = 0; i < SingleContextCount; i++)
            {
                AllocationContexts ctx = OrderedSingleContexts[i];
                if ((contexts & ctx) != ctx) continue;
                int shift = i * SlotBits;
                if (((available >> shift) & SlotByteMask) < ((cap >> shift) & SlotByteMask))
                    return true;
            }
            return false;
        }

        /// <summary>True if any single-bit context in <paramref name="contexts"/> has no remaining slots.</summary>
        public static bool AnyFull(ulong available, AllocationContexts contexts)
        {
            for (int i = 0; i < SingleContextCount; i++)
            {
                AllocationContexts ctx = OrderedSingleContexts[i];
                if ((contexts & ctx) != ctx) continue;
                if (((available >> (i * SlotBits)) & SlotByteMask) == 0)
                    return true;
            }
            return false;
        }

        /// <summary>Union of single-bit contexts whose slot is currently taken (<c>available[i] &lt; cap[i]</c>).</summary>
        public static AllocationContexts AllocatedFrom(ulong available, ulong cap)
        {
            AllocationContexts result = AllocationContexts.None;
            for (int i = 0; i < SingleContextCount; i++)
            {
                int shift = i * SlotBits;
                if (((available >> shift) & SlotByteMask) < ((cap >> shift) & SlotByteMask))
                    result |= OrderedSingleContexts[i];
            }
            return result;
        }

        public readonly bool Equals(AllocationAllowances other) =>
            Headers == other.Headers && Bodies == other.Bodies && Receipts == other.Receipts &&
            State == other.State && Snap == other.Snap && ForwardHeader == other.ForwardHeader;

        public readonly override bool Equals(object? obj) => obj is AllocationAllowances other && Equals(other);

        public readonly override int GetHashCode() => HashCode.Combine(Headers, Bodies, Receipts, State, Snap, ForwardHeader);

        public static bool operator ==(AllocationAllowances left, AllocationAllowances right) => left.Equals(right);
        public static bool operator !=(AllocationAllowances left, AllocationAllowances right) => !left.Equals(right);

        [DoesNotReturn]
        private static byte ThrowNotSingle(AllocationContexts context) =>
            throw new ArgumentOutOfRangeException(nameof(context), context, "Expected a single allocation context flag.");
    }
}
