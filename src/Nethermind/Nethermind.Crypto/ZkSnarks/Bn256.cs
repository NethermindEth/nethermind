﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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

namespace Nethermind.Crypto.ZkSnarks
{
    public static class Bn256
    {
        public static readonly BigInteger P = BigInteger.Parse("21888242871839275222246405745257275088696311157297823662689037894645226208583");
        public static readonly BigInteger R = BigInteger.Parse("21888242871839275222246405745257275088548364400416034343698204186575808495617");

        private const string Bn256Lib = "mclbn256";

        public const int LenFp = 32;
        
        static Bn256()
        {
            LibResolver.Setup();
            Init();
        }

        [DllImport(Bn256Lib)]
        public static extern int mclBn_init(int curve, int compiledTimeVar);

        [DllImport(Bn256Lib)]
        public static extern void mclBnFr_clear(ref Fr x);

        [DllImport(Bn256Lib)]
        public static extern void mclBnFr_setInt(ref Fr y, int x);

        [DllImport(Bn256Lib)]
        public static extern unsafe int mclBnFr_setLittleEndian(ref Fr y, void* bytes, int len);

        [DllImport(Bn256Lib)]
        public static extern unsafe int mclBnFr_setLittleEndianMod(ref Fr y, void* bytes, int len);

        [DllImport(Bn256Lib)]
        public static extern unsafe int mclBnFr_serialize(void* buf, int bufSize, ref Fr x);
        
        [DllImport(Bn256Lib)]
        public static extern unsafe int mclBnG1_serialize(void* buf, int bufSize, ref G1 x);
        
        [DllImport(Bn256Lib)]
        public static extern unsafe int mclBnG2_serialize(void* buf, int bufSize, ref G2 x);
        
        [DllImport(Bn256Lib)]
        public static extern unsafe int mclBnGT_serialize(void* buf, int bufSize, ref GT x);

        [DllImport(Bn256Lib)]
        public static extern unsafe int mclBnFr_deserialize(ref Fr x, void* buf, int bufSize);
        
        [DllImport(Bn256Lib)]
        public static extern unsafe int mclBnFp_deserialize(ref Fr x, void* buf, int bufSize);
        
        [DllImport(Bn256Lib)]
        public static extern unsafe int mclBnG1_deserialize(ref G1 x, void* buf, int bufSize);
        
        [DllImport(Bn256Lib)]
        public static extern unsafe int mclBnG2_deserialize(ref G2 x, void* buf, int bufSize);
        
        [DllImport(Bn256Lib)]
        public static extern unsafe int mclBnGT_deserialize(ref GT x, void* buf, int bufSize);

        [DllImport(Bn256Lib)]
        public static extern int mclBnFr_setStr(ref Fr x, [In] [MarshalAs(UnmanagedType.LPStr)] string buf, long bufSize, int ioMode);

        [DllImport(Bn256Lib)]
        public static extern int mclBnFr_isValid(ref Fr x);

        [DllImport(Bn256Lib)]
        public static extern int mclBnFr_isEqual(ref Fr x, ref Fr y);

        [DllImport(Bn256Lib)]
        public static extern int mclBnFr_isZero(ref Fr x);

        [DllImport(Bn256Lib)]
        public static extern int mclBnFr_isOne(ref Fr x);

        [DllImport(Bn256Lib)]
        public static extern void mclBnFr_setByCSPRNG(ref Fr x);

        [DllImport(Bn256Lib)]
        public static extern int mclBnFr_setHashOf(ref Fr x, [In] [MarshalAs(UnmanagedType.LPStr)] string buf, long bufSize);

        [DllImport(Bn256Lib)]
        public static extern int mclBnFr_getStr([Out] StringBuilder buf, long maxBufSize, ref Fr x, int ioMode);

        [DllImport(Bn256Lib)]
        public static extern void mclBnFr_neg(ref Fr y, ref Fr x);

        [DllImport(Bn256Lib)]
        public static extern void mclBnFr_inv(ref Fr y, ref Fr x);

        [DllImport(Bn256Lib)]
        public static extern void mclBnFr_add(ref Fr z, ref Fr x, ref Fr y);
        
        [DllImport(Bn256Lib)]
        public static extern void mclBnFr_dbl(ref Fr y, ref Fr x);
        
        [DllImport(Bn256Lib)]
        public static extern void mclBnFp_add(ref Fr z, ref Fr x, ref Fr y);

        [DllImport(Bn256Lib)]
        public static extern void mclBnFr_sub(ref Fr z, ref Fr x, ref Fr y);

        [DllImport(Bn256Lib)]
        public static extern void mclBnFr_mul(ref Fr z, ref Fr x, ref Fr y);
        
        [DllImport(Bn256Lib)]
        public static extern void mclBnFp_mul(ref Fr z, ref Fr x, ref Fr y);

        [DllImport(Bn256Lib)]
        public static extern void mclBnFr_sqr(ref Fr y, ref Fr x);

        [DllImport(Bn256Lib)]
        public static extern void mclBnFr_div(ref Fr z, ref Fr x, ref Fr y);

        [DllImport(Bn256Lib)]
        public static extern void mclBnG1_clear(ref G1 x);

        [DllImport(Bn256Lib)]
        public static extern int mclBnG1_setStr(ref G1 x, [In] [MarshalAs(UnmanagedType.LPStr)] string buf, long bufSize, int ioMode);
        
        [DllImport(Bn256Lib)]
        public static extern int mclBnG1_isValid(ref G1 x);

        [DllImport(Bn256Lib)]
        public static extern int mclBnG1_isEqual(ref G1 x, ref G1 y);

        [DllImport(Bn256Lib)]
        public static extern int mclBnG1_isZero(ref G1 x);

        [DllImport(Bn256Lib)]
        public static extern int mclBnG1_hashAndMapTo(ref G1 x, [In] [MarshalAs(UnmanagedType.LPStr)] string buf, long bufSize);

        [DllImport(Bn256Lib)]
        public static extern long mclBnG1_getStr([Out] StringBuilder buf, long maxBufSize, ref G1 x, int ioMode);

        [DllImport(Bn256Lib)]
        public static extern void mclBnG1_neg(ref G1 y, ref G1 x);

        [DllImport(Bn256Lib)]
        public static extern void mclBnG1_dbl(ref G1 y, ref G1 x);

        [DllImport(Bn256Lib)]
        public static extern void mclBnG1_add(ref G1 z, ref G1 x, ref G1 y);

        [DllImport(Bn256Lib)]
        public static extern void mclBnG1_sub(ref G1 z, ref G1 x, ref G1 y);

        [DllImport(Bn256Lib)]
        public static extern void mclBnG1_mul(ref G1 z, ref G1 x, ref Fr y);

        [DllImport(Bn256Lib)]
        public static extern void mclBnG2_clear(ref G2 x);

        [DllImport(Bn256Lib)]
        public static extern int mclBnG2_setStr(ref G2 x, [In] [MarshalAs(UnmanagedType.LPStr)] string buf, long bufSize, int ioMode);

        [DllImport(Bn256Lib)]
        public static extern int mclBnG2_isValid(ref G2 x);

        [DllImport(Bn256Lib)]
        public static extern int mclBnG2_isEqual(ref G2 x, ref G2 y);

        [DllImport(Bn256Lib)]
        public static extern int mclBnG2_isZero(ref G2 x);

        [DllImport(Bn256Lib)]
        public static extern int mclBnG2_hashAndMapTo(ref G2 x, [In] [MarshalAs(UnmanagedType.LPStr)] string buf, long bufSize);

        [DllImport(Bn256Lib)]
        public static extern long mclBnG2_getStr([Out] StringBuilder buf, long maxBufSize, ref G2 x, int ioMode);

        [DllImport(Bn256Lib)]
        public static extern void mclBnG2_neg(ref G2 y, ref G2 x);

        [DllImport(Bn256Lib)]
        public static extern void mclBnG2_dbl(ref G2 y, ref G2 x);

        [DllImport(Bn256Lib)]
        public static extern void mclBnG2_add(ref G2 z, ref G2 x, ref G2 y);

        [DllImport(Bn256Lib)]
        public static extern void mclBnG2_sub(ref G2 z, ref G2 x, ref G2 y);

        [DllImport(Bn256Lib)]
        public static extern void mclBnG2_mul(ref G2 z, ref G2 x, ref Fr y);

        [DllImport(Bn256Lib)]
        public static extern void mclBnGT_clear(ref GT x);

        [DllImport(Bn256Lib)]
        public static extern int mclBnGT_setStr(ref GT x, [In] [MarshalAs(UnmanagedType.LPStr)] string buf, long bufSize, int ioMode);

        [DllImport(Bn256Lib)]
        public static extern int mclBnGT_isEqual(ref GT x, ref GT y);

        [DllImport(Bn256Lib)]
        public static extern int mclBnGT_isZero(ref GT x);

        [DllImport(Bn256Lib)]
        public static extern int mclBnGT_isOne(ref GT x);

        [DllImport(Bn256Lib)]
        public static extern long mclBnGT_getStr([Out] StringBuilder buf, long maxBufSize, ref GT x, int ioMode);

        [DllImport(Bn256Lib)]
        public static extern void mclBnGT_neg(ref GT y, ref GT x);

        [DllImport(Bn256Lib)]
        public static extern void mclBnGT_inv(ref GT y, ref GT x);

        [DllImport(Bn256Lib)]
        public static extern void mclBnGT_add(ref GT z, ref GT x, ref GT y);

        [DllImport(Bn256Lib)]
        public static extern void mclBnGT_sub(ref GT z, ref GT x, ref GT y);

        [DllImport(Bn256Lib)]
        public static extern void mclBnGT_mul(ref GT z, ref GT x, ref GT y);

        [DllImport(Bn256Lib)]
        public static extern void mclBnGT_div(ref GT z, ref GT x, ref GT y);

        [DllImport(Bn256Lib)]
        public static extern void mclBnGT_pow(ref GT z, ref GT x, ref Fr y);

        [DllImport(Bn256Lib)]
        public static extern void mclBn_pairing(ref GT z, ref G1 x, ref G2 y);

        [DllImport(Bn256Lib)]
        public static extern void mclBn_finalExp(ref GT y, ref GT x);

        [DllImport(Bn256Lib)]
        public static extern void mclBn_millerLoop(ref GT z, ref G1 x, ref G2 y);

        public static void Init()
        {
            // const int curveFr254BNb = 0;
            const int MCLBN_FR_UNIT_SIZE = 4;
            const int MCLBN_FP_UNIT_SIZE = 4;
            const int MCLBN_COMPILED_TIME_VAR = (MCLBN_FR_UNIT_SIZE) * 10 + (MCLBN_FP_UNIT_SIZE);
            if (mclBn_init(4, MCLBN_COMPILED_TIME_VAR) != 0)
            {
                throw new InvalidOperationException("mclBn_init");
            }
        }
    }
}