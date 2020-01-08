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

namespace Nethermind.Core2.Crypto
{
    public struct Hash32 : IEquatable<Hash32>, IComparable<Hash32>
    {
        public const int Length = 32;

        public Hash32(string hex)
        {
            byte[] bytes = Nethermind.Core2.Bytes.FromHexString(hex);
            if (bytes.Length != Length)
            {
                throw new ArgumentOutOfRangeException(nameof(hex), bytes.Length, $"{nameof(Hash32)} must have exactly {Length} bytes");
            }

            Bytes = bytes;
        }
        
        public unsafe Hash32(ReadOnlySpan<byte> span)
        {
            if (span.Length != Length)
            {
                throw new ArgumentOutOfRangeException(nameof(span), span.Length, $"{nameof(Hash32)} must have exactly {Length} bytes");
            }

            Bytes = new byte[32];
            fixed (byte* ptr = Bytes)
            {
                var output = new Span<byte>(ptr, Length);
                span.CopyTo(output);
            }
            
            // simpler but keeping above for easier testing of fixed
            // _bytes = span.ToArray();
        } 

        // tests are not passing when using fixed
        //        private fixed byte Bytes[32];
        //        public Span<byte> AsSpan() => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref this, 1));
        
        public readonly byte[] Bytes;
        
        public ReadOnlySpan<byte> AsSpan()
        {
            return new ReadOnlySpan<byte>(Bytes);
        }
        
        public bool Equals(Hash32 other)
        {
            return AsSpan().SequenceEqual(other.AsSpan());
        }

        public static Hash32 Zero { get; } = new Hash32(new byte[32]);

        public static bool operator !=(Hash32 left, Hash32 right)
        {
            return !(left == right);
        }

        public static bool operator ==(Hash32 left, Hash32 right)
        {
            return left.Equals(right);
        }

        public int CompareTo(Hash32 other)
        {
            // lexicographic compare
            return AsSpan().SequenceCompareTo(other.AsSpan());
        }

        public override bool Equals(object? obj)
        {
            return !(obj is null) && Equals((Hash32) obj);
        }

        public override int GetHashCode()
        {
            return BinaryPrimitives.ReadInt32LittleEndian(AsSpan().Slice(0, 4));
        }

        public override string ToString()
        {
            return AsSpan().ToHexString(true);
        }

        public Hash32 Xor(Hash32 other)
        {
            return new Hash32(other.AsSpan().Xor(AsSpan()));
        }
    }
}