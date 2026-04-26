// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nethermind.Synchronization.Peers
{
    /// <summary>
    /// Per-peer allocation slot counts, one byte per single <see cref="AllocationContexts"/> flag.
    /// Mutable; callers serialise access externally (PeerInfo's methods are <see cref="MethodImplOptions.Synchronized"/>).
    /// </summary>
    public struct AllocationAllowances : IEquatable<AllocationAllowances>
    {
        public byte Headers;
        public byte Bodies;
        public byte Receipts;
        public byte State;
        public byte Snap;

        public AllocationAllowances(byte headers, byte bodies, byte receipts, byte state, byte snap)
        {
            Headers = headers;
            Bodies = bodies;
            Receipts = receipts;
            State = state;
            Snap = snap;
        }

        public static AllocationAllowances Default { get; } = new(1, 1, 1, 1, 1);

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
                    default: ThrowNotSingle(context); break;
                }
            }
        }

        public readonly bool Equals(AllocationAllowances other) =>
            Headers == other.Headers && Bodies == other.Bodies && Receipts == other.Receipts &&
            State == other.State && Snap == other.Snap;

        public readonly override bool Equals(object? obj) => obj is AllocationAllowances other && Equals(other);

        public readonly override int GetHashCode() => HashCode.Combine(Headers, Bodies, Receipts, State, Snap);

        public static bool operator ==(AllocationAllowances left, AllocationAllowances right) => left.Equals(right);
        public static bool operator !=(AllocationAllowances left, AllocationAllowances right) => !left.Equals(right);

        [DoesNotReturn]
        private static byte ThrowNotSingle(AllocationContexts context) =>
            throw new ArgumentOutOfRangeException(nameof(context), context, "Expected a single allocation context flag.");
    }
}
