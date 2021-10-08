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

using System.Runtime.InteropServices;

//[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory|DllImportSearchPath.AssemblyDirectory)]

namespace Nethermind.Cryptography
{
    internal static class Bls384Interop
    //public static class Bls384Interop
    {
        //#define BLS_ETH_MODE_OLD 0
        //#define BLS_ETH_MODE_LATEST 1
        
        public const int BLS_ETH_MODE_OLD = 0;
        public const int BLS_ETH_MODE_DRAFT_05 = 1; // 2020/Jan/30
        public const int BLS_ETH_MODE_DRAFT_06 = 2; // 2020/Mar/15
        public const int BLS_ETH_MODE_LATEST = 1;
        
        // 	MCL_BLS12_381 = 5,
        public const int MCL_BLS12_381 = 5;

        //#define MCLBN_COMPILED_TIME_VAR ((MCLBN_FR_UNIT_SIZE) * 10 + (MCLBN_FP_UNIT_SIZE))
        // The +200 is for BLS_ETH
        public const int MCLBN_COMPILED_TIME_VAR = MCLBN_FR_UNIT_SIZE * 10 + MCLBN_FP_UNIT_SIZE + 200;

        // This will search and load bls384_256.dll on Windows, and libbls384_256.dll on Linux
        private const string DllName = "bls384_256";

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

        
        /*
            all msg[i] has the same msgSize byte, so msgVec must have (msgSize * n) byte area
            verify prod e(H(pubVec[i], msgToG2[i]) == e(P, sig)
            @note CHECK that sig has the valid order, all msg are different each other before calling this
        */
        //BLS_DLL_API int blsAggregateVerifyNoCheck(const blsSignature *sig, const blsPublicKey *pubVec, const void *msgVec, mclSize msgSize, mclSize n);
        [DllImport(DllName, EntryPoint = "blsAggregateVerifyNoCheck")]
        public static extern unsafe int AggregateVerifyNoCheck(ref BlsSignature sig, BlsPublicKey[] pubVec, byte* msgVec, int msgSize, int n);
        
        // verify(sig, sum of pubVec[0..n], msg)
        //BLS_DLL_API int blsFastAggregateVerify(const blsSignature *sig, const blsPublicKey *pubVec, mclSize n, const void *msg, mclSize msgSize);
        [DllImport(DllName, EntryPoint = "blsFastAggregateVerify")]
        public static extern unsafe int FastAggregateVerify(ref BlsSignature sig, BlsPublicKey[] pubVec, int n, byte* msg, int msgSize);

        // BLS_DLL_API void blsGetPublicKey(blsPublicKey* pub, const blsSecretKey* sec);
        [DllImport(DllName, EntryPoint = "blsGetPublicKey")]
        public static extern void GetPublicKey([In, Out] ref BlsPublicKey pub, ref BlsSecretKey sec);

        // utility function: convert hashWithDomain to a serialized Fp2
        // BLS_DLL_API void blsHashWithDomainToFp2(uint8_t buf[96], const uint8_t hashWithDomain[40]);
        [DllImport(DllName, EntryPoint = "blsHashWithDomainToFp2")]
        public static extern unsafe void HashWithDomainToFp2(byte* buf, byte* hashWithDomain);

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

        //BLS_DLL_API void blsPublicKeyAdd(blsPublicKey *pub, const blsPublicKey *rhs);
        [DllImport(DllName, EntryPoint = "blsPublicKeyAdd")]
        public static extern void PublicKeyAdd([In, Out] ref BlsPublicKey pub, ref BlsPublicKey rhs);

        //BLS_DLL_API mclSize blsPublicKeyDeserialize(blsPublicKey* pub, const void* buf, mclSize bufSize);
        [DllImport(DllName, EntryPoint = "blsPublicKeyDeserialize")]
        public static extern unsafe int PublicKeyDeserialize([In, Out] ref BlsPublicKey pub, byte* buf, int bufSize);

        //BLS_DLL_API mclSize blsPublicKeySerialize(void *buf, mclSize maxBufSize, const blsPublicKey *pub);
        [DllImport(DllName, EntryPoint = "blsPublicKeySerialize")]
        public static extern unsafe int PublicKeySerialize(byte* buf, int maxBufSize, ref BlsPublicKey pub);

        // return read byte size if success else 0
        //BLS_DLL_API mclSize blsIdDeserialize(blsId* id, const void* buf, mclSize bufSize);
        //BLS_DLL_API mclSize blsSecretKeyDeserialize(blsSecretKey* sec, const void* buf, mclSize bufSize);
        [DllImport(DllName, EntryPoint = "blsSecretKeyDeserialize")]
        public static extern unsafe int SecretKeyDeserialize([In, Out] ref BlsSecretKey sec, byte* buf, int bufSize);

        // return written byte size if success else 0
        //BLS_DLL_API mclSize blsIdSerialize(void *buf, mclSize maxBufSize, const blsId *id);
        //BLS_DLL_API mclSize blsSecretKeySerialize(void *buf, mclSize maxBufSize, const blsSecretKey *sec);
        [DllImport(DllName, EntryPoint = "blsSecretKeySerialize")]
        public static extern unsafe int SecretKeySerialize(byte* buf, int maxBufSize, ref BlsSecretKey sec);

	    // use new eth 2.0 spec
	    // @return 0 if success
	    // @remark
	    // this functions and the spec may change until it is fixed
	    // the size of message <= 32
        //#define BLS_ETH_MODE_OLD 0
        //#define BLS_ETH_MODE_LATEST 1
        //        BLS_DLL_API int blsSetETHmode(int mode);
        [DllImport(DllName, EntryPoint = "blsSetETHmode")]
        public static extern void SetEthMode(int mode);

        //set ETH serialization mode for BLS12-381
        //@param ETHserialization [in] 1:enable,  0:disable
        //@note ignore the flag if curve is not BLS12-381
        //BLS_DLL_API void blsSetETHserialization(int ETHserialization);
        [DllImport(DllName, EntryPoint = "blsSetETHserialization")]
        public static extern void SetEthSerialization(int ETHserialization);

        // set secretKey if system has /dev/urandom or CryptGenRandom
        // return 0 if success else -1
        // BLS_DLL_API int blsSecretKeySetByCSPRNG(blsSecretKey* sec);
        //[DllImport(DllName, EntryPoint = "blsSecretKeySetByCSPRNG")]
        //public static extern int SecretKeySetByCSPRNG(out BlsSecretKey sec);
        
        // calculate the has of m and sign the hash
        // BLS_DLL_API void blsSign(blsSignature* sig, const blsSecretKey* sec, const void* m, mclSize size);
        [DllImport(DllName, EntryPoint = "blsSign")]
        public static extern unsafe void Sign([In, Out] ref BlsSignature sig, ref BlsSecretKey sec, byte* m, int size);

        //BLS_DLL_API void blsSignatureAdd(blsSignature* sig, const blsSignature* rhs);
        [DllImport(DllName, EntryPoint = "blsSignatureAdd")]
        public static extern void SignatureAdd([In, Out] ref BlsSignature sig, ref BlsSignature rhs);

        //BLS_DLL_API mclSize blsSignatureDeserialize(blsSignature* sig, const void* buf, mclSize bufSize);
        [DllImport(DllName, EntryPoint = "blsSignatureDeserialize")]
        public static extern unsafe int SignatureDeserialize([In, Out] ref BlsSignature sig, byte* buf, int bufSize);

        //BLS_DLL_API mclSize blsSignatureSerialize(void *buf, mclSize maxBufSize, const blsSignature *sig);
        [DllImport(DllName, EntryPoint = "blsSignatureSerialize")]
        public static extern unsafe int SignatureSerialize(byte* buf, int maxBufSize, ref BlsSignature sig);

        //sign the hash
        //use the low (bitSize of r) - 1 bit of h
        //return 0 if success else -1
        //NOTE : return false if h is zero or c1 or -c1 value for BN254. see hashTest() in test/bls_test.hpp
        //BLS_DLL_API int blsSignHash(blsSignature* sig, const blsSecretKey* sec, const void* h, mclSize size);
        [DllImport(DllName, EntryPoint = "blsSignHash")]
        public static extern unsafe int SignHash([In, Out] ref BlsSignature sig, ref BlsSecretKey sec, byte* h, int size);

        //sign hashWithDomain by sec
        //hashWithDomain[0:32] 32 bytes message
        //hashWithDomain[32:40] 8 bytes data
        //see https://github.com/ethereum/eth2.0-specs/blob/dev/specs/bls_signature.md#hash_to_g2
        //HashWithDomain apis support only for BLS_ETH=1 and BLS12_381
        //return 0 if success else -1
        // BLS_DLL_API int blsSignHashWithDomain(blsSignature *sig, const blsSecretKey *sec, const unsigned char hashWithDomain[40]);
        [DllImport(DllName, EntryPoint = "blsSignHashWithDomain")]
        public static extern unsafe int SignHashWithDomain([In, Out] ref BlsSignature sig, ref BlsSecretKey sec, byte* hashWithDomain);

        // return 1 if valid
        // BLS_DLL_API int blsVerify(const blsSignature* sig, const blsPublicKey* pub, const void* m, mclSize size);
        [DllImport(DllName, EntryPoint = "blsVerify")]
        public static extern unsafe int Verify(ref BlsSignature sig, ref BlsPublicKey pub, byte* m, int size);
        
        //verify aggSig with pubVec[0, n) and hVec[0, n)
        //e(aggSig, Q) = prod_i e(hVec[i], pubVec[i])
        //return 1 if valid
        //@note do not check duplication of hVec
        //BLS_DLL_API int blsVerifyAggregatedHashes(const blsSignature* aggSig, const blsPublicKey* pubVec, const void* hVec, size_t sizeofHash, mclSize n);
        [DllImport(DllName, EntryPoint = "blsVerifyAggregatedHashes")]
        public static extern unsafe int VerifyAggregateHashes(ref BlsSignature aggSig, BlsPublicKey[] pubVec, byte* hVec, int sizeofHash, int n);
        
        //pubVec is an array of size n
        //hashWithDomain is an array of size (40 * n)
        //BLS_DLL_API int blsVerifyAggregatedHashWithDomain(const blsSignature *aggSig, const blsPublicKey *pubVec, const unsigned char hashWithDomain[][40], mclSize n);
        [DllImport(DllName, EntryPoint = "blsVerifyAggregatedHashWithDomain")]
        public static extern unsafe int VerifyAggregatedHashWithDomain(ref BlsSignature aggSig, BlsPublicKey[] pubVec, byte* hashWithDomain, int n);

        // return 1 if valid
        //BLS_DLL_API int blsVerifyHash(const blsSignature* sig, const blsPublicKey* pub, const void* h, mclSize size);
        [DllImport(DllName, EntryPoint = "blsVerifyHash")]
        public static extern unsafe int VerifyHash(ref BlsSignature sig, ref BlsPublicKey pub, byte* h, int size);

        // return 1 if valid
        // BLS_DLL_API int blsVerifyHashWithDomain(const blsSignature *sig, const blsPublicKey *pub, const unsigned char hashWithDomain[40]);
        [DllImport(DllName, EntryPoint = "blsVerifyHashWithDomain")]
        public static extern unsafe int VerifyHashWithDomain(ref BlsSignature sig, ref BlsPublicKey pub, byte* hashWithDomain);

        //verify X == sY by checking e(X, sQ) = e(Y, Q)
        //@param X [in]
        //@param Y [in]
        //@param pub [in] pub = sQ
        //@return 1 if e(X, pub) = e(Y, Q) else 0
        //BLS_DLL_API int blsVerifyPairing(const blsSignature* X, const blsSignature* Y, const blsPublicKey* pub);
        // Note: bls_verify in Eth 2.0 has "Verify that e(pubkey, hash_to_G2(message_hash, domain)) == e(g, signature)"
        // i.e. if X = G2 of hash, then Y = signature ??
        [DllImport(DllName, EntryPoint = "blsVerifyPairing")]
        public static extern int VerifyPairing(ref BlsSignature x, ref BlsSignature y, ref BlsPublicKey pub);

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
