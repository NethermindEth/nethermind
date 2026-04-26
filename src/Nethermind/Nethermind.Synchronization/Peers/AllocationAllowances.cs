// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nethermind.Synchronization.Peers
{
    /// <summary>
    /// Per-peer concurrent-request allowance, one byte per single-bit <see cref="AllocationContexts"/> flag.
    /// Configured once when the peer is created and read-only afterwards, so it stays a simple struct of named
    /// fields. <see cref="PeerInfo"/> packs the live counters into a single <see cref="ulong"/> internally to
    /// enable atomic compound CAS — that is a private implementation detail of <see cref="PeerInfo"/>.
    /// </summary>
    public struct AllocationAllowances(byte headers, byte bodies, byte receipts, byte state, byte snap, byte forwardHeader) : IEquatable<AllocationAllowances>
    {
        public byte Headers = headers;
        public byte Bodies = bodies;
        public byte Receipts = receipts;
        public byte State = state;
        public byte Snap = snap;
        public byte ForwardHeader = forwardHeader;

        public static AllocationAllowances Default { get; } = new(1, 1, 1, 1, 1, 1);

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
