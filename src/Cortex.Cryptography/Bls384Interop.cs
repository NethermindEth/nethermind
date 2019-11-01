using System.Runtime.InteropServices;

namespace Cortex.Cryptography
{
    internal static class Bls384Interop
    //public static class Bls384Interop
    {
        // 	MCL_BLS12_381 = 5,
        public const int MCL_BLS12_381 = 5;

        //#define MCLBN_COMPILED_TIME_VAR ((MCLBN_FR_UNIT_SIZE) * 10 + (MCLBN_FP_UNIT_SIZE))
        // The +100 is for BLS_SWAP_G
        public const int MCLBN_COMPILED_TIME_VAR = MCLBN_FR_UNIT_SIZE * 10 + MCLBN_FP_UNIT_SIZE + 100;

        private const string DllName = "bls384_256.dll";

        // Notes on passing Span as pointer
        // https://medium.com/@antao.almada/p-invoking-using-span-t-a398b86f95d3
        // https://ericsink.com/entries/utf8z.html

        // Using https://github.com/herumi/bls
        // * Install Visual Studio C++ tools
        // * Open 64-bit command prompt
        // * "mklib.bat dll" worked (as does test, mentioned in readme)
        // * `make BLS_SWAP_G=1` then G1 is assigned to PublicKey and G2 is assigned to Signature.
        // * copy the output dll from bin folder
        /**
	        @file
	        @brief C API of 384-bit optimal ate pairing over BN curves
	        @author MITSUNARI Shigeo(@herumi)
	        @license modified new BSD license
	        http://opensource.org/licenses/BSD-3-Clause
        */

        //#define MCLBN_FP_UNIT_SIZE 6
        //#define MCLBN_FR_UNIT_SIZE 6
        private const int MCLBN_FP_UNIT_SIZE = 6;

        private const int MCLBN_FR_UNIT_SIZE = 4;

        // BLS_DLL_API void blsGetPublicKey(blsPublicKey* pub, const blsSecretKey* sec);
        [DllImport(DllName, EntryPoint = "blsGetPublicKey")]
        public static extern void GetPublicKey(out BlsPublicKey pub, BlsSecretKey sec);

        //initialize this library
        //call this once before using the other functions
        //@param curve [in] enum value defined in mcl/bn.h
        //@param compiledTimeVar [in] specify MCLBN_COMPILED_TIME_VAR,
        //which macro is used to make sure that the values
        //are the same when the library is built and used
        //@return 0 if success
        //@note blsInit() is not thread safe
        // BLS_DLL_API int blsInit(int curve, int compiledTimeVar);
        [DllImport(DllName, EntryPoint = "blsInit")]
        public static extern int Init(int curve, int compiledTimeVar);

        //BLS_DLL_API mclSize blsPublicKeyDeserialize(blsPublicKey* pub, const void* buf, mclSize bufSize);
        [DllImport(DllName, EntryPoint = "blsPublicKeyDeserialize")]
        public static extern unsafe int PublicKeyDeserialize(out BlsPublicKey pub, byte* buf, int bufSize);

        //BLS_DLL_API mclSize blsPublicKeySerialize(void *buf, mclSize maxBufSize, const blsPublicKey *pub);
        [DllImport(DllName, EntryPoint = "blsPublicKeySerialize")]
        public static extern unsafe int PublicKeySerialize(byte* buf, int maxBufSize, in BlsPublicKey pub);

        // return read byte size if success else 0
        //BLS_DLL_API mclSize blsIdDeserialize(blsId* id, const void* buf, mclSize bufSize);
        //BLS_DLL_API mclSize blsSecretKeyDeserialize(blsSecretKey* sec, const void* buf, mclSize bufSize);
        [DllImport(DllName, EntryPoint = "blsSecretKeyDeserialize")]
        public static extern unsafe int SecretKeyDeserialize(out BlsSecretKey sec, byte* buf, int bufSize);

        // return written byte size if success else 0
        //BLS_DLL_API mclSize blsIdSerialize(void *buf, mclSize maxBufSize, const blsId *id);
        //BLS_DLL_API mclSize blsSecretKeySerialize(void *buf, mclSize maxBufSize, const blsSecretKey *sec);
        [DllImport(DllName, EntryPoint = "blsSecretKeySerialize")]
        public static extern unsafe int SecretKeySerialize(byte* buf, int maxBufSize, BlsSecretKey sec);

        //set ETH serialization mode for BLS12-381
        //@param ETHserialization [in] 1:enable,  0:disable
        //@note ignore the flag if curve is not BLS12-381
        //BLS_DLL_API void blsSetETHserialization(int ETHserialization);
        [DllImport(DllName, EntryPoint = "blsSetETHserialization")]
        public static extern void SetETHserialization(int ETHserialization);

        // set secretKey if system has /dev/urandom or CryptGenRandom
        // return 0 if success else -1
        // BLS_DLL_API int blsSecretKeySetByCSPRNG(blsSecretKey* sec);
        //[DllImport(DllName, EntryPoint = "blsSecretKeySetByCSPRNG")]
        //public static extern int SecretKeySetByCSPRNG(out BlsSecretKey sec);
        // calculate the has of m and sign the hash
        // BLS_DLL_API void blsSign(blsSignature* sig, const blsSecretKey* sec, const void* m, mclSize size);
        [DllImport(DllName, EntryPoint = "blsSign")]
        public static extern unsafe int Sign(out BlsSignature sig, BlsSecretKey sec, byte* m, int size);

        //BLS_DLL_API void blsSignatureAdd(blsSignature* sig, const blsSignature* rhs);
        [DllImport(DllName, EntryPoint = "blsSignatureAdd")]
        public static extern void SignatureAdd(ref BlsSignature sig, BlsSignature rhs);

        //BLS_DLL_API mclSize blsSignatureDeserialize(blsSignature* sig, const void* buf, mclSize bufSize);
        [DllImport(DllName, EntryPoint = "blsSignatureDeserialize")]
        public static extern unsafe int SignatureDeserialize(out BlsSignature sig, byte* buf, int bufSize);

        //BLS_DLL_API mclSize blsSignatureSerialize(void *buf, mclSize maxBufSize, const blsSignature *sig);
        [DllImport(DllName, EntryPoint = "blsSignatureSerialize")]
        public static extern unsafe int SignatureSerialize(byte* buf, int maxBufSize, BlsSignature sig);

        //sign the hash
        //use the low (bitSize of r) - 1 bit of h
        //return 0 if success else -1
        //NOTE : return false if h is zero or c1 or -c1 value for BN254. see hashTest() in test/bls_test.hpp
        //BLS_DLL_API int blsSignHash(blsSignature* sig, const blsSecretKey* sec, const void* h, mclSize size);
        [DllImport(DllName, EntryPoint = "blsSignHash")]
        public static extern unsafe int SignHash(out BlsSignature sig, BlsSecretKey sec, byte* h, int size);

        // return 1 if valid
        // BLS_DLL_API int blsVerify(const blsSignature* sig, const blsPublicKey* pub, const void* m, mclSize size);
        [DllImport(DllName, EntryPoint = "blsVerify")]
        public static extern unsafe int Verify(BlsSignature sig, BlsPublicKey pub, byte* m, int size);

        // return 1 if valid
        //BLS_DLL_API int blsVerifyHash(const blsSignature* sig, const blsPublicKey* pub, const void* h, mclSize size);
        [DllImport(DllName, EntryPoint = "blsVerifyHash")]
        public static extern unsafe int VerifyHash(BlsSignature sig, BlsPublicKey pub, byte* h, int size);

        //verify X == sY by checking e(X, sQ) = e(Y, Q)
        //@param X [in]
        //@param Y [in]
        //@param pub [in] pub = sQ
        //@return 1 if e(X, pub) = e(Y, Q) else 0
        //BLS_DLL_API int blsVerifyPairing(const blsSignature* X, const blsSignature* Y, const blsPublicKey* pub);
        // Note: bls_verify in Eth 2.0 has "Verify that e(pubkey, hash_to_G2(message_hash, domain)) == e(g, signature)"
        // i.e. if X = G2 of hash, then Y = signature ??
        [DllImport(DllName, EntryPoint = "blsVerifyPairing")]
        public static extern int VerifyPairing(BlsSignature x, BlsSignature y, BlsPublicKey pub);

        // MCLBN_DLL_API mclSize mclBnFp2_serialize(void *buf, mclSize maxBufSize, const mclBnFp2 *x);
        //[DllImport(@"mclbn384_256.dll")]
        //public static extern int mclBnFp2_serialize(byte[] buf, int maxBufSiz, Bls384Interop.MclBnFp2 x);

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
                return v.ToString();
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
                return v.ToString();
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
                return v.ToString();
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
    }
}
