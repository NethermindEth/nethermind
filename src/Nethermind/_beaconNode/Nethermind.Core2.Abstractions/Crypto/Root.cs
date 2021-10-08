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
using System.Runtime.InteropServices;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core2.Crypto
{
    public class Root : IEquatable<Root>, IComparable<Root>
    {
        public const int Length = 32;

        public byte[] Bytes { get; }

        public Root()
        {
            Bytes = new byte[Length];
        }

        public Root(UInt256 span)
            : this(MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateReadOnlySpan(ref span, 1)))
        {
        }
        
        public Root(ReadOnlySpan<byte> span)
            : this(span.ToArray())
        {
        }

        public void AsInt(out UInt256 intRoot)
        {
            UInt256.CreateFromLittleEndian(out intRoot, Bytes.AsSpan());
        }
        
        public static Root Wrap(byte[] bytes)
        {
            return new Root(bytes);
        }

        private Root(byte[] bytes)
        {
            if (bytes.Length != Length)
            {
                throw new ArgumentOutOfRangeException(nameof(bytes), bytes.Length,
                    $"{nameof(Root)} must have exactly {Length} bytes");
            }

            Bytes = bytes;
        }

        public Root(string hex)
        {
            byte[] bytes = Nethermind.Core2.Bytes.FromHexString(hex);
            if (bytes.Length != Length)
            {
                throw new ArgumentOutOfRangeException(nameof(hex), bytes.Length, $"{nameof(Root)} must have exactly {Length} bytes");
            }

            Bytes = bytes;
        }

        public static Root Zero { get; } = new Root(new byte[Length]);

        public ReadOnlySpan<byte> AsSpan()
        {
            return new ReadOnlySpan<byte>(Bytes);
        }

        public override int GetHashCode()
        {
            return BinaryPrimitives.ReadInt32LittleEndian(AsSpan().Slice(0, 4));
        }

        public static bool operator ==(Root left, Root right)
        {
            if (ReferenceEquals(left, right)) return true;
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
            return Bytes.SequenceEqual(other.Bytes);
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