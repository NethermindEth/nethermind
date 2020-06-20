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
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Nethermind.Crypto.Bls
{
    public static class MclBls12
    {
        public static readonly BigInteger P = BigInteger.Parse("1a0111ea397fe69a4b1ba7b6434bacd764774b84f38512bf6730d2a0f6b0f6241eabfffeb153ffffb9feffffffffaaab", NumberStyles.AllowHexSpecifier);
        public static readonly BigInteger R = BigInteger.Parse("73eda753299d7d483339d80809a1d80553bda402fffe5bfeffffffff00000001", NumberStyles.AllowHexSpecifier);

        private const string MclBls12Lib = "mclbn384_256";

        static MclBls12()
        {
            LibResolver.Setup();
            Init();
        }

        [DllImport(MclBls12Lib)]
        public static extern int mclBn_init(int curve, int compiledTimeVar);

        [DllImport(MclBls12Lib)]
        public static extern unsafe int mclBnG1_serialize(void* buf, int bufSize, ref G1 x);

        [DllImport(MclBls12Lib)]
        public static extern unsafe int mclBnG2_serialize(void* buf, int bufSize, ref G2 x);

        [DllImport(MclBls12Lib)]
        public static extern unsafe int mclBnGT_serialize(void* buf, int bufSize, ref GT x);

        [DllImport(MclBls12Lib)]
        public static extern unsafe int mclBnFp_deserialize(ref Fp x, void* buf, int bufSize);

        [DllImport(MclBls12Lib)]
        public static extern unsafe int mclBnG1_deserialize([In, Out] ref G1 x, byte* buf, int bufSize);

        [DllImport(MclBls12Lib)]
        public static extern unsafe int mclBnG2_deserialize(ref G2 x, void* buf, int bufSize);

        [DllImport(MclBls12Lib)]
        public static extern unsafe int mclBnGT_deserialize(ref GT x, void* buf, int bufSize);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnFp_clear(ref Fp x);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnFp_setInt(ref Fp y, int x);

        [DllImport(MclBls12Lib)]
        public static extern unsafe int mclBnFp_setLittleEndian(ref Fp y, void* bytes, int len);

        [DllImport(MclBls12Lib)]
        public static extern unsafe int mclBnFp_setLittleEndianMod(ref Fp y, void* bytes, int len);

        [DllImport(MclBls12Lib)]
        public static extern int mclBnFp_setStr(ref Fp x, [In] [MarshalAs(UnmanagedType.LPStr)] string buf, long bufSize, int ioMode);

        [DllImport(MclBls12Lib)]
        public static extern int mclBnFp_isValid(ref Fp x);

        [DllImport(MclBls12Lib)]
        public static extern int mclBnFp_isEqual(ref Fp x, ref Fp y);

        [DllImport(MclBls12Lib)]
        public static extern int mclBnFp_isZero(ref Fp x);

        [DllImport(MclBls12Lib)]
        public static extern int mclBnFp_isOne(ref Fp x);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnFp_setByCSPRNG(ref Fp x);

        [DllImport(MclBls12Lib)]
        public static extern int mclBnFp_setHashOf(ref Fp x, [In] [MarshalAs(UnmanagedType.LPStr)] string buf, long bufSize);

        [DllImport(MclBls12Lib)]
        public static extern int mclBnFp_getStr([Out] StringBuilder buf, long maxBufSize, ref Fp x, int ioMode);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnFp_neg(ref Fp y, ref Fp x);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnFp_inv(ref Fp y, ref Fp x);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnFp_add(ref Fp z, ref Fp x, ref Fp y);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnFp_dbl(ref Fp y, ref Fp x);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnFp_sub(ref Fp z, ref Fp x, ref Fp y);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnFp_mul(ref Fp z, ref Fp x, ref Fp y);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnFp_sqr(ref Fp y, ref Fp x);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnFp_div(ref Fp z, ref Fp x, ref Fp y);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnFr_clear(ref Fr x);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnFr_setInt(ref Fr y, int x);

        [DllImport(MclBls12Lib)]
        public static extern unsafe int mclBnFr_setLittleEndian(ref Fr y, void* bytes, int len);

        [DllImport(MclBls12Lib)]
        public static extern unsafe int mclBnFr_setLittleEndianMod(ref Fr y, void* bytes, int len);

        [DllImport(MclBls12Lib)]
        public static extern int mclBnFr_setStr(ref Fr x, [In] [MarshalAs(UnmanagedType.LPStr)] string buf, long bufSize, int ioMode);

        [DllImport(MclBls12Lib)]
        public static extern int mclBnFr_isValid(ref Fr x);

        [DllImport(MclBls12Lib)]
        public static extern int mclBnFr_isEqual(ref Fr x, ref Fr y);

        [DllImport(MclBls12Lib)]
        public static extern int mclBnFr_isZero(ref Fr x);

        [DllImport(MclBls12Lib)]
        public static extern int mclBnFr_isOne(ref Fr x);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnFr_setByCSPRNG(ref Fr x);

        [DllImport(MclBls12Lib)]
        public static extern int mclBnFr_setHashOf(ref Fr x, [In] [MarshalAs(UnmanagedType.LPStr)] string buf, long bufSize);

        [DllImport(MclBls12Lib)]
        public static extern int mclBnFr_getStr([Out] StringBuilder buf, long maxBufSize, ref Fr x, int ioMode);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnFr_neg(ref Fr y, ref Fr x);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnFr_inv(ref Fr y, ref Fr x);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnFr_add(ref Fr z, ref Fr x, ref Fr y);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnFr_dbl(ref Fr y, ref Fr x);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnFr_sub(ref Fr z, ref Fr x, ref Fr y);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnFr_mul(ref Fr z, ref Fr x, ref Fr y);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnFr_sqr(ref Fr y, ref Fr x);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnFr_div(ref Fr z, ref Fr x, ref Fr y);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnG1_clear(ref G1 x);

        [DllImport(MclBls12Lib)]
        public static extern int mclBnG1_setStr(ref G1 x, [In] [MarshalAs(UnmanagedType.LPStr)] string buf, long bufSize, int ioMode);

        [DllImport(MclBls12Lib)]
        public static extern int mclBnG1_isValid(ref G1 x);

        [DllImport(MclBls12Lib)]
        public static extern int mclBnG1_isEqual(ref G1 x, ref G1 y);

        [DllImport(MclBls12Lib)]
        public static extern int mclBnG1_isZero(ref G1 x);

        [DllImport(MclBls12Lib)]
        public static extern int mclBnG1_hashAndMapTo(ref G1 x, [In] [MarshalAs(UnmanagedType.LPStr)] string buf, long bufSize);

        [DllImport(MclBls12Lib)]
        public static extern long mclBnG1_getStr([Out] StringBuilder buf, long maxBufSize, ref G1 x, int ioMode);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnG1_neg(ref G1 y, ref G1 x);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnG1_dbl(ref G1 y, ref G1 x);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnG1_add(ref G1 z, ref G1 x, ref G1 y);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnG1_sub(ref G1 z, ref G1 x, ref G1 y);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnG1_mul(ref G1 z, ref G1 x, ref Fr y);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnG2_clear(ref G2 x);

        [DllImport(MclBls12Lib)]
        public static extern int mclBnG2_setStr(ref G2 x, [In] [MarshalAs(UnmanagedType.LPStr)] string buf, long bufSize, int ioMode);

        [DllImport(MclBls12Lib)]
        public static extern int mclBnG2_isValid(ref G2 x);

        [DllImport(MclBls12Lib)]
        public static extern int mclBnG2_isEqual(ref G2 x, ref G2 y);

        [DllImport(MclBls12Lib)]
        public static extern int mclBnG2_isZero(ref G2 x);

        [DllImport(MclBls12Lib)]
        public static extern int mclBnG2_hashAndMapTo(ref G2 x, [In] [MarshalAs(UnmanagedType.LPStr)] string buf, long bufSize);

        [DllImport(MclBls12Lib)]
        public static extern long mclBnG2_getStr([Out] StringBuilder buf, long maxBufSize, ref G2 x, int ioMode);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnG2_neg(ref G2 y, ref G2 x);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnG2_dbl(ref G2 y, ref G2 x);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnG2_add(ref G2 z, ref G2 x, ref G2 y);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnG2_sub(ref G2 z, ref G2 x, ref G2 y);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnG2_mul(ref G2 z, ref G2 x, ref Fr y);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnGT_clear(ref GT x);

        [DllImport(MclBls12Lib)]
        public static extern int mclBnGT_setStr(ref GT x, [In] [MarshalAs(UnmanagedType.LPStr)] string buf, long bufSize, int ioMode);

        [DllImport(MclBls12Lib)]
        public static extern int mclBnGT_isEqual(ref GT x, ref GT y);

        [DllImport(MclBls12Lib)]
        public static extern int mclBnGT_isZero(ref GT x);

        [DllImport(MclBls12Lib)]
        public static extern int mclBnGT_isOne(ref GT x);

        [DllImport(MclBls12Lib)]
        public static extern long mclBnGT_getStr([Out] StringBuilder buf, long maxBufSize, ref GT x, int ioMode);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnGT_neg(ref GT y, ref GT x);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnGT_inv(ref GT y, ref GT x);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnGT_add(ref GT z, ref GT x, ref GT y);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnGT_sub(ref GT z, ref GT x, ref GT y);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnGT_mul(ref GT z, ref GT x, ref GT y);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnGT_div(ref GT z, ref GT x, ref GT y);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnGT_pow(ref GT z, ref GT x, ref Fp y);

        [DllImport(MclBls12Lib)]
        public static extern void mclBn_pairing(ref GT z, ref G1 x, ref G2 y);

        [DllImport(MclBls12Lib)]
        public static extern void mclBn_finalExp(ref GT y, ref GT x);

        [DllImport(MclBls12Lib)]
        public static extern void mclBn_millerLoop(ref GT z, ref G1 x, ref G2 y);

        [DllImport(MclBls12Lib)]
        public static extern int mclBnFp_mapToG1(ref G1 y, ref Fp x);

        [DllImport(MclBls12Lib)]
        public static extern int mclBn_setMapToMode(int mode);

        [DllImport(MclBls12Lib)]
        public static extern unsafe void mclBnG1_mulVec(ref G1 z, void* x, void* y, int size);

        [DllImport(MclBls12Lib)]
        public static extern unsafe void mclBnG2_mulVec(ref G2 z, void* x, void* y, int size);

        // int mclBnFp2_mapToG2(ref G1 y, ref Fp x);

        public static void Init()
        {
            const int MCLBN_FR_UNIT_SIZE = 4;
            const int MCLBN_FP_UNIT_SIZE = 6;
            const int MCLBN_COMPILED_TIME_VAR = (MCLBN_FR_UNIT_SIZE) * 10 + (MCLBN_FP_UNIT_SIZE);
            const int MCL_BLS12_381 = 5;
            const int MCL_MAP_TO_MODE_HASH_TO_CURVE_07 = 5; // or 7?

            int initRes = mclBn_init(MCL_BLS12_381, MCLBN_COMPILED_TIME_VAR);
            if (initRes != 0)
            {
                throw new InvalidOperationException($"mclBn_init->{initRes}");
            }

            int mapModeRes = mclBn_setMapToMode(MCL_MAP_TO_MODE_HASH_TO_CURVE_07);
            if (mapModeRes != 0)
            {
                throw new InvalidOperationException($"mclBn_setMapToMode->{mapModeRes}");
            }
        }
    }
}