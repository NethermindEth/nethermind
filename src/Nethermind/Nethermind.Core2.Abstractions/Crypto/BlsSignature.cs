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
        public BlsSignature(byte[] bytes)
        {
            Bytes = bytes;
        }

        public const int Length = 96;

        public byte[] Bytes { get; }

        public static BlsSignature Empty = new BlsSignature(new byte[Length]);
        
        public bool Equals(BlsSignature? other)
        {
            return other != null && Core2.Bytes.AreEqual(Bytes, other.Bytes);
        }
        
        public static bool operator !=(BlsSignature? left, BlsSignature? right)
        {
            return !(left == right);
        }

        public static bool operator ==(BlsSignature? left, BlsSignature? right)
        {
            if (left is null)
            {
                return right is null;
            }
            
            return left.Equals(right);
        }

        public ReadOnlySpan<byte> AsSpan()
        {
            return new ReadOnlySpan<byte>(Bytes);
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

        public override string ToString()
        {
            return Bytes.ToHexString(true);
        }
    }
}