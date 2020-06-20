using System;
using System.Runtime.InteropServices;
using System.Text;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Native;

namespace Nethermind.Crypto
{
    public static class Bn256
    {
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

        [StructLayout(LayoutKind.Sequential)]
        public struct Fr : IEquatable<Fr>
        {
            private ulong v0, v1, v2, v3;

            public void Clear()
            {
                mclBnFr_clear(ref this);
            }

            public void SetInt(int x)
            {
                mclBnFr_setInt(ref this, x);
            }

            public void SetStr(string s, int ioMode)
            {
                if (mclBnFr_setStr(ref this, s, s.Length, ioMode) != 0)
                {
                    throw new ArgumentException("mclBnFr_setStr" + s);
                }
            }
            
            public unsafe void Deserialize(Span<byte> data, int len)
            {
                fixed (byte* dataPtr = &MemoryMarshal.GetReference(data))
                {
                    mclBnFr_deserialize(ref this, dataPtr, len);
                }
            }
            
            public unsafe void DeserializeFp(Span<byte> data, int len)
            {
                fixed (byte* dataPtr = &MemoryMarshal.GetReference(data))
                {
                    mclBnFp_deserialize(ref this, dataPtr, len);
                }
            }

            public unsafe void FrSetLittleEndian(byte[] data, int len)
            {
                fixed (byte* serializedPtr = &MemoryMarshal.GetReference(data.AsSpan()))
                {
                    mclBnFr_setLittleEndian(ref this, serializedPtr, len);
                }
            }

            public unsafe void FrSetLittleEndianMod(byte[] data, int len)
            {
                fixed (byte* serializedPtr = &MemoryMarshal.GetReference(data.AsSpan()))
                {
                    mclBnFr_setLittleEndian(ref this, serializedPtr, len);
                }
            }

            public bool IsValid()
            {
                return mclBnFr_isValid(ref this) == 1;
            }

            public bool Equals(Fr rhs)
            {
                return mclBnFr_isEqual(ref this, ref rhs) == 1;
            }
            
            // public override bool Equals(Fr other)
            // {
            //     return v0 == other.v0 && v1 == other.v1 && v2 == other.v2 && v3 == other.v3;
            // }

            public bool IsZero()
            {
                return mclBnFr_isZero(ref this) == 1;
            }

            public bool IsOne()
            {
                return mclBnFr_isOne(ref this) == 1;
            }

            public void SetByCSPRNG()
            {
                mclBnFr_setByCSPRNG(ref this);
            }

            public void SetHashOf(String s)
            {
                if (mclBnFr_setHashOf(ref this, s, s.Length) != 0)
                {
                    throw new InvalidOperationException("mclBnFr_setHashOf:" + s);
                }
            }

            public string GetStr(int ioMode)
            {
                StringBuilder sb = new StringBuilder(1024);
                long size = mclBnFr_getStr(sb, sb.Capacity, ref this, ioMode);
                if (size == 0)
                {
                    throw new InvalidOperationException("mclBnFr_getStr:");
                }

                return sb.ToString();
            }

            public override string ToString()
            {
                return GetStr(0);
            }

            public void Neg(Fr x)
            {
                mclBnFr_neg(ref this, ref x);
            }

            public void Inv(Fr x)
            {
                mclBnFr_inv(ref this, ref x);
            }

            public void Add(Fr x, Fr y)
            {
                mclBnFr_add(ref this, ref x, ref y);
            }
            
            public void Dbl(Fr x)
            {
                mclBnFr_dbl(ref this, ref x);
            }
            
            public void AddFp(Fr x, Fr y)
            {
                mclBnFp_add(ref this, ref x, ref y);
            }

            public void Sub(Fr x, Fr y)
            {
                mclBnFr_sub(ref this, ref x, ref y);
            }

            public void Mul(Fr x, Fr y)
            {
                mclBnFr_mul(ref this, ref x, ref y);
            }
            
            public void MulFp(Fr x, Fr y)
            {
                mclBnFp_mul(ref this, ref x, ref y);
            }
            
            public void Sqr(Fr x)
            {
                mclBnFr_sqr(ref this, ref x);
            }

            public void Div(Fr x, Fr y)
            {
                mclBnFr_div(ref this, ref x, ref y);
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

        [StructLayout(LayoutKind.Sequential)]
        public struct G1
        {
            private ulong v00, v01, v02, v03, v04, v05, v06, v07, v08, v09, v10, v11;

            public void Clear()
            {
                mclBnG1_clear(ref this);
            }

            public static G1? CreateFromBigEndian(Span<byte> x, Span<byte> y)
            {
                UInt256.CreateFromBigEndian(out UInt256 xInt, x);
                UInt256.CreateFromBigEndian(out UInt256 yInt, y);
                return Create(xInt, yInt);
            }
            
            public unsafe void Deserialize(Span<byte> data, int len)
            {
                fixed (byte* dataPtr = &MemoryMarshal.GetReference(data))
                {
                    mclBnG1_deserialize(ref this, dataPtr, len);
                }
            }
            
            public unsafe void Serialize(Span<byte> data, int len)
            {
                fixed (byte* dataPtr = &MemoryMarshal.GetReference(data))
                {
                    mclBnG1_serialize(dataPtr, len, ref this);
                }
            }

            // public static bool IsOnCurve(UInt256 x, UInt256 y)
            // {
            //     BigInteger r = BigInteger.Parse("2523648240000001ba344d8000000007ff9f800000000010a10000000000000d", NumberStyles.HexNumber);
            //     BigInteger p = BigInteger.Parse("2523648240000001ba344d80000000086121000000000013a700000000000013", NumberStyles.HexNumber);
            //     // return true;
            //     // no idea why below never works
            //
            //     if (x.IsZero && y.IsZero)
            //     {
            //         return true;
            //     }
            //
            //     Span<byte> bytesX = stackalloc byte[32];
            //     x.ToLittleEndian(bytesX);
            //     Fr xFr = new Fr();
            //     xFr.Deserialize(bytesX, bytesX.Length);
            //     
            //     Fr xFp = new Fr();
            //     xFp.DeserializeFp(bytesX, bytesX.Length);
            //     
            //     Span<byte> bytesY = stackalloc byte[32];
            //     y.ToLittleEndian(bytesY);
            //     Fr yFr = new Fr();
            //     yFr.Deserialize(bytesY, bytesY.Length);
            //     
            //     Fr yFp = new Fr();
            //     yFp.DeserializeFp(bytesY, bytesY.Length);
            //
            //     // y^2 = x^3 + 2
            //     //
            //     Fr left = new Fr();
            //     left.Sqr(yFr);
            //     
            //     Fr leftAlt = new Fr();
            //     leftAlt.Mul(yFr, yFr);
            //
            //     Fr resAlt = MulAlternative(xFr, y);
            //     
            //     Fr leftAltFp = new Fr();
            //     leftAltFp.MulFp(yFp, yFp);
            //     
            //     Fr leftAltFrFp = new Fr();
            //     leftAltFrFp.MulFp(yFr, yFr);
            //     
            //     Fr leftAltFpFR = new Fr();
            //     leftAltFpFR.Mul(yFp, yFp);
            //
            //     Fr leftOp = yFp * yFp;
            //     Fr leftOp2 = yFr * yFr;
            //     Fr leftOp3 = yFr * yFp;
            //     Fr leftOp4 = yFp * yFr;
            //     
            //     
            //     Fr two = new Fr();
            //     two.SetInt(3);
            //     //
            //     Fr right = new Fr();
            //     right.Sqr(xFr);
            //     right.Mul(right, xFr);
            //     right.Add(right, two);
            //     
            //     Fr rightAlt = new Fr();
            //     rightAlt.Mul(xFr, xFr);
            //     rightAlt.Mul(rightAlt, xFr);
            //     
            //     Fr rightAltFp = new Fr();
            //     rightAltFp.MulFp(xFp, xFp);
            //     rightAltFp.MulFp(rightAltFp, xFp);
            //
            //     return left.Equals(right);
            //     // return true;
            // }
            
            // private static Fr MulAlternative(Bn256.Fr g1, UInt256 s)
            // {
            //     if (s.IsZero) // P * 0 = 0
            //     {
            //         g1.Clear();
            //     }
            //
            //     if (g1.IsZero())
            //     {
            //         return g1;
            //     }
            //
            //     Fr res = new Bn256.Fr();
            //     res.Clear();
            //
            //     int bitLength = ((BigInteger)s).BitLength();
            //     for (int i = bitLength - 1; i >= 0; i--)
            //     {
            //         res.Dbl(res);
            //         if (s.TestBit(i))
            //         {
            //             res.Add(res, g1);
            //         }
            //     }
            //
            //     return res;
            // }
            
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
                StringBuilder sb = new StringBuilder(1024);
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

            public void Mul(G1 x, Fr y)
            {
                mclBnG1_mul(ref this, ref x, ref y);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct G2
        {
            private ulong v00, v01, v02, v03, v04, v05, v06, v07, v08, v09, v10, v11;
            private ulong v12, v13, v14, v15, v16, v17, v18, v19, v20, v21, v22, v23;

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

            public void Mul(G2 x, Fr y)
            {
                mclBnG2_mul(ref this, ref x, ref y);
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

            public void Pow(GT x, Fr y)
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