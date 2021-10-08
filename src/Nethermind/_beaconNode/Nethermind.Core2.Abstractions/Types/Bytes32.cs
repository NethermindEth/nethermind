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
using System.Diagnostics;
using System.Linq;

namespace Nethermind.Core2.Types
{
    [DebuggerStepThrough]
    public class Bytes32 : IEquatable<Bytes32>
    {
        public const int Length = 32;

        private readonly byte[] _bytes;

        public Bytes32()
        {
            _bytes = new byte[Length];
        }

        public static Bytes32 Wrap(byte[] bytes)
        {
            return new Bytes32(bytes);
        }
        
        public byte[] Unwrap()
        {
            return _bytes;
        }
        
        private Bytes32(byte[] bytes)
        {
            if (bytes.Length != Length)
            {
                throw new ArgumentOutOfRangeException(nameof(bytes), bytes.Length,
                    $"{nameof(Bytes32)} must have exactly {Length} bytes");
            }

            _bytes = bytes;
        }
        
        public Bytes32(ReadOnlySpan<byte> span)
        {
            if (span.Length != Length)
            {
                throw new ArgumentOutOfRangeException(nameof(span), span.Length,
                    $"{nameof(Bytes32)} must have exactly {Length} bytes");
            }

            _bytes = span.ToArray();
        }

        public static Bytes32 Zero { get; } = new Bytes32(new byte[Length]);

        public ReadOnlySpan<byte> AsSpan()
        {
            return new ReadOnlySpan<byte>(_bytes);
        }

        public static bool operator ==(Bytes32 left, Bytes32 right)
        {
            return left.Equals(right);
        }

        public static explicit operator Bytes32(ReadOnlySpan<byte> span) => new Bytes32(span);

        public static explicit operator ReadOnlySpan<byte>(Bytes32 value) => value.AsSpan();

        public static bool operator !=(Bytes32 left, Bytes32 right)
        {
            return !(left == right);
        }

        public override string ToString()
        {
            return AsSpan().ToHexString(true);
        }

        public Bytes32 Xor(Bytes32 other)
        {
            // if used much - optimize, for now leave this way
            return new Bytes32(other.AsSpan().Xor(AsSpan()));
        }

        public bool Equals(Bytes32? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return _bytes.SequenceEqual(other._bytes);
        }

        public override bool Equals(object? obj)
        {
            return ReferenceEquals(this, obj) || obj is Bytes32 other && Equals(other);
        }

        public override int GetHashCode()
        {
            return BinaryPrimitives.ReadInt32LittleEndian(AsSpan().Slice(0, 4));
        }
    }
}