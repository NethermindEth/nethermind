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
using System.Text;

namespace Nethermind.Crypto.ZkSnarks
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Fr : IEquatable<Fr>
    {
        private ulong v0, v1, v2, v3;

        public void Clear()
        {
            Bn256.mclBnFr_clear(ref this);
        }

        public void SetInt(int x)
        {
            Bn256.mclBnFr_setInt(ref this, x);
        }

        public void SetStr(string s, int ioMode)
        {
            int res = Bn256.mclBnFr_setStr(ref this, s, s.Length, ioMode);
            if (res != 0)
            {
                throw new ArgumentException($"Bn256.mclBnFr_setStr({s})->{res}");
            }
        }

        public unsafe void SetLittleEndian(Span<byte> data, int len)
        {
            fixed (byte* serializedPtr = &MemoryMarshal.GetReference(data))
            {
                Bn256.mclBnFr_setLittleEndian(ref this, serializedPtr, len);
            }
        }

        public unsafe void SetLittleEndianMod(Span<byte> data, int len)
        {
            fixed (byte* serializedPtr = &MemoryMarshal.GetReference(data))
            {
                Bn256.mclBnFr_setLittleEndianMod(ref this, serializedPtr, len);
            }
        }

        public bool IsValid()
        {
            return Bn256.mclBnFr_isValid(ref this) == 1;
        }

        public bool Equals(Fr rhs)
        {
            return Bn256.mclBnFr_isEqual(ref this, ref rhs) == 1;
        }

        // public override bool Equals(Fr other)
        // {
        //     return v0 == other.v0 && v1 == other.v1 && v2 == other.v2 && v3 == other.v3;
        // }

        public bool IsZero()
        {
            return Bn256.mclBnFr_isZero(ref this) == 1;
        }

        public bool IsOne()
        {
            return Bn256.mclBnFr_isOne(ref this) == 1;
        }

        public void SetByCSPRNG()
        {
            Bn256.mclBnFr_setByCSPRNG(ref this);
        }

        public void SetHashOf(String s)
        {
            if (Bn256.mclBnFr_setHashOf(ref this, s, s.Length) != 0)
            {
                throw new InvalidOperationException("Bn256.mclBnFr_setHashOf:" + s);
            }
        }

        public string GetStr(int ioMode)
        {
            StringBuilder sb = new StringBuilder(1024);
            long size = Bn256.mclBnFr_getStr(sb, sb.Capacity, ref this, ioMode);
            if (size == 0)
            {
                throw new InvalidOperationException("Bn256.mclBnFr_getStr:");
            }

            return sb.ToString();
        }

        public override string ToString()
        {
            return GetStr(0);
        }

        public void Neg(Fr x)
        {
            Bn256.mclBnFr_neg(ref this, ref x);
        }

        public void Inv(Fr x)
        {
            Bn256.mclBnFr_inv(ref this, ref x);
        }

        public void Add(Fr x, Fr y)
        {
            Bn256.mclBnFr_add(ref this, ref x, ref y);
        }

        public void Dbl(Fr x)
        {
            Bn256.mclBnFr_dbl(ref this, ref x);
        }

        public void Sub(Fr x, Fr y)
        {
            Bn256.mclBnFr_sub(ref this, ref x, ref y);
        }

        public void Mul(Fr x, Fr y)
        {
            Bn256.mclBnFr_mul(ref this, ref x, ref y);
        }

        public void Sqr(Fr x)
        {
            Bn256.mclBnFr_sqr(ref this, ref x);
        }

        public void Div(Fr x, Fr y)
        {
            Bn256.mclBnFr_div(ref this, ref x, ref y);
        }

        public static Fr operator -(Fr x)
        {
            Fr y = new Fr();
            y.Neg(x);
            return y;
        }

        public static Fr operator +(Fr x, Fr y)
        {
            Fr z = new Fr();
            z.Add(x, y);
            return z;
        }

        public static Fr operator -(Fr x, Fr y)
        {
            Fr z = new Fr();
            z.Sub(x, y);
            return z;
        }

        public static Fr operator *(Fr x, Fr y)
        {
            Fr z = new Fr();
            z.Mul(x, y);
            return z;
        }

        public static Fr operator /(Fr x, Fr y)
        {
            Fr z = new Fr();
            z.Div(x, y);
            return z;
        }
    }
}