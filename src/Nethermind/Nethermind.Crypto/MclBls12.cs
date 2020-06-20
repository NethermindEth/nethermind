using System;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Nethermind.Crypto
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
        public static extern void mclBnFp_clear(ref Fp x);

        [DllImport(MclBls12Lib)]
        public static extern void mclBnFp_setInt(ref Fp y, int x);

        [DllImport(MclBls12Lib)]
        public static extern unsafe int mclBnFp_setLittleEndian(ref Fp y, void* bytes, int len);

        [DllImport(MclBls12Lib)]
        public static extern unsafe int mclBnFp_setLittleEndianMod(ref Fp y, void* bytes, int len);

        [DllImport(MclBls12Lib)]
        public static extern unsafe int mclBnFp_serialize(void* buf, int bufSize, ref Fp x);
        
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
        public static extern void mclBnG1_mul(ref G1 z, ref G1 x, ref Fp y);

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
        public static extern void mclBnG2_mul(ref G2 z, ref G2 x, ref Fp y);

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

        [StructLayout(LayoutKind.Sequential)]
        public struct Fp : IEquatable<Fp>
        {
            private ulong v0, v1, v2, v3, v4, v5;

            public void Clear()
            {
                mclBnFp_clear(ref this);
            }

            public void SetInt(int x)
            {
                mclBnFp_setInt(ref this, x);
            }

            public void SetStr(string s, int ioMode)
            {
                if (mclBnFp_setStr(ref this, s, s.Length, ioMode) != 0)
                {
                    throw new ArgumentException("mclBnFp_setStr" + s);
                }
            }
            
            public unsafe void Deserialize(Span<byte> data, int len)
            {
                fixed (byte* dataPtr = &MemoryMarshal.GetReference(data))
                {
                    mclBnFp_deserialize(ref this, dataPtr, len);
                }
            }
            
            public unsafe void DeserializeFp(Span<byte> data, int len)
            {
                fixed (byte* dataPtr = &MemoryMarshal.GetReference(data))
                {
                    mclBnFp_deserialize(ref this, dataPtr, len);
                }
            }

            public unsafe void FpSetLittleEndian(Span<byte> data, int len)
            {
                fixed (byte* serializedPtr = &MemoryMarshal.GetReference(data))
                {
                    mclBnFp_setLittleEndian(ref this, serializedPtr, len);
                }
            }

            public unsafe void FpSetLittleEndianMod(Span<byte> data, int len)
            {
                fixed (byte* serializedPtr = &MemoryMarshal.GetReference(data))
                {
                    mclBnFp_setLittleEndianMod(ref this, serializedPtr, len);
                }
            }

            public bool IsValid()
            {
                return mclBnFp_isValid(ref this) == 1;
            }

            public bool Equals(Fp rhs)
            {
                return mclBnFp_isEqual(ref this, ref rhs) == 1;
            }
            
            // public override bool Equals(Fp other)
            // {
            //     return v0 == other.v0 && v1 == other.v1 && v2 == other.v2 && v3 == other.v3;
            // }

            public bool IsZero()
            {
                return mclBnFp_isZero(ref this) == 1;
            }

            public bool IsOne()
            {
                return mclBnFp_isOne(ref this) == 1;
            }

            public void SetByCSPRNG()
            {
                mclBnFp_setByCSPRNG(ref this);
            }

            public void SetHashOf(String s)
            {
                if (mclBnFp_setHashOf(ref this, s, s.Length) != 0)
                {
                    throw new InvalidOperationException("mclBnFp_setHashOf:" + s);
                }
            }

            public string GetStr(int ioMode)
            {
                StringBuilder sb = new StringBuilder(1024);
                long size = mclBnFp_getStr(sb, sb.Capacity, ref this, ioMode);
                if (size == 0)
                {
                    throw new InvalidOperationException("mclBnFp_getStr:");
                }

                return sb.ToString();
            }

            public override string ToString()
            {
                return GetStr(0);
            }

            public void Neg(Fp x)
            {
                mclBnFp_neg(ref this, ref x);
            }

            public void Inv(Fp x)
            {
                mclBnFp_inv(ref this, ref x);
            }

            public void Add(Fp x, Fp y)
            {
                mclBnFp_add(ref this, ref x, ref y);
            }
            
            public void Dbl(Fp x)
            {
                mclBnFp_dbl(ref this, ref x);
            }
            
            public void AddFp(Fp x, Fp y)
            {
                mclBnFp_add(ref this, ref x, ref y);
            }

            public void Sub(Fp x, Fp y)
            {
                mclBnFp_sub(ref this, ref x, ref y);
            }

            public void Mul(Fp x, Fp y)
            {
                mclBnFp_mul(ref this, ref x, ref y);
            }
            
            public void MulFp(Fp x, Fp y)
            {
                mclBnFp_mul(ref this, ref x, ref y);
            }
            
            public void Sqr(Fp x)
            {
                mclBnFp_sqr(ref this, ref x);
            }

            public void Div(Fp x, Fp y)
            {
                mclBnFp_div(ref this, ref x, ref y);
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
                mclBnFp_mapToG1(ref g1, ref this);
                return g1;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct G1
        {
            private ulong v00, v01, v02, v03, v04, v05, v06, v07, v08, v09, v10, v11, v12, v13, v14, v15, v16, v17;

            public void Clear()
            {
                mclBnG1_clear(ref this);
            }

            public unsafe void Deserialize(ReadOnlySpan<byte> data, int len)
            {
                fixed (byte* dataPtr = data)
                {
                    int readBytes = mclBnG1_deserialize(ref this, dataPtr, len);
                }
            }
            
            public unsafe void Serialize(ReadOnlySpan<byte> data, int len)
            {
                fixed (byte* dataPtr = data)
                {
                    mclBnG1_serialize(dataPtr, len, ref this);
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
                if (mclBnG1_setStr(ref this, s, s.Length, ioMode) != 0)
                {
                    throw new ArgumentException("mclBnG1_setStr:" + s);
                }
            }

            public bool IsValid()
            {
                return mclBnG1_isValid(ref this) == 1;
            }

            public bool Equals(G1 rhs)
            {
                return mclBnG1_isEqual(ref this, ref rhs) == 1;
            }

            public bool IsZero()
            {
                return mclBnG1_isZero(ref this) == 1;
            }

            public void HashAndMapTo(String s)
            {
                if (mclBnG1_hashAndMapTo(ref this, s, s.Length) != 0)
                {
                    throw new ArgumentException("mclBnG1_hashAndMapTo:" + s);
                }
            }

            public string GetStr(int ioMode)
            {
                StringBuilder sb = new StringBuilder(2048);
                long size = mclBnG1_getStr(sb, sb.Capacity, ref this, ioMode);
                if (size == 0)
                {
                    throw new InvalidOperationException("mclBnG1_getStr:");
                }

                return sb.ToString();
            }

            public override string ToString()
            {
                return GetStr(0);
            }

            public void Neg(G1 x)
            {
                mclBnG1_neg(ref this, ref x);
            }

            public void Dbl(G1 x)
            {
                mclBnG1_dbl(ref this, ref x);
            }

            public void Add(G1 x, G1 y)
            {
                mclBnG1_add(ref this, ref x, ref y);
            }

            public void Sub(G1 x, G1 y)
            {
                mclBnG1_sub(ref this, ref x, ref y);
            }

            public void Mul(G1 x, Fp y)
            {
                mclBnG1_mul(ref this, ref x, ref y);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct G2
        {
            private ulong v00, v01, v02, v03, v04, v05, v06, v07, v08, v09, v10, v11, v12, v13, v14, v15, v16, v17;
            private ulong v18, v19, v20, v21, v22, v23, v24, v25, v26, v27, v28, v29, v30, v31, v32, v33, v34, v35;

            public void Clear()
            {
                mclBnG2_clear(ref this);
            }

            public void setStr(String s, int ioMode)
            {
                if (mclBnG2_setStr(ref this, s, s.Length, ioMode) != 0)
                {
                    throw new ArgumentException("mclBnG2_setStr:" + s);
                }
            }

            public bool IsValid()
            {
                return mclBnG2_isValid(ref this) == 1;
            }

            public bool Equals(G2 rhs)
            {
                return mclBnG2_isEqual(ref this, ref rhs) == 1;
            }

            public bool IsZero()
            {
                return mclBnG2_isZero(ref this) == 1;
            }

            public void HashAndMapTo(String s)
            {
                if (mclBnG2_hashAndMapTo(ref this, s, s.Length) != 0)
                {
                    throw new ArgumentException("mclBnG2_hashAndMapTo:" + s);
                }
            }

            public string GetStr(int ioMode)
            {
                StringBuilder sb = new StringBuilder(1024);
                long size = mclBnG2_getStr(sb, sb.Capacity, ref this, ioMode);
                if (size == 0)
                {
                    throw new InvalidOperationException("mclBnG2_getStr:");
                }

                return sb.ToString();
            }

            public static G2 CreateFpomBigEndian(Span<byte> a, Span<byte> b, Span<byte> c, Span<byte> d)
            {
                var aInt = new BigInteger(a, true, true);
                var bInt = new BigInteger(b, true, true);
                var cInt = new BigInteger(c, true, true);
                var dInt = new BigInteger(d, true, true);
                return Create(aInt, bInt, cInt, dInt);
            }

            public static G2 Create(BigInteger a, BigInteger b, BigInteger c, BigInteger d)
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
                mclBnG2_neg(ref this, ref x);
            }

            public void Dbl(G2 x)
            {
                mclBnG2_dbl(ref this, ref x);
            }

            public void Add(G2 x, G2 y)
            {
                mclBnG2_add(ref this, ref x, ref y);
            }

            public void Sub(G2 x, G2 y)
            {
                mclBnG2_sub(ref this, ref x, ref y);
            }

            public void Mul(G2 x, Fp y)
            {
                mclBnG2_mul(ref this, ref x, ref y);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GT
        {
            private ulong v00, v01, v02, v03, v04, v05, v06, v07, v08, v09, v10, v11, v12, v13, v14, v15, v16, v17;
            private ulong v18, v19, v20, v21, v22, v23, v24, v25, v26, v27, v28, v29, v30, v31, v32, v33, v34, v35;
            private ulong v36, v37, v38, v39, v40, v41, v42, v43, v44, v45, v46, v47, v48, v49, v50, v51, v52, v53;
            private ulong v54, v55, v56, v57, v58, v59, v60, v61, v62, v63, v64, v65, v66, v67, v68, v69, v70, v71;

            public void Clear()
            {
                mclBnGT_clear(ref this);
            }

            public void setStr(String s, int ioMode)
            {
                if (mclBnGT_setStr(ref this, s, s.Length, ioMode) != 0)
                {
                    throw new ArgumentException("mclBnGT_setStr:" + s);
                }
            }

            public bool Equals(GT rhs)
            {
                return mclBnGT_isEqual(ref this, ref rhs) == 1;
            }

            public bool IsZero()
            {
                return mclBnGT_isZero(ref this) == 1;
            }

            public bool IsOne()
            {
                return mclBnGT_isOne(ref this) == 1;
            }

            public string GetStr(int ioMode)
            {
                StringBuilder sb = new StringBuilder(1024);
                long size = mclBnGT_getStr(sb, sb.Capacity, ref this, ioMode);
                if (size == 0)
                {
                    throw new InvalidOperationException("mclBnGT_getStr:");
                }

                return sb.ToString();
            }

            public void Neg(GT x)
            {
                mclBnGT_neg(ref this, ref x);
            }

            public void Inv(GT x)
            {
                mclBnGT_inv(ref this, ref x);
            }

            public void Add(GT x, GT y)
            {
                mclBnGT_add(ref this, ref x, ref y);
            }

            public void Sub(GT x, GT y)
            {
                mclBnGT_sub(ref this, ref x, ref y);
            }

            public void Mul(GT x, GT y)
            {
                mclBnGT_mul(ref this, ref x, ref y);
            }

            public void Div(GT x, GT y)
            {
                mclBnGT_div(ref this, ref x, ref y);
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
                mclBnGT_pow(ref this, ref x, ref y);
            }

            public void Pairing(G1 x, G2 y)
            {
                mclBn_pairing(ref this, ref x, ref y);
            }

            public void FinalExp(GT x)
            {
                mclBn_finalExp(ref this, ref x);
            }

            public void MillerLoop(G1 x, G2 y)
            {
                mclBn_millerLoop(ref this, ref x, ref y);
            }
        }
    }
}