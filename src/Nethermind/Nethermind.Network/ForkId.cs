// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core.Extensions;

namespace Nethermind.Network
{
    public readonly struct ForkId : IEquatable<ForkId>
    {
        public ForkId(uint forkHash, ulong next)
        {
            ForkHash = forkHash;
            Next = next;
        }

        public uint ForkHash { get; }

        public ulong Next { get; }

        public byte[] HashBytes
        {
            get
            {
                byte[] hash = new byte[4];
                BinaryPrimitives.TryWriteUInt32BigEndian(hash, ForkHash);
                return hash;
            }
        }

        public bool Equals(ForkId other)
        {
            return ForkHash == other.ForkHash && Next == other.Next;
        }

        public override bool Equals(object obj)
        {
            return obj is ForkId other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(ForkHash.GetHashCode(), Next);
        }

        public override string ToString()
        {
            return $"{HashBytes.ToHexString()} {Next}";
        }
    }
}
