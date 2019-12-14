//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Nethermind.Core2.Types
{
    public struct ForkVersion : IEquatable<ForkVersion>
    {
        public const int SszLength = sizeof(uint);

        [Obsolete("Fork version is not a number; it is a 4-byte sequence. While internally it may be stored as a number, there is no specification how to convert to a number, i.e. little-endian or big-endian.")]
        public ForkVersion(uint number)
        {
            Number = number;
        }

        [Obsolete("Fork version is not a number; it is a 4-byte sequence. While internally it may be stored as a number, there is no specification how to convert to a number, i.e. little-endian or big-endian.")]
        public uint Number { get; }

        public ReadOnlySpan<byte> AsSpan()
        {
            return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref this, 1));
        }
        
        public ForkVersion(Span<byte> span)
        {
            if (span.Length != sizeof(uint))
            {
                throw new ArgumentOutOfRangeException(nameof(span), span.Length, $"{nameof(ForkVersion)} must have exactly {sizeof(uint)} bytes");
            }
            
            Number = MemoryMarshal.Cast<byte, uint>(span)[0];
        }

        public static bool operator ==(ForkVersion a, ForkVersion b)
        {
            return a.Number == b.Number;
        }

        public static bool operator !=(ForkVersion a, ForkVersion b)
        {
            return !(a == b);
        }

        public bool Equals(ForkVersion other)
        {
            return Number == other.Number;
        }

        public override bool Equals(object? obj)
        {
            return obj is ForkVersion other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Number.GetHashCode();
        }

        public override string ToString()
        {
            return BitConverter.ToString(AsSpan().ToArray()).Replace("-", "");
        }
    }
}