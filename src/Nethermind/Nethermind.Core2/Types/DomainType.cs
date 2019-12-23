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
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Nethermind.Core2.Types
{
    public struct DomainType : IEquatable<DomainType>
    {
        public const int SszLength = sizeof(uint);

        private readonly uint _number;

        public ReadOnlySpan<byte> AsSpan()
        {
            // Or if we always want little endian internally; need to change both this and constructor
            // Span<byte> destination = new Span<byte>(new byte[SszLength]);
            // BinaryPrimitives.WriteUInt32LittleEndian(destination, _number);
            // NOTE: Using native (i.e. ignoring endianness) is probably better because AsSpan() is zero alloc
            // (just read-only pointer to memory). The constructor is ByValue single alloc.
            return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref this, 1));
        }

        public DomainType(Span<byte> span)
        {
            if (span.Length != sizeof(uint))
            {
                throw new ArgumentOutOfRangeException(nameof(span), span.Length,
                    $"{nameof(ForkVersion)} must have exactly {sizeof(uint)} bytes");
            }

            // Or if we always want little endian internally; need to change both this and AsSpan()
            // BinaryPrimitives.ReadUInt32LittleEndian(span);
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
            return BitConverter.ToString(AsSpan().ToArray()).Replace("-", "");
        }
    }
}