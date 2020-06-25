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
    public struct GT
    {
        private ulong v00, v01, v02, v03, v04, v05, v06, v07, v08, v09, v10, v11, v12, v13, v14, v15, v16, v17;
        private ulong v18, v19, v20, v21, v22, v23, v24, v25, v26, v27, v28, v29, v30, v31, v32, v33, v34, v35;
        private ulong v36, v37, v38, v39, v40, v41, v42, v43, v44, v45, v46, v47, v48, v49, v50, v51, v52, v53;
        private ulong v54, v55, v56, v57, v58, v59, v60, v61, v62, v63, v64, v65, v66, v67, v68, v69, v70, v71;

        public void Clear()
        {
            MclBls12.mclBnGT_clear(ref this);
        }

        public void setStr(String s, int ioMode)
        {
            if (MclBls12.mclBnGT_setStr(ref this, s, s.Length, ioMode) != 0)
            {
                throw new ArgumentException("MclBls12.mclBnGT_setStr:" + s);
            }
        }

        public bool Equals(GT rhs)
        {
            return MclBls12.mclBnGT_isEqual(ref this, ref rhs) == 1;
        }

        public bool IsZero()
        {
            return MclBls12.mclBnGT_isZero(ref this) == 1;
        }

        public bool IsOne()
        {
            return MclBls12.mclBnGT_isOne(ref this) == 1;
        }

        public string GetStr(int ioMode)
        {
            StringBuilder sb = new StringBuilder(1024);
            long size = MclBls12.mclBnGT_getStr(sb, sb.Capacity, ref this, ioMode);
            if (size == 0)
            {
                throw new InvalidOperationException("MclBls12.mclBnGT_getStr:");
            }

            return sb.ToString();
        }

        public void Neg(GT x)
        {
            MclBls12.mclBnGT_neg(ref this, ref x);
        }

        public void Inv(GT x)
        {
            MclBls12.mclBnGT_inv(ref this, ref x);
        }

        public void Add(GT x, GT y)
        {
            MclBls12.mclBnGT_add(ref this, ref x, ref y);
        }

        public void Sub(GT x, GT y)
        {
            MclBls12.mclBnGT_sub(ref this, ref x, ref y);
        }

        public void Mul(GT x, GT y)
        {
            MclBls12.mclBnGT_mul(ref this, ref x, ref y);
        }

        public void Div(GT x, GT y)
        {
            MclBls12.mclBnGT_div(ref this, ref x, ref y);
        }

        public static GT operator -(GT x)
        {
            GT y = new GT();
            y.Neg(x);
            return y;
        }

        public static GT operator +(GT x, GT y)
        {
            GT z = new GT();
            z.Add(x, y);
            return z;
        }

        public static GT operator -(GT x, GT y)
        {
            GT z = new GT();
            z.Sub(x, y);
            return z;
        }

        public static GT operator *(GT x, GT y)
        {
            GT z = new GT();
            z.Mul(x, y);
            return z;
        }

        public static GT operator /(GT x, GT y)
        {
            GT z = new GT();
            z.Div(x, y);
            return z;
        }

        public void Pow(GT x, Fp y)
        {
            MclBls12.mclBnGT_pow(ref this, ref x, ref y);
        }

        public void Pairing(G1 x, G2 y)
        {
            MclBls12.mclBn_pairing(ref this, ref x, ref y);
        }

        public void FinalExp(GT x)
        {
            MclBls12.mclBn_finalExp(ref this, ref x);
        }

        public void MillerLoop(G1 x, G2 y)
        {
            MclBls12.mclBn_millerLoop(ref this, ref x, ref y);
        }
    }
}