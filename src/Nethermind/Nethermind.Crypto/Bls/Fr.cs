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

namespace Nethermind.Crypto.Bls
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Fr : IEquatable<Fr>
    {
        private ulong v0, v1, v2, v3, v4, v5;

        public void Clear()
        {
            MclBls12.mclBnFr_clear(ref this);
        }

        public void SetInt(int x)
        {
            MclBls12.mclBnFr_setInt(ref this, x);
        }

        public void SetStr(string s, int ioMode)
        {
            if (MclBls12.mclBnFr_setStr(ref this, s, s.Length, ioMode) != 0)
            {
                throw new ArgumentException("MclBls12.mclBnFr_setStr" + s);
            }
        }

        public unsafe void SetLittleEndian(Span<byte> data, int len)
        {
            fixed (byte* serializedPtr = &MemoryMarshal.GetReference(data))
            {
                MclBls12.mclBnFr_setLittleEndian(ref this, serializedPtr, len);
            }
        }

        public unsafe void SetLittleEndianMod(Span<byte> data, int len)
        {
            fixed (byte* serializedPtr = &MemoryMarshal.GetReference(data))
            {
                MclBls12.mclBnFr_setLittleEndianMod(ref this, serializedPtr, len);
            }
        }

        public bool IsValid()
        {
            return MclBls12.mclBnFr_isValid(ref this) == 1;
        }

        public bool Equals(Fr rhs)
        {
            return MclBls12.mclBnFr_isEqual(ref this, ref rhs) == 1;
        }

        public bool IsZero()
        {
            return MclBls12.mclBnFr_isZero(ref this) == 1;
        }

        public bool IsOne()
        {
            return MclBls12.mclBnFr_isOne(ref this) == 1;
        }

        public void SetByCSPRNG()
        {
            MclBls12.mclBnFr_setByCSPRNG(ref this);
        }

        public void SetHashOf(String s)
        {
            if (MclBls12.mclBnFr_setHashOf(ref this, s, s.Length) != 0)
            {
                throw new InvalidOperationException("MclBls12.mclBnFr_setHashOf:" + s);
            }
        }

        public string GetStr(int ioMode)
        {
            StringBuilder sb = new StringBuilder(1024);
            long size = MclBls12.mclBnFr_getStr(sb, sb.Capacity, ref this, ioMode);
            if (size == 0)
            {
                throw new InvalidOperationException("MclBls12.mclBnFr_getStr:");
            }

            return sb.ToString();
        }

        public override string ToString()
        {
            return GetStr(0);
        }

        public void Neg(Fr x)
        {
            MclBls12.mclBnFr_neg(ref this, ref x);
        }

        public void Inv(Fr x)
        {
            MclBls12.mclBnFr_inv(ref this, ref x);
        }

        public void Add(Fr x, Fr y)
        {
            MclBls12.mclBnFr_add(ref this, ref x, ref y);
        }

        public void Dbl(Fr x)
        {
            MclBls12.mclBnFr_dbl(ref this, ref x);
        }

        public void AddFr(Fr x, Fr y)
        {
            MclBls12.mclBnFr_add(ref this, ref x, ref y);
        }

        public void Sub(Fr x, Fr y)
        {
            MclBls12.mclBnFr_sub(ref this, ref x, ref y);
        }

        public void Mul(Fr x, Fr y)
        {
            MclBls12.mclBnFr_mul(ref this, ref x, ref y);
        }

        public void MulFr(Fr x, Fr y)
        {
            MclBls12.mclBnFr_mul(ref this, ref x, ref y);
        }

        public void Sqr(Fr x)
        {
            MclBls12.mclBnFr_sqr(ref this, ref x);
        }

        public void Div(Fr x, Fr y)
        {
            MclBls12.mclBnFr_div(ref this, ref x, ref y);
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