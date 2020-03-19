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

namespace Nethermind.Core2.Crypto
{
    public class BlsPublicKey : IEquatable<BlsPublicKey>
    {
        public const int Length = 48;

        public static readonly BlsPublicKey Zero = new BlsPublicKey(new byte[Length]);

        public BlsPublicKey(string hexString)
            : this(Core2.Bytes.FromHexString(hexString))
        {
        }

        public BlsPublicKey(ReadOnlySpan<byte> span)
        {
            if (span.Length != Length)
            {
                throw new ArgumentException($"{nameof(BlsPublicKey)} should be {Length} bytes long", nameof(span));
            }

            Bytes = span.ToArray();
        }

        public byte[] Bytes { get; }

        public ReadOnlySpan<byte> AsSpan()
        {
            return new ReadOnlySpan<byte>(Bytes);
        }

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

        public string ToShortString()
        {
            var value = Bytes.ToHexString(false);
            return $"{value.Substring(0, 6)}...{value.Substring(value.Length - 6)}";
            ;
        }

        public override string ToString()
        {
            return Bytes.ToHexString(true);
        }
    }
}