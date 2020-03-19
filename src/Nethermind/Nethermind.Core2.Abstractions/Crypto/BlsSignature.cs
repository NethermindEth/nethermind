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
    public class BlsSignature : IEquatable<BlsSignature>
    {
        public const int Length = 96;

        public static readonly BlsSignature Zero = new BlsSignature(new byte[Length]);

        private BlsSignature(byte[] bytes)
        {
            Bytes = bytes;
        }

        public BlsSignature(ReadOnlySpan<byte> span)
        {
            if (span.Length != Length)
            {
                throw new ArgumentOutOfRangeException(nameof(span), span.Length,
                    $"{nameof(Root)} must have exactly {Length} bytes");
            }
            Bytes = span.ToArray();
        }

        /// <summary>
        /// Creates a BlsSignature directly using the provided buffer; it is up to the caller to ensure the buffer is unique.
        /// </summary>
        public static BlsSignature WithBuffer(byte[] bytes)
        {
            return new BlsSignature(bytes);
        }

        public byte[] Bytes { get; }

        public ReadOnlySpan<byte> AsSpan()
        {
            return new ReadOnlySpan<byte>(Bytes);
        }

        public bool Equals(BlsSignature? other)
        {
            return other != null && Core2.Bytes.AreEqual(Bytes, other.Bytes);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == GetType() && Equals((BlsSignature) obj);
        }

        public override int GetHashCode()
        {
            return Bytes != null ? BinaryPrimitives.ReadInt32LittleEndian(Bytes) : 0;
        }

        public static bool operator ==(BlsSignature? left, BlsSignature? right)
        {
            if (left is null)
            {
                return right is null;
            }

            return left.Equals(right);
        }

        public static bool operator !=(BlsSignature? left, BlsSignature? right)
        {
            return !(left == right);
        }

        public override string ToString()
        {
            return Bytes.ToHexString(true);
        }
    }
}