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

using System.Buffers.Binary;

namespace Nethermind.Core2.Crypto
{
    public class BlsSignature
    {
        public BlsSignature(byte[] bytes)
        {
            Bytes = bytes;
        }
        
        public const int SszLength = 96;

        public byte[] Bytes { get; }
        
        public static BlsSignature TestSig1 = new BlsSignature(new byte[SszLength]);
        
        public bool Equals(BlsSignature other)
        {
            return Core.Extensions.Bytes.AreEqual(Bytes, other.Bytes);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == GetType() && Equals((BlsSignature) obj);
        }

        public override int GetHashCode()
        {
            return Bytes != null ? BinaryPrimitives.ReadInt32LittleEndian(Bytes) : 0;
        }   
    }
}