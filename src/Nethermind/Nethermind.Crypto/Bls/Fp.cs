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
    public struct Fp : IEquatable<Fp>
    {
        private ulong v0, v1, v2, v3, v4, v5;

        public void Clear()
        {
            MclBls12.mclBnFp_clear(ref this);
        }

        public void SetInt(int x)
        {
            MclBls12.mclBnFp_setInt(ref this, x);
        }

        public void SetStr(string s, int ioMode)
        {
            if (MclBls12.mclBnFp_setStr(ref this, s, s.Length, ioMode) != 0)
            {
                throw new ArgumentException("MclBls12.mclBnFp_setStr" + s);
            }
        }

        public unsafe void Deserialize(Span<byte> data, int len)
        {
            fixed (byte* dataPtr = &MemoryMarshal.GetReference(data))
            {
                MclBls12.mclBnFp_deserialize(ref this, dataPtr, len);
            }
        }

        public unsafe void DeserializeFp(Span<byte> data, int len)
        {
            fixed (byte* dataPtr = &MemoryMarshal.GetReference(data))
            {
                MclBls12.mclBnFp_deserialize(ref this, dataPtr, len);
            }
        }

        public unsafe void FpSetLittleEndian(Span<byte> data, int len)
        {
            fixed (byte* serializedPtr = &MemoryMarshal.GetReference(data))
            {
                MclBls12.mclBnFp_setLittleEndian(ref this, serializedPtr, len);
            }
        }

        public unsafe void FpSetLittleEndianMod(Span<byte> data, int len)
        {
            fixed (byte* serializedPtr = &MemoryMarshal.GetReference(data))
            {
                MclBls12.mclBnFp_setLittleEndianMod(ref this, serializedPtr, len);
            }
        }

        public bool IsValid()
        {
            return MclBls12.mclBnFp_isValid(ref this) == 1;
        }

        public bool Equals(Fp rhs)
        {
            return MclBls12.mclBnFp_isEqual(ref this, ref rhs) == 1;
        }

        // public override bool Equals(Fp other)
        // {
        //     return v0 == other.v0 && v1 == other.v1 && v2 == other.v2 && v3 == other.v3;
        // }

        public bool IsZero()
        {
            return MclBls12.mclBnFp_isZero(ref this) == 1;
        }

        public bool IsOne()
        {
            return MclBls12.mclBnFp_isOne(ref this) == 1;
        }

        public void SetByCSPRNG()
        {
            MclBls12.mclBnFp_setByCSPRNG(ref this);
        }

        public void SetHashOf(String s)
        {
            if (MclBls12.mclBnFp_setHashOf(ref this, s, s.Length) != 0)
            {
                throw new InvalidOperationException("MclBls12.mclBnFp_setHashOf:" + s);
            }
        }

        public string GetStr(int ioMode)
        {
            StringBuilder sb = new StringBuilder(1024);
            long size = MclBls12.mclBnFp_getStr(sb, sb.Capacity, ref this, ioMode);
            if (size == 0)
            {
                throw new InvalidOperationException("MclBls12.mclBnFp_getStr:");
            }

            return sb.ToString();
        }

        public override string ToString()
        {
            return GetStr(0);
        }

        public void Neg(Fp x)
        {
            MclBls12.mclBnFp_neg(ref this, ref x);
        }

        public void Inv(Fp x)
        {
            MclBls12.mclBnFp_inv(ref this, ref x);
        }

        public void Add(Fp x, Fp y)
        {
            MclBls12.mclBnFp_add(ref this, ref x, ref y);
        }

        public void Dbl(Fp x)
        {
            MclBls12.mclBnFp_dbl(ref this, ref x);
        }

        public void AddFp(Fp x, Fp y)
        {
            MclBls12.mclBnFp_add(ref this, ref x, ref y);
        }

        public void Sub(Fp x, Fp y)
        {
            MclBls12.mclBnFp_sub(ref this, ref x, ref y);
        }

        public void Mul(Fp x, Fp y)
        {
            MclBls12.mclBnFp_mul(ref this, ref x, ref y);
        }

        public void MulFp(Fp x, Fp y)
        {
            MclBls12.mclBnFp_mul(ref this, ref x, ref y);
        }

        public void Sqr(Fp x)
        {
            MclBls12.mclBnFp_sqr(ref this, ref x);
        }

        public void Div(Fp x, Fp y)
        {
            MclBls12.mclBnFp_div(ref this, ref x, ref y);
        }

        public static Fp operator -(Fp x)
        {
            Fp y = new Fp();
            y.Neg(x);
            return y;
        }

        public static Fp operator +(Fp x, Fp y)
        {
            Fp z = new Fp();
            z.Add(x, y);
            return z;
        }

        public static Fp operator -(Fp x, Fp y)
        {
            Fp z = new Fp();
            z.Sub(x, y);
            return z;
        }

        public static Fp operator *(Fp x, Fp y)
        {
            Fp z = new Fp();
            z.Mul(x, y);
            return z;
        }

        public static Fp operator /(Fp x, Fp y)
        {
            Fp z = new Fp();
            z.Div(x, y);
            return z;
        }

        public G1 MapToG1()
        {
            G1 g1 = new G1();
            MclBls12.mclBnFp_mapToG1(ref g1, ref this);
            return g1;
        }
    }
}