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
    public struct DomainType : IEquatable<DomainType>
    {
        public const int Length = sizeof(uint);

        private readonly uint _number;

        public ReadOnlySpan<byte> AsSpan()
        {
            return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref this, 1));
        }

        public DomainType(Span<byte> span)
        {
            if (span.Length != Length)
            {
                throw new ArgumentOutOfRangeException(nameof(span), span.Length,
                    $"{nameof(DomainType)} must have exactly {Length} bytes");
            }
            _number = MemoryMarshal.Cast<byte, uint>(span)[0];
        }

        public static bool operator ==(DomainType a, DomainType b)
        {
            return a._number == b._number;
        }

        public static bool operator !=(DomainType a, DomainType b)
        {
            return !(a == b);
        }

        public bool Equals(DomainType other)
        {
            return _number == other._number;
        }

        public override bool Equals(object? obj)
        {
            return obj is DomainType other && Equals(other);
        }

        public override int GetHashCode()
        {
            return _number.GetHashCode();
        }

        public override string ToString()
        {
            return AsSpan().ToHexString(true);
        }
    }
}