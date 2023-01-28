// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;

namespace Nethermind.Network
{
    public readonly struct ForkId : IEquatable<ForkId>
    {
        public ForkId(byte[] forkHash, ulong next)
        {
            ForkHash = forkHash;
            Next = next;
        }

        public byte[] ForkHash { get; }

        public ulong Next { get; }

        public bool Equals(ForkId other)
        {
            return Bytes.AreEqual(ForkHash, other.ForkHash) && Next == other.Next;
        }

        public override bool Equals(object obj)
        {
            return obj is ForkId other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(ForkHash.GetSimplifiedHashCode(), Next);
        }

        public override string ToString()
        {
            return $"{ForkHash.ToHexString()} {Next}";
        }
    }
}
