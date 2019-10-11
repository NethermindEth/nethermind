using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Test.Bls
{
    // Using https://github.com/herumi/bls
    // * Install Visual Studio C++ tools
    // * Open 64-bit command prompt
    // * "makelib.bat dll" worked (as does test, mentioned in readme)
    // * copy the output dll from bin folder

    class Program
    {
        // 	MCL_BLS12_381 = 5,
        const int MCL_BLS12_381 = 5;

        /**
	        @file
	        @brief C API of 384-bit optimal ate pairing over BN curves
	        @author MITSUNARI Shigeo(@herumi)
	        @license modified new BSD license
	        http://opensource.org/licenses/BSD-3-Clause
        */
        //#define MCLBN_FP_UNIT_SIZE 6
        //#define MCLBN_FR_UNIT_SIZE 6
        const int MCLBN_FP_UNIT_SIZE = 6;
        const int MCLBN_FR_UNIT_SIZE = 6;

        //#define MCLBN_COMPILED_TIME_VAR ((MCLBN_FR_UNIT_SIZE) * 10 + (MCLBN_FP_UNIT_SIZE))
        const int MCLBN_COMPILED_TIME_VAR = MCLBN_FR_UNIT_SIZE * 10 + MCLBN_FP_UNIT_SIZE;

        //typedef struct {
        //uint64_t d[MCLBN_FP_UNIT_SIZE];
        //}
        //mclBnFp;
        public struct mclBnFp
        {
            public ulong d_0;
            public ulong d_1;
            public ulong d_2;
            public ulong d_3;
            public ulong d_4;
            public ulong d_5;
        }

        /*
	        x = d[0] + d[1] i where i^2 = -1
        */
        //typedef struct {
        //    mclBnFp d[2];
        //}
        //mclBnFp2;
        public struct mclBnFp2
        {
            public mclBnFp d_0;
            public mclBnFp d_1;
        }

        /*
            G1 and G2 are isomorphism to Fr
        */
        //typedef struct {
        //    uint64_t d[MCLBN_FR_UNIT_SIZE];
        //    }
        //    mclBnFr;
        public struct mclBnFr
        {
            public ulong d_0;
            public ulong d_1;
            public ulong d_2;
            public ulong d_3;
            public ulong d_4;
            public ulong d_5;
        }

        /*
	        G1 is defined over Fp
        */
        //typedef struct {
        //    mclBnFp x, y, z;
        //    }
        //    mclBnG1;
        public struct mclBnG1
        {
            public mclBnFp x;
            public mclBnFp y;
            public mclBnFp z;
        }

        //typedef struct {

        //    mclBnFp2 x, y, z;
        //}
        //mclBnG2;
        public struct mclBnG2
        {
            public mclBnFp2 x;
            public mclBnFp2 y;
            public mclBnFp2 z;
        }

        //typedef struct {
        //    mclBnFr v;
        //    }
        //    blsSecretKey;
        public struct blsSecretKey
        {
            public mclBnFr v;

            public override string ToString()
            {
                return BitConverter.ToString(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref this, 1)).ToArray());
            }
        }

        //typedef struct {
        //#ifdef BLS_SWAP_G
        //	mclBnG1 v;
        //#else
        //    mclBnG2 v;
        //#endif
        //}
        //blsPublicKey;
        public struct blsPublicKey
        {
            public mclBnG2 v;

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
        public struct blsSignature
        {
            public mclBnG1 v;

            public override string ToString()
            {
                return BitConverter.ToString(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref this, 1)).ToArray());
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
        [DllImport(@"bls384.dll")]
        public static extern int blsInit(int curve, int compiledTimeVar);

        /*
	        set secretKey if system has /dev/urandom or CryptGenRandom
	        return 0 if success else -1
        */
        // BLS_DLL_API int blsSecretKeySetByCSPRNG(blsSecretKey* sec);
        [DllImport(@"bls384.dll")]
        public static extern int blsSecretKeySetByCSPRNG(out blsSecretKey sec);

        // BLS_DLL_API void blsGetPublicKey(blsPublicKey* pub, const blsSecretKey* sec);
        [DllImport(@"bls384.dll")]
        public static extern int blsGetPublicKey(out blsPublicKey pub, blsSecretKey sec);

        // calculate the has of m and sign the hash
        // BLS_DLL_API void blsSign(blsSignature* sig, const blsSecretKey* sec, const void* m, mclSize size);
        [DllImport(@"bls384.dll")]
        public static extern int blsSign(out blsSignature sig, blsSecretKey sec, byte[] m, int size);

        // return 1 if valid
        // BLS_DLL_API int blsVerify(const blsSignature* sig, const blsPublicKey* pub, const void* m, mclSize size);
        [DllImport(@"bls384.dll")]
        public static extern int blsVerify(blsSignature sig, blsPublicKey pub, byte[] m, int size);


        static void Main(string[] args)
        {
            Console.WriteLine("Test BLS");

            var curveType = MCL_BLS12_381;
            var r = "52435875175126190479447740508185965837690552500527637822603658699938581184513";
            var p = "4002409555221667393417789825735904156556882819939007885332058136124031650490837864442687629129015664037894272559787";

            int ret = blsInit(curveType, MCLBN_COMPILED_TIME_VAR);
            Console.WriteLine("Init Result {0}", ret);
            bls_use_stackTest();
        }

        static void bls_use_stackTest()
        {
            blsSecretKey sec;
            blsPublicKey pub;
            blsSignature sig;
            var msg = "this is a pen";
            var msgBytes = Encoding.UTF8.GetBytes(msg);
            var msgSize = msgBytes.Length;

            blsSecretKeySetByCSPRNG(out sec);
            Console.WriteLine("Secret key: {0}", sec);

            blsGetPublicKey(out pub, sec);
            Console.WriteLine("Public key: {0}", pub);

            blsSign(out sig, sec, msgBytes, msgSize);
            Console.WriteLine("Signature : {0}", sig);

            int ret = blsVerify(sig, pub, msgBytes, msgSize);
            Console.WriteLine("Verify Result {0}", ret);

            msgBytes[0]++;
            int ret2 = blsVerify(sig, pub, msgBytes, msgSize);
            Console.WriteLine("Verify Result after tamper {0}", ret2);
        }
    }
}
