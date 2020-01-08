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
    public struct Domain : IEquatable<Domain>
    {
        public const int Length = 8;

        private readonly ulong _value;

        public Domain(ReadOnlySpan<byte> span)
        {
            if (span.Length != Length)
            {
                throw new ArgumentOutOfRangeException(nameof(span), span.Length, $"{nameof(Domain)} must have exactly {Length} bytes");
            }
            _value = BitConverter.ToUInt32(span);
        }

        public static explicit operator Domain(byte[] bytes) => new Domain(bytes);

        public static explicit operator Domain(Span<byte> span) => new Domain(span);

        public static explicit operator Domain(ReadOnlySpan<byte> span) => new Domain(span);

        public static explicit operator ReadOnlySpan<byte>(Domain item) => item.AsSpan();

        public static bool operator !=(Domain left, Domain right)
        {
            return !(left == right);
        }

        public static bool operator ==(Domain left, Domain right)
        {
            return left.Equals(right);
        }

        public ReadOnlySpan<byte> AsSpan()
        {
            return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref this, 1));
        }

        public override bool Equals(object? obj)
        {
            return obj is Domain type && Equals(type);
        }

        public bool Equals(Domain other)
        {
            return _value == other._value;
        }

        public override int GetHashCode()
        {
            return _value.GetHashCode();
        }

        public override string ToString()
        {
            return BitConverter.ToString(AsSpan().ToArray()).Replace("-", "");
        }
    }
}
