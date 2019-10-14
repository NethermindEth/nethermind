using System;
using System.Runtime.InteropServices;

namespace Cortex.Cryptography
{
    internal static class Bls384Interop
    {
#pragma warning disable IDE1006 // Naming Styles

        // Using https://github.com/herumi/bls
        // * Install Visual Studio C++ tools
        // * Open 64-bit command prompt
        // * "mklib.bat dll" worked (as does test, mentioned in readme)
        // * `make BLS_SWAP_G=1` then G1 is assigned to PublicKey and G2 is assigned to Signature.
        // * copy the output dll from bin folder

        // 	MCL_BLS12_381 = 5,
        public const int MCL_BLS12_381 = 5;

        /**
	        @file
	        @brief C API of 384-bit optimal ate pairing over BN curves
	        @author MITSUNARI Shigeo(@herumi)
	        @license modified new BSD license
	        http://opensource.org/licenses/BSD-3-Clause
        */

        //#define MCLBN_COMPILED_TIME_VAR ((MCLBN_FR_UNIT_SIZE) * 10 + (MCLBN_FP_UNIT_SIZE))
        // The +100 is for BLS_SWAP_G
        public const int MCLBN_COMPILED_TIME_VAR = MCLBN_FR_UNIT_SIZE * 10 + MCLBN_FP_UNIT_SIZE + 100;

        //#define MCLBN_FP_UNIT_SIZE 6
        //#define MCLBN_FR_UNIT_SIZE 6
        private const int MCLBN_FP_UNIT_SIZE = 6;

        private const int MCLBN_FR_UNIT_SIZE = 4;

        // BLS_DLL_API void blsGetPublicKey(blsPublicKey* pub, const blsSecretKey* sec);
        [DllImport(@"bls384_256.dll")]
        public static extern int blsGetPublicKey(out BlsPublicKey pub, BlsSecretKey sec);

        /*
	        initialize this library
	        call this once before using the other functions
	        @param curve [in] enum value defined in mcl/bn.h
	        @param compiledTimeVar [in] specify MCLBN_COMPILED_TIME_VAR,
	        which macro is used to make sure that the values
	        are the same when the library is built and used
	        @return 0 if success
	        @note blsInit() is not thread safe
        */

        // BLS_DLL_API int blsInit(int curve, int compiledTimeVar);
        [DllImport(@"bls384_256.dll")]
        public static extern int blsInit(int curve, int compiledTimeVar);

        /*
	        set secretKey if system has /dev/urandom or CryptGenRandom
	        return 0 if success else -1
        */

        // BLS_DLL_API int blsSecretKeySetByCSPRNG(blsSecretKey* sec);
        [DllImport(@"bls384_256.dll")]
        public static extern int blsSecretKeySetByCSPRNG(out BlsSecretKey sec);

        // calculate the has of m and sign the hash
        // BLS_DLL_API void blsSign(blsSignature* sig, const blsSecretKey* sec, const void* m, mclSize size);
        [DllImport(@"bls384_256.dll")]
        public static extern int blsSign(out BlsSignature sig, BlsSecretKey sec, byte[] m, int size);

        /*
	        sign the hash
	        use the low (bitSize of r) - 1 bit of h
	        return 0 if success else -1
	        NOTE : return false if h is zero or c1 or -c1 value for BN254. see hashTest() in test/bls_test.hpp
        */

        //BLS_DLL_API int blsSignHash(blsSignature* sig, const blsSecretKey* sec, const void* h, mclSize size);
        [DllImport(@"bls384_256.dll")]
        public static extern int blsSignHash(out BlsSignature sig, BlsSecretKey sec, byte[] h, int size);

        // return 1 if valid
        // BLS_DLL_API int blsVerify(const blsSignature* sig, const blsPublicKey* pub, const void* m, mclSize size);
        [DllImport(@"bls384_256.dll")]
        public static extern int blsVerify(BlsSignature sig, BlsPublicKey pub, byte[] m, int size);

        // return 1 if valid
        //BLS_DLL_API int blsVerifyHash(const blsSignature* sig, const blsPublicKey* pub, const void* h, mclSize size);
        [DllImport(@"bls384_256.dll")]
        public static extern int blsVerifyHash(BlsSignature sig, BlsPublicKey pub, byte[] h, int size);

        /*
	        verify X == sY by checking e(X, sQ) = e(Y, Q)
	        @param X [in]
	        @param Y [in]
	        @param pub [in] pub = sQ
	        @return 1 if e(X, pub) = e(Y, Q) else 0
        */

        //BLS_DLL_API int blsVerifyPairing(const blsSignature* X, const blsSignature* Y, const blsPublicKey* pub);
        // Note: bls_verify in Eth 2.0 has "Verify that e(pubkey, hash_to_G2(message_hash, domain)) == e(g, signature)"
        // i.e. if X = G2 of hash, then Y = signature ??
        [DllImport(@"bls384.dll")]
        public static extern int blsVerifyPairing(BlsSignature x, BlsSignature y, BlsPublicKey pub);

        //typedef struct {
        //#ifdef BLS_SWAP_G
        //	mclBnG1 v;
        //#else
        //    mclBnG2 v;
        //#endif
        //}
        //blsPublicKey;
        public struct BlsPublicKey
        {
            public MclBnG1 v;

            public override string ToString()
            {
                return BitConverter.ToString(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref this, 1)).ToArray());
            }
        }

        //typedef struct {
        //    mclBnFr v;
        //    }
        //    blsSecretKey;
        public struct BlsSecretKey
        {
            public MclBnFr v;

            public override string ToString()
            {
                return BitConverter.ToString(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref this, 1)).ToArray());
            }
        }

        //typedef struct {
        //#ifdef BLS_SWAP_G
        //	mclBnG2 v;
        //#else
        //mclBnG1 v;
        //#endif
        //} blsSignature;
        public struct BlsSignature
        {
            public MclBnG2 v;

            public override string ToString()
            {
                return BitConverter.ToString(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref this, 1)).ToArray());
            }
        }

        //typedef struct {
        //uint64_t d[MCLBN_FP_UNIT_SIZE];
        //}
        //mclBnFp;
        public struct MclBnFp
        {
            public ulong d_0;
            public ulong d_1;
            public ulong d_2;
            public ulong d_3;
            public ulong d_4;
            public ulong d_5;

            public override string ToString()
            {
                return $"FP(ulong[{d_0:x},{d_1:x},{d_2:x},{d_3:x},{d_4:x},{d_5:x}])";
            }
        }

        /*
	        x = d[0] + d[1] i where i^2 = -1
        */

        //typedef struct {
        //    mclBnFp d[2];
        //}
        //mclBnFp2;
        public struct MclBnFp2
        {
            public MclBnFp d_0;
            public MclBnFp d_1;

            public override string ToString()
            {
                return $"FP2({d_0},{d_1})";
            }
        }

        /*
            G1 and G2 are isomorphism to Fr
        */

        //typedef struct {
        //    uint64_t d[MCLBN_FR_UNIT_SIZE];
        //    }
        //    mclBnFr;
        public struct MclBnFr
        {
            public ulong d_0;
            public ulong d_1;
            public ulong d_2;
            public ulong d_3;

            public override string ToString()
            {
                return $"FR(ulong[{d_0:x},{d_1:x},{d_2:x},{d_3:x}])";
            }
        }

        /*
	        G1 is defined over Fp
        */

        //typedef struct {
        //    mclBnFp x, y, z;
        //    }
        //    mclBnG1;
        public struct MclBnG1
        {
            public MclBnFp x;
            public MclBnFp y;
            public MclBnFp z;

            public override string ToString()
            {
                return $"G1(x={x},y={y},z={z})";
            }
        }

        //typedef struct {
        //    mclBnFp2 x, y, z;
        //}
        //mclBnG2;
        public struct MclBnG2
        {
            public MclBnFp2 x;
            public MclBnFp2 y;
            public MclBnFp2 z;

            public override string ToString()
            {
                return $"G2(x={x},y={y},z={z})";
            }
        }

        //# ifdef __EMSCRIPTEN__
        //        // avoid 64-bit integer
        //#define mclSize unsigned int
        //#define mclInt int
        //#else
        //        // use #define for cgo
        //#define mclSize size_t
        //#define mclInt int64_t
        //#endif

#pragma warning restore IDE1006 // Naming Styles
    }
}
