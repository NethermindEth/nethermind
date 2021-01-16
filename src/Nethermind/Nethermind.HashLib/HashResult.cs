//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.Diagnostics;
using Nethermind.HashLib.Extensions;

namespace Nethermind.HashLib
{
    [DebuggerDisplay("HashResult, Size: {m_hash.Length}, Hash: {ToString()}")]
    [DebuggerStepThrough]
    public class HashResult
    {
        private byte[] m_hash;

        public HashResult(uint a_hash)
        {
            m_hash = BitConverter.GetBytes(a_hash);
        }

        internal HashResult(int a_hash)
        {
            m_hash = BitConverter.GetBytes(a_hash);
        }

        public HashResult(ulong a_hash)
        {
            m_hash = BitConverter.GetBytes(a_hash);
        }
        
        public HashResult(byte[] a_hash)
        {
            m_hash = a_hash;
        }

        public byte[] GetBytes()
        {
            //return m_hash.ToArray();
            return m_hash;
        }

        public uint GetUInt()
        {
            if (m_hash.Length != 4)
                throw new InvalidOperationException();

            return BitConverter.ToUInt32(m_hash, 0);
        }

        public int GetInt()
        {
            if (m_hash.Length != 4)
                throw new InvalidOperationException();

            return BitConverter.ToInt32(m_hash, 0);
        }

        public ulong GetULong()
        {
            if (m_hash.Length != 8)
                throw new InvalidOperationException();

            return BitConverter.ToUInt64(m_hash, 0);
        }

        public override string ToString()
        {
            return Converters.ConvertBytesToHexString(m_hash);
        }

        public override bool Equals(Object a_obj)
        {
            HashResult hash_result = a_obj as HashResult;
            if ((HashResult)hash_result == null)
                return false;

            return Equals(hash_result);
        }

        public bool Equals(HashResult a_hashResult)
        {
            return HashResult.SameArrays(a_hashResult.GetBytes(), m_hash);
        }

        public override int GetHashCode()
        {
            return Convert.ToBase64String(m_hash).GetHashCode();
        }

        private static bool SameArrays(byte[] a_ar1, byte[] a_ar2)
        {
            if (Object.ReferenceEquals(a_ar1, a_ar2))
                return true;

            if (a_ar1.Length != a_ar2.Length)
                return false;

            for (int i = 0; i < a_ar1.Length; i++)
                if (a_ar1[i] != a_ar2[i])
                    return false;

            return true;
        }
    }
}
