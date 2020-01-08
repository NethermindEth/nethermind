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
using System.Linq;

namespace Nethermind.Core2.Types
{
    public class Bytes32 : IEquatable<Bytes32>
    {
        public const int Length = 32;

        private readonly byte[] _bytes;

        public Bytes32()
        {
            _bytes = new byte[Length];
        }

        public Bytes32(ReadOnlySpan<byte> span)
        {
            if (span.Length != Length)
            {
                throw new ArgumentOutOfRangeException(nameof(span), span.Length, $"{nameof(Bytes32)} must have exactly {Length} bytes");
            }
            _bytes = span.ToArray();
        }

        public static explicit operator Bytes32(byte[] bytes) => new Bytes32(bytes);

        public static explicit operator Bytes32(Span<byte> span) => new Bytes32(span);

        public static explicit operator Bytes32(ReadOnlySpan<byte> span) => new Bytes32(span);

        public static explicit operator ReadOnlySpan<byte>(Bytes32 value) => value.AsSpan();

        public ReadOnlySpan<byte> AsSpan()
        {
            return new ReadOnlySpan<byte>(_bytes);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as Bytes32);
        }

        public bool Equals(Bytes32? other)
        {
            return other != null &&
                _bytes.SequenceEqual(other._bytes);
        }

        public override int GetHashCode()
        {
            return BinaryPrimitives.ReadInt32LittleEndian(AsSpan().Slice(0, 4));
        }

        public override string ToString()
        {
            return AsSpan().ToHexString(true);
        }
    }
}
