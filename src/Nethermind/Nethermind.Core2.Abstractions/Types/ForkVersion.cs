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
using System.Runtime.InteropServices;

namespace Nethermind.Core2.Types
{
    public struct ForkVersion : IEquatable<ForkVersion>
    {
        public const int Length = sizeof(uint);
        public static ForkVersion Zero = new ForkVersion();

        private readonly uint _number;

        public ForkVersion(ReadOnlySpan<byte> span)
        {
            if (span.Length != sizeof(uint))
            {
                throw new ArgumentOutOfRangeException(nameof(span), span.Length,
                    $"{nameof(ForkVersion)} must have exactly {sizeof(uint)} bytes");
            }

            _number = MemoryMarshal.Cast<byte, uint>(span)[0];
        }

        public ReadOnlySpan<byte> AsSpan()
        {
            return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref this, 1));
        }

        public bool Equals(ForkVersion other)
        {
            return _number == other._number;
        }

        public override bool Equals(object? obj)
        {
            return obj is ForkVersion other && Equals(other);
        }

        public override int GetHashCode()
        {
            return _number.GetHashCode();
        }

        public static bool operator ==(ForkVersion a, ForkVersion b)
        {
            return a._number == b._number;
        }

        public static bool operator !=(ForkVersion a, ForkVersion b)
        {
            return !(a == b);
        }

        public override string ToString()
        {
            return AsSpan().ToHexString(true);
        }
    }
}