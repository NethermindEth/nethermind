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
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Crypto.ZkSnarks
{
    [StructLayout(LayoutKind.Sequential)]
    public struct G1
    {
        private ulong v00, v01, v02, v03, v04, v05, v06, v07, v08, v09, v10, v11;

        public void Clear()
        {
            Bn256.mclBnG1_clear(ref this);
        }

        public static G1? CreateFromBigEndian(Span<byte> x, Span<byte> y)
        {
            UInt256.CreateFromBigEndian(out UInt256 xInt, x);
            UInt256.CreateFromBigEndian(out UInt256 yInt, y);
            return Create(xInt, yInt);
        }

        public static G1 Create(UInt256 x, UInt256 y)
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
            }

            return g1;
        }

        public void setStr(String s, int ioMode)
        {
            if (Bn256.mclBnG1_setStr(ref this, s, s.Length, ioMode) != 0)
            {
                throw new ArgumentException("Bn256.mclBnG1_setStr:" + s);
            }
        }

        public bool IsValid()
        {
            return Bn256.mclBnG1_isValid(ref this) == 1;
        }

        public bool Equals(G1 rhs)
        {
            return Bn256.mclBnG1_isEqual(ref this, ref rhs) == 1;
        }

        public bool IsZero()
        {
            return Bn256.mclBnG1_isZero(ref this) == 1;
        }

        public void HashAndMapTo(String s)
        {
            if (Bn256.mclBnG1_hashAndMapTo(ref this, s, s.Length) != 0)
            {
                throw new ArgumentException("Bn256.mclBnG1_hashAndMapTo:" + s);
            }
        }

        public string GetStr(int ioMode)
        {
            StringBuilder sb = new StringBuilder(1024);
            long size = Bn256.mclBnG1_getStr(sb, sb.Capacity, ref this, ioMode);
            if (size == 0)
            {
                throw new InvalidOperationException("Bn256.mclBnG1_getStr:");
            }

            return sb.ToString();
        }

        public override string ToString()
        {
            return GetStr(0);
        }

        public void Neg(G1 x)
        {
            Bn256.mclBnG1_neg(ref this, ref x);
        }

        public void Dbl(G1 x)
        {
            Bn256.mclBnG1_dbl(ref this, ref x);
        }

        public void Add(G1 x, G1 y)
        {
            Bn256.mclBnG1_add(ref this, ref x, ref y);
        }

        public void Sub(G1 x, G1 y)
        {
            Bn256.mclBnG1_sub(ref this, ref x, ref y);
        }

        public void Mul(G1 x, Fr y)
        {
            Bn256.mclBnG1_mul(ref this, ref x, ref y);
        }
    }
}