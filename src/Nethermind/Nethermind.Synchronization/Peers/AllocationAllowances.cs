// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.Synchronization.Peers
{
    /// <summary>
    /// Per-peer concurrent-request allowance for each single-bit <see cref="AllocationContexts"/> flag.
    /// Stored as six bytes packed into a single <see cref="ulong"/> so the whole state can be read or
    /// compare-exchanged in a single 64-bit instruction.
    /// </summary>
    public struct AllocationAllowances(byte headers, byte bodies, byte receipts, byte state, byte snap, byte forwardHeader) : IEquatable<AllocationAllowances>
    {
        public const int BitsPerSlot = 8;

        internal ulong Packed = ((ulong)headers << (0 * BitsPerSlot))
                              | ((ulong)bodies << (1 * BitsPerSlot))
                              | ((ulong)receipts << (2 * BitsPerSlot))
                              | ((ulong)state << (3 * BitsPerSlot))
                              | ((ulong)snap << (4 * BitsPerSlot))
                              | ((ulong)forwardHeader << (5 * BitsPerSlot));

        public byte Headers { readonly get => Get(0); set => Set(0, value); }
        public byte Bodies { readonly get => Get(1); set => Set(1, value); }
        public byte Receipts { readonly get => Get(2); set => Set(2, value); }
        public byte State { readonly get => Get(3); set => Set(3, value); }
        public byte Snap { readonly get => Get(4); set => Set(4, value); }
        public byte ForwardHeader { readonly get => Get(5); set => Set(5, value); }

        public static AllocationAllowances Default { get; } = new(1, 1, 1, 1, 1, 1);

        /// <summary>
        /// Indexer for single-bit allocation contexts. Pre-check with <see cref="PeerInfo.IsOnlyOneContext"/>;
        /// passing a multi-bit value silently aliases to the lowest set bit's slot.
        /// </summary>
        public byte this[AllocationContexts context]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            readonly get
            {
                Debug.Assert(IsSingleBit(context), "AllocationAllowances indexer requires a single-bit context.");
                return Get(BitOperations.Log2((uint)context));
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                Debug.Assert(IsSingleBit(context), "AllocationAllowances indexer requires a single-bit context.");
                Set(BitOperations.Log2((uint)context), value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly byte Get(int slotIndex) => (byte)(Packed >> (slotIndex * BitsPerSlot));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Set(int slotIndex, byte value)
        {
            int shift = slotIndex * BitsPerSlot;
            Packed = (Packed & ~(0xFFul << shift)) | ((ulong)value << shift);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsSingleBit(AllocationContexts ctx) => ctx != 0 && ((uint)ctx & ((uint)ctx - 1)) == 0;

        public readonly bool Equals(AllocationAllowances other) => Packed == other.Packed;
        public readonly override bool Equals(object? obj) => obj is AllocationAllowances o && Equals(o);
        public readonly override int GetHashCode() => Packed.GetHashCode();
        public static bool operator ==(AllocationAllowances left, AllocationAllowances right) => left.Packed == right.Packed;
        public static bool operator !=(AllocationAllowances left, AllocationAllowances right) => left.Packed != right.Packed;
    }
}
