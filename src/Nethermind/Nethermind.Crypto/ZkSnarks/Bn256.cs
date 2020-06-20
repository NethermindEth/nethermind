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

    [StructLayout(LayoutKind.Sequential)]
        public struct GT
        {
            private ulong v00, v01, v02, v03, v04, v05, v06, v07, v08, v09, v10, v11;
            private ulong v12, v13, v14, v15, v16, v17, v18, v19, v20, v21, v22, v23;
            private ulong v24, v25, v26, v27, v28, v29, v30, v31, v32, v33, v34, v35;
            private ulong v36, v37, v38, v39, v40, v41, v42, v43, v44, v45, v46, v47;

            public void Clear()
            {
                Bn256.mclBnGT_clear(ref this);
            }

            public void setStr(String s, int ioMode)
            {
                if (Bn256.mclBnGT_setStr(ref this, s, s.Length, ioMode) != 0)
                {
                    throw new ArgumentException("Bn256.mclBnGT_setStr:" + s);
                }
            }

            public bool Equals(GT rhs)
            {
                return Bn256.mclBnGT_isEqual(ref this, ref rhs) == 1;
            }

            public bool IsZero()
            {
                return Bn256.mclBnGT_isZero(ref this) == 1;
            }

            public bool IsOne()
            {
                return Bn256.mclBnGT_isOne(ref this) == 1;
            }

            public string GetStr(int ioMode)
            {
                StringBuilder sb = new StringBuilder(1024);
                long size = Bn256.mclBnGT_getStr(sb, sb.Capacity, ref this, ioMode);
                if (size == 0)
                {
                    throw new InvalidOperationException("Bn256.mclBnGT_getStr:");
                }

                return sb.ToString();
            }

            public void Neg(GT x)
            {
                Bn256.mclBnGT_neg(ref this, ref x);
            }

            public void Inv(GT x)
            {
                Bn256.mclBnGT_inv(ref this, ref x);
            }

            public void Add(GT x, GT y)
            {
                Bn256.mclBnGT_add(ref this, ref x, ref y);
            }

            public void Sub(GT x, GT y)
            {
                Bn256.mclBnGT_sub(ref this, ref x, ref y);
            }

            public void Mul(GT x, GT y)
            {
                Bn256.mclBnGT_mul(ref this, ref x, ref y);
            }

            public void Div(GT x, GT y)
            {
                Bn256.mclBnGT_div(ref this, ref x, ref y);
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

            public void Pow(GT x, Fr y)
            {
                Bn256.mclBnGT_pow(ref this, ref x, ref y);
            }

            public void Pairing(G1 x, G2 y)
            {
                Bn256.mclBn_pairing(ref this, ref x, ref y);
            }

            public void FinalExp(GT x)
            {
                Bn256.mclBn_finalExp(ref this, ref x);
            }

            public void MillerLoop(G1 x, G2 y)
            {
                Bn256.mclBn_millerLoop(ref this, ref x, ref y);
            }
        }
}