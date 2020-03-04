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

namespace Nethermind.Core2.Crypto
{
    public class Root : IEquatable<Root>, IComparable<Root>
    {
        public const int Length = 32;

        private readonly byte[] _bytes;

        public Root()
        {
            _bytes = new byte[Length];
        }

        public Root(ReadOnlySpan<byte> span)
        {
            if (span.Length != Length)
            {
                throw new ArgumentOutOfRangeException(nameof(span), span.Length,
                    $"{nameof(Root)} must have exactly {Length} bytes");
            }

            _bytes = span.ToArray();
        }

        public Root(string hex)
        {
            byte[] bytes = Bytes.FromHexString(hex);
            if (bytes.Length != Length)
            {
                throw new ArgumentOutOfRangeException(nameof(hex), bytes.Length, $"{nameof(Root)} must have exactly {Length} bytes");
            }
            _bytes = bytes;
        }
        
        public static Root Zero { get; } = new Root(new byte[Length]);

        public ReadOnlySpan<byte> AsSpan()
        {
            return new ReadOnlySpan<byte>(_bytes);
        }
        
        public override int GetHashCode()
        {
            return BinaryPrimitives.ReadInt32LittleEndian(AsSpan().Slice(0, 4));
        }

        public static bool operator ==(Root left, Root right)
        {
            return left.Equals(right);
        }

        public static explicit operator Root(ReadOnlySpan<byte> span) => new Root(span);

        public static explicit operator ReadOnlySpan<byte>(Root value) => value.AsSpan();

        public static bool operator !=(Root left, Root right)
        {
            return !(left == right);
        }

        public override string ToString()
        {
            return AsSpan().ToHexString(true);
        }

        public bool Equals(Root? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return _bytes.SequenceEqual(other._bytes);
        }

        public override bool Equals(object? obj)
        {
            return ReferenceEquals(this, obj) || obj is Root other && Equals(other);
        }

        public int CompareTo(Root? other)
        {
            // lexicographic compare
            return other is null ? 1 : AsSpan().SequenceCompareTo(other.AsSpan());
        }

    }
}