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
    public struct G2
    {
        private ulong v00, v01, v02, v03, v04, v05, v06, v07, v08, v09, v10, v11;
        private ulong v12, v13, v14, v15, v16, v17, v18, v19, v20, v21, v22, v23;

        public void Clear()
        {
            Bn256.mclBnG2_clear(ref this);
        }

        public void setStr(String s, int ioMode)
        {
            if (Bn256.mclBnG2_setStr(ref this, s, s.Length, ioMode) != 0)
            {
                throw new ArgumentException("Bn256.mclBnG2_setStr:" + s);
            }
        }

        public bool IsValid()
        {
            return Bn256.mclBnG2_isValid(ref this) == 1;
        }

        public bool Equals(G2 rhs)
        {
            return Bn256.mclBnG2_isEqual(ref this, ref rhs) == 1;
        }

        public bool IsZero()
        {
            return Bn256.mclBnG2_isZero(ref this) == 1;
        }

        public void HashAndMapTo(String s)
        {
            if (Bn256.mclBnG2_hashAndMapTo(ref this, s, s.Length) != 0)
            {
                throw new ArgumentException("Bn256.mclBnG2_hashAndMapTo:" + s);
            }
        }

        public string GetStr(int ioMode)
        {
            StringBuilder sb = new StringBuilder(1024);
            long size = Bn256.mclBnG2_getStr(sb, sb.Capacity, ref this, ioMode);
            if (size == 0)
            {
                throw new InvalidOperationException("Bn256.mclBnG2_getStr:");
            }

            return sb.ToString();
        }

        public static G2 CreateFromBigEndian(Span<byte> a, Span<byte> b, Span<byte> c, Span<byte> d)
        {
            UInt256.CreateFromBigEndian(out UInt256 aInt, a);
            UInt256.CreateFromBigEndian(out UInt256 bInt, b);
            UInt256.CreateFromBigEndian(out UInt256 cInt, c);
            UInt256.CreateFromBigEndian(out UInt256 dInt, d);
            return Create(aInt, bInt, cInt, dInt);
        }

        public static G2 Create(UInt256 a, UInt256 b, UInt256 c, UInt256 d)
        {
            G2 g2 = new G2();
            if (a.IsZero && b.IsZero && c.IsZero && d.IsZero)
            {
                g2.Clear();
            }
            else
            {
                g2.setStr($"1 {a.ToString()} {b.ToString()} {c.ToString()} {d.ToString()}", 0);
            }

            return g2;
        }

        public void Neg(G2 x)
        {
            Bn256.mclBnG2_neg(ref this, ref x);
        }

        public void Dbl(G2 x)
        {
            Bn256.mclBnG2_dbl(ref this, ref x);
        }

        public void Add(G2 x, G2 y)
        {
            Bn256.mclBnG2_add(ref this, ref x, ref y);
        }

        public void Sub(G2 x, G2 y)
        {
            Bn256.mclBnG2_sub(ref this, ref x, ref y);
        }

        public void Mul(G2 x, Fr y)
        {
            Bn256.mclBnG2_mul(ref this, ref x, ref y);
        }
    }
}