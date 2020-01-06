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
using System.Threading;

namespace Nethermind.Core2.Crypto
{
    public class BlsPublicKey : IEquatable<BlsPublicKey>
    {
        public const int Length = 48;

        public const int SszLength = Length;
        
        public static BlsPublicKey Empty = new BlsPublicKey(new byte[Length]);
        
        public static BlsPublicKey TestKey1 = new BlsPublicKey(
            "0x000102030405060708090a0b0c0d0e0f" +
            "101112131415161718191a1b1c1d1e1f" +
            "202122232425262728292a2b2c2d2e2f");

        public BlsPublicKey(string hexString)
            : this(Core2.Bytes.FromHexString(hexString))
        {
        }
        
        public ReadOnlySpan<byte> AsSpan()
        {
            return new ReadOnlySpan<byte>(Bytes);
        }

        public BlsPublicKey(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            if (bytes.Length != Length)
            {
                throw new ArgumentException($"{nameof(BlsPublicKey)} should be {Length} bytes long", nameof(bytes));
            }

            Bytes = bytes;
        }

        public byte[] Bytes { get; }

        public bool Equals(BlsPublicKey? other)
        {
            return !(other is null) && Core2.Bytes.AreEqual(Bytes, other.Bytes);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as BlsPublicKey);
        }

        public override int GetHashCode()
        {
            return MemoryMarshal.Read<int>(Bytes);
        }

        public override string ToString()
        {
            return Bytes.ToHexString(true);
        }

        public string ToString(bool with0X)
        {
            return Bytes.ToHexString(with0X);
        }

        public string ToShortString()
        {
            var value = Bytes.ToHexString(false);
            return $"{value.Substring(0, 6)}...{value.Substring(value.Length - 6)}";
            ;
        }

        public static bool operator ==(BlsPublicKey? a, BlsPublicKey? b)
        {
            if (ReferenceEquals(a, null))
            {
                return ReferenceEquals(b, null);
            }

            if (ReferenceEquals(b, null))
            {
                return false;
            }

            return Core2.Bytes.AreEqual(a.Bytes, b.Bytes);
        }

        public static bool operator !=(BlsPublicKey? a, BlsPublicKey? b)
        {
            return !(a == b);
        }
    }
}