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
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Nethermind.Crypto.Bls
{
    [StructLayout(LayoutKind.Sequential)]
    public struct G1
    {
        private ulong v00, v01, v02, v03, v04, v05, v06, v07, v08, v09, v10, v11, v12, v13, v14, v15, v16, v17;

        public void Clear()
        {
            MclBls12.mclBnG1_clear(ref this);
        }

        public unsafe void Deserialize(ReadOnlySpan<byte> data, int len)
        {
            fixed (byte* dataPtr = data)
            {
                int readBytes = MclBls12.mclBnG1_deserialize(ref this, dataPtr, len);
            }
        }

        public unsafe void Serialize(ReadOnlySpan<byte> data, int len)
        {
            fixed (byte* dataPtr = data)
            {
                MclBls12.mclBnG1_serialize(dataPtr, len, ref this);
            }
        }

        public static G1 Create(Span<byte> x)
        {
            G1 g1 = new G1();
            g1.Deserialize(x, 48);
            return g1;
        }

        public static G1 Create(BigInteger x, BigInteger y)
        {
            G1 g1 = new G1();
            if (x.IsZero && y.IsZero)
            {
                g1.Clear();
            }
            else
            {
                // cannot deserialize x,y as only the compressed form with x and oddity of y is supported
                // Span<byte> array = stackalloc byte[32];
                // x.ToLittleEndian(array);
                // g1.Deserialize(array, 32);

                // /* we cannot use compressed form as we are using mcl for validating the x,y pair */
                g1.setStr($"1 {x.ToString()} {y.ToString()}", 0);
                // g1.setStr($"2 {x.ToString()}", 0);
                // g1.setStr($"3 {x.ToString()}", 0);
            }

            return g1;
        }

        public void setStr(String s, int ioMode)
        {
            if (MclBls12.mclBnG1_setStr(ref this, s, s.Length, ioMode) != 0)
            {
                throw new ArgumentException("MclBls12.mclBnG1_setStr:" + s);
            }
        }

        public bool IsValid()
        {
            return MclBls12.mclBnG1_isValid(ref this) == 1;
        }

        public bool Equals(G1 rhs)
        {
            return MclBls12.mclBnG1_isEqual(ref this, ref rhs) == 1;
        }

        public bool IsZero()
        {
            return MclBls12.mclBnG1_isZero(ref this) == 1;
        }

        public void HashAndMapTo(String s)
        {
            if (MclBls12.mclBnG1_hashAndMapTo(ref this, s, s.Length) != 0)
            {
                throw new ArgumentException("MclBls12.mclBnG1_hashAndMapTo:" + s);
            }
        }

        public string GetStr(int ioMode)
        {
            StringBuilder sb = new StringBuilder(2048);
            long size = MclBls12.mclBnG1_getStr(sb, sb.Capacity, ref this, ioMode);
            if (size == 0)
            {
                throw new InvalidOperationException("MclBls12.mclBnG1_getStr:");
            }

            return sb.ToString();
        }

        public override string ToString()
        {
            return GetStr(0);
        }

        public void Neg(G1 x)
        {
            MclBls12.mclBnG1_neg(ref this, ref x);
        }

        public void Dbl(G1 x)
        {
            MclBls12.mclBnG1_dbl(ref this, ref x);
        }

        public void Add(G1 x, G1 y)
        {
            MclBls12.mclBnG1_add(ref this, ref x, ref y);
        }

        public void Sub(G1 x, G1 y)
        {
            MclBls12.mclBnG1_sub(ref this, ref x, ref y);
        }

        public void Mul(G1 x, Fr y)
        {
            MclBls12.mclBnG1_mul(ref this, ref x, ref y);
        }
        
        public static unsafe void MultiMul(ref G1 z, Span<G1> x, Span<Fr> y)
        {
            fixed (G1* xPtr = x)
            fixed (Fr* yPtr = y)
            {
                MclBls12.mclBnG1_mulVec(ref z, xPtr, yPtr, x.Length);
            }
        }
    }
}